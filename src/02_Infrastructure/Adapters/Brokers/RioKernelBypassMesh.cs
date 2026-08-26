// ============================================================================
// RioKernelBypassMesh — v6.0.0 Windows RIO(Registered I/O) 커널 바이패스 수신 패브릭
//   (+ v6.1.0 Path A: IGMP 멀티캐스트 조인 · 물리 NIC 바인딩 · 제로카피 인라인 파싱)
//
//  표준 Winsock 의 OS 커널 스케줄링 지터/syscall/소켓버퍼 복사를 제거하기 위해 mswsock 의 RIO 확장
//  (WSAIoctl(SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER, WSAID_MULTIPLE_RIO) → RIO_EXTENSION_FUNCTION_TABLE)
//  를 네이티브 함수 포인터(delegate* unmanaged[Stdcall])로 직접 바인딩한다. NIC→등록 언매니지드 고정
//  버퍼(NativeMemory)로 커널 소켓버퍼를 우회한 무복사 수신 후, 완료 순간 QPC 를 봉인해 CausalWeaveRing 에
//  발행한다(수신 지연 나노초 대역).
//
//  ── ★v6.1.0 Path A: 라이브 마켓 피드 결선 ──────────────────────────────────
//  · IGMP 멀티캐스트: setsockopt(IP_ADD_MEMBERSHIP, ip_mreq{group, nicInterface}) 를 RIO 소켓 셋업
//    파이프라인에 마운트(관리형 폴백은 Socket.SetSocketOption(IP, AddMembership, MulticastOption)).
//  · NIC 바인딩: bind(특정 NIC IP:port) + IP_MULTICAST_IF(nicInterface) 로 물리 인터페이스 강제 고정.
//  · 제로카피 라우팅: RIOReceive 등록버퍼(NativeMemory 핀 고정)를 중간 byte[] 없이 ReadOnlySpan 으로
//    즉시 파싱(RioZeroCopyParser·TryParseL3Header)한 뒤 CausalWeaveRing 에 직접 발행(δ=0).
//
//  ★unsafe 범위 확장(의식적): 기존 관례는 unsafe 를 MmfIpcFabric 에 국한했다. RIO 는 함수 포인터·네이티브
//    등록 버퍼·RIO_BUF 포인터가 본질이라 이 패브릭으로 범위를 명시 확장한다(나머지 코드베이스 Span/Marshal 유지).
//  ★Safe Fallback(fail-closed 아님·안심 강등): RIO 미지원(OS/NIC/WSAIoctl 실패) 감지 시 예외를 삼키지 않고
//    정직한 사유(LastFallbackReason)를 남긴 뒤, 기존 초고속 관리형 소켓 수신 파이프로 강등한다. 두 경로 모두
//    동일 CausalWeaveRing 에 발행 → 하류 소비자는 경로에 무관하게 동작.
//  ★liveArmed:false(구조적 0-leak): 순수 인바운드 수신 — onSend/PublishRaw/아웃바운드 소켓 쓰기 참조 0.
//    이 패브릭이 산출하는 건 오직 '수신 프레임'이며, 발주 경로(GatedOrderDispatcher.onSend)에 도달하는 코드가
//    존재하지 않는다. δ=0(드레인 핫패스: 네이티브 포인터·스택 RIORESULT·Publish — 힙 0). 결정론(주입 QPC).
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>RIO 수신 프레임 소비자(무할당 in 파라미터 — 봉인 QPC 동반).</summary>
    public delegate void RioFrameSink(ReadOnlySpan<byte> frame, long qpc);

    /// <summary>제로카피 인라인 파서(발행 직전 스팬 위에서 무할당 파싱 — L3 헤더 피크/라우팅). δ=0(필드 델리게이트·무캡처).</summary>
    public delegate void RioZeroCopyParser(ReadOnlySpan<byte> frame, long qpc);

    /// <summary>
    /// RIO 커널 바이패스 수신 패브릭. TryActivate() 로 RIO 바인딩 시도 → 실패 시 관리형 소켓 폴백.
    /// 두 경로 모두 CausalWeaveRing 발행 + (옵션) 직결 sink + (옵션) 제로카피 파서. IDisposable.
    /// </summary>
    public sealed unsafe partial class RioKernelBypassMesh : IDisposable
    {
        // ── Winsock/RIO 상수 ──
        private const int    AF_INET = 2, SOCK_DGRAM = 2, IPPROTO_UDP = 17;
        private const uint   WSA_FLAG_REGISTERED_IO = 0x100;
        private const uint   SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER = 0xC8000024;
        private static readonly nuint INVALID_SOCKET = unchecked((nuint)(~(nuint)0));
        private const int    SOCKET_ERROR = -1;
        // ── ★멀티캐스트/NIC setsockopt 상수 ──
        private const int    SOL_SOCKET = 0xFFFF, SO_REUSEADDR = 0x0004;
        private const int    IPPROTO_IP = 0, IP_MULTICAST_IF = 9, IP_ADD_MEMBERSHIP = 12;

        private readonly CausalWeaveClock _clock;      // ★수신 QPC 워터마크 관측(라이브 단조)
        private readonly CausalWeaveRing  _ring;       // 발행 대상(오버런 증명·newest-wins)
        private readonly Func<long>       _nowTicks;   // QPC(주입·기본 Stopwatch.GetTimestamp)
        private readonly int _slotCount, _slotSize;

        private nuint _socket = INVALID_SOCKET;
        private RIO_EXTENSION_FUNCTION_TABLE _rio;
        private IntPtr _bufferId = IntPtr.Zero;        // RIO_BUFFERID
        private IntPtr _cq = IntPtr.Zero;              // RIO_CQ
        private IntPtr _rq = IntPtr.Zero;              // RIO_RQ
        private byte*  _buffer;                        // 등록 네이티브 수신 버퍼(slotCount*slotSize)
        private RIO_BUF* _bufDescs;                    // 슬롯별 RIO_BUF(포인터 안정)
        private bool   _wsaStarted;

        private Thread? _drainThread;
        private Socket? _fallbackSocket;
        private Thread? _fallbackThread;
        private volatile int _running;                 // 0/1
        private volatile int _rioActive;               // RIO 바인딩 성공
        private volatile int _fallbackActive;          // 폴백 가동
        private long _lastRecvLatencyNanos;            // 최근 수신 지연(ns) — dequeue→publish
        private long _framesReceived;
        private string _lastFallbackReason = "";
        private RioZeroCopyParser? _zeroCopyParser;    // ★제로카피 인라인 파서(옵션·필드 — 핫패스 무캡처)
        private uint _multicastGroup;                  // 조인한 멀티캐스트 그룹(in_addr·0=없음)
        private uint _boundInterface;                  // 멀티캐스트 NIC in_addr(0=INADDR_ANY)

        public RioKernelBypassMesh(CausalWeaveClock clock, CausalWeaveRing ring,
                                   Func<long>? nowTicks = null, int slotCount = 1024, int slotSize = 2048)
        {
            _clock    = clock ?? throw new ArgumentNullException(nameof(clock));
            _ring     = ring  ?? throw new ArgumentNullException(nameof(ring));
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            if (slotCount < 8 || (slotCount & (slotCount - 1)) != 0) throw new ArgumentException("slotCount 는 2의 거듭제곱(≥8).", nameof(slotCount));
            _slotCount = slotCount;
            _slotSize  = slotSize;
        }

        public bool   RioBypassActive     => _rioActive == 1;
        public bool   FallbackActive      => _fallbackActive == 1;
        public long   ReceiveLatencyNanos => Interlocked.Read(ref _lastRecvLatencyNanos);
        public long   FramesReceived      => Interlocked.Read(ref _framesReceived);
        public string LastFallbackReason  => _lastFallbackReason;
        /// <summary>IGMP 멀티캐스트 그룹 조인 여부(라이브 마켓 피드 결선).</summary>
        public bool   MulticastJoined     => _multicastGroup != 0u;
        public uint   MulticastGroupAddr  => _multicastGroup;
        public uint   BoundInterfaceAddr  => _boundInterface;

        /// <summary>
        /// RIO 바인딩을 시도한다(UDP 시장 피드 수신). 성공 시 true + 드레인 스레드 가동. 실패 시 false 를
        /// 반환하되 예외를 삼키지 않고 사유를 <see cref="LastFallbackReason"/> 에 남긴다(호출자는 ActivateFallback 강등).
        /// <paramref name="multicastGroup"/> 지정 시 IGMP 조인(IP_ADD_MEMBERSHIP), <paramref name="nicInterface"/> 로 물리 NIC 고정.
        /// <paramref name="parser"/> 지정 시 발행 직전 스팬 위에서 제로카피 인라인 파싱(δ=0).
        /// </summary>
        public bool TryActivate(IPEndPoint bind, RioFrameSink? sink = null,
                                IPAddress? multicastGroup = null, IPAddress? nicInterface = null,
                                RioZeroCopyParser? parser = null)
        {
            _zeroCopyParser = parser;
            if (!OperatingSystem.IsWindows()) { _lastFallbackReason = "비-Windows 플랫폼 — RIO 미지원"; return false; }
            try
            {
                Span<byte> wsaData = stackalloc byte[512];
                if (WSAStartup(0x0202, wsaData) != 0) { _lastFallbackReason = "WSAStartup 실패"; return false; }
                _wsaStarted = true;

                _socket = WSASocketW(AF_INET, SOCK_DGRAM, IPPROTO_UDP, IntPtr.Zero, 0, WSA_FLAG_REGISTERED_IO);
                if (_socket == INVALID_SOCKET) { _lastFallbackReason = $"WSASocketW(REGISTERED_IO) 실패 err={Marshal.GetLastPInvokeError()}"; return CleanupFail(); }

                // RIO 확장 함수 테이블 취득 — 이 WSAIoctl 이 실패하면 곧 RIO 미지원(가장 흔한 강등 지점).
                Guid rioGuid = new(0x8509e081, 0x96dd, 0x4005, 0xb1, 0x65, 0x9e, 0x2e, 0xe8, 0xc7, 0x9e, 0x3f);
                RIO_EXTENSION_FUNCTION_TABLE table = default;
                table.cbSize = (uint)sizeof(RIO_EXTENSION_FUNCTION_TABLE);
                uint bytes;
                int rc = WSAIoctl(_socket, SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER,
                                  ref rioGuid, (uint)sizeof(Guid), out table, (uint)sizeof(RIO_EXTENSION_FUNCTION_TABLE),
                                  out bytes, IntPtr.Zero, IntPtr.Zero);
                if (rc == SOCKET_ERROR) { _lastFallbackReason = $"RIO 미지원(WSAIoctl err={Marshal.GetLastPInvokeError()})"; return CleanupFail(); }
                _rio = table;

                // ★멀티캐스트 수신 시 SO_REUSEADDR 를 bind 이전에 설정(그룹 포트 공유 수신 허용).
                if (multicastGroup != null) { int reuse = 1; setsockopt_i(_socket, SOL_SOCKET, SO_REUSEADDR, ref reuse, sizeof(int)); }

                // bind(수신 로컬 엔드포인트/물리 NIC).
                SOCKADDR_IN addr = default;
                addr.sin_family = AF_INET;
                addr.sin_port   = HostToNetworkPort((ushort)bind.Port);
                addr.sin_addr   = BitConverter.ToUInt32(bind.Address.MapToIPv4().GetAddressBytes(), 0);
                if (bind_(_socket, ref addr, sizeof(SOCKADDR_IN)) == SOCKET_ERROR)
                { _lastFallbackReason = $"bind 실패 err={Marshal.GetLastPInvokeError()}"; return CleanupFail(); }

                // ★IGMP 멀티캐스트 그룹 조인(특정 NIC 인터페이스) — 라이브 마켓 피드 수신 마운트.
                if (multicastGroup != null && !JoinMulticastNative(multicastGroup, nicInterface))
                    return CleanupFail();   // 사유는 JoinMulticastNative 가 기록

                // 등록 네이티브 버퍼(무복사 대상) + 슬롯 RIO_BUF 배열.
                nuint total = (nuint)_slotCount * (nuint)_slotSize;
                _buffer   = (byte*)NativeMemory.AllocZeroed(total);
                _bufDescs = (RIO_BUF*)NativeMemory.AllocZeroed((nuint)_slotCount * (nuint)sizeof(RIO_BUF));

                var pRegister = (delegate* unmanaged[Stdcall]<byte*, uint, IntPtr>)_rio.RIORegisterBuffer;
                _bufferId = pRegister(_buffer, total <= uint.MaxValue ? (uint)total : throw new InvalidOperationException("버퍼 초과"));
                if (_bufferId == IntPtr.Zero || _bufferId == (IntPtr)(-1)) { _lastFallbackReason = "RIORegisterBuffer 실패"; return CleanupFail(); }

                var pCreateCq = (delegate* unmanaged[Stdcall]<uint, IntPtr, IntPtr>)_rio.RIOCreateCompletionQueue;
                _cq = pCreateCq((uint)_slotCount, IntPtr.Zero);   // 폴링 모드(NotificationCompletion=NULL)
                if (_cq == IntPtr.Zero || _cq == (IntPtr)(-1)) { _lastFallbackReason = "RIOCreateCompletionQueue 실패"; return CleanupFail(); }

                var pCreateRq = (delegate* unmanaged[Stdcall]<nuint, uint, uint, uint, uint, IntPtr, IntPtr, IntPtr, IntPtr>)_rio.RIOCreateRequestQueue;
                _rq = pCreateRq(_socket, (uint)_slotCount, 1, 1, 1, _cq, _cq, IntPtr.Zero);
                if (_rq == IntPtr.Zero || _rq == (IntPtr)(-1)) { _lastFallbackReason = "RIOCreateRequestQueue 실패"; return CleanupFail(); }

                // 전 슬롯 RIOReceive 사전 게시(NIC 가 언제든 DMA 하도록).
                var pReceive = (delegate* unmanaged[Stdcall]<IntPtr, RIO_BUF*, uint, uint, void*, int>)_rio.RIOReceive;
                for (int i = 0; i < _slotCount; i++)
                {
                    _bufDescs[i].BufferId = _bufferId;
                    _bufDescs[i].Offset   = (uint)(i * _slotSize);
                    _bufDescs[i].Length   = (uint)_slotSize;
                    if (pReceive(_rq, &_bufDescs[i], 1, 0, (void*)(nint)i) == 0)
                    { _lastFallbackReason = $"RIOReceive 게시 실패 slot={i}"; return CleanupFail(); }
                }

                _rioActive = 1;
                _running   = 1;
                _drainThread = new Thread(() => DrainLoop(sink)) { IsBackground = true, Name = "RioBypassDrain", Priority = ThreadPriority.Highest };
                _drainThread.Start();
                return true;
            }
            catch (Exception ex)
            {
                _lastFallbackReason = "RIO 활성화 예외: " + ex.Message;   // 삼키지 않고 사유 보존
                return CleanupFail();
            }
        }

        // RIO 완료 드레인(전용 스레드·δ=0 핫패스). 완료마다 QPC 봉인 → (제로카피 파싱) → 링 발행 → 슬롯 재게시.
        private void DrainLoop(RioFrameSink? sink)
        {
            const int Batch = 64;
            RIORESULT* results = stackalloc RIORESULT[Batch];
            var pDequeue = (delegate* unmanaged[Stdcall]<IntPtr, RIORESULT*, uint, uint>)_rio.RIODequeueCompletion;
            var pReceive = (delegate* unmanaged[Stdcall]<IntPtr, RIO_BUF*, uint, uint, void*, int>)_rio.RIOReceive;
            while (_running == 1)
            {
                uint n = pDequeue(_cq, results, Batch);
                if (n == 0) { Thread.SpinWait(256); continue; }   // 무완료 — 저지연 스핀(양보 없이 나노초 폴)
                if (n == 0xFFFFFFFF)
                {
                    // CQ 오염(RIO_CORRUPT_CQ) — 복구 불가 종단. ★정직 강등(fail-closed): _rioActive 를 내려
                    // RioBypassActive 가 즉시 false 가 되어 상류가 무음 데드 피드를 인지하고, ActivateFallback 의
                    // `_rioActive==1` 가드가 풀려 관리형 폴백 강등이 가능해진다(무음 fail-open 방지).
                    _lastFallbackReason = "RIODequeueCompletion 오염(CQ) — RIO 종단, 관리형 강등 필요";
                    Interlocked.Exchange(ref _rioActive, 0);
                    _running = 0;
                    break;
                }
                long qpc = _nowTicks();
                for (uint i = 0; i < n; i++)
                {
                    int slot = (int)(nint)results[i].RequestContext;
                    // ★경계 검사(읽기·재게시 공통 선행) — 손상/외부 완료가 돌려준 미검증 slot 으로
                    //   _bufDescs(정확히 _slotCount 개만 할당) 를 인덱싱하면 네이티브 힙 OOB 쓰기 + 쓰레기
                    //   RIO_BUF 를 커널 DMA 대상으로 넘기게 된다. 두 경로 모두 이 가드 뒤에서만 slot 을 쓴다.
                    if ((uint)slot >= (uint)_slotCount)
                    {
                        _lastFallbackReason = "손상 완료 RequestContext(slot 범위초과) — 프레임 폐기(재게시 차단)";
                        continue;   // 이 완료만 폐기 — OOB 쓰기·커널 OOB RIO_BUF 전달 원천 차단
                    }
                    int len = (int)results[i].BytesTransferred;
                    if (len > _slotSize) len = _slotSize;   // NIC/드라이버 오염 길이 클램프(슬롯 경계 초과 스팬 방지)
                    if (results[i].Status == 0 && len > 0)
                    {
                        // ★제로카피: 등록 네이티브 버퍼를 중간 byte[] 없이 스팬으로 즉시 관측/파싱/발행.
                        var frame = new ReadOnlySpan<byte>(_buffer + (nint)slot * _slotSize, len);
                        _clock.ObserveLive(qpc);
                        _zeroCopyParser?.Invoke(frame, qpc);   // ★발행 직전 인라인 파싱(무할당·무캡처)
                        _ring.Publish(frame, qpc);             // 오버런은 링이 증명 회수(newest-wins)
                        Interlocked.Increment(ref _framesReceived);
                        sink?.Invoke(frame, qpc);              // (옵션) 직결 소비
                    }
                    // 슬롯 재게시(다음 패킷 수신) — 재사용(무복사). slot 은 위에서 [0,_slotCount) 검증됨.
                    _bufDescs[slot].Offset = (uint)(slot * _slotSize);
                    _bufDescs[slot].Length = (uint)_slotSize;
                    if (pReceive(_rq, &_bufDescs[slot], 1, 0, (void*)(nint)slot) == 0)
                    {
                        // ★fail-closed: 재게시 실패 시 그 슬롯이 outstanding 집합에서 영구 이탈해 수신 큐가
                        //   무음 고갈(데드 피드)한다. _rioActive 강등 + 드레인 종료로 무한 스핀을 끊고
                        //   상류·폴백이 인지·대응하게 한다(초기 게시(178줄) 치명 처리와 대칭).
                        _lastFallbackReason = $"RIOReceive 재게시 실패 slot={slot} err={Marshal.GetLastPInvokeError()}";
                        Interlocked.Exchange(ref _rioActive, 0);
                        _running = 0;
                        break;
                    }
                }
                long done = _nowTicks();
                Interlocked.Exchange(ref _lastRecvLatencyNanos, TicksToNanos(done - qpc));
            }
        }

        /// <summary>
        /// 안심 강등 — 기존 초고속 관리형 UDP 소켓 수신 파이프. RIO 미지원 환경에서 동일 CausalWeaveRing 에 발행한다.
        /// <paramref name="multicastGroup"/> 지정 시 관리형 IGMP 조인(MulticastOption·명령 명세). 조인 실패는 비-치명(강등 계속).
        /// (프로덕션에선 KiwoomBridgeClient/TossProductionSocket 수신 seam 에 결선 가능 — 여기선 자립 UDP 폴백.)
        /// </summary>
        public void ActivateFallback(IPEndPoint bind, RioFrameSink? sink = null,
                                     IPAddress? multicastGroup = null, IPAddress? nicInterface = null,
                                     RioZeroCopyParser? parser = null)
        {
            if (_fallbackActive == 1 || _rioActive == 1) return;
            _zeroCopyParser = parser;
            _fallbackSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (multicastGroup != null)
                _fallbackSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);  // bind 이전
            _fallbackSocket.Bind(bind);
            if (multicastGroup != null)
            {
                // ★관리형 IGMP 조인(명령 명세: MulticastOption) — RIO 미지원 환경 동일 CausalWeaveRing 결선.
                try
                {
                    var opt = nicInterface != null ? new MulticastOption(multicastGroup, nicInterface)
                                                   : new MulticastOption(multicastGroup);
                    _fallbackSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, opt);
                    _multicastGroup = BitConverter.ToUInt32(multicastGroup.MapToIPv4().GetAddressBytes(), 0);
                    if (nicInterface != null) _boundInterface = BitConverter.ToUInt32(nicInterface.MapToIPv4().GetAddressBytes(), 0);
                }
                catch (SocketException ex) { _lastFallbackReason = $"관리형 멀티캐스트 조인 실패(강등 계속) err={ex.ErrorCode}"; }
            }
            _fallbackActive = 1;
            _running = 1;
            _fallbackThread = new Thread(() => FallbackLoop(sink)) { IsBackground = true, Name = "RioFallbackRecv", Priority = ThreadPriority.AboveNormal };
            _fallbackThread.Start();
        }

        private void FallbackLoop(RioFrameSink? sink)
        {
            byte[] buf = new byte[_slotSize];       // 콜드(스레드 수명 1회) — 이후 재사용
            // ★재사용 주소 버퍼: 무할당 오버로드가 송신자 주소를 이 SocketAddress 에 in-place 로 기록한다.
            //   레거시 ReceiveFrom(byte[], ref EndPoint) 는 호출마다 SocketAddress(+주소 변동 시 IPEndPoint)를
            //   새로 할당하므로 buf 재사용만으로는 δ=0 이 아니다 — .NET 7+ Span 오버로드로 진짜 무할당 수신.
            var recvAddr = new SocketAddress(AddressFamily.InterNetwork);
            var sock = _fallbackSocket!;
            while (_running == 1)
            {
                int len;
                try { len = sock.ReceiveFrom(buf, SocketFlags.None, recvAddr); }   // δ=0: 주소를 recvAddr 에 in-place
                catch (SocketException) { if (_running == 0) break; continue; }
                catch (ObjectDisposedException) { break; }
                if (len <= 0) continue;
                long qpc = _nowTicks();
                var frame = new ReadOnlySpan<byte>(buf, 0, len);
                _clock.ObserveLive(qpc);
                _zeroCopyParser?.Invoke(frame, qpc);   // ★제로카피 인라인 파싱(발행 직전·무할당)
                _ring.Publish(frame, qpc);
                Interlocked.Increment(ref _framesReceived);
                sink?.Invoke(frame, qpc);
                Interlocked.Exchange(ref _lastRecvLatencyNanos, TicksToNanos(_nowTicks() - qpc));
            }
        }

        // ★네이티브 IGMP 조인(RIO 소켓) — IP_ADD_MEMBERSHIP(+ NIC 선택 IP_MULTICAST_IF). 실패 시 사유 기록 후 false.
        private bool JoinMulticastNative(IPAddress group, IPAddress? nic)
        {
            uint g   = BitConverter.ToUInt32(group.MapToIPv4().GetAddressBytes(), 0);
            uint ifc = nic != null ? BitConverter.ToUInt32(nic.MapToIPv4().GetAddressBytes(), 0) : 0u;   // 0=INADDR_ANY
            if (ifc != 0u) setsockopt_u(_socket, IPPROTO_IP, IP_MULTICAST_IF, ref ifc, sizeof(uint));    // 수신 NIC 고정
            IP_MREQ mreq = new() { imr_multiaddr = g, imr_interface = ifc };
            if (setsockopt_mreq(_socket, IPPROTO_IP, IP_ADD_MEMBERSHIP, ref mreq, sizeof(IP_MREQ)) == SOCKET_ERROR)
            { _lastFallbackReason = $"IP_ADD_MEMBERSHIP 실패 err={Marshal.GetLastPInvokeError()}"; return false; }
            _multicastGroup = g; _boundInterface = ifc;
            return true;
        }

        /// <summary>
        /// ★제로카피 L3 마켓데이터 헤더 피크(무할당·스팬 직파싱) — [u16 msgLen LE | u8 msgType | u8 flags | u32 field LE].
        /// 중간 byte[] 없이 ReadOnlySpan 에서 직접 파싱한다(RIO 등록버퍼/폴백버퍼 공용). 유효 헤더면 true.
        /// </summary>
        public static bool TryParseL3Header(ReadOnlySpan<byte> frame, out ushort msgLen, out byte msgType, out uint field)
        {
            msgLen = 0; msgType = 0; field = 0;
            if (frame.Length < 8) return false;
            msgLen  = BinaryPrimitives.ReadUInt16LittleEndian(frame);
            msgType = frame[2];
            field   = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(4));
            return true;
        }

        private bool CleanupFail() { CloseRio(); return false; }

        private void CloseRio()
        {
            _rioActive = 0;
            // 소켓 먼저 — outstanding RIOReceive 취소·소켓 바인딩 RQ 파기(NIC DMA 중단).
            if (_socket != INVALID_SOCKET) { closesocket(_socket); _socket = INVALID_SOCKET; }
            // ★RIO 규약 준수 순서: CQ 닫기 → 등록 버퍼 해제 → 네이티브 반환. 등록 버퍼는 RIORegisterBuffer 가
            //   커널 MDL 로 페이지락한 상태라, deregister 없이 NativeMemory.Free 하면 커널이 반환될 VA 범위를
            //   계속 참조할 여지가 남고(재바인딩 루프에선 CQ/등록 누수). CQ 는 소켓 독립 객체라 closesocket 로
            //   닫히지 않는다. early CleanupFail 경로는 _rio 가 default 이므로 함수 포인터 0 가드로 안전.
            if (_cq != IntPtr.Zero && _cq != (IntPtr)(-1) && _rio.RIOCloseCompletionQueue != IntPtr.Zero)
                ((delegate* unmanaged[Stdcall]<IntPtr, void>)_rio.RIOCloseCompletionQueue)(_cq);
            if (_bufferId != IntPtr.Zero && _bufferId != (IntPtr)(-1) && _rio.RIODeregisterBuffer != IntPtr.Zero)
                ((delegate* unmanaged[Stdcall]<IntPtr, void>)_rio.RIODeregisterBuffer)(_bufferId);
            if (_buffer   != null) { NativeMemory.Free(_buffer);   _buffer = null; }
            if (_bufDescs != null) { NativeMemory.Free(_bufDescs); _bufDescs = null; }
            _bufferId = _cq = _rq = IntPtr.Zero;
            if (_wsaStarted) { WSACleanup(); _wsaStarted = false; }
        }

        private long TicksToNanos(long ticks) => ticks <= 0 ? 0 : (long)(ticks * 1_000_000_000.0 / Stopwatch.Frequency);
        private static ushort HostToNetworkPort(ushort p) => (ushort)((p << 8) | (p >> 8));

        public void Dispose()
        {
            _running = 0;
            // 폴백 소켓 먼저 닫아 ReceiveFrom 블로킹을 깨운다(FallbackLoop 즉시 이탈). 폴백은 관리형 byte[] 를
            // 쓰므로 CloseRio 의 네이티브 해제와 무관 — 조인만 하면 된다.
            try { _fallbackSocket?.Dispose(); } catch { }
            try { _fallbackThread?.Join(2000); } catch { }
            _fallbackActive = 0;
            // ★드레인 스레드는 매 반복 _buffer/_bufDescs/_cq/_rq 를 역참조한다. 네이티브 해제(CloseRio)는
            //   드레인 종료가 '입증'된 후에만 수행한다. DrainLoop 은 블로킹 호출이 없어 _running=0 후 통상
            //   <1ms 내 종료하나, 운영자 주입 sink/parser 지연 가능성에 대비해 유계 Join 후 성공한 경우에만
            //   해제하고, 타임아웃 시엔 UAF 대신 네이티브 자원 누수를 허용한다(v5.5.0 로거 관례 계승).
            bool drainStopped;
            try { drainStopped = _drainThread == null || _drainThread.Join(2000); } catch { drainStopped = false; }
            if (drainStopped) CloseRio();
            else _lastFallbackReason = "Dispose: 드레인 Join 타임아웃 — 네이티브 자원 누수 허용(UAF 회피)";
        }

        // ── Native (LibraryImport 소스생성 — SYSLIB1054 회피, TreatWarningsAsErrors 준수) ──
        [StructLayout(LayoutKind.Sequential)]
        private struct RIO_EXTENSION_FUNCTION_TABLE
        {
            public uint   cbSize;
            public IntPtr RIOReceive, RIOReceiveEx, RIOSend, RIOSendEx, RIOCloseCompletionQueue,
                          RIOCreateCompletionQueue, RIOCreateRequestQueue, RIODequeueCompletion,
                          RIODeregisterBuffer, RIONotify, RIORegisterBuffer, RIOResizeCompletionQueue,
                          RIOResizeRequestQueue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RIO_BUF { public IntPtr BufferId; public uint Offset; public uint Length; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RIORESULT { public int Status; public uint BytesTransferred; public ulong SocketContext; public ulong RequestContext; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SOCKADDR_IN { public short sin_family; public ushort sin_port; public uint sin_addr; public ulong sin_zero; }

        [StructLayout(LayoutKind.Sequential)]
        private struct IP_MREQ { public uint imr_multiaddr; public uint imr_interface; }

        [LibraryImport("ws2_32.dll", SetLastError = true)]
        private static partial int WSAStartup(ushort wVersionRequested, Span<byte> lpWSAData);

        [LibraryImport("ws2_32.dll", SetLastError = true)]
        private static partial nuint WSASocketW(int af, int type, int protocol, IntPtr lpProtocolInfo, uint g, uint dwFlags);

        [LibraryImport("ws2_32.dll", SetLastError = true)]
        private static partial int WSAIoctl(nuint s, uint dwIoControlCode, ref Guid lpvInBuffer, uint cbInBuffer,
                                            out RIO_EXTENSION_FUNCTION_TABLE lpvOutBuffer, uint cbOutBuffer,
                                            out uint lpcbBytesReturned, IntPtr lpOverlapped, IntPtr lpCompletionRoutine);

        [LibraryImport("ws2_32.dll", EntryPoint = "bind", SetLastError = true)]
        private static partial int bind_(nuint s, ref SOCKADDR_IN name, int namelen);

        [LibraryImport("ws2_32.dll", EntryPoint = "setsockopt", SetLastError = true)]
        private static partial int setsockopt_i(nuint s, int level, int optname, ref int optval, int optlen);

        [LibraryImport("ws2_32.dll", EntryPoint = "setsockopt", SetLastError = true)]
        private static partial int setsockopt_u(nuint s, int level, int optname, ref uint optval, int optlen);

        [LibraryImport("ws2_32.dll", EntryPoint = "setsockopt", SetLastError = true)]
        private static partial int setsockopt_mreq(nuint s, int level, int optname, ref IP_MREQ optval, int optlen);

        [LibraryImport("ws2_32.dll", SetLastError = true)]
        private static partial int closesocket(nuint s);

        [LibraryImport("ws2_32.dll")]
        private static partial int WSACleanup();
    }
}
