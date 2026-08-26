// ============================================================================
// MockExecutionOrderBookMirror — v10.1.0 무할당 가상 체결 미러 (드라이런 체결 엔진)
//
//  실제 외부 브로커 소켓으로 발주를 유출하지 않고, 실전과 '동일한' SealedEmissionInterlock 봉인 경로(exactly-once +
//  페이로드 동결 + FNV seal)를 통과한 뒤, 최하위 소켓 전송 직전 샌드박스 가상 미러로 원자적 루프백(broker.Place 대체)
//  한다. $0원 비용으로 전체 오더 파이프라인을 100% 가상 검증 — 체결 지연(RTT)·지정가 슬리피지(adverse)·부분 체결을 δ=0
//  로 모사한다. IBroker·소켓·onSend(실 소켓) 참조 0 — 어떤 경로로도 실물 발주 누수 불가(liveArmed:false).
//
//  ★불변: δ=0(SealAndVirtualFill/VirtualFill 핫패스 힙 0·Interlocked/Volatile·값타입 토큰·Int128 슬리피지) · 결정론
//    (adverse 슬리피지 SplitMix64 시드·벽시계 무참조) · liveArmed:false(실 소켓 미접촉·미러 전용) · fail-closed
//    (봉인 재검 실패 → 미체결). ★Reset() = 가상 잔량 O(1) 제로화(전환 시 실전 계좌 오염 차단·barrier onPurge).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>무할당 가상 체결 미러(δ=0). SealAndVirtualFill = 실전 동일 봉인 경로 통과 후 미러 루프백. 슬리피지/부분/RTT 모사.</summary>
    public sealed class MockExecutionOrderBookMirror
    {
        private const long Scale = 100_000_000L;   // QFixed 10^8
        private const long Golden = unchecked((long)0x9E3779B97F4A7C15UL);

        private readonly SealedEmissionInterlock _sealed;   // 드라이런 봉인(실전과 동일 exactly-once + seal 경로)
        private readonly SealedSend _virtualFillOnSend;     // ★δ=0: 인스턴스 메서드그룹→SealedSend 델리게이트 1회 캐시(사출당 변환 할당 제거)
        private readonly long _slippageRatioRaw;            // 최대 adverse 슬리피지(raw 비율·10^8=100%)
        private readonly long _baseRttNanos;                // 기본 가상 RTT
        private readonly long _partialThresholdRaw;         // 이 수량 초과 시 부분 체결
        private long _prngState;
        private long _virtualFills, _partialFills, _sealRejects;
        private long _lastSlippageBps, _lastFillRttNanos, _lastFillPriceRaw, _lastFilledQtyRaw, _residualVirtualQtyRaw;

        public MockExecutionOrderBookMirror(SealedEmissionInterlock? sealedEmission = null, long slippageRatioRaw = 100_000L,
                                            long baseRttNanos = 500_000L, long partialThresholdRaw = 100L * Scale, ulong seed = 0xD189A5EEDUL)
        {
            _sealed = sealedEmission ?? new SealedEmissionInterlock();
            _virtualFillOnSend = VirtualFillOnSend;   // ★δ=0: 델리게이트 1회 생성(핫패스 변환 할당 제거)
            _slippageRatioRaw = Math.Clamp(slippageRatioRaw, 0L, Scale);
            _baseRttNanos = Math.Max(0L, baseRttNanos);
            _partialThresholdRaw = Math.Max(1L, partialThresholdRaw);
            _prngState = unchecked((long)seed);
        }

        // ── 읽기(δ=0·락프리) ──
        public long VirtualFills          => Interlocked.Read(ref _virtualFills);
        public long PartialFills          => Interlocked.Read(ref _partialFills);
        public long SealRejects           => Interlocked.Read(ref _sealRejects);
        public long LastSlippageBps       => Interlocked.Read(ref _lastSlippageBps);
        public long LastFillRttNanos      => Interlocked.Read(ref _lastFillRttNanos);
        public long LastFillPriceRaw      => Interlocked.Read(ref _lastFillPriceRaw);
        public long LastFilledQtyRaw      => Interlocked.Read(ref _lastFilledQtyRaw);
        public long ResidualVirtualQtyRaw => Interlocked.Read(ref _residualVirtualQtyRaw);
        public bool Active                => Interlocked.Read(ref _virtualFills) > 0L;
        public long DoubleDispatchBlocks  => _sealed.DoubleDispatchBlocks;

        /// <summary>
        /// ★봉인 사출 후 가상 체결(δ=0). 실전과 '동일한' SealedEmissionInterlock(exactly-once + 페이로드 동결 + seal) 경로를
        /// 통과한 뒤 미러로 루프백 — broker.Place 절대 미호출. 봉인 재검 실패/중복 사출 시 미체결(false).
        /// </summary>
        public bool SealAndVirtualFill(long orderId, long symbolCode, byte side, byte kind, long priceRaw, long qtyRaw)
            => _sealed.TrySealAndEmit(orderId, symbolCode, side, kind, priceRaw, qtyRaw, _virtualFillOnSend);

        // SealedSend — 최하위 '소켓 전송 직전' 원자 루프백 지점(실전에선 broker.Place). 불변 봉인 토큰만 수령.
        private void VirtualFillOnSend(in SealedOrderToken t)
        {
            if (!SealedEmissionInterlock.VerifySeal(in t)) { Interlocked.Increment(ref _sealRejects); return; }   // fail-closed
            // adverse 슬리피지(taker 불리): 매수=상향·매도=하향.
            long jitter = NextDecision() % (_slippageRatioRaw + 1L);   // ∈ [0, _slippageRatioRaw]
            long delta  = (long)((System.Int128)t.PriceRaw * jitter / Scale);
            long fillPrice = t.SideByte == 0 ? t.PriceRaw + delta : Math.Max(0L, t.PriceRaw - delta);
            // 부분 체결(수량 비례): 임계 초과분은 절반만 체결.
            long filled = t.QtyRaw;
            if (t.QtyRaw > _partialThresholdRaw)
            {
                filled = _partialThresholdRaw + (t.QtyRaw - _partialThresholdRaw) / 2L;
                Interlocked.Increment(ref _partialFills);
            }
            // 가상 RTT(기본 + 지터).
            long rtt = _baseRttNanos + (NextDecision() % (_baseRttNanos + 1L));
            // 기록(δ=0). 슬리피지 bps = jitter/Scale × 10000 = jitter / (Scale/10000).
            Volatile.Write(ref _lastSlippageBps, jitter / (Scale / 10_000L));
            Volatile.Write(ref _lastFillPriceRaw, fillPrice);
            Volatile.Write(ref _lastFilledQtyRaw, filled);
            Volatile.Write(ref _lastFillRttNanos, rtt);
            long resid = Interlocked.Add(ref _residualVirtualQtyRaw, filled);   // 누적 가상 잔량(전환 시 퍼지 대상)
            if (resid < 0L) Volatile.Write(ref _residualVirtualQtyRaw, long.MaxValue);   // 오버플로 포화
            Interlocked.Increment(ref _virtualFills);
        }

        /// <summary>
        /// ★누적 가상 상태 O(1) 원자 제로화(0x00 Zeroization). 드라이런↔실전 전환 찰나 barrier 가 호출 — 누적 가상 잔량·최근 체결·
        /// 봉인 래치를 원자 제로화해 실전 계좌 오염(Residual Leakage)을 차단한다(신규 미러 체결은 barrier 쿨다운이 Bounce 로 차단).
        /// 생애 카운터(VirtualFills 등)는 텔레메트리로 유지. ※전환 찰나 in-flight 1건 잔량은 순수 가상·차기 사이클 정리(무자본 영향).
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _residualVirtualQtyRaw, 0L);   // ★가상 잔량 제로화(오염 차단)
            Volatile.Write(ref _lastSlippageBps, 0L);
            Volatile.Write(ref _lastFillRttNanos, 0L);
            Volatile.Write(ref _lastFillPriceRaw, 0L);
            Volatile.Write(ref _lastFilledQtyRaw, 0L);
            _sealed.Reset();                                       // 봉인 래치 슬롯 영점(exactly-once 재사용)
        }

        private long NextDecision()   // Interlocked 전진 SplitMix64(δ=0·음수 제거)
        {
            long s = Interlocked.Add(ref _prngState, Golden);
            ulong z = (ulong)s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (long)((z ^ (z >> 31)) & 0x7FFFFFFFFFFFFFFFUL);
        }
    }
}
