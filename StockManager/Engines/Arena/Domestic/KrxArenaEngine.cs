// ============================================================================
// KrxArenaEngine — v12.18.59-DEV: 국내 대형주 아레나(휴먼 vs AI 듀얼 가상 원장 · 설명 가능한 AI 매매) — 아레나 3대 모드 1단계
//
//   ① 정규 세션: 평일 09:00 ≤ KST &lt; 15:30 만 체결(휴장일 캘린더 미반영 — 호출 측 피드가 휴장일엔 시세를 보내지 않음).
//     15:20(동시호가 진입 전) 이후 AI 신규 진입 0 · 보유 전량 SessionClose 청산.
//   ② 알파 시그널(bp, 각 ±10000): 야간 글로벌(SetOvernightMacro) · 호가 불균형 (매수잔량−매도잔량)/합 ·
//     거래량 급증(틱 거래량 / EWMA − 1, 가격 방향 부호) · 기술적 모멘텀(중간가 vs EWMA 중간가 × 20).
//     점수 = (20·매크로 + 35·불균형 + 20·급증 + 25·모멘텀)/100. 종목별 워밍업 5틱 전 시그널 0.
//     점수 ≥ 3000 → 진입(확신도 = 점수/100 · 목표 +(100+3·확신도)bp · 손절 −80bp · KRX 호가단위 정렬).
//     보유 중 목표가 도달 / 손절가 이탈 / 점수 ≤ −2000 / 15:20 → 청산. AI 체결 1건마다 AiTradeRationale 1건(누락 0).
//   ③ 체결: 시장가 = 최우선 호가(매수 = 매도1호가 · 매도 = 매수1호가) · 수량 상한 = 그 호가 잔량(부분 체결 · 잔량 폐기 — 큐잉/재시도 0).
//     수수료 0.015%(양방향) · 거래세 0.18%(매도). 공매도 없음. 양 진영은 같은 호가를 '각자' 소비(진영 간 간섭 0 · 원장 공유 0).
//   ④ 휴먼 주문: SubmitHuman(임의 스레드) → 고정 링(Vyukov 유계 MPSC · 포화 = QueueFull 폐기) → 정산 스레드가 다음 OnQuote/Drain 에서 체결.
//   ⑤ 게시: 정산 1회 전체가 단일 seqlock 쓰기 창(홀수 = 기록 중). 스냅샷 · 보유 · 근거 피드 판독은 같은 버전으로 검증.
//
//   ★δ=0: OnQuote/Drain/SubmitHuman/판독 경로 힙 할당 0(고정 배열 · 값 타입 · LINQ/박싱/클로저/문자열 0).
//   ★단일 작성자: OnQuote/Drain/SetCapital 은 정산 스레드 1개만. ★liveArmed:false — 외부 송출 수단 0.
// ============================================================================
using System;
using System.Threading;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena.Common;

namespace StockManager.Engines.Arena.Domestic
{
    /// <summary>알파 시그널 1회 평가 결과(bp). Dominant = 점수 방향으로 가장 크게 기여한 구성요소.</summary>
    public readonly struct KrxAlphaSignal
    {
        public readonly int Score, MacroBp, ImbalanceBp, SurgeBp, MomentumBp;
        public readonly ArenaTrigger Dominant;

        public KrxAlphaSignal(int score, int macroBp, int imbalanceBp, int surgeBp, int momentumBp, ArenaTrigger dominant)
        {
            Score = score; MacroBp = macroBp; ImbalanceBp = imbalanceBp; SurgeBp = surgeBp; MomentumBp = momentumBp; Dominant = dominant;
        }
    }

    public sealed class KrxArenaEngine : IArenaModeEngine
    {
        public const long DefaultCapitalWon = 100_000_000;
        public const int MaxSymbols = 32, MaxPositions = 16, MaxAiPositions = 5, RationaleCapacity = 128, HumanQueueCapacity = 64;
        public const long FeePer100k = 15, TaxPer100k = 180;
        public const long MaxOrderQty = 10_000_000, MaxPriceWon = 10_000_000, MaxTickVolume = 1_000_000_000;
        public const int WarmupTicks = 5;
        public const int EntryScore = 3000, ExitScore = -2000;
        public const int MacroWeight = 20, ImbalanceWeight = 35, SurgeWeight = 20, MomentumWeight = 25, MomentumGain = 20;
        public const int StopBp = 80, TargetBaseBp = 100, TargetPerConfidenceBp = 3, PositionWeightBp = 2000;
        public static readonly TimeSpan SessionStart = new(9, 0, 0), SessionEnd = new(15, 30, 0), AiCutoff = new(15, 20, 0);

