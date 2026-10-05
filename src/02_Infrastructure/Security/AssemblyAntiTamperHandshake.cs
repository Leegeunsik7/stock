// ============================================================================
// AssemblyAntiTamperHandshake — [Phase 4] Think Beyond 방향 A+B:
//   Zero-Alloc Cold-Boot Cryptographic Handshake Sentinel + Runtime Anti-Tamper Memory Guard
//
//   기존 ColdBootPohRingIntegritySentinel(v12.14.0)은 FNV-1a 64 비암호 체크섬이라 메모리 쓰기 권한을 가진 공격자가
//   충돌을 손쉽게 만들 수 있고, 바이너리 파일 해시는 GoldenMasterSealVerificationSentinel 이 매 호출 스트림/배열을 할당했다.
//   이 핸드셰이크는 두 축을 SHA-256 으로 묶어 "정상 상태(steady-state) 0 Byte" 로 재검증한다:
//     ① 어셈블리 바이너리 — 생성 시 1회 File.OpenHandle(FileShare.Read) 로 열어 보유 → 핸들 보유 중 외부 프로세스의 쓰기/교체
//        오픈 자체가 공유 위반으로 거부되고, Verify 는 RandomAccess.Read → POH 고정 청크 버퍼 → 재사용 IncrementalHash 로 해시.
//     ② POH 링/세션 메모리 영역 — 등록 시점(콜드 부팅, 첫 소비 이전) SHA-256 을 기준선으로 봉인하고 이후 동일 경로로 재대조.
//        외부 프로세스의 WriteProcessMemory 등으로 영역 바이트가 1비트라도 바뀌면 불일치.
//   불일치 → 0ms Trip(sticky): 등록된 발주 토큰/API Secret(PoisonedSecretBuffer) 을 먼저 중독 표식 후 0x00 소거,
//   VolatileMemoryPurger 전량 소거, ArmingBlocked=true(blockWhile tighten-only). 재무장은 프로세스 재시작 + 물리 이중키만.
//   StartMonitor 는 전용 백그라운드 스레드에서 주기 재검증(방향 B) — 루프 자체도 할당 0.
//
//   ★정직 한계: JIT 된 네이티브 코드 페이지·런타임 내부 구조는 검사하지 않는다(관리 코드에서 신뢰성 있게 열거 불가).
//     보호 대상 = 디스크 이미지(로드 원본) + 명시 등록한 데이터 영역. 핸들 보유는 Windows 공유 모드 보호이며 관리자 권한
//     공격자의 원시 디스크 쓰기까지 막지는 못한다(그 경우 다음 주기 해시가 탐지).
//   불변: δ=0(Verify/모니터 루프 힙 0 — 생성·등록은 콜드) · 결정론(SHA-256 순수 함수) · fail-closed(첫 검증 성공 전 ArmingBlocked,
//        판독 오류·길이 변경·해시 불일치 전부 Trip) · liveArmed:false(IBroker·소켓·발주 참조 0 — 차단만 가능, 무장 부여 불가).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using MS2E.Core;

namespace MS2E.Infrastructure
{
    public enum TamperReason : byte { None = 0, ArtifactHashMismatch = 1, ArtifactLengthChanged = 2, ArtifactReadFault = 3, RegionHashMismatch = 4 }

    /// <summary>핸드셰이크 판정(값 타입 — 힙 0). Index = 결함 아티팩트/영역 인덱스(-1 = 해당 없음).</summary>
    public readonly record struct HandshakeVerdict(bool Passed, TamperReason Reason, int Index, bool Tripped);

    public sealed class AssemblyAntiTamperHandshake : IDisposable
    {
        public const int HashLen = 32;
        public const int DefaultChunkBytes = 64 * 1024;

        private readonly SafeFileHandle[] _handles;
        private readonly long[] _lengths;
        private readonly byte[] _expected;            // POH: [아티팩트 n × 32][영역 m × 32]
        private readonly List<byte[]> _regions = new();
        private readonly byte[] _chunk;               // POH 고정 청크(GC 이동 0)
        private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly object _verifyGate = new();  // IncrementalHash 는 비스레드안전 — 검증 직렬화(Monitor thin-lock·할당 0)
        private readonly PoisonedSecretBuffer[] _secrets;
        private readonly VolatileMemoryPurger? _purger;
        private readonly Action<TamperReason>? _onTrip;
        private readonly ManualResetEventSlim _stop = new(false);
        private Thread? _monitor;
        private int _tripped, _handshakeOk, _disposed;
        private long _verifications, _tripReason, _callbackFaults;

