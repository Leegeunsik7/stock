// ============================================================================
// UpbitCryptoArenaEngine — v12.18.66-DEV: 업비트 가상자산 24/7 아레나(휴먼 vs AI 듀얼 가상 원화 원장 · 설명 가능한 AI 매매) — 아레나 3대 모드 3단계
//
//   ① 종목: CryptoArenaUniverse 4종(KRW-BTC · KRW-ETH · KRW-XRP · KRW-SOL) — 표 밖 마켓 · 호가 격자 비정렬 · 교차/역전 · 잔량 범위 밖 = 폐기(fail-closed).
//     단위(CryptoPaperExchange 와 동일 · 전부 long raw 10^8): 원화 금액 · 가격(원/코인) · 수량(코인 · 1 raw = 0.00000001). 곱셈 = Int128 중간값.
//     진영별 시작 자본 1억 원 독립 원장(현금/보유/손익 공유 0).
//   ② 세션: 24시간 365일 무휴 — CryptoContinuousSession(유닉스 마이크로초 > 0 = ContinuousTrading · 그 외 Invalid = 체결 0).
//     장 마감/청산 시각 없음(SessionClose 사유 미사용). 시각 = 호출자가 넘긴 UTC 유닉스 마이크로초(자체 시계 없음 · 결정론 리플레이).
//     ★호가 신선도: 종목 호가 시각 기준 CryptoPaperExchange.DefaultMaxQuoteAgeMicros(2초) 초과/미래 = 휴먼 체결 StaleQuote 거부(스트림 단절 = 체결 0).
//   ③ 크립토 플루토 두뇌 팩터(bp, 각 ±10000):
//     L2 오더북 불균형 = (매수1 잔량 − 매도1 잔량)/(합) · 24시간 체결 모멘텀 = (중간가 vs 느린 EWMA(α=1/64) × 20 + 테이커 체결 강도)/2 ·
//     김치프리미엄/변동성 감응 = 글로벌 BTC 24h 등락 × 베타 × 10 − 김치프리미엄 × 감응 × 5 − 초과 변동성(|Δ중간가| EWMA − 15bp) × 100.
//     점수 = (40·오더북 + 30·모멘텀 + 30·김프/변동성)/100. 종목별 워밍업 5틱 전 시그널 0.
//     점수 ≥ 3000 → 진입(확신도 = 점수/100 · 목표 +(200+4·확신도)bp · 손절 −150bp · UpbitTickAligner 호가 격자 정렬).
//     보유 중 목표가 도달 / 손절가 이탈 / 점수 ≤ −2000 → 청산. AI 체결 1건마다 AiTradeRationale(Crypto · 오더북 두께 포함) 1건(누락 0).
//   ④ 체결: CryptoPaperExchange 매칭 산식 공유(SlippedMarketPrice · NotionalWithFee) — 시장가 매수 = AlignUp(매도1 × (1+2bp)) ·
//     매도 = AlignDown(매수1 × (1−2bp)) · 대금 매수 올림/매도 내림 · 수수료 0.05%(업비트 KRW 마켓 · 올림 · 양방향) · 최소 주문 5,000원.
//     수량 상한 = 최우선 잔량(부분 체결 · 잔량 폐기 — 큐잉/재시도 0). 공매도 없음.
//     AI 먼지 방지: 진입 대금 ≥ 최소 주문 2배 · 부분 청산은 잔여 보유가 최소 주문 2배 이상 남도록 축소(ExitQty — 축소 불가 = 다음 호가 대기). 양 진영은 같은 호가를 '각자' 소비(진영 간 간섭 0).
//   ⑤ 휴먼 주문: SubmitHuman(임의 스레드) → 고정 링(Vyukov 유계 MPSC · 포화 = QueueFull 폐기) → 정산 스레드가 다음 OnQuote/Drain 에서 체결.
//   ⑥ 게시: 정산 1회 전체가 단일 seqlock 쓰기 창(홀수 = 기록 중). 스냅샷 · 보유 · 호가 · 근거 피드 판독은 같은 버전으로 검증.
//
//   ★δ=0: OnQuote/OnTrade/Drain/SubmitHuman/판독 경로 힙 할당 0(고정 배열 · 값 타입 · Int128 · LINQ/박싱/클로저/문자열 0).
//   ★단일 작성자: OnQuote/OnTrade/Drain/SetCapital 은 정산 스레드 1개만. ★liveArmed:false — 업비트 주문/인증 API 참조 0(순수 인메모리 가상 대결).
//   [v12.18.67-DEV] 플루토 거버넌스(EnableGovernance · 선택): 마켓별 국면 판정(매 유효 호가 · Int128 EWMA) → AI 진입 직전 PlutoCognitiveGovernor(국면 재가중 · 패닉 차단 ·
//     연패 쿨다운(엔진 KST 시각) · 악마의 변호인 · 동적 비중 5~25%) · 청산 = 자가 회고 + 연패 집계 · 근거 Governance 원자 게시. 정지(기본) = 기존 동작 동일.
//   [v12.18.71-DEV] 플루토 카피 트레이딩: SubmitFollow(기준가 = AI 체결가(슬리피지 반영) 대비 불리한 괴리 20bp 초과 = 취소 · 매도 = 보유 상한) ·
//     SetAutoCopy(비례 미러링 · 예수금 부족/최소 주문 미만 = Fail-Closed · 복제분 동반 청산). 24/7 시장 = 시초가 개장 게이트 없음. 정지(기본) = 기존 동작 동일.
//   [v12.18.73-DEV] 플루토 기술적 팩터(EnableTechnical · 선택): 매 유효 호가 = 마켓별 1분 봉(중간가 · KST 시각) · 매 체결 = 매물대 프로파일(체결 수량 raw) 갱신(δ=0 · Int128) →
//     팩터 게시(TryReadTechnical) · 가동 시 점수 × 100bp 를 TechnicalWeight(20)로 가산 · 기술적 기여 최대 = VolatilitySqueezeBreakout / VolumeProfileSupport 진입 ·
//     POC 매물벽 = 악마의 변호인 반대 논리(거버너 가동 시). 정지(기본) = 기존 동작 동일.
//   [v12.18.75-DEV] 플루토 미시구조 팩터(EnableMicrostructure · 선택): 매 체결 = 실측 ask_bid 방향(BID = 매수 테이커) × 체결 수량 raw CVD 누적(KST 1분 봉) ·
//     매 유효 호가 = L1 잔량 뎁스 판독(피드 = orderbook_units[0] → 제공 범위 비례 가중) → 팩터 게시(TryReadMicrostructure) · 가동 시 점수 × 100bp 를
//     MicrostructureWeight(20)로 가산 · 기여 최대 = CvdBullishDivergence / OrderbookDepthSupport 진입 · 약세 다이버전스 · 매도벽/허매수 = 악마의 변호인 ⑤⑥.
//   [v12.18.76-DEV] 15호가 뎁스 결속(OnDepthQuote): 업비트 orderbook_units 최대 15단 잔량 열 → PlutoOrderbookDepthAnalyzer 직접 판독(δ=0 · span) →
//     매수벽 지지(+10) · 매도벽(−15) · 원거리 허매수(−10) 실가동 · 매도벽/허매수 = 악마의 변호인 ⑥ DepthTrap(−20) · 근거 뱃지에 벽 위치 호가.
//     잔량 열 검증(ValidDepthTiers): [0] = L1 잔량 일치 · 0 ≤ 잔량 ≤ 상한인 연속 접두만 · 불일치/2단 미만 = L1 판독(기존 동작 · fail-closed).
// ============================================================================
using System;
using System.Threading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Brokers.Crypto;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Learning;
using StockManager.Engines.Cognitive.Microstructure;
using StockManager.Engines.Cognitive.Regime;
using StockManager.Engines.Cognitive.Technical;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분
using StockManager.Engines.Feeds;
using StockManager.Engines.Pricing;

