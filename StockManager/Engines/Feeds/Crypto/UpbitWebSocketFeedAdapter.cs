// ============================================================================
// UpbitWebSocketFeedAdapter — [v12.18.56-DEV] 업비트 공개 WebSocket 실시간 호가/체결 피드(KRW-BTC · 인증 0 · 주문 0 · 수신/파싱 δ=0)
//
//  목적: REST 폴링(v12.18.54 UpbitPublicFeedProbe · 왕복 ~10ms) 대신 wss://api.upbit.com/websocket/v1 공개 스트림에서
//   orderbook(L1 = orderbook_units[0]) · trade 틱을 받아 seqlock 시장 상태(UpbitMarketState)와 CryptoPaperExchange(순수 인메모리)로 즉시 투영.
//  ★송출 = 구독 요청 1건(연결당) + 유휴 유지 "PING"(기본 60초) 뿐 — 접속 대상은 Endpoint 정확 일치만(다른 스킴/호스트/경로/쿼리/사용자정보 = 연결 전 예외).
//   주문 · 인증 · 거래 API 참조 0 → liveArmed:false 와 무관하게 실거래 송출 경로 자체가 없다. 체결은 메모리 잔고만 바꾼다.
//  구독(DEFAULT 포맷 · 업비트 개발자 문서 'WebSocket 요청 포맷'):
//   [{"ticket":"SM-UPBIT-STREAM"},{"type":"orderbook","codes":["KRW-BTC"]},{"type":"trade","codes":["KRW-BTC"]}]
//  ★파서(UpbitStreamProtocol.TryParse): KisProtocol/UpbitPublicProtocol 과 같은 형태 — ReadOnlySpan<byte> · Utf8JsonReader 타입드 게터(TryGetDecimal/TryGetInt64) ·
//   예외는 JsonException 만 catch · ★isFinalBlock=false — 잘림/뒤따르는 값 = 예외 없이 거부(할당 0) · 최상위 = 객체 1개(잔여 = 공백만) · 깊이 상한 8 · 필드 순서 무관 · 추적 필드 중복 = 거부 ·
//   code 정확 일치 · 숫자 → raw 10^8(음수 · 범위 초과 = 거부 · 소수 8자리 초과 = 내림) · 시각 ms > 0 · ×1000 오버플로 없는 범위만. 호출당 힙 0.
//   orderbook = 첫 단 ask/bid 가격 · 잔량 4종 필수 · 0 < 매수1 < 매도1. trade = trade_price · trade_volume > 0 · trade_timestamp · ask_bid(ASK/BID) 필수.
//   {"status":"UP"}(PING 응답) = Status · {"error":{…}} = Error · 다른 type = Unsupported · 그 외 = Invalid — 전부 카운터만 증가(상태 무변경 · 크래시 0).
//  투영: 시장 상태 = 단일 라이터 CAS 진입권 + seqlock(짝=안정 · 홀=기입 중 · CryptoPaperExchange 와 같은 규약) — 호가 시각 역행 = 폐기 ·
//   체결 시각 역행 또는 같은 시각 같은 sequential_id(재전송) = 폐기. 수용된 호가만 exchange.UpdateQuote(매수1, 매도1, 호가 시각 ms × 1000).
//   경합(CAS 실패)·거래소 거부 = 즉시 폐기(대기/재시도 0 — 다음 프레임이 최신값으로 덮는다).
//  수신(ReceiveLoopAsync): 고정(POH) 64KB 버퍼에 조각을 이어 붙여 완성 메시지만 OnFrame(Text/Binary 모두 · 업비트는 Binary 송신) ·
//   버퍼 초과 메시지 = 끝까지 폐기(오판독 0). 전송 ReceiveAsync = ValueTask(풀링 빌더) · 루프/파서/투영 = 프레임당 힙 0.
//  연결 관리(RunAsync · 콜드): 접속 제한 시간 10초 · 실패 = 지수 백오프 1 · 2 · 4 · … · 30초 상한(업비트 연결 요청 한도 초당 5회 미만) ·
//   수신 프레임이 1개 이상이던 연결이 끊기면 백오프 초기화. 유지 루프(1초 틱): 무수신 10초 = 침묵 단절(half-open) → 연결 강제 종료 → 재접속 ·
//   PING 60초(업비트 유휴 120초 종료 대비 · 메시지 한도 초당 5회 미만). 송신은 구독 완료 후 유지 루프 단독(동시 송신 0).
//  ★fail-closed: 단절 중 IsConnected = false · 호가 갱신 정지 → CryptoPaperExchange 가 호가 지연(기본 2초) 초과로 가상 주문을 StaleQuote 거부.
//   전송/파서/콜백 예외는 루프 밖으로 새지 않는다(Faults 카운터 · 재접속). 시각: 체결/호가 판정은 업비트 프레임 시각(자체 시계 미사용) ·
//   침묵 판정만 Environment.TickCount64(무할당 단조 시계).
//  [v12.18.74-DEV] 다중 마켓 결선: UpbitMarketSet(콜드 생성 · 1~8 마켓 · 코드 형식/중복 검증 → 구독 페이로드 1건 생성) — UpbitFeedOptions.Markets
//   (기본 = KRW-BTC 단독 · 기존 페이로드 바이트 동일). 파서 = code → 표 인덱스(ValueTextEquals 순차 · 표 밖 = Invalid · 할당 0) ·
//   시장 상태 = 마켓별 seqlock 1개(시각 역행/재전송 판정이 마켓마다 독립) · 거래소(CryptoPaperExchange) = 인덱스 0(주 마켓)만 투영.
//   marketTap(인덱스 · 프레임) = 시장 상태가 '수용'한 프레임만 통지 — 재접속 SNAPSHOT 의 직전 체결 재전송(같은 시각 · 같은 sequential_id)이
//   소비자 누적 체결량에 이중 가산되지 않는다. 기존 tap = 인덱스 0 프레임만(다른 마켓을 주 마켓으로 오인 통지 0 · 기존 의미 유지).
// ============================================================================
using System;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Engines.Brokers.Crypto;
using StockManager.Engines.Pricing;

