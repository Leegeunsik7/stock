# MS2E StockSuite — v12.0.2 물리 하드웨어 현장 운용 런북 (Physical Hardware Field Runbook)

> **적용 기준**: v12.0.2 · Release 삼중 빌드 0W/0E · 전체 유닛 테스트 100% Pass
> **핵심 철칙**: `liveArmed:false` 불변 · fail-closed · 무할당(δ=0, Release 표준) 핫패스 · 결정론 · 한국 관례 색상(상승/이익=빨강, 하락/손실=파랑)
> **선행 문서**: [`v12.0.0_GOLDEN_MASTER_IGNITION.md`](v12.0.0_GOLDEN_MASTER_IGNITION.md)(점화 수순) · [`v12.0.0_FINAL_FORENSICS_REPORT.md`](v12.0.0_FINAL_FORENSICS_REPORT.md)(아키텍처 포렌식)

---

## ⚠️ 0. 안전 경계 (Safety Boundary) — 필독

> **에이전트/자동화는 물리 키를 절대 무장하지 않으며, 실 금융 자격증명을 입력하지 않는다.**
> 본 런북의 모든 물리 점화 수순은 **물리 키 접점을 가진 운영자만** 수행한다. 소프트웨어·에이전트가
> 검증 가능한 것은 **적대적 시뮬레이션**(`Stage1CanaryIgnitionSimulationTests`, `HardwareKillSwitchStressTests`,
> `Phase99HardwarePulseGuardTests`)뿐이며, **실 매매는 0**이다. 실 발주 인가는 물리 이중 키가 **둘 다**
> 접점 연결됐을 때만 성립한다(`CanaryHealthSentinel.TryAuthorize > 0`).

이 문서는 **소프트웨어 안전 계약이 현장 물리 하드웨어에 어떻게 사상(map)되는가**를 규정한다. 실 배선·실 봉인·실
접점은 인증된 하드웨어 담당 운영자의 영역이며, 본 런북은 그 **결선 규약과 예외 대응 프로토콜**을 명세한다.

---

## 1. 물리 계층 ↔ 소프트웨어 계약 사상표

| 물리 하드웨어 | 소프트웨어 심볼 | 코드 위치 | 계약 |
|---|---|---|---|
| USB/COM 물리 버튼 (킬스위치) | `HardwareInterruptSpinLock` → `HardwareKillSwitchBridge.Signal` | [BlazorHomeForm.cs:907](../StockManager/Blazor/BlazorHomeForm.cs) | ISR 펄스 → 킬 신호 전달 |
| 접점 채터/전기 노이즈/위조 신호 | `HardwarePulseGuard.ShouldFire` | [HardwarePulseGuard.cs](../src/01_Core/HardwarePulseGuard.cs) | 디바운스 + 단조 토큰 안티탬퍼 |
| 밸브 물리 키 (1차) | `CanaryHealthSentinel.Arm(cap, valveWired:true, …)` | [BlazorHomeForm.cs:903](../StockManager/Blazor/BlazorHomeForm.cs) | `ValveWired` 접점 |
| 라이브 무장 물리 키 (2차) | `CanaryHealthSentinel.Arm(cap, …, liveArmingWired:true)` | 〃 | `LiveArmingWired` 접점 |
| DPAPI 암호화 금고 봉인 | `IGoldenMasterProbe.VaultSealed` | [BlazorHomeForm.cs:1177](../StockManager/Blazor/BlazorHomeForm.cs) | 평문 시크릿 미배치 |
| 케이블 단선/신호 차단 | `killBridge.Killed += …` (Disarm+Purge) | [BlazorHomeForm.cs:1134](../StockManager/Blazor/BlazorHomeForm.cs) | 0ms 원자 Disarm + 메모리 중독 |
| API Secret 휘발성 버퍼 | `PoisonedSecretBuffer.PurgeAndPoison` / `VolatileMemoryPurger.PurgeAll` | [SignalDemuxer.cs](../src/01_Core/SignalDemuxer.cs) | 0x00 소거 + 중독 표식 |

---

