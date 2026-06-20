# S21 #1 — 카오스 엔지니어링 인젝터 아키텍처 청사진 (The Chaos Injection Layer)

> **상태**: 설계 명세 (Architectural Blueprint) — 구현 코드 없음
> **버전 기준**: v4.8.0 (1004 테스트) 위에 적층 · v5.0.0 라이브 점화 직전 방어선
> **설계 철칙**: 외부 의존성 0 · 순수 C# 결정론 · **고의 파괴 코드는 프로덕션 밖** · fail-closed 불변식 검증

---

## 0. 설계 사상 & 핵심 판단

v4.8.0 드라이런 조립체에 **인위적 번개(악성 장애)**를 꽂아, 라이브 점화 전 1004개 성벽이
극한 런타임 크래시에서도 fail-closed·무누수·복구를 보장하는지 검증한다.

| # | 핵심 판단 | 근거 |
|---|-----------|------|
| **J1** | **카오스 하니스는 테스트 사이드(StockManager.Tests)** — 프로덕션에 `ChaosAttestationInjector.cs` 같은 고의 파괴 클래스를 출하하지 않는다 | 안전 크리티컬 트레이딩 앱에 '장전된 총'을 넣는 것은 안티패턴. 카오스는 본질적으로 검증 도구다 |
| **J2** | 프로덕션은 필요 시 **명시·기본 OFF 주입 seam만** 노출 (선례: `GreenhouseService.CycleFaultHook`) | 최소 침습. 데드 스위치 없음 |
| **J3** | 모든 카오스는 **결정론적(seeded FNV)** — 실패는 재현 가능해야 함 | 카오스 ≠ 플래키. 동일 시드 → 동일 장애열 |
| **J4** | 검증 대상은 **불변식**(아래 §3)이지 "안 죽음"이 아니다 | fail-closed = 죽는 게 정답인 경로가 있다(동결) |

```
        ┌─────────────────── StockManager.Tests (카오스 하니스) ───────────────────┐
        │  ChaosBroker · ChaosMacroFeed · MmfCorruptor · ChaosDropper · ChaosClock  │
        │     (장애 주입 테스트 더블 — seeded, 프로덕션 미출하)                      │
        └───────────────────────────────│──────────────────────────────────────────┘
                                          │ 주입(inject)
        ┌─────────────────────────────────▼─────────────────────────────── 검증 대상(프로덕션) ─┐
        │  StateHydrationEngine(MMF) · AlgoExecutionGate · ShadowExecutionRouter                │
        │  MultiBrokerRouter/IBroker · OrderAttestationService · MemoryLeakGuard · RingBufferCore │
        └────────────────────────────────────────────────────────────────────────────────────────┘
                                          │ 관측
                              불변식 단언(§3): fail-closed · 무누수 · 복구 · 무torn · 호스트 생존
```

---

## 1. 카오스 표적 매트릭스 (어디에 어떤 장애를 주입하나)

| 표적 (프로덕션) | 주입 장애 | 기대 거동 (불변식) | 도구 |
|-----------------|-----------|---------------------|------|
| `StateHydrationEngine` MMF | 부팅 스냅샷 바이트 무작위 오염(seeded) | `BootIntegrityChecker` → fail-closed 동결 + 스냅샷 폐기 | `MmfCorruptor` |
| `IBroker`/`MultiBrokerRouter` | `Place()` 예외 난사·타임아웃·부분 실패 | 라이브 모드서 `AlgoExecutionGate` 동결, 드라이런선 영향 0 | `ChaosBroker` |
| `KiwoomBridgeClient` | `ProxyLost` 이벤트 폭격 | `OrderAttestationService.ForceFreeze` 전파 → 게이트·라우터 차단 | `ProxyLost` 직접 발화 |
| `AlgoExecutionGate` 디싱크 | 원장 1주 불일치 난수 주입 | 즉시 `EmergencyFreeze` + 라우터 분배 거부 | `DesyncBooks` 생성기 |
| `GreenhouseService` 사이클 | `CycleFaultHook` 예외 주입(기존 seam) | 격리 방화벽 흡수, 메인 GatingMode 무사, 누수 0 | `CycleFaultHook` |
| `RingBufferCore` | 강제 추월(생산자 폭주) → 드롭 | seqlock torn 0, 단조성 유지, Dropped 정확 계측 | `ChaosDropper`(고속 생산) |
| `MemoryLeakGuard` | 카오스 종료 후 잔여 리스 점검 | LiveCount 가 베이스라인으로 수렴(0주 누수) | 직접 Probe |
| `MacroRegimeFusion` 피드 | `MacroFeed` 예외/극단값 주입 | 그레이스풀 폴백(평온) 또는 위기 수축, 무크래시 | `ChaosMacroFeed` |

