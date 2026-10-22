// ============================================================================
// KrxArenaEngineTests — v12.18.59-DEV: 국내 대형주 아레나(휴먼 vs AI) · 설명 가능한 AI 근거(AiTradeRationale)
//
//  §1 근거 무결성 — AI 진입/익절/손절/장마감 청산마다 근거 1건 · 필드(사유 · 감성 · 확신도 · 목표/손절 · 손익비 · 한국어 요약) 온전 ·
//     목표/손절가 KRX 호가단위 정렬 · 무작위 장세 2만 틱에서 AI 체결 수 == 근거 수(누락 0)
//  §2 듀얼 원장 독립 — 휴먼 체결이 AI 현금/보유에 영향 0(역도 동일) · 같은 호가 각자 소비 · 세션 외/미수신/보유 부족 거부
//  §3 동시성 — 다중 제출 스레드 + 정산 스레드 + 판독 스레드: 접수 = 체결 + 거부(유실 0) · 같은 seqlock 창에서 AI 체결 == 근거 수
//  §4 δ=0 — 시그널 평가 + 가상 체결 + 휴먼 큐 + 판독 10만 회 루프 힙 할당 0B(ZeroAllocProbe) · 핫패스 IL 할당 지점 0
//  §5 Pure Shell — IKrxArenaPort 에 브로커/무장/실주문 멤버 0 · 포트 DTO 는 엔진 타입 참조 0 · DI 결선 · Fed=false 정직 렌더
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Domestic;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class KrxArenaEngineTests
    {
        private const long Samsung = 5930, Hynix = 660;
        private static readonly DateTime Open10 = new(2026, 10, 22, 10, 0, 0);   // 목요일 10:00 KST
        private readonly ITestOutputHelper _out;
        private DateTime _t = Open10;

        public KrxArenaEngineTests(ITestOutputHelper o) => _out = o;

        private void Q(KrxArenaEngine e, long sym, long bid, long ask, long bidQty = 1000, long askQty = 1000, long vol = 100)
        {
            _t = _t.AddMilliseconds(100);
            e.OnQuote(sym, bid, ask, bidQty, askQty, vol, _t);
        }

        private void Warm(KrxArenaEngine e, long sym = Samsung, long bid = 70_000, long ask = 70_100)
        {
            for (int i = 0; i < KrxArenaEngine.WarmupTicks + 1; i++) Q(e, sym, bid, ask);
        }

        // 거시 +3000 · 불균형 +8000 · 거래량 10배 · 가격 상승 → 점수 ≈ 5471(진입 · 주도 = 호가 불균형)
        private void Spike(KrxArenaEngine e, long sym = Samsung, long bid = 70_100, long ask = 70_200)
        {
            e.SetOvernightMacro(3000);
            Q(e, sym, bid, ask, bidQty: 9000, askQty: 1000, vol: 1000);
        }

        private static AiTradeRationale[] Rationales(KrxArenaEngine e)
        {
            var buf = new AiTradeRationale[KrxArenaEngine.RationaleCapacity];
            int n = e.CopyRationales(buf);
            return buf.AsSpan(0, n).ToArray();
        }

        private static DuelSnapshot Snap(KrxArenaEngine e)
        {
            Assert.True(e.TryReadSnapshot(out var s));
            return s;
        }

        // ── §1 근거 무결성 ──────────────────────────────────────────────────────

        [Fact]
        public void AiEntry_Binds_Complete_Rationale()
        {
            var e = new KrxArenaEngine();
            Warm(e);
            Assert.Empty(Rationales(e));
            Spike(e);

            var r = Assert.Single(Rationales(e));
            _out.WriteLine(r.HumanReadableSummary);
            Assert.True(r.IsWellFormed);
            Assert.Equal(1, r.Side);
            Assert.Equal(Samsung, r.Symbol);
            Assert.Equal(70_200, r.PriceWon);
            Assert.Equal(ArenaTrigger.OrderbookImbalance, r.TriggerType);
            Assert.Equal(3000, r.MacroBp);
            Assert.Equal(8000, r.ImbalanceBp);
            Assert.Equal(10000, r.SurgeBp);
            Assert.True(r.MomentumBp > 0);
            Assert.InRange(r.SentimentScore, KrxArenaEngine.EntryScore, 10000);
            Assert.Equal(r.SentimentScore / 100, r.ConfidenceScore);
            Assert.True(KrxTickLadder.IsOnTick(r.TargetPrice) && KrxTickLadder.IsOnTick(r.StopLossPrice));
            Assert.Equal(72_100, r.TargetPrice);   // 70,200 × (1 + (100 + 3·54)bp) = 72,039 → 호가 올림
            Assert.Equal(69_600, r.StopLossPrice); // 70,200 × (1 − 80bp) = 69,638 → 호가 내림
            Assert.Equal((72_100 - 70_200) * 100 / (70_200 - 69_600), r.RiskRewardX100);
            Assert.Equal(Open10.Date, r.AtKst.Date);

            string s = r.HumanReadableSummary;
            foreach (var must in new[] { "[AI 매수]", "005930", "주도 근거: 호가 수급 불균형", "야간 글로벌 +3,000bp", "호가 불균형 +8,000bp",
                                         "거래량 급증 +10,000bp", "확신도", "목표가 72,100원", "손절가 69,600원", "손익비 3.16" })
                Assert.Contains(must, s);

            Span<DuelPosition> pos = stackalloc DuelPosition[4];
            Assert.Equal(1, e.CopyPositions(ArenaSide.Ai, pos));
            Assert.Equal(r.Qty, pos[0].Qty);
            Assert.Equal(r.TargetPrice, pos[0].TargetPrice);
            Assert.Equal(r.StopLossPrice, pos[0].StopLossPrice);
            Assert.Equal(1, Snap(e).Ai.Fills);
        }

        [Fact]
        public void AiTakeProfit_Exit_Rationale_Carries_Entry_Plan_And_Win()
        {
            var e = new KrxArenaEngine();
            Warm(e); Spike(e);
            var entry = Rationales(e)[0];
            e.SetOvernightMacro(0);
            Q(e, Samsung, entry.TargetPrice, entry.TargetPrice + 100);

            var all = Rationales(e);
            Assert.Equal(2, all.Length);
            var x = all[1];
            _out.WriteLine(x.HumanReadableSummary);
            Assert.True(x.IsWellFormed);
            Assert.Equal(-1, x.Side);
            Assert.Equal(ArenaTrigger.TakeProfit, x.TriggerType);
            Assert.Equal(entry.Qty, x.Qty);
            Assert.Equal(entry.TargetPrice, x.PriceWon);
            Assert.Equal(entry.TargetPrice, x.TargetPrice);
            Assert.Equal(entry.StopLossPrice, x.StopLossPrice);
            Assert.Equal(entry.RiskRewardX100, x.RiskRewardX100);
            Assert.Equal(100, x.ConfidenceScore);
            Assert.True(x.RealizedPnlWon > 0);
            Assert.Contains("청산 사유: 목표가 도달(익절)", x.HumanReadableSummary);
            Assert.Contains("실현손익 +", x.HumanReadableSummary);

            var s = Snap(e);
            Assert.Equal(1, s.Ai.Wins);
            Assert.Equal(0, s.Ai.Losses);
            Assert.Equal(10000, s.Ai.WinRateBp);
            Assert.Equal(0, s.Ai.OpenPositions);
            Assert.Equal(x.RealizedPnlWon, s.Ai.RealizedWon);
            Assert.Equal(s.Ai.CapitalWon + s.Ai.RealizedWon, s.Ai.CashWon);
        }

        [Fact]
        public void AiStopLoss_Exit_Counts_Loss_And_Mdd()
        {
            var e = new KrxArenaEngine();
            Warm(e); Spike(e);
            var entry = Rationales(e)[0];
            Q(e, Samsung, entry.StopLossPrice, entry.StopLossPrice + 100);

            var x = Rationales(e)[^1];
            Assert.True(x.IsWellFormed);
            Assert.Equal(ArenaTrigger.StopLoss, x.TriggerType);
            Assert.True(x.RealizedPnlWon < 0);
            Assert.Contains("손절가 이탈(손절)", x.HumanReadableSummary);
            var s = Snap(e);
            Assert.Equal(1, s.Ai.Losses);
            Assert.True(s.Ai.MddBp > 0);
            Assert.True(s.Ai.ReturnBp < 0);
        }

        [Fact]
        public void Session_Gate_Cutoff_Flattens_And_Blocks_Off_Hours()
        {
            var e = new KrxArenaEngine();
            _t = new DateTime(2026, 10, 22, 15, 10, 0);
            Warm(e); Spike(e);
            Assert.Equal(1, Snap(e).Ai.OpenPositions);

            e.SetOvernightMacro(0);
            _t = new DateTime(2026, 10, 22, 15, 20, 0);
            Q(e, Samsung, 70_100, 70_200);
            var x = Rationales(e)[^1];
            Assert.Equal(ArenaTrigger.SessionClose, x.TriggerType);
            Assert.True(x.IsWellFormed);
            Assert.Equal(0, Snap(e).Ai.OpenPositions);

            // 15:20 이후 진입 0 · 15:30 이후/주말 세션 닫힘 → AI 0 · 휴먼 거부
            Spike(e);
            Assert.Equal(2, Rationales(e).Length);
            _t = new DateTime(2026, 10, 24, 10, 0, 0);   // 토요일
            Assert.False(KrxArenaEngine.IsRegularSession(_t));
            Assert.Equal(KrxArenaReject.None, e.SubmitHuman(Samsung, 1, 1));
            Spike(e);
            var s = Snap(e);
            Assert.False(s.SessionOpen);
            Assert.Equal(2, s.RationaleCount);
            Assert.Equal(1, s.Human.Rejects);
            Assert.Equal(0, s.Human.Fills);
            Assert.True(KrxArenaEngine.IsRegularSession(new DateTime(2026, 10, 22, 9, 0, 0)));
            Assert.False(KrxArenaEngine.IsRegularSession(new DateTime(2026, 10, 22, 8, 59, 59)));
            Assert.False(KrxArenaEngine.IsRegularSession(new DateTime(2026, 10, 22, 15, 30, 0)));
        }

        [Fact]
        public void Every_Ai_Fill_Has_WellFormed_Rationale_Under_Random_Tape()
        {
            var e = new KrxArenaEngine();
            var syms = new long[] { 5930, 660, 373220, 207940, 5380 };
            var px = new long[] { 70_000, 180_000, 190_000, 150_000, 110_000 };
            var rnd = new Random(59);
            long seenRationales = 0;
            for (int i = 0; i < 20_000; i++)
            {
                if (i % 500 == 0) e.SetOvernightMacro(rnd.Next(-6000, 6001));
                int k = rnd.Next(syms.Length);
                px[k] = Math.Clamp(px[k] + rnd.Next(-3, 4) * 100, 60_000, 190_000);
                long bq = rnd.Next(100, 5000), aq = rnd.Next(100, 5000);
                Q(e, syms[k], px[k], px[k] + 100, bq, aq, rnd.Next(10, rnd.Next(2) == 0 ? 3000 : 300));
                if (i % 64 == 63)
                {
                    var s = Snap(e);
                    Assert.Equal(s.Ai.Fills, s.RationaleCount);
                    foreach (var r in Rationales(e)) Assert.True(r.IsWellFormed, r.HumanReadableSummary);
                    seenRationales = s.RationaleCount;
                }
            }
            var fin = Snap(e);
            _out.WriteLine($"AI fills={fin.Ai.Fills} wins={fin.Ai.Wins} losses={fin.Ai.Losses} ret={fin.Ai.ReturnBp}bp mdd={fin.Ai.MddBp}bp");
            Assert.True(seenRationales > 10, $"무작위 장세에서 AI 체결이 너무 적음({seenRationales})");
            Assert.Equal(fin.Ai.Fills, fin.RationaleCount);
            var seqs = Rationales(e).Select(r => r.Seq).ToArray();
            Assert.Equal(Enumerable.Range((int)(fin.RationaleCount - seqs.Length + 1), seqs.Length).Select(x => (long)x), seqs);
        }

        [Fact]
        public void Signal_Evaluate_Is_Bounded_And_Neutral_On_Flat_Tape()
        {
            const long fx = 1024;
            var flat = KrxArenaEngine.Evaluate(0, 1000, 1000, 100 * fx, 100 * fx, 70_050 * fx, 70_050 * fx);
            Assert.Equal(0, flat.Score);
            var max = KrxArenaEngine.Evaluate(int.MaxValue, 1_000_000_000, 0, 1_000_000_000L * fx, 1, 10_000_000 * fx, 1);
            var min = KrxArenaEngine.Evaluate(int.MinValue, 0, 1_000_000_000, 1_000_000_000L * fx, 1, 1, 10_000_000 * fx);
            Assert.InRange(max.Score, -10000, 10000);
            Assert.InRange(min.Score, -10000, 10000);
            Assert.Equal(10000, max.Score);
            Assert.Equal(-10000, min.Score);
            var macroLed = KrxArenaEngine.Evaluate(10000, 1000, 1000, 100 * fx, 100 * fx, 70_050 * fx, 70_050 * fx);
            Assert.Equal(ArenaTrigger.MacroTailwind, macroLed.Dominant);
        }

        // ── §2 듀얼 원장 독립 ────────────────────────────────────────────────────

        [Fact]
        public void Human_And_Ai_Books_Are_Independent()
        {
            var e = new KrxArenaEngine();
            Warm(e);
            Assert.Equal(KrxArenaReject.None, e.SubmitHuman(Samsung, 1, 100));
            e.Drain(_t);
            var s = Snap(e);
            long cost = 100 * 70_100 + 100 * 70_100 * KrxArenaEngine.FeePer100k / 100_000;
            Assert.Equal(KrxArenaEngine.DefaultCapitalWon - cost, s.Human.CashWon);
            Assert.Equal(KrxArenaEngine.DefaultCapitalWon, s.Ai.CashWon);
            Assert.Equal(0, s.Ai.Fills);

            // 같은 호가 각자 소비: 휴먼 900주 + AI 진입이 동일 1000주 매도잔량을 각자 소진(간섭 0)
            Assert.Equal(KrxArenaReject.None, e.SubmitHuman(Samsung, 1, 900));
            Spike(e);
            s = Snap(e);
            Span<DuelPosition> hp = stackalloc DuelPosition[4], ap = stackalloc DuelPosition[4];
            Assert.Equal(1, e.CopyPositions(ArenaSide.Human, hp));
            Assert.Equal(1, e.CopyPositions(ArenaSide.Ai, ap));
            Assert.Equal(1000, hp[0].Qty);
            Assert.Equal(0, hp[0].TargetPrice);
            Assert.Equal(Rationales(e)[0].Qty, ap[0].Qty);
            Assert.Equal(KrxArenaEngine.DefaultCapitalWon - ap[0].CostWon, s.Ai.CashWon);
            Assert.Equal(KrxArenaEngine.DefaultCapitalWon - hp[0].CostWon, s.Human.CashWon);

            // AI 익절 청산은 휴먼 보유에 영향 0
            Q(e, Samsung, 72_100, 72_200);
            Assert.Equal(0, Snap(e).Ai.OpenPositions);
            Assert.Equal(1, e.CopyPositions(ArenaSide.Human, hp));
            Assert.Equal(1000, hp[0].Qty);

            // 휴먼: 보유 초과 매도 · 미수신 종목 · 값 오류 → 거부(AI 무영향)
            Assert.Equal(KrxArenaReject.None, e.SubmitHuman(Samsung, -1, 5000));
            Assert.Equal(KrxArenaReject.None, e.SubmitHuman(Hynix, 1, 1));
            Assert.Equal(KrxArenaReject.Invalid, e.SubmitHuman(Samsung, 0, 1));
            Assert.Equal(KrxArenaReject.Invalid, e.SubmitHuman(Samsung, 1, 0));
            Assert.Equal(KrxArenaReject.Invalid, e.SubmitHuman(-1, 1, 1));
            long aiFills = Snap(e).Ai.Fills;
            e.Drain(_t);
            s = Snap(e);
            Assert.Equal(2, s.Human.Rejects);
            Assert.Equal(aiFills, s.Ai.Fills);
            Assert.Equal(0, s.Ai.Rejects);

            // 휴먼 전량 매도 → 손익 정산
            Assert.Equal(KrxArenaReject.None, e.SubmitHuman(Samsung, -1, 1000));
            e.Drain(_t);
            s = Snap(e);
            Assert.Equal(0, s.Human.OpenPositions);
            Assert.Equal(1, s.Human.Wins);
            Assert.Equal(s.Human.CapitalWon + s.Human.RealizedWon, s.Human.CashWon);
        }

        [Fact]
        public void Human_Queue_Full_Drops_Without_Retry_And_Cash_Guard_Rejects()
        {
            var e = new KrxArenaEngine(capitalWon: 1_000_000);
            Warm(e);
            for (int i = 0; i < KrxArenaEngine.HumanQueueCapacity; i++) Assert.Equal(KrxArenaReject.None, e.SubmitHuman(Samsung, 1, 1));
            Assert.Equal(KrxArenaReject.QueueFull, e.SubmitHuman(Samsung, 1, 1));
            e.Drain(_t);
            var s = Snap(e);
            Assert.Equal(KrxArenaEngine.HumanQueueCapacity, s.Human.Fills + s.Human.Rejects);
            Assert.Equal(14, s.Human.Fills);                  // 1,000,000 / 70,111(수수료 포함) = 14주
            Assert.True(s.Human.CashWon >= 0);
            Assert.Equal(KrxArenaReject.None, e.SubmitHuman(Samsung, 1, 1));   // 드레인 후 큐 재사용
            Assert.Throws<ArgumentOutOfRangeException>(() => new KrxArenaEngine(capitalWon: 0));
        }

        [Fact]
        public void Invalid_Quotes_Are_Dropped_FailClosed()
        {
            var e = new KrxArenaEngine();
            e.OnQuote(Samsung, 0, 70_100, 1, 1, 1, Open10);
            e.OnQuote(Samsung, 70_200, 70_100, 1, 1, 1, Open10);
            e.OnQuote(Samsung, 70_000, 70_100, -1, 1, 1, Open10);
            e.OnQuote(0, 70_000, 70_100, 1, 1, 1, Open10);
            e.OnQuote(Samsung, 70_000, 70_100, 1, 1, -5, Open10);
            var s = Snap(e);
            Assert.Equal(5, s.DroppedQuotes);
            Assert.Equal(0, s.Ticks);
            Assert.False(new KrxArenaPortAdapter(e).Fed);
        }

        // ── §3 동시성 ───────────────────────────────────────────────────────────

        [Fact]
        public void Concurrent_Submitters_Feed_And_Readers_Lose_Nothing()
        {
            var e = new KrxArenaEngine();
            Warm(e);
            const int Submitters = 4, PerSubmitter = 5_000, FeedTicks = 30_000;
            long accepted = 0, dropped = 0, unexpected = 0, torn = 0, reads = 0;
            int feedDone = 0;
            var baseT = _t;

            var feed = new Thread(() =>
            {
                var rnd = new Random(7);
                long p = 70_000;
                for (int i = 0; i < FeedTicks; i++)
                {
                    p = Math.Clamp(p + rnd.Next(-2, 3) * 100, 65_000, 75_000);
                    if (i % 400 == 0) e.SetOvernightMacro(rnd.Next(-5000, 5001));
                    e.OnQuote(Samsung, p, p + 100, rnd.Next(100, 5000), rnd.Next(100, 5000), rnd.Next(10, 2000), baseT.AddMilliseconds(i));
                }
                Volatile.Write(ref feedDone, 1);
            });
            var subs = Enumerable.Range(0, Submitters).Select(k => new Thread(() =>
            {
                for (int i = 0; i < PerSubmitter; i++)
                {
                    int rc = e.SubmitHuman(Samsung, (i & 1) == 0 ? 1 : -1, 1);
                    if (rc == KrxArenaReject.None) Interlocked.Increment(ref accepted);
                    else if (rc == KrxArenaReject.QueueFull) Interlocked.Increment(ref dropped);
                    else Interlocked.Increment(ref unexpected);   // 스레드 내 throw = 호스트 크래시 → 계수 후 단언
                    if ((i & 31) == 0) Thread.Yield();
                }
            })).ToArray();
            var reader = new Thread(() =>
            {
                var buf = new AiTradeRationale[KrxArenaEngine.RationaleCapacity];
                while (Volatile.Read(ref feedDone) == 0)
                {
                    if (!e.TryReadSnapshot(out var s)) continue;
                    reads++;
                    if (s.Ai.Fills != s.RationaleCount || s.Ai.CashWon < 0 || s.Human.CashWon < 0) Interlocked.Increment(ref torn);
                    int n = e.CopyRationales(buf);
                    for (int i = 0; i < n; i++) if (!buf[i].IsWellFormed) Interlocked.Increment(ref torn);
                }
            });

            feed.Start(); reader.Start();
            foreach (var t in subs) t.Start();
            foreach (var t in subs) t.Join();
            feed.Join(); reader.Join();
            e.Drain(baseT.AddMilliseconds(FeedTicks));

            var fin = Snap(e);
            _out.WriteLine($"accepted={accepted} dropped={dropped} humanFills={fin.Human.Fills} humanRejects={fin.Human.Rejects} aiFills={fin.Ai.Fills} reads={reads}");
            Assert.Equal(0, unexpected);
            Assert.Equal(Submitters * PerSubmitter, accepted + dropped);
            Assert.Equal(accepted, fin.Human.Fills + fin.Human.Rejects);
            Assert.Equal(0, torn);
            Assert.Equal(fin.Ai.Fills, fin.RationaleCount);
            Assert.True(reads > 0);
        }

        // ── §4 δ=0 ─────────────────────────────────────────────────────────────

        [Fact]
        public void HotPath_100k_Signal_And_Virtual_Fills_Allocate_Zero_Bytes()
        {
            var e = new KrxArenaEngine();
            var rationales = new AiTradeRationale[KrxArenaEngine.RationaleCapacity];
            var positions = new DuelPosition[KrxArenaEngine.MaxPositions];
            long baseTicks = Open10.Ticks;
            ulong x = 0x9E3779B97F4A7C15;
            long p0 = 70_000, p1 = 180_000, i0 = 0;

            void Run(int n)
            {
                for (int i = 0; i < n; i++, i0++)
                {
                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                    bool hy = (x & 1) != 0;
                    long sym = hy ? Hynix : Samsung;
                    long step = (long)(x >> 8 & 3) - 1;
                    long p = hy ? (p1 = Math.Clamp(p1 + step * 100, 150_000, 195_000)) : (p0 = Math.Clamp(p0 + step * 100, 60_000, 80_000));
                    if ((i0 & 511) == 0) e.SetOvernightMacro((int)(x >> 20 & 0x1FFF) - 4096);
                    long bq = 100 + (long)(x >> 24 & 4095), aq = 100 + (long)(x >> 36 & 4095);
                    long vol = (x >> 48 & 15) == 0 ? 3000 : 50 + (long)(x >> 52 & 255);
                    var kst = new DateTime(baseTicks + (i0 % 90_000) * TimeSpan.TicksPerMillisecond * 100);
                    e.OnQuote(sym, p, p + 100, bq, aq, vol, kst);
                    if ((i0 & 7) == 0) e.SubmitHuman(sym, (i0 & 8) == 0 ? 1 : -1, 1 + (long)(x >> 40 & 7));
                    if ((i0 & 63) == 0)
                    {
                        e.TryReadSnapshot(out _);
                        e.CopyRationales(rationales);
                        e.CopyPositions(ArenaSide.Ai, positions);
                    }
                }
            }

            Run(20_000);   // JIT · 티어링 워밍업
            long before = Snap(e).Ai.Fills;
            long bytes = MinAllocated(() => Run(100_000));
            var s = Snap(e);
            _out.WriteLine($"δ={bytes}B aiFills={s.Ai.Fills} humanFills={s.Human.Fills} rationales={s.RationaleCount}");
            Assert.Equal(0, bytes);
            Assert.True(s.Ai.Fills > before, "측정 루프에서 AI 가상 체결이 발생해야 한다(빈 루프 측정 금지)");
            Assert.True(s.Human.Fills > 0);
            Assert.Equal(s.Ai.Fills, s.RationaleCount);
        }

        [Fact]
        public void HotPath_Methods_Have_No_Il_Allocation_Sites()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var t = typeof(KrxArenaEngine);
            string[] hot = { "OnQuote", "Drain", "Evaluate", "StepAi", "DrainHuman", "Buy", "Sell", "Record", "Equity", "Stats", "PublishSnapshot",
                             "SubmitHuman", "TryDequeue", "TryReadSnapshot", "CopyPositions", "CopyRationales", "FindSym", "FindOrAddSym", "FindPos",
                             "RoundUpTick", "RoundDownTick", "IsRegularSession" };
            foreach (var name in hot)
            {
                var m = t.GetMethod(name, all);
                Assert.NotNull(m);
                var sites = ZeroAllocIlInspector.Inspect(m!);
                Assert.True(sites.Count == 0, string.Join("\n", sites));
            }
        }

        // ── §5 Pure Shell · Fail-Closed ────────────────────────────────────────

        [Fact]
        public void Port_Surface_Has_No_Broker_Arm_Or_Live_Path_And_No_Engine_Types()
        {
            var names = typeof(IKrxArenaPort).GetMembers().Select(m => m.Name).ToArray();
            foreach (var bad in new[] { "Arm", "Broker", "Live", "Send", "Route", "Valve" })
                Assert.DoesNotContain(names, n => n.Contains(bad, StringComparison.OrdinalIgnoreCase));
            var engineNames = typeof(IArenaModeEngine).GetMembers().Select(m => m.Name).ToArray();
            foreach (var bad in new[] { "Arm", "Broker", "Live", "Send", "Route", "Valve" })
                Assert.DoesNotContain(engineNames, n => n.Contains(bad, StringComparison.OrdinalIgnoreCase));

            var portable = typeof(IKrxArenaPort).Assembly;
            Assert.DoesNotContain(portable.GetReferencedAssemblies(), a => a.Name == typeof(KrxArenaEngine).Assembly.GetName().Name);
            foreach (var dto in new[] { typeof(KrxArenaView), typeof(KrxArenaBookView), typeof(KrxRationaleView) })
                Assert.Equal(portable, dto.Assembly);
        }

        [Fact]
        public void Adapter_Projects_Snapshot_And_Korean_Rationales_Newest_First()
        {
            var e = new KrxArenaEngine();
            IKrxArenaPort port = new KrxArenaPortAdapter(e);
            Assert.False(port.Fed);
            Assert.True(port.TryRead(out var v0));
            Assert.Equal(0, v0.Ticks);
            Assert.Equal(ArenaLeader.Draw, v0.Leader);
            Assert.Empty(port.RecentRationales(10));

            Warm(e); Spike(e);
            Q(e, Samsung, 72_100, 72_200);
            Assert.True(port.Fed);
            Assert.True(port.TryRead(out var v));
            Assert.Equal(2, v.RationaleCount);
            Assert.Equal(ArenaLeader.Ai, v.Leader);
            Assert.Equal(KrxArenaEngine.DefaultCapitalWon, v.Ai.CapitalWon);
            var feed = port.RecentRationales(10);
            Assert.Equal(2, feed.Count);
            Assert.Equal(-1, feed[0].Side);
            Assert.Equal("목표가 도달(익절)", feed[0].Trigger);
            Assert.Equal("호가 수급 불균형", feed[1].Trigger);
            Assert.All(feed, f => Assert.False(string.IsNullOrWhiteSpace(f.Summary)));
            Assert.Single(port.RecentRationales(1));
            Assert.Equal("국내 대형주(KRX)", port.ModeName);
            Assert.Equal(KrxArenaReject.None, port.SubmitHuman(Samsung, 1, 1));
        }

        [Fact]
        public void Host_Di_Registers_Krx_Arena_Port_Via_Adapter()
        {
            string form = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("services.AddSingleton<MS2E.UI.Portable.Ports.Trading.IKrxArenaPort>(new StockManager.Adapters.KrxArenaPortAdapter(krxArena));", form);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("StockManager.sln");
        }
    }
}
