// ============================================================================
// WatchlistStore — v4.64.0: 실시간 시세 워치리스트 저장소(Razor 밖 결정론·테스트 가능)
//
//  카테고리(빌트인+커스텀) CRUD, 그룹별 종목 추가/삭제, 드래그 재정렬(SortOrder 영속)을 LocalDatabase 위에
//  캡슐화한다. Watchlist 테이블(Symbol,Name,Market,GroupName,SortOrder · PK(Symbol,GroupName))은 임의 GroupName 을
//  허용하므로 커스텀 카테고리는 '별도 테이블 없이' GroupName 으로 표현하고, '커스텀 카테고리 목록'만 Settings 에 영속.
//
//  ★빈-그룹 폴백 함정(IsBuiltIn): 빌트인 카테고리의 빈 그룹은 큐레이션 시드를 보여주지만, '커스텀' 빈 그룹은 진짜
//  비어 있어야 한다(사용자가 직접 채울 캔버스). 시드 폴백 적용 여부는 호출자가 IsBuiltIn 으로 판단한다.
//  ★재정렬 영속: SortOrder 컬럼/ORDER BY 는 이미 존재했으나 모든 행이 0 이었다 — 여기서 0..n-1 distinct 로 일괄 기입.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;

namespace StockManager.Common
{
    public static class WatchlistStore
    {
        /// <summary>고정 빌트인 카테고리(삭제 불가·빈 그룹은 큐레이션 시드 폴백 대상).</summary>
        public static readonly string[] BuiltInCategories =
            { "⭐ 즐겨찾기", "🇰🇷 국내주식", "🇺🇸 미국주식", "💎 반도체", "🔋 2차전지", "🤖 AI", "🦾 AI 피지컬" };

        private const string CustomKey = "Watchlist.CustomCategories";

        public static bool IsBuiltIn(string category) => Array.IndexOf(BuiltInCategories, category) >= 0;

        /// <summary>전체 카테고리 = 빌트인 + 커스텀(순서 보존·중복 제거).</summary>
        public static List<string> LoadCategories(LocalDatabase db)
        {
            var list = new List<string>(BuiltInCategories);
            foreach (var c in LoadCustom(db)) if (!list.Contains(c)) list.Add(c);
            return list;
        }

        public static List<string> LoadCustom(LocalDatabase db)
        {
            try
            {
                var j = db.GetSetting(CustomKey);
                if (!string.IsNullOrWhiteSpace(j))
                {
                    var arr = JsonSerializer.Deserialize<List<string>>(j);
                    if (arr != null)
                        return arr.Where(s => !string.IsNullOrWhiteSpace(s) && !IsBuiltIn(s)).Distinct().ToList();
                }
            }
            catch { }
            return new List<string>();
        }

        private static void SaveCustom(LocalDatabase db, List<string> custom)
        { try { db.SetSetting(CustomKey, JsonSerializer.Serialize(custom)); } catch { } }

        /// <summary>커스텀 카테고리 생성. 빈/빌트인/중복 이름은 거부(false).</summary>
        public static bool AddCategory(LocalDatabase db, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || IsBuiltIn(name)) return false;
            var custom = LoadCustom(db);
            if (custom.Contains(name)) return false;
            custom.Add(name); SaveCustom(db, custom);
            return true;
        }

