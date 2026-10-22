// ============================================================================
// KisOverseasProtocol — [v12.18.57-DEV] KIS 해외주식 응답 무할당 파서(VTTS3012R 해외 잔고 · HHDFS00000300 현재체결가) + 미국 세션 시세 유효성 게이트(δ=0)
//
//  목적: 미국 3대 거래소(UsExchangeCode) 잔고 · 현재가 응답 본문을 힙 할당 없이 raw 10^8(UsTickAligner.Scale) 정수로 판독하고,
//   UsSessionStateMachine 페이즈와 결합해 '이 시세를 주문 근거로 써도 되는가' 를 판정한다. 송출 코드 0(요청 생성/전송은 호출 측 · 본 파일 = 순수 판독).
//  ★파서: UpbitStreamProtocol 과 같은 형태 — ReadOnlySpan<byte> · Utf8JsonReader(isFinalBlock=false · 잘림 = 예외 없이 거부) · 루트 뒤 잔여 = 공백만 ·
//   깊이 상한 8 · 필드 순서 무관 · 추적 필드 중복 = 거부 · 예외는 JsonException 만 catch. 문자열 필드(rt_cd · msg_cd · msg1)는 본문 내 위치(KisTextRange)만
//   기록(복호 = 표시 경로에서만 · 이스케이프 포함 값 = 위치 없음). 호출당 힙 0.
//  금액/가격(KIS = 문자열 "1234.56000" · 숫자 토큰도 수용): Utf8Parser 로 decimal → raw 10^8 반올림 · 공백/빈 문자열/형식 이탈/|값| 범위 초과/이스케이프 = Missing.
//  VTTS3012R: output1(보유 행 배열 · 행별 ovrs_excg_cd ≠ 요청 거래소 = 불일치 행 집계) · output2(객체 — 배열 변형은 첫 원소) ·
//   frcr_pchs_amt1(외화 매입) · tot_evlu_pfls_amt(평가손익) · ovrs_tot_pfls(총손익). output1 부재/비배열 = 행 수 -1.
//  HHDFS00000300: output.rsym = "D" + 시세 코드(NAS/NYS/AMS) + 심볼 정확 일치만 SymbolVerified(빈 값/불일치 = 사용 불가) ·
//   last(현재가) · base(전일 종가) · tvol(거래량 · 소수 절사) · zdiv(소수 자리수). 사용 가능 = 파싱 · rt_cd "0" · 심볼 검증 · last > 0.
//  ★게이트(UsQuoteSessionGate): 정규 연속매매(ContinuousTrading)만 Tradable — 프리/애프터 = ExtendedIndicative(참고용 · 호가 공백/유동성 상이) ·
//   장 마감/휴장 = ClosedReference(마지막 가격 = 직전 종가 참고) · 달력 범위 밖 = Uncovered · 사용 불가 시세 = Invalid. 주문 근거 = Tradable 만(fail-closed).
//   ※ 게이트 판정은 시세 품질 판정일 뿐 — 해외 주문 송출 경로는 존재하지 않는다(liveArmed:false 와 무관하게 송출 0).
// ============================================================================
using System;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using StockManager.Engines.Feeds;
using StockManager.Engines.Pricing;

namespace StockManager.Engines.Brokers.Kis
{
    /// <summary>응답 본문 내 문자열 값 위치(바이트 · 따옴표 제외). Start &lt; 0 = 없음(부재 · 이스케이프 포함).</summary>
    public readonly record struct KisTextRange(int Start, int Length)
    {
        public static readonly KisTextRange None = new(-1, 0);
        public bool IsAvailable => Start >= 0;

        public ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> body)
            => IsAvailable && (uint)Start <= (uint)body.Length && (uint)Length <= (uint)(body.Length - Start) ? body.Slice(Start, Length) : ReadOnlySpan<byte>.Empty;

