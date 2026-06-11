# 📈 MS2-E Stock Suite — 진행 현황 (Progress)

> 작성일 2026-06-12 · 기준 버전 **v3.15.0** (오버나잇 옵티마이저까지 구현 완료)
> 이 문서는 WinForms→Blazor Hybrid 이식 이후 누적된 기능 개발의 진행 상태를 정리한다.
> (제품 개요/빌드 방법은 `README.md`, 소수 모듈은 `Common/Primes_Guide.md` 참고)

---

## 0. 한눈에 보기

| 항목 | 상태 |
|------|------|
| 빌드 | ✅ Release 0 에러 / 0 경고 (`dotnet build StockManager.sln -c Release -m:1`) |
| 단위 테스트 | ✅ **178건 통과** (`dotnet test StockManager.Tests`) |
| 최신 커밋 | `8f83fad` v3.14.0 (몬테카를로) |
| 커밋 대기 | ⏳ **v3.15.0 오버나잇 옵티마이저** (구현·테스트 완료, 커밋 직전 중단됨) |
| 다음 예정 | 🅕 인과 추적 저널 (v3.16.0) |

> **검증 경계(정직 표기):** 빌드 성공·단위 테스트 통과는 자동 검증된 사실이다.
> 그러나 **UI 렌더링, WebSocket 실시간 틱, Gemini LLM 응답, LAN/원격 접속, 실시간 시세 수집**은
> 사용자가 실제 앱을 켜고 확인해야 하는 런타임 의존 영역이며, 이 문서에서 "✅"로 표기하지 않는다.

---

## 1. 버전 이력 (요약)

