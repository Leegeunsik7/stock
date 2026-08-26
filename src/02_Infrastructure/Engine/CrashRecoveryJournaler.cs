// ============================================================================
// CrashRecoveryJournaler — Season 24: 에페메럴 저널링 + 제로-메모리 크래시 복구 원장
//
//  하드웨어 전원 단절·호스트 크래시로 급작 종료돼도, 메모리상 포렌식/챔피언 상태를 MMF에
//  실시간 영속화하고 부팅 시 무결성 검증 후 복구한다. 단, '조용한 오염 복구'는 최악의 결함이므로
//  단 1비트라도 깨졌으면 시스템을 fail-closed(ForceFreeze)로 묶는다.
//
//  ── Mission 1: 무할당 직렬화 ────────────────────────────────────────────────
//  JournalRecord 는 unmanaged 값 타입(참조 0 → 블리터블). BridgeProtocol 과 동일하게
//  MemoryMarshal.Write/Read 로 구조체↔바이트를 옮긴다 — BinaryFormatter/JSON·unsafe 불필요.
//  Encode/Decode 는 호출자 span 위에서 동작하고 인코딩 버퍼는 생성 시 1회 할당 → per-op 할당 0.
//
//  ── Mission 2: torn journal fail-closed ────────────────────────────────────
//  레이아웃 [MagicHead][Version][Count][CRC32][Record×N][MagicTail]. 부팅 검증 수식:
//    head≠MAGIC ∨ ver≠V ∨ count<0 ∨ 길이부족 ∨ tail≠MAGIC ∨ CRC≠stored  ⟹  Corrupt ⟹ Freeze
//  전원 차단 중 torn write 는 후미 매직 소실 또는 CRC 불일치로 반드시 포착된다. 파일 부재·
//  count=0 은 Empty(클린 부팅)로 분리 — 정상 신규를 동결하지 않는다(BootIntegrityChecker 사상).
// ============================================================================
using System;
using System.Buffers.Binary;
using System.IO;                       // IOException(접근불가 분류)
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace StockManager.Common
{
    public enum RecoveryKind   : byte { None = 0, Rejection = 1, Champion = 2 }

    // append-only(기존 0~4 불변). Inaccessible=5 신규 — I/O 예외(파일 락·권한)는 '오염'이 아니다.
    public enum JournalStatus : byte
    {
        Empty = 0, Ok = 1, BadMagic = 2, BadCrc = 3, Truncated = 4,
        Inaccessible = 5
    }

    public static class JournalStatusEx
    {
        // 내용 증거 기반 오염(불결한 크래시·변조) — 동결 + (하이드레이션 측) 폐기 대상.
        public static bool IsCorrupt(this JournalStatus s)
            => s is JournalStatus.BadMagic or JournalStatus.BadCrc or JournalStatus.Truncated;

        // 읽지 못함(증거 無) — 오염 아님. S24-b: 안전 상태(게이팅)는 하이드레이션이 권위 있게 검증하므로
        // 포렌식 저널의 일시적 접근불가는 동결시키지 않는다(미션3 자가당착 차단). 비파괴.
        public static bool IsInaccessible(this JournalStatus s)
            => s == JournalStatus.Inaccessible;
    }

    /// <summary>
    /// 크래시 복구 저널 1건(unmanaged · 참조 0 · 블리터블). 기본값(Seq=0)='없음'.
    /// S24-c — Pack=1: 정렬 패딩 바이트를 제거해 와이어 포맷을 완전히 결정론화한다. 패딩이 있으면
    /// MemoryMarshal.Write 가 정의되지 않은 패딩 비트까지 직렬화해 CRC32 가 부팅마다 튈 여지가 생긴다.
    /// (x64 Windows 전용 — Pack=1 의 비정렬 접근 비용은 가끔 읽는 저널 레코드엔 무시 가능.)
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct JournalRecord
    {
        public readonly long Seq;
        public readonly long AsOfTicks;
        public readonly long NowTicks;
        public readonly long WindowTicks;
        public readonly long FitnessBits;   // BitConverter.DoubleToInt64Bits(fitness)
        public readonly long GenomeHash;     // FNV-1a(SpecId) — 문자열 참조 대신 안정 해시(블리터블 유지)
        public readonly byte Kind;           // RecoveryKind
        public readonly byte Stage;          // RejectionStage
        public readonly byte Promotion;      // PromotionState

        public JournalRecord(long seq, RecoveryKind kind, byte stage, byte promotion,
                             long asOfTicks, long nowTicks, long windowTicks, double fitness, long genomeHash)
        {
            Seq         = seq;
            Kind        = (byte)kind;
            Stage       = stage;
            Promotion   = promotion;
            AsOfTicks   = asOfTicks;
            NowTicks    = nowTicks;
            WindowTicks = windowTicks;
            FitnessBits = BitConverter.DoubleToInt64Bits(fitness);
            GenomeHash  = genomeHash;
        }

        public bool   HasValue => Seq > 0;
        public double Fitness  => BitConverter.Int64BitsToDouble(FitnessBits);

        /// <summary>문자열 식별자를 안정 long 해시로(FNV-1a) — 구조체를 블리터블로 유지하기 위함.</summary>
        public static long HashId(string? id)
        {
            if (string.IsNullOrEmpty(id)) return 0L;
            unchecked
            {
                ulong h = 1469598103934665603UL;       // FNV offset basis
                for (int i = 0; i < id.Length; i++) { h ^= id[i]; h *= 1099511628211UL; }
                return (long)h;
            }
        }
    }

    // ── 무할당 코덱 (순수 · 호출자 span 위에서 동작) ───────────────────────────────
    public static class JournalCodec
    {
        public const  uint MagicHead = 0x4D533243u;     // "MS2C"
        public const  uint MagicTail = 0x4A524E4Cu;     // "JRNL"
        public const  int  Version   = 2;               // S24-c — Pack=1 레이아웃(이전 v1 패딩 포맷과 비호환)
        public const  int  HeaderSize = 16;             // head(4)+ver(4)+count(4)+crc(4)
        public static readonly int RecordSize = Unsafe.SizeOf<JournalRecord>();

        public static int RequiredBytes(int count) => HeaderSize + count * RecordSize + 4;  // +tail magic

        /// <summary>레코드를 dest 에 직렬화하고 총 길이 반환. 무할당. dest 부족 시 예외.</summary>
        public static int Encode(ReadOnlySpan<JournalRecord> recs, Span<byte> dest)
        {
            int n     = recs.Length;
            int total = RequiredBytes(n);
            if (dest.Length < total)
                throw new ArgumentException("저널 대상 버퍼 부족", nameof(dest));

            BinaryPrimitives.WriteUInt32LittleEndian(dest, MagicHead);
            BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(4), Version);
            BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(8), n);

            var recRegion = dest.Slice(HeaderSize, n * RecordSize);
            for (int i = 0; i < n; i++)
                MemoryMarshal.Write(recRegion.Slice(i * RecordSize), in recs[i]);   // 구조체→바이트(무할당)

            uint crc = Crc32.Compute(recRegion);
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(12), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(HeaderSize + n * RecordSize), MagicTail);
            return total;
        }

        /// <summary>src 를 검증·복호화해 dest 에 채운다. 무할당. 오염 시 Corrupt 상태 반환.</summary>
        public static JournalStatus Decode(ReadOnlySpan<byte> src, Span<JournalRecord> dest, out int count)
        {
            count = 0;
            if (src.Length < HeaderSize) return JournalStatus.Empty;          // 파일 부재/극소 → 클린

            uint head = BinaryPrimitives.ReadUInt32LittleEndian(src);
            if (head == 0u)        return JournalStatus.Empty;                // 미초기화(0) → 클린
            if (head != MagicHead) return JournalStatus.BadMagic;

            int ver = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(4));
            // S24-c — 매직은 맞으나 버전이 다르면 '우리 구 포맷'(양성 스큐)이지 torn/변조 오염이 아니다.
            // 저널은 에페메럴 복구 보조이지 권위 게이팅 상태가 아니므로, 구 포맷은 폐기하고 클린 부팅(무동결).
            // (S24-b 사상 일관: 내용 증거 오염만 동결, 양성 신호는 비동결 — 포맷 업그레이드가 거래를 동결시키면 자가당착.)
            if (ver != Version) return JournalStatus.Empty;

            int n = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(8));
            if (n < 0) return JournalStatus.Truncated;
            if (n == 0) return JournalStatus.Empty;                          // 유효 헤더+0레코드 → 클린

            long need = (long)HeaderSize + (long)n * RecordSize + 4L;
            if (src.Length < need) return JournalStatus.Truncated;           // torn — 후미 잘림

            uint tail = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(HeaderSize + n * RecordSize));
            if (tail != MagicTail) return JournalStatus.Truncated;           // 후미 매직 소실 = torn write

            uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(12));
            var  recRegion = src.Slice(HeaderSize, n * RecordSize);
            if (Crc32.Compute(recRegion) != storedCrc) return JournalStatus.BadCrc;   // 1비트 오염

            int copy = Math.Min(n, dest.Length);
            for (int i = 0; i < copy; i++)
                dest[i] = MemoryMarshal.Read<JournalRecord>(recRegion.Slice(i * RecordSize));
            count = copy;
            return JournalStatus.Ok;
        }
    }

    // ── 저널 결속 (StateHydrationEngine 패턴) — 물리 I/O 는 IJournalStore 로 격리(S25-c) ──────────
    public sealed class CrashRecoveryJournaler
    {
        private readonly IJournalStore _store;   // S25-c — MMF(프로덕션) ↔ 인메모리(헤르메틱 카오스) 추상화
        private readonly int    _capacity;
        private readonly long   _maxBytes;
        private readonly byte[] _buf;          // 재사용 플랫 인코딩/디코딩 버퍼(생성 시 1회 할당)
        private readonly byte[] _compositeBuf; // S25-b — 복합 포맷 전용 재사용 버퍼(플랫보다 큼: 게놈섹터+헤더)

        /// <summary>경로 생성자 — 프로덕션 MMF backing(후방호환: 기존 모든 호출부·테스트가 그대로 동작).</summary>
        public CrashRecoveryJournaler(string path, int capacity = 64)
            : this(new MmfJournalStore(path ?? throw new ArgumentNullException(nameof(path))), capacity) { }

        /// <summary>store 주입 생성자 — 헤르메틱 카오스 테스트가 InMemoryJournalStore 로 디스크 없이 검증.</summary>
        public CrashRecoveryJournaler(IJournalStore store, int capacity = 64)
        {
            _store        = store ?? throw new ArgumentNullException(nameof(store));
            _capacity     = capacity < 1 ? 1 : capacity;
            _maxBytes     = JournalCodec.RequiredBytes(_capacity);
            _buf          = new byte[_maxBytes];
            _compositeBuf = new byte[CompositeJournalCodec.RequiredBytes(_capacity)];
        }

        public int Capacity => _capacity;

        /// <summary>
        /// 최근 레코드 스냅샷을 MMF에 영속화. 인코딩 무할당(재사용 버퍼). 디스크 실패는 흡수하되
        /// false 를 반환해 호출자(플러시 펌프)가 flushedGen 을 전진시키지 않고 재시도하게 한다.
        /// </summary>
        public bool Persist(ReadOnlySpan<JournalRecord> recs)
        {
            var slice = recs.Length > _capacity ? recs.Slice(recs.Length - _capacity) : recs;
            int len   = JournalCodec.Encode(slice, _buf);
            try { _store.Write(_buf, len, _maxBytes); return true; }
            catch { return false; /* 영속화 실패 흡수 — 펌프가 다음 주기 재시도 */ }
        }

        /// <summary>부팅 시 1회 복구. 검증 후 레코드를 dest 에 채우고 상태 반환. 클린 부재 → Empty.</summary>
        public JournalStatus Recover(Span<JournalRecord> dest, out int count)
        {
            count = 0;
            try
            {
                if (!_store.Exists) return JournalStatus.Empty;
                if (_store.Length < JournalCodec.HeaderSize) return JournalStatus.Empty;

                int readLen = _store.Read(_buf);   // min(파일길이, _buf.Length) — 기존 readLen 과 동일
                return JournalCodec.Decode(_buf.AsSpan(0, readLen), dest, out count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // S24-b 미션3 — 파일 락·권한 등 접근 실패는 '오염'이 아니다(증거 無). 비파괴 신호.
                return JournalStatus.Inaccessible;
            }
            catch
            {
                // 그 외(파싱·범위·프로그래밍 오류 등 미상) = 보수적 corrupt 유지(fail-closed 유도).
                return JournalStatus.Truncated;
            }
        }

        /// <summary>
        /// 복구 + 부팅 방화벽. 내용 오염(Corrupt)이면 ForceFreeze(fail-closed, 파일 보존). 상태 반환.
        /// I/O 접근불가(Inaccessible)는 동결하지 않는다 — 권위 있는 게이팅 검증은 하이드레이션이 담당하며,
        /// 포렌식 저널의 일시적 락으로 거래를 동결하는 것이 미션3의 자가당착이기 때문이다(구별 경보만).
        /// </summary>
        public JournalStatus RecoverOrFreeze(OrderAttestationService attestation, Span<JournalRecord> dest, out int count)
        {
            var status = Recover(dest, out count);
            if (status.IsCorrupt())
            {
                try
                {
                    attestation?.ForceFreeze(
                        $"크래시 복구: 저널 오염({status}) 감지 — 불결한 종료 증거, 부팅 fail-closed. " +
                        "/broker/gating 수동 정합 후 해제(저널 파일은 보존됨).");
                }
                catch { /* 동결 실패해도 상태는 정직히 반환 */ }
            }
            else if (status.IsInaccessible())
            {
                // 동결하지 않음 — 비파괴. 운영자에게 '재시도 가능·오염 아님'을 구별 보고.
                Log.Warn("[복구] 크래시 저널 접근불가(I/O·락) — 오염 아님, 비동결. 포렌식 복구만 생략, 안전 상태는 하이드레이션이 검증.");
            }
            return status;
        }

        // ── S25-b 복합 포맷(게놈 섹터 + 포렌식 링) ────────────────────────────────────

        /// <summary>원자 세션(게놈 + 포렌식)을 복합 포맷으로 MMF 영속화. 무할당(재사용 _compositeBuf). 디스크 실패 시 false.</summary>
        public bool PersistComposite(in GenomeSnapshot genome, ReadOnlySpan<JournalRecord> recs)
        {
            int len = CompositeJournalCodec.Encode(genome, recs, _capacity, _compositeBuf);
            try { _store.Write(_compositeBuf, len, _compositeBuf.Length); return true; }
            catch { return false; }
        }

        /// <summary>복합 저널 복구. 섹터 독립 검증. 구 플랫(MS2C)·버전 스큐는 Empty(마이그레이션). I/O 예외는 Inaccessible.</summary>
        public CompositeResult RecoverComposite(Span<JournalRecord> recsDest, out int count, out GenomeSnapshot genome)
        {
            count = 0; genome = default;
            try
            {
                if (!_store.Exists)
                    return new CompositeResult(JournalStatus.Empty, JournalStatus.Empty, JournalStatus.Empty, default, 0);
                if (_store.Length < 8)
                    return new CompositeResult(JournalStatus.Empty, JournalStatus.Empty, JournalStatus.Empty, default, 0);

                int readLen = _store.Read(_compositeBuf);   // min(파일길이, _compositeBuf.Length)
                var r = CompositeJournalCodec.Decode(_compositeBuf.AsSpan(0, readLen), _capacity, recsDest, out count);
                genome = r.GenomeRecord;
                return r;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new CompositeResult(JournalStatus.Inaccessible, JournalStatus.Inaccessible, JournalStatus.Inaccessible, default, 0);
            }
            catch
            {
                return new CompositeResult(JournalStatus.Truncated, JournalStatus.Truncated, JournalStatus.Truncated, default, 0);
            }
        }

        /// <summary>복합 복구 + 부팅 방화벽. 어느 섹터든 내용 오염(Corrupt)이면 ForceFreeze. Inaccessible·Empty·구포맷은 비동결.</summary>
        public CompositeResult RecoverOrFreezeComposite(OrderAttestationService attestation,
                                                        Span<JournalRecord> recsDest, out int count, out GenomeSnapshot genome)
        {
            var r = RecoverComposite(recsDest, out count, out genome);
            if (r.AnyCorrupt)
            {
                try
                {
                    attestation?.ForceFreeze(
                        $"크래시 복구(복합): 저널 오염(file={r.File} genome={r.Genome} forensic={r.Forensic}) — " +
                        "불결한 종료 증거, 부팅 fail-closed. /broker/gating 수동 정합 후 해제(파일 보존).");
                }
                catch { }
            }
            else if (r.File.IsInaccessible())
            {
                Log.Warn("[복구] 복합 저널 접근불가(I/O·락) — 오염 아님, 비동결(자가당착 차단).");
            }
            return r;
        }
    }
}
