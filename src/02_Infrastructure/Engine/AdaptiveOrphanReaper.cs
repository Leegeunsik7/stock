// ============================================================================
// AdaptiveOrphanReaper — v5.1.0 ★Think Beyond Gemini: 적응형 고아주문 사냥꾼 & 자본 회수
//
//  ── 아키텍처 논증(제미나이 초월) ────────────────────────────────────────────
//  제미나이 힌트 A(고정 타임아웃 사냥꾼)·B(시장정지 정적 홀드)는 모두 '탐지 후 정지'다. 본 설계는 '자율 힐링'을 추가한다:
//  브로커 서버 대기열 목졸림(Order Queue Saturation)으로 '주문 송신은 성공했으나 체결/거부 응답이 영원히 안 오는' 랙에서
//  자본이 허공에 묶이는 가장 파멸적 리스크를, ① 고정이 아닌 ★'실측 RTT 분포 적응형' 데드라인(mean+Kσ, δ=0 롤링 통계)으로
//  고아를 선언하고, ② 진상 조회(reconcile)로 실제 상태를 확정해 자본을 '회수(heal)'하며 — 미체결이면 예약 자본 해제, 체결이면
//  장부 확정 — ③ 조회조차 재수확(reap) 데드라인 내 불확정이면 fail-closed ForceFreeze 로 닫는다. 고정 타임아웃은 저지연장에선
//  조기 오탐, 고지연장에선 늦은 대응이지만, 적응형은 '지금 이 세션의 실제 왕복 분포'에 스스로 맞춰 오탐·지연을 동시 최소화한다.
//
//  ── 불변식 ──────────────────────────────────────────────────────────────────
//  liveArmed:false — 리퍼는 관측·조회·회수 신호·동결만. 실주문 사출 0(reconcile 조회는 읽기, 회수는 내부 예약 해제 신호).
//  δ=0 — Register/Resolve 핫경로: 고정 슬롯 테이블 CAS + 고정 long[128] RTT 링(무할당). Sweep 은 콜드(백그라운드). 결정론(주입 시계).
//  ★체결/거부/무응답 3분기 — OrderRejected 는 FillReceived(FillQty=0)로 위장 도착하므로 '응답 도착=고아 해제'로 보되
//  미체결(NotFilled)/체결(Filled)/불확정(Unknown)을 명시 구분(freeze-storm·유령대금 회피).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>고아 주문 진상 조회 결과. Unknown 이 재수확 데드라인 초과 시 fail-closed.</summary>
    public enum OrphanVerdict : byte { NotFilled = 0, Filled = 1, Unknown = 2 }

    /// <summary>발주 후 종단 응답 무도착(허공 묶임)을 실측 RTT 분포 적응형 데드라인으로 탐지→조회→자본 회수/동결.</summary>
    public sealed class AdaptiveOrphanReaper
    {
        private const int RttCap = 128;
        private const int MinSamples = 8;

        private readonly OrderAttestationService? _att;
        private readonly Func<long, OrphanVerdict>? _reconcile;   // 진상 조회(주입·읽기 전용). 미주입 시 Unknown(fail-closed).
        private readonly Action<long>?              _reclaim;     // 미체결 확정 시 예약 자본 해제 신호(주입)
        private readonly Func<long>                 _nowTicks;
        private readonly long                       _tickFreq;
        private readonly long                       _fallbackDeadlineTicks;  // RTT 표본 부족 시 폴백 데드라인
        private readonly int                        _kSigma;                 // 적응형 데드라인 = mean + K·σ
        private readonly long                       _reapMultiple;           // 재수확(동결) 데드라인 = 적응형 × 이 배수

        // 고정 인-플라이트 테이블(δ=0 Register/Resolve·CAS 슬롯).
        private struct Slot { public long OrderId; public long DispatchTicks; public int State; }   // State: 0 Free, 1 InFlight
        private readonly Slot[] _slots;

        // δ=0 RTT 롤링 통계(단일 라이터=Resolve). ★_rttSeq seqlock 으로 Int128 _rSumSq 의 reader(Sweep)/writer(Resolve) tear 봉쇄.
        private readonly long[] _rtt = new long[RttCap];
        private int   _rIdx, _rCount;
        private long  _rSum;
        private Int128 _rSumSq;
        private long  _rttSeq;   // seqlock 버전(짝=안정·홀=쓰기중)

        // 슬롯 상태: 0 Free · 1 InFlight(발행 완료) · 2 Claiming(등록 중·필드 미기록) · 3 Reaping(재수확 중). Sweep/Resolve 는 1 에서만 작동.
        private const int Free = 0, InFlight = 1, Claiming = 2, Reaping = 3;

        private long _registered, _resolved, _orphans, _reclaimed, _freezes, _tableFull, _lastDeadlineTicks;

        public AdaptiveOrphanReaper(OrderAttestationService? attestation = null, Func<long, OrphanVerdict>? reconcile = null,
                                    Action<long>? reclaim = null, Func<long>? nowTicks = null, long tickFrequency = 0,
                                    int capacity = 1024, int kSigma = 4, long reapMultiple = 3, long fallbackDeadlineMs = 5000)
        {
            _att        = attestation;
            _reconcile  = reconcile;
            _reclaim    = reclaim;
            _nowTicks   = nowTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;
            _tickFreq   = tickFrequency > 0 ? tickFrequency : System.Diagnostics.Stopwatch.Frequency;
            int cap = capacity < 8 ? 8 : capacity;
            _slots = new Slot[cap];
            _kSigma = kSigma < 1 ? 1 : kSigma;
            _reapMultiple = reapMultiple < 1 ? 1 : reapMultiple;
            _fallbackDeadlineTicks = (long)(fallbackDeadlineMs / 1000.0 * _tickFreq);
        }

        public long Registered  => Interlocked.Read(ref _registered);
        public long Resolved    => Interlocked.Read(ref _resolved);
        public long Orphans     => Interlocked.Read(ref _orphans);
        public long Reclaimed   => Interlocked.Read(ref _reclaimed);
        public long Freezes     => Interlocked.Read(ref _freezes);
        public long TableFull   => Interlocked.Read(ref _tableFull);
        public long LastAdaptiveDeadlineTicks => Interlocked.Read(ref _lastDeadlineTicks);
        public int  InFlightCount { get { int c = 0; for (int i = 0; i < _slots.Length; i++) if (Volatile.Read(ref _slots[i].State) == InFlight) c++; return c; } }

        /// <summary>발주 시 등록(δ=0). 빈 슬롯 CAS 점유. 테이블 만원이면 false(추적 불가 — 상위가 판단).</summary>
        public bool Register(long orderId, long dispatchTicks)
        {
            if (orderId < 0) return false;
            for (int i = 0; i < _slots.Length; i++)
            {
                // ★Free→Claiming 원자 점유 후 필드 기록, 마지막에 InFlight 발행(publish-after-populate). Sweep/Resolve 는 InFlight 만 봄 → stale 필드 판독 봉쇄.
                if (Interlocked.CompareExchange(ref _slots[i].State, Claiming, Free) == Free)
                {
                    Volatile.Write(ref _slots[i].OrderId, orderId);
                    Volatile.Write(ref _slots[i].DispatchTicks, dispatchTicks);
                    Volatile.Write(ref _slots[i].State, InFlight);   // 발행 — 이 시점 이후에만 필드가 유효
                    Interlocked.Increment(ref _registered);
                    return true;
                }
            }
            Interlocked.Increment(ref _tableFull);
            return false;   // 만원 — 인-플라이트 과다(그 자체가 신호). 상위/Sweep 이 처리.
        }

        /// <summary>종단 응답(체결/거부) 도착 시 해제(δ=0). RTT 표본 적재. ★InFlight→Free CAS(Sweep 재수확과 상호배제).</summary>
        public void Resolve(long orderId)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (Volatile.Read(ref _slots[i].State) == InFlight && Volatile.Read(ref _slots[i].OrderId) == orderId)
                {
                    long dt = Volatile.Read(ref _slots[i].DispatchTicks);
                    if (Interlocked.CompareExchange(ref _slots[i].State, Free, InFlight) == InFlight)   // 여전히 InFlight 일 때만 해제
                    {
                        long rtt = _nowTicks() - dt;
                        if (rtt >= 0) PushRtt(rtt);
                        Interlocked.Increment(ref _resolved);
                    }
                    return;   // Sweep 가 이미 Reaping 클레임했으면(CAS 실패) 스킵 — 리퍼가 처리
                }
            }
        }

        /// <summary>현재 적응형 데드라인(ticks) = mean + K·σ. 표본 부족 시 폴백. ★seqlock 일관 독출(Int128 tear 봉쇄).</summary>
        public long AdaptiveDeadlineTicks()
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                long v1 = Volatile.Read(ref _rttSeq);
                if ((v1 & 1L) != 0L) continue;                 // 홀 = 쓰기중 → 재시도
                int n = _rCount;
                long sum = _rSum;
                Int128 sumSq = _rSumSq;
                long v2 = Volatile.Read(ref _rttSeq);
                if (v1 != v2) continue;                         // 도중 쓰기 → 재시도
                if (n < MinSamples) return _fallbackDeadlineTicks;
                long mean = sum / n;
                Int128 var = sumSq / n - (Int128)mean * mean;
                long sigma = var > 0 ? (long)ISqrt(var) : 0;
                long d = mean + _kSigma * sigma;
                return d < 1 ? 1 : d;
            }
            return _fallbackDeadlineTicks;   // 경합 지속 — 폴백(fail-safe)
        }

        /// <summary>
        /// 주기 스윕(콜드·백그라운드) — 인-플라이트 각 주문의 나이가 적응형 데드라인 초과면 고아 선언 → 진상 조회:
        /// NotFilled→예약 자본 회수·해제(heal), Filled→장부 확정·해제(heal), Unknown→재수확 데드라인 초과 시 fail-closed 동결.
        /// </summary>
        public void SweepOnce()
        {
            long now = _nowTicks();
            long deadline = AdaptiveDeadlineTicks();
            Interlocked.Exchange(ref _lastDeadlineTicks, deadline);
            long reapDeadline = deadline * _reapMultiple;

            for (int i = 0; i < _slots.Length; i++)
            {
                if (Volatile.Read(ref _slots[i].State) != InFlight) continue;
                long age = now - Volatile.Read(ref _slots[i].DispatchTicks);
                if (age <= deadline) continue;   // 아직 데드라인 이내 — 정상 대기

                // ★InFlight→Reaping 원자 클레임 — 실패 = Resolve 가 먼저 해제(응답 도착) → 스킵(이중 처리·유령 회수 봉쇄).
                if (Interlocked.CompareExchange(ref _slots[i].State, Reaping, InFlight) != InFlight) continue;
                long orderId = Volatile.Read(ref _slots[i].OrderId);   // Reaping 클레임 후 판독 — 발행된 안정 값
                Interlocked.Increment(ref _orphans);
                OrphanVerdict v;
                try { v = _reconcile is { } rc ? rc(orderId) : OrphanVerdict.Unknown; }
                catch { v = OrphanVerdict.Unknown; }

                if (v == OrphanVerdict.NotFilled)
                {
                    try { _reclaim?.Invoke(orderId); } catch { }   // 예약 자본 회수(heal)
                    Interlocked.Increment(ref _reclaimed);
                    Volatile.Write(ref _slots[i].State, Free);
                }
                else if (v == OrphanVerdict.Filled)
                {
                    Volatile.Write(ref _slots[i].State, Free);     // 장부 확정은 회계 경로가 — 여기선 추적 해제(heal)
                }
                else // Unknown
                {
                    if (age > reapDeadline)
                    {
                        // ★조회조차 불확정 + 재수확 데드라인 초과 — 자본 허공 묶임 확정 → fail-closed(처리량보다 자본 보호).
                        Interlocked.Increment(ref _freezes);
                        try { _att?.ForceFreeze($"AdaptiveOrphanReaper: 고아 주문 {orderId} 진상 불확정 + 재수확 데드라인({reapDeadline}ticks) 초과 — 자본 묶임 fail-closed"); } catch { }
                        Volatile.Write(ref _slots[i].State, Free);
                    }
                    else
                    {
                        Volatile.Write(ref _slots[i].State, InFlight);   // 재수확 보류 — InFlight 로 복귀(다음 스윕 재조회)
                    }
                }
            }
        }

        private void PushRtt(long rtt)
        {
            // ★seqlock: 홀(쓰기중)→Int128/합 갱신→짝(완료). AdaptiveDeadlineTicks 가 tear 없이 일관 독출.
            Interlocked.Increment(ref _rttSeq);   // → 홀
            if (_rCount == RttCap) { long old = _rtt[_rIdx]; _rSum -= old; _rSumSq -= (Int128)old * old; }
            else _rCount++;
            _rtt[_rIdx] = rtt; _rSum += rtt; _rSumSq += (Int128)rtt * rtt;
            _rIdx = (_rIdx + 1) % RttCap;
            Interlocked.Increment(ref _rttSeq);   // → 짝
        }

        private static Int128 ISqrt(Int128 n)
        {
            if (n <= 0) return 0;
            Int128 x = n, y = (x + 1) / 2;
            while (y < x) { x = y; y = (x + n / x) / 2; }
            return x;
        }
    }
}
