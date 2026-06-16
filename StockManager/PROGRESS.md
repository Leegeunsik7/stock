# 📈 MS2-E Stock Suite — 진행 현황 (Progress)

> 작성일 2026-06-12 · 기준 버전 **v3.38.0** (… IA 개편 + 🄸 드리프트 + **Season 4 ①②③🅰🅱**(로컬LLM·토론·야간퍼블리싱·코파일럿·타임머신)까지 완료)
> 이 문서는 WinForms→Blazor Hybrid 이식 이후 누적된 기능 개발의 진행 상태를 정리한다.
> (제품 개요/빌드 방법은 `README.md`, 소수 모듈은 `Common/Primes_Guide.md` 참고)

---

## 0. 한눈에 보기

| 항목 | 상태 |
|------|------|
| 빌드 | ✅ Release 0 에러 / 0 경고 (`dotnet build StockManager.sln -c Release -m:1`) |
| 단위 테스트 | ✅ **435건 통과** (`dotnet test StockManager.Tests`) |
| 최신 커밋 | `2cd11a5` v3.60.0 (Season 8 #3 LLM 전략 컴파일러) · 태그 `v3.41~60.0` |
| 커밋 대기 | ⏳ **v3.61.0 Season 8 #4(마지막) — 회고 코치**(`RetroCoach` 이동평균 라운드트립 페어링·행동편향 탐지, `RetroCoachService` 직전5일수익률·반사실·LLM서술 + `/coach` + 테스트 10건) (빌드 0 에러·테스트 435) |
| 다음 예정 | **🌌 Season 8 완결** · 사용자 런타임 검증 |

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
| v3.15.0 | **오버나잇 전략 옵티마이저** 🅔 (Gemini 2차 #3) |
| v3.16.0 | **인과 추적 저널** 🅕 (Gemini 2차 #4) |
| v3.17.0 | **Walk-Forward 옵티마이저 2.0** 🅖 (차기 로드맵 — 과최적화 검증) |
| v3.18.0 | **로컬 RAG "투자 두뇌"** 🅗 (차기 로드맵 — 이력-인지 비서) |
| v3.19.0 | **레짐 변화 이상탐지** 🅘 (차기 로드맵 — 통계 경보) |
| v3.20.0 | **MPT 리밸런싱 옵티마이저** 🅚 (차세대 II — 처방 레이어) |
| v3.21.0 | **반사실 디지털 트윈** 🄰 (차세대 II — 행동 격차) |
| v3.22.0 | **알림 신뢰도 캘리브레이터** 🄱 (차세대 II — 자기평가) |
| v3.23.0 | **개인 이벤트 스터디 엔진** 🄲 (차세대 II — 엣지 발굴) |
| v3.24.0 | **시나리오 스튜디오** 🅩 (조건부 스트레스 테스트) |
| v3.25.0 | **리스크 기여도/패리티** 🄳 (차세대 III — 리스크 투명성) |
| v3.26.0 | **예측 스코어보드** 🄵 (차세대 III — 자기 보정도) |
| v3.27.0 | **자동 가설 발굴 엔진** 🄶 (차세대 III — 자율 발견) |
| v3.28.0 | **엣지 인지 포지션 사이저** 🄷 (켈리 + 변동성 타깃) |
| v3.29.0 | **내비게이션 IA 전면 개편** (허브 탭·아코디언·전역 종목 컨텍스트) |
| v3.30.0 | **PortfolioHub 상태 호이스팅** (1회 로드 + CascadingValue로 탭 공유) |
| v3.31.0 | **RiskHub cascade**(몬테카를로+시나리오 1회 로드 공유) + **NavMenu 상태 배지**(미확인 알림) |
| v3.32.0 | **전역 종목 컨텍스트**(Common·OnChange) 실시간 연동 + **횡이동 딥링크** CrossLinks |
| v3.33.0 | **🄸 MPT 드리프트 트리거** (타겟 비중 대비 이탈 → 백그라운드 리밸런싱 알림) |
| v3.34.0 | **Season 4 ① 로컬 LLM(Ollama)** — 오프라인·무한·프라이빗 두뇌(LlmRouter 추상화) |
| v3.35.0 | **Season 4 ② 멀티에이전트 토론** — 강세·약세·심판(승률 가중) |
| v3.36.0 | **Season 4 ③ 야간 자동 퍼블리싱** — 종합 PDF+텔레그램/메일(SMTP) |
| v3.37.0 | **Season 4 🅰 목표주도 코파일럿** — NL 목표→상시 평가·제안(게이팅) |
| v3.38.0 | **Season 4 🅱 타임머신 샌드박스** — 시점 고정(무누수) 재현+채점 |
| v3.39.0 | **운영화** — DB 유지보수(VACUUM 주1회) · Ollama 타임아웃 · CI 릴리스 패키징 |
| v3.40.0 | **진단 대시보드** `/diagnostics` — 백그라운드 훅·LLM/시세·DB 상태 가시화(도그푸딩) |
| v3.41.0 | **Inno Setup 설치 파일** `installer/` — 더블클릭 설치·WebView2 점검·빌드 가이드 |
| (docs) | **오픈소스 쇼케이스** — 영문/한글 README·Mermaid 아키텍처 다이어그램·CONTRIBUTING (`a377b7b`, 무버전) |
| v3.42.0 | **동적 플러그인 아키텍처(Season 5 🅐)** — `plugins/*.plugin.json` 런타임 스캔(지표·알림규칙·프롬프트·어셈블리), 선언적 평가 순수+테스트, 어셈블리 `.dll` 게이팅·`/plugins` UI |
| v3.43.0 | **다기기 동기화(Season 5 🅑)** — E2EE(AES-256-GCM+PBKDF2) 암호화 번들 파일 교환·결정론적 가산 머지(거래=합집합·보유=신규만)·`VACUUM INTO` 백업·미리보기·**허용목록 fail-closed 비밀 차단** |
| v3.43.1 | **동기화 하드닝(적대적 리뷰)** — TZ 불변 거래 지문(교차 TZ 중복 방지)·AvgPrice 정규화·백업 고유 파일명·`ApplyResult.Ok`·enum/설정 적용시 재검증·NaN/null 방어 |
| v3.44.0 | **실시간 차트 버그 수정 + 화면 고급화** — Naver siseJson **공백 파싱 버그**(`["d", v, v…]` → 캔들 0개) 수정·`ParseSiseJson` 순수 추출+테스트·Aggregator **히스토리 폴백**(KRX Naver→Yahoo)·**프리미엄 차트 카드**·데이터 실패 시 안내+재시도 |
| v3.45.0 | **엔터프라이즈 UI 1단계(블룸버그/트레이딩뷰 톤)** — `app.css` 다크 **차콜(#121212)+반투명 하어라인** 팔레트·**고정폭 금융 숫자**(`--mono` tabular/slashed-zero)·얇은 커스텀 스크롤바·**sticky 테이블 헤더**·`.panel`/`.kpi`/`.delta` 컴포넌트·홈 **KPI 커맨드 스트립+인라인 스파크라인**·상승=네온그린/하락=뮤트레드(서구식)·기본 테마 dark |
| v3.46.0 | **엔터프라이즈 UI 2-1 — 포트폴리오 보유 탭** — 4타일→**KPI 커맨드 스트립**·테이블 **`.panel` 래핑+sticky 헤더+dense**·종목별 **인라인 스파크라인(최근 30거래일 종가, 백그라운드 수집)** 컬럼 |
| v3.47.0 | **엔터프라이즈 UI 2-2 — 시장 티커 + 리스크 그리드** — 전역 **시장 티커 바**(`MarketTicker`, 마퀴·20초 폴링·MainLayout 상단)·**리스크 분해** 페이지 KPI 스트립+`.panel`+`.ctbl` sticky/mono 패널화 |
| v3.48.0 | **엔터프라이즈 UI 2-3 — 위젯/패널 통일** — 적대적 리뷰(3렌즈) 반영: `.w` 스코프 대신 **베이스 클래스 승격**(`.wh`·`.row`·`.prog`·`.qa`·`.tile`·`.w` → 하어라인 테두리/구분선, `.wh` 헤더 밴드+그라데이션을 `.panel-h`와 통일)으로 위젯·비위젯·`.tile` 페이지가 한 디자인 시스템. grip order 제거·반경/패딩 정렬 |
| v3.49.0 | **엔터프라이즈 UI 2-4 — 레거시 타일 통일(CSS)** — 13개 페이지 `.tile`을 KPI 톤으로 CSS 일괄 일치(마크업 무변경) |
| v3.50.0 | **엔터프라이즈 UI 마감 — 실 마크업 이관 + 표 패널** — 워크플로(13 병렬 에이전트)로 `.tile`→`.kpi` **실제 마크업 이관**(인라인 `padding:`→`margin:`으로 정렬 보존, 상태 액센트 `.kpi.u/d/k/g` CSS 추가) · 12개 페이지 주요 표를 **`.panel`+헤더 밴드+`.ctbl`**로 래핑 · 라이트 테마 하어라인 대비 보정. 잔여 `.tile` 0 |
| v3.51.0 | **Season 6 #1 — 인터랙티브 차트(TradingView Lightweight Charts)** — lightweight-charts **v4 로컬 번들**·`tvchart.js` v4 재작성(테마 동기화·MA5/20/60·거래량·크로스헤어·ResizeObserver·`renderLine`)·실시간 고급차트 기본 활성(SVG 폴백 유지) |
| v3.52.0 | **Season 6 #2 — 반응형/모바일 레디** — `app.css` `@media` 3단(≤1180/≤820/≤640): KPI 4→2→1열·홈 위젯 풀폭 스태킹·좌우 2열 세로 스택(`.split-lr`)·사이드바 아이콘 레일·협소 시 티커 숨김 |
| v3.53.0 | **Season 6 #3 — 글로벌 타임-스크러버(시그니처)** — `AsOfContext`·상단 스크러버 바(`AsOfBar`)·`AsOfPortfolio.Reconstruct`(거래내역→그 시점 보유 순수 재구성, 테스트 6건)·실시간 차트 as-of 슬라이스+배지·포트폴리오 그 시점 포지션 재구성 |
| **v3.54.0** | **Season 6 #4(마지막) — 리포트 브랜드 동기화 + 커맨드라인 함수 문법** — 야간 PDF(`NightlyReport.BuildPdf`)를 엔터프라이즈 토큰(accent·상승그린/하락레드·고정폭 숫자·패널 밴드·accent 헤더 룰)으로 재튜닝 · 커맨드 팔레트(Ctrl+K)에 **블룸버그식 `<종목> <기능[ 날짜]>` 문법**(RISK/TM/OPT/EV/MINE/SIZE/TWIN/FC/MC/SCN/AI/DEBATE/Q) → SymbolContext 설정 + 모듈 딥링크 + (날짜 시 AsOfContext) — ⏳ 커밋 대기 |
| — | **🎬 Season 6 완료** (#1 인터랙티브 차트 · #2 반응형 · #3 타임-스크러버 · #4 리포트/커맨드라인) |
| v3.55.0 | **Season 7 #1 — 확률 미래 엔진** — `FutureCone` 블록 부트스트랩 확률 콘 + `/future`(SVG 콘+KPI) + as-of 예측 vs 실제 오버레이(타임-스크러버 결합) |
| v3.56.0 | **Season 7 #2 — 전략 카트리지 생태계** — `.ms2cart` 서명 카트리지 P2P 교환(ECDsa + 결정론 백테스트 증명서 verify-by-recompute + canonical 서명 + 갤러리 + 매크로). 적대적 리뷰 하드닝 |
| **v3.57.0** | **Season 7 #3(마지막) — Strategy Forge** — `StrategyForge`(MA 격자 × **워크포워드 OOS 폴드** 평가·강건 점수=OOS평균−0.5·표준편차·거래빈약 컷·결정론, 테스트 4건) + `/forge`(순위표 + KPI) + **선택 전략을 #2 카트리지로 서명·증명서 포장**(발견→검증→교환 흐름 완결) |
| — | **🌌 Season 7 완료** (#1 확률 미래 · #2 카트리지 생태계 · #3 Strategy Forge) |
| **v3.58.0** | **Season 8 #1 — 블랙스완 시뮬레이터** — `BlackSwan`(과거 실제 위기의 **대표 일별 충격 경로**를 현재 포트폴리오에 강제 주입 · 상관 1 붕괴 가정: 종목수익=β×시장충격, 포트=Σ(비중×β)×충격 · 결정론·외부의존 0 · 위기 라이브러리 4종 코로나/2008/2022/블랙먼데이 · 테스트 5건) + `/blackswan`(위기 드롭다운+β가정 → 붕괴 곡선 SVG·KPI·손실기여 최악 종목) |
| **v3.59.0** | **Season 8 #2 — 자율 야간 연구 루프(The Terminal Dreams)** — `NightlyResearch`(새벽 1회: 보유종목 Forge 전략 발굴 → `.ms2cart` 자가서명 → `FutureCone` 확률 미래 투영 → `CopilotEngine` 리스크 평가 → 시스템 알림으로 **인과 저널 적재** → 일지·히스토리 영속화 · 순수헬퍼 결정론·네트워크 0 · 테스트 8건) + `AlertEngineService` 야간 훅(옵트인·LastRun 선마킹·25종목 상한·API 보호) + `/research`(일지 KPI·발굴표·코파일럿 제안·히스토리·지금실행 버튼) · **게이팅: 발굴·서명·투영·평가는 모두 제안/읽기전용, 자동 주문 없음** · 적대적 리뷰 7건 반영(공유 재진입 게이트·취소·정직성 라벨) |
| **v3.60.0** | **Season 8 #3 — LLM 전략 컴파일러** — `StrategyCompiler`(선언적 `StrategySpec`: 지표항 Price/SMA/EMA/RSI/MACD/%B × 연산 상향·하향돌파/>/< + 손절·익절 · **결정론 백테스트**: TradingCost 재현·역슬리피지·강제청산·룩어헤드 없음 · 정준 직렬화 · MA크로스 환원) + `StrategyParser`(오프라인 **규칙 NL 파서** + LLM **JSON 검증 파서**=환각 방화벽) + `StrategyCompilerService`(LLM 우선→C# 검증→실패 시 규칙 폴백 · 검증 통과분만 실행/서명 · CartPrompt 정준+MA크로스면 CartStrategy 증명) + `/compiler`(자연어→스펙→KPI·자본곡선→카트리지) · 테스트 11건 · 적대적 리뷰 11건 반영(로직 id 반영·진입 날조 금지·상수 상한·승률 비용반영·정직성) |
| **v3.61.0** | **Season 8 #4(마지막) — 회고 코치** — `RetroCoach`(실제 거래 이력 **이동평균 라운드트립 페어링**: AsOfPortfolio 규약 정합·부분매도 원가비례 차감·수수료 비례 · 행동편향 탐지: 처분효과·손익비 열위·과매매·추격매수·패닉매도·물타기 · 통화 혼합 회피 위해 **퍼센트 기반** 통계·손익비 999 센티넬 · 결정론·외부의존 0 · 테스트 10건) + `RetroCoachService`(직전5일수익률·반사실 '보유했다면' FX정규화·`ExplainDayAsync` 패턴 LLM 서술=환각 방화벽) + `/coach`(KPI·편향 카드·반사실·닫힌거래표·AI 코칭) · **게이팅: 읽기전용 사후 복기, 주문 없음** — ⏳ 커밋 대기 |
| — | **🌌 Season 8 완결** (#1 블랙스완 · #2 자율 야간 루프 · #3 LLM 전략 컴파일러 · #4 회고 코치) |

---

## 2. 이번 로드맵 (Gemini 2차 라운드) 진행 현황

> 추천 순서대로 진행: **#1 → #2 → #3 → #4**

| # | 기능 | 버전 | 상태 |
|---|------|------|------|
| #1 | 헤드리스 모드 + 원격 접속 강화 | v3.13.0 | ✅ 커밋 완료 |
| #2 | 포트폴리오 몬테카를로 시뮬 🅓 | v3.14.0 | ✅ 커밋 완료 |
| #3 | 오버나잇 전략 옵티마이저 🅔 | v3.15.0 | ✅ 커밋 완료 (`431e30e`) |
| #4 | 인과 추적 저널 🅕 | v3.16.0 | ⏳ 구현·테스트 완료, **커밋 대기** |

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

### #3 오버나잇 전략 옵티마이저 🅔 — v3.15.0 ✅

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

### #4 인과 추적 저널 🅕 — v3.16.0 ⏳ (커밋 대기)

타임스탬프가 있는 4개 소스(거래·시그널·알림·뉴스)를 하나의 타임라인으로 통합하고,
일자별로 묶어 LLM이 "왜 움직였나"를 사후 복기한다. **환각 방지**: 사건/수치는 C#이 DB에서
결정적으로 뽑아 "그날의 기록"으로 직렬화하고, LLM은 그 기록만 근거로 해석한다.

#### 구현 파일

| 파일 | 변경 | 내용 |
|------|------|------|
| `Common/CausalJournal.cs` | 신규 | 타임라인 통합·일자 그룹·LLM 근거 직렬화 (외부 의존 0) |
| `Common/AiService.cs` | 추가 | `ExplainDayAsync(dayContext, model)` — 인과 복기 grounded 프롬프트 |
| `Blazor/Pages/JournalPage.razor` | 신규 | `/journal` 타임라인 UI + 일자별 「🧠 왜 움직였나」 |
| `Blazor/NavMenu.razor` | 추가 | 🧭 인과 저널 메뉴 |
| `StockManager.Tests/CausalJournalTests.cs` | 신규 | 테스트 6건 |
| `StockManager.csproj` | 버전 | 3.15.0 → **3.16.0** |

#### 동작 (숨은 룰)

- **통합 소스 4종**: `Transactions`(거래)·`Signals`(시그널)·`Alerts`(알림)·`News`(뉴스)
- **날짜 필터는 C#에서** (`Safe.DT` 파싱 후 비교) → 저장 포맷 차이에 견고
- **종목 필터**: 거래/시그널/알림은 Symbol 정확일치, 뉴스는 `RelatedSymbols` 부분일치
- **정렬**: 전체·일자그룹·그룹내 이벤트 모두 내림차순(최신 먼저)
- **가격 맥락(거짓 수치 금지)**: 포트폴리오 총자산 전일대비는 `Dashboard.Snapshots`(엔진 적재)에서,
  종목별 등락은 `Candles`(있으면) best-effort — 둘 다 없으면 **생략**
- **LLM 복기 규칙**: 기록에 없는 외부 사건·수치 금지, 단정 대신 신중 표현,
  사건 간 연결(시그널→매수, 뉴스→급등 알림) 강조, 근거 약하면 "단정 어렵다" — 투자 권유 아님 명시

#### `/journal` 페이지 기능

1. 기간(7/30/90일)·종목 필터·모델 선택
2. 일자 카드 타임라인 — 시각·종류 배지·아이콘·제목·상세, 그날 총자산 등락 뱃지
3. 일자별 **「🧠 왜 움직였나」** → 그날의 기록만 근거로 LLM 인과 복기(키 없으면 비활성, 타임라인은 동작)
4. 테스트 6건: 4소스 병합·내림차순·날짜범위·종목필터(정확/부분)·일자그룹·총자산변동·빈DB

> ⚠️ LLM 복기는 Gemini 호출이 일어나는 유일한 지점이며, 사용자가 버튼을 눌러야만 실행된다(비용 통제).

---

## 3. 빌드 · 테스트 재현

```powershell
# 빌드 (MSB4166 회피 위해 단일 노드 -m:1 권장)
dotnet build StockManager.sln -c Release -m:1

# 테스트 (전체 184건)
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
| **옵티마이저/강건성** 🅔🅖 | `/lab/optimizer` — 「지금 전체 실행」·「🧪 강건성 검증」 OOS 폴드 (시세 전제) |
| **인과 저널** | `/intel/journal` — 일자별 「🧠 왜 움직였나」 LLM 복기 (Gemini 키·할당량 필요) |
| **이력-인지 비서** 🅗 | `/intel` — 「📚 RAG」 켜고 "지난번 비슷한 상황…" 질문 → 참조 출처 확인 |
| **레짐 감시** 🅘 | 설정 → 「🛰 레짐 변화 감시」 → 간격 경과 후 알림 센터 경보 |
| **리밸런싱/리스크** 🅚🄳 | `/portfolio/rebalance`·`/portfolio/risk` — 1회 로드 후 탭 전환 재수집 0 (시세 전제) |
| **리스크 시뮬** 🅓🅩 | `/risklab`·`/risklab/scenario` — 몬테카를로·시나리오 cascade 공유 (시세 전제) |
| **디지털 트윈/예측/신뢰도** 🄰🄵🄱 | `/validate`·`/validate/forecast`·`/validate/calibrator` (거래·알림·시세 이력) |
| **이벤트/발굴** 🄲🄶 | `/lab/eventstudy`·`/lab/miner` — 분포·엣지·OOS 생존 가설 (장기 시세 전제) |
| **드리프트 트리거** 🄸 | `/portfolio/rebalance`에서 「🎯 타겟으로 저장」 → 설정 「⚖️ 드리프트 알림」 켜고 이탈 시 경보 |
| **전역 종목/횡이동** | 상단 바에서 종목 설정 → 열린 연구 탭 즉시 재실행 + 페이지 상단 횡이동 버튼 점프 |

---

## 5. 다음 단계

1. **4차 로드맵까지 누적 구현 완료** — Gemini 2차(#1~#4) · 차기 🅖🅗🅘(§6) · 차세대 II 🅚🄰🄱🄲🅩(§7) · 차세대 III 🄳🄵🄶(§8)
2. **사용자 런타임 집중 검증** → §4 표대로 앱을 켜서 UI·WS·LLM·시세 의존부 확인
   - 정적 검증 완료(DI 등록·라우트/내비 정합성), 헤드리스 기동 스모크 통과(호스트+엔진 부팅 OK)
3. 남은 honorable mention(포지션 사이저·MPT 드리프트 트리거)은 사용자 피드백 후 차기 라운드로

---

## 6. 차기 로드맵 제안 (Next Step) — Claude 독창 아이디어

> 원칙: 외부 무거운 의존성 없이("Primes 패턴" 순수 모듈), 기존 자산(옵티마이저·LLM+로컬DB·백그라운드 엔진)을
> 각각 다른 축으로 극한까지 끌어올린다. 핵심 로직은 단위 테스트 가능하게, 런타임 의존부는 정직하게 분리한다.
> 셋 다 지금 있는 데이터·엔진만으로 성립하며 새 인프라가 필요 없다.

### 🅖 자기진화 전략 랩 — Walk-Forward 옵티마이저 2.0  *(견고성 축 · ✅ v3.17.0 구현)*

**구현 완료:** `Common/WalkForward.cs`(순수) — 롤링 train/test로 OOS 재채점, 과최적화 갭·효율·최빈 추천 파라미터·강건성 판정(강건/취약/부적합/보통). `/optimizer` 페이지에 「🧪 강건성 검증」 섹션(판정 뱃지·집계 타일·폴드 표). 테스트 6건. (강건 전략 모의 자동집행은 실거래 보호 위해 별도 게이팅으로 보류)


**문제:** 현재 옵티마이저는 전체 기간 in-sample 최적화 → **과최적화(curve-fitting) 위험**. "과거에 맞았던" ≠ "앞으로 맞을".

**아이디어:** 히스토리를 **롤링 학습/검증 창**으로 쪼개 train에서 뽑은 우승 파라미터를 **out-of-sample test에서 재채점**.
OOS에서도 살아남는 전략만 "승격". 승격된 전략은 모의(Paper)로 집행 → **인과 저널**이 실제 결정을 기록 →
다음 워크포워드가 "기대 vs 실현" 드리프트를 재검증. **옵티마이저→모의→저널→재검증** 루프가 닫힌다.

- 재사용: `BacktestEngine.RunMaCross`(이미 파라미터화), `StrategyOptimizer`, Paper 경로, `CausalJournal`
- 순수 모듈 `WalkForward.cs`(창 분할·OOS 점수·강건성 지표) → 결정적, 테스트 용이
- 산출: 종목별 "강건 점수(robustness)"와 in/out 성과 갭. 갭 큰 전략은 자동 탈락
- 난이도 중 · 리스크 낮음(읽기전용 계산). LLM 불필요(저비용)

### 🅗 로컬 RAG "투자 두뇌" — 내 과거로 답하는 비서  *(지능/기억 축 · ✅ v3.18.0 구현)*

**구현 완료:** `Common/MemoryIndex.cs`(순수 BM25, 외부의존0) — CJK 문자 bigram + ASCII 단어 토큰(형태소 분석기 없이 한국어 검색). `AiService.AskWithMemoryAsync` — CausalJournal 타임라인을 검색해 상위 K건을 grounded 프롬프트에 주입(환각 차단), 참조 출처 반환. `/advisor`에 「📚 내 과거 이력 검색(RAG)」 토글 + 출처 펼침. 테스트 9건. (2단계 Gemini 임베딩은 후속 선택지)


**문제:** 지금 AI 비서는 **현재 스냅샷**만 근거. "지난번 비슷한 국면에서 나는 어떻게 했고 결과는?"에 답 못 함.

**아이디어:** 거래·시그널·알림·뉴스·저널 주석은 이미 전부 로컬 DB에 있다. 이를 **의미 검색 가능한 개인 기억**으로.
질의가 오면 과거 사건을 검색해 프롬프트에 주입 → LLM이 **사용자 자신의 이력**에 근거해 답한다(여전히 환각 차단).

- 1단계(무의존): 순수 C# **BM25/TF-IDF 검색기**(`MemoryIndex.cs`) — 외부 패키지 0, 결정적, 테스트 가능
- 2단계(선택·업그레이드): Gemini 임베딩 API로 시맨틱 검색 강화(키 있을 때만)
- 재사용: `CausalJournal`(이벤트 소스), `AiService`(grounded 주입 패턴), 로컬 DB 전체
- 효과: "AskAsync"가 스냅샷-인지 → **이력-인지**로 진화. 난이도 중 · 리스크 낮음

### 🅘 레짐 변화 이상탐지 엔진 — 임계값을 넘어 "정상성"을 감시  *(실시간/리스크 축 · ✅ v3.19.0 구현)*

**구현 완료:** `Common/RegimeDetector.cs`(순수) — 수익률 z-score·EWMA 변동성 비율·CUSUM(평균 레짐 이동)·평균 페어 상관(분산효과 붕괴). `AlertEngineService.CheckRegimeAsync` 옵트인 훅(기본 12h, 종목당 20h 쿨다운, 보유 주식 4개월 일봉, 상관≥0.85 붕괴 경고). 설정 페이지에 「🛰 레짐 변화 감시」 카드. 테스트 9건. 설정 키: `Regime.Enabled`·`Regime.IntervalHours`·`Regime.LastRun`·`Regime.Alerts`.


**문제:** 알림 엔진이 **고정 임계값**(가격 N 도달)에만 반응. 시장의 "성격이 바뀌는" 순간은 못 잡음.

**아이디어:** 백그라운드 엔진에 **온라인 통계 이상탐지**(EWMA·z-score·CUSUM, 수익률의 볼린저)를 얹어
**레짐 변화**를 감지: "삼성전자가 평소 변동성의 3σ 이탈", "포트폴리오 상관관계 급등(분산효과 붕괴)".
인과 저널과 짝지으면 "왜 레짐이 바뀌었나"까지 복기.

- 순수 모듈 `RegimeDetector.cs`(EWMA/z-score/CUSUM/상관 모니터) → 결정적, 테스트 용이
- 재사용: `AlertEngineService`(상시 평가 훅), `AnalyticsServices`(Pearson 상관 이미 보유), `AlertCenter`
- 고정 임계값 알림과 **다른 클래스**의 경보. 난이도 중상 · 리스크 낮음(읽기전용 신호)

> **honorable mention — 🅩 시나리오 스튜디오:** 몬테카를로를 "수동 분포"에서 **조건부 스트레스 테스트**로 확장.
> 사용자가 충격을 정의("나스닥 −10%", "환율 +5%")하면 과거 베타·상관으로 포트폴리오 영향 전파(순수 회귀). 추천 4순위.

**추천 시퀀스:** 🅖(옵티마이저를 진짜 신뢰할 수 있게) → 🅗(비서를 내 이력으로) → 🅘(리스크 감시를 한 단계 위로).

---

## 7. 차세대 로드맵 II (🅚🄰🄱🄲) — ✅ 전부 구현

> Gemini 제안(🅚) 1건 + Claude 독창 3건(🄰🄱🄲). 각기 다른 축이며 모두 순수 모듈 + 정직한 런타임 분리.

### 🅚 MPT 리밸런싱 옵티마이저 — *처방 축 · ✅ v3.20.0*
`Common/MarkowitzOptimizer.cs`(순수) — 평균-분산 최적화. GMV·tangency 두 펀드 블렌딩으로 long-only 효율 프론티어 추적, 목표변동성→비중 역산. 가우스-조던 역행렬(ridge 안정화)·워터필링 상한·연환산. `/rebalance` 페이지(현재 vs 타겟 비중·조정 금액·프론티어 산점도). 🅓 수익률 수집 재사용. 테스트 8건. *위험을 측정·감지·최적화·회상하던 시스템에 빠져 있던 '처방' 레이어를 채움.*

### 🄰 반사실 디지털 트윈 — *자기인식 축 · ✅ v3.21.0*
`Common/CounterfactualTwin.cs`(순수) — 같은 종목·창에서 buyhold/signal(시그널 추종)/actual(실제 거래) 세 트랙 수익률 비교 → 행동 격차 정량화 + 정규화 자산곡선. `/twin` 페이지(3트랙 타일·격차·판정·곡선). 테스트 9건. *ReplayService("따랐나?")보다 한 걸음 더 — 또 다른 나의 완전한 자산곡선.*

### 🄱 알림 신뢰도 캘리브레이터 — *피드백 축 · ✅ v3.22.0*
`Common/AlertCalibrator.cs`(순수) — 각 알림 이후 forward return으로 유형별 적중률·평균이동·권고 채점(정보성). `/calibrator` 페이지(스코어카드). 테스트 8건. *알림 시스템이 스스로를 평가·교정하는 메타 레이어. 읽기전용(자동 음소거 없음).*

### 🄲 개인 이벤트 스터디 엔진 — *엣지 발굴 축 · ✅ v3.23.0*
`Common/EventStudy.cs`(순수) — 복합규칙(`CompositeAlertEngine.Evaluate` 재사용) 패턴을 과거 캔들 전수 스캔 → 발생 후 미래수익 분포(평균·중앙값·승률·P5/P95) + **무조건 베이스라인 대비 엣지**. `/eventstudy` 페이지(프리셋 + 저장 규칙). 테스트 7건. *복합알림 빌더를 '~때 알려줘'에서 '~뒤 역사적으로 무슨 일이?'로 격상.*

### 🅩 시나리오 스튜디오 — *조건부 스트레스 축 · ✅ v3.24.0*
`Common/ScenarioStudio.cs`(순수) — 보유 종목 가중 합성 시장 팩터 + 종목별 베타(=cov/var, R²)로 "시장 −10%·환율 +5%" 충격을 포트폴리오에 선형 전파. `/scenario` 페이지(충격 슬라이더·프리셋·종목별 베타 표, 베타 1회 추정 후 선형 스케일). 테스트 9건. *🅓 몬테카를로(무조건 분포)의 짝 — 조건부 What-if. honorable mention에서 정식 구현으로 승격.*

---

## 8. 차세대 로드맵 III (🄳🄵🄶) — ✅ 전부 구현

> "기관급 로컬 퀀트 엔진"의 빈 칸 셋: 리스크 투명성 · 자기 보정 · 자율 발견. 모두 순수 모듈 + 기존 자산 재사용.

### 🄳 리스크 기여도 분해 + 리스크 패리티 — *리스크 투명성 축 · ✅ v3.25.0*
`Common/RiskAttribution.cs`(순수) — 공분산 Euler 분해로 MCTR·위험비중(합 100%) 산출 → "비중 10%인데 위험 38%" 같은 숨은 집중 폭로. 동일위험분담 리스크 패리티 비중 대안 제시(곱셈 업데이트·√스텝). `/risk` 페이지(비중 vs 위험비중 막대·배율·패리티 델타). 🅚 공분산 재사용. 테스트 7건. *MPT(수익 기반)와 다른 리스크 기반 시각.*

### 🄵 예측 스코어보드 — *자기 보정·신뢰 축 · ✅ v3.26.0*
`Common/ForecastScore.cs`(순수) — 구간 커버리지 vs 명목·편향·MAE·방향적중·하단위반율 채점 + CalibrationBacktest(과거 trailing 통계로 구간 발행→미래 실현 즉시 채점). `Common/ForecastLog.cs`(DB) 라이브 예측 발행/만기정산. `/forecast` 페이지(보정도 백테스트 + 라이브 로그). 테스트 9건. *🄱(이산 알림)의 상위 — 연속 확률예측을 채점하는 엔진 자기 감사.*

### 🄶 자동 가설 발굴 엔진 "유전자 풀" — *자율 발견 축 · ✅ v3.27.0*
`Common/HypothesisMiner.cs`(순수) — 조건 빌딩블록 14종 자동 생성 → 🄲 EventStudy로 train/OOS 엣지 측정 → **OOS 양수 필수 + 다중검정 보정 효과크기 바**로 과최적화 방어, 생존 패턴만 유전자 풀 등재. `/miner` 페이지(생존 가설 표·「＋알림」 채택 → 🄲/복합알림 연동). 테스트 6건. *사람이 규칙을 안 정해도 시스템이 스스로 엣지를 발굴 — 데이터마이닝 함정은 OOS 게이트로 통제.*

> honorable mention 켈리/변동성 포지션 사이저 → **v3.28.0 🄷 정식 구현**(`Common/PositionSizer.cs` + `/sizer`, 테스트 9건).
> MPT 드리프트 트리거는 IA 개편 우선으로 **보류**(차기).

---

## 9. UI/UX — 내비게이션 IA 전면 개편 (v3.29.0) — ✅ 구현

> 기능이 27개 평면 라우트로 불어나 인지 부하↑. 기존 페이지·라우트는 **건드리지 않고** 가산적으로 상위 IA를 얹음(저위험·되돌리기 쉬움).

**① 허브 탭 통합** — 성격이 같은 페이지를 엄브렐러 라우트 + 슬림 탭으로 묶음(활성 탭만 자식 렌더 → 지연 로드). `HubTabs.razor` + 7 허브.
> **v3.30.0 — 포트폴리오 허브 정식 패턴화:** `/folio` 임베드형을 폐기하고 **`PortfolioHub`(`/portfolio/{Tab?}`)** 로 재구성.
> ① 딥링크 `{Tab?}` ② **상태 호이스팅**: 보유·환율·수익률을 허브가 1회 로드(소비 탭 첫 진입 시 지연) ③ **`<CascadingValue>`** 로 `PortfolioData`를 자식에 전파(새 인스턴스 할당으로 변경 감지) ④ switch 자식 교체(전체 새로고침 X).
> 기존 `/portfolio·/assets·/rebalance·/risk` 페이지는 `@page` 제거(강등). `RebalancePage`·`RiskPage`는 `[CascadingParameter]`로 R/Names/Weights만 소비해 **탭 전환 시 재수집 0**(목표변동성·상한은 탭-로컬 동기 재계산).
> **v3.31.0 — RiskHub 동일 적용:** 몬테카를로+시나리오도 보유 수익률을 공유하므로 `RiskHub`(`/risklab`)에 같은 cascade 적용(둘 다 강등·소비). 공용 로더 `PortfolioData.LoadAsync`로 추출(DRY).
> **허브별 패턴 선택(정직):** 탭이 *같은 데이터*를 공유하는 허브(포트폴리오·리스크시뮬)만 cascade. 탭마다 데이터가 다른 허브(전략연구소·AI·검증·알림)는 **지연 탭 컨테이너**(활성 자식만 렌더, 자체 로드)가 정답 — cascade 강제는 과설계.

**②-b 상태 배지(v3.31.0):** 아코디언 그룹 우측에 상태 점(`.navbadge`) 마크업 추가. 현재 **미확인 알림 수**(`Alerts.IsRead=0`)를 알림 그룹에 실배지로 표시(위치 변경 시 갱신). 다른 그룹 배지는 `BadgeFor()`에 확장.
| 허브 | 라우트 | 탭(기존 페이지 임베드) |
|---|---|---|
| 포트폴리오 | `/folio` | 보유·배분 · 리밸런싱 · 리스크분해 · 총자산 |
| 전략 연구소 | `/lab` | 시그널 · 옵티마이저 · 이벤트스터디 · 가설발굴 · 사이저 |
| 리스크 시뮬 | `/risklab` | 몬테카를로 · 시나리오 |
| AI & 저널 | `/intel` | 비서(RAG) · 종목분석 · 인과저널 |
| 검증 센터 | `/validate` | 디지털트윈 · 예측스코어 · 알림신뢰도 · 리플레이 |
| 알림 센터 | `/alertcenter` | 현재 · 복합규칙 · 이력 |
| 실시간 | `/live` | 주식 · 코인 |

**② 아코디언 NavMenu** — 8 대분류(마켓·포트폴리오·전략연구소·리스크시뮬·AI&저널·검증·알림·도구) + 홈, 영문 캡션, **단일 펼침**(현재 라우트 자동 펼침, `LocationChanged` 구독), 헤더 클릭=첫 항목 이동/접기. 사이드바 최상위 27→11.

**③ 전역 종목 컨텍스트** — `SymbolContext`(싱글톤) + 셸 상단 `SymbolBar`(종목 1회 설정 → 연구로 이동 딥링크 6개). 연구 페이지 6종(옵티마이저·이벤트스터디·가설발굴·사이저·트윈·예측)이 init에 컨텍스트를 **시드**하고 실행 시 **반영(write-back)** → 도구를 옮겨도 종목 유지(재입력 0). 대시보드 위젯·Ctrl K 딥링크도 허브 라우트로 정렬.
> **v3.32.0 — 실시간 연동 + 페이지 내 횡이동 완성:**
> · `SymbolContext`를 **`Common/`로 이전**하고 이벤트를 **`OnChange`** 패턴으로(스펙 정합). 동일값 무시 가드로 write-back↔구독 무한루프 방지. 단위 테스트 5건.
> · 연구 6종이 `OnChange`를 **구독**(`IDisposable`로 해제) → 상단 바에서 종목을 바꾸면 **열려 있는 페이지가 즉시 새 종목으로 재실행**(시드만 → 실시간 재계산으로 격상).
> · **`CrossLinks.razor`**: 각 분석 페이지 상단에 "▶ 005930 으로 → 옵티마이저/이벤트/발굴/사이징/트윈/예측" 딥링크 버튼(자기 자신 제외) → 사이드바 없이 도구 간 횡이동.

빌드 0에러/0경고 · 헤드리스 부팅 OK. (UI 렌더링·탭 전환은 런타임 검증 영역)

---

## 10. 🄸 MPT 드리프트 트리거 — ✅ v3.33.0 (보류 항목 안착)

> IA 개편으로 포트폴리오 허브가 비중을 1회 로드·cascade하게 되자, 그 위에 "타겟 이탈 감시"를 자연스럽게 얹음.

- `Common/RebalanceDrift.cs`(순수): `Compute(current, target)` → MaxDriftPct(최대 |현재−타겟| %p)·MaxSymbol·TurnoverPct(회전율). 키 합집합(없는 쪽 0). `Breached(band)`. 테스트 6건.
- **타겟 저장**: `/portfolio/rebalance` 탭에 「🎯 타겟으로 저장」 → MPT 결과 비중을 **종목코드 기준** `Rebalance.Target`(JSON)에 저장.
- **백그라운드 훅** `AlertEngineService.CheckDriftAsync`: 옵트인(`Drift.Enabled`, 기본 24h·밴드 5%p). 보유 주식 시세 수집→현재 비중 산출→타겟과 비교, 밴드 이탈 시 "⚖️ 리밸런싱 시점" 시스템 알림(20h 쿨다운). 읽기전용·주문 미접촉.
- **설정 카드**: 설정 페이지 「⚖️ 리밸런싱 드리프트 알림」(토글·밴드·간격). 설정 키: `Drift.Enabled`·`Drift.BandPct`·`Drift.IntervalHours`·`Drift.LastRun`·`Drift.LastAlert`.

**→ 이로써 제안·논의된 모든 로드맵·UX·보류 항목 구현 완료.** 누적 테스트 170→**291건**, 빌드 항상 0/0.

---

## 11. Season 4 — 퀀트 터미널 끝판왕 진화 (자동화·다각화·프라이버시) — ✅ 전부 구현

> 분석/계산은 완료. Season 4는 자동화·다각화·프라이버시 축. 순수 코어 + 어댑터 분리·게이팅 원칙 유지.

### ① 로컬 LLM(Ollama) — *프라이버시 · ✅ v3.34.0*
`Common/LlmProviders.cs`(순수 LlmRouter + OllamaProvider). `AiService` 펀넬을 라우터로 분기(auto/gemini/ollama) → **오프라인·무한·완전 프라이빗**(데이터가 기기를 안 떠남, 429 없음). 설정 「🧠 LLM 백엔드」. 외부 SDK 0. 테스트 4건.

### ② 멀티에이전트 토론 — *다각화 · ✅ v3.35.0*
`Common/DebateService.cs`(BuildContext 순수 + RunAsync 3에이전트) + `DebateLog/DebateScore`(판정 14일 후 정산→강세/약세 적중률 채점). 심판이 **자기 과거 승률로 가중**(자기보정 앙상블). `/intel/debate`. 로컬 LLM이면 무료·프라이빗. 테스트 5건.

### ③ 야간 자동 퍼블리싱 — *자동화 · ✅ v3.36.0*
`Common/NightlyReport.cs`(Gather DB집계 + ComposeText 순수 + BuildPdf QuestPDF) + `EmailSender.cs`(BCL SMTP, PDF첨부, DPAPI 비번). `AlertEngineService.CheckNightlyAsync` 옵트인(시각 후 1회/일)→텔레그램+메일. "앱을 안 켜도 일하는" 서비스화. 테스트 2건.

### 🅰 목표주도 코파일럿 — *자동화→자율 · ✅ v3.37.0*
`Common/CopilotEngine.cs`(BuildCtx 자산곡선→변동성·낙폭·레짐 + Evaluate 정책→제안 + ParseGoal NL→정책, 모두 순수). `CheckCopilot` 옵트인 훅 → 목표 위반 시 "🧑‍✈️ 제안" 알림(인과 저널 자동 적재)·정책당 24h 쿨다운. **⚠️ 실주문 절대 미접촉(제안만)**. `/copilot`. 테스트 8건.

### 🅱 타임머신 샌드박스 — *검증 캡스톤 · ✅ v3.38.0*
`Common/TimeMachine.cs`(AsOfSlice look-ahead 차단 + Replay 시점재현·빨리감기 채점, 순수). "그 날짜에 터미널은 뭐라 했나"(레짐·RSI·옵티마이저 우승) → +N봉 실현으로 적중 채점. `/timemachine`. 모든 모듈이 순수라 as-of 슬라이스만 먹이면 동작. 테스트 6건.

**→ Season 4까지 누적: 순수 모듈 ~22종, 단위테스트 170→329, 빌드 항상 0/0, v3.4.2→v3.38.0. 로컬 LLM/메일/SMTP/Ollama 실호출은 런타임 검증 영역.**

---

## 12. 운영화 + Season 5 — 배포·생태계

> 기능 개발 이후 단계: 실사용 가시화 → 배포 자동화 → 확장 생태계. 순수 코어/어댑터 분리·정직한 검증 경계 유지.

### 운영화 (v3.39.0~v3.41.0) — ✅
- **v3.39.0** DB 유지보수(`DbMaintenance` 주1회 VACUUM/ANALYZE) · Ollama 타임아웃 하드닝 · CI 릴리스 패키징(`v*` 태그→publish→zip→Release)
- **v3.40.0** 진단 대시보드 `/diagnostics`(`Diagnostics` 순수) — 백그라운드 훅·LLM/시세·DB 상태 가시화(도그푸딩 도구)
- **v3.41.0** Inno Setup 설치 파일 `installer/`(사용자단위·WebView2 점검·한/영) + 빌드 가이드

### 오픈소스 쇼케이스 (docs, `a377b7b`) — ✅
영문 `README.md`/한글 `README.ko.md`(Mermaid 아키텍처 다이어그램·기능 매트릭스·설계 원칙·버전 궤적·검증 경계) + `CONTRIBUTING.md`(한/영). 낡은 `StockManager/README.md`(v2.3)는 현재 구조 안내로 교체. **바이너리 무변경 → 버전 미부여.**

### 🅐 동적 플러그인 아키텍처 — *확장성 · ✅ v3.42.0*
다시 빌드 없이 `plugins/` 폴더의 매니페스트를 런타임 편입.
- `Common/PluginSystem.cs` **(순수)**: `PluginManifest.TryParse`(JSON 검증) · `PluginCatalog.Build`(중복제거=고버전 채택, 어셈블리 게이팅, 활성/비활성 판정) · 선언적 평가기 — `PromptTemplatePlugin`(`{{key}}` 치환), `DeclarativeIndicator`(emaCross/smaCross 스탠스), `DeclarativeAlertRule`(metric op value).
- `Common/PluginLoader.cs` **(어댑터·런타임)**: `%LOCALAPPDATA%\StockManager\plugins\**\*.plugin.json` 스캔 + `AssemblyLoadContext`(수거 가능·의존성 해석) 격리 로드. `IStockPlugin`/`IPluginHost` 인터페이스.
- **보안 게이팅**: 어셈블리(.dll)는 임의 코드 실행 → **기본 차단**, `Plugins.AllowAssemblies=true` 명시 허용 필요. 선언적 플러그인은 코드 실행 없이 안전.
- UI `/plugins`(도구 그룹 🧩): 카탈로그 표·항목별 사용 토글(`Plugins.Enabled` csv)·어셈블리 허용 토글·폴더 열기·매니페스트 경고·작성 가이드.
- 테스트 `PluginSystemTests` **23건**(파싱 유효/무효, 중복제거, 어셈블리 게이팅, 활성 오버라이드, 버전 비교, 템플릿 렌더/자리표시자, 지표 스탠스/데이터부족, 알림규칙 평가).

**→ 누적: 단위테스트 334→357, 빌드 항상 0/0, v3.41.0→v3.42.0. 폴더 스캔·.dll 로드·`/plugins` 렌더는 런타임 검증 영역.**

### 🅑 다기기 동기화(P2P, 위험하지 않은 범위) — *연결성 · ✅ v3.43.0*
중앙 서버 없이 데스크톱↔노트북 데이터를 안전하게 병합. **라이브 소켓 대신 암호화 번들 파일 교환**(공유폴더/Tailscale/USB).
- `Common/SyncEngine.cs` **(순수)**: `Fingerprint`(SHA-256), `Plan`(결정론적 dry-run) — 거래=UnionDistinct(내용해시 합집합), 보유=AddMissingOnly(기존 절대 미덮어쓰기), 설정=PreferIncoming(충돌은 확정 시만). per-record 타임스탬프 없는 스키마라 LWW 대신 **가산 머지 → 데이터 손실 불가**.
- `Common/SyncCrypto.cs` **(E2EE)**: AES-256-GCM + PBKDF2-SHA256(200k), BCL 전용. 매직헤더+salt+nonce+tag 포맷, GCM 태그로 변조 탐지(오암호/손상 거부).
- `Common/SyncService.cs` **(어댑터)**: 수집·번들·`.ms2sync` 파일 입출력·`PlanAgainstLocal`·`VACUUM INTO` 자동 백업·`ApplyPlan`(백업 실패 시 적용 중단). 설정은 **fail-closed 허용목록**으로만 내보냄.
- UI `/sync`(도구 🔄): 내보내기(암호 2회·항목 선택)·가져오기(목록 선택·미리보기 표·백업 후 적용·충돌 덮어쓰기 옵트인).
- 테스트 `SyncEngineTests` **8건**(지문 결정성·머지 분류·union 무충돌·암호 라운드트립/오암호/변조·비번들 거부·허용목록 비밀차단·서비스 라운드트립 멱등·보유 미덮어쓰기).
- **적대적 리뷰 반영**: 초기 거부목록(fail-open)이 `Lan.Pin`·`Email.*`·`KIS.AccountNo`·`Telegram.ChatId`를 누출 → **허용목록(fail-closed)으로 전환** + 비밀/식별자 토큰 이중 차단. (crypto/merge 렌즈는 "견고함" 교차확인; data-safety/edge 렌즈는 세션 한도로 미완 → 자체 점검: VACUUM INTO 경로 escape·백업실패 중단·decimal/double 라운드트립·빈 번들 처리 확인.)

**→ 누적: 단위테스트 357→365, 빌드 항상 0/0, v3.42.0→v3.43.0. 파일 IO·DB 적용·`/sync` 렌더는 런타임 검증 영역.**
