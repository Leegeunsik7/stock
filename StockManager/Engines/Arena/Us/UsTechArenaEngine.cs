// ============================================================================
// UsTechArenaEngine — v12.18.61-DEV: 미국 빅테크 아레나(휴먼 vs AI 듀얼 가상 USD 원장 · 설명 가능한 AI 매매) — 아레나 3대 모드 2단계
//
//   ① 종목: UsTechUniverse 5종(AAPL · NVDA · MSFT · AMZN · TSLA · NASD) — 표 밖 티커 · $1 미만(서브페니 구간) 호가 = 폐기(fail-closed).
//     금액 = USD 센트(long) · 진영별 시작 자본 $100,000(10,000,000센트) 독립 원장(현금/보유/손익 공유 0).
//   ② 세션: KST → 미 동부(UsTradingCalendar · 서머타임 자동) → UsSessionStateMachine 페이즈.
//     PreMarket(EDT 17:00~22:30 KST) = 탐색/준비 — 시그널 워밍업 · 프리마켓 갭 측정만 · AI/휴먼 체결 0.
//     ContinuousTrading(EDT 22:30~05:00 KST) = 유일한 체결 페이즈. 휴장일/애프터/종료/달력 범위 밖 = 체결 0.
//     정규장 마감 10분 전(조기 폐장일 12:50 ET · 평일 15:50 ET) 이후 AI 신규 진입 0 · 보유 전량 SessionClose 청산.
//     미 동부 날짜 변경 = 직전 정규장 마지막 중간가를 전일 종가로 확정(프리마켓 갭 기준) · 갭 초기화.
//   ③ 알파 시그널(bp, 각 ±10000): QQQ/반도체 ETF 베타(SetMarketContext · 종목별 베타) · 10년물 국채금리 감응(금리 상승 = 음) ·
//     프리마켓 갭/수급((갭 × 10 + 호가 불균형)/2 · 갭은 프리마켓 중 갱신 · 정규장 중 고정) · 장중 모멘텀(중간가 vs EWMA × 20).
//     점수 = (25·ETF + 20·금리 + 30·갭/수급 + 25·모멘텀)/100. 종목별 워밍업 5틱 전 시그널 0.
//     점수 ≥ 3000 → 진입(확신도 = 점수/100 · 목표 +(150+4·확신도)bp · 손절 −150bp · UsTickAligner $0.01 격자 정렬).
//     보유 중 목표가 도달 / 손절가 이탈 / 점수 ≤ −2000 / 마감 10분 전 → 청산. AI 체결 1건마다 AiTradeRationale(Usd) 1건(누락 0).
//   ④ 체결: 시장가 = 최우선 호가 · 수량 상한 = 그 호가 잔량(부분 체결 · 잔량 폐기 — 큐잉/재시도 0). 위탁 수수료 0.025%(양방향) ·
//     SEC 수수료 $27.80/백만(매도). 공매도 없음. 양 진영은 같은 호가를 '각자' 소비(진영 간 간섭 0).
//   ⑤ 휴먼 주문: SubmitHuman(임의 스레드) → 고정 링(Vyukov 유계 MPSC · 포화 = QueueFull 폐기) → 정산 스레드가 다음 OnQuote/Drain 에서 체결.
//   ⑥ 게시: 정산 1회 전체가 단일 seqlock 쓰기 창(홀수 = 기록 중). 스냅샷 · 보유 · 호가 · 근거 피드 판독은 같은 버전으로 검증.
//
//   ★δ=0: OnQuote/Drain/SubmitHuman/판독 경로 힙 할당 0(고정 배열 · 값 타입 · LINQ/박싱/클로저/문자열 0).
//   ★단일 작성자: OnQuote/Drain/SetCapital 은 정산 스레드 1개만. ★liveArmed:false — 해외 주문 API 송출 수단 0(순수 인메모리 가상 대결).
//   [v12.18.63-DEV] 플루토 뉴스 촉매(AttachPluto · 선택): 정산마다 티커 뉴스 판정을 seqlock 판독(δ=0) → 실효 × 100bp 를 NewsWeight(30)로 가산 ·
//     뉴스 기여 최대 = NewsCatalyst 진입 · 치명적 구조적 악재(실효 ≤ −60) = 신규 진입 차단 · 보유 즉시 NewsShock 청산. 미결속/뉴스 없음 = 기존 동작 동일.
//   [v12.18.65-DEV] 플루토 밸류에이션(AttachValuation · 선택): 정산마다 티커 판정을 seqlock 판독 → 매도호가(센트)로 재판정(δ=0) →
//     점수 × 100bp 를 ValuationWeight(20)로 가산 · 밸류에이션 기여 최대 = ValuationDiscount 진입 · 고평가 경계 이상 = 신규 매수 보류 ·
//     극심한 과열 = 보유 ValuationBubble 조기 청산. 미결속/판정 없음/만료 = 기존 동작 동일.
//   [v12.18.67-DEV] 플루토 거버넌스(EnableGovernance · 선택): 티커별 국면 판정(매 유효 호가) → AI 진입 직전 PlutoCognitiveGovernor(국면 재가중 · 패닉 차단 ·
//     연패 쿨다운 · 악마의 변호인 · 동적 비중 5~25%) · 청산 = 자가 회고 + 연패 집계 · 근거 Governance 원자 게시. 모멘텀 = MomentumBp · 불균형 = GapFlowBp. 정지(기본) = 기존 동작 동일.
//   [v12.18.71-DEV] 시초가 개장 게이트(MarketOpenAuctionGate): 미 동부 일자 변경 = 전일 종가 승계 · 티커 정규장(09:30 ET = 22:30/23:30 KST) 첫 틱 = 개장 갭 →
//     (거버너 가동 시) 개장 창 갭 상승 ≥ 3% 추격 금지 · 갭 하락 ≤ −5% 패닉 차단 · −5%~−3% ∧ 갭/수급 우위 = OpeningGapFill 진입.
//     동시호가 접수 창(프리마켓 09:00~09:30 ET) 휴먼 주문 = 경매 장부 적재(32건) → 개장 틱 단일 시초가($0.01 내림 중간가) 일괄 체결.
//   [v12.18.71-DEV] 플루토 카피 트레이딩: SubmitFollow(슬리피지 20bp 보호) · SetAutoCopy(비례 미러링 · 예수금 부족 Fail-Closed · 복제분 동반 청산). 정지(기본) = 기존 동작 동일.
//   [v12.18.73-DEV] 플루토 기술적 팩터(EnableTechnical · 선택): 매 유효 호가 = 티커별 1분 봉(중간가) + 매물대 프로파일 갱신(δ=0) — 피드에 체결량 없음 →
//     호가 1틱 = 가중 1(시간-가격 TPO 근사 · 정직 표기). 팩터 게시(TryReadTechnical) · 가동 시 점수 × 100bp 를 TechnicalWeight(20)로 가산 ·
//     기술적 기여 최대 = VolatilitySqueezeBreakout / VolumeProfileSupport 진입 · POC 매물벽 = 악마의 변호인 반대 논리(거버너 가동 시). 정지(기본) = 기존 동작 동일.
//   [v12.18.75-DEV] 플루토 미시구조 팩터(EnableMicrostructure · 선택): 피드에 체결량 · 체결 방향 없음 → 매 유효 호가 = 틱 룰(중간가 상승 = 매수 · 하락 = 매도 · 보합 = 직전) ×
//     호가 1틱(틱 카운트 CVD 근사 · 정직 표기) + L1 잔량 뎁스 판독(제공 범위 비례 가중) → 팩터 게시(TryReadMicrostructure) · 가동 시 점수 × 100bp 를
//     MicrostructureWeight(20)로 가산 · 기여 최대 = CvdBullishDivergence / OrderbookDepthSupport 진입 · 약세 다이버전스 · 매도벽/허매수 = 악마의 변호인 ⑤⑥.
// ============================================================================
using System;
using System.Threading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Learning;
using StockManager.Engines.Cognitive.Microstructure;
using StockManager.Engines.Cognitive.News;
using StockManager.Engines.Cognitive.Regime;
using StockManager.Engines.Cognitive.Technical;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분
using StockManager.Engines.Cognitive.Valuation;
using StockManager.Engines.Feeds;
using StockManager.Engines.Pricing;

