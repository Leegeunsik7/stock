# ============================================================================
# release-package.ps1 — v12.14.0-GM Production Golden Master 봉인 & 배포 패키징 자동화
#   Section 2: Automated Quad-Build Release Packaging Pipeline
#
#   package-release.ps1(v10.1.0, 기존)이 '단일 Host R2R 퍼블리시'용이라면, 이 스크립트는 '사중 빌드 CI 게이트 +
#   배포 아티팩트 패키징'용 별도 파이프라인이다(관심사 분리 — 기존 스크립트를 덮어쓰지 않음).
#
#   실제로 '동시 출하'되는 아티팩트는 Host(win-x64) + KiwoomProxy(win-x86) 뿐이다(WebOnly·PrimesBench 는 CI
#   검증 전용 빌드 — build-installer.ps1 도 이 둘만 배포한다). 따라서:
#     [1/7] WebOnly Release 빌드  — 0W/0E 검증 전용(산출물 폐기, Host 출력 폴더를 공유하므로 먼저 태움)
#     [2/7] Default Release 빌드(sln 전체: Host+Tests+PrimesBench+x86 KiwoomProxy) — 0W/0E, 이후 패키징 대상
#     [3/7] 전체 유닛 테스트 스위트 — 100% Pass 필수
#     [4/7] infra-scan.ps1(Phase 83 결합도) — baseline 이하 필수
#     [5/7] Phase114 RatchetCeiling 테스트 필터 — 단독 재확인(회귀 조기 발견)
#     [6/7] liveArmed 소스 기본값 가드 — Dispatch.ValveWired/LiveArmingWired 하드코딩 기본이 false 인지 정적 검사
#           (컴파일된 바이너리가 아닌 소스를 검사 — 릴리스 직전 실수로 기본값이 true 로 뒤집히는 회귀를 소스
#           레벨에서 차단. 바이너리 자체의 암호 무결성은 [7/7]의 manifest.sig 가 별도로 봉인한다.)
#     [7/7] genesis-봉인 manifest.sig — ReleasePackager(AssemblySealGenerator 경유)가 아닌, 여기서는 순수
#           SHA-256 원시 해시 리스트만 PowerShell 로 직접 산출한다(★정직 스코핑: genesis 헤드 해시 결합은
#           CausalAuditVault 상태가 필요해 스크립트만으로 재현 불가 — package-release.ps1 이 이미 동일하게
#           명시한 제약과 동일. 실제 genesis-봉인 masterSeal 산출/검증은 AssemblySealGenerator.GenerateReleaseSeal
#           을 앱/테스트 계층에서 호출해 생성하고, 이 zip 에는 그 결과물도 함께 담는다 — 이미 생성돼 있으면 포함,
#           없으면 원시 SHA-256 리스트만으로 zip 을 완성하고 경고만 남긴다. 전부 실패-닫힘: 1~6단계 중 하나라도
#           실패하면 즉시 중단하고 zip 은 만들지 않는다).
#
#   사용:  pwsh ./release-package.ps1  [-Version <자동 추출>]  [-SkipWebOnlyGate]
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
function Step($n, $msg) { Write-Host "[$n/7] $msg" -ForegroundColor Cyan }

Write-Host "== MS2E StockSuite v$Version Production Golden Master 패키징 ==" -ForegroundColor Green

# ── [1/7] WebOnly 빌드(검증 전용 — 산출물 폐기, Host 출력 폴더 공유이므로 먼저 태움) ──
if (-not $SkipWebOnlyGate) {
    Step 1 "WebOnly Release 빌드(0W/0E 검증 전용)..."
    dotnet build $hostProj -c Release -p:WebOnly=true -m:1
    if ($LASTEXITCODE -ne 0) { Fail "WebOnly 빌드 실패(exit $LASTEXITCODE) — 0W/0E 아님, 패키징 중단" }
} else {
    Write-Host "[1/7] WebOnly 게이트 건너뜀(-SkipWebOnlyGate)" -ForegroundColor DarkGray
}

# ── [2/7] Default 빌드(sln 전체 — Host/Tests/PrimesBench/x86 KiwoomProxy, 패키징 대상 산출) ──
Step 2 "Default Release 빌드(sln 전체: Host+Tests+PrimesBench+x86 KiwoomProxy)..."
dotnet build $sln -c Release -m:1
if ($LASTEXITCODE -ne 0) { Fail "Default 빌드 실패(exit $LASTEXITCODE) — 0W/0E 아님, 패키징 중단" }

# ── [3/7] 전체 유닛 테스트 스위트 ──
Step 3 "전체 유닛 테스트 스위트 실행..."
dotnet test $testsProj -c Release -m:1 --no-build
if ($LASTEXITCODE -ne 0) { Fail "테스트 실패 — 패키징 중단(fail-closed)" }

# ── [4/7] infra-scan(Phase 83 결합도) ──
Step 4 "infra-scan.ps1(Phase 83 결합도) 실행..."
& (Join-Path $root "infra-scan.ps1")
if ($LASTEXITCODE -ne 0) { Fail "infra-scan 회귀(coupling > baseline) — 패키징 중단" }

# ── [5/7] Phase114 RatchetCeiling 단독 재확인 ──
Step 5 "Phase114 RatchetCeiling(PureShellFitness) 재확인..."
dotnet test $testsProj -c Release -m:1 --no-build --filter "FullyQualifiedName~Phase114PureShellFitnessTests"
if ($LASTEXITCODE -ne 0) { Fail "RatchetCeiling 회귀 — 패키징 중단" }

