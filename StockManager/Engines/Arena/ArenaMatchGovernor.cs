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
//  [v12.18.15-DEV] AI 페르소나(ActivePersona): 0 = 시그널 엔진 결정 분기(기존) · 1 스캘퍼 · 2 추세추종 · 3 평균회귀(IArenaStrategy · 독립 인스턴스).
//   대상 종목 1개(포커스 우선 · 없으면 첫 수신 종목 · 무포지션일 때만 재지정)의 호가/체결 → 전략 · ArenaVwapTracker → 틱마다 Evaluate →
//   원장 재확인(무포지션 · 미완료 주문 0) 후 AI 시장가 접수. 교체(SetPersona) = 다음 회차 반영 · 진행 중이면 AI 비상 전량 청산 후 새 전략이 빈 원장에서 시작.
//   시그널 엔진 외 페르소나 활성 중 OnAiDecision 은 무시(두 두뇌 동시 발주 0). HUD(VWAP · 이격률 · σ · 잔량 불균형 · 목표/손절 · 사유)는 같은 seqlock 창 게시.
//  [v12.18.18-DEV] 초현실 아레나:
//   · 휴먼 빙산 지정가(SubmitHumanIceberg → 시뮬레이터 Peak 노출 · 리필 시 대기열 맨 뒤) · 시뮬레이터 유동성 진공 페널티(양 진영 공통).
//   · 포식자 페르소나(4): 매 회차 휴먼 사후 분석(연패 · 틸트 · 미청산 MAE)을 역추적 주입 → 공격성 · 진입 임계 · 목표 틱 조절 · 은밀(공격성 &lt; 50%) = TWAP 분할 진입.
//     휴먼 연패 ≥ 2 = 게시 시 휴먼 DOM 래더(포커스 = 대상 종목) 1호가 표시 잔량에만 허수 가산(가상 · 체결/대기열 계산 무관 · HUD 공개).
//   · 하드 리스크 한도(riskLimitBp · 기본 300 = −3%): 시가평가 후 휴먼 자산 ≤ 자본금 × (1 − 한도) → 같은 정산 회차 안에서 동결 래치 + 시뮬레이터 MarginCall
//     (지연 대기 · 제어 주문 소거 + 미체결 취소 + 보유 전량 시장가 매도 지연 0) — 이후 이 라운드의 휴먼 발주(시장가 · 지정가 · 빙산 · 취소 · 청산 · 핫키 · 래더)는
//     생산자 단계(Volatile 래치) · 정산 단계(동결 원장 Submit 거부) 이중으로 RiskLocked. 해제 = 다음 매치 개시(Begin)뿐.
// ============================================================================
using System;
using System.Threading;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;
using StockManager.Engines.Arena.Strategy;

namespace StockManager.Engines.Arena
{
    /// <summary>아레나 주문 링 항목. Kind 0 = 휴먼 시장가 · 1 = AI 진입 · 2 = AI 청산 · 3 = 휴먼 지정가 · 4 = 휴먼 전량 취소 · 5 = 휴먼 비상 청산 · 6 = 휴먼 빙산(Peak).</summary>
    public struct ArenaOrder { public long Symbol, Qty, LimitPx, Peak; public int Dir, Kind; }

    /// <summary>
    /// [v12.18.13-DEV] 누적 거래량 → 체결 틱 거래량(키움 실시간 FID 13 = 누적). 종목별 직전 누적(개방 주소 고정 표 · 단일 작성자 · δ=0).
    /// 첫 관측 = 기준선만(거래량 0) · 감소(장 재시작/정정) = 기준선 재설정.
    /// </summary>
    public sealed class CumulativeVolumeTracker
    {
        public const int Slots = 512;
        private readonly long[] _sym = new long[Slots], _cum = new long[Slots];
        private long _full;
        public long TableFull => _full;

        public long Delta(long symbol, long cumulative)
        {
            if (symbol <= 0 || cumulative < 0) return 0;
            int h = (int)((ulong)symbol * 0x9E3779B97F4A7C15UL >> 55);
            for (int k = 0; k < Slots; k++)
            {
                int i = (h + k) & (Slots - 1);
                if (_sym[i] == symbol)
                {
                    long d = cumulative - _cum[i];
                    _cum[i] = cumulative;
                    return d > 0 ? d : 0;
                }
                if (_sym[i] == 0) { _sym[i] = symbol; _cum[i] = cumulative; return 0; }
            }
            _full++;
            return 0;
        }

        public void Clear() { Array.Clear(_sym); Array.Clear(_cum); }
    }

