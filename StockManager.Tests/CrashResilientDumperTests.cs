// ============================================================================
// CrashResilientDumperTests — v4.95.0 과제2: 동결 원자 덤프·암호 봉인
//
//  §1 인코드/디코드·무결성    — 라운드트립·빈 덤프·CRC 오염·SHA256 변조 탐지.
//  §2 동결 옵저버 훅          — RegisterFreezeObserver 격발·예외 무해.
//  §3(별도) 동결→봉인→덤프 통합·실패 시 OS핸들 abort+격리(ConcurrencyStress).
// ============================================================================
using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class CrashResilientDumperTests
    {
        private static FlightRecordEvent Ev(long seq, long sym, byte kind)
            => new(seq, 1000 + seq, sym, seq * 10, seq * 20, seq * 30, 7, 1, kind, 0);

        // ── §1 인코드/디코드 · 무결성 ──────────────────────────────────────────

        [Fact]
        public void Encode_Decode_RoundTrip()
        {
            var events = new[] { Ev(1, 5930, (byte)FlightEventKind.RiskAdmit), Ev(2, 660, (byte)FlightEventKind.MmfPublish), Ev(3, 5, (byte)FlightEventKind.Freeze) };
            byte[] file = CrashResilientDumper.Encode(events, 0x1234_5678_9ABC_DEF0L);
            Assert.True(CrashResilientDumper.TryLoadAndVerify(file, out var back, out var err), err);
            Assert.Equal(3, back.Length);
            Assert.Equal(1L, back[0].Sequence);
            Assert.Equal(5930L, back[0].SymbolCode);
            Assert.Equal(FlightEventKind.MmfPublish, back[1].Event);
            Assert.Equal(20L, back[1].Arg0);   // seq2(=2) × 10 · XOR 왕복 무손실
            Assert.Equal(FlightEventKind.Freeze, back[2].Event);
        }

        [Fact]
        public void Encode_Empty_RoundTrip()
        {
            byte[] file = CrashResilientDumper.Encode(ReadOnlySpan<FlightRecordEvent>.Empty, 42L);
            Assert.True(CrashResilientDumper.TryLoadAndVerify(file, out var back, out _));
            Assert.Empty(back);
        }

        [Fact]
        public void Payload_Obfuscated_NotPlaintext()
        {
            var events = new[] { Ev(1, 0x5930, (byte)FlightEventKind.RiskAdmit) };
            byte[] file = CrashResilientDumper.Encode(events, 0xDEAD_BEEFL);
            // 페이로드(HeaderSize=56 이후)에 심볼 raw 리틀엔디안(0x5930)이 평문으로 남아있지 않아야 함(XOR 난독).
            var payload = file.AsSpan(56, file.Length - 56 - 4);
            bool plaintextSym = false;
            for (int i = 0; i + 1 < payload.Length; i++)
                if (payload[i] == 0x30 && payload[i + 1] == 0x59) { plaintextSym = true; break; }
            Assert.False(plaintextSym);   // XOR 로 평문 육안 판독 차단
        }

        [Fact]
        public void CorruptPayload_Crc32_Detects()
        {
            var events = new[] { Ev(1, 5930, (byte)FlightEventKind.RiskAdmit), Ev(2, 1, (byte)FlightEventKind.MmfPoison) };
            byte[] file = CrashResilientDumper.Encode(events, 7L);
            file[60] ^= 0xFF;   // 페이로드 1바이트 오염(CRC 미수정)
            Assert.False(CrashResilientDumper.TryLoadAndVerify(file, out _, out var err));
            Assert.Contains("CRC", err);
        }

        [Fact]
        public void Tamper_BypassCrc_Sha256_Detects()
        {
            var events = new[] { Ev(1, 5930, (byte)FlightEventKind.RiskAdmit), Ev(2, 1, (byte)FlightEventKind.MmfPoison) };
            byte[] file = CrashResilientDumper.Encode(events, 7L);
            file[60] ^= 0xFF;   // 페이로드 변조
            // CRC 를 재계산해 트레일러에 덮어 CRC 게이트 우회 → SHA256 이 잡아야 함.
            uint crc = Crc32.Compute(file.AsSpan(0, file.Length - 4));
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(file.Length - 4), crc);
            Assert.False(CrashResilientDumper.TryLoadAndVerify(file, out _, out var err));
            Assert.Contains("SHA256", err);   // ★변조 탐지(무결성 최종 방어)
        }

        [Fact]
        public void BadMagic_Rejected()
        {
            var file = new byte[56 + 4];
            Assert.False(CrashResilientDumper.TryLoadAndVerify(file, out _, out var err));
            Assert.Contains("매직", err);
        }

        [Fact]
        public void OverflowCount_Rejected_NoOom()
        {
            // ★적대검증 회귀(C#9/C#10): 조작 count(2^26+1)로 int 오버플로→소형파일 통과→수GB 배열 할당(OOM) 방지.
            //   124바이트 파일(56+64+4)에 count=2^26+1 세팅 후 CRC 재계산으로 CRC 게이트 우회 시도 → 길이 검증(long)이 먼저 거부.
            byte[] file = CrashResilientDumper.Encode(new[] { Ev(1, 5930, (byte)FlightEventKind.Freeze) }, 1L);  // 정상 1레코드(124B)
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(12), 67_108_865);   // count 오염(2^26+1 → ×64 int 래핑=64)
            uint crc = Crc32.Compute(file.AsSpan(0, file.Length - 4));               // CRC 재계산(비인증 우회)
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(file.Length - 4), crc);
            Assert.False(CrashResilientDumper.TryLoadAndVerify(file, out _, out var err));   // ★크래시 없이 거부
            Assert.Contains("길이", err);
        }

        [Fact]
        public void NegativeCount_Rejected()
        {
            byte[] file = CrashResilientDumper.Encode(new[] { Ev(1, 1, (byte)FlightEventKind.Freeze) }, 1L);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(12), -5);
            Assert.False(CrashResilientDumper.TryLoadAndVerify(file, out _, out var err));
            Assert.Contains("카운트", err);
        }

        // ── §2 동결 옵저버 훅 ──────────────────────────────────────────────────

        [Fact]
        public void RegisterFreezeObserver_FiresOnForceFreeze()
        {
            var att = new OrderAttestationService();
            int fired = 0; string note = "";
            att.RegisterFreezeObserver(e => { Interlocked.Increment(ref fired); note = e.Note; });
            att.ForceFreeze("테스트 동결");
            Assert.Equal(1, fired);
            Assert.Equal("테스트 동결", note);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void FreezeObserver_Exception_DoesNotBreakFreeze()
        {
            var att = new OrderAttestationService();
            att.RegisterFreezeObserver(_ => throw new InvalidOperationException("boom"));
            att.ForceFreeze("x");   // 옵저버 예외가 동결 완료를 막지 않아야 함
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }
    }

    // ── §3 동결→봉인→덤프 통합 · 실패 시 격리 ──────────────────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class CrashResilientDumperIntegrationTests
    {
        [Fact]
        public void Freeze_Seals_And_DumpsVerifiableFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ms2dumptest_" + Guid.NewGuid().ToString("N"));
            try
            {
                var rec = new MmfFlightRecorder(64, () => 1000L);
                for (int i = 0; i < 20; i++) rec.Record(FlightEventKind.RiskAdmit, i, i * 10, i * 20);
                var att = new OrderAttestationService();
                using var dumper = new CrashResilientDumper(rec, att, dumpDir: dir, nowTicks: () => 777_000L);
                dumper.Start();

                att.ForceFreeze("통합 테스트 동결");   // 옵저버 → Seal + 신호 → 격리 스레드 덤프

                // 덤프 완료 폴링(격리 스레드·바운드 대기).
                bool done = SpinUntil(() => dumper.State == BlackboxDumpState.Completed || dumper.State == BlackboxDumpState.Failed, 10_000);
                Assert.True(done, "덤프 미완료");
                Assert.Equal(BlackboxDumpState.Completed, dumper.State);
                Assert.True(rec.IsSealed);                                 // ★봉인됨
                Assert.True(File.Exists(dumper.DumpPath));                 // 파일 생성
                Assert.True(CrashResilientDumper.TryLoadAndVerify(dumper.DumpPath, out var back, out var err), err);
                Assert.Equal(20, back.Length);                             // 20 레코드 검증 복원
                Assert.Equal(FlightEventKind.RiskAdmit, back[0].Event);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void DumpFailure_BadPath_AbortsAndSeals_AndFreezes()
        {
            // dumpDir 를 '파일'로 지정 → FileStream 생성 예외 → OS 핸들 abort + 링 봉인 + att 동결 + State=Failed.
            string fileAsDir = Path.Combine(Path.GetTempPath(), "ms2dumpfile_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(fileAsDir, "not a directory");
            try
            {
                var rec = new MmfFlightRecorder(16, () => 1L);
                rec.Record(FlightEventKind.Freeze, 1, 0, 0);
                var att = new OrderAttestationService();
                using var dumper = new CrashResilientDumper(rec, att, dumpDir: fileAsDir, nowTicks: () => 5L);
                dumper.Start();      // ★옵저버 등록(C#4 재진입 경로 활성화)
                dumper.DumpOnce();   // 동기 직접 호출(결정론) — catch 의 재진입 ForceFreeze 가 옵저버를 격발
                Assert.Equal(BlackboxDumpState.Failed, dumper.State);       // ★C#4: 재진입 옵저버(CAS)가 Failed 를 Sealed 로 은폐 못 함
                Assert.True(rec.IsSealed);                                 // ★재기록 원천 차단
                Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);        // ★무응답 격리 수렴
                Assert.False(string.IsNullOrEmpty(dumper.LastError));      // 사유 박제(삼킴 아님)
            }
            finally { try { File.Delete(fileAsDir); } catch { } }
        }

        private static bool SpinUntil(Func<bool> cond, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; Thread.Sleep(10); }
            return cond();
        }
    }
}
