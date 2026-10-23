// ============================================================================
// PlutoFundamentalProfile — v12.18.65-DEV: 플루토 브레인 2단계 — 종목 1개 재무 원천 프로필(불변 값 타입 · 정수 스케일)
//
//   가격 단위 = 시장 통화 최소 단위 정수(Krx = 원 · Us = 센트). EPS/BPS 도 같은 단위(주당). 배수 = ×10(PER) · ×100(PBR) · 비율 = bp.
//   EpsTtm(최근 4분기) · FwdEps(12개월 선행 컨센서스) · Bps · RoeBps · 5년 역사적 PER/PBR 밴드(하단/중앙/상단) · 업종 평균 Fwd PER ·
//   PerWeightPct(적정가 PER 가중 · 나머지 = PBR) · 분기 영업이익 컨센서스/실적(억원 · 백만달러 — 서프라이즈 계산 전용) · AsOfUtcTicks(재무 기준 시각).
//   ★시드(PlutoFundamentalSeeds): 국내 5종(아레나 합성 유니버스) + 미국 5종 — 실 재무 피드(DART · SEC EDGAR) 미결선 상태의
//     근사 예시치(Source = Seed). 근거 요약에 '시드 프로필'로 정직 표기 · 기준일 + 180일 경과 = 자동 만료(fail-closed · 아레나 기존 동작).
// ============================================================================
using System;

namespace StockManager.Engines.Cognitive.Valuation
{
    public enum PlutoMarket : byte
    {
        Unknown = 0,
        Krx = 1,      // 가격 = 원
        Us = 2,       // 가격 = 센트
    }

    public enum PlutoFundamentalSource : byte
    {
        Unknown = 0,
        Seed = 1,     // 내장 예시치(실 피드 미결선)
        Dart = 2,
        SecEdgar = 3,
        Manual = 4,
    }

    public readonly struct PlutoFundamentalProfile
    {
        /// <summary>EPS/BPS/가격 절댓값 상한(원 · 센트) — 정수 곱셈 오버플로 차단.</summary>
        public const long MaxPerShare = 1_000_000_000;
        /// <summary>PER ×10 · PBR ×100 밴드 상한.</summary>
        public const int MaxMultiple = 100_000;

        public readonly long Symbol;
        public readonly PlutoMarket Market;
        public readonly PlutoFundamentalSource Source;
        public readonly long AsOfUtcTicks;
        public readonly long EpsTtm, FwdEps, Bps;
        public readonly int RoeBps;
        public readonly int PerBandLowX10, PerBandMidX10, PerBandHighX10;
        public readonly int PbrBandLowX100, PbrBandMidX100, PbrBandHighX100;
        public readonly int SectorFwdPerX10;
        public readonly int PerWeightPct;
        public readonly long ConsensusOpProfit, ActualOpProfit;
        /// <summary>시드 평가 기준가(부트 시 1회 평가 · 아레나는 체결 시점 호가로 재판정).</summary>
        public readonly long ReferencePrice;

        public PlutoFundamentalProfile(long symbol, PlutoMarket market, PlutoFundamentalSource source, long asOfUtcTicks,
            long epsTtm, long fwdEps, long bps, int roeBps,
            int perBandLowX10, int perBandMidX10, int perBandHighX10,
            int pbrBandLowX100, int pbrBandMidX100, int pbrBandHighX100,
            int sectorFwdPerX10, int perWeightPct, long consensusOpProfit, long actualOpProfit, long referencePrice)
        {
            Symbol = symbol; Market = market; Source = source; AsOfUtcTicks = asOfUtcTicks;
            EpsTtm = epsTtm; FwdEps = fwdEps; Bps = bps; RoeBps = roeBps;
            PerBandLowX10 = perBandLowX10; PerBandMidX10 = perBandMidX10; PerBandHighX10 = perBandHighX10;
            PbrBandLowX100 = pbrBandLowX100; PbrBandMidX100 = pbrBandMidX100; PbrBandHighX100 = pbrBandHighX100;
            SectorFwdPerX10 = sectorFwdPerX10; PerWeightPct = perWeightPct;
            ConsensusOpProfit = consensusOpProfit; ActualOpProfit = actualOpProfit; ReferencePrice = referencePrice;
        }

        /// <summary>선행 EPS 성장률(bp) = (FwdEps − EpsTtm) / |EpsTtm| · EpsTtm 0 = 0 · ±1,000,000 포화.</summary>
        public int EpsGrowthBps => EpsTtm == 0 ? 0 : (int)Math.Clamp((FwdEps - EpsTtm) * 10000 / Math.Abs(EpsTtm), -1_000_000, 1_000_000);

        /// <summary>분기 영업이익 서프라이즈(bp) = (실적 − 컨센서스) / |컨센서스| · 컨센서스 0 = 0 · ±1,000,000 포화.</summary>
        public int SurpriseBps => ConsensusOpProfit == 0 ? 0
            : (int)Math.Clamp((ActualOpProfit - ConsensusOpProfit) * 10000 / Math.Abs(ConsensusOpProfit), -1_000_000, 1_000_000);

        /// <summary>
        /// 무결성: 종목 · 시장 · 기준 시각 양수 · EPS/BPS/컨센서스 범위 · 밴드 0 &lt; 하단 ≤ 중앙 ≤ 상단 ≤ 상한 · 업종 PER 양수 · 가중 0~100 ·
        /// PER 가중 &gt; 0 이면 FwdEps &gt; 0 필요(적자 = PBR 단독 · PER 가중 100 + 적자 = 적정가 산출 불가 → 거부) · PBR 가중 &gt; 0 이면 Bps &gt; 0.
        /// </summary>
        public bool IsWellFormed =>
            Symbol > 0 && Market != PlutoMarket.Unknown && AsOfUtcTicks > 0
            && Math.Abs(EpsTtm) <= MaxPerShare && Math.Abs(FwdEps) <= MaxPerShare && Bps >= 0 && Bps <= MaxPerShare
            && RoeBps is >= -100_000 and <= 100_000
            && PerBandLowX10 > 0 && PerBandLowX10 <= PerBandMidX10 && PerBandMidX10 <= PerBandHighX10 && PerBandHighX10 <= MaxMultiple
            && PbrBandLowX100 > 0 && PbrBandLowX100 <= PbrBandMidX100 && PbrBandMidX100 <= PbrBandHighX100 && PbrBandHighX100 <= MaxMultiple
            && SectorFwdPerX10 is > 0 and <= MaxMultiple && PerWeightPct is >= 0 and <= 100
            && Math.Abs(ConsensusOpProfit) <= MaxPerShare * 1000 && Math.Abs(ActualOpProfit) <= MaxPerShare * 1000
            && ReferencePrice >= 0 && ReferencePrice <= MaxPerShare
            && (PerWeightPct < 100 ? Bps > 0 : FwdEps > 0);
    }

