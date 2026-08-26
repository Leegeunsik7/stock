// ============================================================================
// MultiBrokerShadowRing — v4.94.0 과제2: 다중 브로커 섀도우 집행 분산 링 (페어 원자 집행 센티널)
//
//  키움 x86 프록시(국내주식·정수 호가 격자)와 토스 소켓(해외주식·달러 소수점 격자)을 단일 주문 분출
//  인터페이스 아래로 통합해 자산 스펙별 자동 라우팅을 완성한다.
//
//  ── 정직 회계(정찰 렌즈 C 확정) ──────────────────────────────────────────────
//  집행 인터페이스는 IBroker 하나뿐이었고 IExecutionGate/IExtendedExecutionGate 는 전무. 결정적 갭: IBroker.Place
//  가 (bool ok, string msg)만 반환 → 반대편 leg 취소에 필요한 '브로커 주문ID'를 구조적으로 회수할 수 없다
//  (KisLiveBroker.Cancel 미구현·LiveRouterBroker.Cancel 의 KIS 미분기까지 겹쳐 국내 leg 취소가 사실상 불가).
//  따라서 IExtendedExecutionGate:IBroker 를 신설하되 1차 목적을 '주문ID 왕복(PlaceTracked)'으로 둔다.
//  라우팅은 AssetClass(DomesticStock/ForeignStock/Crypto)로 — 기존엔 집행 라우팅과 분리돼 있던 분류를 결속.
//
//  ── ★Think Further: 원자적 집행 파손 맹점 (In-Doubt 창) ──────────────────────
//  롱-숏 이종 자산 페어에서 국내 leg 는 키움 IPC 로 성공했으나 해외 leg 가 WSS/REST 지연·인증 만료로 실패하면
//  포트폴리오가 한쪽으로 쏠려 원장이 파손된다. 순진한 2PC 조차 '한쪽 성공 후 반대편 취소도 실패'하는 in-doubt
//  창을 남긴다. 본 링은 2단계 커밋에 준하는 '비동기 연쇄 롤백/중단 센티널' + 에스컬레이션 사다리로 이를 봉쇄:
//    · 양 leg 성공(각 50ms 데드라인 내) → Committed(원자 집행 성립).
//    · legA 실패 → 아무 leg 미발주 → RejectedNoLeg(깨끗).
//    · legB 실패/타임아웃 → 반대편(legA) 즉시 TryCancel(50ms 내) → 성공이면 RolledBack(깨끗한 중단).
//    · ★취소마저 실패 = 화해 불가 단독 노출 → IgnitionLock.Downgrade(신규 대형 발주 물리 차단) + ForceFreeze
//      (하드 단절·인간 개입 필요) → Exposed. '조용한 스큐'를 절대 남기지 않는다.
//    · 느린 성공(Ok 이나 데드라인 초과) leg 도 스큐 위험으로 간주해 취소 경로에 편입.
//  롤백은 멱등(주문ID 없으면 취소 불가 = 노출)·예외 격리(TryCancel throw → 취소 실패 간주, fail-closed).
//
//  ── liveArmed:false 불변 ─────────────────────────────────────────────────────
//  링은 IExtendedExecutionGate 구현체(모의/섀도우/미무장 스텁)에 위임할 뿐 발주 게이트를 우회하지 않는다.
//  미무장 게이트는 LiveBrokerStub 처럼 거부하고, 실 송출은 여전히 하류 이중키∧MasterIgnited 가 지배 → 미무장 0.
//
//  ── δ=0 경계(정직) ───────────────────────────────────────────────────────────
//  단건 라우팅은 값 타입 경로. 페어 오케스트레이션은 브로커가 반환하는 문자열(주문ID·메시지)을 담으므로 δ=0
//  '핫패스'가 아니다(페어 집행은 틱 핫패스 대비 저빈도). ActiveNodeMask 는 부팅 시 1회 산출(불변)해 독출 무할당.
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.Infrastructure
{
    /// <summary>발주 접수증 — 반대편 취소를 위한 브로커 주문ID 왕복(IBroker.Place 의 (bool,string) 갭 해소). 값 타입.</summary>
    public readonly record struct PlaceReceipt(bool Ok, string BrokerOrderId, string Msg)
    {
        public static readonly PlaceReceipt Rejected = new(false, "", "미접수");
    }

    /// <summary>자산 스펙별 확장 집행 게이트 — 주문ID 회수형 발주 + 확정적 취소. IBroker 를 상속(기존 계약 계승).</summary>
    public interface IExtendedExecutionGate : IBroker
    {
        /// <summary>이 게이트가 담당하는 자산 분류(라우팅 키).</summary>
        AssetClass Class { get; }

        /// <summary>주문ID 회수형 발주. 성공 시 BrokerOrderId 로 반대편 leg 취소가 가능(원자 집행 전제).</summary>
        PlaceReceipt PlaceTracked(PaperAsset asset, string symbol, string name,
            PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice);

        /// <summary>확정적 취소 — 성공 여부 반환(best-effort 아님). true = 취소 확인, false = 취소 실패(노출 위험).</summary>
        bool TryCancel(string brokerOrderId);
    }

    /// <summary>페어 leg 입력(값 타입).</summary>
    public readonly struct PairLeg
    {
        public readonly AssetClass Class;
        public readonly PaperAsset Asset;
        public readonly string     Symbol;
        public readonly string     Name;
        public readonly PaperSide  Side;
        public readonly PaperKind  Kind;
        public readonly decimal    Qty;
        public readonly decimal    LimitPrice;
        public readonly decimal    CurrentPrice;
        public PairLeg(AssetClass cls, PaperAsset asset, string symbol, string name,
                       PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            Class = cls; Asset = asset; Symbol = symbol; Name = name;
            Side = side; Kind = kind; Qty = qty; LimitPrice = limitPrice; CurrentPrice = currentPrice;
        }
    }

    /// <summary>페어 집행 결과 코드.</summary>
    public enum PairOutcome
    {
        Committed,      // 양 leg 성립 — 원자 집행 완료
        RejectedNoLeg,  // legA 실패/게이트 부재 → 아무 leg 미발주(깨끗)
        RolledBack,     // legB 실패 → legA 취소 성공(깨끗한 중단)
        Exposed         // ★취소 실패 = 화해 불가 단독 노출 → Downgrade + ForceFreeze
    }

    /// <summary>페어 집행 결과(값 타입). 각 leg 접수증 + 소요 tick + 사유.</summary>
    public readonly record struct PairResult(PairOutcome Outcome, PlaceReceipt LegA, PlaceReceipt LegB, long ElapsedTicks, string Detail)
    {
        public bool Ok       => Outcome == PairOutcome.Committed;
        public bool Exposed  => Outcome == PairOutcome.Exposed;
    }

    /// <summary>
    /// 다중 브로커 섀도우 집행 분산 링 — AssetClass 자동 라우팅 + 페어 2단계 원자 집행 센티널.
    /// </summary>
    public sealed class MultiBrokerShadowRing
    {
        private readonly Dictionary<AssetClass, IExtendedExecutionGate> _gates;
        private readonly OrderAttestationService? _att;
        private readonly IgnitionLock?            _ignition;
        private readonly Func<long>               _nowTicks;
        private readonly long                     _legDeadlineTicks;
        private readonly long                     _activeNodeMask;   // 부팅 1회 산출(게이트 불변) — 독출 무할당

        private long _pairsCommitted, _pairsRolledBack, _pairsExposed, _pairsRejected;
        private long _cancels, _cancelFailures, _routed, _routeRejected;

        /// <param name="gates">AssetClass→게이트 매핑(불변 스냅샷 복사).</param>
        /// <param name="nowTicks">★단조 시계(기본 Stopwatch.GetTimestamp). 데드라인 판정 경로라 벽시계(UtcNow) 금지 —
        ///   비단조 시계는 NTP 전진 점프 시 건강한 페어를 스푸리어스 타임아웃→노출로 오판시킨다. 테스트는 주입.</param>
        /// <param name="tickFrequency">nowTicks 의 ticks/sec(기본 Stopwatch.Frequency). 데드라인 tick 환산용. 테스트는 주입 시 정합값 전달.</param>
        /// <param name="legDeadlineMs">leg 데드라인(기본 50ms). 초과 시 스큐 위험으로 취소 경로 편입.</param>
        public MultiBrokerShadowRing(IReadOnlyDictionary<AssetClass, IExtendedExecutionGate> gates,
                                     OrderAttestationService? attestation = null,
                                     IgnitionLock? ignition = null,
                                     Func<long>? nowTicks = null,
                                     int legDeadlineMs = 50,
                                     long tickFrequency = 0)
        {
            if (gates is null) throw new ArgumentNullException(nameof(gates));
            _gates = new Dictionary<AssetClass, IExtendedExecutionGate>(gates.Count);
            long mask = 0L;
            foreach (var kv in gates)
            {
                if (kv.Value is null) throw new ArgumentNullException(nameof(gates), "게이트 값이 null");
                _gates[kv.Key] = kv.Value;
                mask |= 1L << (int)kv.Key;
            }
            _att              = attestation;
            _ignition         = ignition;
            _nowTicks         = nowTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;   // ★단조(결정론·NTP 점프 면역)
            long freq         = tickFrequency > 0 ? tickFrequency : System.Diagnostics.Stopwatch.Frequency;
            _legDeadlineTicks = Math.Max(1L, (long)(Math.Max(1, legDeadlineMs) * (freq / 1000.0)));   // ms → nowTicks 단위
            _activeNodeMask   = mask;
        }

        // ── 텔레메트리(락-프리 독출) ────────────────────────────────────────────
        /// <summary>등록된 브로커 노드 비트마스크(bit=AssetClass). 0=미등록. 부팅 불변.</summary>
        public long ActiveNodeMask   => _activeNodeMask;
        public int  NodeCount        => _gates.Count;
        public long PairsCommitted   => System.Threading.Interlocked.Read(ref _pairsCommitted);
        public long PairsRolledBack  => System.Threading.Interlocked.Read(ref _pairsRolledBack);
        public long PairsExposed     => System.Threading.Interlocked.Read(ref _pairsExposed);
        public long PairsRejected    => System.Threading.Interlocked.Read(ref _pairsRejected);
        public long Cancels          => System.Threading.Interlocked.Read(ref _cancels);
        public long CancelFailures   => System.Threading.Interlocked.Read(ref _cancelFailures);
        public long Routed           => System.Threading.Interlocked.Read(ref _routed);
        public long RouteRejected    => System.Threading.Interlocked.Read(ref _routeRejected);
        public bool HasGate(AssetClass cls) => _gates.ContainsKey(cls);

        /// <summary>단건 자동 라우팅 — AssetClass 게이트로 위임. 미등록이면 (false, 사유). 실 송출은 게이트 무장 계약이 지배.</summary>
        public (bool ok, string msg) Route(AssetClass cls, PaperAsset asset, string symbol, string name,
            PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            if (_att is { } a && a.Mode == GatingMode.EmergencyFreeze)
            { System.Threading.Interlocked.Increment(ref _routeRejected); return (false, "비상 동결 — 라우팅 거부"); }
            if (!_gates.TryGetValue(cls, out var gate))
            { System.Threading.Interlocked.Increment(ref _routeRejected); return (false, "라우팅 불가: " + cls + " 게이트 미등록"); }
            System.Threading.Interlocked.Increment(ref _routed);
            try { return gate.Place(asset, symbol, name, side, kind, qty, limitPrice, currentPrice); }
            catch (Exception ex) { System.Threading.Interlocked.Increment(ref _routeRejected); return (false, "게이트 예외: " + ex.Message); }
        }

        /// <summary>
        /// 페어(2-leg) 원자 집행 — 연쇄 롤백/중단 센티널. legA→legB 순차 발주, 실패/타임아웃 시 반대편 즉시 취소,
        /// 취소마저 실패면 Downgrade+ForceFreeze 로 노출을 하드 단절. 조용한 스큐를 남기지 않는다.
        /// </summary>
        public PairResult ExecutePair(in PairLeg legA, in PairLeg legB)
        {
            long start = _nowTicks();
            if (_att is { } att && att.Mode == GatingMode.EmergencyFreeze)
            { System.Threading.Interlocked.Increment(ref _pairsRejected);
              return new PairResult(PairOutcome.RejectedNoLeg, PlaceReceipt.Rejected, PlaceReceipt.Rejected, 0L, "비상 동결 — 페어 거부"); }

            if (!_gates.TryGetValue(legA.Class, out var gateA) || !_gates.TryGetValue(legB.Class, out var gateB))
            { System.Threading.Interlocked.Increment(ref _pairsRejected);
              return new PairResult(PairOutcome.RejectedNoLeg, PlaceReceipt.Rejected, PlaceReceipt.Rejected, _nowTicks() - start, "게이트 미등록"); }

            // Phase 1a — legA 발주(추적).
            PlaceReceipt rA = SafePlace(gateA, in legA);
            long afterA = _nowTicks();
            if (!rA.Ok)
            { System.Threading.Interlocked.Increment(ref _pairsRejected);
              return new PairResult(PairOutcome.RejectedNoLeg, rA, PlaceReceipt.Rejected, afterA - start, "legA 실패 — 미발주"); }
            if ((afterA - start) > _legDeadlineTicks)
            {
                // legA 느린 성공 — legB 진입 전 스큐 위험. 취소 시도.
                if (TryRollback(gateA, rA))
                { System.Threading.Interlocked.Increment(ref _pairsRolledBack);
                  return new PairResult(PairOutcome.RolledBack, rA, PlaceReceipt.Rejected, _nowTicks() - start, "legA 데드라인 초과 — 취소 성공"); }
                Escalate("legA 데드라인 초과 후 취소 실패 — 단독 노출");
                System.Threading.Interlocked.Increment(ref _pairsExposed);
                return new PairResult(PairOutcome.Exposed, rA, PlaceReceipt.Rejected, _nowTicks() - start, "★legA 취소 실패 — Downgrade+ForceFreeze");
            }

            // Phase 1b — legB 발주(추적).
            PlaceReceipt rB = SafePlace(gateB, in legB);
            long afterB = _nowTicks();
            bool bTimedOut = (afterB - afterA) > _legDeadlineTicks;
            if (rB.Ok && !bTimedOut)
            { System.Threading.Interlocked.Increment(ref _pairsCommitted);
              return new PairResult(PairOutcome.Committed, rA, rB, afterB - start, "양 leg 체결 — 원자 집행 성립"); }

            // ★legB 실패/타임아웃 → 양 leg 원상복구. legB 느린 성공이면 legB 취소, legA 도 항상 취소.
            //   ★두 취소 결과를 AND 결합 — 하나라도 실패하면(살아있는 leg 잔존) Exposed 로 에스컬레이션(조용한 스큐 금지).
            //   단락 회피: aUnwound 를 별도 라인에서 평가해 legA 취소가 항상 시도되게 한다(bUnwound 값과 무관).
            bool bUnwound = !(rB.Ok && bTimedOut) || TryRollback(gateB, rB);   // legB 취소 대상 없으면 true, 있으면 취소 성공 여부
            bool aUnwound = TryRollback(gateA, rA);                            // legA 취소 항상 시도
            if (bUnwound && aUnwound)
            { System.Threading.Interlocked.Increment(ref _pairsRolledBack);
              return new PairResult(PairOutcome.RolledBack, rA, rB, _nowTicks() - start,
                  bTimedOut ? "legB 데드라인 초과 — 양 leg 롤백 성공" : "legB 실패 — legA 롤백 성공"); }

            // ★어느 leg 든 취소 실패 = 화해 불가 단독 노출. 하드 단절(Downgrade + ForceFreeze).
            Escalate(bUnwound ? "페어 원자성 파손 — legA 취소 실패로 화해 불가 노출"
                              : "페어 원자성 파손 — legB(느린 성공) 취소 실패로 화해 불가 노출");
            System.Threading.Interlocked.Increment(ref _pairsExposed);
            return new PairResult(PairOutcome.Exposed, rA, rB, _nowTicks() - start, "★leg 취소 실패 — Downgrade+ForceFreeze");
        }

        // ── 내부 ────────────────────────────────────────────────────────────────

        private static PlaceReceipt SafePlace(IExtendedExecutionGate gate, in PairLeg leg)
        {
            try { return gate.PlaceTracked(leg.Asset, leg.Symbol, leg.Name, leg.Side, leg.Kind, leg.Qty, leg.LimitPrice, leg.CurrentPrice); }
            catch { return PlaceReceipt.Rejected; }   // ★발주 예외 → 미접수 간주(fail-closed)
        }

        /// <summary>멱등 롤백 — 접수·ID 있을 때만 취소. 취소 확인 시 true, 그 외(ID 없음·예외·실패) false = 노출.</summary>
        private bool TryRollback(IExtendedExecutionGate gate, PlaceReceipt receipt)
        {
            if (!receipt.Ok || string.IsNullOrEmpty(receipt.BrokerOrderId)) return false;   // 취소 대상 없음/ID 부재 → 노출
            System.Threading.Interlocked.Increment(ref _cancels);
            bool ok;
            try { ok = gate.TryCancel(receipt.BrokerOrderId); } catch { ok = false; }
            if (!ok) System.Threading.Interlocked.Increment(ref _cancelFailures);
            return ok;
        }

        private void Escalate(string reason)
        {
            try { _ignition?.Downgrade("MultiBrokerShadowRing: " + reason); } catch { }
            try { _att?.ForceFreeze("MultiBrokerShadowRing: " + reason); } catch { }
        }
    }
}