namespace StockManager.Engines.Feeds.Crypto
{
    public enum UpbitStreamKind : byte
    {
        Invalid = 0,                // default ≠ 유효
        Orderbook = 1,
        Trade = 2,
        Status = 3,                 // {"status":"UP"} — PING 응답
        Error = 4,                  // {"error":{"name":…,"message":…}}
        Unsupported = 5,            // 형식은 맞지만 구독하지 않은 type
    }

    public enum UpbitTradeSide : byte { None = 0, Bid = 1, Ask = 2 }   // ask_bid: BID = 매수 체결 · ASK = 매도 체결

    /// <summary>판독된 프레임(raw 10^8 · 시각 = 유닉스 밀리초). Orderbook = 호가 필드 · Trade = 체결 필드만 유효.</summary>
    public readonly record struct UpbitStreamFrame(UpbitStreamKind Kind, long TimestampMs, long BidRaw, long AskRaw, long BidSizeRaw, long AskSizeRaw,
                                                   long TradePriceRaw, long TradeVolumeRaw, UpbitTradeSide TradeSide, long SequentialId);

    /// <summary>업비트 WebSocket 프레임 무할당 파서(δ=0 · fail-closed) + 송출 페이로드 2종 + 접속 대상 가드.</summary>
    public static class UpbitStreamProtocol
    {
        public const string EndpointText = "wss://api.upbit.com/websocket/v1";
        public static readonly Uri Endpoint = new(EndpointText);
        public static ReadOnlySpan<byte> MarketKrwBtc => "KRW-BTC"u8;
        public const int MaxDepth = 8;
        public const long MaxTimestampMs = long.MaxValue / 1000;           // × 1000(마이크로초) 오버플로 없는 상한

        private static readonly byte[] s_subscribe =
            "[{\"ticket\":\"SM-UPBIT-STREAM\"},{\"type\":\"orderbook\",\"codes\":[\"KRW-BTC\"]},{\"type\":\"trade\",\"codes\":[\"KRW-BTC\"]}]"u8.ToArray();
        private static readonly byte[] s_ping = "PING"u8.ToArray();

        public static ReadOnlyMemory<byte> SubscribePayload => s_subscribe;
        public static ReadOnlyMemory<byte> PingPayload => s_ping;

        /// <summary>접속 대상 = Endpoint 정확 일치만(정규화 AbsoluteUri 서수 비교 — 사용자정보 · 쿼리 · 조각 · 다른 포트 = 불일치). 위반 = 예외(연결 전).</summary>
        public static void EnsureEndpoint(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);
            if (!uri.IsAbsoluteUri || !string.Equals(uri.AbsoluteUri, EndpointText, StringComparison.Ordinal))
                throw new ArgumentException("업비트 공개 WebSocket 외 접속 대상 거부", nameof(uri));
        }

        /// <summary>프레임 1개 판독. 반환 = 종류(Orderbook/Trade 일 때만 f 유효 · 그 외 f = default).</summary>
        public static UpbitStreamKind TryParse(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> market, out UpbitStreamFrame f)
            => Parse(frame, market, null, out _, out f);

        /// <summary>다중 마켓 판독. marketIndex = 표 인덱스(Orderbook/Trade 일 때만 ≥ 0 · 그 외 −1).</summary>
        public static UpbitStreamKind TryParse(ReadOnlySpan<byte> frame, UpbitMarketSet markets, out int marketIndex, out UpbitStreamFrame f)
        {
            ArgumentNullException.ThrowIfNull(markets);
            return Parse(frame, default, markets, out marketIndex, out f);
        }

