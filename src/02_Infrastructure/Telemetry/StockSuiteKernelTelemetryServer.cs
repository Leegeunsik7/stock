// ============================================================================
// StockSuiteKernelTelemetryServer — v6.3.0: 음원 관제 앱 ↔ StockSuite 커널 IPC 텔레메트리 서버
//
//  음원 관제 앱(KernelIpcBridgeService)이 대기하는 NamedPipe(\\.\pipe\StockSuiteKernelTelemetry)의 '서버측'.
//  커널 상태(RIO Active · SIMD Armed · MMF INTACT · 수신 프레임)를 라이브 브로드캐스트하고, UI 로부터의 비상
//  FREEZE 커맨드를 수신해 커널 비가역 봉인을 집행한다.
//
//  ── Task 1: Non-blocking Async Server + δ=0 송출 ──────────────────────────────
//   · 전용 백그라운드 스레드(수락/수신 + 송출) 2개로 커널 핫패스와 완전 분리(핫패스 성능 저하 0).
//   · 송출 직렬화는 사전할당 버퍼 + Span(BinaryPrimitives) 로 δ=0(프레임당 new byte[]/boxing 0). 동기 블로킹
//     Write 를 전용 스레드에서 수행해 async 상태머신 할당도 회피.
//
//  ── Task 2: QPC 지연 프로토콜 + 양방향 비상 게이트 ────────────────────────────
//   · 24B 프레임(LE): [magic u32=0x4B53544B "KSTK"][flags u32][framesReceived i64][qpcTicks i64].
//     flags: bit0 RIO Active · bit1 SIMD Armed · bit2 MMF INTACT. QPC(Stopwatch.GetTimestamp)로 클라이언트가
//     IPC 도달 지연(ns)을 산출. (16B→24B 진화 — 클라이언트도 동반 갱신.)
//   · 수신 스레드가 [0xFF] FREEZE 커맨드를 감지하면 onFreeze(→ CausalWeaveClock.TripFuse) 를 호출해 비가역 봉인.
//
//  ── Task 3(★독자 혁신): MMF Dual-Bus Fallback ────────────────────────────────
//   · 파이프 송출과 별개로, 매 프레임을 시스템 공유 MMF(StockSuiteKernelTelemetryBus)의 단일 슬롯에 seqlock 으로
//     상시 미러링(δ=0). 파이프 절단·혼잡 시 소비자는 이 MMF 스냅샷을 zero-copy 로 pull 하여 마지막 상태를 잃지
//     않는다(push=파이프 · pull=MMF 이중 버스). 파이프 클라이언트 유무와 무관하게 항상 최신.
//
//  ★불변 제약: δ=0 송출 직렬화(사전할당·Span·포인터). 커널 하드-의존 0(상태 델리게이트 주입). 기존 불변 회로
//    무접촉(신규 파일). ★unsafe 범위: MMF 페이지 포인터(기존 MmfIpcFabric/NvmeAuditJournal 관례 연장).
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>커널 텔레메트리 플래그 스냅샷(블리터블 값타입·힙 0).</summary>
    public readonly struct KernelFlags
    {
        public readonly bool RioActive, SimdArmed, MmfIntact;
        public readonly long FramesReceived;
        public KernelFlags(bool rioActive, bool simdArmed, bool mmfIntact, long framesReceived)
        { RioActive = rioActive; SimdArmed = simdArmed; MmfIntact = mmfIntact; FramesReceived = framesReceived; }
        public uint ToBits() => (RioActive ? 1u : 0u) | (SimdArmed ? 2u : 0u) | (MmfIntact ? 4u : 0u);
    }

    /// <summary>StockSuite 커널 텔레메트리 IPC 서버(NamedPipe + MMF 이중버스 · FREEZE 게이트 · δ=0 송출). IDisposable.</summary>
    public sealed unsafe class StockSuiteKernelTelemetryServer : IDisposable
    {
        public const string DefaultPipeName = "StockSuiteKernelTelemetry";
        public const string DefaultMmfName  = "StockSuiteKernelTelemetryBus";
        public const uint   FrameMagic = 0x4B53544B;   // "KSTK"(LE)
        public const int    FrameSize  = 24;           // magic(4)+flags(4)+framesReceived(8)+qpcTicks(8)
        public const byte   CmdFreeze  = 0xFF;         // 비상 FREEZE 커맨드 opcode
        private const int   MmfSeqOff  = 8;            // MMF: [version i64 @0][sequence i64 @8][frame 24B @16]
        private const int   MmfFrameOff = 16;
        private const int   MmfSize    = 64;           // 1 캐시라인(8+8+24=40 ≤ 64)
        private const int   PipeBuffer = 4096;         // 송/수신 버퍼(프레임 큐 여유 — 미독출 시 Write 즉시 블록 방지)

        private readonly Func<KernelFlags> _state;
        private readonly Action<string>?   _onFreeze;
        private readonly Func<long>        _nowTicks;
        private readonly int    _heartbeatMs;
        private readonly string _pipeName, _mmfName;

        private readonly byte[] _frame = new byte[FrameSize];   // 사전할당 송출 버퍼(δ=0)
        private readonly byte[] _pipeFrame = new byte[FrameSize]; // 비동기 파이프 쓰기용 안정 스냅샷(브로드캐스트 _frame 덮어써도 무손상)
        private readonly object _pipeLock = new();               // _pipe 수명 직렬화(AcceptLoop↔Dispose TOCTOU 차단)
        private Thread? _acceptThread, _broadcastThread;
        private volatile bool _running;
        private volatile NamedPipeServerStream? _pipe;
        private volatile int _clientConnected;
        private int  _pipeBusy;                                  // 0/1 — 이전 파이프 쓰기 미완이면 드롭(느린 소비자 격리)
        private long _framesSent, _freezeCommands, _mmfVer, _mmfSeq;

        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _mmfAcc;
        private byte* _mmfBase;

        public StockSuiteKernelTelemetryServer(Func<KernelFlags> stateProvider, Action<string>? onFreeze = null,
                                               Func<long>? nowTicks = null, int heartbeatMs = 100,
                                               string? pipeName = null, string? mmfName = null)
        {
            _state       = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _onFreeze    = onFreeze;
            _nowTicks    = nowTicks ?? Stopwatch.GetTimestamp;
            _heartbeatMs = heartbeatMs < 10 ? 10 : heartbeatMs;
            _pipeName    = pipeName ?? DefaultPipeName;
            _mmfName     = mmfName  ?? DefaultMmfName;
        }

        public bool ClientConnected => _clientConnected == 1;
        public long FramesSent      => Interlocked.Read(ref _framesSent);
        public long FreezeCommands  => Interlocked.Read(ref _freezeCommands);
        public bool MmfActive       => _mmfBase != null;

        public void Start()
        {
            if (_running) return;
            OpenMmf();
            _running = true;
            _broadcastThread = new Thread(BroadcastLoop) { IsBackground = true, Name = "KernelTelemetryBroadcast", Priority = ThreadPriority.AboveNormal };
            _acceptThread    = new Thread(AcceptLoop)    { IsBackground = true, Name = "KernelTelemetryAccept" };
            _broadcastThread.Start();
            _acceptThread.Start();
        }

        private void OpenMmf()
        {
            try
            {
                _mmf = MemoryMappedFile.CreateOrOpen(_mmfName, MmfSize, MemoryMappedFileAccess.ReadWrite);
                _mmfAcc = _mmf.CreateViewAccessor(0, MmfSize, MemoryMappedFileAccess.ReadWrite);
                byte* p = null;
                _mmfAcc.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                _mmfBase = p + _mmfAcc.PointerOffset;
                NativeMemory.Clear(_mmfBase, (nuint)MmfSize);
            }
            catch { _mmfBase = null; /* MMF 불가 — 파이프 단독 운용 */ }
        }

        // 수락/수신 전용 스레드: 클라이언트 연결 수락 → FREEZE 수신(블로킹) → 절단 시 재수락.
        private void AcceptLoop()
        {
            while (_running)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    // ★PipeOptions.Asynchronous: 오버랩 I/O 로 동기 Read(FREEZE 대기)와 비동기 Write(프레임 송출)가 핸들에서
                    //   직렬화되지 않도록 한다. ★create-then-publish 를 락 하 _running 재확인 뒤에만 발행 — Dispose 와의
                    //   TOCTOU(새 파이프가 Dispose 이후 생성돼 누수·스트랜딩)를 직렬화 차단.
                    lock (_pipeLock)
                    {
                        if (!_running) break;
                        pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, PipeBuffer, PipeBuffer);
                        _pipe = pipe;
                    }
                    pipe.WaitForConnection();
                    Interlocked.Exchange(ref _clientConnected, 1);
                    RecvLoop(pipe);   // 절단/EOF 까지 블로킹(FREEZE 수신)
                }
                catch { /* 수락/IO 오류 — 재수락 */ }
                finally
                {
                    Interlocked.Exchange(ref _clientConnected, 0);
                    lock (_pipeLock) { if (ReferenceEquals(_pipe, pipe)) _pipe = null; }
                    try { pipe?.Dispose(); } catch { }   // 로컬 참조 dispose(Dispose 와 idempotent)
                }
                if (_running) Thread.Sleep(150);
            }
        }

        // 송출 전용 스레드: 상태 변경 또는 heartbeat 마다 δ=0 직렬화 → MMF(상시) + 파이프(연결 시) 이중 브로드캐스트.
        private void BroadcastLoop()
        {
            KernelFlags last = default; bool have = false; long lastMs = 0;
            while (_running)
            {
                try
                {
                    KernelFlags cur = _state();
                    long nowMs = Environment.TickCount64;
                    if (!have || !FlagsEqual(cur, last) || (nowMs - lastMs) >= _heartbeatMs)
                    {
                        Serialize(cur);          // δ=0(사전할당 _frame + Span)
                        WriteMmf();              // ★MMF 이중버스 상시 미러(파이프 무관·항상 신선)
                        // ★파이프는 best-effort 보조 푸시 — 이전 쓰기가 미완(느린 소비자)이면 이 프레임 드롭. WritePipeAsync 가
                        //   동기/비동기 예외를 자체 흡수(disposed pipe 크래시 방지)하므로 브로드캐스트 스레드는 정지하지 않는다.
                        var p = _pipe;
                        if (p != null && _clientConnected == 1 && Interlocked.CompareExchange(ref _pipeBusy, 1, 0) == 0)
                        {
                            Buffer.BlockCopy(_frame, 0, _pipeFrame, 0, FrameSize);   // 비동기 쓰기용 안정 스냅샷
                            WritePipeAsync(p);   // fire-and-forget(완료 시 _pipeBusy=0) — 브로드캐스트 무블록
                        }
                        last = cur; have = true; lastMs = nowMs;
                    }
                }
                catch { /* ★관측 스레드 생존 최우선 — 일시 예외 흡수(다음 주기 재시도). 텔레메트리 스레드 사멸 방지. */ }
                Thread.Sleep(20);   // 변경 폴 주기(≤20ms 반영 + heartbeat)
            }
        }

        // 비동기 파이프 송출(드롭-if-pending). ★이 클래스는 unsafe(MMF 포인터)라 await 사용 불가(CS4004) → WriteAsync 를
        //   ContinueWith 로 이어 완료 시 _pipeBusy 를 해제한다. 절단/절체 예외는 관측·삼킴 — 브로드캐스트/MMF 무영향.
        private void WritePipeAsync(NamedPipeServerStream p)
        {
            try
            {
                // ★p.WriteAsync 는 disposed 파이프(연결 절단·Dispose 경합)에 대해 '동기적으로' ObjectDisposedException 을
                //   던진다 — 이 try 가 없으면 브로드캐스트 스레드에서 미포착 예외 → 프로세스 크래시(Release 타이밍 재현). 감싸서 흡수.
                p.WriteAsync(_pipeFrame, 0, FrameSize).ContinueWith(t =>
                {
                    try
                    {
                        _ = t.Exception;   // 관측(unobserved 예외 이벤트 억제)
                        if (t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                        {
                            p.Flush();
                            Interlocked.Increment(ref _framesSent);
                        }
                    }
                    catch { /* 절단/플러시 실패 — AcceptLoop 가 재수락 */ }
                    finally { Interlocked.Exchange(ref _pipeBusy, 0); }
                }, System.Threading.Tasks.TaskScheduler.Default);
            }
            catch   // WriteAsync 동기 throw(disposed·IO) — 프레임 드롭·busy 해제(AcceptLoop 재수락). 브로드캐스트 스레드 생존.
            {
                Interlocked.Exchange(ref _pipeBusy, 0);
            }
        }

        private static bool FlagsEqual(KernelFlags a, KernelFlags b)
            => a.RioActive == b.RioActive && a.SimdArmed == b.SimdArmed && a.MmfIntact == b.MmfIntact && a.FramesReceived == b.FramesReceived;

        // 24B 프레임 δ=0 직렬화(사전할당 버퍼 재사용).
        private void Serialize(KernelFlags f)
        {
            var span = _frame.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(span, FrameMagic);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), f.ToBits());
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(8), f.FramesReceived);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(16), _nowTicks());   // ★QPC 타임스탬프(도달 지연 산출용)
        }

        // MMF 단일 슬롯 seqlock 미러(δ=0·포인터). 홀=쓰기중, 짝=완료.
        private void WriteMmf()
        {
            byte* b = _mmfBase;
            if (b == null) return;
            // ★엄격 단조 seqlock: 프레임마다 +2(짝수→짝수). 기존 (++_mmfVer)|1 은 연속 프레임이 같은 짝/홀 버전을
            //   공유해(예 2,3 → 둘 다 완료 4) 소비자의 torn-read 검출을 무력화하던 결함 — 버전을 프레임마다 엄격 증가시킨다.
            long ver = (_mmfVer += 2);                     // 짝수 기저(2,4,6,…)
            Volatile.Write(ref *(long*)b, ver - 1L);       // 홀수(쓰기 시작)
            *(long*)(b + MmfSeqOff) = ++_mmfSeq;           // ★64비트 프레임 시퀀스(폴백 gap 감사용)
            for (int i = 0; i < FrameSize; i++) b[MmfFrameOff + i] = _frame[i];
            Volatile.Write(ref *(long*)b, ver);            // 짝수(완료·엄격 단조 +2)
        }

        // FREEZE 수신: [0xFF] → onFreeze(비가역 봉인) 집행.
        private void RecvLoop(NamedPipeServerStream pipe)
        {
            try
            {
                while (_running && pipe.IsConnected)
                {
                    int b = pipe.ReadByte();
                    if (b < 0) break;   // EOF(절단)
                    if ((byte)b == CmdFreeze)
                    {
                        Interlocked.Increment(ref _freezeCommands);
                        try { _onFreeze?.Invoke("음원 관제 UI 비상 FREEZE 커맨드 수신 — 커널 비가역 하드웨어 봉인 집행."); } catch { }
                    }
                }
            }
            catch { /* 절단 — 상위가 재수락 */ }
        }

        /// <summary>MMF 이중버스에서 최신 스냅샷을 seqlock 무락 독출(소비자 fallback·δ=0). 성공 시 true.</summary>
        public static bool TryReadMmfSnapshot(out uint flags, out long framesReceived, out long qpcTicks, out long sequence, string? mmfName = null)
        {
            flags = 0; framesReceived = 0; qpcTicks = 0; sequence = 0;
            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(mmfName ?? DefaultMmfName, MemoryMappedFileRights.Read);
                using var acc = mmf.CreateViewAccessor(0, MmfSize, MemoryMappedFileAccess.Read);
                byte* p = null;
                acc.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                try
                {
                    p += acc.PointerOffset;
                    for (int attempt = 0; attempt < 8; attempt++)
                    {
                        long v1 = Volatile.Read(ref *(long*)p);
                        if ((v1 & 1L) != 0) continue;                       // 쓰기중 → 재시도
                        long sq    = ReadI64(p + MmfSeqOff);
                        uint magic = ReadU32(p + MmfFrameOff);
                        uint fl    = ReadU32(p + MmfFrameOff + 4);
                        long fr    = ReadI64(p + MmfFrameOff + 8);
                        long qp    = ReadI64(p + MmfFrameOff + 16);
                        long v2 = Volatile.Read(ref *(long*)p);
                        if (v1 != v2) continue;                            // 독출 중 갱신 → 재시도
                        if (magic != FrameMagic) return false;             // 미초기화
                        flags = fl; framesReceived = fr; qpcTicks = qp; sequence = sq;
                        return true;
                    }
                    return false;
                }
                finally { acc.SafeMemoryMappedViewHandle.ReleasePointer(); }
            }
            catch { return false; }   // MMF 부재(서버 미가동) — 폴백 불가
        }

        private static uint ReadU32(byte* p) => BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(p, 4));
        private static long ReadI64(byte* p) => BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(p, 8));

        public void Dispose()
        {
            _running = false;
            // ★_pipe 를 락 하에서 dispose — AcceptLoop create-then-publish 와 TOCTOU 직렬화(핸들 누수·스레드 스트랜딩
            //   차단). 블로킹 WaitForConnection 은 이 핸들 dispose 로 깨어난다.
            lock (_pipeLock) { try { _pipe?.Dispose(); } catch { } }
            SafeJoin(_acceptThread, 2000);
            // ★broadcast 스레드는 MMF 를 매 반복 역참조 → 종료 '입증' 후에만 언맵(UAF 제거). 파이프 무블록화로 반복은
            //   <20ms 라 통상 즉시 종료 — 타임아웃 시엔 UAF 대신 MMF 자원 누수 허용(안전 방향).
            if (SafeJoin(_broadcastThread, 2000) && _mmfBase != null)
            {
                try { _mmfAcc!.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
                _mmfBase = null;
                try { _mmfAcc?.Dispose(); } catch { }
                try { _mmf?.Dispose(); } catch { }
            }
        }

        private static bool SafeJoin(Thread? t, int ms) { if (t == null) return true; try { return t.Join(ms); } catch { return false; } }
    }
}
