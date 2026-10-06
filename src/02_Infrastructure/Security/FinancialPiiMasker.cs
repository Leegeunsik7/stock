// ============================================================================
// FinancialPiiMasker — [PII 하드닝] 금융 PII · 민감 토큰 무할당(δ=0) 제자리 마스커
//
//   탐지(전부 Span<char> 인덱스 스캔 — 정규식·부분문자열·박싱 0, 길이 불변 치환):
//     ① 주민등록번호 dddddd-ddddddd(구분자 '-'·' ') · 무구분 13자리는 생년월일(YYMMDD)+성별자리(1~8) 유효할 때만
//     ② 카드 13~19자리 — 구분자 그룹형(첫 그룹 4·6자리)은 무조건(보수적), 무구분 연속형은 15~16자리 + Luhn 통과 + 타임스탬프 아님
//     ③ 이메일 local@domain.tld(TLD 영문 2자+ · git@ 원격 제외)
//     ④ 전화 — 국내 구분자형(01x/02/0[3-6][1-5]/070 + 국번 3~4 + 4) · 무구분은 휴대 01x 11자리만 · 국제 '+'8~15자리
//     ⑤ 계좌 — 증권(KIS 8-2 · 키움 4-4-2)·은행 구분자 템플릿, 또는 계좌 키워드(계좌/account/acct/accno/CANO) 문맥의 8~14자리
//     ⑥ 토큰 — 알려진 접두(sk-·AIza·ghp_·xoxb-·xoxp-·AKIA) 16자+, JWT(eyJ…, KIS 접근토큰) 20자+, KIS AppKey(PS+34)
//
//   ★주가/주문 데이터 오마스킹 금지(퀀트 파라미터 보호): 6자리 종목코드(005930)·가격(72,500 · 72500.5)·수량·호가·
//     타임스탬프(20260930091530)·주문번호(0000123456 · 0212345678)·해시(64 hex)·버전(v12.14.1)은 어떤 규칙에도 걸리지 않도록
//     설계했다 — 특히 무구분 국내 유선번호(02/031+7~8자리)는 주문번호와 형태가 같아 증권 문맥에서는 탐지하지 않는다(정직 트레이드오프).
//     금액 단위(원·KRW·주·₩·$)가 붙은 숫자는 계좌 키워드 문맥이어도 계좌로 보지 않는다("계좌 예수금 5000000000원").
// ============================================================================
using System;

namespace MS2E.Infrastructure
{
    public static class FinancialPiiMasker
    {
        private static readonly string[] TokenPrefixes = { "sk-", "AIza", "ghp_", "xoxb-", "xoxp-", "AKIA" };
        private static readonly string[] AccountKeywords = { "계좌", "account", "acct", "accno", "cano" };
        private static readonly int[][] AccountTemplates =
        {
            new[] { 8, 2 },        // 한국투자(KIS) CANO-ACNT_PRDT_CD
            new[] { 4, 4, 2 },     // 키움 표기
            new[] { 3, 2, 6 }, new[] { 3, 3, 6 }, new[] { 4, 4, 4 }, new[] { 3, 6, 3 },   // 은행(11~12자리)
            new[] { 6, 2, 6 }, new[] { 3, 6, 5 }, new[] { 4, 3, 6 }, new[] { 4, 2, 7 }, new[] { 3, 4, 4, 2 }, new[] { 3, 6, 2, 3 },
        };

        /// <summary>source → destination 복사 후 제자리 마스킹(힙 할당 0). 반환 = 마스킹 항목 수.</summary>
        public static int MaskInto(ReadOnlySpan<char> source, Span<char> destination)
        {
            if (destination.Length != source.Length) throw new ArgumentException("destination 길이는 source 와 같아야 한다(길이 불변 치환).");
            source.CopyTo(destination);
            return MaskInPlace(destination);
        }

        // ── ⑦ 등록 비밀값(정확 일치) ─────────────────────────────────────────
        //   [KIS] AppSecret 은 고정 접두·구조가 없는 긴 무작위 문자열이라 패턴 규칙(⑥)이 잡지 못한다(실측: 수정 전 평문 통과).
        //   런타임에 실제 비밀값을 등록(콜드 1회)하면 등장 위치를 전 문자 '*' 로 치환. 판정은 Span IndexOf(힙 0).
        private static volatile char[][] s_secrets = Array.Empty<char[]>();
        private static readonly object s_secretGate = new();
        public const int MinRegisteredSecretLength = 8;

