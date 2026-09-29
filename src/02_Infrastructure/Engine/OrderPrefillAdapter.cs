// ============================================================================
// OrderPrefillAdapter — v12.6.0 IOrderPrefillPort 구현(Advisory-Only Order Prep Bridge)
//
//   종목코드/목표가/손절가 3개 숫자만 보관하는 락프리 상태 홀더. ★TryStage 가 유일한 쓰기 진입점이며
//   divergenceBlocked=true 를 받으면 상태를 건드리지 않고 즉시 false 반환(자동 차단 — 단일 초크포인트).
//   liveArmed·물리 키·발주 경로에 대한 참조가 전혀 없음(구조적 Advisory-Only) — MainDashboardAiFeedTests.cs
//   의 리플렉션 검증 대상.
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    public sealed class OrderPrefillAdapter : MS2E.UI.Portable.Ports.Shell.IOrderPrefillPort
    {
        private sealed record Snapshot(bool HasValue, string Symbol, decimal TargetPrice, decimal StopLossPrice)
            : MS2E.UI.Portable.Ports.Shell.IOrderPrefillSnapshot;

        private static readonly Snapshot Empty = new(false, string.Empty, 0m, 0m);

        private Snapshot _current = Empty;

        public MS2E.UI.Portable.Ports.Shell.IOrderPrefillSnapshot Current => Volatile.Read(ref _current);

        public event Action? OnChange;

        public bool TryStage(string symbol, decimal targetPrice, decimal stopLossPrice, bool divergenceBlocked)
        {
            if (divergenceBlocked) return false;   // ★자동 차단 — 상태 불변
            if (string.IsNullOrWhiteSpace(symbol)) return false;
            Volatile.Write(ref _current, new Snapshot(true, symbol, targetPrice, stopLossPrice));
            OnChange?.Invoke();
            return true;
        }

        public void Clear()
        {
            Volatile.Write(ref _current, Empty);
            OnChange?.Invoke();
        }
    }
}
