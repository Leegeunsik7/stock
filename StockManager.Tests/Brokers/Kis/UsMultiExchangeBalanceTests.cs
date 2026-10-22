// ============================================================================
// UsMultiExchangeBalanceTests — [v12.18.57-DEV] KIS 해외주식 미국 3대 거래소(NASD · NYSE · AMEX) 잔고/현재가 무할당 파서 · 세션 유효성 게이트(오프라인 · 송출 0)
//
//   §1 코드 매핑: 거래 계열(NASD/NYSE/AMEX) ↔ 시세 계열(NAS/NYS/AMS) 왕복 · 미지/대소문자/공백/계열 혼용 = None · 심볼 형식
//   §2 잔고(VTTS3012R): 거래소별 원시 응답 정확값(raw 10^8) · 행 거래소 불일치 집계 · output2 배열 변형 · 오류 응답 · 금액 형식 이탈 = Missing ·
//      손상 본문 표(잘림 · 뒤따르는 값 · 중복 필드 · 최상위 비객체 · 깊이 폭탄 · 비 UTF-8) = 거부(예외 0) · 무작위 변조 10만 회 예외 0
//   §3 현재가(HHDFS00000300): rsym = "D" + 시세 코드 + 심볼 정확 일치만 검증 · 빈 시세(장 외 미존재 종목) = 사용 불가 · zdiv/tvol
//   §4 게이트: 페이즈 6종 × 사용 가능/불가 → 유효성 · 주문 근거 = 정규장 Tradable 만
//   §5 δ=0: 3대 거래소 잔고 10만 회 · 현재가 10만 회 · 게이트 판정 10만 회 = 힙 0(ZeroAllocProbe 창 최솟값)
// ============================================================================
using System;
using System.Text;
using StockManager.Engines.Brokers.Kis;
using StockManager.Engines.Feeds;
using StockManager.Engines.Pricing;
using Xunit;

namespace StockManager.Tests.Brokers.Kis
{
    [Collection("ConcurrencyStress")]
    public sealed class UsMultiExchangeBalanceTests
    {
        private const long S = UsTickAligner.Scale;
        private const long M = KisOverseasProtocol.Missing;

        private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

        // KIS 해외주식 잔고(VTTS3012R) 응답 형태 — output1 행 · output2 객체 · 문자열 금액
        private static string Balance(string excg, string pchs = "1234.56000", string evlu = "-7.89000000", string tot = "-12.34500")
            => "{\"ctx_area_fk200\":\"50098765^01^" + excg + "^USD\",\"ctx_area_nk200\":\"\","
             + "\"output1\":[{\"cano\":\"50098765\",\"ovrs_pdno\":\"AAPL\",\"ovrs_item_name\":\"애플\",\"ovrs_cblc_qty\":\"3\",\"ovrs_excg_cd\":\"" + excg + "\",\"now_pric2\":\"229.000000\"},"
             + "{\"ovrs_pdno\":\"MSFT\",\"ovrs_excg_cd\":\"" + excg + "\",\"ovrs_cblc_qty\":\"1\"}],"
             + "\"output2\":{\"frcr_pchs_amt1\":\"" + pchs + "\",\"ovrs_rlzt_pfls_amt\":\"0.00000\",\"ovrs_tot_pfls\":\"" + tot + "\",\"tot_evlu_pfls_amt\":\"" + evlu + "\",\"tot_pftrt\":\"-0.64\"},"
             + "\"rt_cd\":\"0\",\"msg_cd\":\"20310000\",\"msg1\":\"모의투자 조회가 완료되었습니다.                       \"}";

        // KIS 해외주식 현재체결가(HHDFS00000300) 응답 형태
        private static string Quote(string rsym, string last = "229.0000", string prev = "227.5200", string tvol = "38123456", string zdiv = "4")
            => "{\"output\":{\"rsym\":\"" + rsym + "\",\"zdiv\":\"" + zdiv + "\",\"base\":\"" + prev + "\",\"pvol\":\"41000000\",\"last\":\"" + last + "\",\"sign\":\"2\","
             + "\"diff\":\"1.4800\",\"rate\":\"+0.65\",\"tvol\":\"" + tvol + "\",\"tamt\":\"8730000000\",\"ordy\":\"매수불가\"},"
             + "\"rt_cd\":\"0\",\"msg_cd\":\"MCA00000\",\"msg1\":\"정상처리 되었습니다.\"}";

