# Current Project Status

- **Active Milestone**: [정정/취소·계좌 · 추천 A] 키움 정정/취소 파이프라인(원주문번호 사상) · 계좌 상태 동기화(OPW00018/OPW00001 · δ=0) · Cancel-Fill Race 배리어(방향 A) · 계좌 잔고 괴리 Disarm(방향 B) (v12.14.1-GM)
- **Build Status**: 사중 빌드 Release 0/0(WebOnly → sln Default+PrimesBench+x86 KiwoomProxy), 3,307/3,307 Pass(2회 연속), infra-scan coupling 0, KIWOOM_COM 인터롭 쉼 컴파일 0/0
- **Updated At**: 2026-10-06 02:10

## Completed Deliverables
- [실결함] ProductionKiwoomApi.CancelOrder/QueryAccount 빈 스텁(호스트 취소 소실) → 코어 위임: 취소 3/4 · 정정 5/6 · 원주문번호 D7 · 최신 미체결 기준 잔량 초과/중복/미접수 차단 · 사상 전 취소 보류 후 집행
- [실결함] 취소/정정은 새 주문번호 — 취소확인 통보가 미사상 번호로 보류→만료되어 호스트 슬롯이 TTL 까지 잔존(sticky Disarm) → RC 응답 사상 + FID 904 원주문번호 폴백 + 대체된 구 번호 비체결 통보 무시
- 호스트 IOrderAmendGate(OrderExecutionFeedAdapter): 종결/중복/잔량 초과 취소·정정 송신 0 · ExecutionBridgeBroker 전방 차단 · Modify/QueryAccount 경로 신설
- 방향 A: 취소 후 체결 반영·경합 완전 체결 = CancelRaceFilled 종결 · 정정 진행 창(5초) 내 잔량 불일치 유예 · 정정 접수 잔량으로 추적 수량 재기준(오격발 방지)
- AccountStateSyncAdapter: 브리지 고정 프레임(Cash 32B · Position 48B) → 고정 배열 δ=0 · 주문가능금액 예약 게이트 · 추정 보유 매도 게이트 · 만료 = fail-closed(RouterFeedGate 판정 15/16/17)
- 방향 B: 정적 구간(조회 전후 체결 0) 1주 단위 양방향 대조 → AccountDivergenceInterlock(필수 비트 14 · 이중키 재무장) · 10초 주기 조회(분당 TR 12건)
- KiwoomCancelModifyAccountTests.cs 25건(교정 끔 Red 15건)

## 정직 스코핑
- ★키움 SDK 미설치 — 실 AxKHOpenAPI 미검증 · 정정/취소 통보의 9203/904 실제 배치는 공개 문서 기반 가정(모의투자 1회 실측 필요)
- OPW00004 에 주문가능금액 부재 → OPW00001 사용 · 예수금은 수수료/세금/D+2 미모델로 게이트만(1원 대조 아님) · 매도 미체결 예약 미반영(브로커 거부로 종결)
- Next: 커밋 대기(MMF·키움 프로토콜·L2·저지연·키움 COM·정정/취소 미커밋) · SDK 설치 환경 모의투자 검증
