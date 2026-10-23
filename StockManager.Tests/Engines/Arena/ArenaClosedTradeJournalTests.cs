// ============================================================================
// ArenaClosedTradeJournalTests — v12.18.70-DEV: AI 실현손익 체결 내역(청산 저널) · 한글 종목명 매핑 · /dualarena 손익 시각화
//
//  §1 매핑 — StockDisplayNameResolver 국내/미국/코인 한글명 · "SK하이닉스 000660" 병기 · 미등록 = 코드(날조 0) · 대소문자/공백/null
//  §2 산식 — ArenaClosedTradeMath.ReturnBp 정확값 · 코인 스케일 · 진입가/수량 0 가드 · Int128 오버플로 안전 포화
//  §3 매퍼 — 매도 근거만 최신순 · max 상한 · 청산 사유 → 유형(익절/손절/장마감/반전/악재/과열) · 진입가 미기록 = 수익률 없음(가짜 0% 금지)
//  §4 엔진+어댑터 — 국내/미국/코인 왕복 체결: 청산 근거 EntryPrice = 진입 평단 · 매수 근거 = 0 · 저널 실현손익 = 원장 실현 · 한글명
//  §5 페이지 — 3대 탭 [📜 AI 실현손익 체결 내역] 표(한글명 · 진입가 · 청산가 · 손익 · 수익률 · 유형) · 보유/티켓/카드 한글명 · [실현손익 칩]
//  §6 경계 — 청산 없음/펌프 미결선 정직 렌더 · Portable 신규 파일 엔진 참조 0 · 포트 기본 구현 = 빈 목록
// ============================================================================
using System;
using System.IO;
using System.Linq;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Pricing;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class ArenaClosedTradeJournalTests
    {
        private const long Samsung = 5930, Hynix = 660;
        private static readonly DateTime Open10 = new(2026, 10, 22, 10, 0, 0);          // 목요일 10:00 KST
        private static readonly DateTime Reg23 = new(2026, 10, 22, 23, 0, 0);           // 미 정규장(EDT) 23:00 KST
        private static readonly long Sat = (new DateTime(2026, 10, 24, 3, 0, 0).Ticks - DateTime.UnixEpoch.Ticks) / 10;   // UTC µs
        private const long S = UpbitTickAligner.Scale;

        private readonly ITestOutputHelper _out;
        private DateTime _t = Open10;

        public ArenaClosedTradeJournalTests(ITestOutputHelper o) => _out = o;

        // ── 국내 시나리오 헬퍼 ────────────────────────────────────────────────

        private void Q(KrxArenaEngine e, long sym, long bid, long ask, long bidQty = 1000, long askQty = 1000, long vol = 100)
        {
            _t = _t.AddMilliseconds(100);
            e.OnQuote(sym, bid, ask, bidQty, askQty, vol, _t);
        }

        private void KrxEnter(KrxArenaEngine e, long sym = Samsung)
        {
            for (int i = 0; i < KrxArenaEngine.WarmupTicks + 1; i++) Q(e, sym, 70_000, 70_100);
            e.SetOvernightMacro(3000);
            Q(e, sym, 70_100, 70_200, bidQty: 9000, askQty: 1000, vol: 1000);
        }

        private static AiTradeRationale[] Rationales(KrxArenaEngine e)
        {
            var buf = new AiTradeRationale[KrxArenaEngine.RationaleCapacity];
            return buf.AsSpan(0, e.CopyRationales(buf)).ToArray();
        }

        private static KrxArenaQuoteFeeder Pumped(KrxArenaEngine e)
        {
            var f = new KrxArenaQuoteFeeder(e);
            f.Pump(Open10.AddHours(-9).Ticks);
            return f;
        }

        // ── 미국 시나리오 헬퍼 ────────────────────────────────────────────────

        private static void QU(UsTechArenaEngine e, ref DateTime t, long bid, long ask, long bidQty = 1000, long askQty = 1000)
        {
            t = t.AddMilliseconds(100);
            e.OnQuote(UsTechUniverse.Symbol(UsTechUniverse.Nvda), bid, ask, bidQty, askQty, t);
        }

        private static AiTradeRationale UsEnter(UsTechArenaEngine e, ref DateTime t)
        {
            for (int i = 0; i < UsTechArenaEngine.WarmupTicks + 1; i++) QU(e, ref t, 13_000, 13_001);
            e.SetMarketContext(300, 300, -10);
            QU(e, ref t, 13_050, 13_051, bidQty: 9000, askQty: 1000);
            var buf = new AiTradeRationale[UsTechArenaEngine.RationaleCapacity];
            Assert.Equal(1, e.CopyRationales(buf));
            return buf[0];
        }

        // ── 코인 시나리오 헬퍼 ────────────────────────────────────────────────

        private static void QC(UpbitCryptoArenaEngine e, ref long t, long bidWon, long askWon, long bidQty = 1 * S, long askQty = 1 * S)
        {
            t += 100_000;
            e.OnQuote(CryptoArenaUniverse.Symbol(CryptoArenaUniverse.Btc), bidWon * S, askWon * S, bidQty, askQty, t);
        }

        private static AiTradeRationale CryptoEnter(UpbitCryptoArenaEngine e, ref long t)
        {
            for (int i = 0; i < UpbitCryptoArenaEngine.WarmupTicks + 1; i++) QC(e, ref t, 150_000_000, 150_001_000);
            e.SetMarketContext(0, 300);
            for (int i = 0; i < 4; i++) { t += 1_000; e.OnTrade(CryptoArenaUniverse.Symbol(CryptoArenaUniverse.Btc), 150_001_000 * S, S / 2, 1, t); }
            QC(e, ref t, 150_100_000, 150_101_000, 9 * S, 1 * S);
            var buf = new AiTradeRationale[UpbitCryptoArenaEngine.RationaleCapacity];
            Assert.Equal(1, e.CopyRationales(buf));
            return buf[0];
        }

        private static (Bunit.TestContext Ctx, IRenderedComponent<DualArenaPage> Cut) Render(IKrxArenaPort krx, IUsArenaPort us, ICryptoArenaPort crypto)
        {
            var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton(krx);
            ctx.Services.AddSingleton(us);
            ctx.Services.AddSingleton(crypto);
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);
            return (ctx, ctx.RenderComponent<DualArenaPage>());
        }

        private static AiTradeRationale Exit(long seq, long symbol, ArenaTrigger t, long entry, long exit, long qty, long pnl) =>
            new(seq, symbol, -1, qty, exit, Open10.Ticks + seq, t, 0, 100, entry + 100, entry - 100, 100, 0, 0, 0, 0, pnl, entryPrice: entry);

        private static AiTradeRationale Entry(long seq, long symbol, long px, long qty) =>
            new(seq, symbol, 1, qty, px, Open10.Ticks + seq, ArenaTrigger.OrderbookImbalance, 5000, 50, px + 100, px - 100, 100, 0, 0, 0, 0, 0);

        // ── §1 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Resolver_Maps_Domestic_Us_Crypto_Codes_To_Korean_Names_And_Never_Fabricates()
        {
            Assert.Equal("삼성전자", StockDisplayNameResolver.Name("005930"));
            Assert.Equal("SK하이닉스", StockDisplayNameResolver.Name("000660"));
            Assert.Equal("삼성바이오로직스", StockDisplayNameResolver.Name("207940"));
            Assert.Equal("현대차", StockDisplayNameResolver.Name("005380"));
            Assert.Equal("셀트리온", StockDisplayNameResolver.Name("068270"));
            Assert.Equal("LG에너지솔루션", StockDisplayNameResolver.Name(373220));
            Assert.Equal("SK하이닉스", StockDisplayNameResolver.Name(Hynix));
            foreach (var (code, name) in new[] { ("AAPL", "애플"), ("NVDA", "엔비디아"), ("MSFT", "마이크로소프트"), ("AMZN", "아마존"), ("TSLA", "테슬라") })
                Assert.Equal(name, StockDisplayNameResolver.Name(code));
            Assert.Equal("비트코인", StockDisplayNameResolver.Name("KRW-BTC"));
            Assert.Equal("이더리움", StockDisplayNameResolver.Name("KRW-ETH"));
            Assert.Equal("엔비디아", StockDisplayNameResolver.Name(" nvda "));               // 대소문자 · 공백 무관

            Assert.Equal("SK하이닉스 000660", StockDisplayNameResolver.Display(Hynix));
            Assert.Equal("엔비디아 NVDA", StockDisplayNameResolver.Display("NVDA"));
            Assert.Equal("비트코인 KRW-BTC", StockDisplayNameResolver.Display("KRW-BTC"));
            Assert.Equal("000660", StockDisplayNameResolver.KrxCode(660));

            // 미등록 = 이름 없음 · 코드 그대로(가짜 이름 0)
            Assert.Null(StockDisplayNameResolver.Name("999999"));
            Assert.Null(StockDisplayNameResolver.Name(""));
            Assert.Null(StockDisplayNameResolver.Name((string?)null));
            Assert.Equal("123456", StockDisplayNameResolver.Display(123456));
            Assert.Equal("ZZZZ", StockDisplayNameResolver.NameOrCode("ZZZZ"));

            // 아레나 유니버스(합성 데모 종목 · 미국 5종 · 코인 4종) 전부 등록
            foreach (long sym in KrxArenaQuoteFeeder.SyntheticSymbols) Assert.NotNull(StockDisplayNameResolver.Name(sym));
            for (int i = 0; i < 5; i++) Assert.NotNull(StockDisplayNameResolver.Name(UsTechUniverse.Unpack(UsTechUniverse.Symbol(i))));
            for (int i = 0; i < 4; i++) Assert.NotNull(StockDisplayNameResolver.Name(CryptoArenaUniverse.Label(CryptoArenaUniverse.Symbol(i))));
        }

        // ── §2 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void ReturnBp_Is_Exact_Scaled_Guarded_And_Overflow_Safe()
        {
            Assert.Equal(170, ArenaClosedTradeMath.ReturnBp(70_000, 500, 595_000));          // 595,000 ÷ 35,000,000 = 1.70%
            Assert.Equal(-200, ArenaClosedTradeMath.ReturnBp(10_000, 100, -20_000));
            Assert.Equal(0, ArenaClosedTradeMath.ReturnBp(10_000, 100, 0));
            Assert.Equal(1, ArenaClosedTradeMath.ReturnBp(10_000, 3, 5));                   // 5 ÷ 30,000 = 1.66bp → 0 방향 절단 1
            // 코인: 가격 150,000,000원 raw × 0.2 BTC raw ÷ 10^8 = 30,000,000원 raw → 손익 +300,000원 raw = +100bp
            Assert.Equal(100, ArenaClosedTradeMath.ReturnBp(150_000_000 * S, S / 5, 300_000 * S, S));
            Assert.Equal(0, ArenaClosedTradeMath.ReturnBp(0, 100, 1_000));
            Assert.Equal(0, ArenaClosedTradeMath.ReturnBp(100, 0, 1_000));
            Assert.Equal(0, ArenaClosedTradeMath.ReturnBp(100, 10, 1_000, 0));
            Assert.Equal(0, ArenaClosedTradeMath.ReturnBp(1, 1, 1_000, S));                  // 원가 0 절단 = 0(나눗셈 0 방지)
            Assert.Equal(1_000_000_000, ArenaClosedTradeMath.ReturnBp(1, 1, long.MaxValue));
            Assert.Equal(-1_000_000_000, ArenaClosedTradeMath.ReturnBp(1, 1, long.MinValue));
            Assert.Equal(0, ArenaClosedTradeMath.ReturnBp(long.MaxValue, long.MaxValue, 1));
        }

        // ── §3 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Mapper_Takes_Only_Exits_Newest_First_Capped_With_Kinds_And_Honest_Missing_Entry()
        {
            var ring = new[]
            {
                Entry(1, Samsung, 70_000, 10),
                Exit(2, Samsung, ArenaTrigger.TakeProfit, 70_000, 71_000, 10, 9_000),
                Entry(3, Hynix, 200_000, 5),
                Exit(4, Hynix, ArenaTrigger.StopLoss, 200_000, 198_000, 5, -11_000),
                Exit(5, 207940, ArenaTrigger.SessionClose, 0, 800_000, 1, 1_000),                 // 진입가 미기록
                Exit(6, 5380, ArenaTrigger.SignalReversal, 250_000, 251_000, 2, 1_500),
                Exit(7, 68270, ArenaTrigger.NewsShock, 180_000, 170_000, 1, -10_100),
                Exit(8, 999999, ArenaTrigger.ValuationBubble, 10_000, 11_000, 1, 900),
            };
            static string Code(long s) => s.ToString("D6");
            var all = ArenaClosedTradeMapper.Collect(ring, 50, Code, 1);
            Assert.Equal(new long[] { 8, 7, 6, 5, 4, 2 }, all.Select(x => x.Seq).ToArray());
            Assert.Equal(new[] { ArenaExitKind.ValuationBubble, ArenaExitKind.NewsShock, ArenaExitKind.SignalReversal, ArenaExitKind.SessionClose,
                                 ArenaExitKind.StopLoss, ArenaExitKind.TakeProfit }, all.Select(x => x.ExitKind).ToArray());
            Assert.Equal(new[] { "과열 조기 청산", "악재 긴급 청산", "시그널 반전", "장마감", "손절", "익절" }, all.Select(x => x.ExitLabel).ToArray());

            var tp = all[^1];
            Assert.Equal(("005930", "삼성전자", 10L, 70_000L, 71_000L, 9_000L), (tp.Code, tp.Name, tp.Qty, tp.EntryPrice, tp.ExitPrice, tp.RealizedPnl));
            Assert.Equal(128, tp.ReturnBp);                                                    // 9,000 ÷ 700,000 = 1.2857%
            Assert.Equal("목표가 도달(익절)", tp.Reason);
            Assert.Contains("[AI 매도] 005930", tp.Summary);
            Assert.Equal(new DateTime(Open10.Ticks + 2), tp.AtKst);

            var sl = all[^2];
            Assert.Equal("SK하이닉스", sl.Name);
            Assert.Equal(-110, sl.ReturnBp);

            var noEntry = all.Single(x => x.Seq == 5);
            Assert.False(noEntry.HasReturn);
            Assert.Equal(0, noEntry.ReturnBp);
            Assert.Equal("삼성바이오로직스", noEntry.Name);
            Assert.Equal("999999", all[0].Name);                                              // 미등록 = 코드

            Assert.Equal(new long[] { 8, 7 }, ArenaClosedTradeMapper.Collect(ring, 2, Code, 1).Select(x => x.Seq).ToArray());
            Assert.Empty(ArenaClosedTradeMapper.Collect(ring, 0, Code, 1));
            Assert.Empty(ArenaClosedTradeMapper.Collect(ReadOnlySpan<AiTradeRationale>.Empty, 50, Code, 1));
            Assert.Equal(ArenaExitKind.Other, ArenaClosedTradeMapper.Kind(ArenaTrigger.OrderbookImbalance));
        }

        // ── §4 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Krx_RoundTrips_Record_Entry_Average_And_Journal_Matches_Ledger()
        {
            var e = new KrxArenaEngine();
            var port = new KrxArenaPortAdapter(e);
            Assert.Empty(port.GetClosedTrades());

            KrxEnter(e);
            var entry = Rationales(e)[0];
            Assert.Equal(0, entry.EntryPrice);                                                 // 매수 근거 = 0
            Assert.Empty(port.GetClosedTrades());                                              // 청산 전 = 저널 없음
            e.SetOvernightMacro(0);
            Q(e, Samsung, entry.TargetPrice, entry.TargetPrice + 100);

            var x = Rationales(e)[1];
            Assert.Equal(ArenaTrigger.TakeProfit, x.TriggerType);
            Assert.Equal(entry.PriceWon, x.EntryPrice);                                        // 단일 체결 평단 = 진입 체결가
            Assert.True(x.IsWellFormed);

            var t = Assert.Single(port.GetClosedTrades());
            _out.WriteLine($"{t.AtKst:HH:mm:ss} {t.Name} {t.Code} {t.Qty}주 {t.EntryPrice:N0} → {t.ExitPrice:N0} 실현 {t.RealizedPnl:N0} ({t.ReturnBp}bp) {t.ExitLabel}");
            Assert.Equal(x.Seq, t.Seq);
            Assert.Equal(("005930", "삼성전자"), (t.Code, t.Name));
            Assert.Equal((entry.Qty, entry.PriceWon, entry.TargetPrice), (t.Qty, t.EntryPrice, t.ExitPrice));
            Assert.Equal(ArenaExitKind.TakeProfit, t.ExitKind);
            Assert.True(e.TryReadSnapshot(out var s));
            Assert.Equal(s.Ai.RealizedWon, t.RealizedPnl);                                     // 저널 = 원장 실현(수수료 · 세금 차감 후)
            Assert.True(t.RealizedPnl > 0);
            Assert.Equal(t.RealizedPnl * 10_000 / (t.EntryPrice * t.Qty), t.ReturnBp);
            Assert.True(t.ReturnBp > 0);

            // 2번째 왕복(손절) → 최신순
            _t = _t.AddMinutes(1);
            KrxEnter(e, Hynix);
            var e2 = Rationales(e)[^1];
            Assert.Equal(Hynix, e2.Symbol);
            Q(e, Hynix, e2.StopLossPrice - 100, e2.StopLossPrice);
            var list = port.GetClosedTrades();
            Assert.Equal(2, list.Count);
            Assert.Equal(("SK하이닉스", ArenaExitKind.StopLoss), (list[0].Name, list[0].ExitKind));
            Assert.True(list[0].RealizedPnl < 0 && list[0].ReturnBp < 0);
            Assert.Equal(e2.PriceWon, list[0].EntryPrice);
            Assert.Single(port.GetClosedTrades(1));
        }

        [Fact]
        public void Us_And_Crypto_RoundTrips_Project_Ticker_Market_Names_And_Scaled_Returns()
        {
            var us = new UsTechArenaEngine();
            var up = new UsArenaPortAdapter(us);
            var t = Reg23;
            var ue = UsEnter(us, ref t);
            QU(us, ref t, ue.TargetPrice + 5, ue.TargetPrice + 6);
            var ut = Assert.Single(up.GetClosedTrades());
            Assert.Equal(("NVDA", "엔비디아", ArenaExitKind.TakeProfit), (ut.Code, ut.Name, ut.ExitKind));
            Assert.Equal((ue.Qty, ue.PriceWon), (ut.Qty, ut.EntryPrice));                       // 센트
            Assert.True(us.TryReadSnapshot(out var us1));
            Assert.Equal(us1.Ai.RealizedWon, ut.RealizedPnl);
            Assert.Equal(ut.RealizedPnl * 10_000 / (ut.EntryPrice * ut.Qty), ut.ReturnBp);
            Assert.True(ut.ReturnBp > 0);

            var cx = new UpbitCryptoArenaEngine();
            var cp = new CryptoArenaPortAdapter(cx);
            long ct = Sat;
            var ce = CryptoEnter(cx, ref ct);
            QC(cx, ref ct, ce.StopLossPrice / S - 1_000, ce.StopLossPrice / S);
            var c = Assert.Single(cp.GetClosedTrades());
            _out.WriteLine($"{c.Name} {c.Code} qty={c.Qty} entry={c.EntryPrice} exit={c.ExitPrice} pnl={c.RealizedPnl} {c.ReturnBp}bp {c.ExitLabel}");
            Assert.Equal(("KRW-BTC", "비트코인", ArenaExitKind.StopLoss), (c.Code, c.Name, c.ExitKind));
            Assert.Equal(ce.PriceWon, c.EntryPrice);
            Assert.True(c.RealizedPnl < 0);
            Assert.Equal(ArenaClosedTradeMath.ReturnBp(c.EntryPrice, c.Qty, c.RealizedPnl, S), c.ReturnBp);
            Assert.InRange(c.ReturnBp, -1_000, -1);                                            // 손절 폭 수준(스케일 오류 = 수십억 bp)
        }

        // ── §5 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Page_Krx_Renders_Closed_Journal_Korean_Names_And_Pnl_Chip()
        {
            var e = new KrxArenaEngine();
            var feeder = Pumped(e);
            var (ctx, cut) = Render(new KrxArenaPortAdapter(e, feeder), new UsArenaPortAdapter(new UsTechArenaEngine()),
                new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            using (ctx)
            {
                KrxEnter(e);
                var entry = Rationales(e)[0];
                cut.Render();
                Assert.Contains("삼성전자 005930", cut.Find($".da-book[data-side=\"{ArenaSide.Ai}\"] .da-pos").TextContent);   // AI 보유 표
                Assert.Contains("삼성전자 005930", cut.Find("select.da-sym").TextContent);                     // 티켓
                Assert.Contains("아직 AI 청산 체결이 없습니다", cut.Find(".da-closed .da-noclosed").TextContent);
                Assert.Empty(cut.FindAll(".da-pnl-chip"));                                                      // 매수 카드 = 칩 없음

                e.SetOvernightMacro(0);
                Q(e, Samsung, entry.TargetPrice, entry.TargetPrice + 100);
                cut.Render();

                var t = Assert.Single(new KrxArenaPortAdapter(e).GetClosedTrades());
                var row = Assert.Single(cut.FindAll(".da-closed-tbl tr[data-seq]"));
                _out.WriteLine(row.TextContent);
                var cells = row.QuerySelectorAll("td").Select(c => c.TextContent.Trim()).ToArray();
                Assert.Equal(10, cells.Length);                                      // [v12.18.71-DEV] 복제 열(👤 따라팔기) 추가
                Assert.Equal(t.AtKst.ToString("MM-dd HH:mm:ss"), cells[0]);
                Assert.Contains("삼성전자", cells[1]); Assert.Contains("005930", cells[1]);
                Assert.Equal(t.Qty.ToString("N0") + " 주", cells[2]);
                Assert.Equal(t.EntryPrice.ToString("N0") + "원", cells[3]);
                Assert.Equal(t.ExitPrice.ToString("N0") + "원", cells[4]);
                Assert.Equal("+" + t.RealizedPnl.ToString("N0") + "원", cells[5]);
                Assert.Equal("+" + (t.ReturnBp / 100.0).ToString("F2") + "%", cells[6]);
                Assert.Equal("익절", cells[7]);
                Assert.Equal("목표가 도달(익절)", cells[8]);
                Assert.Contains("ar-up", row.QuerySelectorAll("td")[5].ClassList);                               // 상승 적색
                Assert.Contains("ar-up", row.QuerySelector(".da-ret")!.ClassList);
                Assert.Contains("tp", row.QuerySelector(".da-exit")!.ClassList);
                Assert.Contains("[AI 매도] 005930", row.GetAttribute("title"));
                Assert.Contains("수익 1건 · 손실 0건", cut.Find(".da-closed-sum").TextContent);

                // 근거 카드 헤더 — 한글 종목명 + [실현손익 칩](금액 + 수익률)
                var sell = cut.Find($".da-card.sell[data-seq=\"{t.Seq}\"]");
                Assert.Equal("삼성전자 005930", sell.QuerySelector(".da-card-head .da-name")!.TextContent);
                var chip = sell.QuerySelector(".da-card-head .da-pnl-chip")!;
                Assert.Equal($"[실현손익: +{t.RealizedPnl:N0}원 (+{t.ReturnBp / 100.0:F2}%)]", chip.TextContent.Trim());
                Assert.Contains("ar-up", chip.ClassList);
                Assert.Equal("삼성전자 005930", cut.Find($".da-card.buy[data-seq=\"{entry.Seq}\"] .da-name").TextContent);
            }
        }

        [Fact]
        public void Page_Us_And_Crypto_Tabs_Render_Closed_Journal_With_Names_And_Loss_In_Blue()
        {
            var us = new UsTechArenaEngine();
            var uf = new UsArenaQuoteFeeder(us);
            uf.Pump(Reg23.AddHours(-9).Ticks);
            var cx = new UpbitCryptoArenaEngine();
            var cf = new CryptoArenaQuoteFeeder(cx);
            cf.Pump(new DateTime(2026, 10, 24, 3, 0, 0).Ticks);
            var (ctx, cut) = Render(new KrxArenaPortAdapter(new KrxArenaEngine()), new UsArenaPortAdapter(us, uf), new CryptoArenaPortAdapter(cx, cf));
            using (ctx)
            {
                var t = Reg23;
                var ue = UsEnter(us, ref t);
                QU(us, ref t, ue.StopLossPrice - 1, ue.StopLossPrice);
                cut.Find(".da-mode-tab[data-mode=us]").Click();
                var urow = Assert.Single(cut.FindAll(".da-us .da-closed-tbl tr[data-seq]"));
                _out.WriteLine(urow.TextContent);
                Assert.Contains("엔비디아", urow.TextContent);
                Assert.Contains("NVDA", urow.TextContent);
                Assert.Contains("손절", urow.QuerySelector(".da-exit")!.TextContent);
                Assert.Contains("ar-down", urow.QuerySelector(".da-ret")!.ClassList);                            // 하락 청색
                Assert.Contains("$", urow.QuerySelectorAll("td")[3].TextContent);
                var uchip = cut.Find(".da-us .da-card.sell .da-pnl-chip");
                Assert.StartsWith("[실현손익: -$", uchip.TextContent.Trim());
                Assert.Contains("ar-down", uchip.ClassList);
                Assert.Equal("엔비디아 NVDA", cut.Find(".da-us .da-card.sell .da-name").TextContent);

                long ct = Sat;
                var ce = CryptoEnter(cx, ref ct);
                QC(cx, ref ct, ce.TargetPrice / S, UpbitTickAligner.AlignUp(ce.TargetPrice + 1) / S);
                cut.Find(".da-mode-tab[data-mode=crypto]").Click();
                var crow = Assert.Single(cut.FindAll(".da-crypto .da-closed-tbl tr[data-seq]"));
                _out.WriteLine(crow.TextContent);
                Assert.Contains("비트코인", crow.TextContent);
                Assert.Contains("KRW-BTC", crow.TextContent);
                Assert.Contains("코인", crow.QuerySelectorAll("td")[2].TextContent);
                Assert.Equal("익절", crow.QuerySelector(".da-exit")!.TextContent);
                Assert.Contains("ar-up", crow.QuerySelector(".da-ret")!.ClassList);
                Assert.Matches(@"^\[실현손익: \+[\d,]+원 \(\+\d+\.\d{2}%\)\]$", cut.Find(".da-crypto .da-card.sell .da-pnl-chip").TextContent.Trim());
                Assert.Equal("비트코인 KRW-BTC", cut.Find(".da-crypto .da-card.sell .da-name").TextContent);
            }
        }

        // ── §6 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Unpumped_Page_Shows_No_Journal_And_Portable_Files_Have_No_Engine_References()
        {
            var e = new KrxArenaEngine();
            var (ctx, cut) = Render(new KrxArenaPortAdapter(e), new UsArenaPortAdapter(new UsTechArenaEngine()),
                new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            using (ctx)
            {
                KrxEnter(e);
                cut.Render();
                Assert.Contains("정산 펌프 미결선", cut.Markup);
                Assert.Empty(cut.FindAll(".da-closed"));                                        // 미결선 = 저널 표 없음(가짜 내역 0)
            }

            // 포트 기본 구현 = 빈 목록(비추상 기본 메서드)
            foreach (var it in new[] { typeof(IKrxArenaPort), typeof(IUsArenaPort), typeof(ICryptoArenaPort) })
            {
                var m = it.GetMethod(nameof(IKrxArenaPort.GetClosedTrades))!;
                Assert.False(m.IsAbstract, it.Name);
            }

            string root = RepoRoot();
            foreach (var rel in new[] { "MS2E.UI.Portable/Ports/Trading/ArenaClosedTradePort.cs", "MS2E.UI.Portable/Helpers/StockDisplayNameResolver.cs",
                                        "MS2E.UI.Portable/Pages/Trading/DualArenaPage.razor" })
            {
                string src = File.ReadAllText(Path.Combine(root, rel));
                Assert.DoesNotContain("StockManager.Engines", src);
                Assert.DoesNotContain("StockManager.Adapters", src);
            }
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("StockManager.sln");
        }
    }
}
