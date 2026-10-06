// ============================================================================
// CorrelationCrashInterlock — [포트폴리오 · 방향 B] Portfolio Correlation Crash Disarm Interlock(δ=0)
//
//  개별 종목 급락은 MarketDataSpikeInterlock(틱 점프)·극한 슬리피지 인터락이 잡는다. 이 인터락이 잡는 것은 '체계적' 위험 —
//  보유 종목들이 같은 방향으로 '동시에' 무너지는 상관 붕괴(서킷브레이커 전야 · 섹터 패닉)다. 이때 개별 종목은 한도 안이어도
//  포트폴리오는 분산 효과가 사라져 VaR(공분산 추정 기반)를 크게 넘는다.
//  판정(세션 단위 · 정수 bps):
//    · 보유 종목마다 세션 고점 대비 낙폭 dd = (고점 − 현재가) × 10,000 / 고점.
//    · dd ≥ dropBps 인 보유 종목 수 = crashed. 보유 종목 수 = held.
//    · crashed ≥ minNames  ∧  crashed × 10,000 ≥ breadthBps × held  → Trip(sticky).
//      (두 조건 동시 — 소수 종목 포트폴리오에서 1~2종목 급락을 '체계적'으로 오판하지 않고, 대형 포트폴리오에서도 폭(breadth) 요구)
//  Trip 즉시: ① 집계 비트(ArmingInterlockSource.CorrelationCrash) 원자 Raise → 신규 송출 차단
//            ② 브로커 접수 미체결 주문 일괄 취소 요청(취소는 노출 축소 — 무장 차단과 무관하게 집행)
//  재무장 = 물리 이중키(TryRearm) + ResetSession(고점 재설정). 이 호스트에는 운용자 재무장 UI 경로가 없으므로 실질 복구 = 재시작.
//  δ=0: 고정 배열 · 선형 탐색 · 취소 대상 번호는 사전 할당 버퍼(취소 송신 자체는 브로커 문자열 계약 — 콜드 1회).
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;

namespace StockManager.Engines
{
    public sealed class CorrelationCrashInterlock : IArmingBlockSource
    {
        public const long DefaultDropBps = 300;       // 세션 고점 대비 −3%
        public const int DefaultMinNames = 3;
        public const long DefaultBreadthBps = 6_000;  // 보유 종목의 60% 이상

        private readonly object _gate = new();
        private readonly long _dropBps, _breadthBps;
        private readonly int _minNames;
        private readonly long[] _code, _peak, _last;
        private readonly bool[] _held, _crashed;
        private int _count, _heldCount, _crashedCount;
        private readonly long[] _cancelBuf;
        private readonly Func<Memory<long>, int>? _openOrders;   // 미체결 주문번호 수집(예: OrderExecutionFeedAdapter.CopyOpenBrokerOrders)
        private readonly Action<long>? _cancel;                  // 주문번호 → 취소 송신(예: ExecutionBridgeBroker.Cancel)
        private readonly Action<int>? _onTrip;
        private ArmingInterlockLink? _link;
        private int _tripped;
        private long _trips, _cancelsIssued, _cancelFaults, _ticks;

        public CorrelationCrashInterlock(long dropBps = DefaultDropBps, int minNames = DefaultMinNames, long breadthBps = DefaultBreadthBps,
                                         int capacity = 512, Func<Memory<long>, int>? openOrders = null, Action<long>? cancel = null,
                                         Action<int>? onTrip = null, int cancelCapacity = 1024)
        {
            if (dropBps <= 0 || dropBps >= 10_000) throw new ArgumentOutOfRangeException(nameof(dropBps));
            if (minNames < 1) throw new ArgumentOutOfRangeException(nameof(minNames));
            if (breadthBps <= 0 || breadthBps > 10_000) throw new ArgumentOutOfRangeException(nameof(breadthBps));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _dropBps = dropBps; _minNames = minNames; _breadthBps = breadthBps;
            _code = new long[capacity]; _peak = new long[capacity]; _last = new long[capacity];
            _held = new bool[capacity]; _crashed = new bool[capacity];
            _cancelBuf = new long[Math.Max(1, cancelCapacity)];
            _openOrders = openOrders; _cancel = cancel; _onTrip = onTrip;
        }

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;
        public long Trips => Interlocked.Read(ref _trips);
        public long CancelsIssued => Interlocked.Read(ref _cancelsIssued);
        public long CancelFaults => Interlocked.Read(ref _cancelFaults);
        public long Ticks => Interlocked.Read(ref _ticks);
        public int HeldCount { get { lock (_gate) return _heldCount; } }
        public int CrashedCount { get { lock (_gate) return _crashedCount; } }

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        /// <summary>보유 종목 집합 갱신(계좌 동기화 주기 · δ=0). 새로 보유된 종목은 고점을 최근가로 시작.</summary>
        public void SetHeld(ReadOnlySpan<long> symbols)
        {
            bool trip;
            lock (_gate)
            {
                for (int i = 0; i < _count; i++) _held[i] = false;
                for (int k = 0; k < symbols.Length; k++)
                {
                    int i = Slot(symbols[k], create: true);
                    if (i < 0) continue;
                    if (!_held[i] && _peak[i] == 0) _peak[i] = _last[i];
                    _held[i] = true;
                }
                Recount();
                trip = ShouldTrip();
            }
            if (trip) Trip();
        }

