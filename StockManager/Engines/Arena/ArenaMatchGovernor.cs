// ============================================================================
// ArenaMatchGovernor — [v12.18.11-DEV] AI vs Human 실시간 대결 상태 머신(대기 → 진행 → 종료) + 무잠금 스냅샷 게시
//
//  ① 생산자(임의 스레드 · δ=0 · wait-free): OnQuote(L2 10호가 · 브리지 수신) · OnAiDecision(시그널 엔진 진입/청산 결정 — 허브 싱크 분기) ·
//     SubmitHuman(UI 퀵 티켓). 전부 고정 링(TelemetryRing)에 구조체 적재 후 즉시 반환 — 생산자는 대결 모드 존재를 느끼지 못한다.
//     Start/Stop = 명령어 1워드 원자 교체(마지막 명령 우선) — 원장 변경은 펌프만 한다.
//  ② 정산(Step · 50ms 허브 펌프 단일 스레드): 명령 반영 → 호가 링 소비(종목별 최신) → 주문 링 소비(DualBookSimulator 가상 체결) →
//     양측 시가평가 · 고점 · MDD · 리더 → 자산곡선 표본(기본 1초 · 600점) → seqlock 게시(홀수 = 작성 중 · 짝수 = 안정).
//  ③ 소비자(UI): TryRead / CopyEquity = seqlock 재시도 사본(잠금 · 대기 · 할당 0). 작성자는 독자를 기다리지 않는다.
//  AI 주문 규칙(가상): 진입 = 미보유 종목만 자본금 × AiLotBp(기본 10%) ÷ 최우선 매도호가 수량 시장가 매수 · 청산 = 보유 전량 시장가 매도.
//  ★Fail-Closed: 브로커 · 주문 디스패처 · 무장 키 참조 0 — liveArmed 상태와 무관하게 아레나 주문은 가상 원장 밖으로 나갈 경로가 없다.
// ============================================================================
using System;
using System.Threading;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;

namespace StockManager.Engines.Arena
{
    /// <summary>아레나 주문 링 항목. Kind 0 = 휴먼 시장가 · 1 = AI 진입 · 2 = AI 청산.</summary>
    public struct ArenaOrder { public long Symbol, Qty; public int Dir, Kind; }

    public sealed class ArenaMatchGovernor : IArenaPort
    {
        public const long MinCapitalWon = 1_000_000, MaxCapitalWon = 10_000_000_000, DefaultCapitalWon = 10_000_000;
        public const int MaxEquity = 600, ReadSpins = 64;
        public const int KindHuman = 0, KindAiEntry = 1, KindAiExit = 2;
        private const long CmdNone = 0, CmdStop = -1;
        private const long PumpStaleTicks = 2 * 10_000_000L;

        private readonly DualBookSimulator _sim = new();
        private readonly Func<long> _nowTicks;
        private readonly int _aiLotBp;
        private int _equityEvery;

        private readonly TelemetryRing<OrderBookPayload> _quotes = new(256);
        private readonly TelemetryRing<ArenaOrder> _orders = new(128);
        private long _quoteCursor, _orderCursor, _quotesLost, _ordersLost, _quotesSeen, _aiSignals;
        private long _cmd;
        private int _state;
        private long _capital, _startedTicks, _lastStep, _steps;
        private long _eqAi, _eqHuman, _unrealAi, _unrealHuman;

        // ── seqlock 게시 영역(작성자 = Step) ──
        private long _seq;
        private ArenaSnapshot _snap;
        private readonly long[] _curveAi = new long[MaxEquity], _curveHuman = new long[MaxEquity];
        private long _curveN;

        /// <param name="nowTicks">시계(UTC ticks · 테스트 주입).</param>
        /// <param name="equitySampleEverySteps">자산곡선 표본 간격(Step 회수 · 기본 20 = 50ms 펌프 기준 1초).</param>
        /// <param name="aiLotBp">AI 진입 1회 예산(자본금 대비 bp · 기본 1000 = 10%).</param>
        public ArenaMatchGovernor(Func<long>? nowTicks = null, int equitySampleEverySteps = 20, int aiLotBp = 1_000)
        {
            _nowTicks = nowTicks ?? (static () => DateTime.UtcNow.Ticks);
            _equityEvery = Math.Max(1, equitySampleEverySteps);
            _aiLotBp = Math.Clamp(aiLotBp, 1, 10_000);
        }

