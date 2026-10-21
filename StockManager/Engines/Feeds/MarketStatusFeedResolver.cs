// ============================================================================
// MarketStatusFeedResolver — [v12.18.50-DEV] 장운영 세션 · 종목 상태 이벤트(VI · 정지) → AiTradingPipelineService 거래 상태 단일 작성자(δ=0)
//
//  왜: 호가 피드(키움 10호가)는 거래 상태를 싣지 않아 감독자 슬롯이 Unknown(차단)에 머문다(§143). 상태 피드(KIS H0STMKO0)는 VI 발동/해제
//   시에만 프레임이 온다 — '프레임 없음 = 정상' 을 안전하게 쓰려면 세션 시각 · 피드 생존 · 재접속 공백을 함께 따져야 한다.
//  유효 상태(종목별 · 우선순위 위→아래 · Normal 은 맨 마지막 하나뿐):
//   ① 세션 페이즈 ≠ ContinuousTrading(KrxSessionStateMachine §v12.18.51: 휴장일 달력 · 수능 순연 · 연초 지연 개장 · 장 시작/마감 동시호가 ·
//      시간외 · 달력 범위 밖) 또는 주입 휴장 판정기 true = Closed
//   ② 이벤트 래치 ≠ Normal(VI · 정지 · 미분류 코드 = Unknown) = 그 상태 — ★해제는 같은 종목의 Normal 이벤트로만(호가 · 세션 · 시간 경과로 풀리지 않음)
//   ③ 상태 피드 비생존(미연결 · 트립 · 침묵) = Unknown
//   ④ 재접속 직후 재동기 보류(기본 150초 = VI 2분 + 종료 무작위 30초) 중이고 그 종목 프레임을 아직 못 받음 = Unknown
//      (단절 중 시작된 VI 를 놓쳤을 수 있다 — 보류 동안 그 종목 프레임이 오면 즉시 판정)
//   ⑤ 그 외 = Normal
//  반영: 유효 상태가 바뀐 종목만 pipeline.OnTradingStatus(종목, 상태, 단조 시각) — 감독자에서 제한 = 고정(pin) · Normal = 해제.
//   상태 프레임 판독 실패 = NoteFeedFault → 재동기 보류 재시작(어느 종목 통지였는지 모름 · 전 종목 Unknown).
//   이벤트 시각 역행: 제한 이벤트 = 그대로 반영(정지 유실 0) · 해제 이벤트 = 마지막 이벤트보다 이르면 거부(늦게 온 옛 해제 차단).
//  슬롯 64(감독자와 같은 상한 · 개방 주소 해시 · 해제 없음) · 상태 피드 수신 스레드와 타이머 Evaluate 는 CAS 스핀으로 직렬화
//   (임계 구역 = 배열 갱신 + 감독자 상태 통지뿐 · 대기 짧음) — 이벤트를 버리는 경합 경로 없음(제한 · 해제 모두 반드시 반영).
//  ★신호 시점 게이트(ITradingStatusGate): 감독자 슬롯 통지는 타이머 주기(호스트 50ms) 사이에 낡을 수 있다(15:20:00 직후 · 단절 직후) —
//   파이프라인이 신호마다 IsTradable(자체 벽시계)을 평가 전 + 인계 직전에 직접 물어 그 틈을 닫는다(무잠금 Volatile 판독 · δ=0).
//  ★정직: 휴장일 달력 = KrxTradingCalendar 2024~2027 내장(범위 밖 = 전면 차단 · 임시공휴일은 표 갱신 필요 · 주입 판정기는 추가 휴장 OR) ·
//   상태 코드 값 사상은 KisMarketStatusParser 머리말 참조(fail-closed).
// ============================================================================
using System;
using System.Threading;
using StockManager.Engines.Strategy;

namespace StockManager.Engines.Feeds
{
    public enum MarketStatusEventVerdict : byte { Applied = 0, InvalidSymbol = 1, SlotsExhausted = 2, StaleRelease = 3 }

