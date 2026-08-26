// ============================================================================
// MicroCapitalCanaryGovernor — v10.1.0 실전 운용: 카나리 소액 집행 거버너 (Gemini 보강 과제)
//
//  최초 실전 집행 시 전체 자본의 극소 비율(기본 0.1%=10bps) 이내로만 주문 사출을 허용하고, 실측 체결 슬리피지가
//  임계를 초과하면 O(1)로 무장을 해제하고 드라이런으로 자동 강등한다. "작게 시작해 실측으로 증명하고, 나빠지면 즉시
//  물러선다" — 실자본 최초 노출의 물리적 안전 캡.
//
//  · TryAdmitOrder(notional): 누적 카나리 명목이 캡을 넘으면 거부(δ=0·CAS·다중 라이터 안전·오버플로 가드).
//  · RecordSlippage(bps): 임계 초과 → 강등 래치(비가역) + onDowngrade 콜백(→ 드라이런 전환) + 감사 각인.
//  · CanaryBlocking(강등 시 무장 tighten-only) — liveArmed blockWhile 결속.
//
//  ★불변: δ=0(TryAdmit/RecordSlippage 핫패스 힙 0·Interlocked/CAS·Int128 값타입) · 결정론(순수 산술·벽시계 무참조) ·
//    liveArmed:false(순수 캡/판정 — IBroker·소켓·onSend 참조 0 · 캡은 LiveBroker 경로에서만·CanaryBlocking tighten-only) ·
//    fail-closed(캡 초과·강등 → 거부·오버플로 → 거부).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>카나리 소액 집행 거버너(δ=0). 자본 ≤0.1% 캡 내 주문만 admit·슬리피지 임계 초과 시 O(1) 드라이런 강등.</summary>
    public sealed class MicroCapitalCanaryGovernor
    {
        private const long Scale = 100_000_000L;   // QFixed 10^8

        private readonly long _capNotionalRaw;         // 최대 누적 카나리 명목(자본 × capBps / 10000)
        private readonly long _slippageThresholdBps;   // 이 bps 초과 체결 → 강등
        private readonly Action? _onDowngrade;         // 강등 콜백(→ 드라이런 전환·미러 라우팅)
        private readonly IgnitionAuditTrailRecorder? _audit;
        private long _cumNotionalRaw, _admitted, _rejected, _lastSlippageBps, _downgraded;

        public MicroCapitalCanaryGovernor(long capitalKrwRaw, long capBps = 10L, long slippageThresholdBps = 50L,
                                          Action? onDowngrade = null, IgnitionAuditTrailRecorder? audit = null)
        {
            // 캡 명목 = 자본 × capBps / 10000(Int128 오버플로 안전). capBps 기본 10=0.1%.
            _capNotionalRaw = (long)((System.Int128)Math.Max(0L, capitalKrwRaw) * Math.Clamp(capBps, 1L, 10_000L) / 10_000L);
            _slippageThresholdBps = Math.Max(1L, slippageThresholdBps);
            _onDowngrade = onDowngrade;
            _audit = audit;
        }

        // ── 읽기(δ=0·락프리) ──
        public long CapNotionalRaw   => _capNotionalRaw;
        public long CumNotionalRaw   => Interlocked.Read(ref _cumNotionalRaw);
        public long Admitted         => Interlocked.Read(ref _admitted);
        public long Rejected         => Interlocked.Read(ref _rejected);
        public long LastSlippageBps  => Interlocked.Read(ref _lastSlippageBps);
        public bool Downgraded       => Interlocked.Read(ref _downgraded) == 1L;
        /// <summary>카나리 캡 집행율(‰). cum/cap × 1000.</summary>
        public long CapUtilizationPermille
        {
            get { long cap = _capNotionalRaw; return cap > 0L ? (long)((System.Int128)Interlocked.Read(ref _cumNotionalRaw) * 1000L / cap) : 0L; }
        }
        /// <summary>★blockWhile — 슬리피지 임계 초과 강등 시 무장 차단(tighten-only·드라이런으로 물러섬).</summary>
        public bool CanaryBlocking   => Interlocked.Read(ref _downgraded) == 1L;

        /// <summary>주문 사출 전 카나리 캡 검문(δ=0·CAS·다중 라이터 안전). 강등·캡 초과·오버플로 → 거부.</summary>
        public bool TryAdmitOrder(long notionalRaw)
        {
            if (notionalRaw <= 0L) { Interlocked.Increment(ref _rejected); return false; }   // ★0/음수 명목(퇴화·미상) 거부 — zero-notional 은 강등 후 CAS 무효화 불가하므로 입구 차단
            if (Interlocked.Read(ref _downgraded) == 1L) { Interlocked.Increment(ref _rejected); return false; }
            long cur, proj;
            do
            {
                cur  = Interlocked.Read(ref _cumNotionalRaw);
                proj = cur + notionalRaw;
                if (proj < cur || proj > _capNotionalRaw) { Interlocked.Increment(ref _rejected); return false; }   // 오버플로 or 캡 초과
            }
            while (Interlocked.CompareExchange(ref _cumNotionalRaw, proj, cur) != cur);
            Interlocked.Increment(ref _admitted);
            return true;
            // ★정직 고지(bounded 누수 수용): 강등 순간(line 61 통과 직후 타 스레드 강등)의 in-flight 최대 1건 admit 은 lock-free 단일워드
            //   CAS 의 본질적 한계다. 그러나 이 1건은 ★cap 검문을 이미 통과(proj≤cap)한 under-cap 주문 — 자본 캡(≤0.1%)을 '초과'하지 않고
            //   '한 주문 늦은 후퇴'일 뿐(캡 breach 아님). cum 을 봉인/롤백해 닫으면 각각 텔레메트리 왜곡/음수 cum(Reset 경합)이라는 더 나쁜
            //   결함을 낳음이 적대검증 2회로 확증됐으므로, cum 정확성(CAS 단조·Reset 0·항상 비음수)을 지키고 bounded 누수를 정직 수용한다.
        }

        /// <summary>실측 체결 슬리피지 기록(δ=0). 임계 초과 → O(1) 강등 래치(비가역) + 드라이런 콜백 + 감사 각인.</summary>
        public void RecordSlippage(long slippageBps)
        {
            Volatile.Write(ref _lastSlippageBps, slippageBps);
            // ★임계 초과 → 단일 강등 래치(비가역). cum 은 미변경(정확성 유지) — line 61 강등 검문이 이후 admit 을 차단.
            //   봉인/롤백을 쓰지 않음: 봉인=cum→cap 텔레메트리 왜곡, 롤백=음수 cum/Reset 경합(둘 다 적대검증 확증). cum 은 항상 비음수·정확.
            if (slippageBps > _slippageThresholdBps && Interlocked.CompareExchange(ref _downgraded, 1L, 0L) == 0L)
            {
                try { _onDowngrade?.Invoke(); } catch { /* 강등 콜백 실패 무해 — 래치는 유지(fail-closed) */ }
                _audit?.Record(IgnitionEventKind.CanaryDowngrade, slippageBps, Interlocked.Read(ref _cumNotionalRaw));
            }
        }

        /// <summary>카나리 세션 재개(δ=0). 누적 명목·강등 래치 영점(운용자가 원인 교정 후 재시도). 생애 카운터는 텔레메트리로 유지.</summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _cumNotionalRaw, 0L);
            Interlocked.Exchange(ref _downgraded, 0L);
            Volatile.Write(ref _lastSlippageBps, 0L);
        }
    }
}
