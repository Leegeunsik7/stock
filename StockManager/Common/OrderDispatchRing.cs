// ============================================================================
// OrderDispatchRing — S29 (Path C): SPMC 락-프리 주문 디스패치 링버퍼 개통
//
//  인메모리 챔피언(전략)이 산출한 '주문 제안'을 단일 생산자 → 다중 소비자(브릿지·감사·텔레메트리)로
//  무잠금 분배한다. 이미 검증된 RingBufferCore<T>(seqlock SPMC, S20 #1) 위에 고정소수점 OrderProposal
//  값 타입을 얹어, 제안 발행과 소비 경로의 컨텍스트 스위칭 마찰을 격리한다.
//
//  ── 정직 경계(RingBufferCore 헤더 사상 계승) ────────────────────────────────
//  · 명분은 'ns 거래 지연'이 아니다 — 라이브 송출 바닥은 키움 왕복(~100ms+)이 지배한다. 본 계층의 실익은
//    '재사용 가능한 고성능 분배 코어 + 백테스트/리플레이 처리량 + 생산-소비 분리'다.
//  · OrderProposal 은 '전송 단위'일 뿐, 실제 브로커 송출은 본 링 밖의 무장 게이트가 결정한다(아래).
//
//  ── liveArmed:false 불변(전 영역) ───────────────────────────────────────────
//  링은 '전송 전용'이다. GatedOrderDispatcher 가 제안을 읽어 '무장(armed)+미동결'일 때만 IBroker 로 송출하고,
//  그 외엔 송출하지 않고 게이팅 카운터만 올린다. 무장 술어가 false(기본 자세)면 송출은 수학적 0 —
//  제안은 감사·텔레메트리 소비자로만 흐른다(드라이런 가시성). 실 무장 배선(BrokerHub.IsArmed·IgnitionLock)은
//  앱 레벨 옵트인 단계이며, 본 스캐폴드는 술어 주입으로 그 계약만 고정한다.
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>주문 제안 — 고정소수점 전송 단위(unmanaged 값 타입, 링버퍼 슬롯에 무할당 복사).</summary>
    public readonly struct OrderProposal
    {
        public readonly long OrderId;          // 단조 증가 발행 id
        public readonly long GenomeId;         // 산출 챔피언 계보
        public readonly long SymbolCode;       // KSE 6자리 등 심볼 코드
        public readonly byte SideByte;         // OrderSide(Buy=0/Sell=1)
        public readonly byte KindByte;         // 0=Market, 1=Limit
        public readonly long PriceRaw;         // QPrice raw(10^8) — 지정가(시장가는 0 허용)
        public readonly long QtyRaw;           // QQty raw(10^8)
        public readonly long TimestampTicks;   // 발행 시각(주입 — 결정론)

        public OrderProposal(long orderId, long genomeId, long symbolCode,
                             OrderSide side, byte kind, QPrice price, QQty qty, long timestampTicks)
        {
            OrderId = orderId; GenomeId = genomeId; SymbolCode = symbolCode;
            SideByte = (byte)side; KindByte = kind;
            PriceRaw = price.Raw; QtyRaw = qty.Raw; TimestampTicks = timestampTicks;
        }

        public OrderSide Side  => (OrderSide)SideByte;
        public QPrice    Price => new(PriceRaw);
        public QQty      Qty   => new(QtyRaw);
    }

    /// <summary>S29 — SPMC 주문 디스패치 링. <b>단일 생산자 전용</b> Publish. 소비자는 CreateConsumer 로 독립 커서.</summary>
    public sealed class OrderDispatchRing
    {
        private readonly RingBufferCore<OrderProposal> _ring;
        private long _nextOrderId;
        private int  _publishing;        // 0=idle,1=publishing — ★단일 생산자 계약 런타임 강제(CAS)
        private long _writerCollisions;  // S29 Gamma — CAS 게이트 낙방(동시 생산자 위반) 누적 계측

        /// <param name="capacity">2 이상의 2의 거듭제곱(기본 1024).</param>
        public OrderDispatchRing(int capacity = 1024) => _ring = new RingBufferCore<OrderProposal>(capacity);

        public int  Capacity  => _ring.Capacity;
        /// <summary>최고 발행 시퀀스(-1 = 미발행).</summary>
        public long Published  => _ring.Published;
        /// <summary>S29 Gamma — 단일 생산자 CAS 게이트 낙방 누적(동시 발행 진입 위반 횟수). 0 이 정상.</summary>
        public long WriterCollisions => Interlocked.Read(ref _writerCollisions);

        // ★적대적 리뷰(minor) — RingBufferCore.Publish 는 단일 라이터 전용(비원자 seq++). 계약을 주석에만 두지 않고
        //   CAS 로 런타임 강제: 동시 생산자 진입은 슬롯/시퀀스 손상 대신 즉시 fail-closed throw. 정당 단일 생산자는 비경합.
        //   S29 Gamma — throw 전에 낙방을 계상(텔레메트리). 정상 단일 생산자 경로는 비경합이라 0 유지(핫패스 무영향).
        private void EnterPublish()
        {
            if (Interlocked.CompareExchange(ref _publishing, 1, 0) != 0)
            {
                Interlocked.Increment(ref _writerCollisions);
                throw new InvalidOperationException("OrderDispatchRing: 동시 생산자 위반 — Publish 는 단일 생산자 전용");
            }
        }

        /// <summary>챔피언 제안 발행(단일 생산자 전용·게이트 강제). OrderId 자동 증가, 발행 id 반환. 무할당.</summary>
        public long Publish(long genomeId, long symbolCode, OrderSide side, byte kind,
                            QPrice price, QQty qty, long timestampTicks)
        {
            EnterPublish();
            try
            {
                long id = ++_nextOrderId;   // 게이트가 단일 생산자 보장 → 비경합(Interlocked 불요, 다중생산자 오해 제거)
                _ring.Publish(new OrderProposal(id, genomeId, symbolCode, side, kind, price, qty, timestampTicks));
                return id;
            }
            finally { Volatile.Write(ref _publishing, 0); }
        }

        /// <summary>사전 구성된 제안 직접 발행(단일 생산자 전용·게이트 강제·무할당).</summary>
        public void PublishRaw(in OrderProposal proposal)
        {
            EnterPublish();
            try { _ring.Publish(in proposal); }
            finally { Volatile.Write(ref _publishing, 0); }
        }

        /// <summary>독립 커서 소비자 생성(브릿지·감사·텔레메트리 각 1개). 무잠금 TryRead.</summary>
        public RingConsumer<OrderProposal> CreateConsumer() => _ring.CreateConsumer();
    }

    /// <summary>
    /// S29 — 무장 게이트 디스패처. 링에서 제안을 빼 '무장+미동결'일 때만 송출 콜백 호출, 아니면 게이팅 카운터만 증가.
    /// liveArmed 술어가 false 면 송출 0(드라이런). 술어 주입으로 BrokerHub.IsArmed·IgnitionLock·attestation 과
    /// 결속한다(앱 레벨 옵트인). 송출 콜백 자체가 실 브로커 경계 — 본 디스패처는 게이트만 강제.
    /// ★Mission A — onSend throw fail-closed 벽: 브로커 단절·네트워크 오류 시 즉시 ForceFreeze(셧다운). fail-open 절대 금지.
    /// </summary>
    public sealed class GatedOrderDispatcher
    {
        private readonly RingConsumer<OrderProposal> _consumer;
        private readonly Func<bool>                  _isArmed;
        private readonly OrderAttestationService?    _att;    // Mission A: onSend throw → ForceFreeze(null 허용 = att 없음)
        private readonly MmfFlightRecorder?          _blackbox;   // v4.95.0 — 블랙박스 이산 사건 기록(null=미배선·완전 no-op·δ=0)
        private readonly BlastRadiusLimiter?         _blastRadius;   // v5.0.0 — 카나리 검역창 명목 상한 물리 강제(null=무제한·하위호환)
        private long _forwarded;
        private long _gatedOut;
        private long _sendErrors;   // Mission A: downstream throw 누적(원자)

        /// <param name="attestation">선택적 — null 이면 ForceFreeze 미호출(테스트 시나리오). 앱 레벨은 반드시 주입.</param>
        /// <param name="blackbox">선택적 — v4.95.0 블랙박스 기록계(null=미배선·완전 no-op). Record 는 δ=0.</param>
        /// <param name="blastRadius">선택적 — v5.0.0 블래스트 반경 제한기(null=무제한). 명목 상한 초과 시 fail-closed 동결.</param>
        public GatedOrderDispatcher(OrderDispatchRing ring, Func<bool> isArmed,
                                    OrderAttestationService? attestation = null,
                                    MmfFlightRecorder? blackbox = null,
                                    BlastRadiusLimiter? blastRadius = null)
        {
            if (ring is null) throw new ArgumentNullException(nameof(ring));
            _consumer = ring.CreateConsumer();
            _isArmed  = isArmed ?? throw new ArgumentNullException(nameof(isArmed));
            _att      = attestation;
            _blackbox = blackbox;
            _blastRadius = blastRadius;
        }

        /// <summary>무장 상태에서 실제 브로커로 송출된 제안 수(onSend 성공 시만 계상).</summary>
        public long Forwarded   => Interlocked.Read(ref _forwarded);
        /// <summary>미무장(fail-closed)으로 송출이 차단된 제안 수.</summary>
        public long GatedOut    => Interlocked.Read(ref _gatedOut);
        /// <summary>추월로 유실된 제안 누적 수(소비 지연 계측).</summary>
        public long Dropped     => _consumer.Dropped;
        /// <summary>Mission A — onSend throw(브로커 단절 등) 누적. 0 이 정상. 양수 시 비상 동결 확인 필수.</summary>
        public long SendErrors  => Interlocked.Read(ref _sendErrors);

        /// <summary>가용 제안을 모두 처리. 무장 시 onSend 호출(+Forwarded), 미무장 시 차단(+GatedOut). 처리 건수 반환.
        /// ★fail-closed 벽: onSend throw 시 SendErrors++·ForceFreeze(즉시 비상 동결)·루프 break(추가 처리 차단).
        /// 단일 소비자 전용(이 디스패처를 동시 Drain 하지 말 것 — 내부 RingConsumer 커서는 비원자).</summary>
        public int Drain(Action<OrderProposal> onSend)
        {
            if (onSend is null) throw new ArgumentNullException(nameof(onSend));
            // 이미 비상 동결 상태면 즉시 0 반환 — ForceFreeze 재호출 불요·루프 폭주 차단.
            if (_att is { } attCheck && attCheck.Mode == GatingMode.EmergencyFreeze) return 0;
            int n = 0;
            while (_consumer.TryRead(out var p))
            {
                n++;   // 제안은 이미 소비됨 — throw 로 인한 '증발' 방지 위해 먼저 계상
                // ★무장 게이트 — 매 제안마다 재평가(런타임 후퇴 즉시 반영). 술어가 throw 하면(예: BrokerHub.IsArmed 의
                //   db I/O 예외) fail-closed: 미무장 간주 → 송출 안 함 + GatedOut 계상(예외 전파·제안 증발 차단).
                bool armed;
                try { armed = _isArmed(); }
                catch { armed = false; }
                if (armed)
                {
                    // ★v5.0.0 블래스트 반경 물리 강제 — 무장 통과 직후·송출 직전 단일 초크포인트. 검역창 명목 상한 초과=경성 동결(fail-closed).
                    //   완전 무장 상태여도 카나리 1주 상한을 초과하는 대형 주문(fat-finger/버그)은 여기서 물리 차단. 무제한(null/미설정)이면 통과.
                    if (_blastRadius is { } br)
                    {
                        long notional;
                        // ★시장가(PriceRaw<=0)는 명목 미상 — 상한 활성 중이면 '무한 명목'으로 취급해 물리 차단(대형 시장가 fat-finger 봉쇄).
                        //   PreTradeRiskShield 의 시장가=미상 fail-closed 관례와 정렬. 상한 비활성(무제한)이면 무영향(0 통과).
                        if (p.PriceRaw <= 0) notional = br.Active ? long.MaxValue : 0L;
                        else { try { notional = QMath.Notional(p.PriceRaw, p.QtyRaw, roundUp: true); } catch (OverflowException) { notional = long.MaxValue; } }
                        if (!br.Check(notional))
                        {
                            Interlocked.Increment(ref _sendErrors);
                            _blackbox?.Record(FlightEventKind.OrderSendError, p.SymbolCode, p.PriceRaw, p.QtyRaw, p.OrderId, 2);
                            _att?.ForceFreeze($"블래스트 반경 초과 — 명목 {notional} > 상한 {br.CeilingRaw}(카나리 검역창 물리 상한·fail-closed)");
                            break;
                        }
                    }
                    try
                    {
                        onSend(p);
                        Interlocked.Increment(ref _forwarded);   // 성공 시만 계상
                        _blackbox?.Record(FlightEventKind.OrderForwarded, p.SymbolCode, p.PriceRaw, p.QtyRaw, p.OrderId, 1);
                    }
                    catch (Exception ex)
                    {
                        // ★Mission A — downstream throw(브로커 단절·네트워크 오류) fail-closed 벽.
                        // ForceFreeze 는 항상 성공(멱등·audit append) — fail-open 절대 금지.
                        Interlocked.Increment(ref _sendErrors);
                        _blackbox?.Record(FlightEventKind.OrderSendError, p.SymbolCode, p.PriceRaw, p.QtyRaw, p.OrderId, 1);
                        _att?.ForceFreeze("GatedOrderDispatcher downstream throw: " + ex.Message);
                        break;   // 추가 제안 처리 차단 — 동결 후 루프 폭주 방지
                    }
                }
                else
                {
                    Interlocked.Increment(ref _gatedOut);
                    _blackbox?.Record(FlightEventKind.OrderGated, p.SymbolCode, p.PriceRaw, p.QtyRaw, p.OrderId, 1);
                }
            }
            return n;
        }
    }
}
