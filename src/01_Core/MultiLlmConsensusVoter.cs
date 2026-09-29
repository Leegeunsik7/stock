// ============================================================================
// MultiLlmConsensusVoter — v12.7.0 Multi-LLM Consensus Voter & Advisory Safety Gate
//
//   원격(Gemini 등) 및 로컬(Ollama/ONNX) 모델이 각각 산출한 방향성 시그널을 교차 검증한다. ★"원자적으로"란
//   본 함수가 순수(pure)·무상태이기 때문에 호출 자체가 곧 원자적 전(all-or-nothing) 판정이라는 뜻이다 — 부분
//   집계 상태가 관측될 수 없다(락/CAS 불필요, 함수 반환이 곧 원자 스냅샷).
//
//   ★정직 회계: 이 저장소의 LlmRouter(v3.34.0)는 설정에 따라 Gemini 또는 Ollama '중 하나'만 선택해 호출한다
//   (동시 다중 백엔드 질의는 아직 배선되어 있지 않음 — 확인됨). 따라서 본 투표기는 N개의 이미 산출된 방향성
//   신호(어디서 왔든)를 합성하는 순수 함수로 제공하며, 실제로 몇 개의 백엔드가 동시에 질의되는지는 상위
//   호출자(AiRecommendationAdapter 의 선택적 delegate)에게 달려 있다(날조 없음 — 현재는 1개 신호로도 정상 동작).
//
//   불변: 결정론(순수 함수) · δ=0(Consensus 힙 0 — ReadOnlySpan 순회) · fail-closed(모델 간 방향 불일치 →
//        Neutral/Hold 하향 — 확신 없는 신호를 사출하지 않는다) · liveArmed:false(투표는 등급 산출 보조일 뿐).
// ============================================================================
using System;

namespace MS2E.Core
{
    /// <summary>단일 모델의 방향성 투표.</summary>
    public enum LlmVote : sbyte { Bearish = -1, Neutral = 0, Bullish = 1 }

    public static class MultiLlmConsensusVoter
    {
        /// <summary>
        /// N개 모델 투표를 합성(δ=0·순수). 전원(또는 유일 투표)이 같은 방향이면 그 방향 유지. Bullish 와 Bearish
        /// 가 동시에 존재(불일치)하면 ★Neutral(Hold 하향)로 강등 — 확신 없는 상충 신호를 사출하지 않는다.
        /// 투표가 없으면(빈 span) Neutral.
        /// </summary>
        public static LlmVote Consensus(ReadOnlySpan<LlmVote> votes)
        {
            bool anyBullish = false, anyBearish = false;
            foreach (var v in votes)
            {
                if (v == LlmVote.Bullish) anyBullish = true;
                else if (v == LlmVote.Bearish) anyBearish = true;
            }
            if (anyBullish && anyBearish) return LlmVote.Neutral;   // ★상충 → Hold 하향
            if (anyBullish) return LlmVote.Bullish;
            if (anyBearish) return LlmVote.Bearish;
            return LlmVote.Neutral;
        }

        /// <summary>투표 간 만장일치 여부(관측용 — Consensus 의 부수 정보). 빈 span 은 true(공허하게 참).</summary>
        public static bool IsUnanimous(ReadOnlySpan<LlmVote> votes)
        {
            if (votes.Length == 0) return true;
            LlmVote first = votes[0];
            foreach (var v in votes) if (v != first) return false;
            return true;
        }
    }
}
