// ============================================================================
// UnmanagedOrderBookBuffer — [저지연 · 추천 A] GC 힙 밖(NativeMemory) L2 10호가 스냅샷 저장소 + 범용 네이티브 seqlock 표
//
//  NativeSeqlockTable<T>(T : unmanaged):
//   · 64B 정렬 NativeMemory.AlignedAlloc 1회 — 슬롯 = [키 8B][버전 8B][수신 µs 8B][T] 를 64B 배수로 패딩(인접 슬롯 거짓공유 차단).
//   · 기록: Monitor 직렬화(비경합 = 힙 0) + seqlock(홀 = 기록 중). 판독: 락 없이 seqlock 재검(값 통째 복사 · 찢김 0).
//   · 수명: NativeMemoryHandle(SafeHandle · 임계 종료자) — Dispose 시 즉시 해제, 미처분이면 GC 종료자/프로세스 종료 경로에서
//     해제(.NET 8 에는 AppDomain 언로드가 없다 — AssemblyLoadContext 언로드·프로세스 종료 모두 SafeHandle 종료자가 담당).
//   · 사용 중 해제 차단: 모든 접근은 DangerousAddRef/DangerousRelease(참조 계수 · 힙 0) 안에서만 포인터를 역참조 —
//     Dispose 가 동시 진행돼도 마지막 Release 뒤에만 실제 Free(use-after-free 0). 처분 후 접근 = false(예외 없음).
//   · 누수 실측: 정적 계수 LiveAllocations/LiveBytes(할당 +1 · 해제 −1) — 테스트가 0 복귀를 단언.
//
//  UnmanagedOrderBookBuffer: 종목 키 → OrderBookPayload(10호가 · 원/주) + 수신 µs. 키움 L2 프레임 전량 보존(파서 판정 무관 원본).
//  ★정직: L2Depth/OrderBookPayload 는 참조 필드가 없어 관리 배열이었어도 GC 가 내용을 스캔하지 않는다 — 이 격상의 실익은
//   'GC 가 이동/압축하지 않는 고정 주소 + 대용량 표가 LOH/Gen2 예산에 잡히지 않음 + 결정론적 해제'이지 할당 감소가 아니다
//   (이전 구현도 갱신 핫패스는 δ=0 이었다).
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using MS2E.Infrastructure;

namespace StockManager.Buffers
{
    /// <summary>NativeMemory 정렬 블록 SafeHandle — 임계 종료자로 프로세스 종료/미처분 시에도 해제 보장.</summary>
    public sealed unsafe class NativeMemoryHandle : SafeHandle
    {
        private static long s_liveAllocations, s_liveBytes;
        private readonly long _bytes;

        public static long LiveAllocations => Interlocked.Read(ref s_liveAllocations);
        public static long LiveBytes => Interlocked.Read(ref s_liveBytes);

        public NativeMemoryHandle(nuint bytes, nuint alignment = 64) : base(IntPtr.Zero, ownsHandle: true)
        {
            if (bytes == 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            void* p = NativeMemory.AlignedAlloc(bytes, alignment);
            NativeMemory.Clear(p, bytes);
            SetHandle((IntPtr)p);
            _bytes = (long)bytes;
            Interlocked.Increment(ref s_liveAllocations);
            Interlocked.Add(ref s_liveBytes, _bytes);
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            NativeMemory.AlignedFree((void*)handle);
            Interlocked.Decrement(ref s_liveAllocations);
            Interlocked.Add(ref s_liveBytes, -_bytes);
            return true;
        }

        public byte* Pointer => (byte*)handle;
    }

    /// <summary>네이티브 seqlock 해시 표(키 long → T unmanaged). 기록 직렬화 · 판독 락프리 · δ=0 · 결정론적 해제.</summary>
    public sealed unsafe class NativeSeqlockTable<T> : IDisposable where T : unmanaged
    {
        private const int KeyOff = 0, VerOff = 8, AtOff = 16, ValOff = 24;
        private readonly NativeMemoryHandle _mem;
        private readonly int _mask, _stride;
        private readonly object _writeGate = new();
        private int _count;
        private long _tableFull;

        public NativeSeqlockTable(int capacityPow2)
        {
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0) throw new ArgumentException("용량은 2의 거듭제곱", nameof(capacityPow2));
            _mask = capacityPow2 - 1;
            _stride = (ValOff + sizeof(T) + 63) & ~63;                              // 64B 배수 슬롯
            _mem = new NativeMemoryHandle((nuint)((long)_stride * capacityPow2));
        }

        public int Capacity => _mask + 1;
        public int SlotBytes => _stride;
        public int Count => Volatile.Read(ref _count);
        public long TableFull => Interlocked.Read(ref _tableFull);
        public bool IsDisposed => _mem.IsClosed;

