// ============================================================================
// AtomicCutoverGate — v4.97.0 과제2: 원자적 컷오버 게이트웨이 + Think Beyond Gemini
//
//  운영자 서명 컷오버 시 섀도우→실전을 무재시작·무상태손실로 <1ms 원자 전환하고, 미체결 섀도우 백로그를 원자적으로
//  플러시(취소)하며, 컷오버 후 첫 핫패스 주문만 실전 라인에 무손실 진입시킨다.
//
//  ── ★Think Beyond Gemini(제미니 기초 아이디어 초월) ─────────────────────────
//  제미니 예시(Idea A 카나리 링·Idea B 듀얼트랙)를 '둘 중 택1'이 아니라 하나의 자기치유 3중 방어로 융합한 나의 설계:
//  「자율 카나리 검역 + 듀얼트랙 대조 + 되감기 원자 롤백」.
//   (A) 자율 카나리 검역: 컷오버 직후 30초 검역창에서 실전 RTT를 '섀도우 베이스라인 대비 단측(상향) 3σ'로 δ=0 검증.
//       느려짐=위험만 트립(SentimentVelocityBreaker 단측 3σ 원형 복제). 트립 시 <0.1ms '가역' 롤백.
//   (B) 듀얼트랙 대조: 섀도우가 예측한 체결 명목 vs 실체결 명목을 1raw 정밀 대조(ShadowClearingTracer 관용구).
//       |drift|>1raw = 회계 불일치(중대) → 경성 ForceFreeze(ManualRelease 필요·비가역).
//   (C) 되감기 원자 롤백: EnterShadow() 단일 volatile write(<0.1ms)로 자본을 즉시 섀도우로 복귀. ★핵심 통찰 —
//       '섀도우 플래그가 마스터 컷오프'이므로 EnterShadow() 하나로 자본이 100% 안전해진다(점화 FSM 은 MasterIgnited
//       그대로 두어 재컷오버를 즉시·무의식 가능케 = 진짜 가역). 이중키 인터락(②)은 매 라우팅마다 재평가되므로
//       단일 플래그로도 이중 게이트가 유지된다.
//   ▷ 제미니 초월 논거: 제미니의 카나리(A)는 '검출 후 롤백'만, 듀얼트랙(B)은 '검출 후 정지'만 제안한다. 나의 설계는
//     둘을 위험 등급별로 분기(연성 RTT 이상=가역 롤백, 경성 회계 불일치=경성 동결)하고, 롤백을 '점화 FSM 파괴 없는
//     플래그 되감기'로 만들어 30초 창 안에서 무제한 재시도(canary 재무장)를 무비용으로 허용한다. 되감기 저널
//     (MmfFlightRecorder)에 전 전이를 기록해 사후 포렌식까지 닫는다.
//
//  ── 2단 컷오버(느린 사전의식 / <1ms 원자 flip 분리) ─────────────────────────
//   PreArm(sig): SafeIgnitionSequencer.BeginIgnition 재사용 — 이중키(ValveWired∧LiveArmingWired)+서명+격리 1주
//     프로브+3σ·지연 검증→MasterIgnited. ★성공해도 IsShadowMode 는 true 유지(자본 안전). 상태=ArmedReady.
//   Cutover(sig): 상태 ArmedReady 확인 → 점화 MasterIgnited·이중키 '둘 다' 재검(인터락②) → 서명 재확인 →
//     섀도우 백로그 원자 플러시 → gateway.EnterLive()(단일 volatile write·<0.1ms) → 검역 진입. 무재시작·무상태손실.
//
//  ── liveArmed:false 이중 차단 인터락(적대검증 요구) ──────────────────────────
//   ①층(수학적 0): 컷오버 자체가 BeginIgnition 의 G0(valve∧live 동시)를 통과해야만 발생 — 이중키 미충족이면 컷오버
//     불성립·섀도우 유지(자본 안전). ②층(물리 차단): 설령 컷오버가 됐어도 매 TryRoute 가 IsOrderSendAllowed
//     (이중키∧MasterIgnited) 재평가 — 컷오버 후 이중키가 하나라도 내려가면 실주문 0바이트+fail-closed 동결.
//
//  ── δ=0 / 결정론 ────────────────────────────────────────────────────────────
//   상태 전이=volatile/Interlocked, 카나리 3σ=고정 long[128] 링+Int128(무할당·무sqrt), 듀얼트랙=정수 차. 컷오버/롤백
//   경로도 힙 0(문자열 사유는 사전 상수/보간이나 핫패스 아님). 벽시계/RNG 판정 없음(주입 시계).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>컷오버 수명주기 상태(관제·텔레메트리). 단조: Shadow→ArmedReady→CanaryQuarantine→Live, 이탈: RolledBack/Frozen.</summary>
    public enum CutoverState : byte { Shadow = 0, ArmedReady = 1, CanaryQuarantine = 2, Live = 3, RolledBack = 4, Frozen = 5 }

    /// <summary>컷오버 시도 결과(불변 진단 스냅샷).</summary>
    public readonly struct CutoverResult
    {
        public readonly bool        Success;
        public readonly CutoverState State;
        public readonly string      Reason;
        public CutoverResult(bool success, CutoverState state, string reason)
        { Success = success; State = state; Reason = reason; }
    }

    /// <summary>
    /// 원자적 컷오버 게이트웨이 — 섀도우↔실전 무재시작 전환 + 자율 카나리 검역·듀얼트랙 대조·가역 되감기.
    /// 기존 싱글톤(SafeIgnitionSequencer·IgnitionLock·OrderAttestationService·ShadowExecutionGateway) 공유(신규 인스턴스 금지).
    /// </summary>
    public sealed class AtomicCutoverGate
    {
        // ── 협력자(주입·전부 기존 싱글톤 재사용) ───────────────────────────────
        private readonly ShadowExecutionGateway   _gateway;
        private readonly SafeIgnitionSequencer    _sequencer;
        private readonly IgnitionLock             _ignition;
        private readonly OrderAttestationService  _liveAtt;      // 경성 동결(듀얼트랙 위반)·라우팅 건전성 소스
        private readonly Func<bool>               _valveWired;   // 이중키 A
        private readonly Func<bool>               _liveArmingWired; // 이중키 B
        private readonly Func<long>               _nowTicks;
        private readonly long                     _tickFreq;
        private readonly MmfFlightRecorder?       _blackbox;     // 되감기 저널(포렌식·선택)

        // ── 정책(불변) ─────────────────────────────────────────────────────────
        /// <summary>듀얼트랙 허용 오차(raw). |drift| 가 이 값을 '초과'할 때만 동결(정상 1raw 반올림 잔차는 관용). 헤더 |drift|&gt;1raw 계약과 정렬.</summary>
        public long DualTrackToleranceRaw { get; init; } = 1L;
        /// <summary>검역 통과(=Live 승격)에 필요한 '컷오버 후' 카나리 청정 표본 수(시드 베이스라인 표본은 불포함).</summary>
        public int  CanaryPromoteSamples  { get; init; } = 16;

        // ── 상태 ───────────────────────────────────────────────────────────────
        private int _state = (int)CutoverState.Shadow;   // Volatile.Read/Write(ref) 접근 — 평문 int(CS0420 회피)
        private long _cutoverTicks, _canaryTrips, _dualTrackFreezes, _rollbacks, _cutovers;
        private long _lastCanaryDevRaw, _lastDualTrackDriftRaw, _lastShadowBacklogFlushed;
        private int  _canaryLiveSamples;   // ★컷오버 후 청정 라이브 표본 계수(시드 베이스라인과 분리 — 검역창 붕괴 방지)
        private readonly CanaryRttSentinel _canary = new();

        public AtomicCutoverGate(ShadowExecutionGateway gateway, SafeIgnitionSequencer sequencer, IgnitionLock ignition,
                                 OrderAttestationService liveAttestation, Func<bool> valveWired, Func<bool> liveArmingWired,
                                 Func<long>? nowTicks = null, long tickFrequency = 0, MmfFlightRecorder? blackbox = null)
        {
            _gateway         = gateway;
            _sequencer       = sequencer;
            _ignition        = ignition;
            _liveAtt         = liveAttestation;
            _valveWired      = valveWired;
            _liveArmingWired = liveArmingWired;
            _nowTicks        = nowTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;
            _tickFreq        = tickFrequency > 0 ? tickFrequency : System.Diagnostics.Stopwatch.Frequency;
            _blackbox        = blackbox;
        }

        // ── 관제 독출(락-프리) ─────────────────────────────────────────────────
        public CutoverState State           => (CutoverState)Volatile.Read(ref _state);
        public bool IsLive                   => !_gateway.IsShadowMode;   // 게이트웨이 플래그가 단일 진실원
        public long CutoverCount             => Interlocked.Read(ref _cutovers);
        public long CanaryTripCount          => Interlocked.Read(ref _canaryTrips);
        public long DualTrackFreezeCount     => Interlocked.Read(ref _dualTrackFreezes);
        public long RollbackCount            => Interlocked.Read(ref _rollbacks);
        public long LastCanaryDeviationRaw   => Interlocked.Read(ref _lastCanaryDevRaw);
        public long LastDualTrackDriftRaw    => Interlocked.Read(ref _lastDualTrackDriftRaw);
        public long LastShadowBacklogFlushed => Interlocked.Read(ref _lastShadowBacklogFlushed);
        public int  CanarySampleCount        => _canary.SampleCount;

        private bool DualKeyIntact => _valveWired() && _liveArmingWired();

        /// <summary>섀도우 단계 RTT 표본을 카나리 베이스라인에 워밍업 주입(컷오버 전). δ=0.</summary>
        public void SeedCanaryBaseline(long shadowRttRaw) => _canary.Seed(shadowRttRaw);

        /// <summary>
        /// 1단계 사전 무장 — 이중키+서명+격리 프로브+3σ 의식(SafeIgnitionSequencer 재사용). 성공해도 섀도우 유지(자본 안전).
        /// 느린 사전의식(프로브 왕복)이라 컷오버(<1ms flip)와 분리. 실패는 fail-closed(내부에서 필요 시 동결).
        /// </summary>
        public CutoverResult PreArm(string signature)
        {
            if (IsLive) return new CutoverResult(false, State, "이미 실전 — 사전무장 불필요");

            IgnitionSeqResult seq = _sequencer.BeginIgnition(signature ?? "");
            if (!seq.Succeeded)
            {
                // 섀도우 유지(EnterLive 미호출). 이중키 미충족이면 여기서 자연히 걸러짐(BeginIgnition G0).
                return new CutoverResult(false, State, $"사전무장 거부 — {seq.Reason}");
            }

            Volatile.Write(ref _state, (int)CutoverState.ArmedReady);
            Record(FlightEventKind.CutoverArmed, 0L);
            return new CutoverResult(true, CutoverState.ArmedReady, "사전무장 완료(MasterIgnited·섀도우 유지) — 컷오버 대기");
        }

        /// <summary>
        /// ★2단계 원자 컷오버(<1ms) — ArmedReady/RolledBack 에서만. 점화 MasterIgnited·이중키 '둘 다' 재검(인터락②) →
        /// 섀도우 백로그 원자 플러시 → EnterLive() 단일 volatile write. 무재시작·무상태손실.
        /// </summary>
        public CutoverResult Cutover(string signature)
        {
            if (IsLive) return new CutoverResult(false, State, "이미 실전 라인 — 재컷오버 불필요");

            var st = State;
            if (st != CutoverState.ArmedReady && st != CutoverState.RolledBack)
                return new CutoverResult(false, st, "사전무장 미완료 — PreArm 선행 필요(fail-closed)");

            if (string.IsNullOrWhiteSpace(signature))
                return new CutoverResult(false, st, "운영자 서명 공백 — 컷오버 거부(fail-closed)");

            // ── 인터락② 재검: 점화 MasterIgnited ∧ 이중키 둘 다 ON ∧ 미동결 ──────
            if (_ignition.Stage != IgnitionStage.MasterIgnited)
                return new CutoverResult(false, st, $"점화 미완({_ignition.Stage}) — 컷오버 거부");
            if (_liveAtt.Mode != GatingMode.Normal)
                return new CutoverResult(false, st, "비상 동결 중 — 컷오버 거부(fail-closed)");
            if (!DualKeyIntact)
            {
                // 컷오버 순간 이중키 붕괴 = 안전 침해 시도 → 경성 동결(fail-closed).
                try { _liveAtt.ForceFreeze("AtomicCutoverGate: 컷오버 순간 이중키(ValveWired∧LiveArmingWired) 붕괴 — fail-closed"); } catch { }
                Volatile.Write(ref _state, (int)CutoverState.Frozen);
                return new CutoverResult(false, CutoverState.Frozen, "이중키 붕괴 — fail-closed 동결");
            }

            // ── 섀도우 백로그 원자 플러시(미체결 섀도우 주문 취소) ─────────────────
            long flushed = _gateway.FlushShadowBacklog();
            Interlocked.Exchange(ref _lastShadowBacklogFlushed, flushed);

            // ── ★원자 flip(<0.1ms 단일 volatile write) ───────────────────────────
            Interlocked.Exchange(ref _cutoverTicks, _nowTicks());
            Volatile.Write(ref _canaryLiveSamples, 0);   // ★검역창 리셋 — 시드 베이스라인 표본과 무관하게 '컷오버 후' 청정 표본만 계수
            _gateway.EnterLive();
            Interlocked.Increment(ref _cutovers);
            Volatile.Write(ref _state, (int)CutoverState.CanaryQuarantine);
            Record(FlightEventKind.CutoverLive, flushed);
            return new CutoverResult(true, CutoverState.CanaryQuarantine,
                $"컷오버 성공 — 실전 진입·카나리 검역(백로그 {flushed}건 플러시)");
        }

        /// <summary>
        /// ★Think Beyond A — 자율 카나리 검역. 컷오버 후 실전 RTT를 섀도우 베이스라인 대비 단측 3σ로 δ=0 검증.
        /// 상향(느려짐) 3σ 이탈=위험 → <0.1ms 가역 롤백. 청정 표본 누적 시 검역 해제(Live 승격). 반환=트립 여부.
        /// </summary>
        public bool ObserveCanaryRtt(long measuredRttRaw)
        {
            var st = State;
            if (st != CutoverState.CanaryQuarantine && st != CutoverState.Live) return false;

            bool tripped = _canary.RecordAndCheckSlow(measuredRttRaw);
            Interlocked.Exchange(ref _lastCanaryDevRaw, _canary.LastDevRaw);
            if (tripped)
            {
                Interlocked.Increment(ref _canaryTrips);
                Record(FlightEventKind.CanaryTrip, measuredRttRaw);
                RollbackToShadow($"카나리 RTT 단측 3σ 상향 이탈 — 실측={measuredRttRaw}raw dev={_canary.LastDevRaw}raw");
                return true;
            }
            // ★컷오버 후 청정 라이브 표본만 계수(시드 베이스라인 표본 제외) → 검역창이 단일 표본으로 붕괴하지 않음.
            int live = Interlocked.Increment(ref _canaryLiveSamples);
            if (st == CutoverState.CanaryQuarantine && live >= CanaryPromoteSamples)
                Volatile.Write(ref _state, (int)CutoverState.Live);   // 검역 통과 — 실전 안정
            return false;
        }

        /// <summary>
        /// ★Think Beyond B — 듀얼트랙 대조. 섀도우 예측 명목 vs 실체결 명목을 1raw 정밀 대조. |drift|>허용=회계 불일치(중대)
        /// → 경성 ForceFreeze(비가역·ManualRelease 필요). 반환=동결 여부.
        /// </summary>
        public bool ReconcileDualTrack(long shadowPredictedRaw, long realRaw)
        {
            long drift = realRaw - shadowPredictedRaw;
            Interlocked.Exchange(ref _lastDualTrackDriftRaw, drift);
            long tol = DualTrackToleranceRaw;
            if (drift < -tol || drift > tol)   // ★|drift|>tol 만 동결(정상 ±tol 반올림 잔차는 관용 — off-by-one 방지)
            {
                Interlocked.Increment(ref _dualTrackFreezes);
                Record(FlightEventKind.DualTrackFreeze, drift);
                // ★경성 동결 + 섀도우 복귀: 동결이 ManualRelease 로 해제돼도 즉시 재라이브되지 않도록 섀도우로 되감아 재컷오버를 강제.
                _gateway.EnterShadow();
                try { _liveAtt.ForceFreeze($"듀얼트랙 대조 실패 — 섀도우예측={shadowPredictedRaw} vs 실체결={realRaw} drift={drift}raw(허용 {tol}) 초과"); } catch { }
                Volatile.Write(ref _state, (int)CutoverState.Frozen);
                return true;
            }
            return false;
        }

        /// <summary>
        /// ★가역 원자 되감기(<0.1ms) — EnterShadow() 단일 volatile write 로 자본을 즉시 섀도우로 복귀. 점화 FSM 은
        /// MasterIgnited 그대로 두어 재컷오버(EnterLive) 즉시 가능. 경성 동결 아님(연성 이상 대응). 저널에 기록.
        /// </summary>
        public void RollbackToShadow(string reason)
        {
            _gateway.EnterShadow();                        // ★자본 즉시 안전(섀도우 플래그=마스터 컷오프)
            Interlocked.Increment(ref _rollbacks);
            Volatile.Write(ref _state, (int)CutoverState.RolledBack);
            Record(FlightEventKind.CutoverRollback, 0L);
        }

        private void Record(FlightEventKind kind, long arg)
        {
            var bb = _blackbox;
            if (bb is null) return;
            try { bb.Record(kind, symbolCode: 0L, arg0: arg, arg1: _state, arg2: _nowTicks(), sourceCode: 97); } catch { }
        }

        // ====================================================================
        // CanaryRttSentinel — 단측(상향) 3σ RTT 이탈 검출(δ=0·무sqrt). SentimentVelocityBreaker 단측 원형 복제.
        //   느려짐(dev>0)만 트립. 고정 long[128] 링 + Int128 누적. 단일 라이터(카나리 프로브 루프) 전제.
        // ====================================================================
        private sealed class CanaryRttSentinel
        {
            private const int Capacity = 128;
            private const int MinSamples = 10;
            private readonly long[] _rtt = new long[Capacity];
            private int   _idx, _count;
            private long  _sum;
            private Int128 _sumSq;
            private long  _lastDevRaw;

            public int  SampleCount => Volatile.Read(ref _count);
            public long LastDevRaw  => Interlocked.Read(ref _lastDevRaw);

            /// <summary>베이스라인 워밍업 주입(섀도우 RTT). 트립 판정 없이 통계만 적재.</summary>
            public void Seed(long rttRaw) => Push(rttRaw);

            /// <summary>표본 적재 후 단측 3σ 상향 이탈 판정. 워밍업(&lt;MinSamples)·퇴화 분산은 false(fail-open — 오탐 억제).</summary>
            public bool RecordAndCheckSlow(long rttRaw)
            {
                // 판정은 '적재 전' 통계(과거 분포) 대비로 — 자기 자신을 평균에 넣어 희석시키지 않게.
                int n = _count;
                bool trip = false;
                if (n >= MinSamples)
                {
                    long mean = _sum / n;
                    long dev  = rttRaw - mean;
                    Interlocked.Exchange(ref _lastDevRaw, dev);
                    if (dev > 0)   // ★단측 — 느려짐만 위험
                    {
                        Int128 var = _sumSq / n - (Int128)mean * mean;
                        if (var > 0)
                        {
                            Int128 devSq = (Int128)dev * dev;
                            if (devSq > (Int128)9 * var) trip = true;   // 3σ (제곱 비교·무sqrt)
                        }
                    }
                }
                Push(rttRaw);
                return trip;
            }

            private void Push(long rttRaw)
            {
                if (_count == Capacity)
                {
                    long old = _rtt[_idx];
                    _sum   -= old;
                    _sumSq -= (Int128)old * old;
                }
                else _count++;
                _rtt[_idx] = rttRaw;
                _sum   += rttRaw;
                _sumSq += (Int128)rttRaw * rttRaw;
                _idx = (_idx + 1) % Capacity;
            }
        }
    }
}
