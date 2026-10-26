// ============================================================================
// PlutoBriefingService — v12.18.85-DEV: 플루토 인지 학습 브리핑 서비스(호스트 · 콜드 · 판독 전용)
//
//   두 UI 포트(IPlutoLearningPort · IPlutoRecommendationPort) 판독 → PlutoBriefingComposer 합성(RCL 모달과 동일 규칙 · 단일 원천).
//   호출 = UI 요청 · CLI(--pluto-briefing) 온디맨드만 — 체결 핫패스 · 스크리너 루프 무간섭(포트 판독 = 불변 스냅샷 참조 읽기).
//   ★liveArmed:false — 주문 · 무장 · 브로커 경로 0 · QuantumVault/피어 접근 0.
// ============================================================================
using System;
using System.Collections.Generic;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Adapters
{
    public sealed class PlutoBriefingService
    {
        private readonly IPlutoLearningPort _learning;
        private readonly IPlutoRecommendationPort _reco;

        public PlutoBriefingService(IPlutoLearningPort learning, IPlutoRecommendationPort reco)
        {
            _learning = learning ?? throw new ArgumentNullException(nameof(learning));
            _reco = reco ?? throw new ArgumentNullException(nameof(reco));
        }

        public PlutoBriefingReport Build(int topN = PlutoBriefingComposer.DefaultTopN)
        {
            bool lw = _learning.IsWired, rw = _reco.IsWired;
            var lv = lw ? _learning.Current : default;
            return PlutoBriefingComposer.Compose(lw, in lv, rw, rw ? _reco.Current : PlutoRecoBoardView.None, topN);
        }

        public IReadOnlyList<string> Render(int topN = PlutoBriefingComposer.DefaultTopN) => Build(topN).Lines();
    }
}
