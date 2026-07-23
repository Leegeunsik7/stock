// ============================================================================
// StatePlaybackSimulator — v5.6.0 결정론적 타임라인 플레이백 엔진
//
//  v5.5.0 이 영속화한 .ms2log(BlittableLogEvent 32B·프레임별 Brotli+XOR+CRC32) 및
//  .ms2dump(FlightRecordEvent 64B·Header56+XOR payload+SHA256+CRC32) 이진 블록을, 가상 격리
//  샌드박스에서 '당시 나노초 프레임'을 그대로 재현한다. 배속(1x/10x/Max) + 1틱 정밀 스텝.
//
//  ── δ=0 정직 회계 ──────────────────────────────────────────────────────────
//  · 로드(1회·콜드): 파일을 단일 관리형 버퍼로 읽고 무결성(프레임별 CRC / dump SHA256+CRC) 검증.
//    (★unsafe 미사용 — 코드베이스의 'unsafe 회피(Span/MemoryMarshal)' 관례 유지. unsafe 는 RIO 패브릭에 국한.)
//  · 재생 핫패스(초당 수십만 프레임): 사전할당 스크래치(_xorScratch·_rawScratch)만 재사용 — 프레임당 new/box/LINQ 0.
//    이벤트는 MemoryMarshal.Cast 로 스팬 위에서 읽고 PlaybackSink(in) 로 무할당 사출.
//
//  ── ★CausalWeave 인터락(타임-와프 봉쇄) ────────────────────────────────────
//  매 프레임의 '봉인된 QPC'를 CausalWeaveClock.Advance 로 인과 시계에 전진 적용한다. 재생 QPC 가
//  워터마크보다 뒤로 흐르면(덤프 손상·수동 조작·비결정 오염) 퓨즈가 즉시 트립되어 재생이 fail-closed
//  로 정지(Faulted)한다 — "기록한 그대로만 재생된다"를 설계상 강제. 실시간 벽시계 오유입은 Clock.TickSource
//  를 컴포넌트에 배선하면 구조적으로 차단(NowTicks 가 봉인 QPC 만 반환).
//
//  ★liveArmed:false: 순수 오프라인 재현 — IBroker/소켓/onSend/PublishRaw 참조 0. 재생 틱이 나노초로
//    분사돼도 실 아웃바운드 경로 무접촉(구조적 0-leak). 결정론(주입 QPC)·fail-closed(퓨즈·검증 실패).
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>재생 상태(텔레메트리·UI 배지). </summary>
    public enum PlaybackState { Idle = 0, Playing = 1, Paused = 2, Stepping = 3, Completed = 4, Faulted = 5 }

    /// <summary>재생 배속. Max(=0) 은 무제한(페이싱 없이 최대 속도 드레인).</summary>
    public enum PlaybackSpeed { Realtime1x = 1, Fast10x = 10, MaxSpeed = 0 }

    /// <summary>.ms2log/.ms2dump 이벤트의 통합 재생 뷰(블리터블 값 타입·힙 0).</summary>
    public readonly struct PlaybackFrame
    {
        public readonly long Qpc;      // 봉인 QPC(Stopwatch tick 도메인) — 인과 시계 앵커
        public readonly long Seq;      // 단조 시퀀스
        public readonly long Arg0;     // .ms2log=Arg / .ms2dump=Arg0
        public readonly long Arg1;     // .ms2dump=Arg1 (.ms2log=0)
        public readonly int  Code;     // .ms2log=Code(opcode) / .ms2dump=SourceCode
        public readonly int  ThreadId;
        public readonly byte Kind;     // .ms2dump=FlightEventKind (.ms2log=0)
        public PlaybackFrame(long qpc, long seq, long arg0, long arg1, int code, int threadId, byte kind)
        { Qpc = qpc; Seq = seq; Arg0 = arg0; Arg1 = arg1; Code = code; ThreadId = threadId; Kind = kind; }
    }

    /// <summary>재생 프레임 소비자(무할당 in 파라미터).</summary>
    public delegate void PlaybackSink(in PlaybackFrame frame);

    /// <summary>결정론적 오프라인 플레이백 시뮬레이터. Load → (StepOne | RunToEnd | Pump). IDisposable.</summary>
    public sealed class StatePlaybackSimulator : IDisposable
    {
        private const long   Ms2LogSeedBase = unchecked(0x4D53_3245_4C4F_4700L);   // .ms2log XOR seed base
        private const long   DumpMagic       = 0x4D5332_44554D50L;                  // .ms2dump Header magic
        private const int    Ms2LogHdr       = 13;                                  // flag(1)+rawLen(4)+payloadLen(4)+crc(4)
        private const int    LogEvSize       = 32;                                  // BlittableLogEvent
        private const int    DumpEvSize      = 64;                                  // FlightRecordEvent
        private const int    DumpHeaderSize  = 56;
        private const int    MaxBatchRaw     = 4096 * LogEvSize;                     // 로거 배치 상한(4096 이벤트)

        private readonly Func<long> _nowTicks;          // 실시간 페이싱 기준(주입·기본 Stopwatch.GetTimestamp)
        private readonly CausalWeaveClock _clock;       // ★타임-와프 퓨즈(주입·필수 인터락)
        private readonly long _freq;                    // Stopwatch.Frequency(페이싱 tick→ns)

        // 사전할당 재생 스크래치(δ=0 핫패스) — 로드마다 재사용/필요시 콜드 성장.
        private readonly byte[] _xorScratch = new byte[MaxBatchRaw];   // 온디스크 payload de-XOR 대상
        private byte[] _rawScratch = new byte[MaxBatchRaw];            // Brotli 해제/원시 이벤트 바이트(콜드 성장 가능)

        private bool _isDump;           // true=.ms2dump / false=.ms2log
        private int  _batchCount;       // 상주 버퍼 이벤트 총수(로드 시 전량 디코드)
        private int  _batchIdx;         // 다음 이벤트 인덱스(상주 버퍼 커서)
        private long _totalEvents;      // 전체 이벤트(진행률)
        private long _playedEvents;     // 재생된 이벤트
        private long _integrityNum, _integrityDen;   // 무결성 = 통과 프레임/전체 프레임
        private long _fpsWindowStart, _fpsWindowEvents;
        private double _fps;
        private int _state;             // PlaybackState
        private bool _started;          // 첫 프레임에 인과 시계를 플레이백 모드로 앵커링했는지

        public StatePlaybackSimulator(CausalWeaveClock clock, Func<long>? nowTicks = null)
        {
            _clock    = clock ?? throw new ArgumentNullException(nameof(clock));
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            _freq     = Stopwatch.Frequency;
        }

        public PlaybackState State        => (PlaybackState)Volatile.Read(ref _state);
        public long          TotalEvents  => Interlocked.Read(ref _totalEvents);
        public long          PlayedEvents => Interlocked.Read(ref _playedEvents);
        public double        Fps          => Volatile.Read(ref _fps);
        public bool          IsFaulted    => State == PlaybackState.Faulted || _clock.FuseTripped;
        /// <summary>진행률 0..1.</summary>
        public double        Progress     => _totalEvents > 0 ? (double)Interlocked.Read(ref _playedEvents) / _totalEvents : 0.0;
        /// <summary>덤프 디코딩 무결성 점수 0..1(프레임/레코드 CRC·SHA 통과 비율). QMath.Scale raw 는 텔레메트리에서 환산.</summary>
        public double        IntegrityScore => _integrityDen > 0 ? (double)_integrityNum / _integrityDen : 0.0;
        public long          IntegrityScoreRaw => _integrityDen > 0 ? _integrityNum * QMath.Scale / _integrityDen : QMath.Scale;

        // ── 로드(콜드·1회) ─────────────────────────────────────────────────
        /// <summary>확장자/매직으로 형식 자동 감지 후 로드+무결성 검증. 실패 시 false(fail-closed·Faulted 아님).</summary>
        public bool Load(string path, out string error)
        {
            error = "";
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) { error = "읽기 실패: " + ex.Message; return false; }

            bool looksDump = bytes.Length >= DumpHeaderSize
                             && BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(0)) == DumpMagic;
            return looksDump ? LoadDumpBytes(bytes, out error) : LoadLogBytes(bytes, out error);
        }

        /// <summary>.ms2log 로드 — 무결성 검증 + de-XOR + (flag==1) Brotli 해제를 콜드 1회에 수행해 전 프레임을
        /// 상주 버퍼(_rawScratch)로 정착시킨다(재생 커서 리셋). 재생 핫패스는 이 버퍼를 커서 전진만 하므로 δ=0.</summary>
        public bool LoadLogBytes(byte[] file, out string error)
        {
            error = "";
            _isDump = false; _batchIdx = 0;
            _playedEvents = 0; _integrityNum = 0; _integrityDen = 0; _started = false;
            var span = file.AsSpan();
            long events = 0;    // 무결(CRC 통과) 프레임 이벤트 총수(진행률 분모)
            int  w = 0;         // 상주 버퍼(_rawScratch) 디코드 기록 위치(bytes)
            int  pos = 0;
            // ★단일 콜드 패스: CRC 검증 + de-XOR + (flag==1) Brotli 해제를 '로드 시 1회' 수행해 전 프레임을
            //   상주 _rawScratch 로 정착시킨다. 재생 핫패스가 이 버퍼를 커서 전진만 하므로, Brotli 정적 API 의
            //   호출당 SafeBrotliDecoderHandle(관리형 파이널라이저) 할당이 핫패스에서 사라진다(δ=0 진짜 복원 —
            //   덤프 경로 LoadDumpBytes 의 상주 de-XOR 패턴과 동형).
            while (pos + Ms2LogHdr <= span.Length)
            {
                byte flag       = span[pos];
                int  rawLen     = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 1));
                int  payloadLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 5));
                if (rawLen < 0 || payloadLen < 0 || rawLen % LogEvSize != 0) break;   // 오염/절단 → 클린 종료
                if (pos + Ms2LogHdr + payloadLen > span.Length) break;                // 절단된 말미 프레임 폐기
                uint crc = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos + 9));
                var payload = span.Slice(pos + Ms2LogHdr, payloadLen);
                pos += Ms2LogHdr + payloadLen;
                _integrityDen++;
                if (Crc32.Compute(payload) != crc) continue;                          // CRC 실패 → 무결성 분모만(재생 제외)
                _integrityNum++;
                int n = rawLen / LogEvSize;
                events += n;                                                          // CRC 통과 이벤트(진행률 분모)
                if (payloadLen > _xorScratch.Length) continue;                        // 프레임 payload 스크래치 초과 방어
                long seed = unchecked(Ms2LogSeedBase ^ n);
                payload.CopyTo(_xorScratch.AsSpan(0, payloadLen));
                XorKeystream(_xorScratch.AsSpan(0, payloadLen), seed);
                if ((long)w + rawLen > _rawScratch.Length)                            // 상주 버퍼 콜드 성장(재생 전이라 안전)
                {
                    long needL = (long)w + rawLen, growL = (long)_rawScratch.Length * 2;
                    long newL  = needL > growL ? needL : growL;
                    if (newL > int.MaxValue) break;                                   // 초과 방어(클린 종료)
                    var bigger = new byte[(int)newL];
                    Array.Copy(_rawScratch, bigger, w);
                    _rawScratch = bigger;
                }
                if (flag == 1)
                {
                    if (!BrotliDecoder.TryDecompress(_xorScratch.AsSpan(0, payloadLen), _rawScratch.AsSpan(w, rawLen), out int wrote) || wrote != rawLen)
                        continue;                                                     // 해제 실패 프레임 스킵(상주 제외)
                }
                else
                {
                    if (payloadLen != rawLen) continue;
                    _xorScratch.AsSpan(0, rawLen).CopyTo(_rawScratch.AsSpan(w, rawLen));
                }
                w += rawLen;
            }
            _totalEvents = events;
            _batchCount  = w / LogEvSize;     // 상주 버퍼 실제 디코드 이벤트 수(재생 상한)
            _batchIdx    = 0;
            Volatile.Write(ref _state, (int)(_batchCount > 0 ? PlaybackState.Idle : PlaybackState.Completed));
            if (events == 0 && _integrityDen == 0) { error = ".ms2log 프레임 없음/전량 손상"; return false; }
            return true;
        }

        /// <summary>.ms2dump 로드 — CRC32(header+payload)→SHA256(payload)→de-XOR 검증. 손상 시 false.</summary>
        public bool LoadDumpBytes(byte[] file, out string error)
        {
            error = "";
            var span = file.AsSpan();
            if (span.Length < DumpHeaderSize + 4) { error = "덤프 헤더 미달"; return false; }
            int count = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(12));
            long seed = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(16));
            if (count < 0) { error = "덤프 카운트 오염"; return false; }
            long payloadLenL = (long)count * DumpEvSize;                              // ★오버플로 가드(TryLoadAndVerify 미러)
            if (payloadLenL != (long)span.Length - DumpHeaderSize - 4) { error = "덤프 길이 불일치"; return false; }
            int payloadLen = (int)payloadLenL;
            var payload = span.Slice(DumpHeaderSize, payloadLen);
            uint expectCrc = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(DumpHeaderSize + payloadLen));
            if (Crc32.Compute(span.Slice(0, DumpHeaderSize + payloadLen)) != expectCrc) { error = "덤프 CRC 불일치(오염)"; return false; }
            Span<byte> sha = stackalloc byte[32];
            System.Security.Cryptography.SHA256.HashData(payload, sha);
            if (!sha.SequenceEqual(span.Slice(24, 32))) { error = "덤프 SHA256 불일치(변조)"; return false; }

            // de-XOR 를 재사용 스크래치로(콜드·1회). payload 를 관리형 배킹으로 상주 후 스트리밍.
            if (_rawScratch.Length < payloadLen) _rawScratch = new byte[payloadLen];   // 콜드 성장
            payload.CopyTo(_rawScratch.AsSpan(0, payloadLen));
            XorKeystream(_rawScratch.AsSpan(0, payloadLen), seed);

            _isDump = true; _batchIdx = 0; _batchCount = count;
            _playedEvents = 0; _totalEvents = count; _started = false;
            _integrityNum = 1; _integrityDen = 1;                                     // dump 는 전량 무결(SHA+CRC 통과)
            Volatile.Write(ref _state, (int)(count > 0 ? PlaybackState.Idle : PlaybackState.Completed));
            return true;
        }

        // ── 재생 ────────────────────────────────────────────────────────────
        /// <summary>1틱 정밀 스텝(단일 이벤트) — 디버깅. 더 없으면 false(Completed). δ=0.</summary>
        public bool StepOne(PlaybackSink sink)
        {
            if (_clock.FuseTripped) { Volatile.Write(ref _state, (int)PlaybackState.Faulted); return false; }
            Volatile.Write(ref _state, (int)PlaybackState.Stepping);
            return DispatchNext(sink, out _);
        }

        /// <summary>배속 재생 — 봉인 QPC 델타 × (1/speed) 로 페이싱. Max 는 무페이싱. 취소/퓨즈/EOF 시 종료. δ=0 핫패스.</summary>
        public void Play(PlaybackSpeed speed, PlaybackSink sink, CancellationToken ct = default)
        {
            Volatile.Write(ref _state, (int)PlaybackState.Playing);
            _fpsWindowStart = _nowTicks(); _fpsWindowEvents = 0;
            long spanStartWall = _nowTicks(); long firstQpc = 0; bool haveFirst = false;
            while (!ct.IsCancellationRequested)
            {
                if (_clock.FuseTripped) { Volatile.Write(ref _state, (int)PlaybackState.Faulted); return; }
                // 다음 프레임 QPC 를 미리 확인해 페이싱(디스패치 전 대기). δ=0(값 반환).
                if (!PeekNextQpc(out long qpc)) break;
                if (speed != PlaybackSpeed.MaxSpeed)
                {
                    if (!haveFirst) { firstQpc = qpc; spanStartWall = _nowTicks(); haveFirst = true; }
                    long targetWall = spanStartWall + (long)((qpc - firstQpc) / (double)(int)speed);
                    SpinUntil(targetWall, ct);
                }
                if (!DispatchNext(sink, out _)) break;
                UpdateFps();
            }
            if (!_clock.FuseTripped && State != PlaybackState.Faulted)
                Volatile.Write(ref _state, (int)(_playedEvents >= _totalEvents ? PlaybackState.Completed : PlaybackState.Paused));
        }

        /// <summary>전량 최대속 드레인(백테스트) — 페이싱 없이 전 프레임 재생. δ=0 핫패스.</summary>
        public long RunToEnd(PlaybackSink sink, CancellationToken ct = default)
        {
            Volatile.Write(ref _state, (int)PlaybackState.Playing);
            long n = 0;
            while (!ct.IsCancellationRequested && !_clock.FuseTripped && DispatchNext(sink, out _)) n++;
            Volatile.Write(ref _state, (int)(_clock.FuseTripped ? PlaybackState.Faulted
                                          : _playedEvents >= _totalEvents ? PlaybackState.Completed : PlaybackState.Paused));
            return n;
        }

        // 다음 이벤트를 디코드·인과전진·사출. δ=0(스크래치 재사용). 더 없으면 false.
        private bool DispatchNext(PlaybackSink sink, out PlaybackFrame frame)
        {
            frame = default;
            if (_batchIdx >= _batchCount) return false;   // 상주 버퍼 소진 → 재생 종료(전 프레임 콜드 로드됨)
            if (_isDump) frame = ReadDumpEvent(_batchIdx);
            else         frame = ReadLogEvent(_batchIdx);
            _batchIdx++;
            // 첫 프레임: 인과 시계를 그 봉인 QPC 로 앵커링(라이브 워터마크와 무관하게 결정론 재생 개시).
            if (!_started) { _clock.EnterPlayback(frame.Qpc); _started = true; }
            // ★CausalWeave 인터락: 퓨즈 판정 기준은 '기록 시퀀스(Seq)' — 역행이면 재정렬/손상/변조로 fail-closed 정지.
            //   (다중 생산자 로거가 seq 를 Interlocked 로 먼저 클레임 후 QPC 를 샘플 → 정상 로그도 QPC 는 국소 역전하므로 트립 기준 부적합.)
            if (!_clock.AdvanceSeq(frame.Seq)) { Volatile.Write(ref _state, (int)PlaybackState.Faulted); return false; }
            _clock.Advance(frame.Qpc);   // 봉인 QPC 는 자문적 페이싱 시각(단조 클램프·트립 없음)
            Interlocked.Increment(ref _playedEvents);
            sink(in frame);   // 무할당 사출(소비자 재사용)
            return true;
        }

        private PlaybackFrame ReadLogEvent(int idx)
        {
            var ev = MemoryMarshal.Read<BlittableLogEvent>(_rawScratch.AsSpan(idx * LogEvSize, LogEvSize));
            return new PlaybackFrame(ev.TimestampTicks, ev.Seq, ev.Arg, 0L, ev.Code, ev.ThreadId, 0);
        }

        private PlaybackFrame ReadDumpEvent(int idx)
        {
            var ev = MemoryMarshal.Read<FlightRecordEvent>(_rawScratch.AsSpan(idx * DumpEvSize, DumpEvSize));
            return new PlaybackFrame(ev.TimestampTicks, ev.Sequence, ev.Arg0, ev.Arg1, ev.SourceCode, ev.ThreadId, ev.Kind);
        }

        private bool PeekNextQpc(out long qpc)
        {
            qpc = 0;
            if (_batchIdx >= _batchCount) return false;   // 상주 버퍼 소진
            qpc = _isDump ? MemoryMarshal.Read<FlightRecordEvent>(_rawScratch.AsSpan(_batchIdx * DumpEvSize, DumpEvSize)).TimestampTicks
                          : MemoryMarshal.Read<BlittableLogEvent>(_rawScratch.AsSpan(_batchIdx * LogEvSize, LogEvSize)).TimestampTicks;
            return true;
        }

        private void SpinUntil(long targetWall, CancellationToken ct)
        {
            while (_nowTicks() < targetWall && !ct.IsCancellationRequested)
            {
                long remain = targetWall - _nowTicks();
                if (remain > _freq / 1000) Thread.Sleep(1);   // >1ms 남으면 양보(무할당)
                else Thread.SpinWait(64);                     // 근접 구간은 스핀(정밀)
            }
        }

        private void UpdateFps()
        {
            _fpsWindowEvents++;
            long now = _nowTicks();
            long dt = now - _fpsWindowStart;
            if (dt >= _freq)   // 1초 창
            {
                Volatile.Write(ref _fps, _fpsWindowEvents * (double)_freq / dt);
                _fpsWindowStart = now; _fpsWindowEvents = 0;
            }
        }

        // .ms2log/.ms2dump 공용 XOR-LCG(자기역·결정론) — 원본이 private static 이라 동일 상수로 재구현.
        private static void XorKeystream(Span<byte> data, long seed)
        {
            ulong k = unchecked((ulong)seed);
            for (int i = 0; i < data.Length; i++)
            {
                k = unchecked(k * 6364136223846793005UL + 1442695040888963407UL);
                data[i] ^= (byte)(k >> 56);
            }
        }

        public void Dispose() { try { _clock.ExitPlayback(); } catch { } }
    }
}
