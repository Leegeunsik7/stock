// ============================================================================
// RedTeamDefense — v5.0.0 Phase3: 원격 킬스위치 + 데드맨 역-컷오버
//
//  실전 강인함은 '위기 생존력'에서 나온다. 두 최종 방어선:
//   · RemoteKillSwitch(능동): 외부(모바일 웹훅/로컬 시그널)에서 서명된 비상정지 명령이 투하되면, <0.1ms 내에
//     자격증명 금고 재봉인(Seal) + 소켓 Abort + ForceFreeze 로 수렴. ★수신 계층(웹훅/포트)은 열지 않는다 —
//     서명 검증된 명령을 Trip(command) 으로 받는 것까지가 본 클래스의 경계(외부 네트워크 표면 미개방).
//   · DeadmanReverseCutover(수동·★Think Beyond B): 운영자 생존 신호(Beat)가 데드라인 내 갱신되지 않으면 자동으로
//     AtomicCutoverGate.RollbackToShadow 를 격발해 실전→섀도우 역-컷오버. 킬스위치가 '능동 정지'라면 데드맨은
//     '신호 부재→안전' 수동 안전망(운영자 무력화·제어 채널 사망 대비).
//
//  liveArmed:false: 두 방어선 모두 '더 안전한 방향(정지/섀도우)'으로만 작동 — 실주문을 여는 경로 0.
//  결정론(주입 시계)·킬스위치 수렴 경로는 인메모리 volatile write(금고 Seal·ForceFreeze) + 주입 abort 콜백.
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public enum KillSwitchState : byte { Ready = 0, Tripped = 1 }
    public enum DeadmanState    : byte { Idle = 0, Watching = 1, Reverted = 2 }

    /// <summary>원격 서명 비상정지 → 금고 재봉인+소켓 Abort+ForceFreeze 원자 수렴(&lt;0.1ms). 수신 네트워크 표면은 미개방.</summary>
    public sealed class RemoteKillSwitch
    {
        private readonly OrderAttestationService _att;
        private readonly Func<string, bool>      _verify;        // 서명 검증(IgnitionLock signatureValidator 패턴 재사용)
        private readonly SecurityAssetVault?     _vault;         // 재봉인 대상(불가역 Seal)
        private readonly Action?                 _abortSockets;  // 실 소켓/파이프 Abort 콜백(운영자 배선·비블록)
        private readonly MmfFlightRecorder?      _blackbox;

        private int _state = (int)KillSwitchState.Ready;   // Volatile.Read/Write(ref) 접근(CS0420 회피)
        private long _trips, _rejected;

        public RemoteKillSwitch(OrderAttestationService attestation, Func<string, bool> verifySignature,
                                SecurityAssetVault? vault = null, Action? abortSockets = null, MmfFlightRecorder? blackbox = null)
        {
            _att          = attestation ?? throw new ArgumentNullException(nameof(attestation));
            _verify       = verifySignature ?? throw new ArgumentNullException(nameof(verifySignature));
            _vault        = vault;
            _abortSockets = abortSockets;
            _blackbox     = blackbox;
        }

        public KillSwitchState State => (KillSwitchState)Volatile.Read(ref _state);
        public long Trips     => Interlocked.Read(ref _trips);
        public long Rejected  => Interlocked.Read(ref _rejected);

        /// <summary>
        /// 서명된 비상정지 명령 투하. 서명 무효/공백이면 무동작(거부·계수만) — 위조 킬 명령으로 시스템을 흔들 수 없음.
        /// 유효 서명이면 원자 수렴: ForceFreeze(단일 진실원) → 금고 재봉인(Seal·불가역) → 소켓 Abort. 반환=발동 여부.
        /// </summary>
        public bool Trip(string signedCommand)
        {
            bool ok;
            try { ok = !string.IsNullOrWhiteSpace(signedCommand) && _verify(signedCommand); }
            catch { ok = false; }
            if (!ok) { Interlocked.Increment(ref _rejected); return false; }   // 위조/미서명 → 거부(흔들기 방지)

            // ★원자 비상 수렴(인메모리 volatile write — <0.1ms). ForceFreeze 가 CrashResilientDumper 옵저버를 자동 격발(블랙박스 보존).
            try { _att.ForceFreeze("원격 킬스위치 — 비상 정지(서명 투하)"); } catch { }
            try { _vault?.Seal(); } catch { }                                  // 자격증명 금고 재봉인(불가역)
            try { _abortSockets?.Invoke(); } catch { }                         // 실 소켓/파이프 강제 Abort(운영자 배선)
            Volatile.Write(ref _state, (int)KillSwitchState.Tripped);
            Interlocked.Increment(ref _trips);
            try { _blackbox?.Record(FlightEventKind.KillSwitchTrip, symbolCode: 0L, arg0: 1L, arg1: 0L, arg2: 0L, sourceCode: 53); } catch { }
            return true;
        }
    }

    /// <summary>★Think Beyond B — 운영자 생존 신호 감시자. 데드라인 내 Beat 부재 시 자동 섀도우 역-컷오버(수동 안전망).</summary>
    public sealed class DeadmanReverseCutover : IDisposable
    {
        private readonly AtomicCutoverGate  _cutover;
        private readonly Func<long>         _nowTicks;
        private readonly long               _deadlineTicks;
        private readonly int                _pollMs;
        private readonly MmfFlightRecorder? _blackbox;

        private long _lastBeat;
        private int _state = (int)DeadmanState.Idle;   // Volatile.Read/Write(ref) 접근(CS0420 회피)
        private long _reversions;
        private System.Threading.Timer? _timer;

        /// <param name="deadline">이 시간 내 Beat 가 없으면 역-컷오버.</param>
        /// <param name="tickFrequency">nowTicks 의 초당 틱(기본 Stopwatch.Frequency).</param>
        public DeadmanReverseCutover(AtomicCutoverGate cutover, TimeSpan deadline,
                                     Func<long>? nowTicks = null, long tickFrequency = 0, int pollMs = 100,
                                     MmfFlightRecorder? blackbox = null)
        {
            _cutover  = cutover ?? throw new ArgumentNullException(nameof(cutover));
            _nowTicks = nowTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;
            long freq = tickFrequency > 0 ? tickFrequency : System.Diagnostics.Stopwatch.Frequency;
            double secs = deadline.TotalSeconds <= 0 ? 1.0 : deadline.TotalSeconds;
            _deadlineTicks = (long)(secs * freq);
            _pollMs = pollMs < 10 ? 10 : pollMs;
            _blackbox = blackbox;
        }

        public DeadmanState State => (DeadmanState)Volatile.Read(ref _state);
        public long Reversions    => Interlocked.Read(ref _reversions);

        /// <summary>운영자 생존 신호(주기 호출). 감시 시작 후 데드라인마다 갱신돼야 함.</summary>
        public void Beat() => Interlocked.Exchange(ref _lastBeat, _nowTicks());

        /// <summary>감시 시작(컷오버 성공 후). 첫 Beat 를 세팅하고 폴 타이머 기동.</summary>
        public void StartWatching()
        {
            Interlocked.Exchange(ref _lastBeat, _nowTicks());
            Volatile.Write(ref _state, (int)DeadmanState.Watching);
            _timer ??= new System.Threading.Timer(_ => { try { CheckOnce(); } catch { } }, null, _pollMs, _pollMs);
        }

        /// <summary>1회 생존 검사(테스트는 주입 시계로 직접 호출). Watching∧실전∧데드라인 초과 시 자동 역-컷오버.</summary>
        public void CheckOnce()
        {
            if (Volatile.Read(ref _state) != (int)DeadmanState.Watching) return;
            long since = _nowTicks() - Interlocked.Read(ref _lastBeat);
            if (since > _deadlineTicks && _cutover.IsLive)
            {
                // ★CAS 전이 — Timer 콜백과 수동 CheckOnce 동시 실행 시 정확히 1회만 역-컷오버(이중 롤백/이중 계수 봉쇄).
                if (Interlocked.CompareExchange(ref _state, (int)DeadmanState.Reverted, (int)DeadmanState.Watching) != (int)DeadmanState.Watching)
                    return;
                _cutover.RollbackToShadow("데드맨 — 운영자 생존 신호 소실(자동 섀도우 역-컷오버)");
                Interlocked.Increment(ref _reversions);
                try { _blackbox?.Record(FlightEventKind.DeadmanRevert, symbolCode: 0L, arg0: since, arg1: _deadlineTicks, arg2: 0L, sourceCode: 54); } catch { }
            }
        }

        public void Dispose() { try { _timer?.Dispose(); } catch { } _timer = null; }
    }
}
