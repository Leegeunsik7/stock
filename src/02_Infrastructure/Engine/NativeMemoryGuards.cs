// ============================================================================
// NativeMemoryGuards — [크래시 트리아지 · 추천 A] 네이티브 버퍼 수명 게이트(방향 B) + 전/후방 카나리 경계 가드(방향 A) · δ=0
//
//  실결함(2026-10-07 정적 전수 조사 → 코드 확인): NativeMemory 를 직접 소유한 엔진들(ClaudeBlackSwanEngine · CacheLineCoherenceSentinel ·
//    ZeroAllocAsyncCompressedLogger · SimdAlignedRingVectorizer)은 연산이 '포인터 null 확인 → 포인터 사용' 이고 Dispose 가 '즉시 해제' 다.
//    다른 스레드가 확인과 사용 사이에 있을 때 Dispose 가 끼면 해제된 힙에 쓴다(use-after-free) — 즉시 크래시가 아니라 이후 무관한
//    지점의 힙 손상(0xC0000374)·AV 로 나타나 원인 테스트와 크래시 테스트가 어긋난다. 테스트 하네스의 'Join(타임아웃) 단언 실패 →
//    using Dispose' 경로(전체 스위트 부하로 기아가 생기면 실제로 도달)가 그 창을 연다.
//
//  ① NativeLifetimeGate(방향 B · 진행 중 연산 배수): int 1개 상태 = 하위 31비트 진행 중 연산 수 + 부호 비트 폐쇄 표지.
//     TryEnter = Interlocked.Increment → 음수(폐쇄)면 되돌리고 거부 · Exit = Decrement · Close = 부호 비트 원자 설정 후 진행 수 0 까지
//     유한 대기(기본 2초) → 배수 성공일 때만 호출측이 해제, 실패면 '누수'(해제된 메모리에 쓰는 것보다 누수가 안전).
//     비용: 연산당 Interlocked 2회 · 힙 0B · 락 0.
//  ② NativeCanaryBlock(방향 A · 경계 가드): 요청 크기 앞뒤에 64B(캐시라인) 가드를 붙여 할당하고 각 가드 첫 16B 에 고정 패턴을 심는다.
//     Verify(O(1) · δ=0) = 전/후방 16B 패턴 대조 → 월경(overrun/underrun)·해제 후 재사용 오염 탐지. Free 는 검증 후 해제하며 파열이면
//     해제하지 않고(손상 힙에 free 를 얹으면 손상 증폭) false 반환 — 호출측이 기록/차단. 사용자 영역 포인터는 64B 정렬 유지.
//  ★지시서의 '0.1ns 이내 중단'은 측정 불가 단위 — 연산당 검증은 수 ns(16B 비교 2회). 쓰기 순간이 아니라 '검증 시점'에 잡는다(정직).
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>네이티브 버퍼 소유자의 진행 중 연산 배수 게이트(필드 int 1개에 대해 동작 · δ=0).</summary>
    public static class NativeLifetimeGate
    {
        public const int ClosedBit = int.MinValue;
        public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(2);

        /// <summary>연산 진입. false = 폐쇄됨(해제 중/해제됨) — 포인터 접근 금지.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryEnter(ref int gate)
        {
            if (Interlocked.Increment(ref gate) > 0) return true;
            Interlocked.Decrement(ref gate);
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Exit(ref int gate) => Interlocked.Decrement(ref gate);

        public static bool IsClosed(ref int gate) => Volatile.Read(ref gate) < 0;
        public static int InFlight(ref int gate) => Volatile.Read(ref gate) & int.MaxValue;

        /// <summary>
        /// 폐쇄 + 배수. 반환 true = 진행 중 연산 0 확인(해제 안전) · false = 시한 내 미배수(해제 금지 — 누수) 또는 이미 폐쇄됨.
        /// 1회만 true 를 반환(중복 Dispose 의 이중 해제 차단).
        /// </summary>
        public static bool CloseAndDrain(ref int gate, TimeSpan timeout)
        {
            int cur;
            do
            {
                cur = Volatile.Read(ref gate);
                if (cur < 0) return false;                                       // 이미 폐쇄(이중 해제 차단)
            } while (Interlocked.CompareExchange(ref gate, cur | ClosedBit, cur) != cur);
            long deadline = Environment.TickCount64 + (long)Math.Max(0, timeout.TotalMilliseconds);
            var sw = new SpinWait();
            while ((Volatile.Read(ref gate) & int.MaxValue) != 0)
            {
                if (Environment.TickCount64 > deadline) return false;
                sw.SpinOnce(sleep1Threshold: 20);
            }
            return true;
        }
    }

    /// <summary>전/후방 카나리 가드가 붙은 64B 정렬 네이티브 블록(정적 도우미 · δ=0 검증).</summary>
    public static unsafe class NativeCanaryBlock
    {
        public const int GuardBytes = 64;                                       // 캐시라인 — 사용자 포인터 정렬 유지
        private const ulong FrontA = 0xF0E1D2C3B4A59687UL, FrontB = 0x0123456789ABCDEFUL;
        private const ulong BackA = 0x8796A5B4C3D2E1F0UL, BackB = 0xFEDCBA9876543210UL;

        // 헤더(앞 가드 안 · 패턴 뒤): +16 사용자 크기(8B)
        private const int SizeOffset = 16;

        /// <summary>size 바이트 사용자 영역(0 초기화) + 앞/뒤 가드. 반환 = 사용자 포인터(64B 정렬).</summary>
        public static void* Alloc(nuint size)
        {
            if (size == 0) throw new ArgumentOutOfRangeException(nameof(size));
            nuint total = size + 2 * (nuint)GuardBytes;
            byte* raw = (byte*)NativeMemory.AlignedAlloc(total, GuardBytes);
            NativeMemory.Clear(raw, total);
            ((ulong*)raw)[0] = FrontA; ((ulong*)raw)[1] = FrontB;
            *(ulong*)(raw + SizeOffset) = size;
            byte* back = raw + GuardBytes + size;
            ((ulong*)back)[0] = BackA; ((ulong*)back)[1] = BackB;
            return raw + GuardBytes;
        }

        /// <summary>경계 무결성(O(1) · δ=0). 앞/뒤 16B 패턴 · 기록 크기 일치.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Verify(void* user, nuint expectedSize)
        {
            if (user == null) return false;
            byte* raw = (byte*)user - GuardBytes;
            if (((ulong*)raw)[0] != FrontA || ((ulong*)raw)[1] != FrontB || *(ulong*)(raw + SizeOffset) != expectedSize) return false;
            byte* back = (byte*)user + expectedSize;
            return ((ulong*)back)[0] == BackA && ((ulong*)back)[1] == BackB;
        }

        /// <summary>검증 후 해제. 파열 = 해제하지 않고 false(손상 증폭 차단 · 호출측 기록). 해제 후 패턴을 지워 재검증이 실패하게 한다.</summary>
        public static bool Free(void* user, nuint expectedSize)
        {
            if (user == null) return false;
            if (!Verify(user, expectedSize)) return false;
            byte* raw = (byte*)user - GuardBytes;
            ((ulong*)raw)[0] = 0; ((ulong*)raw)[1] = 0;
            NativeMemory.AlignedFree(raw);
            return true;
        }
    }
}
