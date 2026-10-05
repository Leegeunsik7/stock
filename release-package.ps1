# ============================================================================
# release-package.ps1 — v12.14.1-GM Production Golden Master 봉인 & 배포 패키징 자동화 ([Phase 4] 교정판)
#
#   실제 '동시 출하' 아티팩트는 Host(win-x64) + KiwoomProxy(win-x86) 뿐이다(WebOnly·PrimesBench 는 CI 검증 전용).
#     [1/8] WebOnly Release 빌드 — 0W/0E 검증 전용(산출물 폐기 · Host 출력 폴더를 공유하므로 먼저 태움)
#     [2/8] Default Release 빌드(sln: Host+Tests+PrimesBench+x86 KiwoomProxy) — 0W/0E, 패키징 대상
#     [3/8] StockManager 전체 유닛 테스트 — 100% Pass 필수
#     [4/8] infra-scan.ps1(Phase 83 결합도) — baseline 이하
#     [5/8] Phase114 RatchetCeiling(≤40) 단독 재확인
#     [6/8] liveArmed 소스 가드 — Dispatch.ValveWired/LiveArmingWired 조회의 기본값이 "전부" 리터럴 false 인지
#           ([Phase 4] 교정: 기존은 false 호출이 1개라도 있으면 통과 → true 기본값 호출이 추가돼도 통과하던 fail-open)
#     [7/8] 스테이징 + SHA-256 리스트 + genesis v2 manifest.sig 생성·검증 + 출하 바이너리 IL 가드
#           ([Phase 4] 교정: 기존은 release\ 를 지운 뒤 같은 폴더의 manifest.sig 존재를 검사해 구조상 영구 미동봉이었다 —
#           이제 GoldenMasterFreezeTests 파이프라인 모드가 "스테이징된 실제 출하 바이너리"로 매니페스트를 생성·검증하고,
#           같은 테스트가 IL 로 물리 이중키 기본값 false 를, 해시로 "검사한 바이너리 = 출하 바이너리"를 결속한다. 미생성 = 중단)
#           + [봉인 운영] 수검 CLI 드라이런: 출하 호스트 바이너리 자신의 --verify-seal 로 스테이징 폴더를 수검(외부 앵커 = 방금 생성한
#             masterSeal · 미등재 바이너리 0 · 런타임 BootIntegrityGate 와 동일 코드 경로). 실패 = 중단.
#     [8/8] zip 패키징 + 사후 검증(빈 zip·해시 리스트 불일치·매니페스트 누락/봉인 불일치 → zip 삭제 후 중단)
#           + [봉인 운영] 최종 zip 을 수검 CLI 로 재수검(추출 → 앵커 대조) — 통과해야만 .sha256·master-seal.txt 발행.
#   전부 실패-닫힘: 어느 단계든 실패하면 즉시 중단, 불완전 zip 은 남기지 않는다.
#
#   사용:  powershell -File ./release-package.ps1  [-Version <자동: csproj>]  [-SkipWebOnlyGate]
# ============================================================================
param(
    [string]$Version = "",
    [switch]$SkipWebOnlyGate
)
$ErrorActionPreference = "Stop"
$root       = $PSScriptRoot
$sln        = Join-Path $root "StockManager.sln"
$hostProj   = Join-Path $root "StockManager\StockManager.csproj"
$testsProj  = Join-Path $root "StockManager.Tests\StockManager.Tests.csproj"
$hostBin    = Join-Path $root "StockManager\bin\Release\net8.0-windows"
$proxyBin   = Join-Path $root "StockManager.KiwoomProxy\bin\Release\net8.0-windows"
$releaseDir = Join-Path $root "release"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $csproj = Get-Content -LiteralPath $hostProj -Raw
    if ($csproj -match '<Version>([^<]+)</Version>') { $Version = $Matches[1] } else { $Version = "0.0.0" }
}
$manifestOut = Join-Path $releaseDir "manifest.sig"
$zipOut      = Join-Path $releaseDir "release-v$Version-GM.zip"

