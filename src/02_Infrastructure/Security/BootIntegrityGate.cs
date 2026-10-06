// ============================================================================
// BootIntegrityGate — [F7] Cold-Boot Anti-Tamper Handshake 부팅 결속 + 설치본 manifest.sig 런타임 수렴
//
//   릴리스 zip 레이아웃(release-package.ps1): <root>/manifest.sig · <root>/host/MS2E.StockSuite.exe … · <root>/proxy/…
//   실행 시 AppContext.BaseDirectory = <root>/host 이므로 manifest.sig 는 BaseDirectory → 상위 폴더 순으로 탐지한다.
//   수렴 순서(전부 콜드 — 부팅 1회):
//     ① manifest.sig 탐지(없으면 Unsealed — 개발 빌드/비봉인 설치본: 무장 차단·소거 없음)
//     ② v2 스키마 파싱(System.Text.Json · BCL) + 이름 검증(역할 host/·proxy/ 한정 · 경로 이탈 "..", 구분자 거부)
//     ③ 외부 신뢰 앵커 대조(Release.ExpectedMasterSeal 주입 시 manifest.masterSeal 과 상수시간 비교) — manifest 자체 위조 차단
//     ④ 디스크 파일로 masterSeal 재계산(ReleasePackager v2) — 매니페스트 내부 일관성
//     ⑤ AssemblyAntiTamperHandshake 구성 → Verify(δ=0) → 백그라운드 모니터 주기 재검증
//   판정: Sealed 만 무장 허용 후보. Unsealed/Incomplete/ManifestInvalid/Verifying/Tampered/Faulted → ArmingBlocked(fail-closed).
//   Tampered(해시 불일치·런타임 변조)는 핸드셰이크 Trip → 발주 토큰/Secret 0x00 소거(sticky). 누락(Incomplete)·위조 매니페스트는
//   차단만 하고 소거하지 않는다(설치 레이아웃 차이로 인한 오탐 소거 방지 — 무장이 막히므로 안전 측).
//   ★정직 한계: manifest.sig 는 서명키 없는 해시 목록이다 — 외부 앵커(③) 미주입이면 manifest 를 통째로 재작성한 공격은
//   ④·⑤를 통과한다. [봉인 운영] requireExternalAnchor=true(호스트 기본) 이면 그 상태는 Unanchored(무장 차단)로 끝난다.
//
//  [봉인 운영 · 추천 A] 교정(실측):
//   · 수정 전 Release.ExpectedMasterSeal 은 두 검사에 동시에 주입됐다 — 이 게이트(③: manifest v2 masterSeal 과 대조)와
//     ReleaseArtifactSha256Attestor(실행 DLL 단일 파일 SHA-256 과 대조). 두 값은 정의상 같을 수 없으므로 어떤 설정값으로도
//     둘 다 통과할 수 없었다 → 무장 술어가 영구 차단(fail-closed 를 넘어선 교착). 이제 앵커 판정은 이 게이트 단일 권위이고
//     Attestor 는 판정을 이 게이트에 위임한다(DelegateToManifestGate).
//   · 앵커 형식 엄격화: 64 hex 가 아니면(짧음·비hex·공백만) 비교 없이 ManifestInvalid. 1비트 불일치 = 상수시간 비교 실패.
//   · 앵커 불일치(= 바이너리+매니페스트 동시 재작성 의심) → 차단 + Trip(Secret/발주 토큰 0x00 소거 + 킬 콜백 · sticky).
//   · [방향 B] Seal Tamper Evidence: 실패 판정마다 (UTC 시각 · 상태 · 아티팩트 · 기대 SHA-256 · 실측 SHA-256 · 사유)를
//     증거 링(최대 64)에 남기고, 포렌식 해시 체인(ZeroAllocForensicAuditStream)에 SealTamper 이벤트 쌍(기대 lo64 · 실측 lo64)으로
//     원자 각인 — 체인은 이후 위조 시 ForensicChainDisarmInterlock 이 감지한다.
//   불변: liveArmed:false(차단만 가능·무장 부여 불가) · 부팅 비차단(검증은 스레드풀, 그동안 Verifying=차단) · 결정론.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;

namespace MS2E.Infrastructure
{
    public enum BootIntegrityStatus : byte { Verifying = 0, Sealed = 1, Unsealed = 2, Incomplete = 3, ManifestInvalid = 4, Tampered = 5, Faulted = 6, Unanchored = 7 }

