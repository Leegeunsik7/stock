# MS2E StockSuite — 정밀 인수인계 문서

> 기준 버전: **v4.83.0** (2026-07-01)  
> 대상: 신규 개발자 또는 지휘관(gslee@angtec.com)이 컨텍스트 없이 재진입할 때  
> 소스가 권위. 이 문서는 네비게이션 보조다.

---

## 0. 30초 요약

**MS2E StockSuite**는 한국 개인 퀀트 트레이더를 위한 올인원 터미널이다.

- .NET 8 WinForms + Blazor Hybrid (WebView2), SQLite 단일 DB
- 9개 WinForms 모듈 + Blazor 대시보드가 `LocalDatabase`를 공유
- AI 분석/포트폴리오 멘토/미래가치 시뮬/카오스 진단 내장
- **고정소수점(QFixed) 청산 척추** + **seqlock SPMC 링버퍼** + **3중 liveArmed:false 게이트**
- 1,698 테스트 클린런, 전역 플래키 0%, 빌드 경고 0

```powershell
# 빌드 (앱 종료 필수 — exe가 바이너리 잠금)
dotnet build StockManager/StockManager.csproj -c Release

# 전체 테스트
dotnet test StockManager.Tests/StockManager.Tests.csproj --logger "trx" -c Release

# 앱 실행
dotnet run --project StockManager/StockManager.csproj
```

---

## 1. 솔루션 구조

```
StockManager_v3.4.1/
├── HANDOVER.md                          ← 이 문서
├── StockManager/                        ← 메인 앱
│   ├── StockManager.csproj              ← SDK=Razor, net8.0-windows, AssemblyName=MS2E.StockSuite
│   ├── Launcher.cs                      ← 통합 메뉴 진입점 (9개 모듈 카드 UI)
│   ├── 00_Setup/SetupWizard.cs          ← 설정 마법사
│   ├── 01_RealtimeWatcher/              ← 실시간 시세 + 차트
│   ├── 02_Portfolio/                    ← 포트폴리오 관리
│   ├── 03_AIAnalysis/                   ← AI 분석
│   ├── 04_NewsRadar/                    ← 뉴스 레이더
│   ├── 05_SignalEngine/                 ← 시그널 엔진 + 백테스트
│   ├── 06_CryptoWatcher/               ← 암호화폐 감시
│   ├── 07_TotalAsset/                   ← 총자산 현황
│   ├── 08_PaperTrading/                 ← 모의투자
│   ├── 09_CompositeAlerts/              ← 복합 알림
│   ├── Blazor/                          ← Blazor Hybrid 대시보드
│   │   ├── BlazorHomeForm.cs            ← DI 컨테이너 + WebView2 호스팅
│   │   ├── ModuleBridge.cs              ← IModuleBridge(DI) + 정적 브릿지(STA 마샬링)
│   │   ├── DashboardService.cs          ← Blazor 데이터 허브 (싱글톤)
│   │   ├── ToastService.cs              ← 전역 액션형 토스트
│   │   └── Pages/                       ← Razor 페이지들
│   │       ├── DashboardHome.razor      ← @page "/dashboard" — 통합 관제탑 런처 허브
│   ├── Common/                          ← 핵심 인프라 (아래 §2~§5 상세)
│   └── wwwroot/                         ← index.html, app.css, dashboard.js
└── StockManager.Tests/                  ← xUnit (~1,472 테스트)
```

### 1.1 아키텍처 개요

```
Launcher.cs
    └── BlazorHomeForm (WinForms 폼)
            ├── BlazorWebView → Blazor 대시보드 (WebView2)
            ├── DI 컨테이너 (AddSingleton 체인)
            │     ├── LocalDatabase (SQLite, WAL+busy_timeout+재시도)
            │     ├── OrderAttestationService (라이브 전용)
            │     ├── ShadowCutoverGate (전용 섀도우 att 주입)
            │     ├── ExecutionBridgeBroker / BrokerHub
            │     ├── IgnitionLock
            │     ├── RuntimeConfig / ConfigAuditor
            │     ├── ComplianceBridge / SlippageSentinel
            │     ├── GatedOrderDispatcher + LiveArmingGate (3관문 무장 술어)
            │     ├── OrderDrainWorker (50ms 드레인 엔진, ValveWired 이중 키)
            │     └── IModuleBridge → HostBoundModuleBridge (STA 마샬링 어댑터)
            └── ModuleBridge(정적) → 레거시 WinForms 창 위임 (InvokeRequired 마샬링 내장)

Pages/DashboardHome.razor (@page "/dashboard") — v4.69.0 통합 관제탑
    ├── NavigationManager.NavigateTo() → 15개 모듈 Blazor 네이티브 전환
    └── IModuleBridge.Open() → 엣지 케이스 WinForms STA 위임
```

**단일 진실 소스**: `LocalDatabase` (`%LocalAppData%\StockManager\portfolio.db`). WinForms·Blazor·백그라운드 서비스 모두 같은 DB 공유.

---

## 2. 핵심 설계 원칙

| 원칙 | 구현 |
|---|---|
| **liveArmed:false 불변** | 3중 게이트 — GatedOrderDispatcher/IgnitionLock/OrderAttestationService |
| **fail-closed** | 경계 오염·오버플로 → ForceFreeze, 모호함 통과 금지 |
| **무할당(δ=0) 핫패스** | 고정소수점 값 타입, seqlock, 스택/사전할당 버퍼 |
| **결정론** | 동일 입력 → 비트 동일 출력, RNG 사용 금지(시뮬·테스트) |
| **Gemini 3룰** | LLM은 엔티티 추출·표현만, 수학·결정·락은 코어 |

