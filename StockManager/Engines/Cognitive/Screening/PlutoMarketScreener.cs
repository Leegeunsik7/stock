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
//   [v12.18.79-DEV] 3대 자산(KRX · US · 업비트 KRW) — 가상자산 유니버스(PlutoArenaUniverse.Crypto · 코인 4대 팩터) 추가.
//    감시 카탈로그(PlutoMultiAssetUniverseCatalog · KRX 200 · US 100 · 업비트 120) = 집계 Monitored(감시 수) — 피드 판독이 없는 카탈로그 종목은 점수 산출 0
//    ('피드 판독 대기'로 정직 집계 · 시세/재무 날조 0). Passed = 추천 기준 통과(Top 5 절단 전). 코인 추천 수량 = 코인 × 10^8 raw.
//   [v12.18.85-DEV] ③ 랭킹 = PlutoRecommendationEngine 위임(학습 가중치 스냅샷 투영 · 모멘텀 조정/악마의 변호인 감점 · 중립 = 기존 점수 그대로).
//   [v12.18.88-DEV] 위원회 허브(선택) = 스텝마다 교차 자산 파급 공급/게시 · 거시/국면 판독 → 시장별 위원회 입력(비토 · KRX 선행 점수 · 켈리) → 39셀 판정 보드 게시.
//   [v12.18.80-DEV] 유니버스 = PlutoUniverseMerge(매트릭스 보드 Live 셀(병렬 학습 스웜 실데이터) 우선 + 아레나 시세 보조) · 스캔 버퍼 256.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Learning;
using StockManager.Engines.Cognitive.Microstructure;
using StockManager.Engines.Cognitive.Technical;
using StockManager.Engines.Cognitive.Valuation;

namespace StockManager.Engines.Cognitive.Screening
{
    /// <summary>유니버스 1회 판독(반환 = 채운 수). 스크리너 스텝 잠금 안에서만 호출(단일 스레드).</summary>
    public delegate int PlutoUniverseScanner(Span<PlutoScreenInputs> destination);

    public sealed class PlutoMarketScreener : IDisposable
    {
        public const int MaxUniverse = 256;   // [v12.18.80-DEV] 128 → 256(매트릭스 KRX 200 실데이터 + 아레나 병합)

        private sealed class Universe
        {
            public readonly PlutoUniverseScanner Scan;
            public readonly long Capital;
            public Universe(PlutoUniverseScanner scan, long capital) { Scan = scan; Capital = capital; }
        }

        private readonly PlutoRecommendationBoard _board;
        private readonly Func<PlutoScreenMacro> _macro;
        private readonly Func<long> _utcNow;
        private readonly Func<PlutoCognitiveWeights> _weights;
        private readonly Universe?[] _universes = new Universe?[PlutoRecommendationSnapshot.Markets];
        private readonly PlutoScreenInputs[] _scratch = new PlutoScreenInputs[MaxUniverse];
        private readonly object _stepGate = new();
        private readonly PlutoCognitiveTensorLake? _tensor;
        private readonly Council.PlutoCouncilHub? _council;
        private long _generation, _cycles, _faulted;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        /// <param name="weights">[v12.18.85-DEV] 학습 가중치 스냅샷 공급자(스텝당 1회 · 미지정 = 중립 → 기존 점수 그대로).</param>
        /// <param name="tensor">[v12.18.86-DEV] 36슬롯 인지 텐서(시장별 전용 가중치 독립 투영 · 미지정 = 전역 가중치 공유).</param>
        public PlutoMarketScreener(PlutoRecommendationBoard board, Func<PlutoScreenMacro>? macro = null, Func<long>? utcNow = null,
            Func<PlutoCognitiveWeights>? weights = null, PlutoCognitiveTensorLake? tensor = null, Council.PlutoCouncilHub? council = null)
        {
            _tensor = tensor;
            _council = council;
            _board = board ?? throw new ArgumentNullException(nameof(board));
            _macro = macro ?? (static () => default);
            _utcNow = utcNow ?? (static () => DateTime.UtcNow.Ticks);
            _weights = weights ?? (static () => default);
        }

        /// <summary>[v12.18.85-DEV] 학습 seqlock → 가중치 공급자(판독 경합 = 중립 · 체결 스레드와 잠금 공유 0).</summary>
        public static Func<PlutoCognitiveWeights> WeightsFrom(PlutoCognitiveWeightSeqlock seqlock)
        {
            ArgumentNullException.ThrowIfNull(seqlock);
            return () => seqlock.TryRead(out var w) ? w : default;
        }

        public PlutoRecommendationBoard Board => _board;
        /// <summary>[v12.18.88-DEV] 위원회 허브(미결선 = null · 기존 랭킹).</summary>
        public Council.PlutoCouncilHub? CouncilHub => _council;
        public long Cycles => Interlocked.Read(ref _cycles);
        public long Faulted => Interlocked.Read(ref _faulted);
        public bool Running => Volatile.Read(ref _loop) is { IsCompleted: false };

        /// <summary>시장 유니버스 등록(같은 시장 재등록 = 교체). capital = 추천 수량 산정 기준 자본(KRX 원 · US 센트 · 코인 원).</summary>
        public void AddUniverse(PlutoScreenMarket market, PlutoUniverseScanner scan, long capital)
        {
            ArgumentNullException.ThrowIfNull(scan);
            if (market is not (PlutoScreenMarket.Krx or PlutoScreenMarket.Us or PlutoScreenMarket.Crypto)) throw new ArgumentOutOfRangeException(nameof(market));
            if (capital <= 0) throw new ArgumentOutOfRangeException(nameof(capital));
            lock (_stepGate) _universes[(int)market] = new Universe(scan, capital);
        }

