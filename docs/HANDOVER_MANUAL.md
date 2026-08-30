# MS2E StockSuite — 마스터 개발자 인수인계서 (HANDOVER_MANUAL)

> **문서 등급**: 온보딩(Onboarding) · 신규/인수 개발자 필독
> **적용 버전**: v11.4.1 (master) · 삼중 빌드 0W/0E · 2,4xx 유닛 테스트 100% Pass 기준
> **연계 문서**: [`docs/CANARY_DEPLOYMENT_RUNBOOK.md`](CANARY_DEPLOYMENT_RUNBOOK.md) · [`docs/CODEMAP_MIGRATION.md`](CODEMAP_MIGRATION.md) · [`src/README.md`](../src/README.md)

---

## 0. 이 문서를 읽는 법

이 매뉴얼은 신규 개발자가 **환경 구성 → 아키텍처 이해 → 안전한 기여 → 프로덕션 운용**까지 자력으로 도달하도록 쓰였다.
가장 먼저 §1(퀵스타트)로 앱을 세우고, §2(불변)를 암기하고, §3~§5(아키텍처/UI 이주)로 기여 방법을 익힌 뒤, §6~§8(보안/카나리)로 실거래 운용을 학습한다.

### ★ 절대 불변 (물리적 제약 — 어떤 PR도 위반 불가)

| 불변 | 의미 | 강제 수단 |
|---|---|---|
| **`liveArmed:false`** | 앱은 실 발주가 **물리적으로 불가능**한 상태로 부팅 | `SecurityAssetVault.Reveal(requireArmed)` · `CanaryHealthSentinel.Arm(이중 키)` · `OnboardingHealthSentinel` 부팅 자세 |
| **fail-closed** | 미상/오류 = 안전측(거부·빈 데이터) | 전 Port 어댑터 · 센티널 |
| **무할당(δ=0) 핫패스** | 핫패스에서 GC 힙 할당 0 | `[Collection("ConcurrencyStress")]` δ=0 테스트 |
| **결정론** | 핫 커널에 시계/난수 0 | 순수 함수 커널 + 주입식 probe |
| **이중 빌드 0W/0E** | Default·WebOnly 무경고·무오류 | CI 삼중 빌드 게이트 |
| **한국 관례 색상** | 상승/이익=**빨강** · 하락/손실=**파랑** | 코드 리뷰 · CSS 변수 `--up`/`--down` |

---

## 1. 퀵스타트 (신규 PC 30분 세팅)

### 1.1 선행 요건
- **.NET 8 SDK** (`dotnet --info` 로 8.x 확인) · Windows 10/11 (WinForms 호스트 + DPAPI).
- **x86 빌드 지원**(키움 COM 프록시용) · Git.

### 1.2 클론 & 빌드 & 실행
```bash
git clone <repo> && cd StockManager
dotnet build -c Debug              # 전체 솔루션(호스트+RCL+테스트)
dotnet run --project StockManager  # WinForms + Blazor Hybrid 앱 기동
```

### 1.3 최초 로그인
- 시드 관리자 계정: **ID `GSLEE` / PW `1234567890`** (최초 기동 시 `MemberRegistryService` 가 자동 시드).
- 로그인 후 좌측 내비의 **🗺️ 코드맵 뷰어**(`/tools/codemap` → [`ProjectCodeViewer.razor`](../StockManager/Blazor/Pages/ProjectCodeViewer.razor))에서 소스 레이어 분포를 실시간 확인.

### 1.4 ★ 온보딩 헬스 체크 (자동)
부팅 시 [`OnboardingHealthSentinel`](../src/02_Infrastructure/Engine/OnboardingHealthSentinel.cs)가 환경을 자동 진단하고 결과를 로그(`[Onboarding] readiness=…`)로 투사한다. `Blocked` 판정이면 **구체적 해결 힌트**가 함께 출력된다(§9 참조). 환경 오류 시 이 로그를 최우선 확인하라.

### 1.5 검증(기여 전 반드시 통과)
```bash
dotnet build -c Release
dotnet build -c Release -p:WebOnly=true
dotnet build StockManager.KiwoomProxy -c Release   # x86 골든마스터(TreatWarningsAsErrors)
dotnet test  -c Release                            # 전체 유닛 스위트
```

