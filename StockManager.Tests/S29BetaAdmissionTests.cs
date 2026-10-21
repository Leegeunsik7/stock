using System;
using Xunit;

namespace StockManager.Tests
{
    // S29 Path Beta — 멀티-자산 펌프 진입 검문(흐름 제어 Seam).
    //   §1 정렬 통과(혼재 자산) §2 비정렬 서브틱 fail-closed(silent skew 차단) §3 미등록/오염/음수 거부
    //   §4 링 진입 결속(통과분만 발행) §5 무할당 §6 카운터.
    public sealed class S29BetaAdmissionTests
    {
        // KRW 정수(1원) · USD 센트(0.01) · 코인(사토시 수량) — S27-d 와 동일 격자 매트릭스.
        private static QTickRegistry Reg() => new(new[]
        {
            ("005930",  new QTickSpec(QPrice.Of(1m),    QQty.Of(1m))),
            ("AAPL",    new QTickSpec(QPrice.Of(0.01m), QQty.Of(1m))),
            ("KRW-BTC", new QTickSpec(QPrice.Of(1000m), QQty.Of(0.00000001m))),
        });

        // ═══════════════════ §1 정렬 통과 ═══════════════════

        [Fact]
        public void Admit_AlignedMultiAsset_AllPass_NoFreeze()
        {
            var att  = new OrderAttestationService();
            var gate = new QAdmissionGate(Reg(), att);

            Assert.Equal(AdmitResult.Admitted, gate.TryAdmit("005930",  70000m,    10m,    OrderSide.Buy,  out _, out _, out _));
            Assert.Equal(AdmitResult.Admitted, gate.TryAdmit("AAPL",    213.92m,   3m,     OrderSide.Sell, out _, out _, out _));
            Assert.Equal(AdmitResult.Admitted, gate.TryAdmit("KRW-BTC", 98370000m, 0.255m, OrderSide.Buy,  out _, out _, out _));
            Assert.Equal(3, gate.Admitted);
            Assert.Equal(0, gate.RejectedTotal);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Admit_OnGridPrice_PreservedExactly()
        {
            // 격자 위 입력은 왜곡 0 으로 통과 — out QPrice 가 입력을 정확 보존(silent skew 0).
            var gate = new QAdmissionGate(Reg());
            Assert.Equal(AdmitResult.Admitted, gate.TryAdmit("AAPL", 100.10m, 1m, OrderSide.Buy, out var p, out var q, out var spec));
            Assert.Equal(100.10m, p.ToDecimal());     // 센트 격자 → 왜곡 없음
            Assert.Equal(1m, q.ToDecimal());
            Assert.Equal(QPrice.Of(0.01m), spec.Tick);
        }

        // ═══════════════════ §2 비정렬 서브틱 fail-closed ═══════════════════

        [Fact]
        public void Admit_SubTickMisaligned_FailClosed_NotSnapped()
        {
            // ★핵심 — AAPL 센트 격자(0.01)에서 $100.005 는 서브-센트(비정렬). 진입은 '스냅하지 않고 거부'한다.
            var att  = new OrderAttestationService();
            var gate = new QAdmissionGate(Reg(), att);
            Assert.Equal(AdmitResult.RejectMisaligned, gate.TryAdmit("AAPL", 100.005m, 1m, OrderSide.Buy, out _, out _, out _));
            Assert.Equal(1, gate.RejectedMisaligned);
            Assert.Equal(0, gate.Admitted);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Admit_MisalignedQty_Coin_FailClosed()
        {
            // 005930(KRW 주식)은 수량 step=1주(정수). 0.5주는 비정렬 → 거부(분할 주식 오염 차단).
            var att  = new OrderAttestationService();
            var gate = new QAdmissionGate(Reg(), att);
            Assert.Equal(AdmitResult.RejectMisaligned, gate.TryAdmit("005930", 70000m, 0.5m, OrderSide.Buy, out _, out _, out _));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Admit_WrongCoarseGrid_RejectsInsteadOfSilentSkew()
        {
            // ★Silent Skew 박제 — 잘못된 '단일 전역' 거친 격자(1원)를 AAPL 에 쓰면 $100.10 가 비정렬이다.
            //   S27-d 의 청산 경로라면 101 로 '조용히 스냅'됐겠지만, 진입 검문은 스냅 대신 거부한다(왜곡 진입 0).
            var coarse = new QTickRegistry(new[] { ("AAPL", new QTickSpec(QPrice.Of(1m), QQty.Of(1m))) });
            var att    = new OrderAttestationService();
            var gate   = new QAdmissionGate(coarse, att);
            Assert.Equal(AdmitResult.RejectMisaligned, gate.TryAdmit("AAPL", 100.10m, 1m, OrderSide.Buy, out var p, out _, out _));
            Assert.Equal(0L, p.Raw);   // out 은 default — '101 로 스냅된 값'이 진입로를 통과하지 않았음을 박제
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ═══════════════════ §3 미등록/오염/음수 거부 ═══════════════════

        [Fact]
        public void Admit_Unregistered_FailClosed()
        {
            var att  = new OrderAttestationService();
            var gate = new QAdmissionGate(Reg(), att);
            Assert.Equal(AdmitResult.RejectUnregistered, gate.TryAdmit("DOES_NOT_EXIST", 100m, 1m, OrderSide.Buy, out _, out _, out _));
            Assert.Equal(1, gate.RejectedUnregistered);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Admit_EmptySymbol_FailClosed()
        {
            var att  = new OrderAttestationService();
            var gate = new QAdmissionGate(Reg(), att);
            Assert.Equal(AdmitResult.RejectPoisonInput, gate.TryAdmit("", 100m, 1m, OrderSide.Buy, out _, out _, out _));
            Assert.Equal(1, gate.RejectedPoison);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Admit_NegativePrice_FailClosed_Acl()
        {
            var att  = new OrderAttestationService();
            var gate = new QAdmissionGate(Reg(), att);
            // AAPL 등록·격자 통과하더라도 ACL 이 음수 단가를 경계에서 동결.
            Assert.Equal(AdmitResult.RejectNegativeOrOverflow, gate.TryAdmit("AAPL", -0.01m, 1m, OrderSide.Buy, out _, out _, out _));
            Assert.Equal(1, gate.RejectedAcl);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ═══════════════════ §4 링 진입 결속 ═══════════════════

        [Fact]
        public void AdmitAndPublish_OnlyAlignedEnterRing()
        {
            var ring = new OrderDispatchRing(capacity: 8);
            var gate = new QAdmissionGate(Reg());
            Assert.Equal(-1L, ring.Published);   // 미발행

            long id1 = gate.AdmitAndPublish(ring, "AAPL", genomeId: 7, symbolCode: 1001,
                                            213.92m, 3m, OrderSide.Buy, kind: 1, timestampTicks: 100);
            Assert.True(id1 >= 1);               // 통과 → 발행 OrderId
            Assert.Equal(0L, ring.Published);    // 링 시퀀스 0 (첫 발행)

            long idReject = gate.AdmitAndPublish(ring, "AAPL", genomeId: 7, symbolCode: 1001,
                                                 100.005m, 1m, OrderSide.Buy, kind: 1, timestampTicks: 101);
            Assert.Equal(-1L, idReject);         // 비정렬 → 미발행
            Assert.Equal(0L, ring.Published);    // ★링 시퀀스 불변 — 오염 제안이 펌프에 진입하지 않음

            long id2 = gate.AdmitAndPublish(ring, "005930", genomeId: 7, symbolCode: 5930,
                                            70000m, 10m, OrderSide.Sell, kind: 1, timestampTicks: 102);
            Assert.True(id2 > id1);
            Assert.Equal(1L, ring.Published);    // 다음 정렬 제안만 진입
            Assert.Equal(2, gate.Admitted);
            Assert.Equal(1, gate.RejectedMisaligned);
        }

        [Fact]
        public void AdmitAndPublish_NullRing_Throws()
            => Assert.Throws<ArgumentNullException>(() =>
                new QAdmissionGate(Reg()).AdmitAndPublish(null!, "AAPL", 1, 1, 1m, 1m, OrderSide.Buy, 0, 0));

        // ═══════════════════ §5 무할당 ═══════════════════

        [Fact]
        public void Admit_ZeroAllocation_HotPath()
        {
            var gate = new QAdmissionGate(Reg());
            for (int i = 0; i < 10_000; i++) gate.TryAdmit("AAPL", 213.92m, 3m, OrderSide.Buy, out _, out _, out _);
            int ok = 0;
            long delta = ZeroAllocProbe.MinAllocated(() =>
            {
                ok = 0;
                for (int i = 0; i < 50_000; i++)
                    if (gate.TryAdmit("AAPL", 213.92m, 3m, OrderSide.Buy, out _, out _, out _) == AdmitResult.Admitted) ok++;
            });
            Assert.Equal(50_000, ok);
            // [v12.18.48-DEV] 예전 완화 임계(< 50,000B · 일회성 JIT 잡음 흡수)를 0 으로 복원 — 다중 창 최솟값이 잡음을 걸러낸다.
            Assert.Equal(0, delta);
        }

        // ═══════════════════ §6 카운터 ═══════════════════

        [Fact]
        public void Admit_Counters_MixedBatch_Accurate()
        {
            var gate = new QAdmissionGate(Reg());
            gate.TryAdmit("AAPL",    213.92m,  3m, OrderSide.Buy,  out _, out _, out _);   // admit
            gate.TryAdmit("005930",  70000m,  10m, OrderSide.Sell, out _, out _, out _);   // admit
            gate.TryAdmit("AAPL",    100.005m, 1m, OrderSide.Buy,  out _, out _, out _);   // misaligned
            gate.TryAdmit("UNKNOWN", 100m,     1m, OrderSide.Buy,  out _, out _, out _);   // unregistered
            gate.TryAdmit("",        100m,     1m, OrderSide.Buy,  out _, out _, out _);   // poison

            Assert.Equal(2, gate.Admitted);
            Assert.Equal(1, gate.RejectedMisaligned);
            Assert.Equal(1, gate.RejectedUnregistered);
            Assert.Equal(1, gate.RejectedPoison);
            Assert.Equal(0, gate.RejectedAcl);
            Assert.Equal(3, gate.RejectedTotal);
        }
    }
}
