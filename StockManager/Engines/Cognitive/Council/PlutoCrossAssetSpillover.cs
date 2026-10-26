// ============================================================================
// PlutoCrossAssetSpillover — v12.18.88-DEV: 교차 자산 선행-지행 파급 엔진(US 빅테크 · 미 10년물 · 업비트 BTC 변동성 → KRX 200 선행 신호 · 무할당 롤링 버퍼 · seqlock δ=0)
//
//   롤링 버퍼(레인 3개 · 고정 배열 64칸 · 구축 1회 후 할당 0): Krx(감시 바스켓 일간 수익률) · BigTech(US 빅테크 바스켓 일간 수익률) · Btc(KRW-BTC 봉간 수익률).
//     키 = 일 번호(UTC ticks ÷ 1일 · BTC = 봉 시각 분 번호) 오름차순 · 같은 키 = 갱신 · 더 오래된 키 = 무시(과거 재기록 0) · 꽉 참 = 가장 오래된 칸 밀어냄.
//   선행 상관(LeadCorrX1000): US 세션 u 의 빅테크 수익률 ↔ 그 다음 KRX 세션 k(u < k ≤ u + 4일) 의 KRX 수익률 — 쌍 ≥ 10 일 때만 Pearson(−1000~+1000) · 미만 = 미추정.
//   KRX 선행 투영(LeadPts · −6~+6 · KRX 추천 점수에만 가산):
//     ① 빅테크 = 최근 US 세션 수익률 × max(상관, 0) ÷ 50bp(±3 · 상관 미추정 = 0 — 검증 안 된 파급을 점수에 넣지 않음)
//     ② 금리 = −(미 10년물 변화 bp ÷ 10)(±3 · 거시 피드 미결선 = 0)  ③ BTC 변동성 = 단기(최근 5) RMS ÷ 기준(그 직전 20 · 겹침 0) RMS ≥ 150% = −1 · ≥ 200% = −2.
//   충격(Shock · 리스크 통제관 비토 입력): |10년물 변화| ≥ 25bp · 빅테크 세션 ≤ −300bp · BTC 변동성 비율 ≥ 250%.
//   ★단일 작성자(Track B 스크리너 스텝 · 작성 게이트) · 판독 = 버전 브래킷 seqlock TryRead(체결 핫패스 δ=0 · 잠금 0).
//   ★실측만: 레인 표본이 없으면 그 항목 = 0 · Fields 비트 미설정(가짜 파급 0 · CLAUDE.md §2). ★liveArmed:false · QuantumVault/피어 0.
// ============================================================================
using System;
using System.Threading;
using StockManager.Engines.Cognitive.Screening;

namespace StockManager.Engines.Cognitive.Council
{
    public enum PlutoLeadLane : byte
    {
        Krx = 0,
        BigTech = 1,
        Btc = 2,
    }

    [Flags]
    public enum PlutoSpilloverField : byte
    {
        None = 0,
        Us10y = 1,
        BigTech = 2,
        BtcVol = 4,
        LeadCorr = 8,
    }

    [Flags]
    public enum PlutoSpilloverShock : byte
    {
        None = 0,
        Us10yJump = 1,
        BigTechCrash = 2,
        BtcVolSpike = 4,
    }

