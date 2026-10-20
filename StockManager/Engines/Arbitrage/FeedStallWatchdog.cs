// ============================================================================
// FeedStallWatchdog — [v12.18.38-DEV] 전역 피드 동시 두절 워치독(비트 28 OrderPathLatency 4차 입력 · δ=0)
//
//  배경: SyntheticSpreadRadar 의 전파 지연 · 침묵 판정은 '산 피드의 틱'에서만 일어난다 — 두 피드가 함께 멈추면(ISP 다운 · 프록시 프리징)
//   판정할 틱 자체가 없어 맹목. 워치독은 틱 경로와 분리된 저부하 타이머(기본 100ms)가 마지막 수신 시각을 능동 대조한다.
//  입력: 피드 디스패치 스레드가 수신마다 Stamp(venue) — Interlocked.Exchange 로 venue 별 마지막 수신 시각(µs) 1회 기록(락 0 · 할당 0).
//  판정(Poll · 타이머 단일 호출자): 장중(호스트 = OrderbookIlliquidityInterlock.KrxContinuousSessionNow · KRX 연속매매 09:00 ≤ KST < 15:20 · 판정 예외 = 장중 취급) ∧ 활성 피드(1회 이상 수신) ≥ 1 ∧
//   모든 활성 피드의 무수신 > StallBudget(기본 3,000ms) = sticky 래치 → 비트 28 Raise(무장 차단 · 비트 재결속 0 · 정의 불변).
//   무수신 기준 = max(마지막 수신, 장외 → 장중 전환 관측 시각) — 장전/장후 정적 구간이 개장 첫 폴링에서 오격발하지 않도록 장 진입부터 예산을 다시 센다.
//   격발 지연 ≤ 예산 + 폴링 주기(100ms). 해제 = TryRearm(물리 이중키) — 두절 지속이면 다음 폴링 즉시 재래치(타이머 재시작 없음 · 안전 방향).
//  δ=0: Stamp · Poll · ArmingBlocked 힙 0. 타이머 콜백 = 정적 람다 + 상태 인자(this) — 클로저 · 박싱 0.
//  ★브로커 · 디스패처 · 무장 키 참조 0 — 실주문 송출 경로 없음(liveArmed:false 무관 · 구조적 0).
//  ★한계(정직): 한 번도 수신하지 않은 피드는 '비활성'(감시 대상 아님 — 미구성 개발 PC 상시 차단 방지) · 공휴일 미반영(격발 측 오차) ·
//   구독 종목이 모두 비유동이면 3초 무변경이 정상일 수 있다(차단 측 오탐 = 안전 방향). 한 피드만 정지 = 레이더 침묵 감시(§119) 담당.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using MS2E.Core;

namespace StockManager.Engines.Arbitrage
{
    /// <summary>[v12.18.38-DEV] 비트 28 4차 입력 수용자(호스트 = OrderPathLatencyInterlock) — 워치독은 지연 인터락 형식을 참조하지 않는다.</summary>
    public interface IFeedStallHost
    {
        void AttachStallGuard(IArmingBlockSource guard);
        void Resync();
    }

    public sealed class FeedStallWatchdog : IArmingBlockSource, IDisposable
    {
        public const int MaxVenues = 4;
        public const long DefaultStallMicros = 3_000_000;
        public const int DefaultPollMillis = 100;

        private readonly long[] _lastRx = new long[MaxVenues];          // venue 별 마지막 수신 µs(0 = 미수신 = 비활성)
        private readonly Func<long> _nowMicros;
        private readonly Func<bool>? _marketHours;
        private readonly long _stallMicros;
        private long _sessionSince;                                     // 장외 → 장중 전환을 관측한 폴링 시각(0 = 기동 이래 장중) · 폴링 단일 작성자
        private bool _offSession;                                       // 직전 폴링 = 장외
        private int _tripped;
        private long _polls, _trips, _violations, _lastStallMicros, _maxStallMicros;
        private IFeedStallHost? _host;
        private System.Threading.Timer? _timer;

        /// <param name="nowMicros">단조 µs 시계(기본 Stopwatch).</param>
        /// <param name="marketHours">장중 판정(미지정 = 상시 장중 · 예외 = 장중 취급).</param>
        /// <param name="stallMicros">모든 활성 피드 무수신 예산(µs · &gt; 0).</param>
        public FeedStallWatchdog(Func<long>? nowMicros = null, Func<bool>? marketHours = null, long stallMicros = DefaultStallMicros)
        {
            if (stallMicros <= 0) throw new ArgumentOutOfRangeException(nameof(stallMicros));
            _nowMicros = nowMicros ?? StopwatchMicros;
            _marketHours = marketHours;
            _stallMicros = stallMicros;
        }

