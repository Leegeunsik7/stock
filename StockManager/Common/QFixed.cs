// ============================================================================
// QFixed — Season 27: 무할당 고정소수점 정밀도 척추 (Decimal Sweep)
//
//  부동소수점 오차·부당 절사로 인한 총자산 원장 디싱크('소수점 증발 유령')를 통제한다. ★정직: 16자리 정확곱을
//  8자리로 줄일 때 서브-틱 잔여는 0으로 '증발'시킬 수 없다 — 대신 그 잔여를 '항상 거래소 유리(플랫폼 불리)'
//  방향으로 편향해 원장이 플랫폼 가치를 결코 과대계상하지 않게 한다(누수가 있다면 늘 플랫폼에 불리한 안전 방향).
//  long Raw × 고정 스케일 10^8(사토시·원 모두 커버)의 unmanaged readonly record struct — 문자열 파싱·
//  decimal 박싱 없이 순수 정수 연산자만으로 나노초·제로 할당(δ=0)으로 동작한다.
//
//  ── 오버플로 트랩 ───────────────────────────────────────────────────────────
//  교차 자산 곱(QPrice×QQty)은 Raw·Raw 가 long 을 넘으므로 Int128 중간 연산 후 checked 내로잉으로
//  long 범위 초과를 OverflowException 으로 포착한다. 가산/감산도 checked — 묵시적 래핑(silent wrap) 차단.
//
//  ── 보수 편향 정렬(거래소 유리) ─────────────────────────────────────────────
//  "모호함 앞 Fail-Closed": 계산값이 호가단위(tick)·수량단위(step)의 불연속 배수 경계에서 미세 오차를 내면,
//  플랫폼에 '불리'하고 거래소에 '유리'한 방향으로 강제 편향한다 — 매수가는 올림(비용↑), 매도가는 내림(수령↓),
//  수량은 항상 내림(보유/체결 과대 금지). 이로써 원장은 그리드에 정렬된 값만 담아 유령 누수가 누적되지 않는다.
// ============================================================================
using System;
using System.Runtime.CompilerServices;

namespace StockManager.Common
{
    public enum OrderSide : byte { Buy = 0, Sell = 1 }

