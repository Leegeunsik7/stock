// ============================================================================
// ConfigAuditor — 부팅 설정 무결성 감사 (외부 의존 0 · 결정론)
//
//  RuntimeConfig 는 런타임 접근 시 잘못/누락 값을 조용히 안전 폴백·클램프한다.
//  그 "조용함"이 운영자에게는 사각지대다 — 설정이 무시됐는지 알 길이 없다.
//  ConfigAuditor 는 부팅 시점에 출처(provenance)를 드러낸다:
//
//    · Valid     명시값이 존재하고 유효(범위 내)
//    · Defaulted 키 부재/공백 → 폴백 사용 (정상)
//    · Coerced   명시값이 잘못/범위초과 → 안전값으로 강제 (이상! 경보 대상)
//
//  Coerced 가 하나라도 있으면 운영자가 의도한 설정이 무시됐다는 신호다.
//  부팅 원장에 redacted 스냅샷을 남기고, 관제탑에 [⚠️ 설정 복구] 경보를 점등한다.
//
//  ── 격리 ────────────────────────────────────────────────────────────────────
//  코어는 키→원시값 provider(Func)만 안다. RuntimeConfig 와 동일 provider 를 공유해
//  감사 결과와 런타임 실효값이 일관되도록 호스트가 배선한다.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MS2E.Infrastructure
{
    public enum ConfigKind { Bool, Int, Double, Text, Secret }

    /// <summary>감사 대상 설정 1건의 선언 — 키·타입·폴백(문자열)·바운드·표시 라벨.</summary>
    public sealed record ConfigSpec(
        string     Key,
        ConfigKind Kind,
        string     Fallback,
        double     Min   = double.MinValue,
        double     Max   = double.MaxValue,
        string?    Label = null);

    /// <summary>설정 출처 분류.</summary>
    public enum ConfigStatus { Valid, Defaulted, Coerced }

    /// <summary>설정 1건 감사 결과 — Display 는 로깅 안전(비밀은 마스킹됨).</summary>
    public sealed record ConfigAuditEntry(
        string       Key,
        string       Label,
        ConfigStatus Status,
        string       Display,
        string       Detail);

    /// <summary>전체 감사 리포트 — 이상(Coerced) 집계·요약.</summary>
    public sealed record ConfigAuditReport(IReadOnlyList<ConfigAuditEntry> Entries)
    {
        public int  CoercedCount => Entries.Count(e => e.Status == ConfigStatus.Coerced);
        public bool HasAnomaly   => CoercedCount > 0;
        public IEnumerable<ConfigAuditEntry> Coerced => Entries.Where(e => e.Status == ConfigStatus.Coerced);

        /// <summary>원장용 1줄 요약 — Valid 무표시, Defaulted '°', Coerced '⚠'.</summary>
        public string Summary()
            => Entries.Count == 0 ? "(설정 없음)" : string.Join(" · ", Entries.Select(Format));

        private static string Format(ConfigAuditEntry e) => e.Status switch
        {
            ConfigStatus.Coerced   => $"{e.Label}={e.Display}⚠",
            ConfigStatus.Defaulted => $"{e.Label}={e.Display}°",
            _                      => $"{e.Label}={e.Display}",
        };
    }

    public sealed class ConfigAuditor
    {
        private readonly Func<string, string?> _raw;

        public ConfigAuditor(Func<string, string?> rawProvider)
            => _raw = rawProvider ?? throw new ArgumentNullException(nameof(rawProvider));

        public ConfigAuditReport Audit(IEnumerable<ConfigSpec>? specs)
        {
            var list = new List<ConfigAuditEntry>();
            foreach (var s in specs ?? Enumerable.Empty<ConfigSpec>())
                list.Add(AuditOne(s));
            return new ConfigAuditReport(list);
        }

        private ConfigAuditEntry AuditOne(ConfigSpec s)
        {
            string label = string.IsNullOrWhiteSpace(s.Label) ? s.Key : s.Label!;

            string? raw;
            try { raw = _raw(s.Key); } catch { raw = null; }   // provider 장애 흡수

            // 키 부재/공백 → 폴백 (정상, 이상 아님)
            if (string.IsNullOrWhiteSpace(raw))
                return new ConfigAuditEntry(s.Key, label, ConfigStatus.Defaulted,
                    DisplayFallback(s), "키 부재 — 폴백 사용");

            raw = raw.Trim();

            switch (s.Kind)
            {
                case ConfigKind.Bool:
                {
                    string t = raw.ToLowerInvariant();
                    bool truthy = t is "true" or "1" or "yes" or "on";
                    bool falsy  = t is "false" or "0" or "no" or "off";
                    if (!truthy && !falsy)
                        return Coerced(s, label, DisplayFallback(s), $"불리언 파싱 실패 '{raw}' → 폴백");
                    return Valid(s, label, truthy ? "true" : "false");
                }

                case ConfigKind.Int:
                {
                    if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        return Coerced(s, label, DisplayFallback(s), $"정수 파싱 실패 '{raw}' → 폴백");
                    double clamped = Math.Clamp((double)n, s.Min, s.Max);
                    if (clamped != n)
                        return Coerced(s, label, ((long)clamped).ToString(CultureInfo.InvariantCulture),
                            $"범위[{Bound(s.Min)},{Bound(s.Max)}] 밖 {n} → 클램프");
                    return Valid(s, label, n.ToString(CultureInfo.InvariantCulture));
                }

                case ConfigKind.Double:
                {
                    if (!double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands,
                                         CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                        return Coerced(s, label, DisplayFallback(s), $"실수 파싱 실패/비유한 '{raw}' → 폴백");
                    double clamped = Math.Clamp(d, s.Min, s.Max);
                    if (clamped != d)
                        return Coerced(s, label, clamped.ToString("0.###", CultureInfo.InvariantCulture),
                            $"범위 밖 {d.ToString("0.###", CultureInfo.InvariantCulture)} → 클램프");
                    return Valid(s, label, d.ToString("0.###", CultureInfo.InvariantCulture));
                }

                case ConfigKind.Secret:
                    return Valid(s, label, RuntimeConfig.Redact(raw), "명시값(마스킹)");

                default: // Text
                    return Valid(s, label, raw);
            }
        }

        private static ConfigAuditEntry Valid(ConfigSpec s, string label, string display, string detail = "명시값")
            => new(s.Key, label, ConfigStatus.Valid, display, detail);

        private static ConfigAuditEntry Coerced(ConfigSpec s, string label, string display, string detail)
            => new(s.Key, label, ConfigStatus.Coerced, display, detail);

        // 폴백의 표시값 — 비밀이면 마스킹
        private static string DisplayFallback(ConfigSpec s)
            => s.Kind == ConfigKind.Secret ? RuntimeConfig.Redact(s.Fallback) : s.Fallback;

        private static string Bound(double v)
            => v <= double.MinValue ? "-∞"
             : v >= double.MaxValue ? "+∞"
             : v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