namespace StockManager.Engines.Arena.Crypto
{
    /// <summary>크립토 알파 시그널 1회 평가 결과(bp). Dominant = 점수 방향으로 가장 크게 기여한 구성요소.</summary>
    public readonly struct CryptoAlphaSignal
    {
        public readonly int Score, ImbalanceBp, MomentumBp, FlowBp, KimchiVolBp, TechnicalBp, MicrostructureBp;
        public readonly ArenaTrigger Dominant;

        public CryptoAlphaSignal(int score, int imbalanceBp, int momentumBp, int flowBp, int kimchiVolBp, ArenaTrigger dominant, int technicalBp = 0,
            int microstructureBp = 0)
        {
            Score = score; ImbalanceBp = imbalanceBp; MomentumBp = momentumBp; FlowBp = flowBp; KimchiVolBp = kimchiVolBp; Dominant = dominant; TechnicalBp = technicalBp;
            MicrostructureBp = microstructureBp;
        }
    }

    /// <summary>크립토 아레나 스냅샷(정산 1회 = 버전 1 증가 · 원장 금액 = 원 raw 10^8 · DuelBookStats.TaxWon = 0).</summary>
    public readonly struct CryptoArenaSnapshot
    {
        public readonly long Ticks, Trades, DroppedQuotes, RationaleCount, LastKstTicks, LastUtcMicros;
        public readonly CryptoSessionPhase Phase;
        public readonly int KimchiPremiumBp, GlobalChangeBp;
        public readonly DuelBookStats Ai, Human;

        public CryptoArenaSnapshot(long ticks, long trades, long droppedQuotes, long rationaleCount, long lastKstTicks, long lastUtcMicros,
            CryptoSessionPhase phase, int kimchiPremiumBp, int globalChangeBp, in DuelBookStats ai, in DuelBookStats human)
        {
            Ticks = ticks; Trades = trades; DroppedQuotes = droppedQuotes; RationaleCount = rationaleCount; LastKstTicks = lastKstTicks;
            LastUtcMicros = lastUtcMicros; Phase = phase; KimchiPremiumBp = kimchiPremiumBp; GlobalChangeBp = globalChangeBp; Ai = ai; Human = human;
        }

        public bool Tradable => Phase == CryptoSessionPhase.ContinuousTrading;
        public DuelBookStats Side(int side) => side == ArenaSide.Human ? Human : Ai;
    }

    public sealed class UpbitCryptoArenaEngine
    {
        public const long Scale = UpbitTickAligner.Scale;
        public const long DefaultCapitalWon = 100_000_000;     // 1억 원
        public const long MinCapitalWon = 1_000_000, MaxCapitalWon = 10_000_000_000;
        public const int MaxSymbols = CryptoArenaUniverse.Count, MaxPositions = CryptoArenaUniverse.Count, MaxAiPositions = 2;
        public const int RationaleCapacity = 128, HumanQueueCapacity = 64;
        public const int FeeBp = CryptoPaperExchange.DefaultFeeBp, SlippageBp = CryptoPaperExchange.DefaultSlippageBp;
        public const long MaxQuoteAgeMicros = CryptoPaperExchange.DefaultMaxQuoteAgeMicros;
        public const long MaxOrderQtyRaw = 1_000_000_000L * Scale, MaxBookQtyRaw = 10_000_000_000L * Scale, MaxPriceRaw = 10_000_000_000L * Scale;
        public const int WarmupTicks = 5;
        public const int EntryScore = 3000, ExitScore = -2000;
        public const int ImbalanceWeight = 40, MomentumWeight = 30, KimchiVolWeight = 30;
        public const int MicrostructureWeight = 20;   // [v12.18.75-DEV] 플루토 미시구조 팩터 가산 가중(점수 ±100 → 점수 ±2000)
        public const int TechnicalWeight = 20;   // [v12.18.73-DEV] 플루토 기술적 팩터 가산 가중(점수 ±100 → 점수 ±2000)
        public const int MomentumGain = 20, GlobalGain = 10, KimchiGain = 5, VolFloorBp = 15, VolGain = 100;
        public const int StopBp = 150, TargetBaseBp = 200, TargetPerConfidenceBp = 4, PositionWeightBp = 3000;
        public static readonly long MaxUtcMicros = (DateTime.MaxValue.Ticks - DateTime.UnixEpoch.Ticks - KstOffsetTicks) / 10;

        private const long KstOffsetTicks = 9 * TimeSpan.TicksPerHour;
        private const int EmaShift = 6;          // 느린 EWMA α = 1/64(24시간 추세 대용)
        private const int FlowShift = 4;         // 테이커 체결량 EWMA α = 1/16
        private const int VolShift = 4;          // |Δ중간가| EWMA α = 1/16 · 값 ×16 고정소수점
        private const int SeqlockRetries = 64;
        private const int QueueMask = HumanQueueCapacity - 1;

        private struct Sym { public long Symbol, Bid, Ask, BidQty, AskQty, QuoteUtc, EmaMid, LastMid, BuyFlow, SellFlow; public int VolX16, Seen; public PlutoRegimeState Regime; }
        private struct Pos { public long Symbol, Qty, AvgPx, Cost, Target, Stop, TradePnl, CopiedQty; public int SymIdx, RiskRewardX100; public PlutoEntryMemo Memo; }
        private struct HumanCell { public long Seq, Symbol, Qty, RefPx; public int Side, Flags; }
        private bool _copyAuto;                                                   // [v12.18.71-DEV] 플루토 자동 미러링
        private int _copyRatioBp = ArenaCopyMath.DefaultRatioBp;
        private ArenaCopyLedger _copy, _copySnap;

        private sealed class Book
        {
            public readonly Pos[] Positions = new Pos[MaxPositions];
            public int Count, Wins, Losses;
            public long Cash, Realized, Fees, Peak, MddBp, Fills, Rejects;
        }