## 2. USB/COM 물리 버튼 회로 결선 (Kill-Switch Wiring)

### 2.1 회로 규격 (개념 도해)

물리 킬스위치는 **상시 닫힘(NC, Normally-Closed)** 접점으로 배선한다. 이는 **케이블 단선 = 신호 차단 = 킬 격발**
(fail-closed)을 보장하기 위함이다. 상시 열림(NO)으로 배선하면 단선 시 킬이 격발되지 않아(fail-open) 안전 철칙에 위배된다.

```
   [물리 버튼/릴레이]        [디바운스 R-C]          [USB-Serial / COM]         [호스트 ISR]
                                                                          
   VCC ──┬── NC 접점 ──┬──── 10kΩ ──┬──── DTR/CTS ────── COMx ───▶ HardwareInterruptSpinLock
         │             │            │                                      │
         │            (눌림 → 개방)  ═╪═ 100nF (하드 디바운스 1차)            ▼
         └── GND ──────┘            GND                          HardwarePulseGuard.ShouldFire
                                                                          │
                                                    Debounced→억제  Accept∧Tamper→killBridge.Signal
```

- **1차 디바운스(하드웨어)**: R-C 회로(≈1ms)로 전기적 채터 1차 흡수.
- **2차 디바운스(소프트웨어)**: `HardwarePulseGuard` 디바운스 창 **5ms**(`Stopwatch.Frequency / 200`, 200Hz)로
  잔여 채터·전기 노이즈·위조 펄스를 걸러낸다(§3).
- **접점 극성**: NC 배선 필수. 단선/탈거 → 신호 부재 → 워치독이 킬 격발(§5).

### 2.2 COM 포트 매핑 확인

```powershell
# 물리 킬스위치 COM 포트 식별(운영자 수행 — 실제 포트 번호 확인용)
[System.IO.Ports.SerialPort]::GetPortNames()
```

식별된 포트를 킬스위치 워치독 설정에 결선한다. 포트 번호는 **설치 PC마다 상이**하므로 배포 산출물에 하드코딩하지
않는다(설치 규약 — 배포 installer 는 사용자 데이터/자격증명/포트 미포함).

---

## 3. 하드웨어 펄스 안티탬퍼 (v12.0.2 Think-Beyond Engine ①)

### 3.1 위험 가정

물리 버튼·릴레이 접점은 한 번 눌러도 수 ms 동안 수십 개의 전기 에지를 튀긴다(chatter). 이를 그대로 인터럽트로
받으면 **하나의 물리 동작이 수십 개 격발로 증폭**된다. 더 나쁜 것은 전기 노이즈/위조 신호가 정당한 인터럽트로
위장해 주입되는 경우다(재생/역행 공격).

### 3.2 3분류 판정 (`HardwarePulseGuard.Evaluate`)

각 하드웨어 펄스는 **원자 단조 토큰**(`seq`)과 **주입 tick**(`Stopwatch.GetTimestamp()`)으로 판정된다:

| 판정 | 조건 | 격발 여부 | 의미 |
|---|---|---|---|
| **Accept** | 단조 전진(`seq > 마지막수용`) + 디바운스 창 밖 | ✅ 격발 | 정당한 물리 동작 |
| **Debounced** | 단조 전진 + 디바운스 창 안(`< 5ms`) | ⛔ 억제 | 접점 채터(바운스) |
| **Tamper** | 역행/재생(`seq ≤ 마지막수용`) | ✅ 격발 | 위조 신호 → **fail-closed 안전측 Disarm** |

> **fail-closed 원칙**: `ShouldFire`는 **Accept 와 Tamper 모두 격발**한다. 위조/재생 신호도 안전측(Disarm)으로
> 귀결시켜 "의심스러우면 끈다". **Debounced 만 억제**하며, 격발은 sticky 이므로 **정당한 킬은 절대 유실되지 않는다**
> (첫 펄스는 미초기화 상태라 무조건 Accept).

### 3.3 결선 위치

