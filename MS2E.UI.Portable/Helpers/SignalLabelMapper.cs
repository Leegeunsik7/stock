// ============================================================================
// SignalLabelMapper — [대시보드 UX · 추천 A] 판단 근거 · 인터락 · 확신도 코드 → 정적 한글 문자열(조회 = 할당 0)
//
//  관제 프레임(PaperDashboardFrame)은 코드 · 수치만 싣는다(허브 무할당). 이 매퍼는 렌더 시점에 코드를 '미리 만들어 둔' 정적 문자열로
//  바꾼다 — 배열 인덱싱 · 범위 밖 = 정적 폴백. 문자열 보간/연결/ToString 0(테스트가 0B 실측).
//  ★RCL 순수성: 엔진 열거형(QuantReason · ArmingInterlockSource)을 참조하지 않는다 — 같은 코드 표를 여기 복제하고, 엔진 값과의
//    동기화(누락/순서 이탈)는 호스트 테스트(DashboardUxLabelMappingTests)가 열거형 전수 대조로 강제한다.
//  확신도: 0~100% 텍스트 101개 정적 캐시 · 0.85 게이트 통과 여부 배지(매수 = 적색 · 매도 = 청색 · 보류/거부 = 회색 — 상승 적/하강 청 규약).
//  ★정직: Blazor 렌더 트리 · 속성 문자열화 자체는 프레임워크 할당이다 — 0B 보증 범위는 '라벨 조회 · 확신도 텍스트 · 스파크라인 좌표 작성'.
// ============================================================================
using System;

namespace MS2E.UI.Portable.Helpers
{
    public static class SignalLabelMapper
    {
        public const string Unknown = "미정의 코드";

        // ── 판단 근거(QuantReason 코드 0..15 — 엔진 열거형과 같은 순서) ──
        private static readonly string[] Reasons =
        {
            "근거 없음",                       // 0 None
            "5호가 매수 잔량 우위",            // 1 ImbalanceBuy
            "5호가 매도 잔량 우위",            // 2 ImbalanceSell
            "10호가 매수 잔량 추세(선행)",     // 3 MicroTrendBuy
            "10호가 매도 잔량 추세(선행)",     // 4 MicroTrendSell
            "VWAP 과이격 추격 감쇄",           // 5 VwapStretch
            "변동성 급등 — 거부",              // 6 VolatilitySpikeVeto
            "신호 만료",                       // 7 StaleSignal
            "확신도 미달",                     // 8 LowConfidence
            "방향 불일치",                     // 9 SideMismatch
            "전략 산출 이상",                  // 10 Anomaly
            "신호 없음",                       // 11 NoSignal
            "키움↔KIS 호가 꼬임",             // 12 CrossedFeed
            "키움↔KIS 방향 불일치",           // 13 CrossDisagree
            "단일 피드(확신 ½)",              // 14 SingleSource
            "피드 시각 이탈 > 1초 — 거부",     // 15 CrossSkewVeto
        };

        // 판단 근거 성격(배지 색): 0 중립 · 1 매수 근거 · 2 매도 근거 · 3 경고(거부/이상)
        private static readonly byte[] ReasonTone = { 0, 1, 2, 1, 2, 0, 3, 0, 0, 0, 3, 0, 3, 0, 0, 3 };

        public static int ReasonCount => Reasons.Length;
        public static string Reason(int code) => (uint)code < (uint)Reasons.Length ? Reasons[code] : Unknown;
        public static bool IsWarningReason(int code) => (uint)code < (uint)ReasonTone.Length && ReasonTone[code] == 3;