        private readonly Sym[] _syms = new Sym[MaxSymbols];
        private readonly Book[] _books = { new Book(), new Book() };   // ArenaSide.Ai 0 · Human 1
        private readonly AiTradeRationale[] _rationales = new AiTradeRationale[RationaleCapacity];
        private long _rationaleCount;
        private readonly HumanCell[] _hq = new HumanCell[HumanQueueCapacity];
        private long _hqEnq, _hqDeq;
        private long _capitalRaw;
        private long _ticks, _trades, _dropped, _lastKstTicks, _lastUtc;
        private CryptoSessionPhase _phase = CryptoSessionPhase.Invalid;
        private int _kimchiBp, _globalBp;
        private bool _governed;
        private readonly PlutoTechnicalState[] _tech = new PlutoTechnicalState[MaxSymbols];      // [v12.18.73-DEV] 인덱스 = CryptoArenaUniverse 인덱스
        private readonly PlutoTechnicalFactor[] _techLast = new PlutoTechnicalFactor[MaxSymbols];
        private bool _technical;
        private readonly PlutoMicrostructureState[] _micro = new PlutoMicrostructureState[MaxSymbols];   // [v12.18.75-DEV] 인덱스 = CryptoArenaUniverse 인덱스
        private readonly PlutoMicrostructureFactor[] _microLast = new PlutoMicrostructureFactor[MaxSymbols];
        private bool _microstructure;
        private int _govUs10yBp, _govDxyBp;
        private PlutoCognitiveWeightSeqlock? _learning;
        private PlutoRegimeMatcher? _regimeMatcher;                         // [v12.18.82-DEV] 12대 역사적 국면 판정기(Track B 게시 · 정산 스레드 δ=0 판독)
        private PlutoSizerState _sizer;
        private long _govApproved, _govPanic, _govCooldown, _govVeto;
        private PlutoGovernorStats _govSnap;
        private long _ver;
        private CryptoArenaSnapshot _snap;

        public UpbitCryptoArenaEngine(long capitalWon = DefaultCapitalWon)
        {
            for (int i = 0; i < HumanQueueCapacity; i++) _hq[i].Seq = i;
            SetCapital(capitalWon);
        }

        public string ModeName => "가상자산(Upbit ₿)";
        public long CapitalRaw => Interlocked.Read(ref _capitalRaw);
        public long CapitalWon => CapitalRaw / Scale;
        public long Version => Volatile.Read(ref _ver);

        /// <summary>양 진영 원장 · 종목 상태 초기화(정산 스레드 · 대결 시작 전/시세 출처 전환). 범위 밖(100만~100억 원) = ArgumentOutOfRange.</summary>
        public void SetCapital(long capitalWon)
        {
            if (capitalWon < MinCapitalWon || capitalWon > MaxCapitalWon) throw new ArgumentOutOfRangeException(nameof(capitalWon));
            long raw = capitalWon * Scale;
            BeginWrite();
            Interlocked.Exchange(ref _capitalRaw, raw);
            for (int s = 0; s < ArenaSide.Count; s++)
            {
                var b = _books[s];
                Array.Clear(b.Positions);
                b.Count = b.Wins = b.Losses = 0;
                b.Cash = b.Peak = raw;
                b.Realized = b.Fees = b.MddBp = b.Fills = b.Rejects = 0;
            }
            Array.Clear(_rationales);
            _sizer = default;                         // [v12.18.67-DEV] 연패/쿨다운 · 거버넌스 계수 초기화
            _govApproved = _govPanic = _govCooldown = _govVeto = 0;
            _rationaleCount = 0;
            for (int i = 0; i < MaxSymbols; i++) _syms[i] = new Sym { Symbol = CryptoArenaUniverse.Symbol(i) };
            Array.Clear(_tech);                       // [v12.18.73-DEV] 봉 · 매물대 프로파일 · 게시 팩터 동반 초기화
            Array.Clear(_techLast);
            Array.Clear(_micro);                      // [v12.18.75-DEV] CVD · 뎁스 · 게시 팩터 동반 초기화
            Array.Clear(_microLast);
            _copy = default;                          // [v12.18.71-DEV] 복제 계수 초기화(자동 미러링 설정은 유지)
            _ticks = _trades = _dropped = 0;
            PublishSnapshot();
            EndWrite();
        }

        /// <summary>시장 맥락(bp · ±10000 클램프 · 다음 정산부터 반영): 김치프리미엄(업비트 vs 해외 BTC 괴리) · 글로벌 BTC 24시간 등락.</summary>
        public void SetMarketContext(int kimchiPremiumBp, int globalChangeBp)
        {
            Volatile.Write(ref _kimchiBp, Math.Clamp(kimchiPremiumBp, -10000, 10000));
            Volatile.Write(ref _globalBp, Math.Clamp(globalChangeBp, -10000, 10000));
        }

        /// <summary>[v12.18.67-DEV] 플루토 거버넌스 가동/정지(국면 · 악마의 변호인 · 동적 사이징 · 자가 회고 · 다음 정산부터 · 정지 = 기존 동작 동일).</summary>
        public void EnableGovernance(bool enabled) => Volatile.Write(ref _governed, enabled);

        /// <summary>[v12.18.77-DEV] 플루토 지속 학습 가중치 결속(Track B 스웜이 핫스왑 · 정산 스레드는 seqlock 판독만 · δ=0).</summary>
        public void AttachLearning(PlutoCognitiveWeightSeqlock weights) => Volatile.Write(ref _learning, weights ?? throw new ArgumentNullException(nameof(weights)));

        public bool LearningAttached => Volatile.Read(ref _learning) is not null;

        /// <summary>[v12.18.82-DEV] 역사적 국면 판정기 결속(Track B 스웜이 게시 · 정산 스레드는 seqlock 판독만 · 유사도 ≥ 60% 국면 전략 → 학습 가중치 편향 · δ=0).</summary>
        public void AttachRegimeMatcher(PlutoRegimeMatcher matcher) => Volatile.Write(ref _regimeMatcher, matcher ?? throw new ArgumentNullException(nameof(matcher)));

        public bool RegimeMatcherAttached => Volatile.Read(ref _regimeMatcher) is not null;

        public bool GovernanceEnabled => Volatile.Read(ref _governed);

        /// <summary>[v12.18.73-DEV] 플루토 기술적 팩터 시그널 가산 가동/정지(봉 · 프로파일 갱신과 팩터 게시는 상시 · 다음 정산부터 · 정지 = 기존 동작 동일).</summary>
        public void EnableTechnical(bool enabled) => Volatile.Write(ref _technical, enabled);

        public bool TechnicalEnabled => Volatile.Read(ref _technical);

        /// <summary>[v12.18.75-DEV] 플루토 미시구조 팩터 시그널 가산 가동/정지(CVD · 뎁스 갱신과 팩터 게시는 상시 · 다음 정산부터 · 정지 = 기존 동작 동일).</summary>
        public void EnableMicrostructure(bool enabled) => Volatile.Write(ref _microstructure, enabled);

        public bool MicrostructureEnabled => Volatile.Read(ref _microstructure);