| 버전 | 핵심 내용 |
|------|-----------|
| v3.4.2 | Git 형상관리 토대(.sln·.gitignore·CI 스캐폴드) |
| v3.5.0 | SQLite 동시성 하드닝 — WAL + `busy_timeout` + 지수백오프 재시도 |
| v3.5.1 | 민감정보 at-rest 재암호화 마이그레이션(DPAPI `enc:v1:`) |
| v3.5.2 | 대시보드 일봉 캔들 5분 캐시(API 한도 방어) |
| v3.5.3~3.5.5 | NavMenu Ctrl+K, Composite 캔들 캐시, AlertPolicy 추출 + 테스트 |
| v3.6.0~3.6.1 | 거래 설정·복합 알림 편집기 Blazor 네이티브 이식 |
| v3.7.0~3.7.2 | 업비트 WebSocket 오버레이, 실거래 안전가드 테스트, 설정 마법사 5스텝 |
| v3.8.0 | **AI 투자비서 + 아침 브리핑** (Next-Level Step1) |
| v3.9.0 | 결정품질 리플레이 🅐 + 자연어 알림 컴파일러 🅱 |
| v3.10.0 | TradingView Lightweight Charts 통합(폴백형) |
| v3.11.0 | 읽기전용 LAN 상태 서버 🅲 (HttpListener, 무의존) |
| v3.12.0 | 자동 DCA — **모의(Paper) 전용** ④ |
| v3.12.1 | 앱 실행 피드백 수정(미국 시세·그룹 큐레이션·Gemini 모델) |
| v3.13.0 | **헤드리스 모드 + 원격 접속 강화** (Gemini 2차 #1) |
| v3.14.0 | **포트폴리오 몬테카를로 시뮬** 🅓 (Gemini 2차 #2) |
| **v3.15.0** | **오버나잇 전략 옵티마이저** 🅔 (Gemini 2차 #3) — ⏳ 커밋 대기 |

---

## 2. 이번 로드맵 (Gemini 2차 라운드) 진행 현황

> 추천 순서대로 진행: **#1 → #2 → #3 → #4**

| # | 기능 | 버전 | 상태 |
|---|------|------|------|
| #1 | 헤드리스 모드 + 원격 접속 강화 | v3.13.0 | ✅ 커밋 완료 |
| #2 | 포트폴리오 몬테카를로 시뮬 🅓 | v3.14.0 | ✅ 커밋 완료 |
| #3 | 오버나잇 전략 옵티마이저 🅔 | v3.15.0 | ⏳ 구현·테스트 완료, **커밋 대기** |
| #4 | 인과 추적 저널 🅕 | v3.16.0 | ⬜ 예정 |

---

### #1 헤드리스 모드 + 원격 접속 강화 — v3.13.0 ✅

UI 창 없이 백그라운드 엔진만 돌리는 실행 모드.

- `Program.cs`: `--headless` 인자 → `RunHeadless(db, cleanup)` (트레이 기반, BlazorWebView 미생성)
- `--classic` 폴백 인자 유지
- 상시 평가 엔진(CompositeAlertService, AlertEngineService, LanStatusServer)이 헤드리스에서도 가동
- 원격은 v3.11.0의 읽기전용 LAN 서버(PIN 브루트포스 잠금: 10회 실패 → 60초)를 활용

---

### #2 포트폴리오 몬테카를로 시뮬 🅓 — v3.14.0 ✅

보유 종목의 과거 수익률·상관관계를 보존한 채 미래 분포를 시뮬레이션.

- `Common/MonteCarlo.cs` — **순수 계산**(외부 의존 0). Cholesky 분해로 상관된 정규 충격 생성
  → P5/P50/P95 · VaR95 · 손실확률 · 히스토그램 반환
- `Blazor/Pages/MonteCarloPage.razor` (`/montecarlo`) — 기간(1주/1개월/3개월)·시뮬 횟수 선택, SVG 분포 차트
- 입력: `CorrelationAnalyzer.CollectReturnsAsync`(과거 90일) + 시가총액 비중
- 테스트: `MonteCarloTests` 5건 (분산0 결정성·동일 시드 재현·백분위 순서·경계·변동성↑→VaR↑)
- **투자 권유 아님** 배너 명시

---

### #3 오버나잇 전략 옵티마이저 🅔 — v3.15.0 ⏳ (커밋 대기)

유휴 시간에 보유 주식마다 MA 골든/데드크로스 기간을 **격자 전수 백테스트**해
종목별 최적값을 발굴한다. **읽기전용** — 주문/DCA를 절대 건드리지 않는다.

#### 구현 파일

| 파일 | 변경 | 내용 |
|------|------|------|
| `Common/BacktestEngine.cs` | 리팩토링 | 시뮬 코어 `RunCore(candles, market, Func<int,int> signalAt, warmup)` 추출. 기존 `Run`은 이를 호출(동작·워밍업 30 동일 유지). **`RunMaCross(candles, market, shortP, longP)`** 신규 — 단·장기 기간 파라미터화 |
| `Common/StrategyOptimizer.cs` | 신규 | 격자탐색 엔진 + DTO |
| `Common/AlertEngineService.cs` | 추가 | 옵트인 백그라운드 훅 `CheckOptimizerAsync()` |
| `Blazor/Pages/OptimizerPage.razor` | 신규 | `/optimizer` 페이지 |
| `Blazor/NavMenu.razor` | 추가 | 🌙 전략 옵티마이저 메뉴 |
| `StockManager.Tests/StrategyOptimizerTests.cs` | 신규 | 테스트 8건 |
| `StockManager.csproj` | 버전 | 3.14.0 → **3.15.0** |

#### 알고리즘 (숨은 룰)

- **격자** = 단기 {5, 8, 10, 12} × 장기 {20, 26, 30, 60}, 단기<장기 → **16조합** (`GridSize`)
- **점수** = 총수익률(TotalReturn) ↓, 동점 시 MDD 작은 쪽 → 그래도 동점이면 단기 짧은 쪽
- **무거래(신호 0건) 조합은 후보에서 제외** — 과최적화/우연 방지
- 캔들 부족(최장 장기 60 + 2 미만)·전 조합 무거래 → `IsValid=false`
- **결정적**: 동일 캔들 → 동일 우승자 (난수/시드 없음)
- 비용 모델(수수료·세금·슬리피지)·강제청산·MDD/Sharpe는 기존 백테스트와 100% 공유

#### 백그라운드 훅 동작 (`AlertEngineService.CheckOptimizerAsync`)

- 옵트인: `Optimizer.Enabled == "true"` 일 때만
- 간격: `Optimizer.IntervalHours`(기본 24h) — `Optimizer.LastRun` 기준, **먼저 마킹**(중복 실행 방지, DCA/Brief와 동일 규약)
- 대상: `GetAllHoldings()` 중 코인 제외 주식, 심볼 중복 제거 후 **최대 25종목**
- 종목당 18개월 일봉, **150ms 간격**(API 한도 보호)
- 결과 → `Optimizer.Results`(JSON, 경량 `OptSnapshot`) 저장 + Info 알림 1건

#### 설정 키

| 키 | 의미 | 기본 |
|----|------|------|
| `Optimizer.Enabled` | 밤사이 자동 분석 on/off | `false` |
| `Optimizer.IntervalHours` | 실행 간격(시간) | `24` |
| `Optimizer.LastRun` | 마지막 실행 시각(ISO) | — |
| `Optimizer.Results` | 최신 스냅샷(JSON) | — |

#### `/optimizer` 페이지 기능

1. **종목 즉시 분석** — 종목코드+시장 입력 → 16후보 표(우승 🏆 강조) + 우승 지표 타일
2. **밤사이 자동 분석 설정** — on/off 토글, 간격, 「지금 전체 실행」(타이머 안 기다리고 즉시)
3. **백그라운드 스냅샷 표** — 보유 종목별 최적 전략, 행별 「분석」 버튼
4. **복합 알림 원클릭 주입** — 우승 장기선(MA{long}) 복귀 추세필터를 `CompositeRule`로 추가
   → **정확한 MA 교차가 아닌 "표준화된 추세 필터"임을 UI에 정직 표기**
   (복합알림 엔진의 메트릭이 MA괴리율(가격 vs MA)만 지원하므로, 교차를 그대로 표현하지 않음)

#### 안전 설계

- 옵티마이저는 **발굴만** 한다. 실주문 경로(BrokerHub/LiveBrokers) 및 자동 DCA에 **직접 주입하지 않는다.**
- 자동 DCA는 기존 설계대로 **코인 KRW 마켓·모의(Paper) 전용**으로 유지(실거래 자동매매는 게이팅).
- 복합알림 주입조차 "주문"이 아니라 "알림 규칙"이며, 사용자가 명시적으로 버튼을 눌러야 생성됨.

#### 테스트 (`StrategyOptimizerTests`, 8건)

격자 크기(16)·유효 우승·우승=거래발생 중 최고수익률·결정론(동일 캔들 동일 우승)·
캔들 부족 무효·평탄가격 무거래 무효·`RunMaCross` 잘못된 파라미터 빈 결과·`ToRow` 매핑.

---

### #4 인과 추적 저널 🅕 — v3.16.0 ⬜ (예정)

타임스탬프가 있는 모든 테이블(거래·시그널·알림·스냅샷·브리핑)을 하나의 타임라인으로 통합하고,
LLM이 "왜 움직였나"를 인과적으로 주석. **LLM 호출 비용** 때문에 로드맵 마지막으로 배치.

---

## 3. 빌드 · 테스트 재현

```powershell
# 빌드 (MSB4166 회피 위해 단일 노드 -m:1 권장)
dotnet build StockManager.sln -c Release -m:1

# 테스트 (전체 178건)
dotnet test "..\StockManager.Tests\StockManager.Tests.csproj" -c Release -m:1
```

> 참고: 테스트 프로젝트는 솔루션 상위 폴더(`StockManager_v3.4.1\StockManager.Tests`)에 있다.

---

## 4. 런타임 확인이 필요한 항목 (사용자 점검 권장)

| 영역 | 확인 방법 |
|------|-----------|
| TradingView 차트 | `wwwroot/lib/lightweight-charts.standalone.production.js` 배치 후 실시간 페이지에서 토글 |
| LAN/원격 접속 | 설정에서 LAN 서버 켜고 같은 망의 다른 기기/Tailscale·Cloudflare로 접속 |
| Gemini 응답 | 유효 키 입력 후 AI 비서/브리핑 — 429(무료 할당 소진)는 코드 문제 아님 |
| 실시간 시세/WS 틱 | 코인 실시간 페이지에서 업비트 WebSocket 오버레이 확인 |
| **옵티마이저** | `/optimizer`에서 보유 종목 「지금 전체 실행」 → 스냅샷 표 확인 (시세 제공자 정상 동작 전제) |

---

## 5. 다음 단계

1. **v3.15.0 커밋** (현재 대기 중) — 7개 파일 스테이징 완료 상태
2. #4 인과 추적 저널(v3.16.0) 구현
3. 로드맵 종료 후 사용자 런타임 검증 → 피드백 반영
