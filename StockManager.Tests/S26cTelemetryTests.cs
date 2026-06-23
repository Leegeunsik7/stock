using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // S26-c(Horizon B) — 락-프리 단방향 텔레메트리 채널.
    //   §1 seqlock round-trip·torn-read 멸절·버전 단조  §2 무할당(δ=0)·비블록  §3 캐시라인 격리 레이아웃
    public sealed class S26cTelemetryTests
    {
        private static ForensicTelemetry T(long s) =>
            new(persistSeq: s, lastPersistMono: s * 11, forensicSeqlockVersion: s,
                freshnessLagTicks: s * 13, forensicCount: (int)(s % 97), integrityScore: (int)(s % 101),
                genomeFp: (uint)(s * 3), forensicFp: (uint)(s * 7));

        // ═══════════════════ §1 seqlock ═══════════════════

        [Fact]
        public void PublishTryRead_RoundTrip_AllFields()
        {
            var ch = new ForensicTelemetryChannel();
            ch.Publish(T(42));
            Assert.True(ch.TryRead(out var t));
            Assert.Equal(42, t.PersistSeq);
            Assert.Equal(42 * 11, t.LastPersistMono);
            Assert.Equal(42, t.ForensicSeqlockVersion);
            Assert.Equal((uint)(42 * 3), t.GenomeFp);
            Assert.Equal((uint)(42 * 7), t.ForensicFp);
            Assert.Equal(2, ch.Version);   // 1회 발행 → 커서 2(짝)
        }

        [Fact]
        public void TryRead_BeforeAnyPublish_DefaultStable()
        {
            var ch = new ForensicTelemetryChannel();
            Assert.True(ch.TryRead(out var t));   // 버전 0(짝)·default — torn 아님
            Assert.False(t.HasValue);
            Assert.Equal(0, ch.Version);
        }

        [Fact]
        public void Version_Monotonic_TwoPerPublish()
        {
            var ch = new ForensicTelemetryChannel();
            for (long s = 1; s <= 1000; s++) ch.Publish(T(s));
            Assert.Equal(2000, ch.Version);   // 발행마다 +2, 항상 짝
        }

        [Fact]
        public void TornRead_Melted_UnderConcurrentPublish()
        {
            // 단일 라이터 고빈도 발행 중 2 리더가 무할당 독출 — 필드 간 불변식이 깨지면 torn.
            //   불변식: GenomeFp==PersistSeq*3 ∧ ForensicFp==PersistSeq*7 ∧ SeqlockVersion==PersistSeq.
            // ★적대적 리뷰(major) 수정 — 단순 torn==0 은 '경합 없이' 통과할 수 있어 공허하다(retry 경로 미실증).
            //   okReads(동시 발행 중 성공 독출)와 maxSeq(리더가 따라잡은 최신 발행)를 집계해 '실제로 발행 폭풍
            //   한가운데서 무수히 읽었음'을 단언 → torn==0 이 비공허하게 seqlock 견고성을 증명한다.
            var ch = new ForensicTelemetryChannel();
            long torn = 0, okReads = 0, maxSeq = 0; bool stop = false;
            void Reader()
            {
                while (!Volatile.Read(ref stop))
                {
                    if (ch.TryRead(out var t) && t.HasValue)
                    {
                        Interlocked.Increment(ref okReads);
                        if (t.PersistSeq > Interlocked.Read(ref maxSeq)) Interlocked.Exchange(ref maxSeq, t.PersistSeq);
                        if (t.GenomeFp != (uint)(t.PersistSeq * 3) ||
                            t.ForensicFp != (uint)(t.PersistSeq * 7) ||
                            t.ForensicSeqlockVersion != t.PersistSeq)
                            Interlocked.Increment(ref torn);
                    }
                }
            }
            var r1 = new Thread(Reader) { IsBackground = true };
            var r2 = new Thread(Reader) { IsBackground = true };
            r1.Start(); r2.Start();
            for (long s = 1; s <= 200000; s++) ch.Publish(T(s));
            Volatile.Write(ref stop, true); r1.Join(3000); r2.Join(3000);
            Assert.Equal(0, torn);                                  // torn read 0(불변식 무손상)
            Assert.True(okReads > 1000, $"동시 독출 비공허 입증 — okReads={okReads}");   // 발행 중 실제로 무수히 읽음
            Assert.True(maxSeq > 1000, $"리더가 발행 폭풍을 추격 — maxSeq={maxSeq}");      // 정지 시점이 아닌 라이브 값 독출
        }

        // ═══════════════════ §2 무할당 · 비블록 ═══════════════════

        [Fact]
        public void PublishTryRead_ZeroAllocation()
        {
            // ★적대적 리뷰(major) 수정 — 1회 워밍업은 tiered JIT 재컴파일이 측정 중 δ를 흔들어 flaky 위험.
            //   충분히 워밍업(10k)해 핫패스 코드가 안정화된 뒤 측정. 전제(정직): 단일 스레드·러너 백그라운드
            //   할당 없음 가정 하의 GC.GetAllocatedBytesForCurrentThread 델타 — 이 스레드 할당만 본다.
            var ch = new ForensicTelemetryChannel();
            var t = T(7);
            for (int i = 0; i < 10_000; i++) { ch.Publish(t); ch.TryRead(out _); }   // 워밍업(tiered 안정화)
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50_000; i++) { ch.Publish(t); ch.TryRead(out _); }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, delta);   // 값 타입 in/out — 박싱·힙 0
        }

        [Fact]
        public void Reader_NeverBlocks_StressReturnsPromptly()
        {
            // 리더가 코어를 절대 기다리지 않음 — 200k 발행 동안 리더 루프가 완주(행 없음)함을 시간으로 증명.
            var ch = new ForensicTelemetryChannel();
            bool stop = false; long reads = 0;
            var reader = new Thread(() => { while (!Volatile.Read(ref stop)) { ch.TryRead(out _); Interlocked.Increment(ref reads); } }) { IsBackground = true };
            reader.Start();
            for (long s = 1; s <= 200000; s++) ch.Publish(T(s));
            Volatile.Write(ref stop, true);
            Assert.True(reader.Join(3000));   // 3초 내 완주 = 비블록
            Assert.True(Interlocked.Read(ref reads) > 0);
        }

        // ═══════════════════ §3 캐시라인 격리 레이아웃 ═══════════════════

        [Fact]
        public void CacheLine_FieldOffsets_Declared_PaddingRegressionGuard()
        {
            // ★적대적 리뷰(정직 한계) — 이 단언은 '선언적 FieldOffset(의도)'만 잠근다(패딩 축소 회귀 방지용).
            //   런타임 절대주소의 64B 정렬이나 실제 false-sharing 부재를 증명하지 '않는다' — .NET 힙은 객체
            //   베이스만 정렬하므로 Version 은 라인 내 비정렬(실측 40/56)에 착지한다. 격리는 '필드당 전용 패딩'에서
            //   오는 것이지 정렬에서 오지 않는다(ForensicTelemetry.cs Cell 주석 참조).
            var cell = typeof(ForensicTelemetryChannel).GetNestedType("Cell", BindingFlags.NonPublic);
            Assert.NotNull(cell);
            int vOff = cell!.GetField("Version")!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
            int dOff = cell.GetField("Data")!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
            Assert.True(vOff >= 64, "Version 앞 선행 64B 전용 패딩 선언");
            Assert.True(dOff - vOff >= 64, "Version↔Data 별도 캐시라인 선언");
        }
    }
}