function Fail($msg) { Write-Host "✗ $msg" -ForegroundColor Red; exit 1 }
function Step($n, $msg) { Write-Host "[$n/8] $msg" -ForegroundColor Cyan }
$hostExe = Join-Path $hostBin "MS2E.StockSuite.exe"

# [봉인 운영] 수검 CLI 호출 — 출하 호스트 실행 파일의 --verify-seal(UI/DB 초기화 이전 분기). 종료 코드 0 만 통과.
#   PS 5.1 Start-Process 는 인자를 따옴표 없이 공백 결합하므로 경로를 직접 감싼다.
function Invoke-SealCli([string]$target, [string]$seal, [string]$label) {
    if (-not (Test-Path -LiteralPath $hostExe)) { Fail "수검 CLI 실행 파일 없음: $hostExe" }
    $report = Join-Path $releaseDir "verify-$label.txt"
    $cliArgs = @('--verify-seal', ('"' + $target + '"'), '--expected', $seal, '--require-anchor', '--out', ('"' + $report + '"'))
    $proc = Start-Process -FilePath $hostExe -ArgumentList $cliArgs -Wait -PassThru -NoNewWindow
    if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Encoding UTF8 | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray } }
    return $proc.ExitCode
}

Write-Host "== MS2E StockSuite v$Version Production Golden Master 패키징 ==" -ForegroundColor Green

# ── [1/8] WebOnly 빌드(검증 전용 — 산출물 폐기, Host 출력 폴더 공유이므로 먼저 태움) ──
if (-not $SkipWebOnlyGate) {
    Step 1 "WebOnly Release 빌드(0W/0E 검증 전용)..."
    dotnet build $hostProj -c Release -p:WebOnly=true -m:1
    if ($LASTEXITCODE -ne 0) { Fail "WebOnly 빌드 실패(exit $LASTEXITCODE) — 0W/0E 아님, 패키징 중단" }
} else {
    Write-Host "[1/8] WebOnly 게이트 건너뜀(-SkipWebOnlyGate)" -ForegroundColor DarkGray
}

# ── [2/8] Default 빌드(sln 전체 — Host/Tests/PrimesBench/x86 KiwoomProxy, 패키징 대상 산출) ──
Step 2 "Default Release 빌드(sln 전체: Host+Tests+PrimesBench+x86 KiwoomProxy)..."
dotnet build $sln -c Release -m:1
if ($LASTEXITCODE -ne 0) { Fail "Default 빌드 실패(exit $LASTEXITCODE) — 0W/0E 아님, 패키징 중단" }

# ── [3/8] 전체 유닛 테스트 스위트 ──
Step 3 "전체 유닛 테스트 스위트 실행..."
dotnet test $testsProj -c Release -m:1 --no-build
if ($LASTEXITCODE -ne 0) { Fail "테스트 실패 — 패키징 중단(fail-closed)" }

# ── [4/8] infra-scan(Phase 83 결합도) ──
Step 4 "infra-scan.ps1(Phase 83 결합도) 실행..."
& (Join-Path $root "infra-scan.ps1")
if ($LASTEXITCODE -ne 0) { Fail "infra-scan 회귀(coupling > baseline) — 패키징 중단" }

# ── [5/8] Phase114 RatchetCeiling 단독 재확인 ──
Step 5 "Phase114 RatchetCeiling(PureShellFitness) 재확인..."
dotnet test $testsProj -c Release -m:1 --no-build --filter "FullyQualifiedName~Phase114PureShellFitnessTests"
if ($LASTEXITCODE -ne 0) { Fail "RatchetCeiling 회귀 — 패키징 중단" }

