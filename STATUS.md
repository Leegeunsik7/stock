# STATUS

- **Active Milestone**: v12.18.86-DEV — 웜스타트 실효성(Candles 적재 결선) + DualArena 인지 브리핑 텐서 패널
- **Build Status**: 오중 Release -warnaserror 0W/0E (WebOnly + sln: Default/Portable/PrimesBench/x86 KiwoomProxy/KiwoomComShim) · 5,659 통과 / 9 건너뜀 / 0 실패 · infra-scan 0 · 래칫 ≤40
- **Updated At**: 2026-10-26
- **Completed Deliverables**:
  - 전제 검증: Candles 테이블 기록 경로 부재 확인(스키마·판독 어댑터만 존재 · 웜스타트 = 항상 NoData 였음)
  - PlutoCandleRecorder: 스웜 소스 데코레이터 → 수신 정상 일봉만 (Symbol, Date) Upsert(동일 값 기록 0) · 종목당 30분 1회 · 단위 환원 · 미수신 = 기록 0 · DB 장애 = 롤백(스웜 무영향)
  - 업비트 일봉 수확(/candles/days 120봉 · 마켓당 1시간 1회 · 같은 페이서 초당 8회 이하 · 429 = RateLimited) · KIS 국내/해외 = 일봉 표본 그대로 적재 · 분 봉 둔갑 0
  - 호스트 결선 `Pluto.CandleRecorderEnabled`(기본 켜짐) — 다음 기동부터 PlutoLocalCandleSource 가 실 적재분 판독
  - PlutoTensorPanel(RCL · 주입 0): 인지 브리핑 내 KRX/US/Upbit 활성 슬롯 출처·모멘텀 %·엄격도·웜스타트(WarmReady/NoData/대기/장애) · 미결선 정직 렌더
  - 신규 테스트 16건(적재 멱등·미수신 0·분봉 차단·장애 롤백·수확 창/429·종단 웜스타트·어댑터·bUnit·RCL 순수성)
- **Next Milestone**: v12.18.86-GM 이중 출하(버전 승격 · -NoPeer 11단계 패키징)
