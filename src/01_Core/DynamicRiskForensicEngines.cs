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
    public enum AuditEventKind : byte { Arm = 0, Disarm = 1, KillSwitch = 2, RiskViolation = 3, SealTamper = 4, Reconciliation = 5 }

    /// <summary>감사 링크 1건의 불변 뷰(포렌식 조회용 — 원본 배열 노출 없이 값타입 복사).</summary>
    public readonly record struct AuditLinkView(long Sequence, AuditEventKind Kind, long PayloadTag, long TimestampTicks, bool Live,
                                                long RecordedTicks = 0, bool Quarantined = false)
    {
        /// <summary>[MMF 하드닝] 이벤트(발주/리스크) 시각 → 체인 커밋 시각 지연(µs). 두 시각이 같은 UTC tick(100ns) 도메인일 때 유효
        /// (스트림 기본 기록 시계 = DateTime.UtcNow.Ticks = DynamicRiskAdapter 기본 이벤트 시계). 음수 = 이벤트가 커밋보다 늦게 찍힘(시계 스텝).</summary>
        public long CommitLatencyMicros => Live ? (RecordedTicks - TimestampTicks) / 10 : 0;
    }

    /// <summary>[MMF 하드닝] 체인 판정 사유(첫 결함 1건).</summary>
    public enum ForensicBreakKind : byte { None = 0, SlotSequenceMismatch = 1, GenesisAnchor = 2, LinkBroken = 3, HashMismatch = 4, RecordClockRegressed = 5 }

    /// <summary>[MMF 하드닝] 상세 검증 결과(값 타입 — 힙 0). FirstBrokenSequence = 결함이 드러난 첫 링크(-1 = 무결).</summary>
    public readonly record struct ForensicChainVerdict(bool Intact, long FirstBrokenSequence, ForensicBreakKind Reason, long VerifiedLinks);

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
        private readonly long[] _recorded;   // [MMF 하드닝] 기록(커밋) 시각 — 단조 클램프, 해시 입력에 결속
        private readonly Func<long> _recordClock;
        private readonly object _gate = new();   // [Phase 1] 단일 라이터 계약을 호출측 신뢰가 아닌 코드로 강제
        private long _writeSeq;              // 원자 단조 커서
        private long _lastRecorded;
        private long _clockRegressions;
        private long _forwardDated;
        private long _firstBroken = -1;      // 격리된 첫 결함 링크(sticky)

        private const int InputBytes = HashBytes + 1 + 8 + 8 + 8 + 8;   // Prev‖Kind‖Seq‖EventTs‖Payload‖RecordedTs

        public ZeroAllocForensicAuditStream(int capacityPow2) : this(capacityPow2, null) { }

        /// <param name="recordClock">[MMF 하드닝] 기록 시각 원천. null = DateTime.UtcNow.Ticks(100ns UTC) — 생산자(DynamicRiskAdapter)의
        /// 기본 이벤트 시계와 같은 도메인이라 이벤트→커밋 지연을 µs 로 사상할 수 있다(이전 기본값 Stopwatch.GetTimestamp 는 도메인 불일치로
        /// 사상 불가였다). 벽시계 역행(NTP 스텝)은 단조 클램프로 흡수하고 ClockRegressions 로 계수. 테스트는 결정론 시계 주입.</param>
        public ZeroAllocForensicAuditStream(int capacityPow2, Func<long>? recordClock)
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
            _recorded = new long[capacityPow2];
            _recordClock = recordClock ?? UtcTicks;
            _tip = new byte[HashBytes];   // 제네시스 = 전부 0x00(고정 시드)
        }

        public int Capacity => _capacity;
        public long WriteHead => Volatile.Read(ref _writeSeq);
        private static long UtcTicks() => DateTime.UtcNow.Ticks;
        /// <summary>이벤트 시각이 커밋 시각보다 미래로 찍힌 건수(시계 스텝·생산자 시계 불일치 관측 — 판정 실패로 보지는 않음: 오격발 Disarm 방지).</summary>
        public long ForwardDatedEvents => Interlocked.Read(ref _forwardDated);
        /// <summary>기록 시계가 뒤로 간 횟수(단조 클램프로 흡수 — 시계 조작/NTP 역행 관측치).</summary>
        public long ClockRegressions => Interlocked.Read(ref _clockRegressions);
        /// <summary>검증에서 결함이 확인돼 격리된 첫 링크(-1 = 없음 · sticky).</summary>
        public long QuarantinedSequence => Interlocked.Read(ref _firstBroken);
        public bool Compromised => QuarantinedSequence >= 0;

        /// <summary>
        /// 감사 이벤트 1건 체인 결속(★단일 라이터 계약·O(1)·δ=0). PrevHash(32)‖Kind(1)‖Seq(8)‖Timestamp(8)‖Payload(8)
        /// 을 SHA-256 해시해 새 팁을 확정 — 슬롯에 (PrevHash, Hash) 를 함께 각인해 이후 지역 연쇄 재검증을 가능케 한다.
        /// </summary>
        /// <remarks>[Phase 1 감사 교정] 이 스트림은 DI 싱글턴(앱 전역 공유)인데 Append 는 "단일 라이터 계약"을 주석으로만
        /// 요구했다 — 두 생산자가 동시에 들어오면 같은 w/슬롯을 쓰고 팁이 뒤엉켜 VerifyIntegrity 가 위변조로 오판(허위 감지)한다.
        /// Verify 도 랩 중인 가장 오래된 슬롯을 Append 가 덮어쓰는 도중 읽으면 같은 허위 감지가 난다. 현 배선은 Blazor 디스패처
        /// 단일 스레드라 도달하지 않았지만(잠재), 감사 이벤트는 저빈도라 lock 으로 직렬화해 계약을 코드로 강제한다
        /// (경합 없는 Monitor 는 관리 힙 할당 0 — δ=0 유지).</remarks>
        [ZeroAllocHotPath]
        public long Append(AuditEventKind kind, long payloadTag, long timestampTicks)
        {
            lock (_gate) return AppendLocked(kind, payloadTag, timestampTicks);
        }

        [ZeroAllocHotPath]
        private long AppendLocked(AuditEventKind kind, long payloadTag, long timestampTicks)
        {
            long w = Volatile.Read(ref _writeSeq) + 1;
            int idx = (int)((w - 1) & _mask);

            // [MMF 하드닝] 발주/이벤트 시각(호출측)과 기록 시각(체인 커밋)을 같은 락 안에서 원자적으로 사상 — 둘 다 해시에 결속되므로
            //   사후에 어느 한쪽만 고치는 위조(예: 발주 후 기록을 늦게 한 흔적 지우기)가 불가. 기록 시각은 단조 클램프(역행 계수).
            long rec = _recordClock();
            if (rec < _lastRecorded) { Interlocked.Increment(ref _clockRegressions); rec = _lastRecorded; }
            _lastRecorded = rec;
            if (timestampTicks > rec) Interlocked.Increment(ref _forwardDated);

            Span<byte> input = stackalloc byte[InputBytes];
            _tip.AsSpan(0, HashBytes).CopyTo(input);
            input[HashBytes] = (byte)kind;
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 1, 8), w);
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 9, 8), timestampTicks);
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 17, 8), payloadTag);
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 25, 8), rec);

            Span<byte> prevSlot = _prevHash.AsSpan(idx * HashBytes, HashBytes);
            _tip.AsSpan(0, HashBytes).CopyTo(prevSlot);                       // 이 슬롯이 체결한 PrevHash 보존

            Span<byte> hashSlot = _hash.AsSpan(idx * HashBytes, HashBytes);
            SHA256.HashData(input, hashSlot);                                  // 새 해시(δ=0 정적 Span 오버로드)
            hashSlot.CopyTo(_tip.AsSpan(0, HashBytes));                        // 팁 전진

            _kind[idx] = (long)kind; _payload[idx] = payloadTag; _timestamp[idx] = timestampTicks; _recorded[idx] = rec; _seq[idx] = w;
            Volatile.Write(ref _writeSeq, w);                                  // 발행(release)
            return w;
        }

        /// <summary>
        /// ★무결성 검증(δ=0 재계산). 살아있는 슬롯 각각에 대해 (a) SHA256(PrevHash‖필드)==Hash 재검, (b) 인접 슬롯과
        /// PrevHash 가 이전 슬롯 Hash 와 정합하는지 지역 연쇄 재검. 위변조(내용 변경·해시 위조·슬롯 재배열) 시 false.
        /// </summary>
        /// <remarks>[Phase 1 감사 교정] ① 이전엔 호출마다 32B 배열을 할당했다("δ=0 재계산" 주석과 불일치 — UI 갱신마다 호출됨)
        /// → stackalloc. ② 링이 아직 랩되지 않았으면 첫 링크의 PrevHash 는 반드시 제네시스(0x00)여야 하는데 검사하지 않아,
        /// 첫 링크를 해시까지 재계산해 "일관 위조"하면 통과했다 → 제네시스 앵커 검사 추가.
        /// ★정직 한계: 무키(unkeyed) SHA-256 체인이라, 살아있는 창 전체를 처음부터 일관되게 재계산하는 공격자(랩 이후 구간)는
        /// 탐지하지 못한다 — 진정한 변조 방지는 HMAC 키 또는 외부(파일/원격) 팁 앵커링이 필요하다.</remarks>
        public bool VerifyIntegrity() => VerifyDetailed().Intact;

        /// <summary>[MMF 하드닝] 상세 검증(δ=0) — 첫 결함 링크와 사유를 특정하고, 결함 링크는 격리(sticky: ReadLink Quarantined·Live=false).</summary>
        public ForensicChainVerdict VerifyDetailed()
        {
            lock (_gate)
            {
                var v = VerifyLocked();
                if (!v.Intact && v.FirstBrokenSequence > 0) Interlocked.CompareExchange(ref _firstBroken, v.FirstBrokenSequence, -1);
                return v;
            }
        }

        [ZeroAllocHotPath]
        private ForensicChainVerdict VerifyLocked()
        {
            long head = Volatile.Read(ref _writeSeq);
            long floor = head - _capacity; if (floor < 0) floor = 0;
            Span<byte> recomputed = stackalloc byte[HashBytes];
            Span<byte> input = stackalloc byte[InputBytes];
            Span<byte> prevLiveHash = stackalloc byte[HashBytes];
            bool havePrev = false;
            long prevRecorded = long.MinValue, verified = 0;

            for (long s = floor + 1; s <= head; s++)
            {
                int idx = (int)((s - 1) & _mask);
                if (_seq[idx] != s) return new(false, s, ForensicBreakKind.SlotSequenceMismatch, verified);   // 랩/미기록 슬롯 → 안전측

                ReadOnlySpan<byte> prevSlot = _prevHash.AsSpan(idx * HashBytes, HashBytes);
                ReadOnlySpan<byte> hashSlot = _hash.AsSpan(idx * HashBytes, HashBytes);

                if (s == 1 && prevSlot.IndexOfAnyExcept((byte)0) >= 0)
                    return new(false, s, ForensicBreakKind.GenesisAnchor, verified);   // ★제네시스 앵커
                if (havePrev && !prevSlot.SequenceEqual(prevLiveHash))
                    return new(false, s, ForensicBreakKind.LinkBroken, verified);      // ★지역 연쇄 단절(재배열/삽입/삭제)
                if (_recorded[idx] < prevRecorded)
                    return new(false, s, ForensicBreakKind.RecordClockRegressed, verified);   // ★기록 시각 역행(단조 클램프 위반 = 위조)

                prevSlot.CopyTo(input.Slice(0, HashBytes));
                input[HashBytes] = (byte)_kind[idx];
                BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 1, 8), _seq[idx]);
                BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 9, 8), _timestamp[idx]);
                BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 17, 8), _payload[idx]);
                BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 25, 8), _recorded[idx]);
                SHA256.HashData(input, recomputed);
                if (!recomputed.SequenceEqual(hashSlot)) return new(false, s, ForensicBreakKind.HashMismatch, verified);   // ★내용/해시 위조

                hashSlot.CopyTo(prevLiveHash);
                havePrev = true;
                prevRecorded = _recorded[idx];
                verified++;
            }
            return new(true, -1, ForensicBreakKind.None, verified);
        }

        /// <summary>포렌식 조회(δ=0 아님 — 값타입 복사·저빈도 호출 전용). 시퀀스가 창 밖이면 Live=false.</summary>
        public AuditLinkView ReadLink(long sequence)
        {
            lock (_gate) return ReadLinkLocked(sequence);
        }

        private AuditLinkView ReadLinkLocked(long sequence)
        {
            long head = Volatile.Read(ref _writeSeq);
            long floor = head - _capacity; if (floor < 0) floor = 0;
            if (sequence <= floor || sequence > head) return new AuditLinkView(sequence, default, 0, 0, false);
            int idx = (int)((sequence - 1) & _mask);
            if (_seq[idx] != sequence) return new AuditLinkView(sequence, default, 0, 0, false);
            bool quarantined = sequence == Interlocked.Read(ref _firstBroken);   // [MMF 하드닝] 결함 링크는 신뢰 표시 차단
            return new AuditLinkView(sequence, (AuditEventKind)_kind[idx], _payload[idx], _timestamp[idx], !quarantined, _recorded[idx], quarantined);
        }

        /// <summary>★테스트 전용 위변조 주입 seam(InternalsVisibleTo(StockManager.Tests)). 프로덕션 경로에서 도달 불가.</summary>
        internal void __AdversarialCorruptForTest(long sequence, int byteOffset)
        {
            int idx = (int)((sequence - 1) & _mask);
            _hash[idx * HashBytes + (byteOffset & (HashBytes - 1))] ^= 0xFF;
        }

        /// <summary>★테스트 전용 — 첫 링크(seq=1)를 "일관되게" 위조: PrevHash 를 제네시스가 아닌 값으로 바꾸고 해시까지
        /// 재계산해 자기 정합을 맞춘다(해시 재계산만으로는 잡히지 않는 공격자 모델). 제네시스 앵커 검사만이 탐지한다.</summary>
        internal void __AdversarialForgeFirstLinkForTest()
        {
            const int idx = 0;
            Span<byte> prevSlot = _prevHash.AsSpan(idx * HashBytes, HashBytes);
            prevSlot.Fill(0xAB);
            Span<byte> input = stackalloc byte[InputBytes];
            prevSlot.CopyTo(input);
            input[HashBytes] = (byte)_kind[idx];
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 1, 8), _seq[idx]);
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 9, 8), _timestamp[idx]);
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 17, 8), _payload[idx]);
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashBytes + 25, 8), _recorded[idx]);
            SHA256.HashData(input, _hash.AsSpan(idx * HashBytes, HashBytes));
        }

        /// <summary>★테스트 전용 — 링크의 기록 시각만 과거로 되돌림(해시 재계산 없음 → HashMismatch 또는 RecordClockRegressed 로 탐지).</summary>
        internal void __AdversarialRewindRecordedForTest(long sequence, long delta)
        {
            int idx = (int)((sequence - 1) & _mask);
            _recorded[idx] -= delta;
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
        [ZeroAllocHotPath]
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
