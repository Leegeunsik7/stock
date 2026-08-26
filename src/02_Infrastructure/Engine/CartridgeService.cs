// ============================================================================
// CartridgeService - 전략 카트리지 어댑터 (Season 7 #2) · IO·DB·신원키 (런타임)
//  순수 코어(CartridgeEngine 서명/증명서)를 파일·DB·신원키와 연결.
//   · 신원키: 최초 1회 ECDsa 키쌍 생성 — 공개키는 평문, 개인키는 DPAPI(SetSecret) 보관.
//   · 교환: cartridges/ 폴더의 .ms2cart(JSON) 파일. 서명+증명서로 무신뢰 검증.
//   · 적용: 카트리지의 커맨드라인 매크로를 Settings(Cartridge.Macros)에 병합 → Ctrl+K가 해석.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace StockManager.Common
{
    public static class CartridgeService
    {
        public static string Dir => Path.Combine(Path.GetDirectoryName(LocalDatabase.DefaultPath)!, "cartridges");

        // ── 신원키 (ECDsa, 개인키 DPAPI) ──
        public static string EnsureIdentity(LocalDatabase db)
        {
            var pub = db.GetSetting("Cartridge.PubKey");
            if (string.IsNullOrWhiteSpace(pub))
            {
                var (priv, pubNew) = CartridgeCrypto.GenerateKeyPair();
                db.SetSecret("Cartridge.PrivKey", priv);   // DPAPI at-rest
                db.SetSetting("Cartridge.PubKey", pubNew);
                pub = pubNew;
            }
            return pub!;
        }
        public static string Author(LocalDatabase db) => db.GetSetting("Cartridge.Author") is { Length: > 0 } a ? a : "me";

        // ── 매크로 (Settings: alias→command) ──
        public static List<CartMacro> Macros(LocalDatabase db)
        {
            try
            {
                var j = db.GetSetting("Cartridge.Macros");
                if (!string.IsNullOrWhiteSpace(j)) return JsonSerializer.Deserialize<List<CartMacro>>(j) ?? new();
            }
            catch { }
            return new();
        }
        public static Dictionary<string, string> MacroMap(LocalDatabase db)
            => Macros(db).GroupBy(m => m.Alias, StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(g => g.Key, g => g.Last().Command, StringComparer.OrdinalIgnoreCase);

        public static void ApplyMacros(LocalDatabase db, IEnumerable<CartMacro> incoming)
        {
            var map = Macros(db).ToDictionary(m => m.Alias, m => m.Command, StringComparer.OrdinalIgnoreCase);
            foreach (var m in incoming) if (!string.IsNullOrWhiteSpace(m.Alias)) map[m.Alias] = m.Command;
            db.SetSetting("Cartridge.Macros", JsonSerializer.Serialize(map.Select(kv => new CartMacro(kv.Key, kv.Value)).ToList()));
        }

        // ── 빌드 + 서명 ──
        public static Cartridge BuildSigned(LocalDatabase db, string name, string desc, List<CartStrategy> strategies, List<CartMacro>? macros = null, List<CartPrompt>? prompts = null)
        {
            EnsureIdentity(db);
            var cart = new Cartridge(
                CartridgeEngine.FormatVersion, Guid.NewGuid().ToString("N")[..10],
                string.IsNullOrWhiteSpace(name) ? "전략팩" : name.Trim(),
                Author(db), DateTime.Now.ToString("yyyy-MM-dd"),
                db.GetSetting("Cartridge.PubKey") ?? "", desc ?? "",
                macros ?? Macros(db), strategies ?? new(), prompts ?? new(), "");
            var priv = db.GetSecret("Cartridge.PrivKey") ?? "";
            return CartridgeEngine.Sign(cart, priv);
        }

        // ── 파일 입출력 ──
        public static string ExportToFile(Cartridge cart)
        {
            Directory.CreateDirectory(Dir);
            var name = $"{Sanitize(cart.Name)}-{cart.Id}.ms2cart";
            var path = Path.Combine(Dir, name);
            File.WriteAllText(path, JsonSerializer.Serialize(cart, new JsonSerializerOptions { WriteIndented = true }));
            Log.Info($"[Cartridge] 내보내기: {name}");
            return path;
        }
        public static List<string> ListFiles()
        {
            try { Directory.CreateDirectory(Dir); return Directory.GetFiles(Dir, "*.ms2cart").OrderByDescending(f => f).ToList(); }
            catch { return new(); }
        }
        public static bool TryImport(string path, out Cartridge? cart, out string error)
        {
            cart = null; error = "";
            try
            {
                cart = JsonSerializer.Deserialize<Cartridge>(File.ReadAllText(path));
                if (cart == null) { error = "파싱 실패"; return false; }
                if (cart.FormatVersion != CartridgeEngine.FormatVersion) { error = $"지원하지 않는 포맷 버전 {cart.FormatVersion}"; cart = null; return false; }
                return true;
            }
            catch (Exception ex) { error = "가져오기 실패: " + ex.Message; return false; }
        }

        private static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim().Length == 0 ? "cart" : s.Trim();
        }
    }
}
