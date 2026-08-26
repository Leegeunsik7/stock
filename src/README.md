# `src/` — MS2E StockSuite 소스 레이아웃 (Clean Architecture · Ports & Adapters)

**v11.2.0** 도입. 의존은 **안으로만** 향한다(바깥 레이어 → 안쪽 레이어만 참조).
`ArchitectureFitnessAnalyzer`(유닛 테스트 `Phase112ArchitectureFitnessTests`)가 이 규칙을 **자기강제(self-enforcing)** 한다 — 레이어 위반 시 빌드가 실패한다.

| 레이어 | 역할 | 참조 가능 |
|---|---|---|
| `01_Core` | 순수 도메인·엔티티·불변 DTO·Port 인터페이스 | Core |
| `02_Infrastructure` | 외부 어댑터·HW(SIMD)·보안(금고)·텔레메트리·영속화 | Core, Infra |
| `03_UI.Portable` | 순수 Blazor UI(RCL) | Core, UI (**★엔진 무참조**) |
| `04_Host` | WinForms/Blazor Hybrid 합성 루트·네비게이션 | 전부 |
| `05_Tests` | 유닛·통합·δ=0 무할당 증명 | 전부 |

- **라이브 분포·오배치(재배치 대상)**: 앱 내 **🗺️ 코드맵 뷰어**(`/tools/codemap`)에서 실시간 확인.
- **물리 이주 절차·현재→목표 매핑**: [`docs/CODEMAP_MIGRATION.md`](../docs/CODEMAP_MIGRATION.md).

> ⚠️ 이 스캐폴드는 **격리 브랜치 `feature/v11.2.0-restructure`** 에서 생성됨(공유 master 무손상). 물리 파일 이주는 코어 철학(0W/0E·2344 테스트·δ=0·liveArmed:false)을 단계별로 사수하며 진행한다.
