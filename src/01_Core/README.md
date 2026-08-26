# 01_Core — 순수 도메인

순수 도메인 로직. **바깥(Infrastructure/UI/Host) 의존 0.** 외부 라이브러리·파일 I/O·네트워크·시계·난수 금지(결정론 사수).

- `Entities/` — `Order`, `Account`, `Portfolio`, `Position` + 불변 스냅샷 DTO(`PortfolioSummary`, `TossAccountSnapshot`, `TossHolding` …)
- `Ports/` — `IBrokerPort`, `IPortfolioSyncPort`, `IBrokerAccountPort`, `IQuantumVaultPort`, `IMarketDataStreamPort`, `IOrderExecutionPort` …
- `ValueObjects/` — `QFixed`, `QMoney`, `QMath`(10^8 고정소수), `Price`, `Quantity`, `Symbol`, `Money`

**현재 물리 위치**: 대부분 `StockManager/Common/`(예: `QFixed.cs`, `UniversalBrokerTypes.cs`, `TossPortfolioTypes.cs`) — 뷰어가 오배치로 탐지.
