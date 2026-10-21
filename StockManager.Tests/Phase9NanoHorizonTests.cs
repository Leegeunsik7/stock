// ============================================================================
// Phase9NanoHorizonTests — v9.0.0 적대적 검증(100% 단언)
//   ⚡ SimdRiskInterlock: SIMD 10호가 병렬 검증·비가역 Veto 래치·δ=0
//   🔮 SpeculativeOrderBook: 상위-3 델타 사전연산·FNV-1a 핑거프린트 적중·δ=0
//   🦅 ClaudeBlackSwanEngine: 선지 캐시-예열 격자(사전판정 재사용·분기 락·Black Swan 안전 낙하)·δ=0
// ============================================================================
using System;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Phase9NanoHorizonTests
    {
        private const int L = SpeculativeOrderBook.Levels;   // 10

        // 유효 10호가 사다리(매수 비증가·매도 비감소·스프레드 10틱·잔량 modest).
        private static void FillValid(Span<long> bidPx, Span<long> bidQty, Span<long> askPx, Span<long> askQty)
        {
            for (int i = 0; i < L; i++)
            {
                bidPx[i] = 10000 - i; askPx[i] = 10010 + i;
                bidQty[i] = 100 + i;  askQty[i] = 100 + i;
            }
        }

        // ══ ⚡ Task 1: SimdRiskInterlock ══════════════════════════════════════
        [Fact]
        public void Simd_CleanBook_Passes_NoVeto()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            var il = new SimdRiskInterlock();
            var v = il.Validate(bp, bq, ap, aq, SimdRiskLimits.Default);
            Assert.True(v.Ok);
            Assert.Equal(RiskAnomaly.None, v.Reason);
            Assert.False(il.IsVetoed);
            Assert.True(il.IsOrderEmissionAllowed);
        }

        [Fact]
        public void Simd_FatFinger_Vetoes_And_LatchesIrreversibly()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            bq[3] = 2_000_000;   // 호가별 상한(1M) 초과 — Fat-Finger
            var il = new SimdRiskInterlock();
            var v = il.Validate(bp, bq, ap, aq, SimdRiskLimits.Default);
            Assert.False(v.Ok);
            Assert.Equal(RiskAnomaly.FatFingerQty, v.Reason);
            Assert.True(il.IsVetoed);
            Assert.False(il.IsOrderEmissionAllowed);   // ★발주 회로 비가역 차단

            // 이후 완전 정상 사다리도 래치로 인해 거부(자동복구 없음).
            FillValid(bp, bq, ap, aq);
            var v2 = il.Validate(bp, bq, ap, aq, SimdRiskLimits.Default);
            Assert.False(v2.Ok);
            Assert.Equal(RiskAnomaly.LatchedVeto, v2.Reason);
        }

        [Fact]
        public void Simd_Veto_ForceFreezes_Attestation()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            bq[0] = -5;   // 음수 잔량(오염)
            var att = new OrderAttestationService();
            var il = new SimdRiskInterlock(att);
            var v = il.Validate(bp, bq, ap, aq, SimdRiskLimits.Default);
            Assert.False(v.Ok);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // ★fail-closed 이중 봉인
        }

        [Fact]
        public void Simd_CrossedSpread_Vetoes()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            ap[0] = 9999;   // 매도 최우선 < 매수 최우선 → 교차(음수 스프레드)
            var v = SimdRiskInterlock.ValidateCore(bp, bq, ap, aq, SimdRiskLimits.Default);
            Assert.False(v.Ok);
            Assert.Equal(RiskAnomaly.CrossedOrTightSpread, v.Reason);
        }

        [Fact]
        public void Simd_WideSpread_And_TotalSpoof_And_Disorder_And_NonPositive()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            var lim = SimdRiskLimits.Default;

            FillValid(bp, bq, ap, aq);
            for (int i = 0; i < L; i++) ap[i] = 30000 + i;   // 스프레드 20000 > 10000 → 광폭
            Assert.Equal(RiskAnomaly.WideSpread, SimdRiskInterlock.ValidateCore(bp, bq, ap, aq, lim).Reason);

            FillValid(bp, bq, ap, aq);
            for (int i = 0; i < L; i++) bq[i] = 600_000;     // 편측 총잔량 6M > 5M, 호가별 <1M → 스푸핑
            Assert.Equal(RiskAnomaly.TotalQtySpoof, SimdRiskInterlock.ValidateCore(bp, bq, ap, aq, lim).Reason);

            FillValid(bp, bq, ap, aq);
            bp[2] = 10005;   // 매수 사다리 증가(비단조) → 붕괴
            Assert.Equal(RiskAnomaly.LadderDisorder, SimdRiskInterlock.ValidateCore(bp, bq, ap, aq, lim).Reason);

            FillValid(bp, bq, ap, aq);
            bp[0] = 0;       // 최우선 매수가 0 → 비양수
            Assert.Equal(RiskAnomaly.NonPositivePrice, SimdRiskInterlock.ValidateCore(bp, bq, ap, aq, lim).Reason);
        }

        [Fact]
        public void Simd_ValidateCore_ZeroAllocation_HotPath()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            var lim = SimdRiskLimits.Default;
            for (int i = 0; i < 10_000; i++) SimdRiskInterlock.ValidateCore(bp, bq, ap, aq, lim);   // 워밍업
            long minAlloc = long.MaxValue;   // 람다 캡처 불가(Span 로컬) — ZeroAllocProbe 와 같은 다중 창 최솟값을 인라인으로
            for (int zw = 0; zw < ZeroAllocProbe.DefaultWindows && minAlloc != 0; zw++)
            {
                long zb = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) SimdRiskInterlock.ValidateCore(bp, bq, ap, aq, lim);
                minAlloc = Math.Min(minAlloc, GC.GetAllocatedBytesForCurrentThread() - zb);
            }
            Assert.Equal(0L, minAlloc); // SIMD 핫패스 힙 0
        }

        // ══ 🔮 Task 2: SpeculativeOrderBook ══════════════════════════════════
        [Fact]
        public void Spec_Prime_Resolve_Hit_CopiesCandidate()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            var spec = new SpeculativeOrderBook();
            Assert.True(spec.Prime(0, bp, bq, ap, aq));
            long fp = SpeculativeOrderBook.FingerprintLadder(bp, bq, ap, aq);

            Assert.True(spec.TryResolve(fp, out int slot));   // ★1-사이클 핑거프린트 적중
            Assert.Equal(0, slot);
            Assert.Equal(1L, spec.Hits);

            Span<long> obp = stackalloc long[L], obq = stackalloc long[L], oap = stackalloc long[L], oaq = stackalloc long[L];
            Assert.True(spec.CopyCandidate(slot, obp, obq, oap, oaq));
            for (int i = 0; i < L; i++) { Assert.Equal(bp[i], obp[i]); Assert.Equal(aq[i], oaq[i]); }   // 조립 스킵·캐시 적용
        }

        [Fact]
        public void Spec_Resolve_Miss_CountsAndReturnsFalse()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            var spec = new SpeculativeOrderBook();
            spec.Prime(0, bp, bq, ap, aq);
            Assert.False(spec.TryResolve(unchecked(0x1234_5678_9ABC_DEF0L), out int slot));
            Assert.Equal(-1, slot);
            Assert.Equal(1L, spec.Misses);
        }

        [Fact]
        public void Spec_PrimeFromTrend_ThreeDeltas_UpTickHits()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            var spec = new SpeculativeOrderBook();
            Assert.True(spec.PrimeFromTrend(bp, bq, ap, aq, tickDelta: 5));
            Assert.Equal(3L, spec.Primed);

            // 상승(+5틱) 후보를 수동 조립해 핑거프린트 → 적중해야 함(사전연산 델타와 일치).
            Span<long> ubp = stackalloc long[L], uap = stackalloc long[L];
            for (int i = 0; i < L; i++) { ubp[i] = bp[i] + 5; uap[i] = ap[i] + 5; }
            long upFp = SpeculativeOrderBook.FingerprintLadder(ubp, bq, uap, aq);
            Assert.True(spec.TryResolve(upFp, out int slot));
            Assert.Equal(0, slot);   // 후보0 = 상승 델타
        }

        [Fact]
        public void Spec_Fingerprint_Deterministic()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            long f1 = SpeculativeOrderBook.FingerprintLadder(bp, bq, ap, aq);
            long f2 = SpeculativeOrderBook.FingerprintLadder(bp, bq, ap, aq);
            Assert.Equal(f1, f2);
            bp[4] += 1;   // 1틱 변화 → 핑거프린트 상이(충돌 방지)
            Assert.NotEqual(f1, SpeculativeOrderBook.FingerprintLadder(bp, bq, ap, aq));
        }

        [Fact]
        public void Spec_ZeroAllocation_HotPath()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            var spec = new SpeculativeOrderBook();
            long fp = SpeculativeOrderBook.FingerprintLadder(bp, bq, ap, aq);
            for (int i = 0; i < 10_000; i++) { spec.Prime(0, bp, bq, ap, aq); spec.TryResolve(fp, out _); }   // 워밍업
            long minAlloc = long.MaxValue;   // 람다 캡처 불가(Span 로컬) — ZeroAllocProbe 와 같은 다중 창 최솟값을 인라인으로
            for (int zw = 0; zw < ZeroAllocProbe.DefaultWindows && minAlloc != 0; zw++)
            {
                long zb = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) { spec.Prime(0, bp, bq, ap, aq); spec.TryResolve(fp, out _); }
                minAlloc = Math.Min(minAlloc, GC.GetAllocatedBytesForCurrentThread() - zb);
            }
            Assert.Equal(0L, minAlloc); // 사전연산+적중 힙 0
        }

        // ══ 🦅 Task 3: ClaudeBlackSwanEngine (선지 캐시-예열 격자) ══════════════
        [Fact]
        public void BlackSwan_Prime_Precognize_ReturnsPreValidatedVerdict()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            var lim = SimdRiskLimits.Default;
            using var eng = new ClaudeBlackSwanEngine();

            // (a) 정상 책 → 사전판정 OK 를 재연산 0으로 반환.
            FillValid(bp, bq, ap, aq);
            long okFp = SpeculativeOrderBook.FingerprintLadder(bp, bq, ap, aq);
            Assert.True(eng.Prime(0, okFp, bp, bq, ap, aq, lim));
            Assert.True(eng.TryPrecognize(okFp, out bool ok1, out int r1));
            Assert.True(ok1);
            Assert.Equal((int)RiskAnomaly.None, r1);

            // (b) Fat-Finger 책 → 사전판정 Veto 를 재연산 0으로 반환(조립+검증 동시 스킵).
            FillValid(bp, bq, ap, aq);
            bq[2] = 9_000_000;
            long badFp = SpeculativeOrderBook.FingerprintLadder(bp, bq, ap, aq);
            Assert.True(eng.Prime(1, badFp, bp, bq, ap, aq, lim));
            Assert.True(eng.TryPrecognize(badFp, out bool ok2, out int r2));
            Assert.False(ok2);
            Assert.Equal((int)RiskAnomaly.FatFingerQty, r2);
            Assert.Equal(2L, eng.Hits);
        }

        [Fact]
        public void BlackSwan_Miss_ReleasesLock_SafeFallthrough()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            using var eng = new ClaudeBlackSwanEngine(lockThreshold: 4);
            long fp = SpeculativeOrderBook.FingerprintLadder(bp, bq, ap, aq);
            eng.Prime(0, fp, bp, bq, ap, aq, SimdRiskLimits.Default);

            for (int i = 0; i < 4; i++) Assert.True(eng.TryPrecognize(fp, out _, out _));   // 연속 적중
            Assert.True(eng.PrecognitionLocked);                                             // ★선지 락 체결

            Assert.False(eng.TryPrecognize(unchecked(0x7777_7777_7777_7777L), out _, out _)); // Black Swan(미추측)
            Assert.False(eng.PrecognitionLocked);                                            // ★락 해제(안전 낙하)
            Assert.Equal(1L, eng.Misses);
        }

        [Fact]
        public void BlackSwan_TryPrecognize_ZeroAllocation_HotPath()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            using var eng = new ClaudeBlackSwanEngine();
            long fp = SpeculativeOrderBook.FingerprintLadder(bp, bq, ap, aq);
            eng.Prime(0, fp, bp, bq, ap, aq, SimdRiskLimits.Default);
            for (int i = 0; i < 10_000; i++) eng.TryPrecognize(fp, out _, out _);   // 워밍업

            // ★Double-Measure 하드닝: 측정 루프를 동일 IL 로컬함수로 격리(δ=0 무캡처-힙) → 1차 호출이 티어드
            //   JIT/OSR 재컴파일 노이즈를 흡수하고, 2차(측정) 호출은 이미 tier-1 이라 위양성 없이 δ=0 을 단언한다.
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 500_000; i++) eng.TryPrecognize(fp, out _, out _);
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();                          // 웜업 런(OSR/tier-up 흡수)
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));   // 선지 관통 힙 0(포인터 접근) — 안정 측정
        }

        [Fact]
        public void BlackSwan_Dispose_Idempotent()
        {
            var eng = new ClaudeBlackSwanEngine();
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            eng.Prime(0, 12345L, bp, bq, ap, aq, SimdRiskLimits.Default);
            eng.Dispose();
            eng.Dispose();   // 이중 Dispose 안전(네이티브 이중 해제 없음)
            Assert.False(eng.TryPrecognize(12345L, out _, out _));   // 해제 후 관측 불가(널 격자)
        }

        // ══ 🛡️ 적대검증 회귀(v9 확정 결함 교정) ══════════════════════════════
        // ── D5: 빈 레벨(price=0)을 이상치로 오판하지 않음 — L3 Delete·10호가 미만은 정상 이벤트 ──
        [Fact]
        public void Simd_EmptyDeepLevels_ZeroPrice_NoFalseVeto()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            for (int i = 7; i < L; i++) { bp[i] = 0; bq[i] = 0; ap[i] = 0; aq[i] = 0; }   // 심레벨 3개 소거(빈 레벨)
            var v = SimdRiskInterlock.ValidateCore(bp, bq, ap, aq, SimdRiskLimits.Default);
            Assert.True(v.Ok);                          // ★PriceOutOfBounds/LadderDisorder 오탐 없음(과차단 제거)
            Assert.Equal(RiskAnomaly.None, v.Reason);
        }

        // ── D5 경계: 0(빈 레벨)은 허용하되 '음수' 심가격은 여전히 차단(하한 완화가 음수를 뚫지 않음) ──
        [Fact]
        public void Simd_NegativeDeepPrice_StillVetoes()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            FillValid(bp, bq, ap, aq);
            bp[5] = -1;   // 음수 가격(오염) — 빈 레벨 0 과 구분되어야 함
            var v = SimdRiskInterlock.ValidateCore(bp, bq, ap, aq, SimdRiskLimits.Default);
            Assert.False(v.Ok);
            Assert.Equal(RiskAnomaly.PriceOutOfBounds, v.Reason);
        }

        // ── D3: slot 재-Prime 이 새 fingerprint 를 새 판정과 원자적으로 짝지음(이전 판정 잔류 금지) ──
        [Fact]
        public void BlackSwan_Reprime_ReplacesVerdict_NoStalePairing()
        {
            Span<long> bp = stackalloc long[L], bq = stackalloc long[L], ap = stackalloc long[L], aq = stackalloc long[L];
            var lim = SimdRiskLimits.Default;
            using var eng = new ClaudeBlackSwanEngine();

            FillValid(bp, bq, ap, aq);
            long fpA = SpeculativeOrderBook.FingerprintLadder(bp, bq, ap, aq);
            eng.Prime(0, fpA, bp, bq, ap, aq, lim);
            Assert.True(eng.TryPrecognize(fpA, out bool okA, out _));
            Assert.True(okA);

            FillValid(bp, bq, ap, aq);
            bq[2] = 9_000_000;                                // 위반책 B
            long fpB = SpeculativeOrderBook.FingerprintLadder(bp, bq, ap, aq);
            eng.Prime(0, fpB, bp, bq, ap, aq, lim);           // 같은 slot0 재-Prime
            Assert.True(eng.TryPrecognize(fpB, out bool okB, out int rB));
            Assert.False(okB);                                // ★새 fp 에 이전 OK 가 아니라 새 Veto 가 짝지어짐
            Assert.Equal((int)RiskAnomaly.FatFingerQty, rB);
            Assert.False(eng.TryPrecognize(fpA, out _, out _)); // 이전 fp 는 교체돼 미적중
        }
    }

    // ★D3 동시성 회귀 — 라이터가 slot0 을 안전책↔위반책으로 번갈아 재-Prime 하는 동안, 리더의 TryPrecognize 적중이
    //   절대 (안전fp↔veto)/(위반fp↔OK) 로 어긋나지 않음(seqlock torn-read 부재)을 입증. 별도 클래스(δ=0 계측과 격리).
    public sealed class Phase9BlackSwanConcurrencyTests
    {
        private const int L = SpeculativeOrderBook.Levels;
        private static void Fill(long[] bidPx, long[] bidQty, long[] askPx, long[] askQty)
        {
            for (int i = 0; i < L; i++) { bidPx[i] = 10000 - i; askPx[i] = 10010 + i; bidQty[i] = 100 + i; askQty[i] = 100 + i; }
        }

        [Fact]
        public void BlackSwan_ConcurrentReprime_NeverPairsFingerprintWithStaleVerdict()
        {
            var lim = SimdRiskLimits.Default;
            long[] aBid = new long[L], aBidQ = new long[L], aAsk = new long[L], aAskQ = new long[L];
            long[] bBid = new long[L], bBidQ = new long[L], bAsk = new long[L], bAskQ = new long[L];
            Fill(aBid, aBidQ, aAsk, aAskQ);
            Fill(bBid, bBidQ, bAsk, bAskQ); bBidQ[2] = 9_000_000;   // B = Fat-Finger veto
            long fpA = SpeculativeOrderBook.FingerprintLadder(aBid, aBidQ, aAsk, aAskQ);
            long fpB = SpeculativeOrderBook.FingerprintLadder(bBid, bBidQ, bAsk, bAskQ);
            Assert.NotEqual(fpA, fpB);

            using var eng = new ClaudeBlackSwanEngine();
            int[] mismatch = new int[1];
            const int Iters = 300_000;
            var writer = new System.Threading.Thread(() =>
            {
                for (int k = 0; k < Iters; k++)
                {
                    if ((k & 1) == 0) eng.Prime(0, fpA, aBid, aBidQ, aAsk, aAskQ, lim);
                    else              eng.Prime(0, fpB, bBid, bBidQ, bAsk, bAskQ, lim);
                }
            });
            var reader = new System.Threading.Thread(() =>
            {
                for (int k = 0; k < Iters; k++)
                {
                    if (eng.TryPrecognize(fpA, out bool okA, out _) && !okA) System.Threading.Interlocked.Increment(ref mismatch[0]); // fpA 는 반드시 OK
                    if (eng.TryPrecognize(fpB, out bool okB, out _) &&  okB) System.Threading.Interlocked.Increment(ref mismatch[0]); // fpB 는 반드시 veto
                }
            });
            writer.Start(); reader.Start();
            Assert.True(writer.Join(20000)); Assert.True(reader.Join(20000));
            Assert.Equal(0, System.Threading.Volatile.Read(ref mismatch[0]));   // ★fp 와 판정이 항상 정합(torn read 부재)
        }
    }
}
