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
// ============================================================================
using System;
using System.Threading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Cognitive.News;
using StockManager.Engines.Feeds;
using StockManager.Engines.Pricing;

namespace StockManager.Engines.Arena.Us
{
    /// <summary>US 알파 시그널 1회 평가 결과(bp). Dominant = 점수 방향으로 가장 크게 기여한 구성요소.</summary>
    public readonly struct UsAlphaSignal
    {
        public readonly int Score, EtfBetaBp, RateBp, GapFlowBp, MomentumBp, NewsBp;
        public readonly ArenaTrigger Dominant;

        public UsAlphaSignal(int score, int etfBetaBp, int rateBp, int gapFlowBp, int momentumBp, ArenaTrigger dominant, int newsBp = 0)
        {
            Score = score; EtfBetaBp = etfBetaBp; RateBp = rateBp; GapFlowBp = gapFlowBp; MomentumBp = momentumBp; Dominant = dominant; NewsBp = newsBp;
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
        public const int EtfGain = 10, RateGain = 100, GapGain = 10, MomentumGain = 20;
        public const int StopBp = 150, TargetBaseBp = 150, TargetPerConfidenceBp = 4, PositionWeightBp = 2000;
        public static readonly long AiCutoffBeforeCloseTicks = TimeSpan.FromMinutes(10).Ticks;

        private const long KstOffsetTicks = 9 * TimeSpan.TicksPerHour;
        private const long RegularCloseTicks = 16 * TimeSpan.TicksPerHour, EarlyCloseTicks = 13 * TimeSpan.TicksPerHour;
        private const int EmaShift = 3;          // α = 1/8
        private const int Fx = 10;               // EWMA 고정소수점 ×1024
        private const int SeqlockRetries = 64;
        private const int QueueMask = HumanQueueCapacity - 1;

        private struct Sym { public long Symbol, Bid, Ask, BidQty, AskQty, EmaMidFx, LastRegularMidX2, PrevCloseX2; public int GapBp, Seen; }
        private struct Pos { public long Symbol, Qty, AvgPx, Cost, Target, Stop, TradePnl; public int SymIdx, RiskRewardX100; }
        private struct HumanCell { public long Seq, Symbol, Qty; public int Side; }

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
        private UsSessionPhase _phase = UsSessionPhase.Closed;
        private bool _cutoff;
        private int _qqqBp, _soxxBp, _us10yBp;
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
            _rationaleCount = 0;
            for (int i = 0; i < MaxSymbols; i++) _syms[i] = new Sym { Symbol = UsTechUniverse.Symbol(i) };
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

        // ── 휴먼 주문 큐(Vyukov 유계 MPSC · 할당 0) ──────────────────────────────────

        public int SubmitHuman(long symbol, int side, long qty)
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
                        c.Symbol = symbol; c.Side = side; c.Qty = qty;
                        Volatile.Write(ref c.Seq, pos + 1);
                        return UsArenaReject.None;
                    }
                    pos = Volatile.Read(ref _hqEnq);
                }
                else if (dif < 0) return UsArenaReject.QueueFull;
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

        // ── 세션 시계 ──────────────────────────────────────────────────────────

        /// <summary>KST 시각의 미국 세션 페이즈(δ=0 · UsSessionStateMachine 위임).</summary>
        public static UsSessionPhase PhaseAt(DateTime kst) => UsSessionStateMachine.GetSessionPhase(kst);