    public sealed class ArenaMatchGovernor : IArenaPort
    {
        public const long MinCapitalWon = 1_000_000, MaxCapitalWon = 10_000_000_000, DefaultCapitalWon = 10_000_000;
        public const int MaxEquity = 600, ReadSpins = 64;
        public const int KindHuman = 0, KindAiEntry = 1, KindAiExit = 2, KindHumanLimit = 3, KindHumanCancel = 4, KindHumanFlatten = 5, KindHumanIceberg = 6;
        /// <summary>[v12.18.18-DEV] 기본 일일 손실 한도(bp · 300 = −3%) · 포식자 은밀 진입 TWAP(4 슬라이스 · 100ms 간격).</summary>
        public const int DefaultRiskLimitBp = 300, PredatorTwapSlices = 4;
        public const long PredatorTwapIntervalTicks = 1_000_000;
        private const long CmdNone = 0, CmdStop = -1;
        private const long PumpStaleTicks = 2 * 10_000_000L;

        private readonly DualBookSimulator _sim;
        private readonly Func<long> _nowTicks;
        private readonly int _aiLotBp;
        private int _equityEvery;

        private readonly TelemetryRing<OrderBookPayload> _quotes = new(256);
        private readonly TelemetryRing<ArenaOrder> _orders = new(128);
        private readonly TelemetryRing<ArenaTrade> _trades = new(1024);
        private readonly TelemetryRing<ArenaTrade> _directTrades = new(1024);        // [v12.18.16-DEV] 건별 체결량(KIS)
        private long _directCursor;
        private readonly CumulativeVolumeTracker _volume = new();
        private long _quoteCursor, _orderCursor, _tradeCursor, _quotesLost, _ordersLost, _tradesLost, _quotesSeen, _tradesSeen, _aiSignals;
        private long _cmd;
        private int _state;
        private long _capital, _startedTicks, _lastStep, _steps;
        private long _eqAi, _eqHuman, _unrealAi, _unrealHuman;

        // ── seqlock 게시 영역(작성자 = Step) ──
        private long _seq;
        private ArenaSnapshot _snap;
        private readonly long[] _curveAi = new long[MaxEquity], _curveHuman = new long[MaxEquity];
        private long _curveN;
        // [v12.18.14-DEV] DOM 래더 포커스(UI 가 Volatile 기록) · 래더/복기 사본(같은 seqlock 창에서 게시)
        private long _focus;
        private ArenaDepth _depth;
        private ArenaDebrief _debriefAi, _debriefHuman;
        // [v12.18.15-DEV] AI 페르소나(정산 스레드 단일 작성자 · _persona 만 생산자 스레드가 Volatile 판독)
        private readonly PredatorStrategy _predator = new();
        private readonly IArenaStrategy?[] _strategies;
        private readonly ArenaVwapTracker _vwap = new();
        private int _persona, _personaCmd = -1, _switches, _imbalanceBp = 5_000;
        private long _target, _spread, _decisions;
        private StrategySignal _signal;
        // [v12.18.18-DEV] 하드 리스크(정산 스레드 단일 작성자 · _humanLocked 만 생산자 스레드가 Volatile 판독)
        private readonly int _riskLimitBp;
        private int _humanLocked;
        private long _riskTripTicks, _riskTripEquity;

        /// <param name="nowTicks">시계(UTC ticks · 테스트 주입).</param>
        /// <param name="equitySampleEverySteps">자산곡선 표본 간격(Step 회수 · 기본 20 = 50ms 펌프 기준 1초).</param>
        /// <param name="aiLotBp">AI 진입 1회 예산(자본금 대비 bp · 기본 1000 = 10%).</param>
        /// <param name="latencyMinTicks">[v12.18.13-DEV] 체결 지연 하한(UTC ticks · 기본 30ms).</param>
        /// <param name="latencyMaxTicks">체결 지연 상한(기본 50ms).</param>
        /// <param name="riskLimitBp">[v12.18.18-DEV] 휴먼 일일 손실 한도(자본금 대비 bp · 기본 300 = −3% · 0 = 비활성 · 상한 10000).</param>
        public ArenaMatchGovernor(Func<long>? nowTicks = null, int equitySampleEverySteps = 20, int aiLotBp = 1_000,
                                  long latencyMinTicks = DualBookSimulator.DefaultLatencyMinTicks, long latencyMaxTicks = DualBookSimulator.DefaultLatencyMaxTicks,
                                  int riskLimitBp = DefaultRiskLimitBp)
        {
            _sim = new DualBookSimulator(latencyMinTicks, latencyMaxTicks);
            _strategies = new IArenaStrategy?[] { null, new ScalperStrategy(), new TrendStrategy(), new MeanReversionStrategy(), _predator };
            _riskLimitBp = Math.Clamp(riskLimitBp, 0, 10_000);
            _nowTicks = nowTicks ?? (static () => DateTime.UtcNow.Ticks);
            _equityEvery = Math.Max(1, equitySampleEverySteps);
            _aiLotBp = Math.Clamp(aiLotBp, 1, 10_000);
        }

