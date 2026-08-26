// ============================================================================
// CrashResilientDumper — v4.95.0 과제2: 동결 찰나의 원자적 덤프 및 암호화 봉인
//
//  ForceFreeze 격발 순간 블랙박스 순환 링을 물리 봉인(Seal)해 추가 덮어쓰기를 차단하고, 유효 레코드를
//  %LOCALAPPDATA%\StockManager\blackbox.ms2dump 바이너리 블록으로 원자 영속화한다.
//
//  ── 정직 회계(정찰 렌즈 A/C 확정) ────────────────────────────────────────────
//  OrderAttestationService 에 관측 훅이 전무 → RegisterFreezeObserver 신규(과제3). 봉인 래치는 SecurityAssetVault
//  ._sealed/EnsureOpenOrThrow(EmergencyFreeze→영구봉인) 관례를, 무결성은 공용 Crc32.Compute + SHA256.HashData
//  (span,span·stackalloc) 를, OS 핸들 abort 는 MmfIpcFabric.Dispose(드레인+ReleasePointer+try/catch) 를 미러링.
//  경로 관례 %LOCALAPPDATA%\StockManager\ 재사용. XOR/SHA256/CRC 조합·순환 링 덤프는 신규 델타.
//
//  ── ★Think Further: 변조 방지 + 디스크 예외 시 OS 핸들 abort ──────────────────
//  · 파일 헤더에 전체 페이로드의 SHA256(변조 탐지) + Crc32(우발 오염 탐지) + XorSeed. 페이로드는 고속 정수
//    LCG 키스트림으로 XOR(간이 변조 방지 래치 — 평문 육안 판독·직접 편집 방지). ★confidentiality 아님(seed 저장):
//    SHA256 이 진짜 무결성 검증, XOR 는 경량 난독. 정직 표기.
//  · 원자 기록: temp(.tmp) 에 기록→Flush(true) 디스크 동기화→원자 rename. 부분 파일 미노출.
//  · 디스크 풀 등 예외 시 예외를 삼키지 않고 → SafeFileHandle.SetHandleAsInvalid(OS 핸들 강제 해제) + temp 삭제 +
//    링 영구 Seal + 실패 사유 박제 → 완전 무응답 격리 수렴(재기록 시도 없음). att 는 이미 동결됨.
//
//  ── 스레드 격리(핫패스 무간섭) ───────────────────────────────────────────────
//  동결 옵저버는 락-프리로 recorder.Seal() + 신호(ManualResetEventSlim.Set)만 — 나노초·무할당·무I/O. 디스크
//  덤프는 전용 BelowNormal 백그라운드 스레드(BlackboxDump)가 신호를 받아 1회(CAS 멱등) 수행 → 동결(핫패스)
//  스레드는 절대 I/O 하지 않음. 50ms 드레인·MMF IPC 동기 마진 무간섭.
//
//  ── liveArmed:false ─────────────────────────────────────────────────────────
//  덤퍼는 관측·영속화만 — IBroker/소켓/무장 술어 참조 0. 덤프 파일 생성이 이중키 게이트에 어떤 우회도 만들지 않음.
// ============================================================================
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>블랙박스 덤프 상태(텔레메트리). 0 유휴/1 봉인·덤프중/2 완료/3 실패.</summary>
    public enum BlackboxDumpState { Idle = 0, Sealed = 1, Completed = 2, Failed = 3 }

    /// <summary>동결 찰나 원자 덤프·암호 봉인 — 격리 백그라운드 스레드. att 동결 옵저버로 트리거.</summary>
    public sealed class CrashResilientDumper : IDisposable
    {
        private const long   Magic       = 0x4D5332_44554D50L;   // "MS2DUMP" 근사
        private const int    Version     = 1;
        // 레이아웃: [Header 56B][Payload N*64 (XOR)][CRC32 4B 트레일러]. ★CRC 를 말미에 둬 header+payload 를
        //   '연속 span' 하나로 Crc32.Compute(중간 CRC 구멍 없음 — Crc32 는 Compute 만 제공·Continue 없음).
        private const int    HeaderSize  = 56;
        private const int    MagicOff = 0, VerOff = 8, CountOff = 12, SeedOff = 16, ShaOff = 24;
        private const int    TrailerCrc = 4;

        private readonly MmfFlightRecorder      _recorder;
        private readonly OrderAttestationService _att;
        private readonly string                  _dumpPath;
        private readonly string                  _tmpPath;
        private readonly Func<long>              _nowTicks;
        private readonly ManualResetEventSlim    _signal = new(false);

        private Thread?       _thread;
        private volatile bool _stop;
        private int           _dumpStarted;
        private int           _stateCode;   // BlackboxDumpState
        private long          _dumpedCount;
        private volatile string _lastError = "";

        /// <param name="dumpDir">덤프 디렉토리(기본 %LOCALAPPDATA%\StockManager). 테스트는 임시 경로 주입.</param>
        /// <param name="nowTicks">단조 시계(XorSeed·결정론). 테스트 주입.</param>
        public CrashResilientDumper(MmfFlightRecorder recorder, OrderAttestationService attestation,
                                    string? dumpDir = null, Func<long>? nowTicks = null)
        {
            _recorder = recorder   ?? throw new ArgumentNullException(nameof(recorder));
            _att      = attestation ?? throw new ArgumentNullException(nameof(attestation));
            _nowTicks = nowTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;
            string dir = dumpDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StockManager");
            try { Directory.CreateDirectory(dir); } catch { /* 생성 실패는 덤프 시 예외로 재현 */ }
            _dumpPath = Path.Combine(dir, "blackbox.ms2dump");
            _tmpPath  = _dumpPath + ".tmp";
        }

        // ── 텔레메트리 ──────────────────────────────────────────────────────────
        public BlackboxDumpState State => (BlackboxDumpState)Volatile.Read(ref _stateCode);
        public long   DumpedCount => Interlocked.Read(ref _dumpedCount);
        public string LastError   => _lastError;
        public string DumpPath    => _dumpPath;

        /// <summary>배선: att 동결 옵저버 등록 + 백그라운드 덤프 스레드 기동. 옵저버는 Seal+신호만(무I/O).</summary>
        public void Start()
        {
            _att.RegisterFreezeObserver(_ =>
            {
                _recorder.Seal();                                 // ★동결 찰나 인덱스 물리 잠금(나노초·무할당)
                // ★C#4: Idle→Sealed 만 전이(CAS) — 재진입 ForceFreeze(덤프 실패 시)가 Failed/Completed 를 덮어 은폐하지 못하게.
                Interlocked.CompareExchange(ref _stateCode, (int)BlackboxDumpState.Sealed, (int)BlackboxDumpState.Idle);
                _signal.Set();                                    // 격리 스레드 기상(디스크 덤프)
            });
            if (_thread is not null) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "BlackboxDump", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        private void Loop()
        {
            try { _signal.Wait(); } catch { return; }
            if (_stop) return;
            DumpOnce();   // 1회(CAS 멱등)
        }

        /// <summary>덤프 1회 수행(멱등). 실패 시 OS 핸들 abort + 링 봉인 + 사유 박제(무응답 격리).</summary>
        public void DumpOnce()
        {
            if (Interlocked.CompareExchange(ref _dumpStarted, 1, 0) != 0) return;   // 멱등
            _recorder.Seal();
            _recorder.QuiesceAfterSeal();   // ★C#2: 진행 라이터 배수 후 스냅샷 — mid-write 없는 일관 덤프
            FileStream? fs = null;
            try
            {
                var buf = new FlightRecordEvent[_recorder.Capacity];   // 덤프 시점 할당(핫패스 아님)
                int n = _recorder.SnapshotRecent(buf);
                byte[] file = Encode(new ReadOnlySpan<FlightRecordEvent>(buf, 0, n), unchecked(_nowTicks() ^ 0x5A5A_A5A5_1234_5678L));

                fs = new FileStream(_tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                fs.Write(file, 0, file.Length);
                fs.Flush(flushToDisk: true);          // 디스크 물리 동기화
                fs.Dispose(); fs = null;
                File.Move(_tmpPath, _dumpPath, overwrite: true);   // 원자 rename(부분 파일 미노출)

                Interlocked.Exchange(ref _dumpedCount, n);
                Volatile.Write(ref _stateCode, (int)BlackboxDumpState.Completed);
            }
            catch (Exception ex)
            {
                // ★예외 삼키지 않고 격리 수렴: 핸들 해제(CloseHandle=Dispose) + temp 삭제 + 링 영구 봉인 + ForceFreeze + 사유 박제.
                //   ★C#5: SetHandleAsInvalid 는 CloseHandle 을 건너뛰어 OS 핸들을 누수(→temp 잠김·삭제 실패)시키므로 사용 금지.
                //   fs.Dispose() 가 정상 CloseHandle 을 수행해 배타 잠금 해제 → File.Delete 성공. 이후 상태를 Failed 로 확정(최종).
                _lastError = ex.GetType().Name + ": " + ex.Message;
                try { fs?.Dispose(); } catch { }                              // OS 핸들 정상 해제(누수 없음)
                try { if (File.Exists(_tmpPath)) File.Delete(_tmpPath); } catch { }
                _recorder.Seal();                     // 재기록 원천 차단(불가역)
                try { _att.ForceFreeze("CrashResilientDumper 덤프 실패 — 무응답 격리: " + _lastError); } catch { }
                Volatile.Write(ref _stateCode, (int)BlackboxDumpState.Failed);   // ★C#4: ForceFreeze(재진입 옵저버) 이후 마지막에 확정 — Failed 가 최종
            }
        }

        // ── 인코드/디코드(정적·테스트/재로드 검증 공용) ────────────────────────

        /// <summary>덤프 바이너리 인코드: [Header 56|Payload(XOR)|CRC32 트레일러]. SHA256=저장 페이로드, CRC32=header+payload(연속).</summary>
        public static byte[] Encode(ReadOnlySpan<FlightRecordEvent> records, long xorSeed)
        {
            int count = records.Length;
            int payloadLen = count * MmfFlightRecorder.EventSize;
            var file = new byte[HeaderSize + payloadLen + TrailerCrc];
            var span = file.AsSpan();

            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(span[MagicOff..], Magic);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[VerOff..], Version);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[CountOff..], count);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(span[SeedOff..], xorSeed);

            // 페이로드 = 레코드 바이트를 XOR 키스트림으로 난독.
            var payload = span.Slice(HeaderSize, payloadLen);
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(records).CopyTo(payload);
            XorKeystream(payload, xorSeed);

            // SHA256(저장 페이로드) → 헤더[ShaOff..].
            SHA256.HashData(payload, span.Slice(ShaOff, 32));
            // CRC32(header + payload) → 말미 트레일러(연속 span 하나·중간 구멍 없음).
            uint crc = Crc32.Compute(span[..(HeaderSize + payloadLen)]);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span[(HeaderSize + payloadLen)..], crc);
            return file;
        }

        /// <summary>재로드+검증(변조·오염 탐지). CRC·SHA·Magic 불일치면 false. 성공 시 원본 레코드 복원.</summary>
        public static bool TryLoadAndVerify(ReadOnlySpan<byte> file, out FlightRecordEvent[] records, out string error)
        {
            records = Array.Empty<FlightRecordEvent>();
            error = "";
            if (file.Length < HeaderSize + TrailerCrc) { error = "헤더 미달"; return false; }
            long magic = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(file[MagicOff..]);
            if (magic != Magic) { error = "매직 불일치"; return false; }
            int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(file[CountOff..]);
            long seed = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(file[SeedOff..]);
            if (count < 0) { error = "카운트 오염"; return false; }
            // ★C#9/C#10: long 곱으로 int 오버플로 차단 후 파일 길이로 결속 — 조작 count(예: 2^26+1)로 int 래핑→소형파일 통과→
            //   new FlightRecordEvent[count](수GB) OOM 크래시(fail-open)를 방지. file.Length(int 유한)와 등치 검증이 count 상한.
            long payloadLenL = (long)count * MmfFlightRecorder.EventSize;
            if (payloadLenL != (long)file.Length - HeaderSize - TrailerCrc) { error = "길이 불일치"; return false; }
            int payloadLen = (int)payloadLenL;

            var payload = file.Slice(HeaderSize, payloadLen);
            // CRC 검증(header+payload 연속).
            uint expectCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(file[(HeaderSize + payloadLen)..]);
            if (Crc32.Compute(file[..(HeaderSize + payloadLen)]) != expectCrc) { error = "CRC 불일치(오염)"; return false; }
            // SHA256 검증(변조).
            Span<byte> sha = stackalloc byte[32];
            SHA256.HashData(payload, sha);
            if (!sha.SequenceEqual(file.Slice(ShaOff, 32))) { error = "SHA256 불일치(변조)"; return false; }

            // 페이로드 복호(XOR 역연산) → 레코드 복원.
            var plain = new byte[payloadLen];
            payload.CopyTo(plain);
            XorKeystream(plain, seed);
            records = payloadLen == 0 ? Array.Empty<FlightRecordEvent>() : new FlightRecordEvent[count];
            if (payloadLen > 0) System.Runtime.InteropServices.MemoryMarshal.Cast<byte, FlightRecordEvent>(plain).CopyTo(records);
            return true;
        }

        /// <summary>파일 경로 재로드+검증 오버로드.</summary>
        public static bool TryLoadAndVerify(string path, out FlightRecordEvent[] records, out string error)
        {
            records = Array.Empty<FlightRecordEvent>(); error = "";
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) { error = "읽기 실패: " + ex.Message; return false; }
            return TryLoadAndVerify(bytes, out records, out error);
        }

        // 고속 정수 LCG 키스트림 XOR(자기역연산·결정론). ★난독 전용 — 무결성은 SHA256.
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
            _stop = true;
            try { _signal.Set(); } catch { }
            try { _thread?.Join(2000); } catch { }
            try { _signal.Dispose(); } catch { }
        }
    }
}
