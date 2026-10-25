// ============================================================================
// PlutoRecommendationSnapshot — v12.18.78-DEV: 시장별 Top 5 추천 불변 스냅샷 + 원자 참조 교체 보드(δ=0 판독)
//
//   스크리너(Track B · 단일 라이터) 가 주기마다 새 스냅샷을 통째로 만든 뒤 PlutoRecommendationBoard.Publish(Volatile.Write 참조 1회)로 교체.
//   판독자(UI 어댑터 · 체결 핫패스) = Volatile.Read 1회 → 불변 배열 판독 — 잠금 0 · 찢어진 판독 0(게시 후 스냅샷은 절대 변이 없음).
//   TryFind(시장 · 종목) = 최대 5칸 선형 탐색 · 값 out — 힙 할당 0B(아레나 체결 루프 내 조회 허용).
//   [v12.18.79-DEV] 가상자산(업비트) 슬롯 추가(3대 자산) · 시장별 감시 종목 수(카탈로그) · 통과 종목 수 → 전체 모니터링 통계(TotalMonitored/TotalScanned/TotalPassed)를
//    생성자에서 정수로 미리 합산(판독 = 필드 읽기 · δ=0).
//   ★판독 전용 — 주문 · 무장 · 브로커 경로 0(liveArmed:false).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace StockManager.Engines.Cognitive.Screening
{
    /// <summary>추천 1건(불변). Code = KRX 6자리 코드 · US 티커 · 업비트 마켓("KRW-BTC") · Price/FairPrice = KRX 원 · US 센트 · 코인 원 × 10^8 raw ·
    /// RecommendedQty = 주(코인 = 코인 × 10^8 raw) · Reasons = 핵심 근거 3줄.</summary>
    public sealed record PlutoRecommendation(
        PlutoScreenMarket Market, int Rank, long Symbol, string Code, long Price, PlutoDiscoveryScore Score,
        IReadOnlyList<string> Reasons, bool HasValuation, int MarginOfSafetyBps, long FairPrice, bool SeedProfile,
        long RecommendedQty, string RiskText);

    /// <summary>시장별 스캔 집계 — Scanned = 피드 판독 종목 · Vetoed = 반대 논리 ≥ 2 탈락 · Insufficient = 판독 전무 · BelowThreshold = 50점 미만 ·
    /// [v12.18.79-DEV] Monitored = 감시 카탈로그 종목 수(피드 판독 대기 포함) · Passed = 추천 기준 통과(Top 5 절단 전).</summary>
    public readonly record struct PlutoScreenTally(int Scanned, int Vetoed, int Insufficient, int BelowThreshold, int Monitored = 0, int Passed = 0);

    public sealed class PlutoRecommendationSnapshot
    {
        public const int TopN = 5, Markets = 3;

        public static readonly PlutoRecommendationSnapshot Empty = new(0, 0,
            Array.Empty<PlutoRecommendation>(), default, Array.Empty<PlutoRecommendation>(), default);

        private readonly PlutoRecommendation[] _krx, _us, _crypto;
        private readonly ReadOnlyCollection<PlutoRecommendation> _krxView, _usView, _cryptoView;
        private readonly PlutoScreenTally _krxTally, _usTally, _cryptoTally;

        public PlutoRecommendationSnapshot(long generation, long updatedUtcTicks,
            PlutoRecommendation[] krx, PlutoScreenTally krxTally, PlutoRecommendation[] us, PlutoScreenTally usTally)
            : this(generation, updatedUtcTicks, krx, krxTally, us, usTally, Array.Empty<PlutoRecommendation>(), default) { }

        public PlutoRecommendationSnapshot(long generation, long updatedUtcTicks,
            PlutoRecommendation[] krx, PlutoScreenTally krxTally, PlutoRecommendation[] us, PlutoScreenTally usTally,
            PlutoRecommendation[] crypto, PlutoScreenTally cryptoTally)
        {
            ArgumentNullException.ThrowIfNull(krx);
            ArgumentNullException.ThrowIfNull(us);
            ArgumentNullException.ThrowIfNull(crypto);
            if (krx.Length > TopN || us.Length > TopN || crypto.Length > TopN) throw new ArgumentOutOfRangeException(nameof(krx), "시장별 최대 5건");
            Generation = generation;
            UpdatedUtcTicks = updatedUtcTicks;
            _krx = (PlutoRecommendation[])krx.Clone();   // 방어 복사 — 게시 후 호출자 배열 변이가 스냅샷을 찢지 못하게
            _us = (PlutoRecommendation[])us.Clone();
            _crypto = (PlutoRecommendation[])crypto.Clone();
            _krxView = Array.AsReadOnly(_krx);
            _usView = Array.AsReadOnly(_us);
            _cryptoView = Array.AsReadOnly(_crypto);
            _krxTally = krxTally;
            _usTally = usTally;
            _cryptoTally = cryptoTally;
            TotalMonitored = krxTally.Monitored + usTally.Monitored + cryptoTally.Monitored;
            TotalScanned = krxTally.Scanned + usTally.Scanned + cryptoTally.Scanned;
            TotalPassed = krxTally.Passed + usTally.Passed + cryptoTally.Passed;
        }

        public long Generation { get; }
        public long UpdatedUtcTicks { get; }
        /// <summary>[v12.18.79-DEV] 3대 시장 감시 카탈로그 합계 · 피드 판독 합계 · 추천 기준 통과 합계(생성 시 합산 · 판독 δ=0).</summary>
        public int TotalMonitored { get; }
        public int TotalScanned { get; }
        public int TotalPassed { get; }

        public IReadOnlyList<PlutoRecommendation> Of(PlutoScreenMarket m) => m switch
        {
            PlutoScreenMarket.Us => _usView,
            PlutoScreenMarket.Crypto => _cryptoView,
            _ => _krxView,
        };

        public PlutoScreenTally TallyOf(PlutoScreenMarket m) => m switch
        {
            PlutoScreenMarket.Us => _usTally,
            PlutoScreenMarket.Crypto => _cryptoTally,
            _ => _krxTally,
        };

        /// <summary>추천 목록 내 종목 조회(δ=0). 없음 = false · score/rank 0.</summary>
        public bool TryFind(PlutoScreenMarket m, long symbol, out int score, out int rank)
        {
            var arr = m switch { PlutoScreenMarket.Us => _us, PlutoScreenMarket.Crypto => _crypto, _ => _krx };
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
