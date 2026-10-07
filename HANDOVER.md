# MS2E StockSuite — 정밀 인수인계 문서

> 기준 버전: **v10.1.0** (2026-08-02)  
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
- **Phase 8/9 나노 파이프라인**(제로카피 flat L3 오더북·SIMD 10호가 리스크 veto 비가역 래치·FNV-1a 추측 프리페치·선지 캐시-예열 리스크 격자·MMF append-only SHA-256 감사저널 — δ=0·liveArmed:false, 6렌즈 적대검증 확정 5결함 교정)
- **무할당 실시간 포트폴리오 VaR & Gershgorin 적응형 리지 방화벽**(δ=0 Parametric/Historical/Monte Carlo/Stress VaR·공분산 특이점 Gershgorin 최소 리지 PD 수학적 보장·NaN 퓨즈 무상관 보수 강등·리스크 밸브 tighten-only blockWhile 결선)
- **하드웨어 P-Core 물리 격리 & 서멀 스로틀 가드 & ★캐시라인 코히런스 센티널**(GetLogicalProcessorInformationEx EfficiencyClass P-Core affinity 물리 핀·QPC 유효주파수 δ=0 실측 가역 안전홀드·64B 정렬 언매니지드 아레나 캐시라인 격리+코어 마이그레이션 δ=0 계수+슬롯 카나리 O(1) 오염 퓨즈 — False Sharing 구조적 불가 통합 불변식. 드레인/틱 수신 핫패스 P-Core 옵트인 핀·서멀/코히런스 blockWhile tighten-only·TelemetryMonitor 🕹️ RIO/CPU 격리 패널 — δ=0·liveArmed:false, 6렌즈 적대검증 확정 2결함 교정)
- **커널 텔레메트리 IPC 이중버스 서버**(음원 관제 앱 브리지 서버측 — NamedPipe 24B QPC 프레임 + MMF 단일슬롯 seqlock 미러 이중버스·비상 FREEZE→CausalWeaveClock 비가역 봉인·δ=0 송출·6렌즈 적대검증 확정 6결함 교정)
- **Genesis 실전 점화 전 정교성 점검 패브릭**(δ=0 종합 Pre-Flight 인증 4항목 채점·단1개 부적격 fail-closed veto+포렌식 · 무장 임대 300s 자동 하향 · 데드맨 스위치 500ms 행→미체결 전량취소+ForceFreeze · ★Think Beyond fat-finger 가격괴리+폭주 명목속도 사출 방화벽[Knight Capital 방어]을 GatedOrderDispatcher 초크포인트 배선 · TelemetryMonitor 🛡️ Pre-Flight 관제 패널 — δ=0·liveArmed:false, 6렌즈 적대검증 확정 3결함 교정)
- **AVX-512 초저지연 미시 알파 엔진**(L2 10호가 피처 16×float → 512-bit ZMM 병렬 점적[FMA] 알파·외부 Python/C++/GPU IPC 0·L1/L2 인캐시 δ=0 · 티어드 무손실 강등[Avx512F.FusedMultiplyAdd→Fma.MultiplyAdd→스칼라] · 64B 정렬 SIMD 벡터라이저[정렬 로드 페널티 박멸] · SIMD SNR 노이즈 양자화기[허수 호가 스푸핑 소거] · ★Think Beyond 실행폭 오토튜너[ZMM 웜업+다운클럭을 단일 지연-피드백으로 통합] · TelemetryMonitor ⚡ AVX-512 패널 — δ=0·liveArmed:false[알파 수치만 산출·발주는 무장 게이트 전담], 6렌즈 적대검증 확정 0결함)
- **안티-RAM 퀀텀 금고 & Phase 83 인프라 해방 & WebOnly 이중 빌드**(디버거 댓치/디코이 변조/Native API 후킹 탐지 시 시크릿 검증 제로화+ForceFreeze+FailFast 자가 소멸[기본 dormant·옵트인] · ★Think Beyond 판독-검증 시큐어 제로화[와이프를 신뢰 않고 readback 증명·JIT 제거 방지] · 허니키 디코이 트랩 · anti-hook blockWhile tighten-only · ISystemConfigPort/IFileSystemPort 포트+어댑터 + AtomicConfigSnapshotPort RCU[δ=0 락프리] · infra-scan.ps1 결합도 래칫[실측 baseline 138·비회귀] · Default∧WebOnly 이중 빌드 0W/0E · TelemetryMonitor 🔒 패널 — δ=0·liveArmed:false[탐지/제로화 순수 방어·발주 유발 0], 6렌즈 적대검증)
- **초저지연 스마트 오더 라우터 & 빙산호가 포획 엔진 & Phase 84 인프라 해방**(SIMD 알파+L2 스프레드/깊이 → δ=0 순수 라우팅 계획[방향·시장가공격/지정가수동·빙산흡수·슬라이스] · 마이크로 체결→나노초 리로드 서명 O(1) 링 스캔 빙산 숨은물량 포획 · 가격-시간 우선 큐 위치 체결임접‰ 추정 · ★Think Beyond 스푸핑 취소속도 진위 필터[진짜 빙산=체결 vs 가짜 스푸핑=취소 분별→라우팅 사전 차단] · Phase 84 config 직결 138→117[≤120]·infra-baseline 래칫 exit 0 · TelemetryMonitor 🎯 패널 — δ=0·liveArmed:false[라우팅 '계획'만·강한 계획→미무장 dispatcher→forwarded 0 실증], 6렌즈 적대검증)
- **극단적 카오스 결함 주입 샌드박스**(δ=0 결함 주입 엔진[소켓 지터/패킷드롭·메모리 압박·서멀 드롭·seqlock torn 비트마스크]·주입 시 무장 SafeHold[가역]·파멸 강도 ForceFreeze[하드] · SplitMix64 시드 100% 결정론 재현기 · 단계별 재인증 복구 스로틀러[Thundering Herd 거부] · ★Think Beyond bounded-progress 진행 보장 가드[스핀 경계 라이브락 O(1) 탈출 + 원자 이중버퍼 torn '불가능'化를 단일 불변식으로 통합] · 무장 술어 blockWhile 3종 tighten-only 합성 · TelemetryMonitor 🧪 패널 — δ=0·liveArmed:false[극단 결함/파멸 동결 하에서도 미무장 dispatcher→forwarded 0 실증], 6렌즈 적대검증)
- **Genesis Live Ignition Gateway**(실자본 사출 파이프라인 최종 빗장 — 하드웨어 밸브 키 ∧ 마스터 물리 무장 키 이중 논리곱 ∧ 전 방어선 ∧ 키 펄스 생존 δ=0 산출·전부 PASS 시에만 onSend 1회성 원자 개통 · δ=0 MMF tri-state 링 포렌식 감사[µs+FNV 해시·무결성 검증] · 50ms 하드웨어 키 펄스 심박[단선 가역/지속 하드 래치·dormant 분별] · ★Think Beyond 봉인 사출 인터락[주문별 exactly-once CAS 래치 + 불변 페이로드 동결 + FNV seal 을 단일 봉인으로 → 이중 사출·torn payload 0%] · LiveAccountingMesh Freeze 레이스 핀포인트 교정[publish-after-effect·herd 100% 결정론] · TelemetryMonitor 🚀 패널 — δ=0·liveArmed:false[이중 물리 키 미결속 시 강한 사출도 실물 0비트], 6렌즈 적대검증)
- **Live Flight Certification & Mock Dry-Run Fabric**(실자본 집행 직전 $0원 100% 가상 검증 + 전역 암호 봉인 — δ=0 가상 체결 미러[실전과 동일 SealedEmissionInterlock 봉인 경로 통과 후 소켓 직전 미러 루프백·adverse 슬리피지/부분/RTT·IBroker 참조 0] · 무장 직전 전역 ~48 불변식 총괄 채점[0~100]·100점 시에만 키드-FNV 봉인·무장 차단 · ★Think Beyond 스코프-에폭 펜스[드라이런↔실전 전환 시 에폭 전진(스코프 식별)+가상 잔량 O(1) 퍼지(오염 차단)+100ms 쿨다운(유령 폭주 차단)] · TelemetryMonitor 🛩️ 패널 — δ=0·liveArmed:false[드라이런 100% 가상 체결이어도 물리 이중 키 전 실 브로커 0비트], 6렌즈 적대검증)
- 2,297 테스트 클린런, 전역 플래키 0%, 빌드 경고 0(Default+WebOnly+PrimesBench 삼중)

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
| v10.1.0 (Phase 93-B) | ★Admin.razor 물리 이주 & Pure DTO Arc Slice 2(Ports & Adapters UI 격리) — 정직 정찰로 명령 전제(Mail 이주/Portable/Port 인터페이스 기존)가 실제 저장소와 배치(전부 부재·모놀리식) 실증 후 지휘관 결정으로 greenfield 신규 구축. 신규 순수 UI 프로젝트 `MS2E.UI.Portable`(net8.0·엔진 무참조 RCL): `Admin.razor` + Port(`IAdminCommandPort`/`IMailAuditLogPort`/`IAdminSecretPort`) + 순수 불변 DTO + 가변 뷰모델(낙관적 mutation·Mail 교훈 명명 분리). 어댑터 `AdminPortAdapters.cs`(엔진 감사/금고/시크릿 바인딩·seal 검증)·얇은 호스트 `AdminHostPage.razor`(@page "/admin"→`<Admin/>`). Gemini `AdminCommandPortDispatcher`(δ=0 커맨드 버스·불변 Intent 포맷+봉인+전송) + `SecureAdminViewPresenter`(시크릿 char 버퍼 경유 마스킹→`CryptographicOperations.ZeroMemory` 0x00·plain string 미생성). ★Think Beyond `AdminAuthoritySealGuard`(제미나이 A[핸드셰이크]/B[freeze-purge]를 '권한 봉인' 단일 불변식 통합 초월: 세션 비밀 키 keyed-FNV 봉인→어댑터 VerifySeal[rogue 위조 불가] + ForceFreeze 능동 Revoke[RegisterFreezeObserver·세션키 0x00→전 봉인 무효+시크릿 퍼지]). 3렌즈 적대검증 6 실결함 교정(★F1 CRIT: 공용 IssueSeal 무인증 발급 오라클→internal 격리·F2 HIGH: BeginSession(0) known-key fail-open→fail-closed·F3: 하드코딩 키→런타임 RNG·단문≤3자 마스킹 전량노출→전량마스킹·lazy freeze revoke→능동·stackalloc 상한). +9 테스트. ★liveArmed:false(Admin 명령=금고/캐시/시크릿 관제·IBroker·소켓·실 발주 유발 0·어댑터는 flag 토글+감사 각인뿐)·δ=0(봉인/디스패치/마스킹 힙 0·record struct)·삼중 0W/0E(신규 RCL 포함)·전체 2306(vault 백그라운드 플래키 격리·무관) |
| v10.1.0 | ★Live Flight Certification & Mock Dry-Run Fabric(실전 점화 드라이런 및 최종 인증 패브릭) — 실자본 집행 직전 $0원 100% 가상 검증 + 전역 암호 봉인. 과제1 `MockExecutionOrderBookMirror.cs`(δ=0 가상 체결 미러·실전과 '동일한' SealedEmissionInterlock 봉인 경로[exactly-once + 페이로드 동결 + FNV seal] 통과 후 최하위 소켓 전송 직전 미러로 원자 루프백[broker.Place 대체·IBroker/소켓 참조 0]·adverse 슬리피지[SplitMix64 매수↑/매도↓]·수량비례 부분 체결·가상 RTT·Reset=가상 잔량 O(1) 제로화). Gemini 보강 `LiveFlightCertificationSealer.cs`(물리 이중 키 무장 직전 RingTelemetry 프레임의 전역 불변식 ~48항목 δ=0 총괄 채점[0~100·로컬 함수 무할당]·100점 전 항목 PASS 시에만 키드-FNV[HMAC-lite·비밀 키 시드·위조 방지] 봉인 생성·MMF 감사 각인·CertificationBlocking blockWhile tighten). ★Think Beyond `ScopeSealedTransitionBarrier.cs`(제미나이 A[퍼지+쿨다운]/B[스코프 식별자]를 '스코프-에폭 펜스' 단일 원자로 통합·초월: 전환 시 ①ScopeEpoch 전진[이전 스코프 스테일化=식별] ②가상 상태 O(1) 퍼지[onPurge→mirror.Reset·오염 차단] ③100ms 쿨다운[RouteNow=Bounce·유령 폭주 차단]·VerifyScope 스테일 튕김). RingTelemetry +8(드라이런활성·가상체결·슬리피지·RTT·인증점수·봉인유효·봉인·스코프Bounce·Cell 1664→1728=64×27=194필드)·Publisher 3주입·★Sample 이 직전 프레임 인증 재채점. BlazorHomeForm 무장 술어 재구조화(liveArmed=물리키∧방어선∧인증봉인∧전환아님·armedPredicate=드라이런 급전[barrier RouteNow broker 물리 차단]∨liveArmed·게이트웨이 defensesPass=liveArmed) + TelemetryMonitor 🛩️ 패널(청/보라 [DRY-RUN CERTIFIED - MOCK MIRROR ACTIVE]·황색 인증중·암흑자색 인증실패/스코프오염 오버레이+포렌식). +7 테스트. ★liveArmed:false(★드라이런 100% 가상 체결이어도 물리 이중 키 전 실 브로커 소켓 0비트 — 미러 IBroker 참조 0·barrier RouteNow 가 broker 물리 차단·실전 Dispatch→미러 end-to-end 실증)·δ=0(SealAndVirtualFill/Certify/RouteNow/Transition 핫패스 힙 0·인스턴스 델리게이트 1회 캐시)·결정론(SplitMix64·FNV·주입 시계)·fail-closed(봉인 재검 실패→미체결·인증 1개 실패→봉인 무효·스테일/쿨다운→Bounce). 빌드 0/0(Default+WebOnly 이중), 전체 2274 |
| v10.0.0 | ★Genesis Live Ignition Gateway(실자본 오더 사출 파이프라인 최종 빗장) — 선행 레이스 교정 + 물리 이중 키 하드웨어 인터락 결속 + 봉인 사출. 과제0(선행) `LiveAccountingMesh.Freeze` IsFrozen↔ForceFreeze 순서 레이스 핀포인트 교정(_freezeClaim CAS 단발 청구 + publish-after-effect[ForceFreeze 먼저·_stateCode 마지막]→IsFrozen⟹att.Mode=EmergencyFreeze·herd 16스레드 100% 결정론). 과제1 `GenesisLiveIgnitionGateway.cs`(ValveWired ∧ LiveArmingWired 이중 물리 키 ∧ 전 방어선[blockWhile] ∧ 키 펄스 δ=0 산출·전부 PASS 시에만 onSend 1회성 원자 CAS 개통·매 사출 재검·veto 포렌식[밸브/무장/방어선/펄스]·OrderDrainWorker 최종 빗장 additive 배선). Gemini 보강1 `IgnitionAuditTrailRecorder.cs`(MmfFlightRecorder δ=0 멀티라이터 tri-state 링 미러·64B 레코드 µs[Int128]+FNV-1a-64 포렌식 해시·SnapshotRecent wrap-torn 격리·VerifyIntegrity 변조탐지·Seal). Gemini 보강2 `HardwareKeyPulseHeartbeatSentinel.cs`(DeadMan 주입시계 심박 미러·50ms 펄스·2티어[가역 복원 + 지속단선 하드 래치→IgnitionLock.Lock]·dormant/loss 분별). ★Think Beyond `SealedEmissionInterlock.cs`(제미나이 A[티켓 락]/B[커밋-동결 토큰]를 '단일 봉인'으로 통합·초월: 소켓 접촉 이전에 ①주문별 exactly-once 원자 CAS 래치[double-dispatch 0%] ②불변 페이로드 토큰 동결[반쪽 노출 불가] ③FNV-1a-64 무결성 seal[torn 탐지]·onSend 는 불변 토큰만 in-ref 수령). RingTelemetry +8(이중키결속·점화상태·veto수·veto코드·감사기록·키펄스·이중사출봉쇄·봉인사출·Cell 1600→1664=64×26=186필드)·Publisher 4주입(ignitionGateway/ignitionAudit/keyPulse/sealedEmission)·BlazorHomeForm 무장 술어 blockWhile keyPulse.SafeHoldActive tighten-only 합성(물리 키 없이 무장 불가) + TelemetryMonitor 🚀 패널(청/녹 [GENESIS IGNITION ACTIVE - PHYSICAL DUAL-KEY WIRED]·황색 키 펄스 단선·암흑자색 [IGNITION VETO] 오버레이+포렌식). +11 테스트. 6렌즈 적대검증 2라운드 확정 7(FP 5·liveArmed:false 실물 누수 0 확증) 교정(전부 게이트웨이 배선·안전성 위반 아님: PlaceSealed 메서드그룹 사출당 64B 할당→델리게이트 1회 캐시·게이트웨이 veto Forwarded 오계수→Drain Func&lt;bool&gt; overload[veto=GatedOut]·VerifyIntegrity torn 슬롯 오판→사후 재검 미러링). ★liveArmed:false(★이중 물리 키 미결속[기본]이면 강한 사출 시도도 브로커 소켓으로 단 1비트 실물 누수 0 — gateway 최종 빗장 + 봉인 인터락 물리 봉쇄·디스패처 드레인 forwarded 0 실증)·δ=0(Emit/질의/Record/TrySealAndEmit/Pulse 핫패스 힙 0)·결정론(주입 시계·FNV 순수·Int128 µs)·fail-closed(어느 키/방어선/펄스 실패→veto·미사출). 빌드 0/0(Default+WebOnly 이중), 전체 2267 |
| v9.1.0 | ★극단적 카오스 결함 주입 샌드박스(ChaosEngineSanitizer & Fault Injection Framework) — 극단 재난 하에서도 fail-closed 불변식 100% 사수 실증. 과제1 `ChaosEngineSanitizer.cs`(δ=0 결함 주입 엔진·비트마스크[소켓 지터/패킷드롭·메모리 압박·서멀 드롭·seqlock torn]·주입 즉시 SafeHoldActive[가역·tighten-only] 무장 하향·파멸 강도≥950‰→주입식 onForceFreeze 원자 하드 동결·IBroker/소켓/onSend/ring 참조 0·결정론 시드 SplitMix64 결정 스트림). Gemini 보강1 `DeterministicChaosReplayer.cs`(검증된 무할당 DeterministicPrng[SplitMix64] 재사용·시드 하나로 결함 순서/강도/조합 100% 결정론 재현·Reset(seed)→Next() 동일 시드=동일 시퀀스·시각 무관). Gemini 보강2 `ChaosRecoveryRateLimiter.cs`(카오스 해제 후 Thundering Herd 방지·Frozen→Cooldown→Ready FSM·단계별 재인증 AttemptReauth 이 쿨다운/단계수 간격 후에만 통과·너무 이른 재인증 거부·RecoveryBlocking tighten-only). ★Think Beyond `ChaosResilientProgressGuard.cs`(제미나이 A[스핀 경계]/B[이중버퍼]를 단일 불변식 bounded-progress 로 통합·초월: ①ChaosSpinFence[값타입·δ=0] 락프리 재시도 하드 상한 O(1) 탈출[라이브락 격리] ②원자 이중버퍼 에폭 체크포인트[비활성 그림자 슬롯 기록+Interlocked.Exchange 인덱스 스왑만 발행→부분 쓰기 구조적 불가시=torn '불가능'化] ③★독자 seqlock 재시도조차 같은 스핀 펜스로 상한→bounded-progress 가 라이브락+torn 을 한 메커니즘으로 동시 봉쇄). RingTelemetry +8(샌드박스상태·결함마스크·주입수·라이브락탈출·torn차단·복구상태·재현시드·SafeHold·Cell 1536→1600=64×25=178필드)·Publisher 4주입(chaos/chaosRecovery/chaosGuard/chaosReplayer)·BlazorHomeForm 무장 술어 blockWhile 3종 tighten-only 합성 + TelemetryMonitor 🧪 패널(청/녹 [CHAOS MONITOR ACTIVE - STANDBY]·적색점멸 주입중·황색 SafeHold·암흑자색 [LOCK-FREE BREACH] 오버레이+포렌식). +14 테스트. 6렌즈 적대검증 2라운드 확정 3(FP 8) 교정(전부 ChaosRecoveryRateLimiter 다중라이터: AttemptReauth TOCTOU→CAS 전진·BeginRecovery publish-before-init→데이터 먼저·최종단계 오버슛/중복완료→가드+단발 CAS)·herd 회귀 테스트 추가. ★liveArmed:false(극단 결함 주입/파멸 동결 하에서도 무장 술어 tighten→미무장 dispatcher→forwarded 0 실증·단 1비트 오발주 누수 봉쇄)·δ=0(Inject/질의/Next/TryCommit/TryRead/펜스 핫패스 힙 0 실증)·결정론(시드 SplitMix64·벽시계 무참조·고정 1e7)·fail-closed(패킷드롭=미발송·파멸=ForceFreeze·라이브락/torn=O(1) 무효화). 빌드 0/0(Default+WebOnly 이중), 전체 2256 |
| v9.0.0 | ★초저지연 스마트 오더 라우터 & 빙산호가 포획 엔진 + Phase 84 인프라 해방 Slice 3. 과제1 `SmartOrderRouterEngine.cs`(v8.0.0 SIMD 알파[부호·강도]+L2 스프레드/깊이 → δ=0 순수 라우팅 계획: 방향·시장가공격/지정가수동·빙산흡수·슬라이스폭·슬라이스수. 라우팅 코드 비트 R_NO_ALPHA/R_SPOOF_BLOCKED/R_MARKET_AGGRESSIVE/R_LIMIT_PASSIVE/R_ICEBERG_ABSORB. onSend·IBroker·소켓·ring 참조 0 — '계획'만). 과제2 `IcebergHunterSentinel.cs`(bid/ask 각 IcebergReloadSignatureDetector 래핑·마이크로 체결→리로드 서명 O(1) 링 스캔·숨은 빙산 물량=cumTraded−displayed 추정). Gemini 보강1 `IcebergReloadSignatureDetector.cs`(주입 ticks 나노초 dip→refill 지터·reloadThreshold 내 refillFraction 재적재→Reloads++·ConfirmationPermille obs<4=0). Gemini 보강2 `QueuePositionEstimator.cs`(가격-시간 우선 FIFO 큐 위치·Place/OnTrade[ahead 소각]/OnCancelAhead→FillImminencePermille 0~1000). ★Think Beyond `LiquidityAuthenticityVerifier.cs`(Anti-Spoofing Cancel-Velocity Filter — bid/ask 256슬롯 링[ticks,fill,cancel]·authenticity=fill·1000/(fill+cancel)·cancel/fill>3000‰ 스푸핑·AllowRoute 게이트가 진짜 빙산[체결]과 가짜 스푸핑[취소] 분별→SOR 사전 차단 SpoofBlocks++). RingTelemetry +8(SOR활성·라우팅수·라우팅코드·빙산숨은물량·빙산포획수·큐임접‰·스푸핑차단·유동성진위‰·Cell 1472→1536=64×24)·Publisher 4주입(sor/icebergHunter/authVerify/queueEst) + TelemetryMonitor 🎯 패널(청/자 [SOR ACTIVE - ICEBERG HUNTER ARMED]·황색 [SPOOFING DETECTED - ROUTE BLOCKED]·암흑자색 [SOR ROUTING ANOMALY] 오버레이+포렌식). Phase 84: AiService·EmailSender·Diagnostics 잔여 config 직결→DatabaseConfigAdapter(ISystemConfigPort) 경유 해방(콜드 경로·행동 완전 보존)·infra-scan 결합도 138→117(≤120)·infra-baseline 래칫 exit 0. +10 테스트. 6렌즈 적대검증(find→반증-verify) 확정 12(FP 5) 교정(SOR 빈/크로스 오더북 fail-open→가드·LiquidityAuth/Queue/SOR Int128 오버플로 방어·Iceberg hidden phantom→0·Reset 내부검출기 초기화·Stopwatch.Frequency 호스트의존→고정 1e7·refill 창 조기종료→부분반등 유지·Queue torn read→seqlock). ★liveArmed:false(강한 라우팅 계획→ring publish→미무장 dispatcher→forwarded 0 실증)·δ=0(ComputeRoute/Queue/Iceberg/Auth 힙 0)·결정론(주입 시계·RNG 0·주입 alpha/틱)·fail-closed(alpha 0/스푸핑→R_NO_ALPHA/R_SPOOF_BLOCKED). 빌드 0/0(Default+WebOnly 이중), 전체 2242 |
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