        /// <summary>체결가 틱(원 · δ=0). 보유 종목만 판정에 반영 — 비보유는 고점만 추적(보유 전환 대비).</summary>
        public void OnTick(long symbolCode, long priceWon)
        {
            if (priceWon <= 0) return;
            Interlocked.Increment(ref _ticks);
            bool trip = false;
            lock (_gate)
            {
                int i = Slot(symbolCode, create: true);
                if (i < 0) return;
                _last[i] = priceWon;
                if (priceWon > _peak[i]) _peak[i] = priceWon;
                bool crashed = _peak[i] > 0 && (Int128)(_peak[i] - priceWon) * 10_000 >= (Int128)_dropBps * _peak[i];
                if (crashed != _crashed[i])
                {
                    _crashed[i] = crashed;
                    if (_held[i]) _crashedCount += crashed ? 1 : -1;
                }
                if (_held[i] && crashed) trip = ShouldTrip();
            }
            if (trip) Trip();
        }

        /// <summary>세션 재시작(장 개시 · 재무장 시) — 고점 = 최근가로 재설정 · 낙폭 플래그 해제.</summary>
        public void ResetSession()
        {
            lock (_gate)
            {
                for (int i = 0; i < _count; i++) { _peak[i] = _last[i]; _crashed[i] = false; }
                _crashedCount = 0;
            }
        }

        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            ResetSession();
            Volatile.Write(ref _tripped, 0);
            Volatile.Read(ref _link)?.Resync();
            return true;
        }

        // ── 내부 ──

        private bool ShouldTrip()
            => Volatile.Read(ref _tripped) == 0 && _heldCount >= _minNames && _crashedCount >= _minNames
               && (long)_crashedCount * 10_000 >= _breadthBps * _heldCount;

        private void Recount()
        {
            int h = 0, c = 0;
            for (int i = 0; i < _count; i++) { if (!_held[i]) continue; h++; if (_crashed[i]) c++; }
            _heldCount = h; _crashedCount = c;
        }

        private void Trip()
        {
            Interlocked.Increment(ref _trips);
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return;
            Volatile.Read(ref _link)?.Resync();                         // ① 차단 선반영
            int issued = 0;
            if (_openOrders is { } collect && _cancel is { } cancel)    // ② 미체결 일괄 취소(노출 축소)
            {
                int n;
                try { n = collect(_cancelBuf); } catch (Exception) { n = 0; Interlocked.Increment(ref _cancelFaults); }
                for (int k = 0; k < n && k < _cancelBuf.Length; k++)
                {
                    try { cancel(_cancelBuf[k]); issued++; }
                    catch (Exception) { Interlocked.Increment(ref _cancelFaults); }
                }
                Interlocked.Add(ref _cancelsIssued, issued);
            }
            try { _onTrip?.Invoke(issued); } catch (Exception) { Interlocked.Increment(ref _cancelFaults); }
        }

        private int Slot(long symbolCode, bool create)
        {
            for (int i = 0; i < _count; i++) if (_code[i] == symbolCode) return i;
            if (!create || _count == _code.Length) return -1;
            _code[_count] = symbolCode; _peak[_count] = 0; _last[_count] = 0; _held[_count] = false; _crashed[_count] = false;
            return _count++;
        }
    }
}
