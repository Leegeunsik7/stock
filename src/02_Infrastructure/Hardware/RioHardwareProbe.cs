// ============================================================================
// RioHardwareProbe — v7.0.0 Phase 7: 물리 NIC 하드웨어 사전 점검(Pre-Flight) 프로브
//
//  실전 RIO 커널 바이패스 배선 전에 하드웨어/OS 캡스(capabilities)를 진단한다:
//    · Winsock RIO 지원 여부(WSASocketW(REGISTERED_IO) + WSAIoctl(WSAID_MULTIPLE_RIO) 성공)
//    · 소켓 수신 버퍼 한계(기본/최대 실효 SO_RCVBUF)
//    · 물리 어댑터 열거(수·Up 수·최고 링크 속도)
//    · 하드웨어 타임스탬프 정렬(QPC 주파수/고해상도/분해능 ns)
//  RIO 미지원 감지 시 <see cref="RioProbeReport.RecommendSafeFallback"/> = true 로 안심 강등을 즉시 권고한다.
//
//  ★설계 정직 고지:
//   · 이 프로브는 '콜드 1회' 진단이다. NIC 열거(NetworkInterface.GetAllNetworkInterfaces)는 BCL 특성상 관리형
//     배열을 할당하나, 이는 핫패스가 아니라 1회 사전점검 스캔이다(δ=0 규칙은 반복 진단 루프/기록 루프에 적용).
//     RIO 캡스·소켓버퍼 네이티브 프로브 구간은 stackalloc/ref 로 δ=0 이다.
//   · 'HardwareTimestampAligned' 는 NIC PHY 하드웨어 타임스탬프(PTP)가 아니라 OS QPC(고해상도·≥1MHz) 정렬을
//     보고한다 — 진짜 NIC PHY 타임스탬프는 벤더 드라이버 API 가 필요하며 여기서 이식적으로 질의할 수 없다(정직 한계).
//   · 기존 v6.1.0 RioKernelBypassMesh 회로를 단 1줄도 건드리지 않는 독립 P/Invoke(중복 struct/import 는 각자 private).
// ============================================================================
using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace MS2E.Infrastructure
{
    /// <summary>RIO 하드웨어 사전 점검 결과(블리터블 값타입·힙 0). 텔레메트리/UI 매트릭스 직결.</summary>
    public readonly struct RioProbeReport
    {
        public readonly bool   RioSupported;               // Winsock RIO 확장 취득 성공
        public readonly int    AdapterCount;               // 물리 어댑터 수(loopback/tunnel 제외 전 총수)
        public readonly int    UpAdapterCount;             // OperationalStatus.Up 어댑터 수
        public readonly long   BestAdapterSpeedMbps;       // 최고 링크 속도(Mbps)
        public readonly int    MaxReceiveBufferBytes;      // 최대 실효 SO_RCVBUF(bytes)
        public readonly int    DefaultReceiveBufferBytes;  // 기본 SO_RCVBUF(bytes)
        public readonly long   QpcFrequency;               // Stopwatch.Frequency(Hz)
        public readonly bool   QpcHighResolution;          // Stopwatch.IsHighResolution
        public readonly double QpcResolutionNanos;         // QPC 1틱 분해능(ns)
        public readonly bool   HardwareTimestampAligned;   // QPC 고해상도·≥1MHz(OS 타임스탬프 정렬)
        public readonly bool   RecommendSafeFallback;      // RIO 미지원 → 안심 강등 즉시 권고
        public readonly int    ProbeCode;                  // 0 ok / 1 rio-unsupported / 2 non-windows / 3 probe-error

        public RioProbeReport(bool rioSupported, int adapterCount, int upAdapterCount, long bestSpeedMbps,
                              int maxRcvBuf, int defRcvBuf, long qpcFreq, bool qpcHiRes, double qpcResNanos,
                              bool hwTsAligned, bool recommendFallback, int probeCode)
        {
            RioSupported = rioSupported; AdapterCount = adapterCount; UpAdapterCount = upAdapterCount;
            BestAdapterSpeedMbps = bestSpeedMbps; MaxReceiveBufferBytes = maxRcvBuf; DefaultReceiveBufferBytes = defRcvBuf;
            QpcFrequency = qpcFreq; QpcHighResolution = qpcHiRes; QpcResolutionNanos = qpcResNanos;
            HardwareTimestampAligned = hwTsAligned; RecommendSafeFallback = recommendFallback; ProbeCode = probeCode;
        }
    }

    /// <summary>RIO 하드웨어 사전 점검 엔진. Probe() 로 진단 리포트 산출(콜드 1회). LibraryImport → partial.</summary>
    public sealed partial class RioHardwareProbe
    {
        private const int  AF_INET = 2, SOCK_DGRAM = 2, IPPROTO_UDP = 17;
        private const uint WSA_FLAG_REGISTERED_IO = 0x100;
        private const uint SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER = 0xC8000024;
        private const int  SOL_SOCKET = 0xFFFF, SO_RCVBUF = 0x1002;
        private static readonly nuint INVALID_SOCKET = unchecked((nuint)(~(nuint)0));
        private const int  SOCKET_ERROR = -1;
        private const int  ProbeMaxRcvBufTry = 16 * 1024 * 1024;   // 16MB 시도 → 실효 최대 재조회

        /// <summary>하드웨어/OS 캡스를 진단해 <see cref="RioProbeReport"/> 를 산출한다(콜드 1회·핫패스 아님).</summary>
        public RioProbeReport Probe()
        {
            long   qpcFreq  = Stopwatch.Frequency;
            bool   qpcHi    = Stopwatch.IsHighResolution;
            double qpcResNs = qpcFreq > 0 ? 1_000_000_000.0 / qpcFreq : 0.0;
            bool   hwTs     = qpcHi && qpcFreq >= 1_000_000;   // ≥1MHz 고해상도 QPC = OS 타임스탬프 정렬로 간주

            if (!OperatingSystem.IsWindows())
                return new RioProbeReport(false, 0, 0, 0, 0, 0, qpcFreq, qpcHi, qpcResNs, hwTs, true, 2);

            // ── 물리 NIC 열거(콜드 1회·BCL 할당 — 핫패스 아님) ──
            int adapters = 0, up = 0; long bestSpeed = 0;
            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces();
                adapters = nics.Length;
                for (int i = 0; i < nics.Length; i++)
                {
                    var ni = nics[i];
                    if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                    if (ni.OperationalStatus == OperationalStatus.Up)
                    {
                        up++;
                        long spd; try { spd = ni.Speed; } catch { spd = 0; }   // 일부 어댑터 Speed 미지원 방어
                        if (spd > bestSpeed) bestSpeed = spd;
                    }
                }
            }
            catch { /* 열거 실패 — 부분 카운트로 진단 계속(콜드) */ }
            long bestMbps = bestSpeed > 0 ? bestSpeed / 1_000_000L : 0L;

            // ── RIO 지원 + SO_RCVBUF 한계 δ=0 네이티브 프로브(stackalloc·ref) ──
            bool rio = false; int maxBuf = 0, defBuf = 0, code = 0;
            bool started = false; nuint s = INVALID_SOCKET;
            try
            {
                Span<byte> wsaData = stackalloc byte[512];
                if (WSAStartup(0x0202, wsaData) == 0)
                {
                    started = true;
                    s = WSASocketW(AF_INET, SOCK_DGRAM, IPPROTO_UDP, IntPtr.Zero, 0, WSA_FLAG_REGISTERED_IO);
                    if (s != INVALID_SOCKET)
                    {
                        Guid rioGuid = new(0x8509e081, 0x96dd, 0x4005, 0xb1, 0x65, 0x9e, 0x2e, 0xe8, 0xc7, 0x9e, 0x3f);
                        RIO_EXTENSION_FUNCTION_TABLE table = default;
                        table.cbSize = (uint)Marshal.SizeOf<RIO_EXTENSION_FUNCTION_TABLE>();
                        int rc = WSAIoctl(s, SIO_GET_MULTIPLE_EXTENSION_FUNCTION_POINTER, ref rioGuid, 16u,
                                          out table, table.cbSize, out _, IntPtr.Zero, IntPtr.Zero);
                        rio = rc != SOCKET_ERROR;

                        int val = 0, len = sizeof(int);
                        if (getsockopt(s, SOL_SOCKET, SO_RCVBUF, ref val, ref len) != SOCKET_ERROR) defBuf = val;
                        int want = ProbeMaxRcvBufTry;
                        setsockopt(s, SOL_SOCKET, SO_RCVBUF, ref want, sizeof(int));   // 최대 시도(커널이 실효치로 클램프)
                        val = 0; len = sizeof(int);
                        maxBuf = getsockopt(s, SOL_SOCKET, SO_RCVBUF, ref val, ref len) != SOCKET_ERROR ? val : defBuf;
                    }
                    else code = 3;
                }
                else code = 3;
            }
            catch { code = 3; }
            finally
            {
                if (s != INVALID_SOCKET) closesocket(s);
                if (started) WSACleanup();
            }
            if (!rio && code == 0) code = 1;   // 소켓/스타트업은 됐으나 RIO 확장 미취득

            return new RioProbeReport(rio, adapters, up, bestMbps, maxBuf, defBuf,
                                      qpcFreq, qpcHi, qpcResNs, hwTs, recommendFallback: !rio, probeCode: code);
        }

        // ── Native (LibraryImport — 독립 선언·RioKernelBypassMesh 무접촉) ──
        [StructLayout(LayoutKind.Sequential)]
        private struct RIO_EXTENSION_FUNCTION_TABLE
        {
            public uint   cbSize;
            public IntPtr RIOReceive, RIOReceiveEx, RIOSend, RIOSendEx, RIOCloseCompletionQueue,
                          RIOCreateCompletionQueue, RIOCreateRequestQueue, RIODequeueCompletion,
                          RIODeregisterBuffer, RIONotify, RIORegisterBuffer, RIOResizeCompletionQueue,
                          RIOResizeRequestQueue;
        }

        [LibraryImport("ws2_32.dll", SetLastError = true)]
        private static partial int WSAStartup(ushort wVersionRequested, Span<byte> lpWSAData);

        [LibraryImport("ws2_32.dll", SetLastError = true)]
        private static partial nuint WSASocketW(int af, int type, int protocol, IntPtr lpProtocolInfo, uint g, uint dwFlags);

        [LibraryImport("ws2_32.dll", SetLastError = true)]
        private static partial int WSAIoctl(nuint s, uint dwIoControlCode, ref Guid lpvInBuffer, uint cbInBuffer,
                                            out RIO_EXTENSION_FUNCTION_TABLE lpvOutBuffer, uint cbOutBuffer,
                                            out uint lpcbBytesReturned, IntPtr lpOverlapped, IntPtr lpCompletionRoutine);

        [LibraryImport("ws2_32.dll", EntryPoint = "getsockopt", SetLastError = true)]
        private static partial int getsockopt(nuint s, int level, int optname, ref int optval, ref int optlen);

        [LibraryImport("ws2_32.dll", EntryPoint = "setsockopt", SetLastError = true)]
        private static partial int setsockopt(nuint s, int level, int optname, ref int optval, int optlen);

        [LibraryImport("ws2_32.dll", SetLastError = true)]
        private static partial int closesocket(nuint s);

        [LibraryImport("ws2_32.dll")]
        private static partial int WSACleanup();
    }
}
