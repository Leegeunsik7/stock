// ============================================================================
// ColdBootPohRingIntegritySentinel — v12.14.0-GM Think Beyond Gemini(방향 A 변형, 독자 설계):
//   Zero-Alloc Cold-Boot Hardware/Memory Handshake Sentinel
//
//   위험 가정(기존 GoldenMasterBootSentinel 이 다루지 않는 축): 그 센티널은 '리플렉션 정적 판정'(RCL 참조·
//   래칫·liveArmed·이중키·금고 봉인)만 본다 — 정작 NativeBacktestRingCache/OrderBookDepthMatrixCoalescer 등이
//   부팅 시 Pinned Object Heap 에 이미 할당해 둔 원시 바이트 영역 자체가 위·변조(외부 프로세스 메모리 라이트,
//   손상된 페이지 등)됐는지는 아무도 검사하지 않는다. 첫 소비 이전(콜드 부팅 찰나)에 단 한 번, 그 스냅샷과
//   물리 이중 키 상태를 힙 할당 없이 함께 판정한다.
//
//   기법: FNV-1a 폴드 체크섬을 ReadOnlySpan<byte> 위에서 직접 순회(박싱·LINQ·문자열보간 0 — 핫패스 δ=0 규약
//   준수). 이중 키 상태는 2 bool 을 stackalloc<bool> 스팬에 실어 GoldenMasterBootSentinel.Evaluate 와 동일한
//   "unmanaged 스팬 순회 판정" 관용구를 재사용(신규 판정 커널이 기존 관용구와 시각적으로 정합 — 유지보수 시
//   두 센티널을 같은 패턴으로 읽을 수 있음).
//
//   불변: 결정론(체크섬은 순수 바이트 함수·시계/난수 0) · δ=0(Checksum/Evaluate 전부 힙 0 — Span 은 호출측이
//        이미 보유한 메모리를 가리킬 뿐 복사 0) · fail-closed(체크섬 불일치 OR 부팅 찰나에 이중 키가 이미
//        결선돼 있으면(물리 불가능한 정상 상태) 거부) · liveArmed:false(발주/소켓 참조 0 — 순수 판독 전용).
// ============================================================================
using System;

namespace MS2E.Infrastructure
{
    /// <summary>콜드 부팅 POH 링 무결성 + 이중 키 상태 결합 판정(unmanaged — 값 반환·힙 0).</summary>
    public readonly record struct ColdBootVerdict(bool ChecksumMatch, bool DualKeyPhysicallyAbsent, bool Passed);

    public static class ColdBootPohRingIntegritySentinel
    {
        /// <summary>FNV-1a 64비트 폴드 체크섬(δ=0 — Span 순회만, 할당 0). POH/Unmanaged 링 원시 바이트 스냅샷용.</summary>
        public static ulong ComputeChecksum(ReadOnlySpan<byte> region)
        {
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offsetBasis;
            for (int i = 0; i < region.Length; i++)
            {
                hash ^= region[i];
                hash *= prime;
            }
            return hash;
        }

        /// <summary>
        /// ★fail-closed 판정 커널(δ=0). 체크섬 불일치, 또는 부팅 찰나에 물리 이중 키(ValveWired∧LiveArmingWired)가
        /// 이미 결선돼 있으면(콜드 부팅 시점엔 있을 수 없는 상태 — 결선은 운용자가 부팅 '후' 수행) 거부.
        /// </summary>
        public static ColdBootVerdict Verify(ReadOnlySpan<byte> region, ulong expectedChecksum,
                                              bool valveWired, bool liveArmingWired)
        {
            bool checksumMatch = ComputeChecksum(region) == expectedChecksum;
            bool dualKeyAbsent = !(valveWired && liveArmingWired);

            Span<bool> axes = stackalloc bool[2] { checksumMatch, dualKeyAbsent };
            bool all = true;
            for (int i = 0; i < axes.Length; i++)
                if (!axes[i]) { all = false; break; }

            return new ColdBootVerdict(checksumMatch, dualKeyAbsent, all);
        }
    }
}
