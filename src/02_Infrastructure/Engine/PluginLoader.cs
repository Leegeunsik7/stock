// ============================================================================
// PluginLoader - 플러그인 어댑터 (IO·코드실행, 런타임·사용자 검증 영역) (v3.42.0)
//  · ScanManifests : %LOCALAPPDATA%\StockManager\plugins\**\*.plugin.json 읽기
//  · BuildCatalog  : 스캔 + 설정(활성 id·어셈블리 허용) → 순수 PluginCatalog.Build
//  · LoadAssemblies: 게이팅(Status==Ok)된 .dll만 격리 컨텍스트로 로드 → IStockPlugin
//  ⚠️ 어셈블리 로드는 임의 코드 실행 — 기본 차단(Plugins.AllowAssemblies=true 필요).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace StockManager.Common
{
    // 어셈블리 플러그인 격리 로드 컨텍스트(의존성 해석 포함, 수거 가능).
    internal sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        public PluginLoadContext(string mainPath) : base(isCollectible: true)
            => _resolver = new AssemblyDependencyResolver(mainPath);

        protected override Assembly? Load(AssemblyName name)
        {
            string? p = _resolver.ResolveAssemblyToPath(name);
            return p != null ? LoadFromAssemblyPath(p) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? p = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return p != null ? LoadUnmanagedDllFromPath(p) : IntPtr.Zero;
        }
    }

    public static class PluginLoader
    {
        public static string PluginsDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StockManager", "plugins");

        public static void EnsureDir() { try { Directory.CreateDirectory(PluginsDir); } catch { } }

        /// <summary>*.plugin.json 매니페스트 스캔(하위 폴더 포함). 실패는 조용히 건너뜀.</summary>
        public static List<(string file, string json)> ScanManifests()
        {
            var list = new List<(string, string)>();
            try
            {
                if (!Directory.Exists(PluginsDir)) return list;
                foreach (var f in Directory.GetFiles(PluginsDir, "*.plugin.json", SearchOption.AllDirectories))
                {
                    try { list.Add((f, File.ReadAllText(f))); } catch { }
                }
            }
            catch { }
            return list;
        }

        /// <summary>스캔 + 설정으로 카탈로그 구성(순수 Build 호출).</summary>
        public static PluginCatalog BuildCatalog(LocalDatabase db)
        {
            var raw = ScanManifests();
            var csv = db.GetSetting("Plugins.Enabled");
            ISet<string>? enabled = string.IsNullOrWhiteSpace(csv)
                ? null
                : new HashSet<string>(
                    csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);
            bool allowAsm = string.Equals(db.GetSetting("Plugins.AllowAssemblies"), "true", StringComparison.OrdinalIgnoreCase);
            return PluginCatalog.Build(raw, enabled, allowAsm);
        }

        /// <summary>게이팅된(Ok) 어셈블리 플러그인을 로드·초기화. 런타임·사용자 검증.</summary>
        public static List<IStockPlugin> LoadAssemblies(IEnumerable<PluginDescriptor> descriptors, IPluginHost host)
        {
            var loaded = new List<IStockPlugin>();
            foreach (var d in descriptors.Where(x => x.Manifest.Kind == PluginKind.Assembly && x.Status == PluginStatus.Ok))
            {
                try
                {
                    var dir = Path.GetDirectoryName(d.SourceFile) ?? PluginsDir;
                    var dll = Path.IsPathRooted(d.Manifest.Entry!) ? d.Manifest.Entry! : Path.Combine(dir, d.Manifest.Entry!);
                    if (!File.Exists(dll)) { host.Log($"플러그인 dll 없음: {dll}"); continue; }
                    var alc = new PluginLoadContext(dll);
                    var asm = alc.LoadFromAssemblyPath(dll);
                    foreach (var t in asm.GetTypes())
                    {
                        if (typeof(IStockPlugin).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                        {
                            if (Activator.CreateInstance(t) is IStockPlugin p) { p.Initialize(host); loaded.Add(p); }
                        }
                    }
                }
                catch (Exception ex) { host.Log($"플러그인 로드 실패 [{d.Manifest.Id}]: {ex.Message}"); }
            }
            return loaded;
        }
    }

    /// <summary>어셈블리 플러그인에 제공하는 기본 호스트(설정 읽기 + 로그 콜백).</summary>
    public sealed class SimplePluginHost : IPluginHost
    {
        private readonly LocalDatabase _db;
        private readonly Action<string>? _log;
        public SimplePluginHost(LocalDatabase db, Action<string>? log = null) { _db = db; _log = log; }
        public void Log(string message) => _log?.Invoke(message);
        public string? GetSetting(string key) => _db.GetSetting(key);
    }
}