        public DualBookSimulator Simulator => _sim;
        public long TradesSeen => Interlocked.Read(ref _tradesSeen);
        public int State => Volatile.Read(ref _state);
        public long Steps => Interlocked.Read(ref _steps);
        public int ActivePersona => Volatile.Read(ref _persona);
        public IArenaStrategy? Strategy(int persona) => ArenaPersona.IsValid(persona) ? _strategies[persona] : null;
        public ArenaVwapTracker Vwap => _vwap;
        public long TargetSymbol => _target;
        public StrategySignal LastSignal => _signal;
        public PredatorStrategy Predator => _predator;
        /// <summary>[v12.18.18-DEV] 휴먼 리스크 동결 래치(이번 라운드).</summary>
        public bool HumanRiskLocked => Volatile.Read(ref _humanLocked) != 0;
        public int RiskLimitBp => _riskLimitBp;

        // ════════════ ① 생산자(δ=0 · wait-free) ════════════

        /// <summary>L2 10호가 1건(브리지 수신 스레드). 펌프가 종목별 최신으로 반영.</summary>
        public void OnQuote(in OrderBookPayload b) => _quotes.Publish(in b);

        /// <summary>시그널 엔진 결정 분기(DashDecisionKind 진입 1 · 청산 2 만 · 그 외 무시). 매치 진행 중일 때만 적재.</summary>
        public void OnAiDecision(int kind, long symbol)
        {
            if (symbol <= 0 || Volatile.Read(ref _state) != ArenaState.Running || Volatile.Read(ref _persona) != ArenaPersona.Signal) return;
            if (kind == DashDecisionKind.Entry) _orders.Publish(new ArenaOrder { Symbol = symbol, Dir = 1, Kind = KindAiEntry });
            else if (kind == DashDecisionKind.Exit) _orders.Publish(new ArenaOrder { Symbol = symbol, Dir = -1, Kind = KindAiExit });
        }

        public int SubmitHuman(long symbol, int side, long qty)
        {
            if (Volatile.Read(ref _state) != ArenaState.Running) return ArenaReject.NotRunning;
            if (Volatile.Read(ref _humanLocked) != 0) return ArenaReject.RiskLocked;
            if (symbol <= 0 || side is not (1 or -1) || qty <= 0 || qty > DualBookSimulator.MaxOrderQty) return ArenaReject.Invalid;
            _orders.Publish(new ArenaOrder { Symbol = symbol, Qty = qty, Dir = side, Kind = KindHuman });
            return ArenaReject.None;
        }

        public int SubmitHumanLimit(long symbol, int side, long qty, long priceWon)
        {
            if (Volatile.Read(ref _state) != ArenaState.Running) return ArenaReject.NotRunning;
            if (Volatile.Read(ref _humanLocked) != 0) return ArenaReject.RiskLocked;
            if (symbol <= 0 || side is not (1 or -1) || qty <= 0 || qty > DualBookSimulator.MaxOrderQty || priceWon <= 0 || priceWon > DualBookSimulator.MaxPriceWon) return ArenaReject.Invalid;
            _orders.Publish(new ArenaOrder { Symbol = symbol, Qty = qty, Dir = side, LimitPx = priceWon, Kind = KindHumanLimit });
            return ArenaReject.None;
        }

        public int SubmitHumanIceberg(long symbol, int side, long qty, long priceWon, long peakQty)
        {
            if (Volatile.Read(ref _state) != ArenaState.Running) return ArenaReject.NotRunning;
            if (Volatile.Read(ref _humanLocked) != 0) return ArenaReject.RiskLocked;
            if (symbol <= 0 || side is not (1 or -1) || qty <= 0 || qty > DualBookSimulator.MaxOrderQty || priceWon <= 0 || priceWon > DualBookSimulator.MaxPriceWon || peakQty <= 0)
                return ArenaReject.Invalid;
            _orders.Publish(new ArenaOrder { Symbol = symbol, Qty = qty, Dir = side, LimitPx = priceWon, Peak = peakQty, Kind = KindHumanIceberg });
            return ArenaReject.None;
        }

        public int FlattenHuman()
        {
            if (Volatile.Read(ref _state) != ArenaState.Running) return ArenaReject.NotRunning;
            if (Volatile.Read(ref _humanLocked) != 0) return ArenaReject.RiskLocked;
            _orders.Publish(new ArenaOrder { Kind = KindHumanFlatten });
            return ArenaReject.None;
        }

        public void SetFocus(long symbol) => Volatile.Write(ref _focus, symbol > 0 ? symbol : 0);

        public int CancelHuman()
        {
            if (Volatile.Read(ref _state) != ArenaState.Running) return ArenaReject.NotRunning;
            if (Volatile.Read(ref _humanLocked) != 0) return ArenaReject.RiskLocked;
            _orders.Publish(new ArenaOrder { Kind = KindHumanCancel });
            return ArenaReject.None;
        }

