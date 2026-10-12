// ============================================================================
// PeerReadinessWatcherTests — [피어 대기 · 방향 B] 준비 상태 전이 경보 · 런북 트리거 디바운스 · 비파괴 · 피어 독립
//
//  §1 전이 순서(주입 판정기 · 결정론): 같은 판정 반복 = 기록 0 · 사유 변화 = 102 기록 · NotReady→Ready 는 같은 HEAD 연속 2회에서만 트리거 1회 ·
//     Ready 유지 = 재트리거 0 · HEAD 변경(피어 재커밋) = 확인 재시작 후 재트리거 · Ready↔NotReady 깜빡임 = 트리거 0.
//  §2 실 임시 저장소 전 구간: 미커밋+키+낡은 봉인 → 커밋 · 키 이관 · 재봉인 → 트리거 1회 · 피어 작업 트리/.git/index 바이트 불변 · CLI 0/2/64.
//  §3 호스트 런타임 미결선(피어 독립) · 이벤트 코드 102.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MS2E.Infrastructure;
using StockManager.Tools;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class PeerReadinessWatcherTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ms2e_watch_" + Guid.NewGuid().ToString("N"));
        public PeerReadinessWatcherTests(ITestOutputHelper output) { _out = output; Directory.CreateDirectory(_root); }

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(_root, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static readonly string H1 = new string('a', 40), H2 = new string('b', 40);
        private static PeerReadinessReport Ready(string head) => new(head, Array.Empty<(PeerReadinessIssue, string)>(), 0, 0);
        private static PeerReadinessReport NotReady(string head, params PeerReadinessIssue[] issues)
            => new(head, issues.Select(i => (i, "x")).ToArray(), issues.Contains(PeerReadinessIssue.UncommittedCode) ? 1 : 0, issues.Contains(PeerReadinessIssue.KeyMaterialPresent) ? 1 : 0);

        private static (PeerReadinessWatcher W, MmfFlightRecorder Box, List<string> Fired) Scripted(Queue<PeerReadinessReport> script, int confirm = 2)
        {
            var box = new MmfFlightRecorder(1_024);
            var fired = new List<string>();
            var w = new PeerReadinessWatcher("peer", box, (r, runbook) => fired.Add(r.Head + "|" + runbook), confirm, probe: (_, _) => script.Dequeue());
            return (w, box, fired);
        }

        private static FlightRecordEvent[] Events(MmfFlightRecorder box)
        {
            var ev = new FlightRecordEvent[1_024];
            int n = box.SnapshotRecent(ev);
            return ev.Take(n).Where(e => e.Kind == (byte)FlightEventKind.PeerReadinessTransition).OrderBy(e => e.Sequence).ToArray();
        }

        // ════════════ §1 전이 순서 ════════════

        [Fact]
        public void Transitions_Recorded_ReadyTriggerDebounced_RetriggerOnlyOnNewHead()
        {
            var q = new Queue<PeerReadinessReport>(new[]
            {
                NotReady(H1, PeerReadinessIssue.UncommittedCode, PeerReadinessIssue.KeyMaterialPresent),   // 1 전이(최초)
                NotReady(H1, PeerReadinessIssue.UncommittedCode, PeerReadinessIssue.KeyMaterialPresent),   // 2 동일 = 기록 0
                NotReady(H1, PeerReadinessIssue.ManifestStale),                                             // 3 사유 변화 = 전이
                Ready(H1),                                                                                  // 4 전이 · 확인 1/2
                Ready(H1),                                                                                  // 5 ★트리거
                Ready(H1),                                                                                  // 6 유지 = 재트리거 0
                Ready(H2),                                                                                  // 7 피어 재커밋 = 전이 · 확인 재시작
                Ready(H2),                                                                                  // 8 ★재트리거
            });
            var (w, box, fired) = Scripted(q);
            bool[] trig = new bool[8];
            for (int i = 0; i < 8; i++) trig[i] = w.Poll();
            Assert.Equal(new[] { false, false, false, false, true, false, false, true }, trig);
            Assert.Equal(2, w.RunbookTriggers);
            Assert.Equal(2, fired.Count);
            Assert.StartsWith(H1 + "|", fired[0]);
            Assert.StartsWith(H2 + "|", fired[1]);
            Assert.Contains("-NoPeer 없이", fired[0]);
            Assert.Contains("사용자 승인", fired[0]);                                                   // 트리거 = 알림(QV 태그는 승인 몫)
            Assert.Equal(4, w.Transitions);                                                                // 1 · 3 · 4 · 7
            var ev = Events(box);
            foreach (var e in ev) _out.WriteLine($"[102] poll#{e.SymbolCode} {e.Arg0:X} → {e.Arg1:X} head {e.Arg2:X}");
            Assert.Equal(6, ev.Length);                                                                    // 전이 4 + 트리거 확정 2
            Assert.Equal(2, ev.Count(e => (e.Arg1 & 2) != 0));                                             // 트리거 비트
            Assert.All(ev, e => Assert.Equal(PeerReadinessWatcher.SourceCode, e.SourceCode));
            Assert.Equal(0xBBBBBBBBBBBBBBBL, ev[^1].Arg2);                                                 // HEAD 앞 15 hex
        }

        [Fact]
        public void Flapping_ReadyNotReadyReady_NeverTriggers_UntilStable()
        {
            var q = new Queue<PeerReadinessReport>(new[] { Ready(H1), NotReady(H1, PeerReadinessIssue.ManifestStale), Ready(H1), NotReady(H1, PeerReadinessIssue.UncommittedCode), Ready(H1), Ready(H1) });
            var (w, _, fired) = Scripted(q);
            var trig = Enumerable.Range(0, 6).Select(_ => w.Poll()).ToArray();
            Assert.Equal(new[] { false, false, false, false, false, true }, trig);                       // 깜빡임 중 0 · 안정 2연속에서만
            Assert.Single(fired);
            Assert.Throws<ArgumentOutOfRangeException>(() => new PeerReadinessWatcher("p", confirmPolls: 0));
            var (w1, _, f1) = Scripted(new Queue<PeerReadinessReport>(new[] { Ready(H1) }), confirm: 1);
            Assert.True(w1.Poll());                                                                        // confirm=1 = 즉시
            Assert.Single(f1);
        }

        // ════════════ §2 실 저장소 전 구간 · 비파괴 ════════════

        private static void Git(string dir, params string[] args)
            => Assert.NotNull(GitRepoProbe.Git(dir, new[] { "-c", "user.name=probe", "-c", "user.email=probe@local", "-c", "commit.gpgsign=false" }.Concat(args).ToArray()));

        private static void Manifest(string repo, DateTimeOffset sealedAt)
        {
            string dir = Path.Combine(repo, "release", "offline-bundle");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "RELEASE_MANIFEST.json"), "{\"rootHash\":\"" + new string('c', 64) + "\",\"sealedAtUtc\":\"" + sealedAt.UtcDateTime.ToString("O") + "\"}");
        }

        /// <summary>작업 트리 + .git 전체(경로 · 크기 · 내용 해시) 지문 — 비파괴 판정용.</summary>
        private static string TreeFingerprint(string repo)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(Path.GetRelativePath(repo, f)).Append('|').Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))).Append('\n');
            return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
        }

        [Fact]
        public void RealRepo_DirtyThenCleanedAndResealed_TriggersOnce_PeerUntouched_Cli()
        {
            string repo = Path.Combine(_root, "qv");
            Directory.CreateDirectory(repo);
            Assert.NotNull(GitRepoProbe.Git(repo, "init", "-q"));
            File.WriteAllText(Path.Combine(repo, ".gitignore"), "release/offline-bundle/\n");
            File.WriteAllText(Path.Combine(repo, "Core.cs"), "class Core {}\n");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-q", "-m", "init");
            Manifest(repo, DateTimeOffset.UtcNow.AddDays(-1));                                               // 낡은 봉인
            File.WriteAllText(Path.Combine(repo, "auto_seal.ps1"), "# 봉인 도구\n");                         // 미커밋
            File.WriteAllText(Path.Combine(repo, "key_out.txt"), "SECRET");                                   // 키

            var box = new MmfFlightRecorder(1_024);
            int fired = 0;
            var w = new PeerReadinessWatcher(repo, box, (_, _) => fired++);
            File.SetLastWriteTimeUtc(Path.Combine(repo, "Core.cs"), DateTime.UtcNow.AddMinutes(9));         // 일반 status 라면 인덱스 재기록할 상태
            string before = TreeFingerprint(repo);
            for (int i = 0; i < 3; i++) Assert.False(w.Poll());
            Assert.Equal(before, TreeFingerprint(repo));                                                       // ★폴링 3회 후 피어 바이트 불변(.git 포함)
            Assert.False(w.Last!.Ready);
            Assert.Equal(1, w.Transitions);
            Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.PeerWatchFlag, repo, "--polls", "2", "--interval-ms", "0" }, TextWriter.Null));
            Assert.Equal(before, TreeFingerprint(repo));

            // 피어 세션 정돈: 커밋 · 키 이관(저장소 밖) · 현재 HEAD 이후 재봉인
            Git(repo, "add", "auto_seal.ps1");
            Git(repo, "commit", "-q", "-m", "seal tool");
            File.Move(Path.Combine(repo, "key_out.txt"), Path.Combine(_root, "key_out.moved"));
            Manifest(repo, DateTimeOffset.UtcNow.AddMinutes(5));
            Assert.False(w.Poll());                                                                            // Ready 1/2
            Assert.True(w.Poll());                                                                             // ★트리거
            Assert.False(w.Poll());                                                                            // 유지
            Assert.Equal(1, fired);
            Assert.True(w.ConfirmedReady);
            Assert.True(Events(box).Length >= 3);
            var sw = new StringWriter();
            Assert.Equal(ReleaseHandshakeCli.ExitOk, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.PeerWatchFlag, repo, "--polls", "3", "--interval-ms", "0" }, sw));
            _out.WriteLine(sw.ToString());
            Assert.Contains("Ready 확정", sw.ToString());
            Assert.Equal(ReleaseHandshakeCli.ExitUsage, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.PeerWatchFlag }, TextWriter.Null));
            Assert.True(ReleaseHandshakeCli.IsInvocation(new[] { ReleaseHandshakeCli.PeerWatchFlag, repo }));
        }

        // ════════════ §3 피어 독립 ════════════

        [Fact]
        public void HostRuntime_DoesNotWireWatcher_EventCode102()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            string form = File.ReadAllText(Path.Combine(d!.FullName, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.DoesNotContain("PeerReadinessWatcher", form);                                             // 운용 CLI · 테스트 전용
            Assert.DoesNotContain("PeerReadinessProbe", form);
            Assert.Equal(102, (int)FlightEventKind.PeerReadinessTransition);
            var values = Enum.GetValues<FlightEventKind>().Select(v => (int)v).ToArray();
            Assert.Equal(values.Length, values.Distinct().Count());
        }
    }
}