        /// <summary>정확 일치 마스킹 대상 비밀값 등록(콜드 · 8자 미만/중복 무시). 반환 = 신규 등록 여부.</summary>
        public static bool RegisterSecret(string? secret)
        {
            if (string.IsNullOrWhiteSpace(secret)) return false;
            secret = secret.Trim();
            if (secret.Length < MinRegisteredSecretLength) return false;
            lock (s_secretGate)
            {
                foreach (var s in s_secrets) if (s.AsSpan().SequenceEqual(secret)) return false;
                var copy = GC.AllocateArray<char>(secret.Length, pinned: true);
                secret.AsSpan().CopyTo(copy);
                var next = new char[s_secrets.Length + 1][];
                s_secrets.CopyTo(next, 0); next[^1] = copy;
                s_secrets = next;
                return true;
            }
        }

        private static int MaskRegisteredSecrets(Span<char> t)
        {
            var secrets = s_secrets;
            int count = 0;
            for (int k = 0; k < secrets.Length; k++)
            {
                ReadOnlySpan<char> s = secrets[k];
                int from = 0;
                while (from <= t.Length - s.Length)
                {
                    int at = t[from..].IndexOf(s);
                    if (at < 0) break;
                    at += from;
                    t.Slice(at, s.Length).Fill('*');
                    count++;
                    from = at + s.Length;
                }
            }
            return count;
        }

        /// <summary>호출측 버퍼를 제자리 마스킹(힙 할당 0).</summary>
        public static int MaskInPlace(Span<char> text)
        {
            int n = MaskRegisteredSecrets(text);   // ⑦ 먼저 — 등록 비밀값이 다른 규칙에 부분 매칭돼 일부만 가려지는 것 방지
            n += MaskResidentNumbers(text);
            n += MaskCards(text);
            n += MaskEmails(text);
            n += MaskPhones(text);
            n += MaskAccounts(text);
            n += MaskTokens(text);
            return n;
        }

        /// <summary>문자열 편의 오버로드(결과 string 1회 할당은 불가피 — 콜드 로그 경로용).</summary>
        public static string Mask(string? input)
        {
            if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
            char[] buf = input.ToCharArray();
            return MaskInPlace(buf) == 0 ? input : new string(buf);
        }

        // ── 공통 경계 ──────────────────────────────────────────────────────────
        private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
        private static bool D(char c) => char.IsAsciiDigit(c);

        private static bool BoundaryBefore(ReadOnlySpan<char> t, int s)
        {
            if (s == 0) return true;
            char p = t[s - 1];
            if (IsWordChar(p) || p is '/' or '\\' or '#' or '*' or '+' or '@' or '$' or '₩') return false;
            if (p is '.' or '-' or ',' or ':' && s >= 2 && D(t[s - 2])) return false;   // 12.14 · 2026-09 · 72,500 · 09:15
            return true;
        }

        private static bool BoundaryAfter(ReadOnlySpan<char> t, int e)
        {
            if (e >= t.Length) return true;
            char n = t[e];
            if (IsWordChar(n) || n is '/' or '\\' or '*' or '@') return false;
            if (n is '.' or '-' or ',' or ':' && e + 1 < t.Length && D(t[e + 1])) return false;
            return true;
        }

        private static void Star(Span<char> t, int s, int e) { for (int k = s; k < e; k++) if (D(t[k])) t[k] = '*'; }

        private static int Digits(ReadOnlySpan<char> t, int p) { int n = 0; while (p + n < t.Length && D(t[p + n])) n++; return n; }

