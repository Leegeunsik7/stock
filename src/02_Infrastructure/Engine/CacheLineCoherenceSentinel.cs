// ============================================================================
// CacheLineCoherenceSentinel — v7.3.0 ★Think Beyond Gemini: 하드웨어 코히런스 센티널
//   "Cache-Line Isolation + Core-Migration + Arena-Canary — 하나의 코히런스 불변식으로 통합"
//
//  ── 왜 제미나이의 A/B 를 초월하는가 ────────────────────────────────────────
//  제미나이는 두 하드웨어 리스크를 서로 다른 두 모듈로 방어하자 제안했다:
//    · 아이디어 A — 캐시라인 정렬 패딩으로 False Sharing 차단(정적 방어).
//    · 아이디어 B — RIO/MMF 핀드 아레나 오염 감지(런타임 방어).
//  이 센티널은 이 둘이 사실 '하나의 불변식(하드웨어 메모리 코히런스)'의 두 얼굴임을 간파해 통합한다:
//    핫패스 코어별 카운터를 64B 정렬 언매니지드 아레나에 '각 카운터를 자기만의 캐시라인'에 배치하면 —
//    (1) 코어 간 False Sharing 이 '구조적으로 불가능'해진다(A 초월: 패딩이 아니라 라인-단위 물리 격리).
//    (2) 각 슬롯이 '마지막 기록 코어'를 봉인하므로, 서로 다른 코어가 같은 슬롯을 다투는 '라인 바운싱/코어
//        마이그레이션'을 δ=0 로 실시간 계수한다(핀 무효화·E-Core 강등의 정량 관측).
//    (3) 슬롯 경계의 카나리로 아레나 오염(네이티브 OOB/핀드 페이지 파열)을 O(1) 로 대조한다(B 초월: RIO 뿐
//        아니라 모든 핫패스 아레나에 적용되는 범용 무결성 봉인).
//    ∴ 카나리 파열 시 코히런스 퓨즈가 fail-closed 로 트립되어 무장을 '더 안전한 방향'으로 홀드한다.
//
//  ★불변: δ=0(Record/AttestArena/AttestIsolated 핫패스 힙 0·네이티브 포인터·Interlocked) · 결정론(순수) ·
//    fail-closed(카나리 파열 → 퓨즈 + 옵션 ForceFreeze·홀드) · liveArmed:false(순수 관측/진단 — IBroker·소켓·
//    onSend·PublishRaw 참조 0·퓨즈는 tighten-only). ★한국색상은 UI 계층. ★unsafe 는 이 하드웨어 패브릭에 국한.
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>하드웨어 코히런스 센티널(캐시라인 격리·코어 바운싱·아레나 카나리). IDisposable·δ=0.</summary>
    public sealed unsafe class CacheLineCoherenceSentinel : IDisposable
    {
        private const int  SlotLongs  = 8;      // 8 × 8B = 64B = 정확히 1 캐시라인
        private const int  CacheLine  = 64;
        private const int  C_COUNT = 0, C_CORE = 1, C_CANARY = 2;   // 슬롯 레이아웃(나머지 5 long = pad)
        private const long CanaryValue = unchecked((long)0xC0DECACE11FE0FF1UL);

        private long* _arena;                   // slots × 8 longs, 64B 정렬 네이티브(각 슬롯 = 자기 캐시라인)
        private readonly int _slots;
        private readonly OrderAttestationService? _attestation;
        private long _crossCoreWrites, _attests;
        private int  _fuseTripped, _disposed;

        public CacheLineCoherenceSentinel(int slots = 8, OrderAttestationService? attestation = null)
        {
            _slots = Math.Max(1, slots);
            _attestation = attestation;
            nuint bytes = (nuint)(_slots * SlotLongs * sizeof(long));
            _arena = (long*)NativeMemory.AlignedAlloc(bytes, (nuint)CacheLine);   // ★캐시라인 정렬
            NativeMemory.Clear(_arena, bytes);
            for (int i = 0; i < _slots; i++) _arena[i * SlotLongs + C_CANARY] = CanaryValue;   // 슬롯 경계 카나리 심기
        }

        public bool CoherenceFuseTripped => Volatile.Read(ref _fuseTripped) == 1;
        /// <summary>★안전 홀드 — 코히런스 파열 시 무장 술어 blockWhile 이 읽어 tighten-only 억제.</summary>
        public bool SafeHoldActive       => Volatile.Read(ref _fuseTripped) == 1;
        public long CrossCoreWrites      => Interlocked.Read(ref _crossCoreWrites);
        public long Attests              => Interlocked.Read(ref _attests);
        public int  Slots                => _slots;

        /// <summary>
        /// 캐시라인 격리 슬롯 카운터 증가 + 기록 코어 봉인(δ=0). 직전과 다른 코어가 같은 슬롯을 쓰면 '라인 바운싱/
        /// 코어 마이그레이션'으로 계수한다(False Sharing 은 각 슬롯이 자기 라인이라 구조적으로 불가). 슬롯당 1코어 사용이 이상적.
        /// </summary>
        public void Record(int slot)
        {
            long* a = _arena;
            if (a == null || (uint)slot >= (uint)_slots) return;
            long* s = a + slot * SlotLongs;
            int core = Thread.GetCurrentProcessorId();               // 관리형·δ=0·빠른 코어 ID
            long prev = Interlocked.Exchange(ref s[C_CORE], core);
            if (prev != core) Interlocked.Increment(ref _crossCoreWrites);   // 다른 코어가 이 슬롯을 씀(라인 바운싱 지표)
            Interlocked.Increment(ref s[C_COUNT]);
        }

        /// <summary>슬롯 카운터 값(δ=0·관측).</summary>
        public long CounterOf(int slot)
        {
            long* a = _arena;
            if (a == null || (uint)slot >= (uint)_slots) return 0;
            return Interlocked.Read(ref a[slot * SlotLongs + C_COUNT]);
        }

        /// <summary>
        /// 아레나 무결성 O(1) 대조(δ=0): 전 슬롯 경계 카나리를 검사한다. 파열(네이티브 OOB·핀드 페이지 오염)이면
        /// 코히런스 퓨즈를 fail-closed 로 1회 래치(옵션 ForceFreeze)하고 false 반환. 정상이면 true.
        /// </summary>
        public bool AttestArena()
        {
            Interlocked.Increment(ref _attests);
            long* a = _arena;
            if (a == null || Volatile.Read(ref _fuseTripped) == 1) return false;
            for (int i = 0; i < _slots; i++)
                if (a[i * SlotLongs + C_CANARY] != CanaryValue) { TripFuse($"아레나 카나리 파열 slot={i}"); return false; }
            return true;
        }

        /// <summary>
        /// ★정적 격리 증명(δ=0): 두 unmanaged 필드가 서로 다른 64B 캐시라인을 점유하는지(False Sharing 불가) 반환한다.
        /// ★단순 시작오프셋 비교로는 부족 — 앞선(주소 낮은) 필드가 크면 뒤 필드의 라인으로 '흘러넘쳐(spill)' 라인을 공유할
        /// 수 있다(예 72B 필드@0 + 필드@72: 시작이격 72≥64 이나 앞 필드가 라인0·1 을 걸쳐 라인1 을 공유). 따라서 앞 필드의
        /// '크기'를 반영해 캐시라인 인덱스로 판정한다: 뒤 필드 시작 라인 &gt; 앞 필드 끝 라인 ⇔ 공유 라인 없음.
        /// ★전제: 앞선 필드가 64B 라인 경계에서 시작(이 모듈의 아레나 슬롯은 AlignedAlloc 로 라인 정렬 — 전제 성립).
        /// 오프셋·크기만 사용해 결정론적(런타임 주소 비의존).
        /// </summary>
        public static bool AttestIsolated<T1, T2>(ref T1 a, ref T2 b) where T1 : unmanaged where T2 : unmanaged
        {
            nint delta = Unsafe.ByteOffset(ref Unsafe.As<T1, byte>(ref a), ref Unsafe.As<T2, byte>(ref b));
            long gap       = delta < 0 ? -(long)delta : (long)delta;                       // 두 필드 시작점 절대 이격
            long firstSize = delta >= 0 ? Unsafe.SizeOf<T1>() : Unsafe.SizeOf<T2>();        // 앞선(주소 낮은) 필드 크기
            long firstEndLine    = (firstSize - 1) / CacheLine;   // 앞 필드가 점유하는 마지막 라인(라인정렬 시작 기준)
            long secondStartLine = gap / CacheLine;               // 뒤 필드가 시작하는 라인
            return secondStartLine > firstEndLine;                // 엄격히 높은 라인 ⇒ 공유 라인 부재(격리)
        }

        private void TripFuse(string reason)
        {
            if (Interlocked.CompareExchange(ref _fuseTripped, 1, 0) != 0) return;   // 1회만
            try { _attestation?.ForceFreeze($"하드웨어 코히런스 파열 — {reason}. 무장 안전 홀드."); } catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            long* a = _arena; _arena = null;
            if (a != null) NativeMemory.AlignedFree(a);
        }
    }
}
