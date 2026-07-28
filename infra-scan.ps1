# ============================================================================
# infra-scan.ps1 — Phase 83 인프라 결합도 스캐너 & 래칫(Infrastructure Liberation)
#
#   코어(StockManager\Common)가 콘크리트 인프라(SQLite 설정소스 db.GetSetting·System.IO 파일시스템)를 '직결'
#   참조하는 결합 수를 센다. Ports & Adapters(ISystemConfigPort/IFileSystemPort) 로 해방할수록 이 수가 줄어든다.
#   포트/어댑터/파사드(합성 루트가 배선하는 인프라 경계)는 결합에서 제외한다.
#
#   사용:
#     pwsh infra-scan.ps1              # 현재 결합도 출력 + baseline 대비 검사(초과 시 exit 1)
#     pwsh infra-scan.ps1 -ratchet     # 감소 시 infra-baseline.txt 를 하향 갱신(래칫·비가역 조임)
#
#   ★래칫 원리: 결합도는 '증가 불가'(회귀 차단). 감소는 baseline 을 낮춰 되돌림을 막는다. exit 0 = 청정(회귀 없음).
# ============================================================================
param([switch]$ratchet)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$core = Join-Path $root "StockManager\Common"
$baselineFile = Join-Path $root "infra-baseline.txt"

# 결합 패턴 — 코어가 인프라를 직결 참조(Slice 2 범위: Config + FileSystem).
$patterns = @(
    '\.GetSetting\(',                                  # SQLite 설정 직결(→ ISystemConfigPort)
    'Environment\.GetEnvironmentVariable',             # 환경변수 직결(→ ISystemConfigPort)
    '\bFile\.(ReadAll|WriteAll|Exists|Open|Delete|Copy|Move|AppendAll)',   # 파일 IO 직결(→ IFileSystemPort)
    '\bDirectory\.(Create|Exists|Delete|GetFiles|GetDirectories)',          # 디렉터리 직결(→ IFileSystemPort)
    'new\s+(StreamReader|StreamWriter|FileStream)\b',  # 스트림 직결(→ IFileSystemPort)
    '\bDriveInfo\b'                                    # 드라이브 직결(→ IFileSystemPort)
)
# 인프라 경계(어댑터/파사드) — 인프라 접촉이 '허용'되는 파일. 결합에서 제외.
$exclude = @('InfraLiberationSlice2.cs', 'RuntimeConfig.cs')

$total = 0
$breakdown = @{}
Get-ChildItem -Path $core -Filter *.cs -File | Where-Object { $exclude -notcontains $_.Name } | ForEach-Object {
    $file = $_.Name
    $lines = Get-Content -LiteralPath $_.FullName
    $fileCount = 0
    foreach ($p in $patterns) {
        $m = ($lines | Select-String -Pattern $p -AllMatches)
        if ($m) { $fileCount += ($m.Matches | Measure-Object).Count }
    }
    if ($fileCount -gt 0) { $breakdown[$file] = $fileCount; $total += $fileCount }
}

Write-Host "=== Phase 83 infra coupling (core direct Config/FileSystem refs) ==="
$breakdown.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 12 | ForEach-Object { Write-Host ("  {0,-40} {1}" -f $_.Key, $_.Value) }
Write-Host "TOTAL coupling: $total"

if (-not (Test-Path $baselineFile)) {
    Set-Content -LiteralPath $baselineFile -Value $total
    Write-Host "baseline created: $total (exit 0)"
    exit 0
}
$baseline = [int]((Get-Content -LiteralPath $baselineFile | Select-Object -First 1).Trim())
if ($total -gt $baseline) {
    Write-Host "REGRESSION: coupling $total > baseline $baseline — 새 인프라 직결 결합 도입 차단(exit 1)"
    exit 1
}
if ($ratchet -and $total -lt $baseline) {
    Set-Content -LiteralPath $baselineFile -Value $total
    Write-Host "RATCHET: baseline $baseline -> $total (하향 조임)"
}
Write-Host "OK: coupling $total <= baseline $baseline (exit 0)"
exit 0
