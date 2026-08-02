// ============================================================================
// OrderDrainWorker — v4.70.0: 발주 밸브 결속
//
// GatedOrderDispatcher.Drain() 을 50ms 주기 타이머로 구동해 IBroker.Place() 와
// 결속한다.  Dispatch.ValveWired=true 일 때만 타이머 가동(기본 false — 드라이런 박제).
//
// 안전 원칙:
//   ① broker.Place() ok=false → InvalidOperationException throw.
//      GatedOrderDispatcher Mission A 벽 → ForceFreeze(비상 동결) + 루프 break.
//   ② broker.Place() 예외 → 동일 경로(Mission A 흡수).
//   ③ 타이머 callback 외곽 try/catch — 소실 방지. 지속 오류는 SendErrors 누적.
//   ④ SymbolCode(long) → D6(KSE 6자리). name = symbol(링에 표시명 미포함).
//   ⑤ currentPrice = limitPrice — 브로커 내부 시장가 조회 계약.
//
// 이중 키 잠금:
//   Dispatch.ValveWired(이 클래스)  ∧  Dispatch.LiveArmingWired(armedPredicate)
//   둘 다 true 여야 실 주문 흐름.  어느 하나만 false 여도 실거래 0%.
// ============================================================================
using System;

namespace StockManager.Common
{
    public sealed class OrderDrainWorker : IDisposable
    {
        private readonly GatedOrderDispatcher        _dispatcher;
        private readonly IBroker                     _broker;
        private readonly OrderAttestationService?    _att;          // v4.87.0 fail-closed 단일 소스
        private readonly Func<bool>?                 _dualKeyIntact; // v4.87.0 이중키 무결성 런타임 검문(옵션)
        private readonly GenesisLiveIgnitionGateway? _gateway;      // v10.0.0 실물 사출 최종 빗장(옵션·null=기존 직접 경로)
        private readonly SealedSend?                 _placeSealed;  // v10.0.0 ★δ=0: PlaceSealed 델리게이트 1회 캐시(사출당 변환 할당 제거)
        private readonly MockExecutionOrderBookMirror? _mockMirror; // v10.1.0 드라이런 가상 체결 미러(옵션)
        private readonly ScopeSealedTransitionBarrier? _scopeBarrier; // v10.1.0 스코프-봉인 전환 장벽(옵션·라우팅 권위)
        private readonly System.Threading.Timer?     _timer;
        private volatile bool                        _disposed;
        private readonly CpuAffinityPinningGrid?      _pinGrid;   // v7.3.0 — 주입 시 전용 P-Core 핀 드레인 스레드 모드
        private readonly int                          _periodMs;
        private readonly System.Threading.Thread?     _thread;
        private readonly System.Threading.ManualResetEventSlim? _stop;
        private int                                   _disposedLatch;

        public bool IsWired { get; }

