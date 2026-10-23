// ============================================================================
// PlutoValuationEngine — v12.18.65-DEV: 플루토 브레인 2단계 — 재무 프로필 → 적정가치 · 안전마진 · 등급/점수 산출 → seqlock 버퍼 게시
//
//   적정가(FairPrice) = PER 경로 × w + PBR 경로 × (1 − w)
//     PER 경로 = Fwd EPS × 5년 PER 밴드 중앙값 · PBR 경로 = BPS × 5년 PBR 밴드 중앙값 · w = PerWeightPct(국내 60 · 미국 100)
//     적자(Fwd EPS ≤ 0) = PBR 경로 단독(w = 0) · 두 경로 모두 불가 = 산출 불가(0 · 게시 거부 · fail-closed).
//   안전마진 MoS(bp) = (적정가 − 현재가) × 10000 / 현재가(0 방향 절단 · ±1,000,000 포화).
//   등급/점수 = ValuationText 매트릭스(PlutoValuationFactor 헤더) — Assess 는 순수 정수 함수(할당 0 · 아레나 핫패스 재판정 공용).
//   계층 분리: 프로필 저장/평가/게시 = 콜드(부트 · 재무 갱신 · 임의 스레드 · 프로필 표 lock) ↔ 아레나 = 버퍼 판독 + Assess 재판정만(δ=0).
//   ★liveArmed:false — 판정은 가상 대결 시그널 입력일 뿐 주문 수단 0. ★QuantumVault · 피어 · 외부 네트워크 접근 0.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;

namespace StockManager.Engines.Cognitive.Valuation
{
    public static class PlutoValuationMath
    {
        /// <summary>적정가(가격 단위 정수). 산출 불가 = 0.</summary>
        public static long FairPrice(in PlutoFundamentalProfile p)
        {
            if (!p.IsWellFormed) return 0;
            long perFair = p.FwdEps > 0 ? p.FwdEps * p.PerBandMidX10 / 10 : 0;
            long pbrFair = p.Bps > 0 ? p.Bps * p.PbrBandMidX100 / 100 : 0;
            int w = p.PerWeightPct;
            if (perFair <= 0) w = 0;
            if (w == 0 && pbrFair <= 0) return 0;
            long fair = (perFair * w + pbrFair * (100 - w)) / 100;
            return fair is > 0 and <= PlutoFundamentalProfile.MaxPerShare ? fair : 0;
        }

        /// <summary>안전마진(bp) = (적정가 − 현재가) / 현재가. 무효 = 0.</summary>
        public static int MarginOfSafetyBps(long fairPrice, long price)
        {
            if (fairPrice <= 0 || price <= 0 || fairPrice > PlutoFundamentalProfile.MaxPerShare || price > PlutoFundamentalProfile.MaxPerShare) return 0;
            return (int)Math.Clamp((fairPrice - price) * 10000 / price, -ValuationText.MaxMosBps, ValuationText.MaxMosBps);
        }

        /// <summary>안전마진 → 원점수(등급 구간 선형 보간 · 구간 경계 연속 · 0 방향 절단).</summary>
        public static int RawScore(int mosBps)
        {
            long m = mosBps;
            if (m >= ValuationText.DeepValueMosBps) return (int)Math.Min(100, 70 + (m - ValuationText.DeepValueMosBps) * 30 / 3000);
            if (m >= ValuationText.FairDiscountMosBps) return (int)(20 + (m - ValuationText.FairDiscountMosBps) * 49 / 1999);
            if (m >= 0) return (int)(m * 19 / 999);
            if (m > ValuationText.OvervaluedMosBps) return (int)(m * 19 / 1499);
            if (m > ValuationText.BubbleMosBps) return (int)(-20 - (ValuationText.OvervaluedMosBps - m) * 39 / 1499);
            return (int)Math.Max(-100, -60 - (ValuationText.BubbleMosBps - m) * 40 / 3000);
        }

        /// <summary>
        /// 현재가 기준 판정(순수 · 할당 0). 순서: MoS 등급 → 적자 할인 상한(FairlyValued) · DeepValue 자격(Fwd PER &lt; 업종 ∧ ROE &gt; 0) 미달 강등 →
        /// 밴드 상단 이탈 ∧ 역성장 = ExtremeBubble 승격 → 점수 = 원점수 + 서프라이즈 가감(±10) 을 최종 등급 밴드로 포화.
        /// </summary>
        public static PlutoValuationReading Assess(long fairPrice, long price, long fwdEps, long bps, int roeBps,
            int sectorFwdPerX10, int perBandHighX10, int epsGrowthBps, int surpriseBps)
        {
            if (fairPrice <= 0 || price <= 0 || fairPrice > PlutoFundamentalProfile.MaxPerShare || price > PlutoFundamentalProfile.MaxPerShare) return default;
            int mos = MarginOfSafetyBps(fairPrice, price);
            int per = fwdEps > 0 ? (int)Math.Min(price * 10 / fwdEps, PlutoFundamentalProfile.MaxMultiple) : 0;
            int pbr = bps > 0 ? (int)Math.Min(price * 100 / bps, PlutoFundamentalProfile.MaxMultiple) : 0;

            var g = mos >= ValuationText.DeepValueMosBps ? PlutoValuationGrade.DeepValue
                  : mos >= ValuationText.FairDiscountMosBps ? PlutoValuationGrade.FairDiscount
                  : mos > ValuationText.OvervaluedMosBps ? PlutoValuationGrade.FairlyValued
                  : mos > ValuationText.BubbleMosBps ? PlutoValuationGrade.Overvalued
                  : PlutoValuationGrade.ExtremeBubble;
            if (fwdEps <= 0 && g < PlutoValuationGrade.FairlyValued) g = PlutoValuationGrade.FairlyValued;
            else if (g == PlutoValuationGrade.DeepValue && (per >= sectorFwdPerX10 || roeBps <= 0)) g = PlutoValuationGrade.FairDiscount;
            if (per > 0 && per > perBandHighX10 && epsGrowthBps < 0) g = PlutoValuationGrade.ExtremeBubble;

            int adj = Math.Clamp(surpriseBps / 100, -ValuationText.MaxSurpriseAdjust, ValuationText.MaxSurpriseAdjust);
            var (lo, hi) = ValuationText.Band(g);
            int score = Math.Clamp(RawScore(mos) + adj, lo, hi);
            return new PlutoValuationReading(g, score, mos, per, pbr);
        }

