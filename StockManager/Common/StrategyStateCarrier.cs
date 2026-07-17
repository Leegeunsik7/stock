// ============================================================================
// StrategyStateCarrier — v5.3.0 과제2: 무할당 미체결 상태 이월 프로토콜
//
//  구버전 전략이 활성 중 사출한 '진행 중 주문(In-Flight)'과 누적 통계(실현손익·최대낙폭·누적체결)를 신버전 전략으로
//  δ=0 원자 소유권 전이(Ownership Transfer)한다. 문자열·힙 할당 0 — blittable StrategyState 구조체 값 복사로 구 ALC 타입
//  참조를 절단(참조 잔존 시 구 ALC 언로드가 봉쇄돼 좀비화되므로 값 복사가 필수).
//
//  ── 정직 회계(정찰) ─────────────────────────────────────────────────────────
//  StrategySlot 의 Interlocked.Exchange 참조 스왑 + _draining 그레이스풀 인수인계 패턴을 blittable In-Flight/통계로 확장.
//  기존 PendingOrder/BacktestSummary/AlphaReport 는 힙 참조 포함(string·List) → blittable 아님 → 스칼라만 투영. blittable
//  원본은 OrderProposal·AdaptiveOrphanReaper.Slot·L2Depth(InlineArray). 이월 RTT 는 주입 시계로 ns 측정.
//
//  δ=0: StrategyState 는 unmanaged(long/int/InlineArray) — 힙 0. 전이는 리로드 게이트(콜드·저빈도) 단일 라이터 하 값 복사.
//  결정론: 주입 시계.
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>C#12 InlineArray — 8원소 long 인라인 버퍼(힙 0). 이월 In-Flight 주문 ID 최대 8건.</summary>
    [InlineArray(8)] public struct Fixed8Long { private long _e0; }

    /// <summary>전략 버전 간 이월되는 blittable 상태(unmanaged·힙 0). 스칼라 통계 + 고정 In-Flight ID 배열.</summary>
    public struct StrategyState
    {
        public long Generation;         // 현 소유 세대(핫스왑마다 재태그)
        public long RealizedPnlRaw;     // 실현손익(raw 10^8)
        public long MaxDrawdownRaw;     // 최대 낙폭(raw)
        public long PeakEquityRaw;      // 고점 자본(낙폭 기준)
        public long CumFills;           // 누적 체결 수
        public int  InFlightCount;      // 진행 중 주문 수(≤8)
        public Fixed8Long InFlightOrderIds;
    }

    /// <summary>구→신 전략 blittable δ=0 소유권 전이 캐리어. In-Flight 등록/해제 + 핫스왑 시 원자 이월. 참조 절단(언로드 봉쇄 방지).</summary>
    public sealed class StrategyStateCarrier
    {
        private readonly object     _gate = new();   // 전이·In-Flight 변경 직렬화(리로드 콜드·발주 저빈도·무할당 lock)
        private StrategyState       _state;
        private readonly Func<long> _nowTicks;
        private readonly long       _tickFreq;
        private long _handovers, _lastHandoverRttNs, _droppedInFlight;

        public StrategyStateCarrier(Func<long>? nowTicks = null, long tickFrequency = 0)
        {
            _nowTicks = nowTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;
            _tickFreq = tickFrequency > 0 ? tickFrequency : System.Diagnostics.Stopwatch.Frequency;
        }

        public long Handovers          => Interlocked.Read(ref _handovers);
        public long LastHandoverRttNs  => Interlocked.Read(ref _lastHandoverRttNs);
        public long DroppedInFlight    => Interlocked.Read(ref _droppedInFlight);
        public long CumFills           { get { lock (_gate) return _state.CumFills; } }
        public int  InFlightCount      { get { lock (_gate) return _state.InFlightCount; } }
        public long Generation         { get { lock (_gate) return _state.Generation; } }

        /// <summary>진행 중 주문 등록(사출 시). 8건 초과분은 드롭 계수(이월 상한 — 초과 시 별도 원장이 추적).</summary>
        public void TrackInFlight(long orderId)
        {
            lock (_gate)
            {
                if (_state.InFlightCount >= 8) { Interlocked.Increment(ref _droppedInFlight); return; }
                _state.InFlightOrderIds[_state.InFlightCount++] = orderId;
            }
        }

        /// <summary>진행 중 주문 해제(체결/취소 시). 마지막 원소로 스왑 삭제(δ=0·순서 무관).</summary>
        public void ReleaseInFlight(long orderId)
        {
            lock (_gate)
            {
                int n = _state.InFlightCount;
                for (int i = 0; i < n; i++)
                    if (_state.InFlightOrderIds[i] == orderId)
                    {
                        _state.InFlightOrderIds[i] = _state.InFlightOrderIds[n - 1];
                        _state.InFlightOrderIds[n - 1] = 0;
                        _state.InFlightCount = n - 1;
                        return;
                    }
            }
        }

        /// <summary>통계 누적(체결 확정 시). δ=0.</summary>
        public void RecordFill(long realizedPnlDeltaRaw, long equityRaw)
        {
            lock (_gate)
            {
                // ★적대검증 교정 — 첫 체결에서 고점을 실측 자기자본으로 시딩. 미시딩(기본 0) 시 최초 자기자본이 0 이하이면
                //   (선물 마진 소진·음수 순자산) 유령 기준선 0 대비 허위 낙폭(dd=0-equity)이 잡혀 Handover 로 신버전에 영구 오염 이월.
                if (_state.CumFills == 0) _state.PeakEquityRaw = equityRaw;
                _state.CumFills++;
                _state.RealizedPnlRaw += realizedPnlDeltaRaw;
                if (equityRaw > _state.PeakEquityRaw) _state.PeakEquityRaw = equityRaw;
                long dd = _state.PeakEquityRaw - equityRaw;   // 고점 대비 낙폭
                if (dd > _state.MaxDrawdownRaw) _state.MaxDrawdownRaw = dd;
            }
        }

        /// <summary>
        /// ★핫스왑 원자 이월 — 구버전 상태(In-Flight ID·통계)를 값 그대로 유지한 채 소유 세대만 신버전으로 재태그.
        /// blittable 값이라 구 ALC 타입 참조를 전혀 잡지 않음(언로드 봉쇄 방지). RTT(ns) 측정. 리로드 게이트가 래치 하에 1회 호출.
        /// </summary>
        public void Handover(long newGeneration)
        {
            long t0 = _nowTicks();
            lock (_gate) { _state.Generation = newGeneration; }
            long dt = _nowTicks() - t0; if (dt < 0) dt = 0;
            Interlocked.Exchange(ref _lastHandoverRttNs, dt * 1_000_000_000L / _tickFreq);
            Interlocked.Increment(ref _handovers);
        }

        /// <summary>현재 상태 값 스냅샷(신버전이 이월 상태 판독). 값 복사 — 참조 0.</summary>
        public StrategyState Snapshot() { lock (_gate) return _state; }
    }
}
