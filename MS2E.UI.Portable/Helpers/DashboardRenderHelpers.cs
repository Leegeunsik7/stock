// ============================================================================
// DashboardRenderHelpers — [대시보드 UX · Think Beyond]
//
//  [방향 A] SparklineWriter — 확신도 이력(bp) → SVG polyline points 문자를 '호출측 고정 버퍼'(Span&lt;char&gt;)에 정수 좌표로 작성(할당 0).
//    JS Interop 없음. 뷰는 프레임 버전이 바뀔 때만 버퍼 → 문자열 1회 생성(DOM 속성은 string 필요 — 물리 한계 · 정직) · 같은 버전 재렌더 = 재사용.
//  [방향 B] DashboardDegradationGovernor — 스냅샷 적용 지연(허브 조립 시각 → 브라우저 렌더 완료) EWMA 를 관측해 지속 지연(기본 250ms ·
//    연속 3회)이면 열화 모드(차트 · 스파크라인 · 점멸 애니메이션 끔 → 핵심 텍스트만), 회복(100ms 미만 연속 10회)이면 복귀(히스테리시스 —
//    경계에서 깜빡임 방지). 순수 계산(시계 주입) — 매매 파이프라인과 무관(UI 스레드 전용).
// ============================================================================
using System;

namespace MS2E.UI.Portable.Helpers
{
    public static class SparklineWriter
    {
        /// <summary>
        /// 값(oldest → newest · 0~10,000 bp) → "x,y x,y …"(정수 좌표 · 위 = 높음). 반환 = 작성 문자 수(0 = 값 &lt; 2 또는 버퍼 부족).
        /// width/height = viewBox 크기. 값은 0~10,000 으로 클램프(고정 축 — 0.85 게이트 선과 같은 척도).
        /// </summary>
        public static int Write(ReadOnlySpan<long> values, Span<char> dst, int width, int height)
        {
            if (values.Length < 2 || width <= 0 || height <= 0) return 0;
            int pos = 0;
            for (int i = 0; i < values.Length; i++)
            {
                long x = (long)i * width / (values.Length - 1);
                long v = Math.Clamp(values[i], 0, 10_000);
                long y = height - v * height / 10_000;
                if (i > 0) { if (pos >= dst.Length) return 0; dst[pos++] = ' '; }
                if (!x.TryFormat(dst[pos..], out int a, provider: System.Globalization.CultureInfo.InvariantCulture)) return 0;
                pos += a;
                if (pos >= dst.Length) return 0;
                dst[pos++] = ',';
                if (!y.TryFormat(dst[pos..], out int b, provider: System.Globalization.CultureInfo.InvariantCulture)) return 0;
                pos += b;
            }
            return pos;
        }

        /// <summary>게이트 선 y 좌표(정수).</summary>
        public static int GateY(int height, long gateBp = SignalLabelMapper.GateBp) => (int)(height - gateBp * height / 10_000);

        /// <summary>
        /// [v12.17.5-DEV] 지연(ns · oldest → newest) → "x,y …"(정수 좌표 · 위 = 느림). 세로축 = 지연 버킷 인덱스(로그 축 — 1µs 와 10ms 를 한 화면에) —
        /// 버킷 → 0~10,000 척도로 사상한 뒤 Write 와 같은 작성기를 쓴다(고정 버퍼 · 할당 0). 반환 = 작성 문자 수(0 = 값 &lt; 2 또는 버퍼 부족).
        /// </summary>
        public static int WriteLatency(ReadOnlySpan<long> nanos, Span<long> scratch, Span<char> dst, int width, int height)
        {
            if (nanos.Length < 2 || scratch.Length < nanos.Length) return 0;
            for (int i = 0; i < nanos.Length; i++) scratch[i] = LatencyScale(nanos[i]);
            return Write(scratch[..nanos.Length], dst, width, height);
        }

        /// <summary>지연(ns) → 0~10,000 세로 척도(버킷 인덱스 비례).</summary>
        public static long LatencyScale(long nanos) => (long)SignalLabelMapper.LatencyBucket(nanos) * 10_000 / (SignalLabelMapper.LatencyBuckets - 1);

        /// <summary>지연 기준선(기본 1ms = 미세 정체 경계) y 좌표(정수).</summary>
        public static int LatencyY(int height, long nanos = 1_000_000) => (int)(height - LatencyScale(nanos) * height / 10_000);
    }

    public sealed class DashboardDegradationGovernor
    {
        public const double DefaultDegradeMs = 250, DefaultRecoverMs = 100;
        public const int DefaultDegradeStreak = 3, DefaultRecoverStreak = 10;
        private readonly double _degradeMs, _recoverMs, _alpha;
        private readonly int _degradeStreak, _recoverStreak;
        private double _ewma = -1;
        private int _bad, _good;
        private long _degradations, _recoveries, _samples;

        public DashboardDegradationGovernor(double degradeMs = DefaultDegradeMs, double recoverMs = DefaultRecoverMs,
                                            int degradeStreak = DefaultDegradeStreak, int recoverStreak = DefaultRecoverStreak, double alpha = 0.5)
        {
            if (!(recoverMs > 0 && degradeMs > recoverMs)) throw new ArgumentOutOfRangeException(nameof(degradeMs), "열화 임계 > 회복 임계 > 0");
            if (degradeStreak < 1 || recoverStreak < 1) throw new ArgumentOutOfRangeException(nameof(degradeStreak));
            if (!(alpha > 0 && alpha <= 1)) throw new ArgumentOutOfRangeException(nameof(alpha));
            _degradeMs = degradeMs; _recoverMs = recoverMs; _degradeStreak = degradeStreak; _recoverStreak = recoverStreak; _alpha = alpha;
        }

        public bool Degraded { get; private set; }
        public double LagEwmaMs => _ewma < 0 ? 0 : _ewma;
        public long Degradations => _degradations;
        public long Recoveries => _recoveries;
        public long Samples => _samples;

        /// <summary>지연 1표본(ms · 음수/NaN = 무시). 반환 = 이번 표본으로 모드가 바뀌었는가.</summary>
        public bool Observe(double lagMs)
        {
            if (!double.IsFinite(lagMs) || lagMs < 0) return false;
            _samples++;
            _ewma = _ewma < 0 ? lagMs : _alpha * lagMs + (1 - _alpha) * _ewma;
            if (!Degraded)
            {
                _bad = _ewma > _degradeMs ? _bad + 1 : 0;
                if (_bad >= _degradeStreak) { Degraded = true; _bad = 0; _good = 0; _degradations++; return true; }
            }
            else
            {
                _good = _ewma < _recoverMs ? _good + 1 : 0;
                if (_good >= _recoverStreak) { Degraded = false; _good = 0; _bad = 0; _recoveries++; return true; }
            }
            return false;
        }
    }
}
