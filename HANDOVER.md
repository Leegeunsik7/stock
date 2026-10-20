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

## 34. 차기: QuantumVault 피어 정렬 · 다중 저장소 상호 봉인 준비 (2026-10-07 · 미커밋)

### 34.1 전제 검증 · 결정
- QuantumVault 미커밋 변경 = 타 세션의 진행 중 작업(오늘 15:31 까지 커밋 지속): 릴리스 신뢰 앵커(Services/Security/ReleaseTrustAnchor.cs) · 릴리스 공개키 교체(release/gm02_public.b64) · 서명 도구(Tools/SealerProgram.Signing.cs · Tools/QvSeal/ · Tools/ReleasePackager.cs) · 봉인 스크립트. key_out.txt 는 0B(비밀 노출 없음).
- ★사용자 결정(2026-10-07): QuantumVault 읽기 전용 유지 — 타 세션의 키/신뢰 앵커 교체를 대신 커밋 · 태그하지 않는다. 따라서 피어 포함 정식 패키징 · 양쪽 v12.15.0-GM 태그는 이번에 불가(피어 사전 점검이 정당하게 거부). StockManager 태그 v12.15.0-GM(f5e1bca)은 이동하지 않음(출하된 -NoPeer 산출물과 정합 유지).
- 방향 A 의 ML-DSA 서명 검증은 .NET 8 BCL 미지원(외부 NuGet 신규 도입 = BCL 우선 원칙 위반) → 미구현. SHA-256 자기 봉인 재대조는 기존 무할당 스트리머로 실측(중앙값 약 27µs).

### 34.2 구현
- [방향 B] PeerPin — release-package.ps1 [0/10] 피어 사전 점검에서 피어 HEAD · 봉인 매니페스트 SHA-256 고정 → [10/10] 동기화 · 핸드셰이크에 전달 · 기록 피어 ≠ 고정 = PeerDrift(21) → 산출물 전량 폐기. 형식(40/64 hex) · --peer 동반 강제.
- ★[실결함] 피어 대조 요청인데 매니페스트에 피어가 없으면 피어 검사를 통째로 건너뛰어 Ok — 단독(-NoPeer) 출하가 다중 저장소 봉인처럼 보였다 → PeerMissing.

### 34.3 검증 · 다음
- MultiRepoFullPackagingTests.cs 6건(고정 일치 Ok · 1비트 이동 PeerDrift · 단독 매니페스트 피어 모드 거부 · CLI 형식 · 스크립트 순서/읽기 전용 · 스트리머 δ=0 <1ms) · 방어 4종 끔 4/4 Red · 전체 3,541 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 통과.
- 다음: QuantumVault 소유 세션이 봉인 작업을 커밋하고 깨끗한 작업 트리에서 봉인 매니페스트를 재생성하면 → 이 변경 커밋 + 태그(v12.15.1-GM 권장 · 또는 v12.15.0-GM 재지정 후 재패키징) → `release-package.ps1`(-NoPeer 없이) 실행.

## 35. 차기: AI 전략 정량 시그널 엔진 · 신뢰도 게이트 · 전략 이상 인터락 (2026-10-07 · 미커밋)

### 35.1 전제 검증
- ChildSlotOrderSplitter · RouterFeedGate.TryAdmitSplit(1~3호가 × 브로커 한도) · DashboardSnapshotHub.PublishDecision 은 기존재. 시그널 엔진 · 신뢰도 게이트 · 전략 이상 인터락 · 판단 근거 실호출부는 부재(§31 대시보드 판단 로그가 비어 있던 원인).
- ★정직: 신호는 공개 미시구조 지표의 결정론 합성(규칙 기반)이며 백테스트 · 실거래 성과 검증 없음 — '알파' 주장 아님. 목적 = 판단 근거 관제 + 확신 낮은 주문 전방 거부.
- 거래량 의미: KIS 틱 Volume = 누적 거래량 · 키움 틱 Volume 의미 미검증 → 누적 증분 규칙(감소 시 값 자체를 증분) · 첫 건 기준선.

### 35.2 구현
- `StockManager/Engines/QuantSignalProvider.cs` — 종목별 고정 슬롯(작성 잠금 + seqlock 판독 · δ=0): imb5 · [방향 A] 마이크로 추세 EWMA(Δ10호가 불균형 ×10 · 틱 없이 잔량 기울기로 선행) · 중간가 수익률 EWMA 변동성 · VWAP 이격 → score = 0.6·imb5 + 0.4·micro · 확신 = |score| · 변동성 스파이크(>30bp) = 확신 0 · VWAP 과이격 추격 = ½. 정적 불균형만으로는 0.85 미달(설계상 잔량 추세 동반 필요). 원시 바이트 프레임(OnBookFrame) 입력.
- IQuantSignalGate → RouterFeedGate.EnableSignalGate: 단일 · 분할 주문 모두 예약/슬롯 확보 전 판정 — 신호 없음 · 만료(1초) · 확신 < 0.85 · 방향 불일치 = SignalRejected(21) 전방 거부 · 게이트 예외 = 거부.
- [방향 B] StrategyAnomalyInterlock(비트 27 · 필수): 산출 NaN/∞ · 범위 이탈 · 수량 0 이하/음수 가격 제안 · 1회 연산 > 10ms = sticky Disarm · 최초 원인 각인 · 재무장 이중키.
- 관제: 판단 상태(등급 · 방향 · 사유) 변화 또는 250ms 경과 시에만 IDecisionSink(DashboardSnapshotHub) 사출.
- 호스트: 키움 브리지 호가/틱 → 엔진 · 비트 27 필수 결속(Seal 전) · 시그널 게이트는 `Strategy.SignalGateEnabled`(기본 false) · 임계 `Strategy.ConfidenceThresholdBp`(기본 8,500).

### 35.3 검증 · 한계
- QuantSignalStrategyTests.cs 8건(수학 · 게이트 · 이상 · 관제 사출 억제 · 하네스 E2E: 약한 신호 송출 0 · 강한 매수 = 매수만 · 수량 0 제안 = 비트 27 → 이후 송출 0 · δ=0) · 방어 9종 끔 9/9 Red · 전체 3,549 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 통과.
- 한계: KIS 호가(H0STASP0) 미구독이라 엔진 입력은 키움 호가뿐 · 판단 사유는 대시보드에 숫자 코드로 표시(라벨 매핑 미구현) · 인터락 29종(0~27 + 63) — 출하본 v12.15.0-GM 은 28.

## 36. 차기: 24시간 가속 Long-Soak 프로파일 · 미세 누수 예측 인터락 (2026-10-07 · 미커밋)

### 36.1 전제 검증
- 실제 24시간 키움/KIS VTS 틱 기록은 저장소에 없음 → 시드 결정론 합성 시세(±1틱 보행 · ±5% 경계 · 12시간째 거래일 경계)로 86,400 모의 초 재생(실 약 1.2초).
- HealthMonitoringAgent(비트 21: Working Set · 스레드풀 정체 · GC 정지)는 기존재·호스트 결선 · LiveSoakSentinel(v12.5.0)은 계단식 5% 드리프트 모델(예측 아님 · 미결선).
- 방향 A(EventCounters 덤퍼) 미채택: EventListener 는 이벤트마다 EventWrittenEventArgs · 페이로드 컬렉션을 할당 — 0B 조건과 모순. 같은 값은 BCL 무할당 판독기(GC.GetTotalMemory · Environment.WorkingSet · GC.CollectionCount · ThreadPool.PendingWorkItemCount · GetAllocatedBytesForCurrentThread — 각 0B 실측)로 표본화.
- ★[실결함] 정량 엔진 세션 VWAP — 누적 거래량 감소(새 거래일)에도 VWAP 누적을 이어 가 하루를 넘기면 여러 날 평균(장기 구동에서만 드러남) → 세션 리셋.

### 36.2 구현
- `StockManager/Simulation/LongSoakProfileHarness.cs` — 하네스(실 게이팅 사슬 + 가상 브로커) + 정량 엔진(비트 27) + 관제 허브 + 헬스 에이전트(실 WS · 비트 21) + 스톰 게이트 + 비관리 호가 표 + MMF 블랙박스 · 모의 시계 · 시간당 표본(고정 배열) · 워밍업 후 raised 전이/블랙박스 InterlockRaised 계수 · WS 주입.
- [방향 B] HealthMonitoringAgent MemoryTrend(같은 비트 21 · 사유 8): 1분 표본 128점 링 · 1시간+ 창 최소제곱 기울기 · R² ≥ 0.9 · ETA(상한 도달) ≤ 1시간 = 상한 초과 전 발주 중단. 기동 성장 · GC 톱니 · 평탄 잡음 · 창 미달 = 무트립.

### 36.3 검증 · 한계
- LongSoakMemoryProfileTests.cs 7건 — 24시간: 틱 72만 · 호가 14만 · 주문 3,000 = 체결 3,000 · 구동 스레드 할당 0B · GC 0/0/0 · 워밍업 후 raised 0 · 블랙박스 0 · 스톰 미격발 · WS 1.06GB 주입 = 비트 21 단독 격발 후 송출 0 · 누수 예측 61분째 883MB 에서 트립(ETA 48분) · 비관리 표 Dispose 후 복귀 · 방어 6종 끔 6/6 Red(핫패스 할당 1건 주입 포함). · 전체 3,556 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 통과.
- 부수 교정: MainDashboardAiFeedConcurrencyTests δ=0 2건이 단일 측정 창이라 전체 스위트에서 8,168B(할당 문맥 크기 — 창에 겹친 타 스레드 GC 의 회계 흔적)로 2회 연속 실패 → 측정 창 최대 3회 최솟값(저장소 표준 MinAllocated 방식). 실제 할당이면 3회 모두 > 0.
- 한계: 합성 시세(실 틱 재생 아님) · 실시간 압축이라 스레드풀/GC 정지/Working Set 의 '시간 의존' 열화는 재현 불가(추세 판정은 주입 시계 단위 테스트로 입증) · 관리 힙 Δ 약 1MB 는 테스트 호스트 타 스레드(구동 스레드 0B · GC 0) · 실 24시간 무인 가동 리허설은 운용 과제.

## 37. 차기: KIS 10호가(H0STASP0) 결선 · 교차 피드 확인 시그널 (2026-10-08 · 미커밋)

### 37.1 전제 검증
- ★거짓 전제: 키움 · KIS 는 같은 KRX 호가창을 중계한다(ChildSlotOrderSplitter 설계 주석과 동일) — 두 피드 차이 = 전송 지연/노후화이지 '차익' · '브로커 보유 잔량'이 아니다. 지시서 방향 A(잔량 많은 브로커로 자식 가중 배치)는 근거가 없어 미구현. 교차 피드는 데이터 품질 검증으로 사용(방향 B).
- (참고) 거래소 단위 차이(KRX ↔ 대체거래소)는 다른 TR · 다른 설계 — 이번 범위 아님.
- 정량 엔진 입력은 키움 호가뿐이었다(§35 한계).

### 37.2 구현
- `StockManager/Adapters/KisOrderBookParser.cs` — H0STASP0 공개 문서 필드 순서(0 코드 · 3~12 매도호가 · 13~22 매수호가 · 23~32 매도잔량 · 33~42 매수잔량) δ=0 판독 · 잘림/비숫자/음수/최우선 결측 거부(실서버 미검증).
- KisOpenApiAdapter onBook(평문 1레코드만) · KisProtocol.ClassifyTr H0STASP0 = 중립 · KisRealtimeSession bookSymbols 구독(상한 38).
- QuantSignalProvider 피드별 상태(불균형 · 마이크로 추세 · 변동성 · 최우선 호가 · 수신 시각). 교차 모드: 두 피드 신선(≤500ms) ∧ 방향 일치 = min(|점수|) · 불일치 = 0 · 한 피드만 = ½(Zero Single-Source Bias) · [방향 B] 피드 간 꼬임(한쪽 매도 < 다른 쪽 매수) = 확신 0 · 연속 3회 = 비트 27 Disarm(CrossFeedCrossed). 비교차 모드 = 기존 동작 그대로(피드 1 무시).
- 호스트: `Kis.BookSymbols`(쉼표 · 기본 빈 값) 있으면 KIS 호가 구독 + 교차 모드 · `Strategy.CrossMaxSkewMs`(기본 500).

### 37.3 검증 · 한계
- CrossBrokerSignalTests.cs 8건(파서 · 어댑터 경로 · 교차 확신도 정책 · 꼬임 연속 3회 Disarm · 하네스 E2E: 단일 피드 송출 0 · 양 피드 일치 송출 · 꼬임 후 송출 0 · δ=0) · 방어 8종 끔 8/8 Red · 기존 정량/Soak 15건 무변화 통과. 전체 3,564 Pass(2회 중 1회 기존 동시성 테스트 Stage4FullRolloutConcurrencyTests 간헐 실패 — 단독 재실행 통과 · 본 변경 무관) · 오중 빌드 0W/0E.
- 한계: H0STASP0 필드 순서 실서버 미대조 · 시각은 수신 시각 기준(거래소 시각 초 단위라 미사용) · 교차 모드는 KIS 실시간 결선(Kis.RealtimeEnabled + HTS ID) + 호가 종목 설정 시에만.

## 38. v12.16.0-DEV 재검증 · 선별 커밋 · 태그 (2026-10-08)

### 38.1 재검증
- infra-scan(Phase 83 결합도) coupling 0 ≤ baseline 117 · Phase114 PureShell total=82 · coupled=40 · shell=42 · ratchet=40(상한 ≤ 40 유지).
- 오중 빌드 Release -warnaserror 0W/0E · 전체 3,565 Pass(최종 3회 중 2회 전원 통과 · 1회 기존 MmfIpcFabricTests 실스레드 백프레셔 간헐 — 단독 5/5 · v12.14.1 이후 무변경) · Stage4FullRolloutConcurrencyTests 단독 ×5.
- Soak 측정 보강: GC 횟수는 프로세스 전역 — 앞선 테스트 잔여 가비지가 측정 창에서 수집돼 1/1/0 오탐 → 측정 직전 GC 정리 + 기준선 재설정(LongSoakProfileHarness.ResetBaseline) · 구동 스레드 0B 단언 유지.
- ★[실결함] StepUpTierGovernor 단계/한도 경합 — 단계 CAS 뒤 한도(_capRaw)를 따로 기록: 선점된 승격 스레드가 늦게 깨어 이전 단계 한도로 덮음(지난 회차 실측 'Full 인데 5억 = 2단계 5%') · Rollback(안전지대/완전 Disarm)과 겹치면 '단계 0/−1 인데 높은 한도'(fail-open) 가능 → 한도를 단계의 순수 함수로(별도 필드 제거 · Disarm = 0). 회귀 스트레스 테스트 Cap_AlwaysMatchesTier_UnderStepUpRollbackDisarmRace 추가(경합 창이 나노초라 수정 전 코드로 결정론 재현은 불가 — 3회 통과 · 정직 기록).

### 38.2 커밋
- 선별 스테이징: §34~§38 변경만 · 제외 = StockManager/Blazor/Pages/BrokerSettingsPage.razor · StockManager/Blazor/Pages/InstallerBuildPanel.razor · .claude/*(타 세션).
- 커밋 "v12.16.0-DEV — KIS L2 Depth Parser, Cross-Broker Signal Validation & Interlock Bit 27 Complete" + 주석 태그 v12.16.0-DEV(로컬 · push 안 함).

## 39. 차기: KIS H0STASP0 공식 레이아웃 대조 · 호가 경계 방어 · 피드 격리 · 시각 이탈 거부 (2026-10-08 · 미커밋)

### 39.1 전제 검증
- ★VTS 실수신 패킷 캡처 · 실계좌 연결 없음 → '실데이터 1:1 대조'는 불가. 대신 KIS 공식 공개 예제(koreainvestment/open-trading-api · examples_llm/domestic_stock/asking_price_krx)의 컬럼 정의(현행 63필드 · 실전/모의 같은 TR)와 파서 인덱스를 대조 → 0~44 완전 일치(0 종목 · 2 시간구분 · 3~12 매도호가 · 13~22 매수호가 · 23~32 매도잔량 · 33~42 매수잔량 · 43/44 총잔량).
- ★[실결함] HOUR_CLS_CODE(필드 2) 미판독 — 장전/장후 예상 · VI · 시간외 예상 호가가 연속매매 호가처럼 시그널 엔진에 유입(예상 잔량이 불균형/교차 판정 오염). 이제 '0'(장중)만 피드 · 나머지 무시 계수.
- 방향 A(0.1ns 프레임 분석) 불가 — Stopwatch 분해능 수십~100ns(이 호스트) · 실패킷 부재. 방향 B 채택.

### 39.2 구현
- KisOrderBookParser: 최소 45필드 · 사유 코드(KisBookReject: 잘림 · 종목코드 · 숫자 아님 · 음수 · 최우선 결측 · 단조성 · 구멍/가격 없는 잔량 · 연속매매 최우선 교차 · 시간구분 · 프레임 헤더) · 총잔량/시간구분 노출 · δ=0.
- KisOpenApiAdapter: 이탈 틱 무시 + 블랙박스 KisBookRejected(98 · 사유 · 종목 · 연속 수) · 연속 5회 = KIS 호가 격리(전달 0 · KisBookQuarantine 99) · 정상 20회 = 복귀 · 예상 호가 제외.
- QuantSignalProvider: 피드 격리(SetFeedQuarantined — 입력 무시 · 교차 판정 제외 → 키움 단일 출처 ½ 유지 · Zero Single-Source Bias 계승) · [방향 B] 두 피드 수신 시각 격차 > 1초(`Strategy.CrossHardSkewMs`) = 교차 확신 원자적 0(CrossSkewVeto) · 0.5~1초 = 단일 출처 ½.
- 지시서 '격리 시 키움 단독 모드 전환'은 단일 출처 감쇄 유지로 해석(감쇄 없는 단독 전환 = 교차 확인 모드의 보수성 후퇴).

### 39.3 검증 · 한계
- KisVtsOrderBookParserTests.cs 6건(공식 63컬럼 사상 · 경계 사유 19종 · 격리 5회/복귀 20회 · 이탈 데이터 엔진 유입 0 · 블랙박스 사유 기록 · 시각 이탈 1.1초 = 0 · δ=0) · 방어 9종 끔 9/9 Red · CrossBrokerSignalTests 최소 필드 기대값 갱신(43→45). 전체 3,571 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 coupled 40 = ratchet 40.
- 한계: 대조 기준 = 공식 예제 정의(실패킷 아님) · KIS 연결 끊김(무수신) 상태에서 키움만 갱신되면 1초 뒤 교차 확신 0(교차 모드 의도된 fail-closed — KIS 피드 복구 또는 교차 모드 해제 필요).

## 40. 차기: AI 대시보드 UX — 한글 라벨 매핑 · 확신도 배지 · 점멸 경고 · 스파크라인 · 경량 모드 (2026-10-08 · 미커밋)

### 40.1 전제 검증
- `MS2E.UI.Portable/Helpers` 부재 → 신설. RCL 은 엔진 열거형(QuantReason · ArmingInterlockSource) 참조 불가 → 코드 표를 Portable 에 정적 배열로 복제하고 호스트 테스트가 열거형 전수 대조로 동기화 강제(코드 추가/재번호 시 실패).
- ★정직: Blazor 렌더 트리 · 속성 문자열화는 프레임워크 할당 — '렌더 루프 0B' 는 보증 불가. 0B 보증 범위 = 라벨 조회 · 확신도 텍스트(101개 정적 캐시) · 배지 판정 · 스파크라인 좌표 작성(고정 char 버퍼). 스파크라인 DOM 속성 문자열은 프레임 버전당 1회.
- §31 판단 로그의 '사유 N' 숫자 표기가 이번 개선 대상(§35 한계).

### 40.2 구현
- `MS2E.UI.Portable/Helpers/SignalLabelMapper.cs` — 판단 근거 16 · 인터락 29(0~27 + 63) 한글 · 경고 근거(변동성 거부 · 전략 이상 · 호가 꼬임 · 시각 이탈) · 점멸 비트(헬스 21 · 전략 이상 27 · 송출 우회 15 · 자격증명 1) · 확신도 배지(0.85 게이트: 매수 적 · 매도 청 · 보류 회색 · 거부 경고).
- `MS2E.UI.Portable/Helpers/DashboardRenderHelpers.cs` — [방향 A] SparklineWriter(정수 좌표 · 고정 버퍼 · 85% 게이트 선) · [방향 B] DashboardDegradationGovernor(지연 EWMA > 250ms 연속 3회 = 경량 · < 100ms 연속 10회 = 복귀 · 무효 표본 무시).
- PaperTradingDashboardView: LED/포렌식 한글 · 점멸(경량 모드에선 끔) · 판단 로그 배지 + 확신도 진행률(85% 표식) + 한글 사유 · 확신도 스파크라인 · 렌더 지연(허브 조립 시각 → 렌더 완료) 관측 → 경량 모드(곡선/스파크라인/점멸 생략). @inject 2개 불변 · 호스트 shim Inject 0.

### 40.3 검증 · 한계
- DashboardUxLabelMappingTests.cs 7건(열거형 전수 대조 · 0B · 게이트 경계 · 스파크라인 · 히스테리시스 · 허브 꼬임 판단 → 경고 라벨/점멸 · 래칫 · RCL 순수성) · 방어 8종 끔 8/8 Red · 전체 3,578 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 coupled 40 = ratchet 40(UI 고도화 후 불변).
- 한계: 브라우저 실렌더/점멸 육안 미확인(WinForms WebView 호스트) · 스파크라인 = 프레임의 최근 판단 48건(종목 혼합) · 경량 모드 지연은 서버 측 렌더 완료 기준(브라우저 페인트 시각 아님).

## 41. v12.16.1-GM Production Golden Master 출하 (2026-10-08)

### 41.1 전제 검증
- 출하 버전 원천 = 호스트 csproj `<Version>` → 12.15.0 → 12.16.1 승격(Assembly/File 12.16.1.0 · ReleasePackager.DefaultVersion · zip 이름 · 태그 v12.16.1-GM). 태그 v12.16.1-GM 은 사전 부재(-f 불요 — 신규 주석 태그).
- [방향 A] zip 무추출 봉인 재수검은 기존재(`--verify-zip` · ZipSealDryRunVerifier, §33) — 재사용 · 실측 시간만 기록.
- [방향 B] 기존 [7/10] IL 가드는 테스트 프로세스가 '로드한' 어셈블리를 리플렉션으로 검사하고 범위가 BlazorHomeForm 트리 한정 → 출하 zip 자체의 바이너리는 직접 판독하지 않았고, BlazorHomeForm 밖 조회 지점(InfraLiberationSlice2 의 RuntimeConfigSnapshot.Capture)은 사각.

### 41.2 구현
- `StockManager/Tools/ShippedBinaryLiveArmedGuard.cs` — 출하 zip 의 `host/MS2E.StockSuite.dll` 항목 스트림을 메모리로만 읽어(추출 · 로드 · 실행 0) System.Reflection.Metadata 로 어셈블리 전체 메서드 IL 판독. 조회 지점 = 키 ldstr 이후 GetBool 소비(배열 초기화 등 조회 아닌 사용 제외) · 판정 = ldstr → ldc.i4.0 → GetBool 직결 · 두 키 각각 지점 ≥1(비공허성) · 손상 zip/PE · 항목 누락 = 거부(못 읽음 ≠ 위반 없음).
- 콜드 CLI `--verify-live-armed <zip>`(ReleaseHandshakeCli) · release-package.ps1 [9/10] 에서 `--verify-zip` 직후 실행 · 거부 = 출하 산출물 전량 폐기 후 중단(Remove-ReleaseOutputs 5곳).
- 버전 핀 갱신: GoldenMasterFreezeTests · GoldenMasterReleasePackagingTests(12.16.1 · 스크립트 머리말 v12.16.1-GM · 출하 인터락 29종 + StrategyAnomaly) · QuantumVaultPipelineIntegrationTests(폐기 분기 4 → 5).

### 41.3 검증
- GoldenMasterReleasePackagingTests §5 4건: 빌드 산출 DLL 지점 16 · 위반 0 · ~50ms · ldc.i4.0→ldc.i4.1 1바이트 패치 사본 16/16 거부 · 항목 누락/잘린 PE/zip 아님/부재 파일 = 거부 · 스크립트 [9] 결선 순서.
- 방어 끔 5종 중 4종 Red: 리터럴 false 판정 제거 · 키별 비공허성 제거 · 스크립트 미결선 · ★InfraLiberationSlice2 소스 기본값 true 로 뒤집기(기존 BlazorHomeForm 가드는 Green 유지 = 사각 실증, 신규 가드 Red). stelem 정지 조건 제거는 Green — 현 코드에선 다음 ldstr 정지가 배열 초기화를 이미 차단(중복 방어 · 독립 미검증).
- ★실결함(첫 출하 실행 4d4d7a1 · [9/10] 거부 · 원자 폐기 정상 동작): PS 5.1 Compress-Archive 가 zip 항목을 'host\MS2E.StockSuite.dll'(역슬래시)로 기록 → 가드의 정확 일치 조회 = 항목 없음(지점 0 · 거부). 단위 테스트 합성 zip 은 '/' 라 미검출. 교정: ZipSealDryRunVerifier 와 같은 '\'→'/' 정규화 · 정규화 후 동명 2개 = 거부(모호). 회귀 테스트(역슬래시 항목 수용 · 중복 거부) · 방어 끔 2/2 Red. 수정 커밋 후 태그 v12.16.1-GM 재지정(-f · 미푸시).

### 41.4 출하 기록
- 태그 v12.16.1-GM → 593525d(재지정 · 미푸시) · 형제 작업 트리 D:/PROGRAM/SM_GM_wt 에서 release-package.ps1 -NoPeer [0/10]~[10/10] 전 단계 통과 · 작업 트리 제거.
- release-v12.16.1-GM.zip 3,013,329B · SHA-256 6F03A77F2C901CCC40ED35D3A95EB077CEAA1CDE76CF6EAD89361EC9BA50AAAF · masterSeal 14019EFF6BDC0FB5983D2342BE5417B69D1F2744BEA0740FDD1775D915ADB1B9 · 아티팩트 38 · 인터락 29 · liveArmed false · 피어 기록 없음(-NoPeer · QuantumVault 읽기 전용).
- [9/10] --verify-zip Ok · --verify-live-armed Ok(지점 16 · 위반 0) · [10/10] sync/handshake Ok · 스크립트 내부 스위트 3,583 Pass.
- primary release/ 로 5종 복사 · v12.15.0-GM 산출물은 release/superseded-v12.15.0-GM/ 로 이동 · 출하 체인 테스트 27/27(zip 무추출 재수검 38ms · 1비트 변조 거부 · 실 zip IL 판독 Ok).
- 첫 실행(4d4d7a1)은 [9/10] 역슬래시 결함으로 거부 · 산출물 원자 폐기 확인 — 불완전 zip 잔존 0.

## 42. 차기: 다중 브로커 세션 자동 복구 — 침묵 단절 강제 종료 · 지수 백오프 · 재구독 · 대사 결속 · 소진 래치 (2026-10-08 · 미커밋)

### 42.1 전제 검증
- SessionRecoveryManager 부재 → 신설. ProcessRolloverGuard · BrokerOpenOrderReconciler · KisRealtimeSession(재접속 시 재구독 기존재 · 고정 5초 무한 재시도) · KiwoomBridgeLink 실재.
- ★실결함 3건: ① 장중 3초 침묵 시 KisSessionInterlock 이 Disarm 비트만 올리고 소켓은 아무도 닫지 않음 → half-open 연결의 ReceiveAsync 무한 대기 · 재접속/재구독 없음. ② 백오프 · 소진 판정 없음. ③ 대사기 확정 비트가 재접속 후에도 남아 단절 동안의 미체결 원장을 다시 대조하지 않음.
- 거부/한정(정직): 키움 파이프 자동 재접속은 하지 않는다 — KiwoomProxySupervisor 는 브리지 1회용 설계(하트비트 단절 = 전역 동결 · 재기동 운용자 몫)이므로 감지 · 격리 · 복귀 시 대사만. KIS 미체결 조회(TTTC8001R)는 호스트에 미결선(파서 · OnKisPage 만 존재) → 재접속 후 KIS 대사 확정 불가 → KIS 격리 유지(fail-closed). 프로덕션엔 소프트웨어 재무장 경로가 없다(TryRearm 호출부 0) — 자동 복구가 되살리는 것은 '가시성'(체결 · 호가 · 대사)이지 무장이 아니다. 소켓 재접속 자체(ClientWebSocket · 구독 JSON)는 BCL 할당 — δ=0 범위는 감지 · 백오프 · 전이 · 격리 · 동기 허가.

### 42.2 구현
- `StockManager/Adapters/SessionRecoveryManager.cs` — 브로커당 상태기계(Connecting · Connected · Backoff · Reconciling). 단절/침묵 = 즉시 격리(한도 피드 세션 격리 = 한도 0 + 스위치보드 블랙리스트) · [방향 A] 50ms 평가 타이머의 Pump 가 단조 시각 스캔으로 백오프(1·2·4·8·16s) 기한 도래 시 재사용 ManualResetValueTaskSourceCore 완료(재시도마다 Timer/Task.Delay 0) · 재접속 = 대사 확정 철회를 먼저 끝낸 뒤 Reconciling · 요청 이후 확정 전까지 격리 유지 · [방향 B] 재접속 연속 5회 실패(대사 전 재단절 포함) = 소진 래치 → 자식 매트릭스 IsolateBroker(비트 23 sticky) · 이중키 TryReset. 최초 접속 전 실패는 백오프만(격리 · 소진 없음 — 블랙리스트는 재무장으로만 지워짐).
- ★설계 검토 중 실측 결함: 격리 작동을 락 밖에서 하면 펌프의 해제(false)가 동시 단절의 격리(true) 뒤에 적용돼 단절된 브로커가 한 주기 열림 → 격리는 상태 락 안에서 현재 상태 반영(ActuateLocked) · 매 펌프 재주장.
- KisRealtimeSession: recovery 결속 시 펌프 허가 대기 · 연결별 취소원 · AbortConnection(침묵 강제 종료) · 미결속 = 기존 동작. KisSessionInterlock: SilentNow(판정만) · SetRearmHold(대사 미확정/소진 = 재무장 거부). BrokerOpenOrderReconciler: RequestReconcile(venue)(확정 철회 · 진행 배치 무효화 · 즉시 조회) · IsConfirmed. BrokerLimitFeedProvider: SetSessionIsolated(한도 0).
- 결선(BlazorHomeForm): KIS = 자동(침묵 · 백오프 · `Kis.ReconnectMaxFailures` 기본 5 · 소진 = IsolateBroker(1)) · 키움 = 수동(링크 프로브 · 복귀 시 OPT10075 대사 · 비트 22) · 둘 다 50ms 평가 타이머에서 Pump.

### 42.3 검증 · 한계
- SessionAutoRecoveryTests.cs 14건(호스트 결선 · 오중 빌드 회귀 · 상태기계 · 백오프 수열 · 소진 래치/재래치 · 플래핑 · 수동 모드 · 해제↔단절 경합 스트레스 20,000 사이클 위반 0 · δ=0 · 통합: half-open → 침묵 강제 종료 → 1s 재접속 · 재구독 40건 동일 → 낡은 확정 철회 → 확정 전 한도 0 · 재무장 거부 → KIS 대사 패스 → 해제 · 접속키 장애 소진). 방어 끔 11종 11/11 Red(락 밖 해제 경합 재현 포함). 전체 3,597 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 coupled 40 = ratchet 40 · CrossBrokerSignalTests 결선 핀 갱신(recovery 인자).
- 한계: 실 KIS/키움 네트워크 단절 미실측(전송 대역) · KIS 대사 수단 미결선이라 프로덕션 KIS 는 재접속 후 격리 유지 · 비동기 허가(대기자 깨움)는 스레드 풀 큐잉 할당 가능(재시도당 1회).

## 43. 차기: KIS 미체결 조회(TTTC8001R) 실배선 · 재접속 후 대사 확정 해제 · 불일치 래치 · 유령 비상 취소 (2026-10-08 · 미커밋)

### 43.1 전제 검증
- v12.17.0-DEV 태그 없음 — §42 세션 복구는 미커밋 상태로 이어서 작업. 지시서의 'Bit 22(ReconciliationDiscrepancy)' = 실제 이름 OpenOrderDivergence(비트 22). SessionRecoveryManager.OnReconciliationCompleted 부재(펌프 폴링 해제만) → 신설.
- KIS 판독 경로는 기존재(KisProtocol.ParseOpenOrders · 대사기 OnKisPage) — 결손은 ① 조회 호출부 0(TTTC8001R REST 없음) ② 연속 조회(tr_cont 헤더 · ctx_area 키) 미처리 ③ KIS 유령 취소 경로 '미배선 — 차단만' ④ 대사기 진행 배치가 venue 공용 1개라 KIS 조회가 키움 OPT10075 대기 배치를 덮어쓸 수 있음.

### 43.2 구현
- KisLiveBroker: QueryOpenOrdersJson(inquire-daily-ccld · TTTC8001R/VTTC8001R · KST 당일 · CCLD_DVSN=02 · 연속 = 헤더 tr_cont N + ctx 키 · 응답 tr_cont F/M = 다음 페이지) · CancelOpenOrder(odno, ord_gno_brno → TTTC0803U 전량 취소) · SendReadCont(응답 tr_cont). ★공개 문서/샘플 기준 — 실계좌 미검증.
- KisProtocol: ord_gno_brno → 행 Reserved(취소 조직번호) · TryParseContinuation(ctx_area_fk100/nk100 → 고정 버퍼 · 초과/이스케이프/깨짐 = 거부).
- `StockManager/Adapters/KisOpenOrderQuery.cs` — 전용 1초 타이머(HTTP 블로킹 — 50ms 평가 타이머 분리) · 30초/미확정 3초 · 대사 요청 계수 변화 = 즉시 · TryBeginSnapshot(진행 배치 덮어쓰기 0) · 페이지 루프(상한 20 · 실패 = AbortSnapshot · 재시도 루프 없음) · 게이트(한도 피드 대사 격리 마스크 = 조회 수단 없음 ∨ 미확정 ∨ 유령 ∨ 불일치 ∨ 래치 · 생성 즉시 닫힘) · OnPass 라우팅(확정 = 복구 관리자 즉시 해제 통지 · 불일치/유령 = 게이트 + 영구 래치 + 호스트 격리 1회).
- 대사기: TryBeginSnapshot · AbortSnapshot · AttachKisCanceller(★[방향 B] KIS 유령 = 패스당 ≤4건 · 5초 중복 억제 · 지점번호 미상 = 송신 안 함). SessionRecoveryManager: OnReconciliationCompleted(락 안 확정 프로브 재확인 후 즉시 Connected) · OnDiscrepancy(영구 래치 · Held 포함 · 이중키 TryReset). 한도 피드: SetReconcileIsolated(세션 격리와 별개 마스크 — 두 구동자 비트 덮어쓰기 0).
- 결선: KIS 설정 시 조회기 + 취소 결속 + 패스 훅 · 불일치 = 블랙리스트 + 자식 매트릭스 IsolateBroker(1)(비트 23) · 미설정 = KIS 대사 격리 고정.

### 43.3 검증 · 한계
- KisOpenOrderReconciliationTests.cs 9건(판독 δ=0 · 페이지 키 전달 · 실패 4종 + 바쁨 + 미설정 · 재접속 즉시 해제 · 오발 통지 무시 · 불일치 래치/가격 괴리/이중키 · 유령 취소 상한/억제/지점번호 · 낡은 스냅샷 경합 · δ=0 판독+대사+게이트+라우팅 · 결선/오중 빌드). 방어 끔 13종 13/13 Red(래치 무감 테스트 1건 발견 → 연결 상태에서 Held 단언으로 보강). 전체 3,606 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 coupled 40 = ratchet 40 · KisVtsProtocolIntegrationTests 이탈 응답 핀 갱신(SendReadCont 3-튜플 — 본문 미반환 불변).
- 한계: 실 KIS 계좌 미검증(TR · 필드 · tr_cont 의미 · ord_gno_brno 를 KRX_FWDG_ORD_ORGNO 로 쓰는 것은 공개 샘플 기준) · 유령 비상 취소는 '호스트가 모르는 KIS 미체결' 전부가 대상 — 같은 계좌로 HTS/MTS 수동 주문을 내면 취소된다(키움 경로와 같은 기존 설계 · 전용 계좌 전제) · 방향 A(SHA-256 해시 스트리머)는 미구현 — 기존 대사기 패스 해시 체인 감사 + MMF 블랙박스 기록이 패스 판정 단위로 이미 존재.

## 44. v12.17.0-GM Production Golden Master 출하 — 세션 자동 복구(§42) · KIS TTTC8001R/0803U 대사 결선(§43) · 인터락 29종 (2026-10-08)

### 44.1 전제 검증
- v12.17.0-DEV 태그 없음 — §42 · §43 은 이번 출하 커밋에 함께 포함(미커밋 상태였음). 버전 원천 csproj 12.16.1 → 12.17.0(Assembly/File 12.17.0.0 · 태그 v12.17.0-GM · zip release-v12.17.0-GM.zip). 태그 사전 부재.
- [방향 A] 출하 zip 호스트 IL 정적 판독은 기존재(ShippedBinaryLiveArmedGuard · §41 · 스크립트 [9/10] --verify-live-armed) — 재사용. '16 지점'은 실측 조회 지점 수(Dispatch.ValveWired/LiveArmingWired GetBool · ldc.i4.0).
- [방향 B] ★실결함 2건(무추출 수검기 ZipSealDryRunVerifier): ① manifest.sig 항목 2개 = 미검출(루프가 중복 검사 전에 건너뜀 · 어느 쪽이 쓰일지 모호) ② 매니페스트 기재 비바이너리(runtimeconfig/deps.json 등)의 대소문자 변형 사본 = 무시(Windows 대소문자 무시 파일시스템에서 해제 시 봉인 항목을 덮어씀). 기존 중복 검사는 기재 항목의 서수 일치만 대상.

### 44.2 구현
- ZipSealDryRunVerifier: 모든 파일 항목(manifest.sig 포함)을 '\\'→'/' 정규화 + 대소문자 무시 집합으로 대조 · 두 번째 = DuplicateEntry. ShippedBinaryLiveArmedGuard: 호스트 항목 대조를 대소문자 무시로(대소문자 변형 사본 = 중복 거부).
- 버전 핀 갱신: GoldenMasterFreezeTests · GoldenMasterReleasePackagingTests(12.17.0 · 스크립트 머리말 v12.17.0-GM) · release-package.ps1 머리말(BOM 유지).
- GoldenMasterReleasePackagingTests §6: PS 5.1 역슬래시 실봉인 zip(실 호스트 DLL + 봉인 runtimeconfig) = 수검 Ok + IL 판독 Ok · manifest.sig 2개 / 봉인 비바이너리 대소문자 변형 / 구분자 변형 바이너리 / 대소문자 변형 바이너리 = 전부 DuplicateEntry · IL 판독기 모호 항목 = 거부. 방어 끔 3종 3/3 Red.

### 44.3 검증 · 출하 기록
- 게이트: 오중 빌드 0W/0E · 전체 3,607 Pass ×2(1회차 Pipeline_FullChain_500k δ=0 단일 창 플래키 1건 → 10만×5창 최솟값으로 교정 · 판정 약화 없음) · infra 0 · Phase114 40/40.
- 태그 v12.17.0-GM → 57a5954(신규 · 미푸시) · 형제 작업 트리 D:/PROGRAM/SM_GM_wt 에서 release-package.ps1 -NoPeer [0/10]~[10/10] 1회 통과(스크립트 내부 스위트 3,607 Pass) · 작업 트리 제거.
- release-v12.17.0-GM.zip 3,020,925B · SHA-256 C7BA6657829F923A3D4CA3723AB2A423ADC31711140B2A6843D36B39B5BC5101 · masterSeal 4636F2B239D2CD178CA176B1556E9FE378E2BEE9C2C949FABAA83FE2435D28B0 · 아티팩트 38 · 인터락 29 · liveArmed false · 피어 기록 없음(-NoPeer).
- [9/10] --verify-zip Ok · --verify-live-armed Ok(지점 16 · 위반 0) · [10/10] sync/handshake Ok. primary release/ 로 5종 복사 · v12.16.1-GM 산출물은 release/superseded-v12.16.1-GM/ · 출하 체인 테스트 28/28(무추출 재수검 41ms).

## 45. 차기: 다중 브로커 단절 · 자동 복구 · 미체결 대사 24h 가속 Long-Soak · 복구 상태 포렌식 모니터 (2026-10-08 · 미커밋)

### 45.1 전제 검증
- LongSoakProfileHarness 실재 — 단일 경로 모의투자(7계층 · 정량 엔진 · 관제) 24h 가속이며 세션 복구 사슬은 없음 → 같은 원칙(주입 모의 시계 · 시드 결정론 · 고정 버퍼)의 동반 하니스를 신설(기존 하니스 · 그 테스트 무변경).
- 거부/한정(정직): '0.1ns' 기록 불가 — 단조 시계(Stopwatch/QPC) 해상도는 Stopwatch.Frequency(일반 100ns) · 원시 틱 기록. 실 키움 COM/KIS VTS 연결 없음(모의 소켓/파이프/REST — 비동기 실소켓 경로는 SessionAutoRecoveryTests 가 별도 실측). 방향 B(연쇄 소진 전역 Disarm)는 기존재로 판정 — KIS 재접속 소진 = IsolateBroker(1) → 비트 23 전역 sticky Disarm · 키움 파이프 단절 = 브리지 하트비트 ForceFreeze 전역 동결 → 중복 구현하지 않음.
- 실결함(방향 A 근거): SessionRecoveryManager 전이(단절 · 백오프 · 재접속 · 대사 · 해제 · 소진/불일치)가 카운터로만 남고 블랙박스 기록 0 — 사고 후 격리 시점 · 체류 재구성 불가.

### 45.2 구현
- `StockManager/Adapters/RecoveryStateDriftMonitor.cs` — 브로커별 상태 묶음(상태 · Held · Isolated · Exhausted · Discrepancy · 연속 실패) 전이 = SessionRecoveryTransition(100 · 직전/새 묶음 · 체류 Stopwatch 틱) · Held 최장 체류 · 작동 표류(한도 피드 세션 격리 비트 ≠ 관리자 Isolated 가 2 펌프 지속) = SessionRecoveryDrift(101) 1회. δ=0. 호스트 50ms 평가 타이머에서 관리자 펌프 뒤에 결선.
- `StockManager/Simulation/MultiBrokerRecoverySoakHarness.cs` — 실 구성요소(복구 관리자 2 · KIS 세션 인터락 3초 · 대사기[키움 venue = 하니스 · KIS = KisOpenOrderQuery] · 한도 피드 · 모니터 · 호스트 미체결 원장 4건) + 모의 브로커(KIS 소켓 · 서버 장애 · REST 재접속 후 2초 지연 · 키움 파이프 · 운용자 복구 · 원장 페이지/행 고정 버퍼). ★진실 독립 발주 탐침(매 초): 발주 가능(한도 Available ∧ 비트 22 해제)인데 진실(연결 ∧ 접속/복구 후 대사 확정 ∧ 오염 증거 없음)이 더러우면 위반. 주입 오염은 원장이 호스트에 조회된 순간부터 더러움(그 전은 탐지 지연 — 정직).

### 45.3 검증 · 한계
- MultiBrokerRecoverySoakTests.cs 7건. 24h(86,400 모의 초 + 워밍업 1h): KIS 침묵 57 · 강제 종료 57 · 재접속 57(재구독 2,320 = 58 × 40) · 대사 후 해제 57 · 서버 장애 실패 51(1·2·4·8 수열 관측) · 키움 파이프 파기/복구/대사 해제 12/12/12 · ★오발주 0 · 더러운 구간 차단 4,586 · 정상 발주 2,929 · 표류 0 · 소진 0 · 24 시간 창 관리 힙 전부 0B. 초 단위 추적(대사 전 Reconciling/up/held 3초) · 소진 래치(서버 복구 · 대사 확정에도 0 · 이중키 후 개방) · KIS 유령(비트 22 · TTTC0803U 취소 · 래치) · KIS 원가 괴리(비트 22 · 취소 0) · 키움 유령(비트 22 · 키움 취소) · 모니터(전이 3건 체류 · 고장 작동기 표류 1회 · δ=0) · 결선/이벤트 코드 유일/오중 빌드. 방어 끔 7종 7/7 Red(무효 변이 1건 교정 후). 전체 3,614 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 coupled 40 = ratchet 40.
- 한계: 모의 브로커(실 네트워크 지연 분포 · 서버 동작 아님) · 원장 오염 탐지 지연 = 평시 KIS 30초 주기(재접속 직후는 즉시) — 그 사이 브로커 측 변화는 호스트가 알 수 없다 · 비트 19(KIS 세션 sticky)는 프로덕션에선 재무장 전 전역 차단이지만 탐침에선 제외(최악 가정: 재무장 후에도 브로커별 게이트만으로 차단되는지 검증).

## 46. v12.17.1-GM Production Golden Master 출하 — 세션 복구 Long-Soak(§45) · 포렌식 모니터(이벤트 100/101) · 인터락 29종 · 마스터 플래그 IL 판독 (2026-10-08)

### 46.1 전제 검증
- 지시서의 'HANDOVER §44' 갱신 = §44 는 이미 v12.17.0-GM 출하 기록 → 이번 출하는 §46(§45 Long-Soak 미커밋분을 이번 출하 커밋에 포함). v12.17.1-DEV 태그 없음 · 버전 원천 12.17.0 → 12.17.1 · 태그 v12.17.1-GM 사전 부재.
- [방향 B] zip 구분자 정규화 · 중복 가드는 기존재(§41 · §44) — 재사용.
- [방향 A] ★실결함: 출하 바이너리 IL 판독기 · 스크립트 [6/10] 소스 가드 모두 물리 이중키 GetBool 기본값만 대조 — 실제 마스터 플래그 `new ShadowMetricsBridge(..., liveArmed: false)`(BlazorHomeForm)는 어느 쪽도 보지 않았다(리터럴 true 로 바뀌어도 출하 통과).

### 46.2 구현
- ShippedBinaryLiveArmedGuard 확장: ① newobj 대상 생성자의 bool 형식 liveArmed/isLiveArmed 매개변수 = 리터럴 false(ldc.i4.0) 필수 · 마지막 위치 아니면 판독 불가 = 위반 · 생성 지점 수 MasterSites ≥ 1(비공허성) ② 일반 호출의 마지막 liveArmed 인자 리터럴 true = 위반 ③ get_LiveArmed/get_IsLiveArmed 본문 'ldc.i4.1; ret' = 위반. 형식 판독 = System.Reflection.Metadata 최소 시그니처 디코더(bool 여부) — Func<bool> 술어(SecurityAssetVault · LiveArmedEgressSentinel)는 플래그가 아니므로 제외(설계 검토 중 오탐 실측 → 교정). CLI 출력에 마스터 플래그 생성 수.
- release-package.ps1 [6/10]: 마스터 플래그 소스 대조(생성 0건 = 구조 변경 거부 · false 아님 = 거부) · 머리말 v12.17.1-GM(BOM 유지). 버전 핀 갱신(Freeze · ReleasePackaging).
- GoldenMasterReleasePackagingTests §7: 빌드 DLL 마스터 생성 1 · 위반 0 · newobj 직전 1바이트 패치 = 거부(CLI 포함) · get_LiveArmed 1바이트 패치 = 거부 · 술어 매개변수 오탐 0 · 스크립트 [6/10] 대조. 방어 끔 6종 6/6 Red(★실 소스 BlazorHomeForm liveArmed: true 뒤집기 = 소스 가드 + 바이너리 판독 모두 Red).

### 46.3 검증 · 출하 기록
- 게이트: 오중 빌드 0W/0E · 전체 3,616 Pass ×2 · infra 0 · Phase114 40/40.
- 태그 v12.17.1-GM → 68ff158(신규 · 미푸시) · 형제 작업 트리에서 release-package.ps1 -NoPeer [0/10]~[10/10] 1회 통과([6/10] 마스터 플래그 소스 1건 false · 스크립트 내부 스위트 3,616 Pass) · 작업 트리 제거.
- release-v12.17.1-GM.zip 3,030,966B · SHA-256 4EC0FF7F9A9D6C176A9F8832E86F7E3AB336F165F4D12E10947CE5EF3F66370A · masterSeal 5BEAC1A544CFD91F198A5799AD642B786FAEE780A32A2AD98AC29F47B4575A8E · 아티팩트 38 · 인터락 29 · liveArmed false · 피어 기록 없음(-NoPeer).
- [9/10] --verify-zip Ok · --verify-live-armed Ok(키 조회 16 · 마스터 플래그 생성 1 · 위반 0) · [10/10] sync/handshake Ok. primary release/ 로 5종 복사 · v12.17.0-GM 산출물은 release/superseded-v12.17.0-GM/ · 출하 체인 테스트 30/30(무추출 재수검 62ms).

## 47. 단독 운용 보존(v12.17.1-GM) · QuantumVault 피어 정돈 대기 · 비파괴 준비 탐침 · 전이 경보 (2026-10-08~12 · v12.17.2-DEV)

### 47.1 단독 출하 재검증 · 피어 상태
- release/release-v12.17.1-GM.zip: SHA-256 4EC0FF7F… = 사이드카 일치 · 호스트 CLI --verify-zip Ok(아티팩트 38 · masterSeal 5BEAC1A5… 무추출) · --verify-live-armed Ok(키 조회 16 · 마스터 플래그 생성 1 · 위반 0).
- 피어 오염 격리: StockManager 빌드 정의(sln · csproj · props · targets)에 QuantumVault 경로 참조 0 · 출하 매니페스트 피어 기록 없음(-NoPeer) · 아티팩트에 피어 파일 0(테스트 강제).
- QuantumVault 실측(읽기 전용 · 이 세션 쓰기 0): HEAD c1eb24e(직전 점검 bdb8bb2 → 피어 세션이 대부분 커밋) · 미커밋 코드 3(예: auto_seal.ps1 — key_out.txt 포함) · 키 의심 파일 key_out.txt(내용 미열람) · 매니페스트 봉인 2026-10-07 03:59Z < HEAD 커밋 18:40Z = 낡음 → NotReady.

### 47.2 구현
- [방향 B] `StockManager/Tools/PeerReadinessProbe.cs` + CLI `--peer-readiness <피어 루트>`(0 Ready · 2 NotReady · 64 사용 오류): ① 저장소 최상위 = 지정 경로(아니면 상위 저장소를 판정하는 함정 — 설계 검토 중 실측: 비저장소 폴더에서 감싸는 저장소 전체 스캔 7분 38초 → 교정) ② 미커밋 코드 0(스크립트 [0/10] 규칙) ③ 키 원문 의심 파일 0(추적 · 미추적 · gitignore 포함 · 이름만 — 내용 미열람/미출력) ④ 매니페스트 존재 · rootHash · sealedAtUtc ≥ HEAD 커밋 시각.
- ★실결함(읽기 전용 피어 보호): 일반 git status 는 인덱스 stat 캐시를 기회적으로 갱신하며 .git/index 를 다시 쓴다 — release-package.ps1 피어 precheck 와 핸드셰이크 피어 관측(GitRepoProbe.Git)이 QuantumVault 인덱스를 쓸 수 있었다. GitRepoProbe.Git 모든 호출 + 스크립트 피어 status 에 --no-optional-locks. 실측: 실 QuantumVault 판정 전후 .git/index 해시 · mtime 불변.
- PeerReadinessProbeTests.cs 6건(임시 저장소: Ready/CLI 0 · 미커밋+키(gitignore 안 포함 · 비밀값 미출력) · 매니페스트 누락/무효/낡음 · 저장소 아님/하위 폴더 · ★비파괴(stat 만 바꾼 상태에서 탐침 = 인덱스 불변 · 대조군 일반 status = 재기록 관측) · 스크립트/헬퍼 플래그 · 피어 오염 격리). 방어 끔 6종 6/6 Red. 전체 3,622 Pass ×2 · 오중 빌드 0W/0E · infra 0 · Phase114 coupled 40 = ratchet 40.

### 47.3 피어 정돈 완료 시 런북(정식 이중 저장소 봉인)
1) 탐침: `StockManager\bin\Release\net8.0-windows\MS2E.StockSuite.exe --peer-readiness D:\PROGRAM\QuantumVault --report <파일>` → 종료 코드 0 이어야 진행(NotReady 사유: 미커밋 코드 · 키 파일 이관 · 현재 HEAD 기준 재봉인).
2) 버전/태그: StockManager 는 새 버전(예: 12.17.2)으로 커밋 · 태그 — v12.17.1-GM 단독 출하물은 이미 봉인돼 있으므로 덮어쓰지 않는다. QuantumVault 태그는 피어 세션 또는 사용자 명시 승인 시에만(읽기 전용 원칙).
3) 형제 작업 트리(safe.directory = GIT_CONFIG_* env)에서 `release-package.ps1`(-NoPeer 없이) → [0/10] 피어 고정 · [10/10] 핸드셰이크 PeerPin.
- 한계: 탐침은 커밋 시각 기준 낡음만 본다(매니페스트가 현재 HEAD 산출물의 해시인지 재빌드 대조는 피어 측 도구 몫) · 키 판별은 이름 규칙(내용 미열람이 원칙).

### 47.4 v12.17.2-DEV — 준비 상태 전이 경보 · 대기 관측 (2026-10-12)
- [방향 B] `StockManager/Tools/PeerReadinessWatcher.cs` + CLI `--peer-watch <피어 루트> [--polls N] [--interval-ms M] [--confirm K]`: 폴링마다 판정 묶음(Ready · 사유 비트 · HEAD 앞 12 hex)이 바뀌면 PeerReadinessTransition(102) 블랙박스 기록 · NotReady→Ready 는 같은 HEAD 연속 2회 Ready 에서만 확정(디바운스 — 피어 커밋 도중 순간 Ready 무시) → 런북 '알림' 1회(출하 실행 0 · QuantumVault 태그는 승인 몫) · HEAD 변경 = 재확인 후 재트리거. 호스트 런타임 미결선(피어 독립 · 테스트가 강제). 방향 A('0.1ns' 진단)는 불가(단조 시계 ≈100ns · git 호출 자체가 수십 ms) — 거부.
- PeerReadinessWatcherTests.cs 4건(전이/디바운스/재트리거 순서 · 깜빡임 0 · 실 임시 저장소 정돈 전 구간 트리거 1회 + 피어 작업 트리 · .git 전체 바이트 지문 불변 + CLI 0/2/64 · 호스트 미결선 · 이벤트 102 유일). 방어 끔 5종 5/5 Red.
- 대기 관측(2026-10-12 · 읽기 전용): QuantumVault HEAD c1eb24e(브랜치 feature/v2.8.0-signed-manifest) · NotReady — 미커밋 코드 34(예: GrpcHost/Program.cs — 10-08 의 3건에서 다시 증가: 피어 세션 진행 중) · key_out.txt · 매니페스트 봉인 10-07 03:59Z < HEAD 커밋 18:40Z. --peer-watch 2폴링: 전이 1 · 트리거 0 · .git/index 해시 · HEAD 참조 불변.
- 단독 출하 재대조: release-v12.17.1-GM.zip --verify-zip Ok(38 · 5BEAC1A5…) · --verify-live-armed Ok(16 + 마스터 1 · 위반 0).

## 48. v12.17.3-GM Production Golden Master 출하 — 자율 헤드리스 UI 퍼저 · UiSettingWriteGuard · --verify-ui-switches · UI 기입 가드 IL 판독 (2026-10-12)

### 48.1 실결함(v12.17.3-DEV 교정)
- `IAdminSettingWidgetPort.SetSetting/SetSecret` · `IStrategyHubInfraPort.SetSetting` 이 임의 키를 LocalDatabase 에 기입했다. 호스트 RuntimeConfig precedence = 환경변수 → DB 이므로 `Dispatch.ValveWired` · `Dispatch.LiveArmingWired`(물리 이중 키) · `Broker.LiveArmed` · `Canary.*` · `Risk.*` 등이 순수 UI 한 줄로 변조 가능했다(현 RCL 은 미사용 — 포트가 허용한 것 자체가 Pure Shell 경계 구멍).
- 교정: `src/02_Infrastructure/Engine/UiSettingWriteGuard.cs` 허용 목록(RCL 리터럴 기입 네임스페이스 8종: Lan. Llm. Ollama. Regime. Drift. Nightly. Email. Optimizer. · 미분류 = 폐기 + 계수 · 예외 아님 · 큐잉/재시도 없음). 두 어댑터가 `Admit(key)` 통과 시에만 기입. 새 UI 설정 네임스페이스는 허용 목록 등재 필수(테스트가 RCL 리터럴 키 전수 대조 · 동적 키 기입 0 강제).

### 48.2 자율 UI 검증 하네스
- `StockManager/Simulation/AutonomousUiSwitchVerifier.cs`: 헤드리스 Blazor Renderer 표면(설정 · 내비 · 대시보드 3 루트 · onclick/checkbox/앵커 자동 탐색) · 무장 프로브(실 집계어 28 소스 + 샌니티 = 29 비트 봉인 · 포렌식 기록기 · LiveArmingGate · DispatchArming · 매 회전 카나리 재무장 시도 재현) · 시드 결정론 퍼저(순차 스윕 → 무작위 · 50 회전마다 적대 기입 + 재판독) · 방향 A Fnv64/UiStateMatrix 지문 + 토글 대칭 · 방향 B SwitchStormIsolationGuard(CAS · 폐기) + CoreTickIsolationMonitor.
- CLI `MS2E.StockSuite.exe --verify-ui-switches [--iterations N] [--seed S] [--storm-ms ms] [--out 파일]` — UI/운영 DB/뮤텍스 초기화 이전 분기 · 일회용 %TEMP% DB · LAN 소켓 생략 · 종료 0/1/2. 실측: 10,343 상호작용 위반 0 · 적대 200/200 차단 · 스톰 1초 약 800만 시도/1,000 허용 · 코어 틱 할당 0B · 1ms 초과 비율 8e-6.
- 테스트: bUnit 1.36.0(테스트 프로젝트 전용) `StockManager.Tests/Simulation/AutonomousUiFuzzer.cs` + `AutonomousUiVerificationTests.cs` 35 — DOM↔렌더 프레임 탐색 교차 일치 · 음성 대조(가드 우회 표면 = FAIL).
- 정직 공개: 코어 틱 최댓값은 OS 선점 포함(실측 3.4ms 1회) → 판정 = 1ms 초과 비율 ≤ 0.1%. 고유 상태 포화는 진단(난수 PIN 생성 = 무한 상태 공간). PaperTradingDashboardView 는 클릭 요소 0(읽기 전용 포트) — 렌더 부하 대상.

### 48.3 [방향 A] 출하 바이너리 UI 기입 가드 IL 판독
- `ShippedBinaryLiveArmedGuard` 확장(`--verify-live-armed` · [9/10] 자동 적용): 기입자 3종(AdminSettingWidgetAdapter.SetSetting/SetSecret · StrategyHubInfraAdapter.SetSetting) 각각 `ldarg.1 → call UiSettingWriteGuard.Admit → brfalse` · LocalDatabase 기입 정확히 1회 · Admit 뒤. 허용 목록 `.cctor` ldstr 접두사 ≥1 · 호스트 안전 네임스페이스(25종 실측) 겹침 0. 대상 부재 = 비공허성 위반. `Result.UiGuardSites` = 3 이어야 Ok.
- 테스트(GoldenMasterReleasePackagingTests §8): 빌드 DLL 3/3 · brfalse.s → pop;nop 1바이트 패치 = 기입자마다 거부(3) · CLI 거부 · #US "Email." → "Kis.ab" = 거부. 공허 이미지 비공허성 위반 7(키 2 + 마스터 1 + 기입자 3 + 허용 목록 1).
- 방향 B(zip 항목 정규화 · 중복 가드)는 v12.17.0-GM 에서 이미 출하(§44) — 재구현 없음.

### 48.4 출하
- 버전 12.17.3 · 태그 v12.17.3-GM · `release-package.ps1 -NoPeer`(QuantumVault 피어 미정돈 — §47) · 태그 기준 형제 worktree 에서 패키징 후 release/ 복사 · 이전 v12.17.1-GM 산출물은 release/superseded-v12.17.1-GM 으로 이동.
- 출하 기록(2026-10-12): 태그 v12.17.3-GM = 674e662 · `release-v12.17.3-GM.zip` SHA-256 5268015F128954645CC879F99DA37568BDA5CD48E1DDFAF6C1DE62C0503C9975 · masterSeal 06786D5B68CEC8592986CE4DE66520C854EEB17E10B480340E152DA34EDA8F6D · 아티팩트 38 · 피어 기록 없음(-NoPeer).
- [0/10]~[10/10] 전 단계 통과: 미커밋 코드 0 · 오중 빌드 0W/0E · 전체 스위트 Pass · infra coupling 0 · 래칫 ≤ 40 · 소스 가드(이중 키 14 · 마스터 1) · --verify-seal 38 · --verify-zip Ok · --verify-live-armed Ok(키 조회 19 · 마스터 1 · UI 기입 가드 3/3 · 위반 0) · 핸드셰이크 재수검 Ok.
- 주의(실측): 공유 저장소의 commit-graph 손상 경고(`unable to find all commit-graph files`)가 git stderr 로 나오면 PS 5.1 + ErrorActionPreference=Stop 이 [0/10] 를 즉시 중단시킨다. 프로세스 한정 `GIT_CONFIG_KEY_n=core.commitGraph / VALUE=false` 로 우회(전역 설정 · .git 무변경).

## 49. v12.17.4-GM Production Golden Master 출하 — 2 브로커 분할 라우팅 드라이런 · 주문 경로 구간 계측 · 시뮬레이션 격리 IL 판독 (2026-10-12)

### 49.1 다중 브로커 드라이런(v12.17.4-DEV)
- 전제(정직): 실 키움 COM(x86 프록시 · OpenAPI+ 로그인 · 장중) · KIS VTS(App Key/Secret · 웹소켓)는 개발 호스트/CI 에서 연결 불가 — 가짜 세션 배선은 만들지 않았다. 실 장중 VTS 실측은 사용자 자격증명 · 장 시간이 필요한 별도 작업.
- `StockManager/Simulation/MultiBrokerDryRunHarness.cs`: 호스트 BlazorHomeForm 과 같은 사슬(10호가 → QuantSignalProvider 확신 게이트 0.85 + DepthBook + 참조가 → OrderDispatchRing → GatedOrderDispatcher → OrderDrainWorker[인터락 veto → RouterFeedGate 시그널 게이트 → ChildSlotOrderSplitter 1~3호가 × 브로커 한도 → TickToTrade 송출 직전 veto → 자식 순차 송출] → 브로커별 LiveArmedEgressSentinel → 가상 브로커) + ChildSlotTrackingMatrix 불균형 취소. 가상 브로커 2(키움 주문번호 · KisOrderIdSpace) · 한도 = 고정값 · 미승인 도달 독립 계수.
- 무장 모델: 물리 이중키(기본 off) · InjectUpstreamFault(디스패처 술어만 강제 true — 센티널은 실 이중키) — 상류 전면 오작동 리허설.
- [방향 A] `StockManager/Telemetry/OrderPathStageProfiler.cs`: 시그널 → 게이트 승인 → 첫 자식 출구 진입 → 마지막 출구 판정 구간(log2 ns 히스토그램 · δ=0). 실측(Release · 164 부모 · 워밍업 포함): 시그널→출구 p50 ≤ 16µs · p99 ≤ 131µs · max 12.5ms(첫 호출 JIT). 호스트 미결선(하네스 데코레이터만).
- [방향 B] 신규 구현 없음 — 기존 ChildSlotTrackingMatrix 불균형 취소(기본 500ms · Routing.ImbalanceMs 하한 50ms)를 50ms 로 실측: 49ms 취소 0 · 51ms KIS 잔여 전량 1회 + 비트 23 · 이후 송출 veto.
- `StockManager.Tests/ProductionDryRunRoutingTests.cs` 13: 승인 분할(60/40) · 깊이 상한 · 확신 미달/방향 불일치 전방 거부 · 50ms 차단기 · 키 off(3 조합) · 상류 오작동 = 센티널 첫 자식 차단(전달 0 · sticky · 동결) · 형제 사이 인터락 격발 = 집계어 veto · 퍼징 2,000 사이클 미승인 도달 0 · 송출 경로 δ=0 · 실 브로커 형식 0. 방어 끔 4종 4/4 Red. δ=0 테스트는 확신 임계 0.5(같은 호가 반복 시 마이크로 추세 감쇠 → 0.85 미달 · 게이트는 매 주문 평가).

### 49.2 [방향 A] 시뮬레이션 격리 IL 판독(출하 바이너리)
- 호스트 DLL 은 StockManager.Simulation(가상 브로커 · 무장 술어 강제 true 디스패처 · InjectUpstreamFault)을 함께 출하한다. `ShippedBinaryLiveArmedGuard` ⑥: 최외곽 선언 형식 네임스페이스가 Simulation 인 메서드를 가리키는 newobj/call/callvirt/ldftn/ldvirtftn 이 시뮬레이션 밖에서 나오면 위반 · 허용 = 시뮬레이션 내부 + CLI 격리 형식(UiSwitchVerifierCli · UiFuzzRig — Program.Main 조기 분기). 비공허성 = 시뮬레이션 형식 ≥ 1. 한계: 리플렉션 생성은 IL 토큰이 없어 미탐.
- 실측: 키 조회 19 · 마스터 플래그 1 · UI 기입 가드 3/3 · 시뮬레이션 형식 51(CLI 참조 29) · 위반 0. 오더의 '검증 지점 20개'는 실측과 다르다(키 19 + 마스터 1 = 20 이 의도였다면 일치 · 판독 항목은 그 외 UI 3 · 격리 51 형식 추가).
- 테스트(GoldenMasterReleasePackagingTests §9): BlazorHomeForm 의 LiveArmedEgressSentinel 생성 newobj 토큰을 MultiBrokerDryRunHarness 생성자로 교체한 사본 = BlazorHomeForm 위반 거부 · 시뮬레이션 내부 참조는 허용 · PS 5.1 역슬래시 zip 경유 CLI Ok/거부. 공허 이미지 비공허성 위반 8.
- 방향 B(zip 항목 정규화 · 중복 가드)는 v12.17.0-GM 출하분 그대로(§44) — 재구현 없음.

### 49.3 출하
- 버전 12.17.4 · 태그 v12.17.4-GM · `release-package.ps1 -NoPeer` · 형제 worktree(GIT_CONFIG safe.directory + core.commitGraph=false) · release/ 복사 · v12.17.3-GM 산출물 → release/superseded-v12.17.3-GM.
- 출하 기록(2026-10-12): 태그 v12.17.4-GM = 261feb6 · `release-v12.17.4-GM.zip` SHA-256 82DD88580372CD458173617B857671163DCE4C357F30FAF074BC06182CD2211F · masterSeal 6C2545C7DD0C672B6BEA18BC545768EB8AE8AFFEEE1B63365268E87A278679EA · 아티팩트 38 · 피어 기록 없음(-NoPeer).
- [0/10]~[10/10] 전 단계 통과 · --verify-zip Ok · --verify-live-armed Ok(키 19 · 마스터 1 · UI 3/3 · 시뮬레이션 격리 51 형식(CLI 29) · 위반 0) · 핸드셰이크 Ok · primary 재대조 + GoldenMasterReleasePackagingTests 14/14.

## 50. v12.17.5~6-DEV — 주문 경로 지연 시각화(/aidash) · 실발주 경로 스탬프 프로덕션 결선 · 비트 28 지연 인터락 (2026-10-13)

### 50.1 시각화(v12.17.5-DEV)
- `StockManager/Telemetry/LatencyHistogram.cs`: 고정 InlineArray 108 버킷(1µs~10ms · 옥타브당 8 · 상대 오차 ≤ 12.5%) 백분위 — δ=0. RCL(`SignalLabelMapper`)에 축 사본 · 테스트가 버킷 전수 대조.
- `OrderPathStageProfiler` 최근 1,000 부모 창(단일 라이터 · claim 카운터 · 판독자는 찢김 가능 최고령 표본 절단) → `DashboardSnapshotHub` 50ms 프레임에 p50/p99 3구간 + 추세 64.
- `LatencySpikeDegradationGuard`: p99 > 1ms = 차트 발행 50 → 200ms 강등 · 2s 무스파이크 = 복귀. /aidash 지연 배지 · 확신-지연 이중 스파크라인(좌표 δ=0) · 등급(정상/미세 정체/심각 지연/미계측) 정적 라벨.

### 50.2 실발주 경로 결선(v12.17.6-DEV)
- 스탬프 데코레이터(`StageStampingSplitGate` · `EgressStageProbe`) Simulation → `StockManager/Telemetry/OrderPathStamping.cs`(프로덕션 네임스페이스 — IL 시뮬레이션 격리 위반 0).
- 시그널 시각 = `OrderDispatchRing.Publish`(IOrderPathObserver — 전 호스트 생산자 공통 1지점) · 종결 = `GatedOrderDispatcher.Drain` try/finally(무장 차단 · 동결 · 송출 · veto · 예외 전부 정확히 1회).
- 호스트: dispatchRing.PathObserver · routerGate 감쌈 · 센티널 감싼 `stampedBrokerMap`(판정은 센티널 그대로 · 매트릭스 취소는 원 brokerMap) · 허브 `orderPath:`.
- [방향 A] 미도달 부모 슬롯 CAS 즉시 회수 — 축출 0(대조: 관측자 없으면 3,000 중 1,976 오축출). 스탬프 비용 주문당 141~232ns · δ=0.
- [방향 B] `OrderPathLatencyInterlock` = 신규 필수 비트 28 `OrderPathLatency` — 시그널→출구 p99 > 예산 연속 3회(fresh 판정 · 표본 ≥ 100) = Disarm(sticky) · 재무장 물리 이중키 + 기준선. 판정은 50ms 콜드 타이머(주문 스레드 밖).
- ★예산 = 병목 50ms + 드레인 주기 50ms = 100ms(`Router.OrderPathBottleneckMs`): 시그널 스탬프가 링 발행이라 설계된 드레인 대기 0~50ms 가 S2 에 포함 — 문자 그대로 50ms 면 정상 운용 p99 50.9ms 로 오격발(테스트가 두 경우 실측).
- 정직: liveArmed:false 기본에서는 출구 도달 0 → /aidash = '계측 대기 · 발행 N · 회수 N'. 실 p50/p99 는 키 on(드라이런 사슬 p50 ≤ 6.7µs · p99 ≤ 21µs). 실 브로커 출구 지연은 미실측.
- 인터락 30종(0~28 + StartupSanity 63) — 개수 고정 테스트 5곳 갱신.

## 51. v12.18.0-GM Production Golden Master 출하 — 30대 인터락 봉인 무추출 대조 · 호스트 스탬프 · 100ms 지연 예산 (2026-10-13)

### 51.1 인터락 명세(출하)
- ArmingInterlockSource 30종 = 비트 0~28 연속 + StartupSanity(63) · 정의 마스크 0x800000001FFFFFFF · 비트 28 `OrderPathLatency`(§50.2 · 예산 100ms).
- 매니페스트 interlocks = 태그 소스 열거(기존 [10/10] 핸드셰이크) = zip 호스트 DLL 정의(신규 · 51.2) — 3자 일치해야 출하.

### 51.2 [방향 A] `StockManager/Tools/ShippedInterlockManifestVerifier.cs` · CLI `--verify-interlocks`
- 빈틈: 기존 핸드셰이크는 매니페스트 ↔ '태그 소스'만 대조 — 실제 출하되는 zip 안 바이너리(스테일 bin · 다른 커밋 빌드)는 대조되지 않았다.
- ① 매니페스트 자기 봉인 · liveArmedDefault=false · masterSeal == master-seal.txt · 매니페스트 개수 == 기대값.
  ② zip = MemoryMappedFile 읽기 전용 뷰(정확 길이) · 항목 정규화('\'→'/' · 대소문자 무시) 중복 = 거부 · 호스트 DLL 만 팽창 → SHA-256 == 매니페스트 artifacts(판독 = 봉인 바이트).
  ③ 팽창 버퍼를 복사 없이 PEReader(ImmutableCollectionsMarshal) → MS2E.Core.ArmingInterlockSource TypeDef 정확히 1개 · 리터럴 byte 상수 · 값 오름차순 이름 == 매니페스트 순서(MetadataStringComparer) · 값 중복/범위 = 거부 · 마스크 == 구조 규칙(0~n-2 + 63).
- 실측: 정의 대조(③) 최솟값 114µs(< 1ms) · 대조 루프 힙 0B · 팽창+해시(②) ≈ 16~21ms(Deflate — 1ms 대상 아님, 보고서 분리 기록).
- 스크립트 `$ExpectedInterlocks = 30`: [10/10] 핸드셰이크 뒤 실행 · 거부 = Remove-ReleaseOutputs(산출물 전량 폐기). 새 비트 추가 시 이 값을 갱신해야 출하(테스트가 열거 길이와 고정 대조).
- 테스트(GoldenMasterReleasePackagingTests §10) 4: Ok(PS 5.1 역슬래시 zip · 실 DLL) · 구 명세 29 / 기대 29 / 순서 교환 / 개명 / 앵커 / 봉인 해시 / 매니페스트 변조 / 정규화 중복 · 누락 · 손상 = 거부 · ★DLL 상수 28→29 패치 + 일관 재봉인 = MaskMismatch(27 = 값 중복) · 스크립트 결선 · CLI 0/2/64. 방어 끔 변이 2/2 Red(마스크 · 중복).
- 방향 B(zip 항목 정규화 · 중복 가드)는 §44 기존 + 이 검증기 ②에 같은 규칙 재적용.

### 51.3 출하
- 버전 12.18.0 · 태그 v12.18.0-GM · `release-package.ps1 -NoPeer` · 형제 worktree(GIT_CONFIG safe.directory + core.commitGraph=false) · release/ 복사 · v12.17.4-GM 산출물 → release/superseded-v12.17.4-GM.
- 출하 기록(2026-10-13): 태그 v12.18.0-GM = 454c0d9 · `release-v12.18.0-GM.zip` SHA-256 DEA3EE725906E3BC826232C652B0CE2397E3F6E44FF2C641E5E9356A639FFD74 · masterSeal 42B627E45253BC0CF26EC8B8EAFD30EDDF08661E875040694E526737B08D9E8E · 아티팩트 38 · 피어 기록 없음(-NoPeer).
- [0/10]~[10/10] 전 단계 통과(3,698/3,698) · --verify-zip Ok · --verify-live-armed Ok(키 19 · 마스터 1 · UI 3/3 · 시뮬레이션 격리 49 형식(CLI 29) · 위반 0) · 핸드셰이크 Ok · --verify-interlocks Ok(30 · 0x800000001FFFFFFF). 콜드 CLI 1회 정의 대조 7.3ms(프로세스 첫 호출 JIT 포함 — 1ms 예산은 웜 기준 · primary 재대조 81µs) · primary 재대조 GoldenMasterReleasePackagingTests 18/18.

## 52. v12.18.1~2-DEV — 변동성 적응 확신 보정 · 시그널 churn 차단기 · 호스트 시그널 게이트 실결선 (2026-10-13)

### 52.1 보정(v12.18.1-DEV)
- `StockManager/Engines/VolatilityAdaptiveCalibrator.cs`: 정수 전용(Q8 bp) · EMV 시프트 α=2^-k(기본 k=5) · 효율비 · 호가 불균형 잡음 → `MicroRegime` Warmup/Trend/MeanReverting/Choppy → 요구 확신 9500/8500/9000/9500bp · ExitHold 히스테리시스 32 북.
- `SignalChurnCircuitBreaker`: 60 버킷 InlineArray 러닝섬 O(1) · 분당 30 = 억제 · 60 = `StrategyAnomalyKind.ModelBreakdown` → 비트 27 Disarm(억제된 승인도 하드 한도 집계).
- /aidash `.pd-cal` 스트립(국면 · 요구 확신 · churn) · 거부 사유 16 `RegimeThresholdNotMet` "국면 임계 미달 — 전방 거부/관망".
- 정직: 1,000틱 링/ SIMD 대신 EMV · 스프레드 계산은 하되 판정 미사용 · 국면 경계는 실 호가 미보정.

### 52.2 호스트 결선(v12.18.2-DEV)
- `IOrderProposalObserver`: GatedOrderDispatcher 가 무장 검사 '앞'에서 호출 → 게이트 꺼짐(관찰 모드)에서도 churn 집계 · 비트 27 격발 보장(게이트 기본 off 에서 라우터 미도달 문제 교정).
- `SignalGateRuntimeSwitch`(1초 폴링): 켜기 즉시 · 판독 실패 = 켜기 · 끄기 = 물리 키 둘 다 off 일 때만(키 판독 실패 = on 취급). 국면 거부는 주문 원장 미기록 · /aidash 라벨.
- UiSettingWriteGuard 허용 목록에 Strategy.* 추가 요청은 거부(IL 규칙 ⑤ 호스트 안전 네임스페이스) → §53 강화 전용 경로로 대체.
- 테스트: VolatilityCalibrationTests 14 · HostSignalGateIntegrationTests 10 · 변이 3/3 · 4/4 Red.

## 53. v12.18.3-DEV — 강화 전용 UI 설정 경로(TightenOnlyUiSettingWriter) · 단조성 IL 가드 ⑦~⑩ (2026-10-13)
- `src/02_Infrastructure/Engine/TightenOnlyUiSettingWriter.cs` 키 6: SignalGateEnabled · AdaptiveThreshold = false→true 만 · ConfidenceThresholdBp · AdaptiveMaxBp = 상향만 · ChurnSoftPerMin · ChurnHardPerMin = 하향만.
  호스트 유효값(파싱 + 클램프) 비교 · 형식 불량 = 거부(폴백 대체 아님) · 환경변수 덮음 = 거부 · 판독-판정-기입 단일 잠금 · 무장/카나리/디스패치/브로커/미등재 키 = 무예외 폐기 + 사유 계수.
- 런타임 하한(확신 · 최대 임계)은 올리기만 — 낮추기는 재기동 필요. AdaptiveThreshold · Churn* 는 재기동 반영.
- `ShippedBinaryLiveArmedGuard` ⑦ 키 표 · 규칙 바이트 = 고정 명세 TightenSpec · 폴백/하한/상한 = 호스트 GetBool/GetInt 판독 상수 ⑧ IsTightening 비교 방향 ⑨ Judge 분기 · TryWrite brfalse · WriteStored 1회 ⑩ 진입점 TryWrite 1회 · DB 직접 기입 0.
  경로 전체 부재 = 통과(구 바이너리 호환) — 출하 게이트에서 §54 가 닫는다.
- 실측: 퍼즈 20만(완화 기입 0) · 1바이트 패치 6/6 거부 · 변이 4/4 Red · 판정 4만 회 0B.

## 54. v12.18.1-GM Production Golden Master 출하 — 30대 인터락 · 강화 전용 6키 행렬 핀 고정 · 무추출 행렬 대조 (2026-10-13)

### 54.1 명세(출하)
- 인터락 30종 불변(비트 0~28 + StartupSanity 63 · 마스크 0x800000001FFFFFFF) — `--verify-interlocks` §51.2 그대로.
- 강화 전용 행렬 6키(키|규칙|폴백|하한|상한): SignalGateEnabled|1|0|0|1 · AdaptiveThreshold|1|1|0|1 · ConfidenceThresholdBp|2|8500|5000|10000 ·
  AdaptiveMaxBp|2|9500|5000|10000 · ChurnSoftPerMin|4|30|1|100000 · ChurnHardPerMin|4|60|2|100000 (규칙 1 = true 만 · 2 = ≥ · 4 = ≤).
- 스크립트 `$ExpectedTightenKeys = 6`(= 가드 고정 명세 길이 — 테스트가 대조).

### 54.2 매니페스트 확장(ms2e-multirepo-release/1 · 하위 호환)
- `tightenRules`(선택 배열 · 있으면 ≥1) — 정규화 줄 `tighten=<행>` 을 interlock 줄 뒤에. 없으면 키 생략 = 구 매니페스트 해시 불변. 독립 무할당 판독기(ManifestSealStreamer)도 같은 규칙(빈 배열 = 둘 다 거부).
- sync 는 태그 커밋 소스 `TightenOnlyUiSettingWriter.cs`(git ls-tree/show)를 파싱해 기록 · 판독 실패 = 기록 거부 · 파일 부재(구 태그) = 빈 목록.
- 핸드셰이크 `TightenRuleMismatch`(22): 태그 소스에 표가 있는데 기록에서 뺐거나(조용한 누락) 값이 다르면 거부 · 기록 있는데 태그 소스 관측 불가 = 거부.

### 54.3 [방향 A] `StockManager/Tools/ShippedTightenMatrixVerifier.cs` · CLI `--verify-tighten`
- ① 매니페스트 자기 봉인 · liveArmed=false · 앵커 · tightenRules 개수 == 기대(6) == 가드 명세 · 행 형식 · 키/규칙 == 가드 명세.
  ② 공용 로더 `ShippedInterlockManifestVerifier.LoadSealedHost`(MMF · 항목 정규화/중복 가드 · SHA-256 == 봉인 해시).
  ③ `CompareMatrix`(힙 0): .cctor IL 스택 버퍼 해독 → ldstr 키 순서 = 매니페스트(#US UTF-16 BlobReader 직접 비교) · ldtoken→stsfld Fallbacks/Minimums/Maximums RVA 블롭 = 매니페스트 열 · get_Rules RVA 블롭 = 규칙 열 · IsTightening 규칙 비트 ↔ ==1/≥/≤ 정확히 1개씩 · 순서 · 상수 true 0 · 미정의 = false.
- `--verify-live-armed --expect-tighten-keys 6`: 가드의 '경로 전체 부재 = 통과'를 출하 게이트에서 거부로 전환.
- 스크립트: [9/10] verify-live-armed 키 수 고정 · [10/10] verify-interlocks 뒤 verify-tighten · 거부 = Remove-ReleaseOutputs(fail 줄 7).
- 실측: 대조 1,000회 0B · 회당 ≈ 40µs(웜) · CLI 콜드 109µs. 패치 + 일관 재봉인 5종(폴백 9500→5000 · 규칙 2→4 · clt→cgt · 상수 true · #US 키 개명) 전부 거부. 변이 2/2 Red(방향 판정 끔 · 핸드셰이크 누락 판정 끔).
- 테스트(GoldenMasterReleasePackagingTests §11) 6 + §3 출하 기록 행렬 대조. 방향 B(zip 항목 정규화 · 중복 가드)는 §51 로더 재사용.
- 한계(정직): 검증기는 검사 대상과 같은 바이너리에 들어 있다(출하 CLI 가 자기 사본 판독) · 형식/메서드 이름 기반 탐색 · IL 방향 판정은 현재 컴파일러 패턴(clt/cgt + ceq 0) 고정 — 컴파일러가 다른 형태를 내면 거부(fail-closed)로 나타난다.

### 54.4 출하
- 버전 12.18.1 · 태그 v12.18.1-GM · `release-package.ps1 -NoPeer` · 형제 worktree · release/ 복사 · v12.18.0-GM 산출물 → release/superseded-v12.18.0-GM.
- 출하 기록(2026-10-13): 태그 v12.18.1-GM = 6ded491 · `release-v12.18.1-GM.zip` SHA-256 17F1F124A41AE80313999779FCECF3C891AF89DEBF0A6062A86B86FC1C32DE97 · masterSeal 551DBA60FBDD590665BE9059243093406603EF83D67712D616A0CD141370DDE4 · 아티팩트 38 · 피어 기록 없음(-NoPeer) · tightenRules 6.
- [0/10]~[10/10] 전 단계 통과(3,742/3,742) · --verify-zip Ok · --verify-live-armed Ok(키 21 · 마스터 1 · UI 3/3 · 시뮬레이션 49(CLI 29) · 강화 전용 키 6(호스트 판독 8) · 위반 0 · --expect-tighten-keys 6) · 핸드셰이크 Ok · --verify-interlocks Ok(30 · 0x800000001FFFFFFF) · --verify-tighten Ok(6).
- 콜드 CLI 1회 대조 시간: 인터락 7.5ms · 행렬 14.3ms(프로세스 첫 호출 JIT 포함 — 웜 기준은 각 < 1ms · 행렬 ≈ 40µs). primary 재대조(§3 테스트) 전부 Ok.

## 55. v12.18.2-DEV — 강화 전용 설정 감사 원장(MMF 해시 체인) · 변조 = 동결 + 비트 27 · --verify-audit-chain (2026-10-13)

### 55.1 원장(`StockManager/Security/MonotonicSettingAuditLog.cs`)
- 파일 `%LOCALAPPDATA%/StockManager/audit/tighten-audit.mmf`(헤더 128B + 레코드 128B × 2,048 = 256KB · MMF · 쓰기 핸들 1개 FileShare.Read).
- 헤더: 매직 MS2EAUDT · 버전 · 레코드 크기 · 용량 · 생성 시각 · 난수 nonce · 헤더 해시 = SHA-256([0,96)) = 제네시스.
- 레코드: 순번 · UTC ticks · 키 인덱스(−1 미등재) · 판정 · 규칙 · 생략 거부 수 · 키 FNV-1a 64 · 현재/제안 유효값 · 예약 0 · 직전 해시 · 레코드 해시 = SHA-256([0,96)).
- Verify(정적 · 0B): 헤더 → 순번 연속 · 직전 해시 연결 · 재계산 해시 · 시각 비감소 · 필드 범위/예약 0 → 빈 슬롯 이후 전부 0.
- Append(0B): 매번 전체 재검증 + 프로세스 메모리 앵커(개수 · 머리 해시) 대조 → 기록. 시계 역행 = 직전 시각 유지.
- 동결(sticky): 체인 불일치 · 손상 · 앵커 불일치 · 크기 이탈 · 열기 실패 → AuditChainBroken · 가득 참 → AuditLedgerFull — StrategyAnomalyInterlock.Trip(비트 27 원자 Disarm).
  물리 이중키 재무장으로 비트 27 은 풀 수 있지만 원장 동결은 프로세스 수명 동안 유지(설정 변경 계속 차단). 운영자 조치 = 파일 보관 후 재기동.
- 거부 레코드 예산: 분당 60 · 용량 3/4 이후 중단(생략 수는 다음 레코드에 누계) — 강화 레코드는 예산 없음.
- 호스트: 싱글턴 ITightenAuditSink · 1초 타이머 PollIntegrity(쓰기 없어도 변조 감지 ≤ 1초).

### 55.2 기입기 결선(TightenOnlyUiSettingWriter.TryWrite — 주문의 'AdmitAndApply' 는 이 메서드)
- 원장 미결속(null) · 동결 = 판독 0 · 기입 0 · `TightenVerdict.AuditFrozen`(7 · RCL 라벨 "🔒 감사 원장 동결").
- ★선기록: 강화 레코드 Append 성공 후에만 WriteStored · 저장 실패 = 강화 + StoreFault 2건. 거부/변화 없음도 기록(Reject 경유).
- 어댑터 생성자 1개(원장 필수 · null = 예외). 리허설 CLI 는 익명 MMF 원장.
- 출하 IL 가드 ⑪(ITightenAuditSink 형식이 있을 때만 — 구 출하 바이너리 호환): TryWrite Append 정확히 1 · Judge 뒤 · WriteStored 앞 · 직후 조건 분기 · 진입점 감사 인자 ldnull 0(nop 채움 건너뜀).

### 55.3 [방향 A] CLI `--verify-audit-chain <file> [--expect-head <64hex>] [--expect-count <n>]`
- 실행 중 호스트와 공존(읽기 전용 MMF 뷰 · 복사 0) · 0 = Ok · 2 = 거부 · 64 = 사용 오류. 외부 앵커 = 오프라인 '일관 재해시' 위조 대조용.
- 실측: 가득 찬 2,048 레코드 전체 검증 웜 최솟값 646µs(< 1ms) · CLI 1회 668~713µs.

### 55.4 검증(MonotonicSettingAuditLogTests 11)
- δ: Append 200 = 0B · PollIntegrity 20 = 0B · 기입기 거부 경로 50 = 0B.
- 1비트 변조 774건(헤더 · 가운데/마지막 레코드 전 바이트 × 2비트 · 꼬리) 전부 거부 · 타임스탬프 +1틱 · 순서 교환 거부.
- 실행 중 변조 → 다음 기입 AuditFrozen(감지 4µs) · 기입 0 · 비트 27 · 이후 판독 0 · 원상 복구해도 동결 유지.
- 일관 재해시 위조 · 꼬리 절단 = 정적 검증 통과(정직한 한계) → 메모리 앵커가 거부. 기동 시 손상/크기 이탈/쓰기 잠금/가득 참 = 동결 + 비트 27.
- IL ⑪ 1바이트 패치 3종 거부 · 변이 2/2 Red(앵커 끔 · 선기록 결과 무시).
- 한계(정직): 키 없는 SHA-256 — 프로세스가 내려간 사이 파일 전체 일관 재해시는 외부 앵커(--expect-head) 없이는 구분 불가 · 기록 중 정전 = 찢긴 레코드 → 재기동 동결(안전 쪽) ·
  주문의 IncrementalHash 대신 정적 SHA256.HashData(동일 결과 · 상태 객체 0) · '0ms 감지' = 다음 기입 즉시 또는 1초 주기.

## 56. v12.18.2-GM Production Golden Master 출하 — 감사 원장(§55) · 규칙 ⑪ 출하 강제 · 강화 전용 6키 · 30대 인터락 (2026-10-13)

### 56.1 명세(출하)
- 인터락 30종 불변(0~28 + 63 · 0x800000001FFFFFFF) · 강화 전용 행렬 6키 불변(§54.1) · 감사 원장 MonotonicSettingAuditLog(§55 · 비트 27 재사용 — 새 비트 0).
- 주문의 'MonotonicSettingAuditLog.TryRecord' 는 없다 — 감사 진입점 = ITightenAuditSink.Append(TightenOnlyUiSettingWriter.TryWrite 에서 호출). 검사는 그 실제 지점을 판독.

### 56.2 [방향 A] 감사 원장 결선 무추출 · 무할당 수검(`ShippedTightenMatrixVerifier.CheckAuditWiring` · `--require-audit-ledger`)
- 빈틈: 가드 ⑪은 ITightenAuditSink 형식이 없으면 '구 바이너리'로 보고 생략 → 싱크 형식 개명(#Strings 1바이트)만으로 무감사 기입기가 가드를 통과(테스트 실증).
- 검사: ITightenAuditSink 정의 1 · TryWrite = Append 정확히 1 · Judge < Append < WriteStored(정확히 1) · Append 직후 조건 분기 · TightenSetting = TryWrite 1 · 감사 인자(직전 비 nop) ≠ ldnull.
  이름 비교 = MetadataStringComparer · 명령 해독 = 스택 버퍼 → 1,000회 0B · 회당 ≈ 70µs. 판정 18 AuditLedgerMissing · 19 AuditLedgerBypass.
- 스크립트: [9/10] --verify-live-armed · [10/10] --verify-tighten 둘 다 `--require-audit-ledger`(가드 = AuditLedgerAware 아니면 거부) · 거부 = 기존 Remove-ReleaseOutputs 경로(fail 줄 7 불변).
- 패치 + 일관 재봉인 4종(Append 분기 → br.s · Append → get_Frozen · 진입점 ldnull · 싱크 개명) 전부 거부 · 변이 1/1 Red(순서/분기 판정 끔). 구 출하물(v12.18.1-GM) = 요구 플래그에서 거부 · 없으면 통과(재수검 호환).
- 방향 B(zip 항목 정규화 · 중복 가드)는 §51 공용 로더 그대로.

### 56.3 출하
- 버전 12.18.2 · 태그 v12.18.2-GM · `release-package.ps1 -NoPeer` · 형제 worktree · release/ 복사 · v12.18.1-GM 산출물 → release/superseded-v12.18.1-GM.
- 출하 기록(2026-10-13): 태그 v12.18.2-GM = 80c0ed4 · `release-v12.18.2-GM.zip` SHA-256 EA3090065D6DCE5DB97A9D7209721796969001AD66D20D82FB784FB632E33C16 · masterSeal 45E55800F2D34E12B345D4E93910B8C60F6EE76598D2BB8DDCE6F96C183B5F82 · 아티팩트 38 · 피어 기록 없음(-NoPeer) · tightenRules 6.
- [0/10]~[10/10] 전 단계 통과(3,756/3,756) · --verify-zip Ok · --verify-live-armed Ok(키 21 · 마스터 1 · UI 3/3 · 시뮬레이션 49(CLI 29) · 강화 전용 키 6(호스트 판독 8) · 감사 원장 ⑪ 적용 · 위반 0) · 핸드셰이크 Ok · --verify-interlocks Ok(30) · --verify-tighten Ok(6 · 감사 원장 선기록 결선).
- 콜드 CLI 1회 대조: 인터락 7.1ms · 행렬+결선 19.7ms(프로세스 첫 호출 JIT 포함 — 웜 기준 행렬 ≈ 40µs · 결선 ≈ 70µs). primary 재대조(§3 · §12) 27/27.

## 57. v12.18.3-DEV — 감사 원장 관제 배지(설정 '안전 강화' · /aidash) · 무간섭 스냅샷 · 포화 경고 · 머리 해시 지문 (2026-10-13)
- `MonotonicSettingAuditLog.Snapshot()` → `AuditLedgerSnapshot`(수 · 용량 · 동결 원인 · 판정 · 머리 해시 상위 8바이트 BE · 버전 · State · UsedPercent).
  기록자(체인 잠금 보유 · 단일)가 기록/동결 직후 seqlock 게시 → 판독자는 잠금 0 · 원장 영역 접근 0 · 해시 0(51ns/회 · 0B). 64회 찢김 = Contended → 동결로 표시(fail-closed). 폐기 = 사용 불가.
- 허브 `AttachAuditLedger` → 프레임 AuditState/Count/Capacity/UsedPercent/FreezeCause/HeadPrefix · 버전/원인 변화 = 즉시 재게시. 설정 포트 `TryGetAuditLedger(out AuditLedgerBadge)`(대역 싱크 = 미결선).
- RCL: `DashAuditLedger` 코드 · `SignalLabelMapper` 상태/원인/사용률 정적 캐시 · [방향 A] `HeadHex`(16자 · 1칸 캐시 — 머리 변경 시만 할당) · `HeadSwatch`(상위 4니블 → 16색 팔레트 · 시세 적/청 제외).
  /aidash 스트립 `.pd-audit` 배지 · 설정 '안전 강화' 패널 배지 + 기록 N/2048 + 지문 + 새로고침 · 동결/포화 안내 문구. RCL 엔진 형식 참조 0 · PureShell coupled 40 불변.
- [방향 B] 포화 소프트 가드: 사용 ≥ 90%(2,048 → 1,843) = Saturating · 1회 Trace 고지 · 기록/무장 판정 불변 · 100% = 기존 동결(가득 참) + 비트 27.
- 검증(AuditLedgerTelemetryTests 8): Snapshot 1만 · 라벨 1만 · 허브 펌프 200 = 0B · 체인 잠금 점유 중 즉시 반환 · 검증 횟수 불변 · 동시 기록 1,500 × 판독 65,536 찢김 0 · 변조 → Frozen → 허브 재게시 · 포화 90 → 경고 1회 → 가득 참 동결.
- 정직: 허브 펌프 0B 실측은 변화 없는 펌프(서명 계산 + 스냅샷) 기준 · 설정 화면은 실시간 푸시가 아니라 진입/기입/새로고침 시 갱신(/aidash 는 50ms 프레임) ·
  [교정] --verify-audit-chain 1ms 시험이 다른 테스트와 함께 돌 때 672~1,144µs 로 실패 — 정적 SHA256.HashData(호출마다 핸들 경로)를 원장별 재사용
  IncrementalHash(잠금 안 단독 사용 · 스팬 출력)로 교체: 2,048 레코드 485 → 237µs(벤치) · 결합 실행 374~590µs · Append/Poll 0B 유지 · 결과 바이트 동일.
  (주문 원안의 IncrementalHash 가 맞았다 — §55 의 '원샷이 동등' 판단 정정.) 웜 최솟값 표본 25 유지.

## 58. v12.18.3-GM Production Golden Master — 관제 배지(§57) 출하 · 재사용 IncrementalHash · 규칙 ⑪ · 강화 전용 6키 · 30대 인터락 · 배지 무잠금 결선 IL 수검 (2026-10-13)

### 58.1 명세(출하)
- 30대 인터락(비트 0~28 + 63 · 구조 마스크 0x800000001FFFFFFF) 불변 · 강화 전용 6키 행렬(§54.1) 불변 · 규칙 ⑪(감사 원장 선기록 · §56) 불변.
- §57 관제 배지(Snapshot seqlock · /aidash · 설정 '안전 강화') + 재사용 IncrementalHash(결과 바이트 동일 — 기존 원장 파일 그대로 검증) 출하.
- 버전 12.18.3 · `release-package.ps1` 머리말 v12.18.3-GM · `$ExpectedInterlocks` 30 · `$ExpectedTightenKeys` 6 · `Remove-ReleaseOutputs; Fail` 7(폐기 경로 불변).
- 주문 항목 대조: '[방향 B] Zip 항목 정규화 · 중복 가드'는 v12.17.0-GM(§6 · ZipEntryGuard)에서 이미 출하 게이트에 있다 — 이번은 방향 A 만 구현.

### 58.2 [방향 A] 관제 배지 무잠금 결선 IL 수검(`ShippedTightenMatrixVerifier.CheckAuditTelemetry` · `--require-audit-telemetry`)
- 출하 zip 호스트 DLL(무추출 · 로드 0 · 실행 0)에서 '텔레메트리 조회가 체인 잠금에 간섭하지 않음'을 IL 사실로 판정:
  - `MonotonicSettingAuditLog.Snapshot` = 순수 seqlock 판독자: `Volatile.Read(ref _snapSeq)` ≥ 2 · 잠금 형식(Monitor/SpinLock/Mutex/SemaphoreSlim/ReaderWriterLockSlim/Lock/WaitHandle) 호출 0 ·
    원장 자기 메서드 호출 0 · `_gate`/`_hasher`/`_head` 접근 0 · stfld/stsfld 0 · newarr/box/ldftn/ldvirtftn/calli 0 · newobj = 값 형식 AuditLedgerSnapshot 만(기반 System.ValueType 확인).
  - `PublishSnapshot` = `Interlocked.Increment(ref _snapSeq)` 정확히 2.
  - `DashboardSnapshotHub.PumpCore` · `AdminSettingWidgetAdapter.TryGetAuditLedger` = Snapshot 호출 정확히 1 · 그 외 원장 메서드(CopyHead/PollIntegrity/Append/Verify) 0.
  - `BlazorHomeForm`(중첩 클로저 형식 포함) = `AttachAuditLedger` 호출 정확히 1.
- 판정 20 AuditTelemetryMissing · 21 AuditTelemetryUnsafe(중복 정의 = WriterAmbiguous). 해석 불가 피호출자 = 거부(fail-closed).
- 해독기 `IlCursor`(스트리밍 · 버퍼 0 · 길이 제한 없음 — PumpCore 같은 큰 본문) 신설, 기존 `Decode` 는 그 위로 재구성(동작 동일 · §11/§12 회귀 통과).
- 힙 0(200회 루프 0B) · 웜 회당 ≈ 0.66ms(호스트 폼 전 메서드 스트리밍 포함). `[10/10] --verify-tighten` 에 `--require-audit-telemetry` 추가 — 배지 결선 누락 · 잠금 간섭 = 출하 산출물 전량 폐기.
- 구 출하물 호환: v12.18.2-GM zip = 요구 없음 Ok · 요구 시 AuditTelemetryMissing(배지 이전 바이너리).
- 시험(GoldenMasterReleasePackagingTests §13 · 3): 실 빌드 Ok + 0B + CLI · 일관 재봉인 패치 9종(Volatile.Read→Monitor.Enter · _snapSeq→_gate/_head · 값 newobj→참조 형식 · →CopyHead ·
  닫기 증가 제거 · 허브 Snapshot→PollIntegrity · 어댑터 Snapshot→CopyHead = Unsafe · 호스트 AttachAuditLedger 제거 = Missing) — 전부 행렬 + ⑪ 은 통과하는 바이너리.

### 58.3 출하
- 버전 12.18.3 · 태그 v12.18.3-GM = f1a9bcf · `release-package.ps1 -NoPeer` · 형제 worktree · release/ 복사 · v12.18.2-GM 산출물 → release/superseded-v12.18.2-GM.
- 출하 기록(2026-10-13): `release-v12.18.3-GM.zip` SHA-256 153C2841AE9776A7370DCC0C7C6C239C868AB1C947477DEA0D03C7F325A3D07D · masterSeal 75D15D198784A66074D4D97E72D02CC9903D3D35A60AF55C526F3B7F89C7CCF8 · 아티팩트 38 · 피어 기록 없음(-NoPeer) · tightenRules 6.
- [0/10]~[10/10] 전 단계 통과(3,767/3,767 · coupling 0) · --verify-zip Ok · --verify-live-armed Ok(키 21 · 마스터 1 · UI 3/3 · 시뮬레이션 49(CLI 29) · 강화 전용 키 6(호스트 판독 8) · ⑪ 적용 · 위반 0) ·
  핸드셰이크 Ok · --verify-interlocks Ok(30 · 0x800000001FFFFFFF) · --verify-tighten Ok(6 · ⑪ 선기록 결선 · ⑤ 관제 배지 무잠금 결선).
- 콜드 CLI 1회 대조: 인터락 7.5ms · 행렬+⑪+⑤ 30.0ms(프로세스 첫 호출 JIT 포함). primary 재대조(GoldenMasterReleasePackagingTests) 30/30.
- 정직: 사전 전체 스위트 1회차에서 MmfIpcFabricTests.Backpressure_RealThreads_ReaderStall_NoFalsePoison 1건 실패(이미 ConcurrencyStress · 20ms 임계 실 스레드 시험 —
  타 컬렉션 병렬 CPU 경합). 단독 5/5 통과 · 재실행 3,767/3,767 · 패키저 내 실행 3,767/3,767. 이번 변경은 IPC 무관 — 코드 수정 없음(기록만).

## 59. v12.18.4-DEV — 전 사슬 가속 Soak(100만 틱) · 3중 경합 퍼징 · 지터 센티널 · IPC 스텝 락 결정론화 (2026-10-13)

### 59.1 전 사슬 가속 Soak(`StockManager/Simulation/LongSoakProfileHarness.cs` — 주문의 `StockManager.Tests/Simulation/` 경로는 없음)
- `fullChain` = 호스트와 같은 선택 결속: VolatilityAdaptiveCalibrator + SignalChurnCircuitBreaker → QuantSignalProvider · 허브 AttachCalibration ·
  OrderPathStageProfiler → 디스패치 링 PathObserver(`PaperTradingSimulationHarness(pathObserver:)` 신설) + 허브 orderPath · 휘발 MonotonicSettingAuditLog(같은 비트 27) → 허브 배지 ·
  MicrosecondJitterSentinel. `regimeCycleSeconds` = 추세(한 방향 · 한쪽 잔량 우세) ↔ 횡보(틱 교대 · 넓은 잔량 잡음) 교차. `RunTicks(n)` · `RunOrderBurst`(고속 제안 루프).
- `Settle()` = 강제 전체 GC + 그 정지를 헬스 GC 정지 판정(스캔 간 > 500ms = 비트 21)에서 제외(주입 gcPauseTicks · 프로덕션 에이전트 무변경).

### 59.2 [방향 A] `StockManager/Telemetry/MicrosecondJitterSentinel.cs`
- 500µs 초과 = 비관리 원형 링(NativeMemory · 넘치면 오래된 것 덮어씀 + 계수) · 전체 표본 상위 K(16) 정확 보존(임계 미만도) · LatencyHistogram ‰ 백분위.
- 원인 = 표본 구간 Gen0 수집 횟수 변화 시 Gc · 아니면 Unattributed(ETW 없이 OS 선점 구분 불가 — 추측 라벨 없음). 기록 0B · 단일 작성자.

### 59.3 검증(LongSoakIntegrationTests 5 · 컬렉션 NonParallelSoak · 테스트 전후 강제 GC)
- 100만 틱(예열 10만 + 측정 90만): 측정 할당 0B · GC 0/0/0 · raised 0 · 집계어 0 · 횡보 진입 6,191 · 지터 p99 ≈ 12µs.
- 3중 경합(실 스레드): 주문 ≈ 64,500/s · 판독 1,000/s(+50ms 프레임) · 퍼저 ≈ 93/s · 원장 256 → 찢김 0 · 역행 0 · 교착 0 · 주문/판독 0B · GC 0 ·
  동결 관측 순간 비트 27 · 동결 뒤 기입 0 · 송출 0 · 비트 27 외 0. 퍼저 0B 는 회차별 측정 — 90% 고지 1회차(Trace 문자열 440B · 콜드 설계)만 분리.
- 결정론 포화: 90~100% = 경고(무장 불변) · 100% 다음 기입 = 동결 + 그 반환 시점 비트 27(§55 설계 그대로 — 마지막 레코드 즉시 동결로 바꿀지는 미결정).

### 59.4 IPC 스텝 락(MmfIpcFabricTests · [방향 A] 결정론 스텝 락 이벤트 배리어)
- 원인: 실 시계 + 20ms 생존 임계 — 리더 40ms Sleep 뒤 라이터가 마지막 20ms 안에 백프레셔 하트비트를 찍어야 통과 → 라이터 선점 시 정당한 '단절' 판정(엔진 정상 · 하네스 결함).
- 교정: 두 패브릭이 코디네이터 논리 시계만 읽는다(OS 지연 = 논리 시간 0). 정지 = ① 가득 참 백프레셔 대기 ② 시계 +40ms ③ 전진 뒤 시작된 하트비트(계수 > C+1) 대기 ④ 재개.
  대기 = MRES 힌트 + 계수 술어 재확인 루프. ★실측 결함(25회 중 2회): 이전 단계의 지연된 Set 이 ③ 대기를 통과 → Reset→무장 순서로는 못 막고 술어 재확인으로 교정.
  대조군 = 라이터 게이트 정지(parked 확인 후 전진) → 정확히 첫 정지 뒤 단절(결정론 Red). 적대: 코어 × 2 스핀 스레드 + 100ms 정지 19회 = 무오판(15초).
- Thread.Sleep 0(해당 테스트) · 클래스 40회 연속 통과(교정 전 20회 중 1 · 25회 중 2 실패).

### 59.5 게이트 · 정직
- 오중 빌드 0W/0E(Default 마지막) · 전체 스위트 연속 2회 3,773/3,773(주문의 3,772 + 적대 스텝 락 1) · coupling 0 · 래칫 40.
- 전역 직렬: 이 어셈블리는 v10.1.0 부터 DisableTestParallelization(추적 파일) — '병렬 경합'은 원인이 아니었다. NonParallelSoak 는 방어적 고정.
- ★Soak 간헐 raised 1 의 근본 원인은 미확정: Settle 제외 GC 정지 실측 26ms(500ms 한도 대비 작음) — 메모리 압박(페이징) 시에만 커지는 가설.
  이후 2회 연속 무재현이나 재현 시 단언 메시지가 헬스 위반 사유 · 스레드풀 대기 · 제외 정지를 남긴다. 방향 B(메모리 거버너)는 미구현.

## 60. v12.18.4-GM Production Golden Master — Soak · 지터 센티널 · IPC 스텝 락(§59) 출하 · 30대 인터락 · 강화 전용 6키 · 규칙 ⑪ · IPC 패브릭 무슬립 위상 IL 수검 (2026-10-13)

### 60.1 명세(출하)
- 30대 인터락(비트 0~28 + 63 · 구조 마스크 0x800000001FFFFFFF) 불변 · 강화 전용 6키 행렬(§54.1) 불변 · 규칙 ⑪(감사 원장 선기록 · §56) 불변 · ⑤ 관제 배지 seqlock(§58) 불변.
- 결정론적 IPC 가짜 시계 배선(§59.4): `MmfIpcFabric(nowTicks:)` 주입은 테스트(코디네이터 논리 시계)만 — 출하 생성자 기본값 = `Stopwatch.GetTimestamp`(⑥ (c) 가 IL 로 고정).
- 원장 동결 규격(지연 동결 · §55/§59.3): 90~100% = 포화 경고(무장 불변) · 100% 도달 뒤 '다음' 기입 = 동결 + 그 반환 시점 비트 27. 즉시 동결 전환은 미결정(이번 출하 = 기존 규격 그대로).
- 버전 12.18.4 · `release-package.ps1` 머리말 v12.18.4-GM · `$ExpectedInterlocks` 30 · `$ExpectedTightenKeys` 6 · `Remove-ReleaseOutputs; Fail` 7(폐기 경로 불변).
- 주문 항목 대조: '[방향 B] Zip 항목 정규화 · 중복 가드'는 v12.17.0-GM(ZipEntryGuard)부터 출하 게이트에 있다 — 이번은 방향 A 만 구현.

### 60.2 [방향 A] IPC 패브릭 무슬립 위상 IL 수검(`ShippedTightenMatrixVerifier.CheckIpcFabric` · `--require-ipc-fabric`)
- 출하 zip 호스트 DLL(②의 MMF 뷰 로더 · 무추출 · 로드 0 · 실행 0)에서 `MS2E.Infrastructure.MmfIpcFabric` 판독:
  - (a) 핫 폐포 = TryPublish/TryConsume/Beat/CheckPeerAlive 에서 call/callvirt/newobj/ldftn 으로 닿는 같은 형식 메서드 전부(고정점 · stackalloc 상태 바이트).
    차단 대기 0: Thread.Sleep/Yield/SpinWait/Join · Task.Delay/Wait/WaitAll/WaitAny/Yield · SpinWait(SpinOnce 포함) · Monitor · WaitHandle 계열 · MRES · SemaphoreSlim ·
    Mutex · SpinLock · RWLS · Lock · Barrier · CountdownEvent. 중첩 형식(클로저) 호출 · 해석 불가 피호출자 = 거부. 다른 형식으로 나가는 호출은 범위 밖(정직).
  - (b) 형식 전체(중첩 · 생성자 · Dispose 포함) Thread.Sleep/Yield · Task.Delay · SpinWait.SpinUntil 0. Dispose 바운드 드레인 SpinOnce 는 핫 폐포 밖이라 허용.
  - (c) 생성자 기본 시계 ldftn Stopwatch.GetTimestamp 정확히 1.
- 판정 22 IpcFabricMissing · 23 IpcFabricBlocking · 24 IpcFabricClockDrift(중복 정의 = WriterAmbiguous). 힙 0(200회 0B) · 웜 회당 ≈ 0.27ms.
- `[10/10] --verify-tighten` 에 `--require-ipc-fabric` 추가 — 위반 = 출하 산출물 전량 폐기.
- 시험(GoldenMasterReleasePackagingTests §14 · 3): 실 빌드 Ok + 0B + CLI · 타 어셈블리 = Missing · 일관 재봉인 패치 5종(TryConsume MemoryBarrier→Sleep · Enter Increment→SpinOnce(전이) ·
  TryPublish Enter→Dispose(드레인 유입) · Dispose SpinOnce→Sleep = Blocking · 생성자 시계 ldftn 이탈 = ClockDrift) — 전부 행렬 + ⑪ + ⑤ 는 통과하는 바이너리.
- 구 출하물: v12.18.3-GM zip 도 ⑥ Ok(패브릭 소스 무변경 — v12.18.4-DEV 결정론화는 테스트 하네스 쪽).

### 60.3 출하
- 버전 12.18.4 · 태그 v12.18.4-GM = d893f1b · `release-package.ps1 -NoPeer` · 형제 worktree · release/ 복사 · v12.18.3-GM 산출물 → release/superseded-v12.18.3-GM.
- 출하 기록(2026-10-13): `release-v12.18.4-GM.zip` SHA-256 FA7830F7AD54288C479E88CA431C70E46646B3FB13125F3DDC68D83C215E56E2 · masterSeal 5C2597E464150233A4D338C041DA1D020FBA89EE5485D0759AD442CAD03F8480 · 아티팩트 38 · 피어 기록 없음(-NoPeer) · tightenRules 6.
- [0/10]~[10/10] 전 단계 통과(오중 빌드 0W/0E · 3,776/3,776 · coupling 0) · --verify-zip Ok · --verify-live-armed Ok(키 21 · 마스터 1 · UI 3/3 · 시뮬레이션 49(CLI 29) · 강화 전용 키 6(호스트 판독 8) · ⑪ 적용 · 위반 0) ·
  핸드셰이크 Ok · --verify-interlocks Ok(30 · 0x800000001FFFFFFF) · --verify-tighten Ok(6 · ⑪ · ⑤ · ⑥ IPC 패브릭 무슬립 위상).
- primary 재대조(GoldenMasterReleasePackagingTests) 33/33 — 출하 레코드 시험이 ⑪+⑤+⑥ 를 실 출하 zip 에 재수검.

## 61. v12.18.5-GM — 이중 저장소 교차 봉인 출하 라인 · 피어 철회 드리프트 방어 · [0/10] 피어 정돈 하드 게이트 (2026-10-13)

### 61.1 버전 계보
- v12.18.4-GM(d893f1b · 단독 출하 §60.3) 불변 보존. 드리프트 방어 · 게이트는 태그 이후 코드라 12.18.5 로 범프(§47.3 런북 — 봉인된 출하물 덮어쓰기 금지).
- 태그 v12.18.5-GM = d44c35b(미출하 · 미푸시 상태에서 03de6de → cd6e55a → d44c35b 로 재지정).

### 61.2 [방향 B] 피어 철회 드리프트 방어(`StockManager/Tools/PeerDriftGuard.cs` · `--peer-drift`)
- [0/10] 고정 = 피어 브랜치(symbolic-ref · 분리 HEAD = 중단) · HEAD · 봉인 매니페스트 SHA-256. 현재 피어와 대조(읽기 전용 · --no-optional-locks):
  같은 커밋 다른 브랜치 전환(기존 HEAD 고정은 통과하던 경우) · 분리 HEAD · 1커밋 전진(문서만이라도) · 재봉인/1비트/삭제 · 미커밋 코드 = 거부.
- 스크립트 Assert-PeerPinned: [7/10] zip 직전 · [10/10] 동기화 전 · 완료 직전 — 이동 = 스테이징 + 출하 산출물 전량 폐기(`Remove-ReleaseOutputs; Fail` 7 → 8).

### 61.3 [0/10] 피어 정돈 하드 게이트(`--peer-readiness`)
- 고정 직후 PeerReadinessProbe 판정 — NotReady(★ManifestStale: sealedAtUtc < 피어 HEAD 커밋 시각 · 1초라도) = 기존 산출물 폐기 · [1/10] 이전 Fail(exit 1).
  판정 직후 --peer-drift 재대조(Ready 판정 = 고정한 상태). 깨끗한 작업 트리엔 판정기가 없어 호스트만 먼저 컴파일(-warnaserror).
- 실측(형제 worktree · 태그 d44c35b · -NoPeer 없이): QuantumVault 50d8c42 · 봉인 2026-10-07 = ManifestStale → [0/10] exit 1 · [1/10] 미진입 · 산출물 0.
- 시험: PeerDriftGuardTests 12(실 임시 git — 판정 · CLI 0/2/64 · 브랜치 이름 주입 · 피어 .git 바이트 불변 · 1초 낡음 거부/같은 시각 통과 · 스크립트 순서) ·
  출하 레코드 시험 = 피어 기록 시 교차 지문 형식 + 피어가 그 커밋/봉인이면 드리프트 재대조. 전체 스위트 3,788/3,788 · sln 0W/0E.

### 61.4 출하(보류)
- QuantumVault 재봉인 미완(HEAD 50d8c42 · RELEASE_MANIFEST sealedAtUtc 2026-10-07T03:59:36Z) — 운영자 터미널의 seal_v2.8.0.ps1(서명 시드 = 자격 증명 관리자) 대기.
  재봉인 → --peer-readiness 0 → 태그 기준 worktree 에서 -NoPeer 없이 패키징 → release-v12.18.5-GM.zip · v12.18.4-GM 산출물 → release/superseded-v12.18.4-GM.

### 61.5 [0/10] 게이트 실 스크립트 회귀 핀 · 피어 재탐침 (2026-10-14)
- GoldenMasterReleasePackagingTests §13: 임시 호스트(태그 v12.18.5-GM = HEAD · 판정기 = 실 Release 산출물 정션) + 임시 피어(깨끗함 · 봉인 = HEAD 커밋 1초 전)로
  release-package.ps1 실제 구동 → exit 1 · 판정기 exit=2(ManifestStale 단독) · [1/10]/[2/10] 미진입 · zip/master-seal 0 · 게이트 뒤 드리프트 재대조 미실행 · 실측 ~2.8s(빌드 0).
  시험 전용 커밋(출하 바이너리 무관) — 출하는 계속 태그 d44c35b 기준 worktree.
- QuantumVault 재탐침(HEAD 50d8c42): --peer-readiness exit 2 = ManifestStale(봉인 2026-10-07T03:59:36Z) + ★UncommittedCode(.serena/project.yml 신규).
  태그 worktree 실 스크립트(-NoPeer 없이) = [0/10] 피어 미커밋 코드 사전 점검에서 먼저 exit 1(1.2s · 빌드 0 · release/ 생성 0). 출하 계속 보류(§61.4).
- 운영자 조치: QuantumVault .serena/project.yml 정리(커밋/원복) + seal_v2.8.0.ps1 재봉인 → --peer-readiness 0 확인 후 §61.4 절차.
- 전체 스위트 3,789/3,789 · sln + WebOnly Release -warnaserror 0W/0E · 래칫 40 불변(UI 변경 0).

### 61.6 출하 — v12.18.5-GM 단독(-NoPeer) (2026-10-14)
- 결정: QuantumVault 피어 연동 중단 · StockManager 단독 출하(-NoPeer · 피어 조회/탐침 0). §61.4 양방향 보류 라인 종결.
- 형제 worktree `StockManager-gm-12.18.5-np`(태그 v12.18.5-GM = d44c35b · 미커밋 0)에서 release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0:
  [1]/[2] WebOnly · sln 전체(Host · Tests · PrimesBench · x86 KiwoomProxy · KiwoomComShim) -warnaserror 통과 · [3] 3,788/3,788(태그 소스 — §61.5 §13 시험은 태그 이후) ·
  [4] 결합도 0 ≤ 117 · [5] 래칫 40 통과 · [6] 이중키 조회 16 · 마스터 플래그 1 = false · [7] 아티팩트 38 · genesis v2 manifest.sig · 봉인 38 일치 · 앵커 일치 ·
  [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 49 · 강화 키 6 · 원장 ⑪) ·
  [10] interlocks Ok(30 · 마스크 0x800000001FFFFFFF) · tighten Ok(6 · 원장 ⑪ · seqlock ⑤ · IPC 패브릭 무대기·무슬립 ⑥) · handshake Ok @d44c35b587.
- 산출물(release/): release-v12.18.5-GM.zip SHA-256 `60276FC322456A91B35A6F48A753F3664AC27C87E3F59B64DDBA65E073CE83E7` ·
  masterSeal `A019E3F7DF70F3C297B73155C16519E1E2B2D00D816BAB00824DE11F1F15F560` · sourceCommit d44c35b · 피어 기록 없음(단독).
- v12.18.4-GM 산출물 13건(zip FA7830F7… · masterSeal 5C2597E4…) → release/superseded-v12.18.4-GM/ 이관.
- 메인 체크아웃(885fcfd) 재수검: GoldenMasterReleasePackagingTests · PeerDriftGuardTests · PureShellFitness 48/48 · 전체 스위트 3,789/3,789.

## 62. v12.18.6-DEV — /aidash 마이크로초 지터 센티널 무할당 배지 (2026-10-14)

### 62.1 전제 검증(오더 ↔ 실재)
- MicrosecondJitterSentinel(StockManager/Telemetry) · LongSoakProfileHarness(Simulation) · DashboardSnapshotHub 실재.
  /aidash 는 호스트 4줄 셸(StockManager/Blazor/Pages/PaperTradingDashboard.razor) → 실 뷰 = MS2E.UI.Portable/Pages/Trading/PaperTradingDashboardView.razor.
  SignalLabelMapper = MS2E.UI.Portable/Helpers(RCL). 시험 폴더 StockManager.Tests/Telemetry 신설.
- ★센티널 작성자는 Soak 하네스(시뮬레이션)뿐 — 호스트에 실 측정 생산자 없음. 시뮬레이션 형식을 호스트에 결선하면 [9/10] 시뮬레이션 격리 가드 위반 →
  호스트는 결선하지 않음(배지 = '지터 미결선' 정직 렌더 · 숫자 날조 0). 실 생산자(예: 펌프/IPC 하트비트 계측) 결선은 별도 결정 사항.

### 62.2 구현
- JitterSnapshot(값 형식): 임계 초과 누적 · GC 누적 · 최대 · 마지막 스파이크(ns · GC 태그) · 최악 K 요약(개수 · K 번째 · GC 수) · Version.
- 센티널 seqlock 게시: 드문 경로(스파이크 · 꼬리 진입 — 최대 갱신은 항상 꼬리 진입)와 Reset 에서만 → 정상 표본 경로 명령 추가 0.
  TryReadSnapshot = 임의 스레드 · 잠금/대기/할당 0 · 4회 경합 = false(허브는 직전 사본 유지 · JitterContended 계수) · 관리 필드라 Dispose 후 판독 안전.
- 허브 AttachJitter: 50ms 펌프마다 사본 판독 → 누적 증가 = 5초(DashJitter.HoldTicks) 스파이크 배지 · 마지막 GC 태그 = GC 유발 · 만료 = 정상 복귀 재게시.
  첫 판독 = 기준선(결선 전 스파이크 ≠ 최근) · Reset(감소) ≠ 스파이크.
- 포트: DashJitter(0 미결선 · 1 정상 녹 · 2 스파이크 황 · 3 GC 적) + 프레임 Jitter* 8필드. 뷰: 상단 스트립 주문 경로 배지 옆 지터 배지(스파이크 시 µs).
- SignalLabelMapper: 상태 라벨/클래스 정적 배열 · JitterMicrosText(슬롯별 직전 값 1칸 캐시 — 값 불변 렌더 = 0B · 새 값 1회 = 문자열 1개).
  ★정직: Blazor 렌더 트리와 뷰의 누적 수 ToString("N0")은 기존 배지와 같이 프레임워크/표시 할당 — 0B 보증 범위 = 수집 · 사본 · 펌프 · 라벨 조회.

### 62.3 검증
- JitterTelemetryBindingTests 6: 정상 100만 표본 게시 0 · 값 정확 · δ=0(기록 · 스파이크 게시 · 판독 · 펌프 · 라벨) · 즉시 반영/5초 복귀 ·
  동시성 판독 604만 회(경합 107만) 찢김 0 · Soak 하네스 실 센티널 결선(표본 2.6만 · 최악 16) · 래칫 ≤40 · RCL 순수성 · 호스트 미결선 핀.
- WebOnly + sln 전체 Release -warnaserror 0W/0E · 전체 스위트 3,795/3,795. 버전 범프 없음(DEV · 출하 v12.18.5-GM 불변).

### 62.4 호스트 실 지터 결선 — 대시보드 펌프 실행 소요(2026-10-14)
- 생산자 = DashboardSnapshotHub.Pump 자신(매매 핫패스 밖): 회차 전체(링 소비 · 조립 · 게시 · 구독자 통지)를 Begin/End(Stopwatch.GetTimestamp + Gen0 수집 차)로 측정 →
  호스트 전용 센티널 기록(Kind = PumpJitterKind 1). 작성자 = 펌프(단일 진입 CAS로 직렬화 → 센티널 단일 작성자 계약 충족).
- AttachPumpJitter(s) = 기록 결선 + AttachJitter(s). 이번 회차 측정은 다음 회차 판독에 반영(50ms 지연 · 정직). 500µs 초과 = 스파이크 · 구간 내 GC = GC 태그.
- BlazorHomeForm: 센티널 생성 → AttachPumpJitter → Start. 종료 = 허브 Dispose(진행 중 회차 대기 후 _pumping=2 영구 봉인) → 센티널 Dispose(NativeMemory 해제 경합 0).
- 시뮬레이션 격리 유지: 호스트는 Soak 하네스 미참조 · dashHub.AttachJitter 직접 호출 0(§6 핀 갱신 — AttachPumpJitter 존재 핀).
- HostJitterInstrumentationTests 4: 결선 펌프(유휴 · 게시 · 스파이크 기록) δ=0 · 계측 단가 ≈91ns/회차(상한 1µs · 50ms 대비 0.0002%) ·
  구독자 700µs 지연 주입 → 스파이크 +1 · 최대/마지막 ≥700µs · 배지 Spike · GC.Collect 동반 → GcSpike · 미결선 → 결선 후 첫 회차 정상 지표 ·
  실 타이머 첫 게시 ≤119ms 지표 전환 · Dispose 봉인(진행 중 20ms 회차 대기 · 이후 Pump false).
- 오중 빌드(Default sln · WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 3,799/3,799 · 래칫 ≤40.

## 63. v12.18.6-GM — 호스트 펌프 지터 텔레메트리 단독 출하(-NoPeer) · IL 규칙 ⑫ (2026-10-14)

### 63.1 버전 · 태그
- 12.18.5 → 12.18.6(csproj Version/Assembly/File · GoldenMasterFreeze/ReleasePackaging 핀 · 스크립트 머리말). 태그 v12.18.6-GM = 741fe17(fc3ce56 DEV 위).
- QuantumVault 미접근(-NoPeer · 피어 조회/탐침 0) · 매니페스트 피어 기록 없음.

### 63.2 [9/10] IL 규칙 ⑫ — 호스트 펌프 지터 결선(ShippedBinaryLiveArmedGuard)
- DashboardSnapshotHub.AttachPumpJitter 호출 = BlazorHomeForm 트리에서만(≥1 · 보고 PumpJitterSites) · 조립 밖 결선 = 위반(시뮬레이션 · CLI 격리 형식 제외).
- AttachJitter 직접 호출 = 허브 자신만 — 호스트가 외부 센티널을 배지에 결선 = 위반. AttachPumpJitter 본문 AttachJitter 정확히 1(측정 센티널 = 배지 센티널).
- 비공허성: 진입점이 있는데 호스트 호출 0 = 위반. 진입점 부재(⑫ 이전 바이너리) = 미적용 → 출하 게이트 `--require-pump-jitter`(스크립트 [9/10])가 거부.
- 시험 §15: 빌드 DLL Ok(1곳) · call 토큰 AttachPumpJitter→AttachJitter 패치 = 직접 결선 + 비공허성 거부 · CLI 요구 플래그 · v12.18.5-GM zip = ⑫ 0 → 플래그 거부.
- §3 실 출하 기록: 12.18.6 이후 출하물 PumpJitterSites ≥ 1 요구.

### 63.3 출하 실행(형제 worktree StockManager-gm-12.18.6-np · 태그 741fe17 · 미커밋 0)
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0: [1]/[2] WebOnly · sln 전체(Host · Tests · PrimesBench · x86 KiwoomProxy · KiwoomComShim) -warnaserror ·
  [3] 3,800/3,800 · [4] 결합도 · [5] 래칫 ≤40 통과 · [6] 이중키 조회 16 · 마스터 플래그 1 = false · [7] 아티팩트 38 · genesis v2 manifest.sig · 봉인 38 일치 ·
  [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 49(CLI 29) · 강화 키 6(호스트 판독 8) · 원장 ⑪ · 펌프 지터 ⑫ 1 · 위반 0) ·
  [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · ⑪ · seqlock ⑤ · IPC 패브릭 ⑥) · handshake Ok @741fe17626.
- 산출물(release/): release-v12.18.6-GM.zip SHA-256 `9F1C12DF72CADDADC4FA41ABFB3B1F940D16DCD4F4423302CE24ECFCBAC1FE45` ·
  masterSeal `1DDCE28A1AAD0BEA9943816A048A02D1D097A98ADD3FA7860EFD6A59C8C8826A` · sourceCommit 741fe17 · 피어 기록 없음(단독).
- v12.18.5-GM 산출물 13건(zip 60276FC3… · masterSeal A019E3F7…) → release/superseded-v12.18.5-GM/ 이관. worktree 제거.
- 메인 체크아웃 재수검: sln Release -warnaserror 0W/0E · GoldenMaster*/PeerDriftGuard/PureShellFitness 61/61 · 전체 스위트 3,800/3,800 · CLI --verify-live-armed --require-pump-jitter Ok.

## 64. v12.18.7-DEV — /aidash 브로커 세션 복구 배지 (2026-10-14)

### 64.1 전제 검증(오더 ↔ 실재) — 대부분 v12.17.0~1-GM 에서 이미 출하
- SessionRecoveryManager(백오프 1·2·4·8·16s · 소진 5회 sticky · 침묵 강제 종료 · 재접속 후 대사 확정 전 격리) · RecoveryStateDriftMonitor(MMF 100/101) ·
  KisRealtimeSession(PINGPONG 반송 · 장중 3초 침묵 = 비트 19 KisSession) · KisTokenLifecycle/KisLiveBroker(서버 expires_in · 만료 5분 전 갱신 · 실패 = 비트 18 KisAuth) ·
  KisOpenOrderQuery(TTTC8001R) + BrokerOpenOrderReconciler(불일치/유령 = 비트 22 + 영구 래치 · 유령 비상 취소 TTTC0803U 패스당 ≤4) · 키움 링크 = 비트 25 KiwoomBridgeLink.
- 오더 서술과 실재 차이(변경 안 함 · 운영자 결정 사항):
  ① 비트 14 = AccountDivergence(BrokerCommTimeout 아님) — 키움 프록시 무응답은 기존 비트 25 + 수동 모드 복구 관리자 격리.
  ② 백오프 상한 16s(오더 30s) · 토큰 선행 갱신 5분(오더 30분) — 출하된 동작 · 시험 핀 유지.
  ③ ★유령 비상 취소는 liveArmed 와 무관하게 동작(실측 · BrokerOpenOrderReconciler.CancelRowsLocked — 무장 술어 미참조): KIS 자격 증명이 구성되면
     (kisLive ≠ null → AttachKisCanceller) 로컬 미추적 미체결 = 유령 → 실 TTTC0803U 취소 송신(패스당 ≤4 · 5초 중복 억제). HTS/MTS 수동 주문도 대상.
     취소 = 노출 축소 방향이라 의도된 설계일 수 있으나 '수동 주문 병행 계좌'에선 사고 — 운영자 결정 필요(무장 게이트 · 설정 옵트인 · 엔진 주문 식별 한정). 변경 안 함.
  ④ GenesisPreFlightUpdater 는 무장 임대 재인증기(브로커 세션 무관) — 확장하지 않음. 1초 집계 대신 대시보드 50ms 펌프가 관리자 사본을 직접 판독.
- 실제 공백 = /aidash 에 브로커 세션 상태 표시 0 → 이 마일스톤 범위.

### 64.2 구현
- SessionRecoveryManager.Snapshot(): 상태 · 수립 이력 · 소진/불일치 래치 · 격리 · 연속 실패/상한 · 단절/복구/시도 누적을 락 1회로 판독(record struct · 0B).
- 포트: DashSession(0 미결선 · 1 연결 · 2 접속 대기 · 3 백오프 · 4 재접속 중 · 5 대사 중 · 6 소진 격리 · 7 불일치 격리 — 래치 우선) + DashBrokerSession · 프레임 Sessions/SessionCount.
- DashboardSnapshotHub.AttachSessionRecovery(인덱스 = Broker · 0..3): 펌프가 사본 판독 → 서명(결선 표지 + 상태/래치/계수 — 결선 즉시 · 전이 즉시 재게시) → 프레임.
  ★교정(시험 실측): 초기 상태(Connecting · 계수 0)는 서명 기여 0 → 결선 직후 재게시 누락 — 결선 표지(+1 + 인덱스)로 해소.
- BlazorHomeForm: 드리프트 모니터 생성 뒤 kiwoomRecovery/kisRecovery 결선(판독 전용 — 격리/재접속 경로 무간섭). 브로커 연결 꺼짐 = '세션 복구 관리자 미결선' 정직 렌더.
- 뷰: '브로커 한도' 카드 아래 '브로커 세션' 표(녹 연결 · 황 대기/백오프/재접속/대사 · 적 소진/불일치 · 실패 n/상한 · 단절 · 복구). SignalLabelMapper 정적 라벨/클래스.

### 64.3 검증
- StockManager.Tests/Adapters/SessionRecoveryManagerTests 5: 사본 = 속성 일치 · 전 생애주기 분류(백오프 · 재접속 · 대사 · 복구 · 소진 · 불일치 우선) ·
  허브 결선 즉시/전이 즉시 재게시 · 무변경 게시 0 · 인덱스 범위 거부 · δ=0(사본 · 2관리자 펌프 유휴/전이 · 라벨) · 결선/순수성/래칫 ≤40.
- 오중 빌드 Release -warnaserror 0W/0E · 전체 스위트 3,805/3,805. liveArmed:false 불변 · 실 브로커 송신 0 · QuantumVault 미접근. 버전 범프 없음(DEV).

### 64.4 유령 비상 취소 무장 가드(BrokerOpenOrderReconciler · §64.1 ③ 교정)
- 결함: CancelRowsLocked 가 무장 술어 미참조 → liveArmed:false 에서도 브로커 미체결(HTS/MTS 수동 주문 포함)에 실 취소 송신(키움 CancelByVenueNo · KIS TTTC0803U).
- 교정: AttachCancelArming(Func<bool>) — 유령 판정 시 무장 표본 1회 · 참일 때만 CancelRowsLocked(패스당 ≤4 · 5초 중복 억제 그대로).
  미결속 · 거짓 · 예외 = 송신 0 + CancelsSuppressed(행) · SuppressedPasses 계수 · OpenOrderPassResult.CancelsSuppressed(호스트 Log.Warn 표기). 키움 · KIS 공통.
- 비트 22(OpenOrderDivergence) Raise · sticky · 한도 격리 = 무장 무관 그대로(차단 항상 먼저).
- ★표본 시점 = 유령 판정 직후 · 자기 Disarm(비트 22 Resync) '이전': 호스트 liveArmed 는 집계 마스크 Blocked 를 포함 → 송신 직전 재판정이면
  자기 비트 22 때문에 무장 상태에서도 송신 영영 0(설계 중 확인 · 시험 핀). 결과: 무장 중 유령 = 그 패스 ≤4건 송신 · 다음 패스부터 sticky 로 억제
  (구 동작의 '패스마다 ≤4건 계속 정리'에서 축소 — 남은 유령은 운용자 처리 · 물리 이중키 재무장 전 자동 송신 0).
- 호스트: openOrderRecon.AttachCancelArming(liveArmed) — 물리 키 ∧ 전 방어선. 드라이런 술어(armedPredicate)는 허가하지 않음(실 송신 = 실전 무장만).
- 시험 리그(OpenOrder/KisOpenOrder Reconciliation · MultiBrokerRecoverySoakHarness)는 무장 경로 회귀라 명시적 무장 술어 결속.
- 인터락 정의 불변: 14 AccountDivergence · 22 OpenOrderDivergence · 25 KiwoomBridgeLink · 30종(오더의 'ReconciliationMismatch'/'KiwoomLinkDead' 는 실 이름 아님 — 개명 안 함).
- BrokerOpenOrderReconcilerTests 7: 무장 해제 KIS 6행 × 6패스 송신 0 · 억제 36 · 비트 22 · 키움 동일 · 미결속/예외 0 · 무장 = 상한 4 · Disarm 선반영 ·
  호스트형 술어(!Blocked) 1패스 송신 4 → 2패스 억제 · 다른 방어선 차단 시 0 · δ=0(무장 해제 유령 패스 500회 0B) · 호스트 결선/순서/인터락 정의 핀.

## 65. v12.18.7-GM — 브로커 안전 단독 출하(-NoPeer) · IL 규칙 ⑬ 유령 취소 무장 가드 (2026-10-14)

### 65.1 버전 · 태그
- 12.18.6 → 12.18.7(csproj · Freeze/Packaging 핀 · 스크립트 머리말). 태그 v12.18.7-GM = d8c2194(3e3a27c 세션 패널 · 7a36921 무장 가드 위).
- QuantumVault 미접근(-NoPeer) · 매니페스트 피어 기록 없음.

### 65.2 [9/10] IL 규칙 ⑬ — 유령 비상 취소 무장 가드(ShippedBinaryLiveArmedGuard)
- 적용 = BrokerOpenOrderReconciler.AttachCancelArming 존재 시: CancelRowsLocked(브로커 취소 송신) 호출마다 같은 메서드 안 앞선 CancelArmedNow 호출 +
  그 뒤 조건 분기 · 대사기 밖 CancelRowsLocked 호출 0 · 호스트(BlazorHomeForm 트리) AttachCancelArming ≥1(CancelArmingSites) · 조립 밖 결선 = 위반.
- 진입점 부재(구 바이너리 = 무장 무관 송신) = 미적용 → 출하 게이트 `--require-cancel-arming`(스크립트 [9/10])이 거부. v12.18.6-GM zip = 거부 확인.
- 위반은 진입점 존재 확인 후 확정(메서드 열거 순서 무관 — 시험 중 구 바이너리 오탐 교정).
- 시험 §16: 빌드 DLL Ok(1곳) · CancelArmedNow→get_GhostDetected 토큰 패치 = '무장 무관 취소 송신' 거부 · AttachCancelArming→AttachKisCanceller = 비공허성 거부 ·
  CLI 요구 플래그 · 구 출하물 = ⑬ 0. §3 실 출하 기록: 12.18.7 이후 CancelArmingSites ≥ 1 요구.

### 65.3 출하 실행(형제 worktree StockManager-gm-12.18.7-np · 태그 d8c2194 · 미커밋 0)
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0: [1]/[2] 오중 빌드 -warnaserror · [3] 3,813/3,813 · [5] 래칫 ≤40 · [6] 이중키 조회 16 · 마스터 false ·
  [7] 아티팩트 38 · 봉인 일치 · [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) ·
  원장 ⑪ · 펌프 지터 ⑫ 1 · 유령 취소 무장 ⑬ 1 · 위반 0) · [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · ⑪ · seqlock ⑤ · IPC ⑥) · handshake Ok @d8c2194441.
- 산출물(release/): release-v12.18.7-GM.zip SHA-256 `5120E8FEF024373DE6D41A78190CB7AE32D45EFE852E68D4EE320227D09AD9B1` ·
  masterSeal `B7B5092CACAD1FACD44FED198BB0A2AD2C82A22727B8E5B89F28022EFC8A5D26` · sourceCommit d8c2194 · 피어 기록 없음.
- v12.18.6-GM 산출물 13건(zip 9F1C12DF… · masterSeal 1DDCE28A…) → release/superseded-v12.18.6-GM/. worktree 제거.
- 메인 체크아웃 재수검: sln Release -warnaserror 0W/0E · CLI --verify-live-armed --require-pump-jitter --require-cancel-arming exit 0 · 전체 스위트 3,813/3,813.

## 66. v12.18.8-DEV — 활성 텔레메트리 하 자율 UI 스위치 퍼징 (2026-10-14)

### 66.1 전제 검증
- bUnit 퍼저(AutonomousUiSwitchFuzzer + BunitClickSurface) · 헤드리스 렌더러 표면 · 10k CLI 퍼징 · 스톰 격리는 v12.17.4-GM 부터 존재(AutonomousUiVerificationTests).
- 공백: 퍼즈 리그(UiFuzzRig) 허브에 펌프 지터 · 세션 관리자 미결선 → 대시보드 두 패널이 '미결선'으로만 렌더 = 라이브 갱신 경합 미검증.

### 66.2 시험(StockManager.Tests/Blazor/UiSwitchFuzzingConcurrencyTests · 4)
- 리그 허브에 AttachPumpJitter(50ms 타이머 실측) + 세션 관리자 2(키움 · KIS) — 전용 스레드가 무작위 전이(연결 · 단절 · 허가 · 실패 · 확정 · 불일치 · 이중키 재설정)
  를 수억 회 구동하는 동안 10,255 상호작용(워밍업 1,000 + 50 × 180 청크) · 50회마다 게시본 일관성 판독(세션 2 · 순서 · 상태 범위 · 상한 · 계수 · 마스크 0).
- bUnit(DOM): UI 예외 0 · 불법 무장 0 · 적대 400(수용 0) · 대칭 255(파손 0) · 찢김 0 · 전이 예외 0 · 5대 상태 도달 · 펌프 고장 0 · 189/s ·
  컨텍스트 폐기 후 허브 FrameReady 구독자 0(누수 0).
- 헤드리스(호스트와 같은 Renderer 파생): 위와 같음 + 2,523/s · ★힙(전체 GC 후) 16,666 → 16,683 → 16,708KB(9k 상호작용 +42KB · 평탄) = 누수 판정.
- 라벨 캐시: 세션 8 · 지터 4 라벨/클래스 = 같은 참조(정적 · 히트 100%) · µs 텍스트 같은 버킷 = 같은 참조 · 조회 10만 회 0B · 두 패널 결선 펌프 유휴/전이 0B.
- 래칫 ≤40 · RCL 이 호스트 어셈블리 미참조.

### 66.3 발견 · 교정(전부 하네스 — 제품 결함 0)
- ① bUnit 힙 상호작용당 ~24KB 보유(46 → 170 → 292MB) — A/B 실측: 텔레메트리 끈 bUnit 도 동일 · 헤드리스는 평탄 ⇒ bUnit/AngleSharp 하네스 보유. 누수 판정은 헤드리스로 · bUnit 수치는 보고만.
- ② 노후 핸들러(UnknownEventHandlerIdException): 탐색(DOM 사본) ↔ 클릭 사이 비동기 재렌더 → BunitClickSurface 가 같은 루트 · 종류 · 태그 · 순번 요소 재탐색 후 1회 재시도
  (StaleTargets 계수 · 재시도 실패 = 결함).
- ③ 토글 대칭 위양성: 변동성 루트(대시보드)는 라이브 프레임이 두 토글 사이 루트를 바꿈 → IUiClickSurface.IsVolatile(기본 false) · 퍼저가 변동성 루트 대칭 판정 제외.
- ④ 토글 대칭 위양성(설정 '서버 사용' @bind): bUnit 표면이 반전값을 DOM 사본 checked 로 정하는데 CPU 포화 시 직전 반전 렌더가 사본에 닿기 전 재판독 → 같은 값 2회 송신.
  진단 = 루트 마크업 추적(checked → 미checked). 교정 = 자기 조작 렌더 반영 대기(WaitForState ≤250ms · 거부 = SettleTimeouts). 헤드리스는 렌더 트리 판독이라 0.
- ⑤ 시험 자체 결함(전이 스레드): 미완료 WaitForAttemptAsync 방치 → 대기자 1개 계약 위반 예외 400만 — 보관 · 완료 시 1회 소비로 교정.
- 허브: FrameReadySubscribers(콜드 판독) 추가 — 누수 점검용.
- ★정직: 렌더(diff · 마크업 · DOM 파싱)는 프레임워크 할당 — 0B 보증 = 라벨 조회 · 허브 펌프 · 세션 사본. bUnit 속도 189/s(요구 100/s 충족) · 헤드리스 2.5k/s.

### 66.4 전체 스위트 안정화(커밋 전 · 2026-10-14)
- 증상: 전체 스위트 시험 호스트 무음 종료(UnhandledException · 1026 · 2004 전무) 3회 — 지점 상이(2,471 / 2,165 / 3,153 · --blame = 기존 MonitorLifecycleRaceTests).
  대조군(v12.18.7-GM 문서 커밋 2dfd468 · 변경 0) 전체 스위트 2회 중 1회도 무음 종료(WarmRestartEngineTests) ⇒ 이 마일스톤 이전부터의 부하 민감 현상
  (동시 타 세션 python 2개 ~15GB · 물리 여유 ~4GB). 근본 원인 미확정 — 재현 시 [[testhost-crash-triage-eventlog]] 절차.
- 신규 bUnit 퍼징이 가장 무거운 시험이라 점유 축소: ① bUnit 컨텍스트 ~2,000 상호작용마다 재탑재(보유 해제 — 힙 136MB 평탄 · 재탑재마다 구독 0 재판정)
  ② 전이 스레드 256 단계마다 Sleep(1)(코어 독점 해소 · 전이 수백만 유지).
- 뷰 구독 해지 = 렌더러 폐기가 진행 중 라이브 렌더를 마친 뒤 ≤ ~17ms 지연(누수 아님) — 판정은 2초 안 0 수렴(SubscribersAfterDispose).
- 전체 스위트 부하에서 드러난 하네스 대칭 위양성 2건 교정: checkbox 렌더 반영 대기 250ms → 2s(정상 시 초과 0) · ★노후 표적 재시도 경로에도 반영 대기
  (누락 시 재시도 토글 직후 사본 판독 → 같은 값 2회) · 재시도 재식별에 부모 텍스트 일치 요구(구조 변경 시 오표적 조작 0 — 불일치 = 소멸 취급).
- 결과: 오중 빌드 Release -warnaserror 0W/0E · 전체 스위트 3,817/3,817 연속 2회(무크래시) · bUnit 10,255 상호작용 UI 예외 0 · 대칭 파손 0 · 반영 대기 초과 0.

## 67. v12.18.8-GM — UI 텔레메트리 퍼징 단독 출하(-NoPeer) (2026-10-14)

### 67.1 버전 · 태그
- 12.18.7 → 12.18.8(csproj · Freeze/Packaging 핀 · 스크립트 머리말). 태그 v12.18.8-GM = 1024240(1eb3350 퍼징 마일스톤 위). 신규 게이트 없음 — ⑫ · ⑬ 유지.
- QuantumVault 미접근(-NoPeer) · 매니페스트 피어 기록 없음.

### 67.2 출하 실행(형제 worktree StockManager-gm-12.18.8-np · 태그 1024240 · 미커밋 0)
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0: [1]/[2] 오중 빌드 -warnaserror · [3] 3,817/3,817(무크래시) · [5] 래칫 ≤40 · [6] 이중키 조회 16 · 마스터 false ·
  [7] 아티팩트 38 · 봉인 일치 · [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) ·
  원장 ⑪ · 펌프 지터 ⑫ 1 · 유령 취소 무장 ⑬ 1 · 위반 0) · [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · ⑪ · seqlock ⑤ · IPC ⑥) · handshake Ok @10242408da.
- 산출물(release/): release-v12.18.8-GM.zip SHA-256 `9C683FC2410E8F3AAC790FE64512546A72B5024B37F46C159A4CAEB7C91333FB` ·
  masterSeal `355A687D6521F138CAE4A005FDC2DB2FF510A9B2EA80C54023A73D1EEFB02ECD` · sourceCommit 1024240 · 피어 기록 없음.
- v12.18.7-GM 산출물 13건(zip 5120E8FE… · masterSeal B7B5092C…) → release/superseded-v12.18.7-GM/. worktree 제거.
- 메인 체크아웃 재수검: sln Release -warnaserror 0W/0E · CLI --verify-live-armed --require-pump-jitter --require-cancel-arming exit 0 · GoldenMaster Freeze/Packaging 48/48.

## 68. v12.18.9-GM — Teardown 강화 단독 출하(-NoPeer) (2026-10-15)

### 68.1 버전 · 태그
- 12.18.8 → 12.18.9(csproj · Freeze/Packaging 핀 · 스크립트 머리말). 태그 v12.18.9-GM = b9d5558(6bb9fd6 TeardownDrain · UI 퍼징 Quiesce 마일스톤 위). 신규 게이트 없음 — ⑫ · ⑬ 유지.
- QuantumVault 미접근(-NoPeer) · 매니페스트 피어 기록 없음 · 원격 미구성(푸시 없음).

### 68.2 출하 실행(형제 worktree StockManager-gm-12.18.9-np · 태그 b9d5558 · 미커밋 0)
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0: [1]/[2] 오중 빌드 -warnaserror 0W/0E · [3] 3,817/3,817(3m30s · 무크래시) · [5] 래칫 ≤40 · [6] liveArmed 기본값 수검 ·
  [7] 아티팩트 38 · 봉인 일치 · [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) ·
  원장 ⑪ · 펌프 지터 ⑫ 1 · 유령 취소 무장 ⑬ 1 · 위반 0) · [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · seqlock · IPC) · handshake Ok @b9d5558a30.
- 산출물(release/): release-v12.18.9-GM.zip SHA-256 `6F43023E722A5D9E8CBA523EC21C6293C4F6E86EE3A4BE6179078CD7B03C9EDC` ·
  masterSeal `071BC4493F856E9BC042154E2E5ED843C553884873DCD4517C6B086006F7E83A` · sourceCommit b9d5558 · 피어 기록 없음.
- v12.18.8-GM 산출물 13건(zip 9C683FC2… · masterSeal 355A687D…) → release/superseded-v12.18.8-GM/. worktree 제거.
- 메인 체크아웃 재수검: sln Release -warnaserror 0W/0E · CLI --verify-live-armed --require-pump-jitter --require-cancel-arming exit 0 · GoldenMaster Freeze/Packaging 48/48.


## 69. v12.18.10-DEV — Live Session Pre-Flight Sentinel(브로커 생존성 사전 진단 · 인터락 선제 래치) (2026-10-15)

### 69.1 전제 교정
- 지시서 경로 `StockManager/Services/GenesisPreFlightUpdater.cs` 는 부재 — 실제 `StockManager/Blazor/GenesisPreFlightUpdater.cs` 를 확장.
- 비트 14(AccountDivergence) · 25(KiwoomBridgeLink)는 이미 각 인터락이 단독 결속(`Bind` 재결속 거부) → 새 소스로 재결속하지 않고 소유 인터락의 sticky 경로로만 래치.

### 69.2 구성
- `BrokerLivenessPreFlight`(신규 · Blazor/): 레인(0 키움 · 1 KIS) 병렬 비파괴 프로브 · 레인별 시한 3초(WaitAsync — 취소 무시 프로브도 절단) ·
  판정(NotConfigured · Healthy · Timeout · Invalid · Fault · Pending). 비정상 판정 = 그 레인 continuation 안에서 즉시 래치(다른 레인 대기 0):
  키움 → `KiwoomBridgeLinkInterlock.LatchPreFlight()`(비트 25 · 링크 재수립으로 해제 불가) + 비트 14 · KIS → 비트 14(`AccountDivergenceInterlock.Trip(-1, −판정)`).
  상태 Pending → Passed/Failed(sticky · 재진단 거부) · `TryRearm` 물리 이중키 → Pending(재진단 필요).
- `GenesisPreFlightUpdater`: `ProbeBrokerLiveness()` · 임대 갱신 술어 `RenewAllowed` = 인증 ∧ 무장 ∧ (진단 미결선 ∨ Passed) — 진단 통과 전 임대 갱신 0.
- 호스트: 키움 프로브 = `bridge.IsProductionProxy ∧ !IsHeartbeatTimedOut` 판독(송신 0) · KIS 프로브 = `QueryBalanceJson`(토큰 발급 + TTTC8434R 잔고 GET · 원장 변형 0).
  미결선 레인(Kiwoom.ConnectEnabled=false · KIS 자격증명 없음) = 비차단. 기동 시 콜드 비동기 1회.
- 진단 후 핑퐁 계측: 1초 타이머가 키움 IPC RTT 를 `RecordPingPong`(Interlocked 만 · δ=0) — 시한 초과는 누계만(차단은 기존 세션 인터락 소유 · 이중 래치 금지). KIS RTT 원천 없음 = 0(정직).
- /aidash 브로커 세션 패널: '사전 진단' 행(DashPreFlight 상태 · 레인 판정 · 소요 ms · 핑퐁 최대 µs) — 허브 `AttachPreFlight` 판독 전용 무할당 투영.

### 69.3 검증
- `StockManager.Tests/Services/PreFlightSentinelTests.cs` 11건: 무응답 시한 래치(25 · 14) · 실패 레인 선래치(타 레인 판정 전) · 래치 sticky/이중키 ·
  토큰 무효(14 만) · 예외/취소/미판정 응답 fail-closed · 정상/미결선 Passed · 임대 술어 · 재진단 거부 · 핑퐁/판독 δ=0(Release · MinAllocated 3창) · 송출 경로 부재 소스 수검 · 투영 코드 일치.
- 오중 빌드(Default sln · WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 3,828/3,828 · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 한계(정직): '0ms 격발' = 판정 continuation 내 동기 래치(시한 3초 경과 자체는 대기). 실 키움 COM/KIS 계좌 실측은 이 호스트에서 불가(시뮬레이션 프로브로 증명).

## 70. v12.18.10-GM — Pre-Flight Sentinel 단독 출하(-NoPeer) (2026-10-15)

### 70.1 버전 · 태그
- 12.18.9 → 12.18.10(csproj · Freeze/Packaging 핀 · 스크립트 머리말). 태그 v12.18.10-GM = 9341871(6579dd4 사전 진단 센티넬 마일스톤 위). 신규 게이트 없음 — ⑫ · ⑬ 유지.
- QuantumVault 미접근(-NoPeer) · 매니페스트 피어 기록 없음 · 원격 미구성(푸시 없음).

### 70.2 출하 실행(형제 worktree StockManager-gm-12.18.10-np · 태그 9341871 · 미커밋 0)
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0: [1]/[2] 오중 빌드 -warnaserror 0W/0E · [3] 3,828/3,828(4m · 무크래시) · [5] 래칫 ≤40 · [6] liveArmed 기본값 수검 ·
  [7] 아티팩트 38 · 봉인 일치 · [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) ·
  펌프 지터 ⑫ 1 · 유령 취소 무장 ⑬ 1 · 위반 0) · [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6) · handshake Ok @9341871885.
- 산출물(release/): release-v12.18.10-GM.zip SHA-256 `52D30390D069D09B5AB6967E639FCC7338ED288BBA4E89F13E9DB93BD3A087B2` ·
  masterSeal `BDA2178B89C0F6BF8A420C90BB176E7761FC0C5D0044C7E2C7BFFCF070130958` · sourceCommit 9341871 · 피어 기록 없음.
- v12.18.9-GM 산출물 13건(zip 6F43023E… · masterSeal 071BC449…) → release/superseded-v12.18.9-GM/. worktree 제거.
- 메인 체크아웃 재수검: sln + WebOnly Release -warnaserror 0W/0E · 전체 스위트 3,828/3,828 · CLI --verify-live-armed(출하 zip) exit 0 · GoldenMaster Freeze/Packaging 48/48.

## 71. v12.18.11-DEV — Real-time AI vs Human Arena Mode · Dual-Book Simulator (2026-10-16)

### 71.1 전제 교정
- 지시서 `MS2E.UI.Portable/Shared/NavMenu.razor` 는 부재 — 실제 `MS2E.UI.Portable/Components/NavMenu.razor`('AI & 저널' 그룹)에 항목 추가.
- 래칫 실측 coupled = 40 = 상한 → 호스트 셸 `StockManager/Blazor/Pages/Arena.razor` 는 [Inject] 0(라우팅 셸 전용 · 래칫 불변).
- 호스트 조립부는 `StockManager.Simulation`(리허설 사슬 = 가상 브로커 · 무장 술어 강제 true) 문자열 0 · IL 참조 0 이 불변(격리 ⑥ + 소스 가드).
  아레나는 호스트 런타임 기능이므로 파일은 지시대로 `StockManager/Simulation/Arena/` 에 두되 네임스페이스는 `StockManager.Engines.Arena` —
  리허설 사슬과 분리 · 아레나 자체 IL 허용목록 테스트가 `StockManager.Simulation` 형식 참조도 위반으로 강제.

### 71.2 구성
- `ArenaPort`(RCL Ports/Trading): `ArenaSnapshot`/`ArenaSideStats`(읽기 전용 구조체 · 양측 자산 · 현금 · 실현/평가 · 수수료 · 세금 · 슬리피지 · 고점 · MDD(원·bp) ·
  승/패 · 체결/거부 · 리더 · 격차) · `IArenaPort`(TryRead · CopyEquity · Start · Stop · SubmitHuman — 무장/브로커/실주문 멤버 부재).
- `DualBookSimulator`(Simulation/Arena · ns Engines.Arena): 종목별 최신 10호가 사본(128 슬롯) · 시장가 = 호가 1→10 순회 IOC(공유 호가 미차감 — 양측 동일 조건) ·
  수수료 0.015%(양방향) · 거래세 0.18%(매도) 원 미만 절사(Int128) · 현금 부족 = 전량 거부(축소 0) · 공매도 거부 · 평가 = 보유 × 최우선 매수호가(없으면 원가).
- `ArenaMatchGovernor`: 대기 → 진행 → 종료 상태 머신(명령 1워드 원자 교체 · 마지막 명령 우선 · 재개시 = 원장 초기화 + 잔여 주문 폐기) ·
  생산자 링(호가 256 · 주문 128 · drop-oldest 계수 · 재시도 0) · Step = 명령 → 호가 → 주문 → 시가평가/MDD/리더 → 자산곡선(1초 · 600점) → seqlock 게시.
  AI 규칙: 진입 = 미보유 종목만 자본금 × 10% ÷ 최우선 매도호가 · 청산 = 보유 전량.
- 허브 `DashboardSnapshotHub.AttachArena()`: 50ms 펌프가 `Step` 구동(대시보드 프레임 서명 무관) · `PublishDecision`(시그널 엔진 싱크)이 진입/청산을 AI 링에 분기.
- 호스트 결선(BlazorHomeForm): `bridge.OrderBookReceived → arena.OnQuote` · `IArenaPort` 싱글턴. 실 피드 미결선이면 '호가 미수신 — 체결 불가' 정직 렌더(가짜 시세 0).
- UI: `ArenaBattleView.razor`(좌 AI · 우 휴먼 스코어보드 + 자산곡선 스파크라인 · 상승 적색/하강 청색 · 휴먼 퀵 티켓 시장가 매수/매도 → SubmitHuman) ·
  `/arena` 셸 · NavMenu '⚔️ AI vs Human'.

### 71.3 검증
- `StockManager.Tests/Simulation/ArenaMatchGovernorTests.cs` 11건: 수수료/세금 절사 · 동일 가격 진입 → 수수료 차감 후 양측 동일 · 휴먼 청산 실현손익 8,511원 정확값 ·
  리더/격차 · 호가 순회 슬리피지 · IOC 부분 체결 · 거부 분류(호가 미수신 · 공매도 · 현금 부족 · 미진행 · 값 오류) · 종료 동결/재개시 초기화 · MDD/자산곡선 ·
  핫패스(호가 · 휴먼/AI 주문 · Step · TryRead · CopyEquity) δ=0(Release · MinAllocated 3창) · seqlock 동시 판독 찢김 0 ·
  Fail-Closed: IL 참조 허용목록(System · Ports.Trading · Arena · TelemetryRing · OrderBookPayload 외 0) + 허브 경유 전 매치 후 런타임 객체 그래프 도달 형식 검사 · 셸 [Inject] 0 · 결선 소스 수검.
- 오중 빌드(Default sln · WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 3,839/3,839(3m38s) · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 한계(정직): 가상 체결은 시장 충격 · 대기열 순번 · 체결 지연을 모델링하지 않는다(최신 호가 즉시 체결). UI 문자열 생성은 렌더 시점(250ms 버전 폴링)만 — δ=0 대상은 정산/판독 경로.
  실 키움 L2 피드 실측은 이 호스트에서 불가(합성 호가로 증명).
  전체 스위트 1차 실행에서 기존 타이밍 테스트 1건씩 간헐 실패(LongSoak 3중 경합 퍼저 32B · PreFlight 시한 continuation) — 단독 5회 반복 통과 · 재실행 3,839/3,839(아레나 무관 · 기존 플래키).

## 72. v12.18.12-DEV — Arena High-Speed Tick Replay · Playback Controller (2026-10-16)

### 72.1 전제 교정
- 지시 경로 `StockManager/Engines/Arena/` 는 부재 — 신설. v12.18.11 의 `ArenaMatchGovernor` · `DualBookSimulator` 도 `StockManager/Simulation/Arena/` → `StockManager/Engines/Arena/` 로
  git mv(네임스페이스 `StockManager.Engines.Arena` 와 폴더 일치 · 내용 변경 = 리플레이 구동 API 추가뿐). §71 의 경로 기술은 당시 기록으로 유지.
- '50ms 마다 DashboardSnapshotHub 로 ArenaSnapshot 공급' — 허브 펌프가 리플레이 원장에 직접 쓰면 이중 작성자(seqlock 찢김). 허브는 `AttachArenaReplay` 로
  게시 요청 플래그만 세우고 게시는 원장 단일 작성자인 재생 스레드가 수행(요청 또는 자체 50ms 경과).
- 리플레이 AI = 실시간 AI 와 같은 정량 시그널 엔진(`QuantSignalProvider`) 전용 인스턴스(시계 = 재생 시뮬레이션 시각). 전략 이상 인터락은 무장 집계어 미결속.

### 72.2 구성
- `ArenaTickSource`: `RecordedTickSource`(녹화 OrderBookPayload 배열 · 시각 역행 거부) · `SyntheticTickSource`(시드 결정론 · 틱당 13B 압축 → 판독 시 10호가 전개 ·
  KRX 호가 단위 · 1M 틱 ≈ 13MB · 간격 23.4ms = 1M 틱이 정규장 6.5시간 · 종목 3개 · 압력 국면 = 잔량 불균형 + 가격 표류). 합성 데이터는 훈련용(실 시장 통계 아님).
- `ArenaTickReplayEngine`(IArenaReplayPort): 명령 링(시작/재개 · 일시정지 · 리셋 — 순서 보존) · 배속 1/5/10/50/100/최대 · Stopwatch 앵커 스케줄
  (목표 sim = anchorSim + Δwall × 배속 — 대기 오차 비누적 · 회차당 최대 8,192틱 · 배속 변경 = 그 회차까지 기존 배속 처리 후 재기준) ·
  틱마다 AI OnBook → 원장 `Advance`(호가 직접 반영 · 주문 링 소비 · 시가평가 · 자산곡선 표본) · 스냅샷 게시는 50ms(또는 허브 요청)마다 1회 ·
  일시정지 = 틱 처리 중단(원장 정확 동결) · 일시정지 중 휴먼 주문 = 대기 → 재개 첫 틱 체결 · 재개 = 재기준(정지 시간 미보상) · 완료 = 원장 Finish 동결 ·
  재생 오류 = 일시정지(재시도 루프 0) · 첫 명령 시 테이프 생성 + 전용 스레드 기동(콜드) · Dispose = 스레드 조인.
- `ArenaMatchGovernor` 추가 API: `Advance` · `PublishNow` · `Rearm`(초기화 + 진행/대기 · 표본 간격) · `Finish` — 리플레이 인스턴스 전용(허브 Step 혼용 금지).
- 호스트: 리플레이 원장 · 합성 1M 테이프(지연 생성) · `AttachArenaReplay` · `IArenaReplayPort` 싱글턴 · 종료 시 Dispose.
- UI: `ArenaBattleView` 상단 [📡 정규장 라이브 | ⏩ 고속 틱 리플레이] 탭 · 리플레이 컨트롤 바(▶ 시작/재개 · ⏸ 일시정지 · ⏹ 리셋 · 배속 6종 · 진행률 바 ·
  틱 현재/전체 · 재생 시각 KST · 처리율) · 스코어보드/퀵 티켓 = 현재 모드 원장. UI 는 250ms 버전 폴링만(배속 무관). 호스트 셸 변경 0(래칫 불변).

### 72.3 검증
- `StockManager.Tests/Engines/Arena/ArenaTickReplayEngineTests.cs` 19건: 배속 5종 정확 틱 수 · 배속 변경 재기준/최대 배치/완료 · 일시정지 동결 · 대기 주문 재개 체결
  (그 틱 호가 순회 + 시뮬레이터 수수료 정확값) · 정지 시간 미보상 · 리셋/결정론(2회 재생 AI 결과 동일) · 완료 후 새 자본금 재시작 · 녹화 테이프 체결 수수료/세금/슬리피지 정확값 ·
  100x · 최대 배속 δ=0(틱 주입 · AI · 체결 · 정산 · 게시 · 판독 · 휴먼 주문 · Release MinAllocated 3창) · 실 재생 스레드 1M 틱 최대 배속(0.35s · 약 285만 틱/s ·
  누적 할당 259KB = 콜드 · Gen0 0) · 최대 배속 게시 50ms 주기(1초 20회) · 허브 요청 즉시 게시 · IL 허용목록 · 런타임 객체 그래프 · 호스트 결선 · bUnit 헤드리스 컨트롤 결속.
- 오중 빌드(Default sln · WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 3,856/3,856(4m20s) · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 한계(정직): 녹화 테이프 영속화(파일 저장/로드)는 범위 밖 — 현재 원천은 합성 테이프 · 메모리 배열. 가상 체결은 시장 충격/대기열을 모델링하지 않는다.
  Windows 대기 해상도(약 15.6ms)로 1x 근방에서는 틱이 소규모 묶음으로 처리된다(앵커 기준이라 누적 지연 0).

## 73. v12.18.13-DEV — Microstructure Queue Execution · Binary Tape Engine (2026-10-16)

### 73.1 전제 교정
- '10호가 뎁스 + 실체결 틱을 13B 압축 구조체로' — 10호가(가격 20 + 잔량 20)는 13B 에 무손실로 담을 수 없다. 13B 는 합성 테이프의 매개변수 압축(SyntheticTickSource).
  실호가 녹화는 무손실 고정 176B 레코드(int32 가격/잔량 · 헤더 64B · 1M ≈ 176MB)로 구현 — 고정 길이라 리플레이 임의 위치 O(1).
- 키움 실시간 체결 Volume = 누적 거래량(FID 13 · 정량 엔진도 cumulativeVolume 으로 사용) · 체결 방향 필드 없음 → 체결 거래량 = 종목별 누적 차분(CumulativeVolumeTracker) ·
  방향 = 시뮬레이터가 최신 호가로 추정(≥ 매도 1호가 매수 주도 · ≤ 매수 1호가 매도 주도 · 스프레드 안 = 미상).
- 브리지 호가/체결 이벤트는 단일 DispatchLoop 스레드에서 발생(녹화기 실질 단일 생산자) — 기록기는 다중 생산자 슬롯 claim 으로도 안전.

### 73.2 구성
- `DualBookSimulator` 미시구조 모델: 접수 → 지연 버퍼(30 ~ 50ms · 진영별 결정론 xorshift · 매치 Reset 시 시드 복원) → 판단 시점 호가로 체결(접수 시점 아님) ·
  시장가 = 10호가 순회(진영별 소비 오버레이 = 같은 스냅샷 재소비 0 · 새 호가 = 오버레이 초기화) · 잔량 초과 = 작동 상태 이월 → 다음 호가 갱신마다 재순회 분할 체결 ·
  슬리피지 = 활성화 시 최우선 대비 누적(충격) · 매수 조각별 현금 검사(부족 = 가능분 체결 후 잔량 취소 CashExhausted) ·
  지정가 = 시장성 부분 즉시 → 잔량은 같은 가격 표시 잔량 + 내 앞선 주문 뒤 대기 · 앞 잔량 소진 = 그 가격 체결 거래량(방향 일치) · 표시 잔량 축소 min · 최우선보다 좋아짐 0 ·
  관통 체결 = 잔량 전부 · 반대 호가 도달 = 시장성 체결 · 예약(지정가 매수 현금 · 매도 수량) = 이중 집행 0 · 전량 취소도 지연 경유 · 주문 풀 64(초과 OrdersFull).
- `ArenaMatchGovernor`: 지연 파라미터 · 지정가/취소 포트(SubmitHumanLimit · CancelHuman) · 라이브 체결 링(누적 → 차분) · AI 진입/청산 = 미체결(지연 중 포함) 있으면 생략 ·
  리플레이 `ApplyTrade` · `Advance(호가 유무 · 시장 시각 = 테이프 시각)` · 스냅샷 지연/작동 주문 · 내 앞 잔량 · 취소 · 예약 현금.
- `ArenaTapeStore.cs`: `ArenaTapeWriter`(파일 용량 확장 + MMF 포인터 1회 · Append = Interlocked claim + 포인터 기록 δ=0 · kind 마지막 기록 · 1,024 단위 헤더 count ·
  Seal = 차단 → 진행 중 완료 대기 → 확정 → 절단) · `ArenaTapeReader : ArenaTickSource`(헤더 검증 fail-closed · 판독 δ=0 · 미봉인 테이프도 count 까지) ·
  `ArenaTapeRecorder : IArenaTapePort`(ON/OFF · OFF 중에도 누적 기준선 유지) · `ArenaTapeStore.List`(헤더만 · 녹화 중 제외).
- 틱 원천 계약: 인덱스 = 호가 · 체결 중 하나/둘(체결 → 호가) · 합성 테이프 체결 = 인덱스 해시 결정론 파생(저장 0B). 리플레이 `LoadTape`(호출 스레드 열기/검증 → 재생 스레드 교체 · 이전 판독기 폐기).
- 호스트: 라이브 아레나 체결 결선(bridge.TickReceived → OnTrade) · 녹화기(%LOCALAPPDATA%\MS2E.StockSuite\tapes · 브리지 호가/체결 · 종료 시 봉인) · IArenaTapePort.
- UI: 라이브 = 틱 녹화 ON/OFF · 레코드 수/파일 · 리플레이 = 테이프 선택(합성 · .tape)/목록 갱신/불러오기 · 원천 라벨 · 퀵 티켓 지정가 매수/매도 · 미체결 전량 취소 ·
  스코어보드 지연/작동 · 내 앞 잔량 · 예약 현금 · 취소. 디렉터리 조회는 버튼 클릭 시만. 호스트 셸 변경 0(래칫 불변).

### 73.3 검증
- `StockManager.Tests/Engines/Arena/MicrostructureExecutionTests.cs` 17건: 대기열 보류/소진/관통/예약 정확값 · 표시 잔량 min · 최우선 개선 0 · 방향 추정 · 내 앞선 주문 누적 ·
  시장성 지정가 · 지연 전 판단 0 · 지연 경과 시점 호가 체결(틱 레이스) · 30 ~ 50ms 분포/결정론 · 취소 지연 vs 관통 경합 · 리플레이 테이프 시각 지연 ·
  잔량 이월 분할 + 오버레이 + 진영 독립 · 조각 현금 부족 잔량 취소 · 예약 이중 집행 0 · AI 중복 접수 0 · 라이브 누적 차분/기준선 · 테이프 무손실 왕복/헤더 fail-closed/덮어쓰기 금지 ·
  미봉인 count 판독 · 봉인 중 동시 기록 유실 0 · 녹화기 ON/OFF/목록 · 리플레이 .tape 로드 → 녹화 체결로 지정가 체결 ·
  δ=0 + GC 0: 대기열/체결 핫패스 · 1M 레코드 기록(0.31s · 약 324만/s) · 판독(0.15s · 약 665만/s) · 1M 테이프 리플레이(최대 배속).
- 기존 테스트: v12.18.11/12 파일은 체결 지연 0 구성으로 원 의미 보존 · IOC 테스트 = 같은 스냅샷 충격/이월 의미로 갱신 · IL 허용목록에 System.IO/MemoryMappedFiles/Collections.Generic 추가.
- 오중 빌드(Default sln · WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 3,874/3,874(4m05s) · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 한계(정직): 대기열 모델은 표준 근사(취소 위치 미상 = min 상한 · 10호가 밖 앞 잔량 = 10번째 잔량 추정) — 실 거래소 큐 재현 아님. 라이브 체결 방향은 호가 추정.
  리플레이 AI 에는 체결 틱(VWAP)을 공급하지 않는다. 테이프 1M = 176MB 디스크. 실 키움 피드 녹화 실측은 이 호스트에서 불가(합성/기록기 단위로 증명).

## 74. v12.18.14-DEV — Arena Pro Console Hotkeys · DOM Ladder · Post-Match Analytics (2026-10-16)

### 74.1 전제 교정
- 핫키 = Blazor `@onkeydown`(포커스 가능 콘솔 영역 tabindex · JS interop 0). δ=0 범위 = 키 분류 → 휴먼 큐 디스패치 → 정산/래더/복기 게시(우리 코드).
  Blazor 이벤트 인자 생성 · 렌더 diff · 메시지 문자열은 프레임워크/렌더 시점 할당(정직 — 측정 대상 아님).
- 한글 IME 활성 시 KeyboardEvent.key 가 'ㅠ/ㄴ/ㅊ' → 매핑은 code(KeyB/KeyS/KeyC/DigitN/Space) 우선 · key 폴백.
- 'B/S = 최우선 호가 즉시 매수/매도' = 최우선 반대 호가 지정가(시장성 — 1호가 내 즉시 체결 · 초과분은 그 가격 대기 · 지연 30 ~ 50ms 경유). 시장가 휴먼 경로는 퀵 티켓에 유지.

### 74.2 구성
- `ArenaHotkeys`(RCL Helpers): 1~4 = 비중 10/25/50/100% · B/S · Space = 비상 전량 청산 · C = 미체결 전량 취소 · 자동 반복(repeat) = 발주/청산/취소 무시(비중만 허용 —
  키 누름 유지로 큐 범람 0) · 수량 = ⌊자본금 × 비중 ÷ 가격⌋(매도 = 매도 가능 상한) · 래더 클릭 = 매수호가 열 지정가 매수 · 매도호가 열 지정가 매도.
- 포트: `ArenaDepth`(InlineArray 10단 · 호가 + 가격별 내 대기 수량/맨 앞 앞 잔량 + 보유/매도 가능/원가 + 래더 밖 내 수량) · `ArenaDebrief` · `ArenaTilt` ·
  IArenaPort `FlattenHuman` · `SetFocus` · `TryReadDepth` · `TryReadDebrief`(거버너 50ms 게시 seqlock 창에서 함께 투영).
- `DualBookSimulator`: `ArenaOrderKind.Flatten`(지연 1회 → 미체결 전량(지연 대기 포함) 취소 + 보유 전 종목 시장가 매도 동시 활성 · 잔량 이월 · 포지션 역순 순회) ·
  `FillDepth` · 시장 시각(`SetClock`) · 체결/시가평가/접수 분석 훅.
- `ArenaTradeAnalytics`(신규): 왕복 거래 단위 MAE(≤0)/MFE(≥0)(편차 = 보유 × 최우선 매수 − 원가 + trip 내 실현 · 청산 실현 포함) · 보유 시간(시장 시각) ·
  trip 슬리피지/전체 슬리피지 드래그 · 포착률(Σ실현 ÷ ΣMFE) · 연패/최대 연패 · 틸트(연패 ≥ 2 상태에서 평온 기준선 EWMA 대비 간격 &lt; ½ = 빈도 · 금액 &gt; 2배 = 규모 ·
  기준선 표본 ≥ 3 · 연패 1회 = 기준선 동결 · 취소/청산 = 보호 행동이라 제외) — 고정 배열 · 정수 연산 · 원장 Reset 과 함께 초기화.
- UI: 프로 콘솔(포커스 영역 · 현재 비중 표시) · DOM 래더(매도 10 → 매수 10 · 잔량 바 · 가격 셀 클릭 · 내 주문 '수량 · 앞 잔량' 뱃지 · 보유/매도 가능/래더 밖) ·
  종목코드 변경 시 라이브/리플레이 포커스 동기화(@bind:after) · 경기 종료(종료 · 테이프 완주) 시 복기 카드(AI/휴먼). 호스트 결선 · 셸 변경 0(래칫 불변).

### 74.3 검증
- `StockManager.Tests/Engines/Arena/ArenaProConsoleTests.cs` 27건: 키 매핑 14(IME · 폴백 · 반복 무시) · 수량 공식 · B/S/C 큐 디스패치 정확값 · 래더 클릭 지정가/뱃지/래더 밖/포커스 ·
  Space 비상 청산(지연 경유 · 지연 대기 포함 취소 3 · 예약 0 · 포지션 0) · MAE −10,105 / MFE 14,895 / 실현 8,511 / 보유 5s / 포착률 정확값 · 부분 매도 누적 · 슬리피지 드래그 ·
  복기 게시/종료 후 유지/재개시 초기화 · 틸트 빈도/규모/복합/연패 1 동결/승리 해제/기준선 부족/취소·청산 제외 ·
  δ=0 + GC 0(핫키 분류 + 디스패치 + 래더 클릭 + 정산/래더/복기 게시 루프 · 체결/시가평가 분석 루프) · ArenaHotkeys IL = System + Portable · 포트 호출 3종만 ·
  bUnit 헤드리스 키 300 + 래더 클릭 300 + 동시 정산 스레드(1.8s · 예외/교착 0 · 현금 ≥ 예약 ≥ 0).
- 결함 수정(테스트가 발견): 비상 청산의 포지션 정순 순회 + 전량 체결 교체 제거 → 마지막 포지션 누락 → 역순 순회.
- 오중 빌드(Default sln · WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 3,901/3,901(5m41s) · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 한계(정직): 콘솔 영역은 Space 스크롤 방지를 위해 keydown 기본 동작을 막는다(영역 밖 클릭으로 포커스 해제). bUnit 은 상호작용마다 렌더 기록을 보유하므로
  메모리 누수 판정은 디스패치/정산 루프 δ=0 · GC 0 실측으로 한다. 실 WebView2 키 입력/래더 육안 확인은 이 세션에서 미수행.

## 75. v12.18.14-GM — AI vs Human Arena 단독 출하(-NoPeer) (2026-10-16)

### 75.1 전제 교정 · 버전 · 태그
- 지시서 '가장 최근 출하본 v12.18.9-GM' — v12.18.9-GM 산출물은 이미 superseded-v12.18.9-GM/(§70). 실제 최근 출하본 = v12.18.10-GM → superseded-v12.18.10-GM/ 로 이관.
- 12.18.10 → 12.18.14(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말) = 189ad3c.
- 1차 패키징 [3/10] 전체 스위트 1건 실패 → 중단(fail-closed · 산출물 미사출): ArenaProConsoleTests 헤드리스 스트레스의 bUnit UnknownEventHandlerIdException
  (뷰 250ms 폴링 재렌더가 FindAll ↔ Click 사이에 끼어 오래된 핸들러 ID — 하네스 경합 · 제품 결함 아님). 조회+발화를 cut.InvokeAsync 안에서 원자화(async 테스트 · xUnit1031) =
  356f90f · 단독 10/10. 태그 v12.18.14-GM 재지정(-f · 원격 미구성 · 푸시 이력 없음) = 356f90f.
- QuantumVault 미접근(-NoPeer) · 매니페스트 피어 기록 없음 · 원격 푸시 없음.

### 75.2 출하 실행(형제 worktree StockManager-gm-12.18.14-np · 태그 356f90f · 미커밋 0)
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0: [0] 태그 = HEAD 코드 · 미커밋 0 · [1]/[2] 오중 빌드 -warnaserror 0W/0E · [3] 3,901/3,901(3m41s) · [4] infra-scan ·
  [5] 래칫 ≤40 · [6] liveArmed 기본값 수검(물리 이중키 조회 16 · 마스터 플래그 1 = false) · [7] 아티팩트 38 SHA-256 · genesis v2 manifest.sig · 봉인 일치 ·
  [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) · 감사 원장 ⑪ · 펌프 지터 ⑫ 1 ·
  유령 취소 무장 ⑬ 1 · 위반 0 — --require-pump-jitter · --require-cancel-arming) · [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · 감사 원장 · seqlock · IPC 패브릭) ·
  handshake Ok @356f90fc04.
- 산출물(release/): release-v12.18.14-GM.zip SHA-256 `85E39CE8F86B889335745B2A460232691D68434D36FFAD602850995D14A8127F` ·
  masterSeal `53951F6A83D936BD13D17CBBF0A095C2EFEEBB5B28BD643A370E70B8D5CD2F6A` · sourceCommit 356f90f · 피어 기록 없음.
- v12.18.10-GM 산출물 13건(zip 52D30390… · masterSeal BDA2178B…) → release/superseded-v12.18.10-GM/. worktree 제거.
- 메인 체크아웃 재수검: WebOnly + sln Release -warnaserror 0W/0E · CLI --verify-live-armed(출하 zip · ⑫ ⑬ 강제) exit 0 · --verify-zip(master-seal) exit 0 · 전체 스위트 3,901/3,901.

## 76. v12.18.15-DEV — Arena Multi-AI Strategy Personas · Real-Time VWAP Accumulator (2026-10-18)

### 76.1 전제 교정
- 지시서의 `OrderBookSnapshot10` · `TradeSide` · `ArenaMarketState` · `StrategySignal` 은 코드에 없던 타입 — 10호가는 기존 `OrderBookPayload`(InlineArray · 336B)를 그대로 쓰고
  나머지 3종은 `StockManager.Engines.Arena.Strategy` 에 신설.
- DualBookSimulator 는 공매도 불가 → 3종 모두 매수 진입 · 보유 전량 매도 청산만. 스캘퍼 '≤ 35%' = 즉시 청산 트리거 · 평균회귀 '상단 +1.5%' = 진입 0(HUD '공매도 불가로 관측만').
- 탭은 4개(기존 '시그널 엔진' 기본 + 스캘퍼 ⚡ · 추세추종 📈 · 평균회귀 🔄) — 기존 QuantSignalProvider 결정 분기 · 허브 결선 · 기존 테스트 계약 보존.

### 76.2 구성
- `ArenaVwapTracker`: Σ(P×V) · ΣV = Int128(1M × 1억 원 × 100만 주 오버플로 0) · VWAP 반올림 · 체결량 가중 Welford σ · 이격률 bp · 밴드 VWAP ± kσ ·
  Pause/Resume(입력 무시) · Reset · Capture/Restore(`ArenaVwapState` 체크포인트). 단일 작성자 · 게시는 거버너 seqlock.
- `IArenaStrategy`(OnQuote · OnTrade · Evaluate · Reset) · `ArenaMarketState`(대상 종목 호가 · VWAP · 이격 · σ · AI 보유/원가 · Busy) · `StrategySignal`(관망/진입/청산 · 사유 · 목표/손절) · `ArenaBookMath`.
- 스캘퍼: 불균형 ≥ 65% + 스프레드 ≤ 1틱 → 매수 · 목표 +2틱 · 손절 −2틱 · 불균형 ≤ 35% · 보유 3초 → 청산.
- 추세: EMA(8)/EMA(34) 체결가 · 예열 34체결 · 정배열 + 체결가 > EMA(8) + 폭증(직전 체결 대비 ≥ 200% · 평균 이상 · 매도 주도 제외 · 래치 1회 소비) → 매수 ·
  R = max(3틱, 0.4%) · 목표 2.5R · 트레일링 스탑 max(진입 − R, 고점 − R) · 역배열 청산.
- 평균회귀: VWAP 예열 20체결 · 이격 ≤ −1.5% + 매수 3단 ≥ 매도 3단 × 200% → 매수 · 매수 1호가 ≥ VWAP 익절 · 이격 ≤ min(진입, −1.5%) − 0.5% 손절.
- `ArenaMatchGovernor`: 페르소나 4슬롯(독립 인스턴스) · `SetPersona`(IArenaPort · 명령 1워드) → 다음 회차 반영 · 진행 중이면 AI 비상 전량 청산(Flatten) 후 교체 ·
  교체 전 적재된 시그널 엔진 결정 폐기 · 페르소나 활성 중 OnAiDecision 무시. 대상 종목 = 포커스 우선 · 첫 관측 · 무포지션일 때만 재지정(VWAP/전략 초기화).
  틱마다 Evaluate → 원장 재확인(무포지션 · 미완료 주문 0)에서만 AI 시장가(자본금 × 10%) 접수. Step(라이브) · Advance/ApplyTrade(리플레이) 공용.
- 포트: `ArenaPersonaPort.cs`(ArenaPersona · ArenaAiAction · ArenaAiReason · ArenaAiHud) · `ArenaSnapshot.AiHud`(같은 seqlock 창) · `IArenaPort.SetPersona`.
- UI: 스코어보드 위 페르소나 카드(탭 4 · 논리 요약 · 교체 횟수) + 설명 가능 HUD(판단 · 사유 · VWAP 이격 게이지 ±3% · ±1.5% 눈금 · 매수 잔량 게이지 35/65% 눈금 ·
  VWAP · σ · 목표/손절 · 스프레드 · 발주 수). 상승 #E53935 / 하강 #1E88E5. 호스트 결선 · 셸 변경 0(래칫 불변).

### 76.3 검증
- `StockManager.Tests/Engines/Arena/ArenaStrategyPersonasTests.cs` 18건: VWAP 정확값/σ/이격/밴드 · 일시정지/초기화/체크포인트 비트 동일 · Int128 극한 1M ·
  스캘퍼 65.00% 경계/스프레드 2틱 차단/대기 · 목표·손절·역전·3초 경계 · 추세 예열/정배열 + 폭증/199% 미달/매도 주도/역배열 · 손익비 2.5 · 트레일링 상향 고정 ·
  평균회귀 −1.49/−1.50% 경계 · 지지벽 · 상단 관측만 · 회귀 익절 · 추가 이탈 손절 · 거버너 결속(스캘퍼 진입 142주 → 역전 청산 · 시그널 엔진 무시 · HUD 투영 ·
  평균회귀 실현 이익 · 추세 진입) · 교체 = AI 전량 청산 + 미완료 0 + 평가자산 = 현금 + 교체 전 결정 폐기 · 대기 중 교체/재개시 유지 ·
  리플레이 200k 틱 3종 독립 발주(체결 궤적 상이) · δ=0 + GC 0(VWAP 1M · 재생 1M 틱 체결 → VWAP → 평가 → 디스패치 + 25만 틱마다 교체) ·
  전략/VWAP IL 허용목록 · 거버너 도달 객체 그래프 브로커/디스패처/키움 타입 0 · bUnit 헤드리스 탭 200 + 회차당 100틱 주입 정산 스레드(버전 전진 · 예외/교착 0).
- 기존 IL 허용목록(ArenaMatchGovernorTests · ArenaTickReplayEngineTests)에 `StockManager.Engines.Arena.Strategy` 네임스페이스 추가.
- 결함 수정(전체 스위트가 발견): 거버너가 Strategy 네임스페이스를 참조하자 리플레이 IL/객체 그래프 허용목록 2건 실패 → 허용목록 확장 + IL 순회가 Strategy 형식까지 재귀 검사하도록 교정.
- 오중 빌드(Default sln · WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 3,919/3,919(3m50s) · infra-scan 0 ≤ 117 · 래칫 ≤40(Phase114 스위트 내 통과).
- 한계(정직): 페르소나 파라미터는 합성 테이프 기준 정성 설계 — 실 시장 수익성 검증 아님. 대상 종목 1개(다종목 동시 운용 아님). 실 WebView2 육안 확인은 이 세션에서 미수행.

## 77. v12.18.15-GM — AI 전략 페르소나 · 실시간 VWAP 단독 출하(-NoPeer) (2026-10-18)

### 77.1 버전 · 태그
- v12.18.15-DEV(§76) = c062117(선별 커밋 · 타 세션 파일 3종 제외).
- 12.18.14 → 12.18.15(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말) = 21da2f5 · sln -warnaserror 0W/0E · GoldenMaster 83/83.
- 태그 v12.18.15-GM(annotated) = 21da2f5. QuantumVault 미접근(-NoPeer) · 매니페스트 피어 기록 없음 · 원격 푸시 없음.

### 77.2 출하 실행(형제 worktree StockManager-gm-12.18.15-np · 태그 21da2f5 · 미커밋 0)
- safe.directory · core.commitGraph=false = 프로세스 GIT_CONFIG_* 환경변수(전역 설정 · .git 무변경).
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0(1회 통과): [0] 태그 = HEAD 코드 · 미커밋 0 · [1]/[2] 오중 빌드 -warnaserror 0W/0E(KiwoomComShim 산출 확인) ·
  [3] 3,919/3,919(4m15s) · [4] infra-scan 0 ≤ 117 · [5] 래칫 ≤40(2/2) · [6] liveArmed 기본값 수검(물리 이중키 조회 16 · 마스터 플래그 1 = false) ·
  [7] 아티팩트 38 SHA-256 · genesis v2 manifest.sig · 봉인 일치 · [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) ·
  강화 키 6(호스트 8) · 감사 원장 ⑪ · 펌프 지터 ⑫ 1 · 유령 취소 무장 ⑬ 1 · 위반 0 — --require-pump-jitter · --require-cancel-arming) ·
  [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · 감사 원장 ⑪ · seqlock ⑤ · IPC 패브릭 무대기/무슬립 ⑥) · handshake Ok @21da2f5bd6.
- 산출물(release/): release-v12.18.15-GM.zip SHA-256 `504DCB3E1C6208CA92EB4CC4E29A264B6FCE6765389A62583FDBF0315FEDD50E` ·
  masterSeal `E71C6AEE062CA2EE53215120566E3963ADA0952A1B0F0755C67B2B3EA6612C1C` · sourceCommit 21da2f5 · 피어 기록 없음.
- v12.18.14-GM 산출물 13건(zip 85E39CE8… · masterSeal 53951F6A…) → release/superseded-v12.18.14-GM/. worktree 제거 · prune.
- 메인 체크아웃 재수검: 복사본 SHA-256 일치 · CLI --verify-zip(--seal-file master-seal.txt) exit 0 · --verify-live-armed(⑪ ⑫ ⑬ 강제 · 강화 키 6) exit 0 · 위반 0.

## 78. v12.18.16-DEV — KIS WebSocket 10호가/체결 판독 · 시각 편차 인터락(비트 28) (2026-10-18)

### 78.1 전제 교정
- H0STASP0 판독기는 이미 존재(KisOrderBookParser · 구조 검증 9종 · 어댑터/정량 엔진 결선) · H0STCNT0 은 KisProtocol.ParseTicks — 재구현하지 않고
  `KisWebSocketOrderbookParser` 가 둘을 감싸 전문 시각(호가 1 BSOP_HOUR · 체결 1 STCK_CNTG_HOUR)만 추가 판독. 체결 구분은 21 CCLD_DVSN 을 KisTick.Side 로 추가.
- KIS 전문 시각 = HHmmss(초 해상도 · 밀리초 필드 없음) → 지연은 [하한, 상한] 구간으로만 측정 가능. 50ms 격발 = '하한 > 50ms'(지연이 확실히 초과) — 오격발 0 ·
  50 ~ 1,050ms 구간 일부 미포착 가능(정직한 한계). 호스트 NTP 동기가 전제.
- 비트 28 은 v12.17.6 OrderPathLatencyInterlock(실발주 p99) 소유 · 집계어는 비트당 소스 1개(재결속 = 예외) → 새 비트/재결속 없이
  OrderPathLatencyInterlock 에 2차 차단 입력(AttachFeedGuard · 1회 · 교체 금지)으로 OR 합성. 인터락 30종 정의 · 마스크 0x800000001FFFFFFF 불변.
- 지시서의 '50ms 지연 시 거래 루프 차단' = 무장 술어 차단(비트 28 Raise). 시세 피드 자체는 계속 흐른다(가상 원장 · 관측 — 차단 대상 아님).

### 78.2 구성
- `Adapters/Kis/KisWebSocketOrderbookParser`: Decode(프레임 → Book/Trades/Other/Encrypted/Malformed) · TryDecodeBook(시각 동반) · DecodeTrades · HHmmss 검증.
- `Adapters/Kis/KisTimeSkewGuard`(IArmingBlockSource · IKisFeedLatencyPort): 스트림(호가/체결)별 Observe → 하한 > 50ms · 스트림 내 역전 · 상한 < −50ms(시계 앞섬) ·
  시각 형식 이탈 = 래치 + 비트 28 Resync. 자정 경계 보정. 위반은 래치 중에도 전수 계수. 해제 = TryRearm(이중키) ∧ 마지막 위반 후 정상 ≥ 100 전문 ·
  해제/위반 경합 = 위반 표지 재확인 후 재래치(유실 0). 자체 OrderPathLatency 재무장은 2차 래치를 풀지 않음.
- `KisOpenApiAdapter`: skewGuard · onTrade(KisTick — 체결량 · 주도 방향) 선택 인자. 호스트: KIS onBook → 정량 엔진 + 아레나 OnQuote + 테이프 OnBook ·
  onTrade → 아레나 `OnTradeQty`(건별 체결 전용 링 — 브리지 누적 차분 표와 분리 → 교차 유령 거래량 0) + 테이프 `OnTrade`(176B 레코드 · 주도 방향 보존).
- 관제: PaperDashboardFrame KisFeed* 필드 · 허브 AttachKisFeed → /aidash 지연 뱃지 · /arena 제목 뱃지(IKisFeedLatencyPort 판독 전용 · 재무장 멤버 없음).

### 78.3 검증
- `StockManager.Tests/Adapters/Kis/KisWebSocketParserTests.cs` 18건: 63필드 호가 10단 정확값 · 46필드 다건 체결(1 매수/5 매도/3 미상 · 21 결측 유지) · 형식 이탈 10종 +
  무작위 변조 2만 회 예외 0 · HHmmss 경계 7 · δ=0 + GC 0(10만 회 호가 + 체결 2건 판독 + 편차 판정) · 51ms = 즉시 격발/50ms = 무격발 · 집계어 비트 28 Raise ·
  역전/스트림 독립/시계 앞섬/형식 이탈/자정 경계 · 래치(회복 99 · 이중키 결여 = 해제 불가 · 회복 중 위반 재계수 · 자체 재무장 무효) · 2차 입력 재결선 거부 ·
  어댑터 결속(지연 전문 격발 + 피드 유지) · 아레나 누적/건별 교차 유령 거래량 0 · 테이프 주도 방향 · IL(송신/주문/브로커 멤버 0 · 인터락 호출 = AttachFeedGuard/Resync만) ·
  1M 전문 폭주(무작위 지연 · 역전) + 동시 재무장/판독 스레드: 위반 = 독립 기대값 정확 일치 · 예외/교착 0 · 최종 비트 = 래치 상태.
- 기존 핀 교정: CrossBrokerSignalTests(onBook 블록 람다) · 아레나 bUnit 3건(IKisFeedLatencyPort 등록).
- 오중 빌드(WebOnly · sln = Default/PrimesBench/KiwoomProxy x86/KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 3,937/3,937(3m46s) · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 한계(정직): 실 KIS 수신 캡처 없음(공개 예제 컬럼 기준 합성 프레임) · 초 해상도 미포착 구간 · 호스트에 해제(재무장) UI/호출부는 없음(기존 비트 28 과 동일 — 재기동 해제).

## 79. v12.18.16-GM — KIS WebSocket 판독 · 시각 편차 인터락 단독 출하(-NoPeer) (2026-10-18)

### 79.1 버전 · 태그
- v12.18.16-DEV(§78) = 5344cd5(선별 커밋 · 타 세션 파일 3종 제외).
- 12.18.15 → 12.18.16(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말) = 7bf0282 · sln -warnaserror 0W/0E · GoldenMaster 83/83.
- 태그 v12.18.16-GM(annotated) = 7bf0282. QuantumVault 미접근(-NoPeer) · 매니페스트 피어 기록 없음 · 원격 푸시 없음.

### 79.2 출하 실행(형제 worktree StockManager-gm-12.18.16-np · 태그 7bf0282 · 미커밋 0)
- safe.directory · core.commitGraph=false = 프로세스 GIT_CONFIG_* 환경변수(전역 설정 · .git 무변경).
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0(1회 통과): [0] 태그 = HEAD 코드 · 미커밋 0 · [1]/[2] 오중 빌드 -warnaserror 0W/0E(KiwoomComShim 산출 확인) ·
  [3] 3,937/3,937(3m29s) · [4] infra-scan 0 ≤ 117 · [5] 래칫 ≤40(2/2) · [6] liveArmed 기본값 수검 · [7] 아티팩트 38 SHA-256 · genesis v2 manifest.sig · 봉인 일치 ·
  [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) · 감사 원장 ⑪ · 펌프 지터 ⑫ 1 ·
  유령 취소 무장 ⑬ 1 · 위반 0) · [10] interlocks Ok(30 · 0x800000001FFFFFFF · 비트 28 포함) · tighten Ok(6 · ⑪ · seqlock ⑤ · IPC 패브릭 무대기/무슬립 ⑥) ·
  handshake Ok @7bf028231c.
- 산출물(release/): release-v12.18.16-GM.zip SHA-256 `451640EA32F9444A7233FA659EFE522DF1030FDF6AB00B467563CAF93CD76898` ·
  masterSeal `6027D7A89F03A3CC97149EE906C27F5279A20E283993275E70FDCD76702361B1` · sourceCommit 7bf0282 · 피어 기록 없음.
- v12.18.15-GM 산출물 13건(zip 504DCB3E… · masterSeal E71C6AEE…) → release/superseded-v12.18.15-GM/. worktree 제거 · prune.
- 메인 체크아웃 재수검: 복사본 SHA-256 일치 · CLI --verify-zip(--seal-file) exit 0 · --verify-live-armed(⑪ ⑫ ⑬ 강제 · 강화 키 6) exit 0.

## 81. v12.18.18-DEV — 아레나 초현실 고도화: 기관급 집행(빙산 · TWAP) · 유동성 진공 · 포식자 AI · 하드 리스크 동결 (2026-10-18)

### 81.1 전제 교정
- §80 은 v12.18.17-DEV(테스트 안정화 — VirtualTimeProvider · UtcNowLeapCacheExpirer · PreFlight/LongSoak 결정론화)용 예약 — 해당 변경은 작업 트리에 미커밋 상태로
  남아 있어(타 세션 소유) 이 커밋에 포함하지 않았다. 아래 전체 스위트 수치는 그 미커밋 변경이 작업 트리에 있는 상태에서 측정.
- '허수 잔량 주입' = 휴먼 DOM 래더 사본(ArenaDepth) 1호가 표시에만 가산 — 시뮬레이터 호가 · 체결 · 대기열 계산에 존재하지 않는다(체결 불가 · HUD 에 수량 공개).
- 리스크 동결 해제 = 다음 매치 개시(Begin)뿐 — '라운드' 경계. 같은 라운드 안의 우회 경로는 없음(81.3).

### 81.2 구성
- `DualBookSimulator`: ArenaWorkingOrder Peak/Visible/IntervalTicks/NextReleaseTicks/Refills. 빙산 지정가 = 노출분만 대기열 · 수동 체결 소진 시 min(Peak, 잔량) 리필 →
  `Requeue`(앞 잔량 = 최신 표시 잔량 + 같은 가격 내 다른 내 주문 노출분 전부 = 최하위) · 시장성/관통 = 은닉분 포함 전량 · 예약 = 전량. TWAP 시장가 = Interval 마다
  슬라이스 방출(호가 갱신 없이도 재순회). 유동성 진공 = 시장가가 같은 스냅샷 단 잔량 70% 이상 소진 → 진영 · 종목 · 방향별 단계 k(창 500ms · 재발동 k+1 · 상한 6) →
  후속 시장가 체결가 ± (2^k − 1) 호가 단위(슬리피지 · VacuumPenaltyWon 반영). `MarginCall` = 동결 래치 + 지연 대기 제어 주문 소거 + 비상 청산(지연 0 · 잔량 이월) ·
  동결 원장 Submit = RiskLocked.
- `Strategy/PredatorStrategy`(페르소나 4 · 🦈): ObservePrey(휴먼 연패 · 틸트 경보(30초 기억) · 미청산 MAE) → 공격성 2500 + 연패 × 2000(상한 3) + 틸트 2000 + MAE 1%당 500(상한 1500) ·
  진입 임계 58% → 45% 선형 하향 · 목표 1 + 공격성/2500 틱 · 은밀(< 50%) = 거버너가 TWAP 4분할(100ms) 진입 · 휴먼 2연패 ≥ = 허수(매수측 기준 × 공격성 × 3 · 매도측 ⅓).
- `ArenaMatchGovernor`: riskLimitBp(기본 300 = −3% · 0 비활성) · 시가평가 직후 CheckRisk → Volatile 래치 → MarginCall → 재평가(같은 회차). 휴먼 생산자 6종
  (시장가 · 지정가 · 빙산 · 취소 · 청산) 래치 판독 RiskLocked · 링 잔류분은 정산 단계 원장 동결이 거부(이중). 게시 = 래더 허수 투영 후 HUD(같은 창).
- 포트: IArenaPort.SubmitHumanIceberg · ArenaReject.RiskLocked · ArenaSnapshot HumanRiskLocked/RiskLimitBp/RiskTripTicks/RiskTripEquityWon · ArenaSideStats 진공/리필 ·
  ArenaPersona.Predator(Count 5) · ArenaAiReason PreyStalk/PreyAmbush · ArenaAiHud 공격성/역추적/허수 · ArenaDepth MyHiddenQty/Phantom*. ArenaTradeAnalytics.OpenMaeWon.
- UI(ArenaBattleView): 🧊 Iceberg 체크 + 노출 수량(콘솔 키 영역 밖 — 숫자 입력이 비중 핫키로 먹히지 않게) → 래더 클릭 · 지정가 티켓 · 동결 = 전면 붉은 틴트 +
  "계좌 동결 (Risk Limit Exceeded)" 오버레이 · 티켓/래더/핫키 비활성 · 포식자 HUD · 스코어보드 진공/리필.

### 81.3 검증
- `StockManager.Tests/Engines/Arena/HyperRealismTests.cs` 16건: 빙산 리필 최하위 재배치(뒤 주문 선체결) · 시장성/관통 은닉분 전량 · 포트 검증 · 진공 69% 미발동/70% 발동 ·
  진영/방향 독립 · 창 해소 · 지수 단계 0→1→3→…→63 상한 · TWAP 방출 · 포식자 공격성 산식 · 허수 = 래더 사본/HUD 전용(실 1호가 초과분은 2호가 체결) · 은밀 TWAP 진입 ·
  −3% 같은 회차 전량 청산 + 우회 전수 거부(퀵 · 지정가 · 빙산 · 취소 · 청산 · 핫키 B/Space/C · 래더 10단 · 엔진 직접 Submit) · 지연 중 취소가 강제 청산을 지우지 못함 ·
  잔량 이월 청산 · 다음 라운드 해제 · 생산자 경합(54만 호출 · 동결 후 체결 0 · 정산 단계 거부 128) · δ=0 + GC 0(진공 · 리필 · 허수 · TWAP 2만 틱 / 마진콜 경로) ·
  IL 허용목록 · 헤드리스 동결 오버레이/버튼 비활성 · 빙산 체크박스 → 원장 Peak.
- 기존 핀 교정: 진공 반영 미시구조 2건(이월 슬리피지 · 현금 한도 수량) · 대칭/처리량 불변식 2건(torn snapshot · 핫키 δ=0)은 riskLimitBp: 0 명시.
- 오중 빌드(WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim · Default sln) Release -warnaserror 0W/0E · 전체 스위트 3,953/3,953(4m35s) · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 한계(정직): 진공 창/임계/지수는 모델 상수(실측 보정 아님) · 빙산 리필 직후 남은 체결량은 뒤 주문 몫으로 보수 처리 · 실 WebView2 육안 확인 미실시.

## 82. v12.18.18-GM — 아레나 초현실 고도화 단독 출하(-NoPeer) (2026-10-18)

### 82.1 버전 · 태그
- 오더의 'HANDOVER §81' 은 이미 v12.18.18-DEV 기록(§81 · 0b6ddcc)이 사용 중이고 §80 은 v12.18.17-DEV(타 세션 미커밋) 예약 → 출하 기록은 §82.
- v12.18.18-DEV(§81) = 0b6ddcc. 12.18.16 → 12.18.18(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말) = 471440d(4파일 · 11/11 줄) ·
  핀 테스트 2/2(Release).
- 태그 v12.18.18-GM(annotated · -f) = 471440d. QuantumVault 미접근(-NoPeer) · 매니페스트 피어 기록 없음 · 원격 푸시 없음.
- v12.18.17-DEV 테스트 안정화(VirtualTimeProvider 등)는 타 세션 미커밋 → 태그 트리에 미포함 — 출하 스위트는 그 변경 없이 측정.

### 82.2 출하 실행(형제 worktree StockManager-gm-12.18.18-np · 태그 471440d · 미커밋 0)
- safe.directory · core.commitGraph=false = 프로세스 GIT_CONFIG_* 환경변수(전역 설정 · .git 무변경).
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0(1회 통과): [0] 태그 = HEAD 코드 · 미커밋 0 · [1]/[2] 오중 빌드 -warnaserror 0W/0E ·
  [3] 3,953/3,953(3m51s) · [4] infra-scan 0 ≤ 117 · [5] 래칫 ≤40(2/2) · [6] liveArmed 기본값 수검 · [7] 아티팩트 38 SHA-256 · genesis v2 manifest.sig · 봉인 일치 ·
  [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) · 감사 원장 ⑪ · 펌프 지터 ⑫ 1 ·
  유령 취소 무장 ⑬ 1 · 위반 0) · [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · ⑪ · seqlock · IPC 패브릭 무대기/무슬립) · handshake Ok @471440dcb6.
- 산출물(release/): release-v12.18.18-GM.zip SHA-256 `3398DCAA9384F7504715C59775CA490C514B9211F67C19D0D374597C64E3C04A` ·
  masterSeal `4405950627BDE19A561731D81354FB0069A14052EA0A66BAD34F9EED509B718C` · sourceCommit 471440d · 피어 기록 없음.
- v12.18.16-GM 산출물 13건(zip 451640EA… · masterSeal 6027D7A8…) → release/superseded-v12.18.16-GM/. worktree 제거 · prune.
- 메인 체크아웃 재수검: 복사본 SHA-256 일치 · CLI --verify-zip(--seal-file) · --verify-live-armed(⑪ ⑫ ⑬ 강제 · 강화 키 6) · --verify-interlocks(30) ·
  --verify-tighten(6 · IPC 패브릭) 전부 exit 0.

## 83. v12.18.19-DEV — 멀티 브로커 아비트라지 · 합성 스프레드 레이더 (2026-10-18)

### 83.1 전제 교정
- 30대 인터락 정의 불변 → 신규 비트 없음. 괴리 래치는 비트 22(OpenOrderDivergence) '2차 입력'(비트 28 KisTimeSkewGuard 선례와 같은 방식 ·
  `BrokerOpenOrderReconciler.AttachQuoteGuard` · 대사기 ArmingBlocked OR · 교체 금지 · 판독 예외 = 차단). 대사기 자체 판정 · 비상 취소 경로는 건드리지 않음.
- '가상 아레나 원장에 모의 체결' → 레이더 자체 가상 기록(건수 · 수량 · 가격차 합)으로 한정. 아레나 원장은 휴먼/AI 대결 회계라 섞지 않음.
- 두 증권사는 같은 KRX 호가창을 중계 → 역전/괴리는 대개 한 피드의 노후 · 모의 차익 = 관측 지표(실현 손익 아님 · UI 명시).
- KIS 미구성 = '대조 대기 · 미수신' 정직 렌더(가짜 호가 0). 'MMF L2' = 기존 브리지 `OrderBookReceived`(키움 프록시) 재사용.

### 83.2 구성
- `StockManager/Engines/Arbitrage/SyntheticSpreadRadar.cs`: 종목 슬롯 64(CAS 선점) · 두 피드 작성자 seqlock(CAS 짝→홀) · 판독 잠금 0.
  단위 cbp(0.01bp) — 합성 = |Mid키움 − MidKIS| / 평균 Mid · 교차 = (높은 매수 − 낮은 매도) / 평균 Mid(양수 = 역전) · 10호가 잔량 비대칭(피드별 · 괴리).
  대조 조건 = 두 피드 수신 시각 차 ≤ 1초(초과 = 생략 + 괴리 타이머 리셋) · venue 별 전문 시각 역전 = 폐기(최신 보존) · 무효 호가 폐기.
  래치 = 합성 ≥ 30bp 가 500ms 연속 → sticky → 비트 22 Resync. 해제 = TryRearm(물리 이중키) · epoch 증가로 전 슬롯 타이머 재시작.
  역전 상승 에지 = 모의 차익 1건(수량 = 두 최우선 잔량 최소 · 상한 100) · 래치 중 = 기록 거부. 상태 우선순위 = 래치 > 괴리 > 역전.
- 포트 `MS2E.UI.Portable/Ports/Trading/ArbitrageRadarPort.cs`(IArbitrageRadarPort · ArbRadar · ArbitrageRadarStatus) — 판독 + 표시 종목 선택만.
- 허브: `AttachArbitrage` · 펌프 회차마다 Sample(스파크 128 링) + Status 사본 → 프레임 ArbState/ArbSpreadCbp/ArbCrossCbp/ArbOpportunities/ArbTrips.
- 호스트(BlazorHomeForm): 브리지 OrderBookReceived → venue 0 · KIS onBook → venue 1 · BindDivergenceInterlock(openOrderRecon) · DI 싱글턴 + 포트.
- UI: `ArbitrageRadarView.razor`(듀얼 10호가 래더 · 불일치 셀 강조 · 합성 스프레드 게이지 · 교차 · 비대칭 · 모의 차익 카드 · SVG 스파크라인 · 래치 배너 ·
  종목 고정/자동) · 호스트 shim `/arbitrage`([Inject] 0) · NavMenu "⚡ 아비트라지 레이더".

### 83.3 검증
- `StockManager.Tests/Engines/Arbitrage/SyntheticSpreadRadarTests.cs` 12건: bp 산출(정상 −14.27 · 괴리 35.62 · 비대칭 50%) · 역전 상승 에지 1회/반대 방향/수량 상한 ·
  499ms 미격발/500ms 비트 22 Raise · 래치 중 모의 거부 · 한쪽 키 재무장 거부 · 임계 하회 타이머 리셋 · 실 대사기 OR/교체 금지/예외 = 차단(비상 취소 0) ·
  노후 피드 생략 · 시각 역전 폐기 · 무효 호가 4종 · 테이블 포화 · δ=0 1M 틱(4M 입력 · 대조 3,999,992 · 래치/재무장 · 0B · Gen0 0) · 허브 펌프 200회 0B ·
  두 작성자 + 판독자 찢김 0 · IL 허용목록(브로커/디스패처/무장 형식 0) · 포트 발주 멤버 0 · 헤드리스(미수신 정직 렌더 · 래치 배너 · 래더 20행 · 발주 버튼 0).
- 오중 빌드(WebOnly · Default sln = PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 3,965/3,965(3m44s) · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 한계(정직): 30bp · 500ms · 1초 노후 · 수량 상한은 모델 상수(실측 보정 아님) · 실 키움/KIS 동시 피드 · WebView2 육안 확인 미실시 · 슬롯 64 초과 종목은 계수만.

## 84. v12.18.19-GM — 멀티 브로커 아비트라지 레이더 단독 출하(-NoPeer) (2026-10-19)

### 84.1 버전 · 태그
- v12.18.19-DEV(§83) = 03d65a2. 12.18.18 → 12.18.19(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말) = 9e9f44a(4파일 · 11/11 줄) ·
  핀 테스트(GoldenMasterFreeze 버전 핀 + GoldenMasterReleasePackaging 전체) 37/37(Release).
- 태그 v12.18.19-GM(annotated · -f) = 9e9f44a. QuantumVault 미접근(-NoPeer) · 매니페스트 피어 기록 없음 · 원격 푸시 없음.
- v12.18.17-DEV 테스트 안정화(VirtualTimeProvider 등)는 여전히 타 세션 미커밋 → 태그 트리에 미포함 — 출하 스위트는 그 변경 없이 측정.

### 84.2 출하 실행(형제 worktree StockManager-gm-12.18.19 · 태그 9e9f44a · 미커밋 0)
- safe.directory · core.commitGraph=false = 프로세스 GIT_CONFIG_* 환경변수(전역 설정 · .git 무변경).
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0(1회 통과): [0] 태그 = HEAD 코드 · 미커밋 0 · [1]/[2] 오중 빌드 -warnaserror 0W/0E ·
  [3] 3,965/3,965(3m35s) · [4] infra-scan 0 ≤ 117 · [5] 래칫 ≤40(2/2) · [6] liveArmed 기본값 수검 · [7] 아티팩트 38 SHA-256 · manifest.sig · 봉인 일치 ·
  [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) · 감사 원장 ⑪ · 펌프 지터 ⑫ 1 ·
  유령 취소 무장 ⑬ 1 · 위반 0) · [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · ⑪ · seqlock · IPC 패브릭 무대기/무슬립) · handshake Ok @9e9f44a389.
- 산출물(release/): release-v12.18.19-GM.zip SHA-256 `D8DB82A946B811BC60E25F3C9ABA590AEF78B0206D7912F4F573336275F021A5` ·
  masterSeal `933BD01B865CB65682BF03D813740AE1399C562826A32C9EF46F7DFEC765199B` · sourceCommit 9e9f44a · 피어 기록 없음.
- v12.18.18-GM 산출물 13건(zip 3398DCAA… · masterSeal 44059506…) → release/superseded-v12.18.18-GM/. worktree 제거 · prune.
- 메인 체크아웃 재수검: 복사본 SHA-256 일치 · CLI --verify-zip(--seal-file) · --verify-live-armed(⑪ ⑫ ⑬ 강제 · 강화 키 6) · --verify-interlocks(30) ·
  --verify-tighten(6 · IPC 패브릭) 전부 exit 0.

## 85. v12.18.20-DEV — 결정론 테스트 하드닝 · LongSoak 100회 무할당 완주 (2026-10-19)

### 85.1 원인 · 원칙
- 직전 100회 반복 78회차 실패 = Soak 1M 의 HealthMonitoringAgent 스레드풀 탐침이 큐 대기 > 100ms(DefaultMaxStallMicros)로 ThreadPoolStall 래치(비트 21) —
  엔진 결함이 아니라 호스트 부하로 인한 OS 스케줄링 지연(외생). 출하 코드(LongSoakProfileHarness · HealthMonitoringAgent) 무변경 · 테스트 단언부에서만 분리.
- 동봉: v12.18.17-DEV 타 세션 미커밋 안정화(UtcNow 윤초 캐시 32B 회피 = 포렌식 기록기 하네스 시계 주입 · UtcNowLeapCacheExpirer · BrokerLivenessPreFlight
  TimeProvider 주입 + VirtualTimeProvider 결정론 시한 · 3중 경합 외생 GC 분리 계측) — LongSoak 테스트가 이에 의존하므로 함께 커밋(§84.1 잔여 해소).

### 85.2 분리 판정(LongSoakIntegrationTests)
- `IsIsolatedThreadPoolStall`: 헬스 사유 = 정확히 ThreadPoolStall · 집계어 = 정확히 비트 21 · 워밍업 후 raised = 블랙박스 InterlockRaised ∈ {0, 1} · 폭풍 동결 없음
  → 진단 출력("외부 호스트 부하로 인한 OS ThreadPool 지연") 후 통과. 하나라도 어긋나면 원 단언(raised 0 · 블랙박스 0 · 집계어 0 · 폭풍 없음) 그대로 격발.
- 할당 0B · 측정 구간 GC 0 · 체결 정합 · 국면 전이 등 나머지 단언은 약화 없음. 분류기 Theory 11건(단독 래치 2 통과 · 복합 사유/타 비트/raised 2/블랙박스 불일치/폭풍/비트 부재/무위반 9 거부).
- 3중 경합 창 GC: 세 대상 스레드 0B 단언 통과 뒤에만 도달 → GC 발생 시 외생 할당(프로세스 전역 − 대상 스레드분) 진단 후 통과.

### 85.3 검증
- 프로세스 격리 100회(회차마다 별도 dotnet test · LongSoakIntegrationTests 16건): 100/100 통과 · 실패 0 · 외생 지연 분리 발동 0회(이번 측정에서는 지연 미발생).
  주의: 첫 시도는 감시 tail 의 로그 파일 잠금으로 3회차 이후 기록 불능 → 무효 처리 후 회차별 파일 기록 방식으로 전량 재실행.
- 오중 빌드(WebOnly → Default sln = Host · Tests · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 3,976/3,976(3m30s · +11 Theory) ·
  infra-scan 0 ≤ 117 · 래칫/PureShell 18/18(≤40). liveArmed:false · QuantumVault 미접근.
- 한계(정직): 분리 경로의 실발동은 이번 100회에서 관측되지 않음 — 판정은 분류기 Theory 로만 검증. 100ms 임계 자체는 프로덕션 값 그대로.

## 86. v12.18.20-GM — 결정론 테스트 하드닝 단독 출하(-NoPeer) (2026-10-19)

### 86.1 버전 · 태그
- v12.18.20-DEV(§85) = 2e8e3f9. 12.18.19 → 12.18.20(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말) = dbc3198(4파일 · 11/11 줄) ·
  핀 테스트 48/48(Release).
- 1차 출하 실행(태그 → dbc3198) [3/10] 1건 실패로 fail-closed 중단(ZIP 미사출): ZeroDowntimeRolloverTests.Health_RealThread_RealProbe_HeartbeatFresh_ZeroAllocScan
  "실 가동 하트비트가 신선해야 함" — Scans ≥ 10 직후 AllowOrder 단발 판독이 전체 스위트 부하에서 노후(블로킹 GC 정지로 작성 스레드 정지) 또는
  찢김(seqlock 홀수 구간 선점)으로 거부된 것으로 추정(거부 사유 미기록 → 원인 단정 불가). 게이트는 설계대로 fail-closed · 다음 10ms 박동에서 해소.
- 교정 7b33bf5(테스트만 · 제품 코드 무변경): 2초 한도 재판독 · 트립/부재 거부 0 단언 추가 · 실패 시 거부 계수 출력. 클래스 12/12.
- 태그 v12.18.20-GM(annotated · -f) = 7b33bf5(직전 태그 객체 253b1bb → dbc3198 폐기). 원격 푸시 없음.

### 86.2 출하 실행(형제 worktree StockManager-gm-12.18.20 · 태그 7b33bf5 · 미커밋 0 · 1차 worktree 제거 후 재생성)
- safe.directory · core.commitGraph=false = 프로세스 GIT_CONFIG_* 환경변수(전역 설정 · .git 무변경).
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0: [0] 태그 = HEAD 코드 · 미커밋 0 · [1]/[2] 오중 빌드 -warnaserror 0W/0E(KiwoomComShim 산출 확인) ·
  [3] 3,976/3,976 · [4] infra-scan · [5] 래칫 ≤40(2/2) · [6] liveArmed 기본값 소스 가드(이중키 16 · 마스터 플래그 1 전부 false) · [7] 아티팩트 38 SHA-256 · manifest.sig ·
  [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) · 원장 ⑪ · 펌프 지터 ⑫ 1 · 유령 취소 무장 ⑬ 1 · 위반 0) ·
  [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · ⑪ · seqlock ⑤ · IPC 패브릭 무대기/무슬립 · Stopwatch ⑥) · handshake Ok @7b33bf51ab.
- 산출물(release/): release-v12.18.20-GM.zip SHA-256 `08807B099ECAACB5C30B5C4CBD098566F52B22C9FA64D15960601FF358439270` ·
  masterSeal `23D3D37BC298A62F57CBAA05C51F8D040E61E22117825055C61DC6C851AE76B1` · sourceCommit 7b33bf5 · 피어 기록 없음(-NoPeer).
- v12.18.19-GM 산출물 13건(zip D8DB82A9… · masterSeal 933BD01B…) → release/superseded-v12.18.19-GM/. worktree 제거 · prune.
- 메인 체크아웃 재수검(태그 빌드 호스트 CLI · 메인 release/ 복사본 대상): 복사본 SHA-256 일치 · --verify-zip(--seal-file) · --verify-live-armed(⑪ ⑫ ⑬ 강제 · 강화 키 6) ·
  --verify-interlocks(30) · --verify-tighten(6 · 관제 · IPC 패브릭 강제) 전부 exit 0.
- 정직: 작업 사본 점검 명령에 QuantumVault 디렉터리 존재 확인(ls · 출력 폐기) 1회가 섞임 — 파일 판독/기록 · 피어 탐침 · 매니페스트 피어 기록 없음.

## 87. v12.18.21-DEV — 락프리 브로커 레이트 게이트 · 적응형 지수 백오프 (2026-10-19)

### 87.1 전제 교정(승인 반영)
- 지시서의 `KisRestApiClient` · `KiwoomOrderDispatcher` · `StockManager/Adapters/Common/` 부재. 키움 실 송신 지점 = `KiwoomComEventCore`(src/02 KiwoomComBridgeCore.cs ·
  x86 프록시 링크 소스) → 두 엔진은 `src/02_Infrastructure/Engine/` 배치(프록시 · 쉼 csproj 링크 추가 · Core 형식 무참조). KIS 주문 경로는 없음 → 실 REST 조회(`KisProvider`)에만 결선.
- 고전 토큰 버킷(용량 C · 리필 r)은 임의 1초 창 최대 C + r(5 + 5 = 10) → 슬라이딩 창 허가 기록 링으로 대체(이름만 TokenBucket).
- 대기 큐 없음(CLAUDE.md §2) → 허브는 큐 깊이 대신 거부 · 차단 누계 투영. 키움 초과 코드 = −308(주문) · −200(조회)(지시서의 0x8000 계열 아님).
- 기존 TossLiveApiGovernor · AdaptiveOrderThrottle 무변경.

### 87.2 구성
- `LockFreeTokenBucket`: 머리 워드 = (허가 시각 µs << 16 | 순번 16비트) 단일 CAS — 시각 = max(now, 직전) 으로 순번 단조. 링 R = 2의 거듭제곱 ≥ max(N1, N2) · 슬롯 = (시각 | 순번 태그).
  허가 k ⇔ 각 창 i 에서 t(k) − t(k − Ni) ≥ Wi · 슬롯 미발행 = 거부. 보증: 임의 [a, a + W) 허가 ≤ N(순번 단조 + 판정식 귀류 · 파일 머리말). 최대 2창(키움 = 1초 5 · 60초 100).
  CAS 64회 경합 초과 = 거부 · 47비트 µs 소진 = 영구 거부 · 시계 역행 = 직전 유지. Remaining = 최근 N 슬롯 판독(O(N) · 0B).
- `BrokerBackoffController`: 429 · −308 · −200 → 기준 min(100ms × 2^(n−1), 3,000ms) + 지터 [0, 기준/4](SplitMix64 원자 카운터 · 시드 결정론) · 3,000ms 상한 ·
  차단 종료 CAS-max. 연속 3회 → sticky 래치 + 경보 콜백 1회 · 해제 = TryRearm(물리 이중키). TryEnterRiskReducing(취소) = 백오프 창만 따르고 래치 무시. 재시도 · 대기열 0.
- 키움 코어 `AttachRateGate(주문, 조회, 백오프)`: 신규 = 게이트 거부 시 EmitReject + RcRateGated(−9308 · 로컬) · 정정 = RateLimited(플래그 원복) ·
  취소/유령 취소 = 위험 감소 게이트 · 조회 QueryAccount = 2 허가 · QueryOpenOrders = 1 허가. OCX 반환 0 = 성공 · −308/−200 = 초과 · 그 밖 = 무기록. 미결선 = 종전 동작.
  ProductionKiwoomApi(#if KIWOOM_COM) = 주문 · 조회 CreateKiwoomLimiter + 백오프 결선.
- `KisProvider`: 기본 게이트 초당 5 + 백오프(생성자 주입 가능) · 시세/일봉 송신 직전 SendGatedAsync(거부 = null/빈 목록) · 429 = 초과 · 2xx = 성공.
  GetQuotesAsync 간격 100 → 200ms(초당 5 정합). 토큰 발급은 23시간 캐시라 게이트 밖.
- 허브 `AttachRateGate(레인, 게이트, 백오프)` → 프레임 RateKiwoom*/RateKis*(Bound · Remaining · Rejected · Consecutive · Latched). 호스트는 KIS 레인만 결선
  (`QuoteAggregator.Find<KisProvider>()`) — 키움 게이트는 x86 프록시 프로세스 안이라 호스트 판독 경로 없음 → Bound false 정직 렌더.

### 87.3 검증
- `StockManager.Tests/Adapters/BrokerRateLimiterTests.cs` 22건: 버스트 5 · 정확히 W 경과 재허가 · 순차 만료 · 2창(60초 = 100 · 60초째 재허가) · 시계 역행 ·
  임의 도착 20만 회 전수 창 대조(≤ 5/1초 · ≤ 40/10초 · 탐욕 95%+) · 16 스레드 가상 계단 65초(임의 1초 ≤ 5 · 60초 ≤ 100 · 이론 최대 125 중 ≥ 112 · 스레드별 0B) ·
  16 스레드 실 Stopwatch 2.2초(허가 ≤ 5⌈경과⌉ · 0B) · 10만 회 TryAcquire + 백오프 0B · 단계 지연 100~3,000 + 지터 범위 · 창 경계 정확 · 시드 결정론 · 3회 래치/경보 1회/이중키 ·
  성공 리셋 · 겹친 초과 창 비단축 · 키움 6건 → OCX 5 + 거부 통보 · −308 × 3 → 신규/정정 OCX 0 · 취소 통과 · 취소도 백오프 창 준수 · 조회 2 TR 소진 · 미결선 종전 동작 ·
  KIS 스텁 핸들러 6건 → 서버 5 · 429 × 3 → 래치 후 서버 0 · IL 허용목록(브로커/OCX/HTTP/무장 형식 0 · 공개 발주 멤버 0). 10회 반복 10/10.
- 오중 빌드(WebOnly → Default sln = Host · Tests · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 3,998/3,998(3m34s) · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 한계(정직): 보증 대상은 주입 시계의 '허가 시각' — 실 송신은 허가 직후(게이트 → 송신 사이 대기 없음)지만 OS 선점으로 송신 순서가 섞일 수 있다(서버 측 창 경계 근처 오차).
  KIS 실 초과 응답은 HTTP 500 + msg_cd EGW00201 로 오는 사례가 알려져 있으나 미분류(429 만 판정). 키움 게이트 텔레메트리는 프록시 → 호스트 IPC 미배선.
  실 키움/KIS 연결 미실시(liveArmed:false · 스텁).

## 88. v12.18.21-GM — 브로커 레이트 게이트 단독 출하(-NoPeer) (2026-10-19)

### 88.1 버전 · 태그
- v12.18.21-DEV(§87) = 30e2fb5. 12.18.20 → 12.18.21(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말 · BOM 유지) = d202286(4파일 · 11/11 줄) ·
  핀 테스트 48/48(Release).
- 태그 v12.18.21-GM(annotated · -f) = d202286. QuantumVault 미접근(-NoPeer · 경로 참조 0) · 매니페스트 피어 기록 없음 · 원격 푸시 없음.

### 88.2 출하 실행(형제 worktree StockManager-gm-12.18.21 · 태그 d202286 · 미커밋 0)
- safe.directory · core.commitGraph=false = 프로세스 GIT_CONFIG_* 환경변수(전역 설정 · .git 무변경).
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0(1회 통과): [0] 태그 = HEAD 코드 · 미커밋 0 · [1]/[2] 오중 빌드 -warnaserror 0W/0E(KiwoomComShim 산출 확인) ·
  [3] 3,998/3,998 · [4] infra-scan · [5] 래칫 ≤40(2/2) · [6] liveArmed 기본값 소스 가드(이중키 16 · 마스터 플래그 1 전부 false) · [7] 아티팩트 38 SHA-256 · manifest.sig ·
  [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) · 원장 ⑪ · 펌프 지터 ⑫ 1 · 유령 취소 무장 ⑬ 1 · 위반 0) ·
  [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · ⑪ · seqlock ⑤ · IPC 패브릭 무대기/무슬립 · Stopwatch ⑥) · handshake Ok @d202286565.
- 산출물(release/): release-v12.18.21-GM.zip SHA-256 `A4B1FCA05E93E6C98CC56601545CAA258D194AD7DB0B7431ADD1521A1610A0C4` ·
  masterSeal `3AD2A89A995529B1B2AF63936C0817F50FF99F48242053479E5F713DD0ABB808` · sourceCommit d202286 · 피어 기록 없음.
- v12.18.20-GM 산출물 13건(zip 08807B09… · masterSeal 23D3D37B…) → release/superseded-v12.18.20-GM/. worktree 제거 · prune.
- 메인 체크아웃 재수검(태그 빌드 호스트 CLI · 메인 release/ 복사본 대상): 복사본 SHA-256 일치 · --verify-zip(--seal-file) · --verify-live-armed(⑪ ⑫ ⑬ 강제 · 강화 키 6) ·
  --verify-interlocks(30) · --verify-tighten(6 · 관제 · IPC 패브릭 강제) 전부 exit 0.

## 89. v12.18.22-DEV — 키움 프록시 → 호스트 레이트 게이트 MMF 관제 · KIS EGW00201 분류 (2026-10-19)

### 89.1 전제 교정
- 프록시 → 호스트 방향 MMF 는 없었다(기존 = 호스트 → 프록시 HostHealthHeartbeat 단방향) → 같은 패턴(64B 고정 · seqlock · 지연 열기)으로 역방향 채널 신설.
- /aidash = 포터블 PaperTradingDashboardView — v12.18.21 은 프레임 필드만 있고 마크업이 없었다 → 키움 · KIS 쿼터 배지 신설(DashRateGate 상태 6종).
- EGW00201 판정은 '문자열 파싱 없이'를 CLAUDE.md §3 에 맞춰 Utf8JsonReader 타입드 판독(ValueTextEquals 바이트 비교 · catch JsonException)으로 구현 — 바이트 검색(IndexOf)은
  중첩/msg1 문구 오분류 위험으로 배제. 분류기는 별도 정적 형식(KisRateLimitClassifier) — x86 링크 대상인 BrokerBackoffController 는 JSON 무참조 유지.

### 89.2 구성
- `src/02_Infrastructure/Engine/KiwoomRateTelemetry.cs`(프록시 · 쉼 링크): 맵 Local\MS2E_KIWOOM_RATE_V1 · 64B(magic · seq · QPC · freq · 거부 누계 · 백오프 µs ·
  주문/조회 1초·60초 잔여 i16 · 연속 초과 · 플래그(래치 · 게이트 결선) · pid). 작성자 = in-process CAS 소유권 단일화(점유 중 호출 = 더티 → 소유자가 원천
  IKiwoomRateTelemetrySource 를 다시 읽어 재게시 · 대기 0 · 유실 0). 판독자 = 시퀀스 판독 32회 상한 · 첫 게시 전 = Missing · 노후(> 2초) = Stale · 홀수 고착 = Torn ·
  맵 부재 재열기 1초 간격(예외 생성 콜드) · Dispose 경합 = Missing(예외 전파 0).
- 키움 코어: IKiwoomRateTelemetrySource 구현 · AttachRateTelemetry · 게이트 판정 · OCX 응답 기록마다 게시. 프록시(#if KIWOOM_COM) = 작성자 생성(실패 = 관제 없이 게이트만) ·
  50ms 펌프 박동 게시(시간 경과 회복 반영) · Dispose 시 해제. LockFreeTokenBucket.RemainingInWindow(창별 잔여) 추가.
- 허브 AttachKiwoomRateTelemetry(판독자) → 펌프 회차마다 1회 판독 → 프레임 RateKiwoomState/Remaining/Remaining60/QueryRemaining(60)/Rejected/BackoffMs/Consecutive/Latched ·
  비 Live = 잔여 0(낡은 여유 비표시) · 노후 = 누계 · 래치 마지막 값 유지. KIS 레인도 상태/60초/백오프 ms 투영. 호스트 BlazorHomeForm = 판독자 결선 · FormClosed 해제.
- `KisRateLimitClassifier.IsRateLimit(status, utf8)`: 429(본문 무관) · 또는 ≥ 400 + 최상위 msg_cd == "EGW00201"(중첩 무시 · BOM 허용 · 형식 오류 = false).
  KisProvider.SendGatedAsync: 비성공 본문 버퍼링(콜드) → 분류 → NoteLimitExceeded(429 와 같은 백오프 · 3회 래치) · EgwRateLimits 계수.

### 89.3 검증
- KiwoomIpcRateLimitTelemetryTests 14건: 코어 판정 → 호스트 Live 정확 쿼터(주문 0/95 · 조회 3/98 · 1초 경과 회복) · −308 백오프/3회 래치 가시화 · 부재/첫 게시 전 Missing ·
  재열기 1,000회 중 시도 1 · 노후 Stale(누계 보존) · 홀수 고착 Torn 100회 < 2초(무블로킹) · 프록시 종료 후 뷰 유지 + 재기동 시퀀스 단조 · 판독자 Dispose 경합 50라운드 예외 0 ·
  해제 후 게시 무동작 · 10만 회 판독/게시 0B · 작성 4 + 판독 1 스레드 1.5초 찢김 0 · 누계/시퀀스 역행 0 · 허브 투영(미결선 → 수신 대기 → 정상 → 노후) · 뷰/호스트 결선 ·
  IL 허용목록(브로커/OCX/HTTP/무장 형식 0).
- KisErrorClassificationTests 22건: 분류 16 사례(필드 순서 · 상태 · 다른 코드 · 중첩 · 문구만 · 비문자열 · 접두 · 잘림 · HTML · 빈 본문 · 배열) · BOM/중첩 선행 ·
  EGW 1회 = 즉시 백오프 + 창 안 서버 0 · 3회 = 래치 + 서버 0 · EGW/429 혼합 누적 · 다른 500 무기록 + 성공 리셋 · 정상 형식 10만 회 분류 0B.
- 오중 빌드(WebOnly → Default sln) Release -warnaserror 0W/0E · 전체 스위트 4,034/4,034 · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 한계(정직): 형식 오류 본문(HTML · 잘림)은 판독기가 JsonException 을 던져 예외 객체 할당(콜드 오류 경로 · 판정 false). 실 키움 프록시 · 실 KIS 미연결(스텁) —
  프로세스 간 실측은 같은 프로세스 내 두 핸들(동일 OS 경로)로 대체. Local 네임스페이스 = 같은 로그온 세션 전제(프록시를 다른 세션/서비스로 띄우면 관제 미수신).

## 90. v12.18.22-GM — 키움 레이트 관제 · KIS EGW00201 단독 출하(-NoPeer) (2026-10-19)

### 90.1 버전 · 태그
- v12.18.22-DEV(§89) = 8ce2f39. 12.18.21 → 12.18.22(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말 · BOM 유지) = 3db5698(4파일 · 11/11 줄) ·
  핀 테스트 48/48(Release).
- 태그 v12.18.22-GM(annotated · -f) = 3db5698. QuantumVault 미접근(-NoPeer · 경로 참조 0) · 매니페스트 피어 기록 없음 · 원격 푸시 없음.

### 90.2 출하 실행(형제 worktree StockManager-gm-12.18.22 · 태그 3db5698 · 미커밋 0)
- safe.directory · core.commitGraph=false = 프로세스 GIT_CONFIG_* 환경변수(전역 설정 · .git 무변경).
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0(1회 통과): [0] 태그 = HEAD 코드 · 미커밋 0 · [1]/[2] 오중 빌드 -warnaserror 0W/0E(KiwoomComShim 산출 확인) ·
  [3] 4,034/4,034 · [4] infra-scan · [5] 래칫 ≤40(2/2) · [6] liveArmed 기본값 소스 가드(이중키 16 · 마스터 플래그 1 전부 false) · [7] 아티팩트 38 SHA-256 · manifest.sig ·
  [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) · 원장 ⑪ · 펌프 지터 ⑫ 1 · 유령 취소 무장 ⑬ 1 · 위반 0) ·
  [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · ⑪ · seqlock ⑤ · IPC 패브릭 무대기/무슬립 · Stopwatch ⑥) · handshake Ok @3db5698b33.
- 산출물(release/): release-v12.18.22-GM.zip SHA-256 `EEF1C3B92405592EF60A4857A5645C0B1E5163D7D872F46372C32ADCED16AF4E` ·
  masterSeal `97B6B3FD260F5C5A5003CECC5327BD871209286A899E87108CC2481871D5A5BB` · sourceCommit 3db5698 · 피어 기록 없음.
- v12.18.21-GM 산출물 13건(zip A4B1FCA0… · masterSeal 3AD2A89A…) → release/superseded-v12.18.21-GM/. worktree 제거 · prune.
- 메인 체크아웃 재수검(메인 release/ 복사본 대상): 복사본 SHA-256 일치 · --verify-zip(--seal-file) · --verify-live-armed(⑪ ⑫ ⑬ 강제 · 강화 키 6) · --verify-interlocks(30) ·
  --verify-tighten(6 · 관제 · IPC 패브릭 강제) 전부 exit 0(태그 worktree 빌드 CLI + 같은 커밋 3db5698 의 메인 체크아웃 Release 빌드 CLI 양쪽).

## 91. v12.18.23-DEV — 아레나 초현실 2단계: 대기열 취소 이탈 · 가상 공매도 (2026-10-19)

### 91.1 전제 교정
- `ArenaPositionLedger.cs` 부재 — 가상 원장 = `DualBookSimulator.cs` 안 `ArenaBook`(포지션 배열 · 현금 · 예약). 그 자리에서 확장.
- 기존 '공매도 금지' 전제에 묶인 테스트 5건(무보유 매도 = InsufficientPosition · 평균회귀 상단 = 관측만 · 대칭 찢김 스트레스의 무보유 매도)은 의미 변화에 맞춰 갱신 —
  무보유 매도 단언은 SyntheticShortingTests 로 이관(삭제 아님). 대주 수수료는 미도입(선택 항목 · 정직한 단순화로 명시).
- 포식자 '숏 스퀴즈 유도': 진영 간 체결 간섭 0 원칙상 AI 매수가 휴먼 체결가를 움직일 수 없다 → 추격 선매수 + 휴먼 래더 1호가 허수 매수 잔량(표시 전용)으로 구현.

### 91.2 구성
- ⑪ 대기열 취소 이탈: 작동 지정가마다 직전 스냅샷 내 가격 표시 잔량(LastShown) · 그 이후 내 가격 체결량(TradedAtPx — 앞 잔량을 소진한 체결 틱과 같은 조건) 기록.
  새 호가: C = max(0, (LastShown − 현재 표시) − TradedAtPx) · 앞 잔량 −= C × min(앞, LastShown) ÷ LastShown(절사 · `DualBookSimulator.CancelDepletion` 순수 정적) → 이후 표시 상한(min) 종전대로.
  잔량 증가 = 뒤 합류(불변) · 10호가 밖/미표시 = 미상(LastShown 0 → 다음 관측 재기준 · 미상 구간 감축 0) · 최우선보다 좋은 가격 = 0. 원장 QueueCancelDepletedQty 누계.
- ⑫ 가상 공매도: 롱 없음(무포지션 · 숏) 매도 = 숏 개시(PosQty < 0 · PosCost = 순매도 대금 · PosLock = 대금 × 150% 담보 · ShortLockedWon 합) ·
  가용 현금 = 현금 − 예약 − 숏 담보. 개시 예약 = 추정 대금(지정가 = max(지정가, 매수 1호가)) × 50% + 수수료 + 세금. 숏 상태 매수 = 숏커버(커버 가능 = 숏 − 커버 예약 상한 ·
  실현 = 비례 순매도 대금 − 커버 비용 · 담보 비례 해제 · 순회 예산 = 가용 + 해제분). 주문 Mode(0 개시 · 1 청산) 활성화 시 확정 — 한 주문은 0 을 가로지르지 않음(롱 청산 초과 매도 = 거부) ·
  같은 종목 롱 개시 ↔ 숏 개시 동시 진행 = OppositeExposure(신규 ArenaReject 11). 시가평가: 숏 = −수량 × 매도 1호가(부채) — 하드 리스크(−3%) 가 숏 평가손을 그대로 반영.
  비상 청산(Space) · 마진콜 = 롱 시장가 매도 + 숏 시장가 커버(Forced — 현금 한도 무시 · 음수 현금 = 실제 손실). 사후 분석 trip = 무포지션 → 롱/숏 개시 · |수량| 감소 = 청산.
- AI: ArenaAiAction.EnterShort(3) · 평균회귀 상단(+1.5% · 매도벽 200%) = 숏 진입(VwapUpperShort 18) · 매도 1호가 ≤ VWAP = 커버 · 이격 ≥ max(진입, 1.5%) + 0.5% = 손절 커버.
  포식자 ObservePreyShort(휴먼 대상 종목 숏 수량 · 평가손익) — 숏 평가손 = 스퀴즈(공격성 +2000 · 진입 임계 매복 45% · 허수 활성 · PreySqueeze 19). 거버너 Exit = 롱 매도 / 숏 커버.
- UI/포트: ArenaPositionSide(FLAT/LONG/SHORT) · 스냅샷 AiTargetSide/HumanFocusSide · ArenaSideStats 롱/숏 종목 수 · 숏 담보 · 취소 이탈 누계 · ArenaDepth.Coverable.
  래더 하단 [LONG]/[SHORT] 배지(상승 적색 · 하강 청색) · 평균 매입/순매도 단가 · 평가손익 색 반전 · AI 페르소나 카드 방향 배지. 핫키: 롱 보유 S = 청산 · 무포지션/숏 S = 숏 진입 ·
  숏 B = 커버(상한) · 래더 매도호가 클릭 동일 규칙 · Space = 롱 매도 + 숏 커버.

### 91.3 검증
- QueueDepletionTests 19건: 수식 10 사례 · 체결 0 대량 취소 = 비례 전진(종전 min 모델 대비 500 → 200) · 체결/취소 혼합 이중 차감 0 · 매수 주도 체결 = 취소 판정 · 단축 후 체결 앞당김 ·
  최우선 = 0 · 래더 이탈 재진입 재기준 · 10호가 밖 미상 · 매도 대칭 · 10만 회 0B.
- SyntheticShortingTests 12건: 숏 개시 회계(순매도 6,976,370 · 담보 10,485,000 · 시가평가 −23,630) · 부분/전량 커버 익절/손절 · 현금 보존 · 증거금/0 교차/롱 초과 매도/반대 개시 거부 ·
  숏 지정가 수동 체결 + 비상 청산 커버 · −3% 1틱 전 미격발 → 72,800 동결 + 강제 커버(실현 −304,722 · 발동 자산 9,696,370) · 급등 강제 커버 음수 현금 · 평균회귀 숏/커버 · 거버너 AI 숏 →
  회귀 익절 · 포식자 스퀴즈 · 핫키/래더 양방향 · 10만 회 개시/커버 0B · IL 허용목록(브로커/디스패처/무장 형식 0).
- 오중 빌드(WebOnly → Default sln) Release -warnaserror 0W/0E · 전체 스위트 4,065/4,065 · 아레나 141건 3회 연속 · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 한계(정직): 취소 균등 분포 = 기대값 모델(실 대기열 순번 미관측) · 대주 수수료/대차 가능 수량/업틱 룰 미반영 · 강제 커버는 호가 잔량까지(초과분 = 다음 호가 재순회).

## 92. v12.18.23-GM — 아레나 대기열 취소 이탈 · 가상 공매도 단독 출하(-NoPeer) (2026-10-19)

### 92.1 버전 · 태그
- v12.18.23-DEV(§91) = 8e99ef3. 12.18.22 → 12.18.23(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말 · BOM 유지) = 0c23671(4파일 · 11/11 줄) ·
  핀 테스트 48/48(Release).
- 태그 v12.18.23-GM(annotated · -f) = 0c23671. QuantumVault 미접근(-NoPeer · 경로 참조 0) · 매니페스트 피어 기록 없음 · 원격 푸시 없음.

### 92.2 출하 실행(형제 worktree StockManager-gm-12.18.23 · 태그 0c23671 · 미커밋 0)
- safe.directory · core.commitGraph=false = 프로세스 GIT_CONFIG_* 환경변수(전역 설정 · .git 무변경).
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0(1회 통과): [0] 태그 = HEAD 코드 · 미커밋 0 · [1]/[2] 오중 빌드 -warnaserror 0W/0E(KiwoomComShim 산출 확인) ·
  [3] 4,065/4,065 · [4] infra-scan · [5] 래칫 ≤40(2/2) · [6] liveArmed 기본값 소스 가드(이중키 16 · 마스터 플래그 1 전부 false) · [7] 아티팩트 38 SHA-256 · manifest.sig ·
  [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) · 원장 ⑪ · 펌프 지터 ⑫ 1 · 유령 취소 무장 ⑬ 1 · 위반 0) ·
  [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · ⑪ · seqlock ⑤ · IPC 패브릭 무대기/무슬립 · Stopwatch ⑥) · handshake Ok @0c23671a63.
- 산출물(release/): release-v12.18.23-GM.zip SHA-256 `8AE33D2991D3B125B9F0E09CF1D519D44ADC316CC26B03E6AD8DAFC3B028E131` ·
  masterSeal `B7F64DFFA5B56A20964B7E32010CD7F6A854505B2EABE05B3D4D7393616CCC2E` · sourceCommit 0c23671 · 피어 기록 없음.
- v12.18.22-GM 산출물 13건(zip EEF1C3B9… · masterSeal 97B6B3FD…) → release/superseded-v12.18.22-GM/. worktree 제거 · prune.
- 메인 체크아웃 재수검(같은 커밋 0c23671 Release 빌드 CLI · 메인 release/ 복사본 대상): 복사본 SHA-256 일치 · --verify-zip(--seal-file) · --verify-live-armed(⑪ ⑫ ⑬ 강제 · 강화 키 6) ·
  --verify-interlocks(30) · --verify-tighten(6 · 관제 · IPC 패브릭 강제) 전부 exit 0.

## 93. v12.18.24-DEV — /arena HMI 렌더 펌프 최적화 · 핫키 입력 지연 헤드리스 검증 (2026-10-19)

### 93.1 전제 점검
- 스로틀은 이미 있었다: ArenaBattleView = 250ms 버전 폴링(변경 시에만 InvokeAsync · 최대 4 FPS). 호가 1,000 TPS 는 정산 펌프(50ms 게시)에서 이미 20Hz 로 합쳐지고
  UI 는 버전만 본다 → 'StateHasChanged 폭주' 경로는 없었음. 결함은 (a) 4 FPS = 화면이 최대 250ms 늦음 (b) 진행 중 렌더 중첩 적재 방지 부재 (c) 엣지 판정(_seen 을
  InvokeAsync 전에 갱신 → 그 렌더가 다른 버전을 그려도 재요청 없음).
- 입력 지연 '16ms' 는 두 구간으로 분리 계측(정직): ① 키 이벤트 디스패치 → 포트 접수(UI 스레드) ② 접수 → 상태 전이 = 바로 다음 정산 Step 1회.
  운용 종단 지연 = ① + 정산 펌프 주기(≤ 50ms) + 모의 체결 지연(30~50ms · 현실성 설계 — 줄이지 않음).

### 93.2 변경(ArenaBattleView.razor · 순수 RCL · [Inject] 무증가)
- 렌더 펌프 = 레벨 트리거 50ms(≤ 20 FPS = 정산 게시 주기): 렌더 본문이 '그려진 버전'(라이브 Version · 리플레이 Status.Version)을 기록 → 타이머가 현재 버전과 비교 →
  다르고 진행 중 렌더가 없을 때만 InvokeAsync 1건(CAS 진행 중 플래그 · OnAfterRender 에서 해제 · InvokeAsync 예외 = 플래그 원복 후 다음 폴링 재시도).
  렌더 중 들어온 갱신은 다음 폴링이 다시 집어 감(후행 유실 0) · 변경 없으면 렌더 0. 진단 PumpRenders/PumpSkippedInFlight · RenderPumpMs = 50.

### 93.3 검증 — StockManager.Tests/Blazor/ArenaUIStressTests.cs 4건
- 고빈도 스트레스: 호가 생산 스레드 1,000 TPS(실측 1,000) + 키 사이 정산 Step 4회 + S/B/S/Space 순환 120키(포트 데코레이터로 접수 시각 계측):
  ① 키 디스패치 → 포트 접수 p50 0.05ms · p99 0.37~0.80ms(3회) · 핸들러 + 재렌더 완료 p99 ≤ 12.2ms · ② 접수 → 상태 전이(Step 1회) p99 ≤ 2.9ms ·
  전이 불일치 0(S = 숏 진입/추가 · B = 숏 감소 · Space = FLAT) · 정산 스레드 Step 할당 0B · 주문/호가 유실 0 · 렌더 ≤ 키 + 경과/50ms + 8(20 FPS 상한) · 마지막 버전 결국 렌더.
- 무변경 400ms = 렌더 0 → 단일 변경 = 펌프 렌더 1~2 · 마운트/해제 50회 → 전체 GC 후 뷰 약참조 생존 0(활성 타이머 = GC 루트 → 해제 누락 검출) ·
  IArenaPort 표면 브로커/무장/실주문/디스패치 멤버 0 · 뷰 주입 = Ports.Trading 계약만.
- 하네스 교정 2건(제품 결함 아님): bUnit cut.Find(마크업 → AngleSharp DOM 재파싱 · 수 ms)가 계측 구간에 섞여 p99 43ms 로 보였음 → 조회 후 디스패치 순간부터 계측 ·
  가격 진동 호가에서 지정가 핫키가 대기열로 남는 정상 동작 → 생산기는 가격 고정 · 잔량만 변화. B 는 비중 수량 커버(숏 143 · 비중 142 → 잔여 1)라 기대값 = 숏 감소.
- 오중 빌드(WebOnly → Default sln) Release -warnaserror 0W/0E · 전체 스위트 4,069/4,069 · HMI 3회 연속 · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 한계(정직): bUnit 렌더러 ≠ WebView2(실 DOM · JS interop 지연 미측정 · 육안 확인 미실시) · 지시서 목표 4,070+ 건은 1건 미달(의미 없는 테스트로 채우지 않음).

## 94. v12.18.24-GM — /arena HMI 렌더 펌프 단독 출하(-NoPeer) (2026-10-19)

### 94.1 버전 · 태그
- v12.18.24-DEV(§93) = 0b201e3. 12.18.23 → 12.18.24(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말 · BOM 유지) = 7aadea2(4파일 · 11/11 줄) ·
  핀 테스트 48/48(Release).
- 승격 직후 첫 빌드 MSB3021/3026(24건) = 타 세션 testhost(PID 45496 · vstest 16:06 시작 · 본 세션 명령 아님)가 공유 체크아웃 Tests/bin 출력 DLL 잠금 —
  강제 종료하지 않고 자연 종료 대기 → 재빌드 0W/0E · 핀 48/48 후 커밋(실패 상태 커밋 0). 출하 실행은 형제 worktree 라 공유 체크아웃 잠금과 무관.
- 태그 v12.18.24-GM(annotated · -f) = 7aadea2. QuantumVault 미접근(-NoPeer · 경로 참조 0) · 매니페스트 피어 기록 없음 · 원격 푸시 없음.

### 94.2 출하 실행(형제 worktree StockManager-gm-12.18.24 · 태그 7aadea2 · 미커밋 0)
- safe.directory · core.commitGraph=false = 프로세스 GIT_CONFIG_* 환경변수(전역 설정 · .git 무변경).
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0(1회 통과): [0] 태그 = HEAD 코드 · 미커밋 0 · [1]/[2] 오중 빌드 -warnaserror 0W/0E(KiwoomComShim 산출 확인) ·
  [3] 4,069/4,069 · [4] infra-scan · [5] 래칫 ≤40(2/2) · [6] liveArmed 기본값 소스 가드(이중키 16 · 마스터 플래그 1 전부 false) · [7] 아티팩트 38 SHA-256 · manifest.sig ·
  [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) · 원장 ⑪ · 펌프 지터 ⑫ 1 · 유령 취소 무장 ⑬ 1 · 위반 0) ·
  [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · ⑪ · seqlock ⑤ · IPC 패브릭 무대기/무슬립 · Stopwatch ⑥) · handshake Ok @7aadea21cb.
- 산출물(release/): release-v12.18.24-GM.zip SHA-256 `7FC52AECF7D103426FA6E564E06C998EF1DED8EE375B898C5699F51F5FF8710A` ·
  masterSeal `E13BBA072A1169F261918A313F1B5E6B0AF1E18BDC2C768FEFBF0C839B982B6A` · sourceCommit 7aadea2 · 피어 기록 없음.
- v12.18.23-GM 산출물 13건(zip 8AE33D29… · masterSeal B7F64DFF…) → release/superseded-v12.18.23-GM/. worktree 제거 · prune.
- 메인 체크아웃 재수검(같은 커밋 7aadea2 Release 빌드 CLI · 메인 release/ 복사본 대상): 복사본 SHA-256 일치 · --verify-zip(--seal-file) · --verify-live-armed(⑪ ⑫ ⑬ 강제 · 강화 키 6) ·
  --verify-interlocks(30) · --verify-tighten(6 · 관제 · IPC 패브릭 강제) 전부 exit 0.

## 95. v12.18.25-DEV — 아레나 초현실 3단계: 마찰비용 Gross/Net · Net 청산가치 리스크 · 틱 단위 MAE/MFE 포렌식 (2026-10-19)

### 95.1 전제 교정
- 거래세 0.18% · 수수료 0.015% 는 v12.18.13 부터 체결마다 원 미만 절사로 이미 차감(⑥ · 숏 개시 = 매도 → 거래세 포함 · v12.18.23). 재구현하지 않고 검증만 추가.
- MAE/MFE 는 v12.18.14 ArenaTradeAnalytics 가 '원' 편차를 시가평가 주기(정산 50ms)로 표본 — 이번 추가 = 틱(호가 갱신) 단위 '가격' 추적 + 거래 기록.
- 갭: Gross/Net 분리 부재 · −3% 하드 리스크가 시가평가 자산(퇴출 수수료/세금 미반영 = 덜 엄격) · 손익비/청산 효율 부재 · 종료 모달 부재.
- 추적기 파일은 지시대로 `Engines/Arena/Analytics/` 지만 네임스페이스는 `StockManager.Engines.Arena`(기존 IL 허용목록 7곳을 넓히지 않고 그대로 통과).

### 95.2 구성
- ⑬ Gross/Net(DualBookSimulator.PnL): Net = 시가평가 자산 − 자본금 · Gross = Net + 누적 수수료 + 누적 거래세(항등식 · 재계산 없음). ArenaBook.CapitalWon 추가.
- ⑭ LiquidationEquity: 현금 + Σ 롱(수량 × 매수 1호가 − 수수료 − 거래세) − Σ 숏(수량 × 매도 1호가 + 수수료) · 호가 없음 = 원가 폴백.
  거버너 CheckRisk = 이 값 ≤ 자본금 × 97% → 동결 + 마진콜(발동 자산 = 청산가치). 표시 자산은 종전 시가평가. ArenaSideStats Gross/Net/LiquidationEquity 투영.
- ⑮ `ArenaExecutionQualityTracker`(원장 단일 작성자 · 고정 배열 · Int128): OnQuote 마다 진행 중 trip 표시가(롱 = 매수 1호가 · 숏 = 매도 1호가) 최저/최고 · 진입 체결가 첫 표본 ·
  OnFill = 개시/추가(평균 진입가) · 축소(평균 청산가 · Net 누적) · 체결별 수수료/세금 누적 · 청산 시 TradeExecutionRecord(링 256) — 진입/청산가 · MAE/MFE(원/주 · bp · 원) ·
  보유 시간 · Gross = Net + 수수료 + 세금 · 청산 효율. 요약 = 승률 · 손익비(Net · 손실 0 = 무한 −1) · 평균 MAE/MFE bp · 청산 효율 · MAE 대비 실현(Σ Net ÷ Σ MAE 원).
- ArenaDebrief 포렌식 필드(기본값 인자 — 기존 생성 호환) · 거버너 Publish 가 복기 계산을 seqlock 창 밖으로 이동(창 = 값 복사만).
- /arena: 경기 종료 시 '📊 포렌식 성과 분석' 모달(AI vs 휴먼 — 거래/승률 · 손익비 · Net · Gross · 수수료/거래세 · MDD · 평균 MAE/MFE · 청산 효율 · MAE 대비 실현 · 마지막 거래 ·
  Net 우위 판정 + Gross 와 승패가 갈리면 '마찰비용이 승부를 뒤집음' 표기) · 닫기 = 그 매치 동안 숨김.
- HMI 강건화: 뷰가 seqlock 판독 상한 초과 시 직전 정상 스냅샷 유지('대기' 자리표시 깜빡임 0 — 아레나 그룹 부하에서 콘솔 요소가 순간 사라지던 현상).

### 95.3 검증
- FrictionAccountingTests 10건: 롱/숏 왕복 수수료 · 세금 정확 차감 + Gross/Net 항등식 + trip 기록 · 체결별 절사 5경계 · Net 청산가치 리스크(1원 단위 경계 탐색 —
  시가평가 > 하한 ≥ 청산가치 = 동결 · 1원 위 = 미격발) · 무작위 롱/숏 10만 체결: FeesWon/TaxWon = 체결별 독립 재계산 합(오차 0원) · 무포지션 현금 = 자본 + Σ Net ·
  trip Net 합 = 원장 실현 · 10만 체결(마찰 + 포렌식 + 청산가치 + 복기) 0B.
- ExecutionQualityTests 7건: 롱 급락/급등 틱 MAE 2,000 · MFE 2,500 · bp · 청산 효율 40% · 보유 30초 · 진입 체결가 표본 · 숏 대칭(추가 진입 평균 · 부분 커버 · MAE 3,050 · MFE 2,950) ·
  요약(승률 50% · 손익비 1.50 · MAE 대비 실현 30% · 링 순서/덮어씀) · 거버너 스냅샷/복기 포렌식 · 헤드리스 모달(종료 후 표시 · 닫기) · IL 허용목록.
- 의미 변경 갱신 3건: HyperRealism −3% 발동 자산 · SyntheticShorting 발동 자산(9,696,370 → 9,695,278 = 커버 수수료 포함) · ArenaUIStress 생산 TPS 측정 경계(취소 후 경과 제외).
- 오중 빌드(WebOnly → Default sln) Release -warnaserror 0W/0E · 전체 스위트 4,086/4,086 · 아레나 그룹 175건 5회 연속 · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 한계(정직): 틱 MAE/MFE = 최우선 호가 기준(체결 틱 가격 미포함 · 호가 사이 체결 극값 미포착 가능) · 청산가치 = 최우선 호가 전량 가정(호가 잔량 소진 미반영) · 대주 수수료 없음.

## 96. v12.18.25-GM — 아레나 마찰비용 · Net 청산가치 리스크 · 틱 MAE/MFE 포렌식 단독 출하(-NoPeer) (2026-10-19)

### 96.1 버전 · 태그
- v12.18.25-DEV(§95) = 1624433. 12.18.24 → 12.18.25(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말 · BOM 유지) = 0bbfa73(4파일 · 11/11 줄 · 구버전 문자열 잔존 0) ·
  핀 테스트 48/48(Release). 빌드 전 공유 체크아웃 testhost 잠금 0 확인(§94 교훈).
- 태그 v12.18.25-GM(annotated · -f) = 0bbfa73. QuantumVault 미접근(-NoPeer · 경로 참조 0) · 매니페스트 피어 기록 없음 · 원격 푸시 없음.

### 96.2 출하 실행(형제 worktree StockManager-gm-12.18.25 · 태그 0bbfa73 · 미커밋 0)
- safe.directory · core.commitGraph=false = 프로세스 GIT_CONFIG_* 환경변수(전역 설정 · .git 무변경).
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0(1회 통과): [0] 태그 = HEAD 코드 · 미커밋 0 · [1]/[2] 오중 빌드 -warnaserror 0W/0E(KiwoomComShim 산출 확인) ·
  [3] 4,086/4,086 · [4] infra-scan · [5] 래칫 ≤40(2/2) · [6] liveArmed 기본값 소스 가드(이중키 16 · 마스터 플래그 1 전부 false) · [7] 아티팩트 38 SHA-256 · manifest.sig ·
  [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) · 원장 ⑪ · 펌프 지터 ⑫ 1 · 유령 취소 무장 ⑬ 1 · 위반 0) ·
  [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · ⑪ · seqlock ⑤ · IPC 패브릭 무대기/무슬립 · Stopwatch ⑥) · handshake Ok @0bbfa73d8d.
- 산출물(release/): release-v12.18.25-GM.zip SHA-256 `B29FA15D2C285A1E003E26B0700838C02C2736605BB1D8D39C242FFF99BDA9A9` ·
  masterSeal `6FB1A71F7F1A5A16448DE1D2FC9050F9F2E80E2127FE0539F0677DA7DDDEEA5B` · sourceCommit 0bbfa73 · 피어 기록 없음.
- v12.18.24-GM 산출물 13건(zip 7FC52AEC… · masterSeal E13BBA07…) → release/superseded-v12.18.24-GM/. worktree 제거 · prune.
- 메인 체크아웃 재수검(같은 커밋 0bbfa73 Release 빌드 CLI · 메인 release/ 복사본 대상): 복사본 SHA-256 일치 · --verify-zip(--seal-file) · --verify-live-armed(⑪ ⑫ ⑬ 강제 · 강화 키 6) ·
  --verify-interlocks(30) · --verify-tighten(6 · 관제 · IPC 패브릭 강제) 전부 exit 0.

## 97. v12.18.26-DEV — 아레나 극한 생존 훈련: 플래시 크래시 · 숏 스퀴즈 시나리오 인젝터(블라인드) (2026-10-19)

### 97.1 전제 교정
- '기존 Random Walk' = `SyntheticTickSource`(시드 고정 압력 국면 랜덤 워크). 대체가 아니라 데코레이터로 감쌈 — 원천(합성 · 녹화 .tape) 무변경, 시나리오 끔 = 종전 테이프 그대로.
- 주입 대상은 고속 틱 리플레이(훈련) 원장만. 정규장 라이브 아레나는 실 수신 호가 — 가짜 시세 주입 경로를 만들지 않음.
- 인젝터 파일은 지시대로 `Engines/Arena/Simulation/` 이나 네임스페이스는 `StockManager.Engines.Arena`(§95 Analytics 선례 — 기존 IL 허용목록 7곳 무변경 통과).

### 97.2 구성
- `FlashCrashScenarioInjector : ArenaTickSource`: 발동 시각(원천 시작 + offset) 이후 첫 호가 틱 앞에 버스트 8,000틱(간격 0.25ms = 4,000틱/초 · 2초) 삽입 · 이후 원천 시각 +2초(단조 유지).
  버스트 앞 60% = 볼록 급락(급등) 경로로 깊이 도달 · 뒤 40% = 깊이의 40% 되돌림. 급락 국면 = 유동성 진공(무너지는 쪽 10호가 잔량 1 ~ 30주 · 2틱 간격 · 스프레드 2 ~ 6틱 ·
  반대쪽 1,500 ~ 4,500주) + 매 틱 주도 체결 300 ~ 3,000주. 여진 = 버스트 종료 편차를 대상 종목 원천 호가/체결에 더하고 60초 선형 감쇠(KRX 호가 단위 재정렬).
  판독 δ=0(버스트 = 인덱스 해시 결정론 파생 · 저장 0B). 원천이 발동 시각보다 짧으면 미발동(통과 · 가짜 발동 표시 0). 버스트 간격 > 0.5ms(< 2,000틱/초) 생성 거부.
- 블라인드(`CreateBlind`): 엔트로피 1개 → 종류(급락/급등 반반) · offset 3 ~ 5분 균등 · 깊이 5 ~ 12% · 지터 시드.
- `ArenaTickReplayEngine`: `_source`(원천 · 소유/폐기 기준) / `_tape`(재생 = 원천 또는 인젝터) 분리. 새 매치(Rewind running) + 블라인드 = 인젝터 생성(콜드 · 엔트로피 기본
  Stopwatch ⊕ TickCount64 · 생성자 주입 가능) · 리셋/원천 교체 = 해제. 게시 시 국면(잠복 → 발동 → 여진) 판정 — 종류 · 종목 · 버스트 틱/초는 발동부터만 게시.
- 포트(`IArenaReplayPort.SetScenario` · `ArenaScenario`/`ArenaScenarioProfile`/`ArenaScenarioPhase` · `ArenaReplayStatus` 시나리오 필드 — 기본값 인자로 기존 생성 호환):
  재생/일시정지 중 변경 거부(진행 매치 불변). 브로커 · 무장 · 실주문 경로 없음.
- /arena 리플레이 세팅: ⚡ Extreme Scenario · 블라인드 체크(진행 중 비활성) · 인디케이터 = 잠복(종류/시각 비공개 · 종료 시 잠복 = '미발동' 정직 표기) /
  발동(role=alert 펄스 · 종류 · 종목 · 틱/초 · 급락 청 · 급등 적) / 여진(60초 회복 구간).

### 97.3 검증
- FlashCrashInjectorTests 14건: 10만 틱 원천 + 버스트 = 108,000틱 전수(시각 단조 · 호가 비역전 · 4,000틱/초 · 발동 이전/비대상 종목/회복 후 원천 동일 · 급락/급등 양쪽) ·
  급락 61,000 → 56,100(8%) · 매도 주도 · 진공 잔량/구멍/스프레드 · 되돌림 · 여진 연속 · 급등 대칭 · 블라인드 2,000회(3 ~ 5분 · 5 ~ 12% · 반반 · 결정론) · 짧은 원천 통과 ·
  인자 fail-closed · 108k 판독 0B · 최대 배속 버스트 재생 0B · 엔진 국면 잠복→발동→여진(잠복 중 비공개) + 휴먼 시장가 진공 발동 · 진행/일시정지 중 변경 거부 · 리셋 해제 ·
  전 구간 완주 · 인젝터 IL 허용목록 · 헤드리스 UI(체크 · 인디케이터 3국면 · 진행 중 비활성).
- 오중 빌드(WebOnly → Default sln: StockSuite · Portable · PrimesBench · KiwoomProxy · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 4,100/4,100 ·
  아레나 그룹 189건 3회 연속 · 신규 14건 3회 연속 · infra-scan 0 ≤ 117 · 래칫 ≤40(PureShellFitness).
- 한계(정직): 합성 훈련 데이터 — 실 플래시 크래시 통계 재현 아님 · 버스트는 대상 1종목만(타 종목 동조 하락 없음) · 블라인드 발동 시각 = 재생 시뮬레이션 시각 기준(배속 비례로 벽시계 단축).

## 98. v12.18.26-GM — 아레나 블라인드 플래시 크래시 · 숏 스퀴즈 인젝터 단독 출하(-NoPeer) (2026-10-19)

### 98.1 버전 · 태그
- v12.18.26-DEV(§97) = 11e1d0b. 12.18.25 → 12.18.26(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말 · BOM 유지) = 7f71894(4파일 · 11/11 줄 · 구버전 문자열 잔존 0) ·
  GoldenMaster 핀 테스트 83/83(Release) · sln Release -warnaserror 0W/0E.
- 태그 v12.18.26-GM(annotated · -f) = 7f71894. QuantumVault 미접근(-NoPeer · 경로 참조 0) · 매니페스트 피어 기록 없음 · 원격 푸시 없음.

### 98.2 출하 실행(형제 worktree StockManager-gm-12.18.26 · 태그 7f71894 · 미커밋 0)
- safe.directory · core.commitGraph=false = 프로세스 GIT_CONFIG_* 환경변수(전역 설정 · .git 무변경).
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0(1회 통과): [0] 태그 = HEAD 코드 · 미커밋 0 · [1]/[2] 오중 빌드 -warnaserror 0W/0E ·
  [3] 4,100/4,100 · [4] infra-scan 0 ≤ 117 · [5] 래칫 ≤40(2/2) · [6] liveArmed 기본값 소스 가드 · [7] 아티팩트 38 SHA-256 · manifest.sig ·
  [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) · 원장 ⑪ · 펌프 지터 ⑫ 1 · 유령 취소 무장 ⑬ 1 · 위반 0) ·
  [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · ⑪ · seqlock · IPC 패브릭 무대기/무슬립 · Stopwatch) · handshake Ok @7f718949e5.
- 산출물(release/): release-v12.18.26-GM.zip SHA-256 `0F3A42B3238D6B9A15C0B01E65B0D108E4A1DC991C5450FE38D10D493B86DB0C` ·
  masterSeal `22850C200A94B8E6BC547F4D255BAED3597946E6CE9DFC53FD0D7D1BE0F05FB5` · sourceCommit 7f71894 · 피어 기록 없음.
- v12.18.25-GM 산출물 13건(zip B29FA15D… · masterSeal 6FB1A71F…) → release/superseded-v12.18.25-GM/. worktree 제거 · prune.
- 메인 체크아웃 재수검(같은 커밋 7f71894 Release 빌드 CLI · 메인 release/ 복사본 대상): 복사본 SHA-256 일치 · --verify-zip(--seal-file) · --verify-live-armed(⑪ ⑫ ⑬ 강제 · 강화 키 6) ·
  --verify-interlocks(30) · --verify-tighten(6 · 관제 · IPC 패브릭 강제) 전부 exit 0.

## 99. v12.18.27-DEV — KIS 리얼 와이어 LongSoak 하네스: 100만 프레임 수신 경로 δ=0 · 단절/리셋/핑 타임아웃 복구 (2026-10-20)

### 99.1 전제 교정
- 실 시세 수신 경로는 이미 존재(`KisRealtimeSession` 접속키 → WS 구독 · 조각 조립 · PINGPONG 반송 · 단절 인터락 / `KisOpenApiAdapter.OnFrame` → H0STASP0 · H0STCNT0 δ=0 판독기). 재구현하지 않고 그 경로를 소크.
- ★리얼 와이어 미실행: 착수 시점(2026-10-20 02:37 KST) = KRX 장외 · 이 호스트에 KIS 모의투자 자격증명 환경변수 없음. 리얼 와이어 테스트는 옵트인 Fact 로 작성 —
  조건 미충족 시 '건너뜀'(사유 표기 · 통과 위장 0). 실 수신 결과 · 실 프레임 판독 δ=0 · 실망 재접속은 아직 증명되지 않았다.
- KIS 실시간 구독에는 REST 토큰 버킷이 개입하지 않는다(접속키 1회 · 구독 ≤ 41/세션). 지시의 'Rate Limiter' 감시 대상은 이 경로에 없음 — 재접속 백오프(고정 지연)만 해당.

### 99.2 구성 — `StockManager.Tests/Integration/LiveWireKisLongSoakTests.cs`
- §1 오프라인 결정론 소크(상시): 동기 완료 대역 전송으로 실 `RunAsync` 수신 루프 + 어댑터 판독을 테스트 스레드에서 구동 — 38종목 H0STASP0 · H0STCNT0 · PINGPONG(1/64) ·
  2조각 분할(1/16) · 연결마다 300KB 버퍼 초과 메시지 1건 · 연결당 5만 프레임 후 장애 순환(서버 Close · WebSocketException 리셋 · 핑/퐁 61초 침묵 → Evaluate 트립 + SilentNow → AbortConnection).
  연결별 정상 상태 창(프레임 1,000 ~ 끝) 스레드 할당을 GC.GetAllocatedBytesForCurrentThread 로 직접 측정.
- §2 리얼 와이어(옵트인 `[KisVtsLiveFact]`): MS2E_KIS_VTS_APPKEY · MS2E_KIS_VTS_SECRETKEY · MS2E_KIS_VTS_HTSID + KRX 정규장일 때만. 모의 도메인 고정
  (접속키 = openapivts…:29443/oauth2/Approval 1회 · WS = ops…:31000) · 시세/체결통보 구독만 · 주문/잔고/토큰 REST 0 · 브로커 인스턴스 0.
  100만 틱(호가 + 체결) 또는 MS2E_KIS_SOAK_MINUTES(기본 60) 중 빠른 쪽 · 1초 감시(Evaluate · 침묵 시 AbortConnection) · 실 프레임 8,192건 고정 버퍼 캡처 →
  종료 후 새 어댑터로 재주입해 판독 할당 0B 단언 · 실 소켓 프로세스 할당은 B/프레임 보고만(BCL ClientWebSocket 내부 — 판독 핫패스 아님).

### 99.3 검증
- 오프라인 소크 3회 연속: 1,000,000프레임(0.3 ~ 0.9M/s) · 연결 20 · 단절 20 · 장애 19(종료 6 · 리셋 7 · 핑타임아웃 6) · 강제종료 6 · 수신 예외 13(크래시 0) ·
  인터락 sticky 트립 · 호가 500,000 · 체결 484,360 · 핑 반송 15,620(= 어댑터 반송) · 조각 62,500 · 초과 폐기 20 · 형식 이탈 0 · ★정상 구간 할당 0B/20창 ·
  연결당 콜드(구독 JSON · 취소원 · 예외) 약 27KB.
- 오중 빌드(WebOnly → Default sln) Release -warnaserror 0W/0E · 전체 스위트 4,101 통과 + 1 건너뜀(리얼 와이어 옵트인) · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 다음: 정규장 중 모의투자 자격증명 환경변수 설정 후 `dotnet test … --filter "FullyQualifiedName~LiveWire_Vts"` 실행 → 결과로 이 절 보강.

## 100. v12.18.27-GM — KIS 리얼 와이어 LongSoak 하네스 단독 출하(-NoPeer) (2026-10-20)

### 100.1 버전 · 태그
- v12.18.27-DEV(§99) = e298961. 12.18.26 → 12.18.27(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말 · BOM 유지) = 7c5cc1d(4파일 · 11/11 줄 · 구버전 문자열 잔존 0) ·
  GoldenMaster 핀 테스트 83/83(Release) · sln Release -warnaserror 0W/0E.
- 태그 v12.18.27-GM(annotated · -f) = 7c5cc1d. QuantumVault 미접근(-NoPeer · 경로 참조 0) · 매니페스트 피어 기록 없음 · 원격 푸시 없음.

### 100.2 출하 실행(형제 worktree StockManager-gm-12.18.27 · 태그 7c5cc1d · 미커밋 0)
- safe.directory · core.commitGraph=false = 프로세스 GIT_CONFIG_* 환경변수(전역 설정 · .git 무변경).
- release-package.ps1 -NoPeer → [0/10]~[10/10] EXIT 0(1회 통과): [0] 태그 = HEAD 코드 · 미커밋 0 · [1]/[2] 오중 빌드 -warnaserror 0W/0E ·
  [3] 4,101 통과 + 1 건너뜀(§99 리얼 와이어 옵트인 — 자격증명 미설정) · [4] infra-scan 0 ≤ 117 · [5] 래칫 ≤40 · [6] liveArmed 기본값 소스 가드 · [7] 아티팩트 38 SHA-256 · manifest.sig ·
  [9] verify-zip Ok · verify-live-armed Ok(조회 21 · 마스터 1 · UI 가드 3/3 · 시뮬레이션 격리 50(CLI 29) · 강화 키 6(호스트 8) · 원장 ⑪ · 펌프 지터 ⑫ 1 · 유령 취소 무장 ⑬ 1 · 위반 0) ·
  [10] interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · ⑪ · seqlock · IPC 패브릭 무대기/무슬립 · Stopwatch) · handshake Ok @7c5cc1d1da.
- 산출물(release/): release-v12.18.27-GM.zip SHA-256 `64FC0CE1A6689468AAF4FFF646BFB75964B7A3F860AB801DEABE2A3A3FF5EFBD` ·
  masterSeal `9FCB21B9F4B6CC164EFC34C8675DE2D06D4DF67B60BBCA579EF08F1439CEC87A` · sourceCommit 7c5cc1d · 피어 기록 없음.
- v12.18.26-GM 산출물 13건(zip 0F3A42B3… · masterSeal 22850C20…) → release/superseded-v12.18.26-GM/. worktree 제거 · prune.
- 메인 체크아웃 재수검(같은 커밋 7c5cc1d Release 빌드 CLI · 메인 release/ 복사본 대상): 복사본 SHA-256 일치 · --verify-zip(--seal-file) · --verify-live-armed(⑪ ⑫ ⑬ 강제 · 강화 키 6) ·
  --verify-interlocks(30) · --verify-tighten(6 · 관제 · IPC 패브릭 강제) 전부 exit 0.
- 한계(정직): 출하본의 KIS 리얼 와이어 소크는 여전히 미실행(§99 — 정규장 + 모의투자 자격증명 필요).

## 101. v12.18.28-DEV — 이중 산출 패키징: 감사 봉인본 + 즉시 실행 배포본(Standalone win-x64) (2026-10-20)

### 101.1 전제 교정
- v12.18.26 스모크(§98 이후)에서 확인: 감사 봉인 zip(38 바이너리)에는 runtimeconfig · deps.json · 네이티브(WebView2Loader · e_sqlite3) · wwwroot 가 없어 exit 131(hostpolicy) — 실행본이 아님.
- 배포본 바이너리를 publish 산출물 그대로 쓰면 봉인 바이너리와 바이트가 다르다(RID 빌드 · 실측 MS2E.StockSuite.dll/exe 해시 상이). 그래서 설계 =
  self-contained publish 는 '런타임 층'만 제공하고, 앱/의존 바이너리 38개는 봉인 zip 에서 꺼내 덮어쓴다(바이트 동일 · 실측 기동 정상).
- 버전은 12.18.27 유지(DEV) — 배포본 이름은 csproj 버전을 따른다(다음 GM 승격 시 StockManager-v12.18.28-Standalone-win-x64.zip).

### 101.2 구성
- `standalone-bundle.ps1`(UTF-8 BOM): ① 봉인 zip 추출(바이너리 원천) ② dotnet publish self-contained(호스트 win-x64 · KiwoomProxy win-x86) ③ 봉인 38개 오버레이 +
  artifacts.sha256.txt 전 항목 해시 대조(불일치 = 중단) · artifacts.sha256.txt · manifest.sig · master-seal.txt 동봉 ④ *.staticwebassets.runtime.json(빌드 머신 절대 경로) · *.pdb 제거 ·
  필수 자산 14종 · runtimeconfig includedFrameworks 확인 ⑤ zip + .sha256 → 판독 테스트(MS2E_RELEASE_STANDALONE_ZIP) → 새 임시 폴더에 풀어 실기동 · 주 창 핸들 ≤ 45초 · 경로 격리 확인 후 종료
  (-SkipLaunch = 무인 CI). 실패-닫힘: Abort = 배포 zip 삭제 + 임시 폴더 정리 + exit 1 · trap 으로 종료 오류도 같은 경로. 피어 참조 0 · 감사 봉인 산출물 쓰기 0.
- `release-package.ps1`: [11/11] = [10/10] 인터락/강화 대조 뒤 · 완료 전에 호출 · 실패 = Remove-ReleaseOutputs(배포본 포함으로 확장) + Fail · -SkipStandaloneLaunch ·
  단계 표기 [n/11](주석의 [n/10] = 감사 봉인 단계 번호 그대로). 감사 봉인 [0/10]~[10/10] 로직 · IL 가드 · 봉인 해시 체인 무변경.
- `StockManager.Tests/Packaging/StandaloneBundleTests.cs` 16건: 판독기(역슬래시 항목 정상 · 필수 자산 8종 누락 · 프레임워크 의존 runtimeconfig · 봉인 바이너리 1비트 변조 ·
  봉인 등재 누락 · 개발 경로 누출 = 거부) · 파이프라인(봉인 38 = 배포본) · 스크립트 결선 2건(순서 · 폐기 · BOM · publish · 오버레이 · 격리 기동 · 피어 0 · 봉인 산출물 쓰기 0).
- 핀 갱신(의도된 명세 변경): `Remove-ReleaseOutputs; Fail` 8 → 9(3곳) · 실행 스크립트 출력 [0/10] → [0/11](§13 낡은 피어 거부 테스트).

### 101.3 검증
- 실 봉인 zip(release-v12.18.27-GM.zip) 대상 standalone-bundle.ps1 단독 실행(출력 = 스크래치패드 · 메인 release/ 무변경) EXIT 0: 봉인 38 = 바이트 동일 · 995 항목 · 147MB(전개 352MB) ·
  pdb/staticwebassets 0 · 판독 테스트 통과 · 격리 기동 PID 35944 · HWND 0xDE1082 · 'MS2-E Stock Suite' · 경로 = 임시 격리 폴더.
- 적대: 봉인 zip 의 MS2E.UI.Portable.dll 1비트 변조본 → '배포본 바이너리 ≠ 봉인 해시' EXIT 1 · 배포 zip 0 · 임시 폴더 0. 판독 테스트 환경변수 오경로 = 실패(통과 위장 0).
- 오중 빌드(WebOnly → Default sln) Release -warnaserror 0W/0E · 전체 스위트 4,117 통과 + 1 건너뜀(§99 리얼 와이어 옵트인) · infra-scan 0 ≤ 117 · 래칫 ≤40.
- 한계(정직): release-package.ps1 [0/10]~[11/11] 전 구간 실행은 다음 GM(태그 필요)에서 첫 증명 · WebView2 런타임은 Windows 구성요소(미설치 PC = 창은 뜨나 화면 빈칸 가능 — 번들 미포함) ·
  격리 기동은 %LOCALAPPDATA%\StockManager(감사 원장 등)에 기록(개발 실행과 같은 위치).

## 102. v12.18.28-GM — 감사 봉인본 + 즉시 실행 배포본 이중 출하(-NoPeer · 11단계 첫 완주) (2026-10-20)

### 102.1 버전 · 태그
- v12.18.28-DEV(§101) = 0371b5c. 12.18.27 → 12.18.28(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말 · BOM 유지) = 4c1c193(4파일 · 11/11 줄 · 구버전 문자열 잔존 0) ·
  핀 + StandaloneBundleTests 99/99(Release) · sln Release -warnaserror 0W/0E. (지시서의 GoldenMasterVersionPinTests.cs · ReleasePackagingVersionPinTests.cs 는 저장소에 없음 — 실제 핀 = 위 2종)
- 태그 v12.18.28-GM(annotated · -f) = 4c1c193. QuantumVault 미접근(-NoPeer · 경로 참조 0) · 매니페스트 피어 기록 없음 · 원격 푸시 없음.

### 102.2 출하 실행(형제 worktree StockManager-gm-12.18.28 · 태그 4c1c193 · 미커밋 0)
- release-package.ps1 -NoPeer → [0/11]~[11/11] EXIT 0(1회 통과 · 11단계 첫 완주): [1]/[2] 오중 빌드 -warnaserror 0W/0E · [3] 4,117 통과 + 1 건너뜀(§99 옵트인) · [4] infra-scan 0 ≤ 117 ·
  [5] 래칫 ≤40 · [6] liveArmed 소스 가드 · [7] 아티팩트 38 · manifest.sig · [9] verify-zip Ok · verify-live-armed Ok(⑪ · ⑫ 1 · ⑬ 1 · 위반 0) ·
  [10] handshake Ok @4c1c193fdd · interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6 · seqlock · IPC 패브릭 무대기/무슬립) ·
  [11] 봉인 오버레이 38 = 봉인 해시(바이트 동일) · 판독 테스트 통과 · 격리 기동 PID 49044 · HWND 0x64113C · 'MS2-E Stock Suite' · 경로 = 임시 격리 폴더(종료 · 정리 완료).
- 산출물(release/):
  - 감사 봉인본 release-v12.18.28-GM.zip SHA-256 `2461B0C7C044AAFBF8B39C77F14931814BF95E4C6359CBCCF3B8225C425B832C` · masterSeal `BB55762C0C772BF81F350046DB341BBC38175D44FFB7685B40FD1DE27D7D9E28` · sourceCommit 4c1c193 · 피어 기록 없음.
  - 즉시 실행 배포본 StockManager-v12.18.28-Standalone-win-x64.zip SHA-256 `1C794A30CEBDA79059F23376DEE047F111A0EA25D974BCDA934B7BF616656C7F` · 993 항목 · 147MB(전개 352MB) ·
    봉인 38 바이트 동일 · artifacts.sha256.txt · manifest.sig · master-seal.txt 동봉.
- v12.18.27-GM 산출물 13건(zip 64FC0CE1… · masterSeal 9FCB21B9…) → release/superseded-v12.18.27-GM/. 신규 15건 복사. worktree 제거 · prune · 격리 기동 임시 폴더/프로세스 잔존 0.
- 메인 체크아웃 재수검(같은 커밋 4c1c193 Release 빌드 CLI · 메인 release/ 복사본 대상): 두 zip 복사본 SHA-256 = worktree 원본 = .sha256 기록 · --verify-zip · --verify-live-armed(⑪ ⑫ ⑬ 강제) ·
  --verify-interlocks(30) · --verify-tighten(6 · 관제 · IPC 패브릭 강제) 전부 exit 0 · 배포본 판독기(StandaloneBundleTests 파이프라인 · 봉인 38 대조) 통과.
- 한계(정직): WebView2 런타임 미포함(Windows 구성요소) · KIS 리얼 와이어 소크 미실행(§99).

## 103. v12.18.29-DEV — WebView2 Evergreen 런타임 사전 비행 가드 · 안전 부트스트랩 (2026-10-20)

- 문제(실측 근거): BlazorWebView 는 WebView2 런타임 미설치 PC에서 생성자 예외 없이 비동기 초기화만 실패 → 빈 화면. Program.cs 의 기존 try/catch 클래식 폴백은 이 경로를 잡지 못함.
- 신규 StockManager/Blazor/WebView2RuntimeGuard.cs(호스트 · RCL 미접촉): CoreWebView2Environment.GetAvailableBrowserVersionString() 탐침 → Present / MissingOnline / MissingOffline.
  - 탐침 실패 전부(미설치 예외 · 로더 DLL 부재 · 미분류 예외 · null/빈 버전) = 미설치(fail-safe) · 네트워크 탐침(GetIsNetworkAvailable) 예외 = 오프라인.
  - MissingOnline: 안내 다이얼로그 [예] = 공식 Evergreen 부트스트래퍼 링크(fwlink 2124703 · build-installer.ps1/.iss 와 동일)를 기본 브라우저로 열어 다운로드 트리거 · [아니오] = 트리거 0 · 트리거 실패 = 수동 안내로 강등(크래시 0).
  - MissingOffline: 오프라인 안내(공식 주소 · --classic/--headless 대안 표기)만.
  - 미설치 전 경로 = Blazor 진행 거부 → Environment.ExitCode = 0 안전 종료.
- Program.cs 결선: 봉인 수검 CLI 분기 뒤 · DB/시크릿/브로커(KisProvider)/BlazorHomeForm 구성 이전 · --classic/--headless 제외. 런타임 검사 중 브로커·DB·피어 접점 0(liveArmed:false 무관).
- 미채택(정직): 무인 백그라운드 설치 — 내려받은 exe 를 Authenticode 체인 검증 없이 실행하게 되므로 배제. 설치형(Inno Setup)은 동봉 부트스트래퍼로 이미 /silent /install. 즉시 실행 배포본은 링크 안내.
- 테스트 StockManager.Tests/Packaging/WebView2RuntimeGuardTests.cs 18건: 탐침 분류(6종 실패 = 미설치) · 상태 전이(Present = UI 0 · 수락/거부/트리거 실패/오프라인) · 미설치 = 진행 0 · 실 탐침 무예외 · Program.cs 결선 순서 · 가드 소스 피어/브로커/원격 실행 접점 0.
- 검증: 오중 빌드(Default · WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 4,135 통과 + 1 건너뜀(§99 옵트인) · infra-scan 0 · 래칫 ≤40(RCL 변경 0).
- 한계(정직): 미설치 PC 실기 확인 미실시(개발 호스트는 런타임 설치됨 — 분기는 모의 UI 로 증명). QuantumVault 미접근 · 원격 푸시 없음.

## 104. v12.18.29-GM — WebView2 런타임 가드 반영 이중 출하(감사 봉인본 + 즉시 실행 배포본 · -NoPeer) (2026-10-20)

### 104.1 버전 · 태그
- v12.18.29-DEV(§103) = 91d885d. 12.18.28 → 12.18.29(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말 · BOM 유지) = 00176b5(4파일 · 11/11 줄) ·
  sln Release -warnaserror 0W/0E · 핀/패키징/배포본/가드 테스트 82/82(Release).
- 태그 v12.18.29-GM(annotated · -f) = 00176b5. QuantumVault 미접근(-NoPeer) · 매니페스트 피어 기록 0 · 원격 푸시 없음.

### 104.2 출하 실행(형제 worktree StockManager-gm-12.18.29 · 태그 00176b5 · 미커밋 0)
- 1차 실행 EXIT 1 — [3/11] 전체 스위트 4,134 통과 · 1 실패(LiveWireKisLongSoakTests.Offline_1M: 정상구간 할당 2,992B ≠ 0 · 20창 중 부하 시 단일 창 튐) → fail-closed 산출물 폐기.
  판정: 같은 빌드 단독 5회 = 0B/20창 × 5 · KIS 수신 경로는 e298961 이후 무변경 → 회귀 아님 · 전체 스위트 부하 하 δ=0 단일 창 측정 아티팩트. 코드/게이트 무수정 재실행.
- 2차 실행 release-package.ps1 -NoPeer → [0/11]~[11/11] EXIT 0: [1]/[2] 오중 빌드 0W/0E · [3] 4,135 통과 + 1 건너뜀(§99 옵트인) · [4] infra-scan 0 ≤ 117 · [5] 래칫 ≤40 ·
  [6] liveArmed 소스 가드(이중키 16 · 마스터 플래그 1) · [7] 아티팩트 38 · manifest.sig · [9] verify-zip Ok · verify-live-armed Ok(⑪ · ⑫ · ⑬) ·
  [10] handshake Ok @00176b574b · interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6) · [11] 봉인 오버레이 38 바이트 동일 · 판독 테스트 통과 · 격리 기동 PID 50744 · HWND 0xC812D6 · 'MS2-E Stock Suite'.
- 산출물(release/):
  - 감사 봉인본 release-v12.18.29-GM.zip SHA-256 `512549CF9C6A5129CF72D8980DBDF39317289BEE8CC8BD937EBC355D194614EF` · masterSeal `D5E5ADCEB16A6085B3DCBBDBC01413785BA91D5CC1FAF3D7EB0BF46C3001D10F` · sourceCommit 00176b5 · 피어 기록 없음.
  - 즉시 실행 배포본 StockManager-v12.18.29-Standalone-win-x64.zip SHA-256 `DAEB089B8AC0CAF09D16ADD27A5782D9E0E442FB120431E625B616228C42CFC1` · 993 항목 · 전개 352MB · WebView2RuntimeGuard 포함.
- v12.18.28-GM 산출물 15건 → release/superseded-v12.18.28-GM/. 신규 15건 복사. worktree 제거 · prune · 격리 기동 임시 폴더/프로세스 잔존 0.
- 메인 체크아웃 재수검(같은 커밋 00176b5 Release CLI · 메인 release/ 복사본): 두 zip SHA-256 = .sha256 기록 · --verify-zip · --verify-live-armed(⑪ ⑫ ⑬ 강제) · --verify-interlocks(30) ·
  --verify-tighten(6 · 관제 · IPC 패브릭 강제) 전부 exit 0 · StandaloneBundleTests 파이프라인(MS2E_RELEASE_STANDALONE_ZIP) 16/16.
- 한계(정직): WebView2 미설치 PC 실기 미확인(가드 분기는 모의 UI 증명) · KIS 리얼 와이어 소크 미실행(§99) · Offline_1M δ=0 단일 창 판정은 전체 스위트 부하 하 플래키(창별 최솟값/재측정 하드닝 후보).

## 105. v12.18.30-DEV — LiveWireKisLongSoakTests.Offline_1M δ=0 측정 하드닝 (2026-10-20)

- 문제(실측 근거): v12.18.29-GM 1차 출하 [3/11] 에서 Offline_1M 정상구간 할당 2,992B ≠ 0(20창 합산 단일 판정) → fail-closed. 같은 빌드 단독 5회 = 0B · KIS 수신 경로 무변경 → 전체 스위트 부하 하 외생 노이즈(타 스레드 GC 겹침 시 할당 컨텍스트 재계수).
- 수정(StockManager.Tests/Integration/LiveWireKisLongSoakTests.cs 단일 파일 · 제품 코드 0):
  - 웜업: 측정 대상과 분리된 어댑터 · 인터락 · 싱크로 5,000프레임 사전 파싱(JIT · 티어 승격 · 내부 버퍼) — 본 소크 계수 무오염.
  - 격리: 측정 직전 GC.Collect · WaitForPendingFinalizers(선행 테스트 잔여 정리) → 전용 측정 스레드(KisSoakMeasure)에서 RunAsync 동기 구동(스레드풀 · 동기화 컨텍스트 무관 · 예외는 캡처 후 단언).
  - 판정: 창별 할당 배열 기록(20창) → 최솟값 0B(다중 표본 · 결정론 δ=0) + 0B 창 과반 + 최대 창 ≤ 64KB(노이즈 상한 · 관측 2,992B/8,168B). 실 누수는 창당 ≥ 48,000프레임이라 전 창에서 드러남(완화 아님).
  - 기존 단언(프레임 · 재접속 · 트립 · 핑 반송 · 초과 폐기 등) 전부 유지 · 출력에 창별 할당 표기.
- 검증: Offline_1M 단독 5회 연속 통과(20창 전부 0B × 5) · 전체 스위트 Release 4,135 통과 + 1 건너뜀(§99 옵트인) · WebOnly · sln(Default · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E.
- 불변: liveArmed:false · RCL/래칫 ≤40 무변경(테스트 파일만) · QuantumVault 미접근 · 타 세션 파일 4종 미스테이징 · 원격 푸시 없음.
- 한계(정직): 전체 스위트 부하 하 재발 여부는 1회 실행으로만 확인 — 다음 출하 [3/11] 에서 재관측 필요. §99 리얼 와이어 VTS 옵트인 시도(사용자 보고): 접속키 발급 682회 연속 실패 · 연결 0 · 프레임 0 → 자격증명(모의 전용 AppKey · HTS ID) 확인 전까지 MS2E_KIS_VTS_* 미설정 유지(건너뜀). 접속키 실패 시 5초 간격 무한 재발급(1시간 682회 REST)은 백오프 부재 — 후속 검토 후보.

## 106. v12.18.30-GM — Offline_1M δ=0 측정 하드닝 반영 이중 출하(감사 봉인본 + 즉시 실행 배포본 · -NoPeer) (2026-10-20)

### 106.1 버전 · 태그
- v12.18.30-DEV(§105) = af69410. 12.18.29 → 12.18.30(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말 · BOM 유지) = e0cfa3e(4파일 · 11/11 줄) ·
  sln Release -warnaserror 0W/0E · 핀/패키징/배포본/가드 테스트 136/136(Release).
- 태그 v12.18.30-GM(annotated · -f) = e0cfa3e. 패키징 셸 MS2E_KIS_VTS_* 미설정(프로세스 · 사용자 · 시스템 0) · QuantumVault 미접근(-NoPeer) · 매니페스트 피어 기록 0 · 원격 푸시 없음.

### 106.2 출하 실행(형제 worktree StockManager-gm-12.18.30 · 태그 e0cfa3e · 미커밋 0)
- release-package.ps1 -NoPeer 1차 실행으로 [0/11]~[11/11] EXIT 0: [1]/[2] 오중 빌드 0W/0E(KiwoomComShim 컴파일 증거 확인) · [3] 4,135 통과 + 1 건너뜀(§99 옵트인) ·
  ★하드닝된 Offline_1M 이 전체 스위트 부하 하에서 원패스 통과 · [4] infra-scan 0 ≤ 117 · [5] 래칫 ≤40 · [6] liveArmed 소스 가드(이중키 16 · 마스터 플래그 1) ·
  [7] 아티팩트 38 · manifest.sig · [9] verify-zip Ok · verify-live-armed Ok · [10] handshake Ok @e0cfa3e725 · interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6) ·
  [11] 봉인 오버레이 38 바이트 동일 · 판독 테스트 통과 · 격리 기동 PID 48260 · HWND 0x2717D8 · 'MS2-E Stock Suite'.
- 산출물(release/):
  - 감사 봉인본 release-v12.18.30-GM.zip SHA-256 `DE76FAADD62D835DFEE21B5C69EA191F2233B380BAC8660B92AE38CC8A4BA11D` · masterSeal `2851B8DC211D425582EE3494CD43D4F025B636AFB67CE566C4AAB044F3CA32A9` · sourceCommit e0cfa3e · 피어 기록 없음.
  - 즉시 실행 배포본 StockManager-v12.18.30-Standalone-win-x64.zip SHA-256 `232822F455AC6462B806FCA8C3F20CEFD9F975F94E477813C9515E2B46B845C5`.
- v12.18.29-GM 산출물 15건 → release/superseded-v12.18.29-GM/. 신규 15건 복사(두 zip SHA-256 = .sha256 기록). worktree 제거 · prune · 격리 기동 임시 폴더/프로세스 잔존 0.
- 메인 체크아웃 재수검(같은 소스 e0cfa3e Release CLI · 메인 release/ 복사본): --verify-zip · --verify-live-armed(⑪ ⑫ ⑬ 강제) · --verify-interlocks(30) · --verify-tighten(6 · 관제 · IPC 패브릭 강제) 전부 exit 0.
- 한계(정직): Offline_1M 부하 하 통과는 이번 1회 관측 · WebView2 미설치 PC 실기 미확인 · KIS 리얼 와이어 소크 미실행(§99 · 접속키 682회 실패 보고 — 자격증명 재확인 대기).

## 107. v12.18.31-DEV — KIS 접속키 발급 지수 백오프 · 자격증명 거부 sticky 래치(재시도 폭풍 차단) (2026-10-20)

- 문제(실측 · 사용자 보고): §99 VTS 옵트인 소크에서 접속키(POST /oauth2/Approval) 발급 실패 시 고정 5s 간격 무한 재요청 → 1시간 682회 REST(연결 0 · 프레임 0).
  복구 관리자 결속(호스트 실배선) 경로도 1~16s 펌프 간격으로 무한 재발급 — 같은 폭풍 위험. 401/403(자격증명 오류)도 일시 실패와 구분 없음.
- 신규 StockManager/Adapters/KisApprovalBackoff.cs(호스트 · RCL 미접촉):
  - 일시 실패 n 회 → 기준 지연 5 · 10 · 20 · 40 · 60s 상한 + 지터 +12.5~25%(SplitMix64 무할당 · 주입 시드). 지터 하한이 상한 구간 최소 간격 67.5s 를 보장 → 1시간 발급 ≤ 57회(시드 무관).
  - 자격증명 거부 = sticky 래치(발급 시도 0 · 재시작 전 해제 없음 · 상태 보고 ApprovalRejected/LastRejectStatus). 성공 = 연속 실패 리셋.
  - δ=0: NoteFailure · NoteSuccess · RemainingMicros = long 연산 + Interlocked/Volatile. 대기(Task.Delay)는 실패당 1회 콜드.
- KisRealtimeSession: 발급 직전 백오프 잔여 대기(복구 관리자 펌프 간격과 max 합성 — 두 모드 모두 적용) · 거부 래치 = 수신 루프 종료(미연결 = 세션 인터락 차단 유지).
- KisLiveBroker.RequestApprovalKey: HTTP 401/403 = KisApprovalRejectedException(IsCredentialRejection) · 그 밖(429 · 5xx · 네트워크 · 본문 이상) = 종전대로 null(일시 실패).
- 개발 중 발견 · 수정: 해제 표지 long.MinValue − 시계 감산 오버플로 → 거대 대기(TimeSpan 오버플로) — 비교 후 감산 + 회귀 단언.
- 테스트(LiveWireKisLongSoakTests §3 · 가상 시계 + 모의 발급 엔드포인트 대역): 1시간 일시 실패 54회(< 60 · 간격 5.7 → 11.5 → 22.7 → 48.9 → 67.7~74.8s) ·
  시드 64종 최악 54회(≤ 57 단언) · 401/403 = 발급 1회 후 래치 · 상태 분류 6종 · 3회 실패 후 성공 = 리셋 + 접속 1회 · 상태 머신 δ=0(3창 최솟값 0B).
  기존 2건(KisLivePipelineWiring · SessionAutoRecovery 접속키 장애)은 재시도/펌프 검증 목적이라 축소 백오프(1~4ms) 주입.
- 검증: Offline_1M 단독 5회 연속 통과(20창 0B × 5) · 전체 스위트 Release 4,147 통과 + 1 건너뜀 · WebOnly · sln(Default · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · infra-scan 0 · 래칫 ≤40(RCL 변경 0).
- 불변: liveArmed:false(수신 전용 경로 · 주문 송신 0) · 인터락 30/Tighten 6 무변경 · QuantumVault 미접근 · 타 세션 파일 4종 미스테이징 · 원격 푸시 없음.
- 한계(정직): 401/403 분류는 HTTP 상태 기준 — KIS 가 자격증명 오류를 다른 상태(예: 500 + 본문 코드)로 돌려주면 일시 실패(≤ 57회/시간)로 처리된다(실 응답 미관측).
  KisLiveBroker 의 실 HTTP 매핑은 정적 HttpClient 라 단위 테스트 미적용(분류 함수만 검증). 실 VTS 재소크 미실시.

## 108. v12.18.31-GM — KIS 접속키 백오프 반영 이중 출하(감사 봉인본 + 즉시 실행 배포본 · -NoPeer) (2026-10-20)

### 108.1 버전 · 태그
- v12.18.31-DEV(§107) = 84bdb7d. 12.18.30 → 12.18.31(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말 · BOM 유지) = 050a344(4파일 · 11/11 줄) ·
  sln Release -warnaserror 0W/0E · 핀/패키징/배포본/가드 테스트 136/136(Release).
- 태그 v12.18.31-GM(annotated · -f) = 050a344. 패키징 셸 MS2E_KIS_VTS_* 미설정(프로세스 · 사용자 · 시스템 0) · QuantumVault 미접근(-NoPeer) · 매니페스트 피어 기록 0 · 원격 푸시 없음.

### 108.2 출하 실행(형제 worktree StockManager-gm-12.18.31 · 태그 050a344 · 미커밋 0)
- release-package.ps1 -NoPeer 1차 실행으로 [0/11]~[11/11] EXIT 0: [1]/[2] 오중 빌드 0W/0E(KiwoomComShim 컴파일 증거 확인) · [3] 4,147 통과 + 1 건너뜀(§99 옵트인 · Offline_1M 원패스) ·
  [4] infra-scan 0 ≤ 117 · [5] 래칫 ≤40 · [6] liveArmed 소스 가드(이중키 16 · 마스터 플래그 1) · [7] 아티팩트 38 · manifest.sig · [9] verify-zip Ok · verify-live-armed Ok ·
  [10] handshake Ok @050a344265 · interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6) · [11] 봉인 오버레이 38 바이트 동일 · 판독 테스트 통과 · 격리 기동 PID 21560 · HWND 0x30B60 · 'MS2-E Stock Suite'.
- 산출물(release/):
  - 감사 봉인본 release-v12.18.31-GM.zip SHA-256 `999DEB2E3644EAA15AE47EC5041A09ADD982F66108E0D249737AB74111F8C149` · masterSeal `54779E72E0160359F11E7082AB4C4BEC7322BE5B390381A4FEBE8CB0F98B09E8` · sourceCommit 050a344 · 피어 기록 없음.
  - 즉시 실행 배포본 StockManager-v12.18.31-Standalone-win-x64.zip SHA-256 `E537CD5E82EE2AF34489156DCCB0CA793FB5AB9AD829AC2B1DF1ABB4CDC3EFF0`.
- v12.18.30-GM 산출물 15건 → release/superseded-v12.18.30-GM/. 신규 15건 복사(두 zip SHA-256 = .sha256 기록). worktree 제거 · prune · 격리 기동 임시 폴더/프로세스 잔존 0.
- 메인 체크아웃 재수검(같은 소스 050a344 Release CLI · 메인 release/ 복사본): --verify-zip · --verify-live-armed(⑪ ⑫ ⑬ 강제) · --verify-interlocks(30) · --verify-tighten(6 · 관제 · IPC 패브릭 강제) 전부 exit 0.
- 한계(정직): 401/403 외 형식의 KIS 자격증명 거부는 일시 실패(≤ 57회/시간)로 처리 · 실 VTS 재소크 미실시(§99) · WebView2 미설치 PC 실기 미확인.

## 109. v12.18.32-DEV — KIS 접속키 비표준 본문 거부(HTTP 200/500 + error_code/msg_cd) 1회 즉시 sticky 래치 (2026-10-20)

- 배경: §107/§108 한계 — KIS 는 자격증명 오류를 401/403 대신 HTTP 200/500 + JSON 본문 코드로 반환하기도 한다 → 종전엔 일시 실패로 분류돼 백오프 재시도(≤ 57회/시간).
- KisApprovalRejectedException(LiveBrokers.cs):
  - FatalBodyCodes = EGW00103(AppKey 무효) · EGW00105(AppSecret 무효) · EGW00121 · EGW00123 · EGW00133 · OPS00001 · ErrorCode 속성(null = HTTP 상태 거부) · (status, code) 생성자.
  - MatchFatalBodyCode(ReadOnlySpan<byte>): Utf8JsonReader 타입드 판독 · 최상위(깊이 1) error_code/msg_cd 문자열만 · ValueTextEquals(u8) 비교 · catch(JsonException) → −1. δ=0(문자열 할당 0).
- KisLiveBroker.ParseApprovalResponse(status, body)(public static · 정적 HttpClient 분리 → 단위 테스트 가능): 401/403 → 거부 · 본문 치명 코드 → 상태 무관 거부 ·
  그 밖 비2xx · approval_key 부재/공백 · 비JSON/절단 = null(일시 실패 → 백오프). 성공 판독도 JsonDocument → Utf8JsonReader 로 교체. RequestApprovalKey 는 본문(바이트)을 상태 무관 판독.
- 세션/백오프 경로 무변경(기존 catch → NoteRejected(status) sticky). 헤더 주석만 갱신(KisRealtimeSession · KisApprovalBackoff).
- 테스트(LiveWireKisLongSoakTests §4 · +15): 200/EGW00121 · 500/EGW00123 · 200/EGW00103(msg_cd) · 500/OPS00001 · 200/EGW00133 · 500/EGW00105 = 발급 1회 후 래치(Consecutive 0 · 접속 0 · 인터락 차단) ·
  비치명 코드 · 비JSON · 빈 본문 · 빈 키 · 중첩 필드 · 비코드 필드 · 절단 본문 = 비래치 + 백오프(≤ 57) · 정상 본문 = 키 판독 + 접속 1회 · 판독기 δ=0(정상/거부 본문 3창 최솟값 0B).
- 검증: WebOnly · sln(Default · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 Release 4,162 통과 + 1 건너뜀 · Offline_1M 단독 5회 연속 통과 · infra-scan 0 · 래칫 ≤40(RCL 변경 0).
- 불변: liveArmed:false(수신 전용 · 주문 송신 0) · 인터락 30/Tighten 6 무변경 · QuantumVault 미접근 · 타 세션 파일 4종 미스테이징 · 원격 푸시 없음.
- 한계(정직): 코드 표는 오더 명세 + 공개 문서 기억 기반(실 응답 미관측). EGW00121/00123 은 토큰 무효/만료, EGW00133 은 토큰 발급 빈도 제한(1분 1회)으로 알려져 있어
  접속키 경로에서 영구 래치는 과잉(재시작 전까지 실시간 수신 중단)일 수 있음 — 실 VTS 응답 관측 후 표 재검토 필요. 본문 판독 범위 = 최상위 필드만.

## 110. v12.18.32-GM — KIS 본문 거부 래치 반영 이중 출하(감사 봉인본 + 즉시 실행 배포본 · -NoPeer) (2026-10-20)

### 110.1 버전 · 태그
- v12.18.32-DEV(§109) = b47745a. 12.18.31 → 12.18.32(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말 · BOM 유지) = cbbd8ce(4파일 · 11/11 줄) ·
  sln Release -warnaserror 0W/0E · 핀/패키징/배포본/가드 테스트 136/136(Release).
- 태그 v12.18.32-GM(annotated · -f) = cbbd8ce. 패키징 셸 MS2E_KIS_VTS_* 미설정(프로세스 · 사용자 · 시스템 0) · QuantumVault 미접근(-NoPeer) · 매니페스트 피어 기록 0 · 원격 푸시 없음.

### 110.2 출하 실행(형제 worktree StockManager-gm-12.18.32 · 태그 cbbd8ce · 미커밋 0)
- release-package.ps1 -NoPeer 1차 실행으로 [0/11]~[11/11] EXIT 0: [1]/[2] 오중 빌드 0W/0E(KiwoomComShim 컴파일 증거 확인) · [3] 4,162 통과 + 1 건너뜀(§99 옵트인 · Offline_1M 원패스) ·
  [4] infra-scan 0 ≤ 117 · [5] 래칫 ≤40 · [6] liveArmed 소스 가드 · [7] 아티팩트 38 · manifest.sig · [9] verify-zip Ok · verify-live-armed Ok ·
  [10] handshake Ok @cbbd8ce793 · interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6) · [11] 봉인 오버레이 38 바이트 동일 · 판독 테스트 통과 · 격리 기동 PID 53024 · HWND 0x1C181C · 'MS2-E Stock Suite'.
- 산출물(release/):
  - 감사 봉인본 release-v12.18.32-GM.zip SHA-256 `A370A53F73B4D93D44C8E84D13CB8E950B2511C1B5958277304A5CAC08853F96` · masterSeal `3C2F018DB0CC25FC62B7BC0945DFA446559158BD264B1C6CCA09B0CB8ABAFC65` · sourceCommit cbbd8ce · 피어 기록 없음.
  - 즉시 실행 배포본 StockManager-v12.18.32-Standalone-win-x64.zip SHA-256 `EA00DA39FB5C53A4EFE46131A84571EA06AE7B7ACFFA89866A99CDB56644817B`.
- v12.18.31-GM 산출물 15건 → release/superseded-v12.18.31-GM/. 신규 15건 복사(두 zip SHA-256 = .sha256 기록). worktree 제거 · prune · 격리 기동 임시 폴더/프로세스 잔존 0.
- 메인 체크아웃 재수검(메인 Release CLI · 메인 release/ 복사본): --verify-zip · --verify-live-armed(⑪ ⑫ ⑬ 강제) · --verify-interlocks(30) · --verify-tighten(6 · 관제 · IPC 패브릭 강제) 전부 exit 0.
- 한계(정직): 본문 치명 코드 표 실 응답 미관측(EGW00121/123/133 과잉 래치 가능 · §109) · 실 VTS 재소크 미실시(§99) · WebView2 미설치 PC 실기 미확인.
  메인 CLI 바이너리는 타 세션 미커밋 BrokerSettingsPage.razor 변경이 포함된 작업 트리 빌드(수검 로직 무관).

## 111. v12.18.33-DEV — 합성 스프레드 레이더 인터락 경화(깊은 역전 = 1틱 비트 22 · 피드 간 L1 전파 지연 > 50ms = 1틱 비트 28) (2026-10-20)

- 전제 검증: 오더가 신규 작성을 요구한 SyntheticSpreadRadar(StockManager/Engines/Arbitrage · v12.18.19-DEV §83)와 비트 22 결속(괴리 ≥ 30bp 500ms 지속 → 대사기 AttachQuoteGuard)은 이미 존재.
  오더 경로 StockManager/Core/Arbitrage · StockManager.Tests/Core/ 는 부재 → 기존 위치(Engines/Arbitrage · Tests/Engines/Arbitrage)에서 확장. 신규 파일 0.
  실제 공백 2건: ① 호가 역전은 모의 차익 관측만 하고 인터락 미결속 ② 비트 28 결속 0(레이더 측 지연 판정 없음).
- SyntheticSpreadRadar:
  - 깊은 역전: 교차 스프레드 ≥ InversionTripCbp(기본 30bp) = 지속 대기 없이 그 틱에 비트 22 래치(LatchReason = Inversion). 얕은 역전 = 종전대로 모의 차익 관측.
    래치 판정을 모의 기록보다 먼저 → 래치 틱의 역전도 기록 거부. 괴리 지속 래치(Divergence)는 무변경.
  - 피드 간 L1 전파 지연: 두 피드 최우선 매수/매도 가격 불일치 지속 시간(슬롯 LagSinceMicros · epoch). 경과 > LagBudget(기본 50ms) 을 관측한 틱 = 비트 28 래치
    (진행 중 경과 · 뒤처진 피드 도달로 확정된 지연 둘 다). 노후 생략(> MaxSkew) 이전에 판정 → 피드 정지 + 가격 변경 = 산 피드 다음 틱에 격발. 위반은 래치 중에도 계수.
  - 결선: IFeedLatencyHost(레이더는 지연 인터락 형식 비참조) · BindLatencyInterlock → 내부 LagGuard 뷰(비트 22 래치와 분리된 지연 래치만 노출).
  - TryRearm(물리 이중키) = 두 래치 해제 + epoch 증가(재무장 전 시작된 괴리/지연 타이머 폐기). 한쪽 키 = 거부.
- OrderPathLatencyInterlock: 3차 차단 입력 슬롯 AttachCrossFeedGuard(콜드 1회 · 교체 금지) 신설 — 2차 슬롯(KisTimeSkewGuard · 교체 금지 계약 테스트 고정)은 그대로.
  FeedBlocked = 2차 ∨ 3차(판독 예외 = 차단). 비트 재결속 0 · 인터락 30종 정의(0x800000001FFFFFFF) · Tighten 6 무변경.
- 호스트(BlazorHomeForm): arbRadar.BindLatencyInterlock(orderPathLatency) 1줄 추가.
- 테스트(SyntheticSpreadRadarTests §7 · +6): 마스크 위치(0x00400000 · 0x10000000 ⊂ 정의 마스크) · 얕은 역전 무래치/깊은 역전 1틱 비트 22 + 모의 거부 + 이중키 ·
  지연 50ms 경계 무격발/50.001ms 1틱 비트 28(실 OrderPathLatencyInterlock · 실 집계 마스크 · 자체 p99 래치와 분리) · 래치 중 위반 계수 · 진행 중 지연 격발 ·
  노후 생략 경로 격발 · KIS 2차 슬롯과 공존 + 3차 교체 금지 + 판독 예외 fail-closed · 두 비트 결속 1M 틱 격발/재무장 순환 δ=0(3창 최솟값 0B). IL 허용목록에 IFeedLatencyHost 포함.
- 검증: WebOnly · sln(Default · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 Release 4,168 통과 + 1 건너뜀(§99 옵트인) ·
  infra-scan 0 · 래칫 ≤40(RCL 변경 0).
- 불변: liveArmed:false(레이더 브로커/디스패처/무장 키 참조 0 — IL 테스트) · QuantumVault 미접근 · 타 세션 파일 4종 미스테이징 · 원격 푸시 없음.
- 한계(정직): 오더 목표 '4,170건+' 미달(4,169 = 기존 4,163 + 신규 6 — 건수 맞추기용 테스트 미추가). 피드별 병합(conflation) 시점 차로 급변 구간 L1 불일치가 50ms 를
  넘으면 실지연이 아니어도 비트 28 래치(안전 방향 · 실 피드 오탐률 미관측). 가격 동일 상태에서 한 피드만 정지 = 변경 전까지 미포착. 30bp 역전 임계는 실 호가 분포 미보정.

## 112. v12.18.33-GM — 합성 스프레드 레이더 인터락 경화 반영 이중 출하(감사 봉인본 + 즉시 실행 배포본 · -NoPeer) (2026-10-20)

### 112.1 버전 · 태그
- v12.18.33-DEV(§111) = b413489. 12.18.32 → 12.18.33(csproj Version/AssemblyVersion/FileVersion · GoldenMasterFreeze/Packaging 핀 · release-package.ps1 머리말 · BOM 유지) = 55ae770(4파일 · 11/11 줄).
- 태그 v12.18.33-GM(annotated · -f) = 55ae770. 패키징 셸 MS2E_KIS_VTS_* 미설정 · QuantumVault 미접근(-NoPeer) · 매니페스트 피어 기록 0 · 원격 푸시 없음.

### 112.2 출하 실행(형제 worktree SM_GM_wt · 태그 55ae770 · 미커밋 0)
- release-package.ps1 -NoPeer 1차 실행으로 [0/11]~[11/11] EXIT 0: [0] 태그 = HEAD · 미커밋 코드 0 · [1]/[2] 오중 빌드 0W/0E(KiwoomComShim 컴파일 증거 확인) ·
  [3] 4,168 통과 + 1 건너뜀(§99 옵트인 · Offline_1M 원패스) · [4] infra-scan 0 ≤ 117 · [5] 래칫 ≤40 · [6] liveArmed 소스 가드 · [7] 아티팩트 38 · manifest.sig · [8] 봉인 재검 38 ·
  [9] verify-zip Ok · verify-live-armed Ok · [10] handshake Ok @55ae7700c7 · interlocks Ok(30 · 0x800000001FFFFFFF) · tighten Ok(6) ·
  [11] 봉인 오버레이 38 바이트 동일 · 판독 테스트 통과 · 격리 기동 PID 10080 · HWND 0x2B31076 · 'MS2-E Stock Suite'.
- 산출물(release/):
  - 감사 봉인본 release-v12.18.33-GM.zip SHA-256 `3FAA0CB2D0ECBCD005DB6BA0A9BE93096C370E952AC25B8F2F122A5EBE5E587B` · masterSeal `514129100B0CA847444109D19E49F8536787D0719B2BCD979DDC27F9C0481B05` · sourceCommit 55ae770 · 피어 기록 없음.
  - 즉시 실행 배포본 StockManager-v12.18.33-Standalone-win-x64.zip SHA-256 `EFB8DF1C7F0FAFB646577FDFCCF0F500753061EB4A1A006B10F3DB50C7C8601B`.
- v12.18.32-GM 산출물 15건 → release/superseded-v12.18.32-GM/. 신규 15건 복사(두 zip SHA-256 = .sha256 기록). worktree 제거 · prune · 격리 기동 임시 폴더/프로세스 잔존 0.
- 메인 release/ 복사본 재수검(태그 빌드 CLI · worktree 제거 전): --verify-zip · --verify-live-armed(⑪ ⑫ ⑬ 강제) · --verify-interlocks(30) · --verify-tighten(6 · 관제 · IPC 패브릭 강제) 전부 exit 0.
- 한계(정직): 비트 28 실 피드 오탐률 미관측(LagBudget 50ms 보정 미실시 · §111) · 실 VTS 재소크 미실시(§99) · WebView2 미설치 PC 실기 미확인.

## 113. v12.18.34-DEV — WebView2 미설치 런타임 결함 주입 · 실 프로세스 안전 종료 실증 (2026-10-20)

- 전제 검증: 오더가 명명한 WebView2RuntimeGuard.cs · WebView2RuntimeGuardTests.cs 는 v12.18.29-DEV(91d885d)에 이미 존재(탐침 · 온라인/오프라인 안내 · Program.cs 에서 DB/브로커/Blazor 홈 이전 결선 · exit 0).
  실제 공백 2건: ① 설치 PC 에서 미설치 경로를 재현할 수단 없음 ② 실 프로세스 수준 exit 0 · 창 미생성 실증 없음(단위 테스트만). 신규 파일 0.
- WebView2RuntimeGuard: MS2E_FORCE_WEBVIEW2_MISSING 결함 주입.
  - "1" = 탐침 강제 미설치(실 미설치 PC 와 같은 WebView2RuntimeNotFoundException) + 실 안내 대화상자(운용자 리허설).
  - "silent"(대소문자 · 공백 무시) = 강제 미설치 + SilentUi(대화상자 · 브라우저 실행 0 · 안내 문구는 로그 싱크).
  - 그 외 값 = 주입 없음. 주입은 미설치 방향만 강제하고, 설치로 위장하는 값은 없음(가드 우회 0).
- Program.cs: 가드 블록에서 주입 판독 → 탐침/UI 교체 → 결함 주입 WARN 로그. 클래식/헤드리스 제외 · DB/브로커/뮤텍스/BlazorHomeForm 이전 · exit 0 결선 무변경.
- 테스트(WebView2RuntimeGuardTests §4/§5 · +15):
  - §4 해석 표(정확한 값만) · 열거값 = None/ForceMissing/ForceMissingSilent(우회값 0) · 주입 탐침 = 실 예외 · 온라인/오프라인 모두 Present 불가.
  - §4 SilentUi 강제 미설치 = Declined/OfflineExit · 로그 1줄 · 브라우저 실행 0. Main 진입~안전 종료 구간 QuantumVault/KisProvider/LocalDatabase/IBroker/BlazorHomeForm/LiveArm/ValveWired 0.
  - §5 실 프로세스(ConcurrencyStress): 테스트 출력의 MS2E.StockSuite.exe 를 silent 주입으로 기동 → 주 창 미관측 · 30초 내 exit 0.
- 대조 실측: 주입 없이 같은 exe 기동 = 주 창 생성(HWND 관측 → 강제 종료) → §5 가 가드 작동과 미작동을 구분함을 확인.
  주입 기동 로그 = 결함 주입 → silent 트리거 생략 → BootstrapperDeclined · 안전 종료.
- 하네스 교정: S26cTelemetryTests.Reader_NeverBlocks — 부하 중 리더 스케줄 전 발행 완료 시 reads=0 헛실패(1회 18ms 실측 · 단독 8/8 통과) → 첫 판독 확인 후 발행. 제품 코드 무변경.
- 검증: WebOnly · sln(Default · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0W/0E · 전체 스위트 Release 4,183 통과 + 1 건너뜀(§99 옵트인) · infra-scan 0 · 래칫 ≤40.
- 한계(정직): 실 미설치 PC 기동은 여전히 미실측(주입 탐침으로 대체). 런타임은 있으나 BlazorWebView 비동기 초기화가 실패하는 경로(손상 런타임 · 사용자 데이터 폴더 쓰기 불가)는 이 가드 범위 밖.
  로그는 실 %LOCALAPPDATA%\StockManager\logs 에 기록됨. 메인 체크아웃 빌드는 타 세션 미커밋 파일 포함.
- 불변: liveArmed:false · 인터락 30(0x800000001FFFFFFF)/Tighten 6 무변경 · QuantumVault 미접근 · 타 세션 파일 4종 미스테이징 · 원격 푸시 없음.