    public sealed class MarketStatusFeedResolver : ITradingStatusGate
    {
        public const int MaxSlots = AiExecutionSupervisor.MaxSlots;
        public const long DefaultResyncHoldMicros = 150_000_000;                       // VI 2분 + 무작위 종료 30초
        private const long MicrosPerDay = 86_400_000_000L, MicrosPerMinute = 60_000_000L;
        private const long KstOffset = 9 * 60 * MicrosPerMinute;

        private readonly AiTradingPipelineService _pipeline;
        private readonly Func<long, bool>? _isHoliday;
        private readonly Func<long> _utcNow;
        private readonly long _hold;
        private readonly long[] _sym = new long[MaxSlots], _latch = new long[MaxSlots], _latchTs = new long[MaxSlots], _pushed = new long[MaxSlots];
        private readonly long[] _seenEpoch = new long[MaxSlots];
        private readonly long[] _keys = new long[128];
        private readonly int[] _vals = new int[128];
        private int _used, _busy, _feedAlive;
        private long _epoch, _resyncUntil, _lastNow;
        private long _events, _applied, _staleReleases, _invalid, _exhausted, _pushes, _evaluations, _faults;

        /// <param name="pipeline">거래 상태 반영 대상(감독자 슬롯).</param>
        /// <param name="isKrxHoliday">KST 일 번호(1970-01-01 = 0) → 추가 휴장일 여부(내장 달력에 OR · null = 내장 달력만).</param>
        /// <param name="utcNowMicros">신호 시점 게이트용 벽시계(유닉스 UTC 마이크로초 · null = DateTime.UtcNow).</param>
        public MarketStatusFeedResolver(AiTradingPipelineService pipeline, Func<long, bool>? isKrxHoliday = null, long resyncHoldMicros = DefaultResyncHoldMicros,
                                        Func<long>? utcNowMicros = null)
        {
            _utcNow = utcNowMicros ?? UtcNowMicros;
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            if (resyncHoldMicros < 0) throw new ArgumentOutOfRangeException(nameof(resyncHoldMicros));
            _isHoliday = isKrxHoliday; _hold = resyncHoldMicros;
            for (int i = 0; i < MaxSlots; i++) { _latch[i] = (long)MarketTradingStatus.Normal; _pushed[i] = -1; }
        }

        public static long UtcNowMicros() => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;

        /// <summary>신호 시점 게이트(자체 벽시계 · 무잠금 판독 · δ=0) — 유효 상태 Normal 일 때만 true.</summary>
        public bool IsTradable(long symbolCode) => EffectiveStatus(symbolCode, _utcNow()) == MarketTradingStatus.Normal;

        public int Symbols => Volatile.Read(ref _used);
        public bool FeedAlive => Volatile.Read(ref _feedAlive) == 1;
        public long Events => Interlocked.Read(ref _events);
        public long Applied => Interlocked.Read(ref _applied);
        public long StaleReleases => Interlocked.Read(ref _staleReleases);
        public long Pushes => Interlocked.Read(ref _pushes);
        public long Evaluations => Interlocked.Read(ref _evaluations);
        public long FeedFaults => Interlocked.Read(ref _faults);

        /// <summary>KRX 세션 페이즈(KrxSessionStateMachine · 휴장일 달력 · 수능 순연 · 동시호가 구분 · δ=0). utcMicros = 유닉스 에포크 UTC 마이크로초.</summary>
        public KrxSessionPhase SessionPhase(long utcMicros) => KrxSessionStateMachine.GetSessionPhaseUtcMicros(utcMicros);

