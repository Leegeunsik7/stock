# Current Project Status

- **Active Milestone**: v12.18.22-DEV — 키움 프록시 → 호스트 레이트 게이트 MMF 관제 · KIS EGW00201 분류
- **Build Status**: 오중 빌드(Default · WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0/0 · 전체 스위트 4,034/4,034 · infra-scan 0 · 래칫 ≤40
- **Updated At**: 2026-10-19

## Completed Deliverables
- KiwoomRateTelemetry: 64B MMF seqlock 채널(프록시 작성 · 호스트 판독 · δ=0 · 무블로킹 · 부재/찢김/노후 분류) · 프록시 결선 + 50ms 박동 게시
- 허브 키움 레인 실시간 쿼터(주문/조회 1초·60초 · 거부 누계 · 백오프 · 래치) · /aidash 키움 · KIS 쿼터 배지(DashRateGate)
- KisRateLimitClassifier: 500 + msg_cd EGW00201 = 한도 초과(Utf8JsonReader δ=0) → 429 와 동일 백오프 · 3회 래치
- 신규 테스트 36건(KiwoomIpcRateLimitTelemetryTests 14 · KisErrorClassificationTests 22) · HANDOVER §89

## Next Milestone
- v12.18.22 GM 단독 출하(-NoPeer) 판단
- 실 키움 + KIS 동시 피드로 괴리 상수 보정 · WebView2 육안 확인
