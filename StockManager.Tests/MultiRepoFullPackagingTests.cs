// ============================================================================
// MultiRepoFullPackagingTests — [다중 저장소 상호 봉인 · v12.15.0-GM · 추천 A]
//
//  §1 [방향 B] 피어 고정(PeerPin) — [0/10] 에서 고정한 피어(HEAD · 봉인 매니페스트 해시)와 기록 피어가 1비트라도 다르면 PeerDrift
//     (빌드 도중 피어 새 커밋/재봉인 = 출하 시작 때 승인한 피어가 아님) · 고정 = 기록 = 관측이면 Ok.
//  §2 [실결함] 피어 대조 요청 + 기록에 피어 없음(단독 -NoPeer 매니페스트) = PeerMissing — 수정 전엔 피어 블록을 건너뛰어 Ok.
//  §3 CLI — 고정값은 --peer 와 함께 · 40/64 hex 만(형식 이탈 = 사용 오류 · 수집 전 거부).
//  §4 패키징 스크립트 — 고정은 [0/10] 피어 사전 점검 안 · 빌드 전 · 동기화/핸드셰이크 양쪽에 전달 · 피어 쓰기 0.
//  §5 [방향 A] 교차 봉인 판독 — 무할당 스트리머(고정 버퍼) 재대조 δ=0 · 1ms 미만(실 출하 매니페스트 · 존재할 때).
//  §6 오중 빌드 회귀.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Tools;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class MultiRepoFullPackagingTests
    {
        private readonly ITestOutputHelper _out;
        public MultiRepoFullPackagingTests(ITestOutputHelper o) => _out = o;

        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
            }
            return min;
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        private const string Src = "f5e1bca2902868cb09e5ef0d896b89ceebf077e8";
        private const string PeerHead = "88343db0aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string QvPath = "release/offline-bundle/RELEASE_MANIFEST.json";
        private static readonly string Seal = new('7', 64), Genesis = new('6', 64), SigSha = new('5', 64), ZipSha = new('8', 64), ShimSha = new('C', 64), PeerSha = new('2', 64);
        private static readonly string[] Locks = { "BootIntegrity", "KisProtocolDivergence", "StartupSanity" };

        private static ReleaseManifestInput Input(PeerRecord? peer = null, bool noPeer = false) => new(
            "12.15.0-GM", "v12.15.0-GM", Src, SigSha, Seal, Genesis,
            new List<(string, string)> { ("host/MS2E.StockSuite.dll", new string('1', 64)), ("proxy/MS2E.KiwoomProxyHost.exe", new string('3', 64)) },
            "release-v12.15.0-GM.zip", ZipSha, ShimSha, false, Locks,
            noPeer ? null : peer ?? new PeerRecord("QuantumVault", PeerHead, QvPath, PeerSha, "root1"));

        private static HandshakeObservation Obs(PeerPin? pin = null, string head = PeerHead, string? sha = null, bool peerObserved = true)
            => new(new RepoObservation(Src, Src, Array.Empty<string>(), Array.Empty<string>()), SigSha, "12.15.0-GM", Seal, Input().Artifacts, ZipSha, Seal, ShimSha,
                   peerObserved ? new PeerObservation(head, Array.Empty<string>(), sha ?? PeerSha, "root1", QvPath, "QuantumVault") : null,
                   Genesis, Locks, pin);

        private static ReleaseHandshakeVerdict V(string json, HandshakeObservation o) => MultiRepoReleaseManifest.Verify(json, o, out _);
        private static string Flip(string hex) => (hex[0] == '0' ? '1' : '0') + hex[1..];

        // ════════════ §1 피어 고정 ════════════

        [Fact]
        public void PeerPin_MatchingStartState_Ok_AnyBitOfDrift_Rejected()
        {
            string json = MultiRepoReleaseManifest.Build(Input());
            var pin = new PeerPin(PeerHead, PeerSha);
            Assert.Equal(ReleaseHandshakeVerdict.Ok, V(json, Obs(pin)));
            Assert.Equal(ReleaseHandshakeVerdict.Ok, V(json, Obs()));                                       // 고정 없음 = 기존 동작
            Assert.Equal(ReleaseHandshakeVerdict.PeerDrift, V(json, Obs(new PeerPin(Flip(PeerHead), PeerSha))));   // ★시작 후 피어 새 커밋
            Assert.Equal(ReleaseHandshakeVerdict.PeerDrift, V(json, Obs(new PeerPin(PeerHead, Flip(PeerSha)))));   // ★시작 후 피어 재봉인
            Assert.Equal(ReleaseHandshakeVerdict.PeerDrift, V(json, Obs(new PeerPin(PeerHead.ToUpperInvariant(), Flip(PeerSha)))));
            Assert.Equal(ReleaseHandshakeVerdict.Ok, V(json, Obs(new PeerPin(PeerHead.ToUpperInvariant(), PeerSha.ToLowerInvariant()))));   // 대소문자 무관
            // 시작 고정 = 기록인데 관측(지금)이 이동 = 기존 PeerCommitMismatch/ManifestMismatch(고정 판정이 관측 판정을 가리지 않음)
            Assert.Equal(ReleaseHandshakeVerdict.PeerCommitMismatch, V(json, Obs(pin, head: Flip(PeerHead))));
            Assert.Equal(ReleaseHandshakeVerdict.PeerManifestMismatch, V(json, Obs(pin, sha: Flip(PeerSha))));
        }

        // ════════════ §2 단독 매니페스트 피어 모드 ════════════

        [Fact]
        public void PeerRequested_ButManifestHasNoPeer_PeerMissing()
        {
            string solo = MultiRepoReleaseManifest.Build(Input(noPeer: true));
            Assert.Equal(ReleaseHandshakeVerdict.Ok, V(solo, Obs(peerObserved: false)));                   // 단독 출하 = 단독 핸드셰이크 Ok
            Assert.Equal(ReleaseHandshakeVerdict.PeerMissing, V(solo, Obs()));                              // ★수정 전 Ok(피어 블록 생략)
            Assert.Equal(ReleaseHandshakeVerdict.PeerMissing, V(solo, Obs(new PeerPin(PeerHead, PeerSha), peerObserved: false)));
            Assert.Equal((21, 13), ((int)ReleaseHandshakeVerdict.PeerDrift, (int)ReleaseHandshakeVerdict.PeerMissing));
        }

        // ════════════ §3 CLI ════════════

        [Fact]
        public void Cli_PinRequiresPeer_AndExactHexWidths()
        {
            string tmp = Path.GetTempPath();
            int Run(params string[] extra)
            {
                var a = new List<string> { ReleaseHandshakeCli.HandshakeFlag, "--release-dir", tmp, "--repo", tmp, "--shim", Path.Combine(tmp, "absent.dll") };
                a.AddRange(extra);
                return ReleaseHandshakeCli.Run(a.ToArray(), TextWriter.Null);
            }
            Assert.Equal(ReleaseHandshakeCli.ExitUsage, Run("--pin-peer-head", PeerHead, "--pin-peer-manifest", PeerSha));          // 피어 없이 고정
            Assert.Equal(ReleaseHandshakeCli.ExitUsage, Run("--peer", tmp, "--pin-peer-head", PeerHead[..39], "--pin-peer-manifest", PeerSha));
            Assert.Equal(ReleaseHandshakeCli.ExitUsage, Run("--peer", tmp, "--pin-peer-head", PeerHead, "--pin-peer-manifest", PeerSha[..63] + "Z"));
            Assert.Equal(ReleaseHandshakeCli.ExitUsage, Run("--peer", tmp, "--pin-peer-head", PeerHead));                           // 짝 누락
            Assert.Equal(ReleaseHandshakeCli.ExitRejected, Run("--peer", tmp, "--pin-peer-head", PeerHead, "--pin-peer-manifest", PeerSha,
                                                               "--manifest", Path.Combine(tmp, "absent_" + Guid.NewGuid().ToString("N") + ".json")));   // 형식 통과 → 이후 판정
        }

        // ════════════ §4 패키징 스크립트 ════════════

        [Fact]
        public void PackageScript_PinsPeerInPreflight_PassesToSyncAndHandshake_ReadOnly()
        {
            string s = File.ReadAllText(Path.Combine(RepoRoot(), "release-package.ps1"));
            int dirtyGate = s.IndexOf("if ($peerDirty.Count -gt 0) { Fail", StringComparison.Ordinal);
            int pinHead = s.IndexOf("$peerPinHead = (& git -C $PeerRepo rev-parse HEAD 2>$null)", StringComparison.Ordinal);
            int pinSha = s.IndexOf("$peerPinSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $peerManifest).Hash", StringComparison.Ordinal);
            int step1 = s.IndexOf("Step 1 ", StringComparison.Ordinal);
            Assert.True(dirtyGate > 0 && pinHead > dirtyGate && pinSha > pinHead && pinSha < step1, "★피어 고정 = 사전 점검 안 · 빌드 전");
            Assert.Contains("'--pin-peer-head', $peerPinHead, '--pin-peer-manifest', $peerPinSha", s);
            int hsArgs = s.IndexOf("'--pin-peer-head', $peerPinHead", StringComparison.Ordinal);
            Assert.True(hsArgs < s.IndexOf("'--sync-release-manifest'", StringComparison.Ordinal), "고정값은 동기화 · 핸드셰이크 공통 인자");
            Assert.DoesNotMatch(@"(Set-Content|Out-File|Copy-Item|Move-Item|Remove-Item|New-Item|Add-Content)[^\r\n]*\$(PeerRepo|peerManifest)", s);
            Assert.Equal(6, Regex.Matches(s, "Remove-ReleaseOutputs; Fail").Count);   // v12.16.1-GM: + 바이너리 liveArmed 판독 거부 · v12.18.0-GM: + 30대 인터락 봉인 대조 거부
        }

        // ════════════ §5 교차 봉인 판독 ════════════

        [Fact]
        public void CrossSealStreamer_ZeroAlloc_SubMillisecond()
        {
            string json = MultiRepoReleaseManifest.Build(Input());
            var st = new ManifestSealStreamer();
            Assert.True(st.Load(System.Text.Encoding.UTF8.GetBytes(json)));
            for (int i = 0; i < 50; i++) st.Verify(default);
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 200; i++) st.Verify(default); }));
            var samples = new long[201];
            for (int i = 0; i < samples.Length; i++) { long t0 = Stopwatch.GetTimestamp(); st.Verify(default); samples[i] = Stopwatch.GetTimestamp() - t0; }
            Array.Sort(samples);
            double medianMs = samples[100] * 1000.0 / Stopwatch.Frequency;
            _out.WriteLine($"[교차 봉인] 매니페스트 자기 봉인 재계산 중앙값 {medianMs * 1000:0.0}µs");
            Assert.True(medianMs < 1.0, "1ms 초과: " + medianMs);

            string shipped = Path.Combine(RepoRoot(), "release", "RELEASE_MANIFEST.json");
            if (!File.Exists(shipped)) return;
            var real = new ManifestSealStreamer();
            using (var fs = File.OpenRead(shipped)) Assert.True(real.Load(fs));
            Assert.Equal(ManifestStreamVerdict.Ok, real.Verify(default));
            Assert.Equal(0, MinAllocated(() => real.Verify(default)));
            using var doc = JsonDocument.Parse(File.ReadAllText(shipped));
            bool hasPeer = doc.RootElement.TryGetProperty("peer", out var pe) && pe.ValueKind == JsonValueKind.Object;
            _out.WriteLine("[출하] release/RELEASE_MANIFEST.json 피어 기록 " + (hasPeer ? "있음(다중 저장소 봉인)" : "없음(단독 출하 — 다중 저장소 봉인 아님)"));
        }

        // ════════════ §6 오중 빌드 ════════════

        [Fact]
        public void FiveBuild_Regression()
        {
            string root = RepoRoot();
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
        }
    }
}