---

## 3. Common 계층 — 고정소수점 척추

### 3.1 `QFixed.cs` — 정밀도 기반

**스케일**: 10^8 raw (사토시·KRW 원 공용). 전부 `checked` 정수 산술.

```
QMath         — 내부 정수 산술 (Add/Sub/Notional/CrossDiv, Int128 중간연산)
QPrice        — 호가. 보수 정렬: 매수=AlignUp(ceil)↑, 매도=AlignDown(floor)↓
QQty          — 수량. 보수 정렬: 항상 AlignDown↓ (과대 금지)
QMoney        — 명목금액/원장 가치. CrossDiv(금액÷단가=수량) 내장
QLedgerGuard  — 청산 게이트: 보수 정렬 후 명목 산출, 오염 → ForceFreeze
```

**보수 편향 핵심**: `Notional(a, b, roundUp: side==Buy)` — 매수=ceil(비용↑), 매도=floor(수령↓). `0방향 절사`는 매수 비용을 과소계상해 AlignUp 효과를 무효화하므로 방향 분리 필수.

**오버플로 트랩**: QPrice×QQty = raw² → Int128 중간 + `checked((long)q)`. AlignUp도 `checked(raw + step - r)`.

### 3.2 `QClearing.cs` — 3미션 청산 아키텍처

decimal → QFixed 단계적 마이그레이션 교량:

```
QAcl                  M1: 부패 방지 계층. decimal import → Try 패턴
                           음수/오버플로 → 경계에서 ForceFreeze (raw 예외 확산 금지)
QScaleGuard           M2: 멀티-스케일 정합
                           격자 비정렬 또는 round-trip 디싱크(≥1step) → 동결
ShadowClearingTracer  M3: 섀도우 추적
                           (1) 보수 불변식: 정렬exact 대비 매수ceil·매도floor·|Δ|≤1raw
                           (2) 회귀 밴드: naive decimal 대비 정렬+반올림 델타 초과 → 동결
```

#### ShadowCutoverGate (`QClearing.cs`)

```csharp
// 생성자 2종
ShadowCutoverGate(bool enabled, QTickSpec spec, OrderAttestationService? att,
                  ClearingAuthority authority = LegacyPrimary)
ShadowCutoverGate(bool enabled, QTickRegistry registry, ...)  // S27-e: 자산별

// Clear 메서드 4종
Clear(price, qty, side)                          // 단일 spec (동일 통화)
Clear(symbol, price, qty, side)                  // 자산별 spec 조회
Clear(symbol, price, qty, side, fxQuote, fxBounds)  // 이종통화 FX — 반환: 자산통화
ClearBase(symbol, price, qty, side, fxQuote, fxBounds)  // S29-c — 반환: base통화

// 권위 역전 (S29-c)
enum ClearingAuthority { LegacyPrimary=0, QFixedPrimary=1 }
void SetAuthority(authority)  // volatile 쓰기, 런타임 가역, 무할당

// 텔레메트리
long Observed       // 관측 누적
long FrozenCount    // 동결 누적
double LastDivergence  // 최근 신-구 명목 차 (Interlocked 락-프리)
```

**안전 불변**:
- `enabled=false && LegacyPrimary` → INERT (즉시 legacy 반환, 할당 0)
- 미등록 심볼 → fail-closed 동결
- FX `Clear()`는 권위 역전 미적용 (반환 단위=자산통화, 후방호환)
- `ClearBase()`만 base통화 권위 지원

---

## 4. Common 계층 — 주문 실행 파이프라인

### 4.1 `OrderAttestationService.cs` — 거래 증적 감사

```
GatingMode: Normal / EmergencyFreeze  (volatile int 원자)

AssertNormal(ctx)         — 실주문 진입부 인터셉터. Freeze면 즉시 throw
Reconcile(local, broker)  — O(N) 교차검증. 1원/1주 불일치 → EmergencyFreeze
ForceFreeze(reason)       — 외부 이벤트 (파이프 끊김, 청산 오염) → 즉시 동결
ManualRelease(signature)  — 서명 확인 후만 해제 (감사 로그 기록)
ProcessFill(fill, expected) — 체결 감사: 방향/가격오차(0.5%)/과체결 → ForceFreeze
MaxLogEntries: 200        — FIFO 감사 로그 캡
```

**DI 필수 규칙**: 섀도우 게이트는 **전용 `new OrderAttestationService()`** 주입. 라이브 인스턴스 공유 시 섀도우 동결 → 실 주문 차단 (footgun, `BlazorHomeForm.cs` 주석 참조).

### 4.2 브로커 계층

```
IBroker
  ├── PaperBroker          — 모의투자 (시드 1천만KRW, 수수료 0.015%, 지정가 프로세싱)
  ├── LiveBrokerStub       — 미무장 시 거부 (liveArmed:false 물리 구현)
  ├── LiveRouterBroker     — 실거래 라우터
  ├── UpbitLiveBroker      — 업비트 실주문 (JWT HS256 + SHA512 쿼리 해시)
  ├── ExecutionBridgeBroker — 키움 파이프 (FillReceived/ProxyLost → ForceFreeze)
  └── ShadowClearingBroker — Path B 데코레이터 (QFixed 병렬 관측, inner 권위 불변)
```

```csharp
// 팩토리 패턴
IBroker inner = !IsLive(db)  ? new PaperBroker(db)
              : !IsArmed(db) ? new LiveBrokerStub()
              : (IBroker)new LiveRouterBroker(db);
return shadowGate is null ? inner : new ShadowClearingBroker(inner, shadowGate);
```

