# ============================================================================
# HostTuningAudit.ps1 — v10.1.0 실전 운용: 호스트 OS 커널 & BIOS 최적화 감사 (읽기 전용)
#
#   실자본 집행 세션 시작 전, HFT 지연에 치명적인 호스트 튜닝을 진단한다:
#     ① OS 고해상도 타이머(≤0.5ms) ② CPU C-States / Power Throttling 비활성
#     ③ NIC Interrupt Moderation 튜닝 ④ P-Core Affinity 정합
#
#   ★안전: 본 스크립트는 '읽기 전용 진단'이다 — 시스템 설정을 변경하지 않는다. 각 항목에 [OK]/[WARN]/[FAIL]과
#     권고 조치를 출력하며, 실제 튜닝 적용은 운용자가 관리자 권한으로 수동 집행한다(권고 명령을 그대로 실행).
#   사용:  pwsh ./HostTuningAudit.ps1   (관리자 PowerShell 권장 — 일부 쿼리는 권한 필요)
# ============================================================================
$ErrorActionPreference = "Continue"
$script:pass = 0; $script:warn = 0; $script:fail = 0

function Report($name, $status, $detail, $advice) {
    $color = "Gray"
    if ($status -eq "OK")   { $color = "Green";  $script:pass++ }
    if ($status -eq "WARN") { $color = "Yellow"; $script:warn++ }
    if ($status -eq "FAIL") { $color = "Red";    $script:fail++ }
    Write-Host ("  [{0,-4}] {1,-34} {2}" -f $status, $name, $detail) -ForegroundColor $color
    if (($status -ne "OK") -and $advice) { Write-Host ("         └ 권고: {0}" -f $advice) -ForegroundColor DarkGray }
}

Write-Host "== MS2E StockSuite v10.1.0 호스트 튜닝 감사 (읽기 전용) ==" -ForegroundColor Cyan

# ── ① OS 고해상도 타이머 ─────────────────────────────────────────────────────
try {
    Add-Type -Name TimerRes -Namespace MS2E -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("ntdll.dll")]
public static extern int NtQueryTimerResolution(out uint Min, out uint Max, out uint Cur);
'@ -ErrorAction Stop
    $mn = 0; $mx = 0; $cur = 0
    [void][MS2E.TimerRes]::NtQueryTimerResolution([ref]$mn, [ref]$mx, [ref]$cur)
    $curMs = [math]::Round($cur / 10000.0, 3)   # 100ns 단위 → ms
    if ($cur -le 5100)      { Report "Timer Resolution" "OK"   ("현재 {0} ms (≤0.5ms 튜닝됨)" -f $curMs) $null }
    elseif ($cur -le 10100) { Report "Timer Resolution" "WARN" ("현재 {0} ms (1ms — 0.5ms 권장)" -f $curMs) "앱이 timeBeginPeriod(1) 이상으로 상향(0.5ms는 NtSetTimerResolution)." }
    else                    { Report "Timer Resolution" "FAIL" ("현재 {0} ms (기본 15.6ms 추정 — 미튜닝)" -f $curMs) "고해상도 타이머 미요청 상태. 앱 기동 시 타이머 상향 필요(관제 📜 패널로 확인)." }
} catch {
    Report "Timer Resolution" "WARN" "측정 실패(NtQueryTimerResolution)" "관리자 권한으로 재실행."
}

# ── ② CPU 전력 계획 / C-States / Power Throttling ────────────────────────────
try {
    $scheme = (powercfg /getactivescheme) 2>$null
    if ($scheme -match "High performance|고성능|Ultimate") {
        Report "Power Plan" "OK" "고성능/Ultimate 전력 계획 활성" $null
    } else {
        Report "Power Plan" "WARN" ("현재 계획: {0}" -f ($scheme -replace '.*:\s*','')) "powercfg /setactive SCHEME_MIN (고성능) 적용 권장."
    }
} catch { Report "Power Plan" "WARN" "powercfg 쿼리 실패" "관리자 권한 필요." }