    /// <summary>[방향 B] 봉인 변조 증거 1건(콜드 · 불변). Expected/Actual 은 SHA-256 hex(해당 없음 = "").</summary>
    public readonly record struct SealTamperEvidence(long UtcTicks, BootIntegrityStatus Status, string Artifact,
                                                     string ExpectedSha256, string ActualSha256, string Detail);

    public sealed class BootIntegrityGate : IDisposable, MS2E.Core.IArmingBlockSource
    {
        public const string ManifestFileName = "manifest.sig";

        private readonly string _baseDir;
        private readonly string _expectedMasterSeal;
        private readonly VolatileMemoryPurger? _purger;
        private readonly IReadOnlyList<PoisonedSecretBuffer>? _secrets;
        private readonly Action<TamperReason>? _onTrip;
        private readonly TimeSpan _monitorInterval;
        private AssemblyAntiTamperHandshake? _handshake;
        private int _status;   // BootIntegrityStatus
        private int _disposed;
        private readonly bool _requireAnchor;
        private int _anchorVerified;
        public const int MaxEvidence = 64;
        private readonly object _evidenceGate = new();
        private readonly List<SealTamperEvidence> _evidence = new();
        private ZeroAllocForensicAuditStream? _forensic;

        public string? ManifestPath { get; private set; }
        public string Detail { get; private set; } = "";
        public int ArtifactCount => _handshake?.ArtifactCount ?? 0;
        public BootIntegrityStatus Status
        {
            get
            {
                var s = (BootIntegrityStatus)Volatile.Read(ref _status);
                return (s is BootIntegrityStatus.Sealed or BootIntegrityStatus.Unanchored) && (_handshake?.IsTripped ?? false) ? BootIntegrityStatus.Tampered : s;
            }
        }

        /// <summary>★blockWhile — Sealed 이고 핸드셰이크가 무장 허용일 때만 false(그 외 전부 차단 · fail-closed).</summary>
        public bool ArmingBlocked => Status != BootIntegrityStatus.Sealed || (_handshake?.ArmingBlocked ?? true)
                                     || (_requireAnchor && Volatile.Read(ref _anchorVerified) == 0);
        /// <summary>외부 신뢰 앵커(Release.ExpectedMasterSeal)가 주입됐고 manifest.masterSeal 과 정확히 일치.</summary>
        public bool AnchorVerified => Volatile.Read(ref _anchorVerified) == 1;
        public bool RequireExternalAnchor => _requireAnchor;
        /// <summary>[방향 B] 변조 증거 스냅샷(콜드).</summary>
        public IReadOnlyList<SealTamperEvidence> TamperEvidence { get { lock (_evidenceGate) return _evidence.ToArray(); } }

        /// <summary>[방향 B] 포렌식 해시 체인 결속 — 결속 전 쌓인 증거도 재생 각인(부팅 순서상 체인이 게이트보다 늦게 생성돼도 유실 0).</summary>
        public void AttachForensicStream(ZeroAllocForensicAuditStream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            lock (_evidenceGate)
            {
                if (_forensic is not null) return;
                _forensic = stream;
                foreach (var e in _evidence) AppendForensic(stream, e);
            }
        }

        private void Evidence(BootIntegrityStatus status, string artifact, string expected, string actual, string detail)
        {
            var e = new SealTamperEvidence(DateTime.UtcNow.Ticks, status, artifact, expected, actual, detail);
            lock (_evidenceGate)
            {
                if (_evidence.Count < MaxEvidence) _evidence.Add(e);
                if (_forensic is { } f) AppendForensic(f, e);
            }
        }

        // 체인 각인 = (기대 lo64, 실측 lo64) 2 링크 — 각 링크는 시각·순번·이전 해시에 결속(사후 한쪽만 고치는 위조 불가).
        private static void AppendForensic(ZeroAllocForensicAuditStream f, in SealTamperEvidence e)
        {
            try
            {
                f.Append(AuditEventKind.SealTamper, Low64(e.ExpectedSha256), e.UtcTicks);
                f.Append(AuditEventKind.SealTamper, Low64(e.ActualSha256), e.UtcTicks);
            }
            catch { /* 감사 각인 실패가 차단 판정을 되돌리지 않음 */ }
        }

        private static long Low64(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length < 16) return 0;
            ulong v = 0;
            for (int i = hex.Length - 16; i < hex.Length; i++)
            {
                int d = hex[i] switch { >= '0' and <= '9' => hex[i] - '0', >= 'a' and <= 'f' => hex[i] - 'a' + 10, >= 'A' and <= 'F' => hex[i] - 'A' + 10, _ => -1 };
                if (d < 0) return 0;
                v = (v << 4) | (uint)d;
            }
            return (long)v;
        }