`ShadowClearingBroker.Place()`: 게이트 INERT면 Clear 미호출(완전 통과). 관측 예외는 삼킴 — 섀도우 결함이 `inner.Place`를 절대 막지 않음.

### 4.3 `IgnitionLock.cs` — 점화 잠금 (4단계 FSM)

```
Locked → ProbeReady → TimeBoxOpen → MasterIgnited
```

- `Arm(signature)`: Locked + 서명 필요 (비대칭 안전)
- `Lock()`: 즉시 전환 (어느 상태에서도)
- `AllowPhysical()`: MasterIgnited일 때만 true
- **3σ 슬리피지 경보** → 자동 Lock
- 프로브: 1주 소액, 30분 시간상자

### 4.4 `OrderDispatchRing.cs` — SPMC 링버퍼 (Path C)

```csharp
// 고정소수점 전송 단위 (unmanaged)
readonly struct OrderProposal {
    long OrderId, GenomeId, SymbolCode, PriceRaw, QtyRaw, TimestampTicks;
    byte SideByte, KindByte;
}

// 단일 생산자 CAS 게이트
OrderDispatchRing.Publish(...)    // EnterPublish: CAS _publishing → fail-closed throw
OrderDispatchRing.PublishRaw(in proposal)
long WriterCollisions             // CAS 낙방 누적 (정상=0)

// 무장 게이트 디스패처
GatedOrderDispatcher.Drain(onSend)
  // n++ 먼저 (증발 방지)
  // try { armed = _isArmed(); } catch { armed = false; }  // 술어 throw → fail-closed
  // armed → onSend(p) + Forwarded++  /  else → GatedOut++
```

`RingBufferCore<T>` (S20 seqlock SPMC):
- 생산자: `Seq=Writing` → 값 기록 → `Seq=시퀀스` (release fence)
- 소비자: `s1 == s2 == seq`일 때만 채택, torn → 재시도 (최대 32회)
- 추월: `published - next >= capacity` → fast-forward, Dropped 계상

### 4.5 `RingTelemetry` — UI 락-프리 텔레메트리 (Path Gamma)

```
RingTelemetry           — blittable 값 타입 (Published/Forwarded/GatedOut/Dropped/WriterCollisions/ThroughputPerSec)
RingTelemetryChannel    — [StructLayout(Explicit)] Cell seqlock (홀→payload→짝, 8회 재시도→직전 프레임)
RingTelemetryPublisher  — 주기 스냅샷 발행 (CAS 가드, Stopwatch 주입 → 결정론 테스트)
```

UI는 `TryRead()`만 (단방향). 코어 무블록, 경합 시 직전 프레임 유지.

---

## 5. Common 계층 — AI 포트폴리오 멘토 (S28)

### 5.1 `QPortfolioMentor.cs` — M1 온보딩 엔진

```
EntityDraft          — LLM 추출 ACL 경계 DTO (decimal?/string? — 신뢰하지 않음)
PortfolioSlot        — 정준화 슬롯 (QQty·QPrice·SlotField.Known)
OnboardingCanonicalizer.TryCanonicalize()
  — 소수 0-절사 재검증: qty.Raw <= 0 → BadQuantity
  — OverflowException → CanonReject.Overflow
OnboardingPump.Ingest()     — 점진 upsert (누락 필드 병합), 상태 진행
ComplianceGate.Admit(IntentClass)  — sticky 락 (세션 내 해제 불가)
```

**Gemini 룰1**: LLM은 엔티티 추출만 (숫자 계산 금지).  
**Gemini 룰3**: 유사투자자문/확정수익/탈옥 → `ComplianceLocked` 전이, 모호한 회피 응답 없이 결정론 고지문 발화.

### 5.2 `QFutureValue.cs` — M2 미래가치 + 락-프리 교량

```
FutureValueEngine.Project()
  — 결정론 복리: V = Σ A_i·(1+r_i)^t (고정소수점, RNG 없음)
  — 분위 밴드: ±k·σ·√t (k=1.645, closed-form) — 몬테카를로 사칭 금지
  — 음수 drift·overflow → fail-closed

ScenarioPublisher    — seqlock SPMC 발행자
  .Recompute(ShadowMutation)  — CAS _writerGate (단일 라이터 강제)
                                  경합 패배 → _writerCollisions++만 (ForceFreeze 금지 — 자해성 동결 차단)
  .TryReadFrame()             — torn-read 재시도 (최대 64스핀)

ShadowMutation: ScaleSymbol(symbol, factor) / ScaleClass(class, factor)
  — 구조체 값 타입 (무할당), LLM → 코어 주입
```

**정직 경계**: 복리 factor는 매 연차 floor 절사 → 결정론적 하향 편향 (미래 과대 표시 불가).

### 5.3 `QChaos.cs` — M3 카오스 + 진단 + 리밸런싱 밸브

```
ChaosScenario: None / Gfc2008 / RateSpike / InflationShock / CryptoWinter
ChaosEngine.Severity()           — CryptoWinter=0.55 (×β2.0→1.10>1 → 0 클램프 실도달)
ChaosEngine.Beta(scenario, class) — 자산군 시스템 베타
ChaosEngine.ApplyStress()        — stressed_i = V0_i · max(0, 1 - severity·β)
PortfolioDiagnosis.Diagnose()    — 집중도·방어점수·최악낙폭 (숫자만, LLM이 서술)
RebalanceLever.TryScaleClass/Symbol(bp)  — 범위[0, 50000bp] 이탈 → fail-closed
```

---

## 6. Common 계층 — 3중 liveArmed:false 게이트