        /// <summary>[v12.18.13-DEV] 실시간 체결 틱(브리지 수신 스레드 · 누적 거래량 그대로) — 펌프가 종목별 차분으로 체결 거래량 산출(δ=0).</summary>
        public void OnTrade(long symbol, long priceWon, long cumulativeVolume)
        {
            if (symbol <= 0 || priceWon <= 0 || cumulativeVolume < 0) return;
            _trades.Publish(new ArenaTrade { Symbol = symbol, PriceWon = priceWon, Qty = cumulativeVolume });
        }

        /// <summary>
        /// [v12.18.16-DEV] 건별 체결(KIS H0STCNT0 체결량 · 주도 방향 +1/−1/0) — 누적 차분 표를 거치지 않는 별도 링(δ=0 · 다중 생산자 안전).
        /// 같은 종목을 브리지(누적)와 함께 받아도 누적값 교차로 인한 유령 거래량이 생기지 않는다.
        /// </summary>
        public void OnTradeQty(long symbol, long priceWon, long qty, int side)
        {
            if (symbol <= 0 || priceWon <= 0 || qty <= 0) return;
            _directTrades.Publish(new ArenaTrade { Symbol = symbol, PriceWon = priceWon, Qty = qty, Side = Math.Sign(side) });
        }

        public bool Start(long capitalWon)
        {
            if (capitalWon < MinCapitalWon || capitalWon > MaxCapitalWon) return false;
            Interlocked.Exchange(ref _cmd, capitalWon);
            return true;
        }

        public void Stop() => Interlocked.Exchange(ref _cmd, CmdStop);

        public bool SetPersona(int persona)
        {
            if (!ArenaPersona.IsValid(persona)) return false;
            Interlocked.Exchange(ref _personaCmd, persona);
            return true;
        }

        // ════════════ ② 정산(펌프 단일 스레드 · δ=0) ════════════

        /// <summary>정산 1회차(허브 50ms 펌프). 단일 스레드 계약 — 동시 호출 금지(허브 펌프는 단일 진입 CAS 로 직렬화).</summary>
        public void Step(long nowTicks)
        {
            _steps++;
            long cmd = Interlocked.Exchange(ref _cmd, CmdNone);
            if (cmd == CmdStop && _state == ArenaState.Running) Volatile.Write(ref _state, ArenaState.Finished);
            else if (cmd > 0) Begin(cmd, nowTicks);
            ApplyPersona(nowTicks);

            DrainQuotes();
            _sim.SetClock(nowTicks);
            DrainTrades();
            RunPersona(nowTicks);
            DrainOrders(nowTicks);
            _sim.Process(nowTicks);

            if (_state == ArenaState.Running)
            {
                _eqAi = _sim.MarkToMarket(ArenaSide.Ai, out _unrealAi);
                _eqHuman = _sim.MarkToMarket(ArenaSide.Human, out _unrealHuman);
                CheckRisk(nowTicks);
            }
            Publish(nowTicks, sample: _state == ArenaState.Running && _steps % _equityEvery == 0);
            Volatile.Write(ref _lastStep, nowTicks);
        }

        // ── [v12.18.12-DEV] 리플레이 구동(ArenaTickReplayEngine 전용 · 재생 스레드 = 단일 작성자 · 허브 Step 과 같은 인스턴스에 혼용 금지) ──

        /// <summary>
        /// 틱 1건 정산: 명령 → 호가 직접 반영(링 경유 0 — 최대 배속에서도 유실 0) → 주문 링 소비(AI · 휴먼) → 시가평가 → 자산곡선 표본(Step 수 기준).
        /// 스냅샷 게시는 하지 않는다 — 재생 속도와 무관하게 PublishNow 를 50ms 주기로 따로 호출(UI 프레임 디커플링).
        /// </summary>
        public void Advance(in OrderBookPayload quote, long nowTicks) => Advance(in quote, hasQuote: true, quote.TimestampTicks > 0 ? quote.TimestampTicks : nowTicks, nowTicks);

        /// <summary>
        /// [v12.18.13-DEV] 틱 1건 정산(시장 시각 = 테이프 시각 — 체결 지연 판정 기준). hasQuote = false 면 체결 틱 전용 레코드(호가 갱신 없이 지연 경과 판정만).
        /// </summary>
        public void Advance(in OrderBookPayload quote, bool hasQuote, long marketTicks, long nowTicks)
        {
            _steps++;
            long cmd = Interlocked.Exchange(ref _cmd, CmdNone);
            if (cmd == CmdStop && _state == ArenaState.Running) Volatile.Write(ref _state, ArenaState.Finished);
            else if (cmd > 0) Begin(cmd, nowTicks);
            ApplyPersona(marketTicks);
            DrainOrders(marketTicks);
            if (hasQuote) { _quotesSeen++; _sim.OnQuote(in quote); FeedQuote(in quote); }
            RunPersona(marketTicks);
            _sim.Process(marketTicks);
            if (_state != ArenaState.Running) return;
            _eqAi = _sim.MarkToMarket(ArenaSide.Ai, out _unrealAi);
            _eqHuman = _sim.MarkToMarket(ArenaSide.Human, out _unrealHuman);
            CheckRisk(marketTicks);
            if (_steps % _equityEvery == 0) SampleEquity();
        }

