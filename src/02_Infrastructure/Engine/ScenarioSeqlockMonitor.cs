// ============================================================================
// ScenarioSeqlockMonitor — v4.63.0: 시나리오 변이 seqlock(ScenarioPublisher) 관제 브리지
//
//  ★수명 불일치 해소: ScenarioPublisher 는 FutureSimPage 가 OnInitialized 에서 구성하는 '페이지별 transient' 라,
//  호스트의 '싱글톤' 중앙 샘플러(RingTelemetryPublisher)가 직접 읽을 인스턴스가 없다. 그래서 활성 시뮬 페이지가
//  매 폴 프레임 자기 퍼블리셔의 카운터(WriterCollisions/FrozenCount/Version)를 본 싱글톤에 Report 하고, 중앙
//  샘플러는 Sample 시 Read 해 RingTelemetry 에 합류시킨다. 페이지 비활성 시 마지막 값 유지(stale·무해; Version 으로 판별).
//
//  무할당(δ=0): long 3개의 Volatile 쓰기/읽기뿐 — 박싱·힙 0. 다중 라이터(이론상)라도 개별 long 은 원자적이고
//  '관측 텔레메트리'(권위 아님)라 약한 일관성으로 충분(찢긴 조합은 다음 프레임에 자가 치유).
// ============================================================================
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>시나리오 seqlock 카운터의 싱글톤 관제 브리지(페이지 transient → 호스트 싱글톤 시임). 무할당.</summary>
    public sealed class ScenarioSeqlockMonitor
    {
        private long _writerCollisions;
        private long _frozen;
        private long _version;   // 0 = 시나리오 퍼블리셔 미보고(시뮬 페이지 비활성)

        /// <summary>활성 시뮬 페이지가 매 프레임 자기 퍼블리셔 카운터를 보고(단일 라이터 가정·Volatile 쓰기).</summary>
        public void Report(long writerCollisions, long frozen, long version)
        {
            Volatile.Write(ref _writerCollisions, writerCollisions);
            Volatile.Write(ref _frozen, frozen);
            Volatile.Write(ref _version, version);
        }

        public long WriterCollisions => Volatile.Read(ref _writerCollisions);
        public long Frozen           => Volatile.Read(ref _frozen);
        public long Version          => Volatile.Read(ref _version);
        /// <summary>시나리오 퍼블리셔가 한 번이라도 보고했는가(시뮬 페이지 활성 이력).</summary>
        public bool HasReported      => Volatile.Read(ref _version) > 0;
    }
}
