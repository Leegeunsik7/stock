// ============================================================================
// MmfFlightRecorder — v4.95.0 과제1: 무할당 순환 커널 블랙박스 (멀티라이터 tri-state 링)
//
//  전역 동결(ForceFreeze) 격발 직전 시스템 내부의 모든 핫패스 이산 사건을 단 한 글자도 유실 없이 기록하는
//  무할당(δ=0) 순환 비행 기록계. 사전 할당된 blittable 버퍼(FlightRecordEvent[] 100,000 슬롯) 위에서 동작한다.
//
//  ── 정직 회계(정찰 렌즈 B/D 확정) ────────────────────────────────────────────
//  기존 CrashRecoveryJournaler 는 부팅용 소형 단발 스냅샷(64슬롯 전량 재기록·순환 아님), MmfIpcFabric 은 SPSC
//  (단일 라이터)·오염 시 fail-closed 폐기 — 둘 다 '다중 핫패스 동시 push + 순환 덮어쓰기 + 크래시 후 생존·판독'
//  용도로 재사용 불가. 따라서 멀티라이터 순환 블랙박스는 신규다. 재사용: [Sequential,Pack=1] blittable(JournalRecord
//  51B 선례)·seqlock 무효화→커밋 이디엄(RingBufferCore/MmfIpcFabric)·Crc32/SHA256(덤프)·PaddedSequence 캐시라인.
//
//  ── ★Think Further: 동결 찰나의 Partial Write & Wrap-Torn 봉쇄 ────────────────
//  수많은 스레드가 동시에 기록하는 와중 ForceFreeze 가 터지면, 한 스레드가 64B 중 절반만 쓴 상태(Partial Write)로
//  슬롯이 동결돼 데이터가 오염되거나 인덱스가 뒤틀릴 수 있다. 방어:
//    ① 인덱스 원자 선점: Interlocked.Increment(_writeSeq) 로 각 라이터가 유일한 단조 시퀀스를 받고 slot=(seq-1)&mask.
//       순환 재사용은 정상(매 _capacity 클레임마다 슬롯 재사용) — '동시 겨냥 불가'가 아니라, 선점됐다 재개한 랩(구식)
//       라이터가 최신 슬롯을 덮지 않도록 클레임 후 랩 가드(_writeSeq-seq>=_capacity → DroppedLapped)로 격리한다.
//    ② 슬롯 tri-state(volatile int State: Empty=0/Writing=1/Ready=2): 라이터 Writing→64B 기록→Ready(release).
//       스냅샷은 Ready 아닌 슬롯을 안전 무시 → Partial Write 격리.
//    ③ ★wrap-torn 재검: 스냅샷이 Ready 확인→복사→Ready 재확인 + '라이브 슬롯 Sequence 재독출'이 기대 시퀀스와
//       일치할 때만 채택(복사본 Sequence 만 믿으면 복사 중 풀사이클 덮어쓰기의 offset0-옛값 tearing 을 놓침).
//    ④ Seal + Quiesce: 동결 옵저버가 Seal() → 새 Record 는 no-op(DroppedWhileSealed). 덤프는 QuiesceAfterSeal 로
//       진행 라이터(_inFlight)를 0 까지 배수한 뒤 스냅샷 → mid-write 없는 일관 덤프(직전 in-flight tail 포함=포렌식 유익).
//
//  ── 스레드 격리(핫패스 무간섭 보증) ──────────────────────────────────────────
//  Record 는 (a)null-가드 시 완전 no-op (b)활성 시에도 사전할당 슬롯에 struct 복사 + Interlocked long + volatile
//  int 만 — new/박싱/클로저/문자열/락 0. 50ms 드레인·MMF IPC 자료구조와 메모리를 공유하지 않으며(자체 링·자체
//  카운터), 디스크 덤프는 전액 격리 백그라운드 스레드(CrashResilientDumper). 링 미배선 핫패스는 비용 0.
//
//  ── liveArmed:false ─────────────────────────────────────────────────────────
//  기록계는 '관측'만 한다 — 발주/무장 상태를 만지지 않고 IBroker/소켓 참조가 없다. Record·덤프가 진행돼도
//  이중키∧MasterIgnited 게이트에 어떤 우회 바인딩도 만들지 않는다.
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>블랙박스 이산 사건 분류(byte). 핫패스 엔진별 통과 사건.</summary>
    public enum FlightEventKind : byte
    {
        None = 0,
        OrderForwarded = 1, OrderGated = 2, OrderSendError = 3,   // GatedOrderDispatcher
        RiskAdmit = 10, RiskReject = 11, RiskEscalate = 12,       // PreTradeRiskShield
        MmfPublish = 20, MmfConsume = 21, MmfPoison = 22,         // MmfIpcFabric
        NewsIngest = 30, NewsBreakerTrip = 31,                    // NewsIntelController
        CutoverArmed = 40, CutoverLive = 41, CutoverRollback = 42, // v4.97.0 AtomicCutoverGate
        CanaryTrip = 43, DualTrackFreeze = 44,                    // v4.97.0 카나리/듀얼트랙 방어
        GenesisSynced = 50, CanaryReconcile = 51, BlastRadiusBreach = 52, // v5.0.0 실전 총점화
        KillSwitchTrip = 53, DeadmanRevert = 54, RedTeamDrill = 55,       // v5.0.0 Red Team·데드맨
        ComaDiagnosed = 60, ChannelFailover = 61, ThreadStarvation = 62, // v5.1.0 자율 라이브 감시망
        OrphanReaped = 63, OrphanFreeze = 64,                            // v5.1.0 적응형 고아주문 사냥꾼
        LiquiditySlice = 70, OrderThrottled = 71, ToxicFlowHold = 72,    // v5.2.0 미시 유동성 슬라이서·스로틀·독성 방어
        StrategyHotSwap = 80, ZombieAssembly = 81, OrphanEventRejected = 82, // v5.3.0 무정지 전략 핫패치·좀비 방어
        BasketNetted = 83, WashTradeRejected = 84, StrategyFanout = 85,   // v5.4.0 다중 전략 인터리빙·네팅·자전거래 방어
        RiskCascade = 86, CapitalResegment = 87,                         // v5.4.0 교차 전략 캐스케이드 방화벽·자본 재분할
        LogFabricStall = 88, KernelJitterHold = 89,                      // v5.5.0 로그 패브릭 드롭·커널 지터 안전 홀드
        Freeze = 90,                                             // ForceFreeze 격발
        DiskStallFreeze = 91, PageFaultThrash = 92,                      // v5.5.0 디스크 I/O stall·페이지폴트 thrashing 방어
    }

    /// <summary>블랙박스 레코드 — 정확히 64바이트 고정 blittable(참조 0·힙 0). MemoryMarshal 로 바이트 직렬화.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public readonly struct FlightRecordEvent
    {
        public readonly long Sequence;        // [0]  전역 단조 클레임 시퀀스(1-based·wrap 판별)
        public readonly long TimestampTicks;  // [8]  주입 시계 tick(결정론)
        public readonly long SymbolCode;      // [16] 컨텍스트 심볼(0=n/a)
        public readonly long Arg0;            // [24] 종류별(예: 명목 raw·head)
        public readonly long Arg1;            // [32] 종류별(예: 수량 raw·µs 레이턴시)
        public readonly long Arg2;            // [40] 종류별(예: 검증코드)
        public readonly int  ThreadId;        // [48] 발원 관리 스레드
        public readonly int  SourceCode;      // [52] 발원 엔진 식별
        public readonly byte Kind;            // [56] FlightEventKind
        public readonly byte Flags;           // [57]
        public readonly short Reserved0;      // [58]
        public readonly int  Reserved1;       // [60] → 총 64B
        public FlightRecordEvent(long sequence, long timestampTicks, long symbolCode,
                                 long arg0, long arg1, long arg2, int threadId, int sourceCode, byte kind, byte flags)
        {
            Sequence = sequence; TimestampTicks = timestampTicks; SymbolCode = symbolCode;
            Arg0 = arg0; Arg1 = arg1; Arg2 = arg2; ThreadId = threadId; SourceCode = sourceCode;
            Kind = kind; Flags = flags; Reserved0 = 0; Reserved1 = 0;
        }
        public FlightEventKind Event => (FlightEventKind)Kind;
    }

    /// <summary>무할당 순환 커널 블랙박스 — 멀티라이터 tri-state 링. Record(δ=0)·Seal·SnapshotRecent.</summary>
    public sealed class MmfFlightRecorder
    {
        public const int  EventSize = 64;
        private const int Empty = 0, Writing = 1, Ready = 2;

        private readonly FlightRecordEvent[] _slots;
        private readonly int[]               _state;   // 슬롯별 tri-state(volatile 접근)
        private readonly int                 _capacity;
        private readonly long                _mask;
        private readonly Func<long>          _nowTicks;

        private long _writeSeq;             // 전역 단조 클레임(Interlocked.Increment)
        private long _totalRecorded;        // 실제 커밋 저장 수(랩·봉인 드롭 제외)
        private long _droppedWhileSealed;
        private long _droppedLapped;        // ★C#3 — 랩(구식) 라이터 드롭(최신 슬롯 덮어쓰기 방지)
        private long _inFlight;             // ★C#2 — 진행 중 Record 수(Seal 후 덤프가 배수)
        private int  _sealed;

        /// <param name="capacity">슬롯 수(2의 거듭제곱, 기본 100,000→131,072 로 올림). 64B×131072 ≈ 8MB 상주.</param>
        /// <param name="nowTicks">단조 시계(기본 Stopwatch.GetTimestamp). 테스트는 주입.</param>
        public MmfFlightRecorder(int capacity = 100_000, Func<long>? nowTicks = null)
        {
            if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity), "2 이상");
            int cap = NextPow2(capacity);
            _capacity = cap;
            _mask     = cap - 1;
            _slots    = new FlightRecordEvent[cap];   // ★사전 할당(부팅 1회) — 이후 Record 는 힙 할당 0
            _state    = new int[cap];
            _nowTicks = nowTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;
        }

        // ── 텔레메트리(락-프리 독출) ────────────────────────────────────────────
        public int  Capacity           => _capacity;
        public bool IsSealed           => Volatile.Read(ref _sealed) != 0;
        public long TotalRecorded      => Interlocked.Read(ref _totalRecorded);
        public long DroppedWhileSealed => Interlocked.Read(ref _droppedWhileSealed);
        public long DroppedLapped      => Interlocked.Read(ref _droppedLapped);
        public long ClaimedSeq         => Interlocked.Read(ref _writeSeq);
        /// <summary>링에 상주하는 유효 레코드 수(min(클레임, 용량)).</summary>
        public long RecordedCount { get { long c = Interlocked.Read(ref _writeSeq); return c < _capacity ? c : _capacity; } }
        /// <summary>링 포화도(0..1 × 10^8 raw). 클레임이 용량 도달 시 상시 100%(순환 덮어쓰기).</summary>
        public long SaturationRaw
        {
            get { long c = Interlocked.Read(ref _writeSeq); if (c >= _capacity) return QMath.Scale; return (long)((Int128)c * QMath.Scale / _capacity); }
        }

        /// <summary>
        /// 이산 사건 기록 — ★δ=0(사전할당 슬롯 struct 복사 + Interlocked long + volatile int·new/박싱/락 0).
        /// 봉인(Seal) 후엔 no-op(DroppedWhileSealed 계상). ★C#2: _inFlight 가드로 Seal 후 덤프가 진행 라이터를 배수(QuiesceAfterSeal).
        /// ★C#3: 랩(구식) 라이터가 최신 슬롯을 덮어쓰지 않도록 한 바퀴 이상 밀린 클레임은 드롭(DroppedLapped).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Record(FlightEventKind kind, long symbolCode, long arg0, long arg1, long arg2 = 0L, int sourceCode = 0)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                if (Volatile.Read(ref _sealed) != 0) { Interlocked.Increment(ref _droppedWhileSealed); return; }
                long seq = Interlocked.Increment(ref _writeSeq);   // 유일 단조 클레임(1-based)
                // ★C#3 랩 가드: 클레임 후 선점됐다 재개한 사이 링이 한 바퀴 이상 진행했으면 최신 슬롯을 훼손하지 않고 드롭.
                if (Interlocked.Read(ref _writeSeq) - seq >= _capacity) { Interlocked.Increment(ref _droppedLapped); return; }
                int idx = (int)((seq - 1) & _mask);
                Volatile.Write(ref _state[idx], Writing);          // ★무효화(스냅샷 격리)
                _slots[idx] = new FlightRecordEvent(
                    seq, _nowTicks(), symbolCode, arg0, arg1, arg2,
                    Environment.CurrentManagedThreadId, sourceCode, (byte)kind, 0);
                Volatile.Write(ref _state[idx], Ready);            // ★커밋(release)
                Interlocked.Increment(ref _totalRecorded);
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        /// <summary>덤프 인덱스 물리 잠금 — 이후 새 Record 는 no-op. 불가역(재기록 원천 차단). 멱등.</summary>
        public void Seal() => Volatile.Write(ref _sealed, 1);

        /// <summary>★C#2 — Seal 후 호출. 진행 중 라이터(_inFlight)가 0 으로 배수될 때까지 바운드 스핀(새 라이터는 봉인 드롭이라 곧 수렴).
        /// 반환 후 스냅샷은 mid-write 슬롯 없이 일관. 미봉인 상태 호출은 무의미(스핀만).</summary>
        public void QuiesceAfterSeal(int maxSpins = 2_000_000)
        {
            var spin = new SpinWait();
            int g = 0;
            while (Volatile.Read(ref _inFlight) > 0 && g++ < maxSpins) spin.SpinOnce();
        }

        /// <summary>
        /// 최신순 스냅샷 — dest 를 최근 레코드부터 채워 개수 반환(dest[0]=가장 최근). ★δ=0(dest 는 호출자 제공).
        /// ★C#7: 유효 창(최근 _capacity 슬롯) 하한으로 스캔 제한 — wrap 링에서 O(head) 폭주 방지.
        /// ★C#1: Ready→복사→Ready 재확인 후 '라이브 슬롯 Sequence 재독출'로 복사 중 풀사이클 덮어쓰기(torn) 격리
        ///        (복사본 ev.Sequence 만 믿으면 offset0 이 옛값으로 남는 intra-copy tearing 을 놓침).
        /// </summary>
        public int SnapshotRecent(Span<FlightRecordEvent> dest)
        {
            int want = dest.Length;
            if (want <= 0) return 0;
            long head  = Interlocked.Read(ref _writeSeq);
            long floor = head - _capacity;   // 이보다 오래된 s 는 이미 덮임 — 스캔 하한
            int n = 0;
            for (long s = head; s > 0 && s > floor && n < want; s--)
            {
                int idx = (int)((s - 1) & _mask);
                if (Volatile.Read(ref _state[idx]) != Ready) continue;      // Empty/Writing 격리
                FlightRecordEvent ev = _slots[idx];                        // struct 복사(스택)
                if (Volatile.Read(ref _state[idx]) != Ready) continue;      // 복사 중 덮어쓰기 시작 → 격리(acquire)
                long liveSeq = _slots[idx].Sequence;                       // ★라이브 재독출(acquire 이후·x64 TSO) — intra-copy 풀사이클 검출
                if (liveSeq != s || ev.Sequence != s) continue;            // 둘 다 s 일 때만 무결(torn/랩 격리)
                dest[n++] = ev;
            }
            return n;
        }

        private static int NextPow2(int v)
        {
            if (v < 2) return 2;
            v--; v |= v >> 1; v |= v >> 2; v |= v >> 4; v |= v >> 8; v |= v >> 16; return v + 1;
        }
    }

    /// <summary>
    /// ★무할당 UI 헥스 렌더 헬퍼 — byte→hex 를 정적 글리프 룩업으로 변환(byte.ToString("X2") 회피). 동결 시에만
    /// 고정 N 레코드를 1회 정적 투사(문자열 생성 극소화 = char[] 1개 + string 1개). 정상 가동 중엔 호출되지 않음.
    /// </summary>
    public static class FlightHexRenderer
    {
        private static readonly char[] Glyph = "0123456789ABCDEF".ToCharArray();

        /// <summary>레코드들을 오프셋·16바이트/행 헥스 덤프 텍스트로 렌더(호출자 char[] 재사용·per-byte 문자열 0).
        /// scratch 는 최소 (events.Length*EventSize)*4 크기 권장(오프셋+hex+개행 여유). 반환=사용 길이.</summary>
        public static int RenderInto(ReadOnlySpan<FlightRecordEvent> events, Span<char> scratch)
        {
            int pos = 0;
            ReadOnlySpan<byte> allBytes = MemoryMarshal.AsBytes(events);
            const int perRow = 16;
            for (int off = 0; off < allBytes.Length; off += perRow)
            {
                // 오프셋(8 hex) + ": "
                if (pos + 10 > scratch.Length) break;
                WriteHex32(scratch, ref pos, (uint)off);
                scratch[pos++] = ':'; scratch[pos++] = ' ';
                int end = Math.Min(off + perRow, allBytes.Length);
                for (int i = off; i < end; i++)
                {
                    if (pos + 3 > scratch.Length) return pos;
                    byte b = allBytes[i];
                    scratch[pos++] = Glyph[(b >> 4) & 0xF];
                    scratch[pos++] = Glyph[b & 0xF];
                    scratch[pos++] = ' ';
                }
                if (pos < scratch.Length) scratch[pos++] = '\n';
            }
            return pos;
        }

        /// <summary>편의 오버로드 — 단일 string 1회 생성(동결 시 1회 호출·이후 캐시). scratch 내부 대여.</summary>
        public static string Render(ReadOnlySpan<FlightRecordEvent> events)
        {
            int need = events.Length * EventSizeChars();
            char[] buf = new char[need];
            int used = RenderInto(events, buf);
            return new string(buf, 0, used);
        }

        private static int EventSizeChars() => MmfFlightRecorder.EventSize * 4 + 16;   // 행당 여유 상한

        private static void WriteHex32(Span<char> dest, ref int pos, uint v)
        {
            for (int shift = 28; shift >= 0; shift -= 4)
                dest[pos++] = Glyph[(int)((v >> shift) & 0xF)];
        }
    }
}
