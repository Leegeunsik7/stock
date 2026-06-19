# S19 #1 — 섀도우 그린하우스 아키텍처 청사진 (The Shadow Mutation Greenhouse)

> **상태**: 설계 명세 (Architectural Blueprint) — 구현 코드 없음
> **버전 기준**: v4.0.0 (The Gold Master) 위에 적층
> **설계 철칙**: 외부 의존성 0% · 순수 C# 결정론 · 제로-할당 스레드 격리 · Fail-Closed 계승

---

## 0. 설계 사상 & 불가침 경계 (Invariants)

섀도우 그린하우스는 **메인 실거래 코어에 단 1바이트도 간섭하지 않는 격리된 진화 생태계**다. 핵심 불변식 5가지:

| # | 불변식 | 강제 메커니즘 |
|---|--------|--------------|
| I1 | 실거래 핫패스(틱→집행)는 온실 연산과 가변 상태를 **공유하지 않는다** | 온실에 캔들 **불변 스냅샷 복사본** 전달, 공유 mutable 0 |
| I2 | 온실은 **절대 자율 실배포하지 않는다** | `PromotionGateway` 사람 서명 게이트 필수 (S12 `ManualRelease` 패턴 재사용) |
| I3 | 모든 변이·평가·정렬은 **결정론적** | 기존 `CartridgeGenome`의 FNV-1a 의사난수 재사용 (시계·`Random` 금지) |
| I4 | `EmergencyFreeze` 중에는 **승인·핫스왑 모두 차단** | `OrderAttestationService.AssertNormal()` 게이트 |
| I5 | 온실 스레드는 메인 대비 **CPU 후순위** | `Thread.Priority = BelowNormal`, 유휴 시에만 한 세대 진행 |

```
┌──────────────────────── 실거래 코어 (v4.0.0 · 불가침) ─────────────────────────┐
│  ZeroAllocTickEngine → SlippageSentinel(live) → ExecutionBridgeBroker          │
│        │                                              ▲                         │
│        │ (읽기 전용 캔들 스냅샷 1회 복사)               │ Volatile.Read(_activeSlot) │
│        ▼                                              │                         │
│   [불변 Candle[] 스냅샷]                         ┌─ StrategySlot (원자 핫스왑) ─┐ │
└────────│──────────────────────────────────────────│──────────────────────────│─┘
         │                                            ▲ Interlocked.Exchange     │
   ╔═════▼════════════ 섀도우 그린하우스 (격리 스레드 · BelowNormal) ═══════════╗ │
   ║  GreenhouseService (오케스트레이터)                                        ║ │
   ║    1) MutationBoundsMatrix  — 변이 제약 (난수환각·과적합 차단)              ║ │
   ║    2) CartridgeGenome.Evolve — 교차·변이 (기존, 결정론)                     ║ │
   ║    3) FitnessEvaluator       — 다차원 적응도 (섀도우 마찰 포함)            ║ │
   ║    4) PromotionGateway       — 챔피언 → /command 승인 대기 플래그           ║ │
   ╚════════════════════════════════════│═══════════════════════════════════════╝ │
                                          │ 사람 서명(ManualRelease) → 원자 스왑 ───┘
                                          ▼
                              /command 관제탑 (승인 대기 배지)
```

---

## 1. 변이 제약 매트릭스 (Mutation Bounds Matrix)

`CartridgeGenome`의 교차·변이가 **난수 환각·과적합·논리 모순**에 빠지지 않도록 강제하는 3계층 결정론 게이트. 기존 클램프를 흡수하고 **신규 논리 가드**를 추가한다.

### 1.1 현황 — 기존 클램프 (이미 구현됨)

| 파라미터 | 기존 경계 | 출처 |
|----------|-----------|------|
| 지표 주기 `SpecTerm.Param` | `[MinPeriod=2, MaxPeriod=200]` | `StrategyCompiler` |
| 손절 `StopLossPct` | `[0, 50]` (정수 퍼센트포인트 스냅) | `CartridgeGenome.Mutate` |
| 익절 `TakeProfitPct` | `[0, 100]` | `CartridgeGenome.Mutate` |
| 임계 상수 `SpecCond.Const` | `[0, 200]` | `CartridgeGenome.MutateTerms` |

