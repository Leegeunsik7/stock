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

namespace StockManager.Common
{
    /// <summary>IBroker 데코레이터 — Place 시 ShadowCutoverGate 로 신 QFixed 청산을 병렬 관측(레거시 권위 불변).</summary>
    public sealed class ShadowClearingBroker : IBroker
    {
        private readonly IBroker            _inner;
        private readonly ShadowCutoverGate  _gate;

        public ShadowClearingBroker(IBroker inner, ShadowCutoverGate gate)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _gate  = gate  ?? throw new ArgumentNullException(nameof(gate));
        }

        public string Name   => $"Shadow({_inner.Name})";
        public bool   IsLive => _inner.IsLive;

        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name,
            PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            // 1) 섀도우 관측 — 게이트 INERT 면 호출조차 안 함(완전 통과). 활성 시에만 신 엔진 병렬 대조.
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

            // 2) 권위 경로 — 레거시 브로커가 그대로 체결/거부를 결정. 데코레이터는 1:1 위임.
            return _inner.Place(asset, symbol, name, side, kind, qty, limitPrice, currentPrice);
        }

        public void    Cancel(string orderId)               => _inner.Cancel(orderId);
        public decimal HeldQty(PaperAsset asset, string sym) => _inner.HeldQty(asset, sym);
    }
}
