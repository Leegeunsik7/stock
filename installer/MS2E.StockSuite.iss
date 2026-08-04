; ============================================================================
; MS2E StockSuite — Inno Setup 배포 스크립트 (v10.1.0 Phase 94-A)
;   자체 포함(.NET 8 런타임 내장) x64 메인 + x86 KiwoomProxy 번들 + WebView2 자동 설치.
;   build-installer.ps1 이 publish\installer_stage 를 채운 뒤 iscc 로 이 스크립트를 컴파일한다.
;   산출: dist\MS2E.StockSuite-Setup-v<버전>.exe (제어판 설치/제거 등록·시작메뉴/바탕화면 아이콘).
;
;   ★소스 보호: 스테이징에는 컴파일된 DLL/EXE 만 포함(.cs 원본 미포함·build-installer.ps1 이 .pdb/.xml 제거).
;   ★Win 10/11: MinVersion=10.0.17763(Windows 10 1809+)로 두 OS 커버.
; ============================================================================

; 버전: CLI(/DAppVersion=...)가 있으면 그것을, 없으면 기본값을 사용.
#ifndef AppVersion
  #define AppVersion "10.1.0"
#endif
#define AppName "MS2E StockSuite"
#define AppPublisher "AngTec"
#define AppExeName "MS2E.StockSuite.exe"
#define StageDir "..\publish\installer_stage"

[Setup]
; ★AppId 는 절대 바꾸지 말 것 — 업그레이드/제거 식별자(고정 GUID). 기존 v3.4.1 설치기(StockManager.iss)와 동일 AppId 를
;   유지해 구버전 위에 설치 시 '별개 앱'이 아닌 '업그레이드'로 인식되게 한다.
AppId={{8F3A2C10-9B4E-4D7A-AE21-6C2F0B5E9D34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName} v{#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=..\dist
OutputBaseFilename=MS2E.StockSuite-Setup-v{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
; ★x64 전용(자체 포함 win-x64). Win 10 1809+ ~ Win 11 커버.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; ★사용자 단위 설치(관리자 권한/UAC 불필요) — 개인 배포에 친화적. {autopf}=사용자 Programs, WebView2 부트스트랩도 per-user 설치.
PrivilegesRequired=lowest
WizardStyle=modern
DisableProgramGroupPage=yes
ShowLanguageDialog=auto

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; ★스테이징(자체 포함 퍼블리시 산출물) 전체 — 하위 폴더(kiwoom-x86 x86 프록시 포함) 재귀 복사.
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; WebView2 Evergreen 부트스트랩(온라인 설치기·~2MB) — 미설치 시에만 동봉·실행 후 삭제.
Source: "MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: WebView2Missing

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} 제거"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; WebView2 런타임 설치(미설치 시·무인 설치).
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Microsoft WebView2 런타임 설치 중..."; Check: WebView2Missing
; 설치 완료 후 즉시 실행 옵션.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
{ WebView2 Evergreen 런타임 설치 여부 판정 — 클라이언트 GUID 의 pv(버전) 레지스트리 키 확인(HKLM x64/x86·HKCU). }
function WebView2Installed: Boolean;
var
  v: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0') then
    Result := True
  else if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0') then
    Result := True
  else if RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', v) and (v <> '') and (v <> '0.0.0.0') then
    Result := True;
end;

function WebView2Missing: Boolean;
begin
  Result := not WebView2Installed;
end;
