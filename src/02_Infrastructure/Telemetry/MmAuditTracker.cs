// ============================================================================
// MmAuditTracker — v6.3.0 Task 3a: MMF 프레임 시퀀스 & Gap 감사기
//
//  StockSuiteKernelTelemetryServer 가 MMF 버스 헤더에 부여하는 64비트 프레임 시퀀스를 관측해, 파이프→MMF
//  이중버스 폴백 시 '프레임 유실(gap)' 여부를 자동 추적한다. MMF 는 단일 슬롯(최신 스냅샷 pull)이라 소비자
//  폴 주기 < 서버 갱신 주기이면 중간 프레임을 건너뛸 수 있는데, 그 유실량을 시퀀스 도약으로 정량 감사한다
//  (최신 상태는 무손실 — '유실 없이 최신 프레임 지속 수신' — 이면서, 건너뛴 중간 프레임은 gap 으로 계수).
//
//  ★불변 제약: δ=0(Interlocked 카운터만·힙 0). 락프리. liveArmed:false(순수 관측 계수).
// ============================================================================
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>MMF 프레임 시퀀스 gap 감사기(δ=0·락프리). Observe(seq) → 유실/리셋 계수.</summary>
    public sealed class MmAuditTracker
    {
        private long _lastSeq = -1L, _observed, _gaps, _resets, _maxSeq;

        public long Observed     => Interlocked.Read(ref _observed);
        public long Gaps         => Interlocked.Read(ref _gaps);     // 누적 유실(건너뛴) 프레임 수
        public long Resets       => Interlocked.Read(ref _resets);   // 시퀀스 되감김(서버 재기동) 횟수
        public long LastSequence => Interlocked.Read(ref _lastSeq);
        public long MaxSequence  => Interlocked.Read(ref _maxSeq);
        /// <summary>무결성 무손실률(‰) — 관측 대비 (관측+유실) 중 실제 수신 비율.</summary>
        public long IntegrityPermille { get { long o = Observed, g = Gaps, t = o + g; return t > 0 ? o * 1000L / t : 1000L; } }

        /// <summary>시퀀스 1건 관측(δ=0). 직전보다 2↑ 도약이면 그 사이가 유실(gap). 감소=서버 재기동(reset).</summary>
        public void Observe(long sequence)
        {
            long last = Interlocked.Exchange(ref _lastSeq, sequence);
            Interlocked.Increment(ref _observed);
            long mx = Interlocked.Read(ref _maxSeq);
            while (sequence > mx && Interlocked.CompareExchange(ref _maxSeq, sequence, mx) != mx) mx = Interlocked.Read(ref _maxSeq);
            if (last < 0L) return;                                          // 첫 관측
            if (sequence < last) { Interlocked.Increment(ref _resets); return; }   // 되감김 = 서버 재기동
            long delta = sequence - last;
            if (delta > 1L) Interlocked.Add(ref _gaps, delta - 1L);        // 사이 프레임 유실 계수
        }

        /// <summary>감사 상태 초기화(새 폴백 세션).</summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _lastSeq, -1L);
            Interlocked.Exchange(ref _observed, 0L);
            Interlocked.Exchange(ref _gaps, 0L);
            Interlocked.Exchange(ref _resets, 0L);
            Interlocked.Exchange(ref _maxSeq, 0L);   // ★새 폴백 세션에서 MaxSequence 도 초기화(stale 잔류 결함 교정)
        }
    }
}