### 1.2 신규 — 논리 정합성 가드 (현재 갭, S19 신설)

범위 클램프만으로는 **논리적으로 모순되거나 병리적인 게놈**을 막지 못한다. `MutationBoundsMatrix.Reconcile(spec) → (bool ok, string reason, StrategySpec repaired)` 가 변이 직후·`Validate` 직전에 개입한다.

| 코드 | 가드 | 규칙 (결정론) | 위반 시 |
|------|------|--------------|---------|
| **B1** 이평 역전 방지 | 한 조건이 단기/장기 이동평균 비교쌍(`SMA/EMA`)일 때 | `shortParam < longParam` 강제. 역전 시 두 주기 **스왑**(폐기 아님 — 유전자 보존) | 자동 교정 |
| **B2** 타임프레임 하한 | 모든 지표 주기 | 전략의 **최소 유효 주기 ≥ `MinEffectivePeriod`(예: 3)** — 1~2봉 과민 신호 차단 | 하한으로 클램프 |
| **B3** 손익비 가드 | `StopLossPct`, `TakeProfitPct` 둘 다 >0 일 때 | `TakeProfitPct ≥ StopLossPct × MinRR`(예: RR 1.0) 권장. 위반 시 익절을 손절×MinRR로 상향 | 자동 교정 |
| **B4** 조건 복잡도 상한 | `Entry.Count + Exit.Count` | `≤ MaxConditions`(예: 6) — 과적합 트리 팽창 차단(오캄의 면도날) | 초과 조건 폐기(결정론적 절단) |
| **B5** 중복·모순 조건 제거 | 동일 `(Kind, Param, Op)` 중복, 또는 `X>k AND X<k'(k≥k')` 같은 공집합 | 정준화(canonicalize) 후 중복 제거·모순쌍 제거 | 정리 |
| **B6** 표본 캔들 하한 | 평가 입력 | `candles.Count ≥ 60`(기존 `Evolve` 유지) + 워밍업 `maxParam+1` 확보 | 평가 스킵 |

> **3계층 파이프라인**: `(A) 범위 클램프(기존)` → `(B) MutationBoundsMatrix.Reconcile(신규 B1~B6)` → `(C) StrategyCompiler.Validate(기존)`. A·C는 이미 있고, **B만 신규 모듈**.

### 1.3 과적합 환각 방지 원칙

- **격자 스냅(기존 유지)**: 손익은 정수 퍼센트포인트, 주기는 정수 — 분수 드리프트(0.x% 손절 같은 병리)를 원천 차단.
- **델타 근방 탐색(기존 유지)**: 변이는 `{-5,-2,-1,0,1,2,5}` 같은 **유한 격자 델타**만 사용 → 파라미터 공간을 연속이 아닌 이산으로 제약.
- **복잡도 패널티(신규)**: B4 + 적응도 항(§2)의 표본수 게이트가 결합해 "데이터에 끼워맞춘" 고복잡도 개체를 억제.

---

## 2. 다차원 적응도 평면 (Fitness Landscape Metrics)

### 2.1 문제: 단일 차원의 함정

현재 `CartridgeGenome`은 **Sharpe 단일 정렬**이다. 단순 수익률·샤프만 추종하는 개체는 (a) 깊은 낙폭, (b) 시장에 큰 마찰(슬리피지)을 유발, (c) 과소표본 행운으로 실전에서 파멸한다.

### 2.2 복합 적응도 — "보상 코어 × 다중 거부 게이트"

핵심 설계: **보상 항은 합산하되, 견고성·마찰·표본은 곱셈 게이트**로 둔다. 게이트가 0에 수렴하면 아무리 수익이 높아도 적응도가 소멸 → "파멸 가능 개체"가 리더보드에 오르지 못한다.

```
                  ┌── 보상 코어 (가산) ──┐   ┌──── 거부 게이트 (곱셈, 0~1) ────┐
   F(genome) =  ( wP·P  +  wW·W  +  wO·O )  ·  gate_R(MDD) · gate_G(Z̄) · gate_N(N) · gate_OOS
```

