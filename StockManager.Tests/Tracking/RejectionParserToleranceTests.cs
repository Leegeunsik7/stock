// ============================================================================
// RejectionParserToleranceTests — [v12.18.44-DEV] 공개 규격 기반 거부 파서 구조 내성 · 미분류 통보 fail-closed(비트 23)
//
//  ★정직: 이 파일의 페이로드는 전부 KIS Developers 공개 필드 순서(H0STCNI0/9 · order-cash 응답)로 '합성'한 변칙 픽스처다 —
//    실 VTS 캡처가 아니다(계정 발급 후 VtsOrderRejectionProbeTests 실측 대조 예정 · HANDOVER §133).
//
//   §1 체결통보(^ 위치 규격 · AES 암호문 → 실 KisOpenApiAdapter → 호스트와 같은 onFill/onNoticeFault 결선 → 매트릭스):
//      변칙 5종 — ① 필드 공백/탭 패딩 ② 구분자 주변 공백 + CRLF 종결 ③ 미지 후행 메타 필드 ④ 필드 순서 셔플 ⑤ 정의 밖 거부값/필드 부족/
//      복호 불가/키 없음 — 전부 예외 0 · 그 OnFrame 호출 안에서 0x00800000 래치. ①~③ = 거부로 판독(UntrackedRejects) · ④⑤ = 미분류(UnclassifiedNotices).
//      오탐 0: 공백/미지 필드가 섞인 접수/체결 통보 = 정상 분류 · 래치 0.
//   §2 REST(JSON · Utf8JsonReader 순서 비의존): 거부 본문 4멤버 24순열 · 공백/개행 · 미지 중첩 필드/배열 · 중복 rt_cd · BOM · 절단 = 성공 판정 0 ·
//      성공 본문 24순열 = ODNO 판독(미탐 0).
//   §3 퍼즈: 공백 주입/미지 필드/절단 무작위 변칙 = 항상 래치 · 임의 바이트 통보 = 예외 0 + (방출 1 ∨ 미분류 1) 정확히 하나 · 키움 브리지 임의 바이트 = 예외 0
//   §4 δ=0: 변칙 통보 10만 프레임(래치/재무장 순환) + 변칙 REST 본문 10만 판독 힙 0B(Release · 3창 최솟값)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Tracking
{
    [Collection("ConcurrencyStress")]
    public sealed class RejectionParserToleranceTests
    {
        private const long Bit23Mask = 0x00800000L;
        private static readonly byte[] Key = Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz012345"), Iv = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOP");
        private readonly ITestOutputHelper _out;
        public RejectionParserToleranceTests(ITestOutputHelper o) => _out = o;

        private sealed class Rig : IDisposable
        {
            public readonly ArmingInterlockBitmask Mask = new(ArmingInterlockSource.ChildSlotRejection);
            public readonly ChildSlotTrackingMatrix M = new(16, () => 1_000_000);
            public readonly List<FillPayload> Fills = new();
            public readonly KisOpenApiAdapter Kis;
            public Rig(bool keys = true)
            {
                M.BindInterlock(Mask, ArmingInterlockSource.ChildSlotRejection);
                Assert.True(Mask.Seal(out _));
                Kis = new KisOpenApiAdapter(new KisSessionInterlock(null, 1, () => 1), onFill: f => { Fills.Add(f); M.OnFill(f); }, nowTicks: () => 7,
                                            onNoticeFault: M.OnUnclassifiedNotice);
                if (keys) Kis.SetNoticeKeys(Key, Iv);
            }
            public bool Latched => M.Tripped && (Mask.Mask & Bit23Mask) == Bit23Mask;
            public void Dispose() => Kis.Dispose();
        }

        // 공개 규격 H0STCNI0 필드 순서: 0 고객 · 1 계좌 · 2 주문번호 · 3 원주문 · 4 매도매수 · 5 정정 · 6 · 7 · 8 종목 · 9 체결량 · 10 체결가 · 11 시각 ·
        //   12 거부여부 · 13 체결여부 · 14 접수여부 · 15 지점 · 16 주문수량 · (17~ 이름 등 부가)
        private static string[] Fields(long odno, int refuse, int cntgYn, long qty = 0, long px = 0)
            => new[] { "cust01", "12345678", odno.ToString("D10"), "", "02", "0", "00", "0", "005930", qty.ToString(), px.ToString(), "093001",
                       refuse.ToString(), cntgYn.ToString(), "1", "91252", "10", "홍길동", "삼성전자" };

        private static byte[] Encrypted(string plain)
        {
            using var aes = Aes.Create();
            aes.Key = Key;
            return Encoding.UTF8.GetBytes("1|H0STCNI9|001|" + Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(plain), Iv, PaddingMode.PKCS7)));
        }

        private static string Reject(long odno = 117_058) => string.Join('^', Fields(odno, 1, 1));

        // ════════════ §1 체결통보 변칙 ════════════

        public static IEnumerable<object[]> ReadableRejectVariants()
        {
            yield return new object[] { "① 필드 공백/탭 패딩", string.Join('^', Fields(117_058, 1, 1).Select((f, i) => (i % 2 == 0 ? " " : "\t") + f + "  ")) };
            yield return new object[] { "② 구분자 주변 공백 + CRLF", string.Join(" ^ ", Fields(117_058, 1, 1)) + "\r\n" };
            yield return new object[] { "③ 미지 후행 메타 필드 12개", string.Join('^', Fields(117_058, 1, 1).Concat(new[] { "X-META=1", "{\"k\":1}", "", "  ", "ZZZ", "0", "-1", "9999999999999999999999", "가나다", "\t", "end", "" })) };
            yield return new object[] { "①+② 혼합(앞 공백 · 개행 패딩)", string.Join('^', Fields(117_058, 1, 1).Select(f => "\n " + f + " \r")) };
        }

        [Theory]
        [MemberData(nameof(ReadableRejectVariants))]
        public void Readable_Anomalous_Reject_Notice_Parses_As_Rejected_And_Latches_Within_Frame(string label, string plain)
        {
            using var r = new Rig();
            Assert.False(r.Latched);
            bool handled = r.Kis.OnFrame(Encrypted(plain));
            Assert.True(handled, label);
            Assert.True(r.Latched, label);                                                            // 그 프레임 처리 안에서 래치(1틱)
            Assert.Equal(BridgeFillStatus.Rejected, BridgeFillStatusCodec.Decode(r.Fills.Single().Status, out _));
            Assert.Equal(KisOrderIdSpace.Encode(117_058), r.Fills[0].OrderId);
            Assert.Equal(1, r.M.UntrackedRejects);
            Assert.Equal(0, r.M.UnclassifiedNotices);
            Assert.Equal(0, r.Kis.NoticeFaults);
        }

        public static IEnumerable<object[]> UnreadableNoticeVariants()
        {
            var f = Fields(117_058, 1, 1);
            yield return new object[] { "④ 필드 순서 역순", string.Join('^', Enumerable.Reverse(f)) };
            var swap = (string[])f.Clone(); (swap[4], swap[12]) = (swap[12], swap[4]);                // 매도매수 ↔ 거부여부 — 거부값 '02' = 정의 밖
            yield return new object[] { "④ 매도매수 ↔ 거부여부 교환", string.Join('^', swap) };
            var swap2 = (string[])f.Clone(); (swap2[2], swap2[8]) = (swap2[8], swap2[2]);              // 주문번호 ↔ 종목코드 — 종목 10자리 = 형식 이탈
            yield return new object[] { "④ 주문번호 ↔ 종목 교환", string.Join('^', swap2) };
            var y = (string[])f.Clone(); y[12] = "Y";
            yield return new object[] { "⑤ 거부여부 'Y'(정의 밖 표기)", string.Join('^', y) };
            var two = (string[])f.Clone(); two[12] = "2";
            yield return new object[] { "⑤ 거부여부 2(수정 전 = 접수 오분류)", string.Join('^', two) };
            var rc = (string[])f.Clone(); rc[5] = "7";
            yield return new object[] { "⑤ 정정구분 7(정의 밖)", string.Join('^', rc) };
            yield return new object[] { "⑤ 필드 부족(12개)", string.Join('^', f.Take(12)) };
            yield return new object[] { "⑤ 빈 본문 공백만", "   \r\n" };
            yield return new object[] { "⑤ 구분자 전환('|')", string.Join('|', f) };
        }

        [Theory]
        [MemberData(nameof(UnreadableNoticeVariants))]
        public void Unreadable_Notice_Is_Unclassified_And_Latches_Within_Frame(string label, string plain)
        {
            using var r = new Rig();
            var ex = Record.Exception(() => r.Kis.OnFrame(Encrypted(plain)));
            Assert.Null(ex);
            Assert.True(r.Latched, label);
            Assert.Empty(r.Fills);                                                                    // 추측 분류 방출 0
            Assert.Equal(1, r.M.UnclassifiedNotices);
            Assert.Equal(1, r.Kis.NoticeFaults);
        }

        [Fact]
        public void Undecryptable_And_Keyless_Notices_Latch()
        {
            using (var r = new Rig())
            {
                r.Kis.OnFrame(Encoding.ASCII.GetBytes("1|H0STCNI9|001|@@@not-base64@@@"));          // Base64 이탈
                Assert.True(r.Latched);
                Assert.Equal(1, r.M.UnclassifiedNotices);
            }
            using (var r = new Rig())
            {
                var b = Encrypted(Reject());
                b[^5] = (byte)(b[^5] == (byte)'A' ? 'B' : 'A');                                       // 마지막 블록 변조(패딩 비트 없는 완전 문자) → 패딩/평문 붕괴
                r.Kis.OnFrame(b);
                Assert.True(r.Latched);
                Assert.Empty(r.Fills.Where(x => BridgeFillStatusCodec.Decode(x.Status, out _) != BridgeFillStatus.Rejected));
            }
            using (var r = new Rig(keys: false))                                                       // 구독 응답(키) 이전 암호 통보
            {
                r.Kis.OnFrame(Encrypted(Reject()));
                Assert.True(r.Latched);
                Assert.Equal(1, r.Kis.NoticeWithoutKey);
                Assert.Equal(1, r.M.UnclassifiedNotices);
            }
        }

        [Fact]
        public void Plaintext_Notice_Frame_Is_Equally_Tolerant()
        {
            using var r = new Rig();
            r.Kis.OnFrame(Encoding.UTF8.GetBytes("0|H0STCNI9|001|" + string.Join(" ^ ", Fields(117_058, 1, 1)) + "\r\n"));
            Assert.True(r.Latched);
            Assert.Equal(1, r.M.UntrackedRejects);
        }

        [Fact]
        public void Anomalous_But_Benign_Notices_Do_Not_Latch()
        {
            using var r = new Rig();
            string Pad(string[] f) => string.Join(" ^ ", f.Select(x => "\t" + x + " ").Concat(new[] { "X-META=1", "{}" })) + "\r\n";
            Assert.True(r.Kis.OnFrame(Encrypted(Pad(Fields(117_100, 0, 1)))));                        // 접수
            Assert.True(r.Kis.OnFrame(Encrypted(Pad(Fields(117_101, 0, 2, qty: 10, px: 71_900)))));    // 체결
            Assert.False(r.M.Tripped);
            Assert.Equal(0, r.Mask.Mask & Bit23Mask);
            Assert.Equal(BridgeFillStatus.Accepted, BridgeFillStatusCodec.Decode(r.Fills[0].Status, out _));
            Assert.Equal(BridgeFillStatus.Filled, BridgeFillStatusCodec.Decode(r.Fills[1].Status, out _));
            Assert.Equal((10L, 71_900.0), (r.Fills[1].FillQty, r.Fills[1].FillPrice));
            Assert.Equal(0, r.Kis.NoticeFaults);
        }

        [Fact]
        public void Foreign_Account_Notice_Is_Dropped_Not_Unclassified()
        {
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.ChildSlotRejection);
            var m = new ChildSlotTrackingMatrix(16, () => 1_000_000);
            m.BindInterlock(mask, ArmingInterlockSource.ChildSlotRejection);
            using var kis = new KisOpenApiAdapter(new KisSessionInterlock(null, 1, () => 1), onFill: m.OnFill, nowTicks: () => 7,
                                                  accountCano: "99999999", onNoticeFault: m.OnUnclassifiedNotice);
            kis.SetNoticeKeys(Key, Iv);
            Assert.False(kis.OnFrame(Encrypted(string.Join('^', Fields(117_058, 0, 1)))));              // 판독 성공 · 타 계좌 접수 = 폐기
            Assert.Equal(1, kis.ForeignAccountNotices);
            Assert.False(m.Tripped);
            Assert.Equal(0, kis.NoticeFaults);
        }

        [Fact]
        public void Host_Wires_Notice_Fault_Into_Child_Matrix()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(d!.FullName, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("onNoticeFault: childMatrix.OnUnclassifiedNotice,", src);
        }

        // ════════════ §2 REST(JSON) ════════════

        private static readonly string[] RejectMembers = { "\"rt_cd\":\"1\"", "\"msg_cd\":\"APBK0000\"", "\"msg1\":\"주문 거부(합성 픽스처)\"", "\"output\":{\"KRX_FWDG_ORD_ORGNO\":\"\",\"ODNO\":\"\",\"ORD_TMD\":\"\"}" };
        private static readonly string[] AcceptMembers = { "\"rt_cd\":\"0\"", "\"msg_cd\":\"APBK0013\"", "\"msg1\":\"주문 전송 완료(합성 픽스처)\"", "\"output\":{\"ORD_TMD\":\"093001\",\"ODNO\":\"0000117058\",\"KRX_FWDG_ORD_ORGNO\":\"91252\"}" };

        private static IEnumerable<string[]> Permutations(string[] a)
        {
            if (a.Length <= 1) { yield return a; yield break; }
            for (int i = 0; i < a.Length; i++)
                foreach (var rest in Permutations(a.Where((_, j) => j != i).ToArray()))
                    yield return new[] { a[i] }.Concat(rest).ToArray();
        }

        [Fact]
        public void Rest_Reject_Body_All_24_Permutations_And_Whitespace_Never_Succeed()
        {
            int n = 0;
            foreach (var p in Permutations(RejectMembers))
            {
                foreach (string json in new[] { "{" + string.Join(",", p) + "}", "{\r\n\t" + string.Join(" ,\r\n\t ", p) + "\r\n}\r\n", "  { " + string.Join(" , ", p) + " }  " })
                {
                    Assert.True(KisProtocol.TryParseOrderAck(Encoding.UTF8.GetBytes(json), out var ack), json);   // 형식 판독 성공(rt_cd 존재)
                    Assert.False(ack.Ok, json);
                    n++;
                }
            }
            Assert.Equal(72, n);
        }

        [Fact]
        public void Rest_Accept_Body_All_24_Permutations_Read_OrderNo()
        {
            foreach (var p in Permutations(AcceptMembers))
            {
                string json = "{\n  " + string.Join(",\n  ", p) + "\n}";
                Assert.True(KisProtocol.TryParseOrderAck(Encoding.UTF8.GetBytes(json), out var ack), json);
                Assert.True(ack.Ok, json);
                Assert.Equal((117_058L, 91_252L), (ack.OrderNo, ack.OrgNo));
            }
        }

        [Theory]
        [InlineData("{\"trace\":{\"rt_cd\":\"0\",\"output\":{\"ODNO\":\"1\"}},\"rt_cd\":\"1\",\"output\":{\"ODNO\":\"\"}}")]   // 미지 중첩 객체 안 rt_cd/ODNO = 무시
        [InlineData("{\"rt_cd\":\"1\",\"extra\":[{\"rt_cd\":\"0\"},{\"ODNO\":\"5\"}],\"output\":{\"ODNO\":\"\"}}")]          // 미지 배열
        [InlineData("{\"rt_cd\":\"1\",\"output\":{\"ODNO\":\"\",\"meta\":{\"ODNO\":\"9\"}}}")]                                // output 안 미지 중첩
        [InlineData("{\"rt_cd\":\"1\",\"rt_cd\":\"0\",\"output\":{\"ODNO\":\"117058\"}}")]                                   // ★중복 rt_cd(거부 → 성공) = 모호 → 실패
        [InlineData("{\"rt_cd\":\"0\",\"rt_cd\":\"1\",\"output\":{\"ODNO\":\"117058\"}}")]
        [InlineData("{\"rt_cd\":\"0\",\"rt_cd\":\"0\",\"output\":{\"ODNO\":\"117058\"}}")]                                   // 동일 중복도 모호(fail-closed)
        [InlineData("{\"rt_cd\":\" 0\",\"output\":{\"ODNO\":\"117058\"}}")]                                                   // 값 내부 공백 = "0" 아님
        [InlineData("{\"rt_cd\":0,\"output\":{\"ODNO\":\"117058\"}}")]                                                        // 숫자형 rt_cd = 형식 이탈
        [InlineData("{\"rt_cd\":\"0\",\"output\":{\"ODNO\":\"117058\"")]                                                      // 절단
        [InlineData("{\"output\":{\"ODNO\":\"117058\"},\"msg1\":\"rt_cd 없음\"}")]
        [InlineData("﻿{\"rt_cd\":\"1\",\"output\":{\"ODNO\":\"\"}}")]                                                  // BOM
        [InlineData("")]
        [InlineData("<html>502 Bad Gateway</html>")]
        public void Rest_Anomalous_Bodies_Never_Report_Success(string json)
        {
            var ex = Record.Exception(() => KisProtocol.TryParseOrderAck(Encoding.UTF8.GetBytes(json), out _));
            Assert.Null(ex);
            KisProtocol.TryParseOrderAck(Encoding.UTF8.GetBytes(json), out var ack);
            Assert.False(ack.Ok, json);
        }

        // ════════════ §3 퍼즈 ════════════

        private static readonly string[] Ws = { " ", "\t", "\r", "\n", "  ", "\r\n" };

        [Fact]
        public void Fuzz_Whitespace_UnknownFields_Truncation_Always_Latch()
        {
            var rng = new Random(20261021);
            int readable = 0, unclassified = 0;
            for (int run = 0; run < 2_000; run++)
            {
                var f = Fields(117_058, 1, 1).Select(x => rng.Next(3) == 0 ? Ws[rng.Next(Ws.Length)] + x + Ws[rng.Next(Ws.Length)] : x).ToList();
                int extra = rng.Next(0, 8);
                for (int k = 0; k < extra; k++) f.Add(rng.Next(2) == 0 ? "META" + k : Ws[rng.Next(Ws.Length)]);
                if (rng.Next(5) == 0) f = f.Take(rng.Next(0, 17)).ToList();                          // 필수 필드 이전 절단
                using var r = new Rig();
                Assert.Null(Record.Exception(() => r.Kis.OnFrame(Encrypted(string.Join('^', f)))));
                Assert.True(r.Latched, string.Join('^', f));
                readable += (int)r.M.UntrackedRejects; unclassified += (int)r.M.UnclassifiedNotices;
                Assert.Equal(1, r.M.UntrackedRejects + r.M.UnclassifiedNotices);
            }
            _out.WriteLine($"[퍼즈] 2,000 변칙 거부 통보 = 거부 판독 {readable} · 미분류 {unclassified} · 미래치 0");
            Assert.True(readable > 0 && unclassified > 0);
        }

        [Fact]
        public void Fuzz_Random_Bytes_Notice_Never_Throws_And_Is_Either_Emitted_Or_Unclassified()
        {
            var rng = new Random(7);
            using var r = new Rig();
            for (int run = 0; run < 5_000; run++)
            {
                var plain = new byte[rng.Next(0, 200)];
                rng.NextBytes(plain);
                for (int k = 0; k < plain.Length; k++) if (rng.Next(6) == 0) plain[k] = (byte)'^';
                byte[] frame;
                using (var aes = Aes.Create()) { aes.Key = Key; frame = Encoding.ASCII.GetBytes("1|H0STCNI9|001|" + Convert.ToBase64String(aes.EncryptCbc(plain, Iv, PaddingMode.PKCS7))); }
                long e0 = r.Kis.FillsEmitted, u0 = r.Kis.NoticeFaults;
                Assert.Null(Record.Exception(() => r.Kis.OnFrame(frame)));
                Assert.Equal(1, (r.Kis.FillsEmitted - e0) + (r.Kis.NoticeFaults - u0));                 // 무음 폐기 0
            }
            Assert.True(r.Latched);
        }

        [Fact]
        public void Fuzz_Kiwoom_Bridge_Random_Bytes_Never_Throw()
        {
            var rng = new Random(11);
            var (mask, m) = (new ArmingInterlockBitmask(ArmingInterlockSource.ChildSlotRejection), new ChildSlotTrackingMatrix(16, () => 1));
            m.BindInterlock(mask, ArmingInterlockSource.ChildSlotRejection);
            for (int run = 0; run < 5_000; run++)
            {
                var b = new byte[rng.Next(0, 96)];
                rng.NextBytes(b);
                Assert.Null(Record.Exception(() => { if (BridgeFillReader.TryReadFill(b, out var f)) m.OnFill(f); }));
            }
        }

        // ════════════ §4 δ=0 ════════════

        [Fact]
        public void HundredThousand_Anomalous_Notices_And_Rest_Bodies_Allocate_Zero_Bytes()
        {
            using var r = new Rig();
            var unread = UnreadableNoticeVariants().Select(o => (string)o[1]).ToArray();
            var frames = ReadableRejectVariants().Select(o => Encrypted((string)o[1]))
                         .Concat(unread.Take(4).Select(Encrypted))
                         .Append(Encrypted(string.Join(" ^ ", Fields(117_100, 0, 1)) + "\r\n"))
                         .Append(Encrypted(string.Join('^', Fields(117_101, 0, 2, qty: 10, px: 71_900).Concat(new[] { "X", "Y" }))))
                         .Take(8).ToArray();
            Assert.Equal(8, frames.Length);
            var bodies = Permutations(RejectMembers).Take(4).Select(p => Encoding.UTF8.GetBytes("{\r\n " + string.Join(" ,\n\t", p) + " }"))
                         .Concat(Permutations(AcceptMembers).Take(3).Select(p => Encoding.UTF8.GetBytes("{" + string.Join(",", p) + "}")))
                         .Append(Encoding.UTF8.GetBytes("{\"rt_cd\":\"1\",\"rt_cd\":\"0\",\"output\":{\"ODNO\":\"117058\"}}")).ToArray();
            var mem = frames.Select(f => (ReadOnlyMemory<byte>)f).ToArray();
            long sink = 0;
            void Run()
            {
                for (int i = 0; i < 100_000; i++)
                {
                    r.Kis.OnFrame(mem[i & 7]);
                    if (KisProtocol.TryParseOrderAck(bodies[i & 7], out var ack) && ack.Ok) sink += ack.OrderNo;
                    if ((i & 1023) == 0) r.M.TryRearm(true, true);
                }
            }
            Run();                                                                                    // 예열(JIT)
            r.Fills.Capacity = r.Fills.Count + 200_000;                                               // 테스트 수집 리스트 성장 = 측정 제외
            long min = long.MaxValue;
            for (int w = 0; w < 3; w++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Run();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            _out.WriteLine($"[δ] 변칙 통보 10만 + 변칙 REST 10만 × 3창 최소 = {min}B · 미분류 {r.M.UnclassifiedNotices} · 거부 {r.M.UntrackedRejects} · sink {sink}");
            Assert.Equal(0, min);
            Assert.True(r.Latched);
            Assert.Equal(4 * 50_000, r.M.UnclassifiedNotices);
        }
    }
}
