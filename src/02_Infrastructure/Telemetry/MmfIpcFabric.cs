// ============================================================================
// MmfIpcFabric — v4.94.0 과제3: MMF 기반 물리 격리 레이턴시 제로 가속 패브릭 (unsafe·δ=0)
//
//  AnyCPU 메인 ↔ x86 키움 프록시 IPC 를 Windows Memory-Mapped File(MMF) 상주 공유메모리 + 포인터 기반
//  락-프리 SPSC 링으로 전환해, NamedPipe 왕복의 프로세스 문맥 전환 비용을 극한으로 압축한다.
//
//  ── 정직 회계(정찰 렌즈 D 확정) ──────────────────────────────────────────────
//  기존 MMF 사용처는 스냅샷 파일 I/O 2곳(StateHydrationEngine·MmfJournalStore)뿐 — 전부 copy 기반 관리형
//  Accessor 이고 unsafe/포인터/AllowUnsafeBlocks 는 코드베이스 전무. IPC 전송은 NamedPipe(KiwoomBridgeClient↔
//  KiwoomProxyHost). 따라서 상주 락-프리 링은 신규다. 재사용: seqlock 이디엄(RingBufferCore 무효화 마커→기록→
//  커밋 / s1==s2 이중독출)·프레임은 호출자 Span(BridgeSerializer.Pack 이 그 위에 pack 가능)·[Sequential,Pack=1]
//  블리터블 관례. v4.94.0 에서 AllowUnsafeBlocks 를 켜 이 파일에 한해 byte* 포인터 산술을 허용한다.
//
//  ── 공유메모리 레이아웃(x64/x86 폭 일치 = 전 필드 long·8정렬) ─────────────────
//   헤더(384B, 6 캐시라인 — 핫 필드 거짓공유 격리):
//     [0]Magic [8]Capacity [16]SlotStride [24]CorruptionFlag
//     [64]Head(발행 시퀀스) [128]Tail(소비 커서) [192]WriterHeartbeat [256]ReaderHeartbeat [320]Epoch
//   슬롯(Capacity×SlotStride): [0]Seq(long·Writing 마커=long.MinValue) [8]Len(long) [16]PublishTicks(long) [24]Payload
//
//  ── ★Think Further: 크래시/오염 데드락·오염 포인터 방어 ──────────────────────
//  언매니지드 공유메모리는 한쪽 프로세스가 크래시하거나 메모리 오염을 일으키고 쓰기를 멈추면, 상대가 영원히
//  대기(livelock)하거나 오염 포인터를 읽어 크래시할 수 있다. 방어를 이진 레벨로 심는다:
//    ① 세이프티 하트비트: 발행/소비마다 자기 하트비트 tick 갱신. 상대 하트비트가 20ms 단절이면 크래시 간주.
//    ② 매 연산 경계검증(bound-check): Magic·Capacity·SlotStride 헤더 재검증 + Head/Tail 이 [0,Capacity] 불변식
//       (head>=tail, head-tail<=capacity) + 슬롯 Seq==기대 시퀀스 + Len∈[0,maxPayload]. 하나라도 파손 →
//       메모리 스트림 즉시 격리 폐기 + CorruptionFlag 세팅 + ForceFreeze("MMF Fabric Corrupted").
//    ③ 라이터 중단 포이즌: 슬롯 Seq==Writing 마커가 지속되면(라이터 mid-write 크래시) 소비는 진행 불가 —
//       하트비트 ①이 20ms 내 이를 부패로 승격. 소비 seqlock 재검(s2==s1)으로 wrap 중 오염 독출도 격리.
//    ④ 공유 길이 불신: MMF 가 보고하는 값이 아니라 로컬 상수(_capacity/_slotStride)로만 경계 산정.
//  전부 unsafe 포인터 + Volatile/Interlocked(x64 TSO·8정렬) 로 무할당 수행.
//
//  ── liveArmed:false 불변 ─────────────────────────────────────────────────────
//  패브릭은 '프레임 전송'만 한다 — 외부 증권사 소켓을 열지 않는다(그것은 x86 프록시의 몫이고, 주문 송출은 여전히
//  하류 ZeroLeakProtocolIsolator + 이중키∧MasterIgnited 게이트가 지배). MMF 개통이 곧 발주 무장이 아니다 —
//  패브릭이 완전히 열려 프레임을 펌핑해도 DispatchArming.AlwaysFalse(미배선) 하에 실주문 확률은 수학적 0.
//
//  ── δ=0 증명(구조적) ─────────────────────────────────────────────────────────
//  TryPublish/TryConsume/Beat/CheckPeerAlive 는 (a)포인터 산술 (b)Volatile/Interlocked long 접근 (c)
//  Buffer.MemoryCopy(네이티브 memmove·관리형 할당 0) (d)스택 로컬만 사용 — new/박싱/클로저 0. 프레임은
//  호출자 제공 ReadOnlySpan/Span 을 fixed 로 고정(할당 없음). 문자열 사유는 오염(동결) 경로만.
// ============================================================================
using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>패브릭 역할 — Writer 는 MMF 헤더를 초기화(소유), Reader 는 검증만.</summary>
    public enum MmfFabricRole { Writer, Reader }

    /// <summary>MMF 상주 락-프리 SPSC 링 — unsafe 포인터 δ=0. 하트비트·경계검증·오염 시 ForceFreeze.</summary>
    public sealed unsafe class MmfIpcFabric : IDisposable
    {
        // ── 상수(레이아웃·마커) ────────────────────────────────────────────────
        private const long Magic         = 0x4D533245_4D4D4631L;   // "MS2EMMF1"
        private const long WritingMarker = long.MinValue;          // 슬롯 mid-write 표시

        private const int HeaderBytes    = 384;   // 6 캐시라인
        private const int MagicOffset      = 0;
        private const int CapacityOffset   = 8;
        private const int SlotStrideOffset = 16;
        private const int CorruptionOffset = 24;
        private const int HeadOffset       = 64;    // 캐시라인 1
        private const int TailOffset       = 128;   // 캐시라인 2
        private const int WriterHbOffset   = 192;   // 캐시라인 3
        private const int ReaderHbOffset   = 256;   // 캐시라인 4
        private const int EpochOffset      = 320;   // 캐시라인 5

        private const int SlotSeqOff   = 0;
        private const int SlotLenOff   = 8;
        private const int SlotTsOff    = 16;
        private const int SlotPayOff   = 24;

        private readonly MmfFabricRole            _role;
        private readonly OrderAttestationService? _att;
        private readonly MmfFlightRecorder?       _blackbox;   // v4.95.0 — 블랙박스(δ=0·null=no-op)
        private readonly Func<long>               _nowTicks;
        private readonly long                     _tickFrequency;    // ticks/sec(레이턴시 µs 환산)
        private readonly long                     _hbTimeoutTicks;   // 20ms in ticks
        private readonly int                       _capacity;        // 슬롯 수(2의 거듭제곱)
        private readonly long                      _mask;
        private readonly int                       _maxPayload;
        private readonly int                       _slotStride;
        private readonly long                      _totalBytes;

        private MemoryMappedFile?         _mmf;
        private MemoryMappedViewAccessor? _view;
        private byte*                     _base;
        private int                       _acquired;   // AcquirePointer 성공(Dispose 시 Release)
        private int                       _disposed;
        private int                       _corrupted;
        private long                      _inFlight;   // ★진행 중 hot 연산 수 — Dispose 가 언매핑 전 0 까지 드레인(use-after-free 차단)

        private long _lastLatencyMicros;
        private long _corruptionEvents;
        private volatile string _lastReason = "";

        /// <param name="mapName">공유 MMF 이름(양 프로세스 동일). page-file 백킹.</param>
        /// <param name="capacity">슬롯 수(2의 거듭제곱, 2 이상).</param>
        /// <param name="maxPayload">슬롯당 최대 페이로드(8 배수로 올림).</param>
        /// <param name="nowTicks">단조 시계(기본 Stopwatch.GetTimestamp — 프로세스 간 동일 도메인).</param>
        /// <param name="tickFrequency">ticks/sec(기본 Stopwatch.Frequency). 테스트는 주입.</param>
        /// <param name="heartbeatTimeoutMs">상대 하트비트 단절 임계(기본 20ms).</param>
        public MmfIpcFabric(string mapName, MmfFabricRole role, int capacity = 1024, int maxPayload = 4096,
                            OrderAttestationService? attestation = null,
                            Func<long>? nowTicks = null, long tickFrequency = 0,
                            int heartbeatTimeoutMs = 20, MmfFlightRecorder? blackbox = null)
        {
            if (string.IsNullOrWhiteSpace(mapName)) throw new ArgumentException("MMF 이름 필요", nameof(mapName));
            if (capacity < 2 || (capacity & (capacity - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(capacity), "2 이상의 2의 거듭제곱");
            if (maxPayload < 8) throw new ArgumentOutOfRangeException(nameof(maxPayload), "8 이상");

            _role          = role;
            _att           = attestation;
            _blackbox      = blackbox;
            _nowTicks      = nowTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;
            _tickFrequency = tickFrequency > 0 ? tickFrequency : System.Diagnostics.Stopwatch.Frequency;
            _hbTimeoutTicks= Math.Max(1, heartbeatTimeoutMs) * _tickFrequency / 1000L;
            _capacity      = capacity;
            _mask          = capacity - 1;
            _maxPayload    = (maxPayload + 7) & ~7;                    // 8 배수 올림(정렬)
            _slotStride    = SlotPayOff + _maxPayload;                 // 이미 8 배수(SlotPayOff=24)
            _totalBytes    = HeaderBytes + (long)_capacity * _slotStride;

            _mmf  = MemoryMappedFile.CreateOrOpen(mapName, _totalBytes, MemoryMappedFileAccess.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, _totalBytes, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _base = p + _view.PointerOffset;   // 뷰 시작 오프셋 보정(0 기대·방어)
            Volatile.Write(ref _acquired, 1);

            if (_role == MmfFabricRole.Writer) InitializeHeader();
        }

        // ── 텔레메트리(락-프리 독출) ────────────────────────────────────────────
        public bool   IsCorrupted       => Volatile.Read(ref _corrupted) != 0;
        public long   LastLatencyMicros => Volatile.Read(ref _lastLatencyMicros);
        public long   CorruptionEvents  => Interlocked.Read(ref _corruptionEvents);
        public string LastReason        => _lastReason;
        public int    Capacity          => _capacity;
        public MmfFabricRole Role       => _role;
        public long   Published         { get { if (!Enter()) return 0L; try { return Volatile.Read(ref HeadRef); } finally { Leave(); } } }
        public long   Consumed          { get { if (!Enter()) return 0L; try { return Volatile.Read(ref TailRef); } finally { Leave(); } } }
        public long   Depth             { get { if (!Enter()) return 0L; try { long h = Volatile.Read(ref HeadRef), t = Volatile.Read(ref TailRef); long d = h - t; return d < 0 ? 0 : d; } finally { Leave(); } } }

        // ★인플라이트 드레인 가드 — 포인터 역참조 hot 연산은 Enter/Leave 로 감싸고, Dispose 는 _inFlight==0 까지 드레인 후
        //   언매핑한다. Dispose 순서(타이머 vs 패브릭)와 무관하게 use-after-free(언매핑 후 역참조 AccessViolation)를 원천 차단.
        //   Enter 는 _disposed 를 증가 전·후 이중 검사(Dispose 와의 레이스에서 새 진입 배제). δ=0(Interlocked long — 힙 할당 0).
        private bool Enter()
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            Interlocked.Increment(ref _inFlight);
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _acquired) == 0 || _base == null)
            { Interlocked.Decrement(ref _inFlight); return false; }
            return true;
        }
        private void Leave() => Interlocked.Decrement(ref _inFlight);

        // ── 헤더 ref 접근자(unsafe) ─────────────────────────────────────────────
        private ref long HeadRef     => ref *(long*)(_base + HeadOffset);
        private ref long TailRef     => ref *(long*)(_base + TailOffset);
        private ref long WriterHbRef => ref *(long*)(_base + WriterHbOffset);
        private ref long ReaderHbRef => ref *(long*)(_base + ReaderHbOffset);
        private ref long EpochRef    => ref *(long*)(_base + EpochOffset);
        private ref long CorruptRef  => ref *(long*)(_base + CorruptionOffset);

        private void InitializeHeader()
        {
            Volatile.Write(ref HeadRef, 0L);
            Volatile.Write(ref TailRef, 0L);
            Volatile.Write(ref WriterHbRef, 0L);
            Volatile.Write(ref ReaderHbRef, 0L);
            Volatile.Write(ref EpochRef, 0L);
            Volatile.Write(ref CorruptRef, 0L);
            *(long*)(_base + CapacityOffset)   = _capacity;
            *(long*)(_base + SlotStrideOffset) = _slotStride;
            Volatile.Write(ref *(long*)(_base + MagicOffset), Magic);   // 매직 마지막 — 리더가 완전 초기화 후에만 통과
        }

        /// <summary>프레임 발행(Writer). 성공 true. 가득참=false(백프레셔·정상). 경계 파손=false+동결.</summary>
        public bool TryPublish(ReadOnlySpan<byte> frame)
        {
            if (!Enter()) return false;              // ★드레인 가드 — Dispose 와 겹쳐도 언매핑 후 역참조 차단
            try
            {
                if (IsCorrupted) return false;
                if (frame.Length > _maxPayload) { Poison("발행 프레임이 최대 페이로드 초과"); return false; }
                if (!VerifyHeaderIntegrity()) return false;

                long head = Volatile.Read(ref HeadRef);
                long tail = Volatile.Read(ref TailRef);
                if (!BoundsOk(head, tail)) { Poison("MMF Fabric Corrupted — 헤드/테일 경계 파손"); return false; }
                if (head - tail >= _capacity) return false;   // 가득참 — 정상 백프레셔(부패 아님)

                byte* slot = _base + HeaderBytes + (head & _mask) * _slotStride;
                long* seqp = (long*)(slot + SlotSeqOff);

                Volatile.Write(ref *seqp, WritingMarker);                    // 무효화(리더 격리)
                *(long*)(slot + SlotLenOff) = frame.Length;
                *(long*)(slot + SlotTsOff)  = _nowTicks();
                if (frame.Length > 0)
                    fixed (byte* src = frame)
                        Buffer.MemoryCopy(src, slot + SlotPayOff, _maxPayload, frame.Length);
                Volatile.Write(ref *seqp, head);                             // 커밋(슬롯 시퀀스=head)
                Volatile.Write(ref HeadRef, head + 1);
                Volatile.Write(ref EpochRef, Volatile.Read(ref EpochRef) + 1);   // 단일 라이터 — 비경합
                Volatile.Write(ref WriterHbRef, _nowTicks());
                _blackbox?.Record(FlightEventKind.MmfPublish, 0L, head, frame.Length, 0L, 3);
                return true;
            }
            finally { Leave(); }
        }

        /// <summary>프레임 소비(Reader). 성공 true+len. 빔/미준비/torn=false(len 0). 경계·하트비트 파손=false+동결.
        /// dest 는 최소 maxPayload 크기 권장(작으면 false).</summary>
        public bool TryConsume(Span<byte> dest, out int len)
        {
            len = 0;
            if (!Enter()) return false;              // ★드레인 가드
            try
            {
                if (IsCorrupted) return false;
                if (!VerifyHeaderIntegrity()) return false;

                long now = _nowTicks();
                long whb = Volatile.Read(ref WriterHbRef);
                if (whb != 0 && (now - whb) > _hbTimeoutTicks) { Poison("MMF Fabric Corrupted — 라이터 하트비트 단절"); return false; }

                long head = Volatile.Read(ref HeadRef);
                long tail = Volatile.Read(ref TailRef);
                if (!BoundsOk(head, tail)) { Poison("MMF Fabric Corrupted — 헤드/테일 경계 파손"); return false; }
                if (tail >= head) { Volatile.Write(ref ReaderHbRef, now); return false; }   // 빔(정상)

                byte* slot = _base + HeaderBytes + (tail & _mask) * _slotStride;
                long* seqp = (long*)(slot + SlotSeqOff);
                long s1 = Volatile.Read(ref *seqp);
                if (s1 == WritingMarker) { Volatile.Write(ref ReaderHbRef, now); return false; }   // 라이터 mid-write — 미준비
                if (s1 != tail) { Poison("MMF Fabric Corrupted — 슬롯 시퀀스 불일치(오염/추월)"); return false; }

                long frameLen = *(long*)(slot + SlotLenOff);
                if (frameLen < 0 || frameLen > _maxPayload) { Poison("MMF Fabric Corrupted — 프레임 길이 경계 파손"); return false; }
                if (frameLen > dest.Length) return false;   // dest 과소(호출자 사이징) — 부패 아님
                long pubTs = *(long*)(slot + SlotTsOff);

                if (frameLen > 0)
                    fixed (byte* d = dest)
                        Buffer.MemoryCopy(slot + SlotPayOff, d, dest.Length, frameLen);
                long s2 = Volatile.Read(ref *seqp);           // seqlock 재검 — 복사 중 라이터 덮어씀 격리
                if (s2 != s1) { Volatile.Write(ref ReaderHbRef, now); return false; }   // torn — 재시도(부패 아님)

                len = (int)frameLen;
                Volatile.Write(ref TailRef, tail + 1);
                if (pubTs != 0)
                {
                    long dt = now - pubTs; if (dt < 0) dt = 0;
                    Volatile.Write(ref _lastLatencyMicros, dt * 1_000_000L / _tickFrequency);
                }
                Volatile.Write(ref ReaderHbRef, _nowTicks());
                _blackbox?.Record(FlightEventKind.MmfConsume, 0L, tail, frameLen, Volatile.Read(ref _lastLatencyMicros), 3);
                return true;
            }
            finally { Leave(); }
        }

        /// <summary>자기 하트비트 갱신(주기 호출 권장). 역할별 슬롯에 현재 tick 기록.</summary>
        public void Beat()
        {
            if (!Enter()) return;                    // ★드레인 가드 — 타이머 콜백이 Dispose 와 겹쳐도 안전
            try
            {
                if (IsCorrupted) return;
                if (!VerifyHeaderIntegrity()) return;
                long off = _role == MmfFabricRole.Writer ? WriterHbOffset : ReaderHbOffset;
                Volatile.Write(ref *(long*)(_base + off), _nowTicks());
            }
            finally { Leave(); }
        }

        /// <summary>상대 생존 확인. 상대 하트비트 단절 시 오염 승격(ForceFreeze) + false. 미확립(0)=false(무동결).</summary>
        public bool CheckPeerAlive()
        {
            if (!Enter()) return false;              // ★드레인 가드
            try
            {
                if (IsCorrupted) return false;
                if (!VerifyHeaderIntegrity()) return false;
                long off = _role == MmfFabricRole.Writer ? ReaderHbOffset : WriterHbOffset;
                long hb = Volatile.Read(ref *(long*)(_base + off));
                if (hb == 0) return false;   // 아직 시작 안 함(미확립·무동결)
                if ((_nowTicks() - hb) > _hbTimeoutTicks) { Poison("MMF Fabric Corrupted — 상대 하트비트 단절"); return false; }
                return true;
            }
            finally { Leave(); }
        }

        // ── 내부 ────────────────────────────────────────────────────────────────

        /// <summary>Head/Tail 불변식: 비음수 ∧ head>=tail ∧ head-tail<=capacity. ★공유 값 불신 — 로컬 상수로만 판정.</summary>
        private bool BoundsOk(long head, long tail)
            => head >= 0 && tail >= 0 && head >= tail && (head - tail) <= _capacity;

        private bool VerifyHeaderIntegrity()
        {
            long magic = Volatile.Read(ref *(long*)(_base + MagicOffset));
            if (magic == 0) return false;                          // 라이터 미초기화 — 대기(무동결)
            if (magic != Magic) { Poison("MMF Fabric Corrupted — 매직 파손"); return false; }
            long cap = *(long*)(_base + CapacityOffset);
            long ss  = *(long*)(_base + SlotStrideOffset);
            if (cap != _capacity || ss != _slotStride) { Poison("MMF Fabric Corrupted — 레이아웃(용량/스트라이드) 파손"); return false; }
            if (Volatile.Read(ref CorruptRef) != 0) { IsolateLocal("공유 오염 플래그 감지"); return false; }
            return true;
        }

        /// <summary>부패 확정 — 로컬 격리 + 공유 CorruptionFlag 세팅 + ForceFreeze. 이후 전 연산 즉시 false.</summary>
        private void Poison(string reason)
        {
            Volatile.Write(ref _corrupted, 1);
            _lastReason = reason;
            Interlocked.Increment(ref _corruptionEvents);
            _blackbox?.Record(FlightEventKind.MmfPoison, 0L, 0L, 0L, 0L, 3);
            try { if (_base != null) Volatile.Write(ref CorruptRef, 1L); } catch { }
            try { _att?.ForceFreeze("MmfIpcFabric: " + reason); } catch { }
        }

        /// <summary>상대가 이미 부패를 알린 경우 — 로컬 격리만(중복 동결 방지·이미 상대가 동결).</summary>
        private void IsolateLocal(string reason)
        {
            Volatile.Write(ref _corrupted, 1);
            _lastReason = reason;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // ★언매핑 전 진행 중 hot 연산 드레인 — _disposed=1 이후 새 Enter 는 배제되므로 _inFlight 는 단조 감소.
            //   바운드 스핀(무한 대기 방지). Dispose 순서(타이머 vs 패브릭) 무관하게 use-after-free 원천 차단.
            var spin = new SpinWait();
            int guard = 0;
            while (Volatile.Read(ref _inFlight) > 0 && guard++ < 2_000_000) spin.SpinOnce();
            try { if (Volatile.Read(ref _acquired) != 0 && _view is { }) _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
            Volatile.Write(ref _acquired, 0);
            _base = null;
            try { _view?.Dispose(); } catch { }
            try { _mmf?.Dispose();  } catch { }
            _view = null; _mmf = null;
        }
    }
}