```
게이트 1: GatedOrderDispatcher
  _isArmed() == false → GatedOut (송출 0)
  _isArmed() throw    → fail-closed (미무장 간주, GatedOut)

게이트 2: IgnitionLock
  AllowPhysical() == false → 물리 주문 차단
  3σ 슬리피지           → 자동 Lock

게이트 3: OrderAttestationService
  AssertNormal() — 모든 실주문 진입부 호출
  EmergencyFreeze → InvalidOperationException (체결 불가)
```

**현재 상태**: `liveArmed:false` 전 영역 유지(기본).
- v4.57.0: `LiveArmingGate` 3관문 결속 (`BrokerHub.IsArmed ∧ IgnitionLock.AllowPhysical ∧ att.AssertNormal`).
- v4.59.0: `DispatchArming.ResolvePredicate(wired, gate)` — `Dispatch.LiveArmingWired=false` 기본.
- v4.70.0: `OrderDrainWorker` 50ms 드레인 엔진 — `Dispatch.ValveWired=false` 기본.

**이중 키 잠금**: `ValveWired=true ∧ LiveArmingWired=true` 양쪽 ON 시에만 실 주문 흐름. 어느 한쪽만 false여도 실거래 0% 수학적 보장.

---

## 7. AI / 전략 계층

### 7.1 분석 엔진

| 파일 | 역할 | 핵심 |
|---|---|---|
| `BacktestEngine.cs` | 전략 백테스트 | 5전략, 슬리피지 0.1%, 수수료 0.015%(KR)/0.99$(US), Sharpe×√252 |
| `StrategyOptimizer.cs` | MA 격자탐색 | short∈{5,8,10,12}×long∈{20,26,30,60}, 무거래 조합 제외 |
| `AlphaEngine.cs` | OLS 진성 알파 | Beta=Cov/Var, TrueAlpha=Π(1+Resid), IR=연환산 Sharpe |
| `CommitteeEngine.cs` | 위원회 팩트시트 | AlphaReport+TailBudget+Backtest → 한국어 직렬화 (LLM 숫자 환각 방화벽) |
| `MultiverseEngine.cs` | 다중 시나리오 | 4시나리오(Live/Bull/Stagflation/DeepRecession), GFC 2008 오버레이 |
| `RegimeDetector.cs` | 레짐 변화 탐지 | 이상탐지 |
| `MarkowitzOptimizer.cs` | MPT 최적화 | 효율적 포트폴리오 프론티어 |
| `MonteCarlo.cs` | 몬테카를로 | 수익 분포 시뮬 |
| `WalkForward.cs` | Walk-Forward | Out-of-Sample 검증 (과최적화 방지) |
| `EventStudy.cs` | 이벤트 스터디 | 이벤트 전후 수익률 분석 |
| `RiskAttribution.cs` | 리스크 기여도 | 패리티 분해 |
| `CounterfactualTwin.cs` | 반사실 트윈 | 역사적 What-If |
| `HypothesisMiner.cs` | 자동 가설 발굴 | |
| `ScenarioStudio.cs` | 시나리오 스튜디오 | What-If |
| `ForecastScore.cs` | 예측 스코어보드 | |
| `PositionSizer.cs` | 포지션 사이저 | Kelly 크기 산정 |
| `AlertCalibrator.cs` | 알림 캘리브레이터 | 신뢰도 보정 |

### 7.2 전략 진화

```
CartridgeGenome.Evolve()   — 유전자 알고리즘 (교차+변이+선택, FNV 결정론 난수)
CartridgeService           — .ms2cart JSON 서명·검증·배포 (ECDsa, DPAPI)
StrategyCompiler/Parser    — 전략 DSL 컴파일
StrategyTournament         — 전략 토너먼트 선발
AgentRouter/AgentService   — LLM 에이전트 라우팅
```

### 7.3 LLM 연동

```
ILlmProvider               — 추상 (Gemini, GPT-4o, Ollama 로컬)
AiService.AskAsync()       — PortfolioContext.Build() 주입으로 숫자 환각 차단
PortfolioContext.Build()   — DB → 정확한 한국어 포트폴리오 직렬화 (LLM에게 수학 시키지 않음)
DebateService              — 멀티에이전트 토론 (Bull/Bear/CRO 역할)
NightlyResearch            — 야간 자동 리서치/퍼블리싱
```

---

## 8. 데이터 공급자

| 공급자 | 파일 | 비고 |
|---|---|---|
| **키움 OpenAPI+** | `BrokerProviders.cs` | 32-bit COM 전용 (`PlatformTarget=x86` 주석 해제 필수) |
| **한국투자증권 KIS** | `BrokerProviders.cs` | REST, Bearer JWT, `prdy_vrss` 전일대비 정확 처리 |
| **업비트** | `LiveBrokers.cs` | JWT HS256 + SHA512 쿼리 해시 |
| **빗썸** | `BithumbProvider.cs` | |
| **네이버/다음** | `QuoteProviders.cs` | 일봉 캐시 (`LinearQuoteCache`) |
| **로컬 DB** | SQLite | WAL + `busy_timeout` + 재시도 래퍼 |

---

## 9. 테스트 인프라

### 현황 (v4.83.0)

- **총 테스트**: 1,698 (클린런)
- **플래키 0%**: Path A에서 전역 달성 (27회 연속 TRX-clean)
- **빌드 게이트**: `TreatWarningsAsErrors=true`

### 주요 테스트 파일

