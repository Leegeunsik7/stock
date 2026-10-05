// ============================================================================
// RawCredentialLeakInterlock — [PII 하드닝 Think Beyond 방향 B] Account Token Zeroize Disarm Interlock
//
//   위협: 예외 덤프·디버그 출력·서드파티 로그가 "마스킹 이전의 날것" API Key/Secret/접근토큰/비밀번호를 텍스트 채널로 흘리려는 순간.
//   LogRedactor 는 이를 가려 기록하지만, 가려서 적는 것으로 끝나면 "자격증명이 이미 평문으로 메모리 밖 경로에 노출되려 했다"는
//   사실(코드 결함 또는 침해 징후)이 묻힌다. 이 인터락은 그 순간을 침해로 간주한다:
//     ▸ 탐지(δ=0): 등록 자격증명의 UTF-16 사본(POH 고정)과 UTF-8 사본을 보관하고, 텍스트/바이트 스팬에서 IndexOf 로만 대조.
//     ▸ Trip(0ms · sticky): ① 결속된 발주 토큰(PoisonedSecretBuffer) 중독+0x00 ② VolatileMemoryPurger 전량 소거
//       ③ 자신이 보관한 자격증명 사본 0x00 ④ ArmingBlocked=true(BlazorHomeForm blockWhile) ⑤ onTrip(킬스위치 격발).
//       재무장은 프로세스 재시작 + 물리 이중키 + 자격증명 재발급(유출 추정)으로만.
//   범위(정직): 대상은 "자격증명"(키·시크릿·토큰·비밀번호·개인키)뿐 — 계좌번호 같은 식별자는 정상 로그에도 등장할 수 있어 차단 대상이
//   아니라 LogRedactor 마스킹 대상이다(오격발로 인한 무장 불능 방지). 블랙박스(MmfCircularBlackBoxWriter)는 숫자형 FlightRecordEvent
//   구조체만 기록하므로 텍스트 자격증명 유입 경로가 없다 — 바이트 채널용 InspectUtf8 는 제공하되 그곳엔 결속하지 않았다.
//   불변: liveArmed:false(차단만 가능 · 무장 부여 불가) · Inspect/InspectUtf8 힙 0 · 예외 비전파(로깅 경로를 죽이지 않음).
// ============================================================================
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MS2E.Core;

namespace MS2E.Infrastructure
{
    public sealed class RawCredentialLeakInterlock : IArmingBlockSource
    {
        /// <summary>프로세스 전역 — LogRedactor.Shared 가 모든 로그 라인을 이 인스턴스로 검사.</summary>
        public static RawCredentialLeakInterlock Shared { get; } = new RawCredentialLeakInterlock();

        public const int MinCredentialLength = 8;   // 초단문은 일반 텍스트와 우연 일치(오격발) 위험

        private readonly object _gate = new();
        private volatile char[][] _chars = Array.Empty<char[]>();
        private volatile byte[][] _utf8 = Array.Empty<byte[]>();
        private volatile PoisonedSecretBuffer[] _tokens = Array.Empty<PoisonedSecretBuffer>();
        private VolatileMemoryPurger? _purger;
        private Action? _onTrip;
        private int _tripped;
        private long _leakAttempts, _faults;

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        /// <summary>★blockWhile — 원문 자격증명 유출 시도 이후 무장 영구 차단.</summary>
        public bool ArmingBlocked => Tripped;
        public long LeakAttempts => Interlocked.Read(ref _leakAttempts);
        public long CallbackFaults => Interlocked.Read(ref _faults);
        public int CredentialCount => _chars.Length;

