using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace StockManager.Common.Account;

/// <summary>설치 PC 텔레메트리 레코드 — 배포자(관리자)가 설치자 PC 정보를 확인하기 위한 스냅샷.</summary>
public sealed record InstallInfo(
    string MachineName, string UserName, string OsDescription,
    string[] IpAddresses, string[] MacAddresses, string AppVersion,
    string CapturedAt, string DataFolder);

/// <summary>
/// [MS2E StockSuite] 프로그램 설치/기동 시 설치 PC의 호스트·사용자·OS·IP·MAC 을 자동 수집하여
///   운영자 PC 로컬 디스크(install_info.json · %LocalAppData%\StockManager)에 append-only 저장.
///   외부 의존 없이 BCL(System.Net.NetworkInformation)만 사용 · 로컬 IP만 수집. 모니터링/스마트팜/PLAI/UAV와 동일 설계.
/// </summary>
public sealed class InstallTelemetryService
{
    private readonly string _path;
    private readonly object _lock = new();

    /// <summary>가장 최근 캡처된 설치 정보(관리자 화면 표시용).</summary>
    public InstallInfo? Info { get; private set; }
    public string FilePath => _path;

    public InstallTelemetryService()
    {
        _path = Path.Combine(LicenseService.DataAreaRoot(), "install_info.json");
    }

    /// <summary>이 PC에 적재된 설치 이력 전체(관리자 설치 목록 · 최신순).</summary>
    public List<InstallInfo> All()
    {
        try
        {
            if (!File.Exists(_path)) return new List<InstallInfo>();
            var list = JsonSerializer.Deserialize<List<InstallInfo>>(File.ReadAllText(_path)) ?? new List<InstallInfo>();
            list.Reverse();
            return list;
        }
        catch { return new List<InstallInfo>(); }
    }

    /// <summary>기동 시 1회 호출 — 설치 PC 정보 캡처 → 로컬 디스크 append-only 저장.</summary>
    public void CaptureAndPersist()
    {
        try
        {
            var info = Gather();
            Info = info;
            lock (_lock)
            {
                var list = new List<InstallInfo>();
                if (File.Exists(_path))
                {
                    try { list = JsonSerializer.Deserialize<List<InstallInfo>>(File.ReadAllText(_path)) ?? new List<InstallInfo>(); }
                    catch { /* 손상 시 새 목록으로 시작 */ }
                }
                list.Add(info);
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { /* 텔레메트리 실패는 앱 기동을 막지 않는다 */ }
    }

    private static InstallInfo Gather() => new(
        SafeGet(() => Environment.MachineName),
        SafeGet(() => Environment.UserName),
        SafeGet(() => RuntimeInformation.OSDescription),
        LocalIps(),
        Macs(),
        AppVersion(),
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        SafeGet(LicenseService.DataAreaRoot));

    private static string SafeGet(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

    private static string AppVersion()
    {
        try { return typeof(InstallTelemetryService).Assembly.GetName().Version?.ToString() ?? "1.0"; }
        catch { return "1.0"; }
    }

    private static string[] LocalIps()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                .Select(a => a.Address.ToString())
                .Distinct().ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static string[] Macs()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetPhysicalAddress().ToString())
                .Where(m => m.Length >= 12)
                .Select(m => string.Join(":", Enumerable.Range(0, 6).Select(i => m.Substring(i * 2, 2))))
                .Distinct().ToArray();
        }
        catch { return Array.Empty<string>(); }
    }
}