---

## 2. 저장소 지도 (어디에 무엇이 있는가)

| 프로젝트(`.csproj`) | 어셈블리 | 역할 |
|---|---|---|
| `StockManager` | **MS2E.StockSuite** | 호스트(합성 루트). 물리적으로 `src/01_Core`·`src/02_Infrastructure` 폴더 + `Blazor/` UI 를 포함하는 모놀리식. RCL 참조. |
| `MS2E.UI.Portable` | MS2E.UI.Portable | **순수 UI(RCL·net8.0)**. Ports/DTO + 이주된 페이지. **★엔진 무참조**(구조적으로 호스트를 못 봄). |
| `StockManager.Tests` | — | xUnit 유닛·통합·δ=0 증명. |
| `StockManager.KiwoomProxy` | — | **x86** 키움 OpenAPI COM 프록시(별도 프로세스·`TreatWarningsAsErrors`). |
| `StockManager.PrimesBench` | — | 벤치/삼중 빌드 3번째 축. |

> **⚠️ 정직한 현재 상태**: [`src/README.md`](../src/README.md) 의 5대 레이어(`01_Core`~`05_Tests`)는 **목표 구조**다. `01_Core`·`02_Infrastructure` 는 실제 소스 폴더(호스트 어셈블리에 컴파일)지만, `03_UI.Portable`·`04_Host`·`05_Tests` 는 현재 **README 스캐폴드**이고 실코드는 각각 루트의 `MS2E.UI.Portable`·`StockManager`·`StockManager.Tests` 에 있다. 물리 이주 절차는 [`docs/CODEMAP_MIGRATION.md`](CODEMAP_MIGRATION.md) 참조. **레이어 의존 규칙 자체는 논리적으로 이미 강제된다**(§3).

---

## 3. 5대 레이어 & 의존 방향 (Clean Architecture)

의존은 **안쪽으로만** 향한다. [`ArchitectureFitnessAnalyzer`](../src/02_Infrastructure/Engine) (유닛 테스트 [`Phase112ArchitectureFitnessTests`](../StockManager.Tests/Phase112ArchitectureFitnessTests.cs))가 이 규칙을 **자기강제**한다 — 위반 시 테스트가 실패한다.

| 레이어 | 역할 | 참조 가능 |
|---|---|---|
| `01_Core` | 순수 도메인·엔티티·불변 DTO·Port 인터페이스 | Core |
| `02_Infrastructure` | 외부 어댑터·HW(SIMD)·보안(금고)·텔레메트리·영속화 | Core, Infra |
| `03_UI.Portable` | 순수 Blazor UI(RCL) | Core, UI (**★엔진 무참조**) |
| `04_Host` | WinForms/Blazor Hybrid 합성 루트·네비게이션 | 전부 |
| `05_Tests` | 유닛·통합·δ=0 무할당 증명 | 전부 |

- **RCL 순수성 강제**: `UiAssemblyPurity` 불변식([`Phase94UiLiberationTests.Portable_Assembly_Has_Zero_Engine_Reference`](../StockManager.Tests/Phase94UiLiberationTests.cs))이 `MS2E.UI.Portable` 의 엔진(MS2E.StockSuite/StockManager*) 참조 0을 CI로 강제한다.
- **코드맵**: 라이브 분포/오배치는 앱 내 🗺️ 코드맵 뷰어(`/tools/codemap`), 이주 매핑은 [`docs/CODEMAP_MIGRATION.md`](CODEMAP_MIGRATION.md).

---

## 4. Ports & Adapters — UI를 엔진에서 격리하는 법 (기여 핵심)

UI 컴포넌트가 엔진 콘크리트(`LocalDatabase`·정적 싱글턴·`IQuoteProvider` 등)에 직접 닿으면 RCL 로 이주할 수 없다(=PortBlocked). 이주는 **DIP 슬라이스**로 한다.

### 4.1 정석 5단계 (Phase 94-G 알림 센터를 실제 예시로)

