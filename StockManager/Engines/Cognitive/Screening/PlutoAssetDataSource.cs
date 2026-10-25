// ============================================================================
// PlutoAssetDataSource — v12.18.80-DEV: 플루토 병렬 학습 스웜 실데이터 소스 경계(봉 · 방향 체결 · 호가 뎁스 표본) + 표본 → 스크리닝 입력 빌더
//
//   소스 1개 = 시장 1개(업비트 공개 REST · KIS 일봉 등). 스웜 워커 스레드(BelowNormal)가 종목마다 Fetch(동기 · 워커 스레드에서 파싱/분석 수행) 호출.
//   결과 = PlutoFetchOutcome: Ok · NoData(응답 정상 · 봉 없음) · RateLimited(HTTP 429 → 그 스윕 즉시 중단 · 재시도/큐잉 0 · fail-closed) ·
//     Faulted(전송/파싱 실패) · NotConnected(인증 미구성 · 미결선 → '실피드 연결 대기' 정직 집계).
//   ★날조 0 — 소스가 실제로 받은 봉/체결/호가만 표본에 담는다. 없는 팩터 = 판독 없음(Has* false · 0점 · 중립 점수 아님).
//   PlutoBarFactorBuilder = 기존 브레인 분석기(스퀴즈 · 매물대 프로파일 · CVD · 15호가 뎁스)를 봉/체결/뎁스 표본으로 재생(값 타입 상태 · δ=0).
//   PlutoRequestPacer = 소스별 최소 송신 간격(업비트 공개 시세 초당 8회 이하 · 대기 = 워커 스레드 Sleep · 초과 요청 큐잉 0).
//   ★liveArmed:false — 시세 조회(GET)만 · 주문/무장/브로커 경로 0 · QuantumVault/피어 접근 0.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using StockManager.Engines.Cognitive.Microstructure;
using StockManager.Engines.Cognitive.Technical;
using StockManager.Engines.Cognitive.Valuation;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;

namespace StockManager.Engines.Cognitive.Screening
{
    /// <summary>OHLCV 봉 1개(시장 가격 단위 정수 · KRX 원 · US 센트 · 코인 원 × 10^8 raw · 거래량 = 주/코인 × 10^8 raw).</summary>
    public readonly struct PlutoOhlcvBar
    {
        public readonly long TimeUtcTicks, Open, High, Low, Close, Volume;

        public PlutoOhlcvBar(long timeUtcTicks, long open, long high, long low, long close, long volume)
        {
            TimeUtcTicks = timeUtcTicks; Open = open; High = high; Low = low; Close = close; Volume = volume;
        }

        public bool IsValid => High > 0 && Low > 0 && Close > 0 && High >= Low && Close <= High && Close >= Low && Volume >= 0;
    }

    /// <summary>실측 방향 체결 1건(Side +1 매수 테이커 · −1 매도 테이커).</summary>
    public readonly struct PlutoSideTrade
    {
        public readonly long TimeUtcTicks, Price, Volume;
        public readonly int Side;

        public PlutoSideTrade(long timeUtcTicks, long price, long volume, int side)
        {
            TimeUtcTicks = timeUtcTicks; Price = price; Volume = volume; Side = side;
        }
    }

    public enum PlutoFetchOutcome : byte
    {
        Ok = 0,
        NoData = 1,
        RateLimited = 2,
        Faulted = 3,
        NotConnected = 4,
    }

    /// <summary>워커 1개 전용 표본 버퍼(재사용 · 단일 스레드). 시간 순서 = 소스가 오래된 것부터 채운다.</summary>
    public sealed class PlutoAssetSample
    {
        public const int MaxBars = 200, MaxTrades = 200, MaxDepth = 15;

        public readonly PlutoOhlcvBar[] Bars = new PlutoOhlcvBar[MaxBars];
        public readonly PlutoSideTrade[] Trades = new PlutoSideTrade[MaxTrades];
        public readonly long[] BidQty = new long[MaxDepth], AskQty = new long[MaxDepth];
        public int BarCount, TradeCount, DepthTiers;
        public long BestBid, BestAsk;

