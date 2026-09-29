// ============================================================================
// ZeroAllocForensicAuditStream + StreamingParametricVarCalculator — v12.2.0 Think Beyond Gemini
//
//   위험 가정(제미나이의 '단순 결속' 이 다루지 않는 프로덕션 리스크/포렌식 파멸 위험):
//     ① "감사 로그가 사후 조작 가능" — 단순 리스트/DB 레코드는 운영자 권한 탈취 시 과거 Arm/Disarm/킬스위치 이력을
//        조용히 편집·삭제할 수 있다. 개별 레코드 해시(FNV 등)만으로는 '레코드 자체를 통째로 교체'하는 위변조를
//        걸러내지 못한다 — 이전 레코드와의 연쇄(chain) 없이는 순서 조작·삽입·삭제가 탐지 불가.
//     ② "포트폴리오 낙폭 판정이 순간값(snapshot)에만 의존" — 단일 시점 손익률만 보면 변동성 자체가 커지는 초입
//        (스파이크 전조)을 놓친다. 실시간 VaR(Value at Risk) 없이는 '아직 임계선을 넘지 않았지만 분산이 폭증 중인'
//        상태를 서킷 브레이커가 선제 방어할 수 없다.
//
//   ── Think Beyond (이중 엔진) ─────────────────────────────────────────────
//     ① Zero-Alloc Ring-Buffered Forensic Audit Stream(Idea A) — 사전 할당 고정 링(Pinned Object Heap)에 SHA-256
//        해시 체인을 O(1) 마이크로초 단위로 갱신. 각 슬롯이 '자신이 체결된 PrevHash'를 함께 보존해, 링이 랩(wrap)
//        되어도 살아있는 구간만으로 지역 연쇄(adjacent-link) 무결성을 재검증할 수 있다 — 전체 역사 재생 불필요.
//        stackalloc 입력 버퍼 + SHA256.HashData 정적 Span 오버로드로 힙 0.
//     ② Real-Time Streaming Parametric VaR Circuit Breaker(Idea B) — 수익률 표본(bps)을 사전 할당 링 + 러닝섬/러닝
//        제곱합으로 O(1) 분산 산출 → 정수 뉴턴법 제곱근으로 표준편차 → z-score 곱으로 Parametric VaR(bps) 산출.
//        분산이 급등하면(임계 미초과라도) VaR 자체가 즉시 반영되어 상위 인터록의 선제 Disarm 트리거로 소비된다.
//
//   불변: 결정론(주입 tick·순수 정수/스팬 연산) · δ=0(Append/Push/VarBps 핫패스 힙 0) · fail-closed(위변조 →
//        VerifyIntegrity=false → 상위가 부팅/무장 파이프라인 중단) · 락프리(Interlocked/Volatile).
//   ※ MS2E.Core(최내부·순수) — 상위(MS2E.Infrastructure)가 DynamicRiskInterlock 으로 두 엔진을 결선한다.
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading;

namespace MS2E.Core
{
    /// <summary>감사 체인 이벤트 종류 — 무장/해제/킬스위치/리스크 위반.</summary>
    public enum AuditEventKind : byte { Arm = 0, Disarm = 1, KillSwitch = 2, RiskViolation = 3 }

    /// <summary>감사 링크 1건의 불변 뷰(포렌식 조회용 — 원본 배열 노출 없이 값타입 복사).</summary>
    public readonly record struct AuditLinkView(long Sequence, AuditEventKind Kind, long PayloadTag, long TimestampTicks, bool Live);

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① (Idea A) — Zero-Alloc Ring-Buffered Forensic Audit Stream
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// 무장/해제/킬스위치/리스크위반 이벤트를 SHA-256 해시 체인(Append-Only)으로 기록하는 무할당 포렌식 스트림.
    /// 슬롯마다 자신이 체결된 PrevHash 를 함께 보존 — 랩된 링에서도 '살아있는 구간'만으로 지역 연쇄 무결성을 재검증.
    /// </summary>
    public sealed class ZeroAllocForensicAuditStream
    {
        private const int HashBytes = 32;

        private readonly byte[] _hash;       // Pinned: capacity*32 — 각 슬롯의 확정 해시
        private readonly byte[] _prevHash;   // Pinned: capacity*32 — 각 슬롯이 체결 당시 참조한 PrevHash
        private readonly long[] _kind;
        private readonly long[] _payload;
        private readonly long[] _timestamp;
        private readonly long[] _seq;        // 슬롯에 실제 적힌 시퀀스(1-based) — 랩 판별
        private readonly byte[] _tip;        // 현재 체인 팁(비-pinned, 짧은 스칼라 상태)
        private readonly int _capacity, _mask;
        private long _writeSeq;              // 원자 단조 커서

