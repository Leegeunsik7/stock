// ============================================================================
// LiveArming — S29 Mission B(v4.57.0): 3중 안전 게이트 '실 무장(Live Arming)' 술어
//
//  GatedOrderDispatcher._isArmed 는 v4.55.0 까지 '주입만 받는' 스캐폴드(불투명 Func<bool>)였다. 본 단위는 그
//  술어가 실제로 평가해야 할 '실거래 무장' 판정을 한 곳에 모아 결정론 단위테스트가 가능한 1급 시민으로 승격한다.
//  세 생존 지표의 AND:
//    ① OrderAttestationService.Mode == Normal   (미동결 — 원장 미스매치·외부 결함 시 EmergencyFreeze)
//    ② IgnitionLock.AllowPhysical()             (점화락 물리 발주 허용 — Locked/동결/창만료면 false)
//    ③ brokerArmed()                            (브로커 실무장 — 보통 () => BrokerHub.IsArmed(db))
//
//  ── ★fail-closed 절대 원칙(Mission B.3) ────────────────────────────────────────
//  어떤 항을 평가하다 예외가 나도(예: brokerArmed 의 db 설정 I/O 일시 단선) 절대 밖으로 터뜨리거나 '무장'으로
//  오판하지 않고 무조건 false(미무장)로 수렴한다. 이는 GatedOrderDispatcher.Drain 의 외곽 try/catch(→armed=false
//  →GatedOut++)와 더해 '이중 벽'(방어 심층화)을 이룬다 — 둘 중 어느 층에서 잡혀도 결과는 동일(미무장·GatedOut++).
//
//  ── 단락(short-circuit) 순서 = δ=0 핫패스 비용 최소화 ──────────────────────────
//  ①Mode(volatile read·무할당·가장 결정적) → ②AllowPhysical()(락-프리 enum 스위치·무할당) → ③brokerArmed()
//  (db 설정 I/O 가능·throw 원천)를 '맨 끝'에 둔다. 기본 자세(점화 Locked)면 ②에서 단락되어 ③ db 읽기가 아예
//  발생하지 않는다 → 정상(미무장) 경로는 db I/O 0·할당 0. liveArmed:false 박제는 brokerArmed 기본 false +
//  점화 Locked 가 보존(본 게이트는 '판정'만; 실 송출은 디스패처 onSend + 드레인 루프 배선이라는 별도 옵트인 단계).
// ============================================================================
using System;

namespace StockManager.Common
{
    /// <summary>
    /// 3중 안전 게이트 실 무장 술어. IsArmed() = (미동결) ∧ (점화 물리 허용) ∧ (브로커 무장).
    /// 평가 중 어떤 예외도 fail-closed(false)로 흡수한다. GatedOrderDispatcher 에 IsArmed 를 Func&lt;bool&gt; 로 주입.
    /// </summary>
    public sealed class LiveArmingGate
    {
        private readonly IgnitionLock            _ignition;
        private readonly OrderAttestationService _att;
        private readonly Func<bool>              _brokerArmed;   // db/정적 BrokerHub 결합을 술어로 격리(테스트 주입 가능)

        /// <param name="brokerArmed">브로커 실무장 프로브. 앱 배선은 보통 () =&gt; BrokerHub.IsArmed(db).</param>
        public LiveArmingGate(IgnitionLock ignition, OrderAttestationService attestation, Func<bool> brokerArmed)
        {
            _ignition    = ignition    ?? throw new ArgumentNullException(nameof(ignition));
            _att         = attestation ?? throw new ArgumentNullException(nameof(attestation));
            _brokerArmed = brokerArmed ?? throw new ArgumentNullException(nameof(brokerArmed));
        }

        /// <summary>실 무장 여부. 세 지표 AND(단락 순서 = 무할당·db-I/O-회피 최적). 평가 중 어떤 예외도 fail-closed(false).</summary>
        public bool IsArmed()
        {
            try
            {
                return _att.Mode == GatingMode.Normal   // ① 가장 싸고 결정적(volatile read·무할당)
                    && _ignition.AllowPhysical()        // ② 락-프리(동결·단계·프로브·창 AND 게이트·무할당)
                    && _brokerArmed();                  // ③ db 설정 I/O 가능 — 단락 끝에 둬 정상 미무장 시 미호출
            }
            catch
            {
                return false;   // ★fail-closed: 평가 중 단선/예외 → 무조건 미무장(무장 오판 절대 금지)
            }
        }
    }
}
