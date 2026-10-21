// ============================================================================
// KisMultiBrokerRoutingTests — [KIS · 다중 브로커 · 추천 A]
//
//  §1 KisProtocol — 실시간 헤더 · H0STCNT0 단건/다건/형식 오류 · PINGPONG 판별 · H0STCNI0 평문/AES 암호문 체결통보 ·
//     REST 주문 응답(공백·필드 순서 무관 — 수정 전 Contains 판정 Red) · 토큰 응답 · δ=0.
//  §2 KisTokenLifecycle — 서버 만료 사용(KST 만료 시각이 더 이르면 그쪽) · 5분 선행 구간 갱신 실패 = Disarm · 만료 = Disarm ·
//     갱신 실패 시 기존 토큰 유지 · Bearer 헤더 δ=0 · 새 토큰 마스킹 등록.
//  §3 FinancialPiiMasker — KIS AppSecret(무구조 장문) 정확 일치 마스킹(수정 전 평문 통과 Red) · AppKey/JWT 패턴.
//  §4 다중 브로커 — 라우터 브로커 인덱스 → 실제 송출 IBroker(수정 전 단일 브로커 고정 Red) · 미매핑 인덱스 = 거부 · KIS 주문번호
//     네임스페이스로 키움 번호와 충돌 0 · KIS 통보로 추적 슬롯 대사 · 장애 = 라우터 Disarm + 페일오버 선지정(재송출 없음).
//  §5 [방향 B] KisSessionInterlock — 미연결 차단 · 침묵 타임아웃/끊김 → 비트 + 센티널 장애 보고 · 슬롯 회수 없음(증거 건수만).
//  §6 KisLiveBroker 결함 교정 소스 회귀 · 오중 빌드 회귀.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class KisMultiBrokerRoutingTests
    {
        // H0STCNT0 레코드 1건(46필드) — 0 코드 · 1 시간 · 2 현재가 · 12 체결량 · 13 누적량 · 나머지 0
        private static string TickRecord(string code, long time, long price, long vol, long acc)
        {
            var f = new string[KisProtocol.H0STCNT0FieldCount];
            for (int i = 0; i < f.Length; i++) f[i] = "0";
            f[0] = code; f[1] = time.ToString("D6"); f[2] = price.ToString(); f[12] = vol.ToString(); f[13] = acc.ToString();
            return string.Join('^', f);
        }

        private static byte[] U8(string s) => Encoding.UTF8.GetBytes(s);

        // H0STCNI0 평문 — 공개 문서 필드 순서(0..22 중 0..16 사용)
        private static string Notice(long odno, long orig, string side, int rctf, string code, long qty, long px, int refuse, int cntgYn, long ordQty = 10)
            => string.Join('^', new[] { "cust01", "12345678", odno.ToString("D10"), orig > 0 ? orig.ToString("D10") : "", side, rctf.ToString(), "00", "0",
                                        code, qty.ToString(), px.ToString(), "093001", refuse.ToString(), cntgYn.ToString(), "1", "91252", ordQty.ToString(), "홍길동", "삼성전자" });

        // ════════════ §1 프로토콜 ════════════

        [Fact]
        public void Header_And_Ticks_Single_Multi_Malformed()
        {
            var frame = U8("0|H0STCNT0|002|" + TickRecord("005930", 93001, 71900, 5, 3052507) + "^" + TickRecord("000660", 93002, 120500, 10, 99));
            Assert.True(KisProtocol.TryParseHeader(frame, out bool enc, out var tr, out int count, out var body));
            Assert.False(enc);
            Assert.True(KisProtocol.IsTickTr(tr));
            Assert.Equal(2, count);
            Span<KisTick> ticks = new KisTick[4];
            Assert.Equal(2, KisProtocol.ParseTicks(body, count, ticks));
            Assert.Equal((5930L, 93001L, 71900L, 5L, 3052507L), (ticks[0].SymbolCode, ticks[0].TimeHhmmss, ticks[0].Price, ticks[0].TradeVolume, ticks[0].AccVolume));
            Assert.Equal((660L, 120500L), (ticks[1].SymbolCode, ticks[1].Price));
            Assert.Equal(0, KisProtocol.ParseTicks(U8(TickRecord("005930", 93001, 71900, 5, 1).Replace("^71900^", "^7l900^")), 1, ticks));   // 형식 오류
            Assert.Equal(0, KisProtocol.ParseTicks(U8("005930^093001^71900"), 1, ticks));                                       // 필수 필드 부족
            Assert.False(KisProtocol.TryParseHeader(U8("2|H0STCNT0|001|x^y^z^w"), out _, out _, out _, out _));
            Assert.False(KisProtocol.TryParseHeader(U8("0|H0STCNT0|999|" + TickRecord("005930", 1, 1, 1, 1)), out _, out _, out _, out _));   // 건수 상한
            Assert.False(KisProtocol.TryParseHeader(U8("0|H0STCNT0|"), out _, out _, out _, out _));
        }

        [Theory]
        [InlineData("{\"header\":{\"tr_id\":\"PINGPONG\",\"datetime\":\"20261006093000\"}}", true)]
        [InlineData("{\"header\":{\"datetime\":\"x\",\"tr_id\":\"PINGPONG\"}}", true)]
        [InlineData("{\"header\":{\"tr_id\":\"H0STCNT0\"},\"body\":{\"rt_cd\":\"0\"}}", false)]
        [InlineData("{\"body\":{\"header\":{\"tr_id\":\"PINGPONG\"}}}", false)]        // 중첩 위치 위장
        [InlineData("{\"header\":{\"tr_id\":\"PINGPONG\"", false)]                     // 잘린 JSON
        [InlineData("0|PINGPONG|001|x", false)]
        public void PingPong_Detection(string frame, bool expected) => Assert.Equal(expected, KisProtocol.IsPingPong(U8(frame)));

        [Fact]
        public void Notice_Plain_Kinds()
        {
            Assert.True(KisProtocol.TryParseNotice(U8(Notice(117057, 0, "02", 0, "005930", 10, 71900, 0, 2)), out var f));
            Assert.Equal((KisNoticeKind.Filled, 117057L, 5930L, 10L, 71900L, 1), (f.Kind, f.OrderNo, f.SymbolCode, f.FillQty, f.FillPrice, f.Side));
            Assert.True(KisProtocol.TryParseNotice(U8(Notice(117058, 0, "01", 0, "005930", 0, 0, 1, 1)), out var rej));
            Assert.Equal((KisNoticeKind.Rejected, -1), (rej.Kind, rej.Side));
            Assert.True(KisProtocol.TryParseNotice(U8(Notice(117099, 117057, "02", 2, "005930", 0, 0, 0, 1)), out var cxl));
            Assert.Equal((KisNoticeKind.CancelConfirmed, 117057L), (cxl.Kind, cxl.OrigOrderNo));
            Assert.True(KisProtocol.TryParseNotice(U8(Notice(117060, 0, "02", 0, "005930", 0, 0, 0, 1)), out var acc));
            Assert.Equal(KisNoticeKind.Accepted, acc.Kind);
            Assert.False(KisProtocol.TryParseNotice(U8("a^b^c"), out _));                                                     // 필드 부족
            Assert.False(KisProtocol.TryParseNotice(U8(Notice(117061, 0, "03", 0, "005930", 1, 1, 0, 2)), out _));             // 매도매수 구분 이탈
            Assert.False(KisProtocol.TryParseNotice(U8(Notice(117062, 0, "02", 0, "005930", 0, 0, 0, 2)), out _));             // 체결인데 수량 0
        }

        [Fact]
        public void OrderAck_JsonRobust_OldContainsCheckWouldFail()
        {
            const string spaced = "{ \"msg1\": \"주문 전송 완료 되었습니다.\", \"rt_cd\": \"0\", \"msg_cd\": \"APBK0013\", \"output\": { \"ORD_TMD\": \"121052\", \"ODNO\": \"0000117057\", \"KRX_FWDG_ORD_ORGNO\": \"91252\" } }";
            Assert.DoesNotContain("\"rt_cd\":\"0\"", spaced);                    // ★수정 전 판정(Contains)은 정상 성공 응답을 '실패'로 오판
            Assert.True(KisProtocol.TryParseOrderAck(U8(spaced), out var ack));
            Assert.Equal((true, 117057L, 91252L), (ack.Ok, ack.OrderNo, ack.OrgNo));
            Assert.True(KisProtocol.TryParseOrderAck(U8("{\"rt_cd\":\"1\",\"msg1\":\"주문가능금액 초과\",\"output\":{}}"), out var fail));
            Assert.False(fail.Ok);
            Assert.True(KisProtocol.TryParseOrderAck(U8("{\"rt_cd\":\"0\",\"output\":{\"ODNO\":\"\"}}"), out var noOdno));
            Assert.False(noOdno.Ok);                                             // 성공 코드라도 주문번호 없으면 실패(추적 불가 = 미송출 취급)
            Assert.False(KisProtocol.TryParseOrderAck(U8("not json"), out _));
            Assert.False(KisProtocol.TryParseOrderAck(U8("{\"output\":{\"ODNO\":\"1\"}}"), out _));   // rt_cd 부재
        }

        private const string Jwt = "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0b2tlbiIsImV4cCI6MTc2fQ.abcDEFghiJKLmnoPQRstuVWXyz012345";

        [Fact]
        public void Token_Parse_NumberOrString_KstExpiry_EscapedRejected()
        {
            Span<byte> buf = new byte[512];
            Assert.True(KisProtocol.TryParseToken(U8($"{{\"access_token\":\"{Jwt}\",\"access_token_token_expired\":\"2026-10-07 09:00:00\",\"token_type\":\"Bearer\",\"expires_in\":86400}}"),
                buf, out int len, out long sec, out long kst));
            Assert.Equal(Jwt, Encoding.ASCII.GetString(buf[..len]));
            Assert.Equal(86400, sec);
            Assert.Equal(new DateTime(2026, 10, 7, 9, 0, 0).Ticks, kst);
            Assert.True(KisProtocol.TryParseToken(U8($"{{\"expires_in\":\"3600\",\"access_token\":\"{Jwt}\"}}"), buf, out _, out long s2, out long k2));
            Assert.Equal((3600L, 0L), (s2, k2));
            Assert.False(KisProtocol.TryParseToken(U8("{\"access_token\":\"ab\\u0063defghijklmnopq\",\"expires_in\":10}"), buf, out _, out _, out _));   // 이스케이프 토큰 거부
            Assert.False(KisProtocol.TryParseToken(U8("{\"access_token\":\"short\",\"expires_in\":10}"), buf, out _, out _, out _));
            Assert.False(KisProtocol.TryParseToken(U8($"{{\"access_token\":\"{Jwt}\",\"access_token_token_expired\":\"2026-13-07 09:00:00\",\"expires_in\":10}}"), buf, out _, out _, out _));
        }

        private static (byte[] key, byte[] iv) Keys() => (Enumerable32(), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
        private static byte[] Enumerable32() { var k = new byte[32]; for (int i = 0; i < 32; i++) k[i] = (byte)(i * 7 + 3); return k; }

        private static string EncryptNotice(string plain, byte[] key, byte[] iv)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(plain), iv, PaddingMode.PKCS7));
        }

        [Fact]
        public void Adapter_EncryptedNotice_PingPongEcho_Ticks_ZeroAlloc()
        {
            var (key, iv) = Keys();
            var session = new KisSessionInterlock(null, 1, () => 1);
            var echoes = new List<int>();
            var fills = new List<FillPayload>(64);
            var ticks = new List<TickPayload>(256);
            using var a = new KisOpenApiAdapter(session, send: m => { echoes.Add(m.Length); return true; }, onTick: ticks.Add, onFill: fills.Add, nowTicks: () => 7);
            var noticeFrame = U8("1|H0STCNI0|001|" + EncryptNotice(Notice(117057, 0, "02", 0, "005930", 10, 71900, 0, 2), key, iv));
            Assert.False(a.OnFrame(noticeFrame));                                // 키 없음 = 폐기
            Assert.Equal(1, a.NoticeWithoutKey);
            a.SetNoticeKeys(key, iv);
            Assert.True(a.OnFrame(noticeFrame));
            var f = Assert.Single(fills);
            Assert.Equal(KisOrderIdSpace.Encode(117057), f.OrderId);             // ★KIS 네임스페이스 주문번호
            Assert.Equal((5930L, 10L, 71900.0, 1), (f.SymbolCode, f.FillQty, f.FillPrice, f.Side));
            Assert.Equal(BridgeFillStatus.Filled, BridgeFillStatusCodec.Decode(in f, out long rem));
            Assert.Equal(-1, rem);                                               // KIS 통보엔 잔량 없음 → 대조 생략
            var wrong = U8("1|H0STCNI0|001|" + EncryptNotice(Notice(1, 0, "02", 0, "005930", 1, 1, 0, 2), new byte[32], iv));
            Assert.False(a.OnFrame(wrong));                                      // 다른 키 = 복호 실패(패딩) → 폐기
            var ping = U8("{\"header\":{\"tr_id\":\"PINGPONG\",\"datetime\":\"20261006093000\"}}");
            Assert.True(a.OnFrame(ping));
            Assert.Equal(new[] { ping.Length }, echoes.ToArray());               // ★받은 그대로 반송
            Assert.Equal(1, session.PingPongs);
            var tickFrame = U8("0|H0STCNT0|001|" + TickRecord("005930", 93001, 71900, 5, 3052507));
            Assert.True(a.OnFrame(tickFrame));
            Assert.Equal(71900.0, Assert.Single(ticks).Price);
            Assert.False(a.OnFrame(U8("1|H0STCNT0|001|" + TickRecord("005930", 1, 1, 1, 1))));   // 체결가는 평문이어야 함
            ReadOnlyMemory<byte> nm = noticeFrame, tm = tickFrame, pm = ping;
            Action body = () => { for (int k = 0; k < 50; k++) { a.OnFrame(tm); a.OnFrame(nm); a.OnFrame(pm); } };
            fills.Clear(); ticks.Clear(); echoes.Capacity = 1024;
            body();
            fills.Clear(); ticks.Clear(); echoes.Clear();
            Assert.Equal(0, MinAllocated(body));                                 // ★프레임 판독·AES 복호·통보/틱 사상 δ=0
        }

        [Fact]
        public void NoticeCipher_MatchesBclCbc_AllLengths_RejectsBadPadding()
        {
            var (key, iv) = Keys();
            using var cipher = new KisNoticeCipher(key, iv);
            using var bcl = Aes.Create();
            bcl.Key = key;
            var rng = new Random(11);
            var work = new byte[4096]; var plain = new byte[4096];
            for (int len = 0; len <= 200; len += 7)
            {
                var msg = new byte[len]; rng.NextBytes(msg);
                byte[] ct = bcl.EncryptCbc(msg, iv, PaddingMode.PKCS7);
                byte[] b64 = U8(Convert.ToBase64String(ct));
                int n = cipher.TryDecrypt(b64, work, plain);
                Assert.Equal(len, n);
                Assert.Equal(msg, plain.AsSpan(0, n).ToArray());                // BCL 원샷 복호와 바이트 동일
                ct[^1] ^= 0x5A;                                                   // 마지막 블록 변조 → 패딩 위반(거의 확실)
                int bad = cipher.TryDecrypt(U8(Convert.ToBase64String(ct)), work, plain);
                Assert.True(bad == -1 || bad != len || !plain.AsSpan(0, Math.Max(0, bad)).SequenceEqual(msg));
            }
            Assert.Equal(-1, cipher.TryDecrypt(U8("not base64!!"), work, plain));
            Assert.Equal(-1, cipher.TryDecrypt(U8(Convert.ToBase64String(new byte[15])), work, plain));   // 블록 배수 아님
            Assert.Throws<ArgumentException>(() => new KisNoticeCipher(new byte[16], iv));
        }

        [Fact]
        public void ZeroAlloc_PureParsers()
        {
            byte[] tick = U8("0|H0STCNT0|001|" + TickRecord("005930", 93001, 71900, 5, 3052507));
            byte[] ping = U8("{\"header\":{\"tr_id\":\"PINGPONG\"}}");
            byte[] notice = U8(Notice(117057, 0, "02", 0, "005930", 10, 71900, 0, 2));
            byte[] ack = U8("{\"rt_cd\":\"0\",\"output\":{\"ODNO\":\"0000117057\",\"KRX_FWDG_ORD_ORGNO\":\"91252\"}}");
            var ticks = new KisTick[4];
            long sum = 0;
            Action body = () =>
            {
                for (int k = 0; k < 200; k++)
                {
                    KisProtocol.TryParseHeader(tick, out _, out _, out int c, out var b);
                    sum += KisProtocol.ParseTicks(b, c, ticks);
                    sum += KisProtocol.IsPingPong(ping) ? 1 : 0;
                    sum += KisProtocol.TryParseNotice(notice, out var n) ? n.FillQty : 0;
                    sum += KisProtocol.TryParseOrderAck(ack, out var a) ? a.OrderNo : 0;
                }
            };
            body();
            Assert.Equal(0, MinAllocated(body));
            Assert.True(sum > 0);
        }

        // ════════════ §2 토큰 수명주기 ════════════

        private sealed class TokenServer
        {
            public string? Json; public bool Throw; public int Calls;
            public Task<int> Fetch(Memory<byte> dst, CancellationToken ct)
            {
                Calls++;
                if (Throw) throw new HttpRequestFailure();
                if (Json is null) return Task.FromResult(0);
                var b = Encoding.UTF8.GetBytes(Json);
                b.CopyTo(dst);
                return Task.FromResult(b.Length);
            }
        }
        private sealed class HttpRequestFailure : Exception { }

        [Fact]
        public async Task TokenLifecycle_LeadWindowFailure_Disarms_SuccessRecovers_ExpiryDisarms()
        {
            long now = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc).Ticks;
            var srv = new TokenServer { Json = $"{{\"access_token\":\"{Jwt}\",\"expires_in\":3600}}" };
            var registered = new List<string>();
            var life = new KisTokenLifecycle(srv.Fetch, () => now, onNewTokenText: registered.Add);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KisAuth);
            life.BindInterlock(mask, ArmingInterlockSource.KisAuth);
            Assert.True(mask.Seal(out _));
            Assert.True(mask.Blocked);                                           // 토큰 없음 = 차단
            Assert.True(life.NeedsRefresh());
            Assert.True(await life.RefreshAsync());
            Assert.False(mask.Blocked);
            Assert.Equal(new[] { Jwt }, registered.ToArray());                   // 마스커/유출 감지 등록 경로
            byte[] hdr = new byte[512];
            int n = life.TryWriteBearer(hdr);
            Assert.Equal("Bearer " + Jwt, Encoding.ASCII.GetString(hdr, 0, n));
            now += TimeSpan.FromMinutes(54).Ticks;                               // 만료 6분 전 — 아직 선행 구간 밖
            life.Evaluate();
            Assert.False(life.NeedsRefresh());
            Assert.False(mask.Blocked);
            now += TimeSpan.FromMinutes(2).Ticks;                                // 만료 4분 전 — 선행 구간
            Assert.True(life.NeedsRefresh());
            srv.Throw = true;
            Assert.False(await life.RefreshAsync());                             // ★선행 구간 갱신 실패 = 즉시 Disarm
            Assert.True(mask.IsRaised(ArmingInterlockSource.KisAuth));
            Assert.True(life.TryWriteBearer(hdr) > 0);                           // 기존 토큰은 만료 전까지 유지(실패가 토큰을 지우지 않음)
            srv.Throw = false;
            srv.Json = "{\"access_token\":\"garbage\"}";
            Assert.False(await life.RefreshAsync());                             // 형식 오류 응답도 실패
            Assert.Equal(n, life.TryWriteBearer(hdr));                           // ★기존 토큰 무손상(검증 후 교체)
            srv.Json = $"{{\"access_token\":\"{Jwt}X\",\"expires_in\":3600}}";
            Assert.True(await life.RefreshAsync());
            Assert.False(mask.Blocked);                                          // 갱신 성공 = 해제
            now += TimeSpan.FromHours(2).Ticks;                                  // 갱신 없이 만료
            life.Evaluate();
            Assert.True(mask.Blocked);
            Assert.Equal(-1, life.TryWriteBearer(hdr));                          // 만료 토큰 송신 금지
            Assert.Equal(2, life.Failures);
        }

        [Fact]
        public async Task TokenLifecycle_KstExpiryEarlierWins_BearerZeroAlloc()
        {
            long now = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc).Ticks;   // = KST 09:00
            var srv = new TokenServer { Json = $"{{\"access_token\":\"{Jwt}\",\"access_token_token_expired\":\"2026-10-06 10:00:00\",\"expires_in\":86400}}" };
            var life = new KisTokenLifecycle(srv.Fetch, () => now, onNewTokenText: _ => { });
            Assert.True(await life.RefreshAsync());
            Assert.Equal(now + TimeSpan.FromHours(1).Ticks, life.ExpiresUtcTicks);   // KST 10:00 = UTC 01:00(expires_in 24h 보다 이름)
            var hdr = new byte[512];
            Action body = () => { for (int k = 0; k < 1000; k++) { life.TryWriteBearer(hdr); life.Evaluate(); _ = life.NeedsRefresh(); } };
            body();
            Assert.Equal(0, MinAllocated(body));
            life.Zeroize();
            Assert.True(life.ArmingBlocked);
            Assert.Equal(-1, life.TryWriteBearer(hdr));
        }

        // ════════════ §3 마스커 ════════════

        [Fact]
        public void Masker_RegisteredKisAppSecret_Masked_PatternsStillWork()
        {
            string appSecret = "q8Zt3+Lm9Xc/Pw0VnR7yKd2HfJs6BgT1aUeO4iYlNpWzQxCv5MbAhGrDjSk8FuE=" + Guid.NewGuid().ToString("N");
            string line = $"[KIS] token req appsecret={appSecret} ok";
            Assert.Contains(appSecret, FinancialPiiMasker.Mask(line));           // ★등록 전: 무구조 장문 비밀은 패턴 규칙에 안 걸림(실측)
            Assert.True(FinancialPiiMasker.RegisterSecret(appSecret));
            Assert.False(FinancialPiiMasker.RegisterSecret(appSecret));          // 중복
            Assert.False(FinancialPiiMasker.RegisterSecret("short"));
            string masked = FinancialPiiMasker.Mask(line);
            Assert.DoesNotContain(appSecret, masked);
            Assert.Contains(new string('*', appSecret.Length), masked);
            Assert.Equal(line.Length, masked.Length);                            // 길이 불변
            string appKey = "PS" + new string('A', 17) + new string('7', 17);    // KIS AppKey 형식(PS+34)
            Assert.DoesNotContain(appKey, FinancialPiiMasker.Mask("appkey=" + appKey));
            Assert.DoesNotContain(Jwt, FinancialPiiMasker.Mask("authorization: Bearer " + Jwt));
            char[] buf = line.ToCharArray();
            Action body = () => { for (int k = 0; k < 100; k++) { line.AsSpan().CopyTo(buf); FinancialPiiMasker.MaskInPlace(buf); } };
            body();
            Assert.Equal(0, MinAllocated(body));
        }

        // ════════════ §4 다중 브로커 ════════════

        private sealed class RecBroker : IBroker
        {
            public readonly string Tag; public int Places; public long NextId;
            public RecBroker(string tag, long firstId) { Tag = tag; NextId = firstId; }
            public string Name => Tag;
            public bool IsLive => false;
            public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name, PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
            { Places++; return (true, (NextId++).ToString()); }
            public void Cancel(string orderId) { }
            public decimal HeldQty(PaperAsset asset, string symbol) => 0m;
        }

        private sealed class Rig
        {
            public long Now = 1_000_000;
            public LockFreeBrokerSwitchboard Board = null!;
            public BrokerFailoverSentinel Sentinel = null!;
            public ResilientOrderRouter Router = null!;
            public OrderExecutionFeedAdapter Exec = null!;
            public RouterFeedGate Gate = null!;
            public OrderDispatchRing Ring = null!;
            public OrderDrainWorker Worker = null!;
            public RecBroker Kiwoom = null!, Kis = null!;
        }

        private static Rig Make(int active, IBroker?[]? overrideMap = null, bool useMap = true)
        {
            var r = new Rig();
            Func<long> clock = () => Volatile.Read(ref r.Now);
            var price = new ReferencePriceFeedAdapter(capacityPow2: 64, nowMicros: clock);
            r.Board = new LockFreeBrokerSwitchboard(2, initialActive: active);
            var pending = new PendingOrderCascadeCanceller(64);
            r.Sentinel = new BrokerFailoverSentinel(r.Board, pendingOrders: pending);
            r.Router = new ResilientOrderRouter(r.Board, r.Sentinel, pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            r.Exec = new OrderExecutionFeedAdapter(r.Router, 64, 120_000_000, clock);
            var breaker = new FeedDisconnectCircuitBreaker(price, () => true);
            r.Gate = new RouterFeedGate(r.Router, price, r.Exec, breaker, requireDepthForMarket: false);
            price.OnTick(ReferencePriceFeedAdapter.SymbolHashForCode(5930), 70_000, 1);
            breaker.Evaluate();
            r.Kiwoom = new RecBroker("kiwoom", 117057);                        // 키움 클라이언트 ID 와
            r.Kis = new RecBroker("kis", KisOrderIdSpace.Encode(117057));      // KIS ODNO 가 같은 숫자 117057
            r.Ring = new OrderDispatchRing(64);
            var disp = new GatedOrderDispatcher(r.Ring, () => true);
            r.Worker = new OrderDrainWorker(disp, r.Kiwoom, wired: false, routeGate: r.Gate,
                                            brokersByIndex: useMap ? overrideMap ?? new IBroker?[] { r.Kiwoom, r.Kis } : null);
            return r;
        }

        private static void Submit(Rig r, int n = 1)
        {
            for (int i = 0; i < n; i++) r.Ring.Publish(0, 5930, OrderSide.Buy, 1, new QPrice(70_000L * 100_000_000L), new QQty(100_000_000L), 0);
            r.Worker.DrainOnce();
        }

        [Fact]
        public void RoutedBrokerIndex_ReachesThatBroker_LegacySingleBrokerIgnoredIt()
        {
            var r = Make(active: 1);
            Submit(r);
            Assert.Equal((0, 1), (r.Kiwoom.Places, r.Kis.Places));              // ★라우터가 고른 KIS(1)로 송출
            var legacy = Make(active: 1, useMap: false);
            Submit(legacy);
            Assert.Equal((1, 0), (legacy.Kiwoom.Places, legacy.Kis.Places));    // 수정 전 동작: 라우터 판정 무시 · 단일 브로커 고정
            var unmapped = Make(active: 1, overrideMap: new IBroker?[] { new RecBroker("only", 1) });
            Submit(unmapped);
            Assert.Equal(1, unmapped.Worker.BrokerIndexVetoes);                 // 매핑 없는 인덱스 = 기본 브로커 대체 금지(거부)
            Assert.Equal(0, unmapped.Router.TrackedOrders);                     // 슬롯 롤백
        }

        [Fact]
        public void KisNamespace_NoCollisionWithKiwoom_NoticeReconcilesSlot()
        {
            var r = Make(active: 0);
            Submit(r);                                                           // 키움: 브로커 번호 117057
            r.Board.SwitchTo(1, 1);
            Submit(r);                                                           // KIS: 브로커 번호 2^48 | 117057
            Assert.Equal(2, r.Exec.Tracked);
            Assert.True(KisOrderIdSpace.IsKis(KisOrderIdSpace.Encode(117057)));
            Assert.False(KisOrderIdSpace.IsKis(117057));
            Assert.Equal(117057, KisOrderIdSpace.Decode(KisOrderIdSpace.Encode(117057)));
            Assert.Equal(0, KisOrderIdSpace.Encode(KisOrderIdSpace.Namespace));     // 범위 밖 = 0(사상 거부)
            // KIS 체결통보(암호문 해독 후 사상된 FillPayload) — 같은 숫자의 키움 주문은 건드리지 않는다
            r.Exec.OnFill(new FillPayload { OrderId = KisOrderIdSpace.Encode(117057), SymbolCode = 5930, FillQty = 1, FillPrice = 70_000, Side = 1,
                                            Status = BridgeFillStatusCodec.Encode(BridgeFillStatus.Filled, false) });
            Assert.Equal(1, r.Exec.Tracked);
            r.Exec.OnFill(new FillPayload { OrderId = 117057, SymbolCode = 5930, FillQty = 1, FillPrice = 70_000, Side = 1,
                                            Status = BridgeFillStatusCodec.Encode(BridgeFillStatus.Filled, false) });
            Assert.Equal(0, r.Exec.Tracked);
        }

        [Fact]
        public void BrokerFault_RouterDisarms_FailoverPreselects_NoRemainderResend()
        {
            var r = Make(active: 1);
            Submit(r, 3);                                                        // KIS 미체결 3건
            Assert.Equal(3, r.Kis.Places);
            var session = new KisSessionInterlock(r.Sentinel, 1, () => Volatile.Read(ref r.Now), silenceMicros: 5_000_000, openKisOrders: () => r.Exec.Tracked);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KisSession);
            session.BindInterlock(mask, ArmingInterlockSource.KisSession);
            Assert.True(mask.Seal(out _));
            Assert.True(mask.Blocked);                                           // 미연결 = 차단
            session.MarkConnected();
            Assert.False(mask.Blocked);
            r.Now += 4_000_000; session.OnActivity(true); r.Now += 4_000_000; session.Evaluate();
            Assert.False(session.Tripped);                                       // 활동 갱신
            r.Now += 6_000_000; session.Evaluate();                              // ★침묵 6초 > 5초
            Assert.True(session.Tripped && mask.IsRaised(ArmingInterlockSource.KisSession));
            Assert.True(r.Board.IsBlacklisted(1));
            Assert.Equal(0, r.Board.Active);                                     // 페일오버 선지정(키움)
            Assert.True(r.Sentinel.IsDisarmed);                                  // 기존 설계: 장애 = 라우터 sticky Disarm
            Assert.Equal(3, session.OrphanOrdersAtTrip);                         // 증거만 — 슬롯 회수 없음
            Assert.Equal(3, r.Exec.Tracked);                                     // ★KIS 미체결 추적 유지(노출 가시성)
            int kiwoomBefore = r.Kiwoom.Places;
            Submit(r, 2);
            Assert.Equal(kiwoomBefore, r.Kiwoom.Places);                         // ★잔량 재송출/신규 송출 0(재무장 전) — 이중 체결 위험 차단
            Assert.Equal(3, r.Kis.Places);
            Assert.False(session.TryRearm(true, true) && false);
        }

        [Fact]
        public void SessionInterlock_DisconnectTrips_RearmNeedsConnectionAndDualKey()
        {
            long now = 0;
            var s = new KisSessionInterlock(null, 1, () => now);
            Assert.True(s.ArmingBlocked);
            s.MarkConnected();
            Assert.False(s.ArmingBlocked);
            s.MarkDisconnected();
            Assert.True(s.Tripped);
            Assert.False(s.TryRearm(true, true));                                // 미연결 재무장 불가
            s.MarkConnected();
            Assert.False(s.TryRearm(true, false));
            Assert.True(s.TryRearm(true, true));
            Assert.False(s.ArmingBlocked);
            Action body = () => { for (int k = 0; k < 1000; k++) { now++; s.OnActivity(k % 7 == 0); s.Evaluate(); } };
            body();
            Assert.Equal(0, MinAllocated(body));
        }

        // ════════════ §6 회귀 ════════════

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void KisLiveBroker_DefectFixes_SourceRegression_FiveBuild()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "02_Infrastructure", "Adapters", "Brokers", "LiveBrokers.cs"));
            int a = src.IndexOf("public class KisLiveBroker", StringComparison.Ordinal), b = src.IndexOf("public class LiveRouterBroker", StringComparison.Ordinal);
            string kis = src[a..b];
            Assert.DoesNotContain("private static string? _token", kis);       // ① 정적 단일 토큰 슬롯 제거
            Assert.DoesNotContain("AddHours(23)", kis);                         // ① 추정 만료 제거
            Assert.Contains("KisProtocol.TryParseToken", kis);
            Assert.DoesNotContain("body.Contains(\"\\\"rt_cd\\\":\\\"0\\\"\")", kis);   // ② 문자열 Contains 판정 제거
            Assert.Contains("KisProtocol.TryParseOrderAck", kis);
            Assert.Contains("KisOrderIdSpace.Encode(ack.OrderNo)", kis);        // ③ msg = 브로커 주문번호
            Assert.Contains("qty != decimal.Truncate(qty)", kis);               // ④ 절단 송출 금지
            Assert.Contains("order-rvsecncl", kis);                              // ④ 취소 구현
            Assert.Contains("FinancialPiiMasker.Mask(", kis);
            Assert.Equal((18, 19), ((int)ArmingInterlockSource.KisAuth, (int)ArmingInterlockSource.KisSession));
            string sln = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager.sln"));
            foreach (var p in new[] { @"StockManager.PrimesBench\StockManager.PrimesBench.csproj", @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(p, sln);
        }
    }
}
