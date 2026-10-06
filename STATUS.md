# Current Project Status

- **Active Milestone**: [고가용성 · 추천 A] 프로세스 롤오버 슬롯 승계 가드(MMF · 체크섬 · HandoffAck) · 무할당 헬스 에이전트 · 호스트 하트비트→프록시 송신 게이트(방향 A+B) (v12.15.0-DEV · 미커밋 — 사전 검증/포트폴리오 · KIS 작업 포함)
- **Build Status**: 오중 빌드 Release -warnaserror 0/0, 3,407/3,407 Pass(2회 연속), infra-scan coupling 0, RatchetCeiling 통과
- **Updated At**: 2026-10-06 16:00

## Completed Deliverables
- [발견] LiveSoakSentinel(v12.5.0)은 어디서도 생성되지 않는 사문 코드 — 호스트에 자원 폭주→무장 차단 경로 0 · [발견] 키움 프록시는 호스트 1연결 후 종료(주문번호 사상표 소멸)
- ProcessRolloverGuard: 발행(미결속 송신 존재 = 거부) → SHA-256 봉인 → 승계(검증 후 전량 · 실패 = sticky 불일치) → HandoffAck(epoch·체크섬) · 승계 후 브로커 대사 전까지 Rollover 비트(20) 차단 · 종료 시 Ack 미확인 = 운용자 확인 없이는 종료 취소
- OrderExecutionFeedAdapter Export/ImportSlot(승계 슬롯 라우터 호출 생략) · ExecutionBridgeBroker.SeedOrderIdFloor(ID 충돌 방지)
- HealthMonitoringAgent(전용 스레드 50ms): Environment.WorkingSet · 스레드풀 탐침(미실행 대기 포함) · GC 누적 정지 증가분 · CPU(GetProcessTimes) → HealthAgent 비트(21) · 기동 유예 30초 · δ=0
- 방향 A+B: HostHeartbeatWriter(64B 고정 · seqlock MMF) → 프록시 HostHeartbeatEgressGate — 박동 200ms 초과/트립/맵 없음/찢김 = 매수·매도·정정 키움 송신 0 + 거부 프레임(취소는 통과)
- ZeroDowntimeRolloverTests.cs 12건(방어 끔 Red 9건) · HANDOVER.md §22

## 정직 스코핑
- ★'무중단 발주'가 아니라 '무중단 가시성': 프록시가 호스트와 함께 종료돼 구 주문 체결 사상 불가 → 브로커 미체결 조회(OPT10075 · 미구현) 대사 전까지 신규 발주 차단
- 지시서 '0.1ns 사상'·'외부 프로세스의 소켓 물리 단절'은 비현실/권한 필요 → 송신 지점(프록시) 게이트로 대체 · Working Set 1GB·정체 100ms 는 설정값(실측 운용 기준 조정 필요)
- 프로덕션 호스트 브리지 미연결 · 프록시 진입점 NullKiwoomApi — 실 슬롯 승계 경로는 현재 비활성 · Next: 커밋 대기 · 브로커 미체결 대사기 설계
