// ============================================================================
// SimdRiskInterlock — v9.0.0 Phase 9 Task 1: SIMD 나노-리스크 차단기
//
//  L3 오더북 매수/매도 10호가(가격·잔량 수열)를 SIMD 벡터 레지스터에 로드해, 단일 벡터 비교로 이상 변동
//  (스푸핑·Fat-Finger·교차/광폭 스프레드·사다리 붕괴)을 <5ns 대역에서 병렬 차단한다.
//    · AVX-512(Vector512<long>) 가용 시 8레인/명령, 아니면 AVX2(Vector256<long>) 4레인/명령, 그마저 없으면 스칼라.
//    · 검증 실패 시 '하드웨어 퓨즈'(비가역 래치)를 구동 → 발주/링 발행 회로를 즉시·영구 차단(Veto). 옵션으로
//      OrderAttestationService.ForceFreeze 이중 봉인(자동복구 없음·재시작 전까지 fail-closed).
//
//  ★불변 제약: δ=0 핫패스(스팬 LoadUnsafe·값타입 반환·Interlocked — new/box/람다캡처/LINQ 0). 결정론(순수 비교).
//    liveArmed:false(순수 관측 — Veto 신호만 산출, 아웃바운드 미접촉). 기존 불변 회로 무접촉(신규 파일).
//    ★한국색상은 UI 계층. 여기선 판정·계수만.
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>SIMD 리스크 이상 사유(값이 곧 포렌식 코드). </summary>
    public enum RiskAnomaly
    {
        None = 0, MalformedBook, NonPositivePrice, CrossedOrTightSpread, WideSpread,
        FatFingerQty, TotalQtySpoof, PriceOutOfBounds, LadderDisorder, LatchedVeto
    }

    /// <summary>SIMD 리스크 판정(블리터블 값타입·힙 0).</summary>
    public readonly struct SimdRiskVerdict
    {
        public readonly bool        Ok;
        public readonly RiskAnomaly Reason;
        public readonly long        Offending;   // 위반 값(포렌식)
        private SimdRiskVerdict(bool ok, RiskAnomaly reason, long offending) { Ok = ok; Reason = reason; Offending = offending; }
        public static SimdRiskVerdict Pass()                          => new(true,  RiskAnomaly.None, 0);
        public static SimdRiskVerdict Veto(RiskAnomaly r, long value)  => new(false, r, value);
    }

    /// <summary>리스크 임계치(값타입). 틱/원시 정수 도메인.</summary>
    public readonly struct SimdRiskLimits
    {
        public readonly long MaxPrice;        // 호가 상한(초과=이상)
        public readonly long MaxQtyPerLevel;  // 호가별 잔량 상한(Fat-Finger)
        public readonly long MaxTotalQty;     // 편측 총잔량 상한(스푸핑 대량)
        public readonly long MinSpreadTicks;  // 스프레드 하한(음수=교차 차단). 0=locked 허용·교차 거부
        public readonly long MaxSpreadTicks;  // 스프레드 상한(광폭=이상)
        public SimdRiskLimits(long maxPrice, long maxQtyPerLevel, long maxTotalQty, long minSpreadTicks, long maxSpreadTicks)
        { MaxPrice = maxPrice; MaxQtyPerLevel = maxQtyPerLevel; MaxTotalQty = maxTotalQty; MinSpreadTicks = minSpreadTicks; MaxSpreadTicks = maxSpreadTicks; }
        public static SimdRiskLimits Default => new(100_000_000L, 1_000_000L, 5_000_000L, 0L, 10_000L);
    }

    /// <summary>SIMD 10호가 리스크 차단기(비가역 Veto 래치). ValidateCore 는 순수 정적(δ=0·재사용).</summary>
    public sealed class SimdRiskInterlock
    {
        private readonly OrderAttestationService? _attestation;   // 옵션 이중 봉인(fail-closed)
        private int  _vetoed;        // 0/1 — 1회 래치(비가역 하드웨어 퓨즈)
        private long _checks, _vetoes;
        private int  _lastReason;

        public SimdRiskInterlock(OrderAttestationService? attestation = null) { _attestation = attestation; }

        public bool IsVetoed              => Volatile.Read(ref _vetoed) == 1;
        public bool IsOrderEmissionAllowed => Volatile.Read(ref _vetoed) == 0;   // ★발주/링 발행 게이트
        public long Checks                => Interlocked.Read(ref _checks);
        public long Vetoes                => Interlocked.Read(ref _vetoes);
        public RiskAnomaly LastReason     => (RiskAnomaly)Volatile.Read(ref _lastReason);

        /// <summary>
        /// 10호가를 SIMD 검증하고, 실패 시 하드웨어 퓨즈를 비가역 래치(발주 차단). 이미 래치면 즉시 LatchedVeto(0 재연산).
        /// </summary>
        public SimdRiskVerdict Validate(ReadOnlySpan<long> bidPx, ReadOnlySpan<long> bidQty,
                                        ReadOnlySpan<long> askPx, ReadOnlySpan<long> askQty,
                                        in SimdRiskLimits limits)
        {
            if (Volatile.Read(ref _vetoed) == 1) return SimdRiskVerdict.Veto(RiskAnomaly.LatchedVeto, 0);   // fail-closed 즉시
            SimdRiskVerdict v = ValidateCore(bidPx, bidQty, askPx, askQty, limits);
            Interlocked.Increment(ref _checks);
            if (!v.Ok && Interlocked.CompareExchange(ref _vetoed, 1, 0) == 0)   // 1회만 래치(비가역)
            {
                Interlocked.Increment(ref _vetoes);
                Volatile.Write(ref _lastReason, (int)v.Reason);
                try { _attestation?.ForceFreeze($"SIMD 리스크 Veto — {v.Reason}(값={v.Offending}). 발주 회로 비가역 차단."); } catch { }
            }
            return v;
        }

        /// <summary>순수 SIMD 검증(래치 없음·δ=0). Task 3(추측 사전검증)이 재사용한다.</summary>
        public static SimdRiskVerdict ValidateCore(ReadOnlySpan<long> bidPx, ReadOnlySpan<long> bidQty,
                                                   ReadOnlySpan<long> askPx, ReadOnlySpan<long> askQty,
                                                   in SimdRiskLimits limits)
        {
            int n = bidPx.Length;
            if (n == 0 || bidQty.Length != n || askPx.Length != n || askQty.Length != n)
                return SimdRiskVerdict.Veto(RiskAnomaly.MalformedBook, n);

            long bestBid = bidPx[0], bestAsk = askPx[0];
            if (bestBid <= 0 || bestAsk <= 0)
                return SimdRiskVerdict.Veto(RiskAnomaly.NonPositivePrice, bestBid <= 0 ? bestBid : bestAsk);
            long spread = bestAsk - bestBid;
            if (spread < limits.MinSpreadTicks) return SimdRiskVerdict.Veto(RiskAnomaly.CrossedOrTightSpread, spread);  // 교차/음수
            if (spread > limits.MaxSpreadTicks) return SimdRiskVerdict.Veto(RiskAnomaly.WideSpread, spread);            // 광폭(스푸핑)

            // ── SIMD: 잔량 Fat-Finger(음수·상한 초과) + 편측 총잔량 스푸핑(수평 합) ──
            if (ScanOutOfRange(bidQty, 0L, limits.MaxQtyPerLevel, out long bidTot))
                return SimdRiskVerdict.Veto(RiskAnomaly.FatFingerQty, limits.MaxQtyPerLevel);
            if (ScanOutOfRange(askQty, 0L, limits.MaxQtyPerLevel, out long askTot))
                return SimdRiskVerdict.Veto(RiskAnomaly.FatFingerQty, limits.MaxQtyPerLevel);
            if (bidTot > limits.MaxTotalQty || askTot > limits.MaxTotalQty)
                return SimdRiskVerdict.Veto(RiskAnomaly.TotalQtySpoof, bidTot > askTot ? bidTot : askTot);

            // ── SIMD: 가격 범위(음수 또는 상한 초과). 하한 0 — 빈 레벨(L3 Delete·10호가 미만)은 price=0 으로 정상 표현되며
            //   top-of-book 양수성은 위 NonPositivePrice(레벨0)에서 이미 보장하므로 심레벨 0 은 이상치가 아니다(정상 이벤트 과차단 방지). ──
            if (ScanOutOfRange(bidPx, 0L, limits.MaxPrice, out _) || ScanOutOfRange(askPx, 0L, limits.MaxPrice, out _))
                return SimdRiskVerdict.Veto(RiskAnomaly.PriceOutOfBounds, limits.MaxPrice);

            // ── 사다리 단조성(매수 비증가·매도 비감소) — 순차 의존이라 스칼라(무시할 비용).
            //   빈 레벨(price=0)은 건너뛰고 '마지막 비어있지 않은 레벨'과만 비교한다(후행 0·중간 갭에서 LadderDisorder 오탐 방지). ──
            long prevBid = bestBid, prevAsk = bestAsk;   // 레벨0 은 위에서 양수 보장
            for (int i = 1; i < n; i++)
            {
                long b = bidPx[i], a = askPx[i];
                if (b != 0L) { if (b > prevBid) return SimdRiskVerdict.Veto(RiskAnomaly.LadderDisorder, b); prevBid = b; }
                if (a != 0L) { if (a < prevAsk) return SimdRiskVerdict.Veto(RiskAnomaly.LadderDisorder, a); prevAsk = a; }
            }
            return SimdRiskVerdict.Pass();
        }

        // 어느 레인이든 [lo,hi] 벗어나면 true + 수평 합. AVX-512 → AVX2 → 스칼라 3단(δ=0).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ScanOutOfRange(ReadOnlySpan<long> data, long lo, long hi, out long total)
        {
            int n = data.Length, i = 0;
            bool anomaly = false;
            long sum = 0;
            ref long p = ref MemoryMarshal.GetReference(data);

            if (Vector512.IsHardwareAccelerated && n >= Vector512<long>.Count)
            {
                var loV = Vector512.Create(lo);
                var hiV = Vector512.Create(hi);
                var flag = Vector512<long>.Zero;
                var acc  = Vector512<long>.Zero;
                for (; i + Vector512<long>.Count <= n; i += Vector512<long>.Count)
                {
                    var v = Vector512.LoadUnsafe(ref p, (nuint)i);
                    flag |= Vector512.LessThan(v, loV) | Vector512.GreaterThan(v, hiV);   // ★단일 SIMD 비교(레인 병렬)
                    acc  += v;
                }
                if (flag != Vector512<long>.Zero) anomaly = true;
                sum += Vector512.Sum(acc);
            }
            else if (Vector256.IsHardwareAccelerated && n >= Vector256<long>.Count)
            {
                var loV = Vector256.Create(lo);
                var hiV = Vector256.Create(hi);
                var flag = Vector256<long>.Zero;
                var acc  = Vector256<long>.Zero;
                for (; i + Vector256<long>.Count <= n; i += Vector256<long>.Count)
                {
                    var v = Vector256.LoadUnsafe(ref p, (nuint)i);
                    flag |= Vector256.LessThan(v, loV) | Vector256.GreaterThan(v, hiV);   // ★단일 SIMD 비교(레인 병렬)
                    acc  += v;
                }
                if (flag != Vector256<long>.Zero) anomaly = true;
                sum += Vector256.Sum(acc);
            }
            for (; i < n; i++)   // 스칼라 잔여(10 % width) — 완전 이식 폴백 겸용
            {
                long v = Unsafe.Add(ref p, i);
                if (v < lo || v > hi) anomaly = true;
                sum += v;
            }
            total = sum;
            return anomaly;
        }
    }
}