        private static string ActualHex(string path)
        {
            try { return Convert.ToHexString(PinnedSha256Streamer.HashFile(path, out _)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "READ-FAULT"; }
        }

        /// <summary>엄격 앵커 형식 — 공백 제거 후 정확히 64 hex.</summary>
        public static bool IsStrictSealHex(string? s)
        {
            if (s is null) return false;
            var t = s.AsSpan().Trim();
            if (t.Length != 64) return false;
            foreach (char c in t) if (!char.IsAsciiHexDigit(c)) return false;
            return true;
        }

        // [실배선 · 방향 B] 원자 인터락 집계기 푸시 결속(ArmingInterlockBitmask) — 상태 전이 직후 Resync(δ=0).
        private MS2E.Core.ArmingInterlockLink? _interlockLink;
        /// <summary>집계기에 이 엔진을 고정 비트로 결속 — 결속 즉시 현재 상태 동기화(이미 차단이면 비트 유지).</summary>
        public void BindInterlock(MS2E.Core.ArmingInterlockBitmask aggregator, MS2E.Core.ArmingInterlockSource bit)
        {
            ArgumentNullException.ThrowIfNull(aggregator);
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _interlockLink, link);
            link.Resync();   // Bind ~ 링크 가시화 사이의 전이 흡수
        }
        private void SignalInterlock() => Volatile.Read(ref _interlockLink)?.Resync();

        // 핸드셰이크(30초 주기 재검증) Trip → 집계 비트 Raise 후 원 콜백. 핸드셰이크가 플래그를 먼저 세운 뒤 호출한다.
        private void OnHandshakeTrip(TamperReason reason)
        {
            SignalInterlock();
            // 부팅 수렴 중 Trip 은 Converge 가 아티팩트·기대/실측 해시까지 상세 증거로 남긴다 — 여기선 봉인 이후(런타임 재검증)만 기록.
            if ((BootIntegrityStatus)Volatile.Read(ref _status) is BootIntegrityStatus.Sealed or BootIntegrityStatus.Unanchored)
                Evidence(BootIntegrityStatus.Tampered, "runtime-monitor", "", "", "런타임 재검증 Trip: " + reason);
            _onTrip?.Invoke(reason);
        }

        private BootIntegrityGate(string baseDir, string expectedMasterSeal, VolatileMemoryPurger? purger,
                                  IReadOnlyList<PoisonedSecretBuffer>? secrets, Action<TamperReason>? onTrip, TimeSpan monitorInterval,
                                  bool requireExternalAnchor)
        {
            _baseDir = baseDir; _expectedMasterSeal = expectedMasterSeal ?? ""; _purger = purger;
            _secrets = secrets; _onTrip = onTrip; _monitorInterval = monitorInterval; _requireAnchor = requireExternalAnchor;
        }

        /// <summary>게이트 생성 + 검증 착수. runAsync=true 면 스레드풀에서 검증(부팅 UI 스레드 비차단 — 완료 전 Verifying=차단).</summary>
        public static BootIntegrityGate Start(string baseDir, string? expectedMasterSeal = null, VolatileMemoryPurger? purger = null,
                                             IReadOnlyList<PoisonedSecretBuffer>? secrets = null, Action<TamperReason>? onTrip = null,
                                             TimeSpan? monitorInterval = null, bool runAsync = true,
                                             bool requireExternalAnchor = false)
        {
            var g = new BootIntegrityGate(baseDir ?? throw new ArgumentNullException(nameof(baseDir)), expectedMasterSeal ?? "",
                                          purger, secrets, onTrip, monitorInterval ?? TimeSpan.FromSeconds(30), requireExternalAnchor);
            if (runAsync) _ = Task.Run(g.Converge);
            else g.Converge();
            return g;
        }

        private void Set(BootIntegrityStatus s, string detail) { Detail = detail; Volatile.Write(ref _status, (int)s); SignalInterlock(); }

