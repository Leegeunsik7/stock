// ============================================================================
// KseSymbolText — [E2E 스트레스 · δ=0] KSE 6자리 종목 코드 → "D6" 문자열 캐시
//
//  OrderDrainWorker 는 IBroker.Place(string symbol, …) 계약 때문에 주문마다 SymbolCode.ToString("D6") 로 새 문자열을
//  만들었다(실측: 송출 1건당 문자열 1개 할당 — E2E 하네스 δ 측정에서 드러남). 실 운용 종목은 수십~수백 개로 고정적이므로
//  직접 사상(code & mask) 캐시에 불변 항목(코드+문자열)을 원자 게시해 재사용한다 — 적중 = 할당 0, 미스(최초·충돌 교체) = 1회 할당.
//  항목은 불변 참조형 1개라 Volatile 게시만으로 찢김 없음(코드·문자열이 항상 짝). 경합 시 중복 생성은 무해(같은 값).
// ============================================================================
using System.Threading;

namespace MS2E.Infrastructure
{
    public static class KseSymbolText
    {
        private const int Slots = 4096;   // 2의 거듭제곱(마스크 색인)
        private sealed class Entry { public readonly long Code; public readonly string Text; public Entry(long c, string t) { Code = c; Text = t; } }
        private static readonly Entry?[] s_table = new Entry?[Slots];
        private static long s_misses;

        /// <summary>캐시 미스(문자열 생성) 누계 — 정상 운용에서는 종목 수 근처에서 멈춘다.</summary>
        public static long Misses => Interlocked.Read(ref s_misses);

        /// <summary>코드 → "000000" 6자리 문자열(적중 δ=0). 범위 밖(음수·7자리↑)도 동일 규칙으로 포맷(호출측 검증 책임).</summary>
        public static string D6(long code)
        {
            int i = (int)(code & (Slots - 1));
            var e = Volatile.Read(ref s_table[i]);
            if (e is not null && e.Code == code) return e.Text;
            Interlocked.Increment(ref s_misses);
            var fresh = new Entry(code, code.ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
            Volatile.Write(ref s_table[i], fresh);
            return fresh.Text;
        }
    }
}
