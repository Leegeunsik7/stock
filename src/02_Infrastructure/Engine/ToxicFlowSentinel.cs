// ============================================================================
// ToxicFlowSentinel — v5.2.0 ★Think Beyond Gemini: 독약 유동성 감지 & 실시간 주문 큐 차단막
//
//  ── 아키텍처 논증(제미나이 초월) ────────────────────────────────────────────
//  제미나이 힌트 A(호가 비대칭 가속도 3σ)와 B(유령 유동성=스프레드/RTT)는 각각 단일 신호다. 본 설계는 셋을 하나의
//  '합성 3등급' 방어막으로 융합·초월한다: ① 비대칭 가속도(|Δimbalance|) 단측 3σ(독성 흐름 Adverse Selection 유입 징후),
//  ② 유령 유동성(스프레드가 EWMA 베이스라인 대비 비이성적 확대 ∨ 호가 갱신 RTT 급증 = 호가 장막 사기), ③ ★북워크 독성
//  베토(ToxicityAdjustedFillable): 슬라이서가 '가시 잔량'을 그대로 믿지 않고 독성 등급으로 할인(Clear 100%·Caution 50%·
//  Toxic 0%)해 실제 집행 가능량을 산출. 단순 '탐지 후 정지'가 아니라 '집행량을 독성에 비례해 자율 축소'하는 것이 핵심 초월점.
//  등급별 물리 결선: Toxic→IgnitionLock.Downgrade(강등, 영구락업 아님·역할분리) + 슬리피지 모델 공격적 보수화, Caution→
//  슬리피지 완만 보수화 + 슬라이스 절반 축소, Clear→통상.
//
//  ── fail-closed 맹점 방어 ───────────────────────────────────────────────────
//  3σ 는 워밍업/퇴화 분산 시 미발화(SlippageSentinel 관례)라 방어 해제 맹점이 생길 수 있어, ★하드 임계(|imbalance|>90%
//  단측)를 3σ 와 독립으로 병치 — 워밍업 중에도 극단 비대칭은 즉시 Caution 이상으로 잡는다.
//
//  δ=0: Ingest 핫 연산 = 고정 long[128] 링 + EWMA 정수 + Interlocked(힙 0). Downgrade/재조정은 콜드(등급 전이 시). 시계 주입.
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    public enum ToxicityVerdict : byte { Clear = 0, Caution = 1, Toxic = 2 }

    /// <summary>호가 비대칭 가속도 3σ + 유령 유동성 + 북워크 독성 베토 합성 3등급 미시 방어. Toxic→Downgrade·Caution→축소.</summary>
    public sealed class ToxicFlowSentinel
    {
        private const int Capacity = 128;
        private const int MinSamples = 10;
        private const long HardImbalanceRaw = 90_000_000L;   // |imbalance|>0.9×Scale(90% 단측) = 하드 임계(워밍업 맹점 방어)

        private readonly IgnitionLock?      _ignition;
        private readonly Action<int>?       _rescaleSlippage;   // 등급(1 Caution·2 Toxic) 전달 → 슬리피지 보수화(주입)
        private readonly MmfFlightRecorder? _blackbox;
        private readonly long               _rttDeadlineTicks;  // 호가 갱신 RTT 유령 임계(0=미사용)
        private readonly long               _spreadWidenFactor; // 스프레드 확대 배수(기본 3배)

        // δ=0 비대칭 가속도 단측 3σ 링(단일 라이터=Ingest).
        private readonly long[] _vel = new long[Capacity];
        private int   _idx, _count;
        private long  _sum;
        private Int128 _sumSq;
        private long  _lastImbalance;
        private long  _spreadEwma;   // 스프레드 EWMA 베이스라인(정수)

        private int  _verdict = (int)ToxicityVerdict.Clear;
        private long _toxicTrips, _cautionCount, _lastVelocityRaw, _lastSpreadRaw;

        public ToxicFlowSentinel(IgnitionLock? ignition = null, Action<int>? rescaleSlippage = null,
                                 long rttDeadlineTicks = 0, long spreadWidenFactor = 3, MmfFlightRecorder? blackbox = null)
        {
            _ignition = ignition; _rescaleSlippage = rescaleSlippage; _blackbox = blackbox;
            _rttDeadlineTicks = rttDeadlineTicks < 0 ? 0 : rttDeadlineTicks;
            _spreadWidenFactor = spreadWidenFactor < 2 ? 2 : spreadWidenFactor;
        }

        public ToxicityVerdict Verdict => (ToxicityVerdict)Volatile.Read(ref _verdict);
        public long ToxicTrips        => Interlocked.Read(ref _toxicTrips);
        public long CautionCount      => Interlocked.Read(ref _cautionCount);
        public long LastVelocityRaw   => Interlocked.Read(ref _lastVelocityRaw);
        public long LastSpreadRaw     => Interlocked.Read(ref _lastSpreadRaw);

        /// <summary>독성 등급으로 가시 잔량을 할인한 실제 집행 가능량(북워크 베토). Clear=100%·Caution=50%·Toxic=0.</summary>
        public long ToxicityAdjustedFillable(long visibleFillableRaw)
        {
            if (visibleFillableRaw <= 0) return 0;
            return Verdict switch
            {
                ToxicityVerdict.Toxic   => 0,
                ToxicityVerdict.Caution => visibleFillableRaw / 2,
                _                       => visibleFillableRaw,
            };
        }

        /// <summary>
        /// L2 depth 인입 시 독성 평가(δ=0 핫). 비대칭 가속도 단측 3σ + 스프레드 EWMA 확대 + RTT 유령 + 하드 임계 합성 → 3등급.
        /// Toxic 전이 시 Downgrade + 공격적 슬리피지 보수화, Caution 시 완만 보수화. 반환=현재 등급.
        /// </summary>
        public ToxicityVerdict Ingest(in L2Depth d, long imbalanceRaw, long quoteRttTicks)
        {
            // ① 비대칭 가속도 = |Δimbalance| 단측 3σ.
            long vel = Math.Abs(imbalanceRaw - _lastImbalance);
            _lastImbalance = imbalanceRaw;
            Interlocked.Exchange(ref _lastVelocityRaw, vel);
            bool velTrip = ExceedsThreeSigmaUp(vel);
            Push(vel);

            // ② 유령 유동성 — 스프레드(best) 확대 or 호가 RTT 급증.
            long bestBid = d.BidPriceRaw[0], bestAsk = d.AskPriceRaw[0];
            long spread = (bestAsk > 0 && bestBid > 0 && bestAsk >= bestBid) ? bestAsk - bestBid : 0;
            Interlocked.Exchange(ref _lastSpreadRaw, spread);
            bool spreadWiden = _spreadEwma > 0 && spread > _spreadEwma * _spreadWidenFactor;
            if (spread > 0) _spreadEwma = _spreadEwma <= 0 ? spread : (_spreadEwma * 15 + spread) / 16;   // EWMA(1/16)
            bool rttPhantom = _rttDeadlineTicks > 0 && quoteRttTicks > _rttDeadlineTicks;
            bool phantom = spreadWiden || rttPhantom;

            // ③ 하드 임계(워밍업 맹점 방어) + 합성 등급.
            bool hardImbalance = Math.Abs(imbalanceRaw) > HardImbalanceRaw;
            ToxicityVerdict v;
            if (velTrip || (hardImbalance && phantom)) v = ToxicityVerdict.Toxic;
            else if (hardImbalance || phantom)          v = ToxicityVerdict.Caution;
            else                                        v = ToxicityVerdict.Clear;

            int prev = Interlocked.Exchange(ref _verdict, (int)v);
            if (v == ToxicityVerdict.Toxic && prev != (int)ToxicityVerdict.Toxic)
            {
                Interlocked.Increment(ref _toxicTrips);
                try { _blackbox?.Record(FlightEventKind.ToxicFlowHold, d.SymbolCode, imbalanceRaw, spread, vel, 72); } catch { }
                try { _ignition?.Downgrade($"ToxicFlowSentinel Toxic — 비대칭 가속도/유령 유동성 감지(imb={imbalanceRaw} vel={vel} spread={spread})"); } catch { }
                try { _rescaleSlippage?.Invoke(2); } catch { }   // 공격적 보수화
            }
            else if (v == ToxicityVerdict.Caution && prev == (int)ToxicityVerdict.Clear)
            {
                Interlocked.Increment(ref _cautionCount);
                try { _rescaleSlippage?.Invoke(1); } catch { }   // 완만 보수화
            }
            return v;
        }

        private bool ExceedsThreeSigmaUp(long vel)
        {
            int n = _count;
            if (n < MinSamples) return false;
            long mean = _sum / n;
            long dev = vel - mean;
            if (dev <= 0) return false;   // 단측 — 급증만
            Int128 var = _sumSq / n - (Int128)mean * mean;
            if (var <= 0) return false;
            return (Int128)dev * dev > (Int128)9 * var;
        }

        private void Push(long vel)
        {
            if (_count == Capacity) { long old = _vel[_idx]; _sum -= old; _sumSq -= (Int128)old * old; }
            else _count++;
            _vel[_idx] = vel; _sum += vel; _sumSq += (Int128)vel * vel;
            _idx = (_idx + 1) % Capacity;
        }
    }
}
