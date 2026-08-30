# MS2E StockSuite — Production Release & Canary Deployment Runbook (v11.4.0)

> **문서 등급**: 운영(Operations) · 실거래 관제 담당자 전용
> **적용 버전**: v11.4.0 (master @ `d7e5db4` 이후) · 삼중 빌드 0W/0E · 2,443+ 유닛 테스트 100% Pass 기준
> **절대 불변(물리적 제약)**: `liveArmed:false` · fail-closed · 무할당(δ=0) 핫패스 · 결정론 · 이중 빌드 0W/0E · 한국 관례 색상(상승=빨강·하락=파랑)

---

## 0. 개요 & 안전 철학

본 절차서는 **모의(Mock) 상태 → 0.1% 카나리 → 전체 운용**으로 이어지는 4단계 프로덕션 배포를 규정한다.
핵심 안전 장치는 두 개의 자율 방어 엔진이다:

| 엔진 | 위치 | 역할 |
|---|---|---|
| **CanaryHealthSentinel** | `src/02_Infrastructure/Engine/CanaryHealthSentinel.cs` | 카나리 주문 폭발 반경(Blast Radius) 격리 — 물리 이중 키 게이트 + 한도 초과/이상 감지 시 0ms 원자 킬스위치 + 발주 토큰 0x00 소거 |
| **PureShellFitnessSentinel** | `src/02_Infrastructure/Engine/PureShellFitnessSentinel.cs` | 호스트 어셈블리 결합 UI 오염 방지 — 결합 컴포넌트 수를 단조 래칫으로 강제(신규 유입 시 CI 거부) |

### ★ liveArmed:false 최상위 불변
- 앱은 **기본적으로 실 발주가 물리적으로 불가능**한 상태로 부팅한다.
- 실 발주는 **물리 이중 키가 둘 다 배선**(`ValveWired ∧ LiveArmingWired`)됐을 때만 가능하다.
- `CanaryHealthSentinel.Arm(cap, valveWired, liveArmingWired)` 는 두 키가 모두 `true` 가 아니면 **무장 실패**(disarmed 유지) → `TryAuthorize()` 는 **항상 0(거부)**. 즉 **물리 열쇠 없이는 단 1비트의 발주도 인가되지 않는다.** (유닛 테스트 `Phase114CanarySentinelTests.Arm_Requires_Both_Physical_Keys` 로 상시 검증.)

---

## Stage 1 — Mock Staging & Zero-Leak Audit (`liveArmed:false`)

**목표**: 실 발주 0인 상태에서 토스/키움 이중 실통신의 조회·관제 경로가 정상 동작하고, 모든 발주 시도가 Veto 됨을 감사.

### 1.1 진입 조건
- [ ] `git rev-parse HEAD` 가 릴리스 태그와 일치.
- [ ] 삼중 빌드 0W/0E: `dotnet build -c Release` · `dotnet build -c Release -p:WebOnly=true` · PrimesBench.
- [ ] KiwoomProxy 골든마스터 0W/0E: `dotnet build StockManager.KiwoomProxy -c Release`.
- [ ] 전체 스위트 100% Pass: `dotnet test -c Release`.

### 1.2 이중 실통신 조회 검증
1. **이중 관제 대시보드** `/admin/dual-live` (`DualLiveBrokerControlPanel`) 진입.
2. 키움 라인: `IsProxyAlive` 점등 · IPC RTT/pong 카운트 갱신 확인.
3. 토스 라인: `HasValidToken`/`SecondsUntilExpiry`/`Http429` 점등 확인.
4. 양 브로커 **평가자산/보유종목 병렬 비교표**가 실 잔고(또는 미동기화 fail-closed)를 정직 렌더하는지 확인 — **가짜 잔고 0**.

### 1.3 Zero-Leak Veto 감사 (필수)
- [ ] `IDualBrokerStatusPort.LiveArmed == false` (관제 배너 `liveArmed: FALSE` 확인).
- [ ] 조회/관제 Port 에 발주/변조 메서드가 **0개**임을 리플렉션으로 재확인 (`Phase94DDualBrokerVetoTests`).
- [ ] `CanaryHealthSentinel` 미무장 상태에서 `TryAuthorize(any) == 0` (발주 인가 봉인).
- **판정**: 위 3개 중 하나라도 실패 시 **배포 중단**. Stage 2 진입 금지.

