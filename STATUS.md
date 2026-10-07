# Current Project Status

- **Active Milestone**: [KIS 실배선 · 추천 A] REST 주문가능/잔고 → KIS 한도 뷰(비트 24) · WebSocket 체결통보 세션(구독 키 자동 장착 · 심박 · 장중 3초 침묵 차단) · 매트릭스 가중평균가 (v12.15.x-DEV · 미커밋 — 대사·봉인 감사·트리아지·분할·결선 포함)
- **Build Status**: 오중 빌드 Release -warnaserror 0/0, 3,474/3,474 Pass(3회 중 1회 기존 IPC 타이밍 테스트 간헐 · 단독 5/5), infra-scan coupling 0, Phase114 통과
- **Updated At**: 2026-10-07 15:00

## Completed Deliverables
- [실결함] 어댑터가 구독 응답 key/iv 를 버림(운영 SetNoticeKeys 호출부 없음) → 실연결 시 암호 체결통보 전량 폐기 → 자동 장착
- KisProtocol 판독기 3종(TTTC8908R · TTTC8434R · 구독 응답 · δ=0) · KisLiveBroker 조회/접속키
- KisBalanceQueryAdapter: 실패 즉시 한도 0 · 연속 3회 Disarm(비트 24 sticky) · 30초 만료 · 미설정 비차단
- KisRealtimeSession: 체결통보(HTS ID) + 심박 체결가 구독 · 조각 조립 · PINGPONG 복사 반송 · 단절 = 비트 19 · 재접속
- 방향 B: 장중(KST 09:00~15:20) 3초 침묵 차단 · 매트릭스 가중평균 체결가 · KisLivePipelineWiringTests.cs 9건 · HANDOVER.md §28

## 정직 스코핑
- 실시간 접속은 Kis.RealtimeEnabled(기본 false) ∧ vault KIS.HtsId 필요 · TR/필드/WS URL 공개 문서 기준 실서버 미검증
- KIS 잔고 1페이지만 · 키움 브리지 ConnectAsync 미호출 유지 · 방향 A(비관리 링버퍼) 미채택(경합 원천 없음)
- Next: 커밋(선별 스테이징) · 모의(VTS) 도메인 실프레임 대조 · 설정 UI 에 HTS ID 입력 · 키움 브리지 실연결