        /// <summary>프로필 · 가격 → 판정. 프로필 무결성 위반 · 적정가 산출 불가 · 무효 가격 = false.</summary>
        public static bool TryEvaluate(in PlutoFundamentalProfile p, long price, out PlutoValuationFactor factor)
        {
            factor = default;
            long fair = FairPrice(in p);
            if (fair <= 0 || price <= 0 || price > PlutoFundamentalProfile.MaxPerShare) return false;
            var r = Assess(fair, price, p.FwdEps, p.Bps, p.RoeBps, p.SectorFwdPerX10, p.PerBandHighX10, p.EpsGrowthBps, p.SurpriseBps);
            if (r.Grade == PlutoValuationGrade.Unknown) return false;
            factor = new PlutoValuationFactor(in p, fair, price, in r);
            return true;
        }
    }

    public enum PlutoValuationStatus : byte
    {
        Published = 0,
        InvalidProfile = 1,
        InvalidPrice = 2,
        NoProfile = 3,
        Stale = 4,
        BufferFull = 5,
        Rejected = 6,
    }

    /// <summary>재무 프로필 보관 · 평가 · 버퍼 게시(콜드 경로 · 임의 스레드).</summary>
    public sealed class PlutoValuationEngine
    {
        private readonly PlutoValuationBuffer _buffer;
        private readonly Dictionary<long, PlutoFundamentalProfile> _profiles = new();
        private readonly object _gate = new();
        private long _evaluations, _failures;

        public PlutoValuationEngine(PlutoValuationBuffer buffer) => _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

        public PlutoValuationBuffer Buffer => _buffer;
        public long Evaluations => Interlocked.Read(ref _evaluations);
        public long Failures => Interlocked.Read(ref _failures);

        public int ProfileCount
        {
            get { lock (_gate) return _profiles.Count; }
        }

        public bool TryGetProfile(long symbol, out PlutoFundamentalProfile profile)
        {
            lock (_gate) return _profiles.TryGetValue(symbol, out profile);
        }

        /// <summary>프로필 등록/갱신 + 가격 평가 + 게시. 프로필 무결성 위반 = 저장 안 함 · 보관 프로필보다 과거 기준일 = Stale(보관 프로필 유지).</summary>
        public PlutoValuationStatus Upsert(in PlutoFundamentalProfile profile, long price)
        {
            if (!profile.IsWellFormed || PlutoValuationMath.FairPrice(in profile) <= 0) return Fail(PlutoValuationStatus.InvalidProfile);
            lock (_gate)
            {
                if (_profiles.TryGetValue(profile.Symbol, out var cur) && profile.AsOfUtcTicks < cur.AsOfUtcTicks) return Fail(PlutoValuationStatus.Stale);
                _profiles[profile.Symbol] = profile;
            }
            return Evaluate(in profile, price);
        }

        /// <summary>저장 프로필을 새 가격으로 재평가 + 게시(재무 불변 · 가격 갱신).</summary>
        public PlutoValuationStatus Revalue(long symbol, long price)
        {
            PlutoFundamentalProfile p;
            lock (_gate)
                if (!_profiles.TryGetValue(symbol, out p)) return Fail(PlutoValuationStatus.NoProfile);
            return Evaluate(in p, price);
        }

        /// <summary>내장 시드 프로필 전량 등록 · 기준가 평가. 반환 = 게시 성공 수.</summary>
        public int SeedDefaults()
        {
            int n = 0;
            foreach (var p in PlutoFundamentalSeeds.Create())
                if (Upsert(in p, p.ReferencePrice) == PlutoValuationStatus.Published) n++;
            return n;
        }

        private PlutoValuationStatus Evaluate(in PlutoFundamentalProfile p, long price)
        {
            if (!PlutoValuationMath.TryEvaluate(in p, price, out var f)) return Fail(PlutoValuationStatus.InvalidPrice);
            Interlocked.Increment(ref _evaluations);
            return _buffer.Publish(in f) switch
            {
                PlutoValuationPublishResult.Published => PlutoValuationStatus.Published,
                PlutoValuationPublishResult.Stale => Fail(PlutoValuationStatus.Stale),
                PlutoValuationPublishResult.BufferFull => Fail(PlutoValuationStatus.BufferFull),
                _ => Fail(PlutoValuationStatus.Rejected),
            };
        }

        private PlutoValuationStatus Fail(PlutoValuationStatus s)
        {
            Interlocked.Increment(ref _failures);
            return s;
        }
    }
}
