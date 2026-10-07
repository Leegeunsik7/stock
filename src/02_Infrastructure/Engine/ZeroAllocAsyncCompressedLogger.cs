// ============================================================================
// ZeroAllocAsyncCompressedLogger — v5.5.0 과제1: 무할당 비동기 압축 로깅 패브릭
//
//  핫패스는 고정 크기 blittable BlittableLogEvent(32B)를 ★언매니지드 락프리 링(NativeMemory)에 O(1) 푸시하고 즉시 복귀한다
//  (string.Format/ToString/박싱/할당 0 — Interlocked 클레임 + 포인터 기록 + Volatile 커밋만). 백그라운드 전용 I/O 스레드가
//  커밋 프레임을 차출→Brotli 압축(.NET 내장·의존성 0)→XOR 난독→CRC32 프레임 무결→.ms2log 파일에 비동기 스트리밍 append.
//  50ms 발주 드레인에 단 1ns 지터도 전파되지 않도록, 압축·암호화·디스크 I/O 는 전량 콜드 스레드로 격리한다.
//
//  ── 정직 회계(정찰) ─────────────────────────────────────────────────────────
//  MmfFlightRecorder.Record 의 δ=0 단조 클레임 + Volatile 커밋 이디엄, CrashResilientDumper 의 전용 I/O 스레드·Encode
//  (XorKeystream/CRC32)·atomic write·fail-closed 수렴, JournalFlushPump 의 인터럽터블 대기·Join-후-최종플러시를 재사용.
//  ★신설: 언매니지드 링(NativeMemory.AlignedAlloc·오프힙·GC 압력 0)·스트리밍 롤링 .ms2log·Brotli 압축 단계(코드베이스 부재).
//
//  ── liveArmed:false / δ=0 / 결정론 ─────────────────────────────────────────
//  로거는 IBroker·소켓 참조 0 — 관측/영속 인프라. δ=0 은 핫패스 푸시에만(사전할당 링·Interlocked). 압축/암호/파일은 콜드
//  스레드(사전할당 scratch·드레인마다 new 금지). 시각은 Stopwatch.GetTimestamp(QPC 래퍼) — 인프라 관측이라 트레이딩 판정 무유입.
//  ★드롭-오래된-것(overwrite) 무대기 링: 디스크 stall 로 소비가 밀려도 핫패스는 절대 블로킹 없이 최신을 쓰고 구형을 덮어씀.
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>고정 크기 blittable 로그 이벤트(32B·[Sequential,Pack=1]·참조 0·언매니지드 링 원소).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 32)]
    public readonly struct BlittableLogEvent
    {
        public readonly long Seq;             // 단조 시퀀스(커밋/드롭 판별 겸용)
        public readonly long TimestampTicks;  // Stopwatch.GetTimestamp(QPC)
        public readonly long Arg;             // 페이로드(심볼코드·값 등)
        public readonly int  Code;            // 오코드(opcode)
        public readonly int  ThreadId;        // 라이터 관리 스레드 id

        public BlittableLogEvent(long seq, long ticks, long arg, int code, int threadId)
        { Seq = seq; TimestampTicks = ticks; Arg = arg; Code = code; ThreadId = threadId; }
    }

    /// <summary>무할당 핫패스 푸시 + 백그라운드 Brotli 압축/암호화 → .ms2log 비동기 영속. 언매니지드 드롭-오래된 락프리 링.</summary>
    public sealed unsafe class ZeroAllocAsyncCompressedLogger : IDisposable
    {
        private readonly BlittableLogEvent* _ring;    // 오프힙 고정 버퍼(GC 무압력)
        private readonly long*              _slotSeq; // 슬롯별 커밋 시퀀스(-1=미커밋). Volatile 커밋 마커
        private readonly long   _capacity;            // 2의 거듭제곱
        private readonly long   _mask;
        private readonly int    _evSize = sizeof(BlittableLogEvent);

        private long _head;        // 다음 클레임 시퀀스(단조·Interlocked)
        private long _tail;        // 백그라운드 소비 커서(단일 소비자 전용)
        private long _pushed, _dropped, _framesWritten, _rawBytesCum, _compBytesCum, _lastDrainTicks, _drainErrors;

        private readonly Thread                 _io;
        private readonly ManualResetEventSlim    _wake = new(false);
        private readonly Action<string>?         _onDiskFault;   // 디스크 영속 실패 → 상위 방화벽/동결 통지
        private readonly MmfFlightRecorder?      _blackbox;
        private readonly string                  _path;
        private readonly long                    _tickFreq;
        private volatile bool _stop;
        private volatile bool _diskFaulted;   // ★적대검증 교정 — 로거 자체 디스크 fault 래치(드레인 억제). 전역 동결과 무관(동결 중 로깅은 포렌식 가치).
        private int _disposed;

        // 콜드 드레인 전용 사전할당 scratch(드레인마다 new 금지 — δ=0 은 핫패스지만 콜드도 GC 절제).
        private readonly BlittableLogEvent[] _batch;
        private readonly byte[]              _rawScratch;
        private readonly byte[]              _compScratch;
        private FileStream? _fs;

        public ZeroAllocAsyncCompressedLogger(string? path = null, int capacity = 65536,
            Action<string>? onDiskFault = null, MmfFlightRecorder? blackbox = null)
        {
            long cap = 1; while (cap < Math.Max(1024, capacity)) cap <<= 1;   // pow2
            _capacity = cap; _mask = cap - 1;
            _ring     = (BlittableLogEvent*)NativeMemory.AlignedAlloc((nuint)(cap * _evSize), 64);
            _slotSeq  = (long*)NativeMemory.AlignedAlloc((nuint)(cap * sizeof(long)), 64);
            for (long i = 0; i < cap; i++) _slotSeq[i] = -1L;
            _onDiskFault = onDiskFault; _blackbox = blackbox;
            _tickFreq = Stopwatch.Frequency;

            int batchN = (int)Math.Min(cap, 4096);
            _batch       = new BlittableLogEvent[batchN];
            _rawScratch  = new byte[batchN * _evSize];
            _compScratch = new byte[batchN * _evSize + 64];   // Brotli 최악 팽창 여유
            _path = path ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MS2E.StockSuite", "logs", $"session_{Environment.ProcessId}.ms2log");

            _lastDrainTicks = Stopwatch.GetTimestamp();
            _io = new Thread(IoLoop) { IsBackground = true, Name = "Ms2Logger", Priority = ThreadPriority.BelowNormal };
        }

        public void Start() { if (!_io.IsAlive) _io.Start(); }

        public long PushedTotal    => Interlocked.Read(ref _pushed);
        public long DroppedTotal   => Interlocked.Read(ref _dropped);
        public long FramesWritten  => Interlocked.Read(ref _framesWritten);
        public long RawBytesCum    => Interlocked.Read(ref _rawBytesCum);
        public long CompBytesCum   => Interlocked.Read(ref _compBytesCum);
        public long DrainErrors    => Interlocked.Read(ref _drainErrors);
        /// <summary>미소비 큐 깊이(head-tail). capacity 근접이면 소비 지연(디스크 stall 징후).</summary>
        public long QueueDepth     { get { long d = Interlocked.Read(ref _head) - Volatile.Read(ref _tail); return d < 0 ? 0 : d; } }
        /// <summary>백그라운드 I/O 마지막 진행 후 경과(ms). 디스크 stall 워치독 입력.</summary>
        public long MillisSinceLastDrain => (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastDrainTicks)) * 1000L / _tickFreq;
        /// <summary>압축비 bps(압축/원본 ×10000). 낮을수록 압축 효율↑.</summary>
        public long CompressionRatioBps { get { long r = Interlocked.Read(ref _rawBytesCum); return r <= 0 ? 10000L : Interlocked.Read(ref _compBytesCum) * 10000L / r; } }

        /// <summary>
        /// ★핫패스(δ=0) 로그 푸시 — 단조 클레임(Interlocked) + 언매니지드 슬롯 기록 + Volatile 커밋. new/박싱/문자열/락 0.
        /// 드롭-오래된-것: 소비가 밀려 링이 만원이면 구형 슬롯을 덮어씀(핫패스 무대기). 절대 블로킹 없음 → 디스크 stall 역전파 차단.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Log(int code, long arg)
        {
            // [크래시 트리아지] 수명 게이트 — 수정 전 Dispose 이후/도중 Log 가 해제된 _ring/_slotSeq 에 기록(use-after-free).
            if (!NativeLifetimeGate.TryEnter(ref _gate)) { Interlocked.Increment(ref _dropped); return; }
            try
            {
            long seq  = Interlocked.Increment(ref _head) - 1L;
            long slot = seq & _mask;
            Volatile.Write(ref _slotSeq[slot], long.MinValue);   // ★seqlock 무효화 — 데이터 덮어쓰기 前(랩어라운드 torn read 방지)
            _ring[slot] = new BlittableLogEvent(seq, Stopwatch.GetTimestamp(), arg, code, Environment.CurrentManagedThreadId);
            Volatile.Write(ref _slotSeq[slot], seq);   // 커밋 마커(소비자는 seq 일치 + 복사 후 재검증으로만 채택)
            Interlocked.Increment(ref _pushed);
            if (Interlocked.Read(ref _head) - Volatile.Read(ref _tail) > (_capacity >> 1)) _wake.Set();   // 반 이상 차면 드레인 재촉
            }
            finally { NativeLifetimeGate.Exit(ref _gate); }
        }

        private int _gate, _leaked;
        public bool LeakedOnDispose => Volatile.Read(ref _leaked) == 1;
        internal Action? DrainHookForTest;      // 테스트 전용 — IO 스레드 드레인 지연 주입(Join 타임아웃 재현)
        internal bool IoThreadAlive => _io.IsAlive;

        /// <summary>테스트 전용 — 백그라운드 스레드 없이 드레인 1회 실행(드롭 계수·프레임 영속 결정론 검증).</summary>
        public void DrainOnceForTest() => DrainOnce();

        private void IoLoop()
        {
            while (!_stop)
            {
                try { DrainOnce(); }
                catch { Interlocked.Increment(ref _drainErrors); }
                Interlocked.Exchange(ref _lastDrainTicks, Stopwatch.GetTimestamp());
                // [크래시 트리아지 · 실결함] 수정 전 Reset 이 try 밖 — Dispose 의 Join 타임아웃 후 _wake 가 처분되면 ObjectDisposedException 이
                //   이 백그라운드 스레드에서 미처리 → 테스트 호스트/앱 프로세스 종료(이벤트 로그 1026 'ManualResetEventSlim' 패턴과 동형).
                try { _wake.Reset(); _wake.Wait(25); } catch (ObjectDisposedException) { return; }   // 25ms 또는 신호 시 기상(인터럽터블)
            }
            try { DrainOnce(); } catch { }   // 종료 최종 드레인
            try { _fs?.Flush(true); _fs?.Dispose(); } catch { }
        }

        /// <summary>커밋 이벤트를 배치 차출 → 직렬화 → Brotli 압축 → XOR 난독 → CRC32 프레임 → .ms2log append(콜드).</summary>
        private void DrainOnce()
        {
            DrainHookForTest?.Invoke();
            // ★적대검증 교정(D6) — 로거 자체 디스크 fault 시에만 억제(전역 동결 무관 — 동결 중 로깅은 포렌식 가치·종료 최종 드레인도 허용).
            if (_diskFaulted) return;
            long head = Interlocked.Read(ref _head);
            long tail = Volatile.Read(ref _tail);
            long avail = head - tail;
            if (avail <= 0) return;

            // 드롭-오래된-것: 소비가 capacity 이상 밀렸으면 오래된 (avail-capacity) 건은 덮어써져 유실 — 계수 후 tail 전진.
            if (avail > _capacity)
            {
                long drop = avail - _capacity;
                Interlocked.Add(ref _dropped, drop);
                tail += drop;
                try { _blackbox?.Record(FlightEventKind.LogFabricStall, 0L, drop, avail, _capacity, 88); } catch { }
            }

            // ★적대검증 교정(D3/D4) — 3분기: 커밋(seqlock 재검증 후 소비)·덮어쓰기(드롭 계수)·미커밋 프런티어(정지·다음 드레인 재방문).
            bool blocked = false;
            while (tail < head && !blocked)
            {
                int n = 0;
                for (; n < _batch.Length && tail < head; )
                {
                    long slot = tail & _mask;
                    long s = Volatile.Read(ref _slotSeq[slot]);
                    if (s == tail)
                    {
                        var ev = _ring[slot];                                       // 후보 복사(로컬)
                        Interlocked.MemoryBarrier();                                // 복사 완료 후 마커 재독출(seqlock)
                        if (Volatile.Read(ref _slotSeq[slot]) != tail)              // 복사 중 랩 생산자가 덮어씀 → 폐기·드롭 계수
                        { Interlocked.Increment(ref _dropped); tail++; continue; }
                        _batch[n++] = ev; tail++;
                    }
                    else if (s > tail) { Interlocked.Increment(ref _dropped); tail++; }   // 후세대 덮어쓰기 확정 — 드롭 계수(스킵 아님)
                    else { blocked = true; break; }                                       // 미커밋(long.MinValue/구세대) 프런티어 — 정지
                }
                if (n > 0) PersistBatch(n);
                if (n == 0) break;
            }
            Volatile.Write(ref _tail, tail);
        }

        private void PersistBatch(int n)
        {
            int rawLen = n * _evSize;
            var raw = _rawScratch.AsSpan(0, rawLen);
            MemoryMarshal.AsBytes(new ReadOnlySpan<BlittableLogEvent>(_batch, 0, n)).CopyTo(raw);

            // Brotli 압축(내장·의존성 0·span API·스트림 미할당). 팽창하면 미압축 프레임으로 폴백. 결과는 항상 _compScratch 에 정착.
            long seed = unchecked(0x4D53_3245_4C4F_4700L ^ n);
            byte flag; int payloadLen;
            if (BrotliEncoder.TryCompress(raw, _compScratch, out int compLen, quality: 1, window: 22) && compLen < rawLen)
            { flag = 1; payloadLen = compLen; }                                   // 압축 성공 — 이미 _compScratch 에 있음
            else
            { flag = 0; payloadLen = rawLen; raw.CopyTo(_compScratch.AsSpan(0, rawLen)); }   // 미압축 폴백 — 원본 복사
            var obf = _compScratch.AsSpan(0, payloadLen);
            XorKeystream(obf, seed);        // 난독(자기역·결정론)
            uint crc = Crc32.Compute(obf);

            EnsureFile();
            if (_fs is null) return;
            // 프레임: [flag(1)|rawLen(4)|payloadLen(4)|crc(4)|payload] — 리더가 프레임별 검증/복원.
            Span<byte> hdr = stackalloc byte[13];
            hdr[0] = flag;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(hdr[1..], rawLen);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(hdr[5..], obf.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(hdr[9..], crc);
            try
            {
                _fs.Write(hdr);
                _fs.Write(obf);
                Interlocked.Add(ref _rawBytesCum, rawLen);
                Interlocked.Add(ref _compBytesCum, obf.Length);
                Interlocked.Increment(ref _framesWritten);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _drainErrors);
                try { _fs?.Dispose(); } catch { }
                _fs = null;
                _diskFaulted = true;   // ★로거 자체 디스크 fault 래치 — 이후 드레인 억제(디스크 계속 두드리지 않음). 전역 동결과 분리.
                try { _onDiskFault?.Invoke("ms2log write 실패: " + ex.Message); } catch { }   // 상위 방화벽/동결 통지(fail-closed)
            }
        }

        private void EnsureFile()
        {
            if (_fs is not null) return;
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, bufferSize: 1 << 16);
            }
            catch { _fs = null; }
        }

        private static void XorKeystream(Span<byte> data, long seed)
        {
            ulong k = unchecked((ulong)seed);
            for (int i = 0; i < data.Length; i++)
            {
                k = unchecked(k * 6364136223846793005UL + 1442695040888963407UL);
                data[i] ^= (byte)(k >> 56);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop = true;
            try { _wake.Set(); } catch { }
            // ★적대검증 교정(D2) — Join 성공(스레드 확실 종료) 시에만 해제. 디스크 stall 로 Join 타임아웃이면 소비자가 여전히 _ring/_slotSeq
            //   를 읽는 중일 수 있어 해제하면 use-after-free. 그 경우 누수 허용(종료 시 OS 가 회수·무한 대기로 종료 지연 재유발 회피).
            bool joined = true;
            try { joined = !_io.IsAlive || _io.Join(2000); } catch { }
            // ★수정 전: 미종료(joined=false)여도 _wake 를 처분 → 살아 있는 IO 스레드의 Reset/Wait 가 ODE 로 프로세스를 죽였다.
            //   미종료면 처분하지 않는다(핸들 1개 누수 — 스레드가 나중에 _stop 을 보고 스스로 종료).
            bool producersDrained = NativeLifetimeGate.CloseAndDrain(ref _gate, NativeLifetimeGate.DefaultDrainTimeout);
            if (!joined || !producersDrained) { Volatile.Write(ref _leaked, 1); return; }   // 소비자 또는 생산자 미배수 — 해제 금지(UAF 대신 누수)
            try { _wake.Dispose(); } catch { }
            try { NativeMemory.AlignedFree(_ring); } catch { }
            try { NativeMemory.AlignedFree(_slotSeq); } catch { }
        }
    }
}
