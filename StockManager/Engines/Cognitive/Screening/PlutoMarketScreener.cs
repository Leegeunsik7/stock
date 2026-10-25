// ============================================================================
// PlutoMarketScreener — v12.18.78-DEV: 플루토 멀티 마켓 종목 발굴 스크리너(Track B 백그라운드 · 콜드) → 시장별 Top 5 추천 스냅샷 게시
//
//   주기 Step 1회:
//     ① 시장별 유니버스 스캐너(PlutoUniverseScanner) 판독 — 아레나 엔진 시세 유니버스 × 기존 4대 브레인 판독(밸류에이션 버퍼 · 기술 · 미시구조 · 국면).
//     ② 학습 스웜 최근접 과거 국면(거시 공급자 · 없음 = 판정 전) → PlutoDiscoveryScorer 4대 팩터 0~100점 + 반대 논리 판정.
//     ③ 악마의 변호인 탈락(≥ 2) · 근거 부족 · 50점 미만 제외 → 점수 내림차순(동점 = 반대 논리 적은 순 → 종목 오름차순 · 결정적) 상위 5.
//     ④ 추천 수량 = 시작 자본 × PlutoDynamicSizer 비중(확신도 = 종합 점수 · 5~25%) ÷ 매도 1호가(내림 · 자본 부족 = 0).
//     ⑤ 불변 스냅샷 → PlutoRecommendationBoard.Publish(원자 참조 교체 · 세대 +1).
//   Start = PeriodicTimer 루프(예외 = Faulted 계수 · 루프 지속 · 체결 스레드 무간섭) · Dispose = 취소.
//   ★유니버스 = 아레나가 실제로 시세를 받는 종목만(시장 전 종목 아님 · 피드 밖 종목 날조 0). ★liveArmed:false · QuantumVault/피어 접근 0.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Microstructure;
using StockManager.Engines.Cognitive.Technical;
using StockManager.Engines.Cognitive.Valuation;

namespace StockManager.Engines.Cognitive.Screening
{
    /// <summary>유니버스 1회 판독(반환 = 채운 수). 스크리너 스텝 잠금 안에서만 호출(단일 스레드).</summary>
    public delegate int PlutoUniverseScanner(Span<PlutoScreenInputs> destination);

    public sealed class PlutoMarketScreener : IDisposable
    {
        public const int MaxUniverse = 128;

        private sealed class Universe
        {
            public readonly PlutoUniverseScanner Scan;
            public readonly long Capital;
            public Universe(PlutoUniverseScanner scan, long capital) { Scan = scan; Capital = capital; }
        }

        private readonly PlutoRecommendationBoard _board;
        private readonly Func<PlutoScreenMacro> _macro;
        private readonly Func<long> _utcNow;
        private readonly Universe?[] _universes = new Universe?[PlutoRecommendationSnapshot.Markets];
        private readonly PlutoScreenInputs[] _scratch = new PlutoScreenInputs[MaxUniverse];
        private readonly object _stepGate = new();
        private long _generation, _cycles, _faulted;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public PlutoMarketScreener(PlutoRecommendationBoard board, Func<PlutoScreenMacro>? macro = null, Func<long>? utcNow = null)
        {
            _board = board ?? throw new ArgumentNullException(nameof(board));
            _macro = macro ?? (static () => default);
            _utcNow = utcNow ?? (static () => DateTime.UtcNow.Ticks);
        }

        public PlutoRecommendationBoard Board => _board;
        public long Cycles => Interlocked.Read(ref _cycles);
        public long Faulted => Interlocked.Read(ref _faulted);
        public bool Running => Volatile.Read(ref _loop) is { IsCompleted: false };

        /// <summary>시장 유니버스 등록(같은 시장 재등록 = 교체). capital = 추천 수량 산정 기준 자본(KRX 원 · US 센트).</summary>
        public void AddUniverse(PlutoScreenMarket market, PlutoUniverseScanner scan, long capital)
        {
            ArgumentNullException.ThrowIfNull(scan);
            if (market is not (PlutoScreenMarket.Krx or PlutoScreenMarket.Us)) throw new ArgumentOutOfRangeException(nameof(market));
            if (capital <= 0) throw new ArgumentOutOfRangeException(nameof(capital));
            lock (_stepGate) _universes[(int)market] = new Universe(scan, capital);
        }

        /// <summary>스캔 1회 → 스냅샷 게시(동시 호출 = 직렬화).</summary>
        public PlutoRecommendationSnapshot Step()
        {
            lock (_stepGate)
            {
                var macro = _macro();
                var krx = ScanMarket(PlutoScreenMarket.Krx, in macro, out var krxTally);
                var us = ScanMarket(PlutoScreenMarket.Us, in macro, out var usTally);
                var snap = new PlutoRecommendationSnapshot(++_generation, _utcNow(), krx, krxTally, us, usTally);
                _board.Publish(snap);
                Interlocked.Increment(ref _cycles);
                return snap;
            }
        }

