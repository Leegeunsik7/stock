// ============================================================================
// CausalAuditVault — v7.0.0 Phase 7: 암호학적 실행 감사 금고(Append-Only SHA-256 해시체인 원장)
//
//  CausalWeaveClock 타임스탬프·QPC 오프셋·링 방출(Eviction) 지표를 수정 불가능한 해시체인에 Append-Only 기록:
//      CurrentHash = SHA256( PreviousHash ‖ QPC ‖ Evictions ‖ SeqOffset )   (56B 입력 → 32B 출력)
//  각 레코드는 직전 헤드 해시를 삼켜 사슬을 이룬다 — 중간 레코드 1비트 변조 시 이후 전 해시가 불일치(변조 증명).
//
//  ── ★불변 제약 준수 ──────────────────────────────────────────────────────
//   · δ=0 기록 루프 — 사전할당 _input(56)/_scratch(32)/_current(32) + Span + SHA256.HashData(무할당 정적) 재사용.
//     new/box/람다캡처/LINQ 0. 검증(Verify)도 별도 사전할당 _verifyInput/_verifyRunning 로 δ=0(라이브 버퍼 무간섭).
//   · Fail-Closed 트립 봉인 — QPC 역행(EmergencyFreeze) 시 SealTrip 이 포렌식(QPC Δ·시퀀스 오프셋·버퍼 상태)을
//     특수 레코드로 사슬에 영구 봉인하고 _sealed 를 1회 래치(Interlocked) → 이후 모든 TryAppend 비가역 거부.
//     자동복구 경로가 '존재하지 않는다'(재시작 전까지 영구 봉인).
//   · Append-Only 무-wrap — 용량 소진 시 조용히 덮어쓰지 않고 false 반환(유실 없음·정직). 프로덕션은 디스크 영속.
//   · liveArmed:false — 순수 관측/기록. IBroker/소켓/onSend/PublishRaw 참조 0. unsafe 미사용(관리형 배킹).
//     CausalWeaveClock 회로는 '읽기만' 한다(수정 0줄) — 봉인은 외부 관측(FuseTripped) 후 이 금고가 자체 래치.
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>원장 무결성 상태(텔레메트리/UI). </summary>
    public enum LedgerIntegrity { Unverified = 0, Intact = 1, Corrupted = 2 }

    /// <summary>Append-Only SHA-256 해시체인 감사 금고(단일 라이터·δ=0 기록·비가역 트립 봉인).</summary>
    public sealed class CausalAuditVault
    {
        private const int  HashLen = 32;
        private const int  RecLen  = HashLen + 8 + 8 + 8;                     // prevHash|qpc|evict|seq = 56
        private const long SealMagic = unchecked((long)0xDEAD5EA15EA10000UL); // 트립 봉인 레코드 QPC 마커

        private readonly int    _capacity;
        private readonly byte[] _input        = new byte[RecLen];   // 사전할당 해시 입력(δ=0)
        private readonly byte[] _scratch      = new byte[HashLen];  // 사전할당 해시 출력(δ=0)
        private readonly byte[] _current      = new byte[HashLen];  // 롤링 헤드 해시
        private readonly byte[] _genesis      = new byte[HashLen];  // 제네시스 앵커
        private readonly byte[] _verifyInput  = new byte[RecLen];   // 검증 전용 입력(라이브 무간섭·δ=0)
        private readonly byte[] _verifyRunning= new byte[HashLen];  // 검증 전용 러닝 해시
        private readonly byte[] _hashes;                            // flattened cap*32 — 레코드별 헤드 해시
        private readonly long[] _qpc;
        private readonly long[] _evict;
        private readonly long[] _seq;
        private readonly long   _freq;
        private readonly long   _verifyIntervalTicks;

        private long _count;
        private int  _sealed;                                        // 0/1 — 1회 래치(비가역)
        private long _lastIntegrityCode = (long)LedgerIntegrity.Intact;   // 빈 원장(제네시스만)=무결
        private long _lastVerifyTick;
        private long _headHash8;                                     // 헤드 해시 상위 8B(δ=0 포렌식 표시)
        private long _sealQpcDelta, _sealSeqOffset, _sealBufferState, _sealAtRecord;

        public CausalAuditVault(int capacity = 65536, Func<long>? nowTicks = null, long verifyIntervalMillis = 2000)
        {
            if (capacity < 8) capacity = 8;
            _capacity = capacity;
            _freq = Stopwatch.Frequency;
            _verifyIntervalTicks = _freq * Math.Max(1L, verifyIntervalMillis) / 1000L;
            long total = (long)capacity * HashLen;
            _hashes = new byte[total <= int.MaxValue ? capacity * HashLen : throw new ArgumentException("금고 용량 초과", nameof(capacity))];
            _qpc = new long[capacity]; _evict = new long[capacity]; _seq = new long[capacity];
            for (int i = 0; i < HashLen; i++) _genesis[i] = (byte)(0x5A ^ (i * 0x1B));   // 결정론 제네시스 앵커
            _genesis.CopyTo(_current, 0);
            _headHash8 = BinaryPrimitives.ReadInt64LittleEndian(_current);
            _ = nowTicks;   // (콜드 시계 주입 여지 — 현재 throttle 은 호출자 now 인자 사용)
        }

        public long Count           => Interlocked.Read(ref _count);
        public int  Capacity        => _capacity;
        public bool Sealed          => Volatile.Read(ref _sealed) == 1;
        public bool Full            => Interlocked.Read(ref _count) >= _capacity;
        public long IntegrityCode   => Volatile.Read(ref _lastIntegrityCode);
        public bool IsIntact        => IntegrityCode == (long)LedgerIntegrity.Intact;
        public long HeadHash8       => Interlocked.Read(ref _headHash8);
        public long SealQpcDelta    => Volatile.Read(ref _sealQpcDelta);
        public long SealSeqOffset   => Volatile.Read(ref _sealSeqOffset);
        public long SealBufferState => Volatile.Read(ref _sealBufferState);
        public long SealAtRecord    => Volatile.Read(ref _sealAtRecord);

        /// <summary>
        /// 감사 레코드 1건을 사슬에 Append(δ=0). 봉인 후엔 false(비가역 차단), 용량 소진 시 false(무-wrap·유실 없음).
        /// </summary>
        public bool TryAppend(long qpc, long evictions, long seqOffset)
        {
            if (Volatile.Read(ref _sealed) == 1) return false;          // 트립 봉인 후 비가역 차단
            if (Interlocked.Read(ref _count) >= _capacity) return false; // Append-Only 무-wrap
            AppendCore(qpc, evictions, seqOffset);
            return true;
        }

        // 봉인 게이트를 우회하는 내부 사슬 확장(δ=0). TryAppend/SealTrip 공용.
        private void AppendCore(long qpc, long evictions, long seqOffset)
        {
            int i = (int)Interlocked.Read(ref _count);
            var input = _input.AsSpan();
            _current.AsSpan(0, HashLen).CopyTo(input);                                  // prevHash
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashLen),      qpc);    // ‖ QPC
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashLen + 8),  evictions); // ‖ Evictions
            BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashLen + 16), seqOffset); // ‖ SeqOffset
            SHA256.HashData(input.Slice(0, RecLen), _scratch.AsSpan(0, HashLen));       // ★무할당 정적 해시(δ=0)
            _scratch.AsSpan(0, HashLen).CopyTo(_hashes.AsSpan(i * HashLen, HashLen));   // 레코드 헤드 해시 상주
            _qpc[i] = qpc; _evict[i] = evictions; _seq[i] = seqOffset;
            _scratch.AsSpan(0, HashLen).CopyTo(_current.AsSpan(0, HashLen));            // 헤드 전진
            Interlocked.Exchange(ref _headHash8, BinaryPrimitives.ReadInt64LittleEndian(_current));
            Interlocked.Exchange(ref _count, i + 1);
        }

        /// <summary>
        /// ★타임-와프 트립 포렌식 봉인 — 트립 당시 포렌식(QPC Δ·시퀀스 오프셋·버퍼 상태)을 특수 레코드로 사슬에
        /// 영구 봉인하고 _sealed 를 1회 래치(비가역). 이후 모든 TryAppend 거부. 1회만 성립(중복 호출 false). δ=0.
        /// </summary>
        public bool SealTrip(long qpcDelta, long seqOffset, long bufferState)
        {
            if (Interlocked.CompareExchange(ref _sealed, 1, 0) != 0) return false;   // 이미 봉인 — 비가역
            Volatile.Write(ref _sealQpcDelta, qpcDelta);
            Volatile.Write(ref _sealSeqOffset, seqOffset);
            Volatile.Write(ref _sealBufferState, bufferState);
            Volatile.Write(ref _sealAtRecord, Interlocked.Read(ref _count));
            if (Interlocked.Read(ref _count) < _capacity)                            // 봉인 레코드는 게이트 우회 기입(1건)
                AppendCore(SealMagic ^ qpcDelta, bufferState, seqOffset);
            RecomputeIntegrity();                                                    // 봉인 시점 무결성 확정
            return true;
        }

        /// <summary>전 사슬을 제네시스부터 재계산해 저장 해시/헤드와 대조(δ=0·별도 스크래치). INTACT/CORRUPTED.</summary>
        public LedgerIntegrity Verify() => RecomputeIntegrity();

        private LedgerIntegrity RecomputeIntegrity()
        {
            long n = Interlocked.Read(ref _count);
            var running = _verifyRunning.AsSpan(0, HashLen);
            _genesis.AsSpan(0, HashLen).CopyTo(running);
            var input = _verifyInput.AsSpan();
            for (long k = 0; k < n; k++)
            {
                int i = (int)k;
                running.CopyTo(input.Slice(0, HashLen));
                BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashLen),      _qpc[i]);
                BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashLen + 8),  _evict[i]);
                BinaryPrimitives.WriteInt64LittleEndian(input.Slice(HashLen + 16), _seq[i]);
                SHA256.HashData(input.Slice(0, RecLen), running);
                if (!running.SequenceEqual(_hashes.AsSpan(i * HashLen, HashLen)))
                { Volatile.Write(ref _lastIntegrityCode, (long)LedgerIntegrity.Corrupted); return LedgerIntegrity.Corrupted; }
            }
            bool headOk = running.SequenceEqual(_current.AsSpan(0, HashLen));
            var res = headOk ? LedgerIntegrity.Intact : LedgerIntegrity.Corrupted;
            Volatile.Write(ref _lastIntegrityCode, (long)res);
            return res;
        }

        /// <summary>스로틀 무결성 폴(텔레메트리·δ=0) — verifyInterval 초과 시에만 재검증하고 캐시 코드 반환.</summary>
        public long PollVerify(long nowTicks)
        {
            long last = Interlocked.Read(ref _lastVerifyTick);
            if (nowTicks - last >= _verifyIntervalTicks
                && Interlocked.CompareExchange(ref _lastVerifyTick, nowTicks, last) == last)
                RecomputeIntegrity();
            return Volatile.Read(ref _lastIntegrityCode);
        }

        /// <summary>헤드 해시를 호출자 버퍼로 복사(δ=0·검증/내보내기). 버퍼 &lt;32 면 false.</summary>
        public bool CopyHeadHash(Span<byte> dst)
        {
            if (dst.Length < HashLen) return false;
            _current.AsSpan(0, HashLen).CopyTo(dst);
            return true;
        }

        /// <summary>헤드 해시 hex(콜드·UI 포렌식 표시 — 핫패스 아님).</summary>
        public string CurrentHashHex() => Convert.ToHexString(_current);
    }
}
