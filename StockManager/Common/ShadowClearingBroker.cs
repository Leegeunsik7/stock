// ============================================================================
// ShadowClearingBroker — Path B: QFixed 청산 척추를 실 브로커 파이프라인에 '유선 납땜'하는 데코레이터
//
//  S27-c/d/e 에서 스캐폴드로 완성된 ShadowCutoverGate(결정론 고정소수점 청산 + 보수 정렬 + FX 펜싱)를
//  실제 IBroker.Place 경로 최하단에 결속한다. 단, liveArmed:false 성벽과 동일 사상으로 '컷오버'가 아니라
//  '병렬 섀도우 관측'이다 — 레거시 decimal 이 여전히 권위(반환은 inner 그대로), 신 엔진은 옆에서 대조만 한다.
//
//  ── 안전 불변(절대 실주문에 영향 없음) ──────────────────────────────────────
//   1. 게이트 INERT(enabled=false, 기본)면 Clear() 자체를 호출하지 않는다(무할당·무관측·완전 통과).
//   2. 관측은 결과를 '버린다'(레거시 inner 반환이 권위). 신 엔진 값으로 주문을 바꾸지 않는다.
//   3. 관측 중 어떤 예외도 삼켜(try/catch) inner.Place 를 절대 막지 않는다 — 섀도우 결함이 실거래를 깨지 못함.
//
//  ── 활성화(enabled=true) 전제(정직 표기) ────────────────────────────────────
//   · 자산별 정확한 QTickSpec(레지스트리) 필요. 기본 단일 spec(KRW 정수단가)을 USD 센트·소수단가에 쓰면
//     명목이 조용히 왜곡된다(BlazorHomeForm 주석·회귀테스트가 박제). → 활성 전 Currency→QTickSpec 분기 필수.
//   · 섀도우 발산 시 ForceFreeze 가 '라이브' OrderAttestationService 를 동결시키지 않도록, 게이트는 전용
//     섀도우 attestation 으로 구성할 것(라이브 패닉 상태 오염 방지). 데코레이터는 게이트를 '있는 그대로' 쓴다.
//   현재는 BrokerHub.Create(db) 기본 호출이 gate=null 이라 미배선(후방호환) — 명시적 옵트인에서만 결속.
// ============================================================================
using System;
using System.Diagnostics;

namespace StockManager.Common
{
    /// <summary>IBroker 데코레이터 — 게이트 권위에 따라 분기: LegacyPrimary=병렬 관측(레거시 권위 불변),
    /// QFixedPrimary=완전 집권(보수 정렬값으로 주문 치환 + 청산 실패 시 fail-closed 하드 단절).</summary>
    public sealed class ShadowClearingBroker : IBroker
    {
        private readonly IBroker                   _inner;
        private readonly ShadowCutoverGate         _gate;
        // v4.61.0 — QFixedPrimary 'load-bearing' fail-closed 시 라이브 파이프라인을 동결할 attestation(옵션·기본 null).
        //   null 이면 throw 만으로 하드 단절(섀도우↔라이브 동결 결합 footgun 미발생). 명시 주입 시에만 라이브 동결 에스컬레이션.
        private readonly OrderAttestationService?   _liveAtt;

        public ShadowClearingBroker(IBroker inner, ShadowCutoverGate gate,
                                    OrderAttestationService? liveAttestation = null)
        {
            _inner   = inner ?? throw new ArgumentNullException(nameof(inner));
            _gate    = gate  ?? throw new ArgumentNullException(nameof(gate));
            _liveAtt = liveAttestation;
        }

