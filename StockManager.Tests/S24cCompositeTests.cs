using System;
using System.IO;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    // S24-c — 전방위 트리거 + 복합 저널 + Pack=1.
    //   §1 Pack=1 패딩 사살(크기·결정론)  §2 복합 레이아웃(섹터 독립 복원·오버플로·라운드트립)
    //   §3 다중 소스 RequestFlush 레이스(Interlocked 손실 0)  §4 PromotionGateway OnLedgerChanged 트리거
    public sealed class S24cCompositeTests
    {
        private static string TempPath()
            => Path.Combine(Path.GetTempPath(), "ms2e_s24c_" + Guid.NewGuid().ToString("N") + ".dat");
        private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        private static JournalRecord JR(long seq) => new JournalRecord(
            seq, RecoveryKind.Rejection, 2, 0, seq * 1000, seq * 1000 + 877, 500, seq * 1.5, seq * 7);
        private static JournalRecord[] JRecs(int n)
        {
            var a = new JournalRecord[n];
            for (int i = 0; i < n; i++) a[i] = JR(i + 1);
            return a;
        }
        private static GenomeSnapshot Genome(long seq) =>
            new GenomeSnapshot(seq, genomeHash: seq * 31, fitness: seq * 0.25, asOfTicks: seq * 1000, promotion: 3);

        private sealed class FakeProvider : IJournalSnapshotProvider
        {
            public int CaptureInto(Span<JournalRecord> dest)
            {
                int k = Math.Min(2, dest.Length);
                for (int i = 0; i < k; i++) dest[i] = JR(i + 1);
                return k;
            }
        }

        // ═══════════════════ §1 Pack=1 ═══════════════════

        [Fact]
        public void JournalRecord_Pack1_Size51_NoPadding()
            => Assert.Equal(51, JournalCodec.RecordSize);   // 6×long+3×byte. 비-Pack1이면 56(패딩 5).

        [Fact]
        public void GenomeSnapshot_Pack1_Size33()
            => Assert.Equal(33, CompositeJournalCodec.GenomeSize);   // 4×long+1×byte

        [Fact]
        public void Codec_Encode_FromDirtyBuffer_Deterministic()
        {
            // 더티 버퍼(0xFF)에서 인코딩해도 클린 버퍼와 바이트 동일 → 패딩 갭 없음(CRC 부팅 안정).
            var recs = JRecs(4);
            int sz = JournalCodec.RequiredBytes(4);
            var clean = new byte[sz];
            var dirty = new byte[sz]; Array.Fill(dirty, (byte)0xFF);
            JournalCodec.Encode(recs, clean);
            JournalCodec.Encode(recs, dirty);
            Assert.Equal(clean, dirty);
        }

        // ═══════════════════ §2 CompositeJournalCodec ═══════════════════

        [Fact]
        public void Composite_RoundTrip_BothSectors()
        {
            const int cap = 16;
            var buf = new byte[CompositeJournalCodec.RequiredBytes(cap)];
            CompositeJournalCodec.Encode(Genome(42), JRecs(3), cap, buf);

            var dest = new JournalRecord[cap];
            var r = CompositeJournalCodec.Decode(buf, cap, dest, out int n);
            Assert.Equal(JournalStatus.Ok, r.File);
            Assert.Equal(JournalStatus.Ok, r.Genome);
            Assert.Equal(JournalStatus.Ok, r.Forensic);
            Assert.False(r.AnyCorrupt);
            Assert.Equal(42, r.GenomeRecord.Seq);
            Assert.Equal(42 * 0.25, r.GenomeRecord.Fitness, 6);
            Assert.Equal(3, n);
            Assert.Equal(3, dest[2].Seq);
        }

        [Fact]
        public void Composite_GenomeSurvives_ForensicCorruption()
        {
            // 섹터 독립 복원 — 포렌식 링이 torn 돼도 게놈은 복구된다.
            const int cap = 16;
            var buf = new byte[CompositeJournalCodec.RequiredBytes(cap)];
            CompositeJournalCodec.Encode(Genome(7), JRecs(3), cap, buf);

            int forensicDataOff = 8 + 8 + CompositeJournalCodec.GenomeSize + 12;   // 파일hdr+게놈hdr+게놈+포렌식hdr
            buf[forensicDataOff + 2] ^= 0xFF;   // 포렌식 데이터 1비트 오염

            var dest = new JournalRecord[cap];
            var r = CompositeJournalCodec.Decode(buf, cap, dest, out _);
            Assert.Equal(JournalStatus.BadCrc, r.Forensic);   // 포렌식 손상
            Assert.Equal(JournalStatus.Ok, r.Genome);         // ★게놈은 생존
            Assert.Equal(7, r.GenomeRecord.Seq);
            Assert.True(r.AnyCorrupt);                         // 부팅 판단은 보수적(동결)
        }

        [Fact]
        public void Composite_ForensicSurvives_GenomeCorruption()
        {
            const int cap = 16;
            var buf = new byte[CompositeJournalCodec.RequiredBytes(cap)];
            CompositeJournalCodec.Encode(Genome(7), JRecs(3), cap, buf);

            buf[16 + 2] ^= 0xFF;   // 게놈 데이터(오프셋 16~) 1비트 오염

            var dest = new JournalRecord[cap];
            var r = CompositeJournalCodec.Decode(buf, cap, dest, out int n);
            Assert.Equal(JournalStatus.BadCrc, r.Genome);     // 게놈 손상
            Assert.Equal(JournalStatus.Ok, r.Forensic);       // ★포렌식은 생존
            Assert.Equal(3, n);
        }

        [Fact]
        public void Composite_OverflowClamp_KeepsNewest()
        {
            const int cap = 16;
            var buf = new byte[CompositeJournalCodec.RequiredBytes(cap)];
            CompositeJournalCodec.Encode(Genome(1), JRecs(100), cap, buf);   // 100 > cap

            var dest = new JournalRecord[cap];
            var r = CompositeJournalCodec.Decode(buf, cap, dest, out int n);
            Assert.Equal(JournalStatus.Ok, r.Forensic);
            Assert.Equal(cap, n);                  // Cap 으로 클램프
            Assert.Equal(100, dest[cap - 1].Seq);  // 최신(100번째) 보존
            Assert.Equal(85, dest[0].Seq);         // 100 - 16 + 1
        }

        [Fact]
        public void Composite_ZeroBuffer_AllEmpty()
        {
            var r = CompositeJournalCodec.Decode(new byte[256], 16, new JournalRecord[16], out _);
            Assert.Equal(JournalStatus.Empty, r.File);
            Assert.False(r.AnyCorrupt);
        }

        [Fact]
        public void Composite_BadFileMagic_AllBadMagic()
        {
            const int cap = 16;
            var buf = new byte[CompositeJournalCodec.RequiredBytes(cap)];
            CompositeJournalCodec.Encode(Genome(1), JRecs(2), cap, buf);
            buf[0] ^= 0xFF;
            var r = CompositeJournalCodec.Decode(buf, cap, new JournalRecord[cap], out _);
            Assert.True(r.AnyCorrupt);
            Assert.Equal(JournalStatus.BadMagic, r.File);
        }

        [Fact]
        public void Composite_VersionSkew_AllEmpty_NotCorrupt()
        {
            const int cap = 8;
            var buf = new byte[CompositeJournalCodec.RequiredBytes(cap)];
            CompositeJournalCodec.Encode(Genome(1), JRecs(2), cap, buf);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), 99);   // 버전 스큐
            var r = CompositeJournalCodec.Decode(buf, cap, new JournalRecord[cap], out _);
            Assert.Equal(JournalStatus.Empty, r.File);
            Assert.False(r.AnyCorrupt);   // 양성 스큐 → 무동결
        }

        [Fact]
        public void Composite_DirtyBuffer_Deterministic()
        {
            const int cap = 8;
            int sz = CompositeJournalCodec.RequiredBytes(cap);
            var clean = new byte[sz];
            var dirty = new byte[sz]; Array.Fill(dirty, (byte)0xAA);
            CompositeJournalCodec.Encode(Genome(5), JRecs(3), cap, clean);
            CompositeJournalCodec.Encode(Genome(5), JRecs(3), cap, dirty);
            // CRC 대상 영역(게놈·n레코드)이 결정론이라 디코드 결과는 동일(미사용 tail 의 더티는 CRC 무관)
            var d1 = new JournalRecord[cap]; var r1 = CompositeJournalCodec.Decode(clean, cap, d1, out int n1);
            var d2 = new JournalRecord[cap]; var r2 = CompositeJournalCodec.Decode(dirty, cap, d2, out int n2);
            Assert.Equal(JournalStatus.Ok, r1.Forensic);
            Assert.Equal(JournalStatus.Ok, r2.Forensic);
            Assert.Equal(n1, n2);
            Assert.Equal(r1.GenomeRecord.Seq, r2.GenomeRecord.Seq);
        }

        // ═══════════════════ §3 다중 소스 RequestFlush 레이스 ═══════════════════

        [Fact]
        public void Pump_MultiThread_RequestFlush_NoLostIncrements()
        {
            // 미션1 — 온실·UI·게이트웨이 임계구역이 동시 RequestFlush 해도 Interlocked 로 손실 0.
            string path = TempPath();
            try
            {
                using var pump = new JournalFlushPump(new CrashRecoveryJournaler(path, 16), new FakeProvider(), 16);
                const int threads = 8, per = 20000;
                var ts = new Thread[threads];
                for (int i = 0; i < threads; i++)
                {
                    ts[i] = new Thread(() => { for (int k = 0; k < per; k++) pump.RequestFlush(); }) { IsBackground = true };
                    ts[i].Start();
                }
                foreach (var t in ts) t.Join(5000);
                Assert.Equal((long)threads * per, pump.DirtyGen);   // 80만 누적, 손실 0
            }
            finally { TryDelete(path); }
        }

        // ═══════════════════ §4 PromotionGateway OnLedgerChanged ═══════════════════

        [Fact]
        public void Gateway_OnLedgerChanged_FiresOnApproveAndActivate()
        {
            int fired = 0;
            var slot = new StrategySlot();
            var gw = new PromotionGateway(null, null, s => s == "CMDR") { OnLedgerChanged = () => fired++ };
            gw.Submit(GreenhouseActivator.DefaultSeeds()[0], 1.0);
            Assert.True(gw.Approve("CMDR"));    // 트리거 1
            Assert.True(gw.Activate(slot));     // 트리거 2
            Assert.Equal(2, fired);
        }

        [Fact]
        public void Gateway_OnLedgerChanged_FiresOnStaleRejection()
        {
            int fired = 0;
            DateTime now = new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);
            var gw = new PromotionGateway(null, null, s => s == "CMDR")
            {
                ProposalFreshnessWindow = TimeSpan.FromMinutes(5),
                Clock = () => now,
                Forensics = new RejectionForensicAuditor(),
                OnLedgerChanged = () => fired++,
            };
            gw.Submit(GreenhouseActivator.DefaultSeeds()[0], 1.0, now.Ticks);
            now = now.AddMinutes(10);           // 만료
            Assert.False(gw.Approve("CMDR"));   // stale 거부 → RecordRejection → 트리거
            Assert.True(fired >= 1);
        }
    }
}
