// ============================================================================
// MarketOpenAuctionGate — v12.18.71-DEV: 정규장 개장 순간 시초가 갭 · 단일가 매칭 게이트(국내 09:00 · 미국 09:30 ET = 22:30/23:30 KST · δ=0)
//
//   ① 갭: 종목별 '정규장 첫 유효 호가' = 개장 → 시초 중간가 vs 전일 정규장 마지막 중간가(일자 변경 시 RollDay 로 승계) = GapBp(±10000 클램프).
//     전일 종가 없음(첫 거래일 · 원장 초기화 직후) = 갭 판정 없음(None · 가짜 갭 0).
//   ② 갭 전략(개장 후 OpeningWindowTicks 틱 동안만 · 플루토 거버너(악마의 변호인) 가동 시에만 — 정지 = 기존 동작 동일):
//     · 갭 상승 ≥ +300bp = 추격 금지(ChaseVeto · 악마의 변호인: 시초가 과열 되돌림 위험) → AI 신규 진입 차단.
//     · 갭 하락 ≤ −500bp = 패닉 갭(PanicVeto) → AI 신규 진입 차단(떨어지는 칼날).
//     · −500bp &lt; 갭 ≤ −300bp ∧ 호가 불균형 ≥ +2000bp = 갭 메우기 매수 후보(GapFillLong → OpeningGapFill 진입 근거).
//   ③ 단일가 매칭: 동시호가 접수 창(국내 08:30~09:00 · 미국 09:00~09:30 ET)의 휴먼 주문 = 엔진 고정 경매 장부(ArenaAuctionOrder)에 적재 →
//     해당 종목 개장 틱에 '단일 시초가'(호가단위 정렬 중간가)로 일괄 체결(매수/매도 동일 가격 · 잔량 상한). 장부 포화 = QueueFull 거부(재시도 0).
//   ★δ=0: 값 타입 · 정수 산술 · 할당 0. ★liveArmed:false — 가상 원장 체결 규칙일 뿐 외부 송출 0.
// ============================================================================
using System;

namespace StockManager.Engines.Arena.Common
{
    public enum OpenGapAction : byte { None = 0, Flat = 1, ChaseVeto = 2, GapFillLong = 3, PanicVeto = 4 }

    /// <summary>종목 1개 개장 갭 상태(엔진 종목 표 슬롯에 내장 · 정산 스레드 단일 작성자).</summary>
    public struct OpenGapState
    {
        public long PrevCloseX2, LastRegularMidX2, OpenX2;
        public int GapBp, OpenTicks;
        public OpenGapAction Action;
        public bool Opened;
    }

    /// <summary>동시호가 접수 창 휴먼 주문 1건(Flags 1 = 카피 · RefPx = 슬리피지 기준가 · 0 = 검사 없음).</summary>
    public struct ArenaAuctionOrder
    {
        public long Symbol, Qty, RefPx;
        public int Side, Flags;
    }

    /// <summary>개장 게이트 계수 게시본.</summary>
    public readonly struct ArenaAuctionStats
    {
        public readonly bool PreOpen;
        public readonly int Parked;
        public readonly long Fills, Rejects, GapVetoes, GapFillEntries;

        public ArenaAuctionStats(bool preOpen, int parked, long fills, long rejects, long gapVetoes, long gapFillEntries)
        {
            PreOpen = preOpen; Parked = parked; Fills = fills; Rejects = rejects; GapVetoes = gapVetoes; GapFillEntries = gapFillEntries;
        }
    }

    public static class MarketOpenAuctionGate
    {
        public const int GapThresholdBp = 300, PanicGapBp = 500, GapFillMinImbalanceBp = 2000, OpeningWindowTicks = 5;
        public const int AuctionCapacity = 32;
        public const int FlagCopy = 1;
        public static readonly TimeSpan KrxAuctionStart = new(8, 30, 0);
        public static readonly long UsAuctionStartEtTicks = new TimeSpan(9, 0, 0).Ticks;

        public static OpenGapAction Assess(int gapBp)
            => gapBp >= GapThresholdBp ? OpenGapAction.ChaseVeto
             : gapBp <= -PanicGapBp ? OpenGapAction.PanicVeto
             : gapBp <= -GapThresholdBp ? OpenGapAction.GapFillLong
             : OpenGapAction.Flat;

        /// <summary>국내 동시호가 접수 창(평일 08:30 ≤ KST &lt; 09:00).</summary>
        public static bool IsKrxPreOpen(DateTime kst)
        {
            if (kst.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
            var t = kst.TimeOfDay;
            return t >= KrxAuctionStart && t < new TimeSpan(9, 0, 0);
        }

        /// <summary>일자 변경: 직전 정규장 마지막 중간가 → 전일 종가 · 개장 상태 초기화.</summary>
        public static void RollDay(ref OpenGapState g)
        {
            if (g.LastRegularMidX2 > 0) g.PrevCloseX2 = g.LastRegularMidX2;
            g.Opened = false; g.OpenTicks = 0; g.GapBp = 0; g.OpenX2 = 0; g.Action = OpenGapAction.None;
        }

        /// <summary>정규장 유효 호가 1틱(중간가 ×2). 그날 첫 틱 = 개장(갭 산출 · true).</summary>
        public static bool OnRegularTick(ref OpenGapState g, long midX2)
        {
            g.LastRegularMidX2 = midX2;
            if (g.Opened)
            {
                if (g.OpenTicks < int.MaxValue) g.OpenTicks++;
                return false;
            }
            g.Opened = true; g.OpenTicks = 1; g.OpenX2 = midX2;
            if (g.PrevCloseX2 > 0)
            {
                g.GapBp = (int)Math.Clamp((midX2 - g.PrevCloseX2) * 10000 / g.PrevCloseX2, -10000, 10000);
                g.Action = Assess(g.GapBp);
            }
            else { g.GapBp = 0; g.Action = OpenGapAction.None; }
            return true;
        }

        public static bool InOpeningWindow(in OpenGapState g) => g.Opened && g.OpenTicks <= OpeningWindowTicks;

        /// <summary>개장 창 갭 거부권(추격 금지 · 패닉 갭) — AI 신규 진입 차단.</summary>
        public static bool VetoesEntry(in OpenGapState g) => InOpeningWindow(in g) && g.Action is OpenGapAction.ChaseVeto or OpenGapAction.PanicVeto;

        /// <summary>갭 메우기 매수 후보(개장 창 · −500 &lt; 갭 ≤ −300 · 호가 불균형 ≥ +2000).</summary>
        public static bool GapFillSignal(in OpenGapState g, int imbalanceBp)
            => InOpeningWindow(in g) && g.Action == OpenGapAction.GapFillLong && imbalanceBp >= GapFillMinImbalanceBp;

    }
}
