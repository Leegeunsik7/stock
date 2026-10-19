// ============================================================================
// ArenaPersonaPort — [v12.18.15-DEV] 아레나 AI 전략 페르소나 계약(설명 가능 AI HUD) — 순수 UI 경계 · 엔진 타입 참조 0
//
//   ArenaPersona: AI 진영 두뇌 선택(0 = 기존 정량 시그널 엔진 · 1 스캘퍼 · 2 추세추종 · 3 평균회귀). 교체 = IArenaPort.SetPersona(다음 정산 회차 반영 ·
//     진행 중이면 AI 원장 비상 전량 청산 후 새 페르소나가 빈 원장에서 시작).
//   ArenaAiHud: 거버너가 50ms 게시 때 ArenaSnapshot 에 실어 보내는 의사결정 근거(VWAP · 이격률 · σ · 10호가 잔량 불균형 · 목표/손절가 · 마지막 판단과 사유).
//   [v12.18.18-DEV] 4번째 페르소나 포식자(Predator): 휴먼 원장의 연패 · 틸트 · 미청산 MAE 를 정산 스레드에서 역추적해 공격성(bp)을 조절 —
//     휴먼 연패 시 휴먼 DOM 래더 표시에만 스프레드 양방향 허수 잔량(가상 · 체결 불가 · 매수측 가중)을 얹어 추격 매수를 유도. 저공격 = TWAP 분할 은밀 진입.
//   ★읽기 전용 값 — 페르소나 주문의 목적지는 AI 가상 원장뿐(브로커 · 무장 경로 없음). 시뮬레이터는 공매도 불가 → 모든 페르소나는 매수 진입 · 매도 청산만.
// ============================================================================
namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>AI 전략 페르소나(0 = 기존 시그널 엔진 결정 분기).</summary>
    public static class ArenaPersona
    {
        public const int Signal = 0, Scalper = 1, Trend = 2, MeanReversion = 3, Predator = 4, Count = 5;
        public static bool IsValid(int p) => p is >= Signal and < Count;
        public static string Label(int p) => p switch
        {
            Signal => "시그널 엔진", Scalper => "스캘퍼 ⚡", Trend => "추세추종 📈", MeanReversion => "평균회귀 🔄", Predator => "포식자 🦈", _ => "?",
        };
        public static string Thesis(int p) => p switch
        {
            Signal => "정량 시그널 엔진의 진입/청산 결정을 그대로 집행",
            Scalper => "10호가 잔량 불균형 ≥ 65% + 스프레드 1틱 압축 → 2틱 목표 · 불균형 역전(≤ 35%) · 3초 경과 · 2틱 역행 시 즉시 청산",
            Trend => "EMA(8) > EMA(34) 정배열 + 직전 체결 대비 200% 거래량 폭증 → 추세 진입 · 손익비 2.5 목표 · 트레일링 스탑 홀딩",
            MeanReversion => "VWAP 하단 −1.5% 과다 이격 + 매수 지지벽 → 역진입 · VWAP 회귀 시 전량 익절 · 추가 −0.5% 이탈 시 손절",
            Predator => "휴먼 연패 · 틸트 · MAE 역추적 → 공격성 가중 · 연패 시 스프레드 양방향 허수 잔량(가상)으로 추격 매수 유도 · 선매수 후 추격 물량에 매도",
            _ => "",
        };
    }

    /// <summary>페르소나 판단(0 관망 · 1 진입 · 2 청산).</summary>
    public static class ArenaAiAction
    {
        public const int Hold = 0, Enter = 1, Exit = 2, EnterShort = 3;
        public static string Label(int a) => a switch { Enter => "진입", Exit => "청산", EnterShort => "숏 진입", _ => "관망" };
    }

    /// <summary>판단 사유 코드(설명 가능 AI HUD).</summary>
    public static class ArenaAiReason
    {
        public const int None = 0, Warmup = 1, Waiting = 2, Holding = 3, ImbalanceBuy = 4, ImbalanceFlip = 5, TakeProfit = 6, StopLoss = 7, Timeout = 8,
                         TrendBreakout = 9, TrailingStop = 10, DeadCross = 11, VwapLowerRevert = 12, VwapReverted = 13, BandBreak = 14, UpperBandNoShort = 15,
                         PreyStalk = 16, PreyAmbush = 17, VwapUpperShort = 18, PreySqueeze = 19;
        public static string Label(int r) => r switch
        {
            None => "조건 미충족 — 관망", Warmup => "지표 예열 중", Waiting => "주문 지연/작동 중 — 결과 대기", Holding => "보유 — 목표/손절 감시",
            ImbalanceBuy => "매수 잔량 우위 + 스프레드 압축", ImbalanceFlip => "호가 불균형 역전", TakeProfit => "목표가 도달 익절", StopLoss => "손절가 이탈",
            Timeout => "시간 초과 청산", TrendBreakout => "EMA 정배열 + 거래량 폭증", TrailingStop => "트레일링 스탑 발동", DeadCross => "EMA 역배열 전환",
            VwapLowerRevert => "VWAP 하단 과다 이격 + 매수 지지벽", VwapReverted => "VWAP 회귀 익절", BandBreak => "밴드 추가 이탈 손절",
            UpperBandNoShort => "VWAP 상단 과열 — 공매도 불가로 관측만(v12.18.22 이전)",
            VwapUpperShort => "VWAP 상단 과다 이격 + 매도 저항벽 — 가상 공매도", PreySqueeze => "휴먼 숏 손실 포착 — 숏 스퀴즈 추격 선매수",
            PreyStalk => "매수 우위 감지 — 은밀 분할 선매수", PreyAmbush => "휴먼 연패/틸트 포착 — 추격 유도 선매수", _ => "?",
        };
    }

    /// <summary>AI 의사결정 HUD 1프레임(원 · bp = 1/10000). ImbalanceBp = 매수 총잔량 ÷ (매수 + 매도) · VwapDevBp = (중간가 − VWAP) ÷ VWAP.</summary>
    public readonly struct ArenaAiHud
    {
        public readonly int Persona, Action, Reason, Switches, ImbalanceBp;
        public readonly long TargetSymbol, VwapWon, VwapDevBp, SigmaWon, VwapTrades, SpreadWon, TargetPx, StopPx, Decisions;
        /// <summary>[v12.18.18-DEV] 포식자 역추적: 공격성(bp) · 휴먼 연패 · 틸트 경보 누적 · 휴먼 미청산 MAE(원 ≤ 0) · 휴먼 래더에 얹은 허수 잔량(매수/매도 1호가 · 가상).</summary>
        public readonly int AggressionBp, PreyLossStreak, PreyTiltAlerts;
        public readonly long PreyMaeWon, PhantomBidQty, PhantomAskQty;

        public ArenaAiHud(int persona, int action, int reason, int switches, long targetSymbol, long vwapWon, long vwapDevBp, long sigmaWon, long vwapTrades,
                          int imbalanceBp, long spreadWon, long targetPx, long stopPx, long decisions,
                          int aggressionBp = 0, int preyLossStreak = 0, int preyTiltAlerts = 0, long preyMaeWon = 0, long phantomBidQty = 0, long phantomAskQty = 0)
        {
            AggressionBp = aggressionBp; PreyLossStreak = preyLossStreak; PreyTiltAlerts = preyTiltAlerts; PreyMaeWon = preyMaeWon;
            PhantomBidQty = phantomBidQty; PhantomAskQty = phantomAskQty;
            Persona = persona; Action = action; Reason = reason; Switches = switches; TargetSymbol = targetSymbol;
            VwapWon = vwapWon; VwapDevBp = vwapDevBp; SigmaWon = sigmaWon; VwapTrades = vwapTrades;
            ImbalanceBp = imbalanceBp; SpreadWon = spreadWon; TargetPx = targetPx; StopPx = stopPx; Decisions = decisions;
        }
    }
}
