// ============================================================================
// OperatorDuressPanicSwitch — v10.1.0 실전 운용: 운용자 비상 탈출(패닉 스위치) 최후 메쉬
//
//  운용자 판단 지연/패닉 상황에서 '골든타임 상실(Inaction Panic Trapping)'을 격파하는 최후의 비상 탈출. 전용 핫키/패닉
//  시퀀스(운용자·UI 배선)가 Trip() 을 호출하면 O(1)로: ①ForceFreeze(전 발주 비가역 차단·Mission-A 벽) ②onPanic 훅
//  (Cancel-All + 시크릿 0x00 Zeroization 등 상위 배선) ③감사 각인 ④비가역 래치(재무장 불가). 판단이 늦어도 '한 동작'으로
//  전량 정지에 도달한다.
//
//  ★Trip 경로는 O(1)·상수 문자열(concat 무·인터닝)·콜백만 — 초고속. 상태 조회(Tripped/PanicBlocking)는 δ=0(Interlocked.Read).
//    핫키 포획 자체는 UI/OS 관심사(OPERATIONAL_RUNBOOK 문서)이며, 본 모듈은 O(1) 트립 기구를 제공·안전 배선한다.
//
//  ★불변: liveArmed:false(순수 정지 지향 — 실 브로커 사출 유발 0·onPanic 은 '취소/소멸'만·PanicBlocking tighten-only) ·
//    fail-closed(트립 = 전 발주 비가역 차단) · 결정론(주입 시계). δ=0 상태 조회.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>운용자 비상 탈출 패닉 스위치. Trip()=O(1) ForceFreeze+훅+감사+비가역 래치. 상태 조회 δ=0.</summary>
    public sealed class OperatorDuressPanicSwitch
    {
        private const string PanicReason = "운용자 비상 탈출(패닉 스위치) — 전 발주 비가역 차단";

        private readonly OrderAttestationService? _attestation;   // 전 발주 차단(Mission-A 벽)
        private readonly Action? _onPanic;                        // Cancel-All + 시크릿 zeroize 등 상위 훅
        private readonly IgnitionAuditTrailRecorder? _audit;
        private readonly Func<long> _nowTicks;
        private long _tripped, _tripTicks, _trips;

        public OperatorDuressPanicSwitch(OrderAttestationService? attestation = null, Action? onPanic = null,
                                         IgnitionAuditTrailRecorder? audit = null, Func<long>? nowTicks = null)
        {
            _attestation = attestation;
            _onPanic = onPanic;
            _audit = audit;
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
        }

        // ── 읽기(δ=0·락프리) ──
        public bool Tripped        => Interlocked.Read(ref _tripped) == 1L;
        public long TripTicks      => Interlocked.Read(ref _tripTicks);
        public long Trips          => Interlocked.Read(ref _trips);
        /// <summary>★blockWhile — 패닉 격발 시 무장 차단(tighten-only·비가역).</summary>
        public bool PanicBlocking  => Interlocked.Read(ref _tripped) == 1L;

        /// <summary>
        /// ★비상 탈출 격발(O(1)·비가역). ①ForceFreeze(전 발주 차단) ②onPanic(Cancel-All + 시크릿 zeroize 훅) ③감사 각인
        /// ④래치. 이미 격발 시 무동작(멱등). reasonCode 는 격발 원인(핫키/시퀀스/원격)을 포렌식 기록.
        /// </summary>
        public void Trip(long reasonCode = 0L)
        {
            if (Interlocked.CompareExchange(ref _tripped, 1L, 0L) != 0L) return;   // 멱등(1회 래치)
            Volatile.Write(ref _tripTicks, _nowTicks());
            Interlocked.Increment(ref _trips);
            try { _attestation?.ForceFreeze(PanicReason); } catch { /* 동결 실패해도 진행 오염 금지 */ }
            try { _onPanic?.Invoke(); } catch { /* Cancel-All/zeroize 훅 실패 무해 — 래치·동결은 유지 */ }
            _audit?.Record(IgnitionEventKind.PanicAbort, Volatile.Read(ref _tripTicks), reasonCode);
        }
    }
}
