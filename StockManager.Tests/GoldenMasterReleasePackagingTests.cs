// ============================================================================
// GoldenMasterReleasePackagingTests — [출하 · v12.17.3-GM · 추천 A]
//
//  §1 버전 정합 — 호스트 어셈블리 = ReleasePackager.DefaultVersion = 12.17.3(출하 zip 이름 · 태그 v12.17.3-GM 의 원천).
//  §2 [방향 B · 실결함] 출하 롤백 가드 — 사전 검사 통과 직후 같은 버전 기존 산출물 전량 폐기(빌드 전) → [1]~[10] 어느 실패에도
//     직전 실행 zip 이 '통과한 출하물'처럼 잔존하지 않음 · 빌드 -warnaserror(MSBuild 경고 포함 = 게이트와 같은 판정).
//  §3 [Fail-Closed Tag & Seal] 실 출하 기록(release/ · 존재할 때만 — 태그 기준 깨끗한 작업 트리에서 생성 후 복사):
//     매니페스트 자기 해시 · manifest.sig · master-seal.txt · zip SHA-256 · zip 무추출 재수검(Ok) · 소스 커밋 = 태그 커밋 ·
//     인터락 29종(0~27 + StartupSanity) = 태그 소스 열거 · 아티팩트 38 · liveArmed 기본 false · 피어 기록(있으면 이름 대조 · -NoPeer 면 부재) · 1비트 변조 zip = 거부 · 스트리머 δ=0.
//  §4 오중 빌드 회귀.
//  §7 [v12.17.1-GM · 방향 A 확장] ★마스터 플래그 IL 판독: 빌드 산출 DLL = 마스터 플래그 생성 ≥1(ShadowMetricsBridge liveArmed 리터럴 false) ·
//     newobj 직전 ldc.i4.0→ldc.i4.1 1바이트 패치 = 거부 · get_LiveArmed 본문 ldc.i4.0→ldc.i4.1 = 거부 · Func<bool> 술어 매개변수 = 대상 아님 ·
//     스크립트 [6/10] 소스 가드도 마스터 플래그 대조.
//  §8 [v12.17.3-GM · 방향 A] ★UI 설정 기입 가드 IL 판독: 빌드 산출 DLL = 기입자 3/3 게이트 · 허용 목록 안전 네임스페이스 0 ·
//     Admit 직후 brfalse.s → pop;nop(판정 무시) 1바이트 패치 = 기입자마다 거부 · 허용 목록 접두사 'Email.' → 'Kis.ab'(#US 힙) = 거부 · CLI 거부.
//  §6 [v12.17.0-GM · 방향 B] zip 항목 정규화 · 중복 가드: PS 5.1 역슬래시 실봉인 zip(실 호스트 DLL + 봉인 비바이너리 runtimeconfig) = 수검 Ok ·
//     IL 판독 Ok · manifest.sig 2개 / 봉인 비바이너리 대소문자 변형 사본 / 구분자 · 대소문자 변형 바이너리 사본 = 전부 거부(Windows 해제 시 덮어쓰기 모호성).
//  §5 [v12.16.1-GM · 방향 B] 출하 바이너리 liveArmed 기본값 IL 직접 판독(ShippedBinaryLiveArmedGuard — 추출·로드·실행 0):
//     빌드 산출 DLL = 조회 지점 ≥2 · 위반 0 · ldc.i4.0→ldc.i4.1 1바이트 패치 사본 = 거부 · 손상/항목 누락 zip = 거부 · 스크립트 [9] 결선.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
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
        public void HostVersion_Is_12_17_3_And_DrivesReleaseNames()
        {
            var v = typeof(StockManager.Blazor.BlazorHomeForm).Assembly.GetName().Version!;
            Assert.Equal(new Version(12, 17, 3, 0), v);
            Assert.Equal("12.17.3", ReleasePackager.DefaultVersion);
            string csproj = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "StockManager.csproj"));
            Assert.Contains("<Version>12.17.3</Version>", csproj);
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
            Assert.Contains("v12.17.3-GM Production Golden Master", s);
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
            // 인터락 29종(0~27 + StartupSanity) = 태그 소스 열거
            var tagLocks = GitRepoProbe.TagInterlockNames(root, m.SourceCommit);
            Assert.NotNull(tagLocks);
            Assert.Equal(m.Interlocks, tagLocks!);
            Assert.Equal(29, m.Interlocks.Count);
            Assert.Contains("KisProtocolDivergence", m.Interlocks);
            Assert.Contains("StrategyAnomaly", m.Interlocks);
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
            // [방향 B] 출하 zip 안 호스트 DLL 이중 물리 키 기본값 = 리터럴 false(IL 직접 판독)
            var arm = ShippedBinaryLiveArmedGuard.ScanZip(zip);
            Assert.True(arm.Ok, "출하 바이너리 liveArmed 판독 거부: 지점 " + arm.Sites + " · " + string.Join(" | ", arm.Violations));
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
            Assert.Equal(29, Enum.GetValues<ArmingInterlockSource>().Length);                   // 0~27 + StartupSanity — v12.17.1-GM 출하본(태그 소스)도 29
        }

        // ════════════ §5 [방향 B] 출하 바이너리 liveArmed 기본값 IL 직접 판독 ════════════

        private static string HostDll() => typeof(ReleasePackager).Assembly.Location;

        private static string ZipWithHost(byte[] dll, params string[] entries)
        {
            if (entries.Length == 0) entries = new[] { ShippedBinaryLiveArmedGuard.HostEntry };
            string tmp = Path.Combine(Path.GetTempPath(), "ms2e_arm_" + Guid.NewGuid().ToString("N") + ".zip");
            using (var z = ZipFile.Open(tmp, ZipArchiveMode.Create))
                foreach (var name in entries)
                    using (var e = z.CreateEntry(name).Open()) e.Write(dll);
            return tmp;
        }

        /// <summary>ldstr "Dispatch.ValveWired|LiveArmingWired" → ldc.i4.0 → call/callvirt 의 ldc.i4.0 을 ldc.i4.1 로 바꾼 사본(패치 수 반환).</summary>
        private static byte[] PatchDefaultsToTrue(byte[] dll, out int patched)
        {
            var tokens = new List<int>();
            using (var pe = new PEReader(new MemoryStream(dll)))
            {
                var md = pe.GetMetadataReader();
                for (var h = MetadataTokens.UserStringHandle(1); !h.IsNil; h = md.GetNextHandle(h))
                {
                    string us = md.GetUserString(h);
                    if (us is "Dispatch.ValveWired" or "Dispatch.LiveArmingWired") tokens.Add(MetadataTokens.GetToken(h));
                }
            }
            Assert.Equal(2, tokens.Count);
            var b = (byte[])dll.Clone();
            patched = 0;
            for (int i = 0; i + 6 < b.Length; i++)
            {
                if (b[i] != 0x72 || !tokens.Contains(BitConverter.ToInt32(b, i + 1))) continue;
                if (b[i + 5] == 0x16 && (b[i + 6] == 0x28 || b[i + 6] == 0x6F)) { b[i + 5] = 0x17; patched++; }
            }
            return b;
        }

        [Fact]
        public void ShippedBinaryGuard_BuiltHost_DualKeyDefaultsLiteralFalse_ExtractFree()
        {
            byte[] dll = File.ReadAllBytes(HostDll());
            var sw = Stopwatch.StartNew();
            var r = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(dll));
            sw.Stop();
            _out.WriteLine($"[방향 B] 호스트 DLL {dll.Length:N0}B · 조회 지점 {r.Sites} · 위반 {r.Violations.Count} · {sw.ElapsedMilliseconds}ms");
            Assert.True(r.Sites >= 2, "조회 지점 " + r.Sites);                                   // 비공허성: 두 키 모두 읽혀야 한다
            Assert.Empty(r.Violations);
            Assert.True(r.Ok);
            // zip 경유(항목 스트림 · 디스크 추출 0) + 콜드 CLI
            string zip = ZipWithHost(dll);
            try
            {
                Assert.True(ShippedBinaryLiveArmedGuard.ScanZip(zip).Ok);
                Assert.Equal(ReleaseHandshakeCli.ExitOk, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, zip }, TextWriter.Null));
                Assert.True(ReleaseHandshakeCli.IsInvocation(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, zip }));
            }
            finally { File.Delete(zip); }
        }

        [Fact]
        public void ShippedBinaryGuard_BackslashEntryNames_FromPs51CompressArchive_Accepted_DuplicatesRejected()
        {
            // 실결함(v12.16.1-GM 첫 출하 실행 [9/10] 거부): PS 5.1 Compress-Archive 는 'host\MS2E.StockSuite.dll' 로 기록 → 정확 일치 조회 = 항목 없음
            byte[] dll = File.ReadAllBytes(HostDll());
            string ps51 = ZipWithHost(dll, @"host\MS2E.StockSuite.dll"), dup = ZipWithHost(dll, @"host\MS2E.StockSuite.dll", "host/MS2E.StockSuite.dll");
            try
            {
                var ok = ShippedBinaryLiveArmedGuard.ScanZip(ps51);
                Assert.True(ok.Ok, string.Join(" | ", ok.Violations));
                Assert.True(ok.Sites >= 2);
                var amb = ShippedBinaryLiveArmedGuard.ScanZip(dup);                               // 정규화 후 같은 이름 2개 = 어느 쪽이 실행될지 모호
                Assert.False(amb.Ok);
                Assert.Contains(amb.Violations, v => v.Contains("중복", StringComparison.Ordinal));
            }
            finally { File.Delete(ps51); File.Delete(dup); }
        }

        [Fact]
        public void ShippedBinaryGuard_DefaultFlippedToTrue_OneBytePatch_Rejected()
        {
            byte[] bad = PatchDefaultsToTrue(File.ReadAllBytes(HostDll()), out int patched);
            Assert.True(patched >= 2, "패치 지점 " + patched);
            var r = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(bad));
            foreach (var v in r.Violations) _out.WriteLine("[방향 B] 위반: " + v);
            Assert.False(r.Ok);
            Assert.Equal(patched, r.Violations.Count);                                          // 패치한 지점마다 정확히 1건
            Assert.All(r.Violations, v => Assert.Contains("ldc.i4.1", v));
            string zip = ZipWithHost(bad);
            try { Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, zip }, TextWriter.Null)); }
            finally { File.Delete(zip); }
        }

        [Fact]
        public void ShippedBinaryGuard_Unreadable_Or_MissingEntry_FailsClosed()
        {
            byte[] dll = File.ReadAllBytes(HostDll());
            // 항목 누락(경로 이탈) · 잘린 PE · zip 아님 · 존재하지 않는 파일 = 전부 거부(못 읽음 ≠ 위반 없음)
            string wrongEntry = ZipWithHost(dll, "host/Other.dll"), truncated = ZipWithHost(dll.AsSpan(0, 4096).ToArray());
            string notZip = Path.Combine(Path.GetTempPath(), "ms2e_arm_" + Guid.NewGuid().ToString("N") + ".zip");
            File.WriteAllBytes(notZip, new byte[] { 1, 2, 3, 4, 5 });
            try
            {
                foreach (var z in new[] { wrongEntry, truncated, notZip, notZip + ".missing" })
                {
                    var r = ShippedBinaryLiveArmedGuard.ScanZip(z);
                    _out.WriteLine($"[방향 B] {Path.GetFileName(z)} → 지점 {r.Sites} · {string.Join(" | ", r.Violations)}");
                    Assert.False(r.Ok);
                    Assert.Equal(0, r.Sites);
                    Assert.NotEmpty(r.Violations);
                }
                Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, wrongEntry }, TextWriter.Null));
                Assert.Equal(ReleaseHandshakeCli.ExitUsage, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag }, TextWriter.Null));
                // 공허 이미지(키 조회 0) = 거부: xUnit 어셈블리는 키 문자열을 GetBool 에 넘기지 않는다
                var empty = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(File.ReadAllBytes(typeof(FactAttribute).Assembly.Location)));
                Assert.Equal(0, empty.Sites);
                Assert.False(empty.Ok);
                Assert.Equal(7, empty.Violations.Count(v => v.Contains("비공허성", StringComparison.Ordinal)));   // 키 2 + 마스터 플래그 생성 지점(v12.17.1-GM) + UI 기입자 3 · 허용 목록 1(v12.17.3-GM)
            }
            finally { foreach (var z in new[] { wrongEntry, truncated, notZip }) File.Delete(z); }
        }

        // ════════════ §6 [v12.17.0-GM · 방향 B] zip 항목 정규화 · 중복 가드 ════════════

        /// <summary>실 호스트 DLL + 봉인 비바이너리(runtimeconfig)를 봉인한 zip — 항목 이름은 PS 5.1 Compress-Archive 처럼 역슬래시.</summary>
        private static (string Zip, string Seal) SealedPs51Zip(string dir, Action<ZipArchive>? extra = null)
        {
            Directory.CreateDirectory(Path.Combine(dir, "host"));
            var files = new[]
            {
                ("host/MS2E.StockSuite.dll", File.ReadAllBytes(HostDll())),
                ("host/MS2E.StockSuite.runtimeconfig.json", Encoding.UTF8.GetBytes("{\"runtimeOptions\":{\"tfm\":\"net8.0\"}}")),
            };
            var arts = new List<ReleasePackager.ReleaseArtifact>();
            foreach (var (rel, bytes) in files)
            {
                string path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                File.WriteAllBytes(path, bytes);
                arts.Add(new ReleasePackager.ReleaseArtifact(rel, path));
            }
            string manifest = ReleasePackager.GenerateManifestJson(ReleasePackager.DefaultVersion + "-GM", arts);
            Assert.True(BootIntegrityGate.TryParse(manifest, out _, out string seal, out _));
            string zip = Path.Combine(dir, "pkg.zip");
            using (var za = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(za.CreateEntry("manifest.sig").Open(), new UTF8Encoding(false))) w.Write(manifest);
                foreach (var (rel, bytes) in files) using (var e = za.CreateEntry(rel.Replace('/', '\\')).Open()) e.Write(bytes);
                extra?.Invoke(za);
            }
            return (zip, seal);
        }

        private static void AddEntry(ZipArchive za, string name, byte[] bytes) { using var e = za.CreateEntry(name).Open(); e.Write(bytes); }

        [Fact]
        public void ZipEntryGuard_Ps51BackslashSealedZip_VerifiesAndIlInspects_AmbiguousDuplicatesRejected()
        {
            string root = Path.Combine(Path.GetTempPath(), "ms2e_zipguard_" + Guid.NewGuid().ToString("N"));
            try
            {
                var ok = SealedPs51Zip(Path.Combine(root, "ok"));
                Assert.Equal(ZipSealVerdict.Ok, ZipSealDryRunVerifier.Verify(ok.Zip, ok.Seal, out string d));    // 역슬래시 항목 = 정규화 후 봉인 일치
                _out.WriteLine("[방향 B] PS5.1 역슬래시 실봉인 zip: " + d);
                Assert.True(ShippedBinaryLiveArmedGuard.ScanZip(ok.Zip).Ok);                                        // 같은 zip 의 호스트 IL 판독

                byte[] dll = File.ReadAllBytes(HostDll());
                byte[] cfgEvil = Encoding.UTF8.GetBytes("{\"runtimeOptions\":{\"configProperties\":{\"evil\":true}}}");
                var cases = new (string Name, Action<ZipArchive> Extra)[]
                {
                    ("manifest.sig 2개", za => AddEntry(za, "manifest.sig", Encoding.UTF8.GetBytes("{}"))),
                    ("봉인 비바이너리 대소문자 변형", za => AddEntry(za, "HOST/ms2e.stocksuite.RUNTIMECONFIG.json", cfgEvil)),
                    ("구분자 변형 바이너리 사본", za => AddEntry(za, "host/MS2E.StockSuite.dll", dll)),
                    ("대소문자 변형 바이너리 사본", za => AddEntry(za, "Host\\ms2e.stocksuite.DLL", dll)),
                };
                int i = 0;
                foreach (var (name, extra) in cases)
                {
                    var bad = SealedPs51Zip(Path.Combine(root, "bad" + i++), extra);
                    var v = ZipSealDryRunVerifier.Verify(bad.Zip, bad.Seal, out string why);
                    _out.WriteLine($"[방향 B] {name} → {v} · {why}");
                    Assert.Equal(ZipSealVerdict.DuplicateEntry, v);
                }
                // IL 판독기도 모호한(정규화 · 대소문자 무시 후 2개) 호스트 항목 = 거부
                var amb = SealedPs51Zip(Path.Combine(root, "amb"), za => AddEntry(za, "HOST/MS2E.StockSuite.dll", dll));
                var g = ShippedBinaryLiveArmedGuard.ScanZip(amb.Zip);
                Assert.False(g.Ok);
                Assert.Contains(g.Violations, x => x.Contains("중복", StringComparison.Ordinal));
            }
            finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        // ════════════ §7 [v12.17.1-GM · 방향 A 확장] 마스터 플래그 IL 판독 ════════════

        private static (MethodDefinitionHandle Ctor, MethodDefinitionHandle Getter, int GetterOffset) FindMasterTargets(byte[] dll)
        {
            using var pe = new PEReader(new MemoryStream(dll));
            var md = pe.GetMetadataReader();
            MethodDefinitionHandle ctor = default, getter = default;
            int getterOffset = -1;
            foreach (var th in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(th);
                string tn = md.GetString(t.Name);
                foreach (var mh in t.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    string mn = md.GetString(m.Name);
                    if (tn == "ShadowMetricsBridge" && mn == ".ctor") ctor = mh;
                    if (tn == "DualBrokerStatusAdapter" && mn == "get_LiveArmed")
                    {
                        getter = mh;
                        Assert.True(pe.PEHeaders.TryGetDirectoryOffset(new DirectoryEntry(m.RelativeVirtualAddress, 1), out getterOffset));
                    }
                }
            }
            Assert.False(ctor.IsNil);
            Assert.False(getter.IsNil);
            return (ctor, getter, getterOffset);
        }

        [Fact]
        public void MasterFlag_BuiltHost_LiteralFalse_OneBytePatches_Rejected()
        {
            byte[] dll = File.ReadAllBytes(HostDll());
            var ok = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(dll));
            _out.WriteLine($"[방향 A] 키 조회 {ok.Sites} · 마스터 플래그 생성 {ok.MasterSites} · 위반 {ok.Violations.Count}");
            Assert.True(ok.Ok, string.Join(" | ", ok.Violations));
            Assert.True(ok.MasterSites >= 1);                                                    // BlazorHomeForm: new ShadowMetricsBridge(..., liveArmed: false)

            var (ctor, _, getterOffset) = FindMasterTargets(dll);
            // ① 마스터 플래그 리터럴 false → true(newobj 직전 ldc.i4.0)
            int token = MetadataTokens.GetToken(ctor);
            var bad = (byte[])dll.Clone();
            int patched = 0;
            for (int i = 1; i + 4 < bad.Length; i++)
                if (bad[i] == 0x73 && BitConverter.ToInt32(bad, i + 1) == token && bad[i - 1] == 0x16) { bad[i - 1] = 0x17; patched++; }
            Assert.True(patched >= 1, "마스터 플래그 생성 지점 패치 0");
            var r1 = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(bad));
            foreach (var v in r1.Violations) _out.WriteLine("[방향 A] 위반: " + v);
            Assert.False(r1.Ok);
            Assert.Equal(patched, r1.Violations.Count(v => v.Contains("마스터 플래그", StringComparison.Ordinal) && v.Contains("ldc.i4.1", StringComparison.Ordinal)));
            string zip = ZipWithHost(bad);
            try { Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, zip }, TextWriter.Null)); }
            finally { File.Delete(zip); }

            // ② get_LiveArmed 상수 getter(tiny 헤더 + ldc.i4.0; ret) → 항상 true
            var g = (byte[])dll.Clone();
            Assert.Equal(0x16, g[getterOffset + 1]);
            Assert.Equal(0x2A, g[getterOffset + 2]);
            g[getterOffset + 1] = 0x17;
            var r2 = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(g));
            Assert.False(r2.Ok);
            Assert.Contains(r2.Violations, v => v.Contains("DualBrokerStatusAdapter.get_LiveArmed", StringComparison.Ordinal) && v.Contains("항상 true", StringComparison.Ordinal));

            // ③ Func<bool> 술어 매개변수(liveArmed/isLiveArmed)는 '플래그'가 아니다 — 원본에서 위반 0 이 곧 증거(SecurityAssetVault · LiveArmedEgressSentinel)
            Assert.DoesNotContain(ok.Violations, v => v.Contains("SecurityAssetVault", StringComparison.Ordinal) || v.Contains("LiveArmedEgressSentinel", StringComparison.Ordinal));
        }

        [Fact]
        public void PackageScript_Step6_SourceGuardsMasterFlag()
        {
            string s = File.ReadAllText(Path.Combine(RepoRoot(), "release-package.ps1"));
            Assert.Contains("new ShadowMetricsBridge\\([^;]*?liveArmed:\\s*(\\w+)\\s*\\)", s);
            Assert.Contains("마스터 플래그", s);
            string form = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Blazor", "BlazorHomeForm.cs"));
            var m = Regex.Matches(form, @"new ShadowMetricsBridge\([^;]*?liveArmed:\s*(\w+)\s*\)");
            Assert.True(m.Count >= 1);
            Assert.All(m, x => Assert.Equal("false", x.Groups[1].Value));
        }

        [Fact]
        public void PackageScript_Step9_RunsBinaryLiveArmedGuard_BeforeManifestSync()
        {
            string s = File.ReadAllText(Path.Combine(RepoRoot(), "release-package.ps1"));
            int vz = s.IndexOf("'--verify-zip'", StringComparison.Ordinal), arm = s.IndexOf("'--verify-live-armed'", StringComparison.Ordinal),
                sync = s.IndexOf("'--sync-release-manifest'", StringComparison.Ordinal);
            Assert.True(vz > 0 && arm > vz && sync > arm, $"순서 vz={vz} arm={arm} sync={sync}");
            Assert.Contains("if ($armVerify -ne 0) { Remove-ReleaseOutputs; Fail", s.Substring(arm, sync - arm));   // 거부 = 출하 산출물 전량 폐기
        }

        // ════════════ §8 [v12.17.3-GM · 방향 A] UI 설정 기입 가드 IL 판독 ════════════

        private static int AdmitToken(byte[] dll)
        {
            using var pe = new PEReader(new MemoryStream(dll));
            var md = pe.GetMetadataReader();
            foreach (var th in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(th);
                if (md.GetString(t.Name) != "UiSettingWriteGuard") continue;
                foreach (var mh in t.GetMethods())
                    if (md.GetString(md.GetMethodDefinition(mh).Name) == "Admit") return MetadataTokens.GetToken(mh);
            }
            Assert.Fail("UiSettingWriteGuard.Admit 없음");
            return 0;
        }

        [Fact]
        public void UiSettingGuard_BuiltHost_AllWritersGated_OneBytePatches_Rejected()
        {
            byte[] dll = File.ReadAllBytes(HostDll());
            var ok = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(dll));
            _out.WriteLine($"[방향 A] UI 설정 기입 가드 {ok.UiGuardSites}/3 · 위반 {ok.Violations.Count}");
            Assert.True(ok.Ok, string.Join(" | ", ok.Violations));
            Assert.Equal(3, ok.UiGuardSites);

            // ① Admit 판정 무시: call Admit; brfalse.s xx → call Admit; pop; nop (IL 유효 · 기입 무조건 실행)
            int token = AdmitToken(dll);
            var bad = (byte[])dll.Clone();
            int patched = 0;
            for (int i = 0; i + 6 < bad.Length; i++)
                if (bad[i] == 0x28 && BitConverter.ToInt32(bad, i + 1) == token && bad[i + 5] == 0x2C) { bad[i + 5] = 0x26; bad[i + 6] = 0x00; patched++; }
            Assert.Equal(3, patched);                                                            // 기입자 3종 각 1 지점
            var r1 = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(bad));
            foreach (var v in r1.Violations) _out.WriteLine("[방향 A] 위반: " + v);
            Assert.False(r1.Ok);
            Assert.Equal(3, r1.Violations.Count(v => v.Contains("분기하지 않음", StringComparison.Ordinal)));
            string zip = ZipWithHost(bad);
            try { Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, zip }, TextWriter.Null)); }
            finally { File.Delete(zip); }

            // ② 허용 목록 오염: #US 힙의 "Email." (길이 13 = 6×2+1) → "Kis.ab" — 안전 네임스페이스 Kis. 가 UI 기입 가능해지는 바이너리
            byte[] from = new byte[] { 13 }.Concat(Encoding.Unicode.GetBytes("Email.")).ToArray();
            byte[] to = Encoding.Unicode.GetBytes("Kis.ab");
            var g = (byte[])dll.Clone();
            int at = g.AsSpan().IndexOf(from);
            Assert.True(at > 0, "#US \"Email.\" 없음");
            to.CopyTo(g, at + 1);
            var r2 = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(g));
            Assert.False(r2.Ok);
            Assert.Contains(r2.Violations, v => v.Contains("'Kis.ab' (Kis.)", StringComparison.Ordinal));
        }
    }
}
