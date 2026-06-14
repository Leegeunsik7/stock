// ============================================================================
// PortfolioData - 포트폴리오 허브 공통 컨텍스트 (IA 개편 1단계, v3.29.0)
//  HubPage가 OnInitializedAsync에서 1회 로드해 CascadingValue로 자식 탭에 내려준다.
//  자식(리밸런싱·리스크 분해)은 이 안의 R/Names/Weights만 소비 → 탭 전환 시 재수집 0.
//  ⚠️ 새 로드마다 '새 인스턴스'를 할당해야 CascadingValue가 자식에 변경을 전파한다.
// ============================================================================
using System;
using System.Collections.Generic;
using StockManager.Common;

namespace StockManager.Blazor
{
    public class PortfolioData
    {
        public bool Loaded { get; set; }
        public string Error { get; set; } = "";
        public int Days { get; set; } = 180;
        public decimal UsdKrw { get; set; } = 1380m;

        public List<Holding> Used { get; set; } = new();         // 수익률 ≥2 보유 종목
        public double[][] R { get; set; } = Array.Empty<double[]>(); // 일수익률 행렬(%)
        public string[] Names { get; set; } = Array.Empty<string>();
        public double[] Weights { get; set; } = Array.Empty<double>(); // 시총 비중(합 1)
        public double Total { get; set; }                         // 총 평가액(KRW)
    }
}
