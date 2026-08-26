// ============================================================================
// DbMaintenance - SQLite 유지보수 (Dogfooding 메인터넌스) (v3.39.0)
//  타임머신·이벤트스터디·반사실 트윈 등이 캔들/시그널/알림을 끊임없이 읽고 쓰며
//  WAL이 커지고 파편화된다. 주기적(주 1회) wal_checkpoint + ANALYZE + VACUUM로 정리.
//  단일 연결 + lock 직렬화 모델이라 유휴 시 안전. 실패는 삼켜 헤드리스 안정성 유지.
//  ※ 핵심 테이블 인덱스는 이미 존재(IX_Tx_*, IX_Signals_*, IX_Alerts_*, IX_News_*, IX_Dividends_*).
// ============================================================================
using System;

namespace MS2E.Infrastructure
{
    public static class DbMaintenance
    {
        /// <summary>checkpoint + ANALYZE + VACUUM 실행. (전/후 DB 크기 KB) 반환.</summary>
        public static (long beforeKb, long afterKb) Run(LocalDatabase db)
        {
            long before = SizeKb(db);
            try { db.Execute("PRAGMA wal_checkpoint(TRUNCATE)"); } catch (Exception ex) { Log.Warn($"[Maint] checkpoint: {ex.Message}"); }
            try { db.Execute("ANALYZE"); } catch (Exception ex) { Log.Warn($"[Maint] ANALYZE: {ex.Message}"); }
            try { db.Execute("VACUUM"); } catch (Exception ex) { Log.Warn($"[Maint] VACUUM: {ex.Message}"); }
            long after = SizeKb(db);
            return (before, after);
        }

        /// <summary>page_count × page_size 로 현재 DB 크기(KB) 추정.</summary>
        public static long SizeKb(LocalDatabase db)
        {
            try
            {
                var pc = db.Query("PRAGMA page_count");
                var ps = db.Query("PRAGMA page_size");
                long pages = pc.Rows.Count > 0 ? Convert.ToInt64(pc.Rows[0][0]) : 0;
                long size = ps.Rows.Count > 0 ? Convert.ToInt64(ps.Rows[0][0]) : 0;
                return pages * size / 1024;
            }
            catch { return 0; }
        }
    }
}
