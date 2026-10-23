// ============================================================================
// ArenaCopyTradingPort — v12.18.71-DEV: 플루토 카피 트레이딩 · 시초가 개장 게이트 UI 경계(순수 DTO · 엔진 타입 참조 0)
//
//   3대 아레나 포트(IKrxArenaPort · IUsArenaPort · ICryptoArenaPort)가 같은 DTO 로 복제 API 를 노출한다:
//     FollowAiTrade(rationaleSeq, copyRatio) = AI 근거 1건을 휴먼 가상 원장으로 1-클릭 복제(접수 단계 결과 · 체결은 다음 정산 회차).
//     SetAutoCopyEnabled(enabled, ratio) = AI 체결 즉시 같은 정산 창에서 휴먼 원장 자동 미러링(+ AI 청산 시 복제분 동반 청산).
//     CopyTrading = 자동 미러링 상태 · 복제 계수 · 마지막 복제 결과(토스트).
//   ★안전 거버넌스: 슬리피지 20bp 초과 = 복제 취소 · 예수금 부족 = Fail-Closed(축소 복제 없음) · AI 청산 = 복제분 동반 청산(자동 모드).
//   ★liveArmed:false — 복제 목적지 = 휴먼 '가상' 원장뿐. 브로커 · 무장 · 실주문 메서드 없음. 기본 구현 = 미지원(가짜 복제 0).
// ============================================================================
using System;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>복제 결과 코드(접수 · 체결 단계 공용 · 0 = 성공).</summary>
    public static class ArenaCopyCode
    {
        public const int None = 0, NotFound = 1, Invalid = 2, ZeroQty = 3, SlippageExceeded = 4, InsufficientCash = 5,
                         InsufficientPosition = 6, SessionClosed = 7, NoQuote = 8, NoLiquidity = 9, QueueFull = 10, Unsupported = 11, Rejected = 12;

        public static string Label(int c) => c switch
        {
            None => "복제 성공",
            NotFound => "AI 체결 근거 만료(최근 근거 링 밖)",
            Invalid => "복제 값 오류(배율 0.1x~3.0x)",
            ZeroQty => "비례 수량 0(자본 대비 너무 작음)",
            SlippageExceeded => "슬리피지 20bp 초과 — 복제 취소",
            InsufficientCash => "예수금 부족 — 복제 거부(Fail-Closed)",
            InsufficientPosition => "보유 수량 없음 — 따라팔기 불가",
            SessionClosed => "체결 가능 세션 아님",
            NoQuote => "호가 미수신",
            NoLiquidity => "호가 잔량 없음",
            QueueFull => "주문 큐 포화 — 폐기(재시도 없음)",
            Unsupported => "이 아레나는 카피 트레이딩 미지원",
            _ => "복제 거부",
        };
    }

    /// <summary>복제 접수 결과(Qty = 비례 환산 수량 · 가격 단위 = 아레나 원장 단위).</summary>
    public readonly record struct ArenaCopyResult(int Code, int Side, long Qty, long RationaleSeq)
    {
        public bool Accepted => Code == ArenaCopyCode.None;
        public string Label => ArenaCopyCode.Label(Code);
    }

    /// <summary>자동 미러링 상태 · 복제 계수(LastEvent = 마지막 결과 갱신 회차 · 0 = 이력 없음 · LastSymbol = 표시 코드).</summary>
    public readonly record struct ArenaCopyTradingView(
        bool Supported, bool AutoEnabled, int RatioBp, long Mirrored, long Followed, long CompanionCloses, long SlippageCancels, long CashRejects,
        long OtherRejects, long LastEvent, int LastKind, int LastCode, int LastSide, long LastQty, string? LastSymbol)
    {
        public const int KindFollow = 0, KindMirror = 1, KindCompanion = 2;
        public string RatioLabel => (RatioBp / 10_000m).ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + "x";
        public string LastKindLabel => LastKind switch { KindMirror => "자동 미러링", KindCompanion => "동반 청산", _ => "따라하기" };
    }

    /// <summary>시초가 개장 갭 판정(UI 분류).</summary>
    public static class ArenaOpenGapKind
    {
        public const int None = 0, Flat = 1, ChaseVeto = 2, GapFillLong = 3, PanicVeto = 4;
        public static string Label(int k) => k switch
        {
            Flat => "갭 미미",
            ChaseVeto => "갭 상승 추격 금지(악마의 변호인)",
            GapFillLong => "갭 하락 메우기 후보",
            PanicVeto => "패닉 갭 — 신규 진입 차단",
            _ => "전일 종가 없음",
        };
        public static string Css(int k) => k switch { ChaseVeto => "chase", GapFillLong => "fill", PanicVeto => "panic", Flat => "flat", _ => "none" };
    }

    /// <summary>종목 1개 개장 갭(Code = 표시 코드 · 가격 = 원장 단위 · InWindow = 개장 직후 판정 창).</summary>
    public readonly record struct ArenaOpenGapView(string Code, long PrevClose, long OpenPrice, int GapBp, int Kind, bool InWindow)
    {
        public string KindLabel => ArenaOpenGapKind.Label(Kind);
    }

    /// <summary>개장 게이트 요약(Supported=false = 24/7 시장 · 미결선).</summary>
    public readonly record struct ArenaOpeningAuctionView(bool Supported, bool PreOpen, int Parked, long AuctionFills, long AuctionRejects, long GapVetoes, long GapFillEntries);
}
