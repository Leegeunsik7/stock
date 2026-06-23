// ============================================================================
// QClearing — S27-b: 청산 마이그레이션 골격 (ACL · 멀티-스케일 정합 · Shadow Tracing)
//
//  레거시 System.Decimal 청산을 QFixed 척추로 교체하기 위한 '안전 교량'. 빅뱅 컷오버 금물 — 섀도우가
//  신·구 결과 분기를 잡아내며 패리티를 입증한 '뒤에야' 실 경로 전환. 라이브 포트폴리오/청산 경로는
//  아직 미배선(decimal 공존) — 정직 표기.
//
//  ── Mission 1 ACL(부패 방지 계층, fail-closed) ─────────────────────────────
//  decimal 은 '외부 경계'에서만. import 는 Try 패턴 — 음수·오버플로를 경계에서 ForceFreeze 로 변환(raw 예외
//  확산·오염 통과 금지). 코어는 검증된 QFixed 만 받는다. 변환은 값 타입(박싱·힙 0).
//
//  ── Mission 2 멀티-스케일 정합 ──────────────────────────────────────────────
//  정준 스케일 10^8. 정렬 후 격자 정수배 위반 또는 round-trip(명목÷단가) 디싱크가 1 step '이상'이면 스케일
//  충돌로 fail-closed(경계 포함 — drift>=step).
//
//  ── Mission 3 Shadow Tracing (★적대적 리뷰로 동어반복 제거) ─────────────────
//  '구 척추'는 코드베이스에 별도 청산 엔진이 없으므로 = 정렬 없는 naive decimal 곱(현 포트폴리오의 가치
//  계산 방식). 신 보수 결과를 이 naive 곱과 대조한다:
//   (1) 보수 반올림 불변식 — 정렬값의 정확곱(exact) 대비 매수 ceil(≥)·매도 floor(≤)·|Δ|≤1raw (QLedgerGuard 회귀 가드).
//   (2) 회귀 밴드 — naive 곱 대비 정렬+반올림 델타(틱×수량 + 스텝×단가 + 1raw) 초과 발산이면 마이그레이션 분기로 동결.
//  ⚠️ 정직: 독립 구 청산 엔진은 부재 — (1)은 QLedgerGuard 자기 일관성, (2)는 naive decimal 대비 회귀 밴드.
//  진짜 '다른 정렬규칙의 구 엔진'을 갖게 되면 그 출력을 naive 자리에 주입하면 된다.
// ============================================================================
using System;

namespace StockManager.Common
{
    /// <summary>자산별 격자 사양 — 호가틱(QPrice)·수량스텝(QQty). 멀티-스케일 정합 기준.</summary>
    public readonly record struct QTickSpec(QPrice Tick, QQty Step)
    {
        public bool IsValid => Tick.Raw > 0 && Step.Raw > 0;
    }

    /// <summary>Mission 1 — 부패 방지 계층(fail-closed). import 는 Try(음수·오버플로 → 경계에서 동결).</summary>
    public static class QAcl
    {
        public static bool TryImportPrice(decimal v, OrderAttestationService? att, out QPrice price)
        {
            price = default;
            if (v < 0m) { Freeze(att, "ACL: 음수 단가 import — fail-closed"); return false; }
            try { price = QPrice.Of(v); return true; }
            catch (OverflowException) { Freeze(att, "ACL: 단가 import 오버플로 — fail-closed"); return false; }
        }
        public static bool TryImportQty(decimal v, OrderAttestationService? att, out QQty qty)
        {
            qty = default;
            if (v < 0m) { Freeze(att, "ACL: 음수 수량 import — fail-closed"); return false; }
            try { qty = QQty.Of(v); return true; }
            catch (OverflowException) { Freeze(att, "ACL: 수량 import 오버플로 — fail-closed"); return false; }
        }
        public static decimal ExportPrice(QPrice p) => p.ToDecimal();   // 코어 → 외부(무예외)
        public static decimal ExportQty(QQty q)     => q.ToDecimal();
        public static decimal ExportMoney(QMoney m) => m.ToDecimal();

        private static void Freeze(OrderAttestationService? att, string msg) { try { att?.ForceFreeze(msg); } catch { } }
    }