        private void Converge()
        {
            try
            {
                string? manifest = Locate(_baseDir);
                if (manifest is null) { Set(BootIntegrityStatus.Unsealed, "manifest.sig 없음(비봉인 빌드) — 무장 차단"); return; }
                ManifestPath = manifest;
                string root = Path.GetDirectoryName(manifest)!;

                if (!TryParse(File.ReadAllText(manifest), out var entries, out string masterSeal, out string why))
                { Set(BootIntegrityStatus.ManifestInvalid, why); return; }

                // ③ 외부 신뢰 앵커 — 엄격 형식(64 hex) · 상수시간 1비트 비교. 불일치 = 바이너리+매니페스트 동시 재작성 의심 → 소거·킬.
                if (_expectedMasterSeal.Trim().Length > 0)
                {
                    if (!IsStrictSealHex(_expectedMasterSeal))
                    {
                        Evidence(BootIntegrityStatus.ManifestInvalid, "Release.ExpectedMasterSeal", "", masterSeal, "외부 앵커 형식 오류(64 hex 아님)");
                        Set(BootIntegrityStatus.ManifestInvalid, "Release.ExpectedMasterSeal 형식 오류(64 hex 필요) — 무장 차단");
                        return;
                    }
                    if (!HexEquals(_expectedMasterSeal, masterSeal))
                    {
                        Evidence(BootIntegrityStatus.ManifestInvalid, "manifest.sig#masterSeal", _expectedMasterSeal.Trim().ToUpperInvariant(), masterSeal.ToUpperInvariant(),
                                 "외부 신뢰 앵커 불일치 — 바이너리·매니페스트 동시 변조 의심");
                        Set(BootIntegrityStatus.ManifestInvalid, "manifest.masterSeal ≠ 외부 신뢰 앵커(Release.ExpectedMasterSeal) — 동시 변조 의심 · 소거 · 영구 차단");
                        Trip();
                        return;
                    }
                    Volatile.Write(ref _anchorVerified, 1);
                }

                var named = new List<ReleasePackager.ReleaseArtifact>(entries.Count);
                var shake = new List<(string Path, byte[] ExpectedSha256)>(entries.Count);
                foreach (var (name, sha) in entries)
                {
                    string? path = Resolve(root, name);
                    if (path is null)
                    {
                        Evidence(BootIntegrityStatus.Incomplete, name, Convert.ToHexString(sha), "MISSING", "아티팩트 누락");
                        Set(BootIntegrityStatus.Incomplete, "아티팩트 누락: " + name);
                        return;
                    }
                    named.Add(new(name, path));
                    shake.Add((path, sha));
                }
                if (!ReleasePackager.VerifyArtifacts(named, masterSeal))
                {
                    // 디스크 바이너리가 매니페스트와 다름 = 변조 → 핸드셰이크로 Trip(소거) 경로 태움
                    if (!Adopt(new AssemblyAntiTamperHandshake(shake, _secrets, _purger, OnHandshakeTrip))) return;
                    var v0 = _handshake!.Verify();
                    if (v0.Passed)
                    {
                        string recomputed;
                        try { recomputed = ReleasePackager.ComputeMasterSealHex(named); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { recomputed = "READ-FAULT"; }
                        Evidence(BootIntegrityStatus.Tampered, "manifest.sig#artifactSet", masterSeal.ToUpperInvariant(), recomputed, "개별 해시 일치 · 봉인 불일치(항목 누락/추가/개명)");
                    }
                    else if ((uint)v0.Index < (uint)shake.Count)
                        Evidence(BootIntegrityStatus.Tampered, entries[v0.Index].Name, Convert.ToHexString(entries[v0.Index].Sha), ActualHex(shake[v0.Index].Path), "바이너리 해시 불일치: " + v0.Reason);
                    Set(BootIntegrityStatus.Tampered, v0.Passed ? "masterSeal 불일치" : $"변조: {v0.Reason} #{v0.Index}");
                    if (v0.Passed) Trip();   // 개별 해시는 맞으나 봉인이 다름(항목 누락/추가) — 매니페스트 불일치로 소거
                    return;
                }
                if (!Adopt(new AssemblyAntiTamperHandshake(shake, _secrets, _purger, OnHandshakeTrip))) return;
                var v = _handshake!.Verify();
                if (!v.Passed)
                {
                    if ((uint)v.Index < (uint)shake.Count)
                        Evidence(BootIntegrityStatus.Tampered, entries[v.Index].Name, Convert.ToHexString(entries[v.Index].Sha), ActualHex(shake[v.Index].Path), "바이너리 해시 불일치: " + v.Reason);
                    Set(BootIntegrityStatus.Tampered, $"변조: {v.Reason} #{v.Index}");
                    return;
                }
                if (Volatile.Read(ref _disposed) == 1) return;
                if (_requireAnchor && Volatile.Read(ref _anchorVerified) == 0)
                    Set(BootIntegrityStatus.Unanchored, $"봉인 내부 일관 {entries.Count}건 · 외부 신뢰 앵커(Release.ExpectedMasterSeal) 미주입 — 무장 차단");
                else
                    Set(BootIntegrityStatus.Sealed, $"봉인 검증 {entries.Count}건 일치" + (AnchorVerified ? " · 외부 앵커 일치" : ""));
                _handshake.StartMonitor(_monitorInterval);   // 경합 Dispose 시 ObjectDisposedException → 아래 catch(Faulted = 차단)
            }
            catch (Exception ex)
            {
                Set(BootIntegrityStatus.Faulted, ex.GetType().Name + ": " + ex.Message);   // 예외 = 차단(fail-closed)
            }
        }

        // [실결함 교정] 비동기 수렴 중 게이트가 이미 처분됐으면(Dispose 가 _handshake=null 을 본 경우) 새 핸드셰이크를 스스로 처분 —
        //   수정 전엔 아티팩트 파일 핸들(FileShare.Read = 쓰기·삭제 거부)이 GC 종료자까지 누수돼 바이너리 교체/삭제가 막혔다.
        //   Dekker 순서: 여기는 _handshake 게시(Exchange) → _disposed 판독, Dispose 는 _disposed 기록(Exchange) → _handshake 판독 —
        //   양쪽 전 펜스라 적어도 한쪽이 상대를 보고 처분한다(이중 처분은 멱등).
        private bool Adopt(AssemblyAntiTamperHandshake h)
        {
            Interlocked.Exchange(ref _handshake, h);
            if (Volatile.Read(ref _disposed) == 0) return true;
            h.Dispose();
            return false;
        }

        private void Trip()
        {
            try { if (_secrets is not null) foreach (var s in _secrets) s.PurgeAndPoison(); } catch { }
            try { _purger?.PurgeAll(); } catch { }
            try { _onTrip?.Invoke(TamperReason.ArtifactHashMismatch); } catch { }
        }

        public static string? Locate(string baseDir)
        {
            string here = Path.Combine(baseDir, ManifestFileName);
            if (File.Exists(here)) return here;
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(baseDir));
            if (parent is not null)
            {
                string up = Path.Combine(parent, ManifestFileName);
                if (File.Exists(up)) return up;
            }
            return null;
        }

