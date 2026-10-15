# ============================================================================
# release-package.ps1 — v12.18.10-GM Production Golden Master 봉인 & 배포 패키징 자동화 ([Phase 4] 교정판)
#
#   실제 '동시 출하' 아티팩트는 Host(win-x64) + KiwoomProxy(win-x86) 뿐이다(WebOnly·PrimesBench·KiwoomComShim 은 CI 검증 전용).
#     [0/10] [릴리스 CI] 소스 무결성 사전 검사 — 태그 v<ver>-GM 존재 · 태그↔HEAD 코드 이탈 0 · 미커밋 코드 0(문서·release/·.claude/ 제외).
#            출하 바이너리가 '태그된 소스' 그대로 빌드됨을 빌드 전에 보증(실증: v12.14.1-GM 태그는 IKiwoomApi.cs 미포함으로 x86 프록시
#            빌드 불가 · 작업 트리 빌드로 출하 → 태그와 출하물 불일치). 실패 = 즉시 중단(빌드 0).
#     [1/8] WebOnly Release 빌드 — 0W/0E 검증 전용(산출물 폐기 · Host 출력 폴더를 공유하므로 먼저 태움)
#     [2/8] Default Release 빌드(sln: Host+Tests+PrimesBench+x86 KiwoomProxy+KiwoomComShim) — 0W/0E, 패키징 대상
#           + [오중 빌드] KiwoomComShim(#if KIWOOM_COM 인터롭 형상 쉼) 산출물이 이번 빌드에서 갱신됐는지 확인(미갱신 = 중단)
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
#     [9/10] [방향 A] 출하 zip 무추출 수검(--verify-zip) — 항목 스트림 SHA-256 · 내장 manifest.sig ↔ master-seal.txt ·
#            실측 해시 재계산 봉인 ↔ 앵커 · 미등재/누락/경로 이탈 0. 실패 = zip·.sha256·master-seal.txt 삭제 후 중단.
#            + [v12.16.1-GM] 출하 zip 안 호스트 DLL 이중 물리 키 기본값 IL 직접 판독(--verify-live-armed · 추출·로드·실행 0)
#              ldstr "Dispatch.ValveWired|LiveArmingWired" → ldc.i4.0 → GetBool 아니면(또는 조회 지점 0) = 전량 폐기 후 중단.
#     [10/10] [방향 B] 다중 저장소 핸드셰이크 — RELEASE_MANIFEST.json(태그·소스 커밋·manifest.sig·masterSeal·아티팩트·zip·쉼 해시·
#            liveArmed=false·인터락 + 피어 QuantumVault HEAD/봉인 매니페스트 해시 '읽기 전용')을 원자 기록 후 독립 재수검.
#            불일치 = 모든 출하 산출물 삭제 후 중단. 피어 저장소에는 쓰지 않는다.
#            + [v12.18.0-GM · 방향 A] 30대 인터락 봉인 대조(--verify-interlocks) — zip 안 호스트 DLL 의 ArmingInterlockSource 정의(이름 · 값 ·
#              비트마스크)를 MMF 뷰에서 무추출 판독 → 기록된 매니페스트 interlocks · master-seal.txt · 기대 개수($ExpectedInterlocks) 와 대조.
#              불일치 = 출하 산출물 전량 폐기. 새 인터락 비트를 추가하면 $ExpectedInterlocks 도 갱신해야 출하된다(의도된 명세 게이트).
#            + [v12.18.1-GM · 방향 A] 강화 전용 단조성 행렬 대조(--verify-tighten) — zip 안 호스트 DLL 의 TightenOnlyUiSettingWriter
#              키 표 · 규칙 비트 · 폴백/하한/상한 · IsTightening 비교 방향(==1 · ≥ · ≤)을 무추출 판독 → 매니페스트 tightenRules(태그 소스 판독) ·
#              가드 고정 명세 · master-seal.txt · 기대 키 수($ExpectedTightenKeys) 와 대조. [9/10] --verify-live-armed 도 같은 수를 고정
#              (가드의 '경로 전체 부재 = 통과'를 출하 게이트에서 닫음). 불일치 · 누락 = 출하 산출물 전량 폐기.
#            + [v12.18.2-GM · 방향 A] 감사 원장 결선 강제(--require-audit-ledger · 두 CLI 모두) — 기입기 TryWrite 의 ITightenAuditSink.Append
#              선기록(Judge < Append < WriteStored · 직후 조건 분기) · 진입점 감사 인자 ≠ null · 형식 부재(= 규칙 ⑪ 조용한 생략) = 출하 산출물 전량 폐기.
#            + [v12.18.3-GM · 방향 A] 관제 배지 결선 강제(--require-audit-telemetry · --verify-tighten) — 원장 Snapshot() = 순수 seqlock 판독자
#              (잠금/원장 메서드/_gate·_hasher·_head/기록/할당 0) · PublishSnapshot 시퀀스 증가 2 · 허브 PumpCore/어댑터 TryGetAuditLedger 의 Snapshot 호출 1 ·
#              호스트 AttachAuditLedger 1. 배지 결선 누락 · 잠금 간섭 = 출하 산출물 전량 폐기.
#            + [v12.18.4-GM · 방향 A] IPC 패브릭 무슬립 위상 강제(--require-ipc-fabric · --verify-tighten) — MmfIpcFabric 핫 폐포(TryPublish/TryConsume/
#              Beat/CheckPeerAlive 에서 닿는 같은 형식 메서드 전부) 차단 대기 0(Sleep/Yield/SpinWait/잠금/대기 핸들/Task 대기) · 형식 전체 Thread.Sleep/Yield ·
#              Task.Delay · SpinWait.SpinUntil 0 · 생성자 기본 시계 = Stopwatch.GetTimestamp. 위반 = 출하 산출물 전량 폐기.
#            + [v12.18.6-GM] 호스트 펌프 지터 결선 강제(--require-pump-jitter · [9/10] --verify-live-armed) — ⑫ BlazorHomeForm 트리의
#              DashboardSnapshotHub.AttachPumpJitter ≥1 · 조립 밖 결선/AttachJitter 직접 결선 0 · 진입점 본문 AttachJitter 정확히 1. 위반 = 산출물 전량 폐기.
#            + [v12.18.7-GM] 유령 취소 무장 가드 강제(--require-cancel-arming · [9/10] --verify-live-armed) — ⑬ BrokerOpenOrderReconciler 의
#              CancelRowsLocked 호출마다 앞선 CancelArmedNow 표본 + 조건 분기 · 대사기 밖 송신 0 · 호스트 AttachCancelArming ≥1. 위반 = 산출물 전량 폐기.
#   [v12.18.4-GM · 방향 B] 피어 철회 드리프트 방어(PeerRevocationDriftSafeguard · --peer-drift) — [0/10] 이 피어 '브랜치'까지 고정(분리 HEAD = 중단)하고
#     [7/10] zip 생성 직전 · [10/10] 동기화 전 · 완료 직전에 브랜치 · HEAD · 봉인 매니페스트 SHA-256 · 미커밋 코드 0 을 재판독.
#     1비트라도 이동(브랜치 전환 · 1커밋 전진 · 재봉인) = 스테이징 · 출하 산출물 전량 폐기 후 중단(-NoPeer = 생략).
#   전부 실패-닫힘: 어느 단계든 실패하면 즉시 중단, 불완전 zip 은 남기지 않는다.
#
#   사용:  powershell -File ./release-package.ps1  [-Version <자동: csproj>]  [-SkipWebOnlyGate]  [-PeerRepo <..\QuantumVault>]  [-NoPeer]
# ============================================================================
param(
    [string]$Version = "",
    [switch]$SkipWebOnlyGate,
    [string]$PeerRepo = "",
    [switch]$NoPeer
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
$ExpectedInterlocks = 30   # [v12.18.0-GM] ArmingInterlockSource 0~28 + StartupSanity(63) — 출하 명세(바이너리 · 매니페스트와 불일치 = 폐기)
$ExpectedTightenKeys = 6   # [v12.18.1-GM] 강화 전용 키(SignalGateEnabled · AdaptiveThreshold · ConfidenceThresholdBp · AdaptiveMaxBp · ChurnSoftPerMin · ChurnHardPerMin)

function Fail($msg) { Write-Host "✗ $msg" -ForegroundColor Red; exit 1 }
function Step($n, $msg) { Write-Host "[$n/10] $msg" -ForegroundColor Cyan }
$hostExe = Join-Path $hostBin "MS2E.StockSuite.exe"
$shimDll = Join-Path $root "tools\KiwoomComShim\bin\Release\net8.0-windows\MS2E.KiwoomComShim.dll"
if ([string]::IsNullOrWhiteSpace($PeerRepo)) { $PeerRepo = Join-Path (Split-Path $root -Parent) "QuantumVault" }

# [릴리스 CI] 출하 산출물 전량 폐기(부분 산출 잔존 금지)
function Remove-ReleaseOutputs {
    foreach ($f in @($zipOut, "$zipOut.sha256", (Join-Path $releaseDir "master-seal.txt"), (Join-Path $releaseDir "RELEASE_MANIFEST.json"))) {
        Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
    }
}

# [v12.18.4-GM · 방향 B] 피어 철회 드리프트 방어 — [0/10] 고정(브랜치 · HEAD · 매니페스트 SHA-256)과 현재 피어 대조(호스트 CLI · 읽기 전용).
#   이동 = 스테이징 폴더 · 출하 산출물 전량 폐기 후 중단. 호출 시점: [7/10] zip 직전 · [10/10] 동기화 전 · 완료 직전.
function Assert-PeerPinned([string]$when) {
    if ($NoPeer) { return }
    $driftExit = Invoke-HostCli @('--peer-drift', ('"' + $PeerRepo + '"'), '--pin-peer-branch', $peerPinBranch, '--pin-peer-head', $peerPinHead, '--pin-peer-manifest', $peerPinSha) "peer-drift-$when"
    if ($driftExit -ne 0) {
        if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
        Remove-ReleaseOutputs; Fail "피어 드리프트($when · exit=$driftExit) — [0/10] 고정 브랜치/HEAD/봉인 매니페스트 이탈 · 출하 산출물 전량 폐기"
    }
    Write-Host "  OK: 피어 고정 유지($when) — 브랜치 $peerPinBranch · HEAD $($peerPinHead.Substring(0, 10))" -ForegroundColor DarkGreen
}

# [릴리스 CI] 호스트 콜드 CLI(--verify-zip · --sync-release-manifest · --release-handshake) — 보고서는 UTF-8 파일(--report)로 판독.
function Invoke-HostCli([string[]]$cliArgs, [string]$label) {
    if (-not (Test-Path -LiteralPath $hostExe)) { Fail "CLI 실행 파일 없음: $hostExe" }
    $report = Join-Path $releaseDir "cli-$label.txt"
    $proc = Start-Process -FilePath $hostExe -ArgumentList ($cliArgs + @('--report', ('"' + $report + '"'))) -Wait -PassThru -NoNewWindow
    if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Encoding UTF8 | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray } }
    return $proc.ExitCode
}

