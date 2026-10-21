// ============================================================================
// AuditLedgerTelemetryTests — [v12.18.3-DEV · 추천 A] 감사 원장 관제 배지(설정 화면 · /aidash) 검증
//
//  §1 스냅샷 정합: 레코드 수 · 용량 · 머리 해시 상위 8바이트(빅 엔디언 = 16진 앞 16자) · 버전 증가 · 상태/원인 코드 = RCL 코드.
//  §2 δ=0: Snapshot 1만 회 · 라벨/사용률/견본/16진(같은 값 반복) 매핑 0B · 허브 펌프(원장 결선) 추가 할당 0.
//  §3 ★무간섭: 체인 잠금(_gate)을 다른 스레드가 쥐고 있어도 Snapshot 즉시 반환 · 조회가 검증 횟수 · 동결 · 판정을 바꾸지 않음 ·
//     기록 1,500건과 동시 판독 — 모든 판독 (수, 머리) 쌍이 실제로 존재했던 상태(찢김 0).
//  §4 동결 전이: 실행 중 변조 → PollIntegrity → 스냅샷 Frozen(체인 불일치) → 허브 다음 펌프 즉시 재게시 · 설정 포트 배지 · 폐기 = 사용 불가.
//  §5 [방향 B] 포화 경고: 90%(2,048 → 1,843 · 100 → 90) = Saturating + 1회 고지 · 100% = 동결(가득 참).
//  §6 [방향 A] 지문 견본: 상위 4니블 → 팔레트 · 16자 16진 · 1비트 차이 머리 = 다른 문자열.
//  §7 결선 · 회귀: 어댑터(원장 = 결선 · 대역 = 미결선) · 호스트 결선 소스 · 오중 빌드 · 래칫 ≤ 40 · 인터락 30.
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;
using StockManager.Engines;
using StockManager.Security;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class AuditLedgerTelemetryTests
    {
        private readonly ITestOutputHelper _out;
        public AuditLedgerTelemetryTests(ITestOutputHelper o) => _out = o;

        private sealed class Clock { public long T = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc).Ticks; public long Next() => T += TimeSpan.TicksPerSecond; }

        private static ulong HeadPrefix(MonotonicSettingAuditLog log) { Span<byte> h = stackalloc byte[32]; log.CopyHead(h); return BinaryPrimitives.ReadUInt64BigEndian(h); }

        private static unsafe void Flip(MonotonicSettingAuditLog log, long offset, byte bit)
        {
            var f = typeof(MonotonicSettingAuditLog).GetField("_base", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            byte* p = (byte*)System.Reflection.Pointer.Unbox(f.GetValue(log)!);
            p[offset] ^= bit;
        }

        // ════════════ §1 스냅샷 정합 ════════════

        [Fact]
        public void Snapshot_CountCapacityHeadVersion_CodesMatchRcl()
        {
            using var log = MonotonicSettingAuditLog.CreateVolatile(new StrategyAnomalyInterlock(), 64, new Clock().Next);
            var s0 = log.Snapshot();
            Assert.Equal((0, 64, AuditLedgerState.Normal, AuditFreezeCause.None, false), (s0.Count, s0.Capacity, s0.State, s0.FreezeCause, s0.Contended));
            Assert.Equal(HeadPrefix(log), s0.HeadPrefix);                                   // 빈 원장 = 헤더 해시(제네시스)
            Assert.NotEqual(0UL, s0.HeadPrefix);
            for (int i = 0; i < 5; i++) Assert.True(log.Append(3, "Strategy.AdaptiveMaxBp", TightenVerdict.Tightened, 9_500 + i, 9_501 + i));
            var s1 = log.Snapshot();
            Assert.Equal(5, s1.Count);
            Assert.Equal(HeadPrefix(log), s1.HeadPrefix);
            Assert.True(s1.Version > s0.Version);
            Assert.Equal(7, s1.UsedPercent);                                                  // 5/64 = 7.8% 내림
            Span<byte> h = stackalloc byte[32]; log.CopyHead(h);
            Assert.Equal(Convert.ToHexString(h)[..16], SignalLabelMapper.HeadHex(s1.HeadPrefix));

            // 호스트 열거 = RCL 코드
            Assert.Equal((DashAuditLedger.Normal, DashAuditLedger.Saturating, DashAuditLedger.Frozen),
                         ((int)AuditLedgerState.Normal, (int)AuditLedgerState.Saturating, (int)AuditLedgerState.Frozen));
            Assert.Equal((DashAuditLedger.CauseNone, DashAuditLedger.CauseChainBroken, DashAuditLedger.CauseFull, DashAuditLedger.CauseUnavailable),
                         ((int)AuditFreezeCause.None, (int)AuditFreezeCause.ChainBroken, (int)AuditFreezeCause.Full, (int)AuditFreezeCause.Unavailable));
            Assert.Equal(Enum.GetValues<AuditLedgerState>().Length + 1, SignalLabelMapper.AuditStateCount);   // + 미결선
            Assert.Equal(Enum.GetValues<AuditFreezeCause>().Length, SignalLabelMapper.AuditCauseCount);
        }

        // ════════════ §2 δ=0 ════════════

        [Fact]
        public void Snapshot_Labels_HubPump_ZeroAlloc()
        {
            using var log = MonotonicSettingAuditLog.CreateVolatile(new StrategyAnomalyInterlock(), 2048, new Clock().Next);
            for (int i = 0; i < 10; i++) log.Append(0, "Strategy.SignalGateEnabled", TightenVerdict.Unchanged, 1, 1);
            ulong head = log.Snapshot().HeadPrefix;
            _ = SignalLabelMapper.HeadHex(head);                                               // 캐시 적재(머리 변경 시 1회 할당)
            long snap = MinAllocated(() => { for (int i = 0; i < 10_000; i++) _ = log.Snapshot(); });
            long labels = MinAllocated(() =>
            {
                for (int i = 0; i < 10_000; i++)
                {
                    _ = SignalLabelMapper.AuditStateLabel(i % 4, i % 4); _ = SignalLabelMapper.AuditStateClass(i % 4);
                    _ = SignalLabelMapper.AuditUsedText(i % 101); _ = SignalLabelMapper.HeadSwatch(head, i % 4); _ = SignalLabelMapper.HeadHex(head);
                }
            });
            long ticks = 1;
            var hub = new DashboardSnapshotHub(nowTicks: () => ++ticks);
            hub.AttachAuditLedger(log);
            Assert.True(hub.Pump());
            long pump = MinAllocated(() => { for (int i = 0; i < 200; i++) { ticks += TimeSpan.TicksPerSecond; hub.Pump(); } });
            _out.WriteLine($"[δ] Snapshot 1만 = {snap}B · 라벨 1만 = {labels}B · 허브 펌프 200 = {pump}B");
            Assert.Equal(0, snap);
            Assert.Equal(0, labels);
            Assert.Equal(0, pump);
        }

        // ════════════ §3 무간섭 ════════════

        [Fact]
        public void Snapshot_DoesNotTakeChainLock_NorVerify_NorChangeState()
        {
            var anomaly = new StrategyAnomalyInterlock();
            using var log = MonotonicSettingAuditLog.CreateVolatile(anomaly, 64, new Clock().Next);
            Assert.True(log.Append(3, "Strategy.AdaptiveMaxBp", TightenVerdict.Tightened, 9_500, 9_600));
            var gate = typeof(MonotonicSettingAuditLog).GetField("_gate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(log)!;
            using var held = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var holder = new Thread(() => { lock (gate) { held.Set(); release.Wait(10_000); } }) { IsBackground = true };
            holder.Start();
            Assert.True(held.Wait(5_000));
            try
            {
                long verifications = log.Verifications;
                var sw = Stopwatch.StartNew();
                AuditLedgerSnapshot s = default;
                for (int i = 0; i < 100_000; i++) s = log.Snapshot();
                sw.Stop();
                _out.WriteLine($"[무간섭] 체인 잠금 점유 중 Snapshot 10만 회 {sw.Elapsed.TotalMilliseconds:F1}ms · 회당 {sw.Elapsed.TotalMilliseconds * 1_000_000 / 100_000:F0}ns");
                Assert.True(sw.ElapsedMilliseconds < 2_000, "잠금 대기 의심: " + sw.ElapsedMilliseconds + "ms");
                Assert.Equal(1, s.Count);
                Assert.False(s.Contended);
                Assert.Equal(verifications, log.Verifications);                              // 조회 = 체인 검증 0
                Assert.False(log.Frozen);
                Assert.False(anomaly.Tripped);
                // 어댑터 조회도 같은 경로(잠금 점유 중 즉시)
                IAdminSettingWidgetPort port = new AdminSettingWidgetAdapter(new LocalDatabase(Path.Combine(Path.GetTempPath(), "ms2e_tel_" + Guid.NewGuid().ToString("N") + ".db")), log);
                Assert.True(port.TryGetAuditLedger(out var b));
                Assert.Equal((DashAuditLedger.Normal, 1, 64), (b.State, b.Count, b.Capacity));
            }
            finally { release.Set(); holder.Join(5_000); }
        }

        [Fact]
        public void Snapshot_ConcurrentWithWriter_NeverTorn()
        {
            const int N = 1_500;
            using var log = MonotonicSettingAuditLog.CreateVolatile(new StrategyAnomalyInterlock(), 2048, new Clock().Next);
            var heads = new ulong[N + 1];
            heads[0] = HeadPrefix(log);
            var seen = new List<(int Count, ulong Head)>(1 << 16);
            using var go = new CountdownEvent(2);
            bool done = false;
            var reader = new Thread(() =>
            {
                go.Signal(); go.Wait();
                while (!Volatile.Read(ref done)) { var s = log.Snapshot(); if (!s.Contended && seen.Count < (1 << 16)) seen.Add((s.Count, s.HeadPrefix)); }
            });
            reader.Start();
            go.Signal(); go.Wait();
            for (int i = 1; i <= N; i++)
            {
                // 강화 레코드(거부 예산 영향 없음) — 기록 직후 그 수의 머리를 정답표에 저장
                Assert.True(log.Append(3, "Strategy.AdaptiveMaxBp", TightenVerdict.Tightened, i, i + 1));
                heads[i] = HeadPrefix(log);
            }
            Volatile.Write(ref done, true);
            reader.Join();
            int torn = 0;
            foreach (var (c, hd) in seen) if (c < 0 || c > N || heads[c] != hd) torn++;
            _out.WriteLine($"[동시 판독] 기록 {N} · 판독 표본 {seen.Count} · 찢김 {torn}");
            Assert.True(seen.Count > 100);
            Assert.Equal(0, torn);
        }

        // ════════════ §4 동결 전이 ════════════

        [Fact]
        public void Freeze_Tamper_SnapshotFrozen_HubRepublishes_PortBadge_DisposedUnavailable()
        {
            var anomaly = new StrategyAnomalyInterlock();
            var log = MonotonicSettingAuditLog.CreateVolatile(anomaly, 64, new Clock().Next);
            Assert.True(log.Append(0, "Strategy.SignalGateEnabled", TightenVerdict.Tightened, 0, 1));
            long ticks = 1;
            var hub = new DashboardSnapshotHub(nowTicks: () => ++ticks);
            hub.AttachAuditLedger(log);
            Assert.True(hub.Pump());
            var f0 = hub.Acquire()!;
            Assert.Equal((DashAuditLedger.Normal, 1, 64, 1), (f0.AuditState, f0.AuditCount, f0.AuditCapacity, f0.AuditUsedPercent));
            Assert.Equal("원장 정상", SignalLabelMapper.AuditStateLabel(f0.AuditState, f0.AuditFreezeCause));
            hub.Release(f0);
            Assert.False(hub.Pump());                                                          // 변화 없음 = 재게시 없음

            Flip(log, 128 + 40, 0x01);
            Assert.False(log.PollIntegrity());
            var s = log.Snapshot();
            Assert.Equal((AuditLedgerState.Frozen, AuditFreezeCause.ChainBroken, AuditChainVerdict.RecordHashMismatch), (s.State, s.FreezeCause, s.LastVerdict));
            Assert.True(anomaly.Tripped);
            Assert.True(hub.Pump());                                                           // ★동결 = 즉시 재게시(서명 변화)
            var f1 = hub.Acquire()!;
            Assert.Equal((DashAuditLedger.Frozen, DashAuditLedger.CauseChainBroken), (f1.AuditState, f1.AuditFreezeCause));
            Assert.Equal("원장 동결 — 해시 체인 불일치 · 손상", SignalLabelMapper.AuditStateLabel(f1.AuditState, f1.AuditFreezeCause));
            Assert.Equal("frozen", SignalLabelMapper.AuditStateClass(f1.AuditState));
            hub.Release(f1);
            IAdminSettingWidgetPort port = new AdminSettingWidgetAdapter(new LocalDatabase(Path.Combine(Path.GetTempPath(), "ms2e_tel_" + Guid.NewGuid().ToString("N") + ".db")), log);
            Assert.True(port.TryGetAuditLedger(out var b));
            Assert.Equal((DashAuditLedger.Frozen, DashAuditLedger.CauseChainBroken), (b.State, b.FreezeCause));
            Assert.Equal(TightenVerdictCode.AuditFrozen, port.TightenSetting("Strategy.AdaptiveMaxBp", "9900"));
            Assert.Equal(1, log.Snapshot().Count);                                            // 조회 · 거부가 상태를 바꾸지 않음

            // 폐기 = 기록 불가 → 사용 불가로 표시(동결 원인 없던 원장)
            var log2 = MonotonicSettingAuditLog.CreateVolatile(new StrategyAnomalyInterlock(), 8, new Clock().Next);
            log2.Dispose();
            Assert.Equal((AuditLedgerState.Frozen, AuditFreezeCause.Unavailable), (log2.Snapshot().State, log2.Snapshot().FreezeCause));
            log.Dispose();

            // 미결선 허브 = Unbound 라벨
            var hub0 = new DashboardSnapshotHub(nowTicks: () => ++ticks);
            Assert.True(hub0.Pump());
            var fu = hub0.Acquire()!;
            Assert.Equal(DashAuditLedger.Unbound, fu.AuditState);
            Assert.Equal("원장 미결선", SignalLabelMapper.AuditStateLabel(fu.AuditState, fu.AuditFreezeCause));
            hub0.Release(fu);
        }

        // ════════════ §5 [방향 B] 포화 경고 ════════════

        [Fact]
        public void Saturation_90Percent_Warns_Once_Full_Freezes()
        {
            Assert.Equal(1_843, AuditLedgerSnapshot.SaturationThreshold(MonotonicSettingAuditLog.DefaultCapacity));
            var anomaly = new StrategyAnomalyInterlock();
            using var log = MonotonicSettingAuditLog.CreateVolatile(anomaly, 100, new Clock().Next);
            for (int i = 0; i < 89; i++) Assert.True(log.Append(3, "k", TightenVerdict.Tightened, i, i + 1));
            Assert.Equal(AuditLedgerState.Normal, log.Snapshot().State);
            Assert.Equal(0, log.SaturationWarnings);
            Assert.True(log.Append(3, "k", TightenVerdict.Tightened, 89, 90));
            var s = log.Snapshot();
            Assert.Equal((AuditLedgerState.Saturating, 90), (s.State, s.UsedPercent));
            Assert.Equal(1, log.SaturationWarnings);
            Assert.Equal("원장 90% — 보관 · 재기동 필요", SignalLabelMapper.AuditStateLabel((int)s.State, (int)s.FreezeCause));
            Assert.Equal("sat", SignalLabelMapper.AuditStateClass((int)s.State));
            Assert.False(anomaly.Tripped);                                                    // 소프트 가드 — 기록 · 무장 판정 불변
            for (int i = 90; i < 100; i++) Assert.True(log.Append(3, "k", TightenVerdict.Tightened, i, i + 1));
            Assert.Equal(1, log.SaturationWarnings);                                          // 1회만 고지
            Assert.Equal(AuditLedgerState.Saturating, log.Snapshot().State);                  // 100/100 · 아직 다음 기록 시도 전
            Assert.False(log.Append(3, "k", TightenVerdict.Tightened, 100, 101));
            var full = log.Snapshot();
            Assert.Equal((AuditLedgerState.Frozen, AuditFreezeCause.Full, 100), (full.State, full.FreezeCause, full.UsedPercent));
            Assert.Equal(StrategyAnomalyKind.AuditLedgerFull, anomaly.FirstCause);
        }

        // ════════════ §6 [방향 A] 지문 견본 ════════════

        [Fact]
        public void HeadFingerprint_Swatches_Hex_OneBitDiffers()
        {
            const ulong p = 0x0123456789ABCDEFUL;
            Assert.Equal("0123456789ABCDEF", SignalLabelMapper.HeadHex(p));
            var sw = new[] { SignalLabelMapper.HeadSwatch(p, 0), SignalLabelMapper.HeadSwatch(p, 1), SignalLabelMapper.HeadSwatch(p, 2), SignalLabelMapper.HeadSwatch(p, 3) };
            Assert.Equal(4, new HashSet<string>(sw).Count);                                   // 니블 0 · 1 · 2 · 3 = 서로 다른 색
            Assert.Equal(SignalLabelMapper.HeadSwatch(0xF000000000000000UL, 0), SignalLabelMapper.HeadSwatch(0xFFFFFFFFFFFFFFFFUL, 3));
            Assert.Equal(SignalLabelMapper.HeadSwatch(p, 3), SignalLabelMapper.HeadSwatch(p, 99));   // 범위 밖 = 클램프
            for (int i = 0; i < 16; i++)
            {
                string c = SignalLabelMapper.HeadSwatch((ulong)i << 60, 0);
                Assert.NotEqual("#E53935", c, StringComparer.OrdinalIgnoreCase);               // 시세 상승/하강 색 미사용
                Assert.NotEqual("#1E88E5", c, StringComparer.OrdinalIgnoreCase);
            }
            Assert.NotEqual(SignalLabelMapper.HeadHex(p), SignalLabelMapper.HeadHex(p ^ 1));
            // 실 원장: 기록 1건 = 지문 변화
            using var log = MonotonicSettingAuditLog.CreateVolatile(new StrategyAnomalyInterlock(), 8, new Clock().Next);
            string a = SignalLabelMapper.HeadHex(log.Snapshot().HeadPrefix);
            log.Append(1, "Strategy.AdaptiveThreshold", TightenVerdict.Unchanged, 1, 1);
            Assert.NotEqual(a, SignalLabelMapper.HeadHex(log.Snapshot().HeadPrefix));
        }

        // ════════════ §7 결선 · 회귀 ════════════

        private sealed class NotALedger : ITightenAuditSink
        {
            public bool Frozen => false;
            public bool Append(int keyIndex, string? key, TightenVerdict verdict, long current, long proposed) => true;
        }

        [Fact]
        public void Wiring_Adapter_Host_Razor_Regression()
        {
            string db = Path.Combine(Path.GetTempPath(), "ms2e_tel_" + Guid.NewGuid().ToString("N") + ".db");
            IAdminSettingWidgetPort stub = new AdminSettingWidgetAdapter(new LocalDatabase(db), new NotALedger());
            Assert.False(stub.TryGetAuditLedger(out var none));
            Assert.Equal(DashAuditLedger.Unbound, none.State);

            string root = RepoRoot();
            string host = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("dashHub.AttachAuditLedger(tightenAudit);", host);
            string dash = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Pages", "Trading", "PaperTradingDashboardView.razor"));
            Assert.Contains("SignalLabelMapper.AuditStateLabel(f.AuditState, f.AuditFreezeCause)", dash);
            Assert.Contains("SignalLabelMapper.HeadHex(f.AuditHeadPrefix)", dash);
            string settings = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Pages", "Settings", "SettingsPage.razor"));
            Assert.Contains("Settings.TryGetAuditLedger(out _audit)", settings);
            foreach (var razor in new[] { dash, settings }) Assert.DoesNotContain("StockManager.Security", razor);   // RCL = 포트 · 코드만(엔진 형식 0)

            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", File.ReadAllText(Path.Combine(root, proj)));
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
            Assert.Equal(30, Enum.GetValues<ArmingInterlockSource>().Length);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }
    }
}
