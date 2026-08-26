// ============================================================================
// NvmeAuditJournal — v8.0.0 Phase 8 Task 1: NVMe MMF 영속 감사 저널
//
//  CausalAuditVault 의 SHA-256 해시체인을, Memory-Mapped File 의 네이티브 페이지 영역에 Append-Only 로 직기입한다.
//  ★비침습 데코레이터: 기존 CausalAuditVault.cs 를 단 1줄도 수정하지 않는다. 이 저널이 vault 를 '감싸'
//    TryAppend 를 write-through 로 중계한다 — vault.TryAppend 성공 시, 그 레코드(56B)를 MMF 로 미러링한다.
//
//  ── 레코드 레이아웃 ────────────────────────────────────────────────────────
//   헤더(64B): [magic i64 | version i64 | capacity i64 | count i64 | genesisHash 32B]
//   레코드(56B): [qpc i64 | evictions i64 | seqOffset i64 | resultingHash 32B]   ← vault 사슬과 100% 정합
//   검증: h_i = SHA256(h_{i-1} ‖ qpc_i ‖ evict_i ‖ seq_i), h_0 = genesis, h_i == 저장 resultingHash_i.
//
//  ★불변 제약: δ=0 핫패스 — MMF 네이티브 포인터(byte*)로 직접 복사(Unsafe.CopyBlockUnaligned). managed byte[]
//    할당/박싱 0(사전할당 _recScratch 재사용·vault.CopyHeadHash 는 스팬 목적지에 직기입). 결정론(순수 해시).
//    ★unsafe 범위: MMF 페이지 포인터가 본질(기존 MmfIpcFabric 관례 연장). liveArmed:false(순수 영속 관측).
// ============================================================================
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>CausalAuditVault 의 MMF 영속 미러(비침습 데코레이터·Append-Only·δ=0 직기입). IDisposable.</summary>
    public sealed unsafe class NvmeAuditJournal : IDisposable
    {
        public const int  HeaderLen = 64;
        public const int  RecordLen = 56;                 // qpc(8)+evict(8)+seq(8)+hash(32)
        private const int HashLen   = 32;
        private const long Magic    = 0x4E564D45_4A524E4CL;   // "NVMEJRNL"
        private const long Version  = 8_00_00L;

        private readonly CausalAuditVault _vault;          // 감싸는 대상(0줄 수정)
        private readonly long _capacity;
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        private byte* _base;
        private long  _records;
        private int   _disposed;

        private readonly byte[] _recScratch = new byte[RecordLen];   // 사전할당 레코드 스테이징(δ=0)
        private readonly byte[] _vInput     = new byte[HashLen + 24];// 검증 전용 입력(라이브 무간섭)
        private readonly byte[] _vRun       = new byte[HashLen];     // 검증 러닝 해시
        private readonly byte[] _genesis    = new byte[HashLen];

        /// <summary>신선한(append 이전) vault 를 감싸 저널을 연다 — 제네시스를 정확히 캡처하기 위함.</summary>
        public NvmeAuditJournal(CausalAuditVault vault, string path, long capacityRecords = 65536)
        {
            _vault = vault ?? throw new ArgumentNullException(nameof(vault));
            if (capacityRecords < 1) capacityRecords = 1;
            _capacity = capacityRecords;
            long totalBytes = HeaderLen + capacityRecords * RecordLen;

            _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Create, mapName: null, totalBytes, MemoryMappedFileAccess.ReadWrite);
            // ★예외 안전: CreateFromFile 이후 어느 단계(뷰 매핑·포인터 획득·헤더 기입)가 throw 하면 생성자가 전파되어
            //   Dispose 가 결코 불리지 않는다 → 획득분 해제 + orphan 파일 삭제 후 재전파(자원·파일 누수 차단).
            try
            {
                _accessor = _mmf.CreateViewAccessor(0, totalBytes, MemoryMappedFileAccess.ReadWrite);
                byte* p = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                _base = p + _accessor.PointerOffset;

                // 제네시스 캡처(신선 vault 헤드 = 제네시스) → 헤더 기입.
                _vault.CopyHeadHash(_genesis);
                *(long*)(_base + 0)  = Magic;
                *(long*)(_base + 8)  = Version;
                *(long*)(_base + 16) = capacityRecords;
                *(long*)(_base + 24) = 0L;
                Unsafe.CopyBlockUnaligned(ref *(_base + 32), ref MemoryMarshal.GetArrayDataReference(_genesis), HashLen);
            }
            catch
            {
                if (_base != null) { try { _accessor!.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { } _base = null; }
                _accessor?.Dispose();
                _mmf.Dispose();
                try { File.Delete(path); } catch { }
                throw;
            }
        }

        public long RecordsJournaled => Interlocked.Read(ref _records);
        public long Capacity         => _capacity;
        public bool Full             => Interlocked.Read(ref _records) >= _capacity;

        /// <summary>
        /// write-through 미러(δ=0): vault.TryAppend 성공 시 그 레코드(56B)를 MMF 네이티브 페이지에 직기입한다.
        /// vault 가 거부(봉인/만원)하거나 저널 용량 소진이면 false.
        /// </summary>
        public bool TryAppend(long qpc, long evictions, long seqOffset)
        {
            byte* b = _base;   // ★1회 스냅샷 + fail-closed 가드: Dispose 후/도중 무효 주소·해제 페이지로의 네이티브 쓰기 차단.
            if (Volatile.Read(ref _disposed) != 0 || b == null) return false;
            if (Interlocked.Read(ref _records) >= _capacity) return false;
            if (!_vault.TryAppend(qpc, evictions, seqOffset)) return false;   // 원장 사슬 확장(원본 무접촉)

            long idx = Interlocked.Read(ref _records);
            var rec = _recScratch.AsSpan();
            BinaryPrimitives.WriteInt64LittleEndian(rec,            qpc);
            BinaryPrimitives.WriteInt64LittleEndian(rec.Slice(8),   evictions);
            BinaryPrimitives.WriteInt64LittleEndian(rec.Slice(16),  seqOffset);
            _vault.CopyHeadHash(rec.Slice(24, HashLen));                      // append 직후 헤드 = 결과 해시

            // ★네이티브 포인터로 직접 메모리 복사(OS 페이지 캐시 MMF 영역) — 중간 managed 할당 0.
            byte* dst = b + HeaderLen + (nint)idx * RecordLen;
            Unsafe.CopyBlockUnaligned(ref *dst, ref MemoryMarshal.GetArrayDataReference(_recScratch), RecordLen);

            long n = idx + 1;
            *(long*)(b + 24) = n;                                              // 헤더 count 갱신
            Interlocked.Exchange(ref _records, n);
            return true;
        }

        /// <summary>MMF 뷰를 디스크로 플러시(영속 동기화). 직렬 호출(호출자가 주기/비동기 결정).</summary>
        public void Flush()
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try { _accessor.Flush(); } catch { }
        }

        /// <summary>저널 레코드 독출(검증용·δ=0). hash 는 ≥32B.</summary>
        public bool ReadRecord(long i, out long qpc, out long evictions, out long seqOffset, Span<byte> hash)
        {
            qpc = evictions = seqOffset = 0;
            byte* b = _base;
            if (Volatile.Read(ref _disposed) != 0 || b == null) return false;
            if ((ulong)i >= (ulong)Interlocked.Read(ref _records) || hash.Length < HashLen) return false;
            byte* rec = b + HeaderLen + (nint)i * RecordLen;
            qpc        = *(long*)(rec + 0);
            evictions  = *(long*)(rec + 8);
            seqOffset  = *(long*)(rec + 16);
            new ReadOnlySpan<byte>(rec + 24, HashLen).CopyTo(hash);
            return true;
        }

        /// <summary>제네시스 앵커 복사(검증용).</summary>
        public bool CopyGenesis(Span<byte> dst)
        {
            if (dst.Length < HashLen) return false;
            _genesis.AsSpan(0, HashLen).CopyTo(dst);
            return true;
        }

        /// <summary>
        /// 저널 자기-정합 검증(δ=0): 제네시스부터 저장 (qpc,evict,seq) 로 SHA-256 사슬을 재계산해 저장 resultingHash 와
        /// 전량 대조한다. 최종 헤드는 <see cref="CopyLastHash"/> 로 노출 — 호출자가 vault 헤드와 교차 검증한다.
        /// </summary>
        public bool VerifyChain()
        {
            byte* b = _base;
            if (Volatile.Read(ref _disposed) != 0 || b == null) return false;
            long n = Interlocked.Read(ref _records);
            var run = _vRun.AsSpan(0, HashLen);
            _genesis.AsSpan(0, HashLen).CopyTo(run);
            var input = _vInput.AsSpan();
            Span<byte> stored = stackalloc byte[HashLen];
            for (long k = 0; k < n; k++)
            {
                byte* rec = b + HeaderLen + (nint)k * RecordLen;
                run.CopyTo(input.Slice(0, HashLen));
                new ReadOnlySpan<byte>(rec, 24).CopyTo(input.Slice(HashLen, 24));   // qpc|evict|seq
                SHA256.HashData(input.Slice(0, HashLen + 24), run);
                new ReadOnlySpan<byte>(rec + 24, HashLen).CopyTo(stored);
                if (!run.SequenceEqual(stored)) return false;
            }
            return true;
        }

        /// <summary>현재 저널 헤드 해시(마지막 레코드의 resultingHash, 없으면 제네시스)를 복사.</summary>
        public bool CopyLastHash(Span<byte> dst)
        {
            if (dst.Length < HashLen) return false;
            byte* b = _base;
            if (Volatile.Read(ref _disposed) != 0 || b == null) return false;
            long n = Interlocked.Read(ref _records);
            if (n == 0) { _genesis.AsSpan(0, HashLen).CopyTo(dst); return true; }
            byte* rec = b + HeaderLen + (nint)(n - 1) * RecordLen;
            new ReadOnlySpan<byte>(rec + 24, HashLen).CopyTo(dst);
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _accessor.Flush(); } catch { }
            if (_base != null) { try { _accessor.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { } _base = null; }
            _accessor.Dispose();
            _mmf.Dispose();
        }
    }
}
