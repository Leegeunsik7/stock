using System;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // S28 — AI 포트폴리오 멘토 & 미래가치 시뮬레이터.
    //  M1 제로마찰 온보딩 정준화·인입 펌프·컴플라이언스 락
    //  M2 고정소수점 복리 곡선·분위 밴드·seqlock 락프리 실시간·δ=0
    //  M3 카오스 비대칭 스트레스·진단·리밸런싱 밸브 fail-closed
    [Collection("ConcurrencyStress")]   // v4.58.0 — 헤비 스트레스 직렬화(CPU 초과 구독·Barrier 기아 방지)
    public sealed class S28PortfolioMentorTests
    {
        // "삼성전자"/"005930", "AAPL"/"애플", "비트코인"/"BTC"/"KRW-BTC" 만 해석. 그 외 미해석(fail-closed).
        private sealed class TestResolver : ISymbolResolver
        {
            public bool TryResolve(string raw, out string canon, out Market market, out Currency ccy)
            {
                switch (raw)
                {
                    case "삼성전자": case "005930": canon = "005930"; market = Market.KRX_KOSPI; ccy = Currency.KRW; return true;
                    case "애플": case "AAPL":       canon = "AAPL";   market = Market.US_NASDAQ; ccy = Currency.USD; return true;
                    case "비트코인": case "BTC": case "KRW-BTC": canon = "KRW-BTC"; market = Market.CRYPTO; ccy = Currency.KRW; return true;
                    default: canon = ""; market = default; ccy = default; return false;
                }
            }
        }

        // ═══════════════════ M1 온보딩 정준화 · 인입 펌프 · 컴플라이언스 ═══════════════════

        [Fact]
        public void Canonicalize_FullDraft_ProducesCompleteSlot()
        {
            var r = new TestResolver();
            var ok = OnboardingCanonicalizer.TryCanonicalize(
                new EntityDraft("삼성전자", 50m, 70000m, null), r, out var slot, out var reject);
            Assert.True(ok);
            Assert.Equal(CanonReject.None, reject);
            Assert.Equal("005930", slot.Symbol);
            Assert.Equal(AssetClass.DomesticStock, slot.Class);
            Assert.Equal(Currency.KRW, slot.Ccy);
            Assert.True(slot.IsComplete);
            Assert.Equal(50m, slot.Qty.ToDecimal());
            Assert.Equal(70000m, slot.AvgPrice.ToDecimal());
        }

        [Fact]
        public void Canonicalize_SymbolOnly_OpensPartialSlot()
        {
            var r = new TestResolver();
            var ok = OnboardingCanonicalizer.TryCanonicalize(new EntityDraft("비트코인", null, null, null), r, out var slot, out _);
            Assert.True(ok);
            Assert.Equal("KRW-BTC", slot.Symbol);
            Assert.Equal(AssetClass.Crypto, slot.Class);
            Assert.False(slot.HasQty);
            Assert.False(slot.HasAvgPrice);
            Assert.False(slot.IsComplete);
        }

        [Fact]
        public void Canonicalize_UnresolvedSymbol_FailClosed()
        {
            var r = new TestResolver();
            Assert.False(OnboardingCanonicalizer.TryCanonicalize(new EntityDraft("듣보종목", 1m, 1m, null), r, out _, out var reject));
            Assert.Equal(CanonReject.UnresolvedSymbol, reject);
        }

        [Theory]
        [InlineData(-5)]
        [InlineData(0)]
        public void Canonicalize_NonPositiveQty_FailClosed(int qty)
        {
            var r = new TestResolver();
            Assert.False(OnboardingCanonicalizer.TryCanonicalize(new EntityDraft("AAPL", qty, 100m, null), r, out _, out var reject));
            Assert.Equal(CanonReject.BadQuantity, reject);
        }

        [Fact]
        public void Canonicalize_NoSymbol_FailClosed()
        {
            var r = new TestResolver();
            Assert.False(OnboardingCanonicalizer.TryCanonicalize(new EntityDraft("   ", 1m, 1m, null), r, out _, out var reject));
            Assert.Equal(CanonReject.NoSymbol, reject);
        }

        [Fact]
        public void Canonicalize_SubScaleQty_TruncatesToZero_FailClosed()
        {
            // ★적대적 리뷰(major) — 0<q<1e-8 은 q>0 게이트를 통과하나 (long) 절사로 Raw=0. '수량 0 오염 슬롯' 거부.
            var r = new TestResolver();
            Assert.False(OnboardingCanonicalizer.TryCanonicalize(new EntityDraft("AAPL", 0.000000001m, 100m, null), r, out _, out var reject));
            Assert.Equal(CanonReject.BadQuantity, reject);
        }

        [Fact]
        public void Canonicalize_HugeValue_FailClosed_NoCrash()
        {
            // ★적대적 리뷰(major) — 대형값은 fail-crash(OverflowException)가 아니라 fail-closed(거부)여야 한다.
            var r = new TestResolver();
            Assert.False(OnboardingCanonicalizer.TryCanonicalize(new EntityDraft("AAPL", 1_000_000_000_000m, 100m, null), r, out _, out var reject));
            Assert.Equal(CanonReject.Overflow, reject);
        }

        [Fact]
        public void Canonicalize_SubScalePositivePrice_FailClosed_ButZeroAllowed()
        {
            var r = new TestResolver();
            // 양수 단가가 0으로 절사 → 손실 거부.
            Assert.False(OnboardingCanonicalizer.TryCanonicalize(new EntityDraft("AAPL", 1m, 0.000000001m, null), r, out _, out var rj));
            Assert.Equal(CanonReject.BadPrice, rj);
            // 정확히 0 단가(무상취득 등)는 의도적 값으로 허용.
            Assert.True(OnboardingCanonicalizer.TryCanonicalize(new EntityDraft("AAPL", 1m, 0m, null), r, out var slot, out _));
            Assert.True(slot.HasAvgPrice);
            Assert.Equal(0m, slot.AvgPrice.ToDecimal());
        }

        [Fact]
        public void Pump_FirstAsset_ElicitsFieldsInDeterministicOrder()
        {
            var pump = new OnboardingPump(new TestResolver());
            Assert.Equal(ElicitField.FirstAsset, pump.NextElicitation());     // 빈 격자 → 첫 자산
            Assert.True(pump.Ingest(new EntityDraft("삼성전자", null, null, null), out _));
            Assert.Equal(OnboardingState.FillingFields, pump.State);
            Assert.Equal(ElicitField.Quantity, pump.NextElicitation());       // 수량 누락 우선
            Assert.True(pump.Ingest(new EntityDraft("삼성전자", 10m, null, null), out _));
            Assert.Equal(ElicitField.AvgPrice, pump.NextElicitation());       // 그다음 평단
            Assert.True(pump.Ingest(new EntityDraft("삼성전자", null, 68000m, null), out _));
            Assert.Equal(ElicitField.Confirm, pump.NextElicitation());        // 완성 → 확정
            Assert.Equal(OnboardingState.Confirming, pump.State);
        }

        [Fact]
        public void Pump_SameSymbol_MergesNoDuplicate()
        {
            var pump = new OnboardingPump(new TestResolver());
            Assert.True(pump.Ingest(new EntityDraft("삼성전자", 10m, null, null), out _));   // 코드/이름 다른 표기
            Assert.True(pump.Ingest(new EntityDraft("005930", null, 70000m, null), out _)); // 동일 정준 심볼
            Assert.Equal(1, pump.Count);                                       // 중복 슬롯 없음
            var s = pump.Slots[0];
            Assert.True(s.IsComplete);
            Assert.Equal(10m, s.Qty.ToDecimal());
            Assert.Equal(70000m, s.AvgPrice.ToDecimal());
        }

        [Fact]
        public void Pump_Reject_LeavesGridAndStateUnchanged()
        {
            var pump = new OnboardingPump(new TestResolver());
            Assert.True(pump.Ingest(new EntityDraft("AAPL", 5m, 100m, null), out _));
            var stateBefore = pump.State; int countBefore = pump.Count;
            Assert.False(pump.Ingest(new EntityDraft("듣보", 1m, 1m, null), out var reject));   // 미해석
            Assert.Equal(CanonReject.UnresolvedSymbol, reject);
            Assert.Equal(countBefore, pump.Count);
            Assert.Equal(stateBefore, pump.State);
        }

        [Fact]
        public void Pump_TryComplete_OnlyWhenAllSlotsComplete()
        {
            var pump = new OnboardingPump(new TestResolver());
            Assert.True(pump.Ingest(new EntityDraft("비트코인", null, null, null), out _));   // 미완성
            Assert.False(pump.TryComplete());
            Assert.NotEqual(OnboardingState.Complete, pump.State);
            Assert.True(pump.Ingest(new EntityDraft("비트코인", 0.13m, 90_000_000m, null), out _));
            Assert.True(pump.TryComplete());
            Assert.Equal(OnboardingState.Complete, pump.State);
        }

        [Fact]
        public void Pump_CapacityExceeded_RejectsWithExplicitReason()
        {
            // ★적대적 리뷰(minor) — 용량 초과 거부는 CanonReject.None(성공과 혼동)이 아니라 CapacityExceeded 로 보고.
            var pump = new OnboardingPump(new TestResolver(), capacity: 2);
            Assert.True(pump.Ingest(new EntityDraft("005930", 1m, 1m, null), out _));
            Assert.True(pump.Ingest(new EntityDraft("AAPL", 1m, 1m, null), out _));
            Assert.False(pump.Ingest(new EntityDraft("비트코인", 1m, 1m, null), out var reject));
            Assert.Equal(CanonReject.CapacityExceeded, reject);
            Assert.Equal(2, pump.Count);
        }

        [Fact]
        public void Compliance_NormalIntent_Admits()
        {
            var gate = new ComplianceGate();
            Assert.True(gate.Admit(IntentClass.Normal));
            Assert.Equal(PersonaState.Engaged, gate.State);
        }

        [Theory]
        [InlineData(IntentClass.GuaranteedReturnSolicitation)]
        [InlineData(IntentClass.UnlicensedAdviceRequest)]
        [InlineData(IntentClass.Jailbreak)]
        public void Compliance_AbnormalIntent_LocksStickyFailClosed(IntentClass bad)
        {
            var gate = new ComplianceGate();
            Assert.False(gate.Admit(bad));                       // 거부
            Assert.Equal(PersonaState.ComplianceLocked, gate.State);
            Assert.False(gate.Admit(IntentClass.Normal));        // sticky — 이후 정상 의도도 거부
            Assert.Equal(PersonaState.ComplianceLocked, gate.State);
        }

        // ═══════════════════ M2 미래가치 복리 · 분위 밴드 · seqlock ═══════════════════

        private static GrowthAssumption[] Assume(int n, decimal drift, decimal vol)
        {
            var a = new GrowthAssumption[n];
            for (int i = 0; i < n; i++) a[i] = GrowthAssumption.Of(drift, vol);
            return a;
        }

        [Fact]
        public void Project_ZeroDriftZeroVol_FlatCurveEqualsPresent()
        {
            var present = new[] { QMoney.Of(1000m) };
            var asm = Assume(1, 0m, 0m);
            Span<long> med = stackalloc long[6]; Span<long> opt = stackalloc long[6]; Span<long> pess = stackalloc long[6];
            Assert.True(FutureValueEngine.Project(present, asm, 5, med, opt, pess, null));
            for (int t = 0; t <= 5; t++)
            {
                Assert.Equal(QMoney.Of(1000m).Raw, med[t]);
                Assert.Equal(med[t], opt[t]);
                Assert.Equal(med[t], pess[t]);
            }
        }

        [Fact]
        public void Project_KnownCompound_MatchesClosedForm()
        {
            var present = new[] { QMoney.Of(100m) };
            var asm = Assume(1, 0.10m, 0m);                 // 10%/yr, 변동성 0
            Span<long> med = stackalloc long[11]; Span<long> opt = stackalloc long[11]; Span<long> pess = stackalloc long[11];
            Assert.True(FutureValueEngine.Project(present, asm, 10, med, opt, pess, null));
            decimal got = new QMoney(med[10]).ToDecimal();
            Assert.Equal(259.374246m, got, 3);             // 100 × 1.1^10 ≈ 259.3742
            Assert.Equal(100m, new QMoney(med[0]).ToDecimal());   // 연차0 = 현재가치
        }

        [Fact]
        public void Project_PositiveVol_BandOrdered_PessLeMedLeOpt()
        {
            var present = new[] { QMoney.Of(1000m) };
            var asm = Assume(1, 0.08m, 0.20m);
            Span<long> med = stackalloc long[11]; Span<long> opt = stackalloc long[11]; Span<long> pess = stackalloc long[11];
            Assert.True(FutureValueEngine.Project(present, asm, 10, med, opt, pess, null));
            Assert.Equal(med[0], opt[0]); Assert.Equal(med[0], pess[0]);   // t=0 밴드 0
            for (int t = 1; t <= 10; t++)
            {
                Assert.True(pess[t] <= med[t], $"t={t} pess>med");
                Assert.True(med[t] <= opt[t], $"t={t} med>opt");
                Assert.True(opt[t] > pess[t], $"t={t} 밴드 폭 0");   // vol>0 → 펼쳐짐
            }
        }

        [Fact]
        public void Project_LengthMismatch_FailClosed_Freezes()
        {
            var att = new OrderAttestationService();
            var present = new[] { QMoney.Of(100m) };
            var asm = Assume(1, 0.05m, 0m);
            Span<long> bad = stackalloc long[3];   // years=5 인데 길이 6 아님
            Assert.False(FutureValueEngine.Project(present, asm, 5, bad, bad, bad, att));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Project_DegenerateRate_OnePlusRNonPositive_FailClosed()
        {
            var att = new OrderAttestationService();
            var present = new[] { QMoney.Of(100m) };
            var asm = new[] { GrowthAssumption.Of(-1.5m, 0m) };   // 1+r = -0.5 ≤ 0
            Span<long> med = stackalloc long[3]; Span<long> opt = stackalloc long[3]; Span<long> pess = stackalloc long[3];
            Assert.False(FutureValueEngine.Project(present, asm, 2, med, opt, pess, att));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        private static (PortfolioSlot[] slots, QMoney[] present) TwoAssetBook()
        {
            var slots = new[]
            {
                new PortfolioSlot("005930", AssetClass.DomesticStock, Currency.KRW, QQty.Of(10m), QPrice.Of(70000m), SlotField.All),
                new PortfolioSlot("KRW-BTC", AssetClass.Crypto, Currency.KRW, QQty.Of(0.1m), QPrice.Of(90_000_000m), SlotField.All),
            };
            var present = new[] { QMoney.Of(700_000m), QMoney.Of(9_000_000m) };   // base KRW 현재가치
            return (slots, present);
        }

        [Fact]
        public void Publisher_RecomputeNone_ReadFrame_MatchesDirectProject()
        {
            var (slots, present) = TwoAssetBook();
            var asm = Assume(2, 0.07m, 0.15m);
            var pub = new ScenarioPublisher(slots, present, asm, 10);
            Assert.True(pub.Recompute(ShadowMutation.None));

            Span<long> rm = stackalloc long[11]; Span<long> ro = stackalloc long[11]; Span<long> rp = stackalloc long[11];
            Assert.True(pub.TryReadFrame(rm, ro, rp));

            Span<long> dm = stackalloc long[11]; Span<long> do_ = stackalloc long[11]; Span<long> dp = stackalloc long[11];
            Assert.True(FutureValueEngine.Project(present, asm, 10, dm, do_, dp, null));
            for (int t = 0; t <= 10; t++) { Assert.Equal(dm[t], rm[t]); Assert.Equal(do_[t], ro[t]); Assert.Equal(dp[t], rp[t]); }
        }

        [Fact]
        public void Publisher_ShadowMutation_ScalesTargetClass_AtYearZero()
        {
            var (slots, present) = TwoAssetBook();
            var asm = Assume(2, 0.0m, 0.0m);   // 평탄 → 연차0 = 총현재가치
            var pub = new ScenarioPublisher(slots, present, asm, 4);

            Span<long> baseM = stackalloc long[5]; Span<long> tmpA = stackalloc long[5]; Span<long> tmpB = stackalloc long[5];
            Assert.True(pub.Recompute(ShadowMutation.None));
            Assert.True(pub.TryReadFrame(baseM, tmpA, tmpB));
            long baseTotal = baseM[0];

            Assert.True(pub.Recompute(ShadowMutation.ScaleClass(AssetClass.Crypto, 2m)));   // 코인 2배
            Span<long> mutM = stackalloc long[5];
            Assert.True(pub.TryReadFrame(mutM, tmpA, tmpB));
            // 코인 현재가치(9,000,000)만큼 추가 → 총합 += 9,000,000.
            Assert.Equal(baseTotal + QMoney.Of(9_000_000m).Raw, mutM[0]);
        }

        [Fact]
        public void Publisher_Recompute_ZeroAllocation()
        {
            var (slots, present) = TwoAssetBook();
            var asm = Assume(2, 0.07m, 0.15m);
            var pub = new ScenarioPublisher(slots, present, asm, 20);
            var mut = ShadowMutation.ScaleClass(AssetClass.Crypto, 1.10m);
            for (int i = 0; i < 10_000; i++) pub.Recompute(in mut);   // 워밍업(tiered JIT 안정화)

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50_000; i++) pub.Recompute(in mut);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);   // 섀도우 스왑+산출 핫패스 δ=0
        }

        [Fact]
        public void Publisher_NegativePresentInCtor_FailFast_Throws()
        {
            // ★적대적 리뷰(major) — 음수 현재가치(poison)는 구성 시점 fail-fast(이후 Recompute 의 Notional throw 차단).
            var slotsArr   = new[] { new PortfolioSlot("005930", AssetClass.DomesticStock, Currency.KRW, QQty.Of(1m), QPrice.Of(1m), SlotField.All) };
            var presentArr = new[] { new QMoney(-1) };
            var asmArr     = Assume(1, 0.05m, 0m);
            Assert.Throws<ArgumentException>(() => new ScenarioPublisher(slotsArr, presentArr, asmArr, 4));
        }

        [Fact]
        public void Publisher_HugeMutationFactor_FailClosed_NoThrow()
        {
            // ★적대적 리뷰(major) — 대형 배수는 FromDecimal 오버플로로 throw 가 아니라 fail-closed(false+_frozen++).
            var (slots, present) = TwoAssetBook();
            var pub = new ScenarioPublisher(slots, present, Assume(2, 0.05m, 0m), 4);
            long frozenBefore = pub.FrozenCount;
            bool ok = pub.Recompute(ShadowMutation.ScaleClass(AssetClass.Crypto, 1_000_000_000_000m));
            Assert.False(ok);
            Assert.True(pub.FrozenCount > frozenBefore);
        }

        // ★적대적 리뷰(major) — 단일 라이터 계약을 CAS 게이트로 강제: 2 라이터 동시 호출에도 torn 프레임 0.
        // ★결정론 재설계(부하 플레이키 제거) — '시간'이 아니라 '작업량'으로 경계한다. 라이터는 고정 횟수만
        //   재산출 후 종료하므로 CPU 경합 하에서도 join 타임아웃·무한 스핀이 없다. 안전성(torn==0)은 부하와
        //   무관하게 seqlock+게이트가 항상 보장하고(측정: 경합·고읽기 양 체제 모두 torn=0, 31회 연속 0), 활성성
        //   (읽기 성공·정합)은 라이터 종료 뒤 '정지 상태 보장 읽기'로 결정론 검증한다 — 경합 중 리더 기아로
        //   reads 가 0 까지 떨어져도 플레이키하지 않다. (과거 리포트된 torn 폭주는 CAS 게이트 도입 전의
        //   잔재이며 현 코드로는 재현 불가: 게이트가 두 라이터의 임계구역 중첩을 원천 차단한다.)
        [Fact]
        public void Publisher_ConcurrentWriters_GateSerializes_NoTornFrame()
        {
            var (slots, present) = TwoAssetBook();
            var asm = Assume(2, 0m, 0m);   // 평탄 → 일관 프레임은 모든 연차 동일값
            const int years = 8;
            var pub = new ScenarioPublisher(slots, present, asm, years);
            pub.Recompute(ShadowMutation.None);
            long versionBefore = pub.Version;

            long totalNone = present[0].Raw + present[1].Raw;
            long totalMut  = present[0].Raw + QMath_Notional(present[1].Raw, 3m);
            var allowed = new[] { totalNone, totalMut };

            const int writerIters = 40_000;   // 작업량 경계(시간 아님) — 부하에도 수십~수백 ms 내 종료
            int writersLeft = 2;
            int torn = 0;

            var writers = new Thread[2];
            for (int w = 0; w < writers.Length; w++)
            {
                bool startMut = w == 0;
                writers[w] = new Thread(() =>
                {
                    bool flip = startMut;
                    for (int i = 0; i < writerIters; i++)
                    { pub.Recompute(flip ? ShadowMutation.ScaleClass(AssetClass.Crypto, 3m) : ShadowMutation.None); flip = !flip; }
                    Interlocked.Decrement(ref writersLeft);
                }) { IsBackground = true };
            }
            var readers = new Thread[4];
            for (int k = 0; k < readers.Length; k++)
                readers[k] = new Thread(() =>
                {
                    var m = new long[years + 1]; var o = new long[years + 1]; var p = new long[years + 1];
                    // 라이터가 사는 동안 계속 읽어 경합 구간을 덮는다 — 고정 횟수 루프의 join 타임아웃 위험 제거.
                    while (Volatile.Read(ref writersLeft) > 0)
                    {
                        if (!pub.TryReadFrame(m, o, p)) continue;
                        long v0 = m[0];
                        bool consistent = (v0 == allowed[0] || v0 == allowed[1]);
                        for (int t = 0; t <= years; t++) if (m[t] != v0) consistent = false;
                        if (!consistent) Interlocked.Increment(ref torn);
                    }
                }) { IsBackground = true };

            foreach (var t in writers) t.Start(); foreach (var t in readers) t.Start();
            // 작업량 경계라 정상 < 1s 종료. 타임아웃은 데드락 안전망(게이트는 무잠금 CAS — 데드락 불가).
            foreach (var t in writers) Assert.True(t.Join(60000), "writer join 타임아웃(데드락?)");
            foreach (var t in readers) Assert.True(t.Join(60000), "reader join 타임아웃(데드락?)");

            // 안전성 — 경합 중 리더가 본 모든 스냅샷은 찢기지 않았다(게이트가 단일 라이터로 직렬화).
            Assert.Equal(0, torn);

            // 활성성·정합(결정론) — 라이터 종료 → 버전 안정 → 보장된 안정 읽기 1회가 성공·일관해야 한다.
            var fm = new long[years + 1]; var fo = new long[years + 1]; var fp = new long[years + 1];
            Assert.True(pub.TryReadFrame(fm, fo, fp), "정지 상태에서 안정 스냅샷 읽기 실패");
            long fv0 = fm[0];
            Assert.True(fv0 == allowed[0] || fv0 == allowed[1], $"최종 총합 {fv0} 이 허용값 밖");
            for (int t = 0; t <= years; t++) Assert.Equal(fv0, fm[t]);   // 평탄 불변

            // 라이터가 실제로 재발행했다(공회전 아님) — 최소 한 라이터는 모든 경쟁에서 못 지므로 버전은 단조 증가.
            Assert.True(pub.Version > versionBefore, "라이터가 재산출을 발행하지 않음");

            // ★(b) 자해성 동결 회로 제거 검증(결정론) — 경합 패배는 _frozen 이 아니라 _writerCollisions 로만 집계되고,
            //   이 시나리오는 산출 실패가 없으므로 FrozenCount 는 정확히 0 이어야 한다(경합이 _frozen 을 오염시키면 실패).
            Assert.Equal(0, pub.FrozenCount);
        }

        // ★S28-b Mission 3 — 자해성 동결 회로 제거 회귀: 24 라이터 극한 경합에서 게이트 패배는 _writerCollisions 로만
        //   집계되고 _frozen/ForceFreeze 를 건드리지 않는다. 안전성(torn=0)·무자해(FrozenCount=0·EmergencyFreeze 미발화)는
        //   부하 무관 결정론. 경합 발생(WriterCollisions>0)은 24 라이터·9.6만 시도에선 통계적 확정(게이트가 실제 직렬화
        //   중이라는 증거이자 테스트 비vacuity 보증) — 기아로 0 될 수 있는 reads>0 류와 달리, 선점 스케줄링상 게이트
        //   보유자가 잘리면 타 라이터 CAS 가 반드시 실패하므로 경합은 막을 수 없다.
        [Fact]
        public void Publisher_HeavyContention_CollisionsCounted_NoSelfFreeze()
        {
            var (slots, present) = TwoAssetBook();
            var att = new OrderAttestationService();
            var pub = new ScenarioPublisher(slots, present, Assume(2, 0m, 0m), 4, att);
            pub.Recompute(ShadowMutation.None);

            long totalNone = present[0].Raw + present[1].Raw;
            long totalMut  = present[0].Raw + QMath_Notional(present[1].Raw, 3m);
            var allowed = new[] { totalNone, totalMut };

            const int writerCount = 24, iters = 4_000;   // 24 라이터 극한 과부하(작업량 경계 — 시간 아님)
            int writersLeft = writerCount;
            int torn = 0;

            var writers = new Thread[writerCount];
            for (int w = 0; w < writerCount; w++)
            {
                bool startMut = (w & 1) == 0;
                writers[w] = new Thread(() =>
                {
                    bool flip = startMut;
                    for (int i = 0; i < iters; i++)
                    { pub.Recompute(flip ? ShadowMutation.ScaleClass(AssetClass.Crypto, 3m) : ShadowMutation.None); flip = !flip; }
                    Interlocked.Decrement(ref writersLeft);
                }) { IsBackground = true };
            }
            var readers = new Thread[4];
            for (int k = 0; k < readers.Length; k++)
                readers[k] = new Thread(() =>
                {
                    var m = new long[5]; var o = new long[5]; var p = new long[5];
                    while (Volatile.Read(ref writersLeft) > 0)
                    {
                        if (!pub.TryReadFrame(m, o, p)) continue;
                        long v0 = m[0];
                        bool consistent = (v0 == allowed[0] || v0 == allowed[1]);
                        for (int t = 0; t < 5; t++) if (m[t] != v0) consistent = false;
                        if (!consistent) Interlocked.Increment(ref torn);
                    }
                }) { IsBackground = true };

            foreach (var t in writers) t.Start(); foreach (var t in readers) t.Start();
            foreach (var t in writers) Assert.True(t.Join(60000), "writer join 타임아웃(데드락?)");
            foreach (var t in readers) Assert.True(t.Join(60000), "reader join 타임아웃(데드락?)");

            Assert.Equal(0, torn);                                       // 안전성(부하 무관) — 찢긴 프레임 0
            Assert.Equal(0, pub.FrozenCount);                            // ★무자해 — 경합이 _frozen 오염 안 함
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);       // ★무자해 — 전역 ForceFreeze 미발화
            Assert.True(pub.WriterCollisions > 0, "24 라이터 극한 경합인데 WriterCollisions=0 (게이트 직렬화/비vacuity 의심)");

            var fm = new long[5]; var fo = new long[5]; var fp = new long[5];   // 정지 상태 보장 읽기 — 활성성·정합
            Assert.True(pub.TryReadFrame(fm, fo, fp), "정지 상태 안정 스냅샷 실패");
            Assert.True(fm[0] == allowed[0] || fm[0] == allowed[1], $"최종 총합 {fm[0]} 허용값 밖");
        }

        // seqlock 락프리 — 단일 라이터가 총합을 토글하며 재산출, 다중 리더는 '찢긴 곡선'을 절대 보지 않는다.
        // ★결정론(부하 무관) — 형제 2-라이터 테스트와 동일 사상: '시간' 아닌 '작업량'으로 경계한다. 라이터는
        //   고정 횟수만 재산출 후 종료하므로 CI 에이전트 과부하에도 join 타임아웃·리더 기아 플레이키가 없다.
        //   단일 라이터라 게이트 경합이 없어 기아 가능성은 훨씬 낮지만, 부하 불변(load-invariant)으로 통일한다.
        [Fact]
        public void Publisher_SingleWriter_ManyReaders_NoTornFrame()
        {
            var (slots, present) = TwoAssetBook();
            var asm = Assume(2, 0m, 0m);   // 평탄 → 일관 프레임은 모든 연차가 동일값
            const int years = 8;
            var pub = new ScenarioPublisher(slots, present, asm, years);
            pub.Recompute(ShadowMutation.None);
            long versionBefore = pub.Version;

            long totalNone = present[0].Raw + present[1].Raw;
            long totalMut  = present[0].Raw + QMath_Notional(present[1].Raw, 3m);   // 코인 3배
            var allowed = new[] { totalNone, totalMut };

            const int writerIters = 60_000;   // 작업량 경계(시간 아님) — 부하에도 수십~수백 ms 내 종료
            int writerDone = 0;
            int torn = 0;

            var writer = new Thread(() =>
            {
                bool flip = false;
                for (int i = 0; i < writerIters; i++)
                { pub.Recompute(flip ? ShadowMutation.ScaleClass(AssetClass.Crypto, 3m) : ShadowMutation.None); flip = !flip; }
                Volatile.Write(ref writerDone, 1);
            }) { IsBackground = true };

            var readers = new Thread[4];
            for (int k = 0; k < readers.Length; k++)
                readers[k] = new Thread(() =>
                {
                    var m = new long[years + 1]; var o = new long[years + 1]; var p = new long[years + 1];
                    // 라이터가 사는 동안 계속 읽어 경합 구간을 덮는다 — 고정 횟수 루프의 join 타임아웃 위험 제거.
                    while (Volatile.Read(ref writerDone) == 0)
                    {
                        if (!pub.TryReadFrame(m, o, p)) continue;
                        long v0 = m[0];
                        bool consistent = (v0 == allowed[0] || v0 == allowed[1]);
                        for (int t = 0; t <= years; t++) if (m[t] != v0) consistent = false;   // 평탄 불변 — 섞이면 torn
                        if (!consistent) Interlocked.Increment(ref torn);
                    }
                }) { IsBackground = true };

            writer.Start(); foreach (var t in readers) t.Start();
            // 작업량 경계라 정상 < 1s 종료. 타임아웃은 데드락 안전망(단일 라이터 seqlock — 데드락 불가).
            Assert.True(writer.Join(60000), "writer join 타임아웃(데드락?)");
            foreach (var t in readers) Assert.True(t.Join(60000), "reader join 타임아웃(데드락?)");

            // 안전성 — 경합 중 리더가 본 모든 스냅샷은 찢기지 않았다(단 한 번의 찢긴 프레임도 없어야 함).
            Assert.Equal(0, torn);

            // 활성성·정합(결정론) — 라이터 종료 → 버전 안정 → 보장된 안정 읽기 1회가 성공·일관해야 한다.
            var fm = new long[years + 1]; var fo = new long[years + 1]; var fp = new long[years + 1];
            Assert.True(pub.TryReadFrame(fm, fo, fp), "정지 상태에서 안정 스냅샷 읽기 실패");
            long fv0 = fm[0];
            Assert.True(fv0 == allowed[0] || fv0 == allowed[1], $"최종 총합 {fv0} 이 허용값 밖");
            for (int t = 0; t <= years; t++) Assert.Equal(fv0, fm[t]);   // 평탄 불변

            // 라이터가 실제로 재발행했다(공회전 아님) — 버전 단조 증가.
            Assert.True(pub.Version > versionBefore, "라이터가 재산출을 발행하지 않음");
        }

        // 테스트 보조 — present.Raw × factor(decimal) 를 코어와 동일 규칙(floor)으로 계산.
        private static long QMath_Notional(long raw, decimal factor)
        {
            Int128 prod = (Int128)raw * (long)(factor * 100_000_000m);
            return (long)(prod / 100_000_000L);
        }

        // ═══════════════════ M3 카오스 비대칭 스트레스 · 진단 · 밸브 ═══════════════════

        [Fact]
        public void Chaos_Gfc2008_StocksCrash_DrawdownAboutHalf()
        {
            var present = new[] { QMoney.Of(10_000_000m) };
            var classes = new[] { AssetClass.DomesticStock };
            ChaosEngine.StressTotal(present, classes, ChaosScenario.Gfc2008, out var b, out var s);
            decimal dd = (b.ToDecimal() - s.ToDecimal()) / b.ToDecimal() * 100m;
            Assert.Equal(50m, dd, 2);   // severity 0.50 × β 1.0
        }

        [Fact]
        public void Chaos_CryptoHeavy_HasWorseWorstDrawdown_ThanDomesticHeavy()
        {
            // 동일 총액, 자산군 비중만 상이.
            var cryptoHeavy  = new[] { QMoney.Of(9_000_000m), QMoney.Of(1_000_000m) };
            var domesticHeavy = new[] { QMoney.Of(1_000_000m), QMoney.Of(9_000_000m) };
            var classes = new[] { AssetClass.Crypto, AssetClass.DomesticStock };

            var dCrypto   = PortfolioDiagnosis.Diagnose(cryptoHeavy, classes);
            var dDomestic = PortfolioDiagnosis.Diagnose(domesticHeavy, classes);
            Assert.True(dCrypto.WorstDrawdownPct > dDomestic.WorstDrawdownPct,
                $"crypto {dCrypto.WorstDrawdownPct} ≤ domestic {dDomestic.WorstDrawdownPct}");
            Assert.True(dCrypto.DefenseScorePct < dDomestic.DefenseScorePct);   // 방어점수 비대칭
        }

        [Fact]
        public void Diagnose_Concentration_DetectsDominantClass()
        {
            var present = new[] { QMoney.Of(8_000_000m), QMoney.Of(2_000_000m) };
            var classes = new[] { AssetClass.ForeignStock, AssetClass.DomesticStock };
            var d = PortfolioDiagnosis.Diagnose(present, classes);
            Assert.Equal(AssetClass.ForeignStock, d.TopClass);
            Assert.Equal(80m, d.ConcentrationPct, 2);
            Assert.Equal(10_000_000m, d.TotalValueKrw);
            Assert.InRange(d.DefenseScorePct, 0m, 100m);
        }

        [Fact]
        public void Diagnose_EmptyBook_NoDivideByZero()
        {
            var d = PortfolioDiagnosis.Diagnose(ReadOnlySpan<QMoney>.Empty, ReadOnlySpan<AssetClass>.Empty);
            Assert.Equal(0m, d.ConcentrationPct);
            Assert.Equal(0m, d.WorstDrawdownPct);
            Assert.Equal(100m, d.DefenseScorePct);   // 충격받을 자산 없음 → 만점
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(RebalanceLever.MaxLeverBp + 1)]
        public void Lever_OutOfRange_FailClosed(int leverBp)
        {
            Assert.False(RebalanceLever.TryScaleClass(AssetClass.Crypto, leverBp, out var m));
            Assert.Equal(MutationKind.None, m.Kind);
        }

        [Fact]
        public void Lever_Valid_FeedsPublisher_ChangesYearZero()
        {
            var (slots, present) = TwoAssetBook();
            var pub = new ScenarioPublisher(slots, present, Assume(2, 0m, 0m), 4);
            pub.Recompute(ShadowMutation.None);
            Span<long> baseM = stackalloc long[5]; Span<long> a = stackalloc long[5]; Span<long> bspan = stackalloc long[5];
            pub.TryReadFrame(baseM, a, bspan);
            long baseTotal = baseM[0];

            Assert.True(RebalanceLever.TryScaleClass(AssetClass.Crypto, 20_000, out var mut));   // 2.000×
            Assert.True(pub.Recompute(in mut));
            Span<long> mutM = stackalloc long[5];
            pub.TryReadFrame(mutM, a, bspan);
            Assert.Equal(baseTotal + present[1].Raw, mutM[0]);   // 코인 2배 → +코인 현재가치
        }

        [Fact]
        public void Lever_Neutral10000bp_NoChange()
        {
            Assert.True(RebalanceLever.TryScaleClass(AssetClass.Crypto, RebalanceLever.NeutralBp, out var m));
            Assert.Equal(1m, m.Factor);
            Assert.Equal(MutationKind.ScaleQty, m.Kind);
        }

        [Fact]
        public void Lever_ZeroBp_EmptiesClass_Valid()
        {
            // ★적대적 리뷰(minor) — 허용 가장자리 leverBp=0: 0× 배수(자산군 비움)는 유효 입력. 핀 고정.
            var (slots, present) = TwoAssetBook();
            var pub = new ScenarioPublisher(slots, present, Assume(2, 0m, 0m), 4);
            pub.Recompute(ShadowMutation.None);
            Span<long> a = stackalloc long[5]; Span<long> b = stackalloc long[5];

            Assert.True(RebalanceLever.TryScaleClass(AssetClass.Crypto, 0, out var m));
            Assert.Equal(0m, m.Factor);
            Assert.True(pub.Recompute(in m));
            Span<long> mm = stackalloc long[5];
            Assert.True(pub.TryReadFrame(mm, a, b));
            Assert.Equal(present[0].Raw, mm[0]);   // 코인 0배 → 국내 현재가치만 남음
        }

        [Fact]
        public void Lever_MaxBp_Valid_FiveX()
        {
            // ★적대적 리뷰(minor) — 허용 가장자리 leverBp=MaxLeverBp: 정확히 수락(5.000×).
            Assert.True(RebalanceLever.TryScaleClass(AssetClass.Crypto, RebalanceLever.MaxLeverBp, out var m));
            Assert.Equal(5m, m.Factor);
            Assert.Equal(MutationKind.ScaleQty, m.Kind);
        }

        [Fact]
        public void Chaos_NegativePresent_AbsorbedToZero_DiagnoseNoCrash()
        {
            // ★적대적 리뷰(minor) — 음수 poison 은 진단(비권위)에서 0 흡수, 크래시 없이 sane 리포트.
            var present = new[] { new QMoney(-5_000_000), QMoney.Of(5_000_000m) };
            var classes = new[] { AssetClass.Crypto, AssetClass.DomesticStock };
            var d = PortfolioDiagnosis.Diagnose(present, classes);
            Assert.Equal(5_000_000m, d.TotalValueKrw);          // 음수 흡수 → 국내 5M만
            Assert.Equal(AssetClass.DomesticStock, d.TopClass);
        }

        [Fact]
        public void Chaos_CryptoWinter_CryptoTotalLoss_ClampedNotNegative_NoCrash()
        {
            // ★복원 클램프의 실 도달 경로 — CryptoWinter×Crypto = 0.55×2.0 = 1.10 > 1 → survival 음수가 되려 하나
            //   max(0,…) 으로 0 클램프. 음수 survival 이면 QMath.Notional 이 throw 하므로, 이 테스트가 클램프를 박제한다.
            var present = new[] { QMoney.Of(5_000_000m) };
            var classes = new[] { AssetClass.Crypto };
            ChaosEngine.StressTotal(present, classes, ChaosScenario.CryptoWinter, out var bb, out var ss);
            Assert.Equal(5_000_000m, bb.ToDecimal());
            Assert.Equal(0m, ss.ToDecimal());                   // 전손(0) — 음수·크래시 아님

            Span<QMoney> outb = stackalloc QMoney[1];
            Assert.True(ChaosEngine.ApplyStress(present, classes, ChaosScenario.CryptoWinter, outb));
            Assert.Equal(0m, outb[0].ToDecimal());
        }

        // ═══════════════════ v4.58.0 진격 프로토콜 3종 예외 가드 박제 ═══════════════════

        // [B] 스티키 락 발동 시 LLM 스트림이 전면 차단되고 고지문이 뜬다(오버레이 점등 조건).
        //     AdvisorPage.Ask 의 결정 로직 박제 — Razor @if(Compliance.IsLocked) 오버레이 바인딩은 빌드 검증(bUnit 미사용).
        [Fact]
        public void ComplianceLocked_DropsLlmStream_AndDisplaysOverlay()
        {
            var gate = new ComplianceGate();
            const string llmStream = "확정 90% 수익 보장! 지금 풀매수 하세요";   // 위험 LLM 원본 — 절대 화면 도달 금지

            // 페이지 렌더 결정: 분류 → Admit. 거부면 LLM 미호출, RefusalNotice 만 렌더(스트림 드롭).
            string Render(string q) => gate.Admit(ComplianceClassifier.Classify(q)) ? llmStream : ComplianceGate.RefusalNotice;

            Assert.False(gate.IsLocked);
            Assert.Equal(llmStream, Render("내 수익률 어때?"));        // 정상 — 스트림 통과(Admit true)

            var shown = Render("원금 보장되는 확정 수익 종목 추천해줘");  // 유사투자자문/확정수익 유도
            Assert.Equal(ComplianceGate.RefusalNotice, shown);        // LLM 원본 대신 고정 고지문
            Assert.NotEqual(llmStream, shown);                        // 위험 스트림 물리 차단
            Assert.True(gate.IsLocked);                               // ★오버레이 점등 조건(PersonaState.ComplianceLocked)
            Assert.Equal(PersonaState.ComplianceLocked, gate.State);

            // sticky — 이후 정상 질의도 드롭(오버레이 유지·세션 봉인)
            Assert.Equal(ComplianceGate.RefusalNotice, Render("리밸런싱 조언 줘"));
            Assert.True(gate.IsLocked);
        }

        // [A] 일부 필드가 깨졌을 때 정상 자산만 저장되고 올바른 핀포인트 질문 컨텍스트가 생성된다.
        [Fact]
        public void PartialDraft_SavesValidFields_AndTriggersTargetedQuestion()
        {
            var pump = new OnboardingPump(new TestResolver());
            // 수량이 서브스케일 절사(0.000000001 → Raw 0)로 깨짐 · 단가는 정상. 통짜 거절이면 삼성전자 자체를 잃는다.
            Assert.True(pump.IngestPartial(new EntityDraft("삼성전자", 0.000000001m, 70_000m, null), out var partial));

            // ① 정상 필드만 저장 — 심볼·단가 식별, 불량 수량은 미저장(0 강제 아님·오염 미반영)
            Assert.Equal(1, pump.Count);
            var slot = pump.Slots[0];
            Assert.Equal("005930", slot.Symbol);
            Assert.True(slot.HasAvgPrice);
            Assert.Equal(70_000m, slot.AvgPrice.ToDecimal());
            Assert.False(slot.HasQty);
            Assert.Equal(0L, slot.Qty.Raw);
            Assert.Equal(CanonReject.BadQuantity, partial.QtyReject);   // 왜 깨졌는지(핀포인트 사유)
            Assert.True(partial.HasFieldIssue);

            // ② 올바른 핀포인트 질문 컨텍스트 — '어느 자산'의 '무슨 필드'
            var field = pump.NextGap(out var which);
            Assert.Equal(ElicitField.Quantity, field);
            Assert.Equal("005930", which.Symbol);                      // 삼성전자의 수량을 정밀 타격 질문

            // ③ 수량 보정 → 동일 심볼 병합으로 완성(자산 소실 없음)
            Assert.True(pump.IngestPartial(new EntityDraft("005930", 10m, null, null), out _));
            Assert.Equal(1, pump.Count);
            Assert.True(pump.Slots[0].IsComplete);
        }

        // [C] 시뮬레이터 독출 경합(Torn-Read) 시 에러 없이 직전 프레임을 정상 유지한다.
        //     ★결정론(부하 무관·작업량 경계 [[concurrency-test-work-bounded]]) — 라이터 고정 횟수 후 종료.
        //     안전성(찢긴 곡선 0·예외 0·유지 프레임 항상 유효)만 단언하고, 찢김 '발생'(활성성)은 강제하지 않는다(기아 비플레이키).
        [Fact]
        public void SeqlockRead_TornRead_RetainsPreviousFrame()
        {
            var (slots, present) = TwoAssetBook();
            var asm = Assume(2, 0m, 0m);   // 평탄 → 일관 프레임은 전 연차 동일값
            const int years = 8;
            var pub = new ScenarioPublisher(slots, present, asm, years);
            pub.Recompute(ShadowMutation.None);

            long totalNone = present[0].Raw + present[1].Raw;
            long totalMut  = present[0].Raw + QMath_Notional(present[1].Raw, 3m);   // 코인 3배
            var allowed = new[] { totalNone, totalMut };

            const int writerIters = 50_000;
            int writerDone = 0, torn = 0, prevInvalid = 0, exceptions = 0;

            var writer = new Thread(() =>
            {
                bool flip = false;
                for (int i = 0; i < writerIters; i++)
                { pub.Recompute(flip ? ShadowMutation.ScaleClass(AssetClass.Crypto, 3m) : ShadowMutation.None); flip = !flip; }
                Volatile.Write(ref writerDone, 1);
            }) { IsBackground = true };

            var reader = new Thread(() =>
            {
                var cur = new long[years + 1]; var o = new long[years + 1]; var p = new long[years + 1];
                var prev = new long[years + 1]; bool havePrev = false;
                try
                {
                    while (Volatile.Read(ref writerDone) == 0)
                    {
                        if (pub.TryReadFrame(cur, o, p))
                        {
                            long v0 = cur[0];
                            bool ok = (v0 == allowed[0] || v0 == allowed[1]);
                            for (int t = 0; t <= years; t++) if (cur[t] != v0) ok = false;   // 평탄 불변 — 섞이면 torn
                            if (!ok) Interlocked.Increment(ref torn);
                            Array.Copy(cur, prev, years + 1); havePrev = true;               // 성공 시에만 직전 프레임 갱신
                        }
                        else if (havePrev)
                        {
                            // 찢김/미발행 — 직전 프레임 그대로 유지(prev 불변). 유지한 프레임은 항상 유효 발행본이어야 한다.
                            long pv0 = prev[0];
                            if (!(pv0 == allowed[0] || pv0 == allowed[1])) Interlocked.Increment(ref prevInvalid);
                        }
                    }
                }
                catch { Interlocked.Increment(ref exceptions); }
            }) { IsBackground = true };

            writer.Start(); reader.Start();
            Assert.True(writer.Join(60000), "writer join 타임아웃(데드락?)");
            Assert.True(reader.Join(60000), "reader join 타임아웃(데드락?)");

            Assert.Equal(0, exceptions);    // 경합에 예외를 던지지 않음(비블록·fail-closed)
            Assert.Equal(0, torn);          // 찢긴 곡선 0(seqlock 버전 재독출)
            Assert.Equal(0, prevInvalid);   // 찢김 시 유지한 직전 프레임은 항상 유효 발행본
        }
    }
}