        /// <summary>스캔 1회 → 스냅샷 게시(동시 호출 = 직렬화).</summary>
        public PlutoRecommendationSnapshot Step()
        {
            lock (_stepGate)
            {
                var macro = _macro();
                var weights = _weights();
                long now = _utcNow();
                var cm = _council?.Prepare(now) ?? Council.PlutoCouncilMacro.None;   // [v12.18.88-DEV] 파급 공급 · 게시 → 거시/국면 판독(미결선 = 생략)
                var krx = ScanMarket(PlutoScreenMarket.Krx, in macro, PlutoRecommendationEngine.AssetWeights(PlutoScreenMarket.Krx, in weights, _tensor), in cm, out var krxTally);
                var us = ScanMarket(PlutoScreenMarket.Us, in macro, PlutoRecommendationEngine.AssetWeights(PlutoScreenMarket.Us, in weights, _tensor), in cm, out var usTally);
                var crypto = ScanMarket(PlutoScreenMarket.Crypto, in macro, PlutoRecommendationEngine.AssetWeights(PlutoScreenMarket.Crypto, in weights, _tensor), in cm, out var cryptoTally);
                var snap = new PlutoRecommendationSnapshot(++_generation, now, krx, krxTally, us, usTally, crypto, cryptoTally);
                _board.Publish(snap);
                _council?.PublishBoard(in cm, _ => weights);   // 체결 핫패스 39셀 판정 보드(셀 해석은 허브가 텐서로 수행)
                Interlocked.Increment(ref _cycles);
                return snap;
            }
        }

        private PlutoRecommendation[] ScanMarket(PlutoScreenMarket market, in PlutoScreenMacro macro, in PlutoCognitiveWeights weights, in Council.PlutoCouncilMacro cm,
            out PlutoScreenTally tally)
        {
            var u = _universes[(int)market];
            int monitored = PlutoMultiAssetUniverseCatalog.CountOf(market);
            if (u is null) { tally = new PlutoScreenTally(0, 0, 0, 0, monitored, 0); return Array.Empty<PlutoRecommendation>(); }
            int n = Math.Clamp(u.Scan(_scratch), 0, _scratch.Length);
            var council = _council is null ? default : _council.InputsFor(market, in cm, in weights);
            return PlutoRecommendationEngine.Rank(_scratch.AsSpan(0, n), market, in macro, in weights, u.Capital, monitored, PlutoRecommendationSnapshot.TopN, in council, out tally);
        }

        /// <summary>추천 수량(내림 · 자본 × 비중 &lt; 1주 = 0).</summary>
        public static long RecommendedQty(long capital, int score, long price)
        {
            if (capital <= 0 || price <= 0) return 0;
            long budget = (long)((Int128)capital * PlutoDynamicSizer.WeightBp(score, 0, 0) / 10000);
            return budget / price;
        }

        /// <summary>[v12.18.88-DEV] 권장 비중 bp(프랙탈 켈리) 기준 추천 수량 — 주식 = 주 · 코인 = 코인 × 10^8 raw(내림 · 자본 부족 = 0).</summary>
        public static long RecommendedQtyBp(PlutoScreenMarket market, long capital, int weightBp, long price)
        {
            if (capital <= 0 || price <= 0 || weightBp <= 0) return 0;
            Int128 budget = (Int128)capital * Math.Min(weightBp, PlutoDynamicSizer.MaxWeightBp) / 10000;
            if (market != PlutoScreenMarket.Crypto) return (long)(budget / price);
            return (long)Int128.Min(budget * PlutoDiscoveryScorer.CryptoScale * PlutoDiscoveryScorer.CryptoScale / price, long.MaxValue);
        }

        /// <summary>[v12.18.79-DEV] 시장별 추천 수량 — 코인 = 원 자본 × 비중 ÷ 가격 raw → 코인 × 10^8 raw(내림 · 1 raw 미만 = 0) · 주식 = 주.</summary>
        public static long RecommendedQty(PlutoScreenMarket market, long capital, int score, long price)
        {
            if (market != PlutoScreenMarket.Crypto) return RecommendedQty(capital, score, price);
            if (capital <= 0 || price <= 0) return 0;
            Int128 budgetWon = (Int128)capital * PlutoDynamicSizer.WeightBp(score, 0, 0) / 10000;
            return (long)Int128.Min(budgetWon * PlutoDiscoveryScorer.CryptoScale * PlutoDiscoveryScorer.CryptoScale / price, long.MaxValue);
        }

        public static string CodeOf(PlutoScreenMarket m, long symbol) => m switch
        {
            PlutoScreenMarket.Us => UsTechUniverse.Unpack(symbol),
            PlutoScreenMarket.Crypto => CryptoArenaUniverse.Unpack(symbol),
            _ => symbol.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
        };

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

        /// <summary>[v12.18.79-DEV] 업비트 아레나 → 가상자산 유니버스(가격 = 매도 1호가 raw · 매수 1호가 = 스프레드 유동성 · 밸류에이션 없음).</summary>
        public static PlutoUniverseScanner Crypto(UpbitCryptoArenaEngine engine)
        {
            ArgumentNullException.ThrowIfNull(engine);
            var quotes = new DuelQuote[UpbitCryptoArenaEngine.MaxSymbols];
            return dest => Fill(dest, quotes, engine.CopyQuotes(quotes), PlutoScreenMarket.Crypto, null, 0,
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
                dest[k++] = PlutoScreenInputs.From(market, q.Symbol, q.AskWon, in val, in tf, in mf, regime(q.Symbol), q.BidWon);
            }
            return k;
        }
    }
}
