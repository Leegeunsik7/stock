using Xunit;

namespace StockManager.Tests
{
    /// <summary>
    /// v4.58.0 — 헤비 seqlock/Barrier 스트레스 클래스(S28 멘토·S29 Gamma 텔레메트리·S29 주문 디스패치)를 단일
    /// '비병렬' 컬렉션으로 직렬화한다. xUnit 기본은 테스트 클래스마다 별도 컬렉션을 '병렬' 실행하는데, 이 클래스들은
    /// sustained spin-loop(seqlock 리더 스핀 + Barrier 100k 라운드 + 24-라이터 경합)를 돌려 동시 가동 시 CPU 를
    /// 초과 구독(oversubscribe)한다 → Barrier/Join(timeout) 이 기아로 만료되어 Release 풀스위트가 플래키해진다
    /// ([[concurrency-test-work-bounded]]: 안전성은 부하 무관이나 활성성=Join 은 기아에 취약).
    /// DisableParallelization=true 로 이 컬렉션은 다른 컬렉션과 겹치지 않고 단독 구간에서 순차 실행 — 동시 1개만
    /// 가동되어 초과 구독이 사라진다. 나머지 1,500+ 빠른 테스트는 병렬 유지(전체 실행시간 영향 최소).
    /// 안전성 단언(torn==0 등)은 직렬화와 무관하게 항상 성립하며, 본 변경은 '활성성(Join 비기아)'만 결정론화한다.
    /// </summary>
    [CollectionDefinition("ConcurrencyStress", DisableParallelization = true)]
    public sealed class ConcurrencyStressCollection { }
}
