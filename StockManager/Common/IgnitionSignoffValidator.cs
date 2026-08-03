// ============================================================================
// IgnitionSignoffValidator — v10.1.0 실전 운용: 사전 점화 서명 체정기 (Gemini 보강 과제)
//
//  물리 이중 키(ValveWired ∧ LiveArmingWired)가 회전하기 '직전', 실자본 집행에 필수인 호스트/하드웨어/보안 사전
//  조건을 무할당(δ=0)으로 채점한다: ①OS 타이머 해상도 튜닝 ②P-Core 물리 핀 ③MMF 감사 링 무결 ④퀀텀 금고 무장·무침해
//  ⑤Pre-Flight 총점 ⑥워치독 생존 ⑦인프라 결합도 ⑧시스템 가동. 단 1개라도 미달이면 SignoffBlocking 이 무장 회전을
//  하드-차단(Fail-Closed Veto) — 운용자가 키를 돌려도 소프트웨어가 서명 없이는 실전 무장을 부여하지 않는다.
//
//  ★정직 고지(항목 강도): ①②④⑤⑦⑧ 은 '양성 배선' 항목 — 타이머 실측/핀>0/금고 무장/Pre-Flight≥임계/결합도∈[1,120]/Published>0
//    은 서브시스템이 실제 배선·구동돼야만 PASS(미배선=미달=차단). 반면 ③(!MmfFabricCorrupted)·⑥(WatchdogHealthy) 은 'alarm-부재'
//    항목이라 해당 서브시스템 미배선 시 default-0 이 무-알람으로 읽혀 vacuous PASS 가능(프레임에 양성-liveness 신호 부재). 이 둘은
//    RUNBOOK 1단계 운용자 육안 확인이 백스톱하고, 양성 항목(①②④⑤)이 미배선 호스트를 이미 fail-closed 차단하므로 전체 서명이 미배선
//    시스템을 통과시키진 않는다. 완전 양성-liveness 각인은 후속 과제로 고지.
//
//  ★단일 프레임 스냅샷 채점(RingTelemetry 프레임 in-ref) + 키드-FNV 포렌식 봉인 + ★신선도 리스(샘플러 정지 시 stale
//    서명 sticky-open 방지 — 마지막 유효 서명 시각 + 리스 만료 → fail-closed). SignoffBlocking 은 tighten-only.
//
//  ★불변: δ=0(Certify 핫패스 힙 0·프레임 in-ref·Interlocked/Volatile·FNV 순수) · 결정론(순수 산술·주입 시계) ·
//    liveArmed:false(순수 채점/봉인 — IBroker·소켓·onSend 참조 0 · SignoffBlocking tighten-only) · fail-closed
//    (1개라도 미달 or 리스 만료 → 서명 무효·무장 차단).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>사전 점화 서명 체정기(δ=0). Certify(in frame)로 호스트/하드웨어/보안 8항목 채점·전 항목 PASS 시에만 키드-FNV 서명·미달 시 무장 하드 차단.</summary>
    public sealed class IgnitionSignoffValidator
    {
        private const ulong FnvPrime = 1099511628211UL;

        private readonly ulong _sealKey;
        private readonly IgnitionAuditTrailRecorder? _audit;
        private readonly Func<long> _nowTicks;           // ★신선도 리스: 단조 시계(자체=Stopwatch.GetTimestamp·주입 가능)
        private readonly long _leaseTicks;               // 서명 신선도 리스(초과 시 stale → fail-closed)
        private readonly long _timerBudgetNanos;         // OS 타이머 해상도 예산(이 이하만 PASS·튜닝 목표 ≤0.5ms)
        private readonly long _preFlightThreshold;       // Pre-Flight 총점 하한
        private long _score, _itemMask, _failMask, _seal, _passed, _attempts, _signoffs, _rejections, _totalItems, _lastPassTicks;

        public IgnitionSignoffValidator(ulong sealKey = 0x51617D0FF5160FF1UL, IgnitionAuditTrailRecorder? audit = null,
                                        Func<long>? nowTicks = null, long tickFrequency = 0, long leaseMillis = 2000,
                                        long timerBudgetNanos = 1_000_000L, long preFlightThreshold = 100L)
        {
            _sealKey = sealKey == 0UL ? 0x51617D0FF5160FF1UL : sealKey;
            _audit = audit;
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            long freq = tickFrequency > 0 ? tickFrequency : Stopwatch.Frequency;   // ★자체시계 소스 매칭(비-1e7 QPC 안전)
            _leaseTicks = freq * Math.Max(1L, leaseMillis) / 1000L;
            _timerBudgetNanos = Math.Max(1L, timerBudgetNanos);
            _preFlightThreshold = Math.Clamp(preFlightThreshold, 1L, 100L);
        }

        // ── 읽기(δ=0·락프리) ──
        public long Score        => Interlocked.Read(ref _score);
        public long ItemMask     => Interlocked.Read(ref _itemMask);
        public long FailMask     => Interlocked.Read(ref _failMask);
        public long Seal         => Interlocked.Read(ref _seal);
        public long TotalItems   => Interlocked.Read(ref _totalItems);
        public long Signoffs     => Interlocked.Read(ref _signoffs);
        public long Rejections   => Interlocked.Read(ref _rejections);
        /// <summary>전 항목 PASS ∧ 신선(리스 내). 이 값이 true 여야 물리 이중 키 회전이 실전 무장으로 이어진다.</summary>
        public bool SignoffPassed => !SignoffBlocking;
        /// <summary>★blockWhile — 서명 미완(미달) 또는 신선도 리스 만료(샘플러 정지 sticky-open 방지) 시 무장 하드 차단(tighten-only·fail-closed).</summary>
        public bool SignoffBlocking
            => Interlocked.Read(ref _passed) == 0L
               || (_nowTicks() - Volatile.Read(ref _lastPassTicks)) > _leaseTicks;

        /// <summary>사전 점화 8항목 채점(δ=0) + 서명. 전 항목 PASS 시에만 키드-FNV 서명·신선도 리스 갱신·감사 각인. 1개라도 미달 → 서명 무효·실패 마스크.</summary>
        public long Certify(in RingTelemetry f)
        {
            long mask = 0L;
            int passed = 0, idx = 0;
            void Item(bool healthy) { if (idx < 64) { if (healthy) { passed++; mask |= (1L << idx); } idx++; } }

            Item(f.HostTimerResNanos > 0L && f.HostTimerResNanos <= _timerBudgetNanos);   // ① OS 타이머 해상도 튜닝(양성: 실측 ≤예산)
            Item(f.CpuPinActive);                                                          // ② P-Core 물리 핀(양성: 핀 스레드>0 ∧ P-Core>0)
            Item(!f.MmfFabricCorrupted);                                                   // ③ MMF 감사 링 무결(alarm-부재·아래 정직 고지 참조)
            Item(f.QuantumVaultActive && !f.VaultBreach);                                  // ④ 퀀텀 금고 ★무장(양성) ∧ 무침해 — 미배선(미무장)=미달(fail-closed)
            Item(f.PreFlightScore >= _preFlightThreshold);                                 // ⑤ Pre-Flight 총점(양성: ≥임계)
            Item(f.WatchdogHealthy);                                                       // ⑥ 워치독 생존
            Item(f.InfraCouplingClean);                                                    // ⑦ 인프라 결합도
            Item(f.Published > 0L);                                                        // ⑧ 시스템 가동(idle fail-open 방지)

            int n = idx;
            long score = n == 0 ? 0L : (long)passed * 100L / n;
            long full  = n >= 64 ? -1L : ((1L << n) - 1L);
            long nonce = Interlocked.Increment(ref _attempts);
            Volatile.Write(ref _score, score);
            Volatile.Write(ref _itemMask, mask);
            Volatile.Write(ref _totalItems, n);
            if (n > 0 && passed == n)   // ★전 항목 PASS
            {
                long seal = ComputeSeal(score, mask, nonce, _sealKey);
                Volatile.Write(ref _seal, seal);
                Volatile.Write(ref _failMask, 0L);
                Volatile.Write(ref _passed, 1L);
                Volatile.Write(ref _lastPassTicks, _nowTicks());   // ★신선도 리스 갱신(유효 서명 시각)
                Interlocked.Increment(ref _signoffs);
                _audit?.Record(IgnitionEventKind.Signoff, seal, score);
            }
            else
            {
                Volatile.Write(ref _failMask, ~mask & full);
                Volatile.Write(ref _passed, 0L);
                Interlocked.Increment(ref _rejections);
            }
            return score;
        }

        /// <summary>키드-FNV-64 서명 재계산(δ=0·순수·포렌식 검증용).</summary>
        public static long ComputeSeal(long score, long itemMask, long nonce, ulong key)
        {
            unchecked
            {
                ulong h = key;
                h = (h ^ (ulong)score)    * FnvPrime;
                h = (h ^ (ulong)itemMask) * FnvPrime;
                h = (h ^ (ulong)nonce)    * FnvPrime;
                h = (h ^ key)             * FnvPrime;
                return (long)h;
            }
        }
    }
}
