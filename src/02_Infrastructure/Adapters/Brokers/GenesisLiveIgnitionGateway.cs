// ============================================================================
// GenesisLiveIgnitionGateway — v10.0.0 실전 점화 게이트웨이 (실자본 사출 최종 빗장)
//
//  하드웨어 리스크 밸브 키(ValveWired)와 마스터 물리 무장 키(LiveArmingWired)의 이중 논리곱을 엄격 조건으로,
//  Pre-Flight/Arming-Lease/Dead-Man/Fat-Finger/Chaos 등 모든 방어선(blockWhile 프레임워크·tighten-only)과 하드웨어
//  키 펄스 생존까지 전부 PASS 일 때에만, 오더 디스패처의 실물 사출 경로(onSend)를 1회성(Single-Shot) 원자 스위치로
//  개통한다. 개통 후에도 매 사출마다 이중키∧방어선∧펄스를 재검(fail-closed)하고, 실제 사출은 SealedEmissionInterlock
//  (★Think Beyond — exactly-once ∧ atomic-payload 봉인)을 반드시 경유한다.
//
//  ★불변: δ=0(IsIgnitable/Emit 핫패스 힙 0·Interlocked·값타입) · 결정론(주입 술어·순수) · fail-closed(어느 방어선/
//    키 하나라도 실패 → veto·미사출·포렌식 코드) · liveArmed:false(★이중 키는 물리 하드웨어[운영자]·소프트웨어는
//    산출/봉인만 — valve/arming 미결속[기본]이면 강한 사출 시도도 실물 0비트. onSend 는 하류 주입·미배선 시 0).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>실전 점화 게이트웨이(δ=0·이중키 최종 빗장). 이중키∧방어선∧펄스 PASS 시 1회성 개통 + 봉인 사출.</summary>
    public sealed class GenesisLiveIgnitionGateway
    {
        // Veto 코드 비트(포렌식).
        public const long V_VALVE     = 1L << 0;   // 하드웨어 밸브 키 미결속
        public const long V_ARMING    = 1L << 1;   // 마스터 물리 무장 키 미결속
        public const long V_DEFENSE   = 1L << 2;   // 방어선(blockWhile) veto
        public const long V_KEY_PULSE = 1L << 3;   // 하드웨어 키 펄스 단선

        private readonly Func<bool> _valveWired;       // Key A — 하드웨어 리스크 밸브
        private readonly Func<bool> _liveArmingWired;  // Key B — 마스터 물리 무장 키
        private readonly Func<bool> _defensesPass;     // 전 방어선 PASS(= armedPredicate: !blockWhile ∧ gate.IsArmed)
        private readonly Func<bool>? _keyPulseAlive;   // 하드웨어 펄스 생존(옵션·미배선 시 무시)
        private readonly SealedEmissionInterlock? _sealed;    // ★Think Beyond — 봉인 사출
        private readonly IgnitionAuditTrailRecorder? _audit;  // 포렌식 감사
        private readonly Action? _onFirstIgnition;
        private long _emissionArmed;   // 0=닫힘 / 1=1회성 개통(단조)
        private long _ignitions, _vetoes, _emissions, _lastVetoCode;

        public GenesisLiveIgnitionGateway(Func<bool> valveWired, Func<bool> liveArmingWired, Func<bool> defensesPass,
            Func<bool>? keyPulseAlive = null, SealedEmissionInterlock? sealedEmission = null,
            IgnitionAuditTrailRecorder? audit = null, Action? onFirstIgnition = null)
        {
            _valveWired      = valveWired      ?? throw new ArgumentNullException(nameof(valveWired));
            _liveArmingWired = liveArmingWired ?? throw new ArgumentNullException(nameof(liveArmingWired));
            _defensesPass    = defensesPass    ?? throw new ArgumentNullException(nameof(defensesPass));
            _keyPulseAlive   = keyPulseAlive;
            _sealed          = sealedEmission;
            _audit           = audit;
            _onFirstIgnition = onFirstIgnition;
        }

        // ── 읽기(δ=0·락프리) ──
        /// <summary>이중 물리 키 논리곱(ValveWired ∧ LiveArmingWired).</summary>
        public bool DualKeyWired  { get { try { return _valveWired() && _liveArmingWired(); } catch { return false; } } }
        public bool KeyPulseAlive { get { try { return _keyPulseAlive?.Invoke() ?? true; } catch { return false; } } }
        /// <summary>점화 가능: 이중키 ∧ 전 방어선 ∧ 펄스 생존(δ=0·fail-closed).</summary>
        public bool IsIgnitable
        {
            get { try { return _valveWired() && _liveArmingWired() && _defensesPass() && KeyPulseAlive; } catch { return false; } }
        }
        public bool EmissionOpen => Interlocked.Read(ref _emissionArmed) == 1L;
        public long Ignitions    => Interlocked.Read(ref _ignitions);
        public long Vetoes       => Interlocked.Read(ref _vetoes);
        public long Emissions    => Interlocked.Read(ref _emissions);
        public long LastVetoCode => Interlocked.Read(ref _lastVetoCode);
        /// <summary>최종 상태 코드(UI): 0 dormant / 1 armed-open / 2 vetoed.</summary>
        public long StateCode
        {
            get
            {
                if (EmissionOpen && IsIgnitable) return 1L;
                if (Interlocked.Read(ref _lastVetoCode) != 0L) return 2L;
                return 0L;
            }
        }

        // veto 판정 + 포렌식 코드 산출(δ=0·fail-closed).
        private long ComputeVeto()
        {
            long v = 0L;
            try { if (!_valveWired())      v |= V_VALVE; }   catch { v |= V_VALVE; }
            try { if (!_liveArmingWired()) v |= V_ARMING; }  catch { v |= V_ARMING; }
            try { if (!_defensesPass())    v |= V_DEFENSE; } catch { v |= V_DEFENSE; }
            if (!KeyPulseAlive)            v |= V_KEY_PULSE;
            return v;
        }

        /// <summary>★1회성 원자 개통(δ=0). 최초로 이중키∧방어선∧펄스 PASS 시 emission 경로 단발 개통 + 감사 1회. veto 시 false.</summary>
        public bool TryArmEmission()
        {
            long v = ComputeVeto();
            if (v != 0L) { RecordVeto(v); return false; }
            ArmOnce();
            Volatile.Write(ref _lastVetoCode, 0L);
            return true;
        }

        /// <summary>
        /// ★실물 사출 최종 빗장(δ=0). 매 사출마다 이중키∧방어선∧펄스 재검(fail-closed) → 미개통이면 자동 단발 개통 →
        /// SealedEmissionInterlock(exactly-once ∧ atomic-payload 봉인) 경유로 onSend. veto/봉인차단 시 미사출·false.
        /// ★valve/arming 미결속이면 여기서 veto — onSend 절대 미호출(단 1비트 실물 누수 봉쇄).
        /// </summary>
        public bool Emit(long orderId, long symbolCode, byte side, byte kind, long priceRaw, long qtyRaw, SealedSend onSend)
        {
            long v = ComputeVeto();
            if (v != 0L) { RecordVeto(v); return false; }   // ★이중키/방어선/펄스 실패 → 미사출
            if (Volatile.Read(ref _emissionArmed) == 0L) ArmOnce();   // 미개통 → 자동 단발 개통(PASS 확정 상태)
            bool sent;
            if (_sealed is { } se) sent = se.TrySealAndEmit(orderId, symbolCode, side, kind, priceRaw, qtyRaw, onSend);
            else
            {
                long seal = SealedEmissionInterlock.ComputeSeal(orderId, symbolCode, side, kind, priceRaw, qtyRaw);
                var token = new SealedOrderToken(orderId, symbolCode, side, kind, priceRaw, qtyRaw, seal);
                onSend(in token); sent = true;
            }
            if (sent)
            {
                Interlocked.Increment(ref _emissions);
                _audit?.Record(IgnitionEventKind.Emission, orderId, symbolCode);
            }
            return sent;
        }

        private void ArmOnce()
        {
            if (Interlocked.CompareExchange(ref _emissionArmed, 1L, 0L) == 0L)   // 1회성 단발 개통
            {
                Interlocked.Increment(ref _ignitions);
                _audit?.Record(IgnitionEventKind.IgnitionOpened, 1L);
                try { _onFirstIgnition?.Invoke(); } catch { }
            }
        }

        private void RecordVeto(long code)
        {
            Interlocked.Increment(ref _vetoes);
            Volatile.Write(ref _lastVetoCode, code);
            _audit?.Record(IgnitionEventKind.Veto, code);
        }
    }
}
