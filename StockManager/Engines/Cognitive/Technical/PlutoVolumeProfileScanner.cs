// ============================================================================
// PlutoVolumeProfileScanner — v12.18.73-DEV: 플루토 브레인 3단계 — 가격대별 누적 거래량 프로파일(POC · 밸류 에어리어) · 지지/저항 판정(값 타입 · δ=0)
//
//   격자: 32 구간 고정 인라인 버킷 · 구간 폭 = 첫 체결가 × 25bp(최소 1 가격 단위) · 첫 체결가 = 16번 구간 중앙.
//     격자 밖 체결 = 격자 이동(가격이 16번 구간에 오도록 이동 · 밀려난 구간 폐기) · 32 구간 이상 점프 = 초기화 후 현재가 기준 재격자(폭 재산출).
//   누적: 체결 1건 = 가격 구간 += 거래량(1건 상한 2^48 · 구간 2^56 초과 = 전 구간 절반 축소 — 비율 보존 · 오버플로 0).
//     최근성 = 봉 완성마다 전 구간 × 15/16 감쇄(반감기 약 11 봉). 표본(거래량 &gt; 0 체결) 30건 전 = 판정 없음(fail-closed).
//   POC = 최대 거래량 구간 중앙가(동률 = 낮은 구간) · 밸류 에어리어 = POC 에서 인접 구간 중 큰 쪽을 붙여 총량 70% 도달 → VAL(하단 경계) · VAH(상단 경계).
//   현재가 판정(구간 폭 기준 거리 d = (현재가 − POC) × 100 / 폭):
//     |d| &lt; 25            AtPoc        0         (POC 균형점 · 방향 없음)
//     25 ≤ d ≤ 250        PocSupport   +100→+50  (POC 바로 위 = 최대 매물대 지지)
//     d &gt; 250 ∧ ≤ VAH     UpperValue   +30
//     &gt; VAH              AboveValue   +50       (밸류 상단 이탈 = 상단 매물 공백)
//     −250 ≤ d ≤ −25      PocResistance −100→−50 (POC 바로 아래 = 상단 매물벽 · 악마의 변호인 반대 논리)
//     d &lt; −250 ∧ ≥ VAL    LowerValue   −30
//     &lt; VAL              BelowValue   −50
//     최종 점수 = 기준 × 집중도(50 + POC 점유율bp/40 · 50~100%) / 100 → −100~+100.
//   ★거래량 출처: KRX = 틱 거래량 · Upbit = 체결 수량(raw 10^8) · US = 피드에 체결량 없음 → 호가 1틱 = 가중 1(시간-가격 TPO 근사 · 정직 표기).
//   ★δ=0: Add/Decay/Read 힙 할당 0(InlineArray · 값 타입 · Int128 비교 · LINQ/박싱/클로저/문자열 0).
// ============================================================================
using System;
using System.Runtime.CompilerServices;

namespace StockManager.Engines.Cognitive.Technical
{
    /// <summary>32 구간 거래량 고정 인라인 버킷(힙 0).</summary>
    [InlineArray(PlutoVolumeProfileScanner.Bins)]
    public struct PlutoProfileBins
    {
        private long _e0;
    }

    public enum PlutoProfileZone : byte
    {
        None = 0,
        AtPoc = 1,
        PocSupport = 2,
        UpperValue = 3,
        AboveValue = 4,
        PocResistance = 5,
        LowerValue = 6,
        BelowValue = 7,
    }

    /// <summary>종목 1개 거래량 프로파일(값 타입 · 엔진 종목 표에 내장 · default = 빈 프로파일). 단일 작성자(정산 스레드).</summary>
    public struct PlutoVolumeProfileState
    {
        public PlutoProfileBins Volume;
        /// <summary>0번 구간 하단 경계 · 구간 폭(가격 단위 · 0 = 미격자).</summary>
        public long Base, Width;
        public int Samples;
    }

    /// <summary>현재가 기준 프로파일 판독(값 타입 · Zone = None ↔ 판정 불가).</summary>
    public readonly struct PlutoVolumeProfileReading
    {
        public readonly PlutoProfileZone Zone;
        public readonly int Score, PocShareBp;
        public readonly long PocPrice, ValueAreaHigh, ValueAreaLow;

        public PlutoVolumeProfileReading(PlutoProfileZone zone, int score, int pocShareBp, long pocPrice, long valueAreaHigh, long valueAreaLow)
        {
            Zone = zone; Score = score; PocShareBp = pocShareBp; PocPrice = pocPrice; ValueAreaHigh = valueAreaHigh; ValueAreaLow = valueAreaLow;
        }
    }

    public static class PlutoVolumeProfileScanner
    {
        public const int Bins = 32, CenterBin = 16;
        public const int BinWidthBps = 25;
        public const int MinSamples = 30;
        public const int ValueAreaPct = 70;
        public const int DecayShift = 4;
        public const long MaxWeight = 1L << 48, BinCap = 1L << 56;
        /// <summary>판정 경계(구간 폭 × 1/100 단위).</summary>
        public const int PocDeadbandX100 = 25, PocReachX100 = 250;
        public const int SupportMaxScore = 100, SupportMinScore = 50, ValueScore = 30, OutsideValueScore = 50;

        /// <summary>체결 1건 누적(δ=0). 가격 ≤ 0 · 거래량 ≤ 0 = 무시.</summary>
        public static void Add(ref PlutoVolumeProfileState s, long price, long weight)
        {
            if (price <= 0 || weight <= 0) return;
            if (s.Width <= 0) Grid(ref s, price);
            long idx = BinOf(in s, price);
            if (idx < 0 || idx >= Bins)
            {
                long shift = idx - CenterBin;
                if (shift <= -Bins || shift >= Bins) { s = default; Grid(ref s, price); }
                else Shift(ref s, (int)shift);
                idx = BinOf(in s, price);
                if (idx < 0 || idx >= Bins) return;                     // 방어(도달 불가)
            }
            long w = Math.Min(weight, MaxWeight);
            s.Volume[(int)idx] += w;
            if (s.Volume[(int)idx] > BinCap)
                for (int i = 0; i < Bins; i++) s.Volume[i] >>= 1;
            if (s.Samples < int.MaxValue) s.Samples++;
        }

