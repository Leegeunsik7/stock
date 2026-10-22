// ============================================================================
// CryptoContinuousSession — [v12.18.54-DEV] 가상자산(업비트 KRW 마켓) 세션 판정(순수 · 무상태 · δ=0 · 24/7/365)
//
//  가상자산 현물 시장은 휴장일 · 개장/폐장 시각 · 서머타임이 없다 → 유효 시각이면 언제나 ContinuousTrading.
//   KrxSessionStateMachine / UsSessionStateMachine 과 같은 진입점 모양(UTC 마이크로초)을 맞춰 소비자가 시장 종류만 바꿔 끼우게 한다.
//  fail-closed: 시각 ≤0(미초기화 · 시계 결함) = Invalid — '언제나 개장' 이 '시각을 몰라도 개장' 이 되지 않게 한다.
//  ★거래소 정기/긴급 점검(입출금 · 매매 일시 중단)은 공지 기반 비정기 이벤트라 이 판정기 범위 밖 — 호가 공백/정체는 피드 측 감시가 막는다.
// ============================================================================
namespace StockManager.Engines.Feeds
{
    public enum CryptoSessionPhase : byte
    {
        Invalid = 0,
        ContinuousTrading = 3,      // UsSessionPhase.ContinuousTrading 과 같은 값(로그 · 대시보드 일관)
    }

    public static class CryptoContinuousSession
    {
        /// <summary>UTC 유닉스 마이크로초 기준 페이즈. ≤0 = Invalid(fail-closed) · 그 외 = ContinuousTrading.</summary>
        public static CryptoSessionPhase PhaseAtUtcMicros(long utcMicros)
            => utcMicros > 0 ? CryptoSessionPhase.ContinuousTrading : CryptoSessionPhase.Invalid;

        /// <summary>주문 후보 허용 여부(연속매매 페이즈만 true).</summary>
        public static bool IsContinuousTradingUtcMicros(long utcMicros)
            => PhaseAtUtcMicros(utcMicros) == CryptoSessionPhase.ContinuousTrading;
    }
}
