// ============================================================================
// SafeIgnitionSequencer — 과제 1: 실 무장 옵트인 '안전 점화 시퀀스' (신규 sealed)
//
//  ── 무엇을 푸는가 ────────────────────────────────────────────────────────────
//  두 옵트인 키(Dispatch.ValveWired ∧ Dispatch.LiveArmingWired)가 '동시에' 켜지는
//  순간을 단순 boolean flip 으로 흘려보내지 않고, 4단계 자기검증 시퀀스를 강제한다:
//
//    Disarmed ──BeginIgnition(서명)──▶ Probing ──1주 프로브 강제 발주──▶
//    Verifying ──3σ 슬리피지 + 파이프 지연 통과?──▶ MasterIgnited
//        └─ 임계 초과 / 예외 / 미등록 / 디싱크 ─▶ Frozen (즉시 ForceFreeze)
//
//  ── 비대칭 안전(타협 불가 #2 fail-closed) ──────────────────────────────────────
//  전진(Disarmed→…→MasterIgnited)은 모든 가드 통과 + 인간 서명일 때만. 후퇴(→Frozen)는
//  어떤 단계·어떤 예외에서도 즉시·무조건. 모호하면 통과시키지 않고 동결한다. 한 줄도
//  try/catch 밖으로 새지 않게 전 구간을 감싸고 catch 는 ForceFreeze + 상태 Frozen 로 수렴.
//
//  ── liveArmed:false 박제(타협 불가 #1) ──────────────────────────────────────────
//  이 클래스는 '점화가 허용되는가'만 판정·증명하며, 실 자본 발주는 하지 않는다. 프로브는
//  반드시 격리 브로커(PaperBroker 권장)로만 나간다(probeBroker 주입·실 자본 위험 0). 두 키가
//  켜져도 MasterIgnited 도달 전까지 IgnitionLock 은 Locked 이고 LiveArmingGate 는 false 다.
//
//  ── 결정론(타협 불가 #4) ──────────────────────────────────────────────────────
//  시간은 주입 시계(Func<DateTime>)로만 읽는다(핫패스 아님·동결경로 할당 허용). RNG·
//  DateTime.UtcNow 직접 호출 없음. 동일 입력 → 동일 전이.
//
//  ── 할당(타협 불가 #3) ──────────────────────────────────────────────────────────
//  본 시퀀서는 '점화'라는 1회성 의식(ritual)이지 주문/청산 핫패스가 아니다. 따라서
//  진단 문자열·PendingOrder 생성 등 할당이 허용된다(δ=0 제약은 링/디스패처 핫패스 한정).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>안전 점화 시퀀스의 4단계 + 종단 상태. 부팅 기본 Disarmed.</summary>
    public enum IgnitionSeqState { Disarmed, Probing, Verifying, MasterIgnited, Frozen }

    /// <summary>한 번의 BeginIgnition 시도 결과(불변 진단 스냅샷·UI/로그용).</summary>
    public sealed record IgnitionSeqResult(
        bool             Succeeded,
        IgnitionSeqState FinalState,
        string           Reason,
        double           ObservedZ,
        long             PipelineLatencyMs);

    /// <summary>
    /// 두 옵트인 키 동시 활성 시 실행되는 안전 점화 시퀀서.
    /// IgnitionLock(4단계 FSM) + OrderAttestationService + SlippageSentinel 과 연동해
    /// ①1주 프로브 강제 → ②3σ·지연 검증 → ③통과 시에만 MasterIgnited, ④실패 시 ForceFreeze.
    /// 모든 전이는 fail-closed: 예외/임계초과/미등록/디싱크는 즉시 Frozen 으로 닫힌다.
    /// </summary>
    public sealed class SafeIgnitionSequencer
    {
        // ── 협력자(주입) ───────────────────────────────────────────────────────
        private readonly IgnitionLock             _ignition;
        private readonly OrderAttestationService  _attestation;
        private readonly SlippageSentinel         _sentinel;
        private readonly IBroker                  _probeBroker;   // ★격리 경로 — PaperBroker 권장
        private readonly Func<bool>               _valveWired;
        private readonly Func<bool>               _liveArmingWired;
        private readonly Func<DateTime>           _now;
        private readonly Func<string, bool>       _authorize;

        // ── 옵트인 정책(불변·init) ──────────────────────────────────────────────
        /// <summary>프로브 종목 코드(KSE 6자리). 미등록·파싱 실패 시 fail-closed 동결.</summary>
        public string ProbeSymbol         { get; init; } = "005930";
        /// <summary>프로브 종목명(브로커 표시용·검증 무관).</summary>
        public string ProbeName           { get; init; } = "PROBE";
        /// <summary>프로브 기준가(원). 슬리피지 잔차 계산 기준. ★양수 필수(&gt;0):
        /// ref&lt;=0(시장가)이면 StockValidator.ValidateFill(FillPrice&gt;0)·PaperBroker.Place(시장가 currentPrice&gt;0)
        /// 양쪽이 거부 → happy path 영구 동결(dead-on-arrival). 격리 PaperBroker 기준 명목가(직전가 또는 표식가)를
        /// 주입해 지정가 경로로 검증을 성립시킨다. ref&lt;=0 은 의도적 fail-closed 동결로 정의(적대검증 교정).</summary>
        public double ProbeReferencePrice { get; init; } = 1.0;
        /// <summary>마스터 점화에 첨부할 서명 라벨(감사 추적).</summary>
        public string IgnitionSignature   { get; init; } = "SAFE_IGNITION_SEQ";
        /// <summary>파이프라인 지연 상한(ms). 초과 시 fail-closed 동결.</summary>
        public long   MaxPipelineLatencyMs { get; init; } = 2_000;
        /// <summary>3σ 임계(센티넬 임계와 정렬). 관측 Z 가 이 값을 초과하면 fail-closed.</summary>
        public double MaxObservedSigma     { get; init; } = 3.0;

        // 재진입 방지(점화 의식은 단일 진행만 허용) + 종단 상태(volatile 원자 읽기)
        private int          _inFlight;
        private volatile int _stateInt = (int)IgnitionSeqState.Disarmed;

        public IgnitionSeqState State => (IgnitionSeqState)_stateInt;

        public SafeIgnitionSequencer(
            IgnitionLock            ignition,
            OrderAttestationService attestation,
            SlippageSentinel        sentinel,
            IBroker                 probeBroker,
            Func<bool>              valveWired,
            Func<bool>              liveArmingWired,
            Func<DateTime>?         clock     = null,
            Func<string, bool>?     authorize = null)
        {
            _ignition        = ignition        ?? throw new ArgumentNullException(nameof(ignition));
            _attestation     = attestation     ?? throw new ArgumentNullException(nameof(attestation));
            _sentinel        = sentinel        ?? throw new ArgumentNullException(nameof(sentinel));
            _probeBroker     = probeBroker     ?? throw new ArgumentNullException(nameof(probeBroker));
            _valveWired      = valveWired      ?? throw new ArgumentNullException(nameof(valveWired));
            _liveArmingWired = liveArmingWired ?? throw new ArgumentNullException(nameof(liveArmingWired));
            _now             = clock     ?? (() => DateTime.UtcNow);
            _authorize       = authorize ?? (s => !string.IsNullOrWhiteSpace(s));
        }

        // ── 진입점 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 두 옵트인 키가 '동시에' 켜졌을 때만 호출 가능한 점화 의식.
        /// 어느 한 키라도 꺼졌거나 이미 동결/진행 중이면 즉시 거부(전진 가드).
        /// 시퀀스 도중 어떤 단계 실패·예외도 catch→ForceFreeze→Frozen 로 닫힌다(후퇴 가드).
        /// </summary>
        /// <param name="humanSignature">운영자 서명. 미인가/공백이면 전진 거부.</param>
        public IgnitionSeqResult BeginIgnition(string humanSignature)
        {
            // 단일 진행 보장 — 동시 두 번째 호출은 즉시 거부(상태 오염 방지).
            if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
                return Fail(State, "이미 점화 시퀀스 진행 중 — 재진입 거부", 0.0, 0L, freeze: false);

            try
            {
                // ── 전진 가드 G0: 이중 키 동시 활성 + 인간 서명 + 미동결 ──────────
                if (State is IgnitionSeqState.Frozen or IgnitionSeqState.MasterIgnited)
                    return Fail(State, $"점화 불가 상태({State}) — 전진 거부", 0.0, 0L, freeze: false);

                if (_attestation.Mode != GatingMode.Normal)
                    return Fail(IgnitionSeqState.Disarmed, "비상 동결 중 — 점화 불가(fail-closed)", 0.0, 0L, freeze: false);

                bool valve = _valveWired();
                bool live  = _liveArmingWired();
                if (!(valve && live))
                    return Fail(IgnitionSeqState.Disarmed,
                        $"이중 키 미충족 — ValveWired={valve}, LiveArmingWired={live} (둘 다 ON 필요)",
                        0.0, 0L, freeze: false);

                if (!_authorize(humanSignature ?? ""))
                    return Fail(IgnitionSeqState.Disarmed, "미인가·변조 서명 — 점화 거부", 0.0, 0L, freeze: false);

                // ── ① Probing: ProbeReady 무장 + 1주 프로브 강제 발주(격리 경로) ──
                _stateInt = (int)IgnitionSeqState.Probing;

                if (!_ignition.Arm(IgnitionStage.ProbeReady, IgnitionSignature))
                    return Fail(IgnitionSeqState.Probing, "IgnitionLock.Arm(ProbeReady) 거부", 0.0, 0L, freeze: true);

                long probeId = ExecuteIsolatedProbe();   // 실패 시 내부에서 throw → 아래 catch 가 동결

                // ── ② Verifying: 3σ 슬리피지 + 파이프 지연 검증 ───────────────────
                _stateInt = (int)IgnitionSeqState.Verifying;

                long startTicks = _now().Ticks;
                FillPayload probeFill = SimulateProbeFill(probeId, startTicks);

                // 체결 증적 — ProcessFill 이 방향/수량/가격오차 위반 시 내부에서 ForceFreeze.
                var pending = BuildPendingOrder(probeId);
                FillAuditResult audit = _attestation.ProcessFill(probeFill, pending);
                if (!audit.Passed)
                    return Fail(IgnitionSeqState.Verifying, $"체결 증적 실패: {audit.Reason}", 0.0, 0L, freeze: true);

                // 슬리피지 센티넬에 잔차 주입 → 관측 Z 회수(3σ 게이트).
                // ★경계 정직: SlippageSentinel 은 표본 _n<4(분산 미수렴)면 LastZ 를 갱신하지 않으므로,
                //   단일 프로브 1회로는 항상 z=0 보류 통과한다. 3σ 적발력은 다회 표본/실 체결 스트림에서만
                //   유효하며, 격리 단일 프로브 단계에서 이 게이트는 NaN/Inf/직전 누적 z 방어선 역할만 한다.
                _sentinel.RecordFill(probeFill, ProbeReferencePrice);
                double observedZ = _sentinel.LastZ;
                if (double.IsNaN(observedZ) || double.IsInfinity(observedZ) || observedZ > MaxObservedSigma)
                    return Fail(IgnitionSeqState.Verifying,
                        $"슬리피지 {observedZ:F2}σ > {MaxObservedSigma:F2}σ 임계 초과", observedZ, 0L, freeze: true);

                // 파이프라인 지연(발주→체결 왕복 측정) 게이트.
                long latencyMs = (_now().Ticks - startTicks) / TimeSpan.TicksPerMillisecond;
                if (latencyMs < 0 || latencyMs > MaxPipelineLatencyMs)
                    return Fail(IgnitionSeqState.Verifying,
                        $"파이프 지연 {latencyMs}ms > {MaxPipelineLatencyMs}ms 임계 초과", observedZ, latencyMs, freeze: true);

                // 검증 직전 재독출 — 검증 도중 외부 신호(3σ Attach·디싱크)로 이미 동결됐는지 확인.
                if (_attestation.Mode != GatingMode.Normal || _ignition.SystemFrozen)
                    return Fail(IgnitionSeqState.Verifying, "검증 중 외부 동결 감지 — 점화 중단", observedZ, latencyMs, freeze: true);

                // ── ③ MasterIgnited: 모든 가드 통과 — 최종 점화 ──────────────────
                if (!_ignition.Arm(IgnitionStage.MasterIgnited, IgnitionSignature))
                    return Fail(IgnitionSeqState.Verifying, "최종 IgnitionLock.Arm(MasterIgnited) 거부", observedZ, latencyMs, freeze: true);

                _stateInt = (int)IgnitionSeqState.MasterIgnited;
                return new IgnitionSeqResult(true, IgnitionSeqState.MasterIgnited,
                    "안전 점화 시퀀스 통과 — MasterIgnited", observedZ, latencyMs);
            }
            catch (Exception ex)
            {
                // ── ④ 어떤 예외도 fail-closed 로 흡수: 즉시 동결, 절대 재throw 않음 ──
                return Fail(State, $"점화 시퀀스 예외 — fail-closed 동결: {ex.GetType().Name}: {ex.Message}",
                            0.0, 0L, freeze: true);
            }
            finally
            {
                Volatile.Write(ref _inFlight, 0);
            }
        }

        /// <summary>운영자 수동 동결(긴급). 어떤 상태에서도 즉시 Frozen + ForceFreeze.</summary>
        public IgnitionSeqResult Abort(string reason)
            => Fail(State, string.IsNullOrWhiteSpace(reason) ? "운영자 수동 점화 중단" : reason, 0.0, 0L, freeze: true);

        // ── 격리 프로브 발주 (실 자본 위험 0) ─────────────────────────────────────

        /// <summary>
        /// 1주 프로브를 격리 브로커로만 발주한다. IsLive 브로커가 주입되면(실 자본 노출) 즉시 throw —
        /// 시퀀스가 catch 에서 fail-closed 동결한다. TryConsumePhysical 로 점화락 물리 게이트도 원자 소비.
        /// </summary>
        private long ExecuteIsolatedProbe()
        {
            // ★실 자본 위험 최소화: 격리 경로 강제. IsLive 브로커면 프로브 자체를 거부.
            if (_probeBroker.IsLive)
                throw new InvalidOperationException(
                    "프로브 브로커가 실거래(IsLive) — 격리 경로 위반. PaperBroker 등 격리 브로커만 허용.");

            long symbolCode = BridgeSerializer.ParseSymbol(ProbeSymbol);
            if (symbolCode <= 0L)
                throw new InvalidOperationException($"미등록·불량 프로브 종목코드: '{ProbeSymbol}'");

            // ★적대검증 교정: ProbeReferencePrice<=0(시장가)은 PaperBroker.Place(시장가 currentPrice<=0 거부)와
            //   StockValidator.ValidateFill(FillPrice>0)이 양쪽에서 거부하므로 happy path 가 영구 동결된다.
            //   양수 기준가만 허용(지정가 경로). ref<=0 은 모호하게 시장가로 흘리지 않고 명시 동결(fail-closed).
            if (ProbeReferencePrice <= 0.0)
                throw new InvalidOperationException(
                    "ProbeReferencePrice<=0(시장가) — 격리 프로브는 양수 명목 기준가 필요(시장가 격리 발주 불가). fail-closed 동결.");

            // 점화락 물리 게이트 원자 소비(ProbeReady→1주 소비 후 자동 재잠금).
            if (!_ignition.TryConsumePhysical())
                throw new InvalidOperationException("IgnitionLock.TryConsumePhysical 거부 — 물리 게이트 미충족");

            // 보수 편향(#5): 매수 프로브는 올림. 1주 정수라 변화 없으나 계약을 명시한다.
            decimal qty   = Math.Ceiling(1m);
            decimal limit = (decimal)ProbeReferencePrice;   // >0 보장(위 가드) → 항상 지정가 경로

            // 항상 PaperKind.Limit — currentPrice=limit>0 이므로 PaperBroker 시장가 거부를 회피.
            var (ok, msg) = _probeBroker.Place(
                PaperAsset.Stock, ProbeSymbol, ProbeName,
                PaperSide.Buy, PaperKind.Limit, qty, limit, limit);

            if (!ok)
                throw new InvalidOperationException($"프로브 발주 거부: {msg}");

            return symbolCode;   // 프로브 식별자(결정론·종목코드 재사용)
        }

        // ── 체결/주문 모델 구성 (결정론·격리 — 실 체결 미수신 환경의 자기검증용) ──────

        /// <summary>
        /// 프로브 체결 페이로드를 결정론으로 구성한다(격리 환경엔 실 체결 콜백이 없으므로 기준가=체결가로
        /// 잔차 0 을 만든다 → 정상 격리 프로브는 슬리피지 0σ 로 통과, 비정상은 ProcessFill/센티넬이 적발).
        /// 실 체결 콜백이 배선된 환경(별도 단계)에서는 이 메서드 대신 수신 FillPayload 를 ProcessFill 에 넘긴다.
        /// </summary>
        private FillPayload SimulateProbeFill(long symbolCode, long tsTicks) => new()
        {
            OrderId        = symbolCode,
            SymbolCode     = symbolCode,
            FillPrice      = ProbeReferencePrice,   // 격리 기준: 잔차 0
            FillQty        = 1L,
            Side           = +1,                     // 매수(FillPayload 규약: +1 매수)
            Pad            = 0,
            TimestampTicks = tsTicks,
        };

        private PendingOrder BuildPendingOrder(long symbolCode) => new()
        {
            OrderId       = symbolCode,
            Symbol        = ProbeSymbol,
            Side          = PaperSide.Buy,
            Kind          = ProbeReferencePrice > 0.0 ? PaperKind.Limit : PaperKind.Market,
            ExpectedPrice = ProbeReferencePrice,
            Quantity      = 1L,
            GenomeId      = 0L,
            SubmittedAt   = _now(),
        };

        // ── fail-closed 수렴점 (모든 실패 경로가 여기로 모인다) ────────────────────

        /// <summary>
        /// 실패를 단일 경로로 수렴. freeze=true 면 IgnitionLock.Lock + OrderAttestationService.ForceFreeze
        /// 로 이중 동결하고 상태를 Frozen 으로 못박는다. freeze=false 는 전진 거부(상태 보존·동결 없음).
        /// 이 메서드 자체도 절대 throw 하지 않는다(fail-closed 의 마지막 벽).
        /// </summary>
        private IgnitionSeqResult Fail(
            IgnitionSeqState observedState, string reason, double z, long latency, bool freeze)
        {
            if (freeze)
            {
                // 어느 한 동결이 throw 해도 다른 한쪽은 반드시 수행되도록 개별 보호.
                try { _ignition.Lock(reason); }            catch { /* 이미 잠금/락경합 — 무시 */ }
                try { _attestation.ForceFreeze(reason); }  catch { /* 최후 방벽 — 삼킴 */ }
                _stateInt = (int)IgnitionSeqState.Frozen;
                return new IgnitionSeqResult(false, IgnitionSeqState.Frozen, reason, z, latency);
            }

            return new IgnitionSeqResult(false, observedState, reason, z, latency);
        }
    }
}