        // ── ① 주민등록번호 ────────────────────────────────────────────────────
        private static int MaskResidentNumbers(Span<char> t)
        {
            int count = 0;
            for (int i = 0; i < t.Length; i++)
            {
                if (!D(t[i]) || !BoundaryBefore(t, i)) continue;
                int a = Digits(t, i);
                if (a == 6 && i + 7 < t.Length && t[i + 6] is '-' or ' ' && Digits(t, i + 7) == 7 && BoundaryAfter(t, i + 14) && t[i + 7] is >= '1' and <= '8')
                { Star(t, i, i + 14); count++; i += 13; continue; }
                if (a == 13 && BoundaryAfter(t, i + 13) && IsBirthDate(t, i) && t[i + 6] is >= '1' and <= '8')
                { Star(t, i, i + 13); count++; i += 12; continue; }
                i += a - 1;
            }
            return count;
        }

        private static bool IsBirthDate(ReadOnlySpan<char> t, int i)
        {
            int mm = (t[i + 2] - '0') * 10 + (t[i + 3] - '0'), dd = (t[i + 4] - '0') * 10 + (t[i + 5] - '0');
            return mm is >= 1 and <= 12 && dd is >= 1 and <= 31;
        }

        // ── ② 카드 ───────────────────────────────────────────────────────────
        private static int MaskCards(Span<char> t)
        {
            int count = 0;
            int i = 0;
            while (i < t.Length)
            {
                if (!D(t[i]) || !BoundaryBefore(t, i)) { i++; continue; }
                // 그룹 스캔(구분자 '-'·' ' 1개, 일관)
                int p = i, digits = 0, groups = 0;
                char sep = '\0';
                while (p < t.Length)
                {
                    int g = Digits(t, p);
                    if (g == 0) break;
                    digits += g; p += g; groups++;
                    if (p + 1 < t.Length && t[p] is '-' or ' ' && D(t[p + 1]) && (sep == '\0' || sep == t[p])) { sep = t[p]; p++; continue; }
                    break;
                }
                if (digits is >= 13 and <= 19 && BoundaryAfter(t, p))
                {
                    bool grouped = groups >= 3 && sep != '\0';
                    // 무구분 연속형은 15~16자리(Amex·Visa/MC 대다수)만 — 17~19자리 무구분은 DateTime.Ticks·시퀀스 ID 와 충돌(오마스킹 방지)
                    bool contiguous = groups == 1 && digits is 15 or 16;
                    if ((grouped && GroupsLookLikeCard(t, i, p)) || (contiguous && Luhn(t, i, p) && !IsTimestamp(t, i, p)))
                    { Star(t, i, p); count++; }
                }
                i = Math.Max(p, i + 1);
            }
            return count;
        }

        private static bool GroupsLookLikeCard(ReadOnlySpan<char> t, int s, int e)
        {
            int first = Digits(t, s);
            return first is 4 or 6;   // 4-4-4-4 · 4-6-5(Amex) · 6-... (증권/은행 계좌 템플릿 6-2-6 과는 ⑤가 별도 처리)
        }

        private static bool Luhn(ReadOnlySpan<char> t, int s, int e)
        {
            int sum = 0; bool dbl = false;
            for (int k = e - 1; k >= s; k--)
            {
                int d = t[k] - '0';
                if (dbl) { d *= 2; if (d > 9) d -= 9; }
                sum += d; dbl = !dbl;
            }
            return sum % 10 == 0;
        }

        /// <summary>yyyyMMdd[HHmmss[fff…]] 형태(19xx/20xx·유효 월일) — 체결/주문 로그 타임스탬프.</summary>
        private static bool IsTimestamp(ReadOnlySpan<char> t, int s, int e)
        {
            if (e - s < 8) return false;
            bool century = (t[s] == '1' && t[s + 1] == '9') || (t[s] == '2' && t[s + 1] == '0');
            int mm = (t[s + 4] - '0') * 10 + (t[s + 5] - '0'), dd = (t[s + 6] - '0') * 10 + (t[s + 7] - '0');
            return century && mm is >= 1 and <= 12 && dd is >= 1 and <= 31;
        }

        // ── ③ 이메일 ─────────────────────────────────────────────────────────
        private static bool IsLocalChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '%' or '+' or '-';