    /// <summary>Mission 2 — 멀티-스케일 정합. 격자 배수·round-trip 디싱크(≥1 step) 위반 시 ForceFreeze.</summary>
    public static class QScaleGuard
    {
        public static bool ReconcileOrFreeze(QPrice alignedPrice, QQty alignedQty, QMoney notional,
                                             QTickSpec spec, OrderAttestationService? attestation)
        {
            try
            {
                if (!spec.IsValid || alignedPrice.Raw <= 0 || alignedQty.Raw < 0)
                { Freeze(attestation, "Q정합: 격자/입력 오염 — fail-closed"); return false; }
                if (!alignedPrice.IsAligned(spec.Tick) || !alignedQty.IsAligned(spec.Step))
                { Freeze(attestation, "Q정합: 격자 비정렬 — 스케일 충돌 의심, fail-closed"); return false; }
                QQty back  = notional / alignedPrice;   // CrossDiv(음수/0 트랩)
                long drift = back.Raw - alignedQty.Raw;
                // ★적대적 리뷰(off-by-one) — 정당 floor 잔여 drift 는 ≈1 raw 이므로, 정확히 1 step 이상 어긋남은
                //   합법 라운딩이 아닌 실제 디싱크. 경계 포함 동결(>= step).
                if (drift <= -spec.Step.Raw || drift >= spec.Step.Raw)
                { Freeze(attestation, $"Q정합: round-trip 디싱크 {drift} raw(≥1 step) — 스케일/정밀 오염, fail-closed"); return false; }
                return true;
            }
            catch (Exception ex) when (ex is OverflowException or DivideByZeroException or ArgumentOutOfRangeException)
            { Freeze(attestation, "Q정합: 연산 트랩 — fail-closed"); return false; }
        }

        private static void Freeze(OrderAttestationService? att, string msg) { try { att?.ForceFreeze(msg); } catch { } }
    }

    /// <summary>Mission 3 — Shadow Tracing. 보수 불변식(정렬 exact 대비) + 회귀 밴드(naive decimal 대비). 위반 시 동결.</summary>
    public static class ShadowClearingTracer
    {
        private const decimal OneRaw = 0.00000001m;   // 1 raw = 10^-8 (정준 스케일 최소 단위)

        public static bool TraceOrFreeze(decimal priceDec, decimal qtyDec, OrderSide side, QTickSpec spec,
                                         OrderAttestationService? att, out QMoney shadow)
        {
            shadow = default;
            try
            {
                if (!QAcl.TryImportPrice(priceDec, att, out var price)) return false;   // 경계 fail-closed
                if (!QAcl.TryImportQty(qtyDec, att, out var qty)) return false;
                if (!QLedgerGuard.TryClearNotional(price, qty, spec.Tick, spec.Step, side, att, out shadow, out var ap, out var aq))
                    return false;
                if (!QScaleGuard.ReconcileOrFreeze(ap, aq, shadow, spec, att)) return false;

                decimal shadowDec = shadow.ToDecimal();
                // (1) 보수 반올림 불변식 — 정렬값의 정확곱 대비. QLedgerGuard 의 방향 반올림이 깨지면 포착.
                decimal exactAligned = ap.ToDecimal() * aq.ToDecimal();
                if (!CompareConservativeOrFreeze(shadowDec, exactAligned, side, att)) return false;
                // (2) 회귀 밴드 — 정렬 없는 naive decimal 곱(구 포트폴리오 방식) 대비 정렬+반올림 델타 한도.
                decimal naiveLegacy = priceDec * qtyDec;
                decimal alignBand   = spec.Tick.ToDecimal() * qtyDec + spec.Step.ToDecimal() * priceDec + OneRaw;
                if (!CompareLegacyBandOrFreeze(shadowDec, naiveLegacy, alignBand, att)) return false;
                return true;
            }
            catch (OverflowException) { Freeze(att, "Shadow: 명목 오버플로 — fail-closed"); shadow = default; return false; }
        }

        /// <summary>보수 반올림 불변식 — 매수 ceil(shadow≥exact)·매도 floor(shadow≤exact)·|Δ|≤1raw. 위반 시 동결.</summary>
        public static bool CompareConservativeOrFreeze(decimal shadowDec, decimal exactAligned, OrderSide side,
                                                       OrderAttestationService? att)
        {
            if (side == OrderSide.Buy  && shadowDec < exactAligned) { Freeze(att, "Shadow: 매수 보수 반올림 역전(ceil 위반·플랫폼 유리) — 동결"); return false; }
            if (side == OrderSide.Sell && shadowDec > exactAligned) { Freeze(att, "Shadow: 매도 보수 반올림 역전(floor 위반·플랫폼 유리) — 동결"); return false; }
            if (Math.Abs(shadowDec - exactAligned) > OneRaw)        { Freeze(att, "Shadow: 보수 반올림 1raw 초과 — QLedgerGuard 회귀, 동결"); return false; }
            return true;
        }

        /// <summary>회귀 밴드 — 신 보수 명목이 구 naive 곱 대비 정렬+반올림 델타(band)를 넘어 발산하면 동결.</summary>
        public static bool CompareLegacyBandOrFreeze(decimal shadowDec, decimal naiveLegacy, decimal band,
                                                     OrderAttestationService? att)
        {
            if (Math.Abs(shadowDec - naiveLegacy) > band)
            { Freeze(att, "Shadow: 신·구 명목 발산 — 정렬델타 밴드 초과, 마이그레이션 분기 동결"); return false; }
            return true;
        }

        private static void Freeze(OrderAttestationService? att, string msg) { try { att?.ForceFreeze(msg); } catch { } }
    }
}
