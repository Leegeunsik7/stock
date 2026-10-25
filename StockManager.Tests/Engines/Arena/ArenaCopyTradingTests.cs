// ============================================================================
// ArenaCopyTradingTests — v12.18.71-DEV: 시초가 개장 게이트 · 플루토 카피 트레이딩(1-클릭 따라하기 · 자동 미러링 · 동반 청산) · /dualarena 복제 UI
//
//  §1 산식 — ArenaCopyMath 비례 수량 · 불리한 슬리피지(올림 · 20bp 경계) · 동반 청산 비율 · 배율 검증
//  §2 게이트 — MarketOpenAuctionGate 갭 판정 경계 · 일자 롤 · 개장 창 · 전일 종가 없음 = 판정 없음(가짜 갭 0)
//  §3 자동 미러링 — 국내: AI 진입 = 같은 정산 창 휴먼 비례 복제 · AI 익절 = 복제분 동반 청산 · 예수금 부족 = Fail-Closed(축소 복제 0)
//  §4 1-클릭 — 국내: 따라사기(0.5x) · 따라팔기(AI 청산 비율) · 슬리피지 20bp 경계(14bp 체결 · 28bp 취소) · 만료/배율/보유 없음 거부
//  §5 개장 — 국내: 동시호가 장부 → 개장 단일가 체결 · 갭 상승 추격 금지(거버너 가동 시만) · 미개장 장부 = 장 종료 시 거부 · 미국: 프리마켓 장부 → 개장 단일가
//  §6 미국/코인 — 따라사기 · 자동 미러링(포트 DTO 계수)
//  §7 δ=0 — 엔진 복제 접수/체결 루프 10만 회 · 산식 10만 회 힙 할당 0B
//  §8 페이지 — 자동 미러링 토글 · 배율 · [👤 따라사기]/[👤 따라팔기] · 토스트 · 개장 갭 배지 · Portable 신규 파일 엔진 참조 0
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
using StockManager.Engines.Pricing;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class ArenaCopyTradingTests
    {
        private const long Samsung = 5930, Hynix = 660;
        private static readonly DateTime Open10 = new(2026, 10, 22, 10, 0, 0);          // 목요일 10:00 KST
        private static readonly DateTime Reg23 = new(2026, 10, 22, 23, 0, 0);           // 미 정규장(EDT) 23:00 KST
        private static readonly long Sat = (new DateTime(2026, 10, 24, 3, 0, 0).Ticks - DateTime.UnixEpoch.Ticks) / 10;   // UTC µs
        private const long S = UpbitTickAligner.Scale;

        private readonly ITestOutputHelper _out;
        private DateTime _t = Open10;

        public ArenaCopyTradingTests(ITestOutputHelper o) => _out = o;

        // ── 헬퍼 ──────────────────────────────────────────────────────────────

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

        private static DuelPosition[] Human(KrxArenaEngine e)
        {
            var buf = new DuelPosition[KrxArenaEngine.MaxPositions];
            return buf.AsSpan(0, e.CopyPositions(ArenaSide.Human, buf)).ToArray();
        }

        private static ArenaCopyLedger Ledger(KrxArenaEngine e)
        {
            Assert.True(e.TryReadCopy(out var l));
            return l;
        }

        private static KrxArenaQuoteFeeder Pumped(KrxArenaEngine e)
        {
            var f = new KrxArenaQuoteFeeder(e);
            f.Pump(Open10.AddHours(-9).Ticks);
            return f;
        }

        private static void QU(UsTechArenaEngine e, ref DateTime t, long bid, long ask, long bidQty = 1000, long askQty = 1000)
        {
            t = t.AddMilliseconds(100);
            e.OnQuote(UsTechUniverse.Symbol(UsTechUniverse.Nvda), bid, ask, bidQty, askQty, t);
        }

        private static void QC(UpbitCryptoArenaEngine e, ref long t, long bidWon, long askWon, long bidQty = 1 * S, long askQty = 1 * S)
        {
            t += 100_000;
            e.OnQuote(CryptoArenaUniverse.Symbol(CryptoArenaUniverse.Btc), bidWon * S, askWon * S, bidQty, askQty, t);
        }

        private static void CryptoEnter(UpbitCryptoArenaEngine e, ref long t)
        {
            for (int i = 0; i < UpbitCryptoArenaEngine.WarmupTicks + 1; i++) QC(e, ref t, 150_000_000, 150_001_000);
            e.SetMarketContext(0, 300);
            for (int i = 0; i < 4; i++) { t += 1_000; e.OnTrade(CryptoArenaUniverse.Symbol(CryptoArenaUniverse.Btc), 150_001_000 * S, S / 2, 1, t); }
            QC(e, ref t, 150_100_000, 150_101_000, 9 * S, 1 * S);
        }

        // ── §1 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void CopyMath_Scales_Guards_Slippage_And_Companion_Exactly()
        {
            Assert.Equal(284, ArenaCopyMath.ScaleQty(284, 100_000_000, 100_000_000, 10_000));
            Assert.Equal(142, ArenaCopyMath.ScaleQty(284, 100_000_000, 100_000_000, 5_000));
            Assert.Equal(71, ArenaCopyMath.ScaleQty(284, 100_000_000, 50_000_000, 5_000));       // 자본 절반 × 0.5x = 1/4(내림)
            Assert.Equal(0, ArenaCopyMath.ScaleQty(1, 100_000_000, 1_000_000, 10_000));          // 너무 작음 = 0(복제 없음)
            Assert.Equal(0, ArenaCopyMath.ScaleQty(10, 0, 100, 10_000));
            Assert.Equal(0, ArenaCopyMath.ScaleQty(10, 100, 100, 999));                          // 배율 범위 밖
            Assert.Equal(long.MaxValue, ArenaCopyMath.ScaleQty(long.MaxValue, 1, long.MaxValue, 30_000));   // Int128 포화(배율 곱 오버플로 회피)
            Assert.Equal(3 * (long.MaxValue / 10), ArenaCopyMath.ScaleQty(long.MaxValue / 10, long.MaxValue, long.MaxValue, 30_000));

            Assert.True(ArenaCopyMath.TryRatioBp(0.5m, out int half) && half == 5_000);
            Assert.True(ArenaCopyMath.TryRatioBp(1.0m, out int one) && one == 10_000);
            Assert.False(ArenaCopyMath.TryRatioBp(0.05m, out _));
            Assert.False(ArenaCopyMath.TryRatioBp(5m, out _));
            Assert.False(ArenaCopyMath.TryRatioBp(0.12345m, out _));

            Assert.Equal(20, ArenaCopyMath.AdverseSlippageBp(1, 10_000, 10_020));               // 정확히 20bp = 허용
            Assert.False(ArenaCopyMath.SlippageExceeded(1, 10_000, 10_020));
            Assert.Equal(21, ArenaCopyMath.AdverseSlippageBp(1, 10_000, 10_020 + 1));            // 20.01bp 올림 = 21 → 취소
            Assert.True(ArenaCopyMath.SlippageExceeded(1, 10_000, 10_021));
            Assert.Equal(-50, ArenaCopyMath.AdverseSlippageBp(1, 10_000, 9_950));                // 유리 = 음수
            Assert.Equal(30, ArenaCopyMath.AdverseSlippageBp(-1, 10_000, 9_970));                // 매도 = 낮게 체결이 불리
            Assert.True(ArenaCopyMath.SlippageExceeded(-1, 10_000, 9_970));
            Assert.True(ArenaCopyMath.SlippageExceeded(1, 0, 100));                              // 기준가 무효 = fail-closed
            Assert.True(ArenaCopyMath.SlippageExceeded(2, 100, 100));

            Assert.Equal(100, ArenaCopyMath.CompanionQty(100, 150, 284, 284));                   // AI 전량 청산 = 복제분 전량(자기 매수분 보존)
            Assert.Equal(50, ArenaCopyMath.CompanionQty(100, 150, 142, 284));                    // AI 절반 청산 = 복제분 절반
            Assert.Equal(40, ArenaCopyMath.CompanionQty(100, 40, 284, 284));                     // 휴먼 보유 상한
            Assert.Equal(0, ArenaCopyMath.CompanionQty(0, 100, 10, 10));
        }

        // ── §2 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void OpenGate_Assess_Boundaries_RollDay_And_Window()
        {
            Assert.Equal(OpenGapAction.ChaseVeto, MarketOpenAuctionGate.Assess(300));
            Assert.Equal(OpenGapAction.Flat, MarketOpenAuctionGate.Assess(299));
            Assert.Equal(OpenGapAction.Flat, MarketOpenAuctionGate.Assess(-299));
            Assert.Equal(OpenGapAction.GapFillLong, MarketOpenAuctionGate.Assess(-300));
            Assert.Equal(OpenGapAction.GapFillLong, MarketOpenAuctionGate.Assess(-499));
            Assert.Equal(OpenGapAction.PanicVeto, MarketOpenAuctionGate.Assess(-500));

            var g = default(OpenGapState);
            Assert.True(MarketOpenAuctionGate.OnRegularTick(ref g, 20_000));                    // 첫 거래일 개장 — 전일 종가 없음
            Assert.Equal(OpenGapAction.None, g.Action);
            Assert.False(MarketOpenAuctionGate.VetoesEntry(in g));
            Assert.False(MarketOpenAuctionGate.OnRegularTick(ref g, 20_100));
            MarketOpenAuctionGate.RollDay(ref g);
            Assert.Equal(20_100, g.PrevCloseX2);
            Assert.False(g.Opened);
            Assert.True(MarketOpenAuctionGate.OnRegularTick(ref g, 21_105));                    // +5% 갭 상승
            Assert.Equal(500, g.GapBp);
            Assert.True(MarketOpenAuctionGate.VetoesEntry(in g));
            for (int i = 0; i < MarketOpenAuctionGate.OpeningWindowTicks; i++) MarketOpenAuctionGate.OnRegularTick(ref g, 21_105);
            Assert.False(MarketOpenAuctionGate.InOpeningWindow(in g));                          // 창 경과 = 거부권 해제
            Assert.False(MarketOpenAuctionGate.VetoesEntry(in g));

            MarketOpenAuctionGate.RollDay(ref g);
            MarketOpenAuctionGate.OnRegularTick(ref g, 20_260);                                 // −4% 갭 하락
            Assert.Equal(OpenGapAction.GapFillLong, g.Action);
            Assert.True(MarketOpenAuctionGate.GapFillSignal(in g, 2000));
            Assert.False(MarketOpenAuctionGate.GapFillSignal(in g, 1999));                      // 매수 수급 우위 없음 = 후보 아님

            Assert.True(MarketOpenAuctionGate.IsKrxPreOpen(new DateTime(2026, 10, 23, 8, 30, 0)));
            Assert.False(MarketOpenAuctionGate.IsKrxPreOpen(new DateTime(2026, 10, 23, 9, 0, 0)));
            Assert.False(MarketOpenAuctionGate.IsKrxPreOpen(new DateTime(2026, 10, 24, 8, 45, 0)));   // 토요일
        }

        // ── §3 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Krx_AutoMirror_Copies_Entry_In_Same_Window_And_Companion_Closes_On_Ai_Exit()
        {
            var e = new KrxArenaEngine();
            Assert.False(e.SetAutoCopy(true, 999));                                            // 범위 밖 = 거부 · 무변경
            Assert.False(e.AutoCopyEnabled);
            Assert.True(e.SetAutoCopy(true, 10_000));
            KrxEnter(e);
            var entry = Assert.Single(Rationales(e));
            var h = Assert.Single(Human(e));
            Assert.Equal(Samsung, h.Symbol);
            Assert.Equal(entry.Qty, h.Qty);                                                    // 동일 자본 · 1.0x = 동일 수량
            Assert.Equal(entry.PriceWon, h.AvgPriceWon);                                       // 같은 호가 · 같은 가격
            var l = Ledger(e);
            Assert.Equal(1, l.Mirrored);
            Assert.Equal(ArenaCopyKind.Mirror, l.LastKind);
            Assert.Equal(ArenaCopyCode.None, l.LastCode);

            e.SetOvernightMacro(0);
            Q(e, Samsung, entry.TargetPrice, entry.TargetPrice + 100);                         // AI 익절
            Assert.Equal(ArenaTrigger.TakeProfit, Rationales(e)[^1].TriggerType);
            Assert.Empty(Human(e));                                                            // 동반 청산
            l = Ledger(e);
            Assert.Equal(1, l.CompanionCloses);
            Assert.Equal(ArenaCopyKind.Companion, l.LastKind);
            Assert.True(e.TryReadSnapshot(out var s));
            Assert.True(s.Human.RealizedWon > 0);
            Assert.Equal(1, s.Human.Wins);
        }

        [Fact]
        public void Krx_AutoMirror_Insufficient_Cash_Is_Fail_Closed_Without_Shrinking_And_Own_Buys_Survive_Companion()
        {
            var e = new KrxArenaEngine();
            for (int i = 0; i < 2; i++) Q(e, Hynix, 200_000, 200_100);
            Assert.Equal(KrxArenaReject.None, e.SubmitHuman(Hynix, 1, 450));                  // 휴먼 자기 매수 9천만 원
            Q(e, Hynix, 200_000, 200_100);
            Assert.True(e.SetAutoCopy(true, 10_000));
            KrxEnter(e);
            Assert.Single(Rationales(e));
            var h = Assert.Single(Human(e));
            Assert.Equal(Hynix, h.Symbol);                                                     // 삼성 복제 0주(축소 복제 없음)
            var l = Ledger(e);
            Assert.Equal(0, l.Mirrored);
            Assert.Equal(1, l.CashRejects);
            Assert.Equal(ArenaCopyCode.InsufficientCash, l.LastCode);

            // 자기 매수 + 복제가 섞인 종목: AI 청산 = 복제분만 동반 청산
            var e2 = new KrxArenaEngine();
            for (int i = 0; i < KrxArenaEngine.WarmupTicks + 1; i++) Q(e2, Samsung, 70_000, 70_100);
            Assert.Equal(KrxArenaReject.None, e2.SubmitHuman(Samsung, 1, 50));
            Q(e2, Samsung, 70_000, 70_100);
            Assert.True(e2.SetAutoCopy(true, 10_000));
            e2.SetOvernightMacro(3000);
            Q(e2, Samsung, 70_100, 70_200, bidQty: 9000, askQty: 1000, vol: 1000);
            var entry = Assert.Single(Rationales(e2));
            Assert.InRange(Assert.Single(Human(e2)).Qty, 50 + entry.Qty - 1, 50 + entry.Qty);   // 자기 매수 수수료만큼 평가자산 미세 감소 → 비례 내림
            e2.SetOvernightMacro(0);
            Q(e2, Samsung, entry.TargetPrice, entry.TargetPrice + 100);
            Assert.Equal(50, Assert.Single(Human(e2)).Qty);                                    // 자기 매수 50주 보존
        }

        // ── §4 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Krx_Follow_Buy_Half_Ratio_Then_Follow_Sell_On_Exit_Rationale()
        {
            var e = new KrxArenaEngine();
            var port = new KrxArenaPortAdapter(e);
            KrxEnter(e);
            var entry = Assert.Single(Rationales(e));
            Assert.Empty(Human(e));                                                            // 자동 미러링 꺼짐(기본) = 복제 0

            var r = port.FollowAiTrade(entry.Seq, 0.5m);
            Assert.True(r.Accepted, r.Label);
            Assert.Equal(1, r.Side);
            Assert.Equal(entry.Qty / 2, r.Qty);
            Q(e, Samsung, 70_100, 70_200);                                                     // 다음 정산 = 기준가 그대로 체결
            Assert.Equal(entry.Qty / 2, Assert.Single(Human(e)).Qty);
            Assert.Equal(1, Ledger(e).Followed);

            e.SetOvernightMacro(0);
            Q(e, Samsung, entry.TargetPrice, entry.TargetPrice + 100);
            var exit = Rationales(e)[^1];
            Assert.Equal(-1, exit.Side);
            Assert.Single(Human(e));                                                           // 따라하기 매수분은 자동 동반 청산 대상 아님(자동 미러링 꺼짐)
            var s = port.FollowAiTrade(exit.Seq);                                              // 1.0x = AI 청산 수량 비례(보유 상한으로 전량)
            Assert.True(s.Accepted, s.Label);
            Assert.Equal(-1, s.Side);
            Q(e, Samsung, entry.TargetPrice, entry.TargetPrice + 100);
            Assert.Empty(Human(e));
            Assert.Equal(2, Ledger(e).Followed);
            Assert.Equal(2, port.CopyTrading.Followed);
            Assert.Equal("005930", port.CopyTrading.LastSymbol);
        }

        [Fact]
        public void Krx_Follow_Slippage_Within_20bp_Fills_Beyond_Cancels()
        {
            var e = new KrxArenaEngine();
            var port = new KrxArenaPortAdapter(e);
            KrxEnter(e);
            long seq = Rationales(e)[0].Seq;                                                   // 기준가 70,200

            Assert.True(port.FollowAiTrade(seq, 0.5m).Accepted);
            Q(e, Samsung, 70_200, 70_300);                                                     // +14.2bp = 체결
            Assert.Single(Human(e));
            long held = Human(e)[0].Qty;

            Assert.True(port.FollowAiTrade(seq, 0.5m).Accepted);                               // 접수는 통과 — 체결 시점 판정
            Q(e, Samsung, 70_300, 70_400);                                                     // +28.5bp = 취소
            Assert.Equal(held, Human(e)[0].Qty);
            var l = Ledger(e);
            Assert.Equal(1, l.SlippageCancels);
            Assert.Equal(ArenaCopyCode.SlippageExceeded, l.LastCode);
            Assert.Equal(ArenaCopyCode.SlippageExceeded, port.CopyTrading.LastCode);
            Assert.True(e.TryReadSnapshot(out var s) && s.Human.Rejects == 1);
        }

        [Fact]
        public void Krx_Follow_Rejects_Expired_Bad_Ratio_And_Sell_Without_Holding()
        {
            var e = new KrxArenaEngine();
            var port = new KrxArenaPortAdapter(e);
            Assert.Equal(ArenaCopyCode.NotFound, port.FollowAiTrade(1).Code);                  // 근거 없음
            KrxEnter(e);
            Assert.Equal(ArenaCopyCode.Invalid, port.FollowAiTrade(1, 5m).Code);
            Assert.Equal(ArenaCopyCode.Invalid, port.FollowAiTrade(1, 0m).Code);
            Assert.Equal(ArenaCopyCode.NotFound, port.FollowAiTrade(999).Code);
            Assert.Equal(KrxArenaReject.Invalid, e.SubmitFollow(Samsung, 1, 1, 0));             // 기준가 없음 = 접수 거부

            e.SetOvernightMacro(0);
            var entry = Rationales(e)[0];
            Q(e, Samsung, entry.TargetPrice, entry.TargetPrice + 100);
            var sell = port.FollowAiTrade(Rationales(e)[^1].Seq);
            Assert.True(sell.Accepted);                                                        // 접수 단계 통과
            Q(e, Samsung, entry.TargetPrice, entry.TargetPrice + 100);
            var l = Ledger(e);
            Assert.Equal(ArenaCopyCode.InsufficientPosition, l.LastCode);                      // 보유 0 = 따라팔기 거부(공매도 0)
            Assert.Equal(1, l.OtherRejects);
            Assert.Empty(Human(e));
        }

        // ── §5 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Krx_PreOpen_Orders_Park_And_Uncross_At_Single_Opening_Price_With_Gap_Badge()
        {
            var e = new KrxArenaEngine();
            var port = new KrxArenaPortAdapter(e);
            for (int i = 0; i < KrxArenaEngine.WarmupTicks + 1; i++) Q(e, Samsung, 70_000, 70_100);   // 1일차 마지막 중간가 70,050

            var pre = new DateTime(2026, 10, 23, 8, 45, 0);                                    // 금요일 동시호가 접수
            Assert.Equal(KrxArenaReject.None, e.SubmitHuman(Samsung, 1, 10));
            e.Drain(pre);
            var a = port.OpeningAuction;
            Assert.True(a.Supported && a.PreOpen);
            Assert.Equal(1, a.Parked);
            Assert.Empty(Human(e));

            _t = new DateTime(2026, 10, 23, 9, 0, 0);
            Q(e, Samsung, 73_500, 73_600);                                                     // 개장 +4.99% 갭
            var h = Assert.Single(Human(e));
            Assert.Equal(10, h.Qty);
            Assert.Equal(73_500, h.AvgPriceWon);                                               // 단일 시초가 = 중간가 호가단위 내림
            a = port.OpeningAuction;
            Assert.False(a.PreOpen);
            Assert.Equal(0, a.Parked);
            Assert.Equal(1, a.AuctionFills);
            var g = Assert.Single(port.OpeningGaps());
            Assert.Equal("005930", g.Code);
            Assert.Equal(70_050, g.PrevClose);
            Assert.Equal(499, g.GapBp);
            Assert.Equal(ArenaOpenGapKind.ChaseVeto, g.Kind);
            Assert.True(g.InWindow);
        }

        [Fact]
        public void Krx_Gap_Chase_Veto_Applies_Only_With_Governor_And_Unopened_Book_Is_Purged()
        {
            long Day2(bool governed, out int aiEntries)
            {
                _t = Open10;
                var e = new KrxArenaEngine();
                e.EnableGovernance(governed);
                for (int i = 0; i < KrxArenaEngine.WarmupTicks + 1; i++) Q(e, Samsung, 70_000, 70_100);
                e.SetOvernightMacro(3000);
                _t = new DateTime(2026, 10, 23, 9, 0, 0);
                Q(e, Samsung, 73_500, 73_600, bidQty: 9000, askQty: 1000, vol: 1000);         // 갭 상승 + 강한 매수 시그널
                aiEntries = Rationales(e).Count(r => r.Side > 0);
                Assert.True(e.TryReadAuction(out var st));
                return st.GapVetoes;
            }
            Assert.Equal(1, Day2(true, out int governedEntries));
            Assert.Equal(0, governedEntries);                                                  // 추격 금지(악마의 변호인)
            Assert.Equal(0, Day2(false, out int plainEntries));
            Assert.Equal(1, plainEntries);                                                     // 거버너 정지 = 기존 동작 동일

            _t = Open10;
            var e2 = new KrxArenaEngine();
            Q(e2, Hynix, 200_000, 200_100);
            Assert.Equal(KrxArenaReject.None, e2.SubmitHuman(Hynix, 1, 1));
            e2.Drain(new DateTime(2026, 10, 23, 8, 50, 0));
            Assert.True(e2.TryReadAuction(out var parked) && parked.Parked == 1);
            e2.Drain(new DateTime(2026, 10, 23, 16, 0, 0));                                    // 개장 틱 없이 장 종료 = 장부 전량 거부(이월 0)
            Assert.True(e2.TryReadAuction(out var purged));
            Assert.Equal(0, purged.Parked);
            Assert.Equal(1, purged.Rejects);
            Assert.True(e2.TryReadSnapshot(out var s) && s.Human.Rejects == 1);
        }

        [Fact]
        public void Us_PreMarket_Orders_Uncross_At_Open_And_Gap_Is_Measured()
        {
            var e = new UsTechArenaEngine();
            var port = new UsArenaPortAdapter(e);
            var t = Reg23;
            for (int i = 0; i < 3; i++) QU(e, ref t, 13_000, 13_002);                         // 1일차 정규장 마지막 중간가 $130.01
            t = new DateTime(2026, 10, 23, 22, 15, 0);                                         // 09:15 ET 동시호가 접수
            QU(e, ref t, 13_050, 13_052);
            Assert.Equal(UsArenaReject.None, port.SubmitHuman("NVDA", 1, 5));
            e.Drain(t.AddSeconds(1));
            Assert.True(port.OpeningAuction.PreOpen);
            Assert.Equal(1, port.OpeningAuction.Parked);

            t = new DateTime(2026, 10, 23, 22, 30, 0);
            QU(e, ref t, 13_100, 13_104);
            var h = Assert.Single(port.Positions(ArenaSide.Human));
            Assert.Equal(5, h.Qty);
            Assert.Equal(13_102, h.AvgPriceCents);                                             // 단일 시초가 = 중간가 $0.01 내림
            Assert.Equal(1, port.OpeningAuction.AuctionFills);
            var g = Assert.Single(port.OpeningGaps());
            Assert.Equal("NVDA", g.Code);
            Assert.Equal(13_001, g.PrevClose);
            Assert.Equal(ArenaOpenGapKind.Flat, g.Kind);
        }

        // ── §6 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Us_And_Crypto_Follow_And_AutoMirror_Through_Ports()
        {
            var us = new UsTechArenaEngine();
            var up = new UsArenaPortAdapter(us);
            Assert.True(up.SetAutoCopyEnabled(true, 0.5m));
            Assert.False(up.SetAutoCopyEnabled(true, 0.01m));
            var t = Reg23;
            for (int i = 0; i < UsTechArenaEngine.WarmupTicks + 1; i++) QU(us, ref t, 13_000, 13_001);
            us.SetMarketContext(300, 300, -10);
            QU(us, ref t, 13_050, 13_051, bidQty: 9000, askQty: 1000);
            var ai = Assert.Single(up.RecentRationales(5));
            var hum = Assert.Single(up.Positions(ArenaSide.Human));
            Assert.Equal(ai.Qty / 2, hum.Qty);                                                 // 0.5x 미러링
            var v = up.CopyTrading;
            Assert.True(v.Supported && v.AutoEnabled);
            Assert.Equal(5_000, v.RatioBp);
            Assert.Equal("0.5x", v.RatioLabel);
            Assert.Equal(1, v.Mirrored);
            Assert.Equal("NVDA", v.LastSymbol);
            var uf = up.FollowAiTrade(ai.Seq);
            Assert.True(uf.Accepted, uf.Label);
            Assert.InRange(uf.Qty, ai.Qty - 1, ai.Qty);                                        // 1.0x · 진영 평가자산 비례(수수료 차 내림)
            QU(us, ref t, 13_050, 13_051);
            Assert.Equal(ai.Qty / 2 + uf.Qty, Assert.Single(up.Positions(ArenaSide.Human)).Qty);

            var c = new UpbitCryptoArenaEngine();
            var cp = new CryptoArenaPortAdapter(c);
            long ct = Sat;
            Assert.True(cp.SetAutoCopyEnabled(true));
            CryptoEnter(c, ref ct);
            var cr = Assert.Single(cp.RecentRationales(5));
            var ch = Assert.Single(cp.Positions(ArenaSide.Human));
            Assert.Equal(cr.QtyRaw, ch.QtyRaw);
            Assert.Equal(1, cp.CopyTrading.Mirrored);
            Assert.Equal("KRW-BTC", cp.CopyTrading.LastSymbol);
            Assert.True(cp.SetAutoCopyEnabled(false));
            var f = cp.FollowAiTrade(cr.Seq, 0.5m);
            Assert.True(f.Accepted, f.Label);
            QC(c, ref ct, 150_100_000, 150_101_000);
            Assert.Equal(cr.QtyRaw + f.Qty, Assert.Single(cp.Positions(ArenaSide.Human)).QtyRaw);
            Assert.Equal(1, cp.CopyTrading.Followed);
        }

        // ── §7 ─────────────────────────────────────────────────────────────────

        [Fact]
        public void ZeroAlloc_Copy_Follow_Fill_Loop_100k_And_Math_100k()
        {
            var e = new KrxArenaEngine(10_000_000_000);                                       // 40만 회차 왕복 비용(~1억 원)에도 예수금 충분
            for (int i = 0; i < KrxArenaEngine.WarmupTicks + 1; i++) Q(e, Samsung, 70_000, 70_100, bidQty: 1_000_000, askQty: 1_000_000);
            Assert.True(e.SetAutoCopy(true, 10_000));
            var at = _t;
            const int N = 100_000;
            void Loop()
            {
                for (int i = 0; i < N; i++)
                {
                    e.SubmitFollow(Samsung, 1, 1, 70_100);
                    e.SubmitFollow(Samsung, -1, 1, 70_000);
                    e.Drain(at);
                }
            }
            Loop();                                                                            // JIT 예열
            long engine = ZeroAllocProbe.MinAllocated(Loop);
            long sink = 0;
            long math = ZeroAllocProbe.MinAllocated(() =>
            {
                for (int i = 0; i < N; i++)
                {
                    sink += ArenaCopyMath.ScaleQty(284 + i, 100_000_000, 99_000_000, 10_000);
                    sink += ArenaCopyMath.AdverseSlippageBp(1, 70_200, 70_200 + (i & 255));
                    sink += ArenaCopyMath.CompanionQty(100, 200, i & 127, 128);
                }
            });
            Assert.True(e.TryReadCopy(out var l));
            _out.WriteLine($"engine={engine}B math={math}B followed={l.Followed} sink={sink}");
            Assert.Equal(0, engine);
            Assert.Equal(0, math);
            Assert.True(l.Followed >= 2L * N * 2);                                             // 매 회차 매수/매도 복제 체결
            Assert.Equal(0, l.SlippageCancels);
            Assert.Equal(0, l.CashRejects);
        }

        // ── §8 ─────────────────────────────────────────────────────────────────

        private static (Bunit.TestContext Ctx, IRenderedComponent<DualArenaPage> Cut) Render(IKrxArenaPort krx, IUsArenaPort us, ICryptoArenaPort crypto)
        {
            var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton(krx);
            ctx.Services.AddSingleton(us);
            ctx.Services.AddSingleton(crypto);
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);
            ctx.Services.AddSingleton<IPlutoLearningPort>(UnwiredPlutoLearningPort.Instance);   // [v12.18.77-DEV] 학습 엔진 미결선
            ctx.Services.AddSingleton<IPlutoRecommendationPort>(UnwiredPlutoRecommendationPort.Instance);   // [v12.18.78-DEV] 추천 엔진 미결선
            return (ctx, ctx.RenderComponent<DualArenaPage>());
        }

        [Fact]
        public void Page_Copy_Toggle_Ratio_Follow_Buttons_And_Toast()
        {
            var e = new KrxArenaEngine();
            var port = new KrxArenaPortAdapter(e, Pumped(e));
            var (ctx, cut) = Render(port, new UsArenaPortAdapter(new UsTechArenaEngine()), new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            using (ctx)
            {
                KrxEnter(e);
                cut.Render();
                var toggle = cut.Find(".da-krx .da-copy-toggle");
                Assert.Contains("OFF", toggle.TextContent);
                cut.Find(".da-krx .da-copy-ratio[data-ratio=\"0.5x\"]").Click();
                Assert.True(port.CopyTrading.RatioBp == 5_000 && !port.CopyTrading.AutoEnabled);
                cut.Find(".da-krx .da-copy-toggle").Click();
                Assert.True(port.CopyTrading.AutoEnabled);
                Assert.Contains("ON", cut.Find(".da-krx .da-copy-toggle").TextContent);
                Assert.Contains("자동 미러링 ON (0.5x)", cut.Find(".da-toast.ok").TextContent);

                var buy = cut.Find(".da-card.buy .da-follow.buy");
                Assert.Contains("따라사기", buy.TextContent);
                buy.Click();
                var toast = cut.Find(".da-toast");
                _out.WriteLine(toast.TextContent);
                Assert.Contains("ok", toast.ClassName);
                Assert.Contains("따라사기 접수 #1", toast.TextContent);
                Assert.Contains("(배율 0.5x)", toast.TextContent);

                e.SetOvernightMacro(0);
                var entry = Rationales(e)[0];
                Q(e, Samsung, entry.TargetPrice, entry.TargetPrice + 100);
                cut.Render();
                var sellBtn = cut.Find(".da-closed-tbl tr[data-seq] .da-follow.sell");
                Assert.Contains("따라팔기", sellBtn.TextContent);
                Assert.NotEmpty(cut.FindAll(".da-card.sell .da-follow.sell"));
                Assert.NotNull(cut.Find(".da-copy-last"));                                    // 자동 미러링 최근 결과 배지

                cut.Find(".da-krx .da-copy-toggle").Click();
                Assert.False(port.CopyTrading.AutoEnabled);
                Assert.Contains("OFF", cut.Find(".da-toast.idle").TextContent);
            }
        }

        /// <summary>카피 트레이딩 미구현 포트(기본 인터페이스 구현 = 미지원) — 판독 멤버만 위임.</summary>
        private sealed class NoCopyKrxPort : IKrxArenaPort
        {
            private readonly IKrxArenaPort _in;
            public NoCopyKrxPort(IKrxArenaPort inner) => _in = inner;
            public string ModeName => _in.ModeName;
            public bool Fed => _in.Fed;
            public bool TryRead(out KrxArenaView view) => _in.TryRead(out view);
            public System.Collections.Generic.IReadOnlyList<KrxRationaleView> RecentRationales(int max) => _in.RecentRationales(max);
            public int SubmitHuman(long symbol, int side, long qty) => _in.SubmitHuman(symbol, side, qty);
            public long Version => _in.Version;
            public KrxArenaFeedStatus Feed => _in.Feed;
            public bool SelectSource(int source) => _in.SelectSource(source);
            public System.Collections.Generic.IReadOnlyList<KrxArenaQuoteView> Quotes() => _in.Quotes();
            public System.Collections.Generic.IReadOnlyList<KrxArenaPositionView> Positions(int side) => _in.Positions(side);
        }

        [Fact]
        public void Page_Unsupported_Port_Shows_Honest_Reject_Toast_And_Portable_Files_Reference_No_Engine()
        {
            var e = new KrxArenaEngine();
            var port = new NoCopyKrxPort(new KrxArenaPortAdapter(e, Pumped(e)));
            var (ctx, cut) = Render(port, new UsArenaPortAdapter(new UsTechArenaEngine()), new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            using (ctx)
            {
                KrxEnter(e);
                cut.Render();
                Assert.True(cut.Find(".da-krx .da-copy-toggle").HasAttribute("disabled"));     // 미결선 = 토글 비활성(가짜 ON 0)
                Assert.Contains("카피 트레이딩 미결선", cut.Find(".da-krx .da-copy-bar").TextContent);
                cut.Find(".da-card.buy .da-follow.buy").Click();
                var toast = cut.Find(".da-toast.bad");
                Assert.Contains("거부 #1", toast.TextContent);
                Assert.Contains(ArenaCopyCode.Label(ArenaCopyCode.Unsupported), toast.TextContent);
                Assert.Empty(Human(e));
            }

            string root = FindRoot();
            foreach (var rel in new[] { "MS2E.UI.Portable/Ports/Trading/ArenaCopyTradingPort.cs", "MS2E.UI.Portable/Pages/Trading/DualArenaPage.razor" })
            {
                string src = File.ReadAllText(Path.Combine(root, rel));
                Assert.DoesNotContain("StockManager.Engines", src);
                Assert.DoesNotContain("StockManager.Adapters", src);
            }
        }

        private static string FindRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("StockManager.sln");
        }
    }
}