        private static UpbitStreamKind Parse(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> market, UpbitMarketSet? set, out int marketIndex, out UpbitStreamFrame f)
        {
            f = default;
            marketIndex = -1;
            int idx = -1;
            const byte TypeNone = 0, TypeOrderbook = 1, TypeTrade = 2, TypeOther = 3;
            byte type = TypeNone;
            bool codeSeen = false, codeOk = false, tsSeen = false, unitsSeen = false, statusSeen = false, errorSeen = false;
            bool askSeen = false, bidSeen = false, askSzSeen = false, bidSzSeen = false;
            bool tpSeen = false, tvSeen = false, ttsSeen = false, sideSeen = false, seqSeen = false;
            long ts = 0, bid = 0, ask = 0, bidSz = 0, askSz = 0, tp = 0, tv = 0, tts = 0, seq = 0;
            UpbitTradeSide side = UpbitTradeSide.None;
            int units = 0;
            try
            {
                // isFinalBlock = false: 잘린 프레임 = Read() false(예외 · 할당 0) → depth ≠ 0 = Invalid. 루트 종료 즉시 판독 중단 → 잔여 = 공백만 허용.
                var r = new Utf8JsonReader(frame, isFinalBlock: false, new JsonReaderState(new JsonReaderOptions { MaxDepth = MaxDepth }));
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return UpbitStreamKind.Invalid;
                int depth = 1; bool inUnits = false;
                while (depth > 0 && r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.StartObject:
                            depth++;
                            if (inUnits && depth == 3) units++;
                            break;
                        case JsonTokenType.StartArray: depth++; break;
                        case JsonTokenType.EndObject: depth--; break;
                        case JsonTokenType.EndArray:
                            if (inUnits && depth == 2) inUnits = false;
                            depth--; break;
                        case JsonTokenType.PropertyName:
                            if (depth == 1)
                            {
                                if (r.ValueTextEquals("type"u8))
                                {
                                    if (type != TypeNone || !r.Read() || r.TokenType != JsonTokenType.String) return UpbitStreamKind.Invalid;
                                    type = r.ValueTextEquals("orderbook"u8) ? TypeOrderbook : r.ValueTextEquals("trade"u8) ? TypeTrade : TypeOther;
                                }
                                else if (r.ValueTextEquals("code"u8))
                                {
                                    if (codeSeen || !r.Read() || r.TokenType != JsonTokenType.String) return UpbitStreamKind.Invalid;
                                    codeSeen = true; idx = set is null ? (r.ValueTextEquals(market) ? 0 : -1) : set.IndexOf(ref r); codeOk = idx >= 0;
                                }
                                else if (r.ValueTextEquals("timestamp"u8)) { if (tsSeen || !ReadMillis(ref r, out ts)) return UpbitStreamKind.Invalid; tsSeen = true; }
                                else if (r.ValueTextEquals("orderbook_units"u8))
                                {
                                    if (unitsSeen || !r.Read() || r.TokenType != JsonTokenType.StartArray) return UpbitStreamKind.Invalid;
                                    unitsSeen = true; inUnits = true; depth++;
                                }
                                else if (r.ValueTextEquals("trade_price"u8)) { if (tpSeen || !ReadRaw(ref r, out tp)) return UpbitStreamKind.Invalid; tpSeen = true; }
                                else if (r.ValueTextEquals("trade_volume"u8)) { if (tvSeen || !ReadRaw(ref r, out tv)) return UpbitStreamKind.Invalid; tvSeen = true; }
                                else if (r.ValueTextEquals("trade_timestamp"u8)) { if (ttsSeen || !ReadMillis(ref r, out tts)) return UpbitStreamKind.Invalid; ttsSeen = true; }
                                else if (r.ValueTextEquals("ask_bid"u8))
                                {
                                    if (sideSeen || !r.Read() || r.TokenType != JsonTokenType.String) return UpbitStreamKind.Invalid;
                                    side = r.ValueTextEquals("BID"u8) ? UpbitTradeSide.Bid : r.ValueTextEquals("ASK"u8) ? UpbitTradeSide.Ask : UpbitTradeSide.None;
                                    if (side == UpbitTradeSide.None) return UpbitStreamKind.Invalid;
                                    sideSeen = true;
                                }
                                else if (r.ValueTextEquals("sequential_id"u8))
                                {
                                    if (seqSeen || !r.Read() || r.TokenType != JsonTokenType.Number || !r.TryGetInt64(out seq) || seq < 0) return UpbitStreamKind.Invalid;
                                    seqSeen = true;
                                }
                                else if (r.ValueTextEquals("status"u8)) { if (statusSeen) return UpbitStreamKind.Invalid; statusSeen = true; }
                                else if (r.ValueTextEquals("error"u8)) { if (errorSeen) return UpbitStreamKind.Invalid; errorSeen = true; }
                            }
                            else if (depth == 3 && inUnits && units == 1)                        // 최우선 단(첫 원소)만
                            {
                                if (r.ValueTextEquals("ask_price"u8)) { if (askSeen || !ReadRaw(ref r, out ask)) return UpbitStreamKind.Invalid; askSeen = true; }
                                else if (r.ValueTextEquals("bid_price"u8)) { if (bidSeen || !ReadRaw(ref r, out bid)) return UpbitStreamKind.Invalid; bidSeen = true; }
                                else if (r.ValueTextEquals("ask_size"u8)) { if (askSzSeen || !ReadRaw(ref r, out askSz)) return UpbitStreamKind.Invalid; askSzSeen = true; }
                                else if (r.ValueTextEquals("bid_size"u8)) { if (bidSzSeen || !ReadRaw(ref r, out bidSz)) return UpbitStreamKind.Invalid; bidSzSeen = true; }
                            }
                            break;
                    }
                }
                if (depth != 0 || !IsWhitespace(frame.Slice((int)r.BytesConsumed))) return UpbitStreamKind.Invalid;
            }
            catch (JsonException) { return UpbitStreamKind.Invalid; }                        // 어휘 오류(잘못된 토큰 · 비 UTF-8 · 깊이 초과) — 이상 프레임 한정 경로

