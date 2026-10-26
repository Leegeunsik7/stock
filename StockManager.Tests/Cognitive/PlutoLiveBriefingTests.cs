// ============================================================================
// PlutoLiveBriefingTests — v12.18.89-DEV: DualArena UX 직관화(라이브 브리핑 한 줄 · 3대 시장 신호등 · 사용자 용어 순화)
//
//   ① 신호등: KRX(정규장/장마감/개장 전/주말 + 다음 개장) · US(휴장/장마감/거시 키 대기/프리마켓/정규장/달력 밖) · Upbit(24/7 · 무효 시각 · 실피드 미결선) · 미결선 = ⚪.
//   ② 한 줄 브리핑: 야간 장마감 모드 문장 · 후보 = 추천 통과 카드 이름만(없음 = 정직 문구) · 열린 시장 0 = 관망.
//   ③ 컴포넌트: HUD 브리핑 카드/신호등 · 거시 분석 대기(FRED 키) · 0세대 · 국면 가중치(기본값) · 매트릭스 장외 대기 행.
//   ④ RCL 순수성: 신규 포트 엔진 참조 0.
// ============================================================================
using System;
using System.IO;
using Bunit;
using MS2E.UI.Portable.Components;
using MS2E.UI.Portable.Ports.Trading;
using Xunit;

namespace StockManager.Tests.Cognitive
{
    public sealed class PlutoLiveBriefingTests
    {
        private static readonly DateTime MonNight = new(2026, 10, 26, 22, 10, 0);   // 월요일 22:10 KST
        private static readonly DateTime FriNight = new(2026, 10, 30, 21, 0, 0);    // 금요일
        private static readonly DateTime TueMorning = new(2026, 10, 27, 8, 30, 0);  // 화요일 개장 전
        private static readonly DateTime Sat = new(2026, 10, 31, 12, 0, 0);

        private static PlutoSwarmLaneView Lane(int m, bool connected) => new(m, connected, "src", 100, connected ? 90 : 0, 0, 0, 0, false, false, 0, 0);

        // ── ① 신호등 ─────────────────────────────────────────────────────────

        [Fact]
        public void Krx_Light_Session_States_And_Next_Open()
        {
            Assert.Equal(PlutoLightCode.Off, PlutoLiveBriefing.Krx(false, false, MonNight, null).State);
            Assert.Equal(PlutoLightCode.Off, PlutoLiveBriefing.Krx(true, false, null, null).State);          // 시각 없음 = 미결선
            var closed = PlutoLiveBriefing.Krx(true, false, MonNight, null);
            Assert.Equal("🌙 장마감 (내일 09:00 개장)", closed.Badge);
            Assert.Equal("🌙 장마감 대기 중 (내일 09:00 개장)", closed.AwaitText("x"));
            Assert.Equal("월요일 09:00 개장", PlutoLiveBriefing.Krx(true, false, FriNight, null).Next);
            Assert.Equal("🌙 개장 전 (오늘 09:00 개장)", PlutoLiveBriefing.Krx(true, false, TueMorning, null).Badge);
            Assert.Equal("🌙 주말 휴장 (월요일 09:00 개장)", PlutoLiveBriefing.Krx(true, false, Sat, null).Badge);
            Assert.Equal("🟢 실시간 감시 중", PlutoLiveBriefing.Krx(true, true, MonNight, null).Badge);
            Assert.Equal(PlutoLightCode.Await, PlutoLiveBriefing.Krx(true, true, MonNight, Lane(0, false)).State);
            Assert.Equal("시세 수신 대기 — n", PlutoLiveBriefing.Krx(true, true, MonNight, null).AwaitText("n"));
        }

        [Fact]
        public void Us_And_Crypto_Lights()
        {
            Assert.Equal("🟡 거시 피드 대기", PlutoLiveBriefing.Us(true, UsArenaPhase.PreMarket, true, null).Badge);
            Assert.Equal("🟡 거시 피드 대기", PlutoLiveBriefing.Us(true, UsArenaPhase.ContinuousTrading, true, null).Badge);
            Assert.Equal(PlutoLightCode.Closed, PlutoLiveBriefing.Us(true, UsArenaPhase.Closed, true, null).State);   // 장외 우선
            Assert.Equal(PlutoLightCode.Closed, PlutoLiveBriefing.Us(true, UsArenaPhase.HolidayOrWeekend, false, null).State);
            Assert.Equal(PlutoLightCode.Await, PlutoLiveBriefing.Us(true, UsArenaPhase.PreMarket, false, null).State);
            Assert.Equal(PlutoLightCode.Await, PlutoLiveBriefing.Us(true, UsArenaPhase.ContinuousTrading, false, Lane(1, false)).State);
            Assert.Equal(PlutoLightCode.Live, PlutoLiveBriefing.Us(true, UsArenaPhase.ContinuousTrading, false, Lane(1, true)).State);
            Assert.Equal(PlutoLightCode.Off, PlutoLiveBriefing.Us(true, UsArenaPhase.CalendarUncovered, false, null).State);   // fail-closed
            Assert.Equal(PlutoLightCode.Off, PlutoLiveBriefing.Us(false, UsArenaPhase.ContinuousTrading, false, null).State);

            Assert.Equal("🟢 실시간 감시 중", PlutoLiveBriefing.Crypto(true, true, null).Badge);
            Assert.Equal(PlutoLightCode.Off, PlutoLiveBriefing.Crypto(true, false, null).State);
            Assert.Equal(PlutoLightCode.Await, PlutoLiveBriefing.Crypto(true, true, Lane(2, false)).State);
            Assert.Equal(PlutoLightCode.Off, PlutoLiveBriefing.Crypto(false, true, null).State);
        }

