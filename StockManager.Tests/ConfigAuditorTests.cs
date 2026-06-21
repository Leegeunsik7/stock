using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class ConfigAuditorTests
    {
        private static ConfigAuditor Auditor(params (string k, string? v)[] pairs)
        {
            var d = new Dictionary<string, string?>();
            foreach (var (k, v) in pairs) d[k] = v;
            return new ConfigAuditor(key => d.TryGetValue(key, out var x) ? x : null);
        }

        private static readonly ConfigSpec IntSpec =
            new("N", ConfigKind.Int, "10", 0, 100, "수량");

        // ════════════════════════ §1 Defaulted (정상) ════════════════════════

        [Fact]
        public void MissingKey_IsDefaulted_NotAnomaly()
        {
            var e = Auditor().Audit(new[] { IntSpec }).Entries.Single();
            Assert.Equal(ConfigStatus.Defaulted, e.Status);
            Assert.Equal("10", e.Display);       // 폴백 표시
        }

        [Theory]
        [InlineData("")] [InlineData("   ")]
        public void BlankValue_IsDefaulted(string raw)
            => Assert.Equal(ConfigStatus.Defaulted,
                Auditor(("N", raw)).Audit(new[] { IntSpec }).Entries.Single().Status);

        // ════════════════════════ §2 Int ═════════════════════════════════════

        [Fact]
        public void Int_InRange_IsValid()
        {
            var e = Auditor(("N", "42")).Audit(new[] { IntSpec }).Entries.Single();
            Assert.Equal(ConfigStatus.Valid, e.Status);
            Assert.Equal("42", e.Display);
        }

        [Fact]
        public void Int_OutOfRange_IsCoerced_ToClampedValue()
        {
            var e = Auditor(("N", "9999")).Audit(new[] { IntSpec }).Entries.Single();
            Assert.Equal(ConfigStatus.Coerced, e.Status);
            Assert.Equal("100", e.Display);      // max 로 클램프
            Assert.Contains("클램프", e.Detail);
        }

        [Fact]
        public void Int_BelowMin_IsCoerced()
        {
            var e = Auditor(("N", "-5")).Audit(new[] { IntSpec }).Entries.Single();
            Assert.Equal(ConfigStatus.Coerced, e.Status);
            Assert.Equal("0", e.Display);
        }

        [Theory]
        [InlineData("abc")] [InlineData("4.2")]
        public void Int_ParseFail_IsCoerced_ToFallback(string raw)
        {
            var e = Auditor(("N", raw)).Audit(new[] { IntSpec }).Entries.Single();
            Assert.Equal(ConfigStatus.Coerced, e.Status);
            Assert.Equal("10", e.Display);       // 폴백
            Assert.Contains("파싱 실패", e.Detail);
        }

        // ════════════════════════ §3 Double ══════════════════════════════════

        [Fact]
        public void Double_InRange_IsValid()
        {
            var spec = new ConfigSpec("D", ConfigKind.Double, "1.0", 0, 10);
            var e = Auditor(("D", "2.5")).Audit(new[] { spec }).Entries.Single();
            Assert.Equal(ConfigStatus.Valid, e.Status);
        }

        [Theory]
        [InlineData("NaN")] [InlineData("Infinity")] [InlineData("zzz")]
        public void Double_NonFiniteOrBad_IsCoerced(string raw)
        {
            var spec = new ConfigSpec("D", ConfigKind.Double, "1.5", -100, 100);
            var e = Auditor(("D", raw)).Audit(new[] { spec }).Entries.Single();
            Assert.Equal(ConfigStatus.Coerced, e.Status);
            Assert.Equal("1.5", e.Display);
        }

        // ════════════════════════ §4 Bool ════════════════════════════════════

        [Fact]
        public void Bool_Valid_NormalizesDisplay()
        {
            var spec = new ConfigSpec("B", ConfigKind.Bool, "false");
            Assert.Equal("true", Auditor(("B", "YES")).Audit(new[] { spec }).Entries.Single().Display);
        }

        [Fact]
        public void Bool_Garbage_IsCoerced()
        {
            var spec = new ConfigSpec("B", ConfigKind.Bool, "false");
            Assert.Equal(ConfigStatus.Coerced, Auditor(("B", "maybe")).Audit(new[] { spec }).Entries.Single().Status);
        }

        // ════════════════════════ §5 Secret (마스킹) ═════════════════════════

        [Fact]
        public void Secret_Value_IsRedactedInDisplay()
        {
            var spec = new ConfigSpec("API", ConfigKind.Secret, "");
            var e = Auditor(("API", "SK-LIVE-abcdefghijklmnop")).Audit(new[] { spec }).Entries.Single();
            Assert.Equal(ConfigStatus.Valid, e.Status);
            Assert.DoesNotContain("LIVE", e.Display);      // 평문 비노출
            Assert.Contains("*", e.Display);
        }

        // ════════════════════════ §6 Text ════════════════════════════════════

        [Fact]
        public void Text_Value_PassesThrough()
        {
            var spec = new ConfigSpec("T", ConfigKind.Text, "fb");
            var e = Auditor(("T", "hello")).Audit(new[] { spec }).Entries.Single();
            Assert.Equal(ConfigStatus.Valid, e.Status);
            Assert.Equal("hello", e.Display);
        }

        // ════════════════════════ §7 Report 집계 ═════════════════════════════

        [Fact]
        public void Report_AggregatesAnomalies()
        {
            var specs = new[]
            {
                new ConfigSpec("A", ConfigKind.Int, "10", 0, 100),   // valid
                new ConfigSpec("B", ConfigKind.Int, "10", 0, 100),   // coerced (range)
                new ConfigSpec("C", ConfigKind.Int, "10", 0, 100),   // defaulted
            };
            var r = Auditor(("A", "50"), ("B", "9999")).Audit(specs);

            Assert.True(r.HasAnomaly);
            Assert.Equal(1, r.CoercedCount);
            Assert.Equal("B", r.Coerced.Single().Key);
            Assert.Equal(3, r.Entries.Count);
        }

        [Fact]
        public void Report_AllValid_NoAnomaly()
        {
            var r = Auditor(("N", "42")).Audit(new[] { IntSpec });
            Assert.False(r.HasAnomaly);
            Assert.Equal(0, r.CoercedCount);
        }

        [Fact]
        public void Summary_MarksCoercedAndDefaulted()
        {
            var specs = new[]
            {
                new ConfigSpec("A", ConfigKind.Int, "10", 0, 100, "에이"),
                new ConfigSpec("B", ConfigKind.Int, "10", 0, 100, "비"),
            };
            var sum = Auditor(("A", "9999")).Audit(specs).Summary();
            Assert.Contains("에이=100⚠", sum);   // coerced 마커
            Assert.Contains("비=10°", sum);       // defaulted 마커
        }

        [Fact]
        public void EmptyReport_Summary_IsSafe()
            => Assert.Equal("(설정 없음)", Auditor().Audit(Array.Empty<ConfigSpec>()).Summary());

        [Fact]
        public void NullSpecs_YieldsEmptyReport()
            => Assert.Empty(Auditor().Audit(null).Entries);

        // ════════════════════════ §8 견고성 ══════════════════════════════════

        [Fact]
        public void NullProvider_Throws()
            => Assert.Throws<ArgumentNullException>(() => new ConfigAuditor(null!));

        [Fact]
        public void ThrowingProvider_IsAbsorbed_AsDefaulted()
        {
            var auditor = new ConfigAuditor(_ => throw new InvalidOperationException("boom"));
            var e = auditor.Audit(new[] { IntSpec }).Entries.Single();
            Assert.Equal(ConfigStatus.Defaulted, e.Status);   // 장애 흡수 → 폴백
        }

        // ════════════════════════ §9 BootConfigSpecs 정합 ════════════════════

        [Fact]
        public void BootConfigSpecs_DefaultsAreWithinTheirOwnBounds()
        {
            // 폴백 자체가 바운드 밖이면, 부재 시에도 즉시 Coerced 가 되는 자가당착.
            // 클린 환경(설정 부재)에서 모든 부팅 스펙은 Defaulted(이상 0)여야 한다.
            var clean = new ConfigAuditor(_ => null).Audit(BootConfigSpecs.All);
            Assert.False(clean.HasAnomaly);
            Assert.All(clean.Entries, e => Assert.Equal(ConfigStatus.Defaulted, e.Status));
            Assert.Equal(4, clean.Entries.Count);
        }

        [Fact]
        public void BootConfigSpecs_IncludesLanPort_AndAuditCatchesBadPort()
        {
            // PathA — LAN 포트가 SSOT 에 편입되어 부팅 감사가 자동 포함하는지 + 잘못된 포트를 잡는지.
            var lan = BootConfigSpecs.All.Single(s => s.Key == BootConfigSpecs.LanPortKey);
            Assert.Equal(ConfigKind.Int, lan.Kind);
            Assert.Equal("8787", lan.Fallback);

            var e = new ConfigAuditor(_ => "70000").Audit(new[] { lan }).Entries.Single();
            Assert.Equal(ConfigStatus.Coerced, e.Status);   // 범위 밖 → 클램프
            Assert.Equal("65535", e.Display);
        }
    }
}
