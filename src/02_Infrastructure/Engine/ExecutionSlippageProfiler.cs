// ============================================================================
// ExecutionSlippageProfiler — v12.3.0 Micro-Slippage & Execution Fill Latency Profiler
//
//   Section 2: 주문 사출(RecordOrderSent) → 체결 통보(RecordFill) 간 Order-to-Fill 지연(µs)과 의도가 대비 실체결가
//   슬리피지(bps)를 산출해 ★기존 MultiTelemetryDisarmInterlock(v11.4.4, TelemetrySource.Slippage/Latency)에 그대로
//   Note() 로 전달한다 — 신규 병렬 Disarm 인터록을 중복 생성하지 않고 검증된 0ms 원자 Disarm 경로를 재사용한다.
//   동시에 SlippageForensicHeatmapRouter(MS2E.Core, Think-Beyond Idea B)에 브로커별 표본을 적재해 이상 브로커
//   페널티/라우팅 우선순위 신호를 함께 생산한다.
//
//   불변: 결정론(주입 tick·자체시계 0) · δ=0(RecordOrderSent/RecordFill 핫패스 힙 0) · fail-closed(미스테이징
//        슬롯=판정 생략) · liveArmed:false(측정·통보 반영일 뿐 무장 인가 아님 — Note() 가 임계 초과 시 기존
//        인터록을 통해서만 Disarm 격발).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>
    /// 주문 사출~체결 통보 간 슬리피지/지연을 측정해 MultiTelemetryDisarmInterlock 에 전달하는 δ=0 프로파일러.
    /// 슬롯은 호출자가 배정(주문 링 인덱스 등 재사용) — 내부 딕셔너리/리스트 없음(힙 0).
    /// </summary>
    public sealed class ExecutionSlippageProfiler
    {
        private readonly long[] _intendedPriceRaw;   // 슬롯별 의도 주문가(raw). 0 = 미스테이징.
        private readonly long[] _sentTicks;
        private readonly int _capacity;
        private readonly MultiTelemetryDisarmInterlock _interlock;
        private readonly MS2E.Core.SlippageForensicHeatmapRouter? _heatmap;
        private readonly long _tickFrequency;

        public ExecutionSlippageProfiler(int orderSlotCapacity, MultiTelemetryDisarmInterlock interlock,
            MS2E.Core.SlippageForensicHeatmapRouter? heatmap = null, long tickFrequency = 0)
        {
            _capacity = orderSlotCapacity > 0 ? orderSlotCapacity : 1024;
            _intendedPriceRaw = new long[_capacity];
            _sentTicks = new long[_capacity];
            _interlock = interlock ?? throw new ArgumentNullException(nameof(interlock));
            _heatmap = heatmap;
            _tickFrequency = tickFrequency > 0 ? tickFrequency : System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>주문 사출 시점 스테이징(★슬롯은 호출자 배정·δ=0). 동일 슬롯 재사용 시 이전 값을 덮어쓴다.</summary>
        public void RecordOrderSent(int slot, long intendedPriceRaw, long sentTicks)
        {
            if ((uint)slot >= (uint)_capacity) return;
            Volatile.Write(ref _sentTicks[slot], sentTicks);
            Volatile.Write(ref _intendedPriceRaw[slot], intendedPriceRaw);   // ★가격을 마지막에 발행(release) — 판독측 정합
        }

        /// <summary>
        /// 체결 통보 반영(δ=0). 슬리피지(bps)·Order-to-Fill 지연(µs) 산출 → MultiTelemetryDisarmInterlock.Note() 로
        /// 전달(임계 초과 시 기존 인터록이 0ms 원자 Disarm 격발) + 히트맵 라우터에 브로커별 표본 적재.
        /// 미스테이징 슬롯(intendedPriceRaw&lt;=0)은 fail-closed 로 판정 생략(0,0 반환).
        /// </summary>
        public (long slippageBps, long fillLatencyMicros) RecordFill(int slot, long filledPriceRaw, long ackTicks, int brokerId)
        {
            if ((uint)slot >= (uint)_capacity) return (0, 0);
            long intended = Volatile.Read(ref _intendedPriceRaw[slot]);
            if (intended <= 0) return (0, 0);
            long sent = Volatile.Read(ref _sentTicks[slot]);

            long slippageBps = Math.Abs(filledPriceRaw - intended) * 10_000 / intended;
            long elapsedTicks = ackTicks - sent;
            long fillLatencyMicros = elapsedTicks > 0 ? (long)((System.Int128)elapsedTicks * 1_000_000 / _tickFrequency) : 0;

            _interlock.Note(TelemetrySource.Slippage, slippageBps);
            _interlock.Note(TelemetrySource.Latency, fillLatencyMicros);
            _heatmap?.RecordSlippage(brokerId, slippageBps);

            return (slippageBps, fillLatencyMicros);
        }
    }
}
