// ============================================================================
// BrokerLivenessPreFlight — v12.18.10 Live Session Pre-Flight Sentinel(브로커 생존성 사전 진단 + 인터락 선제 래치)
//
//  왜 필요한가:
//   호스트 기동 직후 키움 프록시 링크 · KIS 토큰/REST 가 실제로 살아 있는지 확인하기 전에 무장 임대(AtomicArmingLease)가 갱신될 수 있었다.
//   기존 인터락(비트 25 키움 링크 · 비트 19 KIS 세션)은 '결선 후 단절'을 잡지만, '처음부터 응답 없음'을 기동 시한 안에 판정하는 단계가 없었다.
//
//  설계:
//   · 브로커 레인(0 키움 · 1 KIS — /aidash 브로커 인덱스와 같음)마다 비파괴 프로브 델리게이트 1개(null = 미결선 = NotConfigured · 비차단).
//     프로브는 읽기 전용이어야 한다(키움 = 실 COM 프록시 링크 + 하트비트 판독 · KIS = 토큰 발급 + 잔고 조회 GET). 주문/취소 전문 경로 0.
//   · 레인별 시한(기본 3초) — 프로브가 취소를 무시하고 영영 응답하지 않아도 WaitAsync 가 시한에서 끊는다.
//   · ★선제 래치: 레인 판정이 Healthy 가 아닌 순간 '그 레인의 continuation 안에서' 즉시 래치(다른 레인 대기 0 · 반환 전 차단).
//       키움 실패 → 비트 25(KiwoomBridgeLink · 사전 진단 sticky 래치) + 비트 14(AccountDivergence · 계좌 원장 판독 불가 = 괴리 간주)
//       KIS 실패  → 비트 14
//     비트 14/25 는 이미 각 인터락이 단독 결속하므로(Bind 재결속 거부) 소유 인터락의 sticky 경로로만 올린다 — 해제 = 물리 이중키 재무장.
//   · 판정 상태(Pending → Passed/Failed) 는 GenesisPreFlightUpdater 가 임대 갱신 조건으로 읽는다: Passed 전에는 무장 임대 갱신 0
//     (발주 엔진이 가동되기 전 차단). Failed = sticky(TryRearm 은 물리 이중키 확인 후 Pending 복귀 → 재진단 필요).
//   · 진단 통과 후 핑퐁 계측(RecordPingPong) = Interlocked 만(δ=0) — 레인별 누계 · 최근 · 최대 · 시한 초과 건수.
//
//  불변: liveArmed:false(이 타입은 차단만 올린다 — IBroker 참조 0 · 송출 경로 0) · 핑퐁/판독 δ=0 · 프로브(콜드 비동기)만 할당.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Adapters;

namespace StockManager.Blazor
{
    /// <summary>레인 판정 — NotConfigured(미결선 · 비차단) · Healthy · Timeout(시한 초과) · Invalid(토큰/로그인 무효 · 링크 미수립) · Fault(예외).</summary>
    public enum BrokerProbeVerdict : byte { NotConfigured = 0, Healthy = 1, Timeout = 2, Invalid = 3, Fault = 4, Pending = 5 }

    /// <summary>사전 진단 단계 — Pending(미판정 · 무장 임대 갱신 0) · Passed · Failed(sticky).</summary>
    public enum PreFlightLivenessState : byte { Pending = 0, Passed = 1, Failed = 2 }

    /// <summary>사전 진단 1회 결과(값 타입 · 무할당 판독).</summary>
    public readonly struct BrokerLivenessReport
    {
        public readonly BrokerProbeVerdict Kiwoom, Kis;
        public readonly PreFlightLivenessState State;
        public readonly long ElapsedMicros;
        public BrokerLivenessReport(BrokerProbeVerdict kiwoom, BrokerProbeVerdict kis, PreFlightLivenessState state, long elapsedMicros)
        { Kiwoom = kiwoom; Kis = kis; State = state; ElapsedMicros = elapsedMicros; }
        public bool Passed => State == PreFlightLivenessState.Passed;
    }

    public sealed class BrokerLivenessPreFlight
    {
        public const int LaneKiwoom = 0, LaneKis = 1, Lanes = 2;
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
        /// <summary>AccountDivergenceInterlock 에 남기는 원인 표식(종목 코드 자리 · 실 종목과 겹치지 않는 음수).</summary>
        public const long PreFlightSymbolCode = -1;

