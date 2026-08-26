// ============================================================================
// ShadowExecutionGateway — v4.97.0 과제1: 실거래 섀도우 집행 게이트웨이
//
//  실전 브로커망(키움 OpenAPI·토스 WSS/REST)에 정상 접속해 시세·예수금·인증을 수신하되, 최하위 주문 송출 직전
//  IsShadowMode==true 면 실 아웃바운드 소켓 전송을 100% 바이패스(격리)하고, 가로챈 주문을 폐기하거나 가상 청산
//  파이프로 미러링해 백엔드 청산 메쉬에 가상 체결(50ms 이내)을 공급한다.
//
//  ── 정직 회계(정찰 6렌즈 확정) ──────────────────────────────────────────────
//  '섀도우' 개념·부품은 이미 4중 존재(ShadowMetricsBridge _liveArmed∧TryConsumePhysical 2중게이트·ShadowClearing
//  ·ShadowExecutionRouter·MultiBrokerShadowRing). 재사용: ZeroLeakProtocolIsolator.ContainsOrderOpcode(주문패킷 식별기)
//  ·IsOrderSendAllowed(이중키∧MasterIgnited)·AssertSendAllowed(최후 물리 펜스)·ShadowNoiseConfig 슬리피지·
//  LiveAccountingMesh.OnFillPayload(가상 체결 싱크). ★진짜 델타: (1)런타임 IsShadowMode 원자 flip(기존 _liveArmed 는
//  readonly·flip 불가), (2)프리즈가 아니라 '바이패스+미러+가상체결' 분기, (3)★토스 송출 경계 절연 갭 보강(키움만 절연,
//  토스 PostOrder 무보호), (4)50ms 가상 청산 push 펌프(기존 ShadowBroker 는 sim-clock pull).
//
//  ── ★영지식 격리 샌드박스 펜스(적대검증 요구) ────────────────────────────────
//  IsShadowMode==true 면 어떤 스레드 경합·예외에서도 실 소켓으로 1바이트도 안 나간다. 2겹 방어:
//   ① 구조적 라우팅 격리: TryRoute 가 섀도우면 실 송출 심볼(SendOrderAsync/PostOrder)에 애초에 도달하지 않고 가상
//      청산 링으로만 분기. ② 최후 물리 펜스: 만약 라이브 소켓 바이트에 도달해도 AllowOutboundBytes 가 섀도우면
//      false(송출 금지)로 반환하고, 라이브-미무장 주문 패킷은 ZeroLeakProtocolIsolator.AssertSendAllowed(abort+
//      ForceFreeze+throw)로 물리 차단. ★토스 경계는 이 펜스가 없던 갭 → 본 게이트웨이 결속으로 메움.
//
//  ── liveArmed:false 이중 차단 인터락 ─────────────────────────────────────────
//  컷오버(IsShadowMode=false + MasterIgnited)만으로는 부족. 실전 라우팅(Live)은 IsOrderSendAllowed(dualKeyArmed∧
//  MasterIgnited)를 만족할 때만. 이중키(ValveWired∧LiveArmingWired) 미충족이면 라이브여도 Blocked+fail-closed 동결.
//
//  ── δ=0 ─────────────────────────────────────────────────────────────────────
//  TryRoute 핫패스: 값 타입 ShadowRouteIntent·Volatile 독출·Interlocked·RingBufferCore.Publish(관리형 슬롯 복사)·CAS — 힙 0.
//  가상 체결 생성(펌프)은 50ms 타이머 스레드(핫패스 아님). 결정론: 주입 시계·시드 PRNG(벽시계/RNG 판정 없음).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>송출 라우팅 판정 — 섀도우(바이패스+가상체결)·실전(실 송출 허용)·차단(fail-closed).</summary>
    public enum RouteVerdict : byte { Shadow = 0, Live = 1, Blocked = 2 }

    /// <summary>가상 청산 큐 단위(blittable·RingBufferCore 슬롯 복사). 가로챈 주문의 구조화 표현.</summary>
    public readonly struct ShadowRouteIntent
    {
        public readonly long SymbolCode;
        public readonly byte SideByte;   // OrderSide
        public readonly byte KindByte;   // AssetKind
        public readonly long PriceRaw;
        public readonly long QtyRaw;
        public readonly long CaptureTicks;
        public ShadowRouteIntent(long symbolCode, OrderSide side, AssetKind kind, long priceRaw, long qtyRaw, long captureTicks)
        { SymbolCode = symbolCode; SideByte = (byte)side; KindByte = (byte)kind; PriceRaw = priceRaw; QtyRaw = qtyRaw; CaptureTicks = captureTicks; }
        public OrderSide Side => (OrderSide)SideByte;
        public AssetKind Kind => (AssetKind)KindByte;
    }

    /// <summary>실거래 섀도우 집행 게이트웨이 — 송출 격리 바이패스 + 50ms 가상 청산 파이프. IsShadowMode 원자 flip.</summary>
    public sealed class ShadowExecutionGateway : IDisposable
    {
        private readonly OrderAttestationService?    _liveAtt;      // 라이브 동결(fail-closed) 단일 소스
        private readonly LiveAccountingMesh?         _shadowMesh;   // 가상 체결 싱크(★전용 인스턴스·전용 attestation 권장)
        private readonly Func<long>                  _nowTicks;
        private readonly long                        _tickFreq;
        private readonly long                        _slippageBps;  // 가상 슬리피지(raw·10^8 스케일 비율)
        private readonly int                         _pumpPeriodMs;

        private readonly RingBufferCore<ShadowRouteIntent> _ring;
        private readonly RingConsumer<ShadowRouteIntent>   _consumer;
        private System.Threading.Timer? _pump;

        private volatile int _shadowMode = 1;   // ★1=섀도우(기본·안전)·0=실전. 런타임 원자 flip.
        private int  _producing;                 // 단일 생산자 게이트(TryRoute 동시 진입 fail-closed)
        private int  _pumpBusy;                  // 단일 소비자 게이트(펌프 비재진입 ∧ 플러시 상호배제)
        private long _bypassedCount, _virtualFillCount, _blockedCount, _liveRoutedCount, _lastFillRttMicros, _droppedShadow;
        private ulong _prng;                     // 결정론 슬리피지 PRNG(SplitMix64)

        /// <param name="slippageBpsRaw">가상 슬리피지 비율 raw(10^8 스케일·기본 0.001=100000). ShadowNoiseConfig 정합.</param>
        /// <param name="pumpPeriodMs">가상 체결 공급 주기(기본 50ms — 요구 사양).</param>
        public ShadowExecutionGateway(OrderAttestationService? liveAttestation = null, LiveAccountingMesh? shadowMesh = null,
                                      Func<long>? nowTicks = null, long tickFrequency = 0,
                                      long slippageBpsRaw = 100_000L, int pumpPeriodMs = 50,
                                      int capacity = 4096, ulong slippageSeed = 42UL)
        {
            _liveAtt    = liveAttestation;
            _shadowMesh = shadowMesh;
            _nowTicks   = nowTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;
            _tickFreq   = tickFrequency > 0 ? tickFrequency : System.Diagnostics.Stopwatch.Frequency;
            // [0, Scale] 로 클램프 — 상한(100% 슬리피지)으로 `_slippageBps + 1` 오버플로·pathological 설정 차단(적대검증 방어).
            _slippageBps = slippageBpsRaw < 0 ? 0 : (slippageBpsRaw > QMath.Scale ? QMath.Scale : slippageBpsRaw);
            _pumpPeriodMs = pumpPeriodMs < 1 ? 1 : pumpPeriodMs;
            _ring       = new RingBufferCore<ShadowRouteIntent>(capacity);
            _consumer   = _ring.CreateConsumer();
            _prng       = slippageSeed == 0 ? 0x9E3779B97F4A7C15UL : slippageSeed;
        }

        // ── 상태·텔레메트리(락-프리 독출) ──────────────────────────────────────
        public bool IsShadowMode      => _shadowMode != 0;   // volatile 직접 독출(acquire) — ref 전달 금지(CS0420)
        public long BypassedCount     => Interlocked.Read(ref _bypassedCount);
        public long VirtualFillCount  => Interlocked.Read(ref _virtualFillCount);
        public long BlockedCount      => Interlocked.Read(ref _blockedCount);
        public long LiveRoutedCount   => Interlocked.Read(ref _liveRoutedCount);
        public long DroppedShadow     => Interlocked.Read(ref _droppedShadow);
        public long LastFillRttMicros => Interlocked.Read(ref _lastFillRttMicros);

        /// <summary>★실전 원자 전환(<1ms·무할당 volatile write). AtomicCutoverGate 만 호출(이중키∧MasterIgnited 검증 후).</summary>
        public void EnterLive()   => _shadowMode = 0;   // volatile 직접 기록(release·원자 int write)
        /// <summary>★섀도우 원자 복귀(되감기·<0.1ms). 카나리 실패·듀얼트랙 불일치 시 롤백.</summary>
        public void EnterShadow() => _shadowMode = 1;

        /// <summary>가상 청산 펌프 기동(50ms Timer). 섀도우 주문을 슬리피지 반영해 가상 체결로 공급.</summary>
        public void StartPump()
        {
            if (_pump is not null) return;
            _pump = new System.Threading.Timer(_ => { try { PumpOnce(); } catch { } }, null, dueTime: _pumpPeriodMs, period: _pumpPeriodMs);
        }

        /// <summary>
        /// ★구조화 송출 라우팅(핫패스·δ=0) — 하위 실 송출 '직전' 호출. 섀도우면 가상 청산 링으로 분기(실 송출 미도달),
        /// 실전이면 이중키∧MasterIgnited 시만 Live, 아니면 Blocked+fail-closed 동결.
        /// </summary>
        public RouteVerdict TryRoute(in ShadowRouteIntent o, bool dualKeyArmed, IgnitionStage stage)
        {
            if (o.QtyRaw <= 0 || o.PriceRaw < 0)   // 오염 주문 — 차단(양방향 안전)
            { Interlocked.Increment(ref _blockedCount); return RouteVerdict.Blocked; }

            if (_shadowMode != 0)
            {
                // ★섀도우: 실 소켓 미도달 — 가상 청산 링으로만 분기. 단일 생산자 게이트로 링 무결성 강제.
                if (Interlocked.CompareExchange(ref _producing, 1, 0) != 0)
                { Interlocked.Increment(ref _droppedShadow); return RouteVerdict.Shadow; }   // 동시 생산자 — 이 건 링 유실(부패 아님)
                try { _ring.Publish(o); }
                finally { Volatile.Write(ref _producing, 0); }
                Interlocked.Increment(ref _bypassedCount);
                return RouteVerdict.Shadow;
            }

            // 실전: 이미 동결 상태면 라우팅 차단(재동결 안 함) — 동결은 IsOrderSendAllowed 가 검사하지 않는 직교 조건.
            if (_liveAtt is { } att && att.Mode != GatingMode.Normal)
            { Interlocked.Increment(ref _blockedCount); return RouteVerdict.Blocked; }

            // 실전: 이중키∧MasterIgnited 동시만 허용(컷오버만으로는 부족·이중 차단 인터락).
            if (ZeroLeakProtocolIsolator.IsOrderSendAllowed(dualKeyArmed, stage))
            { Interlocked.Increment(ref _liveRoutedCount); return RouteVerdict.Live; }

            // 실전인데 이중키/점화 미충족 → fail-closed 동결(허위 실주문 방지).
            Interlocked.Increment(ref _blockedCount);
            try { _liveAtt?.ForceFreeze("ShadowExecutionGateway: 실전 라우팅인데 이중키∧MasterIgnited 미충족 — fail-closed"); } catch { }
            return RouteVerdict.Blocked;
        }

        /// <summary>
        /// ★최후 물리 펜스(원시 바이트) — 라이브 소켓 송신 직전 호출(특히 토스 REST 갭 보강). 반환 true=실 송출 허용.
        /// 비주문 패킷 통과. 섀도우면 주문 패킷 실 송출 금지(false·0바이트). 라이브-미무장 주문은 abort+ForceFreeze+throw.
        /// </summary>
        public bool AllowOutboundBytes(ReadOnlySpan<byte> packet, bool dualKeyArmed, IgnitionStage stage, Action? abort = null)
        {
            if (!ZeroLeakProtocolIsolator.ContainsOrderOpcode(packet)) return true;   // 비주문(구독·하트비트 등) 통과
            if (_shadowMode != 0)
            { Interlocked.Increment(ref _bypassedCount); return false; }              // ★섀도우 — 실 송출 금지(1바이트도 안 나감)
            // 실전: 이미 동결이면 실 송출 금지(AssertSendAllowed 는 동결을 검사하지 않으므로 TryRoute 와 동일한 직교 검문 필요·fail-closed).
            if (_liveAtt is { } att && att.Mode != GatingMode.Normal)
            { Interlocked.Increment(ref _blockedCount); return false; }
            // 실전: 최하위 절연기(미무장 주문 → 소켓 파괴+ForceFreeze+throw). 재사용.
            ZeroLeakProtocolIsolator.AssertSendAllowed(packet, dualKeyArmed, stage, _liveAtt, abort);
            Interlocked.Increment(ref _liveRoutedCount);
            return true;
        }

        /// <summary>
        /// ★섀도우 백로그 원자 플러시(취소) — 컷오버 순간 미체결 섀도우 주문을 배수-폐기(가상 체결 미생성). 펌프와 상호배제
        /// (_pumpBusy 게이트)로 단일 소비자 규율 유지. 반환=취소된 백로그 건수.
        /// </summary>
        public long FlushShadowBacklog()
        {
            var spin = new SpinWait();
            while (Interlocked.CompareExchange(ref _pumpBusy, 1, 0) != 0) spin.SpinOnce();
            try
            {
                long n = 0;
                while (_consumer.TryRead(out _)) n++;
                return n;
            }
            finally { Volatile.Write(ref _pumpBusy, 0); }
        }

        // ── 50ms 가상 청산 펌프(핫패스 아님) ───────────────────────────────────
        private void PumpOnce()
        {
            if (_shadowMesh is null) return;
            if (Interlocked.CompareExchange(ref _pumpBusy, 1, 0) != 0) return;   // 비재진입 ∧ 플러시와 상호배제
            try { DrainToFills(); }
            finally { Volatile.Write(ref _pumpBusy, 0); }
        }

        private void DrainToFills()
        {
            var mesh = _shadowMesh;
            if (mesh is null) return;
            long now = _nowTicks();
            while (_consumer.TryRead(out var o))
            {
                long fillPriceRaw = ApplySlippage(o.PriceRaw, o.Side);
                // ★raw 수량 경로(OnFill) 직결 — FillPayload 정수 FillQty 왕복이 코인 소수 수량을 절사(유실)시키므로 우회.
                bool accepted;
                try { accepted = mesh.OnFill(o.SymbolCode, o.Side, o.Kind, fillPriceRaw, o.QtyRaw, now); }
                catch { accepted = false; }
                if (accepted) Interlocked.Increment(ref _virtualFillCount);   // ★수락된 가상 체결만 계수(no-op/거부를 허위 계수하지 않음)
                // 가상 체결 RTT(캡처→공급) µs.
                long dt = now - o.CaptureTicks; if (dt < 0) dt = 0;
                Interlocked.Exchange(ref _lastFillRttMicros, dt * 1_000_000L / _tickFreq);
            }
        }

        // 결정론 슬리피지 — 매수 불리(가격↑)·매도 불리(가격↓)로 편향(플랫폼 보수). SplitMix64 시드 지터.
        private long ApplySlippage(long priceRaw, OrderSide side)
        {
            _prng += 0x9E3779B97F4A7C15UL;
            ulong z = _prng; z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL; z ^= z >> 31;
            long jitter = (long)(z % (ulong)(_slippageBps + 1));   // [0, slippageBps]
            // notional bias: delta = priceRaw × jitter / Scale.
            long delta = (long)((Int128)priceRaw * jitter / QMath.Scale);
            long adj = side == OrderSide.Buy ? priceRaw + delta : priceRaw - delta;
            return adj < 0 ? 0 : adj;
        }

        public void Dispose()
        {
            try { _pump?.Dispose(); } catch { }
            _pump = null;
        }
    }
}
