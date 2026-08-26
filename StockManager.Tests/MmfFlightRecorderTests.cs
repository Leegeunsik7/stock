// ============================================================================
// MmfFlightRecorderTests — v4.95.0 과제1: 무할당 순환 커널 블랙박스
//
//  §1 레코드 구조·기록·스냅샷   — 64B 고정·최신순 스냅샷·순환 덮어쓰기.
//  §2 봉인·헥스 렌더             — Seal 후 no-op·DroppedWhileSealed·글리프 헥스.
//  §3(별도) 멀티라이터 torn=0·δ=0·핫패스 무간섭(ConcurrencyStress).
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class MmfFlightRecorderTests
    {
        // ── §1 구조 · 기록 · 스냅샷 ────────────────────────────────────────────

        [Fact]
        public void FlightRecordEvent_Is_Exactly64Bytes()
        {
            Assert.Equal(64, Marshal.SizeOf<FlightRecordEvent>());
            Assert.Equal(64, MmfFlightRecorder.EventSize);
        }

        [Fact]
        public void Record_SnapshotRecent_MostRecentFirst()
        {
            var rec = new MmfFlightRecorder(16, () => 1000L);
            rec.Record(FlightEventKind.OrderForwarded, 1, 10, 20);
            rec.Record(FlightEventKind.RiskAdmit, 2, 30, 40);
            rec.Record(FlightEventKind.MmfPublish, 3, 50, 60);
            var dst = new FlightRecordEvent[10];
            int n = rec.SnapshotRecent(dst);
            Assert.Equal(3, n);
            Assert.Equal(3L, dst[0].SymbolCode);              // 최신 먼저
            Assert.Equal(2L, dst[1].SymbolCode);
            Assert.Equal(1L, dst[2].SymbolCode);
            Assert.Equal(FlightEventKind.MmfPublish, dst[0].Event);
            Assert.Equal(3L, dst[0].Sequence);                // 단조 시퀀스
            Assert.Equal(3L, rec.TotalRecorded);
        }

        [Fact]
        public void Wrap_OverwritesOldest_SnapshotReturnsRecent()
        {
            var rec = new MmfFlightRecorder(4, () => 1L);     // 용량 4(2^2)
            for (long i = 1; i <= 10; i++) rec.Record(FlightEventKind.NewsIngest, i, i, i);
            Assert.Equal(4, rec.Capacity);
            Assert.Equal(10L, rec.TotalRecorded);
            Assert.Equal(4L, rec.RecordedCount);              // 상주 = 용량
            var dst = new FlightRecordEvent[4];
            int n = rec.SnapshotRecent(dst);
            Assert.Equal(4, n);
            Assert.Equal(10L, dst[0].SymbolCode);             // 최근 4개(10,9,8,7)
            Assert.Equal(9L, dst[1].SymbolCode);
            Assert.Equal(8L, dst[2].SymbolCode);
            Assert.Equal(7L, dst[3].SymbolCode);
        }

        [Fact]
        public void SnapshotRecent_ManyWraps_BoundedAndCorrect()
        {
            // ★적대검증 회귀(C#7): 다중 wrap 후에도 스냅샷은 최근 창만 반환(O(head) 폭주 없이)·정합.
            var rec = new MmfFlightRecorder(8, () => 1L);   // 용량 8
            for (long i = 1; i <= 100_000; i++) rec.Record(FlightEventKind.MmfPublish, i, i, i);   // 12500 wrap
            var dst = new FlightRecordEvent[8];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = rec.SnapshotRecent(dst);
            sw.Stop();
            Assert.Equal(8, n);
            Assert.Equal(100_000L, dst[0].SymbolCode);      // 최근
            Assert.Equal(99_993L, dst[7].SymbolCode);       // 최근 8개(100000..99993)
            Assert.True(sw.ElapsedMilliseconds < 500, $"스캔이 O(head) 폭주(경과 {sw.ElapsedMilliseconds}ms) — 하한 미적용");
        }

        [Fact]
        public void Saturation_ReachesFull_AtCapacity()
        {
            var rec = new MmfFlightRecorder(8, () => 1L);
            Assert.Equal(0L, rec.SaturationRaw);
            for (int i = 0; i < 4; i++) rec.Record(FlightEventKind.MmfPublish, i, 0, 0);
            Assert.Equal(50_000_000L, rec.SaturationRaw);     // 4/8 = 0.5 × 10^8
            for (int i = 0; i < 100; i++) rec.Record(FlightEventKind.MmfPublish, i, 0, 0);
            Assert.Equal(100_000_000L, rec.SaturationRaw);    // 포화(순환)
        }

        // ── §2 봉인 · 헥스 렌더 ────────────────────────────────────────────────

        [Fact]
        public void Seal_StopsRecording_CountsDropped()
        {
            var rec = new MmfFlightRecorder(16, () => 1L);
            rec.Record(FlightEventKind.RiskAdmit, 1, 0, 0);
            Assert.False(rec.IsSealed);
            rec.Seal();
            Assert.True(rec.IsSealed);
            rec.Record(FlightEventKind.RiskAdmit, 2, 0, 0);   // 봉인 후 → no-op
            rec.Record(FlightEventKind.RiskAdmit, 3, 0, 0);
            Assert.Equal(1L, rec.TotalRecorded);              // 첫 1건만
            Assert.Equal(2L, rec.DroppedWhileSealed);
        }

        [Fact]
        public void HexRenderer_ProducesHexGlyphs()
        {
            var rec = new MmfFlightRecorder(8, () => 0x1122334455667788L);
            rec.Record(FlightEventKind.Freeze, 0xABCD, 0, 0);
            var dst = new FlightRecordEvent[4];
            int n = rec.SnapshotRecent(dst);
            string hex = FlightHexRenderer.Render(new ReadOnlySpan<FlightRecordEvent>(dst, 0, n));
            Assert.False(string.IsNullOrEmpty(hex));
            Assert.Contains(":", hex);                         // 오프셋 구분
            Assert.Contains("\n", hex);                        // 행 구분
            foreach (char c in hex)                            // 헥스 글리프·구분자만(per-byte ToString 회피 확인)
                Assert.True(Uri.IsHexDigit(c) || c == ' ' || c == ':' || c == '\n');
        }
    }

    // ── §3 멀티라이터 torn=0 · δ=0 · 핫패스 무간섭 ─────────────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class MmfFlightRecorderConcurrencyTests
    {
        [Fact]
        public void MultiWriter_Concurrent_NoLossNoTorn()
        {
            // 8스레드 × 5000 = 40,000 기록(용량 초과 없음). torn 검사: Arg0==Arg1==Arg2(찢김 시 불일치).
            const int threads = 8, perThread = 5000;
            var rec = new MmfFlightRecorder(1 << 18);   // 262,144 슬롯(> 40,000)
            var ts = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                int marker = t;
                ts[t] = new Thread(() =>
                {
                    for (int i = 0; i < perThread; i++)
                    { long v = ((long)marker << 40) | (long)i; rec.Record(FlightEventKind.MmfPublish, v, v, v, v, marker); }   // arg0=arg1=arg2=v(찢김 검출용 일치)
                });
            }
            foreach (var th in ts) th.Start();
            foreach (var th in ts) Assert.True(th.Join(30_000));

            Assert.Equal((long)threads * perThread, rec.TotalRecorded);   // 무손실
            var dst = new FlightRecordEvent[rec.Capacity];
            int n = rec.SnapshotRecent(dst);
            Assert.Equal(threads * perThread, n);                         // 전량 상주(wrap 없음)
            int torn = 0;
            for (int i = 0; i < n; i++)
            {
                var e = dst[i];
                if (e.Arg0 != e.Arg1 || e.Arg1 != e.Arg2) torn++;         // ★찢김(torn) 검출
                if (e.Event != FlightEventKind.MmfPublish) torn++;
            }
            Assert.Equal(0, torn);                                        // ★torn 0(tri-state 격리 성공)
        }

        [Fact]
        public void Record_HotLoop_ZeroAllocation()
        {
            var rec = new MmfFlightRecorder(1 << 16, () => 1000L);
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) rec.Record(FlightEventKind.MmfPublish, i, i, i);   // 워밍업(wrap 포함)
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 100_000; i++) rec.Record(FlightEventKind.RiskAdmit, i, i, i);
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★Record 힙 할당 0(δ=0)
        }

        [Fact]
        public void WiredIntoRiskShield_HotPath_StaysZeroAllocation()
        {
            // ★핫패스 무간섭 증명: 블랙박스를 리스크 실드에 결속해도 TryAdmit/Release 는 여전히 δ=0.
            var rec = new MmfFlightRecorder(1 << 16, () => 1000L);
            var shield = new PreTradeRiskShield(QMoney.Of(1_000_000_000m).Raw, QMoney.Of(1_000_000_000m).Raw, blackbox: rec);
            var buy = new OrderProposal(1, 0, 5930, OrderSide.Buy, 1, QPrice.Of(100m), QQty.Of(1m), 0);
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) if (shield.TryAdmit(buy, out var w)) shield.Release(w.ReservedNotionalRaw);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long sink = 0;
                    for (int i = 0; i < 100_000; i++) if (shield.TryAdmit(buy, out var d)) { sink += d.ReservedNotionalRaw; shield.Release(d.ReservedNotionalRaw); }
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                    GC.KeepAlive(sink);
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★블랙박스 결속 후에도 리스크 실드 핫패스 δ=0(무간섭)
            Assert.True(rec.TotalRecorded > 0);   // 실제로 기록됨(no-op 아님)
        }
    }
}
