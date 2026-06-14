# Contributing to MS2-E Stock Suite

[English](#english) · [한국어](#한국어)

---

## English

Thanks for your interest! This project values a **deterministic, well-tested core** and an
**honest verification boundary**. Please keep both in mind.

### Ground rules

1. **Keep the core pure.** Anything under `Common/` that does math/analysis must be a pure,
   deterministic function — no clock, no `Random` without an injected seed, no network, no file
   IO. Side-effecting code (network, DB, LLM, notifications) belongs in **adapters**.
2. **No real-money trading paths.** This is non-negotiable. New features may *propose*, *paper-
   trade*, or *analyze read-only* — never place live orders. Auto-DCA / copilot stay paper-only.
3. **Test the math.** Every pure module ships with xUnit tests (`StockManager.Tests/`) covering
   determinism, fixed-seed reproducibility, boundary conditions, and key invariants.
4. **Be honest in docs.** Mark only build + unit tests as "verified". UI / WebSocket / LLM /
   live-network behavior is *runtime-dependent* — never claim it as verified.

### Dev workflow

```powershell
# build (single-process to keep Razor compiler errors readable)
dotnet build StockManager.sln -c Release -m:1

# test
dotnet test StockManager.Tests\StockManager.Tests.csproj -c Release -m:1

# run
dotnet run --project StockManager\StockManager.csproj
# headless engine smoke test (boot, ~6-8s, then close)
dotnet run --project StockManager\StockManager.csproj -- --headless
```

### Conventions

- **Versioning:** bump `<Version>` in `StockManager/StockManager.csproj` per feature, and bump
  the `MS2-E · vX.Y` label in `Blazor/NavMenu.razor`.
- **Commits:** one feature per commit. Conventional-style prefix (`feat:`, `fix:`, `chore:`,
  `build:`, `docs:`). Korean commit messages are welcome.
- **Razor + Korean gotcha:** `@member한글` is parsed as a single identifier and fails to
  compile. Wrap the member access: `@(Member)한글`.
- **Nullable:** `Db.GetSetting(...)` returns `string?`. Guard with a null check or `!` when you
  know it's set.
- **Docs:** update `StockManager/PROGRESS.md` (version history + section) with each version.

### Pull requests

- Keep PRs focused (one feature/fix). Include the test additions in the same PR.
- State the verification done: build result, test count, and what is *not* verified (runtime).
- CI (`.github/workflows/ci.yml`) must be green (build + tests on `windows-latest`).

---

## 한국어

관심 가져주셔서 감사합니다! 이 프로젝트는 **결정론적이고 잘 테스트된 코어**와 **정직한 검증
경계**를 중요하게 여깁니다. 두 가지를 항상 염두에 두세요.

### 기본 원칙

1. **코어는 순수하게.** `Common/` 아래에서 수학/분석을 하는 코드는 순수·결정론적 함수여야
   합니다 — 시계 없음, 시드 없는 `Random` 없음, 네트워크 없음, 파일 IO 없음. 부수효과가 있는
   코드(네트워크·DB·LLM·알림)는 **어댑터**에 둡니다.
2. **실거래 경로 금지.** 타협 불가입니다. 새 기능은 *제안*, *모의(paper)*, *읽기전용 분석*만
   가능하며 실주문을 넣어선 안 됩니다. 자동 DCA·코파일럿은 모의 전용을 유지합니다.
3. **수학을 테스트하세요.** 모든 순수 모듈은 xUnit 테스트(`StockManager.Tests/`)와 함께
   제출합니다 — 결정성, 고정 시드 재현성, 경계 조건, 핵심 불변식.
4. **문서는 정직하게.** 빌드+단위 테스트만 "검증됨"으로 표기하세요. UI / WebSocket / LLM /
   실시간 네트워크 동작은 *런타임 의존*이며, 검증됨으로 주장하지 않습니다.

### 개발 워크플로

```powershell
# 빌드 (Razor 컴파일 에러를 읽기 쉽게 단일 프로세스로)
dotnet build StockManager.sln -c Release -m:1

# 테스트
dotnet test StockManager.Tests\StockManager.Tests.csproj -c Release -m:1

# 실행
dotnet run --project StockManager\StockManager.csproj
# 헤드리스 엔진 스모크 테스트 (부팅, 약 6~8초 후 종료)
dotnet run --project StockManager\StockManager.csproj -- --headless
```

### 컨벤션

- **버전:** 기능마다 `StockManager/StockManager.csproj`의 `<Version>`을 올리고,
  `Blazor/NavMenu.razor`의 `MS2-E · vX.Y` 라벨도 함께 올립니다.
- **커밋:** 기능 하나당 커밋 하나. 컨벤셔널 접두사(`feat:`, `fix:`, `chore:`, `build:`,
  `docs:`). 한글 커밋 메시지 환영합니다.
- **Razor + 한글 함정:** `@멤버한글`은 하나의 식별자로 파싱되어 컴파일에 실패합니다. 멤버
  접근을 감싸세요: `@(멤버)한글`.
- **Nullable:** `Db.GetSetting(...)`은 `string?`을 반환합니다. null 체크 또는 (설정이 있음을
  아는 경우) `!`로 가드하세요.
- **문서:** 버전마다 `StockManager/PROGRESS.md`(버전 이력 + 섹션)를 갱신합니다.

### Pull Request

- PR은 한 가지(기능/수정)에 집중하고, 테스트 추가를 같은 PR에 포함하세요.
- 수행한 검증을 명시하세요: 빌드 결과, 테스트 건수, 그리고 *검증되지 않은* 런타임 영역.
- CI(`.github/workflows/ci.yml`)가 녹색이어야 합니다(`windows-latest`에서 빌드+테스트).