        private void Clock(DateTime kst)
        {
            _lastKstTicks = kst.Ticks;
            if (kst.Ticks < KstOffsetTicks) { _phase = UsSessionPhase.CalendarUncovered; _cutoff = true; return; }
            DateTime et = UsTradingCalendar.ToEastern(new DateTime(kst.Ticks - KstOffsetTicks));
            _phase = UsSessionStateMachine.GetSessionPhaseEastern(et);
            long day = et.Ticks - et.Ticks % TimeSpan.TicksPerDay;
            if (day != _etDay)
            {
                if (_etDay != 0) RollDay();
                _etDay = day;
            }
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
                long midX2 = bidCents + askCents;
                long midFx = midX2 << (Fx - 1);
                if (_phase == UsSessionPhase.PreMarket && s.PrevCloseX2 > 0)
                    s.GapBp = (int)Math.Clamp((midX2 - s.PrevCloseX2) * 10000 / s.PrevCloseX2, -10000, 10000);
                else if (_phase == UsSessionPhase.ContinuousTrading)
                    s.LastRegularMidX2 = midX2;
                PlutoNewsTag news = default;
                var pluto = Volatile.Read(ref _pluto);
                if (pluto is not null && pluto.TryRead(symbol, out var nf)) news = new PlutoNewsTag(in nf, kst.Ticks - PlutoClock.KstOffsetTicks);
                var sig = s.Seen >= WarmupTicks
                    ? Evaluate(si, Volatile.Read(ref _qqqBp), Volatile.Read(ref _soxxBp), Volatile.Read(ref _us10yBp), s.GapBp, bidQty, askQty, midFx, s.EmaMidFx, news.NewsBp)
                    : default;
                if (s.Seen == 0) s.EmaMidFx = midFx;
                else s.EmaMidFx += (midFx - s.EmaMidFx) >> EmaShift;
                if (s.Seen < int.MaxValue) s.Seen++;

                DrainHuman();
                if (_phase == UsSessionPhase.ContinuousTrading) StepAi(si, in sig, in news, kst);
            }
            PublishSnapshot();
            EndWrite();
        }

        /// <summary>순수 시그널 평가(할당 0). index = UsTechUniverse 인덱스 · midFx/emaMidFx = 중간가 ×1024 고정소수점.</summary>
        public static UsAlphaSignal Evaluate(int index, int qqqBp, int soxxBp, int us10yChangeBp, int gapBp, long bidQty, long askQty, long midFx, long emaMidFx, int newsBp = 0)
        {
            if ((uint)index >= MaxSymbols) return default;
            int news = Math.Clamp(newsBp, -10000, 10000);
            long qqq = Math.Clamp(qqqBp, -10000, 10000), soxx = Math.Clamp(soxxBp, -10000, 10000), y10 = Math.Clamp(us10yChangeBp, -10000, 10000);
            int etf = (int)Math.Clamp((qqq * UsTechUniverse.QqqBetaX100(index) + soxx * UsTechUniverse.SemiBetaX100(index)) / 100 * EtfGain, -10000, 10000);
            int rate = (int)Math.Clamp(-y10 * UsTechUniverse.RateSensX100(index) / 100 * RateGain, -10000, 10000);
            long depth = bidQty + askQty;
            int obi = depth <= 0 ? 0 : (int)((bidQty - askQty) * 10000 / depth);
            int gapFlow = ((int)Math.Clamp((long)Math.Clamp(gapBp, -10000, 10000) * GapGain, -10000, 10000) + obi) / 2;
            int mom = emaMidFx <= 0 ? 0 : (int)Math.Clamp((midFx - emaMidFx) * 10000 / emaMidFx * MomentumGain, -10000, 10000);
            long cEtf = (long)etf * EtfWeight, cRate = (long)rate * RateWeight, cGap = (long)gapFlow * GapFlowWeight, cMom = (long)mom * MomentumWeight;
            long cNews = (long)news * NewsWeight;
            int score = (int)Math.Clamp((cEtf + cRate + cGap + cMom + cNews) / 100, -10000, 10000);
            int sign = score >= 0 ? 1 : -1;
            var dom = ArenaTrigger.PremarketGapFlow;
            long best = cGap * sign;
            if (cEtf * sign > best) { best = cEtf * sign; dom = ArenaTrigger.EtfBeta; }
            if (cMom * sign > best) { best = cMom * sign; dom = ArenaTrigger.MomentumBreakout; }
            if (cRate * sign > best) { best = cRate * sign; dom = ArenaTrigger.RateSensitivity; }
            if (cNews * sign > best) dom = ArenaTrigger.NewsCatalyst;
            return new UsAlphaSignal(score, etf, rate, gapFlow, mom, dom, news);
        }

        private void StepAi(int si, in UsAlphaSignal sig, in PlutoNewsTag news, DateTime kst)
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
                else if (s.Bid >= p.Target) exit = ArenaTrigger.TakeProfit;
                else if (s.Bid <= p.Stop) exit = ArenaTrigger.StopLoss;
                else if (sig.Score <= ExitScore) exit = ArenaTrigger.SignalReversal;
                if (exit == ArenaTrigger.None) return;
                long target = p.Target, stop = p.Stop; int rr = p.RiskRewardX100;
                if (Sell(b, si, p.Qty, out long filled, out long pnl) != UsArenaReject.None) return;
                int conf = exit == ArenaTrigger.SignalReversal ? Math.Clamp(-sig.Score / 100, 0, 100)
                         : exit == ArenaTrigger.NewsShock ? Math.Clamp(-news.EffectiveScore, 0, 100) : 100;
                Record(new AiTradeRationale(_rationaleCount + 1, s.Symbol, -1, filled, s.Bid, kst.Ticks, exit, sig.Score, conf,
                    target, stop, rr, sig.RateBp, sig.GapFlowBp, sig.EtfBetaBp, sig.MomentumBp, pnl, ArenaCurrency.Usd, in news));
                return;
            }
            if (_cutoff || news.IsShock || sig.Score < EntryScore || b.Count >= MaxAiPositions) return;
            int confidence = Math.Clamp(sig.Score / 100, 0, 100);
            long tgt = AlignUpCents(s.Ask, TargetBaseBp + TargetPerConfidenceBp * confidence);
            long stp = AlignDownCents(s.Ask, -StopBp);
            if (stp <= 0 || stp >= s.Ask || tgt <= s.Ask) return;
            int rrX100 = (int)Math.Min((tgt - s.Ask) * 100 / (s.Ask - stp), 100_000);
            if (rrX100 <= 0) return;
            long budget = Math.Min(b.Cash, Equity(b) * PositionWeightBp / 10000);
            long qty = budget * 100_000 / (s.Ask * (100_000 + FeePer100k));
            if (qty <= 0) return;
            if (Buy(b, si, qty, out long bought) != UsArenaReject.None) return;
            ref Pos np = ref b.Positions[FindPos(b, s.Symbol)];
            np.Target = tgt; np.Stop = stp; np.RiskRewardX100 = rrX100;
            Record(new AiTradeRationale(_rationaleCount + 1, s.Symbol, 1, bought, s.Ask, kst.Ticks, sig.Dominant, sig.Score, confidence,
                tgt, stp, rrX100, sig.RateBp, sig.GapFlowBp, sig.EtfBetaBp, sig.MomentumBp, 0, ArenaCurrency.Usd, in news));
        }

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
            var b = _books[ArenaSide.Human];
            while (TryDequeue(out long symbol, out int side, out long qty))
            {
                int rc;
                int si = UsTechUniverse.IndexOf(symbol);
                if (_phase != UsSessionPhase.ContinuousTrading) rc = UsArenaReject.SessionClosed;
                else if (si < 0 || _syms[si].Seen == 0) rc = UsArenaReject.NoQuote;
                else if (side > 0) rc = Buy(b, si, qty, out _);
                else
                {
                    int pi = FindPos(b, symbol);
                    rc = pi < 0 || b.Positions[pi].Qty < qty ? UsArenaReject.InsufficientPosition : Sell(b, si, qty, out _, out _);
                }
                if (rc != UsArenaReject.None) b.Rejects++;
            }
        }

        private int Buy(Book b, int si, long qty, out long filled)
        {
            filled = 0;
            ref Sym s = ref _syms[si];
            qty = Math.Min(qty, s.AskQty);
            if (qty <= 0) return UsArenaReject.NoLiquidity;
            long notional = qty * s.Ask;
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

        private int Sell(Book b, int si, long qty, out long filled, out long pnl)
        {
            filled = 0; pnl = 0;
            ref Sym s = ref _syms[si];
            int pi = FindPos(b, s.Symbol);
            if (pi < 0) return UsArenaReject.InsufficientPosition;
            ref Pos p = ref b.Positions[pi];
            qty = Math.Min(Math.Min(qty, p.Qty), s.BidQty);
            if (qty <= 0) return UsArenaReject.NoLiquidity;
            long notional = qty * s.Bid;
            long fee = notional * FeePer100k / 100_000;
            long sec = notional * SecFeePer10M / 10_000_000;
            long net = notional - fee - sec;
            long basis = p.Cost * qty / p.Qty;
            pnl = net - basis;
            p.Cost -= basis; p.Qty -= qty; p.TradePnl += pnl;
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

        private void PublishSnapshot()
        {
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