1. **Port 인터페이스 + RCL DTO 를 RCL에 신설** — 엔진 타입 무참조.
   예: [`MS2E.UI.Portable/Ports/Alerts/AlertsPorts.cs`](../MS2E.UI.Portable/Ports/Alerts/AlertsPorts.cs) — `IAlertFeedPort`, 읽기 계약 `IAlertRecord`, RCL 열거형 `AlertSeverity`/`AlertOrigin`.
2. **엔진 타입이 읽기 계약을 공변 구현**(선택 — 무할당 프로젝션).
   예: 엔진 `AlertItem : IAlertRecord`(열거형만 명시 구현) → 어댑터가 `List<AlertItem>` 을 `IReadOnlyList<IAlertRecord>` 로 **사본 0** 노출(`IReadOnlyList<out T>` 공변성).
3. **호스트 어댑터가 Port를 콘크리트에 결속**.
   예: [`AlertFeedAdapter`](../src/02_Infrastructure/Engine/AlertFeedAdapter.cs) — 정적 `AlertCenter.Instance` 를 감싸고 엔진 이벤트를 RCL `Action<IAlertRecord>` 로 재발화.
4. **DI 배선**([`BlazorHomeForm.cs`](../StockManager/Blazor/BlazorHomeForm.cs)):
   ```csharp
   services.AddSingleton<MS2E.UI.Portable.Ports.Alerts.IAlertFeedPort,
                         MS2E.Infrastructure.AlertFeedAdapter>();
   ```
   > ★ 이 배선을 빠뜨리면 컴파일은 통과해도 그 페이지가 **런타임 화이트스크린**(미해결 `@inject`)으로 죽는다. `OnboardingHealthSentinel` 이 부팅 시 이를 검출한다(§9).
5. **컴포넌트를 RCL로 `git move`** 하고 `@page` 를 제거, **얇은 호스트 shim** 으로 라우트 보존.
   - RCL: [`MS2E.UI.Portable/Pages/Alerts/AlertPage.razor`](../MS2E.UI.Portable/Pages/Alerts/AlertPage.razor) (`@inject IAlertFeedPort Feed`).
   - 호스트 shim: [`AlertsHostPage.razor`](../StockManager/Blazor/Pages/AlertsHostPage.razor) = `@page "/alerts"` + `<MS2E.UI.Portable.Pages.Alerts.AlertPage/>`.
   - 라우터는 `AppAssembly` 만 스캔하므로(추가 어셈블리 미등록) **RCL @page 는 자동 라우팅되지 않는다 → shim 필수**.

### 4.2 라우팅 보존 규칙
- 호스트 `Router`(App.razor)는 `AppAssembly="@typeof(App).Assembly"` — 호스트 어셈블리의 `@page` 만 인식.
- 따라서 이주 페이지는 **호스트 `XxxHostPage.razor`(@page 보유) → RCL 컴포넌트(완전수식)** 패턴으로 라우트를 보존한다(예: `Admin/Forge/Optimizer/Signal/BacktestLab/DualLive/Alerts…HostPage`).

---

## 5. Pure Host Shell 래칫 (회귀 방지 게이트)

- **목표**: 호스트를 순수 셸(Program.cs·App.razor·라우팅 shim·DI)로 수렴시킨다.
- **측정**: [`PureShellFitnessSentinel`](../src/02_Infrastructure/Engine/PureShellFitnessSentinel.cs) 가 호스트 어셈블리를 스캔해 **결합 UI**(`[Inject]`/`[CascadingParameter]` 보유 `ComponentBase` = PortBlocked) 수를 집계.
- **단조 래칫**: `RatchetCeiling`(**현재 81**, v11.4.1 실측) 초과 시 CI 거부([`Phase114PureShellFitnessTests`](../StockManager.Tests/Phase114PureShellFitnessTests.cs)). **이주가 진행될 때마다 이 값을 낮춘다**(역행 불가).
- 분류 도우미: `UiPortabilityAnalyzer`(LeafReady / PortBlocked / HostShell).
- **기여 규칙**: 새 결합 UI 를 호스트에 **늘리지 말 것**. 페이지 추가 시 §4 슬라이스로 RCL 에 두거나, 불가피하면 래칫 상향을 **정당화**하라(리뷰 필수).

---

## 6. 보안 — 자격증명 봉인 & at-rest 암호화

