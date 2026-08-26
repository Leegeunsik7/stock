// ============================================================================
// NewsIntelligenceTests — v4.93.0: 실시간 뉴스 인텔리전스
//
//  §1 페이로드·δ=0 파서    — 미분석 격리·감성 분류·JSON span 파싱·fail-closed.
//  §2 세맨틱 쇼크 행렬      — 하향 floor·severity 클램프·무충격·오염 fail-closed.
//  §3 감성 속도 브레이커    — 워밍업·하향 3σ 트립→IgnitionLock.Downgrade·상향 무트립.
//  §4 멘토·SVG·컨트롤러·텔레메트리·liveArmed 격리.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace StockManager.Tests
{
    public sealed class NewsIntelligenceTests
    {
        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

        // ── §1 페이로드 · δ=0 파서 ─────────────────────────────────────────────

        [Fact]
        public void Payload_Unanalyzed_And_State_Classification()
        {
            Assert.True(NewsPayload.Of(5930, 0m, 0).IsUnanalyzed);                       // 감성 0 = 미분석
            Assert.Equal(SentimentState.Unanalyzed, NewsPayload.Of(5930, 0m, 0).State);
            Assert.Equal(SentimentState.Bullish,  NewsPayload.Of(5930, 0.42m, 0).State); // ≥+0.3 호재
            Assert.Equal(SentimentState.Bearish,  NewsPayload.Of(5930, -0.55m, 0).State);// ≤-0.3 악재
            Assert.Equal(SentimentState.Neutral,  NewsPayload.Of(5930, 0.1m, 0).State);
        }

        [Fact]
        public void Parser_ValidJson_ParsesNumericFields_ZeroString()
        {
            Assert.True(NewsIntelParser.TryParse(Utf8("{\"code\":5930,\"sent\":0.42,\"ts\":638000000000000000,\"shock\":1,\"sev\":0.8}"), out var p));
            Assert.Equal(5930L, p.SymbolCode);
            Assert.Equal(0.42m, p.Sentiment, precision: 6);
            Assert.Equal(NewsShockClass.MacroInterest, p.ShockClass);
            Assert.Equal(0.8m, p.Severity, precision: 6);
            Assert.False(p.IsUnanalyzed);
        }

        [Fact]
        public void Parser_Invalid_FailClosed()
        {
            Assert.False(NewsIntelParser.TryParse(Utf8("{}"), out _));                 // code 없음
            Assert.False(NewsIntelParser.TryParse(Utf8("not json"), out _));           // 파싱 예외
            // 감성 범위 초과 → 클램프, 알 수 없는 쇼크 → None.
            Assert.True(NewsIntelParser.TryParse(Utf8("{\"code\":1,\"sent\":9.9,\"shock\":99}"), out var q));
            Assert.Equal(1.0m, q.Sentiment, precision: 6);
            Assert.Equal(NewsShockClass.None, q.ShockClass);
        }

        [Fact]
        public void Ring_Publish_Read_RoundTrip()
        {
            var ring = new NewsDispatchRing(64);
            ring.Publish(NewsPayload.Of(5930, 0.3m, 100));
            Assert.Equal(1L, ring.Published);
            Assert.True(ring.TryRead(out var p));
            Assert.Equal(5930L, p.SymbolCode);
            Assert.False(ring.TryRead(out _));
        }

        // ── §2 세맨틱 쇼크 전파 행렬 ───────────────────────────────────────────

        [Fact]
        public void Shock_Propagate_FloorsDownward()
        {
            var present = new[] { QMoney.Of(1000m), QMoney.Of(2000m) };
            var classes = new[] { AssetClass.Crypto, AssetClass.DomesticStock };
            var stressed = new QMoney[2];
            // MacroInterest sev=0.5: Crypto β=1.8 → survival max(0,1−0.9)=0.1 → 1000×0.1=100.
            Assert.True(SemanticShockMatrix.Propagate(new ShockDto(NewsShockClass.MacroInterest, 0.5m), present, classes, stressed));
            Assert.Equal(100m, stressed[0].ToDecimal(), precision: 2);
            Assert.True(stressed[0].ToDecimal() < present[0].ToDecimal());   // ★하향(floor)
            Assert.True(stressed[1].ToDecimal() < present[1].ToDecimal());
        }

        [Fact]
        public void Shock_None_NoStress_And_SeverityClamp()
        {
            var present = new[] { QMoney.Of(1000m) };
            var classes = new[] { AssetClass.Crypto };
            var stressed = new QMoney[1];
            // None → survival 1.0 → 무충격.
            Assert.True(SemanticShockMatrix.Propagate(new ShockDto(NewsShockClass.None, 0.9m), present, classes, stressed));
            Assert.Equal(1000m, stressed[0].ToDecimal(), precision: 2);
            // severity>1 클램프 → 1.0.
            Assert.True(SemanticShockMatrix.Propagate(new ShockDto(NewsShockClass.Geopolitical, 5.0m), present, classes, stressed));
            Assert.True(stressed[0].ToDecimal() <= present[0].ToDecimal());
        }

        [Fact]
        public void Shock_NegativePresent_FailClosed()
        {
            var att = new OrderAttestationService();
            var present = new[] { new QMoney(-1) };
            var classes = new[] { AssetClass.DomesticStock };
            var stressed = new QMoney[1];
            Assert.False(SemanticShockMatrix.Propagate(new ShockDto(NewsShockClass.MacroInterest, 0.5m), present, classes, stressed, att));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // 오염 → fail-closed 동결
            // 길이 불일치도 false.
            Assert.False(SemanticShockMatrix.Propagate(new ShockDto(NewsShockClass.MacroInterest, 0.5m), present, classes, new QMoney[2]));
        }

        // ── §3 감성 속도 서킷 브레이커 ─────────────────────────────────────────

        private static SentimentVelocityBreaker Primed(IgnitionLock ign)
        {
            var b = new SentimentVelocityBreaker(ign);
            // 워밍업 + 분산 형성: +0.10~+0.12 진동(델타 ±0.02) 15표본 → var>0·mean≈0.
            long[] seq = { 10_000_000, 12_000_000, 10_000_000, 12_000_000, 11_000_000, 10_000_000, 12_000_000,
                           11_000_000, 10_000_000, 12_000_000, 11_000_000, 10_000_000, 12_000_000, 11_000_000, 10_000_000 };
            foreach (var s in seq) b.RecordAndCheck(s);
            return b;
        }

        [Fact]
        public void Breaker_Warmup_NoTrip()
        {
            var b = new SentimentVelocityBreaker(null);
            for (int i = 0; i < 5; i++) Assert.False(b.RecordAndCheck(10_000_000 + i));   // <10 표본 워밍업
            Assert.False(b.IsTripped);
        }

        [Fact]
        public void Breaker_DownwardSpike_Trips_And_Downgrades()
        {
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(s => s == "SIG", att);
            Assert.True(ign.Arm(IgnitionStage.MasterIgnited, "SIG"));
            var b = Primed(ign);
            // 급격한 하향(-0.9) → 3σ 하향 폭주 → 트립 + 발주 무장 강등.
            Assert.True(b.RecordAndCheck(-90_000_000));
            Assert.True(b.IsTripped);
            Assert.True(b.TripCount >= 1);
            Assert.Equal(IgnitionStage.ProbeReady, ign.Stage);   // ★MasterIgnited → ProbeReady 강등
        }

        [Fact]
        public void Breaker_UpwardSpike_NoTrip()
        {
            var b = Primed(new IgnitionLock(s => s == "SIG", new OrderAttestationService()));
            Assert.False(b.RecordAndCheck(90_000_000));   // 급격한 상향 → 무트립(하향만 패닉)
            Assert.False(b.IsTripped);
        }

        // ── §4 멘토 · SVG · 컨트롤러 · 텔레메트리 · liveArmed ───────────────────

        [Fact]
        public void Mentor_Advise_Deterministic_Korean_LiveArmedNote()
        {
            var bear = PortfolioMentorAdvisor.Advise(new MentorInput(0, 0, 0, AssetClass.DomesticStock, 0, -0.5m, false, false));
            Assert.Contains("악재(파랑)", bear);
            var bull = PortfolioMentorAdvisor.Advise(new MentorInput(0, 0, 0, AssetClass.DomesticStock, 0, 0.5m, false, false));
            Assert.Contains("호재(빨강)", bull);
            var trip = PortfolioMentorAdvisor.Advise(new MentorInput(0, 0, 0, AssetClass.DomesticStock, 0, -0.5m, true, true));
            Assert.Contains("정보 서킷 브레이커", trip);
            Assert.Contains("쇼크", trip);
            Assert.Contains("이중 키", bull);   // ★liveArmed:false 고지 — 조언은 실주문 무접촉
        }

        [Fact]
        public void Svg_Build_KoreanColors_Dots()
        {
            var svg = NewsRadarSvg.Build(new decimal[] { 100m, 101m, 99m, 103m }, new List<NewsRadarSvg.NewsDot> { new(1, true), new(2, false) });
            Assert.Contains("<svg", svg);
            Assert.Contains("<polyline", svg);
            Assert.Contains("#ff5252", svg);       // ★한국색상: 상승 마감 선 + 호재 닷 = 빨강
            Assert.Contains("#5082c8", svg);       // ★한국색상: 악재 닷 = 파랑
            Assert.DoesNotContain("var(--up)", svg);    // ★서구식 토큰(app.css --up=초록) 미사용
            Assert.DoesNotContain("var(--down)", svg);
            Assert.Contains("<circle", svg);
        }

        [Fact]
        public void Controller_Ingest_IsolatesUnanalyzed_UpdatesSentiment()
        {
            var b = new SentimentVelocityBreaker(null);
            var c = new NewsIntelController(b, new NewsDispatchRing(64), () => 1000L);
            c.Ingest(NewsPayload.Of(5930, 0m, 100));           // 미분석 → 격리(감성 미갱신)
            Assert.Equal(1L, c.NewsUnanalyzedCount);
            Assert.Equal(0L, c.MarketSentimentRaw);
            c.Ingest(NewsPayload.Of(5930, 0.4m, 200, NewsShockClass.MacroInterest, 0.6m));   // 분석 완료 + 쇼크
            Assert.True(c.MarketSentimentRaw > 0);
            Assert.True((c.SemanticShockActive & (1L << (int)NewsShockClass.MacroInterest)) != 0);
        }

        [Fact]
        public void Controller_DownwardStream_Trips_MentorFsmAmber()
        {
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(s => s == "SIG", att);
            ign.Arm(IgnitionStage.MasterIgnited, "SIG");
            var b = new SentimentVelocityBreaker(ign);
            var c = new NewsIntelController(b, new NewsDispatchRing(128), () => 1000L);
            long[] seq = { 10_000_000, 12_000_000, 10_000_000, 12_000_000, 11_000_000, 10_000_000, 12_000_000,
                           11_000_000, 10_000_000, 12_000_000, 11_000_000, 10_000_000, 12_000_000, 11_000_000, 10_000_000 };
            foreach (var s in seq) c.Ingest(NewsPayload.Of(5930, s / 100_000_000m, 300));
            c.Ingest(NewsPayload.Of(5930, -0.9m, 400));   // 하향 폭주
            Assert.Equal(1L, c.SentimentBreakerTripped);
            Assert.Equal((long)MentorFsmState.Amber, c.MentorFsmCode);   // ★브레이커 트립 → FSM Amber
        }

        [Fact]
        public void RingTelemetry_NewsFields_And_Helpers()
        {
            var t = new RingTelemetry(0, 0, 0, 0, 0, 0.0,
                newsRatePerMin: 42, marketSentimentRaw: -40_000_000L, semanticShockActive: 1,
                sentimentBreakerTripped: 1, newsUnanalyzedCount: 3, mentorFsmCode: (long)MentorFsmState.Amber);
            Assert.Equal(42L, t.NewsRatePerMin);
            Assert.True(t.MarketBearish);                 // -0.4 ≤ -0.3 → 악재(파랑)
            Assert.False(t.MarketBullish);
            Assert.True(t.SemanticShockEngaged);
            Assert.True(t.SentimentBreakerActive);
            Assert.Equal("Amber", t.MentorFsmName);
            Assert.Equal(-0.4m, t.MarketSentiment, precision: 6);
        }

        [Fact]
        public void Publisher_ReflectsNewsIntelController()
        {
            var att = new OrderAttestationService();
            var b = new SentimentVelocityBreaker(null);
            var c = new NewsIntelController(b, new NewsDispatchRing(64), () => 1000L);
            c.Ingest(NewsPayload.Of(5930, 0.5m, 900, NewsShockClass.Geopolitical, 0.7m));
            var ring = new OrderDispatchRing(8);
            var disp = new GatedOrderDispatcher(ring, () => false, att);
            var ch = new RingTelemetryChannel();
            var pub = new RingTelemetryPublisher(ring, disp, ch, () => 0L,
                null, null, null, null, null, null, null, null, null, c);
            pub.Sample();
            Assert.True(ch.TryRead(out var snap));
            Assert.True(snap.MarketSentimentRaw > 0);
            Assert.True(snap.SemanticShockEngaged);
        }

        [Fact]
        public void LiveArmed_IntelLayer_NeverTouchesOrderPath()
        {
            // ★적대 요구: 멘토 조언/쇼크 클램프가 아무리 공격적이어도 실주문 경로에 1바이트도 미치지 않음.
            var att = new OrderAttestationService();   // Normal
            _ = PortfolioMentorAdvisor.Advise(new MentorInput(1_000_000m, 500_000m, 30m, AssetClass.Crypto, 80m, 0.9m, true, false));
            var present = new[] { QMoney.Of(1000m) }; var classes = new[] { AssetClass.Crypto }; var stressed = new QMoney[1];
            SemanticShockMatrix.Propagate(new ShockDto(NewsShockClass.MacroInterest, 1.0m), present, classes, stressed, att);
            // 조언·쇼크 전파는 순수 텍스트·그림자 가치 산출 — 게이팅/발주 상태 불변(정상 유지).
            Assert.Equal(GatingMode.Normal, att.Mode);
            Assert.Equal(0m, stressed[0].ToDecimal(), precision: 2);   // Crypto β1.8×sev1.0>1 → 0 클램프(방어 방향)
        }

        // ── §5 적대 검증 회귀(v4.93.0 확정 결함 고정) ──────────────────────────

        [Fact]
        public void Parser_OutOfRangeNumbers_FailClosed_NoThrow()
        {
            // ★#1 회귀: 유효 JSON 숫자이나 CLR 타입 범위 밖 → GetInt64/GetDecimal throw 대신 TryGet* 로 fail-closed 거부.
            Assert.False(NewsIntelParser.TryParse(Utf8("{\"code\":1.5}"), out _));                    // Int64 자리 소수
            Assert.False(NewsIntelParser.TryParse(Utf8("{\"code\":99999999999999999999}"), out _));   // Int64 오버플로
            Assert.False(NewsIntelParser.TryParse(Utf8("{\"code\":5930,\"shock\":1.5}"), out _));      // Int32 자리 소수
            Assert.False(NewsIntelParser.TryParse(Utf8("{\"code\":5930,\"sent\":1e400}"), out _));     // decimal 오버플로
            Assert.False(NewsIntelParser.TryParse(Utf8("{\"code\":5930,\"ts\":1e309}"), out _));       // Int64(ts) 오버플로
        }

        [Fact]
        public void Parser_OutOfRange_Rejection_IsAllocationFree()
        {
            // ★#1 회귀: 적대 숫자 버스트도 예외 힙 할당 없이 거부 — δ=0 인입 불변식 유지(현재 스레드 측정).
            var bad = Utf8("{\"code\":99999999999999999999,\"sent\":1e400,\"shock\":1.5}");
            NewsIntelParser.TryParse(bad, out _);                                     // 워밍업(JIT)
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; i++) Assert.False(NewsIntelParser.TryParse(bad, out _));
            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);        // ★무할당 거부(예외 미발생)
        }

        [Fact]
        public void Parser_NestedContainerValue_NoKeyLeak_FailClosed()
        {
            // ★#6 회귀: 매칭 키 값이 컨테이너면 Skip — 중첩 프로퍼티명이 최상위 필드로 누수되지 않고 컨테이너 code 는 무효 프레임.
            Assert.False(NewsIntelParser.TryParse(Utf8("{\"code\":{\"sent\":0.5}}"), out _));    // 버그면 code 컨테이너를 유효로 오인+중첩 sent 누수
            // code 는 유효 숫자, 무관 키 아래 중첩 sent 는 미누수(격리).
            Assert.True(NewsIntelParser.TryParse(Utf8("{\"code\":5930,\"meta\":{\"sent\":0.9}}"), out var p));
            Assert.Equal(5930L, p.SymbolCode);
            Assert.True(p.IsUnanalyzed);   // 중첩 sent 미채택 → SentimentRaw 0
        }

        [Fact]
        public void Controller_SentimentEma_ZeroCrossing_KeepsHistory()
        {
            // ★#2/#4 회귀: EMA 가 정당하게 0을 지나도 '표본 없음' 센티널로 오인·단일 표본 리셋하지 않음(_ingested 카운트 판별).
            var c = new NewsIntelController(new SentimentVelocityBreaker(null), new NewsDispatchRing(64), () => 1000L);
            c.Ingest(NewsPayload.Of(5930, 0.0025m, 100));    // count1 → seed ema=250000
            c.Ingest(NewsPayload.Of(5930, -0.01m, 200));     // count2 → (250000*8 + (-1000000)*2)/10 = 0 (정당한 중립 EMA)
            Assert.Equal(0L, c.MarketSentimentRaw);
            c.Ingest(NewsPayload.Of(5930, 0.5m, 300));       // count3 → 블렌드 (0*8 + 50000000*2)/10 = 10000000
            Assert.Equal(10_000_000L, c.MarketSentimentRaw); // ★리셋(50000000) 아님 — 이력 보존
        }
    }
}