        private const int EmaShift = 3;          // α = 1/8
        private const int Fx = 10;               // EWMA 고정소수점 ×1024
        private const int SeqlockRetries = 64;
        private const int QueueMask = HumanQueueCapacity - 1;

        private struct Sym { public long Symbol, Bid, Ask, BidQty, AskQty, EmaMidFx, EmaVolFx; public int Seen; }
        private struct Pos { public long Symbol, Qty, AvgPx, CostWon, Target, Stop, TradePnl; public int SymIdx, RiskRewardX100; }
        private struct HumanCell { public long Seq, Symbol, Qty; public int Side; }

        private sealed class Book
        {
            public readonly Pos[] Positions = new Pos[MaxPositions];
            public int Count, Wins, Losses;
            public long Cash, Realized, Fees, Tax, Peak, MddBp, Fills, Rejects;
        }

        private readonly Sym[] _syms = new Sym[MaxSymbols];
        private int _symCount;
        private readonly Book[] _books = { new Book(), new Book() };   // ArenaSide.Ai 0 · Human 1
        private readonly AiTradeRationale[] _rationales = new AiTradeRationale[RationaleCapacity];
        private long _rationaleCount;
        private readonly HumanCell[] _hq = new HumanCell[HumanQueueCapacity];
        private long _hqEnq, _hqDeq;
        private long _capital;
        private long _ticks, _dropped, _lastKstTicks;
        private bool _sessionOpen;
        private int _macroBp;
        private long _ver;
        private DuelSnapshot _snap;

        public KrxArenaEngine(long capitalWon = DefaultCapitalWon)
        {
            for (int i = 0; i < HumanQueueCapacity; i++) _hq[i].Seq = i;
            SetCapital(capitalWon);
        }

        public string ModeName => "국내 대형주(KRX)";
        public long CapitalWon => Interlocked.Read(ref _capital);
        public long Version => Volatile.Read(ref _ver);
        public int OvernightMacroBp => Volatile.Read(ref _macroBp);

        /// <summary>양 진영 원장 초기화(정산 스레드 · 대결 시작 전). 범위 밖 자본금 = ArgumentOutOfRange.</summary>
        public void SetCapital(long capitalWon)
        {
            if (capitalWon < 1_000_000 || capitalWon > 100_000_000_000) throw new ArgumentOutOfRangeException(nameof(capitalWon));
            BeginWrite();
            Interlocked.Exchange(ref _capital, capitalWon);
            for (int s = 0; s < ArenaSide.Count; s++)
            {
                var b = _books[s];
                Array.Clear(b.Positions);
                b.Count = b.Wins = b.Losses = 0;
                b.Cash = b.Peak = capitalWon;
                b.Realized = b.Fees = b.Tax = b.MddBp = b.Fills = b.Rejects = 0;
            }
            Array.Clear(_rationales);
            _rationaleCount = 0;
            PublishSnapshot();
            EndWrite();
        }

        public void SetOvernightMacro(int macroBp) => Volatile.Write(ref _macroBp, Math.Clamp(macroBp, -10000, 10000));

        public static bool IsRegularSession(DateTime kst)
        {
            if (kst.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
            var t = kst.TimeOfDay;
            return t >= SessionStart && t < SessionEnd;
        }

        // ── 휴먼 주문 큐(Vyukov 유계 MPSC · 할당 0) ──────────────────────────────────

        public int SubmitHuman(long symbol, int side, long qty)
        {
            if (symbol <= 0 || (side != 1 && side != -1) || qty <= 0 || qty > MaxOrderQty) return KrxArenaReject.Invalid;
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
                        return KrxArenaReject.None;
                    }
                    pos = Volatile.Read(ref _hqEnq);
                }
                else if (dif < 0) return KrxArenaReject.QueueFull;
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

        // ── 정산(단일 작성자) ────────────────────────────────────────────────────

