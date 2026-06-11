# 📦 MS2-E Stock Suite — 개발 인수인계 문서 (HANDOVER)

> 작성: 이 세션의 작업 결과 인수인계용 · 대상 솔루션: `StockManager_v3.4.1`
> 이 문서 하나로 **현재 상태 파악 → 빌드/실행 → 이어서 작업**까지 가능하도록 작성했습니다.

---

## 0. 30초 요약 (TL;DR)

이 저장소에는 **두 갈래의 작업**이 들어 있습니다.

1. **MS2-E Stock Suite (주식·코인 관리 앱)** — 기존 **WinForms 앱을 Blazor Hybrid로 이식**하던 프로젝트. 이번 세션에서 "이식 과정에서 누락되기 쉬운 숨은 비즈니스 룰"을 원본(v3.0)과 대조 감사하고, 누락분을 Blazor 측에 구현했습니다.
2. **Primes (소수 유틸리티 모듈)** — 사용자 요청으로 **새로 추가한 독립 수학 모듈**. 주식 앱과 기능적 연관은 없으며, `Common/`에 들어 있는 순수 정적 유틸 + 전용 테스트/벤치 프로젝트입니다.

**현재 상태:** 메인 앱 빌드 ✅ (오류 0·경고 0) · Primes 테스트 ✅ **105/105 통과** · 벤치 ✅ 실측 완료.

```bash
# 빌드/테스트/실행 (작업 디렉터리: StockManager_v3.4.1)
dotnet build StockManager                                  # 메인 앱
dotnet test  StockManager.Tests                            # 소수 테스트 105개
dotnet run   --project StockManager.PrimesBench -c Release # 소수 벤치마크
dotnet run   --project StockManager                        # 앱 실행(Blazor, WebView2 필요)
dotnet run   --project StockManager -- --classic           # 구 WinForms 런처로 실행
```

---

## 1. 솔루션 구조

`.sln` 파일은 **없습니다.** 3개 프로젝트가 ProjectReference로 연결되어 있어 개별 빌드/테스트가 가능합니다. (통합 빌드를 원하면 `dotnet new sln` 후 3개 프로젝트를 add 하세요.)

```
StockManager_v3.4.1/
├─ HANDOVER.md                  ← (이 문서)
├─ StockManager/                ← 메인 앱 (WinForms + Blazor Hybrid)
│  ├─ Program.cs                ← 엔트리포인트 (앱/엔진 기동)
│  ├─ StockManager.csproj       ← net8.0-windows, WinExe, AssemblyName=MS2E.StockSuite
│  ├─ Common/                   ← 공용 엔진·서비스·모델 (핵심 로직 대부분)
│  ├─ Blazor/                   ← Blazor Hybrid UI (Dashboard, Pages/, 서비스)
│  ├─ 01_RealtimeWatcher ~ 09_CompositeAlerts/  ← 레거시 WinForms 모듈 창
│  ├─ wwwroot/                  ← index.html, app.css, dashboard.js
│  └─ _design_prototype/        ← ⚠️ 컴파일 제외된 HTML 목업 (SortableJS는 여기만 잔존)
├─ StockManager.Tests/          ← xUnit 테스트 (현재 Primes 전용, 105 케이스)
└─ StockManager.PrimesBench/    ← 무의존 Stopwatch 벤치마크 콘솔
```

### 1.1 아키텍처 한눈에

- **하이브리드 호스팅:** `Program.cs` → `BlazorHomeForm`(WinForms 폼) 안에 `BlazorWebView`를 띄워 Blazor UI를 렌더. WebView2 런타임 실패 시 자동으로 구 `LauncherForm`(WinForms)으로 폴백. `--classic` 인자로 강제 가능.
- **단일 진실 소스 = SQLite DB.** `LocalDatabase` (`%LocalAppData%\StockManager\portfolio.db`). WinForms 창과 Blazor가 **같은 DB**를 공유합니다. 설정/상태도 `Settings` 테이블(Key-Value) 및 JSON(`Crypto.State`, `Paper.Account` 등)에 저장.
- **ModuleBridge:** Blazor에서 레거시 WinForms 모듈 창을 그대로 여는 다리. 아직 Blazor로 이식 안 된 화면(설정 마법사, 거래 설정, 복합 알림 관리 등)은 이 경로로 네이티브 창을 띄웁니다.
- **백그라운드 엔진:** `CompositeAlertService`(복합 알림) + `AlertEngineService`(가격 알림·지정가 체결)가 앱 시작 시 상시 기동되어, 모듈 창이 닫혀 있어도 동작합니다.

