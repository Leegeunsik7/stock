; ============================================================================
;  MS2-E Stock Suite — Inno Setup 설치 스크립트 (v3.41.0)
;  더블클릭 한 번으로 앱 설치 + 시작메뉴/언인스톨 + WebView2 의존성 안내.
;  빌드: 먼저 publish 후 ISCC 컴파일 (installer\README.md 참고)
;    1) dotnet publish StockManager\StockManager.csproj -c Release -o StockManager\publish
;    2) ISCC installer\StockManager.iss   →  installer\Output\MS2E-StockSuite-Setup-3.41.0.exe
; ============================================================================

#define MyAppName "MS2-E Stock Suite"
#define MyAppVersion "3.41.0"
#define MyAppPublisher "Angtec"
#define MyAppExeName "MS2E.StockSuite.exe"
#define PublishDir "..\StockManager\publish"

[Setup]
; 고정 AppId — 업그레이드 시 동일 유지(절대 변경 금지)
AppId={{8F3A2C10-9B4E-4D7A-AE21-6C2F0B5E9D34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\MS2-E Stock Suite
DefaultGroupName=MS2-E Stock Suite
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=Output
OutputBaseFilename=MS2E-StockSuite-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest          ; 사용자 단위 설치(관리자 권한 불필요)
WizardStyle=modern
DisableProgramGroupPage=yes

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면 바로가기 생성"; GroupDescription: "추가 아이콘:"; Flags: unchecked

[Files]
; dotnet publish 산출물 전체(앱 + 의존성 DLL + wwwroot)
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} (헤드리스 상주)"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--headless"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "지금 {#MyAppName} 실행"; Flags: nowait postinstall skipifsilent

[Code]
{ Evergreen WebView2 런타임 설치 여부 확인(BlazorWebView 필수 의존성) }
function WebView2Installed(): Boolean;
var v: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) or
    RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) or
    RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not WebView2Installed()) then
    MsgBox('이 앱은 Microsoft Edge WebView2 런타임이 필요합니다.' + #13#10 +
           '대부분의 Windows 11 / 최신 Windows 10에는 이미 설치되어 있습니다.' + #13#10 +
           '없다면 아래에서 무료로 설치하세요:' + #13#10 +
           'https://go.microsoft.com/fwlink/p/?LinkId=2124703',
           mbInformation, MB_OK);
end;
