// ============================================================================
// StandaloneBundleTests — [v12.18.28-DEV] 즉시 실행 배포본(StockManager-v<ver>-Standalone-win-x64.zip) 구조 정합성 · 봉인 동일성
//
//  §1 판독기(StandaloneBundleVerifier) — 합성 zip(PS 5.1 Compress-Archive 역슬래시 항목명 포함): 정상 = Ok · 필수 실행 자산 누락 ·
//     프레임워크 의존 runtimeconfig(includedFrameworks 없음) · 봉인 바이너리 1비트 변조 · 봉인 등재 누락 · 개발 경로 누출(staticwebassets.runtime.json) = 거부
//  §2 파이프라인 — release-package.ps1 [11/11] 이 MS2E_RELEASE_STANDALONE_ZIP 로 실 배포본을 넘길 때만 판독(로컬 실행 = §1 이 같은 판독기를 증명)
//  §3 스크립트 결선 — [11/11] = 감사 봉인 [10/10] 인터락/강화 대조 뒤 · 실패 = 전량 폐기 · 폐기 목록에 배포본 포함 · self-contained publish(x64 호스트 · x86 프록시) ·
//     봉인 zip 원천 오버레이 + 해시 대조 · 격리 기동(주 창 핸들) · 피어(QuantumVault) 참조 0 · 감사 봉인 산출물 쓰기 0
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace StockManager.Tests.Packaging
{
    /// <summary>즉시 실행 배포본 판독기(무추출 · 항목 스트림 해시).</summary>
    internal static class StandaloneBundleVerifier
    {
        public static readonly string[] Required =
        {
            "host/MS2E.StockSuite.exe", "host/MS2E.StockSuite.dll", "host/MS2E.StockSuite.runtimeconfig.json", "host/MS2E.StockSuite.deps.json",
            "host/hostfxr.dll", "host/coreclr.dll", "host/WebView2Loader.dll", "host/e_sqlite3.dll", "host/wwwroot/index.html",
            "proxy/MS2E.KiwoomProxyHost.exe", "proxy/MS2E.KiwoomProxyHost.runtimeconfig.json", "proxy/hostfxr.dll",
            "artifacts.sha256.txt", "manifest.sig",
        };

        public static bool Verify(string zipPath, out string reason, out int sealedCount)
        {
            reason = ""; sealedCount = 0;
            using var zip = ZipFile.OpenRead(zipPath);
            static string Norm(string n) => n.Replace('\\', '/');
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries) entries[Norm(e.FullName)] = e;
            foreach (var r in Required)
                if (!entries.ContainsKey(r)) { reason = "필수 실행 자산 누락: " + r; return false; }
            foreach (var rc in new[] { "host/MS2E.StockSuite.runtimeconfig.json", "proxy/MS2E.KiwoomProxyHost.runtimeconfig.json" })
                if (!Read(entries[rc]).Contains("\"includedFrameworks\"", StringComparison.Ordinal)) { reason = "자체 포함 런타임 아님: " + rc; return false; }
            var leak = entries.Keys.FirstOrDefault(k => k.EndsWith(".staticwebassets.runtime.json", StringComparison.OrdinalIgnoreCase));
            if (leak is not null) { reason = "개발 경로 누출: " + leak; return false; }
            foreach (var line in Read(entries["artifacts.sha256.txt"]).Split('\n'))
            {
                var m = Regex.Match(line.Trim(), @"^([0-9A-Fa-f]{64})\s+(\S.*)$");
                if (!m.Success) continue;
                string rel = Norm(m.Groups[2].Value.Trim());
                if (!entries.TryGetValue(rel, out var e)) { reason = "봉인 등재 바이너리 누락: " + rel; return false; }
                using var s = e.Open();
                string got = Convert.ToHexString(SHA256.HashData(s));
                if (!got.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) { reason = "봉인 해시 불일치: " + rel; return false; }
                sealedCount++;
            }
            if (sealedCount == 0) { reason = "봉인 아티팩트 0건"; return false; }
            return true;
        }

        private static string Read(ZipArchiveEntry e)
        {
            using var r = new StreamReader(e.Open(), Encoding.UTF8);
            return r.ReadToEnd();
        }
    }

    public sealed class StandaloneBundleTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("StockManager.sln 루트 탐색 실패");
        }

        private const string SelfContained = "{\"runtimeOptions\":{\"tfm\":\"net8.0\",\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"8.0.0\"}]}}";
        private const string FrameworkDependent = "{\"runtimeOptions\":{\"tfm\":\"net8.0\",\"frameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"8.0.0\"}]}}";

        /// <summary>합성 배포본 — 항목명은 PS 5.1 Compress-Archive 처럼 역슬래시(판독기 정규화 증명).</summary>
        private static string Synthetic(Action<Dictionary<string, byte[]>>? mutate = null, Action<Dictionary<string, byte[]>>? afterSeal = null)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in StandaloneBundleVerifier.Required) files[r] = Encoding.UTF8.GetBytes("bin:" + r);
            files["host/MS2E.StockSuite.runtimeconfig.json"] = Encoding.UTF8.GetBytes(SelfContained);
            files["proxy/MS2E.KiwoomProxyHost.runtimeconfig.json"] = Encoding.UTF8.GetBytes(SelfContained);
            files["host/MS2E.UI.Portable.dll"] = Encoding.UTF8.GetBytes("portable");
            files["proxy/MS2E.KiwoomProxyHost.dll"] = Encoding.UTF8.GetBytes("proxy");
            mutate?.Invoke(files);
            var sealedNames = new[] { "host/MS2E.StockSuite.exe", "host/MS2E.StockSuite.dll", "host/MS2E.UI.Portable.dll", "proxy/MS2E.KiwoomProxyHost.exe", "proxy/MS2E.KiwoomProxyHost.dll" };
            var sb = new StringBuilder("# MS2E StockSuite 배포 아티팩트 SHA-256\n");
            foreach (var n in sealedNames)
                if (files.TryGetValue(n, out var b)) sb.Append(Convert.ToHexString(SHA256.HashData(b))).Append("  ").Append(n.Replace('/', '\\')).Append('\n');
                else sb.Append(new string('A', 64)).Append("  ").Append(n.Replace('/', '\\')).Append('\n');
            if (files.ContainsKey("artifacts.sha256.txt")) files["artifacts.sha256.txt"] = Encoding.UTF8.GetBytes(sb.ToString());
            afterSeal?.Invoke(files);
            string path = Path.Combine(Path.GetTempPath(), "ms2e_standalone_" + Guid.NewGuid().ToString("N") + ".zip");
            using (var z = ZipFile.Open(path, ZipArchiveMode.Create))
                foreach (var kv in files)
                {
                    var e = z.CreateEntry(kv.Key.Replace('/', '\\'));
                    using var s = e.Open();
                    s.Write(kv.Value);
                }
            return path;
        }

        private static void Expect(string zip, bool ok, string? reasonPart = null)
        {
            try
            {
                bool got = StandaloneBundleVerifier.Verify(zip, out var why, out int n);
                Assert.True(got == ok, $"기대 {ok} · 판정 {got} · 사유 '{why}'");
                if (ok) Assert.Equal(5, n);
                if (reasonPart is not null) Assert.Contains(reasonPart, why);
            }
            finally { File.Delete(zip); }
        }

        // ═══ §1 판독기 ═══

        [Fact]
        public void Verifier_Accepts_Complete_SelfContained_Bundle_With_Backslash_Entries() => Expect(Synthetic(), ok: true);

        [Theory]
        [InlineData("host/MS2E.StockSuite.runtimeconfig.json")]
        [InlineData("host/MS2E.StockSuite.deps.json")]
        [InlineData("host/wwwroot/index.html")]
        [InlineData("host/WebView2Loader.dll")]
        [InlineData("host/e_sqlite3.dll")]
        [InlineData("host/hostfxr.dll")]
        [InlineData("proxy/MS2E.KiwoomProxyHost.exe")]
        [InlineData("manifest.sig")]
        public void Verifier_Rejects_Missing_Runtime_Asset(string missing)
            => Expect(Synthetic(f => f.Remove(missing)), ok: false, reasonPart: missing);

        [Fact]
        public void Verifier_Rejects_FrameworkDependent_Runtimeconfig()
            => Expect(Synthetic(f => f["host/MS2E.StockSuite.runtimeconfig.json"] = Encoding.UTF8.GetBytes(FrameworkDependent)), ok: false, reasonPart: "자체 포함 런타임 아님");

        [Fact]
        public void Verifier_Rejects_Tampered_Sealed_Binary()
            => Expect(Synthetic(afterSeal: f => f["host/MS2E.StockSuite.dll"] = Encoding.UTF8.GetBytes("bin:host/MS2E.StockSuite.dlX")), ok: false, reasonPart: "봉인 해시 불일치: host/MS2E.StockSuite.dll");

        [Fact]
        public void Verifier_Rejects_Missing_Sealed_Binary()
            => Expect(Synthetic(f => f.Remove("host/MS2E.UI.Portable.dll")), ok: false, reasonPart: "봉인 등재 바이너리 누락: host/MS2E.UI.Portable.dll");

        [Fact]
        public void Verifier_Rejects_Dev_Path_Leak()
            => Expect(Synthetic(f => f["host/MS2E.StockSuite.staticwebassets.runtime.json"] = Encoding.UTF8.GetBytes("{\"ContentRoots\":[\"D:\\\\PROGRAM\\\\StockManager\\\\wwwroot\\\\\"]}")),
                      ok: false, reasonPart: "개발 경로 누출");

        // ═══ §2 파이프라인 ═══

        [Fact]
        public void Pipeline_Standalone_Zip_Is_Runnable_Layout_When_Provided()
        {
            string? zip = Environment.GetEnvironmentVariable("MS2E_RELEASE_STANDALONE_ZIP");
            if (string.IsNullOrWhiteSpace(zip)) return;                                  // 파이프라인 [11/11] 전용(로컬 = §1 이 같은 판독기 증명)
            Assert.True(StandaloneBundleVerifier.Verify(zip, out var why, out int n), why);
            Assert.Equal(38, n);                                                        // 감사 봉인본 아티팩트 전량 = 배포본 바이너리(바이트 동일)
        }

        // ═══ §3 스크립트 결선 ═══

        [Fact]
        public void Package_Script_Wires_Step11_After_Seal_Gates_FailClosed()
        {
            string s = File.ReadAllText(Path.Combine(RepoRoot(), "release-package.ps1"));
            int tighten = s.IndexOf("Fail \"강화 전용 단조성 행렬 대조 거부", StringComparison.Ordinal);
            int step11 = s.IndexOf("Step 11 ", StringComparison.Ordinal);
            int call = s.IndexOf("& (Join-Path $root \"standalone-bundle.ps1\") @bundleArgs", StringComparison.Ordinal);
            int final = s.IndexOf("Assert-PeerPinned \"final\"", StringComparison.Ordinal);
            Assert.True(tighten > 0 && step11 > tighten && call > step11 && final > call, "[11/11] = 감사 봉인 게이트 뒤 · 완료 전");
            Assert.Matches(new Regex(@"^Step 11 """, RegexOptions.Multiline), s);
            Assert.Contains("if ($bundleExit -ne 0) { Remove-ReleaseOutputs; Fail", s);
            Assert.Contains("$standaloneZip, \"$standaloneZip.sha256\")) {", s);             // 폐기 목록 = 배포본 포함
            Assert.Contains("SealedZip = $zipOut", s);
            Assert.Contains("function Step($n, $msg) { Write-Host \"[$n/11] $msg\"", s);
        }

        [Fact]
        public void Bundle_Script_Is_SelfContained_Sealed_Overlay_With_Isolated_Launch_And_No_Peer()
        {
            string root = RepoRoot();
            byte[] raw = File.ReadAllBytes(Path.Combine(root, "standalone-bundle.ps1"));
            Assert.True(raw.Length > 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF, "한글 .ps1 = UTF-8 BOM 필수(PS 5.1 CP949 파싱 실패 방지)");
            string s = Encoding.UTF8.GetString(raw);
            Assert.Contains("dotnet publish $hostProj -c Release -r win-x64 --self-contained true", s);
            Assert.Contains("dotnet publish $proxyProj -c Release -r win-x86 --self-contained true", s);
            Assert.Contains("Expand-Archive -LiteralPath $SealedZip", s);                   // 바이너리 원천 = 봉인 zip
            Assert.Contains("if ($got -ne $a.Hash) { Abort", s);                              // 전 항목 해시 대조
            Assert.Contains("*.staticwebassets.runtime.json, *.pdb", s);
            Assert.Contains("\"includedFrameworks\"", s);
            Assert.Contains("StandaloneBundleTests.Pipeline_Standalone_Zip_Is_Runnable_Layout_When_Provided", s);
            Assert.Contains("$hwnd = [int64]$p.MainWindowHandle", s);
            Assert.Contains("Stop-Process -Id $p.Id -Force", s);
            Assert.Contains("trap { Abort", s);
            Assert.DoesNotMatch("(?i)QuantumVault|PeerRepo|--peer", s);
            // 감사 봉인 산출물(zip · master-seal.txt · RELEASE_MANIFEST.json · manifest.sig)은 읽기만 — 쓰기/삭제 cmdlet 대상 0
            Assert.DoesNotMatch(@"(Set-Content|Out-File|Remove-Item|Move-Item|Add-Content)[^\r\n]*(\$SealedZip|\$sealTxt|RELEASE_MANIFEST)", s);
            foreach (var r in StandaloneBundleVerifier.Required) Assert.Contains("\"" + r.Replace('/', '\\') + "\"", s);   // 스크립트 필수 목록 = 판독기 필수 목록
        }
    }
}
