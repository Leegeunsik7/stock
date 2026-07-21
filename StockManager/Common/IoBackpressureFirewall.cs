// ============================================================================
// IoBackpressureFirewall — v5.5.0 ★Think Beyond Gemini: 결정론적 I/O 역류 방화벽 & 자가계측 페이지폴트 프로브
//
//  ── 아키텍처 논증(제미나이 초월) ────────────────────────────────────────────
//  제미나이 A(디스크 목졸림 드롭 게이트)와 B(페이지폴트 VirtualLock 가드)는 각각 반응형이다. 본 설계는 장기 상주 HFT 터미널의
//  두 파국(디스크 I/O flush stall·페이지폴트 thrashing)을 ★근본 다른 두 감지기로 분리 방어하고 응답 강도를 차등화한다:
//   ① 디스크 stall 워치독(백프레셔 역류 차단): 로거의 언매니지드 링은 이미 '드롭-오래된-것 무대기'라 디스크 stall 이 핫패스로
//      역전파되지 않는다(구조적 방어). 그 위에, 로거 큐 깊이가 고수위인데 백그라운드 I/O 진행이 stall 데드라인을 넘으면 =
//      디스크 쓰기 목졸림 → OrderAttestationService.ForceFreeze(진짜 파국·수동 해제 정당·fail-closed 1회 래치).
//   ② ★자가계측 페이지폴트 인-시투 프로브(제미나이 B 초월): VirtualLock(P/Invoke·OS 의존·한도) 대신, 전용 상주 페이지 영역을
//      콜드 스레드가 주기 터치하고 그 지연을 Stopwatch(QPC)로 브래킷한다. 메모리 압박으로 페이지가 스왑아웃되면 터치가 hard
//      page fault 를 유발해 지연이 스파이크 → thrashing 결정론 탐지. OS 락 없이, 실제 메모리 상주 상태를 '측정'으로 포착.
//      thrashing 은 일시적이고 타이밍 휴리스틱이라 ★가역 안전 홀드(자기 만료)만 — 비가역 Downgrade 격발 금지(오탐이 영구 후퇴 유발 방지).
//      GC 가 낀 샘플은 폐기(선점/GC pause 를 폴트로 오인 방지). 지속 thrashing 은 홀드가 계속 재격발되어 연속 홀드로 자율 수렴.
//  ▷ 초월점: '락으로 예방'(B)이 아니라 '측정으로 탐지'하며, 디스크 stall(파국·동결)과 페이지 thrashing(일시·가역 홀드)을
//    응답 강도로 분리한다. 두 감지기 모두 콜드 스레드 전용 — 핫패스(발주 드레인)는 단 1ns 도 건드리지 않는다(δ=0 불변).
//
//  ── liveArmed:false / 결정론 ───────────────────────────────────────────────
//  IBroker·소켓 참조 0. 홀드는 무장 술어를 '더 안전한 방향'으로만 억제(HoldActive → blockWhile). 디스크 stall 동결은 기존
//  ForceFreeze 경로 재사용(신규 무장/해제 경로 0). I/O·메모리 지터는 인프라 관측 — 트레이딩 판정 입력 무유입.
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>I/O 역류 방화벽 — 디스크 stall(→ForceFreeze) + 자가계측 페이지폴트 thrashing(→가역 홀드/Downgrade) 분리 방어.</summary>
    public sealed unsafe class IoBackpressureFirewall : IDisposable
    {
        private readonly Func<long>? _queueDepth;         // 로거 미소비 큐 깊이
        private readonly Func<long>? _millisSinceDrain;   // 로거 백그라운드 I/O 마지막 진행 후 경과(ms)
        private readonly long _queueHighWater;            // 큐 고수위(이 이상 + 진행 stall = 목졸림)
        private readonly long _stallDeadlineMs;           // 디스크 진행 stall 데드라인

        private readonly byte* _probe;                    // 페이지폴트 프로브 상주 영역(오프힙)
        private readonly int   _probeBytes;
        private readonly int   _pageStride = 4096;        // OS 페이지(4KB) 단위 터치
        private readonly long  _faultDeadlineTicks;       // 프로브 터치 지연 > 이거면 페이지 폴트 의심
        private readonly long  _holdTicks;                // 가역 홀드 지속

        private readonly Func<long> _nowTicks;
        private readonly long _tickFreq;
        private readonly OrderAttestationService? _att;
        private readonly MmfFlightRecorder? _blackbox;
        private System.Threading.Timer? _timer;
        private int _disposed;

        private long _pageFaultSpikes, _lastTouchMicros, _pageHoldUntilTicks, _diskStalls;
        private int  _diskStallLatched;
        private byte _probeAccumulator;

        public IoBackpressureFirewall(
            Func<long>? queueDepth = null, Func<long>? millisSinceDrain = null,
            OrderAttestationService? attestation = null, MmfFlightRecorder? blackbox = null,
            long queueHighWater = 32768, long stallDeadlineMs = 1000, double faultDeadlineMs = 3.0,
            double holdMs = 300.0, int probeBytes = 1 << 20, int pollMs = 50,
            Func<long>? nowTicks = null, long tickFrequency = 0)
        {
            _queueDepth = queueDepth; _millisSinceDrain = millisSinceDrain;
            _att = attestation; _blackbox = blackbox;
            _queueHighWater = queueHighWater; _stallDeadlineMs = stallDeadlineMs;
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            _tickFreq = tickFrequency > 0 ? tickFrequency : Stopwatch.Frequency;
            _faultDeadlineTicks = (long)(faultDeadlineMs / 1000.0 * _tickFreq);
            _holdTicks = (long)(Math.Max(1.0, holdMs) / 1000.0 * _tickFreq);
            _probeBytes = probeBytes < _pageStride ? _pageStride : probeBytes;
            _probe = (byte*)NativeMemory.AllocZeroed((nuint)_probeBytes);
            int pm = pollMs < 1 ? 1 : pollMs;
            _timer = new System.Threading.Timer(_ => { try { Poll(); } catch { } }, null, pm, pm);
        }

        // ── 락프리 텔레메트리(Sample() pull) ──
        public bool PageThrashHoldActive => _nowTicks() < Interlocked.Read(ref _pageHoldUntilTicks);   // 가역
        public bool DiskStallFrozen      => Volatile.Read(ref _diskStallLatched) != 0;                 // 래치(파국)
        public bool HoldActive           => PageThrashHoldActive || DiskStallFrozen;
        public long PageFaultSpikes      => Interlocked.Read(ref _pageFaultSpikes);
        public long LastTouchMicros      => Interlocked.Read(ref _lastTouchMicros);
        public long DiskStalls           => Interlocked.Read(ref _diskStalls);
        /// <summary>포렌식 상태 코드 — 0 정상·1 페이지 홀드·2 디스크 stall 동결.</summary>
        public long StatusCode => DiskStallFrozen ? 2L : (PageThrashHoldActive ? 1L : 0L);

        private void Poll()
        {
            if (Volatile.Read(ref _disposed) != 0) return;   // ★적대검증(D1) — Dispose 시작 후 프로브 메모리 접근 금지(UAF 방어·in-flight bail)
            long now = _nowTicks();
            CheckDiskStall();

            // ② ★자가계측 페이지폴트 프로브 — 상주 영역을 페이지 단위 터치하며 지연 브래킷. 스왑아웃된 페이지면 hard fault 로 스파이크.
            //   ★적대검증 교정(D5) — GC 가드: 터치 구간에 GC 가 끼면(pause 로 지연 부풀림) 그 샘플은 폴트로 오인하지 않고 폐기(스케줄러/GC 오탐 제거).
            int g0 = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
            long t0 = _nowTicks();
            byte acc = _probeAccumulator;
            for (int off = 0; off < _probeBytes; off += _pageStride)
            {
                acc = unchecked((byte)(acc + _probe[off]));   // 읽기(폴트 유발) + …
                _probe[off] = unchecked((byte)(acc ^ 0x5A)); // …쓰기(dirty·상주 유지) — JIT 소거 방지
            }
            _probeAccumulator = acc;
            long touch = _nowTicks() - t0;
            int g1 = GC.CollectionCount(0) + GC.CollectionCount(1) + GC.CollectionCount(2);
            Interlocked.Exchange(ref _lastTouchMicros, touch * 1_000_000L / _tickFreq);

            // GC-clean 샘플만 폴트로 판정. ★타이밍 휴리스틱으로 비가역 Downgrade 격발 금지 — 가역 안전 홀드만(자기 만료). 지속 thrashing 이면
            //   홀드가 계속 재격발되어 연속 홀드로 수렴(자율 회복). 오탐(선점)이어도 짧은 홀드라 무해. 진짜 파국(디스크 stall)만 ForceFreeze(위 CheckDiskStall).
            if (g1 == g0 && touch > _faultDeadlineTicks)
            {
                Interlocked.Increment(ref _pageFaultSpikes);
                Interlocked.Exchange(ref _pageHoldUntilTicks, now + _holdTicks);   // 가역 안전 홀드(자기 만료)
                try { _blackbox?.Record(FlightEventKind.PageFaultThrash, 0L, touch, _faultDeadlineTicks, 0L, 92); } catch { }
            }
        }

        // ① 디스크 I/O stall — 큐 고수위인데 백그라운드 진행이 데드라인 초과 = 쓰기 목졸림. 1회 래치·ForceFreeze(파국·fail-closed).
        private void CheckDiskStall()
        {
            if (_queueDepth is not { } qd || _millisSinceDrain is not { } md) return;
            long depth = qd(); long since = md();
            if (depth >= _queueHighWater && since >= _stallDeadlineMs
                && Interlocked.CompareExchange(ref _diskStallLatched, 1, 0) == 0)
            {
                Interlocked.Increment(ref _diskStalls);
                try { _blackbox?.Record(FlightEventKind.DiskStallFreeze, 0L, depth, since, 0L, 91); } catch { }
                try { _att?.ForceFreeze($"IoBackpressureFirewall: Disk I/O Stall Detected — 큐 {depth}·정체 {since}ms"); } catch { }
            }
        }

        /// <summary>테스트 전용 — 디스크 stall 판정 1회 실행(타이머 우회).</summary>
        public void CheckDiskStallForTest() => CheckDiskStall();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // ★적대검증 교정(D1) — Timer.Dispose(WaitHandle) 로 진행 중 Poll 완료를 대기한 뒤 프로브 해제(use-after-free 차단).
            //   파라미터리스 Dispose 는 in-flight 콜백을 기다리지 않아, 프로브 터치 루프 중 Free 가 겹치면 해제 메모리 접근 → AV/오염.
            var t = _timer; _timer = null;
            if (t is not null)
            {
                using var done = new ManualResetEvent(false);
                try { if (!t.Dispose(done)) done.Set(); done.WaitOne(2000); } catch { }
            }
            try { NativeMemory.Free(_probe); } catch { }
        }
    }
}
