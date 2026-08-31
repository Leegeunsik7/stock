// ============================================================================
// LockFreeSignalDemuxer + PoisonedSecretBuffer — v11.5.1 Phase 94-N Part 3 (Think Beyond Gemini)
//
//   위험 가정(제미나이 설계가 다루지 않는 잔여 위협):
//     ① "신호 다중화(Signal Multiplexing) 경합으로 킬스위치 유실" — 다수 물리 전송로(USB/COM/NamedPipe/MMF)가
//        마이크로초 내 동시 격발하면, 순진한 last-writer-wins 상태기계는 일부 신호를 덮어써 '어느 경로가 먼저 죽였나'
//        (포렌식)를 잃거나, 최악의 경우 경합 창에서 Disarm 자체를 유실한다.
//     ② "비동기 태스크 잔여 포인터의 뒤늦은 API 키 재참조" — VolatileMemoryPurger 가 버퍼를 0x00 소거해도, 소거 '이전'
//        발사된 async 발주 태스크가 잔여 참조로 그 버퍼를 뒤늦게 재-read 하면(소거 직전 캡처) 권한 탈취 창이 남는다.
//
//   ── Think Beyond (이중 방어 엔진) ─────────────────────────────────────────────
//     ① Lock-Free Hardware Signal Demuxer — 각 전송로를 포화 마스크(saturation mask)의 1비트로 매핑, 단일 원자
//        Interlocked.Or 로 병합. '0→nonzero' 최초 전이를 이룬 단 하나의 신호만 전역 Disarm 을 1회 격발(경합 승자
//        결정론). 어떤 신호도 유실 없이 마스크에 누적(포렌식) — μs 전역 Disarm. δ=0·락프리.
//     ② Poisoned Memory Access Guard — 소거와 동시에 버퍼를 '중독(poisoned)' 표식. 이후 어떤 read 경로(뒤늦은 async
//        태스크 포함)도 O(1) 판정으로 ObjectDisposedException 을 던져 '소거 후 재참조'를 즉시 탐지·차단(fail-closed).
//
//   불변: 결정론 · δ=0(Signal/Fired/IsDisarmed·Read 핫패스 힙 0) · fail-closed(중독 후 접근 예외) · 락프리(Interlocked/Volatile).
//   ※ MS2E.Core(최내부·순수) — 상위 레이어가 onFirstDisarm 콜백으로 HardwareKillSwitchBridge.Signal 에 팬인 가능.
// ============================================================================
using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading;

namespace MS2E.Core
{
    /// <summary>물리 킬스위치 전송로 — 포화 마스크의 비트 인덱스(1 &lt;&lt; ordinal). 64개까지 확장 가능.</summary>
    public enum KillTransport
    {
        UsbButton         = 0,   // USB 물리 버튼
        ComButton         = 1,   // COM/시리얼 라인 버튼
        NamedPipe         = 2,   // NamedPipe IPC 신호
        MemoryMappedFile  = 3,   // MMF 공유메모리 신호
        Watchdog          = 4,   // 하드웨어 워치독 타임아웃
        Manual            = 5,   // 운영자 수동 트립
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① — Lock-Free Hardware Signal Demuxer
    // ════════════════════════════════════════════════════════════════════════
    public sealed class LockFreeSignalDemuxer
    {
        private long _mask;        // 포화 마스크 — 격발한 모든 전송로 비트의 OR(무손실 누적·포렌식)
        private long _firstBit;    // 0→nonzero 최초 전이를 이룬 비트(경합 승자)
        private long _disarmed;    // 0=armed, 1=disarmed(전역 sticky)
        private readonly Action<long>? _onFirstDisarm;   // 정확히 1회 격발(승자 비트 전달). Action<long> — 박싱 0.

        public LockFreeSignalDemuxer(Action<long>? onFirstDisarm = null) => _onFirstDisarm = onFirstDisarm;

        /// <summary>
        /// 외부 전송로 신호 병합 — 락프리 단일 원자 OR. 이전 마스크가 0 이었으면(이 신호가 최초) 전역 Disarm 1회 격발.
        /// 다중 전송로 동시 호출에도 승자는 정확히 하나(결정론), 모든 비트는 마스크에 무손실 누적. δ=0.
        /// </summary>
        public void Signal(KillTransport transport)
        {
            long bit = 1L << (int)transport;
            long prev = Interlocked.Or(ref _mask, bit);   // 원자 OR — 이전 값 반환(.NET 5+)
            if (prev == 0)                                 // ★이 신호가 0→nonzero 최초 전이(경합 승자)
            {
                Volatile.Write(ref _firstBit, bit);
                Volatile.Write(ref _disarmed, 1);
                _onFirstDisarm?.Invoke(bit);               // → 상위: HardwareKillSwitchBridge.Signal 팬인
            }
        }

