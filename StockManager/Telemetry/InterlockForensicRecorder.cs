// ============================================================================
// InterlockForensicRecorder — [E2E 스트레스] 무장 집계어 비트 전이 → 포렌식 해시 체인 + MMF 블랙박스 즉시 각인
//
//  ArmingInterlockBitmask 는 비트가 실제로 바뀐 순간(0→1 / 1→0)에만 IInterlockTransitionSink 를 정확히 1회 부른다.
//  이 기록기는 그 전이를 (a) ZeroAllocForensicAuditStream 에 Disarm(raised 비트) / Arm(cleared 비트)으로 해시 체인 결속하고
//  (b) MmfFlightRecorder(프로세스 크래시 후에도 남는 MMF 블랙박스)에 InterlockRaised/InterlockCleared 로 남긴다.
//  ★수정 전 공백: 인터락 중 무엇이 언제 Disarm 을 격발했는지 체인에 남는 경로가 0 이었다(대사·봉인 이벤트만 각인).
//  δ=0: 체인 Append 는 경합 없는 Monitor + stackalloc, 블랙박스 Record 는 값 타입 — 관리 힙 0B(테스트 실측).
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Telemetry
{
    public sealed class InterlockForensicRecorder : IInterlockTransitionSink
    {
        private readonly ZeroAllocForensicAuditStream _stream;
        private readonly MmfFlightRecorder? _blackbox;
        private readonly Func<long> _nowTicks;
        private long _raisedEvents, _clearedEvents, _raisedUnion, _faults;

        public InterlockForensicRecorder(ZeroAllocForensicAuditStream stream, MmfFlightRecorder? blackbox = null, Func<long>? nowTicks = null)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _blackbox = blackbox;
            _nowTicks = nowTicks ?? (static () => DateTime.UtcNow.Ticks);
        }

        public long RaisedEvents => Interlocked.Read(ref _raisedEvents);
        public long ClearedEvents => Interlocked.Read(ref _clearedEvents);
        /// <summary>지금까지 raised 로 각인된 비트 합집합(진단).</summary>
        public long RaisedUnion => Interlocked.Read(ref _raisedUnion);
        public long Faults => Interlocked.Read(ref _faults);

        public void OnTransition(long raisedBits, long clearedBits, long maskAfter)
        {
            long now = _nowTicks();
            if (raisedBits != 0)
            {
                Interlocked.Increment(ref _raisedEvents);
                Interlocked.Or(ref _raisedUnion, raisedBits);
                Write(AuditEventKind.Disarm, FlightEventKind.InterlockRaised, raisedBits, maskAfter, now);
            }
            if (clearedBits != 0)
            {
                Interlocked.Increment(ref _clearedEvents);
                Write(AuditEventKind.Arm, FlightEventKind.InterlockCleared, clearedBits, maskAfter, now);
            }
        }

        private void Write(AuditEventKind audit, FlightEventKind flight, long bits, long maskAfter, long now)
        {
            try { _stream.Append(audit, bits, now); } catch (Exception) { Interlocked.Increment(ref _faults); }
            try { _blackbox?.Record(flight, 0, bits, maskAfter); } catch (Exception) { Interlocked.Increment(ref _faults); }
        }
    }
}
