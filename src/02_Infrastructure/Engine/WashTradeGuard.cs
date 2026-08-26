// ============================================================================
// WashTradeGuard — v5.4.0 과제3(제미나이 보강): 자전거래 차단 가드(Wash-Trade Prevention Gate)
//
//  네팅 엔진이 동일 틱 대향 주문을 상쇄해도, 미세한 시간 차(수 밀리초)로 서로 다른 전략이 동일 종목의 매수/매도 잔여를
//  시장에 연속 사출하면 거래소 전산망이 '악성 자전거래(Wash-Trading)·시세조종'으로 탐지해 세션을 강제 셧다운할 수 있다.
//  본 가드는 핫패스 종단(잔여 물리 사출 직전)에 배치되어, 동일 계좌 내부 전략들이 마이크로 윈도우 안에서 서로의 호가를
//  받아먹는 레이스를 전제 감지하고 해당 대향 주문을 즉각 무효화(OrderRejected)한다.
//
//  ── 정직 회계(정찰) ─────────────────────────────────────────────────────────
//  AutonomousFailoverController.TryClaimDispatch 의 '고정 2의 거듭제곱 링 + 슬롯 = 키&mask' 락프리 관용구를 심볼별
//  최신 잔여 방향/시각 추적으로 이식. AlgoExecutionGate 의 Rejected* 거부 계약 사상 계승(단, 여기선 드레인 전 거부).
//
//  δ=0: 고정 배열 슬롯 + 정수 해시 + 주입 시각 비교(new/박싱/락 없음 — 단일 소비자 계약 하 슬롯 원자 갱신). fail-closed:
//  판정 불확실(윈도우 경계·시각 역행)은 거부 방향으로 보수. 결정론: 주입 시각(TimestampTicks)만, 벽시계/RNG 무사용.
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>전략 간 교차 자전거래 차단막 — 동일 심볼 대향 잔여가 마이크로 윈도우 내면 거부(OrderRejected).</summary>
    public sealed class WashTradeGuard
    {
        // 심볼별 최신 물리 잔여 마크: 방향(Side)·사각 시각을 하나의 long 에 팩(락프리 CAS). 별도 심볼 배열로 해시 충돌 검출.
        //   packed = (side << 62) | (ticks & Mask62).  side: 0=Buy,1=Sell.  ticks 62bit(주입 시각·양수 전제).
        private const long Mask62 = (1L << 62) - 1L;
        private readonly long[] _packed;    // 심볼 해시 슬롯 — 최신 잔여(side|ticks)
        private readonly long[] _symbol;    // 슬롯 소유 심볼(충돌 판별)
        private readonly int    _mask;
        private readonly long   _windowTicks;   // 대향 금지 마이크로 윈도우(주입 tickFreq 기준)
        private readonly MmfFlightRecorder? _blackbox;
        private long _rejections, _admits;

        public WashTradeGuard(long windowTicks, int capacity = 1024, MmfFlightRecorder? blackbox = null)
        {
            _windowTicks = windowTicks < 0 ? 0 : windowTicks;
            _blackbox = blackbox;
            int cap = 1; while (cap < Math.Max(8, capacity)) cap <<= 1;   // 2의 거듭제곱
            _packed = new long[cap];
            _symbol = new long[cap];
            for (int i = 0; i < cap; i++) _symbol[i] = long.MinValue;      // 미점유 sentinel
            _mask = cap - 1;
        }

        public long Rejections => Interlocked.Read(ref _rejections);
        public long Admits     => Interlocked.Read(ref _admits);

        private int Slot(long symbolCode) => (int)((symbolCode * 2654435761L) & _mask);   // Knuth 승산 해시

        /// <summary>
        /// ★핫패스(δ=0) — 잔여 물리 사출 직전 교차 자전거래 검문. 동일 심볼의 직전 잔여가 대향이며 윈도우 내면 거부(false·OrderRejected).
        /// 통과 시(true) 이 잔여를 심볼 슬롯의 최신 마크로 기록. 단일 소비자(quote-pump) 계약 — 슬롯 갱신 비경합.
        /// </summary>
        public bool Admit(long symbolCode, OrderSide side, long nowTicks)
        {
            int slot = Slot(symbolCode);
            long prevSym = Volatile.Read(ref _symbol[slot]);
            if (prevSym == symbolCode)
            {
                long prev = Volatile.Read(ref _packed[slot]);
                int  prevSide = (int)((prev >> 62) & 0x3L);
                long prevTicks = prev & Mask62;
                long dt = nowTicks - prevTicks;
                // 대향(매수↔매도) + [윈도우 내(0 ≤ dt ≤ window) ∨ 시각 역행(dt<0)] → 거부. ★적대검증 교정: dt<0(클록 역행/이상)은
                //   fail-closed 로 거부(직전 통과는 문서와 달리 fail-open 이었음). dt>window(오래된 마크)만 통과.
                if (prevSide != (int)side && (dt < 0 || dt <= _windowTicks))
                {
                    Interlocked.Increment(ref _rejections);
                    try { _blackbox?.Record(FlightEventKind.WashTradeRejected, symbolCode, (long)side, dt, 0, 84); } catch { }
                    return false;
                }
            }
            // 통과 — 이 잔여를 최신 마크로 기록(방향·시각).
            long packed = ((long)((int)side & 0x3) << 62) | (nowTicks & Mask62);
            Volatile.Write(ref _symbol[slot], symbolCode);
            Volatile.Write(ref _packed[slot], packed);
            Interlocked.Increment(ref _admits);
            return true;
        }
    }
}
