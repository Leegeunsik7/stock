// ============================================================================
// LogRedactor — 로그 비밀 차폐 (외부 의존 0 · 예외 안전 · 스레드 안전)
//
//  RuntimeConfig.Redact 는 '알려진 비밀 1개'를 마스킹한다. 그러나 로그 스크러빙은
//  임의 로그 라인 어디에 비밀이 박혀 있는지 모른 채 탐지·차폐해야 한다 — 특히
//  예외 덤프(`KisException: auth failed for key ABCD…`)처럼 비밀이 생으로 끼어드는 경로.
//
//  ── 2겹 방어 ────────────────────────────────────────────────────────────────
//   1) 등록 비밀의 정확 일치 — 부팅 시 실 비밀값(키움 API 키·계좌번호 등)을 등록하면
//      그 값이 어느 라인에 나타나든(예외 메시지·스택 포함) 정확히 마스킹. 오탐 0.
//   2) 라벨 패턴 — (?i)(key|secret|token|password)=값 형태의 구조적 누출을 차단.
//      미등록 비밀(서드파티 로그 등)에 대한 심층 방어.
//
//   3) [PII 하드닝] 패턴 PII — FinancialPiiMasker(주민번호·카드·이메일·전화·계좌·JWT/알려진 키 접두)로 미등록·무라벨
//      민감정보까지 차폐. 주가/주문 수치(종목코드·가격·수량·타임스탬프·주문번호)는 경계 규칙으로 보존.
//   4) [PII 하드닝] 라벨 확장 — authorization/bearer·client_secret·access_key·private_key·CANO/ACNT·account(no) 형태.
//   ★Shared 인스턴스는 RawCredentialLeakInterlock.Shared 와 결속 — 스크럽 "이전" 원문 라인에 등록 자격증명이 있으면 유출 시도로
//     Trip(발주 토큰 0x00·무장 차단). 독립 인스턴스(new LogRedactor())는 인터락 미결속(테스트·도구용).
//
//  ── 견고성 ──────────────────────────────────────────────────────────────────
//  Scrub 은 절대 예외를 던지지 않는다(로깅은 무슨 일이 있어도 죽지 않아야 한다).
//  중간에 실패해도 그때까지의 부분 차폐분을 반환한다(정확-일치 패스가 먼저라 비밀 우선 차폐).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MS2E.Infrastructure
{
    public sealed class LogRedactor
    {
        /// <summary>프로세스 전역 공유 인스턴스 — Log.Write 가 참조.</summary>
        public static LogRedactor Shared { get; } = new LogRedactor { LeakInterlock = RawCredentialLeakInterlock.Shared };

        /// <summary>원문 자격증명이 탐지된 라인을 대체하는 고정 표식(원문 일부도 남기지 않음).</summary>
        public const string LeakDiscardedLine = "[보안] 원문 자격증명 유출 시도 탐지 — 라인 폐기 · 발주 토큰 소거 · 무장 차단";

        /// <summary>[PII 하드닝] 원문 자격증명 유출 인터락(선택). Shared 는 전역 인터락 결속.</summary>
        public RawCredentialLeakInterlock? LeakInterlock { get; init; }

        private const int MinSecretLen = 6;   // 초단문은 과차폐(오탐) 위험 → 등록 제외

        private readonly object   _gate    = new();
        private volatile string[] _secrets = Array.Empty<string>();

        // 라벨: 값 → '값'만 마스킹(라벨·구분자·여는따옴표 보존). 값은 4자 이상·구분자 전까지.
        private static readonly Regex Labeled = new Regex(
            @"(?i)(\b(?:api[_-]?key|app[_-]?key|app[_-]?secret|secret|token|access[_-]?token|access[_-]?key|client[_-]?secret|client[_-]?id|private[_-]?key|password|passwd|pwd|cano|acnt[_-]?prdt[_-]?cd|account[_-]?(?:no|number)|acct[_-]?no)\b\s*[:=]\s*)(""?)([^\s""',;)}\]]{4,})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // [PII 하드닝] "Authorization: Bearer <token>" — 헤더 덤프(예외 메시지·HTTP 트레이스)의 접근토큰
        private static readonly Regex Bearer = new Regex(
            @"(?i)(\bbearer\s+)([A-Za-z0-9._~+/=-]{8,})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>실 비밀값 1건 등록. 공백·중복·초단문은 무시. 스레드 안전.</summary>
        public void Register(string? secret)
        {
            if (string.IsNullOrWhiteSpace(secret)) return;
            secret = secret.Trim();
            if (secret.Length < MinSecretLen) return;
            lock (_gate)
            {
                if (Array.IndexOf(_secrets, secret) >= 0) return;
                var next = new List<string>(_secrets) { secret };
                next.Sort(static (a, b) => b.Length.CompareTo(a.Length));   // 긴 것 우선(부분 겹침 방지)
                _secrets = next.ToArray();
            }
        }

        /// <summary>등록된 비밀 개수(진단용).</summary>
        public int Count => _secrets.Length;

        /// <summary>등록 비밀을 모두 비운다(테스트·재설정용).</summary>
        public void Clear()
        {
            lock (_gate) _secrets = Array.Empty<string>();
        }

        /// <summary>로그 라인에서 등록 비밀·라벨 비밀을 마스킹. 예외 안전.</summary>
        public string Scrub(string? line)
        {
            if (string.IsNullOrEmpty(line)) return line ?? "";
            string s = line;
            // 원문 자격증명 유출 시도 → Trip + 라인 전체 폐기(Fail-Closed: 인터락에만 등록된 값이어도 싱크에 원문 0바이트)
            try { if (LeakInterlock?.Inspect(line) == true) return LeakDiscardedLine; } catch { /* 인터락 실패가 로깅을 죽이지 않음 */ }
            try
            {
                var secrets = _secrets;   // 락-프리 스냅샷
                foreach (var sec in secrets)
                    if (s.Contains(sec, StringComparison.Ordinal))
                        s = s.Replace(sec, RuntimeConfig.Redact(sec));

                s = Labeled.Replace(s, static m =>
                    m.Groups[1].Value + m.Groups[2].Value + RuntimeConfig.Redact(m.Groups[3].Value));
                s = Bearer.Replace(s, static m => m.Groups[1].Value + RuntimeConfig.Redact(m.Groups[2].Value));
                s = FinancialPiiMasker.Mask(s);   // 패턴 PII(미등록·무라벨) — 주가/주문 수치 보존
            }
            catch { /* 차폐 실패해도 부분 차폐분 반환 — 정확-일치가 먼저라 비밀은 우선 차폐됨 */ }
            return s;
        }
    }
}