    /// <summary>고정소수점 raw 정수 산술 — 전부 checked(오버플로 트랩)·무할당. 스케일 10^8.</summary>
    internal static class QMath
    {
        public const long Scale = 100_000_000L;   // 10^8

        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static long Add(long a, long b) => checked(a + b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static long Sub(long a, long b) => checked(a - b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static long Scalar(long a, long n) => checked(a * n);

        // 교차 곱 명목금액: (a/S)·(b/S) 를 S 스케일로 = a·b/S. Int128 중간(무할당 값 타입) + checked 내로잉.
        // ★적대적 리뷰(major) — 16자리 정확곱을 8자리로 줄일 때의 서브-틱 잔여는 '0으로 증발'하지 않는다.
        //   roundUp 로 그 잔여를 '거래소 유리' 방향으로 편향한다(매수 비용=올림·매도 수령=내림). 잔여를 0 방향
        //   (toward-zero)으로 절사하면 매수 비용을 과소계상해 보수 가격 정렬(AlignUp)을 무효화하므로 분리 제어.
        public static long Notional(long a, long b, bool roundUp)
        {
            if (a < 0 || b < 0) throw new ArgumentOutOfRangeException(nameof(a), "Q: 음수 입력(poison) — 명목 곱 거부");
            Int128 prod = (Int128)a * b;
            Int128 q    = roundUp ? (prod + (Scale - 1)) / Scale : prod / Scale;   // 비음수: ceil vs floor
            return checked((long)q);
        }
        // 바 연산자(QPrice*QQty)용 — 0 방향(floor) 절사. ★보수 정산은 QLedgerGuard(방향 인지)를 쓸 것.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long CrossMul(long a, long b) => Notional(a, b, roundUp: false);

        // 교차 나눗셈: (num/S)/(den/S) 를 S 스케일로 = num·S/den. 음수/0 분모·음수 분자 트랩.
        public static long CrossDiv(long num, long den)
        {
            if (num < 0 || den < 0) throw new ArgumentOutOfRangeException(nameof(num), "Q: 음수 입력(poison)");
            if (den == 0) throw new DivideByZeroException("Q: 0 분모");
            return checked((long)((Int128)num * Scale / den));
        }

        // 경계 변환(문자열 파싱 대체) — decimal→long 캐스트는 0 방향 절사 + 범위 초과 시 OverflowException(고유).
        public static long FromDecimal(decimal v) => (long)(v * Scale);
        public static decimal ToDecimal(long raw) => raw / (decimal)Scale;

        public static long AlignDown(long raw, long step)
        {
            if (step <= 0) throw new ArgumentOutOfRangeException(nameof(step), "Q: 스텝은 양수여야 함");
            if (raw  <  0) throw new ArgumentOutOfRangeException(nameof(raw),  "Q: 음수 정렬 불가(poison)");
            return raw - (raw % step);
        }
        public static long AlignUp(long raw, long step)
        {
            if (step <= 0) throw new ArgumentOutOfRangeException(nameof(step), "Q: 스텝은 양수여야 함");
            if (raw  <  0) throw new ArgumentOutOfRangeException(nameof(raw),  "Q: 음수 정렬 불가(poison)");
            long r = raw % step;
            return r == 0 ? raw : checked(raw + (step - r));   // 올림 시 long 경계 초과 트랩
        }
        public static bool IsAligned(long raw, long step) => step > 0 && raw >= 0 && raw % step == 0;
    }

    /// <summary>수량(코인 소수점·주식 정수 공용). 스케일 10^8 raw. 보수 정렬 = 항상 내림(과대 금지).</summary>
    public readonly record struct QQty(long Raw)
    {
        public static QQty Of(decimal v) => new(QMath.FromDecimal(v));
        public decimal ToDecimal()       => QMath.ToDecimal(Raw);
        public bool    IsValid           => Raw >= 0;

        public static QQty operator +(QQty a, QQty b) => new(QMath.Add(a.Raw, b.Raw));
        public static QQty operator -(QQty a, QQty b) => new(QMath.Sub(a.Raw, b.Raw));
        public static QQty operator *(QQty a, long n) => new(QMath.Scalar(a.Raw, n));
        public static bool operator <(QQty a, QQty b) => a.Raw <  b.Raw;
        public static bool operator >(QQty a, QQty b) => a.Raw >  b.Raw;
        public static bool operator <=(QQty a, QQty b) => a.Raw <= b.Raw;
        public static bool operator >=(QQty a, QQty b) => a.Raw >= b.Raw;

        public bool IsAligned(QQty step) => QMath.IsAligned(Raw, step.Raw);
        public QQty AlignDown(QQty step) => new(QMath.AlignDown(Raw, step.Raw));   // 보수(보유/체결 과대 금지)
        public QQty AlignUp(QQty step)   => new(QMath.AlignUp(Raw, step.Raw));
    }

    /// <summary>호가(단가). 스케일 10^8 raw. 보수 정렬 = 매수 올림(비용↑)·매도 내림(수령↓) — 거래소 유리.</summary>
    public readonly record struct QPrice(long Raw)
    {
        public static QPrice Of(decimal v) => new(QMath.FromDecimal(v));
        public decimal ToDecimal()         => QMath.ToDecimal(Raw);
        public bool    IsValid             => Raw >= 0;

        public static QPrice operator +(QPrice a, QPrice b) => new(QMath.Add(a.Raw, b.Raw));
        public static QPrice operator -(QPrice a, QPrice b) => new(QMath.Sub(a.Raw, b.Raw));
        public static bool operator <(QPrice a, QPrice b) => a.Raw <  b.Raw;
        public static bool operator >(QPrice a, QPrice b) => a.Raw >  b.Raw;
        public static bool operator <=(QPrice a, QPrice b) => a.Raw <= b.Raw;
        public static bool operator >=(QPrice a, QPrice b) => a.Raw >= b.Raw;

        // 교차 자산 곱 — 단가×수량 = 명목금액. 0 방향(floor) 절사·음수/오버플로 트랩. ★보수(방향) 정산은 QLedgerGuard 사용.
        public static QMoney operator *(QPrice p, QQty q) => new(QMath.CrossMul(p.Raw, q.Raw));

        public bool   IsAligned(QPrice tick) => QMath.IsAligned(Raw, tick.Raw);
        public QPrice AlignDown(QPrice tick) => new(QMath.AlignDown(Raw, tick.Raw));
        public QPrice AlignUp(QPrice tick)   => new(QMath.AlignUp(Raw, tick.Raw));
        /// <summary>보수 편향 정렬 — 매수는 올림(비용↑), 매도는 내림(수령↓). 플랫폼 불리·거래소 유리.</summary>
        public QPrice ConservativeAlign(QPrice tick, OrderSide side)
            => side == OrderSide.Buy ? AlignUp(tick) : AlignDown(tick);
    }

    /// <summary>명목금액/원장 가치. 스케일 10^8 raw. 단가÷명목 등 역연산 + 보수 내림.</summary>
    public readonly record struct QMoney(long Raw)
    {
        public static QMoney Of(decimal v) => new(QMath.FromDecimal(v));
        public decimal ToDecimal()         => QMath.ToDecimal(Raw);
        public bool    IsValid             => Raw >= 0;

        public static QMoney operator +(QMoney a, QMoney b) => new(QMath.Add(a.Raw, b.Raw));
        public static QMoney operator -(QMoney a, QMoney b) => new(QMath.Sub(a.Raw, b.Raw));
        public static QQty operator /(QMoney m, QPrice p)   => new(QMath.CrossDiv(m.Raw, p.Raw));   // 금액÷단가=수량
    }

    /// <summary>
    /// 청산 게이트 — 보수 정렬 후 명목금액 산출. 입력 오염(음수·0 스텝)·정밀도 오버플로 시 절대 확산시키지 않고
    /// 즉시 ForceFreeze(fail-closed) + false. 정상 경로 무할당(예외 경로만 할당 — 동결이라 핫패스 아님).
    /// </summary>
    public static class QLedgerGuard
    {
        public static bool TryClearNotional(QPrice price, QQty qty, QPrice tick, QQty step, OrderSide side,
                                            OrderAttestationService? attestation,
                                            out QMoney notional, out QPrice alignedPrice, out QQty alignedQty)
        {
            notional = default; alignedPrice = default; alignedQty = default;
            try
            {
                if (!price.IsValid || !qty.IsValid || tick.Raw <= 0 || step.Raw <= 0)
                { Freeze(attestation, "Q 청산: 음수/0 호가·수량 단위 — 입력 오염, fail-closed"); return false; }

                alignedPrice = price.ConservativeAlign(tick, side);   // 매수↑·매도↓
                alignedQty   = qty.AlignDown(step);                   // 수량 항상 내림(과대 금지)
                // ★적대적 리뷰(major) — 명목금액의 서브-틱 잔여도 거래소 유리로 편향: 매수=올림(비용↑)·매도=내림
                //   (수령↓). 바 연산자(floor)를 쓰면 매수 비용이 과소계상돼 가격 AlignUp 보수 정렬이 무효화된다.
                notional     = new QMoney(QMath.Notional(alignedPrice.Raw, alignedQty.Raw, roundUp: side == OrderSide.Buy));
                return true;
            }
            // OverflowException(명목 오버플로)이 유일한 실도달 경로. Arg/DivByZero catch 는 방어적 — 입력 게이트가
            // 음수·0 스텝을 선차단하고 CrossDiv 미사용이라 현 경로에선 도달 불가(미래 확장·심층 방어용으로 유지).
            catch (OverflowException)           { Freeze(attestation, "Q 청산: 정밀도 오버플로 — 중단, fail-closed"); return false; }
            catch (DivideByZeroException)       { Freeze(attestation, "Q 청산: 0 분모 — 중단, fail-closed"); return false; }
            catch (ArgumentOutOfRangeException) { Freeze(attestation, "Q 청산: 음수/스텝 오염 — 중단, fail-closed"); return false; }
        }

        private static void Freeze(OrderAttestationService? att, string msg)
        {
            try { att?.ForceFreeze(msg); } catch { /* 동결 실패해도 false 반환은 정직 유지 */ }
        }
    }
}
