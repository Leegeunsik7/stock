# ============================================================================
# crash-triage-loop.ps1 — [크래시 트리아지] 전체 테스트 스위트 N회 연속 실행 + 중단 원인 자동 분류
#
#  회차마다: 시작 시각 · 여유 커밋(GB) 기록 → dotnet test(Release · --no-build · --blame) → 결과 판정.
#  중단(테스트 호스트 종료) 시 원인 분류(교차 증거):
#    Internal   — Windows 이벤트 '.NET Runtime 1026' / 'Application Error 1000'(testhost) 또는 종료 원인 기록기
#                 (%TEMP%\ms2e-testhost\<pid>.log)의 UnhandledException — 프로세스 내부 예외/AV/FailFast(스택 포함)
#    LowMemory  — 시스템 이벤트 2004(가상 메모리 부족) 동반 — 호스트 커밋 고갈(외부 환경)
#    Silent     — 위 기록 전무 — 외부 TerminateProcess(다른 프로세스/세션의 강제 종료) 추정
#  출력: <OutDir>\summary.csv · 콘솔 요약. 중단 1건 이상이면 exit 1.
#  사용: powershell -File tools\crash-triage-loop.ps1 [-Runs 50] [-OutDir <dir>] [-StopOnAbort]
# ============================================================================
param(
    [int]$Runs = 50,
    [string]$OutDir = (Join-Path $env:TEMP ("ms2e-crash-triage-" + (Get-Date -Format "yyyyMMdd-HHmmss"))),
    [switch]$StopOnAbort
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$testProj = Join-Path $root "StockManager.Tests\StockManager.Tests.csproj"
$sentinelDir = Join-Path $env:TEMP "ms2e-testhost"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$rows = New-Object System.Collections.Generic.List[object]

function FreeCommitGB { [math]::Round((Get-CimInstance Win32_OperatingSystem).FreeVirtualMemory / 1MB, 1) }

for ($i = 1; $i -le $Runs; $i++) {
    $start = Get-Date
    $fc = FreeCommitGB
    $log = Join-Path $OutDir ("run{0:D3}.txt" -f $i)
    # PS 5.1 의 *> 는 네이티브 출력을 콘솔 코드페이지로 해석 후 UTF-16 으로 기록(문자 손상) → cmd 원시 리다이렉션 + 영어 CLI 출력
    $res = Join-Path $OutDir ("r{0:D3}" -f $i)
    $env:DOTNET_CLI_UI_LANGUAGE = "en"; $env:VSLANG = "1033"
    & cmd /c "dotnet test `"$testProj`" -c Release -m:1 --no-build --blame --results-directory `"$res`" > `"$log`" 2>&1"
    $text = [System.IO.File]::ReadAllText($log)
    $aborted = $text -match "aborted|crashed|중단"
    $passLine = ([regex]::Match($text, "(Passed|Failed|통과|실패)!\s*-\s*(Failed|실패):\s*\d+[^\r\n]*")).Value
    $failed = [regex]::Match($passLine, "(Failed|실패):\s*(\d+)").Groups[2].Value
    if ($failed -eq "" -and -not $aborted) { $failed = "?" }
    $cause = ""; $detail = ""
    if ($aborted) {
        $rt = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $start; Id = 1000, 1026 } -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'testhost' })
        $lm = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; StartTime = $start.AddMinutes(-2); Id = 2004 } -ErrorAction SilentlyContinue)
        $sen = @(Get-ChildItem -LiteralPath $sentinelDir -Filter *.log -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $start } |
                 Where-Object { (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8) -match 'UnhandledException' })
        if ($rt.Count -gt 0 -or $sen.Count -gt 0) {
            $cause = "Internal"
            $detail = if ($rt.Count -gt 0) { (($rt[0].Message -split "`n") | Select-Object -Skip 4 -First 3) -join " | " } else { $sen[0].Name }
        } elseif ($lm.Count -gt 0) { $cause = "LowMemory"; $detail = (($lm[0].Message -split "`n") | Select-Object -First 1) }
        else { $cause = "Silent"; $detail = "이벤트 1000/1026/2004 · 기록기 로그 전무 — 외부 강제 종료 추정" }
    }
    $row = [pscustomobject]@{ Run = $i; Start = $start.ToString("HH:mm:ss"); FreeCommitGB = $fc; Aborted = $aborted; Failed = $failed; Cause = $cause; Detail = $detail; Summary = $passLine }
    $rows.Add($row)
    Write-Host ("[{0}/{1}] {2} commit={3}GB {4} {5}" -f $i, $Runs, $(if ($aborted) { "ABORT" } elseif ($failed -ne "0") { "FAIL" } else { "PASS" }), $fc, $cause, $passLine)
    if ($aborted -and $StopOnAbort) { break }
}
$rows | Export-Csv -LiteralPath (Join-Path $OutDir "summary.csv") -NoTypeInformation -Encoding UTF8
$ab = @($rows | Where-Object { $_.Aborted }).Count
$fl = @($rows | Where-Object { -not $_.Aborted -and $_.Failed -ne "0" }).Count
Write-Host ("== 실행 {0}회 · 중단 {1} · 실패 포함 {2} · 결과 {3}" -f $rows.Count, $ab, $fl, (Join-Path $OutDir "summary.csv"))
if ($ab -gt 0 -or $fl -gt 0) { exit 1 }
exit 0
