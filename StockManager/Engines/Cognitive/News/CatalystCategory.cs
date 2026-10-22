// ============================================================================
// CatalystCategory — v12.18.63-DEV: 플루토 브레인 1단계 — 뉴스/공시 촉매 분류 · 점수 밴드 · 반감기(값 타입 · 할당 0)
//
//   점수 밴드(−100 ~ +100): 구조적 호재 +60~+100(수주 · 인허가 · 특허 · 사상 최대 실적) · 단기 모멘텀 +20~+59(MOU · 목표가 상향 · 테마 수급) ·
//     노이즈 0(루머 · 낚시성 · 단순 시황 — 즉시 기각) · 단기 악재 −20~−59(구설 · 결함 · 목표가 하향) ·
//     치명적 구조적 악재 −60~−100(횡령 · 배임 · 유상증자 · CB · 분식회계 · 감사의견 거절).
//   반감기(분): 단기 모멘텀(±) 10 · 구조적 호재 30 · 구조적 악재 60(악재는 더 오래 남긴다 — 위험 비대칭 · fail-closed 측).
//   ★liveArmed:false — 분류는 가상 대결 시그널 입력일 뿐 주문 수단이 아니다.
// ============================================================================
using System;

namespace StockManager.Engines.Cognitive.News
{
    /// <summary>촉매 유형. Noise = 기각(점수 0 · 버퍼 미기록).</summary>
    public enum CatalystCategory : byte
    {
        Noise = 0,
        StructuralPositive = 1,
        MomentumPositive = 2,
        MomentumNegative = 3,
        StructuralNegative = 4,
    }

    /// <summary>뉴스/공시 출처. 공시(DART · SEC) = 1차 출처(루머/낚시 필터 면제 · 확신도 최고).</summary>
    public enum PlutoSource : byte
    {
        Unknown = 0,
        Dart = 1,
        Sec = 2,
        Wire = 3,
    }

    public static class CatalystText
    {
        public const int MaxScore = 100;
        public const int StructuralMin = 60, MomentumMin = 20;

        public static string Label(CatalystCategory c) => c switch
        {
            CatalystCategory.StructuralPositive => "구조적 호재",
            CatalystCategory.MomentumPositive => "단기 모멘텀 호재",
            CatalystCategory.MomentumNegative => "단기 악재",
            CatalystCategory.StructuralNegative => "치명적 구조적 악재",
            _ => "노이즈(기각)",
        };

        public static string SourceLabel(PlutoSource s) => s switch
        {
            PlutoSource.Dart => "DART 공시",
            PlutoSource.Sec => "SEC 공시",
            PlutoSource.Wire => "통신사 속보",
            _ => "출처 미상",
        };

        /// <summary>유형별 점수 밴드(포함). Noise = [0, 0].</summary>
        public static (int Min, int Max) Band(CatalystCategory c) => c switch
        {
            CatalystCategory.StructuralPositive => (StructuralMin, MaxScore),
            CatalystCategory.MomentumPositive => (MomentumMin, StructuralMin - 1),
            CatalystCategory.MomentumNegative => (-(StructuralMin - 1), -MomentumMin),
            CatalystCategory.StructuralNegative => (-MaxScore, -StructuralMin),
            _ => (0, 0),
        };

        public static bool InBand(CatalystCategory c, int score)
        {
            var (min, max) = Band(c);
            return score >= min && score <= max;
        }

        /// <summary>반감기(분). Noise = 0(감쇄 대상 아님).</summary>
        public static int HalfLifeMinutes(CatalystCategory c) => c switch
        {
            CatalystCategory.MomentumPositive or CatalystCategory.MomentumNegative => 10,
            CatalystCategory.StructuralPositive => 30,
            CatalystCategory.StructuralNegative => 60,
            _ => 0,
        };

        public static long HalfLifeTicks(CatalystCategory c) => HalfLifeMinutes(c) * TimeSpan.TicksPerMinute;

        public static bool IsPositive(CatalystCategory c) => c is CatalystCategory.StructuralPositive or CatalystCategory.MomentumPositive;
        public static bool IsNegative(CatalystCategory c) => c is CatalystCategory.StructuralNegative or CatalystCategory.MomentumNegative;
    }
}
