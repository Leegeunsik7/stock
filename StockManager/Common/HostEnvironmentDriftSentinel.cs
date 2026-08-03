// ============================================================================
// HostEnvironmentDriftSentinel — v10.1.0 실전 운용: ★Think Beyond Gemini — 라이브 호스트 환경 드리프트 센티널
//   "서명 시점의 호스트는 튜닝돼 있었다. 문제는 '라이브 가동 중' 그 상태가 조용히 변질되는 것이다."
//
//  ── 왜 제미나이의 '즉시 강등' 센티널을 초월하는가 ────────────────────────────────
//  제미나이는 "QPC 스케일링/타이머 해상도/C-State 재활성화 징후 포착 즉시 liveArmed:false 강등"을 제안했다. 그러나
//  (a) '절대 임계'는 호스트마다 달라 오탐/미탐이고 (b) '즉시' 강등은 단발 스케줄링 blip(GC·순간 부하)에 무장을 오취소해
//  운용을 마비시킨다. 이 센티널은 두 결함을 ★'봉인된 베이스라인 + 히스테리시스'로 초월한다: ①운용자가 서명(signoff)한
//  '바로 그 순간'의 호스트 타이밍(OS 타이머 해상도·측정 지터)을 SealBaseline 으로 봉인 — 절대 임계가 아니라 '내가
//  서명한 호스트로부터의 divergence'라는 호스트-불가지(host-agnostic) 불변식으로 판정. ②단발 blip 은 무시하고 divergence 가
//  '연속 N회 확인(confirmCount)'돼야만 DriftTripped 원자 래치 — 진짜 지속 변질(타이머 해상도 revert·C-State 재활성화 →
//  지터 지속 상승)만 잡는다. 트립 = liveArmed:false tighten-only(비가역·재시작/재서명 전 무장 불가).
//
//  ★타이머 해상도는 NtQueryTimerResolution(LibraryImport·SYSLIB1054 회피)로 실측. 산출은 콜드(샘플러 200ms)·Probe 판정은
//    δ=0 순수 비교. 비-Windows/실패 시 0 반환(측정 불가 → 드리프트 무판정·signoff 항목이 별도 fail-closed 처리).
//
//  ★불변: δ=0(Probe/판정 핫패스 힙 0·Interlocked/CAS) · 결정론(순수 정수·판정은 입력 측정에만 의존) · liveArmed:false
//    (순수 판정 — IBroker·소켓 참조 0 · DriftBlocking tighten-only) · fail-closed(지속 divergence → 무장 하향).
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>라이브 호스트 환경 드리프트 센티널(δ=0). SealBaseline(서명 시점 봉인)·Probe(연속 divergence 확인 시 원자 강등).</summary>
    public sealed partial class HostEnvironmentDriftSentinel
    {
        private readonly long _timerSlackMult;       // 타이머 해상도 열화 허용 배수(baseline × mult 초과 = 드리프트)
        private readonly long _timerAbsSlackNanos;   // 절대 슬랙(작은 baseline 오탐 방지)
        private readonly long _jitterSlackPermille;  // 지터율 상승 허용(baseline + slack 초과 = 드리프트)
        private readonly long _confirmCount;         // 연속 divergence 확인 횟수(히스테리시스·단발 blip 오강등 방지)
        private readonly IgnitionAuditTrailRecorder? _audit;
        private long _sealed, _baseTimerResNanos, _baseJitterPermille, _consecutive, _divergences, _driftTripped, _lastTimerResNanos;

        public HostEnvironmentDriftSentinel(long timerSlackMult = 4L, long timerAbsSlackNanos = 200_000L,
                                            long jitterSlackPermille = 200L, long confirmCount = 3L,
                                            IgnitionAuditTrailRecorder? audit = null)
        {
            _timerSlackMult      = Math.Max(2L, timerSlackMult);
            _timerAbsSlackNanos  = Math.Max(0L, timerAbsSlackNanos);
            _jitterSlackPermille = Math.Max(0L, jitterSlackPermille);
            _confirmCount        = Math.Max(1L, confirmCount);
            _audit = audit;
        }

        // ── 읽기(δ=0·락프리) ──
        public bool Sealed                => Volatile.Read(ref _sealed) == 1L;
        public long BaselineTimerResNanos => Interlocked.Read(ref _baseTimerResNanos);
        public long LastTimerResNanos     => Interlocked.Read(ref _lastTimerResNanos);
        public long Divergences           => Interlocked.Read(ref _divergences);
        public bool DriftTripped          => Volatile.Read(ref _driftTripped) == 1L;
        /// <summary>★blockWhile — 서명 호스트로부터 지속 드리프트 포착 시 무장 하향(tighten-only·비가역).</summary>
        public bool DriftBlocking         => Volatile.Read(ref _driftTripped) == 1L;

        /// <summary>서명(signoff) 시점 호스트 타이밍 베이스라인 봉인. 이후 이 상태로부터의 divergence 를 드리프트로 판정.</summary>
        public void SealBaseline(long timerResNanos, long jitterPermille)
        {
            Volatile.Write(ref _baseTimerResNanos, Math.Max(0L, timerResNanos));
            Volatile.Write(ref _baseJitterPermille, Math.Max(0L, jitterPermille));
            Interlocked.Exchange(ref _consecutive, 0L);
            Volatile.Write(ref _sealed, 1L);
        }

        /// <summary>
        /// ★호스트 타이밍 probe(δ=0). 봉인 후, 타이머 해상도 열화(baseline×배수 초과) 또는 지터율 지속 상승이 연속 confirmCount
        /// 회 확인되면 DriftTripped 원자 래치(단발 blip 은 카운터 리셋으로 무시). 봉인 전/이미 트립 시 무판정.
        /// </summary>
        public void Probe(long timerResNanos, long jitterPermille)
        {
            Volatile.Write(ref _lastTimerResNanos, timerResNanos);
            if (Volatile.Read(ref _sealed) == 0L || Volatile.Read(ref _driftTripped) == 1L) return;

            long baseT = Volatile.Read(ref _baseTimerResNanos);
            long baseJ = Volatile.Read(ref _baseJitterPermille);
            long timerCeil = Math.Max(baseT * _timerSlackMult, baseT + _timerAbsSlackNanos);
            bool timerDrift  = baseT > 0L && timerResNanos > timerCeil;             // 타이머 해상도 열화(revert)
            bool jitterDrift = jitterPermille > baseJ + _jitterSlackPermille;       // 지터 지속 상승(C-State 재활성화 징후)

            if (timerDrift || jitterDrift)
            {
                Interlocked.Increment(ref _divergences);
                if (Interlocked.Increment(ref _consecutive) >= _confirmCount
                    && Interlocked.CompareExchange(ref _driftTripped, 1L, 0L) == 0L)
                    _audit?.Record(IgnitionEventKind.HostDrift, timerResNanos, jitterPermille);   // ★확정 드리프트 각인
            }
            else
            {
                Interlocked.Exchange(ref _consecutive, 0L);   // 연속 아님 → 리셋(히스테리시스)
            }
        }

        // ── Native (LibraryImport 소스생성 — SYSLIB1054 회피, TreatWarningsAsErrors 준수) ──
        [LibraryImport("ntdll.dll")]
        private static partial int NtQueryTimerResolution(out uint minimumResolution, out uint maximumResolution, out uint currentResolution);

        /// <summary>현재 OS 타이머 해상도(ns). NtQueryTimerResolution(100ns 단위)×100. 비-Windows/실패 → 0(측정 불가).</summary>
        public static long CurrentTimerResolutionNanos()
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return 0L;
                int status = NtQueryTimerResolution(out _, out _, out uint current);
                return status == 0 ? (long)current * 100L : 0L;
            }
            catch { return 0L; }
        }
    }
}
