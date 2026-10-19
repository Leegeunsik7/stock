// ============================================================================
// FlashCrashInjectorTests — [v12.18.26-DEV] 아레나 극한 시나리오 인젝터(FlashCrashScenarioInjector) · 블라인드 결선 · 경고 인디케이터 적대적 검증
//
//  §1 10만 틱 고속 주입 — 시각 단조 · 버스트 ≥ 2,000틱/초 · 발동 이전 원천 동일 · 비대상 종목 원천 동일(시각만 이동) · 회복 후 대상 원천 복귀
//  §2 급락 — 깊이 도달 · 되돌림 · 매도 주도 체결 · 유동성 진공(매수 잔량 ≤ 30 · 2틱 구멍 · 스프레드 ≥ 2틱) · 여진 편차 연속/감쇠 / 급등 대칭
//  §3 블라인드 — 발동 3 ~ 5분 · 깊이 5 ~ 12% · 종류 반반 · 결정론 · 짧은 원천 = 미발동(통과) · 인자 검증(< 2,000틱/초 거부)
//  §4 δ=0 — 10만 틱 전 구간 판독 0B · 재생 엔진 버스트 재생(최대 배속) 0B
//  §5 재생 엔진 — 잠복(종류/종목 비공개) → 발동(공개) → 여진 국면 · 휴먼 시장가 진공 발동 · SetScenario 진행 중 거부 · 리셋 = 해제
//  §6 Fail-Closed — 인젝터 IL 참조 허용목록 · 헤드리스 UI(체크 · 잠복/발동/여진 인디케이터 · 진행 중 비활성)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class FlashCrashInjectorTests
    {
        private const long Freq = 10_000_000, Sec = Freq;
        private const long Capital = 100_000_000;
        private const int BaseTicks = 100_000;
        private static readonly long Minute = TimeSpan.TicksPerMinute;
        private readonly ITestOutputHelper _out;
        private long _now = 1_000 * Sec;

        public FlashCrashInjectorTests(ITestOutputHelper o) => _out = o;

        private static SyntheticTickSource Base(int count = BaseTicks) => SyntheticTickSource.Create(count);

        private static FlashCrashScenarioInjector Inject(ArenaTickSource b, int profile, long offset = 4 * TimeSpan.TicksPerMinute, int depthBp = 800)
            => new(b, profile, offset, seed: 7, depthBp: depthBp);

        private static ulong EntropyFor(int profile)
        {
            var probe = Base(1);
            for (ulong e = 1; ; e++)
                if (FlashCrashScenarioInjector.CreateBlind(probe, e).Profile == profile) return e;
        }

        private static long MinAllocated(Action body)
        {
            long min = long.MaxValue;
            for (int w = 0; w < 3; w++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return min;
        }

        // ═══ §1 10만 틱 고속 주입 ═══

        [Theory]
        [InlineData(ArenaScenarioProfile.FlashCrash)]
        [InlineData(ArenaScenarioProfile.ShortSqueeze)]
        public void Injection_Over_100k_Ticks_Is_Monotonic_Burst_Rate_And_Source_Preserving(int profile)
        {
            var b = Base();
            var inj = Inject(b, profile);
            Assert.True(inj.Injected);
            Assert.Equal(BaseTicks + FlashCrashScenarioInjector.DefaultBurstTicks, inj.Count);
            Assert.Equal(inj.BurstStart + inj.BurstTicks, inj.BurstEnd);
            long t0 = b.TimestampOf(0);
            Assert.InRange(inj.TimestampOf(inj.BurstStart) - t0, 4 * Minute, 4 * Minute + 3 * SyntheticTickSource.DefaultIntervalTicks);

            long prev = long.MinValue, shift = (long)inj.BurstTicks * FlashCrashScenarioInjector.DefaultBurstIntervalTicks;
            long recoverEnd = inj.TimestampOf(inj.BurstStart) + FlashCrashScenarioInjector.DefaultRecoveryTicks;
            int sameBefore = 0, sameOther = 0, recovered = 0, shifted = 0;
            for (int i = 0; i < inj.Count; i++)
            {
                long ts = inj.TimestampOf(i);
                Assert.True(ts >= prev, "시각 역행 @" + i);
                prev = ts;
                Assert.True(inj.Read(i, out var book));
                Assert.Equal(ts, book.TimestampTicks);
                Assert.True(book.BidPrice[0] > 0 && book.AskPrice[0] > book.BidPrice[0], "호가 역전 @" + i);
                Assert.True(inj.TryReadTrade(i, out var tr) && tr.Qty > 0 && tr.PriceWon > 0);
                if (i < inj.BurstStart)
                {
                    b.Read(i, out var o);
                    Assert.Equal(o.BidPrice[0], book.BidPrice[0]); Assert.Equal(o.AskQty[3], book.AskQty[3]); sameBefore++;
                }
                else if (i >= inj.BurstEnd)
                {
                    int j = i - inj.BurstTicks;
                    b.Read(j, out var o);
                    Assert.Equal(o.TimestampTicks + shift, ts);
                    if (book.SymbolCode != inj.TargetSymbol) { Assert.Equal(o.BidPrice[0], book.BidPrice[0]); Assert.Equal(o.AskPrice[9], book.AskPrice[9]); sameOther++; }
                    else if (o.TimestampTicks >= recoverEnd) { Assert.Equal(o.BidPrice[0], book.BidPrice[0]); recovered++; }
                    else if (o.BidPrice[0] != book.BidPrice[0]) shifted++;
                }
            }
            long span = inj.TimestampOf(inj.BurstEnd - 1) - inj.TimestampOf(inj.BurstStart);
            long tps = (long)(inj.BurstTicks - 1) * Sec / span;
            _out.WriteLine($"[{ArenaScenarioProfile.Label(profile)}] 전 {inj.Count:N0}틱 · 버스트 {inj.BurstTicks:N0}틱/{span / 10_000}ms = {tps:N0}틱/초 · 기준 {inj.AnchorPrice:N0} → 극값 {inj.ExtremePrice:N0} · 종료 편차 {inj.EndOffsetWon:N0} · 원천동일 {sameBefore}/{sameOther}/{recovered} · 여진 이동 {shifted}");
            Assert.True(tps >= 2_000, "버스트 " + tps + "틱/초 < 2,000");
            Assert.Equal(4_000, inj.BurstTicksPerSecond);
            Assert.Equal(inj.BurstStart, sameBefore);
            Assert.True(sameOther > 55_000 && recovered > 20_000 && shifted > 100, $"{sameOther}/{recovered}/{shifted}");
        }

        // ═══ §2 급락 · 급등 경로와 유동성 진공 ═══

        [Fact]
        public void FlashCrash_Plunges_To_Depth_With_Liquidity_Vacuum_And_Sell_Driven_Panic_Then_Retraces()
        {
            var b = Base();
            var inj = Inject(b, ArenaScenarioProfile.FlashCrash, depthBp: 800);
            long anchor = inj.AnchorPrice, tick = SyntheticTickSource.TickSize(anchor);
            Assert.True(inj.ExtremePrice <= anchor * 9_200 / 10_000 && inj.ExtremePrice >= anchor * 9_200 / 10_000 - tick, $"극값 {inj.ExtremePrice} 기준 {anchor}");
            int plunge = inj.BurstTicks * 3 / 5;
            long minBid = long.MaxValue, panicQty = 0;
            for (int k = 0; k < inj.BurstTicks; k++)
            {
                int i = inj.BurstStart + k;
                inj.Read(i, out var book);
                inj.TryReadTrade(i, out var tr);
                Assert.Equal(inj.TargetSymbol, book.SymbolCode);
                Assert.Equal(inj.TargetSymbol, tr.Symbol);
                minBid = Math.Min(minBid, book.BidPrice[0]);
                if (k < plunge)
                {
                    long t = SyntheticTickSource.TickSize(book.BidPrice[0]);
                    Assert.Equal(-1, tr.Side);                                               // 패닉 = 매도 주도
                    Assert.Equal(book.BidPrice[0], tr.PriceWon);
                    Assert.InRange(tr.Qty, 300, 3_000);
                    panicQty += tr.Qty;
                    Assert.True(book.AskPrice[0] - book.BidPrice[0] >= 2 * t, "스프레드 < 2틱 @" + k);
                    for (int l = 0; l < 10; l++)
                    {
                        Assert.InRange(book.BidQty[l], 1, 30);                              // 진공: 무너지는 쪽 잔량 고갈
                        Assert.InRange(book.AskQty[l], 1_500, 4_500);                       // 반대쪽 패닉 물량
                        if (l > 0) Assert.Equal(2 * t, book.BidPrice[l - 1] - book.BidPrice[l]);
                    }
                }
            }
            Assert.True(minBid <= inj.ExtremePrice + tick && minBid >= inj.ExtremePrice - 2 * tick, $"최저 {minBid} 극값 {inj.ExtremePrice}");
            Assert.True(panicQty / (plunge / 4.0 / 1_000) > 2_000_000, "패닉 체결량(주/초) 과소");
            inj.Read(inj.BurstEnd - 1, out var last);
            long end = anchor + inj.EndOffsetWon;
            Assert.InRange(last.BidPrice[0], end - 2 * tick, end + 2 * tick);
            Assert.True(inj.EndOffsetWon < 0 && -inj.EndOffsetWon < anchor * 800 / 10_000 * 7 / 10, "되돌림 40% 미반영 " + inj.EndOffsetWon);

            // 여진: 첫 대상 호가 = 원천 + 편차(연속) · 이후 감쇠
            int firstPost = -1;
            for (int i = inj.BurstEnd; i < inj.Count && firstPost < 0; i++) { inj.Read(i, out var bk); if (bk.SymbolCode == inj.TargetSymbol) firstPost = i; }
            inj.Read(firstPost, out var post);
            b.Read(firstPost - inj.BurstTicks, out var orig);
            Assert.InRange(post.BidPrice[0] - orig.BidPrice[0], inj.EndOffsetWon - 2 * tick, inj.EndOffsetWon + 2 * tick);
            Assert.InRange(post.BidPrice[0], last.BidPrice[0] - 4 * tick, last.BidPrice[0] + 4 * tick);
        }

        [Fact]
        public void ShortSqueeze_Spikes_With_Ask_Side_Vacuum_And_Buy_Driven_Panic()
        {
            var inj = Inject(Base(), ArenaScenarioProfile.ShortSqueeze, depthBp: 1_000);
            long anchor = inj.AnchorPrice;
            Assert.True(inj.ExtremePrice >= anchor * 11_000 / 10_000 - SyntheticTickSource.TickSize(inj.ExtremePrice), $"고점 {inj.ExtremePrice} 기준 {anchor}");
            int plunge = inj.BurstTicks * 3 / 5;
            long maxAsk = 0;
            for (int k = 0; k < plunge; k++)
            {
                inj.Read(inj.BurstStart + k, out var book);
                inj.TryReadTrade(inj.BurstStart + k, out var tr);
                maxAsk = Math.Max(maxAsk, book.AskPrice[0]);
                Assert.Equal(1, tr.Side);
                Assert.Equal(book.AskPrice[0], tr.PriceWon);
                for (int l = 0; l < 10; l++) { Assert.InRange(book.AskQty[l], 1, 30); Assert.InRange(book.BidQty[l], 1_500, 4_500); }
            }
            Assert.True(maxAsk >= inj.ExtremePrice - SyntheticTickSource.TickSize(maxAsk));
            Assert.True(inj.EndOffsetWon > 0);
        }

        // ═══ §3 블라인드 · 경계 ═══

        [Fact]
        public void Blind_Draws_Profile_Offset_And_Depth_Within_Window_Deterministically()
        {
            var b = Base();
            int crash = 0;
            for (ulong e = 0; e < 2_000; e++)
            {
                var inj = FlashCrashScenarioInjector.CreateBlind(b, e);
                Assert.InRange(inj.OffsetTicks, 3 * Minute, 5 * Minute);
                Assert.InRange(inj.DepthBp, FlashCrashScenarioInjector.BlindMinDepthBp, FlashCrashScenarioInjector.BlindMaxDepthBp);
                Assert.True(inj.Injected);
                Assert.InRange(inj.TimestampOf(inj.BurstStart) - b.TimestampOf(0), 3 * Minute, 5 * Minute + 3 * SyntheticTickSource.DefaultIntervalTicks);
                if (inj.Profile == ArenaScenarioProfile.FlashCrash) crash++;
                else Assert.Equal(ArenaScenarioProfile.ShortSqueeze, inj.Profile);
            }
            Assert.InRange(crash, 850, 1_150);                                                // 반반(±7.5%)
            var x = FlashCrashScenarioInjector.CreateBlind(b, 12345);
            var y = FlashCrashScenarioInjector.CreateBlind(b, 12345);
            Assert.Equal((x.Profile, x.OffsetTicks, x.DepthBp, x.BurstStart, x.EndOffsetWon), (y.Profile, y.OffsetTicks, y.DepthBp, y.BurstStart, y.EndOffsetWon));
            for (int i = x.BurstStart; i < x.BurstEnd; i += 97)
            {
                x.Read(i, out var p); y.Read(i, out var q);
                Assert.Equal((p.BidPrice[0], p.BidQty[4], p.AskQty[7]), (q.BidPrice[0], q.BidQty[4], q.AskQty[7]));
            }
        }

        [Fact]
        public void Short_Source_Is_Not_Injected_And_Passes_Through()
        {
            var b = Base(1_000);                                                              // 23.4초 < 3분
            var inj = FlashCrashScenarioInjector.CreateBlind(b, 99);
            Assert.False(inj.Injected);
            Assert.Equal(-1, inj.BurstStart);
            Assert.Equal(b.Count, inj.Count);
            for (int i = 0; i < b.Count; i++)
            {
                b.Read(i, out var o); inj.Read(i, out var p);
                Assert.Equal((o.TimestampTicks, o.BidPrice[0]), (p.TimestampTicks, p.BidPrice[0]));
            }
        }

        [Fact]
        public void Arguments_Fail_Closed()
        {
            var b = Base(10);
            Assert.Throws<ArgumentOutOfRangeException>(() => new FlashCrashScenarioInjector(b, ArenaScenarioProfile.None, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FlashCrashScenarioInjector(b, ArenaScenarioProfile.FlashCrash, 0, burstIntervalTicks: FlashCrashScenarioInjector.MaxBurstIntervalTicks + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FlashCrashScenarioInjector(b, ArenaScenarioProfile.FlashCrash, 0, depthBp: FlashCrashScenarioInjector.MaxDepthBp + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FlashCrashScenarioInjector(b, ArenaScenarioProfile.FlashCrash, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FlashCrashScenarioInjector(b, ArenaScenarioProfile.FlashCrash, 0, burstTicks: 4));
            Assert.Throws<ArgumentNullException>(() => new FlashCrashScenarioInjector(null!, ArenaScenarioProfile.FlashCrash, 0));
            var edge = new FlashCrashScenarioInjector(b, ArenaScenarioProfile.FlashCrash, 0, burstIntervalTicks: FlashCrashScenarioInjector.MaxBurstIntervalTicks);
            Assert.Equal(2_000, edge.BurstTicksPerSecond);
            Assert.Equal(0, edge.BurstStart);
        }

        // ═══ §4 δ=0 ═══

        [Fact]
        public void Reading_All_108k_Injected_Ticks_Is_Zero_Alloc()
        {
            var inj = Inject(Base(), ArenaScenarioProfile.FlashCrash);
            long sink = 0;
            void ReadAll()
            {
                for (int i = 0; i < inj.Count; i++)
                {
                    sink += inj.TimestampOf(i);
                    if (inj.Read(i, out var bk)) sink += bk.BidPrice[0] + bk.AskQty[9];
                    if (inj.TryReadTrade(i, out var tr)) sink += tr.Qty;
                }
            }
            ReadAll();
            Assert.Equal(0, MinAllocated(ReadAll));
            Assert.True(sink != 0);
        }

        [Fact]
        public void Replay_Engine_Plays_Through_Burst_At_Max_Speed_Zero_Alloc()
        {
            var tape = Base();
            using var e = new ArenaTickReplayEngine(() => tape, () => _now, Freq, ownThread: false, latencyMinTicks: 0, latencyMaxTicks: 0,
                                                    scenarioEntropy: () => EntropyFor(ArenaScenarioProfile.FlashCrash));
            Assert.True(e.SetScenario(ArenaScenario.Blind));
            e.SetSpeed(ArenaReplaySpeed.Max);
            long min = long.MaxValue, sink = 0;
            for (int run = 0; run < 3; run++)
            {
                e.Reset(); _now += Sec; e.Pump(_now);
                Assert.True(e.Play(Capital));
                _now += Sec / 1000; e.Pump(_now);                                           // 콜드: 매치 개시 · AI · 인젝터 생성
                var inj = e.Scenario!;
                Assert.True(inj.Injected);
                int stopAt = inj.BurstEnd + 2 * ArenaTickReplayEngine.MaxBatch;
                long before = GC.GetAllocatedBytesForCurrentThread();
                while (e.Status.Cursor < stopAt) { _now += Sec / 1000; sink += e.Pump(_now); e.OnHubPump(); }
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
                Assert.True(e.Status.Cursor > inj.BurstEnd);
            }
            _out.WriteLine($"[버스트 최대 배속 재생] 최소 할당 {min}B");
            Assert.Equal(0, min);
            Assert.True(sink > 0);
        }

        // ═══ §5 재생 엔진 결선 ═══

        [Theory]
        [InlineData(ArenaScenarioProfile.FlashCrash, -1)]
        [InlineData(ArenaScenarioProfile.ShortSqueeze, 1)]
        public void Replay_Blind_Phases_Reveal_Only_On_Trigger_And_Human_Hits_Vacuum(int profile, int side)
        {
            var tape = Base();
            using var e = new ArenaTickReplayEngine(() => tape, () => _now, Freq, ownThread: false, latencyMinTicks: 0, latencyMaxTicks: 0,
                                                    scenarioEntropy: () => EntropyFor(profile));
            Assert.Equal(ArenaScenario.Off, e.Status.ScenarioMode);
            Assert.True(e.SetScenario(ArenaScenario.Blind));
            Assert.True(e.SetSpeed(10));                                                     // 10x: 50ms 회차 = 버스트 2,000틱 → 발동 국면 4회차 관측
            Assert.True(e.Play(Capital));
            e.Pump(_now);
            var inj = e.Scenario!;
            Assert.Equal(profile, inj.Profile);
            Assert.Equal(inj.Count, e.Status.Total);
            var seen = new List<int>();
            bool hit = false;
            long sym = 0;
            while (e.State == ArenaReplayState.Playing)
            {
                _now += Sec / 20;
                e.Pump(_now);
                var st = e.Status;
                if (seen.Count == 0 || seen[^1] != st.ScenarioPhase) seen.Add(st.ScenarioPhase);
                Assert.Equal(ArenaScenario.Blind, st.ScenarioMode);
                if (st.ScenarioPhase == ArenaScenarioPhase.Armed)
                    Assert.Equal((0, 0L, 0L), (st.ScenarioProfile, st.ScenarioSymbol, st.ScenarioTicksPerSecond));   // 블라인드: 잠복 중 비공개
                else
                {
                    Assert.Equal((profile, inj.TargetSymbol, 4_000L), (st.ScenarioProfile, st.ScenarioSymbol, st.ScenarioTicksPerSecond));
                    sym = st.ScenarioSymbol;
                }
                if (st.ScenarioPhase == ArenaScenarioPhase.Active && !hit)
                {
                    Assert.Equal(ArenaReject.None, e.Arena.SubmitHuman(sym, side, 60));     // 진공 쪽으로 시장가 60주(잔량 1 ~ 30 → 70% 이상 소진)
                    hit = true;
                }
                if (st.ScenarioPhase == ArenaScenarioPhase.Aftermath && e.Status.Cursor > inj.BurstEnd + 30_000) break;
                Assert.False(e.SetScenario(ArenaScenario.Off));                             // 진행 중 = 거부
            }
            Assert.Equal(new[] { ArenaScenarioPhase.Armed, ArenaScenarioPhase.Active, ArenaScenarioPhase.Aftermath }, seen);
            Assert.True(e.Arena.TryRead(out var s));
            _out.WriteLine($"[{ArenaScenarioProfile.Label(profile)}] 휴먼 진공 {s.Human.VacuumEvents} · 페널티 {s.Human.VacuumPenaltyWon:N0}원 · 슬리피지 {s.Human.SlippageWon:N0}원 · AI 체결 {s.Ai.Fills}");
            Assert.True(s.Human.VacuumEvents > 0, "진공 미발동");
            Assert.True(s.Human.SlippageWon > 0);

            e.Pause(); _now += Sec / 20; e.Pump(_now);
            Assert.False(e.SetScenario(ArenaScenario.Off));                                  // 일시정지 = 거부
            e.Reset(); _now += Sec / 20; e.Pump(_now);
            Assert.Equal(ArenaScenarioPhase.None, e.Status.ScenarioPhase);                   // 리셋 = 인젝터 해제 · 원천 그대로
            Assert.Null(e.Scenario);
            Assert.Equal(BaseTicks, e.Status.Total);
            Assert.False(e.SetScenario(7));
            Assert.True(e.SetScenario(ArenaScenario.Off));
            Assert.True(e.Play(Capital)); e.Pump(_now);
            Assert.Null(e.Scenario);
            Assert.Equal(ArenaScenarioPhase.None, e.Status.ScenarioPhase);
        }

        [Fact]
        public void Replay_Full_Run_With_Scenario_Finishes_All_Ticks()
        {
            var tape = Base();
            using var e = new ArenaTickReplayEngine(() => tape, () => _now, Freq, ownThread: false, latencyMinTicks: 0, latencyMaxTicks: 0, scenarioEntropy: () => 3);
            e.SetScenario(ArenaScenario.Blind);
            e.SetSpeed(ArenaReplaySpeed.Max);
            e.Play(Capital);
            do { _now += Sec / 1000; e.Pump(_now); } while (e.State == ArenaReplayState.Playing);
            Assert.Equal(ArenaReplayState.Finished, e.State);
            Assert.Equal(BaseTicks + FlashCrashScenarioInjector.DefaultBurstTicks, e.Processed);
            Assert.Equal(ArenaScenarioPhase.Aftermath, e.Status.ScenarioPhase);
            Assert.True(e.Arena.TryRead(out var s) && s.State == ArenaState.Finished && s.AiSignals > 0);
        }

        // ═══ §6 Fail-Closed ═══

        [Fact]
        public void Injector_Il_References_Only_Allowlisted_Types()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var t = typeof(FlashCrashScenarioInjector);
            static bool Allowed(Type x)
            {
                while (x.HasElementType) x = x.GetElementType()!;
                if (x.IsGenericType && !x.IsGenericTypeDefinition) return Allowed(x.GetGenericTypeDefinition()) && x.GetGenericArguments().All(Allowed);
                if (x == typeof(OrderBookPayload) || x == typeof(OrderBookLevels10) || x.FullName == "<PrivateImplementationDetails>") return true;   // InlineArray 인덱서 헬퍼
                string ns = x.Namespace ?? "";
                return ns == "System" || ns.StartsWith("System.Runtime", StringComparison.Ordinal) || ns == "MS2E.UI.Portable.Ports.Trading" || ns == "StockManager.Engines.Arena";
            }
            var bad = new List<string>();
            foreach (var f in t.GetFields(All)) if (!Allowed(f.FieldType)) bad.Add(f.Name + ": " + f.FieldType.FullName);
            foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
                foreach (var r in ZeroAllocIlInspector.ReferencedMembers(m))
                    if ((r as Type ?? r.DeclaringType) is { } owner && !Allowed(owner)) bad.Add(m.Name + " → " + owner.FullName + "::" + r.Name);
            Assert.True(bad.Count == 0, string.Join("\n", bad.Distinct()));
            Assert.DoesNotContain(typeof(IArenaReplayPort).GetMembers(), m => m.Name.Contains("Arm", StringComparison.OrdinalIgnoreCase) || m.Name.Contains("Broker", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Headless_Ui_Blind_Option_And_Warning_Indicator_Follow_Phases()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ms2e_xs_" + Guid.NewGuid().ToString("N"));
            var tape = Base();
            using var e = new ArenaTickReplayEngine(() => tape, () => _now, Freq, ownThread: false, latencyMinTicks: 0, latencyMaxTicks: 0,
                                                    scenarioEntropy: () => EntropyFor(ArenaScenarioProfile.FlashCrash));
            using var rec = new ArenaTapeRecorder(dir, 10);
            try
            {
                using var ctx = new Bunit.TestContext();
                ctx.Services.AddSingleton<IArenaPort>(new ArenaMatchGovernor(() => _now));
                ctx.Services.AddSingleton<IArenaReplayPort>(e);
                ctx.Services.AddSingleton<IArenaTapePort>(rec);
                ctx.Services.AddSingleton<IKisFeedLatencyPort>(new StockManager.Adapters.Kis.KisTimeSkewGuard());
                var cut = ctx.RenderComponent<MS2E.UI.Portable.Pages.Trading.ArenaBattleView>();
                cut.FindAll(".ar-mode .ar-tab")[1].Click();                                   // 리플레이 모드
                var box = cut.Find(".ar-xs-opt input");
                Assert.False(box.HasAttribute("checked"));
                Assert.Contains("Extreme Scenario", cut.Find(".ar-xs-opt").TextContent);
                box.Change(true);
                Assert.Equal(ArenaScenario.Blind, e.Status.ScenarioMode);
                Assert.Empty(cut.FindAll(".ar-xs"));                                         // 매치 전 = 인디케이터 없음

                e.SetSpeed(10);
                e.Play(Capital); e.Pump(_now);
                cut.Render();
                var armed = cut.Find(".ar-xs.armed");
                Assert.Contains("잠복", armed.TextContent);
                Assert.DoesNotContain("크래시", armed.TextContent);                          // 블라인드: 종류 비공개
                Assert.DoesNotContain("스퀴즈", armed.TextContent);
                Assert.True(cut.Find(".ar-xs-opt input").HasAttribute("disabled"));           // 진행 중 = 비활성

                var inj = e.Scenario!;
                while (e.Status.ScenarioPhase != ArenaScenarioPhase.Active) { _now += Sec / 20; e.Pump(_now); }
                cut.Render();
                var alert = cut.Find(".ar-xs.active.p1");
                Assert.Equal("alert", alert.GetAttribute("role"));
                Assert.Contains("플래시 크래시", alert.TextContent);
                Assert.Contains(inj.TargetSymbol.ToString("D6"), alert.TextContent);
                Assert.Contains("4,000틱/초", alert.TextContent);
                Assert.Empty(cut.FindAll(".ar-xs.armed"));

                while (e.Status.ScenarioPhase != ArenaScenarioPhase.Aftermath) { _now += Sec / 20; e.Pump(_now); }
                cut.Render();
                Assert.Contains("여진", cut.Find(".ar-xs.after").TextContent);
                Assert.Empty(cut.FindAll(".ar-xs.active"));
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
}