        public DualBookSimulator Simulator => _sim;
        public int State => Volatile.Read(ref _state);
        public long Steps => Interlocked.Read(ref _steps);

        // ════════════ ① 생산자(δ=0 · wait-free) ════════════

        /// <summary>L2 10호가 1건(브리지 수신 스레드). 펌프가 종목별 최신으로 반영.</summary>
        public void OnQuote(in OrderBookPayload b) => _quotes.Publish(in b);

        /// <summary>시그널 엔진 결정 분기(DashDecisionKind 진입 1 · 청산 2 만 · 그 외 무시). 매치 진행 중일 때만 적재.</summary>
        public void OnAiDecision(int kind, long symbol)
        {
            if (symbol <= 0 || Volatile.Read(ref _state) != ArenaState.Running) return;
            if (kind == DashDecisionKind.Entry) _orders.Publish(new ArenaOrder { Symbol = symbol, Dir = 1, Kind = KindAiEntry });
            else if (kind == DashDecisionKind.Exit) _orders.Publish(new ArenaOrder { Symbol = symbol, Dir = -1, Kind = KindAiExit });
        }

        public int SubmitHuman(long symbol, int side, long qty)
        {
            if (Volatile.Read(ref _state) != ArenaState.Running) return ArenaReject.NotRunning;
            if (symbol <= 0 || side is not (1 or -1) || qty <= 0 || qty > DualBookSimulator.MaxOrderQty) return ArenaReject.Invalid;
            _orders.Publish(new ArenaOrder { Symbol = symbol, Qty = qty, Dir = side, Kind = KindHuman });
            return ArenaReject.None;
        }

        public bool Start(long capitalWon)
        {
            if (capitalWon < MinCapitalWon || capitalWon > MaxCapitalWon) return false;
            Interlocked.Exchange(ref _cmd, capitalWon);
            return true;
        }

        public void Stop() => Interlocked.Exchange(ref _cmd, CmdStop);

        // ════════════ ② 정산(펌프 단일 스레드 · δ=0) ════════════

        /// <summary>정산 1회차(허브 50ms 펌프). 단일 스레드 계약 — 동시 호출 금지(허브 펌프는 단일 진입 CAS 로 직렬화).</summary>
        public void Step(long nowTicks)
        {
            _steps++;
            long cmd = Interlocked.Exchange(ref _cmd, CmdNone);
            if (cmd == CmdStop && _state == ArenaState.Running) Volatile.Write(ref _state, ArenaState.Finished);
            else if (cmd > 0) Begin(cmd, nowTicks);

            DrainQuotes();
            DrainOrders();

            if (_state == ArenaState.Running)
            {
                _eqAi = _sim.MarkToMarket(ArenaSide.Ai, out _unrealAi);
                _eqHuman = _sim.MarkToMarket(ArenaSide.Human, out _unrealHuman);
            }
            Publish(nowTicks, sample: _state == ArenaState.Running && _steps % _equityEvery == 0);
            Volatile.Write(ref _lastStep, nowTicks);
        }

        // ── [v12.18.12-DEV] 리플레이 구동(ArenaTickReplayEngine 전용 · 재생 스레드 = 단일 작성자 · 허브 Step 과 같은 인스턴스에 혼용 금지) ──

        /// <summary>
        /// 틱 1건 정산: 명령 → 호가 직접 반영(링 경유 0 — 최대 배속에서도 유실 0) → 주문 링 소비(AI · 휴먼) → 시가평가 → 자산곡선 표본(Step 수 기준).
        /// 스냅샷 게시는 하지 않는다 — 재생 속도와 무관하게 PublishNow 를 50ms 주기로 따로 호출(UI 프레임 디커플링).
        /// </summary>
        public void Advance(in OrderBookPayload quote, long nowTicks)
        {
            _steps++;
            long cmd = Interlocked.Exchange(ref _cmd, CmdNone);
            if (cmd == CmdStop && _state == ArenaState.Running) Volatile.Write(ref _state, ArenaState.Finished);
            else if (cmd > 0) Begin(cmd, nowTicks);
            _quotesSeen++;
            _sim.OnQuote(in quote);
            DrainOrders();
            if (_state != ArenaState.Running) return;
            _eqAi = _sim.MarkToMarket(ArenaSide.Ai, out _unrealAi);
            _eqHuman = _sim.MarkToMarket(ArenaSide.Human, out _unrealHuman);
            if (_steps % _equityEvery == 0) SampleEquity();
        }

