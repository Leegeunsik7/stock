// ============================================================================
// MonotonicSettingAuditLog — [v12.18.2-DEV · 추천 A] 강화 전용 설정 감사 원장(MMF 해시 체인 · 변조 감지 · fail-closed 동결)
//
//  TightenOnlyUiSettingWriter 의 모든 시도(강화 · 변화 없음 · 거부)를 128B 고정 레코드로 파일 기반 MemoryMappedFile 에 기록한다.
//  각 레코드 = SHA-256(레코드 앞 96B — 직전 레코드 해시 포함). 첫 레코드의 '직전 해시' = 헤더 해시(파일마다 난수 nonce → 원장마다 다른 제네시스).
//
//  레이아웃(리틀 엔디언):
//   헤더 128B: [0,8) "MS2EAUDT" · [8,12) 버전 1 · [12,16) 레코드 크기 128 · [16,20) 용량 · [20,24) 0 · [24,32) 생성 UTC ticks ·
//             [32,64) nonce · [64,96) 0 · [96,128) 헤더 해시 = SHA-256([0,96)).
//   레코드 128B: [0,8) 순번(1부터 · 0 = 빈 슬롯) · [8,16) UTC ticks · [16,18) 키 인덱스(−1 = 미등재) · [18] 판정(TightenVerdict) ·
//             [19] 규칙 비트 · [20,24) 직전 레코드 이후 예산 초과로 생략된 거부 수 · [24,32) 키 원문 FNV-1a 64 · [32,40) 현재 유효값 ·
//             [40,48) 제안 유효값 · [48,64) 0 · [64,96) 직전 해시 · [96,128) 레코드 해시.
//
//  검증(Verify — 정적 · 힙 0): 헤더 형식 · 헤더 해시 → 슬롯마다 순번 = i+1 · 직전 해시 = 앞 레코드 해시 · 재계산 해시 = 저장 해시 ·
//   시각 비감소 · 필드 범위 · 예약 0 → 첫 빈 슬롯부터 끝까지 전부 0(꼬리 쓰레기 = 거부). 1비트라도 바뀌면 그 레코드(또는 헤더/꼬리)에서 거부.
//
//  ★fail-closed:
//   · Append 는 매번 전체 체인을 재검증한 뒤 기록하고, 결과(개수 · 머리 해시)를 프로세스 메모리의 앵커와도 대조한다 — 실행 중 파일을
//     '일관되게 다시 해시한' 위조(체인 자체는 맞음)나 꼬리 절단도 다음 기입에서 거부.
//   · 불일치/손상/가득 참/열기 실패 = 동결(sticky — 이 프로세스 수명 동안 해제 없음) + StrategyAnomalyInterlock.Trip → 비트 27 원자 Disarm.
//     동결 원장 = TightenOnlyUiSettingWriter 가 판독조차 하지 않고 AuditFrozen 거부(기록 없는 변경 0).
//   · 호스트 1초 타이머가 PollIntegrity 로 전체 재검증 — 쓰기가 없어도 변조 감지 ≤ 1초.
//  거부 레코드 예산: 분당 60건 · 용량의 3/4 이후 거부 레코드 기록 중단(생략 수는 다음 레코드 [20,24)에 누계) — UI 거부 폭주가 원장을
//   채워 강화(안전 방향) 기입까지 막는 것을 늦춘다. 강화 레코드는 예산 없음 — 가득 차면 동결(+비트 27). 운영자 조치 = 파일 보관 후 재기동(새 원장).
//
//  δ=0 범위(정직): Append(직렬화 · 해시 · 체인 재검증) · Verify · PollIntegrity 힙 0(테스트 실측). 해시 = 원장마다 재사용하는 IncrementalHash
//   (SHA-256 · 잠금 안에서만 사용 · TryGetHashAndReset 스팬 출력) — [v12.18.3-DEV 교정] 정적 SHA256.HashData 는 호출마다 알고리즘 핸들 경로를 타
//   레코드 2,048개 검증이 이 호스트에서 ≈ 485µs → 부하 시 1ms 예산 초과(635~1,144µs 실측). 재사용 해시 ≈ 237µs(같은 바이트 결과).
//   정적 Verify(외부 도구)는 호출당 IncrementalHash 1개를 시간 측정 구간 밖에서 만든다. 열기/생성(파일 · MMF · 뷰)은 콜드.
//  [v12.18.3-DEV] 관제 스냅샷(Snapshot · AuditLedgerSnapshot): 기록자(잠금 안 · 단일)가 기록/동결 직후 seqlock 으로 게시한 사본을 판독자가
//   잠금 없이 읽는다 — UI 조회는 체인 잠금(_gate)을 잡지 않고(경합 0) · 원장 영역을 읽지도 해시하지도 않는다(변조 판정은 기록자 몫 그대로).
//   찢긴 읽기 = 재시도(기록은 사람 속도 — 실질 재시도 0) · 64회 실패 = 직전 일관 사본이 아니라 Contended 표지(fail-closed 표시: 동결로 보임).
//   [방향 B] 포화 소프트 가드: 사용 ≥ 90%(용량 2,048 → 1,843) = Saturating — 기록은 계속하되 운영자에게 보관 · 재기동을 미리 알린다(1회 Trace).
//   가득 참(100%) = 기존 동결 + 비트 27. 거부 레코드는 75% 부터 이미 생략(강화 레코드 자리 보존).
//  한계(정직): 키 없는 SHA-256 체인이다 — 프로세스가 내려간 사이 파일 전체를 일관되게 다시 해시한 위조는 정적 검증만으로 구분할 수 없다
//   (실행 중 메모리 앵커 · CLI --expect-head 외부 앵커가 그 몫). 기록 중 정전으로 찢긴 레코드 = 재기동 시 체인 불일치 → 동결(안전 쪽).
// ============================================================================
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Threading;
using MS2E.Infrastructure;
using StockManager.Engines;