        /// <summary>[v12.18.13-DEV] 체결 틱 직접 반영(리플레이 · 체결 거래량 그대로 · 단일 작성자) — 작동 지정가 대기열 소진/체결.</summary>
        public void ApplyTrade(in ArenaTrade trade, long marketTicks = 0)
        {
            _tradesSeen++;
            if (marketTicks > 0) _sim.SetClock(marketTicks);
            if (_state == ArenaState.Running) _sim.OnTrade(in trade);
            FeedTrade(in trade);
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
            _tradesSeen = 0;
            _riskTripTicks = _riskTripEquity = 0;
            Volatile.Write(ref _humanLocked, 0);                                     // 라운드 경계 = 동결 해제(원장도 Reset 으로 해제)
            ResetPersonaState();
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
                FeedQuote(in b);
            }
        }

        private void DrainTrades()
        {
            long head = _trades.Head;
            if (head - _tradeCursor > _trades.Capacity) { _tradesLost += head - _trades.Capacity - _tradeCursor; _tradeCursor = head - _trades.Capacity; }
            for (; _tradeCursor < head; _tradeCursor++)
            {
                int r = _trades.TryRead(_tradeCursor, out var t);
                if (r == 0) break;
                if (r < 0) { _tradesLost++; continue; }
                t.Qty = _volume.Delta(t.Symbol, t.Qty);                            // 누적 → 체결 거래량(방향 미상 → 시뮬레이터가 호가로 추정)
                if (t.Qty <= 0) continue;
                _tradesSeen++;
                if (_state == ArenaState.Running) _sim.OnTrade(in t);
                FeedTrade(in t);
            }
            head = _directTrades.Head;
            if (head - _directCursor > _directTrades.Capacity) { _tradesLost += head - _directTrades.Capacity - _directCursor; _directCursor = head - _directTrades.Capacity; }
            for (; _directCursor < head; _directCursor++)
            {
                int r = _directTrades.TryRead(_directCursor, out var t);
                if (r == 0) break;
                if (r < 0) { _tradesLost++; continue; }
                _tradesSeen++;
                if (_state == ArenaState.Running) _sim.OnTrade(in t);
                FeedTrade(in t);
            }
        }

        private void DrainOrders(long nowTicks)
        {
            long head = _orders.Head;
            if (head - _orderCursor > _orders.Capacity) { _ordersLost += head - _orders.Capacity - _orderCursor; _orderCursor = head - _orders.Capacity; }
            for (; _orderCursor < head; _orderCursor++)
            {
                int r = _orders.TryRead(_orderCursor, out var o);
                if (r == 0) break;
                if (r < 0) { _ordersLost++; continue; }
                if (_state != ArenaState.Running) continue;                         // 종료 후 도착 = 폐기(가상 원장 동결)
                Execute(in o, nowTicks);
            }
        }

        // 접수 = 시뮬레이터 지연 버퍼 적재(체결 판단은 지연 경과 후 Process)
        private void Execute(in ArenaOrder o, long nowTicks)
        {
            var aiBook = _sim.Book(ArenaSide.Ai);
            switch (o.Kind)
            {
                case KindHuman:
                    _sim.Submit(ArenaSide.Human, o.Symbol, o.Dir, o.Qty, ArenaOrderKind.Market, 0, nowTicks);
                    break;
                case KindHumanLimit:
                    _sim.Submit(ArenaSide.Human, o.Symbol, o.Dir, o.Qty, ArenaOrderKind.Limit, o.LimitPx, nowTicks);
                    break;
                case KindHumanCancel:
                    _sim.Submit(ArenaSide.Human, 0, 0, 0, ArenaOrderKind.CancelAll, 0, nowTicks);
                    break;
                case KindHumanFlatten:
                    _sim.Submit(ArenaSide.Human, 0, 0, 0, ArenaOrderKind.Flatten, 0, nowTicks);
                    break;
                case KindHumanIceberg:
                    _sim.Submit(ArenaSide.Human, o.Symbol, o.Dir, o.Qty, ArenaOrderKind.Limit, o.LimitPx, nowTicks, peakQty: o.Peak);
                    break;
                case KindAiEntry:
                    if (_persona != ArenaPersona.Signal) break;                         // 교체 전 적재된 시그널 엔진 결정 = 폐기
                    _aiSignals++;
                    if (aiBook.Position(o.Symbol) != 0 || aiBook.HasOpenOrder(o.Symbol)) break;   // 미보유 · 미체결 없는 종목만 진입(물타기 · 지연 중 중복 0)
                    long ask = _sim.BestAsk(o.Symbol);
                    long budget = (long)((Int128)_capital * _aiLotBp / 10_000);
                    _sim.Submit(ArenaSide.Ai, o.Symbol, 1, ask > 0 ? Math.Clamp(budget / ask, 1, DualBookSimulator.MaxOrderQty) : 1, ArenaOrderKind.Market, 0, nowTicks);
                    break;
                case KindAiExit:
                    if (_persona != ArenaPersona.Signal) break;
                    _aiSignals++;
                    if (aiBook.HasOpenOrder(o.Symbol)) break;
                    long held = aiBook.Sellable(o.Symbol);
                    if (held > 0) _sim.Submit(ArenaSide.Ai, o.Symbol, -1, held, ArenaOrderKind.Market, 0, nowTicks);
                    break;
            }
        }

