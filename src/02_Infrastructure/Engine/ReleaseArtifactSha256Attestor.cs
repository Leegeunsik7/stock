// ============================================================================
// ReleaseArtifactSha256Attestor — v10.1.0 Final Packaging: 릴리스 아티팩트 SHA-256 포렌식 봉인기 (Gemini 보강)
//
//  실행 빌드 바이너리의 SHA-256 포렌식 해시를 콜드 1회 산출해 봉인하고, 외부 서명 매니페스트(ReleasePackager)의
//  기대 봉인과 대조해 무결성을 검증한다. 일치할 때에만 SealValid=1(★fail-closed 기본: 기대 봉인 미주입 = 미검증 = 무효).
//  검증 결과는 점화 감사 링(IgnitionAuditTrailRecorder)에 각인해 사후 포렌식 재구성이 가능하다.
//
//  ★"O(1) 무할당"의 정직한 범위: '런타임 상태 조회'(SealValid/SealHex64/Verified)만 O(1) δ=0(콜드 산출 결과 캐시
//    읽기)이다. SHA-256 산출 자체는 파일 전체를 스트리밍 해시하므로 O(n)·콜드 1회(생성자/명시 Attest)다 — 이를 "O(1)
//    무할당 SHA-256"이라 참칭하지 않는다. 또한 자가-해시의 부트스트랩 순환(실행 바이너리 해시는 자신을 포함 불가)을
//    피하기 위해 기대 봉인은 반드시 외부 신뢰 매니페스트에서 주입받아 대조한다(주입 없으면 seal 발행만·검증 아님).
//
//  ★불변: liveArmed:false(IBroker·소켓·onSend 참조 0 — 순수 해시/대조/감사) · 결정론(SHA-256 순수·벽시계 무참조) ·
//    fail-closed(파일 접근 실패·기대 봉인 부재·불일치 → SealValid=0). δ=0(런타임 조회 핫패스 힙 0·Interlocked).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>릴리스 아티팩트 SHA-256 포렌식 봉인기(콜드 산출·δ=0 상태 조회). 실행 바이너리 자가-봉인 + 매니페스트 대조 + 감사 각인.</summary>
    public sealed class ReleaseArtifactSha256Attestor
    {
        private readonly IgnitionAuditTrailRecorder? _audit;
        private readonly string _selfSealHex;   // 실행 어셈블리 SHA-256(콜드 1회·hex). 접근 불가 시 "".
        private long _selfSeal64;                // 봉인 하위 64비트(텔레메트리 δ=0 표시)
        private long _sealValid;                 // 0/1 — 기대 봉인 대조 성공(fail-closed 기본 0)
        private long _verified;                  // 0/1 — 대조 수행됨(기대 봉인 주입)
        private long _attestedArtifacts;         // 대조한 아티팩트 수

        public ReleaseArtifactSha256Attestor(string? selfPath = null, string? expectedSealHex = null,
                                             IgnitionAuditTrailRecorder? audit = null)
        {
            _audit = audit;
            string path = selfPath ?? SelfAssemblyPath();
            string hex;
            try { hex = string.IsNullOrEmpty(path) ? "" : ComputeFileSealHex(path); }
            catch { hex = ""; }   // 파일 접근/단일파일/AOT(Location="") → 빈 봉인(fail-closed·미검증)
            _selfSealHex = hex;
            Volatile.Write(ref _selfSeal64, HexLow64(hex));

            if (!string.IsNullOrEmpty(expectedSealHex))
                Attest(expectedSealHex);
            else
                _audit?.Record(IgnitionEventKind.ReleaseAttestation, Interlocked.Read(ref _selfSeal64), 0L);   // seal 발행만(미검증)
        }

        // ── 읽기(δ=0·락프리·O(1)) ──
        public bool   SealValid         => Interlocked.Read(ref _sealValid) == 1L;   // ★검증 성공(기대 봉인 일치)
        public bool   Verified          => Interlocked.Read(ref _verified) == 1L;    // 대조 수행됨(기대 봉인 주입)
        public long   SealHex64         => Interlocked.Read(ref _selfSeal64);        // 봉인 하위 64비트(UI 헥스)
        public string SealHex           => _selfSealHex;                             // 전체 봉인 hex(콜드 불변)
        public long   AttestedArtifacts => Interlocked.Read(ref _attestedArtifacts);
        /// <summary>★blockWhile 후보 — 유효 봉인(기대 매니페스트 일치) 없으면 릴리스 인증 차단(tighten-only·fail-closed).</summary>
        public bool   AttestationBlocking => Interlocked.Read(ref _sealValid) == 0L;

        /// <summary>실행 바이너리 봉인을 기대 봉인(외부 신뢰 매니페스트)과 대조(콜드). 일치 시 SealValid=1. 감사 각인.</summary>
        public bool Attest(string expectedSealHex)
        {
            bool ok = !string.IsNullOrEmpty(_selfSealHex)
                      && string.Equals(_selfSealHex, expectedSealHex?.Trim(), StringComparison.OrdinalIgnoreCase);
            Volatile.Write(ref _verified, 1L);
            Volatile.Write(ref _sealValid, ok ? 1L : 0L);
            _audit?.Record(IgnitionEventKind.ReleaseAttestation, Interlocked.Read(ref _selfSeal64), ok ? 1L : 0L);
            return ok;
        }

        /// <summary>아티팩트 집합을 서명 매니페스트 masterSeal 과 대조(콜드·ReleasePackager 위임·변조/누락 감지). 일치 시 SealValid=1.</summary>
        public bool AttestArtifacts(IReadOnlyList<string> artifactPaths, string expectedMasterSealHex)
        {
            bool ok;
            try { ok = ReleasePackager.VerifyArtifacts(artifactPaths, expectedMasterSealHex); }
            catch { ok = false; }   // 아티팩트 접근 실패 → 미검증(fail-closed)
            Volatile.Write(ref _attestedArtifacts, artifactPaths?.Count ?? 0);
            Volatile.Write(ref _verified, 1L);
            Volatile.Write(ref _sealValid, ok ? 1L : 0L);
            _audit?.Record(IgnitionEventKind.ReleaseAttestation, HexLow64(expectedMasterSealHex), ok ? 1L : 0L);
            return ok;
        }

        /// <summary>파일 SHA-256(hex·콜드·O(n) 스트리밍). 무할당 아님 — 패키징/시작 1회 산출용.</summary>
        public static string ComputeFileSealHex(string path)
        {
            using FileStream fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs));   // 스트리밍 해시(전체 바이트 배열 미적재)
        }

        private static string SelfAssemblyPath()
        {
            try { return typeof(ReleaseArtifactSha256Attestor).Assembly.Location ?? ""; }
            catch { return ""; }
        }

        // hex 문자열의 하위 16 니블(64비트) → long(텔레메트리 δ=0 표시·캐시). 유효하지 않은 니블 → 0.
        private static long HexLow64(string? hex)
        {
            if (string.IsNullOrEmpty(hex)) return 0L;
            int n = hex.Length, start = n > 16 ? n - 16 : 0;
            ulong v = 0UL;
            for (int i = start; i < n; i++)
            {
                int d = HexNibble(hex[i]);
                if (d < 0) return 0L;
                v = (v << 4) | (uint)d;
            }
            return (long)v;
        }

        private static int HexNibble(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };
    }
}
