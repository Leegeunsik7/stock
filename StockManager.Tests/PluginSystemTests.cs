// ============================================================================
// PluginSystemTests - 플러그인 매니페스트/카탈로그/선언적 평가 (순수)
// ============================================================================
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class PluginSystemTests
    {
        private static string Indicator(string id, string ver = "1.0.0", string op = "emaCross", int f = 5, int s = 20) =>
            $@"{{ ""id"":""{id}"", ""name"":""{id} ind"", ""version"":""{ver}"", ""kind"":""indicator"",
                 ""spec"": {{ ""op"":""{op}"", ""fast"":{f}, ""slow"":{s} }} }}";

        private static string Prompt(string id) =>
            $@"{{ ""id"":""{id}"", ""name"":""{id} prompt"", ""version"":""1.0.0"", ""kind"":""prompt"",
                 ""template"":""Analyze {{{{symbol}}}} now"" }}";

        private static string Asm(string id) =>
            $@"{{ ""id"":""{id}"", ""name"":""{id} asm"", ""version"":""1.0.0"", ""kind"":""assembly"", ""entry"":""my.dll"" }}";

        // ── 매니페스트 파싱 ────────────────────────────────────────────────
        [Fact]
        public void Manifest_Valid_Parses()
        {
            Assert.True(PluginManifest.TryParse(Indicator("ema-5-20"), out var m, out _));
            Assert.NotNull(m);
            Assert.Equal("ema-5-20", m!.Id);
            Assert.Equal(PluginKind.Indicator, m.Kind);
            Assert.True(m.Enabled);                 // 기본 true
            Assert.Contains("emaCross", m.Payload);
        }

        [Theory]
        [InlineData("{ not json")]                                                  // 깨진 JSON
        [InlineData(@"{ ""id"":""Bad ID"", ""name"":""x"", ""version"":""1.0"", ""kind"":""prompt"", ""template"":""t"" }")] // id 형식
        [InlineData(@"{ ""id"":""ok"", ""name"":""x"", ""version"":""v1"", ""kind"":""prompt"", ""template"":""t"" }")]      // 버전 형식
        [InlineData(@"{ ""id"":""ok"", ""name"":""x"", ""version"":""1.0"", ""kind"":""weird"", ""template"":""t"" }")]      // 알 수 없는 kind
        [InlineData(@"{ ""id"":""ok"", ""name"":""x"", ""version"":""1.0"", ""kind"":""indicator"" }")]                       // spec 누락
        [InlineData(@"{ ""id"":""ok"", ""name"":""x"", ""version"":""1.0"", ""kind"":""prompt"" }")]                          // template 누락
        public void Manifest_Invalid_Fails(string json)
        {
            Assert.False(PluginManifest.TryParse(json, out var m, out var err));
            Assert.Null(m);
            Assert.NotEqual("", err);
        }

        [Fact]
        public void Manifest_EnabledFalse_Respected()
        {
            var json = @"{ ""id"":""p1"", ""name"":""x"", ""version"":""1.0"", ""kind"":""prompt"", ""template"":""t"", ""enabled"":false }";
            Assert.True(PluginManifest.TryParse(json, out var m, out _));
            Assert.False(m!.Enabled);
        }

        // ── 카탈로그 ──────────────────────────────────────────────────────
        [Fact]
        public void Catalog_Dedup_KeepsHighestVersion()
        {
            var raw = new List<(string, string)>
            {
                ("a.plugin.json", Indicator("dup", "1.0.0")),
                ("b.plugin.json", Indicator("dup", "2.3.0")),
            };
            var cat = PluginCatalog.Build(raw, null, allowAssemblies: false);
            var item = Assert.Single(cat.Items);
            Assert.Equal("2.3.0", item.Manifest.Version);
            Assert.Contains(cat.Errors, e => e.Contains("중복"));
        }

        [Fact]
        public void Catalog_Assembly_BlockedByDefault_AllowedWhenOptedIn()
        {
            var raw = new List<(string, string)> { ("a.plugin.json", Asm("risky")) };

            var blocked = PluginCatalog.Build(raw, null, allowAssemblies: false).Items.Single();
            Assert.Equal(PluginStatus.Blocked, blocked.Status);

            var allowed = PluginCatalog.Build(raw, null, allowAssemblies: true).Items.Single();
            Assert.Equal(PluginStatus.Ok, allowed.Status);          // 허용 + 기본 enabled
        }

        [Fact]
        public void Catalog_EnabledIds_OverrideManifestDefault()
        {
            var raw = new List<(string, string)>
            {
                ("a.plugin.json", Prompt("keep")),
                ("b.plugin.json", Prompt("drop")),
            };
            var enabled = new HashSet<string> { "keep" };
            var cat = PluginCatalog.Build(raw, enabled, allowAssemblies: false);
            Assert.Equal(PluginStatus.Ok, cat.Items.Single(i => i.Manifest.Id == "keep").Status);
            Assert.Equal(PluginStatus.Disabled, cat.Items.Single(i => i.Manifest.Id == "drop").Status);
            Assert.Single(cat.Active);
        }

        [Fact]
        public void Catalog_BadManifest_GoesToErrors_NotItems()
        {
            var raw = new List<(string, string)>
            {
                ("ok.plugin.json", Prompt("good")),
                ("bad.plugin.json", "{ broken"),
            };
            var cat = PluginCatalog.Build(raw, null, false);
            Assert.Single(cat.Items);
            Assert.Contains(cat.Errors, e => e.Contains("bad.plugin.json"));
        }

        [Theory]
        [InlineData("1.0.0", "1.0.1", -1)]
        [InlineData("2.0.0", "1.9.9", 1)]
        [InlineData("1.2", "1.2.0", 0)]
        public void CompareVersion_Cases(string a, string b, int sign)
            => Assert.Equal(sign, System.Math.Sign(PluginCatalog.CompareVersion(a, b)));

        // ── 프롬프트 템플릿 ────────────────────────────────────────────────
        [Fact]
        public void PromptTemplate_Render_Substitutes_And_PreservesUnknown()
        {
            var vars = new Dictionary<string, string> { ["symbol"] = "삼성전자", ["rsi"] = "28" };
            var outp = PromptTemplatePlugin.Render("{{symbol}} RSI={{rsi}} regime={{regime}}", vars);
            Assert.Equal("삼성전자 RSI=28 regime={{regime}}", outp);
        }

        [Fact]
        public void PromptTemplate_Placeholders_DistinctInOrder()
        {
            var ph = PromptTemplatePlugin.Placeholders("{{a}}-{{b}}-{{a}}-{{ c }}");
            Assert.Equal(new[] { "a", "b", "c" }, ph.ToArray());
        }

        // ── 선언적 지표 ────────────────────────────────────────────────────
        [Fact]
        public void Indicator_Spec_Parse_Valid_And_Invalid()
        {
            Assert.True(DeclarativeIndicator.TryParseSpec(@"{""op"":""emaCross"",""fast"":5,""slow"":20}", out var s, out _));
            Assert.Equal(5, s!.Fast);
            Assert.False(DeclarativeIndicator.TryParseSpec(@"{""op"":""emaCross"",""fast"":20,""slow"":5}", out _, out var e1)); // fast>=slow
            Assert.NotEqual("", e1);
            Assert.False(DeclarativeIndicator.TryParseSpec(@"{""op"":""bogus"",""fast"":5,""slow"":20}", out _, out _));        // op
        }

        [Fact]
        public void Indicator_Evaluate_Stance_And_InsufficientData()
        {
            var spec = new IndicatorSpec("smaCross", 2, 4);
            // 상승 추세 → 단기 SMA > 장기 SMA → Long
            var up = new double[] { 1, 2, 3, 4, 5, 6 };
            Assert.Equal(IndicatorSignal.Long, DeclarativeIndicator.Evaluate(spec, up));
            // 하락 추세 → Short
            var down = new double[] { 6, 5, 4, 3, 2, 1 };
            Assert.Equal(IndicatorSignal.Short, DeclarativeIndicator.Evaluate(spec, down));
            // 데이터 부족(< slow) → Flat
            Assert.Equal(IndicatorSignal.Flat, DeclarativeIndicator.Evaluate(spec, new double[] { 1, 2 }));
        }

        // ── 선언적 알림 규칙 ───────────────────────────────────────────────
        [Fact]
        public void AlertRule_Parse_And_Eval()
        {
            Assert.True(DeclarativeAlertRule.TryParse(@"{""metric"":""rsi"",""op"":""<"",""value"":30}", out var spec, out _));
            Assert.True(DeclarativeAlertRule.Eval(spec!, 28));     // 28 < 30
            Assert.False(DeclarativeAlertRule.Eval(spec!, 35));    // 35 !< 30
        }

        [Theory]
        [InlineData(@"{""metric"":""bogus"",""op"":""<"",""value"":30}")]   // metric
        [InlineData(@"{""metric"":""rsi"",""op"":""~"",""value"":30}")]     // op
        [InlineData(@"{""metric"":""rsi"",""op"":""<""}")]                  // value 누락
        public void AlertRule_Invalid_Fails(string json)
            => Assert.False(DeclarativeAlertRule.TryParse(json, out _, out _));
    }
}
