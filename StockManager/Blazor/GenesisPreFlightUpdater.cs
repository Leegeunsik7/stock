// ============================================================================
// GenesisPreFlightUpdater — v7.4.0 Pre-Flight 재인증 콜드 구동기
//
//  GenesisPreFlightCertifier 는 '순수 함수'다 — 입력만으로 채점한다. 이 구동기는 그 입력을 시스템 전역 서브시스템
//  (레이턴시/스로틀/CPU 그리드/서멀/VaR/I-O 방어선/증거금)에서 주기적으로 수집해 Certify() 를 호출하고, 인증 통과
//  ∧ 무장 상태일 때만 AtomicArmingLease.Renew() 로 임대를 갱신한다(재인증이 끊기면 임대가 자연 만료 → 자동 하향).
//  디스크 잔여 용량(DriveInfo)은 콜드 파일시스템 호출이므로 200ms 텔레메트리 샘플러가 아닌 이 BelowNormal 전용
//  스레드에서 성기게(≈5s) 캐시 샘플링해 핫패스 지터와 분리한다.
//
//  ★불변: 수집/Certify 는 콜드 백그라운드(트레이딩 핫패스 무접촉·fail-safe). Certify 자체는 δ=0. 결정론(주입 시계).
//    liveArmed:false(순수 관측/채점 — IBroker·소켓·onSend 참조 0. 인증은 blockWhile 로만 무장을 '거둘' 뿐 부여 불가).
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace StockManager.Blazor
{
    /// <summary>Pre-Flight 재인증 콜드 구동기(BelowNormal 전용 스레드·fail-safe). Start/Dispose. Certify + 조건부 lease.Renew.</summary>
    public sealed class GenesisPreFlightUpdater : IDisposable
    {
        private readonly GenesisPreFlightCertifier _certifier;
        private readonly AtomicArmingLease         _lease;
        private readonly LatencyTelemetryChannel?  _latency;
        private readonly AdaptiveOrderThrottle?    _throttle;
        private readonly CpuAffinityPinningGrid?   _cpuGrid;
        private readonly ThermalFrequencyJitterGuard? _thermal;
        private readonly PortfolioRiskVaREngine?   _varEngine;
        private readonly IoBackpressureFirewall?   _ioFirewall;
        private readonly Func<long>?               _marginHeadroomRaw;
        private readonly Func<bool>?               _isArmed;
        private readonly Func<long>                _nowTicks;
        private readonly string?                   _diskProbePath;
        private readonly int                       _intervalMs;
        private readonly long                      _rttMaxMicros;

        private Thread? _thread;
        private readonly ManualResetEventSlim _stop = new(false);
        private int _started, _disposed;
        private long _cachedDiskFreeMB = long.MaxValue;   // 미측정 초기값 = 통과(측정 실패 시 디스크 항목 무해)
        private long _lastDiskSampleMs;
        private bool _diskSampled;                        // '미측정' vs '측정됨(성공/실패)' 구분 — 실패도 5s 스로틀 준수(재프로브 폭주 방지)

        public GenesisPreFlightUpdater(GenesisPreFlightCertifier certifier, AtomicArmingLease lease,
                                       LatencyTelemetryChannel? latency = null, AdaptiveOrderThrottle? throttle = null,
                                       CpuAffinityPinningGrid? cpuGrid = null, ThermalFrequencyJitterGuard? thermal = null,
                                       PortfolioRiskVaREngine? varEngine = null, IoBackpressureFirewall? ioFirewall = null,
                                       Func<long>? marginHeadroomRaw = null, Func<bool>? isArmed = null,
                                       string? diskProbePath = null, Func<long>? nowTicks = null,
                                       int intervalMs = 250, long rttMaxMicros = 5000)
        {
            _certifier = certifier ?? throw new ArgumentNullException(nameof(certifier));
            _lease     = lease     ?? throw new ArgumentNullException(nameof(lease));
            _latency = latency; _throttle = throttle; _cpuGrid = cpuGrid; _thermal = thermal;
            _varEngine = varEngine; _ioFirewall = ioFirewall; _marginHeadroomRaw = marginHeadroomRaw; _isArmed = isArmed;
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            _diskProbePath = diskProbePath;
            _intervalMs = Math.Clamp(intervalMs, 50, 5000);
            _rttMaxMicros = rttMaxMicros;
        }

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "GenesisPreFlight", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        private void Loop()
        {
            while (!_stop.Wait(_intervalMs))
            {
                try { Recertify(); } catch { /* 콜드 수집 실패 → 다음 주기 재시도(핫패스 무영향) */ }
            }
        }

        /// <summary>1회 재인증(콜드). 서브시스템에서 입력 수집 → Certify → 인증∧무장 시 lease.Renew. 테스트 직접 호출 가능.</summary>
        public void Recertify()
        {
            // ① 브로커 소켓
            bool ipcWired = false; long rttMicros = long.MaxValue;
            if (_latency is { } lat && lat.TryRead(out var snap))
            {
                ipcWired = snap.IpcWired;
                rttMicros = ipcWired ? snap.CurrentIpcRttTicks / 10L : long.MaxValue;   // 100ns ticks → µs
            }
            long rateHeadroom = 1000L;   // 스로틀 미배선 = 무제한(만공)
            if (_throttle is { } th && th.Limit1s > 0)
            {
                long used = th.Rate1s(_nowTicks());
                long head = th.Limit1s - used;
                rateHeadroom = head <= 0 ? 0L : (head >= th.Limit1s ? 1000L : head * 1000L / th.Limit1s);
            }
            // ② CPU/서멀
            bool cpuSupported = _cpuGrid?.Supported ?? false;
            bool pcorePinned  = (_cpuGrid?.PinnedThreads ?? 0L) > 0L;
            bool thermalHold  = _thermal?.SafeHoldActive ?? false;
            long effFreq      = _thermal?.EffectiveFreqPermille ?? 1000L;   // 미배선 = 최고(무해)
            // ③ 계좌/VaR
            long marginHeadroom = _marginHeadroomRaw?.Invoke() ?? long.MaxValue;   // 미배선 = 무한 여유(디스크처럼 무해)
            bool varBreach      = _varEngine?.LimitBreached ?? false;
            int  covState       = _varEngine?.CovGuardStateCode ?? 0;
            // ④ 디스크/MMF
            long diskFreeMB = SampleDiskCached();
            long ioStatus   = _ioFirewall?.StatusCode ?? 0L;
            bool pageHold   = _ioFirewall?.PageThrashHoldActive ?? false;

            var inputs = new PreFlightInputs(ipcWired, rttMicros, rateHeadroom,
                cpuSupported, pcorePinned, thermalHold, effFreq,
                marginHeadroom, varBreach, covState,
                diskFreeMB, ioStatus, pageHold);
            _certifier.Certify(inputs);

            // ★인증 통과 ∧ 무장 상태에서만 임대 갱신 — 미인증/미무장이면 갱신 안 함 → 임대 자연 만료(자동 하향).
            bool armed = false; try { armed = _isArmed?.Invoke() ?? false; } catch { armed = false; }
            if (_certifier.IsCertified && armed) _lease.Renew();
        }

        // 디스크 잔여(MB) — 콜드 DriveInfo 를 ≈5s 마다만 실측·캐시(파일시스템 호출을 성기게).
        private long SampleDiskCached()
        {
            long nowMs = Environment.TickCount64;
            if (!_diskSampled || nowMs - _lastDiskSampleMs >= 5000)   // 최초(미측정)만 즉시, 이후엔 성공/실패 무관 5s 스로틀
            {
                _lastDiskSampleMs = nowMs;
                _diskSampled = true;   // 성공/실패 공통 — 실패해도 다음 5s 까지 재프로브 안 함(멈춘 볼륨 블로킹 syscall 폭주 차단)
                try
                {
                    string path = _diskProbePath ?? AppContext.BaseDirectory;
                    string? root = Path.GetPathRoot(Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(root))
                        _cachedDiskFreeMB = new DriveInfo(root).AvailableFreeSpace / (1024L * 1024L);
                }
                catch { _cachedDiskFreeMB = long.MaxValue; }   // 측정 실패 → 디스크 항목 통과(무해·과오탐 방지)
            }
            return _cachedDiskFreeMB;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _stop.Set();
            bool exited; try { exited = _thread == null || _thread.Join(2000); } catch { exited = false; }
            if (exited) { try { _stop.Dispose(); } catch { } }
        }
    }
}