namespace StockManager.Security
{
    public enum AuditChainVerdict : byte
    {
        Ok = 0, HeaderCorrupt = 1, SequenceGap = 2, PrevHashMismatch = 3, RecordHashMismatch = 4, TimestampRegressed = 5,
        FieldInvalid = 6, TrailingGarbage = 7, AnchorMismatch = 8, Unavailable = 9,
    }

    public enum AuditFreezeCause : byte { None = 0, ChainBroken = 1, Full = 2, Unavailable = 3 }

    /// <summary>원장 관제 상태(RCL DashAuditLedger 와 같은 값 — 0 = 미결선은 UI 전용).</summary>
    public enum AuditLedgerState : byte { Normal = 1, Saturating = 2, Frozen = 3 }

    /// <summary>
    /// [v12.18.3-DEV] 원장 관제 스냅샷(값 형식 · 할당 0). HeadPrefix = 머리 해시 상위 8바이트(빅 엔디언 — 16진 문자열 앞 16자와 같은 순서).
    /// Contended = seqlock 판독 실패(값 무효 — 표시는 동결 취급).
    /// </summary>
    public readonly record struct AuditLedgerSnapshot(int Count, int Capacity, AuditFreezeCause FreezeCause, AuditChainVerdict LastVerdict,
                                                      ulong HeadPrefix, long Version, bool Contended = false)
    {
        public const int SaturationPercent = 90;
        /// <summary>포화 경고 시작 레코드 수(용량 × 90% 내림 — 2,048 → 1,843).</summary>
        public static int SaturationThreshold(int capacity) => (int)((long)capacity * SaturationPercent / 100);
        public AuditLedgerState State => Contended || FreezeCause != AuditFreezeCause.None ? AuditLedgerState.Frozen
                                       : Capacity > 0 && Count >= SaturationThreshold(Capacity) ? AuditLedgerState.Saturating : AuditLedgerState.Normal;
        /// <summary>사용률(정수 % · 내림 · 0~100).</summary>
        public int UsedPercent => Capacity <= 0 ? 0 : (int)Math.Min(100, (long)Count * 100 / Capacity);
    }

