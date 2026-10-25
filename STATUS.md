# STATUS

- **Active Milestone**: v12.18.76-DEV — 업비트 15호가(orderbook_units) 무할당 파이프라인 + 매수벽/매도벽/허매수 아레나 실가동
- **Build Status**: 오중 Release -warnaserror 0W/0E · 5,392 통과 / 7 건너뜀 / 0 실패 · infra-scan 0 · 래칫 ≤40
- **Updated At**: 2026-10-25
- **Completed Deliverables**:
  - UpbitStreamProtocol 15호가 판독(InlineArray 고정 버퍼 · 연속 유효 접두 · fail-closed) · 디코딩 10만 회 0B
  - depthTap → 피더 슬롯 → Pump → OnDepthQuote → 15호가 뎁스 판독 · 파이프라인 10만 회 0B
  - 벽 위치 뱃지(n호가 매수벽 / 매도벽 -15 / 허매수 감지) · 악마의 변호인 ⑥ DepthTrap(−20) 실연동
  - 신규 테스트 19건 · HANDOVER §188
- **Next Milestone**: v12.18.76-GM 이중 출하(-NoPeer) 또는 실 업비트 15호가 라이브 소크