        public void Clear()
        {
            BarCount = TradeCount = DepthTiers = 0;
            BestBid = BestAsk = 0;
        }
    }

    /// <summary>시장 1개 실데이터 소스(스웜 워커 스레드에서 동기 호출 · 단일 워커 전용).</summary>
    public interface IPlutoAssetDataSource
    {
        PlutoScreenMarket Market { get; }
        /// <summary>인증/결선 구성 여부(false = 조회 0 · 슬롯 '실피드 연결 대기').</summary>
        bool IsConnected { get; }
        /// <summary>소스 표기(예 "업비트 공개 REST(키 불필요)" · "KIS 미구성").</summary>
        string Label { get; }
        PlutoFetchOutcome Fetch(PlutoUniverseEntry entry, PlutoAssetSample sample, CancellationToken ct);
    }

    /// <summary>소스별 최소 송신 간격 게이트(초당 N회 이하). Wait = 다음 허용 시각까지 Sleep(호출 스레드) · 취소 = false(송신 0).</summary>
    public sealed class PlutoRequestPacer
    {
        private readonly long _intervalTicks;
        private readonly object _gate = new();
        private long _next;

        public PlutoRequestPacer(int perSecond)
        {
            if (perSecond is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(perSecond));
            PerSecond = perSecond;
            _intervalTicks = (Stopwatch.Frequency + perSecond - 1) / perSecond;   // 올림 — 초당 N회 초과 0
            MinIntervalMs = (1000 + perSecond - 1) / perSecond;
        }

        /// <summary>[v12.18.81-DEV] 최소 송신 간격(ms) 지정 — 초당 정수 회로 표현 못 하는 간격(예 KIS 해외 VTS 1,150ms)용. PerSecond = 1000 ÷ 간격(내림 · 1초 초과 = 0).</summary>
        public PlutoRequestPacer(TimeSpan minInterval)
        {
            long ms = (long)Math.Ceiling(minInterval.TotalMilliseconds);
            if (ms is < 1 or > 60_000) throw new ArgumentOutOfRangeException(nameof(minInterval));
            MinIntervalMs = ms;
            PerSecond = (int)(1000 / ms);
            _intervalTicks = (ms * Stopwatch.Frequency + 999) / 1000;              // 올림 — 간격 미달 0
        }

        public int PerSecond { get; }
        /// <summary>송신 간 최소 간격(ms · 올림).</summary>
        public long MinIntervalMs { get; }
        public long Granted => Interlocked.Read(ref _granted);
        private long _granted;

        /// <summary>송신 슬롯 1개 예약 후 그 시각까지 대기(공유 · 스레드 안전).</summary>
        public bool Wait(CancellationToken ct)
        {
            long slot;
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                slot = Math.Max(now, _next);
                _next = slot + _intervalTicks;
            }
            long waitTicks;
            while ((waitTicks = slot - Stopwatch.GetTimestamp()) > 0)             // 타이머 조기 기상 = 재대기(간격 미달 0)
            {
                int ms = (int)Math.Min(int.MaxValue, (waitTicks * 1000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency);
                if (ct.WaitHandle.WaitOne(ms)) return false;
            }
            if (ct.IsCancellationRequested) return false;
            Interlocked.Increment(ref _granted);
            return true;
        }

        // [v12.18.81-DEV] 완료 기준 간격 — 송신 1건 독점(공유 페이서의 다른 워커는 Complete 까지 대기) · 다음 송신 = 직전 응답 수신 + 간격 이후.
        private readonly SemaphoreSlim _inflight = new(1, 1);

        /// <summary>독점 송신 슬롯 획득(직전 송신 시작 + 간격 ∧ 직전 응답 수신 + 간격 이후). true = 반드시 Complete 1회 · 취소 = false(송신 0 · 슬롯 반납).</summary>
        public bool WaitExclusive(CancellationToken ct)
        {
            try { _inflight.Wait(ct); }
            catch (OperationCanceledException) { return false; }
            if (Wait(ct)) return true;
            _inflight.Release();
            return false;
        }

