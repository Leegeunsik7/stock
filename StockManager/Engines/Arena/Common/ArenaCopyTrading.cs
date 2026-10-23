// ============================================================================
// ArenaCopyTrading — v12.18.71-DEV: 플루토 카피 트레이딩 공용 산식 · 복제 원장 계수(3대 아레나 엔진 공용 · δ=0)
//
//   ① 비례 수량: 휴먼 수량 = AI 수량 × (휴먼 평가자산 ÷ AI 평가자산) × 배율(bp) — Int128 · 내림 · 0 이하 = 0(복제 없음).
//   ② 슬리피지 보호: 기준가(AI 체결가) 대비 '불리한 방향' 체결가 괴리 > 20bp = 복제 취소(올림 판정 · 기준가/체결가 무효 = 초과 취급 fail-closed).
//   ③ 동반 청산: AI 청산 = 휴먼 복제 보유분(CopiedQty)을 AI 청산 비율만큼 매도(전량 청산 = 복제분 전량 · 휴먼 자기 매수분은 건드리지 않음).
//   ④ 예수금 부족 = Fail-Closed(수량 축소 · 부분 복제 없음 — 엔진 Buy 의 InsufficientCash 그대로).
//   ★δ=0: 전 경로 값 타입 · 정수 산술 · 할당 0. ★liveArmed:false — 복제 대상 = 휴먼 '가상' 원장뿐(외부 송출 수단 0).
// ============================================================================
using System;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Engines.Arena.Common
{
    public static class ArenaCopyMath
    {
        public const int MaxSlippageBp = 20;
        public const int DefaultRatioBp = 10_000, MinRatioBp = 1_000, MaxRatioBp = 30_000;   // 0.1x ~ 3.0x

        public static bool ValidRatio(int ratioBp) => ratioBp is >= MinRatioBp and <= MaxRatioBp;

        /// <summary>배율(decimal · 포트 경계 콜드) → bp. 범위 밖 · 0.0001 미만 단위 = false.</summary>
        public static bool TryRatioBp(decimal ratio, out int ratioBp)
        {
            ratioBp = 0;
            decimal bp = ratio * 10_000m;
            if (bp != decimal.Truncate(bp) || bp < MinRatioBp || bp > MaxRatioBp) return false;
            ratioBp = (int)bp;
            return true;
        }

        /// <summary>진영 자본 비례 복제 수량(Int128 · 내림 · 입력 무효 = 0).</summary>
        public static long ScaleQty(long aiQty, long aiEquity, long humanEquity, int ratioBp)
        {
            if (aiQty <= 0 || aiEquity <= 0 || humanEquity <= 0 || !ValidRatio(ratioBp)) return 0;
            Int128 num = (Int128)aiQty * humanEquity;                                  // ≤ 2^126 — 배율 곱 전 오버플로 경계 판정
            Int128 q;
            if (num <= Int128.MaxValue / ratioBp) q = num * ratioBp / ((Int128)aiEquity * 10_000);
            else
            {
                Int128 t = num / aiEquity;
                if (t > long.MaxValue) return long.MaxValue;
                q = t * ratioBp / 10_000;
            }
            return q > long.MaxValue ? long.MaxValue : (long)q;
        }

        /// <summary>불리한 방향 슬리피지(bp · 올림): 매수 = 체결가 &gt; 기준가 · 매도 = 체결가 &lt; 기준가 · 유리/동일 ≤ 0 · 무효 = long.MaxValue.</summary>
        public static long AdverseSlippageBp(int side, long refPx, long fillPx)
        {
            if (refPx <= 0 || fillPx <= 0 || (side != 1 && side != -1)) return long.MaxValue;
            Int128 d = side > 0 ? (Int128)fillPx - refPx : (Int128)refPx - fillPx;
            Int128 n = d * 10_000;
            Int128 bp = n / refPx;
            if (n > 0 && bp * refPx != n) bp++;
            return bp > long.MaxValue ? long.MaxValue : bp < long.MinValue ? long.MinValue : (long)bp;
        }

        public static bool SlippageExceeded(int side, long refPx, long fillPx) => AdverseSlippageBp(side, refPx, fillPx) > MaxSlippageBp;

        /// <summary>동반 청산 수량: 복제 보유분(휴먼 보유 상한) × AI 매도 수량 ÷ AI 청산 전 보유(전량 = 복제분 전량 · 입력 무효 = 0).</summary>
        public static long CompanionQty(long copiedQty, long humanQty, long aiSold, long aiHeldBefore)
        {
            if (copiedQty <= 0 || humanQty <= 0 || aiSold <= 0 || aiHeldBefore <= 0) return 0;
            long c = Math.Min(copiedQty, humanQty);
            return aiSold >= aiHeldBefore ? c : (long)((Int128)c * aiSold / aiHeldBefore);
        }
    }

    /// <summary>복제 1건 종류(계수 분류).</summary>
    public enum ArenaCopyKind : byte { Follow = 0, Mirror = 1, Companion = 2 }

    /// <summary>
    /// 복제 원장 계수(정산 스레드 단일 작성자 · seqlock 게시). 성공 = 종류별 계수 · 실패 = 사유별 계수 · 마지막 결과 1건(UI 토스트).
    /// </summary>
    public struct ArenaCopyLedger
    {
        public long Mirrored, Followed, CompanionCloses, SlippageCancels, CashRejects, OtherRejects;
        public int LastCode, LastSide;
        public ArenaCopyKind LastKind;
        public long LastSymbol, LastQty, LastEvent;

        public void Record(ArenaCopyKind kind, int code, int side, long symbol, long qty)
        {
            if (code == ArenaCopyCode.None)
            {
                if (kind == ArenaCopyKind.Mirror) Mirrored++;
                else if (kind == ArenaCopyKind.Companion) CompanionCloses++;
                else Followed++;
            }
            else if (code == ArenaCopyCode.SlippageExceeded) SlippageCancels++;
            else if (code == ArenaCopyCode.InsufficientCash) CashRejects++;
            else OtherRejects++;
            LastKind = kind; LastCode = code; LastSide = side; LastSymbol = symbol; LastQty = code == ArenaCopyCode.None ? qty : 0;
            LastEvent++;
        }
    }
}
