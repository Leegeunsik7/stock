# ============================================================================
# package-release.ps1 — v6.2.0 프로덕션 릴리즈 패키징 & 배포 서명
#
#   1) Release + ReadyToRun(R2R) 프로덕션 퍼블리시
#   2) Native AOT 호환성 정직 진단(WinForms/Blazor Hybrid 는 AOT 미지원 — 사실 그대로 보고)
#   3) 아티팩트 수집 + SHA-256 원시 해시 리스트
#   4) 제네시스-봉인 마스터 매니페스트(v6.2.0_release_manifest.json)는 ReleasePackager(C#)가 생성
#      — CausalAuditVault 제네시스 헤드 해시와 결합해 배포 무결성을 서명(스크립트만으로는 genesis 봉인 불가).
#
#   사용:  pwsh ./package-release.ps1  [-Version 6.2.0]  [-SkipPublish]
# ============================================================================
param(
    [string]$Version = "10.1.0",   # ★stale-ref 교정: 현행 릴리스 버전 추종(v10.1.0 Final Release Packaging)
    [switch]$SkipPublish
)
$ErrorActionPreference = "Stop"
$root    = $PSScriptRoot
$proj    = Join-Path $root "StockManager\StockManager.csproj"
$outDir  = Join-Path $root "publish\v$Version"
$rid     = "win-x64"

Write-Host "== MS2E StockSuite v$Version 릴리즈 패키징 ==" -ForegroundColor Cyan

# ── 1) R2R 프로덕션 퍼블리시 ────────────────────────────────────────────────
if (-not $SkipPublish) {
    Write-Host "[1/4] Release + ReadyToRun 퍼블리시 ($rid)..." -ForegroundColor Yellow
    dotnet publish $proj -c Release -r $rid --self-contained false `
        -p:PublishReadyToRun=true -p:TreatWarningsAsErrors=true `
        -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "퍼블리시 실패(exit $LASTEXITCODE)" }
} else {
    Write-Host "[1/4] 퍼블리시 건너뜀(-SkipPublish)" -ForegroundColor DarkGray
}

# ── 2) Native AOT 호환성 정직 진단 ──────────────────────────────────────────
Write-Host "[2/4] Native AOT 호환성 진단..." -ForegroundColor Yellow
Write-Host "  ⚠️ 정직 고지: 이 어셈블리는 WinForms + Blazor Hybrid(UseWindowsForms=true) 로," -ForegroundColor DarkYellow
Write-Host "     .NET 8 Native AOT 와 '호환되지 않습니다'(WinForms 는 AOT 미지원)." -ForegroundColor DarkYellow
Write-Host "     → 프로덕션 최적화는 ReadyToRun(R2R)로 수행합니다(위 1단계). AOT 는 헤드리스 코어" -ForegroundColor DarkYellow
Write-Host "       분리 시에만 후보이며, 현 구성에선 R2R 이 정확한 선택입니다." -ForegroundColor DarkYellow

# ── 3) 아티팩트 수집 + SHA-256 원시 리스트 ─────────────────────────────────
Write-Host "[3/4] 아티팩트 SHA-256 수집..." -ForegroundColor Yellow
if (Test-Path $outDir) {
    $artifacts = Get-ChildItem -Path $outDir -Include *.dll, *.exe -Recurse -File | Sort-Object Name
    foreach ($a in $artifacts) {
        $h = (Get-FileHash -Algorithm SHA256 -Path $a.FullName).Hash
        Write-Host ("    {0,-40} {1}" -f $a.Name, $h)
    }
    Write-Host "  아티팩트 수: $($artifacts.Count)"
} else {
    Write-Host "  (퍼블리시 산출물 없음 — 먼저 퍼블리시하세요)" -ForegroundColor DarkGray
}

# ── 4) 제네시스-봉인 마스터 매니페스트 ─────────────────────────────────────
Write-Host "[4/4] 제네시스-봉인 매니페스트(v${Version}_release_manifest.json)" -ForegroundColor Yellow
Write-Host "  → ReleasePackager.WriteManifest(version, artifactPaths, outPath) 호출로 생성됩니다." -ForegroundColor Gray
Write-Host "     masterSeal = SHA256(CausalAuditVault 제네시스 헤드 ‖ 정렬된 아티팩트 SHA-256)." -ForegroundColor Gray
Write-Host "     (검증: ReleasePackager.VerifyArtifacts(artifactPaths, masterSeal))" -ForegroundColor Gray

Write-Host "== 완료 ==" -ForegroundColor Cyan