### 1.4 Exit Gate
> ✅ 이중 실통신 조회 정상 · 발주 경로 100% Veto · 가짜 데이터 0 → **Stage 2 승인**.

---

## Stage 2 — Canary Micro-Arming (0.1%)

**목표**: 물리 이중 키 1단계 점화 후, **총자산의 0.1% 이내** 소액 카나리 발주로 실통신 왕복을 실증.

### 2.1 물리 이중 키 점화 (2인 규칙 권장)
- **Key A — ValveWired**: 발주 밸브 하드웨어 키 삽입/배선.
- **Key B — LiveArmingWired**: 라이브 무장 하드웨어 키 삽입/배선.
- 두 키가 **동시에** 배선된 순간에만 무장 시도 가능.

### 2.2 카나리 한도(Circuit Breaker) 설정
```csharp
// 총자산 조회(조회 전용) → 0.1% 명목 한도 산정 → 물리 이중 키로 무장
long capRaw = (long)(totalAssetsRaw * 0.001m);          // ★0.1% 상한
bool armed = canary.Arm(capRaw, valveWired: keyA, liveArmingWired: keyB);
// armed == false 이면(키 결여/한도≤0) 실 발주는 물리적으로 불가 — 절대 우회 금지.
```
- [ ] `canary.Arm(...)` 가 `true` 반환(두 키 모두 배선 확인).
- [ ] `canary.CapRaw == 총자산 × 0.1%`.
- [ ] 단건 카나리 주문 명목액 ≤ 종목당 서브한도(권장: cap 의 1/10).

### 2.3 카나리 발주 프로토콜
- 각 카나리 주문 직전 `long token = canary.TryAuthorize(notionalRaw);` 호출.
  - `token > 0` → 인가(발주 진행). `token == 0` → **거부(발주 금지)**.
- 누적 인가액이 cap 을 **초과하는 순간 단 1회라도** → 센티널이 **즉시 Kill**(무장 해제 + 토큰 소거 + sticky 락아웃). 이후 모든 발주 0.
- 락-프리 CAS 로 다중 발주 스레드에서도 **한도 누출 0** (`Phase114CanarySentinelTests.Concurrent_Authorize_No_Budget_Leak`).

### 2.4 Exit Gate
> ✅ 카나리 왕복 정상(체결/취소/잔고 반영) · cap 누출 0 · 미인가 발주 0 → **Stage 3 상시 관찰로 이행**.

---

## Stage 3 — Real-Time Safety Interlock (50ms Disarm)

**목표**: 운용 중 이상 징후 발생 시 **50ms 이내 자동 무장 해제 + 안전 락아웃**.

### 3.1 이상 트리거 → 즉시 Kill 매핑
| 트리거 | 감지원 | 조치 |
|---|---|---|
| 틱 레이턴시 임계 초과 | `CrossBrokerDispatchCoalescer` 헬스 / 브로커 RTT | `canary.NoteAnomaly(LatencyBreach)` |
| API 429 초과 | `TossLiveApiGovernor.Http429` 증가 | `canary.NoteAnomaly(NetworkAnomaly)` |
| 프록시 단선 | `KiwoomBridgeClient.IsProxyAlive == false` | 코얼레서 `SafeHold(브로커)` + `canary.NoteAnomaly(NetworkAnomaly)` |
| 슬리피지 오차 초과 | 체결가 vs 기준가 편차 | `canary.NoteAnomaly(SlippageBreach)` |

### 3.2 Disarm/Lockout 시퀀스 (원자적)
1. `canary.Kill(reason)` → `_armed=0`(즉시 무장 해제) · `_token` **0x00 소거**(메모리 잔재 0) · `_killLatch=1`(sticky).
2. 이후 `TryAuthorize()` 는 재무장 전까지 **항상 0**.
3. 이종 브로커 라인은 `CrossBrokerDispatchCoalescer.SafeHold` 로 UI 격리(다른 라인 라이브니스는 유지).
4. **재개는 자동 금지** — 운영자가 원인 규명 후 **물리 이중 키로만 재무장**(`Arm`).

