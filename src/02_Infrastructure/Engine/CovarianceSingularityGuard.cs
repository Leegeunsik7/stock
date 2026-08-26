// ============================================================================
// CovarianceSingularityGuard — v7.2.0 ★Think Beyond Gemini: 공분산 특이점 자율 정규화 방화벽
//   "Gershgorin-Bounded Minimal Adaptive Ridge" (원판정리 하한 기반 최소 적응형 리지)
//
//  ── 왜 기존/제미나이안(고정 지터·고정 shrinkage)을 초월하는가 ────────────────────
//  기존 코드는 비양정치(NPD) 위험을 '고정 지터'로 때운다: MonteCarlo.Cholesky 는 대각합이 ≤0 이면
//  1e-12 로 강제, MarkowitzOptimizer 는 Σ+1e-4·I 를 무조건 더한다. 두 방식 다 결함이 있다:
//    · 고정 지터가 '너무 작으면' 시장 유동성 고갈(상관 1 수렴)에서 여전히 특이 → NaN/DivByZero 폭발.
//    · 고정 지터가 '너무 크면' 참 공분산 구조를 왜곡해 VaR 을 상시 과대/과소 추정(정상 구간마저 오염).
//  이 방화벽은 Gershgorin 원판정리로 '실제로 필요한 만큼만' 정규화한다:
//    임의 대칭행렬 Σ 의 모든 고유값은 원판 ∪_i [a_ii − R_i, a_ii + R_i] (R_i=Σ_{j≠i}|a_ij|) 안에 있다.
//    ⇒ λ_min(Σ) ≥ min_i (a_ii − R_i) =: b   (O(n²) 로 저렴하게 하한 계측 — 고유값 완전분해 O(n³) 불필요).
//    b < ε 이면(특이점 임박) 대각에 λ = ε − b 만 로딩 ⇒ 새 하한 = b + λ = ε > 0 ⇒ PD '수학적 보장',
//    그것도 '최소 왜곡'(참 구조를 최대 보존)으로. ε 이상이면 손대지 않는다(무-왜곡).
//  ∴ 정상 구간엔 개입 0(무-왜곡), 위기 구간엔 딱 필요한 만큼만(적응형), NaN/Inf(데이터 파열)엔 fail-closed.
//
//  ★불변 제약: δ=0 핫패스(in-place Span 연산·사전할당 무관·Interlocked — new/box/LINQ 0. 퓨즈 트립 문자열은
//    파국 1회 래치 경로라 무관). 결정론(순수 산술). liveArmed:false(순수 수치 방어 — 발주 경로 참조 0).
//    fail-closed(NaN/Inf/음-대각 = 특이점 파열 → 퓨즈 래치 + 옵션 OrderAttestationService.ForceFreeze,
//    호출자는 무상관 보수 VaR 로 강등). ★한국색상은 UI 계층. 여기선 상태·계수만.
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>공분산 특이점 방화벽 상태(텔레메트리·UI 배지). 값이 클수록 위중.</summary>
    public enum CovGuardState
    {
        Nominal      = 0,   // 정상 — λ_min 하한 ≥ ε, 무-개입(무-왜곡)
        Regularized  = 1,   // 특이점 임박 — 최소 적응형 리지 주입(PD 복원)
        FuseTripped  = 2,    // 특이점 파열(NaN/Inf/음-대각) — fail-closed 보수 모델 강등
    }

    /// <summary>
    /// Gershgorin 하한 기반 공분산 특이점 자율 정규화 방화벽. 대칭 공분산(행 우선 n×n Span)에 in-place 로
    /// '필요한 만큼만' 적응형 리지를 주입해 양정치(PD)를 보장하고, 데이터 파열(NaN/Inf) 시 fail-closed 로 래치한다.
    /// </summary>
    public sealed class CovarianceSingularityGuard
    {
        private readonly double _epsilon;                       // PD 최소 고유값 임계(양수 하한 목표)
        private readonly OrderAttestationService? _attestation; // 옵션 이중 봉인(fail-closed)

        private long _checks, _ridgeInjections, _fuseTrips;
        private double _lastBound = double.NaN;                 // 최근 Gershgorin 최소 고유값 하한
        private double _lastRidge;                              // 최근 주입 리지 λ
        private int  _fuseTripped;                              // 0/1 — 특이점 파열 래치(Reset 로만 해제)
        private int  _lastState;

        /// <param name="epsilon">PD 목표 최소 고유값(하한). 공분산 스케일(일수익률 %²) 대비 작은 양수. 기본 1e-9.</param>
        public CovarianceSingularityGuard(double epsilon = 1e-9, OrderAttestationService? attestation = null)
        {
            _epsilon = epsilon > 0 ? epsilon : 1e-9;
            _attestation = attestation;
        }

        public CovGuardState State        => (CovGuardState)Volatile.Read(ref _lastState);
        public bool   FuseTripped         => Volatile.Read(ref _fuseTripped) == 1;
        public long   Checks              => Interlocked.Read(ref _checks);
        public long   RidgeInjections     => Interlocked.Read(ref _ridgeInjections);
        public long   FuseTrips           => Interlocked.Read(ref _fuseTrips);
        public double LastGershgorinBound => Volatile.Read(ref _lastBound);
        public double LastRidge           => Volatile.Read(ref _lastRidge);
        public double Epsilon             => _epsilon;

        /// <summary>
        /// Gershgorin 원판정리 최소 고유값 하한: min_i (a_ii − Σ_{j≠i}|a_ij|). δ=0(O(n²)·무할당).
        /// 대각/비대각 어디든 NaN/Inf 면 double.NaN 반환(호출자 fail-closed 신호). cov 는 행 우선 n×n.
        /// </summary>
        public static double GershgorinLowerBound(ReadOnlySpan<double> cov, int n)
        {
            if (n <= 0 || cov.Length < n * n) return double.NaN;
            double minBound = double.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                double diag = cov[i * n + i];
                double radius = 0.0;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    radius += Math.Abs(cov[i * n + j]);
                }
                double bound = diag - radius;
                if (double.IsNaN(bound) || double.IsInfinity(bound)) return double.NaN;
                if (bound < minBound) minBound = bound;
            }
            return minBound;
        }

        /// <summary>
        /// 공분산(행 우선 n×n Span)을 in-place 로 특이점 방어한다. δ=0(무할당·정상 경로).
        ///  · λ_min 하한 ≥ ε: 무-개입(Nominal). 참 구조 무-왜곡.
        ///  · 0 ≤ λ_min 하한 &lt; ε: 대각에 λ = ε − 하한 주입(Regularized) → PD 보장·최소 왜곡.
        ///  · 하한이 음수: 동일 공식으로 λ = ε − 하한(&gt; ε) 주입 → 음정치도 PD 로 승격(Regularized).
        ///  · NaN/Inf(데이터 파열): 퓨즈 래치(FuseTripped) + false 반환 → 호출자는 무상관 보수 VaR 로 강등.
        /// 반환: true = PD 사용가능(Nominal/Regularized), false = 퓨즈 트립(보수 모델 강등).
        /// </summary>
        public bool Regularize(Span<double> cov, int n)
        {
            Interlocked.Increment(ref _checks);
            if (Volatile.Read(ref _fuseTripped) == 1) { Volatile.Write(ref _lastState, (int)CovGuardState.FuseTripped); return false; }

            double bound = GershgorinLowerBound(cov, n);
            Volatile.Write(ref _lastBound, bound);

            if (double.IsNaN(bound))   // NaN/Inf 대각·비대각 = 특이점 파열(신뢰 불가)
            {
                Volatile.Write(ref _lastRidge, 0.0);
                TripFuse("공분산 NaN/Inf — 특이점 파열(데이터 신뢰 불가)");
                return false;
            }

            if (bound < _epsilon)
            {
                double ridge = _epsilon - bound;   // ★적응형: 하한을 정확히 ε 로 끌어올리는 최소 대각 로딩
                for (int i = 0; i < n; i++) cov[i * n + i] += ridge;
                Volatile.Write(ref _lastRidge, ridge);
                Interlocked.Increment(ref _ridgeInjections);
                Volatile.Write(ref _lastState, (int)CovGuardState.Regularized);
            }
            else
            {
                Volatile.Write(ref _lastRidge, 0.0);
                Volatile.Write(ref _lastState, (int)CovGuardState.Nominal);
            }
            return true;
        }

        /// <summary>특이점 퓨즈 트립(1회 래치·Reset 로만 해제) — 이중 봉인(옵션 ForceFreeze), try/catch 격리.</summary>
        private void TripFuse(string reason)
        {
            Volatile.Write(ref _lastState, (int)CovGuardState.FuseTripped);
            if (Interlocked.CompareExchange(ref _fuseTripped, 1, 0) != 0) return;   // 1회만
            Interlocked.Increment(ref _fuseTrips);
            try { _attestation?.ForceFreeze($"공분산 특이점 파열 — {reason}. VaR 무상관 보수 모델 강등."); } catch { }
        }

        /// <summary>새 수익률 창(재계측)에서 퓨즈·상태 리셋 — 신선 데이터에 재기회 부여. 계수는 누적 보존.</summary>
        public void Reset()
        {
            Volatile.Write(ref _fuseTripped, 0);
            Volatile.Write(ref _lastState, (int)CovGuardState.Nominal);
        }
    }
}
