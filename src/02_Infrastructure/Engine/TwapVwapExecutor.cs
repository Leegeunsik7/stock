// ============================================================================
// TwapVwapExecutor — S15 #1: 제로-할당 TWAP·VWAP 분할 집행 코어
//
//  · IBroker 미들웨어 — 전략은 TwapVwapExecutor 를 일반 IBroker 로 취급.
//  · Place(qty >= DefaultConfig.MinQty) → 배경 분할 집행 시작, "ALGO-{id}" 반환.
//  · Place(qty <  DefaultConfig.MinQty) → 즉시 inner.Place() 통과.
//  · 슬라이스 수량 배열: ArrayPool<decimal> 임대 → RunAlgoAsync finally 반납.
//  · VWAP 가중치: stackalloc double[] (슬라이스 ≤64) 또는 ArrayPool.
//  · KSE 장 중 U자형 거래량 프로파일 10버킷 (9:00~15:30 근사).
//
//  [알고 집행 하드닝] 교정(수정 전 실측):
//   ① VWAP 오버필 — 슬라이스마다 Math.Max(1, floor(raw)) 로 최소 1주를 강제해, 총수량 < 슬라이스 수(예: 5주/10슬라이스)면
//      슬라이스 합이 총수량을 넘었다(10주 발주 = 모 주문의 2배 체결 위험). → 누적 반올림 배분(Σ = TotalQty 정확 · 음수 0 · 0 슬라이스 허용).
//   ② Cancel 이 "이후 슬라이스"만 멈추고 이미 하위 브로커에 나간 자식 주문(미체결 지정가)은 그대로 방치 → 자식 ID 추적 + 일괄 취소.
//   ③ 슬라이스 대기 종료 직후 도착한 취소를 확인하지 않고 발주 — 취소 뒤 조각 1건이 새어 나감 → 발주 직전 재확인.
//   ④ Slices ≤0(튜너 오출력)·Duration ≤0·qty ≤0 에서 0 나눗셈 예외/음수 간격 → 거부(fail-closed).
//   ⑤ CreateLinkedTokenSource 미처분(핸들 누수) · GATING FREEZE 외 예외 시 State 가 Running 으로 고착 → finally 처분 · Frozen 판정.
//   ※ FilledQty 는 "하위 브로커 접수 수량"이다(IBroker 계약에 체결 통보가 없음) — 실 체결 대사는 ExecutionMatrixTracker(MS2E.Core) 몫.
// ============================================================================
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
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

        /// <summary>S16 #1: 마찰 적응형 튜너. null 이면 DefaultConfig 값 그대로 사용.</summary>
        public AdaptiveExecutionTuner? Tuner { get; set; }

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

        public const int MaxSlices = 10_000;

        public (bool ok, string msg) Place(
            PaperAsset asset, string symbol, string name,
            PaperSide  side,  PaperKind kind,
            decimal    qty,   decimal limitPrice, decimal currentPrice)
        {
            if (qty <= 0m) return (false, "[ALGO] 수량이 0 이하입니다.");   // [④] fail-closed
            // 임계치 미만은 즉시 통과
            if (qty < DefaultConfig.MinQty)
                return _inner.Place(asset, symbol, name, side, kind, qty, limitPrice, currentPrice);

            // S17 #2: 튜너 안전 동결 상태이면 신규 주문 차단
            var tuner = Tuner;
            if (tuner?.IsFrozen == true)
                return (false, "[TUNER-FREEZE] 안전 동결 모드 — 알고 집행 차단");

            // S16 #1: 튜너가 연결된 경우 최신 Z-score 기반 파라미터 보정 후 적용
            tuner?.Calibrate();
            int      effectiveSlices   = tuner?.CurrentSlices   ?? DefaultConfig.Slices;
            TimeSpan effectiveDuration = tuner?.CurrentDuration ?? DefaultConfig.Duration;
            // [④] 튜너/설정 오출력 방어 — 0 나눗셈·음수 간격·과대 분할 대신 거부(발주 0).
            if (effectiveSlices < 1 || effectiveSlices > MaxSlices) return (false, $"[ALGO] 슬라이스 수 비정상({effectiveSlices}) — 집행 거부");
            if (effectiveDuration <= TimeSpan.Zero) return (false, "[ALGO] 집행 시간창 비정상 — 집행 거부");

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
                Duration   = effectiveDuration,
                Slices     = effectiveSlices,
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
                try { pair.Cts.Cancel(); } catch (ObjectDisposedException) { }
                CancelChildren(pair.Exec);   // [②] 이미 나간 자식 주문 일괄 취소(루프의 취소 경로와 멱등 공유)
                return;
            }
            _inner.Cancel(orderId);
        }

        // 모 주문별 발주된 자식 주문 ID(하위 브로커 반환값). 취소 시 전량 inner.Cancel.
        private readonly ConcurrentDictionary<long, ConcurrentQueue<string>> _children = new();

        private void CancelChildren(AlgoExecution exec)
        {
            if (!_children.TryGetValue(exec.AlgoId, out var q)) return;
            while (q.TryDequeue(out var childId))
            {
                try { _inner.Cancel(childId); } catch { /* 하위 취소 실패가 나머지 취소를 막지 않음 */ }
            }
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
                    ct.ThrowIfCancellationRequested();   // [③] 대기 종료 직후 도착한 취소 — 이 조각은 발주하지 않음

                    decimal qty = rented[i];
                    if (qty <= 0m) continue;

                    try
                    {
                        var (ok, childId) = _inner.Place(
                            exec.Order.Asset, exec.Order.Symbol, exec.Order.Name,
                            exec.Order.Side,  exec.Order.Kind,
                            qty,              exec.Order.LimitPrice, 0m);

                        if (ok)
                        {
                            if (!string.IsNullOrEmpty(childId)) _children.GetOrAdd(exec.AlgoId, static _ => new ConcurrentQueue<string>()).Enqueue(childId);
                            exec.ChildrenFired++;
                            exec.FilledQty += qty;   // 접수 수량(체결 통보 없는 IBroker 계약) — 헤더 주석 참조
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

                // [②] 마지막 조각 발주와 취소가 경합해 루프가 정상 종료된 경우 — 그 사이 큐에 들어간 자식도 취소
                ct.ThrowIfCancellationRequested();
                if (exec.State == AlgoState.Running)
                    exec.State = AlgoState.Completed;
            }
            catch (OperationCanceledException)
            {
                exec.State = AlgoState.Cancelled;
                CancelChildren(exec);   // [②] 이미 나간 미체결 조각 원자 일괄 취소
            }
            catch (Exception)
            {
                exec.State = AlgoState.Frozen;   // [⑤] 미상 하위 예외 — Running 고착 대신 동결(이후 조각 0) + 자식 취소
                CancelChildren(exec);
            }
            finally
            {
                ArrayPool<decimal>.Shared.Return(rented, clearArray: true);
                if (_active.TryRemove(exec.AlgoId, out var pair)) pair.Cts.Dispose();   // [⑤] 링크 CTS 처분
                _children.TryRemove(exec.AlgoId, out _);
                AlgoCompleted?.Invoke(exec);
            }
        }

        // ── TWAP 슬라이스 계산 ────────────────────────────────────────────────
        // 균등 분할: floor(TotalQty / N) × (N-1) + 나머지를 마지막 슬라이스에 집중.

        public static void ComputeTwap(AlgoOrder order, Span<decimal> qtys)
        {
            int     n        = order.Slices;
            if (n < 1) throw new ArgumentOutOfRangeException(nameof(order), "Slices ≥ 1");
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
            if (n < 1) throw new ArgumentOutOfRangeException(nameof(order), "Slices ≥ 1");

            double[]? rented = null;
            Span<double> weights = n <= StackAllocMax
                ? stackalloc double[n]
                : (rented = ArrayPool<double>.Shared.Rent(n)).AsSpan(0, n);

            try
            {
                InterpolateIntradayWeights(weights);

                // [①] 누적 반올림 배분 — target_i = floor(Total × Σw_0..i), slice_i = target_i − target_{i−1}.
                //   누적 목표가 단조 증가하므로 슬라이스는 음수가 될 수 없고, 마지막 목표 = Total 이라 Σslice = Total 정확(오버필 0).
                //   거래량이 적은 구간은 0 슬라이스(실행 루프가 건너뜀) — 최소 1주 강제로 총량을 넘기던 결함 제거.
                decimal total = order.TotalQty, prevTarget = 0m;
                double cum = 0.0;
                for (int i = 0; i < n; i++)
                {
                    cum += weights[i];
                    decimal target = i == n - 1 ? total : Math.Min(total, Math.Floor(total * (decimal)Math.Min(cum, 1.0)));
                    if (target < prevTarget) target = prevTarget;
                    qtys[i] = target - prevTarget;
                    prevTarget = target;
                }
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
