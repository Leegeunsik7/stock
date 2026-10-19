// ============================================================================
// FrictionAccountingTests — [v12.18.25-DEV] 아레나 마찰비용(거래세 0.18% · 수수료 0.015%) · Gross/Net · Net 청산가치 리스크 판정
//
//  §1 정확 차감 — 롱 왕복(매수 수수료 · 매도 수수료 + 거래세) · 숏 왕복(개시 = 매도 → 거래세 · 커버 = 매수 수수료) · 체결별 원 미만 절사 = 독립 재계산 일치
//  §2 Gross/Net — Net = 자산 − 자본금 · Gross = Net + 수수료 + 세금(항등식) · trip 기록 Gross = Net + trip 수수료 + 세금
//  §3 리스크 — 시가평가 자산은 한도 위인데 퇴출 마찰(수수료 · 세금)을 빼면 한도 아래 = Net 청산가치 기준 동결(경계 1원 단위 대조)
//  §4 무오차 — 무작위 롱/숏 왕복 10만 체결: FeesWon/TaxWon = 체결별 Fee/Tax 독립 합 · 무포지션 현금 = 자본 + Σ Net 실현 · Gross 항등식(오차 0원)
//  §5 δ=0 — 10만 체결(세금/수수료 · 포렌식 갱신 포함) 0B
// ============================================================================
using System;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena;
using Xunit;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class FrictionAccountingTests
    {
        private const long Sym = 5930, T0 = 638_000_000_000_000_000, Ms = 10_000;
        private long _now = T0;

        private static OrderBookPayload Book(long ask, long bid, long qty = 1_000_000)
        {
            var b = new OrderBookPayload { SymbolCode = Sym };
            for (int l = 0; l < 10; l++)
            {
                b.AskPrice[l] = ask + l * 100; b.AskQty[l] = qty;
                b.BidPrice[l] = bid - l * 100; b.BidQty[l] = qty;
            }
            return b;
        }

        private static DualBookSimulator Sim(long capital = 100_000_000)
        {
            var sim = new DualBookSimulator(0, 0);
            sim.Reset(capital);
            sim.OnQuote(Book(70_000, 69_900));
            return sim;
        }

        private static void Mkt(DualBookSimulator sim, int dir, long qty)
        {
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Human, Sym, dir, qty, ArenaOrderKind.Market, 0, T0));
            sim.Process(T0);
        }

        // ════════════ §1 · §2 ════════════

        [Fact]
        public void LongRoundTrip_FeeBothSides_TaxOnSell_GrossNetIdentity()
        {
            var sim = Sim();
            var h = sim.Book(ArenaSide.Human);
            Mkt(sim, 1, 100);                                                     // 7,000,000 · 수수료 1,050
            sim.OnQuote(Book(71_100, 71_000));
            Mkt(sim, -1, 100);                                                    // 7,100,000 · 수수료 1,065 · 세금 12,780
            Assert.Equal((2_115L, 12_780L), (h.FeesWon, h.TaxWon));
            long net = 7_100_000 - 1_065 - 12_780 - (7_000_000 + 1_050);           // 85,105
            Assert.Equal(net, h.RealizedWon);
            long eq = sim.MarkToMarket(ArenaSide.Human, out _);
            sim.PnL(ArenaSide.Human, eq, out long gross, out long netPnl);
            Assert.Equal((100_000L, net), (gross, netPnl));                        // Gross = 가격 차 100원 × 100주
            Assert.True(h.Quality.TryLast(out var r));
            Assert.Equal((1, 70_000L, 71_000L, 100_000L, net, 2_115L, 12_780L), (r.Side, r.EntryPx, r.ExitPx, r.GrossWon, r.NetWon, r.FeesWon, r.TaxWon));
        }

        [Fact]
        public void ShortRoundTrip_TaxOnShortOpen_FeeOnCover_GrossNetIdentity()
        {
            var sim = Sim();
            var h = sim.Book(ArenaSide.Human);
            Mkt(sim, -1, 100);                                                    // 숏 개시 6,990,000 · 수수료 1,048 · 세금 12,582
            sim.OnQuote(Book(69_000, 68_900));
            Mkt(sim, 1, 100);                                                     // 커버 6,900,000 · 수수료 1,035
            Assert.Equal((1_048L + 1_035L, 12_582L), (h.FeesWon, h.TaxWon));
            long net = (6_990_000 - 1_048 - 12_582) - (6_900_000 + 1_035);
            Assert.Equal(net, h.RealizedWon);
            long eq = sim.MarkToMarket(ArenaSide.Human, out _);
            sim.PnL(ArenaSide.Human, eq, out long gross, out long netPnl);
            Assert.Equal((90_000L, net), (gross, netPnl));                         // 69,900 − 69,000 = 900원 × 100주
            Assert.True(h.Quality.TryLast(out var r));
            Assert.Equal((-1, 69_900L, 69_000L, 90_000L, net), (r.Side, r.EntryPx, r.ExitPx, r.GrossWon, r.NetWon));
        }

        [Theory]
        [InlineData(1, 0, 0)]                 // 1원 = 수수료/세금 절사 0
        [InlineData(6_666, 0, 11)]            // 6,666 × 0.015% = 0.9999 → 0 · × 0.18% = 11.99 → 11
        [InlineData(10_000, 1, 18)]
        [InlineData(70_000_000, 10_500, 126_000)]
        [InlineData(99_999_999_999, 14_999_999, 179_999_999)]
        public void PerFill_Truncation_Is_Floor(long notional, long fee, long tax)
            => Assert.Equal((fee, tax), (DualBookSimulator.Fee(notional), DualBookSimulator.Tax(notional)));

        // ════════════ §3 Net 청산가치 리스크 ════════════

        private void Step(ArenaMatchGovernor g) { _now += Ms; g.Step(_now); }

        [Fact]
        public void MarginCall_Uses_Net_LiquidationValue_Not_Gross_Mark()
        {
            // 롱 1,000 @ 70,000(원가 70,010,500) — 하한 9,700만. 매수 1호가 B 에서
            //   시가평가 자산 = 29,989,500 + 1,000B · 청산가치 = 그 − Fee(1,000B) − Tax(1,000B)
            //   B = 66,800: 시가평가 96,789,500 (하한 위 · 종전 판정 = 통과) · 청산가치 96,789,500 − 10,020 − 120,240 = 96,659,240 (하한 아래 아님)
            //   B = 66,900 은 이미 −3% 위 — 경계 탐색: 시가평가 > 하한 ≥ 청산가치 인 B 를 찾아 동결 확인
            var g = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: 0, latencyMaxTicks: 0);
            Assert.True(g.Start(100_000_000));
            Step(g);
            g.SetFocus(Sym);
            g.OnQuote(Book(70_000, 69_900));
            Step(g);
            Assert.Equal(ArenaReject.None, g.SubmitHuman(Sym, 1, 1_000));
            Step(g);
            const long floor = 97_000_000, cashAfterBuy = 100_000_000 - 70_010_500;
            long bid = 0;
            for (long b = 67_200; b > 66_800; b -= 1)                             // 1원 단위 탐색(호가 단위 무관 · 시뮬레이터는 표시 가격 그대로 사용)
            {
                long mark = cashAfterBuy + 1_000 * b;
                long liq = mark - DualBookSimulator.Fee(1_000 * b) - DualBookSimulator.Tax(1_000 * b);
                if (mark > floor && liq <= floor) { bid = b; break; }
            }
            Assert.True(bid > 0, "경계 가격 없음");
            g.OnQuote(Book(bid + 100, bid));
            Step(g);
            Assert.True(g.TryRead(out var s));
            Assert.True(s.HumanRiskLocked, $"B={bid}: 시가평가 {cashAfterBuy + 1_000 * bid:N0} > 하한 · 청산가치 ≤ 하한 → 동결");
            long expectLiq = cashAfterBuy + 1_000 * bid - DualBookSimulator.Fee(1_000 * bid) - DualBookSimulator.Tax(1_000 * bid);
            Assert.Equal(expectLiq, s.RiskTripEquityWon);
            Assert.True(expectLiq <= floor && cashAfterBuy + 1_000 * bid > floor);

            var g2 = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: 0, latencyMaxTicks: 0);   // 1원 위 = 청산가치도 하한 위 → 미격발
            Assert.True(g2.Start(100_000_000));
            Step(g2); g2.SetFocus(Sym); g2.OnQuote(Book(70_000, 69_900)); Step(g2);
            g2.SubmitHuman(Sym, 1, 1_000); Step(g2);
            long safe = bid + 1;
            g2.OnQuote(Book(safe + 100, safe));
            Step(g2);
            Assert.True(g2.TryRead(out var s2));
            long liq2 = cashAfterBuy + 1_000 * safe - DualBookSimulator.Fee(1_000 * safe) - DualBookSimulator.Tax(1_000 * safe);
            Assert.Equal(liq2 <= floor, s2.HumanRiskLocked);
            Assert.False(s2.HumanRiskLocked);
            Assert.Equal(liq2, s2.Human.LiquidationEquityWon);
            Assert.Equal(s2.Human.NetPnlWon + s2.Human.FeesWon + s2.Human.TaxWon, s2.Human.GrossPnlWon);
        }

        // ════════════ §4 무오차 ════════════

        [Fact]
        public void HundredThousand_Random_Fills_Ledger_Exact_NoRoundingDrift()
        {
            var sim = Sim(10_000_000_000);
            var h = sim.Book(ArenaSide.Human);
            var rng = new Random(20261019);
            long feeSum = 0, taxSum = 0, fills = 0;
            long pos = 0;
            while (fills < 100_000)
            {
                long bid = 50_000 + rng.Next(0, 400) * 10, ask = bid + 10 + rng.Next(0, 3) * 10;
                sim.OnQuote(Book(ask, bid));
                long q = 1 + rng.Next(0, 37);
                int dir;
                if (pos == 0) dir = rng.Next(2) == 0 ? 1 : -1;                    // 롱/숏 개시
                else if (pos > 0) { dir = rng.Next(3) == 0 ? 1 : -1; if (dir < 0) q = Math.Min(q, pos); }
                else { dir = rng.Next(3) == 0 ? -1 : 1; if (dir > 0) q = Math.Min(q, -pos); }
                long px = dir > 0 ? ask : bid, n = q * px;
                bool opensShort = dir < 0 && pos <= 0, closesLong = dir < 0 && pos > 0;
                Mkt(sim, dir, q);
                feeSum += DualBookSimulator.Fee(n);
                if (opensShort || closesLong) taxSum += DualBookSimulator.Tax(n);
                pos += dir * q;
                Assert.Equal(pos, h.Position(Sym));
                fills++;
            }
            if (pos != 0) { sim.OnQuote(Book(55_000, 54_990)); long n = Math.Abs(pos) * (pos > 0 ? 54_990 : 55_000); Mkt(sim, pos > 0 ? -1 : 1, Math.Abs(pos)); feeSum += DualBookSimulator.Fee(n); if (pos > 0) taxSum += DualBookSimulator.Tax(n); }
            Assert.Equal(0, h.Position(Sym));
            Assert.Equal(feeSum, h.FeesWon);                                       // ★체결별 절사 독립 재계산 = 원장(오차 0원)
            Assert.Equal(taxSum, h.TaxWon);
            Assert.Equal(10_000_000_000 + h.RealizedWon, h.CashWon);               // ★무포지션 현금 = 자본 + Σ Net 실현
            Assert.Equal(0, h.ShortLockedWon);
            long eq = sim.MarkToMarket(ArenaSide.Human, out long u);
            Assert.Equal(0, u);
            sim.PnL(ArenaSide.Human, eq, out long gross, out long net);
            Assert.Equal(h.RealizedWon, net);
            Assert.Equal(net + feeSum + taxSum, gross);
            var q2 = h.Quality.Summary();
            Assert.Equal(h.RealizedWon, q2.NetWon);                                // ★trip 기록 Net 합 = 원장 실현
            Assert.Equal(feeSum, q2.FeesWon);
            Assert.Equal(taxSum, q2.TaxWon);
            Assert.Equal(q2.NetWon + q2.FeesWon + q2.TaxWon, q2.GrossWon);
        }

        // ════════════ §5 δ=0 ════════════

        [Fact]
        public void HundredThousand_Fills_FrictionAndForensics_ZeroAlloc()
        {
            var sim = Sim(1_000_000_000_000);
            var up = Book(70_100, 70_000); var dn = Book(70_000, 69_900);
            Mkt(sim, 1, 1); Mkt(sim, -1, 1); Mkt(sim, -1, 1); Mkt(sim, 1, 1);    // 예열(롱 · 숏 경로)
            long min = long.MaxValue;
            for (int w = 0; w < 3 && min != 0; w++)
            {
                long a = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 25_000; i++)                                   // 측정 루프 = 엔진 호출만(xUnit Assert 는 비교자 할당 — 루프 밖)
                {
                    sim.OnQuote(in up); sim.Submit(ArenaSide.Human, Sym, 1, 3, ArenaOrderKind.Market, 0, T0); sim.Process(T0);    // 롱 개시
                    sim.OnQuote(in dn); sim.Submit(ArenaSide.Human, Sym, -1, 3, ArenaOrderKind.Market, 0, T0); sim.Process(T0);   // 롱 청산(세금)
                    sim.OnQuote(in up); sim.Submit(ArenaSide.Human, Sym, -1, 3, ArenaOrderKind.Market, 0, T0); sim.Process(T0);   // 숏 개시(세금)
                    sim.OnQuote(in dn); sim.Submit(ArenaSide.Human, Sym, 1, 3, ArenaOrderKind.Market, 0, T0); sim.Process(T0);    // 숏 커버
                    if ((i & 1023) == 0) { long eq = sim.MarkToMarket(ArenaSide.Human, out _); sim.PnL(ArenaSide.Human, eq, out _, out _); _ = sim.LiquidationEquity(ArenaSide.Human); _ = sim.Debrief(ArenaSide.Human, eq, 0); }
                }
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - a);
            }
            Assert.Equal(0, min);
            Assert.True(sim.Book(ArenaSide.Human).Quality.Recorded >= 50_000);
        }
    }
}
