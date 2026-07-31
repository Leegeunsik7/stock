// ============================================================================
// IcebergReloadSignatureDetector — v9.0.0 호가 재전개 정밀 계측기(빙산 확정 확률)
//
//  숨은 빙산호가(Iceberg)는 표시 잔량이 체결로 줄어드는 찰나, '즉시' 원래 수준으로 재전개(reload)된다 — 인간
//  수기로는 불가능한 나노초 급 재전개 지터가 알고리즘 빙산의 서명이다. 이 계측기는 체결 dip → 재전개까지의
//  지연을 δ=0 로 실측해, 임계 이내 재전개가 반복되면 빙산 확정 확률(‰)을 사출한다.
//
//   서명: 표시잔량 dip(체결) → reloadThreshold 이내에 dip 전 수준의 refillFraction 이상으로 복원 = 빙산 reload 1건.
//
//  ★불변: δ=0(Observe/ConfirmationPermille 힙 0·정수/Volatile) · 결정론(주입 시계·순수 산술) · liveArmed:false
//    (순수 관측 — IBroker·소켓·onSend 참조 0) · fail-closed(경계/오버플로우 안전). ★O(1) 상태(링 불요).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>호가 재전개 서명 계측기(δ=0·O(1)). Observe(표시잔량,ticks) → dip→즉시 refill 반복 시 빙산 확정 확률 사출.</summary>
    public sealed class IcebergReloadSignatureDetector
    {
        private readonly long _reloadThresholdTicks;   // 재전개 허용 지연(이내면 빙산 서명)
        private readonly long _refillFractionPermille; // 재전개 복원 임계 ‰(dip 전 대비·기본 800=80%)
        private readonly long _freq;

        private long _lastQty = -1, _preDipQty, _dipQty, _dipTicks;
        private long _reloads, _observations, _lastReloadLatencyTicks;
        private int  _dipOpen;   // dip 진행 중(재전개 대기)

        public IcebergReloadSignatureDetector(long reloadThresholdMicros = 500, long refillFractionPermille = 800,
                                              Func<long>? nowTicks = null, long tickFrequency = 0)
        {
            // ★결정론: tickFrequency 미지정 시 호스트 Stopwatch.Frequency 대신 고정 기준(1e7 tick/s·QPC 관례)을 사용
            //   — 재전개 임계가 호스트 무관·리플레이 재현. 시각은 Observe 주입 ticks 만 사용(자체 시계 미참조).
            _ = nowTicks;   // 예약 파라미터(자체 시계 미사용) — 호출자 계약 유지용
            _freq = tickFrequency > 0 ? tickFrequency : 10_000_000L;
            _reloadThresholdTicks = _freq * Math.Max(1, reloadThresholdMicros) / 1_000_000L;
            _refillFractionPermille = Math.Clamp(refillFractionPermille, 1, 1000);
        }

        public long Reloads              => Interlocked.Read(ref _reloads);
        public long Observations         => Interlocked.Read(ref _observations);
        public long LastReloadLatencyNanos => LatNanos(Interlocked.Read(ref _lastReloadLatencyTicks));
        /// <summary>빙산 확정 확률(‰·δ=0). 재전개 서명 밀도 — reloads 가 관측 대비 잦을수록 높음. 0~1000.</summary>
        public long ConfirmationPermille
        {
            get { long o = Interlocked.Read(ref _observations), r = Interlocked.Read(ref _reloads); if (o < 4) return 0L; long p = r * 2000L / o; return p > 1000L ? 1000L : p; }
        }

        /// <summary>표시 잔량 1건 관측(δ=0·단일 라이터). dip(체결) → 임계 이내 refill 복원 = 빙산 reload 계수.</summary>
        public void Observe(long displayedQty, long ticks)
        {
            Interlocked.Increment(ref _observations);
            long last = _lastQty;
            if (last < 0) { _lastQty = displayedQty; return; }   // 첫 관측

            if (displayedQty < last)   // dip(체결로 잔량 감소) — 재전개 대기 시작
            {
                if (_dipOpen == 0) { _preDipQty = last; _dipTicks = ticks; _dipOpen = 1; }
                _dipQty = displayedQty;
            }
            else if (_dipOpen == 1 && displayedQty > _dipQty)   // 재전개(복원 시도)
            {
                long latency = ticks - _dipTicks;
                long threshold = _preDipQty * _refillFractionPermille / 1000L;
                if (latency >= 0 && latency <= _reloadThresholdTicks && displayedQty >= threshold)
                {
                    Interlocked.Increment(ref _reloads);   // ★임계 이내 원수준 복원 = 빙산 reload 서명
                    Volatile.Write(ref _lastReloadLatencyTicks, latency);
                    _dipOpen = 0;                          // 완전 복원 계수 시에만 에피소드 종료
                }
                else if (latency < 0 || latency > _reloadThresholdTicks)
                    _dipOpen = 0;                          // 창 만료(또는 시각 역행) 시 종료
                // ★임계 이내 '부분' 반등(threshold 미달)은 창을 닫지 않는다 — 이어지는 완전 복원을 계수하기 위함(false-negative 방지)
            }
            _lastQty = displayedQty;
        }

        /// <summary>검출기 상태 초기화(새 세션/심볼 전환·단일 라이터). 잔재 확정확률로 인한 유령 빙산 오탐 방지.</summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _reloads, 0);
            Interlocked.Exchange(ref _observations, 0);
            Volatile.Write(ref _lastReloadLatencyTicks, 0);
            _lastQty = -1; _preDipQty = 0; _dipQty = 0; _dipTicks = 0; _dipOpen = 0;
        }

        private long LatNanos(long ticks) => ticks <= 0 ? 0L : (long)(ticks * 1_000_000_000.0 / _freq);
    }
}
