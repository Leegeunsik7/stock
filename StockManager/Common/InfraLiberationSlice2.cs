// ============================================================================
// InfraLiberationSlice2 — v8.1.0 Phase 83 인프라 해방 Slice 2: Config/SystemIO Ports & Adapters
//
//  코어/도메인이 콘크리트 인프라(SQLite Settings·System.IO)를 직결 참조하던 결합을 Ports & Adapters(육각형)로
//  해체한다. 코어는 포트 인터페이스(ISystemConfigPort·IFileSystemPort)만 알고, 호스트(Program.cs/BlazorHomeForm
//  합성 루트)가 Engine 어댑터를, 테스트가 InMemory 어댑터를 배선한다. 기존 RuntimeConfig(Func<string,string?>
//  파사드)·Func<long> 시계 주입 관례의 연장 — Func 파사드를 명시적 포트 인터페이스로 승격.
//
//   · AtomicConfigSnapshotPort<T> — RCU(Read-Copy-Update): 핫패스는 락 없이 Volatile.Read 로 최신 스냅샷 참조를
//     읽고(δ=0), 스위처는 Interlocked.Exchange 로 스냅샷 포인터를 원자 교체한다. 리더/라이터 무경합.
//
//  ★불변: δ=0(Current 스냅샷 읽기 힙 0·Volatile) · 결정론(순수·InMemory 주입) · liveArmed:false(순수 인프라 추상 —
//    IBroker·소켓·onSend 참조 0) · fail-closed(어댑터 실패 → 기본값). 코어 순수성(콘크리트 인프라 직결 제거).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace StockManager.Common
{
    // ── 포트(코어가 아는 유일한 인프라 계약) ──────────────────────────────────
    /// <summary>시스템 설정 포트 — 코어는 이 계약만 안다(콘크리트 설정소스 비의존). 실패 시 기본값(fail-safe).</summary>
    public interface ISystemConfigPort
    {
        bool   GetBool(string key, bool def);
        double GetDouble(string key, double def, double min, double max);
        string GetString(string key, string def);
    }

    /// <summary>파일시스템 포트 — 코어는 이 계약만 안다(System.IO 비의존). 실패 시 안전값(fail-safe).</summary>
    public interface IFileSystemPort
    {
        bool   FileExists(string path);
        string ReadAllText(string path, string def);
        bool   TryWriteAllText(string path, string content);
        long   FreeSpaceMB(string rootOrPath);
    }

    // ── Engine 어댑터(호스트가 배선 — 콘크리트 인프라 결선) ────────────────────
    /// <summary>Config 포트의 Engine 어댑터 — RuntimeConfig 파사드에 위임(호스트 합성 루트 배선).</summary>
    public sealed class EngineConfigAdapter : ISystemConfigPort
    {
        private readonly RuntimeConfig _cfg;
        public EngineConfigAdapter(RuntimeConfig cfg) => _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        public bool   GetBool(string key, bool def) => _cfg.GetBool(key, def);
        public double GetDouble(string key, double def, double min, double max) => _cfg.GetDouble(key, def, min, max);
        public string GetString(string key, string def) => _cfg.GetString(key, def);
    }

    /// <summary>FileSystem 포트의 Engine 어댑터 — System.IO 직결(호스트만 콘크리트 인프라를 안다). 전 호출 fail-safe.</summary>
    public sealed class EngineFileSystemAdapter : IFileSystemPort
    {
        public bool FileExists(string path) { try { return File.Exists(path); } catch { return false; } }
        public string ReadAllText(string path, string def) { try { return File.Exists(path) ? File.ReadAllText(path) : def; } catch { return def; } }
        public bool TryWriteAllText(string path, string content) { try { File.WriteAllText(path, content); return true; } catch { return false; } }
        public long FreeSpaceMB(string rootOrPath)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(rootOrPath));
                return string.IsNullOrEmpty(root) ? -1L : new DriveInfo(root).AvailableFreeSpace / (1024L * 1024L);
            }
            catch { return -1L; }
        }
    }

    // ── InMemory 어댑터(테스트가 배선 — 결정론·인프라 무접촉) ──────────────────
    /// <summary>Config 포트의 InMemory 어댑터(테스트·결정론). 딕셔너리 주입.</summary>
    public sealed class InMemoryConfigAdapter : ISystemConfigPort
    {
        private readonly Dictionary<string, string> _map;
        public InMemoryConfigAdapter(Dictionary<string, string>? seed = null) => _map = seed ?? new();
        public void Set(string key, string value) => _map[key] = value;
        public bool GetBool(string key, bool def) => _map.TryGetValue(key, out var v) ? (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) : def;
        public double GetDouble(string key, double def, double min, double max)
            => _map.TryGetValue(key, out var v) && double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)
               ? Math.Clamp(d, min, max) : def;
        public string GetString(string key, string def) => _map.TryGetValue(key, out var v) ? v : def;
    }

    /// <summary>FileSystem 포트의 InMemory 어댑터(테스트·결정론·실 디스크 무접촉).</summary>
    public sealed class InMemoryFileSystemAdapter : IFileSystemPort
    {
        private readonly Dictionary<string, string> _files = new();
        public long FreeMB = 1_000_000;
        public bool FileExists(string path) => _files.ContainsKey(path);
        public string ReadAllText(string path, string def) => _files.TryGetValue(path, out var v) ? v : def;
        public bool TryWriteAllText(string path, string content) { _files[path] = content; return true; }
        public long FreeSpaceMB(string rootOrPath) => FreeMB;
    }

    /// <summary>핫패스 Config 스냅샷(불변 record — RCU 원자 교체 단위). 코어는 포트로 최신 스냅샷을 δ=0 로 읽는다.</summary>
    public sealed record RuntimeConfigSnapshot(bool ValveWired, bool LiveArmingWired, double Var99LimitKrw, bool QuantumVaultArmed)
    {
        /// <summary>포트에서 현재 설정을 캡처해 불변 스냅샷 생성(콜드·합성 루트/스위처 호출).</summary>
        public static RuntimeConfigSnapshot Capture(ISystemConfigPort cfg) => new(
            cfg.GetBool("Dispatch.ValveWired", false),
            cfg.GetBool("Dispatch.LiveArmingWired", false),
            cfg.GetDouble("Risk.Var99LimitKrw", 0.0, 0.0, double.MaxValue),
            cfg.GetBool("Security.QuantumVaultArmed", false));
    }

    // ── AtomicConfigSnapshotPort — RCU 무할당 Config 스위처 ────────────────────
    /// <summary>RCU Config 스냅샷 포트(δ=0 핫패스 읽기·락프리 원자 교체). Current 는 Volatile.Read, Swap 은 Interlocked.Exchange.</summary>
    public sealed class AtomicConfigSnapshotPort<T> where T : class
    {
        private T    _snapshot;
        private long _version;

        public AtomicConfigSnapshotPort(T initial)
            => _snapshot = initial ?? throw new ArgumentNullException(nameof(initial));

        /// <summary>★핫패스 최신 스냅샷 참조(δ=0·락프리). port.Current.Field 로 접근 — 무경합 리더.</summary>
        public T    Current => Volatile.Read(ref _snapshot);
        /// <summary>스냅샷 세대(교체마다 +1·텔레메트리).</summary>
        public long Version => Interlocked.Read(ref _version);

        /// <summary>스냅샷 원자 교체(RCU 스위치·락프리). 이전 스냅샷 반환. 리더는 교체 전/후 일관 스냅샷을 본다.</summary>
        public T Swap(T next)
        {
            if (next is null) throw new ArgumentNullException(nameof(next));
            var prev = Interlocked.Exchange(ref _snapshot, next);
            Interlocked.Increment(ref _version);
            return prev;
        }
    }
}
