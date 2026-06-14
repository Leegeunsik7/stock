// ============================================================================
// NightlyReportTests - 야간 리포트 요약 텍스트 조립 (순수)
// ============================================================================
using System.Collections.Generic;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class NightlyReportTests
    {
        [Fact]
        public void ComposeText_IncludesHeadlineAndSections()
        {
            var d = new NightlyData
            {
                Date = "2026-06-14 (일)",
                Total = 12_340_000m, Pnl = 340_000m, RetPct = 2.8, Holdings = 5,
                Gainers = new() { ("삼성전자", 3.2), ("SK하이닉스", 1.1) },
                Losers = new() { ("카카오", -2.4) },
                OptimizerNote = "최고 삼성전자 MA5/20 (+18.0%)",
                ForecastNote = "커버리지 82%/90% · 편향 +0.3% (n40)",
                DebateNote = "강세 60%(5) · 약세 50%(4)",
                DriftNote = "타겟 저장됨"
            };
            var t = NightlyReport.ComposeText(d);
            Assert.Contains("야간 리포트", t);
            Assert.Contains("12,340,000", t);
            Assert.Contains("+2.8%", t);
            Assert.Contains("삼성전자", t);
            Assert.Contains("카카오", t);
            Assert.Contains("옵티마이저", t);
            Assert.Contains("토론승률", t);
            Assert.Contains("투자 권유가 아닙니다", t);
        }

        [Fact]
        public void ComposeText_OmitsEmptySections()
        {
            var d = new NightlyData { Date = "2026-06-14", Total = 1000m, Holdings = 1 };
            var t = NightlyReport.ComposeText(d);
            Assert.DoesNotContain("옵티마이저", t);   // 빈 섹션 생략
            Assert.DoesNotContain("토론승률", t);
            Assert.Contains("야간 리포트", t);
        }
    }
}