### 6.1 SecurityAssetVault (DPAPI 자격 금고)
[`src/02_Infrastructure/Security/SecurityAssetVault.cs`](../src/02_Infrastructure/Security/SecurityAssetVault.cs)

- **at-rest 암호화**: API 키/시크릿은 **DPAPI**(Windows 사용자 프로파일 결속)로만 저장 — **평문 저장 금지**.
- **Span 기반 무평문 노출**: `Store(key, ReadOnlySpan<char>)` / `Reveal(key) → SecretLease`. `SecretLease.Value` 는 `ReadOnlySpan<char>` 이고 `Dispose()` 시 버퍼를 소거 — **plain `string` 을 힙에 남기지 않는다**.
- **★liveArmed 게이트**: `Reveal(key, requireArmed: true)` 가 기본. `Func<bool> isLiveArmed` 이 참이 아니면 실 시크릿 노출을 거부(fail-closed). `Seal()`/`IsSealed` 로 금고를 봉인.

### 6.2 QuantumVault — 무장 상태 & 안티탬퍼
- `AdminCommandKind.QuantumVaultArm/Disarm`([`AdminPortAdapters.cs`](../src/02_Infrastructure/Adapters/Brokers/AdminPortAdapters.cs))로 금고 무장 상태(`_vaultArmed`)를 원자적으로 토글.
- **능동 방어**: `QuantumVaultSentinel`(디버거 탐지) + [`HoneyKeyDecoyMesh`](../src/02_Infrastructure/Adapters/Brokers/HoneyKeyDecoyMesh.cs)(디코이 카나리 무결성 δ=0 대조 — 메모리 인젝션/패칭 시 트립) 로 공격면을 덮는다.
- 런타임 스냅샷 `RuntimeConfigSnapshot(ValveWired, LiveArmingWired, Var99LimitKrw, QuantumVaultArmed)`([`InfraLiberationSlice2.cs`](../src/02_Infrastructure/Engine/InfraLiberationSlice2.cs)).

### 6.3 배포본 보안 불변
- 배포 인스톨러/빌드에 **사용자 데이터·자격증명 미포함**.
- API 키는 항상 DPAPI at-rest(평문 금지). 시크릿은 Span 으로만 다루고 즉시 소거.

---

## 7. 이중 실통신 파이프라인 (Kiwoom x86 COM × Toss OAuth REST)

### 7.1 키움 (x86 COM 프록시)
- 키움 OpenAPI 는 **32비트 COM** 이라 64비트 호스트와 **별도 x86 프로세스**([`StockManager.KiwoomProxy`])로 격리, IPC 브리지로 통신.
- 클라이언트: [`KiwoomBridgeClient`](../src/02_Infrastructure/Adapters/Brokers/KiwoomBridgeClient.cs) — `IsProxyAlive`·RTT/pong 하트비트.
- IPC 계약 파일은 호스트/프록시가 **링크 공유**(경로 오류 주의 — 과거 v11.2.0 이주 잔재로 링크 깨짐 이력). 프록시는 `TreatWarningsAsErrors` **골든마스터**이므로 0W/0E 필수.

### 7.2 토스 (OAuth 2.0 REST)
- **자격 모델**: 로그인 ID/PW 가 **아니라** 발급받은 **App Key/Secret**(`client_id`/`client_secret`) 기반 **OAuth2 client_credentials**.
- 토큰: [`TossTokenService`](../src/02_Infrastructure/Adapters/Brokers/TossTokenService.cs)(`SecondsUntilExpiry()` 만료 관제) · 실통신 어댑터 [`TossOpenApiLiveAdapter`](../src/02_Infrastructure/Adapters/Brokers/TossOpenApiLiveAdapter.cs) · 429 레이트리밋은 `TossLiveApiGovernor.Http429`.

### 7.3 이종 브로커 UI 디스패처 격리
- [`CrossBrokerDispatchCoalescer`](../MS2E.UI.Portable/Infrastructure/CrossBrokerDispatchCoalescer.cs) — δ=0 락프리. 한 브로커 라인의 지연/단선이 다른 라인의 UI 라이브니스를 막지 않도록 `SafeHold` 래치로 격리.
- 이중 관제 대시보드: [`DualLiveBrokerControlPanel`](../MS2E.UI.Portable/Pages/BrokerControl/DualLiveBrokerControlPanel.razor)(조회 전용 `IDualBrokerStatusPort` — 발주/변조 메서드 0).

