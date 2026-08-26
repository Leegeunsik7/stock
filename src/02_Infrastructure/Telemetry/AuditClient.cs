// ============================================================================
// AuditClient — S17 #1: 컴플라이언스 파사드 (v3.92.0)
//
//  두 가지 독립 경로:
//  ① PostFraudAlert(Transaction, reason) — 트랜잭션 검증 실패 시 HTTP 알림 전용
//     (DB INSERT는 Database._WriteFraudAlert에서 이미 완료)
//  ② PostAlert(symbol, source, message, severity) — 외부 경보 소스 직접 호출
//     LocalDatabase 주입 여부에 따라 DB 영속화 + HTTP 통지
//
//  가드레일 (환각 방화벽):
//  · 모든 경로에서 발생하는 예외는 silently 삼킴 → 트레이딩 루프 오염 0
//  · HTTP: localhost:9000/api/v1/audit/log (fire-and-forget ContinueWith)
//  · _db: volatile nullable — Initialize() 미호출 시 null, PostAlert DB 경로 skip
// ============================================================================
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MS2E.Infrastructure
{
    public static class AuditClient
    {
        private static volatile LocalDatabase? _db;
        private const string _url = "http://localhost:9000/api/v1/audit/log";

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(4),
        };

        // ── DI 진입점 — 앱 시작 시 1회 호출 (선택적) ────────────────────────
        public static void Initialize(LocalDatabase db)
            => _db = db ?? throw new ArgumentNullException(nameof(db));

        // ── ① 트랜잭션 검증 실패 → HTTP 알림 전용 (DB는 호출자가 이미 처리) ─────
        public static void PostFraudAlert(Transaction t, string reason)
        {
            try
            {
                FireAndForget(
                    t?.Symbol          ?? "",
                    t?.Type.ToString() ?? "",
                    reason             ?? "",
                    "error");
            }
            catch { }
        }

        // ── ② 외부 경보 소스 → DB 영속화(주입된 경우) + HTTP ──────────────────
        public static void PostAlert(string symbol, string source, string message,
            string severity = "HIGH")
        {
            try { _db?.AddFraudAlert(symbol, source, message, severity); } catch { }
            try { FireAndForget(symbol ?? "", source ?? "", message ?? "",
                                severity?.ToLowerInvariant() ?? "high"); }
            catch { }
        }

        // ── HTTP fire-and-forget ───────────────────────────────────────────────
        private static void FireAndForget(string symbol, string source, string message,
            string severity)
        {
            try
            {
                var body = new
                {
                    project   = "stockmanager",
                    timestamp = DateTime.UtcNow.ToString("o"),
                    severity,
                    payload   = new { symbol, source, message },
                };
                string json = JsonSerializer.Serialize(body);
                _http.PostAsync(_url, new StringContent(json, Encoding.UTF8, "application/json"))
                     .ContinueWith(_ => { });
            }
            catch { }
        }
    }
}
