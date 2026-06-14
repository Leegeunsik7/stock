# 📈 MS2-E Stock Suite — 앱 프로젝트

> 이 디렉터리는 본체 앱(`net8.0-windows`, WinExe, `AssemblyName=MS2E.StockSuite`)입니다.
> **제품 소개·아키텍처·빌드/설치 가이드(쇼케이스)는 리포지토리 루트의 README를 보세요:**
>
> - 🇺🇸 [`../README.md`](../README.md)
> - 🇰🇷 [`../README.ko.md`](../README.ko.md)
> - 🤝 기여 가이드: [`../CONTRIBUTING.md`](../CONTRIBUTING.md)
> - 🗒️ 상세 진행 이력: [`PROGRESS.md`](PROGRESS.md)
> - 📦 설치 파일 빌드: [`../installer/README.md`](../installer/README.md)

현재 버전 **v3.41.0** · Blazor Hybrid(WebView2) · 순수 결정론적 분석 코어 61개 모듈 ·
Blazor 페이지 39개 · 단위 테스트 334건.

## 빠른 명령

```powershell
# 리포 루트(StockManager_v3.4.1)에서 실행
dotnet build StockManager.sln -c Release -m:1
dotnet test  StockManager.Tests\StockManager.Tests.csproj -c Release -m:1
dotnet run   --project StockManager\StockManager.csproj            # 데스크톱 UI
dotnet run   --project StockManager\StockManager.csproj -- --headless   # 백그라운드 엔진만
```

## 디렉터리 개요

```
StockManager/
├── Program.cs            # 호스트: 트레이 · --headless · --classic
├── Common/               # 순수 코어 엔진 + 어댑터 (61개 .cs)
├── Blazor/               # Hybrid UI (Pages 39개 · NavMenu 아코디언 IA)
├── PROGRESS.md           # 버전별 상세 진행 이력 (v2.x → v3.41)
└── StockManager.csproj   # <Version> 3.41.0
```

> 과거 v2.2~v2.3 시절의 WinForms 5프로그램 런처 문서는 더 이상 현재 구조를 반영하지 않아
> 루트 쇼케이스 README로 대체되었습니다. 세부 변경 이력은 `PROGRESS.md` 및 `git log` 참고.
