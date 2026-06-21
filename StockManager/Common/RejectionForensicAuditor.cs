// ============================================================================
// RejectionForensicAuditor — S23-c: 비가역 거부 포렌식 (제로 할당 · 락-프리 독출)
//
//  인간 지휘관이 올바른 서명으로 승인했으나, 비가역 핫스왑 직전 최하단 _lock 임계구역에서
//  As-Of 신선도 만료로 Rejected 된 microscopic 한 찰나 — 그 거부 사유를 단 1비트의 힙 할당도
//  없이 원장에 박제한다. 인지적 공포(오작동인가? 타이밍인가?)를 정직하게 분별하기 위한 텔레메트리.
//
//  ── 제로 할당 ───────────────────────────────────────────────────────────────
//  값 타입 RejectionRecord 의 사전 할당 순환 큐(RejectionRecord[]). Record 는 문자열 결합·인스턴스
//  생성 없이 슬롯을 구조체 복사로 덮어쓴다(SpecId 는 기존 문자열 참조 재사용 — 신규 할당 0).
//
//  ── 단일 라이터 seqlock ─────────────────────────────────────────────────────
//  쓰기는 PromotionGateway._lock 으로 직렬화된다(유일 라이터). 독출(관제탑·아그리게이터)은 락-프리:
//  버전 짝/홀 검증으로 torn read 를 격리한다(쓰기 중 홀수 → 재시도). volatile long 불법이므로
//  Volatile.Read/Write(ref long)·Interlocked 만 사용한다.
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public enum RejectionStage : byte { None = 0, Approve = 1, Activate = 2 }

    /// <summary>거부 1건의 불변 포렌식 레코드(값 타입). 기본값(Seq=0)은 '레코드 없음'.</summary>
    public readonly struct RejectionRecord
    {
        public readonly long           Seq;
        public readonly RejectionStage Stage;
        public readonly long           AsOfTicks;     // 챔피언이 진화한 데이터의 마켓 시각
        public readonly long           NowTicks;      // 거부가 일어난 순간
        public readonly long           WindowTicks;   // 신선도 한도
        public readonly double         Fitness;
        public readonly string?        SpecId;        // 기존 문자열 참조(신규 할당 0)

        public RejectionRecord(long seq, RejectionStage stage, long asOfTicks,
                               long nowTicks, long windowTicks, double fitness, string? specId)
        {
            Seq         = seq;
            Stage       = stage;
            AsOfTicks   = asOfTicks;
            NowTicks    = nowTicks;
            WindowTicks = windowTicks;
            Fitness     = fitness;
            SpecId      = specId;
        }

        public bool   HasValue => Seq > 0;
        public long   LagTicks => NowTicks - AsOfTicks;
        public double LagMs    => LagTicks    / (double)TimeSpan.TicksPerMillisecond;
        public double WindowMs => WindowTicks / (double)TimeSpan.TicksPerMillisecond;
    }

    public sealed class RejectionForensicAuditor
    {
        private readonly RejectionRecord[] _ring;
        private long _seq;        // 단조 쓰기 시퀀스(1-based). 0 = 거부 없음.
        private long _version;    // seqlock: 짝=안정, 홀=쓰기중
        private int  _lastIndex;  // 최신 레코드 슬롯

        public RejectionForensicAuditor(int capacity = 64)
            => _ring = new RejectionRecord[capacity < 1 ? 1 : capacity];

        public int  Capacity => _ring.Length;
        public long Count    => Interlocked.Read(ref _seq);

        /// <summary>
        /// 거부를 박제한다. 단일 라이터(PromotionGateway._lock 직렬화)에서만 호출 — 할당 0, 나노초.
        /// 거부 시퀀스를 반환한다.
        /// </summary>
        public long Record(RejectionStage stage, long asOfTicks, long nowTicks,
                           long windowTicks, double fitness, string? specId)
        {
            long seq = Interlocked.Increment(ref _seq);          // 1-based
            int  idx = (int)((seq - 1) % _ring.Length);

            long ver = _version;                                  // 단일 라이터 → 자기 마지막 쓰기값
            Volatile.Write(ref _version, ver + 1);                // 홀수 — 쓰기 시작(reader 무효화)
            _ring[idx] = new RejectionRecord(seq, stage, asOfTicks, nowTicks, windowTicks, fitness, specId);
            _lastIndex = idx;
            Volatile.Write(ref _version, ver + 2);                // 짝수 — 쓰기 완료(슬롯·인덱스 발행)
            return seq;
        }

        /// <summary>
        /// 최신 K개 거부 레코드를 시간순(오래된→최신)으로 dest 에 채운다(락-프리·무할당). 채운 개수 반환.
        /// S24-b 플러시 펌프가 백그라운드에서 ring→저널로 읽어가는 배치 드레인. seqlock 검증으로 torn read 격리.
        /// </summary>
        public int SnapshotInto(Span<RejectionRecord> dest)
        {
            if (dest.IsEmpty) return 0;
            for (int attempt = 0; attempt < 16; attempt++)
            {
                long v1 = Volatile.Read(ref _version);
                if ((v1 & 1L) != 0L) continue;                       // 쓰기 중 → 재시도
                long total = Interlocked.Read(ref _seq);
                if (total == 0L) return 0;                            // 아직 거부 없음
                int cap  = _ring.Length;
                int k    = (int)Math.Min(Math.Min(total, cap), dest.Length);
                int last = Volatile.Read(ref _lastIndex);
                for (int i = 0; i < k; i++)
                {
                    int idx = ((last - (k - 1 - i)) % cap + cap) % cap;   // 음수 안전 모듈로
                    dest[i] = _ring[idx];                            // 구조체 복사
                }
                long v2 = Volatile.Read(ref _version);
                if (v1 == v2) return k;                              // 검증 통과(torn 없음)
            }
            return 0;
        }

        /// <summary>최신 거부 레코드를 락-프리로 읽는다. torn read 면 재시도, 거부 없음/실패 시 false.</summary>
        public bool TryGetLatest(out RejectionRecord rec)
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                long v1 = Volatile.Read(ref _version);
                if ((v1 & 1L) != 0L) continue;                    // 쓰기 중 → 재시도
                if (Interlocked.Read(ref _seq) == 0L) break;      // 아직 거부 없음
                int idx = Volatile.Read(ref _lastIndex);
                rec = _ring[idx];                                  // 구조체 복사
                long v2 = Volatile.Read(ref _version);
                if (v1 == v2) return true;                         // 검증 통과(torn 없음)
            }
            rec = default;
            return false;
        }
    }
}
