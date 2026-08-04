# ============================================================================
# build-installer.ps1 — v10.1.0 Phase 94-A: 원클릭 배포 설치관리자 빌드
#
#   1) 자체 포함 퍼블리시 — 메인(win-x64) + KiwoomProxy(win-x86·별도 하위폴더). .NET 8 런타임 내장 →
#      받는 사람 사전 설치 불필요. ★소스 보호: .pdb/.cs/.xml 잔재 제거(컴파일 DLL/EXE 만 배포).
#   2) WebView2 Evergreen 부트스트랩 동봉(미설치 PC 자동 설치용).
#   3) Inno Setup(iscc.exe) 탐지.
#   4) 설치관리자 컴파일 → dist\MS2E.StockSuite-Setup-v<버전>.exe (제어판 설치/제거·Win 10/11).
#
#   사용:  pwsh ./build-installer.ps1  [-Version 10.1.0]  [-IsccPath <iscc.exe>]
#          [-SkipPublish] [-SkipProxy] [-SkipWebView2Download]
#
#   ⚠️ 전제: 개발 PC 에 .NET 8 SDK + Inno Setup 6(https://jrsoftware.org/isdl.php) 설치.
# ============================================================================
param(
    [string]$Version = "10.1.0",
    [string]$IsccPath = "",
    [switch]$SkipPublish,
    [switch]$SkipProxy,
    [switch]$SkipWebView2Download
)
$ErrorActionPreference = "Stop"
$root      = $PSScriptRoot
$mainProj  = Join-Path $root "StockManager\StockManager.csproj"
$proxyProj = Join-Path $root "StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj"
$stage     = Join-Path $root "publish\installer_stage"
$iss       = Join-Path $root "installer\MS2E.StockSuite.iss"
$dist      = Join-Path $root "dist"
$wv2       = Join-Path $root "installer\MicrosoftEdgeWebview2Setup.exe"

function Write-Step($n, $msg) { Write-Host "[$n] $msg" -ForegroundColor Cyan }

Write-Host "== MS2E StockSuite v$Version 배포 설치관리자 빌드 ==" -ForegroundColor Green

# ── 1) 자체 포함 퍼블리시 ────────────────────────────────────────────────────
if (-not $SkipPublish) {
    Write-Step "1/4" "자체 포함 퍼블리시 (메인 win-x64$(if (-not $SkipProxy) { ' + 프록시 win-x86' }))..."
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    dotnet publish $mainProj -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=true -p:DebugType=none -p:DebugSymbols=false -o $stage
    if ($LASTEXITCODE -ne 0) { throw "메인 퍼블리시 실패(exit $LASTEXITCODE)" }

    if (-not $SkipProxy -and (Test-Path $proxyProj)) {
        # ★x86 프록시는 x64 메인과 DLL 충돌 방지 위해 반드시 별도 하위폴더에.
        $proxyOut = Join-Path $stage "KiwoomProxy"
        dotnet publish $proxyProj -c Release -r win-x86 --self-contained true `
            -p:DebugType=none -p:DebugSymbols=false -o $proxyOut
        if ($LASTEXITCODE -ne 0) { throw "프록시 퍼블리시 실패(exit $LASTEXITCODE)" }
    }

    # ★소스 보호: 디버그 심볼/원본/문서 잔재 제거(컴파일 바이너리만 배포).
    Get-ChildItem $stage -Include *.pdb, *.cs, *.xml -Recurse -File -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue
    $stageFiles = (Get-ChildItem $stage -Recurse -File).Count
    Write-Host "  스테이징: $stage ($stageFiles 파일)" -ForegroundColor Gray
} else {
    Write-Step "1/4" "퍼블리시 건너뜀(-SkipPublish)"
}

# ── 2) WebView2 부트스트랩 동봉 ──────────────────────────────────────────────
Write-Step "2/4" "WebView2 Evergreen 부트스트랩 준비..."
if ((-not (Test-Path $wv2)) -and (-not $SkipWebView2Download)) {
    try {
        Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $wv2 -UseBasicParsing
        Write-Host "  다운로드 완료: $wv2" -ForegroundColor Gray
    } catch {
        Write-Warning "  WebView2 부트스트랩 다운로드 실패 — 수동 배치 필요(https://developer.microsoft.com/microsoft-edge/webview2/)."
    }
}
if (-not (Test-Path $wv2)) {
    throw "WebView2 부트스트랩 부재: $wv2 — 인터넷 연결 후 재시도하거나 수동 배치하세요(설치기가 [Files]에서 참조)."
}

# ── 3) Inno Setup 탐지 ───────────────────────────────────────────────────────
Write-Step "3/4" "Inno Setup(iscc.exe) 탐지..."
if (-not $IsccPath) {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",   # winget 사용자 스코프 설치(기본)
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 5\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe"
    )
    $IsccPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $IsccPath) {
        $cmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
        if ($cmd) { $IsccPath = $cmd.Source }
    }
}
if ((-not $IsccPath) -or (-not (Test-Path $IsccPath))) {
    throw "Inno Setup(iscc.exe) 미탐지 — https://jrsoftware.org/isdl.php 설치 후 재시도(또는 -IsccPath 로 지정)."
}
Write-Host "  iscc: $IsccPath" -ForegroundColor Gray

# ── 4) 설치관리자 컴파일 ─────────────────────────────────────────────────────
Write-Step "4/4" "Inno Setup 컴파일 → dist\..."
New-Item -ItemType Directory -Force -Path $dist | Out-Null
& $IsccPath "/DAppVersion=$Version" "$iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup 컴파일 실패(exit $LASTEXITCODE)" }

$setup = Join-Path $dist "MS2E.StockSuite-Setup-v$Version.exe"
if (Test-Path $setup) {
    $mb = [Math]::Round((Get-Item $setup).Length / 1MB, 1)
    Write-Host "== 완료: $setup ($mb MB) ==" -ForegroundColor Green
} else {
    Write-Warning "컴파일 성공했으나 예상 산출물 미발견: $setup"
}
