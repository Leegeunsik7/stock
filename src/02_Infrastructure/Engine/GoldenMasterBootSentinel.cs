// ============================================================================
// GoldenMasterBootSentinel — v12.0.0 Phase 94-P Part 3 Think Beyond Gemini (Engine B)
//
//   위험 가정(제미나이의 '단순 문서 작성'이 다루지 않는 골든마스터 부팅 파멸 리스크):
//     "미검증 환경에서의 우발적 실매매 무장" — 골든마스터가 (a) RCL 이 엔진을 역참조하거나(순수성 붕괴),
//     (b) 부팅 시 카나리가 이미 무장됐거나(liveArmed:true), (c) 물리 이중 키가 이미 접점 연결됐거나,
//     (d) DPAPI 금고가 미봉인(평문 시크릿)인 상태로 기동하면 — 단 1비트의 무결성 결함이 실 발주 사고로 번진다.
//
//   ── Think Beyond (Engine B) ───────────────────────────────────────────────────
//     Golden Master Boot-Time Integrity Sentinel — 부팅 시 리플렉션 정적 산술 스캔으로 5대 무결성 축을 검사하고,
//     단 1개라도 결함이면 fail-closed(AllPassed=false)로 판정한다. 판정 커널 Evaluate 는 unmanaged 체크 스팬을
//     stackalloc 으로 순회 → δ=0(힙 0)·결정론. Scan 은 콜드 부팅(리플렉션+프로브)이며 상세 사유를 첨부.
//       ① RclZeroEngineRef      — MS2E.UI.Portable 이 호스트/엔진 어셈블리를 역참조하지 않음(0 Concrete Ref 물증).
//       ② HostCoupledWithinRatchet — 호스트 결합 UI ≤ PureShell 래칫(Pure Host Shell 수렴).
//       ③ LiveArmedFalse        — 부팅 시 카나리 미무장(liveArmed:false).
//       ④ DualKeyAbsent         — 물리 이중 키(ValveWired ∧ LiveArmingWired) 미연결(안전 기본).
//       ⑤ VaultSealed           — API 금고 DPAPI 봉인(평문 시크릿 0).
//
//   불변: 결정론(리플렉션 순수 판독·시계/난수 0) · δ=0(Evaluate 커널 힙 0) · fail-closed(빈 체크/1결함 → 거부) ·
//        liveArmed:false(순수 정적 분석 — 발주/소켓 유발 0). ※Scan 은 부팅/CI 콜드 패스(핫패스 아님).
// ============================================================================
using System;
using System.Linq;
using System.Reflection;

namespace MS2E.Infrastructure
{
    public enum GmCheckId : byte
    {
        RclZeroEngineRef = 0,
        HostCoupledWithinRatchet = 1,
        LiveArmedFalse = 2,
        DualKeyAbsent = 3,
        VaultSealed = 4,
    }

    /// <summary>무결성 단일 체크(unmanaged — enum+bool → δ=0 stackalloc 가능).</summary>
    public readonly record struct GoldenMasterCheck(GmCheckId Id, bool Passed);

    /// <summary>골든마스터 부팅 무결성 판정(unmanaged — 값 반환·힙 0).</summary>
    public readonly record struct GoldenMasterReport(int Total, int Passed, bool AllPassed);

    /// <summary>부팅 시점 실환경 상태 프로브(테스트/부팅에서 주입 — 결정론).</summary>
    public interface IGoldenMasterProbe
    {
        bool LiveArmedFalse { get; }   // 카나리 미무장(부팅 안전)
        bool DualKeyAbsent { get; }    // 물리 이중 키 미연결(부팅 안전)
        bool VaultSealed { get; }      // DPAPI 금고 봉인(평문 시크릿 0)
    }

    public static class GoldenMasterBootSentinel
    {
        /// <summary>
        /// ★fail-closed 판정 커널(δ=0). 모든 체크가 Passed 이고 체크가 1개 이상일 때만 AllPassed=true.
        /// 빈 스팬(체크 0)·단 1개 실패 → AllPassed=false(우발적 무장 거부). unmanaged 스팬 순회 → 힙 0.
        /// </summary>
        public static GoldenMasterReport Evaluate(ReadOnlySpan<GoldenMasterCheck> checks)
        {
            int total = checks.Length, passed = 0;
            for (int i = 0; i < checks.Length; i++)
                if (checks[i].Passed) passed++;
            bool all = total > 0 && passed == total;   // fail-closed: 빈/부분 → 거부
            return new GoldenMasterReport(total, passed, all);
        }

        /// <summary>
        /// 콜드 부팅 스캔 — 리플렉션(RCL 0 엔진참조·호스트 래칫) + 프로브(무장/이중키/금고)로 5대 축 검사 후 fail-closed 판정.
        /// checks 배열(상세)을 out 으로 돌려 부팅 로그/런북 진단에 사용.
        /// </summary>
        public static GoldenMasterReport Scan(Assembly hostAssembly, Assembly rclAssembly, IGoldenMasterProbe probe,
                                              out GoldenMasterCheck[] checks)
        {
            bool rclClean = RclReferencesNoEngine(rclAssembly, hostAssembly);
            bool withinRatchet = SafeWithinRatchet(hostAssembly);
            checks = new[]
            {
                new GoldenMasterCheck(GmCheckId.RclZeroEngineRef, rclClean),
                new GoldenMasterCheck(GmCheckId.HostCoupledWithinRatchet, withinRatchet),
                new GoldenMasterCheck(GmCheckId.LiveArmedFalse, probe?.LiveArmedFalse ?? false),
                new GoldenMasterCheck(GmCheckId.DualKeyAbsent, probe?.DualKeyAbsent ?? false),
                new GoldenMasterCheck(GmCheckId.VaultSealed, probe?.VaultSealed ?? false),
            };
            return Evaluate(checks);
        }

        /// <summary>RCL 어셈블리가 호스트/엔진 어셈블리를 역참조하지 않는지(0 Concrete Ref 물증).</summary>
        public static bool RclReferencesNoEngine(Assembly rclAssembly, Assembly hostAssembly)
        {
            try
            {
                var hostName = hostAssembly.GetName().Name ?? "";
                foreach (var r in rclAssembly.GetReferencedAssemblies())
                {
                    var n = r.Name ?? "";
                    if (string.Equals(n, hostName, StringComparison.OrdinalIgnoreCase)) return false;
                    if (n.StartsWith("MS2E.StockSuite", StringComparison.OrdinalIgnoreCase)) return false;
                    if (n.StartsWith("StockManager", StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            }
            catch { return false; }   // 판독 불가 → fail-closed
        }

        private static bool SafeWithinRatchet(Assembly hostAssembly)
        {
            try { return PureShellFitnessSentinel.Scan(hostAssembly).WithinRatchet; }
            catch { return false; }   // 스캔 실패 → fail-closed
        }
    }
}
