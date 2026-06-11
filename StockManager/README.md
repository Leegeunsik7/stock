# 📊 MS2-E Stock Suite v2.3

> 한국·미국 주식 통합 관리 플랫폼 · C# WinForms + .NET 8
> MS2-E v2.1.1 통합 업무 매니저 확장 모듈

## 🎯 주요 특징

- **5개 통합 프로그램**: 실시간 시세 · 포트폴리오 · AI 분석 · 뉴스 레이더 · 시그널 엔진
- **다중 데이터 소스**: 네이버 금융 · Yahoo Finance · 한국투자증권 KIS · 키움 OpenAPI+
- **Google Gemini AI**: 기업 분석 · 뉴스 감성분석 (JSON 강제 응답)
- **통합 알림 센터**: 가격·시그널·뉴스·배당 알림을 한 곳에서
- **텔레그램 봇**: Critical 알림 실시간 푸시
- **주간 PDF 리포트**: 포트폴리오·거래·시그널·뉴스 자동 요약 (QuestPDF, 한글 완벽)

## 📦 구성

| 프로그램 | 디렉토리 | 주요 기능 |
|----------|---------|----------|
| 01 실시간 대시보드 | `01_RealtimeWatcher/` | 히트맵·차트·52주 스캔 |
| 02 포트폴리오 | `02_Portfolio/` | 7탭 (보유/거래/수익률/배분/세금/배당/상관관계) |
| 03 AI 분석 | `03_AIAnalysis/` | 레이더차트·Gemini JSON |
| 04 뉴스 레이더 | `04_NewsRadar/` | RSS·Jaccard 중복·감성분석 |
| 05 시그널 엔진 | `05_SignalEngine/` | 백테스팅·섹터 로테이션 |
| Common | `Common/` | 11개 공유 모듈 |

