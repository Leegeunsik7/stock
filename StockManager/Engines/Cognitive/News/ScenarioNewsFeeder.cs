// ============================================================================
// ScenarioNewsFeeder — v12.18.64-DEV: 플루토 브레인 2단계 — 오프라인 · 시험 · 합성 시세용 골든 시나리오 뉴스 피더(네트워크 0 · 결정론)
//
//   시나리오 = (오프셋 · 출처 · 헤드라인 · 본문 · 종목 힌트) 단계 목록(오프셋 오름차순으로 안정 정렬). Arm(시작 시각) 후 Pump() 마다
//     '시작 + 오프셋 ≤ 시계 현재' 인 단계를 순서대로 싱크에 전달 — 발생 시각 = 시작 + 오프셋(시계 도메인 그대로 · 벽시계 무관).
//   시계 = IVirtualClock: 합성 시세(TickSyncedVirtualClock) 위에서는 가상 세션 ×20 속도로 · 시험(ManualVirtualClock)에서는 Advance 로 재현.
//   ★정직 표기: 기본 헤드라인 접두 "[합성] " — 64B 요약 · UI 툴팁 · AI 근거 카드에 합성 뉴스임이 그대로 드러난다(실공시 위장 0).
//   ★싱크 거부(큐 처분 등) = 계수 후 다음 단계(재시도 0). Pump 무예외.
//   ★requireSynthetic = 시계가 합성/리플레이 도메인(IsSynthetic)일 때만 무장 · 진행(실시세 모드에 합성 뉴스 주입 0).
//     시계 역행(합성 출처 재선택 · Reset 후 재동기) = 현재 시각으로 재무장(처음부터) — 미래에 묶인 시나리오 정지 방지.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Engines.Cognitive.News
{
    /// <summary>시나리오 1단계(Offset = 시나리오 시작 기준 경과).</summary>
    public readonly record struct PlutoScenarioStep(TimeSpan Offset, PlutoSource Source, string Headline, string? Body = null, string? SymbolHint = null);

    public sealed class ScenarioNewsFeeder : IPlutoNewsFeeder
    {
        public const string SyntheticPrefix = "[합성] ";
        public static readonly TimeSpan DefaultPumpInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// 골든 시나리오(합성 데모 종목 · 대결 티커 대상): 대규모 공급계약(+) → 유상증자(−) → 단순 시황(노이즈) → 감사의견 거절(긴급 악재) →
        /// 사상 최대 매출(+) → 리콜(단기 악재). 가상 세션 ×20 기준 약 30초에 전 단계 소진.
        /// </summary>
        public static readonly IReadOnlyList<PlutoScenarioStep> Golden = Array.AsReadOnly(new[]
        {
            new PlutoScenarioStep(TimeSpan.FromSeconds(30), PlutoSource.Dart, "[삼성바이오로직스] 단일판매ㆍ공급계약체결(대규모 1조원 위탁생산 수주)", null, "207940"),
            new PlutoScenarioStep(TimeSpan.FromMinutes(2), PlutoSource.Dart, "[SK하이닉스] 주요사항보고서(유상증자결정)", null, "000660"),
            new PlutoScenarioStep(TimeSpan.FromMinutes(4), PlutoSource.Wire, "현대차 장중 급등 특징주", null, "005380"),
            new PlutoScenarioStep(TimeSpan.FromMinutes(6), PlutoSource.Dart, "[LG에너지솔루션] 감사보고서제출 — 감사의견 거절", null, "373220"),
            new PlutoScenarioStep(TimeSpan.FromMinutes(8), PlutoSource.Sec, "NVIDIA reports record revenue for the quarter", null, "NVDA"),
            new PlutoScenarioStep(TimeSpan.FromMinutes(10), PlutoSource.Sec, "Tesla recalls vehicles over steering defect", null, "TSLA"),
        });

        private readonly Func<PlutoNewsItem, bool> _sink;
        private readonly IVirtualClock _clock;
        private readonly PlutoScenarioStep[] _steps;
        private readonly bool _markSynthetic, _requireSynthetic;
        private readonly TimeSpan _pumpInterval;
        private readonly object _gate = new();
        private long _startUtcTicks;
        private int _cursor, _running;
        private long _polls, _delivered, _rejected, _rearms;
        private CancellationTokenSource? _loopCts;
        private Task? _loop;

        public ScenarioNewsFeeder(Func<PlutoNewsItem, bool> sink, IVirtualClock clock, IReadOnlyList<PlutoScenarioStep>? steps = null,
            bool markSynthetic = true, TimeSpan? pumpInterval = null, bool requireSynthetic = false)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            var src = steps ?? Golden;
            _steps = new PlutoScenarioStep[src.Count];
            for (int i = 0; i < src.Count; i++)
            {
                var s = src[i];
                if (s.Offset < TimeSpan.Zero || string.IsNullOrWhiteSpace(s.Headline)) throw new ArgumentException("시나리오 단계 " + i + " 무효(음수 오프셋 · 빈 헤드라인)", nameof(steps));
                _steps[i] = s;
            }
            // 오프셋 오름차순 안정 정렬(삽입 정렬 · 단계 수 소량)
            for (int i = 1; i < _steps.Length; i++)
            {
                var cur = _steps[i];
                int j = i - 1;
                while (j >= 0 && _steps[j].Offset > cur.Offset) { _steps[j + 1] = _steps[j]; j--; }
                _steps[j + 1] = cur;
            }
            _markSynthetic = markSynthetic;
            _requireSynthetic = requireSynthetic;
            _pumpInterval = pumpInterval ?? DefaultPumpInterval;
            if (_pumpInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pumpInterval));
        }

        public string Name => "합성 시나리오";
        public PlutoSource Source => PlutoSource.Unknown;
        public int StepCount => _steps.Length;
        public int Cursor { get { lock (_gate) return _cursor; } }
        public bool Armed => Interlocked.Read(ref _startUtcTicks) > 0;
        public bool Completed { get { lock (_gate) return _cursor >= _steps.Length; } }
        public long StartUtcTicks => Interlocked.Read(ref _startUtcTicks);
        public long Rearms => Interlocked.Read(ref _rearms);

        public PlutoFeederHealth Health => new(Name, Source, Volatile.Read(ref _running) != 0, Interlocked.Read(ref _polls), Cursor,
            Interlocked.Read(ref _delivered), 0, Interlocked.Read(ref _rejected), 0, 0, 0, 0, PlutoFeederFault.None, 0);

        /// <summary>시나리오 시작(재무장 = 처음부터). startUtcTicks null = 시계 현재.</summary>
        public void Arm(long? startUtcTicks = null)
        {
            long start = startUtcTicks ?? _clock.UtcNowTicks;
            if (start <= 0) throw new ArgumentOutOfRangeException(nameof(startUtcTicks));
            lock (_gate)
            {
                _cursor = 0;
                Interlocked.Exchange(ref _startUtcTicks, start);
            }
        }

        /// <summary>도래 단계 전달(미무장 = 지금 무장 후 진행). 반환 = 싱크 수락 수.</summary>
        public int Pump()
        {
            Interlocked.Increment(ref _polls);
            if (_requireSynthetic && !_clock.IsSynthetic) return 0;
            if (!Armed) Arm();
            int accepted = 0;
            lock (_gate)
            {
                long start = Interlocked.Read(ref _startUtcTicks);
                long now = _clock.UtcNowTicks;
                if (now < start)
                {
                    _cursor = 0;
                    start = now;
                    Interlocked.Exchange(ref _startUtcTicks, now);
                    Interlocked.Increment(ref _rearms);
                }
                while (_cursor < _steps.Length)
                {
                    ref readonly var s = ref _steps[_cursor];
                    long at = start + s.Offset.Ticks;
                    if (at > now) break;
                    _cursor++;
                    string head = _markSynthetic ? SyntheticPrefix + s.Headline : s.Headline;
                    bool ok;
                    try { ok = _sink(new PlutoNewsItem(s.Source, head, at, s.Body, s.SymbolHint)); }
                    catch (Exception) { ok = false; }
                    if (ok) { accepted++; Interlocked.Increment(ref _delivered); }
                    else Interlocked.Increment(ref _rejected);
                }
            }
            return accepted;
        }

        public Task<int> PollOnceAsync(CancellationToken cancellationToken)
            => Task.FromResult(cancellationToken.IsCancellationRequested ? 0 : Pump());

        public void Start()
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            var cts = new CancellationTokenSource();
            _loopCts = cts;
            _loop = Task.Run(() => LoopAsync(cts.Token));
        }

        public void Stop()
        {
            if (Interlocked.CompareExchange(ref _running, 0, 1) != 1) return;
            var cts = _loopCts;
            if (cts is null) return;
            cts.Cancel();
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
            cts.Dispose();
            _loopCts = null;
            _loop = null;
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Pump();
                try { await Task.Delay(_pumpInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }

        public void Dispose() => Stop();
    }
}
