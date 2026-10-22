// ============================================================================
// InstallerBuildPorts — v12.18.58-DEV: 배포 설치관리자 빌드 Port & 순수 DTO
//   InstallerBuildPanel 이 InstallerBuildService(파일시스템 · Process.Start · build-installer.ps1) 구상 타입을
//   직접 new 하지 않도록 하는 경계. ★liveArmed:false: 개발 도구 전용(발주 무접촉).
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.UI.Portable.Ports.Tools
{
    /// <summary>설치관리자 빌드 전제 조건(InstallerBuildService.Prereqs 의 순수 사본).</summary>
    public sealed record InstallerPrereqs(bool IsDevEnvironment, string? RepoRoot, bool DotnetSdk, bool InnoSetup, string? DistDir);

    public interface IInstallerBuildPort
    {
        /// <summary>개발 환경 · 전제 조건 감지.</summary>
        InstallerPrereqs Detect();
        /// <summary>build-installer.ps1 실행(출력 실시간 콜백). 반환 = 종료코드(0 = 성공).</summary>
        Task<int> RunBuildAsync(string repoRoot, string version, Action<string> onLine, CancellationToken ct);
        /// <summary>설치관리자 버전 기본값(앱 버전).</summary>
        string DefaultVersion { get; }
        void OpenFolder(string path);
    }
}