        /// <summary>시세 없이 휴먼 큐만 체결(최신 호가 기준).</summary>
        public void Drain(DateTime kst)
        {
            BeginWrite();
            _sessionOpen = IsRegularSession(kst);
            _lastKstTicks = kst.Ticks;
            DrainHuman();
            PublishSnapshot();
            EndWrite();
        }

        /// <summary>
        /// 종목 1틱(최우선 호가 · 잔량 · 틱 거래량) 정산: 휴먼 큐 체결 → 시그널 평가 → AI 진입/청산(+근거) → 시가평가 게시.
        /// 무효 호가(가격 ≤ 0 · 역전 · 음수 잔량) · 종목 표 포화 = 폐기(DroppedQuotes 증가 · fail-closed).
        /// </summary>
        public void OnQuote(long symbol, long bidWon, long askWon, long bidQty, long askQty, long tickVolume, DateTime kst)
        {
            BeginWrite();
            _sessionOpen = IsRegularSession(kst);
            _lastKstTicks = kst.Ticks;
            int si = -1;
            if (symbol > 0 && bidWon > 0 && askWon >= bidWon && askWon <= MaxPriceWon && bidQty >= 0 && askQty >= 0 && tickVolume >= 0 && tickVolume <= MaxTickVolume && bidQty <= MaxTickVolume && askQty <= MaxTickVolume)
                si = FindOrAddSym(symbol);
            if (si < 0) _dropped++;
            else
            {
                _ticks++;
                ref Sym s = ref _syms[si];
                s.Bid = bidWon; s.Ask = askWon; s.BidQty = bidQty; s.AskQty = askQty;
                long midFx = (bidWon + askWon) << (Fx - 1);
                long volFx = tickVolume << Fx;
                var sig = s.Seen >= WarmupTicks
                    ? Evaluate(Volatile.Read(ref _macroBp), bidQty, askQty, volFx, s.EmaVolFx, midFx, s.EmaMidFx)
                    : default;
                if (s.Seen == 0) { s.EmaMidFx = midFx; s.EmaVolFx = volFx; }
                else { s.EmaMidFx += (midFx - s.EmaMidFx) >> EmaShift; s.EmaVolFx += (volFx - s.EmaVolFx) >> EmaShift; }
                if (s.Seen < int.MaxValue) s.Seen++;

                DrainHuman();
                if (_sessionOpen) StepAi(si, in sig, kst);
            }
            PublishSnapshot();
            EndWrite();
        }

        /// <summary>순수 시그널 평가(할당 0). volFx/emaVolFx/midFx/emaMidFx = ×1024 고정소수점.</summary>
        public static KrxAlphaSignal Evaluate(int macroBp, long bidQty, long askQty, long volFx, long emaVolFx, long midFx, long emaMidFx)
        {
            int macro = Math.Clamp(macroBp, -10000, 10000);
            long depth = bidQty + askQty;
            int obi = depth <= 0 ? 0 : (int)((bidQty - askQty) * 10000 / depth);
            int mom = emaMidFx <= 0 ? 0 : (int)Math.Clamp((midFx - emaMidFx) * 10000 / emaMidFx * MomentumGain, -10000, 10000);
            int surge = 0;
            if (emaVolFx > 0)
            {
                long ratioBp = volFx * 10000 / emaVolFx;
                int mag = (int)Math.Clamp(ratioBp - 10000, 0, 10000);
                int dir = midFx > emaMidFx ? 1 : midFx < emaMidFx ? -1 : (obi > 0 ? 1 : obi < 0 ? -1 : 0);
                surge = mag * dir;
            }
            long cMacro = (long)macro * MacroWeight, cObi = (long)obi * ImbalanceWeight, cSurge = (long)surge * SurgeWeight, cMom = (long)mom * MomentumWeight;
            int score = (int)((cMacro + cObi + cSurge + cMom) / 100);
            int sign = score >= 0 ? 1 : -1;
            var dom = ArenaTrigger.OrderbookImbalance;
            long best = cObi * sign;
            if (cSurge * sign > best) { best = cSurge * sign; dom = ArenaTrigger.VolumeSurge; }
            if (cMom * sign > best) { best = cMom * sign; dom = ArenaTrigger.MomentumBreakout; }
            if (cMacro * sign > best) dom = ArenaTrigger.MacroTailwind;
            return new KrxAlphaSignal(score, macro, obi, surge, mom, dom);
        }