---

## 2. 결정론적 카오스 (Seeded — 재현 가능)

- 모든 장애 주입은 **FNV-1a 의사난수(시드)** 기반 — `CartridgeGenome`·`MutationBoundsMatrix` 와 동일 결정론 패턴 재사용.
- 카오스 시나리오 = `(seed, faultRate, faultKinds[])`. 동일 입력 → 동일 장애 시퀀스 → **재현 가능한 실패**.
- `Math.Random`/`DateTime.Now`/시계 의존 **금지** (플래키 카오스는 카오스가 아니라 노이즈).
- 시간 기반 장애(타임아웃)는 `ChaosClock`(주입식 가상 시계 또는 결정론적 틱 카운터)으로 모사.

---

## 3. 검증 불변식 (Chaos Invariants — 단언 대상)

| 코드 | 불변식 | 위반 = 결함 |
|------|--------|-------------|
| **INV-1 Fail-Closed** | 어떤 주입 장애도 시스템을 *열지* 않는다 — 상태 미상이면 항상 동결 | 장애 후 거래 허용 = 치명 |
| **INV-2 Zero-Leak** | 카오스 종료 후 `MemoryLeakGuard.LiveCount` == 베이스라인 (고아 핸들러 0) | 누수 잔존 |
| **INV-3 Recovery** | 장애 해소 + `ManualRelease`/`Rearm` 후 정상 거동 복귀 | 영구 동결(복구 불능) |
| **INV-4 No-Torn / No-Propagation** | 동시 카오스 중 torn read 0, 오염이 정상 슬롯·원장에 전파 0 | 데이터 파손 |
| **INV-5 Host-Survival** | 백그라운드 카오스가 메인 GatingMode·호스트 프로세스를 크래시시키지 않음 | 격리 방화벽 붕괴 |
| **INV-6 Determinism** | 동일 시드 → 동일 결과(장애열·최종 상태) | 재현 불가 |

---

## 4. 카오스 도구 (테스트 프로젝트 — 장애 주입 더블)

| 도구 (Tests) | 책임 | 형태 |
|--------------|------|------|
| `ChaosBroker : IBroker` | `Place()`에 seeded 예외/타임아웃/부분체결 주입 | 데코레이터 (실 브로커 래핑 또는 단독) |
| `ChaosMacroFeed` | `MacroSnapshot` 에 예외·NaN·극단값 주입 | `Func<MacroSnapshot>` |
| `MmfCorruptor` | 스냅샷 파일 바이트를 seeded 위치/패턴으로 오염 | 정적 헬퍼 (File I/O) |
| `ChaosDropper` | 링버퍼에 생산자 폭주로 강제 추월·드롭 유발 | 고속 Publish 루프 |
| `ChaosClock` | 결정론적 가상 시계/틱 카운터 (타임아웃 모사) | 주입식 시간원 |
| `ChaosScenario` | `(seed, rate, kinds)` → 장애 시퀀스 오케스트레이션 | 순수 결정론 드라이버 |

> 프로덕션 변경은 **0 또는 최소 seam**(기존 `CycleFaultHook` 류)뿐. 신규 프로덕션 클래스 없음(J1).

