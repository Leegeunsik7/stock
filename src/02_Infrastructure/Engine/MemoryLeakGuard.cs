// ============================================================================
// MemoryLeakGuard — S18 #1: 고아 핸들러·리소스 누수 추적 가드 (v3.95.0)
//
//  S14~S16에서 구축된 고속 이벤트 채널(SlippageSentinel 구독, 500ms 하트비트)과
//  10fps 렌더 스로틀 타이머는, 디바이스 전원 오프나 Blazor 컴포넌트 비정상 해제 시
//  구독 해제(unsubscribe)·Dispose 누락으로 고아 객체(Orphaned Handler)가 되어
//  메모리를 야금야금 잠식할 수 있다. 본 가드는 그 누수를 결정론적으로 계측한다.
//
//  ── 설계: 리스(Lease) 기반 결정론 추적 ─────────────────────────────────────
//  · 컴포넌트는 구독/타이머 1건마다 Acquire() 로 리스를 발급받고, Dispose 시 반납.
//  · 반납 누락 = LiveCount 가 0으로 떨어지지 않음 ⇒ 누수가 유닛 테스트에서 가시화.
//  · 카운터는 전부 Interlocked — 제로-할당 핫패스(리스 객체는 구성 시점 1회 할당).
//  · 리스 Dispose 는 멱등(idempotent) — Interlocked.Exchange(ref _guard, null)로
//    더블-디스포즈에도 단 1회만 반납.
//
//  ── Probe(): 보조 신호 ──────────────────────────────────────────────────────
//  GC 관리 힙·워킹셋·Gen2 수집 횟수를 스냅샷해 LiveLeases 와 함께 MemoryReport 로 반환.
//  외부 의존성 0 (System.GC / System.Environment 만 사용).
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace StockManager.Common
{
    // ── 메모리·리스 스냅샷 ──────────────────────────────────────────────────────

    /// <summary>Probe() 1회의 메모리·리스 계측 스냅샷.</summary>
    public sealed record MemoryReport(
        int  LiveLeases,        // 현재 반납되지 않은 리스 수 (핵심 누수 지표)
        long TotalAcquired,     // 누적 발급 수
        long TotalReleased,     // 누적 반납 수
        long ManagedHeapBytes,  // GC.GetTotalMemory(false)
        long WorkingSetBytes,   // Environment.WorkingSet
        int  Gen2Collections,   // GC.CollectionCount(2)
        bool LeakSuspected);    // LiveLeases > LeaseLeakThreshold

    // ── 가드 ────────────────────────────────────────────────────────────────────

    public sealed class MemoryLeakGuard
    {
        /// <summary>프로세스 전역 공유 인스턴스 (앱 호스트가 부팅 시 1회 Probe 로그).</summary>
        public static MemoryLeakGuard Shared { get; } = new MemoryLeakGuard();

        private int  _liveCount;
        private long _totalAcquired;
        private long _totalReleased;
        private readonly ConcurrentDictionary<string, int> _byCategory =
            new(StringComparer.Ordinal);

        /// <summary>이 수를 초과하는 미반납 리스는 누수로 의심 (Probe.LeakSuspected).</summary>
        public int LeaseLeakThreshold { get; init; } = 512;

        // ── 메트릭 읽기 (원자) ───────────────────────────────────────────────────
        public int  LiveCount      => Volatile.Read(ref _liveCount);
        public long TotalAcquired  => Interlocked.Read(ref _totalAcquired);
        public long TotalReleased  => Interlocked.Read(ref _totalReleased);

        /// <summary>특정 카테고리(예: "render-timer", "sentinel-sub")의 미반납 리스 수.</summary>
        public int LiveCountFor(string category)
            => _byCategory.TryGetValue(Norm(category), out var c) ? c : 0;

        // ── 리스 발급/반납 ───────────────────────────────────────────────────────

        /// <summary>
        /// 리소스 리스 1건 발급. 반환된 ResourceLease 를 컴포넌트 Dispose 시 반납하라.
        /// 발급은 구성(구독·타이머 생성) 시점 1회만 일어나는 콜드 패스다.
        /// </summary>
        public ResourceLease Acquire(string category = "default")
        {
            string cat = Norm(category);
            Interlocked.Increment(ref _liveCount);
            Interlocked.Increment(ref _totalAcquired);
            _byCategory.AddOrUpdate(cat, 1, static (_, c) => c + 1);
            return new ResourceLease(this, cat);
        }

        // 리스가 반납될 때만 호출 (ResourceLease.Dispose 내부에서 1회 보장)
        internal void Release(string category)
        {
            Interlocked.Decrement(ref _liveCount);
            Interlocked.Increment(ref _totalReleased);
            _byCategory.AddOrUpdate(category, 0, static (_, c) => c > 0 ? c - 1 : 0);
        }

        // ── 프로파일링 ───────────────────────────────────────────────────────────

        /// <summary>현재 리스·GC·워킹셋을 스냅샷해 MemoryReport 로 반환.</summary>
        public MemoryReport Probe()
        {
            int live = LiveCount;
            long heap, ws;
            int gen2;
            try { heap = GC.GetTotalMemory(false); } catch { heap = 0L; }
            try { ws   = Environment.WorkingSet;    } catch { ws   = 0L; }
            try { gen2 = GC.CollectionCount(2);     } catch { gen2 = 0;  }

            return new MemoryReport(
                LiveLeases:       live,
                TotalAcquired:    TotalAcquired,
                TotalReleased:    TotalReleased,
                ManagedHeapBytes: heap,
                WorkingSetBytes:  ws,
                Gen2Collections:  gen2,
                LeakSuspected:    live > LeaseLeakThreshold);
        }

        /// <summary>테스트 전용 — 카운터·카테고리 초기화. 프로덕션 핫패스에서 호출 금지.</summary>
        public void ResetCounters()
        {
            Interlocked.Exchange(ref _liveCount, 0);
            Interlocked.Exchange(ref _totalAcquired, 0);
            Interlocked.Exchange(ref _totalReleased, 0);
            _byCategory.Clear();
        }

        private static string Norm(string? category)
            => string.IsNullOrEmpty(category) ? "default" : category;
    }

    // ── 리스 토큰 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// MemoryLeakGuard 가 발급한 리소스 리스. Dispose 시 정확히 1회 반납(멱등).
    /// 구독 해제·타이머 정지와 함께 컴포넌트 Dispose 에서 호출하라.
    /// </summary>
    public sealed class ResourceLease : IDisposable
    {
        private MemoryLeakGuard? _guard;   // null = 이미 반납됨
        private readonly string  _category;

        internal ResourceLease(MemoryLeakGuard guard, string category)
        {
            _guard    = guard;
            _category = category;
        }

        /// <summary>이미 반납되었는지 여부.</summary>
        public bool IsReleased => Volatile.Read(ref _guard) is null;

        public void Dispose()
        {
            // 멱등: 첫 Dispose 만 _guard 를 원자적으로 가져가 반납. 더블-디스포즈 무해.
            var g = Interlocked.Exchange(ref _guard, null);
            g?.Release(_category);
        }
    }
}
