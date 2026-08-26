// ============================================================================
// StrategyParser - 자연어/JSON → StrategySpec (순수·결정론·오프라인) (Season 8 #3)
//  · ParseNl: 외부 의존 0 규칙 기반 한국어/영어 파서. LLM 없이도 흔한 전략 문장을 컴파일.
//  · ParseJson: LLM이 내놓은 '스펙 JSON'을 신뢰하지 않고 C#이 파싱·검증(환각 방화벽).
//  둘 다 StrategyCompiler.Validate로 최종 검증한 뒤에만 유효 스펙을 돌려준다.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MS2E.Infrastructure
{
    public static class StrategyParser
    {
        // ===== 규칙 기반 NL 파서 (오프라인 기본 경로) =====
        public static CompileResult ParseNl(string nl)
        {
            if (string.IsNullOrWhiteSpace(nl)) return Fail("문장을 입력하세요.");
            string s = nl.ToLowerInvariant();
            var entry = new List<SpecCond>();
            var exit = new List<SpecCond>();
            double sl = ExtractPct(s, "손절", "스탑", "stop"), tp = ExtractPct(s, "익절", "목표", "target", "takeprofit", "take profit");

            // ① MA 골든/데드크로스
            bool golden = s.Contains("골든") || s.Contains("golden") || s.Contains("정배열");
            bool dead = s.Contains("데드") || s.Contains("dead") || s.Contains("역배열");
            if (golden || dead)
            {
                var (sp, lp) = ExtractMaPair(s);
                var lo = new SpecTerm(SpecKind.Sma, sp);
                var hi = new SpecTerm(SpecKind.Sma, lp);
                if (golden) entry.Add(new SpecCond(lo, SpecOp.CrossAbove, false, 0, hi));   // 골든크로스 진입(매수 의도)
                exit.Add(new SpecCond(lo, SpecOp.CrossBelow, false, 0, hi));                // 데드크로스 청산
                // 데드크로스만 언급(매도 전용)이면 진입은 만들지 않음 → 아래에서 진입 미인식으로 실패 처리
            }

            // ② RSI 임계 (이하/below=과매도 진입, 이상/above=과매수 청산)
            int rsiP = ExtractRsiPeriod(s);
            AddRsi(s, "이하", rsiP, SpecOp.Lt, entry);
            AddRsi(s, "below", rsiP, SpecOp.Lt, entry);
            AddRsi(s, "이상", rsiP, SpecOp.Gt, exit);
            AddRsi(s, "above", rsiP, SpecOp.Gt, exit);

            // ③ 주가 vs N일선
            var maAbove = Regex.Match(s, @"(?:주가|가격|종가|price).{0,8}?(\d{1,3})\s*일?\s*(?:선|ma|sma)?.{0,6}?(위|상회|above|돌파)");
            if (maAbove.Success && int.TryParse(maAbove.Groups[1].Value, out var pA))
                entry.Add(new SpecCond(new SpecTerm(SpecKind.Price, 0), SpecOp.CrossAbove, false, 0, new SpecTerm(SpecKind.Sma, Clamp(pA))));
            var maBelow = Regex.Match(s, @"(?:주가|가격|종가|price).{0,8}?(\d{1,3})\s*일?\s*(?:선|ma|sma)?.{0,6}?(아래|하회|below|이탈)");
            if (maBelow.Success && int.TryParse(maBelow.Groups[1].Value, out var pB))
                exit.Add(new SpecCond(new SpecTerm(SpecKind.Price, 0), SpecOp.CrossBelow, false, 0, new SpecTerm(SpecKind.Sma, Clamp(pB))));

            if (entry.Count == 0)
                return Fail("진입 신호를 인식하지 못했습니다. 예: \"5일선이 20일선을 골든크로스하면 매수, 데드크로스하면 매도, 손절 8%\"");

            var spec = new StrategySpec(MakeId(entry, exit, SpecLogic.And, SpecLogic.Or, sl, tp), Trunc(nl, 60),
                entry, SpecLogic.And, exit, SpecLogic.Or, sl, tp);
            var (ok, err) = StrategyCompiler.Validate(spec);
            return ok ? new CompileResult(true, spec, "규칙", "오프라인 규칙 파서") : Fail(err);
        }

        // ===== LLM 스펙 JSON 파서 (신뢰하지 않고 C#이 검증) =====
        public static CompileResult ParseJson(string json, string source = "LLM")
        {
            if (string.IsNullOrWhiteSpace(json)) return Fail("빈 응답입니다.");
            string body = StripFences(json);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var entry = ReadConds(root, "entry");
                var exit = ReadConds(root, "exit");
                var enLogic = ReadLogic(root, "entryLogic");
                var exLogic = ReadLogic(root, "exitLogic");
                double sl = ReadNum(root, "stopLossPct");
                double tp = ReadNum(root, "takeProfitPct");
                string title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "LLM 전략";

                var spec = new StrategySpec(MakeId(entry, exit, enLogic, exLogic, sl, tp), Trunc(title, 60),
                    entry, enLogic, exit, exLogic, sl, tp);
                var (ok, err) = StrategyCompiler.Validate(spec);
                return ok ? new CompileResult(true, spec, source, "LLM 제안 → C# 검증 통과") : Fail("LLM 스펙 검증 실패: " + err);
            }
            catch (Exception ex) { return Fail("LLM 스펙 JSON 파싱 실패: " + ex.Message); }
        }

        // LLM에 보낼 제약 스키마 프롬프트(JSON만 출력하도록 강제)
        public static string BuildPrompt(string nl) =>
$@"너는 트레이딩 전략 컴파일러다. 아래 한국어/영어 설명을 정확히 다음 JSON 스키마로만 변환하라. 설명·코드블록 금지, JSON만 출력.
스키마:
{{
  ""title"": string,
  ""entryLogic"": ""and""|""or"",
  ""entry"": [{{ ""left"": {{""kind"":""price""|""sma""|""ema""|""rsi""|""macdHist""|""pctB"", ""param"": int}}, ""op"": ""crossAbove""|""crossBelow""|""gt""|""lt"", ""const"": number(선택), ""right"": {{""kind"":..., ""param"":int}}(선택) }}],
  ""exitLogic"": ""and""|""or"",
  ""exit"": [ 위와 동일 ],
  ""stopLossPct"": number, ""takeProfitPct"": number
}}
규칙: op가 gt/lt면 const 또는 right 중 하나; crossAbove/crossBelow는 보통 right(또는 const). kind=price/macdHist는 param 무시(0). 기간 param은 2~200. 손절/익절 없으면 0.
설명: {nl}
JSON:";

        // ===== 내부 헬퍼 =====
        private static CompileResult Fail(string note) => new CompileResult(false, null, "규칙", note);

        private static void AddRsi(string s, string kw, int period, SpecOp op, List<SpecCond> into)
        {
            int idx = s.IndexOf(kw, StringComparison.Ordinal);
            if (idx < 0 || !s.Contains("rsi")) return;
            // kw 앞 12자 내의 숫자를 임계로
            int from = Math.Max(0, idx - 12);
            var m = Regex.Match(s.Substring(from, idx - from + Math.Min(kw.Length, s.Length - idx)), @"(\d{1,3})");
            if (!m.Success || !double.TryParse(m.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var thr)) return;
            if (thr < 0 || thr > 100) return;
            if (into.Any(c => c.Left.Kind == SpecKind.Rsi && c.Op == op)) return;   // 중복 방지
            into.Add(new SpecCond(new SpecTerm(SpecKind.Rsi, period), op, true, thr, null));
        }

        private static int ExtractRsiPeriod(string s)
        {
            // 명시적 기간 형태만 기간으로 인정: "rsi(14)" 또는 "rsi 14일/기간/period/봉".
            // "rsi 30 이하"처럼 임계만 적힌 경우 30을 기간으로 오인하지 않고 기본 14를 쓴다(임계는 AddRsi가 따로 읽음).
            var m = Regex.Match(s, @"rsi\s*\(\s*(\d{1,3})\s*\)|rsi\s*(\d{1,3})\s*(?:일|기간|period|봉)");
            if (m.Success)
            {
                var g = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (int.TryParse(g, out var p) && p >= 2 && p <= 100) return p;
            }
            return 14;
        }

        private static (int, int) ExtractMaPair(string s)
        {
            // "5/20", "5일선 ... 20일선", "ma5 ... ma20" 등에서 두 기간 추출
            var pair = Regex.Match(s, @"(\d{1,3})\s*/\s*(\d{1,3})");
            if (pair.Success && int.TryParse(pair.Groups[1].Value, out var a) && int.TryParse(pair.Groups[2].Value, out var b))
                return Order(a, b);
            var nums = Regex.Matches(s, @"(\d{1,3})\s*(?:일\s*선|일선|ma|sma|day)")
                .Select(m => int.TryParse(m.Groups[1].Value, out var v) ? v : 0).Where(v => v >= 2).Distinct().ToList();
            if (nums.Count >= 2) return Order(nums[0], nums[1]);
            return (5, 20);   // 기본
        }

        private static (int, int) Order(int a, int b) { a = Clamp(a); b = Clamp(b); return a <= b ? (a, b) : (b, a); }
        private static int Clamp(int p) => Math.Clamp(p, StrategyCompiler.MinPeriod, StrategyCompiler.MaxPeriod);

        private static readonly string[] PctKeywords = { "손절", "익절", "목표", "스탑", "stop", "target", "takeprofit" };

        private static double ExtractPct(string s, params string[] kws)
        {
            foreach (var kw in kws)
            {
                int idx = s.IndexOf(kw, StringComparison.Ordinal);
                if (idx < 0) continue;
                var win = s.Substring(idx, Math.Min(16, s.Length - idx));
                int start = Math.Min(kw.Length, win.Length);
                int cut = win.Length;
                int comma = win.IndexOf(',', start); if (comma > 0) cut = Math.Min(cut, comma);
                foreach (var other in PctKeywords)   // 다른 손절/익절 키워드 경계에서 끊어 이웃 숫자 오취 방지
                {
                    if (other == kw) continue;
                    int oi = win.IndexOf(other, start, StringComparison.Ordinal);
                    if (oi > 0) cut = Math.Min(cut, oi);
                }
                var m = Regex.Match(win.Substring(0, cut), @"(\d{1,3}(?:\.\d+)?)\s*%?");
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) && v > 0) return v;
            }
            return 0;
        }

        private static List<SpecCond> ReadConds(JsonElement root, string prop)
        {
            var list = new List<SpecCond>();
            if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in arr.EnumerateArray())
            {
                var left = ReadTerm(e, "left");
                if (left == null) continue;
                var op = ReadOp(e);
                bool isConst = e.TryGetProperty("const", out var cv) && (cv.ValueKind == JsonValueKind.Number || cv.ValueKind == JsonValueKind.String);
                var right = ReadTerm(e, "right");
                if (isConst)
                    list.Add(new SpecCond(left, op, true, ReadNum(e, "const"), null));
                else if (right != null)
                    list.Add(new SpecCond(left, op, false, 0, right));
            }
            return list;
        }

        private static SpecTerm? ReadTerm(JsonElement e, string prop)
        {
            if (!e.TryGetProperty(prop, out var o) || o.ValueKind != JsonValueKind.Object) return null;
            string kind = o.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? (k.GetString() ?? "") : "";
            if (!Enum.TryParse<SpecKind>(kind, true, out var sk)) return null;
            int param = (int)ReadNum(o, "param");
            if (sk is SpecKind.Price or SpecKind.MacdHist) param = 0;
            else param = Clamp(param < 2 ? 14 : param);
            return new SpecTerm(sk, param);
        }

        private static SpecOp ReadOp(JsonElement e)
        {
            string op = e.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? (o.GetString() ?? "") : "";
            return Enum.TryParse<SpecOp>(op, true, out var v) ? v : SpecOp.Gt;
        }

        private static SpecLogic ReadLogic(JsonElement root, string prop)
        {
            string v = root.TryGetProperty(prop, out var o) && o.ValueKind == JsonValueKind.String ? (o.GetString() ?? "") : "";
            return v.Equals("or", StringComparison.OrdinalIgnoreCase) ? SpecLogic.Or : SpecLogic.And;
        }

        private static double ReadNum(JsonElement e, string prop)
        {
            if (!e.TryGetProperty(prop, out var v)) return 0;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s)) return s;
            return 0;
        }

        private static string StripFences(string s)
        {
            s = s.Trim();
            int a = s.IndexOf('{'), b = s.LastIndexOf('}');
            return (a >= 0 && b > a) ? s.Substring(a, b - a + 1) : s;
        }

        private static string Trunc(string s, int n) { s = (s ?? "").Trim(); return s.Length <= n ? s : s.Substring(0, n); }

        // 동일 스펙 동일 Id(공용 StrategyCompiler.Id — 파서·디코더가 같은 해시 사용). 실제 logic 반영.
        private static string MakeId(List<SpecCond> en, List<SpecCond> ex, SpecLogic enLogic, SpecLogic exLogic, double sl, double tp)
            => StrategyCompiler.Id(new StrategySpec("", "", en, enLogic, ex, exLogic, sl, tp));
    }
}