        // ════════════ §1 코드 매핑 ════════════

        [Theory]
        [InlineData(UsExchange.Nasdaq, "NASD", "NAS")]
        [InlineData(UsExchange.Nyse, "NYSE", "NYS")]
        [InlineData(UsExchange.Amex, "AMEX", "AMS")]
        public void Exchange_Codes_Match_Kis_Spec_And_Round_Trip(UsExchange ex, string trade, string quote)
        {
            Assert.True(UsExchangeCode.IsValid(ex));
            Assert.Equal(trade, UsExchangeCode.TradeCode(ex));
            Assert.Equal(quote, UsExchangeCode.QuoteCode(ex));
            Assert.Equal(U(trade), UsExchangeCode.TradeCodeUtf8(ex).ToArray());
            Assert.Equal(U(quote), UsExchangeCode.QuoteCodeUtf8(ex).ToArray());
            Assert.Equal(ex, UsExchangeCode.FromTradeCode(U(trade)));
            Assert.Equal(ex, UsExchangeCode.FromQuoteCode(U(quote)));
            Assert.Equal(UsExchange.None, UsExchangeCode.FromTradeCode(U(quote)));                    // 계열 혼용 = 미지
            Assert.Equal(UsExchange.None, UsExchangeCode.FromQuoteCode(U(trade)));
            Assert.Equal(UsExchange.None, UsExchangeCode.FromTradeCode(U(trade.ToLowerInvariant())));
            Assert.Equal(UsExchange.None, UsExchangeCode.FromTradeCode(U(trade + " ")));
        }

        [Fact]
        public void All_Is_Three_Exchanges_In_Fixed_Order_And_Invalid_Values_Map_To_Empty()
        {
            Assert.Equal(new[] { UsExchange.Nasdaq, UsExchange.Nyse, UsExchange.Amex }, UsExchangeCode.All.ToArray());
            Assert.Equal(UsExchangeCode.Count, UsExchangeCode.All.Length);
            foreach (var bad in new[] { UsExchange.None, (UsExchange)4, (UsExchange)255 })
            {
                Assert.False(UsExchangeCode.IsValid(bad));
                Assert.Equal("", UsExchangeCode.TradeCode(bad));
                Assert.Equal("", UsExchangeCode.QuoteCode(bad));
                Assert.True(UsExchangeCode.TradeCodeUtf8(bad).IsEmpty);
                Assert.True(UsExchangeCode.QuoteCodeUtf8(bad).IsEmpty);
            }
            foreach (var code in new[] { "", "SEHK", "TKSE", "HASE", "SHAA", "NAS ", "NYSEA", "BAQ", "HKS" })
                Assert.Equal(UsExchange.None, UsExchangeCode.FromTradeCode(U(code)));
        }

        [Theory]
        [InlineData("AAPL", true)]
        [InlineData("BRK.B", true)]
        [InlineData("BF/B", true)]
        [InlineData("A", true)]
        [InlineData("ABCDEFGHIJKL", true)]
        [InlineData("ABCDEFGHIJKLM", false)]
        [InlineData("", false)]
        [InlineData("aapl", false)]
        [InlineData("AAPL ", false)]
        [InlineData("AAPL&EXCD=HKS", false)]
        [InlineData("AA%26", false)]
        [InlineData("애플", false)]
        public void Symbol_Format_Is_Strict(string symbol, bool ok) => Assert.Equal(ok, UsExchangeCode.IsValidSymbol(symbol));

        // ════════════ §2 잔고 ════════════

        [Theory]
        [InlineData(UsExchange.Nasdaq, "NASD")]
        [InlineData(UsExchange.Nyse, "NYSE")]
        [InlineData(UsExchange.Amex, "AMEX")]
        public void Balance_Parses_Each_Exchange_Exactly(UsExchange ex, string code)
        {
            byte[] body = U(Balance(code));
            Assert.True(KisOverseasProtocol.TryParseBalance(body, ex, out var b));
            Assert.True(b.Parsed); Assert.True(b.RtOk); Assert.True(b.Ok);
            Assert.Equal("0", b.RtCd.Decode(body));
            Assert.Equal("20310000", b.MsgCd.Decode(body));
            Assert.Equal("모의투자 조회가 완료되었습니다.", b.Msg1.Decode(body).Trim());
            Assert.Equal(2, b.HoldingRows);
            Assert.Equal(0, b.MismatchedExchangeRows);
            Assert.Equal(1234_56 * S / 100, b.PurchaseUsdRaw);
            Assert.Equal(-789 * S / 100, b.EvalPnlUsdRaw);
            Assert.Equal(-1_234_500_000L, b.TotalPnlUsdRaw);
        }

