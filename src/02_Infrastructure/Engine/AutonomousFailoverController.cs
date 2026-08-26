// ============================================================================
// AutonomousFailoverController — v5.1.0 과제2: 무정지 자율 채널 페일오버
//
//  특정 브로커 채널(키움 x86 프록시)에 물리 장애가 발생한 찰나, 주문 링버퍼에 대기 중이던 비가역 주문을 즉각 살아있는
//  대안 채널(토스 프로덕션)로 자율 라우팅한다. ★이중 발주/누수 0을 exactly-once 로 보장:
//   · 1차 보증(구조적): 주문 링은 단조 OrderId·단일 드레인 커서 → 각 주문은 정확히 1회 소비돼 '그 시점 활성 채널'로만 송출.
//     따라서 채널 플립은 미래/대기 주문의 목적지만 바꿀 뿐 이미 소비된 주문을 재송출하지 않는다(재발주 원천 부재).
//   · 2차 보증(백스톱): TryClaimDispatch(OrderId) 락-프리 CAS seen-ring — 어떤 버그로 재드레인이 시도돼도 동일 OrderId
//     2회차 클레임은 거부(dedup). MmfFlightRecorder 에 재라우팅 사건 기록(포렌식·txn 대조).
//
//  ── 정직 회계(정찰) ─────────────────────────────────────────────────────────
//  MultiBrokerRouter(_failoverFired CAS·primary→fallback·ProxyLost 배선)가 가장 근접한 원형이나 단방향·1회·복구 없음.
//  본 컨트롤러는 그 CAS 관용구를 계승하되 ★양방향 복구(FailedOver↔Primary)와 exactly-once dedup·양채널 동시 장애
//  fail-closed 를 추가(신규 델타). 실 라우팅·송출은 하류 ZeroLeakProtocolIsolator/ShadowExecutionGateway 펜스 지배 —
//  이중키∧MasterIgnited 없이는 어느 채널로도 0바이트(liveArmed:false).
//
//  δ=0: 채널 해석·클레임 = Volatile/Interlocked/CAS(힙 0). fail-closed: 양채널 다운 → ForceFreeze. 결정론(상태 전이 원자).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public enum ActiveChannel : byte { Primary = 0, Standby = 1, None = 2 }

    /// <summary>채널 물리 장애 시 대안 채널로 자율 라우팅 + exactly-once dedup + 양방향 복구. 실 송출은 하류 펜스 지배.</summary>
    public sealed class AutonomousFailoverController
    {
        private readonly OrderAttestationService? _att;
        private readonly MmfFlightRecorder?       _blackbox;

        // exactly-once 백스톱 — 클레임된 OrderId seen-ring(2의 거듭제곱). slot 충돌은 최근창 축출(단조 커서가 1차 보증).
        private readonly long[] _seen;
        private readonly long   _seenMask;

        private volatile bool _primaryUp = true, _standbyUp = true;
        private int  _active = (int)ActiveChannel.Primary;   // Volatile.Read/Write(ref)
        private long _failovers, _recoveries, _dedupBlocks, _claims;

        public AutonomousFailoverController(OrderAttestationService? attestation = null, int seenCapacity = 8192, MmfFlightRecorder? blackbox = null)
        {
            _att = attestation;
            int cap = 2; while (cap < seenCapacity) cap <<= 1;   // 2의 거듭제곱 올림
            _seen = new long[cap];
            for (int i = 0; i < cap; i++) _seen[i] = -1L;         // -1 = 빈 슬롯
            _seenMask = cap - 1;
            _blackbox = blackbox;
        }

        public ActiveChannel Active => (ActiveChannel)Volatile.Read(ref _active);
        public bool PrimaryUp       => _primaryUp;
        public bool StandbyUp       => _standbyUp;
        public long Failovers       => Interlocked.Read(ref _failovers);
        public long Recoveries      => Interlocked.Read(ref _recoveries);
        public long DedupBlocks     => Interlocked.Read(ref _dedupBlocks);
        public long Claims          => Interlocked.Read(ref _claims);

        /// <summary>채널 생존성 갱신. Primary 다운 시 Standby 로 자율 페일오버, 양채널 다운 시 fail-closed 동결.</summary>
        public void ReportChannel(ActiveChannel channel, bool up)
        {
            if (channel == ActiveChannel.Primary) _primaryUp = up;
            else if (channel == ActiveChannel.Standby) _standbyUp = up;
            Reconcile();
        }

        private void Reconcile()
        {
            var cur = (ActiveChannel)Volatile.Read(ref _active);
            // 양채널 다운 → 라우팅 불가 → fail-closed 동결(자율 실행 위험 원천 차단).
            if (!_primaryUp && !_standbyUp)
            {
                Volatile.Write(ref _active, (int)ActiveChannel.None);
                try { _att?.ForceFreeze("AutonomousFailover: 양 채널(Primary∧Standby) 동시 물리 장애 — fail-closed"); } catch { }
                return;
            }
            // Primary 우선 복구 지향(무정지 복구): Primary 살아있으면 Primary, 아니면 Standby.
            ActiveChannel target = _primaryUp ? ActiveChannel.Primary : ActiveChannel.Standby;
            if (target == cur) return;
            Volatile.Write(ref _active, (int)target);
            if (target == ActiveChannel.Standby)
            {
                Interlocked.Increment(ref _failovers);
                try { _blackbox?.Record(FlightEventKind.ChannelFailover, symbolCode: 0L, arg0: 1L, arg1: 0L, arg2: 0L, sourceCode: 61); } catch { }
            }
            else if (cur == ActiveChannel.Standby)   // Standby→Primary 복귀 = 무정지 복구
            {
                Interlocked.Increment(ref _recoveries);
                try { _blackbox?.Record(FlightEventKind.ChannelFailover, symbolCode: 0L, arg0: 0L, arg1: 1L, arg2: 0L, sourceCode: 61); } catch { }
            }
        }

        /// <summary>현재 활성 채널(라우팅 목적지). None = 양채널 다운(동결). 락-프리.</summary>
        public ActiveChannel ResolveChannel() => (ActiveChannel)Volatile.Read(ref _active);

        /// <summary>
        /// ★exactly-once 백스톱(δ=0·락-프리 CAS) — 이 OrderId 최초 디스패치면 true(클레임), 이미 클레임된 중복이면 false(dedup 차단).
        /// slot=OrderId&amp;mask, CAS(-1→id) 또는 재확인. 충돌 시 최근창 축출(단조 커서가 1차 보증이라 실무 안전).
        /// </summary>
        public bool TryClaimDispatch(long orderId)
        {
            if (orderId < 0) return false;
            int slot = (int)(orderId & _seenMask);
            var spin = new SpinWait();
            // ★재시도 루프 — CAS 실패 시 단일 recheck 로 fall-through 하지 않고 재시도해, 우리 id 기록 성공(claim=true) 또는
            //   슬롯에 우리 id 발견(dedup=false) 중 하나로만 종료. 다른 id 충돌로 미기록된 채 true 반환하던 false-negative 봉쇄.
            while (true)
            {
                long existing = Volatile.Read(ref _seen[slot]);
                if (existing == orderId) { Interlocked.Increment(ref _dedupBlocks); return false; }   // 이미 클레임됨 — 중복 차단
                if (Interlocked.CompareExchange(ref _seen[slot], orderId, existing) == existing)
                { Interlocked.Increment(ref _claims); return true; }                                   // 우리가 원자 기록 성공
                spin.SpinOnce();   // 경합(다른 스레드가 슬롯 변경) — 재판독 후 재시도
            }
        }
    }
}
