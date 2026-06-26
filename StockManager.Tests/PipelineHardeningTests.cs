// ============================================================================
// PipelineHardeningTests — v4.67.0 풀 파이프라인 하드닝
//
//  Mission C: ShadowClearingBroker ↔ ShadowCutoverGate ↔ PaperBroker 엔드-투-엔드 결속 검증.
//  두 핵심 불변식:
//   1. QFixedPrimary 배선 + 등록 심볼 → 정렬값으로 실주문 변환·체결(ok=true, liveAtt 무동결).
//   2. QFixedPrimary 배선 + 미등록 심볼 → fail-closed(ok=false), liveAtt EmergencyFreeze,
//      inner.Place 미호출 — v4.67.0 Mission B 가드가 예외를 (false,msg)로 변환(UI 크래시 방지).
//
//  정직 표기:
//   · PaperBroker.ResetSeed()는 메모리 내 PaperAccount를 직접 리셋하는 테스트 전용 메서드.
//     프로덕션 시딩은 OnboardingImporter.SeedVirtualCash() (DB 원자 커밋).
//   · QTickRegistry 생성 비용은 테스트 실행당 1회 — 성능 민감 경로 아님.
// ============================================================================
using System;
using System.IO;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class PipelineHardeningTests : IDisposable
    {
        private readonly string          _dbPath;
        private readonly LocalDatabase   _db;
        private readonly PaperBroker     _paper;

        public PipelineHardeningTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"pipe_harden_{Guid.NewGuid():N}.db");
            _db     = new LocalDatabase(_dbPath);
            _paper  = new PaperBroker(_db);
            _paper.ResetSeed(10_000_000m);   // 1,000만원 — 매수 증거금 충분
        }

        public void Dispose()
        {
            _db.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }

        // KRW 정수단가 코스피 spec: tick=1원·step=1주.
        private static QTickRegistry Registry() => new(new (string, QTickSpec)[]
        {
            ("005930", new QTickSpec(QPrice.Of(1m), QQty.Of(1m))),   // 삼성전자 등록
        });

        // ══════════════════════════════════════════════════════════════════════
        // 1. QFixedPrimary 배선 + 등록 심볼 → 실주문 정렬·체결(ok=true)
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void FullPipeline_WhenWiredAndQFixedPrimary_MutatesAndExecutesRealOrder()
        {
            // Arrange — 전용 섀도우 att(gate 내부용)·라이브 att(ShadowClearingBroker 동결 에스컬레이션용) 분리.
            //   공유하면 섀도우 발산 ForceFreeze가 라이브 패닉을 오염(footgun).
            var shadowAtt = new OrderAttestationService();
            var liveAtt   = new OrderAttestationService();
            var gate      = new ShadowCutoverGate(enabled: true, Registry(), shadowAtt);
            gate.SetAuthority(ClearingAuthority.QFixedPrimary);   // ★QFixed 집권 — inner 치환 모드

            var sut = new ShadowClearingBroker(_paper, gate, liveAtt);

            // Act — 등록 심볼(005930) 시장가 매수 10주 @70,000원
            var (ok, msg) = sut.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Market, 10m, 0m, 70_000m);

            // Assert — ① 체결 성공 ② 보유 수량 기록(inner 호출 증거) ③ 라이브 att 무동결
            Assert.True(ok, $"QFixed 등록 심볼 매수 실패: {msg}");
            Assert.True(_paper.HeldQty(PaperAsset.Stock, "005930") > 0m,
                "inner.Place 미호출 — 보유 수량 없음");
            Assert.NotEqual(GatingMode.EmergencyFreeze, liveAtt.Mode);
        }

        // ══════════════════════════════════════════════════════════════════════
        // 2. QFixedPrimary 배선 + 미등록 심볼 → fail-closed 하드 동결·다운스트림 차단
        //    (v4.67.0 Mission B — 가드가 InvalidOperationException을 (false,msg)로 변환해 UI 크래시 방지)
        // ══════════════════════════════════════════════════════════════════════

        [Fact]
        public void FullPipeline_WhenClearingFails_EnforcesHardFreezeAndBlocksDownstream()
        {
            // Arrange
            var shadowAtt = new OrderAttestationService();
            var liveAtt   = new OrderAttestationService();
            var gate      = new ShadowCutoverGate(enabled: true, Registry(), shadowAtt);
            gate.SetAuthority(ClearingAuthority.QFixedPrimary);

            var sut = new ShadowClearingBroker(_paper, gate, liveAtt);

            // Act — 미등록 심볼(격자 없음) → TryClearAligned false → FailClosed → InvalidOperationException
            //   Mission B 가드: 예외가 IBroker.Place 경계에서 포착되어 (false,msg)로 반환 — no throw.
            var (ok, msg) = sut.Place(PaperAsset.Stock, "GHOST999", "미등록",
                PaperSide.Buy, PaperKind.Market, 1m, 0m, 10_000m);

            // Assert ① ok=false(다운스트림 차단) ② fail-closed 메시지 포함 ③ liveAtt 동결(하드 단절)
            //        ④ inner.Place 미호출(보유 0 유지)
            Assert.False(ok, "미등록 심볼 주문이 ok=true로 통과됨 — fail-closed 가드 누락");
            Assert.Contains("CLEARING FAIL-CLOSED", msg, StringComparison.Ordinal);
            Assert.Equal(GatingMode.EmergencyFreeze, liveAtt.Mode);
            Assert.Equal(0m, _paper.HeldQty(PaperAsset.Stock, "GHOST999"));
        }
    }
}