        private static long StopwatchMicros() => (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);

        /// <summary>비트 28 4차 입력 결선(콜드 1회) — 지연 인터락 차단 술어에 이 워치독의 래치를 OR 한다.</summary>
        public void BindLatencyInterlock(IFeedStallHost interlock)
        {
            ArgumentNullException.ThrowIfNull(interlock);
            interlock.AttachStallGuard(this);
            Volatile.Write(ref _host, interlock);
        }

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;
        public long StallBudgetMicros => _stallMicros;
        public long Polls => Interlocked.Read(ref _polls);
        public long Trips => Interlocked.Read(ref _trips);
        /// <summary>예산 초과 판정 수(래치 중 포함).</summary>
        public long Violations => Interlocked.Read(ref _violations);
        /// <summary>최초 래치 시점의 무수신 경과(µs).</summary>
        public long LastStallMicros => Interlocked.Read(ref _lastStallMicros);
        public long MaxStallMicros => Interlocked.Read(ref _maxStallMicros);

        /// <summary>venue 마지막 수신 µs(0 = 미수신).</summary>
        public long LastRxMicros(int venue) => (uint)venue < MaxVenues ? Interlocked.Read(ref _lastRx[venue]) : 0;

        // ── 입력(피드 디스패치 스레드 · δ=0) ──

        /// <summary>수신 1건 — 호가 · 체결 · 하트비트 무엇이든 피드 생존 증거. venue 범위 밖 = 무시.</summary>
        public void Stamp(int venue)
        {
            if ((uint)venue >= MaxVenues) return;
            long now = _nowMicros();
            Interlocked.Exchange(ref _lastRx[venue], now == 0 ? 1 : now);
        }

        // ── 판정(타이머 · 단일 호출자 · δ=0) ──

        /// <summary>1회 판정. 반환 = 이번 판정으로 래치했는가.</summary>
        public bool Poll()
        {
            Interlocked.Increment(ref _polls);
            long now = _nowMicros();
            bool market;
            try { market = _marketHours?.Invoke() ?? true; } catch { market = true; }   // 판정 불가 = 장중 취급(엄격 측)
            if (!market) { _offSession = true; return false; }
            if (_offSession) { _offSession = false; _sessionSince = now; }  // 개장 = 예산 재기산(장전 정적 구간 오격발 0)
            long newest = 0;
            for (int v = 0; v < MaxVenues; v++) newest = Math.Max(newest, Interlocked.Read(ref _lastRx[v]));
            if (newest == 0) return false;                                  // 활성 피드 0 = 감시 대상 없음
            long stall = now - Math.Max(newest, _sessionSince);             // 가장 최근 수신(어느 피드든) 이후 = 모든 활성 피드 무수신
            if (stall <= _stallMicros) return false;
            Interlocked.Increment(ref _violations);
            long cur = Interlocked.Read(ref _maxStallMicros);
            if (stall > cur) Interlocked.Exchange(ref _maxStallMicros, stall);   // 폴링 단일 작성자
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return false;   // 최초 원인만 기록(sticky)
            Interlocked.Exchange(ref _lastStallMicros, stall);
            Interlocked.Increment(ref _trips);
            Volatile.Read(ref _host)?.Resync();                             // ★비트 28 Raise — 무장 차단
            return true;
        }

        /// <summary>물리 이중키 재무장 — 래치 해제(두절 지속 = 다음 폴링 즉시 재래치).</summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _tripped, 0);
            Volatile.Read(ref _host)?.Resync();
            return true;
        }

        /// <summary>백그라운드 폴링 기동(콜드 1회 · 중복 기동 = 무시). 콜백 = 정적 람다 + 상태 인자 — 클로저 0.</summary>
        public void Start(int periodMillis = DefaultPollMillis)
        {
            if (periodMillis <= 0) throw new ArgumentOutOfRangeException(nameof(periodMillis));
            var t = new System.Threading.Timer(static s => ((FeedStallWatchdog)s!).Poll(), this, Timeout.Infinite, Timeout.Infinite);
            if (Interlocked.CompareExchange(ref _timer, t, null) is not null) { t.Dispose(); return; }
            t.Change(periodMillis, periodMillis);
        }

        public void Dispose() => Interlocked.Exchange(ref _timer, null)?.Dispose();
    }
}
