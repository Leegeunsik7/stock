// ============================================================================
// Crc32 — IEEE 802.3 순수 코어 (외부 의존 0 · 무할당 · 결정론)
//
//  저널·MMF 무결성 검증용. ReadOnlySpan<byte> 위에서 테이블 기반으로 계산하므로 할당 0.
//  표준 체크값: Crc32.Compute("123456789") == 0xCBF43926.
//
//  ── static 레이스 안전 (Mission 3) ─────────────────────────────────────────
//  _table 은 static readonly 불변 데이터 — CLR 타입 초기화 락이 단 1회·스레드 안전하게
//  채운다(순수 산술이라 TypeInitializationException 유발 부작용 없음). 가변 static·부작용
//  static ctor 를 피하는 것이 레이스/예외 방어의 핵심 규율.
// ============================================================================
using System;

namespace StockManager.Common
{
    public static class Crc32
    {
        private const uint Poly = 0xEDB88320u;            // IEEE 802.3 reversed
        private static readonly uint[] _table = BuildTable();

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1u) != 0u ? Poly ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        /// <summary>IEEE 802.3 CRC32. 무할당(span 순회).</summary>
        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            for (int i = 0; i < data.Length; i++)
                crc = _table[(crc ^ data[i]) & 0xFFu] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