# ── [6/8] liveArmed 소스 가드 — 물리 이중키 조회 기본값이 "전부" 리터럴 false ──
Step 6 "liveArmed 기본값(Dispatch.ValveWired/LiveArmingWired) 소스 가드..."
$homeFormSrc = Get-Content -LiteralPath (Join-Path $root "StockManager\Blazor\BlazorHomeForm.cs") -Raw
$allCalls = [regex]::Matches($homeFormSrc, 'GetBool\(\s*"Dispatch\.(ValveWired|LiveArmingWired)"\s*,\s*([^)]*)\)')
if ($allCalls.Count -eq 0) { Fail "liveArmed 가드: 물리 이중키 조회 0건 — 가드 대상 소실(구조 변경 확인 필요)" }
foreach ($c in $allCalls) {
    if ($c.Groups[2].Value.Trim() -ne 'false') { Fail "liveArmed 가드: 기본값이 false 가 아닌 조회 발견 → $($c.Value)" }
}
Write-Host "  OK: 물리 이중키 조회 $($allCalls.Count)건 전부 기본값 false(소스) — 바이너리 IL 은 [7/8] 에서 재검증" -ForegroundColor DarkGreen

# ── [7/8] 스테이징 + SHA-256 리스트 + v2 manifest.sig(파이프라인 모드 테스트로 생성·검증) ──
Step 7 "아티팩트 스테이징 + SHA-256 리스트 + genesis v2 manifest.sig + 출하 바이너리 IL 가드..."
if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

$stage = Join-Path $releaseDir "stage"
New-Item -ItemType Directory -Force -Path (Join-Path $stage "host") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stage "proxy") | Out-Null

if (-not (Test-Path $hostBin))  { Fail "Host 산출물 폴더 없음: $hostBin" }
if (-not (Test-Path $proxyBin)) { Fail "KiwoomProxy 산출물 폴더 없음: $proxyBin" }

# ★-Include 는 -Path 가 '\*'로 끝나거나 -Recurse 와 함께여야 실제로 필터링된다(PowerShell 기존 함정 — 1차 실행 실측: 0건 zip).
Get-ChildItem -Path "$hostBin\*" -Include *.dll, *.exe -File | Copy-Item -Destination (Join-Path $stage "host")
Get-ChildItem -Path "$proxyBin\*" -Include *.dll, *.exe -File | Copy-Item -Destination (Join-Path $stage "proxy")

# ★반대 함정: -Recurse 와 함께일 땐 -Path 에 '\*' 를 붙이면 0건(실측) — 순수 디렉터리 경로로.
$sha256Lines = @("# MS2E StockSuite v$Version — 배포 아티팩트 SHA-256(원시 해시 리스트, PowerShell Get-FileHash 산출)")
Get-ChildItem -Path $stage -Recurse -Include *.dll, *.exe -File | Sort-Object FullName | ForEach-Object {
    $h = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash
    $rel = $_.FullName.Substring($stage.Length + 1)
    $sha256Lines += ("{0}  {1}" -f $h, $rel)
}
$artifactCount = $sha256Lines.Count - 1
if ($artifactCount -eq 0) { Fail "배포 아티팩트 0건 수집 — 빈 릴리스 zip 발행 금지(fail-closed)" }
Set-Content -LiteralPath (Join-Path $stage "artifacts.sha256.txt") -Value $sha256Lines -Encoding UTF8
Write-Host "  아티팩트 원시 SHA-256 리스트: $artifactCount 건" -ForegroundColor Gray

