// ============================================================================
// ArbitrageRadarPort — [v12.18.19-DEV] 멀티 브로커 합성 스프레드 레이더 계약(순수 UI 경계 · 엔진 타입 참조 0)
//
//   호스트 SyntheticSpreadRadar 가 키움 L2(브리지 MMF) · KIS H0STASP0(WebSocket) 10호가를 같은 종목 슬롯에서 대조한 결과를 값 구조체로 노출.
//   단위 cbp = 0.01bp. 합성 스프레드 = |Mid키움 − MidKIS| · 교차 스프레드 = 높은 매수 − 낮은 매도(양수 = 역전) — 둘 다 평균 Mid 대비.
//   ★읽기 전용 — 이 포트에는 재무장 · 주문 경로가 없다(괴리 래치 해제는 호스트 물리 이중키뿐). SetFocus 는 표시 종목 선택만 바꾼다.
//   ★정직: 두 증권사는 같은 KRX 호가창을 중계한다 → 역전은 대개 한 피드의 노후 · '모의 차익'은 관측 지표(가상 기록)이지 실현 손익이 아니다.
// ============================================================================
using System;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>레이더 상태(0 미결선 · 1 대조 대기 · 2 정상 · 3 괴리(임계 이상 · 지속 전) · 4 역전 · 5 래치).</summary>
    public static class ArbRadar
    {
        public const int VenueKiwoom = 0, VenueKis = 1, SparkLength = 128;
        public const int Unbound = 0, Waiting = 1, Ok = 2, Diverging = 3, Inverted = 4, Latched = 5;

        public static string Label(int s) => s switch
        {
            Waiting => "양 브로커 호가 대조 대기", Ok => "호가 정합", Diverging => "호가 괴리(임계 초과 · 지속 감시)",
            Inverted => "교차 역전 감지(모의 차익)", Latched => "괴리 래치(비트 22 · 무장 차단)", _ => "아비트라지 레이더 미결선",
        };
        public static string Css(int s) => s switch { Ok => "ok", Diverging => "warn", Inverted => "opp", Latched => "bad", Waiting => "wait", _ => "off" };

        /// <summary>cbp → "12.34bp".</summary>
        public static string Bp(long cbp) => (cbp / 100.0).ToString("0.00") + "bp";
    }

    /// <summary>레이더 1프레임(값 복사).</summary>
    public readonly struct ArbitrageRadarStatus
    {
        public readonly int State;
        public readonly long FocusSymbol, SpreadCbp, CrossCbp, ImbalanceKiwoomCbp, ImbalanceKisCbp;
        public readonly long Comparisons, Opportunities, PaperQty, PaperGrossWon, PaperRefusedLatched;
        public readonly long TimestampInversions, StaleSkips, InvalidBooks, Trips, LatchSymbol, LatchSpreadCbp, ThresholdCbp, Samples;

        public ArbitrageRadarStatus(int state, long focusSymbol, long spreadCbp, long crossCbp, long imbalanceKiwoomCbp, long imbalanceKisCbp,
                                    long comparisons, long opportunities, long paperQty, long paperGrossWon, long paperRefusedLatched,
                                    long timestampInversions, long staleSkips, long invalidBooks, long trips, long latchSymbol, long latchSpreadCbp,
                                    long thresholdCbp, long samples)
        {
            State = state; FocusSymbol = focusSymbol; SpreadCbp = spreadCbp; CrossCbp = crossCbp;
            ImbalanceKiwoomCbp = imbalanceKiwoomCbp; ImbalanceKisCbp = imbalanceKisCbp;
            Comparisons = comparisons; Opportunities = opportunities; PaperQty = paperQty; PaperGrossWon = paperGrossWon; PaperRefusedLatched = paperRefusedLatched;
            TimestampInversions = timestampInversions; StaleSkips = staleSkips; InvalidBooks = invalidBooks; Trips = trips;
            LatchSymbol = latchSymbol; LatchSpreadCbp = latchSpreadCbp; ThresholdCbp = thresholdCbp; Samples = samples;
        }

        /// <summary>잔량 비대칭 괴리(|키움 − KIS|).</summary>
        public long ImbalanceDivergenceCbp => Math.Abs(ImbalanceKiwoomCbp - ImbalanceKisCbp);
    }

    public interface IArbitrageRadarPort
    {
        ArbitrageRadarStatus Status { get; }
        /// <summary>표시 종목(0 = 자동 — 최근 수신 종목).</summary>
        long Focus { get; }
        void SetFocus(long symbol);
        /// <summary>포커스 종목 venue 10호가 사본(각 Span ≥ 10 · 지수 0 = 최우선). 반환 = 수신 이력 있음.</summary>
        bool CopyDepth(int venue, Span<long> bidPx, Span<long> bidQty, Span<long> askPx, Span<long> askQty);
        /// <summary>합성 스프레드 스파크라인(오래된 → 최신 · −1 = 대조 전). 반환 = 채운 표본 수.</summary>
        int CopySpark(Span<int> dst);
    }
}