        /// <summary>현재 정산 상태 스냅샷 게시(seqlock · δ=0) + 펌프 생존 표지.</summary>
        public void PublishNow(long nowTicks)
        {
            Publish(nowTicks, sample: false);
            Volatile.Write(ref _lastStep, nowTicks);
        }

        /// <summary>원장 초기화(양측 동일 자본금 · 잔여 주문 폐기) 후 진행(running) 또는 대기 상태로 즉시 게시. 자산곡선 표본 간격 재설정.</summary>
        public void Rearm(long capitalWon, bool running, int equitySampleEverySteps, long nowTicks)
        {
            Interlocked.Exchange(ref _cmd, CmdNone);
            _equityEvery = Math.Max(1, equitySampleEverySteps);
            Begin(Math.Clamp(capitalWon, MinCapitalWon, MaxCapitalWon), nowTicks);
            _quotesSeen = 0;
            if (!running) Volatile.Write(ref _state, ArenaState.Idle);
            PublishNow(nowTicks);
        }

        /// <summary>매치 종료(진행 중일 때만) + 최종 표본 · 게시 — 이후 원장 동결.</summary>
        public void Finish(long nowTicks)
        {
            if (_state == ArenaState.Running)
            {
                SampleEquity();
                Volatile.Write(ref _state, ArenaState.Finished);
            }
            PublishNow(nowTicks);
        }

        private void SampleEquity()
        {
            long s = _seq;
            Volatile.Write(ref _seq, s + 1);
            Interlocked.MemoryBarrier();
            int k = (int)(_curveN % MaxEquity);
            _curveAi[k] = _eqAi; _curveHuman[k] = _eqHuman;
            _curveN++;
            Volatile.Write(ref _seq, s + 2);
        }

        private void Begin(long capital, long nowTicks)
        {
            _sim.Reset(capital);
            _capital = capital; _startedTicks = nowTicks;
            _eqAi = _eqHuman = capital; _unrealAi = _unrealHuman = 0;
            _aiSignals = 0;
            _orderCursor = _orders.Head;                                            // 이전 매치 잔여 주문 폐기(재시도 · 이월 0)
            Volatile.Write(ref _seq, _seq + 1);
            Interlocked.MemoryBarrier();
            _curveN = 0;
            Volatile.Write(ref _seq, _seq + 1);
            Volatile.Write(ref _state, ArenaState.Running);
        }

        private void DrainQuotes()
        {
            long head = _quotes.Head;
            if (head - _quoteCursor > _quotes.Capacity) { _quotesLost += head - _quotes.Capacity - _quoteCursor; _quoteCursor = head - _quotes.Capacity; }
            for (; _quoteCursor < head; _quoteCursor++)
            {
                int r = _quotes.TryRead(_quoteCursor, out var b);
                if (r == 0) break;
                if (r < 0) { _quotesLost++; continue; }
                _quotesSeen++;
                _sim.OnQuote(in b);
            }
        }

        private void DrainOrders()
        {
            long head = _orders.Head;
            if (head - _orderCursor > _orders.Capacity) { _ordersLost += head - _orders.Capacity - _orderCursor; _orderCursor = head - _orders.Capacity; }
            for (; _orderCursor < head; _orderCursor++)
            {
                int r = _orders.TryRead(_orderCursor, out var o);
                if (r == 0) break;
                if (r < 0) { _ordersLost++; continue; }
                if (_state != ArenaState.Running) continue;                         // 종료 후 도착 = 폐기(가상 원장 동결)
                Execute(in o);
            }
        }