        /// <summary>역할 한정 이름 → 경로. host/ = manifest 폴더의 host 하위(없으면 manifest 폴더 자체), proxy/ 동일. 이탈 이름은 null.</summary>
        private static string? Resolve(string root, string name)
        {
            int slash = name.IndexOf('/');
            if (slash <= 0) return null;
            string role = name[..slash], file = name[(slash + 1)..];
            if (role is not ("host" or "proxy")) return null;
            if (file.Length == 0 || file.Contains("..", StringComparison.Ordinal) || file.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return null;
            foreach (string dir in new[] { Path.Combine(root, role), root })
            {
                string p = Path.Combine(dir, file);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        internal static bool TryParse(string json, out List<(string Name, byte[] Sha)> entries, out string masterSeal, out string why)
        {
            entries = new(); masterSeal = ""; why = "";
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (!r.TryGetProperty("schema", out var sc) || sc.GetString() != "ms2e-release-manifest/2") { why = "스키마 v2 아님"; return false; }
                if (!r.TryGetProperty("masterSeal", out var ms) || ms.GetString() is not { Length: 64 } seal) { why = "masterSeal 형식 오류"; return false; }
                masterSeal = seal;
                if (!r.TryGetProperty("artifacts", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) { why = "아티팩트 0건"; return false; }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in arr.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? hex = a.TryGetProperty("sha256", out var h) ? h.GetString() : null;
                    if (name is null || hex is not { Length: 64 }) { why = "아티팩트 항목 형식 오류"; return false; }
                    if (!seen.Add(name)) { why = "동명 아티팩트: " + name; return false; }
                    entries.Add((name, Convert.FromHexString(hex)));
                }
                if (r.TryGetProperty("artifactCount", out var c) && c.GetInt32() != entries.Count) { why = "artifactCount 불일치"; return false; }
                return true;
            }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
            {
                why = "매니페스트 파싱 실패: " + ex.GetType().Name;
                return false;
            }
        }

        private static bool HexEquals(string a, string b)
        {
            try
            {
                byte[] x = Convert.FromHexString(a.Trim()), y = Convert.FromHexString(b.Trim());
                return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
            }
            catch (FormatException) { return false; }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Volatile.Read(ref _handshake)?.Dispose();
            SignalInterlock();   // 처분 = 차단(ArmingBlocked) → 집계 비트 Raise
        }
    }
}