총 약 **6,715 LOC** (C#)

## 🚀 빠른 시작

### 1. 빌드

```powershell
cd C:\Users\GSLEE\Desktop\StockManager
dotnet restore
dotnet build -c Release

# 키움 실시간 사용 시 (32비트 필수)
dotnet build -c Release -p:PlatformTarget=x86
```

### 2. 실행

```powershell
dotnet run
# 또는 bin\Release\net8.0-windows\MS2E.StockSuite.exe
```

### 3. 초기 설정

런처 → ⚙️ **설정** 카드에서:

- **Gemini API Key**: https://aistudio.google.com/apikey
- **KIS AppKey/Secret**: https://apiportal.koreainvestment.com (모의투자 먼저)
- **Telegram Token/ChatId**: @BotFather → 봇 생성 → Chat ID 조회

## 📋 v2.3 변경 사항

> 정밀 소스 분석 후 완성도 개선 9건 (Critical 3 · High 3 · Medium 3)

### 🔴 Critical

| ID | 이슈 | 해결 |
|----|------|------|
| C1 | Portfolio가 UI 스레드에서 `GetUsdKrwAsync().Result` 블로킹 → 첫 실행 시 데드락 위험 | `RefreshAllAsync`에서 환율 1회 `await` 후 `_usdKrw` 필드 캐시. 모든 `.Result` 제거 |
| C2 | RealtimeWatcher 그리드가 첫 탭에만 배치 → 국내/미국/반도체/2차전지 탭 전환 시 빈 화면 | `MoveGridToCurrentTab()`으로 선택 탭에 그리드 재배치 |
| C3 | Gemini/KIS/Telegram API 키가 SQLite에 평문 저장 | DPAPI(`Secret.Protect/Unprotect`) + `LocalDatabase.GetSecret/SetSecret`. `System.Security.Cryptography.ProtectedData` 추가 |

### 🟠 High (Dead UI 복구)

- **H1.** RealtimeWatcher "종목 추가/삭제/알림 규칙" 버튼 핸들러 구현 (+`AlertRuleDialog`, Watchlist/AlertRules 연동)
- **H2.** NewsRadar 출처·감성 필터 동작 (`RenderNewsGrid` + 행 `Tag` 기사 매핑으로 인덱스 정합성 확보). 출처 콤보를 실제 소스(Google News/Yahoo)로 정리
- **H3.** ChartForm Stochastic·볼린저밴드(%B) 보조지표 구현. 휠 줌 시 거래량/보조 패널도 갱신

### 🟡 Medium

- **M1.** SignalEngine 백테스트 O(n²) 제거 — `closes`/EMA 1회 사전계산, `GetSignal`에서 매 호출 재계산·할당 제거 (`Avg`/`EmaSeries` 헬퍼)
- **M2.** Portfolio 비중·자산배분·통화비중을 환율 환산(KRW) 기준으로 통일 (`KrwValue` 헬퍼, 하드코딩 1380 제거)
- **M3.** ExtremeScanner 52주 신고/신저가를 **종가 기준**(당일 제외)으로 정정

## 📋 v2.2 변경 사항

### ✅ 수정된 이슈 (23건)

#### 🔴 Critical (4건)

| ID | 이슈 | 해결 |
|----|------|------|
| C1 | 네이버 `compareToPreviousClosePrice`를 전일종가로 오인 | `risingOrFalling`으로 부호 판별 후 `price - change` |
| C2 | KIS `prdy_vrss` 미사용 | `prdy_vrss_sign` (4/5=하락) 조합 |
| C3 | GDI+ Font 누수 (Paint마다 new Font) | `GdiCache` 정적 캐시 (12개 프리셋) |
| C4 | csproj `PlatformTarget` 미지정 | `AnyCPU` 명시 + x86 가이드 |

#### 🟠 High (6건)

- H1. `Safe.Dec/Int/Long/Str/DT` 헬퍼 전역 적용 (DBNull 예외 방지)
- H2. KIS `decimal.Parse` + `CultureInfo.InvariantCulture`
- H3. Yahoo `Safe.JsonDec` (int/double/string 혼재 대응)
- H4. Kiwoom 실시간 이벤트 `SynchronizationContext.Post` + `BeginInvoke`
- H5. `RecomputeAverage` 전량 매도 시 `qty=q` 가드 (0/0 방지)
- H6. HTS CSV 헤더 매핑 (`headerMap` 별칭 Dictionary)

#### 🟡 Medium (7건)

- M1. Signals/News/Alerts/Dividends 테이블 인덱스 추가
- M2. `HttpPool.Shared` 싱글톤 (SocketsHttpHandler, 5분 PooledLifetime)
- M3. Settings 다이얼로그 실제 구현 (Gemini/KIS/Telegram)
- M4. 백테스팅에 수수료·증권거래세·슬리피지 반영
- M5. Equity Curve X축을 거래일 인덱스 기반으로 (비영업일 왜곡 제거)
- M6. 뉴스 Jaccard 3-gram 유사도 중복 필터 (임계값 0.8)
- M7. Gemini `responseMimeType="application/json"` 강제

#### 🟢 Low (6건)

- L1. `Let<T>` 죽은 코드 제거
- L2. `TakeLast` 한 번만 `.ToList()`
- L3. `SampleData.KoreanTop` + `USTop` + `All()` 공유
- L4. `Log.Info/Warn/Error` 파일 로깅
- L5. 타이머 Interval 변경 즉시 반영
- L6. `Formatter.HumanizeNumber` (조/억/만)

### 🆕 추가 기능 (8개)

| ID | 기능 | 위치 |
|----|------|------|
| A | 통합 알림 센터 | `Common/AlertCenter.cs` + `AlertCenterForm.cs` |
| C | 해외주식 세금 시뮬레이터 | `AnalyticsServices.cs :: TaxSimulator` |
| D | 배당 캘린더 (향후 60일) | Portfolio 탭 + `Dividends` 테이블 |
| E | 52주 신고가/신저가 스캐너 | `AnalyticsServices.cs :: ExtremeScanner` |
| F | 상관관계 매트릭스 (Pearson) | `AnalyticsServices.cs :: CorrelationAnalyzer` |
| G | 섹터 로테이션 (상대강도) | `AnalyticsServices.cs :: SectorAnalyzer` |
| I | 주간 PDF 리포트 | `Common/WeeklyReportGenerator.cs` (QuestPDF) |
| J | 텔레그램 봇 알림 | `Common/TelegramNotifier.cs` |

### 🔜 향후 (v2.3)

- 기능 B — 다계좌 통합 대시보드 (BrokerAccounts UI)
- 기능 H — MS2-E ModuleShellBase 사이드바 통합
- KIS WebSocket 실시간 · 키움 ActiveX 실제 연결
- DART Open API (재무제표 연동)

## 🏗 아키텍처

```
StockManager/
├── Common/                     # 공유 라이브러리 (11개 파일)
│   ├── Models.cs              # Quote/Holding/Transaction/TradeSignal/NewsArticle
│   ├── Utilities.cs           # Safe, HttpPool, Log, GdiCache
│   ├── ExtendedModels.cs      # Sector, DividendEvent, AlertItem, ...
│   ├── QuoteProviders.cs      # Naver, Yahoo, QuoteAggregator
│   ├── BrokerProviders.cs     # Kiwoom, KIS
│   ├── Database.cs            # LocalDatabase (WAL)
│   ├── AlertCenter.cs         # 싱글톤 알림 버스
│   ├── AlertCenterForm.cs     # 알림 센터 UI
│   ├── AnalyticsServices.cs   # Extreme/Correlation/Sector/Tax
│   ├── TelegramNotifier.cs    # 텔레그램 봇 클라이언트
│   └── WeeklyReportGenerator.cs  # PDF 리포트
├── 01_RealtimeWatcher/
├── 02_Portfolio/
├── 03_AIAnalysis/
├── 04_NewsRadar/
├── 05_SignalEngine/
├── Launcher.cs                # 9카드 런처 + SettingsDialog
├── Program.cs
└── StockManager.csproj
```

## 🔧 로컬 데이터

```
%LOCALAPPDATA%\StockManager\
├── portfolio.db               # SQLite WAL 모드
└── logs\
    └── stock_yyyyMMdd.log    # 일일 로테이션
```

## 📚 라이선스

- **Microsoft.Data.Sqlite**: MIT
- **QuestPDF**: Community License (연 매출 100만 USD 미만 무료)
- **Gemini API**: Google AI Studio (무료 티어 지원)

## 🙏 Credits

- Angtec Engineering — 이근식
- MS2-E v2.1.1 통합 업무 매니저 기반
- v2.2 리팩토링 협업

---

**v2.3.0** · 2026-06-05 · 완성도 개선 9건 (C1~C3 · H1~H3 · M1~M3)
**v2.2.0** · 2026-04-19 · 23개 이슈 수정 + 8개 신기능