---

## 16. v12.14.1-GM 누적 하드닝 로그 (2026-09-30 ~ 10-05)

### 16.1 보호 지정가 변환 · 호가 깊이 가드 (§1.24 대응)
- **실결함 교정**: 피드 연동 후에도 시장가가 `PaperKind.Market` 그대로 브로커 송출 → `IOrderRouteGate.TryAdmit(in p, out OrderAdmission)` 가 송출 형태를 반환하고 `OrderDrainWorker` 는 승인값으로만 송출(송출 Kind 항상 지정가).
- `StockManager/Engines/ProtectiveLimitOrderConverter.cs` — 매수 상한 = 참조가×(1+offset) 내림 정렬 · 매도 하한 ×(1−offset) 올림 정렬 · 가격제한폭(기준가 ±30%) 보호 방향 클램프/이상치 거부 · Int128 포화 · offset ≤ 라우터 슬리피지 한도 강제(기본 30bps).
- `KrxTickAligner`(같은 파일) — QPrice raw 7구간 표, 기존 `KrxTickLadder` 와 전 구간 동치. ★`static ReadOnlySpan<long> X => new long[]{…}` 가 이 빌드에서 호출당 144B 할당(δ=0 테스트 실측) → 정적 readonly 배열로 고정.
- `StockManager/Engines/OrderbookDepthGuard.cs` — `DepthBook`(IDepthSink · 1~3호가 · 보호선 이내 잔량 · Ok/Reduced/Insufficient/Missing/Stale) + `ExtremeSlippageDisarmInterlock`(체결가 ↔ 판정 참조가 불리 괴리 200bps 초과 → sticky Disarm · `ArmingInterlockSource.ExtremeSlippage` · 물리 이중키 재무장).
- 대사 로그 신규 종류: `DepthRejected` · `DepthReduced` · `SlippageRejected` · `ExtremeSlippage`.