    /// <summary>파급 신호 1건(불변 값 타입 · default = 판독 없음).</summary>
    public readonly struct PlutoSpilloverSignal
    {
        public readonly PlutoSpilloverField Fields;
        public readonly PlutoSpilloverShock Shocks;
        public readonly int Us10yChangeBp, BigTechReturnBp, BtcVolBp, BtcVolRatioPct, LeadCorrX1000, Pairs, LeadPts;
        public readonly long Seq, AsOfUtcTicks;

        public PlutoSpilloverSignal(PlutoSpilloverField fields, PlutoSpilloverShock shocks, int us10yChangeBp, int bigTechReturnBp, int btcVolBp, int btcVolRatioPct,
            int leadCorrX1000, int pairs, int leadPts, long seq, long asOfUtcTicks)
        {
            Fields = fields; Shocks = shocks; Us10yChangeBp = us10yChangeBp; BigTechReturnBp = bigTechReturnBp; BtcVolBp = btcVolBp; BtcVolRatioPct = btcVolRatioPct;
            LeadCorrX1000 = leadCorrX1000; Pairs = pairs; LeadPts = leadPts; Seq = seq; AsOfUtcTicks = asOfUtcTicks;
        }

        public bool HasData => Fields != PlutoSpilloverField.None;
        public bool IsShock => Shocks != PlutoSpilloverShock.None;
        public bool Has(PlutoSpilloverField f) => (Fields & f) == f;
    }

    public sealed class PlutoCrossAssetSpillover
    {
        public const int Capacity = 64, Lanes = 3, MinPairs = 10, MaxLeadGapDays = 4;
        public const int ShortVolBars = 5, LongVolBars = 20;
        public const int BigTechBpPerPt = 50, Us10yBpPerPt = 10, MaxComponentPts = 3, MaxLeadPts = 6;
        public const int VolRatioWarnPct = 150, VolRatioHighPct = 200;
        public const int ShockUs10yBp = 25, ShockBigTechBp = -300, ShockBtcVolRatioPct = 250;
        public const int MaxReadSpins = 1024;

        private readonly long[] _keys = new long[Lanes * Capacity];
        private readonly int[] _ret = new int[Lanes * Capacity];
        private readonly int[] _count = new int[Lanes];
        private readonly object _writeGate = new();
        private bool _hasUs10y;
        private int _us10yBp;

        private long _ver;
        private PlutoSpilloverSignal _signal;
        private long _seq, _contended;

        public long Published => Interlocked.Read(ref _seq);
        public long ContendedReads => Interlocked.Read(ref _contended);

        public int CountOf(PlutoLeadLane lane) { lock (_writeGate) return _count[(int)lane % Lanes]; }

        /// <summary>레인 표본 1건(Track B · δ=0). 키 오름차순 · 같은 키 = 갱신 · 과거 키 = 무시(false).</summary>
        public bool ObserveReturn(PlutoLeadLane lane, long key, int returnBp)
        {
            int l = (int)lane;
            if ((uint)l >= Lanes) return false;
            lock (_writeGate)
            {
                int n = _count[l], b = l * Capacity;
                if (n > 0)
                {
                    long last = _keys[b + n - 1];
                    if (key == last) { _ret[b + n - 1] = returnBp; return true; }
                    if (key < last) return false;
                }
                if (n == Capacity)
                {
                    Array.Copy(_keys, b + 1, _keys, b, Capacity - 1);
                    Array.Copy(_ret, b + 1, _ret, b, Capacity - 1);
                    n--;
                }
                _keys[b + n] = key;
                _ret[b + n] = returnBp;
                _count[l] = n + 1;
                return true;
            }
        }

        /// <summary>미 10년물 변화 bp(거시 피드 · Track B). hasValue false = 미결선(항목 0).</summary>
        public void ObserveUs10y(bool hasValue, int changeBp)
        {
            lock (_writeGate) { _hasUs10y = hasValue; _us10yBp = hasValue ? Math.Clamp(changeBp, -5000, 5000) : 0; }
        }

        /// <summary>봉(오래된 것부터) → 레인 봉간 수익률 기록(Track B). daily = 키 일 번호 · 아니면 분 번호. 반환 = 기록 수.</summary>
        public int ObserveBars(PlutoLeadLane lane, ReadOnlySpan<PlutoOhlcvBar> barsOldestFirst, bool daily)
        {
            int written = 0;
            for (int i = 1; i < barsOldestFirst.Length; i++)
            {
                ref readonly var p = ref barsOldestFirst[i - 1];
                ref readonly var c = ref barsOldestFirst[i];
                if (!p.IsValid || !c.IsValid) continue;
                if (ObserveReturn(lane, KeyOf(c.TimeUtcTicks, daily), ReturnBp(p.Close, c.Close))) written++;
            }
            return written;
        }