        private readonly Func<CancellationToken, Task<BrokerProbeVerdict>>?[] _probes = new Func<CancellationToken, Task<BrokerProbeVerdict>>?[Lanes];
        private readonly AccountDivergenceInterlock? _account;
        private readonly KiwoomBridgeLinkInterlock? _kiwoomLink;
        private readonly TimeSpan _timeout;
        private readonly long _timeoutRttTicks;
        private readonly Func<long> _nowTicks;
        private readonly Action<int, BrokerProbeVerdict>? _onLatch;

        private int _state, _probing;
        private readonly int[] _verdicts = { (int)BrokerProbeVerdict.Pending, (int)BrokerProbeVerdict.Pending };
        private long _probeRuns, _latches, _elapsedMicros, _latchedAtTicks;
        private readonly long[] _pingCount = new long[Lanes], _pingLastMicros = new long[Lanes], _pingMaxMicros = new long[Lanes], _pingOverruns = new long[Lanes];

        /// <param name="kiwoomProbe">키움 레인 비파괴 프로브(null = 연결 비활성 · 비차단).</param>
        /// <param name="kisProbe">KIS 레인 비파괴 프로브(null = 미설정 · 비차단).</param>
        /// <param name="account">비트 14 소유 인터락(실패 시 sticky Trip).</param>
        /// <param name="kiwoomLink">비트 25 소유 인터락(키움 실패 시 사전 진단 sticky 래치).</param>
        /// <param name="onLatch">래치 통지(레인 · 판정) — 비트 반영 '뒤' 호출 · 예외 흡수.</param>
        public BrokerLivenessPreFlight(Func<CancellationToken, Task<BrokerProbeVerdict>>? kiwoomProbe, Func<CancellationToken, Task<BrokerProbeVerdict>>? kisProbe,
                                       AccountDivergenceInterlock? account, KiwoomBridgeLinkInterlock? kiwoomLink,
                                       TimeSpan? timeout = null, Func<long>? nowTicks = null, Action<int, BrokerProbeVerdict>? onLatch = null)
        {
            _probes[LaneKiwoom] = kiwoomProbe; _probes[LaneKis] = kisProbe;
            _account = account; _kiwoomLink = kiwoomLink; _onLatch = onLatch;
            _timeout = timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;
            _timeoutRttTicks = _timeout.Ticks;
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
        }

        public PreFlightLivenessState State => (PreFlightLivenessState)Volatile.Read(ref _state);
        /// <summary>진단 통과(임대 갱신 허용 조건).</summary>
        public bool Cleared => Volatile.Read(ref _state) == (int)PreFlightLivenessState.Passed;
        public TimeSpan Timeout => _timeout;
        public BrokerProbeVerdict Verdict(int lane) => (uint)lane < Lanes ? (BrokerProbeVerdict)Volatile.Read(ref _verdicts[lane]) : BrokerProbeVerdict.NotConfigured;
        public long Probes => Interlocked.Read(ref _probeRuns);
        public long Latches => Interlocked.Read(ref _latches);
        public long ElapsedMicros => Interlocked.Read(ref _elapsedMicros);
        /// <summary>마지막 래치 시각(주입 시계 틱 · 0 = 래치 없음).</summary>
        public long LatchedAtTicks => Interlocked.Read(ref _latchedAtTicks);

        public long PingCount(int lane) => (uint)lane < Lanes ? Interlocked.Read(ref _pingCount[lane]) : 0;
        public long PingLastMicros(int lane) => (uint)lane < Lanes ? Interlocked.Read(ref _pingLastMicros[lane]) : 0;
        public long PingMaxMicros(int lane) => (uint)lane < Lanes ? Interlocked.Read(ref _pingMaxMicros[lane]) : 0;
        public long PingOverruns(int lane) => (uint)lane < Lanes ? Interlocked.Read(ref _pingOverruns[lane]) : 0;

        public BrokerLivenessReport Snapshot()
            => new(Verdict(LaneKiwoom), Verdict(LaneKis), State, ElapsedMicros);