각 항은 `[0,1]`로 정규화(higher = better). 기본 가중치 `wP=0.55, wW=0.25, wO=0.20` (합 1.0).

| 항 | 의미 | 정규화 정의 (결정론, 초월함수 회피) | 입력 출처 |
|----|------|-----------------------------------|-----------|
| **P** 수익성 | 위험조정 수익 | `P = clamp((Sharpe − SharpeFloor) / (SharpeRef − SharpeFloor), 0, 1)`, 예 `SharpeFloor=0, SharpeRef=2` | `BacktestSummary.Sharpe` |
| **W** 일관성 | 승률 | `W = clamp((WinRate − 0.40) / 0.30, 0, 1)` → 40%↦0, 70%↦1 | `BacktestSummary.WinRate` |
| **O** 자본효율/기회비용 | 시장 노출 적정성 | `O = 1 − |exposure − ExTarget| / ExTarget` (clamp 0~1). 유휴(저노출)=기회비용 손실, 과노출=리스크 | `EquityCurve` 노출 구간 비율 |
| **gate_R** 견고성 | 최대낙폭 거부 | `gate_R = clamp(1 − MDD / MddTol, 0, 1)`, 예 `MddTol=0.40` → MDD 40%↦0 | `BacktestSummary.MaxDrawdown` |
| **gate_G** 시장 마찰 | 슬리피지 잔차 거부 | `gate_G = clamp(1 − Z̄ / Ztol, ε, 1)`, `Z̄`=섀도우 집행 평균 |Z|, 예 `Ztol=2.0` | 섀도우 `SlippageSentinel.RunningMean/StdDev`→`Z̄` |
| **gate_N** 표본 신뢰 | 과소표본 거부 | `gate_N = clamp(TradeCount / TradeFloor, 0, 1)`, 예 `TradeFloor=20` | `BacktestSummary.TradeCount` |
| **gate_OOS** 과적합 거부 | 표본외 일관성 | `gate_OOS = clamp(1 − |F_is − F_oos| / max(F_is, εF), 0, 1)` | WalkForward 폴드 |

> **"시장에 가장 상처를 덜 내는 견고한 알파"** = `gate_G`(마찰)와 `gate_R`(낙폭)이 곱셈 거부 게이트로 작동하는 정확한 수학적 표현. 마찰이 크거나 낙폭이 깊으면 적응도가 곱셈으로 소멸한다.

### 2.3 섀도우 마찰(Z̄)의 출처 — 핵심 통합점

`gate_G`의 `Z̄`는 백테스트만으로는 못 구한다(체결 마찰은 집행 시점에만 발생). 따라서:

- 온실은 **격리된 `SlippageSentinel` 인스턴스**를 챔피언 후보별로 보유한다(메인 라이브 센티넬과 분리).
- 후보 게놈을 **섀도우/페이퍼 집행 경로**(가상 체결가 vs 기준가)로 흘려 `RecordFill` 누적 → `RunningMean/StdDev`로 `Z̄` 산출.
- 라이브 알고 집행 피드가 연결되기 전(§7 참조)에는 **백테스트 체결 슬리피지 모델을 프록시**로 사용하고, 그 사실을 적응도 메타에 정직 표기(`frictionSource = "backtest-proxy" | "shadow-live"`).

### 2.4 선택 정책 — 스칼라화 vs 파레토

- **기본(권장)**: 위 스칼라 `F`로 내림차순 정렬 → 기존 `Evolve`의 `OrderByDescending(sharpe)`를 `OrderByDescending(F)`로 **드롭인 교체**(최소 침습).
- **고급(옵션)**: `(P, gate_R, gate_G)` 3목적 **파레토 비지배 프론티어** 유지 후, 프론티어 내 스칼라 타이브레이크. 다양성 보존에 유리하나 복잡도↑ → S19 후속 마일스톤으로 분리.

---

## 3. 인간 게이트웨이 & 원자적 핫스왑 가드

### 3.1 승인 워크플로 상태기계

