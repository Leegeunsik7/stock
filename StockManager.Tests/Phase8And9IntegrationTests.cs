// ============================================================================
// Phase8And9IntegrationTests — v8.0.0 적대적 통합 검증
//   💾 NvmeAuditJournal ↔ CausalAuditVault 해시체인 100% 정합 · MMF 변조 감지
//   📈 ZeroCopyOrderBook in-place L3 파싱 · 🔗 NanoPipelineIntegrator 전-파이프라인 δ=0(50만 회)
// ============================================================================
using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Phase8And9IntegrationTests
    {
        private const int L = ZeroCopyOrderBook.Levels;

        private static string TempPath() =>
            Path.Combine(Path.GetTempPath(), "ms2e_nvme_" + Guid.NewGuid().ToString("N") + ".mmf");

        private static void SeedValidBook(ZeroCopyOrderBook b)
        {
            for (int i = 0; i < L; i++) { b.Set(0, i, 10000 - i, 100 + i); b.Set(1, i, 10010 + i, 100 + i); }
        }

        // 파일 바이트에서 저널 사슬을 독립 재계산(제네시스 헤더[32..64] 기준). 변조 시 false.
        private static bool VerifyFileChain(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            const int H = NvmeAuditJournal.HeaderLen, R = NvmeAuditJournal.RecordLen, HL = 32;
            long count = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(24));
            Span<byte> run = stackalloc byte[HL];
            b.AsSpan(32, HL).CopyTo(run);                          // 제네시스
            Span<byte> input = stackalloc byte[HL + 24];
            for (long k = 0; k < count; k++)
            {
                int off = H + (int)k * R;
                run.CopyTo(input.Slice(0, HL));
                b.AsSpan(off, 24).CopyTo(input.Slice(HL, 24));    // qpc|evict|seq
                SHA256.HashData(input, run);
                if (!run.SequenceEqual(b.AsSpan(off + 24, HL))) return false;
            }
            return true;
        }

        // ── 1) 💾 MMF 저널 ↔ vault 해시체인 100% 정합 + 자기-정합 검증 ──
        [Fact]
        public void Mmf_Journal_Matches_VaultHashChain_100Percent()
        {
            string path = TempPath();
            var vault = new CausalAuditVault(4096);
            var journal = new NvmeAuditJournal(vault, path, 4096);
            try
            {
                for (int i = 0; i < 1000; i++)
                    Assert.True(journal.TryAppend(1000 + i, i, i * 2L));

                Assert.Equal(1000L, journal.RecordsJournaled);
                Assert.Equal(1000L, vault.Count);
                Assert.True(journal.VerifyChain());                       // 저널 SHA-256 사슬 자기-정합
                Assert.Equal(LedgerIntegrity.Intact, vault.Verify());     // vault 사슬 무결

                // ★저널 헤드 해시 == vault 헤드 해시(비트 단위 100% 일치)
                Span<byte> jh = stackalloc byte[32], vh = stackalloc byte[32];
                Assert.True(journal.CopyLastHash(jh));
                Assert.True(vault.CopyHeadHash(vh));
                Assert.True(jh.SequenceEqual(vh));

                // 레코드 단위 교차검증(저장 필드 정확)
                Assert.True(journal.ReadRecord(500, out long q, out long e, out long s, stackalloc byte[32]));
                Assert.Equal(1500L, q); Assert.Equal(500L, e); Assert.Equal(1000L, s);
            }
            finally { journal.Dispose(); try { File.Delete(path); } catch { /* best-effort */ } }
        }

        // ── 2) 💾 MMF 변조 감지: 디스크 레코드 1바이트 변조 → 독립 재계산이 사슬 불일치를 잡아냄 ──
        [Fact]
        public void Mmf_Tamper_BreaksChainVerification()
        {
            string path = TempPath();
            var vault = new CausalAuditVault(256);
            var journal = new NvmeAuditJournal(vault, path, 256);
            try
            {
                for (int i = 0; i < 50; i++) Assert.True(journal.TryAppend(7000 + i, i, i));
                journal.Flush();
            }
            finally { journal.Dispose(); }   // 핸들 해제(디스크 영속)

            Assert.True(VerifyFileChain(path));   // 무변조 → 정합

            byte[] raw = File.ReadAllBytes(path);
            int off = NvmeAuditJournal.HeaderLen + 10 * NvmeAuditJournal.RecordLen;   // 10번 레코드
            raw[off] ^= 0xFF;                      // qpc 1바이트 변조
            File.WriteAllBytes(path, raw);

            Assert.False(VerifyFileChain(path));  // ★변조 감지(재계산 해시 불일치)
            try { File.Delete(path); } catch { }
        }

        // ── 3) 📈 ZeroCopyOrderBook: L3 Set/Modify/Delete in-place 파싱 ──
        [Fact]
        public void OrderBook_Apply_SetModifyDelete_InPlace()
        {
            using var book = new ZeroCopyOrderBook();
            Span<byte> msg = stackalloc byte[20];

            // Set bid[0] = (10000, 500)
            msg[0] = 0; msg[1] = 0; msg[2] = 0;
            BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(4), 10000);
            BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(12), 500);
            Assert.True(book.Apply(msg));
            Assert.Equal(10000L, book.GetBidPriceSpan()[0]);
            Assert.Equal(500L, book.GetBidQtySpan()[0]);
            long v1 = book.Version;

            // Modify bid[0] qty=750, price=0(유지)
            msg[0] = 1;
            BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(4), 0);
            BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(12), 750);
            Assert.True(book.Apply(msg));
            Assert.Equal(750L, book.GetBidQtySpan()[0]);
            Assert.Equal(10000L, book.GetBidPriceSpan()[0]);   // 가격 유지
            Assert.True(book.Version > v1);

            // Delete bid[0]
            msg[0] = 2;
            Assert.True(book.Apply(msg));
            Assert.Equal(0L, book.GetBidQtySpan()[0]);
            Assert.Equal(0L, book.GetBidPriceSpan()[0]);

            Assert.False(book.Apply(stackalloc byte[10]));   // 형식 불량 거부
        }

        // ── 4) 🔗 선지 fast-path: 사전판정 봉인 후 동일 책 → SwanHit(조립+검증 스킵) ──
        [Fact]
        public void Pipeline_SwanFastPath_ReusesPreValidatedVerdict()
        {
            using var book = new ZeroCopyOrderBook();
            using var swan = new ClaudeBlackSwanEngine();
            var risk = new SimdRiskInterlock();
            var spec = new SpeculativeOrderBook();
            SeedValidBook(book);
            var integ = new NanoPipelineIntegrator(book, risk, spec, swan, SimdRiskLimits.Default);
            integ.PrimeFromCurrent(tickDelta: 5);   // 현재책 사전판정 봉인(swan) + 상위-3 추측

            // 책을 바꾸지 않는 Modify(bid0 qty→현재값) → 핑거프린트 불변 → 선지 적중.
            Span<byte> msg = stackalloc byte[20];
            msg[0] = 1; msg[1] = 0; msg[2] = 0;
            BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(4), 0);
            BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(12), book.GetBidQtySpan()[0]);
            var r = integ.Process(msg);

            Assert.True(r.SwanHit);          // ★선지 격자 적중
            Assert.True(r.RiskOk);           // 사전판정 OK 재사용(재연산 0)
            Assert.Equal(1L, integ.SwanHits);
        }

        // ── 5) 🔗 Fat-Finger 메시지 → SIMD Veto·비가역 래치·발주 차단 ──
        [Fact]
        public void Pipeline_FatFinger_Vetoes_And_Latches()
        {
            using var book = new ZeroCopyOrderBook();
            using var swan = new ClaudeBlackSwanEngine();
            var risk = new SimdRiskInterlock();
            var spec = new SpeculativeOrderBook();
            SeedValidBook(book);
            var integ = new NanoPipelineIntegrator(book, risk, spec, swan, SimdRiskLimits.Default);

            Span<byte> msg = stackalloc byte[20];
            msg[0] = 0; msg[1] = 0; msg[2] = 3;
            BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(4), 9997);        // bid3 가격 유지(단조)
            BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(12), 9_000_000);  // 잔량 Fat-Finger
            var r = integ.Process(msg);

            Assert.False(r.RiskOk);
            Assert.Equal(RiskAnomaly.FatFingerQty, r.Reason);
            Assert.True(risk.IsVetoed);
            Assert.False(risk.IsOrderEmissionAllowed);   // ★발주 회로 비가역 차단
            Assert.Equal(1L, integ.RiskVetoes);
        }

        // ── 6) 🔗🛡️ 전-파이프라인 50만 회 δ=0: L3→오더북→MMF저널→Phase9 SIMD/추측 ──
        [Fact]
        public void Pipeline_FullChain_500k_ZeroAllocation()
        {
            string path = TempPath();
            var book = new ZeroCopyOrderBook();
            var swan = new ClaudeBlackSwanEngine();
            var risk = new SimdRiskInterlock();
            var spec = new SpeculativeOrderBook();
            var vault = new CausalAuditVault(520_000);
            var journal = new NvmeAuditJournal(vault, path, 520_000);
            try
            {
                SeedValidBook(book);
                var integ = new NanoPipelineIntegrator(book, risk, spec, swan, SimdRiskLimits.Default, journal);
                Span<byte> msg = stackalloc byte[20];
                msg[0] = 1; msg[1] = 0;   // Modify bid(가격 유지·잔량만 변동 → 책 유효 유지)

                for (int i = 0; i < 5_000; i++)   // 워밍업(tiered JIT 안정화)
                {
                    msg[2] = (byte)(i % L);
                    BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(4), 0);
                    BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(12), 100 + (i % 37));
                    integ.Process(msg);
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 500_000; i++)
                {
                    msg[2] = (byte)(i % L);
                    BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(4), 0);
                    BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(12), 100 + (i % 37));
                    integ.Process(msg);
                }
                long delta = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.Equal(0L, delta);                        // ★전 파이프라인 힙 0바이트
                Assert.Equal(0L, risk.Vetoes);                  // 유효 책 유지(veto 없음)
                Assert.True(journal.RecordsJournaled >= 500_000, "50만 레코드 이상 MMF 저널링");
                Assert.Equal(LedgerIntegrity.Intact, vault.Verify());   // 사슬 무결 유지
            }
            finally { journal.Dispose(); book.Dispose(); swan.Dispose(); try { File.Delete(path); } catch { } }
        }

        // ══ 🛡️ 적대검증 회귀(v9 확정 결함 교정) ══════════════════════════════
        // ── 7) D5: 정상 L3 Delete(심레벨 소거·price=0)가 PriceOutOfBounds 로 비가역 오차단하지 않음 ──
        [Fact]
        public void Pipeline_DeepLevelDelete_DoesNotFalseVeto()
        {
            using var book = new ZeroCopyOrderBook();
            using var swan = new ClaudeBlackSwanEngine();
            var risk = new SimdRiskInterlock();
            var spec = new SpeculativeOrderBook();
            SeedValidBook(book);
            var integ = new NanoPipelineIntegrator(book, risk, spec, swan, SimdRiskLimits.Default);

            Span<byte> msg = stackalloc byte[20];
            msg[0] = 2; msg[1] = 0; msg[2] = 7;   // Delete bid level 7(심레벨 소거 → price/qty=0)
            var r = integ.Process(msg);

            Assert.True(r.RiskOk);                              // ★정상 이벤트 → veto 아님(과차단 제거)
            Assert.NotEqual(RiskAnomaly.PriceOutOfBounds, r.Reason);
            Assert.False(risk.IsVetoed);
            Assert.True(risk.IsOrderEmissionAllowed);           // 발주 회로 정상 유지
            Assert.Equal(0L, integ.RiskVetoes);
        }

        // ── 8) D4: 비가역 래치 후에는 선지 적중 경로도 통과를 보고하지 않음(fail-closed 최우선) ──
        [Fact]
        public void Pipeline_OnceLatched_AllPathsFailClosed()
        {
            using var book = new ZeroCopyOrderBook();
            using var swan = new ClaudeBlackSwanEngine();
            var risk = new SimdRiskInterlock();
            var spec = new SpeculativeOrderBook();
            SeedValidBook(book);
            var integ = new NanoPipelineIntegrator(book, risk, spec, swan, SimdRiskLimits.Default);
            integ.PrimeFromCurrent(tickDelta: 5);   // 현재(정상) 책을 swan slot0 에 OK 봉인

            // 1) Fat-Finger 틱 → swan 미적중(새 fp) → _risk.Validate 비가역 래치.
            Span<byte> bad = stackalloc byte[20];
            bad[0] = 0; bad[1] = 0; bad[2] = 3;
            BinaryPrimitives.WriteInt64LittleEndian(bad.Slice(4), 9997);
            BinaryPrimitives.WriteInt64LittleEndian(bad.Slice(12), 9_000_000);
            integ.Process(bad);
            Assert.True(risk.IsVetoed);
            Assert.False(risk.IsOrderEmissionAllowed);

            // 2) 책을 앞서 봉인된 정상 상태로 복원(동일 fp → 원래는 선지 적중 RiskOk=true=fail-open).
            //    이제 래치 존중으로 무조건 LatchedVeto.
            Span<byte> back = stackalloc byte[20];
            back[0] = 0; back[1] = 0; back[2] = 3;
            BinaryPrimitives.WriteInt64LittleEndian(back.Slice(4), 9997);
            BinaryPrimitives.WriteInt64LittleEndian(back.Slice(12), 103);   // SeedValidBook 원래 qty(100+3)
            var r = integ.Process(back);

            Assert.False(r.RiskOk);                             // ★래치 상태에선 무조건 not-OK
            Assert.Equal(RiskAnomaly.LatchedVeto, r.Reason);
            Assert.False(risk.IsOrderEmissionAllowed);          // 영구 차단 유지
        }

        // ── 9) D4: 선지 격자에 봉인된 veto 가 적중하면 실제 비가역 하드웨어 퓨즈를 구동(래치 미구동 fail-open 차단) ──
        [Fact]
        public void Pipeline_SwanVetoHit_EngagesIrreversibleLatch()
        {
            using var book = new ZeroCopyOrderBook();
            using var swan = new ClaudeBlackSwanEngine();
            var risk = new SimdRiskInterlock();
            var spec = new SpeculativeOrderBook();
            SeedValidBook(book);
            // 현재책을 위반 상태(Fat-Finger)로 만든 뒤 swan slot0 에 veto 봉인(_risk 는 아직 미래치).
            Span<byte> ff = stackalloc byte[20];
            ff[0] = 0; ff[1] = 0; ff[2] = 3;
            BinaryPrimitives.WriteInt64LittleEndian(ff.Slice(4), 9997);
            BinaryPrimitives.WriteInt64LittleEndian(ff.Slice(12), 9_000_000);
            Assert.True(book.Apply(ff));
            var integ = new NanoPipelineIntegrator(book, risk, spec, swan, SimdRiskLimits.Default);
            integ.PrimeFromCurrent(tickDelta: 5);
            Assert.True(risk.IsOrderEmissionAllowed);           // 아직 미래치(Validate 미경유)

            // 책 불변 Modify → 동일 fp → 선지 veto 적중.
            Span<byte> noop = stackalloc byte[20];
            noop[0] = 1; noop[1] = 0; noop[2] = 3;
            BinaryPrimitives.WriteInt64LittleEndian(noop.Slice(4), 0);
            BinaryPrimitives.WriteInt64LittleEndian(noop.Slice(12), book.GetBidQtySpan()[3]);
            var r = integ.Process(noop);

            Assert.True(r.SwanHit);                             // 선지 적중
            Assert.False(r.RiskOk);                             // veto 판정
            Assert.True(risk.IsVetoed);                         // ★캐시 적중이라도 비가역 래치 실제 구동
            Assert.False(risk.IsOrderEmissionAllowed);
            Assert.Equal(1L, integ.RiskVetoes);
        }

        // ── 10) D1: Dispose 후 저널 네이티브 접근이 크래시 없이 fail-closed(false) ──
        [Fact]
        public void Journal_AfterDispose_FailsClosed_NoNativeCrash()
        {
            string path = TempPath();
            var vault = new CausalAuditVault(256);
            var journal = new NvmeAuditJournal(vault, path, 256);
            Assert.True(journal.TryAppend(1000, 0, 0));
            journal.Dispose();

            Assert.False(journal.TryAppend(2000, 1, 1));                          // ★AccessViolation 대신 false
            Assert.False(journal.ReadRecord(0, out _, out _, out _, stackalloc byte[32]));
            Assert.False(journal.VerifyChain());
            Assert.False(journal.CopyLastHash(stackalloc byte[32]));
            journal.Flush();                                                      // no-op(예외 없음)
            journal.Dispose();                                                    // 이중 Dispose 안전
            try { File.Delete(path); } catch { }
        }
    }
}