        private PlutoRecommendation[] ScanMarket(PlutoScreenMarket market, in PlutoScreenMacro macro, out PlutoScreenTally tally)
        {
            var u = _universes[(int)market];
            if (u is null) { tally = default; return Array.Empty<PlutoRecommendation>(); }
            int n = Math.Clamp(u.Scan(_scratch), 0, _scratch.Length);
            var picks = new List<(int Index, PlutoDiscoveryScore Score)>(n);
            int scanned = 0, vetoed = 0, insufficient = 0, below = 0;
            for (int i = 0; i < n; i++)
            {
                ref readonly var x = ref _scratch[i];
                if (x.Market != market || x.Symbol <= 0 || x.Price <= 0) continue;   // 오배선 · 무시세 = 유니버스 밖
                scanned++;
                var s = PlutoDiscoveryScorer.Score(in x, in macro);
                if (s.Insufficient) insufficient++;
                else if (s.Vetoed) vetoed++;
                else if (s.Total < PlutoDiscoveryScorer.MinRecommendScore) below++;
                else picks.Add((i, s));
            }
            var scratch = _scratch;
            picks.Sort((a, b) =>
            {
                int c = b.Score.Total.CompareTo(a.Score.Total);
                if (c != 0) return c;
                c = a.Score.RiskCount.CompareTo(b.Score.RiskCount);
                return c != 0 ? c : scratch[a.Index].Symbol.CompareTo(scratch[b.Index].Symbol);
            });
            int take = Math.Min(picks.Count, PlutoRecommendationSnapshot.TopN);
            var result = new PlutoRecommendation[take];
            for (int r = 0; r < take; r++)
            {
                var (idx, s) = picks[r];
                ref readonly var x = ref _scratch[idx];
                result[r] = new PlutoRecommendation(market, r + 1, x.Symbol, CodeOf(market, x.Symbol), x.Price, s,
                    Array.AsReadOnly(PlutoDiscoveryScorer.Reasons(in x, in macro, in s)), x.HasValuation, x.MarginOfSafetyBps, x.FairPrice, x.SeedProfile,
                    RecommendedQty(u.Capital, s.Total, x.Price), PlutoDiscoveryScorer.RiskText(s.Risks));
            }
            tally = new PlutoScreenTally(scanned, vetoed, insufficient, below);
            return result;
        }

        /// <summary>추천 수량(내림 · 자본 × 비중 &lt; 1주 = 0).</summary>
        public static long RecommendedQty(long capital, int score, long price)
        {
            if (capital <= 0 || price <= 0) return 0;
            long budget = (long)((Int128)capital * PlutoDynamicSizer.WeightBp(score, 0, 0) / 10000);
            return budget / price;
        }

        public static string CodeOf(PlutoScreenMarket m, long symbol)
            => m == PlutoScreenMarket.Us ? UsTechUniverse.Unpack(symbol) : symbol.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>백그라운드 스캔 루프 기동(중복 기동 = 무시).</summary>
        public void Start(TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            lock (_stepGate)
            {
                if (_loop is not null) return;
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                _loop = Task.Run(() => LoopAsync(interval, ct));
            }
        }

        private async Task LoopAsync(TimeSpan interval, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(interval);
            while (!ct.IsCancellationRequested)
            {
                try { Step(); }
                catch (Exception) { Interlocked.Increment(ref _faulted); }        // 스캔 장애 = 계수만(직전 스냅샷 유지 · 다음 주기 재시도)
                try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) return; }
                catch (OperationCanceledException) { return; }
            }
        }

        public void Dispose()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_stepGate) { cts = _cts; loop = _loop; _cts = null; }
            if (cts is null) return;
            cts.Cancel();
            try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            cts.Dispose();
        }
    }

    /// <summary>아레나 엔진 → 스크리너 유니버스 어댑터(시세 유니버스 × 기존 브레인 판독 · 콜드).</summary>
    public static class PlutoArenaUniverse
    {
        public static PlutoUniverseScanner Krx(KrxArenaEngine engine, PlutoValuationBuffer? valuation, Func<long>? utcNow = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            var quotes = new DuelQuote[KrxArenaEngine.MaxSymbols];
            var now = utcNow ?? (static () => DateTime.UtcNow.Ticks);
            return dest => Fill(dest, quotes, engine.CopyQuotes(quotes), PlutoScreenMarket.Krx, valuation, now(),
                engine.TryReadTechnical, engine.TryReadMicrostructure, engine.RegimeOf);
        }

        public static PlutoUniverseScanner Us(UsTechArenaEngine engine, PlutoValuationBuffer? valuation, Func<long>? utcNow = null)
        {
            ArgumentNullException.ThrowIfNull(engine);
            var quotes = new DuelQuote[UsTechArenaEngine.MaxSymbols];
            var now = utcNow ?? (static () => DateTime.UtcNow.Ticks);
            return dest => Fill(dest, quotes, engine.CopyQuotes(quotes), PlutoScreenMarket.Us, valuation, now(),
                engine.TryReadTechnical, engine.TryReadMicrostructure, engine.RegimeOf);
        }

        private delegate bool TechReader(long symbol, out PlutoTechnicalFactor factor);
        private delegate bool MicroReader(long symbol, out PlutoMicrostructureFactor factor);

        private static int Fill(Span<PlutoScreenInputs> dest, DuelQuote[] quotes, int n, PlutoScreenMarket market, PlutoValuationBuffer? valuation, long nowUtc,
            TechReader tech, MicroReader micro, Func<long, Regime.MarketRegime> regime)
        {
            int k = 0;
            for (int i = 0; i < n && k < dest.Length; i++)
            {
                ref readonly var q = ref quotes[i];
                if (q.Symbol <= 0 || q.AskWon <= 0) continue;
                PlutoValuationTag val = default;
                if (valuation is not null && valuation.TryRead(q.Symbol, out var vf)) val = new PlutoValuationTag(in vf, q.AskWon, nowUtc);
                if (!tech(q.Symbol, out var tf)) tf = default;
                if (!micro(q.Symbol, out var mf)) mf = default;
                dest[k++] = PlutoScreenInputs.From(market, q.Symbol, q.AskWon, in val, in tf, in mf, regime(q.Symbol));
            }
            return k;
        }
    }
}
