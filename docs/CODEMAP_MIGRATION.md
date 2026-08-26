# CODEMAP_MIGRATION — v11.2.0 프로젝트 재구조화 실행 매니페스트

> 목적: 모놀리식 소스를 5대 Clean Architecture 레이어(`src/01_Core`~`05_Tests`)로 **안전하게(0W/0E·2344 테스트·δ=0·liveArmed:false 사수)** 이주하는 단계별 절차. 인수인계 개발자가 이 문서만으로 이주를 완주할 수 있게 한다.

## 0. 현재 상태 (정찰 실측)

| 지표 | 값 |
|---|---|
| 추적 `.cs` | 518 |
| `StockManager/Common/*.cs`(평면 모놀리식) | 302 |
| `namespace StockManager.Common` 선언 파일 | 302 |
| `using StockManager.Common` 참조 파일 | 201 |
| `.razor` | 92 |
| 프로젝트(`.csproj`) | 5 (StockManager, MS2E.UI.Portable, Tests, PrimesBench, KiwoomProxy) |

라이브 분포·오배치는 앱 내 **🗺️ 코드맵 뷰어**(`/tools/codemap`)가 `ArchitectureFitnessAnalyzer` 로 실시간 집계.

## 1. 목표 구조 & 의존 규칙

`src/README.md` 참조. 핵심 불변: **의존은 안으로만**(Core←Infra←UI, Host/Tests=합성/검증). `Phase112ArchitectureFitnessTests` 가 규칙을 **자기강제**한다.

## 2. 왜 단계적인가 (위험)

1. **공유 primary master + 타 세션 미커밋 작업** — 302개 파일 물리 이동 + 네임스페이스 개편을 master 에서 하면 진행 중인 타 개발자 작업(로그인/유저관리/우편번호 등 11건)을 파괴한다. → **격리 브랜치 `feature/v11.2.0-restructure` 에서만 수행.**
2. **강결합 웹** — `RingTelemetryPublisher` 는 ~60개 의존을 받는다. Core/Infra 를 별도 `.csproj` 로 즉시 쪼개면 순환 의존이 다수 노출되어 0E 도달 불가. → 네임스페이스 보존 폴더 이동을 **먼저**, 프로젝트 분할을 **마지막**에.

## 3. 단계별 절차 (각 단계 후 `dotnet build ×2(Default/WebOnly) 0W/0E` + `dotnet test 2344+` 게이트 통과 필수)

- **Stage 0 — 스캐폴드 + 적합도 게이트** ✅(본 커밋): `src/` 5레이어 폴더 + README, `ArchitectureFitnessAnalyzer`(δ=0) + `Phase112` 테스트, `ProjectCodeViewer`(`/tools/codemap`).
- **Stage 1 — 네임스페이스 보존 폴더 이주**: `git mv` 로 `StockManager/Common/*.cs` 를 역할별 하위 폴더로 이동하되 **네임스페이스(`StockManager.Common`)는 유지**. C# 은 폴더≠네임스페이스라 `using` 201곳 무변경 → **0 blast·0E**. 뷰어 오배치 카운트가 목표 폴더 분포로 수렴하는지 확인.
  - 분류 근거: `ArchitectureFitnessAnalyzer.ClassifyLayer/ClassifyDomain`(경로 규칙). 자동 스크립트가 뷰어와 동일 규칙으로 이동 계획을 산출.
- **Stage 2 — 레이어 네임스페이스 정렬**: 폴더별로 `namespace StockManager.Common` → `MS2E.Core.*` / `MS2E.Infrastructure.*` 로 순차 치환 + 해당 `using` 갱신. **한 도메인씩** 커밋·게이트. (302 선언·201 참조 — 도메인 단위로 쪼개 blast 국소화.)
- **Stage 3 — 프로젝트 분할**: `MS2E.Core.csproj`(무의존)·`MS2E.Infrastructure.csproj`(→Core) 신설, 파일 귀속 이동, `ProjectReference` 재배선. 순환 의존은 Port 역전(DIP)으로 해소. Host/Tests 는 참조만 갱신.
- **Stage 4 — 병합**: 타 세션 작업이 master 에 커밋된 후 `feature/v11.2.0-restructure` 를 rebase→병합. `NavMenu.razor`/`BlazorHomeForm.cs` 충돌은 수동 정합.

## 4. 지휘관 병합 절차

```bash
# 타 세션 작업이 master 에 커밋된 뒤:
git checkout master && git pull
git worktree list                         # feature/v11.2.0-restructure 확인
git merge --no-ff feature/v11.2.0-restructure
# 충돌 시 NavMenu.razor / BlazorHomeForm.cs 수동 정합 후:
dotnet build StockManager/StockManager.csproj -c Release                 # 0W/0E
dotnet build StockManager/StockManager.csproj -c Release -p:WebOnly=true # 0W/0E
dotnet test StockManager.Tests/StockManager.Tests.csproj -c Release      # 2344+ 통과
git worktree remove ../StockManager-v112  # 정리
```

## 5. 불변 (전 단계 공통·타협 불가)

liveArmed:false · fail-closed · δ=0 핫패스 · 결정론 · 이중 빌드 0W/0E · 한국 관례 색상 · 2,344 테스트 0 회귀.