        public ZeroAllocForensicAuditStream(int capacityPow2)
        {
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0)
                throw new ArgumentException("용량은 2의 거듭제곱(≥2)이어야 합니다.", nameof(capacityPow2));
            _capacity = capacityPow2; _mask = capacityPow2 - 1;
            _hash = GC.AllocateArray<byte>(capacityPow2 * HashBytes, pinned: true);
            _prevHash = GC.AllocateArray<byte>(capacityPow2 * HashBytes, pinned: true);
            _kind = new long[capacityPow2];
            _payload = new long[capacityPow2];
            _timestamp = new long[capacityPow2];
            _seq = new long[capacityPow2];
            _tip = new byte[HashBytes];   // 제네시스 = 전부 0x00(고정 시드)
        }

        public int Capacity => _capacity;
        public long WriteHead => Volatile.Read(ref _writeSeq);

        /// <summary>
        /// 감사 이벤트 1건 체인 결속(★단일 라이터 계약·O(1)·δ=0). PrevHash(32)‖Kind(1)‖Seq(8)‖Timestamp(8)‖Payload(8)
        /// 을 SHA-256 해시해 새 팁을 확정 — 슬롯에 (PrevHash, Hash) 를 함께 각인해 이후 지역 연쇄 재검증을 가능케 한다.
        /// </summary>
        public long Append(AuditEventKind kind, long payloadTag, long timestampTicks)
        {
            long w = Volatile.Read(ref _writeSeq) + 1;
            int idx = (int)((w - 1) & _mask);

            Span<byte> input = stackalloc byte[HashBytes + 1 + 8 + 8 + 8];
            _tip.AsSpan(0, HashBytes).CopyTo(input);
            input[HashBytes] = (byte)kind;
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 1, 8), w);
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 9, 8), timestampTicks);
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 17, 8), payloadTag);

            Span<byte> prevSlot = _prevHash.AsSpan(idx * HashBytes, HashBytes);
            _tip.AsSpan(0, HashBytes).CopyTo(prevSlot);                       // 이 슬롯이 체결한 PrevHash 보존

            Span<byte> hashSlot = _hash.AsSpan(idx * HashBytes, HashBytes);
            SHA256.HashData(input, hashSlot);                                  // 새 해시(δ=0 정적 Span 오버로드)
            hashSlot.CopyTo(_tip.AsSpan(0, HashBytes));                        // 팁 전진

            _kind[idx] = (long)kind; _payload[idx] = payloadTag; _timestamp[idx] = timestampTicks; _seq[idx] = w;
            Volatile.Write(ref _writeSeq, w);                                  // 발행(release)
            return w;
        }

        /// <summary>
        /// ★무결성 검증(δ=0 재계산). 살아있는 슬롯 각각에 대해 (a) SHA256(PrevHash‖필드)==Hash 재검, (b) 인접 슬롯과
        /// PrevHash 가 이전 슬롯 Hash 와 정합하는지 지역 연쇄 재검. 위변조(내용 변경·해시 위조·슬롯 재배열) 시 false.
        /// </summary>
        public bool VerifyIntegrity()
        {
            long head = Volatile.Read(ref _writeSeq);
            long floor = head - _capacity; if (floor < 0) floor = 0;
            Span<byte> recomputed = stackalloc byte[HashBytes];
            Span<byte> input = stackalloc byte[HashBytes + 1 + 8 + 8 + 8];

            byte[]? prevLiveHash = null;
            for (long s = floor + 1; s <= head; s++)
            {
                int idx = (int)((s - 1) & _mask);
                if (_seq[idx] != s) return false;   // 랩/미기록 슬롯이 창 안에 있으면 안 됨(스캔 범위 오류) → 안전측 false

                ReadOnlySpan<byte> prevSlot = _prevHash.AsSpan(idx * HashBytes, HashBytes);
                ReadOnlySpan<byte> hashSlot = _hash.AsSpan(idx * HashBytes, HashBytes);

                if (prevLiveHash is not null && !prevSlot.SequenceEqual(prevLiveHash))
                    return false;                    // ★지역 연쇄 단절(재배열/삽입/삭제 탐지)

                prevSlot.CopyTo(input.Slice(0, HashBytes));
                input[HashBytes] = (byte)_kind[idx];
                BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 1, 8), _seq[idx]);
                BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 9, 8), _timestamp[idx]);
                BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 17, 8), _payload[idx]);
                SHA256.HashData(input, recomputed);
                if (!recomputed.SequenceEqual(hashSlot)) return false;   // ★내용/해시 위조 탐지

                prevLiveHash ??= new byte[HashBytes];
                hashSlot.CopyTo(prevLiveHash);
            }
            return true;
        }

        /// <summary>포렌식 조회(δ=0 아님 — 값타입 복사·저빈도 호출 전용). 시퀀스가 창 밖이면 Live=false.</summary>
        public AuditLinkView ReadLink(long sequence)
        {
            long head = Volatile.Read(ref _writeSeq);
            long floor = head - _capacity; if (floor < 0) floor = 0;
            if (sequence <= floor || sequence > head) return new AuditLinkView(sequence, default, 0, 0, false);
            int idx = (int)((sequence - 1) & _mask);
            if (_seq[idx] != sequence) return new AuditLinkView(sequence, default, 0, 0, false);
            return new AuditLinkView(sequence, (AuditEventKind)_kind[idx], _payload[idx], _timestamp[idx], true);
        }

        /// <summary>★테스트 전용 위변조 주입 seam(InternalsVisibleTo(StockManager.Tests)). 프로덕션 경로에서 도달 불가.</summary>
        internal void __AdversarialCorruptForTest(long sequence, int byteOffset)
        {
            int idx = (int)((sequence - 1) & _mask);
            _hash[idx * HashBytes + (byteOffset & (HashBytes - 1))] ^= 0xFF;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② (Idea B) — Real-Time Streaming Parametric VaR Circuit Breaker
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// 수익률 표본(bps)의 러닝섬/러닝제곱합으로 O(1)·δ=0 분산을 산출하고, 정수 뉴턴법 제곱근으로 표준편차를 구해
    /// Parametric VaR(bps) = zScoreMilli(‰) × stdDevBps / 1000 을 계산한다. 단일 라이터(Push) 계약.
    /// </summary>
    public sealed class StreamingParametricVarCalculator
    {
        private readonly long[] _ret;   // 수익률 표본(bps) 링
        private readonly int _cap, _mask;
        private long _writeCursor, _count;
        private long _sum, _sumSq;

        public StreamingParametricVarCalculator(int windowPow2)
        {
            if (windowPow2 < 2 || (windowPow2 & (windowPow2 - 1)) != 0)
                throw new ArgumentException("윈도우는 2의 거듭제곱(≥2)이어야 합니다.", nameof(windowPow2));
            _ret = new long[windowPow2];
            _mask = windowPow2 - 1;
            _cap = windowPow2;
        }

        public long Count => Volatile.Read(ref _count);

        /// <summary>수익률 표본 1건(bps, 부호 있음) 반영(★단일 라이터·O(1)·δ=0).</summary>
        public void Push(long returnBps)
        {
            long w = _writeCursor;
            int idx = (int)(w & _mask);
            if (Volatile.Read(ref _count) == _cap)
            {
                long evicted = _ret[idx];
                _sum -= evicted;
                _sumSq -= evicted * evicted;
            }
            else Volatile.Write(ref _count, Volatile.Read(ref _count) + 1);

            _ret[idx] = returnBps;
            _sum += returnBps;
            _sumSq += returnBps * returnBps;
            Volatile.Write(ref _writeCursor, w + 1);
        }

        public long MeanBps { get { long c = Volatile.Read(ref _count); return c > 0 ? _sum / c : 0; } }

        /// <summary>모집단 분산(bps²) — 러닝섬/러닝제곱합 O(1).</summary>
        public long VarianceBpsSq
        {
            get
            {
                long c = Volatile.Read(ref _count);
                if (c == 0) return 0;
                long mean = _sum / c;
                long v = _sumSq / c - mean * mean;
                return v > 0 ? v : 0;   // 정수 절단 보정(음수 방지)
            }
        }

        public long StdDevBps => IntegerSqrt(VarianceBpsSq);

        /// <summary>Parametric VaR(bps) = zScoreMilli(‰ 스케일, 예: 1645=z1.645) × StdDevBps / 1000.</summary>
        public long VarBps(long zScoreMilli) => zScoreMilli * StdDevBps / 1000;

        /// <summary>VaR 이 임계(bps) 초과 — 분산 급등 선제 감지(순간 손실률이 아직 임계 미달이어도 트립 가능).</summary>
        public bool Breach(long zScoreMilli, long thresholdBps) => VarBps(zScoreMilli) > thresholdBps;

        public void Reset()
        {
            Array.Clear(_ret);
            Volatile.Write(ref _writeCursor, 0); Volatile.Write(ref _count, 0);
            _sum = 0; _sumSq = 0;
        }

        /// <summary>정수 뉴턴법 제곱근(δ=0·결정론). 음수/0 은 0.</summary>
        private static long IntegerSqrt(long value)
        {
            if (value <= 0) return 0;
            long x = value, y = (x + 1) / 2;
            while (y < x) { x = y; y = (x + value / x) / 2; }
            return x;
        }
    }
}