```
 Cultivating ──(F ≥ ChampionThreshold && 세대종료)──▶ Candidate
 Candidate ──(MutationBoundsMatrix 통과 && OOS 게이트 통과)──▶ PendingApproval
 PendingApproval ──(/command 배지 점등, 지휘관 대기)
        ├─(사람 서명 거부)──▶ Rejected ──▶ (유전자풀 잔류 또는 Sunset)
        └─(사람 서명 승인 · AssertNormal OK)──▶ Armed
 Armed ──(원자 핫스왑 성공)──▶ Active
 Active ──(레짐 변화로 마찰/낙폭 게이트 붕괴)──▶ Sunset(자동 안락사) ──▶ (이전 챔피언으로 롤백 또는 무전략)
```

- **자율 허용**: `Cultivating → PendingApproval`(배양·평가), `Active → Sunset`(노후 게놈 안락사 — 실거래 *축소/중단* 방향이므로 안전).
- **사람 게이트 필수**: `PendingApproval → Armed`(실거래 *투입* 방향 — I2 불변식). 자율 실배포는 영구 금지.

### 3.2 /command 관제탑 통합

S18 #2 `SystemStatusAggregator`에 **5번째 센서**를 추가(선택적·nullable, 기존 4센서 불변):

```
GreenhousePresent : bool          // 온실 스레드 가동 여부
PendingApproval   : bool          // 승인 대기 챔피언 존재 → 배지 점등
ChampionFitness   : double        // 대기 챔피언의 복합 F
GenerationsRun    : long          // 누적 세대 수
GreenhouseSeverity: PendingApproval ? Notice : Ok   // 승인 대기는 "주의"(비오류) — 지휘관 액션 유도
```

