# 04_Host — 합성 루트 (Composition Root)

WinForms + Blazor Hybrid 실행 호스트. 모든 레이어를 **DI 로 조립**한다 — 유일하게 전 레이어 참조가 허용되는 곳.

- `Program.cs` — 엔트리포인트(트레이·헤드리스·클래식 폴백)
- `BlazorHomeForm.cs` — WebView2 Blazor 호스트 + 전 서비스 DI 등록(합성 ~900줄)
- `NavMenu.razor` · `MainLayout.razor` — 네비게이션/레이아웃 셸

**현재 물리 위치**: `StockManager/`(`Program.cs`, `Blazor/BlazorHomeForm.cs`, `Blazor/NavMenu.razor` …). 재배치 후 `Common/` 의 도메인/인프라 파일이 01_Core·02_Infrastructure 로 빠지면 이 프로젝트는 순수 합성 루트로 축소된다.