        /// <summary>
        /// 비파괴 생존성 진단 1회(콜드 · 비동기). 레인 병렬 · 레인별 시한 · 실패 레인은 자기 continuation 에서 즉시 래치.
        /// 중복 호출(진단 중) = 진행 중 판정 대기 없이 현재 스냅샷 반환. Failed 상태에서는 재진단하지 않는다(sticky — TryRearm 선행).
        /// </summary>
        public async Task<BrokerLivenessReport> ProbeAsync(CancellationToken ct = default)
        {
            if (Volatile.Read(ref _state) == (int)PreFlightLivenessState.Failed) return Snapshot();
            if (Interlocked.CompareExchange(ref _probing, 1, 0) != 0) return Snapshot();
            try
            {
                Interlocked.Increment(ref _probeRuns);
                long start = Stopwatch.GetTimestamp();
                Volatile.Write(ref _verdicts[LaneKiwoom], (int)BrokerProbeVerdict.Pending);
                Volatile.Write(ref _verdicts[LaneKis], (int)BrokerProbeVerdict.Pending);
                var kiwoom = RunLane(LaneKiwoom, ct);
                var kis = RunLane(LaneKis, ct);
                await Task.WhenAll(kiwoom, kis).ConfigureAwait(false);
                Interlocked.Exchange(ref _elapsedMicros, (Stopwatch.GetTimestamp() - start) * 1_000_000L / Stopwatch.Frequency);
                bool ok = IsPass(kiwoom.Result) && IsPass(kis.Result);
                // Failed 는 RunLane 래치에서 이미 기록 — 통과만 Pending → Passed(래치와 경합해도 Failed 를 덮지 않음)
                if (ok) Interlocked.CompareExchange(ref _state, (int)PreFlightLivenessState.Passed, (int)PreFlightLivenessState.Pending);
                return Snapshot();
            }
            finally { Volatile.Write(ref _probing, 0); }
        }

        private async Task<BrokerProbeVerdict> RunLane(int lane, CancellationToken ct)
        {
            var probe = _probes[lane];
            BrokerProbeVerdict v;
            if (probe is null) v = BrokerProbeVerdict.NotConfigured;
            else
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_timeout);
                try
                {
                    v = await probe(cts.Token).WaitAsync(_timeout, ct).ConfigureAwait(false);
                    if (v is BrokerProbeVerdict.NotConfigured or BrokerProbeVerdict.Pending) v = BrokerProbeVerdict.Invalid;   // 결선된 레인의 '미판정' 응답 = 무효
                }
                catch (TimeoutException) { v = BrokerProbeVerdict.Timeout; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { v = BrokerProbeVerdict.Timeout; }   // 프로브가 시한 토큰으로 끊김
                catch (OperationCanceledException) { v = BrokerProbeVerdict.Fault; }                                        // 호스트 종료 취소 = 미판정 = 실패
                catch (Exception) { v = BrokerProbeVerdict.Fault; }
            }
            Volatile.Write(ref _verdicts[lane], (int)v);
            if (!IsPass(v)) Latch(lane, v);   // ★다른 레인 대기 없이 즉시(반환 전 차단)
            return v;
        }

        private static bool IsPass(BrokerProbeVerdict v) => v is BrokerProbeVerdict.Healthy or BrokerProbeVerdict.NotConfigured;

        private void Latch(int lane, BrokerProbeVerdict v)
        {
            Volatile.Write(ref _state, (int)PreFlightLivenessState.Failed);
            if (lane == LaneKiwoom) _kiwoomLink?.LatchPreFlight();
            _account?.Trip(PreFlightSymbolCode, -(long)v);   // 브로커 수량 자리 = −판정 코드(운용자 원인 식별)
            Interlocked.Exchange(ref _latchedAtTicks, _nowTicks());
            Interlocked.Increment(ref _latches);
            try { _onLatch?.Invoke(lane, v); } catch (Exception) { }
        }

        /// <summary>
        /// [진단 통과 후 · δ=0] 핑퐁 왕복 1건 기록(100ns 틱). 시한 초과는 누계만(차단은 기존 세션 인터락 소유 — 이중 래치 금지).
        /// </summary>
        public void RecordPingPong(int lane, long rttTicks100ns)
        {
            if ((uint)lane >= Lanes || rttTicks100ns < 0) return;
            long us = rttTicks100ns / 10L;
            Interlocked.Increment(ref _pingCount[lane]);
            Interlocked.Exchange(ref _pingLastMicros[lane], us);
            long max = Interlocked.Read(ref _pingMaxMicros[lane]);
            while (us > max)
            {
                long seen = Interlocked.CompareExchange(ref _pingMaxMicros[lane], us, max);
                if (seen == max) break;
                max = seen;
            }
            if (rttTicks100ns > _timeoutRttTicks) Interlocked.Increment(ref _pingOverruns[lane]);
        }

        /// <summary>물리 이중키 확인 후 Failed → Pending(재진단 필요 · 비트 14/25 해제는 각 소유 인터락의 재무장 경로).</summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Interlocked.CompareExchange(ref _state, (int)PreFlightLivenessState.Pending, (int)PreFlightLivenessState.Failed);
            return true;
        }
    }
}