        private static int MaskEmails(Span<char> t)
        {
            int count = 0;
            for (int at = 1; at < t.Length - 3; at++)
            {
                if (t[at] != '@') continue;
                int s = at;
                while (s > 0 && IsLocalChar(t[s - 1])) s--;
                if (s == at || t[s] == '.' || t[at - 1] == '.') continue;
                if (at - s == 3 && (t[s] | 0x20) == 'g' && (t[s + 1] | 0x20) == 'i' && (t[s + 2] | 0x20) == 't') continue;
                int p = at + 1, labels = 0, last = -1, end = -1;
                while (p < t.Length)
                {
                    int ls = p;
                    while (p < t.Length && (char.IsAsciiLetterOrDigit(t[p]) || t[p] == '-')) p++;
                    if (p == ls || t[ls] == '-' || t[p - 1] == '-') break;
                    labels++; last = ls; end = p;
                    if (p + 1 < t.Length && t[p] == '.' && char.IsAsciiLetterOrDigit(t[p + 1])) { p++; continue; }
                    break;
                }
                if (labels < 2 || end - last < 2) continue;
                bool alpha = true;
                for (int k = last; k < end; k++) if (!char.IsAsciiLetter(t[k])) { alpha = false; break; }
                if (!alpha || (end < t.Length && IsWordChar(t[end]))) continue;
                for (int k = s; k < at; k++) t[k] = '*';
                for (int k = at + 1; k < last; k++) if (t[k] != '.') t[k] = '*';
                count++;
                at = end;
            }
            return count;
        }

        // ── ④ 전화 ───────────────────────────────────────────────────────────
        private static int MaskPhones(Span<char> t)
        {
            int count = 0, i = 0;
            while (i < t.Length)
            {
                char c = t[i];
                if ((c is '+' or '0' or '(') && BoundaryBefore(t, i))
                {
                    int end = c == '+' ? MatchInternational(t, i) : MatchDomestic(t, i);
                    if (end > 0 && BoundaryAfter(t, end)) { Star(t, i, end); count++; i = end; continue; }
                }
                i++;
            }
            return count;
        }

        private static int MatchInternational(ReadOnlySpan<char> t, int plus)
        {
            int p = plus + 1, digits = 0, last = -1;
            if (p >= t.Length || !D(t[p]) || t[p] == '0') return -1;
            while (p < t.Length)
            {
                if (D(t[p])) { digits++; last = ++p; continue; }
                if (!(t[p] is '-' or ' ' or '.' or '(' or ')') || last != p || p + 1 >= t.Length) break;
                char n = t[p + 1];
                if (D(n)) { p++; continue; }
                if ((t[p] == ' ' && n == '(') || (t[p] == ')' && n == ' ')) { p += 2; continue; }
                break;
            }
            return digits is >= 8 and <= 15 ? last : -1;
        }

        private static int MatchDomestic(ReadOnlySpan<char> t, int start)
        {
            int p = start;
            bool paren = t[p] == '(';
            if (paren) p++;
            if (p + 2 >= t.Length || t[p] != '0') return -1;
            char a1 = t[p + 1], a2 = t[p + 2];
            bool mobile = a1 == '1' && a2 is '0' or '1' or '6' or '7' or '8' or '9';
            int area;
            if (a1 == '2') area = 2;
            else if (mobile) area = 3;
            else if (a1 is >= '3' and <= '6' && a2 is >= '1' and <= '5') area = 3;
            else if (a1 == '7' && a2 == '0') area = 3;
            else return -1;
            p += area;
            if (paren)
            {
                if (p >= t.Length || t[p] != ')') return -1;
                p++;
                if (p < t.Length && t[p] == ' ') p++;
            }
            char sep = p < t.Length && t[p] is '-' or ' ' or '.' ? t[p] : '\0';
            if (sep == '\0')
            {
                // 증권 문맥: 무구분은 휴대 010 11자리만(02/031+7~8 무구분은 주문번호와 동형 → 오마스킹 방지)
                int run = Digits(t, p);
                return mobile && a2 == '0' && run == 8 && !paren ? p + run : paren && run is 7 or 8 ? p + run : -1;
            }
            p++;
            int mid = Digits(t, p);
            if (mid is not (3 or 4)) return -1;
            p += mid;
            if (p >= t.Length || t[p] != sep) return -1;
            p++;
            return Digits(t, p) == 4 ? p + 4 : -1;
        }

