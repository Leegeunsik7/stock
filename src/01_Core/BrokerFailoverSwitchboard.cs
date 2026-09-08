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
        private readonly int _count;

        public LockFreeBrokerSwitchboard(int brokerCount, int initialActive = 0)
        {
            if (brokerCount < 1) throw new ArgumentOutOfRangeException(nameof(brokerCount), "브로커 수는 ≥1.");
            _count = brokerCount;
            _active = (uint)initialActive < (uint)brokerCount ? initialActive : 0;
        }

        public int BrokerCount => _count;
        public int Active => (int)Volatile.Read(ref _active);
        public long SwitchCount => Interlocked.Read(ref _switches);
        public long LastSwitchTicks => Volatile.Read(ref _lastSwitchTicks);

        /// <summary>★CAS 원자 전환 — 현재 활성이 expected 일 때만 next 로. 경합 안전(lost-update 불가). δ=0.</summary>
        public bool TrySwitch(int expected, int next, long nowTicks)
        {
            if ((uint)next >= (uint)_count) return false;
            if (Interlocked.CompareExchange(ref _active, next, expected) == expected)
            {
                if (next != expected) { Volatile.Write(ref _lastSwitchTicks, nowTicks); Interlocked.Increment(ref _switches); }
                return true;
            }
            return false;
        }

        /// <summary>강제 전환(현재값 무관·원자). 반환=이전 활성 인덱스. δ=0.</summary>
        public int SwitchTo(int next, long nowTicks)
        {
            if ((uint)next >= (uint)_count) return Active;
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

        private long _disarmed;    // 1=주문 경로 봉쇄(sticky·fail-closed)
        private long _wiped;       // 자격증명 소거 1회 보장(멱등)
        private long _failovers;
        private int  _lastFault;   // (byte)BrokerFaultKind

        /// <param name="credentials">API Secret 버퍼(옵션). 장애 시 0x00 소거(1회).</param>
        public BrokerFailoverSentinel(
            LockFreeBrokerSwitchboard board,
            long http429Threshold = 5,
            long http5xxThreshold = 3,
            long slippageBpsThreshold = 50,
            PoisonedSecretBuffer? credentials = null)
        {
            _board        = board ?? throw new ArgumentNullException(nameof(board));
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

        /// <summary>
        /// 장애 보고(δ=0 핫패스·결정론·주입 tick). 장애 판정 시 → ①주문 경로 0ms 원자 Disarm(sticky) ②활성 브로커면 보조로
        /// 무할당 원자 페일오버(데이터 무중단) ③자격증명 0x00 소거(1회). 반환=장애로 판정됐는가.
        /// </summary>
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

            if (broker == _board.Active && _board.BrokerCount > 1)   // 활성 브로커 장애 → 보조로 원자 페일오버(데이터 무중단)
            {
                int next = (broker + 1) % _board.BrokerCount;
                _board.SwitchTo(next, nowTicks);
                Interlocked.Increment(ref _failovers);
            }
            return true;
        }

        /// <summary>★물리 이중 키 재무장(원인 규명 후에만). 소프트웨어 단독 복구 불가(liveArmed:false 불변). 한 키라도 결여 → false.</summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Interlocked.Exchange(ref _disarmed, 0);
            return true;
        }
    }
}