        /// <summary>KRX 정규 연속매매(ContinuousTrading 페이즈 ∧ 주입 휴장 판정기 false) 판정(순수 · δ=0). utcMicros = 유닉스 에포크 UTC 마이크로초.</summary>
        public bool InContinuousSession(long utcMicros)
        {
            if (KrxSessionStateMachine.GetSessionPhaseUtcMicros(utcMicros) != KrxSessionPhase.ContinuousTrading) return false;   // 동시호가 · 시간외 · 휴장 · 표 범위 밖 = 차단
            long day = (utcMicros + KstOffset) / MicrosPerDay;
            if (_isHoliday is { } h) { bool hol; try { hol = h(day); } catch (Exception) { hol = true; } if (hol) return false; }   // 판정 예외 = 휴장(fail-closed)
            return true;
        }

        /// <summary>상태 피드 구독 종목 등록(콜드). 등록 종목만 '프레임 없음 = 정상' 판정 대상.</summary>
        public bool Register(long symbolCode)
        {
            if (symbolCode < 1 || symbolCode > MultiSymbolAiSignalRouter.MaxSymbolCode) return false;
            Enter();
            try { return FindOrClaim(symbolCode) >= 0; }
            finally { Volatile.Write(ref _busy, 0); }
        }

        /// <summary>상태 피드 생존 갱신(타이머). 비생존 → 생존 전이 = 재동기 보류 시작(그 사이 프레임 못 받은 종목 = Unknown).</summary>
        public void SetFeedAlive(bool alive, long utcMicros)
        {
            Enter();
            try
            {
                int prev = Volatile.Read(ref _feedAlive);
                if (alive && prev == 0) { Volatile.Write(ref _epoch, _epoch + 1); Volatile.Write(ref _resyncUntil, Mono(utcMicros) + _hold); }
                Volatile.Write(ref _feedAlive, alive ? 1 : 0);
                if ((alive ? 1 : 0) != prev) { int n = Volatile.Read(ref _used); for (int s = 0; s < n; s++) Push(s, utcMicros); }   // 생존 전이 = 즉시 반영
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        /// <summary>
        /// 상태 프레임 판독 실패(형식 이탈 · 어떤 종목의 통지인지 모름) — 놓친 VI/정지가 있을 수 있으므로 재동기 보류를 새로 시작(전 종목 프레임 재수신 전까지
        /// 또는 보류 만료까지 Unknown). 래치 자체는 유지.
        /// </summary>
        public void NoteFeedFault(long utcMicros)
        {
            Enter();
            try
            {
                Volatile.Write(ref _epoch, _epoch + 1);
                Volatile.Write(ref _resyncUntil, Mono(utcMicros) + _hold);
                Interlocked.Increment(ref _faults);
                int n = Volatile.Read(ref _used);
                for (int s = 0; s < n; s++) Push(s, utcMicros);
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        /// <summary>
        /// 종목 상태 이벤트 1건(δ=0). 제한(VI · 정지 · Unknown · 미정의) = 즉시 래치 · Normal = 래치 해제(마지막 이벤트보다 이르면 StaleRelease 거부).
        /// 반영 후 그 종목 유효 상태를 즉시 재계산해 바뀌었으면 파이프라인에 반영.
        /// </summary>
        public MarketStatusEventVerdict OnStatusEvent(long symbolCode, MarketTradingStatus status, long eventUtcMicros, long nowUtcMicros)
        {
            Interlocked.Increment(ref _events);
            if (symbolCode < 1 || symbolCode > MultiSymbolAiSignalRouter.MaxSymbolCode) { Interlocked.Increment(ref _invalid); return MarketStatusEventVerdict.InvalidSymbol; }
            bool release = status == MarketTradingStatus.Normal;
            Enter();
            try
            {
                int s = FindOrClaim(symbolCode);
                if (s < 0) { Interlocked.Increment(ref _exhausted); return MarketStatusEventVerdict.SlotsExhausted; }
                long last = _latchTs[s];
                if (release && eventUtcMicros < last) { Interlocked.Increment(ref _staleReleases); return MarketStatusEventVerdict.StaleRelease; }
                Volatile.Write(ref _latch[s], (long)status);
                _latchTs[s] = Math.Max(last, eventUtcMicros);
                Volatile.Write(ref _seenEpoch[s], _epoch);                                                 // 이 연결에서 그 종목 프레임 수신 = 재동기 완료
                Interlocked.Increment(ref _applied);
                Push(s, nowUtcMicros);
                return MarketStatusEventVerdict.Applied;
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        /// <summary>전 종목 유효 상태 재계산(타이머 · δ=0) — 세션 개장/마감 · 피드 생존 · 재동기 보류 만료 전이를 반영.</summary>
        public void Evaluate(long nowUtcMicros)
        {
            Enter();
            try
            {
                Interlocked.Increment(ref _evaluations);
                int n = Volatile.Read(ref _used);
                for (int s = 0; s < n; s++) Push(s, nowUtcMicros);
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        /// <summary>종목 유효 상태(관제 · 판독 전용 · 미등록 = Unknown).</summary>
        public MarketTradingStatus EffectiveStatus(long symbolCode, long nowUtcMicros)
        {
            int s = Find(symbolCode);
            return s < 0 ? MarketTradingStatus.Unknown : Effective(s, nowUtcMicros);
        }

        private MarketTradingStatus Effective(int s, long nowUtcMicros)
        {
            if (!InContinuousSession(nowUtcMicros)) return MarketTradingStatus.Closed;
            var latch = (MarketTradingStatus)(byte)Volatile.Read(ref _latch[s]);
            if (latch != MarketTradingStatus.Normal) return latch;
            if (Volatile.Read(ref _feedAlive) == 0) return MarketTradingStatus.Unknown;
            if (nowUtcMicros < Volatile.Read(ref _resyncUntil) && Volatile.Read(ref _seenEpoch[s]) != Volatile.Read(ref _epoch)) return MarketTradingStatus.Unknown;
            return MarketTradingStatus.Normal;
        }

        // 바뀐 종목만 반영(단일 작성자 보유 중 호출)
        private void Push(int s, long nowUtcMicros)
        {
            long now = Mono(nowUtcMicros);
            long eff = (long)Effective(s, nowUtcMicros);
            if (eff == _pushed[s]) return;
            _pipeline.OnTradingStatus(_sym[s], (MarketTradingStatus)(byte)eff, now);
            _pushed[s] = eff;
            Interlocked.Increment(ref _pushes);
        }

        // 반영 시각 단조화(감독자 해제 통지는 마지막 상태 통지 시각 이상이어야 수용)
        private long Mono(long t) { if (t < _lastNow) t = _lastNow; _lastNow = t; return t; }

        private void Enter() { while (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) Thread.SpinWait(8); }

        private int Home(long sym) => (int)((ulong)(sym * unchecked((long)0x9E3779B97F4A7C15UL)) >> 57) & 127;

        private int Find(long sym)
        {
            if (sym < 1 || sym > MultiSymbolAiSignalRouter.MaxSymbolCode) return -1;
            for (int h = Home(sym), n = 0; n < 128; h = (h + 1) & 127, n++)
            {
                long k = Volatile.Read(ref _keys[h]);
                if (k == sym) return _vals[h];
                if (k == 0) return -1;
            }
            return -1;
        }

        private int FindOrClaim(long sym)
        {
            for (int h = Home(sym), n = 0; n < 128; h = (h + 1) & 127, n++)
            {
                long k = _keys[h];
                if (k == sym) return _vals[h];
                if (k != 0) continue;
                if (_used >= MaxSlots) return -1;
                int s = _used;
                _sym[s] = sym;
                _seenEpoch[s] = -1;
                _vals[h] = s;
                Volatile.Write(ref _keys[h], sym);
                Volatile.Write(ref _used, s + 1);
                return s;
            }
            return -1;
        }
    }
}