        [Fact]
        public void Balance_Counts_Rows_From_Other_Exchanges()
        {
            byte[] body = U(Balance("NYSE"));
            Assert.True(KisOverseasProtocol.TryParseBalance(body, UsExchange.Nasdaq, out var b));
            Assert.Equal(2, b.MismatchedExchangeRows);
            Assert.True(KisOverseasProtocol.TryParseBalance(body, UsExchange.None, out var any));                   // None = 대조 생략
            Assert.Equal(0, any.MismatchedExchangeRows);
            Assert.True(KisOverseasProtocol.TryParseBalance(U("{\"rt_cd\":\"0\",\"output1\":[{\"ovrs_excg_cd\":1},{\"ovrs_excg_cd\":\"amex\"},{\"ovrs_excg_cd\":\"AMEX\"},{}]}"),
                UsExchange.Amex, out var mixed));
            Assert.Equal(4, mixed.HoldingRows);
            Assert.Equal(2, mixed.MismatchedExchangeRows);                                                          // 숫자 · 소문자 = 불일치 · 필드 부재 = 대조 안 함
        }

        [Fact]
        public void Balance_Variants_And_Error_Responses()
        {
            // 오류 응답(초당 한도)
            byte[] err = U("{\"rt_cd\":\"1\",\"msg_cd\":\"EGW00201\",\"msg1\":\"초당 거래건수를 초과하였습니다.\"}");
            Assert.True(KisOverseasProtocol.TryParseBalance(err, UsExchange.Nasdaq, out var e));
            Assert.True(e.Parsed); Assert.False(e.RtOk); Assert.False(e.Ok);
            Assert.Equal("EGW00201", e.MsgCd.Decode(err));
            Assert.Equal(-1, e.HoldingRows);
            Assert.Equal(M, e.PurchaseUsdRaw); Assert.Equal(M, e.EvalPnlUsdRaw); Assert.Equal(M, e.TotalPnlUsdRaw);

            // output2 배열 변형 = 첫 원소만 · 빈 보유
            Assert.True(KisOverseasProtocol.TryParseBalance(U("{\"rt_cd\":\"0\",\"output1\":[],\"output2\":[{\"frcr_pchs_amt1\":\"0.00000\"},{\"frcr_pchs_amt1\":\"9\",\"ovrs_tot_pfls\":\"9\"}]}"),
                UsExchange.Nyse, out var arr));
            Assert.True(arr.Ok); Assert.Equal(0, arr.HoldingRows); Assert.Equal(0, arr.PurchaseUsdRaw); Assert.Equal(M, arr.TotalPnlUsdRaw);

            // 금액 형식 이탈 = Missing(파싱은 성공) · 숫자 토큰 수용 · 공백 패딩 수용
            Assert.True(KisOverseasProtocol.TryParseBalance(U(Balance("AMEX", pchs: "", evlu: "1,234.5", tot: "abc")), UsExchange.Amex, out var miss));
            Assert.True(miss.Ok); Assert.Equal(M, miss.PurchaseUsdRaw); Assert.Equal(M, miss.EvalPnlUsdRaw); Assert.Equal(M, miss.TotalPnlUsdRaw);
            Assert.True(KisOverseasProtocol.TryParseBalance(U("{\"rt_cd\":\"0\",\"output2\":{\"frcr_pchs_amt1\":12.5,\"tot_evlu_pfls_amt\":\" -3.25 \",\"ovrs_tot_pfls\":{\"x\":1}}}"),
                UsExchange.Amex, out var num));
            Assert.Equal(1_250_000_000L, num.PurchaseUsdRaw); Assert.Equal(-325_000_000L, num.EvalPnlUsdRaw); Assert.Equal(M, num.TotalPnlUsdRaw);

            // 범위 초과 = Missing · 이스케이프 문자열 = Missing/위치 없음
            Assert.True(KisOverseasProtocol.TryParseBalance(U("{\"rt_cd\":\"0\",\"output2\":{\"frcr_pchs_amt1\":\"999999999999999999999.0\",\"ovrs_tot_pfls\":\"-92233720368.6\"}}"),
                UsExchange.Nasdaq, out var huge));
            Assert.Equal(M, huge.PurchaseUsdRaw); Assert.Equal(M, huge.TotalPnlUsdRaw);
            byte[] esc = U("{\"rt_cd\":\"\\u0030\",\"msg1\":\"a\\nb\",\"output2\":{\"frcr_pchs_amt1\":\"\\u0031\"}}");
            Assert.True(KisOverseasProtocol.TryParseBalance(esc, UsExchange.Nasdaq, out var e2));
            Assert.False(e2.RtOk); Assert.False(e2.Msg1.IsAvailable); Assert.Equal("", e2.Msg1.Decode(esc)); Assert.Equal(M, e2.PurchaseUsdRaw);
        }