        /// <param name="attestation">v4.87.0 — 드레인 루프 예외·이중키 무결성 붕괴 시 ForceFreeze 격발(옵션·null 시 무동작).</param>
        /// <param name="dualKeyIntact">v4.87.0 — 매 드레인 전 이중키(ValveWired ∧ LiveArmingWired) 무결성 검문(옵션).
        /// false 반환 또는 throw 시 강등이 아니라 ForceFreeze 로 파이프라인 영구 락업.</param>
        /// <param name="pinGrid">v7.3.0 — 주입 시 드레인을 threadpool 타이머 대신 '전용 P-Core 핀 스레드'(엄격 순차·재진입
        /// 불가·핫패스 코어 격리)로 구동. null(기본)이면 기존 타이머 유지(동작 불변). wired:false 면 어느 쪽도 미가동.</param>
        public OrderDrainWorker(GatedOrderDispatcher dispatcher, IBroker broker,
                                bool wired, int periodMs = 50,
                                OrderAttestationService? attestation = null,
                                Func<bool>? dualKeyIntact = null,
                                CpuAffinityPinningGrid? pinGrid = null,
                                GenesisLiveIgnitionGateway? gateway = null,
                                MockExecutionOrderBookMirror? mockMirror = null,
                                ScopeSealedTransitionBarrier? scopeBarrier = null)
        {
            _dispatcher    = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _broker        = broker     ?? throw new ArgumentNullException(nameof(broker));
            _att           = attestation;
            _dualKeyIntact = dualKeyIntact;
            _gateway       = gateway;   // v10.0.0 — 주입 시 실물 사출을 최종 빗장(이중키∧방어선∧펄스∧봉인) 경유
            _placeSealed   = gateway != null ? PlaceSealed : null;   // ★δ=0: 델리게이트 1회 생성(핫패스 변환 할당 제거)
            _mockMirror    = mockMirror;    // v10.1.0 — 드라이런 스코프 시 실 브로커 대신 가상 미러로 봉인 루프백
            _scopeBarrier  = scopeBarrier;  // v10.1.0 — 드라이런/실전 라우팅 권위(전환 쿨다운·에폭 펜스)
            _pinGrid       = pinGrid;
            _periodMs      = periodMs < 1 ? 1 : periodMs;
            IsWired        = wired;
            if (wired)
            {
                // ★v7.3.0: pinGrid 주입 시 전용 핀 스레드(엄격 순차 — 타이머 재진입 위험 제거·P-Core 물리 격리). 미주입이면
                //   기존 threadpool 타이머(동작 불변). 밸브 미결선(wired:false·기본)이면 어느 경로도 미가동(드라이런 박제 유지).
                if (pinGrid != null)
                {
                    _stop   = new System.Threading.ManualResetEventSlim(false);
                    _thread = new System.Threading.Thread(PinnedDrainLoop)
                        { IsBackground = true, Name = "OrderDrainWorker", Priority = System.Threading.ThreadPriority.AboveNormal };
                    _thread.Start();
                }
                else
                    _timer = new System.Threading.Timer(_ => DrainOnce(), null, dueTime: 0, period: periodMs);
            }
        }

        // v7.3.0 — 전용 핀 드레인 루프: 현재 스레드를 P-Core 에 물리 핀 고정 후 엄격 순차 주기 드레인(타이머 재진입/풀 기아 배제).
        private void PinnedDrainLoop()
        {
            try { _pinGrid?.PinCurrentThread(); } catch { /* 핀 실패 무해 — 드레인은 계속(fail-safe) */ }
            var stop = _stop;
            while (!_disposed)
            {
                DrainOnce();
                if (stop == null || stop.Wait(_periodMs)) break;   // periodMs 대기 or dispose 신호 → 종료
            }
        }

        /// <summary>
        /// 1회 드레인 사이클(프로덕션은 50ms 타이머 구동·테스트는 직접 호출). ★fail-closed 단일 소스:
        /// 이중키 무결성 검문 실패/예외 또는 드레인 루프 예외 시 강등이 아니라 ForceFreeze 로 영구 락업.
        /// </summary>
        public void DrainOnce()
        {
            if (_disposed) return;   // Dispose 이후 in-flight 콜백 차단
            // ★이중키 무결성 검문(fail-closed): 무결성이 깨졌거나 검문 자체가 throw 하면 즉시 영구 락업.
            if (_dualKeyIntact is not null)
            {
                bool intact;
                try { intact = _dualKeyIntact(); }
                catch (Exception ex) { Freeze("이중키 무결성 검문 예외 — 영구 락업: " + ex.Message); return; }
                if (!intact) { Freeze("이중키(ValveWired ∧ LiveArmingWired) 무결성 붕괴 — 영구 락업"); return; }
            }
            // ★드레인 루프 예외 = 강등 아님. 즉시 ForceFreeze 단일 소스로 파이프라인 영구 락업(모호한 지속 금지).
            try   { _dispatcher.Drain(Dispatch); }
            catch (Exception ex) { Freeze("드레인 루프 예외 — 영구 락업: " + ex.Message); }
        }

        private void Freeze(string reason)
        {
            try { _att?.ForceFreeze("OrderDrainWorker " + reason); } catch { /* 동결 실패해도 진행 오염 금지 */ }
        }

