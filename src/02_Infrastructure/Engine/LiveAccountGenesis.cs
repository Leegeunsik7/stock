// ============================================================================
// LiveAccountGenesis — v5.0.0 Phase1: 실전 계좌 최초 결속 & 1raw 정밀 원장 동기화
//
//  섀도우 핀을 꽂은 채(IsShadowMode==true) 실 증권사 서버로부터 취득한 실 예수금/미수한도를 LiveAccountingMesh 에
//  1raw 단위까지 주입해 장부의 영점을 잡는다. ★초기 세션 수립 중 허위 주문 유출을 원천 차단하기 위해 진입·시딩 직전
//  IsShadowMode==true 를 하드 검문(위반 시 fail-closed 거부).
//
//  ── liveArmed:false / 자격증명 경계 ─────────────────────────────────────────
//  ★실 브로커 접속·자격증명 인가(SecurityAssetVault.Reveal)·실 예수금 쿼리는 '운영자'가 수행한다. 본 클래스는
//  운영자가 주입한 쿼리 델리게이트(AccountSnapshotQuery)로부터 '값'만 받아 장부를 영점 조절할 뿐, 자격증명·소켓을
//  직접 만지지 않는다(관심사 분리). 쿼리 실패는 fail-closed(거부). 실 실행 경로는 이후 이중키+서명(사람)에만 열린다.
//
//  결정론·δ=0 아님(설정 단계·핫패스 아님). 1회성(genesis 래치는 LiveAccountingMesh 내부).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public enum GenesisState : byte { Pending = 0, Synced = 1, Refused = 2 }

    /// <summary>실 증권사 계좌 스냅샷(운영자 쿼리 결과·raw 10^8 스케일). 미수한도는 신용/증거금 한도.</summary>
    public readonly struct AccountSnapshot
    {
        public readonly long DepositKrwRaw;
        public readonly long DepositUsdRaw;
        public readonly long MarginLimitKrwRaw;
        public AccountSnapshot(long depositKrwRaw, long depositUsdRaw, long marginLimitKrwRaw = 0)
        { DepositKrwRaw = depositKrwRaw; DepositUsdRaw = depositUsdRaw; MarginLimitKrwRaw = marginLimitKrwRaw; }
    }

    /// <summary>운영자가 실 브로커 세션에 결속하는 예수금 쿼리 델리게이트. 성공 시 true+snapshot, 실패 시 false(fail-closed).</summary>
    public delegate bool AccountSnapshotQuery(out AccountSnapshot snapshot);

    /// <summary>Genesis 결과(불변 진단).</summary>
    public readonly struct GenesisResult
    {
        public readonly bool Success;
        public readonly GenesisState State;
        public readonly string Reason;
        public GenesisResult(bool success, GenesisState state, string reason) { Success = success; State = state; Reason = reason; }
    }

    /// <summary>실전 계좌 최초 결속 오케스트레이터 — IsShadowMode 하드 검문 하에서만 실 예수금을 장부에 1raw 시딩.</summary>
    public sealed class LiveAccountGenesis
    {
        private readonly LiveAccountingMesh      _mesh;
        private readonly ShadowExecutionGateway  _gateway;   // IsShadowMode 하드 게이트
        private readonly OrderAttestationService? _att;
        private readonly MmfFlightRecorder?      _blackbox;

        private int _state = (int)GenesisState.Pending;   // Volatile.Read/Write(ref) 접근(CS0420 회피)
        private long _syncedKrwRaw, _syncedUsdRaw, _marginLimitRaw;

        public LiveAccountGenesis(LiveAccountingMesh mesh, ShadowExecutionGateway gateway,
                                  OrderAttestationService? attestation = null, MmfFlightRecorder? blackbox = null)
        {
            _mesh     = mesh    ?? throw new ArgumentNullException(nameof(mesh));
            _gateway  = gateway ?? throw new ArgumentNullException(nameof(gateway));
            _att      = attestation;
            _blackbox = blackbox;
        }

        public GenesisState State           => (GenesisState)Volatile.Read(ref _state);
        public long SyncedDepositKrwRaw      => Interlocked.Read(ref _syncedKrwRaw);
        public long SyncedDepositUsdRaw      => Interlocked.Read(ref _syncedUsdRaw);
        public long MarginLimitKrwRaw        => Interlocked.Read(ref _marginLimitRaw);

        /// <summary>
        /// Genesis 실행 — ①IsShadowMode==true 하드 검문 → ②운영자 쿼리(예수금/미수한도) → ③시딩 직전 섀도우 재검(TOCTOU) →
        /// ④LiveAccountingMesh 1raw 재시딩. 어느 단계든 실패면 fail-closed 거부(Refused). 성공 시 Synced.
        /// </summary>
        public GenesisResult Establish(AccountSnapshotQuery query)
        {
            if (query is null) return Refuse("예수금 쿼리 델리게이트 없음");

            // ① 하드 검문: 반드시 섀도우(자본 안전). 실전 모드면 Genesis 금지 — 초기 수립 중 허위 주문 유출 원천 차단.
            if (!_gateway.IsShadowMode) return Refuse("실전 모드(IsShadowMode==false) — Genesis 금지(fail-closed)");
            if (_att is { } a && a.Mode != GatingMode.Normal) return Refuse("비상 동결 중 — Genesis 불가");

            // ② 운영자 쿼리(자격증명·소켓은 운영자가 인가·연결). 실패=fail-closed.
            AccountSnapshot snap;
            try { if (!query(out snap)) return Refuse("실 예수금 쿼리 실패(운영자 세션)"); }
            catch (Exception ex) { return Refuse($"예수금 쿼리 예외: {ex.GetType().Name}"); }

            if (snap.DepositKrwRaw < 0 || snap.DepositUsdRaw < 0 || snap.MarginLimitKrwRaw < 0)
                return Refuse("음수 예수금/미수한도 — 오염 데이터 거부");

            // ③ 시딩 직전 섀도우 재검(쿼리 도중 상태 변이 방어).
            if (!_gateway.IsShadowMode) return Refuse("시딩 직전 실전 전환 감지 — Genesis 중단(fail-closed)");

            // ④ 1raw 재시딩(1회성 genesis 래치·체결 전·미동결에서만). 거부되면 이미 시딩됐거나 체결 시작됨.
            if (!_mesh.GenesisSeedDeposit(snap.DepositKrwRaw, snap.DepositUsdRaw))
                return Refuse("장부 재시딩 거부 — 이미 Genesis 완료/체결 시작/동결");

            Interlocked.Exchange(ref _syncedKrwRaw, snap.DepositKrwRaw);
            Interlocked.Exchange(ref _syncedUsdRaw, snap.DepositUsdRaw);
            Interlocked.Exchange(ref _marginLimitRaw, snap.MarginLimitKrwRaw);
            Volatile.Write(ref _state, (int)GenesisState.Synced);
            try { _blackbox?.Record(FlightEventKind.GenesisSynced, symbolCode: 0L, arg0: snap.DepositKrwRaw, arg1: snap.DepositUsdRaw, arg2: snap.MarginLimitKrwRaw, sourceCode: 50); } catch { }
            return new GenesisResult(true, GenesisState.Synced,
                $"Genesis 완료 — 예수금 KRW={snap.DepositKrwRaw}raw USD={snap.DepositUsdRaw}raw 미수한도={snap.MarginLimitKrwRaw}raw 1raw 시딩(섀도우 유지)");
        }

        private GenesisResult Refuse(string reason)
        {
            Volatile.Write(ref _state, (int)GenesisState.Refused);
            return new GenesisResult(false, GenesisState.Refused, reason);
        }
    }
}