        // ── ⑤ 계좌 ───────────────────────────────────────────────────────────
        private static int MaskAccounts(Span<char> t)
        {
            int count = 0, i = 0;
            Span<int> groups = stackalloc int[6];
            while (i < t.Length)
            {
                if (!D(t[i]) || !BoundaryBefore(t, i)) { i++; continue; }
                int p = i, g = 0, digits = 0;
                char sep = '\0';
                while (p < t.Length && g < groups.Length)
                {
                    int len = Digits(t, p);
                    groups[g++] = len; digits += len; p += len;
                    if (p + 1 < t.Length && t[p] is '-' or ' ' && D(t[p + 1]) && (sep == '\0' || sep == t[p])) { sep = t[p]; p++; continue; }
                    break;
                }
                if (BoundaryAfter(t, p) && !HasCurrencyUnit(t, i, p))
                {
                    bool hit = sep != '\0' && MatchesTemplate(groups[..g]);
                    if (!hit && digits is >= 8 and <= 14 && HasAccountKeyword(t, i)) hit = true;
                    if (hit) { Star(t, i, p); count++; }
                }
                i = Math.Max(p, i + 1);
            }
            return count;
        }

        private static bool HasCurrencyUnit(ReadOnlySpan<char> t, int s, int e)
        {
            if (s > 0 && t[s - 1] is '₩' or '$') return true;
            int q = e;
            if (q < t.Length && t[q] == ' ') q++;
            if (q >= t.Length) return false;
            // 단위 뒤가 글자로 이어지면 단위가 아니다("주문번호"의 '주' · "원금"의 '원')
            bool unitWord = t[q] is '원' or '주' && (q + 1 >= t.Length || !char.IsLetter(t[q + 1]));
            return unitWord || t.Slice(q).StartsWith("KRW", StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesTemplate(ReadOnlySpan<int> groups)
        {
            foreach (var tpl in AccountTemplates)
            {
                if (tpl.Length != groups.Length) continue;
                bool eq = true;
                for (int k = 0; k < tpl.Length; k++) if (tpl[k] != groups[k]) { eq = false; break; }
                if (eq) return true;
            }
            return false;
        }

        private static bool HasAccountKeyword(ReadOnlySpan<char> t, int start)
        {
            // 키워드가 "바로 앞 항목"이어야 한다 — 키워드와 숫자 사이에 다른 숫자가 끼면(예: "계좌 12345678-01 주문번호 0000123456"의
            // 주문번호) 그 숫자는 계좌 문맥이 아니다(주문번호 오마스킹 방지).
            var window = t[Math.Max(0, start - 16)..start];
            foreach (var kw in AccountKeywords)
            {
                int k = window.LastIndexOf(kw.AsSpan(), StringComparison.OrdinalIgnoreCase);
                if (k >= 0 && window[(k + kw.Length)..].IndexOfAnyInRange('0', '9') < 0) return true;
            }
            return false;
        }

        // ── ⑥ 토큰 ───────────────────────────────────────────────────────────
        private static bool IsTokenChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';

        private static int MaskTokens(Span<char> t)
        {
            int count = 0;
            foreach (var prefix in TokenPrefixes) count += MaskPrefixed(t, prefix, 16, allowDot: false);
            count += MaskPrefixed(t, "eyJ", 20, allowDot: true);   // JWT(KIS 접근토큰 등) header.payload.sig
            count += MaskPrefixed(t, "PS", 34, allowDot: false);   // KIS AppKey(PS + 34)
            return count;
        }

        private static int MaskPrefixed(Span<char> t, string prefix, int minBody, bool allowDot)
        {
            int count = 0, i = 0;
            while (i <= t.Length - prefix.Length)
            {
                if ((i == 0 || !IsTokenChar(t[i - 1])) && t.Slice(i, prefix.Length).SequenceEqual(prefix.AsSpan()))
                {
                    int j = i + prefix.Length;
                    while (j < t.Length && (IsTokenChar(t[j]) || (allowDot && t[j] == '.'))) j++;
                    if (j - i >= prefix.Length + minBody)
                    {
                        bool hasDigit = false;
                        for (int k = i; k < j && !hasDigit; k++) hasDigit = D(t[k]);
                        if (hasDigit || allowDot) { for (int k = i; k < j; k++) t[k] = '*'; count++; }
                    }
                    i = j;
                }
                else i++;
            }
            return count;
        }
    }
}
