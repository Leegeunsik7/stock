# Current Project Status

- **Active Milestone**: v12.18.42-DEV — KRX 호가단위 송출 전 정렬(지정가 보호 방향 스냅 · 분할 자식 재검 · 브로커 최종 빗장)
- **Build Status**: 오중 빌드 Release -warnaserror 0/0 · 전체 스위트 4,282 통과 + 2 건너뜀(옵트인) · infra-scan 0 · 래칫 ≤40
- **Updated At**: 2026-10-21

## Completed Deliverables
- RouterFeedGate: 비격자 지정가 = 매수 내림 · 매도 올림 스냅(KrxTickAligner δ=0) · 정렬 불가 = InvalidPrice 거부
- 분할 자식 지정가 라우팅 전 격자 재검 · KIS/키움 브로커 Place 최종 빗장(KrxTickLadder.IsOnTick · 송신 이전)
- KrxTickPreflightTests 27건(경계 12값 · 100만 0B) · 분할 드레인 픽스처 실 격자 교정 · HANDOVER §129

## Next Milestone
- v12.18.42-GM 출하 · 장중 VTS 프로브(§127) · ETF/ETN 호가 규칙 분리 검토