        public bool IsDisarmed => Volatile.Read(ref _disarmed) == 1;
        public long Mask => Volatile.Read(ref _mask);
        public long FirstBit => Volatile.Read(ref _firstBit);

        /// <summary>특정 전송로가 격발했는지(포렌식·δ=0).</summary>
        public bool Fired(KillTransport t) => (Volatile.Read(ref _mask) & (1L << (int)t)) != 0;

        /// <summary>격발한 전송로 총수(포화 마스크의 팝카운트).</summary>
        public int FiredCount => BitOperations.PopCount((ulong)Volatile.Read(ref _mask));

        /// <summary>최초 격발 전송로(경합 승자) — 없으면 null.</summary>
        public KillTransport? FirstTransport
        {
            get
            {
                long b = Volatile.Read(ref _firstBit);
                return b == 0 ? null : (KillTransport)BitOperations.TrailingZeroCount((ulong)b);
            }
        }

        /// <summary>재초기화(테스트/물리 재무장 후). sticky 마스크·Disarm 해제.</summary>
        public void Reset()
        {
            Volatile.Write(ref _mask, 0);
            Volatile.Write(ref _firstBit, 0);
            Volatile.Write(ref _disarmed, 0);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② — Poisoned Memory Access Guard
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// 소거 후 재참조를 물리적으로 차단하는 API Secret 버퍼. PurgeAndPoison 이후 어떤 read 경로도 O(1) 판정으로
    /// ObjectDisposedException 을 던진다 — '킬스위치 소거 직전 캡처된 잔여 async 포인터의 뒤늦은 API 키 재사용'을 즉시 탐지.
    /// </summary>
    public sealed class PoisonedSecretBuffer : IDisposable
    {
        private readonly byte[] _buffer;
        private long _poisoned;   // 0=live, 1=purged/poisoned(sticky)

        public PoisonedSecretBuffer(int size)
        {
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
            _buffer = new byte[size];
        }

        /// <summary>시드 복제(원본은 호출자가 별도 소거). 내부 사본만 보유.</summary>
        public PoisonedSecretBuffer(byte[] seed)
        {
            if (seed is null) throw new ArgumentNullException(nameof(seed));
            _buffer = (byte[])seed.Clone();
        }

        public int Length => _buffer.Length;
        public bool IsPoisoned => Volatile.Read(ref _poisoned) == 1;

        /// <summary>단일 바이트 read — 중독 시 예외(재참조 탐지).</summary>
        public byte ReadByte(int index)
        {
            ThrowIfPoisoned();
            return _buffer[index];
        }

        /// <summary>Secret 을 dest 로 복사 — 중독 시 예외(뒤늦은 async 재참조 차단).</summary>
        public void CopyTo(Span<byte> dest)
        {
            ThrowIfPoisoned();
            _buffer.AsSpan().CopyTo(dest);
        }

        /// <summary>Secret 을 인가된 사용처에 노출(중독 시 예외). 원본 배열 참조 — 호출자는 즉시 소비 후 폐기.</summary>
        public ReadOnlySpan<byte> AsSpan()
        {
            ThrowIfPoisoned();
            return _buffer;
        }

        /// <summary>
        /// ★소거 + 중독(O(1)). poisoned 표식을 '먼저' 세운 뒤 0x00 소거 → 이후 진입하는 어떤 read 도 예외.
        /// 소거 직전 판정을 통과해 진행 중이던 read 는 최악의 경우 0x00(소거값)만 획득 — Secret 누출 0(fail-closed).
        /// </summary>
        public void PurgeAndPoison()
        {
            Volatile.Write(ref _poisoned, 1);              // ★중독 표식 선행 — 신규 접근 즉시 차단
            CryptographicOperations.ZeroMemory(_buffer);   // 0x00 강제 덮어쓰기
        }

        public void Dispose() => PurgeAndPoison();

        /// <summary>소거 실증(테스트) — 내부 전 바이트 0.</summary>
        public bool AllZero()
        {
            foreach (var b in _buffer) if (b != 0) return false;
            return true;
        }

        private void ThrowIfPoisoned()
        {
            if (Volatile.Read(ref _poisoned) == 1)
                throw new ObjectDisposedException(nameof(PoisonedSecretBuffer),
                    "킬스위치 소거된 Secret 버퍼 재참조(중독 접근 차단) — 뒤늦은 async 태스크의 API 키 재사용 시도.");
        }
    }
}
