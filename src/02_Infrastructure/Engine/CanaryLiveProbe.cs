// ============================================================================
// CanaryLiveProbe — v5.0.0 Phase2: 카나리 1주 초정밀 시험 사출 종단 검증 & 청산 대조
//
//  실전 첫 1주 사출의 종단(End-to-End) 파이프라인을 최종 검증한다:
//   ① RTT 포획: 실 브로커 세션 RTT(KiwoomBridgeClient.CurrentIpcRttTicks = 100ns ticks)를 µs 로 '단위 정규화'해
//      AtomicCutoverGate 자율 카나리 검역(단측 3σ)에 공급. ★베이스라인·실측을 반드시 같은 단위(µs)로 통일(단위 혼입 시
//      3σ 판정 무의미 — 정찰 gotcha 교정).
//   ② 청산 대조: 체결 즉시 제비용 차감된 최종 정산금액(broker-reported net)을 백엔드 LiveAccountingMesh 의
//      Remainder Carry Guard 계산치(LastFillNetRaw)와 1raw 정밀 대조 → AtomicCutoverGate.ReconcileDualTrack 로 위임
//      (|drift|>tol → 경성 동결). ★회계는 2단계 BelowNormal 스레드라 비동기 — FillsFinalized 소화 완료 후 대조해야
//      소수점 일치 성립(정찰 gotcha 교정): TryReconcileLatestFill 이 완료를 확인 후에만 대조.
//
//  liveArmed:false: 본 프로브는 관측·대조·검역 위임만 — 실 1주 사출 자체는 운영자 이중키+서명(AtomicCutoverGate.Cutover).
//  결정론: 주입 시계/단위. δ=0 아님(검역 경로·핫패스 아님).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>카나리 1주 종단 검증기 — RTT 단위 정규화 + 건별 net 정산 1raw 대조. AtomicCutoverGate/LiveAccountingMesh 재사용.</summary>
    public sealed class CanaryLiveProbe
    {
        private readonly AtomicCutoverGate  _cutover;
        private readonly LiveAccountingMesh _mesh;
        private readonly MmfFlightRecorder? _blackbox;

        private long _lastNetDriftRaw, _reconciledCount, _reconcileFreezes, _rttSamples;
        private long _lastSeqSeen = -1L;   // 마지막으로 대조한 net seqlock 버전(중복 대조 방지·일관 페어 키)

        public CanaryLiveProbe(AtomicCutoverGate cutover, LiveAccountingMesh mesh, MmfFlightRecorder? blackbox = null)
        {
            _cutover  = cutover ?? throw new ArgumentNullException(nameof(cutover));
            _mesh     = mesh    ?? throw new ArgumentNullException(nameof(mesh));
            _blackbox = blackbox;
        }

        public long LastNetDriftRaw   => Interlocked.Read(ref _lastNetDriftRaw);
        public long ReconciledCount   => Interlocked.Read(ref _reconciledCount);
        public long ReconcileFreezes  => Interlocked.Read(ref _reconcileFreezes);
        public long RttSamples        => Interlocked.Read(ref _rttSamples);

        // ── RTT 단위 정규화(ticks → µs) ────────────────────────────────────────
        private static long ToMicros(long rttTicks, long tickFrequency)
        {
            if (rttTicks < 0) rttTicks = 0;
            if (tickFrequency <= 0) return rttTicks;   // 주파수 미상 — 원값(호출부가 이미 µs 로 준 경우)
            // ★Int128 중간·오버플로 clamp → 병리적(초대형) RTT 도 음수로 wrap 하지 않고 long.MaxValue 로 수렴(단측 3σ trip·fail-closed).
            Int128 micros = (Int128)rttTicks * 1_000_000L / tickFrequency;
            return micros > long.MaxValue ? long.MaxValue : (long)micros;
        }

        /// <summary>섀도우 단계 RTT 베이스라인 워밍업(컷오버 전). ★실측과 같은 단위(µs)로 정규화해 주입.</summary>
        public void SeedBaselineRtt(long rttTicks, long tickFrequency)
            => _cutover.SeedCanaryBaseline(ToMicros(rttTicks, tickFrequency));

        /// <summary>컷오버 후 실 브로커 RTT 관측 → µs 정규화 → 자율 카나리 검역(단측 3σ). 반환=카나리 트립(되감기) 여부.</summary>
        public bool ObserveBrokerRtt(long rttTicks, long tickFrequency)
        {
            Interlocked.Increment(ref _rttSamples);
            return _cutover.ObserveCanaryRtt(ToMicros(rttTicks, tickFrequency));
        }

        /// <summary>
        /// 최근 확정 체결의 broker-reported net 정산금액을 백엔드 계산치(LastFillNetRaw)와 1raw 대조.
        /// ★회계 확정(FillsFinalized 증가) 이후에만 유효 — 미확정이면 대조 보류(false 반환·동결 없음). |drift|>tol → 경성 동결.
        /// </summary>
        /// <returns>true=대조 수행(일치 또는 동결), false=아직 미확정(보류).</returns>
        public bool TryReconcileLatestFill(long brokerNetRaw)
        {
            // ★seqlock 일관 독출 — (seq,net) 를 torn 없이 원자 페어로 취득. 미확정(seq==0)/경합 지속이면 보류.
            if (!_mesh.TryReadLatestNet(out long meshNet, out _, out _, out long seq)) return false;
            if (Interlocked.Read(ref _lastSeqSeen) == seq) return false;       // 이 확정 건은 이미 대조함(중복 방지·seq 키)

            long drift = brokerNetRaw - meshNet;
            Interlocked.Exchange(ref _lastNetDriftRaw, drift);
            Interlocked.Exchange(ref _lastSeqSeen, seq);
            Interlocked.Increment(ref _reconciledCount);
            try { _blackbox?.Record(FlightEventKind.CanaryReconcile, symbolCode: 0L, arg0: meshNet, arg1: brokerNetRaw, arg2: drift, sourceCode: 51); } catch { }

            // 1raw 대조는 AtomicCutoverGate.ReconcileDualTrack 재사용(|drift|>tol → EnterShadow+경성 ForceFreeze). seq 와 net 이 동일 체결로 보증됨.
            bool frozen = _cutover.ReconcileDualTrack(meshNet, brokerNetRaw);
            if (frozen) Interlocked.Increment(ref _reconcileFreezes);
            return true;
        }
    }
}
