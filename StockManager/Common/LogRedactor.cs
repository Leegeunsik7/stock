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
//  ── 견고성 ──────────────────────────────────────────────────────────────────
//  Scrub 은 절대 예외를 던지지 않는다(로깅은 무슨 일이 있어도 죽지 않아야 한다).
//  중간에 실패해도 그때까지의 부분 차폐분을 반환한다(정확-일치 패스가 먼저라 비밀 우선 차폐).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace StockManager.Common
{
    public sealed class LogRedactor
    {
        /// <summary>프로세스 전역 공유 인스턴스 — Log.Write 가 참조.</summary>
        public static LogRedactor Shared { get; } = new LogRedactor();

        private const int MinSecretLen = 6;   // 초단문은 과차폐(오탐) 위험 → 등록 제외

        private readonly object   _gate    = new();
        private volatile string[] _secrets = Array.Empty<string>();

        // 라벨: 값 → '값'만 마스킹(라벨·구분자·여는따옴표 보존). 값은 4자 이상·구분자 전까지.
        private static readonly Regex Labeled = new Regex(
            @"(?i)(\b(?:api[_-]?key|app[_-]?key|app[_-]?secret|secret|token|password|passwd|pwd)\b\s*[:=]\s*)(""?)([^\s""',;)}\]]{4,})",
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
            try
            {
                var secrets = _secrets;   // 락-프리 스냅샷
                foreach (var sec in secrets)
                    if (s.Contains(sec, StringComparison.Ordinal))
                        s = s.Replace(sec, RuntimeConfig.Redact(sec));

                s = Labeled.Replace(s, static m =>
                    m.Groups[1].Value + m.Groups[2].Value + RuntimeConfig.Redact(m.Groups[3].Value));
            }
            catch { /* 차폐 실패해도 부분 차폐분 반환 — 정확-일치가 먼저라 비밀은 우선 차폐됨 */ }
            return s;
        }
    }
}
