using System;
using System.IO;
using Xunit;

namespace StockManager.Tests
{
    // Season 24 — 크래시 복구 원장 검증.
    //   §1 Crc32 표준 벡터  §2 JournalCodec 무할당 직렬화·torn 판정  §3 MMF 결속·부팅 fail-closed
    public sealed class Season24JournalTests
    {
        private static JournalRecord Rec(long seq) => new JournalRecord(
            seq, RecoveryKind.Rejection, stage: 2, promotion: 0,
            asOfTicks: seq * 1000, nowTicks: seq * 1000 + 877, windowTicks: 500,
            fitness: seq * 1.5, genomeHash: JournalRecord.HashId("g-" + seq));

        private static JournalRecord[] Recs(int n)
        {
            var a = new JournalRecord[n];
            for (int i = 0; i < n; i++) a[i] = Rec(i + 1);
            return a;
        }

        // ═══════════════════ §1 Crc32 ═══════════════════

        [Fact]
        public void Crc32_StandardCheckVector()
        {
            // "123456789" → 0xCBF43926 (IEEE 802.3 표준 체크값)
            byte[] data = System.Text.Encoding.ASCII.GetBytes("123456789");
            Assert.Equal(0xCBF43926u, Crc32.Compute(data));
        }

        [Fact]
        public void Crc32_Empty_IsZero()
            => Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));

        [Fact]
        public void Crc32_BitFlip_Changes()
        {
            byte[] a = { 1, 2, 3, 4 };
            byte[] b = { 1, 2, 3, 5 };
            Assert.NotEqual(Crc32.Compute(a), Crc32.Compute(b));
        }

        // ═══════════════════ §2 JournalCodec ═══════════════════

        [Fact]
        public void Codec_RoundTrip_Ok()
        {
            var recs = Recs(5);
            var buf  = new byte[JournalCodec.RequiredBytes(recs.Length)];
            int len  = JournalCodec.Encode(recs, buf);
            Assert.Equal(buf.Length, len);

            var dest = new JournalRecord[16];
            var st = JournalCodec.Decode(buf, dest, out int n);
            Assert.Equal(JournalStatus.Ok, st);
            Assert.Equal(5, n);
            Assert.Equal(recs[0].Seq, dest[0].Seq);
            Assert.Equal(recs[4].GenomeHash, dest[4].GenomeHash);
            Assert.Equal(recs[3].Fitness, dest[3].Fitness, 6);
        }

        [Fact]
        public void Codec_ZeroBuffer_IsEmpty()
            => Assert.Equal(JournalStatus.Empty, JournalCodec.Decode(new byte[64], new JournalRecord[4], out _));

        [Fact]
        public void Codec_TooSmall_IsEmpty()
            => Assert.Equal(JournalStatus.Empty, JournalCodec.Decode(new byte[4], new JournalRecord[4], out _));

        [Fact]
        public void Codec_BadMagic_Detected()
        {
            var buf = new byte[JournalCodec.RequiredBytes(2)];
            JournalCodec.Encode(Recs(2), buf);
            buf[0] ^= 0xFF;   // 매직 헤드 오염
            Assert.Equal(JournalStatus.BadMagic, JournalCodec.Decode(buf, new JournalRecord[4], out _));
        }

        [Fact]
        public void Codec_BitFlipInRecords_BadCrc()
        {
            var buf = new byte[JournalCodec.RequiredBytes(2)];
            JournalCodec.Encode(Recs(2), buf);
            buf[JournalCodec.HeaderSize + 3] ^= 0x01;   // 레코드 영역 1비트 오염
            Assert.Equal(JournalStatus.BadCrc, JournalCodec.Decode(buf, new JournalRecord[4], out _));
        }

        [Fact]
        public void Codec_TornTail_Truncated()
        {
            var buf = new byte[JournalCodec.RequiredBytes(2)];
            JournalCodec.Encode(Recs(2), buf);
            buf[buf.Length - 1] ^= 0xFF;   // 후미 매직 소실(torn write 시뮬)
            Assert.Equal(JournalStatus.Truncated, JournalCodec.Decode(buf, new JournalRecord[4], out _));
        }

        [Fact]
        public void Codec_ShortBuffer_Truncated()
        {
            var full = new byte[JournalCodec.RequiredBytes(3)];
            JournalCodec.Encode(Recs(3), full);
            // 후미가 잘린 버퍼(전원 차단 중 미완 기록) → 길이 부족
            var torn = full.AsSpan(0, full.Length - 6).ToArray();
            Assert.Equal(JournalStatus.Truncated, JournalCodec.Decode(torn, new JournalRecord[4], out _));
        }

        [Fact]
        public void Codec_DestSmallerThanCount_CopiesWhatFits()
        {
            var buf = new byte[JournalCodec.RequiredBytes(5)];
            JournalCodec.Encode(Recs(5), buf);
            var st = JournalCodec.Decode(buf, new JournalRecord[2], out int n);   // dest 2 < 5
            Assert.Equal(JournalStatus.Ok, st);
            Assert.Equal(2, n);
        }

        [Fact]
        public void Codec_EncodeDecode_ZeroAllocation()
        {
            // Mission 1 증명 — 사전 할당 span 위 동작 → per-op GC 할당 0
            var recs = Recs(8);
            var buf  = new byte[JournalCodec.RequiredBytes(recs.Length)];
            var dest = new JournalRecord[8];

            JournalCodec.Encode(recs, buf);            // 워밍업(JIT)
            JournalCodec.Decode(buf, dest, out _);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int k = 0; k < 200; k++)
            {
                JournalCodec.Encode(recs, buf);
                JournalCodec.Decode(buf, dest, out _);
            }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0L, delta);                   // 단 1바이트 할당 없음
        }

        [Fact]
        public void HashId_Stable_And_EmptyZero()
        {
            Assert.Equal(JournalRecord.HashId("genome-7"), JournalRecord.HashId("genome-7"));
            Assert.NotEqual(JournalRecord.HashId("a"), JournalRecord.HashId("b"));
            Assert.Equal(0L, JournalRecord.HashId(null));
            Assert.Equal(0L, JournalRecord.HashId(""));
        }

        // ═══════════════════ §3 CrashRecoveryJournaler (MMF) ═══════════════════

        private static string TempPath()
            => Path.Combine(Path.GetTempPath(), "ms2e_jrnl_" + Guid.NewGuid().ToString("N") + ".dat");

        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        [Fact]
        public void Journaler_PersistRecover_RoundTrip()
        {
            string path = TempPath();
            try
            {
                var j = new CrashRecoveryJournaler(path, capacity: 16);
                j.Persist(Recs(4));
                var dest = new JournalRecord[16];
                var st = j.Recover(dest, out int n);
                Assert.Equal(JournalStatus.Ok, st);
                Assert.Equal(4, n);
                Assert.Equal(4L, dest[3].Seq);
            }
            finally { TryDelete(path); }
        }

        [Fact]
        public void Journaler_NoFile_Empty_NoFreeze()
        {
            var j   = new CrashRecoveryJournaler(TempPath());   // 미존재
            var att = new OrderAttestationService();
            var st  = j.RecoverOrFreeze(att, new JournalRecord[8], out _);
            Assert.Equal(JournalStatus.Empty, st);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);   // 클린 부팅 — 동결 안 함
        }

        [Fact]
        public void Journaler_CorruptFile_RecoverOrFreeze_FailsClosed()
        {
            string path = TempPath();
            try
            {
                var j = new CrashRecoveryJournaler(path, capacity: 16);
                j.Persist(Recs(4));

                var bytes = File.ReadAllBytes(path);
                bytes[JournalCodec.HeaderSize + 2] ^= 0xFF;   // 레코드 영역 오염
                File.WriteAllBytes(path, bytes);

                var att = new OrderAttestationService();
                var st  = j.RecoverOrFreeze(att, new JournalRecord[16], out _);
                Assert.True(st.IsCorrupt());
                Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // 부팅 fail-closed
            }
            finally { TryDelete(path); }
        }

        [Fact]
        public void RecordSize_IsStableAndPositive()
            => Assert.True(JournalCodec.RecordSize >= 48);   // 6×long + 3×byte(+패딩)

        // ═══════════════════ §4 S24-b 자가당착 차단 (Inaccessible ≠ Corrupt) ═══════════════════

        [Fact]
        public void Inaccessible_IsNotCorrupt()
        {
            Assert.False(JournalStatus.Inaccessible.IsCorrupt());
            Assert.True(JournalStatus.Inaccessible.IsInaccessible());
            Assert.True(JournalStatus.BadCrc.IsCorrupt());
            Assert.False(JournalStatus.BadCrc.IsInaccessible());
        }

        [Fact]
        public void Journaler_IoLocked_Inaccessible_NoFreeze_FilePreserved()
        {
            // 미션3 — 백신 스캔/공유위반으로 저널을 못 읽어도 거래를 '엉뚱하게' 동결하지 않는다.
            string path = TempPath();
            try
            {
                var j = new CrashRecoveryJournaler(path, capacity: 16);
                j.Persist(Recs(3));

                using (var locker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var att = new OrderAttestationService();
                    var st  = j.RecoverOrFreeze(att, new JournalRecord[16], out _);
                    Assert.Equal(JournalStatus.Inaccessible, st);
                    Assert.False(st.IsCorrupt());
                    Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);   // 자가당착 차단 — 무동결
                }
                Assert.True(File.Exists(path));   // 비파괴 — 파일 보존
            }
            finally { TryDelete(path); }
        }

        [Fact]
        public void Codec_VersionSkew_TreatedAsEmpty_NotCorrupt()
        {
            // S24-c — 매직 맞고 버전만 다른 '구 포맷'은 양성 스큐 → Empty(무동결). torn/변조 오염과 분리.
            var buf = new byte[JournalCodec.RequiredBytes(3)];
            JournalCodec.Encode(Recs(3), buf);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), 99);   // 다른 버전
            var st = JournalCodec.Decode(buf, new JournalRecord[8], out _);
            Assert.Equal(JournalStatus.Empty, st);
            Assert.False(st.IsCorrupt());
        }

        [Fact]
        public void Journaler_VersionSkewFile_NoFreeze()
        {
            // 포맷 업그레이드가 멀쩡한 시스템을 부팅 동결시키지 않는다(자가당착 차단).
            string path = TempPath();
            try
            {
                var j = new CrashRecoveryJournaler(path, capacity: 16);
                j.Persist(Recs(3));
                var bytes = File.ReadAllBytes(path);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1);   // 구 버전 위장
                File.WriteAllBytes(path, bytes);

                var att = new OrderAttestationService();
                var st  = j.RecoverOrFreeze(att, new JournalRecord[16], out _);
                Assert.Equal(JournalStatus.Empty, st);
                Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);   // 동결 안 함
            }
            finally { TryDelete(path); }
        }

        [Fact]
        public void Journaler_TornTail_FailsClosed()
        {
            // 미션2 — 불결한 크래시(torn write·후미 소실)는 동결한다.
            string path = TempPath();
            try
            {
                var j = new CrashRecoveryJournaler(path, capacity: 16);
                j.Persist(Recs(3));
                var bytes = File.ReadAllBytes(path);
                int tailPos = JournalCodec.HeaderSize + 3 * JournalCodec.RecordSize;   // 후미 매직 위치
                bytes[tailPos] ^= 0xFF;
                File.WriteAllBytes(path, bytes);

                var att = new OrderAttestationService();
                var st  = j.RecoverOrFreeze(att, new JournalRecord[16], out _);
                Assert.Equal(JournalStatus.Truncated, st);
                Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // 불결한 종료 → fail-closed
            }
            finally { TryDelete(path); }
        }
    }
}
