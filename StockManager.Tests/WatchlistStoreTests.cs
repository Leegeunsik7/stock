// ============================================================================
// WatchlistStoreTests — v4.64.0: 실시간 시세 워치리스트 저장소 회귀 고정
//  사용자 요청(스크린샷): ① 카테고리 추가/삭제 ② 그룹 내 종목 추가/삭제 ③ 종목 위·아래 재정렬.
//  숨은 룰 고정: 빌트인 카테고리 불변(추가·삭제 거부), 커스텀 삭제 시 그 그룹 행 일괄 제거,
//   AddSymbol 멱등 upsert(재추가는 이름만·순서 보존), Reorder 가 distinct SortOrder 0..n-1 영속(다음 로드 유지).
//  임시 DB(WithDb)로 Watchlist/Settings 를 직접 검증 — 동어반복 금지(추론값과 다른 축으로 비공허 확인).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class WatchlistStoreTests
    {
        private static void WithDb(Action<LocalDatabase> test)
        {
            var path = Path.Combine(Path.GetTempPath(), "smwl_" + Guid.NewGuid().ToString("N") + ".db");
            try { using var db = new LocalDatabase(path); test(db); }
            finally { foreach (var s in new[] { "", "-wal", "-shm" }) { try { File.Delete(path + s); } catch { } } }
        }

        // ── 카테고리 분류 ─────────────────────────────────────────────────────────
        [Fact]
        public void IsBuiltIn_TrueForSeeded_FalseForArbitrary()
        {
            Assert.True(WatchlistStore.IsBuiltIn("🇰🇷 국내주식"));
            Assert.True(WatchlistStore.IsBuiltIn("🦾 AI 피지컬"));
            Assert.False(WatchlistStore.IsBuiltIn("내 관심주"));
            Assert.False(WatchlistStore.IsBuiltIn(""));
        }

        [Fact]
        public void LoadCategories_FreshDb_ReturnsBuiltInsOnly() => WithDb(db =>
        {
            var cats = WatchlistStore.LoadCategories(db);
            Assert.Equal(WatchlistStore.BuiltInCategories.Length, cats.Count);
            Assert.Equal(WatchlistStore.BuiltInCategories[0], cats[0]);   // 순서 보존(첫=즐겨찾기)
            Assert.Contains("🤖 AI", cats);
        });

        // ── 카테고리 CRUD ─────────────────────────────────────────────────────────
        [Fact]
        public void AddCategory_New_AppearsInCustomAndAll_PersistsAcrossReopen()
        {
            var path = Path.Combine(Path.GetTempPath(), "smwl_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var db = new LocalDatabase(path))
                {
                    Assert.True(WatchlistStore.AddCategory(db, "  바이오  "));   // 트림되어 저장
                    Assert.Contains("바이오", WatchlistStore.LoadCustom(db));
                    Assert.Contains("바이오", WatchlistStore.LoadCategories(db));
                }
                using (var db2 = new LocalDatabase(path))                          // 재오픈 영속(Settings)
                    Assert.Contains("바이오", WatchlistStore.LoadCustom(db2));
            }
            finally { foreach (var s in new[] { "", "-wal", "-shm" }) { try { File.Delete(path + s); } catch { } } }
        }

        [Fact]
        public void AddCategory_RejectsEmpty_BuiltIn_AndDuplicate() => WithDb(db =>
        {
            Assert.False(WatchlistStore.AddCategory(db, ""));            // 빈 거부
            Assert.False(WatchlistStore.AddCategory(db, "   "));        // 공백 거부
            Assert.False(WatchlistStore.AddCategory(db, "🤖 AI"));      // 빌트인 충돌 거부
            Assert.True(WatchlistStore.AddCategory(db, "내테마"));
            Assert.False(WatchlistStore.AddCategory(db, "내테마"));     // 중복 거부
            Assert.Single(WatchlistStore.LoadCustom(db));               // 한 개만 등록됨
        });

        [Fact]
        public void RemoveCategory_BuiltIn_IsRejected() => WithDb(db =>
        {
            Assert.False(WatchlistStore.RemoveCategory(db, "🇰🇷 국내주식"));
            Assert.Equal(WatchlistStore.BuiltInCategories.Length, WatchlistStore.LoadCategories(db).Count);
        });

        [Fact]
        public void RemoveCategory_Custom_DropsCategoryAndItsRows() => WithDb(db =>
        {
            WatchlistStore.AddCategory(db, "내테마");
            WatchlistStore.AddSymbol(db, "내테마", "005930", "삼성전자", Market.KRX_KOSPI);
            WatchlistStore.AddSymbol(db, "내테마", "000660", "SK하이닉스", Market.KRX_KOSPI);
            Assert.Equal(2, WatchlistStore.CountSymbols(db, "내테마"));

            Assert.True(WatchlistStore.RemoveCategory(db, "내테마"));
            Assert.DoesNotContain("내테마", WatchlistStore.LoadCustom(db));   // 카테고리 제거
            Assert.Equal(0, WatchlistStore.CountSymbols(db, "내테마"));        // 그 그룹 행 일괄 삭제
        });

        [Fact]
        public void RemoveCategory_Custom_DoesNotTouchOtherGroups() => WithDb(db =>
        {
            WatchlistStore.AddCategory(db, "A");
            WatchlistStore.AddSymbol(db, "A", "005930", "삼성전자", Market.KRX_KOSPI);
            WatchlistStore.AddSymbol(db, "🤖 AI", "NVDA", "엔비디아", Market.US_NASDAQ);   // 다른(빌트인) 그룹
            WatchlistStore.RemoveCategory(db, "A");
            Assert.Equal(0, WatchlistStore.CountSymbols(db, "A"));
            Assert.Equal(1, WatchlistStore.CountSymbols(db, "🤖 AI"));   // 동일 심볼 아니어도 다른 그룹 보존
        });

        // ── 종목 추가/삭제 ────────────────────────────────────────────────────────
        [Fact]
        public void AddSymbol_AssignsIncrementingSortOrder_LoadInInsertOrder() => WithDb(db =>
        {
            WatchlistStore.AddSymbol(db, "💎 반도체", "005930", "삼성전자", Market.KRX_KOSPI);
            WatchlistStore.AddSymbol(db, "💎 반도체", "000660", "SK하이닉스", Market.KRX_KOSPI);
            WatchlistStore.AddSymbol(db, "💎 반도체", "NVDA", "엔비디아", Market.US_NASDAQ);

            var syms = WatchlistStore.LoadSymbols(db, "💎 반도체").Select(t => t.sym).ToList();
            Assert.Equal(new[] { "005930", "000660", "NVDA" }, syms);   // 삽입 순서 유지

            var orders = db.Query("SELECT SortOrder FROM Watchlist WHERE GroupName=@g ORDER BY SortOrder",
                new() { ["@g"] = "💎 반도체" });
            Assert.Equal(new[] { 0, 1, 2 },
                orders.Rows.Cast<System.Data.DataRow>().Select(r => Safe.Int(r["SortOrder"])).ToArray());
        });

        [Fact]
        public void AddSymbol_Reinsert_IsIdempotent_UpdatesNameKeepsOrder() => WithDb(db =>
        {
            WatchlistStore.AddSymbol(db, "🤖 AI", "NVDA", "엔비디아", Market.US_NASDAQ);
            WatchlistStore.AddSymbol(db, "🤖 AI", "PLTR", "팔란티어", Market.US_NASDAQ);
            // NVDA 재추가(이름 변경) → 행 늘지 않고 이름만 갱신, SortOrder 보존(맨 끝으로 안 밀림)
            WatchlistStore.AddSymbol(db, "🤖 AI", "NVDA", "엔비디아Corp", Market.US_NASDAQ);

            var rows = WatchlistStore.LoadSymbols(db, "🤖 AI");
            Assert.Equal(2, rows.Count);                       // 중복행 없음
            Assert.Equal("NVDA", rows[0].sym);                 // 여전히 0번(순서 보존)
            Assert.Equal("엔비디아Corp", rows[0].name);         // 이름만 갱신
        });

        [Fact]
        public void AddSymbol_LowercaseInput_IsNormalizedUpper() => WithDb(db =>
        {
            WatchlistStore.AddSymbol(db, "🤖 AI", "nvda", "엔비디아", Market.US_NASDAQ);
            Assert.Equal("NVDA", WatchlistStore.LoadSymbols(db, "🤖 AI")[0].sym);
        });

        [Fact]
        public void AddSymbol_AfterRemoveMiddle_KeepsDistinctSortOrder_NoStaleJump() => WithDb(db =>
        {
            // 회귀: SortOrder 를 COUNT(*) 로 잡으면 중간 삭제 후 sparse 가 돼 새 행이 생존 행과 같은 값으로 충돌
            //  → 다음 plain 로드에서 기존 행이 튄다. NextSortOrder=MAX+1 로 단조 증가·distinct 보장.
            WatchlistStore.AddSymbol(db, "🤖 AI", "A", "a", Market.US_NASDAQ);   // SortOrder 0
            WatchlistStore.AddSymbol(db, "🤖 AI", "B", "b", Market.US_NASDAQ);   // 1
            WatchlistStore.AddSymbol(db, "🤖 AI", "C", "c", Market.US_NASDAQ);   // 2
            WatchlistStore.AddSymbol(db, "🤖 AI", "D", "d", Market.US_NASDAQ);   // 3
            WatchlistStore.RemoveSymbol(db, "🤖 AI", "B");   // sparse: {A:0, C:2, D:3}
            WatchlistStore.RemoveSymbol(db, "🤖 AI", "C");   // sparse: {A:0, D:3}  (COUNT=2 였다면 다음 추가가 D 와 충돌)
            WatchlistStore.AddSymbol(db, "🤖 AI", "E", "e", Market.US_NASDAQ);   // MAX(3)+1=4 (COUNT 였다면 2 → C 잔존 시 충돌)
            WatchlistStore.AddSymbol(db, "🤖 AI", "F", "f", Market.US_NASDAQ);   // 5

            var orders = db.Query("SELECT SortOrder FROM Watchlist WHERE GroupName=@g", new() { ["@g"] = "🤖 AI" })
                .Rows.Cast<System.Data.DataRow>().Select(r => Safe.Int(r["SortOrder"])).ToArray();
            Assert.Equal(orders.Length, orders.Distinct().Count());   // 중복 SortOrder 0 (충돌 회귀 차단)
            Assert.Equal(new[] { "A", "D", "E", "F" },                // 신규(E,F)가 장기존속(A,D) 앞으로 튀지 않음
                WatchlistStore.LoadSymbols(db, "🤖 AI").Select(t => t.sym).ToArray());
        });

        [Fact]
        public void AddSymbol_Reinsert_RefreshesStoredMarket() => WithDb(db =>
        {
            // ON CONFLICT 가 Market 도 갱신 — 잘못 분류된 시장이 재추가로 교정돼야(SaveHolding stale-market 수정과 동궤).
            WatchlistStore.AddSymbol(db, "🤖 AI", "NVDA", "엔비디아", Market.US_NYSE);    // 오분류 가정
            WatchlistStore.AddSymbol(db, "🤖 AI", "NVDA", "엔비디아", Market.US_NASDAQ);  // 교정
            var dt = db.Query("SELECT Market FROM Watchlist WHERE Symbol='NVDA' AND GroupName=@g", new() { ["@g"] = "🤖 AI" });
            Assert.Equal((int)Market.US_NASDAQ, Safe.Int(dt.Rows[0]["Market"]));   // 재추가 시 시장 갱신(미갱신 회귀 차단)
        });

        [Fact]
        public void RemoveSymbol_DropsOnlyTargetInGroup() => WithDb(db =>
        {
            WatchlistStore.AddSymbol(db, "🤖 AI", "NVDA", "엔비디아", Market.US_NASDAQ);
            WatchlistStore.AddSymbol(db, "🤖 AI", "PLTR", "팔란티어", Market.US_NASDAQ);
            WatchlistStore.RemoveSymbol(db, "🤖 AI", "NVDA");
            var syms = WatchlistStore.LoadSymbols(db, "🤖 AI").Select(t => t.sym).ToList();
            Assert.Equal(new[] { "PLTR" }, syms);
        });

        [Fact]
        public void Symbol_SameTicker_DifferentGroups_AreIndependent() => WithDb(db =>
        {
            // PK(Symbol,GroupName) — 같은 티커가 서로 다른 그룹에 공존, 한쪽 삭제가 다른 쪽 불변.
            WatchlistStore.AddSymbol(db, "🤖 AI", "NVDA", "엔비디아", Market.US_NASDAQ);
            WatchlistStore.AddSymbol(db, "💎 반도체", "NVDA", "엔비디아", Market.US_NASDAQ);
            WatchlistStore.RemoveSymbol(db, "🤖 AI", "NVDA");
            Assert.Equal(0, WatchlistStore.CountSymbols(db, "🤖 AI"));
            Assert.Equal(1, WatchlistStore.CountSymbols(db, "💎 반도체"));
        });

        // ── 드래그 재정렬(위·아래 변경) ────────────────────────────────────────────
        [Fact]
        public void Reorder_WritesDistinctSortOrder_AndLoadReflectsNewOrder() => WithDb(db =>
        {
            WatchlistStore.AddSymbol(db, "🇰🇷 국내주식", "005930", "삼성전자", Market.KRX_KOSPI);
            WatchlistStore.AddSymbol(db, "🇰🇷 국내주식", "000660", "SK하이닉스", Market.KRX_KOSPI);
            WatchlistStore.AddSymbol(db, "🇰🇷 국내주식", "035720", "카카오", Market.KRX_KOSPI);

            // 카카오를 맨 위로(위로 이동) — 새 순서 [035720, 005930, 000660]
            WatchlistStore.Reorder(db, "🇰🇷 국내주식", new[] { "035720", "005930", "000660" });

            var syms = WatchlistStore.LoadSymbols(db, "🇰🇷 국내주식").Select(t => t.sym).ToList();
            Assert.Equal(new[] { "035720", "005930", "000660" }, syms);   // 다음 로드에 반영

            // SortOrder 가 0,1,2 distinct(전부 0 이던 초기상태 회귀 차단)
            var orders = db.Query("SELECT SortOrder FROM Watchlist WHERE GroupName=@g",
                new() { ["@g"] = "🇰🇷 국내주식" })
                .Rows.Cast<System.Data.DataRow>().Select(r => Safe.Int(r["SortOrder"])).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { 0, 1, 2 }, orders);   // 중복 없음
        });

        [Fact]
        public void Reorder_PersistsAcrossReopen()
        {
            var path = Path.Combine(Path.GetTempPath(), "smwl_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var db = new LocalDatabase(path))
                {
                    WatchlistStore.AddSymbol(db, "🤖 AI", "A", "a", Market.US_NASDAQ);
                    WatchlistStore.AddSymbol(db, "🤖 AI", "B", "b", Market.US_NASDAQ);
                    WatchlistStore.Reorder(db, "🤖 AI", new[] { "B", "A" });   // 뒤집기
                }
                using (var db2 = new LocalDatabase(path))
                    Assert.Equal(new[] { "B", "A" },
                        WatchlistStore.LoadSymbols(db2, "🤖 AI").Select(t => t.sym).ToArray());
            }
            finally { foreach (var s in new[] { "", "-wal", "-shm" }) { try { File.Delete(path + s); } catch { } } }
        }

        [Fact]
        public void Reorder_NullOrEmpty_IsNoOp() => WithDb(db =>
        {
            WatchlistStore.AddSymbol(db, "🤖 AI", "A", "a", Market.US_NASDAQ);
            WatchlistStore.Reorder(db, "🤖 AI", null!);          // null 안전
            WatchlistStore.Reorder(db, "🤖 AI", Array.Empty<string>());
            Assert.Equal(new[] { "A" }, WatchlistStore.LoadSymbols(db, "🤖 AI").Select(t => t.sym).ToArray());
        });

        [Fact]
        public void CountSymbols_EmptyGroup_IsZero() => WithDb(db =>
            Assert.Equal(0, WatchlistStore.CountSymbols(db, "🤖 AI")));
    }
}