        private void StepAi(int si, in KrxAlphaSignal sig, DateTime kst)
        {
            var b = _books[ArenaSide.Ai];
            ref Sym s = ref _syms[si];
            bool cutoff = kst.TimeOfDay >= AiCutoff;
            int pi = FindPos(b, s.Symbol);
            if (pi >= 0)
            {
                ref Pos p = ref b.Positions[pi];
                ArenaTrigger exit = ArenaTrigger.None;
                if (cutoff) exit = ArenaTrigger.SessionClose;
                else if (s.Bid >= p.Target) exit = ArenaTrigger.TakeProfit;
                else if (s.Bid <= p.Stop) exit = ArenaTrigger.StopLoss;
                else if (sig.Score <= ExitScore) exit = ArenaTrigger.SignalReversal;
                if (exit == ArenaTrigger.None) return;
                long target = p.Target, stop = p.Stop; int rr = p.RiskRewardX100;
                if (Sell(b, si, p.Qty, out long filled, out long pnl) != KrxArenaReject.None) return;
                int conf = exit == ArenaTrigger.SignalReversal ? Math.Clamp(-sig.Score / 100, 0, 100) : 100;
                Record(new AiTradeRationale(_rationaleCount + 1, s.Symbol, -1, filled, s.Bid, kst.Ticks, exit, sig.Score, conf,
                    target, stop, rr, sig.MacroBp, sig.ImbalanceBp, sig.SurgeBp, sig.MomentumBp, pnl));
                return;
            }
            if (cutoff || sig.Score < EntryScore || b.Count >= MaxAiPositions) return;
            int confidence = Math.Clamp(sig.Score / 100, 0, 100);
            long tgt = RoundUpTick(s.Ask * (10000 + TargetBaseBp + TargetPerConfidenceBp * confidence) / 10000);
            long stp = RoundDownTick(s.Ask * (10000 - StopBp) / 10000);
            if (stp <= 0 || stp >= s.Ask || tgt <= s.Ask) return;
            int rrX100 = (int)Math.Min((tgt - s.Ask) * 100 / (s.Ask - stp), 100_000);
            if (rrX100 <= 0) return;
            long budget = Math.Min(b.Cash, Equity(b) * PositionWeightBp / 10000);
            long qty = budget * 100_000 / (s.Ask * (100_000 + FeePer100k));
            if (qty <= 0) return;
            if (Buy(b, si, qty, out long bought) != KrxArenaReject.None) return;
            ref Pos np = ref b.Positions[FindPos(b, s.Symbol)];
            np.Target = tgt; np.Stop = stp; np.RiskRewardX100 = rrX100;
            Record(new AiTradeRationale(_rationaleCount + 1, s.Symbol, 1, bought, s.Ask, kst.Ticks, sig.Dominant, sig.Score, confidence,
                tgt, stp, rrX100, sig.MacroBp, sig.ImbalanceBp, sig.SurgeBp, sig.MomentumBp, 0));
        }

        private void DrainHuman()
        {
            var b = _books[ArenaSide.Human];
            while (TryDequeue(out long symbol, out int side, out long qty))
            {
                int rc;
                int si = FindSym(symbol);
                if (!_sessionOpen) rc = KrxArenaReject.SessionClosed;
                else if (si < 0) rc = KrxArenaReject.NoQuote;
                else if (side > 0) rc = Buy(b, si, qty, out _);
                else
                {
                    int pi = FindPos(b, symbol);
                    rc = pi < 0 || b.Positions[pi].Qty < qty ? KrxArenaReject.InsufficientPosition : Sell(b, si, qty, out _, out _);
                }
                if (rc != KrxArenaReject.None) b.Rejects++;
            }
        }

