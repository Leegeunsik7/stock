// ============================================================================
// Phase98GoldenMasterTests — v12.0.0 Phase 94-P Part 3 Think-Beyond 적대적 검증
//   ① ZeroAllocRowStreamMapper — 리더→투사(δ=0 Project·DataRow 무물성화 MapAll·빈커서·용량제한).
//   ② GoldenMasterBootSentinel — fail-closed 판정(전통과=true·1결함=false·빈=false)·δ=0·실 어셈블리 스캔.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class Phase98GoldenMasterTests
    {
        private readonly ITestOutputHelper _out;
        public Phase98GoldenMasterTests(ITestOutputHelper o) => _out = o;

        // 숫자 전용 커서(δ=0 Project 검증용 — long[]/double[] 인덱싱, 접근자 힙 0).
        private sealed class NumericCursor : IRowCursor
        {
            private readonly long[] _a; private readonly double[] _b; private int _i = -1;
            public NumericCursor(long[] a, double[] b) { _a = a; _b = b; }
            public bool Read() => ++_i < _a.Length;
            public long Int64(int col) => _a[_i];
            public double Double(int col) => _b[_i];
            public decimal Decimal(int col) => (decimal)_b[_i];
            public string Str(int col) => "";
            public DateTime Time(int col) => default;
            public bool IsNull(int col) => false;
        }

        // 문자열 포함 범용 커서(MapAll 검증용).
        private sealed class ObjectCursor : IRowCursor
        {
            private readonly (long id, string sym)[] _rows; private int _i = -1;
            public ObjectCursor((long, string)[] rows) => _rows = rows;
            public bool Read() => ++_i < _rows.Length;
            public long Int64(int col) => _rows[_i].id;
            public double Double(int col) => _rows[_i].id;
            public decimal Decimal(int col) => _rows[_i].id;
            public string Str(int col) => _rows[_i].sym;
            public DateTime Time(int col) => default;
            public bool IsNull(int col) => false;
        }

        private readonly record struct Pt(long T, double V);

        [Fact]
        public void Mapper_Project_Reads_Into_Span_And_Respects_Capacity()
        {
            var cur = new NumericCursor(new long[] { 1, 2, 3, 4, 5 }, new double[] { 1.5, 2.5, 3.5, 4.5, 5.5 });
            Span<Pt> dst = stackalloc Pt[3];
            int n = ZeroAllocRowStreamMapper.Project(cur, dst, c => new Pt(c.Int64(0), c.Double(1)));
            Assert.Equal(3, n);   // 용량 제한(dest.Length)
            Assert.Equal(1L, dst[0].T); Assert.Equal(3.5, dst[2].V);
            int n2 = ZeroAllocRowStreamMapper.Project(cur, dst, c => new Pt(c.Int64(0), c.Double(1)));
            Assert.Equal(2, n2);  // 남은 2행
        }

        [Fact]
        public void Mapper_MapAll_Projects_All_Rows_No_DataRow()
        {
            var cur = new ObjectCursor(new[] { (5930L, "삼성전자"), (660L, "SK하이닉스") });
            var list = ZeroAllocRowStreamMapper.MapAll(cur, c => (id: c.Int64(0), sym: c.Str(1)));
            Assert.Equal(2, list.Count);
            Assert.Equal("삼성전자", list[0].sym);
            Assert.Equal(660L, list[1].id);
        }

        [Fact]
        public void Mapper_EmptyCursor_FailSafe()
        {
            var cur = new NumericCursor(Array.Empty<long>(), Array.Empty<double>());
            Span<Pt> dst = stackalloc Pt[4];
            Assert.Equal(0, ZeroAllocRowStreamMapper.Project(cur, dst, c => new Pt(c.Int64(0), c.Double(1))));
            Assert.Empty(ZeroAllocRowStreamMapper.MapAll(new NumericCursor(Array.Empty<long>(), Array.Empty<double>()), c => c.Int64(0)));
        }

        // ── 골든마스터 부트 센티널 — fail-closed ─────────────────────────────────
        private sealed class FakeProbe : IGoldenMasterProbe
        {
            public bool LiveArmedFalse { get; init; }
            public bool DualKeyAbsent { get; init; }
            public bool VaultSealed { get; init; }
        }

        [Fact]
        public void BootSentinel_Evaluate_FailClosed()
        {
            Span<GoldenMasterCheck> all = stackalloc GoldenMasterCheck[]
            {
                new(GmCheckId.RclZeroEngineRef, true), new(GmCheckId.HostCoupledWithinRatchet, true),
                new(GmCheckId.LiveArmedFalse, true), new(GmCheckId.DualKeyAbsent, true), new(GmCheckId.VaultSealed, true),
            };
            Assert.True(GoldenMasterBootSentinel.Evaluate(all).AllPassed);   // 전통과 → 통과

            Span<GoldenMasterCheck> one = stackalloc GoldenMasterCheck[]
            {
                new(GmCheckId.RclZeroEngineRef, true), new(GmCheckId.LiveArmedFalse, false),   // ★단 1결함
            };
            var r = GoldenMasterBootSentinel.Evaluate(one);
            Assert.False(r.AllPassed);   // 1결함 → fail-closed 거부
            Assert.Equal(1, r.Passed); Assert.Equal(2, r.Total);

            Assert.False(GoldenMasterBootSentinel.Evaluate(ReadOnlySpan<GoldenMasterCheck>.Empty).AllPassed);   // 빈 → 거부
        }

        [Fact]
        public void BootSentinel_Scan_RealAssemblies_LiveArmedFalse_Passes()
        {
            var host = typeof(CanaryHealthSentinel).Assembly;                                   // MS2E.StockSuite
            var rcl = typeof(MS2E.UI.Portable.Ports.Validate.IValidateRepositoryPort).Assembly; // MS2E.UI.Portable

            // ★RCL 은 호스트/엔진을 역참조하지 않음(0 Concrete Ref 물증).
            Assert.True(GoldenMasterBootSentinel.RclReferencesNoEngine(rcl, host));

            var safe = new FakeProbe { LiveArmedFalse = true, DualKeyAbsent = true, VaultSealed = true };
            var rep = GoldenMasterBootSentinel.Scan(host, rcl, safe, out var checks);
            _out.WriteLine($"[GoldenMaster] total={rep.Total} passed={rep.Passed} allPassed={rep.AllPassed}");
            Assert.Equal(5, rep.Total);
            Assert.True(rep.AllPassed);   // RCL 순수 + 래칫 + 안전 프로브 → 골든마스터 통과

            // ★미검증 환경(무장됨/이중키 연결/금고 미봉인) → 부팅 fail-closed 거부.
            var unsafe1 = new FakeProbe { LiveArmedFalse = false, DualKeyAbsent = true, VaultSealed = true };
            Assert.False(GoldenMasterBootSentinel.Scan(host, rcl, unsafe1, out _).AllPassed);
            var unsafe2 = new FakeProbe { LiveArmedFalse = true, DualKeyAbsent = false, VaultSealed = true };
            Assert.False(GoldenMasterBootSentinel.Scan(host, rcl, unsafe2, out _).AllPassed);
            var unsafe3 = new FakeProbe { LiveArmedFalse = true, DualKeyAbsent = true, VaultSealed = false };
            Assert.False(GoldenMasterBootSentinel.Scan(host, rcl, unsafe3, out _).AllPassed);
            Assert.False(GoldenMasterBootSentinel.Scan(host, rcl, null!, out _).AllPassed);   // 프로브 없음 → fail-closed
        }
    }

    // ── 적대검증(δ=0) ──────────────────────────────────────────────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class Phase98GoldenMasterConcurrencyTests
    {
        // 리셋 가능 사전할당 커서(δ=0 측정 — 매 반복 Reset 후 실제 64행 투사).
        private sealed class NumCur : IRowCursor
        {
            private readonly int _len; private int _i = -1;
            public NumCur(int len) => _len = len;
            public void Reset() => _i = -1;
            public bool Read() => ++_i < _len;
            public long Int64(int col) => _i;
            public double Double(int col) => _i;
            public decimal Decimal(int col) => _i;
            public string Str(int col) => "";
            public DateTime Time(int col) => default;
            public bool IsNull(int col) => false;
        }
        private readonly record struct Row(long A, double B);
        private static Row Proj(IRowCursor x) => new(x.Int64(0), x.Double(1));   // 캡처 없는 정적 델리게이트(할당 0)

        // ★Project 핫패스 힙 0(δ=0) — 사전할당 커서 리셋 + stackalloc dest, 매 반복 실제 64행 투사.
        [Fact]
        public void Mapper_Project_HotPath_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    Span<Row> dst = stackalloc Row[64];
                    var cur = new NumCur(64);
                    for (int i = 0; i < 300_000; i++) { cur.Reset(); ZeroAllocRowStreamMapper.Project(cur, dst, Proj); }   // 워밍업
                    long minAlloc = long.MaxValue;   // 람다 캡처 불가(Span 로컬) — ZeroAllocProbe 와 같은 다중 창 최솟값을 인라인으로
                    for (int zw = 0; zw < ZeroAllocProbe.DefaultWindows && minAlloc != 0; zw++)
                    {
                        long zb = GC.GetAllocatedBytesForCurrentThread();
                        for (int i = 0; i < 100_000; i++) { cur.Reset(); ZeroAllocRowStreamMapper.Project(cur, dst, Proj); }
                        minAlloc = Math.Min(minAlloc, GC.GetAllocatedBytesForCurrentThread() - zb);
                    }
                    alloc = minAlloc;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★실제 64행 투사 반복 힙 0
        }

        // ★Evaluate 커널 δ=0.
        [Fact]
        public void BootSentinel_Evaluate_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    Span<GoldenMasterCheck> chk = stackalloc GoldenMasterCheck[5];
                    for (int i = 0; i < 5; i++) chk[i] = new GoldenMasterCheck((GmCheckId)(byte)i, true);
                    for (int i = 0; i < 300_000; i++) GoldenMasterBootSentinel.Evaluate(chk);
                    bool sink = false;
                    long minAlloc = long.MaxValue;   // 람다 캡처 불가(Span 로컬) — ZeroAllocProbe 와 같은 다중 창 최솟값을 인라인으로
                    for (int zw = 0; zw < ZeroAllocProbe.DefaultWindows && minAlloc != 0; zw++)
                    {
                        long zb = GC.GetAllocatedBytesForCurrentThread();
                        sink = false;
                        for (int i = 0; i < 100_000; i++) sink ^= GoldenMasterBootSentinel.Evaluate(chk).AllPassed;
                        minAlloc = Math.Min(minAlloc, GC.GetAllocatedBytesForCurrentThread() - zb);
                    }
                    alloc = minAlloc;
                    if (sink && alloc < 0) err = new Exception("unreachable");
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }
    }
}
