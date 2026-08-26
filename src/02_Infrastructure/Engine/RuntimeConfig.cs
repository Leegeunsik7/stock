// ============================================================================
// RuntimeConfig — 설정 격리·런타임 보안 파사드 (외부 의존 0 · fail-safe)
//
//  원시 설정 접근(환경변수·DB·파일)을 검증·바운드·안전기본값 파사드 뒤로 격리한다.
//  잘못되거나 누락된 설정이 조용히 안전을 깨뜨리지 못하도록 모든 접근이 fail-safe.
//
//  ── 격리 (Isolation) ───────────────────────────────────────────────────────
//  코어는 키→원시값 provider(Func)만 안다. 호스트가 precedence(환경변수 → DB → 폴백)를
//  배선하고, 테스트는 딕셔너리를 주입한다 → 결정론·I/O 0.
//
//  ── 런타임 보안 ────────────────────────────────────────────────────────────
//  · Redact: 비밀을 로깅 시 마스킹(앞2·뒤2만 노출, 중간 차폐) — 평문 유출 방지.
//  · 안전 불변식: 라이브 거래는 config 로 켤 수 없다(LiveTradingConfigurable=false).
//    liveArmed 는 코드 게이트 전용 — 설정 격리가 거래 방아쇠로 새지 않는다.
// ============================================================================
using System;
using System.Globalization;

namespace MS2E.Infrastructure
{
    public sealed class RuntimeConfig
    {
        private readonly Func<string, string?> _raw;   // 키 → 원시값(부재 시 null)

        public RuntimeConfig(Func<string, string?> rawProvider)
            => _raw = rawProvider ?? throw new ArgumentNullException(nameof(rawProvider));

        // ── 타입·검증·바운드 접근 (fail-safe) ──────────────────────────────────

        /// <summary>불리언. true/1/yes/on, false/0/no/off(대소문자 무관). 그 외·누락 → fallback.</summary>
        public bool GetBool(string key, bool fallback)
        {
            var v = Read(key);
            if (v is null) return fallback;
            return v.ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "on"  => true,
                "false" or "0" or "no" or "off" => false,
                _                               => fallback,
            };
        }

        /// <summary>정수. 파싱 실패·누락 → fallback. 항상 [min,max]로 클램프.</summary>
        public int GetInt(string key, int fallback, int min = int.MinValue, int max = int.MaxValue)
        {
            var v = Read(key);
            int n = int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : fallback;
            return Math.Clamp(n, min, max);
        }

        /// <summary>실수. 파싱 실패·누락·비유한(NaN/Inf) → fallback. 항상 [min,max]로 클램프.</summary>
        public double GetDouble(string key, double fallback, double min = double.MinValue, double max = double.MaxValue)
        {
            var v = Read(key);
            double n = double.TryParse(v, NumberStyles.Float | NumberStyles.AllowThousands,
                                       CultureInfo.InvariantCulture, out var p) && double.IsFinite(p)
                       ? p : fallback;
            return Math.Clamp(n, min, max);
        }

        /// <summary>문자열. 누락·공백 → fallback. 그 외 trim.</summary>
        public string GetString(string key, string fallback)
        {
            var v = Read(key);
            return v ?? fallback;
        }

        // ── 런타임 보안 ─────────────────────────────────────────────────────────

        /// <summary>비밀을 로깅 안전하게 마스킹. 앞2·뒤2만 노출, 중간 차폐(평문 유출 방지).</summary>
        public static string Redact(string? secret)
        {
            if (string.IsNullOrEmpty(secret)) return "(none)";
            if (secret.Length <= 4)           return "****";
            int mid = Math.Min(8, secret.Length - 4);
            return secret[..2] + new string('*', mid) + secret[^2..];
        }

        /// <summary>
        /// 안전 불변식 — 라이브 거래는 config 로 켤 수 없다(항상 false).
        /// liveArmed 는 코드 게이트 전용. 설정 격리 계층이 거래 방아쇠로 새지 않음을 보증한다.
        /// </summary>
        public bool LiveTradingConfigurable => false;

        // ── 내부 ──────────────────────────────────────────────────────────────

        // 원시값 읽기 — 예외는 흡수(provider 장애가 안전을 깨지 않도록), 공백은 null 로 정규화.
        private string? Read(string key)
        {
            string? v;
            try { v = _raw(key); } catch { return null; }
            if (string.IsNullOrWhiteSpace(v)) return null;
            return v.Trim();
        }
    }
}
