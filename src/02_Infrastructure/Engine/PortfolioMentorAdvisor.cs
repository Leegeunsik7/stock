// ============================================================================
// PortfolioMentorAdvisor — v4.93.0: AI 멘토 조언(결정론 룰기반) + 뉴스 인텔 관제 + Native SVG 마이크로차트
//
//  · NewsIntelController  — 뉴스 링을 ★백그라운드 스레드에서 드레인(50ms 발주 핫패스와 완전 격리·무콘텐션)하며
//                           롤링 인입속도·시장 감성 EMA·미분석 격리·세맨틱 쇼크 마스크·감성 브레이커를 집계(라이브 텔레메트리).
//  · PortfolioMentorAdvisor — 포트폴리오+시장 감성+쇼크로 한국어 조언 카드를 '결정론 룰'로 생성(LLM AiService 는 옵트인).
//                           ★liveArmed:false: 조언은 텍스트일 뿐 — 아무리 공격적이어도 실주문 경로 무접촉.
//  · NewsRadarSvg         — 외부 JS 의존 0. Blazor Native SVG 미니 선형차트 + 뉴스 닷(호재 빨강/악재 파랑).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>AI 멘토 파이프라인 FSM.</summary>
    public enum MentorFsmState : byte { Idle = 0, Analyzing = 1, Advised = 2, Amber = 3 }

    /// <summary>
    /// 뉴스 인텔 통합 컨트롤러(DI 싱글톤·라이브 텔레메트리). 뉴스 링을 전용 백그라운드 스레드로 드레인해
    /// 핫패스 발주 드레인(50ms)과 스레드 격리한다(자연어 폭주가 발주에 지터 0). 집계는 δ=0 정수/EMA.
    /// </summary>
    public sealed class NewsIntelController : IDisposable
    {
        private readonly NewsDispatchRing           _ring;
        private readonly SentimentVelocityBreaker   _breaker;
        private readonly Func<long>                 _nowTicks;
        private readonly MmfFlightRecorder?         _blackbox;   // v4.95.0 — 블랙박스(δ=0·null=no-op)

        private long _marketSentimentRaw;   // 시장 감성 EMA(QFixed)
        private long _unanalyzed;           // 미분석 격리 누적
        private long _shockMask;            // 세맨틱 쇼크 분류 비트마스크
        private long _ingested;             // 분석 완료 인입 누적
        private int  _mentorFsm;

        // 뉴스 인입속도(개/분) — 도착 tick 64슬롯 링(δ=0). 읽을 때 최근 60s 창 카운트.
        private const int RateCap = 64;
        private readonly long[] _arrival = new long[RateCap];
        private int _arrIdx;

        private Thread?       _drainThread;
        private volatile bool _running;

        public NewsIntelController(SentimentVelocityBreaker breaker, NewsDispatchRing? ring = null, Func<long>? nowTicks = null,
                                   MmfFlightRecorder? blackbox = null)
        {
            _breaker  = breaker ?? throw new ArgumentNullException(nameof(breaker));
            _ring     = ring ?? new NewsDispatchRing();
            _nowTicks = nowTicks ?? (() => DateTime.UtcNow.Ticks);
            _blackbox = blackbox;
        }

        public NewsDispatchRing Ring => _ring;

        // ── 라이브 텔레메트리(락-프리) ─────────────────────────────────────────
        public long MarketSentimentRaw     => Interlocked.Read(ref _marketSentimentRaw);
        public long NewsUnanalyzedCount    => Interlocked.Read(ref _unanalyzed);
        public long SemanticShockActive    => Interlocked.Read(ref _shockMask);
        public long SentimentBreakerTripped => _breaker.IsTripped ? 1L : 0L;
        public long MentorFsmCode          => _breaker.IsTripped ? (long)MentorFsmState.Amber : Volatile.Read(ref _mentorFsm);
        public long NewsRatePerMin
        {
            get
            {
                long now = _nowTicks(); long cutoff = now - TimeSpan.TicksPerMinute; int c = 0;
                for (int i = 0; i < RateCap; i++) { long t = Volatile.Read(ref _arrival[i]); if (t > 0 && t >= cutoff && t <= now) c++; }
                return c;
            }
        }

        public void SetMentorFsm(MentorFsmState s) => Volatile.Write(ref _mentorFsm, (int)s);
        public void RegisterShock(NewsShockClass c) { if (c != NewsShockClass.None) Interlocked.Or(ref _shockMask, 1L << (int)c); }

        /// <summary>단일 뉴스 페이로드 집계(드레인 스레드 또는 직접 호출). ★미분석(SentimentRaw==0)은 격리 — 다운스트림 미진입.</summary>
        public void Ingest(in NewsPayload p)
        {
            // 도착 속도 기록(δ=0 링).
            Volatile.Write(ref _arrival[_arrIdx], p.PublishedTicks > 0 ? p.PublishedTicks : _nowTicks());
            _arrIdx = (_arrIdx + 1) % RateCap;

            if (p.ShockClass != NewsShockClass.None) Interlocked.Or(ref _shockMask, 1L << (int)p.ShockClass);

            if (p.IsUnanalyzed) { Interlocked.Increment(ref _unanalyzed); return; }   // ★미분석 격리(오작동 차단)

            _blackbox?.Record(FlightEventKind.NewsIngest, p.SymbolCode, p.SentimentRaw, (long)p.ShockClass, p.SeverityRaw, 4);
            if (_breaker.IsTripped) _blackbox?.Record(FlightEventKind.NewsBreakerTrip, p.SymbolCode, p.SentimentRaw, 0L, 0L, 4);

            // 시장 감성 EMA(α=0.2·정수). ★첫 표본 판별은 _ingested 카운트로 — 0을 '표본 없음' 센티널로 재사용하면
            //   정당한 중립 EMA(0)(예: prev=-2,cur=+8 → 0)와 충돌해 이력을 폐기·단일 표본 리셋하므로 금지.
            long count = Interlocked.Increment(ref _ingested);
            long prev  = Interlocked.Read(ref _marketSentimentRaw);
            long ema   = count == 1 ? p.SentimentRaw : (prev * 8 + p.SentimentRaw * 2) / 10;
            Interlocked.Exchange(ref _marketSentimentRaw, ema);
            // ★감성 속도 브레이커 급전(하향 3σ → IgnitionLock.Downgrade). 단일 라이터(드레인 스레드) 전제.
            _breaker.RecordAndCheck(p.SentimentRaw);
        }

        // ── 백그라운드 오프로드 드레인(핫패스 격리) ───────────────────────────
        public void Start()
        {
            if (_running) return;
            _running = true;
            _drainThread = new Thread(DrainLoop) { IsBackground = true, Name = "NewsIntelDrain", Priority = ThreadPriority.BelowNormal };
            _drainThread.Start();
        }
        public void Stop()
        {
            _running = false;
            var t = _drainThread; if (t is not null && t.IsAlive) t.Join(2000); _drainThread = null;
        }
        private void DrainLoop()
        {
            while (_running)
            {
                int n = 0;
                while (_ring.TryRead(out var p)) { Ingest(in p); n++; }
                if (n == 0) Thread.Sleep(5);   // 콜드 대기 — 발주 핫패스 무영향(BelowNormal)
            }
            while (_ring.TryRead(out var p)) Ingest(in p);   // 종료 전 플러시
        }
        public void Dispose() => Stop();
    }

    /// <summary>멘토 조언 입력(순수·결정론). 포트폴리오 스냅샷 + 시장 감성 + 쇼크/브레이커 상태.</summary>
    public readonly record struct MentorInput(
        decimal TotalKrw, decimal PnlKrw, decimal ReturnPct, AssetClass TopClass, decimal TopWeightPct,
        decimal MarketSentiment, bool SemanticShockActive, bool SentimentBreakerTripped);

    /// <summary>
    /// ★결정론 룰기반 AI 멘토 조언 — 한국어 카드 텍스트 생성. LLM(AiService·비결정론)은 옵트인 별도.
    /// ★liveArmed:false: 순수 텍스트 산출 — 매수/매도 발화가 아무리 공격적이어도 실주문 경로에 1바이트도 미치지 않는다.
    /// </summary>
    public static class PortfolioMentorAdvisor
    {
        public static string Advise(in MentorInput m)
        {
            var sb = new StringBuilder(256);
            // 1) 최우선: 정보 서킷 브레이커 트립(FSM 강등).
            if (m.SentimentBreakerTripped)
                sb.Append("⚠️ 정보 서킷 브레이커 작동 — 감성 3σ 하향 폭주로 발주 무장이 자동 강등되었습니다. 신규 진입을 보류하고 뉴스 진위를 먼저 확인하세요. ");
            // 2) 세맨틱 쇼크 전파.
            if (m.SemanticShockActive)
                sb.Append("거시/공급망 쇼크가 감지되어 관련 자산군의 스트레스 가치가 하향 반영되었습니다. 집중 자산군의 방어막을 점검하세요. ");
            // 3) 시장 감성 방향(한국색상 관례 — 호재 빨강/악재 파랑).
            if (m.MarketSentiment <= -0.3m)
                sb.Append($"시장 감성이 악재(파랑) 우위({m.MarketSentiment:+0.00;-0.00})입니다. 방어적 관점에서 현금 비중과 손절 라인을 재점검하세요. ");
            else if (m.MarketSentiment >= 0.3m)
                sb.Append($"시장 감성이 호재(빨강) 우위({m.MarketSentiment:+0.00;-0.00})입니다. 단, 과열·추격 매수 주의 — 분할 접근을 권합니다. ");
            else
                sb.Append("시장 감성은 중립 구간입니다. 원칙 기반 운용을 유지하세요. ");
            // 4) 집중도 경고.
            if (m.TopWeightPct >= 50m)
                sb.Append($"{ClassLabel(m.TopClass)} 비중이 {m.TopWeightPct:0.0}%로 과도합니다 — 분산으로 최악낙폭을 낮추세요. ");
            // 5) 수익률 코멘트(한국색상: 이익 빨강/손실 파랑은 UI 색이 표현; 텍스트는 사실). 포트폴리오 미주입 시 생략.
            if (m.TotalKrw > 0m)
                sb.Append(m.ReturnPct >= 0m
                    ? $"현재 평가수익률 {m.ReturnPct:+0.0}% (평가손익 {m.PnlKrw:+#,##0;-#,##0}원)."
                    : $"현재 평가수익률 {m.ReturnPct:0.0}% (평가손익 {m.PnlKrw:+#,##0;-#,##0}원) — 손실 구간, 감정적 대응을 경계하세요.");
            sb.Append(" ※ 본 조언은 참고용이며, 실주문은 지휘관의 이중 키(ValveWired∧LiveArmingWired) 해제 전까지 실행되지 않습니다.");
            return sb.ToString();
        }

        private static string ClassLabel(AssetClass c) => c switch
        {
            AssetClass.DomesticStock => "국내주식",
            AssetClass.ForeignStock  => "해외주식",
            AssetClass.Crypto        => "코인",
            _ => "기타",
        };
    }

    /// <summary>
    /// Blazor Native SVG 마이크로차트 — 외부 JS 의존 0. 시계열 선 + 뉴스 닷(호재 빨강/악재 파랑) 인과 마킹.
    /// 순수 문자열 생성(UI 계층·핫패스 아님). InvariantCulture 좌표(로캘 무관).
    /// </summary>
    public static class NewsRadarSvg
    {
        /// <summary>뉴스 닷 — 시계열 인덱스 위치 + 호재/악재.</summary>
        public readonly record struct NewsDot(int Index, bool Bullish);

        /// <summary>미니 선형차트 SVG 마크업 생성. series 는 종가 시계열, dots 는 뉴스 포획 시점 마킹.</summary>
        public static string Build(ReadOnlySpan<decimal> series, IReadOnlyList<NewsDot>? dots = null, int w = 120, int h = 30)
        {
            if (series.Length < 2) return $"<svg viewBox='0 0 {w} {h}' xmlns='http://www.w3.org/2000/svg' style='width:{w}px;height:{h}px'></svg>";
            decimal min = series[0], max = series[0];
            for (int i = 1; i < series.Length; i++) { if (series[i] < min) min = series[i]; if (series[i] > max) max = series[i]; }
            decimal range = max - min; if (range <= 0m) range = 1m;
            var ci = CultureInfo.InvariantCulture;
            int len = series.Length;   // 값 캡처(ref-struct series 를 로컬 함수에서 직접 캡처 불가)

            double X(int i) => (double)i / (len - 1) * (w - 4) + 2;
            double Y(decimal v) => h - 2 - (double)((v - min) / range) * (h - 4);

            // ★한국색상 하드코딩 — app.css --up/--down 은 서구식(--up 초록/--down 빨강)이므로 사용 금지.
            //   인접 KPI(TelemetryMonitor msColor)·범례(🔴 호재/🔵 악재)와 정합: 상승·호재=빨강, 하락·악재=파랑.
            const string KUp = "#ff5252";   // 상승/호재 빨강
            const string KDn = "#5082c8";   // 하락/악재 파랑

            var sb = new StringBuilder(256 + series.Length * 12);
            sb.Append($"<svg viewBox='0 0 {w} {h}' preserveAspectRatio='none' xmlns='http://www.w3.org/2000/svg' style='width:{w}px;height:{h}px;vertical-align:middle'>");
            // 추세선 — 종가 상승 마감 빨강·하락 파랑.
            string lineColor = series[^1] >= series[0] ? KUp : KDn;
            sb.Append("<polyline fill='none' stroke='").Append(lineColor).Append("' stroke-width='1.4' points='");
            for (int i = 0; i < series.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(X(i).ToString("0.#", ci)).Append(',').Append(Y(series[i]).ToString("0.#", ci));
            }
            sb.Append("'/>");
            // 뉴스 닷 — 호재 빨강·악재 파랑. 시계열 선 위 시점 마킹(인과관계).
            if (dots is not null)
                foreach (var d in dots)
                {
                    if ((uint)d.Index >= (uint)series.Length) continue;   // 경계 방어
                    string dc = d.Bullish ? KUp : KDn;
                    sb.Append("<circle r='2' cx='").Append(X(d.Index).ToString("0.#", ci))
                      .Append("' cy='").Append(Y(series[d.Index]).ToString("0.#", ci))
                      .Append("' fill='").Append(dc).Append("'/>");
                }
            sb.Append("</svg>");
            return sb.ToString();
        }
    }
}
