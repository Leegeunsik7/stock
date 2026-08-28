// ============================================================================
// CrossBrokerDispatchCoalescer — v11.3.0 Think Beyond Gemini
//   ★이종 브로커(키움 x86 COM Pipe 스레드 + 토스 REST HTTP Stream 스레드)에서 동시에 터지는 UI 갱신 요청이
//     Blazor Dispatcher 락을 경합해 프레임을 드랍시키는 "Cross-Broker UI Dispatcher Stalling"을 영구 격리한다.
//
//   ── 제미나이 A(Lock-Free UI Coalescer) + B(IPC Health Watchdog)를 '단일 엔진'으로 융합·초월 ──
//     A. 코얼레싱: 브로커별 dirty 카운터를 무락(Interlocked)으로 누적 → 렌더 펌프가 프레임 경계에서 1회만 배수,
//        N개 틱 폭풍이 몰아쳐도 RenderTrigger 는 프레임당 최대 1회(δ=0 핫패스, 힙 0).
//     B. Safe-Hold 헬스 래치: 브로커별 헬스를 동일 엔진 안에 통합. 키움 파이프 단선/토스 429·토큰만료 시 해당 라인만
//        원자적으로 SafeHold 로 래치(sticky·fail-closed) — 다른 브로커의 라이브니스를 잃지 않는다.
//
//   ── ★독창적 초월점(제미나이 미제시) ──
//     ① 통합 상태: 코얼레스(dirty)와 헬스(epoch)가 한 엔진의 원자 배열로 결속 → 펌프가 '데이터 변경 OR 헬스 전이'를
//        하나의 배수로 감지. 무변경 정상상태는 렌더 0(idle-quiet) — 조용한 프레임까지 절약.
//     ② 기아방지 로터(fairness rotor): 지속적 이중 피드 압력에서 배수 시작 브로커를 라운드로빈 → '시끄러운 브로커가
//        조용한 브로커의 UI 갱신을 굶기는' 다중 파이프라인 맹점을 제거(단순 코얼레서엔 없는 공정성).
//
//   불변: δ=0(Mark/TryDrain 힙 0·Interlocked/Volatile 만) · 결정론(epoch 단조·로터 순환) · fail-closed(SafeHold sticky)
//        · 스레드 안전(다중 라이터 Mark/SetHealth ↔ 단일 드레이너 TryDrain) · liveArmed:false(순수 UI 디스패치 관제 —
//        발주/소켓/IPC 유발 0. 브로커 스레드가 '이미 받은' 틱의 렌더 병합만 담당).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.UI.Portable.Infrastructure
{
    /// <summary>브로커 라인 헬스. SafeHold = 워치독이 래치한 격리 상태(sticky, Clear 전까지 유지).</summary>
    public enum BrokerHealth
    {
        Online   = 0,
        Degraded = 1,   // 지연/부분열화(예: 토스 429 근접·버킷 저하)
        SafeHold = 2,   // ★격리 래치(키움 파이프 단선·토스 429 초과·토큰 만료) — fail-closed
        Offline  = 3,
    }

    /// <summary>펌프가 프레임 경계에서 배수한 브로커별 코얼레스 프레임(값타입·무할당).</summary>
    public readonly record struct BrokerFrame(int Broker, long CoalescedMarks, BrokerHealth Health, bool HealthChanged);

    public sealed class CrossBrokerDispatchCoalescer
    {
        private readonly int _n;
        private readonly long[] _dirty;         // 브로커별 코얼레스 마크 카운터(다중 라이터 Interlocked.Increment)
        private readonly int[]  _health;        // 브로커별 BrokerHealth
        private readonly long[] _healthEpoch;   // 헬스 전이마다 +1(단조) — 전이 감지용
        private readonly long[] _lastSeenEpoch; // 펌프 로컬(드레이너 단독 접근) — 마지막 배수 때 본 epoch
        private int  _rotor;                    // 기아방지 로터(드레이너 단독)
        private int  _pending;                  // 0/1 빠른 게이트(마크/헬스 시 1)

        private long _marks;                    // 누적 마크(코얼레스 압축비 산출)
        private long _renderTriggers;           // 실제 렌더 사출 횟수

        public CrossBrokerDispatchCoalescer(int brokerCount = 8)
        {
            if (brokerCount < 1) brokerCount = 1;
            if (brokerCount > 64) brokerCount = 64;   // 상한(고정 용량·δ=0)
            _n = brokerCount;
            _dirty = new long[_n];
            _health = new int[_n];
            _healthEpoch = new long[_n];
            _lastSeenEpoch = new long[_n];
        }

        public int BrokerCount => _n;
        public long TotalMarks => Interlocked.Read(ref _marks);
        public long RenderTriggers => Interlocked.Read(ref _renderTriggers);
        /// <summary>코얼레스로 억제된(렌더로 이어지지 않은) 마크 수 — 압축 효과 지표.</summary>
        public long Coalesced => Math.Max(0, TotalMarks - RenderTriggers);

        public BrokerHealth HealthOf(int broker)
            => (uint)broker < (uint)_n ? (BrokerHealth)Volatile.Read(ref _health[broker]) : BrokerHealth.Offline;

        // ── 핫패스: 브로커 스레드가 UI 관련 틱마다 호출(δ=0·무락) ──────────────
        /// <summary>브로커 UI 갱신 요청 병합. 어느 브로커 스레드에서 몰아쳐도 힙 0·락 0. 프레임당 1회 렌더로 압축된다.</summary>
        public void Mark(int broker)
        {
            if ((uint)broker >= (uint)_n) return;   // 범위 밖 → 조용히 무시(fail-closed·무throw)
            Interlocked.Increment(ref _dirty[broker]);
            Interlocked.Increment(ref _marks);
            Volatile.Write(ref _pending, 1);
        }

        // ── 헬스 래치(워치독이 호출): 변경 시에만 epoch 증가(팬텀 전이 0) ─────
        /// <summary>브로커 헬스 설정. 실제 변경 시에만 전이로 기록. δ=0·원자.</summary>
        public void SetHealth(int broker, BrokerHealth health)
        {
            if ((uint)broker >= (uint)_n) return;
            int prev = Interlocked.Exchange(ref _health[broker], (int)health);
            if (prev != (int)health)
            {
                Interlocked.Increment(ref _healthEpoch[broker]);
                Volatile.Write(ref _pending, 1);
            }
        }

        /// <summary>★Safe-Hold 격리 래치 — 단선/429/토큰만료 감지 시 이 라인만 즉시 격리(O(1) 원자·sticky).</summary>
        public void SafeHold(int broker) => SetHealth(broker, BrokerHealth.SafeHold);

        /// <summary>격리 해제 — 회복 확인 후 Online 복귀.</summary>
        public void Clear(int broker) => SetHealth(broker, BrokerHealth.Online);

        /// <summary>펌프 없이 즉시 렌더가 필요한지 빠른 확인(프레임 타이머가 폴링).</summary>
        public bool HasPending => Volatile.Read(ref _pending) == 1;

        // ── 렌더 펌프(단일 드레이너): 프레임 경계에서 1회 호출 ─────────────────
        /// <summary>
        /// 프레임 경계 배수. dirty 소비 + 헬스 전이 감지 → 변경분만 dest 에 채우고 채운 개수 반환(0 = 렌더 불필요).
        /// dest 는 호출부 제공 스팬(δ=0). 시작 브로커를 로터로 순환시켜 기아를 방지한다.
        /// </summary>
        public int TryDrain(Span<BrokerFrame> dest)
        {
            int count = 0;
            int cap = dest.Length < _n ? dest.Length : _n;
            int start = _rotor;
            for (int k = 0; k < _n && count < cap; k++)
            {
                int i = start + k;
                if (i >= _n) i -= _n;   // 순환(무할당 모듈러)

                long d = Interlocked.Exchange(ref _dirty[i], 0);
                long e = Interlocked.Read(ref _healthEpoch[i]);
                bool trans = e != _lastSeenEpoch[i];
                _lastSeenEpoch[i] = e;
                if (d > 0 || trans)
                {
                    var h = (BrokerHealth)Volatile.Read(ref _health[i]);
                    dest[count++] = new BrokerFrame(i, d, h, trans);
                }
            }

            _rotor = start + 1 >= _n ? 0 : start + 1;   // 로터 전진(다음 배수는 다른 브로커부터)

            if (count > 0) { Interlocked.Increment(ref _renderTriggers); }
            else { Volatile.Write(ref _pending, 0); }    // 실제 변경 없음 → 게이트 내림(idle-quiet)
            return count;
        }

        /// <summary>테스트/재초기화용 — 모든 상태·지표 리셋.</summary>
        public void Reset()
        {
            for (int i = 0; i < _n; i++)
            {
                Volatile.Write(ref _dirty[i], 0);
                Volatile.Write(ref _health[i], (int)BrokerHealth.Online);
                Volatile.Write(ref _healthEpoch[i], 0);
                _lastSeenEpoch[i] = 0;
            }
            _rotor = 0;
            Volatile.Write(ref _pending, 0);
            Volatile.Write(ref _marks, 0);
            Volatile.Write(ref _renderTriggers, 0);
        }
    }
}