    public sealed unsafe class MonotonicSettingAuditLog : ITightenAuditSink, IDisposable
    {
        public const int RecordSize = 128, HeaderSize = 128, HashOffset = 96, PrevOffset = 64, DefaultCapacity = 2048;
        public const int RejectBudgetPerMinute = 60;
        private static ReadOnlySpan<byte> Magic => "MS2EAUDT"u8;
        private const int Version = 1;

        private readonly object _gate = new();
        private readonly MemoryMappedFile? _mmf;
        private readonly MemoryMappedViewAccessor? _view;
        private readonly byte* _base;
        private readonly int _length, _capacity;
        private readonly StrategyAnomalyInterlock _anomaly;
        private readonly Func<long> _utcTicks;
        private readonly byte[] _head = new byte[32];                  // 프로세스 메모리 앵커(파일 밖)
        private readonly IncrementalHash _hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);   // _gate 안(또는 생성자)에서만 사용
        private int _count, _frozen, _disposed, _rejectsInWindow, _suppressed;
        private long _windowStart, _lastTicks;
        private AuditChainVerdict _lastVerdict;
        private long _appended, _rejectRecords, _suppressedTotal, _verifications;
        // [v12.18.3-DEV] 관제 seqlock(기록자 = _gate 보유자 또는 생성자 — 단일) · 판독자 = 잠금 없음
        private int _snapSeq, _snapCount, _snapFreeze, _snapVerdict;
        private long _snapHead, _snapVersion, _saturationWarnings;
        private bool _saturationNoticed;
        public long SaturationWarnings => Interlocked.Read(ref _saturationWarnings);

        public int Capacity => _capacity;
        public int Count => Volatile.Read(ref _count);
        public bool Frozen => Volatile.Read(ref _frozen) != 0 || Volatile.Read(ref _disposed) != 0;   // 폐기 후 = 기록 불가(뷰 해제)
        public AuditFreezeCause FreezeCause => (AuditFreezeCause)Volatile.Read(ref _frozen);
        public AuditChainVerdict LastVerdict => _lastVerdict;
        public long Appended => Interlocked.Read(ref _appended);
        public long RejectRecords => Interlocked.Read(ref _rejectRecords);
        public long SuppressedRejects => Interlocked.Read(ref _suppressedTotal);
        public long Verifications => Interlocked.Read(ref _verifications);
        public string? FilePath { get; }

        /// <summary>파일 기반 원장 열기(없으면 생성). 기존 파일 = 전체 체인 검증 — 불일치/형식 이탈/열기 실패 = 동결 + 비트 27.</summary>
        public static MonotonicSettingAuditLog Open(string path, StrategyAnomalyInterlock anomaly, int capacity = DefaultCapacity, Func<long>? utcTicks = null)
            => new(path, anomaly, capacity, utcTicks);

        /// <summary>파일 없는 원장(익명 MMF — 시뮬레이션 · 도구 · 테스트용). 규칙은 같다.</summary>
        public static MonotonicSettingAuditLog CreateVolatile(StrategyAnomalyInterlock anomaly, int capacity = DefaultCapacity, Func<long>? utcTicks = null)
            => new(null, anomaly, capacity, utcTicks);

        public static long RegionBytes(int capacity) => HeaderSize + (long)capacity * RecordSize;

