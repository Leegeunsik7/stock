// ============================================================================
// InstallerBuildAdapter — v12.18.58-DEV: IInstallerBuildPort → InstallerBuildService 위임(재발명 0)
//   InstallerBuildPanel 의 InstallerBuildService 직접 new · SystemVersionProvider 직결을 이 어댑터 뒤로 격리.
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Tools;

namespace MS2E.Infrastructure
{
    public sealed class InstallerBuildAdapter : IInstallerBuildPort
    {
        private readonly InstallerBuildService _svc = new();

        public InstallerPrereqs Detect()
        {
            var p = _svc.Detect();
            return new InstallerPrereqs(p.IsDevEnvironment, p.RepoRoot, p.DotnetSdk, p.InnoSetup, p.DistDir);
        }

        public Task<int> RunBuildAsync(string repoRoot, string version, Action<string> onLine, CancellationToken ct)
            => _svc.RunBuildAsync(repoRoot, version, onLine, ct);

        public string DefaultVersion
        {
            get { try { return SystemVersionProvider.Version ?? ""; } catch { return ""; } }
        }

        public void OpenFolder(string path) => _svc.OpenFolder(path);
    }
}