        private void Execute(in ArenaOrder o)
        {
            switch (o.Kind)
            {
                case KindHuman:
                    _sim.ExecuteMarket(ArenaSide.Human, o.Symbol, o.Dir, o.Qty);
                    break;
                case KindAiEntry:
                    _aiSignals++;
                    if (_sim.Book(ArenaSide.Ai).Position(o.Symbol) != 0) break;    // 미보유 종목만 진입(물타기 0)
                    long ask = _sim.BestAsk(o.Symbol);
                    long budget = (long)((Int128)_capital * _aiLotBp / 10_000);
                    _sim.ExecuteMarket(ArenaSide.Ai, o.Symbol, 1, ask > 0 ? Math.Clamp(budget / ask, 1, DualBookSimulator.MaxOrderQty) : 1);
                    break;
                case KindAiExit:
                    _aiSignals++;
                    long held = _sim.Book(ArenaSide.Ai).Position(o.Symbol);
                    if (held > 0) _sim.ExecuteMarket(ArenaSide.Ai, o.Symbol, -1, held);
                    break;
            }
        }

        private void Publish(long nowTicks, bool sample)
        {
            var ai = Stats(ArenaSide.Ai, _eqAi, _unrealAi);
            var hu = Stats(ArenaSide.Human, _eqHuman, _unrealHuman);
            int leader = _state == ArenaState.Idle || _eqAi == _eqHuman ? ArenaLeader.Draw : _eqAi > _eqHuman ? ArenaLeader.Ai : ArenaLeader.Human;
            long s = _seq;
            Volatile.Write(ref _seq, s + 1);                                         // 작성 중(홀수)
            Interlocked.MemoryBarrier();
            if (sample)
            {
                int k = (int)(_curveN % MaxEquity);
                _curveAi[k] = _eqAi; _curveHuman[k] = _eqHuman;
                _curveN++;
            }
            _snap = new ArenaSnapshot((s + 2) >> 1, _state, _capital, _startedTicks, nowTicks, _quotesSeen, _quotesLost, _sim.QuoteSymbols,
                                      _ordersLost, _aiSignals, leader, Math.Abs(_eqAi - _eqHuman), in ai, in hu);
            Volatile.Write(ref _seq, s + 2);                                         // 안정(짝수)
        }

        private ArenaSideStats Stats(int side, long equity, long unrealized)
        {
            var b = _sim.Book(side);
            int mddBp = b.PeakWon > 0 ? (int)Math.Min(10_000, (long)((Int128)b.MddWon * 10_000 / b.PeakWon)) : 0;
            return new ArenaSideStats(equity, b.CashWon, b.RealizedWon, unrealized, b.FeesWon, b.TaxWon, b.SlippageWon, b.PeakWon, b.MddWon, mddBp,
                                      b.Fills, b.Rejects, b.Wins, b.Losses, b.PosCount, b.LastReject);
        }

        // ════════════ ③ 소비자(seqlock 사본 · 잠금/할당 0) ════════════

        public bool Pumped
        {
            get { long t = Volatile.Read(ref _lastStep); return t != 0 && _nowTicks() - t < PumpStaleTicks; }
        }

        public long Version => Volatile.Read(ref _seq) >> 1;

        public bool TryRead(out ArenaSnapshot snapshot)
        {
            for (int spin = 0; spin < ReadSpins; spin++)
            {
                long s1 = Volatile.Read(ref _seq);
                if ((s1 & 1) != 0) { Thread.SpinWait(8); continue; }
                snapshot = _snap;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _seq) == s1) return s1 != 0;
            }
            snapshot = default;
            return false;
        }

        public int CopyEquity(int side, Span<long> destination)
        {
            var src = side == ArenaSide.Human ? _curveHuman : _curveAi;
            for (int spin = 0; spin < ReadSpins; spin++)
            {
                long s1 = Volatile.Read(ref _seq);
                if ((s1 & 1) != 0) { Thread.SpinWait(8); continue; }
                long n = _curveN;
                int count = (int)Math.Min(Math.Min(n, MaxEquity), destination.Length);
                long first = n - count;
                for (int i = 0; i < count; i++) destination[i] = src[(int)((first + i) % MaxEquity)];
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _seq) == s1) return count;
            }
            return 0;
        }
    }
}
