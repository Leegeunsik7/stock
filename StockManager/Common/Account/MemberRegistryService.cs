using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace StockManager.Common.Account;

/// <summary>회원 목록 표시용 뷰(비밀번호 제외).</summary>
public sealed record MemberView(string Id, string Name, string Email, string Phone, string Role, string Status,
    DateTime CreatedAt, string Birth, string LicenseKey, string Gender, string Company, string MemberId);

/// <summary>로그인/가입 결과.</summary>
public sealed record MemberAuthResult(bool Ok, string Message, string? Role);

/// <summary>정밀 5단계 회원가입 요청(약관·인적정보·계정·프로필/주소). Platform=관심 시장 · Facility=투자 스타일.</summary>
public sealed record MemberRegisterRequest(
    string Id, string Password, string Name, string Birth, string Gender,
    string Email, string Phone, string LicenseKey, string MemberId,
    string Company, string Zip, string Address, string AddrDetail, string Platform, string Facility);

/// <summary>아이디/비밀번호 찾기 결과.</summary>
public sealed record MemberFindResult(bool Ok, string Message, string? MaskedId, string? TempPassword);

/// <summary>
/// [MS2E StockSuite] 회원 계정 관리(Identity Engine, 싱글턴) — PBKDF2(SHA-256·10만 회) 안전 해싱 + <b>파일 영구 저장</b>
///   (%LocalAppData%\StockManager\members.json). 정밀 5단계 회원가입(라이선스 키·생년월일·성별·프로필/주소 포함)·로그인·
///   아이디/비밀번호 찾기·관리자 회원 관리(권한/승인/잠금/리셋 + 라이선스 재발급). 발급자 관리자(GSLEE) 시드.
///   외부망 없이 동작(BCL만). 모니터링/스마트팜/PLAI/UAV와 동일 설계 · 기존 트레이딩 권한 게이트(AdminAuthoritySealGuard)와 별개.
/// </summary>
public sealed class MemberRegistryService
{
    private sealed class MemberEntity
    {
        public string Id { get; set; } = "";
        public string Hash { get; set; } = "";
        public string Salt { get; set; } = "";
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Role { get; set; } = "Operator";
        public string Status { get; set; } = "active";
        public string Birth { get; set; } = "";
        public string LicenseKey { get; set; } = "";
        public string Gender { get; set; } = "";
        public string Company { get; set; } = "";
        public string Zip { get; set; } = "";
        public string Address { get; set; } = "";
        public string AddrDetail { get; set; } = "";
        public string Platform { get; set; } = "";
        public string Facility { get; set; } = "";
        public string MemberId { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    private readonly List<MemberEntity> _members = new();
    private readonly string _path;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    public event Action? Changed;

    public MemberRegistryService()
    {
        _path = Path.Combine(LicenseService.DataAreaRoot(), "members.json");
        Load();
        // 발급자(관리자) 계정 GSLEE 보장 — 최초 1회 시드 (초기 비밀번호 1234567890).
        if (Get("GSLEE") is null)
        {
            Seed("GSLEE", "1234567890", "이근식", "gslee@angtec.com", "010-0000-0000", "Admin", "active");
            Save();
        }
    }

    private void Seed(string id, string pw, string name, string email, string phone, string role, string status)
    {
        var (h, s) = HashPassword(pw);
        _members.Add(new MemberEntity { Id = id, Hash = h, Salt = s, Name = name, Email = email, Phone = phone, Role = role, Status = status, CreatedAt = new DateTime(2026, 1, 1) });
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var list = JsonSerializer.Deserialize<List<MemberEntity>>(File.ReadAllText(_path));
                if (list is not null) { _members.Clear(); _members.AddRange(list); }
            }
        }
        catch { /* 손상 시 빈 상태로 시작(아래 관리자 시드) */ }
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_members, Opts));
                File.Copy(tmp, _path, overwrite: true);
                try { File.Delete(tmp); } catch { /* 임시파일 삭제 실패 무시 */ }
            }
            catch { /* 저장 실패는 앱 동작을 막지 않음 */ }
        }
    }

    public IReadOnlyList<MemberView> Members =>
        _members.Select(u => new MemberView(u.Id, u.Name, u.Email, u.Phone, u.Role, u.Status, u.CreatedAt, u.Birth, u.LicenseKey, u.Gender, u.Company, u.MemberId)).ToList();
    public int Count => _members.Count;
    public int PendingCount => _members.Count(u => u.Status == "pending");

    /// <summary>아이디 존재(중복) 여부 — 가입 마법사 '중복 확인'.</summary>
    public bool Exists(string id) => Get(id) is not null;

    /// <summary>정밀 5단계 회원가입 — 인적정보·성별·프로필/주소·라이선스·회원식별번호 전체 저장. 즉시 활성, 기본 Viewer.</summary>
    public MemberAuthResult RegisterFull(MemberRegisterRequest r)
    {
        var id = (r.Id ?? "").Trim();
        if (id.Length < 3 || (r.Password ?? "").Length < 4) return new MemberAuthResult(false, "아이디 3자·비밀번호 4자 이상 입력하세요.", null);
        if (_members.Any(u => u.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) return new MemberAuthResult(false, "이미 존재하는 아이디입니다.", null);
        var (h, s) = HashPassword(r.Password!);
        _members.Add(new MemberEntity
        {
            Id = id, Hash = h, Salt = s, Name = (r.Name ?? "").Trim(), Email = (r.Email ?? "").Trim(),
            Phone = (r.Phone ?? "").Trim(), Role = "Viewer", Status = "active",
            Birth = (r.Birth ?? "").Trim(), LicenseKey = (r.LicenseKey ?? "").Trim(),
            Gender = (r.Gender ?? "").Trim(), Company = (r.Company ?? "").Trim(),
            Zip = (r.Zip ?? "").Trim(), Address = (r.Address ?? "").Trim(), AddrDetail = (r.AddrDetail ?? "").Trim(),
            Platform = (r.Platform ?? "").Trim(), Facility = (r.Facility ?? "").Trim(),
            MemberId = (r.MemberId ?? "").Trim(), CreatedAt = DateTime.Now,
        });
        Save();
        Changed?.Invoke();
        return new MemberAuthResult(true, $"'{id}' 가입 완료 — 바로 로그인할 수 있습니다.", "Viewer");
    }

    /// <summary>[관리자] 회원 라이선스 키 교체 — 성공 시 true.</summary>
    public bool UpdateLicense(string id, string newLicenseKey)
    {
        var u = Get(id);
        if (u is null) return false;
        u.LicenseKey = (newLicenseKey ?? "").Trim();
        Save();
        Changed?.Invoke();
        return true;
    }

    /// <summary>로그인 인증 — 해시 검증 + 상태(active) 확인.</summary>
    public MemberAuthResult Authenticate(string id, string pw)
    {
        var u = _members.FirstOrDefault(x => x.Id.Equals((id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (u is null) return new MemberAuthResult(false, "존재하지 않는 아이디입니다.", null);
        if (u.Status == "locked") return new MemberAuthResult(false, "잠긴 계정입니다. 관리자에게 문의하세요.", null);
        if (u.Status == "pending") return new MemberAuthResult(false, "관리자 승인 대기 중인 계정입니다.", null);
        if (!Verify(pw ?? "", u.Hash, u.Salt)) return new MemberAuthResult(false, "비밀번호가 일치하지 않습니다.", null);
        return new MemberAuthResult(true, $"{u.Name}님 환영합니다.", u.Role);
    }

    /// <summary>아이디 찾기 — 이름+전화 검증 시 마스킹 아이디 안내.</summary>
    public MemberFindResult FindId(string name, string phone)
    {
        var u = Match(name, phone);
        return u is null
            ? new MemberFindResult(false, "이름과 전화번호가 일치하는 계정이 없습니다.", null, null)
            : new MemberFindResult(true, "가입된 아이디를 찾았습니다.", MaskId(u.Id), null);
    }

    /// <summary>비밀번호 재발급 — 이름+전화 검증 시 임시 비밀번호 발급.</summary>
    public MemberFindResult ResetPassword(string name, string phone)
    {
        var u = Match(name, phone);
        if (u is null) return new MemberFindResult(false, "이름과 전화번호가 일치하는 계정이 없습니다.", null, null);
        var temp = TempPassword();
        var (h, s) = HashPassword(temp);
        u.Hash = h; u.Salt = s; u.Status = "active";
        Save();
        Changed?.Invoke();
        return new MemberFindResult(true, $"'{MaskId(u.Id)}' 임시 비밀번호가 발급되었습니다.", MaskId(u.Id), temp);
    }

    // ── 관리자 기능 ──
    public void SetRole(string id, string role) { var u = Get(id); if (u is not null && new[] { "Admin", "Operator", "Viewer" }.Contains(role)) { u.Role = role; Save(); Changed?.Invoke(); } }
    public void ToggleStatus(string id) { var u = Get(id); if (u is null) return; u.Status = u.Status == "active" ? "locked" : "active"; Save(); Changed?.Invoke(); }
    public void Approve(string id) { var u = Get(id); if (u is not null) { u.Status = "active"; Save(); Changed?.Invoke(); } }
    public string ForceReset(string id) { var u = Get(id); if (u is null) return ""; var t = TempPassword(); var (h, s) = HashPassword(t); u.Hash = h; u.Salt = s; Save(); Changed?.Invoke(); return t; }
    public bool Delete(string id) { var u = Get(id); if (u is null || u.Role == "Admin") return false; _members.Remove(u); Save(); Changed?.Invoke(); return true; }

    private MemberEntity? Get(string id) => _members.FirstOrDefault(u => u.Id.Equals((id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
    private MemberEntity? Match(string name, string phone)
    {
        string n = (name ?? "").Trim(), p = Digits(phone);
        return _members.FirstOrDefault(u => u.Name == n && Digits(u.Phone) == p && p.Length >= 4);
    }

    private static string Digits(string? s) => new string((s ?? "").Where(char.IsDigit).ToArray());

    private static string MaskId(string id)
    {
        if (id.Length <= 2) return id[..1] + "*";
        int keepEnd = Math.Min(1, id.Length - 2);
        return id[..2] + new string('*', id.Length - 2 - keepEnd) + id[^keepEnd..];
    }

    private static string TempPassword() => "T" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + "!";

    // ── PBKDF2 해싱(SHA-256 · 100,000회 · 16B salt) ──
    private static (string hash, string salt) HashPassword(string pw)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    private static bool Verify(string pw, string hashB64, string saltB64)
    {
        try
        {
            byte[] salt = Convert.FromBase64String(saltB64);
            byte[] expected = Convert.FromBase64String(hashB64);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 100_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; }
    }
}
