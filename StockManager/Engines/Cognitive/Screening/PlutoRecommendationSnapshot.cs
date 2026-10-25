// ============================================================================
// PlutoRecommendationSnapshot — v12.18.78-DEV: 시장별 Top 5 추천 불변 스냅샷 + 원자 참조 교체 보드(δ=0 판독)
//
//   스크리너(Track B · 단일 라이터) 가 주기마다 새 스냅샷을 통째로 만든 뒤 PlutoRecommendationBoard.Publish(Volatile.Write 참조 1회)로 교체.
//   판독자(UI 어댑터 · 체결 핫패스) = Volatile.Read 1회 → 불변 배열 판독 — 잠금 0 · 찢어진 판독 0(게시 후 스냅샷은 절대 변이 없음).
//   TryFind(시장 · 종목) = 최대 5칸 선형 탐색 · 값 out — 힙 할당 0B(아레나 체결 루프 내 조회 허용).
//   ★판독 전용 — 주문 · 무장 · 브로커 경로 0(liveArmed:false).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace StockManager.Engines.Cognitive.Screening
{
    /// <summary>추천 1건(불변). Code = KRX 6자리 코드 · US 티커 · Price/FairPrice = KRX 원 · US 센트 · Reasons = 핵심 근거 3줄.</summary>
    public sealed record PlutoRecommendation(
        PlutoScreenMarket Market, int Rank, long Symbol, string Code, long Price, PlutoDiscoveryScore Score,
        IReadOnlyList<string> Reasons, bool HasValuation, int MarginOfSafetyBps, long FairPrice, bool SeedProfile,
        long RecommendedQty, string RiskText);

    /// <summary>시장별 스캔 집계 — Scanned = 유니버스 종목 · Vetoed = 반대 논리 ≥ 2 탈락 · Insufficient = 판독 전무 · BelowThreshold = 50점 미만.</summary>
    public readonly record struct PlutoScreenTally(int Scanned, int Vetoed, int Insufficient, int BelowThreshold);

    public sealed class PlutoRecommendationSnapshot
    {
        public const int TopN = 5, Markets = 2;

        public static readonly PlutoRecommendationSnapshot Empty = new(0, 0,
            Array.Empty<PlutoRecommendation>(), default, Array.Empty<PlutoRecommendation>(), default);

        private readonly PlutoRecommendation[] _krx, _us;
        private readonly ReadOnlyCollection<PlutoRecommendation> _krxView, _usView;
        private readonly PlutoScreenTally _krxTally, _usTally;

        public PlutoRecommendationSnapshot(long generation, long updatedUtcTicks,
            PlutoRecommendation[] krx, PlutoScreenTally krxTally, PlutoRecommendation[] us, PlutoScreenTally usTally)
        {
            ArgumentNullException.ThrowIfNull(krx);
            ArgumentNullException.ThrowIfNull(us);
            if (krx.Length > TopN || us.Length > TopN) throw new ArgumentOutOfRangeException(nameof(krx), "시장별 최대 5건");
            Generation = generation;
            UpdatedUtcTicks = updatedUtcTicks;
            _krx = (PlutoRecommendation[])krx.Clone();   // 방어 복사 — 게시 후 호출자 배열 변이가 스냅샷을 찢지 못하게
            _us = (PlutoRecommendation[])us.Clone();
            _krxView = Array.AsReadOnly(_krx);
            _usView = Array.AsReadOnly(_us);
            _krxTally = krxTally;
            _usTally = usTally;
        }

        public long Generation { get; }
        public long UpdatedUtcTicks { get; }

        public IReadOnlyList<PlutoRecommendation> Of(PlutoScreenMarket m) => m == PlutoScreenMarket.Us ? _usView : _krxView;

        public PlutoScreenTally TallyOf(PlutoScreenMarket m) => m == PlutoScreenMarket.Us ? _usTally : _krxTally;

        /// <summary>추천 목록 내 종목 조회(δ=0). 없음 = false · score/rank 0.</summary>
        public bool TryFind(PlutoScreenMarket m, long symbol, out int score, out int rank)
        {
            var arr = m == PlutoScreenMarket.Us ? _us : _krx;
            for (int i = 0; i < arr.Length; i++)
            {
                var r = arr[i];
                if (r.Symbol == symbol) { score = r.Score.Total; rank = r.Rank; return true; }
            }
            score = 0; rank = 0;
            return false;
        }
    }

    /// <summary>추천 스냅샷 게시판(단일 라이터 · 다중 판독 · 원자 참조 교체).</summary>
    public sealed class PlutoRecommendationBoard
    {
        private PlutoRecommendationSnapshot _current = PlutoRecommendationSnapshot.Empty;
        private long _published;

        public PlutoRecommendationSnapshot Current => Volatile.Read(ref _current);
        public long Published => Interlocked.Read(ref _published);

        public void Publish(PlutoRecommendationSnapshot snapshot)
        {
            Volatile.Write(ref _current, snapshot ?? throw new ArgumentNullException(nameof(snapshot)));
            Interlocked.Increment(ref _published);
        }

        /// <summary>체결 핫패스 조회(δ=0 · 잠금 0).</summary>
        public bool TryFind(PlutoScreenMarket m, long symbol, out int score, out int rank) => Volatile.Read(ref _current).TryFind(m, symbol, out score, out rank);
    }
}
