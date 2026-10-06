# Current Project Status

- **Active Milestone**: [KIS · 추천 A] KIS OpenAPI 프로토콜 무할당 판독 · OAuth 토큰 수명주기 Disarm · 다중 브로커 라우팅 실배선 · KIS 세션 단절 인터락(방향 B) (기준 태그 v12.14.1-GM · 미커밋 — 직전 사전 검증/포트폴리오 작업 포함)
- **Build Status**: 오중 빌드 Release -warnaserror 0/0, 3,395/3,395 Pass(2회 연속), infra-scan coupling 0, RatchetCeiling 통과
- **Updated At**: 2026-10-06 15:10

## Completed Deliverables
- [실결함] 다중 브로커 미배선 — 라우터가 브로커를 골라도 OrderDrainWorker 는 단일 IBroker 고정 → OrderAdmission.Broker · brokersByIndex(직접·봉인 사출 양 경로) · 미매핑 = 거부
- [실결함] KisLiveBroker: 정적 단일 토큰 캐시(도메인/키 공유) · 23시간 추정 만료 · Contains 성공 판정 · msg=한글(주문번호 아님 → 슬롯 TTL Disarm) · 소수 절단 · 취소 미구현 → 전부 교정
- [실결함] KIS AppSecret 로그 평문 통과(마스커 패턴 미해당) → 등록 비밀 정확 일치 마스킹 · [실결함] PINGPONG 판별이 잘린 JSON 도 true(반송) → 완결 문서만
- KisProtocol(δ=0): 실시간 헤더 · H0STCNT0 다건 · H0STCNI0 통보 · 주문 응답 · 토큰 응답 · KisNoticeCipher(BCL 원샷 AES 184B/건 → ECB 블록 + 수동 CBC/PKCS7 0B)
- KisTokenLifecycle: pinned 바이트 보관 · 서버 만료(KST 만료가 이르면 우선) · 5분 선행 구간 갱신 실패/만료 = KisAuth(18) · 검증 후 교체 · Bearer δ=0
- 방향 B KisSessionInterlock: 미연결/침묵/끊김 → KisSession(19) + 기존 센티널 ReportFault(라우터 Disarm · 블랙리스트 · 페일오버 선지정) · 슬롯 회수·잔량 재송출 없음
- KisOrderIdSpace(비트 48): 키움 클라이언트 ID 와 KIS ODNO 충돌 0 · KisMultiBrokerRoutingTests.cs 21건(Red 6건 + PINGPONG 수정 전 Red)

## 정직 스코핑
- ★지시서 '잔량 원자 재라우팅'·'KIS 슬롯 회수'는 미구현(의도) — 장애 브로커 주문이 살아 있을 수 있어 이중 체결/노출 상실(CLAUDE.md §2 위반) · 기존 설계도 장애 = 전면 Disarm
- 분할 송출(Split Dispatch) 미구현 — 자식 주문별 추적 슬롯·브로커 지정 라우팅 설계 결정 필요 · KIS 필드 순서/TR ID 는 공개 문서 기준(VTS 실측 전) · 호스트 KIS 결선 0(자격증명·전송 계층 없음)
- Next: 커밋 대기(사전 검증/포트폴리오 + KIS) · 분할 송출 설계 방향 수령