# [릴리스 CI] 출하 바이너리에 영향 없는 경로(문서·산출물·도구 캐시) — 호스트 GitRepoProbe.IsCodePath 와 동일 규칙.
function Test-CodePath([string]$p) {
    $q = $p.Trim().Trim('"').Replace('\', '/')
    if ($q.Length -eq 0) { return $false }
    foreach ($pre in @('.claude/', 'release/', 'docs/', 'graphify-out/')) { if ($q.StartsWith($pre, [StringComparison]::OrdinalIgnoreCase)) { return $false } }
    return -not $q.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)
}

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

# ── [0/10] 소스 무결성 사전 검사(빌드 전 · fail-closed) ──
Step 0 "소스 무결성 사전 검사(태그 v$Version-GM · 태그↔HEAD 코드 이탈 · 미커밋 코드)..."
$tag = "v$Version-GM"
$headCommit = (& git -C $root rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $headCommit) { Fail "git HEAD 판독 실패 — 소스 미확인 출하 금지" }
$tagCommit = (& git -C $root rev-list -n 1 $tag 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $tagCommit) { Fail "태그 $tag 없음 — 태그되지 않은 소스 출하 금지" }
if ($headCommit.Trim() -ne $tagCommit.Trim()) {
    $drift = @(& git -C $root diff --name-only $tagCommit.Trim() $headCommit.Trim() | Where-Object { Test-CodePath $_ })
    if ($drift.Count -gt 0) { Fail "태그 $tag 이후 코드 변경 $($drift.Count)건(예: $($drift[0])) — 태그 재지정 또는 HEAD 를 태그로" }
}
$dirty = @(& git -C $root -c core.quotepath=false status --porcelain --untracked-files=all | ForEach-Object { if ($_.Length -ge 4) { $x = $_.Substring(3); if ($x.Contains(' -> ')) { $x = $x.Substring($x.IndexOf(' -> ') + 4) }; $x } } | Where-Object { Test-CodePath $_ })
if ($dirty.Count -gt 0) { Fail "미커밋 코드 $($dirty.Count)건(예: $($dirty[0])) — 작업 트리 빌드는 태그 소스와 다름(출하 금지)" }
Write-Host "  OK: $tag = HEAD 코드 · 미커밋 코드 0" -ForegroundColor DarkGreen
# [다중 저장소 봉인 감사] 피어 사전 점검(빌드 전 fail-fast · 읽기 전용) — [10/10] 핸드셰이크가 거부할 피어 상태(저장소/봉인 매니페스트 부재 ·
#   미커밋 코드)를 수십 분 빌드 전에 같은 판정 규칙(Test-CodePath)으로 차단한다. 피어에는 아무것도 쓰지 않는다.
if (-not $NoPeer) {
    if (-not (Test-Path -LiteralPath (Join-Path $PeerRepo ".git"))) { Fail "피어 저장소 없음: $PeerRepo (-NoPeer 로 명시 생략 가능)" }
    $peerManifest = Join-Path $PeerRepo "release\offline-bundle\RELEASE_MANIFEST.json"
    if (-not (Test-Path -LiteralPath $peerManifest)) { Fail "피어 봉인 매니페스트 없음: $peerManifest" }
    $peerDirty = @(& git -C $PeerRepo --no-optional-locks -c core.quotepath=false status --porcelain --untracked-files=all | ForEach-Object { if ($_.Length -ge 4) { $x = $_.Substring(3); if ($x.Contains(' -> ')) { $x = $x.Substring($x.IndexOf(' -> ') + 4) }; $x } } | Where-Object { Test-CodePath $_ })
    if ($peerDirty.Count -gt 0) { Fail "피어 미커밋 코드 $($peerDirty.Count)건(예: $($peerDirty[0])) — [10/10] 핸드셰이크 거부 예정, 빌드 전 중단" }
    # [방향 B] 피어 고정 — 이 실행이 짝지을 피어(HEAD · 봉인 매니페스트 해시)를 빌드 전에 확정. [10/10] 동기화 · 핸드셰이크가 대조(이동 = 산출물 폐기).
    $peerPinHead = (& git -C $PeerRepo rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $peerPinHead) { Fail "피어 HEAD 판독 실패: $PeerRepo" }
    $peerPinHead = "$peerPinHead".Trim()
    # [v12.18.4-GM · 방향 B] 브랜치도 고정 — 분리 HEAD = 출하가 묶을 계보 없음(중단). 이후 Assert-PeerPinned 가 대조.
    $peerPinBranch = (& git -C $PeerRepo --no-optional-locks symbolic-ref --short -q HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $peerPinBranch) { Fail "피어 분리 HEAD(브랜치 없음) — 교차 봉인할 브랜치를 체크아웃한 뒤 재실행: $PeerRepo" }
    $peerPinBranch = "$peerPinBranch".Trim()
    $peerPinSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $peerManifest).Hash
    # [v12.18.5-GM] 피어 정돈 완료 하드 게이트(--peer-readiness · PeerReadinessProbe) — 미커밋 코드 · 키 원문 의심 파일 · 매니페스트 누락/무효 ·
    #   ★낡은 봉인(sealedAtUtc < 피어 HEAD 커밋 시각 — 1초라도) = 패키징 착수 거부(exit 1 · 산출물 폐기/생성 0).
    #   (수정 전: 위 사전 점검은 '깨끗함 · 매니페스트 존재'만 봐서, 현재 HEAD 이전에 봉인된 피어 매니페스트로도 교차 봉인이 진행됐다.)
    #   판정기는 호스트 CLI 라 깨끗한 작업 트리에선 아직 없다 → 호스트만 먼저 컴파일(-warnaserror · 출하 단계 아님 — [2/10] 이 sln 전체를 다시 빌드).
    #   판정 직후 드리프트 재대조 — Ready 판정이 '방금 고정한' 브랜치/HEAD/매니페스트에 대한 것임을 보증(판정 중 피어 이동 = 중단).
    if (-not (Test-Path -LiteralPath $hostExe)) {
        dotnet build $hostProj -c Release -m:1 -warnaserror | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail "피어 판정기(호스트 CLI) 빌드 실패(exit $LASTEXITCODE)" }
    }
    New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
    $readyExit = Invoke-HostCli @('--peer-readiness', ('"' + $PeerRepo + '"')) "peer-readiness"
    if ($readyExit -ne 0) { Fail "피어 NotReady(exit=$readyExit · 낡은 봉인 ManifestStale 포함) — 피어 재봉인/정돈 뒤 재실행(-NoPeer 로 명시 생략 가능)" }
    $pinExit = Invoke-HostCli @('--peer-drift', ('"' + $PeerRepo + '"'), '--pin-peer-branch', $peerPinBranch, '--pin-peer-head', $peerPinHead, '--pin-peer-manifest', $peerPinSha) "peer-drift-readiness"
    if ($pinExit -ne 0) { Fail "피어가 Ready 판정 중 이동(exit=$pinExit) — 재실행" }
    Write-Host "  OK: 피어 $PeerRepo Ready(미커밋 코드 0 · 키 의심 0 · HEAD 이후 재봉인) · 고정 $peerPinBranch@$($peerPinHead.Substring(0, 10))" -ForegroundColor DarkGreen
}
# [출하 롤백 가드 · v12.15.0-GM] 사전 검사 통과 = 이번 실행이 같은 버전의 기존 출하 산출물을 대체한다 → 빌드 전에 전량 폐기.
#   이후 어느 단계([1]~[10])에서 실패해도 release\ 에 이 버전의 zip · .sha256 · master-seal.txt · RELEASE_MANIFEST.json 이 남지 않는다.
#   (수정 전: [1]~[6] 실패 시 직전 실행이 남긴 같은 이름 zip 이 '통과한 출하물'처럼 잔존 — 지금 소스와 다른 봉인.)
#   빌드는 -warnaserror(MSBuild 경고 포함 — 프로젝트 TreatWarningsAsErrors 는 컴파일러 경고만) = 게이트와 같은 판정.
Remove-ReleaseOutputs
$buildStart = Get-Date

