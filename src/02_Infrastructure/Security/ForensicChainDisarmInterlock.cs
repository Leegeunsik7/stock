// ============================================================================
// ForensicChainDisarmInterlock — [MMF 하드닝 Think Beyond 방향 B] Atomic Forensic Chain Disarm Interlock
//
//   교정 전(실측): ZeroAllocForensicAuditStream 의 VerifyIntegrity 결과를 소비하는 곳은 UI 뷰어(ForensicAuditViewAdapter)뿐이었다 —
//   체인이 변조돼도 화면에 "위변조" 배지가 뜰 뿐 발주 무장 술어는 아무것도 몰랐다(감사 기록이 조작되는 동안에도 무장 가능).
//   이 인터락은 체인 무결성을 무장 술어의 1급 입력으로 만든다:
//     ▸ Check(): VerifyDetailed(δ=0 — stackalloc·정적 SHA-256) → 결함이면 0ms Trip(sticky): ArmingBlocked=true(blockWhile) ·
//       VolatileMemoryPurger 전량 0x00 · 등록 발주 토큰(PoisonedSecretBuffer) 중독 · onTrip(킬스위치 격발). 결함 링크는 스트림이
//       격리(ReadLink Quarantined)한다.
//     ▸ StartMonitor(interval): 전용 백그라운드 스레드 주기 검증(루프 할당 0). 스트림 락을 짧게 공유할 뿐 UI/발주 스레드와 무관.
//     ▸ 재무장: 프로세스 재시작 + 물리 이중키만(수동 리셋 API 없음).
//   불변: liveArmed:false(차단만 — 무장 부여 불가) · Check 힙 0 · 예외 비전파(싱크 실패가 차단을 막지 않음).
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;

namespace MS2E.Infrastructure
{
    public sealed class ForensicChainDisarmInterlock : IDisposable, MS2E.Core.IArmingBlockSource
    {
        private readonly ZeroAllocForensicAuditStream _stream;
        private readonly VolatileMemoryPurger? _purger;
        private readonly PoisonedSecretBuffer[] _tokens;
        private readonly Action<ForensicChainVerdict>? _onTrip;
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly object _verdictGate = new();
        private Thread? _monitor;
        private int _tripped, _disposed;
        private long _checks, _faults;
        private ForensicChainVerdict _tripVerdict;

        public ForensicChainDisarmInterlock(ZeroAllocForensicAuditStream stream, VolatileMemoryPurger? purger = null,
                                            Action<ForensicChainVerdict>? onTrip = null, params PoisonedSecretBuffer[] orderTokens)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _purger = purger;
            _onTrip = onTrip;
            _tokens = orderTokens ?? Array.Empty<PoisonedSecretBuffer>();
        }

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        /// <summary>★blockWhile — 체인 변조 확인 이후 무장 영구 차단.</summary>
        public bool ArmingBlocked => Tripped;
        public long Checks => Interlocked.Read(ref _checks);
        public long CallbackFaults => Interlocked.Read(ref _faults);
        public ForensicChainVerdict TripVerdict { get { lock (_verdictGate) return _tripVerdict; } }

        // [실배선 · 방향 B] 원자 인터락 집계기 푸시 결속(ArmingInterlockBitmask) — 상태 전이 직후 Resync(δ=0).
        private MS2E.Core.ArmingInterlockLink? _interlockLink;
        /// <summary>집계기에 이 엔진을 고정 비트로 결속 — 결속 즉시 현재 상태 동기화(이미 차단이면 비트 유지).</summary>
        public void BindInterlock(MS2E.Core.ArmingInterlockBitmask aggregator, MS2E.Core.ArmingInterlockSource bit)
        {
            ArgumentNullException.ThrowIfNull(aggregator);
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _interlockLink, link);
            link.Resync();   // Bind ~ 링크 가시화 사이의 전이 흡수
        }
        private void SignalInterlock() => Volatile.Read(ref _interlockLink)?.Resync();

        /// <summary>체인 검증 1회(δ=0). 결함이면 Trip 후 결함 판정 반환.</summary>
        public ForensicChainVerdict Check()
        {
            Interlocked.Increment(ref _checks);
            var v = _stream.VerifyDetailed();
            if (!v.Intact) Trip(v);
            return v;
        }

        private void Trip(in ForensicChainVerdict v)
        {
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return;
            SignalInterlock();   // 플래그 선기록 → 집계 비트 Raise
            lock (_verdictGate) _tripVerdict = v;
            foreach (var t in _tokens) { try { t.PurgeAndPoison(); } catch { Interlocked.Increment(ref _faults); } }
            try { _purger?.PurgeAll(); } catch { Interlocked.Increment(ref _faults); }
            try { _onTrip?.Invoke(v); } catch { Interlocked.Increment(ref _faults); }
        }

        public void StartMonitor(TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(ForensicChainDisarmInterlock));
            var t = new Thread(() =>
            {
                while (!_stop.Wait(interval))
                    if (Volatile.Read(ref _disposed) == 1 || Check().Intact == false) return;
            }) { IsBackground = true, Name = "MS2E.ForensicChainGuard" };
            if (Interlocked.CompareExchange(ref _monitor, t, null) is null) t.Start();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _stop.Set();
            var t = Volatile.Read(ref _monitor);
            if (t is not null && t.IsAlive && t != Thread.CurrentThread) t.Join();
            _stop.Dispose();
        }
    }
}
