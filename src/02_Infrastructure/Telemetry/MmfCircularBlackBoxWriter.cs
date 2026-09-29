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
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>FlightRecordEvent(64B blittable)를 실제 OS MemoryMappedFile 에 순환 기록하는 δ=0 블랙박스.
    /// ★단일 라이터 계약(Append) — 다중 라이터 결선이 필요하면 상위가 직렬화한다(MMF 뷰 자체는 스레드 미보증).</summary>
    public sealed class MmfCircularBlackBoxWriter : IDisposable
    {
        // 헤더 레이아웃(64B): [0]Magic(long) [8]Capacity(long) [16]WriteSeq(long) [24..63]Reserved
        private const int HeaderBytes = 64;
        private const long HeaderMagic = 0x4D53_3245_4D4D_4600L;   // "MS2E" + MMF\0 근사

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly int _capacity;
        private long _writeSeq;
        private bool _disposed;

        public string Path { get; }
        public int Capacity => _capacity;
        public long WriteSeq => Volatile.Read(ref _writeSeq);

        public MmfCircularBlackBoxWriter(string path, int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            Path = path;
            _capacity = capacity;
            long size = HeaderBytes + (long)capacity * MmfFlightRecorder.EventSize;

            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.OpenOrCreate, mapName: null, capacity: size);
            _view = _mmf.CreateViewAccessor(0, size);

            long existingMagic = _view.ReadInt64(0);
            if (existingMagic == HeaderMagic)
            {
                _writeSeq = _view.ReadInt64(16);   // 기존 파일 재오픈 — 이전 시퀀스 이어받기(크래시 후 재기동)
            }
            else
            {
                _view.Write(0, HeaderMagic);
                _view.Write(8, (long)capacity);
                _view.Write(16, 0L);
            }
        }

        /// <summary>레코드 1건을 순환 슬롯에 기록(★단일 라이터·정상 경로 δ=0). 용량 초과 시 최고참 슬롯을 덮어쓴다
        /// (드롭 대신 최신 보존 — 크래시 직전 최신 상태가 가장 포렌식 가치가 높다). 값타입 매개변수 — Unsafe/포인터 0.</summary>
        public void Append(FlightRecordEvent ev)
        {
            ThrowIfDisposed();
            long seq = _writeSeq++;
            int idx = (int)(seq % _capacity);
            long offset = HeaderBytes + (long)idx * MmfFlightRecorder.EventSize;
            _view.Write(offset, ref ev);
            _view.Write(16, _writeSeq);   // 헤더 최신 시퀀스 갱신(재기동 시 이어받기 + 사후 판독 유효범위 판단용)
        }

        /// <summary>디스크 반영을 명시 요청(치명적 상황 전용 — 평시엔 OS 지연쓰기에 맡겨 I/O 비용 회피).</summary>
        public void Flush() => _view.Flush();

        /// <summary>기록된(랩 이전까지 최대 capacity 개) 레코드를 시퀀스 순으로 복원(δ=0 아님 — 저빈도 사후 조회 전용).</summary>
        public int ReadAll(Span<FlightRecordEvent> dest)
        {
            long seq = Volatile.Read(ref _writeSeq);
            long count = Math.Min(seq, _capacity);
            long floor = seq - count;
            int n = 0;
            for (long s = floor; s < seq && n < dest.Length; s++)
            {
                int idx = (int)(s % _capacity);
                long offset = HeaderBytes + (long)idx * MmfFlightRecorder.EventSize;
                _view.Read(offset, out FlightRecordEvent rec);
                dest[n++] = rec;
            }
            return n;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MmfCircularBlackBoxWriter));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _view.Flush(); } catch { /* 폐기 경로 — 최선 노력 플러시 */ }
            _view.Dispose();
            _mmf.Dispose();
        }
    }
}
