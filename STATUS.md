# Current Project Status

- **Active Milestone**: [보호 지정가 · 추천 A] 시장가 → 참조가 기반 보호 지정가 변환 · 1~3호가 깊이 가드 · 호가 단위 정렬(방향 A) · 극한 슬리피지 Disarm(방향 B) (v12.14.1-GM)
- **Build Status**: 사중 빌드 Release 0/0(WebOnly → sln Default+PrimesBench+x86 KiwoomProxy), 전체 3,201 — 3회 중 2회 100% · 1회 무관 플레이키(MmfIpcFabricTests.Spsc · 미수정 파일 · 단독 30/30), infra-scan coupling 0
- **Updated At**: 2026-10-05 18:20

## Completed Deliverables
- [실결함] 피드 연동 후에도 시장가는 PaperKind.Market 그대로 브로커 송출(라우터는 참조가 존재만 확인) → IOrderRouteGate 가 송출 형태(OrderAdmission) 반환 · OrderDrainWorker 는 승인값으로 송출 — 송출 Kind 항상 지정가
- ProtectiveLimitOrderConverter: 매수 상한 = 참조가×(1+offset) 내림 정렬 · 매도 하한 ×(1−offset) 올림 정렬 · 가격제한폭(기준가 ±30%) 보호 방향 클램프/이상치 거부 · Int128 포화 · offset ≤ 라우터 한도 강제
- 방향 A KrxTickAligner: QPrice raw 7구간 테이블(기존 KrxTickLadder 전 구간 동치 · 격자 유효성 · δ=0 — ReadOnlySpan 프로퍼티 배열 패턴의 호출당 144B 할당 실측 후 정적 배열로)
- DepthBook(IDepthSink): 1~3호가·보호선 이내 잔량 — 시장가 Ok/축소/거부 · 깊이 미상·만료 시장가 거부(Router.MarketRequiresDepth 기본 true) · 대사 DepthRejected/DepthReduced/SlippageRejected
- 방향 B ExtremeSlippageDisarmInterlock: 체결가 ↔ 판정 참조가 불리 괴리 200bps 초과 → sticky Disarm(ExtremeSlippage 비트 · 이중키 재무장) · BlazorHomeForm 전량 결선
- ProtectiveLimitConverterTests.cs 20건(교정 끔 Red 12건) · RouterFeedAdapterTests 2건 의미 갱신

## 정직 스코핑
- 지시서의 StockManager/Engines 부재(신설) · ★Toss/키움 어느 쪽도 L2 호가를 파싱하지 않아 DepthBook 생산자 0 → 현재 시장가는 실전 경로에서 전량 거부(옵트아웃 키 존재)
- 가격제한폭 클램프는 기준가 주입(MarketDataSpikeInterlock.SetReference) 시에만 — 현 호출자 0 · 지정가 주문은 깊이 축소 미적용(대기 허용)
- Next: MmfIpcFabric 플레이키 Triage 칩 생성 · 신규 지시 대기(누적 미커밋)
