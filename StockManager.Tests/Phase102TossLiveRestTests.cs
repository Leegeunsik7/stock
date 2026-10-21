// ============================================================================
// Phase102TossLiveRestTests — v10.2.1 토스증권 Live REST 실통신 배선 적대적 검증(100% 단언)
//   🧩 TossJsonParser: 계좌+보유 δ=0 파싱 정확성 + ★무할당(δ=0) 증명(1바이트 GC 유출 0)
//   🛡 TossLiveApiGovernor(Think-Beyond): 토큰버킷 rate-limit·429 락아웃·서킷브레이커·δ=0
//   🔒 TossOpenApiLiveAdapter: 무토큰/무자격증명 fail-closed(네트워크 미발생)·발주 하드 veto
//   ⛔ liveArmed:false: 실통신 잔고 조회와 무관하게 실물 발주 100% Veto(0 send)
//   ※ 실 Toss 서버 호출은 테스트에서 절대 수행하지 않음(무토큰 short-circuit·Start/RefreshNow 미호출).
// ============================================================================
using System;
using System.IO;
using System.Text;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class Phase102TossLiveRestTests
    {
        // 파서 프리셋 스키마에 맞춘 현실적 응답(계좌 2필드군 + 국내/해외 보유 2건)
        private static readonly byte[] AccountJson = Encoding.UTF8.GetBytes(
            "{\"account\":{\"totalEval\":12345678,\"totalPnl\":234567,\"deposit\":1000000,\"buyingPower\":900000}," +
            "\"holdings\":[" +
            "{\"symbol\":\"005930\",\"qty\":10,\"avgPrice\":70000,\"curPrice\":72000,\"pnl\":20000,\"overseas\":false}," +
            "{\"symbol\":\"AAPL\",\"qty\":5,\"avgPrice\":180,\"curPrice\":185,\"pnl\":25,\"overseas\":true}]}");

        private const long Sec = TimeSpan.TicksPerSecond;

        // ══ 🧩 TossJsonParser ════════════════════════════════════════════════
        [Fact]
        public void Parser_TossAccount_Parses_Correctly()
        {
            Span<TossHolding> h = stackalloc TossHolding[16];
            bool ok = TossJsonParser.TryParseAccount(AccountJson, nowMicros: 42, out var acct, h, out int n);
            Assert.True(ok);
            Assert.Equal(12345678L, acct.TotalEvalRaw);
            Assert.Equal(234567L, acct.TotalPnlRaw);
            Assert.Equal(1000000L, acct.DepositRaw);
            Assert.Equal(900000L, acct.BuyingPowerRaw);
            Assert.Equal(2, n);
            Assert.Equal(2, acct.HoldingCount);
            Assert.Equal(42L, acct.AsOfMicros);
            Assert.Equal(10L, h[0].Quantity);
            Assert.Equal(70000L, h[0].AvgPriceRaw);
            Assert.False(h[0].IsOverseas);
            Assert.Equal(5L, h[1].Quantity);
            Assert.True(h[1].IsOverseas);
            Assert.NotEqual(h[0].SymbolHash, h[1].SymbolHash);   // FNV 해시 구분
        }

        [Fact]
        public void Parser_Malformed_FailClosed()
        {
            Span<TossHolding> h = stackalloc TossHolding[16];
            Assert.False(TossJsonParser.TryParseAccount(Encoding.UTF8.GetBytes("{not json"), 0, out _, h, out int n));
            Assert.Equal(0, n);
        }

        [Fact]
        public void Parser_ZeroAllocation()
        {
            // 워밍업(JIT)
            Span<TossHolding> warm = stackalloc TossHolding[16];
            for (int i = 0; i < 5_000; i++) TossJsonParser.TryParseAccount(AccountJson, 0, out _, warm, out _);
            long Measure()
            {
                // ★stackalloc 은 루프 밖(CA2014 회피)·Measure 내부(외부 ref struct 캡처 CS8175 회피)
                Span<TossHolding> h = stackalloc TossHolding[16];
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) TossJsonParser.TryParseAccount(AccountJson, 0, out _, h, out _);
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));   // ★실통신 응답 δ=0 파싱 — 1바이트 GC 유출 0
        }

        // ══ 🛡 TossLiveApiGovernor (Think-Beyond) ════════════════════════════
        [Fact]
        public void Governor_TokenBucket_RateLimits_And_Refills()
        {
            long now = 1_000_000_000L;
            var gov = new TossLiveApiGovernor(() => now, maxPerSecond: 5, burst: 5);
            for (int i = 0; i < 5; i++) Assert.True(gov.TryAcquire());   // 초기 만충 버스트 5
            Assert.False(gov.TryAcquire());                             // 버킷 고갈 → 억제
            now += Sec;                                                 // +1초 → 5 리필
            for (int i = 0; i < 5; i++) Assert.True(gov.TryAcquire());
            Assert.False(gov.TryAcquire());
            Assert.True(gov.Allowed >= 10 && gov.RateBlocked >= 2);
        }

        [Fact]
        public void Governor_429_Lockout_FullyBlocks_Until_RetryAfter()
        {
            long now = 2_000_000_000L;
            var gov = new TossLiveApiGovernor(() => now, maxPerSecond: 100, burst: 100);
            Assert.True(gov.TryAcquire());
            gov.NoteRateLimited(3);            // ★429 Retry-After 3초 완전 봉쇄
            Assert.False(gov.TryAcquire());    // 봉쇄 중
            now += 2 * Sec;
            Assert.False(gov.TryAcquire());    // 아직 3초 미경과
            now += 2 * Sec;                    // 총 4초 > 3초
            Assert.True(gov.TryAcquire());     // 봉쇄 해제
            Assert.True(gov.Http429 >= 1);
        }

        [Fact]
        public void Governor_429_Lockout_Monotonic_NeverShortens()
        {
            long now = 3_000_000_000L;
            var gov = new TossLiveApiGovernor(() => now, maxPerSecond: 100, burst: 100);
            gov.NoteRateLimited(10);           // 10초 봉쇄
            gov.NoteRateLimited(1);            // 더 짧은 값 → 단축 금지(단조 연장만)
            now += 5 * Sec;
            Assert.False(gov.TryAcquire());    // 여전히 봉쇄(10초 유지)
        }

        [Fact]
        public void Governor_CircuitBreaker_Opens_HalfOpen_Closes()
        {
            long now = 4_000_000_000L;
            var gov = new TossLiveApiGovernor(() => now, maxPerSecond: 100, burst: 100, failThreshold: 3, openCooldownSeconds: 2);
            gov.NoteFailure(); gov.NoteFailure(); gov.NoteFailure();   // 연속 3 실패 → 개방
            Assert.Equal(GovernorCircuit.Open, gov.Circuit);
            Assert.False(gov.TryAcquire());                            // 개방 → 봉쇄(서버 보호)
            now += 3 * Sec;                                            // 쿨다운 2초 경과
            Assert.True(gov.TryAcquire());                             // half-open 단일 탐침 허용
            Assert.Equal(GovernorCircuit.HalfOpen, gov.Circuit);
            gov.NoteSuccess();                                         // 탐침 성공 → 폐쇄
            Assert.Equal(GovernorCircuit.Closed, gov.Circuit);
            Assert.True(gov.CircuitOpens >= 1);
        }

        [Fact]
        public void Governor_HalfOpen_Failure_Reopens()
        {
            long now = 5_000_000_000L;
            var gov = new TossLiveApiGovernor(() => now, maxPerSecond: 100, burst: 100, failThreshold: 2, openCooldownSeconds: 2);
            gov.NoteFailure(); gov.NoteFailure();          // 개방
            now += 3 * Sec;
            Assert.True(gov.TryAcquire());                 // half-open 탐침
            gov.NoteFailure();                             // 탐침 실패 → 즉시 재개방
            Assert.Equal(GovernorCircuit.Open, gov.Circuit);
            Assert.False(gov.TryAcquire());                // 재봉쇄
        }

        [Fact]
        public void Governor_ZeroAllocation()
        {
            long now = 6_000_000_000L;
            var gov = new TossLiveApiGovernor(() => now, maxPerSecond: 1_000_000, burst: 1_000_000);
            for (int i = 0; i < 10_000; i++) { gov.TryAcquire(); gov.NoteSuccess(); }
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++)
                {
                    gov.TryAcquire();
                    gov.NoteSuccess();
                    gov.NoteFailure();
                    gov.NoteRateLimited(0);
                }
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));   // ★거버너 핫패스 힙 0(정수·Int128·lock 무할당)
        }

        // ══ 🔒 TossOpenApiLiveAdapter — fail-closed(네트워크 미발생) ═══════════
        [Fact]
        public void LiveAdapter_FailClosed_NoCreds_NoNetwork_And_OrderVeto()
        {
            string dbPath = Path.Combine(Path.GetTempPath(), $"tosslive_{Guid.NewGuid():N}.db");
            var db = new LocalDatabase(dbPath);
            try
            {
                var att = new OrderAttestationService();
                var vault = new SecurityAssetVault(db, att, () => false);   // 미무장
                using var live = new TossOpenApiLiveAdapter(vault, "toss.apiKey", "toss.secret", "toss.account",
                                                            clock: () => 1_000_000_000L);
                // ★Start/RefreshNow 미호출 → 백그라운드 네트워크 0. 무토큰 → fail-closed.
                Assert.Equal("", live.AccessToken());          // 미취득 → 빈 토큰
                Assert.Null(live.FetchAccountJson(""));         // 무토큰 short-circuit(HTTP 미발생)
                Assert.False(live.RejectOrderAttempt(1));       // ★조회 전용 — 발주 하드 veto

                // 자격증명 저장해도 '취득(네트워크 콜드 패스)' 전엔 토큰 미발급(저장≠연결).
                vault.Store("toss.apiKey", "fake-client-id".AsSpan());
                vault.Store("toss.secret", "fake-client-secret".AsSpan());
                Assert.Equal("", live.AccessToken());          // 여전히 빈 토큰(선제 갱신 루프 미기동)
            }
            finally { db.Dispose(); try { File.Delete(dbPath); } catch { } }
        }

        // ══ ⛔ liveArmed:false — 실통신과 무관하게 실물 발주 100% Veto ═════════
        [Fact]
        public void LiveArmedFalse_OrderVeto_Regardless_Of_BalanceConnectivity()
        {
            // 잔고 조회가 성공하든 말든, 실물 발주는 이중 물리 키 미결속이면 100% veto·brokerSend 0회.
            var unarmed = new GenesisLiveIgnitionGateway(valveWired: () => false, liveArmingWired: () => false, defensesPass: () => true);
            var ledger = new InFlightOrderReconciliationLedger();
            int sends = 0;
            BrokerOrderSend send = (BrokerId b, in SealedOrderToken t) => sends++;
            var exec = new UniversalOrderExecutionAdapter(BrokerId.Toss, unarmed, ledger, send);

            for (int i = 0; i < 500; i++)
            {
                var r = exec.SendOrder(new ExecOrderRequest(BrokerId.Toss, 5930 + i, OrderSide.Buy, ExecOrderKind.Limit, 70000, 10));
                Assert.False(r.Accepted);
            }
            Assert.Equal(0, sends);               // ★실물 발주 단 1회도 미송출(0비트 누수)
            Assert.Equal(0L, exec.Dispatched);
        }
    }
}
