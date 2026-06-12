// ============================================================================
// ForecastLog - 라이브 예측 로그 🄵 (로컬 DB) (v3.26.0)
//  실제로 발행한 정량 예측을 발행 시점에 저장하고(만기·구간 포함), 만기가 지나면
//  현재가로 실현치를 채워 'Resolved'로 만든다. ForecastScore가 이를 채점한다.
//  설정 Forecast.Log(JSON 리스트)에 보관. 최대 500건 유지.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace StockManager.Common
{
    public class LiveForecast
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string IssuedAt { get; set; } = "";
        public string ResolveAt { get; set; } = "";
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public int Market { get; set; }
        public string Kind { get; set; } = "StatBand";
        public int HorizonDays { get; set; }
        public double BasePrice { get; set; }
        public double PredictedPct { get; set; }
        public double LowPct { get; set; }
        public double HighPct { get; set; }
        public double NominalCoverage { get; set; }
        public bool Resolved { get; set; }
        public double RealizedPct { get; set; }
    }

    public static class ForecastLog
    {
        private const string Key = "Forecast.Log";

        public static List<LiveForecast> Load(LocalDatabase db)
        {
            try
            {
                var j = db.GetSetting(Key);
                if (!string.IsNullOrWhiteSpace(j))
                    return JsonSerializer.Deserialize<List<LiveForecast>>(j) ?? new();
            }
            catch { }
            return new();
        }

        public static void Save(LocalDatabase db, List<LiveForecast> list)
        {
            try
            {
                if (list.Count > 500) list = list.Skip(list.Count - 500).ToList();
                db.SetSetting(Key, JsonSerializer.Serialize(list));
            }
            catch { }
        }

        public static void Record(LocalDatabase db, LiveForecast f)
        {
            var l = Load(db); l.Add(f); Save(db, l);
        }

        /// <summary>만기 지난 미해결 예측을 현재가로 실현치 산정. 해결 건수 반환.</summary>
        public static int ResolveDue(LocalDatabase db, Func<string, Market, decimal> priceNow, DateTime now)
        {
            var l = Load(db); int resolved = 0;
            foreach (var f in l.Where(x => !x.Resolved))
            {
                if (!DateTime.TryParse(f.ResolveAt, out var ra) || ra > now) continue;
                decimal px = 0; try { px = priceNow(f.Symbol, (Market)f.Market); } catch { }
                if (px > 0 && f.BasePrice > 0)
                {
                    f.RealizedPct = ((double)px - f.BasePrice) / f.BasePrice * 100.0;
                    f.Resolved = true; resolved++;
                }
            }
            if (resolved > 0) Save(db, l);
            return resolved;
        }

        public static List<ResolvedForecast> ResolvedToScore(LocalDatabase db) =>
            Load(db).Where(f => f.Resolved).Select(f => new ResolvedForecast
            {
                Kind = f.Kind, HorizonDays = f.HorizonDays,
                PredictedPct = f.PredictedPct, LowPct = f.LowPct, HighPct = f.HighPct,
                NominalCoverage = f.NominalCoverage, RealizedPct = f.RealizedPct
            }).ToList();

        public static int PendingCount(LocalDatabase db) => Load(db).Count(f => !f.Resolved);
    }
}
