// ============================================================================
// ArmingInterlockBitmask — [실배선 · 방향 B] Atomic Interlock Circuit Breaker Aggregator (+ 방향 A 기동 샌니티 봉인)
//
//  왜 필요한가(실측):
//   하드닝된 차단 엔진(부팅 무결성·자격증명 유출·포렌식 체인·이상 시세·카오스 진행 가드)은 각자 ArmingBlocked 를 노출하고,
//   호스트(BlazorHomeForm)의 무장 술어가 ~40 항 `||` 체인으로 매 판정마다 전부 읽는다. 두 공백이 있었다:
//    ① 발주 최종 초크포인트(OrderDrainWorker.Dispatch — 실 IBroker.Place 직전)는 이 엔진들을 직접 보지 않는다.
//       디스패처가 판정한 뒤 실제 송출까지의 사이에 엔진이 Trip 해도 그 주문은 그대로 나간다(판정↔송출 TOCTOU).
//    ② 체인의 각 항은 `?? false/true` 를 호출측이 손으로 고른다 — 결속 누락(ref 미대입) 시 fail-open 이 조용히 가능.
//
//  설계 — 푸시형 단일 원자 상태어(δ=0):
//   · 각 소스는 고정 비트를 받는다. 소스는 '자기 상태가 바뀔 때' Link.Resync() 로 자기 비트를 Raise/Clear 한다(푸시).
//     판정 측은 `Volatile.Read(ref _mask) != 0` 단 1회 로드 — 소스 수와 무관한 O(1)(로드 1회 ≈ 서브 ns · 분기 1).
//   · ★필수 소스(requiredMask) 비트는 생성 시점부터 Raise 상태 — '결속 안 된 필수 소스 = 차단'(fail-closed 가 기본값).
//     결속(Bind) 은 소스 현재 상태로 동기화한 뒤에야 비트를 내린다.
//   · [방향 A] 기동 샌니티 봉인: SanityBit(63) 는 생성 시 Raise. 호스트가 조립을 끝낸 직후 Seal() 을 호출하면
//     "필수 소스 전부 결속" 일 때만 SanityBit 를 내린다. 누락이 하나라도 있으면 영구 Raise(부팅 원자 중단 = 무장 불가) +
//     MissingMask 로 무엇이 빠졌는지 보고. 조립 코드가 예외로 중간에 끊겨 Seal 에 도달하지 못해도 SanityBit 가 남아 차단.
//
//  경합 정합(Link.Resync):
//   · Raise 경로: 소스가 차단이면 Interlocked.Or — 멱등.
//   · Clear 경로: 소스가 비차단이면 Interlocked.And 로 내린 뒤 ★재확인 — 그 사이 Trip 이 끼었으면 즉시 다시 Raise.
//     Trip 측은 '자기 플래그를 먼저 세우고 → Resync' 순서라, 어떤 인터리빙에서도 최종 비트 = 소스 최종 상태로 수렴한다.
//   · 한계(정직): Clear 와 재Raise 사이의 수 ns 창은 '재무장(Resync-Clear) 과 Trip 이 정확히 겹칠 때'만 존재한다.
//     그래서 이 집계기는 기존 무장 술어 체인을 '대체'하지 않고 앞단 단락(fast reject)과 송출 직전 최종 veto 로 '추가'된다 —
//     권위 체인은 각 엔진 플래그를 직접 읽으므로 방어는 겹겹이 누적될 뿐 약해지지 않는다.
//   · 소스의 ArmingBlocked 가 예외를 던지면 차단으로 간주(fail-closed).
//
//  불변: δ=0(Blocked/Raise/Clear/Resync 힙 0 · 링크는 Bind 시 1회 할당) · 락프리(Interlocked.Or/And) · liveArmed:false
//        (이 타입은 차단만 올릴 수 있고 발주를 유발하는 경로가 없다 — 비트 0 은 '다른 모든 게이트 통과 시에만' 의미가 있다).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Core
{
    /// <summary>무장 차단 신호 소스(각 인터락 엔진이 구현).</summary>
    public interface IArmingBlockSource
    {
        bool ArmingBlocked { get; }
    }

    /// <summary>
    /// [E2E 스트레스 · 포렌식] 집계어 비트 전이 관측자 — 비트가 0→1(raised) 또는 1→0(cleared)로 '실제로 바뀐' 순간에만
    /// 정확히 1회 호출된다(Interlocked.Or/And 의 직전 값으로 판정 → 동시 다발 Trip 에서도 비트당 1회 · 중복 0).
    /// 호출 시점에 비트는 이미 반영됐다(차단이 관측보다 먼저 = 관측자 예외/지연이 차단을 늦추지 못함). 구현은 δ=0 · 비차단이어야 한다.
    /// </summary>
    public interface IInterlockTransitionSink
    {
        void OnTransition(long raisedBits, long clearedBits, long maskAfter);
    }

    /// <summary>고정 비트 배정 — 진단/텔레메트리 판독용 안정 레이아웃.</summary>
    public enum ArmingInterlockSource : byte
    {
        BootIntegrity   = 0,
        CredentialLeak  = 1,
        ForensicChain   = 2,
        MarketSpike     = 3,
        ChaosProgress   = 4,
        ExecutionImpact = 5,
        RouterFeed      = 6,   // [피드 연동 · 방향 B] 시세/체결 피드 단절 회로 차단기
        OrderSlotAge    = 7,   // [피드 연동 · 방향 A] 응답 타임아웃 슬롯 회수(미확인 노출) 차단
        ExtremeSlippage = 8,   // [보호 지정가 · 방향 B] 체결가 극한 괴리 기습 체결 → Disarm
        MmfFabric       = 9,   // [MMF Triage · 방향 B] MMF IPC 시퀀스 역전/찢김/경계 파손 → Disarm
        BrokerStateMismatch = 10, // [키움 프로토콜 · 방향 B] 클라이언트 추적 잔량 ≠ 브로커 미체결 잔량 → Disarm
        OrderbookIlliquidity = 11, // [L2 · 방향 B] 연속매매 중 호가 교차/공백/극한 스프레드 → Disarm
        LatencyStall    = 12,  // [저지연 · 방향 B] Tick-to-Trade 프로세스 내 구간 정지 임계 초과(GC 정지·선점) → Disarm
        KiwoomServerError = 13, // [키움 COM · 방향 B] 비밀번호·계좌 정지·증거금 부족·STA 펌프 동결 → Disarm
        AccountDivergence = 14, // [계좌 · 방향 B] OPW00018 보유 ↔ 호스트 추정 보유 불일치(정적 구간) → Disarm
        EgressBypass    = 15,  // [E2E · 방향 B] liveArmed:false 인데 실 브로커 Place 도달(무장 우회 오작동) → 송출 차단 + Disarm
        PreFlightBoot   = 16,  // [사전 검증] 시계 역행(부팅·런타임) · 안전 설정 런타임 변조 · 프록시 파이프 서버 신원 불일치 → Disarm + 키 소거
        CorrelationCrash = 17, // [포트폴리오 · 방향 B] 보유 종목 동시 하락(체계적 상관 급등) → 미체결 일괄 취소 + Disarm
        KisAuth         = 18,  // [KIS] 접근 토큰 만료 임박(5분) 갱신 실패 · 만료 → Disarm
        KisSession      = 19,  // [KIS · 방향 B] 실시간 WebSocket PINGPONG/수신 침묵 타임아웃 → KIS 블랙리스트 + Disarm
        Rollover        = 20,  // [롤오버] 승계 슬롯 불일치(체크섬·건수·충돌) 또는 승계 후 브로커 대사 미완료 → 신규 발주 차단
        HealthAgent     = 21,  // [헬스] Working Set 폭주 · 스레드풀 정체(큐 지연) · GC 정지 폭주 → Disarm
        OpenOrderDivergence = 22, // [미체결 대사] 브로커 미체결 원장 미대사(최초) · 승계/추적 주문 불일치 · 유령 주문(브로커에만 존재) → Disarm(+ 비상 일괄 취소)
        ChildSlotRejection = 23, // [자식 슬롯 분할] 자식 주문 송출 거부/실패(미송출 형제 회수) · 자식 간 체결 불균형(잔여 일괄 취소) → Disarm
        KisLimitFeed    = 24,  // [KIS 실배선] REST 잔고/주문가능 조회 연속 3회 실패 → KIS 한도 0 격리 + Disarm(미설정 = 비차단)
        StartupSanity   = 63,
    }

    /// <summary>소스 ↔ 비트 결속(1 소스 1 링크 · Bind 시 1회 할당). 소스는 상태 전이 직후 Resync() 를 호출한다.</summary>
    public sealed class ArmingInterlockLink
    {
        private readonly ArmingInterlockBitmask _owner;
        private readonly IArmingBlockSource _source;
        private readonly long _bit;

        internal ArmingInterlockLink(ArmingInterlockBitmask owner, IArmingBlockSource source, int bit)
        {
            _owner = owner; _source = source; _bit = 1L << bit;
        }

        public ArmingInterlockSource Source => (ArmingInterlockSource)System.Numerics.BitOperations.TrailingZeroCount((ulong)_bit);

        /// <summary>소스 현재 상태를 비트에 반영(δ=0). Clear 후 재확인으로 Trip 경합을 봉합.</summary>
        public void Resync()
        {
            if (IsSourceBlocked()) { _owner.RaiseBits(_bit); return; }
            _owner.ClearBits(_bit);
            if (IsSourceBlocked()) _owner.RaiseBits(_bit);
        }

        private bool IsSourceBlocked()
        {
            try { return _source.ArmingBlocked; }
            catch { return true; }   // 판독 불가 = 차단(fail-closed)
        }
    }

    public sealed class ArmingInterlockBitmask
    {
        public const int SanityBit = (int)ArmingInterlockSource.StartupSanity;
        private const int MaxSourceBits = 63;   // 0..62 = 소스 · 63 = 샌니티

        private readonly long _required;
        private readonly ArmingInterlockLink?[] _links = new ArmingInterlockLink?[MaxSourceBits];
        private long _mask, _bound, _blockedReads, _sealAttempts;
        private int _sealed;
        private IInterlockTransitionSink? _sink;
        private long _transitions, _sinkFaults;

        /// <param name="requiredSources">결속 전까지 차단 비트가 선 채로 유지될 필수 소스들.</param>
        public ArmingInterlockBitmask(params ArmingInterlockSource[] requiredSources)
        {
            long req = 0;
            foreach (var s in requiredSources)
            {
                if ((int)s >= MaxSourceBits) throw new ArgumentOutOfRangeException(nameof(requiredSources), "StartupSanity 는 자동 필수");
                req |= 1L << (int)s;
            }
            _required = req;
            _mask = req | (1L << SanityBit);   // ★필수 미결속 + 샌니티 미봉인 = 차단(기본 fail-closed)
        }

        /// <summary>★발주 허용 판정 핫패스(δ=0 · O(1)) — 어느 비트라도 서 있으면 차단.</summary>
        public bool Blocked
        {
            get
            {
                if (Volatile.Read(ref _mask) == 0L) return false;
                Interlocked.Increment(ref _blockedReads);
                return true;
            }
        }

        public long Mask => Volatile.Read(ref _mask);
        public long RequiredMask => _required;
        public long BoundMask => Interlocked.Read(ref _bound);
        /// <summary>결속되지 않은 필수 소스 비트(샌니티 진단).</summary>
        public long MissingMask => _required & ~Interlocked.Read(ref _bound);
        public bool IsSealed => Volatile.Read(ref _sealed) == 1;
        public long BlockedReads => Interlocked.Read(ref _blockedReads);
        public long SealAttempts => Interlocked.Read(ref _sealAttempts);
        public bool IsRaised(ArmingInterlockSource s) => (Volatile.Read(ref _mask) & (1L << (int)s)) != 0;
        /// <summary>관측자에 통지된 실제 비트 전이 누계(raised/cleared 각각 1건).</summary>
        public long Transitions => Interlocked.Read(ref _transitions);
        public long SinkFaults => Interlocked.Read(ref _sinkFaults);

        /// <summary>[포렌식] 비트 전이 관측자 결속(1회 · 교체 금지). 결속 시점의 현재 차단 비트를 raised 로 1회 통지(기동 상태 각인).</summary>
        public void AttachTransitionSink(IInterlockTransitionSink sink)
        {
            ArgumentNullException.ThrowIfNull(sink);
            if (Interlocked.CompareExchange(ref _sink, sink, null) is not null)
                throw new InvalidOperationException("전이 관측자 이미 결속됨 — 교체 금지(포렌식 이력 단절 방지)");
            long m = Volatile.Read(ref _mask);
            if (m != 0) Notify(m, 0, m);
        }

        /// <summary>소스 결속(콜드 패스). 같은 비트 재결속은 거부(소스 바꿔치기 차단). 결속 직후 현재 상태로 동기화.</summary>
        public ArmingInterlockLink Bind(ArmingInterlockSource bit, IArmingBlockSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            int b = (int)bit;
            if (b >= MaxSourceBits) throw new ArgumentOutOfRangeException(nameof(bit));
            var link = new ArmingInterlockLink(this, source, b);
            if (Interlocked.CompareExchange(ref _links[b], link, null) is not null)
                throw new InvalidOperationException($"인터락 비트 {bit} 이미 결속됨 — 소스 교체 금지(fail-closed)");
            RaiseBits(1L << b);                    // 동기화 전까지 차단 유지
            Interlocked.Or(ref _bound, 1L << b);
            link.Resync();
            return link;
        }

        /// <summary>
        /// [방향 A] 기동 샌니티 봉인 — 호스트 조립 완료 직후 1회. 필수 소스가 전부 결속됐을 때만 SanityBit 를 내린다.
        /// 누락 시 false + missing(비트) — SanityBit 는 영구 유지(무장 불가). 봉인 후 재호출은 멱등(true).
        /// </summary>
        public bool Seal(out long missing)
        {
            Interlocked.Increment(ref _sealAttempts);
            missing = MissingMask;
            if (Volatile.Read(ref _sealed) == 1) return true;
            if (missing != 0) return false;
            ResyncAll();                                   // 봉인 직전 전 소스 재동기화(결속~봉인 사이 누락 전이 흡수)
            if (Interlocked.CompareExchange(ref _sealed, 1, 0) == 0) ClearBits(1L << SanityBit);
            return true;
        }

        /// <summary>콜드 백스톱 — 주기 모니터가 전 소스를 재동기화(푸시 누락 방어). δ=0.</summary>
        public void ResyncAll()
        {
            for (int i = 0; i < _links.Length; i++) Volatile.Read(ref _links[i])?.Resync();
        }

        // ★Interlocked.Or/And 는 직전 값을 돌려준다 — 실제로 바뀐 비트만 관측자에 통지(동시 Trip 경합에서도 비트당 정확히 1회).
        internal void RaiseBits(long bits)
        {
            long prev = Interlocked.Or(ref _mask, bits);
            long raised = bits & ~prev;
            if (raised != 0) Notify(raised, 0, prev | bits);
        }

        internal void ClearBits(long bits)
        {
            long prev = Interlocked.And(ref _mask, ~bits);
            long cleared = bits & prev;
            if (cleared != 0) Notify(0, cleared, prev & ~bits);
        }

        private void Notify(long raised, long cleared, long maskAfter)
        {
            if (Volatile.Read(ref _sink) is not { } s) return;
            Interlocked.Increment(ref _transitions);
            try { s.OnTransition(raised, cleared, maskAfter); }
            catch (Exception) { Interlocked.Increment(ref _sinkFaults); }   // 관측 실패는 차단에 무영향(비트는 이미 반영)
        }
    }
}