        /// <summary>봉 완성 감쇄(전 구간 × 15/16 · δ=0).</summary>
        public static void Decay(ref PlutoVolumeProfileState s)
        {
            for (int i = 0; i < Bins; i++) s.Volume[i] -= s.Volume[i] >> DecayShift;
        }

        /// <summary>가격 → 구간 번호(내림 나눗셈 · 격자 밖 = 범위 밖 값 · ±10×Bins 포화).</summary>
        public static long BinOf(in PlutoVolumeProfileState s, long price)
        {
            if (s.Width <= 0) return -1;
            Int128 off = (Int128)price - s.Base;
            Int128 q = off / s.Width;
            if (off < 0 && q * s.Width != off) q--;
            return (long)Int128.Clamp(q, -10 * Bins, 10 * Bins);
        }

        private static void Grid(ref PlutoVolumeProfileState s, long price)
        {
            s.Width = Math.Max(1, (long)((Int128)price * BinWidthBps / 10000));
            s.Base = (long)((Int128)price - (Int128)s.Width * CenterBin - s.Width / 2);
        }

        /// <summary>격자 이동(k &gt; 0 = 위로 · 밀려난 구간 폐기 · 새 구간 0).</summary>
        private static void Shift(ref PlutoVolumeProfileState s, int k)
        {
            if (k > 0)
                for (int i = 0; i < Bins; i++) s.Volume[i] = i + k < Bins ? s.Volume[i + k] : 0;
            else
                for (int i = Bins - 1; i >= 0; i--) s.Volume[i] = i + k >= 0 ? s.Volume[i + k] : 0;
            s.Base = (long)((Int128)s.Base + (Int128)k * s.Width);
        }

        /// <summary>집중도 배율(%) = 50 + POC 점유율bp / 40(50~100).</summary>
        public static int StrengthPct(int pocShareBp) => Math.Clamp(50 + pocShareBp / 40, 50, 100);

        /// <summary>구간 거리(d · 폭 × 1/100) → (구역 · 기준 점수). VAH/VAL 판정은 호출 측(가격 비교)이 넘긴다.</summary>
        public static (PlutoProfileZone Zone, int BaseScore) Classify(long dX100, bool aboveVah, bool belowVal)
        {
            if (dX100 > -PocDeadbandX100 && dX100 < PocDeadbandX100) return (PlutoProfileZone.AtPoc, 0);
            if (dX100 >= PocDeadbandX100 && dX100 <= PocReachX100)
                return (PlutoProfileZone.PocSupport, (int)(SupportMaxScore - (dX100 - PocDeadbandX100) * (SupportMaxScore - SupportMinScore) / (PocReachX100 - PocDeadbandX100)));
            if (dX100 <= -PocDeadbandX100 && dX100 >= -PocReachX100)
                return (PlutoProfileZone.PocResistance, (int)(-SupportMaxScore + (-dX100 - PocDeadbandX100) * (SupportMaxScore - SupportMinScore) / (PocReachX100 - PocDeadbandX100)));
            if (dX100 > 0) return aboveVah ? (PlutoProfileZone.AboveValue, OutsideValueScore) : (PlutoProfileZone.UpperValue, ValueScore);
            return belowVal ? (PlutoProfileZone.BelowValue, -OutsideValueScore) : (PlutoProfileZone.LowerValue, -ValueScore);
        }

        /// <summary>현재가 기준 판정(순수 · 할당 0). 표본 부족 · 빈 프로파일 · 무효 가격 = default(None).</summary>
        public static PlutoVolumeProfileReading Read(in PlutoVolumeProfileState s, long price)
        {
            if (price <= 0 || s.Width <= 0 || s.Samples < MinSamples) return default;
            int poc = 0;
            Int128 total = 0;
            for (int i = 0; i < Bins; i++)
            {
                long v = s.Volume[i];
                total += v;
                if (v > s.Volume[poc]) poc = i;
            }
            if (total <= 0 || s.Volume[poc] <= 0) return default;
            int lo = poc, hi = poc;
            Int128 acc = s.Volume[poc];
            while (acc * 100 < total * ValueAreaPct)
            {
                long up = hi + 1 < Bins ? s.Volume[hi + 1] : -1;
                long down = lo - 1 >= 0 ? s.Volume[lo - 1] : -1;
                if (up < 0 && down < 0) break;
                if (up >= down) { hi++; acc += up; }
                else { lo--; acc += down; }
            }
            long pocPrice = (long)((Int128)s.Base + (Int128)poc * s.Width + s.Width / 2);
            long vah = (long)((Int128)s.Base + (Int128)(hi + 1) * s.Width);
            long val = (long)((Int128)s.Base + (Int128)lo * s.Width);
            if (pocPrice <= 0) return default;
            int share = (int)(s.Volume[poc] * (Int128)10000 / total);
            long d = (long)Int128.Clamp(((Int128)price - pocPrice) * 100 / s.Width, -1_000_000_000, 1_000_000_000);
            var (zone, baseScore) = Classify(d, price > vah, price < Math.Max(val, 1));
            int score = baseScore * StrengthPct(share) / 100;
            return new PlutoVolumeProfileReading(zone, score, share, pocPrice, vah, Math.Max(val, 1));
        }
    }
}
