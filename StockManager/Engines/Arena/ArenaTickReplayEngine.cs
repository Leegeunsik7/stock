// ============================================================================
// ArenaTickReplayEngine — [v12.18.12-DEV] 아레나 고속 틱 리플레이(배속 1 · 5 · 10 · 50 · 100 · 최대) — 재생 루프 δ=0
//
//  ① 재사용: 체결 = DualBookSimulator(수수료 · 세금 · 호가 잔량 순회 규칙 그대로) · 정산/스냅샷 = ArenaMatchGovernor(리플레이 전용 인스턴스 —
//     실시간 아레나 원장과 분리) · AI = 정량 시그널 엔진 QuantSignalProvider 전용 인스턴스(시계 = 재생 시뮬레이션 시각 · 결정 싱크 = 리플레이 원장 AI 링).
//     AI 인스턴스의 전략 이상 인터락은 무장 집계어에 결속하지 않는다(리플레이 이상이 실시간 무장 상태에 닿는 경로 0).
//  ② 재생 스레드(전용 · 단일 작성자): Pump(벽시계) = 명령 링 반영 → 배속 기준(anchor) 대비 도래한 틱 처리(틱마다 AI OnBook → 원장 Advance) →
//     50ms 주기(또는 허브 펌프 요청) 스냅샷 게시. 배속 스케줄 = Stopwatch 타임스탬프 기준 목표 시각(sim = anchorSim + Δwall × 배속) — 대기 오차가
//     누적되지 않는다(늦게 깨면 다음 Pump 가 밀린 틱을 처리 · 회차당 최대 MaxBatch). 최대 배속 = 시각 무시 · 회차당 MaxBatch 연속.
//  ③ 일시정지 = 틱 처리 중단(시가평가 · 원장 변경 0 → 포지션 · 손익 정확 동결). 휴먼 주문은 원장 주문 링에 대기 → 재개 첫 틱에 그 틱 호가로 체결.
//  ④ 게시 디커플링: 틱 정산은 게시하지 않는다 — 스냅샷은 50ms(허브 펌프 OnHubPump 요청 또는 자체 50ms 경과)마다 1회. 배속과 무관한 UI 프레임율.
//  ★Fail-Closed: 브로커 · 디스패처 · 무장 키 참조 0(IL 허용목록 테스트). 리플레이 주문은 가상 원장 밖으로 나갈 경로가 없다.
//  콜드 경로(할당 허용): 첫 명령 시 테이프 생성 · 재생 스레드 기동 · 처음부터 재생(AI 인스턴스 재생성).
//  [v12.18.13-DEV] 인덱스마다 체결(있으면) → 원장 ApplyTrade(지정가 대기열 소진) → 호가(있으면) → AI OnBook → 원장 Advance(시장 시각 = 테이프 시각 —
//   체결 지연 30 ~ 50ms 판정 기준). LoadTape = .tape(MMF) 판독기 교체(호출 스레드에서 열기/검증 → 재생 스레드가 교체 · 이전 판독기 폐기 · 처음 · 대기).
//  [v12.18.26-DEV] 극한 시나리오(SetScenario · 블라인드): '처음부터' 재생 시(콜드 · Rewind) 원천(_source)을 FlashCrashScenarioInjector 로 감싼다 —
//   엔트로피(기본 Stopwatch ⊕ TickCount64 · 테스트 주입)로 종류 · 발동 시각(3 ~ 5분) 결정. 국면(잠복 → 발동 → 여진)은 게시 시 커서 대 버스트 구간으로 판정 ·
//   종류 · 대상 종목 · 버스트 틱/초는 발동 국면부터만 게시(블라인드 — 잠복 중 0). 리셋 · 원천 교체 = 원천 그대로(인젝터 해제).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;

namespace StockManager.Engines.Arena
{
    public sealed class ArenaTickReplayEngine : IArenaReplayPort, IDisposable
    {
        public const int MaxBatch = 8_192, PublishMs = 50, EquityPoints = 600;
        private const long CmdPause = -1, CmdReset = -2, CmdLoad = -3;
        public const string SyntheticLabel = "합성 테이프";

        private readonly Func<ArenaTickSource> _tapeFactory;
        private readonly bool _ownThread;
        private readonly Func<long> _clock;
        private readonly Func<ulong> _entropy;
        private readonly long _freq;
        private readonly ArenaMatchGovernor _gov;
        private readonly AiSink _aiSink;
        private readonly TelemetryRing<long> _cmds = new(16);
        private readonly AutoResetEvent _wake = new(false);
        private long _cmdCursor;

