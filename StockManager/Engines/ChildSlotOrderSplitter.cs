// ============================================================================
// ChildSlotOrderSplitter — [자식 슬롯 분할 라우팅 · 추천 A] 부모 주문 1건 → 브로커별 자식 주문 N건(≤ 64) 무할당 분할(δ=0)
//
//  ★전제(정직): 키움과 KIS 는 같은 KRX 호가창으로 주문을 넣는다 — '브로커별 호가 잔량'은 존재하지 않는다(잔량은 시장의 속성).
//    따라서 분할의 두 축은 ① 시장 깊이(1~3호가 중 지정가로 즉시 체결 가능한 잔량 — 과대 발주 방지) ② 브로커별 한도(신용·명목·레이트 캡)다.
//
//  알고리즘(정수 · 결정론):
//   1) 체결 가능 단계: 매수 = 매도호가 ≤ 지정가인 1~3호가 · 매도 = 매수호가 ≥ 지정가인 1~3호가. 단계 잔량(raw 10^8 → 주) 합 = 깊이 상한.
//      깊이 미사용(hasDepth=false)이면 단일 단계(Level −1 · 부모 지정가 · 부모 수량).
//   2) 총 배분 = min(부모 수량, 깊이 상한, Σ 가용 브로커 한도) — ★No Over-Dispatch: 자식 합 ≤ 부모 수량(단 1주도 초과 불가).
//   3) 브로커 할당 = 한도 비례 최대잉여법(정수 · 각 ≤ 한도). 단계별로 최우선호가부터 단계 잔량을 브로커 잔여 할당에 비례 분배.
//   4) 자식 지정가 = 그 단계 호가(매수는 ≤ 부모 지정가 · 매도는 ≥ 부모 지정가 — 부모보다 불리하지 않음). 깊이 미사용 = 부모 지정가.
//   5) 자식 ID = ChildIdSpace.Compose(부모, 순번) · 128비트 추적 ID = (부모, 자식). 자식 수 &gt; 버퍼/64 = TooManyChildren(부분 산출 0).
//  δ=0: 입력/출력 모두 호출측 스팬 · 단계 버퍼는 stackalloc 상수 크기.
// ============================================================================
using System;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Engines
{
    public enum SplitVerdict : byte { Ok = 0, InvalidParent = 1, NoBroker = 2, NothingFillable = 3, TooManyChildren = 4 }

    public static class ChildSlotOrderSplitter
    {
        public const int MaxLevels = 3;
        private const long QtyScale = 100_000_000L;
        private const int MaxBrokers = 16;

        /// <summary>
        /// 분할(δ=0). count = 산출 자식 수 · total = 자식 주수 합(≤ parentShares). Ok 이외 판정은 count=0(부분 산출 없음).
        /// </summary>
        public static SplitVerdict Split(long parentId, OrderSide side, long parentShares, long parentLimitRaw,
                                         in L2Depth depth, bool hasDepth, ReadOnlySpan<BrokerSplitCap> caps,
                                         Span<ChildOrderPlan> dst, out int count, out long total, int maxLevels = MaxLevels)
        {
            count = 0; total = 0;
            if (parentShares <= 0 || parentLimitRaw <= 0 || ChildIdSpace.Compose(parentId, 0) == 0) return SplitVerdict.InvalidParent;
            if (caps.Length == 0 || caps.Length > MaxBrokers) return SplitVerdict.NoBroker;
            maxLevels = Math.Clamp(maxLevels, 1, MaxLevels);

            // ① 체결 가능 단계
            Span<long> lvPx = stackalloc long[MaxLevels], lvShares = stackalloc long[MaxLevels];
            int levels = 0; long depthTotal = 0;
            if (hasDepth)
            {
                for (int i = 0; i < maxLevels; i++)
                {
                    long px = side == OrderSide.Buy ? depth.AskPriceRaw[i] : depth.BidPriceRaw[i];
                    long q = side == OrderSide.Buy ? depth.AskQtyRaw[i] : depth.BidQtyRaw[i];
                    if (px <= 0 || q <= 0) break;                                   // 사다리 끝/공백 — 이후 단계 무시(연속성)
                    if (side == OrderSide.Buy ? px > parentLimitRaw : px < parentLimitRaw) break;   // 지정가 밖 = 즉시 체결 불가
                    long sh = q / QtyScale;
                    if (sh <= 0) continue;
                    lvPx[levels] = px; lvShares[levels] = sh; levels++;
                    depthTotal = SatAdd(depthTotal, sh);
                }
                if (levels == 0) return SplitVerdict.NothingFillable;
            }
            else { lvPx[0] = parentLimitRaw; lvShares[0] = parentShares; levels = 1; depthTotal = parentShares; }

            // ② 브로커 한도 합 · 총 배분
            Span<long> quota = stackalloc long[MaxBrokers];
            long capSum = 0; int live = 0;
            for (int b = 0; b < caps.Length; b++)
                if (caps[b].Available && caps[b].MaxShares > 0 && caps[b].Broker >= 0) { capSum = SatAdd(capSum, caps[b].MaxShares); live++; }
            if (live == 0) return SplitVerdict.NoBroker;
            long alloc = Math.Min(parentShares, Math.Min(depthTotal, capSum));
            if (alloc <= 0) return SplitVerdict.NothingFillable;

            // ③ 브로커 할당(한도 비례 최대잉여법 · 각 ≤ 한도 · 합 = alloc)
            Span<long> rem = stackalloc long[MaxBrokers];
            long given = 0;
            for (int b = 0; b < caps.Length; b++)
            {
                if (!(caps[b].Available && caps[b].MaxShares > 0 && caps[b].Broker >= 0)) { quota[b] = 0; rem[b] = -1; continue; }
                Int128 num = (Int128)alloc * caps[b].MaxShares;
                quota[b] = (long)(num / capSum);
                rem[b] = (long)(num % capSum);
                if (quota[b] > caps[b].MaxShares) quota[b] = caps[b].MaxShares;
                given += quota[b];
            }
            // 버림 결손(< 가용 브로커 수)은 잔여 큰 순으로 1주씩 — 잔여 > 0 인 브로커는 floor < 정확값 < 한도 이므로 +1 해도 한도 이내
            for (long deficit = alloc - given; deficit > 0; deficit--)
            {
                int best = -1;
                for (int b = 0; b < caps.Length; b++) if (rem[b] > 0 && (best < 0 || rem[b] > rem[best])) best = b;
                if (best < 0) break;
                quota[best]++; rem[best] = 0; given++;
            }

            // ④ 단계 × 브로커 자식 산출(최우선호가부터 · 단계 잔량을 브로커 잔여 할당에 비례 · 각 ≤ 단계 잔량)
            int n = 0;
            for (int l = 0; l < levels && given > 0; l++)
            {
                long levelLeft = Math.Min(lvShares[l], given);
                long quotaLeftSum = 0;
                for (int b = 0; b < caps.Length; b++) quotaLeftSum += quota[b];
                if (quotaLeftSum <= 0) break;
                long levelTake = Math.Min(levelLeft, quotaLeftSum);
                long assigned = 0;
                for (int b = 0; b < caps.Length; b++)
                {
                    if (quota[b] <= 0) continue;
                    long share = (long)((Int128)levelTake * quota[b] / quotaLeftSum);
                    if (share > quota[b]) share = quota[b];
                    if (share <= 0) continue;
                    if (!Emit(dst, ref n, parentId, caps[b].Broker, l, hasDepth, share, lvPx[l])) { count = 0; total = 0; return SplitVerdict.TooManyChildren; }
                    quota[b] -= share; assigned += share;
                }
                // 잔여(정수 버림분)는 할당 남은 브로커 순으로 1주씩 — 단계 잔량을 넘지 않음
                for (int b = 0; b < caps.Length && assigned < levelTake; b++)
                {
                    if (quota[b] <= 0) continue;
                    long extra = Math.Min(quota[b], levelTake - assigned);
                    if (!AddOrEmit(dst, ref n, parentId, caps[b].Broker, l, hasDepth, extra, lvPx[l])) { count = 0; total = 0; return SplitVerdict.TooManyChildren; }
                    quota[b] -= extra; assigned += extra;
                }
                given -= assigned;
            }

            long sum = 0;
            for (int i = 0; i < n; i++) sum += dst[i].Shares;
            if (sum <= 0) { count = 0; return SplitVerdict.NothingFillable; }
            if (sum > parentShares) { count = 0; return SplitVerdict.InvalidParent; }   // ★불변식 방어(도달 불가 — 테스트가 퍼징으로 증명)
            count = n; total = sum;
            return SplitVerdict.Ok;
        }

        private static bool Emit(Span<ChildOrderPlan> dst, ref int n, long parentId, int broker, int level, bool hasDepth, long shares, long px)
        {
            if (n >= dst.Length || n >= ChildIdSpace.MaxChildren) return false;
            dst[n] = new ChildOrderPlan
            {
                ChildId = ChildIdSpace.Compose(parentId, n), ParentId = parentId, Shares = shares, LimitRaw = px,
                Broker = broker, Level = hasDepth ? level : -1, Index = n,
            };
            n++;
            return true;
        }

        // 같은 (브로커, 단계) 자식이 이미 있으면 수량 가산 — 자식 수 최소화
        private static bool AddOrEmit(Span<ChildOrderPlan> dst, ref int n, long parentId, int broker, int level, bool hasDepth, long shares, long px)
        {
            int lv = hasDepth ? level : -1;
            for (int i = 0; i < n; i++)
                if (dst[i].Broker == broker && dst[i].Level == lv) { dst[i].Shares += shares; return true; }
            return Emit(dst, ref n, parentId, broker, level, hasDepth, shares, px);
        }

        private static long SatAdd(long a, long b) { long r = a + b; return ((a ^ r) & (b ^ r)) < 0 ? long.MaxValue : r; }
    }
}
