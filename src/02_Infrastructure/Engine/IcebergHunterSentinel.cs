// ============================================================================
// IcebergHunterSentinel — v9.0.0 빙산호가 포획 센티널
//
//  L2 호가창에서 잔량이 줄지 않고 지속 채워지는 미시 체결 패턴(Micro-Fill Reload Signature)을 O(1) 로 스캔해,
//  숨겨진 대형 기관의 빙산호가(Iceberg) 감춰진 물량을 역추적한다. 핵심 단서: 한 가격 레이어에서 '누적 체결량'이
//  '표시 잔량'을 초과하며 표시가 계속 재전개되면(IcebergReloadSignatureDetector), 그 초과 체결분이 곧 숨은 물량이다.
//
//  ★불변: δ=0(ObserveBid/ObserveAsk 핫패스 힙 0·Interlocked·내장 검출기 O(1)) · 결정론(주입 시계) · liveArmed:false
//    (순수 미시구조 관측 — IBroker·소켓·onSend 참조 0. 포획은 SOR 에 '수치'만 공급·발주 유발 0) · fail-closed(경계 안전).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>빙산호가 포획 센티널(δ=0·O(1)). ObserveBid/Ask(표시잔량,체결량,ticks) → 재전개 서명+누적체결로 숨은 물량 역추적.</summary>
    public sealed class IcebergHunterSentinel
    {
        private readonly IcebergReloadSignatureDetector _bidReload, _askReload;
        private readonly long _confirmThresholdPermille;   // 빙산 확정 임계(‰)
        private long _cumBidTraded, _cumAskTraded, _hiddenBid, _hiddenAsk, _icebergsDetected;
        private int  _bidActive, _askActive;

        public IcebergHunterSentinel(long confirmThresholdPermille = 500, long reloadThresholdMicros = 500,
                                     Func<long>? nowTicks = null, long tickFrequency = 0)
        {
            _confirmThresholdPermille = Math.Clamp(confirmThresholdPermille, 1, 1000);
            _bidReload = new IcebergReloadSignatureDetector(reloadThresholdMicros, 800, nowTicks, tickFrequency);
            _askReload = new IcebergReloadSignatureDetector(reloadThresholdMicros, 800, nowTicks, tickFrequency);
        }

        public long EstimatedHiddenBidQtyRaw => Interlocked.Read(ref _hiddenBid);
        public long EstimatedHiddenAskQtyRaw => Interlocked.Read(ref _hiddenAsk);
        public long EstimatedHiddenTotalRaw  => Interlocked.Read(ref _hiddenBid) + Interlocked.Read(ref _hiddenAsk);
        public long IcebergsDetected         => Interlocked.Read(ref _icebergsDetected);
        public bool BidIcebergActive         => Volatile.Read(ref _bidActive) == 1;
        public bool AskIcebergActive         => Volatile.Read(ref _askActive) == 1;
        public bool IcebergActive            => BidIcebergActive || AskIcebergActive;
        public long BidConfidencePermille    => _bidReload.ConfirmationPermille;
        public long AskConfidencePermille    => _askReload.ConfirmationPermille;

        /// <summary>최우선 매수호가 관측(δ=0). displayedQty=표시 잔량, tradedQty=이번 체결량. 재전개 서명+누적체결로 빙산 포획.</summary>
        public void ObserveBid(long displayedQty, long tradedQty, long ticks)
            => Observe(_bidReload, ref _cumBidTraded, ref _hiddenBid, ref _bidActive, displayedQty, tradedQty, ticks);

        /// <summary>최우선 매도호가 관측(δ=0).</summary>
        public void ObserveAsk(long displayedQty, long tradedQty, long ticks)
            => Observe(_askReload, ref _cumAskTraded, ref _hiddenAsk, ref _askActive, displayedQty, tradedQty, ticks);

        private void Observe(IcebergReloadSignatureDetector reload, ref long cumTraded, ref long hidden, ref int active,
                             long displayedQty, long tradedQty, long ticks)
        {
            reload.Observe(displayedQty, ticks);
            if (tradedQty > 0) Interlocked.Add(ref cumTraded, tradedQty);
            long conf = reload.ConfirmationPermille;
            if (conf >= _confirmThresholdPermille)
            {
                if (Interlocked.Exchange(ref active, 1) == 0) Interlocked.Increment(ref _icebergsDetected);
                // ★숨은 물량 추정 = 누적 체결 − 최근 표시(표시 대비 초과 체결분 = 재전개로 감춘 실물).
                long over = Interlocked.Read(ref cumTraded) - (displayedQty < 0 ? 0 : displayedQty);
                Volatile.Write(ref hidden, over > 0 ? over : 0L);   // fail-closed: 표시 잔량으로 완전 설명되면 초과 은닉분 0(phantom 미보고)
            }
            else Volatile.Write(ref active, 0);
        }

        /// <summary>추적 리셋(새 세션/심볼 전환).</summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _cumBidTraded, 0); Interlocked.Exchange(ref _cumAskTraded, 0);
            Volatile.Write(ref _hiddenBid, 0); Volatile.Write(ref _hiddenAsk, 0);
            Volatile.Write(ref _bidActive, 0); Volatile.Write(ref _askActive, 0);
            _bidReload.Reset(); _askReload.Reset();   // ★내부 검출기 상태(reloads/obs/dip)도 초기화 — 심볼 전환 시 잔재 확정확률 유령 빙산 방지
        }
    }
}