        /// <summary>응답 수신(성공/실패/예외 무관) 시각 기록 → 다음 슬롯 = 지금 + 간격 이후 · 독점 해제.</summary>
        public void Complete()
        {
            lock (_gate) _next = Math.Max(_next, Stopwatch.GetTimestamp() + _intervalTicks);
            _inflight.Release();
        }
    }

    /// <summary>표본(봉 · 체결 · 뎁스) → 스크리닝 입력. 기존 브레인 분석기 재생(값 타입 상태 · 할당 0). 국면 = 판정 전(호가 L1 시계열 없음 · 날조 0).</summary>
    public static class PlutoBarFactorBuilder
    {
        public static readonly long KstOffsetTicks = TimeSpan.FromHours(9).Ticks;

        /// <summary>
        /// 가격 = 매도 1호가(있음) ∨ 마지막 봉 종가 · 매수 1호가 = 스프레드 유동성(코인). 봉 0 ∧ 호가 0 = default(Symbol 0 · 판독 불가).
        /// 기술 = 봉 순차 Push(스퀴즈) + 대표가(고+저+종)/3 × 거래량 프로파일 + 봉 완성 감쇄 · 미시구조 = 방향 체결 CVD + 15호가 뎁스(2단 미만 = L1).
        /// </summary>
        public static PlutoScreenInputs Build(PlutoScreenMarket market, long symbol, PlutoAssetSample sample, in PlutoValuationTag valuation)
        {
            ArgumentNullException.ThrowIfNull(sample);
            if (symbol <= 0) return default;
            long last = 0;
            var tech = default(PlutoTechnicalState);
            int bars = Math.Clamp(sample.BarCount, 0, sample.Bars.Length);
            for (int i = 0; i < bars; i++)
            {
                ref readonly var b = ref sample.Bars[i];
                if (!b.IsValid) continue;
                long typical = (long)(((Int128)b.High + b.Low + b.Close) / 3);
                if (b.Volume > 0) PlutoVolumeProfileScanner.Add(ref tech.Profile, typical, b.Volume);
                PlutoVolatilitySqueezeAnalyzer.Push(ref tech.Squeeze, b.High, b.Low, b.Close);
                PlutoVolumeProfileScanner.Decay(ref tech.Profile);
                tech.CompletedBars++;
                tech.High = b.High; tech.Low = b.Low; tech.Close = b.Close; tech.HasBar = true;
                last = b.Close;
            }
            bool book = sample.BestAsk > 0 && sample.BestBid > 0 && sample.BestBid < sample.BestAsk;
            long price = book ? sample.BestAsk : last;
            if (price <= 0) return default;
            long mid = book ? sample.BestBid + (sample.BestAsk - sample.BestBid) / 2 : price;

            var tf = bars > 0 ? PlutoTechnicalAnalyzer.Assess(in tech, symbol, mid) : default;

            var ms = default(PlutoMicrostructureState);
            int trades = Math.Clamp(sample.TradeCount, 0, sample.Trades.Length);
            for (int i = 0; i < trades; i++)
            {
                ref readonly var t = ref sample.Trades[i];
                if (t.Price <= 0 || t.Volume <= 0 || t.TimeUtcTicks <= 0 || (t.Side != 1 && t.Side != -1)) continue;
                PlutoMicrostructureAnalyzer.OnTrade(ref ms, t.Side, t.Volume, t.Price, t.TimeUtcTicks + KstOffsetTicks);
            }
            int tiers = Math.Clamp(sample.DepthTiers, 0, PlutoAssetSample.MaxDepth);
            if (tiers > 1) PlutoMicrostructureAnalyzer.OnDepth(ref ms, sample.BidQty.AsSpan(0, tiers), sample.AskQty.AsSpan(0, tiers));
            else if (tiers == 1) PlutoMicrostructureAnalyzer.OnL1(ref ms, sample.BidQty[0], sample.AskQty[0]);
            var mf = trades > 0 || tiers > 0 ? PlutoMicrostructureAnalyzer.Assess(in ms, symbol, mid) : default;

            return PlutoScreenInputs.From(market, symbol, price, in valuation, in tf, in mf, MarketRegime.Unknown, book ? sample.BestBid : 0);
        }
    }
}