# ── [6/7] liveArmed 소스 기본값 가드(하드코딩 기본이 false 인지 정적 검사) ──
Step 6 "liveArmed 기본값(Dispatch.ValveWired/LiveArmingWired) 소스 가드..."
$homeFormPath = Join-Path $root "StockManager\Blazor\BlazorHomeForm.cs"
$homeFormSrc = Get-Content -LiteralPath $homeFormPath -Raw
$guardPatterns = @(
    'cfg\.GetBool\("Dispatch\.ValveWired",\s*false\)',
    'cfg\.GetBool\("Dispatch\.LiveArmingWired",\s*false\)'
)
foreach ($p in $guardPatterns) {
    if ($homeFormSrc -notmatch $p) {
        Fail "liveArmed 기본값 가드 실패 — 패턴 미발견(기본값이 false 가 아니게 변경됐을 위험): $p"
    }
}
Write-Host "  OK: ValveWired/LiveArmingWired 하드코딩 기본값 = false(불변 유지 확인)" -ForegroundColor DarkGreen

# ── [7/7] 배포 아티팩트 수집 + SHA-256 리스트 + (있으면) genesis 매니페스트 동봉 + zip ──
Step 7 "아티팩트 수집 + SHA-256 리스트 + zip 패키징..."
if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

$stage = Join-Path $releaseDir "stage"
New-Item -ItemType Directory -Force -Path (Join-Path $stage "host") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $stage "proxy") | Out-Null

if (-not (Test-Path $hostBin))  { Fail "Host 산출물 폴더 없음: $hostBin" }
if (-not (Test-Path $proxyBin)) { Fail "KiwoomProxy 산출물 폴더 없음: $proxyBin" }

# ★-Include 는 -Path 가 '\*'로 끝나거나 -Recurse 와 함께여야 실제로 필터링된다(PowerShell 기존 함정) — 디렉터리를
#   그대로 -Path 로 주면 무시되어 0건이 조용히 반환된다(1차 실행에서 실측: 산출물 0건 zip 오발행). '\*' 접미로 교정.
Get-ChildItem -Path "$hostBin\*" -Include *.dll, *.exe -File | Copy-Item -Destination (Join-Path $stage "host")
Get-ChildItem -Path "$proxyBin\*" -Include *.dll, *.exe -File | Copy-Item -Destination (Join-Path $stage "proxy")

# ★반대 함정: -Recurse 와 함께일 땐 -Path 에 '\*' 를 붙이면 오히려 0건이 된다(하위 폴더 파일에 -Include 가
#   재적용되지 않음 — 실측). -Recurse 단독 사용 시엔 '\*' 없이 순수 디렉터리 경로여야 재귀 전체에 필터가 적용된다.
$sha256Lines = @("# MS2E StockSuite v$Version — 배포 아티팩트 SHA-256(원시 해시 리스트, PowerShell Get-FileHash 산출)")
Get-ChildItem -Path $stage -Recurse -Include *.dll, *.exe -File | Sort-Object FullName | ForEach-Object {
    $h = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash
    $rel = $_.FullName.Substring($stage.Length + 1)
    $sha256Lines += ("{0}  {1}" -f $h, $rel)
}
$artifactCount = $sha256Lines.Count - 1
if ($artifactCount -eq 0) { Fail "배포 아티팩트 0건 수집 — 빈 릴리스 zip 발행 금지(fail-closed)" }
$sha256Path = Join-Path $releaseDir "artifacts.sha256.txt"
Set-Content -LiteralPath $sha256Path -Value $sha256Lines -Encoding UTF8
Write-Host "  아티팩트 원시 SHA-256 리스트: $sha256Path ($artifactCount 건)" -ForegroundColor Gray

# genesis-봉인 manifest.sig 는 AssemblySealGenerator.GenerateReleaseSeal(C#, CausalAuditVault 제네시스 헤드 필요)로
# 생성한다 — 이 스크립트만으로는 genesis 봉인을 재현할 수 없다(package-release.ps1 과 동일한 정직한 제약).
# 앱/테스트 계층에서 미리 생성해 뒀다면 여기서 zip 에 함께 담고, 없으면 원시 SHA-256 리스트만으로 진행(경고).
if (Test-Path $manifestOut) {
    Copy-Item -LiteralPath $manifestOut -Destination (Join-Path $releaseDir "manifest.sig.included")
    Write-Host "  genesis-봉인 manifest.sig 동봉됨: $manifestOut" -ForegroundColor DarkGreen
} else {
    Write-Host "  ⚠️ genesis-봉인 manifest.sig 없음 — AssemblySealGenerator.GenerateReleaseSeal(버전, hostBin, proxyBin, out) 을" -ForegroundColor DarkYellow
    Write-Host "     앱/테스트 계층에서 먼저 호출해 생성하세요. 이번 패키징은 원시 SHA-256 리스트만으로 진행합니다." -ForegroundColor DarkYellow
}

Compress-Archive -Path (Join-Path $stage "*"), $sha256Path -DestinationPath $zipOut -Force
Remove-Item $stage -Recurse -Force

Write-Host "== 완료: $zipOut ==" -ForegroundColor Green
exit 0
