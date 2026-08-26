// ============================================================================
// AlgoModels — S15 공용 모델
//   AlgoOrder / AlgoExecution / AlgoConfig  : TWAP·VWAP 분할 집행
//   FailoverEvent                           : 크로스 브로커 폴오버
//   SlippageAlert                           : 슬리피지 편차 경보
// ============================================================================
using System;

namespace MS2E.Infrastructure
{
    public enum AlgoStyle { Twap, Vwap }

    public enum AlgoState { Pending, Running, Completed, Cancelled, Frozen }

    /// <summary>분할 집행 전략 설정. TwapVwapExecutor.DefaultConfig 에 할당.</summary>
    public sealed class AlgoConfig
    {
        public AlgoStyle Style    { get; init; } = AlgoStyle.Twap;
        public TimeSpan  Duration { get; init; } = TimeSpan.FromMinutes(10);
        public int       Slices   { get; init; } = 10;
        /// <summary>이 임계치 이상의 주문만 분할 집행. 미만은 즉시 통과.</summary>
        public decimal   MinQty   { get; init; } = 100m;
    }

    /// <summary>분할 집행 모 주문. Place() 호출 시 내부적으로 생성.</summary>
    public sealed class AlgoOrder
    {
        public long       AlgoId     { get; init; }
        public string     Symbol     { get; init; } = "";
        public string     Name       { get; init; } = "";
        public PaperAsset Asset      { get; init; } = PaperAsset.Stock;
        public PaperSide  Side       { get; init; }
        public PaperKind  Kind       { get; init; }
        public decimal    TotalQty   { get; init; }
        public decimal    LimitPrice { get; init; }  // 0 = 시장가
        public AlgoStyle  Style      { get; init; } = AlgoStyle.Twap;
        public TimeSpan   Duration   { get; init; } = TimeSpan.FromMinutes(10);
        public int        Slices     { get; init; } = 10;
        public long       GenomeId   { get; init; }
    }

    /// <summary>실행 중인 알고리즘 집행 상태. 이벤트로 외부에 노출.</summary>
    public sealed class AlgoExecution
    {
        public long      AlgoId             { get; init; }
        public AlgoOrder Order              { get; init; } = null!;
        public AlgoState State              { get; set;  } = AlgoState.Pending;
        public decimal   FilledQty          { get; set;  }
        public double    WeightedPriceAccum { get; set;  } // Σ(price × qty) — 가중평균가 산출용
        public int       ChildrenFired      { get; set;  }
        public DateTime  StartedAt          { get; init; }

        /// <summary>체결된 수량의 가중 평균 집행가.</summary>
        public double AvgFillPrice
            => FilledQty > 0 ? WeightedPriceAccum / (double)FilledQty : 0.0;
    }

    /// <summary>MultiBrokerRouter 폴백 전환 이벤트.</summary>
    public sealed record FailoverEvent(
        string   Reason,
        string   PrimaryName,
        string   FallbackName,
        DateTime OccurredAt);

    /// <summary>SlippageSentinel 경보 페이로드.</summary>
    public sealed record SlippageAlert(
        string   Symbol,
        double   ZScore,
        double   Mean,
        double   Sigma,
        int      ConsecutiveExceedances,
        DateTime OccurredAt);
}