        // 반환: true=실 송출(Forwarded), false=하류 veto(미송출·GatedOut). throw=Mission-A 벽(ForceFreeze).
        private bool Dispatch(OrderProposal p)
        {
            // KSE 6자리 범위를 이탈한 코드는 조용히 건너뜀(상류 버그) — 기존 계상 보존(true).
            if (p.SymbolCode < 1 || p.SymbolCode > 999_999) return true;

            // ★v10.1.0 스코프 라우팅: barrier 가 드라이런/실전을 원자 결정. 드라이런 스코프면 실 브로커 대신 가상 미러로
            //   봉인 루프백(broker 참조 0). 전환 쿨다운 중이면 Bounce(미사출·유령 폭주 차단). 실전 스코프면 아래 게이트웨이 경로.
            if (_scopeBarrier is { } barrier)
            {
                var route = barrier.RouteNow();
                if (route == ScopeSealedTransitionBarrier.Route.Bounce) return false;   // 전환 쿨다운 — 미사출(GatedOut)
                if (route == ScopeSealedTransitionBarrier.Route.DryRunMirror)
                    return _mockMirror?.SealAndVirtualFill(p.OrderId, p.SymbolCode, p.SideByte, p.KindByte, p.PriceRaw, p.QtyRaw) ?? false;
                // route == LiveBroker → 아래 실전 경로(게이트웨이·물리 키)
            }

            // ★v10.0.0 최종 빗장: 게이트웨이 주입 시 실물 사출을 이중키∧방어선∧펄스 재검 + 단발 개통 + 봉인 사출
            //   (exactly-once·torn 방어) 경유. veto 시 false(미사출·fail-safe·Dispatcher 는 GatedOut). 미주입(기본)이면 직접 경로(동작 불변·true).
            if (_gateway is { } gw)
                return gw.Emit(p.OrderId, p.SymbolCode, p.SideByte, p.KindByte, p.PriceRaw, p.QtyRaw, _placeSealed!);

            var symbol     = p.SymbolCode.ToString("D6");
            var side       = p.Side == OrderSide.Buy ? PaperSide.Buy : PaperSide.Sell;
            var kind       = p.KindByte == 0 ? PaperKind.Market : PaperKind.Limit;
            var qty        = p.Qty.ToDecimal();
            var limitPrice = p.Price.ToDecimal();

            var (ok, msg) = _broker.Place(
                PaperAsset.Stock, symbol, symbol,
                side, kind, qty, limitPrice, limitPrice);

            if (!ok)
                throw new InvalidOperationException($"Broker.Place 거부: {msg}");
            return true;
        }

        // ★v10.0.0 봉인 사출: 게이트웨이가 봉인한 불변 토큰만 소켓으로. 거부 시 throw → 상류 Mission-A 벽(ForceFreeze).
        private void PlaceSealed(in SealedOrderToken t)
        {
            var symbol     = t.SymbolCode.ToString("D6");
            var side       = t.SideByte == 0 ? PaperSide.Buy : PaperSide.Sell;
            var kind       = t.KindByte == 0 ? PaperKind.Market : PaperKind.Limit;
            var qty        = new QQty(t.QtyRaw).ToDecimal();
            var limitPrice = new QPrice(t.PriceRaw).ToDecimal();

            var (ok, msg) = _broker.Place(
                PaperAsset.Stock, symbol, symbol,
                side, kind, qty, limitPrice, limitPrice);

            if (!ok)
                throw new InvalidOperationException($"Broker.Place 거부: {msg}");
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposedLatch, 1) == 1) return;   // 멱등
            _disposed = true;
            try { _timer?.Dispose(); } catch { }
            _stop?.Set();
            // ★핀 스레드 종료 '입증' 후에만 _stop dispose — 살아있는 Wait() 위 dispose 시 ObjectDisposedException 방지(안전 누수 허용).
            bool exited;
            try { exited = _thread == null || _thread.Join(2000); } catch { exited = false; }
            if (exited) { try { _stop?.Dispose(); } catch { } }
        }
    }
}