        public static TheoryData<string> CorruptBalances => new()
        {
            "",
            " ",
            "<html>502 Bad Gateway</html>",
            "[1,2]",
            "\"rt_cd\"",
            "null",
            "{\"rt_cd\":\"0\"",                                                              // 잘림
            "{\"rt_cd\":\"0\",\"output1\":[{\"ovrs_excg_cd\":\"NASD\"}",                    // 배열 내부 잘림
            "{\"rt_cd\":\"0\"} {\"rt_cd\":\"0\"}",                                           // 뒤따르는 값
            "{\"rt_cd\":\"0\"} x",
            "{\"rt_cd\":\"0\",\"rt_cd\":\"1\"}",                                             // 중복 필드
            "{\"msg_cd\":\"a\",\"msg_cd\":\"b\"}",
            "{\"output1\":[],\"output1\":[]}",
            "{\"output2\":{},\"output2\":{}}",
            "{\"output2\":{\"frcr_pchs_amt1\":\"1\",\"frcr_pchs_amt1\":\"2\"}}",
            "{\"rt_cd\":{\"x\":0}}",                                                          // 결과 코드 컨테이너
            "{\"rt_cd\":\"0\",}",                                                             // 후행 쉼표
            "{\"rt_cd\":\"0\" /* c */}",                                                      // 주석
            "{\"a\":[[[[[[[[[[1]]]]]]]]]]}",                                                  // 깊이 폭탄
            "{\"rt_cd\":'0'}",
        };

        [Theory]
        [MemberData(nameof(CorruptBalances))]
        public void Corrupt_Balance_Bodies_Are_Rejected_Without_Exceptions(string body)
        {
            Assert.False(KisOverseasProtocol.TryParseBalance(U(body), UsExchange.Nasdaq, out var b));
            Assert.False(b.Parsed); Assert.False(b.Ok);
        }

        [Fact]
        public void Non_Utf8_And_Random_Mutations_Never_Throw()
        {
            // 토큰 자리의 비 UTF-8 = 거부(예외 0). ※ 문자열 내부 비 UTF-8 은 Utf8JsonReader 가 지연 검증 — 미추적 키는 무시 · 추적 문자열은 위치만(표시 복호 = 대체 문자)
            Assert.False(KisOverseasProtocol.TryParseBalance(new byte[] { 0x7B, 0xFF, 0xFE, 0x7D }, UsExchange.Nasdaq, out _));
            Assert.False(KisOverseasProtocol.TryParseQuote(new byte[] { 0x7B, 0x22, 0x61, 0x22, 0x3A, 0xC3, 0x28, 0x7D }, UsExchange.Nasdaq, "AAPL"u8, out _));
            Assert.True(KisOverseasProtocol.TryParseBalance(new byte[] { 0x7B, 0x22, 0xFF, 0xFE, 0x22, 0x3A, 0x31, 0x7D }, UsExchange.Nasdaq, out var lazy));
            Assert.False(lazy.Ok);                                                                                      // rt_cd 부재 = 성공 아님
            byte[][] seeds = { U(Balance("NASD")), U(Balance("NYSE")), U(Quote("DNASAAPL")), U("{\"rt_cd\":\"1\",\"msg_cd\":\"EGW00201\"}") };
            var rng = new Random(57);
            var buf = new byte[4096];
            int accepted = 0;
            for (int i = 0; i < 100_000; i++)
            {
                var src = seeds[i % seeds.Length];
                int len = src.Length;
                src.CopyTo(buf, 0);
                int edits = 1 + rng.Next(4);
                for (int e = 0; e < edits; e++)
                {
                    int op = rng.Next(3), pos = rng.Next(Math.Max(1, len));
                    if (op == 0 && len > 0) buf[pos] = (byte)rng.Next(256);
                    else if (op == 1 && len < buf.Length - 1) { Buffer.BlockCopy(buf, pos, buf, pos + 1, len - pos); buf[pos] = (byte)"{}[]\",:0.-e"[rng.Next(11)]; len++; }
                    else if (len > 1) { Buffer.BlockCopy(buf, pos + 1, buf, pos, len - pos - 1); len--; }
                }
                var span = new ReadOnlySpan<byte>(buf, 0, len);
                if (KisOverseasProtocol.TryParseBalance(span, UsExchange.Nasdaq, out var b))
                {
                    accepted++;
                    Assert.True(b.HoldingRows >= -1);
                    Assert.True(b.MismatchedExchangeRows >= 0);
                    if (b.Msg1.IsAvailable) Assert.True(b.Msg1.Start + b.Msg1.Length <= len);
                }
                if (KisOverseasProtocol.TryParseQuote(span, UsExchange.Nasdaq, "AAPL"u8, out var q) && q.IsUsable) Assert.True(q.LastRaw > 0);
            }
            Assert.True(accepted > 0);
        }

