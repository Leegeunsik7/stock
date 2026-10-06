// ============================================================================
// LiveArmedEgressSentinel — [E2E · 방향 B] Atomic Fail-Closed Leakage Evidence Collector(실 브로커 송출 초크포인트 백업 인터락)
//
//  OrderDrainWorker → IBroker.Place 는 실 주문이 프로세스를 떠나는 유일한 관문이다. 정상이라면 liveArmed:false
//  (Dispatch.ValveWired ∧ Dispatch.LiveArmingWired 중 하나라도 false)에서는 상류(GatedOrderDispatcher 무장 술어 · 스코프 장벽 ·
//  게이트웨이)가 이미 막아 여기까지 오지 않는다. 이 데코레이터는 상류가 모두 오작동(버그·설정 경합·우회)해 Place 가 도달한
//  '불가능한' 경우를 잡는 마지막 백업이다:
//    · 실 브로커로 전달 0(즉시 거부 반환 → OrderDrainWorker 가 throw → 기존 Mission-A 벽 ForceFreeze)
//    · 증거 수집(시도 수 · 마지막 종목/수량/시각 · MMF 블랙박스 EgressBypassBlocked) + sticky 차단(ArmingInterlockSource.EgressBypass)
//    · 재무장 = 물리 이중키 재확인(TryRearm)
//  ★지시서의 '프로세스 네트워크 소켓 물리 차단'은 구현하지 않았다: 사용자 모드 프로세스가 자기 소켓을 OS 수준에서 끊으려면
//    방화벽 규칙(관리자 권한 · 시스템 보안 설정 변경)이 필요하고, 시세·체결 피드까지 끊겨 이미 나간 주문의 상태를 볼 수 없게 된다.
//    주문이 나가는 단일 초크포인트에서 원자 차단하는 편이 더 좁고 확실한 방어다.
//  Cancel 은 차단하지 않는다(취소는 노출을 줄이는 방향 — 막으면 오히려 위험). HeldQty 는 조회라 그대로 위임.
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    public sealed class LiveArmedEgressSentinel : IBroker, IArmingBlockSource
    {
        private readonly IBroker _inner;
        private readonly Func<bool> _liveArmed;
        private readonly MmfFlightRecorder? _blackbox;
        private readonly Action<string>? _onBypass;
        private ArmingInterlockLink? _link;
        private int _tripped;
        private long _bypassAttempts, _forwarded, _lastSymbolCode, _lastQtyMilli, _lastTicks;

        /// <param name="liveArmed">물리 이중키 판독(ValveWired ∧ LiveArmingWired). 판독 예외 = 미무장(fail-closed).</param>
        /// <param name="onBypass">우회 감지 통지(예: attestation.ForceFreeze) — 예외는 삼킨다(차단은 이미 반영).</param>
        public LiveArmedEgressSentinel(IBroker inner, Func<bool> liveArmed, MmfFlightRecorder? blackbox = null, Action<string>? onBypass = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _liveArmed = liveArmed ?? throw new ArgumentNullException(nameof(liveArmed));
            _blackbox = blackbox;
            _onBypass = onBypass;
        }

        public string Name => _inner.Name;
        public bool IsLive => _inner.IsLive;
        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;
        public long BypassAttempts => Interlocked.Read(ref _bypassAttempts);
        public long Forwarded => Interlocked.Read(ref _forwarded);
        public long LastSymbolCode => Interlocked.Read(ref _lastSymbolCode);
        public long LastQtyMilli => Interlocked.Read(ref _lastQtyMilli);
        public long LastTicks => Interlocked.Read(ref _lastTicks);

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name, PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            bool armed;
            try { armed = _liveArmed(); } catch { armed = false; }
            // 무장 상태여도 이미 우회가 한 번 감지됐다면(sticky) 재무장 전까지 전달 금지
            if (armed && !Tripped)
            {
                Interlocked.Increment(ref _forwarded);
                return _inner.Place(asset, symbol, name, side, kind, qty, limitPrice, currentPrice);
            }
            Interlocked.Increment(ref _bypassAttempts);
            long code = long.TryParse(symbol, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long c) ? c : -1;
            Interlocked.Exchange(ref _lastSymbolCode, code);
            Interlocked.Exchange(ref _lastQtyMilli, qty > 9_000_000_000_000m || qty < 0m ? long.MaxValue : (long)(qty * 1000m));
            Interlocked.Exchange(ref _lastTicks, DateTime.UtcNow.Ticks);
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) == 0) Volatile.Read(ref _link)?.Resync();
            try { _blackbox?.Record(FlightEventKind.EgressBypassBlocked, code, armed ? 1 : 0, Interlocked.Read(ref _bypassAttempts)); } catch { }
            const string why = "liveArmed:false 송출 우회 차단(EgressSentinel) — 실 브로커 전달 0";
            try { _onBypass?.Invoke(why); } catch { }
            return (false, why);
        }

        public void Cancel(string orderId) => _inner.Cancel(orderId);
        public decimal HeldQty(PaperAsset asset, string symbol) => _inner.HeldQty(asset, symbol);

        /// <summary>재무장 — 물리 이중키 재확인 시에만 sticky 해제.</summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _tripped, 0);
            Volatile.Read(ref _link)?.Resync();
            return true;
        }
    }
}