namespace StockManager.Engines.Arena.Us
{
    /// <summary>US 알파 시그널 1회 평가 결과(bp). Dominant = 점수 방향으로 가장 크게 기여한 구성요소.</summary>
    public readonly struct UsAlphaSignal
    {
        public readonly int Score, EtfBetaBp, RateBp, GapFlowBp, MomentumBp, NewsBp, ValuationBp, TechnicalBp, MicrostructureBp;
        public readonly ArenaTrigger Dominant;

        public UsAlphaSignal(int score, int etfBetaBp, int rateBp, int gapFlowBp, int momentumBp, ArenaTrigger dominant, int newsBp = 0, int valuationBp = 0,
            int technicalBp = 0, int microstructureBp = 0)
        {
            Score = score; EtfBetaBp = etfBetaBp; RateBp = rateBp; GapFlowBp = gapFlowBp; MomentumBp = momentumBp; Dominant = dominant; NewsBp = newsBp;
            ValuationBp = valuationBp; TechnicalBp = technicalBp; MicrostructureBp = microstructureBp;
        }
    }

    /// <summary>US 아레나 스냅샷(정산 1회 = 버전 1 증가 · 원장 금액 = 센트 · DuelBookStats.TaxWon = SEC 수수료).</summary>
    public readonly struct UsArenaSnapshot
    {
        public readonly long Ticks, DroppedQuotes, RationaleCount, LastKstTicks;
        public readonly UsSessionPhase Phase;
        public readonly int QqqBp, SoxxBp, Us10yChangeBp;
        public readonly DuelBookStats Ai, Human;

        public UsArenaSnapshot(long ticks, long droppedQuotes, long rationaleCount, long lastKstTicks, UsSessionPhase phase,
            int qqqBp, int soxxBp, int us10yChangeBp, in DuelBookStats ai, in DuelBookStats human)
        {
            Ticks = ticks; DroppedQuotes = droppedQuotes; RationaleCount = rationaleCount; LastKstTicks = lastKstTicks; Phase = phase;
            QqqBp = qqqBp; SoxxBp = soxxBp; Us10yChangeBp = us10yChangeBp; Ai = ai; Human = human;
        }

        public bool Tradable => Phase == UsSessionPhase.ContinuousTrading;
        public DuelBookStats Side(int side) => side == ArenaSide.Human ? Human : Ai;
    }

    public sealed class UsTechArenaEngine
    {
        public const long DefaultCapitalCents = 10_000_000;   // $100,000
        public const int MaxSymbols = UsTechUniverse.Count, MaxPositions = UsTechUniverse.Count, MaxAiPositions = 3;
        public const int RationaleCapacity = 128, HumanQueueCapacity = 64;
        public const long FeePer100k = 25, SecFeePer10M = 278;
        public const long MaxOrderQty = 1_000_000, MinPriceCents = 100, MaxPriceCents = 100_000_000, MaxBookQty = 1_000_000_000;
        public const int WarmupTicks = 5;
        public const int EntryScore = 3000, ExitScore = -2000;
        public const int EtfWeight = 25, RateWeight = 20, GapFlowWeight = 30, MomentumWeight = 25;
        public const int NewsWeight = 30;        // [v12.18.63-DEV] 플루토 뉴스 가산 가중(실효 ±100 → 점수 ±3000)
        public const int ValuationWeight = 20;   // [v12.18.65-DEV] 플루토 밸류에이션 가산 가중(점수 ±100 → 점수 ±2000)
        public const int MicrostructureWeight = 20;   // [v12.18.75-DEV] 플루토 미시구조 팩터 가산 가중(점수 ±100 → 점수 ±2000)
        public const int TechnicalWeight = 20;   // [v12.18.73-DEV] 플루토 기술적 팩터 가산 가중(점수 ±100 → 점수 ±2000)
        public const int EtfGain = 10, RateGain = 100, GapGain = 10, MomentumGain = 20;
        public const int StopBp = 150, TargetBaseBp = 150, TargetPerConfidenceBp = 4, PositionWeightBp = 2000;
        public static readonly long AiCutoffBeforeCloseTicks = TimeSpan.FromMinutes(10).Ticks;

        private const long KstOffsetTicks = 9 * TimeSpan.TicksPerHour;
        private const long RegularCloseTicks = 16 * TimeSpan.TicksPerHour, EarlyCloseTicks = 13 * TimeSpan.TicksPerHour;
        private const int EmaShift = 3;          // α = 1/8
        private const int Fx = 10;               // EWMA 고정소수점 ×1024
        private const int SeqlockRetries = 64;
        private const int QueueMask = HumanQueueCapacity - 1;

        private struct Sym { public long Symbol, Bid, Ask, BidQty, AskQty, EmaMidFx, LastRegularMidX2, PrevCloseX2; public int GapBp, Seen; public PlutoRegimeState Regime; public OpenGapState Gate; }
        private struct Pos { public long Symbol, Qty, AvgPx, Cost, Target, Stop, TradePnl, CopiedQty; public int SymIdx, RiskRewardX100; public PlutoEntryMemo Memo; }
        private struct HumanCell { public long Seq, Symbol, Qty, RefPx; public int Side, Flags; }
        private const int SlippageCancelled = -1;                                   // [v12.18.71-DEV] FillHuman 내부 거부(계수 기록 완료)

        private sealed class Book
        {
            public readonly Pos[] Positions = new Pos[MaxPositions];
            public int Count, Wins, Losses;
            public long Cash, Realized, Fees, SecFees, Peak, MddBp, Fills, Rejects;
        }

        private readonly Sym[] _syms = new Sym[MaxSymbols];
        private readonly Book[] _books = { new Book(), new Book() };   // ArenaSide.Ai 0 · Human 1
        private readonly AiTradeRationale[] _rationales = new AiTradeRationale[RationaleCapacity];
        private long _rationaleCount;
        private readonly HumanCell[] _hq = new HumanCell[HumanQueueCapacity];
        private long _hqEnq, _hqDeq;
        private long _capital;
        private long _ticks, _dropped, _lastKstTicks, _etDay;
        private PlutoSentimentBuffer? _pluto;
        private PlutoValuationBuffer? _valuation;
        private readonly PlutoTechnicalState[] _tech = new PlutoTechnicalState[MaxSymbols];      // [v12.18.73-DEV] 인덱스 = UsTechUniverse 인덱스
        private readonly PlutoTechnicalFactor[] _techLast = new PlutoTechnicalFactor[MaxSymbols];
        private bool _technical;
        private readonly PlutoMicrostructureState[] _micro = new PlutoMicrostructureState[MaxSymbols];   // [v12.18.75-DEV] 인덱스 = UsTechUniverse 인덱스
        private readonly PlutoMicrostructureFactor[] _microLast = new PlutoMicrostructureFactor[MaxSymbols];
        private bool _microstructure;
        private bool _governed;
        private int _govUs10yBp, _govDxyBp;
        private PlutoCognitiveWeightSeqlock? _learning;
        private PlutoSizerState _sizer;
        private long _govApproved, _govPanic, _govCooldown, _govVeto;
        private PlutoGovernorStats _govSnap;
        private UsSessionPhase _phase = UsSessionPhase.Closed;
        private bool _cutoff;
        private int _qqqBp, _soxxBp, _us10yBp;
        private bool _preOpen;                                                    // [v12.18.71-DEV] 동시호가 접수 창(프리마켓 09:00~09:30 ET)
        private readonly ArenaAuctionOrder[] _auction = new ArenaAuctionOrder[MarketOpenAuctionGate.AuctionCapacity];
        private int _auctionCount;
        private long _auctionFills, _auctionRejects, _gapVetoes, _gapFills;
        private ArenaAuctionStats _auctionSnap;
        private bool _copyAuto;
        private int _copyRatioBp = ArenaCopyMath.DefaultRatioBp;
        private ArenaCopyLedger _copy, _copySnap;
        private long _ver;
        private UsArenaSnapshot _snap;

