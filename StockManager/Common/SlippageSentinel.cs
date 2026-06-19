// ============================================================================
// SlippageSentinel — S15 #3: 슬리피지 편차 경보기
//   S16 #1 확장: LastZ — Interlocked int64 비트 저장으로 FrictionFeedbackBridge 노출
//
//  · Welford 온라인 알고리즘으로 실시간 평균·표준편차 갱신 (GC 0).
//  · 순환 윈도우(최근 WindowSize 건)로 이상치 추적.
//  · |Z| > AlertThresholdSigma 가 ConsecutiveAlertCount 회 연속 → AlertTriggered 이벤트.
//  · WebhookUrl 설정 시 Discord/Telegram 포맷 웹훅 POST (화재-망각).
//  · 시장가(expectedPrice=0) 건은 측정 대상 제외.
//  · LastZ: Interlocked.Exchange(long bits) 로 스레드 세이프 노출.
// ============================================================================
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Common
{
    public sealed class SlippageSentinel
    {
        public double  AlertThresholdSigma   { get; init; } = 3.0;
        public int     ConsecutiveAlertCount { get; init; } = 3;
        public int     WindowSize            { get; init; } = 10;
        public string? WebhookUrl            { get; init; }

        // Welford 온라인 통계
        private double _mean;
        private double _m2;   // Σ(x - mean)² 누적 (표본 분산: _m2 / (n-1))
        private int    _n;

        // 최근 WindowSize 잔차 순환 버퍼
        private readonly double[] _window;
        private int _head;
        private int _filledCount;

        // 연속 초과 카운터
        private int _consecutive;

        // S16 #1: FrictionFeedbackBridge 노출용 최근 Z-score (Interlocked int64 비트 저장)
        private long _lastZBits;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

        /// <summary>AlertThresholdSigma 초과가 ConsecutiveAlertCount 회 연속 시 발행.</summary>
        public event Action<SlippageAlert>? AlertTriggered;

        public int    SampleCount  => _n;
        public double RunningMean  => _mean;
        public double RunningStdDev
            => _n >= 2 ? Math.Sqrt(_m2 / (_n - 1)) : 0.0;

        /// <summary>직전 체결의 절대 Z-score. Interlocked 원자 읽기 보장 (FrictionFeedbackBridge 전용).</summary>
        public double LastZ
            => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastZBits));

        public SlippageSentinel()
            => _window = new double[Math.Max(WindowSize, 1)];

        // ── 체결 기록 및 Z-score 검사 ────────────────────────────────────────

        public void RecordFill(FillPayload fill, double expectedPrice)
        {
            // 시장가(expectedPrice=0)는 슬리피지 기준가 없음 → 측정 제외
            if (expectedPrice <= 0.0) return;

            double residual = (fill.FillPrice - expectedPrice) / expectedPrice;

            // Welford 업데이트 (GC 0, 수치 안정)
            _n++;
            double delta = residual - _mean;
            _mean       += delta / _n;
            _m2         += delta * (residual - _mean);

            // 순환 버퍼 업데이트
            _window[_head] = residual;
            _head          = (_head + 1) % _window.Length;
            if (_filledCount < _window.Length) _filledCount++;

            // 분산 수렴 전(초기 4건 미만)은 경보 판단 보류
            double sigma = RunningStdDev;
            if (sigma < 1e-12 || _n < 4) return;

            double z = Math.Abs(residual - _mean) / sigma;
            Interlocked.Exchange(ref _lastZBits, BitConverter.DoubleToInt64Bits(z));

            if (z > AlertThresholdSigma)
            {
                _consecutive++;
                if (_consecutive >= ConsecutiveAlertCount)
                {
                    var alert = new SlippageAlert(
                        BridgeSerializer.FormatSymbol(fill.SymbolCode),
                        Math.Round(z,     4),
                        Math.Round(_mean, 6),
                        Math.Round(sigma, 6),
                        _consecutive,
                        DateTime.UtcNow);

                    AlertTriggered?.Invoke(alert);
                    _ = PostWebhookAsync(alert);
                }
            }
            else
            {
                _consecutive = 0;
            }
        }

        // ── 최근 잔차 스냅샷 (모니터링 UI 용) ───────────────────────────────

        public double[] GetWindowSnapshot()
        {
            var snap = new double[_filledCount];
            for (int i = 0; i < _filledCount; i++)
            {
                int idx = (_head - _filledCount + i + _window.Length) % _window.Length;
                snap[i] = _window[idx];
            }
            return snap;
        }

        // ── 웹훅 전송 (Discord / Telegram 자동 감지) ─────────────────────────

        private async Task PostWebhookAsync(SlippageAlert a)
        {
            if (string.IsNullOrEmpty(WebhookUrl)) return;

            string msg =
                $"🚨 슬리피지 편차 경고 | {a.Symbol} | " +
                $"Z={a.ZScore:F2}σ ({a.ConsecutiveExceedances}회 연속) | " +
                $"μ={a.Mean:+0.0000;-0.0000} σ={a.Sigma:0.0000} | " +
                $"{a.OccurredAt:HH:mm:ss} UTC";

            try
            {
                string json = WebhookUrl.Contains("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase)
                    ? JsonSerializer.Serialize(new { content = msg })
                    : JsonSerializer.Serialize(new { text    = msg });

                await _http.PostAsync(WebhookUrl,
                    new StringContent(json, Encoding.UTF8, "application/json"));
            }
            catch
            {
                // 웹훅 실패는 거래 엔진을 멈추지 않음 (화재-망각)
            }
        }
    }
}