[BlazorHomeForm.cs:905~911](../StockManager/Blazor/BlazorHomeForm.cs) — ISR 포워더가 raw 펄스를 가드에 통과시킨 뒤에만
`killBridge.Signal` 을 호출한다. `Evaluate` 는 δ=0·락프리(Interlocked CAS/Volatile) 핫패스이며, 진단 로그는
`[Conditional("DEBUG")]` 로 Release 에서 제거되어 무할당이 유지된다.

---

## 4. 물리 이중 키 접점 프로토콜 (Dual-Key Contact Protocol)

> ⚠️ **운영자 전용.** 에이전트는 이 수순을 수행하지 않는다(시뮬레이션만).

`liveArmed:false` 는 소프트웨어 단독으로 절대 해제되지 않는다. 무장은 **밸브 키 ∧ 라이브 무장 키**가 둘 다 접점
연결됐을 때만 성립한다(논리곱).

1. **콜드 부팅 확인** — 기동 직후 `CanaryHealthSentinel.IsArmed == false` · `IsKilled == false` ·
   `[GoldenMaster]` 부팅 무결성 로그 결함 없음.
2. **1차 — 밸브 키 접점** (`ValveWired = true`): 물리 키 삽입/회전.
3. **2차 — 라이브 무장 키 접점** (`LiveArmingWired = true`): 물리 키 삽입/회전.
4. **소액 무장** — `Arm(capRaw, valveWired:true, liveArmingWired:true)`. **한 키라도 결여 시 `Arm` 실패 + disarmed
   유지**(fail-closed). Stage-1 cap = 총자산 × **0.1%**(`총자산 × 0.001`).
5. **1건 소액 발주 실증** — `TryAuthorize(notional)` 가 비영 토큰을 1회 반환. 누적이 cap 을 **1원이라도 초과 → 즉시 Kill**.

**단일 키 결여 = 100% Veto** — 검증: `Stage1CanaryIgnitionSimulationTests.PhysicalKeys_NotBothWired_Vetoes_All_Orders`
(밸브만/라이브무장만/둘 다 없음 → 5,000 발주 시도 전량 0 인가).

---

## 5. 케이블 단선 · 하드웨어 신호 차단 예외 대응 (Fail-Closed Exception Response)

케이블 단선·탈거·전원 차단은 NC 배선에 의해 **신호 부재**로 나타나며, 워치독이 이를 킬로 해석한다. 격발 시 다음이
**원자적(<50ms, 사실상 동기)** 으로 연쇄된다:

1. **전역 Disarm** — `HardwareKillSwitchBridge.Signal(reason)` → sticky `_killed`. 이후 어떤 소프트웨어 명령으로도
   `liveArmed` 복구 불가.
2. **CancellationToken 취소** — 진행 중 모든 퀀트/발주 토큰 취소(`bridge.Token.IsCancellationRequested`).
3. **0ms 토큰 소거(0x00)** — `CanaryHealthSentinel.Kill` → `_armed=0` + 발주 토큰 버퍼 0x00 + sticky 락아웃.
4. **API Secret 메모리 중독** — `VolatileMemoryPurger.PurgeAll`(등록 버퍼 0x00) + `PoisonedSecretBuffer.PurgeAndPoison`
   (0x00 소거 + **중독 표식**). 중독 후 `ReadByte`/`CopyTo`/`AsSpan` → `ObjectDisposedException`(뒤늦은 재참조 차단).

**검증**(시뮬레이션): `Stage1CanaryIgnitionSimulationTests.HardwareKillSwitch_During_Ignition_Disarms_And_Poisons` —
점화 중 킬스위치 주입 → 카나리 Kill + Secret 0x00 중독 + 토큰 취소 + `<50ms` + 발주 0.

**재무장 규약**: `TryRearm(valveWired:true, liveArmingWired:true)` — **물리 이중 키만**. 소프트웨어 명령 단독 복구
불가(sticky). 단일 키로는 재무장 실패.

---

## 6. DPAPI 암호화 금고 물리 봉인 검증 (Vault Seal Verification)

