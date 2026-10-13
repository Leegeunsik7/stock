// ============================================================================
// SignalGateRuntimeSwitch — [v12.18.2-DEV · 추천 A] 호스트 시그널 게이트 런타임 토글(관측 ↔ 강제 · fail-closed 비대칭)
//
//  호스트는 시그널 엔진을 항상 드레인 제안 관측자(IOrderProposalObserver)로 결선한다 = '관측' 모드: 비트 27(수량 ≤ 0 · 승인 폭주 모델 붕괴)과
//   그림자 판정('켰다면 거부') 계수는 게이트 꺼짐에서도 작동하지만 확신/국면 미달 제안을 막지는 않는다.
//  '강제' 모드 = RouterFeedGate.EnableSignalGate — 확신 ≥ max(고정, 국면 임계) ∧ 같은 방향 ∧ 신선 ∧ 1분 승인 한도 이내만 라우터 진입.
//  토글 원천 = Strategy.SignalGateEnabled(호스트 RuntimeConfig — 환경변수 → DB · 매 판독 실시간). 콜드 타이머(1초)가 Poll 한다.
//
//  ★비대칭(fail-closed):
//   · 켜기(관측 → 강제) = 즉시. 판독 예외 = '켜기'로 간주(설정을 못 읽으면 더 엄격한 쪽).
//   · 끄기(강제 → 관측) = 물리 키(ValveWired · LiveArmingWired)가 '둘 다 꺼져' 있을 때만. 하나라도 켜져 있으면(또는 키 판독 예외) 거부 · 계수 —
//     무장 경로가 열릴 수 있는 동안 전방 필터를 설정 한 줄로 내리지 못한다. 끄려면 먼저 물리 키를 내린다.
//   · Strategy.* 는 순수 UI 기입 허용 목록(UiSettingWriteGuard)에 없다 — 호스트 안전 네임스페이스(출하 IL 가드 ⑤가 허용 목록 등재를 거부).
//     토글은 운영자 경로(환경변수 · 관리자 DB 설정)로만.
//  δ=0: Poll 은 콜드(설정 판독 = 문자열 · DB I/O 가능) — 매매 핫패스 밖. 게이트 판정 자체(AdmitSignal · OnProposal)는 δ=0.
// ============================================================================
using System;
using System.Threading;
using StockManager.Adapters;

namespace StockManager.Engines
{
    public sealed class SignalGateRuntimeSwitch
    {
        public const int ModeObserve = 1, ModeEnforce = 2;

        private readonly RouterFeedGate _router;
        private readonly QuantSignalProvider _signals;
        private readonly Func<bool> _desired, _anyKeyOn;
        private readonly Func<int>? _confidenceFloorBp, _adaptiveMaxBp;
        private long _floorRaises, _maxRaises;
        private int _polling;
        private long _polls, _enables, _disables, _disableRefused, _readFaults;

        /// <param name="desired">원하는 상태(Strategy.SignalGateEnabled — 실시간 판독).</param>
        /// <param name="anyKeyOn">물리 키 중 하나라도 켜짐(ValveWired ∨ LiveArmingWired).</param>
        /// <param name="confidenceFloorBp">[v12.18.3] Strategy.ConfidenceThresholdBp 실시간 판독 — 런타임 확신 하한 '상향만'(null = 미적용).</param>
        /// <param name="adaptiveMaxBp">[v12.18.3] Strategy.AdaptiveMaxBp 실시간 판독 — 캘리브레이터 최대 임계 '상향만'(null = 미적용).</param>
        public SignalGateRuntimeSwitch(RouterFeedGate router, QuantSignalProvider signals, Func<bool> desired, Func<bool> anyKeyOn,
                                       Func<int>? confidenceFloorBp = null, Func<int>? adaptiveMaxBp = null)
        {
            _confidenceFloorBp = confidenceFloorBp; _adaptiveMaxBp = adaptiveMaxBp;
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _signals = signals ?? throw new ArgumentNullException(nameof(signals));
            _desired = desired ?? throw new ArgumentNullException(nameof(desired));
            _anyKeyOn = anyKeyOn ?? throw new ArgumentNullException(nameof(anyKeyOn));
        }

        public QuantSignalProvider Signals => _signals;
        public bool Enforced => _router.SignalGateEnabled;
        public int Mode => Enforced ? ModeEnforce : ModeObserve;
        public long Polls => Interlocked.Read(ref _polls);
        public long Enables => Interlocked.Read(ref _enables);
        public long Disables => Interlocked.Read(ref _disables);
        /// <summary>물리 키가 켜진 동안의 끄기 요청 거부 수(누적).</summary>
        public long DisableRefused => Interlocked.Read(ref _disableRefused);
        public long ReadFaults => Interlocked.Read(ref _readFaults);
        public long FloorRaises => Interlocked.Read(ref _floorRaises);
        public long MaxRaises => Interlocked.Read(ref _maxRaises);

        /// <summary>1회 판독 · 적용(콜드 · 단일 진입 — 중첩 호출은 생략). 반환 = 적용 후 강제 여부.</summary>
        public bool Poll()
        {
            if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return Enforced;
            try
            {
                Interlocked.Increment(ref _polls);
                bool want;
                try { want = _desired(); }
                catch (Exception) { Interlocked.Increment(ref _readFaults); want = true; }   // 못 읽음 = 더 엄격한 쪽
                bool on = Enforced;
                if (want && !on)
                {
                    _router.EnableSignalGate(_signals);
                    _signals.SetGateEnforced(true);
                    Interlocked.Increment(ref _enables);
                }
                else if (!want && on)
                {
                    bool keys;
                    try { keys = _anyKeyOn(); }
                    catch (Exception) { Interlocked.Increment(ref _readFaults); keys = true; }   // 키 판독 불가 = 켜진 것으로(해제 거부)
                    if (keys) Interlocked.Increment(ref _disableRefused);
                    else
                    {
                        _router.DisableSignalGate();
                        _signals.SetGateEnforced(false);
                        Interlocked.Increment(ref _disables);
                    }
                }
                // [v12.18.3] 임계 상향 전용 반영 — 판독 예외 = 그 회차 생략(현재 하한 유지 · 낮추는 경로 없음)
                if (_confidenceFloorBp is { } cf)
                {
                    try { if (_signals.RaiseConfidenceFloorBp(cf())) Interlocked.Increment(ref _floorRaises); }
                    catch (Exception) { Interlocked.Increment(ref _readFaults); }
                }
                if (_adaptiveMaxBp is { } am && _signals.Calibrator is { } cal)
                {
                    try { if (cal.RaiseMaxBp(am())) Interlocked.Increment(ref _maxRaises); }
                    catch (Exception) { Interlocked.Increment(ref _readFaults); }
                }
                return Enforced;
            }
            finally { Volatile.Write(ref _polling, 0); }
        }
    }
}
