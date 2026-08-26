// ============================================================================
// LatencyTelemetry — v4.84.0 Path Gamma: 초고속 IPC 레이턴시 + FX 변동성 관제 텔레메트리
//
//  20ms/50ms IPC 핑퐁(KiwoomBridgeClient)과 3σ FX 서킷 브레이커(QFxCircuitBreaker)의 실시간 지표를,
//  UI 렌더링 스레드가 '무할당·락-프리'로 읽어 전술 관제 패널(LatencyMonitor.razor)에 비춘다.
//  RingTelemetry(S29 Path Gamma) 검증 idiom 을 레이턴시 프레임 전용으로 미러링한다(재발명 아님).
//
//  ── seqlock(단일 라이터) ─────────────────────────────────────────────────────
//   라이터(LatencyTelemetryPublisher.Sample)는 홀(쓰기 시작)→payload→짝(완료). 리더(UI)는 v1(짝)→
//   payload 복사→v2 재독출→v1==v2 검증으로 torn read 격리. 경합 시 직전 프레임 유지(코어 무블록).
//   ★제네릭 미사용: [StructLayout(Explicit)] Cell 의 구체 Data 에 FieldOffset 을 박아야 해 제네릭 불가.
//
//  ── fail-closed 고지 ─────────────────────────────────────────────────────────
//   FxBreakerState != 0(Stale/Violated) 이면 UI 가 청산 게이트 래치를 비상 고지한다. 미배선(핑퐁 미연결·
//   FX 미급전)은 전부 0 스냅샷 → UI 가 '미배선/데이터 없음'으로 정직 표기(거짓 정상 표시 금지).
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>UI 전달용 무할당 blittable 레이턴시/변동성 스냅샷(참조 0·힙 0). 전 필드 raw 정수(ticks/10^8).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct LatencySnapshot
    {
        public readonly long CurrentIpcRttTicks;   // 최근 Pong RTT(100ns ticks)
        public readonly long PeakIpcRttTicks;      // 관측 최대 RTT(100ns ticks)
        public readonly long TotalPingSent;        // 누적 Ping 송신 수
        public readonly long TotalPongReceived;    // 누적 Pong 수신 수
        public readonly long LastFxRateRaw;        // 최근 수용 환율(QFixed raw·10^8)
        public readonly long FxMovingAverageRaw;   // 창(10분) 이동평균(raw)
        public readonly long FxSigmaRaw;           // 1σ 가치(raw)
        public readonly byte FxBreakerState;       // 0 Normal · 1 Stale · 2 Violated(3σ 급변동·비양수·오버플로 총칭)

        public LatencySnapshot(long currentIpcRttTicks, long peakIpcRttTicks,
                               long totalPingSent, long totalPongReceived,
                               long lastFxRateRaw, long fxMovingAverageRaw, long fxSigmaRaw,
                               byte fxBreakerState)
        {
            CurrentIpcRttTicks = currentIpcRttTicks;
            PeakIpcRttTicks    = peakIpcRttTicks;
            TotalPingSent      = totalPingSent;
            TotalPongReceived  = totalPongReceived;
            LastFxRateRaw      = lastFxRateRaw;
            FxMovingAverageRaw = fxMovingAverageRaw;
            FxSigmaRaw         = fxSigmaRaw;
            FxBreakerState     = fxBreakerState;
        }

        // ── 표시/상태 헬퍼(계산 프로퍼티·힙 0) ────────────────────────────────
        /// <summary>IPC 핑퐁 배선/가동 여부(핑 또는 퐁이 1건이라도 관측).</summary>
        public bool    IpcWired         => TotalPingSent > 0 || TotalPongReceived > 0;
        /// <summary>미응답 ping(양수 = 응답 지연/손실 in-flight). 정상은 0 또는 소수.</summary>
        public long    PongDeficit      => TotalPingSent - TotalPongReceived;
        /// <summary>최근 RTT(µs) — 100ns ticks ÷ 10. 표시용.</summary>
        public double  CurrentRttMicros => CurrentIpcRttTicks / 10.0;
        public double  PeakRttMicros    => PeakIpcRttTicks / 10.0;

        /// <summary>FX 피드 배선/가동 여부(환율 또는 이동평균 관측).</summary>
        public bool    FxWired          => LastFxRateRaw > 0 || FxMovingAverageRaw > 0;
        public decimal LastFxRate       => LastFxRateRaw / (decimal)QMath.Scale;
        public decimal FxMovingAverage  => FxMovingAverageRaw / (decimal)QMath.Scale;
        public decimal FxSigma          => FxSigmaRaw / (decimal)QMath.Scale;
        /// <summary>브레이커 개방(래치) 여부 — 청산 게이트 fail-closed 잠금 신호.</summary>
        public bool    FxBreakerOpen    => FxBreakerState != 0;
        public string  FxBreakerStateName => FxBreakerState switch
        {
            0 => "Normal", 1 => "Stale", 2 => "Violated", _ => "Unknown"
        };
        /// <summary>비상 고지 필요(브레이커 개방). UI 오버레이 트리거.</summary>
        public bool    HasAlert         => FxBreakerState != 0;
    }

    /// <summary>단일 라이터 seqlock 채널 — 캐시라인 격리. Publish(샘플러 단일 라이터)/TryRead(UI 락-프리·무할당).</summary>
    public sealed class LatencyTelemetryChannel
    {
        private const int CacheLine = 64;

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct Cell
        {
            [FieldOffset(CacheLine)]     public long            Version;
            [FieldOffset(CacheLine * 2)] public LatencySnapshot Data;
        }

        private int  _writerTid;   // DEBUG 단일 라이터 회귀 가드(릴리스 비용 0)
        private Cell _cell;

        /// <summary>현재 seqlock 커서(진단). 짝=안정, 홀=발행 중.</summary>
        public long Version => Volatile.Read(ref _cell.Version);

        /// <summary>발행 — '단일 라이터'(샘플러)에서만. 홀→payload→짝. 무할당(구조체 복사).</summary>
        public void Publish(in LatencySnapshot t)
        {
            AssertSingleWriter();
            long ver = _cell.Version;
            Volatile.Write(ref _cell.Version, ver + 1);   // 홀 — 발행 시작(리더 무효화)
            _cell.Data = t;                                // payload 복사(x64 store 순서 보존)
            Volatile.Write(ref _cell.Version, ver + 2);   // 짝 — 발행 완료
        }

        /// <summary>락-프리 무할당 독출(UI). torn read 면 최대 8회 재시도, 끝내 경합이면 false+out=default(직전 프레임 유지).</summary>
        public bool TryRead(out LatencySnapshot t)
        {
            for (int i = 0; i < 8; i++)
            {
                long v1 = Volatile.Read(ref _cell.Version);
                if ((v1 & 1L) == 0L)
                {
                    LatencySnapshot local = _cell.Data;
                    long v2 = Volatile.Read(ref _cell.Version);
                    if (v1 == v2) { t = local; return true; }
                }
            }
            t = default;
            return false;
        }

        [Conditional("DEBUG")]
        private void AssertSingleWriter()
        {
            int tid  = Environment.CurrentManagedThreadId;
            int prev = Interlocked.CompareExchange(ref _writerTid, tid, 0);
            if (prev != 0 && prev != tid)
                throw new InvalidOperationException(
                    "LatencyTelemetryChannel: 단일 라이터 위반 — 둘째 Publish 스레드 감지(seqlock torn write 위험).");
        }
    }

    /// <summary>
    /// KiwoomBridgeClient(IPC RTT) + QFxCircuitBreaker(FX 통계)를 주기 스냅샷해 채널에 발행하는 '단일 라이터'
    /// 샘플러. 둘 다 옵션(미배선이면 해당 필드 0). UI 는 채널만 TryRead(단방향). CAS 가드·무할당.
    /// </summary>
    public sealed class LatencyTelemetryPublisher
    {
        private readonly LatencyTelemetryChannel _channel;
        private readonly KiwoomBridgeClient?     _bridge;
        private readonly QFxCircuitBreaker?      _fx;
        private int _sampling;   // 단일 라이터 CAS 가드(동시 Sample 금지)

        public LatencyTelemetryPublisher(LatencyTelemetryChannel channel,
                                         KiwoomBridgeClient? bridge = null,
                                         QFxCircuitBreaker? fx = null)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
            _bridge  = bridge;
            _fx      = fx;
        }

        /// <summary>1회 스냅샷 → 채널 발행(단일 라이터 전용·CAS 가드·무할당). 동시 Sample 은 fail-closed throw.</summary>
        public void Sample()
        {
            if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0)
                throw new InvalidOperationException("LatencyTelemetryPublisher: 동시 Sample 위반 — 단일 라이터 전용");
            try
            {
                long rtt = 0, peak = 0, pingSent = 0, pongRecv = 0;
                if (_bridge is { } b)
                {
                    rtt      = b.CurrentIpcRttTicks;
                    peak     = b.PeakIpcRttTicks;
                    pingSent = b.TotalPingSent;
                    pongRecv = b.TotalPongReceived;
                }

                long lastFx = 0, ma = 0, sigma = 0;
                byte state = 0;
                _fx?.ReadFxStats(out lastFx, out ma, out sigma, out state);

                _channel.Publish(new LatencySnapshot(rtt, peak, pingSent, pongRecv, lastFx, ma, sigma, state));
            }
            finally { Volatile.Write(ref _sampling, 0); }
        }
    }
}