        // ════════════ §3 현재가 ════════════

        [Theory]
        [InlineData(UsExchange.Nasdaq, "DNASAAPL", "AAPL")]
        [InlineData(UsExchange.Nyse, "DNYSIBM", "IBM")]
        [InlineData(UsExchange.Amex, "DAMSSPY", "SPY")]
        public void Quote_Parses_And_Verifies_Rsym_Per_Exchange(UsExchange ex, string rsym, string symbol)
        {
            byte[] body = U(Quote(rsym));
            Assert.True(KisOverseasProtocol.TryParseQuote(body, ex, U(symbol), out var q));
            Assert.True(q.IsUsable); Assert.True(q.SymbolVerified);
            Assert.Equal(229 * S, q.LastRaw);
            Assert.Equal(22_752_000_000L, q.BaseRaw);
            Assert.Equal(38_123_456L, q.Volume);
            Assert.Equal(4, q.PriceDecimals);
            Assert.Equal("MCA00000", q.MsgCd.Decode(body));
            foreach (var other in UsExchangeCode.All)
            {
                if (other == ex) continue;
                Assert.True(KisOverseasProtocol.TryParseQuote(body, other, U(symbol), out var wrong));
                Assert.False(wrong.SymbolVerified); Assert.False(wrong.IsUsable);                                    // 다른 거래소로 판독 = 사용 불가
            }
            Assert.True(KisOverseasProtocol.TryParseQuote(body, ex, U(symbol + "X"), out var sym2));
            Assert.False(sym2.IsUsable);
            Assert.True(KisOverseasProtocol.TryParseQuote(body, UsExchange.None, U(symbol), out var none));
            Assert.False(none.IsUsable);
        }

