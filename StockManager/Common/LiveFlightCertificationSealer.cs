// ============================================================================
// LiveFlightCertificationSealer — v10.1.0 전방위 인증 봉인기 (Gemini 보강 과제)
//
//  물리 이중 키가 무장되기 '직전', Pre-Flight·VaR·P-Core·퀀텀 금고·카오스·SOR·데드맨·펄스·회계·페일오버 등 시스템
//  전역 불변식 항목(RingTelemetry 스냅샷에 집약)을 무할당(δ=0)으로 총괄 채점(0~100점)한다. 100점 만점 및 전 항목 PASS
//  일 때에만 무할당 포렌식 봉인(키드-FNV-1a·HMAC-lite)을 생성해 CertSeal/CertSealValid 로 발행하고 MMF 감사 링에 각인한다.
//  유효 봉인이 없으면 CertificationBlocking 이 무장을 원자적으로 즉각 차단(blockWhile tighten-only) — 전 시스템 인증 없이 무장 불가.
//
//  ★단일 프레임 스냅샷 채점: RingTelemetry 프레임(전 서브시스템 상태 집약·186+필드)을 in-ref 로 받아 ~50개 '건강' 항목을
//    δ=0 로 평가(로컬 함수 무할당·힙 0). 프레임이 곧 시스템 전역 불변식의 단일 진실원 — 별도 60개 모듈 참조 불요.
//
//  ★불변: δ=0(Certify 핫패스 힙 0·프레임 in-ref·Interlocked/Volatile·FNV 순수) · 결정론(순수 산술·벽시계 무참조) ·
//    liveArmed:false(순수 채점/봉인 — IBroker·소켓·onSend 참조 0·CertificationBlocking tighten-only) · fail-closed
//    (1개라도 실패 → 봉인 무효·무장 차단). ★키드-FNV(비밀 키 시드) — 무단 위조 방지.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>전방위 인증 봉인기(δ=0). Certify(in frame)로 전역 불변식 ~50항목 채점(0~100)·100점 시에만 키드-FNV 봉인·무장 차단.</summary>
    public sealed class LiveFlightCertificationSealer
    {
        private const ulong FnvPrime = 1099511628211UL;

        private readonly ulong _sealKey;         // HMAC-lite 비밀 키(무단 위조 방지)
        private readonly IgnitionAuditTrailRecorder? _audit;
        private readonly Func<long> _nowTicks;   // ★신선도 리스: 단조 시계(자체=Stopwatch.GetTimestamp·주입 가능·δ=0 캐시)
        private readonly long _leaseTicks;       // 인증 신선도 리스 창(초과 시 봉인 stale → fail-closed)
        private long _score, _itemMask, _failMask, _seal, _sealValid, _attempts, _certifications, _failures, _totalItems;
        private long _lastCertTicks;             // 최근 유효 인증 시각(신선도 리스 기준·sticky-open 방지)

        public LiveFlightCertificationSealer(ulong sealKey = 0xF11985EA1CE47B0DUL, IgnitionAuditTrailRecorder? audit = null,
                                             Func<long>? nowTicks = null, long tickFrequency = 0, long leaseMillis = 2000)
        {
            _sealKey = sealKey == 0UL ? 0xF11985EA1CE47B0DUL : sealKey;
            _audit = audit;
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            // ★자체시계(Stopwatch.GetTimestamp) 소스와 매칭: freq 폴백 = Stopwatch.Frequency(고정 1e7 아님·비-1e7 QPC 호스트 안전).
            long freq = tickFrequency > 0 ? tickFrequency : Stopwatch.Frequency;
            _leaseTicks = freq * Math.Max(1L, leaseMillis) / 1000L;
        }

        // ── 읽기(δ=0·락프리) ──
        public long Score          => Interlocked.Read(ref _score);
        public long ItemMask       => Interlocked.Read(ref _itemMask);
        public long FailMask       => Interlocked.Read(ref _failMask);
        public long Seal           => Interlocked.Read(ref _seal);
        public bool SealValid      => Interlocked.Read(ref _sealValid) == 1L;
        public long TotalItems     => Interlocked.Read(ref _totalItems);
        public long Certifications => Interlocked.Read(ref _certifications);
        public long Failures       => Interlocked.Read(ref _failures);
        /// <summary>★blockWhile — 유효 봉인(100/100) 없거나 신선도 리스 만료(샘플러 정지 → stale 봉인 sticky-open 방지) 시 무장 차단(tighten-only·fail-closed).</summary>
        public bool CertificationBlocking
            => Interlocked.Read(ref _sealValid) == 0L
               || (_nowTicks() - Volatile.Read(ref _lastCertTicks)) > _leaseTicks;   // ★신선도: 샘플러 stall 시 stale 봉인 fail-closed

        /// <summary>
        /// ★전역 불변식 총괄 채점(δ=0) + 봉인. RingTelemetry 프레임의 ~50개 '건강' 항목을 평가(로컬 함수 무할당) → 통과 비율로
        /// 0~100점. 전 항목 PASS(100점)일 때만 키드-FNV 봉인 생성·SealValid=1·감사 각인. 1개라도 실패 → 봉인 무효·실패 마스크 기록.
        /// </summary>
        public long Certify(in RingTelemetry f)
        {
            long mask = 0L;
            int passed = 0, idx = 0;
            // 로컬 함수(무할당·δ=0 — 델리게이트 변환 없이 직접 호출): 각 항목이 '건강'이면 통과 비트 세움.
            void Item(bool healthy) { if (idx < 64) { if (healthy) { passed++; mask |= (1L << idx); } idx++; } }

            // ── 코어 파이프라인/무장 건강 ──
            Item(f.SendErrors == 0L);
            Item(!f.HasAlert);
            Item(f.ScenarioFrozen == 0L);
            Item(f.Dropped == 0L);
            // ── 리스크/집행 ──
            Item(f.InfraCouplingClean);
            Item(!f.SorRoutingAnomaly);
            Item(!f.SorSpoofing);
            Item(f.LiquidityAuthenticityPermille >= 500L);
            Item(f.ReservationRejects == 0L);
            Item(f.MarginUtilizationRaw < 90_000_000L);
            Item(!f.RiskShieldFrozen);
            Item(!f.FabricGuardTripped);
            Item(!f.MmfFabricCorrupted);
            Item(!f.ToxicFlowFrozen);
            Item(!f.BlastRadiusActive || f.BlastRadiusCeilingRaw > 0L);
            // ── 카오스 방어선 ──
            Item(!f.ChaosAnomaly);
            Item(!f.ChaosSafeHolding);
            Item(f.ChaosRecoveryState == 0L);
            Item(f.ChaosLivelockEscapes == 0L);
            Item(f.ChaosTornStateBlocks == 0L);
            Item(f.ChaosFaultMask == 0L);
            // ── 점화/키 ──
            Item(!f.IgnitionVetoed);
            Item(!f.HardwareKeyDisconnected);
            Item(f.IgnitionLastVetoCode == 0L);
            // ── 보안/무결성 ──
            Item(!f.VaultBreach);
            Item(!f.AntiHookTampered);
            Item(f.SecureZeroVerifyFailures == 0L);
            Item(f.HoneyDecoyTripped == 0L);
            // ── 하드웨어/커널 ──
            Item(!f.KernelHolding);
            Item(!f.IoFrozen);
            Item(!f.RioFallback);
            Item(!f.PlaybackFaulted);
            // ── 세션/복구/페일오버 ──
            Item(!f.SessionComa);
            Item(f.FailoverActiveChannel == 0L);
            Item(!f.ChannelsDown);
            Item(!f.BlackSwanFrozen);
            Item(f.WatchdogHealthy);
            Item(!f.DeadmanReverted);
            Item(!f.KillSwitchTripped);
            // ── 회계/원장 ──
            Item(!f.AccDesyncFrozen);
            Item(!f.LedgerOverflowAlert);
            Item(f.ZombieAssemblyCount == 0L);
            Item(f.SealedLaneCount == 0L);
            // ── 알파/전략 ──
            Item(!f.AlphaFactoryAmber);
            Item(!f.SentimentBreakerActive);
            Item(!f.BlackboxDumpFailed);
            // ── 시스템 가동 증거(idle-only fail-open 방지) ──
            Item(f.Published > 0L);

            int n = idx;
            long score = n == 0 ? 0L : (long)passed * 100L / n;
            long full  = n >= 64 ? -1L : ((1L << n) - 1L);
            long nonce = Interlocked.Increment(ref _attempts);
            Volatile.Write(ref _score, score);
            Volatile.Write(ref _itemMask, mask);
            Volatile.Write(ref _totalItems, n);
            if (n > 0 && passed == n)   // ★100점·전 항목 PASS
            {
                long seal = ComputeSeal(score, mask, nonce, _sealKey);
                Volatile.Write(ref _seal, seal);
                Volatile.Write(ref _failMask, 0L);
                Volatile.Write(ref _sealValid, 1L);
                Volatile.Write(ref _lastCertTicks, _nowTicks());   // ★신선도 리스 갱신(유효 인증 시각 각인·sticky-open 방지)
                Interlocked.Increment(ref _certifications);
                _audit?.Record(IgnitionEventKind.Certification, seal, score);
            }
            else
            {
                Volatile.Write(ref _failMask, ~mask & full);
                Volatile.Write(ref _sealValid, 0L);
                Interlocked.Increment(ref _failures);
            }
            return score;
        }

        /// <summary>키드-FNV-64 봉인 재계산(δ=0·순수). 포렌식 검증용 — 동일 (score,mask,nonce,key)면 동일 봉인.</summary>
        public static long ComputeSeal(long score, long itemMask, long nonce, ulong key)
        {
            unchecked
            {
                ulong h = key;                              // ★비밀 키 시드 — 무단 위조 방지
                h = (h ^ (ulong)score) * FnvPrime;
                h = (h ^ (ulong)itemMask) * FnvPrime;
                h = (h ^ (ulong)nonce) * FnvPrime;
                h = (h ^ key) * FnvPrime;
                return (long)h;
            }
        }
    }
}
