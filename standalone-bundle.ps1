# ============================================================================
# standalone-bundle.ps1 — [v12.18.28-DEV] 즉시 실행 배포본(Standalone win-x64) 생성 · 봉인 바이너리 동일성 · 격리 기동 검증
#
#   release-package.ps1 [11/11] 이 호출한다(감사 봉인본 [0/10]~[10/10] 완료 후 — 봉인 zip · master-seal.txt · RELEASE_MANIFEST.json 은 읽기만).
#   ① 봉인 zip 을 임시 폴더에 추출(출하 바이너리 원천 = 봉인 zip 자체 · 개발 bin 재사용 0).
#   ② 런타임 층 = dotnet publish 자체 포함(self-contained): 호스트 win-x64 · KiwoomProxy win-x86 → runtimeconfig(includedFrameworks) · deps.json ·
#      hostfxr/coreclr · WebView2Loader · e_sqlite3 · wwwroot. .NET 런타임 설치 불필요(WebView2 런타임은 Windows 구성요소 — 별도).
#   ③ 봉인 오버레이: 봉인 zip 의 host\* · proxy\* 38개를 publish 출력 위에 덮어쓴다 → 배포본 바이너리 = 봉인 바이너리(바이트 동일).
#      artifacts.sha256.txt 전 항목 해시 대조(불일치 · 누락 = 중단) · 배포본 루트에 artifacts.sha256.txt · manifest.sig · master-seal.txt 동봉.
#   ④ 개발 경로 누출 차단: *.staticwebassets.runtime.json(빌드 머신 절대 경로) · *.pdb 제거.
#   ⑤ zip + .sha256 → 배포본 판독 테스트(StandaloneBundleTests 파이프라인 · MS2E_RELEASE_STANDALONE_ZIP) → 격리 기동 검증:
#      배포 zip 을 새 임시 폴더에 풀어 host\MS2E.StockSuite.exe 실행 · 주 창 핸들 출현(≤ 45초) 확인 후 종료(-SkipLaunch = 생략 · 무인 CI 용).
#   실패-닫힘: 어느 단계든 실패 = 배포 zip 삭제 + exit 1(호출자가 감사 봉인 산출물까지 전량 폐기).
#   ★피어 저장소 참조 0 · liveArmed 기본값 = 봉인 바이너리 그대로([9/10] --verify-live-armed 가 판독한 바로 그 바이트).
#
#   사용:  powershell -File ./standalone-bundle.ps1 -Version <ver> -SealedZip <release-v<ver>-GM.zip> [-OutDir <release>] [-SkipLaunch]
# ============================================================================
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$SealedZip,
    [string]$OutDir = "",
    [switch]$SkipLaunch
)
$ErrorActionPreference = "Stop"
$root      = $PSScriptRoot
$hostProj  = Join-Path $root "StockManager\StockManager.csproj"
$proxyProj = Join-Path $root "StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj"
$testsProj = Join-Path $root "StockManager.Tests\StockManager.Tests.csproj"
if ([string]::IsNullOrWhiteSpace($OutDir)) { $OutDir = Split-Path -Parent $SealedZip }
$bundleName = "StockManager-v$Version-Standalone-win-x64"
$bundleZip  = Join-Path $OutDir "$bundleName.zip"
$work       = Join-Path ([IO.Path]::GetTempPath()) ("ms2e-standalone-" + [Guid]::NewGuid().ToString("N"))
$launchDir  = Join-Path ([IO.Path]::GetTempPath()) ("ms2e-standalone-launch-" + [Guid]::NewGuid().ToString("N"))

function Remove-Work { foreach ($d in @($work, $launchDir)) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } } }
function Abort($msg) {
    Write-Host "✗ [standalone] $msg" -ForegroundColor Red
    foreach ($f in @($bundleZip, "$bundleZip.sha256")) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
    Remove-Work
    exit 1
}
trap { Abort ("예외: " + $_.Exception.Message) }                               # 종료 오류(추출 · 복사 · 압축)도 실패-닫힘 정리 경유

if (-not (Test-Path -LiteralPath $SealedZip)) { Abort "봉인 zip 없음: $SealedZip" }
foreach ($f in @($bundleZip, "$bundleZip.sha256")) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
$sealedDir = Join-Path $work "sealed"
$bundleDir = Join-Path $work $bundleName

