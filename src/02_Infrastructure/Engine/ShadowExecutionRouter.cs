// ============================================================================
// ShadowExecutionRouter — S20 #4: 드라이런 종단 조립체 (Closed-Loop 결속)
//
//  Season 19~20의 검증된 부품들을 하나의 종단 파이프라인으로 결속한다:
//    StrategySlot(활성 전략) → AlgoExecutionGate(3중 강철 가드) → MultiAccountMesh(무손실 분배)
//
//  동작: 주문 의도를 안전 게이트에 통과(동결·권한·디싱크·크기 클램프·드라이런)시키고,
//  승인된 수량을 가상 서브계좌 지분율대로 무손실 분배해 계좌별 (가상) 체결 명세를 반환한다.
//
//  ── 안전 계승 (라이브 스위치 0) ────────────────────────────────────────────
//  · 게이트가 거부(동결/디싱크/권한/0)하면 분배 없이 그대로 반환 — 부품 단위 안전 보존.
//  · 라이브 발주 여부는 전적으로 AlgoExecutionGate.IsDryRun 에 위임(기본 드라이런 박제).
//  · 라우터는 무상태 순수 조립 — 게이트의 가상 원장이 집계를, 라우터가 계좌별 분배를 담당.
//
//  외부 의존성 0 · 사이드 이펙트는 게이트에 위임 · 분배는 MultiAccountMesh 무손실 보장.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    /// <summary>가상 서브계좌 1건의 (가상) 체결 분배.</summary>
    public sealed record SubAccountFill(string SubAccountId, long Qty, decimal Price);

    /// <summary>라우팅 결과 — 게이트 판정 + 승인 수량 + 계좌별 분배.</summary>
    public sealed record RoutedExecution(
        ExecOutcome                  Outcome,
        decimal                      ApprovedQty,
        IReadOnlyList<SubAccountFill> Fills,
        string                       Detail);

    public sealed class ShadowExecutionRouter
    {
        private readonly AlgoExecutionGate _gate;
        private readonly MultiAccountMesh  _mesh;

        public ShadowExecutionRouter(AlgoExecutionGate gate, MultiAccountMesh mesh)
        {
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
        }

        /// <summary>
        /// 주문 의도를 3중 안전 가드에 통과시키고, 승인 수량을 가상 서브계좌로 무손실 분배한다.
        /// 게이트가 거부하거나 승인 수량이 0이면 분배 없이 반환.
        /// </summary>
        public RoutedExecution Route(
            OrderIntent intent, LocalState local, BrokerState broker, decimal totalAssetValue)
        {
            var v = _gate.Submit(intent, local, broker, totalAssetValue);

            bool approved = v.Outcome is ExecOutcome.DryRunLogged or ExecOutcome.Executed;
            if (!approved || v.ApprovedQty <= 0m)
                return new RoutedExecution(v.Outcome, v.ApprovedQty, Array.Empty<SubAccountFill>(), v.Detail);

            // 승인된(클램프된) 정수 수량을 지분율대로 무손실 분배
            var split = _mesh.Apportion((long)v.ApprovedQty);
            var fills = split
                .Where(kv => kv.Value > 0L)
                .Select(kv => new SubAccountFill(kv.Key, kv.Value, intent.Price))
                .ToList();

            return new RoutedExecution(v.Outcome, v.ApprovedQty, fills, v.Detail);
        }
    }
}