        private void Publish(long nowTicks, bool sample)
        {
            var ai = Stats(ArenaSide.Ai, _eqAi, _unrealAi);
            var hu = Stats(ArenaSide.Human, _eqHuman, _unrealHuman);
            int leader = _state == ArenaState.Idle || _eqAi == _eqHuman ? ArenaLeader.Draw : _eqAi > _eqHuman ? ArenaLeader.Ai : ArenaLeader.Human;
            var dbAi = _sim.Debrief(ArenaSide.Ai, _eqAi, ai.MddBp);                // [v12.18.25-DEV] 복기 + 포렌식 — seqlock 창 밖에서 계산(창 = 값 복사만 · 판독 경합 최소화)
            var dbHu = _sim.Debrief(ArenaSide.Human, _eqHuman, hu.MddBp);
            long s = _seq;
            Volatile.Write(ref _seq, s + 1);                                         // 작성 중(홀수)
            Interlocked.MemoryBarrier();
            if (sample)
            {
                int k = (int)(_curveN % MaxEquity);
                _curveAi[k] = _eqAi; _curveHuman[k] = _eqHuman;
                _curveN++;
            }
            long focus = Volatile.Read(ref _focus);
            if (focus > 0) { _sim.FillDepth(focus, ref _depth); InjectPhantom(focus); } else _depth = default;
            var hud = Hud();                                                         // 래더 허수 투영 뒤(같은 창의 HUD 공개 수량 = 래더 가산량)
            _snap = new ArenaSnapshot((s + 2) >> 1, _state, _capital, _startedTicks, nowTicks, _quotesSeen, _quotesLost, _sim.QuoteSymbols,
                                      _ordersLost, _aiSignals, leader, Math.Abs(_eqAi - _eqHuman), in ai, in hu, in hud,
                                      _humanLocked != 0, _riskLimitBp, _riskTripTicks, _riskTripEquity,
                                      ArenaPositionSide.Of(_target > 0 ? _sim.Book(ArenaSide.Ai).Position(_target) : 0),
                                      ArenaPositionSide.Of(focus > 0 ? _sim.Book(ArenaSide.Human).Position(focus) : 0));
            _debriefAi = dbAi;
            _debriefHuman = dbHu;
            Volatile.Write(ref _seq, s + 2);                                         // 안정(짝수)
        }

        // ════════════ [v12.18.18-DEV] 하드 리스크 · 포식자 허수(정산 스레드 · δ=0) ════════════

        // 시가평가 직후: 휴먼 자산 ≤ 자본금 × (1 − 한도) → 래치 + 마진콜(같은 회차 · 원자) → 청산 반영 자산 재평가
        private void CheckRisk(long nowTicks)
        {
            if (_riskLimitBp <= 0 || _humanLocked != 0) return;
            long floor = _capital - (long)((Int128)_capital * _riskLimitBp / 10_000);
            long liq = _sim.LiquidationEquity(ArenaSide.Human);                     // [v12.18.25-DEV] Net 청산가치(퇴출 수수료 · 거래세 포함) 기준 — 시가평가보다 엄격
            if (liq > floor) return;
            Volatile.Write(ref _humanLocked, 1);                                    // 생산자 차단 먼저(이후 적재 0) → 원장 동결 · 강제 청산
            _riskTripTicks = nowTicks; _riskTripEquity = liq;
            _sim.MarginCall(ArenaSide.Human);
            _eqHuman = _sim.MarkToMarket(ArenaSide.Human, out _unrealHuman);
        }

        // 포식자 활성 · 휴먼 연패 국면 · 포커스 = 대상 종목일 때만: 휴먼 래더 1호가 표시 잔량에 허수 가산(사본 전용 — 시뮬레이터 호가 불변)
        private void InjectPhantom(long focus)
        {
            if (_persona != ArenaPersona.Predator || _state != ArenaState.Running || focus != _target || !_depth.HasBook || !_predator.PhantomActive) return;
            long baseQty = (_depth.BidQty[0] + _depth.AskQty[0]) / 2;
            long pb = _predator.PhantomBidQty(baseQty), pa = _predator.PhantomAskQty(baseQty);
            if (_depth.BidPx[0] > 0) { _depth.BidQty[0] += pb; _depth.PhantomBidQty = pb; }
            if (_depth.AskPx[0] > 0) { _depth.AskQty[0] += pa; _depth.PhantomAskQty = pa; }
        }