        public static long KeyOf(long utcTicks, bool daily) => utcTicks / (daily ? TimeSpan.TicksPerDay : TimeSpan.TicksPerMinute);

        public static int ReturnBp(long prevClose, long close) => prevClose <= 0 ? 0 : (int)Int128.Clamp((Int128)(close - prevClose) * 10000 / prevClose, -10000, 100000);

        /// <summary>신호 재계산 → seqlock 게시(Track B · δ=0). 반환 = 게시 신호.</summary>
        public PlutoSpilloverSignal Publish(long nowUtcTicks)
        {
            PlutoSpilloverSignal s;
            lock (_writeGate) s = Compute(nowUtcTicks);
            var spin = new SpinWait();
            long v;
            while (true)
            {
                v = Volatile.Read(ref _ver);
                if ((v & 1) == 0 && Interlocked.CompareExchange(ref _ver, v + 1, v) == v) break;
                spin.SpinOnce(-1);
            }
            _signal = s;
            Volatile.Write(ref _ver, v + 2);
            return s;
        }

        /// <summary>최신 신호 판독(체결 핫패스 δ=0 · 경합 상한 초과 = false · default).</summary>
        public bool TryRead(out PlutoSpilloverSignal signal)
        {
            for (int k = 0; k < MaxReadSpins; k++)
            {
                long v1 = Volatile.Read(ref _ver);
                if ((v1 & 1) != 0) { Thread.SpinWait(8); continue; }
                signal = _signal;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v1) return true;
            }
            signal = default;
            Interlocked.Increment(ref _contended);
            return false;
        }

        /// <summary>선행 상관(쌍 수 out · 미만 = 미추정 false). 작성 게이트 안에서만.</summary>
        private bool LeadCorr(out int corrX1000, out int pairs)
        {
            int bt = _count[(int)PlutoLeadLane.BigTech], kx = _count[(int)PlutoLeadLane.Krx];
            int bb = (int)PlutoLeadLane.BigTech * Capacity, kb = (int)PlutoLeadLane.Krx * Capacity;
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            pairs = 0;
            int j = 0;
            for (int i = 0; i < bt; i++)
            {
                long u = _keys[bb + i];
                while (j < kx && _keys[kb + j] <= u) j++;
                if (j >= kx) break;
                long k = _keys[kb + j];
                if (k - u > MaxLeadGapDays) continue;
                double x = _ret[bb + i], y = _ret[kb + j];
                sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
                pairs++;
            }
            corrX1000 = 0;
            if (pairs < MinPairs) return false;
            double cov = sxy - sx * sy / pairs, vx = sxx - sx * sx / pairs, vy = syy - sy * sy / pairs;
            if (vx <= 0 || vy <= 0) return false;
            corrX1000 = (int)Math.Clamp(Math.Round(cov / Math.Sqrt(vx * vy) * 1000), -1000, 1000);
            return true;
        }

        /// <summary>RMS(bp) — 최근 skipRecent 칸을 건너뛴 직전 bars 칸(단기/기준 창 분리 · 겹침 0).</summary>
        private long Rms(int lane, int bars, int skipRecent)
        {
            int n = _count[lane] - skipRecent, b = lane * Capacity, take = Math.Min(bars, n);
            if (take <= 0) return 0;
            long sum = 0;
            for (int i = n - take; i < n; i++) sum += (long)_ret[b + i] * _ret[b + i];
            return (long)Math.Sqrt(sum / (double)take);
        }

