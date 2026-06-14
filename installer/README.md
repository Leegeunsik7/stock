# 📦 설치 파일(Installer) 빌드 가이드 — MS2-E Stock Suite

Inno Setup 기반 Windows 설치 파일(`.exe`)을 만드는 방법입니다. 더블클릭 한 번으로
앱·의존성·시작메뉴·언인스톨이 모두 세팅됩니다.

## 사전 준비
1. **Inno Setup 6+** 설치 — https://jrsoftware.org/isdl.php (Unicode 버전, 무료)
   - 설치 후 `ISCC.exe`(콘솔 컴파일러)가 `C:\Program Files (x86)\Inno Setup 6\`에 위치.

## 빌드 (2단계)
프로젝트 루트(`StockManager_v3.4.1`)에서:

```powershell
# 1) 앱 게시(프레임워크 의존 — 대상 PC에 .NET 8 데스크톱 런타임 필요)
dotnet publish StockManager\StockManager.csproj -c Release -o StockManager\publish

# 2) 설치 파일 컴파일
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\StockManager.iss
```

→ 결과물: `installer\Output\MS2E-StockSuite-Setup-3.41.0.exe`

> **완전 자립 실행(.NET 미설치 PC도 OK)** 을 원하면 1)을 self-contained로:
> ```powershell
> dotnet publish StockManager\StockManager.csproj -c Release -r win-x64 --self-contained true -o StockManager\publish
> ```
> (용량은 커지지만 대상 PC에 .NET 런타임 설치 불필요)

## 설치 파일이 해주는 것
- `%ProgramFiles%\MS2-E Stock Suite`(또는 사용자 폴더)에 앱 설치 — **관리자 권한 불필요**(`PrivilegesRequired=lowest`)
- 시작 메뉴: **MS2-E Stock Suite** + **(헤드리스 상주)** 바로가기(`--headless`)
- 바탕화면 아이콘(옵션) · 표준 제거 관리자 등록
- **WebView2 런타임 점검**: 미설치 시 다운로드 링크 안내(대부분의 Win11/최신 Win10은 기본 탑재)

## CI 연동
`.github/workflows/ci.yml`은 태그(`v*`) 푸시 시 `dotnet publish → zip → GitHub Release`까지
자동화합니다. 설치 파일까지 자동 빌드하려면 릴리스 job에 Inno Setup 설치 + ISCC 스텝을 추가하면 됩니다
(러너에 `choco install innosetup` 후 위 ISCC 명령).
