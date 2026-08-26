// ============================================================================
// QFxCircuitBreaker — 3단계: 실시간 환율 서킷 브레이커(급변동 3σ · 스테일 fail-closed)
//
//  ⚠️ liveArmed:false 불변 — 본 서비스는 '구성'만. 자동 배선·실 피드 결속은 호출부 옵트인.
//
//  ── 역할(명령 3단계 §3) ────────────────────────────────────────────────────
//   기존 QFxGuard(건별 변환 가드: 바운드·역전·스프레드·보수편향)는 '한 건'의 정합만 본다.
//   본 브레이커는 '시계열'을 본다 — 직전 10분 창의 평균/표준편차 대비 3σ 급변동, 또는 피드가
//   1초 이상 지연(Stale)되면 '모호하게 통과시키지 않고' 즉시 ForceFreeze(권위 오염 대신 정지).
//
//  ── fail-closed(적대검증 — fail-open 원천 차단) ────────────────────────────
//   · 환율 ≤0 / import 오버플로 → 즉시 trip(오염 표본 미기록).
//   · 3σ 급변동(창 표본 ≥ minSamples, σ>0) → trip. σ=0(완전 평탄)에선 판정 불가로 미발화(보수).
//   · Submit 시 직전 수용 대비 gap > stale → 피드 지연 → trip. TryGetGuardedRate(now) 조회 시에도
//     stale 이면 trip(청산 경로가 오래된 환율로 진행하는 fail-open 차단).
//   · 한 번 trip 되면 래치 유지(IsOpen) — 운영자가 att ManualRelease + Reset() 해야 재개.
//
//  ★clock 주입 필수 — 단위 ticks(100ns, DateTime.Ticks 의미) 일관·결정론 테스트.
//  ★통계는 decimal 환율값(≈1330 규모)으로 계산해 catastrophic cancellation 회피(raw 1e11 미사용).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>실시간 환율 폴 소스 추상(실 피드 격리). TryPoll: true=신규 환율 1건, false=없음(정상).</summary>
    public interface IFxRateSource
    {
        bool TryPoll(out decimal rate);
    }

    /// <summary>
    /// 환율 시계열 서킷 브레이커. 급변동(3σ)·스테일(지연) 시 fail-closed 동결. 조회는 락 보호(콜드 패스).
    /// </summary>
    public sealed class QFxCircuitBreaker
    {
        private readonly struct Sample
        {
            public readonly long   Ticks;
            public readonly long   RateRaw;
            public readonly double Val;      // decimal 환율값(≈1330) — 통계용
            public Sample(long ticks, long rateRaw, double val) { Ticks = ticks; RateRaw = rateRaw; Val = val; }
        }

        private readonly OrderAttestationService? _att;
        private readonly Func<long> _clock;
        private readonly long   _windowTicks;
        private readonly long   _staleTicks;
        private readonly double _sigmaK;
        private readonly int    _minSamples;

        private readonly Sample[] _buf;
        private int    _start;      // 최고참(oldest) 인덱스
        private int    _count;      // 현재 창 표본 수
        private double _sum;        // 창 내 Val 합
        private double _sumSq;      // 창 내 Val^2 합

        private long   _lastRaw;
        private long   _lastAcceptTicks;
        private bool   _hasLast;

        private int    _openLatch;  // 1 = tripped(open)
        private byte   _stateCode;  // 0 Normal · 1 Stale · 2 Violated(3σ/비양수/오버플로/외부). 개방 시 스냅샷 노출.
        private long   _accepted, _tripped;
        private volatile string? _lastReason;

        private readonly object _lock = new();

        /// <param name="clock">현재 시각(ticks·100ns) — 주입 필수(단위/결정론).</param>
        /// <param name="attestation">동결 채널(옵션). null 이면 카운터/래치만(테스트 격리).</param>
        /// <param name="windowSeconds">이동 평균/σ 창(기본 600s=10분).</param>
        /// <param name="staleSeconds">스테일 임계(기본 1s).</param>
        /// <param name="sigmaK">급변동 배수(기본 3.0).</param>
        /// <param name="minSamples">3σ 판정에 필요한 최소 창 표본(미만이면 판정 보류·수집).</param>
        /// <param name="capacity">창 표본 버퍼 상한(초과 시 최고참 축출).</param>
        public QFxCircuitBreaker(
            Func<long> clock,
            OrderAttestationService? attestation = null,
            long windowSeconds = 600,
            long staleSeconds = 1,
            double sigmaK = 3.0,
            int minSamples = 8,
            int capacity = 8192)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));   // 단위 오염 차단(기본값 금지)
            _att   = attestation;
            _windowTicks = (windowSeconds <= 0 ? 1 : windowSeconds) * TimeSpan.TicksPerSecond;
            _staleTicks  = (staleSeconds  <= 0 ? 1 : staleSeconds)  * TimeSpan.TicksPerSecond;
            _sigmaK      = sigmaK <= 0 ? 3.0 : sigmaK;
            _minSamples  = minSamples < 2 ? 2 : minSamples;
            _buf         = new Sample[capacity < 2 ? 2 : capacity];
        }

        // ── 텔레메트리(관제) ──────────────────────────────────────────────────
        public bool   IsOpen        => Volatile.Read(ref _openLatch) == 1;
        public long   Accepted      => Interlocked.Read(ref _accepted);
        public long   TrippedCount  => Interlocked.Read(ref _tripped);
        public string? LastReason   => _lastReason;
        public int    SampleCount   { get { lock (_lock) return _count; } }

        // ====================================================================
        // 표본 수용 — 급변동/스테일/오염 시 trip. 반환: true=수용(닫힘 유지), false=거부(open).
        // ====================================================================
        public bool Submit(decimal rate)
        {
            // ★fail-closed(경계): 비양수·오버플로는 표본에 기록조차 하지 않음(오염 통계 금지).
            if (rate <= 0m) { Trip("환율 비양수 — fail-closed", 2); return false; }
            long raw;
            try { raw = QFxRate.Of(rate).Raw; }
            catch (OverflowException) { Trip("환율 import 오버플로 — fail-closed", 2); return false; }
            return SubmitCore(raw, (double)rate);
        }

        /// <summary>
        /// 이미 QAcl.TryImportRate 경계를 통과한 QFixed raw 환율을 투입(3단계 §3.2 흐름: 외부 decimal →
        /// QAcl → QFixed raw → 브레이커 시계열). raw≤0 은 방어적으로 trip(경계가 이미 걸렀어야 함).
        /// </summary>
        public bool SubmitRaw(QFxRate rate)
        {
            long raw = rate.Raw;
            if (raw <= 0) { Trip("환율 raw 비양수 — fail-closed", 2); return false; }
            return SubmitCore(raw, (double)rate.ToDecimal());
        }

        /// <summary>외부(예: QAcl 경계 거부) 사유로 브레이커를 명시 래치. ForceFreeze 는 멱등(중복 무해).</summary>
        public void TripExternal(string reason) => Trip(reason, 2);

        private bool SubmitCore(long raw, double val)
        {
            long now = _clock();

            lock (_lock)
            {
                if (_openLatch == 1) return false;   // 이미 개방 — 운영자 Reset 전까지 수용 없음

                // 피드 지연: 직전 수용 대비 gap 초과 → 지연 재개(잠재 점프) fail-closed.
                if (_hasLast && (now - _lastAcceptTicks) > _staleTicks)
                { TripLocked("피드 스테일(수용 간격 초과) — fail-closed", 1); return false; }

                EvictOldLocked(now);

                // 3σ 급변동 — 충분 표본·σ>0 에서만 판정(평탄 이력에선 보류).
                if (_count >= _minSamples)
                {
                    double mean = _sum / _count;
                    double var  = (_sumSq / _count) - (mean * mean);
                    double sd   = var > 0 ? Math.Sqrt(var) : 0.0;
                    if (sd > 0 && Math.Abs(val - mean) >= _sigmaK * sd)
                    { TripLocked($"환율 3σ 급변동(|Δ|={Math.Abs(val - mean):F4}, σ={sd:F4}) — fail-closed", 2); return false; }
                }

                AddLocked(new Sample(now, raw, val));
                _lastRaw = raw; _lastAcceptTicks = now; _hasLast = true;
                Interlocked.Increment(ref _accepted);
                return true;
            }
        }

        // ====================================================================
        // 가드 조회 — 청산 경로가 '신선하고 브레이커 닫힌' 환율만 얻는 유일 지점.
        //   open/미수집/스테일이면 false. 스테일은 조회 시점에도 trip(오래된 환율 통과 차단).
        // ====================================================================
        public bool TryGetGuardedRate(long now, out QFxRate rate)
        {
            rate = default;
            lock (_lock)
            {
                if (_openLatch == 1) return false;
                if (!_hasLast)       return false;    // 표본 없음 — 신선도 미확립(청산 불가)
                if ((now - _lastAcceptTicks) > _staleTicks)
                { TripLocked("피드 스테일(가드 조회) — fail-closed", 1); return false; }
                rate = new QFxRate(_lastRaw);
                return true;
            }
        }

        /// <summary>운영자 수동 재개 — 래치 해제 + 창/통계 초기화. att 동결 해제(ManualRelease)는 별도.</summary>
        public void Reset()
        {
            lock (_lock)
            {
                Volatile.Write(ref _openLatch, 0);
                _stateCode = 0;
                _start = _count = 0; _sum = _sumSq = 0.0;
                _hasLast = false; _lastRaw = 0; _lastAcceptTicks = 0;
            }
        }

        /// <summary>UI 텔레메트리용 FX 통계 스냅샷(무할당·out). 마지막 환율·이동평균·1σ raw + 상태코드(개방 시 사유).</summary>
        public void ReadFxStats(out long lastRaw, out long maRaw, out long sigmaRaw, out byte state)
        {
            lock (_lock)
            {
                lastRaw = _hasLast ? _lastRaw : 0L;
                if (_count > 0)
                {
                    double mean = _sum / _count;
                    double var  = (_sumSq / _count) - (mean * mean);
                    double sd   = var > 0 ? Math.Sqrt(var) : 0.0;
                    maRaw    = (long)(mean * QMath.Scale);
                    sigmaRaw = (long)(sd * QMath.Scale);
                }
                else { maRaw = 0L; sigmaRaw = 0L; }
                state = _openLatch == 1 ? _stateCode : (byte)0;
            }
        }

        // ── 내부(락 보유 가정) ────────────────────────────────────────────────
        private void EvictOldLocked(long now)
        {
            while (_count > 0 && (now - _buf[_start].Ticks) > _windowTicks)
                RemoveOldestLocked();
        }

        private void AddLocked(in Sample s)
        {
            if (_count == _buf.Length) RemoveOldestLocked();   // 용량 초과 → 최고참 축출
            int idx = (_start + _count) % _buf.Length;
            _buf[idx] = s;
            _count++;
            _sum   += s.Val;
            _sumSq += s.Val * s.Val;
        }

        private void RemoveOldestLocked()
        {
            ref readonly Sample s = ref _buf[_start];
            _sum   -= s.Val;
            _sumSq -= s.Val * s.Val;
            _start  = (_start + 1) % _buf.Length;
            _count--;
            if (_count == 0) { _sum = 0.0; _sumSq = 0.0; }   // 부동소수 잔차 리셋(빈 창 정확 0)
        }

        private void Trip(string reason, byte state)
        {
            lock (_lock) { TripLocked(reason, state); }
        }

        private void TripLocked(string reason, byte state)
        {
            _lastReason = reason;
            _stateCode  = state;   // 개방 사유 코드(0/1/2) — ReadFxStats 가 개방 시 노출
            Interlocked.Increment(ref _tripped);
            if (Interlocked.Exchange(ref _openLatch, 1) == 0)   // 1회만 ForceFreeze(래치)
            {
                try { _att?.ForceFreeze("QFxCircuitBreaker: " + reason); }
                catch { /* 동결 실패해도 래치·계상은 정직 유지 */ }
            }
        }
    }

    // ========================================================================
    // 실시간 FX 피드 펌프 — IFxRateSource 폴 → QFxCircuitBreaker.Submit. 백그라운드 격리.
    //   ★liveArmed:false 불변: '구성'만. 실 피드 소스·자동 시작은 호출부 옵트인.
    // ========================================================================
    /// <summary>환율 폴 소스를 주기적으로 읽어 브레이커에 투입하는 콜드 패스 펌프.
    /// ★3단계 §3.2 흐름: 외부 decimal → QAcl.TryImportRate(경계 부패방지·≤0/오버플로 fail-closed) →
    /// QFixed raw → QFxCircuitBreaker.SubmitRaw(시계열 3σ/스테일). QAcl 거부 시 브레이커도 명시 래치.</summary>
    public sealed class QFxFeedPump : IDisposable
    {
        private readonly IFxRateSource          _source;
        private readonly QFxCircuitBreaker       _breaker;
        private readonly OrderAttestationService? _att;   // QAcl 경계 동결 채널(옵션)
        private readonly int                     _pollMs;

        private Thread?       _thread;
        private volatile bool _running;

        public QFxFeedPump(IFxRateSource source, QFxCircuitBreaker breaker, int pollMs = 200,
                           OrderAttestationService? attestation = null)
        {
            _source  = source  ?? throw new ArgumentNullException(nameof(source));
            _breaker = breaker ?? throw new ArgumentNullException(nameof(breaker));
            _att     = attestation;
            _pollMs  = pollMs < 20 ? 20 : pollMs;
        }

        public bool Running => _running;

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "QFxFeedPump" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            var t = _thread;
            if (t is not null && t.IsAlive) t.Join(2000);
            _thread = null;
        }

        /// <summary>1회 폴→(QAcl 경계)→투입(결정론 테스트용). 소스 예외는 흡수하지 않음 — 루프에서만 흡수.</summary>
        public void PumpOnce()
        {
            if (!_source.TryPoll(out decimal rate)) return;   // 데이터 없음(정상) — 브레이커 스테일 감지가 지연을 포착
            // ★QAcl 경계: 외부 decimal → QFixed raw. ≤0/오버플로는 여기서 fail-closed(att 동결) + 브레이커 래치.
            if (QAcl.TryImportRate(rate, _att, out var q))
                _breaker.SubmitRaw(q);
            else
                _breaker.TripExternal("FX 경계 ACL 거부(≤0/오버플로) — fail-closed");
        }

        private void Loop()
        {
            while (_running)
            {
                try { PumpOnce(); }
                catch { /* 소스 일시 오류는 흡수 — 스테일 감지가 지연을 fail-closed 로 포착 */ }
                int slept = 0;
                while (slept < _pollMs && _running) { int c = Math.Min(20, _pollMs - slept); Thread.Sleep(c); slept += c; }
            }
        }

        public void Dispose() => Stop();
    }
}