        private MonotonicSettingAuditLog(string? path, StrategyAnomalyInterlock anomaly, int capacity, Func<long>? utcTicks)
        {
            if (capacity < 1 || capacity > 1 << 20) throw new ArgumentOutOfRangeException(nameof(capacity));
            _anomaly = anomaly ?? throw new ArgumentNullException(nameof(anomaly));
            _utcTicks = utcTicks ?? (static () => DateTime.UtcNow.Ticks);
            _capacity = capacity;
            _length = checked((int)RegionBytes(capacity));
            FilePath = path;
            bool fresh = true;
            try
            {
                if (path is null) _mmf = MemoryMappedFile.CreateNew(null, _length, MemoryMappedFileAccess.ReadWrite);
                else
                {
                    var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    // 쓰기 핸들은 이 프로세스 1개뿐(FileShare.Read) — 외부 판독(--verify-audit-chain)만 허용
                    var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                    fresh = fs.Length == 0;
                    if (!fresh && fs.Length != _length) { fs.Dispose(); FreezeAt(AuditFreezeCause.ChainBroken, AuditChainVerdict.HeaderCorrupt); return; }
                    _mmf = MemoryMappedFile.CreateFromFile(fs, null, _length, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
                }
                _view = _mmf.CreateViewAccessor(0, _length, MemoryMappedFileAccess.ReadWrite);
                byte* p = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                _base = p + _view.PointerOffset;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                _view?.Dispose(); _mmf?.Dispose(); _view = null; _mmf = null; _base = null;
                FreezeAt(AuditFreezeCause.Unavailable, AuditChainVerdict.Unavailable);
                return;
            }
            var region = Region;
            if (fresh) WriteHeader(region[..HeaderSize], capacity, _utcTicks());
            var v = Verify(region, out _count, _head, out _, _hasher);
            Interlocked.Increment(ref _verifications);
            _lastVerdict = v;
            if (v != AuditChainVerdict.Ok) { FreezeAt(AuditFreezeCause.ChainBroken, v); return; }
            _lastTicks = _count == 0 ? 0 : BinaryPrimitives.ReadInt64LittleEndian(region.Slice(HeaderSize + (_count - 1) * RecordSize + 8, 8));
            if (_count == capacity) FreezeAt(AuditFreezeCause.Full, AuditChainVerdict.Ok);
            PublishSnapshot();
        }

        private Span<byte> Region => _base == null ? Span<byte>.Empty : new Span<byte>(_base, _length);

        /// <summary>머리 해시(직전 레코드 해시 · 빈 원장 = 헤더 해시) 사본.</summary>
        public void CopyHead(Span<byte> destination) { lock (_gate) _head.CopyTo(destination); }

        /// <summary>
        /// 레코드 1건(δ=0). ① 동결 = false ② 전체 체인 재검증 + 메모리 앵커 대조 — 불일치 = 동결 + 비트 27 ③ 가득 참 = 동결 + 비트 27 ④ 거부 예산 ⑤ 기록.
        /// </summary>
        public bool Append(int keyIndex, string? key, TightenVerdict verdict, long current, long proposed)
        {
            lock (_gate)
            {
                if (Frozen) return false;
                if (!CheckIntegrity()) return false;
                if (_count == _capacity) { FreezeAt(AuditFreezeCause.Full, AuditChainVerdict.Ok); return false; }
                long now = _utcTicks();
                bool rejection = verdict is not (TightenVerdict.Tightened or TightenVerdict.StoreFault);
                if (rejection)
                {
                    if (now - _windowStart >= TimeSpan.TicksPerMinute || now < _windowStart) { _windowStart = now; _rejectsInWindow = 0; }
                    if (_rejectsInWindow >= RejectBudgetPerMinute || _count >= _capacity - _capacity / 4)
                    {
                        _suppressed++; Interlocked.Increment(ref _suppressedTotal);
                        return false;
                    }
                    _rejectsInWindow++;
                }
                long ticks = Math.Max(now, _lastTicks);                     // 벽시계 역행(NTP) = 직전 시각 유지(체인 시각 비감소 불변식)
                var slot = Region.Slice(HeaderSize + _count * RecordSize, RecordSize);
                slot.Clear();
                BinaryPrimitives.WriteUInt64LittleEndian(slot, (ulong)(_count + 1));
                BinaryPrimitives.WriteInt64LittleEndian(slot[8..], ticks);
                BinaryPrimitives.WriteInt16LittleEndian(slot[16..], (short)(keyIndex < 0 ? -1 : keyIndex));
                slot[18] = (byte)verdict;
                slot[19] = keyIndex >= 0 && keyIndex < TightenOnlyUiSettingWriter.KeyCount ? TightenOnlyUiSettingWriter.RuleAt(keyIndex) : (byte)0;
                BinaryPrimitives.WriteUInt32LittleEndian(slot[20..], (uint)_suppressed);
                BinaryPrimitives.WriteUInt64LittleEndian(slot[24..], KeyHash(key));
                BinaryPrimitives.WriteInt64LittleEndian(slot[32..], current);
                BinaryPrimitives.WriteInt64LittleEndian(slot[40..], proposed);
                _head.CopyTo(slot[PrevOffset..]);
                Hash(_hasher, slot[..HashOffset], slot[HashOffset..]);
                slot[HashOffset..].CopyTo(_head);
                _lastTicks = ticks;
                _suppressed = 0;
                Volatile.Write(ref _count, _count + 1);
                Interlocked.Increment(ref _appended);
                if (rejection) Interlocked.Increment(ref _rejectRecords);
                PublishSnapshot();
                return true;
            }
        }

        /// <summary>주기 무결성 판정(호스트 1초 타이머 · δ=0). false = 동결(이번에 감지했거나 이미 동결).</summary>
        public bool PollIntegrity()
        {
            lock (_gate) return !Frozen && CheckIntegrity();
        }

        // 잠금 안: 전체 체인 + 메모리 앵커(개수 · 머리 해시). 불일치 = 동결 + 비트 27.
        private bool CheckIntegrity()
        {
            Span<byte> head = stackalloc byte[32];
            var v = Verify(Region, out int n, head, out _, _hasher);
            Interlocked.Increment(ref _verifications);
            if (v == AuditChainVerdict.Ok && (n != _count || !CryptographicOperations.FixedTimeEquals(head, _head))) v = AuditChainVerdict.AnchorMismatch;
            _lastVerdict = v;
            if (v == AuditChainVerdict.Ok) return true;
            FreezeAt(AuditFreezeCause.ChainBroken, v);
            return false;
        }

        private void FreezeAt(AuditFreezeCause cause, AuditChainVerdict v)
        {
            _lastVerdict = v;
            if (Interlocked.CompareExchange(ref _frozen, (int)cause, 0) != 0) { PublishSnapshot(); return; }
            PublishSnapshot();                                                      // 관제가 비트 27 과 같은 순간 동결을 본다
            _anomaly.Trip(cause == AuditFreezeCause.Full ? StrategyAnomalyKind.AuditLedgerFull : StrategyAnomalyKind.AuditChainBroken);   // ★비트 27 원자 Disarm
        }

        // ── [v12.18.3-DEV] 관제 스냅샷(seqlock · 판독 잠금 0 · 할당 0) ──

        // 기록자 전용(_gate 보유 또는 생성자). 홀수 = 기록 중.
        private void PublishSnapshot()
        {
            Interlocked.Increment(ref _snapSeq);                                    // 홀수(전체 펜스)
            Volatile.Write(ref _snapCount, _count);
            Volatile.Write(ref _snapFreeze, Volatile.Read(ref _frozen));
            Volatile.Write(ref _snapVerdict, (int)_lastVerdict);
            Volatile.Write(ref _snapHead, (long)BinaryPrimitives.ReadUInt64BigEndian(_head));
            Volatile.Write(ref _snapVersion, _snapVersion + 1);
            Interlocked.Increment(ref _snapSeq);                                    // 짝수(전체 펜스)
            if (!_saturationNoticed && _count >= AuditLedgerSnapshot.SaturationThreshold(_capacity))
            {
                // [방향 B] 포화 소프트 가드 — 1회 고지(콜드 · 기록자 스레드). 동결은 아직 아님(가득 참에서만).
                _saturationNoticed = true;
                Interlocked.Increment(ref _saturationWarnings);
                System.Diagnostics.Trace.WriteLine("[AuditLedger] 사용 " + _count + "/" + _capacity + " ≥ 90% — 원장 보관 후 재기동 필요(가득 차면 설정 변경 동결 + 비트 27)");
            }
        }

        /// <summary>관제 스냅샷(임의 스레드 · 체인 잠금 미사용 · 원장 영역 미접근 · 할당 0).</summary>
        public AuditLedgerSnapshot Snapshot()
        {
            for (int spin = 0; spin < 64; spin++)
            {
                int s1 = Volatile.Read(ref _snapSeq);
                if ((s1 & 1) != 0) { Thread.SpinWait(4); continue; }
                int count = Volatile.Read(ref _snapCount), freeze = Volatile.Read(ref _snapFreeze), verdict = Volatile.Read(ref _snapVerdict);
                long head = Volatile.Read(ref _snapHead), ver = Volatile.Read(ref _snapVersion);
                if (Volatile.Read(ref _snapSeq) != s1) continue;
                // 폐기 후 = 기록 불가(Frozen 속성과 같은 판정) — 동결 원인이 없으면 Unavailable 로 표시
                var cause = (AuditFreezeCause)freeze;
                if (cause == AuditFreezeCause.None && Volatile.Read(ref _disposed) != 0) cause = AuditFreezeCause.Unavailable;
                return new AuditLedgerSnapshot(count, _capacity, cause, (AuditChainVerdict)verdict, (ulong)head, ver);
            }
            return new AuditLedgerSnapshot(0, _capacity, AuditFreezeCause.None, AuditChainVerdict.Ok, 0, 0, Contended: true);
        }

        // ── 정적 검증(힙 0) ──

        /// <summary>
        /// 원장 영역 전체 검증. count = 유효 레코드 수 · head = 마지막 레코드 해시(빈 원장 = 헤더 해시) · at = 첫 불일치 레코드(헤더 = −1).
        /// </summary>
        public static AuditChainVerdict Verify(ReadOnlySpan<byte> region, out int count, Span<byte> head, out int at)
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            return Verify(region, out count, head, out at, hasher);
        }

