// ============================================================================
// ArenaPreferenceAdapter — v12.18.69-DEV: IArenaPreferencePort 구현(LocalDatabase 설정 테이블 · 고정 키 Arena.PlutoGovernanceEnabled)
//
//   저장 = 단일 작성자 드레인 태스크(스레드 풀): 원하는 값(_desired)과 기록된 값(_written)이 같아질 때까지 SetSetting.
//     연속 토글은 드레인 1개가 최신 원하는 값을 다시 읽어 기록 → 늦게 끝난 이전 기록이 최신 값을 덮는 역순 기입 0.
//     드레인 종료(수렴 · 실패)는 잠금 안에서 _drain = null 로 표시 → 종료 직후 들어온 저장은 새 드레인을 띄운다(유실 0).
//   실패(DB 잠금 초과 · 폐기된 연결 등) = 계수 + false · 자동 재시도 없음(다음 토글이 다시 시도). 값 표기 = "true"/"false"(RuntimeConfig.GetBool 해석 범위).
//   복원 = 호스트 RuntimeConfig 가 같은 키를 읽는다(환경변수 우선 — 같은 이름 환경변수가 있으면 저장값보다 우선).
//   [v12.18.72-DEV] 자동 미러링 선호: 아레나별 슬롯(거버너와 같은 드레인 규약 · 슬롯 = 값 1개의 단일 작성자) — 값 = 활성 1비트 + 배율 bp 를 long 1개로 압축 ·
//     기록 = 배율 키("0.5" · 불변 문화권) → 활성 키 순서(중간 중단 = 두 키 모두 유효값 · 배율 단독 갱신은 무해).
//     복원 = RestoreAutoCopy(호스트 RuntimeConfig · 기동 1회 · 콜드): 배율 키 해석 불가/범위 밖 = 복원 생략(기본 OFF · 1.0x 유지 · fail-closed).
// ============================================================================
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena.Common;

namespace StockManager.Adapters
{
    public sealed class ArenaPreferenceAdapter : IArenaPreferencePort
    {
        public const string GovernanceKey = "Arena.PlutoGovernanceEnabled";
        public const string DefaultAutoCopyRatio = "1.0";

        private sealed class Slot
        {
            public long Desired = -1, Written = -1;
            public Task<bool>? Drain;
        }

        private readonly LocalDatabase _db;
        private readonly object _gate = new();
        private readonly Slot _governance = new();
        private readonly Slot[] _autoCopy = { new(), new(), new() };
        private long _failures;

        public ArenaPreferenceAdapter(LocalDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        public long SaveFailures => Interlocked.Read(ref _failures);

        /// <summary>[v12.18.72-DEV] 아레나 자동 미러링 활성 키(범위 밖 아레나 = 예외).</summary>
        public static string AutoCopyEnabledKey(int arena) => "Arena." + ArenaName(arena) + ".AutoCopyEnabled";

        /// <summary>[v12.18.72-DEV] 아레나 자동 미러링 배율 키(값 = 불변 문화권 decimal "0.5" · "1.0").</summary>
        public static string AutoCopyRatioKey(int arena) => "Arena." + ArenaName(arena) + ".AutoCopyRatio";

        private static string ArenaName(int arena) => arena switch
        {
            ArenaPreferenceArena.Krx => "Krx",
            ArenaPreferenceArena.Us => "Us",
            ArenaPreferenceArena.Crypto => "Crypto",
            _ => throw new ArgumentOutOfRangeException(nameof(arena)),
        };

        private static bool? ParseBool(string? v) => v?.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => null,
        };

        /// <summary>배율 문자열 → bp(불변 문화권 · 범위 밖 · 해석 불가 = false).</summary>
        public static bool TryParseRatioBp(string? v, out int ratioBp)
        {
            ratioBp = 0;
            return decimal.TryParse(v?.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var r) && ArenaCopyMath.TryRatioBp(r, out ratioBp);
        }

        private static string FormatRatio(int ratioBp) => (ratioBp / 10_000m).ToString("0.0###", CultureInfo.InvariantCulture);

        public bool? StoredGovernanceEnabled
        {
            get
            {
                try { return ParseBool(_db.GetSetting(GovernanceKey)); }
                catch (Exception) { return null; }
            }
        }

        public ArenaAutoCopyPreference? StoredAutoCopy(int arena)
        {
            if (!ArenaPreferenceArena.Valid(arena)) return null;
            string? enabled, ratio;
            try
            {
                enabled = _db.GetSetting(AutoCopyEnabledKey(arena));
                ratio = _db.GetSetting(AutoCopyRatioKey(arena));
            }
            catch (Exception) { return null; }
            if (ParseBool(enabled) is not { } on || !TryParseRatioBp(ratio ?? DefaultAutoCopyRatio, out int bp)) return null;
            return new ArenaAutoCopyPreference(on, bp / 10_000m);
        }

        public Task<bool> SaveGovernanceEnabledAsync(bool enabled) => Save(_governance, enabled ? 1 : 0, WriteGovernance);

        public Task<bool> SaveAutoCopyAsync(int arena, bool enabled, decimal ratio)
        {
            if (!ArenaPreferenceArena.Valid(arena) || !ArenaCopyMath.TryRatioBp(ratio, out int bp)) return Task.FromResult(false);
            return Save(_autoCopy[arena], ((long)bp << 1) | (enabled ? 1L : 0L), v => WriteAutoCopy(arena, v));
        }

        private void WriteGovernance(long v) => _db.SetSetting(GovernanceKey, v == 1 ? "true" : "false");

        private void WriteAutoCopy(int arena, long v)
        {
            _db.SetSetting(AutoCopyRatioKey(arena), FormatRatio((int)(v >> 1)));
            _db.SetSetting(AutoCopyEnabledKey(arena), (v & 1) == 1 ? "true" : "false");
        }

        private Task<bool> Save(Slot slot, long value, Action<long> write)
        {
            lock (_gate)
            {
                slot.Desired = value;
                return slot.Drain ??= Task.Run(() => Drain(slot, write));
            }
        }

        private bool Drain(Slot slot, Action<long> write)
        {
            while (true)
            {
                long want;
                lock (_gate)
                {
                    want = slot.Desired;
                    if (want == slot.Written) { slot.Drain = null; return true; }
                }
                try
                {
                    write(want);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _failures);
                    lock (_gate) { slot.Written = -1; slot.Drain = null; }
                    return false;
                }
                lock (_gate) slot.Written = want;
            }
        }

        /// <summary>[v12.18.72-DEV] 기동 복원(호스트 · 콜드 1회): 같은 precedence(환경변수 → DB)로 활성/배율 판독 → apply(활성, bp).
        /// 배율 해석 불가 · 범위 밖 = 복원 생략(false · 엔진 기본 OFF 유지). 활성 해석 불가 = false(꺼짐) 로 배율만 복원.</summary>
        public static bool RestoreAutoCopy(RuntimeConfig cfg, int arena, Func<bool, int, bool> apply)
        {
            ArgumentNullException.ThrowIfNull(cfg);
            ArgumentNullException.ThrowIfNull(apply);
            if (!TryParseRatioBp(cfg.GetString(AutoCopyRatioKey(arena), DefaultAutoCopyRatio), out int bp)) return false;
            return apply(cfg.GetBool(AutoCopyEnabledKey(arena), false), bp);
        }
    }
}
