# STATUS

- **Active Milestone**: v12.18.81-DEV — 플루토 스웜 US 레인 실피드(KIS 해외 일봉 HHDFS76240000) + 송신 간격 1,150ms 보정 + 옵트인 VTS 실측
- **Build Status**: 오중 Release -warnaserror 0W/0E · 5,537 통과 / 9 건너뜀 / 0 실패 · infra-scan 0 · 래칫 ≤40
- **Updated At**: 2026-10-25
- **Completed Deliverables**:
  - KisProvider 해외 일봉(EXCD NAS/NYS/AMS · 수정주가 · 레이트 게이트 경유 · fail-closed 빈 목록)
  - UsListingExchange(나스닥 51 · NYSE 49 · 미등재 = 조회 0) · KisDailyBarSource KRX/US 겸용(센트)
  - 송신 간격 1,150ms(PlutoRequestPacer ms 생성자 · KRX/US 공유 페이서)
  - KisOverseasDailyBarLiveTests(MS2E_KIS_OVERSEAS_LIVE=1 옵트인 · 송출 허용 목록 · 실측 미실시)
  - HANDOVER §195(보완 포함)
- **Next Milestone**: 사용자 환경 VTS 실측(MS2E_KIS_OVERSEAS_LIVE=1) 결과 확인 후 v12.18.81-GM 출하
