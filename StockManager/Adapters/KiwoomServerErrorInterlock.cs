// ============================================================================
// KiwoomServerErrorInterlock — [키움 COM · 방향 B] 키움 중대 서버 오류 → 발주 회로 sticky Disarm
//
//  프록시가 OnReceiveMsg 를 분류해 중대 오류(비밀번호 오류 · 계좌 정지/사고/해지 · 증거금/주문가능금액 부족)나 STA 펌프 동결을
//  ErrorReport(KiwoomServerErrorCodes 71xx)로 보내면, 이 인터락이 ArmingInterlockBitmask 비트를 올린다(물리 이중키 재무장).
//  ※ 같은 ErrorReport 는 기존 KiwoomFreezeRelay 가 code≠0 동결(ForceFreeze)로도 처리한다 — 이 인터락은 동결과 별개로
//    '무엇 때문에 막혔는지'(LastCode)를 집계어에 남기고 재무장 경로를 명시한다. 비중대 코드는 무시(관측만).
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    public sealed class KiwoomServerErrorInterlock : IArmingBlockSource
    {
        private int _tripped, _lastCode;
        private long _criticals, _ignored;
        private ArmingInterlockLink? _link;

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;
        public int LastCode => Volatile.Read(ref _lastCode);
        public long Criticals => Interlocked.Read(ref _criticals);
        public long Ignored => Interlocked.Read(ref _ignored);

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        /// <summary>브리지 ErrorReceived 구독 지점(δ=0 · 이벤트 시그니처 그대로).</summary>
        public void OnError(ErrorPayload err)
        {
            if (!KiwoomServerErrorCodes.IsCritical(err.ErrorCode)) { Interlocked.Increment(ref _ignored); return; }
            Interlocked.Increment(ref _criticals);
            Volatile.Write(ref _lastCode, err.ErrorCode);
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) == 0) Volatile.Read(ref _link)?.Resync();
        }

        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _tripped, 0);
            Volatile.Read(ref _link)?.Resync();
            return true;
        }
    }
}
