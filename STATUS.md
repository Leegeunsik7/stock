# Current Project Status

- **Active Milestone**: v12.18.7-DEV — /aidash 브로커 세션 복구 배지(키움 · KIS)
- **Build Status**: 오중 빌드(Default · WebOnly · PrimesBench · KiwoomProxy x86 · KiwoomComShim) Release -warnaserror 0/0 · 전체 스위트 3,805/3,805 · 래칫 ≤40 · 출하 v12.18.6-GM(741fe17) 불변
- **Updated At**: 2026-10-14

## Completed Deliverables
- SessionRecoveryManager.Snapshot(): 상태 · 래치 · 연속 실패/상한 · 누적 계수 락 1회 일관 판독(0B)
- DashSession 8상태(연결 · 대기 · 백오프 · 재접속 · 대사 · 소진 · 불일치 · 미결선) + DashboardSnapshotHub.AttachSessionRecovery
- BlazorHomeForm 키움/KIS 복구 관리자 결선 · /aidash '브로커 세션' 표(녹/황/적)
- 교정: 초기 상태 결선 시 서명 기여 0 → 재게시 누락 — 결선 표지로 해소
- SessionRecoveryManagerTests 5(분류 생애주기 · 즉시 재게시 · δ=0 · 래칫) · HANDOVER §64
- 전제 검증: 세션 복구 · 백오프 · 토큰 갱신 · TTTC8001R 대사 · TTTC0803U 유령 취소는 v12.17.x 기출하

## Next Milestone
- 운영자 결정: 유령 비상 취소가 liveArmed 와 무관하게 실 송신(HTS 수동 주문도 대상) — 게이트/옵트인 여부
- 오더 수치 차이(백오프 상한 16s vs 30s · 토큰 선행 5분 vs 30분 · 비트 14 의미) 조정 여부