### 16.2 운영 주의(필독)
- **L2 호가 피드 생산자 0**: Toss 게이트웨이는 seq/symbol/price/qty 만, 키움 `TickPayload` 는 현재가·거래량만 파싱 → `DepthBook` 이 비어 있어 `Router.MarketRequiresDepth=true`(기본)이면 **실전 경로 시장가 전량 거부**. 시장가 허용 = L2 피드 결선 또는 `Router.MarketRequiresDepth=false`(보호 지정가만 적용 · 깊이 검사 생략).
- **기준가 미주입**: 가격제한폭 클램프는 `MarketDataSpikeInterlock.SetReference` 주입 시에만 동작 — 현재 호출자 0.
- 지정가는 깊이 축소 미적용(가격이 보호선 · 대기 허용). 체결량 0 통보는 거부·접수·취소확인 구분 불가(프록시 프로토콜 한계) → 캐스케이드 취소 슬롯만 해제, 나머지 TTL(120s) 회수 + 무장 차단.
- 실전 무장 후보 조건(집계 비트): 부팅 무결성 + 외부 앵커(`Release.ExpectedMasterSeal` = `release/master-seal.txt`) · Toss 틱 수신 · 키움 프록시 생존 · 슬롯/극한 슬리피지 래치 미발생.

### 16.3 검증 · 지문
- 사중 빌드 Release 0W/0E · 전체 3,201 테스트 PASS(최종 2회 연속). 1회 실행에서 `MmfIpcFabricTests.Spsc_Concurrent_InOrder_NoTornNoLoss` 간헐 실패 관측 — 미수정 커밋 파일 · 단독 30/30 통과 · 부하 의존 플레이키(별도 Triage 작업 칩 생성).
- `ProtectiveLimitConverterTests.cs` 20건(교정 끔 Red 12건) · 힙 할당 0(정렬·변환·깊이 판정·송출 수명주기).
- SHA-256 앞 16자(커밋 직전 작업 트리 기준):
  `d3a3054cead3ac80` Engines/ProtectiveLimitOrderConverter.cs · `f2c1f21e6aabb86a` Engines/OrderbookDepthGuard.cs · `a933146aad377aba` Adapters/RouterFeedGate.cs · `0eca8eb3c6d6fe2f` Adapters/OrderExecutionFeedAdapter.cs · `935e3208719ddc53` Engine/IOrderRouteGate.cs · `f2582a3123b6a906` Engine/OrderDrainWorker.cs · `8e2c764b1e080a90` Tests/ProtectiveLimitConverterTests.cs

### 16.4 같은 커밋에 포함된 선행 마일스톤(미커밋 누적분)
Phase 1 감사 교정 · Phase 3 UI/DI 감사(ErrorBoundary 격리) · Phase 4·F7 부팅 무결성/안티탬퍼 · PII 하드닝 · MMF 포렌식 · 다중 브로커 라우터 · 시세 스트림/렌더 거버너 · 알고 집행(VWAP/TWAP) · 카오스 동시성 Triage · 원자 인터락 집계기 실배선 · 봉인 수검 CLI(`--verify-seal`) · 라우터 참조가/체결 피드 — 상세는 각 단계 STATUS 이력·커밋 본문.

---

## 17. v12.14.1-GM 출하 직전 하드닝 · 릴리스 CI 게이트 (2026-10-05 ~ 10-06)

### 17.1 키움 경로 마일스톤(커밋 9f3b7ef 대상)
- **MMF IPC 트리아지**: 링 포화 시 라이터 하트비트 갱신(배압 중 생존 오판 제거) · s2 재확인 전 `Interlocked.MemoryBarrier` · `ArmingInterlockSource.MmfFabric`(9) 결선. 1,000 라운드 SPSC 스트레스.
- **키움 프로토콜 명시화**: FillPayload 56B(`Status` 매직 0xA5 + 브로커 잔량 902) · 거부 0ms 슬롯 반납 · 접수 장기 TTL · 잔량 불일치 → `BrokerStateMismatch`(10) Disarm · 대사 포렌식 체인 각인.
- **L2 Depth**: 키움 10호가(`OrderBookPayload` 336B) → `L2OrderBookDepthParser`(교차/역전/공백/편측) → `DepthBook`(네이티브 seqlock 저장소) + 스프레드 상한 · 플래시 크래시 `OrderbookIlliquidity`(11) · 기준가 자동 주입(가격제한폭 클램프 활성).
- **지연 프로파일러**: `TickToTradeLatencyProfiler`(µs · P50/P99 · 네이티브 히스토그램 P99.9) · 송출 직전 정지 임계 초과 = 미송출 + `LatencyStall`(12). `UnmanagedOrderBookBuffer`(SafeHandle 결정론 해제).
- **키움 COM 코어 이관**: `KiwoomComEventCore`(COM 무의존 · 가짜 OCX 전 경로 테스트) · OnReceiveMsg 서버 거부 분류 · 주문번호 경합 보류 · STA 펌프 감시 · 중대 오류 `KiwoomServerError`(13).
- **정정/취소 · 계좌 동기화**: 취소 3/4 · 정정 5/6 · 원주문번호 D7 · 잔량 초과/중복 차단 · ★취소/정정 새 주문번호 사상(FID 904 폴백) · 호스트 `IOrderAmendGate` · Cancel-Fill Race 배리어 · `AccountStateSyncAdapter`(OPW00018/OPW00001 · δ=0) · 주문가능금액/보유 게이트 · 정적 구간 잔고 괴리 `AccountDivergence`(14).

