// ============================================================================
// PinnedSha256Streamer — [봉인 운영 · 방향 A] Zero-Alloc POH Buffer Seal Hash Streamer
//
//  왜(실측 결함): ReleasePackager.HashNamed/HashArtifacts 는 File.ReadAllBytes 로 아티팩트를 통째 적재했다.
//   부팅 무결성 게이트(BootIntegrityGate ④)와 릴리스 패키징이 출하 아티팩트 수십 개(대형 DLL 포함)를 해시할 때마다
//   파일 크기만큼의 byte[] 가 85KB 초과 → LOH 로 직행해 파편화·Gen2 압력을 남겼다(테스트 실측: 48MB 파일 = 48MB 할당).
//
//  설계:
//   · 스레드별 1회 Pinned Object Heap 청크(GC.AllocateUninitializedArray(pinned: true)) — GC 이동/LOH 파편화 0.
//   · 스레드별 1회 IncrementalHash(SHA-256) — GetHashAndReset(Span) 로 무할당 재사용.
//   · File.OpenHandle + RandomAccess.Read(오프셋 지정 · 스트림 객체/내부 버퍼 없음).
//   → 할당은 파일당 SafeFileHandle 1개(상수)뿐이며 파일 크기와 무관하다(청크 루프 δ=0).
//   · 길이 재확인: 해시 도중 파일이 자라거나 줄면(교체 경합) 예외 → 호출측 fail-closed.
//  불변: 콜드 경로(부팅/패키징/CLI) · 결정론 · BCL 전용(System.Security.Cryptography · RandomAccess).
// ============================================================================
using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MS2E.Infrastructure
{
    public static class PinnedSha256Streamer
    {
        public const int HashLen = 32;
        public const int ChunkBytes = 1 << 20;   // 1MB POH 청크(순차 읽기 처리량 확보 · 스레드당 1회)

        [ThreadStatic] private static byte[]? t_chunk;
        [ThreadStatic] private static IncrementalHash? t_sha;

        /// <summary>파일 SHA-256 을 dst(32B)에 기록하고 파일 길이를 반환. 할당 = 파일 핸들 1개(크기 무관).</summary>
        public static long HashFile(string path, Span<byte> dst)
        {
            if (dst.Length < HashLen) throw new ArgumentException("dst ≥ 32B", nameof(dst));
            using SafeFileHandle h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            return HashHandle(h, dst);
        }

        /// <summary>열린 핸들의 SHA-256(δ=0 청크 루프). 해시 도중 길이 변동 → IOException(fail-closed).</summary>
        public static long HashHandle(SafeFileHandle handle, Span<byte> dst)
        {
            byte[] chunk = t_chunk ??= GC.AllocateUninitializedArray<byte>(ChunkBytes, pinned: true);
            IncrementalHash sha = t_sha ??= IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = RandomAccess.GetLength(handle);
            long offset = 0;
            try
            {
                while (offset < length)
                {
                    int want = (int)Math.Min(chunk.Length, length - offset);
                    int n = RandomAccess.Read(handle, chunk.AsSpan(0, want), offset);
                    if (n <= 0) throw new IOException("해시 도중 파일 축소(교체 경합) — fail-closed");
                    sha.AppendData(chunk, 0, n);
                    offset += n;
                }
                if (RandomAccess.GetLength(handle) != length) throw new IOException("해시 도중 파일 길이 변동(교체 경합) — fail-closed");
                if (!sha.TryGetHashAndReset(dst, out int written) || written != HashLen) throw new CryptographicException("SHA-256 산출 실패");
                return length;
            }
            catch
            {
                sha.GetHashAndReset();   // 부분 상태 폐기(다음 파일 오염 0 · 실패 경로 한정 할당)
                throw;
            }
        }

        /// <summary>콜드 편의 — 32B 배열 반환(아티팩트 목록 구축용).</summary>
        public static byte[] HashFile(string path, out long length)
        {
            var hash = new byte[HashLen];
            length = HashFile(path, hash);
            return hash;
        }
    }
}