# ── [1/8] WebOnly 빌드(검증 전용 — 산출물 폐기, Host 출력 폴더 공유이므로 먼저 태움) ──
if (-not $SkipWebOnlyGate) {
    Step 1 "WebOnly Release 빌드(0W/0E 검증 전용)..."
    dotnet build $hostProj -c Release -p:WebOnly=true -m:1 -warnaserror
    if ($LASTEXITCODE -ne 0) { Fail "WebOnly 빌드 실패(exit $LASTEXITCODE) — 0W/0E 아님, 패키징 중단" }
} else {
    Write-Host "[1/8] WebOnly 게이트 건너뜀(-SkipWebOnlyGate)" -ForegroundColor DarkGray
}

# ── [2/8] Default 빌드(sln 전체 — Host/Tests/PrimesBench/x86 KiwoomProxy, 패키징 대상 산출) ──
Step 2 "Default Release 빌드(sln 전체: Host+Tests+PrimesBench+x86 KiwoomProxy+KiwoomComShim)..."
dotnet build $sln -c Release -m:1 -warnaserror
if ($LASTEXITCODE -ne 0) { Fail "Default 빌드 실패(exit $LASTEXITCODE) — 0W/0E 아님, 패키징 중단" }
# [오중 빌드] COM 인터롭 쉼(#if KIWOOM_COM) 컴파일 증거 — 산출물 존재 + 이번 빌드 이후 갱신(증분 빌드 생략분은 입력 무변경이 보증)
if (-not (Test-Path -LiteralPath $shimDll)) { Fail "KiwoomComShim 산출물 없음 — #if KIWOOM_COM 블록 미검증 출하 금지: $shimDll" }
$shimProj = Join-Path $root "tools\KiwoomComShim\KiwoomComShim.csproj"
$shimInputs = @($shimProj, (Join-Path $root "tools\KiwoomComShim\AxKHOpenAPIShim.cs"), (Join-Path $root "StockManager.KiwoomProxy\ProductionKiwoomApi.cs"),
                (Join-Path $root "StockManager.KiwoomProxy\IKiwoomApi.cs"), (Join-Path $root "src\02_Infrastructure\Engine\KiwoomComBridgeCore.cs"),
                (Join-Path $root "src\02_Infrastructure\Engine\BridgeProtocol.cs"), (Join-Path $root "src\02_Infrastructure\Engine\BridgeOrderStatus.cs"))
