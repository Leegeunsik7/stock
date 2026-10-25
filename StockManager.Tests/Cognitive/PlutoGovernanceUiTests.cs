// ============================================================================
// PlutoGovernanceUiTests — v12.18.68-DEV: 플루토 프로 거버너 UI(토글 · 국면 뱃지 · 감점 태그 · 자가 회고) · 포트 확장 · 기동 설정
//
//  §1 DTO — ArenaRegimeCode 대표 국면(패닉 우선 · 다수결 · 동률 = 박스 · 판정 0 = 판정 전 · 미상 값 정규화) · 뱃지 문구
//  §2 어댑터 — SetGovernanceEnabled ↔ 엔진 플래그 · 스냅샷 IsGovernanceEnabled/CurrentRegime · 패닉 국면 투영 · 근거 카드 감점 태그/자가 회고 1:1 · 쿨다운 남은 분
//  §3 페이지 — 토글 OFF → ON(3대 엔진 동시) → OFF · 일부 ON → 전부 ON · 국면 뱃지(패닉차단 · 꺼짐 = 표시만) · [거시역풍 감점 -15] · 자가 회고 · [15분 쿨다운 중]
//  §4 경계 — 페이지/포트 엔진 참조 0 · 포트 기본 구현 = 미지원(정지) · 기동 설정 Arena.PlutoGovernanceEnabled 기본 켜짐 · 토글 + 정산 루프 δ=0
// ============================================================================
using System;
using System.IO;
using System.Linq;
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
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoGovernanceUiTests
    {
        private const long Samsung = 5930;
        private static readonly DateTime Open10 = new(2026, 10, 22, 10, 0, 0);   // 목요일 10:00 KST
        private readonly ITestOutputHelper _out;
        private DateTime _t = Open10;

        public PlutoGovernanceUiTests(ITestOutputHelper o) => _out = o;

        private void Q(KrxArenaEngine e, long bid, long ask, long bidQty = 1000, long askQty = 1000, long vol = 100)
        {
            _t = _t.AddMilliseconds(100);
            e.OnQuote(Samsung, bid, ask, bidQty, askQty, vol, _t);
        }

        /// <summary>정산 펌프 1회(브리지 출처 · 호가 없음 = Drain) → Feed.Pumped = true(페이지 대결 영역 렌더).</summary>
        private static KrxArenaQuoteFeeder Pumped(KrxArenaEngine e)
        {
            var f = new KrxArenaQuoteFeeder(e);
            f.Pump(Open10.AddHours(-9).Ticks);
            return f;
        }

        private static (Bunit.TestContext Ctx, IRenderedComponent<DualArenaPage> Cut) Render(IKrxArenaPort krx, IUsArenaPort us, ICryptoArenaPort crypto)
        {
            var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton(krx);
            ctx.Services.AddSingleton(us);
            ctx.Services.AddSingleton(crypto);
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);   // [v12.18.69-DEV] 토글 영속화 포트(저장소 미결선 null 객체)
            ctx.Services.AddSingleton<IPlutoLearningPort>(UnwiredPlutoLearningPort.Instance);   // [v12.18.77-DEV] 학습 엔진 미결선
            ctx.Services.AddSingleton<IPlutoRecommendationPort>(UnwiredPlutoRecommendationPort.Instance);   // [v12.18.78-DEV] 추천 엔진 미결선
            return (ctx, ctx.RenderComponent<DualArenaPage>());
        }

        /// <summary>평온 10틱 → −3% × 3 투매(매도 잔량 99%) → 침체 5틱 → 반등 틱(거시 +10000 · 매수 잔량 99%).</summary>
        private void PanicBounce(KrxArenaEngine e)
        {
            e.SetOvernightMacro(0);
            for (int i = 0; i < 10; i++) Q(e, 100_000, 100_100);
            Q(e, 97_000, 97_100, 100, 9900); Q(e, 94_100, 94_200, 100, 9900); Q(e, 91_300, 91_400, 100, 9900);
            for (int i = 0; i < 5; i++) Q(e, 91_300, 91_400, 100, 9900);
            e.SetOvernightMacro(10000);
            Q(e, 91_300, 91_400, 9900, 100);
        }

        /// <summary>진입(Spike) → 손절 → 침체 10틱.</summary>
        private void LosingRoundTrip(KrxArenaEngine e, long bid, int macro, bool firstSurge)
        {
            e.SetOvernightMacro(macro);
            Q(e, bid, bid + 100, 9000, 1000, firstSurge ? 1000 : 100);
            Assert.True(e.TryReadSnapshot(out var s) && s.Ai.OpenPositions == 1, "진입 실패");
            var buf = new AiTradeRationale[KrxArenaEngine.RationaleCapacity];
            var entry = buf[e.CopyRationales(buf) - 1];
            long crash = entry.StopLossPrice - 600;
            Q(e, crash, crash + 100);
            Assert.True(e.TryReadSnapshot(out s) && s.Ai.OpenPositions == 0, "손절 실패");
            for (int i = 0; i < 10; i++) Q(e, crash, crash + 100);
        }

        /// <summary>가동 KRX: 거시 역풍(−15) 진입 → 손절 회고 → 2연패(쿨다운).</summary>
        private (KrxArenaEngine Engine, KrxArenaPortAdapter Port) GovernedLosingKrx()
        {
            var e = new KrxArenaEngine();
            e.EnableGovernance(true);
            var port = new KrxArenaPortAdapter(e, Pumped(e));
            for (int i = 0; i < KrxArenaEngine.WarmupTicks + 1; i++) Q(e, 70_000, 70_100);
            e.SetMacroHeadwind(20, 0);
            LosingRoundTrip(e, 70_100, 3000, true);
            e.SetMacroHeadwind(0, 0);
            var last = port.RecentRationales(1)[0];
            LosingRoundTrip(e, last.PriceWon + 100, 10000, false);
            return (e, port);
        }

        // ── §1 DTO ──────────────────────────────────────────────────────────────

        [Fact]
        public void RegimeCode_Aggregate_Panic_First_Then_Majority()
        {
            const int B = ArenaRegimeCode.TrendingBull, D = ArenaRegimeCode.TrendingBear, R = ArenaRegimeCode.ChoppyRange, P = ArenaRegimeCode.PanicLiquidation, U = ArenaRegimeCode.Unknown;
            Assert.Equal(U, ArenaRegimeCode.Aggregate(ReadOnlySpan<int>.Empty));
            Assert.Equal(U, ArenaRegimeCode.Aggregate(new[] { U, U, 99 }));
            Assert.Equal(P, ArenaRegimeCode.Aggregate(new[] { B, B, B, P }));
            Assert.Equal(B, ArenaRegimeCode.Aggregate(new[] { B, B, D, U }));
            Assert.Equal(D, ArenaRegimeCode.Aggregate(new[] { D, D, R }));
            Assert.Equal(R, ArenaRegimeCode.Aggregate(new[] { B, D }));
            Assert.Equal(R, ArenaRegimeCode.Aggregate(new[] { R }));
            Assert.Equal(U, ArenaRegimeCode.Normalize(-1));
            Assert.Equal("🚨 패닉차단", ArenaRegimeCode.Badge(P));
            Assert.Equal("📈 상승추세", ArenaRegimeCode.Badge(B));
            Assert.Equal("📉 하락추세", ArenaRegimeCode.Badge(D));
            Assert.Equal("⏸️ 박스권", ArenaRegimeCode.Badge(R));
            Assert.Equal("과열 감점 -35", new ArenaPenaltyTag("과열", -35).Text);
            // 엔진 국면 값 = 포트 코드 1:1
            foreach (StockManager.Engines.Cognitive.Regime.MarketRegime r in Enum.GetValues(typeof(StockManager.Engines.Cognitive.Regime.MarketRegime)))
                Assert.Equal((int)r, ArenaGovernanceMapper.Regime(r));
        }

        // ── §2 어댑터 ───────────────────────────────────────────────────────────

        [Fact]
        public void Adapters_Toggle_Engine_Flag_And_Project_Snapshot()
        {
            var krx = new KrxArenaEngine();
            var us = new UsTechArenaEngine();
            var cx = new UpbitCryptoArenaEngine();
            IKrxArenaPort kp = new KrxArenaPortAdapter(krx);
            IUsArenaPort up = new UsArenaPortAdapter(us);
            ICryptoArenaPort cp = new CryptoArenaPortAdapter(cx);
            Assert.False(kp.Governance.Enabled || up.Governance.Enabled || cp.Governance.Enabled);
            Assert.True(kp.SetGovernanceEnabled(true) && up.SetGovernanceEnabled(true) && cp.SetGovernanceEnabled(true));
            Assert.True(krx.GovernanceEnabled && us.GovernanceEnabled && cx.GovernanceEnabled);
            Assert.True(kp.Governance.Enabled && up.Governance.Enabled && cp.Governance.Enabled);
            Assert.True(kp.TryRead(out var kv) && kv.IsGovernanceEnabled);
            Assert.True(up.TryRead(out var uv) && uv.IsGovernanceEnabled);
            Assert.True(cp.TryRead(out var cv) && cv.IsGovernanceEnabled);
            Assert.Equal(ArenaRegimeCode.Unknown, kv.CurrentRegime);              // 시세 0 = 판정 전
            Assert.True(kp.SetGovernanceEnabled(false));
            Assert.False(krx.GovernanceEnabled);
            Assert.True(kp.TryRead(out kv) && !kv.IsGovernanceEnabled);
        }

        [Fact]
        public void Adapter_Projects_Panic_Regime_And_Block_Count()
        {
            var e = new KrxArenaEngine();
            e.EnableGovernance(true);
            var port = new KrxArenaPortAdapter(e);
            PanicBounce(e);
            var g = port.Governance;
            Assert.Equal(ArenaRegimeCode.PanicLiquidation, g.Regime);
            Assert.Equal(1L, g.PanicBlocks);
            Assert.True(port.TryRead(out var v));
            Assert.Equal(ArenaRegimeCode.PanicLiquidation, v.CurrentRegime);
            Assert.Empty(port.RecentRationales(10));
        }

        [Fact]
        public void Adapter_Rationale_Cards_Bind_Penalty_Tags_PostMortem_And_Cooldown()
        {
            var (e, port) = GovernedLosingKrx();
            var cards = port.RecentRationales(10);                                // 최신 → 과거
            Assert.Equal(4, cards.Count);
            var entry1 = cards[3];
            var exit1 = cards[2];
            var g1 = Assert.IsType<ArenaGovernanceCard>(entry1.Governance);
            Assert.False(g1.IsExit);
            var pen = Assert.Single(g1.Penalties);
            Assert.Equal(("거시역풍", -15), (pen.Label, pen.Points));
            Assert.Equal(g1.RawConfidence - 15, g1.Confidence);
            Assert.Equal(entry1.ConfidenceScore, g1.Confidence);
            Assert.InRange(g1.WeightBp, PlutoDynamicSizer.MinWeightBp, PlutoDynamicSizer.MaxWeightBp);
            var x1 = Assert.IsType<ArenaGovernanceCard>(exit1.Governance);
            Assert.True(x1.IsExit);
            Assert.False(x1.PostMortemWin);
            Assert.Contains("플루토 자가 회고", x1.PostMortem);
            Assert.Contains($"진입 근거 #{entry1.Seq}", x1.PostMortem);           // 1:1
            Assert.Contains("반대 논리", x1.PostMortem);                           // 감점 진입의 패착 교훈
            Assert.Empty(cards[1].Governance!.Penalties);

            var gov = port.Governance;
            Assert.Equal(2, gov.LossStreak);
            Assert.True(gov.InCooldown);
            Assert.InRange(gov.CooldownRemainingMinutes, 14, 15);
            Assert.NotNull(gov.CooldownUntilKst);

            e.EnableGovernance(false);                                            // 미가동 엔진 근거 = 거버넌스 null
            var plain = new KrxArenaEngine();
            var plainPort = new KrxArenaPortAdapter(plain);
            for (int i = 0; i < KrxArenaEngine.WarmupTicks + 1; i++) Q(plain, 70_000, 70_100);
            plain.SetOvernightMacro(3000);
            Q(plain, 70_100, 70_200, 9000, 1000, 1000);
            Assert.Null(Assert.Single(plainPort.RecentRationales(5)).Governance);
        }

        // ── §3 페이지 ───────────────────────────────────────────────────────────

        [Fact]
        public void Page_Toggle_Syncs_All_Three_Arenas()
        {
            var krx = new KrxArenaEngine();
            var us = new UsTechArenaEngine();
            var cx = new UpbitCryptoArenaEngine();
            var (ctx, cut) = Render(new KrxArenaPortAdapter(krx), new UsArenaPortAdapter(us), new CryptoArenaPortAdapter(cx));
            using (ctx)
            {
                var btn = cut.Find(".da-gov-toggle");
                Assert.Equal("off", btn.GetAttribute("data-gov"));
                Assert.Contains("플루토 프로 거버너", btn.TextContent);
                Assert.Contains("(OFF)", btn.TextContent);

                btn.Click();
                Assert.True(krx.GovernanceEnabled && us.GovernanceEnabled && cx.GovernanceEnabled);
                btn = cut.Find(".da-gov-toggle");
                Assert.Equal("on", btn.GetAttribute("data-gov"));
                Assert.Equal("true", btn.GetAttribute("aria-checked"));
                Assert.Contains("(ON)", btn.TextContent);
                Assert.Contains("거버너 가동", cut.Find(".da-gov-msg").TextContent);

                btn.Click();
                Assert.False(krx.GovernanceEnabled || us.GovernanceEnabled || cx.GovernanceEnabled);
                Assert.Equal("off", cut.Find(".da-gov-toggle").GetAttribute("data-gov"));

                us.EnableGovernance(true);                                        // 일부 켜짐(호스트/외부 변경) → 다음 렌더 '일부 ON'
                cut.Find(".da-mode-tab[data-mode=us]").Click();
                Assert.Equal("mixed", cut.Find(".da-gov-toggle").GetAttribute("data-gov"));
                Assert.Contains("일부 ON", cut.Find(".da-gov-toggle").TextContent);
                cut.Find(".da-gov-toggle").Click();                               // 일부 → 전부 켬
                Assert.True(krx.GovernanceEnabled && us.GovernanceEnabled && cx.GovernanceEnabled);
            }
        }

        [Fact]
        public void Page_Renders_Panic_Badge_And_Idle_Badge_When_Off()
        {
            var e = new KrxArenaEngine();
            e.EnableGovernance(true);
            var port = new KrxArenaPortAdapter(e, Pumped(e));
            PanicBounce(e);
            var (ctx, cut) = Render(port, new UsArenaPortAdapter(new UsTechArenaEngine()), new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            using (ctx)
            {
                var badge = cut.Find(".da-krx .da-regime");
                Assert.Equal("panic", badge.GetAttribute("data-regime"));
                Assert.Contains("🚨 패닉차단", badge.TextContent);
                Assert.DoesNotContain("표시만", badge.TextContent);

                e.EnableGovernance(false);
                cut.Find(".da-mode-tab[data-mode=us]").Click();
                cut.Find(".da-mode-tab[data-mode=krx]").Click();
                badge = cut.Find(".da-krx .da-regime");
                Assert.Contains("거버너 꺼짐(표시만)", badge.TextContent);
                Assert.Contains("idle", badge.ClassList);
            }
        }

        [Fact]
        public void Page_Renders_Penalty_Tags_PostMortem_And_Cooldown_Badge()
        {
            var (_, port) = GovernedLosingKrx();
            var (ctx, cut) = Render(port, new UsArenaPortAdapter(new UsTechArenaEngine()), new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            using (ctx)
            {
                var cards = cut.FindAll(".da-krx .da-card");
                Assert.Equal(4, cards.Count);
                var pens = cut.FindAll(".da-pen[data-pen]");
                var pen = Assert.Single(pens);
                Assert.Equal("[거시역풍 감점 -15]", pen.TextContent.Trim());
                Assert.Single(cut.FindAll(".da-pen.none"));                       // 2번째 진입 = 반대 논리 없음
                var pms = cut.FindAll(".da-pm");
                Assert.Equal(2, pms.Count);
                Assert.All(pms, pm => Assert.Equal("loss", pm.GetAttribute("data-verdict")));
                Assert.All(pms, pm => Assert.Contains("플루토 자가 회고", pm.TextContent));
                Assert.Contains("확신도", cut.Find(".da-gov").TextContent);
                var cool = cut.Find(".da-krx .da-cool");
                Assert.Contains("15분 쿨다운 중", cool.TextContent);
                Assert.Contains("AI 연패 2회 · 비중 50% 축소", cut.Find(".da-krx .da-streak").TextContent);
                _out.WriteLine(pms[0].TextContent);
            }
        }

        // ── §4 경계 ─────────────────────────────────────────────────────────────

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        [Fact]
        public void PureShell_Page_And_Ports_Reference_No_Engine_And_Defaults_Are_Unsupported()
        {
            string page = RepoFile("MS2E.UI.Portable", "Pages", "Trading", "DualArenaPage.razor");
            string port = RepoFile("MS2E.UI.Portable", "Ports", "Trading", "ArenaGovernancePort.cs");
            foreach (var src in new[] { page, port })
            {
                Assert.DoesNotContain("StockManager.Engines", src);
                Assert.DoesNotContain("StockManager.Adapters", src);
                Assert.DoesNotContain("Arm(", src);
            }
            var members = typeof(IKrxArenaPort).GetMembers().Select(m => m.Name)
                .Concat(typeof(IUsArenaPort).GetMembers().Select(m => m.Name))
                .Concat(typeof(ICryptoArenaPort).GetMembers().Select(m => m.Name)).ToArray();
            Assert.Equal(3, members.Count(n => n == nameof(IKrxArenaPort.SetGovernanceEnabled)));
            Assert.DoesNotContain(members, n => n.Contains("Arm", StringComparison.Ordinal) || n.Contains("Broker", StringComparison.Ordinal) || n.Contains("Live", StringComparison.Ordinal));
            // 포트 DTO 는 엔진 타입 참조 0
            foreach (var t in new[] { typeof(ArenaGovernanceView), typeof(ArenaGovernanceCard), typeof(ArenaPenaltyTag) })
                Assert.All(t.GetProperties(), p => Assert.DoesNotContain("StockManager", p.PropertyType.FullName ?? ""));
        }

        [Fact]
        public void Host_Config_Defaults_Governance_On_For_All_Three_Arenas()
        {
            string form = RepoFile("StockManager", "Blazor", "BlazorHomeForm.cs");
            Assert.Contains("bool plutoGovernance = cfg.GetBool(\"Arena.PlutoGovernanceEnabled\", true);", form);
            Assert.Contains("krxArena.EnableGovernance(plutoGovernance);", form);
            Assert.Contains("usArena.EnableGovernance(plutoGovernance);", form);
            Assert.Contains("cryptoArena.EnableGovernance(plutoGovernance);", form);
        }

        [Fact]
        public void ZeroAlloc_Toggle_During_Settlement_100k()
        {
            var e = new KrxArenaEngine();
            IKrxArenaPort port = new KrxArenaPortAdapter(e);
            ulong x = 0x9E3779B97F4A7C15;
            long p0 = 70_000, i0 = 0, baseTicks = Open10.Ticks;

            void Run(int n)
            {
                for (int i = 0; i < n; i++, i0++)
                {
                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                    p0 = Math.Clamp(p0 + ((long)(x >> 8 & 3) - 1) * 100, 60_000, 80_000);
                    if ((i0 & 511) == 0) e.SetOvernightMacro((int)(x >> 20 & 0x1FFF) - 2048);
                    if ((i0 & 1023) == 0) port.SetGovernanceEnabled((i0 & 2048) == 0);   // UI 토글 = Volatile 플래그만
                    long bq = 100 + (long)(x >> 24 & 4095), aq = 100 + (long)(x >> 36 & 4095);
                    long vol = (x >> 48 & 15) == 0 ? 3000 : 50 + (long)(x >> 52 & 255);
                    e.OnQuote(Samsung, p0, p0 + 100, bq, aq, vol, new DateTime(baseTicks + i0 * TimeSpan.TicksPerMillisecond * 50));
                }
            }

            Run(20_000);
            long bytes = MinAllocated(() => Run(100_000));
            Assert.True(e.TryReadSnapshot(out var s));
            _out.WriteLine($"toggle + settlement 100k = {bytes}B · AI fills {s.Ai.Fills}");
            Assert.Equal(0, bytes);
        }
    }
}