        [Fact]
        public void Macro_Feed_Key_Pending_Matches_Host_Text_Contract()
        {
            Assert.True(PlutoMacroFeed.IsKeyPending("거시 피드: FRED API 키 미설정 · 관측 대기"));
            Assert.True(PlutoMacroFeed.IsKeyPending("거시 피드: FRED API 키 형식 오류 · 관측 대기"));
            Assert.False(PlutoMacroFeed.IsKeyPending("거시 피드: FRED 공식 API(VIX · 10년물 · 달러 · 원/달러 · S&P500)"));
            Assert.False(PlutoMacroFeed.IsKeyPending(null));
            Assert.Equal("거시 피드: FRED API 키 미설정 · 관측 대기", StockManager.Adapters.PlutoPublicMacroSource.FeedText(StockManager.Adapters.FredKeyState.Missing));
        }

        // ── ② 한 줄 브리핑 ───────────────────────────────────────────────────

        private static PlutoRecommendationCard Coin(string code, string name) =>
            new(PlutoRecoMarket.Crypto, 1, 1, code, name, 70, 20, 20, 20, 10, new[] { "근거" }, false, 0, "1", "", false, 10, 0, "반대 논리 없음");

        private static PlutoRecoBoardView Board(params PlutoRecommendationCard[] coins) =>
            new(true, 3, DateTime.UtcNow, new PlutoRecoMarketView(Array.Empty<PlutoRecommendationCard>(), 0, 0, 0, 0, 200, 0), new PlutoRecoMarketView(Array.Empty<PlutoRecommendationCard>(), 0, 0, 0, 0, 100, 0))
            { Crypto = new PlutoRecoMarketView(coins, coins.Length, 0, 0, 0, 120, coins.Length) };

        private static PlutoMarketLights NightLights() => new(
            PlutoLiveBriefing.Krx(true, false, MonNight, null),
            PlutoLiveBriefing.Us(true, UsArenaPhase.PreMarket, true, null),
            PlutoLiveBriefing.Crypto(true, true, null));

        [Fact]
        public void Night_Briefing_Sentence_Uses_Passed_Cards_Only()
        {
            var lv = default(PlutoLearningView);
            string s = PlutoLiveBriefing.Compose(NightLights(), MonNight, true, in lv, true, Board(Coin("KRW-XRP", "리플"), Coin("KRW-USDT", "테더"), Coin("KRW-BTC", "비트코인")));
            Assert.StartsWith("🌙 야간 장마감 모드: 국내 주식은 내일 09:00 개장 대기 중입니다. 가상자산 120종을 정밀 감시 중이며, 추천 후보(리플·테더)를 관측하고 있습니다.", s);
            Assert.Contains("미국 주식은 거시 피드 대기 중입니다.", s);
            Assert.Contains("AI 방침: 열린 시장만 가상 매매합니다(균형 성향 · 학습 0세대 · 실주문 0).", s);
            Assert.DoesNotContain("비트코인", s);                                                       // 상위 2종만

            string none = PlutoLiveBriefing.Compose(NightLights(), MonNight, true, in lv, true, Board());
            Assert.Contains("추천 기준을 통과한 후보는 아직 없습니다.", none);                         // 가짜 후보 0
            string unwired = PlutoLiveBriefing.Compose(NightLights(), MonNight, false, in lv, false, PlutoRecoBoardView.None);
            Assert.Contains("가상자산 시장을 정밀 감시 중이며, 추천 엔진은 꺼져 있습니다.", unwired);
            Assert.Contains("학습 엔진 미결선", unwired);
        }

