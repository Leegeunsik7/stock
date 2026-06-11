# MS2-E Stock Suite — Blazor Hybrid 대시보드 통합 가이드

기존 WinForms 프로그램을 **그대로 유지하면서**, 홈/대시보드 화면만 Blazor(웹 UI)로 바꾸고
사이드바·드래그 위젯을 쓰는 방법입니다. 무거운 기존 모듈(실시간/포트폴리오/차트 등)은
WinForms 창으로 계속 열리고, Blazor 셸이 그 창들을 띄우는 **공존(Hybrid)** 구조입니다.

> 동봉한 디자인 프로토타입(`_design_prototype/StockSuite_Dashboard_Prototype.html`)을
> 브라우저로 먼저 열어 디자인/드래그 동작을 확인하세요. 아래는 이를 실제 앱에 심는 절차입니다.

---

## 0. 왜 "전체 재작성"이 아니라 Hybrid 인가
- 현재 코드 약 10,000줄(차트·그리드·실시간 WS·COM 연동)을 전부 Blazor로 옮기는 것은 위험/비효율.
- BlazorWebView 컨트롤을 WinForms 폼 안에 넣으면, **한 프로젝트에서** 웹 UI와 기존 폼을 같이 쓸 수 있음.
- 1단계: 홈 화면을 Blazor로. 2단계 이후: 모듈을 하나씩 Razor로 점진 이전(원할 때).

---

## 1. 사전 준비
- Windows + **WebView2 런타임**(대개 최신 Edge에 기본 포함, 없으면 MS에서 Evergreen 런타임 설치).
- .NET 8 SDK (이미 사용 중).

## 2. csproj 수정 (`csproj_changes.txt` 참고)
프로젝트 SDK를 Razor로 바꾸고 Blazor 패키지를 추가합니다.

```xml
<!-- 변경 전 -->
<Project Sdk="Microsoft.NET.Sdk">
<!-- 변경 후 -->
<Project Sdk="Microsoft.NET.Sdk.Razor">
```
```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net8.0-windows</TargetFramework>
  <UseWindowsForms>true</UseWindowsForms>
  <!-- 추가 -->
  <Nullable>enable</Nullable>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Microsoft.AspNetCore.Components.WebView.WindowsForms" Version="8.0.*" />
</ItemGroup>
```
> ⚠️ NuGet 복원이 가능한 환경에서 실행하세요. 사내망이라면 NuGet 접근을 먼저 확인.
> 복원이 막히면 이 단계 전체를 보류하고 프로토타입(HTML)만 사용해도 됩니다.

## 3. 파일 배치
동봉 템플릿의 `.txt` 확장자를 떼고 프로젝트에 추가합니다.

```
StockManager/
├─ wwwroot/
│   ├─ index.html              ← (index.html.txt)
│   └─ app.css                 ← 프로토타입 <style> 내용을 그대로 복사
├─ Blazor/
│   ├─ _Imports.razor          ← (_Imports.razor.txt)
│   ├─ App.razor               ← (App.razor.txt)
│   ├─ MainLayout.razor        ← (MainLayout.razor.txt)
│   ├─ NavMenu.razor           ← (NavMenu.razor.txt)
│   └─ Dashboard.razor         ← (Dashboard.razor.txt)
├─ BlazorHomeForm.cs           ← (BlazorHomeForm.cs.txt)
└─ ModuleBridge.cs             ← (ModuleBridge.cs.txt)
```
- `wwwroot/index.html` 안에 SortableJS CDN 스크립트가 포함돼 있어 드래그가 동작합니다(오프라인이면 로컬 js로 교체).
- `app.css` 는 프로토타입 HTML의 `<style>...</style>` 내용을 그대로 붙여넣으면 됩니다(이미 라이트/다크 변수 포함).

## 4. 진입점 연결 (`Program.cs`)
런처를 Blazor 홈으로 바꾸되, 기존 런처도 살려두고 선택 가능하게 합니다.

```csharp
// 기존: Application.Run(new LauncherForm(provider, db));
ServiceCollection sc = new();
sc.AddWindowsFormsBlazorWebView();
ModuleBridge.Configure(provider, db);     // Blazor에서 기존 모듈을 열 수 있게 주입
Application.Run(new BlazorHomeForm(sc));   // 새 Blazor 홈
```

## 5. 기존 모듈과의 공존 (핵심)
- `ModuleBridge` 가 정적 진입점입니다. Blazor의 사이드바/위젯에서 호출하면 **기존 WinForms 모듈 창**이 그대로 열립니다.
  ```csharp
  ModuleBridge.Open("Portfolio");   // new Portfolio.MainForm(provider, db).Show()
  ModuleBridge.Open("Crypto");
  ModuleBridge.Open("Paper");
  ```
- `Dashboard.razor` 의 위젯 데이터(총자산/코인/모의계좌)는 기존 `LocalDatabase`·`UpbitProvider`·`PaperBroker`를
  그대로 호출해 채웁니다(중복 로직 없음). 예시는 `Dashboard.razor.txt` 주석 참고.

## 6. 드래그·위젯 추가/제거 영속
- 위젯 배치는 Blazor에서 JS interop(SortableJS) + `LocalDatabase.SetSetting("Dashboard.Layout", json)` 으로 저장 권장.
- 프로토타입은 메모리 상태만 사용(새로고침 시 초기화). 영속은 통합 시 추가하세요.

---

## 단계별 권장 로드맵
1. **이번 단계**: Blazor 홈(대시보드) + 기존 모듈 창 공존 — 위 절차.
2. 다음: 자주 쓰는 모듈 1~2개를 Razor 컴포넌트로 이전(예: 총자산, 모의투자 — owner-draw가 적어 이전이 쉬움).
3. 차트는 Lightweight Charts(웹) 위젯으로 자연스럽게 흡수(이미 v3.0에서 WebView2 템플릿 제공).
4. 마지막에 무거운 모듈(실시간/EPLAN 연동 등)을 필요 시 이전.

## 주의 (정직한 한계)
- 이 스캐폴드는 **빌드 검증이 불가한 환경**에서 작성됐습니다. 실제 빌드 시 네임스페이스/패키지 버전 조정이 필요할 수 있습니다.
- BlazorWebView는 WebView2 런타임에 의존합니다(미설치 시 빈 화면). 첫 실행 전 런타임을 확인하세요.
- 기존 모듈의 실시간 타이머/COM 연동은 그대로 동작합니다(별도 창이므로 영향 없음).