        /// <param name="artifacts">(경로, 기대 SHA-256 32B) — 보통 manifest.sig 의 아티팩트 해시.</param>
        /// <param name="secrets">Trip 시 즉시 중독·0x00 소거할 발주 토큰/API Secret 버퍼.</param>
        public AssemblyAntiTamperHandshake(IReadOnlyList<(string Path, byte[] ExpectedSha256)> artifacts,
                                           IReadOnlyList<PoisonedSecretBuffer>? secrets = null,
                                           VolatileMemoryPurger? purger = null,
                                           Action<TamperReason>? onTrip = null,
                                           int maxRegions = 16,
                                           int chunkBytes = DefaultChunkBytes)
        {
            if (artifacts is null) throw new ArgumentNullException(nameof(artifacts));
            if (chunkBytes < 4096) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
            if (maxRegions < 0) throw new ArgumentOutOfRangeException(nameof(maxRegions));
            _handles = new SafeFileHandle[artifacts.Count];
            _lengths = new long[artifacts.Count];
            _expected = GC.AllocateArray<byte>((artifacts.Count + maxRegions) * HashLen, pinned: true);
            _chunk = GC.AllocateUninitializedArray<byte>(chunkBytes, pinned: true);
            _secrets = secrets is null ? Array.Empty<PoisonedSecretBuffer>() : new List<PoisonedSecretBuffer>(secrets).ToArray();
            _purger = purger;
            _onTrip = onTrip;
            try
            {
                for (int i = 0; i < artifacts.Count; i++)
                {
                    var (path, expected) = artifacts[i];
                    if (expected is null || expected.Length != HashLen) throw new ArgumentException($"아티팩트[{i}] 기대 해시는 SHA-256 32바이트", nameof(artifacts));
                    expected.CopyTo(_expected, i * HashLen);
                    // FileShare.Read — 보유 중 다른 프로세스의 쓰기/삭제 오픈 거부(런타임 바이너리 교체 차단)
                    _handles[i] = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    _lengths[i] = RandomAccess.GetLength(_handles[i]);
                }
            }
            catch
            {
                foreach (var h in _handles) h?.Dispose();
                _sha.Dispose();
                throw;
            }
        }

        public bool IsTripped => Volatile.Read(ref _tripped) == 1;
        /// <summary>★blockWhile — 첫 핸드셰이크 성공 전이거나 Trip 이면 무장 차단(tighten-only · fail-closed).</summary>
        public bool ArmingBlocked => Volatile.Read(ref _tripped) == 1 || Volatile.Read(ref _handshakeOk) == 0 || Volatile.Read(ref _disposed) == 1;
        public TamperReason LastTripReason => (TamperReason)Interlocked.Read(ref _tripReason);
        public long Verifications => Interlocked.Read(ref _verifications);
        public long CallbackFaults => Interlocked.Read(ref _callbackFaults);
        public int ArtifactCount => _handles.Length;
        public int RegionCount { get { lock (_verifyGate) return _regions.Count; } }

        /// <summary>POH 링/세션 메모리 영역 등록(콜드) — 등록 순간의 SHA-256 을 기준선으로 봉인. 이후 1비트 변경도 Trip.</summary>
        public int RegisterRegion(byte[] region)
        {
            if (region is null) throw new ArgumentNullException(nameof(region));
            lock (_verifyGate)
            {
                int slot = _handles.Length + _regions.Count;
                if ((slot + 1) * HashLen > _expected.Length) throw new InvalidOperationException("영역 슬롯 초과(maxRegions)");
                _sha.AppendData(region);
                if (!_sha.TryGetHashAndReset(_expected.AsSpan(slot * HashLen, HashLen), out _)) throw new CryptographicException("SHA-256 산출 실패");
                _regions.Add(region);
                return _regions.Count - 1;
            }
        }

