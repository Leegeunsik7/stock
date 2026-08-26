// ============================================================================
// UniversalBrokerGatewayMesh — v10.2.0: 범용 다중 증권사 통합 게이트웨이 메쉬
//
//   토스·KIS·LS 등 각 증권사 IPortfolioSyncPort 어댑터를 런타임 주입(DI)받아 단일 규격으로 관리한다. 등록은 startup
//   (콜드)→Seal→읽기 전용(δ=0 Get). 전 브로커 조회 동기화(SyncAll·콜드)를 오케스트레이션한다.
//
//   ★불변: liveArmed:false(★메쉬는 조회 전용 어댑터만 관리 — 발주 메서드 없음·RejectOrderAttempt 하드 veto·어떤
//     증권사든 실 발주 유발 0) · fail-closed(미등록 브로커 조회 → null) · δ=0(Get 핫패스 힙 0).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;

namespace MS2E.Infrastructure
{
    public sealed class UniversalBrokerGatewayMesh
    {
        private readonly Dictionary<BrokerId, IPortfolioSyncPort> _adapters = new();
        private long _sealed;
        private long _syncRuns, _orderVetoes;

        public int Count            => _adapters.Count;
        public bool IsSealed        => Volatile.Read(ref _sealed) == 1L;
        public long SyncRuns        => Interlocked.Read(ref _syncRuns);
        public long OrderVetoes     => Interlocked.Read(ref _orderVetoes);

        /// <summary>증권사 어댑터 등록(콜드·startup). Seal 후 거부(fail-closed).</summary>
        public void Register(BrokerId broker, IPortfolioSyncPort adapter)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (Volatile.Read(ref _sealed) == 1L)
                throw new InvalidOperationException($"UniversalBrokerGatewayMesh 봉인됨 — {broker} 등록 거부.");
            _adapters[broker] = adapter;
        }

        public void Seal() => Interlocked.Exchange(ref _sealed, 1L);

        public bool Contains(BrokerId broker) => _adapters.ContainsKey(broker);

        /// <summary>브로커 어댑터 조회(δ=0). 미등록 → null(fail-closed).</summary>
        public IPortfolioSyncPort? Get(BrokerId broker)
            => _adapters.TryGetValue(broker, out var a) ? a : null;

        /// <summary>전 브로커 조회 동기화(콜드). 반환=성공 브로커 수.</summary>
        public int SyncAll()
        {
            int ok = 0;
            try
            {
                foreach (var kv in _adapters)   // Dictionary.Enumerator = struct(힙 0)
                {
                    // ★브로커 격리: 한 증권사 네트워크 예외가 나머지 동기화를 중단시키지 않는다(집계 계약 사수).
                    try { if (kv.Value.SyncNow()) ok++; } catch { /* 해당 브로커만 실패 처리 */ }
                }
            }
            finally { Interlocked.Increment(ref _syncRuns); }
            return ok;
        }

        /// <summary>★메쉬 레벨 발주/변조 시도 하드 거부(원자적 veto·항상 false). 증권사 무관 read-only 봉인.</summary>
        public bool RejectOrderAttempt(BrokerId broker, long reasonCode)
        {
            Interlocked.Increment(ref _orderVetoes);
            return false;
        }
    }
}