        /// <summary>재사용 해시 경로(호출측이 hasher 단독 사용 보장 · 할당 0).</summary>
        public static AuditChainVerdict Verify(ReadOnlySpan<byte> region, out int count, Span<byte> head, out int at, IncrementalHash hasher)
        {
            count = 0; at = -1;
            if (head.Length < 32 || region.Length < HeaderSize) return AuditChainVerdict.HeaderCorrupt;
            var h = region[..HeaderSize];
            if (!h[..8].SequenceEqual(Magic) || BinaryPrimitives.ReadInt32LittleEndian(h[8..]) != Version
                || BinaryPrimitives.ReadInt32LittleEndian(h[12..]) != RecordSize || BinaryPrimitives.ReadInt32LittleEndian(h[20..]) != 0
                || h[64..96].IndexOfAnyExcept((byte)0) >= 0)
                return AuditChainVerdict.HeaderCorrupt;
            int capacity = BinaryPrimitives.ReadInt32LittleEndian(h[16..]);
            if (capacity < 1 || region.Length != RegionBytes(capacity)) return AuditChainVerdict.HeaderCorrupt;
            Span<byte> calc = stackalloc byte[32];
            Hash(hasher, h[..HashOffset], calc);
            if (!calc.SequenceEqual(h[HashOffset..])) return AuditChainVerdict.HeaderCorrupt;
            var prev = h[HashOffset..];
            long lastTicks = long.MinValue;
            int i = 0;
            for (; i < capacity; i++)
            {
                var r = region.Slice(HeaderSize + i * RecordSize, RecordSize);
                ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(r);
                if (seq == 0) break;
                at = i;
                if (seq != (ulong)(i + 1)) return AuditChainVerdict.SequenceGap;
                if (!r.Slice(PrevOffset, 32).SequenceEqual(prev)) return AuditChainVerdict.PrevHashMismatch;
                Hash(hasher, r[..HashOffset], calc);
                if (!calc.SequenceEqual(r[HashOffset..])) return AuditChainVerdict.RecordHashMismatch;
                long ticks = BinaryPrimitives.ReadInt64LittleEndian(r[8..]);
                if (ticks < lastTicks || ticks <= 0) return AuditChainVerdict.TimestampRegressed;
                short key = BinaryPrimitives.ReadInt16LittleEndian(r[16..]);
                if (key < -1 || key >= TightenOnlyUiSettingWriter.KeyCount || r[18] > (byte)TightenVerdict.AuditFrozen || r[48..64].IndexOfAnyExcept((byte)0) >= 0)
                    return AuditChainVerdict.FieldInvalid;
                lastTicks = ticks;
                prev = r[HashOffset..];
            }
            count = i;
            int tail = HeaderSize + i * RecordSize;
            if (region[tail..].IndexOfAnyExcept((byte)0) >= 0) { at = i; return AuditChainVerdict.TrailingGarbage; }
            at = -1;
            prev.CopyTo(head);
            return AuditChainVerdict.Ok;
        }