try {
    $ptOff = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling" -Name "PowerThrottlingOff" -ErrorAction Stop).PowerThrottlingOff
    if ($ptOff -eq 1) { Report "Power Throttling" "OK" "전역 비활성(PowerThrottlingOff=1)" $null }
    else              { Report "Power Throttling" "WARN" ("PowerThrottlingOff={0}" -f $ptOff) "레지스트리 PowerThrottlingOff=1 (핫패스 코어 스로틀 방지)." }
} catch {
    Report "Power Throttling" "WARN" "설정 미존재(기본 활성)" "HKLM\...\Power\PowerThrottling\PowerThrottlingOff=1 설정 권장."
}

# 프로세서 idle(C-State) 최소화 여부(활성 계획 idle disable 근사 진단)
try {
    $idle = (powercfg /q SCHEME_CURRENT SUB_PROCESSOR IDLEDISABLE) 2>$null | Out-String
    if ($idle -match "0x00000001") { Report "CPU Idle (C-States)" "OK" "IDLEDISABLE=1 (C-State 비활성)" $null }
    else { Report "CPU Idle (C-States)" "WARN" "C-State 활성 가능성" "powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR IDLEDISABLE 1; powercfg /setactive SCHEME_CURRENT" }
} catch { Report "CPU Idle (C-States)" "WARN" "idle 쿼리 실패" "BIOS에서 C-States 비활성 권장." }

# ── ③ NIC Interrupt Moderation ───────────────────────────────────────────────
try {
    $nics = Get-NetAdapterAdvancedProperty -DisplayName "*Interrupt Moderation*" -ErrorAction Stop
    if ($null -eq $nics -or $nics.Count -eq 0) {
        Report "NIC Interrupt Moderation" "WARN" "속성 미노출(어댑터/드라이버 상이)" "제조사 드라이버에서 Interrupt Moderation 확인."
    } else {
        $on = $nics | Where-Object { $_.DisplayValue -match "Enabled|사용" }
        if ($on) { Report "NIC Interrupt Moderation" "WARN" ("{0}개 어댑터 Moderation 활성" -f $on.Count) "저지연 경로 NIC는 Disabled 권장(지연 균일화)." }
        else     { Report "NIC Interrupt Moderation" "OK" "대상 어댑터 Moderation 비활성" $null }
    }
} catch { Report "NIC Interrupt Moderation" "WARN" "Get-NetAdapterAdvancedProperty 실패" "관리자 권한/드라이버 확인." }

# ── ④ P-Core / 논리 프로세서 정합(정보성) ────────────────────────────────────
try {
    $cpu = Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1
    $logical = ($cpu.NumberOfLogicalProcessors)
    $cores   = ($cpu.NumberOfCores)
    Report "CPU Topology" "OK" ("{0} 코어 / {1} 논리프로세서 — {2}" -f $cores, $logical, $cpu.Name) $null
    Write-Host "         └ 참고: P-Core 물리 핀은 앱의 CpuAffinityPinningGrid가 런타임 집행(관제 🖥️ 패널 P-CORE PINNED 확인)." -ForegroundColor DarkGray
} catch { Report "CPU Topology" "WARN" "Win32_Processor 쿼리 실패" $null }

# ── 요약 ─────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host ("== 요약: OK {0} · WARN {1} · FAIL {2} ==" -f $script:pass, $script:warn, $script:fail) -ForegroundColor Cyan
if ($script:fail -gt 0) {
    Write-Host "  ❌ FAIL 항목이 있습니다 — 튜닝 적용 후 재실행하세요. 미튜닝 시 사전 점화 서명이 거부됩니다." -ForegroundColor Red
    exit 2
} elseif ($script:warn -gt 0) {
    Write-Host "  ⚠️ WARN 항목을 권고대로 점검하세요(집행은 가능하나 지연 균일성 저하 가능)." -ForegroundColor Yellow
    exit 1
} else {
    Write-Host "  ✅ 전 항목 튜닝 완료 — 카나리 스테이징 준비." -ForegroundColor Green
    exit 0
}