> 승인 대기는 **Critical이 아닌 Notice**(슬레이트색). 온실은 실거래 위험원이 아니라 *제안자*이므로, 동결·누수와 동급의 적색이 아니다 (S18 #2 정직 색상 철학 계승).

### 3.3 원자적 핫스왑 — 락-프리 포인터 스왑

활성 전략 슬롯은 **불변 `StrategySpec`(record) 참조 1개**다. `StrategySpec`이 불변이므로 참조 교체만으로 원자 교체가 성립한다.

```
StrategySlot (신규 타입, 락 0)
 ├─ private StrategySpec? _active;         // 단일 참조
 ├─ private long _generation;              // ABA 방지 + 감사 카운터
 │
 ├─ Read()  → Volatile.Read(ref _active)            // 집행 루프가 매 틱 호출 (무중단)
 └─ Swap(next, signature):
        AssertNormal();                              // I4 — 동결 중 스왑 금지
        var prev = Interlocked.Exchange(ref _active, next);   // ⚛ 원자 교체
        Interlocked.Increment(ref _generation);
        Audit(prev, next, signature);                // 감사 원장 기록
        return prev;                                 // 포지션 마이그레이션 입력
```

- **무중단 보장**: 집행 스레드는 `Volatile.Read`로 항상 **일관된 단일 참조**를 본다. 스왑 직전/직후 어느 쪽이든 *완결된* 전략을 읽으며, 절반만 바뀐 상태는 존재할 수 없다(불변 record + 단일 참조 교체).
- **락 경합 0**: `Interlocked.Exchange<T>(ref T, T)`는 락 없는 단일 CAS-급 연산.
- **ABA 방지**: `_generation` 증가로 "같은 참조로 보이지만 다른 세대" 혼동 차단 + 감사 추적성.

### 3.4 포지션 마이그레이션 안전장치

스왑 시점에 구 전략이 **보유 중인 포지션**을 어떻게 처리할지가 핵심 위험. 3가지 정책 중 **그레이스 윈도우(권장)**:

| 정책 | 동작 | 위험 | 채택 |
|------|------|------|------|
| (a) 동결-인수 | 신전략이 기존 포지션 관리 승계 | 신전략 청산규칙이 구 포지션에 부적합 가능 | △ |
| (b) 청산-후-전환 | 구 포지션 전량 청산 후 신전략 가동 | 강제 청산 슬리피지·세금 발생 | △ |
| **(c) 그레이스 윈도우** | **구 포지션은 구 전략 청산규칙으로만 닫고, 신규 진입은 신전략만** | 두 전략 일시 공존(관리됨) | **✅** |

그레이스 윈도우 규칙:
1. 스왑 후 `prev` 전략은 **청산 전용(read-only exit)** 으로 강등 — 신규 진입 금지, 기존 포지션의 청산 신호만 평가.
2. `next` 전략은 **신규 진입 전용** — `prev`가 비운 자본으로만 진입.
3. `prev`의 마지막 포지션이 청산되면 그레이스 윈도우 종료, `prev` 폐기.
4. 전 과정 `OrderAttestationService.Reconcile`로 로컬↔브로커 원장 정합 검증 — 불일치 시 `EmergencyFreeze`(기존 S12 안전망).

### 3.5 승인 = 서명된 감사 엔트리

승인은 S12 `ManualRelease(signature, note)` 패턴을 그대로 재사용한다. 지휘관 서명 없이는 `Swap`이 호출되지 않으며, 모든 스왑은 `(이전 게놈Id, 신규 게놈Id, 서명, 타임스탬프, F-스코어)`로 감사 원장(`FraudAlerts` 또는 전용 `PromotionLog`)에 영속 기록된다.

---

## 4. 스레드 격리 & 제로-할당 모델

| 측면 | 설계 | 근거 |
|------|------|------|
| 스레드 종류 | **전용 long-running `Thread`** (ThreadPool 아님), `IsBackground=true`, `Priority=BelowNormal` | ThreadPool 기아·메인 간섭 방지 (I1·I5) |
| 입력 격리 | 메인이 캔들 히스토리의 **불변 스냅샷(읽기 전용 배열)** 1회 복사 후 온실에 전달 | 핫패스와 공유 가변 상태 0 |
| 할당 격리 | 온실 평가 루프는 **풀링된 버퍼 재사용**(`EquityCurve` 리스트 풀 등). 메인 `ZeroAllocTickEngine`과 객체 미공유 | 메인 GC 압력 0 |
| 백프레셔 | 온실은 **CPU 유휴(틱 유입 한산) 시에만** 한 세대 진행. 메인 절대 우선 | I5 |
| 누수 추적 | 온실 스레드 = `MemoryLeakGuard` 추적 리스 1건, 종료 시 반납 | S18 #1 도그푸딩 |
| 영속화 | 챔피언 게놈·세대 카운터를 `StateHydrationEngine` 슬롯에 스냅샷 → 재부팅 복원 | S13/S18 #1 재사용 |
| 동결 연동 | `EmergencyFreeze` 진입 시 온실 평가 일시정지(`AssertNormal` 폴링) | I4 |

> **제로-할당의 의미 범위**: 메인 틱 핫패스는 기존대로 제로-할당 유지. 온실 내부는 평가 1세대당 일부 할당이 불가피하나(백테스트 equity 등), **풀링 + BelowNormal + 유휴 스케줄링**으로 메인 핫패스에 GC/CPU 간섭이 전파되지 않도록 격리하는 것이 목표다.

---

## 5. 신규 모듈 구성 (시그니처 명세 — 구현 없음)

| 모듈 | 책임 | 핵심 공개 표면 (계약) |
|------|------|----------------------|
| `MutationBoundsMatrix` (static) | §1 논리 가드 B1~B6 | `Reconcile(StrategySpec) → (bool ok, string reason, StrategySpec repaired)` |
| `FitnessEvaluator` (static) | §2 복합 적응도 | `Score(BacktestSummary, double meanAbsZ, OosStats) → FitnessVector`; `FitnessVector { P, W, O, GateR, GateG, GateN, GateOOS, F }` |
| `GreenhouseService` (sealed) | §0/§4 격리 오케스트레이터 | `Start(CandleSnapshot, OrderAttestationService)`, `Stop()`, `Probe() → GreenhouseReport`; 내부서 `CartridgeGenome.Evolve` 구동 |
| `StrategySlot` (sealed) | §3.3 원자 핫스왑 홀더 | `Read() → StrategySpec?`, `Swap(StrategySpec, signature) → StrategySpec? prev`, `Generation` |
| `PromotionGateway` (sealed) | §3.1 승인 상태기계 + 감사 | `Submit(GenomeEntry, FitnessVector)`, `Approve(signature, note)`, `Reject(reason)`, `Pending → GreenhouseReport` |
| `SystemStatusAggregator` (확장) | §3.2 5번째 센서 | 기존 4센서 불변 + `GreenhousePresent/PendingApproval/ChampionFitness` nullable 추가 |

---

## 6. 테스트 전략 (S19 #2+ 예고)

| 영역 | 검증 |
|------|------|
| 변이 경계 | B1 이평 역전 입력 → 스왑 교정 / B4 복잡도 초과 → 절단 / 결정론(동일 seed→동일 결과) |
| 적응도 | 깊은 MDD 개체 → `gate_R→0` → F 소멸 / 고마찰 → `gate_G→0` / 과소표본 → `gate_N` 패널티 / 단조성 |
| 핫스왑 | **동시성**: 집행 스레드가 `Read` 폭주 중 `Swap` 다회 → 항상 완결된 단일 참조, 토른 0, 세대 단조증가 |
| 승인 게이트 | 서명 없이 `Swap` 미호출 / `EmergencyFreeze` 중 `Swap` 거부(fail-closed) |
| 격리 | 온실 가동 중 메인 핫패스 지표 무변(간섭 0), `MemoryLeakGuard` 리스 종료 후 0 수렴 |

---

## 7. 미해결 항목 & 정직 경계 (Honest Boundaries)

1. **라이브 전략→주문 집행 루프는 아직 미배선이다.** S18 #2에서 확인했듯 `AdaptiveExecutionTuner`/`SlippageSentinel`/알고 스택은 라이브 호스트에 결속됐으나 **실제 알고 집행 피드(전략 신호 → 주문)는 가동되지 않는 휴면 상태**다. 본 청사진은 `StrategySlot`이라는 **핫스왑 seam의 계약**을 정의하지만, 그 슬롯을 읽어 실주문을 내는 *집행 루프 자체*는 별도 마일스톤(S19 #3 이후)이다.
2. **`gate_G`(섀도우 마찰)는 라이브 피드 연결 전까지 백테스트 슬리피지 프록시**로만 산출된다. 진짜 시장 마찰 Z̄는 페이퍼/섀도우 집행이 가동된 후에야 수집되며, 적응도 메타에 출처(`backtest-proxy` vs `shadow-live`)를 정직 표기한다.
3. **정규화 상수**(`SharpeRef`, `MddTol`, `Ztol`, `TradeFloor` 등)는 초기 추정값이며, 실제 분포 관측 후 보정 대상이다. 상수는 전부 `init` 프로퍼티로 노출해 환경별 튜닝을 허용하되 기본값은 보수적으로 둔다.
4. **파레토 다목적 선택**(§2.4 고급)은 본 마일스톤 범위 밖 — 기본은 스칼라화 드롭인 교체.

---

## 8. 구현 마일스톤 분해 (제안)

| 마일스톤 | 범위 | 산출물 |
|----------|------|--------|
| **S19 #1** | 본 청사진 | 아키텍처 명세서 (이 문서) |
| S19 #2 | `MutationBoundsMatrix` + `FitnessEvaluator` (순수 함수 코어) | 코어 2종 + 단위 테스트 ~20 |
| S19 #3 | `StrategySlot` 원자 핫스왑 + `PromotionGateway` 상태기계 | 핫스왑·승인 + 동시성 테스트 |
| S19 #4 | `GreenhouseService` 격리 스레드 + `/command` 5번째 센서 | 오케스트레이터 + 관제탑 배지 |
| S19 #5 | 라이브 집행 루프 seam 결속 (휴면 해제) | 별도 안전 검토 필수 |

---

*외부 라이브러리 0 · 순수 C# 결정론 · 제로-할당 격리 · Fail-Closed 계승 — v4.0.0 철갑 위에서만 가능한 진화.*