---

## 8. 카나리 운용 (실거래 4단계) — 요약

전체 절차는 [`docs/CANARY_DEPLOYMENT_RUNBOOK.md`](CANARY_DEPLOYMENT_RUNBOOK.md). 여기서는 인수인계 핵심만.

| Stage | 목표 | 핵심 게이트 |
|---|---|---|
| **1 · Mock** | 실 발주 0에서 이중 조회·관제 정상 + 발주 100% Veto 감사 | `liveArmed:false` · `TryAuthorize(any)==0` |
| **2 · Micro-Arming** | 물리 이중 키로 **총자산 0.1%** 소액 카나리 | `Arm(cap, valveWired, liveArmingWired)` |
| **3 · Interlock** | 이상(429/레이턴시/슬리피지/단선) → **50ms 내 Disarm** | `NoteAnomaly(reason)` → `Kill` sticky |
| **4 · Rollout** | 0.1%→1%→5%→25%→100% 단계 상향 | 스텝별 재무장(독립) |

### 8.1 ★ 물리 이중 키 (liveArmed:false 의 물리적 구현)
- 실 발주는 **`ValveWired ∧ LiveArmingWired`(둘 다 배선)** 일 때만 가능.
- [`CanaryHealthSentinel.Arm(cap, valveWired, liveArmingWired)`](../src/02_Infrastructure/Engine/CanaryHealthSentinel.cs) 는 **두 키가 모두 true 가 아니면 no-op**(disarmed 유지) → `TryAuthorize()` **항상 0**. **소프트웨어로 물리 키를 시뮬레이트하지 않는다.**

### 8.2 ★ 0ms 원자 킬스위치 & 토큰 0x00 소거
- 누적 인가액이 cap 초과 시(단 1회라도) 또는 `NoteAnomaly` 트리거 시 `Kill(reason)`:
  1. `_armed=0`(즉시 무장 해제) · `_killLatch=1`(sticky — 재무장 전까지 전량 0).
  2. 발주 토큰을 **`CryptographicOperations.ZeroMemory` 로 0x00 소거**(메모리 잔재 0).
- δ=0 락프리 CAS 로 다중 발주 스레드에서도 **한도 누출 0**.
- 재개는 자동 금지 — 원인 규명 후 **물리 이중 키로만 재무장**.

---

## 9. 온보딩 헬스 센티널 (신규 개발자 환경 방어)

[`OnboardingHealthSentinel`](../src/02_Infrastructure/Engine/OnboardingHealthSentinel.cs) — 부팅 시 결정론·δ=0·fail-closed 로 6개 점검:

| 점검 | 심각도 | Blocked 시 힌트 요지 |
|---|---|---|
| `DotnetRuntime` | Blocking | .NET 8 SDK/런타임 설치 |
| `DataDirectoryWritable` | Blocking | `%LOCALAPPDATA%\MS2E` 쓰기 권한 |
| `DpapiAvailable` | Blocking | Windows 프로파일에서 실행(at-rest 불변) |
| `KiwoomProxyPresent` | Advisory | x86 프록시 빌드(모의/토스만이면 무시 가능) |
| **`LiveArmingSafePosture`** | Blocking | ★부팅 시 무장 흔적 = `liveArmed:false` 위반 |
| **`MigratedPortBindings`** | Blocking | ★이관 컴포넌트 Port DI 배선 누락 → 화이트스크린 |

- **구조**: 콜드 조립기 `Diagnose(probe)` + **δ=0 순수 커널 `Evaluate(span)`**. `IOnboardingProbe` 로 환경 I/O 를 추상화 → 테스트가 결정론적으로 재현.
- **fail-closed**: 빈 입력/미상/오조립 = `Blocked`. Blocking 미통과 1건이라도 = `Blocked`.
- **배선**: 부팅 시 로그 투사(공유 primary 보호 위해 하드킬 대신 정직 표출). 프로덕션 호스트는 `Blocked` 를 **안전 거부** 신호로 강제한다.

