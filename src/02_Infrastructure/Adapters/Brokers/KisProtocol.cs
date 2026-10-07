// ============================================================================
// KisProtocol — [KIS · 추천 A] 한국투자증권 OpenAPI REST/WebSocket 무할당(δ=0) 프로토콜 판독기
//
//  ★출처·한계(정직): 필드 순서·TR ID·응답 키는 KIS Developers 공개 문서(국내주식 실시간체결가 H0STCNT0 · 실시간체결통보 H0STCNI0 ·
//    접근토큰 /oauth2/tokenP · 현금주문 order-cash)의 알려진 형식 기준이다. 이 저장소에는 실 KIS 계정·네트워크 검증 이력이 없다 —
//    실 연동 전 모의(VTS) 도메인에서 프레임 1건씩 대조 필수. 형식 이탈은 전부 false(fail-closed) — 추측 채움 0.
//
//  ① 실시간 프레임: "<암호화 0|1>|<TR_ID>|<건수 3자리>|<본문>" — 본문은 '^' 구분 필드, 건수 > 1 이면 레코드가 연속(레코드당 필드 수 고정).
//  ② 제어 프레임: '{' 로 시작하는 JSON — PINGPONG(header.tr_id) 은 받은 그대로 되돌려 보내야 세션 유지.
//  ③ 체결통보(H0STCNI0)는 AES-256-CBC 암호문(Base64) — 구독 응답의 key(32B)/iv(16B)로 복호(BCL Aes.TryDecryptCbc · 힙 0).
//  ④ REST: rt_cd "0" = 성공 · output.ODNO(주문번호) · output.KRX_FWDG_ORD_ORGNO(한국거래소전송주문조직번호 — 정정/취소에 필요).
//  ⑤ 토큰: access_token · expires_in(초) · access_token_token_expired("yyyy-MM-dd HH:mm:ss" KST).
//  δ=0: 전부 ReadOnlySpan<byte> 인덱스 스캔 · Utf8Parser · Utf8JsonReader 타입드 게터(예외는 JsonException 만 catch).
// ============================================================================
using System;
using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace MS2E.Infrastructure
{
    /// <summary>H0STCNT0 체결가 1레코드(원 · 주).</summary>
    public struct KisTick
    {
        public long SymbolCode, TimeHhmmss, Price, TradeVolume, AccVolume;
    }

    public enum KisNoticeKind : byte { Unknown = 0, Accepted = 1, Filled = 2, Rejected = 3, CancelConfirmed = 4, Amended = 5 }

    /// <summary>H0STCNI0 체결통보 1건(복호 후).</summary>
    public struct KisFillNotice
    {
        public long OrderNo, OrigOrderNo, SymbolCode, FillQty, FillPrice, OrderQty, TimeHhmmss;
        public int Side;                 // +1 매수 · −1 매도
        public KisNoticeKind Kind;
    }

    /// <summary>[KIS 잔고] TTTC8434R output1 1행 — 종목 · 보유수량(hldg_qty) · 주문가능수량(ord_psbl_qty).</summary>
    public struct KisHolding
    {
        public long SymbolCode, HeldQty, SellableQty;
    }

    /// <summary>[KIS VTS] TR 도메인 — 실(TTTC…/H0STCNI0) · 모의(VTTC…/H0STCNI9) · 중립(시세 H0STCNT0 · PINGPONG) · 미상.</summary>
    public enum KisTrDomain : byte { Unknown = 0, Neutral = 1, Real = 2, Vts = 3 }

    public struct KisOrderAck
    {
        public bool Ok;
        public long OrderNo;             // ODNO
        public long OrgNo;               // KRX_FWDG_ORD_ORGNO(숫자) — 정정/취소 시 원 조직번호
    }

    /// <summary>
    /// [KIS] 체결통보(H0STCNI0) AES-256-CBC 복호기(δ=0). BCL 원샷 Aes.TryDecryptCbc 는 호출마다 내부 객체를 할당한다(실측 184B/건) —
    /// 키 스케줄을 담은 ECB 블록 복호기를 1회 만들어 두고 CBC 체인(평문 = D(C_i) XOR C_{i-1}, C_0 = IV)과 PKCS7 검증을 직접 수행.
    /// 단일 수신 스레드 전용(작업 블록 공유).
    /// </summary>
    public sealed class KisNoticeCipher : IDisposable
    {
        private readonly Aes _aes;
        private readonly ICryptoTransform _ecb;
        private readonly byte[] _iv = new byte[16], _in = new byte[16], _out = new byte[16], _prev = new byte[16];

        public KisNoticeCipher(ReadOnlySpan<byte> key32, ReadOnlySpan<byte> iv16)
        {
            if (key32.Length != 32 || iv16.Length != 16) throw new ArgumentException("KIS 체결통보 키 32B · IV 16B");
            _aes = Aes.Create();
            _aes.Key = key32.ToArray();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _ecb = _aes.CreateDecryptor();
            iv16.CopyTo(_iv);
        }

        /// <summary>Base64 암호문 → 평문(호출측 버퍼). 반환 = 평문 길이(실패 −1 · 패딩 위반 포함).</summary>
        public int TryDecrypt(ReadOnlySpan<byte> base64Cipher, Span<byte> cipherWork, Span<byte> plain)
        {
            if (Base64.DecodeFromUtf8(base64Cipher, cipherWork, out _, out int written) != OperationStatus.Done || written == 0 || written % 16 != 0 || written > plain.Length) return -1;
            _iv.AsSpan().CopyTo(_prev);
            for (int off = 0; off < written; off += 16)
            {
                cipherWork.Slice(off, 16).CopyTo(_in);
                if (_ecb.TransformBlock(_in, 0, 16, _out, 0) != 16) return -1;
                for (int k = 0; k < 16; k++) plain[off + k] = (byte)(_out[k] ^ _prev[k]);
                _in.AsSpan().CopyTo(_prev);
            }
            int pad = plain[written - 1];
            if (pad is < 1 or > 16) return -1;
            for (int k = written - pad; k < written; k++) if (plain[k] != pad) return -1;
            return written - pad;
        }

        public void Dispose()
        {
            _ecb.Dispose();
            _aes.Dispose();
            CryptographicOperations.ZeroMemory(_iv);
        }
    }

    /// <summary>
    /// [다중 브로커] KIS 주문번호 네임스페이스 — OrderExecutionFeedAdapter 는 '브로커 주문번호' 하나로 슬롯을 찾는데 키움(호스트 발급
    /// 클라이언트 ID)과 KIS(ODNO 10자리)가 같은 숫자를 낼 수 있다 → KIS 번호는 비트 48 을 세워 사상(키움 ID 는 2^48 미만 전제).
    /// </summary>
    public static class KisOrderIdSpace
    {
        public const long Namespace = 1L << 48;
        public static long Encode(long odno) => odno is > 0 and < Namespace ? Namespace | odno : 0;
        public static bool IsKis(long brokerOrderId) => brokerOrderId > Namespace && (brokerOrderId & ~(Namespace - 1)) == Namespace;
        public static long Decode(long brokerOrderId) => IsKis(brokerOrderId) ? brokerOrderId & (Namespace - 1) : 0;
    }

    public static class KisProtocol
    {
        public const int H0STCNT0FieldCount = 46;   // 레코드당 필드 수(공개 문서 기준 — 다건 프레임 보폭)
        public const int MaxRecordsPerFrame = 64;
        private static ReadOnlySpan<byte> TrTick => "H0STCNT0"u8;
        private static ReadOnlySpan<byte> TrNotice => "H0STCNI0"u8;
        private static ReadOnlySpan<byte> TrNoticeVts => "H0STCNI9"u8;   // 모의투자 체결통보
        private static ReadOnlySpan<byte> PingPong => "PINGPONG"u8;

        // ── ① 실시간 프레임 헤더 ──

        /// <summary>"0|TR|001|body" 분해. 제어(JSON) 프레임 = false(IsControlFrame 으로 판별).</summary>
        public static bool TryParseHeader(ReadOnlySpan<byte> frame, out bool encrypted, out ReadOnlySpan<byte> trId, out int count, out ReadOnlySpan<byte> body)
        {
            encrypted = false; trId = default; count = 0; body = default;
            if (frame.Length < 15 || (frame[0] != (byte)'0' && frame[0] != (byte)'1') || frame[1] != (byte)'|') return false;
            encrypted = frame[0] == (byte)'1';
            var rest = frame[2..];
            int p1 = rest.IndexOf((byte)'|');
            if (p1 <= 0) return false;
            trId = rest[..p1];
            rest = rest[(p1 + 1)..];
            int p2 = rest.IndexOf((byte)'|');
            if (p2 <= 0) return false;
            if (!Utf8Parser.TryParse(rest[..p2], out count, out int c) || c != p2 || count <= 0 || count > MaxRecordsPerFrame) return false;
            body = rest[(p2 + 1)..];
            return !body.IsEmpty;
        }

        public static bool IsControlFrame(ReadOnlySpan<byte> frame) => !frame.IsEmpty && frame[0] == (byte)'{';
        public static bool IsTickTr(ReadOnlySpan<byte> trId) => trId.SequenceEqual(TrTick);
        public static bool IsNoticeTr(ReadOnlySpan<byte> trId) => trId.SequenceEqual(TrNotice) || trId.SequenceEqual(TrNoticeVts);

        // ── ② 제어 프레임 ──

        /// <summary>{"header":{"tr_id":"PINGPONG",...}} 판별(δ=0). 형식 오류 = false.</summary>
        public static bool IsPingPong(ReadOnlySpan<byte> frame)
        {
            if (!IsControlFrame(frame)) return false;
            try
            {
                var r = new Utf8JsonReader(frame);
                int depth = 0; bool inHeader = false, found = false;
                while (r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.StartObject: depth++; break;
                        case JsonTokenType.EndObject: depth--; if (depth < 2) inHeader = false; break;
                        case JsonTokenType.PropertyName:
                            if (depth == 1 && r.ValueTextEquals("header"u8)) { inHeader = true; break; }
                            if (inHeader && depth == 2 && r.ValueTextEquals("tr_id"u8))
                            {
                                if (!r.Read() || r.TokenType != JsonTokenType.String) return false;
                                found = r.ValueTextEquals(PingPong);
                            }
                            break;
                    }
                }
                // ★끝까지 판독 성공한 '완결' 문서만 인정 — 잘린/변조 제어 프레임을 반송(echo)하지 않는다(수정 전: tr_id 판독 즉시 true)
                return found && depth == 0;
            }
            catch (JsonException) { return false; }
        }

        // ── 체결가(H0STCNT0) ──

        /// <summary>본문(다건) → dst 에 최대 count 건. 반환 = 판독 건수(형식 오류 레코드 = 0 · 전체 중단).</summary>
        public static int ParseTicks(ReadOnlySpan<byte> body, int count, Span<KisTick> dst)
        {
            const int Required = 14;                                              // 0 코드 · 1 시간 · 2 현재가 · 12 체결량 · 13 누적량
            int n = 0;
            for (int rec = 0; rec < count && n < dst.Length; rec++)
            {
                var t = new KisTick();
                int f = 0;
                bool end = false;
                for (; f < H0STCNT0FieldCount && !end; f++)
                {
                    int caret = body.IndexOf((byte)'^');
                    ReadOnlySpan<byte> field;
                    if (caret < 0) { field = body; body = default; end = true; }
                    else { field = body[..caret]; body = body[(caret + 1)..]; }
                    bool ok = f switch
                    {
                        0 => ParseCode(field, out t.SymbolCode),
                        1 => ParseLong(field, out t.TimeHhmmss),
                        2 => ParseLong(field, out t.Price) && t.Price > 0,
                        12 => ParseLong(field, out t.TradeVolume) && t.TradeVolume >= 0,
                        13 => ParseLong(field, out t.AccVolume) && t.AccVolume >= 0,
                        _ => true,
                    };
                    if (!ok) return n;                                              // 형식 오류 = 이후 전부 폐기(fail-closed)
                }
                if (f < Required) return n;                                         // 필수 필드 전에 본문 종료
                dst[n++] = t;
                if (end) return n;                                                  // 본문 소진 — 선언 건수보다 적으면 남은 레코드 없음
            }
            return n;
        }

        // ── ③ 체결통보(H0STCNI0) ──

        /// <summary>복호 평문(^ 구분) → 통보 1건. 공개 문서 필드 순서: 0 고객ID · 1 계좌 · 2 주문번호 · 3 원주문번호 · 4 매도매수(01/02) ·
        /// 5 정정구분(0 정상 · 1 정정 · 2 취소) · 6 주문종류 · 7 주문조건 · 8 종목코드 · 9 체결수량 · 10 체결단가 · 11 체결시간 ·
        /// 12 거부여부(0/1) · 13 체결여부(1 접수/확인 · 2 체결) · 14 접수여부 · 15 지점 · 16 주문수량.</summary>
        public static bool TryParseNotice(ReadOnlySpan<byte> plain, out KisFillNotice n) => TryParseNotice(plain, out n, out _);

        /// <summary>[KIS VTS] 계좌 필드(1 · 원문 슬라이스) 동반 판독 — HTS ID 구독은 그 ID 의 '모든 계좌' 통보를 보낸다(계좌 핀 대조는 호출측).</summary>
        public static bool TryParseNotice(ReadOnlySpan<byte> plain, out KisFillNotice n, out ReadOnlySpan<byte> account)
        {
            n = default; account = default;
            long side = 0, rctf = 0, refuse = 0, cntgYn = 0;
            int f = 0;
            while (f <= 16)
            {
                int caret = plain.IndexOf((byte)'^');
                var field = caret < 0 ? plain : plain[..caret];
                switch (f)
                {
                    case 1: account = field; break;
                    case 2: if (!ParseLong(field, out n.OrderNo) || n.OrderNo <= 0) return false; break;
                    case 3: if (!field.IsEmpty && !ParseLong(field, out n.OrigOrderNo)) return false; break;
                    case 4: if (!ParseLong(field, out side) || side is not (1 or 2)) return false; break;
                    case 5: if (!ParseLong(field, out rctf)) return false; break;
                    case 8: if (!ParseCode(field, out n.SymbolCode)) return false; break;
                    case 9: if (!field.IsEmpty && !ParseLong(field, out n.FillQty)) return false; break;
                    case 10: if (!field.IsEmpty && !ParseLong(field, out n.FillPrice)) return false; break;
                    case 11: if (!field.IsEmpty && !ParseLong(field, out n.TimeHhmmss)) return false; break;
                    case 12: if (!ParseLong(field, out refuse)) return false; break;
                    case 13: if (!ParseLong(field, out cntgYn) || cntgYn is not (1 or 2)) return false; break;
                    case 16: if (!field.IsEmpty && !ParseLong(field, out n.OrderQty)) return false; break;
                }
                f++;
                if (caret < 0) break;
                plain = plain[(caret + 1)..];
            }
            if (f <= 16) return false;                                               // 필드 부족
            n.Side = side == 2 ? 1 : -1;
            n.Kind = refuse == 1 ? KisNoticeKind.Rejected
                   : cntgYn == 2 ? (n.FillQty > 0 && n.FillPrice > 0 ? KisNoticeKind.Filled : KisNoticeKind.Unknown)
                   : rctf == 2 ? KisNoticeKind.CancelConfirmed
                   : rctf == 1 ? KisNoticeKind.Amended
                   : KisNoticeKind.Accepted;
            return n.Kind != KisNoticeKind.Unknown;
        }

        // ── ④ REST 주문 응답 ──

        public static bool TryParseOrderAck(ReadOnlySpan<byte> json, out KisOrderAck ack)
        {
            ack = default;
            bool rtSeen = false, rtOk = false;
            try
            {
                var r = new Utf8JsonReader(json);
                int depth = 0; bool inOutput = false;
                while (r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.StartObject: depth++; break;
                        case JsonTokenType.EndObject: depth--; if (depth < 2) inOutput = false; break;
                        case JsonTokenType.PropertyName:
                            if (depth == 1)
                            {
                                if (r.ValueTextEquals("rt_cd"u8)) { if (!r.Read() || r.TokenType != JsonTokenType.String) return false; rtSeen = true; rtOk = r.ValueTextEquals("0"u8); }
                                else if (r.ValueTextEquals("output"u8)) inOutput = true;
                            }
                            else if (inOutput && depth == 2)
                            {
                                if (r.ValueTextEquals("ODNO"u8)) { if (!ReadDigits(ref r, out ack.OrderNo)) return false; }
                                else if (r.ValueTextEquals("KRX_FWDG_ORD_ORGNO"u8)) { if (!ReadDigits(ref r, out ack.OrgNo)) return false; }
                            }
                            break;
                    }
                }
            }
            catch (JsonException) { return false; }
            ack.Ok = rtSeen && rtOk && ack.OrderNo > 0;
            return rtSeen;
        }

        // ── ⑦ 실시간 구독 응답(체결통보 복호 키) ──

        /// <summary>
        /// 구독 응답 제어 프레임 판독(δ=0): {"header":{"tr_id":..},"body":{"rt_cd":"0","output":{"key":"(32자)","iv":"(16자)"}}}.
        /// 반환 = 구독 응답 형식(body.rt_cd 존재). success = rt_cd "0" · isNotice = 체결통보 TR(H0STCNI0/9) · hasKeys = key 32B + iv 16B 를 dst 에 복사.
        /// </summary>
        public static bool TryParseSubscribeAck(ReadOnlySpan<byte> json, Span<byte> key32, Span<byte> iv16, out bool success, out bool isNotice, out bool hasKeys)
            => TryParseSubscribeAck(json, key32, iv16, out success, out isNotice, out hasKeys, out _);

        /// <summary>[KIS VTS] 구독 응답 header.tr_id 도메인(실/모의/중립/미상) 동반 판독.</summary>
        public static bool TryParseSubscribeAck(ReadOnlySpan<byte> json, Span<byte> key32, Span<byte> iv16, out bool success, out bool isNotice, out bool hasKeys,
                                                out KisTrDomain trDomain)
        {
            success = false; isNotice = false; hasKeys = false; trDomain = KisTrDomain.Unknown;
            bool rtSeen = false, keyOk = false, ivOk = false;
            try
            {
                var r = new Utf8JsonReader(json);
                int depth = 0; bool inHeader = false, inBody = false, inOutput = false;
                while (r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.StartObject: depth++; break;
                        case JsonTokenType.EndObject:
                            if (depth == 3) inOutput = false;
                            if (depth == 2) { inHeader = false; inBody = false; }
                            depth--; break;
                        case JsonTokenType.PropertyName:
                            if (depth == 1) { inHeader = r.ValueTextEquals("header"u8); inBody = r.ValueTextEquals("body"u8); }
                            else if (depth == 2 && inHeader && r.ValueTextEquals("tr_id"u8))
                            {
                                if (!r.Read() || r.TokenType != JsonTokenType.String) return false;
                                isNotice = r.ValueTextEquals(TrNotice) || r.ValueTextEquals(TrNoticeVts);
                                if (!r.ValueIsEscaped) trDomain = ClassifyTr(r.ValueSpan);
                            }
                            else if (depth == 2 && inBody && r.ValueTextEquals("rt_cd"u8))
                            {
                                if (!r.Read() || r.TokenType != JsonTokenType.String) return false;
                                rtSeen = true; success = r.ValueTextEquals("0"u8);
                            }
                            else if (depth == 2 && inBody && r.ValueTextEquals("output"u8)) inOutput = true;
                            else if (depth == 3 && inOutput && (r.ValueTextEquals("key"u8) || r.ValueTextEquals("iv"u8)))
                            {
                                bool isKey = r.ValueTextEquals("key"u8);
                                if (!r.Read() || r.TokenType != JsonTokenType.String || r.ValueIsEscaped) return false;
                                var v = r.ValueSpan;
                                if (isKey) { if (v.Length == key32.Length) { v.CopyTo(key32); keyOk = true; } }
                                else if (v.Length == iv16.Length) { v.CopyTo(iv16); ivOk = true; }
                            }
                            break;
                    }
                }
            }
            catch (JsonException) { return false; }
            hasKeys = keyOk && ivOk;
            return rtSeen;
        }

        // ── ⑨ [KIS VTS] TR 도메인 · 응답 헤더 정합 ──

        private static ReadOnlySpan<byte> TtcStem => "TTC"u8;

        /// <summary>TR ID 도메인 분류(δ=0). 계좌 TR = 9자 '[T|V]TTC' + 영숫자 5(예: TTTC0802U 실 · VTTC0802U 모의). 그 외 = 미상(fail-closed 판정은 호출측).</summary>
        public static KisTrDomain ClassifyTr(ReadOnlySpan<byte> trId)
        {
            if (trId.SequenceEqual(TrNotice)) return KisTrDomain.Real;
            if (trId.SequenceEqual(TrNoticeVts)) return KisTrDomain.Vts;
            if (trId.SequenceEqual(TrTick) || trId.SequenceEqual(PingPong)) return KisTrDomain.Neutral;
            if (trId.Length != 9 || !trId.Slice(1, 3).SequenceEqual(TtcStem)) return KisTrDomain.Unknown;
            for (int i = 4; i < 9; i++) { byte c = trId[i]; if (!((c >= (byte)'0' && c <= (byte)'9') || (c >= (byte)'A' && c <= (byte)'Z'))) return KisTrDomain.Unknown; }
            return trId[0] == (byte)'T' ? KisTrDomain.Real : trId[0] == (byte)'V' ? KisTrDomain.Vts : KisTrDomain.Unknown;
        }

        /// <summary>REST 응답 tr_id 헤더 정합 — 응답 헤더가 있으면 요청 TR 과 서수 일치해야 한다(부재 = 판정 불가 → 통과 · 공개 문서상 응답 헤더 tr_id 반송).</summary>
        public static bool ResponseTrIdConsistent(ReadOnlySpan<char> requested, ReadOnlySpan<char> responded)
            => responded.IsEmpty || responded.SequenceEqual(requested);

        // ── ⑧ 잔고 · 주문가능(REST) ──

        /// <summary>TTTC8908R(매수가능조회) → output.ord_psbl_cash(주문가능현금 · 원). rt_cd≠"0"/필드 없음/형식 이탈 = false(fail-closed).</summary>
        public static bool TryParseBuyable(ReadOnlySpan<byte> json, out long orderableCash)
        {
            orderableCash = 0;
            bool rtSeen = false, rtOk = false, cashSeen = false;
            try
            {
                var r = new Utf8JsonReader(json);
                int depth = 0; bool inOutput = false;
                while (r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.StartObject: depth++; break;
                        case JsonTokenType.EndObject: if (depth == 2) inOutput = false; depth--; break;
                        case JsonTokenType.PropertyName:
                            if (depth == 1)
                            {
                                if (r.ValueTextEquals("rt_cd"u8)) { if (!r.Read() || r.TokenType != JsonTokenType.String) return false; rtSeen = true; rtOk = r.ValueTextEquals("0"u8); }
                                else if (r.ValueTextEquals("output"u8)) inOutput = true;
                            }
                            else if (depth == 2 && inOutput && r.ValueTextEquals("ord_psbl_cash"u8))
                            {
                                if (!ReadWon(ref r, out orderableCash)) return false;
                                cashSeen = true;
                            }
                            break;
                    }
                }
            }
            catch (JsonException) { return false; }
            return rtSeen && rtOk && cashSeen;
        }

        /// <summary>TTTC8434R(주식잔고조회) output1 → 보유 행(δ=0). 반환 = 행 수 · −1 형식 이탈/rt_cd≠"0" · −2 버퍼 부족. 보유 0 행 제외.</summary>
        public static int ParseHoldings(ReadOnlySpan<byte> json, Span<KisHolding> dst)
        {
            bool rtSeen = false, rtOk = false;
            int n = 0;
            try
            {
                var r = new Utf8JsonReader(json);
                int depth = 0; bool inArr = false, inRow = false;
                KisHolding row = default;
                while (r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.StartObject:
                            depth++;
                            if (inArr && depth == 2) { inRow = true; row = default; }
                            break;
                        case JsonTokenType.EndObject:
                            if (inRow && depth == 2)
                            {
                                inRow = false;
                                if (row.HeldQty > 0)
                                {
                                    if (row.SymbolCode <= 0 || row.SellableQty < 0 || row.SellableQty > row.HeldQty) return -1;
                                    if (n >= dst.Length) return -2;
                                    dst[n++] = row;
                                }
                            }
                            depth--; break;
                        case JsonTokenType.EndArray:
                            if (inArr && depth == 1) inArr = false;
                            break;
                        case JsonTokenType.PropertyName:
                            if (depth == 1)
                            {
                                if (r.ValueTextEquals("rt_cd"u8)) { if (!r.Read() || r.TokenType != JsonTokenType.String) return -1; rtSeen = true; rtOk = r.ValueTextEquals("0"u8); }
                                else if (r.ValueTextEquals("output1"u8)) { if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return -1; inArr = true; }
                            }
                            else if (inRow && depth == 2)
                            {
                                if (r.ValueTextEquals("pdno"u8)) { if (!r.Read() || r.TokenType != JsonTokenType.String || !ParseCode(r.ValueSpan, out row.SymbolCode)) return -1; }
                                else if (r.ValueTextEquals("hldg_qty"u8)) { if (!ReadDigits(ref r, out row.HeldQty)) return -1; }
                                else if (r.ValueTextEquals("ord_psbl_qty"u8)) { if (!ReadDigits(ref r, out row.SellableQty)) return -1; }
                            }
                            break;
                    }
                }
            }
            catch (JsonException) { return -1; }
            return rtSeen && rtOk ? n : -1;
        }

        // ── ⑥ 미체결 조회(TTTC8001R 주식일별주문체결조회 · CCLD_DVSN=02 미체결) ──

        /// <summary>
        /// [미체결 대사] REST 응답 1페이지 → 정규화 행(δ=0). output1 배열의 각 객체: odno · orgn_odno · pdno · ord_qty · rmn_qty · ord_unpr ·
        /// sll_buy_dvsn_cd("01" 매도 · "02" 매수) · cncl_yn("Y" = 취소 주문 행 — 제외). 잔량 0 행 제외.
        /// 반환 = 행 수 · −1 형식 이탈/rt_cd≠"0"(fail-closed) · −2 버퍼 부족. 연속 조회(tr_cont)는 HTTP 헤더라 호출측이 last 로 전달.
        /// </summary>
        public static int ParseOpenOrders(ReadOnlySpan<byte> json, Span<BrokerOpenOrderRow> dst)
        {
            bool rtSeen = false, rtOk = false;
            int n = 0;
            try
            {
                var r = new Utf8JsonReader(json);
                int depth = 0; bool inArr = false, inRow = false, cancelled = false;
                BrokerOpenOrderRow row = default;
                while (r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.StartObject:
                            depth++;
                            if (inArr && depth == 2) { inRow = true; row = default; cancelled = false; }
                            break;
                        case JsonTokenType.EndObject:
                            if (inRow && depth == 2)
                            {
                                inRow = false;
                                if (!cancelled && row.RemainingQty > 0)
                                {
                                    if (row.VenueOrderNo <= 0 || row.SymbolCode <= 0 || row.Side == 0 || row.OrderQty < row.RemainingQty) return -1;
                                    if (n >= dst.Length) return -2;
                                    row.Venue = OpenOrderVenueKind.Kis;
                                    dst[n++] = row;
                                }
                            }
                            depth--;
                            break;
                        case JsonTokenType.EndArray:
                            if (inArr && depth == 1) inArr = false;
                            break;
                        case JsonTokenType.PropertyName:
                            if (depth == 1)
                            {
                                if (r.ValueTextEquals("rt_cd"u8)) { if (!r.Read() || r.TokenType != JsonTokenType.String) return -1; rtSeen = true; rtOk = r.ValueTextEquals("0"u8); }
                                else if (r.ValueTextEquals("output1"u8)) { if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return -1; inArr = true; }
                            }
                            else if (inRow && depth == 2)
                            {
                                if (r.ValueTextEquals("odno"u8)) { if (!ReadDigits(ref r, out row.VenueOrderNo)) return -1; }
                                else if (r.ValueTextEquals("orgn_odno"u8)) { if (!ReadDigits(ref r, out row.OrigVenueOrderNo)) return -1; }
                                else if (r.ValueTextEquals("pdno"u8)) { if (!r.Read() || r.TokenType != JsonTokenType.String || !ParseCode(r.ValueSpan, out row.SymbolCode)) return -1; }
                                else if (r.ValueTextEquals("ord_qty"u8)) { if (!ReadDigits(ref r, out row.OrderQty)) return -1; }
                                else if (r.ValueTextEquals("rmn_qty"u8)) { if (!ReadDigits(ref r, out row.RemainingQty)) return -1; }
                                else if (r.ValueTextEquals("ord_unpr"u8)) { if (!ReadWon(ref r, out row.PriceWon)) return -1; }
                                else if (r.ValueTextEquals("sll_buy_dvsn_cd"u8))
                                {
                                    if (!r.Read() || r.TokenType != JsonTokenType.String) return -1;
                                    row.Side = r.ValueTextEquals("02"u8) ? 1 : r.ValueTextEquals("01"u8) ? -1 : 0;
                                }
                                else if (r.ValueTextEquals("cncl_yn"u8))
                                {
                                    if (!r.Read() || r.TokenType != JsonTokenType.String) return -1;
                                    cancelled = r.ValueTextEquals("Y"u8);
                                }
                            }
                            break;
                    }
                }
            }
            catch (JsonException) { return -1; }
            return rtSeen && rtOk ? n : -1;
        }

        // 원 금액 문자열 — 정수 또는 소수부 0("71000" · "71000.00"). 소수부 비영 = 형식 이탈(호가 단위 위반).
        private static bool ReadWon(ref Utf8JsonReader r, out long v)
        {
            v = 0;
            if (!r.Read() || r.TokenType != JsonTokenType.String) return false;
            var s = r.ValueSpan;
            if (s.IsEmpty) return true;
            int dot = s.IndexOf((byte)'.');
            if (dot >= 0)
            {
                for (int k = dot + 1; k < s.Length; k++) if (s[k] != (byte)'0') return false;
                s = s[..dot];
                if (s.IsEmpty) return true;
            }
            return Utf8Parser.TryParse(s, out v, out int c) && c == s.Length && v >= 0;
        }

        // ── ⑤ 토큰 ──

        /// <summary>토큰 응답 → 토큰 바이트(dst) · 수명(초) · 만료 시각(KST 벽시계 ticks · 없으면 0).</summary>
        public static bool TryParseToken(ReadOnlySpan<byte> json, Span<byte> tokenDst, out int tokenLength, out long expiresInSeconds, out long expiredAtKstTicks)
        {
            tokenLength = 0; expiresInSeconds = 0; expiredAtKstTicks = 0;
            try
            {
                var r = new Utf8JsonReader(json);
                int depth = 0;
                while (r.Read())
                {
                    if (r.TokenType == JsonTokenType.StartObject) { depth++; continue; }
                    if (r.TokenType == JsonTokenType.EndObject) { depth--; continue; }
                    if (r.TokenType != JsonTokenType.PropertyName || depth != 1) continue;
                    if (r.ValueTextEquals("access_token"u8))
                    {
                        if (!r.Read() || r.TokenType != JsonTokenType.String || r.ValueIsEscaped || r.ValueSpan.Length > tokenDst.Length) return false;
                        r.ValueSpan.CopyTo(tokenDst); tokenLength = r.ValueSpan.Length;
                    }
                    else if (r.ValueTextEquals("expires_in"u8))
                    {
                        if (!r.Read()) return false;
                        if (r.TokenType == JsonTokenType.Number) { if (!r.TryGetInt64(out expiresInSeconds)) return false; }
                        else if (r.TokenType == JsonTokenType.String) { if (!ParseLong(r.ValueSpan, out expiresInSeconds)) return false; }
                        else return false;
                    }
                    else if (r.ValueTextEquals("access_token_token_expired"u8))
                    {
                        if (!r.Read() || r.TokenType != JsonTokenType.String || !TryParseKstDateTime(r.ValueSpan, out expiredAtKstTicks)) return false;
                    }
                }
            }
            catch (JsonException) { tokenLength = 0; return false; }
            return tokenLength >= 16 && expiresInSeconds > 0;
        }

        /// <summary>"yyyy-MM-dd HH:mm:ss" → DateTime ticks(종류 미지정 · KST 벽시계 값 그대로).</summary>
        public static bool TryParseKstDateTime(ReadOnlySpan<byte> s, out long ticks)
        {
            ticks = 0;
            if (s.Length != 19 || s[4] != '-' || s[7] != '-' || s[10] != ' ' || s[13] != ':' || s[16] != ':') return false;
            if (!Num(s, 0, 4, out int y) || !Num(s, 5, 2, out int mo) || !Num(s, 8, 2, out int d) || !Num(s, 11, 2, out int h) || !Num(s, 14, 2, out int mi) || !Num(s, 17, 2, out int sec)) return false;
            if (y < 2000 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || sec > 59) return false;
            ticks = new DateTime(y, mo, d, h, mi, sec, DateTimeKind.Unspecified).Ticks;
            return true;
        }

        // ── 공통 ──

        private static bool Num(ReadOnlySpan<byte> s, int at, int len, out int v)
            => Utf8Parser.TryParse(s.Slice(at, len), out v, out int c) && c == len;

        private static bool ParseLong(ReadOnlySpan<byte> s, out long v)
        {
            v = 0;
            if (s.IsEmpty) return false;
            int sign = 1;
            if (s[0] == (byte)'-' || s[0] == (byte)'+') { sign = s[0] == (byte)'-' ? -1 : 1; s = s[1..]; }
            if (s.IsEmpty || !Utf8Parser.TryParse(s, out long u, out int c) || c != s.Length || u < 0) return false;
            v = sign * u;
            return true;
        }

        private static bool ParseCode(ReadOnlySpan<byte> s, out long code)
        {
            code = 0;
            if (!s.IsEmpty && (s[0] == (byte)'A' || s[0] == (byte)'a')) s = s[1..];
            return s.Length == 6 && Utf8Parser.TryParse(s, out code, out int c) && c == 6 && code > 0;
        }

        private static bool ReadDigits(ref Utf8JsonReader r, out long v)
        {
            v = 0;
            if (!r.Read() || r.TokenType != JsonTokenType.String) return false;
            var s = r.ValueSpan;
            if (s.IsEmpty) return true;                                              // 빈 값 = 0(호출측이 Ok 판정)
            return Utf8Parser.TryParse(s, out v, out int c) && c == s.Length && v >= 0;
        }
    }
}
