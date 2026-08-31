// ============================================================================
// PortfolioDeepPorts — Phase 94-J: PortfolioPage 6중 심층 결합 해체 계약 (순수 UI 경계)
//
//   1,176줄 PortfolioPage 의 6대 콘크리트 의존(트랜잭션 CRUD·실시간 시세·FX·AsOf 타임머신·JS 대화상자·DB 영속성)과
//   ~15 도메인 타입·~9 정적 엔진을 순수 UI 에서 물리 격리한다. 핵심 기법(94-B~I 검증):
//     • 도메인 타입은 RCL 읽기 뷰(IHoldingView 등)를 '공변 구현'(비침습) → 사본 0 투사.
//     • 편집 폼은 RCL 가변 드래프트(HoldingDraft/TxDraft) → 어댑터가 도메인으로 매핑(구상 무유입).
//     • 데이터+계산은 stateful 프리젠터(IPortfolioBoardPort)로 이관(호스트 어댑터가 도메인/정적엔진 자유 사용).
//   ★엔진 타입 참조 0(RCL 순수성) · liveArmed:false(조회/계산/영속 — 발주·소켓 무접촉) · fail-closed.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MS2E.UI.Portable.Ports.Portfolio
{
    // ── 열거형 미러(엔진 enum 정수값 정렬) ──────────────────────────────────────
    public enum PfMarket { KRX_KOSPI = 0, KRX_KOSDAQ = 1, US_NYSE = 2, US_NASDAQ = 3, US_AMEX = 4, CRYPTO = 5 }
    public enum PfCurrency { KRW = 0, USD = 1 }
    public enum PfTxType { Buy = 0, Sell = 1, Dividend = 2, Split = 3, Deposit = 4, Withdraw = 5 }
    public enum PfAssetClass { DomesticStock = 0, ForeignStock = 1, Crypto = 2 }

    // ── 읽기 전용 뷰(도메인 타입이 공변 구현) ──────────────────────────────────
    public interface IHoldingView
    {
        int Id { get; }
        string Symbol { get; }
        string Name { get; }
        PfMarket Market { get; }
        PfCurrency Currency { get; }
        decimal Quantity { get; }
        decimal AvgPrice { get; }
        decimal CurrentPrice { get; }
        string BrokerAccount { get; }
        decimal Commission { get; }
        decimal Tax { get; }
    }

    public interface ITxRowView
    {
        long Id { get; }
        DateTime At { get; }
        string Sym { get; }
        PfTxType Type { get; }
        decimal Qty { get; }
        decimal Price { get; }
        decimal Comm { get; }
        decimal Tax { get; }
        string Broker { get; }
    }

    public interface IAsOfHoldingView
    {
        string Symbol { get; }
        decimal Quantity { get; }
        decimal AvgCost { get; }
        decimal CostBasis { get; }
    }

    public interface ICategoryView
    {
        PfAssetClass Class { get; }
        string Label { get; }
        int Count { get; }
        decimal ValueKrw { get; }
        decimal PnlKrw { get; }
        decimal ReturnPct { get; }
        decimal WeightPct(decimal totalValueKrw);
    }

    /// <summary>세금 시뮬 1행(보유 + 시나리오 결합 뷰).</summary>
    public interface ITaxRowView
    {
        string Symbol { get; }
        string Name { get; }
        decimal Quantity { get; }
        decimal AvgPrice { get; }
        decimal CurrentPrice { get; }
        PfCurrency Currency { get; }
        decimal UnrealizedGain { get; }
        decimal TaxIfSoldNow { get; }
        decimal NetProceeds { get; }
        string Recommendation { get; }
    }

    /// <summary>배당 1행(이벤트 + 보유수량/예상배당 결합 뷰 — 어댑터가 보유와 조인).</summary>
    public interface IDividendView
    {
        DateTime ExDate { get; }
        string Symbol { get; }
        string Name { get; }
        decimal DividendPerShare { get; }
        PfCurrency Currency { get; }
        DateTime PayDate { get; }
        decimal YieldPct { get; }
        decimal HeldQuantity { get; }
        decimal ExpectedDividend { get; }
    }

    public interface ISymbolEntryView
    {
        string Code { get; }
        string Name { get; }
        PfMarket Market { get; }
        PfCurrency Currency { get; }
        bool IsCrypto { get; }
        string MarketLabel { get; }
    }

    // ── 가변 편집 드래프트(페이지가 @bind — 어댑터가 도메인 매핑) ────────────────
    public sealed class HoldingDraft
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public PfMarket Market { get; set; } = PfMarket.KRX_KOSPI;
        public PfCurrency Currency { get; set; } = PfCurrency.KRW;
        public decimal Quantity { get; set; }
        public decimal AvgPrice { get; set; }
        public string Broker { get; set; } = "";
        public decimal Commission { get; set; }
        public decimal Tax { get; set; }
        public bool Picked { get; set; }
    }

    public sealed class TxDraft
    {
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public PfTxType Type { get; set; } = PfTxType.Buy;   // 매수/매도
        public DateTime ExecutedAt { get; set; } = DateTime.Now;
        public decimal Qty { get; set; }
        public decimal Price { get; set; }
        public decimal Commission { get; set; }
        public decimal Tax { get; set; }
        public string Broker { get; set; } = "";
        public string Memo { get; set; } = "";
        public PfMarket Market { get; set; } = PfMarket.KRX_KOSPI;
        public PfCurrency Currency { get; set; } = PfCurrency.KRW;
        public bool Picked { get; set; }
    }

    // ── 5대 명명 포트(오더 명세) ────────────────────────────────────────────────

    /// <summary>① 트랜잭션 CRUD — 생성/삭제 + 이력 조회(원시 SQL·RecomputeAverage 격리).</summary>
    public interface ITransactionCrudPort
    {
        void Add(TxDraft draft);
        void Delete(long id, string symbol);
        IReadOnlyList<ITxRowView> Recent(int limit);
        /// <summary>저장 직후 진단(보유행 생성 여부·저장값) — 원본 SaveTx 진단 배너 보존.</summary>
        (bool holdingExists, decimal holdingQty, int txCount, string storedRaw) Diagnose(string symbol);
    }

    /// <summary>② 실시간 시세 스트림 — 주식 단건·코인 배치·스파크(IQuoteProvider/UpbitProvider 격리).</summary>
    public interface IQuoteStreamPort
    {
        Task<IQuoteTickView?> GetStockQuoteAsync(string symbol, PfMarket market);
        Task<IReadOnlyList<ICoinTickView>> GetCoinTicksAsync(IEnumerable<string> markets);
        Task<double[]> GetSparkAsync(string symbol, PfMarket market, int days);
    }
    public interface IQuoteTickView { decimal Price { get; } decimal ChangeRate { get; } string Session { get; } }
    public interface ICoinTickView { string Market { get; } decimal Price { get; } decimal ChangeRate { get; } }

    /// <summary>③ FX 실시간 환율(FxConverter 격리·실패 시 캐시 유지).</summary>
    public interface IFxRatePort
    {
        Task<decimal> GetUsdKrwAsync();
    }

    /// <summary>④ 시점 여행(AsOf) — LIVE/과거 상태 + 그 시점 보유 재구성(AsOfContext/AsOfPortfolio 격리).</summary>
    public interface ITimeTravelAsOfPort
    {
        bool IsLive { get; }
        DateTime? AsOf { get; }
        event Action? OnChange;
        IReadOnlyList<IAsOfHoldingView> Reconstruct();
        void EnterAsOf(DateTime date);   // Phase 94-K: AsOfBar — 과거 시점 진입
        void ReturnLive();               // Phase 94-K: AsOfBar — LIVE 복귀
    }

    /// <summary>⑤ JS 대화상자(IJSRuntime 격리·미가용 시 fail-open=true, 원본 폴백 보존).</summary>
    public interface IJsDialogPort
    {
        Task<bool> ConfirmAsync(string message);
    }

    // ── 심볼 검색 Port(SymbolPicker 이주 기반) ──────────────────────────────────
    public interface ISymbolSearchPort
    {
        /// <summary>라이브 코인 마켓 캐시 예열(세션 1회 — 실패해도 내장 카탈로그 폴백).</summary>
        Task WarmupAsync();
        /// <summary>로컬 즉시 검색(내장 카탈로그 + 코인 캐시) — 키 입력 즉시 표시.</summary>
        IReadOnlyList<ISymbolEntryView> SearchLocal(string query, int limit);
        /// <summary>라이브(주식 전 종목) + 로컬 병합(디바운스 후) — 코드 기준 중복 제거.</summary>
        Task<IReadOnlyList<ISymbolEntryView>> SearchMergedAsync(string query, int limit);
    }

    // ── stateful 프리젠터 Port(DB 영속성 + 계산 통합 — 페이지는 얇은 렌더러) ──────
    public sealed record PfCorrResult(IReadOnlyList<string> Symbols, decimal[,]? Matrix, string Message);

    public interface IPortfolioBoardPort
    {
        // 로드/갱신
        Task LoadAsync(bool persist = true);
        void LoadCustomOrder();

        // 집계 지표(현재가 기반)
        decimal TotalValue { get; }
        decimal TotalPnl { get; }
        decimal TotalRet { get; }
        decimal TodayPnl { get; }
        decimal TodayPct { get; }
        decimal UsdKrw { get; }
        string Timestamp { get; }
        int HoldingCount { get; }

        // 보유/삭제/분류
        IReadOnlyList<IHoldingView> Holdings { get; }
        IReadOnlyList<IHoldingView> Deleted { get; }
        IReadOnlyList<ICategoryView> Categories { get; }
        IReadOnlyList<IHoldingView> ViewHoldings(string filter, string sortKey, bool sortDesc);
        int FilterCount(string filter);

        // 종목별 표시 보조
        bool TryGetDayPct(string symbol, out decimal pct);
        string SessionOf(string symbol);
        bool TryGetPrevPrice(string symbol, out decimal prev);
        double[] Spark(string symbol);
        Task LoadSparksAsync(Func<Task> onProgress);

        // 보유 CRUD(드래프트 기반)
        Task SaveHoldingAsync(HoldingDraft draft, bool edited);
        Task DeleteHoldingAsync(string symbol, string brokerAccount);
        Task RestoreHoldingAsync(string symbol, string brokerAccount);
        Task HardDeleteHoldingAsync(string symbol, string brokerAccount);

        // 직접설정 순서
        void MoveCustom(string symbol, int dir, IReadOnlyList<string> viewSymbols);

        // 세금/상관/배당
        IReadOnlyList<ITaxRowView> TaxRows();
        Task<PfCorrResult> CalcCorrelationAsync();
        Task LoadDividendsAsync();
        IReadOnlyList<IDividendView> Dividends { get; }

        // 폼 보조
        (decimal commission, decimal tax) EstimateFees(string broker, PfMarket market, PfTxType type, decimal qty, decimal price);
        IReadOnlyList<string> Brokers { get; }
        PfAssetClass Classify(PfMarket market);
        HoldingDraft ToDraft(IHoldingView h);
    }
}