---

## 10. 테스트 & 검증 관례

### 10.1 δ=0(무할당) 테스트 패턴
```csharp
[Collection("ConcurrencyStress")]   // ★타이밍/δ=0/멀티스레드 신규 테스트 필수
public sealed class XxxZeroAllocTests {
    [Fact] public void HotPath_ZeroAllocation() {
        // 전용 스레드 + 대량 워밍업(300k) 후 100k 측정 → Debug/Release 양쪽 안정
        long before = GC.GetAllocatedBytesForCurrentThread();
        /* 핫 루프 */
        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
```
- 인라인 단순 δ=0 은 Debug 에서 tiered-JIT/OSR 노이즈(예: 24바이트)가 있어 **Release 로 검증**. 전용 스레드+워밍업 패턴은 양쪽 안정.

### 10.2 릴리스 게이트(완료 선언 전 필수)
- 삼중 빌드 0W/0E: Default · `-p:WebOnly=true` · PrimesBench.
- KiwoomProxy 골든마스터 0W/0E(`TreatWarningsAsErrors`).
- 전체 스위트 `-c Release` 100% Pass(스레드/IPC 타이밍 크래시는 Release 에서만 드러날 수 있음).
- `Phase112ArchitectureFitnessTests`(레이어) · `Phase94UiLiberationTests`(RCL 0 Engine Ref) · `Phase114PureShellFitnessTests`(래칫 ≤ 81) PASS.

### 10.3 SIMD 주의
- 개발/CI 호스트가 AVX2·AVX-512 미지원일 수 있음 → SIMD 는 **fallback 티어로만 CI 검증**, AVX-512 경로는 `IsSupported` 가드 필수.

---

## 11. 코딩 관례 요약

- **한국 관례 색상**: 상승/이익=**빨강**(`--up`), 하락/손실=**파랑**(`--down`). (알림 심각도 등 도메인 색은 기존 의미 보존 — 임의 변경 금지.)
- **한글 `.ps1`**: UTF-8 **BOM 필수**(PS5.1 CP949 파싱 실패 회피).
- **관리 타입 stackalloc 금지**: `string` 등 관리 필드 포함 struct 는 stackalloc 불가(CS0208) → 사전 할당 버퍼. unmanaged(enum/숫자/bool) 구조체만 stackalloc.
- **공유 primary**: 저장소는 다중 세션 공유 — **선별 스테이징**, 내 변경만 커밋(무관 변경 격리).

---

## 12. 자주 찾는 곳 (파일 지도)

| 찾는 것 | 위치 |
|---|---|
| DI 합성 루트 | [`StockManager/Blazor/BlazorHomeForm.cs`](../StockManager/Blazor/BlazorHomeForm.cs) |
| 라우터/셸 | `StockManager/Blazor/App.razor` · `_Imports.razor` |
| RCL 순수 UI | `MS2E.UI.Portable/Pages/**` · `Ports/**` · `Components/**` |
| 호스트 라우팅 shim | `StockManager/Blazor/Pages/*HostPage.razor` |
| 보안 금고 | `src/02_Infrastructure/Security/SecurityAssetVault.cs` |
| 브로커 어댑터 | `src/02_Infrastructure/Adapters/Brokers/**` |
| 자율 방어 센티널 | `src/02_Infrastructure/Engine/{CanaryHealthSentinel,PureShellFitnessSentinel,OnboardingHealthSentinel}.cs` |
| 카나리 운용 | [`docs/CANARY_DEPLOYMENT_RUNBOOK.md`](CANARY_DEPLOYMENT_RUNBOOK.md) |
| 레이어 이주 | [`docs/CODEMAP_MIGRATION.md`](CODEMAP_MIGRATION.md) · 앱 내 🗺️ `/tools/codemap` |

---

> ⚠️ **최종 경고**: 이 매뉴얼의 어떤 절차도 `liveArmed:false` 불변을 우회하는 수단이 될 수 없다. 물리 이중 키는 소프트웨어로
> 시뮬레이트하지 않으며, 카나리·금고 센티널은 물리 무장 열쇠 없이 절대 작동하지 않는다(유닛 테스트로 상시 보증).
