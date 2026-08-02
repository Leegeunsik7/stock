# MS2E StockSuite — Architecture & Release Attestation

> 본 문서는 **v10.1.0 Final Release Packaging** 서브시스템(나노 SLA 패브릭 + 릴리스 SHA-256 포렌식 봉인)의
> 아키텍처와 **배포 무결성 각인 절차**를 기록한다. 전체 시스템 개요/버전 이력은 [HANDOVER.md](HANDOVER.md) 참조.

---

## 1. 절대 불변식 (Non-Negotiable Invariants)

| 불변식 | 의미 |
|---|---|
| **liveArmed:false** | 물리 이중 키(`Dispatch.ValveWired ∧ Dispatch.LiveArmingWired`, 기본 false) 완전 결속 전 실 브로커 소켓으로 **단 1비트도 실물 발주 누수 없음**. SLA 100점·봉인 유효·마스터 인증이어도 **무장을 부여하지 않는다**(모든 게이트는 tighten-only — 무장을 '거둘' 뿐). |
| **δ=0 hot path** | 계측/판정/상태 조회 핫패스(Admit·Record·상태 읽기)에서 힙 할당 0(값타입·Int128·Interlocked/CAS). |
| **결정론** | 판정 로직은 벽시계/RNG 무참조 — 입력 표본에만 의존. 시계는 주입 가능(테스트 재현). |
| **fail-closed** | 미검증·SLA 이탈·측정 회귀·봉인 불일치 → 차단(개방 아님). |
| **이중 빌드 0W/0E** | Default(`dotnet build -c Release`) + WebOnly(`-p:WebOnly=true`) 모두 0 경고/0 오류(`TreatWarningsAsErrors`). |

---

## 2. 나노 SLA 패브릭 (Nano SLA Fabric)

```
OrderDrainWorker.DrainOnce()  ──(드레인 사이클 지연 δ=0 계측: Stopwatch 틱→ns Int128)──▶
      SlaJitterRegressionGuard.Admit(nanos)   ─ clean? ─▶  LatencySlaGatekeeper.Record(nanos)
                    │                                              │
          (오염=디스케줄 아티팩트/역행시계 배제)          (δ=0 로그-리니어 히스토그램·192 버킷)
                    │                                              │
     RingTelemetry.Sample() ──▶ slaGate.EvaluateSla() (단일 평가자·P99.99 상계·이탈 시 비가역 트립)
```

- **`LatencySlaGatekeeper`** — δ=0 P99.99 지연 히스토그램 + SLA 이탈 비가역 트립(fail-closed). `IsBreached`가 `liveArmed` blockWhile에 결속되어 이탈 시 무장 tighten. ForceFreeze(영구 락업)는 **옵트인**(`Sla.FuseArmed`, 기본 OFF — 기본은 tighten-only).
  - Percentile은 **2패스 버킷합**을 total로 사용해 `_count`/버킷 torn read를 제거(과소보고=fail-open 봉쇄).
- **`SlaJitterRegressionGuard`** (★Think Beyond) — 벤치마크 시계 지터가 SLA 퓨즈를 오트립(→불필요한 거래 영구 동결)시키는 재앙을 격리. **물리적 개연성** 판별: `max(절대 천장, admit-EWMA baseline × 배수)`를 초과하는 표본은 OS 프리엠션 아티팩트로 간주해 배제(baseline은 admit 표본만 갱신 → 오염 면역). 배제율이 회귀하면 `RegressionTripped` 원자 래치 → 인증/무장 차단.

계측 지점 정직 고지: per-op QPC 계측은 sub-100ns op을 왜곡(관측자 효과)하므로 **드레인 사이클 경계**에서 측정한다.
SLA 예산(`Sla.P9999BudgetNanos`, 기본 500µs)은 사이클 지연 스케일로 설정. 히스토그램 해상도는 나노초.

---

## 3. 릴리스 SHA-256 포렌식 봉인 (Release Attestation)

### 3.1 봉인 생성 (콜드·패키징 시)
```
masterSeal = SHA-256( CausalAuditVault 제네시스 헤드 해시(32B)  ‖  Σ 정렬된 아티팩트별 SHA-256 )
```
- `ReleasePackager.GenerateManifestJson(version, artifactPaths)` → `vX.Y.Z_release_manifest.json`.
- 제네시스 결합으로 (a) 아티팩트 위·변조, (b) 집합 누락/추가, (c) 우리 제네시스 계보를 **동시 검증**(타 빌드는 genesis가 달라 seal이 갈린다).

### 3.2 런타임 검증 (δ=0 상태 조회)
- `ReleaseArtifactSha256Attestor` — 실행 바이너리 SHA-256을 콜드 1회 산출·캐시하고, 외부 신뢰 매니페스트의 기대 봉인(`Release.ExpectedMasterSeal`)과 대조. 일치 시에만 `SealValid=1`.
  - **fail-closed 기본**: 기대 봉인 미주입 = 미검증 = 무효(마스터 인증 불가). 부트스트랩 순환(자가-해시)을 피해 기대 봉인은 반드시 외부에서 주입.
  - `O(1) 무할당`의 정직한 범위: **런타임 상태 조회**(`SealValid`/`SealHex64`)만 δ=0. SHA-256 산출 자체는 O(n)·콜드 1회.
- 검증 결과는 `IgnitionAuditTrailRecorder`(δ=0 락프리 링·µs+FNV 해시)에 `ReleaseAttestation` 이벤트로 **각인**된다.

### 3.3 마스터 릴리스 인증 (텔레메트리)
`RingTelemetry.MasterReleaseCertified = 1` ⇔ **실 SLA 측정(≥1표본) ∧ P99.99 미이탈 ∧ 봉인 검증 성공 ∧ 지터 회귀 없음** (넷 모두·fail-closed). 관제 UI 📦 패널에 청/녹 `[v10.1.0 MASTER RELEASED - NANO SLA CERTIFIED]` 점등. **인증은 무장을 부여하지 않는다** — 물리 이중 키가 지배한다(liveArmed:false).

---

## 4. 패키징 절차

```bash
pwsh ./package-release.ps1 -Version 10.1.0
```
1. Release + ReadyToRun(R2R) 퍼블리시 (WinForms/Blazor Hybrid은 Native AOT 미지원 — R2R이 정확한 선택).
2. 아티팩트 SHA-256 수집.
3. `ReleasePackager.WriteManifest(...)`로 제네시스-봉인 마스터 매니페스트 생성.
4. 운영자가 `masterSeal`을 `Release.ExpectedMasterSeal` config에 주입 → 다음 기동부터 런타임 무결성 검증 활성.

---

*이 문서에 기록된 seal 메커니즘의 실제 값은 빌드별로 상이하며 패키징 시 매니페스트 JSON에 봉인된다.*
