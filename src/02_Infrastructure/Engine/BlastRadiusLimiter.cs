// ============================================================================
// BlastRadiusLimiter — v5.0.0 Think Beyond A: 카나리 블래스트 반경 제한기
//
//  실전 총점화의 가장 위험한 구간(컷오버 직후 카나리 검역창)에서, 완전 무장 상태여도 '주문 명목 상한'을 물리적으로
//  강제한다. 카나리 1주 시험 사출이 fat-finger·버그·경합으로 대형 주문이 되는 것을 송출 초크포인트(GatedOrderDispatcher)
//  에서 원천 차단. 검역 통과(Live 승격) 시 상한을 해제(Clear)하거나 운영 상한으로 상향.
//
//  ── 제미니 초월 논거 ────────────────────────────────────────────────────────
//  기존 IgnitionLock.ProbeReady 는 '주문 건수 1주'만 물리 제한(ProbeRemaining) — 건당 명목(금액) 상한은 없다.
//  PreTradeRiskShield 는 상시 증거금 예산만 본다. 이 제한기는 '검역창 한정, 건당 명목 천장'이라는 직교 차원을
//  단일 초크포인트(모든 무장 제안이 반드시 통과하는 GatedOrderDispatcher.Drain)에 물리적으로 얹어, 무장·점화가
//  모두 참이어도 상한 초과 주문은 실 브로커에 1바이트도 못 나가게 한다(초과=경성 fail-closed 동결).
//
//  δ=0 / 락-프리: 단일 long 상한 + Interlocked. 판정 Check 는 힙 0.
// ============================================================================
using System.Threading;

namespace StockManager.Common
{
    /// <summary>카나리 검역창 동안 주문 명목(raw)에 물리적 상한을 강제하는 락-프리 제한기. 기본 무제한.</summary>
    public sealed class BlastRadiusLimiter
    {
        private long _ceilingRaw = long.MaxValue;   // 기본 무제한(미설정)
        private long _breaches;

        /// <summary>현재 명목 상한(raw). long.MaxValue = 무제한.</summary>
        public long CeilingRaw => Interlocked.Read(ref _ceilingRaw);
        /// <summary>상한 초과로 차단된 누적 건수.</summary>
        public long Breaches   => Interlocked.Read(ref _breaches);
        /// <summary>상한이 설정(활성)돼 있는지.</summary>
        public bool Active     => Interlocked.Read(ref _ceilingRaw) != long.MaxValue;

        /// <summary>명목 상한 설정(카나리 검역 진입 시 예: 1주 명목×안전배율). 음수는 0으로 클램프(모든 주문 차단).</summary>
        public void SetCeiling(long notionalRaw) => Interlocked.Exchange(ref _ceilingRaw, notionalRaw < 0 ? 0 : notionalRaw);

        /// <summary>상한 해제(검역 통과·Live 승격 시). 이후 무제한.</summary>
        public void Clear() => Interlocked.Exchange(ref _ceilingRaw, long.MaxValue);

        /// <summary>명목이 상한 이내면 true(통과), 초과면 false(차단·breach 계수). 경계(==상한)는 통과.</summary>
        public bool Check(long notionalRaw)
        {
            if (notionalRaw > Interlocked.Read(ref _ceilingRaw))
            {
                Interlocked.Increment(ref _breaches);
                return false;
            }
            return true;
        }
    }
}