### 17.2 릴리스 CI 게이트(이번 마일스톤)
- **오중 빌드 5번째 = `tools/KiwoomComShim/KiwoomComShim.csproj`**(솔루션 등재 · x86 · `KIWOOM_COM` · 경고=오류): 실제 `ProductionKiwoomApi.cs`/`IKiwoomApi.cs`/`KiwoomComBridgeCore.cs` 링크 + `AxKHOpenAPIShim.cs`(공개 문서 시그니처 형상). 테스트 프로젝트가 `ReferenceOutputAssembly=false` 로 참조 → **테스트 빌드마다 `#if KIWOOM_COM` 블록 컴파일**. 산출물은 출하 금지(스테이징은 host/proxy bin 만).
  - 위치를 지시서의 `StockManager/Tools/` 가 아닌 루트 `tools/` 로 둔 이유: 호스트 SDK 기본 Compile 글롭이 쉼 소스·obj/*.cs 를 흡수(가짜 AxKHOpenAPILib 가 호스트에 섞임 · AssemblyInfo 중복).
- **방향 A `ZipSealDryRunVerifier`**(`StockManager/Tools/ReleaseHandshake.cs` · CLI `--verify-zip <zip> --seal-file master-seal.txt`): zip 무추출 · 항목 스트림 SHA-256 · 내장 manifest.sig ↔ 앵커 · 실측 해시로 v2 봉인 재계산(봉인 필드만 맞춘 위조 차단) · 미등재/누락/중복/경로 이탈 0. 실 릴리스 zip(38 아티팩트) Ok 실측.
- **방향 B `MultiRepoReleaseManifest` + `GitRepoProbe`**(CLI `--sync-release-manifest` · `--release-handshake`): `release/RELEASE_MANIFEST.json`(schema `ms2e-multirepo-release/1`) = 태그·소스 커밋·manifest.sig 해시·masterSeal·아티팩트 38·zip·쉼 산출물 해시·`liveArmedDefault=false`·인터락 목록 + 피어 QuantumVault HEAD/봉인 매니페스트(`release/offline-bundle/RELEASE_MANIFEST.json`) 해시·rootHash. 정규화 문자열 SHA-256 자기 봉인. 기록 직후 재수검 통과 시에만 원자 교체.
  - **피어 QuantumVault 는 읽기 전용**(2026-10-06 사용자 결정 — 별도 제품 · v2.7.0 봉인 동결 · 매니페스트 수정 시 rootHash 봉인 파괴).
- **`release-package.ps1` 0/10 ~ 10/10**: [0] 빌드 전 소스 무결성(태그 존재 · 태그↔HEAD 코드 이탈 0 · 미커밋 코드 0 — 문서/release/.claude/docs 제외) · [2] 쉼 산출물 갱신 확인 · [9] 무추출 zip 수검 · [10] 동기화 + 독립 핸드셰이크. 실패 = zip·.sha256·master-seal.txt·RELEASE_MANIFEST.json 전량 폐기.

### 17.3 ★출하 차단 사실(조치 필요)
- **태그 `v12.14.1-GM`(9f3b7ef)은 빌드 불가**: 커밋에 `NullKiwoomApi.cs` 는 포함됐으나 그 형식을 정의한 `StockManager.KiwoomProxy/IKiwoomApi.cs`·`KiwoomProxyHost.cs` 가 누락(작업 트리에만 존재) → `git archive v12.14.1-GM` 클린 빌드 시 x86 프록시 CS0246 5건. 현 `release/` zip 은 작업 트리 빌드라 태그 소스와 불일치.
- 조치: 두 파일(+ 이번 CI 게이트 파일) 커밋 → 태그 재지정(원격 푸시 여부 확인 후) → `release-package.ps1` 재실행. 현재 상태에서 스크립트는 [0/10] 에서 정확히 거부(빌드 0 · 기존 zip 무변경 실측).

### 17.4 검증
- 오중 빌드 Release 0W/0E · `QuantumVaultPipelineIntegrationTests.cs` 27건(방어 끔 Red 10건) · 전체 스위트 결과는 STATUS.md.
- δ=0 범위(정직): 항목 해시 루프·봉인 재계산·hex 대조만 0B 실측. ZipArchive 디렉터리/Deflate · JSON 파싱 · git 프로세스는 콜드 1회 할당.

---

## 18. E2E 모의 거래 · 7대 인터락 스트레스 하네스 (2026-10-06)

### 18.1 전제 검증 결과(정직)
- **출하 드라이런은 실전 게이팅 사슬을 리허설하지 않는다**: `Dispatch.DryRunActive` 시 `ScopeSealedTransitionBarrier` 가 주문을 `MockExecutionOrderBookMirror` 로 돌리는데, 이 분기는 `OrderDrainWorker.Dispatch` 안에서 인터락 최종 veto · `RouterFeedGate`(참조가/깊이/보호 지정가/추적 슬롯) · Tick-to-Trade 프로브 **이전**에 있다. → 하네스는 실전 사슬을 그대로 조립하고 IBroker 자리에만 `VirtualPaperBroker` 를 꽂는다.
- **인터락 Disarm 의 포렌식 각인 경로 0**: 지시서의 "격발 시 MMF 포렌식 체인 즉시 사출"은 존재하지 않았다(체인에는 대사·봉인 이벤트만). → `IInterlockTransitionSink` 신설.
- **송출 1건당 문자열 할당**: `OrderDrainWorker` 가 `SymbolCode.ToString("D6")` 로 주문마다 새 문자열 → `KseSymbolText` 캐시(적중 δ=0).
- 지시서의 "0.1ns Atomic Disarm" 은 물리적으로 불가(Interlocked.Or 1회 ≈ 수 ns). 검증 기준은 "격발 호출 반환 시점에 `Blocked` 가 이미 true(비동기 지연 0)".
- "Pii" 인터락 = `RawCredentialLeakInterlock`(CredentialLeak), "MarketDataSpike" = `MarketSpike` 비트.

### 18.2 구현
- `ArmingInterlockBitmask.AttachTransitionSink` — Interlocked.Or/And 직전 값으로 **실제로 바뀐 비트만** 정확히 1회 통지(동시 Trip 경합에서도 중복 0). 관측자 예외는 차단에 무영향(비트 선반영). 교체 금지.
- `StockManager/Telemetry/InterlockForensicRecorder.cs` — 전이 → `ZeroAllocForensicAuditStream`(Disarm/Arm · payload = 비트) + `MmfFlightRecorder`(InterlockRaised/Cleared). 호스트 결선.
- `StockManager/Simulation/PaperTradingSimulationHarness.cs` — `VirtualPaperBroker`(네트워크 0 · IsLive=false · 사전 할당 주문번호) · `SevenLayerInterlockInjector`(실 인터락 객체를 실제 Trip 경로로 격발) · `SimulationTelemetryChannel`(seqlock · 방향 A) · 하네스.
- 방향 A: 텔레메트리 스냅샷 → `SnapshotRenderGovernor.Signal(version)` — 창당 선행+후행 렌더로 합치기. ★거버너는 Idle 상태 첫 신호에서 **신호 스레드에서 동기 렌더**하므로 실제 Blazor 결선 시 render 콜백은 `InvokeAsync` 위임만 해야 한다(UI 페이지는 이번에 만들지 않음 — RatchetCeiling 불변).
- 방향 B(안전 변형): `StockManager/Adapters/LiveArmedEgressSentinel.cs` — 실 브로커 앞 데코레이터. 물리 이중키 미충족 Place = 전달 0 · 증거 · sticky `EgressBypass`(15 · 필수 비트) · ForceFreeze · 이중키 재무장. 호스트 `OrderDrainWorker` 는 센티널 경유.
  - **프로세스 소켓 물리 차단은 구현하지 않음**: 관리자 권한 방화벽 변경(시스템 보안 설정) 필요 + 시세·체결 피드까지 끊겨 이미 나간 주문 상태를 볼 수 없게 됨.

### 18.3 검증
- `PaperTradingE2EStressTests.cs` 19건(방어 끔 Red 16건): E2E 전량 체결·대사 잔존 0 · P50≤P99≤P99.9 · 계층별 단일 격발 정확 비트 + 체인 Disarm · 7계층 동시 격발 40라운드(비트당 전이 정확히 1회 · 체인 무결 · 이후 송출 0) · seqlock 20만 게시 찢김 0 · 1만 틱 렌더 합치기 · 센티널 · 하네스 객체 그래프 실 브로커 0 · E2E 정상 상태 δ=0.

---

## 19. v12.14.1-GM 최종 출하 기록 (2026-10-06)

### 19.1 커밋 · 태그
- `09070c7` — v12.14.1-GM Production Golden Master(E2E 하네스 · 7대 인터락 · 릴리스 CI · 태그 9f3b7ef 누락분 IKiwoomApi.cs/KiwoomProxyHost.cs 복구). 21파일 선별 스테이징(타 세션 BrokerSettingsPage.razor · InstallerBuildPanel.razor · .claude/* 제외).
- `7681aa5` — fix(release-blocker): 백그라운드 감시 스레드 시작↔처분 경합 크래시 교정(아래 19.2).
- 태그 `v12.14.1-GM`(annotated) 재지정 이력: 9f3b7ef(클린 빌드 불가) → 09070c7 → **7681aa5**. 원격 저장소 없음(푸시 이력 0 — 재지정 영향 범위 로컬 한정).
- 클린 체크아웃(별도 git worktree) 오중 빌드 Release `-warnaserror` 0W/0E.

### 19.2 ★출하 중 발견 · 교정한 실결함(release-package [3/10] 이 차단)
- 증상: 테스트 호스트 조기 종료(684 · 1,560/3,353 · 실패 0). Windows 이벤트 로그 1026 = testhost 미처리 `ObjectDisposedException(ManualResetEventSlim)` ← `AssemblyAntiTamperHandshake.MonitorLoop`.
- 원인: `StartMonitor` 가 CAS 로 스레드를 게시한 뒤 `Start` — 그 사이 `BootIntegrityGate` 비동기 수렴(Task.Run)과 경합한 `Dispose` 가 미시작 스레드(IsAlive=false)를 보고 Join 생략 후 `_stop` 처분 → 모니터가 처분된 이벤트 Wait → 프로세스 종료. **앱 종료 시에도 동일 크래시 가능.**
- 교정: AssemblyAntiTamperHandshake · ForensicChainDisarmInterlock 시작/처분 lifecycle 락(시작 후 게시) + 처분 경합 예외 흡수 · BootIntegrityGate `Adopt`(처분 후 생성된 핸드셰이크 자가 처분 — 아티팩트 파일 핸들 누수 동반 교정).
- ★출하본 미포함(태그 이후 · 작업 트리 미커밋): GenesisPreFlightUpdater/PortfolioRiskUpdater 루프 처분 경합 흡수(Join 2초 타임아웃 후 `_stop` 처분 → 다음 Wait 크래시 경로) — 앱 종료 시 크래시 가능성. 다음 버전 커밋 대상.
- 증거: `MonitorLifecycleRaceTests` 4건 — 수정 전 코드 3/3 테스트 호스트 크래시(동일 예외) · 수정 후 통과.
- ★미해명 1건: 교정 커밋 후 첫 packaging 실행에서 테스트 호스트가 243건에서 1회 더 종료(예외 출력·이벤트 로그·WER·덤프 모두 없음). 직접/PowerShell/빌드 직후/새 경로 등 24회 재현 실패 후 packaging 재실행에서 전 단계 통과. 같은 시각 Windows 방화벽 프롬프트(새 경로 testhost — `V560V600PathGammaTests` 의 0.0.0.0 UDP 멀티캐스트 바인딩)가 관측됐으나 인과 미입증 — 외부 요인(보안 제품) 가능성 · 후속 추적 권장.

### 19.3 출하 아티팩트(release-package.ps1 [0/10]~[10/10] 전 단계 통과 · 클린 worktree @7681aa5)
- `release/release-v12.14.1-GM.zip` — 2,903,759 B · SHA-256 `E7C73D63625B578FBD74C8601008C1F9583E586FB13AAFDACD3E8A6EED2D1B31`
- `release/master-seal.txt`(배포 PC `Release.ExpectedMasterSeal`) — `038981351CF2C3E1310C6C17CF95BAB71D5973D82C432738CFE7C18FD3A5EBFE` · 아티팩트 38
- `release/RELEASE_MANIFEST.json` — v12.14.1-GM @7681aa50ec · 피어 QuantumVault@57b18226ae(읽기 전용 관측)
- 독립 재수검: `--verify-seal`(Sealed · 앵커 일치) · `--verify-zip`(Ok 무추출) · `--release-handshake`(Ok) 전부 exit 0.
- 구 산출물(작업 트리 빌드 · 태그 불일치)은 `release/superseded-9f3b7ef-worktree-build/` 로 보관(삭제 아님).

---

## 20. 차기: 사전 검증 · 포트폴리오 리스크 인터락 (2026-10-06 · 미커밋)

### 20.1 전제 검증
- 지시서의 `StockManager/Security/GenesisPreFlightUpdater.cs` · `StockManager/Engines/PortfolioRiskUpdater.cs` 는 실제 `StockManager/Blazor/` 에 존재. 대기 패치 = 처분 경합 흡수(§19.2 후속).
- 이미 있던 것: 사전 인증(GenesisPreFlightCertifier) 거부 · VaR 한도 초과 · 무장 임대 만료는 liveArmed 술어로 무장 차단. **없던 것**: 단일 종목/섹터 편중 송출 차단 · 시계 역행 · 안전 설정 런타임 변조 · 프록시 파이프 서버 신원 검증.
- 이 호스트에는 운용자가 인터락을 재무장(TryRearm)하는 UI 경로가 없다 → sticky 비트 복구 = 원인 제거 후 재시작.

### 20.2 구현
- `StockManager/Security/BootPreFlightSentinel.cs` — ①부팅 시계 앵커(`%LOCALAPPDATA%\MS2E\preflight.clock` · 절대 뒤로 쓰지 않음 · 허용 2분) ②런타임 벽시계↔QPC 역행(정방향 점프는 재기준) ③안전 핵심 키 15종 지문(SHA-256) 부팅 봉인 후 5초 주기 재계산 — 실행 중 변경 = 변조(해당 키를 쓰는 UI 없음) ④프록시 신원 거부 보고 → `ArmingInterlockSource.PreFlightBoot`(16 · 필수) + killPurger/발주 토큰 즉시 0x00.
- `src/02_Infrastructure/Security/PipeServerIdentity.cs` + `KiwoomBridgeClient.ServerIdentityVerifier` — 연결 직후 GetNamedPipeServerProcessId → QueryFullProcessImageNameW 경로를 설치 레이아웃의 `proxy/MS2E.KiwoomProxyHost.exe`(또는 `Kiwoom.ProxyPath`)와 대조. 불일치 = 첫 프레임 전 파괴 + UnauthorizedAccessException. 클라이언트·프록시 서버 모두 `PipeOptions.CurrentUserOnly`.
  - ★프로덕션 호스트에는 `ConnectAsync` 호출부가 없다(브리지 미연결 상태) — 향후 실결선 시 자동 적용.
- `StockManager/Engines/PortfolioExposureGate.cs` — 총자산 = 예수금 + Σ 보유 × 마크가(키움 체결 틱). 매수 1건: (종목 보유 평가 + 승인 대기 + 명목) ≤ 단일 상한(기본 2,000bps) × 총자산, 섹터 합 ≤ 섹터 상한(기본 4,000bps). 평가 미상 · 스냅샷 잘림 = 거부. 섹터표 = `Exposure.SectorTablePath`("코드,섹터" UTF-8 · Utf8Parser 무할당) · 미등재 = 미분류 버킷. RouterFeedGate 판정 ExposureLimit 18 · SectorConcentration 19 · ExposureUnknown 20(계좌 예약 반환 · 라우터 슬롯 확보 전).
- `StockManager/Engines/CorrelationCrashInterlock.cs`(방향 B) — 보유 종목 세션 고점 대비 낙폭 ≥ 300bps 종목 수가 최소 3 ∧ 보유의 60% 이상 → `CorrelationCrash`(17 · 필수) + 브로커 접수 미체결 일괄 취소(OrderExecutionFeedAdapter.CopyOpenBrokerOrders → ExecutionBridgeBroker.Cancel).
- 대기 패치 정식화: GenesisPreFlightUpdater(Start↔Dispose 경합) · PortfolioRiskUpdater(Join 2초 타임아웃 후 처분) 루프 ObjectDisposedException 흡수.

### 20.3 검증
- `PortfolioRiskPreFlightTests.cs` 17건(방어 끔 Red 13건) · GenesisPreFlightUpdater 수정 전 코드 = 테스트 호스트 3/3 크래시(동일 예외) 실측 · 전체 3,374 Pass ×2 · 오중 빌드 -warnaserror 0W/0E.
- 한계: PortfolioRiskUpdater 패치는 DB/시세 의존이 커 전용 테스트 없음(동일 패턴 흡수) · 취소/거부된 매수의 승인 대기분은 자동 반환하지 않음(과대 = 안전 측 · `ResetPending`) · 지시서의 ReadOnlySpan<byte> 요구는 섹터표 수집에 적용(PortfolioRiskUpdater 는 콜드 DB 로더 유지).

---

## 21. 차기: KIS OpenAPI · 다중 브로커 라우팅 (2026-10-06 · 미커밋)

### 21.1 전제 검증 · 실결함
- **다중 브로커 미배선**: 호스트 스위치보드 브로커 1개 · `OrderAdmission` 에 브로커 인덱스 없음 · `OrderDrainWorker` 는 항상 단일 `IBroker` — 라우터/센티널의 브로커 선택·페일오버가 실제 송출 대상을 바꾸지 못했다.
  → `OrderAdmission.Broker`(RouterFeedGate 가 `r.Broker` 동봉) + `OrderDrainWorker(brokersByIndex:)` — 직접 경로·게이트웨이 봉인 사출(PlaceSealed) 양쪽. 미매핑 인덱스는 기본 브로커로 대체하지 않고 거부(슬롯 롤백).
- **KisLiveBroker 결함**: ①정적 단일 토큰(모의/실 도메인·AppKey 공유) ②로컬 시각 +23시간 추정 만료 ③`Contains("\"rt_cd\":\"0\"")` 성공 판정(공백 있는 정상 응답을 실패로) ④성공 msg 가 한글 안내문(IBroker 계약 = 주문번호 — 라우터 게이트 결속 실패 → 슬롯 TTL → OrderSlotAge Disarm) ⑤소수 수량 `(int)` 절단 ⑥Cancel 미구현 → 전부 교정(KIS TR ID 는 상수화 · 2024 신규 체계 공지 대조 필요).
- **KIS AppSecret 로그 평문**: FinancialPiiMasker 패턴(JWT · PS+34 AppKey)이 무구조 장문 AppSecret 을 못 잡음 → `RegisterSecret` 정확 일치 마스킹(⑦ · 최우선 적용).
- **PINGPONG 판별**: tr_id 판독 즉시 true → 잘린 JSON 도 반송. 완결 문서만 인정.

### 21.2 구현
- `src/02_Infrastructure/Adapters/Brokers/KisProtocol.cs` — `KisOrderIdSpace`(KIS 주문번호 = 비트 48 | ODNO · 키움 ID 와 충돌 0) · `KisNoticeCipher`(체결통보 AES-256-CBC: BCL 원샷 `Aes.TryDecryptCbc` 가 건당 184B 할당 → ECB 블록 복호기 1회 생성 + 수동 CBC·PKCS7 = 0B) · `KisProtocol`(실시간 헤더 · H0STCNT0 46필드 다건 · H0STCNI0 · 주문 응답 · 토큰 응답 — Utf8Parser/Utf8JsonReader · JsonException 만 catch).
- `StockManager/Adapters/KisOpenApiAdapter.cs` — `KisTokenLifecycle`(pinned 바이트 · 서버 만료와 KST 만료 중 이른 쪽 · 만료 5분 전 구간 갱신 실패/만료 = `KisAuth` 18 · 검증 통과 후에만 교체 · `TryWriteBearer` δ=0 · 새 토큰 마스커/유출 감지 등록) · `KisSessionInterlock`(방향 B: 미연결·침묵 타임아웃·끊김 = `KisSession` 19 + `BrokerFailoverSentinel.ReportFault(Disconnect)`) · `KisOpenApiAdapter`(프레임 → TickPayload/FillPayload · PINGPONG 반송).
- ★의도적 미구현: 지시서의 '장애 브로커 미체결 잔량 원자 재라우팅'·'KIS 전용 추적 슬롯 회수' — 끊긴 브로커의 주문은 살아 있을 수 있어 재송출 = 이중 체결, 회수 = 노출 상실(CLAUDE.md §2). 기존 센티널 설계도 장애 = 라우터 sticky Disarm(페일오버는 재무장 후 대상 선지정).
- 분할 송출(Split Dispatch) 미구현 — OrderExecutionFeedAdapter 가 제안 1건 = 슬롯 1개 · 라우터는 활성 브로커 1개로만 라우팅 → 자식 주문별 슬롯 + 브로커 지정 라우팅 설계 결정 필요.
- 호스트 결선 없음 — KIS 자격증명·WebSocket/HTTP 전송 계층이 없다(가짜 결선 금지 원칙).

### 21.3 검증
- `KisMultiBrokerRoutingTests.cs` 21건(방어 끔 Red 6건 · PINGPONG 수정 전 Red · Contains 판정 오판 실증) · 프레임 판독·AES 복호·통보 사상·Bearer·세션 판정 δ=0 실측 · 전체 3,395 Pass ×2 · 오중 빌드 -warnaserror 0W/0E.
- 한계: KIS 필드 순서·TR ID·응답 키는 공개 문서 기준(이 저장소에 VTS 실측 이력 없음) · KisLiveBroker 는 동기 HttpClient(네트워크 테스트 없음 · 소스 회귀 테스트만).

---

## 22. 차기: 무중단 롤오버 가드 · 헬스 에이전트 (2026-10-06 · 미커밋)

### 22.1 전제 검증
- `LiveSoakSentinel`(v12.5.0 "최초로 Disarm 에 결속" 주석)은 프로덕션 어디서도 생성되지 않는다(사문) — 호스트에 자원 폭주를 무장 차단으로 잇는 경로가 없었다.
- 키움 프록시(`KiwoomProxyHost.RunAsync`)는 파이프 연결 1개를 받고 끊기면 종료 — 키움 주문번호↔클라이언트 ID 사상표(KiwoomComEventCore)가 함께 사라진다. 새 호스트+새 프록시는 구 주문의 체결 통보를 사상할 수 없다(UnknownOrder).
- 단일 인스턴스 뮤텍스 없음(신·구 프로세스 동시 실행 가능). 프로덕션 호스트는 브리지 연결 호출부가 없고 프록시 진입점은 `NullKiwoomApi`(TODO) — 실 추적 슬롯은 현재 생기지 않는다.

### 22.2 구현
- `StockManager/Services/ProcessRolloverGuard.cs` — MMF(`Local\MS2E_ROLLOVER_V1`) 헤더 128B + 슬롯 80B×N. 구: `Publish`(Admitted 미결속 송신이 있으면 NotQuiescent) → SHA-256(epoch‖count‖슬롯) 봉인 → `WaitForAck`. 신: `TryAdopt`(매직/버전/상태/체크섬 검증 → 전량 ImportSlot → Ack) · 실패 = sticky 불일치. `ArmingBlocked = 불일치 ∨ (승계>0 ∧ 대사 미완료)` · `MarkReconciled()` 는 브로커 대사기(미구현)가 호출해야 할 지점.
- 호스트: 기동 시 승계 시도 → 성공 시 `ExecutionBridgeBroker.SeedOrderIdFloor`(승계 최대 클라이언트 ID 이하 재발급 금지) · `Rollover`(20) 필수 비트. 종료(OnFormClosing) 시 추적 슬롯이 있으면 발행 → `Rollover.AckWaitSeconds`(기본 5) 대기 → 미확인이면 운용자 확인(아니오 = 종료 취소). Windows 종료는 예외.
- `OrderExecutionFeedAdapter.ExportSlots/ImportSlot/InFlightUnbound/MaxTrackedBrokerOrderId` — 승계 슬롯은 이 프로세스 라우터에 기록이 없으므로 라우터 호출 생략(_adopted).
- `StockManager/Telemetry/HealthMonitoringAgent.cs` — 전용 스레드(기본 50ms · 스레드풀 고갈과 무관하게 스캔) · `Environment.WorkingSet` > `Health.MaxWorkingSetMB`(1024) · 재사용 `IThreadPoolWorkItem` 탐침 큐 지연(미실행 대기 포함) > `Health.MaxStallMs`(100) · `GC.GetTotalPauseDuration` 증가분 > `Health.MaxGcPausePerScanMs`(200) · CPU‰(GetProcessTimes · 관측) → `HealthAgent`(21) · 기동 유예 `Health.WarmupSeconds`(30). δ=0.
- `src/02_Infrastructure/Engine/HostHealthHeartbeat.cs`(프록시에 소스 링크) — 방향 A: 호스트가 스캔마다 64B 고정 하트비트(QPC·WS·CPU·트립 플래그 · seqlock) 사상. 방향 B: 프록시 `HostHeartbeatEgressGate` — 매수/매도/정정 송신 직전 맵 없음·찢김·200ms 초과·트립이면 키움 송신 0 + 거부 프레임(호스트 슬롯 즉시 반납). 취소는 통과. 프록시 `Program.cs` 기본 활성.
  - 지시서의 '외부 워치독이 발주 소켓 물리 단절'은 관리자 권한(방화벽/TCP 테이블 조작)이 필요하고 시세 연결까지 끊긴다 → 주문이 실제로 나가는 프록시 송신 지점에서 차단.

### 22.3 검증 · 한계
- `ZeroDowntimeRolloverTests.cs` 12건(방어 끔 Red 9건) · 발행→승계→Ack 왕복·스캔·하트비트·게이트 δ=0 · 전체 3,407 Pass ×2 · 오중 빌드 -warnaserror 0W/0E.
- 한계: 승계는 '가시성' 승계다 — 브로커 미체결 조회(OPT10075 등) 대사기 미구현이라 승계 후 신규 발주는 계속 차단. 프록시 게이트는 프록시 측 단위 테스트가 없다(테스트 프로젝트가 프록시를 참조하지 않음 — 게이트 클래스는 공유 소스라 호스트 측에서 검증 · 프록시 결선은 소스 회귀).

## 23. 차기: 브로커 미체결 원장 대사 · 롤오버 주문 확정 (2026-10-06 · 미커밋)

### 23.1 전제 검증
- 지시서의 `BrokerOpenOrderReconciler.cs` · `OpenOrderReconciliationTests.cs` 는 부재(신규 작성). 승계 슬롯(HandoffSlot)에는 키움 주문번호가 없었다 — 번호는 프록시 사상표에만 있고 프록시와 함께 소멸 → OPT10075 행과 1:1 대사 불가.
- v12.15.0 Rollover 비트(20)는 '대사 전 차단'만 했고 `MarkReconciled()` 를 부르는 주체가 없었다(운용자 수동). 승계 없이 크래시한 선행 프로세스의 브로커 미체결은 새 프로세스가 전혀 몰랐다(유령 노출).
- v12.15.0 커밋(69026eb)에 프록시 송신 게이트 3파일(KiwoomProxyHost.cs · Program.cs · csproj)이 누락돼 미커밋 상태로 남아 있다 — HEAD 의 결선 테스트는 디스크 파일을 읽어 통과.

### 23.2 구현
- 프로토콜: `BridgeMessageType.OpenOrder(13)`(80B 행 · 배치/행 번호) · `OrderMapped(14)`(클라이언트 ID↔키움 번호) · `OrderRequestType.QueryOpenOrders(8)/AdoptMapping(9)/CancelByVenueNo(10)`(필드 재사용 · 레이아웃 불변).
- 프록시 코어(`KiwoomComEventCore`): RQ/RM 사상 시 OrderMapped 통지 · `QueryOpenOrders`(OPT10075 · 체결구분 1) · `OnOpenOrdersTr`(빈 결과도 RowCount 0 통지) · `AdoptMapping`(새 프록시 사상표 재등록) · `CancelByVenueNo`(키움 번호 직접 3/4 취소 · rqName 'RX'). IKiwoomApi/Null/Production/ProxyHost 배선.
- 원장: `HandoffSlot` v2 96B(원장 번호 · 종목 · 지정가 · 롤오버 MMF Version 2 — v1 맵 거부). 실행 피드에 종목/지정가/원장 번호/정정가 · `LedgerSequence`+슬롯별 변경 시퀀스 · 최근 해제 링(256) · 결속 전 사상 보류표(64) · `CopyOpenOrderLedger`/`WasFreedSince`/`OnOrderMapped`. RouterFeedGate 가 종목·지정가(원) 기록.
- `StockManager/Adapters/BrokerOpenOrderReconciler.cs`: 조회 송신 시점 시퀀스 스냅샷 → 키움 배치(행 번호 무결성 · 요청 없는 응답 폐기 · 배치 교체 · 10초 시한 · 페이지 상한 100 보류) / KIS 페이지(`KisProtocol.ParseOpenOrders` TTTC8001R) → 1:1 대사(번호 · 방향 · 종목 · 잔량 · 지정가 ±1원). 스냅샷 이후 변경·정정 진행·최근 종결·번호 미상 후보는 보류(오판 0). 판정 Ghost > Divergence > Inconclusive > Confirmed.
  - Confirmed(최초) = 승계 슬롯 전량 확정 → AdoptMapping → `rolloverGuard.MarkReconciled`(Bit 20 해제). Divergence = sticky. Ghost = 비트 22 Raise 후 브로커 미체결 전 행 비상 취소(패스당 ≤4 · 5초 중복 억제 · 대기열 없이 다음 조회 상태로 재판정).
  - 방향 A: 패스마다 `ZeroAllocForensicAuditStream`(Reconciliation · 판정/브로커/유령/불일치/행 수 태그) + MMF 블랙박스 73 OpenOrderReconciled · 74 OpenOrderDivergence · 75 GhostOrderCancel.
- 인터락 `OpenOrderDivergence = 22`(필수) — 유령 ∨ 불일치 ∨ 최초 대사 미확정. 재무장 = 물리 이중키 + 새 대사 확정. 호스트: 1초 펌프 · 조회 주기 미확정/유령 3초 · 평시 `OpenOrder.IntervalSeconds`(30) · `OpenOrder.MaxCancelsPerPass`(4).

### 23.3 검증 · 한계
- `OpenOrderReconciliationTests.cs` 17건(방어 15종 끔 Red 12건) · 정상 패스(조회+8행+대사+감사) δ=0 · KIS 파서 δ=0 · 전체 3,424 Pass ×2 · 오중 빌드 -warnaserror 0W/0E · infra-scan 0 · Phase114 통과.
- 한계: 프로덕션 호스트 브리지 미연결 · 프록시 진입점 NullKiwoomApi → 실 경로 비활성(현재 최초 대사 불가 = 비트 22 상시 차단 · liveArmed:false 와 정합). OPT10075 연속 조회(prevNext=2) 미지원 → 100행 도달 시 보류. KIS 는 파서/대사만 — REST 조회·취소 송신 미배선(유령은 차단만). HTS 수동 주문도 유령으로 취소된다(운용 공지 필요). 필드명(OPT10075 · TTTC8001R)은 공개 문서 기준 · 실계좌 미검증.

## 24. 차기: 다중 저장소 봉인 감사 · 인터락 전수 명세 (2026-10-06 · 미커밋)

### 24.1 전제 검증
- 지시서 경로 이탈: `StockManager/Services/ReleasePackager.cs` 부재(실재 = `src/02_Infrastructure/Engine/ReleasePackager.cs` · v6.2.0 masterSeal 산출기) · `QuantumVault/RELEASE_MANIFEST.json` 부재 — StockManager 다중 저장소 매니페스트는 `release/RELEASE_MANIFEST.json`(git 무시 폴더)이고 QuantumVault 쪽은 자체 `release/offline-bundle/RELEASE_MANIFEST.json`(ML-DSA-87 서명 · v2.8.0)이다.
- ★QuantumVault 쓰기 거부: 피어는 읽기 전용(2026-10-06 사용자 결정 · ReleaseHandshake 헤더). 피어 매니페스트에 StockManager 필드를 넣으면 피어 자체 서명이 깨지고, 피어 작업 트리에는 타 세션 미커밋 코드(Tests/PqcSignedManifestTests.cs 등)가 있다.
- ★v12.14.1-GM 출하 기록 불변: 기록 인터락은 17종(태그 소스 기준) — 현 HEAD 24종(비트 0~22 + StartupSanity)을 출하 매니페스트에 넣는 것은 출하 바이너리와 다른 사실의 위조라 하지 않았다. 지시서의 '22개'는 실측 23비트(0~22) + 63.
- 현 상태 실측: `--release-handshake` = HeadDrift(태그↔HEAD 코드 이탈 26건) 거부 · 피어 QuantumVault HEAD 898052a ≠ 기록 57b1822(피어 매니페스트 파일 해시는 불변 6AB1F70C) · 피어 미커밋 코드 존재 → 새 출하는 새 태그 + 피어 정리 후에만 가능.

### 24.2 실결함 · 구현
- [실결함 1] `MultiRepoReleaseManifest.Verify` 가 genesisHeadHash · interlocks · peer.manifestPath/name 을 어떤 실측과도 대조하지 않았다 — manifestHash 는 무키 SHA-256 이라 본문 위조 후 재계산하면 통과. → manifest.sig 제네시스 · 태그 소스 `ArmingInterlockSource` 열거(git show <태그>) · 관측 피어 경로/이름과 대조(GenesisMismatch 17 · InterlockSetMismatch 18 · PeerIdentityMismatch 19 · 관측 부재 = 거부). sync 는 인터락을 실행 바이너리 Enum.GetNames 대신 태그 소스 열거로 기록.
- [실결함 2] `GitRepoProbe.Git` 출력 인코딩 미지정 → CP949 디코딩이 UTF-8 한글 끝 바이트로 줄바꿈을 삼켜 줄 병합(태그 소스 판독 시 RouterFeed 한글 주석 다음 OrderSlotAge 소실 — 실 v12.14.1-GM 태그에서 재현 · porcelain 한글 경로 손상) → UTF-8 고정.
- [방향 A] `StockManager/Tools/ManifestSealStreamer.cs` — POH pinned 입력/정규화 버퍼 · Utf8JsonReader 타입드 판독 · 정규화 문자열 직접 조립 · SHA256.HashData(정적) · FixedTimeEquals 앵커(masterSeal/zip/genesis) 대조. δ=0 · 1ms 미만 실측. 핸드셰이크에 '두 번째 독립 판독기'로 결선 — 중복 키·이스케이프 등 판독기 차이 = ParserDifferential(20).
- release-package.ps1 [0/10] 피어 사전 점검(저장소 · 봉인 매니페스트 · 미커밋 코드 — Test-CodePath 동일 규칙 · 읽기 전용): 기존엔 수십 분 빌드 후 [10/10] 에서야 거부.
- 방향 B(매니페스트 변조 → 런타임 Disarm·키 소거)는 미구현: RELEASE_MANIFEST.json 은 zip 에 포함되지 않는 출하 후 기록이라 실행 중 앱이 판독할 대상이 없다(가짜 결선 금지). 실행 바이너리 변조는 기존 BootIntegrity(비트 0 · manifest.sig/master-seal 대조)가 담당.

### 24.3 인터락 전수 명세(현 HEAD · ArmingInterlockSource)
| 비트 | 이름 | 차단 조건 |
|---|---|---|
| 0~5 | BootIntegrity · CredentialLeak · ForensicChain · MarketSpike · ChaosProgress · ExecutionImpact | 봉인 검증 · 자격증명 유출 · 포렌식 체인 · 시세 급변 · 카오스 진행 · 체결 충격 |
| 6 | RouterFeed | 시세/체결 피드 단절 |
| 7 | OrderSlotAge | 응답 타임아웃 슬롯 회수(미확인 노출) |
| 8 | ExtremeSlippage | 체결가 극한 괴리 |
| 9 | MmfFabric | MMF IPC 시퀀스 역전/찢김 |
| 10 | BrokerStateMismatch | 추적 잔량 ≠ 브로커 미체결 잔량 |
| 11 | OrderbookIlliquidity | 호가 교차/공백/극한 스프레드 |
| 12 | LatencyStall | Tick-to-Trade 구간 정지 |
| 13 | KiwoomServerError | 키움 중대 서버 오류 · STA 펌프 동결 |
| 14 | AccountDivergence | OPW00018 보유 ≠ 추정 보유 |
| 15 | EgressBypass | liveArmed:false 인데 실 브로커 도달 |
| 16 | PreFlightBoot | 시계 역행 · 설정 변조 · 파이프 신원 불일치(+ 키 소거) |
| 17 | CorrelationCrash | 보유 종목 동시 하락(+ 미체결 일괄 취소) |
| 18 | KisAuth | KIS 토큰 갱신 실패/만료 |
| 19 | KisSession | KIS WebSocket 침묵 |
| 20 | Rollover | 승계 불일치 · 승계 후 브로커 대사 미완료(대사기가 확정 시 해제) |
| 21 | HealthAgent | Working Set · 스레드풀 정체 · GC 정지 폭주 |
| 22 | OpenOrderDivergence | 최초 미체결 대사 미확정 · 원장 불일치 · 유령 주문(+ 비상 취소 · HandoffSlot v2 96B) |
| 63 | StartupSanity | 봉인 전 기동 위생 |

### 24.4 검증 · 한계
- `MultiRepoManifestAuditTests.cs` 9건 + `QuantumVaultPipelineIntegrationTests.cs` 헬퍼 갱신(방어 10종 끔 Red 7건) · 실 출하 기록(v12.14.1-GM) 매니페스트↔master-seal↔zip↔manifest.sig 제네시스↔태그 소스 인터락 17종 일치 · 오중 빌드 -warnaserror 0W/0E · 전체 3,433 Pass.
- 한계: manifestHash · masterSeal 은 무키 해시 — 쓰기 권한 공격자는 모든 파일을 일관되게 재생성할 수 있다(무결성 ≠ 진본성). 진본성은 서명 키(예: 피어의 ML-DSA-87)가 필요 — 미구현. 실 출하 기록 테스트는 release/(git 무시)가 있는 작업 트리에서만 수검. 전체 스위트에 기존 간헐 테스트 호스트 크래시(무관 모듈 실행 중 중단)가 남아 있다.

## 25. 차기: 간헐 테스트 호스트 크래시 트리아지 (2026-10-07 · 미커밋)

### 25.1 전제 검증 · 진단 경로
- 지시서의 UnmanagedOrderBookBuffer 는 SafeHandle(DangerousAddRef) 보호로 안전 — 원인 아님. dotnet-dump/procdump 미설치 · 런타임 미니덤프 env 는 Windows 에서 미생성(실측).
- ★VSTest 콘솔엔 스택이 없어도 Windows 이벤트 '.NET Runtime 1026' 에 관리 스택이 남는다 — 2026-10-06 크래시 다수 = 백그라운드 스레드가 처분된 ManualResetEventSlim 을 Wait/Reset 하는 ObjectDisposedException(일부는 이전 마일스톤에서 교정됨).
- 정적 전수 조사(서브에이전트 · 코드 확인): 네이티브 메모리 직접 소유 엔진의 Dispose 즉시 해제 vs 진행 중 연산 경합(use-after-free) · 예외 미포착 전용 스레드 다수.

### 25.2 실결함 · 구현(Red→Green)
- [실결함 1] ZeroAllocAsyncCompressedLogger — Dispose 의 Join 타임아웃(IO 스레드 기아) 후에도 _wake 처분 → IoLoop 의 try 밖 _wake.Reset() 이 ODE → 프로세스 종료(Red: 테스트 호스트 실제 종료 · 이벤트 1026 + 기록기 로그 'Ms2Logger' 스택). Log 는 해제 후 버퍼 기록 가능. → 미종료면 처분·해제 보류(누수) · Reset/Wait 예외 흡수 · Log 수명 게이트.
- [실결함 2] HardwareInterruptSpinLock — _onFire 콜백 예외가 최고 우선순위 스핀 스레드에서 미처리 → 프로세스 종료(Red: 'HwKillSwitchSpin' 1026) → 계수(CallbackFaults) 후 감시 지속.
- [실결함 3] ClaudeBlackSwanEngine · CacheLineCoherenceSentinel · SimdAlignedRingVectorizer · 로거 — null 확인 후 필드 재판독/즉시 해제 경합(use-after-free) · 운영 앱 종료 시 링 샘플 타이머가 해제 후 판독. → `src/02_Infrastructure/Engine/NativeMemoryGuards.cs` NativeLifetimeGate(int 1개 · 진행 중 연산 배수 후에만 해제 · 시한 2초 초과 = 누수 · 이중 해제 차단 · 연산당 Interlocked 2회 δ=0) 적용(Red: 진행 중 연산 보유 시 즉시 해제 3건 실패).
- [방향 A] NativeCanaryBlock — 64B 가드 전/후방 16B 패턴 · Verify O(1) δ=0 · 파열 블록은 해제 보류(손상 증폭 차단) · BlackSwan/Coherence 아레나 적용(Coherence 는 AttestArena 에서 퓨즈 래치). '0.1ns 중단'은 측정 불가 — 검증 시점 탐지(정직).
- [방향 B] TestHostExitSentinel(테스트 어셈블리 모듈 초기화) — %TEMP%\ms2e-testhost\<pid>.log 에 UnhandledException·ProcessExit·UnobservedTask 기록 → 종료가 내부(예외)/정상 종료/외부 강제 종료인지 판별. tools/crash-triage-loop.ps1 — N회 연속 + Internal/LowMemory/Silent 분류(PS 5.1 *> 인코딩 손상 회피: cmd 원시 리다이렉션 · 영어 CLI).
- [하네스 결함] HotPathDuringUnload_NoTornReference_NoCrash — 리더 시작 배리어 부재로 CPU 경합 시 observed=0(50회 중 1회) → SpinUntil 시작 배리어.

### 25.3 검증 · 결과(정직)
- CrashTriageStressHarness.cs 10건(게이트·카나리·소유자 배수·누수·퓨즈·로거·킬스위치·50라운드 경합·δ=0·기록기) · 전체 3,443 Pass · 오중 빌드 -warnaserror 0W/0E · infra-scan 0 · Phase114 통과.
- ★50회 연속(수정 후 · 하네스 수정 전 바이너리): 정상 46 · 실패 1(하네스 경합 — 교정) · 중단 3. 중단 3건 모두 프로세스 내부 증거 0(이벤트 1000/1026 무 · 기록기 UnhandledException 무): #18 = 스위트 끝까지 실행 후 ProcessExit(0) 정상 종료인데 VSTest 가 'crashed' 판정(통신 손실) · #32/#39 = 기록 전무 즉사(외부 TerminateProcess 서명). 동시각 다른 세션(MS2-E · AutoCAD_BOM2) 테스트 스위트 실행 관측 · 10:26 중단은 dwm.exe 0xC00001AD 종료·시스템 커밋 고갈(이벤트 2004 · python.exe 다수 ~100GB)과 동일 순간. 외부 종료 주체는 미특정.
- 지시서 'Zero Crash(50회 0건)'는 공유 호스트 환경에서 미달 — 프로세스 내부 원인은 제거했으나 외부 종료는 이 저장소 코드로 막을 수 없다. 전용(격리) 머신/CI 에서 재측정 필요.

## 26. 차기: 다중 브로커 자식 슬롯 분할 라우팅 (2026-10-07 · 미커밋)

### 26.1 전제 검증
- `ChildSlotOrderSplitter.cs`·`ChildSlotSplitRoutingTests.cs` 부재(신규). 기존 MicroLiquiditySlicer 는 단일 링 대상 시간 분할기(브로커 분할 아님).
- ★키움·KIS 는 같은 KRX 호가창 — '브로커별 호가 잔량'은 없다. 분할 축 = 시장 깊이(1~3호가 즉시 체결 잔량) × 브로커별 한도.
- 드레인 경로는 제안 1건 = 라우터 키 1 = 추적 슬롯 1 = 브로커 주문 1건 · 라우터는 항상 활성 브로커만 선택 · Place 거부 = throw(동결). 호스트는 브로커 맵을 결선하지 않음(단일 키움 브리지) · KIS 어댑터 미결선.

### 26.2 구현
- `ResilientOrderRouter.RouteTo`(지정 브로커 · 범위 밖/장애/블랙리스트 = BrokerBlacklisted) — Route 와 판정·부작용 공용(Commit).
- 공용 계약(`IOrderRouteGate.cs`): BrokerSplitCap · ChildOrderPlan(128비트 TrackingId = 부모|자식) · ChildIdSpace(비트62 표지|부모56|순번6) · IChildSplitRouteGate · IChildSlotLedger. 인터락 `ChildSlotRejection = 23`.
- `StockManager/Engines/ChildSlotOrderSplitter.cs` — 1~3호가 즉시 체결 단계 × 한도 비례 최대잉여법 · 자식 지정가 = 단계 호가(부모보다 불리 0) · 자식 합 ≤ min(부모, 깊이, 한도 합) · δ=0.
- `RouterFeedGate.TryAdmitSplit` — 부모 전방 판정 공용화(TryAdmitPrefix: 단일/분할 판정 동일성) · 자식마다 RouteTo + 추적 슬롯(ChildId · 자식 지정가) · 실패 = 전 자식 원자 롤백 + 예약 반환 · 깊이/한도로 적게 나뉘면 초과 예약 즉시 반환. `EnableSplit(caps)`.
- `OrderDrainWorker.DispatchSplit` — 분할 원장+분할 게이트+브로커 맵 결선 시에만(게이트웨이 봉인 경로 제외) · ★순차 송출(동시 송출이면 거부 시점에 형제가 이미 브로커에 있어 회수 불가) · 거부 = 자식 롤백 + 미송출 형제 전부 OnNotSent + 원장 회수·Disarm → throw(기존 동결 벽) · Place 예외 = 도달 모호 자식 추적 유지 · 이후 형제만 회수.
- [방향 A] `ChildSlotTrackingMatrix` — POH 고정 ParentGroup[] × InlineArray(64) 자식 슬롯 · 그룹별 seqlock(작성 CAS 배타 · 판독 무락 재시도) · 수렴(AllFilled/PartialCancelled/ChildRejected/Open) · 송출 실패/거부 통보 = 미송출 형제 회수 + 비트 23 sticky · 과체결 차단 · Open 시 합 ≤ 부모 검증.
- [방향 B] 같은 부모 최초 체결 후 500ms 넘게 체결 0 인 접수 형제 = 잔여 자식 일괄 취소(1회 · 콜백은 seqlock 밖) + 비트 23.

### 26.3 검증 · 한계
- `ChildSlotSplitRoutingTests.cs` 14건(방어 9종 끔 Red 7건 · 깊이 상한은 단계 상한이 이중 방어) · 퍼징 20,000건 불변식 · δ=0(분할 · 매트릭스 체결/판독/평가) · 전체 3,457 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 통과.
- 한계: 호스트 미결선(브로커 맵 · KIS 어댑터 · 매트릭스 체결 피드 · 평가 타이머) → 프로덕션 분할 경로 비활성(liveArmed:false 정합). 브로커 한도 공급자(caps)는 호출측 구현 몫. 방향 B 취소 콜백 → 실 브로커 취소 결선 없음. 계좌 예약 초과 반환 경로는 단위 테스트 미포함.

## 27. 차기: 실전 조합 뿌리 다중 브로커 결선 · 실시간 한도 피드 (2026-10-07 · 미커밋)

### 27.1 전제 검증
- 호스트: 스위치보드 브로커 1 · 드레인 단일 브로커(브로커 맵 없음) · ★게이트웨이(봉인 사출) 결선 상태라 §26 분할 경로(게이트웨이 시 제외)는 운영에서 도달 불가였다.
- ★봉인 래치는 '슬롯별 단조 orderId'(slot = id & mask) — 자식 ID(비트 62)를 같은 래치에 넣으면 같은 슬롯 일반 주문이 영구 차단 → 자식 전용 래치 필요.
- 지시서의 'KisOpenApiAdapter 를 브로커 맵에' = 불가: 그 클래스는 WebSocket 프레임 판독기(IBroker 아님). 송출 가능한 KIS 구현 = KisLiveBroker(OAuth2 토큰 자체 수명주기 · 5분 선행 갱신). PipeServerIdentity 는 §20 에서 이미 결선(bridge.ServerIdentityVerifier).
- ★게이트는 분할 '전'에 부모 매수 명목을 계좌에 예약 — 같은 계좌 여유를 그대로 한도로 쓰면 이중 차감(부모 명목 = 주문가능금액이면 분할 불가).

### 27.2 구현
- 호스트(BlazorHomeForm): 스위치보드 2(0 = 키움 · 1 = KIS) · KIS 자격증명(ApiKeyVault) 없으면 인덱스 1 부팅 블랙리스트 · KisLiveBroker 를 키움과 같은 백업 인터락(LiveArmedEgressSentinel)으로 감싸 브로커 맵 결선 · ChildSlotTrackingMatrix(비트 23 필수) + 키움 체결 통보 + 50ms 평가 타이머 + 불균형 취소 콜백(브로커 맵 Cancel) · BrokerLimitFeedProvider · ★분할은 `Routing.SplitEnabled`(기본 false)로만.
- 계약: IBrokerLimitFeed.Fill(dst, 종목, 방향, 지정가, parentReservedWon) · IChildSlotLedger.Abandon. AccountStateSyncAdapter.TryGetBuyHeadroom/TryGetSellable(만료 = false · 계수 무변경).
- `StockManager/Adapters/BrokerLimitFeedProvider.cs` — 매수 = (여유 + 같은 계좌면 부모 예약 되돌림) / 지정가 · 매도 = 보유 · 주문당 상한 · 미결속/만료 = 자식 0 · [방향 B] 소진 브로커 격리(전이 1회)·회복 해제 → 잔여 한도 브로커로 우회.
- RouterFeedGate.EnableSplit(feed, onUnavailable) — 분할 가능 브로커 0 = 전방 거부 + 콜백(매트릭스 TripExternal → 비트 23) + 예약 반환.
- GenesisLiveIgnitionGateway.EmitChild — 같은 방어선 재검 + 자식 전용 exactly-once 래치(일반 래치 비오염) · 자식 ID 공간 밖 거부. OrderDrainWorker 분할 경로가 게이트웨이 경유로 동작 — 첫 자식 veto = Abandon(송출 0 · Disarm 없음) · 일부 송출 후 veto = 불완전 분할 Disarm.
- 방향 A(비관리 타임스탬프 펌프 · '0.1ns') 미채택: 측정 불가 단위 · Timer 는 생성 시 1회 할당뿐(틱당 0) — 50ms Timer + δ=0 Evaluate.

### 27.3 검증 · 한계
- ProductionWiringMultiBrokerTests.cs 8건(방어 6종 끔 Red 7건) + 분할 테스트 갱신 · 전체 3,465 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 통과.
- 한계: 키움 브리지 ConnectAsync 호출부 여전히 없음 + 프록시 NullKiwoomApi → 키움 경로 휴면 · KIS 계좌 조회(잔고 REST) 미결선 → KIS 한도 뷰 null(미결속) · KIS 체결통보 WebSocket(KisOpenApiAdapter) 미결선 → KIS 자식 체결은 매트릭스에 도달 안 함 · KisTokenLifecycle(별도 관리자)은 KisLiveBroker 내부 토큰과 중복이라 미결선. liveArmed:false · SplitEnabled=false 기본으로 운영 동작 불변.

## 28. 차기: KIS 실배선 — REST 잔고/주문가능 · WebSocket 체결통보 파이프라인 (2026-10-07 · 미커밋)

### 28.1 전제 검증
- KisOpenApiAdapter 는 체결을 OrderId = KisOrderIdSpace.Encode(odno) 로 방출(KisLiveBroker.Place 반환과 동일 공간) → 실행 피드/자식 매트릭스에 그대로 사상 가능.
- ★[실결함] 어댑터 제어 프레임 분기가 PINGPONG 만 처리 — 구독 응답의 체결통보 복호 key/iv 를 버렸고 운영에 SetNoticeKeys 호출부 없음 → 실연결 시 암호 체결통보 전량 '키 없음' 폐기.
- 부재: KIS WebSocket 접속 클라이언트 · 접속키(/oauth2/Approval) · HTS ID(체결통보 구독 키) 보관 · REST 잔고 폴링. 지시서 '미체결 조회'는 §23 TTTC8001R 판독기로 기존재(대사기 KIS 경로).
- ★체결통보만 구독하면 장중에도 대부분 침묵 — 방향 B(3초 침묵 차단)는 심박용 체결가(H0STCNT0) 구독이 전제.

### 28.2 구현
- KisProtocol: TryParseBuyable(TTTC8908R ord_psbl_cash) · ParseHoldings(TTTC8434R output1 · 보유 0 제외 · 매도가능 > 보유 = 형식 이탈) · TryParseSubscribeAck(header.tr_id · body.rt_cd · output.key/iv) — Utf8JsonReader δ=0.
- KisLiveBroker: QueryBuyableJson/QueryBalanceJson(콜드 HTTP 원문) · RequestApprovalKey(접속키 · 마스커 등록) · Configured/IsRealDomain.
- `StockManager/Adapters/KisBalanceQueryAdapter.cs` — IBrokerAccountView + 비트 24(KisLimitFeed): 실패 즉시 한도 0 · 연속 3회 sticky Disarm · 30초 만료 · 미설정 = 미결속·비차단 · 부모 예약 되돌림 비대상.
- KisOpenApiAdapter: 구독 응답 → 체결통보 키 자동 장착 · 구독 거부 계수. KisSessionInterlock: [방향 B] 장중(KST 09:00~15:20 평일) 3초 침묵 임계 · 장외 60초 · 판정 예외 = 장중 취급.
- `StockManager/Adapters/KisRealtimeSession.cs` — 접속키 → 접속 → 구독(체결통보 HTS ID + 심박 체결가) → POH 버퍼 조각 조립 → 어댑터 · PINGPONG 반송 복사·송신 직렬화 · 초과 메시지 폐기 · 단절 = MarkDisconnected(비트 19 · 라우터 KIS 장애) · 재접속(체결 대사 지속). IKisWsTransport + ClientWebSocketTransport.
- ChildSlotTrackingMatrix: 체결 명목 누적 → 부모 가중평균 체결가(AvgFillPriceWon).
- 호스트: KisBalanceQueryAdapter(인덱스 1 한도 뷰 · 10초 폴링 · 중첩 방지 · 비트 24 필수) · `Kis.RealtimeEnabled`(기본 false) ∧ `KIS.HtsId` 있을 때만 세션 + 비트 19 결속 · 체결 = 실행 피드 + 매트릭스 팬아웃 · 50ms 타이머가 세션 침묵 판정.
- 방향 A(비관리 체결 링버퍼) 미채택: 매트릭스 OnFill 이 이미 무락 판독/CAS 작성 · 수신 스레드 1개라 경합 원천 없음.

### 28.3 검증 · 한계
- KisLivePipelineWiringTests.cs 9건(방어 7종 끔 Red 4건 — 다중 방어 테스트) · 전체 3,474 Pass(3회 중 1회 기존 IPC 타이밍 테스트 IpcChaosVerification 간헐 실패 · 단독 5/5 통과 · 본 변경 무관) · 오중 빌드 0W/0E · infra 0 · Phase114 통과.
- 한계: TR ID·필드명·WS URL 은 공개 문서 기준 · 실계좌/실서버 미검증(모의 도메인에서 프레임 대조 필수). KIS 잔고 1페이지만(연속 조회 미지원). HTS ID 는 vault 'KIS.HtsId' 수동 등록 필요(설정 UI 미반영). 키움 브리지 ConnectAsync 미호출 유지.

## 29. 차기: 키움 브리지 실연결 결선 · COM 프록시 신원 선언 (2026-10-07 · 미커밋)

### 29.1 전제 검증
- 이 호스트엔 Kiwoom OpenAPI+ 미설치 → 실 COM 활성화·로그인은 여기서 실측 불가. ProductionKiwoomApi 는 `#if KIWOOM_COM` 뒤(KiwoomComShim 이 형상 쉼으로 컴파일 수검).
- ★[실결함] 프록시 진입점이 무조건 NullKiwoomApi — Null API 는 주문을 조용히 삼키는데 호스트는 이를 구분 불가(실 송출로 오인).
- ★[실결함] OnEventConnect 로그인 실패 코드가 호스트에 통지되지 않음 · AxKHOpenAPI 생성 예외 시 STA 스레드 붕괴(시작 신호 영구 대기).
- 부재: 호스트의 ConnectAsync 호출부 · 프록시 기동/이중 실행 방지. 체잔/호가 파싱은 KiwoomComEventCore 에 기존재(재사용 버퍼 직렬화).

### 29.2 구현
- BridgeProtocol: ProxyHello(타입 15 · 24B · ApiKind/ProtocolVersion/PID/시각) — 프록시가 파이프 연결 직후 API 종류 선언.
- KiwoomBridgeClient: Hello 형식 검증(버전 1 · 알려진 종류만) · IsProductionProxy = 생존 ∧ ProductionCom. ExecutionBridgeBroker.RequireProductionProxy(호스트 true) → 미연결/미선언/시뮬레이션 프록시면 송출 0.
- [방향 B] KiwoomServerErrorCodes 7110~7113(로그인/서버/버전/통신 단절) · 7120(COM 생성 실패) 중대 분류 → 기존 비트 13 Disarm. 미분류 연결 오류 = 로그인 실패(fail-closed).
- 프록시: KiwoomApiFactory(KIWOOM_COM = 실 API · 그 외 Null · --require-production 이면 Null 대체 거부 exit 2) · 세션 뮤텍스 이중 실행 거부(exit 3) · COM 생성 보호.
- `StockManager/Adapters/KiwoomBridgeLink.cs` — 비트 25(KiwoomBridgeLink) 인터락 + KiwoomProxySupervisor(선택 기동 → 시도당 시한 재시도 → 수신 시작 · 신원 거부 즉시 중단 · 처분 시 자기 기동 프록시만 종료).
- 호스트: `Kiwoom.ConnectEnabled`(기본 false) · `Kiwoom.AutoStartProxy`(기본 false) · 계좌 뷰 판정 = IsProductionProxy.
- 방향 A(COM 이벤트 링) 미채택: 프록시가 이미 재사용 버퍼로 직렬화. 파이프 쓰기 STA 블로킹은 잔여 위험으로 기록.

### 29.3 검증 · 한계
- KiwoomRealPipelineWiringTests.cs 8건(실 파이프 왕복 Hello · 단절 즉시 불인정 · 연결 오류 Disarm · 링크 인터락 · 감독자 6상태 · 진입점/결선) · 방어 7종 끔 7/7 Red · 전체 3,482 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 통과.
- 팩토리는 COM 블록 뒤에 배치(쉼 형상 수검 테스트가 첫 `#if KIWOOM_COM` 블록을 판독).
- 한계: 실 COM 로그인·체결 미검증(OpenAPI+ 설치 PC 필요) · 단절 후 자동 재기동 없음(브리지 1회용 · ForceFreeze 설계 계승) · 연결/자동기동 기본 off.

## 30. 차기: KIS 모의투자(VTS) 프로토콜 검증 · 키움-KIS E2E 모의 하네스 (2026-10-07 · 미커밋)

### 30.1 전제 검증
- 주문/잔고/체결통보 TR 은 이미 도메인별 분기: VTTC0802U/0801U/0803U · VTTC8434R/8908R · H0STCNI9 ↔ TTTC…/H0STCNI0(공개 문서 기준 · 실 VTS 패킷 캡처 없음). KIS 의 2024 신규 주문 TR 체계 공지 대조는 여전히 운용 과제.
- ★[실결함] 인터락 봉인(Seal)이 자식 매트릭스(비트 23) · KIS 한도(비트 24) 결속보다 먼저 실행 → 필수 미결속으로 봉인 실패 → 기동 샌니티 비트 영구 → 운영 무장 자체 불가(§27 도입). liveArmed:false 라 드러나지 않았음.
- ★[실결함] KIS 주문 오류 메시지가 절단 후 마스킹 — 180자 경계에 걸친 등록 비밀값 접두가 유출. 업비트 오류 본문은 마스킹 자체 없음.
- ★[실결함] 체결통보 어댑터가 세션 도메인과 무관하게 H0STCNI0/9 모두 수용 · HTS ID 구독은 그 ID 의 모든 계좌 통보를 보내는데 계좌 대조 없음.
- REST 응답 tr_id 헤더 미검증. KIS 보유와 앱 내 체결 누적의 대사 부재.

### 30.2 구현
- 비트 26(KisProtocolDivergence · 필수). KisProtocol: ClassifyTr(실/모의/중립/미상) · ResponseTrIdConsistent · 통보 계좌 필드 · 구독 응답 TR 도메인.
- `StockManager/Adapters/KisVtsProtocolValidator.cs` — 도메인 핀(실시간 TR · 구독 응답 · REST 요청 TR) · 응답 tr_id 헤더 · 주문 응답 형식 이탈 = sticky Disarm + 최초 1회 격리 + 보안 로그(종류 + 상수 라벨만). 업무 거부(모의 호출 제한 EGW00201 등)는 이탈 아님.
- KisLiveBroker: 응답 tr_id ≠ 요청 = 성공 판정 금지 + ResponseDivergence 통지 · 오류 본문 마스킹 후 절단 · AccountCano. KisOpenApiAdapter: 검증기 + 계좌 핀(타 계좌 통보 폐기 · 비 Disarm).
- ChildSlotTrackingMatrix.IsolateBroker — 그 브로커 미송출 자식 회수 + 송출분 0ms 취소 + Disarm(비트 23).
- [방향 B] KisBalanceQueryAdapter 포지션 원장: 최초 잔고 = 기준선 · 체결통보 순증감 · 매 조회 '기준선 + 순체결 ≡ 보유' · 불일치 연속 2회 = 비트 24 Disarm + 한도 무효 · 조회 중 체결 = 판정 보류 · 원장 초과 = 불일치. 실시간 결선 시에만 활성.
- [방향 A] `StockManager/Simulation/KisVtsMockStreamer.cs` — 공개 문서 형식 VTS 프레임 합성(구독 응답 · 체결가 · AES 체결통보) · POH 고정 버퍼 · 무할당 리플레이 + log2(ns) 지연 히스토그램.
- 호스트: Seal 을 모든 결속 뒤로 이동 · 검증기 결선(격리 = 매트릭스 KIS 격리 + 세션 단절 보고) · 원장 팬아웃.

### 30.3 검증 · 한계
- KisVtsProtocolIntegrationTests.cs 15건(키움 시뮬레이션 프록시 실 파이프 + KIS VTS 스트림 동시 수렴 · 500ms 불균형 · 이탈 0ms 격리 · δ=0 · 리플레이 5만 프레임 할당 0B · 이 호스트 실측 약 72만 fps/p99 ≤4µs) · 방어 9종 끔 9/9 Red · 전체 3,497 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 통과.
- 한계: 프레임은 합성(실 VTS 캡처 대조 미실시) · 통보 계좌 필드 형식(8/10자리) 미검증 — 불일치 시 전 통보 폐기 → 원장 대사가 비트 24 로 드러냄 · 응답 tr_id 헤더 부재는 통과 처리 · 체결이 연속 유입되면 원장 판정이 계속 보류될 수 있음.

## 31. 차기: AI 모의투자 실시간 관제 대시보드 · 텔레메트리 허브 (2026-10-07 · 미커밋)

### 31.1 전제 검증
- 렌더 병합기는 기존재: `MS2E.UI.Portable/Infrastructure/SnapshotRenderGovernor.cs`(선행/후행 에지 · 버전 불변 생략 · 부하/동결 적응) — 중복 신설 대신 재사용(50ms 창). 지시서의 `Blazor/Services/SnapshotRenderGovernor.cs` 는 같은 이름 충돌 방지로 `DashboardSnapshotHub.cs` 로 신설(빠진 '무할당 수집 + 3중 버퍼 스냅샷' 담당).
- 호스트 결합 UI 래칫이 이미 40/40 — 지시서 경로의 [Inject] 페이지는 래칫 위반. 관례대로 호스트 shim(`@page "/aidash"` · Inject 0) + 순수 RCL 뷰 + 포트.
- 집계어 전이 관측자는 1개 봉인(포렌식 기록기 점유) → 기록기에 2차 체인(기록 먼저 · 체인 예외 격리) 추가.
- 인터락 비트는 지시서 '26대(0~25)'가 아니라 현재 0~26 + 63(28개) — 실제 enum 기준 표시.

### 31.2 구현
- `MS2E.UI.Portable/Ports/Trading/PaperDashboardPort.cs` — 읽기 전용 포트(Acquire/Release/Version/Running/FrameReady · 명령 멤버 0) + 원시 구조체 프레임 DTO.
- `StockManager/Blazor/Services/DashboardSnapshotHub.cs` — TelemetryRing(MPSC · wait-free 게시 · 유실 판정) · 생산자 4종(체결/틱/AI 판단/인터락 전이) · 전용 타이머 50ms 펌프(모의 원장: 가중평단 · 실현/미실현 · 승패 · MDD · 1초 자산곡선) · 자식 슬롯/한도/집계어 판독 · 3중 버퍼 리스 · 고정 배열 구독자 통지(예외 계수).
- [실결함 · 자체] 3버퍼 모두 리스된 회차의 변경(링 소비분)이 다음 프레임에서 소실 → 게시 성공 시에만 서명 확정 + 보류 플래그.
- `MS2E.UI.Portable/Pages/Trading/PaperTradingDashboardView.razor` — KPI · SVG 손익 곡선 · 인터락 LED + Disarm 포렌식 · 체결 피드 · 브로커 한도 · [방향 A] 자식 슬롯 매트릭스 · [방향 B] AI 판단 근거 로그. 상승 적색 · 하강 청색.
- ChildSlotTrackingMatrix.CopyChildren(seqlock 낙관 판독) · InterlockForensicRecorder.Chain · 하네스 fillObserver · 호스트 결선(키움 체결/틱 · KIS 체결 · 포렌식 체인 · DI · `Dashboard.PeriodMs`) · 내비 'AI 모의투자 관제'.

### 31.3 검증 · 한계
- PaperTradingDashboardUiTests.cs 13건(1만 틱 + 500 체결 = 프레임 1 · δ=0 생산자/펌프/리스 · 리스 불변 · UI 예외/동결 중 생산자 10만 건 무대기 · 포트 읽기 전용 · 하네스 E2E 수렴 · 래칫) · 방어 9종 끔 9/9 Red · 전체 3,510 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 통과(coupled 40 = 래칫 40 불변 · shell 41→42).
- 한계: 렌더 화면은 브라우저 실렌더 미검증(WinForms WebView 호스트) · 차트는 JS 캔버스 대신 SVG · 손익은 수수료/세금 미반영 모의 원장 · AI 판단 근거는 PublishDecision API 만 제공(전략 계층 실호출부 없음 — 하네스 테스트가 대리) · 방향 A 의 브로커별 5호가 잔량 오버레이 미구현(기존 IOrderBookPort 화면 몫) · UI 렌더 시 문자열 할당은 δ=0 대상 밖.

## 32. 차기: 전 시스템 카오스 스톰 · 28계층 인터락 스트레스 주입 (2026-10-07 · 미커밋)

### 32.1 전제 검증
- ★[실결함] 최종 송출 초크포인트 TOCTOU — OrderDrainWorker 는 집계어를 카나리 · 라우터 게이트 · 지연 프로브 '앞'에서 1회 읽고, LiveArmedEgressSentinel 은 물리 이중키만 본다 → 그 사이 격발한 비트는 무장 상태에서 Place 까지 도달.
- ★[실결함] 블랙박스 사건 코드 충돌 — InterlockRaised/Cleared/EgressBypassBlocked(70/71/72)가 v5.2.0 LiquiditySlice/OrderThrottled/ToxicFlowHold 와 같은 값 → 덤프에서 인터락 전이와 유동성 사건 구분 불가.
- 기존재: MmfFlightRecorder(다중 라이터 δ=0 순환 블랙박스 · Seal) = 방향 A 의 실체 — 중복 신설 대신 사건 코드 추가 · 처리량 실측. 7계층 주입기(하네스) 확장.
- 재무장: TryRearm 전수 이중키 선검사 · 미체결 대사기는 이중키 후에도 최초 대사 확정 전 차단(대사 미완료 재무장 거부 = 기존 설계 확인).

### 32.2 구현
- LiveArmedEgressSentinel(interlockBlocked) — 무장이어도 집계어 차단 = 전달 0 · 판독 예외 = 차단 · 우회 sticky 와 별도 계수(InterlockVetoes). 호스트 키움 · KIS 센티널 결선.
- FlightEventKind 재배정(93/94/95) + ChaosInjected(96) · StormFreeze(97). InterlockForensicRecorder 2차 관측자 최대 4 슬롯(중복 거부).
- `StockManager/Simulation/ChaosStormInjector.cs` — 9벡터(시세 스파이크 · 프록시 소실 · 수신 침묵 · 시계 역행 · MMF 오염 · 자격증명 유출 · 호가 교차 · 서버 오류 · 라이브락)를 실 Trip 경로로 격발 · 반환 전 비트 재판독(0ms Disarm) · 주입→전이 µs · 블랙박스 ChaosInjected · xorshift 시드 결정론 · δ=0. 하네스 brokerDecorator.
- [방향 B] `StockManager/Telemetry/CascadingStormFreezeGate.cs` — 창(50ms) 안 서로 다른 비트 3개 격발 = StormFreeze 기록 + 블랙박스 봉인(최초 원인 보존) + ForceFreeze 1회 · 봉인 성공 뒤 Arm(기동 각인 무시). OS 페이지 읽기 전용 고정은 거부(AV catch 불가 → 프로세스 즉사).

### 32.3 검증 · 한계
- ChaosStormInterlockStressTests.cs 21건(28계층 단일/500조합/28스레드 동시 · 전이 비트당 1회 · 9벡터 · 실 파이프 프록시 종료 · 부하 중 무작위 5천 주입 차단 진입 전달 0 · 블랙박스 4스레드 약 779만 건/s · δ=0) · 방어 9종 끔 9/9 Red · 전체 3,531 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 통과.
- 한계: 프록시 Kill 은 파이프 파기 대리(실 x86 프로세스 Kill 아님) · 시계 역행은 런타임 관측 경로만(부팅 앵커 경로는 기존 테스트) · 스톰 게이트 기본 활성(3비트/50ms → ForceFreeze) — 네트워크 단절이 3계층을 동시에 올리면 동결된다(의도된 fail-closed · `Chaos.StormThresholdBits`/`Chaos.StormWindowMs` 조정).

## 33. v12.15.0-GM 출하 — QuantumVault 피어 정렬 · 릴리스 패키징 (2026-10-07)

### 33.1 전제 검증
- 지시서의 `StockManager/Services/ReleasePackager.cs` · `scripts/release-package.ps1` 는 실재 경로가 다르다: `src/02_Infrastructure/Engine/ReleasePackager.cs` · 리포 루트 `release-package.ps1`. 방향 A(zip 무추출 봉인 수검 `--verify-zip`)와 방향 B 일부([8]~[10] 실패 시 산출물 폐기)는 기존재.
- ★[실결함] 롤백 가드 공백 — [1]~[6](빌드·테스트·infra·래칫·liveArmed) 실패 시 직전 실행이 남긴 같은 이름 zip/.sha256/master-seal.txt 가 잔존(지금 소스와 다른 봉인이 '통과한 출하물'처럼 보임) → [0/10] 통과 직후 같은 버전 산출물 전량 폐기.
- ★[실결함] 출하 빌드에 `-warnaserror` 부재 — 프로젝트 TreatWarningsAsErrors 는 컴파일러 경고만, MSBuild 경고(MSB/NETSDK)는 게이트(-warnaserror)와 달리 통과 → 같은 판정으로 정렬.
- 호스트 버전이 12.14.1 그대로 → 12.15.0(출하 zip 이름 · 태그 원천).
- 피어 QuantumVault 작업 트리에 타 세션 미커밋 봉인 도구(Tools/QvSeal · ReleasePackager · seal 스크립트 · Services/Security) → 피어 사전 점검이 정당하게 거부. 피어 봉인 매니페스트(release/offline-bundle)는 git 무시 산출물이라 HEAD 클론에도 없다 — 작업 트리 사본을 고정하면 '미커밋 피어 고정 금지' 우회. 따라서 -NoPeer 명시 생략(피어 쓰기 · stash · 커밋 0).
- 지시서의 'QuantumVault/RELEASE_MANIFEST.json 동기화(피어 쓰기)'는 하지 않는다 — StockManager 쪽 release/RELEASE_MANIFEST.json 에 28 인터락 · 38 아티팩트 · 피어 고정값 기록.

### 33.2 출하 범위(v12.14.1-GM 이후)
- KIS 실배선(REST 잔고 · WS 체결통보 · 비트 24) · 키움 실연결 결선(ProxyHello · 비트 25) · KIS VTS 도메인 핀(비트 26) · 포지션 원장 대사 · 인터락 Seal 순서 교정 · AI 모의투자 관제 대시보드(/aidash) · 카오스 스톰(송출 초크포인트 TOCTOU · 블랙박스 코드 충돌 교정 · 연쇄 격발 동결) · 크래시 트리아지 · 분할 라우팅 · 다중 브로커 결선.
- 인터락 28종(비트 0~26 + 63 StartupSanity) · liveArmed 기본 false 불변.

### 33.3 검증
- GoldenMasterReleasePackagingTests.cs 4건(버전 · 롤백 가드 순서 · -warnaserror · 실 출하 체인: 자기 해시 · sig · 앵커 · zip · 무추출 재수검 · 소스 커밋 = 태그 커밋 · 인터락 28 = 태그 소스 · 1비트 변조 거부 · δ=0) · 방어 3종 끔 3/3 Red.
- 출하 실행(태그 기준 형제 worktree · -NoPeer): [0/10]~[10/10] 전원 통과 — WebOnly/sln -warnaserror 0W/0E · 3,535 Pass · infra 0 · Phase114 · liveArmed 이중키 조회 14건 기본 false · IL 가드 · verify-seal · 무추출 수검 · 매니페스트 원자 기록 + 핸드셰이크 Ok.

### 33.4 출하 기록
- 태그 v12.15.0-GM → f5e1bca(코드 bfeb265 + 출하 테스트 피어 선택화) · release-v12.15.0-GM.zip SHA-256 69D0E902B25F0BE9817EDF14011909881C45B3C035B97A15A40C74F6043C5DE5 · 2,991,569B · 아티팩트 38 · masterSeal 4A2EE5AFF0E4788757968DF09A1AE65C8C90EDE9B3CCE4DE3AAF479B23B82055 · 인터락 28 · 피어 기록 없음.
- release/ 로 복사 · 이전 v12.14.1-GM 산출물은 release/superseded-v12.14.1-GM/ 으로 이동(삭제 0) · worktree 제거. 태그/커밋 원격 push 안 함.
- 피어 정렬 미완: QuantumVault 의 봉인 도구 작업이 커밋되고 깨끗한 작업 트리에서 봉인 매니페스트가 재생성되면 `release-package.ps1`(피어 포함)로 재패키징해 피어 고정값을 기록.
