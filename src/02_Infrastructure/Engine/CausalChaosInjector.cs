// ============================================================================
// CausalChaosInjector — v6.1.0 Path B: L3 극단 변동성 & CausalWeave 카오스 주입기
//
//  StatePlaybackSimulator 의 '적대적 특수 모드' 동반 엔진. 결정론 재생과 달리, 이 주입기는 의도적으로
//  파국을 만들어 CausalWeave 방어선을 적대 검증한다:
//    (1) L3 마이크로-버스트 폭주 — 초당 1,000만 건(10M MPS) 급 합성 UDP 프레임 폭풍을 CausalWeaveRing 에
//        쏟아부어, 소비 지연 시 newest-wins 회수가 '정확히·증명 가능'하게 계수되는지(조용한 드롭 0),
//        락-프리 경합에서 데드락/메모리 누수(δ=0) 없이 완주하는지 검증한다.
//    (2) Time-Warp(QPC 역행) 오염 — 과거 QPC 프레임을 주입해 CausalWeaveClock 퓨즈가 즉시 fail-closed
//        트립(ForceFreeze ∧ Downgrade)하고, 이후 어떤 전진도 '비가역' 거부(재시작 전 자동복구 불가)함을 검증한다.
//
//  ★불변 제약 준수:
//   · δ=0 — 주입 핫패스는 사전할당 _frameScratch 재사용 + 스팬 직기입 + 값타입 반환. new/box/LINQ/캡처 0.
//   · 결정론 — 주입 QPC 는 인자로 결정(단조 증가), 시각은 주입 시계(_nowTicks)로 계측만.
//   · liveArmed:false — 순수 인바운드 합성 발행. IBroker/소켓/onSend/PublishRaw 참조 0(구조적 격리).
//   · 퓨즈 비가역 — 트립 유발만 하고, 복구 경로를 '갖지 않는다'(자동복구 불가는 CausalWeaveClock 이 물리 강제).
//   · unsafe 미사용(관리형 배킹). ★한국색상은 UI 계층. 여기선 상태·계수만 산출.
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>마이크로-버스트 폭주 결과(값타입·힙 0). 처리량은 계측치(하드웨어 의존).</summary>
    public readonly struct MicroBurstResult
    {
        public readonly long Published;          // 이번 폭주로 링에 발행된 프레임 수
        public readonly long Evictions;          // 이번 폭주로 증명 회수(newest-wins)된 수
        public readonly long ElapsedTicks;       // 소요(QPC tick)
        public readonly long PacketsRequested;   // 요청 폭주량
        public MicroBurstResult(long published, long evictions, long elapsedTicks, long requested)
        { Published = published; Evictions = evictions; ElapsedTicks = elapsedTicks; PacketsRequested = requested; }

        /// <summary>초당 주입 처리량(계측·하드웨어 의존). ElapsedTicks==0 이면 0.</summary>
        public double PacketsPerSecond => ElapsedTicks > 0 ? PacketsRequested * (double)Stopwatch.Frequency / ElapsedTicks : 0.0;
        public double MillionPerSecond => PacketsPerSecond / 1_000_000.0;
    }

    /// <summary>Time-Warp 오염 주입 결과(값타입·힙 0).</summary>
    public readonly struct TimeWarpResult
    {
        public readonly bool FuseTripped;        // 역행 주입으로 퓨즈가 실제 트립했는가
        public readonly long WarpEvents;         // 시계가 포착한 타임-와프 시도 누적
        public readonly long ForwardAccepted;    // 트립 전 단조 전진 수용 수
        public readonly long WatermarkAtTrip;    // 트립 직전 워터마크(포렌식)
        public TimeWarpResult(bool tripped, long warp, long fwd, long wm)
        { FuseTripped = tripped; WarpEvents = warp; ForwardAccepted = fwd; WatermarkAtTrip = wm; }
    }

    /// <summary>CausalWeave 적대적 카오스 주입기(마이크로-버스트 + Time-Warp). 순수 관리형·δ=0.</summary>
    public sealed class CausalChaosInjector
    {
        private const int    DefaultFrame = 64;
        private const uint   BurstMagic   = 0xB00B1E5Cu;   // 마이크로-버스트 프레임 매직(포렌식 식별)

        private readonly CausalWeaveRing _ring;
        private readonly Func<long>      _nowTicks;
        private readonly byte[]          _frameScratch;    // 사전할당 프레임(δ=0 — 루프 내 재사용)

        public CausalChaosInjector(CausalWeaveRing ring, Func<long>? nowTicks = null, int frameSize = DefaultFrame)
        {
            _ring     = ring ?? throw new ArgumentNullException(nameof(ring));
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            if (frameSize < 16) frameSize = 16;
            _frameScratch = new byte[frameSize];           // 콜드 1회 — 이후 재사용
        }

        // 단일 합성 프레임 봉인·발행(δ=0). 헤더=[i32 seq LE | u32 magic LE | i64 qpc LE] — 스팬 직기입.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EmitOne(long seq, long qpc)
        {
            var span = _frameScratch.AsSpan();
            BinaryPrimitives.WriteInt32LittleEndian(span, unchecked((int)seq));
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), BurstMagic);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(8), qpc);
            _ring.Publish(span, qpc);   // 오버런 시 링이 newest-wins 증명 회수(조용한 드롭 0)
        }

        /// <summary>
        /// L3 마이크로-버스트 폭주 — <paramref name="packetCount"/> 개 합성 프레임을 단조 QPC 로 CausalWeaveRing 에
        /// 쏟아붓는다(δ=0 핫패스). 소비자 없이 호출하면 링 용량 초과분이 newest-wins 로 증명 회수되어
        /// Evictions 로 '정확히' 계수된다(무소비 시 Evictions == packetCount - capacity).
        /// </summary>
        public MicroBurstResult MicroBurst(long packetCount, long qpcStart = 1, long qpcStep = 1, CancellationToken ct = default)
        {
            if (packetCount < 0) packetCount = 0;
            long pub0 = _ring.Published, ev0 = _ring.Evictions;
            long t0   = _nowTicks();
            long qpc  = qpcStart;
            for (long i = 0; i < packetCount; i++)
            {
                EmitOne(i + 1, qpc);
                qpc += qpcStep;
                if ((i & 0xFFFFF) == 0 && ct.IsCancellationRequested) break;   // 취소 폴(백만마다·무할당)
            }
            long elapsed = _nowTicks() - t0;
            return new MicroBurstResult(_ring.Published - pub0, _ring.Evictions - ev0, elapsed, packetCount);
        }

        /// <summary>
        /// ★Time-Warp 적대적 오염 — 인과 시계에 <paramref name="forwardSteps"/> 프레임을 단조 시퀀스로 전진시킨 뒤,
        /// 과거 시퀀스(<paramref name="regressionSeq"/> &lt; 시퀀스 워터마크) 1발을 주입한다. 퓨즈가 즉시 fail-closed
        /// 트립(재정렬/손상/변조 = 진짜 인과 위반)하고 이후 전진이 비가역 거부됨은 호출자가 단언. δ=0.
        /// (봉인 QPC 는 다중 생산자 로거의 seq-claim↔QPC-sample 선점으로 정상 로그도 국소 역전하므로 퓨즈 기준이 아니다.)
        /// </summary>
        public TimeWarpResult InjectTimeWarp(CausalWeaveClock clock, long firstSeq, int forwardSteps, long stride, long regressionSeq)
        {
            if (clock is null) throw new ArgumentNullException(nameof(clock));
            clock.EnterPlayback(0);   // 인과 시계 플레이백 앵커(QPC 자문 시각 시드 + 시퀀스 워터마크 리셋)
            long accepted = 0;
            for (int i = 0; i < forwardSteps; i++)
                if (clock.AdvanceSeq(firstSeq + (long)i * stride)) accepted++;
            long wmAtTrip = clock.SeqWatermark;
            bool tripped  = !clock.AdvanceSeq(regressionSeq);   // 시퀀스 역행 → 트립 기대(AdvanceSeq false)
            return new TimeWarpResult(clock.FuseTripped && tripped, clock.WarpEvents, accepted, wmAtTrip);
        }
    }
}
