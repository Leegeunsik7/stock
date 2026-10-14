// ============================================================================
// JitterSnapshot — [v12.18.6-DEV · /aidash 지터 배지] MicrosecondJitterSentinel 관제 사본(값 형식 · 할당 0)
//
//  센티널은 단일 작성자(측정 스레드)라 계수 · 링 · 꼬리를 다른 스레드가 직접 읽으면 찢긴다. 대신 작성자가 '드문 경로'(임계 초과 스파이크 또는
//   상위 K 꼬리 진입 — 최대값 갱신은 항상 꼬리 진입이다) 와 Reset 에서만 seqlock 으로 이 사본을 게시하고, 관제 펌프는 임의 스레드에서
//   잠금 없이 판독한다(MicrosecondJitterSentinel.TryReadSnapshot). 정상 표본 경로(임계 미만 · 꼬리 밖)는 명령 0개 추가.
//  ★정직: 사본은 '마지막 스파이크/꼬리 진입 시점' 값이다 — 총 표본 수 · 평균은 싣지 않는다(정상 경로마다 게시하면 핫패스 비용).
//   WorstSample* = 상위 K 꼬리 요약(개수 · 최소값(K 번째) · GC 태그 수). Version = 게시 회차(0 = 아직 게시 없음).
// ============================================================================
namespace StockManager.Telemetry
{
    public readonly record struct JitterSnapshot(
        long SpikeCountOver500Us,
        long GcSpikeCount,
        long MaxJitterNanos,
        bool LastSpikeGcTagged,
        long LastSpikeNanos,
        int WorstSampleCount,
        long WorstSampleFloorNanos,
        int WorstSampleGcCount,
        long Version)
    {
        /// <summary>최대 지연(µs · 내림).</summary>
        public long MaxJitterUs => MaxJitterNanos / 1_000;
    }
}
