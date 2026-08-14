// ============================================================================
// SequenceContinuityGuardian — v10.2.0: ★Think Beyond Gemini — 실시간 시세 시퀀스 연속성 수호자
//   "WebSocket 순간 끊김에 의한 Sequence Hole 을, '메운다'가 아니라 '연속으로 위장하지 못하게 한다'(fail-closed)."
//
//  ── 왜 제미나이 B(갭 리커버리 거버너)를 초월하는가 ─────────────────────────────────────────
//  제미나이 B 는 "재연결 시 유실 시퀀스를 REST 로 슬라이싱해 메운다"이다. 그러나 REST 백필 자체가 실패/지연할 수 있고,
//  그 사이 소비자(리스크/알파 엔진)는 '갭이 메워졌다'고 오신하여 불연속 데이터를 연속으로 소비 → 조용한 데이터 손실이
//  판정에 스며든다(가장 위험). ★근본 불변식은 '갭을 항상 메운다'가 아니라 '갭이 연속 데이터로 위장하지 못하게 한다'이다.
//  Guardian 은 δ=0 단조 시퀀스 추적으로 hole 을 즉시 탐지하고 (1) 영향 상태를 '잠정(provisional)' 워터마크로 표시해
//  소비자가 불연속을 '알게' 하고(fail-closed — 잠정 중엔 연속성 보장 안 함), (2) bounded backfill 요청을 트리거하되,
//  (3) 백필이 ConfirmBackfill 로 확증되기 전엔 잠정 유지, (4) 갭이 maxGap 초과면 복구 불가로 즉시 Stale 판정(날조 금지).
//  즉 '복구'를 넘어 '정직한 불연속 고지 + 확증된 복구만 신뢰'로 승격 — 손실이 판정에 스며드는 경로를 원천 봉쇄.
//
//  ★불변: δ=0(Observe/상태조회 핫패스 힙 0 — 순수 산술 + Interlocked) · 결정론(단조 seq) · fail-closed(갭 → 잠정,
//    미확증 백필은 연속 아님, 초대형 갭 → Stale) · liveArmed:false(순수 시퀀스 진단 — IBroker·소켓·실 발주 유발 0).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>시퀀스 관측 결과.</summary>
    public enum SeqObservation : byte
    {
        Contiguous     = 0,   // 연속(정상 전진)
        Duplicate      = 1,   // 중복/구식(멱등 무시)
        RecoverableGap = 2,   // 갭 — backfill 로 복구 가능(≤maxGap)
        StaleGap       = 3,   // 갭 초대형 — 복구 불가·Stale(연속성 날조 금지)
    }

    /// <summary>δ=0 시퀀스 연속성 수호자. hole 탐지 → 잠정 워터마크(fail-closed) → bounded backfill → 확증 시에만 해제.</summary>
    public sealed class SequenceContinuityGuardian
    {
        private readonly long _maxGap;
        private long _started;             // 0/1 — 첫 관측 여부(−1 센티널 대신 — 음수/거대 seq 안전)
        private long _expectedSeq;         // 다음 기대 시퀀스(단일 소비자=WS 인제스트 스레드)
        private long _highWaterSeq;        // 최고 관측 seq(=frontier·ConfirmBackfill 잠정 해제 판정)
        private long _lastContiguousSeq;   // 마지막 연속 확인 seq
        private long _holesDetected, _holesRecovered, _gapTickTotal;
        private long _provisional;         // 0/1 — 미복구 갭 존재(잠정)
        private long _stale;               // 0/1 — 복구 불가(초대형 갭)

        /// <param name="maxGap">이 이상 갭이면 복구 불가로 Stale(기본 4096틱).</param>
        public SequenceContinuityGuardian(long maxGap = 4096)
        {
            _maxGap = maxGap <= 0 ? 4096 : maxGap;   // _started=0 → 첫 관측이 기준점(seq 값 무관)
        }

        public bool IsProvisional      => Volatile.Read(ref _provisional) == 1L;
        public bool IsStale            => Volatile.Read(ref _stale) == 1L;
        public bool IsContinuous       => Volatile.Read(ref _provisional) == 0L && Volatile.Read(ref _stale) == 0L;
        public long HolesDetected      => Interlocked.Read(ref _holesDetected);
        public long HolesRecovered     => Interlocked.Read(ref _holesRecovered);
        public long GapTickTotal       => Interlocked.Read(ref _gapTickTotal);
        public long LastContiguousSeq  => Interlocked.Read(ref _lastContiguousSeq);

        /// <summary>★수신 seq 관측(δ=0·단일 소비자). 연속=전진, 갭=hole 계수+잠정 표시+복구 가능성 판정.</summary>
        public SeqObservation Observe(long seq)
        {
            if (Volatile.Read(ref _started) == 0L)     // 첫 관측 — 기준점(seq 값 무관·음수/거대값 안전)
            {
                Volatile.Write(ref _started, 1L);
                _expectedSeq = seq + 1;
                Interlocked.Exchange(ref _lastContiguousSeq, seq);
                Interlocked.Exchange(ref _highWaterSeq, seq);
                return SeqObservation.Contiguous;
            }
            long expected = _expectedSeq;
            if (seq == expected)                       // 연속
            {
                _expectedSeq = seq + 1;
                Interlocked.Exchange(ref _lastContiguousSeq, seq);
                Interlocked.Exchange(ref _highWaterSeq, seq);
                return SeqObservation.Contiguous;
            }
            if (seq < expected)                        // 중복/구식 → 멱등 무시
                return SeqObservation.Duplicate;

            // seq > expected → hole
            long gap = seq - expected;
            Interlocked.Increment(ref _holesDetected);
            Interlocked.Add(ref _gapTickTotal, gap);
            Volatile.Write(ref _provisional, 1L);      // ★잠정: 이후 연속성 보장 안 함
            _expectedSeq = seq + 1;                     // 새 seq 이후로 전진(갭은 backfill 대상)
            Interlocked.Exchange(ref _highWaterSeq, seq);   // ★최고 관측 seq(=frontier)
            if (gap > _maxGap)
            {
                Volatile.Write(ref _stale, 1L);        // ★복구 불가 → Stale(날조 금지)
                return SeqObservation.StaleGap;
            }
            return SeqObservation.RecoverableGap;
        }

        /// <summary>
        /// backfill 로 uptoSeq 까지 연속 회복을 확증 통지(콜드). ★잠정 해제는 uptoSeq 가 최고 관측 seq(frontier)까지 덮을
        /// 때만 — 미확증 신규 갭이 남아 있으면 잠정 유지(fail-closed·손실이 연속으로 위장 못함). Stale 은 해제 불가.
        /// 반환=잠정 해제됨(true)/여전히 잠정(false).
        /// </summary>
        public bool ConfirmBackfill(long uptoSeq)
        {
            if (Volatile.Read(ref _stale) == 1L) return false;   // ★Stale 은 backfill 로 해제 불가(손실 확정)
            long prev = Interlocked.Read(ref _lastContiguousSeq);
            if (uptoSeq > prev) Interlocked.Exchange(ref _lastContiguousSeq, uptoSeq);
            if (uptoSeq >= Interlocked.Read(ref _highWaterSeq))  // ★frontier 까지 덮음 → 연속 회복
            {
                Volatile.Write(ref _provisional, 0L);
                Interlocked.Increment(ref _holesRecovered);      // ★성공 복구만 계수
                return true;
            }
            return false;   // 아직 잠정(더 높은 갭 미복구)
        }
    }
}
