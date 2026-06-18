// ============================================================================
// TwapVwapExecutor — S15 #1: 제로-할당 TWAP·VWAP 분할 집행 코어
//
//  · IBroker 미들웨어 — 전략은 TwapVwapExecutor 를 일반 IBroker 로 취급.
//  · Place(qty >= DefaultConfig.MinQty) → 배경 분할 집행 시작, "ALGO-{id}" 반환.
//  · Place(qty <  DefaultConfig.MinQty) → 즉시 inner.Place() 통과.
//  · 슬라이스 수량 배열: ArrayPool<decimal> 임대 → RunAlgoAsync finally 반납.
//  · VWAP 가중치: stackalloc double[] (슬라이스 ≤64) 또는 ArrayPool.
//  · KSE 장 중 U자형 거래량 프로파일 10버킷 (9:00~15:30 근사).
// ============================================================================
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Common
{
    public sealed class TwapVwapExecutor : IBroker, IDisposable
    {
        // KSE 10버킷 장 중 거래량 프로파일 (정규화 합계 = 1.0)
        // 9:00(개장) → 9:30 → 10:30 → 11:30 → 12:30 → 13:30 → 14:30 → 15:30(마감) 구간 대표
        private static readonly double[] IntradayProfile =
        {
            0.151, 0.121, 0.091, 0.081, 0.070,
            0.070, 0.080, 0.100, 0.120, 0.116
        };

        private const int StackAllocMax = 64;

        private readonly IBroker           _inner;
        private readonly LinearQuoteCache? _cache;
        private readonly Func<int, CancellationToken, Task> _delay;

        private readonly CancellationTokenSource _masterCts = new();
        private readonly ConcurrentDictionary<long, (AlgoExecution Exec, CancellationTokenSource Cts)> _active = new();
        private long _nextAlgoId;

        /// <summary>분할 집행 전략 설정 (최소 수량·슬라이스 수·시간창 등).</summary>
        public AlgoConfig DefaultConfig { get; init; } = new AlgoConfig();

        /// <summary>각 슬라이스 발사 직후 발행 (UI 실시간 진행 표시용).</summary>
        public event Action<AlgoExecution>? SliceFired;
        /// <summary>알고리즘 집행 완료·취소·동결 시 발행.</summary>
        public event Action<AlgoExecution>? AlgoCompleted;

        public string Name   => $"AlgoLayer({_inner.Name})";
        public bool   IsLive => _inner.IsLive;

        public IReadOnlyList<AlgoExecution> ActiveExecutions
            => _active.Values.Select(v => v.Exec).ToArray();

        /// <param name="broker">실제 주문을 처리할 하위 브로커.</param>
        /// <param name="cache">VWAP 거래량 참고용 캐시 (null = 인트라데이 프로파일만 사용).</param>
        /// <param name="delayFactory">슬라이스 간격 대기 팩토리 (테스트 주입용).</param>
        public TwapVwapExecutor(
            IBroker                             broker,
            LinearQuoteCache?                   cache         = null,
            Func<int, CancellationToken, Task>? delayFactory  = null)
        {
            _inner = broker ?? throw new ArgumentNullException(nameof(broker));
            _cache = cache;
            _delay = delayFactory ?? ((ms, ct) => Task.Delay(ms, ct));
        }

        // ── IBroker.Place ─────────────────────────────────────────────────────

        public (bool ok, string msg) Place(
            PaperAsset asset, string symbol, string name,
            PaperSide  side,  PaperKind kind,
            decimal    qty,   decimal limitPrice, decimal currentPrice)
        {
            // 임계치 미만은 즉시 통과
            if (qty < DefaultConfig.MinQty)
                return _inner.Place(asset, symbol, name, side, kind, qty, limitPrice, currentPrice);

            long algoId = Interlocked.Increment(ref _nextAlgoId);

            var order = new AlgoOrder
            {
                AlgoId     = algoId,
                Symbol     = symbol,
                Name       = name,
                Asset      = asset,
                Side       = side,
                Kind       = kind,
                TotalQty   = qty,
                LimitPrice = limitPrice,
                Style      = DefaultConfig.Style,
                Duration   = DefaultConfig.Duration,
                Slices     = DefaultConfig.Slices,
            };

            var exec = new AlgoExecution
            {
                AlgoId    = algoId,
                Order     = order,
                StartedAt = DateTime.UtcNow,
            };

            // 슬라이스 수량 배열 임대 (RunAlgoAsync 에서 반납)
            decimal[] rented = ArrayPool<decimal>.Shared.Rent(order.Slices);
            if (order.Style == AlgoStyle.Twap)
                ComputeTwap(order, rented.AsSpan(0, order.Slices));
            else
                ComputeVwap(order, rented.AsSpan(0, order.Slices));

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_masterCts.Token);
            _active[algoId] = (exec, cts);
            _ = RunAlgoAsync(exec, rented, cts.Token);

            return (true, $"ALGO-{algoId}");
        }

        public void Cancel(string orderId)
        {
            if (orderId.StartsWith("ALGO-", StringComparison.Ordinal)
                && long.TryParse(orderId.AsSpan(5), out long id)
                && _active.TryGetValue(id, out var pair))
            {
                pair.Cts.Cancel();
                return;
            }
            _inner.Cancel(orderId);
        }

        public decimal HeldQty(PaperAsset asset, string symbol)
            => _inner.HeldQty(asset, symbol);

        // ── 분할 집행 루프 ────────────────────────────────────────────────────

        private async Task RunAlgoAsync(AlgoExecution exec, decimal[] rented, CancellationToken ct)
        {
            int  slices     = exec.Order.Slices;
            int  intervalMs = Math.Max(1, (int)(exec.Order.Duration.TotalMilliseconds / slices));
            bool frozen     = false;

            exec.State = AlgoState.Running;

            try
            {
                for (int i = 0; i < slices && !ct.IsCancellationRequested && !frozen; i++)
                {
                    await _delay(intervalMs, ct);

                    decimal qty = rented[i];
                    if (qty <= 0m) continue;

                    try
                    {
                        var (ok, _) = _inner.Place(
                            exec.Order.Asset, exec.Order.Symbol, exec.Order.Name,
                            exec.Order.Side,  exec.Order.Kind,
                            qty,              exec.Order.LimitPrice, 0m);

                        if (ok)
                        {
                            exec.ChildrenFired++;
                            exec.FilledQty += qty;
                            SliceFired?.Invoke(exec);
                        }
                    }
                    catch (InvalidOperationException ex)
                        when (ex.Message.StartsWith("[GATING FREEZE]", StringComparison.Ordinal))
                    {
                        exec.State = AlgoState.Frozen;
                        frozen     = true;
                    }
                }

                if (exec.State == AlgoState.Running)
                    exec.State = AlgoState.Completed;
            }
            catch (OperationCanceledException)
            {
                exec.State = AlgoState.Cancelled;
            }
            finally
            {
                ArrayPool<decimal>.Shared.Return(rented, clearArray: true);
                _active.TryRemove(exec.AlgoId, out _);
                AlgoCompleted?.Invoke(exec);
            }
        }

        // ── TWAP 슬라이스 계산 ────────────────────────────────────────────────
        // 균등 분할: floor(TotalQty / N) × (N-1) + 나머지를 마지막 슬라이스에 집중.

        public static void ComputeTwap(AlgoOrder order, Span<decimal> qtys)
        {
            int     n        = order.Slices;
            decimal perSlice = Math.Floor(order.TotalQty / n);
            decimal rem      = order.TotalQty - perSlice * n;

            for (int i = 0; i < n - 1; i++) qtys[i] = perSlice;
            qtys[n - 1] = perSlice + rem;
        }

        // ── VWAP 슬라이스 계산 ────────────────────────────────────────────────
        // KSE 장 중 U자형 거래량 가중치로 슬라이스 배분. 스택 할당으로 힙 압력 0.

        public static void ComputeVwap(AlgoOrder order, Span<decimal> qtys)
        {
            int n = order.Slices;

            double[]? rented = null;
            Span<double> weights = n <= StackAllocMax
                ? stackalloc double[n]
                : (rented = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);

            try
            {
                InterpolateIntradayWeights(weights);

                decimal remaining = order.TotalQty;
                for (int i = 0; i < n - 1; i++)
                {
                    decimal raw = order.TotalQty * (decimal)weights[i];
                    qtys[i]    = Math.Max(1m, Math.Floor(raw));
                    remaining -= qtys[i];
                }
                qtys[n - 1] = Math.Max(1m, remaining);
            }
            finally
            {
                if (rented != null) ArrayPool<double>.Shared.Return(rented);
            }
        }

        // 10버킷 인트라데이 프로파일을 임의 N 슬라이스로 선형 보간 후 정규화.
        private static void InterpolateIntradayWeights(Span<double> weights)
        {
            int    n   = weights.Length;
            int    m   = IntradayProfile.Length;
            double sum = 0.0;

            for (int i = 0; i < n; i++)
            {
                double t    = n == 1 ? 0.5 : (double)i / (n - 1);
                double pos  = t * (m - 1);
                int    lo   = (int)pos;
                int    hi   = Math.Min(lo + 1, m - 1);
                double frac = pos - lo;
                weights[i]  = IntradayProfile[lo] * (1.0 - frac) + IntradayProfile[hi] * frac;
                sum        += weights[i];
            }

            // 합이 1이 되도록 정규화 (보간 오차 보정)
            if (sum > 0) for (int i = 0; i < n; i++) weights[i] /= sum;
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _masterCts.Cancel();
            _masterCts.Dispose();
        }
    }
}
