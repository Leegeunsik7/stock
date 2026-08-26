// ============================================================================
// FatFingerVelocityInterlock — v7.4.0 ★Think Beyond Gemini: 실자본 사출 방화벽
//   "Fat-Finger 가격 격리 + ★폭주 명목속도(Runaway Notional-Velocity) — 하나의 사출 불변식으로 융합"
//
//  ── 왜 제미나이의 A/B 를 초월하는가 ────────────────────────────────────────
//  제미나이는 두 파멸 리스크를 서로 다른 두 점검으로 막자 제안했다:
//    · 아이디어 A — Fat-Finger 가격 괴리 안티-콜리전(호가 대비 지정가 N% 이탈 물리 차단).
//    · 아이디어 B — Rate-Limit 캐스케이드 거버너(초당 요청이 쿼터 90% 도달 시 격리).
//  A 와 B 는 모두 '사출 찰나의 단일 임계 점검'이다. 그러나 실자본 최악의 참사는 개별 점검을 '전부 통과하는'
//  주문들의 '집합적 폭주' 다 — 2012 Knight Capital 은 개별로는 전부 유효한 주문 폭풍으로 45분에 $440M 를 태웠다.
//  A(개별 가격)도 B(개별 rate)도 '개별 유효하나 집합적으로 폭주하는 주문 스톰' 을 잡지 못한다.
//
//  이 인터록은 그 맹점을 '하나의 사출 불변식(이 주문 흐름이 인간 감독 하 자본 집행으로서 물리적으로 타당한가)'
//  으로 통합해 두 직교 축을 δ=0 로 동시 검문한다:
//    (1) 가격 축(A 포함·초월): 지정가가 기준가(L2 최우선/최근가) 대비 N‰ 이상 괴리면 물리 차단(fat-finger).
//    (2) ★명목속도 축(신규): 롤링 윈도(기본 1s)의 '누적 명목' 과 '동일 방향 연속 주문 수' 를 δ=0 링으로 계측해,
//        개별 유효해도 집합 누적 명목이 예산을 넘거나 동일 방향 버스트가 결정론 한계를 넘으면 폭주로 판정·차단.
//    ∴ 위반 시 인터록이 Frozen 으로 래치되고, 디스패처가 이를 받아 ForceFreeze(비가역 밸브 차단)로 승격한다.
//
//  ★불변: δ=0(Admit 핫패스 힙 0 — 사전할당 링·정수 산술·Interlocked·주입 시계) · 결정론(주입 시계·순수 산술) ·
//    fail-closed(오버플로우/미상 명목 → 최대값 취급·위반 시 차단) · liveArmed:false(순수 veto — IBroker·소켓·onSend
//    참조 0. 발주를 '막을' 뿐 유발 불가. Frozen 은 blockWhile tighten-only + 디스패처 ForceFreeze 로만 승격).
//  ★단일 라이터: Admit 는 GatedOrderDispatcher.Drain(단일 소비자 계약)에서만 호출 — 링은 단일 라이터·다중 리더(텔레메트리).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>실자본 사출 방화벽(δ=0). Admit(in OrderProposal)=false → fat-finger/폭주 veto·Frozen 래치. 디스패처가 ForceFreeze 승격.</summary>
    public sealed class FatFingerVelocityInterlock
    {
        private const int RingSize = 1024;         // 2^k — 롤링 윈도 사출 이력(δ=0 사전할당). 초과 시 fail-closed(과소계수 방지).
        private const int RingMask = RingSize - 1;

        private struct Slot { public long Ticks; public long NotionalRaw; public byte Side; }

        private readonly long        _maxDeviationPermille;    // 가격 괴리 임계 ‰(기본 100=10%)
        private readonly long        _windowTicks;             // 명목속도 롤링 윈도(QPC ticks)
        private readonly long        _windowNotionalBudgetRaw; // 윈도 누적 명목 예산(raw 10^8·0=비활성)
        private readonly long        _maxSameSideBurst;        // 윈도 내 동일 방향 최대 주문 수(0=비활성)
        private readonly Func<long, long>? _refPriceRaw;       // symbol→기준가 raw(10^8·0=미상). PreTradeRiskShield 관례 재사용.
        private readonly Func<long>  _nowTicks;

        private readonly Slot[] _ring = new Slot[RingSize];    // 사전할당(δ=0)
        private int  _head;                                    // 단일 라이터(drain 스레드)
        private int  _frozen;                                  // 0/1 — 1회 원자 래치
        private long _inspections, _priceVetoes, _velocityVetoes;
        private long _lastWindowNotionalRaw, _lastWindowSameSide, _lastForensic;

        // 포렌식 코드(Frozen 원인)
        public const long V_PRICE_DEVIATION = 1L << 0;
        public const long V_NOTIONAL_BUDGET = 1L << 1;
        public const long V_SAMESIDE_BURST  = 1L << 2;
        public const long V_WINDOW_SATURATED = 1L << 3;   // 윈도에 RingSize+ 주문(이력 용량 초과 폭주) — fail-closed

        /// <param name="maxDeviationPermille">지정가 기준가 괴리 허용 ‰(기본 100=10%). 초과 시 fat-finger 차단.</param>
        /// <param name="windowSeconds">명목속도 롤링 윈도(초·기본 1).</param>
        /// <param name="windowNotionalBudgetRaw">윈도 누적 명목 예산(raw 10^8·0=비활성).</param>
        /// <param name="maxSameSideBurst">윈도 내 동일 방향 최대 주문 수(0=비활성).</param>
        /// <param name="refPriceRaw">symbol→기준가 raw 프로브(옵션·null 또는 0 반환=가격 축 생략).</param>
        public FatFingerVelocityInterlock(long maxDeviationPermille = 100, int windowSeconds = 1,
                                          long windowNotionalBudgetRaw = 0, long maxSameSideBurst = 0,
                                          Func<long, long>? refPriceRaw = null, Func<long>? nowTicks = null,
                                          long tickFrequency = 0)
        {
            _maxDeviationPermille = maxDeviationPermille < 1 ? 1 : maxDeviationPermille;
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            long freq = tickFrequency > 0 ? tickFrequency : Stopwatch.Frequency;
            _windowTicks = freq * Math.Max(1, windowSeconds);
            _windowNotionalBudgetRaw = windowNotionalBudgetRaw;
            _maxSameSideBurst = maxSameSideBurst;
            _refPriceRaw = refPriceRaw;
        }

        public bool Frozen               => Volatile.Read(ref _frozen) == 1;
        public long Inspections          => Interlocked.Read(ref _inspections);
        public long PriceVetoes          => Interlocked.Read(ref _priceVetoes);
        public long VelocityVetoes       => Interlocked.Read(ref _velocityVetoes);
        public long LastWindowNotionalRaw => Interlocked.Read(ref _lastWindowNotionalRaw);
        public long LastWindowSameSide   => Interlocked.Read(ref _lastWindowSameSide);
        public long LastForensic         => Interlocked.Read(ref _lastForensic);

        /// <summary>
        /// 사출 직전 1회 검문(δ=0·단일 라이터). false=veto(fat-finger 가격 괴리 또는 집합 폭주) → Frozen 래치.
        /// 이미 Frozen 이면 즉시 false. 디스패처가 false 를 받아 ForceFreeze(비가역 밸브 차단)로 승격한다.
        /// </summary>
        public bool Admit(in OrderProposal p)
        {
            Interlocked.Increment(ref _inspections);
            if (Volatile.Read(ref _frozen) == 1) return false;   // 이미 동결 — 전량 차단

            // ── 명목 산출(시장가/오버플로우 fail-closed) ──
            long notional;
            long refRaw = _refPriceRaw?.Invoke(p.SymbolCode) ?? 0L;
            if (p.PriceRaw > 0)
            {
                try { notional = QMath.Notional(p.PriceRaw, p.QtyRaw, roundUp: true); }
                catch (OverflowException) { notional = long.MaxValue; }
            }
            else   // 시장가 — 지정가 미상. 기준가로 명목 추정. 기준가도 미상이면 ★fail-closed: 예산 축 활성 시 '무한 명목'
            {      //   (BlastRadius 시장가=미상 관례)으로 취급해 미상 명목 시장가 스톰이 예산 축을 우회하지 못하게 한다.
                if (refRaw > 0) { try { notional = QMath.Notional(refRaw, p.QtyRaw, roundUp: true); } catch (OverflowException) { notional = long.MaxValue; } }
                else notional = _windowNotionalBudgetRaw > 0 ? long.MaxValue : 0L;   // 예산 비활성 시 0(무해·예산 축 off)
            }

            // ── (1) 가격 축(fat-finger): 지정가 ∧ 기준가 가용 시 괴리 검문 ──
            if (p.PriceRaw > 0 && refRaw > 0)
            {
                long diff = p.PriceRaw > refRaw ? p.PriceRaw - refRaw : refRaw - p.PriceRaw;
                // 괴리 ‰ = diff/ref*1000. 오버플로우 회피 위해 곱 전 분모 대비 비교.
                long deviationPermille;
                try { deviationPermille = checked(diff * 1000L) / refRaw; }
                catch (OverflowException) { deviationPermille = long.MaxValue; }
                if (deviationPermille >= _maxDeviationPermille)
                {
                    Interlocked.Increment(ref _priceVetoes);
                    TripFreeze(V_PRICE_DEVIATION);
                    return false;
                }
            }

            // ── (2) ★명목속도 축: 링 기록 후 롤링 윈도 누적 명목 + 동일 방향 버스트 계측 ──
            long now = _nowTicks();
            long windowFloor = now - _windowTicks;
            bool velocityEnabled = _windowNotionalBudgetRaw > 0 || _maxSameSideBurst > 0;   // 속도 축 활성 여부(둘 다 0=opt-out)
            int slot = _head & RingMask;
            // ★링 포화 fail-closed: 덮어쓸 슬롯이 아직 윈도 내면 윈도에 RingSize(=1024)+ 주문 = 이력 용량 초과 폭주. 과소계수(fail-open)
            //   대신 차단(속도 축 활성 시에만 — opt-out 존중). 사람 감독 자본집행에서 윈도당 1024+ 주문은 명백한 런어웨이.
            bool ringSaturated;
            { ref Slot victim = ref _ring[slot]; ringSaturated = velocityEnabled && victim.Ticks != 0L && victim.Ticks > windowFloor && victim.Ticks <= now; }
            _ring[slot].Ticks = now;
            _ring[slot].NotionalRaw = notional;
            _ring[slot].Side = p.SideByte;
            _head++;   // 단일 라이터

            long windowNotional = 0, sameSide = 0;
            byte curSide = p.SideByte;
            for (int i = 0; i < RingSize; i++)
            {
                ref Slot s = ref _ring[i];
                if (s.Ticks == 0L) continue;                             // 미기록 슬롯(sentinel) — nowTicks 는 양의 단조 ticks(Stopwatch) 전제
                if (s.Ticks <= windowFloor || s.Ticks > now) continue;   // 윈도 밖
                // 누적 명목(오버플로우 포화)
                long acc = windowNotional + s.NotionalRaw;
                windowNotional = acc < windowNotional ? long.MaxValue : acc;   // 랩 → 포화
                if (s.Side == curSide) sameSide++;
            }
            Volatile.Write(ref _lastWindowNotionalRaw, windowNotional);
            Volatile.Write(ref _lastWindowSameSide, sameSide);

            bool budgetBreach = _windowNotionalBudgetRaw > 0 && windowNotional > _windowNotionalBudgetRaw;
            bool burstBreach  = _maxSameSideBurst > 0 && sameSide > _maxSameSideBurst;
            if (budgetBreach || burstBreach || ringSaturated)
            {
                Interlocked.Increment(ref _velocityVetoes);
                TripFreeze((budgetBreach ? V_NOTIONAL_BUDGET : 0L) | (burstBreach ? V_SAMESIDE_BURST : 0L) | (ringSaturated ? V_WINDOW_SATURATED : 0L));
                return false;
            }
            return true;
        }

        private void TripFreeze(long forensic)
        {
            Volatile.Write(ref _lastForensic, forensic);
            Interlocked.Exchange(ref _frozen, 1);   // 원자 래치(1회 승격은 디스패처 ForceFreeze 가 담당)
        }

        /// <summary>수동 해제(운영자 서명 후 재개·테스트). Frozen 및 윈도 이력 초기화.</summary>
        public void Reset()
        {
            Volatile.Write(ref _frozen, 0);
            Volatile.Write(ref _lastForensic, 0L);
            for (int i = 0; i < RingSize; i++) { _ring[i].Ticks = 0; _ring[i].NotionalRaw = 0; _ring[i].Side = 0; }
            _head = 0;
        }
    }
}