### 3.3 검증
- [ ] 모의 429 주입 → 50ms 내 `IsArmed == false` · `IsKilled == true` · `LastKillReason == NetworkAnomaly`.
- [ ] Kill 후 `TryAuthorize()` 전량 0 (sticky).
- **판정**: 상시 관찰 중 임계 이벤트가 반복되면 Stage 2 로 롤백.

### 3.4 Exit Gate
> ✅ 유의 관찰 구간(권장 ≥ 1 거래일) 무이상 · 인터락 정상 동작 실증 → **Stage 4 승인**.

---

## Stage 4 — Full Deployment Rollout

**목표**: 카나리 검증 통과 후 한도를 단계적으로 상향하며 전체 자산 운용으로 전환.

### 4.1 단계적 한도 상향(권장 스텝)
`0.1% → 1% → 5% → 25% → 100%` — 각 스텝 진입 전 Stage 3 인터락 무이상 24h 확인.
```csharp
canary.Kill(CanaryKillReason.Manual);                    // 기존 무장 안전 해제
long nextCap = (long)(totalAssetsRaw * stepPct);          // 다음 스텝 한도
canary.Arm(nextCap, valveWired: keyA, liveArmingWired: keyB);   // 물리 키로 재무장
```
- 각 스텝은 **독립 무장** — 이전 스텝의 인가 이력은 재무장 시 초기화(`_spentRaw=0`).

### 4.2 롤백 절차
- 이상 시 즉시 `canary.Kill(Manual)` → 전 발주 봉인 → 직전 안정 스텝으로 복귀.
- 코드 롤백: `git revert <release>` 후 삼중 빌드 0W/0E + 전체 스위트 재확인.

### 4.3 배포 후 상시 게이트
- [ ] **PureShellFitness 래칫**: `PureShellFitnessSentinel.Scan(host).WithinRatchet == true` (호스트 결합 UI ≤ 래칫 = 83). 신규 UI 콘크리트 오염 시 CI 거부.
- [ ] 삼중 빌드 0W/0E · KiwoomProxy 골든마스터 0W/0E · 전체 스위트 100% 유지.

---

## 부록 A — 배포 검증 체크리스트(요약)

```
[ ] 삼중 빌드 0W/0E (Default · WebOnly · PrimesBench)
[ ] KiwoomProxy TreatWarningsAsErrors 0W/0E
[ ] 전체 유닛 스위트 100% Pass
[ ] ArchitectureFitnessAnalyzer PASS · MS2E.UI.Portable 0 Concrete Ref
[ ] PureShellFitness 래칫 준수(결합 UI ≤ 83, 상향 유입 0)
[ ] liveArmed:false — 물리 이중 키 없이 CanaryHealthSentinel 무장 불가 실증
[ ] Stage 1 Zero-Leak Veto 감사 통과(발주 경로 100% Veto)
```

## 부록 B — 관련 모듈 지도

- 이중 실통신 관제: `MS2E.UI.Portable/Pages/BrokerControl/DualLiveBrokerControlPanel.razor` + `IDualBrokerStatusPort`(조회 전용)
- 이종 브로커 UI 디스패처 격리: `MS2E.UI.Portable/Infrastructure/CrossBrokerDispatchCoalescer.cs` (SafeHold 래치)
- 카나리 폭발 반경 격리: `src/02_Infrastructure/Engine/CanaryHealthSentinel.cs`
- Pure Host Shell 강제: `src/02_Infrastructure/Engine/PureShellFitnessSentinel.cs`
- 토스 토큰/429: `TossTokenService.SecondsUntilExpiry()` · `TossLiveApiGovernor.Http429`
- 키움 하트비트: `KiwoomBridgeClient.IsProxyAlive` · RTT/pong

> ⚠️ **경고**: 본 절차서의 어떤 단계도 `liveArmed:false` 불변을 우회하는 수단으로 사용될 수 없다. 물리 이중 키는
> 소프트웨어로 시뮬레이트하지 않는다. 카나리 센티널은 물리 무장 열쇠 없이 절대 작동하지 않는다(유닛 테스트로 상시 보증).
