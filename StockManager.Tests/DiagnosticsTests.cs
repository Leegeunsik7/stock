// ============================================================================
// DiagnosticsTests - 신선도/경과 계산 + 상태 행 집계 (순수, 임시 DB)
// ============================================================================
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class DiagnosticsTests
    {
        private static readonly DateTime Now = new(2026, 6, 14, 12, 0, 0);

        [Fact]
        public void Freshness_Cases()
        {
            Assert.StartsWith("⏳", Diagnostics.Freshness("", 24, Now));                          // 미실행
            Assert.StartsWith("✅", Diagnostics.Freshness(Now.AddHours(-2).ToString("o"), 24, Now)); // 2h 전·24h내
            Assert.StartsWith("⚠️", Diagnostics.Freshness(Now.AddHours(-30).ToString("o"), 24, Now)); // 30h 전·지연
        }

        [Fact]
        public void TimeAgo_Cases()
        {
            Assert.Equal("실행 전", Diagnostics.TimeAgo(null, Now));
            Assert.Equal("방금", Diagnostics.TimeAgo(Now.AddSeconds(-20).ToString("o"), Now));
            Assert.Equal("3시간 전", Diagnostics.TimeAgo(Now.AddHours(-3).ToString("o"), Now));
            Assert.Equal("2일 전", Diagnostics.TimeAgo(Now.AddDays(-2).ToString("o"), Now));
        }

        [Fact]
        public void BuildRows_ReflectsEnabledFlags()
        {
            var path = Path.Combine(Path.GetTempPath(), "smdg_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using var db = new LocalDatabase(path);
                db.SetSetting("Regime.Enabled", "true");
                db.SetSetting("Regime.LastRun", Now.AddHours(-1).ToString("o"));
                // Drift는 끔(기본)

                var rows = Diagnostics.BuildRows(db, Now.AddSeconds(-10), Now);
                var regime = rows.First(r => r.Area == "레짐 감시");
                Assert.StartsWith("✅", regime.Status);                      // 켜짐 + 1h 전 = 정상
                var drift = rows.First(r => r.Area == "드리프트 트리거");
                Assert.StartsWith("⚪", drift.Status);                        // 꺼짐
                Assert.Contains(rows, r => r.Area == "DB 크기");
                Assert.Contains(rows, r => r.Area == "LLM 백엔드");
                Assert.StartsWith("✅", rows.First(r => r.Area == "엔진 하트비트").Status);  // 10초 전 → 정상
            }
            finally { foreach (var s in new[] { "", "-wal", "-shm" }) { try { File.Delete(path + s); } catch { } } }
        }
    }
}
