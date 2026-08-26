# 03_UI.Portable — 순수 Blazor UI (RCL)

엔진 무참조 순수 UI 컴포넌트 라이브러리. **Infrastructure 직접 참조 금지** — Core 추상/Port 와 뷰모델만 참조.
이미 `MS2E.UI.Portable` RCL 프로젝트가 존재(Phase 93~94 UI 해방 성과).

- `Components/` — 공용 컨트롤·차트·테이블(`HubTabs`, `IndexTickerBar`, `PostcodeSearchModal` …)
- `Pages/` — Dashboard, Portfolio, Admin, Credentials, **CodeViewer**(`/tools/codemap`) …
- `ViewModels/` — 낙관적 UI 업데이트용 가변 뷰모델 + 표시 DTO

**현재 물리 위치**: `MS2E.UI.Portable/`(이주 완료분) + `StockManager/Blazor/`(Host 물리 잔존분 — 뷰어가 오배치로 탐지·`엔진 무참조`가 이주 게이트).
