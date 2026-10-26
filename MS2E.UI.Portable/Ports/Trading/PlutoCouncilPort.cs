// ============================================================================
// PlutoCouncilPort — v12.18.88-DEV: 4대 인지 에이전트 위원회 · 교차 자산 선행 파급 · 프랙탈 켈리 UI 경계(순수 DTO · 엔진 타입 참조 0)
//
//   PlutoCouncilView     = 활성 국면 셀(자산군 3레인) 합의 가중치 4개(bp · 합 10000) · 켈리 권장 비중 · 비토 사유 · 선행 파급 점수/문구.
//   PlutoKellyCode       = 켈리 상태 코드(0 미결선 · 1 표본 부족 · 2 음의 엣지 · 3 산출) — 표본 부족/음의 엣지 = 비중 미표기(날조 0 · CLAUDE.md §2).
//   PlutoCouncilAgentCode = 에이전트 4종 라벨 · 색(추세 = 적색 계열 · 역발상 = 청색 계열 · 수급 · 위험).
//   ★판독 전용 — 주문 · 무장 · 브로커 경로 0(liveArmed:false).
// ============================================================================
using System.Globalization;

namespace MS2E.UI.Portable.Ports.Trading
{
    public static class PlutoKellyCode
    {
        public const int Unwired = 0, Insufficient = 1, NoEdge = 2, Sized = 3;
        public const int MinSamples = 20;

        public static int Normalize(int k) => k is >= Insufficient and <= Sized ? k : Unwired;

        /// <summary>매트릭스 셀 문구: 산출 = "12.5%" · 음의 엣지 = "0%" · 표본 부족 = "n/20" · 미결선 = "—".</summary>
        public static string Cell(int state, int weightBp, int samples) => Normalize(state) switch
        {
            Sized => Pct(weightBp),
            NoEdge => "0%",
            Insufficient => samples.ToString(CultureInfo.InvariantCulture) + "/" + MinSamples.ToString(CultureInfo.InvariantCulture),
            _ => "—",
        };

        public static string Label(int state) => Normalize(state) switch
        {
            Sized => "켈리 산출",
            NoEdge => "음의 엣지 · 진입 비권장",
            Insufficient => "표본 부족",
            _ => "켈리 미결선",
        };

        public static string Css(int state) => Normalize(state) switch
        {
            Sized => "on",
            NoEdge => "fail",
            Insufficient => "stale",
            _ => "none",
        };

        public static string Pct(int bp) => (bp / 100m).ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    public static class PlutoCouncilAgentCode
    {
        public const int Trend = 0, Reversion = 1, Liquidity = 2, Risk = 3, Count = 4;

        public static string Label(int a) => a switch
        {
            Trend => "TrendScout",
            Reversion => "MeanReversion",
            Liquidity => "LiquidityPredator",
            _ => "ChiefRiskOfficer",
        };

        public static string Korean(int a) => a switch
        {
            Trend => "추세",
            Reversion => "역발상",
            Liquidity => "호가 수급",
            _ => "위험 통제",
        };

        public static string Css(int a) => a switch { Trend => "ag-t", Reversion => "ag-r", Liquidity => "ag-l", _ => "ag-c" };
    }

    /// <summary>자산군 1레인(Asset = PlutoRecoMarket 값) — 합의 가중치 4개(bp) · 켈리(KellyState = PlutoKellyCode · 엣지 승률/손익비/표본).</summary>
    public readonly record struct PlutoCouncilLaneView(int Asset, int TrendBp, int ReversionBp, int LiquidityBp, int RiskBp,
        int KellyState, int KellyBp, int WinRateBp, int PayoffX100, int Samples)
    {
        public int WeightOf(int agent) => agent switch
        {
            PlutoCouncilAgentCode.Trend => TrendBp,
            PlutoCouncilAgentCode.Reversion => ReversionBp,
            PlutoCouncilAgentCode.Liquidity => LiquidityBp,
            _ => RiskBp,
        };

        public int Sum => TrendBp + ReversionBp + LiquidityBp + RiskBp;

        /// <summary>최대 가중 에이전트(동률 = 코드 순서 앞).</summary>
        public int Lead
        {
            get
            {
                int lead = 0;
                for (int a = 1; a < PlutoCouncilAgentCode.Count; a++) if (WeightOf(a) > WeightOf(lead)) lead = a;
                return lead;
            }
        }
    }

    /// <summary>
    /// 위원회 상태. Wired false = 위원회 미결선 호스트 · Published false = 첫 게시 전 · RegimeId 0 = 국면 판정 전(기준선 행) ·
    /// VetoText 빈 값 = 비토 없음 · LeadPts = KRX 선행 파급 점수 · SpilloverHasData false = 파급 판독 없음 · CouncilVetoed = 직전 스캔 비토 탈락 종목 수.
    /// </summary>
    public sealed record PlutoCouncilView(bool Wired, bool Published, int RegimeId, string? RegimeName, IReadOnlyList<PlutoCouncilLaneView> Lanes,
        string VetoText, int LeadPts, bool SpilloverHasData, string SpilloverText, int CouncilVetoed)
    {
        public static readonly PlutoCouncilView None = new(false, false, 0, null, Array.Empty<PlutoCouncilLaneView>(), "", 0, false, "", 0);

        public bool Vetoed => VetoText.Length > 0;
    }
}
