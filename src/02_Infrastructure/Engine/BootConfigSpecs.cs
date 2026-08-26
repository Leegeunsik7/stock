// ============================================================================
// BootConfigSpecs — RuntimeConfig 검증 경로로 라우팅되는 '무위험' 부팅·인프라 설정의 단일 진실 공급원.
//
//  여기 선언된 키·폴백·바운드를 호스트의 실효 GetInt 호출과 ConfigAuditor 감사가
//  '동일하게' 참조한다 → 감사 스냅샷과 런타임 실효값이 절대 어긋나지 않는다.
//
//  ⚠️ 안전 경계: 이 목록에는 주문 집행·점화(liveArmed)와 무관한 무위험 튜너블만 둔다.
//     거래 방아쇠는 코드 게이트 전용이며 절대 이 경로로 노출하지 않는다.
//     (RuntimeConfig.LiveTradingConfigurable == false 와 같은 계약)
// ============================================================================
using System.Collections.Generic;

namespace MS2E.Infrastructure
{
    public static class BootConfigSpecs
    {
        // 상태 스냅샷 주기(초) — MMF 게이팅 모드 영속화 타이머.
        public const string SnapshotIntervalSecKey     = "Boot.SnapshotIntervalSec";
        public const int    SnapshotIntervalSecDefault  = 60;
        public const int    SnapshotIntervalSecMin      = 5;
        public const int    SnapshotIntervalSecMax      = 3600;

        // 스냅샷 신선도 한도(시간) — 초과 시 Stale 라벨(진단 전용, 게이트 무영향).
        public const string FreshnessWindowHoursKey     = "Boot.FreshnessWindowHours";
        public const int    FreshnessWindowHoursDefault  = 12;
        public const int    FreshnessWindowHoursMin      = 1;
        public const int    FreshnessWindowHoursMax      = 168;   // 7일

        // 온실 배양 사이클 휴면(ms) — GreenhouseService.CycleIntervalMs (가동 시 사용).
        public const string GreenhouseCycleMsKey        = "Greenhouse.CycleIntervalMs";
        public const int    GreenhouseCycleMsDefault     = 1000;
        public const int    GreenhouseCycleMsMin         = 100;
        public const int    GreenhouseCycleMsMax         = 60000;

        // 읽기전용 LAN 상태 서버 포트 — LanStatusServer (트레이/헤드리스 호스트, 옵트인).
        public const string LanPortKey                  = "Lan.Port";
        public const int    LanPortDefault               = 8787;
        public const int    LanPortMin                   = 1;
        public const int    LanPortMax                   = 65535;

        // 온실 자율 배양 부팅 자동 점등 — false 로 비활성(운영자 제어). 거래 불가(제안 전용).
        public const string GreenhouseAutoStartKey      = "Greenhouse.AutoStart";
        public const bool   GreenhouseAutoStartDefault   = true;

        // S24-b 크래시 복구 저널 플러시 펌프 — 기본 OFF(옵트인). OFF면 저널 미기록 → 부팅 항상 Empty(후방호환).
        public const string RecoveryPumpEnabledKey      = "Recovery.PumpEnabled";
        public const bool   RecoveryPumpEnabledDefault   = false;

        // S25-a 라이브 캔들 인그레스 — 기본 OFF(옵트인). OFF면 합성 BootstrapCandles 유지(후방호환).
        // ON 시 BulkheadCandleSource→LiveCandleFeed→LiveIngressDriver 가 실 시장 캔들로 온실을 구동(실거래선 무관).
        public const string GreenhouseLiveFeedKey       = "Greenhouse.LiveFeed";
        public const bool   GreenhouseLiveFeedDefault    = false;

        /// <summary>감사 대상 = 라우팅 대상. 항상 1:1로 일치한다.</summary>
        public static IReadOnlyList<ConfigSpec> All { get; } = new[]
        {
            new ConfigSpec(SnapshotIntervalSecKey, ConfigKind.Int,
                SnapshotIntervalSecDefault.ToString(),
                SnapshotIntervalSecMin, SnapshotIntervalSecMax, "스냅샷 주기(초)"),

            new ConfigSpec(FreshnessWindowHoursKey, ConfigKind.Int,
                FreshnessWindowHoursDefault.ToString(),
                FreshnessWindowHoursMin, FreshnessWindowHoursMax, "신선도 한도(h)"),

            new ConfigSpec(GreenhouseCycleMsKey, ConfigKind.Int,
                GreenhouseCycleMsDefault.ToString(),
                GreenhouseCycleMsMin, GreenhouseCycleMsMax, "온실 사이클(ms)"),

            new ConfigSpec(LanPortKey, ConfigKind.Int,
                LanPortDefault.ToString(),
                LanPortMin, LanPortMax, "LAN 포트"),

            new ConfigSpec(GreenhouseAutoStartKey, ConfigKind.Bool,
                GreenhouseAutoStartDefault ? "true" : "false",
                Label: "온실 자동점등"),

            new ConfigSpec(RecoveryPumpEnabledKey, ConfigKind.Bool,
                RecoveryPumpEnabledDefault ? "true" : "false",
                Label: "복구 플러시 펌프"),

            new ConfigSpec(GreenhouseLiveFeedKey, ConfigKind.Bool,
                GreenhouseLiveFeedDefault ? "true" : "false",
                Label: "라이브 캔들 인그레스"),
        };
    }
}