            if (errorSeen) return type == TypeNone && !statusSeen ? UpbitStreamKind.Error : UpbitStreamKind.Invalid;
            if (statusSeen) return type == TypeNone ? UpbitStreamKind.Status : UpbitStreamKind.Invalid;
            if (type == TypeNone) return UpbitStreamKind.Invalid;
            if (type == TypeOther) return UpbitStreamKind.Unsupported;
            if (!codeOk) return UpbitStreamKind.Invalid;
            if (type == TypeOrderbook)
            {
                if (!tsSeen || units < 1 || !askSeen || !bidSeen || !askSzSeen || !bidSzSeen || bid <= 0 || ask <= 0 || bid >= ask) return UpbitStreamKind.Invalid;
                f = new UpbitStreamFrame(UpbitStreamKind.Orderbook, ts, bid, ask, bidSz, askSz, 0, 0, UpbitTradeSide.None, 0);
                marketIndex = idx;
                return UpbitStreamKind.Orderbook;
            }
            if (!tpSeen || !tvSeen || !ttsSeen || !sideSeen || tp <= 0 || tv <= 0) return UpbitStreamKind.Invalid;
            f = new UpbitStreamFrame(UpbitStreamKind.Trade, tts, 0, 0, 0, 0, tp, tv, side, seq);
            marketIndex = idx;
            return UpbitStreamKind.Trade;
        }

        private static bool IsWhitespace(ReadOnlySpan<byte> rest)
        {
            foreach (byte b in rest) if (b != 0x20 && b != 0x09 && b != 0x0D && b != 0x0A) return false;     // JSON 공백 4종
            return true;
        }

        private static readonly decimal RawLimit = (decimal)long.MaxValue / UpbitTickAligner.Scale;

        /// <summary>다음 토큰(숫자) → raw 10^8(음수 · 범위 초과 = false · 소수 8자리 초과 = 내림).</summary>
        private static bool ReadRaw(ref Utf8JsonReader r, out long raw)
        {
            raw = 0;
            if (!r.Read() || r.TokenType != JsonTokenType.Number || !r.TryGetDecimal(out decimal d)) return false;
            if (d < 0m || d >= RawLimit) return false;                                          // 곱셈 전 차단(decimal 예외 0)
            raw = (long)decimal.Truncate(d * UpbitTickAligner.Scale);
            return true;
        }

        private static bool ReadMillis(ref Utf8JsonReader r, out long ms)
        {
            ms = 0;
            return r.Read() && r.TokenType == JsonTokenType.Number && r.TryGetInt64(out ms) && ms > 0 && ms <= MaxTimestampMs;
        }
    }

    /// <summary>
    /// 구독 마켓 표(콜드 생성 · 불변). 코드 = ASCII 대문자/숫자 + '-' 1개(앞 1~8 · 뒤 1~10자 · 예 "KRW-BTC") · 1~8개 · 중복 = 예외.
    /// 인덱스 = 생성 순서(구독 페이로드 codes 순서 동일). 판독 = 표 순차 ValueTextEquals(할당 0).
    /// </summary>
    public sealed class UpbitMarketSet
    {
        public const int MaxMarkets = 8;
        private readonly byte[][] _codes;
        private readonly string[] _markets;
        private readonly byte[] _subscribe;

        /// <summary>KRW-BTC 단독(기본 · 페이로드 = UpbitStreamProtocol.SubscribePayload 바이트 동일).</summary>
        public static UpbitMarketSet KrwBtc { get; } = new("KRW-BTC");

        public UpbitMarketSet(params string[] markets)
        {
            ArgumentNullException.ThrowIfNull(markets);
            if (markets.Length is < 1 or > MaxMarkets) throw new ArgumentOutOfRangeException(nameof(markets), "마켓 1~8개");
            _markets = new string[markets.Length];
            _codes = new byte[markets.Length][];
            for (int i = 0; i < markets.Length; i++)
            {
                string m = markets[i] ?? throw new ArgumentNullException(nameof(markets));
                if (!IsValidCode(m)) throw new ArgumentException("업비트 마켓 코드 형식 위반: " + m, nameof(markets));
                for (int j = 0; j < i; j++)
                    if (string.Equals(_markets[j], m, StringComparison.Ordinal)) throw new ArgumentException("중복 마켓: " + m, nameof(markets));
                _markets[i] = m;
                _codes[i] = System.Text.Encoding.ASCII.GetBytes(m);
            }
            string codes = "\"" + string.Join("\",\"", _markets) + "\"";      // 형식 검증 통과 = 따옴표/역슬래시 없음(주입 0)
            _subscribe = System.Text.Encoding.ASCII.GetBytes(
                "[{\"ticket\":\"SM-UPBIT-STREAM\"},{\"type\":\"orderbook\",\"codes\":[" + codes + "]},{\"type\":\"trade\",\"codes\":[" + codes + "]}]");
        }

        public int Count => _codes.Length;
        public string Market(int index) => _markets[index];
        public ReadOnlyMemory<byte> SubscribePayload => _subscribe;

        /// <summary>현재 문자열 토큰 → 표 인덱스(표 밖 = −1 · δ=0).</summary>
        internal int IndexOf(ref Utf8JsonReader r)
        {
            for (int i = 0; i < _codes.Length; i++) if (r.ValueTextEquals(_codes[i])) return i;
            return -1;
        }

        public static bool IsValidCode(string? code)
        {
            if (code is null) return false;
            int dash = code.IndexOf('-');
            if (dash < 1 || dash > 8 || dash != code.LastIndexOf('-') || code.Length - dash - 1 is < 1 or > 10) return false;
            foreach (char ch in code)
                if (ch != '-' && !(ch >= 'A' && ch <= 'Z') && !(ch >= '0' && ch <= '9')) return false;
            return true;
        }
    }

    public enum UpbitPublishResult : byte { Accepted = 1, OutOfOrder = 2, Contended = 3, WrongKind = 4 }

    /// <summary>최신 L1 호가 + 마지막 체결(seqlock 일관 판독 — 모든 필드가 같은 기입 세대).</summary>
    public readonly record struct UpbitMarketSnapshot(long BidRaw, long AskRaw, long BidSizeRaw, long AskSizeRaw, long QuoteTimestampMs,
                                                      long LastTradePriceRaw, long LastTradeVolumeRaw, UpbitTradeSide LastTradeSide,
                                                      long LastTradeSequentialId, long LastTradeTimestampMs, long QuoteCount, long TradeCount);

    /// <summary>업비트 시장 상태(단일 라이터 CAS 진입권 · seqlock 판독 · 락 0 · 할당 0).</summary>
    public sealed class UpbitMarketState
    {
        private UpbitMarketSnapshot _state;
        private long _seq;          // seqlock(짝=안정, 홀=기입 중)
        private int _writer;        // 라이터 진입권(0 = 비어 있음)

        public UpbitMarketSnapshot Snapshot()
        {
            SpinWait sw = default;
            while (true)
            {
                long s1 = Volatile.Read(ref _seq);
                if ((s1 & 1L) != 0L) { sw.SpinOnce(); continue; }
                UpbitMarketSnapshot snap = _state;
                Interlocked.MemoryBarrier();                             // 복사가 s2 판독 뒤로 밀리지 않게(CryptoPaperExchange.Snapshot 동일)
                long s2 = Volatile.Read(ref _seq);
                if (s1 == s2) return snap;
                sw.SpinOnce();
            }
        }

        /// <summary>호가 투영. 시각 역행(직전 호가보다 이른 프레임) = OutOfOrder · 같은 시각 = 수용(재접속 SNAPSHOT 재전송).</summary>
        public UpbitPublishResult TryPublishQuote(in UpbitStreamFrame f)
        {
            if (f.Kind != UpbitStreamKind.Orderbook) return UpbitPublishResult.WrongKind;
            if (Interlocked.CompareExchange(ref _writer, 1, 0) != 0) return UpbitPublishResult.Contended;
            try
            {
                var s = _state;
                if (f.TimestampMs < s.QuoteTimestampMs) return UpbitPublishResult.OutOfOrder;
                Publish(s with { BidRaw = f.BidRaw, AskRaw = f.AskRaw, BidSizeRaw = f.BidSizeRaw, AskSizeRaw = f.AskSizeRaw, QuoteTimestampMs = f.TimestampMs, QuoteCount = s.QuoteCount + 1 });
                return UpbitPublishResult.Accepted;
            }
            finally { Volatile.Write(ref _writer, 0); }
        }

        /// <summary>체결 투영. 시각 역행 · 같은 시각 같은 sequential_id(≠0 · 재전송) = OutOfOrder.</summary>
        public UpbitPublishResult TryPublishTrade(in UpbitStreamFrame f)
        {
            if (f.Kind != UpbitStreamKind.Trade) return UpbitPublishResult.WrongKind;
            if (Interlocked.CompareExchange(ref _writer, 1, 0) != 0) return UpbitPublishResult.Contended;
            try
            {
                var s = _state;
                if (f.TimestampMs < s.LastTradeTimestampMs
                    || (f.TimestampMs == s.LastTradeTimestampMs && f.SequentialId != 0 && f.SequentialId == s.LastTradeSequentialId)) return UpbitPublishResult.OutOfOrder;
                Publish(s with { LastTradePriceRaw = f.TradePriceRaw, LastTradeVolumeRaw = f.TradeVolumeRaw, LastTradeSide = f.TradeSide,
                                 LastTradeSequentialId = f.SequentialId, LastTradeTimestampMs = f.TimestampMs, TradeCount = s.TradeCount + 1 });
                return UpbitPublishResult.Accepted;
            }
            finally { Volatile.Write(ref _writer, 0); }
        }

        private void Publish(in UpbitMarketSnapshot next)
        {
            Interlocked.Increment(ref _seq);    // → 홀(기입 중)
            _state = next;
            Interlocked.Increment(ref _seq);    // → 짝(안정)
        }
    }

    /// <summary>WebSocket 전송 추상(테스트 대역 주입용 · IKisWsTransport 와 같은 형태).</summary>
    public interface IUpbitWsTransport : IDisposable
    {
        Task ConnectAsync(Uri uri, CancellationToken ct);
        Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
        /// <summary>(받은 바이트 수, 메시지 끝, 닫힘).</summary>
        ValueTask<(int Count, bool EndOfMessage, bool Closed)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
    }

    public sealed class UpbitClientWebSocketTransport : IUpbitWsTransport
    {
        private readonly ClientWebSocket _ws = new();

        public Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            UpbitStreamProtocol.EnsureEndpoint(uri);                                     // 연결 전 재검(패킷 0)
            return _ws.ConnectAsync(uri, ct);
        }

        public Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => _ws.SendAsync(payload, WebSocketMessageType.Text, true, ct).AsTask();

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<(int Count, bool EndOfMessage, bool Closed)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var r = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            return (r.Count, r.EndOfMessage, r.MessageType == WebSocketMessageType.Close);
        }

        public void Dispose() => _ws.Dispose();
    }

    /// <summary>연결 관리 설정(콜드). KeepAliveTick ≤ 0 = 유지 루프 끔(테스트 대역 전용).</summary>
    public sealed record UpbitFeedOptions
    {
        public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);
        public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(30);
        public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public TimeSpan KeepAliveTick { get; init; } = TimeSpan.FromSeconds(1);
        public TimeSpan SilenceTimeout { get; init; } = TimeSpan.FromSeconds(10);
        public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(60);
        /// <summary>[v12.18.74-DEV] 구독 마켓 표(기본 = KRW-BTC 단독). 인덱스 0 = 주 마켓(거래소 투영 · 기존 tap 대상).</summary>
        public UpbitMarketSet Markets { get; init; } = UpbitMarketSet.KrwBtc;
    }

    /// <summary>판독 직후 관찰 지점(Orderbook/Trade 만 · 수신 스레드 동기 호출 · 무할당 구현 책임 = 호출자).</summary>
    public delegate void UpbitFrameTap(in UpbitStreamFrame frame);

    /// <summary>[v12.18.74-DEV] 다중 마켓 관찰 지점 — 시장 상태가 수용한 Orderbook/Trade 만(재전송 · 시각 역행 = 통지 0) · 수신 스레드 동기 호출.</summary>
    public delegate void UpbitMarketFrameTap(int marketIndex, in UpbitStreamFrame frame);

    public sealed class UpbitWebSocketFeedAdapter
    {
        public const int RxBufferBytes = 64 * 1024;

        private readonly UpbitMarketState _state;
        private readonly UpbitMarketSet _markets;
        private readonly UpbitMarketState[] _states;                                   // 마켓별(인덱스 0 = _state)
        private readonly long[] _marketQuotes, _marketTrades;                          // 마켓별 수용 계수
        private readonly CryptoPaperExchange? _exchange;
        private readonly Func<IUpbitWsTransport> _factory;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly UpbitFeedOptions _opt;
        private readonly UpbitFrameTap? _tap;
        private readonly UpbitMarketFrameTap? _marketTap;
        private readonly byte[] _rx = GC.AllocateArray<byte>(RxBufferBytes, pinned: true);
        private CancellationTokenSource? _conn;
        private int _connected;
        private long _lastFrameTicks;
        private long _framesIn, _quotesFed, _quotesDropped, _quotesRejected, _trades, _tradesDropped, _status, _errors, _unsupported, _malformed, _oversized;
        private long _connects, _disconnects, _faults, _aborts, _silenceAborts, _pings, _backoffWaits;

        /// <param name="exchange">null = 시장 상태만 투영.</param>
        /// <param name="factory">null = UpbitClientWebSocketTransport.</param>
        /// <param name="delay">백오프 대기(null = Task.Delay · 테스트 = 기록형).</param>
        public UpbitWebSocketFeedAdapter(UpbitMarketState state, CryptoPaperExchange? exchange = null, Func<IUpbitWsTransport>? factory = null,
                                         UpbitFeedOptions? options = null, Func<TimeSpan, CancellationToken, Task>? delay = null, UpbitFrameTap? tap = null,
                                         UpbitMarketFrameTap? marketTap = null)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _exchange = exchange;
            _factory = factory ?? (static () => new UpbitClientWebSocketTransport());
            _opt = options ?? new UpbitFeedOptions();
            if (_opt.InitialBackoff <= TimeSpan.Zero || _opt.MaxBackoff < _opt.InitialBackoff) throw new ArgumentOutOfRangeException(nameof(options));
            if (_opt.ConnectTimeout <= TimeSpan.Zero || _opt.SilenceTimeout <= TimeSpan.Zero || _opt.PingInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
            _markets = _opt.Markets ?? throw new ArgumentOutOfRangeException(nameof(options));
            _states = new UpbitMarketState[_markets.Count];
            _states[0] = _state;
            for (int i = 1; i < _states.Length; i++) _states[i] = new UpbitMarketState();
            _marketQuotes = new long[_markets.Count];
            _marketTrades = new long[_markets.Count];
            _delay = delay ?? Task.Delay;
            _tap = tap;
            _marketTap = marketTap;
        }

        public UpbitMarketState State => _state;
        public UpbitMarketSet Markets => _markets;
        /// <summary>마켓별 시장 상태(인덱스 = Markets 순서 · 0 = State).</summary>
        public UpbitMarketState StateOf(int marketIndex) => _states[marketIndex];
        public long QuotesOf(int marketIndex) => Interlocked.Read(ref _marketQuotes[marketIndex]);   // 시장 상태 수용 호가
        public long TradesOf(int marketIndex) => Interlocked.Read(ref _marketTrades[marketIndex]);   // 시장 상태 수용 체결
        public bool IsConnected => Volatile.Read(ref _connected) != 0;
        public long FramesIn => Interlocked.Read(ref _framesIn);
        public long QuotesFed => Interlocked.Read(ref _quotesFed);              // 시장 상태 + 거래소 반영
        public long QuotesDropped => Interlocked.Read(ref _quotesDropped);      // 시각 역행 · 상태 경합
        public long QuotesRejected => Interlocked.Read(ref _quotesRejected);    // 거래소 거부(비정렬 · 경합)
        public long Trades => Interlocked.Read(ref _trades);
        public long TradesDropped => Interlocked.Read(ref _tradesDropped);
        public long StatusFrames => Interlocked.Read(ref _status);
        public long ErrorFrames => Interlocked.Read(ref _errors);
        public long Unsupported => Interlocked.Read(ref _unsupported);
        public long Malformed => Interlocked.Read(ref _malformed);
        public long Oversized => Interlocked.Read(ref _oversized);
        public long Connects => Interlocked.Read(ref _connects);
        public long Disconnects => Interlocked.Read(ref _disconnects);
        public long Faults => Interlocked.Read(ref _faults);
        public long Aborts => Interlocked.Read(ref _aborts);
        public long SilenceAborts => Interlocked.Read(ref _silenceAborts);
        public long Pings => Interlocked.Read(ref _pings);
        public long BackoffWaits => Interlocked.Read(ref _backoffWaits);

        /// <summary>완성 메시지 1개 처리(δ=0) — 판독 → 시장 상태 → 거래소 호가. 어떤 입력도 예외를 내지 않는다.</summary>
        public UpbitStreamKind OnFrame(ReadOnlySpan<byte> frame)
        {
            Interlocked.Increment(ref _framesIn);
            var kind = UpbitStreamProtocol.TryParse(frame, _markets, out int m, out var f);
            bool accepted = false;
            switch (kind)
            {
                case UpbitStreamKind.Orderbook:
                    if (_states[m].TryPublishQuote(in f) != UpbitPublishResult.Accepted) { Interlocked.Increment(ref _quotesDropped); break; }
                    accepted = true;
                    Interlocked.Increment(ref _marketQuotes[m]);
                    if (m != 0 || _exchange is null || _exchange.UpdateQuote(f.BidRaw, f.AskRaw, f.TimestampMs * 1000)) Interlocked.Increment(ref _quotesFed);
                    else Interlocked.Increment(ref _quotesRejected);
                    break;
                case UpbitStreamKind.Trade:
                    if (_states[m].TryPublishTrade(in f) == UpbitPublishResult.Accepted)
                    {
                        accepted = true;
                        Interlocked.Increment(ref _trades);
                        Interlocked.Increment(ref _marketTrades[m]);
                    }
                    else Interlocked.Increment(ref _tradesDropped);
                    break;
                case UpbitStreamKind.Status: Interlocked.Increment(ref _status); break;
                case UpbitStreamKind.Error: Interlocked.Increment(ref _errors); break;
                case UpbitStreamKind.Unsupported: Interlocked.Increment(ref _unsupported); break;
                default: Interlocked.Increment(ref _malformed); break;
            }
            if (_tap is not null && m == 0 && (kind == UpbitStreamKind.Orderbook || kind == UpbitStreamKind.Trade)) _tap(in f);
            if (accepted && _marketTap is not null) _marketTap(m, in f);
            return kind;
        }

        /// <summary>수신 루프(연결 1개) — 닫힘 수신 시 반환. 조각 조립 · 버퍼 초과 메시지 폐기 · 프레임당 힙 0.</summary>
        public async Task ReceiveLoopAsync(IUpbitWsTransport t, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(t);
            int len = 0; bool dropping = false;
            while (!ct.IsCancellationRequested)
            {
                if (len == _rx.Length)                                                     // 버퍼 초과 — 메시지 끝까지 폐기(집계 = 메시지당 1)
                {
                    if (!dropping) Interlocked.Increment(ref _oversized);
                    dropping = true; len = 0;
                }
                var (count, end, closed) = await t.ReceiveAsync(_rx.AsMemory(len), ct).ConfigureAwait(false);
                if (closed) return;
                len += count;
                if (!end) continue;
                Volatile.Write(ref _lastFrameTicks, Environment.TickCount64);
                if (!dropping) OnFrame(_rx.AsSpan(0, len));
                dropping = false; len = 0;
            }
        }

        /// <summary>현재 연결 강제 종료(침묵 단절 · 외부 감시) → 백오프 재접속. 연결 없음 = 무동작.</summary>
        public void AbortConnection()
        {
            var c = Volatile.Read(ref _conn);
            if (c is null) return;
            try { c.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>연결 · 구독 · 수신 · 재접속(지수 백오프) 루프. 취소 토큰으로 종료 · 예외 미전파.</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            TimeSpan backoff = _opt.InitialBackoff;
            bool first = true;
            while (!ct.IsCancellationRequested)
            {
                if (!first)
                {
                    Interlocked.Increment(ref _backoffWaits);
                    try { await _delay(backoff, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    long doubled = Math.Min(backoff.Ticks * 2, _opt.MaxBackoff.Ticks);
                    backoff = TimeSpan.FromTicks(doubled);
                }
                first = false;
                if (ct.IsCancellationRequested) break;

                IUpbitWsTransport t;
                try { t = _factory(); }
                catch (Exception) { Interlocked.Increment(ref _faults); continue; }

                using var conn = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Volatile.Write(ref _conn, conn);
                long framesBefore = FramesIn;
                bool connected = false;
                Task? keep = null;
                try
                {
                    using (var connectTo = CancellationTokenSource.CreateLinkedTokenSource(conn.Token))
                    {
                        connectTo.CancelAfter(_opt.ConnectTimeout);
                        await t.ConnectAsync(UpbitStreamProtocol.Endpoint, connectTo.Token).ConfigureAwait(false);
                    }
                    await t.SendTextAsync(_markets.SubscribePayload, conn.Token).ConfigureAwait(false);
                    connected = true;
                    Interlocked.Increment(ref _connects);
                    Volatile.Write(ref _lastFrameTicks, Environment.TickCount64);
                    Volatile.Write(ref _connected, 1);
                    if (_opt.KeepAliveTick > TimeSpan.Zero) keep = KeepAliveAsync(t, conn);
                    await ReceiveLoopAsync(t, conn.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { if (!ct.IsCancellationRequested) Interlocked.Increment(ref _aborts); }   // 침묵 단절 · 접속 제한 시간 · 외부 중단
                catch (Exception) { Interlocked.Increment(ref _faults); }
                finally
                {
                    Volatile.Write(ref _connected, 0);
                    Volatile.Write(ref _conn, null);
                    try { conn.Cancel(); } catch (ObjectDisposedException) { }
                    if (keep is not null) { try { await keep.ConfigureAwait(false); } catch (Exception) { } }
                    try { t.Dispose(); } catch (Exception) { }
                    if (connected) Interlocked.Increment(ref _disconnects);
                }
                if (FramesIn > framesBefore) backoff = _opt.InitialBackoff;                   // 수신 실적 있는 연결 = 백오프 초기화
            }
        }

        // 유지 루프(연결당 1개 · 콜드): 침묵 단절 판정 + PING. 구독 송신 완료 후에만 시작 → 송신 주체 단독.
        private async Task KeepAliveAsync(IUpbitWsTransport t, CancellationTokenSource conn)
        {
            long silenceMs = (long)_opt.SilenceTimeout.TotalMilliseconds, pingMs = (long)_opt.PingInterval.TotalMilliseconds;
            long lastPing = Environment.TickCount64;
            try
            {
                var tk = conn.Token;
                while (!tk.IsCancellationRequested)
                {
                    await Task.Delay(_opt.KeepAliveTick, tk).ConfigureAwait(false);
                    long now = Environment.TickCount64;
                    if (now - Volatile.Read(ref _lastFrameTicks) > silenceMs)
                    {
                        Interlocked.Increment(ref _silenceAborts);
                        conn.Cancel();
                        return;
                    }
                    if (now - lastPing >= pingMs)
                    {
                        await t.SendTextAsync(UpbitStreamProtocol.PingPayload, tk).ConfigureAwait(false);
                        Interlocked.Increment(ref _pings);
                        lastPing = now;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                Interlocked.Increment(ref _faults);
                try { conn.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
    }
}
