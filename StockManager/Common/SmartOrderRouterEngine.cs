// ============================================================================
// SmartOrderRouterEngine — v9.0.0 초저지연 스마트 오더 라우터
//
//  v8.0.0 SIMD 알파 신호와 L2 스프레드/깊이를 실시간 스캔해, 지정가/시장가 슬라이싱 수량과 최적 라우팅 경로를
//  무할당(δ=0)으로 산출한다. 강한 알파+타이트 스프레드 → 공격적(marketable), 약한 알파/넓은 스프레드 → 수동 지정가.
//  슬라이스 수량은 최우선 반대측 깊이(+IcebergHunter 가 역추적한 숨은 물량)로 시장 충격을 최소화하고, 진위 인증기
//  (LiquidityAuthenticityVerifier)가 스푸핑 레이어 라우팅을 O(1) 차단한다.
//
//  ★불변: δ=0(ComputeRoute 핫패스 힙 0·스팬/정수·Interlocked) · 결정론(순수 산술·주입 없음) · liveArmed:false
//    (★라우팅 '경로/수치'만 산출 — IBroker·소켓·onSend·ring 참조 0. 실 발주는 하류 OrderDispatchRing→GatedOrderDispatcher
//    무장 게이트 전담) · fail-closed(불완전 호가/스푸핑/미상 → 무라우팅 또는 수동 강등). ★한국색상은 UI 계층.
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>SOR 라우팅 계획(블리터블 값타입·힙 0). 발주 '수치'만 — 실 사출은 무장 게이트 하류.</summary>
    public readonly struct RouteResult
    {
        public readonly bool Route;        // true=실행 가능 경로
        public readonly byte SideByte;     // 0 buy/1 sell
        public readonly byte KindByte;     // 0 market/1 limit
        public readonly long PriceRaw;     // 지정가(시장가=0)
        public readonly long SliceQtyRaw;  // 슬라이스당 수량
        public readonly int  Slices;       // 총 슬라이스 수
        public readonly long RoutingCode;  // 사유 비트마스크
        public RouteResult(bool route, byte side, byte kind, long priceRaw, long sliceQtyRaw, int slices, long code)
        { Route = route; SideByte = side; KindByte = kind; PriceRaw = priceRaw; SliceQtyRaw = sliceQtyRaw; Slices = slices; RoutingCode = code; }
        public static RouteResult None(long code = 0) => new(false, 0, 0, 0, 0, 0, code);
    }

    /// <summary>초저지연 스마트 오더 라우터(δ=0·결정론·liveArmed:false). ComputeRoute(alpha,L2,목표수량) → 슬라이싱·라우팅 계획.</summary>
    public sealed class SmartOrderRouterEngine
    {
        // 라우팅 코드 비트.
        public const long R_NO_ALPHA        = 1L << 0;
        public const long R_SPOOF_BLOCKED   = 1L << 1;
        public const long R_MARKET_AGGRESSIVE = 1L << 2;
        public const long R_LIMIT_PASSIVE   = 1L << 3;
        public const long R_ICEBERG_ABSORB  = 1L << 4;

        private readonly long _alphaThresholdMilli;    // 라우팅 개시 알파 강도(×1000)
        private readonly long _aggressiveMilli;        // 공격적(marketable) 알파 강도
        private readonly long _tightSpreadPermille;    // 타이트 스프레드 임계(‰ of mid)
        private readonly long _sliceFractionPermille;  // 슬라이스 = 유효깊이 × 이 비율(‰)
        private readonly IcebergHunterSentinel? _iceberg;
        private readonly LiquidityAuthenticityVerifier? _auth;

        private long _computations, _routes, _lastCode, _lastSlices;

        public SmartOrderRouterEngine(IcebergHunterSentinel? iceberg = null, LiquidityAuthenticityVerifier? auth = null,
                                      long alphaThresholdMilli = 50, long aggressiveMilli = 500,
                                      long tightSpreadPermille = 5, long sliceFractionPermille = 250)
        {
            _iceberg = iceberg; _auth = auth;
            _alphaThresholdMilli = Math.Max(1, alphaThresholdMilli);
            _aggressiveMilli = Math.Max(_alphaThresholdMilli, aggressiveMilli);
            _tightSpreadPermille = Math.Max(1, tightSpreadPermille);
            _sliceFractionPermille = Math.Clamp(sliceFractionPermille, 1, 1000);
        }

        public long Computations => Interlocked.Read(ref _computations);
        public long Routes       => Interlocked.Read(ref _routes);
        public long LastRoutingCode => Interlocked.Read(ref _lastCode);
        public long LastSlices   => Interlocked.Read(ref _lastSlices);
        public long SpoofBlocks  => _auth?.SpoofBlocks ?? 0L;
        public long HiddenIcebergTotalRaw => _iceberg?.EstimatedHiddenTotalRaw ?? 0L;

        /// <summary>
        /// SIMD 알파 + L2 호가에서 라우팅 계획 산출(δ=0). alpha 부호/강도로 방향·공격성, 반대측 깊이(+빙산)로 슬라이싱,
        /// 진위 인증으로 스푸핑 차단. 불완전 호가/무알파/스푸핑 → 무라우팅(fail-closed). ★발주 유발 0 — 계획만 반환.
        /// </summary>
        public RouteResult ComputeRoute(float alpha, ReadOnlySpan<long> bidPx, ReadOnlySpan<long> bidQty,
                                        ReadOnlySpan<long> askPx, ReadOnlySpan<long> askQty, long targetQtyRaw)
        {
            Interlocked.Increment(ref _computations);
            if (bidPx.Length < 1 || askPx.Length < 1 || bidQty.Length < 1 || askQty.Length < 1 || targetQtyRaw <= 0
                || float.IsNaN(alpha) || float.IsInfinity(alpha))
                return Fail(R_NO_ALPHA);

            long absAlphaMilli = (long)(MathF.Abs(alpha) * 1000f);
            bool isBuy  = alpha > 0 && absAlphaMilli >= _alphaThresholdMilli;
            bool isSell = alpha < 0 && absAlphaMilli >= _alphaThresholdMilli;
            if (!isBuy && !isSell) return Fail(R_NO_ALPHA);

            // ★진위 게이트 — 목표 측면 스푸핑이면 라우팅 차단(Spoofing Iceberg Trap 격리).
            if (_auth is { } av && !av.AllowRoute(isBuy)) return Fail(R_SPOOF_BLOCKED);

            long bestBid = bidPx[0], bestAsk = askPx[0];
            // fail-closed: 빈/비양수/크로스(락 초과) 오더북은 무라우팅 — 퇴화 스냅샷(프리오픈·피드 유실)을 공격적 시장가로 오판하지 않는다.
            if (bestBid <= 0 || bestAsk <= 0 || bestAsk < bestBid) return Fail(R_NO_ALPHA);
            long mid = (bestBid + bestAsk) / 2L;                                          // bestBid·bestAsk>0 보장 → mid>0(0除 불가)
            long spread = bestAsk - bestBid;                                              // ≥0 보장(크로스 위에서 차단)
            bool tight = spread == 0 || (spread * 1000L / mid) <= _tightSpreadPermille;   // spread==0=락 북=정상 타이트
            bool aggressive = absAlphaMilli >= _aggressiveMilli && tight;

            // 슬라이스 = 반대측 최우선 깊이(+빙산 숨은 물량) × sliceFraction — 시장 충격 최소화.
            long topDepth = isBuy ? askQty[0] : bidQty[0];
            long hidden = _iceberg is { } ib ? (isBuy ? ib.EstimatedHiddenAskQtyRaw : ib.EstimatedHiddenBidQtyRaw) : 0L;
            long effDepth = topDepth + (hidden > 0 ? hidden : 0);
            long slice = effDepth > 0 ? effDepth * _sliceFractionPermille / 1000L : targetQtyRaw;
            if (slice <= 0) slice = targetQtyRaw;
            if (slice > targetQtyRaw) slice = targetQtyRaw;
            // ceil(target/slice) = 1 + (target-1)/slice — target≥1·slice≥1 보장이라 덧셈 오버플로 불가. int 범위로 포화(fail-safe).
            long slicesL = slice > 0 ? 1L + (targetQtyRaw - 1L) / slice : 1L;
            int slices = slicesL > int.MaxValue ? int.MaxValue : (int)slicesL;

            byte kind = aggressive ? (byte)0 : (byte)1;                     // market : limit
            long price = aggressive ? 0L : (isBuy ? bestBid : bestAsk);     // 수동 지정가는 자기 최우선(대기)·시장가=0
            long code = (aggressive ? R_MARKET_AGGRESSIVE : R_LIMIT_PASSIVE) | (hidden > 0 ? R_ICEBERG_ABSORB : 0L);

            Interlocked.Increment(ref _routes);
            Volatile.Write(ref _lastCode, code);
            Volatile.Write(ref _lastSlices, slices);
            return new RouteResult(true, (byte)(isBuy ? 0 : 1), kind, price, slice, slices, code);
        }

        private RouteResult Fail(long code) { Volatile.Write(ref _lastCode, code); return RouteResult.None(code); }
    }
}
