// ============================================================================
// PlutoMultiAssetScreenerTests — v12.18.79-DEV: 3대 자산(KRX 200 · US 100 · 업비트 120) 감시 카탈로그 · 코인 4대 팩터 스코어러 · Crypto Top 5 슬롯
//   ① 카탈로그 정합성: 시장별 수 · 계열 수 · 코드 형식 · 중복 0 · 아레나 피드 종목 ⊂ 카탈로그 · 9자 이상 업비트 마켓 = 키 0 · 이름 조회(미등록 = null).
//   ② 코인 스코어러: 유동성 점수표 · 4대 팩터 슬롯(유동성 · 스퀴즈 · CVD · 뎁스) · 밸류에이션 무시 · 판독 전무 = 근거 부족 ·
//      1번 게이트(스프레드 ≥ 50bp) + CVD 약세 = 악마의 변호인 탈락 · 시장별 반대 논리 문구 · 근거 3줄.
//   ③ 스크리너: Crypto 유니버스 → 코인 Top 5 · 추천 수량(코인 raw 10^8) · 감시/통과 집계 · 3대 시장 합계 · TryFind(Crypto).
//   ④ 업비트 아레나 유니버스 어댑터(시세 유니버스 ⊂ 표 · 매수 1호가 = 스프레드 입력).
//   ⑤ 포트 어댑터(코인 카드 · QtyText) · DualArenaPage 3탭 · 감시 뱃지 · 코인 1-Click → 크립토 티켓 바인딩 · 호스트 결선 문자열.
//   ⑥ δ=0: 3대 시장 동시 게시 중 TryFind/합계 판독 10만 회 = 0B · 카탈로그 Contains 10만 회 = 0B(MinAllocated · Release).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Microstructure;
using StockManager.Engines.Cognitive.Screening;
using StockManager.Engines.Cognitive.Technical;
using StockManager.Engines.Cognitive.Valuation;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoMultiAssetScreenerTests
    {
        private const long S = PlutoDiscoveryScorer.CryptoScale;
        private static readonly long T0 = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc).Ticks;
        private static readonly long Btc = CryptoArenaUniverse.Pack("KRW-BTC"), Eth = CryptoArenaUniverse.Pack("KRW-ETH"),
            Xrp = CryptoArenaUniverse.Pack("KRW-XRP"), Sol = CryptoArenaUniverse.Pack("KRW-SOL");

        /// <summary>코인 입력(null = 해당 판독 없음 · cvd/depth 중 하나라도 있으면 미시구조 판독 있음).</summary>
        private static PlutoScreenInputs Coin(long sym, int? squeeze, int? cvd, int? depth,
            long ask = 150_001_000 * S, long bid = 150_000_000 * S, int tiers = 15,
            PlutoDivergenceType div = PlutoDivergenceType.None, bool trap = false, MarketRegime regime = MarketRegime.TrendingBull,
            bool hasValuation = false, PlutoValuationGrade grade = PlutoValuationGrade.Unknown)
        {
            bool hm = cvd.HasValue || depth.HasValue;
            return new PlutoScreenInputs(PlutoScreenMarket.Crypto, sym, ask,
                hasValuation, hasValuation ? -80 : 0, 0, grade, 0, false,
                squeeze.HasValue, squeeze ?? 0, squeeze.HasValue ? PlutoSqueezePhase.BreakoutUp : default, squeeze.HasValue ? PlutoProfileZone.PocSupport : default,
                hm, hm ? (cvd ?? 0) + (depth ?? 0) : 0, hm ? div : PlutoDivergenceType.None, false, false, hm && trap, regime,
                squeeze ?? 0, cvd ?? 0, depth ?? 0, (depth ?? 0) * 100, tiers, bid);
        }

        // ── ① 카탈로그 정합성 ─────────────────────────────────────────────────

        [Fact]
        public void Catalog_Has_Three_Asset_Universe_With_Expected_Counts_And_Segments()
        {
            Assert.Equal(200, PlutoMultiAssetUniverseCatalog.KrxCount);
            Assert.Equal(100, PlutoMultiAssetUniverseCatalog.UsCount);
            Assert.Equal(120, PlutoMultiAssetUniverseCatalog.CryptoCount);
            Assert.Equal(420, PlutoMultiAssetUniverseCatalog.TotalCount);
            Assert.Equal(PlutoMultiAssetUniverseCatalog.TotalCount, PlutoMultiAssetUniverseCatalog.All.Count);
            Assert.Equal(0, PlutoMultiAssetUniverseCatalog.CountOf((PlutoScreenMarket)9));
            Assert.Empty(PlutoMultiAssetUniverseCatalog.Of((PlutoScreenMarket)9));
            var seg = new int[5];
            foreach (var e in PlutoMultiAssetUniverseCatalog.All) seg[(int)e.Segment]++;
            Assert.Equal(new[] { 140, 60, 82, 18, 120 }, seg);
            Assert.Equal("코스피 200", PlutoMultiAssetUniverseCatalog.SegmentLabel(PlutoUniverseSegment.Kospi));
            Assert.Equal("업비트 KRW", PlutoMultiAssetUniverseCatalog.SegmentLabel(PlutoUniverseSegment.UpbitKrw));
        }

        [Fact]
        public void Catalog_Codes_Are_WellFormed_Unique_And_Keyed_Like_The_Arenas()
        {
            foreach (var m in new[] { PlutoScreenMarket.Krx, PlutoScreenMarket.Us, PlutoScreenMarket.Crypto })
            {
                var codes = new HashSet<string>(StringComparer.Ordinal);
                var keys = new HashSet<long>();
                foreach (var e in PlutoMultiAssetUniverseCatalog.Of(m))
                {
                    Assert.Equal(m, e.Market);
                    Assert.True(codes.Add(e.Code), "중복 코드 " + e.Code);
                    Assert.False(string.IsNullOrWhiteSpace(e.Name));
                    Assert.Equal(PlutoMultiAssetUniverseCatalog.KeyOf(m, e.Code), e.Symbol);
                    if (e.Symbol > 0) Assert.True(keys.Add(e.Symbol), "중복 키 " + e.Code);
                    switch (m)
                    {
                        case PlutoScreenMarket.Krx:
                            Assert.Matches("^[0-9]{6}$", e.Code);
                            Assert.True(e.Symbol > 0);
                            Assert.Equal(e.Code, PlutoMarketScreener.CodeOf(m, e.Symbol));
                            break;
                        case PlutoScreenMarket.Us:
                            Assert.True(e.Symbol > 0, e.Code);
                            Assert.Equal(e.Code, PlutoMarketScreener.CodeOf(m, e.Symbol));
                            break;
                        default:
                            Assert.StartsWith("KRW-", e.Code);
                            if (e.Code.Length <= 8) { Assert.True(e.Symbol > 0); Assert.Equal(e.Code, PlutoMarketScreener.CodeOf(m, e.Symbol)); }
                            else Assert.Equal(0, e.Symbol);                       // 9자 이상 = 아레나 키 인코딩 불가 → 감시 수에만 포함
                            break;
                    }
                }
            }
        }

        [Fact]
        public void Catalog_Covers_Every_Arena_Feed_Symbol_And_Resolves_Names_Without_Fabrication()
        {
            foreach (long s in KrxArenaQuoteFeeder.SyntheticSymbols) Assert.True(PlutoMultiAssetUniverseCatalog.Contains(PlutoScreenMarket.Krx, s), s.ToString());
            for (int i = 0; i < UsTechUniverse.Count; i++) Assert.True(PlutoMultiAssetUniverseCatalog.Contains(PlutoScreenMarket.Us, UsTechUniverse.Symbol(i)));
            for (int i = 0; i < CryptoArenaUniverse.Count; i++) Assert.True(PlutoMultiAssetUniverseCatalog.Contains(PlutoScreenMarket.Crypto, CryptoArenaUniverse.Symbol(i)));
            Assert.Equal("삼성전자", PlutoMultiAssetUniverseCatalog.NameOf(PlutoScreenMarket.Krx, 5930));
            Assert.Equal("NVIDIA", PlutoMultiAssetUniverseCatalog.NameOf(PlutoScreenMarket.Us, UsTechUniverse.Pack("NVDA")));
            Assert.Equal("비트코인", PlutoMultiAssetUniverseCatalog.NameOf(PlutoScreenMarket.Crypto, Btc));
            Assert.Null(PlutoMultiAssetUniverseCatalog.NameOf(PlutoScreenMarket.Krx, 999_999));
            Assert.False(PlutoMultiAssetUniverseCatalog.Contains(PlutoScreenMarket.Krx, Btc));   // 시장 교차 = 미포함
            Assert.False(PlutoMultiAssetUniverseCatalog.Contains(PlutoScreenMarket.Crypto, 0));
            Assert.False(PlutoMultiAssetUniverseCatalog.Contains((PlutoScreenMarket)9, 5930));
        }

        // ── ② 코인 스코어러 ───────────────────────────────────────────────────

        [Theory]
        [InlineData(-1, 15, 0)]
        [InlineData(0, 15, 25)]
        [InlineData(5, 0, 20)]
        [InlineData(6, 10, 19)]
        [InlineData(10, 5, 17)]
        [InlineData(25, 4, 10)]
        [InlineData(49, 15, 9)]
        [InlineData(50, 15, 5)]
        [InlineData(500, 0, 0)]
        public void Crypto_Liquidity_Points_Table(int spreadBps, int tiers, int expected)
            => Assert.Equal(expected, PlutoDiscoveryScorer.LiquidityPoints(spreadBps, tiers));

        [Fact]
        public void Crypto_Score_Uses_Four_Crypto_Factors_And_Ignores_Valuation()
        {
            var x = Coin(Btc, 60, 60, 60);
            Assert.Equal(0, x.SpreadBps);                                          // 1,000원 / 1.5억 = 0bp
            var s = PlutoDiscoveryScorer.Score(in x, default);
            Assert.Equal((25, 20, 20, 20), (s.ValuationPts, s.TechnicalPts, s.MicroPts, s.MacroPts));   // 유동성 · 스퀴즈 · CVD · 뎁스
            Assert.Equal(85, s.Total);
            Assert.Equal(2, s.Coverage);
            Assert.Equal(PlutoScreenRisk.None, s.Risks);
            Assert.True(s.Recommendable);

            var over = Coin(Btc, 60, 60, 60, hasValuation: true, grade: PlutoValuationGrade.ExtremeBubble);   // 코인 = 밸류에이션 판독 무시
            Assert.Equal(PlutoScreenRisk.None, PlutoDiscoveryScorer.RisksOf(in over, default));
            Assert.Equal(85, PlutoDiscoveryScorer.Score(in over, default).Total);

            var bare = Coin(Btc, null, null, null);
            var sb = PlutoDiscoveryScorer.Score(in bare, default);
            Assert.True(sb.Insufficient);                                          // 기술 · 미시구조 판독 전무 = 근거 부족
            Assert.Equal(20, sb.ValuationPts);                                     // 유동성만(뎁스 호가 수 = 미시구조 판독 없으면 0)
            Assert.False(sb.Recommendable);

            var noBid = Coin(Btc, 60, 60, 60, bid: 0);
            Assert.Equal(-1, noBid.SpreadBps);
            Assert.Equal(0, PlutoDiscoveryScorer.Score(in noBid, default).ValuationPts);   // 매수 1호가 미판독 = 유동성 0점(fail-closed)
        }

        [Fact]
        public void Crypto_Devil_Gate_Spread_Plus_Bearish_Cvd_Vetoes_With_Crypto_Label()
        {
            var thin = Coin(Xrp, 60, 60, 60, ask: 3_505 * S, bid: 3_480 * S);
            Assert.Equal(71, thin.SpreadBps);
            Assert.Equal(PlutoScreenRisk.Overvalued, PlutoDiscoveryScorer.RisksOf(in thin, default));
            Assert.False(PlutoDiscoveryScorer.Score(in thin, default).Vetoed);    // 1건 = 감시 통과
            var s = PlutoDiscoveryScorer.Score(Coin(Xrp, 60, -100, 60, ask: 3_505 * S, bid: 3_480 * S, div: PlutoDivergenceType.Bearish), default);
            Assert.Equal(PlutoScreenRisk.Overvalued | PlutoScreenRisk.CvdBearish, s.Risks);
            Assert.True(s.Vetoed);
            string text = PlutoDiscoveryScorer.RiskText(s.Risks, PlutoScreenMarket.Crypto);
            Assert.Contains("반대 논리 2건 중첩 — 탈락", text);
            Assert.Contains("호가 스프레드 과대(유동성 고갈)", text);
            Assert.DoesNotContain("고평가", text);
            Assert.Contains("고평가 경계", PlutoDiscoveryScorer.RiskText(PlutoScreenRisk.Overvalued));   // 주식 문구 불변
            var bear = Coin(Btc, 60, 60, 60, regime: MarketRegime.PanicLiquidation, trap: true);
            Assert.True(PlutoDiscoveryScorer.Score(in bear, default).Vetoed);    // 공통 게이트(국면 · 뎁스 함정) 코인에도 적용
        }

        [Fact]
        public void Crypto_Reasons_Are_Crypto_Factor_Texts()
        {
            var x = Coin(Btc, 60, 60, 60);
            var s = PlutoDiscoveryScorer.Score(in x, default);
            var r = PlutoDiscoveryScorer.Reasons(in x, default, in s);
            Assert.Equal(3, r.Length);
            Assert.StartsWith("💧 유동성 스프레드 0.00% · 뎁스 15호가 [25/25]", r[0]);
            Assert.StartsWith("🌀 ", r[1]);
            Assert.StartsWith("📊 ", r[2]);
            var partial = Coin(Btc, 60, null, null);
            var rp = PlutoDiscoveryScorer.Reasons(in partial, default, PlutoDiscoveryScorer.Score(in partial, default));
            Assert.Equal("판독 대기 팩터(워밍업 · 0점 반영)", rp[2]);
            Assert.Equal("150,001,000원", PlutoDiscoveryScorer.PriceText(PlutoScreenMarket.Crypto, 150_001_000 * S));
            Assert.Equal("0.0123원", PlutoDiscoveryScorer.PriceText(PlutoScreenMarket.Crypto, 1_230_000));
        }

        // ── ③ 스크리너 ───────────────────────────────────────────────────────

        private static PlutoMarketScreener CryptoScreener(params PlutoScreenInputs[] universe)
        {
            var s = new PlutoMarketScreener(new PlutoRecommendationBoard(), null, () => T0);
            s.AddUniverse(PlutoScreenMarket.Crypto, dest => { universe.AsSpan().CopyTo(dest); return universe.Length; }, UpbitCryptoArenaEngine.DefaultCapitalWon);
            return s;
        }

        [Fact]
        public void Screener_Publishes_Crypto_Top_With_Raw_Qty_And_Three_Market_Tallies()
        {
            long ethAsk = 5_501_000 * S;
            using var s = CryptoScreener(
                Coin(Btc, 60, 60, 60),
                Coin(Eth, 100, 100, 100, ask: ethAsk, bid: 5_500_000 * S),
                Coin(Xrp, 60, -100, 60, ask: 3_505 * S, bid: 3_480 * S, div: PlutoDivergenceType.Bearish),
                Coin(Sol, null, null, null, ask: 280_100 * S, bid: 280_000 * S));
            var snap = s.Step();
            var c = snap.Of(PlutoScreenMarket.Crypto);
            Assert.Equal(2, c.Count);
            Assert.Equal((Eth, 1, 100, "KRW-ETH"), (c[0].Symbol, c[0].Rank, c[0].Score.Total, c[0].Code));
            Assert.Equal((Btc, 2, 85, "KRW-BTC"), (c[1].Symbol, c[1].Rank, c[1].Score.Total, c[1].Code));
            Assert.Equal(new PlutoScreenTally(4, 1, 1, 0, 120, 2), snap.TallyOf(PlutoScreenMarket.Crypto));

            long qty = c[0].RecommendedQty;                                        // 코인 × 10^8 raw
            long budget = UpbitCryptoArenaEngine.DefaultCapitalWon * PlutoDynamicSizer.WeightBp(100, 0, 0) / 10000;
            Assert.True(qty > 0);
            Assert.True((Int128)qty * ethAsk / S / S <= budget);                   // 내림 — 예산 초과 0
            Assert.True((Int128)(qty + 1) * ethAsk / S / S + 1 > budget);
            Assert.Equal(qty, PlutoMarketScreener.RecommendedQty(PlutoScreenMarket.Crypto, UpbitCryptoArenaEngine.DefaultCapitalWon, 100, ethAsk));
            Assert.Equal(0, PlutoMarketScreener.RecommendedQty(PlutoScreenMarket.Crypto, 0, 100, ethAsk));
            Assert.Equal(PlutoMarketScreener.RecommendedQty(1_000_000, 80, 50_000), PlutoMarketScreener.RecommendedQty(PlutoScreenMarket.Krx, 1_000_000, 80, 50_000));

            Assert.Equal(420, snap.TotalMonitored);                                // 미등록 시장도 감시 카탈로그 수 집계
            Assert.Equal(4, snap.TotalScanned);
            Assert.Equal(2, snap.TotalPassed);
            Assert.Empty(snap.Of(PlutoScreenMarket.Krx));
            Assert.True(snap.TryFind(PlutoScreenMarket.Crypto, Eth, out int score, out int rank));
            Assert.Equal((100, 1), (score, rank));
            Assert.False(snap.TryFind(PlutoScreenMarket.Krx, Eth, out _, out _));
            Assert.Contains("반대 논리 0건", c[0].RiskText);
        }

        [Fact]
        public void Snapshot_Keeps_Legacy_Two_Market_Constructor_And_Bounds_Crypto()
        {
            var legacy = new PlutoRecommendationSnapshot(1, T0, Array.Empty<PlutoRecommendation>(), default, Array.Empty<PlutoRecommendation>(), default);
            Assert.Empty(legacy.Of(PlutoScreenMarket.Crypto));
            Assert.Equal(default, legacy.TallyOf(PlutoScreenMarket.Crypto));
            Assert.Equal(3, PlutoRecommendationSnapshot.Markets);
            Assert.Throws<ArgumentOutOfRangeException>(() => new PlutoRecommendationSnapshot(1, T0, Array.Empty<PlutoRecommendation>(), default,
                Array.Empty<PlutoRecommendation>(), default, new PlutoRecommendation[6], default));
            Assert.Throws<ArgumentNullException>(() => new PlutoRecommendationSnapshot(1, T0, Array.Empty<PlutoRecommendation>(), default,
                Array.Empty<PlutoRecommendation>(), default, null!, default));
        }

        // ── ④ 업비트 아레나 유니버스 ──────────────────────────────────────────

        [Fact]
        public void Crypto_Arena_Universe_Feeds_Only_Table_Markets_With_Bid()
        {
            var e = new UpbitCryptoArenaEngine();
            long t = (new DateTime(2026, 10, 25, 1, 0, 0, DateTimeKind.Utc).Ticks - DateTime.UnixEpoch.Ticks) / 10;
            for (int i = 0; i < 5; i++) { t += 100_000; e.OnQuote(Btc, 150_000_000 * S, 150_001_000 * S, S, S, t); }
            var scan = PlutoArenaUniverse.Crypto(e);
            var dest = new PlutoScreenInputs[PlutoMarketScreener.MaxUniverse];
            int n = scan(dest);
            Assert.True(n >= 1);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(PlutoScreenMarket.Crypto, dest[i].Market);
                Assert.True(CryptoArenaUniverse.IndexOf(dest[i].Symbol) >= 0);
                Assert.False(dest[i].HasValuation);                                // 코인 = 밸류에이션 없음
            }
            int bi = Array.FindIndex(dest, 0, n, x => x.Symbol == Btc);
            Assert.True(bi >= 0);
            Assert.Equal(150_000_000 * S, dest[bi].Bid);
            Assert.Equal(150_001_000 * S, dest[bi].Price);
            Assert.Throws<ArgumentNullException>(() => PlutoArenaUniverse.Crypto(null!));

            using var screener = new PlutoMarketScreener(new PlutoRecommendationBoard(), null, () => T0);
            screener.AddUniverse(PlutoScreenMarket.Crypto, scan, e.CapitalWon);
            var snap = screener.Step();
            Assert.True(snap.TallyOf(PlutoScreenMarket.Crypto).Scanned >= 1);
            foreach (var r in snap.Of(PlutoScreenMarket.Crypto)) Assert.True(CryptoArenaUniverse.IndexOf(r.Symbol) >= 0);   // 추천 ⊂ 시세 유니버스
        }

        // ── ⑤ 포트 · 페이지 · 호스트 ──────────────────────────────────────────

        [Fact]
        public void Adapter_Maps_Crypto_Slot_Cards_And_Monitor_Totals()
        {
            using var s = CryptoScreener(Coin(Btc, 60, 60, 60));
            s.Step();
            var v = new PlutoRecommendationPortAdapter(s).Current;
            var card = Assert.Single(v.Crypto.Cards);
            Assert.Equal(PlutoRecoMarket.Crypto, card.Market);
            Assert.Equal("KRW-BTC", card.Code);
            Assert.Equal("BTC", card.Coin);
            Assert.Equal("150,001,000원", card.PriceText);
            Assert.EndsWith(" BTC", card.QtyText);
            Assert.Contains("재무 안전마진 해당 없음", card.MosText);
            Assert.Same(v.Crypto, v.Of(PlutoRecoMarket.Crypto));
            Assert.Equal((120, 1, 119, 1), (v.Crypto.Monitored, v.Crypto.Scanned, v.Crypto.AwaitingFeed, v.Crypto.Passed));
            Assert.Equal(420, v.TotalMonitored);
            Assert.Equal(1, v.TotalPassed);
            Assert.Equal("📡 3대 시장 전수 감시 중: KRX 200개 | US 100개 | Upbit 120개", v.MonitorBadgeText);
            Assert.Equal("🪙 가상자산 (업비트)", PlutoRecoMarket.Label(PlutoRecoMarket.Crypto));
            Assert.Same(PlutoRecoMarketView.None, PlutoRecoBoardView.None.Crypto);
        }

        private sealed class FixedRecoPort : IPlutoRecommendationPort
        {
            public FixedRecoPort(PlutoRecoBoardView v) => Current = v;
            public bool IsWired => true;
            public PlutoRecoBoardView Current { get; }
        }

        private static PlutoRecommendationCard Card(int market, long sym, string code, string name, int score, long qty) =>
            new(market, 1, sym, code, name, score, 22, 20, 17, 20, Array.AsReadOnly(new[] { "💧 근거1", "🌀 근거2", "📊 근거3" }),
                false, 0, "150,001,000원", "", false, qty, 0, "😈 악마의 변호인: 반대 논리 0건 — 통과");

        [Fact]
        public void DualArenaPage_Crypto_Tab_Monitor_Badge_And_OneClick_Binds_Crypto_Ticket()
        {
            var empty = Array.AsReadOnly(Array.Empty<PlutoRecommendationCard>());
            var view = new PlutoRecoBoardView(true, 4, new DateTime(T0, DateTimeKind.Utc),
                new PlutoRecoMarketView(empty, 5, 0, 5, 0, 200, 0), new PlutoRecoMarketView(empty, 5, 0, 5, 0, 100, 0))
            {
                Crypto = new PlutoRecoMarketView(Array.AsReadOnly(new[] { Card(PlutoRecoMarket.Crypto, Btc, "KRW-BTC", "비트코인", 79, 1_234_000) }), 4, 1, 1, 1, 120, 1),
            };
            using var ctx = new Bunit.TestContext();
            var krx = new KrxArenaEngine();
            ctx.Services.AddSingleton<IKrxArenaPort>(new KrxArenaPortAdapter(krx, new KrxArenaQuoteFeeder(krx)));
            var us = new UsTechArenaEngine();
            ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(us, new UsArenaQuoteFeeder(us)));
            var ce = new UpbitCryptoArenaEngine();
            var cf = new CryptoArenaQuoteFeeder(ce);
            Assert.True(cf.SelectSource(CryptoArenaFeed.Synthetic));                // 합성 시세 → 코인 휴먼 티켓 렌더
            long utc = new DateTime(2026, 10, 25, 1, 0, 0).Ticks;
            for (int i = 0; i < 3; i++) cf.Pump(utc + i * TimeSpan.TicksPerSecond);
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(ce, cf));
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);
            ctx.Services.AddSingleton<IPlutoLearningPort>(UnwiredPlutoLearningPort.Instance);
            ctx.Services.AddSingleton<IPlutoRecommendationPort>(new FixedRecoPort(view));
            var cut = ctx.RenderComponent<DualArenaPage>();

            Assert.Equal(3, cut.FindAll(".da-reco-tab").Count);                    // 국내 · 미국 · 코인 3탭
            Assert.Contains("🪙 가상자산 (업비트) (1)", cut.Find(".da-reco-tab[data-reco-tab='crypto']").TextContent);
            Assert.Equal("📡 3대 시장 전수 감시 중: KRX 200개 | US 100개 | Upbit 120개", cut.Find(".da-reco-watch").TextContent.Trim());
            Assert.Equal("420", cut.Find(".da-reco-monitor").GetAttribute("data-monitored"));
            Assert.Equal("1", cut.Find(".da-reco-monitor").GetAttribute("data-passed"));
            Assert.Empty(cut.FindAll(".da-reco-card"));                            // 국내 모드 = 국내 탭(추천 0)
            Assert.Contains("감시 200종목 · 피드 판독 대기 195 · 통과 0", cut.Find(".da-reco-cover").TextContent);

            cut.Find(".da-reco-tab[data-reco-tab='crypto']").Click();
            var cardEl = cut.Find(".da-reco-card[data-code='KRW-BTC']");
            Assert.Contains("💧 22", cardEl.QuerySelector(".da-reco-pts")!.TextContent);   // 코인 팩터 칩
            Assert.Contains("🧱 20", cardEl.QuerySelector(".da-reco-pts")!.TextContent);
            Assert.Contains("(0.01234 BTC)", cut.Find(".da-reco-card[data-code='KRW-BTC'] .da-reco-load").TextContent);
            Assert.Contains("감시 120종목 · 피드 판독 대기 116 · 통과 1", cut.Find(".da-reco-cover").TextContent);

            cut.Find(".da-reco-card[data-code='KRW-BTC'] .da-reco-load").Click();
            Assert.Contains("on", cut.Find(".da-mode-tab[data-mode='crypto']").ClassList);
            Assert.Equal("KRW-BTC", cut.Find("select.da-mkt").GetAttribute("value"));
            Assert.Equal(0.01234m, decimal.Parse(cut.Find("input.da-cqty").GetAttribute("value")!, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Contains("🌟 플루토 추천 담기: KRW-BTC 0.01234 BTC", cut.Find(".da-msg").TextContent);
            Assert.Contains("휴먼 티켓에 담았습니다", cut.Find(".da-reco-msg").TextContent);
        }

        [Fact]
        public void Host_Wires_Crypto_Universe_Into_Screener()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string form = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("PlutoArenaUniverse.Crypto(cryptoArena), cryptoArena.CapitalWon)", form);
            Assert.Contains("PlutoScreenMarket.Crypto", form);
            string catalog = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "StockManager", "Engines", "Cognitive", "Screening", "PlutoMultiAssetUniverseCatalog.cs"));
            Assert.DoesNotContain("HttpClient", catalog);                          // 카탈로그 = 정적 메타데이터(네트워크 0)
            Assert.DoesNotContain("QuantumVault", catalog);
        }

        // ── ⑥ δ=0 ────────────────────────────────────────────────────────────

        private static PlutoRecommendation Rec(PlutoScreenMarket m, long sym, int rank, int pts) =>
            new(m, rank, sym, "x", 50_000, new PlutoDiscoveryScore(pts, pts, pts, pts, PlutoScreenRisk.None, 2),
                Array.AsReadOnly(new[] { "a", "b", "c" }), false, 0, 0, false, 10, "😈");

        [Fact]
        public void Three_Market_TryFind_And_Totals_During_Concurrent_Publishes_Are_ZeroAlloc_And_Untorn()
        {
            PlutoRecommendationSnapshot Snap(long gen, int pts) => new(gen, T0,
                new[] { Rec(PlutoScreenMarket.Krx, 5930, 1, pts) }, new PlutoScreenTally(1, 0, 0, 0, 200, 1),
                new[] { Rec(PlutoScreenMarket.Us, UsTechUniverse.Pack("NVDA"), 1, pts) }, new PlutoScreenTally(1, 0, 0, 0, 100, 1),
                new[] { Rec(PlutoScreenMarket.Crypto, Btc, 1, pts) }, new PlutoScreenTally(1, 0, 0, 0, 120, 1));
            var a = Snap(1, 20);
            var b = Snap(2, 10);
            var board = new PlutoRecommendationBoard();
            board.Publish(a);
            long nvda = UsTechUniverse.Pack("NVDA");
            int stop = 0, torn = 0;
            var writer = new Thread(() =>
            {
                bool flip = false;
                while (Volatile.Read(ref stop) == 0) { board.Publish(flip ? a : b); flip = !flip; }
            }) { IsBackground = true };
            writer.Start();
            void Reads(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    var cur = board.Current;
                    bool ok = cur.TryFind(PlutoScreenMarket.Krx, 5930, out int s1, out _)
                        & cur.TryFind(PlutoScreenMarket.Us, nvda, out int s2, out _)
                        & cur.TryFind(PlutoScreenMarket.Crypto, Btc, out int s3, out _);
                    if (!ok || s1 != s2 || s2 != s3 || (s1 != 80 && s1 != 40) || cur.TotalMonitored != 420 || cur.TotalPassed != 3) torn++;
                    if (!board.TryFind(PlutoScreenMarket.Crypto, Btc, out _, out _)) torn++;
                }
            }
            try
            {
                Reads(10_000);
                long bytes = MinAllocated(() => Reads(100_000));
                Assert.Equal(0, bytes);
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                writer.Join();
            }
            Assert.Equal(0, torn);
        }

        [Fact]
        public void Catalog_Contains_Is_ZeroAlloc()
        {
            long nvda = UsTechUniverse.Pack("NVDA");
            int hits = 0;
            void Probe(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    if (PlutoMultiAssetUniverseCatalog.Contains(PlutoScreenMarket.Krx, 5930)) hits++;
                    if (PlutoMultiAssetUniverseCatalog.Contains(PlutoScreenMarket.Us, nvda)) hits++;
                    if (PlutoMultiAssetUniverseCatalog.Contains(PlutoScreenMarket.Crypto, Btc)) hits++;
                    if (PlutoMultiAssetUniverseCatalog.Contains(PlutoScreenMarket.Krx, 999_999)) hits--;
                }
            }
            Probe(10_000);
            hits = 0;
            Assert.Equal(0, MinAllocated(() => Probe(100_000)));
            Assert.True(hits > 0);
        }
    }
}
