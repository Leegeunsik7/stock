// ============================================================================
// GoldenMasterReleasePackagingTests — [출하 · v12.18.2-GM · 추천 A]
//
//  §1 버전 정합 — 호스트 어셈블리 = ReleasePackager.DefaultVersion = 12.18.2(출하 zip 이름 · 태그 v12.18.2-GM 의 원천).
//  §2 [방향 B · 실결함] 출하 롤백 가드 — 사전 검사 통과 직후 같은 버전 기존 산출물 전량 폐기(빌드 전) → [1]~[10] 어느 실패에도
//     직전 실행 zip 이 '통과한 출하물'처럼 잔존하지 않음 · 빌드 -warnaserror(MSBuild 경고 포함 = 게이트와 같은 판정).
//  §16 [v12.18.7-GM] ⑬ 유령 취소 무장 가드 IL 판독 — 빌드 DLL = 호스트 결선 1 · 위반 0 · CancelArmedNow→get_GhostDetected 토큰 패치(표본 제거) = 거부 ·
//     AttachCancelArming→AttachKisCanceller 패치 = 비공허성 거부 · CLI --require-cancel-arming · v12.18.6-GM 구 출하물 = ⑬ 미적용 Ok · 요구 플래그 = 거부.
//  §15 [v12.18.6-GM] ⑫ 호스트 펌프 지터 결선 IL 판독 — 빌드 DLL = BlazorHomeForm AttachPumpJitter 1곳 · 위반 0 · AttachJitter 직접 결선 토큰 패치 = 거부
//     (직접 결선 + 비공허성) · CLI --require-pump-jitter 정상 Ok/패치 거부 · v12.18.5-GM 구 출하물 = ⑫ 미적용 Ok · 요구 플래그 = 거부.
//  §13 [v12.18.5-GM] [0/10] 피어 정돈 하드 게이트 실 실행 — 임시 호스트(태그 = HEAD)+피어(봉인 = HEAD 커밋 1초 전)로 release-package.ps1 구동:
//     exit 1 · 판정기 exit=2(ManifestStale 단독) · [1/10] 진입 0 · zip/master-seal 생성 0 · 게이트 뒤 드리프트 재대조 미실행.
//  §3 [Fail-Closed Tag & Seal] 실 출하 기록(release/ · 존재할 때만 — 태그 기준 깨끗한 작업 트리에서 생성 후 복사):
//     매니페스트 자기 해시 · manifest.sig · master-seal.txt · zip SHA-256 · zip 무추출 재수검(Ok) · 소스 커밋 = 태그 커밋 ·
//     인터락 30종(0~28 + StartupSanity) = 태그 소스 열거 = zip 호스트 DLL 정의(§10 검증기) · 아티팩트 38 · liveArmed 기본 false · 피어 기록(있으면 이름 대조 · -NoPeer 면 부재) · 1비트 변조 zip = 거부 · 스트리머 δ=0.
//     + [v12.18.1-GM] 강화 전용 행렬 6키 = 태그 소스 = zip 호스트 DLL IL(§11 검증기) · --verify-live-armed 키 수 6 고정.
//     + [v12.18.2-GM] 감사 원장 결선(⑪ · §12 검사기) = 출하 zip 에서 요구 플래그로 Ok · 가드 ⑪ 적용.
//  §12 [v12.18.2-GM · 방향 A] ★감사 원장 결선 무추출 · 무할당 수검(ShippedTightenMatrixVerifier.CheckAuditWiring): 실 DLL = Ok · 1,000회 0B ·
//     패치 + 일관 재봉인(Append 분기 → br.s · Append → get_Frozen · 진입점 감사 인자 ldnull · 싱크 형식 개명) = 전부 거부 —
//     ★형식 개명은 가드 ⑪이 '구 바이너리'로 보고 생략(가드 단독 통과)하는 빈틈 → 요구 플래그가 닫음 · 스크립트 두 CLI 결선 · 구 출하물 호환.
//  §4 오중 빌드 회귀.
//  §11 [v12.18.1-GM · 방향 A] ★강화 전용 단조성 행렬 무추출 · 무할당 대조(ShippedTightenMatrixVerifier): 소스 판독 = 실행 표 = 가드 명세 ·
//     구 태그(v12.18.0-GM) = 빈 목록 · PS 5.1 zip 실 호스트 DLL ↔ 매니페스트 tightenRules ↔ master-seal.txt = Ok · 대조 루프 δ=0 ·
//     행렬 미기록 / 키 1개 제거 / 기대 5·7 / 명세 밖 방향 · 키 / 폴백 · 하한 · 상한 / 키 순서 교환 / 앵커 / 변조 / 중복 = 전부 거부 ·
//     ★바이너리 패치 + 일관된 재봉인(폴백 9500→5000 · 규칙 2→4 · clt→cgt · 상수 true · #US 키 개명) = 전부 거부 ·
//     핸드셰이크: 행렬을 지운 매니페스트 = TightenRuleMismatch · 스트리머 동등성 · 스크립트 [9]·[10] 결선 · CLI 종료 코드.
//  §10 [v12.18.0-GM · 방향 A] ★30대 인터락 봉인 무추출 대조(ShippedInterlockManifestVerifier): PS 5.1 역슬래시 zip(실 호스트 DLL) ↔ RELEASE_MANIFEST ↔
//     master-seal.txt = Ok(30 · 마스크 0x800000001FFFFFFF · OrderPathLatency=#28) · 정의 대조 1ms 미만 · 대조 루프 δ=0 ·
//     구 명세(29) 매니페스트 / 기대 29 / 이름 순서 교환 / 앵커 교체 / 봉인 해시 불일치 / 매니페스트 변조 / 정규화 중복 · 누락 · 손상 zip = 전부 거부 ·
//     ★바이너리 상수 28 → 29 패치 + 매니페스트 재봉인(일관된 위조) = 마스크 거부 · 스크립트 [10/10] 결선 · CLI 종료 코드.
//  §7 [v12.17.1-GM · 방향 A 확장] ★마스터 플래그 IL 판독: 빌드 산출 DLL = 마스터 플래그 생성 ≥1(ShadowMetricsBridge liveArmed 리터럴 false) ·
//     newobj 직전 ldc.i4.0→ldc.i4.1 1바이트 패치 = 거부 · get_LiveArmed 본문 ldc.i4.0→ldc.i4.1 = 거부 · Func<bool> 술어 매개변수 = 대상 아님 ·
//     스크립트 [6/10] 소스 가드도 마스터 플래그 대조.
//  §9 [v12.17.4-GM · 방향 A] ★시뮬레이션 격리 IL 판독: 빌드 DLL = 시뮬레이션 형식 ≥ 5 · CLI(--verify-ui-switches) 참조만 허용 · 위반 0 ·
//     호스트 조립부 newobj 토큰을 드라이런 하네스 생성자로 바꾼 사본 = BlazorHomeForm 위반으로 거부 · PS 5.1 역슬래시 zip 경유 판독 Ok · CLI 보고.
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
using System.Reflection.Emit;
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
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    public sealed class GoldenMasterReleasePackagingTests
    {
        private readonly ITestOutputHelper _out;
        public GoldenMasterReleasePackagingTests(ITestOutputHelper o) => _out = o;

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        // ════════════ §1 버전 ════════════

        [Fact]
        public void HostVersion_Is_12_18_83_And_DrivesReleaseNames()
        {
            var v = typeof(StockManager.Blazor.BlazorHomeForm).Assembly.GetName().Version!;
            Assert.Equal(new Version(12, 18, 83, 0), v);
            Assert.Equal("12.18.83", ReleasePackager.DefaultVersion);
            string csproj = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "StockManager.csproj"));
            Assert.Contains("<Version>12.18.83</Version>", csproj);
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
            Assert.Contains("v12.18.83-GM Production Golden Master", s);
        }

        // ════════════ §13 [v12.18.5-GM] [0/10] 피어 정돈 하드 게이트 — 실 스크립트 실행(낡은 봉인 1초 = 빌드 전 거부) ════════════

        [Fact]
        public void PackageScript_Executed_StalePeerOneSecond_RejectedAtStep0_NoBuild_NoOutputs()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "ms2e-gate0-" + Guid.NewGuid().ToString("N"));
            string hostRepo = Path.Combine(tmp, "host"), peer = Path.Combine(tmp, "peer");
            string link = Path.Combine(hostRepo, "StockManager", "bin", "Release", "net8.0-windows");
            string realBin = Path.Combine(RepoRoot(), "StockManager", "bin", "Release", "net8.0-windows");
            Assert.True(File.Exists(Path.Combine(realBin, "MS2E.StockSuite.exe")), "판정기(호스트 CLI) Release 산출물 필요");
            void G(string dir, params string[] a) => Assert.NotNull(GitRepoProbe.Git(dir,
                new[] { "-c", "user.name=probe", "-c", "user.email=probe@local", "-c", "commit.gpgsign=false" }.Concat(a).ToArray()));
            try
            {
                // 호스트: 태그 v12.18.5-GM = HEAD · 미커밋 코드 0 (소스 사전 검사 통과) · 판정기는 실 Release 산출물 정션(512MB 복사 회피 · gitignore)
                Directory.CreateDirectory(Path.Combine(hostRepo, "StockManager"));
                Assert.NotNull(GitRepoProbe.Git(hostRepo, "init", "-q", "-b", "master"));
                File.WriteAllBytes(Path.Combine(hostRepo, "release-package.ps1"), File.ReadAllBytes(Path.Combine(RepoRoot(), "release-package.ps1")));
                File.WriteAllText(Path.Combine(hostRepo, "StockManager", "StockManager.csproj"), "<Project><PropertyGroup><Version>12.18.5</Version></PropertyGroup></Project>\n");
                File.WriteAllText(Path.Combine(hostRepo, ".gitignore"), "StockManager/bin/\n");
                G(hostRepo, "add", "-A");
                G(hostRepo, "commit", "-q", "-m", "host");
                G(hostRepo, "tag", "v12.18.5-GM");
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{realBin}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                mk.StandardOutput.ReadToEnd(); mk.WaitForExit();
                Assert.Equal(0, mk.ExitCode);

                // 피어: 브랜치 master · 깨끗함 · 봉인 = HEAD 커밋 1초 전(ManifestStale) — 기존 사전 점검(존재 · 깨끗함)은 통과하는 유일 결함
                Directory.CreateDirectory(peer);
                Assert.NotNull(GitRepoProbe.Git(peer, "init", "-q", "-b", "master"));
                File.WriteAllText(Path.Combine(peer, ".gitignore"), "release/offline-bundle/\n");
                File.WriteAllText(Path.Combine(peer, "Core.cs"), "class Core {}\n");
                G(peer, "add", "-A");
                G(peer, "commit", "-q", "-m", "init");
                var headAt = DateTimeOffset.Parse(GitRepoProbe.Git(peer, "log", "-1", "--format=%cI", "HEAD")!, System.Globalization.CultureInfo.InvariantCulture);
                string manifest = Path.Combine(peer, "release", "offline-bundle", "RELEASE_MANIFEST.json");
                Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
                File.WriteAllText(manifest, "{\"rootHash\":\"" + new string('b', 64) + "\",\"sealedAtUtc\":\"" + headAt.AddSeconds(-1).UtcDateTime.ToString("O") + "\"}");

                var psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(hostRepo, "release-package.ps1")}\" -SkipWebOnlyGate -PeerRepo \"{peer}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = hostRepo };
                var sw = Stopwatch.StartNew();
                using var ps = Process.Start(psi)!;
                string stdout = ps.StandardOutput.ReadToEnd();
                if (!ps.WaitForExit(120_000)) { ps.Kill(true); Assert.Fail("스크립트 120초 무응답 — 게이트 미작동(빌드 진입) 의심"); }
                sw.Stop();
                _out.WriteLine($"[§13 실행] exit={ps.ExitCode} · {sw.ElapsedMilliseconds}ms\n{stdout}");

                Assert.Equal(1, ps.ExitCode);                                                       // Fail = exit 1
                Assert.Contains("[0/11]", stdout);                                                  // v12.18.28-DEV 단계 표기 /11([11/11] 배포본)
                Assert.Contains("exit=2", stdout);                                                  // 판정기 ManifestStale = CLI 2 → 착수 거부
                Assert.DoesNotContain("[1/11]", stdout);                                            // 빌드 단계 진입 0
                Assert.DoesNotContain("[2/11]", stdout);
                string report = File.ReadAllText(Path.Combine(hostRepo, "release", "cli-peer-readiness.txt"), Encoding.UTF8);
                Assert.Contains("ManifestStale", report);
                Assert.DoesNotContain("UncommittedCode", report);                                   // 거부 사유 = 낡은 봉인 단독
                Assert.False(File.Exists(Path.Combine(hostRepo, "release", "release-v12.18.5-GM.zip")));
                Assert.False(File.Exists(Path.Combine(hostRepo, "release", "master-seal.txt")));
                Assert.False(File.Exists(Path.Combine(hostRepo, "release", "cli-peer-drift-readiness.txt")));   // 게이트 뒤 단계 미실행
                Assert.True(sw.ElapsedMilliseconds < 60_000, $"[0/10] 거부 {sw.ElapsedMilliseconds}ms — 빌드 오버헤드 없이 끝나야 함");
            }
            finally
            {
                if (Directory.Exists(link)) Directory.Delete(link, false);                          // 정션만 제거(대상 실 산출물 보존)
                try
                {
                    foreach (var f in Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(tmp, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
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
            if (m.Peer is { } peer)
            {
                Assert.Equal("QuantumVault", peer.Name);
                // [v12.18.5-GM · 이중 저장소] 피어 교차 지문 형식 + 기록 경로 · 피어가 아직 그 커밋 · 그 봉인이면 드리프트 가드로 재대조(읽기 전용)
                Assert.True(PeerDriftGuard.IsHex(peer.Commit, 40) && PeerDriftGuard.IsHex(peer.ManifestSha256, 64) && peer.RootHash.Length >= 64, "피어 교차 지문 형식");
                Assert.Equal(ReleaseHandshakeCli.PeerManifestDefault, peer.ManifestPath);
                string qv = Path.Combine(Path.GetDirectoryName(root)!, "QuantumVault");
                var (pb, ph, ps) = PeerDriftGuard.Observe(qv);
                if (pb is not null && string.Equals(ph, peer.Commit, StringComparison.OrdinalIgnoreCase) && string.Equals(ps, peer.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                {
                    var drift = PeerDriftGuard.Check(qv, pb, peer.Commit, peer.ManifestSha256);
                    _out.WriteLine($"[출하] 피어 교차 재대조 {drift.Verdict} · {drift.Detail}");
                    Assert.True(drift.Ok, drift.Detail);
                }
                else _out.WriteLine($"[출하] 피어가 출하 이후 이동(HEAD {ph ?? "?"}) — 기록 형식만 대조");
            }
            else _out.WriteLine("[출하] 피어 기록 없음(-NoPeer) — 피어 미커밋 상태에선 핸드셰이크 고정을 하지 않는다");
            // ★소스 커밋 = 태그 커밋(태그 재지정 후 1비트라도 어긋나면 거부)
            string? tagCommit = GitRepoProbe.Git(root, "rev-list", "-n", "1", tag)?.Trim();
            Assert.False(string.IsNullOrEmpty(tagCommit), "태그 없음: " + tag);
            Assert.Equal(tagCommit, m.SourceCommit, ignoreCase: true);
            // 인터락 30종(0~28 + StartupSanity) = 태그 소스 열거
            var tagLocks = GitRepoProbe.TagInterlockNames(root, m.SourceCommit);
            Assert.NotNull(tagLocks);
            Assert.Equal(m.Interlocks, tagLocks!);
            Assert.Equal(30, m.Interlocks.Count);
            Assert.Equal("OrderPathLatency", m.Interlocks[28]);
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
            // [v12.18.0-GM · 방향 A] zip 호스트 DLL 인터락 정의 = 매니페스트 = 앵커(무추출)
            var ilk = ShippedInterlockManifestVerifier.Verify(zip, json, anchor, 30);
            _out.WriteLine($"[출하] 인터락 봉인 대조 {ilk.Verdict} · {ilk.Detail} · 팽창 {ilk.InflateMicros}µs · 대조 {ilk.CompareMicros}µs");
            Assert.Equal(InterlockSealVerdict.Ok, ilk.Verdict);
            Assert.Equal(0x800000001FFFFFFFUL, ilk.DefinitionMask);
            // [방향 B] 출하 zip 안 호스트 DLL 이중 물리 키 기본값 = 리터럴 false(IL 직접 판독)
            var arm = ShippedBinaryLiveArmedGuard.ScanZip(zip);
            Assert.True(arm.Ok, "출하 바이너리 liveArmed 판독 거부: 지점 " + arm.Sites + " · " + string.Join(" | ", arm.Violations));
            // [v12.18.1-GM · 방향 A] 강화 전용 행렬 6키 = 태그 소스 = zip 호스트 DLL IL(무추출) · 가드 판독 키 수 6
            Assert.Equal(TightenKeys, arm.TightenKeys);
            Assert.NotNull(m.TightenRules);
            Assert.Equal(TightenKeys, m.TightenRules!.Count);
            var tagRules = GitRepoProbe.TagTightenRules(root, m.SourceCommit);
            Assert.NotNull(tagRules);
            Assert.Equal(m.TightenRules, tagRules!);
            Assert.Equal("Strategy.AdaptiveMaxBp|2|9500|5000|10000", m.TightenRules[3]);
            var tight = ShippedTightenMatrixVerifier.Verify(zip, json, anchor, TightenKeys);
            _out.WriteLine($"[출하] 강화 전용 행렬 대조 {tight.Verdict} · {tight.Detail} · 팽창 {tight.InflateMicros}µs · 대조 {tight.CompareMicros}µs");
            Assert.Equal(TightenSealVerdict.Ok, tight.Verdict);
            Assert.Equal(TightenKeys, st.TightenRuleCount);
            // [v12.18.2-GM · 방향 A] 감사 원장 결선(⑪) — 출하 zip 호스트 DLL · 요구 플래그
            Assert.True(arm.AuditLedgerAware, "출하 바이너리 감사 원장 ⑪ 미적용");
            // [v12.18.6-GM] ⑫ 호스트 펌프 지터 결선(12.18.6 이후 출하물) — 스크립트 [9/10] --require-pump-jitter 와 같은 요구
            if (new Version(ver) >= new Version(12, 18, 6)) Assert.True(arm.PumpJitterSites >= 1, "출하 바이너리 호스트 펌프 지터 결선 ⑫ 0");
            // [v12.18.7-GM] ⑬ 유령 취소 무장 가드(12.18.7 이후 출하물) — 스크립트 [9/10] --require-cancel-arming 과 같은 요구
            if (new Version(ver) >= new Version(12, 18, 7)) Assert.True(arm.CancelArmingSites >= 1, "출하 바이너리 유령 취소 무장 결선 ⑬ 0");
            var audit = ShippedTightenMatrixVerifier.Verify(zip, json, anchor, TightenKeys, requireAuditLedger: true);
            _out.WriteLine($"[출하] 감사 원장 결선 {audit.Verdict} · {audit.Detail}");
            Assert.Equal(TightenSealVerdict.Ok, audit.Verdict);
            // [v12.18.3-GM ⑤ · v12.18.4-GM ⑥] 관제 배지 무잠금 결선 · IPC 패브릭 무슬립 위상 — 출하 zip 호스트 DLL(스크립트 [10/10] 과 같은 요구 플래그)
            var full = ShippedTightenMatrixVerifier.Verify(zip, json, anchor, TightenKeys, requireAuditLedger: true, requireAuditTelemetry: true, requireIpcFabric: true);
            _out.WriteLine($"[출하] ⑪+⑤+⑥ {full.Verdict} · {full.Detail}");
            Assert.Equal(TightenSealVerdict.Ok, full.Verdict);
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
            Assert.Equal(30, Enum.GetValues<ArmingInterlockSource>().Length);                   // v12.18.0-GM: 0~28 + StartupSanity = 출하 매니페스트 · 스크립트 $ExpectedInterlocks
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
                Assert.Equal(8, empty.Violations.Count(v => v.Contains("비공허성", StringComparison.Ordinal)));   // 키 2 + 마스터 플래그 생성 지점(v12.17.1-GM) + UI 기입자 3 · 허용 목록 1(v12.17.3-GM) + 시뮬레이션 형식 1(v12.17.4-GM) · 강화 전용 경로 전체 부재 = 위반 아님(v12.18.3-DEV)
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

        // ════════════ §9 [v12.17.4-GM · 방향 A] 시뮬레이션 격리 IL 판독 ════════════

        private static int CtorToken(byte[] dll, string typeName)
        {
            using var pe = new PEReader(new MemoryStream(dll));
            var md = pe.GetMetadataReader();
            foreach (var th in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(th);
                if (md.GetString(t.Name) != typeName) continue;
                foreach (var mh in t.GetMethods())
                    if (md.GetString(md.GetMethodDefinition(mh).Name) == ".ctor") return MetadataTokens.GetToken(mh);
            }
            Assert.Fail(typeName + " 생성자 없음");
            return 0;
        }

        [Fact]
        public void SimulationQuarantine_BuiltHost_OnlyCliReferences_HostWiringPatch_Rejected()
        {
            byte[] dll = File.ReadAllBytes(HostDll());
            var ok = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(dll));
            _out.WriteLine($"[방향 A] 키 조회 {ok.Sites} · 마스터 {ok.MasterSites} · UI 가드 {ok.UiGuardSites}/3 · 시뮬레이션 형식 {ok.SimulationTypes} · CLI 참조 {ok.SimulationCliSites} · 위반 {ok.Violations.Count}");
            Assert.True(ok.Ok, string.Join(" | ", ok.Violations));
            Assert.True(ok.SimulationTypes >= 5, "시뮬레이션 형식 " + ok.SimulationTypes);
            Assert.True(ok.SimulationCliSites >= 1);                                             // --verify-ui-switches → UiFuzzRig → 하네스

            // 호스트 조립부가 실 송출 센티널 대신 드라이런 하네스를 생성하도록 바뀐 바이너리(newobj 4바이트 토큰 교체 — 판독기는 스택 무검증)
            int from = CtorToken(dll, "LiveArmedEgressSentinel"), to = CtorToken(dll, "MultiBrokerDryRunHarness");
            var bad = (byte[])dll.Clone();
            int patched = 0;
            for (int i = 0; i + 4 < bad.Length; i++)
                if (bad[i] == 0x73 && BitConverter.ToInt32(bad, i + 1) == from) { BitConverter.GetBytes(to).CopyTo(bad, i + 1); patched++; }
            Assert.True(patched >= 2, "패치 " + patched);
            var r = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(bad));
            foreach (var v in r.Violations) _out.WriteLine("[방향 A] 위반: " + v);
            Assert.False(r.Ok);
            Assert.Contains(r.Violations, v => v.StartsWith("BlazorHomeForm.", StringComparison.Ordinal) && v.Contains("MultiBrokerDryRunHarness", StringComparison.Ordinal));
            Assert.DoesNotContain(r.Violations, v => v.StartsWith("PaperTradingSimulationHarness.", StringComparison.Ordinal));   // 시뮬레이션 내부 참조 = 허용

            // PS 5.1 역슬래시 항목 zip 경유 판독 + CLI 보고(정상 Ok · 패치 거부)
            string good = ZipWithHost(dll, "host\\MS2E.StockSuite.dll"), evil = ZipWithHost(bad, "host\\MS2E.StockSuite.dll");
            try
            {
                var sw = new StringWriter();
                Assert.Equal(ReleaseHandshakeCli.ExitOk, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, good }, sw));
                Assert.Contains("시뮬레이션 격리 형식", sw.ToString());
                Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, evil }, TextWriter.Null));
            }
            finally { File.Delete(good); File.Delete(evil); }
        }
    

        // ════════════ §10 [v12.18.0-GM · 방향 A] 30대 인터락 봉인 무추출 대조 ════════════

        private const ulong ThirtyLayerMask = 0x800000001FFFFFFFUL;

        /// <summary>§6 봉인 zip(PS 5.1 역슬래시 · 실 호스트 DLL) + 그 봉인으로 기록한 RELEASE_MANIFEST(인터락 = 빌드 열거 이름 · 호스트 DLL 해시).</summary>
        private static (string Zip, string Seal, string Json) InterlockFixture(string dir, IReadOnlyList<string>? interlocks = null, string? hostSha = null,
                                                                             Action<ZipArchive>? extra = null)
        {
            Directory.CreateDirectory(dir);
            var (zip, seal) = SealedPs51Zip(dir, extra);
            string sha = hostSha ?? ReleaseHandshakeCli.FileSha(HostDll());
            string hex = new string('A', 64);
            var m = new ReleaseManifestInput(ReleasePackager.DefaultVersion + "-GM", "v" + ReleasePackager.DefaultVersion + "-GM", new string('a', 40), hex, seal, hex,
                new List<(string, string)> { ("host/MS2E.StockSuite.dll", sha) }, "pkg.zip", hex, hex, false,
                interlocks ?? Enum.GetNames<ArmingInterlockSource>(), null);
            return (zip, seal, MultiRepoReleaseManifest.Build(m));
        }

        private static string TempDir() { string d = Path.Combine(Path.GetTempPath(), "ms2e_ilk_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

        private static string EvilManifest(byte[] dll, string seal)
        {
            string hex = new string('A', 64);
            return MultiRepoReleaseManifest.Build(new ReleaseManifestInput("x-GM", "vx-GM", new string('a', 40), hex, seal, hex,
                new List<(string, string)> { ("host/MS2E.StockSuite.dll", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dll))) },
                "evil.zip", hex, hex, false, Enum.GetNames<ArmingInterlockSource>(), null));
        }

        [Fact]
        public void InterlockSeal_BuiltHost_Ps51Zip_ThirtyLayers_Ok_CompareUnderOneMs_LoopZeroAlloc()
        {
            string dir = TempDir();
            try
            {
                var (zip, seal, json) = InterlockFixture(dir);
                var r = ShippedInterlockManifestVerifier.Verify(zip, json, seal + "\r\n", 30);       // master-seal.txt = 개행 포함(Set-Content)
                Assert.True(r.Verdict == InterlockSealVerdict.Ok, r.Verdict + " " + r.Detail);
                Assert.Equal(30, r.Count);
                Assert.Equal(ThirtyLayerMask, r.DefinitionMask);
                Assert.Equal(ThirtyLayerMask, ShippedInterlockManifestVerifier.ExpectedMaskFor(30));
                Assert.Equal(ReleaseHandshakeCli.FileSha(HostDll()), r.HostSha256);
                Assert.Equal("OrderPathLatency", Enum.GetNames<ArmingInterlockSource>()[28]);

                // 정의 대조(PE 헤더 · 메타데이터 판독 + 대조) — 1ms 예산 · 반복 최솟값(JIT · 캐시 제외)
                long best = long.MaxValue;
                for (int i = 0; i < 7; i++) best = Math.Min(best, ShippedInterlockManifestVerifier.Verify(zip, json, seal, 30).CompareMicros);
                _out.WriteLine($"[인터락 봉인] 팽창+해시 {r.InflateMicros}µs · 정의 대조 최솟값 {best}µs · 마스크 0x{r.DefinitionMask:X16}");
                Assert.True(best < 1_000, "정의 대조 ≥ 1ms: " + best + "µs");

                // 대조 루프 δ=0(MetadataStringComparer · 스택 버퍼)
                byte[] dll = File.ReadAllBytes(HostDll());
                using var pe = new PEReader(System.Collections.Immutable.ImmutableArray.Create(dll));
                var md = pe.GetMetadataReader();
                IReadOnlyList<string> names = Enum.GetNames<ArmingInterlockSource>();
                Assert.Equal(InterlockSealVerdict.Ok, ShippedInterlockManifestVerifier.CompareDefinition(md, names, ThirtyLayerMask, out _, out _, out _));
                Assert.Equal(0, MinAllocated(() => ShippedInterlockManifestVerifier.CompareDefinition(md, names, ThirtyLayerMask, out _, out _, out _)));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void InterlockSeal_SpecDrift_Anchor_Hash_Tamper_Duplicate_AllRejected()
        {
            string dir = TempDir();
            try
            {
                var names = Enum.GetNames<ArmingInterlockSource>();
                var (zip, seal, json) = InterlockFixture(Path.Combine(dir, "ok"));
                InterlockSealVerdict V(string z, string j, string a, int n) => ShippedInterlockManifestVerifier.Verify(z, j, a, n).Verdict;

                // 구 명세(v12.17.4-GM · 29 — OrderPathLatency 없음) 매니페스트 ↔ 30대 바이너리
                var old = names.Where(n => n != "OrderPathLatency").ToArray();
                Assert.Equal(29, old.Length);
                var (zOld, sOld, jOld) = InterlockFixture(Path.Combine(dir, "old"), old);
                Assert.Equal(InterlockSealVerdict.CountMismatch, V(zOld, jOld, sOld, 29));              // 바이너리 30 ≠ 매니페스트 29
                Assert.Equal(InterlockSealVerdict.CountMismatch, V(zOld, jOld, sOld, 30));              // 매니페스트 29 ≠ 스크립트 명세 30
                Assert.Equal(InterlockSealVerdict.CountMismatch, V(zip, json, seal, 29));               // 스크립트 명세 미갱신
                Assert.Equal(InterlockSealVerdict.CountMismatch, V(zip, json, seal, 0));

                // 이름 순서 교환(27 ↔ 28) · 개명
                var swapped = (string[])names.Clone(); (swapped[27], swapped[28]) = (swapped[28], swapped[27]);
                var (zSw, sSw, jSw) = InterlockFixture(Path.Combine(dir, "sw"), swapped);
                var rSw = ShippedInterlockManifestVerifier.Verify(zSw, jSw, sSw, 30);
                Assert.Equal(InterlockSealVerdict.NameMismatch, rSw.Verdict);
                Assert.Contains("#27", rSw.Detail);
                var renamed = (string[])names.Clone(); renamed[28] = "OrderPathLatencyX";
                var (zRn, sRn, jRn) = InterlockFixture(Path.Combine(dir, "rn"), renamed);
                Assert.Equal(InterlockSealVerdict.NameMismatch, V(zRn, jRn, sRn, 30));

                // 앵커 교체 · 형식 이탈
                Assert.Equal(InterlockSealVerdict.AnchorMismatch, V(zip, json, new string('B', 64), 30));
                Assert.Equal(InterlockSealVerdict.AnchorMismatch, V(zip, json, "", 30));

                // 봉인 해시 ≠ zip 안 DLL(판독 대상 ≠ 봉인 대상)
                var (zH, sH, jH) = InterlockFixture(Path.Combine(dir, "h"), hostSha: new string('C', 64));
                Assert.Equal(InterlockSealVerdict.ArtifactHashMismatch, V(zH, jH, sH, 30));

                // 매니페스트 변조(재봉인 없이 이름 1자) · liveArmedDefault=true · 파싱 불가
                Assert.Equal(InterlockSealVerdict.ManifestInvalid, V(zip, json.Replace("\"OrderPathLatency\"", "\"OrderPathLatencY\""), seal, 30));
                Assert.Equal(InterlockSealVerdict.ManifestInvalid, V(zip, json.Replace("\"liveArmedDefault\": false", "\"liveArmedDefault\": true"), seal, 30));
                Assert.Equal(InterlockSealVerdict.ManifestInvalid, V(zip, "{", seal, 30));

                // [방향 B] 정규화 중복(역슬래시 + 슬래시 · 대소문자) · 누락 · 손상
                byte[] dll = File.ReadAllBytes(HostDll());
                var (zD, sD, jD) = InterlockFixture(Path.Combine(dir, "d"), extra: za => AddEntry(za, "host/MS2E.StockSuite.dll", dll));
                Assert.Equal(InterlockSealVerdict.EntryDuplicate, V(zD, jD, sD, 30));
                var (zC, sC, jC) = InterlockFixture(Path.Combine(dir, "c"), extra: za => AddEntry(za, "HOST\\ms2e.stocksuite.runtimeconfig.JSON", new byte[] { 1 }));
                Assert.Equal(InterlockSealVerdict.EntryDuplicate, V(zC, jC, sC, 30));
                string noHost = Path.Combine(dir, "nohost.zip");
                using (var za = ZipFile.Open(noHost, ZipArchiveMode.Create)) AddEntry(za, "manifest.sig", new byte[] { 1 });
                Assert.Equal(InterlockSealVerdict.EntryMissing, V(noHost, json, seal, 30));
                string junk = Path.Combine(dir, "junk.zip");
                File.WriteAllBytes(junk, Encoding.ASCII.GetBytes("not a zip at all"));
                Assert.Equal(InterlockSealVerdict.ZipUnreadable, V(junk, json, seal, 30));
                Assert.Equal(InterlockSealVerdict.ZipUnreadable, V(Path.Combine(dir, "absent.zip"), json, seal, 30));

                // 원본은 여전히 Ok(거부 사례가 픽스처를 오염시키지 않았다)
                Assert.Equal(InterlockSealVerdict.Ok, V(zip, json, seal, 30));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void InterlockSeal_BinaryConstantPatched_ConsistentlyResealedManifest_MaskRejected()
        {
            // 공격 모델: 호스트 DLL 의 OrderPathLatency 상수(28)를 29 로 바꾸고 매니페스트를 '그 바이너리'로 일관되게 재봉인 —
            //  해시 · 이름 · 개수는 전부 맞는다. 남는 방어선 = 정의 비트마스크 구조 규칙(0~28 연속 + 63).
            byte[] dll = File.ReadAllBytes(HostDll());
            int off;
            using (var pe = new PEReader(System.Collections.Immutable.ImmutableArray.Create(dll)))
            {
                var md = pe.GetMetadataReader();
                var td = md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t => md.GetString(t.Name) == "ArmingInterlockSource" && md.GetString(t.Namespace) == "MS2E.Core");
                var f = td.GetFields().Select(md.GetFieldDefinition).Single(x => md.GetString(x.Name) == "OrderPathLatency");
                var blob = md.GetConstant(f.GetDefaultValue()).Value;
                off = pe.PEHeaders.MetadataStartOffset + md.GetHeapMetadataOffset(HeapIndex.Blob) + MetadataTokens.GetHeapOffset(blob) + 1;   // +1 = 압축 길이 접두
            }
            Assert.Equal(28, dll[off]);
            byte[] evil = (byte[])dll.Clone();
            evil[off] = 29;
            string dir = TempDir();
            try
            {
                string seal = new string('D', 64);
                string zip = Path.Combine(dir, "evil.zip");
                using (var za = ZipFile.Open(zip, ZipArchiveMode.Create)) AddEntry(za, "host\\MS2E.StockSuite.dll", evil);
                var r = ShippedInterlockManifestVerifier.Verify(zip, EvilManifest(evil, seal), seal, 30);
                Assert.Equal(InterlockSealVerdict.MaskMismatch, r.Verdict);
                Assert.Equal((ThirtyLayerMask & ~(1UL << 28)) | (1UL << 29), r.DefinitionMask);

                evil[off] = 27;                                                                         // 값 중복(27 두 번)
                string zip2 = Path.Combine(dir, "evil2.zip");
                using (var za = ZipFile.Open(zip2, ZipArchiveMode.Create)) AddEntry(za, "host\\MS2E.StockSuite.dll", evil);
                Assert.Equal(InterlockSealVerdict.ValueInvalid, ShippedInterlockManifestVerifier.Verify(zip2, EvilManifest(evil, seal), seal, 30).Verdict);

                // 대조군: 패치 없는 원본 + 같은 방식 재봉인 = Ok(거부가 재봉인 절차 탓이 아님)
                string zip3 = Path.Combine(dir, "good.zip");
                using (var za = ZipFile.Open(zip3, ZipArchiveMode.Create)) AddEntry(za, "host\\MS2E.StockSuite.dll", dll);
                Assert.Equal(InterlockSealVerdict.Ok, ShippedInterlockManifestVerifier.Verify(zip3, EvilManifest(dll, seal), seal, 30).Verdict);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void InterlockSeal_PackageScript_Step10_Wired_SpecPinned_CliExitCodes()
        {
            string s = File.ReadAllText(Path.Combine(RepoRoot(), "release-package.ps1"));
            var spec = Regex.Match(s, @"^\$ExpectedInterlocks = (\d+)", RegexOptions.Multiline);
            Assert.True(spec.Success, "스크립트 인터락 명세 고정값 없음");
            Assert.Equal(Enum.GetValues<ArmingInterlockSource>().Length, int.Parse(spec.Groups[1].Value));
            int hs = s.IndexOf("$hsExit = Invoke-HostCli", StringComparison.Ordinal), ilk = s.IndexOf("'--verify-interlocks'", StringComparison.Ordinal),
                done = s.IndexOf("== 완료:", StringComparison.Ordinal);
            Assert.True(hs > 0 && ilk > hs && done > ilk, "★인터락 봉인 대조 = 매니페스트 기록 · 핸드셰이크 뒤 · 완료 앞");
            Assert.Contains("if ($ilkExit -ne 0) { Remove-ReleaseOutputs; Fail", s);
            Assert.True(ReleaseHandshakeCli.IsInvocation(new[] { "--verify-interlocks" }));

            string dir = TempDir();
            try
            {
                var (zip, seal, json) = InterlockFixture(dir);
                string mf = Path.Combine(dir, "RELEASE_MANIFEST.json"), sf = Path.Combine(dir, "master-seal.txt");
                File.WriteAllText(mf, json);
                File.WriteAllText(sf, seal + Environment.NewLine);
                int Run(params string[] a) { var w = new StringWriter(); int c = ReleaseHandshakeCli.Run(a, w); _out.WriteLine(w.ToString().TrimEnd()); return c; }
                Assert.Equal(ReleaseHandshakeCli.ExitOk, Run("--verify-interlocks", zip, "--manifest", mf, "--seal-file", sf, "--expect-interlocks", "30"));
                Assert.Equal(ReleaseHandshakeCli.ExitRejected, Run("--verify-interlocks", zip, "--manifest", mf, "--seal-file", sf, "--expect-interlocks", "29"));
                Assert.Equal(ReleaseHandshakeCli.ExitUsage, Run("--verify-interlocks", zip, "--manifest", mf, "--seal-file", sf));
                Assert.Equal(ReleaseHandshakeCli.ExitUsage, Run("--verify-interlocks", zip, "--seal-file", sf, "--expect-interlocks", "30"));
            }
            finally { Directory.Delete(dir, true); }
        }

        // ════════════ §11 [v12.18.1-GM · 방향 A] 강화 전용 단조성 행렬 무추출 · 무할당 대조 ════════════

        private const int TightenKeys = 6;

        /// <summary>현재 작업 트리 소스의 행렬(태그 소스 판독기와 같은 파서) — 출하 시 sync 가 태그 커밋에서 같은 방식으로 기록한다.</summary>
        private static IReadOnlyList<string> SourceRules()
        {
            var rows = GitRepoProbe.ParseTightenMatrix(File.ReadAllText(Path.Combine(RepoRoot(), GitRepoProbe.TightenSourcePath.Replace('/', Path.DirectorySeparatorChar))));
            Assert.NotNull(rows);
            return rows!;
        }

        private static string TightenManifest(byte[] dll, string seal, IReadOnlyList<string>? rules)
        {
            string hex = new string('A', 64);
            return MultiRepoReleaseManifest.Build(new ReleaseManifestInput(ReleasePackager.DefaultVersion + "-GM", "v" + ReleasePackager.DefaultVersion + "-GM", new string('a', 40), hex, seal, hex,
                new List<(string, string)> { ("host/MS2E.StockSuite.dll", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dll))) },
                "pkg.zip", hex, hex, false, Enum.GetNames<ArmingInterlockSource>(), null, rules));
        }

        private static string Ps51HostZip(string dir, string name, byte[] dll)
        {
            string zip = Path.Combine(dir, name);
            using (var za = ZipFile.Open(zip, ZipArchiveMode.Create)) AddEntry(za, "host\\MS2E.StockSuite.dll", dll);   // PS 5.1 역슬래시 항목
            return zip;
        }

        [Fact]
        public void TightenMatrix_SourceParse_EqualsWriterTables_GuardSpec_OldTagEmpty()
        {
            var rules = SourceRules();
            Assert.Equal(TightenKeys, rules.Count);
            Assert.Equal(TightenKeys, TightenOnlyUiSettingWriter.KeyCount);
            Assert.Equal(TightenKeys, ShippedBinaryLiveArmedGuard.TightenSpecCount);
            for (int i = 0; i < rules.Count; i++)
            {
                string expect = FormattableString.Invariant($"{TightenOnlyUiSettingWriter.KeyAt(i)}|{TightenOnlyUiSettingWriter.RuleAt(i)}|{TightenOnlyUiSettingWriter.FallbackAt(i)}|{TightenOnlyUiSettingWriter.MinimumAt(i)}|{TightenOnlyUiSettingWriter.MaximumAt(i)}");
                Assert.Equal(expect, rules[i]);                                                   // 소스 판독 = 실행 표(파서가 표를 잘못 읽지 않는다)
                Assert.True(ShippedTightenMatrixVerifier.TryParseRow(rules[i], out var row));
                Assert.True(ShippedBinaryLiveArmedGuard.TryTightenSpec(row.Key, out byte spec) && spec == row.Rule, rules[i]);
            }
            Assert.Equal(new[]
            {
                "Strategy.SignalGateEnabled|1|0|0|1", "Strategy.AdaptiveThreshold|1|1|0|1", "Strategy.ConfidenceThresholdBp|2|8500|5000|10000",
                "Strategy.AdaptiveMaxBp|2|9500|5000|10000", "Strategy.ChurnSoftPerMin|4|30|1|100000", "Strategy.ChurnHardPerMin|4|60|2|100000",
            }, rules);

            // 형식 이탈 = null(fail-closed): 열 길이 불일치 · 미지 규칙 · 표 중복 정의 · 표 부재
            string src = File.ReadAllText(Path.Combine(RepoRoot(), GitRepoProbe.TightenSourcePath.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Null(GitRepoProbe.ParseTightenMatrix(src.Replace("{ 0, 1, 8_500, 9_500, 30, 60 }", "{ 0, 1, 8_500, 9_500, 30 }")));
            Assert.Null(GitRepoProbe.ParseTightenMatrix(src.Replace("RuleNonIncreasing, RuleNonIncreasing }", "RuleNonIncreasing, RuleWhatever }")));
            Assert.Null(GitRepoProbe.ParseTightenMatrix(src + "\nstatic readonly int[] Fallbacks = { 1 };"));
            Assert.Null(GitRepoProbe.ParseTightenMatrix("class X { }"));
            foreach (var bad in new[] { "", "a|1|0|0", "Strategy.X|3|0|0|1", "Strategy.X|1|x|0|1", "Str ategy|1|0|0|1", "Strategy.X|1|0|0|1|9" })
                Assert.False(ShippedTightenMatrixVerifier.TryParseRow(bad, out _), bad);

            // 태그 소스 판독: v12.18.0-GM(기입기 이전) = 빈 목록(구 태그 · 대상 아님) · 잘못된 커밋 = null
            string root = RepoRoot();
            string? oldTag = GitRepoProbe.Git(root, "rev-list", "-n", "1", "v12.18.0-GM")?.Trim();
            if (!string.IsNullOrEmpty(oldTag))
            {
                var old = GitRepoProbe.TagTightenRules(root, oldTag);
                Assert.NotNull(old);
                Assert.Empty(old!);
            }
            Assert.Null(GitRepoProbe.TagTightenRules(root, "not-a-commit"));
            Assert.Null(GitRepoProbe.TagTightenRules(root, null));
        }

        [Fact]
        public void TightenMatrix_BuiltHost_Ps51Zip_SixKeys_Ok_CompareLoopZeroAlloc()
        {
            string dir = TempDir();
            try
            {
                byte[] dll = File.ReadAllBytes(HostDll());
                string seal = new string('E', 64);
                string zip = Ps51HostZip(dir, "ok.zip", dll), json = TightenManifest(dll, seal, SourceRules());
                var r = ShippedTightenMatrixVerifier.Verify(zip, json, seal + "\r\n", TightenKeys);
                _out.WriteLine($"[강화 전용 행렬] {r.Verdict} · {r.Detail} · 팽창+해시 {r.InflateMicros}µs · 대조 {r.CompareMicros}µs");
                Assert.True(r.Verdict == TightenSealVerdict.Ok, r.Verdict + " " + r.Detail);
                Assert.Equal(TightenKeys, r.Count);
                Assert.Equal(ReleaseHandshakeCli.FileSha(HostDll()), r.HostSha256);

                // 대조 루프 δ=0 — #US 직접 비교 · RVA 블롭 BlobReader · 명령 버퍼 stackalloc
                var rows = SourceRules().Select(t => { Assert.True(ShippedTightenMatrixVerifier.TryParseRow(t, out var x)); return x; }).ToArray();
                using var pe = new PEReader(System.Collections.Immutable.ImmutableArray.Create(dll));
                var md = pe.GetMetadataReader();
                Assert.Equal(TightenSealVerdict.Ok, ShippedTightenMatrixVerifier.CompareMatrix(pe, md, rows, out _));
                long alloc = MinAllocated(() => { for (int i = 0; i < 1_000; i++) ShippedTightenMatrixVerifier.CompareMatrix(pe, md, rows, out _); });
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 1_000; i++) ShippedTightenMatrixVerifier.CompareMatrix(pe, md, rows, out _);
                sw.Stop();
                _out.WriteLine($"[강화 전용 행렬] 대조 1,000회 할당 {alloc}B · 회당 {sw.Elapsed.TotalMicroseconds / 1_000:F1}µs");
                Assert.Equal(0, alloc);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void TightenMatrix_ManifestMissingOrDrifted_SpecAnchorTamper_AllRejected()
        {
            string dir = TempDir();
            try
            {
                byte[] dll = File.ReadAllBytes(HostDll());
                string seal = new string('E', 64);
                string zip = Ps51HostZip(dir, "ok.zip", dll);
                var rules = SourceRules();
                TightenSealReport V(IReadOnlyList<string>? rs, int expect = TightenKeys, string? anchor = null, string? json = null)
                    => ShippedTightenMatrixVerifier.Verify(zip, json ?? TightenManifest(dll, seal, rs), anchor ?? seal, expect);
                string[] With(int i, string row) { var c = rules.ToArray(); c[i] = row; return c; }

                // 기입기 행렬 미기록(구 매니페스트 형식 · 누락 출하) · 키 1개 제거 · 스크립트 고정값 이탈
                Assert.Equal(TightenSealVerdict.CountMismatch, V(null).Verdict);
                Assert.Equal(TightenSealVerdict.CountMismatch, V(rules.Take(5).ToArray()).Verdict);
                Assert.Equal(TightenSealVerdict.CountMismatch, V(rules, 5).Verdict);
                Assert.Equal(TightenSealVerdict.CountMismatch, V(rules, 7).Verdict);
                // 매니페스트 행이 고정 명세 밖: AdaptiveMaxBp 방향 2 → 4(내리기만 허용) · 명세 밖 키
                Assert.Equal(TightenSealVerdict.SpecMismatch, V(With(3, "Strategy.AdaptiveMaxBp|4|9500|5000|10000")).Verdict);
                Assert.Equal(TightenSealVerdict.SpecMismatch, V(With(0, "Dispatch.ValveWired|1|0|0|1")).Verdict);
                // 매니페스트 상수 ≠ 바이너리(폴백 · 하한 · 상한) · 키 순서 교환(같은 규칙 2 ↔ 2 → 명세는 통과 · 바이너리 대조에서 거부)
                var fb = V(With(3, "Strategy.AdaptiveMaxBp|2|5000|5000|10000"));
                Assert.Equal(TightenSealVerdict.FallbackMismatch, fb.Verdict);
                Assert.Contains("#3 'Strategy.AdaptiveMaxBp'", fb.Detail);
                Assert.Equal(TightenSealVerdict.MinimumMismatch, V(With(2, "Strategy.ConfidenceThresholdBp|2|8500|4000|10000")).Verdict);
                Assert.Equal(TightenSealVerdict.MaximumMismatch, V(With(5, "Strategy.ChurnHardPerMin|4|60|2|99999")).Verdict);
                var swapped = rules.ToArray(); (swapped[2], swapped[3]) = (swapped[3], swapped[2]);
                Assert.Equal(TightenSealVerdict.KeyMismatch, V(swapped).Verdict);
                // 형식 · 중복 · 앵커 · 매니페스트 변조(재봉인 없이 상수 1자)
                Assert.Equal(TightenSealVerdict.ManifestInvalid, V(With(1, "Strategy.AdaptiveThreshold|1|1|0")).Verdict);
                Assert.Equal(TightenSealVerdict.ManifestInvalid, V(With(1, rules[0])).Verdict);
                Assert.Equal(TightenSealVerdict.AnchorMismatch, V(rules, anchor: new string('B', 64)).Verdict);
                string good = TightenManifest(dll, seal, rules);
                Assert.Equal(TightenSealVerdict.ManifestInvalid, V(null, json: good.Replace("|9500|", "|5000|")).Verdict);
                Assert.Equal(TightenSealVerdict.ManifestInvalid, V(null, json: good.Replace("\"liveArmedDefault\": false", "\"liveArmedDefault\": true")).Verdict);
                // 봉인 해시 ≠ zip 안 DLL · 정규화 중복 항목(공용 로더)
                byte[] other = (byte[])dll.Clone(); other[^1] ^= 0x01;
                Assert.Equal(TightenSealVerdict.ArtifactHashMismatch, V(null, json: TightenManifest(other, seal, rules)).Verdict);
                string dup = Path.Combine(dir, "dup.zip");
                using (var za = ZipFile.Open(dup, ZipArchiveMode.Create)) { AddEntry(za, "host\\MS2E.StockSuite.dll", dll); AddEntry(za, "host/MS2E.StockSuite.dll", dll); }
                Assert.Equal(TightenSealVerdict.EntryDuplicate, ShippedTightenMatrixVerifier.Verify(dup, good, seal, TightenKeys).Verdict);

                Assert.Equal(TightenSealVerdict.Ok, V(rules).Verdict);                           // 원본 무손상
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void TightenMatrix_BinaryPatched_ConsistentlyResealed_Rejected()
        {
            // 공격 모델: 호스트 DLL 행렬 1곳을 바꾸고 매니페스트 해시를 '그 바이너리'로 재봉인 — 행렬 기록은 태그 소스(정직)에서 온다.
            byte[] dll = File.ReadAllBytes(HostDll());
            const string T = ShippedBinaryLiveArmedGuard.TightenWriterType;
            var cases = new List<(string Name, byte[] Bytes, TightenSealVerdict Expect)>();
            {
                int f = TightenOnlySettingGuardTests.FallbacksDataFile(dll) + 3 * 4;
                Assert.Equal(9_500, BitConverter.ToInt32(dll, f));
                var b = (byte[])dll.Clone(); BitConverter.GetBytes(5_000).CopyTo(b, f);
                cases.Add(("폴백 9500→5000", b, TightenSealVerdict.FallbackMismatch));
            }
            {
                int f = TightenOnlySettingGuardTests.FieldDataFile(dll, "get_Rules");
                Assert.Equal(2, dll[f + 3]);
                var b = (byte[])dll.Clone(); b[f + 3] = 4;
                cases.Add(("규칙 2→4", b, TightenSealVerdict.RuleMismatch));
            }
            {
                var (off, _, ins) = TightenOnlySettingGuardTests.Body(dll, T, "IsTightening");
                int k = ins.FindIndex(i => i.Op == OpCodes.Clt);
                var b = (byte[])dll.Clone(); b[off + ins[k].At + 1] = 0x02;                     // clt → cgt(≥ → ≤)
                cases.Add(("clt→cgt", b, TightenSealVerdict.DirectionMismatch));
            }
            {
                var (off, _, _) = TightenOnlySettingGuardTests.Body(dll, T, "IsTightening");
                var b = (byte[])dll.Clone(); b[off] = 0x17; b[off + 1] = 0x2A;                   // ldc.i4.1; ret
                cases.Add(("상수 true", b, TightenSealVerdict.DirectionMismatch));
            }
            {
                // #US 힙 키 개명(AdaptiveMaxBp → AdaptiveMaxBq) — 기입기 키와 호스트 판독이 함께 바뀌는 '일관된' 변조
                byte[] key = Encoding.Unicode.GetBytes("Strategy.AdaptiveMaxBp");
                int at = dll.AsSpan().IndexOf(key);
                Assert.True(at > 0 && dll.AsSpan(at + 1).IndexOf(key) < 0, "키 #US 항목 정확히 1개");
                var b = (byte[])dll.Clone(); b[at + key.Length - 2] = (byte)'q';
                cases.Add(("키 개명", b, TightenSealVerdict.KeyMismatch));
            }
            string dir = TempDir();
            try
            {
                string seal = new string('D', 64);
                var rules = SourceRules();
                int n = 0;
                foreach (var (name, bytes, expect) in cases)
                {
                    string zip = Ps51HostZip(dir, "evil" + n++ + ".zip", bytes);
                    var r = ShippedTightenMatrixVerifier.Verify(zip, TightenManifest(bytes, seal, rules), seal, TightenKeys);
                    _out.WriteLine($"[패치] {name} → {r.Verdict} · {r.Detail}");
                    Assert.Equal(expect, r.Verdict);
                }
                string good = Ps51HostZip(dir, "good.zip", dll);                                // 대조군: 같은 재봉인 절차 + 원본 = Ok
                Assert.Equal(TightenSealVerdict.Ok, ShippedTightenMatrixVerifier.Verify(good, TightenManifest(dll, seal, rules), seal, TightenKeys).Verdict);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void TightenMatrix_Handshake_StreamerParity_StrippedRulesRejected()
        {
            const string src = "9f3b7ef08456c9b30a1d29aaa2233a9f28116d43";
            string seal = new('7', 64), genesis = new('6', 64), sigSha = new('5', 64), zipSha = new('8', 64), shim = new('C', 64);
            var arts = new List<(string, string)> { ("host/MS2E.StockSuite.dll", new string('1', 64)) };
            var locks = new[] { "BootIntegrity", "StartupSanity" };
            var rules = SourceRules();
            ReleaseManifestInput In(IReadOnlyList<string>? rs) => new("12.18.1-GM", "v12.18.1-GM", src, sigSha, seal, genesis, arts, "release-v12.18.1-GM.zip", zipSha, shim, false, locks, null, rs);
            HandshakeObservation Obs(IReadOnlyList<string>? tag) => new(new RepoObservation(src, src, Array.Empty<string>(), Array.Empty<string>()), sigSha, "12.18.1-GM", seal, arts,
                zipSha, seal, shim, null, genesis, locks, null, tag);
            ReleaseHandshakeVerdict V(ReleaseManifestInput m, HandshakeObservation o) => MultiRepoReleaseManifest.Verify(MultiRepoReleaseManifest.Build(m), o, out _);

            Assert.Equal(ReleaseHandshakeVerdict.Ok, V(In(rules), Obs(rules)));
            Assert.Equal(ReleaseHandshakeVerdict.Ok, V(In(null), Obs(Array.Empty<string>())));       // 구 태그 · 구 매니페스트(대상 아님)
            Assert.Equal(ReleaseHandshakeVerdict.TightenRuleMismatch, V(In(null), Obs(rules)));        // ★기록에서 행렬을 지운 '조용한 누락'
            Assert.Equal(ReleaseHandshakeVerdict.TightenRuleMismatch, V(In(rules), Obs(null)));        // 태그 소스 관측 불가 = fail-closed
            Assert.Equal(ReleaseHandshakeVerdict.TightenRuleMismatch, V(In(rules), Obs(Array.Empty<string>())));
            var drift = rules.ToArray(); drift[3] = "Strategy.AdaptiveMaxBp|2|5000|5000|10000";
            Assert.Equal(ReleaseHandshakeVerdict.TightenRuleMismatch, V(In(drift), Obs(rules)));

            // 기준 판독(JsonDocument) ↔ 독립 무할당 스트리머: 같은 자기 봉인 · tighten 줄 포함 · 빈 배열 = 둘 다 거부 · δ=0
            string json = MultiRepoReleaseManifest.Build(In(rules));
            Assert.Contains("\"tightenRules\"", json);
            Assert.DoesNotContain("\"tightenRules\"", MultiRepoReleaseManifest.Build(In(null)));
            var st = new ManifestSealStreamer();
            Assert.True(st.Load(Encoding.UTF8.GetBytes(json)));
            byte[] an = Convert.FromHexString(seal);
            Assert.Equal(ManifestStreamVerdict.Ok, st.Verify(an));
            Assert.Equal(TightenKeys, st.TightenRuleCount);
            Assert.Equal(0, MinAllocated(() => st.Verify(an)));
            Assert.True(MultiRepoReleaseManifest.TryParse(json, out var back, out _, out _));
            Assert.Equal(rules, back.TightenRules!);
            string empty = json.Replace(json.Substring(json.IndexOf("\"tightenRules\"", StringComparison.Ordinal),
                json.IndexOf(']', json.IndexOf("\"tightenRules\"", StringComparison.Ordinal)) + 1 - json.IndexOf("\"tightenRules\"", StringComparison.Ordinal)), "\"tightenRules\": []");
            Assert.False(MultiRepoReleaseManifest.TryParse(empty, out _, out _, out _));
            Assert.True(st.Load(Encoding.UTF8.GetBytes(empty)));
            Assert.Equal(ManifestStreamVerdict.NonCanonical, st.Verify(an));
            // 행렬 1자 변조(재봉인 없음) = 두 판독기 모두 자기 봉인 거부
            string tampered = json.Replace("|9500|", "|9400|");
            Assert.Equal(ReleaseHandshakeVerdict.SelfHashMismatch, MultiRepoReleaseManifest.Verify(tampered, Obs(rules), out _));
            Assert.True(st.Load(Encoding.UTF8.GetBytes(tampered)));
            Assert.Equal(ManifestStreamVerdict.SelfHashMismatch, st.Verify(an));
        }

        [Fact]
        public void TightenMatrix_PackageScript_Wired_KeysPinned_CliExitCodes()
        {
            string s = File.ReadAllText(Path.Combine(RepoRoot(), "release-package.ps1"));
            var spec = Regex.Match(s, @"^\$ExpectedTightenKeys = (\d+)", RegexOptions.Multiline);
            Assert.True(spec.Success, "스크립트 강화 전용 키 고정값 없음");
            Assert.Equal(TightenKeys, int.Parse(spec.Groups[1].Value));
            Assert.Equal(ShippedBinaryLiveArmedGuard.TightenSpecCount, int.Parse(spec.Groups[1].Value));
            Assert.Contains("'--verify-live-armed', ('\"' + $zipOut + '\"'), '--expect-tighten-keys', \"$ExpectedTightenKeys\"", s);
            int ilk = s.IndexOf("$ilkExit = Invoke-HostCli", StringComparison.Ordinal), tight = s.IndexOf("'--verify-tighten'", StringComparison.Ordinal),
                done = s.IndexOf("== 완료:", StringComparison.Ordinal);
            Assert.True(ilk > 0 && tight > ilk && done > tight, "★강화 전용 행렬 대조 = 인터락 봉인 대조 뒤 · 완료 앞");
            Assert.Contains("if ($tightExit -ne 0) { Remove-ReleaseOutputs; Fail", s);
            Assert.True(ReleaseHandshakeCli.IsInvocation(new[] { "--verify-tighten" }));

            string dir = TempDir();
            try
            {
                byte[] dll = File.ReadAllBytes(HostDll());
                string seal = new string('E', 64);
                string zip = Ps51HostZip(dir, "ok.zip", dll);
                string mf = Path.Combine(dir, "RELEASE_MANIFEST.json"), sf = Path.Combine(dir, "master-seal.txt");
                File.WriteAllText(mf, TightenManifest(dll, seal, SourceRules()));
                File.WriteAllText(sf, seal + Environment.NewLine);
                int Run(params string[] a) { var w = new StringWriter(); int c = ReleaseHandshakeCli.Run(a, w); _out.WriteLine(w.ToString().TrimEnd()); return c; }
                Assert.Equal(ReleaseHandshakeCli.ExitOk, Run("--verify-tighten", zip, "--manifest", mf, "--seal-file", sf, "--expect-tighten-keys", "6"));
                Assert.Equal(ReleaseHandshakeCli.ExitRejected, Run("--verify-tighten", zip, "--manifest", mf, "--seal-file", sf, "--expect-tighten-keys", "5"));
                Assert.Equal(ReleaseHandshakeCli.ExitUsage, Run("--verify-tighten", zip, "--manifest", mf, "--seal-file", sf));
                // [9/10] --verify-live-armed 키 수 고정: 6 = Ok · 7 = 거부 · 형식 오류 = 사용 오류 · 미지정 = 기존 동작
                Assert.Equal(ReleaseHandshakeCli.ExitOk, Run("--verify-live-armed", zip, "--expect-tighten-keys", "6"));
                Assert.Equal(ReleaseHandshakeCli.ExitRejected, Run("--verify-live-armed", zip, "--expect-tighten-keys", "7"));
                Assert.Equal(ReleaseHandshakeCli.ExitUsage, Run("--verify-live-armed", zip, "--expect-tighten-keys", "x"));
                Assert.Equal(ReleaseHandshakeCli.ExitOk, Run("--verify-live-armed", zip));
                // 기입기 형식을 개명한 바이너리(키 표 판독 0): 가드 위반 + 키 수 고정 불일치 · 행렬 대조 = 기입기 없음
                byte[] gone = (byte[])dll.Clone();
                byte[] name = Encoding.UTF8.GetBytes(ShippedBinaryLiveArmedGuard.TightenWriterType + "\0");
                int at = gone.AsSpan().IndexOf(name);
                Assert.True(at > 0);
                gone[at] = (byte)'X';
                string goneZip = Ps51HostZip(dir, "gone.zip", gone);
                var w2 = new StringWriter();
                int c2 = ReleaseHandshakeCli.Run(new[] { "--verify-live-armed", goneZip, "--expect-tighten-keys", "6" }, w2);
                _out.WriteLine(w2.ToString().TrimEnd());
                Assert.Equal(ReleaseHandshakeCli.ExitRejected, c2);
                Assert.Contains("강화 전용 키 0 ≠ 출하 고정 6", w2.ToString());
                File.WriteAllText(mf, TightenManifest(gone, seal, SourceRules()));
                Assert.Equal(ReleaseHandshakeCli.ExitRejected, Run("--verify-tighten", goneZip, "--manifest", mf, "--seal-file", sf, "--expect-tighten-keys", "6"));
            }
            finally { Directory.Delete(dir, true); }
        }

        // ════════════ §12 [v12.18.2-GM · 방향 A] 감사 원장 결선 무추출 · 무할당 수검 ════════════

        /// <summary>같은 PE 이미지 바이트 1개 이상 패치 사본(매니페스트는 그 사본으로 일관 재봉인).</summary>
        private static byte[] Patch(byte[] dll, Action<byte[]> p) { var b = (byte[])dll.Clone(); p(b); return b; }

        [Fact]
        public void AuditLedger_BuiltHost_Ps51Zip_Required_Ok_InspectorZeroAlloc()
        {
            string dir = TempDir();
            try
            {
                byte[] dll = File.ReadAllBytes(HostDll());
                string seal = new string('E', 64);
                string zip = Ps51HostZip(dir, "ok.zip", dll), json = TightenManifest(dll, seal, SourceRules());
                var r = ShippedTightenMatrixVerifier.Verify(zip, json, seal, TightenKeys, requireAuditLedger: true);
                _out.WriteLine($"[감사 원장 결선] {r.Verdict} · {r.Detail}");
                Assert.True(r.Verdict == TightenSealVerdict.Ok, r.Verdict + " " + r.Detail);
                Assert.Contains("감사 원장 선기록 결선(⑪)", r.Detail);
                var arm = ShippedBinaryLiveArmedGuard.ScanZip(zip);
                Assert.True(arm.Ok && arm.AuditLedgerAware, string.Join(" | ", arm.Violations));

                using var pe = new PEReader(System.Collections.Immutable.ImmutableArray.Create(dll));
                var md = pe.GetMetadataReader();
                Assert.Equal(TightenSealVerdict.Ok, ShippedTightenMatrixVerifier.CheckAuditWiring(pe, md));
                long alloc = MinAllocated(() => { for (int i = 0; i < 1_000; i++) ShippedTightenMatrixVerifier.CheckAuditWiring(pe, md); });
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 1_000; i++) ShippedTightenMatrixVerifier.CheckAuditWiring(pe, md);
                sw.Stop();
                _out.WriteLine($"[감사 원장 결선] 1,000회 할당 {alloc}B · 회당 {sw.Elapsed.TotalMicroseconds / 1_000:F1}µs");
                Assert.Equal(0, alloc);
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void AuditLedger_BinaryPatched_ConsistentlyResealed_Rejected_GuardAloneMissesSinkRemoval()
        {
            byte[] dll = File.ReadAllBytes(HostDll());
            const string T = ShippedBinaryLiveArmedGuard.TightenWriterType;
            var (off, _, ins) = TightenOnlySettingGuardTests.Body(dll, T, "TryWrite");
            int write = ins.FindIndex(i => i.Op == OpCodes.Callvirt && MonotonicSettingAuditLogTests.MethodName(dll, i.Operand) == "WriteStored");
            int app = ins.FindLastIndex(write, i => i.Op == OpCodes.Callvirt && MonotonicSettingAuditLogTests.MethodName(dll, i.Operand) == "Append");
            Assert.True(app > 0 && ins[app + 1].Op.OperandType == OperandType.ShortInlineBrTarget, "Append 직후 짧은 조건 분기 예상");
            var (eoff, _, eins) = TightenOnlySettingGuardTests.Body(dll, "AdminSettingWidgetAdapter", "TightenSetting");
            int tw = eins.FindIndex(i => i.Op == OpCodes.Call && MonotonicSettingAuditLogTests.MethodName(dll, i.Operand) == "TryWrite");
            Assert.Equal(OpCodes.Ldfld, eins[tw - 1].Op);
            int ld0 = eins.FindLastIndex(tw - 1, i => i.Op == OpCodes.Ldarg_0);
            byte[] sinkName = Encoding.UTF8.GetBytes("\0" + ShippedBinaryLiveArmedGuard.TightenAuditSinkType + "\0");
            int sinkAt = dll.AsSpan().IndexOf(sinkName);
            Assert.True(sinkAt > 0 && dll.AsSpan(sinkAt + 1).IndexOf(sinkName) < 0, "#Strings ITightenAuditSink 정확히 1개");

            var cases = new (string Name, byte[] Bytes, TightenSealVerdict Expect, bool GuardCatches)[]
            {
                ("Append 분기 → br.s", Patch(dll, b => b[off + ins[app + 1].At] = 0x2B), TightenSealVerdict.AuditLedgerBypass, true),
                ("Append → get_Frozen", Patch(dll, b => BitConverter.GetBytes(MonotonicSettingAuditLogTests.TokenOf(dll, "ITightenAuditSink", "get_Frozen")).CopyTo(b, off + ins[app].At + 1)),
                    TightenSealVerdict.AuditLedgerMissing, true),
                ("진입점 감사 인자 ldnull", Patch(dll, b => { int at = eoff + eins[tw - 1].At; b[at] = 0; b[at + 1] = 0; b[at + 2] = 0; b[at + 3] = 0; b[at + 4] = 0x14; b[eoff + eins[ld0].At] = 0; }),
                    TightenSealVerdict.AuditLedgerBypass, true),
                // ★싱크 형식 개명(ITightenAuditSink → XTightenAuditSink): 가드 ⑪은 '형식 없음 = 구 바이너리'로 보고 생략 — 출하 게이트(요구 플래그)만 잡는다
                ("싱크 형식 개명", Patch(dll, b => b[sinkAt + 1] = (byte)'X'), TightenSealVerdict.AuditLedgerMissing, false),
            };
            string dir = TempDir();
            try
            {
                string seal = new string('D', 64);
                var rules = SourceRules();
                int n = 0;
                foreach (var (name, bytes, expect, guardCatches) in cases)
                {
                    string zip = Ps51HostZip(dir, "evil" + n++ + ".zip", bytes);
                    string json = TightenManifest(bytes, seal, rules);
                    var r = ShippedTightenMatrixVerifier.Verify(zip, json, seal, TightenKeys, requireAuditLedger: true);
                    var g = ShippedBinaryLiveArmedGuard.ScanZip(zip);
                    _out.WriteLine($"[감사 패치] {name} → 행렬 {r.Verdict} · 가드 {(g.Ok ? "Ok" : "Rejected")}(⑪ {(g.AuditLedgerAware ? "적용" : "생략")})");
                    Assert.Equal(expect, r.Verdict);
                    Assert.Equal(TightenSealVerdict.Ok, ShippedTightenMatrixVerifier.Verify(zip, json, seal, TightenKeys).Verdict);   // 요구 없음 = 행렬만(기존 동작)
                    Assert.Equal(!guardCatches, g.Ok);
                    var w = new StringWriter();
                    Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { "--verify-live-armed", zip, "--expect-tighten-keys", "6", "--require-audit-ledger" }, w));
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void AuditLedger_PackageScript_RequiresLedger_BothCalls_CliExitCodes()
        {
            string s = File.ReadAllText(Path.Combine(RepoRoot(), "release-package.ps1"));
            Assert.Contains("'--expect-tighten-keys', \"$ExpectedTightenKeys\", '--require-audit-ledger', '--require-pump-jitter', '--require-cancel-arming') \"verify-live-armed\"", s);
            Assert.Contains("'--expect-tighten-keys', \"$ExpectedTightenKeys\", '--require-audit-ledger', '--require-audit-telemetry', '--require-ipc-fabric') \"verify-tighten\"", s);
            Assert.Equal(9, Regex.Matches(s, "Remove-ReleaseOutputs; Fail").Count);              // v12.18.28-DEV: + [11/11] 배포본 실패 · 기존 거부 줄에 요구 인자만 추가 · v12.18.4-GM: + 피어 드리프트 거부(Assert-PeerPinned)
            string dir = TempDir();
            try
            {
                byte[] dll = File.ReadAllBytes(HostDll());
                string seal = new string('E', 64);
                string zip = Ps51HostZip(dir, "ok.zip", dll);
                string mf = Path.Combine(dir, "RELEASE_MANIFEST.json"), sf = Path.Combine(dir, "master-seal.txt");
                File.WriteAllText(mf, TightenManifest(dll, seal, SourceRules()));
                File.WriteAllText(sf, seal + Environment.NewLine);
                int Run(params string[] a) { var w = new StringWriter(); int c = ReleaseHandshakeCli.Run(a, w); _out.WriteLine(w.ToString().TrimEnd()); return c; }
                Assert.Equal(ReleaseHandshakeCli.ExitOk, Run("--verify-tighten", zip, "--manifest", mf, "--seal-file", sf, "--expect-tighten-keys", "6", "--require-audit-ledger"));
                Assert.Equal(ReleaseHandshakeCli.ExitOk, Run("--verify-live-armed", zip, "--expect-tighten-keys", "6", "--require-audit-ledger"));
                // v12.18.1-GM 출하물(감사 원장 이전) = 요구 플래그에서 거부 · 없으면 기존대로 통과(구 출하물 재수검 호환)
                string old = Path.Combine(RepoRoot(), "release", "superseded-v12.18.1-GM", "release-v12.18.1-GM.zip");
                if (!File.Exists(old)) old = Path.Combine(RepoRoot(), "release", "release-v12.18.1-GM.zip");
                if (File.Exists(old))
                {
                    Assert.Equal(ReleaseHandshakeCli.ExitOk, Run("--verify-live-armed", old, "--expect-tighten-keys", "6"));
                    Assert.Equal(ReleaseHandshakeCli.ExitRejected, Run("--verify-live-armed", old, "--expect-tighten-keys", "6", "--require-audit-ledger"));
                }
                else _out.WriteLine("[감사 원장] v12.18.1-GM 출하물 없음 — 구 출하물 대조 생략");
            }
            finally { Directory.Delete(dir, true); }
        }

        // ════════════ §13 [v12.18.3-GM · 방향 A] 관제 배지 무잠금 seqlock 결선 무추출 · 무할당 수검 ════════════

        private const string Ledger = ShippedTightenMatrixVerifier.LedgerName;

        /// <summary>
        /// 형식 outerType(중첩 클로저 형식 포함) 메서드 본문 안의 호출 명령(call/callvirt + 토큰) 파일 오프셋 — 정확히 1개여야 한다(패치 대상 모호성 차단).
        /// 검사기(CheckAuditTelemetry (d))와 같은 범위 — [v12.18.4-DEV] 같은 DLL 의 시뮬레이션 하네스(LongSoakProfileHarness)도 AttachAuditLedger 를 부른다.
        /// </summary>
        private static int UniqueCallSite(byte[] dll, int token, string outerType)
        {
            using var pe = new PEReader(new MemoryStream(dll));
            var md = pe.GetMetadataReader();
            int found = -1, n = 0;
            foreach (var mh in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(mh);
                var t = m.GetDeclaringType();
                for (var d = md.GetTypeDefinition(t).GetDeclaringType(); !d.IsNil; d = md.GetTypeDefinition(t).GetDeclaringType()) t = d;
                if (m.RelativeVirtualAddress == 0 || md.GetString(md.GetTypeDefinition(t).Name) != outerType) continue;
                string name = md.GetString(m.Name);
                var (off, il, ins) = TightenOnlySettingGuardTests.Body(dll, md.GetString(md.GetTypeDefinition(m.GetDeclaringType()).Name), name);
                if (off != BodyFileOffset(pe, dll, m.RelativeVirtualAddress)) continue;            // 같은 이름 다른 오버로드 = 건너뜀(Body 는 첫 일치만)
                foreach (var i in ins)
                    if ((i.Op == OpCodes.Call || i.Op == OpCodes.Callvirt) && i.Operand == token) { found = off + i.At; n++; }
            }
            Assert.True(n == 1, $"{outerType} 안 호출 지점 {n}개(토큰 0x{token:X8})");
            return found;
        }

        private static int BodyFileOffset(PEReader pe, byte[] dll, int rva)
        {
            foreach (var s in pe.PEHeaders.SectionHeaders)
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData))
                {
                    int file = rva - s.VirtualAddress + s.PointerToRawData;
                    return file + ((dll[file] & 0x3) == 0x2 ? 1 : ((dll[file + 1] >> 4) & 0xF) * 4);
                }
            return -1;
        }

        private static int MemberRefToken(byte[] dll, string type, string name)
        {
            using var pe = new PEReader(new MemoryStream(dll));
            var md = pe.GetMetadataReader();
            foreach (var h in md.MemberReferences)
            {
                var r = md.GetMemberReference(h);
                if (md.GetString(r.Name) == name && r.Parent.Kind == HandleKind.TypeReference
                    && md.GetString(md.GetTypeReference((TypeReferenceHandle)r.Parent).Name) == type) return MetadataTokens.GetToken(h);
            }
            throw new InvalidOperationException(type + "::" + name + " MemberRef 없음");
        }

        private static int FieldToken(byte[] dll, string type, string field)
        {
            using var pe = new PEReader(new MemoryStream(dll));
            var md = pe.GetMetadataReader();
            foreach (var h in md.FieldDefinitions)
            {
                var f = md.GetFieldDefinition(h);
                if (md.GetString(f.Name) == field && md.GetString(md.GetTypeDefinition(f.GetDeclaringType()).Name) == type) return MetadataTokens.GetToken(h);
            }
            throw new InvalidOperationException(type + "." + field + " 없음");
        }

        private static bool IsField(byte[] dll, int token, string name)
        {
            using var pe = new PEReader(new MemoryStream(dll));
            var md = pe.GetMetadataReader();
            var h = MetadataTokens.EntityHandle(token);
            return h.Kind == HandleKind.FieldDefinition && md.GetString(md.GetFieldDefinition((FieldDefinitionHandle)h).Name) == name;
        }

        [Fact]
        public void AuditTelemetry_BuiltHost_Ps51Zip_SnapshotLockFree_Ok_InspectorZeroAlloc_Cli()
        {
            string dir = TempDir();
            try
            {
                byte[] dll = File.ReadAllBytes(HostDll());
                string seal = new string('E', 64);
                string zip = Ps51HostZip(dir, "ok.zip", dll), json = TightenManifest(dll, seal, SourceRules());
                var r = ShippedTightenMatrixVerifier.Verify(zip, json, seal, TightenKeys, requireAuditLedger: true, requireAuditTelemetry: true);
                _out.WriteLine($"[관제 배지 결선] {r.Verdict} · {r.Detail} · 대조 {r.CompareMicros}µs");
                Assert.True(r.Verdict == TightenSealVerdict.Ok, r.Verdict + " " + r.Detail);
                Assert.Contains("관제 배지 무잠금 seqlock 결선(⑤)", r.Detail);

                using var pe = new PEReader(System.Collections.Immutable.ImmutableArray.Create(dll));
                var md = pe.GetMetadataReader();
                Assert.Equal(TightenSealVerdict.Ok, ShippedTightenMatrixVerifier.CheckAuditTelemetry(pe, md));
                long alloc = MinAllocated(() => { for (int i = 0; i < 200; i++) ShippedTightenMatrixVerifier.CheckAuditTelemetry(pe, md); });
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++) ShippedTightenMatrixVerifier.CheckAuditTelemetry(pe, md);
                sw.Stop();
                _out.WriteLine($"[관제 배지 결선] 200회 할당 {alloc}B · 회당 {sw.Elapsed.TotalMicroseconds / 200:F1}µs");
                Assert.Equal(0, alloc);

                // 판독자 본문 실측 — 시퀀스 판독 ≥ 2 · _gate 미접근(소스 주석이 아니라 IL 사실)
                var (_, _, ins) = TightenOnlySettingGuardTests.Body(dll, Ledger, "Snapshot");
                int seq = ins.Count(i => i.Op == OpCodes.Ldflda && IsField(dll, i.Operand, "_snapSeq"));
                Assert.True(seq >= 2, "Snapshot _snapSeq 판독 " + seq);
                Assert.DoesNotContain(ins, i => (i.Op == OpCodes.Ldfld || i.Op == OpCodes.Ldflda) && IsField(dll, i.Operand, "_gate"));

                string mf = Path.Combine(dir, "RELEASE_MANIFEST.json"), sf = Path.Combine(dir, "master-seal.txt");
                File.WriteAllText(mf, json);
                File.WriteAllText(sf, seal + Environment.NewLine);
                var w = new StringWriter();
                Assert.Equal(ReleaseHandshakeCli.ExitOk, ReleaseHandshakeCli.Run(new[] { "--verify-tighten", zip, "--manifest", mf, "--seal-file", sf, "--expect-tighten-keys", "6",
                                                                                       "--require-audit-ledger", "--require-audit-telemetry" }, w));
                _out.WriteLine(w.ToString().TrimEnd());
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void AuditTelemetry_BinaryPatched_ConsistentlyResealed_Rejected_LedgerWiringUnaffected()
        {
            byte[] dll = File.ReadAllBytes(HostDll());
            var (soff, _, sins) = TightenOnlySettingGuardTests.Body(dll, Ledger, "Snapshot");
            int seqLd = sins.FindIndex(i => i.Op == OpCodes.Ldflda && IsField(dll, i.Operand, "_snapSeq"));
            Assert.True(seqLd >= 0 && sins[seqLd + 1].Op == OpCodes.Call, "Snapshot: ldflda _snapSeq; call Volatile.Read 예상");
            int ctor = sins.FindIndex(i => i.Op == OpCodes.Newobj);
            Assert.True(ctor >= 0, "Snapshot: 값 형식 newobj 예상");
            var (poff, _, pins) = TightenOnlySettingGuardTests.Body(dll, Ledger, "PublishSnapshot");
            int bump = -1;
            for (int k = 1; k < pins.Count; k++)
                if (pins[k].Op == OpCodes.Call && MonotonicSettingAuditLogTests.MethodName(dll, pins[k].Operand) == "Increment"
                    && pins[k - 1].Op == OpCodes.Ldflda && IsField(dll, pins[k - 1].Operand, "_snapSeq")) bump = k;
            Assert.True(bump > 0, "PublishSnapshot: 닫기 Interlocked.Increment(ref _snapSeq) 예상");
            int snapTok = MonotonicSettingAuditLogTests.TokenOf(dll, Ledger, "Snapshot");
            var (hoff, _, hins) = TightenOnlySettingGuardTests.Body(dll, ShippedTightenMatrixVerifier.HubName, ShippedTightenMatrixVerifier.HubPump);
            int pumpCall = hoff + hins.Single(i => (i.Op == OpCodes.Call || i.Op == OpCodes.Callvirt) && i.Operand == snapTok).At;
            var (aoff, _, ains) = TightenOnlySettingGuardTests.Body(dll, ShippedTightenMatrixVerifier.AdapterName, ShippedTightenMatrixVerifier.AdapterBadge);
            int badgeCall = aoff + ains.Single(i => (i.Op == OpCodes.Call || i.Op == OpCodes.Callvirt) && i.Operand == snapTok).At;
            int attach = UniqueCallSite(dll, MonotonicSettingAuditLogTests.TokenOf(dll, ShippedTightenMatrixVerifier.HubName, "AttachAuditLedger"), ShippedTightenMatrixVerifier.HostName);
            static byte[] Tok(int t) => BitConverter.GetBytes(t);
            const TightenSealVerdict U = TightenSealVerdict.AuditTelemetryUnsafe, M = TightenSealVerdict.AuditTelemetryMissing;

            var cases = new (string Name, byte[] Bytes, TightenSealVerdict Expect)[]
            {
                ("판독자 Volatile.Read → Monitor.Enter(잠금)", Patch(dll, b => Tok(MemberRefToken(dll, "Monitor", "Enter")).CopyTo(b, soff + sins[seqLd + 1].At + 1)), U),
                ("판독자 _snapSeq → _gate(체인 잠금 객체)", Patch(dll, b => Tok(FieldToken(dll, Ledger, "_gate")).CopyTo(b, soff + sins[seqLd].At + 1)), U),
                ("판독자 _snapSeq → _head(기록자 머리 버퍼 · 찢긴 읽기)", Patch(dll, b => Tok(FieldToken(dll, Ledger, "_head")).CopyTo(b, soff + sins[seqLd].At + 1)), U),
                ("판독자 값 newobj → 원장 참조 형식 생성(힙 할당)", Patch(dll, b => Tok(MonotonicSettingAuditLogTests.TokenOf(dll, Ledger, ".ctor")).CopyTo(b, soff + sins[ctor].At + 1)), U),
                ("판독자 Volatile.Read → 원장 CopyHead(잠금 경로)", Patch(dll, b => Tok(MonotonicSettingAuditLogTests.TokenOf(dll, Ledger, "CopyHead")).CopyTo(b, soff + sins[seqLd + 1].At + 1)), U),
                ("게시자 닫기 증가 제거(홀수 고착)", Patch(dll, b => { for (int k = 0; k < 5; k++) b[poff + pins[bump].At + k] = 0; }), U),
                ("허브 Snapshot → PollIntegrity(전체 재검증 · _gate)", Patch(dll, b => Tok(MonotonicSettingAuditLogTests.TokenOf(dll, Ledger, "PollIntegrity")).CopyTo(b, pumpCall + 1)), U),
                ("어댑터 Snapshot → CopyHead(_gate)", Patch(dll, b => Tok(MonotonicSettingAuditLogTests.TokenOf(dll, Ledger, "CopyHead")).CopyTo(b, badgeCall + 1)), U),
                ("호스트 AttachAuditLedger 제거(배지 미결선)", Patch(dll, b => { b[attach] = 0x26; b[attach + 1] = 0x26; b[attach + 2] = 0; b[attach + 3] = 0; b[attach + 4] = 0; }), M),
            };
            string dir = TempDir();
            try
            {
                string seal = new string('D', 64);
                var rules = SourceRules();
                int n = 0;
                foreach (var (name, bytes, expect) in cases)
                {
                    string zip = Ps51HostZip(dir, "evil" + n++ + ".zip", bytes);
                    string json = TightenManifest(bytes, seal, rules);
                    var r = ShippedTightenMatrixVerifier.Verify(zip, json, seal, TightenKeys, requireAuditLedger: true, requireAuditTelemetry: true);
                    _out.WriteLine($"[관제 패치] {name} → {r.Verdict}");
                    Assert.Equal(expect, r.Verdict);
                    // 요구 없음 = 기존 동작(행렬 + 원장 선기록 ⑪ 은 이 패치들과 무관하게 통과)
                    Assert.Equal(TightenSealVerdict.Ok, ShippedTightenMatrixVerifier.Verify(zip, json, seal, TightenKeys, requireAuditLedger: true).Verdict);
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void AuditTelemetry_PreviousShipment_v12_18_2_GM_RejectedOnlyWhenRequired()
        {
            string root = RepoRoot();
            string? d = new[] { Path.Combine(root, "release", "superseded-v12.18.2-GM"), Path.Combine(root, "release") }
                .FirstOrDefault(x => File.Exists(Path.Combine(x, "release-v12.18.2-GM.zip")) && File.Exists(Path.Combine(x, "RELEASE_MANIFEST.json"))
                                     && File.ReadAllText(Path.Combine(x, "RELEASE_MANIFEST.json")).Contains("\"v12.18.2-GM\"", StringComparison.Ordinal));
            if (d is null) { _out.WriteLine("[관제 배지] v12.18.2-GM 출하물 없음 — 구 출하물 대조 생략"); return; }
            string zip = Path.Combine(d, "release-v12.18.2-GM.zip"), json = File.ReadAllText(Path.Combine(d, "RELEASE_MANIFEST.json")),
                   seal = File.ReadAllText(Path.Combine(d, "master-seal.txt"));
            var old = ShippedTightenMatrixVerifier.Verify(zip, json, seal, TightenKeys, requireAuditLedger: true);
            var req = ShippedTightenMatrixVerifier.Verify(zip, json, seal, TightenKeys, requireAuditLedger: true, requireAuditTelemetry: true);
            _out.WriteLine($"[관제 배지] v12.18.2-GM: 요구 없음 {old.Verdict} · 요구 {req.Verdict}");
            Assert.Equal(TightenSealVerdict.Ok, old.Verdict);                                   // 구 출하물 재수검 호환
            Assert.Equal(TightenSealVerdict.AuditTelemetryMissing, req.Verdict);               // 배지 이전 바이너리 = 이번 출하 게이트 불통
        }

        // ════════════ §14 [v12.18.4-GM · 방향 A] IPC 패브릭 무슬립 위상 무추출 · 무할당 수검 ════════════

        private const string Fabric = ShippedTightenMatrixVerifier.FabricName;

        /// <summary>본문 안 '호출 명령 + 지정 메서드 이름' 의 명령 파일 오프셋 — 정확히 1개여야 한다(패치 대상 모호성 차단).</summary>
        private static int CallIn(byte[] dll, string method, string callee)
        {
            var (off, _, ins) = TightenOnlySettingGuardTests.Body(dll, Fabric, method);
            var hits = ins.Where(i => (i.Op == OpCodes.Call || i.Op == OpCodes.Callvirt) && MonotonicSettingAuditLogTests.MethodName(dll, i.Operand) == callee).ToList();
            Assert.True(hits.Count == 1, $"{Fabric}.{method} 안 {callee} 호출 {hits.Count}개");
            return off + hits[0].At;
        }

        [Fact]
        public void IpcFabric_BuiltHost_Ps51Zip_HotClosureSleepFree_Ok_InspectorZeroAlloc_Cli()
        {
            string dir = TempDir();
            try
            {
                byte[] dll = File.ReadAllBytes(HostDll());
                string seal = new string('F', 64);
                string zip = Ps51HostZip(dir, "ok.zip", dll), json = TightenManifest(dll, seal, SourceRules());
                var r = ShippedTightenMatrixVerifier.Verify(zip, json, seal, TightenKeys, requireAuditLedger: true, requireAuditTelemetry: true, requireIpcFabric: true);
                _out.WriteLine($"[IPC 위상] {r.Verdict} · {r.Detail} · 대조 {r.CompareMicros}µs");
                Assert.True(r.Verdict == TightenSealVerdict.Ok, r.Verdict + " " + r.Detail);
                Assert.Contains("IPC 패브릭 핫 폐포 무대기 · 무슬립 · 기본 시계 Stopwatch(⑥)", r.Detail);

                using var pe = new PEReader(System.Collections.Immutable.ImmutableArray.Create(dll));
                var md = pe.GetMetadataReader();
                Assert.Equal(TightenSealVerdict.Ok, ShippedTightenMatrixVerifier.CheckIpcFabric(pe, md));
                long alloc = MinAllocated(() => { for (int i = 0; i < 200; i++) ShippedTightenMatrixVerifier.CheckIpcFabric(pe, md); });
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++) ShippedTightenMatrixVerifier.CheckIpcFabric(pe, md);
                sw.Stop();
                _out.WriteLine($"[IPC 위상] 200회 할당 {alloc}B · 회당 {sw.Elapsed.TotalMicroseconds / 200:F1}µs");
                Assert.Equal(0, alloc);

                // 실측 전제(소스 주석이 아니라 IL 사실): Dispose 만 바운드 SpinOnce · 핫 진입점 4종은 그 호출 0
                var (_, _, dins) = TightenOnlySettingGuardTests.Body(dll, Fabric, "Dispose");
                Assert.Contains(dins, i => i.Op == OpCodes.Call && MonotonicSettingAuditLogTests.MethodName(dll, i.Operand) == "SpinOnce");
                foreach (var root in new[] { "TryPublish", "TryConsume", "Beat", "CheckPeerAlive" })
                    Assert.DoesNotContain(TightenOnlySettingGuardTests.Body(dll, Fabric, root).Ins,
                                          i => (i.Op == OpCodes.Call || i.Op == OpCodes.Callvirt) && MonotonicSettingAuditLogTests.MethodName(dll, i.Operand) is "SpinOnce" or "Sleep");

                // 다른 어셈블리(패브릭 없음) = 부재 거부(조용한 통과 0)
                using var tpe = new PEReader(File.OpenRead(typeof(GoldenMasterReleasePackagingTests).Assembly.Location));
                Assert.Equal(TightenSealVerdict.IpcFabricMissing, ShippedTightenMatrixVerifier.CheckIpcFabric(tpe, tpe.GetMetadataReader()));

                string mf = Path.Combine(dir, "RELEASE_MANIFEST.json"), sf = Path.Combine(dir, "master-seal.txt");
                File.WriteAllText(mf, json);
                File.WriteAllText(sf, seal + Environment.NewLine);
                var w = new StringWriter();
                Assert.Equal(ReleaseHandshakeCli.ExitOk, ReleaseHandshakeCli.Run(new[] { "--verify-tighten", zip, "--manifest", mf, "--seal-file", sf, "--expect-tighten-keys", "6",
                                                                                       "--require-audit-ledger", "--require-audit-telemetry", "--require-ipc-fabric" }, w));
                _out.WriteLine(w.ToString().TrimEnd());
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void IpcFabric_BinaryPatched_ConsistentlyResealed_Rejected_OtherSealsUnaffected()
        {
            byte[] dll = File.ReadAllBytes(HostDll());
            int sleep = MemberRefToken(dll, "Thread", "Sleep"), spinOnce = MemberRefToken(dll, "SpinWait", "SpinOnce"), barrier = MemberRefToken(dll, "Interlocked", "MemoryBarrier");
            int consumeBarrier = CallIn(dll, "TryConsume", "MemoryBarrier");
            int enterIncrement = CallIn(dll, "Enter", "Increment");
            int publishEnter = CallIn(dll, "TryPublish", "Enter");
            int disposeSpin = CallIn(dll, "Dispose", "SpinOnce");
            var (coff, _, cins) = TightenOnlySettingGuardTests.Body(dll, Fabric, ".ctor");
            var clock = cins.Single(i => i.Op == OpCodes.Ldftn && MonotonicSettingAuditLogTests.MethodName(dll, i.Operand) == "GetTimestamp");
            static byte[] Tok(int t) => BitConverter.GetBytes(t);
            const TightenSealVerdict B = TightenSealVerdict.IpcFabricBlocking;

            var cases = new (string Name, byte[] Bytes, TightenSealVerdict Expect)[]
            {
                ("TryConsume MemoryBarrier → Thread.Sleep(진입점 직접 슬립)", Patch(dll, b => Tok(sleep).CopyTo(b, consumeBarrier + 1)), B),
                ("Enter Interlocked.Increment → SpinWait.SpinOnce(폐포 전이 — 진입점 아님)", Patch(dll, b => Tok(spinOnce).CopyTo(b, enterIncrement + 1)), B),
                ("TryPublish Enter → Dispose(바운드 드레인 SpinOnce 가 핫 폐포로 유입)", Patch(dll, b => Tok(MonotonicSettingAuditLogTests.TokenOf(dll, Fabric, "Dispose")).CopyTo(b, publishEnter + 1)), B),
                ("Dispose SpinOnce → Thread.Sleep(형식 전체 비결정 슬립)", Patch(dll, b => Tok(sleep).CopyTo(b, disposeSpin + 1)), B),
                ("생성자 기본 시계 Stopwatch.GetTimestamp → 다른 메서드(시계 이탈)", Patch(dll, b => Tok(barrier).CopyTo(b, coff + clock.At + 2)), TightenSealVerdict.IpcFabricClockDrift),
            };
            string dir = TempDir();
            try
            {
                string seal = new string('C', 64);
                var rules = SourceRules();
                int n = 0;
                foreach (var (name, bytes, expect) in cases)
                {
                    string zip = Ps51HostZip(dir, "evil" + n++ + ".zip", bytes);
                    string json = TightenManifest(bytes, seal, rules);
                    var r = ShippedTightenMatrixVerifier.Verify(zip, json, seal, TightenKeys, requireAuditLedger: true, requireAuditTelemetry: true, requireIpcFabric: true);
                    _out.WriteLine($"[IPC 패치] {name} → {r.Verdict}");
                    Assert.Equal(expect, r.Verdict);
                    // 요구 없음 = 기존 동작(행렬 + ⑪ + ⑤ 는 이 패치들과 무관하게 통과 — ⑥ 만이 잡는다)
                    Assert.Equal(TightenSealVerdict.Ok, ShippedTightenMatrixVerifier.Verify(zip, json, seal, TightenKeys, requireAuditLedger: true, requireAuditTelemetry: true).Verdict);
                }
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void IpcFabric_PreviousShipment_v12_18_3_GM_TopologyUnchanged_Ok()
        {
            // 정직: 패브릭 소스는 v12.18.3-GM 이후 변경 없음(v12.18.4-DEV 결정론화는 테스트 하네스 쪽) — 구 출하물도 ⑥ 통과가 맞다.
            string root = RepoRoot();
            string? d = new[] { Path.Combine(root, "release", "superseded-v12.18.3-GM"), Path.Combine(root, "release") }
                .FirstOrDefault(x => File.Exists(Path.Combine(x, "release-v12.18.3-GM.zip")) && File.Exists(Path.Combine(x, "RELEASE_MANIFEST.json"))
                                     && File.ReadAllText(Path.Combine(x, "RELEASE_MANIFEST.json")).Contains("\"v12.18.3-GM\"", StringComparison.Ordinal));
            if (d is null) { _out.WriteLine("[IPC 위상] v12.18.3-GM 출하물 없음 — 구 출하물 대조 생략"); return; }
            var r = ShippedTightenMatrixVerifier.Verify(Path.Combine(d, "release-v12.18.3-GM.zip"), File.ReadAllText(Path.Combine(d, "RELEASE_MANIFEST.json")),
                                                        File.ReadAllText(Path.Combine(d, "master-seal.txt")), TightenKeys, requireAuditLedger: true, requireAuditTelemetry: true, requireIpcFabric: true);
            _out.WriteLine($"[IPC 위상] v12.18.3-GM: {r.Verdict} · {r.Detail}");
            Assert.Equal(TightenSealVerdict.Ok, r.Verdict);
        }

        // ════════════ §15 [v12.18.6-GM] ⑫ 호스트 펌프 지터 결선 IL 판독 ════════════

        private static int MethodToken(byte[] dll, string typeName, string methodName)
        {
            using var pe = new PEReader(new MemoryStream(dll));
            var md = pe.GetMetadataReader();
            foreach (var th in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(th);
                if (md.GetString(t.Name) != typeName) continue;
                foreach (var mh in t.GetMethods())
                    if (md.GetString(md.GetMethodDefinition(mh).Name) == methodName) return MetadataTokens.GetToken(mh);
            }
            Assert.Fail(typeName + "." + methodName + " 없음");
            return 0;
        }

        [Fact]
        public void PumpJitterBinding_BuiltHost_HostAssemblyOnly_DirectBindPatch_Rejected_CliRequires()
        {
            byte[] dll = File.ReadAllBytes(HostDll());
            var ok = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(dll));
            _out.WriteLine($"[⑫] 펌프 지터 결선 {ok.PumpJitterSites} · 시뮬레이션 형식 {ok.SimulationTypes}(CLI {ok.SimulationCliSites}) · 위반 {ok.Violations.Count}");
            Assert.True(ok.Ok, string.Join(" | ", ok.Violations));
            Assert.Equal(1, ok.PumpJitterSites);                                                 // BlazorHomeForm 조립 1곳

            // 호스트가 펌프 결선 대신 외부 센티널을 배지에 직접 결선(call/callvirt 4바이트 토큰 교체 — 같은 시그니처)
            int from = MethodToken(dll, ShippedBinaryLiveArmedGuard.DashHubType, ShippedBinaryLiveArmedGuard.PumpJitterMethod);
            int to = MethodToken(dll, ShippedBinaryLiveArmedGuard.DashHubType, ShippedBinaryLiveArmedGuard.JitterMethod);
            var bad = (byte[])dll.Clone();
            int patched = 0;
            for (int i = 0; i + 4 < bad.Length; i++)
                if ((bad[i] == 0x28 || bad[i] == 0x6F) && BitConverter.ToInt32(bad, i + 1) == from) { BitConverter.GetBytes(to).CopyTo(bad, i + 1); patched++; }
            Assert.True(patched >= 1, "패치 " + patched);
            var r = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(bad));
            foreach (var v in r.Violations) _out.WriteLine("[⑫] 위반: " + v);
            Assert.False(r.Ok);
            Assert.Equal(0, r.PumpJitterSites);
            Assert.Contains(r.Violations, v => v.StartsWith("BlazorHomeForm.", StringComparison.Ordinal) && v.Contains("직접 결선", StringComparison.Ordinal));
            Assert.Contains(r.Violations, v => v.Contains("비공허성", StringComparison.Ordinal) && v.Contains("AttachPumpJitter", StringComparison.Ordinal));

            // CLI: 정상 = Ok · 패치 = 거부 · 요구 플래그 + 결선 진입점 부재(⑫ 이전 바이너리) = 거부
            string good = ZipWithHost(dll, "host\\MS2E.StockSuite.dll"), evil = ZipWithHost(bad, "host\\MS2E.StockSuite.dll");
            try
            {
                var sw = new StringWriter();
                Assert.Equal(ReleaseHandshakeCli.ExitOk, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, good, "--require-pump-jitter" }, sw));
                Assert.Contains("펌프 지터 결선 ⑫ 1", sw.ToString());
                Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, evil, "--require-pump-jitter" }, TextWriter.Null));
            }
            finally { File.Delete(good); File.Delete(evil); }

            string root = RepoRoot();
            string? d = new[] { Path.Combine(root, "release", "superseded-v12.18.5-GM"), Path.Combine(root, "release") }
                .FirstOrDefault(x => File.Exists(Path.Combine(x, "release-v12.18.5-GM.zip")));
            if (d is null) { _out.WriteLine("[⑫] v12.18.5-GM 출하물 없음 — 구 출하물 대조 생략"); return; }
            string old = Path.Combine(d, "release-v12.18.5-GM.zip");
            var o = ShippedBinaryLiveArmedGuard.ScanZip(old);
            Assert.True(o.Ok, string.Join(" | ", o.Violations));                               // 진입점 부재 = ⑫ 미적용(구 바이너리 호환)
            Assert.Equal(0, o.PumpJitterSites);
            Assert.Equal(ReleaseHandshakeCli.ExitOk, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, old }, TextWriter.Null));
            Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, old, "--require-pump-jitter" }, TextWriter.Null));
        }

        // ════════════ §16 [v12.18.7-GM] ⑬ 유령 취소 무장 가드 IL 판독 ════════════

        [Fact]
        public void CancelArmingGuard_BuiltHost_Gated_SamplePatch_And_UnwiredPatch_Rejected_CliRequires()
        {
            byte[] dll = File.ReadAllBytes(HostDll());
            var ok = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(dll));
            _out.WriteLine($"[⑬] 유령 취소 무장 결선 {ok.CancelArmingSites} · 펌프 지터 {ok.PumpJitterSites} · 위반 {ok.Violations.Count}");
            Assert.True(ok.Ok, string.Join(" | ", ok.Violations));
            Assert.Equal(1, ok.CancelArmingSites);                                               // BlazorHomeForm 조립 1곳

            static byte[] Patch(byte[] src, int from, int to)
            {
                var b = (byte[])src.Clone();
                int n = 0;
                for (int i = 0; i + 4 < b.Length; i++)
                    if ((b[i] == 0x28 || b[i] == 0x6F) && BitConverter.ToInt32(b, i + 1) == from) { BitConverter.GetBytes(to).CopyTo(b, i + 1); n++; }
                Assert.True(n >= 1, "패치 0");
                return b;
            }
            const string Rt = ShippedBinaryLiveArmedGuard.ReconcilerType;
            // ① 무장 표본 제거: call CancelArmedNow → call get_GhostDetected(같은 스택 형상 · 항상 참이 될 수 있는 값) = 무장 무관 송신
            var noSample = Patch(dll, MethodToken(dll, Rt, ShippedBinaryLiveArmedGuard.CancelArmedMethod), MethodToken(dll, Rt, "get_GhostDetected"));
            var r1 = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(noSample));
            foreach (var v in r1.Violations) _out.WriteLine("[⑬] 표본 제거 위반: " + v);
            Assert.False(r1.Ok);
            Assert.Contains(r1.Violations, v => v.Contains("무장 무관 취소 송신", StringComparison.Ordinal));
            // ② 호스트 결선 제거: call AttachCancelArming → call AttachKisCanceller(인자 1 · 판독기 스택 무검증)
            var unwired = Patch(dll, MethodToken(dll, Rt, ShippedBinaryLiveArmedGuard.CancelArmingMethod), MethodToken(dll, Rt, "AttachKisCanceller"));
            var r2 = ShippedBinaryLiveArmedGuard.Scan(new MemoryStream(unwired));
            Assert.False(r2.Ok);
            Assert.Equal(0, r2.CancelArmingSites);
            Assert.Contains(r2.Violations, v => v.Contains("비공허성", StringComparison.Ordinal) && v.Contains("AttachCancelArming", StringComparison.Ordinal));

            string good = ZipWithHost(dll, "host\\MS2E.StockSuite.dll"), evil = ZipWithHost(noSample, "host\\MS2E.StockSuite.dll");
            try
            {
                var sw = new StringWriter();
                Assert.Equal(ReleaseHandshakeCli.ExitOk, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, good, "--require-pump-jitter", "--require-cancel-arming" }, sw));
                Assert.Contains("유령 취소 무장 결선 ⑬ 1", sw.ToString());
                Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, evil, "--require-cancel-arming" }, TextWriter.Null));
            }
            finally { File.Delete(good); File.Delete(evil); }

            string root = RepoRoot();
            string? d = new[] { Path.Combine(root, "release", "superseded-v12.18.6-GM"), Path.Combine(root, "release") }
                .FirstOrDefault(x => File.Exists(Path.Combine(x, "release-v12.18.6-GM.zip")));
            if (d is null) { _out.WriteLine("[⑬] v12.18.6-GM 출하물 없음 — 구 출하물 대조 생략"); return; }
            string old = Path.Combine(d, "release-v12.18.6-GM.zip");
            var o = ShippedBinaryLiveArmedGuard.ScanZip(old);
            Assert.True(o.Ok, string.Join(" | ", o.Violations));                               // 진입점 부재 = ⑬ 미적용(구 바이너리 호환)
            Assert.Equal(0, o.CancelArmingSites);
            Assert.Equal(ReleaseHandshakeCli.ExitOk, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, old, "--require-pump-jitter" }, TextWriter.Null));
            Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.VerifyLiveArmedFlag, old, "--require-cancel-arming" }, TextWriter.Null));
        }
    }
}
