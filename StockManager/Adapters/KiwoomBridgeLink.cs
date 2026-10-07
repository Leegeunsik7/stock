// ============================================================================
// KiwoomBridgeLink — [키움 실배선 · 추천 A] 키움 프록시 링크 인터락(비트 25) + 프록시 수명 감독자(기동 · 연결 · 수신 시작)
//
//  KiwoomBridgeLinkInterlock — 연결 활성화(Kiwoom.ConnectEnabled) 상태에서 '실 COM 프록시 링크'(파이프 신원 검증 통과 · ProxyHello=ProductionCom ·
//    하트비트 생존)가 아니면 차단. 미연결 · 신원 거부 · 시뮬레이션 프록시(NullKiwoomApi) · 단절 전부 차단(fail-closed). 연결 비활성 배포 = 비차단
//    (그 경우 키움 송출은 브로커가 거부 — ExecutionBridgeBroker.RequireProductionProxy).
//  KiwoomProxySupervisor — ① (선택) x86 프록시 실행 파일 기동(이중 실행은 프록시 뮤텍스가 거부) ② 연결 재시도(시도당 시한 · 고정 간격 · 상한)
//    ③ 성공 시 브리지 수신 루프 시작. ★신원 거부(UnauthorizedAccessException) = 즉시 중단(재시도 금지 — 파이프 선점은 보안 사건).
//    ★단절 후 자동 재기동 없음: 브리지가 하트비트 단절 시 시스템 전역 동결(ForceFreeze)하는 1회용 설계 — 재기동은 운용자 몫(fail-closed 계승).
//    처분 시 자신이 기동한 프록시만 종료(트리).
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;

namespace StockManager.Adapters
{
    public sealed class KiwoomBridgeLinkInterlock : IArmingBlockSource
    {
        private readonly Func<bool> _productionLinked;
        private readonly bool _required;
        private int _blocked;
        private long _transitions;
        private ArmingInterlockLink? _link;

        public KiwoomBridgeLinkInterlock(Func<bool> productionLinked, bool required)
        {
            _productionLinked = productionLinked ?? throw new ArgumentNullException(nameof(productionLinked));
            _required = required;
            _blocked = required ? 1 : 0;                                          // 활성화 시 최초 = 차단(링크 수립 전)
        }

        public bool Required => _required;
        public bool ArmingBlocked => Volatile.Read(ref _blocked) == 1;
        public long Transitions => Interlocked.Read(ref _transitions);

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        /// <summary>링크 상태 재평가(δ=0 · 타이머/브리지 이벤트). 상태 전이 시에만 집계 비트 재동기화.</summary>
        public void Evaluate()
        {
            bool linked;
            try { linked = _productionLinked(); } catch { linked = false; }
            int now = _required && !linked ? 1 : 0;
            if (Interlocked.Exchange(ref _blocked, now) != now)
            {
                Interlocked.Increment(ref _transitions);
                Volatile.Read(ref _link)?.Resync();
            }
        }
    }

    public enum KiwoomSupervisorState : byte { Idle = 0, Launching = 1, Connecting = 2, Connected = 3, ProxyMissing = 4, IdentityRejected = 5, GaveUp = 6, LaunchFailed = 7 }

    public sealed class KiwoomProxySupervisor : IDisposable
    {
        private readonly Func<CancellationToken, Task> _connect;
        private readonly Func<CancellationToken, Task> _start;
        private readonly string _proxyExe;
        private readonly bool _autoStart;
        private readonly Func<string, IDisposable?> _launcher;
        private readonly TimeSpan _attemptTimeout, _retryDelay;
        private readonly int _maxAttempts;
        private IDisposable? _launched;
        private int _state;
        private long _attempts;

        /// <param name="launcher">프록시 기동(경로 → 종료 핸들 · null = 실패). 기본 = Process.Start(창 없음) + 트리 종료 핸들.</param>
        public KiwoomProxySupervisor(Func<CancellationToken, Task> connect, Func<CancellationToken, Task> start, string proxyExe, bool autoStart,
                                     Func<string, IDisposable?>? launcher = null, TimeSpan? attemptTimeout = null, TimeSpan? retryDelay = null, int maxAttempts = 60)
        {
            _connect = connect ?? throw new ArgumentNullException(nameof(connect));
            _start = start ?? throw new ArgumentNullException(nameof(start));
            _proxyExe = proxyExe ?? "";
            _autoStart = autoStart;
            _launcher = launcher ?? DefaultLauncher;
            _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(3);
            _retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
            _maxAttempts = Math.Max(1, maxAttempts);
        }

        public KiwoomSupervisorState State => (KiwoomSupervisorState)Volatile.Read(ref _state);
        public long Attempts => Interlocked.Read(ref _attempts);

        public async Task RunAsync(CancellationToken ct)
        {
            if (_autoStart)
            {
                Set(KiwoomSupervisorState.Launching);
                if (!File.Exists(_proxyExe)) { Set(KiwoomSupervisorState.ProxyMissing); return; }
                IDisposable? h;
                try { h = _launcher(_proxyExe); } catch (Exception) { h = null; }
                if (h is null) { Set(KiwoomSupervisorState.LaunchFailed); return; }
                Volatile.Write(ref _launched, h);
            }
            Set(KiwoomSupervisorState.Connecting);
            for (int i = 0; i < _maxAttempts && !ct.IsCancellationRequested; i++)
            {
                Interlocked.Increment(ref _attempts);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(_attemptTimeout);
                try
                {
                    await _connect(attempt.Token).ConfigureAwait(false);
                    Set(KiwoomSupervisorState.Connected);
                    _ = _start(ct);                                                   // 수신·디스패치·하트비트 루프(브리지 1회용)
                    return;
                }
                catch (UnauthorizedAccessException) { Set(KiwoomSupervisorState.IdentityRejected); return; }   // ★파이프 선점 = 재시도 금지
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception) { /* 프록시 미기동/대기 — 재시도 */ }
                try { await Task.Delay(_retryDelay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
            if (!ct.IsCancellationRequested) Set(KiwoomSupervisorState.GaveUp);
        }

        private void Set(KiwoomSupervisorState s) => Volatile.Write(ref _state, (int)s);

        private static IDisposable? DefaultLauncher(string exe)
        {
            var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
            return p is null ? null : new ProcessKillHandle(p);
        }

        private sealed class ProcessKillHandle : IDisposable
        {
            private readonly Process _p;
            public ProcessKillHandle(Process p) => _p = p;
            public void Dispose()
            {
                try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); } catch (Exception) { }
                _p.Dispose();
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _launched, null)?.Dispose();
    }
}
