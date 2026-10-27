// ============================================================================
// PlutoInspector — v12.18.92-DEV: 플루토 인지 데이터 인스펙터 · 진화 진행도(ETA) · 쿨다운 다이얼 UI 경계(순수 DTO · 순수 계산 · 엔진 타입 참조 0)
//
//   PlutoTensorCellView = 39셀 인지 텐서 1셀(국면 행 1~12 + 자산 기준선 행 = RegimeId −1 · 열 = PlutoRecoMarket 값) — 호스트 어댑터가 콜드 스냅샷으로 옮긴다.
//   PlutoEvolutionEta = 다음 진화 심사까지 표본 진행도: 첫 심사 = 누적 표본 N / 20(보정기 최소 표본) · 이후 = 직전 심사 이후 새 표본 n / 10(재보정 주기).
//     ★심사 ≠ 진화 확정 — 섀도우 검증 통과 시에만 세대 +1(불통과 = 현행 유지) · 직전 심사 기준점 미상(−1) = 첫 심사 이후 진행도 미표기(가짜 진행률 0).
//   PlutoCooldownDial = 연패 쿨다운(15분) 남은 비율 % · 판독 전용 — 주문 · 무장 · 브로커 경로 0.
// ============================================================================
using System;
using System.Globalization;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>텐서 셀 성숙도 코드 — 0 미학습 · 1 축적 중(표본 &lt; 20) · 2 심사 가능(표본 ≥ 20 · 섀도우 통과 전) · 3 보정 완료(세대 &gt; 0 · 미상 = 미학습).</summary>
    public static class PlutoSlotMaturityCode
    {
        public const int Untrained = 0, Accumulating = 1, Calibratable = 2, Calibrated = 3;

        public static int Normalize(int m) => m is >= Accumulating and <= Calibrated ? m : Untrained;

        public static string Label(int m) => Normalize(m) switch
        {
            Accumulating => "축적 중",
            Calibratable => "심사 가능",
            Calibrated => "보정 완료",
            _ => "미학습",
        };

        public static string Css(int m) => Normalize(m) switch
        {
            Accumulating => "acc",
            Calibratable => "ready",
            Calibrated => "on",
            _ => "none",
        };
    }

    /// <summary>
    /// 39셀 인지 텐서 1셀. RegimeId = 1~12 국면 · −1 = 자산 기준선(국면 판정 전 폴백) · RegimeName = 국면명(기준선 = "자산 기준선") ·
    /// Asset = PlutoRecoMarket 값 · Generation/MomentumTiltPct/DevilStrictnessPts = 셀 가중치 · Maturity = PlutoSlotMaturityCode ·
    /// Lifetime = 셀 평생 에피소드 · Wins/Losses = 셀 보정기 링(최근 ≤ 256) 실측 승패 · WinRateBp = 실측 승률(표본 0 = 0).
    /// </summary>
    public readonly record struct PlutoTensorCellView(int RegimeId, string RegimeName, int Asset, long Generation, int MomentumTiltPct, int DevilStrictnessPts,
        int Maturity, long Lifetime, int Wins, int Losses, int WinRateBp)
    {
        public const int BaselineRegime = -1;

        public bool IsBaseline => RegimeId == BaselineRegime;

        public int Samples => Wins + Losses;

        /// <summary>실측 승률 문구 "58.3% (7승 5패)" · 표본 0 = "—".</summary>
        public string WinText => Samples == 0 ? "—"
            : (WinRateBp / 100m).ToString("0.0", CultureInfo.InvariantCulture) + "% (" + Wins.ToString(CultureInfo.InvariantCulture) + "승 " + Losses.ToString(CultureInfo.InvariantCulture) + "패)";

        /// <summary>엄격도 성향(+ = 방어 · − = 공격 · 0 = 중립).</summary>
        public string StanceText => DevilStrictnessPts > 0 ? "방어" : DevilStrictnessPts < 0 ? "공격" : "중립";
    }

    /// <summary>다음 진화 심사까지 표본 진행도(Known false = 기준점 미상 → 미표기).</summary>
    public readonly record struct PlutoEvolutionProgress(bool Known, bool FirstGate, long Have, long Need)
    {
        public int Pct => !Known || Need <= 0 ? 0 : (int)Math.Clamp(Have * 100 / Need, 0, 100);

        public string Text => !Known ? "첫 심사 통과 — 재보정 주기(" + PlutoEvolutionEta.Every.ToString(CultureInfo.InvariantCulture) + "표본마다) 진행 중"
            : (FirstGate ? "차세대 진화 첫 심사까지 표본: " : "다음 진화 심사까지 새 표본: ")
              + Have.ToString(CultureInfo.InvariantCulture) + " / " + Need.ToString(CultureInfo.InvariantCulture) + " (" + Pct.ToString(CultureInfo.InvariantCulture) + "%)";
    }

    /// <summary>진화 심사 진행도 계산(엔진 상수 미러 — 드리프트 = 테스트 실패).</summary>
    public static class PlutoEvolutionEta
    {
        /// <summary>보정기 최소 표본(PlutoWeightCalibrator.MinSamples) · 재보정 주기(PlutoContinuousLearningSwarm.RecalibrateEvery).</summary>
        public const int FirstGate = 20, Every = 10;

        /// <summary>samples = 누적 청산 회고 표본 · anchor = 직전 심사 시점 표본(−1 = 미상).</summary>
        public static PlutoEvolutionProgress Of(long samples, long anchor)
        {
            long n = Math.Max(0, samples);
            if (n < FirstGate) return new PlutoEvolutionProgress(true, true, n, FirstGate);
            if (anchor < 0) return new PlutoEvolutionProgress(false, false, 0, Every);
            return new PlutoEvolutionProgress(true, false, Math.Clamp(n - anchor, 0, Every), Every);
        }
    }

    /// <summary>연패 쿨다운 다이얼(PlutoDynamicSizer.CooldownTicks = 15분 미러).</summary>
    public static class PlutoCooldownDial
    {
        public const int Minutes = 15;

        /// <summary>남은 비율 %(0~100).</summary>
        public static int Pct(int remainingMinutes) => Math.Clamp(remainingMinutes, 0, Minutes) * 100 / Minutes;

        /// <summary>원형 다이얼 conic-gradient(남은 부분 = 적색 · 경과 = 슬레이트).</summary>
        public static string Ring(int remainingMinutes)
            => "background:conic-gradient(#E53935 0 " + Pct(remainingMinutes).ToString(CultureInfo.InvariantCulture) + "%, #1F2937 0)";
    }
}