        private int Buy(Book b, int si, long qty, out long filled)
        {
            filled = 0;
            ref Sym s = ref _syms[si];
            qty = Math.Min(qty, s.AskQty);
            if (qty <= 0) return KrxArenaReject.NoLiquidity;
            long notional = qty * s.Ask;
            long fee = notional * FeePer100k / 100_000;
            long cost = notional + fee;
            if (cost > b.Cash) return KrxArenaReject.InsufficientCash;
            int pi = FindPos(b, s.Symbol);
            if (pi < 0)
            {
                if (b.Count >= MaxPositions) return KrxArenaReject.BookFull;
                pi = b.Count++;
                b.Positions[pi] = new Pos { Symbol = s.Symbol, SymIdx = si };
            }
            ref Pos p = ref b.Positions[pi];
            p.AvgPx = (p.AvgPx * p.Qty + notional) / (p.Qty + qty);
            p.Qty += qty;
            p.CostWon += cost;
            b.Cash -= cost; b.Fees += fee; b.Fills++;
            filled = qty;
            return KrxArenaReject.None;
        }

        private int Sell(Book b, int si, long qty, out long filled, out long pnl)
        {
            filled = 0; pnl = 0;
            ref Sym s = ref _syms[si];
            int pi = FindPos(b, s.Symbol);
            if (pi < 0) return KrxArenaReject.InsufficientPosition;
            ref Pos p = ref b.Positions[pi];
            qty = Math.Min(Math.Min(qty, p.Qty), s.BidQty);
            if (qty <= 0) return KrxArenaReject.NoLiquidity;
            long notional = qty * s.Bid;
            long fee = notional * FeePer100k / 100_000;
            long tax = notional * TaxPer100k / 100_000;
            long net = notional - fee - tax;
            long basis = p.CostWon * qty / p.Qty;
            pnl = net - basis;
            p.CostWon -= basis; p.Qty -= qty; p.TradePnl += pnl;
            b.Cash += net; b.Realized += pnl; b.Fees += fee; b.Tax += tax; b.Fills++;
            filled = qty;
            if (p.Qty == 0)
            {
                if (p.TradePnl > 0) b.Wins++; else b.Losses++;
                b.Positions[pi] = b.Positions[--b.Count];
                b.Positions[b.Count] = default;
            }
            return KrxArenaReject.None;
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
            for (int i = 0; i < b.Count; i++) cost += b.Positions[i].CostWon;
            if (eq > b.Peak) b.Peak = eq;
            if (b.Peak > 0) b.MddBp = Math.Max(b.MddBp, (b.Peak - eq) * 10000 / b.Peak);
            return new DuelBookStats(_capital, b.Cash, eq, b.Realized, eq - b.Cash - cost, b.Fees, b.Tax, b.Peak, b.MddBp, b.Fills, b.Rejects,
                b.Wins, b.Losses, b.Count);
        }

        private void PublishSnapshot()
        {
            var ai = Stats(_books[ArenaSide.Ai]);
            var human = Stats(_books[ArenaSide.Human]);
            _snap = new DuelSnapshot(_ticks, _dropped, _rationaleCount, _lastKstTicks, _sessionOpen, Volatile.Read(ref _macroBp), in ai, in human);
        }

        private int FindSym(long symbol)
        {
            for (int i = 0; i < _symCount; i++) if (_syms[i].Symbol == symbol) return i;
            return -1;
        }

        private int FindOrAddSym(long symbol)
        {
            int i = FindSym(symbol);
            if (i >= 0 || _symCount >= MaxSymbols) return i;
            _syms[_symCount] = new Sym { Symbol = symbol };
            return _symCount++;
        }

        private static int FindPos(Book b, long symbol)
        {
            for (int i = 0; i < b.Count; i++) if (b.Positions[i].Symbol == symbol) return i;
            return -1;
        }

        private static long RoundUpTick(long px)
        {
            long t = (long)KrxTickLadder.TickWonFor(px);
            return t <= 0 ? px : (px + t - 1) / t * t;
        }

        private static long RoundDownTick(long px)
        {
            long t = (long)KrxTickLadder.TickWonFor(px);
            return t <= 0 ? px : px / t * t;
        }

        // ── seqlock(단일 작성자 · 판독 할당 0) ─────────────────────────────────────

        private void BeginWrite() => Interlocked.Increment(ref _ver);
        private void EndWrite() => Interlocked.Increment(ref _ver);

        public bool TryReadSnapshot(out DuelSnapshot snapshot)
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
                    destination[k] = new DuelPosition(p.Symbol, p.Qty, p.AvgPx, p.CostWon, mark, p.Target, p.Stop);
                }
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v) return n;
            }
            return 0;
        }

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
