# MS2E StockSuite — 정밀 인수인계 문서

> 기준 버전: **v7.0.0** (2026-07-24)  
> 대상: 신규 개발자 또는 지휘관(gslee@angtec.com)이 컨텍스트 없이 재진입할 때  
> 소스가 권위. 이 문서는 네비게이션 보조다.

---

## 0. 30초 요약

**MS2E StockSuite**는 한국 개인 퀀트 트레이더를 위한 올인원 터미널이다.

- .NET 8 WinForms + Blazor Hybrid (WebView2), SQLite 단일 DB
- 9개 WinForms 모듈 + Blazor 대시보드가 `LocalDatabase`를 공유
- AI 분석/포트폴리오 멘토/미래가치 시뮬/카오스 진단 내장
- **고정소수점(QFixed) 청산 척추** + **seqlock SPMC 링버퍼** + **3중 liveArmed:false 게이트**
- **실거래 섀도우 인입 & 원자적 컷오버 게이트웨이**(IsShadowMode 기본 true=실 소켓 0바이트·2단 서명 컷오버)
- **실전 총점화 3단계 안전 골격**(Genesis 1raw 원장 동기화·Canary 1주 검증·Red Team 킬스위치/데드맨 — 실 실행은 운영자)
- **자율 무정지 복구 & 실전 라이브 감시망**(세션 Coma 감시견·채널 페일오버 exactly-once·쓰레드 아사 센서·적응형 고아주문 자본회수)
- **미시 유동성 슬라이서 & 적응형 오더 스로틀**(L2 5호가 δ=0 슬라이싱·1s/10s rate Hold·독약 유동성 합성 3등급 방어)
- **무정지 전략 핫패치 게이트 & ALC 격리 그리드**(수거 ALC 격리·CAS 래치 원자 스왑·δ=0 blittable 상태 이월·세대 펜스+WeakReference 좀비 어테스테이션)
- **다중 전략 인터리빙 공존 & 실시간 바스켓 리밸런서**(δ=0 락프리 팬아웃·정수 가중치 자본 물리 분할·대향 주문 내부 상쇄·자전거래 차단·교차 전략 전염 방화벽)
- **영지식 초고속 로깅 패브릭 & 커널 지터 센티널**(언매니지드 락프리 링·Brotli .ms2log·QPC 3σ 커널 지터·가역 안전 홀드·디스크 stall/페이지폴트 I/O 역류 방화벽)
- **결정론 타임라인 플레이백 & RIO 커널 바이패스 & CausalWeave 인과 워터마크 & Phase 7 프리플라이트/감사금고**(.ms2log/.ms2dump 나노초 재현·Winsock RIO 무복사 수신+관리형 안심 강등·RIO 오버런/타임워프 단일 인과 퓨즈 통합 방어·하드웨어 캡스 콜드 프리플라이트·append-only SHA-256 감사 원장 — RIO/재생은 운영자 옵트인, 부팅 native 자동 기동 없음)
- 2,144 테스트 클린런, 전역 플래키 0%, 빌드 경고 0

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

### 현황 (v4.96.0)