        private PlutoSpilloverSignal Compute(long nowUtcTicks)
        {
            var fields = PlutoSpilloverField.None;
            var shocks = PlutoSpilloverShock.None;
            int pts = 0, btRet = 0, volBp = 0, volRatio = 0;

            if (_hasUs10y)
            {
                fields |= PlutoSpilloverField.Us10y;
                pts += Math.Clamp(-_us10yBp / Us10yBpPerPt, -MaxComponentPts, MaxComponentPts);
                if (Math.Abs(_us10yBp) >= ShockUs10yBp) shocks |= PlutoSpilloverShock.Us10yJump;
            }

            int bt = _count[(int)PlutoLeadLane.BigTech];
            if (bt > 0)
            {
                fields |= PlutoSpilloverField.BigTech;
                btRet = _ret[(int)PlutoLeadLane.BigTech * Capacity + bt - 1];
                if (btRet <= ShockBigTechBp) shocks |= PlutoSpilloverShock.BigTechCrash;
            }
            bool corrOk = LeadCorr(out int corr, out int pairs);
            if (corrOk)
            {
                fields |= PlutoSpilloverField.LeadCorr;
                if (bt > 0 && corr > 0) pts += Math.Clamp((int)((long)btRet * corr / 1000 / BigTechBpPerPt), -MaxComponentPts, MaxComponentPts);
            }

            int btc = (int)PlutoLeadLane.Btc;
            if (_count[btc] >= ShortVolBars + LongVolBars)
            {
                long shortRms = Rms(btc, ShortVolBars, 0), longRms = Rms(btc, LongVolBars, ShortVolBars);
                fields |= PlutoSpilloverField.BtcVol;
                volBp = (int)Math.Min(shortRms, int.MaxValue);
                volRatio = longRms <= 0 ? (shortRms > 0 ? ShockBtcVolRatioPct : 100) : (int)Math.Min(shortRms * 100 / longRms, 10000);
                if (volRatio >= VolRatioHighPct) pts -= 2;
                else if (volRatio >= VolRatioWarnPct) pts -= 1;
                if (volRatio >= ShockBtcVolRatioPct) shocks |= PlutoSpilloverShock.BtcVolSpike;
            }

            long seq = Interlocked.Increment(ref _seq);
            return new PlutoSpilloverSignal(fields, shocks, _hasUs10y ? _us10yBp : 0, btRet, volBp, volRatio, corrOk ? corr : 0, pairs,
                Math.Clamp(pts, -MaxLeadPts, MaxLeadPts), seq, nowUtcTicks);
        }

        /// <summary>근거 문구(콜드): "🌐 선행 파급 +2점: 빅테크 +120bp × 상관 0.42 · 10년물 −5bp · BTC 변동성 110%".</summary>
        public static string Text(in PlutoSpilloverSignal s)
        {
            if (!s.HasData) return "🌐 선행 파급: 판독 없음(빅테크 · 금리 · BTC 표본 미수집 — 점수 조정 0)";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("🌐 선행 파급 ");
            sb.Append(s.LeadPts >= 0 ? "+" : "").Append(s.LeadPts.ToString(ci)).Append("점:");
            sb.Append(s.Has(PlutoSpilloverField.BigTech) ? " 빅테크 " + Signed(s.BigTechReturnBp) + "bp" : " 빅테크 미수집");
            sb.Append(s.Has(PlutoSpilloverField.LeadCorr) ? " × 상관 " + (s.LeadCorrX1000 / 1000m).ToString("0.00", ci) : " (상관 미추정 " + s.Pairs.ToString(ci) + "/" + MinPairs.ToString(ci) + "쌍)");
            sb.Append(s.Has(PlutoSpilloverField.Us10y) ? " · 10년물 " + Signed(s.Us10yChangeBp) + "bp" : " · 10년물 미결선");
            sb.Append(s.Has(PlutoSpilloverField.BtcVol) ? " · BTC 변동성 " + s.BtcVolRatioPct.ToString(ci) + "%" : " · BTC 변동성 미추정");
            if (s.IsShock) sb.Append(" · ⚠ 충격");
            return sb.ToString();
        }