        /// <summary>전 아티팩트·영역 SHA-256 재대조(δ=0). 불일치 즉시 Trip(0ms 소거·차단). Trip 이후에는 재검증 없이 실패 고정.</summary>
        public HandshakeVerdict Verify()
        {
            if (Volatile.Read(ref _tripped) == 1) return new(false, LastTripReason, -1, true);
            if (Volatile.Read(ref _disposed) == 1) return new(false, TamperReason.None, -1, false);   // 정상 종료 — 변조 아님(소거 오탐 방지), 무장은 ArmingBlocked 로 차단
            Span<byte> actual = stackalloc byte[HashLen];
            lock (_verifyGate)
            {
                Interlocked.Increment(ref _verifications);
                for (int i = 0; i < _handles.Length; i++)
                {
                    try
                    {
                        if (RandomAccess.GetLength(_handles[i]) != _lengths[i]) return Trip(TamperReason.ArtifactLengthChanged, i);
                        long offset = 0;
                        while (offset < _lengths[i])
                        {
                            int want = (int)Math.Min(_chunk.Length, _lengths[i] - offset);
                            int n = RandomAccess.Read(_handles[i], _chunk.AsSpan(0, want), offset);
                            if (n <= 0) return Trip(TamperReason.ArtifactReadFault, i);
                            _sha.AppendData(_chunk.AsSpan(0, n));
                            offset += n;
                        }
                    }
                    catch (IOException) { return Trip(TamperReason.ArtifactReadFault, i); }
                    catch (UnauthorizedAccessException) { return Trip(TamperReason.ArtifactReadFault, i); }
                    catch (ObjectDisposedException) { return Trip(TamperReason.ArtifactReadFault, i); }
                    _sha.TryGetHashAndReset(actual, out _);
                    if (!CryptographicOperations.FixedTimeEquals(actual, _expected.AsSpan(i * HashLen, HashLen)))
                        return Trip(TamperReason.ArtifactHashMismatch, i);
                }
                for (int r = 0; r < _regions.Count; r++)
                {
                    _sha.AppendData(_regions[r]);
                    _sha.TryGetHashAndReset(actual, out _);
                    if (!CryptographicOperations.FixedTimeEquals(actual, _expected.AsSpan((_handles.Length + r) * HashLen, HashLen)))
                        return Trip(TamperReason.RegionHashMismatch, r);
                }
            }
            Volatile.Write(ref _handshakeOk, 1);
            return new(true, TamperReason.None, -1, false);
        }

        private HandshakeVerdict Trip(TamperReason reason, int index)
        {
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) == 0)
            {
                Interlocked.Exchange(ref _tripReason, (long)reason);
                // ① 발주 토큰/Secret — 중독 표식 선행 후 0x00(PoisonedSecretBuffer 계약) · 하나 실패가 나머지 소거를 막지 않음
                for (int i = 0; i < _secrets.Length; i++)
                {
                    try { _secrets[i].PurgeAndPoison(); } catch { Interlocked.Increment(ref _callbackFaults); }
                }
                try { _purger?.PurgeAll(); } catch { Interlocked.Increment(ref _callbackFaults); }
                try { _onTrip?.Invoke(reason); } catch { Interlocked.Increment(ref _callbackFaults); }
            }
            return new(false, reason, index, true);
        }

        /// <summary>[방향 B] 전용 백그라운드 스레드 주기 재검증 시작(1회만). Trip 시 루프 종료.</summary>
        public void StartMonitor(TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            if (Volatile.Read(ref _disposed) == 1) throw new ObjectDisposedException(nameof(AssemblyAntiTamperHandshake));
            if (Interlocked.CompareExchange(ref _monitor, new Thread(() => MonitorLoop(interval)) { IsBackground = true, Name = "MS2E.AntiTamperGuard" }, null) is not null)
                return;
            _monitor!.Start();
        }

        private void MonitorLoop(TimeSpan interval)
        {
            while (!_stop.Wait(interval))
            {
                if (Volatile.Read(ref _disposed) == 1 || Verify().Tripped) return;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _stop.Set();
            var t = Volatile.Read(ref _monitor);
            if (t is not null && t.IsAlive && t != Thread.CurrentThread) t.Join();
            lock (_verifyGate)
            {
                foreach (var h in _handles) h?.Dispose();
                _sha.Dispose();
            }
            _stop.Dispose();
        }
    }
}
