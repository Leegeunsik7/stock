// ============================================================================
// ScopeSealedTransitionBarrier — v10.1.0 ★Think Beyond Gemini: 스코프-봉인 전환 장벽
//   "드라이런 ↔ 실전 무장 천이의 찰나 — 가상 잔량 오염도, 유령 체결 폭주도, 스코프 오식별도 '단일 펜스'로 봉쇄."
//
//  ── 왜 제미나이의 A/B 를 초월하는가 ────────────────────────────────────────
//  모의-실전 천이의 두 파멸적 리스크:
//    · 가상 잔량의 실전 계좌 오염(Mock-to-Live State Residual Leakage) — 드라이런 미체결/잔량이 실전 전환 후 잔존.
//    · 유령 체결 폭주(Phantom Execution Spikes) — 드라이런 미체결 주문이 실전 전환 시 한꺼번에 실물 사출.
//  제미나이는 이를 각각 막자 제안했다(A: 상태 퍼지+쿨다운, B: 스코프 식별자 비트마스크). 그러나 이 둘은 하나의
//  불변식의 세 얼굴이다 — **스코프-에폭 펜스**: 전환 시 단조 에폭을 원자 전진시키면 ①이전 스코프에서 사출된 주문은
//  스테일 에폭으로 즉시 튕겨나가고(=스코프 식별·B) ②전진과 동시에 가상 상태를 O(1) 제로화하며(=잔량 퍼지·A) ③에폭
//  전진 직후 100ms 쿨다운 동안 어떤 사출도 Bounce(=유령 폭주 차단·A). 즉 A(퍼지+쿨다운)와 B(스코프 식별)를
//  각각이 아니라 '스코프-에폭' 단일 원자 연산으로 통합·초월. RouteNow 가 사출을 미러/브로커/Bounce 로 O(1) 물리 라우팅.
//
//  ★불변: δ=0(RouteNow/StampScope/VerifyScope/Transition 힙 0·Interlocked/Volatile) · 결정론(주입 시계·순수) ·
//    liveArmed:false(드라이런 스코프는 미러로만·실전 스코프만 브로커·전환 쿨다운 중 Bounce — 크로스 사출 물리 불가) ·
//    fail-closed(스테일 에폭·쿨다운·미상 스코프 → Bounce). ★자체시계는 Stopwatch.GetTimestamp 매칭(freq=Stopwatch.Frequency).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>스코프-봉인 전환 장벽(δ=0). Transition(에폭 전진+퍼지+쿨다운)·RouteNow(미러/브로커/Bounce O(1) 라우팅).</summary>
    public sealed class ScopeSealedTransitionBarrier
    {
        public const long MODE_DRYRUN = 0L;   // 드라이런(가상 미러)
        public const long MODE_LIVE   = 1L;   // 실전(브로커·물리 키)
        public enum Route { Bounce = 0, DryRunMirror = 1, LiveBroker = 2 }

        private readonly Func<long> _nowTicks;
        private readonly long _cooldownTicks;
        private readonly Action? _onPurge;    // 가상 상태 O(1) 제로화(주입·미러 Reset)
        private long _mode = MODE_DRYRUN;
        private long _scopeEpoch = 1L;        // 단조 스코프 에폭(스테일 펜스·1-based)
        private long _transitionEndTicks;     // 쿨다운 종료 시각
        private long _transitions, _scopeBounces, _purges, _staleRejects;

        public ScopeSealedTransitionBarrier(Action? onPurge = null, long cooldownMillis = 100,
                                            Func<long>? nowTicks = null, long tickFrequency = 0)
        {
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            // ★자체시계(Stopwatch.GetTimestamp) 소스와 매칭: freq 폴백 = Stopwatch.Frequency(고정 1e7 아님·비-1e7 QPC 호스트 안전).
            long freq = tickFrequency > 0 ? tickFrequency : Stopwatch.Frequency;
            _cooldownTicks = freq * Math.Max(1L, cooldownMillis) / 1000L;
            _onPurge = onPurge;
        }

        // ── 읽기(δ=0·락프리) ──
        public long Mode          => Interlocked.Read(ref _mode);
        public long ScopeEpoch    => Interlocked.Read(ref _scopeEpoch);
        public long Transitions   => Interlocked.Read(ref _transitions);
        public long ScopeBounces  => Interlocked.Read(ref _scopeBounces);
        public long Purges        => Interlocked.Read(ref _purges);
        public long StaleRejects  => Interlocked.Read(ref _staleRejects);
        public bool DryRunActive  => Interlocked.Read(ref _mode) == MODE_DRYRUN;
        /// <summary>★blockWhile — 전환 쿨다운 중 무장/사출 차단(유령 체결 폭주 방지·tighten-only).</summary>
        public bool TransitionBlocking => _nowTicks() < Volatile.Read(ref _transitionEndTicks);

        /// <summary>
        /// ★스코프 전환(δ=0·원자·자체시계): ①에폭 전진(스테일 주문 펜스=스코프 식별) ②쿨다운 펜스 개시(먼저 — 신규 미러 체결
        /// 즉시 Bounce) ③모드 원자 전환 ④누적 가상 잔량 O(1) 제로화(펜스 뒤 — 신규 유입 차단 후 정리). ★자체 _nowTicks() 사용
        /// (외부 ticks 미수령 — RouteNow/TransitionBlocking/VerifyScope 와 동일 시계 도메인 강제·크로스도메인 footgun 물리 봉쇄).
        /// ※전환 찰나 이미 RouteNow 통과해 미러 체결 진행 중이던 in-flight 1건은 퍼지 후 잔량을 재적재할 수 있으나 순수 가상 수치
        ///   (무자본 영향·liveArmed:false 불변)이며 쿨다운이 신규 미러 체결을 Bounce 로 차단하므로 차기 사이클에 정리된다.
        /// </summary>
        public void Transition(long toMode)
        {
            long now = _nowTicks();                                             // ★자체시계 — RouteNow/Blocking 과 동일 도메인
            Interlocked.Increment(ref _scopeEpoch);                            // ① 에폭 전진(이전 스코프 주문 스테일化)
            Volatile.Write(ref _transitionEndTicks, now + _cooldownTicks);     // ② 쿨다운 펜스(먼저 발행 — 신규 미러 체결 즉시 Bounce)
            Interlocked.Exchange(ref _mode, toMode == MODE_LIVE ? MODE_LIVE : MODE_DRYRUN);   // ③ 모드 발행
            try { _onPurge?.Invoke(); } catch { /* 퍼지 실패 무해 — 쿨다운·모드는 이미 발행됨 */ }   // ④ 가상 상태 제로화(펜스 뒤)
            Interlocked.Increment(ref _purges);
            Interlocked.Increment(ref _transitions);
        }

        /// <summary>초기 모드 설정(전환 아님·쿨다운/퍼지 없음·부팅 시 1회). 이후 변경은 Transition 사용.</summary>
        public void SetInitialMode(long mode) => Interlocked.Exchange(ref _mode, mode == MODE_LIVE ? MODE_LIVE : MODE_DRYRUN);

        /// <summary>현재 사출 라우팅(δ=0·O(1)). 쿨다운 중=Bounce, 아니면 현재 모드로. 드라이런=미러·실전=브로커.</summary>
        public Route RouteNow()
        {
            if (_nowTicks() < Volatile.Read(ref _transitionEndTicks))
            { Interlocked.Increment(ref _scopeBounces); return Route.Bounce; }   // 전환 쿨다운 — 유령 폭주 차단
            return Interlocked.Read(ref _mode) == MODE_LIVE ? Route.LiveBroker : Route.DryRunMirror;
        }

        /// <summary>주문에 현재 스코프 에폭 각인(δ=0). 사출 생성 시점 스코프 포획.</summary>
        public long StampScope() => Interlocked.Read(ref _scopeEpoch);

        /// <summary>
        /// ★스코프 검증(δ=0·O(1)). 사출 직전, 주문 각인 에폭이 현재 에폭과 일치하고 쿨다운이 아니어야 통과.
        /// 스테일 에폭(전환 이전 스코프 주문) 또는 쿨다운 → false(물리 튕김·크로스 스코프 사출 봉쇄).
        /// </summary>
        public bool VerifyScope(long stampedEpoch)
        {
            if (_nowTicks() < Volatile.Read(ref _transitionEndTicks)) { Interlocked.Increment(ref _scopeBounces); return false; }
            if (stampedEpoch != Interlocked.Read(ref _scopeEpoch)) { Interlocked.Increment(ref _staleRejects); return false; }
            return true;
        }
    }
}
