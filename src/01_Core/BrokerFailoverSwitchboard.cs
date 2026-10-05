// ============================================================================
// LockFreeBrokerSwitchboard + NativeInteropFaultGuard + BrokerFailoverSentinel — v12.0.4 Phase 95-A Think Beyond Gemini
//
//   위험 가정(제미나이의 '단순 페일오버 + 어댑터 결속'이 다루지 않는 실물 브로커 연동 파멸 리스크):
//     ① "키움 x86 COM 모듈 내부 Access Violation(0xC0000005)에 의한 호스트 프로세스 크래시" — ActiveX/COM 내부
//        메모리 손상은 ★.NET Core/.NET 8 에서 in-process 로 catch 불가(Corrupted State Exception 처리가 제거됨).
//        try/catch(AccessViolationException) 는 .NET 8 에서 프로세스를 살리지 못한다 → 유일한 실효 방어는
//        ★out-of-process 격리(StockManager.KiwoomProxy x86 별도 프로세스). 크래시는 자식만 죽이고 호스트는 생존.
//     ② "브로커 라우팅 스위칭 시 스레드 락킹 병목" — 장애 순간 lock 기반 라우팅 전환은 경합/지연으로 <10ms SLA 붕괴.
//
//   ── Think Beyond (이중 방어 엔진) ─────────────────────────────────────────────
//     ① Lock-Free Atomic Broker Switchboard(Idea A) — 활성 브로커 인덱스를 단일 long 에 담고 ★CAS 단일 연산으로
//        원자 전환. 스레드 블로킹/GC 객체 생성 0 → O(1) 마이크로초 라우팅 전환(<10ms SLA 를 δ=0 로 초월).
//     ② Native Interop (COM) Fault Guard(Idea B) — ★in-process SEH 로 AV 를 잡으려는 헛된 시도 대신, 아키텍처 진실을
//        수용: (a) COM HRESULT 심각도를 δ=0 순수 분류(연결단절/서버부재/접근거부/접근위반 → Fatal, 미상 → 보수적 Fatal),
//        (b) x86 프록시 프로세스의 하트비트 침묵을 감지(ProxyLivenessProbe) → 프록시 사망(=AV 크래시 대리 신호) 판정.
//        Fatal/사망 → 즉시 fail-closed. 호스트는 격리 덕에 크래시 전이 0.
//
//   ★조립(Part 2) BrokerFailoverSentinel — 위 두 엔진을 결속: 장애(단절·429·50x·슬리피지·COM HRESULT·프록시사망) 감지 시
//     (a) 주문 경로 0ms 원자 Disarm(fail-closed·sticky), (b) 활성 브로커 장애면 보조로 무할당 원자 페일오버(데이터 무중단),
//     (c) PoisonedSecretBuffer 자격증명 0x00 소거(멱등·1회). 재무장은 ★물리 이중 키만(소프트웨어 단독 복구 불가).
//
//   불변: 결정론(주입 tick·순수 분류) · δ=0(ReportFault/Switch/Classify 핫패스 힙 0) · fail-closed(장애·미상 → 안전측) ·
//        락프리(Interlocked/Volatile) · ★liveArmed:false: 데이터 라우팅·안전 감시일 뿐 — Disarm 은 '봉쇄'만, 무장 유발 0.
//   ※ MS2E.Core(최내부·순수) — 상위가 프록시 하트비트/HRESULT/텔레메트리를 ReportFault·Beat 로 결선.
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MS2E.Core
{
    public enum BrokerFaultKind : byte
    {
        None         = 0,
        Disconnect   = 1,   // 피드 단절
        Http429      = 2,   // 레이트리밋 초과(value=누적 429 수)
        Http5xx      = 3,   // 서버 5xx(value=누적 5xx 수)
        SlippageSpike = 4,  // 슬리피지 급증(value=bps)
        ComHResult   = 5,   // COM 호출 실패(value=HRESULT)
        ProxyDead    = 6,   // x86 프록시 프로세스 사망(하트비트 침묵)
    }

    public enum ComFaultSeverity : byte { Ok = 0, Transient = 1, Fatal = 2 }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① (Idea A) — Lock-Free Atomic Broker Switchboard
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>활성 브로커 라우팅을 단일 long CAS 로 원자 전환하는 δ=0 락프리 스위치보드. 스레드 블로킹/GC 0·O(1).</summary>
    public sealed class LockFreeBrokerSwitchboard
    {
        private long _active;
        private long _switches;
        private long _lastSwitchTicks;
        private long _blacklist;   // [라우터 하드닝] 장애 브로커 비트셋(≤64) — 어떤 전환 경로로도 재진입 불가
        private long _refusedSwitches;
        private readonly int _count;

        public const int MaxBrokers = 64;

        public LockFreeBrokerSwitchboard(int brokerCount, int initialActive = 0)
        {
            if (brokerCount < 1 || brokerCount > MaxBrokers) throw new ArgumentOutOfRangeException(nameof(brokerCount), "브로커 수는 1~64.");
            _count = brokerCount;
            _active = (uint)initialActive < (uint)brokerCount ? initialActive : 0;
        }

        public long BlacklistMask => Interlocked.Read(ref _blacklist);
        public long RefusedSwitches => Interlocked.Read(ref _refusedSwitches);
        /// <summary>전 브로커가 블랙리스트(=이중/전면 장애).</summary>
        public bool AllBlacklisted => (BlacklistMask & AllMask) == AllMask;
        private long AllMask => _count == 64 ? -1L : (1L << _count) - 1;
        public bool IsBlacklisted(int broker) => (uint)broker < (uint)_count && (BlacklistMask & (1L << broker)) != 0;

        /// <summary>[라우터 하드닝] 장애 브로커 블랙리스트 등록(원자 Or·멱등). 이전엔 장애 이력이 센티널 내부에만 있어 SwitchTo/TrySwitch
        /// 를 직접 부르는 경로(운영 콘솔·자동 복귀 로직)가 장애 브로커로 라우팅을 되돌릴 수 있었다.</summary>
        [ZeroAllocHotPath]
        public void Blacklist(int broker)
        {
            if ((uint)broker < (uint)_count) Interlocked.Or(ref _blacklist, 1L << broker);
        }

        /// <summary>블랙리스트 해제 — ★물리 이중키 재무장 경로(BrokerFailoverSentinel.TryRearm) 전용.</summary>
        internal void ClearBlacklist() => Interlocked.Exchange(ref _blacklist, 0);

        public int BrokerCount => _count;
        public int Active => (int)Volatile.Read(ref _active);
        public long SwitchCount => Interlocked.Read(ref _switches);
        public long LastSwitchTicks => Volatile.Read(ref _lastSwitchTicks);

        /// <summary>★CAS 원자 전환 — 현재 활성이 expected 일 때만 next 로. 경합 안전(lost-update 불가). δ=0.</summary>
        [ZeroAllocHotPath]
        public bool TrySwitch(int expected, int next, long nowTicks)
        {
            if ((uint)next >= (uint)_count) return false;
            if (next != expected && IsBlacklisted(next)) { Interlocked.Increment(ref _refusedSwitches); return false; }   // 장애 브로커 재진입 차단
            if (Interlocked.CompareExchange(ref _active, next, expected) == expected)
            {
                if (next != expected) { Volatile.Write(ref _lastSwitchTicks, nowTicks); Interlocked.Increment(ref _switches); }
                return true;
            }
            return false;
        }

        /// <summary>강제 전환(현재값 무관·원자). 반환=이전 활성 인덱스. δ=0.</summary>
        [ZeroAllocHotPath]
        public int SwitchTo(int next, long nowTicks)
        {
            if ((uint)next >= (uint)_count) return Active;
            if (IsBlacklisted(next)) { Interlocked.Increment(ref _refusedSwitches); return Active; }   // 장애 브로커 재진입 차단(무변경)
            long prev = Interlocked.Exchange(ref _active, next);
            if (prev != next) { Volatile.Write(ref _lastSwitchTicks, nowTicks); Interlocked.Increment(ref _switches); }
            return (int)prev;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② (Idea B) — Native Interop (COM) Fault Guard
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>COM HRESULT 심각도 분류(δ=0 순수) — in-process SEH 로 AV 를 잡을 수 없는 .NET 8 진실을 수용한 방어.</summary>
    public static class NativeInteropFaultGuard
    {
        public const int RPC_E_CALL_REJECTED         = unchecked((int)0x80010001);
        public const int RPC_E_DISCONNECTED          = unchecked((int)0x80010108);
        public const int RPC_E_SERVERCALL_RETRYLATER = unchecked((int)0x8001010A);
        public const int RPC_S_SERVER_UNAVAILABLE    = unchecked((int)0x800706BA);
        public const int RPC_S_CALL_FAILED           = unchecked((int)0x800706BE);
        public const int E_ACCESSDENIED              = unchecked((int)0x80070005);
        public const int E_FAIL                      = unchecked((int)0x80004005);
        public const int STATUS_ACCESS_VIOLATION     = unchecked((int)0xC0000005);   // AV 대리(프록시 종료코드)

        /// <summary>HRESULT → 심각도. 성공(≥0)=Ok · 재시도가능=Transient · 단절/부재/거부/AV=Fatal · ★미상 실패=보수적 Fatal(fail-closed).</summary>
        [ZeroAllocHotPath]
        public static ComFaultSeverity ClassifyHResult(int hr)
        {
            if (hr >= 0) return ComFaultSeverity.Ok;   // S_OK 및 성공(음수만 실패 HRESULT)
            return hr switch
            {
                RPC_E_SERVERCALL_RETRYLATER or RPC_E_CALL_REJECTED => ComFaultSeverity.Transient,
                RPC_E_DISCONNECTED or RPC_S_SERVER_UNAVAILABLE or RPC_S_CALL_FAILED
                    or E_ACCESSDENIED or STATUS_ACCESS_VIOLATION => ComFaultSeverity.Fatal,
                _ => ComFaultSeverity.Fatal,           // ★미상 실패 → fail-closed(보수)
            };
        }
    }

    /// <summary>x86 프록시 생존성 — 하트비트 침묵 감지(주입 tick·δ=0). 프록시 사망=COM AV 크래시 대리 신호.</summary>
    public sealed class ProxyLivenessProbe
    {
        private long _lastBeatTicks;
        private long _initialized;
        private readonly long _timeoutTicks;

        public ProxyLivenessProbe(long timeoutTicks) => _timeoutTicks = timeoutTicks > 0 ? timeoutTicks : 1;

        public void Beat(long nowTicks)
        {
            Volatile.Write(ref _lastBeatTicks, nowTicks);
            Volatile.Write(ref _initialized, 1);
        }

        /// <summary>마지막 하트비트 이후 임계 초과면 사망. 미시작(하트비트 0건)은 보수적으로 살아있음 판정(오탐 방지).</summary>
        public bool IsDead(long nowTicks)
            => Volatile.Read(ref _initialized) == 1 && (nowTicks - Volatile.Read(ref _lastBeatTicks)) > _timeoutTicks;

        public long LastBeatTicks => Volatile.Read(ref _lastBeatTicks);
    }

    /// <summary>COM RCW 결정적 해제 방어 래퍼(Part 1). null/비-COM 은 no-op(방어). 반환: 남은 refcount 또는 -1(대상 아님/예외).</summary>
    public static class ComReleaseGuard
    {
        public static int SafeFinalRelease(object? comObject)
        {
            if (comObject is null) return 0;
            if (!Marshal.IsComObject(comObject)) return -1;
            try { return Marshal.FinalReleaseComObject(comObject); }
            catch { return -1; }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  조립 (Part 2) — Broker Failover Sentinel (장애 → 0ms Disarm + 원자 페일오버 + 자격증명 소거)
    // ════════════════════════════════════════════════════════════════════════
    public sealed class BrokerFailoverSentinel
    {
        private readonly LockFreeBrokerSwitchboard _board;
        private readonly long _http429Thr, _http5xxThr, _slippageThr;
        private readonly PoisonedSecretBuffer? _credentials;
        private readonly PendingOrderCascadeCanceller? _pending;
        private readonly Action? _onDualBreakdown;
        private long _cascadeCancelled;
        private long _dualBreakdown;   // [방향 B] 전 브로커 장애 도달(sticky)

        private long _disarmed;    // 1=주문 경로 봉쇄(sticky·fail-closed)
        private long _wiped;       // 자격증명 소거 1회 보장(멱등)
        private long _failovers;
        private long _faultedMask; // 장애 판정 브로커 비트셋(≤64) — 페일오버 대상에서 제외
        private int  _lastFault;   // (byte)BrokerFaultKind

        /// <param name="credentials">API Secret 버퍼(옵션). 장애 시 0x00 소거(1회).</param>
        public BrokerFailoverSentinel(
            LockFreeBrokerSwitchboard board,
            long http429Threshold = 5,
            long http5xxThreshold = 3,
            long slippageBpsThreshold = 50,
            PoisonedSecretBuffer? credentials = null,
            PendingOrderCascadeCanceller? pendingOrders = null,
            Action? onDualBreakdown = null)
        {
            _board        = board ?? throw new ArgumentNullException(nameof(board));
            _pending      = pendingOrders;
            _onDualBreakdown = onDualBreakdown;
            _http429Thr   = http429Threshold   > 0 ? http429Threshold   : long.MaxValue;   // ≤0 = 비활성(fail-safe)
            _http5xxThr   = http5xxThreshold   > 0 ? http5xxThreshold   : long.MaxValue;
            _slippageThr  = slippageBpsThreshold > 0 ? slippageBpsThreshold : long.MaxValue;
            _credentials  = credentials;
        }

        public int ActiveBroker => _board.Active;
        public bool IsDisarmed => Volatile.Read(ref _disarmed) == 1;
        /// <summary>주문 사출 가능 여부 — 장애 후 항상 false(fail-closed 추가 계층). ★실 발주는 별도 물리 이중키(CanaryHealthSentinel)로도 봉인.</summary>
        public bool CanEmit => Volatile.Read(ref _disarmed) == 0;
        public long FailoverCount => Interlocked.Read(ref _failovers);
        public BrokerFaultKind LastFault => (BrokerFaultKind)(byte)Volatile.Read(ref _lastFault);
        public bool CredentialsWiped => Volatile.Read(ref _wiped) == 1;
        /// <summary>페일오버 캐스케이드로 취소 전환된 미체결 누계.</summary>
        public long CascadeCancelled => Interlocked.Read(ref _cascadeCancelled);
        /// <summary>[방향 B] Atomic Fallback Disarm — 전 브로커가 장애(블랙리스트)에 도달했는가(sticky, 재무장 전까지 유지).</summary>
        public bool DualBreakdown => Volatile.Read(ref _dualBreakdown) == 1;

        /// <summary>
        /// 장애 보고(δ=0 핫패스·결정론·주입 tick). 장애 판정 시 → ①주문 경로 0ms 원자 Disarm(sticky) ②활성 브로커면 보조로
        /// 무할당 원자 페일오버(데이터 무중단) ③자격증명 0x00 소거(1회). 반환=장애로 판정됐는가.
        /// </summary>
        [ZeroAllocHotPath]
        public bool ReportFault(int broker, BrokerFaultKind kind, long value, long nowTicks)
        {
            bool isFault = kind switch
            {
                BrokerFaultKind.Disconnect or BrokerFaultKind.ProxyDead => true,
                BrokerFaultKind.Http429      => value >= _http429Thr,
                BrokerFaultKind.Http5xx      => value >= _http5xxThr,
                BrokerFaultKind.SlippageSpike => value >= _slippageThr,
                BrokerFaultKind.ComHResult   => NativeInteropFaultGuard.ClassifyHResult(unchecked((int)value)) == ComFaultSeverity.Fatal,
                _ => false,
            };
            if (!isFault) return false;

            Interlocked.Exchange(ref _disarmed, 1);            // ★fail-closed 주문 봉쇄(0ms 원자·sticky)
            Volatile.Write(ref _lastFault, (byte)kind);
            if (_credentials is not null && Interlocked.Exchange(ref _wiped, 1) == 0)
                _credentials.PurgeAndPoison();                 // 자격증명 0x00 소거(멱등·1회)

            // [Phase 1 감사 교정] 이전: `if (broker == Active) SwitchTo((broker+1)%N)` —
            //   ① 점검-후-전환 경합: 점검과 전환 사이 다른 경로가 건강한 브로커로 바꿔두면 Exchange 가 그걸 덮어씀(lost update),
            //   ② 동시 장애 보고 2건이 각자 전환·계수(FailoverCount 이중 계상),
            //   ③ 다음 후보가 이미 장애 판정된 브로커여도 그대로 전환(장애 브로커로 되돌아감).
            // 교정: 장애 브로커를 비트셋에 기록하고, "여전히 그 브로커가 활성일 때만" CAS(TrySwitch)로 다음 건강 브로커로 전환.
            if ((uint)broker < 64u) Interlocked.Or(ref _faultedMask, 1L << broker);
            _board.Blacklist(broker);   // [라우터 하드닝] 스위치보드 자체가 재진입을 거부(센티널 우회 경로 포함)
            // [라우터 하드닝] 장애 브로커의 미체결 전량을 세대 태그 슬롯에서 원자 취소 전환 — 이후 그 브로커에서 늦게 도착하는 체결
            //   통보는 FilledAfterCancel 로, 해제·재사용된 슬롯의 옛 통보는 Stale 로 판정(새 주문 오해제 0 · 체결 유실 0 — 캔슬러 참조).
            if (_pending is not null && (uint)broker <= PendingOrderCascadeCanceller.MaxBrokerId)
                Interlocked.Add(ref _cascadeCancelled, _pending.CancelAllForBroker(broker));
            // [라우터 하드닝] 수렴 루프 — "활성이 블랙리스트에 있는 동안" 건강 후보로 CAS 이동. 이전의 1회 TrySwitch(broker→next) 는
            //   TOCTOU 경합에 열려 있었다: T0 가 후보 1 을 고른 뒤 T1 이 1 을 장애 처리(T1 의 1→2 CAS 는 활성이 아직 0 이라 실패)하고
            //   이어 T0 의 0→1 CAS 가 성공하면 장애 브로커 1 이 활성으로 방치됐다(동시 장애 스트레스 테스트가 실측). 보고자마다 블랙리스트를
            //   먼저 기록한 뒤 이 루프를 돌므로, 마지막 보고자가 끝날 때 활성은 반드시 건강 브로커(또는 전원 장애)다. 반복 ≤ 브로커 수(δ=0).
            for (int attempt = 0; attempt < _board.BrokerCount && _board.BrokerCount > 1; attempt++)
            {
                int cur = _board.Active;
                if (!_board.IsBlacklisted(cur)) break;
                int next = NextHealthy(cur);
                if (next < 0) break;   // 전원 장애 — 전환하지 않음(주문 경로는 이미 Disarm)
                if (_board.TrySwitch(cur, next, nowTicks)) Interlocked.Increment(ref _failovers);
            }
            // [방향 B] 이중(전면) 장애 — 전환할 곳이 없다. 주문 경로는 이미 Disarm 이지만, 여기서 1회 한정 상위 킬(카나리/킬스위치) 격발.
            if (_board.AllBlacklisted && Interlocked.CompareExchange(ref _dualBreakdown, 1, 0) == 0)
            {
                try { _onDualBreakdown?.Invoke(); } catch { /* 콜백 실패가 봉쇄를 되돌리지 않음(이미 sticky Disarm) */ }
            }
            return true;
        }

        public bool IsBrokerFaulted(int broker)
            => (uint)broker < 64u && (Interlocked.Read(ref _faultedMask) & (1L << broker)) != 0;

        [ZeroAllocHotPath]
        private int NextHealthy(int from)
        {
            long faulted = Interlocked.Read(ref _faultedMask) | _board.BlacklistMask;
            int n = _board.BrokerCount;
            for (int k = 1; k < n; k++)
            {
                int c = (from + k) % n;
                if (c < 64 && (faulted & (1L << c)) != 0) continue;
                return c;
            }
            return -1;   // 전원 장애 — 전환하지 않음(주문 경로는 이미 Disarm)
        }

        /// <summary>★물리 이중 키 재무장(원인 규명 후에만). 소프트웨어 단독 복구 불가(liveArmed:false 불변). 한 키라도 결여 → false.</summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Interlocked.Exchange(ref _faultedMask, 0);   // 원인 규명 후 재무장 — 장애 이력도 함께 초기화
            _board.ClearBlacklist();
            Interlocked.Exchange(ref _dualBreakdown, 0);
            Interlocked.Exchange(ref _disarmed, 0);
            return true;
        }
    }
}
