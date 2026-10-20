# Current Project Status

- **Active Milestone**: v12.18.32-DEV — KIS 접속키 비표준 본문 거부(HTTP 200/500 + error_code/msg_cd) 1회 즉시 sticky 래치
- **Build Status**: 오중 빌드(Default · WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0/0 · 전체 스위트 4,162 통과 + 1 건너뜀(리얼 와이어 옵트인) · Offline_1M 5회 연속 통과 · infra-scan 0 · 래칫 ≤40
- **Updated At**: 2026-10-20

## Completed Deliverables
- KisApprovalRejectedException.MatchFatalBodyCode — Utf8JsonReader 최상위 error_code/msg_cd 판독 δ=0(EGW00103/105/121/123/133 · OPS00001)
- KisLiveBroker.ParseApprovalResponse — 본문 치명 코드 = 상태 무관 거부 · 성공 판독 Utf8JsonReader 전환 · 단위 테스트 가능
- LiveWireKisLongSoakTests §4 +15 — 200/500 본문 거부 1회 래치 · 일시 실패 7종 비래치 · 정상 키 접속 · 판독 0B · HANDOVER §109

## Next Milestone
- 실 VTS 거부 응답 관측 → 치명 코드 표 재검토(EGW00121/123/133 과잉 래치 여부)
- v12.18.32-GM 이중 출하(-NoPeer)
