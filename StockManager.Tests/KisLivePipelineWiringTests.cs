// ============================================================================
// KisLivePipelineWiringTests — [KIS 실배선 · 추천 A]
//
//  §1 판독기(δ=0) — TTTC8908R 주문가능현금 · TTTC8434R 보유(0 행 제외 · 매도가능 > 보유 = 형식 이탈 · 버퍼) · 구독 응답 key/iv.
//  §2 KisBalanceQueryAdapter — 미설정 = 미결속·비차단 · 성공 = 한도 뷰 · ★실패 즉시 한도 0 · 연속 3회 = 비트 24 sticky · 만료 · 재무장 ·
//     한도 피드 연동(KIS 자식 한도 = 주문가능현금 / 지정가) · δ=0.
//  §3 [실결함] 구독 응답 key/iv 자동 장착 → 암호 체결통보 복호 → 실행 피드 + 자식 매트릭스(가중평균가) 수렴.
//  §4 KisRealtimeSession — 구독 2건(체결통보 HTS ID + 심박 체결가) · 조각 메시지 조립 · PINGPONG 반송(복사) · 초과 메시지 폐기 ·
//     단절 = 비트 19 Trip · 접속키 실패 재시도.
//  §5 [방향 B] 장중 3초 침묵 차단 · 장외 기본 임계 · KRX 정규장 판정 경계.
//  §6 호스트 결선(소스/IL) · 오중 빌드 회귀.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class KisLivePipelineWiringTests
    {
        private const long S = 100_000_000L;
        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
            }
            return min;
        }

        private static byte[] U8(string s) => Encoding.UTF8.GetBytes(s);
        private const string Buyable = "{\"rt_cd\":\"0\",\"msg_cd\":\"KIOK0000\",\"output\":{\"ord_psbl_cash\":\"1400000\",\"max_buy_amt\":\"1400000\",\"max_buy_qty\":\"20\"}}";
        private const string Holdings = "{\"rt_cd\":\"0\",\"output1\":[" +
            "{\"pdno\":\"005930\",\"prdt_name\":\"x\",\"hldg_qty\":\"10\",\"ord_psbl_qty\":\"7\"}," +
            "{\"pdno\":\"000660\",\"hldg_qty\":\"0\",\"ord_psbl_qty\":\"0\"}," +
            "{\"pdno\":\"035420\",\"hldg_qty\":\"3\",\"ord_psbl_qty\":\"3\"}],\"output2\":[{\"dnca_tot_amt\":\"1500000\"}]}";

        // ════════════ §1 판독기 ════════════

        [Fact]
        public void Parsers_Buyable_Holdings_SubscribeAck_FailClosed_ZeroAlloc()
        {
            Assert.True(KisProtocol.TryParseBuyable(U8(Buyable), out long cash));
            Assert.Equal(1_400_000, cash);
            Assert.False(KisProtocol.TryParseBuyable(U8(Buyable.Replace("\"rt_cd\":\"0\"", "\"rt_cd\":\"1\"")), out _));
            Assert.False(KisProtocol.TryParseBuyable(U8(Buyable.Replace("ord_psbl_cash", "ord_psbl_xxxx")), out _));   // 필드 없음
            Assert.False(KisProtocol.TryParseBuyable(U8(Buyable[..40]), out _));
            var rows = new KisHolding[8];
            Assert.Equal(2, KisProtocol.ParseHoldings(U8(Holdings), rows));                 // 보유 0 행 제외
            Assert.Equal((5930L, 10L, 7L), (rows[0].SymbolCode, rows[0].HeldQty, rows[0].SellableQty));
            Assert.Equal(-2, KisProtocol.ParseHoldings(U8(Holdings), new KisHolding[1]));
            Assert.Equal(-1, KisProtocol.ParseHoldings(U8(Holdings.Replace("\"ord_psbl_qty\":\"7\"", "\"ord_psbl_qty\":\"17\"")), rows));   // 매도가능 > 보유
            Assert.Equal(-1, KisProtocol.ParseHoldings(U8(Holdings.Replace("\"rt_cd\":\"0\"", "\"rt_cd\":\"7\"")), rows));

            var key = new byte[32]; var iv = new byte[16];
            string ack = "{\"header\":{\"tr_id\":\"H0STCNI0\",\"tr_key\":\"htsuser\",\"encrypt\":\"N\"},\"body\":{\"rt_cd\":\"0\",\"msg_cd\":\"OPSP0000\",\"msg1\":\"SUBSCRIBE SUCCESS\",\"output\":{\"iv\":\"ABCDEFGHIJKLMNOP\",\"key\":\"abcdefghijklmnopqrstuvwxyz012345\"}}}";
            Assert.True(KisProtocol.TryParseSubscribeAck(U8(ack), key, iv, out bool ok, out bool notice, out bool keys));
            Assert.Equal((true, true, true), (ok, notice, keys));
            Assert.Equal("abcdefghijklmnopqrstuvwxyz012345", Encoding.ASCII.GetString(key));
            Assert.True(KisProtocol.TryParseSubscribeAck(U8(ack.Replace("\"rt_cd\":\"0\"", "\"rt_cd\":\"1\"")), key, iv, out ok, out _, out _));
            Assert.False(ok);
            Assert.False(KisProtocol.TryParseSubscribeAck(U8("{\"header\":{\"tr_id\":\"PINGPONG\"}}"), key, iv, out _, out _, out _));   // 구독 응답 아님
            byte[] b = U8(Buyable), h = U8(Holdings);
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 200; i++) { KisProtocol.TryParseBuyable(b, out _); KisProtocol.ParseHoldings(h, rows); } }));
        }

        // ════════════ §2 잔고 조회 어댑터 ════════════

        private sealed class Clock { public long Now = 1_000_000; }

        [Fact]
        public void BalanceAdapter_Unconfigured_NotBound_NotBlocking()
        {
            var a = new KisBalanceQueryAdapter(null, configured: true);              // 전송 없음 = 미설정
            Assert.False(a.Configured);
            Assert.False(a.Bound);
            Assert.False(a.ArmingBlocked);                                         // ★KIS 미사용 배포에서 필수 비트 영구 차단 금지
            Assert.False(a.PollOnce());
            Assert.Equal(0, a.Failures);
        }

        [Fact]
        public void BalanceAdapter_Success_FailureZeroesImmediately_ThreeFailuresDisarm_Stale_Rearm()
        {
            var clk = new Clock();
            bool fail = false;
            var a = new KisBalanceQueryAdapter(q => fail ? null : U8(q == KisQuery.Buyable ? Buyable : Holdings), true, () => clk.Now);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KisLimitFeed);
            a.BindInterlock(mask, ArmingInterlockSource.KisLimitFeed);
            Assert.True(a.PollOnce());
            Assert.True(a.Bound);
            Assert.True(a.TryGetBuyHeadroomWon(out long won) && won == 1_400_000);
            Assert.True(a.TryGetSellableShares(5930, out long sh) && sh == 7);
            fail = true;
            Assert.False(a.PollOnce());
            Assert.False(a.Bound);                                                 // ★Zero Stale Limits — 첫 실패 즉시 한도 무효
            Assert.False(a.TryGetBuyHeadroomWon(out _));
            Assert.False(mask.IsRaised(ArmingInterlockSource.KisLimitFeed));
            a.PollOnce(); a.PollOnce();
            Assert.True(mask.IsRaised(ArmingInterlockSource.KisLimitFeed));         // ★연속 3회 = Disarm
            fail = false;
            Assert.True(a.PollOnce());
            Assert.False(a.Bound);                                                 // sticky — 성공해도 재무장 전 미결속
            Assert.False(a.TryRearm(true, false));
            Assert.True(a.TryRearm(true, true));
            Assert.True(a.Bound);
            clk.Now += 31_000_000;
            Assert.False(a.Bound);                                                 // 30초 만료
        }

        [Fact]
        public void BalanceAdapter_FeedsKisChildLimit_ZeroAlloc()
        {
            var a = new KisBalanceQueryAdapter(q => U8(q == KisQuery.Buyable ? Buyable : Holdings), true);
            Assert.True(a.PollOnce());
            var feed = new BrokerLimitFeedProvider(new IBrokerAccountView?[] { null, a });
            var dst = new BrokerSplitCap[2];
            feed.Fill(dst, 5930, OrderSide.Buy, 70_000 * S, parentReservedWon: 700_000);
            Assert.Equal((false, true, 20L), (dst[0].Available, dst[1].Available, dst[1].MaxShares));   // 1,400,000/70,000 = 20(KIS 는 부모 예약 되돌림 대상 아님)
            feed.Fill(dst, 5930, OrderSide.Sell, 70_000 * S, 0);
            Assert.Equal(7, dst[1].MaxShares);                                     // 매도 = 주문가능수량
            byte[] b = U8(Buyable), h = U8(Holdings);
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 100; i++) { a.Ingest(b, h); feed.Fill(dst, 5930, OrderSide.Buy, 70_000 * S, 0); } }));
        }

        // ════════════ §3 구독 응답 키 자동 장착 → 체결 수렴 ════════════

        private const string AckKey = "abcdefghijklmnopqrstuvwxyz012345", AckIv = "ABCDEFGHIJKLMNOP";
        private static string Ack(string trId = "H0STCNI0") =>
            "{\"header\":{\"tr_id\":\"" + trId + "\",\"tr_key\":\"htsuser\",\"encrypt\":\"N\"},\"body\":{\"rt_cd\":\"0\",\"msg_cd\":\"OPSP0000\",\"msg1\":\"SUBSCRIBE SUCCESS\",\"output\":{\"iv\":\"" + AckIv + "\",\"key\":\"" + AckKey + "\"}}}";

        private static string Notice(long odno, long qty, long px, int cntgYn = 2)
            => string.Join('^', new[] { "cust01", "12345678", odno.ToString("D10"), "", "02", "0", "00", "0",
                                        "005930", qty.ToString(), px.ToString(), "093001", "0", cntgYn.ToString(), "1", "91252", "10", "홍길동", "삼성전자" });

        private static byte[] NoticeFrame(long odno, long qty, long px)
        {
            using var aes = Aes.Create();
            aes.Key = Encoding.ASCII.GetBytes(AckKey);
            return U8("1|H0STCNI0|001|" + Convert.ToBase64String(aes.EncryptCbc(U8(Notice(odno, qty, px)), Encoding.ASCII.GetBytes(AckIv), PaddingMode.PKCS7)));
        }

        [Fact]
        public void SubscribeAck_InstallsKeys_EncryptedFills_ConvergeMatrixWithVwap()
        {
            var session = new KisSessionInterlock(null, 1, () => 1);
            var m = new ChildSlotTrackingMatrix(8);
            var fills = new List<FillPayload>();
            using var a = new KisOpenApiAdapter(session, onFill: f => { fills.Add(f); m.OnFill(f); });
            Assert.False(a.OnFrame(NoticeFrame(117057, 4, 70_000)));               // ★수정 전 운영 상태: 키 없음 = 폐기
            Assert.Equal(1, a.NoticeWithoutKey);
            Assert.True(a.OnFrame(U8(Ack("H0STCNT0"))));                             // 체결가 구독 응답 — 키 장착 대상 아님
            Assert.False(a.HasNoticeKeys);
            Assert.True(a.OnFrame(U8(Ack())));                                       // ★체결통보 구독 응답 → 키 자동 장착
            Assert.True(a.HasNoticeKeys);
            Assert.Equal(2, a.SubscribeAcks);

            long parent = 42;
            var plan = new[] { new ChildOrderPlan { ParentId = parent, ChildId = ChildIdSpace.Compose(parent, 0), Broker = 1, Shares = 10, LimitRaw = 70_100 * S } };
            Assert.True(m.Open(parent, 10, plan));
            m.MarkSent(plan[0].ChildId, KisOrderIdSpace.Encode(117057));
            Assert.True(a.OnFrame(NoticeFrame(117057, 4, 70_000)));
            Assert.True(a.OnFrame(NoticeFrame(117057, 6, 70_100)));
            Assert.Equal(2, fills.Count);
            Assert.Equal(KisOrderIdSpace.Encode(117057), fills[0].OrderId);
            Assert.True(m.TryGetParent(parent, out var s));
            Assert.Equal((ParentConvergence.AllFilled, 10L), (s.State, s.FilledQty));
            Assert.Equal(70_060, s.AvgFillPriceWon);                               // ★(4×70,000 + 6×70,100)/10 가중평균가

            Assert.False(a.OnFrame(U8(Ack().Replace("\"rt_cd\":\"0\"", "\"rt_cd\":\"9\""))));   // 구독 거부
            Assert.Equal(1, a.SubscribeRejects);
        }

        // ════════════ §4 실시간 세션 ════════════

        private sealed class FakeTransport : IKisWsTransport
        {
            public readonly Queue<(byte[] Data, bool End, bool Closed)> Script = new();
            public readonly List<string> Sent = new();
            public Uri? Connected;
            public bool Disposed;
            public Task ConnectAsync(Uri uri, CancellationToken ct) { Connected = uri; return Task.CompletedTask; }
            public Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) { lock (Sent) Sent.Add(Encoding.UTF8.GetString(payload.Span)); return Task.CompletedTask; }
            public ValueTask<(int Count, bool EndOfMessage, bool Closed)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
            {
                if (Script.Count == 0) return new((0, true, true));
                var (d, end, closed) = Script.Dequeue();
                if (closed) return new((0, true, true));
                int n = Math.Min(d.Length, buffer.Length);
                d.AsSpan(0, n).CopyTo(buffer.Span);
                return new((n, end, false));
            }
            public void Dispose() => Disposed = true;
        }

        [Fact]
        public async Task Session_Subscribes_AssemblesChunks_EchoesPingPong_DisconnectTrips()
        {
            var clk = new Clock();
            var interlock = new KisSessionInterlock(null, 1, () => clk.Now);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KisSession);
            interlock.BindInterlock(mask, ArmingInterlockSource.KisSession);
            var fills = new List<FillPayload>();
            KisRealtimeSession? rt = null;
            using var adapter = new KisOpenApiAdapter(interlock, send: f => rt!.TrySendFrame(f), onFill: fills.Add);
            var t = new FakeTransport();
            byte[] noticeA = NoticeFrame(117057, 4, 70_000);
            t.Script.Enqueue((U8(Ack()), true, false));
            t.Script.Enqueue((noticeA[..20], false, false));                        // 조각 1
            t.Script.Enqueue((noticeA[20..], true, false));                         // 조각 2 — 완성
            t.Script.Enqueue((U8("{\"header\":{\"tr_id\":\"PINGPONG\",\"datetime\":\"20261007093000\"}}"), true, false));
            t.Script.Enqueue((Array.Empty<byte>(), true, true));                    // 서버 종료
            int factoryCalls = 0;
            rt = new KisRealtimeSession(adapter, interlock, () => { factoryCalls++; return factoryCalls == 1 ? t : new FakeTransport(); },
                                        () => "approval-key-1", "htsuser", vts: false, reconnectDelay: TimeSpan.FromMilliseconds(10));
            using var cts = new CancellationTokenSource();
            var run = Task.Run(() => rt.RunAsync(cts.Token));
            Assert.True(SpinWait.SpinUntil(() => rt.Disconnects >= 1, 10_000));
            cts.Cancel();
            await run;
            Assert.Equal(KisRealtimeSession.RealUri, t.Connected);
            Assert.Contains(t.Sent, x => x.Contains("\"tr_id\":\"H0STCNI0\"") && x.Contains("\"tr_key\":\"htsuser\"") && x.Contains("approval-key-1"));
            Assert.Contains(t.Sent, x => x.Contains("\"tr_id\":\"H0STCNT0\"") && x.Contains("\"tr_key\":\"005930\""));   // ★심박 체결가 구독
            Assert.Contains(t.Sent, x => x.Contains("PINGPONG"));                   // 반송(복사본)
            Assert.Single(fills);                                                  // 조각 조립 후 1건
            Assert.Equal(4, fills[0].FillQty);
            Assert.True(interlock.Tripped);                                        // ★단절 = 비트 19 Trip
            Assert.True(mask.IsRaised(ArmingInterlockSource.KisSession));
            Assert.True(t.Disposed);
        }

        [Fact]
        public async Task Session_ApprovalFailure_RetriesWithoutConnecting()
        {
            var interlock = new KisSessionInterlock(null, 1, () => 1);
            using var adapter = new KisOpenApiAdapter(interlock);
            int factory = 0;
            var rt = new KisRealtimeSession(adapter, interlock, () => { factory++; return new FakeTransport(); }, () => null, "htsuser", vts: true,
                                            reconnectDelay: TimeSpan.FromMilliseconds(5),
                                            approvalBackoff: new KisApprovalBackoff(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(4)));   // 발급 백오프 축소(재시도 계수 검증)
            Assert.Equal("H0STCNI9", rt.NoticeTrId);                               // 모의 체결통보 TR
            using var cts = new CancellationTokenSource();
            var run = Task.Run(() => rt.RunAsync(cts.Token));
            Assert.True(SpinWait.SpinUntil(() => rt.ApprovalFailures >= 3, 10_000));
            cts.Cancel();
            await run;
            Assert.Equal(0, factory);                                              // 접속키 없으면 접속 시도 0
            Assert.True(interlock.ArmingBlocked);                                  // 미연결 = 차단 유지
            Assert.Throws<ArgumentException>(() => new KisRealtimeSession(adapter, interlock, () => new FakeTransport(), () => "k", "", vts: false));
        }

        // ════════════ §5 방향 B — 장중 침묵 ════════════

        [Fact]
        public void MarketHoursSilence_3s_Trips_OffHours_DefaultThreshold_SessionBoundaries()
        {
            var clk = new Clock();
            bool market = true;
            var s = new KisSessionInterlock(null, 1, () => clk.Now, marketHours: () => market);
            s.MarkConnected();
            clk.Now += 2_900_000; s.Evaluate();
            Assert.False(s.Tripped);
            clk.Now += 200_000; s.Evaluate();
            Assert.True(s.Tripped);                                                // ★장중 3초 침묵 = Trip
            var o = new KisSessionInterlock(null, 1, () => clk.Now, marketHours: () => false);
            o.MarkConnected();
            clk.Now += 30_000_000; o.Evaluate();
            Assert.False(o.Tripped);                                               // 장외 = 기본 60초
            var thrower = new KisSessionInterlock(null, 1, () => clk.Now, marketHours: () => throw new InvalidOperationException());
            thrower.MarkConnected();
            clk.Now += 3_100_000; thrower.Evaluate();
            Assert.True(thrower.Tripped);                                          // 판정 불가 = 장중 취급(엄격)

            // KST = UTC+9 · 월요일 2026-10-05
            Assert.False(KisSessionInterlock.IsKrxRegularSession(new DateTime(2026, 10, 4, 23, 59, 0, DateTimeKind.Utc)));   // 08:59
            Assert.True(KisSessionInterlock.IsKrxRegularSession(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc)));      // 09:00
            Assert.True(KisSessionInterlock.IsKrxRegularSession(new DateTime(2026, 10, 5, 6, 19, 0, DateTimeKind.Utc)));     // 15:19
            Assert.False(KisSessionInterlock.IsKrxRegularSession(new DateTime(2026, 10, 5, 6, 20, 0, DateTimeKind.Utc)));    // 15:20
            Assert.False(KisSessionInterlock.IsKrxRegularSession(new DateTime(2026, 10, 3, 1, 0, 0, DateTimeKind.Utc)));     // 토요일 10:00
        }

        // ════════════ §6 호스트 결선 ════════════

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void Host_KisBalancePoll_RealtimeGated_FillFanout_Wired()
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var targets = new MethodBase[]
            {
                typeof(KisBalanceQueryAdapter).GetConstructors()[0],
                typeof(KisBalanceQueryAdapter).GetMethod(nameof(KisBalanceQueryAdapter.BindInterlock))!,
                typeof(KisBalanceQueryAdapter).GetMethod(nameof(KisBalanceQueryAdapter.PollOnce))!,
                typeof(KisRealtimeSession).GetConstructors()[0],
                typeof(KisRealtimeSession).GetMethod(nameof(KisRealtimeSession.RunAsync))!,
                typeof(KisSessionInterlock).GetMethod(nameof(KisSessionInterlock.Evaluate))!,
            };
            var found = new bool[targets.Length];
            var types = new List<Type> { host };
            types.AddRange(host.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public));
            foreach (var ty in types)
            {
                var all = new List<MethodBase>(ty.GetMethods(AllF));
                all.AddRange(ty.GetConstructors(AllF));
                foreach (var mth in all)
                {
                    var il = mth.GetMethodBody()?.GetILAsByteArray();
                    if (il is null) continue;
                    for (int i = 0; i + 4 < il.Length; i++)
                        if (il[i] is 0x28 or 0x6F or 0x73)
                            try
                            {
                                var mb = mth.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                                for (int k = 0; k < targets.Length; k++) if (mb == targets[k]) found[k] = true;
                            }
                            catch (ArgumentException) { }
                }
            }
            for (int k = 0; k < targets.Length; k++) Assert.True(found[k], $"결선 누락: {targets[k].DeclaringType!.Name}.{targets[k].Name}");
            string form = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("MS2E.Core.ArmingInterlockSource.KisLimitFeed,", form);   // 필수 비트
            Assert.Contains("AccountSyncBrokerView(accountSync, () => bridge.IsProductionProxy), kisBalance }", form);
            Assert.Contains("new StockManager.Adapters.BrokerLimitFeedProvider(\n                brokerViews,", form.Replace("\r\n", "\n"));   // 한도 피드 = 같은 뷰 배열   // KIS 한도 뷰(인덱스 1) · 키움 뷰 = 실 COM 링크만
            Assert.Contains("cfg.GetBool(\"Kis.RealtimeEnabled\", false)", form);    // 실시간 접속은 명시 설정
            Assert.Contains("onFill: f => { execFeed.OnFill(f); childMatrix.OnFill(f); kisBalance.OnKisFill(f); dashHub.OnFill(f, 1); }", form);   // 실행 피드 + 매트릭스 + [KIS VTS] 포지션 원장
            Assert.Contains("kisSession.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.KisSession)", form);
            Assert.Equal(24, (int)ArmingInterlockSource.KisLimitFeed);
        }
    }
}
