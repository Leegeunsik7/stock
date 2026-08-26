// ============================================================================
// AutonomousAlphaFactory — v4.72.0: 자율 알파 공장
//
//  24시간 주기 백그라운드 루프:
//   1. HypothesisMiner.Mine() — OOS 생존 패턴 발굴 (결정론 기준 시계열)
//   2. MaGapPct 조건 파라미터 추출 → 교차선 Short/Long 페어 생성
//   3. CartridgeEngine.Attest() — 결정론 SplitMix64 백테스트 증명서
//   4. CartridgeNumericsGuard.TryValidate() — 서명 전 수치 안전 검증
//   5. CartridgeService.BuildSigned() — ECDsa 서명 + 파일 내보내기
//
//  ·  EmergencyFreeze 상태 → 주기 스킵 (fail-closed)
//  ·  외부 시세 API 의존 0 — CartridgeEngine.ReferenceSeries(seed) 전용
//  ·  CartridgeNumericsGuard 는 이 파일에 함께 선언 (서명 전 게이트)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace StockManager.Common
{
    // ── 수치 안전 검증기 — Attest 결과를 ECDsa 서명 전에 점검 ─────────────────
    public static class CartridgeNumericsGuard
    {
        public static bool TryValidate(CartStrategy s, out string violation)
        {
            violation = string.Empty;

            if (s.Short <= 0)
                { violation = $"Short={s.Short}≤0"; return false; }
            if (s.Long <= 0)
                { violation = $"Long={s.Long}≤0"; return false; }
            if (s.Short >= s.Long)
                { violation = $"Short({s.Short})≥Long({s.Long}) — 교차선 역전"; return false; }
            if (s.AttestTrades <= 0)
                { violation = "거래 0 — 전략 기간 내 미발동"; return false; }
            if (!double.IsFinite(s.AttestTotalReturnPct))
                { violation = $"TotalReturn 비유한값 ({s.AttestTotalReturnPct})"; return false; }
            if (!double.IsFinite(s.AttestMaxDrawdownPct))
                { violation = $"MaxDrawdown 비유한값 ({s.AttestMaxDrawdownPct})"; return false; }
            if (s.AttestMaxDrawdownPct > 0)
                { violation = $"MDD={s.AttestMaxDrawdownPct:+0.00;-0.00}>0 (부호 오류: 손실은 음수)"; return false; }
            // 결정론 재검증 — 성과 주장과 재실행 결과 정합 확인
            if (!CartridgeEngine.VerifyStrategy(s))
                { violation = "결정론 재검증 실패 — 백테스트 디싱크 (데이터 변조 의심)"; return false; }

            return true;
        }
    }

    // ── 자기 제어 밸브 — 최근 N 시행 거부율이 임계 초과 시 트립(전략 고갈/오작동 방어) ────────────
    /// <summary>
    /// 최근 window 시행(전략 accept/reject)의 거부율이 threshold 초과 시 IsTripped=1 로 래치.
    /// 결정론·무할당(고정 링). 트립은 지휘관 Reset() 전까지 유지(공장 사이클 스킵).
    /// </summary>
    public sealed class AlphaRejectionValve
    {
        private readonly int      _window;
        private readonly double   _threshold;   // 0..1
        private readonly int      _minSamples;  // 최소 표본(거짓 트립 방지)
        private readonly bool[]   _recent;
        private int _head, _count, _rejected, _tripped;
        private readonly object _lock = new();

        public AlphaRejectionValve(int window = 1000, double threshold = 0.95, int minSamples = 100)
        {
            _window     = window < 2 ? 2 : window;
            _threshold  = threshold is > 0.0 and < 1.0 ? threshold : 0.95;
            _minSamples = minSamples < 1 ? 1 : (minSamples > _window ? _window : minSamples);
            _recent     = new bool[_window];
        }

        public bool   IsTripped   => Volatile.Read(ref _tripped) == 1;
        public double RecentRate  { get { lock (_lock) return _count == 0 ? 0.0 : (double)_rejected / _count; } }
        public int    WindowCount { get { lock (_lock) return _count; } }

        /// <summary>1 시행 결과 기록(true=거부). 임계 초과 시 자동 트립(멱등). 반환=현재 트립 상태.</summary>
        public bool Record(bool rejected)
        {
            lock (_lock)
            {
                if (_count == _window)                       // 최고참 축출
                { if (_recent[_head]) _rejected--; }
                else _count++;
                _recent[_head] = rejected;
                if (rejected) _rejected++;
                _head = (_head + 1) % _window;

                if (_count >= _minSamples && (double)_rejected / _count > _threshold)
                    Interlocked.CompareExchange(ref _tripped, 1, 0);   // 자동 밸브 트립(멱등 래치)
            }
            return IsTripped;
        }

        /// <summary>명시 트립(지휘관 수동 일시정지).</summary>
        public void Trip() => Interlocked.Exchange(ref _tripped, 1);

        /// <summary>재개 — 래치 해제 + 창 초기화(운영자 조치).</summary>
        public void Reset()
        {
            lock (_lock) { _head = _count = _rejected = 0; Array.Clear(_recent); }
            Interlocked.Exchange(ref _tripped, 0);
        }
    }

    // ── 자율 알파 공장 ─────────────────────────────────────────────────────────
    public sealed class AutonomousAlphaFactory : IDisposable
    {
        private readonly LocalDatabase            _db;
        private readonly OrderAttestationService  _gate;
        private readonly Thread                   _worker;
        private volatile bool                     _stop;
        private readonly ManualResetEventSlim      _wake = new(false);   // 인터럽터블 대기 — Dispose 시 즉시 기상(종료 지연 제거)
        private readonly AlphaRejectionValve      _valve = new();   // v4.85.0 자기 제어 밸브(거부율 95%↑ 자동 정지)

        public int CyclesCompleted   { get; private set; }
        public int StrategiesSigned  { get; private set; }
        public int GuardRejections   { get; private set; }

        // ── v4.85.0 자기 제어 밸브(관제·제어) ─────────────────────────────────
        /// <summary>거부율 임계 초과로 자동 일시정지(또는 수동 Suspend)됨. 사이클 스킵.</summary>
        public bool   IsSuspended         => _valve.IsTripped;
        /// <summary>최근 창(≤1000 시행) 거부율(0..1). 누적이 아닌 롤링 — 최근 고갈을 즉시 반영.</summary>
        public double RecentRejectionRate => _valve.RecentRate;
        public int    RecentWindowCount   => _valve.WindowCount;
        /// <summary>수동 일시정지(지휘관). Resume() 전까지 사이클 스킵.</summary>
        public void   Suspend()           => _valve.Trip();
        /// <summary>재개 — 밸브 래치 해제 + 창 초기화.</summary>
        public void   Resume()            => _valve.Reset();

        public AutonomousAlphaFactory(LocalDatabase db, OrderAttestationService gate)
        {
            _db   = db   ?? throw new ArgumentNullException(nameof(db));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _worker = new Thread(RunLoop)
            {
                IsBackground = true,
                Name         = "AutonomousAlphaFactory",
                Priority     = ThreadPriority.BelowNormal,
            };
        }

        public void Start()
        {
            // IsAlive guard: DI singleton 이므로 이중 호출은 없어야 하나, ThreadStateException 방어.
            if (_worker.IsAlive) return;
            _worker.Start();
        }

        private void RunLoop()
        {
            while (!_stop)
            {
                try { RunCycle(); }
                catch { /* 사이클 예외는 루프를 중단하지 않음 */ }

                // 24h 대기 — ★인터럽터블(ManualResetEventSlim). Dispose 가 _wake.Set() 하면 즉시 반환해 종료가 지연되지 않는다.
                //   (기존 1분 단위 Thread.Sleep 은 stop 신호를 최대 60초 뒤에야 관측해 Join(5s) 타임아웃까지 종료가 프리즈됐음.)
                if (!_stop) { try { _wake.Wait(TimeSpan.FromHours(24)); } catch { return; } }
            }
        }

        // 테스트에서 직접 호출 가능 (public)
        public List<CartStrategy> RunCycle()
        {
            // EmergencyFreeze → 발굴·서명 모두 차단 (fail-closed)
            if (_gate.Mode == GatingMode.EmergencyFreeze)
                return new List<CartStrategy>();

            // ★자기 제어 밸브 트립(거부율 임계 초과 또는 수동 Suspend) → 사이클 스킵(전략 고갈 시 폭주 방지)
            if (_valve.IsTripped)
                return new List<CartStrategy>();

            // 결정론 기준 시계열 (seed=42, 300봉) — 외부 API 의존 0
            var candles = CartridgeEngine.ReferenceSeries(seed: 42, n: 300);

            // 1. 가설 발굴
            var mined = HypothesisMiner.Mine(candles,
                horizon: 10, trainFrac: 0.7, minOccurrences: 8, baseEdgeBar: 0.5);

            // 2. 생존 가설에서 MA 기간 추출 → Short/Long 페어
            var pairs = ExtractPeriodPairs(mined.Pool).ToList();

            var signed = new List<CartStrategy>();
            foreach (var (shortP, longP) in pairs)
            {
                if (_stop) break;

                // 3. 결정론 증명서
                var strategy = CartridgeEngine.Attest(
                    $"AlphaFact-S{shortP}L{longP}",
                    shortP, longP,
                    seed: shortP * 1000 + longP);

                // 4. 수치 안전 검증 (서명 전 게이트)
                if (!CartridgeNumericsGuard.TryValidate(strategy, out _))
                {
                    GuardRejections++;
                    _valve.Record(rejected: true);    // 밸브: 거부 시행 기록(임계 초과 시 자동 정지)
                    continue;
                }

                signed.Add(strategy);
                _valve.Record(rejected: false);       // 밸브: 수용 시행 기록
            }

            // 5. 서명 + 파일 내보내기 — EmergencyFreeze 재확인 (Drain 중 상태 변화 방어)
            if (signed.Count > 0 && _gate.Mode != GatingMode.EmergencyFreeze)
            {
                var cart = CartridgeService.BuildSigned(
                    _db,
                    $"AlphaFactory-{CyclesCompleted}",
                    $"자율 알파 공장 자동 생성 — 사이클 {CyclesCompleted}",
                    signed);
                CartridgeService.ExportToFile(cart);
                StrategiesSigned += signed.Count;
            }

            CyclesCompleted++;
            return signed;
        }

        private static IEnumerable<(int shortP, int longP)> ExtractPeriodPairs(
            List<Hypothesis> survivors)
        {
            // MaGapPct 조건의 Param(= MA기간)을 교차선 Long 기간으로 매핑
            var maperiods = survivors
                .SelectMany(h => h.Rule.Conditions)
                .Where(c => c.Metric == CondMetric.MaGapPct)
                .Select(c => c.Param)
                .Where(p => p is >= 5 and <= 200)
                .Distinct()
                .OrderBy(p => p);

            foreach (int longP in maperiods)
            {
                int shortP = Math.Max(3, longP / 3);
                if (shortP < longP) yield return (shortP, longP);
            }

            // 시장 대표 MA 교차 기준 페어 (항상 포함)
            yield return (5, 20);
            yield return (8, 34);
            yield return (13, 55);
        }

        public void Dispose()
        {
            _stop = true;
            try { _wake.Set(); } catch { }   // 상주 대기 즉시 기상 → Join 이 타임아웃 없이 반환(종료 지연 제거)
            try { _worker.Join(TimeSpan.FromSeconds(5)); } catch { }
            try { _wake.Dispose(); } catch { }
        }
    }
}
