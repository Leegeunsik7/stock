// ============================================================================
// QFutureValue — S28 Mission 2: 미래가치 시뮬레이터 연산 교량 + 락-프리 실시간 패리티
//
//  ★결정론·무할당: 복리 성장 V = Σ A_i (1+r_i)^t 를 고정소수점(long raw·10^8) 정수 산술로만 계산한다.
//  부동소수점·RNG 를 쓰지 않으므로 동일 입력은 비트 단위로 동일 곡선을 낸다(소수점 오차 없음). 변동성은
//  '몬테카를로 난수'가 아니라 결정론적 분위 밴드(closed-form quantile)로 실현한다 — σ·√t 랜덤워크 스케일링의
//  ±k 밴드(comonotonic·상관 1 가정의 가장 넓은 정직한 봉투). 난수 시뮬을 사칭하지 않는다(정직 표기).
//
//  ── 락-프리 실시간 교량(LLM ➔ Core Simulator ➔ Chart UI 시임) ─────────────────
//  Gemini 룰2 — 사용자가 "비트코인 10% 더 사면?"이라 던지면 LLM 은 이를 ShadowMutation(가변 변수 구조체)으로
//  '인지'만 하고 백엔드에 주입한다. ScenarioPublisher 는 사전할당 섀도우 버퍼에 구조체 스왑(곱 1회)만으로
//  변형을 적용하고 곡선을 재산출한다 — 힙 할당 0(δ=0). 발행은 시퀀스락(seqlock): 단일 라이터가 버전을
//  홀수(쓰는 중)→짝수(안정)로 올리며 메모리 배리어로 가시성을 강제하고, 무잠금 리더(차트 UI)는 버전을
//  앞뒤로 재독출해 torn-read 면 재시도한다(찢긴 스냅샷을 그래프에 그리지 않음 — fail-closed).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>자산별 성장 가정 — 연 기대수익률(drift)·연 변동성(vol)을 분수 raw 로 보관(0.07 → 7,000,000).</summary>
    public readonly record struct GrowthAssumption
    {
        public long DriftRaw { get; }   // r (음수 허용, 단 r > -1)
        public long VolRaw   { get; }   // σ ≥ 0
        private GrowthAssumption(long d, long v) { DriftRaw = d; VolRaw = v; }

        /// <summary>분수(0.07=7%)로 생성. 변동성 음수는 거부.</summary>
        public static GrowthAssumption Of(decimal driftFraction, decimal volFraction)
        {
            if (volFraction < 0m) throw new ArgumentOutOfRangeException(nameof(volFraction), "변동성은 음수 불가");
            return new GrowthAssumption(QMath.FromDecimal(driftFraction), QMath.FromDecimal(volFraction));
        }
    }

    /// <summary>내부 고정소수점 보조 — 정수 제곱근(결정론 Newton, Int128 무할당).</summary>
    internal static class QGrowthMath
    {
        /// <summary>raw 값 x(=x/Scale)의 √ 를 raw 로. resultRaw = isqrt(x·Scale). 음수 거부.</summary>
        public static long SqrtRaw(long xRaw)
        {
            if (xRaw < 0) throw new ArgumentOutOfRangeException(nameof(xRaw), "음수 제곱근 불가");
            if (xRaw == 0) return 0;
            Int128 n = (Int128)xRaw * QMath.Scale;       // (resultRaw)^2 = xRaw·Scale
            Int128 x = n, y = (n + 1) / 2;               // Newton 정수 제곱근(floor)
            while (y < x) { x = y; y = (x + n / x) / 2; }
            return checked((long)x);
        }
    }

    /// <summary>
    /// Mission 2 — 미래가치 엔진. 자산별 현재가치(base 통화 raw)·성장 가정으로 연차 곡선(중앙/낙관/비관)을
    /// 호출자 버퍼에 기입한다(δ=0). 오버플로·무효 입력(1+r ≤ 0, 길이 불일치, 음수 가치) 시 ForceFreeze + false.
    /// ★적대적 리뷰(minor) — 복리 factor 는 매 연차 floor 절사돼 한 방향(하향)으로만 편향된다(절사 잔여가 다음 연차로
    ///   복리). 이는 '오차 상쇄'가 아니라 결정론적 보수 편향이며, 미래가치를 결코 '과대' 표시하지 않는다(고래 규모일수록
    ///   절사 누적이 커지나 항상 under-state). 멘토가 기대를 부풀리지 않는 안전 방향 — 청산 코어의 보수 편향과 동일 사상.
    /// </summary>
    public static class FutureValueEngine
    {
        private const long K_RAW = 164_500_000L;   // 분위 계수 k≈1.645 (단측 95%) — raw(1.645·10^8)

        public static bool Project(ReadOnlySpan<QMoney> presentBase, ReadOnlySpan<GrowthAssumption> assumptions,
                                   int years, Span<long> medianOut, Span<long> optimisticOut, Span<long> pessimisticOut,
                                   OrderAttestationService? attestation)
        {
            int n = presentBase.Length;
            if (years < 0 || assumptions.Length != n
                || medianOut.Length != years + 1 || optimisticOut.Length != years + 1 || pessimisticOut.Length != years + 1)
            { Freeze(attestation, "미래가치: 길이/연차 불일치 — fail-closed"); return false; }

            medianOut.Clear(); optimisticOut.Clear(); pessimisticOut.Clear();
            try
            {
                for (int i = 0; i < n; i++)
                {
                    long present = presentBase[i].Raw;
                    if (present < 0) { Freeze(attestation, "미래가치: 음수 현재가치(poison) — fail-closed"); return false; }
                    long baseRaw = QMath.Add(QMath.Scale, assumptions[i].DriftRaw);   // (1 + r)
                    if (baseRaw <= 0) { Freeze(attestation, "미래가치: (1+r) ≤ 0 — 발산/소멸 가정 거부, fail-closed"); return false; }
                    long volRaw = assumptions[i].VolRaw;

                    long factor = QMath.Scale;   // t=0 → (1+r)^0 = 1
                    for (int t = 0; t <= years; t++)
                    {
                        long mid = QMath.Notional(present, factor, roundUp: false);   // A_i·(1+r)^t
                        medianOut[t] = QMath.Add(medianOut[t], mid);

                        // 분위 밴드: kσ√t. t=0 → √0=0 → 밴드 0(연차0=현재가치). 비관은 0 하한(전손 이하 불가).
                        long sqrtT  = QGrowthMath.SqrtRaw(QMath.Scalar(QMath.Scale, t));   // √t (raw)
                        long kVol   = QMath.Notional(K_RAW, QMath.Notional(volRaw, sqrtT, false), false);
                        long upMul  = QMath.Add(QMath.Scale, kVol);
                        long dnMul  = QMath.Scale - kVol; if (dnMul < 0) dnMul = 0;
                        optimisticOut[t]  = QMath.Add(optimisticOut[t],  QMath.Notional(mid, upMul, false));
                        pessimisticOut[t] = QMath.Add(pessimisticOut[t], QMath.Notional(mid, dnMul, false));

                        if (t < years) factor = QMath.Notional(factor, baseRaw, roundUp: false);   // 다음 연차로 복리
                    }
                }
                return true;
            }
            catch (OverflowException)
            { Freeze(attestation, "미래가치: 정밀도 오버플로(고래 포트폴리오) — 중단, fail-closed"); return false; }
        }

        private static void Freeze(OrderAttestationService? att, string msg) { try { att?.ForceFreeze(msg); } catch { } }
    }

    /// <summary>가상 시나리오(What-If) 변형 — 인메모리 섀도우 슬롯의 가변 변수(Gemini 룰2). 값 타입(무할당).</summary>
    public enum MutationKind : byte { None = 0, ScaleQty }

    public readonly record struct ShadowMutation(MutationKind Kind, string? Symbol, AssetClass? Class, decimal Factor)
    {
        public static ShadowMutation None => default;
        /// <summary>특정 심볼 비중 배수(1.10 = +10%).</summary>
        public static ShadowMutation ScaleSymbol(string symbol, decimal factor) => new(MutationKind.ScaleQty, symbol, null, factor);
        /// <summary>자산군 전체 비중 배수.</summary>
        public static ShadowMutation ScaleClass(AssetClass cls, decimal factor) => new(MutationKind.ScaleQty, null, cls, factor);
    }

    /// <summary>
    /// Mission 2 — 락-프리 실시간 발행자(seqlock). 단일 라이터(Recompute)가 섀도우 변형을 사전할당 버퍼에
    /// 구조체 스왑으로 적용·재산출하며 버전을 홀↔짝으로 토글하고, 무잠금 리더(TryReadFrame)는 버전 재독출로
    /// torn-read 를 거른다. 정상 재산출 δ=0(곱·가산만). 산출 실패(오버플로 등) 시 _frozen 증가·곡선 미갱신.
    /// </summary>
    public sealed class ScenarioPublisher
    {
        private readonly int _n, _years;
        private readonly string[] _symbols;
        private readonly AssetClass[] _classes;
        private readonly QMoney[] _livePresent;     // base(KRW) 현재가치 — 라이브(불변 입력)
        private readonly GrowthAssumption[] _assumptions;
        private readonly QMoney[] _shadowPresent;   // 변형 적용 스크래치(δ=0 재사용)
        private readonly long[] _median, _opt, _pess;
        private readonly OrderAttestationService? _att;
        private long _version;     // 0=미발행, 짝=안정, 홀=쓰는 중
        private long _frozen;
        private int  _writerGate;  // 0=free, 1=writing — ★단일 라이터 계약 런타임 강제(CAS)

        public ScenarioPublisher(ReadOnlySpan<PortfolioSlot> slots, ReadOnlySpan<QMoney> livePresentBase,
                                 ReadOnlySpan<GrowthAssumption> assumptions, int years,
                                 OrderAttestationService? attestation = null)
        {
            if (years < 0) throw new ArgumentOutOfRangeException(nameof(years));
            _n = slots.Length;
            if (livePresentBase.Length != _n || assumptions.Length != _n)
                throw new ArgumentException("ScenarioPublisher: 슬롯/현재가치/가정 길이 불일치");
            _years = years;
            _symbols = new string[_n]; _classes = new AssetClass[_n];
            _livePresent = new QMoney[_n]; _shadowPresent = new QMoney[_n]; _assumptions = new GrowthAssumption[_n];
            for (int i = 0; i < _n; i++)
            {
                // ★적대적 리뷰(major) — 음수 현재가치(poison)는 구성 시점에 fail-fast(이후 Recompute 의 Notional throw 차단).
                if (livePresentBase[i].Raw < 0) throw new ArgumentException($"ScenarioPublisher: 음수 현재가치(poison) idx={i}");
                _symbols[i] = slots[i].Symbol; _classes[i] = slots[i].Class; _livePresent[i] = livePresentBase[i]; _assumptions[i] = assumptions[i];
            }
            _median = new long[years + 1]; _opt = new long[years + 1]; _pess = new long[years + 1];
            _att = attestation;
        }

        public int  Years       => _years;
        public int  AssetCount  => _n;
        public long Version     => Volatile.Read(ref _version);
        public long FrozenCount => Interlocked.Read(ref _frozen);

        /// <summary>
        /// 변형을 적용해 곡선을 재산출·발행. 무할당(정상). 실패 시 false(곡선 미갱신·_frozen++).
        /// ★단일 라이터 계약을 CAS 로 '런타임 강제': 동시 라이터는 seqlock torn-read 보장을 깨므로 두 번째는 fail-closed 거부.
        /// ★산술 오염(Overflow·ArgumentOutOfRange·DivideByZero — 음수/대형 배수, 음수 현재가치 등 현 경로의 도달 가능
        ///   예외 전부)을 try/catch 로 흡수해 fail-crash 아닌 fail-closed. 필터 밖 예외원은 현 경로에 없다(있다면 전파).
        /// </summary>
        public bool Recompute(in ShadowMutation m)
        {
            // 단일 라이터 강제 — 점유 실패(동시 라이터)면 버퍼를 건드리지 않고 즉시 fail-closed. 리더는 여전히 무잠금.
            if (Interlocked.CompareExchange(ref _writerGate, 1, 0) != 0)
            { Interlocked.Increment(ref _frozen); Freeze("시나리오: 동시 라이터 계약 위반 — fail-closed"); return false; }
            try
            {
                Interlocked.Increment(ref _version);            // → 홀수(쓰는 중)
                bool ok = false;
                try
                {
                    Thread.MemoryBarrier();
                    if (m.Kind == MutationKind.ScaleQty && m.Factor < 0m)
                        Freeze("시나리오: 음수 비중 배수 — fail-closed");   // ok=false 유지
                    else
                    {
                        // 1) 섀도우 현재가치 — 변형 매칭 슬롯만 비중 배수(구조체 스왑, δ=0). FromDecimal/Notional 오버플로는 아래 catch.
                        long factorRaw = m.Kind == MutationKind.ScaleQty ? QMath.FromDecimal(m.Factor) : QMath.Scale;
                        for (int i = 0; i < _n; i++)
                        {
                            bool hit = m.Kind == MutationKind.ScaleQty &&
                                       ((m.Symbol != null && string.Equals(_symbols[i], m.Symbol, StringComparison.Ordinal)) ||
                                        (m.Class is AssetClass c && _classes[i] == c));
                            _shadowPresent[i] = hit ? new QMoney(QMath.Notional(_livePresent[i].Raw, factorRaw, roundUp: false))
                                                    : _livePresent[i];
                        }
                        // 2) 곡선 재산출.
                        Thread.MemoryBarrier();
                        ok = FutureValueEngine.Project(_shadowPresent.AsSpan(0, _n), _assumptions.AsSpan(0, _n), _years,
                                                       _median, _opt, _pess, _att);
                    }
                }
                catch (Exception ex) when (ex is OverflowException or ArgumentOutOfRangeException or DivideByZeroException)
                { Freeze("시나리오: 정밀도 오버플로/오염 — fail-closed"); }   // ★fail-crash 아닌 fail-closed(ok=false)
                finally
                {
                    Thread.MemoryBarrier();
                    Interlocked.Increment(ref _version);        // → 짝수(항상 복원 — 리더 영구 spin-fail 차단)
                }
                if (!ok) Interlocked.Increment(ref _frozen);
                return ok;
            }
            finally { Volatile.Write(ref _writerGate, 0); }   // 라이터 점유 해제(예외 시에도)
        }

        /// <summary>무잠금 리더 — 안정 스냅샷을 호출자 버퍼로 복사. torn-read/미발행이면 false(찢긴 곡선 미노출).</summary>
        public bool TryReadFrame(Span<long> medianDst, Span<long> optimisticDst, Span<long> pessimisticDst, int maxSpins = 64)
        {
            if (medianDst.Length != _years + 1 || optimisticDst.Length != _years + 1 || pessimisticDst.Length != _years + 1)
                return false;
            for (int spin = 0; spin < maxSpins; spin++)
            {
                long v1 = Volatile.Read(ref _version);
                if (v1 == 0) return false;            // 미발행
                if ((v1 & 1L) != 0) { Thread.SpinWait(8); continue; }   // 라이터 진행 중
                _median.AsSpan(0, _years + 1).CopyTo(medianDst);
                _opt.AsSpan(0, _years + 1).CopyTo(optimisticDst);
                _pess.AsSpan(0, _years + 1).CopyTo(pessimisticDst);
                Thread.MemoryBarrier();
                long v2 = Volatile.Read(ref _version);
                if (v1 == v2) return true;            // 앞뒤 버전 동일 → 안정 스냅샷
            }
            return false;                            // 라이터 폭주로 안정 스냅샷 실패 — fail-closed(찢긴 데이터 금지)
        }

        private void Freeze(string msg) { try { _att?.ForceFreeze(msg); } catch { } }
    }
}
