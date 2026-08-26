// ============================================================================
// PluginSystem - 동적 플러그인 아키텍처 · 순수 코어 (v3.42.0)
//  외부 개발자가 plugins/ 폴더에 넣은 매니페스트를 런타임에 편입하는 토대.
//  ── 순수(이 파일): 매니페스트 파싱 · 카탈로그 검증/중복제거/버전 · 선언적 평가
//  ── 어댑터(PluginLoader.cs): 폴더 스캔 IO + .dll AssemblyLoadContext 로드(게이팅)
//  설계 원칙: 분석/검증 로직은 순수·결정론적 → 단위 테스트로 고정. IO·코드실행은 가장자리.
//  보안: 어셈블리(.dll) 플러그인은 임의 코드 실행이므로 기본 차단(명시적 허용 필요).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StockManager.Common
{
    public enum PluginKind { Indicator, AlertRule, PromptTemplate, Assembly, Unknown }

    public enum PluginStatus { Ok, Disabled, Blocked, Error }

    /// <summary>외부 .dll 플러그인이 구현하는 진입점(어셈블리 플러그인용).</summary>
    public interface IStockPlugin
    {
        string Id { get; }
        string Name { get; }
        string Version { get; }
        void Initialize(IPluginHost host);
    }

    /// <summary>어셈블리 플러그인이 호스트에 접근하는 최소 표면(샌드박스 경계).</summary>
    public interface IPluginHost
    {
        void Log(string message);
        string? GetSetting(string key);
    }

    public sealed record PluginManifest(
        string Id,
        string Name,
        string Version,
        PluginKind Kind,
        string Author,
        string Description,
        string? Entry,     // assembly: dll 파일/타입 · 선언적: null
        string? Payload,   // prompt=template 텍스트 · indicator/alertRule=spec/rule JSON
        bool Enabled)
    {
        private static readonly Regex IdRe = new("^[a-z0-9][a-z0-9._-]*$", RegexOptions.Compiled);
        private static readonly Regex VerRe = new(@"^\d+\.\d+(\.\d+)?$", RegexOptions.Compiled);

        /// <summary>매니페스트 JSON 1건을 파싱·검증. 실패 시 false + error.</summary>
        public static bool TryParse(string json, out PluginManifest? manifest, out string error)
        {
            manifest = null; error = "";
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (Exception ex) { error = "JSON 파싱 실패: " + ex.Message; return false; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { error = "최상위가 JSON 객체가 아님"; return false; }

                string id = Str(root, "id");
                string name = Str(root, "name");
                string version = Str(root, "version");
                string kindStr = Str(root, "kind");
                string author = Str(root, "author");
                string desc = Str(root, "description");
                bool enabled = !(root.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False);

                if (!IdRe.IsMatch(id)) { error = $"id 형식 오류(소문자/숫자/._- ): '{id}'"; return false; }
                if (string.IsNullOrWhiteSpace(name)) { error = "name 누락"; return false; }
                if (!VerRe.IsMatch(version)) { error = $"version 형식 오류(x.y[.z]): '{version}'"; return false; }

                PluginKind kind = kindStr.Trim().ToLowerInvariant() switch
                {
                    "indicator" => PluginKind.Indicator,
                    "alert" or "alertrule" => PluginKind.AlertRule,
                    "prompt" or "template" or "prompttemplate" => PluginKind.PromptTemplate,
                    "assembly" or "dll" => PluginKind.Assembly,
                    _ => PluginKind.Unknown,
                };
                if (kind == PluginKind.Unknown) { error = $"알 수 없는 kind: '{kindStr}'"; return false; }

                string? entry = null, payload = null;
                switch (kind)
                {
                    case PluginKind.Assembly:
                        entry = Str(root, "entry");
                        if (string.IsNullOrWhiteSpace(entry)) { error = "assembly 플러그인은 entry(dll 경로) 필요"; return false; }
                        break;
                    case PluginKind.PromptTemplate:
                        payload = Str(root, "template");
                        if (string.IsNullOrWhiteSpace(payload)) { error = "prompt 플러그인은 template 필요"; return false; }
                        break;
                    case PluginKind.Indicator:
                        if (!root.TryGetProperty("spec", out var sp) || sp.ValueKind != JsonValueKind.Object) { error = "indicator 플러그인은 spec 객체 필요"; return false; }
                        payload = sp.GetRawText();
                        break;
                    case PluginKind.AlertRule:
                        if (!root.TryGetProperty("rule", out var rl) || rl.ValueKind != JsonValueKind.Object) { error = "alertRule 플러그인은 rule 객체 필요"; return false; }
                        payload = rl.GetRawText();
                        break;
                }

                manifest = new PluginManifest(id, name, version, kind, author, desc, entry, payload, enabled);
                return true;
            }
        }

        private static string Str(JsonElement o, string prop) =>
            o.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
    }

    public sealed record PluginDescriptor(PluginManifest Manifest, string SourceFile, PluginStatus Status, string Detail);

    public sealed record PluginCatalog(IReadOnlyList<PluginDescriptor> Items, IReadOnlyList<string> Errors)
    {
        public IEnumerable<PluginDescriptor> Active => Items.Where(i => i.Status == PluginStatus.Ok);
        public int Count => Items.Count;

        /// <summary>순수 카탈로그 구성: 파싱→검증→중복제거(고버전 채택)→상태 판정.</summary>
        /// <param name="enabledIds">사용자가 켠 id 집합. null이면 매니페스트 enabled 기본값 사용.</param>
        /// <param name="allowAssemblies">어셈블리(.dll) 플러그인 허용 여부(보안 게이트).</param>
        public static PluginCatalog Build(IReadOnlyList<(string file, string json)> raw, ISet<string>? enabledIds, bool allowAssemblies)
        {
            var parsed = new List<(PluginManifest m, string file)>();
            var errors = new List<string>();
            foreach (var (file, json) in raw)
            {
                if (PluginManifest.TryParse(json, out var m, out var err) && m != null) parsed.Add((m, file));
                else errors.Add($"{ShortName(file)}: {err}");
            }

            // id 중복 → 높은 버전 채택
            var byId = new Dictionary<string, (PluginManifest m, string file)>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in parsed)
            {
                if (byId.TryGetValue(p.m.Id, out var ex))
                {
                    if (CompareVersion(p.m.Version, ex.m.Version) > 0)
                    { errors.Add($"중복 id '{p.m.Id}': v{ex.m.Version} 무시 → v{p.m.Version} 채택"); byId[p.m.Id] = p; }
                    else errors.Add($"중복 id '{p.m.Id}': v{p.m.Version} 무시(채택 v{ex.m.Version})");
                }
                else byId[p.m.Id] = p;
            }

            var items = new List<PluginDescriptor>();
            foreach (var (m, file) in byId.Values)
            {
                bool on = enabledIds != null ? enabledIds.Contains(m.Id) : m.Enabled;
                PluginStatus st; string detail;
                if (m.Kind == PluginKind.Assembly)
                {
                    if (!allowAssemblies) { st = PluginStatus.Blocked; detail = "어셈블리 차단 — 설정에서 명시적 허용 필요(보안)"; }
                    else if (!on) { st = PluginStatus.Disabled; detail = "비활성"; }
                    else { st = PluginStatus.Ok; detail = "허용됨(신뢰)"; }
                }
                else
                {
                    st = on ? PluginStatus.Ok : PluginStatus.Disabled;
                    detail = on ? "활성" : "비활성";
                }
                items.Add(new PluginDescriptor(m, file, st, detail));
            }

            items = items.OrderBy(i => i.Manifest.Kind)
                         .ThenBy(i => i.Manifest.Name, StringComparer.OrdinalIgnoreCase)
                         .ToList();
            return new PluginCatalog(items, errors);
        }

        public static int CompareVersion(string a, string b)
        {
            int[] pa = ParseVer(a), pb = ParseVer(b);
            for (int i = 0; i < 3; i++) { int c = pa[i].CompareTo(pb[i]); if (c != 0) return c; }
            return 0;
        }

        private static int[] ParseVer(string v)
        {
            var parts = (v ?? "").Split('.');
            var r = new int[3];
            for (int i = 0; i < 3 && i < parts.Length; i++) int.TryParse(parts[i], out r[i]);
            return r;
        }

        private static string ShortName(string path)
        {
            int i = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            return i >= 0 ? path.Substring(i + 1) : path;
        }
    }

    // ── 선언적 플러그인 평가기(모두 순수) ───────────────────────────────────

    /// <summary>프롬프트 템플릿: {{key}} 치환. 미지정 키는 원형 보존.</summary>
    public static class PromptTemplatePlugin
    {
        public static string Render(string template, IReadOnlyDictionary<string, string> vars)
        {
            if (string.IsNullOrEmpty(template)) return template ?? "";
            var sb = new StringBuilder(template.Length + 32);
            int i = 0;
            while (i < template.Length)
            {
                int open = template.IndexOf("{{", i, StringComparison.Ordinal);
                if (open < 0) { sb.Append(template, i, template.Length - i); break; }
                sb.Append(template, i, open - i);
                int close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
                if (close < 0) { sb.Append(template, open, template.Length - open); break; }
                string key = template.Substring(open + 2, close - (open + 2)).Trim();
                if (vars.TryGetValue(key, out var val)) sb.Append(val);
                else sb.Append("{{").Append(key).Append("}}");
                i = close + 2;
            }
            return sb.ToString();
        }

        public static IReadOnlyList<string> Placeholders(string template)
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(template)) return list;
            int i = 0;
            while (i < template.Length)
            {
                int open = template.IndexOf("{{", i, StringComparison.Ordinal);
                if (open < 0) break;
                int close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
                if (close < 0) break;
                string key = template.Substring(open + 2, close - (open + 2)).Trim();
                if (key.Length > 0 && seen.Add(key)) list.Add(key);
                i = close + 2;
            }
            return list;
        }
    }

    public enum IndicatorSignal { Long, Short, Flat }

    public sealed record IndicatorSpec(string Op, int Fast, int Slow);

    /// <summary>선언적 지표: emaCross / smaCross. 마지막 봉의 현재 스탠스를 반환.</summary>
    public static class DeclarativeIndicator
    {
        private static readonly HashSet<string> Ops = new(StringComparer.OrdinalIgnoreCase) { "emaCross", "smaCross" };

        public static bool TryParseSpec(string json, out IndicatorSpec? spec, out string error)
        {
            spec = null; error = "";
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                string op = r.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? (o.GetString() ?? "") : "";
                int fast = r.TryGetProperty("fast", out var f) && f.TryGetInt32(out var fi) ? fi : 0;
                int slow = r.TryGetProperty("slow", out var s) && s.TryGetInt32(out var si) ? si : 0;
                if (!Ops.Contains(op)) { error = $"지원하지 않는 op: '{op}' (emaCross/smaCross)"; return false; }
                if (fast <= 0 || slow <= 0) { error = "fast/slow는 양수여야 함"; return false; }
                if (fast >= slow) { error = "fast < slow 여야 함"; return false; }
                spec = new IndicatorSpec(op, fast, slow);
                return true;
            }
            catch (Exception ex) { error = "spec 파싱 실패: " + ex.Message; return false; }
        }

        public static IndicatorSignal Evaluate(IndicatorSpec spec, IReadOnlyList<double> closes)
        {
            if (spec == null || closes == null || closes.Count < spec.Slow) return IndicatorSignal.Flat;
            int n = closes.Count - 1;
            double f, s;
            if (string.Equals(spec.Op, "emaCross", StringComparison.OrdinalIgnoreCase))
            { var ef = Ema(closes, spec.Fast); var es = Ema(closes, spec.Slow); f = ef[n]; s = es[n]; }
            else { f = Sma(closes, spec.Fast, n); s = Sma(closes, spec.Slow, n); }
            return f > s ? IndicatorSignal.Long : f < s ? IndicatorSignal.Short : IndicatorSignal.Flat;
        }

        // 시드 규약: 첫 값 = data[0], k = 2/(p+1) — IndicatorMath.Ema와 동일.
        private static double[] Ema(IReadOnlyList<double> d, int p)
        {
            var r = new double[d.Count];
            if (d.Count == 0) return r;
            double k = 2.0 / (p + 1);
            r[0] = d[0];
            for (int i = 1; i < d.Count; i++) r[i] = d[i] * k + r[i - 1] * (1 - k);
            return r;
        }

        private static double Sma(IReadOnlyList<double> d, int p, int idx)
        {
            double sum = 0;
            for (int i = idx - p + 1; i <= idx; i++) sum += d[i];
            return sum / p;
        }
    }

    public sealed record AlertRuleSpec(string Metric, string Op, double Value);

    /// <summary>선언적 알림 규칙: metric op value (예: rsi &lt; 30). 순수 평가.</summary>
    public static class DeclarativeAlertRule
    {
        private static readonly HashSet<string> Metrics = new(StringComparer.OrdinalIgnoreCase) { "price", "rsi", "change", "ma", "volume" };
        private static readonly HashSet<string> Ops = new() { "<", ">", "<=", ">=", "==" };

        public static bool TryParse(string json, out AlertRuleSpec? spec, out string error)
        {
            spec = null; error = "";
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                string metric = r.TryGetProperty("metric", out var m) && m.ValueKind == JsonValueKind.String ? (m.GetString() ?? "") : "";
                string op = r.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? (o.GetString() ?? "") : "";
                double val = r.TryGetProperty("value", out var v) && v.TryGetDouble(out var d) ? d : double.NaN;
                if (!Metrics.Contains(metric)) { error = $"지원하지 않는 metric: '{metric}' (price/rsi/change/ma/volume)"; return false; }
                if (!Ops.Contains(op)) { error = $"지원하지 않는 op: '{op}'"; return false; }
                if (double.IsNaN(val)) { error = "value 누락/비수치"; return false; }
                spec = new AlertRuleSpec(metric.ToLowerInvariant(), op, val);
                return true;
            }
            catch (Exception ex) { error = "rule 파싱 실패: " + ex.Message; return false; }
        }

        public static bool Eval(AlertRuleSpec spec, double value) => spec.Op switch
        {
            "<" => value < spec.Value,
            ">" => value > spec.Value,
            "<=" => value <= spec.Value,
            ">=" => value >= spec.Value,
            "==" => value == spec.Value,
            _ => false,
        };
    }
}
