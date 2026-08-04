# MS2E StockSuite — 배포 설치관리자 (원클릭 빌드)

자체 포함(.NET 8 런타임 내장) Windows 설치관리자를 생성합니다. 받는 사람은 **사전 설치 없이** Windows 10/11에서
**설치·실행·제거**할 수 있고, **소스코드는 포함되지 않습니다**(컴파일된 DLL/EXE 만 배포).

> **현행 스크립트**: `MS2E.StockSuite.iss` + 루트의 `build-installer.ps1`.
> 구버전 `StockManager.iss`(v3.4.1·프레임워크 의존)는 **폐기(deprecated)** — 참고용으로만 남겨둠. 신규 배포는 아래를 사용.

## 산출물

- `dist\MS2E.StockSuite-Setup-v<버전>.exe` — 단일 설치 파일(약 150~200MB).
  - **사용자 단위 설치**(관리자 권한/UAC 불필요) — 제어판 **프로그램 추가/제거**에 등록(설치·제거).
  - 시작 메뉴 + (선택)바탕화면 아이콘.
  - **WebView2 런타임**이 없으면 설치 중 자동 설치(Evergreen 부트스트랩 동봉).
  - **Windows 10 (1809+) ~ Windows 11** 지원(MinVersion 10.0.17763).
  - 기존 v3.4.1 설치 위에 설치 시 **업그레이드**로 인식(동일 AppId 유지).

## 개발 PC 전제 조건 (1회 설치)

| 도구 | 용도 | 링크 |
|---|---|---|
| **.NET 8 SDK** | 자체 포함 퍼블리시 | https://dotnet.microsoft.com/download/dotnet/8.0 |
| **Inno Setup 6** | 설치관리자 컴파일(iscc.exe) | https://jrsoftware.org/isdl.php |

## 빌드 방법

### 방법 A — 앱 사이드 메뉴 (원클릭)
앱 실행 → 사이드 메뉴 **🧰 도구 → 📦 배포 패키지 만들기** → 버전 확인 → **🚀 설치관리자 빌드**.
전제 조건(.NET SDK·Inno Setup) 상태가 표시되고, 빌드 로그가 실시간 출력됩니다. 완료 후 **📂 dist 폴더 열기**.
(배포된 앱에는 스크립트·SDK 가 없어 이 도구가 비활성화됩니다 — 개발 PC 전용.)

### 방법 B — 스크립트 (터미널)
```powershell
pwsh ./build-installer.ps1 -Version 10.1.0
```
옵션: `-SkipPublish`(재퍼블리시 생략) · `-SkipProxy`(KiwoomProxy 제외) · `-IsccPath <경로>`(iscc 수동 지정).

## 파이프라인 단계 (build-installer.ps1)

1. **자체 포함 퍼블리시** — 메인(win-x64) + KiwoomProxy(win-x86, `KiwoomProxy\` 하위폴더). `.pdb/.cs/.xml` 제거(소스 보호).
2. **WebView2 부트스트랩 동봉** — 미설치 PC 자동 설치용(`installer\MicrosoftEdgeWebview2Setup.exe` 자동 다운로드).
3. **Inno Setup 탐지** — `iscc.exe`(Program Files\Inno Setup 6).
4. **컴파일** — `installer\MS2E.StockSuite.iss` → `dist\...Setup...exe`.

## 참고

- **소스 보호 수준**: `.cs` 원본은 패키지에 없습니다. .NET IL 은 이론상 역컴파일이 가능하나 원본 소스 트리는 노출되지
  않습니다. 더 강한 보호가 필요하면 IL 난독화(예: Obfuscar)를 추가 도입할 수 있습니다(Blazor 리플렉션 재검증 필요).
- **KiwoomProxy**: 키움 실거래를 쓰는 경우에만 필요하며, 받는 사람 PC 에 **키움 OpenAPI+** 별도 설치가 전제입니다.
  키움을 쓰지 않으면 `-SkipProxy` 로 제외해 용량을 줄일 수 있습니다.
- `publish/`, `dist/`, `installer/MicrosoftEdgeWebview2Setup.exe` 는 빌드 산출물이라 git 에 포함되지 않습니다.