        /// <summary>[v12.18.75-DEV] 마켓 최신 미시구조 팩터(직전 정산 판정 · seqlock · δ=0 · 표 밖/경합 상한 초과 = false).</summary>
        public bool TryReadMicrostructure(long symbol, out PlutoMicrostructureFactor factor)
        {
            int i = CryptoArenaUniverse.IndexOf(symbol);
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

        /// <summary>[v12.18.73-DEV] 마켓 최신 기술적 팩터(직전 정산 중간가 판정 · seqlock · δ=0 · 표 밖/경합 상한 초과 = false).</summary>
        public bool TryReadTechnical(long symbol, out PlutoTechnicalFactor factor)
        {
            int i = CryptoArenaUniverse.IndexOf(symbol);
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
                int i = CryptoArenaUniverse.IndexOf(symbol);
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

        /// <summary>휴먼 시장가 주문 적재(side +1 매수 · −1 매도 · 수량 = 코인 raw 10^8). 반환 = CryptoArenaReject(접수 단계).</summary>
        public int SubmitHuman(long symbol, int side, long qtyRaw) => Enqueue(symbol, side, qtyRaw, 0, 0);

        /// <summary>[v12.18.71-DEV] 1-클릭 복제 주문(refPxRaw = AI 체결가 · 체결 시 불리한 슬리피지 20bp 초과 = 취소 · 매도 = 보유 상한). 반환 = CryptoArenaReject(접수 단계).</summary>
        public int SubmitFollow(long symbol, int side, long qtyRaw, long refPxRaw)
            => refPxRaw <= 0 || refPxRaw > MaxPriceRaw ? CryptoArenaReject.Invalid : Enqueue(symbol, side, qtyRaw, refPxRaw, MarketOpenAuctionGate.FlagCopy);

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

        private int Enqueue(long symbol, int side, long qtyRaw, long refPx, int flags)
        {
            if (CryptoArenaUniverse.IndexOf(symbol) < 0 || (side != 1 && side != -1) || qtyRaw <= 0 || qtyRaw > MaxOrderQtyRaw) return CryptoArenaReject.Invalid;
            long pos = Volatile.Read(ref _hqEnq);
            while (true)
            {
                ref HumanCell c = ref _hq[pos & QueueMask];
                long dif = Volatile.Read(ref c.Seq) - pos;
                if (dif == 0)
                {
                    if (Interlocked.CompareExchange(ref _hqEnq, pos + 1, pos) == pos)
                    {
                        c.Symbol = symbol; c.Side = side; c.Qty = qtyRaw; c.RefPx = refPx; c.Flags = flags;
                        Volatile.Write(ref c.Seq, pos + 1);
                        return CryptoArenaReject.None;
                    }
                    pos = Volatile.Read(ref _hqEnq);
                }
                else if (dif < 0) return CryptoArenaReject.QueueFull;
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

        // ── 24/7 세션 시계 ───────────────────────────────────────────────────────

        /// <summary>UTC 유닉스 마이크로초의 세션 페이즈(δ=0 · CryptoContinuousSession 위임 · DateTime 표현 범위 밖 = Invalid).</summary>
        public static CryptoSessionPhase PhaseAt(long utcMicros)
            => utcMicros > MaxUtcMicros ? CryptoSessionPhase.Invalid : CryptoContinuousSession.PhaseAtUtcMicros(utcMicros);

        /// <summary>UTC 유닉스 마이크로초 → KST ticks(범위 밖 = 0).</summary>
        public static long KstTicksOf(long utcMicros)
            => utcMicros <= 0 || utcMicros > MaxUtcMicros ? 0 : DateTime.UnixEpoch.Ticks + utcMicros * 10 + KstOffsetTicks;

        private void Clock(long utcMicros)
        {
            _phase = PhaseAt(utcMicros);
            if (_phase != CryptoSessionPhase.ContinuousTrading) return;
            if (utcMicros > _lastUtc) _lastUtc = utcMicros;
            _lastKstTicks = KstTicksOf(_lastUtc);
        }

        // ── 정산(단일 작성자) ────────────────────────────────────────────────────

        /// <summary>시세 없이 휴먼 큐만 체결(최신 호가 · 신선도 판정 · 세션 판정 갱신).</summary>
        public void Drain(long utcMicros)
        {
            BeginWrite();
            Clock(utcMicros);
            DrainHuman(utcMicros);
            PublishSnapshot();
            EndWrite();
        }

        /// <summary>
        /// 종목 체결 1건(가격 · 수량 raw · side +1 매수 테이커(BID) · −1 매도 테이커(ASK)) → 테이커 체결 강도 EWMA 갱신.
        /// 표 밖 마켓 · 가격/수량 무효 · 세션 무효 = 폐기(DroppedQuotes 증가).
        /// </summary>
        public void OnTrade(long symbol, long priceRaw, long volumeRaw, int side, long utcMicros)
        {
            BeginWrite();
            Clock(utcMicros);
            int si = -1;
            if (_phase == CryptoSessionPhase.ContinuousTrading && priceRaw > 0 && priceRaw <= MaxPriceRaw && volumeRaw > 0 && volumeRaw <= MaxBookQtyRaw && (side == 1 || side == -1))
                si = CryptoArenaUniverse.IndexOf(symbol);
            if (si < 0) _dropped++;
            else
            {
                _trades++;
                ref Sym s = ref _syms[si];
                if (side > 0) { s.BuyFlow += (volumeRaw - s.BuyFlow) >> FlowShift; s.SellFlow -= s.SellFlow >> FlowShift; }
                else { s.SellFlow += (volumeRaw - s.SellFlow) >> FlowShift; s.BuyFlow -= s.BuyFlow >> FlowShift; }
                PlutoTechnicalAnalyzer.OnVolume(ref _tech[si], priceRaw, volumeRaw);          // [v12.18.73-DEV] 체결가 구간 += 체결 수량
                if (s.LastMid > 0) _techLast[si] = PlutoTechnicalAnalyzer.Assess(in _tech[si], s.Symbol, s.LastMid);
                PlutoMicrostructureAnalyzer.OnTrade(ref _micro[si], side, volumeRaw, priceRaw, KstTicksOf(utcMicros));   // [v12.18.75-DEV] 실측 테이커 방향 CVD
                if (s.LastMid > 0) _microLast[si] = PlutoMicrostructureAnalyzer.Assess(in _micro[si], s.Symbol, s.LastMid);
            }
            PublishSnapshot();
            EndWrite();
        }

        /// <summary>
        /// 종목 1틱(최우선 호가 raw · 잔량 raw) 정산: 세션 판정 → 시그널 평가 → 휴먼 큐 체결 → AI 진입/청산(+근거) → 시가평가 게시.
        /// 표 밖 마켓 · 호가 격자 비정렬 · 매수 ≥ 매도 · 잔량 범위 밖 · 세션 무효 = 폐기(DroppedQuotes 증가 · fail-closed).
        /// </summary>
        public void OnQuote(long symbol, long bidRaw, long askRaw, long bidQtyRaw, long askQtyRaw, long utcMicros)
            => OnDepthQuote(symbol, bidRaw, askRaw, bidQtyRaw, askQtyRaw, utcMicros, default, default);

        /// <summary>
        /// [v12.18.76-DEV] OnQuote + 호가별 잔량 열(raw · [0] = 최우선 · 최대 15단). 유효 2단 이상 = 다단 뎁스 판독(벽/허매수) · 그 외 = L1 판독(OnQuote 동일).
        /// </summary>
        public void OnDepthQuote(long symbol, long bidRaw, long askRaw, long bidQtyRaw, long askQtyRaw, long utcMicros,
            ReadOnlySpan<long> bidDepthQtyRaw, ReadOnlySpan<long> askDepthQtyRaw)
        {
            BeginWrite();
            Clock(utcMicros);
            int si = -1;
            if (_phase == CryptoSessionPhase.ContinuousTrading && askRaw <= MaxPriceRaw && bidRaw < askRaw
                && UpbitTickAligner.IsAligned(bidRaw) && UpbitTickAligner.IsAligned(askRaw)
                && bidQtyRaw >= 0 && askQtyRaw >= 0 && bidQtyRaw <= MaxBookQtyRaw && askQtyRaw <= MaxBookQtyRaw)
                si = CryptoArenaUniverse.IndexOf(symbol);
            if (si < 0) _dropped++;
            else
            {
                _ticks++;
                ref Sym s = ref _syms[si];
                s.Bid = bidRaw; s.Ask = askRaw; s.BidQty = bidQtyRaw; s.AskQty = askQtyRaw; s.QuoteUtc = utcMicros;
                PlutoRegimeClassifier.Update(ref s.Regime, bidRaw, askRaw, bidQtyRaw, askQtyRaw);
                long mid = bidRaw + (askRaw - bidRaw) / 2;
                PlutoTechnicalAnalyzer.OnPrice(ref _tech[si], mid, KstTicksOf(utcMicros));
                _techLast[si] = PlutoTechnicalAnalyzer.Assess(in _tech[si], s.Symbol, mid);
                PlutoTechnicalFactor tech = Volatile.Read(ref _technical) ? _techLast[si] : default;
                int tiers = ValidDepthTiers(bidQtyRaw, askQtyRaw, bidDepthQtyRaw, askDepthQtyRaw);
                if (tiers > 1) PlutoMicrostructureAnalyzer.OnDepth(ref _micro[si], bidDepthQtyRaw.Slice(0, tiers), askDepthQtyRaw.Slice(0, tiers));   // [v12.18.76-DEV] 15호가
                else PlutoMicrostructureAnalyzer.OnL1(ref _micro[si], bidQtyRaw, askQtyRaw);
                _microLast[si] = PlutoMicrostructureAnalyzer.Assess(in _micro[si], s.Symbol, mid);
                PlutoMicrostructureFactor micro = Volatile.Read(ref _microstructure) ? _microLast[si] : default;
                if (s.LastMid > 0)
                {
                    int absBp = (int)Int128.Min(Int128.Abs((Int128)(mid - s.LastMid) * 10000 / s.LastMid), 10000);
                    s.VolX16 += ((absBp << VolShift) - s.VolX16) >> VolShift;
                }
                s.LastMid = mid;
                var sig = s.Seen >= WarmupTicks
                    ? Evaluate(si, Volatile.Read(ref _kimchiBp), Volatile.Read(ref _globalBp), bidQtyRaw, askQtyRaw, mid, s.EmaMid, s.BuyFlow, s.SellFlow, s.VolX16 >> VolShift,
                        tech.TechnicalBp, tech.IsBreakoutUp, micro.MicrostructureBp, micro.IsBullishDivergence)
                    : default;
                if (s.Seen == 0) s.EmaMid = mid;
                else s.EmaMid += (mid - s.EmaMid) >> EmaShift;
                if (s.Seen < int.MaxValue) s.Seen++;

                DrainHuman(utcMicros);
                StepAi(si, in sig, in tech, in micro);
            }
            PublishSnapshot();
            EndWrite();
        }

        /// <summary>
        /// 순수 시그널 평가(할당 0). index = CryptoArenaUniverse 인덱스 · mid/emaMid = 원 raw · buyFlow/sellFlow = 테이커 체결량 EWMA raw ·
        /// volBp = |Δ중간가| EWMA(bp).
        /// </summary>
        public static CryptoAlphaSignal Evaluate(int index, int kimchiPremiumBp, int globalChangeBp, long bidQtyRaw, long askQtyRaw, long midRaw, long emaMidRaw,
            long buyFlowRaw, long sellFlowRaw, int volBp, int technicalBp = 0, bool squeezeBreakout = false, int microstructureBp = 0, bool cvdDivergence = false)
        {
            if ((uint)index >= MaxSymbols) return default;
            long kimchi = Math.Clamp(kimchiPremiumBp, -10000, 10000), global = Math.Clamp(globalChangeBp, -10000, 10000);
            long depth = bidQtyRaw + askQtyRaw;
            int obi = depth <= 0 || bidQtyRaw < 0 || askQtyRaw < 0 ? 0 : (int)((Int128)(bidQtyRaw - askQtyRaw) * 10000 / depth);
            int priceMom = emaMidRaw <= 0 ? 0 : (int)Int128.Clamp((Int128)(midRaw - emaMidRaw) * 10000 * MomentumGain / emaMidRaw, -10000, 10000);
            long flowSum = buyFlowRaw + sellFlowRaw;
            int flow = flowSum <= 0 || buyFlowRaw < 0 || sellFlowRaw < 0 ? 0 : (int)((Int128)(buyFlowRaw - sellFlowRaw) * 10000 / flowSum);
            int mom = (priceMom + flow) / 2;
            long excessVol = Math.Max(0, Math.Clamp(volBp, 0, 10000) - VolFloorBp);
            int kv = (int)Math.Clamp(global * CryptoArenaUniverse.GlobalBetaX100(index) / 100 * GlobalGain
                                     - kimchi * CryptoArenaUniverse.KimchiSensX100(index) / 100 * KimchiGain
                                     - excessVol * VolGain, -10000, 10000);
            int technical = Math.Clamp(technicalBp, -10000, 10000);
            int micro = Math.Clamp(microstructureBp, -10000, 10000);
            long cObi = (long)obi * ImbalanceWeight, cMom = (long)mom * MomentumWeight, cKv = (long)kv * KimchiVolWeight, cTech = (long)technical * TechnicalWeight;
            long cMicro = (long)micro * MicrostructureWeight;
            int score = (int)Math.Clamp((cObi + cMom + cKv + cTech + cMicro) / 100, -10000, 10000);
            int sign = score >= 0 ? 1 : -1;
            var dom = ArenaTrigger.OrderbookImbalance;
            long best = cObi * sign;
            if (cMom * sign > best) { best = cMom * sign; dom = ArenaTrigger.TradeFlowMomentum; }
            if (cKv * sign > best) { best = cKv * sign; dom = ArenaTrigger.KimchiVolatility; }
            if (cTech * sign > best) { best = cTech * sign; dom = squeezeBreakout ? ArenaTrigger.VolatilitySqueezeBreakout : ArenaTrigger.VolumeProfileSupport; }
            if (cMicro * sign > best) dom = cvdDivergence ? ArenaTrigger.CvdBullishDivergence : ArenaTrigger.OrderbookDepthSupport;
            return new CryptoAlphaSignal(score, obi, mom, flow, kv, dom, technical, micro);
        }

        private void StepAi(int si, in CryptoAlphaSignal sig, in PlutoTechnicalFactor tech, in PlutoMicrostructureFactor micro)
        {
            var b = _books[ArenaSide.Ai];
            ref Sym s = ref _syms[si];
            long kst = KstTicksOf(s.QuoteUtc);
            long bidDepth = DepthWon(s.BidQty, s.Bid), askDepth = DepthWon(s.AskQty, s.Ask);
            int pi = FindPos(b, s.Symbol);
            if (pi >= 0)
            {
                ref Pos p = ref b.Positions[pi];
                ArenaTrigger exit = ArenaTrigger.None;
                if (s.Bid >= p.Target) exit = ArenaTrigger.TakeProfit;
                else if (s.Bid <= p.Stop) exit = ArenaTrigger.StopLoss;
                else if (sig.Score <= ExitScore) exit = ArenaTrigger.SignalReversal;
                if (exit == ArenaTrigger.None) return;
                long target = p.Target, stop = p.Stop, priorPnl = p.TradePnl; int rr = p.RiskRewardX100;
                var memo = p.Memo;
                long entry = p.AvgPx;                                       // [v12.18.70-DEV] 청산 진입가(Sell 전 판독 · 전량 청산 시 슬롯 회수)
                long sellQty = ExitQty(p.Qty, s.BidQty, s.Bid);
                if (sellQty <= 0) return;                                   // 매수1 잔량 부족 — 먼지 잔량 대신 다음 호가 대기
                long heldBefore = p.Qty;
                if (Sell(b, si, sellQty, out long filled, out long exitPx, out long pnl) != CryptoArenaReject.None) return;
                int conf = exit == ArenaTrigger.SignalReversal ? Math.Clamp(-sig.Score / 100, 0, 100) : 100;
                var exitGov = GovernExit(in memo, exit, si, priorPnl + pnl, FindPos(b, s.Symbol) < 0, kst);
                Record(new AiTradeRationale(_rationaleCount + 1, s.Symbol, -1, filled, exitPx, kst, exit, sig.Score, conf,
                    target, stop, rr, sig.KimchiVolBp, sig.ImbalanceBp, sig.FlowBp, sig.MomentumBp, pnl, ArenaCurrency.Crypto,
                    default, default, bidDepth, askDepth, in exitGov, entry, in tech, in micro));
                if (Volatile.Read(ref _copyAuto)) MirrorExit(si, filled, heldBefore);
                return;
            }
            if (b.Count >= MaxAiPositions) return;
            int score = sig.Score, confidence, weightBp = PositionWeightBp;
            PlutoGovernanceTag gov = default;
            if (Volatile.Read(ref _governed))
            {
                if (!Govern(si, sig.Score, sig.MomentumBp, MomentumWeight, sig.ImbalanceBp, ImbalanceWeight, kst, out gov, tech.IsPocResistance, micro.IsBearishDivergence, micro.IsDepthTrap)) return;
                score = gov.AdjustedScore; confidence = gov.Confidence; weightBp = gov.WeightBp;
            }
            else
            {
                if (sig.Score < EntryScore) return;
                confidence = Math.Clamp(sig.Score / 100, 0, 100);
            }
            long tgt = AlignUpRaw(s.Ask, TargetBaseBp + TargetPerConfidenceBp * confidence);
            long stp = AlignDownRaw(s.Ask, -StopBp);
            if (stp <= 0 || stp >= s.Ask || tgt <= s.Ask) return;
            if (CryptoPaperExchange.SlippedMarketPrice(CryptoOrderSide.Buy, s.Bid, s.Ask, SlippageBp, out long fillPx) != CryptoPaperStatus.Filled || fillPx >= tgt) return;
            int rrX100 = (int)Math.Min((tgt - fillPx) * 100 / (fillPx - stp), 100_000);
            if (rrX100 <= 0) return;
            long budget = Math.Min(b.Cash, (long)((Int128)Equity(b) * weightBp / 10000)) - Scale;   // 1원 여유(대금/수수료 올림)
            if (budget <= 0) return;
            long qty = Math.Min(s.AskQty, (long)Int128.Min((Int128)budget * Scale * 10000 / ((Int128)fillPx * (10000 + FeeBp) + 1), long.MaxValue));
            if (qty <= 0 || (Int128)qty * fillPx / Scale < CryptoPaperExchange.MinOrderKrwRaw * 2) return;   // 매도1 잔량 상한 · 진입 대금 ≥ 최소 주문 2배(먼지 포지션 방지)
            long aiEquity = Equity(b);
            if (Buy(b, si, qty, out long bought, out long px) != CryptoArenaReject.None) return;
            ref Pos np = ref b.Positions[FindPos(b, s.Symbol)];
            np.Target = tgt; np.Stop = stp; np.RiskRewardX100 = rrX100;
            if (gov.Governed) np.Memo = Memo(in gov, kst, np.Cost);
            Record(new AiTradeRationale(_rationaleCount + 1, s.Symbol, 1, bought, px, kst, sig.Dominant, score, confidence,
                tgt, stp, rrX100, sig.KimchiVolBp, sig.ImbalanceBp, sig.FlowBp, sig.MomentumBp, 0, ArenaCurrency.Crypto,
                default, default, bidDepth, askDepth, in gov, 0, in tech, in micro));
            if (Volatile.Read(ref _copyAuto)) MirrorEntry(si, bought, aiEquity);
        }

        /// <summary>[v12.18.71-DEV] 자동 미러링 진입(같은 쓰기 창 · 같은 호가 · 진입 직전 자본 비례 수량 · 예수금 부족/최소 주문 미만 = Fail-Closed).</summary>
        private void MirrorEntry(int si, long aiQty, long aiEquity)
        {
            ref Sym s = ref _syms[si];
            var h = _books[ArenaSide.Human];
            long qty = ArenaCopyMath.ScaleQty(aiQty, aiEquity, Equity(h), Volatile.Read(ref _copyRatioBp));
            long filled = 0;
            int rc = qty <= 0 ? ArenaCopyCode.ZeroQty : CopyCode(Buy(h, si, qty, out filled, out _));
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
            int rc = CopyCode(Sell(h, si, qty, out long filled, out _, out _));
            if (rc != ArenaCopyCode.None) h.Rejects++;
            _copy.Record(ArenaCopyKind.Companion, rc, -1, s.Symbol, filled);
        }

        /// <summary>[v12.18.71-DEV] 아레나 거부 코드 → 복제 결과 코드(ArenaCopyCode).</summary>
        public static int CopyCode(int reject) => reject switch
        {
            CryptoArenaReject.None => ArenaCopyCode.None,
            CryptoArenaReject.InsufficientKrw => ArenaCopyCode.InsufficientCash,
            CryptoArenaReject.InsufficientCoin => ArenaCopyCode.InsufficientPosition,
            CryptoArenaReject.SessionInvalid => ArenaCopyCode.SessionClosed,
            CryptoArenaReject.NoQuote or CryptoArenaReject.StaleQuote => ArenaCopyCode.NoQuote,
            CryptoArenaReject.NoLiquidity => ArenaCopyCode.NoLiquidity,
            CryptoArenaReject.QueueFull => ArenaCopyCode.QueueFull,
            CryptoArenaReject.Invalid => ArenaCopyCode.Invalid,
            _ => ArenaCopyCode.Rejected,
        };

        /// <summary>
        /// AI 청산 수량(δ=0): 매수1 잔량이 보유 전량을 받으면 전량 · 부분 청산이면 잔여 보유가 최소 주문의 2배(10,000원 · 손절 −1.5% 이후에도 최소 주문 이상) 이상 남도록 축소
        /// (최소 주문 미만 '먼지' 잔량 = 업비트에서 영구 청산 불가 → AI 슬롯 고착 방지). 0 = 이번 호가에서 청산 보류.
        /// </summary>
        public static long ExitQty(long positionQtyRaw, long bidQtyRaw, long bidRaw)
        {
            if (positionQtyRaw <= 0 || bidQtyRaw <= 0 || bidRaw <= 0) return 0;
            if (bidQtyRaw >= positionQtyRaw) return positionQtyRaw;
            long keep = (long)Int128.Min((Int128)CryptoPaperExchange.MinOrderKrwRaw * 2 * Scale / bidRaw + 1, long.MaxValue);
            long sell = Math.Min(bidQtyRaw, positionQtyRaw - keep);
            return sell > 0 && (Int128)sell * bidRaw / Scale >= CryptoPaperExchange.MinOrderKrwRaw ? sell : 0;   // 부분 청산분도 최소 주문 이상
        }

        /// <summary>
        /// [v12.18.76-DEV] 잔량 열 유효 단 수(δ=0): [0] = L1 잔량(bidQty/askQty)과 일치해야 하며(불일치 = 0) · 양측 공통 범위(≤ 15) 안에서
        /// 0 ≤ 잔량 ≤ MaxBookQtyRaw 인 연속 접두 길이. 0 = 뎁스 미사용(L1 판독).
        /// </summary>
        public static int ValidDepthTiers(long bidQtyRaw, long askQtyRaw, ReadOnlySpan<long> bidDepthQtyRaw, ReadOnlySpan<long> askDepthQtyRaw)
        {
            int n = Math.Min(Math.Min(bidDepthQtyRaw.Length, askDepthQtyRaw.Length), PlutoOrderbookDepthAnalyzer.MaxTiers);
            if (n <= 0 || bidDepthQtyRaw[0] != bidQtyRaw || askDepthQtyRaw[0] != askQtyRaw) return 0;
            for (int i = 0; i < n; i++)
            {
                long b = bidDepthQtyRaw[i], a = askDepthQtyRaw[i];
                if (b < 0 || a < 0 || b > MaxBookQtyRaw || a > MaxBookQtyRaw) return i;
            }
            return n;
        }

        /// <summary>최우선 잔량 raw × 가격 raw → 원(내림 · δ=0).</summary>
        public static long DepthWon(long qtyRaw, long priceRaw)
            => qtyRaw <= 0 || priceRaw <= 0 ? 0 : (long)Int128.Min((Int128)qtyRaw * priceRaw / Scale / Scale, long.MaxValue);

        /// <summary>가격(raw) × (1 + bp) 를 업비트 호가 격자로 올림(무효 = 0).</summary>
        public static long AlignUpRaw(long priceRaw, int bp)
        {
            if (priceRaw <= 0 || priceRaw > MaxPriceRaw) return 0;
            return UpbitTickAligner.AlignUp((long)((Int128)priceRaw * (10000 + bp) / 10000));
        }

        /// <summary>가격(raw) × (1 + bp) 를 업비트 호가 격자로 내림(무효 = 0).</summary>
        public static long AlignDownRaw(long priceRaw, int bp)
        {
            if (priceRaw <= 0 || priceRaw > MaxPriceRaw) return 0;
            return UpbitTickAligner.AlignDown((long)((Int128)priceRaw * (10000 + bp) / 10000));
        }

        private void DrainHuman(long utcMicros)
        {
            var b = _books[ArenaSide.Human];
            while (TryDequeue(out var o))
            {
                int rc;
                int si = CryptoArenaUniverse.IndexOf(o.Symbol);
                bool copy = o.Flags == MarketOpenAuctionGate.FlagCopy;
                if (_phase != CryptoSessionPhase.ContinuousTrading) rc = CryptoArenaReject.SessionInvalid;
                else if (si < 0 || _syms[si].Seen == 0) rc = CryptoArenaReject.NoQuote;
                else if (utcMicros < _syms[si].QuoteUtc || utcMicros - _syms[si].QuoteUtc > MaxQuoteAgeMicros) rc = CryptoArenaReject.StaleQuote;
                else if (copy && CopySlipped(in o, si))
                {
                    b.Rejects++;                                                // [v12.18.71-DEV] 복제 슬리피지 20bp 초과 = 취소
                    _copy.Record(ArenaCopyKind.Follow, ArenaCopyCode.SlippageExceeded, o.Side, o.Symbol, 0);
                    continue;
                }
                else if (o.Side > 0)
                {
                    rc = Buy(b, si, o.Qty, out long bought, out _);
                    if (rc == CryptoArenaReject.None && copy)
                    {
                        b.Positions[FindPos(b, o.Symbol)].CopiedQty += bought;
                        _copy.Record(ArenaCopyKind.Follow, ArenaCopyCode.None, 1, o.Symbol, bought);
                    }
                }
                else
                {
                    int pi = FindPos(b, o.Symbol);
                    if (pi < 0 || (!copy && b.Positions[pi].Qty < o.Qty)) rc = CryptoArenaReject.InsufficientCoin;
                    else
                    {
                        rc = Sell(b, si, Math.Min(o.Qty, b.Positions[pi].Qty), out long sold, out _, out _);
                        if (rc == CryptoArenaReject.None && copy) _copy.Record(ArenaCopyKind.Follow, ArenaCopyCode.None, -1, o.Symbol, sold);
                    }
                }
                if (rc == CryptoArenaReject.None) continue;
                b.Rejects++;
                if (copy) _copy.Record(ArenaCopyKind.Follow, CopyCode(rc), o.Side, o.Symbol, 0);
            }
        }

        /// <summary>[v12.18.71-DEV] 복제 주문 예상 체결가(슬리피지 반영 시장가) vs 기준가 — 불리한 괴리 20bp 초과 = true(가격 산출 불가 = true · fail-closed).</summary>
        private bool CopySlipped(in ArenaAuctionOrder o, int si)
        {
            ref Sym s = ref _syms[si];
            var st = CryptoPaperExchange.SlippedMarketPrice(o.Side > 0 ? CryptoOrderSide.Buy : CryptoOrderSide.Sell, s.Bid, s.Ask, SlippageBp, out long px);
            return st != CryptoPaperStatus.Filled || ArenaCopyMath.SlippageExceeded(o.Side, o.RefPx, px);
        }

        private static int MapStatus(CryptoPaperStatus st) => st switch
        {
            CryptoPaperStatus.Filled => CryptoArenaReject.None,
            CryptoPaperStatus.BelowMinimumOrder => CryptoArenaReject.BelowMinimumOrder,
            _ => CryptoArenaReject.PriceInvalid,
        };

        private int Buy(Book b, int si, long qty, out long filled, out long px)
        {
            filled = 0; px = 0;
            ref Sym s = ref _syms[si];
            qty = Math.Min(qty, s.AskQty);
            if (qty <= 0) return CryptoArenaReject.NoLiquidity;
            var st = CryptoPaperExchange.SlippedMarketPrice(CryptoOrderSide.Buy, s.Bid, s.Ask, SlippageBp, out long p);
            long notional = 0, fee = 0;
            if (st == CryptoPaperStatus.Filled) st = CryptoPaperExchange.NotionalWithFee(CryptoOrderSide.Buy, p, qty, FeeBp, out notional, out fee);
            if (st != CryptoPaperStatus.Filled) return MapStatus(st);
            long cost = notional + fee;
            if (cost > b.Cash) return CryptoArenaReject.InsufficientKrw;
            int pi = FindPos(b, s.Symbol);
            if (pi < 0)
            {
                if (b.Count >= MaxPositions) return CryptoArenaReject.BookFull;
                pi = b.Count++;
                b.Positions[pi] = new Pos { Symbol = s.Symbol, SymIdx = si };
            }
            ref Pos ps = ref b.Positions[pi];
            ps.AvgPx = (long)(((Int128)ps.AvgPx * ps.Qty + (Int128)p * qty) / (ps.Qty + qty));
            ps.Qty += qty;
            ps.Cost += cost;
            b.Cash -= cost; b.Fees += fee; b.Fills++;
            filled = qty; px = p;
            return CryptoArenaReject.None;
        }

        private int Sell(Book b, int si, long qty, out long filled, out long px, out long pnl)
        {
            filled = 0; px = 0; pnl = 0;
            ref Sym s = ref _syms[si];
            int pi = FindPos(b, s.Symbol);
            if (pi < 0) return CryptoArenaReject.InsufficientCoin;
            ref Pos ps = ref b.Positions[pi];
            qty = Math.Min(Math.Min(qty, ps.Qty), s.BidQty);
            if (qty <= 0) return CryptoArenaReject.NoLiquidity;
            var st = CryptoPaperExchange.SlippedMarketPrice(CryptoOrderSide.Sell, s.Bid, s.Ask, SlippageBp, out long p);
            long notional = 0, fee = 0;
            if (st == CryptoPaperStatus.Filled) st = CryptoPaperExchange.NotionalWithFee(CryptoOrderSide.Sell, p, qty, FeeBp, out notional, out fee);
            if (st != CryptoPaperStatus.Filled) return MapStatus(st);
            long net = notional - fee;
            long basis = (long)((Int128)ps.Cost * qty / ps.Qty);
            pnl = net - basis;
            ps.Cost -= basis; ps.Qty -= qty; ps.TradePnl += pnl;
            if (ps.CopiedQty > ps.Qty) ps.CopiedQty = ps.Qty;              // [v12.18.71-DEV] 복제 보유분 ≤ 보유(휴먼 수동 매도 반영)
            b.Cash += net; b.Realized += pnl; b.Fees += fee; b.Fills++;
            filled = qty; px = p;
            if (ps.Qty == 0)
            {
                if (ps.TradePnl > 0) b.Wins++; else b.Losses++;
                b.Positions[pi] = b.Positions[--b.Count];
                b.Positions[b.Count] = default;
            }
            return CryptoArenaReject.None;
        }

        private void Record(in AiTradeRationale r)
        {
            _rationales[_rationaleCount % RationaleCapacity] = r;
            _rationaleCount++;
        }

        private long Equity(Book b)
        {
            Int128 eq = b.Cash;
            for (int i = 0; i < b.Count; i++) eq += (Int128)b.Positions[i].Qty * _syms[b.Positions[i].SymIdx].Bid / Scale;
            return (long)Int128.Min(eq, long.MaxValue);
        }

        private DuelBookStats Stats(Book b)
        {
            long eq = Equity(b);
            long cost = 0;
            for (int i = 0; i < b.Count; i++) cost += b.Positions[i].Cost;
            if (eq > b.Peak) b.Peak = eq;
            if (b.Peak > 0) b.MddBp = Math.Max(b.MddBp, (long)((Int128)(b.Peak - eq) * 10000 / b.Peak));
            return new DuelBookStats(_capitalRaw, b.Cash, eq, b.Realized, eq - b.Cash - cost, b.Fees, 0, b.Peak, b.MddBp, b.Fills, b.Rejects,
                b.Wins, b.Losses, b.Count);
        }

        /// <summary>[v12.18.67-DEV] AI 신규 진입 거버넌스 승인(δ=0 · 차단 사유별 계수).</summary>
        private bool Govern(int si, int score, int momentumBp, int momentumWeight, int imbalanceBp, int imbalanceWeight, long nowTicks, out PlutoGovernanceTag gov,
            bool pocResistance = false, bool cvdBearish = false, bool depthTrap = false)
        {
            ref Sym s = ref _syms[si];
            PlutoCognitiveWeights learned = default;
            Volatile.Read(ref _learning)?.TryRead(out learned);                 // [v12.18.77-DEV] Track A — 학습 가중치 스냅샷 판독(δ=0 · 미결선/경합 = 중립)
            PlutoRegimeMatch history = default;
            Volatile.Read(ref _regimeMatcher)?.TryRead(out history);               // [v12.18.82-DEV] 역사적 국면 판독(δ=0 · 미결선/경합 = 판정 전 · 편향 0)
            learned = history.Bias(in learned);
            var r = PlutoCognitiveGovernor.Approve(in s.Regime, score, momentumBp, momentumWeight, imbalanceBp, imbalanceWeight, EntryScore, s.BidQty, s.AskQty,
                Volatile.Read(ref _govUs10yBp), Volatile.Read(ref _govDxyBp), in _sizer, nowTicks, out gov, pocResistance, cvdBearish, depthTrap, in learned, in history);
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
            var ai = Stats(_books[ArenaSide.Ai]);
            var human = Stats(_books[ArenaSide.Human]);
            _snap = new CryptoArenaSnapshot(_ticks, _trades, _dropped, _rationaleCount, _lastKstTicks, _lastUtc, _phase,
                Volatile.Read(ref _kimchiBp), Volatile.Read(ref _globalBp), in ai, in human);
        }

        private static int FindPos(Book b, long symbol)
        {
            for (int i = 0; i < b.Count; i++) if (b.Positions[i].Symbol == symbol) return i;
            return -1;
        }

        // ── seqlock(단일 작성자 · 판독 할당 0) ─────────────────────────────────────

        private void BeginWrite() => Interlocked.Increment(ref _ver);
        private void EndWrite() => Interlocked.Increment(ref _ver);

        public bool TryReadSnapshot(out CryptoArenaSnapshot snapshot)
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

        /// <summary>진영 보유 사본(원 raw · 코인 raw) — 반환 = 복사 개수(경합 상한 초과 = 0).</summary>
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

        /// <summary>수신(1회 이상) 종목 최우선 호가 사본(원 raw · 코인 raw · 표 순서) — 반환 = 복사 개수(경합 상한 초과 = 0).</summary>
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
