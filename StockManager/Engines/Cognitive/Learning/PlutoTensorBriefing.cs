// ============================================================================
// PlutoTensorBriefing — v12.18.86-DEV: 36슬롯 인지 텐서 → 브리핑 표(12대 국면별 학습 성숙도 · 가중치 매트릭스 · 콜드 · CLI/진단 전용)
//
//   행 = 12대 국면(ID · 이름) + 자산 기준선 1행 · 열 = KRX · US · Upbit. 칸 = 성숙도 기호 + (보정됨 = 세대 · 모멘텀% · 엄격도 / 미보정 = 표본 수).
//   ○ 미학습(표본 0) · ◔ 축적 중(< 20) · ◑ 보정 가능(≥ 20 · 섀도우 통과 전) · ● 보정됨(섀도우 통과 핫스왑).
//   요약 = 보정된 국면 슬롯 수/36 · 국면별 성숙도(보정 슬롯 0~3/3). 학습 기록 0 = 그대로 '미학습' 표기(가짜 성숙도 0).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;

namespace StockManager.Engines.Cognitive.Learning
{
    public static class PlutoTensorBriefing
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Glyph(PlutoSlotMaturity m) => m switch
        {
            PlutoSlotMaturity.Calibrated => "●",
            PlutoSlotMaturity.Calibratable => "◑",
            PlutoSlotMaturity.Accumulating => "◔",
            _ => "○",
        };

        /// <summary>칸 1개 문구(고정 폭 근사 · 보정됨 = "● G3 +10%/+2점" · 미보정 = "◔ 표본 7").</summary>
        public static string CellText(in PlutoTensorCell c) => c.Maturity == PlutoSlotMaturity.Calibrated
            ? string.Create(Inv, $"{Glyph(c.Maturity)} G{c.Weights.Generation} {Signed(c.Weights.MomentumTiltPct)}%/{Signed(c.Weights.DevilStrictnessPts)}점")
            : string.Create(Inv, $"{Glyph(c.Maturity)} 표본 {c.LifetimeEpisodes}");

        public static IReadOnlyList<string> Render(PlutoCognitiveTensorLake tensor)
        {
            ArgumentNullException.ThrowIfNull(tensor);
            var lines = new List<string>(PlutoCognitiveTensorLake.Regimes + 6)
            {
                string.Create(Inv, $"🧮 36슬롯 인지 텐서 매트릭스 — 12대 국면 × KRX/US/Upbit · 보정 슬롯 {tensor.CalibratedSlots}/{PlutoCognitiveTensorLake.Slots}"),
                " 국면                              | 성숙도 | KRX                  | US                   | Upbit",
            };
            for (int regime = 1; regime <= PlutoCognitiveTensorLake.Regimes; regime++)
            {
                int calibrated = 0;
                var cells = new string[PlutoCognitiveTensorLake.Assets];
                for (int a = 0; a < PlutoCognitiveTensorLake.Assets; a++)
                {
                    var c = tensor.Cell(PlutoCognitiveTensorLake.CellOf((PlutoAssetClass)a, regime));
                    if (c.Maturity == PlutoSlotMaturity.Calibrated) calibrated++;
                    cells[a] = CellText(in c);
                }
                string name = PlutoRegimeVectorLake.Get(regime)?.Name ?? "?";
                lines.Add(string.Create(Inv, $" {regime:D2} {Pad(name, 31)}| {calibrated}/3    | {Pad(cells[0], 21)}| {Pad(cells[1], 21)}| {cells[2]}"));
            }
            var b = new string[PlutoCognitiveTensorLake.Assets];
            int bc = 0;
            for (int a = 0; a < PlutoCognitiveTensorLake.Assets; a++)
            {
                var c = tensor.Cell(PlutoCognitiveTensorLake.CellOf((PlutoAssetClass)a, PlutoCognitiveTensorLake.BaselineRegime));
                if (c.Maturity == PlutoSlotMaturity.Calibrated) bc++;
                b[a] = CellText(in c);
            }
            lines.Add(string.Create(Inv, $" -- {Pad("자산 기준선(국면 판정 전 폴백)", 31)}| {bc}/3    | {Pad(b[0], 21)}| {Pad(b[1], 21)}| {b[2]}"));
            lines.Add("   범례: ○ 미학습 · ◔ 축적 중(<20) · ◑ 보정 가능(≥20) · ● 보정됨(세대 · 모멘텀%/엄격도) — 미보정 슬롯 = 기준선 → 전역 가중치 폴백");
            return lines;
        }

        private static string Signed(long v) => (v >= 0 ? "+" : "") + v.ToString(Inv);

        private static string Pad(string s, int width) => s.Length >= width ? s + " " : s.PadRight(width);
    }
}
