// ============================================================================
// TightenOnlyUiSettingWriter — [v12.18.3-DEV · 추천 A] 순수 UI 발 전략 안전 설정 '강화 전용' 기입(단조성 가드 · fail-closed)
//
//  배경: UiSettingWriteGuard 허용 목록은 Strategy.* 를 막는다(호스트 안전 네임스페이스 — 출하 IL 가드 ⑤). 운영자가 화면에서 긴급히 '더 엄격하게'
//   만들 길이 없었다. 이 기입기는 Strategy.* 중 등재된 키만, 그리고 호스트가 실제로 쓰게 될 값(유효값)이 지금보다 엄격해지거나 같을 때만
//   기입한다. 완화 · 미등재 키 · 무장/카나리/디스패치 키 · 형식 불량 = 폐기(예외 아님) + 판정별 계수.
//
//  [방향 A] 단조성 행렬: 키 표(Keys) ↔ 규칙 비트(Rules · RVA 정적 데이터) ↔ 호스트 폴백 · 하한 · 상한(정적 데이터). 판정 = 표 조회 + 정수 비교(힙 0).
//   규칙: TrueOnly(1) = 유효값이 true 로만(false → true · true 유지) · NonDecreasing(2) = 유효값 ≥ 현재 · NonIncreasing(4) = 유효값 ≤ 현재.
//   ★'유효값' = 호스트 RuntimeConfig 판독 규칙 그대로: 불리언 true/1/yes/on · false/0/no/off(대소문자 무시) 외 = 폴백 · 정수 파싱 실패 = 폴백 ·
//     [하한, 상한] 클램프. 비교는 원문이 아니라 유효값으로 한다 — "-5" 는 하한 5000 으로 클램프돼 9500 보다 느슨하므로 거부, "99999" 는 상한
//     10000 으로 클램프돼 엄격하므로 허용(원문 저장 · 호스트도 같은 클램프).
//   ★제안값은 폴백을 쓰지 않는다: 형식 불량 = 거부(Malformed). 폴백으로 해석되게 두면 '모르는 값' 이 기본값(더 느슨할 수 있음)이 된다.
//   ★현재값은 호스트 precedence(환경변수 → DB → 폴백)로 읽는다. 환경변수가 그 키를 덮고 있으면 DB 기입은 효과가 없으므로 거부
//     (EnvironmentOverride — '강화됐다'고 보고하고 실제로는 무효인 거짓 성공 방지).
//   ★판독-비교-기입은 잠금 하나로 직렬화 — 동시 기입 두 건이 같은 현재값을 보고 늦게 쓴 '덜 엄격한' 값이 이기는 경합(9500 → 9800 · 9600) 차단.
//  호스트 반영: Strategy.SignalGateEnabled 는 1초 판독(SignalGateRuntimeSwitch) · ConfidenceThresholdBp · AdaptiveMaxBp 는 같은 1초 판독이
//   런타임 하한을 '올리기만' 한다(QuantSignalProvider.RaiseConfidenceFloorBp · VolatilityAdaptiveCalibrator.RaiseMaxBp) · AdaptiveThreshold ·
//   Churn* 은 다음 기동부터(생성 시 1회 판독).
//  출하 IL 가드(ShippedBinaryLiveArmedGuard ⑦~⑩)가 이 형식의 키 표 · 규칙 비트 · 폴백/하한/상한 = 호스트 판독 상수 · IsTightening 비교 방향 ·
//   TryWrite 분기 · WriteStored 호출 위치를 바이너리에서 대조한다.
// ============================================================================
using System;
using System.Globalization;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>강화 전용 기입 판정(순수 UI 코드 TightenVerdictCode 와 같은 값 — 테스트가 대조).</summary>
    public enum TightenVerdict : byte
    {
        Tightened = 0, Unchanged = 1, UnknownKey = 2, Loosening = 3, Malformed = 4, EnvironmentOverride = 5, StoreFault = 6,
    }

    /// <summary>강화 전용 기입 저장소(호스트 어댑터 구현). WriteStored 는 TightenOnlyUiSettingWriter.TryWrite 에서만 호출된다(출하 IL 가드 ⑨).</summary>
    public interface ITightenSettingStore
    {
        /// <summary>환경변수 값(호스트 precedence 1순위 · 없으면 null).</summary>
        string? ReadEnvironment(string key);
        /// <summary>DB 설정 값(2순위 · 없으면 null).</summary>
        string? ReadStored(string key);
        void WriteStored(string key, string value);
    }

    public static class TightenOnlyUiSettingWriter
    {
        public const byte RuleTrueOnly = 1, RuleNonDecreasing = 2, RuleNonIncreasing = 4;

        // 키 표(순서 = 아래 정적 데이터 열과 1:1). 전부 Strategy.* — 무장(Dispatch.*) · 카나리(Canary.*) · 브로커(Broker.*) 키는 표에 없으므로 UnknownKey.
        private static readonly string[] Keys =
        {
            "Strategy.SignalGateEnabled", "Strategy.AdaptiveThreshold", "Strategy.ConfidenceThresholdBp", "Strategy.AdaptiveMaxBp",
            "Strategy.ChurnSoftPerMin", "Strategy.ChurnHardPerMin",
        };
        // [방향 A] 규칙 비트(RVA 정적 바이트 — 조회 할당 0) · 호스트 폴백 · 하한 · 상한(BlazorHomeForm cfg.GetBool/GetInt 인자와 같은 값 — 불리언은 0/1 · 하한 0 · 상한 1).
        //  정수 표는 정적 배열(.cctor 1회 InitializeArray). ReadOnlySpan<int> 속성(RuntimeHelpers.CreateSpan)은 이 런타임에서 조회마다 64B 를 할당했다(테스트 실측).
        private static ReadOnlySpan<byte> Rules => new byte[] { RuleTrueOnly, RuleTrueOnly, RuleNonDecreasing, RuleNonDecreasing, RuleNonIncreasing, RuleNonIncreasing };
        private static readonly int[] Fallbacks = { 0, 1, 8_500, 9_500, 30, 60 };
        private static readonly int[] Minimums = { 0, 0, 5_000, 5_000, 1, 2 };
        private static readonly int[] Maximums = { 1, 1, 10_000, 10_000, 100_000, 100_000 };

        private static readonly object Gate = new();
        private static long _tightened, _unchanged, _unknown, _loosening, _malformed, _envOverride, _storeFault;

        public static int KeyCount => Keys.Length;
        public static string KeyAt(int i) => Keys[i];
        public static byte RuleAt(int i) => Rules[i];
        public static int FallbackAt(int i) => Fallbacks[i];
        public static int MinimumAt(int i) => Minimums[i];
        public static int MaximumAt(int i) => Maximums[i];
        public static long Tightened => Interlocked.Read(ref _tightened);
        public static long Unchanged => Interlocked.Read(ref _unchanged);
        public static long UnknownKeys => Interlocked.Read(ref _unknown);
        public static long Loosenings => Interlocked.Read(ref _loosening);
        public static long Malformed => Interlocked.Read(ref _malformed);
        public static long EnvironmentOverrides => Interlocked.Read(ref _envOverride);
        public static long StoreFaults => Interlocked.Read(ref _storeFault);
        /// <summary>거부(폐기) 누적 = 미등재 + 완화 + 형식 불량 + 환경변수 덮음 + 저장 실패.</summary>
        public static long Rejected => UnknownKeys + Loosenings + Malformed + EnvironmentOverrides + StoreFaults;

        /// <summary>등재 키 인덱스(서수 비교 · 할당 0) — 없으면 −1.</summary>
        public static int IndexOf(string? key)
        {
            if (key is null) return -1;
            for (int i = 0; i < Keys.Length; i++)
                if (string.Equals(Keys[i], key, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// <summary>
        /// ★단조성 판정 코어(정수 · 할당 0). 비교 방향은 출하 IL 가드 ⑩이 판독한다 — 규칙별 비교 1개씩 · 상수 true 반환 0 · 미정의 규칙 = false.
        /// </summary>
        private static bool IsTightening(byte rule, long current, long proposed)
        {
            if ((rule & RuleTrueOnly) != 0) return proposed == 1;
            if ((rule & RuleNonDecreasing) != 0) return proposed >= current;
            if ((rule & RuleNonIncreasing) != 0) return proposed <= current;
            return false;
        }

        /// <summary>호스트 RuntimeConfig 와 같은 유효값 해석. strict = 형식 불량이면 false(제안값) · 아니면 폴백(현재값).</summary>
        public static bool TryEffective(int index, ReadOnlySpan<char> raw, bool strict, out long value)
        {
            int fb = Fallbacks[index], lo = Minimums[index], hi = Maximums[index];
            if (Rules[index] == RuleTrueOnly)
            {
                if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("1", StringComparison.Ordinal)
                    || raw.Equals("yes", StringComparison.OrdinalIgnoreCase) || raw.Equals("on", StringComparison.OrdinalIgnoreCase)) { value = 1; return true; }
                if (raw.Equals("false", StringComparison.OrdinalIgnoreCase) || raw.Equals("0", StringComparison.Ordinal)
                    || raw.Equals("no", StringComparison.OrdinalIgnoreCase) || raw.Equals("off", StringComparison.OrdinalIgnoreCase)) { value = 0; return true; }
                value = fb;
                return !strict;
            }
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) { value = Math.Clamp(n, lo, hi); return true; }
            value = Math.Clamp(fb, lo, hi);
            return !strict;
        }

        /// <summary>판정만(기입 0 · 할당 0). currentRaw = 호스트 precedence 로 읽은 원문(null = 미설정 → 폴백).</summary>
        public static TightenVerdict Judge(int index, ReadOnlySpan<char> proposedRaw, string? currentRaw, out long current, out long proposed)
        {
            current = 0; proposed = 0;
            if ((uint)index >= (uint)Keys.Length) return TightenVerdict.UnknownKey;
            TryEffective(index, currentRaw is null ? ReadOnlySpan<char>.Empty : currentRaw.AsSpan(), strict: false, out current);   // 미설정/불량 = 폴백(클램프)
            if (!TryEffective(index, proposedRaw, strict: true, out proposed)) return TightenVerdict.Malformed;
            if (!IsTightening(Rules[index], current, proposed)) return TightenVerdict.Loosening;
            return proposed == current ? TightenVerdict.Unchanged : TightenVerdict.Tightened;
        }

        /// <summary>
        /// 강화 전용 기입(판독 · 판정 · 기입 직렬화). 반환 = 판정. 기입은 Tightened 일 때만 · 그 외 전부 폐기(예외 0 · 계수).
        /// </summary>
        public static TightenVerdict TryWrite(string? key, string? value, ITightenSettingStore store)
        {
            if (store is null) throw new ArgumentNullException(nameof(store));
            int index = IndexOf(key);
            if (index < 0 || value is null) return Count(index < 0 ? TightenVerdict.UnknownKey : TightenVerdict.Malformed);
            lock (Gate)
            {
                string? env, stored;
                try { env = store.ReadEnvironment(key!); stored = env is null ? store.ReadStored(key!) : null; }
                catch (Exception) { return Count(TightenVerdict.StoreFault); }
                if (env is not null) return Count(TightenVerdict.EnvironmentOverride);
                TightenVerdict v = Judge(index, value.AsSpan(), stored, out _, out _);
                if (v != TightenVerdict.Tightened) return Count(v);                       // ★판정 분기(출하 IL 가드 ⑨) — 기입은 이 아래 1곳뿐
                try { store.WriteStored(key!, value); }
                catch (Exception) { return Count(TightenVerdict.StoreFault); }
                return Count(TightenVerdict.Tightened);
            }
        }

        private static TightenVerdict Count(TightenVerdict v)
        {
            switch (v)
            {
                case TightenVerdict.Tightened: Interlocked.Increment(ref _tightened); break;
                case TightenVerdict.Unchanged: Interlocked.Increment(ref _unchanged); break;
                case TightenVerdict.UnknownKey: Interlocked.Increment(ref _unknown); break;
                case TightenVerdict.Loosening: Interlocked.Increment(ref _loosening); break;
                case TightenVerdict.Malformed: Interlocked.Increment(ref _malformed); break;
                case TightenVerdict.EnvironmentOverride: Interlocked.Increment(ref _envOverride); break;
                default: Interlocked.Increment(ref _storeFault); break;
            }
            return v;
        }
    }
}