        public UsTechArenaEngine(long capitalCents = DefaultCapitalCents)
        {
            for (int i = 0; i < HumanQueueCapacity; i++) _hq[i].Seq = i;
            SetCapital(capitalCents);
        }

        public string ModeName => "미국 빅테크(US Tech)";
        public long CapitalCents => Interlocked.Read(ref _capital);
        public long Version => Volatile.Read(ref _ver);

        /// <summary>양 진영 원장 · 종목 상태 · 세션 일자 초기화(정산 스레드 · 대결 시작 전/시세 출처 전환). 범위 밖 = ArgumentOutOfRange.</summary>
        public void SetCapital(long capitalCents)
        {
            if (capitalCents < 100_000 || capitalCents > 1_000_000_000_000) throw new ArgumentOutOfRangeException(nameof(capitalCents));
            BeginWrite();
            Interlocked.Exchange(ref _capital, capitalCents);
            for (int s = 0; s < ArenaSide.Count; s++)
            {
                var b = _books[s];
                Array.Clear(b.Positions);
                b.Count = b.Wins = b.Losses = 0;
                b.Cash = b.Peak = capitalCents;
                b.Realized = b.Fees = b.SecFees = b.MddBp = b.Fills = b.Rejects = 0;
            }
            Array.Clear(_rationales);
            _sizer = default;                         // [v12.18.67-DEV] 연패/쿨다운 · 거버넌스 계수 초기화
            _govApproved = _govPanic = _govCooldown = _govVeto = 0;
            _rationaleCount = 0;
            for (int i = 0; i < MaxSymbols; i++) _syms[i] = new Sym { Symbol = UsTechUniverse.Symbol(i) };
            Array.Clear(_tech);                       // [v12.18.73-DEV] 봉 · 매물대 프로파일 · 게시 팩터 동반 초기화
            Array.Clear(_techLast);
            Array.Clear(_micro);                      // [v12.18.75-DEV] CVD · 뎁스 · 게시 팩터 동반 초기화
            Array.Clear(_microLast);
            Array.Clear(_auction);                    // [v12.18.71-DEV] 경매 장부 · 개장 게이트 · 복제 계수 초기화(자동 미러링 설정은 유지)
            _auctionCount = 0;
            _auctionFills = _auctionRejects = _gapVetoes = _gapFills = 0;
            _copy = default;
            _ticks = _dropped = _etDay = 0;
            PublishSnapshot();
            EndWrite();
        }

        /// <summary>시장 맥락(bp · ±10000 클램프 · 다음 정산부터 반영): QQQ 등락 · 반도체 ETF 등락 · 미 10년물 금리 변화.</summary>
        public void SetMarketContext(int qqqBp, int soxxBp, int us10yChangeBp)
        {
            Volatile.Write(ref _qqqBp, Math.Clamp(qqqBp, -10000, 10000));
            Volatile.Write(ref _soxxBp, Math.Clamp(soxxBp, -10000, 10000));
            Volatile.Write(ref _us10yBp, Math.Clamp(us10yChangeBp, -10000, 10000));
        }

        /// <summary>[v12.18.63-DEV] 플루토 뉴스 촉매 버퍼 결속(정산 시 티커별 판독 · δ=0).</summary>
        public void AttachPluto(PlutoSentimentBuffer buffer) => Volatile.Write(ref _pluto, buffer ?? throw new ArgumentNullException(nameof(buffer)));

        public bool PlutoAttached => Volatile.Read(ref _pluto) is not null;

        /// <summary>[v12.18.64-DEV] 결속된 플루토 버퍼(UI 촉매 배지 투영 · 미결속 = null).</summary>
        public PlutoSentimentBuffer? PlutoBuffer => Volatile.Read(ref _pluto);

        /// <summary>[v12.18.65-DEV] 플루토 밸류에이션 버퍼 결속(정산 시 티커별 판독 + 호가 재판정 · δ=0).</summary>
        public void AttachValuation(PlutoValuationBuffer buffer) => Volatile.Write(ref _valuation, buffer ?? throw new ArgumentNullException(nameof(buffer)));

        public bool ValuationAttached => Volatile.Read(ref _valuation) is not null;

        /// <summary>[v12.18.73-DEV] 플루토 기술적 팩터 시그널 가산 가동/정지(봉 · 프로파일 갱신과 팩터 게시는 상시 · 다음 정산부터 · 정지 = 기존 동작 동일).</summary>
        public void EnableTechnical(bool enabled) => Volatile.Write(ref _technical, enabled);

        public bool TechnicalEnabled => Volatile.Read(ref _technical);

        /// <summary>[v12.18.73-DEV] 티커 최신 기술적 팩터(직전 정산 중간가 판정 · seqlock · δ=0 · 표 밖/경합 상한 초과 = false).</summary>
        public bool TryReadTechnical(long symbol, out PlutoTechnicalFactor factor)
        {
            int i = UsTechUniverse.IndexOf(symbol);
            for (int k = 0; k < SeqlockRetries && i >= 0; k++)
            {
                long v = Volatile.Read(ref _ver);
                if ((v & 1) != 0) { Thread.SpinWait(8); continue; }
                factor = _techLast[i];
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v) return true;
            }
            factor = default;
            return false;
        }

        /// <summary>[v12.18.75-DEV] 플루토 미시구조 팩터 시그널 가산 가동/정지(CVD · 뎁스 갱신과 팩터 게시는 상시 · 다음 정산부터 · 정지 = 기존 동작 동일).</summary>
        public void EnableMicrostructure(bool enabled) => Volatile.Write(ref _microstructure, enabled);

        public bool MicrostructureEnabled => Volatile.Read(ref _microstructure);