        [Fact]
        public void Quote_Without_Price_Or_Symbol_Is_Not_Usable()
        {
            Assert.True(KisOverseasProtocol.TryParseQuote(U(Quote("", last: "", prev: "", tvol: "", zdiv: "")), UsExchange.Nasdaq, "ZZZZ"u8, out var empty));
            Assert.True(empty.Parsed); Assert.True(empty.RtOk); Assert.False(empty.SymbolVerified); Assert.False(empty.IsUsable);
            Assert.Equal(M, empty.LastRaw); Assert.Equal(M, empty.Volume); Assert.Equal(-1, empty.PriceDecimals);
            Assert.True(KisOverseasProtocol.TryParseQuote(U(Quote("DNASAAPL", last: "0.0000")), UsExchange.Nasdaq, "AAPL"u8, out var zero));
            Assert.False(zero.IsUsable);
            Assert.True(KisOverseasProtocol.TryParseQuote(U(Quote("DNASAAPL", last: "-1")), UsExchange.Nasdaq, "AAPL"u8, out var neg));
            Assert.False(neg.IsUsable);
            Assert.True(KisOverseasProtocol.TryParseQuote(U("{\"rt_cd\":\"1\",\"msg_cd\":\"EGW00201\",\"output\":{\"rsym\":\"DNASAAPL\",\"last\":\"229\"}}"), UsExchange.Nasdaq, "AAPL"u8, out var err));
            Assert.False(err.IsUsable);                                                                                // rt_cd ≠ 0
            Assert.True(KisOverseasProtocol.TryParseQuote(U("{\"rt_cd\":\"0\",\"output\":[]}"), UsExchange.Nasdaq, "AAPL"u8, out var arr));
            Assert.False(arr.IsUsable);
            Assert.True(KisOverseasProtocol.TryParseQuote(U(Quote("DNASAAPL", zdiv: "4.5")), UsExchange.Nasdaq, "AAPL"u8, out var zd));
            Assert.Equal(-1, zd.PriceDecimals);
            Assert.True(KisOverseasProtocol.TryParseQuote(U(Quote("DNASAAPL", last: "0.1234")), UsExchange.Nasdaq, "AAPL"u8, out var sub));
            Assert.Equal(12_340_000L, sub.LastRaw);                                                                   // 서브페니
            Assert.False(KisOverseasProtocol.TryParseQuote(U("{\"output\":{\"last\":\"1\",\"last\":\"2\"}}"), UsExchange.Nasdaq, "AAPL"u8, out _));
            Assert.False(KisOverseasProtocol.TryParseQuote(U("{\"output\":{\"rsym\":\"DNASAAPL\",\"rsym\":\"DNASAAPL\"}}"), UsExchange.Nasdaq, "AAPL"u8, out _));
            Assert.False(KisOverseasProtocol.TryParseQuote(U(Quote("DNASAAPL") + "}"), UsExchange.Nasdaq, "AAPL"u8, out _));
        }

        // ════════════ §4 게이트 ════════════

        [Theory]
        [InlineData(UsSessionPhase.ContinuousTrading, UsQuoteValidity.Tradable)]
        [InlineData(UsSessionPhase.PreMarket, UsQuoteValidity.ExtendedIndicative)]
        [InlineData(UsSessionPhase.AfterHours, UsQuoteValidity.ExtendedIndicative)]
        [InlineData(UsSessionPhase.Closed, UsQuoteValidity.ClosedReference)]
        [InlineData(UsSessionPhase.HolidayOrWeekend, UsQuoteValidity.ClosedReference)]
        [InlineData(UsSessionPhase.CalendarUncovered, UsQuoteValidity.Uncovered)]
        [InlineData((UsSessionPhase)99, UsQuoteValidity.Uncovered)]
        public void Gate_Maps_Session_Phase_To_Validity_And_Only_Regular_Session_Is_Order_Basis(UsSessionPhase phase, UsQuoteValidity expected)
        {
            Assert.True(KisOverseasProtocol.TryParseQuote(U(Quote("DNASAAPL")), UsExchange.Nasdaq, "AAPL"u8, out var q));
            var v = UsQuoteSessionGate.Classify(phase, q);
            Assert.Equal(expected, v);
            Assert.Equal(expected == UsQuoteValidity.Tradable, UsQuoteSessionGate.IsOrderBasis(v));
            Assert.True(KisOverseasProtocol.TryParseQuote(U(Quote("")), UsExchange.Nasdaq, "AAPL"u8, out var bad));
            Assert.Equal(UsQuoteValidity.Invalid, UsQuoteSessionGate.Classify(phase, bad));                         // 사용 불가 시세 = 페이즈 무관 Invalid
            Assert.Equal(UsQuoteValidity.Invalid, UsQuoteSessionGate.Classify(phase, default));
            Assert.False(UsQuoteSessionGate.IsOrderBasis(UsQuoteValidity.Invalid));
        }

