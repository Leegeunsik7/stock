// ============================================================================
// ArenaCouncilVetoTests — v12.18.88-GM: 3대 아레나 AI 진입 경로 × PlutoCognitiveCouncil 리스크 통제관 비토 결선
//
//  §1 차단 — 보드 비토(VIX ≥ 35 등) = KRX/US/Upbit AI 진입 0 · 근거 0 · 기각 로그 1건(종목 · 원점수 · 사유 · 셀) · 비토 해제 = 기존 진입 그대로
//  §2 기각 로그 — 같은 사유 연속 = 로그 1건 · 차단 계수만 증가 · 사유 전이 = 새 로그 · 원장 초기화 = 로그 초기화(결속 유지)
//  §3 fail-closed — 보드 판독 불가(seqlock 홀수 고착) = Unreadable 차단 · 종목 패닉 투매 국면 = PanicLiquidation 차단 · 미결속 = 기존 동작(로그 0)
//  §4 E2E — PlutoCouncilHub(거시 VIX 36) Prepare → PublishBoard → 아레나 진입 차단 · 포트 어댑터 피드(사유 문구) · 미결속 = Unwired
//  §5 δ=0 — 비토 결속 상태 진입 후보 10만 회(차단 · 사유 전이 로그 · 판독 포함) 힙 할당 0B(Release · MinAllocated 3창)
// ============================================================================
using System;
using System.Reflection;
using StockManager.Adapters;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Council;
using StockManager.Engines.Cognitive.Learning;
using StockManager.Engines.Pricing;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class ArenaCouncilVetoTests
    {
        private const long Samsung = 5930;
        private static readonly DateTime Open10 = new(2026, 10, 22, 10, 0, 0);   // 목요일 10:00 KST
        private readonly ITestOutputHelper _out;
        private DateTime _t = Open10;

        public ArenaCouncilVetoTests(ITestOutputHelper o) => _out = o;

        private static PlutoCouncilBoard Board(PlutoVetoReason veto, long seq = 1)
        {
            var b = new PlutoCouncilBoard();
            Publish(b, veto, seq);
            return b;
        }

        private static void Publish(PlutoCouncilBoard b, PlutoVetoReason veto, long seq)
        {
            var w = PlutoCognitiveCouncil.BaseOf(0);
            for (int c = 0; c < PlutoCouncilBoard.Cells; c++) b.Publish(c, new PlutoCouncilCell(in w, default, 0, veto, seq));
        }

        // ── KRX 헬퍼(KrxArenaEngineTests 와 같은 장세: 워밍업 → 점수 ≈ 5471 진입) ──
        private void Q(KrxArenaEngine e, long bid, long ask, long bidQty = 1000, long askQty = 1000, long vol = 100)
        {
            _t = _t.AddMilliseconds(100);
            e.OnQuote(Samsung, bid, ask, bidQty, askQty, vol, _t);
        }

        private void Warm(KrxArenaEngine e)
        {
            for (int i = 0; i < KrxArenaEngine.WarmupTicks + 1; i++) Q(e, 70_000, 70_100);
        }

        private void Spike(KrxArenaEngine e, long bid = 70_100, long ask = 70_200)
        {
            e.SetOvernightMacro(3000);
            Q(e, bid, ask, bidQty: 9000, askQty: 1000, vol: 1000);
        }

        private static ArenaVetoRecord[] Vetoes(Func<ArenaVetoRecord[], (int N, long Blocked)> copy, out long blocked)
        {
            var buf = new ArenaVetoRecord[ArenaCouncilVetoGate.Capacity];
            var (n, b) = copy(buf);
            blocked = b;
            return buf[..n];
        }

        private static ArenaVetoRecord[] Vetoes(KrxArenaEngine e, out long blocked)
            => Vetoes(buf => (e.CopyCouncilVetoes(buf, out long b), b), out blocked);

        private static int RationaleCount(KrxArenaEngine e)
        {
            var buf = new AiTradeRationale[KrxArenaEngine.RationaleCapacity];
            return e.CopyRationales(buf);
        }

        // ── §1 차단 ─────────────────────────────────────────────────────────────

        [Fact]
        public void Krx_Veto_Blocks_Ai_Entry_And_Logs_Rejection_Then_Clear_Restores_Entry()
        {
            var e = new KrxArenaEngine();
            var board = Board(PlutoVetoReason.VixSpike);
            e.AttachCouncil(board);
            Assert.True(e.CouncilAttached);
            Warm(e);
            Spike(e);

            Assert.Equal(0, RationaleCount(e));
            Assert.True(e.TryReadSnapshot(out var s));
            Assert.Equal(0, s.Ai.Fills);
            Assert.Equal(0, s.Ai.OpenPositions);
            var v = Assert.Single(Vetoes(e, out long blocked));
            Assert.Equal(1, blocked);
            Assert.True(v.IsWellFormed);
            Assert.Equal(Samsung, v.Symbol);
            Assert.Equal(PlutoVetoReason.VixSpike, v.Veto);
            Assert.InRange(v.Score, KrxArenaEngine.EntryScore, 10000);
            Assert.Equal(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, 0), v.Cell);
            Assert.Equal(1, v.BoardSeq);
            Assert.Equal(_t.Ticks, v.KstTicks);

            Publish(board, PlutoVetoReason.None, 2);                   // 비토 해제 = 기존 진입 그대로
            Spike(e, 70_200, 70_300);
            Assert.Equal(1, RationaleCount(e));
            Assert.True(e.TryReadSnapshot(out s));
            Assert.Equal(1, s.Ai.Fills);
            Assert.Single(Vetoes(e, out blocked));
            Assert.Equal(1, blocked);
        }

        [Fact]
        public void Krx_Attached_Board_Without_Veto_Enters_Exactly_Like_Unattached()
        {
            var plain = new KrxArenaEngine();
            var wired = new KrxArenaEngine();
            wired.AttachCouncil(Board(PlutoVetoReason.None));
            Assert.False(plain.CouncilAttached);
            foreach (var e in new[] { plain, wired }) { _t = Open10; Warm(e); Spike(e); }
            var a = new AiTradeRationale[4];
            var b = new AiTradeRationale[4];
            Assert.Equal(1, plain.CopyRationales(a));
            Assert.Equal(1, wired.CopyRationales(b));
            Assert.Equal(a[0].Qty, b[0].Qty);
            Assert.Equal(a[0].PriceWon, b[0].PriceWon);
            Assert.Equal(a[0].SentimentScore, b[0].SentimentScore);
            Assert.Empty(Vetoes(wired, out long blocked));
            Assert.Equal(0, blocked);
            Assert.Empty(Vetoes(plain, out blocked));
        }

        [Fact]
        public void Us_Veto_Blocks_Ai_Entry_And_Logs_Rejection()
        {
            var e = new UsTechArenaEngine();
            e.AttachCouncil(Board(PlutoVetoReason.SpilloverShock));
            var t = new DateTime(2026, 10, 22, 23, 0, 0);            // 정규장 23:00 KST
            long nvda = UsTechUniverse.Symbol(UsTechUniverse.Nvda);
            for (int i = 0; i < UsTechArenaEngine.WarmupTicks + 1; i++) { t = t.AddMilliseconds(100); e.OnQuote(nvda, 13_000, 13_001, 1000, 1000, t); }
            e.SetMarketContext(300, 300, -10);
            t = t.AddMilliseconds(100);
            e.OnQuote(nvda, 13_050, 13_051, 9000, 1000, t);

            Assert.True(e.TryReadSnapshot(out var s));
            Assert.Equal(0, s.Ai.Fills);
            Assert.Equal(0, s.RationaleCount);
            var buf = new ArenaVetoRecord[8];
            Assert.Equal(1, e.CopyCouncilVetoes(buf, out long blocked));
            var v = buf[0];
            Assert.Equal(1, blocked);
            Assert.Equal(nvda, v.Symbol);
            Assert.Equal(PlutoVetoReason.SpilloverShock, v.Veto);
            Assert.Equal(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Us, 0), v.Cell);
            Assert.InRange(v.Score, UsTechArenaEngine.EntryScore, 10000);
        }

        [Fact]
        public void Upbit_Veto_Blocks_Ai_Entry_And_Logs_Rejection()
        {
            const long S = UpbitTickAligner.Scale;
            var e = new UpbitCryptoArenaEngine();
            e.AttachCouncil(Board(PlutoVetoReason.PanicRegimeMatch));
            long btc = CryptoArenaUniverse.Symbol(CryptoArenaUniverse.Btc);
            long t = (new DateTime(2026, 10, 24, 3, 0, 0).Ticks - DateTime.UnixEpoch.Ticks) / 10;
            for (int i = 0; i < UpbitCryptoArenaEngine.WarmupTicks + 1; i++) { t += 100_000; e.OnQuote(btc, 150_000_000 * S, 150_001_000 * S, S, S, t); }
            e.SetMarketContext(0, 300);
            for (int i = 0; i < 4; i++) { t += 1_000; e.OnTrade(btc, 150_001_000 * S, S / 2, 1, t); }
            t += 100_000;
            e.OnQuote(btc, 150_100_000 * S, 150_101_000 * S, 9 * S, 1 * S, t);

            Assert.True(e.TryReadSnapshot(out var s));
            Assert.Equal(0, s.Ai.Fills);
            Assert.Equal(0, s.RationaleCount);
            var buf = new ArenaVetoRecord[8];
            Assert.Equal(1, e.CopyCouncilVetoes(buf, out long blocked));
            Assert.Equal(1, blocked);
            Assert.Equal(btc, buf[0].Symbol);
            Assert.Equal(PlutoVetoReason.PanicRegimeMatch, buf[0].Veto);
            Assert.Equal(PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Upbit, 0), buf[0].Cell);
            Assert.True(buf[0].IsWellFormed);
        }

        // ── §2 기각 로그 ────────────────────────────────────────────────────────

        [Fact]
        public void Same_Reason_Logs_Once_Reason_Transition_Logs_Again_And_Reset_Clears()
        {
            var e = new KrxArenaEngine();
            var board = Board(PlutoVetoReason.VixSpike);
            e.AttachCouncil(board);
            Warm(e);
            for (int i = 0; i < 5; i++) Spike(e, 70_100 + i * 100, 70_200 + i * 100);
            Assert.Single(Vetoes(e, out long blocked));
            Assert.Equal(5, blocked);

            Publish(board, PlutoVetoReason.VixSpike | PlutoVetoReason.SpilloverShock, 2);
            Spike(e, 70_700, 70_800);
            var log = Vetoes(e, out blocked);
            Assert.Equal(2, log.Length);
            Assert.Equal(6, blocked);
            Assert.Equal(new long[] { 1, 2 }, new[] { log[0].Seq, log[1].Seq });
            Assert.Equal(PlutoVetoReason.VixSpike | PlutoVetoReason.SpilloverShock, log[1].Veto);
            Assert.Equal(2, log[1].BoardSeq);
            Assert.Equal(0, RationaleCount(e));

            e.SetCapital(KrxArenaEngine.DefaultCapitalWon);             // 원장 초기화 = 로그 초기화 · 결속 유지
            Assert.Empty(Vetoes(e, out blocked));
            Assert.Equal(0, blocked);
            Assert.True(e.CouncilAttached);
        }

        [Fact]
        public void Veto_Text_Names_Every_Reason_Including_FailClosed()
        {
            string all = PlutoCognitiveCouncil.VetoText(PlutoVetoReason.VixSpike | PlutoVetoReason.SpilloverShock | PlutoVetoReason.PanicRegimeMatch
                                                        | PlutoVetoReason.PanicLiquidation | PlutoVetoReason.Unreadable);
            foreach (var must in new[] { "리스크 통제관 비토", "VIX ≥ 35", "교차 자산 충격", "과거 패닉 국면 유사", "패닉 투매 국면", "판독 불가(fail-closed)" })
                Assert.Contains(must, all);
            Assert.Equal("", PlutoCognitiveCouncil.VetoText(PlutoVetoReason.None));
        }

        // ── §3 fail-closed ──────────────────────────────────────────────────────

        [Fact]
        public void Unreadable_Board_And_Symbol_Panic_Liquidation_Block_FailClosed()
        {
            var board = Board(PlutoVetoReason.None);
            var gate = new ArenaCouncilVetoGate(PlutoAssetClass.Krx, 4);
            Assert.False(gate.Blocks(0, Samsung, MarketRegime.TrendingBull, 0, 5000, Open10.Ticks));   // 미결속 = 기존 동작
            gate.Attach(board);
            Assert.False(gate.Blocks(0, Samsung, MarketRegime.TrendingBull, 0, 5000, Open10.Ticks));
            Assert.True(gate.Blocks(0, Samsung, MarketRegime.PanicLiquidation, 0, 5000, Open10.Ticks));

            var ver = (long[])typeof(PlutoCouncilBoard).GetField("_ver", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(board)!;
            int cell = PlutoCognitiveTensorLake.CellOf(PlutoAssetClass.Krx, 3);
            ver[cell] = 1;                                              // 작성 중 고착 = 경합 상한 초과
            Assert.True(gate.Blocks(1, Samsung + 1, MarketRegime.TrendingBull, 3, 5000, Open10.Ticks));
            var buf = new ArenaVetoRecord[8];
            int n = gate.Copy(buf);
            Assert.Equal(2, n);
            Assert.Equal(PlutoVetoReason.PanicLiquidation, buf[0].Veto);
            Assert.Equal(PlutoVetoReason.Unreadable, buf[1].Veto);
            Assert.Equal(cell, buf[1].Cell);
            Assert.Equal(2, gate.Blocked);
            Assert.True(board.ContendedReads >= 1);
        }

        // ── §4 E2E · 포트 ───────────────────────────────────────────────────────

        [Fact]
        public void Hub_Macro_Vix_Spike_Publishes_Board_Veto_That_Blocks_Arena_And_Reaches_Port_Feed()
        {
            var snap = new PlutoMacroSnapshot(PlutoMacroField.Vix, 3600, 0, 0, 0, 0, 0, Open10.Ticks, 1);
            var hub = new PlutoCouncilHub(new PlutoCrossAssetSpillover(), new PlutoCouncilBoard(), macro: () => (true, snap));
            var m = hub.Prepare(Open10.Ticks);
            hub.PublishBoard(in m, _ => default);
            Assert.True(hub.Board.TryRead(PlutoAssetClass.Krx, 0, out var cell));
            Assert.Equal(PlutoVetoReason.VixSpike, cell.Veto & PlutoVetoReason.VixSpike);

            var e = new KrxArenaEngine();
            var port = new KrxArenaPortAdapter(e);
            Assert.False(port.CouncilVetoes().Attached);              // 미결속 = Unwired(UI 미표시)
            e.AttachCouncil(hub.Board);
            Warm(e);
            Spike(e);
            Assert.Equal(0, RationaleCount(e));

            var feed = port.CouncilVetoes();
            Assert.True(feed.Attached);
            Assert.Equal(1, feed.Blocked);
            var row = Assert.Single(feed.Recent);
            _out.WriteLine($"{row.Symbol} {row.Score} {row.Reason} cell={row.Cell} {row.AtKst:O}");
            Assert.Equal("005930", row.Symbol);
            Assert.Contains("VIX ≥ 35", row.Reason);
            Assert.Equal(_t, row.AtKst);
            Assert.Empty(port.CouncilVetoes(0).Recent);
        }

        [Fact]
        public void Us_And_Crypto_Ports_Report_Unwired_Until_Attached()
        {
            var us = new UsTechArenaEngine();
            var crypto = new UpbitCryptoArenaEngine();
            Assert.False(new UsArenaPortAdapter(us).CouncilVetoes().Attached);
            Assert.False(new CryptoArenaPortAdapter(crypto).CouncilVetoes().Attached);
            var board = Board(PlutoVetoReason.None);
            us.AttachCouncil(board);
            crypto.AttachCouncil(board);
            var uf = new UsArenaPortAdapter(us).CouncilVetoes();
            var cf = new CryptoArenaPortAdapter(crypto).CouncilVetoes();
            Assert.True(uf.Attached && cf.Attached);
            Assert.Empty(uf.Recent);
            Assert.Empty(cf.Recent);
        }

        // ── §5 δ=0 ──────────────────────────────────────────────────────────────

        [Fact]
        public void HotPath_100k_Vetoed_Entry_Candidates_Allocate_Zero_Bytes()
        {
            var e = new KrxArenaEngine();
            var board = Board(PlutoVetoReason.VixSpike);
            e.AttachCouncil(board);
            var log = new ArenaVetoRecord[ArenaCouncilVetoGate.Capacity];
            Warm(e);
            long i0 = 0;

            void Run(int n)
            {
                for (int i = 0; i < n; i++, i0++)
                {
                    long px = 70_000 + (i0 & 15) * 100;
                    _t = Open10.AddTicks(TimeSpan.TicksPerMillisecond * (i0 & 0xFFFFF));   // 정규장 창 안 순환(시각 단조 · ~17분 주기)
                    e.OnQuote(Samsung, px, px + 100, 9000, 1000, (i0 & 1) == 0 ? 100 : 1000, _t);   // 불균형 +8000 · 매크로 → 진입 후보 → 비토 차단
                    if ((i0 & 255) == 0) Publish(board, (i0 & 256) == 0 ? PlutoVetoReason.SpilloverShock : PlutoVetoReason.VixSpike, i0 + 2);   // 사유 전이 = 로그 기록 경로
                    if ((i0 & 63) == 0) { e.CopyCouncilVetoes(log, out _); e.TryReadSnapshot(out _); }
                }
            }

            e.SetOvernightMacro(3000);
            Run(20_000);   // JIT · 티어링 워밍업
            long bytes = MinAllocated(() => Run(100_000));
            Assert.Equal(0, RationaleCount(e));
            int n = e.CopyCouncilVetoes(log, out long blocked);
            _out.WriteLine($"δ={bytes}B blocked={blocked} log={n}");
            Assert.True(blocked >= 60_000);
            Assert.True(n > 1);
            Assert.Equal(0, bytes);
        }
    }
}
