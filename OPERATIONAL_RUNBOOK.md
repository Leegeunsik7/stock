# MS2E StockSuite — 실전 운용 매뉴얼 (Operational Runbook) v10.1.0

> 실자본 집행 운용자(지휘관)를 위한 3단계 점화 절차 및 비상 탈출 프로토콜.
> **절대 원칙**: 물리 이중 키(`Dispatch.ValveWired ∧ Dispatch.LiveArmingWired`) 완전 결속 전에는
> 소프트웨어의 어떤 상태(SLA 100점·봉인 유효·서명 PASS)로도 실 브로커 소켓에 **단 1비트도 실물 발주가 나가지 않는다**(liveArmed:false).
> 아키텍처/서브시스템은 [ARCHITECTURE.md](ARCHITECTURE.md), 전체 이력은 [HANDOVER.md](HANDOVER.md) 참조.

---

## 0. 사전 조건 — 호스트 튜닝 (집행 세션 시작 전, 1회)

관리자 PowerShell에서 튜닝 감사를 실행하고 **모든 항목이 `[OK]`** 인지 확인한다:

```powershell
pwsh ./HostTuningAudit.ps1
```

- OS 고해상도 타이머 ≤ 0.5ms (`timeBeginPeriod`), CPU C-States/Power Throttling 비활성, NIC Interrupt Moderation, P-Core Affinity 정합.
- 하나라도 `[WARN]`/`[FAIL]`이면 스크립트의 권고 조치를 적용한 뒤 재실행. 튜닝 미완이면 **1단계 서명이 자동으로 거부된다**(타이머 해상도 항목 미달).

> 관제 UI 📜 패널의 **타이머 해상도(µs)**와 **[HOST TUNED - CANARY STAGING READY]** 배지로 실시간 확인.

---

## 1단계 — Pre-Flight 점검 & 사전 점화 서명

1. 앱 기동 후, 관제 UI(TelemetryMonitor)에서 다음을 **육안 + 서명 자동 채점**으로 확인한다:
   - Pre-Flight 총점 **100/100** (🚀 패널), 워치독 생존, 퀀텀 금고 무침해, MMF 감사 링 무결, P-Core 핀 활성, 인프라 결합도 ≤ 120.
2. `IgnitionSignoffValidator`가 위 8항목을 매 프레임 δ=0 채점한다. **전 항목 PASS일 때에만** `SignoffPassed`가 켜지고, 그 순간 `HostEnvironmentDriftSentinel`이 **현재 호스트 타이밍을 베이스라인으로 봉인**한다.
3. 📜 패널 배지가 **[HOST TUNED - CANARY STAGING READY]**(청/녹)가 되면 다음 단계로.
   - ❌ **하나라도 미달**이면 `SignoffBlocking`이 무장을 하드 차단한다 — 물리 키를 돌려도 실전 무장 부여 불가. 서명 실패 마스크(포렌식 헥스)로 미달 항목을 특정해 교정.

## 2단계 — 드라이런 모의 체결 확인 ($0원 가상 검증)

1. `Dispatch.DryRunActive=true`(config)로 기동된 상태에서 전략을 가동한다. 모든 주문은 `MockExecutionOrderBookMirror`로 봉인 루프백된다 — **실 브로커 소켓 0비트**.
2. 🛩️ 패널에서 가상 체결 수·슬리피지(bps)·RTT(µs)를 확인하고, **[DRY-RUN CERTIFIED - MOCK MIRROR ACTIVE]** 및 인증 100/100을 확인한다.
3. 📦 패널 나노 SLA(P99.99)가 예산 이내(SLA PASS)이고 지터 회귀가 없는지 확인. 만족 시 실전 준비 완료.

## 3단계 — 물리 이중 키 결속 (실자본 무장)

> ⚠️ 이 단계부터 실물 발주가 가능해진다. **되돌릴 수 없는 자본 노출**이다.

1. 카나리 캡을 확인·설정: `MicroCapitalCanaryGovernor`가 **전체 자본의 ≤ 0.1%(기본 10bps)** 내에서만 최초 주문을 허용한다. `Sla.FuseArmed`·`Release.ExpectedMasterSeal` 등 운용 config를 검토.
2. **물리 이중 키를 순서대로 결속**한다(둘 다 하드웨어/운용자 조작):
   - `Dispatch.ValveWired = true` (밸브 개통 — 드레인 타이머 가동).
   - `Dispatch.LiveArmingWired = true` (무장 키 — armedPredicate 평가 개시).
3. 두 키가 모두 true이고 **모든 blockWhile(서명·SLA·지터·드리프트·카나리·패닉…)이 clean**일 때에만 `liveArmed()`가 true가 되어 게이트웨이가 실물 사출을 개통한다.
4. 최초 사출은 **카나리 캡 내**로만 나간다(📜 패널 카나리 집행율 % 확인). 실측 슬리피지가 임계를 넘으면 거버너가 **O(1)로 무장을 해제하고 드라이런으로 자동 강등**한다.

---

## 🟣 비상 탈출 프로토콜 (Emergency Abort)

실시간 운용 중 이상 징후 발생 시, **판단이 늦어도 '한 동작'으로 전량 정지에 도달**한다.

### 자동 안전 강등 (무개입)
- **호스트 환경 드리프트**: 서명한 호스트 상태(타이머 해상도·지터)로부터 지속(연속 확인) 이탈 → `HostEnvironmentDriftSentinel`이 `liveArmed:false`로 원자 강등. 📜 패널 암흑자색 오버레이 + 포렌식 헥스.
- **카나리 슬리피지 초과** → 즉시 드라이런 강등.
- **SLA P99.99 이탈·측정 지터 회귀·인증 리스 만료·키 펄스 단선·데드맨 트립** 등 → 각 blockWhile이 무장을 tighten-only 차단.

### 수동 비상 탈출 (운용자 개입)
1. **패닉 스위치 격발**: 지정 핫키/시퀀스로 `OperatorDuressPanicSwitch.Trip()` 호출 → O(1)로:
   - `attestation.ForceFreeze` (전 발주 비가역 차단·Mission-A 벽),
   - `onPanic` 훅 (Cancel-All + 시크릿 0x00 Zeroization — 운용 배선),
   - 감사 각인 + 비가역 래치(재무장 불가).
   > 핫키 바인딩은 운용자/UI가 배선한다(예: 전역 `Ctrl+Alt+Shift+Esc`). 모듈은 O(1) 트립 기구를 제공한다.
2. **물리 키 해제**: `Dispatch.ValveWired=false`로 되돌리면 드레인 타이머 정지 + 이중키 무결성 tripwire(`dualKeyIntact`)가 `ForceFreeze`로 파이프라인을 영구 락업한다.
3. **프로세스 종료**: 위 조치로 신규 발주가 물리적으로 0이 된 것을 📜/🚀 패널에서 확인한 뒤 정상 종료.

### 복구
- 자동 강등/패닉 래치는 **비가역**(fail-closed) — 재시작 후 1~3단계를 처음부터 다시 밟는다. 원인(호스트 튜닝 재적용·슬리피지 원인 제거)을 교정하지 않으면 서명이 다시 거부된다.

---

*이 매뉴얼의 모든 게이트는 tighten-only다 — 무장을 '거둘' 수는 있어도 '부여'할 수는 없다. 실전 무장은 오직 운용자의 물리 이중 키가 지배한다.*