    /// <summary>
    /// 내장 시드 프로필 — 실 재무 피드 미결선 상태의 근사 예시치(투자 판단 근거 아님 · 가상 대결 전용).
    /// 기준가 = 아레나 합성 시세 시작가(KrxArenaQuoteFeeder · UsTechUniverse)와 동일 — 부트 평가가 합성 시세 대역에서 의미 있게 분포.
    /// </summary>
    public static class PlutoFundamentalSeeds
    {
        /// <summary>시드 재무 기준일(2026-10-01 UTC · 3분기 잠정실적 시즌).</summary>
        public static readonly long AsOfUtcTicks = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        private static PlutoFundamentalProfile Krx(long code, long epsTtm, long fwdEps, long bps, int roeBps,
            int perLo, int perMid, int perHi, int pbrLo, int pbrMid, int pbrHi, int sectorPer, int perWeight, long consensus, long actual, long refPx)
            => new(code, PlutoMarket.Krx, PlutoFundamentalSource.Seed, AsOfUtcTicks, epsTtm, fwdEps, bps, roeBps,
                perLo, perMid, perHi, pbrLo, pbrMid, pbrHi, sectorPer, perWeight, consensus, actual, refPx);

        private static PlutoFundamentalProfile Us(string ticker, long epsTtmCents, long fwdEpsCents, long bpsCents, int roeBps,
            int perLo, int perMid, int perHi, int pbrLo, int pbrMid, int pbrHi, int sectorPer, long consensus, long actual, long refCents)
            => new(StockManager.Engines.Arena.Us.UsTechUniverse.Pack(ticker), PlutoMarket.Us, PlutoFundamentalSource.Seed, AsOfUtcTicks,
                epsTtmCents, fwdEpsCents, bpsCents, roeBps, perLo, perMid, perHi, pbrLo, pbrMid, pbrHi, sectorPer, 100, consensus, actual, refCents);

        /// <summary>국내 5종(삼성전자 · SK하이닉스 · LG에너지솔루션 · 삼성바이오로직스 · 현대차) + 미국 5종(AAPL · NVDA · MSFT · AMZN · TSLA).</summary>
        public static PlutoFundamentalProfile[] Create() => new[]
        {
            //       코드     EPS ttm  Fwd EPS    BPS     ROE   PER 하/중/상     PBR 하/중/상     업종PER 가중 컨센(억)  실적(억)  기준가
            Krx(5930,     5_600,   7_600,    60_900, 1240,  80, 120, 180,   90, 140, 210,   141, 60, 100_000, 112_000,  70_000),
            Krx(660,     18_000,  25_000,   110_000, 2300,  60, 100, 160,  100, 160, 250,   141, 60,  70_000,  74_200, 180_000),
            Krx(373220,   3_500,   4_000,   110_000,  350, 400, 600,1200,  200, 300, 600,   141, 60,   4_500,   4_300, 380_000),
            Krx(207940,  12_000,  15_000,   140_000, 1070, 400, 600, 900,  400, 600, 900,   300, 60,   4_000,   4_100, 800_000),
            Krx(5380,    45_000,  48_000,   380_000, 1260,  40,  60,  90,   40,  60,  90,    60, 60,  35_000,  36_400, 220_000),
            //       티커    EPS ttm(¢) Fwd(¢) BPS(¢)  ROE   PER 하/중/상     PBR 하/중/상          업종PER 컨센($M) 실적($M) 기준가(¢)
            Us("AAPL",        680,     760,    450, 15000, 180, 260, 340,  2000, 3500, 5500,  280,  29_000,  29_600, 22_500),
            Us("NVDA",        300,     450,    180,  9000, 250, 350, 600,  1500, 3000, 6000,  280,  33_000,  36_000, 13_500),
            Us("MSFT",      1_300,   1_450,  4_200,  3300, 250, 300, 380,   800, 1200, 1500,  280,  32_000,  33_300, 42_000),
            Us("AMZN",        550,     620,  2_400,  2400, 280, 330, 600,   500,  800, 1200,  280,  18_000,  18_900, 19_000),
            Us("TSLA",        310,     250,  2_300,   900, 400, 700, 900,   800, 1500, 2500,  280,   2_900,   2_400, 25_000),
        };
    }
}
