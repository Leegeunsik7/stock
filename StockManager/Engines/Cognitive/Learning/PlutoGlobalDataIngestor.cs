// ============================================================================
// PlutoGlobalDataIngestor — v12.18.77-DEV: 플루토 지속 학습 Track B — 글로벌 거시 지표 + 뉴스/공시 감성 지수 비동기 수집 허브(Fail-Safe Fallback)
//
//   관측 1건(PlutoMacroSnapshot · 값 타입) = VIX(×100) · 미 10년물 변화(bp) · 달러 인덱스 변화(bp) · 원/달러 변화(bp) · 지수 추세(bp) · 감성 지수(−100~+100) ·
//     필드 존재 마스크(PlutoMacroField) · 기준 시각(UTC ticks) · 순번.
//   입력 경로: ① Ingest(UTF-8 JSON) — Utf8JsonReader 타입드 게터(예외 = JsonException 만 catch) · 알 수 없는 키 = 무시 ·
//       "headlines" 문자열 배열 = PlutoCatalystAnalyzer.Classify(기존 규칙 · Wire 출처) 로 노이즈 제거 후 점수 × 확신도 가중 평균 → 감성 지수.
//     ② Ingest(in snapshot) — 이미 구조화된 관측. ③ RunAsync(IPlutoMacroSource) — 주기 폴링(수집원 미결선 = 호출 0 · 네트워크 0).
//   Fail-Safe: 형식 오류 · 범위 밖 값(VIX 0~200 · 변화 ±5000bp · 감성 ±100) · 필드 0개 = 관측 전체 거부(부분 오염 0) → 직전 안정값 유지(거부 계수).
//     정상 관측 = 직전 안정값에 필드 단위 병합(빠진 필드 = 직전값 유지). 수집원 예외/시간 초과 = 장애 계수 · 직전 안정값 유지 · 다음 주기 재시도(큐잉 0).
//     기준 시각 + StaleAfter 경과 = Stale 상태(값은 유지 · UI 가 '직전 안정값' 으로 정직 표기).
//   ★Track B 전용(콜드 · 체결 스레드 무간섭). 스냅샷 게시 = 버전 브래킷 seqlock(TryRead 할당 0). ★실 거시 피드 미결선 — 수집원은 주입식(가짜 값 날조 0).
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using StockManager.Engines.Cognitive.News;

namespace StockManager.Engines.Cognitive.Learning
{
    [Flags]
    public enum PlutoMacroField : byte
    {
        None = 0,
        Vix = 1,
        Us10y = 2,
        Dollar = 4,
        UsdKrw = 8,
        EquityTrend = 16,
        Sentiment = 32,
        All = Vix | Us10y | Dollar | UsdKrw | EquityTrend | Sentiment,
    }

    public enum PlutoIngestHealth : byte
    {
        /// <summary>안정값 0건(수집원 미결선 또는 첫 관측 전).</summary>
        NoData = 0,
        Live = 1,
        /// <summary>안정값 보유 · 기준 시각 경과(직전 안정값 유지 중).</summary>
        Stale = 2,
    }

