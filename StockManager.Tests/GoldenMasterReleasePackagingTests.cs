// ============================================================================
// GoldenMasterReleasePackagingTests — [출하 · v12.15.0-GM · 추천 A]
//
//  §1 버전 정합 — 호스트 어셈블리 = ReleasePackager.DefaultVersion = 12.15.0(출하 zip 이름 · 태그 v12.15.0-GM 의 원천).
//  §2 [방향 B · 실결함] 출하 롤백 가드 — 사전 검사 통과 직후 같은 버전 기존 산출물 전량 폐기(빌드 전) → [1]~[10] 어느 실패에도
//     직전 실행 zip 이 '통과한 출하물'처럼 잔존하지 않음 · 빌드 -warnaserror(MSBuild 경고 포함 = 게이트와 같은 판정).
//  §3 [Fail-Closed Tag & Seal] 실 출하 기록(release/ · 존재할 때만 — 태그 기준 깨끗한 작업 트리에서 생성 후 복사):
//     매니페스트 자기 해시 · manifest.sig · master-seal.txt · zip SHA-256 · zip 무추출 재수검(Ok) · 소스 커밋 = 태그 커밋 ·
//     인터락 28종 = 태그 소스 열거 · 아티팩트 38 · liveArmed 기본 false · 피어 기록(있으면 이름 대조 · -NoPeer 면 부재) · 1비트 변조 zip = 거부 · 스트리머 δ=0.
//  §4 오중 빌드 회귀.
// ============================================================================
using System;
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
    public sealed class GoldenMasterReleasePackagingTests
    {
        private readonly ITestOutputHelper _out;
        public GoldenMasterReleasePackagingTests(ITestOutputHelper o) => _out = o;

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

        // ════════════ §1 버전 ════════════

        [Fact]
        public void HostVersion_Is_12_15_0_And_DrivesReleaseNames()
        {
            var v = typeof(StockManager.Blazor.BlazorHomeForm).Assembly.GetName().Version!;
            Assert.Equal(new Version(12, 15, 0, 0), v);
            Assert.Equal("12.15.0", ReleasePackager.DefaultVersion);
            string csproj = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "StockManager.csproj"));
            Assert.Contains("<Version>12.15.0</Version>", csproj);
        }

        // ════════════ §2 롤백 가드 ════════════

        [Fact]
        public void PackageScript_PurgesStaleOutputsBeforeBuild_WarnAsError_BomKept()
        {
            string path = Path.Combine(RepoRoot(), "release-package.ps1");
            byte[] raw = File.ReadAllBytes(path);
            Assert.True(raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF, "한글 .ps1 UTF-8 BOM 필수");
            string s = File.ReadAllText(path);
            int step0 = s.IndexOf("Step 0 ", StringComparison.Ordinal);
            int peer = s.IndexOf("[10/10] 핸드셰이크 거부 예정, 빌드 전 중단", StringComparison.Ordinal);
            int purge = Regex.Match(s, @"^Remove-ReleaseOutputs\r?$", RegexOptions.Multiline).Index;
            int step1 = s.IndexOf("Step 1 ", StringComparison.Ordinal);
            Assert.True(step0 > 0 && peer > step0 && purge > peer && purge < step1, "★기존 산출물 폐기 = 사전 검사 뒤 · 빌드 앞");
            Assert.Contains("dotnet build $hostProj -c Release -p:WebOnly=true -m:1 -warnaserror", s);
            Assert.Contains("dotnet build $sln -c Release -m:1 -warnaserror", s);
            var fn = Regex.Match(s, @"function Remove-ReleaseOutputs \{(?<b>[\s\S]*?)\n\}").Groups["b"].Value;
            foreach (var o in new[] { "$zipOut", "\"$zipOut.sha256\"", "master-seal.txt", "RELEASE_MANIFEST.json" }) Assert.Contains(o, fn);
            Assert.Contains("v12.15.0-GM Production Golden Master", s);
        }

        // ════════════ §3 실 출하 기록 ════════════

        [Fact]
        public void ShippedRelease_CurrentVersion_ChainAgrees_TagSourceBound_TamperRejected()
        {
            string root = RepoRoot(), rel = Path.Combine(root, "release");
            string ver = ReleasePackager.DefaultVersion, tag = "v" + ver + "-GM";
            string mf = Path.Combine(rel, "RELEASE_MANIFEST.json"), zip = Path.Combine(rel, "release-" + tag + ".zip");
            if (!File.Exists(mf) || !File.Exists(zip)) { _out.WriteLine("[출하] release/ 에 " + tag + " 산출물 없음 — 실측 생략(태그 기준 작업 트리에서 생성 후 복사)"); return; }
            string json = File.ReadAllText(mf);
            Assert.True(MultiRepoReleaseManifest.TryParse(json, out var m, out string stored, out string why), why);
            Assert.Equal(stored, MultiRepoReleaseManifest.CanonicalHash(m));
            Assert.Equal((ver + "-GM", tag, "release-" + tag + ".zip"), (m.Version, m.Tag, m.ZipName));
            Assert.False(m.LiveArmedDefault);
            Assert.Equal(38, m.Artifacts.Count);
            // 피어 기록: 있으면 QuantumVault 여야 한다 · 없으면 -NoPeer 명시 생략(피어 작업 트리에 미커밋 코드 = 고정 거부 — 우회 금지)
            if (m.Peer is { } peer) Assert.Equal("QuantumVault", peer.Name);
            else _out.WriteLine("[출하] 피어 기록 없음(-NoPeer) — 피어 미커밋 상태에선 핸드셰이크 고정을 하지 않는다");
            // ★소스 커밋 = 태그 커밋(태그 재지정 후 1비트라도 어긋나면 거부)
            string? tagCommit = GitRepoProbe.Git(root, "rev-list", "-n", "1", tag)?.Trim();
            Assert.False(string.IsNullOrEmpty(tagCommit), "태그 없음: " + tag);
            Assert.Equal(tagCommit, m.SourceCommit, ignoreCase: true);
            // 인터락 28종(0~26 + StartupSanity) = 태그 소스 열거
            var tagLocks = GitRepoProbe.TagInterlockNames(root, m.SourceCommit);
            Assert.NotNull(tagLocks);
            Assert.Equal(m.Interlocks, tagLocks!);
            Assert.Equal(28, m.Interlocks.Count);
            Assert.Contains("KisProtocolDivergence", m.Interlocks);
            Assert.Equal("StartupSanity", m.Interlocks[^1]);
            // 매니페스트 ↔ manifest.sig ↔ master-seal.txt ↔ zip
            string anchor = File.ReadAllText(Path.Combine(rel, "master-seal.txt")).Trim();
            Assert.Equal(m.MasterSeal, anchor, ignoreCase: true);
            Assert.Equal(m.ManifestSigSha256, ReleaseHandshakeCli.FileSha(Path.Combine(rel, "manifest.sig")));
            string zipSha = ReleaseHandshakeCli.FileSha(zip);
            Assert.Equal(m.ZipSha256, zipSha, ignoreCase: true);
            string sidecar = File.ReadAllText(zip + ".sha256");
            Assert.Contains(zipSha, sidecar, StringComparison.OrdinalIgnoreCase);
            using var sig = JsonDocument.Parse(File.ReadAllText(Path.Combine(rel, "manifest.sig")));
            string genesis = sig.RootElement.GetProperty("genesisHeadHash").GetString()!;
            var st = new ManifestSealStreamer();
            using (var fs = File.OpenRead(mf)) Assert.True(st.Load(fs));
            byte[] an = Convert.FromHexString(anchor), zp = Convert.FromHexString(zipSha), gn = Convert.FromHexString(genesis);
            Assert.Equal(ManifestStreamVerdict.Ok, st.Verify(an, zp, gn));
            Assert.Equal(0, MinAllocated(() => st.Verify(an, zp, gn)));                          // 핸드셰이크 판정 δ=0
            // zip 무추출 재수검(항목 스트림 해시 · 내장 manifest.sig · 재계산 봉인 ↔ 앵커)
            var sw = Stopwatch.StartNew();
            var verdict = ZipSealDryRunVerifier.Verify(zip, anchor, out string detail);
            sw.Stop();
            _out.WriteLine($"[출하] {tag} zip 무추출 재수검 {verdict} · {sw.ElapsedMilliseconds}ms · {new FileInfo(zip).Length:N0}B · {detail}");
            Assert.Equal(ZipSealVerdict.Ok, verdict);
            // ★1비트 변조(임시 사본) = 거부 · 원본 무손상
            string tmp = Path.Combine(Path.GetTempPath(), "ms2e_gm_tamper_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                File.Copy(zip, tmp);
                using (var f = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite))
                {
                    long at = f.Length / 3;
                    f.Position = at; int b = f.ReadByte();
                    f.Position = at; f.WriteByte((byte)(b ^ 0x01));
                }
                Assert.NotEqual(ZipSealVerdict.Ok, ZipSealDryRunVerifier.Verify(tmp, anchor, out _));
                Assert.NotEqual(zipSha, ReleaseHandshakeCli.FileSha(tmp));
            }
            finally { File.Delete(tmp); }
            Assert.Equal(zipSha, ReleaseHandshakeCli.FileSha(zip));
        }

        // ════════════ §4 오중 빌드 ════════════

        [Fact]
        public void FiveBuild_Projects_WarningsAsErrors()
        {
            string root = RepoRoot();
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
            {
                Assert.Contains(proj, sln);
                Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", File.ReadAllText(Path.Combine(root, proj)));
            }
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
            Assert.Equal(29, Enum.GetValues<ArmingInterlockSource>().Length);                   // v12.15.1-DEV: + StrategyAnomaly(27) — 출하본(태그 소스)은 28
        }
    }
}
