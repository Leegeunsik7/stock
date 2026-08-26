// ============================================================================
// CompositeForensicLedger — S24-c: 복합 저널 레이아웃 (게놈 섹터 + 포렌식 순환 큐 섹터)
//
//  단일 MMF 안에 챔피언 게놈 스냅샷과 RejectionRecord 배열을 '고정 크기 섹터'로 공존시킨다.
//  각 섹터가 독립 매직+CRC32 를 가지므로, '풀사이즈 backing'(후미 0·비트 플립 등 길이는 온전하나 내용이
//  오염된 모드)에서는 포렌식 링이 깨져도 게놈 스냅샷은 복구된다(섹터 독립 복원). 단, 파일 '길이 절단'(torn
//  truncation)에서는 파일 레벨 길이 가드(src.Length<total)가 섹터 검증보다 먼저 발동해 전 섹터를 보수적으로
//  Truncated 처리하므로 게놈도 폐기된다 — 절단 부팅은 폐기가 정석이고 어차피 AnyCorrupt→fail-closed 다.
//
//  ── 결정론 오프셋(고정 크기 섹터) ──────────────────────────────────────────
//    [FileMagic 4][Version 4]                          ← 파일 헤더(8)
//    [GenomeMagic 4][CRC 4][GenomeSnapshot G]          ← 게놈 섹터(8+G)
//    [ForensicMagic 4][CRC 4][Count 4][JournalRecord × Cap]  ← 포렌식 섹터(12+Cap·R)
//    [FileTail 4]                                      ← 파일 후미(4)
//  총 크기는 Cap 으로 완전 결정 — 오프셋이 부팅마다 동일하다. 모든 구조체는 Pack=1(패딩 0).
//
//  ── 오버플로 클램프/회전 ────────────────────────────────────────────────────
//  Encode 는 recs 가 Cap 초과 시 '최신 Cap 개'만 직렬화(슬라이스). 디스크 경계 초과 불가.
//  Count<0 ∨ Count>Cap ∨ CRC 불일치 ∨ 매직 위반 → 해당 섹터 Corrupt(다른 섹터는 독립 판정).
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace StockManager.Common
{
    /// <summary>챔피언 게놈의 블리터블 스냅샷(Pack=1·참조 0). 전체 StrategySpec 그래프 대신 식별 해시+메트릭.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct GenomeSnapshot
    {
        public readonly long Seq;
        public readonly long GenomeHash;    // FNV-1a(SpecId)
        public readonly long FitnessBits;   // DoubleToInt64Bits
        public readonly long AsOfTicks;
        public readonly byte Promotion;     // PromotionState

        public GenomeSnapshot(long seq, long genomeHash, double fitness, long asOfTicks, byte promotion)
        {
            Seq = seq; GenomeHash = genomeHash;
            FitnessBits = BitConverter.DoubleToInt64Bits(fitness);
            AsOfTicks = asOfTicks; Promotion = promotion;
        }

        public bool   HasValue => Seq > 0;
        public double Fitness  => BitConverter.Int64BitsToDouble(FitnessBits);
    }

    /// <summary>복합 디코드 결과 — 섹터별 독립 상태. 포렌식 손상에도 게놈은 살아남을 수 있다.</summary>
    public readonly struct CompositeResult
    {
        public readonly JournalStatus  File;
        public readonly JournalStatus  Genome;
        public readonly JournalStatus  Forensic;
        public readonly GenomeSnapshot GenomeRecord;
        public readonly int            ForensicCount;

        public CompositeResult(JournalStatus file, JournalStatus genome, JournalStatus forensic,
                               GenomeSnapshot genomeRecord, int forensicCount)
        {
            File = file; Genome = genome; Forensic = forensic;
            GenomeRecord = genomeRecord; ForensicCount = forensicCount;
        }

        /// <summary>어느 섹터든 내용 오염이면 true(부팅 fail-closed 판단). 게놈 값은 무관하게 복구 가능.</summary>
        public bool AnyCorrupt => File.IsCorrupt() || Genome.IsCorrupt() || Forensic.IsCorrupt();
    }

    public static class CompositeJournalCodec
    {
        public const uint FileMagic     = 0x4D534331u;  // "MSC1"
        public const uint FileTail      = 0x4C454447u;  // "LEDG"
        public const uint GenomeMagic   = 0x474E4F4Du;  // "GNOM"
        public const uint ForensicMagic = 0x46524E53u;  // "FRNS"
        public const int  Version       = 1;

        public static readonly int GenomeSize = Unsafe.SizeOf<GenomeSnapshot>();
        public static readonly int RecordSize = Unsafe.SizeOf<JournalRecord>();

        private const int FileHdr     = 8;    // FileMagic + Version
        private const int GenomeHdr   = 8;    // GenomeMagic + CRC
        private const int ForensicHdr = 12;   // ForensicMagic + CRC + Count
        private const int TailSize    = 4;

        public static int RequiredBytes(int forensicCapacity)
            => FileHdr + GenomeHdr + GenomeSize + ForensicHdr + forensicCapacity * RecordSize + TailSize;

        /// <summary>게놈 스냅샷 + 포렌식 레코드를 고정 섹터로 직렬화(무할당). 총 길이 반환. recs 초과분은 최신 Cap 개로 클램프.</summary>
        public static int Encode(in GenomeSnapshot genome, ReadOnlySpan<JournalRecord> recs,
                                 int forensicCapacity, Span<byte> dest)
        {
            if (forensicCapacity < 1) forensicCapacity = 1;
            int total = RequiredBytes(forensicCapacity);
            if (dest.Length < total) throw new ArgumentException("복합 저널 대상 버퍼 부족", nameof(dest));

            var src = recs.Length > forensicCapacity ? recs.Slice(recs.Length - forensicCapacity) : recs;   // 최신 Cap(오버플로 클램프)
            int n   = src.Length;

            int o = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(o), FileMagic);   o += 4;
            BinaryPrimitives.WriteInt32LittleEndian (dest.Slice(o), Version);     o += 4;

            // ── 게놈 섹터 ──
            int gMagicOff = o; o += 4;
            int gCrcOff   = o; o += 4;
            var gRegion = dest.Slice(o, GenomeSize);
            MemoryMarshal.Write(gRegion, in genome);
            o += GenomeSize;
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(gMagicOff), GenomeMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(gCrcOff),   Crc32.Compute(gRegion));

            // ── 포렌식 섹터 ──
            int fMagicOff = o; o += 4;
            int fCrcOff   = o; o += 4;
            int fCountOff = o; o += 4;
            var fRegion = dest.Slice(o, forensicCapacity * RecordSize);
            for (int i = 0; i < n; i++)
                MemoryMarshal.Write(fRegion.Slice(i * RecordSize), in src[i]);
            o += forensicCapacity * RecordSize;
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(fMagicOff), ForensicMagic);
            BinaryPrimitives.WriteInt32LittleEndian (dest.Slice(fCountOff), n);
            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(fCrcOff),   Crc32.Compute(fRegion.Slice(0, n * RecordSize)));

            BinaryPrimitives.WriteUInt32LittleEndian(dest.Slice(o), FileTail); o += 4;
            return total;
        }

        /// <summary>복합 저널을 섹터별 독립 검증·복호화. 포렌식 손상에도 게놈은 복구된다(섹터 독립). 무할당.</summary>
        public static CompositeResult Decode(ReadOnlySpan<byte> src, int forensicCapacity,
                                             Span<JournalRecord> recsDest, out int recCount)
        {
            recCount = 0;
            if (forensicCapacity < 1) forensicCapacity = 1;

            if (src.Length < FileHdr)
                return new CompositeResult(JournalStatus.Empty, JournalStatus.Empty, JournalStatus.Empty, default, 0);

            uint fmagic = BinaryPrimitives.ReadUInt32LittleEndian(src);
            if (fmagic == 0u)
                return new CompositeResult(JournalStatus.Empty, JournalStatus.Empty, JournalStatus.Empty, default, 0);
            // S25-b 마이그레이션 — 구 플랫 포맷(JournalCodec.MagicHead "MS2C")은 torn/변조 오염이 아니라 양성 구포맷.
            // 동결하지 말고 Empty(폐기→복합 재생성 신호)로 그레이스풀 흡수(v4.24 버전 스큐 사상 일관). 미상 매직만 BadMagic.
            if (fmagic == JournalCodec.MagicHead)
                return new CompositeResult(JournalStatus.Empty, JournalStatus.Empty, JournalStatus.Empty, default, 0);
            if (fmagic != FileMagic)
                return new CompositeResult(JournalStatus.BadMagic, JournalStatus.BadMagic, JournalStatus.BadMagic, default, 0);
            // S24-c — 매직 맞으나 버전 스큐(우리 구 포맷)는 오염 아님 → 폐기·클린(무동결). 플랫 코덱과 동일 사상.
            if (BinaryPrimitives.ReadInt32LittleEndian(src.Slice(4)) != Version)
                return new CompositeResult(JournalStatus.Empty, JournalStatus.Empty, JournalStatus.Empty, default, 0);

            int total = RequiredBytes(forensicCapacity);
            if (src.Length < total)
                return new CompositeResult(JournalStatus.Truncated, JournalStatus.Truncated, JournalStatus.Truncated, default, 0);

            var fileStatus = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(total - TailSize)) == FileTail
                ? JournalStatus.Ok : JournalStatus.Truncated;

            // ── 게놈 섹터(독립) ──
            int gMagicOff = FileHdr;
            int gCrcOff   = FileHdr + 4;
            int gOff      = FileHdr + GenomeHdr;
            var gRegion   = src.Slice(gOff, GenomeSize);
            JournalStatus  gStatus;
            GenomeSnapshot genome = default;
            if (BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(gMagicOff)) != GenomeMagic)
                gStatus = JournalStatus.BadMagic;
            else if (Crc32.Compute(gRegion) != BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(gCrcOff)))
                gStatus = JournalStatus.BadCrc;
            else { genome = MemoryMarshal.Read<GenomeSnapshot>(gRegion); gStatus = genome.HasValue ? JournalStatus.Ok : JournalStatus.Empty; }

            // ── 포렌식 섹터(독립) ──
            int fMagicOff = gOff + GenomeSize;
            int fCrcOff   = fMagicOff + 4;
            int fCountOff = fMagicOff + 8;
            int fDataOff  = fMagicOff + ForensicHdr;
            JournalStatus fStatus;
            if (BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(fMagicOff)) != ForensicMagic)
                fStatus = JournalStatus.BadMagic;
            else
            {
                int n = BinaryPrimitives.ReadInt32LittleEndian(src.Slice(fCountOff));
                if (n < 0 || n > forensicCapacity)
                    fStatus = JournalStatus.Truncated;
                else
                {
                    var fData = src.Slice(fDataOff, n * RecordSize);
                    if (Crc32.Compute(fData) != BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(fCrcOff)))
                        fStatus = JournalStatus.BadCrc;
                    else
                    {
                        int copy = Math.Min(n, recsDest.Length);
                        for (int i = 0; i < copy; i++)
                            recsDest[i] = MemoryMarshal.Read<JournalRecord>(fData.Slice(i * RecordSize));
                        recCount = copy;
                        fStatus  = n == 0 ? JournalStatus.Empty : JournalStatus.Ok;
                    }
                }
            }

            return new CompositeResult(fileStatus, gStatus, fStatus, genome, recCount);
        }
    }
}
