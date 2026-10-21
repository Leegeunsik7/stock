// ============================================================================
// CoreEngineAuditTests — [Phase 1] 코어 엔진·주문 파이프라인·무할당 핫패스 전수 코드 감사(Red→Green 재현 테스트)
//   각 테스트는 감사에서 발견한 결함 1건을 재현한다. ★수정 전 코드에서 실패(Red)함을 실측한 뒤 수정했다.
//   분류: [실발생] = 현 프로덕션 배선에서 도달 가능 · [잠재] = 코드 결함이나 현재 프로덕션 호출자 0건(휴면 엔진).
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    public sealed class CoreEngineAuditTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly LocalDatabase _db;
        private readonly PaperBroker _paper;

        public CoreEngineAuditTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"coreaudit_{Guid.NewGuid():N}.db");
            _db = new LocalDatabase(_dbPath);
            _paper = new PaperBroker(_db);
            _paper.ResetSeed(100_000_000m);
        }

        public void Dispose()
        {
            _db.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }

        private static ShadowNoiseConfig Noise(double slipBase = 0.0, double slipVol = 0.0, int delayMs = 100) => new()
        {
            Enabled = true, BaseDelayMs = delayMs, MaxDelayMs = delayMs,
            SlippageBasePct = slipBase, SlippageVolMultiplier = slipVol, Seed = 42,
        };

        // ══ ShadowBroker ════════════════════════════════════════════════════════
        [Fact] // [실발생] 지정가가 PaperBroker 로 넘어간 뒤 섀도 ID 로 취소하면 조용히 무시 → 취소한 주문이 나중에 체결
        public void ShadowBroker_CancelAfterForward_CancelsInnerLimitOrder_NoLaterFill()
        {
            var t0 = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
            var broker = new ShadowBroker(_paper, Noise());
            broker.Tick(t0, (_, _) => 70_000m);

            var (ok, _) = broker.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, 1m, 69_000m, 70_000m);
            Assert.True(ok);
            string shadowId = broker.PendingOrders.Single().Id;

            broker.Tick(t0.AddSeconds(1), (_, _) => 70_000m);   // 지연 경과 → PaperBroker 로 이관(가격 미도달 → 대기)
            Assert.Equal(0, broker.QueueDepth);

            broker.Cancel(shadowId);                            // 사용자는 "취소"했다
            broker.Tick(t0.AddSeconds(2), (_, _) => 68_000m);   // 가격 하락 → 지정가 도달

            Assert.DoesNotContain(_paper.Account.Orders, o => o.Status == PaperStatus.Filled);
            Assert.Contains(_paper.Account.Orders, o => o.Status == PaperStatus.Canceled);
        }

        [Fact] // [실발생] 샌드박스 슬리피지 입력은 상한이 없다 — 매도 체결가가 0 이하가 되는 주문을 "접수"로 반환
        public void ShadowBroker_SlippageDrivingFillPriceNonPositive_RejectedAtPlace()
        {
            var broker = new ShadowBroker(_paper, Noise(slipBase: 0.5, slipVol: 2.0), () => new RegimeSignal { VolRatio = 1.0 });
            var (ok, _) = broker.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Sell, PaperKind.Market, 1m, 0m, 70_000m);
            Assert.False(ok);                    // 1 - 0.5×(1+1×2) = -0.5 → 음수 체결가
            Assert.Equal(0, broker.QueueDepth);
        }

        [Fact] // [실발생] 레짐 신호 NaN → Math.Clamp(NaN)=NaN → (int)NaN → AddMilliseconds 예외가 Place 밖으로 누출
        public void ShadowBroker_NaNRegimeSignal_FailClosedReturn_NoException()
        {
            var broker = new ShadowBroker(_paper, Noise(), () => new RegimeSignal { VolRatio = double.NaN });
            var (ok, _) = broker.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Market, 1m, 0m, 70_000m);
            Assert.False(ok);
            Assert.Equal(0, broker.QueueDepth);
        }

        [Fact] // [실발생·저확률] 8자리 hex(32비트) ID — 생일 충돌 시 Cancel 의 RemoveAll 이 남의 주문까지 제거
        public void ShadowBroker_OrderIds_AreFull128Bit()
        {
            var broker = new ShadowBroker(_paper, Noise());
            broker.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Market, 1m, 0m, 70_000m);
            Assert.Equal(32, broker.PendingOrders.Single().Id.Length);
        }

        // ══ PendingOrderCascadeCanceller ════════════════════════════════════════
        [Fact] // [잠재] 용량 초과 등록이 아직 미체결인 슬롯을 덮어씀 → 추적에서 사라진 주문은 페일오버 캐스케이드 취소 누락
        public void Canceller_RegisterWhenFull_FailsClosed_DoesNotEvictLiveOrder()
        {
            var c = new PendingOrderCascadeCanceller(capacity: 2);
            var a = c.Register(0);
            var b = c.Register(0);
            var overflow = c.Register(1);

            Assert.Equal(-1, overflow);                  // 추적 불가 → 호출측이 발주를 거부해야 함(fail-closed)
            Assert.Equal(2, c.CancelAllForBroker(0));     // 기존 미체결 2건 모두 여전히 취소 대상
            Assert.True(c.IsCancelled(a));
            Assert.True(c.IsCancelled(b));
        }

        [Fact] // [잠재] 체결로 해제된 슬롯이 재사용된 뒤, 옛 핸들로 MarkFilled 하면 새 주문의 추적이 풀림(ABA)
        public void Canceller_StaleHandleAfterReuse_CannotReleaseNewOrder()
        {
            var c = new PendingOrderCascadeCanceller(capacity: 1);
            var first = c.Register(0);
            c.MarkFilled(first);
            var second = c.Register(0);                   // 같은 물리 슬롯 재사용

            c.MarkFilled(first);                          // 지연 도착한 옛 체결 통보
            Assert.Equal(1, c.PendingCount(0));           // 새 주문은 여전히 추적 중
            Assert.Equal(1, c.CancelAllForBroker(0));
            Assert.True(c.IsCancelled(second));
        }

        // ══ BrokerFailoverSentinel ══════════════════════════════════════════════
        [Fact] // [잠재] 페일오버 대상 선정이 (broker+1)%N 고정 — 이미 장애 판정된 브로커로 되돌아감
        public void Failover_NeverRoutesBackToFaultedBroker()
        {
            var board = new LockFreeBrokerSwitchboard(3);
            var s = new BrokerFailoverSentinel(board);
            s.ReportFault(0, BrokerFaultKind.Disconnect, 0, 1);
            Assert.Equal(1, board.Active);
            s.ReportFault(1, BrokerFaultKind.Disconnect, 0, 2);
            Assert.Equal(2, board.Active);
            s.ReportFault(2, BrokerFaultKind.Disconnect, 0, 3);   // 전원 장애

            Assert.Equal(2, board.Active);                        // 장애난 0 으로 복귀하지 않음(어차피 Disarm)
            Assert.False(s.CanEmit);
        }

        [Fact] // [잠재] 비활성 브로커 장애 보고가 활성 전환을 유발하지 않아야 하고, 활성 장애는 정확히 1회만 계수
        public void Failover_FaultOnInactiveBroker_NoSwitch_CountedOnce()
        {
            var board = new LockFreeBrokerSwitchboard(3);
            var s = new BrokerFailoverSentinel(board);
            s.ReportFault(2, BrokerFaultKind.Disconnect, 0, 1);   // 비활성 브로커
            Assert.Equal(0, board.Active);
            s.ReportFault(0, BrokerFaultKind.Disconnect, 0, 2);
            s.ReportFault(0, BrokerFaultKind.Disconnect, 0, 3);   // 같은 장애 재보고(이미 전환됨)
            Assert.Equal(1, board.Active);                        // 2 는 장애 → 1 로
            Assert.Equal(1, s.FailoverCount);
        }

        // ══ Flight/Purge Recorder — 소거 후 잔여 사출 ═════════════════════════════
        [Fact] // [잠재] "비동기 컨티뉴에이션이 늦게 재개돼도 사출할 데이터 자체가 없다"는 보증이 깨짐
        public void AnomalyRecorder_StageAfterPurge_BufferStaysZero()
        {
            var r = new AnomalyFlightRecorder(8);
            r.CaptureAndPurge(TelemetrySource.Latency, 999);
            r.StagePending(3, 123_456);                   // Disarm 이후 늦게 도착한 스테이징
            Assert.True(r.PendingBufferIsZero);
        }

        [Fact] // [잠재] 리스크 도메인 동형 결함
        public void RiskPurgeRecorder_StageAfterPurge_BufferStaysZero()
        {
            var r = new RiskOrderPurgeRecorder(8);
            r.CaptureAndPurge(RiskBreachSource.Drawdown, 600);
            r.StagePending(1, 777);
            Assert.True(r.PendingBufferIsZero);
        }

        // ══ DynamicRiskInterlock ════════════════════════════════════════════════
        [Fact] // [잠재·단위계약] (peak-current)×10000 long 곱 오버플로 → 음수 낙폭 → 미격발(fail-open).
               // 현 어댑터는 원(KRW) 단위라 도달 불가, 하우스 관례 QFixed(10^8) 로 넘기면 ~922만원 하락에서 발생.
        public void RiskInterlock_LargeScaleEquity_DrawdownDoesNotOverflow()
        {
            var ri = new DynamicRiskInterlock(new RiskOrderPurgeRecorder());
            ri.Configure(drawdownThresholdBps: 1_000, dailyLossThresholdBps: -1);
            const long qScale = 100_000_000L;              // 하우스 QFixed 스케일(QMath.Scale=10^8, internal)
            ri.Note(100_000_000L * qScale);               // 1억원(QFixed raw)
            ri.Note(50_000_000L * qScale);                // 50% 하락
            Assert.True(ri.IsDisarmed);
            Assert.Equal(5_000, ri.DrawdownBps);
        }

        [Fact] // [잠재] 감사 팬인 콜백 예외가 Note 밖으로 누출(Disarm 은 이미 래치돼 안전하나 평가 루프가 죽음)
        public void RiskInterlock_ThrowingBreachCallback_DoesNotEscape_StillDisarmed()
        {
            var ri = new DynamicRiskInterlock(new RiskOrderPurgeRecorder(), (_, _) => throw new InvalidOperationException("audit down"));
            ri.Configure(100, 100);
            ri.Note(1_000_000);
            var ex = Record.Exception(() => ri.Note(500_000));
            Assert.Null(ex);
            Assert.True(ri.IsDisarmed);
            Assert.True(ri.BreachedSource(RiskBreachSource.DailyLoss));   // 첫 축 콜백이 던져도 두 번째 축 평가 계속
            Assert.Equal(2, ri.CallbackFaults);
        }

        // ══ ZeroAllocForensicAuditStream ════════════════════════════════════════
        [Fact] // [잠재] 비랩 상태에서 첫 링크의 PrevHash 가 제네시스(0x00)인지 검사하지 않음 — 일관 위조한 첫 링크 통과
        public void AuditStream_ForgedFirstLink_DetectedByGenesisAnchor()
        {
            var s = new ZeroAllocForensicAuditStream(8);
            s.Append(AuditEventKind.Arm, 1, 10);
            s.__AdversarialForgeFirstLinkForTest();
            Assert.False(s.VerifyIntegrity());
        }

        // ══ MmfCircularBlackBoxWriter ═══════════════════════════════════════════
        [Fact] // [잠재] 다른 용량으로 기록된 기존 파일을 재오픈하면 시퀀스를 그대로 이어받아 슬롯 배치가 뒤섞임
        public void BlackBox_ReopenWithDifferentCapacity_StartsFresh_PreservesOldFileAside()
        {
            string path = Path.Combine(Path.GetTempPath(), $"bb_{Guid.NewGuid():N}.mmf");
            try
            {
                using (var w = new MmfCircularBlackBoxWriter(path, 8))
                    for (int i = 0; i < 5; i++) w.Append(new FlightRecordEvent(i + 1, i, 0, 0, 0, 0, 0, 0, 1, 0));

                using (var w2 = new MmfCircularBlackBoxWriter(path, 16))
                {
                    Assert.Equal(0, w2.WriteSeq);
                    Assert.True(w2.PreservedIncompatibleFile);
                }
                Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".incompatible.*"));
            }
            finally
            {
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
                    try { File.Delete(f); } catch { }
            }
        }

        [Fact] // [잠재] 손상 헤더(음수 시퀀스) → 이후 모든 Append 가 음수 오프셋 예외(블랙박스 영구 사망)
        public void BlackBox_CorruptNegativeSequenceHeader_RecoversInsteadOfThrowingForever()
        {
            string path = Path.Combine(Path.GetTempPath(), $"bb_{Guid.NewGuid():N}.mmf");
            try
            {
                using (var w = new MmfCircularBlackBoxWriter(path, 8)) w.Append(new FlightRecordEvent(1, 1, 0, 0, 0, 0, 0, 0, 1, 0));
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
                {
                    fs.Position = 16;
                    fs.Write(BitConverter.GetBytes(-5L));
                }
                using var w2 = new MmfCircularBlackBoxWriter(path, 8);
                var ex = Record.Exception(() => w2.Append(new FlightRecordEvent(2, 2, 0, 0, 0, 0, 0, 0, 1, 0)));
                Assert.Null(ex);
            }
            finally
            {
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
                    try { File.Delete(f); } catch { }
            }
        }
    }

    // ══ δ=0 실측(Release) — 타이밍/할당 측정은 직렬 컬렉션 ══════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class CoreEngineAuditAllocationTests
    {
        private static long AllocatedDuring(Action action, int warmup = 2_000, int iterations = 10_000)
        {
            for (int i = 0; i < warmup; i++) action();
            return ZeroAllocProbe.MinAllocated(() => { for (int i = 0; i < iterations; i++) action(); });
        }

        [Fact] // [실발생] ForensicAuditViewAdapter 가 UI 갱신마다 호출 — "δ=0 재계산" 주석과 달리 호출당 32B 할당
        public void ZeroAlloc_AuditStreamVerifyIntegrity()
        {
            var s = new ZeroAllocForensicAuditStream(64);
            for (int i = 0; i < 40; i++) s.Append(AuditEventKind.RiskViolation, i, i);
            Assert.Equal(0, AllocatedDuring(() => s.VerifyIntegrity(), warmup: 200, iterations: 2_000));
        }

        // ── 이번 감사로 바뀐 핫패스 전부 δ=0 재실측(교정이 할당을 새로 만들지 않았음을 증명) ──
        [Fact]
        public void ZeroAlloc_AuditStreamAppend_WithLock()
        {
            var s = new ZeroAllocForensicAuditStream(1024);
            long t = 0;
            Assert.Equal(0, AllocatedDuring(() => s.Append(AuditEventKind.RiskViolation, 7, t++)));
        }

        [Fact]
        public void ZeroAlloc_CancellerRegisterAndRelease()
        {
            var c = new PendingOrderCascadeCanceller(256);
            Assert.Equal(0, AllocatedDuring(() => { long h = c.Register(3); c.MarkFilled(h); }));
        }

        [Fact]
        public void ZeroAlloc_CancellerRegisterWhenFull_FailClosedPath()
        {
            var c = new PendingOrderCascadeCanceller(64);
            for (int i = 0; i < 64; i++) c.Register(1);
            Assert.Equal(0, AllocatedDuring(() => c.Register(1), warmup: 200, iterations: 2_000));
        }

        [Fact]
        public void ZeroAlloc_RiskInterlockNote_CasPeakAndInt128()
        {
            var ri = new DynamicRiskInterlock(new RiskOrderPurgeRecorder());
            ri.Configure(long.MaxValue, long.MaxValue);
            long e = 1_000_000_000;
            Assert.Equal(0, AllocatedDuring(() => ri.Note(e++)));
        }

        [Fact]
        public void ZeroAlloc_RecorderStagePending()
        {
            var r = new AnomalyFlightRecorder(64);
            int slot = 0;
            Assert.Equal(0, AllocatedDuring(() => r.StagePending(slot++ & 63, 123)));
        }

        [Fact]
        public void ZeroAlloc_FailoverReportFault_WithFaultedMask()
        {
            var s = new BrokerFailoverSentinel(new LockFreeBrokerSwitchboard(4));
            int b = 0;
            Assert.Equal(0, AllocatedDuring(() => s.ReportFault(b++ & 3, BrokerFaultKind.Disconnect, 0, b)));
        }
    }

    // ══ Think Beyond 방향 B — Zero-Alloc Hot-Path Static Allocator Guard ═══════════════════
    public sealed class ZeroAllocHotPathGuardTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _out;
        public ZeroAllocHotPathGuardTests(Xunit.Abstractions.ITestOutputHelper o) => _out = o;

        /// <summary>검사기 자체 검증용 표본 — 의도적으로 할당을 유발하는 코드(프로덕션 아님, 표지 없음).</summary>
        private static class Samples
        {
            public static object Boxing(int x) => x;
            public static int[] NewArray() => new int[3];
            public static int Closure(int x) { Func<int> f = () => x + 1; return f(); }
            public static string StringConcat(string a, string b) => a + b;
            public static int Linq(int[] a) => a.Sum();
            public static string ToStr(long v) => v.ToString();
            public static string Interpolation(int x) => $"v={x}";
            public static long Clean(ReadOnlySpan<long> s) { long t = 0; foreach (var v in s) t += v; return t; }
            public static int ThrowPath(int x) { if (x < 0) throw new ArgumentOutOfRangeException(nameof(x)); return x; }
            public static (int, int) ValueTupleNewobj(int a) => (a, a + 1);
        }

        private static System.Collections.Generic.IReadOnlyList<IlAllocationSite> Sites(string method)
            => ZeroAllocIlInspector.Inspect(typeof(Samples).GetMethod(method)!);

        [Theory]
        [InlineData(nameof(Samples.Boxing), "box")]
        [InlineData(nameof(Samples.NewArray), "newarr")]
        [InlineData(nameof(Samples.Closure), "newobj")]
        [InlineData(nameof(Samples.StringConcat), "alloc-call")]
        [InlineData(nameof(Samples.Linq), "alloc-call")]
        [InlineData(nameof(Samples.ToStr), "alloc-call")]
        [InlineData(nameof(Samples.Interpolation), "alloc-call")]
        public void Inspector_DetectsAllocationSite(string method, string expectedKind)
            => Assert.Contains(Sites(method), s => s.Kind.StartsWith(expectedKind, StringComparison.Ordinal));

        [Fact]
        public void Inspector_Closure_ReportsBothDisplayClassAndDelegate()
        {
            var kinds = Sites(nameof(Samples.Closure)).Select(s => s.Kind).ToList();
            Assert.Contains("newobj", kinds);            // <>c__DisplayClass (캡처 클로저)
            Assert.Contains("newobj-delegate", kinds);   // Func<int>
        }

        [Theory]
        [InlineData(nameof(Samples.Clean))]
        [InlineData(nameof(Samples.ThrowPath))]          // 예외 객체는 throw 전용 실패 경로 — 허용
        [InlineData(nameof(Samples.ValueTupleNewobj))]   // 값 형식 newobj 는 스택 생성 — 허용
        public void Inspector_NoFalsePositive(string method)
            => Assert.Empty(Sites(method));

        [Fact]
        public void AllMarkedProductionHotPaths_HaveNoStaticAllocationSites()
        {
            var marked = ZeroAllocIlInspector.FindMarkedMethods(typeof(ZeroAllocHotPathAttribute).Assembly, typeof(ZeroAllocHotPathAttribute));
            Assert.True(marked.Count >= 22, $"표지 메서드 {marked.Count}개 — 계약 표지가 누락/삭제되지 않았는지 확인");

            var violations = marked.SelectMany(ZeroAllocIlInspector.Inspect).ToList();
            foreach (var v in violations) _out.WriteLine(v.ToString());
            Assert.Empty(violations);
        }
    }
}
