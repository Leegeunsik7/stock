// ============================================================================
// PureShellFitnessSentinel — v11.4.0 Think Beyond Gemini
//   ★"Host 어셈블리 내 신규 UI 콘크리트 오염"을 영구 격리한다. Host 프로젝트를 리플렉션 스캔하여 '호스트 결합 UI
//     컴포넌트'([Inject]/[CascadingParameter] 를 선언한 ComponentBase — 즉 PortBlocked)의 수를 집계하고, 단조 감소
//     래칫(RatchetCeiling) 을 넘어 '새' 결합 UI 가 유입되면 위반으로 판정 → CI/부팅 게이트가 거부한다.
//
//   ── 제미나이 문서/이관을 '초월'하는 지점 ───────────────────────────────────
//     Pure Host Shell 은 한 번에 달성되지 않는다(본 코드베이스 잔여 ~86 PortBlocked). 순진한 '전량 이동'은 회귀
//     위험만 키운다. 본 센티널은 목표를 '측정 가능한 단조 래칫'으로 바꾼다 — 이주가 진행되면 상한을 낮추고, 어떤
//     PR 도 호스트 결합 UI 를 '늘리지' 못하게 강제한다. 즉 Pure Host Shell 로의 수렴을 물리적으로 보증한다.
//
//   불변: 결정론(리플렉션 순수 판독·시계/난수 0) · fail-safe(타입 로드 실패 → 로드 가능분만 집계·throw 없음) ·
//        liveArmed:false(순수 정적 분석 — 발주/소켓 유발 0). ※핫패스 아님(부팅/CI 콜드 스캔).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace MS2E.Infrastructure
{
    /// <summary>Pure Host Shell 적합성 리포트.</summary>
    public readonly record struct ShellFitnessReport(
        int TotalComponents, int HostCoupledUi, int ShellComponents, int Ratchet, bool WithinRatchet);

    public static class PureShellFitnessSentinel
    {
        /// <summary>
        /// ★단조 감소 래칫 — 호스트에 잔류 허용되는 결합 UI 컴포넌트 최대 수. 이주가 진행되면 이 값을 낮춘다.
        /// 신규 결합 UI 유입 → 초과 → CI/부팅 거부(Pure Shell 회귀 방지). (v11.4.0 실측 기준 봉인.)
        /// </summary>
        public const int RatchetCeiling = 63;   // v11.5.0 Phase 94-M 하향(64→63): Dashboard(Activity/Layout/Paper 심층결합) RCL 이주. 이주 시마다 단조 감축.

        /// <summary>호스트 어셈블리를 스캔해 결합 UI 수를 집계·래칫 판정.</summary>
        public static ShellFitnessReport Scan(Assembly hostAssembly)
        {
            int total = 0, coupled = 0;
            foreach (var t in SafeTypes(hostAssembly))
            {
                if (t is null || t.IsAbstract) continue;
                if (!typeof(ComponentBase).IsAssignableFrom(t)) continue;
                total++;
                if (IsHostCoupledUi(t)) coupled++;
            }
            return new ShellFitnessReport(total, coupled, total - coupled, RatchetCeiling, coupled <= RatchetCeiling);
        }

        /// <summary>컴포넌트가 [Inject] 또는 [CascadingParameter] 속성을 선언하면 '호스트 결합 UI'(PortBlocked).</summary>
        public static bool IsHostCoupledUi(Type t)
        {
            var props = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var p in props)
            {
                foreach (var a in p.GetCustomAttributesData())
                {
                    var n = a.AttributeType.Name;
                    if (n == "InjectAttribute" || n == "CascadingParameterAttribute") return true;
                }
            }
            return false;
        }

        // 타입 로드 실패(부분 어셈블리) 시에도 로드 가능분만 안전 집계.
        private static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(x => x is not null)!; }
        }
    }
}