    /// <summary>거시/감성 관측 1건(값 타입).</summary>
    public readonly struct PlutoMacroSnapshot
    {
        public const int MaxVixX100 = 20000, MaxChangeBp = 5000, MaxSentiment = 100;

        public readonly int VixX100, Us10yChangeBp, DollarIndexChangeBp, UsdKrwChangeBp, EquityTrendBp, SentimentIndex;
        public readonly PlutoMacroField Fields;
        public readonly long AsOfUtcTicks, Seq;

        public PlutoMacroSnapshot(PlutoMacroField fields, int vixX100, int us10yChangeBp, int dollarIndexChangeBp, int usdKrwChangeBp, int equityTrendBp, int sentimentIndex,
            long asOfUtcTicks, long seq = 0)
        {
            Fields = fields; VixX100 = vixX100; Us10yChangeBp = us10yChangeBp; DollarIndexChangeBp = dollarIndexChangeBp; UsdKrwChangeBp = usdKrwChangeBp;
            EquityTrendBp = equityTrendBp; SentimentIndex = sentimentIndex; AsOfUtcTicks = asOfUtcTicks; Seq = seq;
        }

        public bool Has(PlutoMacroField f) => (Fields & f) == f;

        /// <summary>무결성: 필드 ≥ 1 ∧ 정의된 비트만 ∧ 존재 필드 범위 내 ∧ 기준 시각 양수.</summary>
        public bool IsWellFormed =>
            Fields != PlutoMacroField.None && (Fields & ~PlutoMacroField.All) == 0 && AsOfUtcTicks > 0
            && (!Has(PlutoMacroField.Vix) || VixX100 is >= 0 and <= MaxVixX100)
            && (!Has(PlutoMacroField.Us10y) || Math.Abs(Us10yChangeBp) <= MaxChangeBp)
            && (!Has(PlutoMacroField.Dollar) || Math.Abs(DollarIndexChangeBp) <= MaxChangeBp)
            && (!Has(PlutoMacroField.UsdKrw) || Math.Abs(UsdKrwChangeBp) <= MaxChangeBp)
            && (!Has(PlutoMacroField.EquityTrend) || Math.Abs(EquityTrendBp) <= MaxChangeBp)
            && (!Has(PlutoMacroField.Sentiment) || Math.Abs(SentimentIndex) <= MaxSentiment);

        /// <summary>직전 안정값(prev)에 이 관측을 필드 단위로 병합(빠진 필드 = prev 유지).</summary>
        public PlutoMacroSnapshot MergeOnto(in PlutoMacroSnapshot prev, long seq) => new(
            prev.Fields | Fields,
            Has(PlutoMacroField.Vix) ? VixX100 : prev.VixX100,
            Has(PlutoMacroField.Us10y) ? Us10yChangeBp : prev.Us10yChangeBp,
            Has(PlutoMacroField.Dollar) ? DollarIndexChangeBp : prev.DollarIndexChangeBp,
            Has(PlutoMacroField.UsdKrw) ? UsdKrwChangeBp : prev.UsdKrwChangeBp,
            Has(PlutoMacroField.EquityTrend) ? EquityTrendBp : prev.EquityTrendBp,
            Has(PlutoMacroField.Sentiment) ? SentimentIndex : prev.SentimentIndex,
            Math.Max(prev.AsOfUtcTicks, AsOfUtcTicks), seq);
    }

    /// <summary>거시/감성 수집원(UTF-8 JSON 1건 반환 · 실패 = 예외). 실 피드 어댑터는 호스트가 주입한다.</summary>
    public interface IPlutoMacroSource
    {
        string Name { get; }
        Task<byte[]> FetchAsync(CancellationToken ct);
    }

    public sealed class PlutoGlobalDataIngestor
    {
        public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(30);
        public const int MaxHeadlines = 64;

        private readonly Func<long> _utcNow;
        private readonly long _staleAfterTicks;
        private readonly object _writeGate = new();
        private long _ver;
        private PlutoMacroSnapshot _stable;
        private long _seq, _accepted, _rejected, _sourceFailures, _polls;

        public PlutoGlobalDataIngestor(Func<long>? utcNowTicks = null, TimeSpan? staleAfter = null)
        {
            _utcNow = utcNowTicks ?? (() => DateTime.UtcNow.Ticks);
            _staleAfterTicks = (staleAfter ?? DefaultStaleAfter).Ticks;
            if (_staleAfterTicks <= 0) throw new ArgumentOutOfRangeException(nameof(staleAfter));
        }

        public long Accepted => Interlocked.Read(ref _accepted);
        public long Rejected => Interlocked.Read(ref _rejected);
        public long SourceFailures => Interlocked.Read(ref _sourceFailures);
        public long Polls => Interlocked.Read(ref _polls);
        /// <summary>수집 루프에 결선된 수집원 이름(null = 미결선).</summary>
        public string? SourceName { get; private set; }