---

## 2. Workstream 1 — Blazor 이식 감사 & 구현

### 2.1 배경

원본 WinForms 앱(`StockManager_v3.0`)에는 UI 이벤트에 얽힌 **숨은 비즈니스 룰**이 많았고, Blazor 이식본(v3.4.1)에서 다수가 누락되어 있었습니다. 4대 관점(① 이벤트 연쇄 ② 재진입 가드·숨은 상태 ③ 조건부 UI ④ 실패 복구·더티 체크)으로 전수 대조 감사 후, 누락/변형분을 Blazor에 구현했습니다.

### 2.2 이번 세션에 구현한 것 (라운드별)

| 라운드 | 구현 | 핵심 파일 |
|---|---|---|
| 1차 | 백그라운드 알림·체결 엔진, PaperBroker 재로드, USD 환산, 테마 동기화, 팔레트 매크로, 뉴스 중복 제거 | `Common/AlertEngineService.cs`(신규), `Common/ThemeManager.cs`, `Common/NewsService.cs`, `Blazor/DashboardService.cs`, `Blazor/CommandPalette.razor`, `Program.cs` |
| 2차 | 거래기록→평단 재계산 연쇄, 포트폴리오 5탭(보유/이력/세금/배당/상관), 실시간 워치리스트·갱신주기·플래시·52주·알림규칙, 시그널 영속+알림, 뉴스 감성분석 | `Common/PortfolioService.cs`(신규), `Blazor/Pages/PortfolioPage.razor`, `Blazor/Pages/RealtimePage.razor`, `Blazor/Pages/NewsPage.razor`, `Blazor/Pages/AlertHistoryPage.razor` |
| 3차 | 차트 보조지표(RSI/MACD/Stoch/볼린저), 백테스트 엔진, 김치 프리미엄 | `Common/IndicatorMath.cs`(신규), `Common/BacktestEngine.cs`(신규), `Blazor/Pages/SignalPage.razor`, `Blazor/Pages/CoinPage.razor`, `Common/CryptoProviders.cs` |
| A3 | **SortableJS(외부 DnD 라이브러리) 제거** → 순수 HTML5 네이티브 DnD 위젯 + 헤더 딥링크 | `Blazor/Dashboard.razor`, `wwwroot/index.html`, `wwwroot/dashboard.js`, `wwwroot/app.css` |
| A4 | **액션형 전역 Toast 서비스**(버튼 델리게이트 + 자동 닫힘) | `Blazor/ToastService.cs`(신규), `Blazor/ToastHost.razor`(신규), `Blazor/MainLayout.razor`, `Blazor/BlazorHomeForm.cs` |

### 2.3 핵심 컴포넌트 설명

