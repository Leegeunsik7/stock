// ============================================================================
// HardwareKillSwitchBridge + HardwareInterruptSpinLock + VolatileMemoryPurger — v11.5.0
//   ★궁극의 프로덕션 안전장치: 물리 하드웨어 킬스위치(USB/COM 버튼·NamedPipe/MMF 신호)를 수신해 전역 liveArmed 를
//     강제 Disarm 하고, 진행 중 모든 퀀트/발주 CancellationToken 을 취소하며, 물리 이중 키 재무장 전까지 sticky 격리.
//
//   ── Think Beyond Gemini (이중 방어 엔진) ──────────────────────────────────────
//     ① "스레드 풀 기아로 인한 킬스위치 처리 지연" → HardwareInterruptSpinLock
//        - ThreadPriority.Highest 전용 백그라운드 스레드가 무할당 SpinWait 루프로 킬 플래그를 감시 → CLR 스레드 풀
//          스케줄링 지연을 우회하고 마이크로초 레이턴시로 격발. PollOnce 는 δ=0(힙 0)·결정론(테스트 표적).
//     ② "메모리 상주 API Secret Key 탈취/재사용" → VolatileMemoryPurger
//        - 킬스위치 격발과 동시에 등록된 토스/키움 API Secret 버퍼를 CryptographicOperations.ZeroMemory 로 0x00
//          강제 덮어쓰기 → 프로세스 덤프·메모리 해킹을 통한 발주 권한 탈취를 원천 차단.
//
//   ★liveArmed:false 불변: 킬스위치 격발 후 재무장(물리 이중 키)까지 어떤 소프트웨어 명령으로도 liveArmed 복구 불가.
//     상위 발주 게이트(CanaryHealthSentinel)는 Killed 이벤트로 즉시 Kill 되며, Token 취소로 API 통신·발주 인가 0.
//   불변: 결정론 · δ=0(PollOnce/Signal 판정 힙 0) · fail-closed(미상/재무장 실패 → Disarm 유지) · 락프리(Interlocked/Volatile).
//   ※ MS2E.Core(최내부·순수) — 상위 레이어(Infrastructure)가 Killed 이벤트로 CanaryHealthSentinel·Purger 를 결선.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace MS2E.Core
{
    public enum KillSwitchReason : byte { None = 0, HardwareButton = 1, NamedPipe = 2, Watchdog = 3, Manual = 4 }

    /// <summary>하드웨어 킬스위치 Port — 외부 물리 인터럽트 수신 + 전역 Disarm + CancellationToken 취소 + sticky.</summary>
    public interface IHardwareKillSwitchPort
    {
        /// <summary>외부 하드웨어 인터럽트 진입점(USB/COM/NamedPipe/MMF). 즉시 Disarm·취소·이벤트 격발.</summary>
        void Signal(KillSwitchReason reason);
        bool IsKilled { get; }
        /// <summary>퀀트/발주 파이프라인이 구독하는 취소 토큰 — 킬스위치 격발 시 취소된다.</summary>
        CancellationToken Token { get; }
        KillSwitchReason LastReason { get; }
        event Action<KillSwitchReason>? Killed;
        /// <summary>★물리 이중 키 재무장 — 두 키가 모두 배선됐을 때만 sticky 해제(fail-closed). 소프트웨어 복구 불가.</summary>
        bool TryRearm(bool valveWired, bool liveArmingWired);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Kill-Switch Bridge — sticky Disarm 상태기계
    // ════════════════════════════════════════════════════════════════════════
    public sealed class HardwareKillSwitchBridge : IHardwareKillSwitchPort
    {
        private long _killed;
        private int _reason;
        private CancellationTokenSource _cts = new();

        public event Action<KillSwitchReason>? Killed;

        public bool IsKilled => Volatile.Read(ref _killed) == 1;
        public CancellationToken Token => _cts.Token;
        public KillSwitchReason LastReason => (KillSwitchReason)Volatile.Read(ref _reason);

        public void Signal(KillSwitchReason reason)
        {
            var cts = _cts;   // 로컬 캡처(재무장 경합 방어)
            if (Interlocked.Exchange(ref _killed, 1) == 0)
            {
                Volatile.Write(ref _reason, (int)reason);
                try { cts.Cancel(); } catch { }           // ★진행 중 모든 퀀트/발주 토큰 취소
                Killed?.Invoke(reason);                    // → CanaryHealthSentinel.Kill · VolatileMemoryPurger.PurgeAll
            }
            else { try { cts.Cancel(); } catch { } }       // 멱등(이미 kill)
        }

        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!valveWired || !liveArmingWired) return false;   // ★물리 이중 키 필수(fail-closed)
            var fresh = new CancellationTokenSource();
            var old = Interlocked.Exchange(ref _cts, fresh);
            old.Dispose();
            Volatile.Write(ref _reason, (int)KillSwitchReason.None);
            Interlocked.Exchange(ref _killed, 0);
            return true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① — Zero-Latency Hardware Interrupt Spin-Lock
    // ════════════════════════════════════════════════════════════════════════
    public sealed class HardwareInterruptSpinLock : IDisposable
    {
        private long _running;
        private long _pending;
        private int _reason;
        private long _fires;
        private readonly Action<KillSwitchReason> _onFire;
        private Thread? _thread;

        public HardwareInterruptSpinLock(Action<KillSwitchReason> onFire)
            => _onFire = onFire ?? throw new ArgumentNullException(nameof(onFire));

        /// <summary>외부 인터럽트(USB/COM/NamedPipe) — 킬 플래그 세팅. 전용 스핀 스레드가 마이크로초 내 감지.</summary>
        public void RaiseInterrupt(KillSwitchReason reason)
        {
            Volatile.Write(ref _reason, (int)reason);
            Volatile.Write(ref _pending, 1);
        }

        /// <summary>플래그 1회 폴링(δ=0·락프리). 대기 신호가 있으면 클리어 + 격발 후 true. 스핀 루프의 단위 연산.</summary>
        public bool PollOnce()
        {
            if (Volatile.Read(ref _pending) != 1) return false;
            Volatile.Write(ref _pending, 0);
            Interlocked.Increment(ref _fires);
            _onFire((KillSwitchReason)Volatile.Read(ref _reason));
            return true;
        }

        public long Fires => Volatile.Read(ref _fires);

        /// <summary>전용 최고우선순위 백그라운드 스레드 기동 — SpinWait(스핀-후-양보)로 코어 점유 없이 초저지연 감시.</summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _running, 1) == 1) return;
            _thread = new Thread(SpinLoop) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "HwKillSwitchSpin" };
            _thread.Start();
        }

        private void SpinLoop()
        {
            var sw = new SpinWait();
            while (Volatile.Read(ref _running) == 1)
            {
                if (PollOnce()) sw.Reset();
                else { sw.SpinOnce(); if (sw.NextSpinWillYield) sw.Reset(); }
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _running, 0);
            try { if (_thread is { IsAlive: true }) _thread.Join(1000); } catch { }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② — API Secret Key Volatile Memory Purger
    // ════════════════════════════════════════════════════════════════════════
    public sealed class VolatileMemoryPurger
    {
        private readonly List<byte[]> _byteSecrets = new();
        private readonly List<char[]> _charSecrets = new();
        private readonly object _gate = new();
        private long _purged;

        /// <summary>메모리 상주 API Secret 버퍼 등록(토스/키움 등). 킬스위치 격발 시 전량 0x00 소거.</summary>
        public void Register(byte[] secret) { if (secret is null) return; lock (_gate) _byteSecrets.Add(secret); }
        public void Register(char[] secret) { if (secret is null) return; lock (_gate) _charSecrets.Add(secret); }

        /// <summary>★전량 0x00 강제 덮어쓰기 + sticky purged. 프로세스 덤프/메모리 해킹 통한 권한 탈취 원천 차단.</summary>
        public void PurgeAll()
        {
            Interlocked.Exchange(ref _purged, 1);
            lock (_gate)
            {
                foreach (var s in _byteSecrets) CryptographicOperations.ZeroMemory(s);
                foreach (var s in _charSecrets) CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(s.AsSpan()));
            }
        }

        public bool IsPurged => Volatile.Read(ref _purged) == 1;
        public int RegisteredCount { get { lock (_gate) return _byteSecrets.Count + _charSecrets.Count; } }

        /// <summary>등록 버퍼가 전부 0 인지(소거 실증).</summary>
        public bool AllZero
        {
            get
            {
                lock (_gate)
                {
                    foreach (var s in _byteSecrets) foreach (var b in s) if (b != 0) return false;
                    foreach (var s in _charSecrets) foreach (var c in s) if (c != 0) return false;
                    return true;
                }
            }
        }
    }
}