        [Fact]
        public void Briefing_With_No_Open_Market_Holds()
        {
            var lights = new PlutoMarketLights(PlutoLiveBriefing.Krx(true, false, Sat, null), PlutoLiveBriefing.Us(true, UsArenaPhase.HolidayOrWeekend, false, null), PlutoLiveBriefing.Crypto(true, false, null));
            var lv = default(PlutoLearningView);
            string s = PlutoLiveBriefing.Compose(lights, Sat, true, in lv, true, Board());
            Assert.StartsWith("🌙 주말 휴장 모드: 국내 주식은 월요일 09:00 개장 대기 중입니다.", s);
            Assert.Contains("관망", s);
            Assert.StartsWith("☀️ 정규장 모드", PlutoLiveBriefing.Compose(new PlutoMarketLights(PlutoLiveBriefing.Krx(true, true, TueMorning, null), lights.Us, lights.Crypto), TueMorning, true, in lv, true, Board()));
        }

        // ── ③ 컴포넌트 ───────────────────────────────────────────────────────

        [Fact]
        public void Hud_Renders_Briefing_Lights_And_Plain_Terms()
        {
            using var ctx = new TestContext();
            var lv = new PlutoLearningView(true, PlutoLearningHealth.NoData, null, -1, false, 0, 0, 0, 0, 0, 0,
                MacroFeedText: "거시 피드: FRED API 키 미설정 · 관측 대기",
                Tensor: new PlutoTensorView(0, null, 0, new[] { new PlutoTensorLaneView(0, PlutoTensorSourceCode.Global, 0, 0, 0, PlutoWarmCode.NoData, 0, 0) }));
            var cut = ctx.RenderComponent<PlutoCognitiveHud>(p => p.Add(x => x.Wired, true).Add(x => x.View, lv).Add(x => x.Briefing, "브리핑 문장").Add(x => x.Lights, NightLights()));
            Assert.Equal("브리핑 문장", cut.Find("[data-hud-brief] .hud-brief-t").TextContent);
            Assert.Equal("live", cut.Find("[data-hud-light='2']").GetAttribute("data-light-state"));
            Assert.Contains("Upbit: 🟢 실시간 감시 중", cut.Find("[data-hud-light='2']").TextContent);
            Assert.Contains("KRX: 🌙 장마감 (내일 09:00 개장)", cut.Find("[data-hud-light='0']").TextContent);
            Assert.Contains("US: 🟡 거시 피드 대기", cut.Find("[data-hud-light='1']").TextContent);
            var regime = cut.Find("[data-hud-tile='regime']").TextContent;
            Assert.Contains("거시 분석 대기", regime);
            Assert.Contains("(FRED 키 설정 시 가동)", regime);
            Assert.DoesNotContain("API 키 미설정", regime);
            Assert.Contains("0세대 (시세 데이터 축적 중)", cut.Find("[data-hud-tile='maturity']").TextContent);
            Assert.Contains("국면 가중치 (기본값)", cut.Find("[data-hud-tile='tensor']").TextContent);
            Assert.DoesNotContain("G0", cut.Markup);

            var bare = ctx.RenderComponent<PlutoCognitiveHud>(p => p.Add(x => x.Wired, false).Add(x => x.View, default(PlutoLearningView)));
            Assert.Empty(bare.FindAll("[data-hud-brief]"));                                           // Briefing null = 미표시
        }

        [Fact]
        public void Matrix_Awaiting_Row_Uses_Market_Light()
        {
            var board = new PlutoRecoBoardView(true, 1, DateTime.UtcNow, new PlutoRecoMarketView(Array.Empty<PlutoRecommendationCard>(), 0, 0, 0, 0, 200, 0), PlutoRecoMarketView.None);
            using var ctx = new TestContext();
            var cut = ctx.RenderComponent<PlutoRecoMatrix>(p => p.Add(x => x.Board, board).Add(x => x.Wired, true).Add(x => x.Tab, PlutoRecoMarket.Krx).Add(x => x.Lights, NightLights()));
            var row = cut.Find(".mx-await[data-mx-await='0']");
            Assert.Equal("closed", row.GetAttribute("data-mx-light"));
            Assert.Contains("🌙 장마감 대기 중 (내일 09:00 개장)", row.TextContent);
            Assert.Contains("장외", row.QuerySelector(".mx-state")!.TextContent);
            Assert.Contains("--", row.QuerySelector(".mx-score")!.TextContent);                     // 점수 생성 0
            Assert.DoesNotContain("Awaiting Market Feed", cut.Markup);
        }

        // ── ④ RCL 순수성 ─────────────────────────────────────────────────────

        [Fact]
        public void Live_Briefing_Port_Stays_Pure()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string src = File.ReadAllText(Path.Combine(dir!.FullName, "MS2E.UI.Portable", "Ports", "Trading", "PlutoLiveBriefing.cs"));
            Assert.DoesNotContain("StockManager.", src);
            Assert.DoesNotContain("DateTime.Now", src);                                              // 시각 = 엔진 SessionKst
        }
    }
}