API 키/시크릿은 **DPAPI at-rest 암호화**로만 저장되며 **평문 배치는 절대 금지**된다. 골든마스터 부팅 무결성
센티널의 `VaultSealed` 축이 이를 강제한다([BlazorHomeForm.cs:1177](../StockManager/Blazor/BlazorHomeForm.cs)).

### 6.1 물리 봉인 확인 절차 (운영자 수행)

```powershell
# 1) 금고 경로에 평문 시크릿 파일이 없는지 확인(DPAPI 암호문만 존재해야 함)
$vault = Join-Path $env:LOCALAPPDATA 'MS2E.StockSuite'
Get-ChildItem $vault -Recurse -File -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -match 'secret|apikey|token|cred' } |
  Select-Object FullName, Length
# 2) 발견된 파일이 DPAPI 암호문인지 확인(엔트로피/헤더) — 평문 JSON/KEY=VALUE 이면 ★봉인 파손
```

- **봉인 무결**: 파일 부재 또는 DPAPI 암호문만 존재 → `VaultSealed = true`.
- **봉인 파손**: 평문 시크릿 발견 → 즉시 **부팅 거부**(strict 골든마스터 빌드) + 운영자 재봉인.
- **배포 산출물**: installer 는 사용자 데이터/자격증명/API 키를 **일절 포함하지 않는다**(설치 후 현장 봉인).

---

## 7. 야간 포렌식 워치독 (v12.0.2 Part 2) — 상시 회귀 감시

CI/CD 파이프라인은 [`NightlyForensicWatchdog.Scan`](../src/02_Infrastructure/Engine/NightlyForensicWatchdog.cs) 을
주기 구동하여 4대 축을 무타격 정적 스캔한다. **단 1축 위반 → fail-closed(빌드 중단) + 포렌식 위반 로그**:

| 축 | 판정 | 위반 시 |
|---|---|---|
| `RclZeroEngineRef` | `MS2E.UI.Portable` 0 엔진참조 | 순수성 붕괴 → 중단 |
| `HostCoupledWithinRatchet` | 결합 UI ≤ 래칫 | 신규 오염 → 중단 |
| `RatchetCeilingHeld` | `RatchetCeiling ≤ 40` (감축선) | 상한 상향 회귀 → 중단 |
| `Delta0HotPath` | 핫패스 힙 할당 0 | 메모리 누수 → 중단 |

핫패스 δ=0 는 [`ZeroAllocMemoryProfiler.MeasureLeak`](../src/01_Core/HardwarePulseGuard.cs)(O(1) 스레드 누적할당
스냅샷 비교)로 실측해 주입한다. 검증: `NightlyForensicWatchdogTests`(청정 → AllClean · 누수 주입 → fail-closed +
위반 로그) · `NightlyForensicWatchdogE2ETests`(실 핫패스 측정 0 → 워치독 청정).

---

## 8. 현장 배치 전 최종 체크리스트

- [ ] Release 삼중 빌드 `0W/0E` (`StockManager.csproj` · `-p:WebOnly=true` · PrimesBench)
- [ ] x86 KiwoomProxy `TreatWarningsAsErrors` `0W/0E`
- [ ] 전체 유닛 테스트 Release 2연속 클린(δ=0 판정 표준)
- [ ] `NightlyForensicWatchdog` 4대 축 AllClean
- [ ] `GoldenMasterBootSentinel` 5대 축 AllPassed(`VaultSealed` 물리 봉인 §6 확인)
- [ ] 킬스위치 NC 배선 + 단선 격발 시뮬레이션 검증(§5)
- [ ] `HardwarePulseGuard` 디바운스 창·안티탬퍼 시뮬레이션 검증(`Phase99HardwarePulseGuardTests`)
- [ ] 물리 이중 키 미결속 시 100% Veto 실증(§4, 시뮬레이션)
- [ ] `liveArmed:false` 불변 — 소프트웨어 단독 무장 불가 재확인

> **최종 원칙**: 실 매매 0. 모든 물리 점화는 인증 운영자 + 물리 이중 키. 에이전트는 시뮬레이션으로만 검증하며,
> 의심스러우면 **끈다**(fail-closed).
