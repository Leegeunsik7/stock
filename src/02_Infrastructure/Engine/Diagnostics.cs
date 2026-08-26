// ============================================================================
// Diagnostics - 도그푸딩 진단 (백그라운드 훅·LLM·시세·DB 상태) (v3.40.0)
//  일주일 켜두고 "모든 게 정상인가?"를 한 화면에서 본다. 상태 집계(BuildRows)와
//  신선도/경과 계산은 순수·테스트 가능, 라이브 연결 점검만 페이지에서 비동기로.
// ============================================================================
using System;
using System.Collections.Generic;

namespace StockManager.Common
{
    public record DiagRow(string Area, string Status, string Detail);

    public static class Diagnostics
    {
        /// <summary>마지막 실행이 withinHours 내면 정상, 아니면 지연, 비어있으면 대기. 순수.</summary>
        public static string Freshness(string? lastIso, double withinHours, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(lastIso) || !DateTime.TryParse(lastIso, out var t)) return "⏳ 대기";
            return (now - t).TotalHours <= withinHours ? "✅ 정상" : "⚠️ 지연";
        }

        /// <summary>경과 시간 사람말. 순수.</summary>
        public static string TimeAgo(string? lastIso, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(lastIso) || !DateTime.TryParse(lastIso, out var t)) return "실행 전";
            var d = now - t;
            if (d.TotalMinutes < 1) return "방금";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}분 전";
            if (d.TotalDays < 1) return $"{(int)d.TotalHours}시간 전";
            return $"{(int)d.TotalDays}일 전";
        }

        /// <summary>엔진 하트비트 + 백그라운드 훅 + DB/LLM 상태 행 집계(설정 읽기). 순수(now·heartbeat 주입).</summary>
        public static List<DiagRow> BuildRows(LocalDatabase db, DateTime engineLastRun, DateTime now)
        {
            var rows = new List<DiagRow>
            {
                new("엔진 하트비트", engineLastRun == default ? "⏳ 대기" : Freshness(engineLastRun.ToString("o"), 1.0 / 60 * 3, now),
                    engineLastRun == default ? "미가동" : engineLastRun.ToString("HH:mm:ss"))
            };

            var cfg = new DatabaseConfigAdapter(db);   // Phase 84 — config 포트 경유(직결 해방·콜드 진단 경로)
            void Hook(string area, string enKey, string lastKey, double withinHrs)
            {
                bool en = cfg.GetString(enKey, "") == "true";
                var last = cfg.GetString(lastKey, "");
                rows.Add(new(area, en ? Freshness(last, withinHrs, now) : "⚪ 꺼짐", en ? TimeAgo(last, now) : "비활성"));
            }
            Hook("아침 브리핑", "Brief.Enabled", "Brief.LastSent", 26);
            Hook("오버나잇 옵티마이저", "Optimizer.Enabled", "Optimizer.LastRun", 26);
            Hook("레짐 감시", "Regime.Enabled", "Regime.LastRun", 14);
            Hook("드리프트 트리거", "Drift.Enabled", "Drift.LastRun", 26);
            Hook("야간 리포트", "Nightly.Enabled", "Nightly.LastSent", 26);

            bool copEn = cfg.GetString("Copilot.Enabled", "") == "true";
            rows.Add(new("코파일럿", copEn ? "✅ 가동" : "⚪ 꺼짐", $"정책 {CopilotStore.Load(db).Count}개"));

            rows.Add(new("DB 유지보수(주1회)", Freshness(cfg.GetString("Maint.LastRun", ""), 24 * 8, now), TimeAgo(cfg.GetString("Maint.LastRun", ""), now)));
            rows.Add(new("DB 크기", "—", $"{DbMaintenance.SizeKb(db):N0} KB"));
            rows.Add(new("LLM 백엔드", "—", new AiService(db).ActiveLlm));

            int unread = 0;
            try { var dt = db.Query("SELECT COUNT(*) AS c FROM Alerts WHERE IsRead=0"); unread = dt.Rows.Count > 0 ? Safe.Int(dt.Rows[0]["c"]) : 0; } catch { }
            rows.Add(new("미확인 알림", unread > 0 ? "🔔 " + unread : "✅ 0", unread > 0 ? $"{unread}건" : "없음"));
            return rows;
        }
    }
}
