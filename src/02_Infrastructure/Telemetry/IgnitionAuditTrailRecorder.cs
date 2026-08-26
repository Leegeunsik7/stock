// ============================================================================
// IgnitionAuditTrailRecorder — v10.0.0 점화 포렌식 감사 기록기 (Gemini 보강 과제)
//
//  무장 상태 변경·점화 검증 시도·실물 사출·펄스 단선 전체를 무할당(δ=0) 락프리 링버퍼에 마이크로초 타임스탬프와
//  FNV-1a-64 포렌식 해시로 기록한다. 실자본 집행의 모든 안전 판정을 사후 재구성·감사할 수 있는 비행 기록계.
//
//  ★MmfFlightRecorder(v4.95.0) 의 검증된 δ=0 멀티라이터 tri-state 링을 미러링(재발명 아님): 원자 인덱스 청구
//    (_writeSeq) + 랩 가드(lapped 라이터 드롭) + tri-state 슬롯(Empty/Writing/Ready — 부분 쓰기 격리) + _inFlight
//    드레인(봉인 후 일관 스냅샷). 각 64B 레코드에 FNV-1a-64 무결성 해시 각인(변조/torn 탐지·재검 가능).
//
//  ★불변: δ=0(Record 핫패스 힙 0·Interlocked·값타입 레코드·해시 순수) · 결정론(주입 시계·FNV 순수·µs 변환 Int128
//    오버플로 안전) · liveArmed:false(순수 관측/기록 — IBroker·소켓·onSend 참조 0) · fail-closed(봉인 시 드롭·랩 가드).
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>점화 감사 이벤트 종류.</summary>
    public enum IgnitionEventKind : long
    {
        ArmingChanged    = 1,   // 무장 술어 상태 변경
        IgnitionAttempt  = 2,   // 점화 검증 시도
        IgnitionOpened   = 3,   // 1회성 emission 개통(단발)
        Veto             = 4,   // 점화 거부(방어선/키 실패)
        Emission         = 5,   // 봉인 실물 사출
        KeyPulseLost     = 6,   // 하드웨어 키 펄스 단선
        KeyPulseRestored = 7,   // 하드웨어 키 펄스 복원
        Freeze           = 8,   // 비상 동결
        Certification    = 9,   // 전방위 인증 봉인(v10.1.0)
        DryRun           = 10,  // 드라이런 가상 체결(v10.1.0)
        ReleaseAttestation = 11, // 릴리스 아티팩트 SHA-256 포렌식 봉인(v10.1.0 Final Packaging)
        SlaBreach        = 12,  // 나노 SLA P99.99 이탈 트립(v10.1.0 Final Packaging)
        Signoff          = 13,  // 사전 점화 서명(호스트/OS/금고 채점·v10.1.0 Ops)
        HostDrift        = 14,  // 호스트 환경 드리프트 포획 → 안전 강등(v10.1.0 Ops)
        CanaryDowngrade  = 15,  // 카나리 슬리피지 임계 초과 → 드라이런 강등(v10.1.0 Ops)
        PanicAbort       = 16,  // 운용자 비상 탈출(패닉 스위치) 격발(v10.1.0 Ops)
        AdminCommand     = 17,  // Admin 권한 명령 실행(v10.1.0 Phase 93-B)
    }

    /// <summary>점화 감사 레코드(64B blittable·δ=0). µs 타임스탬프 + 포렌식 해시.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct IgnitionAuditRecord
    {
        public readonly long Sequence;        // [0]  전역 단조 청구 시퀀스(1-based·랩 판별)
        public readonly long TimestampMicros; // [8]  주입 시각(µs)
        public readonly long EventCode;       // [16] IgnitionEventKind
        public readonly long Detail0;         // [24] veto 마스크 / orderId 등
        public readonly long Detail1;         // [32]
        public readonly long ForensicHash;    // [40] FNV-1a-64 무결성 봉인
        public readonly int  ThreadId;        // [48]
        public readonly int  Reserved0;       // [52]
        public readonly long Reserved1;       // [56] → 64B
        public IgnitionAuditRecord(long seq, long tsMicros, long eventCode, long d0, long d1, long hash, int threadId)
        { Sequence = seq; TimestampMicros = tsMicros; EventCode = eventCode; Detail0 = d0; Detail1 = d1; ForensicHash = hash; ThreadId = threadId; Reserved0 = 0; Reserved1 = 0; }
    }

    /// <summary>점화 포렌식 감사 기록기(δ=0·락프리 tri-state 링). Record(kind,detail)→µs+해시 각인·SnapshotRecent·VerifyIntegrity.</summary>
    public sealed class IgnitionAuditTrailRecorder
    {
        private const int Empty = 0, Writing = 1, Ready = 2;
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime  = 1099511628211UL;

        private readonly IgnitionAuditRecord[] _slots;
        private readonly int[] _state;
        private readonly long _mask;
        private readonly int  _capacity;
        private readonly Func<long> _nowTicks;
        private readonly long _freq;
        private long _writeSeq, _totalRecorded, _droppedLapped, _droppedSealed, _inFlight, _lastEventCode;
        private int  _sealed;

        public IgnitionAuditTrailRecorder(int capacity = 65536, Func<long>? nowTicks = null, long tickFrequency = 0)
        {
            int cap = NextPow2(capacity < 16 ? 16 : capacity);
            _capacity = cap; _mask = cap - 1;
            _slots = new IgnitionAuditRecord[cap];
            _state = new int[cap];
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;   // 감사 타임스탬프는 실 벽시계(프로덕션)·테스트 주입
            // ★자체-시계(Stopwatch.GetTimestamp) 소스와 매칭: freq 폴백을 Stopwatch.Frequency 로 — 비-1e7 QPC 호스트에서 µs 왜곡 봉쇄.
            _freq = tickFrequency > 0 ? tickFrequency : Stopwatch.Frequency;
        }

        // ── 읽기(δ=0·락프리) ──
        public long TotalRecorded => Interlocked.Read(ref _totalRecorded);
        public long RecordedCount => Math.Min(Interlocked.Read(ref _totalRecorded), _capacity);
        public long DroppedLapped => Interlocked.Read(ref _droppedLapped);
        public long WriteHead     => Interlocked.Read(ref _writeSeq);
        public long LastEventCode => Interlocked.Read(ref _lastEventCode);
        public bool IsSealed      => Volatile.Read(ref _sealed) != 0;
        public int  Capacity      => _capacity;

        /// <summary>감사 이벤트 기록(δ=0·멀티라이터). µs 타임스탬프 + FNV-1a-64 포렌식 해시 각인. 봉인 후엔 드롭.</summary>
        public void Record(IgnitionEventKind kind, long detail0, long detail1 = 0L)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                if (Volatile.Read(ref _sealed) != 0) { Interlocked.Increment(ref _droppedSealed); return; }
                long seq = Interlocked.Increment(ref _writeSeq);                     // 원자 단조 청구(1-based)
                if (Interlocked.Read(ref _writeSeq) - seq >= _capacity)              // 랩 가드: 뒤처진 라이터 드롭
                { Interlocked.Increment(ref _droppedLapped); return; }
                int idx = (int)((seq - 1) & _mask);
                long micros = ToMicros(_nowTicks());
                long hash = ComputeHash(seq, micros, (long)kind, detail0, detail1);
                Volatile.Write(ref _state[idx], Writing);                            // 무효화(스냅샷 격리)
                _slots[idx] = new IgnitionAuditRecord(seq, micros, (long)kind, detail0, detail1, hash, Environment.CurrentManagedThreadId);
                Volatile.Write(ref _state[idx], Ready);                              // 커밋(release)
                Interlocked.Increment(ref _totalRecorded);
                Volatile.Write(ref _lastEventCode, (long)kind);
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        /// <summary>봉인(δ=0·비가역 인덱스 락). 동결 옵저버에서 호출 — 이후 기록 드롭·스냅샷 일관성 확보.</summary>
        public void Seal() => Volatile.Write(ref _sealed, 1);

        /// <summary>최신순 스냅샷(δ=0·호출자 span). tri-state Ready + Sequence 재검으로 wrap-torn 격리.</summary>
        public int SnapshotRecent(Span<IgnitionAuditRecord> dest)
        {
            if (dest.Length == 0) return 0;
            long head = Interlocked.Read(ref _writeSeq);
            long floor = head - _capacity; if (floor < 0) floor = 0;
            int n = 0;
            for (long s = head; s > floor && n < dest.Length; s--)
            {
                int idx = (int)((s - 1) & _mask);
                if (Volatile.Read(ref _state[idx]) != Ready) continue;
                IgnitionAuditRecord rec = _slots[idx];
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _state[idx]) == Ready && rec.Sequence == s && _slots[idx].Sequence == s)
                    dest[n++] = rec;   // 라이브·복사본 Sequence 동시 일치만 채택(torn 격리)
            }
            return n;
        }

        /// <summary>무결성 검증(δ=0). 기록된 각 Ready 슬롯의 FNV 해시를 재계산·대조 — 변조/torn 시 false.</summary>
        public bool VerifyIntegrity()
        {
            long head = Interlocked.Read(ref _writeSeq);
            long floor = head - _capacity; if (floor < 0) floor = 0;
            for (long s = head; s > floor; s--)
            {
                int idx = (int)((s - 1) & _mask);
                if (Volatile.Read(ref _state[idx]) != Ready) continue;
                IgnitionAuditRecord rec = _slots[idx];
                Interlocked.MemoryBarrier();
                // ★사후 재검(SnapshotRecent 와 동일 격리): 동시 Record 로 랩·torn 된 슬롯은 스킵(변조 아님) — spurious false 방지.
                if (Volatile.Read(ref _state[idx]) != Ready || rec.Sequence != s || _slots[idx].Sequence != s) continue;
                long expect = ComputeHash(rec.Sequence, rec.TimestampMicros, rec.EventCode, rec.Detail0, rec.Detail1);
                if (expect != rec.ForensicHash) return false;
            }
            return true;
        }

        private long ToMicros(long ticks)
            => ticks <= 0 ? 0L : (long)((System.Int128)ticks * 1_000_000 / _freq);   // Int128 오버플로 안전

        private static long ComputeHash(long seq, long micros, long kind, long d0, long d1)
        {
            unchecked
            {
                ulong h = FnvOffset;
                h = (h ^ (ulong)seq)    * FnvPrime;
                h = (h ^ (ulong)micros) * FnvPrime;
                h = (h ^ (ulong)kind)   * FnvPrime;
                h = (h ^ (ulong)d0)     * FnvPrime;
                h = (h ^ (ulong)d1)     * FnvPrime;
                return (long)h;
            }
        }

        private static int NextPow2(int n) { int p = 1; while (p < n) p <<= 1; return p; }
    }
}
