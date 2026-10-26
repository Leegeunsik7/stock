// ============================================================================
// PlutoCandleRecorder — v12.18.86-DEV: 스웜 실데이터 소스 데코레이터 → portfolio.db Candles 일봉 Upsert(웜스타트 PlutoLocalCandleSource 원천 결선)
//
//   내부 소스 Fetch 결과를 그대로 반환(판정 · 표본 무변경). Ok 일 때만 표본의 '일봉'을 Candles(Symbol, Date) 키로 Upsert(중복 = 갱신 · 동일 값 = 기록 0):
//     일봉 원천 = sample.DailyBars(업비트 ④ 일봉 수확) 우선 → 없으면 Cadence = Daily 소스(KIS 국내/해외 일봉)의 sample.Bars → 그 외(분 봉) = 적재 0
//     (★분 봉을 일봉으로 둔갑 0 — 웜스타트 리플레이는 일봉 손절/충격 임계 기준).
//   행 = 유효 봉만(PlutoOhlcvBar.IsValid ∧ 시각 > 0) · Date = 봉 시각 UTC 날짜(yyyy-MM-dd) · 가격 단위 환원(KRX 원 · US 센트 ÷ 100 = 달러 · 코인 raw ÷ 10^8) ·
//     Symbol = 유니버스 코드(KRX 6자리 · US 티커 · 'KRW-XXX') = PlutoLocalCandleSource.Classify 판별 형식 그대로.
//   기록 = 스웜 워커 스레드(BelowNormal 백그라운드 · UI/체결 핫패스 무간섭) · 종목당 MinRecordInterval(30분) 1회 · 단일 트랜잭션.
//   DB 장애 = 그 묶음 롤백 + Faults 계수 · 내부 소스 결과 불변(스웜 레인 무영향) · 재시도/큐잉 0(다음 간격에 새 표본으로 갱신).
//   ★데이터 미수신(NoData · RateLimited · Faulted · NotConnected) = 기록 0 — 가짜 캔들 생성 0(웜스타트 NoData 정직 유지).
//   ★liveArmed:false — 시세 표본 저장만(주문 · 무장 · 브로커 계좌 경로 0) · QuantumVault/피어 접근 0.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using MS2E.Infrastructure;
using StockManager.Engines.Cognitive.Screening;
using StockManager.Engines.Pricing;

namespace StockManager.Adapters
{
    /// <summary>내부 소스 sample.Bars 봉 주기(Daily = 일봉 그대로 적재 · Intraday = DailyBars 만 적재).</summary>
    public enum PlutoCandleCadence : byte
    {
        Daily = 0,
        Intraday = 1,
    }

    public sealed class PlutoCandleRecorder : IPlutoAssetDataSource
    {
        public static readonly TimeSpan MinRecordInterval = TimeSpan.FromMinutes(30);

        public const string UpsertSql =
            "INSERT INTO Candles (Symbol, Date, Open, High, Low, Close, Volume) VALUES (@s, @d, @o, @h, @l, @c, @v) " +
            "ON CONFLICT(Symbol, Date) DO UPDATE SET Open = excluded.Open, High = excluded.High, Low = excluded.Low, Close = excluded.Close, Volume = excluded.Volume " +
            "WHERE Open IS NOT excluded.Open OR High IS NOT excluded.High OR Low IS NOT excluded.Low OR Close IS NOT excluded.Close OR Volume IS NOT excluded.Volume";

        private readonly IPlutoAssetDataSource _inner;
        private readonly LocalDatabase _db;
        private readonly PlutoCandleCadence _cadence;
        private readonly Func<long> _nowMs;
        private readonly Dictionary<string, long> _recordedAt = new(StringComparer.Ordinal);   // 단일 워커 전용
        private long _rows, _batches, _faults;