- **`Common/AlertEngineService.cs`** — 10초 주기 백그라운드 평가. ① 코인 지정가 알림(재무장 정책) ② 주식 AlertRules(one-shot 정책) + 5% 급변 감지 ③ 모의투자 지정가 자동 체결. **해당 WinForms 창이 열려 있으면 그 항목 평가를 건너뜀**(이중 발동 방지). `Program.cs`에서 `Start(db, provider)` / 종료 시 `Stop()`.
- **`Common/PortfolioService.cs`** — `RecomputeAverage(db, symbol, fallbackName)`. 거래 INSERT 후 호출하면 **전체 거래 이력을 시간순 재집계**해 보유 수량·평단을 갱신(이동평균법). 전량 매도 시 평단 0, 보유 0 상태의 매도는 무시.
- **`Common/IndicatorMath.cs`** — 차트용 RSI/MACD/Stochastic/볼린저%B/EMA. ⚠️ 차트용 RSI(`avgL==0→rs=100`)와 백테스트용 RSI(`l==0→100`)는 **공식이 다름** — 섞지 말 것(주석 명시).
- **`Common/BacktestEngine.cs`** — 5전략(이평/RSI/볼린저/MACD/볼륨) + 비용 모델(`TradingCost.Default`: 수수료·매도세·슬리피지) + Equity Curve/MDD/Sharpe/승률. 기간 종료 시 강제 청산.
- **`Blazor/DashboardService.cs`** — Blazor 화면의 데이터 허브(싱글톤). `LoadAsync()`가 환율·주식·코인·모의계좌를 한 번에 적재. `PersistCrossSignal`이 시그널을 DB 저장+알림(매수만). 코인 알림 매크로 헬퍼(`AddCoinAlert`/`DisableAllCoinAlerts`/`SetTelegramEnabled`). 테마 setter는 `ThemeManager.Set`로 일원화.
- **`Blazor/ToastService.cs` + `ToastHost.razor`** — 전역 액션형 토스트. `ToastButton(label, Func<Task>, Primary)` 델리게이트 지원. `MainLayout.razor`에 `<ToastHost />` 마운트, `BlazorHomeForm.cs`에 싱글톤 DI 등록.

### 2.4 ⚠️ 절대 깨면 안 되는 숨은 룰 (Gotchas)

이어받는 분이 리팩토링하다 가장 쉽게 망가뜨릴 지점들입니다.

1. **알림 정책 비대칭** — 주식 알림 = **one-shot**(발동 즉시 `IsTriggered=1` 영구 비활성), 코인 알림 = **재무장(re-arm)**(가격이 조건을 벗어나면 다시 무장). 두 정책을 하나의 알림 서비스로 합치면 한쪽이 깨집니다.
2. **AlertEngineService 창-열림 가드** — 코인/실시간/모의투자 WinForms 창이 열려 있으면 그 창이 처리하므로, 엔진은 해당 항목을 **건너뜁니다**. 가드를 제거하면 알림·체결이 이중 발생합니다.
3. **DI 수명 = Singleton 필수** — `FxConverter`(환율 10분 캐시), KIS 토큰(23시간 캐시), `Crypto.State` JSON. Scoped/Transient로 바꾸면 캐시 무력화 → 환율 폭주 호출·토큰 재발급(레이트리밋 위반).
4. **테마 토글 순서 계약** — `ThemeManager.Set()`은 ① 저장 → ② `ReapplyOpenForms`(표준 컨트롤) → ③ `Changed` 이벤트(커스텀 폼 자기 색 확정) 순. 순서를 바꾸면 커스텀 색이 덮입니다.
5. **USD 보유 환산** — 포트폴리오/총자산 합계는 USD 종목을 **환율로 KRW 환산 후 합산**. 미환산 시 원화·달러가 섞입니다(원본 `KrwValue` 룰).
6. **세금 시뮬 행 간 누적** — `runningRealized`가 평가이익 내림차순으로 250만 공제를 소진(USD만 누적). 행별 독립 계산으로 바꾸면 두 번째 행부터 세액이 전부 틀립니다.
7. **거래 저장 연쇄** — `Db.AddTransaction` 단독 호출 금지. 반드시 `PortfolioService.RecomputeAverage`를 이어서 호출해야 보유 현황이 갱신됩니다.
8. **DB가 진실의 원천** — 다중 인스턴스(Blazor·WinForms 창·백그라운드 엔진)가 같은 DB/Settings를 공유. `PaperBroker.Reload()`를 읽기 전에 호출해 최신화하는 패턴 유지.
9. **한국 관례 색상** — 상승=빨강(`--up`), 하락=파랑(`--down`). 글로벌 관례(상승=초록)로 바꾸지 말 것.
10. **시그널 비대칭** — 골든크로스 = DB 저장 + 알림(Warning), 데드크로스 = 저장만(알림 없음).
11. **뉴스 감성 센티널** — `SentimentScore == 0`은 "미분석" 표시. 분석 대상 선별/중립 표시 모두 이 값에 의존.
12. **이터레이터 예외 지연** — `SegmentedSieveStream` 같은 `yield` 메서드는 인자 검증을 **래퍼에서 즉시** 수행(내부 로컬 함수만 yield). 검증을 yield 본문에 넣으면 "열거해야만 예외"가 되어 계약이 깨집니다.