| 파일 | 커버 |
|---|---|
| `S27DecimalSweepTests.cs` | QFixed 기본 |
| `S27bShadowClearingTests.cs` | 섀도우 추적·보수 불변식 |
| `S27cCutoverTests.cs` | ShadowCutoverGate ON/OFF/INERT |
| `S27dMultiAssetTests.cs` | 멀티자산 레지스트리 |
| `S27eMultiAssetGateTests.cs` | FX·이종통화 청산 |
| `S28PortfolioMentorTests.cs` | M1/M2/M3 (42 tests) |
| `PathBShadowClearingTests.cs` | IBroker 데코레이터 (5 tests) |
| `S29OrderDispatchRingTests.cs` | SPMC 링 (9 tests) |
| `S29GammaRingTelemetryTests.cs` | 링 텔레메트리 (11 tests) |
| `S29cAuthorityInversionTests.cs` | 권위 역전 (10 tests) |
| `S29cFxAuthorityTests.cs` | FX 권위 역전 (7 tests) |
| `RingBufferTests.cs` | seqlock torn-safety |
| `IgnitionLockTests.cs` | 점화 잠금 FSM |
| `OrderAttestationTests.cs` | 감사 서비스 |
| `ResilienceMeshTests.cs` | TWAP/알고 내성 (work-bounded) |
| `KiwoomBridgeTests.cs` | 가상 클럭 결정론 |

### 플래키 제거 패턴

| 문제 | 해결 |
|---|---|
| 벽시계 타임아웃 | 작업량 경계 (고정 반복) + 가상 클럭 주입 |
| 비동기 완료 대기 | 이벤트/세마포어 시그널, `WaitFor(8000ms)` |
| 동시성 torn-read | seqlock s1==s2 재독출, 작업량 경계 writer |
| 순서 불확정 | 결정론 우선순위, `Ordinal` 비교 |

---

## 10. 버전 이력 (주요 마일스톤)

