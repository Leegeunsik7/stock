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
using System.Runtime.CompilerServices;
using System.Threading;

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
        /// <summary>환율 경계 import(3단계 FX 피드). 환율은 0 도 무효(≤0) — 비양수·오버플로 시 fail-closed 동결.</summary>
        public static bool TryImportRate(decimal v, OrderAttestationService? att, out QFxRate rate)
        {
            rate = default;
            if (v <= 0m) { Freeze(att, "ACL: 비양수 환율 import — fail-closed"); return false; }
            try { rate = QFxRate.Of(v); return true; }
            catch (OverflowException) { Freeze(att, "ACL: 환율 import 오버플로 — fail-closed"); return false; }
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

        /// <summary>
        /// S27-e — 멀티 통화 섀도우. 자산 통화 청산(보수 정렬) → QFxGuard 보수 환산(매수 Ask 올림·매도 Bid 내림) →
        /// Mission 2 비항등 수렴 검문. 어느 단계든 오염/역전/한계 초과면 fail-closed(out=default). 정상 무할당(δ=0).
        /// shadowBase 는 base 통화 명목(텔레메트리·대조용). 라이브 반환값은 게이트가 항상 레거시로 처리(비권위).
        /// </summary>
        public static bool TraceFxOrFreeze(decimal priceDec, decimal qtyDec, OrderSide side, QTickSpec spec,
                                           QFxQuote fxQuote, QFxBounds fxBounds, OrderAttestationService? att,
                                           out QMoney shadowBase)
        {
            shadowBase = default;
            try
            {
                if (!QAcl.TryImportPrice(priceDec, att, out var price)) return false;
                if (!QAcl.TryImportQty(qtyDec, att, out var qty)) return false;
                if (!QLedgerGuard.TryClearNotional(price, qty, spec.Tick, spec.Step, side, att, out var shadow, out var ap, out var aq))
                    return false;
                if (!QScaleGuard.ReconcileOrFreeze(ap, aq, shadow, spec, att)) return false;

                // (1) 자산 통화 레벨 보수 불변식(정렬 exact 대비) — FX 전에 자산 청산 자체를 검증.
                decimal exactAligned = ap.ToDecimal() * aq.ToDecimal();
                if (!CompareConservativeOrFreeze(shadow.ToDecimal(), exactAligned, side, att)) return false;

                // (2) 보수 FX 환산(매수 Ask 올림·매도 Bid 내림) + 바운드/스큐/오버플로 동결.
                if (!QFxGuard.TryConvertOrFreeze(shadow, fxQuote, side, fxBounds, att, out shadowBase))
                { shadowBase = default; return false; }

                // (3) Mission 2 — FX 수렴 비항등 검문: 정렬명목×환율(독립 exact) 대비 방향·한계 역치.
                decimal rateDec = (side == OrderSide.Buy ? fxQuote.Ask : fxQuote.Bid).ToDecimal();
                if (!QFxGuard.VerifyConservativeFxOrFreeze(shadowBase, exactAligned, rateDec, side, att))
                { shadowBase = default; return false; }
                return true;
            }
            catch (OverflowException) { Freeze(att, "ShadowFx: 명목/환산 오버플로 — fail-closed"); shadowBase = default; return false; }
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

        /// <summary>회귀 밴드 — 신 보수 명목이 구 naive 곱 대비 정렬+반올림 델타(band)를 넘어 발산하면 동결.
        /// ★Math.Abs(부호 무시) — 매수는 가격↑·수량↓ 상반 정렬이라 신&lt;구(플랫폼 불리) 미세 역전이 밴드 내면 흡수되고
        /// (매도는 양쪽 내림이라 구조적 신≤구). 방향 보증은 naive 가 아닌 '정렬 exact 대비'(CompareConservativeOrFreeze)가
        /// 담당하므로 안전하며, 섀도우 비권위(Clear 는 항상 legacy 반환)라 무영향. 섀도우가 권위가 되면 naive 대비 부호
        /// 있는 검문이 필요하다(독립 구 엔진 출력 대조).</summary>
        public static bool CompareLegacyBandOrFreeze(decimal shadowDec, decimal naiveLegacy, decimal band,
                                                     OrderAttestationService? att)
        {
            if (Math.Abs(shadowDec - naiveLegacy) > band)
            { Freeze(att, "Shadow: 신·구 명목 발산 — 정렬델타 밴드 초과, 마이그레이션 분기 동결"); return false; }
            return true;
        }

        private static void Freeze(OrderAttestationService? att, string msg) { try { att?.ForceFreeze(msg); } catch { } }
    }

    /// <summary>청산 연산 주권 — 어느 엔진의 값을 Clear() 반환(권위)으로 쓸지. 기본 LegacyPrimary(후방호환·기존 의미론 불변).</summary>
    public enum ClearingAuthority
    {
        /// <summary>레거시 naive decimal 이 권위(S27 섀도우 모드). QFixed 는 병렬 관측만.</summary>
        LegacyPrimary = 0,
        /// <summary>QFixed 정렬 청산이 권위(S29-c 역전). 레거시는 후방 백스톱(밴드 위반/오버플로 시 EmergencyFreeze).</summary>
        QFixedPrimary = 1,
    }

    /// <summary>
    /// S27-c — 단계적 섀도우 컷오버 게이트(스캐폴드). enabled 일 때 신 QFixed 엔진을 신·구 병렬 대조하고 역전/발산
    /// 시 ForceFreeze. 기본 OFF = 완전 INERT(섀도우 미수행·att 무접촉·무할당·테스트 무영향).
    ///
    /// S29-c 권위 역전(Authority Inversion): ClearingAuthority 로 Clear() 반환 권위를 가역 전환한다. LegacyPrimary
    /// (기본)면 라이브 반환은 레거시(naive decimal·섀도우 모드 불변). QFixedPrimary 면 QFixed 정렬값(shadow)이 권위가
    /// 되고 레거시 decimal 은 '후방 백스톱'으로 상시 대조된다(밴드 초과/오버플로 → TraceOrFreeze 가 EmergencyFreeze →
    /// 권위 오염 대신 파이프라인 정지·백스톱 legacy 반환). 스위치는 volatile(런타임 가역·무할당). liveArmed:false 불변:
    /// 본 게이트는 '청산 가치 권위'만 정하며 실 송출 무장과 무관하다.
    ///
    /// ★적대적 리뷰 정직 한정:
    ///  · 락-프리/δ=0 은 '패리티(무분기) 경로' 한정. 동결 경로(분기/poison)는 att.ForceFreeze 가 OrderAttestationService
    ///    _lock 으로 동시 호출자를 직렬화하고 감사 레코드를 ~56B 할당한다(동결은 핫패스가 아니므로 의도된 비용).
    ///  · '병렬 납땜'은 미실현 — 라이브 청산 경로도 관측 표면(관제탑)도 아직 Clear()/카운터를 소비하지 않는다(등록만).
    ///    실 컷오버 시 Clear() 결속 + 자산별 spec + 관제탑 독출 배선이 필요하다.
    /// </summary>
    public sealed class ShadowCutoverGate
    {
        private readonly bool _enabled;
        private readonly QTickSpec _spec;
        private readonly QTickRegistry? _registry;   // S27-e — 자산별 격자(설정 시 단일 _spec 대신 심볼 조회)
        private readonly OrderAttestationService? _att;
        private long _observed, _frozen, _lastDivBits;
        // S29-c — 청산 연산 주권. volatile = 런타임 가역 스위치(boot 컨텍스트/설정 토글, 무할당). 기본 LegacyPrimary(불변).
        private volatile ClearingAuthority _authority;

        /// <summary>단일 전역 spec 게이트(S27-c 호환·DI 기본). Clear(symbol)은 심볼 무시하고 이 spec 사용.</summary>
        public ShadowCutoverGate(bool enabled, QTickSpec spec, OrderAttestationService? attestation = null,
                                 ClearingAuthority authority = ClearingAuthority.LegacyPrimary)
        { _enabled = enabled; _spec = spec; _registry = null; _att = attestation; _authority = authority; }

        /// <summary>S27-e — 자산별 레지스트리 게이트. Clear(symbol)이 심볼로 격자를 무할당 조회(미등록→fail-closed).</summary>
        public ShadowCutoverGate(bool enabled, QTickRegistry registry, OrderAttestationService? attestation = null,
                                 ClearingAuthority authority = ClearingAuthority.LegacyPrimary)
        { _enabled = enabled; _registry = registry ?? throw new ArgumentNullException(nameof(registry)); _spec = default; _att = attestation; _authority = authority; }

        public bool   Enabled        => _enabled;
        public long   Observed       => Interlocked.Read(ref _observed);
        public long   FrozenCount    => Interlocked.Read(ref _frozen);
        /// <summary>최근 신−구 명목 차(관측 텔레메트리, double). 락-프리 Interlocked 단일값 발행.</summary>
        public double LastDivergence => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastDivBits));
        /// <summary>현재 청산 권위(LegacyPrimary=섀도우 모드 / QFixedPrimary=역전). volatile 읽기.</summary>
        public ClearingAuthority Authority => _authority;

        /// <summary>런타임 가역 권위 스위치 — volatile 쓰기(무할당·무박싱). QFixedPrimary 로 올리면 Clear 가 QFixed 정렬값을
        /// 권위로 반환(레거시는 백스톱), LegacyPrimary 로 되돌리면 섀도우 모드 복귀. 단일 enum(int) 원자 스왑.</summary>
        public void SetAuthority(ClearingAuthority authority) => _authority = authority;

        // S29-c — 권위 분기(★인라인·무VTable·무박싱: IClearingEngine 같은 인터페이스 디스패치 대신 값타입 enum + if).
        //   QFixed 권위 + 엔진 성공 → shadow(정렬 권위), 실패(동결 트립) → legacy 백스톱(파이프라인은 EmergencyFreeze 로
        //   정지하므로 권위 오염 대신 정직한 degraded). 레거시 권위(섀도우 모드) → 항상 legacy(불변).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static decimal Authoritative(ClearingAuthority auth, bool engineOk, decimal legacy, QMoney shadow)
            => auth == ClearingAuthority.QFixedPrimary ? (engineOk ? shadow.ToDecimal() : legacy) : legacy;

        /// <summary>
        /// 청산 신호 처리. 항상 레거시(naive decimal 곱) 반환. enabled 면 신 엔진 병렬 대조 + 분기 시 동결.
        /// OFF 면 즉시 레거시 반환(INERT) — 섀도우·att·할당 0. 동시 호출 안전(Interlocked만 공유).
        /// </summary>
        public decimal Clear(decimal priceDec, decimal qtyDec, OrderSide side)
        {
            decimal legacy = priceDec * qtyDec;   // 백스톱(섀도우 모드에선 권위)
            var auth = _authority;                 // ★단일 volatile read — 메서드 전체 일관(토글 중 분기/반환 일치 보장)
            if (!_enabled && auth == ClearingAuthority.LegacyPrimary) return legacy;   // ★INERT(불변) — 무영향·무할당·무동결
            Interlocked.Increment(ref _observed);
            // 신 QFixed 엔진(보수 정렬+오버플로/스케일 트랩) 대조 — 레거시 백스톱과 밴드 초과 시 이미 ForceFreeze.
            bool ok = ShadowClearingTracer.TraceOrFreeze(priceDec, qtyDec, side, _spec, _att, out var shadow);
            if (ok) Interlocked.Exchange(ref _lastDivBits, BitConverter.DoubleToInt64Bits((double)(shadow.ToDecimal() - legacy)));
            else    Interlocked.Increment(ref _frozen);
            return Authoritative(auth, ok, legacy, shadow);   // S29-c — 권위에 따라 shadow(QFixed) 또는 legacy
        }

        // S27-e — 자산별 격자를 심볼로 조회(무할당). 레지스트리 미설정이면 단일 _spec 폴백. 미등록/충돌이면
        //   fail-closed(false + 동결) — 미검증 자산을 섀도우 통과시키지 않는다.
        private bool ResolveSpec(string symbol, out QTickSpec spec)
        {
            if (_registry is null) { spec = _spec; return true; }
            if (_registry.TryGet(symbol, out spec)) return true;
            try { _att?.ForceFreeze("멀티자산 청산: 미등록 심볼 — 섀도우 검증 불가, fail-closed"); } catch { }
            return false;
        }

        /// <summary>
        /// S27-e — 자산별 청산(같은 통화·base 자산). 심볼로 격자 조회 후 신 엔진 병렬 대조. 미등록 심볼은 fail-closed
        /// 동결(FrozenCount/EmergencyFreeze 트립). 항상 레거시(naive 곱) 반환(liveArmed:false). 등록 핫패스 무할당(δ=0).
        /// </summary>
        public decimal Clear(string symbol, decimal priceDec, decimal qtyDec, OrderSide side)
        {
            decimal legacy = priceDec * qtyDec;
            var auth = _authority;
            if (!_enabled && auth == ClearingAuthority.LegacyPrimary) return legacy;   // INERT(불변)
            Interlocked.Increment(ref _observed);
            // 미등록 심볼은 fail-closed — QFixed 권위라도 검증 불가 자산을 권위로 통과시키지 않는다(백스톱 legacy + 동결).
            if (!ResolveSpec(symbol, out var spec)) { Interlocked.Increment(ref _frozen); return legacy; }
            bool ok = ShadowClearingTracer.TraceOrFreeze(priceDec, qtyDec, side, spec, _att, out var shadow);
            if (ok) Interlocked.Exchange(ref _lastDivBits, BitConverter.DoubleToInt64Bits((double)(shadow.ToDecimal() - legacy)));
            else    Interlocked.Increment(ref _frozen);
            return Authoritative(auth, ok, legacy, shadow);
        }

        /// <summary>
        /// S27-e — 이종 통화 자산 청산. 심볼로 격자 조회 → 자산 통화 청산 → 보수 FX 환산(매수 Ask·매도 Bid) →
        /// Mission 2 수렴 검문. 텔레메트리(LastDivergence)는 base 통화. 미등록/FX 오염/수렴 위반 시 fail-closed 동결.
        /// 라이브 반환은 자산 통화 레거시(비권위·liveArmed:false). 등록·정렬 핫패스 무할당(δ=0).
        /// </summary>
        public decimal Clear(string symbol, decimal priceDec, decimal qtyDec, OrderSide side,
                             QFxQuote fxQuote, QFxBounds fxBounds)
        {
            decimal legacy = priceDec * qtyDec;
            if (!_enabled) return legacy;                              // INERT
            Interlocked.Increment(ref _observed);
            if (!ResolveSpec(symbol, out var spec)) { Interlocked.Increment(ref _frozen); return legacy; }
            if (ShadowClearingTracer.TraceFxOrFreeze(priceDec, qtyDec, side, spec, fxQuote, fxBounds, _att, out var shadowBase))
            {
                decimal rateDec = (side == OrderSide.Buy ? fxQuote.Ask : fxQuote.Bid).ToDecimal();
                Interlocked.Exchange(ref _lastDivBits, BitConverter.DoubleToInt64Bits((double)(shadowBase.ToDecimal() - legacy * rateDec)));
            }
            else
                Interlocked.Increment(ref _frozen);
            // S29-c — FX 의 '자산통화' Clear 는 권위 역전 미적용(반환 단위 불변·후방호환). base 통화 권위가 필요하면
            //   ClearBase 를 쓸 것(권위값 단위가 base 라 별도 계약 — 아래).
            return legacy;
        }

        /// <summary>
        /// S29-c FX 권위 역전(base 통화 계약). 이종통화 청산의 'base 통화' 권위값을 반환한다. QFixedPrimary 면
        /// shadowBase(보수 FX 환산·수렴검문 통과값)가 권위, 아니면 legacyBase(naive 자산명목 × 환율)가 권위. 레거시
        /// base 는 후방 백스톱 — 밴드/오버플로/수렴위반 시 TraceFxOrFreeze 가 EmergencyFreeze → 권위 오염 대신 정지
        /// (degraded legacyBase 반환). ★반환 단위는 base 통화(자산통화 Clear 와 다름 — 명시 계약). 미등록/오염 fail-closed.
        /// liveArmed:false 불변. 등록·정렬 핫패스 무할당(δ=0·동결 경로만 att 할당).
        /// </summary>
        public decimal ClearBase(string symbol, decimal priceDec, decimal qtyDec, OrderSide side,
                                 QFxQuote fxQuote, QFxBounds fxBounds)
        {
            decimal rateDec    = (side == OrderSide.Buy ? fxQuote.Ask : fxQuote.Bid).ToDecimal();
            decimal legacyBase = priceDec * qtyDec * rateDec;   // base 통화 백스톱(naive 자산명목 × 환율)
            var auth = _authority;
            if (!_enabled && auth == ClearingAuthority.LegacyPrimary) return legacyBase;   // INERT(불변)
            Interlocked.Increment(ref _observed);
            if (!ResolveSpec(symbol, out var spec)) { Interlocked.Increment(ref _frozen); return legacyBase; }
            bool ok = ShadowClearingTracer.TraceFxOrFreeze(priceDec, qtyDec, side, spec, fxQuote, fxBounds, _att, out var shadowBase);
            if (ok) Interlocked.Exchange(ref _lastDivBits, BitConverter.DoubleToInt64Bits((double)(shadowBase.ToDecimal() - legacyBase)));
            else    Interlocked.Increment(ref _frozen);
            return Authoritative(auth, ok, legacyBase, shadowBase);
        }

        /// <summary>
        /// v4.61.0 Load-bearing 컷오버 지원 — QFixed 보수 정렬된 (단가, 수량)을 '분리' 노출한다. 기존 Clear/ClearBase 는
        /// 명목 decimal 만 반환해 주문 필드 치환에 쓸 수 없었다(정직: ClearBase 는 base 통화 명목 — price/qty 분해 불가).
        /// 등록 격자(registry/spec)로 QLedgerGuard.TryClearNotional 을 호출해 alignedPrice(매수 ceil·매도 floor)와
        /// alignedQty(step 내림)를 산출한다. 미등록·음수·오버플로면 false(+_att ForceFreeze·_frozen++) — 호출자
        /// (ShadowClearingBroker)가 이를 받아 fail-closed(실주문 차단)를 '강제'한다. 성공 경로 무할당(값 타입·정렬 연산만).
        /// ★권위(Authority)와 무관한 순수 연산 프리미티브 — 권위 적용 여부(반환값 채택)는 전적으로 호출자 책임.
        /// </summary>
        public bool TryClearAligned(string symbol, decimal priceDec, decimal qtyDec, OrderSide side,
                                    out QPrice alignedPrice, out QQty alignedQty)
        {
            alignedPrice = default; alignedQty = default;
            Interlocked.Increment(ref _observed);
            if (!ResolveSpec(symbol, out var spec))   // 미등록 → ResolveSpec 이 _att 동결
            { Interlocked.Increment(ref _frozen); return false; }
            if (!QAcl.TryImportPrice(priceDec, _att, out var price) || !QAcl.TryImportQty(qtyDec, _att, out var qty))
            { Interlocked.Increment(ref _frozen); return false; }   // 음수/오버플로 import → 경계 동결
            if (!QLedgerGuard.TryClearNotional(price, qty, spec.Tick, spec.Step, side, _att,
                                               out _, out alignedPrice, out alignedQty))
            { Interlocked.Increment(ref _frozen); return false; }   // 명목 오버플로 등 → 동결
            return true;
        }
    }
}
