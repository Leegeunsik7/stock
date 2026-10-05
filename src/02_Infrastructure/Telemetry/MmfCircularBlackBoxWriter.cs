// ============================================================================
// MmfCircularBlackBoxWriter — v12.5.0 Think Beyond Gemini Idea A: 진짜 OS Memory-Mapped File 순환 블랙박스
//
//   ★정직 회계: 기존 "MmfFlightRecorder"(v4.95.0)는 이름과 달리 System.IO.MemoryMappedFiles 를 전혀 쓰지 않는
//   순수 관리형 인메모리 링이다(실측 확인). 그 설계는 '평시 δ=0 고속 기록 + ForceFreeze 격발 시 1회 암호봉인
//   덤프(CrashResilientDumper)'로 충분히 우수하지만, 덤프가 트리거되기 '전에' 프로세스가 강제 종료(전원 차단·
//   OS 강제킬·비정상 크래시로 옵저버조차 못 도는 경우)되면 그 순간까지의 기록이 전부 유실된다.
//
//   본 엔진은 그 공백을 메운다 — 실제 System.IO.MemoryMappedFiles 로 디스크 파일을 커널 페이지 캐시에 매핑해,
//   Append 가 곧 '커널이 이미 알고 있는 페이지'에 쓰는 것과 같다(OS 가 자체 판단으로 언젠가 디스크에 내려씀).
//   ExplicitFlush() 를 호출하지 않아도 프로세스가 죽는 순간(크래시 포함) 이미 커널 페이지 캐시에 반영된 최근
//   레코드까지는 파일에서 사후 복구 가능 — '덤프 이벤트에 의존하지 않는' 무손실 보장 계층.
//
//   ★MemoryMappedViewAccessor.Write&lt;T&gt;/Read&lt;T&gt;(제네릭 unmanaged 오버로드)만 사용 — 원시 포인터/unsafe
//   블록 0(하우스 관례: unsafe 는 MmfIpcFabric 전용으로 국한). 레코드 타입은 기존 FlightRecordEvent(64B blittable)
//   를 재사용(신규 포맷 중복 금지 — CrashResilientDumper.Encode/TryLoadAndVerify 와 완전 호환).
//
//   불변: 결정론(주입 tick) · δ=0(Append 정상 경로 힙 0 — 생성자의 파일/뷰 오픈만 예외) · fail-closed(용량 초과
//        시 순환 덮어쓰기 — 드롭 대신 최신 우선) · liveArmed:false(관측·영속화만 — 발주 경로 무접촉).
// ============================================================================
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>FlightRecordEvent(64B blittable)를 실제 OS MemoryMappedFile 에 순환 기록하는 δ=0 블랙박스.
    /// [MMF 하드닝] Append/ReadAll 은 내부 락으로 직렬화 — 이전의 "단일 라이터 계약(주석)"은 ① 다중 라이터 시 _writeSeq++ 경합으로
    /// 같은 슬롯 이중 기록(레코드 유실) ② 랩 직전 ReadAll 이 덮어쓰기 중인 최고참 슬롯을 읽는 찢긴 판독을 코드로 막지 못했다.
    /// 경합 없는 Monitor 는 관리 힙 할당 0(δ=0 유지).</summary>
    public sealed class MmfCircularBlackBoxWriter : IDisposable
    {
        // 헤더 레이아웃(64B): [0]Magic(long) [8]Capacity(long) [16]WriteSeq(long) [24..63]Reserved
        private const int HeaderBytes = 64;
        private const long HeaderMagic = 0x4D53_3245_4D4D_4600L;   // "MS2E" + MMF\0 근사

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly int _capacity;
        private readonly long _rebaseThreshold;
        private readonly object _gate = new();
        private long _writeSeq;
        private bool _disposed;

        public string Path { get; }
        public bool PreservedIncompatibleFile { get; }
        /// <summary>[방향 A] 콜드스타트 정돈 — 헤더 시퀀스는 전진했지만 레코드 페이지가 디스크에 내려가지 못한(전원 손실) 0x00 유령 슬롯을
        /// 꼬리에서 걷어낸 개수(헤더 시퀀스도 그만큼 후퇴·재기록).</summary>
        public int ColdStartTrimmedZeroSlots { get; }
        /// <summary>[방향 A] 유효 구간 "중간"에 남은 0x00 슬롯 수(ReadAll 이 건너뜀 — 유령 레코드 반환 0).</summary>
        public int ColdStartZeroHoles { get; }
        public int Capacity => _capacity;
        public long WriteSeq => Volatile.Read(ref _writeSeq);

        public MmfCircularBlackBoxWriter(string path, int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            Path = path;
            _capacity = capacity;
            _rebaseThreshold = long.MaxValue - 2L * capacity;
            long size = HeaderBytes + (long)capacity * MmfFlightRecorder.EventSize;

            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // [Phase 1 감사 교정] 기존 파일의 헤더를 매핑 "전에" 검증한다. 이전엔 Magic 만 보고 시퀀스를 이어받아,
            //   ① 다른 용량으로 기록된 파일 → seq % 새용량 으로 슬롯이 뒤섞여 사후 판독이 조용히 오염,
            //   ② 손상 헤더(음수 시퀀스) → 이후 모든 Append 가 음수 오프셋 예외(블랙박스 영구 사망)였다.
            // 호환 불가 파일은 지우지 않고 옆으로 옮겨 보존(포렌식 증거 유실 금지) 후 새 링으로 시작한다.
            PreservedIncompatibleFile = PreserveIfIncompatible(path, capacity, size);

            MemoryMappedFile? mmf = null;
            MemoryMappedViewAccessor? view = null;
            try
            {
                mmf = MemoryMappedFile.CreateFromFile(path, FileMode.OpenOrCreate, mapName: null, capacity: size);
                view = mmf.CreateViewAccessor(0, size);

                if (view.ReadInt64(0) == HeaderMagic)
                {
                    _writeSeq = view.ReadInt64(16);   // 검증 통과한 기존 파일 — 이전 시퀀스 이어받기(크래시 후 재기동)
                    (ColdStartTrimmedZeroSlots, ColdStartZeroHoles) = SanitizeColdStart(view, capacity, ref _writeSeq);
                }
                else
                {
                    view.Write(0, HeaderMagic);
                    view.Write(8, (long)capacity);
                    view.Write(16, 0L);
                }
            }
            catch
            {
                // [Phase 1 감사 교정] 뷰 생성/헤더 I/O 실패 시 이미 연 매핑·파일 핸들이 누수되어 파일이 잠긴 채 남던 결함 차단.
                view?.Dispose();
                mmf?.Dispose();
                throw;
            }
            _mmf = mmf;
            _view = view;
        }

        /// <summary>헤더가 우리 형식(Magic)인데 용량이 다르거나 시퀀스가 음수면 파일을 "{path}.incompatible.{UTC}.bak" 로 옮긴다.
        /// 우리 형식이 아닌 파일(Magic 불일치)은 기존 동작대로 새 헤더로 초기화된다. 반환 = 옮겼는가.</summary>
        private static bool PreserveIfIncompatible(string path, int capacity, long expectedSize)
        {
            if (!File.Exists(path)) return false;
            long magic, storedCapacity, storedSeq, length;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                length = fs.Length;
                if (fs.Length < 24) return false;
                Span<byte> header = stackalloc byte[24];
                fs.ReadExactly(header);
                magic = BitConverter.ToInt64(header[..8]);
                storedCapacity = BitConverter.ToInt64(header.Slice(8, 8));
                storedSeq = BitConverter.ToInt64(header.Slice(16, 8));
            }
            if (magic != HeaderMagic) return false;
            // [MMF 하드닝] 헤더가 정상이어도 파일 길이가 헤더 용량과 다르면(잘린 복사본·부분 동기화) 꼬리 슬롯이 0 으로 확장돼
            //   조용히 유실/오염된다 → 이 역시 호환 불가로 보존 이관.
            if (storedCapacity == capacity && storedSeq >= 0 && length == expectedSize) return false;

            string aside = $"{path}.incompatible.{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak";
            File.Move(path, aside);
            return true;
        }

        /// <summary>레코드 1건을 순환 슬롯에 기록(★단일 라이터·정상 경로 δ=0). 용량 초과 시 최고참 슬롯을 덮어쓴다
        /// (드롭 대신 최신 보존 — 크래시 직전 최신 상태가 가장 포렌식 가치가 높다). 값타입 매개변수 — Unsafe/포인터 0.</summary>
        public void Append(FlightRecordEvent ev)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                // [MMF 하드닝] 시퀀스 포화 rebase — 헤더 손상(거대 양수 시퀀스)이나 초장기 가동으로 long 이 넘치면 seq % cap 이 음수가 돼
                //   음수 오프셋 예외로 블랙박스가 영구 사망했다. 슬롯 사상(seq mod cap)과 "링 가득 참" 의미를 보존한 채 cap..2cap 로 접는다.
                if (_writeSeq >= _rebaseThreshold) _writeSeq = _writeSeq % _capacity + _capacity;
                long seq = _writeSeq;
                int idx = (int)(seq % _capacity);
                long offset = HeaderBytes + (long)idx * MmfFlightRecorder.EventSize;
                _view.Write(offset, ref ev);          // ① 레코드 먼저
                Volatile.Write(ref _writeSeq, seq + 1);
                _view.Write(16, seq + 1);             // ② 헤더 시퀀스 나중(크래시 시 최악 = 마지막 1건이 헤더에 미반영 — 유령 레코드 0)
            }
        }

        /// <summary>디스크 반영을 명시 요청(치명적 상황 전용 — 평시엔 OS 지연쓰기에 맡겨 I/O 비용 회피).</summary>
        public void Flush() => _view.Flush();

        /// <summary>기록된(랩 이전까지 최대 capacity 개) 레코드를 시퀀스 순으로 복원(δ=0 아님 — 저빈도 사후 조회 전용).</summary>
        public int ReadAll(Span<FlightRecordEvent> dest)
        {
            lock (_gate)   // [MMF 하드닝] 판독 중 Append 가 최고참 슬롯을 덮어쓰는 랩 경합 차단(찢긴 레코드 반환 0)
            {
                ThrowIfDisposed();
                long seq = _writeSeq;
                long count = Math.Min(seq, _capacity);
                long floor = seq - count;
                int n = 0;
                for (long s = floor; s < seq && n < dest.Length; s++)
                {
                    int idx = (int)(s % _capacity);
                    long offset = HeaderBytes + (long)idx * MmfFlightRecorder.EventSize;
                    _view.Read(offset, out FlightRecordEvent rec);
                    if (IsZero(in rec)) continue;   // [방향 A] 전원 손실 0x00 구멍 — 유령 레코드로 반환하지 않음
                    dest[n++] = rec;
                }
                return n;
            }
        }

        /// <summary>64B 레코드가 전부 0x00 인가(δ=0 — 값 복사본 위 스팬 판독). 정상 레코드는 Sequence ≥ 1 이라 전부 0 일 수 없다.</summary>
        private static bool IsZero(in FlightRecordEvent rec)
            => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in rec), 1)).IndexOfAnyExcept((byte)0) < 0;

        /// <summary>
        /// [방향 A] Zero-Alloc Cold-Start MMF Header Sanitizer — 이전 실행이 비정상 종료(특히 전원 손실)됐을 때 커널이 헤더 페이지는
        /// 내렸지만 레코드 페이지는 못 내린 경우, 유효 구간 꼬리에 0x00 슬롯이 남는다. 꼬리의 연속 0 슬롯은 "기록된 적 없음"과 같으므로
        /// 시퀀스를 그만큼 후퇴시켜 헤더에 재기록(다음 Append 가 그 자리부터 이어씀 — 유령 레코드·시퀀스 구멍 0), 중간 구멍은 계수만 한다.
        /// 스캔은 뷰 위 값 판독 + 스팬 비교뿐(힙 0) · O(유효 슬롯) 1회.
        /// </summary>
        private static (int Trimmed, int Holes) SanitizeColdStart(MemoryMappedViewAccessor view, int capacity, ref long seq)
        {
            long count = Math.Min(seq, capacity);
            long floor = seq - count;
            int trimmed = 0;
            while (seq > floor)
            {
                view.Read(HeaderBytes + (long)(int)((seq - 1) % capacity) * MmfFlightRecorder.EventSize, out FlightRecordEvent tail);
                if (!IsZero(in tail)) break;
                seq--; trimmed++;
            }
            if (trimmed > 0) view.Write(16, seq);
            int holes = 0;
            for (long s = floor; s < seq; s++)
            {
                view.Read(HeaderBytes + (long)(int)(s % capacity) * MmfFlightRecorder.EventSize, out FlightRecordEvent rec);
                if (IsZero(in rec)) holes++;
            }
            return (trimmed, holes);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MmfCircularBlackBoxWriter));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                try { _view.Flush(); } catch { /* 폐기 경로 — 최선 노력 플러시 */ }
                _view.Dispose();
                _mmf.Dispose();
            }
        }
    }
}