        public PlutoIngestHealth Health
        {
            get
            {
                if (!TryRead(out var s) || s.Fields == PlutoMacroField.None) return PlutoIngestHealth.NoData;
                return _utcNow() - s.AsOfUtcTicks > _staleAfterTicks ? PlutoIngestHealth.Stale : PlutoIngestHealth.Live;
            }
        }

        /// <summary>직전 안정값 판독(할당 0). 관측 0건 = false.</summary>
        public bool TryRead(out PlutoMacroSnapshot snapshot)
        {
            for (int k = 0; k < 1024; k++)
            {
                long v1 = Volatile.Read(ref _ver);
                if ((v1 & 1) != 0) { Thread.SpinWait(8); continue; }
                snapshot = _stable;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v1) return snapshot.Fields != PlutoMacroField.None;
            }
            snapshot = default;
            return false;
        }

        /// <summary>구조화 관측 수집. 무결성 위반 = 거부(직전 안정값 유지).</summary>
        public bool Ingest(in PlutoMacroSnapshot observation)
        {
            if (!observation.IsWellFormed)
            {
                Interlocked.Increment(ref _rejected);
                return false;
            }
            lock (_writeGate)
            {
                var merged = observation.MergeOnto(in _stable, ++_seq);
                Volatile.Write(ref _ver, _ver + 1);
                _stable = merged;
                Volatile.Write(ref _ver, _ver + 1);
            }
            Interlocked.Increment(ref _accepted);
            return true;
        }

        /// <summary>UTF-8 JSON 관측 수집. 파싱 실패 · 범위 밖 = 거부(직전 안정값 유지).</summary>
        public bool Ingest(ReadOnlySpan<byte> utf8Json)
        {
            if (!TryParse(utf8Json, _utcNow(), out var s))
            {
                Interlocked.Increment(ref _rejected);
                return false;
            }
            return Ingest(in s);
        }

