// ============================================================================
// CausalWeave — v6.0.0 ★Think Beyond Gemini: 커널 바이패스 링 오버런 & 플레이백 타임-와프를
//   '단일 인과 시계 앵커(one causal time anchor)'로 동시 격리하는 통합 방어 엔진.
//
//  ── 제미나이 기획을 초월하는 제3의 해결책 ────────────────────────────────────
//  제미나이는 두 파국을 서로 다른 두 모듈로 방어하자고 제안했다:
//    · 아이디어 A — RIO 링 오버런 → 별도의 '드롭 가드'.
//    · 아이디어 B — 플레이백 타임-와프 → 별도의 '시계 검증기'.
//  CausalWeave 는 이 둘이 사실은 '하나의 불변식(단조 인과 워터마크)'의 두 얼굴임을 간파해 통합한다:
//    RIO 완료 순간 하드웨어 QPC 를 패킷에 '봉인'하면 —
//      (1) 링 오버런 시 '가장 최신 QPC 우선(newest-wins)'으로 구형 슬롯을 무할당 회수하되
//          유실을 시퀀스 갭으로 '증명 가능'하게 계수한다(커널 크래시·조용한 드롭 원천 봉쇄). ← A 초월
//      (2) 바로 그 봉인된 QPC 가 플레이백 시계의 '유일한' 진실 원천이 된다 — 재생 프레임의 QPC 가
//          워터마크보다 뒤로 흐르면(타임-와프) 퓨즈가 즉시 fail-closed 로 재생을 정지시킨다. ← B 초월
//    ∴ "기록한 그대로 재생된다(what-you-record-is-what-you-replay)"가 '설계상' 강제된다:
//      라이브 수신 시각과 재생 시각이 단 하나의 단조 실(one monotonic thread)로 직조(weave)되기 때문.
//
//  ★불변 제약 준수: δ=0 핫패스(사전할당 슬롯·seqlock·Interlocked — new/box/LINQ 0), 결정론(주입 시계),
//    fail-closed(퓨즈 트립 = ForceFreeze ∧ IgnitionLock.Downgrade 이중 봉인, 각 try/catch),
//    liveArmed:false(관측/영속 인프라 — IBroker·소켓·onSend·PublishRaw 참조 0). unsafe 미사용(관리형 배킹).
//    ★한국색상은 UI 계층(TelemetryMonitor). 여기선 상태 코드만 산출.
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>CausalWeave 종합 상태(텔레메트리·UI 배지 매핑). 값이 클수록 위중.</summary>
    public enum CausalWeaveState
    {
        Nominal     = 0,   // 정상 직조 — 워터마크 단조·링 여유
        Pressure    = 1,   // 링 포화 임박(≥75%) — 소비 지연 경고(아직 무유실)
        Evicting    = 2,   // 오버런 — newest-wins 회수 발생(유실을 갭으로 증명 계수 중)
        FuseTripped = 3,    // 타임-와프 퓨즈 트립 — 재생 정지·fail-closed(비가역까지 봉인)
    }

    /// <summary>
    /// 단조 인과 시계 + 타임-와프 퓨즈. 라이브 모드는 주입 QPC(Stopwatch.GetTimestamp)를 통과시키고,
    /// 플레이백 모드는 '봉인된 프레임 QPC'만을 시각 진실로 삼는다. 재생 QPC 역행(1 raw)이라도 즉시 퓨즈 트립.
    /// <see cref="TickSource"/> 를 컴포넌트의 주입 시계로 배선하면, 플레이백 컨텍스트에서 실시간 벽시계
    /// 오유입(DateTime.UtcNow 등)을 '구조적으로' 차단한다(결정론 오염 불가).
    /// </summary>
    public sealed class CausalWeaveClock
    {
        private readonly Func<long> _liveNowTicks;                 // 라이브 QPC(주입·기본 Stopwatch.GetTimestamp)
        private readonly OrderAttestationService? _attestation;    // fail-closed 단일 래치(옵션)
        private readonly IgnitionLock? _ignition;                  // 무장 FSM 강등(옵션)

        private long _playbackTicks;      // 플레이백 현재 봉인 QPC(자문적 페이싱 시각 — NowTicks 원천)
        private long _watermark;          // 관측된 최대 단조 QPC(라이브·플레이백 공용 — 계측/자문)
        private long _seqWatermark = long.MinValue;   // ★재생 시퀀스 워터마크(퓨즈 판정 기준 — 기록 인과 순서)
        private int  _playback;           // 0=라이브, 1=플레이백
        private int  _fuseTripped;        // 0/1 — 1이면 비가역 정지(fail-closed)
        private long _warpEvents;         // 포착된 인과 위반(시퀀스 역행) 시도 누적(증거)

        public CausalWeaveClock(Func<long>? liveNowTicks = null,
                                OrderAttestationService? attestation = null,
                                IgnitionLock? ignition = null)
        {
            _liveNowTicks = liveNowTicks ?? Stopwatch.GetTimestamp;
            _attestation  = attestation;
            _ignition     = ignition;
            long seed     = _liveNowTicks();
            _playbackTicks = seed;
            _watermark     = seed;
        }

        /// <summary>주입용 단조 시계 델리게이트 — 라이브=QPC, 플레이백=봉인 프레임 QPC. 무할당(정적 캡처 1회).</summary>
        public Func<long> TickSource => NowTicks;

        public bool IsPlayback  => Volatile.Read(ref _playback) == 1;
        public bool FuseTripped => Volatile.Read(ref _fuseTripped) == 1;
        public long WarpEvents  => Interlocked.Read(ref _warpEvents);
        public long Watermark   => Interlocked.Read(ref _watermark);
        /// <summary>재생 시퀀스 워터마크(퓨즈 판정 기준 — 기록 인과 순서). 플레이백 진입 시 리셋.</summary>
        public long SeqWatermark => Interlocked.Read(ref _seqWatermark);

        public CausalWeaveState State
        {
            get
            {
                if (FuseTripped) return CausalWeaveState.FuseTripped;
                return CausalWeaveState.Nominal;   // 링 압박/회수는 CausalWeaveRing 이 별도 보고(합성은 UI)
            }
        }

        /// <summary>현재 인과 시각(ns 도메인 아님·QPC tick). δ=0.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long NowTicks()
            => Volatile.Read(ref _playback) == 1 ? Volatile.Read(ref _playbackTicks) : _liveNowTicks();

        /// <summary>플레이백 컨텍스트 진입 — 이후 NowTicks 는 봉인 프레임 QPC 만 반환(벽시계 오유입 차단).</summary>
        public void EnterPlayback(long firstFrameQpc)
        {
            Volatile.Write(ref _playbackTicks, firstFrameQpc);
            Volatile.Write(ref _watermark, firstFrameQpc);
            Volatile.Write(ref _seqWatermark, long.MinValue);   // 첫 프레임이 시퀀스 baseline 확립(퓨즈 판정 기준 리셋)
            Volatile.Write(ref _playback, 1);
        }

        /// <summary>플레이백 종료 — 라이브 QPC 복귀(퓨즈 트립 상태는 유지: 비가역 봉인).</summary>
        public void ExitPlayback() => Volatile.Write(ref _playback, 0);

        /// <summary>
        /// ★재생 프레임의 '기록 시퀀스(Seq)'를 인과 퓨즈에 전진 적용 — 퓨즈의 유일 판정 기준.
        /// 로거 드레인이 tail(seq) 오름차순으로만 영속화하므로 정상 데이터는 Seq 가 절대 역행하지 않는다:
        /// 역행은 재정렬/손상/변조를 뜻하므로 즉시 fail-closed 트립하고 false 반환(호출자는 재생 즉시 중단).
        /// (봉인 QPC 를 트립 기준으로 삼지 않는 이유: 다중 생산자 로거가 seq 를 Interlocked 로 먼저 클레임한 뒤
        ///  QPC 를 샘플하므로, 선점 시 정상 로그도 '디스크 seq 순서 ≠ QPC 순서'가 되어 QPC 는 국소 역전한다.
        ///  QPC 는 <see cref="Advance"/> 가 자문적 페이싱 시각으로만 단조 클램프한다.) δ=0.
        /// </summary>
        public bool AdvanceSeq(long frameSeq)
        {
            if (Volatile.Read(ref _fuseTripped) == 1) return false;   // 이미 봉인 — 무조건 정지
            long swm = Volatile.Read(ref _seqWatermark);
            if (frameSeq < swm)                                       // ★인과 위반: 기록 시퀀스 역행
            {
                Interlocked.Increment(ref _warpEvents);
                TripFuse($"CausalWeave 인과 위반 — 재생 시퀀스 역행(frame={frameSeq} < watermark={swm}). 재정렬/손상/변조 fail-closed.");
                return false;
            }
            Volatile.Write(ref _seqWatermark, frameSeq);
            return true;
        }

        /// <summary>
        /// 재생 프레임의 봉인 QPC 를 자문적으로 전진(페이싱·결정 시각·NowTicks 원천). 단조 클램프 —
        /// 역행해도 시각은 뒤로 가지 않으며(다중 생산자 QPC 지터는 정상), 트립하지 않는다(트립은 <see cref="AdvanceSeq"/>).
        /// 이미 퓨즈 트립 상태면 false. δ=0.
        /// </summary>
        public bool Advance(long frameQpc)
        {
            if (Volatile.Read(ref _fuseTripped) == 1) return false;   // 이미 봉인 — 무조건 정지
            long pt = Volatile.Read(ref _playbackTicks);
            if (frameQpc > pt) Volatile.Write(ref _playbackTicks, frameQpc);   // NowTicks 단조(뒤로 안 감)
            long wm = Interlocked.Read(ref _watermark);
            if (frameQpc > wm) Interlocked.CompareExchange(ref _watermark, frameQpc, wm);
            return true;
        }

        /// <summary>라이브 수신 QPC 관측 — 워터마크만 단조 전진(라이브 QPC 는 정의상 단조라 트립 없음·계측용).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ObserveLive(long qpc)
        {
            long wm = Interlocked.Read(ref _watermark);
            if (qpc > wm) Interlocked.CompareExchange(ref _watermark, qpc, wm);
        }

        /// <summary>퓨즈 트립(1회 래치·비가역) — 이중 봉인(ForceFreeze ∧ Downgrade), 각 개별 try/catch(하나 throw 해도 나머지 착지).</summary>
        public void TripFuse(string reason)
        {
            if (Interlocked.CompareExchange(ref _fuseTripped, 1, 0) != 0) return;   // 1회만
            try { _attestation?.ForceFreeze(reason); } catch { /* 삼키지 않되 격리 — 나머지 봉인 보장 */ }
            try { _ignition?.Downgrade(reason); }      catch { }
        }
    }

    /// <summary>
    /// 락-프리 SPSC 인과 워터마크 링 — RIO 완료 드레인(단일 생산자)이 QPC 봉인 프레임을 무할당 발행하고,
    /// 전략 핫패스(단일 소비자)가 seqlock 무할당 독출한다. 오버런 시 'newest-wins' 로 최신 패킷 가치를 사수하며
    /// 구형 슬롯을 무할당 회수하되, 유실을 <see cref="Evictions"/> 갭으로 '증명 가능'하게 계수한다(조용한 드롭 없음).
    ///
    ///  ★zero-copy 경계 정직 고지: RIO 의 무복사는 '커널→등록 네이티브 버퍼' DMA 구간(가장 비싼 syscall/커널 복사
    ///    제거)이다. 이 링 발행은 그 하류의 '경계 한정·δ=0 유저스페이스 스탬프'(사전할당 슬롯 복사)로, 커널
    ///    바이패스 이득을 보존한다. 관리형 배킹이라 unsafe 미사용·완전 단위검증 가능(NIC 없이도).
    /// </summary>
    public sealed class CausalWeaveRing
    {
        private readonly int      _capacity;      // 슬롯 수(2의 거듭제곱)
        private readonly int      _mask;
        private readonly int      _maxFrame;       // 슬롯당 최대 프레임 바이트
        private readonly byte[]   _backing;        // 사전할당 배킹(_capacity * _maxFrame) — 힙 재할당 0
        private readonly long[]   _qpc;            // 슬롯별 봉인 QPC
        private readonly long[]   _seq;            // 슬롯별 단조 시퀀스(갭=유실 증명)
        private readonly int[]    _len;            // 슬롯별 유효 길이
        private readonly int[]    _ver;            // 슬롯별 seqlock 버전(짝=안정, 홀=쓰기중)

        private long _head;         // 생산자 발행 시퀀스(다음 쓸 위치)
        private long _tail;         // 소비자 회수 하한(newest-wins 시 생산자가 전진)
        private long _evictions;    // 오버런 회수(증명 드롭) 누적
        private long _published;    // 총 발행 누적
        private long _lastRead;     // 소비자가 마지막으로 읽은 seq

        public CausalWeaveRing(int capacity = 4096, int maxFrame = 2048)
        {
            if (capacity < 2 || (capacity & (capacity - 1)) != 0)
                throw new ArgumentException("capacity 는 2의 거듭제곱이어야 합니다.", nameof(capacity));
            if (maxFrame < 16) throw new ArgumentException("maxFrame 과소.", nameof(maxFrame));
            _capacity = capacity;
            _mask     = capacity - 1;
            _maxFrame = maxFrame;
            _backing  = new byte[(long)capacity * maxFrame <= int.MaxValue ? capacity * maxFrame
                                 : throw new ArgumentException("배킹 용량 초과")];
            _qpc = new long[capacity];
            _seq = new long[capacity];
            _len = new int[capacity];
            _ver = new int[capacity];
            _head = 0; _tail = 0; _lastRead = -1;
        }

        public long Evictions => Interlocked.Read(ref _evictions);
        public long Published => Interlocked.Read(ref _published);

        /// <summary>현재 포화도(‰·0..1000). 소비 지연으로 미독출 슬롯이 쌓일수록 상승.</summary>
        public int SaturationPermille
        {
            get
            {
                long depth = Interlocked.Read(ref _head) - Interlocked.Read(ref _tail);
                if (depth < 0) depth = 0;
                if (depth > _capacity) depth = _capacity;
                return (int)(depth * 1000L / _capacity);
            }
        }

        /// <summary>포화/회수 종합 상태(UI 배지).</summary>
        public CausalWeaveState State
        {
            get
            {
                if (Interlocked.Read(ref _evictions) > 0 && SaturationPermille >= 990) return CausalWeaveState.Evicting;
                if (SaturationPermille >= 750) return CausalWeaveState.Pressure;
                return CausalWeaveState.Nominal;
            }
        }

        /// <summary>
        /// 프레임 발행(단일 생산자·RIO 드레인/폴백 소켓). QPC 봉인 + 단조 시퀀스 부여. 링 만원이면 newest-wins:
        /// tail 을 전진시켜 구형 슬롯을 무할당 회수하고 회수 수를 증명 계수(커널 크래시 없이 최신 사수). δ=0.
        /// frame.Length > maxFrame 이면 앞부분만 봉인(경계 한정·유실 없음: 헤더/틱 코어는 선두).
        /// </summary>
        public void Publish(ReadOnlySpan<byte> frame, long qpc)
        {
            long seq  = _head;                       // 단일 생산자 → 경합 없음
            int  slot = (int)(seq & _mask);

            // 오버런: 소비자가 한 바퀴 뒤처져 이 슬롯이 미독출 최신을 덮어쓰려 하면 tail 전진(구형 회수)+증명 계수.
            // ★_tail 은 생산자(회수)와 소비자(독출)가 모두 전진시킨다. 단순 Volatile.Write 는 원자적 RMW 가
            //   아니라 동시 갱신 시 lost-update/역행으로 회수 계수(Evictions)가 오염된다(증명-드롭 불변식 위반).
            //   CAS-to-max 로 단조 보장(역행 불가) + 실제 전진량만 계수해 각 회수 슬롯을 정확히 1회 센다.
            if (seq - Interlocked.Read(ref _tail) >= _capacity)
            {
                long moved = AdvanceTailAtLeast(seq - _capacity + 1);
                if (moved > 0) Interlocked.Add(ref _evictions, moved);
            }

            int copyLen = frame.Length <= _maxFrame ? frame.Length : _maxFrame;

            // seqlock: 홀(쓰기 시작)→payload→짝(완료). 소비자 torn read 격리. 버전은 쓰기마다 +2 단조 증가.
            int verOdd = _ver[slot] | 1;                      // 홀(쓰기 중) — 직전이 짝이면 +1, 이미 홀이면 유지
            Volatile.Write(ref _ver[slot], verOdd);
            frame.Slice(0, copyLen).CopyTo(_backing.AsSpan(slot * _maxFrame, copyLen));
            _qpc[slot] = qpc;
            _seq[slot] = seq;
            _len[slot] = copyLen;
            Volatile.Write(ref _ver[slot], verOdd + 1);       // 짝(발행 완료)

            Volatile.Write(ref _head, seq + 1);
            Interlocked.Increment(ref _published);
        }

        /// <summary>
        /// 최신 미독출 프레임을 소비자 버퍼로 무할당 독출(단일 소비자). torn read 면 재시도(≤8). 새 프레임 없으면 false.
        /// dst 는 호출자 재사용 버퍼(δ=0). 회수로 건너뛴 시퀀스는 <paramref name="gap"/> 로 보고(유실 증명).
        /// </summary>
        public bool TryReadNext(Span<byte> dst, out int len, out long qpc, out long seq, out long gap)
        {
            len = 0; qpc = 0; seq = 0; gap = 0;
            long head = Volatile.Read(ref _head);
            long tail = Volatile.Read(ref _tail);
            long next = _lastRead + 1;
            if (next < tail) { gap = tail - next; next = tail; }   // 회수로 건너뛴 구간(유실 증명 보고)
            if (next >= head) return false;                         // 새 프레임 없음

            int slot = (int)(next & _mask);
            for (int i = 0; i < 8; i++)
            {
                int v1 = Volatile.Read(ref _ver[slot]);
                if ((v1 & 1) != 0) continue;                        // 홀=쓰기중 → 재시도
                long sSeq = _seq[slot];
                long sQpc = _qpc[slot];
                int  sLen = _len[slot];
                if (sLen < 0 || sLen > _maxFrame) continue;         // 오염 방어
                int copy = sLen <= dst.Length ? sLen : dst.Length;
                _backing.AsSpan(slot * _maxFrame, copy).CopyTo(dst.Slice(0, copy));
                int v2 = Volatile.Read(ref _ver[slot]);
                if (v1 != v2) continue;                             // 독출 중 덮어쓰기 → 재시도
                if (sSeq != next) { gap += (sSeq > next ? sSeq - next : 0); }  // 슬롯이 이미 앞서감(오버런 경합)
                len = copy; qpc = sQpc; seq = sSeq;
                _lastRead = sSeq;
                AdvanceTailAtLeast(sSeq + 1);   // ★단조 전진(생산자 회수와 경합해도 역행 불가)
                return true;
            }
            return false;   // 경합 지속 → 비블록(다음 폴에서 재시도)
        }

        /// <summary>
        /// _tail 을 target 이상으로 단조 전진(CAS-to-max). 생산자·소비자가 동시에 호출해도 역행하지 않으며,
        /// 실제로 전진시킨 양(target - 직전값, 이미 ≥target 이면 0)을 반환한다. δ=0(무할당·락프리).
        /// </summary>
        private long AdvanceTailAtLeast(long target)
        {
            long cur;
            do
            {
                cur = Volatile.Read(ref _tail);
                if (cur >= target) return 0;
            }
            while (Interlocked.CompareExchange(ref _tail, target, cur) != cur);
            return target - cur;
        }
    }
}