        private static void Hash(IncrementalHash hasher, ReadOnlySpan<byte> data, Span<byte> destination)
        {
            hasher.AppendData(data);
            if (!hasher.TryGetHashAndReset(destination, out int written) || written != 32) throw new CryptographicException("SHA-256 출력 길이");
        }

        private static void WriteHeader(Span<byte> h, int capacity, long createdTicks)
        {
            h.Clear();
            Magic.CopyTo(h);
            BinaryPrimitives.WriteInt32LittleEndian(h[8..], Version);
            BinaryPrimitives.WriteInt32LittleEndian(h[12..], RecordSize);
            BinaryPrimitives.WriteInt32LittleEndian(h[16..], capacity);
            BinaryPrimitives.WriteInt64LittleEndian(h[24..], createdTicks);
            RandomNumberGenerator.Fill(h.Slice(32, 32));
            SHA256.HashData(h[..HashOffset], h[HashOffset..]);
        }

        /// <summary>키 원문 FNV-1a 64(UTF-16 코드 단위 · 할당 0) — 미등재 키도 원문 없이 식별 · null = 0.</summary>
        public static ulong KeyHash(string? key)
        {
            if (key is null) return 0;
            ulong x = 14695981039346656037UL;
            foreach (char c in key) { x ^= c; x *= 1099511628211UL; }
            return x;
        }

