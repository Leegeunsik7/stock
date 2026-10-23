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
// ============================================================================
using System;
using System.Threading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Brokers.Crypto;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Regime;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분
using StockManager.Engines.Feeds;
using StockManager.Engines.Pricing;

namespace StockManager.Engines.Arena.Crypto
{
    /// <summary>크립토 알파 시그널 1회 평가 결과(bp). Dominant = 점수 방향으로 가장 크게 기여한 구성요소.</summary>
    public readonly struct CryptoAlphaSignal
    {
        public readonly int Score, ImbalanceBp, MomentumBp, FlowBp, KimchiVolBp;
        public readonly ArenaTrigger Dominant;

        public CryptoAlphaSignal(int score, int imbalanceBp, int momentumBp, int flowBp, int kimchiVolBp, ArenaTrigger dominant)
        {
            Score = score; ImbalanceBp = imbalanceBp; MomentumBp = momentumBp; FlowBp = flowBp; KimchiVolBp = kimchiVolBp; Dominant = dominant;
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
        private struct Pos { public long Symbol, Qty, AvgPx, Cost, Target, Stop, TradePnl; public int SymIdx, RiskRewardX100; public PlutoEntryMemo Memo; }
        private struct HumanCell { public long Seq, Symbol, Qty; public int Side; }

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
        private int _govUs10yBp, _govDxyBp;
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
        public int SubmitHuman(long symbol, int side, long qtyRaw)
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
                        c.Symbol = symbol; c.Side = side; c.Qty = qtyRaw;
                        Volatile.Write(ref c.Seq, pos + 1);
                        return CryptoArenaReject.None;
                    }
                    pos = Volatile.Read(ref _hqEnq);
                }
                else if (dif < 0) return CryptoArenaReject.QueueFull;
                else pos = Volatile.Read(ref _hqEnq);
            }
        }

        private bool TryDequeue(out long symbol, out int side, out long qty)
        {
            long pos = _hqDeq;
            ref HumanCell c = ref _hq[pos & QueueMask];
            if (Volatile.Read(ref c.Seq) != pos + 1) { symbol = 0; side = 0; qty = 0; return false; }
            symbol = c.Symbol; side = c.Side; qty = c.Qty;
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
            }
            PublishSnapshot();
            EndWrite();
        }

        /// <summary>
        /// 종목 1틱(최우선 호가 raw · 잔량 raw) 정산: 세션 판정 → 시그널 평가 → 휴먼 큐 체결 → AI 진입/청산(+근거) → 시가평가 게시.
        /// 표 밖 마켓 · 호가 격자 비정렬 · 매수 ≥ 매도 · 잔량 범위 밖 · 세션 무효 = 폐기(DroppedQuotes 증가 · fail-closed).
        /// </summary>
        public void OnQuote(long symbol, long bidRaw, long askRaw, long bidQtyRaw, long askQtyRaw, long utcMicros)
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
                if (s.LastMid > 0)
                {
                    int absBp = (int)Int128.Min(Int128.Abs((Int128)(mid - s.LastMid) * 10000 / s.LastMid), 10000);
                    s.VolX16 += ((absBp << VolShift) - s.VolX16) >> VolShift;
                }
                s.LastMid = mid;
                var sig = s.Seen >= WarmupTicks
                    ? Evaluate(si, Volatile.Read(ref _kimchiBp), Volatile.Read(ref _globalBp), bidQtyRaw, askQtyRaw, mid, s.EmaMid, s.BuyFlow, s.SellFlow, s.VolX16 >> VolShift)
                    : default;
                if (s.Seen == 0) s.EmaMid = mid;
                else s.EmaMid += (mid - s.EmaMid) >> EmaShift;
                if (s.Seen < int.MaxValue) s.Seen++;

                DrainHuman(utcMicros);
                StepAi(si, in sig);
            }
            PublishSnapshot();
            EndWrite();
        }

        /// <summary>
        /// 순수 시그널 평가(할당 0). index = CryptoArenaUniverse 인덱스 · mid/emaMid = 원 raw · buyFlow/sellFlow = 테이커 체결량 EWMA raw ·
        /// volBp = |Δ중간가| EWMA(bp).
        /// </summary>
        public static CryptoAlphaSignal Evaluate(int index, int kimchiPremiumBp, int globalChangeBp, long bidQtyRaw, long askQtyRaw, long midRaw, long emaMidRaw,
            long buyFlowRaw, long sellFlowRaw, int volBp)
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
            long cObi = (long)obi * ImbalanceWeight, cMom = (long)mom * MomentumWeight, cKv = (long)kv * KimchiVolWeight;
            int score = (int)Math.Clamp((cObi + cMom + cKv) / 100, -10000, 10000);
            int sign = score >= 0 ? 1 : -1;
            var dom = ArenaTrigger.OrderbookImbalance;
            long best = cObi * sign;
            if (cMom * sign > best) { best = cMom * sign; dom = ArenaTrigger.TradeFlowMomentum; }
            if (cKv * sign > best) dom = ArenaTrigger.KimchiVolatility;
            return new CryptoAlphaSignal(score, obi, mom, flow, kv, dom);
        }

        private void StepAi(int si, in CryptoAlphaSignal sig)
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
                if (Sell(b, si, sellQty, out long filled, out long exitPx, out long pnl) != CryptoArenaReject.None) return;
                int conf = exit == ArenaTrigger.SignalReversal ? Math.Clamp(-sig.Score / 100, 0, 100) : 100;
                var exitGov = GovernExit(in memo, exit, si, priorPnl + pnl, FindPos(b, s.Symbol) < 0, kst);
                Record(new AiTradeRationale(_rationaleCount + 1, s.Symbol, -1, filled, exitPx, kst, exit, sig.Score, conf,
                    target, stop, rr, sig.KimchiVolBp, sig.ImbalanceBp, sig.FlowBp, sig.MomentumBp, pnl, ArenaCurrency.Crypto,
                    default, default, bidDepth, askDepth, in exitGov, entry));
                return;
            }
            if (b.Count >= MaxAiPositions) return;
            int score = sig.Score, confidence, weightBp = PositionWeightBp;
            PlutoGovernanceTag gov = default;
            if (Volatile.Read(ref _governed))
            {
                if (!Govern(si, sig.Score, sig.MomentumBp, MomentumWeight, sig.ImbalanceBp, ImbalanceWeight, kst, out gov)) return;
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
            if (Buy(b, si, qty, out long bought, out long px) != CryptoArenaReject.None) return;
            ref Pos np = ref b.Positions[FindPos(b, s.Symbol)];
            np.Target = tgt; np.Stop = stp; np.RiskRewardX100 = rrX100;
            if (gov.Governed) np.Memo = Memo(in gov, kst, np.Cost);
            Record(new AiTradeRationale(_rationaleCount + 1, s.Symbol, 1, bought, px, kst, sig.Dominant, score, confidence,
                tgt, stp, rrX100, sig.KimchiVolBp, sig.ImbalanceBp, sig.FlowBp, sig.MomentumBp, 0, ArenaCurrency.Crypto,
                default, default, bidDepth, askDepth, in gov));
        }

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
            while (TryDequeue(out long symbol, out int side, out long qty))
            {
                int rc;
                int si = CryptoArenaUniverse.IndexOf(symbol);
                if (_phase != CryptoSessionPhase.ContinuousTrading) rc = CryptoArenaReject.SessionInvalid;
                else if (si < 0 || _syms[si].Seen == 0) rc = CryptoArenaReject.NoQuote;
                else if (utcMicros < _syms[si].QuoteUtc || utcMicros - _syms[si].QuoteUtc > MaxQuoteAgeMicros) rc = CryptoArenaReject.StaleQuote;
                else if (side > 0) rc = Buy(b, si, qty, out _, out _);
                else
                {
                    int pi = FindPos(b, symbol);
                    rc = pi < 0 || b.Positions[pi].Qty < qty ? CryptoArenaReject.InsufficientCoin : Sell(b, si, qty, out _, out _, out _);
                }
                if (rc != CryptoArenaReject.None) b.Rejects++;
            }
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
        private bool Govern(int si, int score, int momentumBp, int momentumWeight, int imbalanceBp, int imbalanceWeight, long nowTicks, out PlutoGovernanceTag gov)
        {
            ref Sym s = ref _syms[si];
            var r = PlutoCognitiveGovernor.Approve(in s.Regime, score, momentumBp, momentumWeight, imbalanceBp, imbalanceWeight, EntryScore, s.BidQty, s.AskQty,
                Volatile.Read(ref _govUs10yBp), Volatile.Read(ref _govDxyBp), in _sizer, nowTicks, out gov);
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
