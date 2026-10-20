# Current Project Status

- **Active Milestone**: v12.18.31-DEV — KIS 접속키 발급 지수 백오프 · 자격증명 거부 sticky 래치(재시도 폭풍 차단) 완료
- **Build Status**: 오중 빌드(Default · WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0/0 · 전체 스위트 4,147 통과 + 1 건너뜀(리얼 와이어 옵트인) · infra-scan 0 · 래칫 ≤40
- **Updated At**: 2026-10-20

## Completed Deliverables
- KisApprovalBackoff: 5 · 10 · 20 · 40 · 60s 상한 + 지터(+12.5~25%) · 1시간 발급 ≤ 57회(실측 54 · 수정 전 682) · δ=0 상태 머신
- 401/403 = KisApprovalRejectedException → sticky 래치(발급 1회 후 중단) · 복구 관리자 경로 포함 두 모드 적용
- 해제 표지 오버플로 결함 개발 중 발견 · 수정 · Offline_1M 5회 연속 통과 · HANDOVER §107

## Next Milestone
- v12.18.31-GM 출하(-NoPeer) · KIS 모의투자 자격증명 재확인 후 리얼 와이어 소크(§99 · 실 거부 응답 형식 관측)
- WebView2 미설치 PC 배포본 실기 확인
