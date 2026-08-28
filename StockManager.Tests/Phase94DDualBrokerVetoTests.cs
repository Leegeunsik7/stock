// ============================================================================
// Phase94DDualBrokerVetoTests — v11.3.0 이중 브로커 관제 liveArmed:false 베토 & fail-closed 지상 증명
//   ★명령 요구: "이중 실통신 조회/관제가 작동하더라도 실제 매수/매도 주문 패킷은 100% Veto".
//     ① 계약(컴파일+리플렉션): IDualBrokerStatusPort 에는 발주/변조 메서드가 0개 — 조회 전용 표면.
//     ② 어댑터: LiveArmed 항상 false · 서비스 미배선(빈 SP) → 전 라인 Offline·계좌 HasData=false(가짜 잔고 0).
// ============================================================================
using System;
using System.Reflection;
using MS2E.UI.Portable.Ports.Brokers;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Phase94DDualBrokerVetoTests
    {
        private sealed class NullServiceProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;   // 모든 서비스 미배선
        }

        // ══ ① 계약: 발주/변조 메서드 0(read-only 표면) ════════════════════════
        [Fact]
        public void Port_Surface_Has_No_Order_Or_Mutation_Methods()
        {
            var t = typeof(IDualBrokerStatusPort);
            string[] forbidden = { "buy", "sell", "place", "order", "send", "submit", "execute", "cancel", "arm", "trade", "dca", "write", "set" };
            foreach (var m in t.GetMethods())
            {
                if (m.IsSpecialName) continue;   // 프로퍼티 접근자(get_LiveArmed 등) 제외 — 실 액션 메서드만 검사
                var name = m.Name.ToLowerInvariant();
                foreach (var f in forbidden)
                    Assert.False(name.Contains(f), $"이중 관제 Port 메서드 '{m.Name}' 가 발주/변조 토큰 '{f}' 포함 — read-only 위반");
            }
            // 기대되는 조회 메서드 + LiveArmed 존재
            Assert.NotNull(t.GetMethod("GetLineStatus"));
            Assert.NotNull(t.GetMethod("GetAccounts"));
            Assert.NotNull(t.GetMethod("GetHoldings"));
            Assert.NotNull(t.GetProperty("LiveArmed"));
        }

        // ══ ② 어댑터: LiveArmed=false · 미배선 fail-closed(가짜 데이터 0) ═══════
        [Fact]
        public void Adapter_LiveArmed_False_And_FailClosed_When_Unwired()
        {
            var a = new MS2E.Infrastructure.DualBrokerStatusAdapter(new NullServiceProvider());

            Assert.False(a.LiveArmed);   // ★발주 무장 절대 없음

            var ls = new BrokerLineStatus[2];   // 관리형 DTO(string) → 배열(stackalloc 불가)
            int n = a.GetLineStatus(ls);
            Assert.Equal(2, n);
            Assert.Equal(BrokerLine.Kiwoom, ls[0].Line);
            Assert.Equal(BrokerLine.Toss, ls[1].Line);
            Assert.Equal(LineHealth.Offline, ls[0].Health);   // 키움 미배선 → Offline
            Assert.Equal(LineHealth.Offline, ls[1].Health);   // 토스 미배선 → Offline
            Assert.False(ls[0].Connected);
            Assert.False(ls[1].Connected);

            var acc = new BrokerAccountLine[2];
            int m = a.GetAccounts(acc);
            Assert.Equal(2, m);
            Assert.False(acc[0].HasData);   // ★가짜 잔고 미생성(fail-closed)
            Assert.False(acc[1].HasData);
            Assert.Equal(0m, acc[0].TotalEval);
            Assert.Equal(0m, acc[1].TotalEval);

            var hold = new BrokerHoldingLine[16];
            Assert.Equal(0, a.GetHoldings(BrokerLine.Toss, hold));    // 미동기화 → 0
            Assert.Equal(0, a.GetHoldings(BrokerLine.Kiwoom, hold));  // 키움 계좌 포트 없음 → 0
        }
    }
}