        // [실배선 · 방향 B] 원자 인터락 집계기 푸시 결속(ArmingInterlockBitmask) — 상태 전이 직후 Resync(δ=0).
        private MS2E.Core.ArmingInterlockLink? _interlockLink;
        /// <summary>집계기에 이 엔진을 고정 비트로 결속 — 결속 즉시 현재 상태 동기화(이미 차단이면 비트 유지).</summary>
        public void BindInterlock(MS2E.Core.ArmingInterlockBitmask aggregator, MS2E.Core.ArmingInterlockSource bit)
        {
            ArgumentNullException.ThrowIfNull(aggregator);
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _interlockLink, link);
            link.Resync();   // Bind ~ 링크 가시화 사이의 전이 흡수
        }
        private void SignalInterlock() => Volatile.Read(ref _interlockLink)?.Resync();

        /// <summary>자격증명 등록(콜드). 공백·초단문·중복 무시. Trip 이후 등록은 즉시 거부(유출 추정 상태).</summary>
        public void RegisterCredential(string? secret)
        {
            if (string.IsNullOrWhiteSpace(secret) || Tripped) return;
            secret = secret.Trim();
            if (secret.Length < MinCredentialLength) return;
            lock (_gate)
            {
                foreach (var c in _chars) if (c.AsSpan().SequenceEqual(secret)) return;
                var ch = GC.AllocateArray<char>(secret.Length, pinned: true);
                secret.AsSpan().CopyTo(ch);
                var u8 = GC.AllocateArray<byte>(Encoding.UTF8.GetByteCount(secret), pinned: true);
                Encoding.UTF8.GetBytes(secret, u8);
                var nc = new char[_chars.Length + 1][]; _chars.CopyTo(nc, 0); nc[^1] = ch;
                var nb = new byte[_utf8.Length + 1][]; _utf8.CopyTo(nb, 0); nb[^1] = u8;
                _chars = nc; _utf8 = nb;
            }
        }

        /// <summary>Trip 시 소거할 발주 토큰 버퍼·Secret 소거기·킬스위치 콜백 결속(콜드).</summary>
        public void Bind(VolatileMemoryPurger? purger = null, Action? onTrip = null, params PoisonedSecretBuffer[] orderTokens)
        {
            lock (_gate)
            {
                _purger = purger ?? _purger;
                _onTrip = onTrip ?? _onTrip;
                if (orderTokens is { Length: > 0 })
                {
                    var n = new PoisonedSecretBuffer[_tokens.Length + orderTokens.Length];
                    _tokens.CopyTo(n, 0); orderTokens.CopyTo(n, _tokens.Length);
                    _tokens = n;
                }
            }
            if (Tripped) FireSinks();   // 이미 Trip 된 뒤 결속된 토큰도 즉시 소거
        }

        /// <summary>텍스트 채널 검사(δ=0). 등록 자격증명 원문 포함 시 Trip 후 true.</summary>
        public bool Inspect(ReadOnlySpan<char> text)
        {
            if (text.IsEmpty) return false;
            var creds = _chars;
            for (int i = 0; i < creds.Length; i++)
                if (text.IndexOf(creds[i]) >= 0) { Trip(); return true; }
            return false;
        }

        /// <summary>바이트 채널(UTF-8) 검사(δ=0).</summary>
        public bool InspectUtf8(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty) return false;
            var creds = _utf8;
            for (int i = 0; i < creds.Length; i++)
                if (bytes.IndexOf(creds[i]) >= 0) { Trip(); return true; }
            return false;
        }

        private void Trip()
        {
            Interlocked.Increment(ref _leakAttempts);
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return;
            SignalInterlock();   // 플래그 선기록 → 집계 비트 Raise
            FireSinks();
            lock (_gate)
            {
                foreach (var c in _chars) CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(c.AsSpan()));
                foreach (var b in _utf8) CryptographicOperations.ZeroMemory(b);
                _chars = Array.Empty<char[]>();
                _utf8 = Array.Empty<byte[]>();
            }
        }

        private void FireSinks()
        {
            foreach (var t in _tokens) { try { t.PurgeAndPoison(); } catch { Interlocked.Increment(ref _faults); } }
            try { _purger?.PurgeAll(); } catch { Interlocked.Increment(ref _faults); }
            try { _onTrip?.Invoke(); } catch { Interlocked.Increment(ref _faults); }
        }
    }
}
