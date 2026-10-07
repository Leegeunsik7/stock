// ============================================================================
// BrokerLimitFeedProvider — [실전 결선 · 추천 A] 브로커별 실시간 주문 한도 → ChildSlotOrderSplitter 공급(δ=0) + 한도 소진 격리(방향 B)
//
//  브로커 b 의 한도(주) =
//    매수: (계좌 매수 여유[원] + 이 부모가 이미 예약한 명목 — 같은 계좌 브로커만) × 10^8 / 자식 기준 지정가 raw   (버림)
//    매도: 해당 브로커 계좌의 추정 보유수량(종목별)
//    → min(위 값, 브로커별 주문당 상한). 계좌 뷰 미결속(어댑터 미연결 · 자격증명 없음) / 스냅샷 만료 = Available=false(그 브로커로 자식 0).
//  ★이중 차감 방지: RouterFeedGate 는 분할 '전'에 부모 매수 명목을 계좌에 예약한다 — 같은 계좌를 보는 브로커의 여유는 이미 그만큼
//    줄어 있으므로 parentReservedWon 을 되돌려 더한다(SharesGateReservation=true 인 뷰만).
//  방향 B — 한도 소진 격리: 결속·신선한데 한도 0 이면 그 브로커 비트를 격리 마스크에 올리고(전이 1회 계수) Available=false → 분할기는
//    잔여 한도 브로커로만 나눈다(안전 우회). 전 브로커가 불가하면 분할기 NoBroker → 게이트가 전방 거부 + 분할 회로 Disarm(비트 23).
//    한도가 다시 생기면(새 스냅샷) 격리 자동 해제.
//  δ=0: 고정 배열 · 계좌 뷰는 락 보유 판독(무할당) · Int128 산술.
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    /// <summary>브로커 1개의 계좌 판독 뷰(δ=0).</summary>
    public interface IBrokerAccountView
    {
        /// <summary>어댑터가 결속(연결·자격증명)되어 있는가.</summary>
        bool Bound { get; }
        /// <summary>RouterFeedGate 의 계좌 예약과 같은 계좌인가(부모 예약분 되돌림 대상).</summary>
        bool SharesGateReservation { get; }
        bool TryGetBuyHeadroomWon(out long won);
        bool TryGetSellableShares(long symbolCode, out long shares);
    }

    /// <summary>AccountStateSyncAdapter(키움 OPW00001/OPW00018) 기반 뷰.</summary>
    public sealed class AccountSyncBrokerView : IBrokerAccountView
    {
        private readonly AccountStateSyncAdapter _account;
        private readonly Func<bool> _bound;
        public AccountSyncBrokerView(AccountStateSyncAdapter account, Func<bool> bound, bool sharesGateReservation = true)
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
            _bound = bound ?? throw new ArgumentNullException(nameof(bound));
            SharesGateReservation = sharesGateReservation;
        }
        public bool Bound { get { try { return _bound(); } catch { return false; } } }
        public bool SharesGateReservation { get; }
        public bool TryGetBuyHeadroomWon(out long won) => _account.TryGetBuyHeadroom(out won);
        public bool TryGetSellableShares(long symbolCode, out long shares) => _account.TryGetSellable(symbolCode, out shares);
    }

    public sealed class BrokerLimitFeedProvider : IBrokerLimitFeed
    {
        private const long QtyScale = 100_000_000L;
        private readonly IBrokerAccountView?[] _views;
        private readonly long[] _perOrderMax;
        private long _isolatedMask, _exhaustions, _unboundSamples, _staleSamples, _fills;
        private int _lastAvailable;

        /// <param name="views">브로커 인덱스 = 배열 인덱스(LockFreeBrokerSwitchboard 와 동일) · null = 미결속.</param>
        /// <param name="perOrderMaxShares">브로커별 주문당 상한(주 · 0/누락 = 무제한).</param>
        public BrokerLimitFeedProvider(IBrokerAccountView?[] views, long[]? perOrderMaxShares = null)
        {
            _views = views ?? throw new ArgumentNullException(nameof(views));
            if (views.Length == 0 || views.Length > 16) throw new ArgumentOutOfRangeException(nameof(views));
            _perOrderMax = new long[views.Length];
            for (int i = 0; i < views.Length; i++) _perOrderMax[i] = perOrderMaxShares is { } m && i < m.Length && m[i] > 0 ? m[i] : long.MaxValue;
        }

        public int Brokers => _views.Length;
        /// <summary>한도 소진으로 격리 중인 브로커 비트(방향 B).</summary>
        public long IsolatedMask => Interlocked.Read(ref _isolatedMask);
        public long Exhaustions => Interlocked.Read(ref _exhaustions);
        public long UnboundSamples => Interlocked.Read(ref _unboundSamples);
        public long StaleSamples => Interlocked.Read(ref _staleSamples);
        public long Fills => Interlocked.Read(ref _fills);
        public int LastAvailable => Volatile.Read(ref _lastAvailable);

        public int Fill(Span<BrokerSplitCap> dst, long symbolCode, OrderSide side, long limitPriceRaw, long parentReservedWon)
        {
            Interlocked.Increment(ref _fills);
            int n = Math.Min(dst.Length, _views.Length), available = 0;
            for (int b = 0; b < n; b++)
            {
                dst[b] = new BrokerSplitCap { Broker = b, MaxShares = 0, Available = false };
                var v = _views[b];
                if (v is null || !v.Bound) { Interlocked.Increment(ref _unboundSamples); continue; }   // ★미결속 = 자식 0
                long shares;
                if (side == OrderSide.Buy)
                {
                    if (limitPriceRaw <= 0 || !v.TryGetBuyHeadroomWon(out long won)) { Interlocked.Increment(ref _staleSamples); continue; }
                    if (v.SharesGateReservation && parentReservedWon > 0) won = won > long.MaxValue - parentReservedWon ? long.MaxValue : won + parentReservedWon;
                    Int128 sh = (Int128)won * QtyScale / limitPriceRaw;
                    shares = sh > long.MaxValue ? long.MaxValue : (long)sh;
                }
                else
                {
                    if (!v.TryGetSellableShares(symbolCode, out shares)) { Interlocked.Increment(ref _staleSamples); continue; }
                }
                shares = Math.Min(shares, _perOrderMax[b]);
                long bit = 1L << b;
                if (shares <= 0)
                {
                    // 방향 B — 결속·신선한데 한도 0 = 소진 → 격리(전이 1회 계수)
                    long prev = Interlocked.Or(ref _isolatedMask, bit);
                    if ((prev & bit) == 0) Interlocked.Increment(ref _exhaustions);
                    continue;
                }
                Interlocked.And(ref _isolatedMask, ~bit);                            // 한도 회복 = 격리 해제
                dst[b] = new BrokerSplitCap { Broker = b, MaxShares = shares, Available = true };
                available++;
            }
            Volatile.Write(ref _lastAvailable, available);
            return n;
        }
    }
}
