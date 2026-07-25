// ============================================================================
// PortfolioRiskVaREngine — v7.2.0 무할당(δ=0) 실시간 포트폴리오 VaR & 스트레스 테스터
//
//  보유 종목 일수익률 행렬(PortfolioData.R, %)과 시총 비중(Weights)에서 Parametric(분산-공분산)·
//  Historical(경험분포)·Monte Carlo(상관보존 시뮬)·Stress(거시 시나리오) Value-at-Risk 를 95%/99%
//  신뢰수준으로 산출한다. 산출 VaR 이 한도를 초과하면 리스크 밸브(LimitBreached)를 세워 신규 발주를
//  '조이기만(tighten-only)' 하는 blockWhile 1항으로 배선한다(발주 경로 무접촉).
//
//  ── 재사용(오라클) & δ=0 정직 회계 ──────────────────────────────────────────
//  · 수학은 MarkowitzOptimizer.Covariance(표본 T-1)·MonteCarlo(Cholesky·상관보존)·PortfolioData.R 를
//    '검증 오라클'로 삼되, 여기선 사전할당 버퍼 + Span in-place 로 δ=0 핫패스로 정제한다.
//  · 콜드(LoadReturns·수익률 창 갱신 시): 행렬 평탄화·평균·공분산 계측(O(n²T)). 여기서만 성장 할당(재사용).
//  · 핫패스(ComputeVar·초당 수만 회): 공분산 특이점 가드(Gershgorin 적응형 리지) → parametric wᵀΣw →
//    historical 분위수(사전할당 정렬) → stress. 프레임당 new/box/LINQ 0(사전할당 _covReg·_pret 재사용).
//
//  ── ★특이점 방어 인터락(CovarianceSingularityGuard) ────────────────────────
//  시장 유동성 고갈로 공분산이 특이/비양정치(NPD)로 무너지면 wᵀΣw<0(음분산)·Cholesky NaN 폭발이 난다.
//  Gershgorin 하한으로 '필요한 만큼만' 적응형 리지를 주입해 PD 를 수학적으로 보장하고, 데이터 파열(NaN/Inf)
//  시엔 퓨즈가 트립돼 '무상관 보수 VaR'(대각 vol 의 |가중| 합·분산효과 0=최악)로 fail-closed 강등한다.
//
//  ★불변 제약: δ=0(핫패스 힙 0)·결정론(주입 시드·순수 산술)·fail-closed(특이점→보수 모델·밸브는 tighten-only)·
//    liveArmed:false(순수 관측/연산 — IBroker·소켓·onSend·PublishRaw 참조 0. 밸브는 '더 안전한 방향'으로만
//    무장 술어를 억제할 뿐 발주를 유발하는 경로가 존재하지 않는다). ★한국색상은 UI 계층.
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>거시 스트레스 시나리오(값타입). 지수 급락·환율 스파이크·금리 충격을 불리 방향 합성.</summary>
    public readonly struct StressScenario
    {
        public readonly double IndexShockPct;        // 지수 급락(음수 %, 예: -10)
        public readonly double FxAdversePct;         // USD 자산 불리 환율 이동(양수 %, 예: 7 — 원화 강세로 USD평가 하락)
        public readonly double UsdWeightFraction;    // 포트 USD 익스포저 비중(0..1)
        public readonly double RateShockEquityPct;   // 금리 충격의 주식 낙폭 프록시(양수 %, 예: 3)

        public StressScenario(double indexShockPct, double fxAdversePct, double usdWeightFraction, double rateShockEquityPct)
        {
            IndexShockPct = indexShockPct; FxAdversePct = fxAdversePct;
            UsdWeightFraction = usdWeightFraction; RateShockEquityPct = rateShockEquityPct;
        }

        /// <summary>지수 -5% + 환율 5% + 금리(주식 -1.5%) 복합 불리.</summary>
        public static StressScenario Crash5(double usdFrac)  => new(-5.0,  5.0, usdFrac, 1.5);
        /// <summary>지수 -10% + 환율 7% + 금리(주식 -3%) 복합 불리(표준 위기).</summary>
        public static StressScenario Crash10(double usdFrac) => new(-10.0, 7.0, usdFrac, 3.0);

        /// <summary>복합 불리 추정 낙폭 %(모두 불리 방향 합산 — '동시 악화' 보수 가정).</summary>
        public double LossPct =>
            Math.Abs(IndexShockPct)
            + Math.Abs(FxAdversePct) * Math.Clamp(UsdWeightFraction, 0.0, 1.0)
            + Math.Abs(RateShockEquityPct);
    }

    /// <summary>VaR 산출 결과(블리터블 값타입·힙 0). Pct=수익률 %, Krw=원화 추정 손실액.</summary>
    public readonly struct VaRResult
    {
        public readonly double SigmaDailyPct;    // 포트 일변동성 σ_p(%)
        public readonly double Var95Pct, Var99Pct;         // parametric VaR(%)
        public readonly long   Var95Krw, Var99Krw;         // parametric VaR(₩)
        public readonly double HistVar95Pct, HistVar99Pct; // historical VaR(%)
        public readonly long   HistVar99Krw;
        public readonly double StressLossPct;    // 스트레스 추정 낙폭(%)
        public readonly long   StressLossKrw;
        public readonly double GershgorinBound;  // 공분산 최소 고유값 하한
        public readonly double RidgeApplied;     // 주입 적응형 리지 λ
        public readonly int    CovGuardStateCode;// 0 정상/1 정규화/2 퓨즈
        public readonly bool   Conservative;     // 퓨즈 → 무상관 보수 모델로 산출됨
        public readonly bool   LimitBreached;    // VaR/스트레스 한도 초과 → 밸브 조임

        public VaRResult(double sigma, double v95, double v99, long v95k, long v99k,
                         double hv95, double hv99, long hv99k, double stressPct, long stressKrw,
                         double gersh, double ridge, int guardState, bool conservative, bool breached)
        {
            SigmaDailyPct = sigma; Var95Pct = v95; Var99Pct = v99; Var95Krw = v95k; Var99Krw = v99k;
            HistVar95Pct = hv95; HistVar99Pct = hv99; HistVar99Krw = hv99k;
            StressLossPct = stressPct; StressLossKrw = stressKrw;
            GershgorinBound = gersh; RidgeApplied = ridge; CovGuardStateCode = guardState;
            Conservative = conservative; LimitBreached = breached;
        }
    }

    /// <summary>무할당 실시간 포트폴리오 VaR 엔진. LoadReturns(콜드) → ComputeVar/MonteCarloVarPct(δ=0 핫).</summary>
    public sealed class PortfolioRiskVaREngine
    {
        // 표준정규 분위수 z(95%)=1.645, z(99%)=2.326 (단측 VaR).
        private const double Z95 = 1.6448536269514722;
        private const double Z99 = 2.3263478740408408;

        private readonly CovarianceSingularityGuard _guard;

        // 사전할당(콜드 성장·재사용) — 핫패스 무할당.
        private int _n, _t;
        private double[] _flat  = Array.Empty<double>();   // n×T 행 우선 일수익률(%)
        private double[] _w      = Array.Empty<double>();   // n 비중
        private double[] _mean   = Array.Empty<double>();   // n 평균수익률
        private double[] _cov    = Array.Empty<double>();   // n×n 원 공분산(콜드 계측)
        private double[] _covReg = Array.Empty<double>();   // n×n 정규화 스크래치(핫)
        private double[] _pret   = Array.Empty<double>();   // T 포트 일수익률(historical 스크래치)
        private double[] _chol   = Array.Empty<double>();   // n×n Cholesky 하삼각(MC 스크래치)
        private double[] _mc     = Array.Empty<double>();   // MC 시뮬 결과(별도 성장)
        private double[] _z      = Array.Empty<double>();   // MC 표준정규 충격 버퍼(n·재사용·δ=0)
        private bool _loaded;

        private int  _limitBreached;   // 0/1 — 리스크 밸브(신규 발주 tighten-only)
        private long _computes;

        public PortfolioRiskVaREngine(CovarianceSingularityGuard? guard = null, double epsilon = 1e-9,
                                      OrderAttestationService? attestation = null)
        {
            _guard = guard ?? new CovarianceSingularityGuard(epsilon, attestation);
        }

        public CovarianceSingularityGuard Guard => _guard;
        public bool   IsLoaded      => _loaded;
        public int    AssetCount    => _n;
        public int    Days          => _t;
        public long   Computes      => Interlocked.Read(ref _computes);

        /// <summary>★리스크 밸브(blockWhile 1항 tightener) — 한도 초과면 true → 신규 발주 억제(더 안전한 방향만).</summary>
        public bool LimitBreached => Volatile.Read(ref _limitBreached) == 1;

        /// <summary>
        /// 콜드: 새 수익률 행렬을 로드하고 평균·공분산을 재계측한다(수익률 창 갱신 시). R 은 % 일수익률(ragged —
        /// 각 자산 최근 공통 T=min 길이 사용), weights 합 1. 특이점 퓨즈 리셋(신선 데이터 재기회).
        /// </summary>
        public bool LoadReturns(double[][] R, double[] weights)
        {
            if (R == null || weights == null || R.Length == 0 || weights.Length != R.Length) { _loaded = false; return false; }
            int n = R.Length;
            int t = int.MaxValue;
            for (int i = 0; i < n; i++) { int li = R[i]?.Length ?? 0; if (li < t) t = li; }
            if (t < 2) { _loaded = false; return false; }

            EnsureCapacity(n, t);
            _n = n; _t = t;

            // 각 자산 최근 T개(꼬리) 평탄화(행 우선) + 평균.
            for (int i = 0; i < n; i++)
            {
                var ri = R[i];
                int off = ri.Length - t;
                double sum = 0;
                for (int k = 0; k < t; k++) { double v = ri[off + k]; _flat[i * t + k] = v; sum += v; }
                _mean[i] = sum / t;
                _w[i] = weights[i];
            }

            // 표본 공분산(T-1) — MarkowitzOptimizer.Covariance 와 동일 규약(오라클 정합). 대칭.
            for (int i = 0; i < n; i++)
                for (int j = i; j < n; j++)
                {
                    double s = 0;
                    double mi = _mean[i], mj = _mean[j];
                    for (int k = 0; k < t; k++) s += (_flat[i * t + k] - mi) * (_flat[j * t + k] - mj);
                    double c = t > 1 ? s / (t - 1) : 0;
                    _cov[i * n + j] = c; _cov[j * n + i] = c;
                }

            _guard.Reset();
            _loaded = true;
            return true;
        }

        /// <summary>
        /// 핫패스(δ=0): Gershgorin 특이점 가드 → parametric·historical·stress VaR + 밸브 상태. 미로드면 기본값.
        /// </summary>
        /// <param name="portfolioValueKrw">현재 총 평가액(₩) — % VaR 을 원화 손실액으로 환산.</param>
        /// <param name="var99LimitKrw">99% VaR/스트레스 한도(₩). 초과 시 LimitBreached(밸브 조임). 0 이하=한도 없음.</param>
        /// <param name="stress">스트레스 시나리오(지수·환율·금리 복합 불리).</param>
        public VaRResult ComputeVar(decimal portfolioValueKrw, double var99LimitKrw, in StressScenario stress)
        {
            Interlocked.Increment(ref _computes);
            int n = _n, t = _t;
            double value = (double)portfolioValueKrw;
            if (!_loaded || n == 0 || t < 2)
                return new VaRResult(0,0,0,0,0,0,0,0,0,0, double.NaN, 0, (int)CovGuardState.Nominal, false, false);

            // ── 공분산 특이점 가드: _cov → _covReg 복사 후 in-place 적응형 리지(δ=0). NaN/Inf → 퓨즈 → 보수 모델.
            Array.Copy(_cov, _covReg, n * n);
            bool pd = _guard.Regularize(_covReg.AsSpan(0, n * n), n);
            double gersh = _guard.LastGershgorinBound;
            double ridge = _guard.LastRidge;
            int guardState = (int)_guard.State;

            double sigmaPct;
            bool conservative = false;
            if (!pd)
            {
                // ★fail-closed 무상관 보수 모델: 분산효과 0(완전상관 가정) → σ_p = Σ_i |w_i|·σ_i. 대각 vol 이 NaN/음수면
                //   보수 기본 vol(일 10%)로 대체 → 큰 VaR → 밸브 조임(안전 방향). (원 _cov 대각 사용 — _covReg 는 리지 오염.)
                double s = 0;
                for (int i = 0; i < n; i++)
                {
                    double vi = _cov[i * n + i];
                    double sd = (double.IsNaN(vi) || double.IsInfinity(vi) || vi < 0) ? 10.0 : Math.Sqrt(vi);
                    s += Math.Abs(_w[i]) * sd;
                }
                sigmaPct = s;
                conservative = true;
            }
            else
            {
                // ── Parametric: σ_p = √(wᵀ Σ_reg w). PD 보장 후라 항상 ≥0.
                double var = 0;
                for (int i = 0; i < n; i++)
                {
                    double wi = _w[i];
                    int roff = i * n;
                    for (int j = 0; j < n; j++) var += wi * _w[j] * _covReg[roff + j];
                }
                sigmaPct = var > 0 ? Math.Sqrt(var) : 0;
            }

            double v95 = Z95 * sigmaPct, v99 = Z99 * sigmaPct;   // parametric VaR(%)

            // ── Historical: 포트 일수익률 r_p(k)=Σ w_i R[i][k] → 정렬 → 5%/1% 분위수(손실 꼬리). δ=0(_pret 정렬).
            for (int k = 0; k < t; k++)
            {
                double rp = 0;
                for (int i = 0; i < n; i++) rp += _w[i] * _flat[i * t + k];
                _pret[k] = rp;
            }
            Array.Sort(_pret, 0, t);                     // double[] 원시 특화 in-place 정렬(δ=0)
            double hv95 = -PercentileAsc(_pret.AsSpan(0, t), 5.0);   // 5% 분위수(오름차순 → 하위=손실). 음수면 손실.
            double hv99 = -PercentileAsc(_pret.AsSpan(0, t), 1.0);
            if (hv95 < 0) hv95 = 0; if (hv99 < 0) hv99 = 0;

            // ── Stress: 복합 불리 추정 낙폭(%).
            double stressPct = stress.LossPct;

            long v95k    = ToKrw(v95, value);
            long v99k    = ToKrw(v99, value);
            long hv99k   = ToKrw(hv99, value);
            long stressk = ToKrw(stressPct, value);

            // ── 리스크 밸브(tighten-only): 99% VaR 또는 스트레스 낙폭이 한도 초과면 신규 발주 억제.
            bool breach = var99LimitKrw > 0 && (v99k > var99LimitKrw || stressk > var99LimitKrw || conservative);
            Volatile.Write(ref _limitBreached, breach ? 1 : 0);

            return new VaRResult(sigmaPct, v95, v99, v95k, v99k, hv95, hv99, hv99k,
                                 stressPct, stressk, gersh, ridge, guardState, conservative, breach);
        }

        /// <summary>
        /// Monte Carlo VaR(%): 정규화 공분산의 Cholesky 로 상관보존 다변량 정규 충격을 결정론 PRNG(주입 시드)로
        /// 생성해 1일 포트 수익률 분포를 시뮬(사전할당·δ=0)한 뒤 5%/1% 분위수를 산출한다. sims 클수록 정밀.
        /// </summary>
        public (double var95Pct, double var99Pct) MonteCarloVarPct(int sims, ulong seed = 0x9E3779B97F4A7C15UL)
        {
            int n = _n, t = _t;
            if (!_loaded || n == 0 || t < 2) return (0, 0);
            sims = Math.Clamp(sims, 100, 200_000);
            if (_mc.Length < sims) _mc = new double[sims];

            // 정규화 공분산 → Cholesky 하삼각 _chol(δ=0). 퓨즈면 무상관 보수 σ 로 즉시 반환(부작용 없음).
            Array.Copy(_cov, _covReg, n * n);
            if (!_guard.Regularize(_covReg.AsSpan(0, n * n), n))
            {
                double sc = 0;
                for (int i = 0; i < n; i++)
                {
                    double vi = _cov[i * n + i];
                    double sd = (double.IsNaN(vi) || double.IsInfinity(vi) || vi < 0) ? 10.0 : Math.Sqrt(vi);
                    sc += Math.Abs(_w[i]) * sd;
                }
                return (Z95 * sc, Z99 * sc);
            }
            CholeskyLower(_covReg, _chol, n);

            ulong rng = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
            var z = _z.AsSpan(0, n);   // ★사전할당 z 버퍼(루프 밖 재사용·δ=0 — stackalloc-in-loop 금지)
            for (int s = 0; s < sims; s++)
            {
                double dayPort = 0;
                for (int i = 0; i < n; i++) z[i] = NextNormal(ref rng);   // 상관보존 충격 shock_i=Σ_{k≤i} L[i][k]·z_k
                for (int i = 0; i < n; i++)
                {
                    double shock = 0;
                    int roff = i * n;
                    for (int k = 0; k <= i; k++) shock += _chol[roff + k] * z[k];
                    dayPort += _w[i] * (_mean[i] + shock);
                }
                _mc[s] = dayPort;
            }
            Array.Sort(_mc, 0, sims);                    // δ=0 in-place
            double v95 = -PercentileAsc(_mc.AsSpan(0, sims), 5.0);
            double v99 = -PercentileAsc(_mc.AsSpan(0, sims), 1.0);
            return (v95 < 0 ? 0 : v95, v99 < 0 ? 0 : v99);
        }

        // ── 헬퍼(모두 δ=0) ────────────────────────────────────────────────
        private void EnsureCapacity(int n, int t)
        {
            if (_flat.Length   < n * t) _flat   = new double[n * t];
            if (_w.Length      < n)     _w      = new double[n];
            if (_mean.Length   < n)     _mean   = new double[n];
            if (_cov.Length    < n * n) _cov    = new double[n * n];
            if (_covReg.Length < n * n) _covReg = new double[n * n];
            if (_pret.Length   < t)     _pret   = new double[t];
            if (_chol.Length   < n * n) _chol   = new double[n * n];
            if (_z.Length      < n)     _z      = new double[n];
        }

        private static long ToKrw(double pct, double value)
        {
            double krw = pct / 100.0 * value;
            if (double.IsNaN(krw) || double.IsInfinity(krw)) return 0;
            if (krw < 0) krw = 0;
            return (long)Math.Round(krw);
        }

        // 오름차순 정렬 배열의 p 백분위(MonteCarlo.Percentile 규약 동일).
        private static double PercentileAsc(ReadOnlySpan<double> sortedAsc, double p)
        {
            int len = sortedAsc.Length;
            if (len == 0) return 0;
            int idx = (int)Math.Round(p / 100.0 * (len - 1));
            if (idx < 0) idx = 0; else if (idx >= len) idx = len - 1;
            return sortedAsc[idx];
        }

        // 하삼각 Cholesky(행 우선). 대각합 ≤0 방어(정규화 후엔 PD 라 미발생, 방어적 지터).
        private static void CholeskyLower(ReadOnlySpan<double> a, Span<double> L, int n)
        {
            for (int i = 0; i < n; i++)
                for (int j = 0; j <= i; j++)
                {
                    double sum = a[i * n + j];
                    for (int k = 0; k < j; k++) sum -= L[i * n + k] * L[j * n + k];
                    if (i == j) L[i * n + j] = Math.Sqrt(sum <= 0 ? 1e-12 : sum);
                    else        L[i * n + j] = L[j * n + j] > 0 ? sum / L[j * n + j] : 0;
                }
        }

        // 결정론 SplitMix64 → [0,1) 균등 → Box-Muller 표준정규(δ=0·무할당·재현).
        private static double NextNormal(ref ulong state)
        {
            double u1 = 1.0 - NextUnit(ref state);
            double u2 = 1.0 - NextUnit(ref state);
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
        private static double NextUnit(ref ulong state)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / 9007199254740992.0);   // 53비트 → [0,1)
        }
    }
}