        public PlutoCandleRecorder(IPlutoAssetDataSource inner, LocalDatabase db, PlutoCandleCadence cadence, Func<long>? nowMs = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _cadence = cadence;
            _nowMs = nowMs ?? (static () => Environment.TickCount64);
        }

        public PlutoScreenMarket Market => _inner.Market;
        public bool IsConnected => _inner.IsConnected;
        public string Label => _inner.Label;
        public IPlutoAssetDataSource Inner => _inner;

        /// <summary>Upsert 시도 행 누계(신규 + 갱신 + 동일 값).</summary>
        public long RecordedRows => Interlocked.Read(ref _rows);
        public long RecordedBatches => Interlocked.Read(ref _batches);
        /// <summary>DB 장애로 롤백된 묶음 누계.</summary>
        public long Faults => Interlocked.Read(ref _faults);

        public PlutoFetchOutcome Fetch(PlutoUniverseEntry entry, PlutoAssetSample sample, CancellationToken ct)
        {
            var r = _inner.Fetch(entry, sample, ct);
            if (r == PlutoFetchOutcome.Ok) Record(entry.Code, sample);
            return r;
        }

        private void Record(string code, PlutoAssetSample sample)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            ReadOnlySpan<PlutoOhlcvBar> bars = sample.DailyBarCount > 0
                ? sample.DailyBars.AsSpan(0, Math.Min(sample.DailyBarCount, sample.DailyBars.Length))
                : _cadence == PlutoCandleCadence.Daily ? sample.Bars.AsSpan(0, Math.Clamp(sample.BarCount, 0, sample.Bars.Length)) : default;
            if (bars.IsEmpty) return;
            long now = _nowMs();
            if (_recordedAt.TryGetValue(code, out long at) && now - at < (long)MinRecordInterval.TotalMilliseconds) return;

            var rows = Rows(code, _inner.Market, bars);
            if (rows.Count == 0) return;
            _recordedAt[code] = now;
            try
            {
                _db.RunInTransaction(() => { foreach (var p in rows) _db.Execute(UpsertSql, p); });
                Interlocked.Add(ref _rows, rows.Count);
                Interlocked.Increment(ref _batches);
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref _faults) == 1) Log.Warn($"[Pluto] Candles 적재 실패(롤백 · 스웜 무영향): {ex.GetType().Name} {ex.Message}");
            }
        }

        /// <summary>유효 봉 → Upsert 파라미터(같은 날짜 중복 = 마지막 봉 우선 · 무효 봉 = 버림).</summary>
        public static List<Dictionary<string, object>> Rows(string symbol, PlutoScreenMarket market, ReadOnlySpan<PlutoOhlcvBar> bars)
        {
            double px = market switch { PlutoScreenMarket.Crypto => UpbitTickAligner.Scale, PlutoScreenMarket.Us => 100d, _ => 1d };
            double vol = market == PlutoScreenMarket.Crypto ? UpbitTickAligner.Scale : 1d;
            var byDate = new Dictionary<string, Dictionary<string, object>>(bars.Length, StringComparer.Ordinal);
            var order = new List<string>(bars.Length);
            foreach (ref readonly var b in bars)
            {
                if (!b.IsValid || b.Open <= 0 || b.TimeUtcTicks <= 0 || b.TimeUtcTicks > DateTime.MaxValue.Ticks) continue;
                string d = new DateTime(b.TimeUtcTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (!byDate.ContainsKey(d)) order.Add(d);
                byDate[d] = new Dictionary<string, object>
                {
                    ["@s"] = symbol, ["@d"] = d,
                    ["@o"] = b.Open / px, ["@h"] = b.High / px, ["@l"] = b.Low / px, ["@c"] = b.Close / px,
                    ["@v"] = market == PlutoScreenMarket.Crypto ? b.Volume / vol : (object)b.Volume,
                };
            }
            var list = new List<Dictionary<string, object>>(order.Count);
            foreach (var d in order) list.Add(byDate[d]);
            return list;
        }
    }
}