        // ── 재생 스레드 상태(단일 작성자) ──
        private ArenaTickSource? _tape;                                         // 재생 중 테이프(원천 또는 시나리오 인젝터)
        private ArenaTickSource? _source;                                       // [v12.18.26-DEV] 원천(합성 · 녹화) — 소유/폐기 기준
        private FlashCrashScenarioInjector? _scenario;
        private QuantSignalProvider? _ai;
        private int _cursor, _appliedSpeed = 1;
        private long _capital = ArenaMatchGovernor.DefaultCapitalWon;
        private long _anchorSim, _anchorWall, _simTicks, _lastPublish, _windowTicks, _windowStart;
        private OrderBookPayload _book;
        private ArenaTrade _trade;
        private ArenaTickSource? _synthetic;
        private ArenaTickSource? _pendingTape;
        private string _sourceLabel = SyntheticLabel;
        private string? _pendingLabel;

        // ── 게시 상태(Volatile) ──
        private int _state, _speed = 1, _requestedSpeed = 1, _threadStarted, _disposed, _hubRequest;
        private int _scenarioMode, _pubScnPhase, _pubScnProfile;
        private long _pubScnSymbol, _pubScnTps;
        private long _pubCursor, _pubTotal, _pubSim, _pubTps, _pubVersion, _processed;
        private Thread? _thread;

        /// <param name="tapeFactory">틱 테이프(첫 명령 시 1회 생성 — 콜드).</param>
        /// <param name="timestamp">벽시계 타임스탬프(기본 Stopwatch.GetTimestamp · 테스트 주입).</param>
        /// <param name="frequency">타임스탬프 주파수(기본 Stopwatch.Frequency).</param>
        /// <param name="ownThread">true = 첫 명령 시 전용 재생 스레드 기동 · false = 호출자가 Pump 를 직접 구동(단일 작성자 · 테스트).</param>
        /// <param name="latencyMinTicks">[v12.18.13-DEV] 체결 지연 하한(시장 시각 · 기본 30ms).</param>
        /// <param name="latencyMaxTicks">체결 지연 상한(기본 50ms).</param>
        /// <param name="scenarioEntropy">[v12.18.26-DEV] 블라인드 시나리오 엔트로피(기본 Stopwatch ⊕ TickCount64 · 테스트 주입).</param>
        public ArenaTickReplayEngine(Func<ArenaTickSource> tapeFactory, Func<long>? timestamp = null, long frequency = 0, bool ownThread = true,
                                     long latencyMinTicks = DualBookSimulator.DefaultLatencyMinTicks, long latencyMaxTicks = DualBookSimulator.DefaultLatencyMaxTicks,
                                     Func<ulong>? scenarioEntropy = null)
        {
            _entropy = scenarioEntropy ?? DefaultEntropy;
            _ownThread = ownThread;
            _tapeFactory = tapeFactory ?? throw new ArgumentNullException(nameof(tapeFactory));
            _clock = timestamp ?? Stopwatch.GetTimestamp;
            _freq = frequency > 0 ? frequency : Stopwatch.Frequency;
            _gov = new ArenaMatchGovernor(WallTicks, latencyMinTicks: latencyMinTicks, latencyMaxTicks: latencyMaxTicks);
            _aiSink = new AiSink(_gov);
        }

        public ArenaMatchGovernor Governor => _gov;
        public IArenaPort Arena => _gov;
        public long Processed => Interlocked.Read(ref _processed);
        public int State => Volatile.Read(ref _state);
        public string SourceLabel => Volatile.Read(ref _sourceLabel);