        [Fact]
        public void Gate_Binds_Real_Session_Clock_Kst_Boundaries()
        {
            Assert.True(KisOverseasProtocol.TryParseQuote(U(Quote("DNASAAPL")), UsExchange.Nasdaq, "AAPL"u8, out var q));
            UsQuoteValidity At(int y, int mo, int d, int h, int mi) => UsQuoteSessionGate.Classify(UsSessionStateMachine.GetSessionPhase(new DateTime(y, mo, d, h, mi, 0)), q);
            Assert.Equal(UsQuoteValidity.ClosedReference, At(2026, 10, 22, 16, 59));                                 // EDT 프리 직전
            Assert.Equal(UsQuoteValidity.ExtendedIndicative, At(2026, 10, 22, 17, 0));                               // 프리 17:00 KST
            Assert.Equal(UsQuoteValidity.ExtendedIndicative, At(2026, 10, 22, 22, 29));
            Assert.Equal(UsQuoteValidity.Tradable, At(2026, 10, 22, 22, 30));                                        // 정규 22:30 KST
            Assert.Equal(UsQuoteValidity.Tradable, At(2026, 10, 23, 4, 59));
            Assert.Equal(UsQuoteValidity.ExtendedIndicative, At(2026, 10, 23, 5, 0));                                // 애프터 05:00 KST
            Assert.Equal(UsQuoteValidity.Tradable, At(2026, 11, 10, 23, 30));                                        // EST 정규 23:30 KST
            Assert.Equal(UsQuoteValidity.ExtendedIndicative, At(2026, 11, 10, 23, 29));
            Assert.Equal(UsQuoteValidity.ClosedReference, At(2026, 10, 24, 23, 0));                                  // 토요일
            Assert.Equal(UsQuoteValidity.Uncovered, At(2031, 1, 5, 23, 30));                                         // 달력 범위 밖
        }

        // ════════════ §5 δ=0 ════════════

        [Fact]
        public void Three_Exchange_Balance_Parsing_100k_Zero_Alloc()
        {
            byte[][] bodies = { U(Balance("NASD")), U(Balance("NYSE")), U(Balance("AMEX")), U("{\"rt_cd\":\"1\",\"msg_cd\":\"EGW00201\",\"msg1\":\"초당 거래건수를 초과하였습니다.\"}") };
            UsExchange[] ex = { UsExchange.Nasdaq, UsExchange.Nyse, UsExchange.Amex, UsExchange.Nasdaq };
            long sum = 0, rows = 0;
            Assert.True(KisOverseasProtocol.TryParseBalance(bodies[0], ex[0], out _));                               // 정적 초기화 · JIT 예열(창 밖)
            long bytes = ZeroAllocProbe.MinAllocated(() =>
            {
                for (int i = 0; i < 100_000; i++)
                {
                    int k = i & 3;
                    if (KisOverseasProtocol.TryParseBalance(bodies[k], ex[k], out var b) && b.Ok) { sum += b.PurchaseUsdRaw; rows += b.HoldingRows - b.MismatchedExchangeRows; }
                }
            });
            Assert.Equal(0, bytes);
            Assert.True(sum > 0 && rows > 0);
        }

        [Fact]
        public void Quote_Parsing_And_Session_Gate_100k_Zero_Alloc()
        {
            byte[][] bodies = { U(Quote("DNASAAPL")), U(Quote("DNYSIBM")), U(Quote("DAMSSPY")), U(Quote("", last: "")) };
            byte[][] syms = { U("AAPL"), U("IBM"), U("SPY"), U("ZZZZ") };
            UsExchange[] ex = { UsExchange.Nasdaq, UsExchange.Nyse, UsExchange.Amex, UsExchange.Nasdaq };
            UsSessionPhase[] phases = { UsSessionPhase.ContinuousTrading, UsSessionPhase.PreMarket, UsSessionPhase.AfterHours, UsSessionPhase.Closed };
            long tradable = 0, last = 0;
            long t0 = new DateTime(2026, 10, 22, 22, 30, 0).Ticks;
            Assert.True(KisOverseasProtocol.TryParseQuote(bodies[0], ex[0], syms[0], out var warm));
            UsQuoteSessionGate.Classify(UsSessionStateMachine.GetSessionPhase(new DateTime(t0)), warm);
            long bytes = ZeroAllocProbe.MinAllocated(() =>
            {
                for (int i = 0; i < 100_000; i++)
                {
                    int k = i & 3;
                    if (!KisOverseasProtocol.TryParseQuote(bodies[k], ex[k], syms[k], out var q)) continue;
                    var phase = (i & 4) == 0 ? phases[k] : UsSessionStateMachine.GetSessionPhase(new DateTime(t0 + i * TimeSpan.TicksPerSecond));
                    var v = UsQuoteSessionGate.Classify(phase, q);
                    if (UsQuoteSessionGate.IsOrderBasis(v)) { tradable++; last += q.LastRaw; }
                }
            });
            Assert.Equal(0, bytes);
            Assert.True(tradable > 0 && last > 0);
        }
    }
}