        /// <summary>판독 전용 검증(외부 도구 · 실행 중 호스트와 공존 — FileShare.ReadWrite). 영역 = MMF 읽기 뷰(복사 0).</summary>
        public static AuditChainVerdict VerifyFile(string path, out int count, Span<byte> head, out int at, out long verifyMicros)
        {
            count = 0; at = -1; verifyMicros = 0;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long len = fs.Length;
                if (len < HeaderSize || len > RegionBytes(1 << 20)) return AuditChainVerdict.HeaderCorrupt;
                using var mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                using var view = mmf.CreateViewAccessor(0, len, MemoryMappedFileAccess.Read);
                byte* p = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                try
                {
                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);   // 측정 구간 밖(콜드 생성)
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    var v = Verify(new ReadOnlySpan<byte>(p + view.PointerOffset, (int)len), out count, head, out at, hasher);
                    verifyMicros = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
                    return v;
                }
                finally { view.SafeMemoryMappedViewHandle.ReleasePointer(); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return AuditChainVerdict.Unavailable;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                if (_view is null) { _hasher.Dispose(); return; }                     // 열기 실패 원장(뷰 없음)도 해시 핸들 반납
                _view.Flush();
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
                _view.Dispose();
                _hasher.Dispose();
                _mmf?.Dispose();
            }
        }
    }
}
