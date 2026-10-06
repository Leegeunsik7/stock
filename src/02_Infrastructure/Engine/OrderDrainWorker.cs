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

namespace MS2E.Infrastructure
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
        private readonly LatencySlaGatekeeper? _slaGate;           // v10.1.0 Final — 드레인 사이클 지연 나노 SLA 계측(옵션·δ=0 Record)
        private readonly SlaJitterRegressionGuard? _jitterGuard;   // v10.1.0 Final — ★Think-Beyond 측정-무결성(디스케줄 배제·옵션)
        private readonly MicroCapitalCanaryGovernor? _canaryGov;   // v10.1.0 Ops — 실전 사출 직전 카나리 캡(자본 ≤0.1%·LiveBroker 경로 전용·옵션)
        private readonly MS2E.Core.ArmingInterlockBitmask? _interlocks;   // [실배선 · 방향 B] 송출 직전 원자 인터락 최종 veto(옵션·O(1) δ=0)
        private long _interlockVetoes;
        private readonly IOrderRouteGate? _routeGate;   // [피드 연동] 참조가·추적 슬롯 라우터 게이트(옵션 · 실전 경로 전용)
        private long _routeVetoes;
        private readonly ITickToTradeProbe? _latencyProbe;
        // [다중 브로커] 라우터 브로커 인덱스 → IBroker. 수정 전: 라우터가 브로커를 골라도 송출은 항상 단일 _broker(인덱스 무시 —
        //   스위치보드 페일오버가 실제 송출 대상을 바꾸지 못했다). 미주입/범위 밖/null 슬롯 = 송출 거부(GatedOut · fail-closed).
        private readonly IBroker?[]? _brokersByIndex;
        private IBroker _sendBroker;   // 드레인 단일 스레드 — 봉인 사출(PlaceSealed) 경로로 선택 브로커 전달
        private long _brokerIndexVetoes;
        public long BrokerIndexVetoes => System.Threading.Interlocked.Read(ref _brokerIndexVetoes);   // [저지연] 주문별 Tick-to-Trade 계측 + 송출 직전 지연 폭주 veto(옵션)
        private long _latencyVetoes;
        // [실배선 δ=0 교정] Drain(Dispatch) 메서드 그룹 변환이 드레인 사이클마다 Func 델리게이트(64B)를 새로 할당했다
        //   (50ms 주기 = 초당 20회 상시 GC 압력 · ProductionPipelineWiringTests 실측). ctor 에서 1회 캐시.
        private readonly Func<OrderProposal, bool> _dispatchFn;
        private static readonly long SlaTickFreq = System.Diagnostics.Stopwatch.Frequency;   // 틱→ns 변환(호스트 QPC 주파수·1회 캐시)
        private readonly System.Threading.Timer?     _timer;
        private volatile bool                        _disposed;
        private readonly CpuAffinityPinningGrid?      _pinGrid;   // v7.3.0 — 주입 시 전용 P-Core 핀 드레인 스레드 모드
        private readonly int                          _periodMs;
        private readonly System.Threading.Thread?     _thread;
        private readonly System.Threading.ManualResetEventSlim? _stop;
        private int                                   _disposedLatch;

        public bool IsWired { get; }
        /// <summary>[실배선] 디스패처 무장 판정 이후 ~ 실 송출 직전 사이에 인터락이 Trip 해 거부된 누계(판정↔송출 TOCTOU 봉합 실측).</summary>
        public long InterlockVetoes => System.Threading.Interlocked.Read(ref _interlockVetoes);
        /// <summary>[피드 연동] 라우터 게이트(참조가 만료·피드 단절·슬리피지·추적 불가) 거부 누계.</summary>
        public long RouteVetoes => System.Threading.Interlocked.Read(ref _routeVetoes);
        /// <summary>[저지연] 디스패치 진입 → 송출 직전 구간이 정지 임계를 넘어 미송출된 누계(GC 정지·스레드 선점).</summary>
        public long LatencyVetoes => System.Threading.Interlocked.Read(ref _latencyVetoes);

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
                                ScopeSealedTransitionBarrier? scopeBarrier = null,
                                LatencySlaGatekeeper? slaGate = null,
                                SlaJitterRegressionGuard? jitterGuard = null,
                                MicroCapitalCanaryGovernor? canaryGov = null,
                                MS2E.Core.ArmingInterlockBitmask? interlocks = null,
                                IOrderRouteGate? routeGate = null,
                                ITickToTradeProbe? latencyProbe = null,
                                IBroker?[]? brokersByIndex = null)
        {
            _brokersByIndex = brokersByIndex;
            _latencyProbe  = latencyProbe;
            _dispatcher    = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _broker        = broker     ?? throw new ArgumentNullException(nameof(broker));
            _sendBroker    = _broker;
            _att           = attestation;
            _dualKeyIntact = dualKeyIntact;
            _gateway       = gateway;   // v10.0.0 — 주입 시 실물 사출을 최종 빗장(이중키∧방어선∧펄스∧봉인) 경유
            _placeSealed   = gateway != null ? PlaceSealed : null;   // ★δ=0: 델리게이트 1회 생성(핫패스 변환 할당 제거)
            _mockMirror    = mockMirror;    // v10.1.0 — 드라이런 스코프 시 실 브로커 대신 가상 미러로 봉인 루프백
            _scopeBarrier  = scopeBarrier;  // v10.1.0 — 드라이런/실전 라우팅 권위(전환 쿨다운·에폭 펜스)
            _slaGate       = slaGate;       // v10.1.0 Final — 드레인 사이클 지연 SLA 계측(주입 시)
            _jitterGuard   = jitterGuard;   // v10.1.0 Final — ★디스케줄 아티팩트 배제(퓨즈 오트립 방지)
            _canaryGov     = canaryGov;     // v10.1.0 Ops — 실전 사출 직전 카나리 캡 검문(LiveBroker/직접 실전 경로 전용)
            _dispatchFn    = Dispatch;      // ★δ=0: 드레인 콜백 델리게이트 1회 생성(타이머/핀 스레드 가동 전)
            _routeGate     = routeGate;     // [피드 연동] 송출 직전 라우터 판정 + 송출 결과 슬롯 결속/롤백
            _interlocks    = interlocks;    // [실배선] 부팅 무결성·자격증명 유출·포렌식 체인·이상 시세·카오스 가드 집계 비트
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
            try
            {
                if (_slaGate is { } sla)
                {
                    // ★나노 SLA 계측: 드레인 사이클 지연(δ=0). per-op QPC 계측은 sub-100ns op 을 왜곡(관측자 효과)하므로 사이클
                    //   경계에서 측정. 지터 가드 admit(디스케줄/역행시계 아티팩트 배제) 표본만 히스토그램 계상 → SLA 퓨즈 오트립 방지.
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    _dispatcher.Drain(_dispatchFn);
                    long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    long nanos = SlaTickFreq > 0L ? (long)((System.Int128)dt * 1_000_000_000L / SlaTickFreq) : 0L;
                    if (_jitterGuard is { } jg) { if (jg.Admit(nanos)) sla.Record(nanos); }
                    else sla.Record(nanos);
                }
                else _dispatcher.Drain(_dispatchFn);
            }
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
            long t0 = _latencyProbe?.Timestamp() ?? 0;   // [저지연] 실전 경로 디스패치 진입 시각(프로브 단조 시계)

            // ★[실배선 · 방향 B] 원자 인터락 최종 veto — 디스패처가 무장을 판정한 '뒤' 실 송출 '직전'에 인터락 집계어를 1회 재판독.
            //   판정과 송출 사이에 부팅 무결성 변조·자격증명 유출·포렌식 체인 붕괴·이상 시세·카오스 홀드가 Trip 했거나, 필수 소스
            //   미결속/기동 샌니티 미봉인이면 미사출(GatedOut). 드라이런 미러 경로(위)는 브로커 참조 0 이라 적용 대상 아님.
            if (_interlocks is { } il && il.Blocked)
            {
                System.Threading.Interlocked.Increment(ref _interlockVetoes);
                return false;
            }

            // ★v10.1.0 Ops 카나리 캡: 실전 사출 직전, 최초 실 노출을 자본 ≤0.1% 로 물리 제한. 캡 초과 or 슬리피지 강등 → 미사출(GatedOut).
            //   드라이런 경로는 위에서 이미 미러로 반환 — 카나리는 LiveBroker/직접 실전 경로에만 적용(liveArmed:false 정합).
            if (_canaryGov is { } canary)
            {
                // ★fail-closed 명목: 시장가(PriceRaw≤0·명목 미상)·비정상 수량·Int128→long 오버플로 → long.MaxValue(캡 초과 거부).
                //   bare (long) 절단은 2^64 배수 근방 명목이 소액 양수로 랩→캡 우회(fail-open)·시장가는 명목 0→무한 우회였음. 지정가만 정확 계산·나머지 거부.
                System.Int128 notional128 = (p.PriceRaw <= 0L || p.QtyRaw <= 0L)
                    ? System.Int128.MaxValue
                    : (System.Int128)p.PriceRaw * p.QtyRaw / 100_000_000L;
                long notionalRaw = notional128 > long.MaxValue ? long.MaxValue : (long)notional128;   // 클램프(절단 랩 봉쇄)
                if (!canary.TryAdmitOrder(notionalRaw)) return false;   // 캡 초과/강등/미상 명목 → veto(GatedOut·fail-closed)
            }

            // ★[피드 연동] 라우터 게이트 — 카나리까지 통과한 실전 주문만 참조가·라우터 판정 + 추적 슬롯 확보. 실패 = 미송출.
            // [보호 지정가] 송출 형태 = 게이트 승인값(시장가 → 보호 지정가 · 잔량 부족 시 수량 축소). 게이트 미주입이면 원 제안 그대로.
            byte sendKind = p.KindByte; long sendPrice = p.PriceRaw, sendQty = p.QtyRaw, basisAge = -1;
            IBroker target = _broker;
            if (_routeGate is { } rg)
            {
                if (!rg.TryAdmit(in p, out var adm))
                {
                    System.Threading.Interlocked.Increment(ref _routeVetoes);
                    return false;
                }
                sendKind = adm.KindByte; sendPrice = adm.PriceRaw; sendQty = adm.QtyRaw; basisAge = adm.BasisAgeMicros;
                if (_brokersByIndex is { } map)
                {
                    // ★라우팅 결과 브로커로 송출 — 매핑 없는 인덱스는 '기본 브로커로 대체'하지 않는다(라우터 판정 우회 = fail-open)
                    if ((uint)adm.Broker >= (uint)map.Length || map[adm.Broker] is not { } chosen)
                    {
                        System.Threading.Interlocked.Increment(ref _brokerIndexVetoes);
                        rg.OnNotSent(p.OrderId);
                        return false;
                    }
                    target = chosen;
                }
            }
            _sendBroker = target;

            // ★[저지연 · 방향 B] 송출 직전 지연 폭주 veto — 디스패치 진입 이후 GC 정지/스레드 선점으로 임계를 넘겼으면 그 주문은
            //   이미 낡은 판단 위에 있다 → 미송출(슬롯 롤백) + 프로브가 회로 Disarm. '보낸 뒤 취소' 대신 '보내기 전 원자 중단'.
            if (_latencyProbe is { } lp && !lp.AdmitBeforeSend(t0))
            {
                System.Threading.Interlocked.Increment(ref _latencyVetoes);
                _routeGate?.OnNotSent(p.OrderId);
                return false;
            }

            // ★v10.0.0 최종 빗장: 게이트웨이 주입 시 실물 사출을 이중키∧방어선∧펄스 재검 + 단발 개통 + 봉인 사출
            //   (exactly-once·torn 방어) 경유. veto 시 false(미사출·fail-safe·Dispatcher 는 GatedOut). 미주입(기본)이면 직접 경로(동작 불변·true).
            if (_gateway is { } gw)
            {
                bool emitted = gw.Emit(p.OrderId, p.SymbolCode, p.SideByte, sendKind, sendPrice, sendQty, _placeSealed!);
                if (!emitted) _routeGate?.OnNotSent(p.OrderId);   // 게이트웨이 veto = 브로커 미도달 → 슬롯 롤백(접수된 주문은 게이트가 무시)
                else _latencyProbe?.RecordSent(t0, basisAge);
                return emitted;
            }

            var symbol     = KseSymbolText.D6(p.SymbolCode);   // [E2E · δ=0] 주문당 문자열 할당 제거(캐시 적중)
            var side       = p.Side == OrderSide.Buy ? PaperSide.Buy : PaperSide.Sell;
            var kind       = sendKind == 0 ? PaperKind.Market : PaperKind.Limit;
            var qty        = new QQty(sendQty).ToDecimal();
            var limitPrice = new QPrice(sendPrice).ToDecimal();

            var (ok, msg) = target.Place(
                PaperAsset.Stock, symbol, symbol,
                side, kind, qty, limitPrice, limitPrice);

            if (!ok)
            {
                _routeGate?.OnNotSent(p.OrderId);
                throw new InvalidOperationException($"Broker.Place 거부: {msg}");
            }
            _routeGate?.OnBrokerAccepted(p.OrderId, msg);
            _latencyProbe?.RecordSent(t0, basisAge);   // [저지연] 송출 완료 시점 Tick-to-Trade 기록
            return true;
        }

        // ★v10.0.0 봉인 사출: 게이트웨이가 봉인한 불변 토큰만 소켓으로. 거부 시 throw → 상류 Mission-A 벽(ForceFreeze).
        private void PlaceSealed(in SealedOrderToken t)
        {
            var symbol     = KseSymbolText.D6(t.SymbolCode);
            var side       = t.SideByte == 0 ? PaperSide.Buy : PaperSide.Sell;
            var kind       = t.KindByte == 0 ? PaperKind.Market : PaperKind.Limit;
            var qty        = new QQty(t.QtyRaw).ToDecimal();
            var limitPrice = new QPrice(t.PriceRaw).ToDecimal();

            var (ok, msg) = _sendBroker.Place(
                PaperAsset.Stock, symbol, symbol,
                side, kind, qty, limitPrice, limitPrice);

            if (!ok)
            {
                _routeGate?.OnNotSent(t.OrderId);
                throw new InvalidOperationException($"Broker.Place 거부: {msg}");
            }
            _routeGate?.OnBrokerAccepted(t.OrderId, msg);
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
