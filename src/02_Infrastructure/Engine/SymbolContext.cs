// ============================================================================
// SymbolContext - 전역 "활성 종목" 경량 상태 (IA 개편 3단계, v3.32.0)
//  현재 분석 중인 종목(코드·이름·시장)을 앱 전역에 들고 있어, 도구를 옮겨도
//  분석 흐름이 끊기지 않게 한다. 변경 시 OnChange로 구독자(상단 바·열린 페이지) 갱신.
//  싱글톤 등록(앱 단일 사용자). 외부 의존 0.
// ============================================================================
using System;

namespace StockManager.Common
{
    public class SymbolContext
    {
        public string Symbol { get; private set; } = "";
        public string Name { get; private set; } = "";
        public Market Market { get; private set; } = Market.KRX_KOSPI;
        public bool HasSymbol => !string.IsNullOrWhiteSpace(Symbol);

        /// <summary>종목이 바뀌면 발생 — UI 구독자가 다시 그린다.</summary>
        public event Action? OnChange;

        /// <summary>활성 종목 설정. 동일 값이면 이벤트를 발생시키지 않는다(재진입·루프 방지).</summary>
        public void Set(string symbol, Market market, string? name = null)
        {
            var s = (symbol ?? "").Trim().Split(' ')[0];
            var nm = name ?? Name;
            if (s == Symbol && market == Market && nm == Name) return;
            Symbol = s; Market = market; Name = nm ?? "";
            OnChange?.Invoke();
        }
    }
}