        public bool LoadTape(string? path)
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            ArenaTickSource? src = null;
            string label = SyntheticLabel;
            if (path is not null)
            {
                try { src = ArenaTapeReader.Open(path); label = System.IO.Path.GetFileName(path); }
                catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or ArgumentException)
                {
                    Trace.WriteLine("[ArenaReplay] 테이프 열기 실패: " + ex.Message);
                    return false;
                }
            }
            if (Interlocked.Exchange(ref _pendingTape, src) is IDisposable stale) stale.Dispose();   // 반영 전 연속 교체 = 앞선 대기분 폐기
            Volatile.Write(ref _pendingLabel, label);
            Command(CmdLoad);
            return true;
        }

        public ArenaReplayStatus Status => new(Volatile.Read(ref _state), Volatile.Read(ref _speed), Volatile.Read(ref _pubCursor), Volatile.Read(ref _pubTotal),
                                               Volatile.Read(ref _pubSim), Volatile.Read(ref _pubTps), Volatile.Read(ref _pubVersion),
                                               Volatile.Read(ref _scenarioMode), Volatile.Read(ref _pubScnPhase), Volatile.Read(ref _pubScnProfile),
                                               Volatile.Read(ref _pubScnSymbol), Volatile.Read(ref _pubScnTps));

        /// <summary>[v12.18.26-DEV] 현재 매치의 시나리오 인젝터(없음 = null · 진단/테스트 — UI 포트에는 비노출).</summary>
        public FlashCrashScenarioInjector? Scenario => Volatile.Read(ref _scenario);

        public bool SetScenario(int mode)
        {
            if (!ArenaScenario.IsValid(mode) || Volatile.Read(ref _disposed) != 0) return false;
            if (Volatile.Read(ref _state) is ArenaReplayState.Playing or ArenaReplayState.Paused) return false;
            Volatile.Write(ref _scenarioMode, mode);
            return true;
        }

        // ════════════ 명령(임의 스레드 · 링 적재 + 깨우기) ════════════

        public bool Play(long capitalWon)
        {
            if (capitalWon < ArenaMatchGovernor.MinCapitalWon || capitalWon > ArenaMatchGovernor.MaxCapitalWon) return false;
            Command(capitalWon);
            return true;
        }

        public void Pause() => Command(CmdPause);
        public void Reset() => Command(CmdReset);

        public bool SetSpeed(int speed)
        {
            if (!ArenaReplaySpeed.IsValid(speed)) return false;
            Volatile.Write(ref _requestedSpeed, speed);
            Volatile.Write(ref _speed, speed);
            Wake();
            return true;
        }

        /// <summary>허브 50ms 펌프 통지 — 다음 재생 회차에 스냅샷 게시(δ=0 · 원장 작성은 재생 스레드만).</summary>
        public void OnHubPump() => Volatile.Write(ref _hubRequest, 1);

        private void Command(long c)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            _cmds.Publish(c);
            if (_ownThread && Interlocked.CompareExchange(ref _threadStarted, 1, 0) == 0)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "ArenaTickReplay" };
                _thread.Start();
            }
            Wake();
        }

        private void Wake()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _wake.Set(); } catch (ObjectDisposedException) { }
        }

        // ════════════ 재생(단일 작성자 · δ=0) ════════════

        /// <summary>재생 1회차 — 반환 = 처리 틱 수. 재생 스레드(또는 테스트 단일 스레드)만 호출.</summary>
        public int Pump(long now)
        {
            HandleCommands(now);
            int speed = _appliedSpeed;                                              // 배속 변경 = 이번 회차까지 기존 배속으로 처리한 뒤 재기준(도래분 유실 0)
            int processed = 0;
            var tape = _tape;
            if (_state == ArenaReplayState.Playing && tape is not null)
            {
                long target = speed == ArenaReplaySpeed.Max ? long.MaxValue
                    : _anchorSim + (long)((Int128)(now - _anchorWall) * speed * TimeSpan.TicksPerSecond / _freq);
                long wall = WallTicksAt(now);
                int total = tape.Count;
                while (_cursor < total && processed < MaxBatch)
                {
                    long ts = tape.TimestampOf(_cursor);
                    if (ts > target) break;
                    bool hasBook = tape.Read(_cursor, out _book);
                    _simTicks = ts;
                    if (tape.TryReadTrade(_cursor, out _trade)) _gov.ApplyTrade(in _trade, ts);   // 체결 → 지정가 대기열(호가보다 먼저)
                    if (hasBook) _ai!.OnBook(in _book);                             // AI 결정 → 원장 AI 링(같은 틱 Advance 에서 접수 · 지연 후 체결)
                    _gov.Advance(in _book, hasBook, ts, wall);
                    _cursor++; processed++;
                }
                _windowTicks += processed;
                Interlocked.Add(ref _processed, processed);
                if (_cursor >= total)
                {
                    _gov.Finish(wall);
                    SetState(ArenaReplayState.Finished);
                    Publish(now, force: true);
                    return processed;
                }
            }
            int requested = Volatile.Read(ref _requestedSpeed);
            if (requested != _appliedSpeed && !(speed != ArenaReplaySpeed.Max && processed >= MaxBatch))   // 밀린 틱이 남았으면 다음 회차에 마저 처리 후 전환
            {
                Reanchor(now);
                _appliedSpeed = requested;
            }
            Publish(now, force: false);
            return processed;
        }

        private void HandleCommands(long now)
        {
            long head = _cmds.Head;
            for (; _cmdCursor < head; _cmdCursor++)
            {
                if (_cmds.TryRead(_cmdCursor, out long c) != 1) continue;
                if (c == CmdLoad) { SwapTape(now); Publish(now, force: true); continue; }
                EnsureTape();
                long wall = WallTicksAt(now);
                if (c > 0)
                {
                    if (_state == ArenaReplayState.Paused) { Reanchor(now); SetState(ArenaReplayState.Playing); }
                    else if (_state != ArenaReplayState.Playing) { _capital = c; Rewind(running: true, wall); Reanchor(now); SetState(ArenaReplayState.Playing); }
                }
                else if (c == CmdPause && _state == ArenaReplayState.Playing) { SetState(ArenaReplayState.Paused); _gov.PublishNow(wall); }
                else if (c == CmdReset) { Rewind(running: false, wall); SetState(ArenaReplayState.Idle); }
                Publish(now, force: true);
            }
        }

        private void EnsureTape()
        {
            if (_tape is not null) return;
            _tape = _source = _synthetic ??= _tapeFactory() ?? throw new InvalidOperationException("틱 테이프 없음");
            Volatile.Write(ref _pubTotal, _tape.Count);
        }

        // 재생 원천 교체(재생 스레드 · 콜드): 대기 원천(null = 합성) 반영 → 이전 파일 판독기 폐기 → 처음 · 대기
        private void SwapTape(long now)
        {
            var next = Interlocked.Exchange(ref _pendingTape, null);
            string label = Volatile.Read(ref _pendingLabel) ?? SyntheticLabel;
            var old = _source;
            _tape = _source = next ?? (_synthetic ??= _tapeFactory() ?? throw new InvalidOperationException("틱 테이프 없음"));
            if (old is IDisposable d && !ReferenceEquals(old, _source) && !ReferenceEquals(old, _synthetic)) d.Dispose();
            Volatile.Write(ref _sourceLabel, label);
            Volatile.Write(ref _pubTotal, _tape.Count);
            Rewind(running: false, WallTicksAt(now));
            SetState(ArenaReplayState.Idle);
        }

        // 처음(틱 0)으로 — AI 인스턴스 재생성(콜드) · 원장 초기화
        private void Rewind(bool running, long wall)
        {
            _cursor = 0;
            ApplyScenario(running);
            _simTicks = _tape!.Count > 0 ? _tape.TimestampOf(0) : 0;
            _ai = new QuantSignalProvider(new StrategyAnomalyInterlock(), _aiSink, nowMicros: SimMicros);
            _gov.Rearm(_capital, running, Math.Max(1, _tape.Count / EquityPoints), wall);
        }

        // [v12.18.26-DEV] 시나리오 결선(콜드): 새 매치 + 블라인드 = 원천을 인젝터로 감쌈(엔트로피 1회) · 그 외 = 원천 그대로
        private void ApplyScenario(bool running)
        {
            var src = _source!;
            FlashCrashScenarioInjector? inj = running && Volatile.Read(ref _scenarioMode) == ArenaScenario.Blind
                ? FlashCrashScenarioInjector.CreateBlind(src, _entropy()) : null;
            _tape = inj is { Injected: true } ? inj : src;
            Volatile.Write(ref _scenario, inj);
            Volatile.Write(ref _pubTotal, _tape.Count);
            PublishScenario();
        }

        // 국면 게시: 잠복(커서 ≤ 버스트 시작) → 발동(버스트 구간) → 여진. 종류 · 종목 · 틱/초는 발동부터만(블라인드).
        private void PublishScenario()
        {
            var inj = _scenario;
            int phase = inj is null ? ArenaScenarioPhase.None
                : !inj.Injected || _cursor <= inj.BurstStart ? ArenaScenarioPhase.Armed
                : _cursor <= inj.BurstEnd ? ArenaScenarioPhase.Active : ArenaScenarioPhase.Aftermath;
            bool revealed = phase >= ArenaScenarioPhase.Active;
            Volatile.Write(ref _pubScnProfile, revealed ? inj!.Profile : ArenaScenarioProfile.None);
            Volatile.Write(ref _pubScnSymbol, revealed ? inj!.TargetSymbol : 0);
            Volatile.Write(ref _pubScnTps, revealed ? inj!.BurstTicksPerSecond : 0);
            Volatile.Write(ref _pubScnPhase, phase);
        }

        // 배속 재기준: 현재 재생 위치(마지막 처리 틱 시각)를 지금 벽시계에 고정 — 다음 틱은 (간격 ÷ 배속) 뒤 도래
        private void Reanchor(long now)
        {
            _anchorSim = _simTicks;
            _anchorWall = now;
        }

        private void SetState(int s) => Volatile.Write(ref _state, s);

        private void Publish(long now, bool force)
        {
            if (!force && Interlocked.Exchange(ref _hubRequest, 0) == 0 && now - _lastPublish < _freq * PublishMs / 1000) return;
            if (_state == ArenaReplayState.Playing) _gov.PublishNow(WallTicksAt(now));
            long dt = now - _windowStart;
            if (dt >= _freq / 4)
            {
                Volatile.Write(ref _pubTps, (long)((Int128)_windowTicks * _freq / dt));
                _windowTicks = 0; _windowStart = now;
            }
            if (_state != ArenaReplayState.Playing) Volatile.Write(ref _pubTps, 0);
            PublishScenario();
            Volatile.Write(ref _pubCursor, _cursor);
            Volatile.Write(ref _pubSim, _simTicks);
            Interlocked.Increment(ref _pubVersion);
            _lastPublish = now;
        }

        private long SimMicros() => _simTicks / 10;
        private static ulong DefaultEntropy() => (ulong)Stopwatch.GetTimestamp() ^ (ulong)Environment.TickCount64 * 0x9E3779B97F4A7C15UL;
        private long WallTicks() => WallTicksAt(_clock());
        private long WallTicksAt(long ts) => (long)((Int128)ts * TimeSpan.TicksPerSecond / _freq);

        // ════════════ 재생 스레드 ════════════

        private void Run()
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                int n;
                try { n = Pump(_clock()); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    SetState(ArenaReplayState.Paused);                              // 재생 오류 = 일시정지(원장 동결 · 재시도 루프 0)
                    Trace.WriteLine("[ArenaReplay] 재생 오류 — 일시정지: " + ex.Message);
                    n = 0;
                }
                if (n >= MaxBatch) continue;                                         // 최대 배속 · 밀린 틱 = 즉시 다음 회차
                _wake.WaitOne(NextWaitMs());
            }
        }

        private int NextWaitMs()
        {
            var tape = _tape;
            if (_state != ArenaReplayState.Playing || tape is null || _cursor >= tape.Count) return PublishMs;
            int speed = _appliedSpeed;
            if (speed == ArenaReplaySpeed.Max) return 0;
            long dueWall = _anchorWall + (long)((Int128)(tape.TimestampOf(_cursor) - _anchorSim) * _freq / (TimeSpan.TicksPerSecond * (long)speed));
            long ms = (dueWall - _clock()) * 1000 / _freq;
            return (int)Math.Clamp(ms, 0, PublishMs);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _wake.Set();
            if (_thread is { } t && !t.Join(2_000)) Trace.WriteLine("[ArenaReplay] 재생 스레드 종료 시한 초과");
            _wake.Dispose();
            if (Interlocked.Exchange(ref _pendingTape, null) is IDisposable p) p.Dispose();
            if (_source is IDisposable cur && !ReferenceEquals(_source, _synthetic)) cur.Dispose();
        }

        /// <summary>AI 결정 싱크 — 진입/청산만 리플레이 원장 AI 링으로(δ=0).</summary>
        private sealed class AiSink : IDecisionSink
        {
            private readonly ArenaMatchGovernor _gov;
            public AiSink(ArenaMatchGovernor gov) => _gov = gov;
            public void PublishDecision(int kind, long symbol, long scaleBp, long volatilityBp, long value, int reason) => _gov.OnAiDecision(kind, symbol);
        }
    }
}
