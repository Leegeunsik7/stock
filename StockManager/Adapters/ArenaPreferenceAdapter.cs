// ============================================================================
// ArenaPreferenceAdapter — v12.18.69-DEV: IArenaPreferencePort 구현(LocalDatabase 설정 테이블 · 고정 키 Arena.PlutoGovernanceEnabled)
//
//   저장 = 단일 작성자 드레인 태스크(스레드 풀): 원하는 값(_desired)과 기록된 값(_written)이 같아질 때까지 SetSetting.
//     연속 토글은 드레인 1개가 최신 원하는 값을 다시 읽어 기록 → 늦게 끝난 이전 기록이 최신 값을 덮는 역순 기입 0.
//     드레인 종료(수렴 · 실패)는 잠금 안에서 _drain = null 로 표시 → 종료 직후 들어온 저장은 새 드레인을 띄운다(유실 0).
//   실패(DB 잠금 초과 · 폐기된 연결 등) = 계수 + false · 자동 재시도 없음(다음 토글이 다시 시도). 값 표기 = "true"/"false"(RuntimeConfig.GetBool 해석 범위).
//   복원 = 호스트 RuntimeConfig 가 같은 키를 읽는다(환경변수 우선 — 같은 이름 환경변수가 있으면 저장값보다 우선).
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Adapters
{
    public sealed class ArenaPreferenceAdapter : IArenaPreferencePort
    {
        public const string GovernanceKey = "Arena.PlutoGovernanceEnabled";

        private readonly LocalDatabase _db;
        private readonly object _gate = new();
        private int _desired = -1, _written = -1;
        private Task<bool>? _drain;
        private long _failures;

        public ArenaPreferenceAdapter(LocalDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        public long SaveFailures => Interlocked.Read(ref _failures);

        public bool? StoredGovernanceEnabled
        {
            get
            {
                string? v;
                try { v = _db.GetSetting(GovernanceKey); }
                catch (Exception) { return null; }
                return v?.Trim().ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" or "on" => true,
                    "false" or "0" or "no" or "off" => false,
                    _ => null,
                };
            }
        }

        public Task<bool> SaveGovernanceEnabledAsync(bool enabled)
        {
            lock (_gate)
            {
                _desired = enabled ? 1 : 0;
                return _drain ??= Task.Run(Drain);
            }
        }

        private bool Drain()
        {
            while (true)
            {
                int want;
                lock (_gate)
                {
                    want = _desired;
                    if (want == _written) { _drain = null; return true; }
                }
                try
                {
                    _db.SetSetting(GovernanceKey, want == 1 ? "true" : "false");
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _failures);
                    lock (_gate) { _written = -1; _drain = null; }
                    return false;
                }
                lock (_gate) _written = want;
            }
        }
    }
}
