// ============================================================================
// IJournalStore — S25-c: 저널 물리 I/O 추상화 (프로덕션 MMF ↔ 헤르메틱 인메모리)
//
//  CrashRecoveryJournaler 가 MMF-파일을 하드코딩하지 않도록 raw 바이트 read/write 를 분리한다.
//  · MmfJournalStore  — 실제 MemoryMappedFile(기존 동작 그대로). 프로덕션 경로.
//  · InMemoryJournalStore — byte[] 백킹의 '가상 MMF'. 디스크 접촉 0 → 카오스 테스트가 실제
//    파일시스템을 영구 오염시켜 후속 1,271 테스트 스위트를 flaky 하게 만드는 부작용을 원천 차단.
//
//  ── 계약(예외 의미론) ──────────────────────────────────────────────────────
//  I/O 실패는 store 가 던지고(throw), 분류는 journaler 의 try/catch 가 한다:
//    IOException·UnauthorizedAccessException → Inaccessible(오염 아님·비동결),
//    그 외 → Truncated(보수적 fail-closed). 따라서 store 는 상태를 판정하지 않는다(단일 책임).
// ============================================================================
using System;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace MS2E.Infrastructure
{
    /// <summary>저널 원장 바이트의 물리 backing. Read/Write 는 I/O 오류 시 예외를 전파한다(journaler 가 분류).</summary>
    public interface IJournalStore
    {
        /// <summary>backing 이 존재하는가(파일 부재·미생성 = false → 정상 신규 부팅 Empty).</summary>
        bool Exists { get; }

        /// <summary>현재 backing 길이(바이트). I/O 오류 시 예외 전파.</summary>
        long Length { get; }

        /// <summary>min(backing 길이, dest.Length) 바이트를 dest 로 읽어 그 길이를 반환. I/O 오류 시 예외 전파.</summary>
        int Read(byte[] dest);

        /// <summary>capacity 크기 backing 을 (재)생성하고 src 의 앞 len 바이트를 기록(나머지 0). I/O 오류 시 예외 전파.</summary>
        void Write(byte[] src, int len, long capacity);
    }

    /// <summary>프로덕션 MMF backing — 기존 CrashRecoveryJournaler 의 MMF 동작과 비트 동일.</summary>
    public sealed class MmfJournalStore : IJournalStore
    {
        private readonly string _path;
        public MmfJournalStore(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));

        public bool Exists => File.Exists(_path);

        // FileInfo.Length 는 부재 시 FileNotFoundException(IOException 파생)·락 시 IOException 을 던진다.
        // Exists 선검사 후 호출하나 TOCTOU 삭제는 IOException → journaler 가 Inaccessible 로 분류(비파괴).
        public long Length => new FileInfo(_path).Length;

        public int Read(byte[] dest)
        {
            long fileLen = new FileInfo(_path).Length;
            int  readLen = (int)Math.Min(fileLen, dest.Length);
            if (readLen <= 0) return 0;
            using var mmf  = MemoryMappedFile.CreateFromFile(
                _path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var view = mmf.CreateViewAccessor(0, readLen, MemoryMappedFileAccess.Read);
            view.ReadArray(0, dest, 0, readLen);
            return readLen;
        }

        public void Write(byte[] src, int len, long capacity)
        {
            using var mmf  = MemoryMappedFile.CreateFromFile(
                _path, FileMode.Create, null, capacity, MemoryMappedFileAccess.ReadWrite);
            using var view = mmf.CreateViewAccessor(0, capacity, MemoryMappedFileAccess.Write);
            view.WriteArray(0, src, 0, len);
        }
    }

    /// <summary>
    /// 헤르메틱 가상 MMF — byte[] 백킹. 실제 MMF 처럼 Write 시 capacity 크기 이미지(나머지 0)를 보존하므로
    /// 전원 차단 시점의 디스크 비트맵을 정밀 모사한다. GC 로 잔존물 0 수렴 → 디스크 부작용·flakiness 불가.
    /// 카오스 인젝션(torn write·비트 플립·접근불가·쓰기전 전원차단)을 결정론적으로 주입한다.
    /// </summary>
    public sealed class InMemoryJournalStore : IJournalStore
    {
        private byte[]? _data;
        private bool    _io;   // true → I/O 예외 모사(Inaccessible 경로)

        public bool Exists => _data is not null;

        public long Length => _io ? throw new IOException("InMemoryJournalStore: 모사 I/O 실패") : (_data?.Length ?? 0L);

        public int Read(byte[] dest)
        {
            if (_io) throw new IOException("InMemoryJournalStore: 모사 I/O 실패");
            if (_data is null) return 0;
            int n = Math.Min(_data.Length, dest.Length);
            Array.Copy(_data, dest, n);
            return n;
        }

        public void Write(byte[] src, int len, long capacity)
        {
            if (_io) throw new IOException("InMemoryJournalStore: 모사 I/O 실패");
            // 실제 MMF 는 파일을 capacity 로 선할당하고 len 바이트만 기록 → 잔여 0. 디스크 이미지를 동일 모사.
            var img = new byte[capacity];
            Array.Copy(src, 0, img, 0, len);
            _data = img;
        }

        // ── 카오스 인젝션(테스트 한정) ───────────────────────────────────────────
        /// <summary>전원 차단으로 backing 이 keepBytes 까지만 디스크에 도달(파일 절단). 구조체 중간 절단 가능.</summary>
        public void SimulateTornWrite(int keepBytes)
        {
            if (_data is null) return;
            Array.Resize(ref _data, Math.Max(0, Math.Min(keepBytes, _data.Length)));
        }

        /// <summary>capacity 선할당된 파일에서 offset 이후가 미플러시(0)로 남은 전원차단(MMF 의 현실 모드).</summary>
        public void ZeroFrom(int offset)
        {
            if (_data is null) return;
            for (int i = Math.Max(0, offset); i < _data.Length; i++) _data[i] = 0;
        }

        /// <summary>비가역 1비트 오염(불결한 종료·매체 결함) — CRC 불일치 유도.</summary>
        public void Corrupt(int offset)
        {
            if (_data is not null && (uint)offset < (uint)_data.Length) _data[offset] ^= 0xFF;
        }

        /// <summary>쓰기 시작 전 전원 차단 = 파일 미생성(부재). 정상 신규 부팅(Empty)이어야 동결 자가당착이 없다.</summary>
        public void SimulatePowerLossBeforeWrite() => _data = null;

        /// <summary>파일 락·권한 등 I/O 접근불가 모사 — Read/Length 가 IOException 을 던진다(Inaccessible).</summary>
        public void SetInaccessible(bool inaccessible) => _io = inaccessible;

        /// <summary>진단용 디스크 이미지 스냅샷(테스트 단언). 부재 시 null.</summary>
        public byte[]? Image => _data;
    }
}