        // ════════════ [v12.18.15-DEV] AI 페르소나(정산 스레드 · δ=0) ════════════

        // 교체 반영: 진행 중이면 AI 원장 비상 전량 청산(지연 대기 포함 취소 + 보유 전량 시장가 매도) → 새 전략 초기화 · 판단 기억 소거
        private void ApplyPersona(long nowTicks)
        {
            int p = Interlocked.Exchange(ref _personaCmd, -1);
            if (p < 0 || p == _persona) return;
            var aiBook = _sim.Book(ArenaSide.Ai);
            if (_state == ArenaState.Running && (aiBook.PosCount > 0 || aiBook.PendingCount + aiBook.WorkingCount > 0))
                _sim.Submit(ArenaSide.Ai, 0, 0, 0, ArenaOrderKind.Flatten, 0, nowTicks);
            _strategies[p]?.Reset();
            _signal = default;
            _switches++;
            Volatile.Write(ref _persona, p);
        }

        private void ResetPersonaState()
        {
            for (int i = 0; i < _strategies.Length; i++) _strategies[i]?.Reset();
            _vwap.Reset();
            _target = 0; _spread = 0; _imbalanceBp = 5_000; _decisions = 0; _switches = 0;
            _signal = default;
        }

        // 대상 종목 확정(포커스 우선 · 없으면 첫 관측) — 이미 정해졌으면 유지(재지정은 RunPersona 가 무포지션일 때만)
        private bool IsTarget(long symbol)
        {
            if (_target == 0)
            {
                long focus = Volatile.Read(ref _focus);
                _target = focus > 0 ? focus : symbol;
            }
            return symbol == _target;
        }

        private void FeedQuote(in OrderBookPayload b)
        {
            if (b.SymbolCode <= 0 || !IsTarget(b.SymbolCode)) return;
            long ask = b.AskPrice[0], bid = b.BidPrice[0];
            if (ask > 0 && bid > 0)
            {
                _imbalanceBp = ArenaBookMath.ImbalanceBp(in b);
                _spread = ask - bid;
            }
            _strategies[_persona]?.OnQuote(in b);
        }

        private void FeedTrade(in ArenaTrade t)
        {
            if (t.Symbol <= 0 || t.PriceWon <= 0 || t.Qty <= 0 || !IsTarget(t.Symbol)) return;
            _vwap.OnTrade(t.PriceWon, t.Qty);
            _strategies[_persona]?.OnTrade(t.PriceWon, t.Qty, (TradeSide)Math.Sign(t.Side));
        }

        // 판단 1회 → 원장 재확인 후에만 접수(무포지션 진입 · 보유분 청산 · 미완료 주문 존재 시 0건)
        private void RunPersona(long nowTicks)
        {
            if (_state != ArenaState.Running) return;
            var strat = _strategies[_persona];
            if (strat is null) return;
            var aiBook = _sim.Book(ArenaSide.Ai);
            int pi = _target > 0 ? aiBook.IndexOf(_target) : -1;
            bool busy = aiBook.PendingCount + aiBook.WorkingCount > 0;
            long focus = Volatile.Read(ref _focus);
            if (focus > 0 && focus != _target && pi < 0 && !busy)
            {
                _target = focus;                                                    // 포커스 이동 = 무포지션일 때만 재지정(지표 처음부터)
                _vwap.Reset();
                strat.Reset();
                _imbalanceBp = 5_000; _spread = 0;
                _signal = default;
                return;
            }
            if (_target == 0) return;
            long bid = _sim.BestBid(_target), ask = _sim.BestAsk(_target);
            if (bid <= 0 || ask <= 0) return;
            if (_persona == ArenaPersona.Predator)                                  // 휴먼 원장 역추적(사후 분석 = 같은 정산 스레드 단일 작성자)
            {
                var prey = _sim.Book(ArenaSide.Human).Analytics;
                _predator.ObservePrey(prey.LossStreak, prey.TiltAlerts, prey.LastTiltTicks, prey.OpenMaeWon, _capital, nowTicks);
                var hb = _sim.Book(ArenaSide.Human);                                // [v12.18.23-DEV] 휴먼 대상 종목 숏 평가손(되사는 값 − 순매도 대금)
                int hp = hb.IndexOf(_target);
                long hq = hp < 0 ? 0 : hb.PosQty[hp];
                _predator.ObservePreyShort(hq < 0 ? -hq : 0, hq < 0 ? hb.PosCost[hp] + hq * ask : 0);
            }
            long qty = pi < 0 ? 0 : aiBook.PosQty[pi], cost = pi < 0 ? 0 : aiBook.PosCost[pi];
            var st = new ArenaMarketState(_target, nowTicks, bid, ask, _vwap.LastPrice, _vwap.VwapWon, _vwap.DeviationBp((bid + ask) / 2), _vwap.SigmaWon,
                                          _vwap.Trades, qty, cost, busy);
            _signal = strat.Evaluate(in st);
            if (busy) return;
            if (_signal.Action == ArenaAiAction.Enter && qty == 0)
            {
                long budget = (long)((Int128)_capital * _aiLotBp / 10_000);
                long lot = Math.Clamp(budget / ask, 1, DualBookSimulator.MaxOrderQty);
                _aiSignals++; _decisions++;
                if (_persona == ArenaPersona.Predator && _predator.Stealth && lot >= PredatorTwapSlices)   // 은밀 = TWAP 분할(시장 충격 · 진공 회피)
                    _sim.Submit(ArenaSide.Ai, _target, 1, lot, ArenaOrderKind.Market, 0, nowTicks, peakQty: lot / PredatorTwapSlices, intervalTicks: PredatorTwapIntervalTicks);
                else _sim.Submit(ArenaSide.Ai, _target, 1, lot, ArenaOrderKind.Market, 0, nowTicks);
            }
            else if (_signal.Action == ArenaAiAction.EnterShort && qty == 0)          // [v12.18.23-DEV] 가상 공매도 진입(시장가 매도 · 담보 잠금)
            {
                long budget = (long)((Int128)_capital * _aiLotBp / 10_000);
                long lot = Math.Clamp(budget / bid, 1, DualBookSimulator.MaxOrderQty);
                _aiSignals++; _decisions++;
                _sim.Submit(ArenaSide.Ai, _target, -1, lot, ArenaOrderKind.Market, 0, nowTicks);
            }
            else if (_signal.Action == ArenaAiAction.Exit)
            {
                long held = qty > 0 ? aiBook.Sellable(_target) : aiBook.Coverable(_target);   // 롱 = 매도 · 숏 = 숏커버 매수
                if (held <= 0) return;
                _aiSignals++; _decisions++;
                _sim.Submit(ArenaSide.Ai, _target, qty > 0 ? -1 : 1, held, ArenaOrderKind.Market, 0, nowTicks);
            }
        }