        /// <summary>
        /// JSON 관측 파싱(Utf8JsonReader 타입드 게터). 키: vix(실수) · us10yChangeBp · dxyChangeBp · usdKrwChangeBp · equityTrendBp · sentiment(정수) ·
        /// asOfUtc(ISO-8601 · 없음 = nowUtcTicks) · headlines(문자열 배열 → 감성 지수 · sentiment 키가 있으면 sentiment 우선).
        /// 최상위 객체 아님 · 타입 불일치 · 범위 밖 · 필드 0개 = false.
        /// </summary>
        public static bool TryParse(ReadOnlySpan<byte> utf8Json, long nowUtcTicks, out PlutoMacroSnapshot snapshot)
        {
            snapshot = default;
            var f = PlutoMacroField.None;
            int vix = 0, us10y = 0, dxy = 0, krw = 0, trend = 0, sentiment = 0;
            long asOf = nowUtcTicks;
            long hlWeighted = 0, hlWeight = 0;
            int hlCount = 0;
            try
            {
                var r = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = 4 });
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
                while (r.Read())
                {
                    if (r.TokenType == JsonTokenType.EndObject) break;
                    if (r.TokenType != JsonTokenType.PropertyName) return false;
                    if (r.ValueTextEquals("vix"u8))
                    {
                        if (!r.Read() || r.TokenType != JsonTokenType.Number || !r.TryGetDouble(out double d) || !double.IsFinite(d) || d < 0 || d > PlutoMacroSnapshot.MaxVixX100 / 100.0) return false;
                        vix = (int)Math.Round(d * 100); f |= PlutoMacroField.Vix;
                    }
                    else if (r.ValueTextEquals("us10yChangeBp"u8)) { if (!ReadBp(ref r, PlutoMacroSnapshot.MaxChangeBp, out us10y)) return false; f |= PlutoMacroField.Us10y; }
                    else if (r.ValueTextEquals("dxyChangeBp"u8)) { if (!ReadBp(ref r, PlutoMacroSnapshot.MaxChangeBp, out dxy)) return false; f |= PlutoMacroField.Dollar; }
                    else if (r.ValueTextEquals("usdKrwChangeBp"u8)) { if (!ReadBp(ref r, PlutoMacroSnapshot.MaxChangeBp, out krw)) return false; f |= PlutoMacroField.UsdKrw; }
                    else if (r.ValueTextEquals("equityTrendBp"u8)) { if (!ReadBp(ref r, PlutoMacroSnapshot.MaxChangeBp, out trend)) return false; f |= PlutoMacroField.EquityTrend; }
                    else if (r.ValueTextEquals("sentiment"u8)) { if (!ReadBp(ref r, PlutoMacroSnapshot.MaxSentiment, out sentiment)) return false; f |= PlutoMacroField.Sentiment; }
                    else if (r.ValueTextEquals("asOfUtc"u8))
                    {
                        if (!r.Read() || r.TokenType != JsonTokenType.String || !r.TryGetDateTimeOffset(out var dto)) return false;
                        asOf = dto.UtcTicks;
                    }
                    else if (r.ValueTextEquals("headlines"u8))
                    {
                        if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return false;
                        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                        {
                            if (r.TokenType != JsonTokenType.String) return false;
                            if (++hlCount > MaxHeadlines) continue;                          // 상한 초과분 = 폐기(큐잉 0)
                            var v = PlutoCatalystAnalyzer.Classify(PlutoSource.Wire, r.GetString() ?? "");
                            if (v.IsNoise) continue;
                            hlWeighted += (long)v.Score * v.Confidence;
                            hlWeight += v.Confidence;
                        }
                        if (r.TokenType != JsonTokenType.EndArray) return false;
                    }
                    else
                    {
                        if (!r.Read()) return false;
                        r.Skip();                                                           // 알 수 없는 키 = 무시
                    }
                }
                if (r.TokenType != JsonTokenType.EndObject) return false;
            }
            catch (JsonException) { return false; }
            catch (InvalidOperationException) { return false; }                             // 헤드라인 문자열 내부 비 UTF-8(GetString 지연 검증) — 콜드 경로 한정
            if ((f & PlutoMacroField.Sentiment) == 0 && hlWeight > 0)
            {
                sentiment = (int)Math.Clamp(hlWeighted / hlWeight, -PlutoMacroSnapshot.MaxSentiment, PlutoMacroSnapshot.MaxSentiment);
                f |= PlutoMacroField.Sentiment;
            }
            snapshot = new PlutoMacroSnapshot(f, vix, us10y, dxy, krw, trend, sentiment, asOf);
            return snapshot.IsWellFormed;
        }

        private static bool ReadBp(ref Utf8JsonReader r, int max, out int value)
        {
            value = 0;
            if (!r.Read() || r.TokenType != JsonTokenType.Number || !r.TryGetInt32(out value)) return false;
            return Math.Abs((long)value) <= max;
        }

        /// <summary>
        /// 주기 수집 루프(Track B). 수집원 1건 · 회당 시간 제한 · 예외/시간 초과 = 장애 계수 + 직전 안정값 유지(재시도 = 다음 주기뿐 · 큐잉 0).
        /// 취소 = 정상 종료.
        /// </summary>
        public async Task RunAsync(IPlutoMacroSource source, TimeSpan interval, CancellationToken ct, TimeSpan? fetchTimeout = null)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            SourceName = source.Name;
            var timeout = fetchTimeout ?? TimeSpan.FromSeconds(10);
            using var timer = new PeriodicTimer(interval);
            do
            {
                Interlocked.Increment(ref _polls);
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(timeout);
                    byte[] body = await source.FetchAsync(cts.Token).ConfigureAwait(false);
                    if (body is null || body.Length == 0 || !Ingest(body)) Interlocked.Increment(ref _sourceFailures);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception)
                {
                    Interlocked.Increment(ref _sourceFailures);                              // 네트워크 장애 · 시간 초과 = 직전 안정값 유지
                }
            }
            while (await WaitNext(timer, ct).ConfigureAwait(false));
        }

        private static async Task<bool> WaitNext(PeriodicTimer timer, CancellationToken ct)
        {
            try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
    }
}