        /// <summary>[v12.18.75-DEV] 티커 최신 미시구조 팩터(직전 정산 중간가 판정 · seqlock · δ=0 · 표 밖/경합 상한 초과 = false).</summary>
        public bool TryReadMicrostructure(long symbol, out PlutoMicrostructureFactor factor)
        {
            int i = UsTechUniverse.IndexOf(symbol);
            for (int k = 0; k < SeqlockRetries && i >= 0; k++)
            {
                long v = Volatile.Read(ref _ver);
                if ((v & 1) != 0) { Thread.SpinWait(8); continue; }
                factor = _microLast[i];
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v) return true;
            }
            factor = default;
            return false;
        }

        /// <summary>[v12.18.67-DEV] 플루토 거버넌스 가동/정지(국면 · 악마의 변호인 · 동적 사이징 · 자가 회고 · 다음 정산부터 · 정지 = 기존 동작 동일).</summary>
        public void EnableGovernance(bool enabled) => Volatile.Write(ref _governed, enabled);

        /// <summary>[v12.18.77-DEV] 플루토 지속 학습 가중치 결속(Track B 스웜이 핫스왑 · 정산 스레드는 seqlock 판독만 · δ=0).</summary>
        public void AttachLearning(PlutoCognitiveWeightSeqlock weights) => Volatile.Write(ref _learning, weights ?? throw new ArgumentNullException(nameof(weights)));

        public bool LearningAttached => Volatile.Read(ref _learning) is not null;

        public bool GovernanceEnabled => Volatile.Read(ref _governed);

        /// <summary>[v12.18.67-DEV] 악마의 변호인 거시 역풍 입력(bp · ±10000 클램프 · 다음 정산부터): 미 10년물 금리 변화 · 달러 인덱스 변화.</summary>
        public void SetMacroHeadwind(int us10yChangeBp, int dollarIndexChangeBp)
        {
            Volatile.Write(ref _govUs10yBp, Math.Clamp(us10yChangeBp, -10000, 10000));
            Volatile.Write(ref _govDxyBp, Math.Clamp(dollarIndexChangeBp, -10000, 10000));
        }

        /// <summary>[v12.18.67-DEV] 종목 현재 국면 상태(seqlock 판독 · δ=0 · 미등록/경합 상한 초과 = false).</summary>
        public bool TryReadRegime(long symbol, out PlutoRegimeState state)
        {
            for (int k = 0; k < SeqlockRetries; k++)
            {
                long v = Volatile.Read(ref _ver);
                if ((v & 1) != 0) { Thread.SpinWait(8); continue; }
                int i = UsTechUniverse.IndexOf(symbol);
                state = i >= 0 ? _syms[i].Regime : default;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v) return i >= 0;
            }
            state = default;
            return false;
        }

        /// <summary>[v12.18.67-DEV] 종목 현재 국면(판독 실패 = Unknown).</summary>
        public MarketRegime RegimeOf(long symbol) => TryReadRegime(symbol, out var s) ? s.Regime : MarketRegime.Unknown;

        /// <summary>[v12.18.67-DEV] 거버넌스 계수 판독(seqlock · 경합 상한 초과 = false).</summary>
        public bool TryReadGovernor(out PlutoGovernorStats stats)
        {
            for (int k = 0; k < SeqlockRetries; k++)
            {
                long v = Volatile.Read(ref _ver);
                if ((v & 1) != 0) { Thread.SpinWait(8); continue; }
                stats = _govSnap;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v) return true;
            }
            stats = default;
            return false;
        }

        // ── 휴먼 주문 큐(Vyukov 유계 MPSC · 할당 0) ──────────────────────────────────

        public int SubmitHuman(long symbol, int side, long qty) => Enqueue(symbol, side, qty, 0, 0);

        /// <summary>[v12.18.71-DEV] 1-클릭 복제 주문(refPx = AI 체결가 센트 · 체결 시 불리한 슬리피지 20bp 초과 = 취소 · 매도 = 보유 상한). 반환 = UsArenaReject(접수 단계).</summary>
        public int SubmitFollow(long symbol, int side, long qty, long refPx)
            => refPx < MinPriceCents || refPx > MaxPriceCents ? UsArenaReject.Invalid : Enqueue(symbol, side, qty, refPx, MarketOpenAuctionGate.FlagCopy);

        /// <summary>[v12.18.71-DEV] 플루토 자동 미러링 가동/정지(배율 bp 0.1x~3.0x · 다음 정산부터 · 범위 밖 = false · 무변경).</summary>
        public bool SetAutoCopy(bool enabled, int ratioBp)
        {
            if (!ArenaCopyMath.ValidRatio(ratioBp)) return false;
            Volatile.Write(ref _copyRatioBp, ratioBp);
            Volatile.Write(ref _copyAuto, enabled);
            return true;
        }

        public bool AutoCopyEnabled => Volatile.Read(ref _copyAuto);
        public int AutoCopyRatioBp => Volatile.Read(ref _copyRatioBp);

        /// <summary>[v12.18.71-DEV] 복제 계수 판독(seqlock · 경합 상한 초과 = false).</summary>
        public bool TryReadCopy(out ArenaCopyLedger ledger)
        {
            for (int k = 0; k < SeqlockRetries; k++)
            {
                long v = Volatile.Read(ref _ver);
                if ((v & 1) != 0) { Thread.SpinWait(8); continue; }
                ledger = _copySnap;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v) return true;
            }
            ledger = default;
            return false;
        }

        /// <summary>[v12.18.71-DEV] 개장 게이트 계수 판독(seqlock · 경합 상한 초과 = false).</summary>
        public bool TryReadAuction(out ArenaAuctionStats stats)
        {
            for (int k = 0; k < SeqlockRetries; k++)
            {
                long v = Volatile.Read(ref _ver);
                if ((v & 1) != 0) { Thread.SpinWait(8); continue; }
                stats = _auctionSnap;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v) return true;
            }
            stats = default;
            return false;
        }

        /// <summary>[v12.18.71-DEV] 티커 개장 갭 상태(seqlock · 표 밖/경합 상한 초과 = false).</summary>
        public bool TryReadOpenGap(long symbol, out OpenGapState gate)
        {
            for (int k = 0; k < SeqlockRetries; k++)
            {
                long v = Volatile.Read(ref _ver);
                if ((v & 1) != 0) { Thread.SpinWait(8); continue; }
                int i = UsTechUniverse.IndexOf(symbol);
                gate = i >= 0 ? _syms[i].Gate : default;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v) return i >= 0;
            }
            gate = default;
            return false;
        }

        private int Enqueue(long symbol, int side, long qty, long refPx, int flags)
        {
            if (UsTechUniverse.IndexOf(symbol) < 0 || (side != 1 && side != -1) || qty <= 0 || qty > MaxOrderQty) return UsArenaReject.Invalid;
            long pos = Volatile.Read(ref _hqEnq);
            while (true)
            {
                ref HumanCell c = ref _hq[pos & QueueMask];
                long dif = Volatile.Read(ref c.Seq) - pos;
                if (dif == 0)
                {
                    if (Interlocked.CompareExchange(ref _hqEnq, pos + 1, pos) == pos)
                    {
                        c.Symbol = symbol; c.Side = side; c.Qty = qty; c.RefPx = refPx; c.Flags = flags;
                        Volatile.Write(ref c.Seq, pos + 1);
                        return UsArenaReject.None;
                    }
                    pos = Volatile.Read(ref _hqEnq);
                }
                else if (dif < 0) return UsArenaReject.QueueFull;
                else pos = Volatile.Read(ref _hqEnq);
            }
        }

        private bool TryDequeue(out ArenaAuctionOrder o)
        {
            long pos = _hqDeq;
            ref HumanCell c = ref _hq[pos & QueueMask];
            if (Volatile.Read(ref c.Seq) != pos + 1) { o = default; return false; }
            o = new ArenaAuctionOrder { Symbol = c.Symbol, Side = c.Side, Qty = c.Qty, RefPx = c.RefPx, Flags = c.Flags };
            Volatile.Write(ref c.Seq, pos + HumanQueueCapacity);
            _hqDeq = pos + 1;
            return true;
        }

        // ── 세션 시계 ──────────────────────────────────────────────────────────

        /// <summary>KST 시각의 미국 세션 페이즈(δ=0 · UsSessionStateMachine 위임).</summary>
        public static UsSessionPhase PhaseAt(DateTime kst) => UsSessionStateMachine.GetSessionPhase(kst);

        private void Clock(DateTime kst)
        {
            _lastKstTicks = kst.Ticks;
            if (kst.Ticks < KstOffsetTicks) { _phase = UsSessionPhase.CalendarUncovered; _cutoff = true; _preOpen = false; return; }
            DateTime et = UsTradingCalendar.ToEastern(new DateTime(kst.Ticks - KstOffsetTicks));
            _phase = UsSessionStateMachine.GetSessionPhaseEastern(et);
            long day = et.Ticks - et.Ticks % TimeSpan.TicksPerDay;
            if (day != _etDay)
            {
                if (_etDay != 0) RollDay();
                _etDay = day;
            }
            _preOpen = _phase == UsSessionPhase.PreMarket && et.Ticks - day >= MarketOpenAuctionGate.UsAuctionStartEtTicks;
            long close = UsTradingCalendar.IsEarlyClose(et) ? EarlyCloseTicks : RegularCloseTicks;
            _cutoff = et.Ticks - day >= close - AiCutoffBeforeCloseTicks;
        }

        /// <summary>미 동부 날짜 변경: 직전 정규장 마지막 중간가 = 전일 종가(갭 기준) · 갭 초기화.</summary>
        private void RollDay()
        {
            for (int i = 0; i < MaxSymbols; i++)
            {
                ref Sym s = ref _syms[i];
                if (s.LastRegularMidX2 > 0) s.PrevCloseX2 = s.LastRegularMidX2;
                s.GapBp = 0;
                MarketOpenAuctionGate.RollDay(ref s.Gate);                  // [v12.18.71-DEV] 개장 게이트 전일 종가 승계
            }
        }

        // ── 정산(단일 작성자) ────────────────────────────────────────────────────

        /// <summary>시세 없이 휴먼 큐만 체결(최신 호가 기준 · 세션 판정 갱신).</summary>
        public void Drain(DateTime kst)
        {
            BeginWrite();
            Clock(kst);
            DrainHuman();
            PublishSnapshot();
            EndWrite();
        }

        /// <summary>
        /// 종목 1틱(최우선 호가 센트 · 잔량) 정산: 세션 판정 → 시그널 평가 → 휴먼 큐 체결 → (정규장만) AI 진입/청산(+근거) → 시가평가 게시.
        /// 표 밖 티커 · $1 미만 · 역전 · 음수 잔량 · 상한 초과 = 폐기(DroppedQuotes 증가 · fail-closed).
        /// </summary>
        public void OnQuote(long symbol, long bidCents, long askCents, long bidQty, long askQty, DateTime kst)
        {
            BeginWrite();
            Clock(kst);
            int si = -1;
            if (bidCents >= MinPriceCents && askCents >= bidCents && askCents <= MaxPriceCents && bidQty >= 0 && askQty >= 0 && bidQty <= MaxBookQty && askQty <= MaxBookQty)
                si = UsTechUniverse.IndexOf(symbol);
            if (si < 0) _dropped++;
            else
            {
                _ticks++;
                ref Sym s = ref _syms[si];
                s.Bid = bidCents; s.Ask = askCents; s.BidQty = bidQty; s.AskQty = askQty;
                PlutoRegimeClassifier.Update(ref s.Regime, bidCents, askCents, bidQty, askQty);
                long midTech = bidCents + (askCents - bidCents) / 2;
                PlutoTechnicalAnalyzer.OnPrice(ref _tech[si], midTech, kst.Ticks);
                PlutoTechnicalAnalyzer.OnVolume(ref _tech[si], midTech, 1);                 // 체결량 미제공 피드 = TPO(호가 1틱 = 1)
                _techLast[si] = PlutoTechnicalAnalyzer.Assess(in _tech[si], symbol, midTech);
                PlutoTechnicalFactor tech = Volatile.Read(ref _technical) ? _techLast[si] : default;
                PlutoMicrostructureAnalyzer.OnTickRuleTrade(ref _micro[si], 1, midTech, kst.Ticks);   // [v12.18.75-DEV] 체결량 · 방향 미제공 = 틱 룰 × 호가 1틱(틱 카운트 CVD)
                PlutoMicrostructureAnalyzer.OnL1(ref _micro[si], bidQty, askQty);
                _microLast[si] = PlutoMicrostructureAnalyzer.Assess(in _micro[si], symbol, midTech);
                PlutoMicrostructureFactor micro = Volatile.Read(ref _microstructure) ? _microLast[si] : default;
                long midX2 = bidCents + askCents;
                long midFx = midX2 << (Fx - 1);
                if (_phase == UsSessionPhase.PreMarket && s.PrevCloseX2 > 0)
                    s.GapBp = (int)Math.Clamp((midX2 - s.PrevCloseX2) * 10000 / s.PrevCloseX2, -10000, 10000);
                else if (_phase == UsSessionPhase.ContinuousTrading)
                    s.LastRegularMidX2 = midX2;
                PlutoNewsTag news = default;
                var pluto = Volatile.Read(ref _pluto);
                if (pluto is not null && pluto.TryRead(symbol, out var nf)) news = new PlutoNewsTag(in nf, kst.Ticks - PlutoClock.KstOffsetTicks);
                PlutoValuationTag val = default;
                var valuation = Volatile.Read(ref _valuation);
                if (valuation is not null && valuation.TryRead(symbol, out var vf)) val = new PlutoValuationTag(in vf, s.Ask, kst.Ticks - PlutoClock.KstOffsetTicks);
                var sig = s.Seen >= WarmupTicks
                    ? Evaluate(si, Volatile.Read(ref _qqqBp), Volatile.Read(ref _soxxBp), Volatile.Read(ref _us10yBp), s.GapBp, bidQty, askQty, midFx, s.EmaMidFx, news.NewsBp, val.ValuationBp,
                        tech.TechnicalBp, tech.IsBreakoutUp, micro.MicrostructureBp, micro.IsBullishDivergence)
                    : default;
                if (s.Seen == 0) s.EmaMidFx = midFx;
                else s.EmaMidFx += (midFx - s.EmaMidFx) >> EmaShift;
                if (s.Seen < int.MaxValue) s.Seen++;

                if (_phase == UsSessionPhase.ContinuousTrading && MarketOpenAuctionGate.OnRegularTick(ref s.Gate, midX2)) Uncross(si);
                DrainHuman();
                if (_phase == UsSessionPhase.ContinuousTrading) StepAi(si, in sig, in news, in val, in tech, in micro, kst);
            }
            PublishSnapshot();
            EndWrite();
        }

        /// <summary>[v12.18.71-DEV] 개장 틱: 경매 장부의 해당 티커 주문을 단일 시초가($0.01 내림 중간가 · 매수1호가 하한)로 일괄 체결(접수 순서 · 잔량 상한).</summary>
        private void Uncross(int si)
        {
            ref Sym s = ref _syms[si];
            long px = Math.Max(AlignDownCents((s.Bid + s.Ask) / 2, 0), s.Bid);
            int keep = 0;
            for (int i = 0; i < _auctionCount; i++)
            {
                if (_auction[i].Symbol != s.Symbol) { _auction[keep++] = _auction[i]; continue; }
                if (FillHuman(in _auction[i], si, px, px) == UsArenaReject.None) _auctionFills++; else _auctionRejects++;
            }
            for (int i = keep; i < _auctionCount; i++) _auction[i] = default;
            _auctionCount = keep;
        }

        /// <summary>순수 시그널 평가(할당 0). index = UsTechUniverse 인덱스 · midFx/emaMidFx = 중간가 ×1024 고정소수점.</summary>
        public static UsAlphaSignal Evaluate(int index, int qqqBp, int soxxBp, int us10yChangeBp, int gapBp, long bidQty, long askQty, long midFx, long emaMidFx, int newsBp = 0, int valuationBp = 0,
            int technicalBp = 0, bool squeezeBreakout = false, int microstructureBp = 0, bool cvdDivergence = false)
        {
            int technical = Math.Clamp(technicalBp, -10000, 10000);
            if ((uint)index >= MaxSymbols) return default;
            int micro = Math.Clamp(microstructureBp, -10000, 10000);
            int news = Math.Clamp(newsBp, -10000, 10000);
            int valuation = Math.Clamp(valuationBp, -10000, 10000);
            long qqq = Math.Clamp(qqqBp, -10000, 10000), soxx = Math.Clamp(soxxBp, -10000, 10000), y10 = Math.Clamp(us10yChangeBp, -10000, 10000);
            int etf = (int)Math.Clamp((qqq * UsTechUniverse.QqqBetaX100(index) + soxx * UsTechUniverse.SemiBetaX100(index)) / 100 * EtfGain, -10000, 10000);
            int rate = (int)Math.Clamp(-y10 * UsTechUniverse.RateSensX100(index) / 100 * RateGain, -10000, 10000);
            long depth = bidQty + askQty;
            int obi = depth <= 0 ? 0 : (int)((bidQty - askQty) * 10000 / depth);
            int gapFlow = ((int)Math.Clamp((long)Math.Clamp(gapBp, -10000, 10000) * GapGain, -10000, 10000) + obi) / 2;
            int mom = emaMidFx <= 0 ? 0 : (int)Math.Clamp((midFx - emaMidFx) * 10000 / emaMidFx * MomentumGain, -10000, 10000);
            long cEtf = (long)etf * EtfWeight, cRate = (long)rate * RateWeight, cGap = (long)gapFlow * GapFlowWeight, cMom = (long)mom * MomentumWeight;
            long cNews = (long)news * NewsWeight, cVal = (long)valuation * ValuationWeight, cTech = (long)technical * TechnicalWeight;
            long cMicro = (long)micro * MicrostructureWeight;
            int score = (int)Math.Clamp((cEtf + cRate + cGap + cMom + cNews + cVal + cTech + cMicro) / 100, -10000, 10000);
            int sign = score >= 0 ? 1 : -1;
            var dom = ArenaTrigger.PremarketGapFlow;
            long best = cGap * sign;
            if (cEtf * sign > best) { best = cEtf * sign; dom = ArenaTrigger.EtfBeta; }
            if (cMom * sign > best) { best = cMom * sign; dom = ArenaTrigger.MomentumBreakout; }
            if (cRate * sign > best) { best = cRate * sign; dom = ArenaTrigger.RateSensitivity; }
            if (cNews * sign > best) { best = cNews * sign; dom = ArenaTrigger.NewsCatalyst; }
            if (cVal * sign > best) { best = cVal * sign; dom = ArenaTrigger.ValuationDiscount; }
            if (cTech * sign > best) { best = cTech * sign; dom = squeezeBreakout ? ArenaTrigger.VolatilitySqueezeBreakout : ArenaTrigger.VolumeProfileSupport; }
            if (cMicro * sign > best) dom = cvdDivergence ? ArenaTrigger.CvdBullishDivergence : ArenaTrigger.OrderbookDepthSupport;
            return new UsAlphaSignal(score, etf, rate, gapFlow, mom, dom, news, valuation, technical, micro);
        }

        private void StepAi(int si, in UsAlphaSignal sig, in PlutoNewsTag news, in PlutoValuationTag val, in PlutoTechnicalFactor tech, in PlutoMicrostructureFactor micro,
            DateTime kst)
        {
            var b = _books[ArenaSide.Ai];
            ref Sym s = ref _syms[si];
            int pi = FindPos(b, s.Symbol);
            if (pi >= 0)
            {
                ref Pos p = ref b.Positions[pi];
                ArenaTrigger exit = ArenaTrigger.None;
                if (_cutoff) exit = ArenaTrigger.SessionClose;
                else if (news.IsShock) exit = ArenaTrigger.NewsShock;
                else if (val.IsBubble) exit = ArenaTrigger.ValuationBubble;
                else if (s.Bid >= p.Target) exit = ArenaTrigger.TakeProfit;
                else if (s.Bid <= p.Stop) exit = ArenaTrigger.StopLoss;
                else if (sig.Score <= ExitScore) exit = ArenaTrigger.SignalReversal;
                if (exit == ArenaTrigger.None) return;
                long target = p.Target, stop = p.Stop, priorPnl = p.TradePnl; int rr = p.RiskRewardX100;
                var memo = p.Memo;
                long entry = p.AvgPx;                                       // [v12.18.70-DEV] 청산 진입가(Sell 전 판독 · 전량 청산 시 슬롯 회수)
                long heldBefore = p.Qty;
                if (Sell(b, si, p.Qty, s.Bid, out long filled, out long pnl) != UsArenaReject.None) return;
                int conf = exit == ArenaTrigger.SignalReversal ? Math.Clamp(-sig.Score / 100, 0, 100)
                         : exit == ArenaTrigger.NewsShock ? Math.Clamp(-news.EffectiveScore, 0, 100)
                         : exit == ArenaTrigger.ValuationBubble ? Math.Clamp(-val.Score, 0, 100) : 100;
                var exitGov = GovernExit(in memo, exit, si, priorPnl + pnl, FindPos(b, s.Symbol) < 0, kst.Ticks);
                Record(new AiTradeRationale(_rationaleCount + 1, s.Symbol, -1, filled, s.Bid, kst.Ticks, exit, sig.Score, conf,
                    target, stop, rr, sig.RateBp, sig.GapFlowBp, sig.EtfBetaBp, sig.MomentumBp, pnl, ArenaCurrency.Usd, in news, in val, 0, 0, in exitGov, entry, in tech, in micro));
                if (Volatile.Read(ref _copyAuto)) MirrorExit(si, filled, heldBefore);
                return;
            }
            if (_cutoff || news.IsShock || val.BlocksEntry || b.Count >= MaxAiPositions) return;
            bool governed = Volatile.Read(ref _governed);                  // [v12.18.71-DEV] 개장 갭 전략 = 거버너(악마의 변호인) 가동 시에만 · 정지 = 기존 동작 동일
            if (governed && MarketOpenAuctionGate.VetoesEntry(in s.Gate)) { if (sig.Score >= EntryScore) _gapVetoes++; return; }
            bool gapFill = governed && MarketOpenAuctionGate.GapFillSignal(in s.Gate, sig.GapFlowBp);
            int raw = gapFill ? Math.Max(sig.Score, EntryScore) : sig.Score;
            var dominant = gapFill && sig.Score < EntryScore ? ArenaTrigger.OpeningGapFill : sig.Dominant;
            int score = raw, confidence, weightBp = PositionWeightBp;
            PlutoGovernanceTag gov = default;
            if (governed)
            {
                if (!Govern(si, raw, sig.MomentumBp, MomentumWeight, sig.GapFlowBp, GapFlowWeight, kst.Ticks, out gov, tech.IsPocResistance, micro.IsBearishDivergence, micro.IsDepthTrap)) return;
                score = gov.AdjustedScore; confidence = gov.Confidence; weightBp = gov.WeightBp;
            }
            else
            {
                if (raw < EntryScore) return;
                confidence = Math.Clamp(raw / 100, 0, 100);
            }
            long tgt = AlignUpCents(s.Ask, TargetBaseBp + TargetPerConfidenceBp * confidence);
            long stp = AlignDownCents(s.Ask, -StopBp);
            if (stp <= 0 || stp >= s.Ask || tgt <= s.Ask) return;
            int rrX100 = (int)Math.Min((tgt - s.Ask) * 100 / (s.Ask - stp), 100_000);
            if (rrX100 <= 0) return;
            long budget = Math.Min(b.Cash, Equity(b) * weightBp / 10000);
            long qty = budget * 100_000 / (s.Ask * (100_000 + FeePer100k));
            if (qty <= 0) return;
            long aiEquity = Equity(b);
            if (Buy(b, si, qty, s.Ask, out long bought) != UsArenaReject.None) return;
            ref Pos np = ref b.Positions[FindPos(b, s.Symbol)];
            np.Target = tgt; np.Stop = stp; np.RiskRewardX100 = rrX100;
            if (gov.Governed) np.Memo = Memo(in gov, kst.Ticks, np.Cost);
            if (dominant == ArenaTrigger.OpeningGapFill) _gapFills++;
            Record(new AiTradeRationale(_rationaleCount + 1, s.Symbol, 1, bought, s.Ask, kst.Ticks, dominant, score, confidence,
                tgt, stp, rrX100, sig.RateBp, sig.GapFlowBp, sig.EtfBetaBp, sig.MomentumBp, 0, ArenaCurrency.Usd, in news, in val, 0, 0, in gov, 0, in tech, in micro));
            if (Volatile.Read(ref _copyAuto)) MirrorEntry(si, bought, aiEquity);
        }

        /// <summary>[v12.18.71-DEV] 자동 미러링 진입(같은 쓰기 창 · AI 체결가 = 체결가 · 진입 직전 자본 비례 수량 · 예수금 부족 = Fail-Closed).</summary>
        private void MirrorEntry(int si, long aiQty, long aiEquity)
        {
            ref Sym s = ref _syms[si];
            var h = _books[ArenaSide.Human];
            long qty = ArenaCopyMath.ScaleQty(aiQty, aiEquity, Equity(h), Volatile.Read(ref _copyRatioBp));
            long filled = 0;
            int rc = qty <= 0 ? ArenaCopyCode.ZeroQty : CopyCode(Buy(h, si, qty, s.Ask, out filled));
            if (rc == ArenaCopyCode.None) h.Positions[FindPos(h, s.Symbol)].CopiedQty += filled;
            else h.Rejects++;
            _copy.Record(ArenaCopyKind.Mirror, rc, 1, s.Symbol, filled);
        }

        /// <summary>[v12.18.71-DEV] AI 청산 동반 청산(휴먼 복제 보유분 × AI 청산 비율 · 복제분 없음 = 무동작).</summary>
        private void MirrorExit(int si, long aiSold, long aiHeldBefore)
        {
            ref Sym s = ref _syms[si];
            var h = _books[ArenaSide.Human];
            int pi = FindPos(h, s.Symbol);
            if (pi < 0 || h.Positions[pi].CopiedQty <= 0) return;
            long qty = ArenaCopyMath.CompanionQty(h.Positions[pi].CopiedQty, h.Positions[pi].Qty, aiSold, aiHeldBefore);
            if (qty <= 0) return;
            int rc = CopyCode(Sell(h, si, qty, s.Bid, out long filled, out _));
            if (rc != ArenaCopyCode.None) h.Rejects++;
            _copy.Record(ArenaCopyKind.Companion, rc, -1, s.Symbol, filled);
        }

        /// <summary>[v12.18.71-DEV] 아레나 거부 코드 → 복제 결과 코드(ArenaCopyCode).</summary>
        public static int CopyCode(int reject) => reject switch
        {
            UsArenaReject.None => ArenaCopyCode.None,
            UsArenaReject.InsufficientCash => ArenaCopyCode.InsufficientCash,
            UsArenaReject.InsufficientPosition => ArenaCopyCode.InsufficientPosition,
            UsArenaReject.SessionClosed => ArenaCopyCode.SessionClosed,
            UsArenaReject.NoQuote => ArenaCopyCode.NoQuote,
            UsArenaReject.NoLiquidity => ArenaCopyCode.NoLiquidity,
            UsArenaReject.QueueFull => ArenaCopyCode.QueueFull,
            UsArenaReject.Invalid => ArenaCopyCode.Invalid,
            _ => ArenaCopyCode.Rejected,
        };

        /// <summary>가격(센트) × (1 + bp) 를 $0.01 격자로 올림(UsTickAligner · raw 10^8 경유 · 무효 = 0).</summary>
        public static long AlignUpCents(long cents, int bp)
        {
            if (cents < MinPriceCents || cents > MaxPriceCents) return 0;
            return UsTickAligner.AlignUp(cents * UsTickAligner.PennyRaw * (10000 + bp) / 10000) / UsTickAligner.PennyRaw;
        }

        /// <summary>가격(센트) × (1 + bp) 를 $0.01 격자로 내림(UsTickAligner · raw 10^8 경유 · 무효 = 0).</summary>
        public static long AlignDownCents(long cents, int bp)
        {
            if (cents < MinPriceCents || cents > MaxPriceCents) return 0;
            return UsTickAligner.AlignDown(cents * UsTickAligner.PennyRaw * (10000 + bp) / 10000) / UsTickAligner.PennyRaw;
        }

        private void DrainHuman()
        {
            bool open = _phase == UsSessionPhase.ContinuousTrading;
            if (!open && !_preOpen) PurgeAuction();
            while (TryDequeue(out var o))
            {
                int si = UsTechUniverse.IndexOf(o.Symbol);
                bool auction = !open && _preOpen && si >= 0 && _syms[si].Seen > 0 && !_syms[si].Gate.Opened;
                if (auction && _auctionCount < MarketOpenAuctionGate.AuctionCapacity)
                {
                    _auction[_auctionCount++] = o;                          // [v12.18.71-DEV] 동시호가 접수 → 개장 틱 단일가 체결
                    continue;
                }
                if (!open) Reject(in o, auction ? UsArenaReject.QueueFull : UsArenaReject.SessionClosed);
                else if (si < 0 || _syms[si].Seen == 0) Reject(in o, UsArenaReject.NoQuote);
                else FillHuman(in o, si, _syms[si].Ask, _syms[si].Bid);
            }
        }

        /// <summary>[v12.18.71-DEV] 휴먼 주문 거부 1건(거부 계수 · 복제 주문 = 복제 원장 기록).</summary>
        private void Reject(in ArenaAuctionOrder o, int reject)
        {
            _books[ArenaSide.Human].Rejects++;
            if (o.Flags == MarketOpenAuctionGate.FlagCopy) _copy.Record(ArenaCopyKind.Follow, CopyCode(reject), o.Side, o.Symbol, 0);
        }

        /// <summary>[v12.18.71-DEV] 동시호가 창 밖 · 세션 종료 = 미체결 경매 주문 전량 거부(SessionClosed · 이월 0).</summary>
        private void PurgeAuction()
        {
            for (int i = 0; i < _auctionCount; i++)
            {
                Reject(in _auction[i], UsArenaReject.SessionClosed);
                _auctionRejects++;
                _auction[i] = default;
            }
            _auctionCount = 0;
        }

        /// <summary>
        /// [v12.18.71-DEV] 휴먼 주문 1건 체결(buyPx/sellPx = 최우선 호가 · 개장 단일가) + 거부 기록. 일반 매도 = 보유 미만 거부(기존 규칙) ·
        /// 복제 주문 = 기준가 대비 불리한 슬리피지 20bp 초과 취소 · 매도 = 보유 상한(보유 0 = InsufficientPosition) · 매수 체결 = 복제 보유분 가산.
        /// </summary>
        private int FillHuman(in ArenaAuctionOrder o, int si, long buyPx, long sellPx)
        {
            var b = _books[ArenaSide.Human];
            bool copy = o.Flags == MarketOpenAuctionGate.FlagCopy;
            int pi = FindPos(b, o.Symbol);
            long qty = o.Qty, filled;
            int rc;
            if (o.Side < 0 && (pi < 0 || (!copy && b.Positions[pi].Qty < qty))) rc = UsArenaReject.InsufficientPosition;
            else if (copy && ArenaCopyMath.SlippageExceeded(o.Side, o.RefPx, o.Side > 0 ? buyPx : sellPx))
            {
                b.Rejects++;
                _copy.Record(ArenaCopyKind.Follow, ArenaCopyCode.SlippageExceeded, o.Side, o.Symbol, 0);
                return SlippageCancelled;
            }
            else if (o.Side > 0)
            {
                rc = Buy(b, si, qty, buyPx, out filled);
                if (rc == UsArenaReject.None && copy) b.Positions[FindPos(b, o.Symbol)].CopiedQty += filled;
                if (rc == UsArenaReject.None && copy) _copy.Record(ArenaCopyKind.Follow, ArenaCopyCode.None, 1, o.Symbol, filled);
            }
            else
            {
                rc = Sell(b, si, Math.Min(qty, b.Positions[pi].Qty), sellPx, out filled, out _);
                if (rc == UsArenaReject.None && copy) _copy.Record(ArenaCopyKind.Follow, ArenaCopyCode.None, -1, o.Symbol, filled);
            }
            if (rc != UsArenaReject.None) Reject(in o, rc);
            return rc;
        }

        private int Buy(Book b, int si, long qty, long px, out long filled)
        {
            filled = 0;
            ref Sym s = ref _syms[si];
            qty = Math.Min(qty, s.AskQty);
            if (qty <= 0) return UsArenaReject.NoLiquidity;
            long notional = qty * px;
            long fee = notional * FeePer100k / 100_000;
            long cost = notional + fee;
            if (cost > b.Cash) return UsArenaReject.InsufficientCash;
            int pi = FindPos(b, s.Symbol);
            if (pi < 0)
            {
                if (b.Count >= MaxPositions) return UsArenaReject.BookFull;
                pi = b.Count++;
                b.Positions[pi] = new Pos { Symbol = s.Symbol, SymIdx = si };
            }
            ref Pos p = ref b.Positions[pi];
            p.AvgPx = (p.AvgPx * p.Qty + notional) / (p.Qty + qty);
            p.Qty += qty;
            p.Cost += cost;
            b.Cash -= cost; b.Fees += fee; b.Fills++;
            filled = qty;
            return UsArenaReject.None;
        }

        private int Sell(Book b, int si, long qty, long px, out long filled, out long pnl)
        {
            filled = 0; pnl = 0;
            ref Sym s = ref _syms[si];
            int pi = FindPos(b, s.Symbol);
            if (pi < 0) return UsArenaReject.InsufficientPosition;
            ref Pos p = ref b.Positions[pi];
            qty = Math.Min(Math.Min(qty, p.Qty), s.BidQty);
            if (qty <= 0) return UsArenaReject.NoLiquidity;
            long notional = qty * px;
            long fee = notional * FeePer100k / 100_000;
            long sec = notional * SecFeePer10M / 10_000_000;
            long net = notional - fee - sec;
            long basis = p.Cost * qty / p.Qty;
            pnl = net - basis;
            p.Cost -= basis; p.Qty -= qty; p.TradePnl += pnl;
            if (p.CopiedQty > p.Qty) p.CopiedQty = p.Qty;                  // [v12.18.71-DEV] 복제 보유분 ≤ 보유(휴먼 수동 매도 반영)
            b.Cash += net; b.Realized += pnl; b.Fees += fee; b.SecFees += sec; b.Fills++;
            filled = qty;
            if (p.Qty == 0)
            {
                if (p.TradePnl > 0) b.Wins++; else b.Losses++;
                b.Positions[pi] = b.Positions[--b.Count];
                b.Positions[b.Count] = default;
            }
            return UsArenaReject.None;
        }

        private void Record(in AiTradeRationale r)
        {
            _rationales[_rationaleCount % RationaleCapacity] = r;
            _rationaleCount++;
        }

        private long Equity(Book b)
        {
            long eq = b.Cash;
            for (int i = 0; i < b.Count; i++) eq += b.Positions[i].Qty * _syms[b.Positions[i].SymIdx].Bid;
            return eq;
        }

        private DuelBookStats Stats(Book b)
        {
            long eq = Equity(b);
            long cost = 0;
            for (int i = 0; i < b.Count; i++) cost += b.Positions[i].Cost;
            if (eq > b.Peak) b.Peak = eq;
            if (b.Peak > 0) b.MddBp = Math.Max(b.MddBp, (b.Peak - eq) * 10000 / b.Peak);
            return new DuelBookStats(_capital, b.Cash, eq, b.Realized, eq - b.Cash - cost, b.Fees, b.SecFees, b.Peak, b.MddBp, b.Fills, b.Rejects,
                b.Wins, b.Losses, b.Count);
        }

        /// <summary>[v12.18.67-DEV] AI 신규 진입 거버넌스 승인(δ=0 · 차단 사유별 계수).</summary>
        private bool Govern(int si, int score, int momentumBp, int momentumWeight, int imbalanceBp, int imbalanceWeight, long nowTicks, out PlutoGovernanceTag gov,
            bool pocResistance = false, bool cvdBearish = false, bool depthTrap = false)
        {
            ref Sym s = ref _syms[si];
            PlutoCognitiveWeights learned = default;
            Volatile.Read(ref _learning)?.TryRead(out learned);                 // [v12.18.77-DEV] Track A — 학습 가중치 스냅샷 판독(δ=0 · 미결선/경합 = 중립)
            var r = PlutoCognitiveGovernor.Approve(in s.Regime, score, momentumBp, momentumWeight, imbalanceBp, imbalanceWeight, EntryScore, s.BidQty, s.AskQty,
                Volatile.Read(ref _govUs10yBp), Volatile.Read(ref _govDxyBp), in _sizer, nowTicks, out gov, pocResistance, cvdBearish, depthTrap, in learned);
            switch (r)
            {
                case PlutoBlockReason.None: _govApproved++; return true;
                case PlutoBlockReason.PanicRegime: _govPanic++; break;
                case PlutoBlockReason.Cooldown: _govCooldown++; break;
                case PlutoBlockReason.CounterThesisVeto: _govVeto++; break;
            }
            return false;
        }

        /// <summary>[v12.18.67-DEV] AI 청산 거버넌스(δ=0): 전량 청산 = 연패 집계 · 진입 메모 있음 = 자가 회고 태그(없음/정지 = default).</summary>
        private PlutoGovernanceTag GovernExit(in PlutoEntryMemo memo, ArenaTrigger exit, int si, long tradePnl, bool closed, long nowTicks)
        {
            if (!Volatile.Read(ref _governed)) return default;
            if (closed) _sizer.OnClose(tradePnl, nowTicks);
            if (!memo.HasMemo) return default;
            ref Sym s = ref _syms[si];
            var pm = PlutoPostMortemReviewer.Review(in memo, exit, s.Regime.Regime, tradePnl, closed, nowTicks, s.Regime.AtrCbp);
            return PlutoGovernanceTag.ForExit(in s.Regime, _sizer.LossStreak, in pm);
        }

        /// <summary>[v12.18.67-DEV] 진입 회고 메모(진입 근거 순번 = 다음 기록 순번).</summary>
        private PlutoEntryMemo Memo(in PlutoGovernanceTag gov, long nowTicks, long entryCost) => new()
        {
            EntrySeq = _rationaleCount + 1, EntryTicks = nowTicks, EntryCost = entryCost, Penalty = gov.Penalty, Confidence = gov.Confidence,
            WeightBp = gov.WeightBp, Regime = gov.Regime, Theses = gov.Theses, StreakCut = gov.StreakCut,
        };

        private void PublishSnapshot()
        {
            _govSnap = new PlutoGovernorStats(_governed, in _sizer, _govApproved, _govPanic, _govCooldown, _govVeto);
            _copySnap = _copy;
            _auctionSnap = new ArenaAuctionStats(_preOpen, _auctionCount, _auctionFills, _auctionRejects, _gapVetoes, _gapFills);
            var ai = Stats(_books[ArenaSide.Ai]);
            var human = Stats(_books[ArenaSide.Human]);
            _snap = new UsArenaSnapshot(_ticks, _dropped, _rationaleCount, _lastKstTicks, _phase,
                Volatile.Read(ref _qqqBp), Volatile.Read(ref _soxxBp), Volatile.Read(ref _us10yBp), in ai, in human);
        }

        private static int FindPos(Book b, long symbol)
        {
            for (int i = 0; i < b.Count; i++) if (b.Positions[i].Symbol == symbol) return i;
            return -1;
        }

        // ── seqlock(단일 작성자 · 판독 할당 0) ─────────────────────────────────────

        private void BeginWrite() => Interlocked.Increment(ref _ver);
        private void EndWrite() => Interlocked.Increment(ref _ver);

        public bool TryReadSnapshot(out UsArenaSnapshot snapshot)
        {
            for (int i = 0; i < SeqlockRetries; i++)
            {
                long v = Volatile.Read(ref _ver);
                if ((v & 1) != 0) { Thread.SpinWait(8); continue; }
                snapshot = _snap;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v) return true;
            }
            snapshot = default;
            return false;
        }

        /// <summary>진영 보유 사본(센트) — 반환 = 복사 개수(경합 상한 초과 = 0).</summary>
        public int CopyPositions(int side, Span<DuelPosition> destination)
        {
            if (side != ArenaSide.Ai && side != ArenaSide.Human) return 0;
            var b = _books[side];
            for (int i = 0; i < SeqlockRetries; i++)
            {
                long v = Volatile.Read(ref _ver);
                if ((v & 1) != 0) { Thread.SpinWait(8); continue; }
                int n = Math.Min(Math.Min(b.Count, MaxPositions), destination.Length);
                for (int k = 0; k < n; k++)
                {
                    Pos p = b.Positions[k];
                    long mark = (uint)p.SymIdx < MaxSymbols ? _syms[p.SymIdx].Bid : 0;
                    destination[k] = new DuelPosition(p.Symbol, p.Qty, p.AvgPx, p.Cost, mark, p.Target, p.Stop);
                }
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v) return n;
            }
            return 0;
        }

        /// <summary>수신(1회 이상) 종목 최우선 호가 사본(센트 · 표 순서) — 반환 = 복사 개수(경합 상한 초과 = 0).</summary>
        public int CopyQuotes(Span<DuelQuote> destination)
        {
            for (int i = 0; i < SeqlockRetries; i++)
            {
                long v = Volatile.Read(ref _ver);
                if ((v & 1) != 0) { Thread.SpinWait(8); continue; }
                int n = 0;
                for (int k = 0; k < MaxSymbols && n < destination.Length; k++)
                {
                    Sym q = _syms[k];
                    if (q.Seen > 0) destination[n++] = new DuelQuote(q.Symbol, q.Bid, q.Ask, q.BidQty, q.AskQty);
                }
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v) return n;
            }
            return 0;
        }

        /// <summary>최근 AI 근거 피드 사본(과거 → 최신) — 반환 = 복사 개수(경합 상한 초과 = 0).</summary>
        public int CopyRationales(Span<AiTradeRationale> destination)
        {
            for (int i = 0; i < SeqlockRetries; i++)
            {
                long v = Volatile.Read(ref _ver);
                if ((v & 1) != 0) { Thread.SpinWait(8); continue; }
                long total = _rationaleCount;
                int n = (int)Math.Min(Math.Min(total, RationaleCapacity), destination.Length);
                long first = total - n;
                for (int k = 0; k < n; k++) destination[k] = _rationales[(first + k) % RationaleCapacity];
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v) return n;
            }
            return 0;
        }
    }
}