        // ── 인터락 비트(ArmingInterlockSource — 0..28 + 63) ──
        private static readonly string[] Interlocks =
        {
            "부팅 무결성",                 // 0 BootIntegrity
            "자격증명 유출",               // 1 CredentialLeak
            "포렌식 체인",                 // 2 ForensicChain
            "이상 시세",                   // 3 MarketSpike
            "카오스 진행 가드",            // 4 ChaosProgress
            "체결 충격",                   // 5 ExecutionImpact
            "라우터 피드 단절",            // 6 RouterFeed
            "주문 슬롯 시한",              // 7 OrderSlotAge
            "극한 슬리피지",               // 8 ExtremeSlippage
            "MMF IPC 오염",                // 9 MmfFabric
            "브로커 잔량 불일치",          // 10 BrokerStateMismatch
            "호가 비유동성",               // 11 OrderbookIlliquidity
            "지연 정지",                   // 12 LatencyStall
            "키움 서버 오류",              // 13 KiwoomServerError
            "계좌 보유 불일치",            // 14 AccountDivergence
            "송출 우회 차단",              // 15 EgressBypass
            "사전 검증(시계/설정)",        // 16 PreFlightBoot
            "상관 급락",                   // 17 CorrelationCrash
            "KIS 인증",                    // 18 KisAuth
            "KIS 실시간 세션",             // 19 KisSession
            "롤오버 승계",                 // 20 Rollover
            "헬스 에이전트(자원)",         // 21 HealthAgent
            "미체결 대사",                 // 22 OpenOrderDivergence
            "자식 슬롯 거부/불균형",       // 23 ChildSlotRejection
            "KIS 한도 피드",               // 24 KisLimitFeed
            "키움 브리지 링크",            // 25 KiwoomBridgeLink
            "KIS 프로토콜 이탈",           // 26 KisProtocolDivergence
            "전략 이상(호가 꼬임 포함)",   // 27 StrategyAnomaly
            "주문 경로 지연(p99)",         // 28 OrderPathLatency
        };
        public const int StartupSanityBit = 63;
        private const string StartupSanity = "기동 샌니티(미봉인)";

        /// <summary>즉시 주목해야 할 비트(점멸 경고): 21 헬스 에이전트 · 27 전략 이상(호가 꼬임) · 15 송출 우회 · 1 자격증명 유출.</summary>
        public static bool IsFlashBit(int bit) => bit is 21 or 27 or 15 or 1;

        public static int InterlockCount => Interlocks.Length + 1;
        public static string Interlock(int bit)
            => (uint)bit < (uint)Interlocks.Length ? Interlocks[bit] : bit == StartupSanityBit ? StartupSanity : Unknown;

        /// <summary>비트 마스크에 점멸 대상이 하나라도 있으면 true(δ=0).</summary>
        public static bool HasFlash(long bits) => (bits & ((1L << 21) | (1L << 27) | (1L << 15) | (1L << 1))) != 0;