        /// <summary>커스텀 카테고리 삭제(+그 그룹의 워치리스트 행 일괄 삭제). 빌트인은 거부(false).</summary>
        public static bool RemoveCategory(LocalDatabase db, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || IsBuiltIn(name)) return false;
            var custom = LoadCustom(db);
            if (!custom.Remove(name)) return false;
            SaveCustom(db, custom);
            try { db.Execute("DELETE FROM Watchlist WHERE GroupName=@g", new() { ["@g"] = name }); } catch { }
            return true;
        }

        /// <summary>그룹의 종목을 SortOrder 순으로 로드. 빌트인 빈-그룹 시드 폴백은 호출자(IsBuiltIn) 책임.</summary>
        public static List<(string sym, string name)> LoadSymbols(LocalDatabase db, string group)
        {
            var list = new List<(string, string)>();
            try
            {
                var dt = db.Query("SELECT Symbol, Name FROM Watchlist WHERE GroupName=@g ORDER BY SortOrder, rowid",
                    new() { ["@g"] = group });
                foreach (DataRow r in dt.Rows) list.Add((Safe.Str(r["Symbol"]), Safe.Str(r["Name"])));
            }
            catch { }
            return list;
        }

        public static int CountSymbols(LocalDatabase db, string group)
        {
            try
            {
                var dt = db.Query("SELECT COUNT(*) AS c FROM Watchlist WHERE GroupName=@g", new() { ["@g"] = group });
                return dt.Rows.Count > 0 ? Safe.Int(dt.Rows[0]["c"]) : 0;
            }
            catch { return 0; }
        }

        /// <summary>그룹에 종목 추가(맨 끝 SortOrder). 멱등 upsert(Symbol,GroupName) — 재추가는 이름·시장 갱신·순서 보존.</summary>
        public static void AddSymbol(LocalDatabase db, string group, string symbol, string name, Market market)
        {
            symbol = (symbol ?? "").Trim().ToUpperInvariant();
            if (symbol.Length == 0) return;
            int next = NextSortOrder(db, group);   // 맨 끝 인덱스 = MAX(SortOrder)+1
            try
            {
                // ON CONFLICT: 이름+시장 모두 갱신(SaveHolding 의 stale-market 수정과 동궤 — Market 미갱신 시 잘못된 시장 영구 잔존 차단).
                db.Execute(@"INSERT INTO Watchlist(Symbol,Name,Market,GroupName,SortOrder)
                             VALUES(@s,@n,@m,@g,@o)
                             ON CONFLICT(Symbol,GroupName) DO UPDATE SET Name=@n, Market=@m",
                    new() { ["@s"] = symbol, ["@n"] = name, ["@m"] = (int)market, ["@g"] = group, ["@o"] = next });
            }
            catch { }
        }

        /// <summary>다음 SortOrder = MAX(SortOrder)+1. ★COUNT(*) 는 중간 삭제로 sparse 가 되면 생존 행의 SortOrder 와 충돌(중복
        /// 정렬값 → 다음 plain 로드에서 기존 행이 튐) — MAX 기반 단조 증가로 회귀 차단(빈 그룹은 0).</summary>
        private static int NextSortOrder(LocalDatabase db, string group)
        {
            try
            {
                var dt = db.Query("SELECT COALESCE(MAX(SortOrder), -1) AS m FROM Watchlist WHERE GroupName=@g",
                    new() { ["@g"] = group });
                return (dt.Rows.Count > 0 ? Safe.Int(dt.Rows[0]["m"]) : -1) + 1;
            }
            catch { return 0; }
        }

        public static void RemoveSymbol(LocalDatabase db, string group, string symbol)
        {
            try { db.Execute("DELETE FROM Watchlist WHERE Symbol=@s AND GroupName=@g",
                new() { ["@s"] = symbol, ["@g"] = group }); }
            catch { }
        }

        /// <summary>드래그 재정렬 영속 — orderedSymbols 순서대로 SortOrder=0..n-1 일괄 UPDATE(다음 로드 시 유지).
        /// ★원자성: N개 UPDATE 를 단일 트랜잭션으로 묶음 — 중간 실패(BUSY/LOCKED 소진·I/O) 시 부분 0..i-1 상태로 남아
        /// 중복·모순 SortOrder 가 영속되던 회귀 차단(롤백 → 직전 순서 그대로 보존). 멀티라이터(UI+WinForms+알림엔진) 공유 DB 대비.</summary>
        public static void Reorder(LocalDatabase db, string group, IReadOnlyList<string> orderedSymbols)
        {
            if (orderedSymbols is null) return;
            try
            {
                db.RunInTransaction(() =>
                {
                    for (int i = 0; i < orderedSymbols.Count; i++)
                        db.Execute("UPDATE Watchlist SET SortOrder=@o WHERE Symbol=@s AND GroupName=@g",
                            new() { ["@o"] = i, ["@s"] = orderedSymbols[i], ["@g"] = group });
                });
            }
            catch { }
        }
    }
}