# 파이프라인 모드: 스테이징된 "실제 출하 바이너리"로 v2 manifest.sig 생성·검증 + IL 이중키 가드 + 출하=검사 대상 해시 결속
#   + 안티탬퍼 핸드셰이크(출하 바이너리 SHA-256 0B 재검증). 하나라도 실패하면 중단.
$env:MS2E_RELEASE_STAGE    = $stage
$env:MS2E_RELEASE_MANIFEST = $manifestOut
try {
    dotnet test $testsProj -c Release -m:1 --no-build --filter "FullyQualifiedName~GoldenMasterFreezeTests|FullyQualifiedName~AssemblyAntiTamperHandshakeTests"
    $freezeExit = $LASTEXITCODE
} finally {
    Remove-Item Env:\MS2E_RELEASE_STAGE, Env:\MS2E_RELEASE_MANIFEST -ErrorAction SilentlyContinue
}
if ($freezeExit -ne 0) { Fail "Golden Master 동결 검증 실패(매니페스트/IL 가드/출하 바이너리 결속) — 패키징 중단" }
if (-not (Test-Path -LiteralPath $manifestOut)) { Fail "manifest.sig 미생성 — 봉인 없는 릴리스 발행 금지(fail-closed)" }
$manifestText = Get-Content -LiteralPath $manifestOut -Raw
if ($manifestText -notmatch '"schema": "ms2e-release-manifest/2"' -or $manifestText -notmatch ('"version": "' + [regex]::Escape("$Version-GM") + '"')) {
    Fail "manifest.sig 스키마/버전 불일치(기대 v2 · $Version-GM)"
}
Copy-Item -LiteralPath $manifestOut -Destination (Join-Path $stage "manifest.sig")
Write-Host "  genesis v2 manifest.sig 생성·검증·동봉: $manifestOut" -ForegroundColor DarkGreen

# [봉인 운영] 수검 CLI 드라이런 — 스테이징 = 출하 레이아웃(<root>/manifest.sig · host/ · proxy/). 외부 앵커 = 방금 봉인한 masterSeal.
$masterSeal = (Get-Content -LiteralPath $manifestOut -Raw | ConvertFrom-Json).masterSeal
if ($masterSeal -notmatch '^[0-9A-Fa-f]{64}$') { Fail "manifest.sig masterSeal 형식 오류(64 hex 아님)" }
$stageCli = Invoke-SealCli $stage $masterSeal "stage"
if ($stageCli -ne 0) { Fail "수검 CLI 드라이런 실패(exit=$stageCli · 2=변조/누락 3=앵커 미주입 4=미등재 바이너리) — 패키징 중단" }
Write-Host "  수검 CLI 드라이런 통과(스테이징 · 앵커 일치 · 미등재 0)" -ForegroundColor DarkGreen

# ── [8/8] zip 패키징 + 사후 검증 ──
Step 8 "zip 패키징 + 사후 검증..."
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipOut -Force
Remove-Item $stage -Recurse -Force
$env:MS2E_RELEASE_ZIP = $zipOut
try {
    dotnet test $testsProj -c Release -m:1 --no-build --filter "FullyQualifiedName~GoldenMasterFreezeTests.Pipeline_Release_Zip_Is_Complete_When_Provided"
    $zipExit = $LASTEXITCODE
} finally {
    Remove-Item Env:\MS2E_RELEASE_ZIP -ErrorAction SilentlyContinue
}
if ($zipExit -ne 0) {
    Remove-Item -LiteralPath $zipOut -Force -ErrorAction SilentlyContinue
    Fail "릴리스 zip 사후 검증 실패(빈 zip/해시 불일치/매니페스트 불일치) — zip 폐기"
}
# [봉인 운영] 최종 zip 수검 CLI 재수검 — 추출본이 앵커와 1비트도 다르면 zip 폐기.
$zipCli = Invoke-SealCli $zipOut $masterSeal "zip"
if ($zipCli -ne 0) {
    Remove-Item -LiteralPath $zipOut -Force -ErrorAction SilentlyContinue
    Fail "릴리스 zip 수검 CLI 실패(exit=$zipCli) — zip 폐기"
}
# 운영자 외부 앵커 — 배포 대상 PC 의 Release.ExpectedMasterSeal 에 이 값을 주입해야 무장 후보가 된다(미주입 = Unanchored 차단).
Set-Content -LiteralPath (Join-Path $releaseDir "master-seal.txt") -Value $masterSeal -Encoding ASCII
$zipSha = (Get-FileHash -Algorithm SHA256 -Path $zipOut).Hash
Set-Content -LiteralPath "$zipOut.sha256" -Value ("{0}  {1}" -f $zipSha, (Split-Path $zipOut -Leaf)) -Encoding UTF8

Write-Host "== 완료: $zipOut (SHA-256 $zipSha) ==" -ForegroundColor Green
exit 0