| 버전 | 내용 |
|---|---|
| v3.4.x | WinForms 9모듈, Blazor Hybrid 이식, DB WAL 하드닝, API 캐시 |
| v3.8~v3.29 | AI 비서, 오버나잇 옵티마이저, Walk-Forward, RAG, MPT, 반사실 트윈 등 |
| v3.34~v3.41 | 로컬 LLM, 멀티에이전트 토론, Blazor DnD 위젯, Inno Setup |
| v4.0 | `TreatWarningsAsErrors` 빌드 하드게이트 |
| v4.47 | S28: AI 포트폴리오 멘토 M1/M2/M3 |
| v4.48.1 | S28-b: 게이트 자해성 동결 제거 (`_writerCollisions` 분리) |
| v4.48.4 | Path A: 전역 플래키 0% (7개 테스트 결정론화) |
| v4.49.0 | Path B: `ShadowClearingBroker` IBroker 데코레이터 + 전용 att DI |
| v4.50.0 | Path C: `OrderDispatchRing` SPMC + `GatedOrderDispatcher` |
| v4.51.0 | Path Beta: `QAdmissionGate` 멀티자산 진입 검문 |
| v4.52.0 | Path Alpha: `ClearingAuthority` 권위 역전 (`QFixedPrimary`) |
| v4.53.0 | Path Gamma: `RingTelemetryChannel` seqlock UI 락-프리 텔레메트리 |
| v4.54.0 | Path Alpha-FX: `ClearBase()` base 통화 권위 계약 (이종통화 역전) |
| v4.55.0 | GatedOrderDispatcher fail-closed 하드닝 + Path Gamma UI 배선 완결 |
| v4.57.0 | 자산 격자 기본 레지스트리 + 3중 안전 게이트 실 무장 술어 + 팔레트 순수 C# 열기 |
| v4.58.0~1 | S28 AI 포트폴리오 멘토 M1/M2/M3 하드닝 + 플래키 0% 박제 |
| v4.59.0 | 진공관 결속 — 무장 술어 스위치 테스트 가능 팩토리 승격 |
| v4.60.0 | ClearingAuthority 권위 역전 컷오버 배선 |
| v4.61.0 | ShadowClearingBroker 권위 채택 load-bearing 가드 하드닝 |
| v4.62.0 | 온보딩 마법사 Blazor 네이티브 + 하이브리드 폼 함정 하드닝 |
| v4.63.0 | δ=0 시계열 차팅 가속 + 다중 seqlock 통합 관제 |
| v4.64.0 | 워치리스트 개인화 + 실시간 지수 스트립 (커스텀 카테고리·DnD·선물 바) |
| v4.65.0 | SymbolPicker 자동완성 + 카테고리 선택 미니모달 |
| v4.66.0 | 모의잔고 원자적 시딩 + 거래 수수료 설정 페이지 |
| v4.67.0 | Blazor DI↔WinForms 브릿지 + 청산 가드 + 파이프라인 하드닝 테스트 |
| v4.68.0 | SymbolPicker mousedown 선택 + IndexTickerBar 마켓 추가 + 병렬 fetch (Sem4·Sem3) |
| v4.69.0 | DashboardHome 통합 관제탑 (Blazor 런처 허브) + IModuleBridge STA 마샬링 |
| v4.70.0 | `OrderDrainWorker` 50ms 드레인 엔진 + 이중 키 잠금(`ValveWired ∧ LiveArmingWired`) 완결 |
| v4.71.0 | `SystemVersionProvider` 무할당 버전 공급자 + HANDOVER.md 대통합 사양 갱신 |
| v4.72.0 | 로우레이턴시 퀀트 커널 3계층: `VectorizedAlphaEngine`(AVX2 SIMD OLS) · `ZeroAllocJsonParser`(hybrid stackalloc/ArrayPool) · `AutonomousAlphaFactory+CartridgeNumericsGuard`(headless mine→attest→guard→sign) · `MacroChaosTailReplayer`(virtual clock 1000x, GFC/LUNA 3σ→ForceFreeze) |
| v4.73.0 | UI 텔레메트리 확장: `RingTelemetry` struct AlphaFactory 카운터(Cycles·Signed·Rejected) 통합 · `RingTelemetryPublisher` AlphaFactory 옵션 배선 · `BlazorHomeForm` AutonomousAlphaFactory singleton 배선(BelowNormal 24h) · `TelemetryMonitor.razor` 🏭 알파 공장 패널(거부율 포함) |
| v4.74.0 | QFixedPrimary 전환 관제탑: `ClearingAuthorityPage.razor`(`/clearing/authority`) 신규 — ShadowCutoverGate Observed/FrozenCount/LastDivergence 300ms 폴링·5조건 승격 체크리스트·가역 SetAuthority 버튼·liveArmed:false 안전 callout · DashboardHome 🔧 청산 권위 카드 추가 |
| v4.75.0 | 4대 진격 과제 검증 스캐폴드(미배선·liveArmed:false): `SafeIgnitionSequencer`(안전 점화 5상태 FSM) · `QKrxTickLadder`+`KrxFxClearing`(KRX 가격대 격자·USD·이종통화 FX) · `KiwoomFreezeRelay`(파이프 생존성 멱등 동결) · `TossLiveBroker`(IBroker·WS→ring·ShadowClearing). 워크플로 적대검증 fail-open 4건 교정 반영 |
| v4.76.0 | 과제1 배선: `SafeIgnitionSequencer` BlazorHomeForm 결속(격리 PaperBroker·이중키 술어) + 12 테스트(dead-on-arrival 교정·해피패스·fail-closed) |
| v4.77.0 | 과제2 배선: ShadowCutoverGate 레지스트리 `BuildAugmentedRegistry`(가격대별 정확 KRX 격자 보강) + 26 테스트(KRX 사다리 경계·★INERT 게이트 FX fail-closed 회귀잠금) |
| v4.78.0 | 과제3 배선: `KiwoomFreezeRelay` 결속(ProxyLost/Error 만 구독·freeze-storm 회피) + `ModuleBridge` STA 하드닝(x86 폼 파괴 레이스 방어) + 10 테스트(★FillReceived 미구독 회귀잠금) |
| v4.79.0 | 과제1 UI: `/command` 점화 제어반에 🚀 안전 점화 시퀀스 원샷 트리거 버튼 추가(`SafeIgnitionSequencer.BeginIgnition` 결속·결과/상태 표시·이중키 미충족 시 거부) |
| v4.80.0 | 로드맵 1단계: 토스 인증 격리 — `TossTokenService`(락-프리 토큰 수명주기·핫패스↔갱신 분리·연속실패 임계 동결·만료 경계 fail-closed) + `ApiKeyVault.SaveToss/GetToss` + `BrokerHub.CreateToss`(옵트인 조립·ShadowClearing 래핑). +15 테스트. liveArmed:false 불변 |
| v4.81.0 | 로드맵 3단계: 실시간 FX 서킷 브레이커 — `QFxCircuitBreaker`(10분 창 3σ 급변동·1s 스테일 → ForceFreeze·래치·시간+용량 축출 링) + `IFxRateSource`/`QFxFeedPump`(폴→Submit) + `QAcl.TryImportRate`. 가드 조회가 유일 청산 진입점(신선·닫힘 환율만 KrxFxClearing 로). +13 테스트. liveArmed:false 불변 |
| v4.82.0 | 로드맵 2단계: 키움 x86 결속 검증 + COM 이벤트 핸들러 GC 방어. ★결정: COM 은 별도 x86 프로세스(KiwoomProxy) 격리 유지(메인 AnyCPU/64비트 불변) — WebView2+COM 동거 압박 구조적 차단. §2.2/§2.3 은 v4.78.0 완비. §2.1 델타: `BridgeSerializer` δ=0 span `Pack`(무할당) + `KiwoomProxyHost` 재사용 버퍼·`_sendLock`(per-event 할당 0 + 동시 파이프 write 레이스 차단). +4 테스트. liveArmed:false 불변 |
| v4.83.0 | 프로덕션 결속(3과제) + 4렌즈 적대검증 6교정. ①토스 실 WSS: `TossProductionSocket`(ClientWebSocket δ=0 수신·핸드셰이크 401/403·정책종료·공백토큰 fail-closed→ForceFreeze) + `TossHttpTransport` 401/403 동결 + `TossProductionAssembler`(옵트인). ②키움 IPC 핑퐁: `Ping/Pong`+`PackSeq`, `KiwoomBridgeClient` 클라 Ping·`_sendGate`·`_pingBuf`(δ=0)·Pong→생존성, `KiwoomProxyHost` Pong 응답, `ExecutionBridgeBroker.CreateFast`(20ms/50ms) → 무응답→ProxyLost→`KiwoomFreezeRelay`→EmergencyFreeze. `ProductionKiwoomApi`(#if KIWOOM_COM·기본 제외·SDK 게이트 axKHOpenAPI 어댑터). ③실 FX: `HttpFxRateSource`(HTTP 폴·dot-path·fail-soft) → `QFxFeedPump`가 `QAcl.TryImportRate`→`SubmitRaw` 결선. +25 테스트. liveArmed:false 불변 |

---

## 11. 빌드 & 실행

### 빌드

```powershell
# 앱 종료 필수 (MS2E.StockSuite.exe가 출력 바이너리 잠금 → MSB3027/3021)
dotnet build StockManager/StockManager.csproj -c Release
```

### 테스트

```powershell
dotnet test StockManager.Tests/StockManager.Tests.csproj --logger "trx" -c Release
# TRX 파싱으로 실제 실패 테스트명 추출 (플래키 census)
```

### 키움 32-bit 실거래 빌드

```xml
<!-- StockManager.csproj: AnyCPU 주석 처리 후 해제 -->
<PlatformTarget>x86</PlatformTarget>
```

### 의존 패키지 (주요)

```
Microsoft.Data.Sqlite 8.0.0
QuestPDF 2024.10.1
System.Security.Cryptography.ProtectedData 8.0.0
Microsoft.AspNetCore.Components.WebView.WindowsForms 8.0.10
xunit / xunit.runner.visualstudio (테스트)
```

---

## 12. 알려진 함정 모음

| 항목 | 내용 |
|---|---|
| **Razor 한글 식별자** | `@멤버한글` → 컴파일 에러. `@(...)` 래핑 필수. 빌드 `-m:1` |
| **Blazor 라우트 중복** | `@page` 중복은 빌드 통과 후 앱 시작 시 "routes are ambiguous"로 죽음. 추가 전 grep |
| **Razor 포맷 문자열** | `@(v:+0.00)` 불가. `.ToString("fmt")` 사용 |
| **Razor `<style>`** | `@@keyframes` (이중 이스케이프) |
| **Write 제어문자** | 구분자 등 제어문자는 `\uXXXX` 이스케이프 (직접 입력 시 빈 문자열로 저장) |
| **SQLite 소수 친화도** | `INTEGER` 컬럼에 0.13 저장 → 0 잘림. REAL 타입으로 마이그레이션 |
| **IME 비동기 재렌더** | 폴링+비동기=입력 리셋/손상. 비제어입력+`@key`+InvariantCulture 파싱 |
| **앱 실행 중 빌드** | exe 잠금 → MSB3027. 종료+빌드를 한 줄로 묶어 첫 번만 확인 |
| **CS0420 경고** | `volatile` 필드 + `Interlocked.*` → `TreatWarningsAsErrors` 빌드 실패. `ref` 지역변수 패턴 |
| **CS8983** | struct 이니셜라이저 경고 → 필드 직접 초기화 |
| **섀도우 att 공유 금지** | `ShadowCutoverGate`에 라이브 `OrderAttestationService` 공유 금지 (섀도우 동결→라이브 차단 footgun) |
| **FX Clear 권위 역전 미적용** | `Clear(symbol, fx...)` 반환=자산통화. base통화 권위는 `ClearBase()` 사용 |
| **fail-closed 주장 함정** | 안전 펜스 커밋 전 음수/역순/경계/재독출 엣지를 선제 반증 필수 (S25-c·S26-b 2회 연속 major 적발 전례) |
| **알림 정책 비대칭** | 주식 알림=one-shot, 코인 알림=재무장(re-arm). 합치면 한쪽 깨짐 |
| **AlertEngineService 창-열림 가드** | 해당 WinForms 창이 열려 있으면 엔진은 건너뜀 (이중 발동 방지). 가드 제거 시 중복 |
| **DI 수명 Singleton 필수** | `FxConverter`(10분 캐시), KIS 토큰(23시간), `Crypto.State`. Scoped/Transient 시 캐시 무력화 |
| **거래 저장 연쇄** | `Db.AddTransaction` 단독 금지 → 반드시 `PortfolioService.RecomputeAverage` 후속 호출 |
| **키움 32-bit 전용** | COM(axKHOpenAPI)은 32비트 전용 → 메인(AnyCPU/64비트)에 동거 금지. 별도 x86 프로세스 `StockManager.KiwoomProxy`(`PlatformTarget=x86`)로 격리 + NamedPipe 결속. ★메인을 x86 전환 금지(격리 하드닝 역행) — v4.82.0 결정 |

---

## 13. 비즈니스 룰 (깨면 안 되는 것들)

1. **한국 관례 색상**: 상승=빨강(`--up`), 하락=파랑(`--down`). 글로벌(상승=초록) 절대 금지.
2. **시그널 비대칭**: 골든크로스=DB저장+알림(Warning), 데드크로스=저장만(알림 없음).
3. **뉴스 감성 센티널**: `SentimentScore==0` = "미분석". 분석 대상 선별/중립 표시 의존.
4. **세금 시뮬 누적**: `runningRealized`가 250만 공제를 소진. 행별 독립 계산 시 세액 전부 틀림.
5. **USD 보유 환산**: 포트폴리오/총자산 합계는 USD를 환율 KRW 환산 후 합산.
6. **DB가 진실의 원천**: `PaperBroker.Reload()` 읽기 전 필수. 다중 인스턴스 공유.
7. **이터레이터 예외 지연**: `yield` 메서드는 래퍼에서 즉시 인자 검증. 본문 yield 전 검증 시 "열거해야만 예외" 계약 위반.

---

## 14. 코드 탐색 치트시트

```
고정소수점 척추      → Common/QFixed.cs
청산 게이트          → Common/QClearing.cs  (ShadowCutoverGate)
브로커 레이어        → Common/PaperBroker.cs, LiveBrokers.cs, ShadowClearingBroker.cs
주문 증적            → Common/OrderAttestationService.cs
링버퍼 코어          → Common/RingBufferCore.cs
주문 디스패치        → Common/OrderDispatchRing.cs
링 텔레메트리        → Common/RingTelemetry*.cs  (Gamma + v4.73.0 AlphaFactory 카운터 통합)
발주 드레인 엔진     → Common/OrderDrainWorker.cs  (v4.70.0 이중 키 잠금)
버전 공급자          → Common/SystemVersionProvider.cs  (무할당 캐싱)
SIMD OLS 알파        → Common/VectorizedAlphaEngine.cs  (AVX2 두 번 패스)
무할당 JSON 파서     → Common/ZeroAllocJsonParser.cs  (stackalloc/ArrayPool 혼합)
자율 알파 공장       → Common/AutonomousAlphaFactory.cs  (headless 24h 루프 + CartridgeNumericsGuard)
꼬리 리스크 재현기   → Common/MacroChaosTailReplayer.cs  (가상 클럭 1000x, GFC·LUNA 3σ→ForceFreeze)
안전 점화 시퀀서     → Common/SafeIgnitionSequencer.cs  (v4.76.0 배선, 5상태 FSM·격리 프로브·이중키)
KRX 호가 사다리·FX   → Common/QKrxTickLadder.cs  (v4.77.0 배선, KrxTickLadder·KrxFxClearing 독립 fail-closed)
FX 서킷 브레이커     → Common/QFxCircuitBreaker.cs  (v4.81.0, 3σ 급변동·스테일 ForceFreeze·QFxFeedPump 폴)
키움 동결 릴레이     → Common/KiwoomFreezeRelay.cs  (v4.78.0 배선, ProxyLost/Error 멱등 동결)
토스 브로커(스캐폴드) → Common/TossLiveBroker.cs  (v4.75.0, δ=0 인입·데코레이터 계약; v4.83.0 REST 401 동결·PumpLoop livelock 교정)
토스 토큰 수명주기     → Common/TossTokenService.cs  (v4.80.0, 락-프리·핫패스↔갱신 분리·fail-closed·CreateToss 조립)
토스 실 WSS/조립      → Common/TossProduction.cs  (v4.83.0, ClientWebSocket δ=0 수신·인증 fail-closed·TossProductionAssembler 옵트인)
실 FX 공급자         → Common/HttpFxRateSource.cs  (v4.83.0, HTTP 폴·dot-path JSON·fail-soft→브레이커 스테일)
키움 실 COM 어댑터    → KiwoomProxy/ProductionKiwoomApi.cs  (v4.83.0, #if KIWOOM_COM·SDK 게이트·axKHOpenAPI↔IKiwoomApi)
포트폴리오 멘토      → Common/QPortfolioMentor.cs
미래가치 시뮬        → Common/QFutureValue.cs
카오스 진단          → Common/QChaos.cs
점화 잠금            → Common/IgnitionLock.cs
백테스트             → Common/BacktestEngine.cs
전략 유전자          → Common/CartridgeGenome.cs
LLM 연동             → Common/AiService.cs, LlmProviders.cs
포트폴리오 컨텍스트  → Common/PortfolioContext.cs
Blazor 런처 허브      → Blazor/Pages/DashboardHome.razor
Blazor DI 진입점     → Blazor/BlazorHomeForm.cs  (AlphaFactory singleton 배선 포함)
파이프라인 관제탑    → Blazor/Pages/TelemetryMonitor.razor  (/broker/pipeline, 🏭 AlphaFactory 패널)
청산 권위 전환       → Blazor/Pages/ClearingAuthorityPage.razor  (/clearing/authority, v4.74.0)
WinForms 진입점      → Launcher.cs
테스트 전체          → StockManager.Tests/
```

---

## 15. 다음 진격 후보

| 항목 | 설명 |
|---|---|
| **실 무장 옵트인** | `appsettings.json` `Dispatch.ValveWired=true` + `LiveArmingWired=true` 동시 ON → `SafeIgnitionSequencer.BeginIgnition` 으로 안전 점화(현재 둘 다 false). /command 트리거 버튼 UI 미배선 |
| **토스 브로커 결속** | `TossLiveBroker`(v4.75.0 스캐폴드)를 `ShadowClearingBroker`로 래핑 + `ITossTransport` 실 구현(REST/wss·OAuth 확정 필요) |
| **키움 32-bit 실거래** | `PlatformTarget=x86`(현 NamedPipe 분리 권장) + `ExecutionBridgeBroker` 파이프 실 연결 + WebView2 비트니스 확인 |
| **FX 실시간 피드** | `KrxFxClearing` 진입점은 준비됨 — `QFxQuote` 환율 실시간 소스(API) 배선 시 USD 자산 라이브 청산 활성 |
| ~~v4.76–78 과제1·2·3 배선~~ | ✅ 완료 — SafeIgnitionSequencer·KrxFxClearing·KiwoomFreezeRelay 결속 + 회귀 테스트 48건 |
| ~~v4.75.0 4대 과제 스캐폴드~~ | ✅ 완료 — 워크플로 설계+적대검증 4파일(fail-open 4건 교정) |
| ~~v4.74.0 QFixedPrimary 전환 관제탑~~ | ✅ 완료 — `ClearingAuthorityPage.razor` 신규 · DashboardHome 🔧 카드 |
| ~~v4.73.0 AlphaFactory 텔레메트리~~ | ✅ 완료 — `RingTelemetry` struct 확장 · `RingTelemetryPublisher` 배선 · `TelemetryMonitor.razor` 🏭 패널 |
| ~~v4.72.0 SIMD·알파·꼬리~~ | ✅ 완료 — `VectorizedAlphaEngine` · `ZeroAllocJsonParser` · `AutonomousAlphaFactory` · `MacroChaosTailReplayer` |

---

*코드가 권위. 이 문서는 재진입 네비게이션 보조. 특정 모듈 상세는 해당 파일 상단 주석 블록을 최우선 참조.*