$newestInput = ($shimInputs | ForEach-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } | Measure-Object -Maximum).Maximum
if ((Get-Item -LiteralPath $shimDll).LastWriteTimeUtc -lt $newestInput) { Fail "KiwoomComShim 산출물이 입력보다 오래됨 — 쉼 미재빌드(오중 빌드 게이트 실패)" }
Write-Host "  OK: KiwoomComShim(#if KIWOOM_COM) 0W/0E 컴파일 산출 확인" -ForegroundColor DarkGreen

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
# [v12.17.1-GM] 마스터 플래그(ShadowMetricsBridge liveArmed 생성자 인자) = 리터럴 false — 바이너리는 [9/10] --verify-live-armed 가 재판독
$masterCalls = [regex]::Matches($homeFormSrc, 'new ShadowMetricsBridge\([^;]*?liveArmed:\s*(\w+)\s*\)')
if ($masterCalls.Count -eq 0) { Fail "liveArmed 가드: 마스터 플래그 생성 0건 — 가드 대상 소실(구조 변경 확인 필요)" }
foreach ($c in $masterCalls) {
    if ($c.Groups[1].Value -ne 'false') { Fail "liveArmed 가드: 마스터 플래그가 false 가 아님 → $($c.Value)" }
}
Write-Host "  OK: 마스터 플래그 생성 $($masterCalls.Count)건 전부 liveArmed: false(소스)" -ForegroundColor DarkGreen

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
Assert-PeerPinned "pre-zip"

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


