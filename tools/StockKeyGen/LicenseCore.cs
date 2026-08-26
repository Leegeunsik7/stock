using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LicenseKeyGen;

/// <summary>발급 명부 레코드 — 본 앱(StockManager.Common.Account.LicenseRecord)과 JSON 스키마가 100% 동일해야 관리자 화면(/admin/users)에 그대로 표시됩니다.</summary>
public sealed record LicenseRecord(string Id, string Licensee, string Key, string IssuedAt, string Expiry);

/// <summary>
/// [독립 발급기] MS2E StockSuite 관제 앱과 동일한 오프라인 서명·명부 로직을 자체 포함(외부 NuGet 0 · BCL RSA만).
///   • 앱과 같은 키페어를 공유(여기 내장 개인키 ↔ 앱 LicenseService.cs 의 PublicKey 상수)해
///     여기서 만든 키가 앱에서 그대로 검증되고, 발급 즉시 같은 명부(license_registry.json)에 적재 → /admin/users 발급 라이선스 관리에 자동 등장.
///   • 반드시 발급자(배포자) 본인 PC에서 실행. 기존 트레이딩 권한 게이트(AdminAuthoritySealGuard)와는 별개 키.
/// </summary>
public static class LicenseCore
{
    // ── 앱과 동일 경로 (LicenseService.DataAreaRoot) ──
    public static string DataAreaRoot()
    {
        var b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(b)) b = Path.GetTempPath();
        var d = Path.Combine(b, "StockManager");
        try { Directory.CreateDirectory(d); } catch { /* 경로만 반환 */ }
        return d;
    }

    private static string RegistryPath => Path.Combine(DataAreaRoot(), "license_registry.json");

    public static bool SigningKeyExists => true;   // 서명키 코드 내장 → 항상 사용 가능
    public static string RegistryFilePath => RegistryPath;

    // [근본] 서명키를 코드에 내장(발급기 전용 · 배포되지 않음).
    //   앱은 LicenseService.cs 의 PublicKey 상수(이 키의 공개키)로 검증하므로 프로세스·PC 무관하게 100% 일치.
    private const string EmbeddedPrivateKeyPkcs8 =
        "MIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQCpQRnpWdwn/zbgwFJKURIYbKfxcQuHM+bBJPLPVGFRhmA1IA9FO8oB7ysSKhb6epk3eNy3nvXvwNTCJSA0oZ5k+FYI1bXIpoerz+fkwXcNst1iLTF+HgBQwf7RA7dzLHiDUhWCf3GPWq5LwxkIZ+li/3qyNruMDaz/Y/1jpBh2hOgDpQKsPKiIszuCuqf4m33wPFXBAKOy5H+AojqdWQX69gGLUfxeZTbTNTHFY+41AnAninEkj8E7KWKyYgIso9nHyEZgsmuhaZqpJDazYBhW+/WucsTYZO7gjHXrLbvr6GASk9s1UWF/MtU2tJHW9iGmHmtnmqPbocN+loIduUr9AgMBAAECggEADyjjl0ETNQno8myHclyckE97I2cKr0t4yafzj5pxNQoiJ9/9PihCybWQWAopELpWat+KdW/pe1X8OH92yQAI3eZYXmY16AjycfMnRBgH/9QbfCWsHy20vYcCudnpXomHKcv6XTvpAfwPTH3nChe0kcdsR/YbCkpXSnC8S2ZxQnwzoLH11bXhPe1759dPIzODSO8rN3Cj3OGcYxmJUNjulG5IQ8Ur3zHMwaCIyJqMMT49E55g2d+v42TjwPbI3ugEYxIXfWRFJsMvRwNch8Udr6uAnjdUliB9Z8pqgZ9bH6Oq7edF2gx8dmpcIcsrxmG59wbg2f6qBU6183kfmpW5iQKBgQDf1rbbQphVDBTiWEzm67A8Tpk+dA/EkK6nxKEqu8JcUOgK0RcUQJ1fxGZ/5SXuiX7lkC4PdaXnHy/M0Jaq/toXAyH7/EMzNv8FS+qXlqabEbvmdBR8vMElnaGyyaquEVRV+931hEwwpH4VW5+IQXYEnilDNpg+HAFGvA32vZ/JawKBgQDBkqaFR3Z+D2Ir7SUkScW6rXrJjuNLkqn3+dlFKGcmrtBw7MHE8w47NwxX/IgaizjznTqEvGjb+yUlodYTgdUhcfDt3v5UdN5TrS5NbAaeSzd/whb8P1q2q64KvUxrfPQMHRGqq8WriboiJkyHumrnLnk32oFOdhDc9YMW8fdPNwKBgDr1JSjPe6w7CG0t8OOKBNt9oGOQ0m5l5XsPEZxd5C3IqbXfqQKOmmUVKCJbywfvc1soBNnRlScxUbfS4Tyc7/jLvHHQ2iptO/K7MklHRw7lNubWgGbymLU9tc+PysMakWoscyQt+pt9WDBr97M9+sXwmtG3WZZSbguhkNQC0kmNAoGBAL8gkqTEQ94CSHeAxoyG/uHsinFbUVf8LzC2lG2xj+/k6dCmJEjXiDS1jM2Pfb9fudLoarBG6+5QkoQOwDYwmoVUYIQuCsbPQfoy46X0RLClKd5OgK19zI7TApx9/mefT8RqK1huFFXkPlGbqTLWwNkUzQF2EPisoMfa7h2IuhyPAoGBAMIuF11BaKt+Nke1Q+F+fF0DEVr79e6LPNEnWifI4GIIAVsOm9rDjq15WR3HCsJzFI7WERa+BOtrhrM8fUdd6LE9w9mB3UDnNfApjy/h/8goPtKXrJRTwzCUkt5h/blrQe1AI0CJspr7MZ3FcmdMIfsEXuaadEeSWAwi+eHVQoI5";

    // ── 발급 ──
    /// <summary>라이선스 키 발급(개인키 서명) + 발급 명부 자동 적재. 수령자에게 전달할 키 문자열 반환.</summary>
    public static string Issue(string licensee, DateTime? expiry)
    {
        using var rsa = LoadPrivate();
        var p = new Payload
        {
            n = (licensee ?? "").Trim(),
            iat = DateTime.Now.ToString("yyyyMMdd"),
            exp = expiry?.ToString("yyyyMMdd") ?? "",
            id = Guid.NewGuid().ToString("N").Substring(0, 8),
        };
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(p);
        var sig = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var key = ToB64Url(payloadBytes) + "." + ToB64Url(sig);
        AppendRegistry(new LicenseRecord(p.id ?? "", p.n ?? "", key, p.iat ?? "", p.exp ?? ""));
        return key;
    }

    /// <summary>배포본 LicenseService.cs 의 PublicKey 상수에 넣을 공개키(Base64). 수령자 PC 검증에 필요.</summary>
    public static string PublicKeyBase64()
    {
        using var rsa = LoadPrivate();
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>이 PC에서 발급한 라이선스 목록(최신순) — 앱 관리자 화면과 동일 데이터.</summary>
    public static List<LicenseRecord> ListIssued()
    {
        try
        {
            if (!File.Exists(RegistryPath)) return new();
            var list = JsonSerializer.Deserialize<List<LicenseRecord>>(File.ReadAllText(RegistryPath)) ?? new();
            list.Reverse();
            return list;
        }
        catch { return new(); }
    }

    /// <summary>발급 명부에서 1건 삭제(장부 정리 · 이미 배포된 키의 원격 무효화는 아님).</summary>
    public static bool RemoveIssued(string id)
    {
        try
        {
            if (!File.Exists(RegistryPath)) return false;
            var list = JsonSerializer.Deserialize<List<LicenseRecord>>(File.ReadAllText(RegistryPath)) ?? new();
            int before = list.Count;
            list.RemoveAll(r => r.Id == id);
            if (list.Count == before) return false;
            WriteRegistry(list);
            return true;
        }
        catch { return false; }
    }

    /// <summary>yyyyMMdd → yyyy-MM-dd 표시 변환.</summary>
    public static string FormatYmd(string? ymd)
        => (ymd is not null && ymd.Length == 8) ? $"{ymd[..4]}-{ymd.Substring(4, 2)}-{ymd.Substring(6, 2)}" : (ymd ?? "");

    // ── 내부: 서명키 = 코드 내장(파일/복호화 없음) ──
    private static RSA LoadPrivate()
    {
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(EmbeddedPrivateKeyPkcs8), out _);
        return rsa;
    }

    private static void AppendRegistry(LicenseRecord rec)
    {
        var list = File.Exists(RegistryPath)
            ? (JsonSerializer.Deserialize<List<LicenseRecord>>(File.ReadAllText(RegistryPath)) ?? new())
            : new List<LicenseRecord>();
        list.Add(rec);
        WriteRegistry(list);
    }

    private static void WriteRegistry(List<LicenseRecord> list)
    {
        var dir = Path.GetDirectoryName(RegistryPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = RegistryPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        File.Copy(tmp, RegistryPath, overwrite: true);
        try { File.Delete(tmp); } catch { /* 임시파일 삭제 실패 무시 */ }
    }

    private static string ToB64Url(byte[] b)
        => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // 앱 LicenseService.Payload 와 키 이름(n/iat/exp/id) 동일 → 앱이 그대로 역직렬화.
    private sealed class Payload
    {
        public string? n { get; set; }     // licensee
        public string? iat { get; set; }   // issued yyyyMMdd
        public string? exp { get; set; }   // expiry yyyyMMdd ("" = 무기한)
        public string? id { get; set; }    // nonce
    }
}
