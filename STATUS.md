# Current Project Status

- **Active Milestone**: v12.18.53-DEV — 미국 증시 달력(NYSE 휴장 · 조기 폐장 · 서머타임) · 세션 상태 머신 · $0.01 호가 정렬 · KIS 해외 모의(VTTS3012R) 통신 점검
- **Build Status**: 오중 빌드 Release -warnaserror 0/0 · 전체 스위트 4,744 통과 4 건너뜀(옵트인) · 실패 0 · infra-scan 0 · 래칫 ≤40
- **Updated At**: 2026-10-21

## Completed Deliverables
- UsTradingCalendar: NYSE 평일 휴장 2024~2027 41일 · 조기 폐장 9일 · DST 규칙 산정(OS 시간대 DB 70,128점 전수 일치) · 범위 밖 fail-closed
- UsSessionStateMachine: 프리/정규/애프터/장외 · 매매일 = 미 동부 날짜 · KST EDT 22:30 / EST 23:30 정규장 개장
- UsTickAligner: raw 10^8 · $1 이상 $0.01 · 미만 $0.0001 · 보호 방향 정렬 · 10만 회 판정+정렬 0B
- KisOverseasPaperProbe(테스트 전용): PaperOverseas DPAPI 판독 · 허용 목록(토큰 POST · VTTS3012R GET만) · 마스킹
- 실측: 토큰 HTTP 200 · 257.4ms · 해외 잔고 rt_cd 0 · 보유 0행 · 30.0ms · 원문 노출 0 · 주문 0 · HANDOVER §152

## Next Milestone
- 미국 세션 머신 소비자 결속(해외 시세 피드) · NYSE/AMEX 잔고 조회 · 장중 VTS 고의 거부 캡처 · 달력 2028+ 연장
