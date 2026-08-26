// ============================================================================
// PortfolioSnapshotIntegritySentinel — Phase 94-B: 캐스케이드 상태 오염 무결성 감시자 (Think Beyond Gemini)
//
//   ★제미나이 A(readonly ref struct 불변 스냅샷 캐스케이드)는 Blazor 에서 원천 불가능하다 — ref struct 는
//   [CascadingParameter]/컴포넌트 파라미터/제네릭/async 경계를 넘지 못한다(CS 제약). 제미나이 B(해시 감시자)를
//   취하되 '초월'한다: (1)IPortfolioSnapshot 자체를 setter-없는 읽기전용 계약으로 만들어 정상 UI 는 컴파일 타임에
//   변조 불가(1차 방어), (2)그럼에도 배열 원소를 캐스트-백/reflection 으로 변조하는 파멸 경로를 δ=0·결정론 해시로
//   렌더 전후 O(내용) 비교해 즉시 탐지·fail-closed(2차 방어). 두 방어를 겹쳐 '퀀트 엔진 상태 오염'을 영구 격리.
//
//   왜 이게 파멸적인가: 하위 UI(Risk/MC)가 캐스케이딩 배열(R·Weights)을 렌더 중 실수로 변형하면, 같은 참조를
//   공유하는 상위 허브·다른 탭·(장차) 엔진 계산이 오염된 데이터를 읽는다. 감시자는 '읽기 전용 계약 위반'을 물증(해시
//   불일치)으로 잡아 렌더 트리를 fail-closed 하고 오염 전파를 차단한다.
//
//   ★불변: δ=0(ComputeHash/Verify 핫패스 힙 0 — FNV·BitConverter.DoubleToInt64Bits·문자/원소 순회, string/배열
//     미생성) · 결정론(순수 내용 해시·시계/난수 0) · fail-closed(불일치 → Verify=false → 호출부가 렌더 파괴·
//     암흑 자색 오류 투사) · liveArmed:false(순수 관측 — 발주/소켓 유발 0).
// ============================================================================
using System;
using MS2E.UI.Portable.Ports;

namespace MS2E.UI.Portable.Infrastructure
{
    public static class PortfolioSnapshotIntegritySentinel
    {
        private const ulong FnvOffset = 1469598103934665603UL;
        private const ulong FnvPrime  = 1099511628211UL;

        /// <summary>
        /// 스냅샷 내용의 δ=0 결정론 해시(FNV-1a-64). 렌더 전 캡처 → 렌더 후 재계산 비교로 변조 탐지.
        /// null 필드는 fail-safe 로 고정 토큰 혼합(NRE 대신 결정론 유지).
        /// </summary>
        public static long ComputeHash(IPortfolioSnapshot s)
        {
            if (s is null) return 0L;
            ulong h = FnvOffset;
            Mix(ref h, (ulong)(s.Loaded ? 1 : 0));
            Mix(ref h, (ulong)s.HoldingCount);

            string err = s.Error;
            if (err is null) Mix(ref h, 0xE0);
            else { Mix(ref h, 0xE1); for (int i = 0; i < err.Length; i++) Mix(ref h, err[i]); }

            string[] names = s.Names;
            if (names is null) Mix(ref h, 0xA0);
            else { Mix(ref h, (ulong)names.Length); for (int i = 0; i < names.Length; i++) { var n = names[i]; if (n is null) { Mix(ref h, 0); continue; } for (int j = 0; j < n.Length; j++) Mix(ref h, n[j]); Mix(ref h, 0x1F); } }

            double[] w = s.Weights;
            if (w is null) Mix(ref h, 0xB0);
            else { Mix(ref h, (ulong)w.Length); for (int i = 0; i < w.Length; i++) Mix(ref h, (ulong)BitConverter.DoubleToInt64Bits(w[i])); }

            bool[] usd = s.UsdMask;
            if (usd is null) Mix(ref h, 0xC0);
            else { Mix(ref h, (ulong)usd.Length); for (int i = 0; i < usd.Length; i++) Mix(ref h, (ulong)(usd[i] ? 1 : 0)); }

            double[][] r = s.R;
            if (r is null) Mix(ref h, 0xD0);
            else
            {
                Mix(ref h, (ulong)r.Length);
                for (int i = 0; i < r.Length; i++)
                {
                    var row = r[i];
                    if (row is null) { Mix(ref h, 0); continue; }
                    Mix(ref h, (ulong)row.Length);
                    for (int j = 0; j < row.Length; j++) Mix(ref h, (ulong)BitConverter.DoubleToInt64Bits(row[j]));
                }
            }
            return (long)h;
        }

        /// <summary>렌더 전 캡처 해시와 현재 해시 비교(δ=0). false=변조 감지 → 호출부 fail-closed.</summary>
        public static bool Verify(IPortfolioSnapshot s, long expectedHash) => ComputeHash(s) == expectedHash;

        private static void Mix(ref ulong h, ulong v) { unchecked { h ^= v; h *= FnvPrime; } }
    }
}
