// ============================================================================
// CopilotEngineTests - 목표 파싱·상태 산출·제안 평가 (순수·결정적)
// ============================================================================
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class CopilotEngineTests
    {
        [Theory]
        [InlineData("변동성 15% 이하", PolicyKind.VolTarget, 15)]
        [InlineData("낙폭 10% 방어", PolicyKind.MaxDrawdown, 10)]
        [InlineData("MDD 12 넘으면 경고", PolicyKind.MaxDrawdown, 12)]
        [InlineData("레짐 급변 가드", PolicyKind.RegimeGuard, 0)]
        public void ParseGoal_RecognizesKindAndNumber(string nl, PolicyKind kind, double thr)
        {
            var p = CopilotEngine.ParseGoal(nl);
            Assert.NotNull(p);
            Assert.Equal(kind, p!.Kind);
            if (kind != PolicyKind.RegimeGuard) Assert.Equal(thr, p.Threshold, 1);
        }

        [Fact]
        public void ParseGoal_Unrecognized_Null()
            => Assert.Null(CopilotEngine.ParseGoal("그냥 아무 말"));

        [Fact]
        public void BuildCtx_ComputesDrawdownAndVol()
        {
            // 100→110(고점)→88 : 현재 낙폭 = (88-110)/110 = -20%
            var eq = new decimal[] { 100, 105, 110, 100, 88 }.ToList();
            var ctx = CopilotEngine.BuildCtx(eq);
            Assert.True(ctx.Valid);
            Assert.Equal(-20.0, ctx.DrawdownPct, 1);
            Assert.True(ctx.PortfolioVolPct > 0);
        }

        [Fact]
        public void BuildCtx_TooShort_Invalid()
            => Assert.False(CopilotEngine.BuildCtx(new decimal[] { 100, 101 }.ToList()).Valid);

        [Fact]
        public void Evaluate_VolTarget_FiresWhenExceeded()
        {
            var ctx = new CopilotEngine.Ctx { Valid = true, PortfolioVolPct = 25, DrawdownPct = -2 };
            var pol = new[] { new CopilotPolicy { Id = "v", Kind = PolicyKind.VolTarget, Threshold = 15, Enabled = true } };
            var props = CopilotEngine.Evaluate(pol, ctx);
            Assert.Single(props);
            Assert.Contains("변동성", props[0].Title);
            Assert.Equal("/portfolio/rebalance", props[0].ActionLink);
        }

        [Fact]
        public void Evaluate_MaxDrawdown_FiresWhenBreached_RespectsEnabled()
        {
            var ctx = new CopilotEngine.Ctx { Valid = true, PortfolioVolPct = 10, DrawdownPct = -15 };
            var on = new CopilotPolicy { Id = "d", Kind = PolicyKind.MaxDrawdown, Threshold = 10, Enabled = true };
            var off = new CopilotPolicy { Id = "d2", Kind = PolicyKind.MaxDrawdown, Threshold = 10, Enabled = false };
            Assert.Single(CopilotEngine.Evaluate(new[] { on }, ctx));        // -15 ≤ -10 → 발생
            Assert.Empty(CopilotEngine.Evaluate(new[] { off }, ctx));        // 비활성 → 제외
        }

        [Fact]
        public void Evaluate_NoBreach_NoProposals()
        {
            var ctx = new CopilotEngine.Ctx { Valid = true, PortfolioVolPct = 10, DrawdownPct = -3, Regime = RegimeKind.None };
            var pol = new[]
            {
                new CopilotPolicy { Kind = PolicyKind.VolTarget, Threshold = 15, Enabled = true },
                new CopilotPolicy { Kind = PolicyKind.MaxDrawdown, Threshold = 10, Enabled = true },
                new CopilotPolicy { Kind = PolicyKind.RegimeGuard, Enabled = true },
            };
            Assert.Empty(CopilotEngine.Evaluate(pol, ctx));
        }

        [Fact]
        public void Evaluate_InvalidCtx_Empty()
            => Assert.Empty(CopilotEngine.Evaluate(new[] { new CopilotPolicy { Kind = PolicyKind.VolTarget, Threshold = 1, Enabled = true } }, new CopilotEngine.Ctx { Valid = false }));
    }
}