        private static string Signed(int v) => (v > 0 ? "+" : "") + v.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 데이터 레이크 → 파급 레인 공급기(Track B 콜드 · 스크래치 구축 1회). 바스켓 = 구성 종목 봉간 수익률의 같은 일 번호 동일 가중 평균(판독 종목만 · 결측 = 평균에서 제외).
    /// </summary>
    public sealed class PlutoSpilloverFeeder
    {
        public static readonly string[] BigTech = { "AAPL", "MSFT", "NVDA", "AMZN", "GOOGL", "META" };
        public static readonly string[] KrxLeaders = { "005930", "000660", "373220", "207940", "005380" };
        public const string Btc = "KRW-BTC";

        private readonly PlutoMultiAssetDataLake _lake;
        private readonly PlutoOhlcvBar[] _bars = new PlutoOhlcvBar[PlutoMultiAssetDataLake.BarsPerSlot];
        private readonly long[] _dayKey = new long[PlutoMultiAssetDataLake.BarsPerSlot * 8];
        private readonly long[] _daySum = new long[PlutoMultiAssetDataLake.BarsPerSlot * 8];
        private readonly int[] _dayCnt = new int[PlutoMultiAssetDataLake.BarsPerSlot * 8];

        public PlutoSpilloverFeeder(PlutoMultiAssetDataLake lake) => _lake = lake ?? throw new ArgumentNullException(nameof(lake));

        /// <summary>3대 레인 갱신(반환 = 기록 표본 수). 레이크 판독 없는 종목 = 건너뜀(날조 0).</summary>
        public int Feed(PlutoCrossAssetSpillover target)
        {
            ArgumentNullException.ThrowIfNull(target);
            int w = FeedBasket(target, PlutoLeadLane.Krx, PlutoScreenMarket.Krx, KrxLeaders);
            w += FeedBasket(target, PlutoLeadLane.BigTech, PlutoScreenMarket.Us, BigTech);
            if (_lake.Layout.TryFindSlot(PlutoScreenMarket.Crypto, PlutoMultiAssetUniverseCatalog.KeyOf(PlutoScreenMarket.Crypto, Btc), out int slot))
            {
                int n = _lake.TryCopyBars(slot, _bars, out _);
                if (n > 1) w += target.ObserveBars(PlutoLeadLane.Btc, _bars.AsSpan(0, n), daily: false);
            }
            return w;
        }

        private int FeedBasket(PlutoCrossAssetSpillover target, PlutoLeadLane lane, PlutoScreenMarket market, string[] codes)
        {
            int days = 0;
            foreach (var code in codes)
            {
                if (!_lake.Layout.TryFindSlot(market, PlutoMultiAssetUniverseCatalog.KeyOf(market, code), out int slot)) continue;
                int n = _lake.TryCopyBars(slot, _bars, out _);
                for (int i = 1; i < n; i++)
                {
                    ref readonly var p = ref _bars[i - 1];
                    ref readonly var c = ref _bars[i];
                    if (!p.IsValid || !c.IsValid) continue;
                    long key = PlutoCrossAssetSpillover.KeyOf(c.TimeUtcTicks, daily: true);
                    int d = Array.IndexOf(_dayKey, key, 0, days);
                    if (d < 0)
                    {
                        if (days == _dayKey.Length) continue;
                        d = days++;
                        _dayKey[d] = key; _daySum[d] = 0; _dayCnt[d] = 0;
                    }
                    _daySum[d] += PlutoCrossAssetSpillover.ReturnBp(p.Close, c.Close);
                    _dayCnt[d]++;
                }
            }
            if (days == 0) return 0;
            for (int i = 0; i < days; i++) _daySum[i] /= Math.Max(1, _dayCnt[i]);   // 합계 → 동일 가중 평균(정렬 전 · 키와 같은 칸)
            Array.Sort(_dayKey, _daySum, 0, days);                                   // 키 오름차순(평균 동반 정렬)
            int written = 0;
            for (int i = 0; i < days; i++)
                if (target.ObserveReturn(lane, _dayKey[i], (int)_daySum[i])) written++;
            return written;
        }
    }
}
