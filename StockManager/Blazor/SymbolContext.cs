// ============================================================================
// SymbolContext - 전역 "활성 종목" 컨텍스트 (IA 개편, v3.29.0)
//  한 번 정한 종목을 연구 도구들(옵티마이저·이벤트스터디·가설발굴·사이저·트윈·예측)이
//  공유한다 — 도구를 옮길 때마다 종목을 다시 입력하는 내비 피로를 없앤다.
//  싱글톤 등록(앱 단일 사용자). 변경 시 Changed 이벤트로 구독자(SymbolBar 등) 갱신.
// ============================================================================
using System;
using StockManager.Common;

namespace StockManager.Blazor
{
    public class SymbolContext
    {
        public string Symbol { get; private set; } = "";
        public Market Market { get; private set; } = Market.KRX_KOSPI;
        public bool HasSymbol => !string.IsNullOrWhiteSpace(Symbol);

        public event Action? Changed;

        /// <summary>활성 종목 설정. 연구 페이지는 이 값을 입력 기본값으로 시드한다.</summary>
        public void Set(string symbol, Market market)
        {
            var s = (symbol ?? "").Trim().Split(' ')[0];
            if (s == Symbol && market == Market) return;
            Symbol = s; Market = market;
            Changed?.Invoke();
        }
    }
}