        /// <summary>기록(δ=0). 표 포화 · 처분 후 = false.</summary>
        public bool Write(long key, in T value, long atMicros)
        {
            if (key == 0) key = 1;
            bool added = false;
            if (_mem.IsClosed) return false;
            try
            {
                _mem.DangerousAddRef(ref added);
                byte* b = _mem.Pointer;
                lock (_writeGate)
                {
                    int i = (int)((ulong)key & (ulong)_mask);
                    for (int probe = 0; probe <= _mask; probe++, i = (i + 1) & _mask)
                    {
                        byte* s = b + (long)i * _stride;
                        long k = Volatile.Read(ref *(long*)(s + KeyOff));
                        if (k != key && k != 0) continue;
                        Interlocked.Increment(ref *(long*)(s + VerOff));           // 홀 = 기록 중
                        *(T*)(s + ValOff) = value;
                        Volatile.Write(ref *(long*)(s + AtOff), atMicros);
                        Interlocked.Increment(ref *(long*)(s + VerOff));           // 짝 = 발행
                        if (k == 0) { Volatile.Write(ref *(long*)(s + KeyOff), key); _count++; }
                        return true;
                    }
                    Interlocked.Increment(ref _tableFull);
                    return false;
                }
            }
            catch (ObjectDisposedException) { return false; }
            finally { if (added) _mem.DangerousRelease(); }
        }

        /// <summary>판독(δ=0 · 락프리 seqlock). 미등록 · 지속 경합 · 처분 후 = false.</summary>
        public bool TryRead(long key, out T value, out long atMicros)
        {
            value = default; atMicros = 0;
            if (key == 0) key = 1;
            bool added = false;
            if (_mem.IsClosed) return false;
            try
            {
                _mem.DangerousAddRef(ref added);
                byte* b = _mem.Pointer;
                int i = (int)((ulong)key & (ulong)_mask);
                for (int probe = 0; probe <= _mask; probe++, i = (i + 1) & _mask)
                {
                    byte* s = b + (long)i * _stride;
                    long k = Volatile.Read(ref *(long*)(s + KeyOff));
                    if (k == 0) return false;
                    if (k != key) continue;
                    for (int spin = 0; spin < 64; spin++)
                    {
                        long v1 = Volatile.Read(ref *(long*)(s + VerOff));
                        if ((v1 & 1) != 0) continue;
                        T copy = *(T*)(s + ValOff);
                        long at = Volatile.Read(ref *(long*)(s + AtOff));
                        Interlocked.MemoryBarrier();
                        if (Volatile.Read(ref *(long*)(s + VerOff)) != v1) continue;
                        value = copy; atMicros = at;
                        return true;
                    }
                    return false;
                }
                return false;
            }
            catch (ObjectDisposedException) { return false; }
            finally { if (added) _mem.DangerousRelease(); }
        }

        public void Dispose() => _mem.Dispose();
    }

    /// <summary>L2 10호가 원본 스냅샷 네이티브 저장소(종목 KSE 코드 키 · 수신 µs).</summary>
    public sealed class UnmanagedOrderBookBuffer : IDisposable, IDepthSnapshotStore
    {
        private readonly NativeSeqlockTable<OrderBookPayload> _table;
        private readonly Func<long> _nowMicros;
        private long _writes;

        public UnmanagedOrderBookBuffer(int capacityPow2 = 4096, Func<long>? nowMicros = null)
        {
            _table = new NativeSeqlockTable<OrderBookPayload>(capacityPow2);
            _nowMicros = nowMicros ?? (static () => (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency));
        }

        public int Symbols => _table.Count;
        public long Writes => Interlocked.Read(ref _writes);
        public int SlotBytes => _table.SlotBytes;
        public bool IsDisposed => _table.IsDisposed;

        public bool Write(in OrderBookPayload book)
        {
            if (!_table.Write(book.SymbolCode, in book, _nowMicros())) return false;
            Interlocked.Increment(ref _writes);
            return true;
        }

        public bool TryRead(long symbolCode, out OrderBookPayload book, out long ageMicros)
        {
            ageMicros = long.MaxValue;
            if (!_table.TryRead(symbolCode, out book, out long at)) return false;
            ageMicros = Math.Max(0, _nowMicros() - at);
            return true;
        }

        public void Dispose() => _table.Dispose();
    }

    /// <summary>원본 10호가 저장소 계약(테스트/소비자 교체 지점).</summary>
    public interface IDepthSnapshotStore
    {
        bool Write(in OrderBookPayload book);
        bool TryRead(long symbolCode, out OrderBookPayload book, out long ageMicros);
    }
}
