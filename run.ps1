# ============================================================================
# run.ps1 — 저장소 루트에 .csproj가 없어(솔루션 내 5개 프로젝트 중 실행 대상이 모호) `dotnet run`이 루트에서
# 바로 동작하지 않는 문제의 편의 래퍼. 실제 실행 대상(StockManager.csproj, WinForms+Blazor 데스크톱 UI)에
# --project로 명시 위임한다. 빌드 그래프/솔루션 구성에는 전혀 개입하지 않음(신규 csproj 추가 없음 — RatchetCeiling/
# 4중 빌드 매트릭스 영향 0).
#
#   사용:  ./run.ps1                    # 데스크톱 UI 실행
#          ./run.ps1 -- --headless      # 헤드리스 엔진 모드로 실행(인자 그대로 전달)
# ============================================================================
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Args
)
$proj = Join-Path $PSScriptRoot "StockManager\StockManager.csproj"
dotnet run --project $proj @Args
