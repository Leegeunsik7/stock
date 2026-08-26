// ============================================================================
// MultiStrategyInterleavedMesh — v5.4.0 과제1: 무할당 다중 전략 인터리빙 메쉬 Core
//
//  격리된 복수 알파 두뇌(Strategy A·B·C…)를 단일 프로세스에 안전 공존시키고, 단일 실시간 시세 스트림(OnQuote)을 힙 할당
//  없이(δ=0) 락프리 팬아웃 라우팅한다. 전역 예수금 한도를 전략별 정수 가중치(Weight) 비율로 물리 분할 바인딩하고, 단일
//  전략이 자기 할당 한도를 1raw라도 초과 발주하지 못하도록 차단하는 무할당 자본 가드를 이식한다. 각 틱마다 팬아웃 →
//  바스켓 네팅(대향 상쇄) → 자전거래 가드 → 잔여만 하류 드레인 링 사출. 교차 전략 전염 방화벽이 봉인 판정을 적용한다.
//
//  ── 정직 회계(정찰) ─────────────────────────────────────────────────────────
//  IHotStrategy.OnQuote(v5.3.0)·OrderProposal.GenomeId(전략 라우팅 천연 키)·OrderDispatchRing.PublishRaw(단일생산자)·
//  StrategyStateCarrier(per-instance blittable 통계·레인당 1개)·PreTradeRiskShield 의 CAS 예약 관용구(private→재구현)·
//  LiveAccountingMesh.FloorChargeCarry(누수 0 가중치 분할 사상)를 재사용. 전략별 자본 물리 분할·팬아웃 홀더는 신설.
//
//  ── liveArmed:false / δ=0 / 결정론 ─────────────────────────────────────────
//  팬아웃·네팅·상쇄·잔여 사출은 전부 '하류 무장 게이트' 위의 관측/최적화 — 잔여도 GatedOrderDispatcher 무장∧이중키∧
//  MasterIgnited 하류라 미무장 시 실 송출 0바이트. ProcessTick: stackalloc 인텐트 버퍼(힙 0)·정수 산술·CAS 예약. 자본
//  분할은 정수 가중치(부동소수 금지)·주입 시각. 동결 시 전 틱 억제(fail-closed). ★단일 소비자(quote-pump) 계약.
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>레인 운영 상태(원자). Active=정상, Muted=신규 진입 억제(진압), Sealed=봉인(자본 격리 예치·유폐).</summary>
    public enum LaneState { Active = 0, Muted = 1, Sealed = 2 }

    /// <summary>전략 신호 의도(mesh 팬아웃 산물·blittable·δ=0). 자본 가드 클램프 후 값.</summary>
    public readonly struct StrategyIntent
    {
        public readonly int  LaneIndex;
        public readonly long GenomeId;
        public readonly long SymbolCode;
        public readonly long SignedQtyRaw;   // +매수/-매도/0
        public readonly long RefPriceRaw;

        public StrategyIntent(int laneIndex, long genomeId, long symbolCode, long signedQtyRaw, long refPriceRaw)
        { LaneIndex = laneIndex; GenomeId = genomeId; SymbolCode = symbolCode; SignedQtyRaw = signedQtyRaw; RefPriceRaw = refPriceRaw; }

        public long AbsQtyRaw => SignedQtyRaw >= 0 ? SignedQtyRaw : -SignedQtyRaw;
    }

    /// <summary>무할당 다중 전략 인터리빙 메쉬 — 팬아웃 공존·가중치 자본 물리 분할·네팅·자전거래·전염 방화벽 오케스트레이션.</summary>
    public sealed class MultiStrategyInterleavedMesh
    {
        /// <summary>전략 레인(per-instance 상태·클래스라 배열 슬롯 참조 δ=0). 자본 셀·익스포저·통계 캐리어.</summary>
        private sealed class Lane
        {
            public long GenomeId;
            public int  WeightBps;                 // 정수 가중치(비율 — 합이 10000일 필요 없음)
            public long SubBudgetRaw;              // 할당 명목 한도(가중치 분할 결과)
            public long ExposureRaw;               // 누적 순 익스포저 명목(가상 장부·부호·자본 소진 원장 |exp|≤SubBudget)
            public long QuarantinedRaw;            // 봉인 시 격리 예치 자본(재분배 금지)
            public IHotStrategy? Strategy;
            public StrategyStateCarrier? Carrier;  // per-lane 통계 이월(v5.3.0 재사용)
            public int  State;                     // LaneState
        }

        private readonly Lane[] _lanes;
        private readonly int    _capacity;
        private int  _laneCount;
        private long _globalBudgetRaw;
        private long _meshGenomeId;                // 잔여(순) 물리 주문 계보 태그(집계)
        private readonly object _cfgGate = new();  // 구성·재분할 직렬화(콜드)

        private readonly OrderDispatchRing?            _ring;
        private readonly OrderAttestationService?      _att;
        private readonly RealTimeBasketNetter?         _netter;
        private readonly WashTradeGuard?               _washGuard;
        private readonly CrossStrategyContagionFirewall? _firewall;
        private readonly MmfFlightRecorder?            _blackbox;

        private long _ticks, _residualsPublished, _capitalRejects, _fanoutSignals;

        public MultiStrategyInterleavedMesh(int capacity = 16, long meshGenomeId = 0,
                                            OrderDispatchRing? ring = null, OrderAttestationService? attestation = null,
                                            RealTimeBasketNetter? netter = null, WashTradeGuard? washGuard = null,
                                            CrossStrategyContagionFirewall? firewall = null, MmfFlightRecorder? blackbox = null)
        {
            _capacity = capacity < 1 ? 1 : (capacity > 64 ? 64 : capacity);
            _meshGenomeId = meshGenomeId;
            _ring = ring; _att = attestation; _netter = netter; _washGuard = washGuard; _firewall = firewall; _blackbox = blackbox;
            _lanes = new Lane[_capacity];
            for (int i = 0; i < _capacity; i++) _lanes[i] = new Lane();
        }

        public int  ActiveLaneCount     { get { int c = 0; for (int i = 0; i < _laneCount; i++) if (Volatile.Read(ref _lanes[i].State) == (int)LaneState.Active && Volatile.Read(ref _lanes[i].Strategy) is not null) c++; return c; } }
        public int  SealedLaneCount     { get { int c = 0; for (int i = 0; i < _laneCount; i++) if (Volatile.Read(ref _lanes[i].State) == (int)LaneState.Sealed) c++; return c; } }
        public int  MutedLaneCount      { get { int c = 0; for (int i = 0; i < _laneCount; i++) if (Volatile.Read(ref _lanes[i].State) == (int)LaneState.Muted) c++; return c; } }
        public long Ticks               => Interlocked.Read(ref _ticks);
        public long ResidualsPublished  => Interlocked.Read(ref _residualsPublished);
        public long CapitalRejects      => Interlocked.Read(ref _capitalRejects);
        public long FanoutSignals       => Interlocked.Read(ref _fanoutSignals);
        public long GlobalBudgetRaw     => Interlocked.Read(ref _globalBudgetRaw);

        // ── UI 다중 분할 바용 경량 per-lane 접근자(δ=0 읽기) ──
        public int      LaneCount            => _laneCount;
        public long     LaneSubBudgetRaw(int i) => (uint)i < (uint)_laneCount ? Volatile.Read(ref _lanes[i].SubBudgetRaw) : 0L;
        public long     LaneExposureRaw(int i)  => (uint)i < (uint)_laneCount ? Volatile.Read(ref _lanes[i].ExposureRaw) : 0L;
        public long     LaneGenomeId(int i)     => (uint)i < (uint)_laneCount ? _lanes[i].GenomeId : 0L;
        public int      LaneWeightBps(int i)     => (uint)i < (uint)_laneCount ? _lanes[i].WeightBps : 0;
        public LaneState LaneStateOf(int i)      => (uint)i < (uint)_laneCount ? (LaneState)Volatile.Read(ref _lanes[i].State) : LaneState.Sealed;
        public long     LaneSaturationBps(int i)
        {
            if ((uint)i >= (uint)_laneCount) return 0L;
            long b = Volatile.Read(ref _lanes[i].SubBudgetRaw);
            if (b <= 0) return 0L;
            long e = Volatile.Read(ref _lanes[i].ExposureRaw); if (e < 0) e = -e;   // |순 익스포저| = 소진 자본
            return e * 10000L / b;
        }

        /// <summary>전 레인 |순 익스포저|/할당 명목 총합 기준 자본 포화도 bps(콜드 읽기·텔레메트리).</summary>
        public long CapitalSaturationBps
        {
            get
            {
                long used = 0, budget = 0;
                for (int i = 0; i < _laneCount; i++)
                {
                    long e = Volatile.Read(ref _lanes[i].ExposureRaw); used += e < 0 ? -e : e;
                    budget += Volatile.Read(ref _lanes[i].SubBudgetRaw);
                }
                return budget <= 0 ? 0L : used * 10000L / budget;
            }
        }

        // ── 구성(콜드) ──────────────────────────────────────────────────────────

        /// <summary>레인 등록(콜드·시작 전). genomeId·정수 가중치·전략·통계 캐리어. 등록 후 Repartition 으로 자본 분할.</summary>
        public int AddLane(long genomeId, int weightBps, IHotStrategy? strategy, StrategyStateCarrier? carrier = null)
        {
            lock (_cfgGate)
            {
                if (_laneCount >= _capacity) return -1;
                int idx = _laneCount++;
                var L = _lanes[idx];
                L.GenomeId = genomeId; L.WeightBps = weightBps < 0 ? 0 : weightBps; L.Strategy = strategy;
                L.Carrier = carrier; L.State = (int)LaneState.Active; L.ExposureRaw = 0; L.QuarantinedRaw = 0;
                return idx;
            }
        }

        /// <summary>
        /// ★전역 예수금을 생존 전략 정수 가중치 비율로 물리 분할(누수 0). ★구획 격벽(적대검증 critical 교정): 봉인 구획의 격리
        /// 자본(Σ QuarantinedRaw)은 배분 풀에서 제외 — distributable = 전역 − 격리. 생존 레인은 격리분을 흡수하지 못하므로
        /// '건강한 전략이 죽은 전략의 리스크를 상속'하는 캐스케이드가 구조적으로 불가능. floor 분배 + 잔여 raw 를 최고 가중치 생존
        /// 레인에 결정론 귀속(Σ 생존 서브버짓 == distributable 정확 일치).
        /// </summary>
        public void Repartition(long globalBudgetRaw)
        {
            lock (_cfgGate)
            {
                Interlocked.Exchange(ref _globalBudgetRaw, globalBudgetRaw < 0 ? 0 : globalBudgetRaw);
                long totalWeight = 0, quarantined = 0;
                int maxWLane = -1; long maxW = -1;
                for (int i = 0; i < _laneCount; i++)
                {
                    quarantined += Volatile.Read(ref _lanes[i].QuarantinedRaw);   // 봉인 구획 격리 자본(재분배 금지)
                    if (Volatile.Read(ref _lanes[i].State) == (int)LaneState.Sealed) continue;   // 봉인 구획 제외
                    totalWeight += _lanes[i].WeightBps;
                    if (_lanes[i].WeightBps > maxW) { maxW = _lanes[i].WeightBps; maxWLane = i; }
                }
                long distributable = _globalBudgetRaw - quarantined;   // ★격리분 제외 — 격벽
                if (distributable < 0) distributable = 0;
                if (totalWeight <= 0 || maxWLane < 0) return;   // 분배 대상 없음
                long assigned = 0;
                for (int i = 0; i < _laneCount; i++)
                {
                    if (Volatile.Read(ref _lanes[i].State) == (int)LaneState.Sealed) { Volatile.Write(ref _lanes[i].SubBudgetRaw, 0); continue; }
                    Int128 prod = (Int128)distributable * _lanes[i].WeightBps;
                    long share = (long)(prod / totalWeight);   // floor
                    Volatile.Write(ref _lanes[i].SubBudgetRaw, share);
                    assigned += share;
                }
                long leftover = distributable - assigned;    // < totalWeight(≤ laneCount) — 결정론 귀속(최고 가중치·동률 최소 인덱스)
                if (leftover > 0) Volatile.Write(ref _lanes[maxWLane].SubBudgetRaw, Volatile.Read(ref _lanes[maxWLane].SubBudgetRaw) + leftover);
                try { _blackbox?.Record(FlightEventKind.CapitalResegment, distributable, totalWeight, _laneCount, 87); } catch { }
            }
        }

        // ── 핫패스(δ=0·단일 소비자) ───────────────────────────────────────────────

        /// <summary>
        /// ★단일 시세 틱 처리(δ=0) — 팬아웃(전 레인 OnQuote·자본 가드 클램프) → 바스켓 네팅(대향 상쇄) → 자전거래 가드 →
        /// 잔여만 드레인 링 사출. 반환=사출된 잔여 순수량(signed·0=전량 상쇄/거부). 동결 시 전 틱 억제(fail-closed).
        /// </summary>
        public long ProcessTick(long symbolCode, long priceRaw, long qtyRaw, long nowTicks)
        {
            Interlocked.Increment(ref _ticks);
            if (_att is { } a && a.Mode != GatingMode.Normal) return 0;   // 동결 — 전 신호 억제
            int lanes = _laneCount;
            if (lanes <= 0 || priceRaw <= 0) return 0;

            Span<StrategyIntent> intents = stackalloc StrategyIntent[lanes];
            int n = FanOut(symbolCode, priceRaw, qtyRaw, intents);
            if (n == 0) return 0;

            NetResult net = _netter is { } nt ? nt.Net(intents.Slice(0, n), priceRaw) : NetInline(intents.Slice(0, n));

            long residual = net.ResidualSignedQtyRaw;
            OrderSide side = residual >= 0 ? OrderSide.Buy : OrderSide.Sell;
            long absResidual = residual >= 0 ? residual : -residual;

            // ★게이트 판정 — 잔여가 자전거래 윈도우 레이스면 거부. 통과 시에만 잔여가 시장/가상 체결(residualFills).
            bool gatesPassed = residual != 0;
            if (gatesPassed && _washGuard is { } wg && !wg.Admit(symbolCode, side, nowTicks)) gatesPassed = false;

            // 잔여 물리 사출 — 단일생산자 링(하류 무장 게이트 지배·미무장 시 실 송출 0). 링 실패 시 미체결로 강등.
            bool emitted = false;
            if (gatesPassed && _ring is { } r)
            {
                try { r.PublishRaw(new OrderProposal(0L, _meshGenomeId, symbolCode, side, 1, new QPrice(priceRaw), new QQty(absResidual), nowTicks)); emitted = true; }
                catch { gatesPassed = false; }
            }

            // ★가상 장부 확정(익스포저 기반) — 상쇄분은 항상 내부 체결, 잔여분은 게이트 통과 시에만 체결. 예약 셀 없음 → 반납 누수 불가.
            BookFills(intents.Slice(0, n), net.CrossedQtyRaw, residualFills: gatesPassed, priceRaw);

            if (residual == 0)
            {
                try { _blackbox?.Record(FlightEventKind.BasketNetted, symbolCode, net.CrossedQtyRaw, 0, n, 83); } catch { }
                return 0;
            }
            if (!gatesPassed) return 0;   // 자전거래 거부/링 실패 — 잔여 미사출
            if (emitted)
            {
                Interlocked.Increment(ref _residualsPublished);
                try { _blackbox?.Record(FlightEventKind.StrategyFanout, symbolCode, residual, net.CrossedQtyRaw, n, 85); } catch { }
            }
            return residual;
        }

        /// <summary>δ=0 팬아웃 — 활성 레인만 OnQuote 위임 → 자본 가드 클램프 → 인텐트 버퍼. 봉인/진압 레인 스킵.</summary>
        private int FanOut(long symbolCode, long priceRaw, long qtyRaw, Span<StrategyIntent> buf)
        {
            int n = 0;
            for (int i = 0; i < _laneCount && n < buf.Length; i++)
            {
                var L = _lanes[i];
                if (Volatile.Read(ref L.State) != (int)LaneState.Active) continue;   // 진압/봉인 레인 신호 억제
                var strat = Volatile.Read(ref L.Strategy);
                if (strat is null) continue;
                long sig = strat.OnQuote(symbolCode, priceRaw, qtyRaw);
                if (sig == 0) continue;
                Interlocked.Increment(ref _fanoutSignals);
                long clamped = AdmitCapital(L, sig, priceRaw, symbolCode);
                if (clamped == 0) continue;   // 자본 초과 → fail-closed 거부
                buf[n++] = new StrategyIntent(i, L.GenomeId, symbolCode, clamped, priceRaw);
            }
            return n;
        }

        /// <summary>
        /// ★무할당 자본 가드(익스포저 기반·tentative·무변경) — 레인 순 익스포저가 자기 서브버짓을 1raw라도 초과하면 fail-closed 거부.
        /// 예약 셀 누적 없음(반납 누수 클래스 원천 차단): 익스포저 원장 하나가 누적 포지션 = 자본 소진을 표상. 반환=허용 signed(0=거부).
        /// </summary>
        private long AdmitCapital(Lane L, long signedSig, long priceRaw, long symbolCode)
        {
            long absQty = signedSig >= 0 ? signedSig : -signedSig;
            long notional;
            try { notional = QMath.Notional(absQty, priceRaw, roundUp: true); }   // 매수 비용 올림(보수)
            catch { return 0; }   // 오버플로/오염 → 거부
            long budget = Volatile.Read(ref L.SubBudgetRaw);
            if (notional > budget) { RejectCapital(L, notional, 1, symbolCode); return 0; }   // 단일 주문이 할당 초과
            long signedNotional = signedSig >= 0 ? notional : -notional;
            long proj = Volatile.Read(ref L.ExposureRaw) + signedNotional;   // 익스포저 ∈ [-budget,budget] 이라 |proj| ≤ 2·budget(무오버플로)
            if (proj > budget || proj < -budget) { RejectCapital(L, notional, 2, symbolCode); return 0; }   // ★누적 순 포지션이 할당 초과 → 1raw 초과 차단
            return signedSig;
        }

        private void RejectCapital(Lane L, long notional, int rc, long symbolCode)
        {
            Interlocked.Increment(ref _capitalRejects);
            try { _blackbox?.Record(FlightEventKind.RiskReject, symbolCode, L.GenomeId, notional, rc, 11); } catch { }
        }

        /// <summary>
        /// ★가상 장부 확정(익스포저 기반) — 각 레인 실체결분만큼 순 익스포저 갱신. 상쇄분(crossed)은 항상 내부 체결, 잔여분은
        /// residualFills(게이트 통과) 시에만 체결. index 워터폴로 상쇄분을 결정론 귀속. 예약 반납 없음 → 반납 누수 불가.
        /// </summary>
        private void BookFills(ReadOnlySpan<StrategyIntent> intents, long crossedQtyRaw, bool residualFills, long priceRaw)
        {
            long buyCross = crossedQtyRaw, sellCross = crossedQtyRaw;   // 양측 각각 crossed 만큼 내부 매칭
            for (int k = 0; k < intents.Length; k++)
            {
                var it = intents[k];
                var L = _lanes[it.LaneIndex];
                long abs = it.AbsQtyRaw;
                long myCross;   // 이 레인 상쇄분(워터폴)
                if (it.SignedQtyRaw > 0) { myCross = abs < buyCross ? abs : buyCross; buyCross -= myCross; }
                else                     { myCross = abs < sellCross ? abs : sellCross; sellCross -= myCross; }
                long filledAbs = residualFills ? abs : myCross;   // 게이트 통과=전량 체결, 거부=상쇄분만
                if (filledAbs <= 0) continue;
                long signedNotional = QMath.Notional(filledAbs, priceRaw, roundUp: false);
                if (it.SignedQtyRaw < 0) signedNotional = -signedNotional;
                Interlocked.Add(ref L.ExposureRaw, signedNotional);   // 순 익스포저(누적 포지션 = 자본 소진 원장)
                L.Carrier?.RecordFill(0L, Volatile.Read(ref L.ExposureRaw));   // 가상 체결 계수(실현손익 0 — 관측 인프라)
            }
        }

        /// <summary>δ=0 인라인 네팅(netter 미주입 시 폴백) — 총 매수/매도·상쇄·잔여.</summary>
        private static NetResult NetInline(ReadOnlySpan<StrategyIntent> intents)
        {
            long buy = 0, sell = 0;
            for (int i = 0; i < intents.Length; i++) { long s = intents[i].SignedQtyRaw; if (s > 0) buy += s; else if (s < 0) sell -= s; }
            long crossed = buy < sell ? buy : sell;
            return new NetResult(buy - sell, crossed, buy, sell, 0L);
        }

        // ── 리스크 방화벽 결선(콜드·체결/에폭 케이던스) ────────────────────────────

        /// <summary>체결 결과 관측 — 방화벽에 손익/익스포저 공급(공진·속도 퓨즈 입력). 체결/정산 경로가 호출(현 dormant·테스트 직접).</summary>
        public void ObserveFill(int laneIndex, long realizedPnlDeltaRaw)
        {
            if ((uint)laneIndex >= (uint)_laneCount) return;
            var L = _lanes[laneIndex];
            _firewall?.MarkResult(laneIndex, realizedPnlDeltaRaw, Volatile.Read(ref L.ExposureRaw));
        }

        /// <summary>리스크 에폭 전진 + 방화벽 평가 → 봉인 판정 적용(봉인 레인 자본 격리 예치·재분배 금지). 주입 케이던스로 구동.</summary>
        public FirewallVerdict AdvanceRiskEpochAndEvaluate()
        {
            if (_firewall is not { } fw) return default;
            fw.AdvanceEpoch();
            FirewallVerdict v = fw.Evaluate();
            if (v.SealLaneMask != 0UL)
            {
                for (int i = 0; i < _laneCount; i++)
                    if ((v.SealLaneMask & (1UL << i)) != 0) SealLane(i);
                Repartition(Interlocked.Read(ref _globalBudgetRaw));   // 생존 구획만 재분할(봉인분 제외·재분배 아님)
            }
            return v;
        }

        /// <summary>레인 봉인 — 상태 Sealed 전이 + 잔여 서브버짓을 격리 예치(QuarantinedRaw·타 전략 재분배 금지=구획 격벽).</summary>
        private void SealLane(int laneIndex)
        {
            var L = _lanes[laneIndex];
            if (Interlocked.Exchange(ref L.State, (int)LaneState.Sealed) == (int)LaneState.Sealed) return;   // 이미 봉인
            long budget = Volatile.Read(ref L.SubBudgetRaw);
            Interlocked.Add(ref L.QuarantinedRaw, budget);
            Volatile.Write(ref L.SubBudgetRaw, 0);   // 봉인 구획 자본 0 — 신규 발주 불가. 격리분은 타 전략에 재분배하지 않음.
        }

        /// <summary>레인 진압/해제(외부 통제) — Muted 는 신규 진입 억제(청산만 허용 의미). 테스트·운영자 통제용.</summary>
        public void SetLaneState(int laneIndex, LaneState state)
        {
            if ((uint)laneIndex >= (uint)_laneCount) return;
            if (Volatile.Read(ref _lanes[laneIndex].State) == (int)LaneState.Sealed) return;   // 봉인은 되돌리지 않음(fail-closed)
            Volatile.Write(ref _lanes[laneIndex].State, (int)state);
        }
    }
}