### 2.5 남은 작업 / 알려진 한계 (Workstream 1)

- **레거시 위임 화면:** 설정 마법사(`SetupWizard`), 거래 설정(`BrokerSettingsForm`), 복합 알림 관리, 알림 이력(WinForms) 등은 아직 `ModuleBridge.Open`으로 **네이티브 WinForms 창**을 띄웁니다. Blazor 네이티브 페이지로 이식하면 일관성이 좋아집니다.
- **포트폴리오 입력 이원화:** `PortfolioPage`에 ⓐ 매수/매도 **거래 기록**(→ Transactions + RecomputeAverage)과 ⓑ **직접 등록/수정**(→ Holdings 직접 CRUD, 거래 이력 미생성)이 공존합니다. 의도된 설계지만, ⓑ로 수정하면 거래 이력과 평단이 어긋날 수 있음을 인지할 것.
- **차트 상호작용:** RealtimePage 차트에 지표·MA·거래량은 구현됐으나, 원본 WinForms `ChartForm`의 크로스헤어 툴팁/마우스 호버 OHLC 표시는 미이식.
- **알려진 사소한 버그:** `NavMenu.razor`의 검색바(kbar) `@onclick`이 `dashboard.setFocus`를 호출하는데 `dashboard.js`에는 해당 함수가 없습니다(현재 no-op). 의도는 Ctrl+K 팔레트 열기로 추정 — `CommandPalette`의 `OpenPalette`로 연결하면 됩니다. (이번 세션 범위 밖이라 미수정)
- **A3 영속:** 위젯 배치는 요청서의 `ms2e_home_layout` LocalStorage가 아니라 기존 **DB `Dashboard.Layout`** 에 저장합니다(단일 진실 소스 유지). LocalStorage 강제가 필요하면 `DashboardService.LoadLayout/SaveLayout` 내부만 JS interop으로 교체.

---

## 3. Workstream 2 — Primes 소수 모듈 (독립)

> ⚠️ 주식 앱과 **기능적 연관 없음.** 사용자 요청으로 추가한 독립 수학 유틸리티입니다. `StockManager.Common` 네임스페이스에 들어 있지만, 다른 코드가 참조하지 않습니다.

### 3.1 구성

- **`StockManager/Common/Primes.cs`** (v2.1) — 순수 정적 유틸. 외부 의존성 0.
- **`StockManager/Common/Primes_Guide.md`** (v1.2) — 모듈 전용 상세 분석/가이드 문서(알고리즘 해설·복잡도·벤치 실측·백로그). **소수 모듈을 이어받을 때는 이 문서를 먼저 읽으세요.**
- **`StockManager.Tests/PrimesTests.cs`** — xUnit 105 케이스.
- **`StockManager.PrimesBench/`** — Stopwatch 기반 무의존 벤치마크.

### 3.2 공개 API (13개 + 상수 3개)

`IsPrime(long)` / `IsPrime(BigInteger)` · `Sieve` · `CountPrimes` · `CountPrimesLong`(Lucy) · `SegmentedSieve` · `SegmentedSieveStream` · `NextPrime`(→`long?`) · `Factorize`(Pollard rho) · `Generate`(지연) · `NthPrime` · `IsCoprime` · `Totient`. 상수: `MaxSieveLimit=10⁹`, `MaxLongPrime`, `MaxPiLimit=10¹³`.

### 3.3 로드맵 진행 상태

- ✅ P0~P4 전부 완료 (Miller–Rabin, BigInteger, 비트 체, 청크 구간체, Pollard rho, Lucy π(n), 105 테스트, 벤치).
- ⬜ **P5 백로그(다음 작업 후보):** ① Meissel–Lehmer π(n) (10¹³ 너머) ② `Factorize(BigInteger)` ③ `Generate` 세그먼트 체 백엔드.