---

## 5. +20 카오스 테스트 분류 (제안)

| 영역 | 케이스 (예시) | 수 |
|------|---------------|-----|
| §A MMF 오염 | 헤더/슬롯/CRC 오염 위치별 → fail-closed 동결·폐기, 부팅 무사, 재부팅 멱등 | 5 |
| §B 브로커 카오스 | Place 예외·타임아웃·부분체결 → 라이브 동결 / 드라이런 무영향 / 라우터 분배 거부 | 5 |
| §C 동시 핫스왑+카오스 | 스왑 폭주 중 디싱크·예외 동시 주입 → torn 0·동결·복구 | 4 |
| §D 누수·격리 | 카오스 폭격 후 LiveCount 0 수렴 / 온실 사이클 예외 흡수 / 호스트 생존 | 3 |
| §E 복구·멱등 | 장애 해소 후 ManualRelease/Rearm → 정상 복귀, 동일 시드 재현 | 3 |
| **합계** | | **20** |

---

## 6. 정직 경계 (Honest Boundaries)

1. **고의 파괴 코드는 프로덕션 밖.** 카오스 하니스는 테스트 전용. 프로덕션 신규 "인젝터 클래스" 없음(J1). 이는 안전 크리티컬 시스템의 마땅한 방어다.
2. **"틱 분배 누락 during 핫스왑" end-to-end 는 미존재 배선을 전제한다.** `RingBufferCore` 는 아직 라이브 디스패치(`KiwoomBridgeClient.Channel`)에 결속되지 않은 독립 프리미티브다(S20 #1 경계). 따라서 §C 는 컴포넌트 단위(슬롯 스왑 + 링버퍼 드롭)를 **각각** 카오스 테스트하며, 라이브 틱 경로 end-to-end 카오스는 점화(라이브 결속) 이후 영역이다.
3. **이 마일스톤은 라이브 스위치를 켜지 않는다.** 드라이런 조립체의 *복원력 검증*일 뿐, 실거래 결속(S21 #2 ShadowMetricsBridge / v5.0.0)이 아니다.
4. **카오스 통과 ≠ 라이브 안전 증명.** 카오스는 *알려진 장애 모드*의 fail-closed 를 입증한다. 미지의 시장 장애·브로커 특이 거동은 별개이며, 라이브 점화는 여전히 단계적·가역적이어야 한다.

---

## 7. 핵심 판단 요약 & 권고

- 카오스 검증은 v5.0.0 점화 전 **가장 가치 있는 무위험 방어선**이다 — 우리 적대적·fail-closed 철학의 정점.
- 단, **테스트 사이드 하니스 + 불변식 단언**으로 구현하며, 프로덕션엔 고의 파괴 코드를 넣지 않는다(J1).
- 결과물: `StockManager.Tests`의 카오스 도구 6종 + 카오스 스위트 ~20개. 프로덕션 변경 0~최소.
- 통과 시: 1004 → ~1024 테스트, 0오류·0경고 하드 게이트 유지, **알려진 장애 모드 전반의 fail-closed·무누수·복구가 결정론적으로 입증된 요새**.

---

## 8. 구현 마일스톤 분해 (제안)

| 마일스톤 | 범위 | 산출 |
|----------|------|------|
| **S21 #1** | 본 청사진 | 카오스 설계 명세 (이 문서) |
| S21 #1-b | 카오스 도구 6종 + 카오스 스위트 ~20 | 테스트 하니스 + 불변식 단언 |
| S21 #2 | ShadowMetricsBridge (실 틱 복사·발주부 물리 차단) | 그림자 관제 — 별도 안전 검토 |
| v5.0.0 | 라이브 파이프 단계적 개방 | 가역·단계적 점화 |

---

*외부 의존성 0 · 순수 결정론 카오스 · 고의 파괴 코드는 프로덕션 밖 · fail-closed 불변식 검증 — 점화 전 마지막 강도 시험.*
