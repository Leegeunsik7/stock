// ============================================================================
// CrossStrategyContagionFirewall — v5.4.0 ★Think Beyond Gemini: 교차 전략 전염 방화벽 & 자본 구획 격벽
//
//  ── 아키텍처 논증(제미나이 초월) ────────────────────────────────────────────
//  제미나이 A(공진 서킷 브레이커)는 상관관계 분석으로 공진을 '반응형' 탐지하고, B(자본 약탈 방지)는 약탈을 '반응형'으로
//  탐지해 ALC 를 언로드한다. 두 아이디어 모두 (1) 부동소수 상관/이력 버퍼(비결정·힙)에 의존하거나 (2) 사후 반응이다.
//  본 설계는 다중 격리 어셈블리가 단일 원장을 공유하는 HFT 환경의 파멸 리스크 '교차 전략 리스크 캐스케이드'를 ★세 층으로
//  '구조적으로' 봉쇄한다 — 반응이 아니라 물리 격벽:
//   ① 자본 구획 격벽(Capital Bulkhead): 봉인된 전략의 자본은 다른 전략에 ★재분배하지 않고 격리 예치(Quarantine)한다.
//      이것만으로 '건강한 전략이 죽은 전략의 리스크 식욕을 상속'하는 캐스케이드가 구조적으로 불가능해진다(B 의 반응형 탐지 초월).
//   ② δ=0 결정론적 공동낙폭 비트셋 공진 탐지(Co-Drawdown Bitset Resonance): 전략별 손실-에폭 비트셋(ulong 64에폭)의
//      popcount(loss[i] & loss[j]) 로 '독립이어야 할' 두 전략의 동기 낙폭을 하드웨어 POPCNT 1회로 포착. 부동소수 상관·
//      이력 배열이 전무 — 정수 비트연산이라 완전 결정론·힙 0(A 의 부동소수 상관 초월). 공진 포착 시 주도(대형 익스포저) 전략 봉인.
//   ③ 손실 속도 퓨즈(Loss-Velocity Fuse): 단일 전략의 연속 낙폭 에폭이 임계 초과 = 자기 구획 자폭 → 도미노 전 즉시 봉인.
//  ▷ 시스템 전역 캐스케이드(다수 구획 동시 봉인)만 IgnitionLock 백스톱(전역·최후)으로 에스컬레이트 — 개별 봉인은 per-lane 로컬.
//
//  δ=0: MarkResult(핫·비트 OR + 익스포저 저장)·Evaluate(콜드-ish·쌍별 popcount·배열 순회) 모두 힙 0. AdvanceEpoch 는 에폭 콜드.
//  결정론: 손실 판정은 정수 pnlΔ 부호, 공진은 정수 popcount, 에폭은 주입 시계. 벽시계/RNG/부동소수 상관 전무.
// ============================================================================
using System;
using System.Numerics;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>방화벽 평가 판정(blittable) — 신규 봉인 레인 비트마스크 + 사유 + 공진 메트릭 + 전역 캐스케이드 여부.</summary>
    public readonly struct FirewallVerdict
    {
        public readonly ulong SealLaneMask;    // 이번 평가에서 새로 봉인 결정된 레인 비트마스크(mesh 가 적용)
        public readonly int   Reason;          // 0=없음, 1=손실속도 퓨즈, 2=공진, 3=캐스케이드
        public readonly long  ResonanceMetric; // 최대 쌍 공동낙폭 popcount(위험 메트릭·텔레메트리)
        public readonly bool  SystemCascade;   // 다수 구획 동시 봉인 → 전역 IgnitionLock 백스톱 격발됨

        public FirewallVerdict(ulong sealMask, int reason, long resonance, bool cascade)
        { SealLaneMask = sealMask; Reason = reason; ResonanceMetric = resonance; SystemCascade = cascade; }
    }

    /// <summary>교차 전략 전염 방화벽 — 자본 구획 격벽 + δ=0 공동낙폭 비트셋 공진 + 손실속도 퓨즈 + 전역 캐스케이드 백스톱.</summary>
    public sealed class CrossStrategyContagionFirewall
    {
        private readonly int  _capacity;            // 레인 수(≤64 — ulong 마스크 도메인)
        private readonly int  _resonanceThreshold;  // 쌍 공동낙폭 popcount 임계(공진)
        private readonly int  _velocityFuse;        // 연속 낙폭 에폭 임계(자폭)
        private readonly int  _cascadeSeals;         // 동시 봉인 수 임계(전역 캐스케이드)
        private readonly IgnitionLock?      _ignition;
        private readonly MmfFlightRecorder? _blackbox;
        private readonly object _gate = new();      // 에폭 전진·평가 직렬화(콜드·무할당 lock)

        private readonly ulong[] _lossBitset;       // 전략별 손실-에폭 비트셋(bit0=최신 에폭)
        private readonly int[]   _pendingLoss;      // 현재 에폭 손실 발생(0/1)
        private readonly int[]   _consecLoss;       // 연속 낙폭 에폭 수(속도 퓨즈)
        private readonly long[]  _exposureRaw;      // 전략별 익스포저 명목(공진 시 주도 전략 판별)
        private ulong _sealedMask;                  // 누적 봉인 레인(재봉인·공진 대상 제외)
        private bool  _cascadeLatched;              // 전역 캐스케이드 백스톱 1회 래치(_gate 하) — 매 에폭 재발화 방지
        private long  _resonanceMetric, _sealCount, _cascades, _epochs;

        public CrossStrategyContagionFirewall(int capacity, int resonanceThreshold = 6, int velocityFuse = 8,
                                              int cascadeSeals = 3, IgnitionLock? ignition = null, MmfFlightRecorder? blackbox = null)
        {
            _capacity = capacity < 1 ? 1 : (capacity > 64 ? 64 : capacity);
            _resonanceThreshold = resonanceThreshold < 1 ? 1 : resonanceThreshold;
            _velocityFuse = velocityFuse < 1 ? 1 : velocityFuse;
            _cascadeSeals = cascadeSeals < 1 ? 1 : cascadeSeals;
            _ignition = ignition; _blackbox = blackbox;
            _lossBitset  = new ulong[_capacity];
            _pendingLoss = new int[_capacity];
            _consecLoss  = new int[_capacity];
            _exposureRaw = new long[_capacity];
        }

        public long ResonanceMetric => Interlocked.Read(ref _resonanceMetric);
        public long SealCount       => Interlocked.Read(ref _sealCount);   // 봉인은 해제 없음 → 현재 봉인 구획 수와 동일
        public long Cascades        => Interlocked.Read(ref _cascades);
        public long Epochs          => Interlocked.Read(ref _epochs);

        /// <summary>★핫패스(δ=0) — 전략의 이번 에폭 결과 관측. 순손실이면 현 에폭 손실 마크, 익스포저 갱신(비트 OR·저장뿐·힙 0).</summary>
        public void MarkResult(int lane, long pnlDeltaRaw, long exposureNotionalRaw)
        {
            if ((uint)lane >= (uint)_capacity) return;
            if (pnlDeltaRaw < 0) Volatile.Write(ref _pendingLoss[lane], 1);
            Volatile.Write(ref _exposureRaw[lane], exposureNotionalRaw < 0 ? -exposureNotionalRaw : exposureNotionalRaw);
        }

        /// <summary>에폭 전진(콜드) — 전 전략 비트셋 좌시프트 + 현 에폭 손실 폴드 + 연속낙폭 갱신. 주입 에폭 케이던스로 구동.</summary>
        public void AdvanceEpoch()
        {
            lock (_gate)
            {
                for (int i = 0; i < _capacity; i++)
                {
                    // ★적대검증 교정 — Interlocked.Exchange 로 원자 read-and-clear. MarkResult(락 밖 Volatile.Write)의 손실 마크가
                    //   read 와 reset 사이에 유실(lost update)되던 것을 봉쇄: write 는 이 Exchange 전(포착) 또는 후(다음 에폭 포착).
                    bool loss = Interlocked.Exchange(ref _pendingLoss[i], 0) != 0;
                    _lossBitset[i] = (_lossBitset[i] << 1) | (loss ? 1UL : 0UL);   // bit0 = 이번 에폭
                    if (loss) _consecLoss[i]++; else _consecLoss[i] = 0;
                }
                Interlocked.Increment(ref _epochs);
            }
        }

        /// <summary>
        /// ★방화벽 평가 — 손실속도 퓨즈(연속 낙폭)·공동낙폭 비트셋 공진(popcount)·전역 캐스케이드를 판정.
        /// 신규 봉인 레인 마스크를 반환(mesh 가 봉인·자본 격리 적용). 캐스케이드면 IgnitionLock 백스톱 격발.
        /// </summary>
        public FirewallVerdict Evaluate()
        {
            lock (_gate)
            {
                ulong newSeals = 0UL;
                int reason = 0;
                long maxRes = 0;

                // ① 손실 속도 퓨즈 — 연속 낙폭 에폭 ≥ 임계 = 자기 구획 자폭. 즉시 봉인 대상.
                for (int i = 0; i < _capacity; i++)
                {
                    if ((_sealedMask & (1UL << i)) != 0) continue;   // 이미 봉인
                    if (_consecLoss[i] >= _velocityFuse) { newSeals |= (1UL << i); if (reason == 0) reason = 1; }
                }

                // ② 공동낙폭 비트셋 공진 — 쌍별 popcount(loss[i]&loss[j]) 최대. 임계 초과 시 주도(대형 익스포저) 전략 봉인.
                for (int i = 0; i < _capacity; i++)
                {
                    if ((_sealedMask & (1UL << i)) != 0 || (newSeals & (1UL << i)) != 0) continue;
                    for (int j = i + 1; j < _capacity; j++)
                    {
                        if ((_sealedMask & (1UL << j)) != 0 || (newSeals & (1UL << j)) != 0) continue;
                        long res = BitOperations.PopCount(_lossBitset[i] & _lossBitset[j]);   // δ=0 하드웨어 POPCNT
                        if (res > maxRes) maxRes = res;
                        if (res >= _resonanceThreshold)
                        {
                            // 주도 전략(대형 익스포저)을 봉인 — 공진 진원 진압. 동률이면 낮은 인덱스(결정론).
                            int lead = _exposureRaw[i] >= _exposureRaw[j] ? i : j;
                            newSeals |= (1UL << lead);
                            if (reason != 1) reason = 2;
                        }
                    }
                }

                Interlocked.Exchange(ref _resonanceMetric, maxRes);

                if (newSeals != 0UL)
                {
                    _sealedMask |= newSeals;
                    int added = BitOperations.PopCount(newSeals);
                    Interlocked.Add(ref _sealCount, added);
                    try { _blackbox?.Record(FlightEventKind.RiskCascade, (long)newSeals, reason, maxRes, added, 86); } catch { }
                }

                // ③ 전역 캐스케이드 — 누적 봉인 구획 수 ≥ 임계 = 시스템 파멸 징후. IgnitionLock 전역 백스톱(최후·graded).
                //   ★적대검증 교정 — 1회 래치: Downgrade·Cascades 증가는 캐스케이드 최초 진입 시 1회만(매 에폭 재발화·재강등으로
                //   운영자 재점화 불가·카운터 인플레 방지). SystemCascade 판정은 현 상태를 매번 보고(UI 오버레이 유지).
                bool cascade = BitOperations.PopCount(_sealedMask) >= _cascadeSeals;
                if (cascade)
                {
                    reason = 3;
                    if (!_cascadeLatched)
                    {
                        _cascadeLatched = true;
                        Interlocked.Increment(ref _cascades);
                        try { _ignition?.Downgrade($"ContagionFirewall: 교차 전략 캐스케이드 — {BitOperations.PopCount(_sealedMask)}개 구획 봉인·전역 강등"); } catch { }
                    }
                }
                return new FirewallVerdict(newSeals, reason, maxRes, cascade);
            }
        }
    }
}