### 3.4 벤치 실측 (참고)
IsPrime 시산제 대비 ≈638배, Factorize 시산제 대비 ≈485배, π(10⁹) 체 대비 ≈292배 — 상세는 `Primes_Guide.md` §6.

---

## 4. 빌드 · 실행 · 테스트 (검증된 명령)

작업 디렉터리: `StockManager_v3.4.1`

```bash
# 1) 메인 앱 빌드
dotnet build StockManager
#    → 오류 0 · 경고 0

# 2) 소수 테스트
dotnet test StockManager.Tests
#    → 통과! 실패: 0, 통과: 105, 전체: 105

# 3) 소수 벤치마크 (Release 권장)
dotnet run --project StockManager.PrimesBench -c Release

# 4) 앱 실행
dotnet run --project StockManager              # Blazor Hybrid (WebView2 런타임 필요)
dotnet run --project StockManager -- --classic # 구 WinForms 런처
```

**필수 런타임:** .NET 8 SDK, Windows (net8.0-windows + WinForms). Blazor 모드는 **WebView2 런타임** 필요(없으면 자동 클래식 폴백).

**NuGet 패키지(메인):** Microsoft.Data.Sqlite 8.0.0 · QuestPDF 2024.10.1 · System.Security.Cryptography.ProtectedData 8.0.0 · Microsoft.AspNetCore.Components.WebView.WindowsForms 8.0.10.

---

## 5. 이어받는 분을 위한 우선순위 To-Do

### 🔴 P0 — 바로 시작 가능
- [ ] **테스트 커버리지 확장:** 현재 `StockManager.Tests`는 Primes 전용. `PortfolioService.RecomputeAverage`, `BacktestEngine`, `IndicatorMath`, `AlertEngineService`(정책 분기)에 회귀 테스트 추가.
- [ ] **`NavMenu` kbar 버그 수정:** `dashboard.setFocus`(미존재) → Ctrl+K 팔레트 열기로 연결.

### 🟠 P1 — 이식 완성도
- [ ] 레거시 위임 화면(설정 마법사·거래 설정·복합 알림 관리)을 Blazor 네이티브 페이지로 이식.
- [ ] RealtimePage 차트에 크로스헤어/OHLC 툴팁 추가(원본 `ChartForm` 수준).
- [ ] `.sln` 생성으로 통합 빌드/CI 구성.

### 🟡 P2 — Primes 모듈 (원하면)
- [ ] Meissel–Lehmer π(n), `Factorize(BigInteger)` — `Primes_Guide.md` §3/§5 참고.

---

## 6. 파일 변경 인벤토리 (이번 세션)

**신규 (8):**
`StockManager/Common/AlertEngineService.cs` · `PortfolioService.cs` · `IndicatorMath.cs` · `BacktestEngine.cs` · `Primes.cs` · `Primes_Guide.md` · `StockManager/Blazor/ToastService.cs` · `ToastHost.razor`
**신규 프로젝트 (2):** `StockManager.Tests/` (csproj + PrimesTests.cs) · `StockManager.PrimesBench/` (csproj + Program.cs)

**수정 (주요):**
`StockManager/Program.cs` · `Common/PaperBroker.cs` · `ThemeManager.cs` · `NewsService.cs` · `CryptoProviders.cs` · `Blazor/DashboardService.cs` · `BlazorHomeForm.cs` · `MainLayout.razor` · `CommandPalette.razor` · `Dashboard.razor` · `Pages/{Portfolio,Realtime,Signal,Coin,PaperTrading,News,AlertHistory}Page.razor` · `wwwroot/{index.html,dashboard.js,app.css}`

> 이 저장소는 git 미초기화 상태입니다(`Is a git repository: false`). 변경 추적을 위해 `git init` 후 첫 커밋을 만들어 두길 권장합니다.

---

*문서 끝. 추가로 특정 모듈의 상세 흐름이 필요하면 해당 파일 상단 주석 블록과 `Primes_Guide.md`(소수)를 참고하세요.*