        /// <summary>표시 경로 전용 복호(할당 발생 — 핫패스 금지).</summary>
        public string Decode(ReadOnlySpan<byte> body) => Encoding.UTF8.GetString(Slice(body));
    }

    /// <summary>VTTS3012R 해외 잔고 판독 결과(raw 10^8 · 금액 부재/형식 이탈 = Missing).</summary>
    public readonly record struct KisOverseasBalance(bool Parsed, bool RtOk, KisTextRange RtCd, KisTextRange MsgCd, KisTextRange Msg1,
                                                     int HoldingRows, int MismatchedExchangeRows, long PurchaseUsdRaw, long EvalPnlUsdRaw, long TotalPnlUsdRaw)
    {
        public const long Missing = long.MinValue;
        public bool Ok => Parsed && RtOk;
    }

    /// <summary>HHDFS00000300 현재체결가 판독 결과(raw 10^8 · 부재/형식 이탈 = Missing).</summary>
    public readonly record struct KisOverseasQuote(bool Parsed, bool RtOk, KisTextRange RtCd, KisTextRange MsgCd, KisTextRange Msg1,
                                                   bool SymbolVerified, long LastRaw, long BaseRaw, long Volume, int PriceDecimals)
    {
        public const long Missing = long.MinValue;
        public bool IsUsable => Parsed && RtOk && SymbolVerified && LastRaw > 0;
    }

    public static class KisOverseasProtocol
    {
        public const int MaxDepth = 8;
        public const long Missing = long.MinValue;

        private const byte SecNone = 0, SecRows = 1, SecTotalsObject = 2, SecTotalsArray = 3, SecQuote = 4;
        private static readonly decimal RawLimit = (decimal)long.MaxValue / UsTickAligner.Scale;

        /// <summary>VTTS3012R 응답 판독. expected = 요청 거래소(행별 ovrs_excg_cd 대조 · None = 대조 생략). 구조 이탈 = Parsed false.</summary>
        public static bool TryParseBalance(ReadOnlySpan<byte> body, UsExchange expected, out KisOverseasBalance b)
        {
            b = default;
            ReadOnlySpan<byte> code = UsExchangeCode.TradeCodeUtf8(expected);
            bool rtSeen = false, msgCdSeen = false, msg1Seen = false, rowsSeen = false, totalsSeen = false;
            bool pchsSeen = false, evluSeen = false, totSeen = false;
            KisTextRange rt = KisTextRange.None, msgCd = KisTextRange.None, msg1 = KisTextRange.None;
            bool rtOk = false;
            int rows = -1, mismatched = 0, totalsElem = 0;
            long pchs = Missing, evlu = Missing, tot = Missing;
            byte sec = SecNone;
            try
            {
                var r = new Utf8JsonReader(body, isFinalBlock: false, new JsonReaderState(new JsonReaderOptions { MaxDepth = MaxDepth }));
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
                int depth = 1;
                while (depth > 0 && r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.StartObject:
                            depth++;
                            if (depth == 3 && sec == SecRows) rows++;
                            else if (depth == 3 && sec == SecTotalsArray) totalsElem++;
                            break;
                        case JsonTokenType.StartArray: depth++; break;
                        case JsonTokenType.EndObject:
                        case JsonTokenType.EndArray:
                            if (depth == 2) sec = SecNone;
                            depth--; break;
                        case JsonTokenType.PropertyName:
                            if (depth == 1)
                            {
                                if (r.ValueTextEquals("rt_cd"u8)) { if (rtSeen || !ReadText(ref r, out rt)) return false; rtSeen = true; rtOk = rt.IsAvailable && r.ValueTextEquals("0"u8); }
                                else if (r.ValueTextEquals("msg_cd"u8)) { if (msgCdSeen || !ReadText(ref r, out msgCd)) return false; msgCdSeen = true; }
                                else if (r.ValueTextEquals("msg1"u8)) { if (msg1Seen || !ReadText(ref r, out msg1)) return false; msg1Seen = true; }
                                else if (r.ValueTextEquals("output1"u8))
                                {
                                    if (rowsSeen || !r.Read()) return false;
                                    rowsSeen = true;
                                    if (r.TokenType == JsonTokenType.StartArray) { rows = 0; sec = SecRows; depth++; }
                                    else if (r.TokenType == JsonTokenType.StartObject) depth++;      // 비배열 = 행 수 -1(내용 무시)
                                }
                                else if (r.ValueTextEquals("output2"u8))
                                {
                                    if (totalsSeen || !r.Read()) return false;
                                    totalsSeen = true;
                                    if (r.TokenType == JsonTokenType.StartObject) { sec = SecTotalsObject; depth++; }
                                    else if (r.TokenType == JsonTokenType.StartArray) { sec = SecTotalsArray; depth++; }
                                }
                            }
                            else if (depth == 3 && sec == SecRows)
                            {
                                if (r.ValueTextEquals("ovrs_excg_cd"u8))
                                {
                                    if (!r.Read()) return false;
                                    if (code.Length != 0 && (r.TokenType != JsonTokenType.String || r.ValueIsEscaped || !r.ValueSpan.SequenceEqual(code))) mismatched++;
                                    if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++;
                                }
                            }
                            else if ((depth == 2 && sec == SecTotalsObject) || (depth == 3 && sec == SecTotalsArray && totalsElem == 1))
                            {
                                if (r.ValueTextEquals("frcr_pchs_amt1"u8)) { if (pchsSeen || !ReadAmount(ref r, ref depth, out pchs)) return false; pchsSeen = true; }
                                else if (r.ValueTextEquals("tot_evlu_pfls_amt"u8)) { if (evluSeen || !ReadAmount(ref r, ref depth, out evlu)) return false; evluSeen = true; }
                                else if (r.ValueTextEquals("ovrs_tot_pfls"u8)) { if (totSeen || !ReadAmount(ref r, ref depth, out tot)) return false; totSeen = true; }
                            }
                            break;
                    }
                }
                if (depth != 0 || !IsWhitespace(body.Slice((int)r.BytesConsumed))) return false;
            }
            catch (JsonException) { return false; }                                        // 어휘 오류(잘못된 토큰 · 비 UTF-8 · 깊이 초과)

            b = new KisOverseasBalance(true, rtOk, rt, msgCd, msg1, rows, mismatched, pchs, evlu, tot);
            return true;
        }

        /// <summary>HHDFS00000300 응답 판독. rsym = "D" + 시세 코드(exchange) + symbol(ASCII) 정확 일치일 때만 SymbolVerified. 구조 이탈 = Parsed false.</summary>
        public static bool TryParseQuote(ReadOnlySpan<byte> body, UsExchange exchange, ReadOnlySpan<byte> symbol, out KisOverseasQuote q)
        {
            q = default;
            ReadOnlySpan<byte> code = UsExchangeCode.QuoteCodeUtf8(exchange);
            bool rtSeen = false, msgCdSeen = false, msg1Seen = false, outSeen = false;
            bool rsymSeen = false, lastSeen = false, baseSeen = false, tvolSeen = false, zdivSeen = false;
            KisTextRange rt = KisTextRange.None, msgCd = KisTextRange.None, msg1 = KisTextRange.None;
            bool rtOk = false, verified = false;
            long last = Missing, prev = Missing, vol = Missing, zdivRaw = Missing;
            byte sec = SecNone;
            try
            {
                var r = new Utf8JsonReader(body, isFinalBlock: false, new JsonReaderState(new JsonReaderOptions { MaxDepth = MaxDepth }));
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
                int depth = 1;
                while (depth > 0 && r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.StartObject:
                        case JsonTokenType.StartArray: depth++; break;
                        case JsonTokenType.EndObject:
                        case JsonTokenType.EndArray:
                            if (depth == 2) sec = SecNone;
                            depth--; break;
                        case JsonTokenType.PropertyName:
                            if (depth == 1)
                            {
                                if (r.ValueTextEquals("rt_cd"u8)) { if (rtSeen || !ReadText(ref r, out rt)) return false; rtSeen = true; rtOk = rt.IsAvailable && r.ValueTextEquals("0"u8); }
                                else if (r.ValueTextEquals("msg_cd"u8)) { if (msgCdSeen || !ReadText(ref r, out msgCd)) return false; msgCdSeen = true; }
                                else if (r.ValueTextEquals("msg1"u8)) { if (msg1Seen || !ReadText(ref r, out msg1)) return false; msg1Seen = true; }
                                else if (r.ValueTextEquals("output"u8))
                                {
                                    if (outSeen || !r.Read()) return false;
                                    outSeen = true;
                                    if (r.TokenType == JsonTokenType.StartObject) { sec = SecQuote; depth++; }
                                    else if (r.TokenType == JsonTokenType.StartArray) depth++;          // 비객체 = 시세 없음
                                }
                            }
                            else if (depth == 2 && sec == SecQuote)
                            {
                                if (r.ValueTextEquals("rsym"u8))
                                {
                                    if (rsymSeen || !r.Read()) return false;
                                    rsymSeen = true;
                                    if (r.TokenType == JsonTokenType.String && !r.ValueIsEscaped && code.Length != 0 && symbol.Length != 0)
                                    {
                                        ReadOnlySpan<byte> v = r.ValueSpan;
                                        verified = v.Length == 1 + code.Length + symbol.Length && v[0] == (byte)'D'
                                                   && v.Slice(1, code.Length).SequenceEqual(code) && v.Slice(1 + code.Length).SequenceEqual(symbol);
                                    }
                                    else if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++;
                                }
                                else if (r.ValueTextEquals("last"u8)) { if (lastSeen || !ReadAmount(ref r, ref depth, out last)) return false; lastSeen = true; }
                                else if (r.ValueTextEquals("base"u8)) { if (baseSeen || !ReadAmount(ref r, ref depth, out prev)) return false; baseSeen = true; }
                                else if (r.ValueTextEquals("tvol"u8)) { if (tvolSeen || !ReadAmount(ref r, ref depth, out vol)) return false; tvolSeen = true; }
                                else if (r.ValueTextEquals("zdiv"u8)) { if (zdivSeen || !ReadAmount(ref r, ref depth, out zdivRaw)) return false; zdivSeen = true; }
                            }
                            break;
                    }
                }
                if (depth != 0 || !IsWhitespace(body.Slice((int)r.BytesConsumed))) return false;
            }
            catch (JsonException) { return false; }

            long shares = vol == Missing || vol < 0 ? Missing : vol / UsTickAligner.Scale;               // 거래량 = 정수 주(소수 절사)
            int decimals = zdivRaw != Missing && zdivRaw >= 0 && zdivRaw % UsTickAligner.Scale == 0 && zdivRaw / UsTickAligner.Scale <= 8
                ? (int)(zdivRaw / UsTickAligner.Scale) : -1;
            q = new KisOverseasQuote(true, rtOk, rt, msgCd, msg1, verified, last, prev, shares, decimals);
            return true;
        }

        /// <summary>다음 토큰 = 문자열 → 위치(이스케이프 포함 = None · 판독 성공). 비문자열 스칼라 = None. 컨테이너 = 구조 이탈(false).</summary>
        private static bool ReadText(ref Utf8JsonReader r, out KisTextRange range)
        {
            range = KisTextRange.None;
            if (!r.Read()) return false;
            if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) return false;
            if (r.TokenType == JsonTokenType.String && !r.ValueIsEscaped) range = new KisTextRange((int)r.TokenStartIndex + 1, r.ValueSpan.Length);
            return true;
        }

        /// <summary>다음 토큰(문자열/숫자) → raw 10^8(반올림 · 부재/형식 이탈/범위 초과 = Missing). 컨테이너 = Missing + depth 반영. 읽기 실패 = false.</summary>
        private static bool ReadAmount(ref Utf8JsonReader r, ref int depth, out long raw)
        {
            raw = Missing;
            if (!r.Read()) return false;
            decimal d;
            switch (r.TokenType)
            {
                case JsonTokenType.String:
                    if (r.ValueIsEscaped) return true;
                    ReadOnlySpan<byte> s = TrimSpaces(r.ValueSpan);
                    if (s.Length == 0 || !Utf8Parser.TryParse(s, out d, out int used) || used != s.Length) return true;
                    break;
                case JsonTokenType.Number:
                    if (!r.TryGetDecimal(out d)) return true;
                    break;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    depth++;
                    return true;
                default:
                    return true;
            }
            if (d >= RawLimit || d <= -RawLimit) return true;                                         // 곱셈 전 차단(decimal 예외 0)
            raw = (long)decimal.Round(d * UsTickAligner.Scale);
            return true;
        }

        private static ReadOnlySpan<byte> TrimSpaces(ReadOnlySpan<byte> s)
        {
            int a = 0, z = s.Length;
            while (a < z && s[a] == 0x20) a++;
            while (z > a && s[z - 1] == 0x20) z--;
            return s.Slice(a, z - a);
        }

        private static bool IsWhitespace(ReadOnlySpan<byte> rest)
        {
            foreach (byte b in rest) if (b != 0x20 && b != 0x09 && b != 0x0D && b != 0x0A) return false;     // JSON 공백 4종
            return true;
        }
    }

    public enum UsQuoteValidity : byte
    {
        Invalid = 0,                // default ≠ 유효 — 사용 불가 시세(파싱 실패 · rt_cd ≠ 0 · 심볼 불일치 · 가격 없음)
        Tradable = 1,               // 정규 연속매매 — 주문 근거 허용 유일 판정
        ExtendedIndicative = 2,     // 프리마켓 · 애프터마켓 — 참고용
        ClosedReference = 3,        // 장 마감 · 주말/휴장 — 마지막 가격 = 직전 종가 참고
        Uncovered = 4,              // 달력 표 범위 밖 — 판정 불가(fail-closed)
    }

    /// <summary>미국 세션 페이즈 × 시세 사용 가능 여부 → 시세 유효성(순수 · δ=0).</summary>
    public static class UsQuoteSessionGate
    {
        public static UsQuoteValidity Classify(UsSessionPhase phase, in KisOverseasQuote q)
        {
            if (!q.IsUsable) return UsQuoteValidity.Invalid;
            return phase switch
            {
                UsSessionPhase.ContinuousTrading => UsQuoteValidity.Tradable,
                UsSessionPhase.PreMarket or UsSessionPhase.AfterHours => UsQuoteValidity.ExtendedIndicative,
                UsSessionPhase.Closed or UsSessionPhase.HolidayOrWeekend => UsQuoteValidity.ClosedReference,
                _ => UsQuoteValidity.Uncovered,
            };
        }

        public static bool IsOrderBasis(UsQuoteValidity v) => v == UsQuoteValidity.Tradable;
    }
}