        public string Name   => $"Shadow({_inner.Name})";
        public bool   IsLive => _inner.IsLive;

        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name,
            PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            // ★v4.61 권위 분기 — QFixedPrimary 는 'load-bearing'(게이트 정렬값이 실주문 권위), 그 외(LegacyPrimary)는 기존 '병렬 관측'.
            if (_gate.Authority == ClearingAuthority.QFixedPrimary)
            {
                // v4.67.0 — IBroker.Place 경계 load-bearing 가드.
                //   PlaceQFixedAuthoritative 가 미등록/격자 오염/수량 0 시 InvalidOperationException을 throw하는데,
                //   이 예외가 WinForms UI 스레드까지 전파되면 OrderConfirmDialog 이후 앱이 크래시.
                //   경계에서 포착해 (false, msg)로 변환 — 호출부는 MessageBox 처리로 안전 강등.
                //   ★FailClosed()는 throw '전에' _liveAtt.ForceFreeze()를 완료하므로 가드 포착 후에도 동결 불변식 유지.
                try
                {
                    return PlaceQFixedAuthoritative(asset, symbol, name, side, kind, qty, limitPrice, currentPrice);
                }
                // when 필터: FailClosed()가 생성하는 "[CLEARING FAIL-CLOSED]" 메시지만 포착.
                //   inner.Place()가 던지는 InvalidOperationException은 when 통과 실패 → 상위 catch로 전파(오귀인 방지).
                catch (InvalidOperationException ex)
                    when (ex.Message.StartsWith("[CLEARING FAIL-CLOSED]", StringComparison.Ordinal))
                {
                    Trace.TraceError("[ShadowClearingBroker] 청산 fail-closed 포착({0}): {1}", symbol, ex.Message);
                    return (false, ex.Message);
                }
            }

            // ── LegacyPrimary(안전 모드·기존 계약 1비트 불변) — 관측만·결과 버림·예외 삼킴·RAW 위임 ──
            if (_gate.Enabled)
            {
                try
                {
                    decimal px = kind == PaperKind.Market ? currentPrice : limitPrice;
                    if (qty > 0m && px >= 0m && !string.IsNullOrEmpty(symbol))
                    {
                        var oside = side == PaperSide.Buy ? OrderSide.Buy : OrderSide.Sell;
                        _gate.Clear(symbol, px, qty, oside);   // 반환 무시 — 레거시 inner 가 권위
                    }
                }
                catch { /* ★섀도우 관측은 실주문을 절대 방해하지 않는다(결함 격리) */ }
            }
            return _inner.Place(asset, symbol, name, side, kind, qty, limitPrice, currentPrice);
        }

        // ★QFixedPrimary 완전 집권 — 게이트 보수 정렬값으로 주문을 '치환'해 송출. 청산 실패(미등록/격자 오염/오버플로)
        //   시 fail-closed: inner.Place 절대 미호출 + 라이브 attestation ForceFreeze(옵션) + InvalidOperationException
        //   으로 파이프라인 하드 단절(자산 오염 차단). LegacyPrimary 의 '삼킴+통과' 계약과 정반대 — 권위가 넘어왔으므로.
        private (bool ok, string msg) PlaceQFixedAuthoritative(PaperAsset asset, string symbol, string name,
            PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            decimal px = kind == PaperKind.Market ? currentPrice : limitPrice;
            var oside  = side == PaperSide.Buy ? OrderSide.Buy : OrderSide.Sell;

            bool cleared; QPrice ap = default; QQty aq = default;
            try { cleared = _gate.TryClearAligned(symbol, px, qty, oside, out ap, out aq); }
            catch { cleared = false; }   // 예상외 산술 오염도 fail-closed(절대 raw 송출로 폴백하지 않음)

            if (!cleared) throw FailClosed(symbol);   // ★미등록/오염 → inner 미호출 + 라이브 동결 + throw

            // 권위 치환 — 수량은 항상 정렬(step 내림); 지정가만 정렬 단가 적용(시장가는 단가 미사용).
            decimal newQty   = aq.ToDecimal();
            decimal newLimit = kind == PaperKind.Limit ? ap.ToDecimal() : limitPrice;
            if (newQty <= 0m) throw FailClosed(symbol);   // 서브-스텝 절사로 수량 0 → 송출 금지(오염 0 미반영)

            return _inner.Place(asset, symbol, name, side, kind, newQty, newLimit, currentPrice);
        }

        // 라이브 동결(옵션) 후 하드 단절 예외 생성. _liveAtt null 이면 throw 만으로 단절(footgun 미발생).
        private InvalidOperationException FailClosed(string symbol)
        {
            try { _liveAtt?.ForceFreeze("ShadowClearingBroker QFixed 청산 실패 — fail-closed 실주문 차단: " + symbol); }
            catch { /* 동결 실패해도 throw 로 하드 단절은 유지 */ }
            return new InvalidOperationException(
                "[CLEARING FAIL-CLOSED] QFixed 권위 청산 실패 — 실주문 차단(미등록/격자 오염): " + symbol);
        }

        public void    Cancel(string orderId)               => _inner.Cancel(orderId);
        public decimal HeldQty(PaperAsset asset, string sym) => _inner.HeldQty(asset, sym);
    }
}