- **총 테스트**: 1,957 (클린런)
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
| v5.5.0 | ★영지식 초고속 로깅 패브릭 & 커널 지터 센티널(Zero-Alloc Async Compressed Logger & Kernel Jitter Sentinel) — 로그 폭주 속 핫패스 지터 0% 통제·커널/HW 지터 정밀 포획. 과제1 `ZeroAllocAsyncCompressedLogger.cs`(핫패스 32B blittable → 언매니지드 락프리 링[NativeMemory] δ=0 O(1) 푸시·seqlock 무효화-쓰기-커밋·드롭-오래된-것 무대기 → 백그라운드 전용 스레드 Brotli[내장]/XOR/CRC32 프레임 → .ms2log append). 과제2 `KernelJitterSpikeSentinel.cs`(Stopwatch[QPC] 스케줄링 지터 앱 지연 분리·전용 2ms 단측 3σ + GC pause 델타 + NIC drop → 가역 안전 홀드[자기 만료·무장 blockWhile 억제]·지속 시 Downgrade). ★Think Beyond `IoBackpressureFirewall.cs`(디스크 stall 워치독[로거 큐+진행 정체 → ForceFreeze] + 자가계측 페이지폴트 프로브[상주 페이지 QPC 터치 계측 → 스왑아웃 탐지·GC 가드·가역 홀드]). RingTelemetry +8(로그 MB/s·큐·드롭·압축비·지터µs·홀드·NIC·I/O상태·Cell 896→960·101필드) + TelemetryMonitor 📦 패널(청색 정상/황색 [KERNEL JITTER SPIKE DETECTED] 홀드/암흑자색 [DISK I/O STALL] 동결+헥스). +10 테스트(회귀 1). 적대검증 6렌즈 확정 15(중복 제거 6)교정(Timer.Dispose in-flight Poll UAF→WaitHandle 핸드셰이크·Logger Dispose Join 타임아웃 UAF→Join 성공 시만 해제·드레인 프런티어 유실+드롭 미계수→3분기+seqlock 재검증·32B TOCTOU torn→seqlock 무효화 우선+복사후 재검증·페이지폴트 GC/선점 오탐→GC 가드+가역 홀드만·전역 동결이 로그 억제→디스크 fault 로컬 래치 분리). ★liveArmed:false(로거/센티널/방화벽 소켓 0·안전홀드 tighten-only·유닛 실증)·δ=0(로그 푸시 핫패스 힙 0·400k 측정)·결정론(지터 인프라 관측·트레이딩 판정 무유입). 빌드 0/0(메인+x86), 전체 2105 |
| v5.4.0 | ★다중 전략 인터리빙 공존 & 실시간 바스켓 리밸런서(Multi-Strategy Interleaved Mesh) — 격리된 복수 알파 두뇌를 단일 프로세스 안전 공존·매수/매도 미시 상쇄로 거래비용 극한 압축. 과제1 `MultiStrategyInterleavedMesh.cs`(복수 IHotStrategy 레인 단일 OnQuote δ=0 락프리 팬아웃[stackalloc]·전역 예수금 정수 가중치 물리 분할[Repartition floor+잔여 결정론·Σ==전역]·무할당 자본 가드[익스포저 기반·순 포지션 서브버짓 1raw 초과 fail-closed]·ProcessTick 단일 소비자). 과제2 `RealTimeBasketNetter.cs`(동일 틱 대향 주문 내부 상쇄[Crossed 물리 0]·잔여만 사출·왕복 수수료+세 절감·효율 2×상쇄/총 Int128). 과제3 `WashTradeGuard.cs`(마이크로 윈도우 교차 자전거래 심볼 해시 슬롯[packed]·OrderRejected·시각 역행 fail-closed). ★Think Beyond `CrossStrategyContagionFirewall.cs`(3층 봉쇄: ①자본 구획 격벽[봉인 자본 재분배 금지·distributable=전역-격리] ②δ=0 공동낙폭 비트셋 공진[popcount(loss[i]&loss[j])·하드웨어 POPCNT·주도 봉인] ③손실속도 퓨즈→다수 봉인 시 IgnitionLock 전역 백스톱 1회 래치). RingTelemetry +6(활성/봉인/네팅절감/자전거래/공진/자본포화·Cell 832→896·93필드) + TelemetryMonitor 🔀 패널(자본 포화 다중 분할 바·황색 [STRATEGY LAUNCH BUFFER MUTED] 진압·암흑자색 캐스케이드+헥스). +25 테스트(회귀 4). 적대검증 6렌즈 확정 10(중복 제거 6)교정(봉인 후 Repartition 격리 재분배 격벽 붕괴 critical→distributable=전역-격리·예약 반납 워터폴 불일치+잔여 예약 미반납 누수→예약 셀 폐기 익스포저 기반 booking 리팩터·Firewall lost-update fail-open→Interlocked.Exchange·WashGuard 시각역행 fail-open→fail-closed·효율 곱셈 오버플로→Int128·캐스케이드 재발화→1회 래치). ★liveArmed:false(팬아웃/네팅/상쇄/잔여/봉인 전부 무장 게이트 하류·미무장 실주문 0·유닛 실증)·δ=0(핫패스 힙 0·400k 측정)·결정론(정수 가중치·주입 시각·정수 popcount). 빌드 0/0(메인+x86), 전체 2086 |
| v5.3.0 | ★무정지 전략 핫패치 게이트 & ALC 격리 그리드(Hot-Strategy Reload Gate & ALC Isolation Grid) — 메인 어셈블리·브로커 소켓·청산 메쉬가 살아있는 채로 알파 전략 바이너리만 메모리상 무정지 교체. 과제1 `HotStrategyReloadGate.cs`(신규 DLL→서명 검증 fail-closed→수거 가능 AssemblyLoadContext[isCollectible:true] 격리 로드→CAS 래치 구버전 핫패스 차단→Interlocked.Exchange 원자 바인딩→상태 이월→세대 전진→구 ALC Unload·OnQuote 핫패스 δ=0·LoadCollectible 운영자 옵트인·AssemblyDependencyResolver). 과제2 `StrategyStateCarrier.cs`(In-Flight 주문+누적 통계를 blittable StrategyState[C#12 InlineArray Fixed8Long] 값복사로 신버전에 δ=0 원자 소유권 이월·구 ALC 참조 절단으로 언로드 봉쇄 방지·이월 RTT ns). ★Think Beyond `ZombieAssemblyShield.cs`(세대 펜스+WeakReference 수거 어테스테이션 단일 메커니즘 이중 방어: ①세대 펜스 AcceptEvent δ=0 long 비교로 구 세대 고아 이벤트 즉시 거부, ②구 ALC Unload 후 grace 5s 롤링 수거 확인·창 초과 생존 시 좀비 격리+IgnitionLock.Downgrade). RingTelemetry +5(활성 세대·빌드ts·이월 RTT·좀비·고아 거부·Cell 832 유지·87필드) + TelemetryMonitor 🔌 패널(청색 healthy/amber [DYNAMIC RE-INITIALIZATION IN PROGRESS] 스왑/자주색 좀비+64bit 헥스). +23 테스트(회귀 6). 적대검증 6렌즈 확정 3(중복 8·FP 2)교정(LoadCollectible Reload 거부 시 ALC 미언로드 누수→!accepted 시 Unload·SweepOnce 프로덕션 미호출로 어테스테이션/슬롯회수 사문화+16회 초과 허위 좀비→200ms 스윕 펌프+1s 능동 GC 타이머+만원 시 수거분 인라인 회수+유휴 스킵·RecordFill 첫 체결 PeakEquity 미시딩 음수 순자산 유령 낙폭→첫 체결 실측 시딩). ★liveArmed:false(핫스왑 무장 무접촉·교체 전략 신호도 무장 게이트 하류·미무장 실주문 0·유닛 실증·검증기 미배선 시 전 Reload 거부)·δ=0(OnQuote/세대펜스 핫패스 힙 0·전용 스레드 300k 워밍업)·결정론(주입 시계·단조 세대). 빌드 0/0(메인+x86), 전체 2061 |
| v5.2.0 | ★미시 유동성 슬라이서 & 적응형 오더 스로틀(Micro-Liquidity Slicer & Adaptive Order Throttle) — 호가 깊이 µs 분해로 슬리피지 극한 압축·쓰로틀링 한도 우회. 과제1 `MicroLiquiditySlicer.cs`(L2Depth C#12 InlineArray δ=0 5호가·즉시체결 잔량분만 발라 미니 TWAP 분할→OrderDispatchRing 동기 사출·ComputeImbalance/FillableQty/SliceInto/EstimateDumpSlippageBps 힙 0). 과제2 `AdaptiveOrderThrottle.cs`(1s/10s 시간버킷 long[10]·epoch CAS 리셋·상한 90% 도달 Hold·★신규 시간창 rate). ★Think Beyond `ToxicFlowSentinel.cs`(독약 유동성: 비대칭 가속도 단측 3σ + 스프레드 EWMA/RTT 유령 + 북워크 독성 베토 합성 3등급 Clear/Caution/Toxic→축소/홀드+Downgrade·하드 임계로 워밍업 맹점 방어). RingTelemetry +5(비대칭 지수·슬라이스·스로틀·슬리피지 절감 bps·독성·Cell 768→832) + TelemetryMonitor 🚦 패널(매수/매도 양방향 게이지·황색섬광 스로틀·암흑자색 독성 오버레이). +17 테스트(회귀 3). 적대검증 4렌즈 확정 3(FP 7)교정(스로틀 lost-update→원자 패킹 버킷 CAS·Current1sRate 프리즈→Rate1s stale-aware·덤프 슬리피지 얇은 북 과소평가→유령 페널티). ★liveArmed:false(슬라이스도 무장 게이트 하류·미무장 실주문 0·유닛 실증)·δ=0(비대칭/슬라이싱 핫패스 힙 0)·결정론(주입 시계·시간버킷)·fail-closed(90% Hold·Toxic Downgrade). 빌드 0/0, 전체 2038 |
| v5.1.0 | ★자율 무정지 복구 & 실전 라이브 감시망(Autonomous Live Watchdog & Recovery) — 실물 자본 상주 중 물리 인프라 마비·OS 자원 고갈 자율 극복·치료. 과제1 `LiveSessionWatchdog.cs`(20ms 핑퐁 RTT 연속 3회 100ms 초과→Coma 진단 CAS 1회→IgnitionLock.Downgrade+격리 소켓 리셋·ExecutionMicrostructureGuard 연속카운팅 미러·무할당). 과제2 `AutonomousFailoverController.cs`(채널 물리 장애→대안 채널 자율 라우팅·exactly-once=단조 OrderId 단일커서+TryClaimDispatch CAS seen-ring dedup·양방향 무정지 복구·양채널 다운 fail-closed). 과제3 `ThreadStarvationSensor.cs`(ThreadPool 가용성+CLR 지터 무할당 단측 3σ→SetMinThreads 프로비저닝+우선순위 상향·★인프라값 트레이딩 판정 격리). ★Think Beyond `AdaptiveOrphanReaper.cs`(체결/거부 무응답 자본 허공묶임을 실측 RTT 분포 적응형 데드라인 mean+Kσ로 고아 선언→진상 조회 자본 회수 heal→불확정+재수확 초과 fail-closed·'탐지 후 정지' 넘는 자율 힐링). RingTelemetry +7(세션 지연·Coma·페일오버·쓰레드풀 가용성·고아 in-flight·블랙스완·Cell 704→768) + TelemetryMonitor 🐕 패널(청색↔황색섬광↔암적색점멸+헥스). +17 테스트(회귀 3). 적대검증 4렌즈 확정 13(중복제거 5)교정(Reaper publish-before-populate TOCTOU→Claiming 상태·Failover dedup false-negative→재시도 루프·Starvation 0↔미측정 센티넬→_measured·pollMs 무시→_pollMs·Reaper RTT Int128 tear→seqlock). ★liveArmed:false(자율 복구 백만 회에도 이중키∧MasterIgnited 없이 실주문 0·유닛 실증)·δ=0(감시견/페일오버 핫패스 힙 0)·결정론(주입 시계·인프라값 격리). 빌드 0/0, 전체 2021 |
| v5.0.0 | ★실전 총점화(Live Total Ignition) 3단계 안전 골격 완결 — 예약 마일스톤(docs/v5.0.0-PhasedIgnition-Blueprint.md). ★실 접속·자격증명·1주 실 사출은 운영자(사람) 이중키+서명 영역 — 소프트웨어는 안전 골격만(자율 실주문 0). Phase1 `LiveAccountGenesis.cs`(IsShadowMode 하드 검문 하 운영자 쿼리 실 예수금을 라이브 청산 메쉬에 1raw 시딩·`LiveAccountingMesh.GenesisSeedDeposit` 1회성 genesis 래치+`_producing` 상호배제·자격증명 무접촉). Phase2 `CanaryLiveProbe.cs`(실 RTT 100ns→µs 단위 정규화 카나리 검역·건별 net 정산을 백엔드 Remainder Carry Guard `LastFillNetRaw` seqlock 일관 페어와 1raw 대조[`ReconcileDualTrack` 재사용]) + ★Think Beyond A `BlastRadiusLimiter.cs`(검역창 명목 상한을 송출 초크포인트 `GatedOrderDispatcher.Drain`에서 물리 강제·시장가 미상=차단·완전 무장이어도 대형 주문 fat-finger 경성 동결). Phase3 `RedTeamDefense.cs`(`RemoteKillSwitch` 서명 비상정지→ForceFreeze+금고 Seal+소켓 Abort <0.1ms·위조 무동작·수신 표면 미개방 + ★Think Beyond B `DeadmanReverseCutover` 생존신호 소실→자동 섀도우 역-컷오버·CAS 멱등). RingTelemetry +6(Genesis 상태·실 예수금·Canary net 드리프트·블래스트 반경·킬스위치·데드맨·Cell 704 유지·70필드 잔여 2슬롯) + TelemetryMonitor 🎯 실전 총점화 대시보드. +21 테스트. 적대검증 4렌즈 확정 6+자체 교정(★블래스트 시장가 우회→시장가 차단·카나리 torn 스냅샷→seqlock 일관 페어·데드맨 이중 롤백→CAS·ToMicros 오버플로→Int128 clamp·Genesis TOCTOU→_producing 게이트·Cell 주석). ★liveArmed:false(소프트웨어 자율 실주문 0·Genesis 섀도우 전용·킬스위치/데드맨/블래스트는 더 안전한 방향으로만). 빌드 0/0(메인+x86), 전체 2004 통과 |
| v4.97.0 | 실거래 섀도우 인입 & 원자적 컷오버 게이트웨이 완결 — 실 브로커 소켓은 시세·예수금·인증만 살리고 최하위 주문 송출을 `IsShadowMode`(기본 true)면 100% 바이패스(실 소켓 0바이트) + 전용 섀도우 청산 메쉬에 50ms 내 가상 체결 공급(슬리피지 모델). `ShadowExecutionGateway.cs`(구조화 라우팅 `TryRoute`[섀도우=가상 청산 링 분기·δ=0] + 원시 바이트 최후 펜스 `AllowOutboundBytes`[토스 갭 보강·동결 직교 검문] + 50ms 가상 청산 펌프[raw 수량 `OnFill` 직결] + `EnterLive`/`EnterShadow` 원자 volatile flip). `AtomicCutoverGate.cs`(★Think Beyond Gemini: 2단 컷오버 — `PreArm`[`SafeIgnitionSequencer.BeginIgnition` 재사용·이중키∧프로브∧3σ→MasterIgnited·섀도우 유지] → `Cutover`[이중키 재검·백로그 원자 플러시·`EnterLive` 단일 volatile write <1ms]. 자율 카나리 검역[RTT 단측 3σ→가역 되감기] + 듀얼트랙 1raw 대조[|drift|>tol→경성 동결+섀도우 복귀] + 되감기 원자 롤백[`EnterShadow`·재컷오버 가역]). 갭 보강: 토스 `TossHttpTransport.PostOrder/DeleteOrder` 영지식 절연 결속(기존 무보호) + 키움 `ExecutionBridgeBroker.Create` `orderArmed`/`ignitionStage` 주입(F1 미배선 보강). RingTelemetry +5(섀도우모드·가상체결 RTT·컷오버 상태·카나리 편차·듀얼트랙 드리프트·Cell 640→704) + TelemetryMonitor 🕹️ 대시보드(섀도우=청색 [SHADOW FLIGHT ACTIVE] ↔ 컷오버=적색점멸 [PRODUCTION LIVE OPERATION]·2단 서명 컷오버). +26 테스트(회귀 6). 적대검증 5렌즈 확정 4+자체판정 4 교정(DI 이중등록 섀도우 메쉬가 라이브 mesh 덮음→미등록·AllowOutboundBytes 동결 미검 fail-open→직교 검문·듀얼트랙 off-by-one>=tol→>tol·듀얼트랙 동결 섀도우 미복귀→EnterShadow·가상체결 FillQty 절사 코인 유실→raw OnFill 직결·카나리 승격 시드표본 계수→라이브 표본 별도 계수·DeleteOrder 절연갭→공용 펜스·슬리피지 상한 클램프). ★liveArmed:false 이중 차단 인터락(①IsShadowMode 면 실 송출 0바이트[스레드 경합·오염 주문 포함·영지식 격리 샌드박스 펜스 유닛테스트] ②컷오버해도 매 라우팅이 이중키∧MasterIgnited 재평가·미충족/동결이면 실주문 0바이트+fail-closed 동결)·δ=0·결정론·섀도우 메쉬 전용 attestation |
| v4.96.0 | 실전 자본 영점 조절 및 무할당 실시간 청산 메쉬 완결 — 체결시점 이종 통화 제비용 δ=0 정산 + 정밀 잔여 캐리 가드 + 스펙큘러티브 2단계 청산 래치 + 예수금 영점. `LiveAccountingMesh.cs`(국내[원화 정수·매수 위탁·매도 위탁+★거래세+농특세 분리]/해외[달러·비율+센트 고정 혼합 양방향]/코인 δ=0 정산·단일 소스 `FeeTaxSchedule`로 4곳 불일치 상수 정직 통합. ★Think Further 1 정밀 잔여 캐리 가드 — 항목·통화별 독립 캐리[위탁/거래세/농특세/해외위탁/코인]에 floor 잔여 이월·spill → 누적오차 0 수렴. ★Think Further 2 스펙큘러티브 2단계 청산 래치 — 핫패스[단일 디스패치] 매수 CAS 델타 차감[매도 선차감 없음]+값타입 링[RingBufferCore] 발행 δ=0, 회계 스레드[BelowNormal] exact+캐리 확정·매도 대금 credit·평단 장부 영점. 통화별 예수금 셀 분리·보유 초과 매도 fail-closed·SeedPosition). RingTelemetry +5(누적 수수료 KRW/USD·캐리 오차·래치 큐·영점 상태·Cell 576→640) + TelemetryMonitor 📊 패널(소수점 투명·거래세/농특세 분리·녹색 영점 인디케이터·1raw 디싱크 암흑적색 오버레이). +18 테스트(회귀 5). 적대검증 6렌즈 확정 8 교정(공유 캐리 세목 오귀속→항목별 독립 캐리·보유초과 매도 유령현금→fail-closed·매도 수수료 선차감 오탐동결→선차감 제거·통화 commingling→KRW/USD 셀 분리·★OrderRejected freeze-storm 재도입→FillQty>0 필터·외부동결 State 미반영→IsFrozen 반영·_book 레이스→락). ★liveArmed:false(메쉬 IBroker/소켓 참조 0 — 내부 원장만 영점·외부 사출 0바이트) |
| v4.95.0 | 무할당 순환 커널 블랙박스 & 감사 오디트 매그넷 완결 — 무할당 순환 비행 기록계 + 동결 원자 덤프·암호 봉인 + 헥스덤프 감사 UI. `MmfFlightRecorder.cs`(64B `FlightRecordEvent`[Sequential,Pack=1]·사전할당 100k→131072 순환 링·★멀티라이터 tri-state[Interlocked.Increment 인덱스 선점 + volatile State Empty/Writing/Ready]·랩 가드·스냅샷 라이브 Sequence 재독 seqlock·Seal→QuiesceAfterSeal[_inFlight 배수]·Record δ=0). `CrashResilientDumper.cs`(동결 옵저버→봉인+격리 BelowNormal 스레드 덤프·★SHA256 변조탐지+CRC32 오염탐지+XOR LCG 난독·temp→Flush(true)→원자 rename·디스크 예외 시 핸들 Dispose+temp 삭제+영구 Seal+ForceFreeze 무응답 격리·`TryLoadAndVerify` 재로드 검증[오버플로 fail-closed]·`.ms2dump`). `OrderAttestationService` 동결 옵저버 훅(`RegisterFreezeObserver` 멀티캐스트 CAS·옵저버별 try/catch·락 밖 격발·ForceFreeze+Reconcile 공용). 핫패스 5곳(발주·리스크·MMF 발행/소비/오염·뉴스)에 δ=0 `Record` 결속. RingTelemetry +4(포화도·기록속도·덤프상태·봉인유실·Cell 576 유지) + TelemetryMonitor 🗃️ 패널(포화 Pure CSS 바·기록속도) + 동결 시에만 무할당 글리프 헥스덤프 1회 정적 투사(암흑적색 감사모드·GC 폭탄 방지). +22 테스트(회귀 3). 적대검증 6렌즈 확정 12(실질 10)결함 교정(스냅샷 seqlock 복사본-Sequence torn→라이브 재독·Seal 후 커밋→_inFlight 드레인·랩 라이터 덮어씀→랩 가드·덤프실패 상태 은폐→CAS+Failed 최종·SetHandleAsInvalid 핸들누수→Dispose·Reconcile 동결 옵저버 미격발→격발·스캔 하한부재 O(head)→유효창 하한·재로드 count 오버플로 OOM→long 곱 결속·rate 첫샘플 스파이크·게이지 InvariantCulture). ★liveArmed:false(레코더/덤퍼/훅 IBroker·소켓 참조 0 — 관측·영속화만) |
| v4.94.0 | 초고속 집행·리스크 실드 완결 — 발주 전 리스크 실드 + 다중 브로커 섀도우 링 + MMF 제로레이턴시 IPC 패브릭. `PreTradeRiskShield.cs`(락-프리 원자 증거금/당일 이중 예산 — ★reserve→check→compensate CAS 로 TOCTOU 봉쇄·over-admit 수학적 불가·next<init 오버플로 fail-closed·경량 필터↔3-strike ForceFreeze+Downgrade 분리·시장가 오라클/무차입 공매도 가드). `MultiBrokerShadowRing.cs`(`IExtendedExecutionGate:IBroker` 주문ID 왕복 PlaceTracked·AssetClass 라우팅·★2PC 연쇄 롤백 센티널: legB 실패→반대편 취소, 취소 실패=화해불가 노출→Downgrade+ForceFreeze·데드라인 50ms·멱등). `MmfIpcFabric.cs`(MMF 상주 락-프리 SPSC 링·unsafe byte* δ=0·`AllowUnsafeBlocks` 신설·★하트비트 20ms+경계검증+로컬상수 불신+mid-write 포이즌→ForceFreeze("MMF Fabric Corrupted")·인플라이트 드레인 가드로 Dispose use-after-free 차단). RingTelemetry +5(증거금 포화·예약 거부·브로커 링 노드·MMF µs·가드 마스크·Cell 512→576) + TelemetryMonitor ⚡ 코어 패널(포화 Pure CSS 바·µs 타임라인) + 가드 동결 적색 오버레이(사유 하드바인딩). +43 테스트(회귀 2). 적대검증 6렌즈 확정 7(실질 4)결함 교정(페어 느린성공 취소실패 노출 오보→Exposed·MMF 하트비트 타임아웃<Beat주기 허위오염→1000ms 정합·Dispose UAF→드레인 가드·비단조 벽시계→Stopwatch). ★liveArmed:false(실드 admit≠발주·MMF 전송≠브로커 소켓·미무장 게이트 거부 — 이중키∧MasterIgnited∧AlwaysFalse 없이 실주문 0) |
| v4.93.0 | 정보 인텔리전스 레이어 완결 — 실시간 뉴스 레이더 + AI 포트폴리오 멘토. `NewsIntelligence.cs`: δ=0 `NewsIntelParser`(Utf8JsonReader span·숫자전용·★TryGet* 비throw fail-closed — 범위밖 Number 예외 힙할당 차단)·`NewsPayload`(SentimentRaw==0=미분석 격리)·`NewsDispatchRing`(Channel DropOldest)·`SemanticShockMatrix`(β표×`QMath.Notional` 하향 floor)·`SentimentVelocityBreaker`(long[128] 롤링 카운트 창·Int128 분산·하향 3σ→`IgnitionLock.Downgrade`). `PortfolioMentorAdvisor.cs`: `NewsIntelController`(백그라운드 BelowNormal 드레인·EMA 감성[★_ingested 카운트 첫표본 판별]·도착률 링)·`Advise`(결정론 한국어)·`NewsRadarSvg`(네이티브 SVG polyline+닷·★한국색상 하드코딩 #ff5252 빨강/#5082c8 파랑). RingTelemetry +6 + TelemetryMonitor 🧠 패널 + 감성 브레이커 트립 앰버 오버레이. +21 테스트(회귀 4). 적대검증 5렌즈 확정 6(실질 5)결함 교정(파서 예외 힙할당·EMA 영교차 리셋·SVG 서구색·문서 창 오표기·중첩 키 누수). ★liveArmed:false(LLM 은 [ShockClass,Severity] ACL DTO 만·멘토 조언/쇼크 클램프 순수 텍스트·실주문 무접촉)·δ=0 핫패스 격리 |
| v4.92.0 | 프로덕션 자격증명 결속 안전 인프라. `EndpointMutationFirewall`(소스 하드코딩 sandbox/real 도메인 host+port+scheme 화이트리스트 vs 주입 URL 교차검증 — off-by-one·플레이스홀더·평문 다운그레이드·모드↔도메인 불일치·★KIS 시세/주문 모드 drift → ForceFreeze 소켓오픈 무력화). `ZeroLeakProtocolIsolator`(송신 직전 주문 OpCode 스캔 — ★키움 바이너리 프레임 WireType=10 + KIS TR·Toss 경로 ASCII, 미무장 시 소켓 Abort+ForceFreeze+throw; `KiwoomBridgeClient.SendOrderAsync` 결속). `ProductionSwapController`(DI 싱글톤·연결상태 BrokerLinkState·토큰수명·실전 감지 텔레메트리). `ProductionCredentialBinder.WithSecret`(SecretLease δ=0 와이프·주문계열 requireArmed). RingTelemetry +5 + TelemetryMonitor 🔌 패널(실전 적색점멸 HIGH SENSITIVITY·모의 청색). +24 테스트. 적대검증 6렌즈 4결함 교정(바이너리 OpCode fail-open·스킴 다운그레이드·미결속·플레이스홀더). ★liveArmed:false(실 연결 옵트인/#if·주문 자격증명 무장 필요)·실 COM/WSS/FX 런타임은 검증불가 옵트인 |
| v4.91.0 | 결정론적 카오스 테스팅 그리드. `DeterministicVirtualTime`(VirtualClock 단조·시간역전 throw / VirtualScheduler 이산사건 우선순위큐=가상 비동기 지연 큐·maxSteps 행 방어 / DeterministicPrng SplitMix64). `DeterministicChaosGrid`: 시드 PRNG 로 복합 장애(Kiwoom IPC 다운·Toss 401·FX 3σ/스테일) 투하 → `ForceFreeze` 단일 수렴. `VirtualProxyLink`(x86↔AnyCPU IPC 격리 스텁·메모리훼손 0). 디퍼런셜 원장(naive `QMath.Notional` vs 실 SQLite `MeshLedgerSink` per-row 재정수화 ≤1raw)·멱등·fail-open 적발. PBT `Explore`(수천 시드)+`ChaosShrinker`(최소 재현 역추적, sinkFactory 로 fail-open 가능 싱크 스윕). RingTelemetry +4 + TelemetryMonitor 🕹️ 카오스 그리드 패널(레이저 배지·시드 재현). +16 테스트(ConcurrencyStress 편입). 적대검증 6렌즈 확정 0·UNCERTAIN 1 정직 보강(탐지기 실효 음성 테스트·PBT 비공허·RejectedFills 단언). ★liveArmed:false(외부 API 0바이트·순수 인메모리)·시간역전/무한루프 물리 차단 |
| v4.90.0 | 결정론적 웜 스타트 복구 엔진. `DeterministicWarmRestartEngine`(5상태 Idle/Validating/Draining/Escalating/Stable+Frozen). `BeginWarmRestart(서명)`: 동결 전제+서명 인가 → `mesh.Stop`(SingleReader 인계) → `ManualRelease`(Mode=Normal) → 금고 `SecretLease`(requireArmed:false·ArrayPool δ=0) → `LedgerProtectionMesh.RecoveryDrain`(동결창 가격정합성 펜스: 저장가 KRX 격자 미정렬>1raw·시세 괴리 8× → ForceFreeze) → 점화 승격(`SafeIgnitionSequencer.BeginIgnition` 재사용 — 격리 1주 프로브→3σ·지연→MasterIgnited) → Stable. 주문 폭주 방지 '복구기 잠금 펜스': `DispatchArming.ResolvePredicate(blockWhile)` 로 복구 중 `IsRecovering`이면 라이브 무장 술어(→CanRouteToLive) 무조건 false. RingTelemetry +4 + TelemetryMonitor 🛠️ 웜스타트 관제 패널(단계 타임라인 배지·백로그 게이지·서명 입력창). +12 테스트. 적대검증 6렌즈 4결함 교정(실패 수렴 무조건 ForceFreeze·펜스 밴드 저장가 고정·시세 괴리 가드). ★fail-closed 단일 소스·liveArmed:false 불변 |
| v4.89.0 | 포트폴리오 UX(지휘관 요청 4건). ①다축 정렬 `HoldingSort`(평가금·총수익률·일간·가나다·직접설정 / 전체·국내·해외·코인 공통) — 직접설정은 `Db.SetSetting` 영속·`MoveInView` 필터뷰 기준 인접 스왑. ②장 마감 후 시간외/애프터 시세(실측 검증): Naver `overMarketPriceInfo.overPrice`(KRX 시간외 단일가)+하락 방향 필드 교정, Yahoo `includePrePost`+`marketState`(프리/애프터), `Quote.Session` 배지. ③매수/매도/직접등록/수정 → 단일 통합 폼(`_showForm`+`_mode` Buy/Sell/Correct)+모드 전환 탭. ④추가매수 가중평균 병합은 `RecomputeAverage`로 기존 구현(정직 회계) — 통합 폼으로 접근성만 개선. +15 테스트. 적대검증 4렌즈 2결함 교정(필터뷰 직접설정 재정렬·Correct→매수 시장권위). ★liveArmed:false 불변(UI+읽기전용 시세) |
| v4.88.0 | 원장 보호 메시. `LedgerProtectionMesh`(`DbTransactionPayload` [Sequential,Pack=1] unmanaged 57B·참조 0·심볼 KSE 6자리 long → Bounded Channel TryWrite 무할당·논블로킹 핫패스). 전용 드레인 스레드: 큐→`AddTransaction`→★직후 `RecomputeAverage` 동일 스레드 순차(HANDOVER §12 장부/평단 디싱크 차단). fail-closed 2대(큐 90%/포화→ForceFreeze·영속화 예외→ForceFreeze). ★동결 인지 유휴(EmergencyFreeze 시 Enqueue 거부·Drain 즉시 0·잔여 큐 보존 → busy-spin·재시도/감사 폭주 차단). RingTelemetry +4 + TelemetryMonitor 🗄️ 원장 보호 패널(Pure CSS 게이지 0-70녹/70-90황/90+적). +10 테스트. 적대검증 4렌즈 확정 2결함(드레인 busy-spin·재시도 폭주) 교정. ★liveArmed:false 불변(순수 영속화 오프로드·발주/점화 무결합) |
| v4.87.0 | 집행 리스크 통제. `SlippageSentinel` δ=0 고정소수점 3σ(`long[128]`·`RecordAndCheck3Sigma`·dev²>9·var sqrt 회피·워밍업<10 fail-closed) + `IgnitionLock.Downgrade`(한 단 강등: 마스터→프로브) + `ExecutionMicrostructureGuard`(3σ 2연속·레이턴시 100ms→Downgrade) + `OrderDrainWorker` fail-closed(드레인 예외·이중키 붕괴→ForceFreeze 영구 락업·`DrainOnce`). RingTelemetry +4 + TelemetryMonitor ⚡ 미시구조 패널. +18 테스트. 적대검증 4렌즈 결함 0. liveArmed:false 불변 |
| v4.86.0 | 자율 전략 진화 팩토리. `StrategyEvolutionPipeline`(3중 게이트: WalkForward IsRobust·Tournament Rank#1·AlphaEngine TrueAlpha>0 → 서명 → PromotionGateway.Submit=PendingApproval) + `TournamentPipelineMode` FSM(락-프리·Paused 동기화). `AutonomousCartridgeSigner`(금고 SecretLease 키·`SignZeroAlloc` SHA256+SignHash·fail-closed 폐기) + `SecurityAssetVault.Reveal(requireArmed)`. `StrategyActivationGate`(liveArmed:false 증명·기본 false). RingTelemetry +3 + TelemetryMonitor 🧬 패널. +16 테스트. 적대검증 1교정(Gate3 에쿼티 날짜 꼬리정렬). ★자율 승격은 인간 서명 전까지 실주문 미배선 |
| v4.85.0 | 3대 전술 과제(적대검증 4렌즈 결함 0). ①`SecurityAssetVault`(DPAPI + `SecretLease` char[] 풀·ZeroMemory 와이프 + 동결 연동 봉인 래치 + liveArmed Reveal 펜스). ②`KrxFxClearing.TryClearUsdToKrwBaseGuarded`(격자 오염 방화벽 — QScaleGuard 2중 검증·격자 디싱크 ForceFreeze). ③`AlphaRejectionValve`(롤링 거부율 95%↑ 자동 Suspend·수동 Suspend/Resume) + RingTelemetry Amber 필드 + TelemetryMonitor 🏭 Amber Alert. +24 테스트. liveArmed:false 불변 |
| v4.84.0 | Path Gamma UI: 레이턴시·변동성 관제. `LatencySnapshot`(무할당 blittable 57B) + `LatencyTelemetryChannel`(seqlock 미러) + `LatencyTelemetryPublisher`(CAS 단일 라이터). `KiwoomBridgeClient` RTT(seq 슬롯 링 δ=0) + `QFxCircuitBreaker.ReadFxStats`(MA·1σ·상태코드 0/1/2). `LatencyMonitor.razor`(/broker/latency·100ms 폴·fail-closed 오버레이·미배선 정직 표기). BlazorHomeForm 싱글톤+ringTimer 합류. +20 테스트. 적대검증 1교정(σ 미확립 밴드 오도→조건부 표시). liveArmed:false 불변 |
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
FX 서킷 브레이커     → Common/QFxCircuitBreaker.cs  (v4.81.0, 3σ 급변동·스테일 ForceFreeze·QFxFeedPump 폴; v4.84.0 ReadFxStats·상태코드)
레이턴시 텔레메트리   → Common/LatencyTelemetry.cs  (v4.84.0, LatencySnapshot·seqlock 채널·퍼블리셔 — RingTelemetry 미러)
레이턴시 관제 UI     → Blazor/Pages/LatencyMonitor.razor  (v4.84.0, /broker/latency·100ms 락-프리 폴·fail-closed 오버레이)
보안 자산 금고       → Common/SecurityAssetVault.cs  (v4.85.0, DPAPI·SecretLease 메모리 와이프·동결 봉인 래치·무장 펜스)
알파 자기 제어 밸브   → Common/AutonomousAlphaFactory.cs  (v4.85.0, AlphaRejectionValve 롤링 거부율 95%↑ 자동 Suspend)
진화 파이프라인·FSM  → Common/StrategyEvolutionPipeline.cs  (v4.86.0, 3중 게이트·TournamentPipelineMode·StrategyActivationGate liveArmed 증명)
자율 카트리지 서명기  → Common/AutonomousCartridgeSigner.cs  (v4.86.0, 금고 SecretLease 키·SignZeroAlloc·fail-closed 폐기)
집행 미시구조 가드    → Common/ExecutionMicrostructureGuard.cs  (v4.87.0, 슬리피지 3σ 2연속·레이턴시 100ms → IgnitionLock.Downgrade)
슬리피지 δ=0 3σ      → Common/SlippageSentinel.cs  (v4.87.0 가산, long[128] 고정소수점·dev²>9·var·워밍업<10 fail-closed)
원장 보호 메시       → Common/LedgerProtectionMesh.cs  (v4.88.0, blittable 57B·Bounded Channel 무할당 핫패스·드레인 AddTransaction→RecomputeAverage 순차·fail-closed 2대·동결 인지 유휴)
보유 목록 정렬       → Common/HoldingSort.cs  (v4.89.0, 순수·결정론 다축 정렬 평가금/수익률/일간/가나다ko-KR/직접설정 + MoveInView 필터뷰 재정렬)
시간외/애프터 시세    → Common/QuoteProviders.cs  (v4.89.0, Naver overMarketPriceInfo.overPrice KRX 시간외·하락부호 교정 / Yahoo includePrePost pre·post — Quote.Session)
웜 스타트 복구 엔진    → Common/DeterministicWarmRestartEngine.cs  (v4.90.0, 5상태 시퀀스·동결창 가격정합성 펜스·복구기 잠금 펜스·SafeIgnitionSequencer 재사용·FailClosed 단일 ForceFreeze)
원장 복구 드레인      → Common/LedgerProtectionMesh.cs  (v4.90.0 RecoveryDrain: Stop 후 단일리더 인계 순차 드레인+펜스 훅 — v4.88.0 메시에 가산)
가상 시간 패브릭      → Common/DeterministicVirtualTime.cs  (v4.91.0, VirtualClock 시간역전 throw·VirtualScheduler 이산사건 큐 maxSteps 행 방어·DeterministicPrng SplitMix64)
카오스 테스팅 그리드   → Common/DeterministicChaosGrid.cs  (v4.91.0, 시드 결함 인젝터·VirtualProxyLink 격리스텁·디퍼런셜 원장 ≤1raw·PBT Explore+ChaosShrinker·In/MeshLedgerSink)
프로덕션 스왑 안전 인프라 → Common/ProductionCredentialBinding.cs  (v4.92.0, EndpointMutationFirewall host+port+scheme+KIS drift·ZeroLeakProtocolIsolator 바이너리/ASCII 주문 OpCode 절연·ProductionSwapController·ProductionCredentialBinder SecretLease δ=0)
뉴스 인텔리전스 코어    → Common/NewsIntelligence.cs  (v4.93.0, δ=0 NewsIntelParser TryGet* fail-closed·NewsPayload 미분석 격리·NewsDispatchRing·SemanticShockMatrix β 하향 floor·SentimentVelocityBreaker long[128] 하향 3σ→IgnitionLock.Downgrade)
뉴스 멘토·SVG 레이더   → Common/PortfolioMentorAdvisor.cs  (v4.93.0, NewsIntelController 백그라운드 드레인·EMA 감성·PortfolioMentorAdvisor 결정론 한국어·NewsRadarSvg 한국색상 하드코딩 #ff5252/#5082c8; liveArmed:false 순수 텍스트/그림자 가치)
발주 전 리스크 실드     → Common/PreTradeRiskShield.cs  (v4.94.0, 락-프리 원자 증거금/당일 이중 예산 예약·TOCTOU-free reserve→check→compensate CAS·오버플로 fail-closed·3-strike ForceFreeze+Downgrade·무차입 공매도/시장가 오라클 가드; liveArmed:false admit≠발주)
다중 브로커 섀도우 링   → Common/MultiBrokerShadowRing.cs  (v4.94.0, IExtendedExecutionGate:IBroker 주문ID 왕복·AssetClass 라우팅·2PC 연쇄 롤백 센티널·취소 실패=Exposed→Downgrade+ForceFreeze·단조 Stopwatch 데드라인)
MMF 제로레이턴시 패브릭 → Common/MmfIpcFabric.cs  (v4.94.0, MMF 상주 락-프리 SPSC 링·unsafe byte* δ=0·하트비트 20ms+경계검증+mid-write 포이즌→ForceFreeze("MMF Fabric Corrupted")·인플라이트 드레인 가드; AllowUnsafeBlocks 신설·intra-machine IPC 라 브로커 소켓 미오픈)
무할당 순환 블랙박스     → Common/MmfFlightRecorder.cs  (v4.95.0, 64B FlightRecordEvent·사전할당 100k 순환 링·멀티라이터 tri-state[Interlocked.Increment 선점+volatile State]·랩 가드·스냅샷 라이브 Sequence 재독 seqlock·Seal+QuiesceAfterSeal·Record δ=0·FlightHexRenderer 무할당 글리프)
동결 원자 덤프·암호봉인   → Common/CrashResilientDumper.cs  (v4.95.0, ForceFreeze 옵저버→봉인+격리 스레드 덤프·SHA256+CRC32+XOR 봉인·temp→원자 rename·디스크 예외 시 핸들 Dispose+Seal+무응답 격리·TryLoadAndVerify 재로드 검증[오버플로 fail-closed]·.ms2dump; liveArmed:false 관측·영속화만)
실시간 청산 메쉬(회계)   → Common/LiveAccountingMesh.cs  (v4.96.0, 체결시점 국내/해외/코인 제비용 δ=0 정산[거래세·농특세 분리·US 혼합]·FeeTaxSchedule 단일소스·정밀 잔여 캐리 가드[항목·통화별 독립·누적오차 0 수렴]·스펙큘러티브 2단계 청산 래치[핫 CAS 델타+회계 스레드 확정]·통화별 예수금 셀·보유 초과 매도 fail-closed·SeedPosition; 브릿지 FillReceived 결속은 FillQty>0 필터[freeze-storm 방지]; liveArmed:false IBroker/소켓 참조 0)
실거래 섀도우 게이트웨이 → Common/ShadowExecutionGateway.cs  (v4.97.0, 런타임 IsShadowMode 원자 volatile flip[기본 true]·구조화 TryRoute[섀도우=가상 청산 링 분기·δ=0·이중키∧MasterIgnited∧미동결만 Live]·원시 바이트 최후 펜스 AllowOutboundBytes[ZeroLeakProtocolIsolator 재사용·동결 직교 검문·토스 갭 보강]·50ms 가상 청산 펌프[raw 수량 OnFill 직결·슬리피지 SplitMix64 시드]·FlushShadowBacklog[펌프와 _pumpBusy 상호배제]; 섀도우면 실 소켓 0바이트)
원자적 컷오버 게이트     → Common/AtomicCutoverGate.cs  (v4.97.0 ★Think Beyond, 2단 컷오버 PreArm[BeginIgnition 재사용·섀도우 유지]→Cutover[이중키 재검·백로그 플러시·EnterLive <1ms]·자율 카나리 검역[CanaryRttSentinel 단측 3σ·컷오버 후 라이브 표본만 계수→가역 되감기]·듀얼트랙 1raw 대조[|drift|>tol→경성 ForceFreeze+EnterShadow]·되감기 원자 롤백[EnterShadow·점화 FSM 유지=재컷오버 가역]·MmfFlightRecorder 저널; liveArmed:false 이중 차단 인터락)
실전 계좌 결속(Genesis)   → Common/LiveAccountGenesis.cs  (v5.0.0 Phase1, IsShadowMode 하드 검문[TOCTOU 재검] 하에서만 운영자 쿼리 델리게이트로 실 예수금/미수한도→LiveAccountingMesh 1raw 시딩·자격증명/소켓 무접촉[값만 수신]·쿼리실패/음수/2회차/체결후/동결 fail-closed; LiveAccountingMesh.GenesisSeedDeposit 1회성 genesis 래치+_producing 상호배제)
카나리 종단 검증(Canary) → Common/CanaryLiveProbe.cs  (v5.0.0 Phase2, 실 RTT 100ns ticks→µs 단위 정규화[Int128 clamp]→자율 카나리 검역 공급·건별 net 정산을 mesh.TryReadLatestNet[seqlock 일관 (seq,net) 페어]로 취득해 1raw 대조[ReconcileDualTrack 재사용]·seq dedup; liveArmed:false 관측·대조·검역 위임만)
블래스트 반경 제한기     → Common/BlastRadiusLimiter.cs  (v5.0.0 ★Think Beyond A, 검역창 명목 상한 락-프리 물리 강제·GatedOrderDispatcher.Drain armed 후·onSend 전 초크포인트·★시장가[PriceRaw=0] 상한 활성 중 차단·초과=경성 fail-closed 동결·δ=0)
Red Team 방어(킬/데드맨) → Common/RedTeamDefense.cs  (v5.0.0 Phase3, RemoteKillSwitch[서명 검증→ForceFreeze+SecurityAssetVault.Seal+소켓 Abort <0.1ms·위조 무동작·수신 네트워크 표면 미개방] + ★Think Beyond B DeadmanReverseCutover[운영자 생존신호 Beat 데드라인 소실→자동 RollbackToShadow·CAS 멱등 전이·주입 시계]; 둘 다 더 안전한 방향으로만)
세션 하트비트 감시견     → Common/LiveSessionWatchdog.cs  (v5.1.0 과제1, 20ms 핑퐁 RTT[KiwoomBridgeClient.CurrentIpcRttTicks] 폴→연속 3회 100ms 데드라인 초과 Observe→Coma CAS 1회 전이→IgnitionLock.Downgrade+격리 소켓 리셋 콜백·ExecutionMicrostructureGuard 연속카운팅 미러·δ=0 판정·주입 시계·인프라값 트레이딩 판정 무유입)
자율 채널 페일오버       → Common/AutonomousFailoverController.cs  (v5.1.0 과제2, ReportChannel→Reconcile[Primary 우선복구·양채널 다운 None+ForceFreeze]·ResolveChannel 락-프리·exactly-once=단조 OrderId 단일커서 1차보증+TryClaimDispatch CAS seen-ring dedup 백스톱·양방향 무정지 복구; 실 송출은 하류 ZeroLeakProtocolIsolator/게이트웨이 펜스 지배)
쓰레드 아사 센서         → Common/ThreadStarvationSensor.cs  (v5.1.0 과제3, ThreadPool.GetAvailableThreads 가용성+Stopwatch 지터 단측 3σ[SlippageSentinel 링 이식]→아사 CAS 전이→provision[SetMinThreads]+boost 콜백·회복 restore·★인프라 전용[QMath/주문 판정과 완전 직교]·δ=0)
적응형 고아주문 사냥꾼   → Common/AdaptiveOrphanReaper.cs  (v5.1.0 ★Think Beyond, Register[CAS 슬롯]/Resolve[RTT 적재]δ=0·SweepOnce[age>적응형데드라인 mean+Kσ→reconcile 3분기: NotFilled→reclaim heal·Filled→해제·Unknown+age>reap→ForceFreeze]·실측 RTT 분포 적응형·주입 시계; 관측·조회·회수 신호·동결만 — 실주문 0)
미시 유동성 슬라이서     → Common/MicroLiquiditySlicer.cs  (v5.2.0 과제1, L2Depth[C#12 InlineArray Fixed5Long·δ=0 5호가]·ComputeImbalance/ComputeFillableQty/SliceInto/EstimateDumpSlippageBps 힙0 순수연산·SliceAndDispatch[독성 베토 축소/홀드→스로틀 TryAcquire→OrderDispatchRing.PublishRaw]·즉시체결 잔량분만 미니 TWAP 분할; 무장 게이트 하류·흐름 축소만)
적응형 오더 스로틀       → Common/AdaptiveOrderThrottle.cs  (v5.2.0 과제2, long[10] 시간버킷·epoch CAS 리셋·TryAcquire[1s=현버킷·10s=유효합·둘다 90% 미만만 허용·아니면 Hold]·주입 tick 초 버킷·δ=0; IP 밴 방지 흐름 억제만)
독약 유동성 센티널       → Common/ToxicFlowSentinel.cs  (v5.2.0 ★Think Beyond, 비대칭 가속도 |Δimb| 단측 3σ + 스프레드 EWMA×3 확대∨RTT>deadline 유령 + 하드임계[|imb|>0.9Scale] 합성 3등급·Toxic→IgnitionLock.Downgrade+슬리피지 보수화·Caution→절반 축소·ToxicityAdjustedFillable[100/50/0%] 북워크 베토·δ=0 링·주입 시계)
무정지 전략 핫패치 게이트 → Common/HotStrategyReloadGate.cs  (v5.3.0 과제1, IHotStrategy 계약·OnQuote[Volatile.Read _active+가상호출+long 반환·동결 시 0·δ=0 핫패스]·Reload[서명검증 fail-closed→CAS 래치 _swapLatch→Interlocked.Increment 세대→shield.Advance→carrier.Handover→Interlocked.Exchange _active→구 ALC Unload+WeakReference+shield.RegisterUnload]·LoadCollectible[운영자 옵트인·수거 ALC·verifyFile fail-closed·!accepted 시 alc.Unload]·StrategyLoadContext:AssemblyLoadContext[isCollectible:true]+AssemblyDependencyResolver; 교체 전략도 무장 게이트 하류·미무장 실주문 0)
전략 상태 이월 캐리어     → Common/StrategyStateCarrier.cs  (v5.3.0 과제2, blittable StrategyState[Generation·RealizedPnl·MaxDrawdown·PeakEquity·CumFills·InFlightCount·Fixed8Long InFlightOrderIds]·TrackInFlight[8건 상한 초과 드롭]/ReleaseInFlight[스왑 삭제]/RecordFill[첫 체결 PeakEquity 시딩·낙폭]/Handover[세대 재태그·RTT ns 주입시계]/Snapshot[값복사]·구 ALC 타입 참조 절단으로 언로드 봉쇄 방지·δ=0 lock 직렬화)
좀비 어셈블리 실드       → Common/ZombieAssemblyShield.cs  (v5.3.0 ★Think Beyond, Advance[세대 전진]·AcceptEvent[δ=0 long 비교로 구 세대 고아 이벤트 거부·OrphanEventsRejected]·RegisterUnload[Pending 슬롯·만원 시 수거분 인라인 회수 후 재시도·진짜 만원만 격발]·SweepOnce[유휴 스킵·driveGc 배출 유도·grace 5s 초과 생존→EscalateZombie→IgnitionLock.Downgrade]·프로덕션 펌프=ringTimer 200ms+전용 1s 능동 GC 타이머[BlazorHomeForm])
다중 전략 인터리빙 메쉬   → Common/MultiStrategyInterleavedMesh.cs  (v5.4.0 과제1, Lane[]{GenomeId·WeightBps·SubBudgetRaw·ExposureRaw[순 포지션 원장]·QuarantinedRaw·Strategy·Carrier·State[Active/Muted/Sealed]}·AddLane/Repartition[distributable=전역-격리·floor+잔여 최고가중치 귀속·Σ==distributable]·ProcessTick[stackalloc 팬아웃→Net→게이트→BookFills·단일 소비자 δ=0]·AdmitCapital[익스포저 기반 tentative·|proj|>budget fail-closed]·BookFills[상쇄분 항상+잔여분 게이트통과 시 체결·예약 셀 없음]·SealLane[봉인 자본 QuarantinedRaw 격리·재분배 금지]·AdvanceRiskEpochAndEvaluate[방화벽 봉인 적용+생존만 재분할]; 잔여도 무장 게이트 하류·미무장 실주문 0)
실시간 바스켓 네터       → Common/RealTimeBasketNetter.cs  (v5.4.0 과제2, Net[ReadOnlySpan 총매수/매도·crossed=min·residual=순·SavedCost=상쇄명목×왕복bps·δ=0 checked]·NettingEfficiencyBps[2×crossed/gross Int128·≤10000]·누적 Interlocked; 상쇄분 물리 사출 0=수수료·세 절감)
자전거래 차단 가드       → Common/WashTradeGuard.cs  (v5.4.0 과제3, Admit[심볼 Knuth 해시 슬롯·packed long(side<<62|ticks)+심볼 배열·대향+윈도우 내 or 시각역행(dt<0) → 거부 fail-closed·Volatile 락프리 δ=0]·거래소 자전거래 셧다운 방어·드레인 전 무효화)
교차 전략 전염 방화벽     → Common/CrossStrategyContagionFirewall.cs  (v5.4.0 ★Think Beyond, MarkResult[핫·손실 비트·익스포저]·AdvanceEpoch[Interlocked.Exchange 원자 read-clear·비트셋 좌시프트·연속낙폭]·Evaluate[δ=0 쌍별 popcount 공진→주도 봉인·연속낙폭 속도퓨즈·캐스케이드→IgnitionLock.Downgrade 1회 래치]; 봉인은 mesh 가 자본 격벽으로 적용)
무할당 압축 로거         → Common/ZeroAllocAsyncCompressedLogger.cs  (v5.5.0 과제1, BlittableLogEvent[32B·Sequential Pack=1]·언매니지드 링[NativeMemory.AlignedAlloc·_slotSeq 커밋 마커]·Log[Interlocked 클레임+seqlock 무효화-쓰기-커밋·δ=0·드롭-오래된-것 무대기]·IoLoop[전용 스레드·25ms 인터럽터블]·DrainOnce[3분기 커밋/드롭/정지+복사후 재검증]·PersistBatch[Brotli 압축→XorKeystream→CRC32 프레임→.ms2log·실패 시 _diskFaulted 래치+onDiskFault ForceFreeze]·Dispose[Join 성공 시만 AlignedFree — 타임아웃 시 누수 허용])
커널 지터 센티널         → Common/KernelJitterSpikeSentinel.cs  (v5.5.0 과제2, Poll[20ms·Stopwatch(QPC) 스케줄링 지터=실제-기대 간격]·단측 3σ 링[_jit[128]·Int128 _sumSq·dev²>9var]·GC pause 델타[GC.GetTotalPauseDuration]·NIC drop[IPv4 IncomingPacketsDiscarded 3s 샘플]·ApplyVerdict[가역 안전 홀드 _safeHoldUntilTicks 자기만료·지속 시 1회 래치 Downgrade]·SafeHoldActive=무장 blockWhile 억제; 지터 인프라 관측·트레이딩 무유입)
I/O 역류 방화벽          → Common/IoBackpressureFirewall.cs  (v5.5.0 ★Think Beyond, CheckDiskStall[로거 큐 고수위+진행 정체 → ForceFreeze 1회 래치·파국]·페이지폴트 프로브[상주 NativeMemory 영역 페이지 터치 QPC 브래킷 → 스왑아웃 지연 스파이크·GC 가드로 선점/GC 오탐 제거·가역 홀드만]·Dispose[Timer.Dispose(WaitHandle) in-flight Poll 대기 후 Free — UAF 차단]; HoldActive=무장 blockWhile)
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
