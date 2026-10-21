# Current Project Status

- **Active Milestone**: v12.18.52-DEV — 피드 정지 워치독 · 세션 침묵 판정 KRX 달력 일원화(휴장 · 동시호가 · 시간외 감시 동결 · 수능 순연)
- **Build Status**: 오중 빌드 Release -warnaserror 0/0 · 전체 스위트 4,632 통과 2 건너뜀(옵트인) · 실패 0 · infra-scan 0 · 래칫 ≤40
- **Updated At**: 2026-10-21

## Completed Deliverables
- KrxSessionStateMachine 감시 창 술어(연속매매 ∨ 달력 범위 밖 = 감시 유지) · 발주 측(범위 밖 차단)과 방향 분리
- KrxContinuousSessionNow(워치독 · 호가 이상 인터락) · IsKrxRegularSession(KIS 침묵 임계) 구형 평일/시각 판정 제거 → 단일 진실 공급원 위임
- 휴장 70일 × 24h 무패킷 오탐 0 · 동시호가/시간외 동결 · 연속매매(수능 · 연초 포함) 3,000.001ms 격발 · 10만 폴링 0B · 사보타주 6건 실패 확인
- VTS 프로브 송출 판정 엄격화 · 기존 경계 테스트 기준일(개천절 대체공휴일) 교정 · HANDOVER §149

## Next Milestone
- v12.18.52-GM 출하 · 2027 KRX 공지/2028학년도 수능일 대조 · 달력 2028+ 연장 · H0STMKO0 VTS 캡처 · 미설치 PC 배포본 실기 확인
