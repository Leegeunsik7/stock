# Current Project Status

- **Active Milestone**: v12.18.21-DEV — 락프리 브로커 레이트 게이트 · 적응형 지수 백오프
- **Build Status**: 오중 빌드(Default · WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0/0 · 전체 스위트 3,998/3,998 · infra-scan 0 · 래칫 ≤40
- **Updated At**: 2026-10-19

## Completed Deliverables
- LockFreeTokenBucket: 슬라이딩 창 허가 기록 링 · 단일 태그 CAS · 임의 W 창 허가 ≤ N 보증(최대 2창 · 키움 1초 5 + 60초 100) · δ=0
- BrokerBackoffController: 429/−308/−200 → 100ms × 2ⁿ(3s 상한) + 지터 · 연속 3회 sticky 래치(물리 이중키 해제) · 재시도/대기열 0
- 키움 코어 주문/정정/취소/조회 게이트(취소 = 래치 무시) · 프록시 결선 · KisProvider 조회 게이트 · 허브 레인별 잔여/거부/래치 투영(KIS 레인)
- BrokerRateLimiterTests 22건(16 스레드 경합 · 10만 회 0B · 백오프 단계 · IL 허용목록) · HANDOVER §87

## Next Milestone
- 키움 프록시 게이트 텔레메트리 IPC 투영 · KIS EGW00201 초과 분류
- 실 키움 + KIS 동시 피드로 괴리 상수 보정 · WebView2 육안 확인
