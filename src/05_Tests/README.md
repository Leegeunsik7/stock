# 05_Tests — 검증 스위트

유닛·통합·δ=0 무할당 증명. 전 레이어 참조 가능. 이미 `StockManager.Tests` 프로젝트 존재.

- **2,344 유닛** + `Phase112ArchitectureFitnessTests`(아키텍처 적합도) — `ArchitectureFitnessAnalyzer` 로 레이어 의존 규칙을 **자기강제**(위반 시 빌드 실패).
- 타이밍/δ=0/멀티스레드 신규 테스트는 `[Collection("ConcurrencyStress")]` 필수(로컬 병렬 플래키 방지).
- δ=0 증명 관용구: `long b=GC.GetAllocatedBytesForCurrentThread(); …; Assert.Equal(0L, after-b);`

**현재 물리 위치**: `StockManager.Tests/`, `StockManager.PrimesBench/`(δ=0 벤치).
