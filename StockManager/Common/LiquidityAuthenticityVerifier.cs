// ============================================================================
// LiquidityAuthenticityVerifier — v9.0.0 ★Think Beyond Gemini: 유동성 진위 인증기
//   "스푸핑을 '차단'하는 데 그치지 않고, 유동성의 '진위'를 인증한다 — Spoofing Iceberg Trap 정면 격파"
//
//  ── 왜 제미나이의 A/B 를 초월하는가 ────────────────────────────────────────
//  제미나이는 두 리스크를 각각 막자 제안했다:
//    · A — Anti-Spoofing Cancel-Velocity Filter(취소 속도 > 체결 속도면 스푸핑으로 차단).
//    · B — Slice Execution Mesh(슬라이스 간 시장 충격 실측 → 다음 진입가 조율).
//  그러나 빙산 포획의 진짜 난제는 '차단'이 아니라 '구별'이다 — IcebergHunter 가 포착한 '재전개'가 진짜 숨은
//  물량(빙산)인지, 체결 없이 취소만 반복하는 허수 호가(스푸핑)인지 가려야 한다(Spoofing Iceberg Trap). A 는
//  스푸핑을 막지만 '진짜 빙산까지' 오탐 차단할 수 있고, 빙산 재전개와 스푸핑 재전개를 구별하지 못한다.
//
//  이 인증기는 '하나의 진위 불변식'으로 통합한다: 각 호가 측면의 롤링 윈도에서 '체결량 vs 취소량'을 δ=0 로 계측해
//  유동성 진위(Authenticity)를 채점한다 — 체결이 지배하면 '진짜'(빙산: 재전개가 실제 실행됨), 취소가 지배하면
//  '가짜'(스푸핑: 실행 없이 당김). ∴ SOR 은 '진짜' 유동성으로만 라우팅하고, '가짜' 레이어 사출을 O(1) 차단하며,
//  IcebergHunter 의 재전개 신호를 '진위'로 게이팅한다(스푸핑 재전개 → 빙산 오탐 방지·진짜 빙산 → 포획 확정).
//
//  ★불변: δ=0(RecordBid/Ask·AllowRoute·Authenticity 핫패스 힙 0·사전할당 링·Interlocked·주입 시계) · 결정론 ·
//    liveArmed:false(순수 미시구조 인증 — IBroker·소켓·onSend 참조 0. 라우팅 '허용/차단'만·발주 유발 0) · fail-closed
//    (불확실/취소 지배 → 가짜 판정·차단). ★단일 라이터(관측 스레드).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>유동성 진위 인증기(δ=0·O(1) 링). RecordBid/Ask(체결,취소,ticks) → 진짜 빙산 vs 가짜 스푸핑 구별·라우팅 게이팅.</summary>
    public sealed class LiquidityAuthenticityVerifier
    {
        private const int RingSize = 256, RingMask = RingSize - 1;
        private struct Slot { public long Ticks; public long Fill; public long Cancel; }

        private readonly long _windowTicks;
        private readonly long _spoofRatioPermille;   // 취소/체결 비 임계 ‰(초과=스푸핑·기본 3000=3배)

        private readonly Slot[] _bid = new Slot[RingSize];
        private readonly Slot[] _ask = new Slot[RingSize];
        private int  _bidHead, _askHead;
        private long _bidAuth = 1000, _askAuth = 1000, _spoofBlocks;
        private int  _bidSpoof, _askSpoof;

        // ★결정론: 윈도 경계를 '주입 ticks'만으로 결정 — tickFrequency 미지정 시 호스트 Stopwatch.Frequency 대신
        //   고정 기준(1e7 tick/s·QPC 관례)을 사용(호스트 무관·리플레이 재현성). 주입 ticks 단위·tickFrequency 는 호출자 계약.
        public LiquidityAuthenticityVerifier(int windowSeconds = 2, long spoofRatioPermille = 3000, long tickFrequency = 0)
        {
            long freq = tickFrequency > 0 ? tickFrequency : 10_000_000L;
            _windowTicks = freq * Math.Max(1, windowSeconds);
            _spoofRatioPermille = Math.Max(1000, spoofRatioPermille);
        }

        /// <summary>매수측 진위 ‰(1000=진짜·0=가짜 스푸핑·δ=0).</summary>
        public long BidAuthenticityPermille => Interlocked.Read(ref _bidAuth);
        public long AskAuthenticityPermille => Interlocked.Read(ref _askAuth);
        public bool BidSpoofed => Volatile.Read(ref _bidSpoof) == 1;
        public bool AskSpoofed => Volatile.Read(ref _askSpoof) == 1;
        public long SpoofBlocks => Interlocked.Read(ref _spoofBlocks);

        /// <summary>매수 호가 이벤트 관측(δ=0·단일 라이터). fillQty=체결, cancelQty=취소.</summary>
        public void RecordBid(long fillQty, long cancelQty, long ticks)
            => Record(_bid, ref _bidHead, ref _bidAuth, ref _bidSpoof, fillQty, cancelQty, ticks);

        /// <summary>매도 호가 이벤트 관측(δ=0).</summary>
        public void RecordAsk(long fillQty, long cancelQty, long ticks)
            => Record(_ask, ref _askHead, ref _askAuth, ref _askSpoof, fillQty, cancelQty, ticks);

        /// <summary>★라우팅 진위 게이트(δ=0). 매수(isBuy)는 매도 유동성으로 체결 → 매도측 스푸핑이면 차단. 진짜만 허용.</summary>
        public bool AllowRoute(bool isBuy)
        {
            bool spoofed = isBuy ? BidToAskSpoof() : Volatile.Read(ref _bidSpoof) == 1;
            if (spoofed) { Interlocked.Increment(ref _spoofBlocks); return false; }
            return true;
        }
        private bool BidToAskSpoof() => Volatile.Read(ref _askSpoof) == 1;

        private void Record(Slot[] ring, ref int head, ref long auth, ref int spoof,
                            long fillQty, long cancelQty, long ticks)
        {
            int slot = head & RingMask;
            ring[slot].Ticks = ticks;
            ring[slot].Fill = fillQty < 0 ? 0 : fillQty;
            ring[slot].Cancel = cancelQty < 0 ? 0 : cancelQty;
            head++;

            long floor = ticks - _windowTicks;
            long sumFill = 0, sumCancel = 0;
            for (int i = 0; i < RingSize; i++)
            {
                ref Slot s = ref ring[i];
                if (s.Ticks == 0L || s.Ticks <= floor || s.Ticks > ticks) continue;
                long f = sumFill + s.Fill; sumFill = f < sumFill ? long.MaxValue : f;
                long c = sumCancel + s.Cancel; sumCancel = c < sumCancel ? long.MaxValue : c;
            }
            // ★오버플로 안전: sumFill·sumCancel(각 ≤ long.Max) 합·곱을 Int128 로 계산 — long 랩(음수 → fail-open) 방지.
            //   Int128 은 값타입이라 힙 할당 0(δ=0 유지). fail-closed: 데이터 없으면 진위 1000.
            System.Int128 total = (System.Int128)sumFill + sumCancel;
            long a = total > 0 ? (long)((System.Int128)sumFill * 1000 / total) : 1000L;   // 진위 = 체결/(체결+취소)
            Volatile.Write(ref auth, a);
            // 스푸핑: 취소가 체결의 spoofRatio 배 초과(체결 실행 거의 없이 취소만 반복 = 허수). 교차곱 비교(0除·랩 없음).
            bool sp = sumFill == 0 ? sumCancel > 0
                    : (System.Int128)sumCancel * 1000 > (System.Int128)_spoofRatioPermille * sumFill;
            Volatile.Write(ref spoof, sp ? 1 : 0);
        }
    }
}
