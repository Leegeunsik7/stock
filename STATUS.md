# Current Project Status

- **Active Milestone**: v12.18.56-DEV — 업비트 공개 WebSocket 실시간 호가/체결 피드 어댑터
- **Build Status**: 오중 빌드 Release 0W/0E · 전체 스위트 4,920 통과 6 건너뜀(옵트인) · 실패 0 · infra 0 · 래칫 ≤40
- **Updated At**: 2026-10-22

## Completed Deliverables
- UpbitWebSocketFeedAdapter: 무할당 스트림 파서 · seqlock 시장 상태 · CryptoPaperExchange 직결 · 지수 백오프 · 침묵 단절 · PING
- 오프라인 테스트 84건: 손상 프레임 fail-closed · 변조 10만 예외 0 · 파서/투영/수신 루프 10만 프레임 δ=0
- 옵트인 실측: 접속 143.8ms · 첫 프레임 173.1ms · 10 프레임 767ms · 가상 왕복 체결 · HANDOVER §159

## Next Milestone
- 피드 어댑터 장시간 소크 · 제품 경로 배선 검토(UI 표시) · 비트 23 실 래치 경로 검토 · KRX 달력 2028(공지 후)
