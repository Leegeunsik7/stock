// ============================================================================
// QAdmission — S29 Path Beta: 멀티-자산 펌프 진입 검문(흐름 제어 Seam)
//
//  S27-d 가 만든 QTickRegistry·QFxGuard 스캐폴드를 '실제 주문 진입로'에 인라인 결속한다. 단일 백그라운드
//  펌프(OrderDispatchRing 생산자)로 USD 소수 자산과 KRW 정수 자산이 교차해 들어올 때, 자산별 격자(TickSize/
//  StepSize)에 '정확히 정렬된' 제안만 통과시키고 비정렬 서브틱은 fail-closed 거부한다.
//
//  ── ShadowClearingTracer 와의 역할 분리(중복 아님) ──────────────────────────
//  · ShadowClearingTracer = '관측' — 비정렬 입력을 ConservativeAlign 로 스냅(거래소 유리)해 명목을 산출하고
//    레거시와 대조한다(컷오버 전 패리티 입증). 즉 스냅이 정상 동작이다.
//  · QAdmissionGate = '입장 통제' — 진입로에서 비정렬 서브틱을 '스냅하지 않고 거부'한다. 진입 단계의 조용한
//    스냅이야말로 Silent Skew 의 원천이기 때문이다(잘못된 단일 전역 spec 으로 $100.10→101 왜곡, S27-d 박제).
//    올바른 자산별 격자로 조회해 '이미 격자 위'인 주문만 받고, 진짜 서브틱(버그·wrong-scale)은 fail-closed.
//
//  ── liveArmed:false 불변 ────────────────────────────────────────────────────
//  본 게이트는 '진입 형식'을 검문할 뿐 무장과 무관하다. 통과한 제안도 송출은 GatedOrderDispatcher 의 무장 술어가
//  결정한다(기본 자세 false → 송출 0). 검문은 링 진입 '앞단' — 오염 제안이 펌프/감사/텔레메트리로 새지 않게 한다.
//
//  ── 무할당(δ=0) ─────────────────────────────────────────────────────────────
//  통과 경로는 FrozenDictionary 룩업 + 값타입 import/정렬검사뿐 — 힙 0. 거부(동결) 경로만 att 감사 레코드를
//  할당한다(거부는 핫패스가 아니므로 의도된 비용). 카운터는 전부 Interlocked(락-프리 텔레메트리).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>펌프 진입 검문 결과. Admitted 외 전부 fail-closed 거부(사유별).</summary>
    public enum AdmitResult : byte
    {
        Admitted                 = 0,
        RejectPoisonInput        = 1,   // 빈 심볼 등 오염 입력
        RejectUnregistered       = 2,   // 미등록 심볼 — 격자 미상(추측 금지)
        RejectNegativeOrOverflow = 3,   // 음수/오버플로 — ACL 경계 차단
        RejectMisaligned         = 4,   // 비정렬 서브틱 — 격자 이탈(silent skew 차단)
    }

    /// <summary>
    /// S29 Path Beta — 멀티-자산 펌프 진입 검문 게이트. 자산별 격자에 정확 정렬된 주문만 통과, 비정렬·미등록·
    /// 오염은 fail-closed(거부 + ForceFreeze). 통과 경로 무할당(δ=0). 동시 호출 안전(Interlocked 카운터만 공유).
    /// </summary>
    public sealed class QAdmissionGate
    {
        private readonly QTickRegistry _registry;
        private readonly OrderAttestationService? _att;
        private long _admitted, _rejPoison, _rejUnregistered, _rejAcl, _rejMisaligned;

        public QAdmissionGate(QTickRegistry registry, OrderAttestationService? attestation = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _att = attestation;
        }

        public long Admitted             => Interlocked.Read(ref _admitted);
        public long RejectedPoison       => Interlocked.Read(ref _rejPoison);
        public long RejectedUnregistered => Interlocked.Read(ref _rejUnregistered);
        public long RejectedAcl          => Interlocked.Read(ref _rejAcl);
        public long RejectedMisaligned   => Interlocked.Read(ref _rejMisaligned);
        /// <summary>거부 총합(사유 무관) — 관제탑 단일 지표.</summary>
        public long RejectedTotal => RejectedPoison + RejectedUnregistered + RejectedAcl + RejectedMisaligned;

        /// <summary>
        /// 단일 펌프 진입 검문. 자산별 격자(tick/step)에 '정확히 정렬'된 주문만 Admitted. 비정렬 서브틱은 조용한
        /// 스냅 대신 거부(silent skew 차단). 통과 시 검증된 QPrice/QQty/QTickSpec out. 통과 경로 무할당.
        /// </summary>
        public AdmitResult TryAdmit(string symbol, decimal priceDec, decimal qtyDec, OrderSide side,
                                    out QPrice price, out QQty qty, out QTickSpec spec)
        {
            // ★fail-closed 계약 — 거부 시 out 은 전부 default. 검증 통과 '직전'에만 로컬→out 배정해,
            //   반쪽 검증된(미정렬·미import) 값이 out 으로 새지 않게 한다(호출자가 result 를 무시해도 안전).
            price = default; qty = default; spec = default;

            if (string.IsNullOrEmpty(symbol))
            {
                Interlocked.Increment(ref _rejPoison);
                Freeze("admission: 빈 심볼 — fail-closed");
                return AdmitResult.RejectPoisonInput;
            }
            // 1) 자산별 격자 — 미등록은 추측 spec 금지(fail-closed). TryGet(string)은 FNV 키 + 심볼 검증(충돌 안전).
            if (!_registry.TryGet(symbol, out var resolved))
            {
                Interlocked.Increment(ref _rejUnregistered);
                Freeze("admission: 미등록 심볼 — 격자 미상, fail-closed");
                return AdmitResult.RejectUnregistered;
            }
            // 2) ACL 경계 — 음수/오버플로는 코어 진입 전에 차단(QAcl 이 이미 ForceFreeze). 로컬에 import.
            if (!QAcl.TryImportPrice(priceDec, _att, out var p) || !QAcl.TryImportQty(qtyDec, _att, out var q))
            {
                Interlocked.Increment(ref _rejAcl);
                return AdmitResult.RejectNegativeOrOverflow;
            }
            // 3) ★흐름 제어 Seam — 자산별 격자 정확 정렬 검문(side 무관: 진입은 grid 위여야 함). 비정렬 = 거부.
            //    side 별 보수 스냅은 '청산'의 몫(ShadowClearingTracer) — '진입'에서 스냅하면 그게 곧 silent skew.
            if (!p.IsAligned(resolved.Tick) || !q.IsAligned(resolved.Step))
            {
                Interlocked.Increment(ref _rejMisaligned);
                Freeze("admission: 비정렬 서브틱(tick/step 격자 이탈) — silent skew 차단, fail-closed");
                return AdmitResult.RejectMisaligned;
            }

            // 통과 — 검증된 값만 out 으로 공개.
            price = p; qty = q; spec = resolved;
            Interlocked.Increment(ref _admitted);
            return AdmitResult.Admitted;
        }

        /// <summary>
        /// 펌프 진입 결속 — 검문 통과 시에만 OrderDispatchRing 에 발행(발행 OrderId 반환), 거부 시 -1(미발행).
        /// 오염/비정렬/미등록 제안이 링(브릿지·감사·텔레메트리)으로 새는 것을 진입로에서 차단하는 단일 Seam.
        /// </summary>
        public long AdmitAndPublish(OrderDispatchRing ring, string symbol, long genomeId, long symbolCode,
                                    decimal priceDec, decimal qtyDec, OrderSide side, byte kind, long timestampTicks)
        {
            if (ring is null) throw new ArgumentNullException(nameof(ring));
            if (TryAdmit(symbol, priceDec, qtyDec, side, out var price, out var qty, out _) != AdmitResult.Admitted)
                return -1L;
            return ring.Publish(genomeId, symbolCode, side, kind, price, qty, timestampTicks);
        }

        private void Freeze(string msg) { try { _att?.ForceFreeze(msg); } catch { /* 동결 실패해도 거부 반환은 정직 유지 */ } }
    }
}
