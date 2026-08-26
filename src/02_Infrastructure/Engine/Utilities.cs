// ============================================================================
// StockManager.Common - 공통 유틸리티
// 안전한 형변환, JSON 파싱, 로깅, HttpClient 싱글톤, 폰트 캐시
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StockManager.Common
{
    /// <summary>API 키 등 민감 문자열 보호 (DPAPI · CurrentUser 범위)</summary>
    public static class Secret
    {
        // 암호화된 값 식별 마커. 레거시 평문은 마커가 없으므로 그대로 반환(하위 호환)
        private const string Marker = "enc:v1:";

        public static string Protect(string? plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var enc = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return Marker + Convert.ToBase64String(enc);
            }
            catch (Exception ex)
            {
                Log.Warn($"[Secret] 암호화 실패(평문 저장): {ex.Message}");
                return plain; // DPAPI 불가 환경 폴백 (차선)
            }
        }

        public static string Unprotect(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith(Marker, StringComparison.Ordinal)) return stored; // 레거시 평문
            try
            {
                var enc = Convert.FromBase64String(stored.Substring(Marker.Length));
                var dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch (Exception ex)
            {
                Log.Warn($"[Secret] 복호화 실패: {ex.Message}");
                return "";
            }
        }

        /// <summary>stored가 DPAPI로 암호화된 형식(enc:v1: 마커)인지. 평문·빈값·null이면 false.</summary>
        public static bool IsProtected(string? stored) =>
            !string.IsNullOrEmpty(stored) && stored.StartsWith(Marker, StringComparison.Ordinal);
    }

    /// <summary>DB/JSON에서 안전한 형변환</summary>
    public static class Safe
    {
        public static decimal Dec(object? v)
        {
            if (v == null || v == DBNull.Value) return 0m;
            try { return Convert.ToDecimal(v, CultureInfo.InvariantCulture); }
            catch { return 0m; }
        }

        public static int Int(object? v)
        {
            if (v == null || v == DBNull.Value) return 0;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        public static long Long(object? v)
        {
            if (v == null || v == DBNull.Value) return 0L;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch { return 0L; }
        }

        public static string Str(object? v) =>
            v == null || v == DBNull.Value ? "" : v.ToString() ?? "";

        public static DateTime DT(object? v, DateTime? fallback = null)
        {
            if (v == null || v == DBNull.Value) return fallback ?? DateTime.Now;
            try { return Convert.ToDateTime(v, CultureInfo.InvariantCulture); }
            catch
            {
                return DateTime.TryParse(v.ToString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dt) ? dt : (fallback ?? DateTime.Now);
            }
        }

        // JSON 안전 파서 - Yahoo/KIS 모두 대응 (int/double/string 혼재)
        public static decimal JsonDec(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetDecimal(out var d) ? d : (decimal)e.GetDouble(),
            JsonValueKind.String =>
                decimal.TryParse(e.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m,
            JsonValueKind.Null => 0m,
            _ => 0m
        };

        public static decimal JsonDec(JsonElement e, string prop)
        {
            if (!e.TryGetProperty(prop, out var v)) return 0m;
            return JsonDec(v);
        }

        public static long JsonLong(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : (long)e.GetDouble(),
            JsonValueKind.String =>
                long.TryParse(e.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : 0L,
            _ => 0L
        };

        public static long JsonLong(JsonElement e, string prop)
        {
            if (!e.TryGetProperty(prop, out var v)) return 0L;
            return JsonLong(v);
        }

        public static string JsonStr(JsonElement e, string prop)
        {
            if (!e.TryGetProperty(prop, out var v)) return "";
            return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" :
                   v.ValueKind == JsonValueKind.Null ? "" : v.ToString();
        }
    }

    /// <summary>HttpClient 싱글톤 - 소켓 고갈 방지</summary>
    public static class HttpPool
    {
        private static readonly Lazy<HttpClient> _shared = new(() =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 10,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };
            var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 MS2E-StockSuite/2.2");
            c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ko-KR,en;q=0.8");
            return c;
        });

        public static HttpClient Shared => _shared.Value;
    }

    /// <summary>간단한 파일 기반 로거 - MS2-E 스타일</summary>
    public static class Log
    {
        private static readonly object _lock = new();
        public static string LogDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StockManager", "logs");

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg, Exception? ex = null) =>
            Write("ERROR", msg + (ex == null ? "" : $"\n{ex}"));

        private static void Write(string level, string msg)
        {
            // S22 #3 — 단일 초크포인트 비밀 차폐: 파일·디버그 출력 전에 등록 비밀·라벨 비밀 마스킹.
            var safe = LogRedactor.Shared.Scrub(msg);
            try
            {
                Directory.CreateDirectory(LogDir);
                var file = Path.Combine(LogDir, $"stock_{DateTime.Now:yyyyMMdd}.log");
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {safe}{Environment.NewLine}";
                lock (_lock) File.AppendAllText(file, line);
            }
            catch { /* 로깅 실패는 무시 */ }

            if (level == "ERROR") System.Diagnostics.Debug.WriteLine($"[{level}] {safe}");
        }
    }

    /// <summary>GDI+ 폰트/브러시 캐시 - Paint 이벤트 누수 방지</summary>
    public static class GdiCache
    {
        private static readonly ConcurrentDictionary<string, Font> _fonts = new();
        private static readonly ConcurrentDictionary<int, SolidBrush> _brushes = new();
        private static readonly ConcurrentDictionary<long, Pen> _pens = new();

        // 기본 폰트 프리셋
        public static readonly Font Small = Font("맑은 고딕", 8F);
        public static readonly Font SmallBold = Font("맑은 고딕", 8F, FontStyle.Bold);
        public static readonly Font Normal = Font("맑은 고딕", 9F);
        public static readonly Font NormalBold = Font("맑은 고딕", 9F, FontStyle.Bold);
        public static readonly Font Medium = Font("맑은 고딕", 10F);
        public static readonly Font MediumBold = Font("맑은 고딕", 10F, FontStyle.Bold);
        public static readonly Font Large = Font("맑은 고딕", 11F);
        public static readonly Font LargeBold = Font("맑은 고딕", 11F, FontStyle.Bold);
        public static readonly Font XLarge = Font("맑은 고딕", 12F, FontStyle.Bold);
        public static readonly Font Title = Font("맑은 고딕", 14F, FontStyle.Bold);
        public static readonly Font Header = Font("맑은 고딕", 16F, FontStyle.Bold);
        public static readonly Font Emoji = Font("Segoe UI Emoji", 38F);

        public static Font Font(string name, float size, FontStyle style = FontStyle.Regular)
        {
            var key = $"{name}|{size}|{(int)style}";
            return _fonts.GetOrAdd(key, _ => new Font(name, size, style));
        }

        public static SolidBrush Brush(Color c)
        {
            var key = c.ToArgb();
            return _brushes.GetOrAdd(key, _ => new SolidBrush(c));
        }

        public static Pen Pen(Color c, float width = 1f)
        {
            var key = ((long)c.ToArgb() << 16) | (long)(width * 100);
            return _pens.GetOrAdd(key, _ => new Pen(c, width));
        }
    }
}