# ① 봉인 zip 추출(출하 바이너리 원천)
Expand-Archive -LiteralPath $SealedZip -DestinationPath $sealedDir -Force
$shaList = Join-Path $sealedDir "artifacts.sha256.txt"
if (-not (Test-Path -LiteralPath $shaList)) { Abort "봉인 zip 에 artifacts.sha256.txt 없음" }
$sealed = @(Get-Content -LiteralPath $shaList -Encoding UTF8 | Where-Object { $_ -match '^[0-9A-Fa-f]{64}\s+\S' } | ForEach-Object {
    $h, $p = $_ -split '\s+', 2
    [pscustomobject]@{ Hash = $h.ToUpperInvariant(); Rel = $p.Trim().Replace('/', '\') }
})
if ($sealed.Count -eq 0) { Abort "봉인 아티팩트 목록 0건" }

# ② 런타임 층(self-contained publish)
dotnet publish $hostProj -c Release -r win-x64 --self-contained true -o (Join-Path $bundleDir "host") -m:1 -warnaserror | Out-Host
if ($LASTEXITCODE -ne 0) { Abort "호스트 publish(win-x64 self-contained) 실패(exit $LASTEXITCODE)" }
dotnet publish $proxyProj -c Release -r win-x86 --self-contained true -o (Join-Path $bundleDir "proxy") -m:1 -warnaserror | Out-Host
if ($LASTEXITCODE -ne 0) { Abort "KiwoomProxy publish(win-x86 self-contained) 실패(exit $LASTEXITCODE)" }

# ③ 봉인 오버레이 + 전 항목 해시 대조
foreach ($a in $sealed) {
    $src = Join-Path $sealedDir $a.Rel
    if (-not (Test-Path -LiteralPath $src)) { Abort "봉인 zip 에 등재 아티팩트 없음: $($a.Rel)" }
    Copy-Item -LiteralPath $src -Destination (Join-Path $bundleDir $a.Rel) -Force
}
foreach ($a in $sealed) {
    $got = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $bundleDir $a.Rel)).Hash
    if ($got -ne $a.Hash) { Abort "배포본 바이너리 ≠ 봉인 해시: $($a.Rel)" }
}
Copy-Item -LiteralPath $shaList -Destination $bundleDir -Force
Copy-Item -LiteralPath (Join-Path $sealedDir "manifest.sig") -Destination $bundleDir -Force
$sealTxt = Join-Path $OutDir "master-seal.txt"
if (Test-Path -LiteralPath $sealTxt) { Copy-Item -LiteralPath $sealTxt -Destination $bundleDir -Force }
Write-Host "  봉인 오버레이: $($sealed.Count)개 바이너리 = 봉인 해시(바이트 동일)" -ForegroundColor DarkGreen

# ④ 개발 경로 누출 · 디버그 심볼 제거
Get-ChildItem -Path $bundleDir -Recurse -File -Include *.staticwebassets.runtime.json, *.pdb | Remove-Item -Force

# 필수 실행 자산(누락 0)
$required = @("host\MS2E.StockSuite.exe", "host\MS2E.StockSuite.dll", "host\MS2E.StockSuite.runtimeconfig.json", "host\MS2E.StockSuite.deps.json",
              "host\hostfxr.dll", "host\coreclr.dll", "host\WebView2Loader.dll", "host\e_sqlite3.dll", "host\wwwroot\index.html",
              "proxy\MS2E.KiwoomProxyHost.exe", "proxy\MS2E.KiwoomProxyHost.runtimeconfig.json", "proxy\hostfxr.dll",
              "artifacts.sha256.txt", "manifest.sig")
foreach ($r in $required) { if (-not (Test-Path -LiteralPath (Join-Path $bundleDir $r))) { Abort "필수 실행 자산 누락: $r" } }
foreach ($rc in @("host\MS2E.StockSuite.runtimeconfig.json", "proxy\MS2E.KiwoomProxyHost.runtimeconfig.json")) {
    if ((Get-Content -LiteralPath (Join-Path $bundleDir $rc) -Raw) -notmatch '"includedFrameworks"') { Abort "자체 포함 런타임 아님(includedFrameworks 없음): $rc" }
}

# ⑤ zip + 해시 → 판독 테스트 → 격리 기동
Compress-Archive -Path (Join-Path $bundleDir "*") -DestinationPath $bundleZip -Force
$bundleSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $bundleZip).Hash
Set-Content -LiteralPath "$bundleZip.sha256" -Value ("{0}  {1}" -f $bundleSha, (Split-Path $bundleZip -Leaf)) -Encoding UTF8
$env:MS2E_RELEASE_STANDALONE_ZIP = $bundleZip
try {
    dotnet test $testsProj -c Release -m:1 --no-build --filter "FullyQualifiedName~StandaloneBundleTests.Pipeline_Standalone_Zip_Is_Runnable_Layout_When_Provided" | Out-Host
    $testExit = $LASTEXITCODE
} finally {
    Remove-Item Env:\MS2E_RELEASE_STANDALONE_ZIP -ErrorAction SilentlyContinue
}
if ($testExit -ne 0) { Abort "배포본 판독 테스트 실패(exit $testExit)" }

if ($SkipLaunch) {
    Write-Host "  격리 기동 검증 생략(-SkipLaunch)" -ForegroundColor DarkGray
} else {
    Expand-Archive -LiteralPath $bundleZip -DestinationPath $launchDir -Force
    $exe = Join-Path $launchDir "host\MS2E.StockSuite.exe"
    $p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) -PassThru
    $hwnd = [int64]0
    for ($i = 0; $i -lt 90 -and $hwnd -eq 0 -and -not $p.HasExited; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh(); $hwnd = [int64]$p.MainWindowHandle }
    if ($p.HasExited) { Abort "격리 기동 실패 — 프로세스 조기 종료(exit $($p.ExitCode))" }
    $title = $p.MainWindowTitle
    $path = $p.Path
    Stop-Process -Id $p.Id -Force
    $p.WaitForExit(10000) | Out-Null
    if ($hwnd -eq 0) { Abort "격리 기동 실패 — 45초 내 주 창 없음(PID $($p.Id))" }
    if (-not $path.StartsWith($launchDir, [StringComparison]::OrdinalIgnoreCase)) { Abort "격리 기동 경로 이탈: $path" }
    Write-Host ("  격리 기동: PID {0} · HWND 0x{1:X} · '{2}' · {3}" -f $p.Id, $hwnd, $title, $path) -ForegroundColor DarkGreen
}

Remove-Work
Write-Host "== 배포본: $bundleZip (SHA-256 $bundleSha) ==" -ForegroundColor Green
exit 0