# ── [9/10] [방향 A] 출하 zip 무추출 수검 ──
Step 9 "출하 zip 무추출 수검(--verify-zip · 항목 스트림 해시 · 재계산 봉인 ↔ master-seal.txt)..."
$zipVerify = Invoke-HostCli @('--verify-zip', ('"' + $zipOut + '"'), '--seal-file', ('"' + (Join-Path $releaseDir "master-seal.txt") + '"')) "verify-zip"
if ($zipVerify -ne 0) { Remove-ReleaseOutputs; Fail "zip 무추출 수검 실패(exit=$zipVerify) — 출하 산출물 전량 폐기" }
$armVerify = Invoke-HostCli @('--verify-live-armed', ('"' + $zipOut + '"'), '--expect-tighten-keys', "$ExpectedTightenKeys", '--require-audit-ledger', '--require-pump-jitter', '--require-cancel-arming') "verify-live-armed"
if ($armVerify -ne 0) { Remove-ReleaseOutputs; Fail "출하 바이너리 liveArmed 기본값 판독 거부(exit=$armVerify) — 출하 산출물 전량 폐기" }

# ── [10/10] [방향 B] 다중 저장소 핸드셰이크(피어 읽기 전용) ──
Step 10 "다중 저장소 매니페스트 원자 기록 + 독립 핸드셰이크..."
Assert-PeerPinned "pre-sync"
$hsArgs = @('--release-dir', ('"' + $releaseDir + '"'), '--repo', ('"' + $root + '"'), '--shim', ('"' + $shimDll + '"'))
if (-not $NoPeer) {
    if (-not (Test-Path -LiteralPath (Join-Path $PeerRepo ".git"))) { Remove-ReleaseOutputs; Fail "피어 저장소 없음: $PeerRepo (-NoPeer 로 명시 생략 가능)" }
    $hsArgs += @('--peer', ('"' + $PeerRepo + '"'), '--pin-peer-head', $peerPinHead, '--pin-peer-manifest', $peerPinSha)
}
$syncExit = Invoke-HostCli (@('--sync-release-manifest') + $hsArgs + @('--live-armed-guard-passed')) "sync"
if ($syncExit -ne 0) { Remove-ReleaseOutputs; Fail "RELEASE_MANIFEST.json 동기화 거부(exit=$syncExit) — 출하 산출물 전량 폐기" }
$hsExit = Invoke-HostCli (@('--release-handshake') + $hsArgs) "handshake"
if ($hsExit -ne 0) { Remove-ReleaseOutputs; Fail "다중 저장소 핸드셰이크 거부(exit=$hsExit) — 출하 산출물 전량 폐기" }
$ilkExit = Invoke-HostCli @('--verify-interlocks', ('"' + $zipOut + '"'), '--manifest', ('"' + (Join-Path $releaseDir "RELEASE_MANIFEST.json") + '"'), '--seal-file', ('"' + (Join-Path $releaseDir "master-seal.txt") + '"'), '--expect-interlocks', "$ExpectedInterlocks") "verify-interlocks"
if ($ilkExit -ne 0) { Remove-ReleaseOutputs; Fail "30대 인터락 봉인 대조 거부(exit=$ilkExit) — 출하 산출물 전량 폐기" }
$tightExit = Invoke-HostCli @('--verify-tighten', ('"' + $zipOut + '"'), '--manifest', ('"' + (Join-Path $releaseDir "RELEASE_MANIFEST.json") + '"'), '--seal-file', ('"' + (Join-Path $releaseDir "master-seal.txt") + '"'), '--expect-tighten-keys', "$ExpectedTightenKeys", '--require-audit-ledger', '--require-audit-telemetry', '--require-ipc-fabric') "verify-tighten"
if ($tightExit -ne 0) { Remove-ReleaseOutputs; Fail "강화 전용 단조성 행렬 대조 거부(exit=$tightExit) — 출하 산출물 전량 폐기" }

Assert-PeerPinned "final"
Write-Host "== 완료: $zipOut (SHA-256 $zipSha) · RELEASE_MANIFEST.json 봉인 ==" -ForegroundColor Green
exit 0