        // ── 확신도 ──
        public const long GateBp = 8_500;
        private static readonly string[] Percent = BuildPercent();
        private static string[] BuildPercent()
        {
            var p = new string[101];
            for (int i = 0; i <= 100; i++) p[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
            return p;
        }

        /// <summary>확신도(bp · 0~10,000) → "0%".."100%" 정적 문자열(내림 · 범위 밖 = 클램프).</summary>
        public static string ConfidenceText(long bp) => Percent[(int)Math.Clamp(bp / 100, 0, 100)];

        /// <summary>진행률 바 너비(%) 정적 문자열 — CSS width 값에 그대로 사용.</summary>
        public static string ConfidenceWidth(long bp) => ConfidenceText(bp);

        /// <summary>배지 CSS 클래스: 게이트(0.85) 통과 매수 = "up" · 매도 = "down" · 그 외 = "hold"(경고 근거면 "warn").</summary>
        public static string BadgeClass(int kind, long bp, int reason)
            => IsWarningReason(reason) ? "warn" : bp >= GateBp && kind == 1 ? "up" : bp >= GateBp && kind == 2 ? "down" : "hold";

        /// <summary>배지 문구(정적).</summary>
        public static string BadgeText(int kind, long bp, int reason)
            => IsWarningReason(reason) ? "거부" : bp >= GateBp && kind == 1 ? "매수 확신" : bp >= GateBp && kind == 2 ? "매도 확신" : "보류";

        // ── [v12.17.5-DEV] 주문 경로 지연 등급 · 구간 · 마이크로초 라벨 ──
        //  등급 코드 = DashLatencyGrade(0 정상 · 1 미세 정체 · 2 심각 지연 · 3 미계측). 배지 색: 정상 녹 · 미세 정체 황 · 심각 지연 적 · 미계측 회.
        private static readonly string[] LatencyGrades = { "정상", "미세 정체", "심각 지연", "미계측" };
        private static readonly string[] LatencyGradeClasses = { "ok", "spike", "lag", "off" };
        private static readonly string[] LatencyStages = { "시그널→게이트", "게이트→출구", "시그널→발주" };

        public static int LatencyGradeCount => LatencyGrades.Length;
        public static string LatencyGradeLabel(int grade) => (uint)grade < (uint)LatencyGrades.Length ? LatencyGrades[grade] : Unknown;
        public static string LatencyGradeClass(int grade) => (uint)grade < (uint)LatencyGradeClasses.Length ? LatencyGradeClasses[grade] : "off";
        public static string LatencyStage(int stage) => (uint)stage < (uint)LatencyStages.Length ? LatencyStages[stage] : Unknown;

        //  지연 축(호스트 Telemetry/LatencyHistogram.cs 복제 — 엔진 참조 0 · 동기화는 호스트 테스트가 전 버킷 대조로 강제):
        //   버킷 0 = ns < 1,024 · 1..106 = 옥타브 2^10..2^23 마다 선형 8 등분 · 107 = 10,485,759ns 초과.
        //   허브가 싣는 백분위는 버킷 상한(또는 그 안의 관측 최대)이므로 ns → 버킷 → '미리 만든' 라벨 1개로 대응된다(조회 = 할당 0).
        public const int LatencyBuckets = 108;
        private const int LatencySub = 8, LatencyMinOctave = 10, LatencyMaxOctave = 23;
        public const long LatencyMaxTrackedNanos = ((LatencySub + 2L) << (LatencyMaxOctave - 3)) - 1;
        private static readonly string[] LatencyTexts = BuildLatencyTexts();

        /// <summary>ns → 지연 버킷(0 ~ 107).</summary>
        public static int LatencyBucket(long nanos)
        {
            if (nanos < (1L << LatencyMinOctave)) return 0;
            if (nanos > LatencyMaxTrackedNanos) return LatencyBuckets - 1;
            int msb = 63 - System.Numerics.BitOperations.LeadingZeroCount((ulong)nanos);
            int sub = (int)(nanos >> (msb - 3)) & (LatencySub - 1);
            return 1 + (msb - LatencyMinOctave) * LatencySub + sub;
        }

        /// <summary>버킷 상한(ns · 포함) — 넘침 버킷 = long.MaxValue.</summary>
        public static long LatencyBucketUpperNanos(int bucket)
        {
            if (bucket <= 0) return (1L << LatencyMinOctave) - 1;
            if (bucket >= LatencyBuckets - 1) return long.MaxValue;
            int o = (bucket - 1) / LatencySub, sub = (bucket - 1) % LatencySub;
            return ((LatencySub + sub + 1L) << (o + LatencyMinOctave - 3)) - 1;
        }

        /// <summary>지연(ns) → "≤16µs" · "≤1.2ms" 등 버킷 상한 라벨(정적 · 올림 = 보수적) · 0 이하 = "-" · 10.5ms 초과 = "&gt;10.5ms".</summary>
        public static string LatencyText(long nanos) => nanos <= 0 ? "-" : LatencyTexts[LatencyBucket(nanos)];

        private static string[] BuildLatencyTexts()
        {
            // 콜드 1회(정적 초기화) — 이후 조회는 배열 인덱싱만
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var t = new string[LatencyBuckets];
            for (int b = 0; b < LatencyBuckets - 1; b++)
            {
                long upper = LatencyBucketUpperNanos(b);
                long tenthsUs = (upper + 99) / 100;                                    // 0.1µs 단위 올림
                t[b] = tenthsUs < 100 ? "≤" + (tenthsUs / 10.0).ToString("0.0", inv) + "µs"
                     : tenthsUs < 10_000 ? "≤" + ((tenthsUs + 9) / 10).ToString(inv) + "µs"
                     : "≤" + (((upper + 99_999) / 100_000) / 10.0).ToString("0.0", inv) + "ms";
            }
            t[LatencyBuckets - 1] = ">" + ((LatencyMaxTrackedNanos + 99_999) / 100_000 / 10.0).ToString("0.0", inv) + "ms";
            return t;
        }
    }
}
