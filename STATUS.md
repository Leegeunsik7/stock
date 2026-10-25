# STATUS

- **Active Milestone**: v12.18.83-DEV — 플루토 공식 거시 피드(FRED 공식 REST API · 무료 키) 결선 + 키 미설정 정직 대기 페일세이프
- **Build Status**: 오중 Release -warnaserror 0W/0E(WebOnly · Default · PrimesBench · KiwoomProxy x86 · KiwoomComShim) · 5,581 통과 / 9 건너뜀 / 0 실패 · infra-scan 0 · 래칫 ≤40
- **Updated At**: 2026-10-25
- **Completed Deliverables**:
  - PlutoPublicMacroSource: api.stlouisfed.org observations × 5종(VIXCLS · DGS10 · DTWEXBGS · DEXKOUS · SP500) · limit=30 내림차순 → 1개월 변화 bp
  - 키 = Pluto.FredApiKey → MS2E_FRED_API_KEY · 미설정/형식 위반 = HTTP 0건 · UI '거시 피드: FRED API 키 미설정 · 관측 대기'
  - 60초 주기 · 3초 제한 · 무재시도 · 429 = 주기 즉시 중단 · 실패/이상치 = 직전 안정값(30분 후 지연 표기)
  - 테스트 31건(FRED JSON 스텁 · fail-closed · 429 취소 · 정직 대기 · δ=0 10만 회 0B) · HANDOVER §199
  - liveArmed:false · 인터락 30(0x800000001FFFFFFF) 무변경 · QuantumVault 미접근 · 실 키 실측 미수행(옵트인)
- **Next Milestone**: v12.18.83-GM — 이중 출하(-NoPeer) · 키 발급 후 옵트인 실측(MS2E_PUBLIC_MACRO_LIVE=1)
