// ============================================================================
// NightlyForensicWatchdog — v12.0.2 CI/CD 야간 포렌식 회귀 워치독
//   ★CI/CD 파이프라인에서 주기 구동되어 시스템 전역 회귀를 무타격 정적 스캔한다. 단 1축이라도 위반 시
//     fail-closed(AllClean=false) 로 판정하고 포렌식 위반 로그를 생성 → 빌드 중단(테스트 Assert 가 게이트).
//
//   4대 축(무타격 정적 스캔):
//     ① RclZeroEngineRef       — MS2E.UI.Portable 이 호스트/엔진 어셈블리 역참조 0(0 Concrete Ref).
//     ② HostCoupledWithinRatchet — 호스트 결합 UI ≤ 래칫(coupled ≤ RatchetCeiling).
//     ③ RatchetCeilingHeld     — ★래칫 천장 자체가 감축선(≤40) 위로 오르지 않음(메타 — 누군가 상한을 올리는 회귀 차단).
//     ④ Delta0HotPath          — Release 빌드 핫패스 힙 할당 0(호출측이 ZeroAllocMemoryProfiler 로 측정해 주입).
//
//   불변: 결정론(리플렉션 순수 판독·시계는 로그 타임스탬프에만) · fail-safe(스캔 실패 → 해당 축 위반 처리) ·
//        liveArmed:false(순수 정적 분석 — 발주/소켓 유발 0). ※콜드 CI 패스(핫패스 아님 — 리포트 문자열은 힙 사용).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace MS2E.Infrastructure
{
    public enum ForensicAxis : byte
    {
        RclZeroEngineRef        = 0,
        HostCoupledWithinRatchet = 1,
        RatchetCeilingHeld      = 2,
        Delta0HotPath           = 3,
    }

    public readonly record struct ForensicFinding(ForensicAxis Axis, bool Passed, long Value, string Detail);

    public sealed class ForensicReport
    {
        public IReadOnlyList<ForensicFinding> Findings { get; init; } = Array.Empty<ForensicFinding>();
        public bool AllClean { get; init; }
        public string ViolationLog { get; init; } = "";
    }

    public static class NightlyForensicWatchdog
    {
        /// <summary>★래칫 천장 감축선 — RatchetCeiling 이 이 값을 넘으면 위반(상한 상향 회귀 차단).</summary>
        public const int MaxRatchetCeiling = 40;

        /// <summary>
        /// 4대 축 정적 스캔 + fail-closed 판정. delta0LeakBytes 는 호출측이 대표 핫패스를 ZeroAllocMemoryProfiler 로
        /// 측정해 넘긴 값(0 이어야 통과). 위반 시 ViolationLog 에 포렌식 로그를 채운다.
        /// </summary>
        public static ForensicReport Scan(Assembly hostAssembly, Assembly rclAssembly, long delta0LeakBytes)
        {
            var findings = new List<ForensicFinding>(4);

            // ① RCL 0 엔진참조(순수성) — 판독 실패 시 fail-closed(위반).
            bool purity;
            try { purity = GoldenMasterBootSentinel.RclReferencesNoEngine(rclAssembly, hostAssembly); }
            catch { purity = false; }
            findings.Add(new(ForensicAxis.RclZeroEngineRef, purity, 0,
                purity ? "RCL 엔진참조 0(0 Concrete Ref)" : "★RCL 이 호스트/엔진 어셈블리를 역참조(순수성 붕괴)"));

            // ② coupled ≤ 래칫 — 스캔 실패 시 fail-closed.
            int coupled = -1, ceiling = -1; bool within;
            try { var s = PureShellFitnessSentinel.Scan(hostAssembly); coupled = s.HostCoupledUi; ceiling = s.Ratchet; within = s.WithinRatchet; }
            catch { within = false; }
            findings.Add(new(ForensicAxis.HostCoupledWithinRatchet, within, coupled,
                within ? $"coupled={coupled} ≤ ceiling={ceiling}" : $"★결합 UI {coupled} > 래칫 {ceiling}(신규 오염)"));

            // ③ 래칫 천장 감축선(메타) — 상한 상향 회귀 차단.
            bool ceilingHeld = PureShellFitnessSentinel.RatchetCeiling <= MaxRatchetCeiling;
            findings.Add(new(ForensicAxis.RatchetCeilingHeld, ceilingHeld, PureShellFitnessSentinel.RatchetCeiling,
                ceilingHeld ? $"RatchetCeiling={PureShellFitnessSentinel.RatchetCeiling} ≤ {MaxRatchetCeiling}"
                            : $"★RatchetCeiling={PureShellFitnessSentinel.RatchetCeiling} > {MaxRatchetCeiling}(감축선 상향 회귀)"));

            // ④ Release 핫패스 δ=0(호출측 측정값 주입).
            bool d0 = delta0LeakBytes == 0;
            findings.Add(new(ForensicAxis.Delta0HotPath, d0, delta0LeakBytes,
                d0 ? "핫패스 힙 할당 0(δ=0)" : $"★핫패스 메모리 누수 {delta0LeakBytes}B(δ=0 붕괴)"));

            bool allClean = findings.All(f => f.Passed);
            return new ForensicReport
            {
                Findings = findings,
                AllClean = allClean,
                ViolationLog = allClean ? "" : BuildViolationLog(findings),
            };
        }

        private static string BuildViolationLog(List<ForensicFinding> findings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════════════════════════════");
            sb.AppendLine(" [FORENSIC WATCHDOG] ★fail-closed 위반 감지 — 빌드 파이프라인 중단 ★");
            sb.Append(" 시각(UTC): ").AppendLine(DateTime.UtcNow.ToString("o"));
            sb.AppendLine("───────────────────────────────────────────────────────────────────");
            foreach (var f in findings)
                sb.Append("  ").Append(f.Passed ? "✓ " : "✗ ").Append(f.Axis).Append(": ").AppendLine(f.Detail);
            sb.AppendLine("═══════════════════════════════════════════════════════════════════");
            return sb.ToString();
        }
    }
}
