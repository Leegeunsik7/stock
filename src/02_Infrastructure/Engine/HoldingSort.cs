// ============================================================================
// HoldingSort — v4.89.0: 보유 목록 다축 정렬(순수·결정론·테스트 대상)
//
//  전체/국내/해외/코인 모든 분류에 공통 적용되는 정렬 코어. UI(PortfolioPage)는 필터 후 이 함수만 호출한다.
//   · val    평가금(현재가×수량, USD는 환율 환산 KRW)      · 높은순/낮은순
//   · ret    총수익률((현재가−평단)/평단 %)                · 높은순/낮은순
//   · day    일간 수익률(전일대비 등락% — 시세에서 포착)    · 높은순/낮은순
//   · name   가나다순(한국어 문화권, 불가 시 Ordinal 폴백)  · 오름/역순
//   · custom 직접설정(사용자 지정 순서·미지정은 뒤로 가나다) · 방향 미적용
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MS2E.Infrastructure
{
    public static class HoldingSort
    {
        // 가나다순 비교자 — ko-KR(불가 시 Ordinal). 한 번만 생성(결정론).
        public static readonly IComparer<string> NameComparer = MakeNameComparer();
        private static IComparer<string> MakeNameComparer()
        {
            try { return StringComparer.Create(CultureInfo.GetCultureInfo("ko-KR"), ignoreCase: true); }
            catch { return StringComparer.Ordinal; }
        }

        private static string NameKey(Holding h) => string.IsNullOrEmpty(h.Name) ? h.Symbol : h.Name;

        /// <summary>필터된 보유 목록을 정렬. dayPct/customOrder 는 선택(없으면 0·미지정 취급). usdKrw 로 USD 평가금 환산.</summary>
        public static IEnumerable<Holding> Sort(
            IEnumerable<Holding> src, string key, bool desc,
            IReadOnlyDictionary<string, decimal>? dayPct = null,
            IReadOnlyList<string>? customOrder = null,
            decimal usdKrw = 1m)
        {
            decimal Val(Holding h) => h.CurrentPrice * h.Quantity * (h.Currency == Currency.USD ? usdKrw : 1m);
            decimal Ret(Holding h) => h.AvgPrice > 0 ? (h.CurrentPrice - h.AvgPrice) / h.AvgPrice * 100m : 0m;
            decimal Day(Holding h) => dayPct != null && dayPct.TryGetValue(h.Symbol, out var d) ? d : 0m;
            var order = customOrder ?? (IReadOnlyList<string>)Array.Empty<string>();

            return key switch
            {
                "name"   => desc ? src.OrderByDescending(NameKey, NameComparer) : src.OrderBy(NameKey, NameComparer),
                "custom" => src.OrderBy(h => { int i = IndexOf(order, h.Symbol); return i < 0 ? int.MaxValue : i; })
                               .ThenBy(NameKey, NameComparer),
                "ret"    => desc ? src.OrderByDescending(Ret) : src.OrderBy(Ret),
                "day"    => desc ? src.OrderByDescending(Day) : src.OrderBy(Day),
                _        => desc ? src.OrderByDescending(Val) : src.OrderBy(Val),   // "val"(기본)
            };
        }

        private static int IndexOf(IReadOnlyList<string> list, string s)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], s, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>
        /// 직접설정 재정렬 — '표시 순서'(viewSymbols, 필터 적용된 화면) 기준으로 target 을 dir(-1 위/+1 아래) 이동.
        /// 화면상 인접 종목과만 전역 order 위치를 스왑하므로, 분류 필터가 켜져 있어도 화면상 한 칸 이동이 정확하고
        /// 필터로 숨겨진 다른 분류 종목의 상대 순서를 건드리지 않는다. 반환: 실제 스왑 여부(경계/부재 시 false).
        /// </summary>
        public static bool MoveInView(List<string> order, IReadOnlyList<string> viewSymbols, string target, int dir)
        {
            int p = IndexOf(viewSymbols, target); if (p < 0) return false;
            int pn = p + dir; if (pn < 0 || pn >= viewSymbols.Count) return false;   // 화면 경계
            var neighbor = viewSymbols[pn];
            int i = IndexOf(order, target);
            int j = IndexOf(order, neighbor);
            if (i < 0 || j < 0) return false;
            (order[i], order[j]) = (order[j], order[i]);   // 전역 위치 스왑(숨은 항목 상대순서 불변)
            return true;
        }
    }
}
