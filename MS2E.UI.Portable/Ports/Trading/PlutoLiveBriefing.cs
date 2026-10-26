// ============================================================================
// PlutoLiveBriefing — v12.18.89-DEV: DualArena 사용자 친화 한 줄 브리핑 · 3대 시장 신호등(순수 표시 모델 · 엔진 타입 참조 0 · 포트 DTO 만 입력)
//
//   PlutoMarketLight     = 시장 1개 신호등(🟢 실시간 감시 · 🌙 장외 · 🟡 대기 · ⚪ 미결선) — 판정 근거 = 아레나 스냅샷(KRX SessionOpen · US Phase · Upbit Tradable)
//                          + 스웜 레인 실피드 결선 + 거시 피드 키 상태. 시각 = 엔진 SessionKst(호스트 시계 재판독 0). 공휴일 달력 미반영(다음 개장 = 평일 기준 예정).
//   PlutoLiveBriefing    = [🧠 플루토 AI 라이브 브리핑] 한 줄(모드 + KRX · Upbit · US 문장 + AI 방침). 후보 종목 = 추천 기준 통과 카드 이름만(날조 0 — CLAUDE.md §2).
//   PlutoMacroFeed       = 호스트 거시 피드 문구 → FRED 키 대기 판정(PlutoPublicMacroSource.FeedText 문구 계약).
//   ★판독 전용 — 주문 · 무장 · 브로커 경로 0(liveArmed:false).
// ============================================================================
using System.Globalization;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>신호등 상태 코드(0 미결선 · 1 실시간 감시 · 2 장외 · 3 대기).</summary>
    public static class PlutoLightCode
    {
        public const int Off = 0, Live = 1, Closed = 2, Await = 3;

        public static string Icon(int s) => s switch { Live => "🟢", Closed => "🌙", Await => "🟡", _ => "⚪" };

        public static string Css(int s) => s switch { Live => "live", Closed => "closed", Await => "await", _ => "off" };
    }

    /// <summary>시장 신호등 1개 — Label = 상태 문구 · Next = 다음 개장 예정(null = 미표기).</summary>
    public readonly record struct PlutoMarketLight(int Market, int State, string Label, string? Next = null)
    {
        /// <summary>"🌙 장마감 (내일 09:00 개장)" · "🟢 실시간 감시 중".</summary>
        public string Badge => PlutoLightCode.Icon(State) + " " + Label + (Next is null ? "" : " (" + Next + ")");

        /// <summary>매트릭스 대기 행 문구: 장외 = "🌙 장마감 대기 중 (내일 09:00 개장)" · 그 외 = "시세 수신 대기 — {note}".</summary>
        public string AwaitText(string note) => State == PlutoLightCode.Closed
            ? "🌙 " + Label + " 대기 중" + (Next is null ? "" : " (" + Next + ")")
            : "시세 수신 대기 — " + note;
    }

    /// <summary>3대 시장 신호등(KRX · US · Upbit).</summary>
    public sealed record PlutoMarketLights(PlutoMarketLight Krx, PlutoMarketLight Us, PlutoMarketLight Crypto)
    {
        public static readonly PlutoMarketLights None = new(
            new(PlutoRecoMarket.Krx, PlutoLightCode.Off, "시세 미결선"), new(PlutoRecoMarket.Us, PlutoLightCode.Off, "시세 미결선"), new(PlutoRecoMarket.Crypto, PlutoLightCode.Off, "시세 미결선"));

        public PlutoMarketLight Of(int market) => market switch { PlutoRecoMarket.Us => Us, PlutoRecoMarket.Crypto => Crypto, _ => Krx };
    }

    public static class PlutoMacroFeed
    {
        /// <summary>FRED 키 미설정/형식 오류 = 거시 분석 대기(키 설정 시 가동).</summary>
        public static bool IsKeyPending(string? feedText) => feedText is not null && (feedText.Contains("키 미설정", StringComparison.Ordinal) || feedText.Contains("키 형식 오류", StringComparison.Ordinal));
    }

    public static class PlutoLiveBriefing
    {
        /// <summary>KRX: 미결선 = ⚪ · 정규장 = 🟢 · 장외 = 🌙(개장 전 · 장마감 · 주말 휴장 + 다음 개장) · 실피드 미결선 = 🟡.</summary>
        public static PlutoMarketLight Krx(bool has, bool sessionOpen, DateTime? kst, PlutoSwarmLaneView? lane)
        {
            if (!has || kst is not { } t) return new(PlutoRecoMarket.Krx, PlutoLightCode.Off, "시세 미결선");
            if (!sessionOpen)
            {
                bool weekend = t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
                string label = weekend ? "주말 휴장" : t.TimeOfDay < KrxOpen ? "개장 전" : "장마감";
                return new(PlutoRecoMarket.Krx, PlutoLightCode.Closed, label, KrxNextOpen(t));
            }
            return lane is { Connected: false }
                ? new(PlutoRecoMarket.Krx, PlutoLightCode.Await, "실피드 연결 대기")
                : new(PlutoRecoMarket.Krx, PlutoLightCode.Live, "실시간 감시 중");
        }

        /// <summary>US: 미결선/달력 밖 = ⚪ · 휴장/장 종료/애프터 = 🌙 · 거시 키 대기 · 프리마켓 · 실피드 미결선 = 🟡 · 정규장 = 🟢.</summary>
        public static PlutoMarketLight Us(bool has, int phase, bool macroKeyPending, PlutoSwarmLaneView? lane)
        {
            if (!has) return new(PlutoRecoMarket.Us, PlutoLightCode.Off, "시세 미결선");
            return phase switch
            {
                UsArenaPhase.CalendarUncovered => new(PlutoRecoMarket.Us, PlutoLightCode.Off, "달력 범위 밖(fail-closed)"),
                UsArenaPhase.HolidayOrWeekend => new(PlutoRecoMarket.Us, PlutoLightCode.Closed, "휴장"),
                UsArenaPhase.Closed => new(PlutoRecoMarket.Us, PlutoLightCode.Closed, "장마감"),
                UsArenaPhase.AfterHours => new(PlutoRecoMarket.Us, PlutoLightCode.Closed, "애프터마켓(체결 차단)"),
                _ when macroKeyPending => new(PlutoRecoMarket.Us, PlutoLightCode.Await, "거시 피드 대기"),
                UsArenaPhase.PreMarket => new(PlutoRecoMarket.Us, PlutoLightCode.Await, "프리마켓(정규장 대기)"),
                _ when lane is { Connected: false } => new(PlutoRecoMarket.Us, PlutoLightCode.Await, "실피드 연결 대기"),
                UsArenaPhase.ContinuousTrading => new(PlutoRecoMarket.Us, PlutoLightCode.Live, "실시간 감시 중"),
                _ => new(PlutoRecoMarket.Us, PlutoLightCode.Off, "세션 판독 불가"),
            };
        }

        /// <summary>Upbit(24/7): 미결선 = ⚪ · 세션 시각 무효 = ⚪(fail-closed) · 실피드 미결선 = 🟡 · 그 외 = 🟢.</summary>
        public static PlutoMarketLight Crypto(bool has, bool tradable, PlutoSwarmLaneView? lane)
        {
            if (!has) return new(PlutoRecoMarket.Crypto, PlutoLightCode.Off, "시세 미결선");
            if (!tradable) return new(PlutoRecoMarket.Crypto, PlutoLightCode.Off, "세션 시각 무효(fail-closed)");
            return lane is { Connected: false }
                ? new(PlutoRecoMarket.Crypto, PlutoLightCode.Await, "실피드 연결 대기")
                : new(PlutoRecoMarket.Crypto, PlutoLightCode.Live, "실시간 감시 중");
        }

        /// <summary>
        /// 한 줄 브리핑. 예(야간 · KRX 장마감 · Upbit 감시 · 후보 2):
        /// "🌙 야간 장마감 모드: 국내 주식은 내일 09:00 개장 대기 중입니다. 가상자산 120종을 정밀 감시 중이며, 추천 후보(리플·테더)를 관측하고 있습니다. 미국 주식은 거시 피드 대기 중입니다. AI 방침: …"
        /// </summary>
        public static string Compose(PlutoMarketLights lights, DateTime? kst, bool learningWired, in PlutoLearningView lv, bool recoWired, PlutoRecoBoardView board)
        {
            ArgumentNullException.ThrowIfNull(lights);
            ArgumentNullException.ThrowIfNull(board);
            var k = lights.Krx;
            string mode = k.State switch
            {
                PlutoLightCode.Closed when k.Label == "주말 휴장" => "🌙 주말 휴장 모드",
                PlutoLightCode.Closed when k.Label == "개장 전" && kst is { Hour: >= 7 } => "🌅 개장 전 모드",
                PlutoLightCode.Closed when kst is { Hour: >= 18 or < 7 } => "🌙 야간 장마감 모드",
                PlutoLightCode.Closed => "🌙 장마감 모드",
                PlutoLightCode.Live => "☀️ 정규장 모드",
                _ => "📡 시세 연결 확인 모드",
            };

            string krx = k.State switch
            {
                PlutoLightCode.Closed => "국내 주식은 " + (k.Next ?? "다음 개장") + " 대기 중입니다.",
                PlutoLightCode.Live => "국내 주식" + Count(recoWired, board.Krx.Monitored) + "을 실시간 감시 중입니다.",
                PlutoLightCode.Await => "국내 주식은 실피드 연결을 기다리고 있습니다.",
                _ => "국내 주식 시세는 아직 연결되지 않았습니다.",
            };

            var c = lights.Crypto;
            string crypto;
            if (c.State == PlutoLightCode.Live)
            {
                string picks = Picks(board.Crypto.Cards);
                crypto = "가상자산" + Count(recoWired, board.Crypto.Monitored) + "을 정밀 감시 중이며, "
                    + (!recoWired ? "추천 엔진은 꺼져 있습니다." : board.Generation == 0 ? "첫 스캔을 기다리고 있습니다."
                       : picks.Length > 0 ? "추천 후보(" + picks + ")를 관측하고 있습니다." : "추천 기준을 통과한 후보는 아직 없습니다.");
            }
            else crypto = c.State == PlutoLightCode.Await ? "가상자산은 실피드 연결을 기다리고 있습니다." : "가상자산 시세는 " + c.Label + " 상태입니다.";

            var u = lights.Us;
            string us = u.State switch
            {
                PlutoLightCode.Live => "미국 주식은 정규장을 실시간 감시 중입니다.",
                PlutoLightCode.Closed => "미국 주식은 " + u.Label + " 상태입니다.",
                PlutoLightCode.Await => "미국 주식은 " + u.Label + " 중입니다.",
                _ => "미국 주식 시세는 " + u.Label + " 상태입니다.",
            };

            bool anyLive = k.State == PlutoLightCode.Live || u.State == PlutoLightCode.Live || c.State == PlutoLightCode.Live;
            string stance = learningWired
                ? PlutoWeightScale.TiltLabel(PlutoWeightScale.TiltX100(lv.MomentumTiltPct, lv.DevilStrictnessPts)) + " 성향 · 학습 " + lv.Generation.ToString(CultureInfo.InvariantCulture) + "세대"
                : "학습 엔진 미결선 · 기본 규칙";
            string action = anyLive
                ? "AI 방침: 열린 시장만 가상 매매합니다(" + stance + " · 실주문 0)."
                : "AI 방침: 열린 시장이 없어 신규 진입 없이 관망합니다(" + stance + ").";

            return mode + ": " + krx + " " + crypto + " " + us + " " + action;
        }

        /// <summary>"내일 09:00 개장" · "오늘 09:00 개장" · "월요일 09:00 개장"(평일 기준 · 공휴일 미반영).</summary>
        public static string KrxNextOpen(DateTime kst)
        {
            bool weekday = kst.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            if (weekday && kst.TimeOfDay < KrxOpen) return "오늘 09:00 개장";
            var next = kst.Date.AddDays(1);
            while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) next = next.AddDays(1);
            return (next == kst.Date.AddDays(1) ? "내일" : DayName(next.DayOfWeek)) + " 09:00 개장";
        }

        private static readonly TimeSpan KrxOpen = new(9, 0, 0);

        private static string DayName(DayOfWeek d) => d switch
        {
            DayOfWeek.Monday => "월요일", DayOfWeek.Tuesday => "화요일", DayOfWeek.Wednesday => "수요일",
            DayOfWeek.Thursday => "목요일", DayOfWeek.Friday => "금요일", DayOfWeek.Saturday => "토요일", _ => "일요일",
        };

        private static string Count(bool recoWired, int monitored) => recoWired && monitored > 0 ? " " + monitored.ToString(CultureInfo.InvariantCulture) + "종" : " 시장";

        /// <summary>추천 기준 통과 상위 2종 이름("리플·테더" · 없음 = "").</summary>
        private static string Picks(IReadOnlyList<PlutoRecommendationCard> cards)
        {
            if (cards.Count == 0) return "";
            return cards.Count == 1 ? cards[0].Name : cards[0].Name + "·" + cards[1].Name;
        }
    }

    public static class PlutoPlainText
    {
        /// <summary>학습 성숙도 보조 문구: 0세대 = "시세 데이터 축적 중" · 그 외 = "학습 가중치 적용 중".</summary>
        public static string MaturityNote(long generation) => generation <= 0 ? "시세 데이터 축적 중" : "학습 가중치 적용 중";

        /// <summary>텐서 보정 문구: 0 = "국면 가중치 (기본값)" · n = "국면 가중치 보정 n/36".</summary>
        public static string TensorCalibration(int calibrated, int slots = 36) => calibrated <= 0
            ? "국면 가중치 (기본값)"
            : "국면 가중치 보정 " + calibrated.ToString(CultureInfo.InvariantCulture) + "/" + slots.ToString(CultureInfo.InvariantCulture);
    }
}
