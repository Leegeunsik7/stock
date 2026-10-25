// ============================================================================
// UpbitCandleSwarmLiveTests — v12.18.80-DEV: 업비트 공개 REST 실측(키 불필요) → 병렬 학습 스웜 → 420슬롯 매트릭스 Live 실데이터 판독
//  실행 조건(옵트인): MS2E_UPBIT_REST=1. 미충족 = Skip(실패 아님). 24/7 시장이라 시각 무관.
//  KRW-BTC · ETH · XRP 3종만 조회(GET 9회 · 초당 8회 이하 페이서) — 실 응답 파싱 · 봉 수 · 스프레드 · 브레인 판독 확인. 주문/인증 경로 0.
// ============================================================================
using System;
using System.Net.Http;
using System.Threading;
using StockManager.Engines.Cognitive.Screening;
using StockManager.Engines.Feeds.Crypto;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Integration
{
    public sealed class UpbitRestFactAttribute : FactAttribute
    {
        public const string OptInVar = "MS2E_UPBIT_REST";
        public static string? SkipReason(Func<string, string?> env) => env(OptInVar) == "1" ? null : OptInVar + " ≠ 1(업비트 공개 REST 실측 옵트인 없음) — 미접속";
        public UpbitRestFactAttribute() => Skip = SkipReason(Environment.GetEnvironmentVariable);
    }

    public sealed class UpbitCandleSwarmLiveTests
    {
        private readonly ITestOutputHelper _out;
        public UpbitCandleSwarmLiveTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void OptIn_SkipReason()
        {
            Assert.NotNull(UpbitRestFactAttribute.SkipReason(_ => null));
            Assert.Null(UpbitRestFactAttribute.SkipReason(k => k == UpbitRestFactAttribute.OptInVar ? "1" : null));
        }

        [UpbitRestFact]
        public void Live_Upbit_Rest_Fills_Matrix_With_Real_Readings()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var board = new PlutoUniverseMatrixBoard();
            var swarm = new PlutoParallelLearningSwarm(board, new PlutoMultiAssetDataLake(board), 100);
            swarm.Attach(new UpbitPublicCandleSource(http));
            var list = PlutoMultiAssetUniverseCatalog.Of(PlutoScreenMarket.Crypto);
            foreach (var code in new[] { "KRW-BTC", "KRW-ETH", "KRW-XRP" })
            {
                int idx = -1;
                for (int i = 0; i < list.Count; i++) if (list[i].Code == code) idx = i;
                var r = swarm.ProcessOne(PlutoScreenMarket.Crypto, idx, CancellationToken.None);
                Assert.Equal(PlutoFetchOutcome.Ok, r);
                Assert.True(board.TryRead(board.SlotOf(PlutoScreenMarket.Crypto, idx), out var c));
                var x = c.Inputs;
                _out.WriteLine(code + " bars=" + c.Bars + " price=" + PlutoDiscoveryScorer.PriceText(PlutoScreenMarket.Crypto, x.Price) + " spreadBps=" + x.SpreadBps
                    + " tech=" + x.HasTechnical + "(" + x.SqueezeScore + ") micro=" + x.HasMicro + "(cvd " + x.CvdScore + " depth " + x.DepthScore + " tiers " + x.DepthTiers + ")"
                    + " total=" + PlutoDiscoveryScorer.Score(in x, default).Total);
                Assert.True(c.IsLive);
                Assert.True(c.Bars > 20);
                Assert.True(x.SpreadBps >= 0);
                Assert.True(x.HasMicro);
                Assert.True(x.DepthTiers > 1);
            }
            Assert.Equal(3, swarm.StatusOf(PlutoScreenMarket.Crypto).Live);
        }
    }
}