        private ArenaAiHud Hud()
        {
            long bid = _target > 0 ? _sim.BestBid(_target) : 0, ask = _target > 0 ? _sim.BestAsk(_target) : 0;
            long dev = bid > 0 && ask > 0 ? _vwap.DeviationBp((bid + ask) / 2) : 0;
            bool pred = _persona == ArenaPersona.Predator;
            return new ArenaAiHud(_persona, _signal.Action, _signal.Reason, _switches, _target, _vwap.VwapWon, dev, _vwap.SigmaWon, _vwap.Trades,
                                  _imbalanceBp, _spread, _signal.TargetPx, _signal.StopPx, _decisions,
                                  pred ? _predator.AggressionBp : 0, pred ? _predator.PreyLossStreak : 0, pred ? _predator.PreyTiltAlerts : 0, pred ? _predator.PreyMaeWon : 0,
                                  pred ? _depth.PhantomBidQty : 0, pred ? _depth.PhantomAskQty : 0);
        }

        private ArenaSideStats Stats(int side, long equity, long unrealized)
        {
            var b = _sim.Book(side);
            int mddBp = b.PeakWon > 0 ? (int)Math.Min(10_000, (long)((Int128)b.MddWon * 10_000 / b.PeakWon)) : 0;
            return new ArenaSideStats(equity, b.CashWon, b.RealizedWon, unrealized, b.FeesWon, b.TaxWon, b.SlippageWon, b.PeakWon, b.MddWon, mddBp,
                                      b.Fills, b.Rejects, b.Wins, b.Losses, b.PosCount, b.LastReject,
                                      b.PendingCount, b.WorkingCount, b.OldestQueueAhead, b.Cancels, b.ReservedCashWon,
                                      b.VacuumEvents, b.VacuumPenaltyWon, b.IcebergRefills,
                                      b.LongPositions, b.ShortPositions, b.ShortLockedWon, b.QueueCancelDepletedQty,
                                      equity - b.CapitalWon + b.FeesWon + b.TaxWon, equity - b.CapitalWon, _sim.LiquidationEquity(side));
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

        public bool TryReadDepth(out ArenaDepth depth)
        {
            for (int spin = 0; spin < ReadSpins; spin++)
            {
                long s1 = Volatile.Read(ref _seq);
                if ((s1 & 1) != 0) { Thread.SpinWait(8); continue; }
                depth = _depth;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _seq) == s1) return s1 != 0 && depth.Symbol > 0;
            }
            depth = default;
            return false;
        }

        public bool TryReadDebrief(int side, out ArenaDebrief debrief)
        {
            for (int spin = 0; spin < ReadSpins; spin++)
            {
                long s1 = Volatile.Read(ref _seq);
                if ((s1 & 1) != 0) { Thread.SpinWait(8); continue; }
                debrief = side == ArenaSide.Human ? _debriefHuman : _debriefAi;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _seq) == s1) return s1 != 0;
            }
            debrief = default;
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
