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
        private readonly Func<bool>? _interlockBlocked;
        private long _interlockVetoes;
        private ArmingInterlockLink? _link;
        private int _tripped;
        private long _bypassAttempts, _forwarded, _lastSymbolCode, _lastQtyMilli, _lastTicks;

        /// <param name="liveArmed">물리 이중키 판독(ValveWired ∧ LiveArmingWired). 판독 예외 = 미무장(fail-closed).</param>
        /// <param name="onBypass">우회 감지 통지(예: attestation.ForceFreeze) — 예외는 삼킨다(차단은 이미 반영).</param>
        /// <param name="interlockBlocked">[카오스 스톰] 원자 인터락 집계어 판독(ArmingInterlockBitmask.Blocked). 차단이면 무장이어도 전달 0.
        ///   ★드레인 워커는 송출 '직전'이 아니라 카나리 · 라우터 게이트 · 지연 프로브 '앞'에서 집계어를 1회 읽는다 — 그 뒤 Place 까지의
        ///   구간에 격발한 비트는 이 초크포인트만 잡을 수 있다(TOCTOU 창 봉쇄). 판독 예외 = 차단(fail-closed).</param>
        public LiveArmedEgressSentinel(IBroker inner, Func<bool> liveArmed, MmfFlightRecorder? blackbox = null, Action<string>? onBypass = null,
                                       Func<bool>? interlockBlocked = null)
        {
            _interlockBlocked = interlockBlocked;
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
        /// <summary>[카오스 스톰] 무장 상태인데 인터락 집계어 차단으로 거부한 송출 수(우회 아님 — 판정~송출 사이 격발).</summary>
        public long InterlockVetoes => Interlocked.Read(ref _interlockVetoes);

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
            // [카오스 스톰] 무장이어도 인터락 집계어가 차단이면 전달 0 — 상류 판독 뒤 격발한 비트(TOCTOU)를 최종 초크포인트에서 원자 veto.
            //   우회(EgressBypass sticky)로 올리지 않는다: 원인 비트가 이미 서 있고, 이건 상류 결함이 아니라 판정~송출 사이 경합이다.
            if (armed && !Tripped && _interlockBlocked is { } ib)
            {
                bool blocked;
                try { blocked = ib(); } catch { blocked = true; }
                if (blocked)
                {
                    Interlocked.Increment(ref _interlockVetoes);
                    try { _blackbox?.Record(FlightEventKind.EgressBypassBlocked, 0, 1, Interlocked.Read(ref _interlockVetoes), 2); } catch { }
                    return (false, "인터락 집계어 차단 — 실 브로커 전달 0(송출 직전 원자 veto)");
                }
            }
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
