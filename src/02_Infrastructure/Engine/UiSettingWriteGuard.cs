// ============================================================================
// UiSettingWriteGuard — [자율 UI 검증 · 추천 A] 순수 UI 발 설정 기입의 무장 키 봉쇄(허용 목록 · fail-closed)
//
//  결함(자율 UI 퍼저 포트 표면 감사로 발견): IAdminSettingWidgetPort.SetSetting / IStrategyHubInfraPort.SetSetting 은 임의 키를
//  LocalDatabase 설정 테이블에 그대로 기입했다. 호스트 RuntimeConfig 의 precedence 는 '환경변수 → DB 설정' 이므로
//  Dispatch.ValveWired · Dispatch.LiveArmingWired(물리 이중 키) · Broker.LiveArmed(LiveArmingGate ③항) · Canary.CapBps(카나리 한도)
//  · Risk.* · Security.* 등 안전 설정이 '순수 UI 의 문자열 한 줄' 로 덮어써질 수 있었다(일부는 람다로 지연 판독 = 재시작 없이 반영).
//  RCL 이 실제로 그런 키를 쓰지는 않지만 포트가 그것을 허용하는 것 자체가 Pure Shell 경계의 구멍이다.
//
//  교정: 허용 목록(allowlist) — RCL 이 실제로 기입하는 설정 네임스페이스만 통과, 그 외(미분류 포함)는 전부 폐기 + 거부 계수.
//   · 거부 목록이 아닌 허용 목록인 이유: 신규 안전 설정 네임스페이스가 추가돼도 자동으로 차단(미분류 = fail-closed 원칙).
//   · 폐기(예외 아님): UI 렌더 경로로 예외를 던지면 페이지 장애가 되므로 조용히 버리고 Rejected 로 관측(큐잉·재시도 없음).
//   · 허용 접두사는 RCL 소스의 SetSetting/SetSecret 리터럴 키 전수(Settings/Optimizer 페이지)와 1:1 — 새 UI 설정은 여기 등재 필수.
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>순수 UI 포트 경유 설정 기입 허용 판정(허용 목록 · 미분류 = 거부).</summary>
    public static class UiSettingWriteGuard
    {
        // RCL 이 기입하는 네임스페이스 전수(SettingsPage: Lan/Llm/Ollama/Regime/Drift/Nightly/Email · OptimizerPage: Optimizer).
        private static readonly string[] AllowedPrefixes =
        {
            "Lan.", "Llm.", "Ollama.", "Regime.", "Drift.", "Nightly.", "Email.", "Optimizer.",
        };

        private static long _rejected;

        /// <summary>프로세스 누적 거부 건수(관측 전용).</summary>
        public static long Rejected => Interlocked.Read(ref _rejected);

        /// <summary>키가 UI 기입 허용 네임스페이스에 속하는가(접두사 뒤 이름 1자 이상 · 서수 비교).</summary>
        public static bool IsUiWritable(string? key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            foreach (var p in AllowedPrefixes)
                if (key.Length > p.Length && key.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>기입 인가. 거부 시 계수 후 false(호출자는 기입을 폐기).</summary>
        public static bool Admit(string? key)
        {
            if (IsUiWritable(key)) return true;
            Interlocked.Increment(ref _rejected);
            return false;
        }
    }
}
