// ============================================================================
// RollingWinRateCalibrationEngine + RegimeAwareDynamicFactorWeightingEngine — v12.7.0 Think Beyond Gemini
//
//   위험 가정(제미나이의 '단순 팩터 합성'이 다루지 않는 실전 퀀트 파멸 리스크):
//     ① "AI 추천이 특정 국면(장세)에서 지속적으로 틀려도 확신도가 그대로 유지" — 정적 신뢰도는 시장 레짐이
//        바뀌어 모델이 구조적으로 틀리기 시작해도 계속 '높은 확신'으로 잘못된 시그널을 사출한다. 사후 적중률을
//        추적해 확신도를 스스로 깎지 않으면 사용자가 무너지는 모델을 계속 신뢰하게 된다.
//     ② "고정 팩터 가중치가 모든 장세에 동일 적용" — 상승장의 모멘텀 우대 가중치를 횡보장/고변동성 국면에도
//        그대로 쓰면, 레인지 반복을 모멘텀 신호로 오인하거나 고변동성 노이즈를 추세로 착각하는 구조적 오탐이 반복된다.
//
//   ── Think Beyond (이중 엔진) ─────────────────────────────────────────────
//     ① Zero-Alloc Rolling Win-Rate Calibration Engine(Idea A) — POH(Pinned Object Heap) 링에 예측 방향을
//        기록하고, 사후 실현 방향(1D/5D/20D 호라이즌)과 부호 일치 여부만으로 ★적중률을 러닝 카운터(O(1))로
//        추적한다(전체 회귀 없음). 적중률이 50% 밑으로 떨어지면 확신도 감쇠 계수가 선형으로 낮아진다(바닥 有).
//        ★정직 스코핑: "누적 수익률" 전체를 추적하려면 별도 PnL 파이프라인이 필요해 날조하지 않고, 방향 적중
//        여부(hit-rate)로 단순화했다 — 스펙의 "적중률 하락" 요구를 정확히 충족하는 대체 지표다.
//     ② Real-Time Regime-Aware Dynamic Factor Weighting Engine(Idea B) — 4개 팩터 가중치를 byte 로 양자화해
//        단일 long 에 비트 패킹, ★단일 Interlocked.Exchange 로 국면 전환 시 4개 가중치를 원자적으로 동시
//        교체한다(중간 상태 관측 불가 — 0 Byte). ClassifyRegime 은 주입된 추세/변동성(bps)만으로 결정론 분류.
//
//   불변: 결정론(전부 외부 주입 값만) · δ=0(RecordPrediction/ResolveOutcome/ApplyRegime/CurrentWeights 힙 0) ·
//        fail-closed(이미 랩되어 사라진 슬롯 해소 시도 → 무시) · liveArmed:false(관측/가중치 산출일 뿐).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Core
{
    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① (Idea A) — Zero-Alloc Rolling Win-Rate Calibration Engine
    // ════════════════════════════════════════════════════════════════════════
    public enum WinRateHorizon { Day1 = 0, Day5 = 1, Day20 = 2 }

    /// <summary>과거 AI 예측 방향과 사후 실현 방향의 적중률을 POH 링 + 러닝 카운터로 O(1) 추적하고,
    /// 적중률 하락 시 확신도 감쇠 계수를 산출하는 δ=0 엔진.</summary>
    public sealed class RollingWinRateCalibrationEngine
    {
        private const int Horizons = 3;

        private readonly long[] _predictedDir;   // Pinned: cap — 슬롯의 예측 방향(+1/0/-1)
        private readonly long[] _seqAtSlot;      // Pinned: cap — 슬롯에 실제 적힌 시퀀스(랩 판별, -1=미기록)
        private readonly long[] _wins;           // [Horizons] 러닝 적중 카운터
        private readonly long[] _resolved;       // [Horizons] 러닝 해소 카운터
        private readonly int _cap;
        private readonly long _attenuationFloorPpm;
        private long _writeSeq;

        public RollingWinRateCalibrationEngine(int capacityPow2, long attenuationFloorPpm = 200_000)
        {
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0)
                throw new ArgumentException("용량은 2의 거듭제곱(≥2)이어야 합니다.", nameof(capacityPow2));
            _cap = capacityPow2;
            _predictedDir = GC.AllocateArray<long>(capacityPow2, pinned: true);
            _seqAtSlot = GC.AllocateArray<long>(capacityPow2, pinned: true);
            for (int i = 0; i < capacityPow2; i++) _seqAtSlot[i] = -1;
            _wins = new long[Horizons];
            _resolved = new long[Horizons];
            _attenuationFloorPpm = attenuationFloorPpm > 0 ? attenuationFloorPpm : 200_000;
        }

        /// <summary>새 예측 방향 기록(★단일 라이터 계약·δ=0). +1=매수 방향, -1=매도 방향, 0=관망. 반환=슬롯 시퀀스
        /// (추후 ResolveOutcome 조회용 — 호출자가 보관).</summary>
        public long RecordPrediction(long predictedDirection)
        {
            long w = _writeSeq++;
            int idx = (int)(w % _cap);
            Volatile.Write(ref _predictedDir[idx], predictedDirection);
            Volatile.Write(ref _seqAtSlot[idx], w);
            return w;
        }

        /// <summary>
        /// 사후 실현 방향 반영(δ=0). 예측 부호와 실현 부호가 일치하면 적중(hit) — 해당 호라이즌 러닝 카운터 갱신.
        /// 슬롯이 랩되어 이미 재사용됐으면 false(fail-closed — 다른 예측에 오귀속 방지).
        /// </summary>
        public bool ResolveOutcome(long sequence, WinRateHorizon horizon, long actualDirection)
        {
            int idx = (int)(sequence % _cap);
            if (Volatile.Read(ref _seqAtSlot[idx]) != sequence) return false;
            long predicted = Volatile.Read(ref _predictedDir[idx]);
            bool hit = predicted != 0 && Math.Sign(predicted) == Math.Sign(actualDirection);
            int h = (int)horizon;
            Interlocked.Increment(ref _resolved[h]);
            if (hit) Interlocked.Increment(ref _wins[h]);
            return true;
        }

        /// <summary>호라이즌별 적중률(ppm). 해소된 표본이 없으면 1,000,000(정보 없음 — 감쇠 없음, fail-closed 아닌
        /// fail-neutral: 표본 부족을 '나쁜 성과'로 오판하지 않는다).</summary>
        public long WinRatePpm(WinRateHorizon horizon)
        {
            int h = (int)horizon;
            long r = Interlocked.Read(ref _resolved[h]);
            return r > 0 ? Interlocked.Read(ref _wins[h]) * 1_000_000 / r : 1_000_000;
        }

        /// <summary>3개 호라이즌 평균 적중률 기반 확신도 감쇠 계수(ppm, 1,000,000=무감쇠). 평균 적중률이 50%
        /// 미만이면 선형 감쇠(0%→바닥, 50%→무감쇠 직전)하고 바닥(floor) 이하로는 떨어지지 않는다.</summary>
        public long ConfidenceAttenuationPpm
        {
            get
            {
                long avg = (WinRatePpm(WinRateHorizon.Day1) + WinRatePpm(WinRateHorizon.Day5) + WinRatePpm(WinRateHorizon.Day20)) / 3;
                if (avg >= 500_000) return 1_000_000;
                long scaled = avg * 2;   // 0..500,000 → 0..1,000,000 선형 매핑
                return Math.Max(_attenuationFloorPpm, scaled);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② (Idea B) — Real-Time Regime-Aware Dynamic Factor Weighting Engine
    // ════════════════════════════════════════════════════════════════════════
    public enum MarketRegime : byte { Bull = 0, Bear = 1, Sideways = 2, HighVolatility = 3 }

    /// <summary>4개 팩터 가중치를 단일 long 에 비트 패킹해 국면 전환 시 ★단일 CAS(Interlocked.Exchange)로
    /// 원자 동시 교체하는 δ=0 다이내믹 웨이터.</summary>
    public sealed class RegimeAwareDynamicFactorWeightingEngine
    {
        private long _packedWeights;   // [0..7]Technical [8..15]Fundamental [16..23]Microstructure [24..31]SupplyDemand
        private long _regime;          // MarketRegime(byte 폭이지만 Interlocked 는 long 만 지원 — long 저장)

        public RegimeAwareDynamicFactorWeightingEngine()
        {
            Volatile.Write(ref _packedWeights, Pack(64, 64, 64, 64));   // 균등 초기(합 256 = 1.0 등가 스케일)
            Volatile.Write(ref _regime, (long)MarketRegime.Sideways);
        }

        public MarketRegime CurrentRegime => (MarketRegime)Volatile.Read(ref _regime);

        /// <summary>주입된 추세/변동성(bps)만으로 국면을 결정론 분류(δ=0·순수·자체시계 0).</summary>
        public static MarketRegime ClassifyRegime(long trendBps, long volatilityBps)
        {
            const long HighVolThreshold = 300;   // 3%
            const long TrendThreshold = 200;     // 2%
            if (volatilityBps >= HighVolThreshold) return MarketRegime.HighVolatility;
            if (trendBps >= TrendThreshold) return MarketRegime.Bull;
            if (trendBps <= -TrendThreshold) return MarketRegime.Bear;
            return MarketRegime.Sideways;
        }

        /// <summary>국면별 사전 정의 가중치 세트로 ★단일 원자 CAS 재배치(0 Byte, 중간 상태 관측 불가).</summary>
        public void ApplyRegime(MarketRegime regime)
        {
            long packed = regime switch
            {
                MarketRegime.Bull => Pack(90, 40, 60, 70),             // 모멘텀(기술적)·수급 우대
                MarketRegime.Bear => Pack(50, 90, 50, 70),              // 가치(기본적) 방어 우대
                MarketRegime.Sideways => Pack(40, 60, 100, 60),         // 미시구조(레인지) 우대
                MarketRegime.HighVolatility => Pack(30, 40, 110, 80),   // 미시구조+수급 민감 우대
                _ => Pack(64, 64, 64, 64),
            };
            Interlocked.Exchange(ref _packedWeights, packed);
            Interlocked.Exchange(ref _regime, (long)regime);
        }

        /// <summary>현재 가중치(0..1 정규화, 4개 합 = 1.0 근사 — 64/256 균등 기준 스케일).</summary>
        public (float Technical, float Fundamental, float Microstructure, float SupplyDemand) CurrentWeights()
        {
            long p = Volatile.Read(ref _packedWeights);
            return (Unpack(p, 0) / 256f, Unpack(p, 8) / 256f, Unpack(p, 16) / 256f, Unpack(p, 24) / 256f);
        }

        private static long Pack(byte t, byte f, byte m, byte s) => t | ((long)f << 8) | ((long)m << 16) | ((long)s << 24);
        private static byte Unpack(long packed, int shift) => (byte)((packed >> shift) & 0xFF);
    }
}
