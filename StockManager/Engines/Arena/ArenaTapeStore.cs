// ============================================================================
// ArenaTapeStore — [v12.18.13-DEV] 아레나 바이너리 틱 테이프(.tape) — MMF 고정 레코드 기록/판독(핫패스 δ=0)
//
//  ★형식 정직: 10호가(가격 20 + 잔량 20)를 손실 없이 13B 에 담을 수 없다 — 13B 는 합성 테이프의 '매개변수' 압축(SyntheticTickSource)이다.
//    실호가 녹화는 무손실 고정 176B 레코드(가격 · 잔량 int32 — KRX 가격/잔량 범위 내 · 초과 잔량은 int32 상한 절단 계수)를 쓴다.
//    1M 레코드 ≈ 176MB(+ 헤더 64B). 고정 길이 = 임의 위치 판독(리플레이 배속 · 되감기)이 O(1).
//  헤더(64B · 리틀 엔디언): magic "MS2ETAPE"(8) · version u32 · recordSize u32 · count i64 · createdUtcTicks i64 · capacity i64 · 예약.
//  레코드(176B): ticks i64 · symbol i32 · kind u8(1 호가 · 2 체결 · 0 빈 슬롯) · side i8 · 예약 u16 ·
//    호가 = askPx[10] · askQty[10] · bidPx[10] · bidQty[10](int32) / 체결 = askPx[0] 가격 · askQty[0] 거래량(나머지 0).
//  ① 기록기(ArenaTapeWriter): 생성 시 파일을 용량만큼 확장 + MMF 뷰 포인터 1회 획득 → Append = 슬롯 claim(Interlocked) + 포인터 기록(할당 0 · 잠금 0).
//     용량 초과 = 기록 거부 계수(녹화 전용 · 매매 무관). 1,024 레코드마다 헤더 count 갱신(비정상 종료 시에도 그 지점까지 판독 가능 — 나머지 kind 0 = 건너뜀).
//     Seal = 신규 기록 차단 → 진행 중 기록 완료 대기 → 헤더 확정 → 뷰 해제 → 파일 크기를 실제 레코드 수로 절단.
//  ② 판독기(ArenaTapeReader : ArenaTickSource): 읽기 전용 MMF · 헤더 검증(magic · 버전 · 레코드 크기 · count ≤ 파일 길이) 실패 = InvalidDataException(fail-closed) ·
//     Read/TryReadTrade = 포인터 판독 → OrderBookPayload/ArenaTrade 전개(할당 0). 시각은 녹화 순서 그대로(역행 = 리플레이가 대기 없이 처리).
//  ③ 녹화기(ArenaTapeRecorder : IArenaTapePort): 정규장 라이브 호가 · 체결(누적 거래량 → 차분) 피드를 ON/OFF 로 기록. 피드 경로 비용 = 녹화 OFF 시 Volatile 1회.
//  ★녹화 · 재생 어느 쪽에도 주문/무장/브로커 경로가 없다.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Engines.Arena
{
    /// <summary>.tape 레코드(176B 고정 · 무손실 10호가 / 체결).</summary>
    public unsafe struct ArenaTapeRecord
    {
        public long Ticks;
        public int Symbol;
        public byte Kind;
        public sbyte Side;
        public ushort Reserved;
        public fixed int AskPx[10];
        public fixed int AskQty[10];
        public fixed int BidPx[10];
        public fixed int BidQty[10];
    }

    public static class ArenaTapeFormat
    {
        public static ReadOnlySpan<byte> MagicBytes => "MS2ETAPE"u8;
        public const int Version = 1, HeaderSize = 64, RecordSize = 176;
        public const byte KindEmpty = 0, KindBook = 1, KindTrade = 2;
        public const string Extension = ".tape";
        public const int CountFlushEvery = 1_024;

        public static long FileBytes(long records) => HeaderSize + records * RecordSize;

        internal static int Clamp32(long v) => v > int.MaxValue ? int.MaxValue : v < 0 ? 0 : (int)v;
    }

    /// <summary>MMF 테이프 기록기(다중 생산자 슬롯 claim · Append δ=0).</summary>
    public sealed unsafe class ArenaTapeWriter : IDisposable
    {
        private readonly FileStream _fs;
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly byte* _base;
        private readonly long _capacity;
        private long _next, _dropped, _clamped, _countMark;
        private int _open = 1, _inflight, _sealed;

        public string Path { get; }
        public long Capacity => _capacity;
        public long Count => Math.Min(Interlocked.Read(ref _next), _capacity);
        public long Dropped => Interlocked.Read(ref _dropped);
        /// <summary>int32 상한 절단된 잔량 값 수(KRX 실데이터에서는 0 이어야 함).</summary>
        public long Clamped => Interlocked.Read(ref _clamped);
        public bool IsOpen => Volatile.Read(ref _open) == 1;

        private ArenaTapeWriter(string path, long capacity, long createdTicks)
        {
            Path = path;
            _capacity = capacity;
            long bytes = ArenaTapeFormat.FileBytes(capacity);
            _fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.None);
            try
            {
                _fs.SetLength(bytes);
                _mmf = MemoryMappedFile.CreateFromFile(_fs, null, bytes, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
                _view = _mmf.CreateViewAccessor(0, bytes, MemoryMappedFileAccess.ReadWrite);
                byte* p = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                _base = p + _view.PointerOffset;
            }
            catch
            {
                _view?.Dispose(); _mmf?.Dispose(); _fs.Dispose();
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            var h = new Span<byte>(_base, ArenaTapeFormat.HeaderSize);
            h.Clear();
            ArenaTapeFormat.MagicBytes.CopyTo(h);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(h.Slice(8), ArenaTapeFormat.Version);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(h.Slice(12), ArenaTapeFormat.RecordSize);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(h.Slice(16), 0);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(h.Slice(24), createdTicks);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(h.Slice(32), capacity);
        }

        /// <summary>새 테이프 생성(기존 파일 = IOException · 용량 1 ~ 12M 레코드).</summary>
        public static ArenaTapeWriter Create(string path, long capacityRecords, long createdUtcTicks = 0)
        {
            if (capacityRecords < 1 || capacityRecords > 12_000_000) throw new ArgumentOutOfRangeException(nameof(capacityRecords));
            return new ArenaTapeWriter(path ?? throw new ArgumentNullException(nameof(path)), capacityRecords,
                                       createdUtcTicks > 0 ? createdUtcTicks : DateTime.UtcNow.Ticks);
        }

        private bool Enter()
        {
            if (Volatile.Read(ref _open) == 0) return false;
            Interlocked.Increment(ref _inflight);
            if (Volatile.Read(ref _open) != 0) return true;
            Interlocked.Decrement(ref _inflight);
            return false;
        }

        private ArenaTapeRecord* Claim()
        {
            long idx = Interlocked.Increment(ref _next) - 1;
            if (idx >= _capacity) { Interlocked.Increment(ref _dropped); return null; }
            return (ArenaTapeRecord*)(_base + ArenaTapeFormat.HeaderSize + idx * ArenaTapeFormat.RecordSize);
        }

        private void Commit(ArenaTapeRecord* r, byte kind)
        {
            Volatile.Write(ref r->Kind, kind);                                       // 종류 = 마지막 기록(부분 기록 슬롯 = kind 0 → 판독 건너뜀)
            long n = ((byte*)r - _base - ArenaTapeFormat.HeaderSize) / ArenaTapeFormat.RecordSize + 1;
            if (n % ArenaTapeFormat.CountFlushEvery == 0) PublishCount(n);
        }

        private void PublishCount(long n)
        {
            long cur;
            do { cur = Interlocked.Read(ref _countMark); if (n <= cur) return; } while (Interlocked.CompareExchange(ref _countMark, n, cur) != cur);
            Volatile.Write(ref *(long*)(_base + 16), n);
        }

        /// <summary>10호가 1건 기록(δ=0). 반환 false = 봉인됨 · 용량 초과.</summary>
        public bool AppendBook(in OrderBookPayload b, long ticks)
        {
            if (!Enter()) return false;
            try
            {
                var r = Claim();
                if (r == null) return false;
                r->Ticks = ticks; r->Symbol = ArenaTapeFormat.Clamp32(b.SymbolCode); r->Side = 0; r->Reserved = 0;
                long clamped = 0;
                for (int l = 0; l < 10; l++)
                {
                    r->AskPx[l] = ArenaTapeFormat.Clamp32(b.AskPrice[l]); r->BidPx[l] = ArenaTapeFormat.Clamp32(b.BidPrice[l]);
                    long aq = b.AskQty[l], bq = b.BidQty[l];
                    if (aq > int.MaxValue) clamped++;
                    if (bq > int.MaxValue) clamped++;
                    r->AskQty[l] = ArenaTapeFormat.Clamp32(aq); r->BidQty[l] = ArenaTapeFormat.Clamp32(bq);
                }
                if (clamped > 0) Interlocked.Add(ref _clamped, clamped);
                Commit(r, ArenaTapeFormat.KindBook);
                return true;
            }
            finally { Interlocked.Decrement(ref _inflight); }
        }

        /// <summary>체결 1건 기록(δ=0 · 거래량 = 체결 틱 거래량 — 누적 아님).</summary>
        public bool AppendTrade(in ArenaTrade t, long ticks)
        {
            if (!Enter()) return false;
            try
            {
                var r = Claim();
                if (r == null) return false;
                *r = default;
                r->Ticks = ticks; r->Symbol = ArenaTapeFormat.Clamp32(t.Symbol); r->Side = (sbyte)Math.Sign(t.Side);
                r->AskPx[0] = ArenaTapeFormat.Clamp32(t.PriceWon); r->AskQty[0] = ArenaTapeFormat.Clamp32(t.Qty);
                Commit(r, ArenaTapeFormat.KindTrade);
                return true;
            }
            finally { Interlocked.Decrement(ref _inflight); }
        }

        /// <summary>봉인: 신규 기록 차단 → 진행 중 기록 완료 대기 → 헤더 확정 → 뷰 해제 → 파일 절단. 반환 = 최종 레코드 수.</summary>
        public long Seal()
        {
            if (Interlocked.Exchange(ref _sealed, 1) == 1) return Count;
            Volatile.Write(ref _open, 0);
            var spin = new SpinWait();
            while (Volatile.Read(ref _inflight) != 0) spin.SpinOnce();
            long count = Count;
            Volatile.Write(ref *(long*)(_base + 16), count);
            _view.Flush();
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _mmf.Dispose();
            _fs.SetLength(ArenaTapeFormat.FileBytes(count));
            _fs.Flush(flushToDisk: true);
            _fs.Dispose();
            return count;
        }

        public void Dispose() => Seal();
    }

    /// <summary>MMF 테이프 판독기(읽기 전용 · 판독 δ=0).</summary>
    public sealed unsafe class ArenaTapeReader : ArenaTickSource, IDisposable
    {
        private readonly FileStream _fs;
        private readonly MemoryMappedFile? _mmf;
        private readonly MemoryMappedViewAccessor? _view;
        private readonly byte* _records;
        private readonly int _count;
        private int _disposed;

        public string Path { get; }
        public long CreatedUtcTicks { get; }
        public override int Count => _count;

        private ArenaTapeReader(string path)
        {
            Path = path;
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);   // 녹화 중/비정상 종료 테이프도 헤더 count 까지 판독
            try
            {
                long len = _fs.Length;
                if (len < ArenaTapeFormat.HeaderSize) throw new InvalidDataException("테이프 헤더 부족(" + len + "B)");
                Span<byte> h = stackalloc byte[ArenaTapeFormat.HeaderSize];
                _fs.ReadExactly(h);
                if (!h.Slice(0, 8).SequenceEqual(ArenaTapeFormat.MagicBytes)) throw new InvalidDataException("테이프 magic 불일치");
                int ver = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(h.Slice(8));
                int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(h.Slice(12));
                long count = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(h.Slice(16));
                CreatedUtcTicks = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(h.Slice(24));
                if (ver != ArenaTapeFormat.Version) throw new InvalidDataException("테이프 버전 " + ver + " 미지원");
                if (size != ArenaTapeFormat.RecordSize) throw new InvalidDataException("레코드 크기 " + size + " ≠ " + ArenaTapeFormat.RecordSize);
                long fit = (len - ArenaTapeFormat.HeaderSize) / ArenaTapeFormat.RecordSize;
                if (count < 0 || count > fit) throw new InvalidDataException("레코드 수 " + count + " > 파일 수용 " + fit);
                if (count > int.MaxValue) throw new InvalidDataException("레코드 수 초과");
                _count = (int)count;
                if (_count > 0)
                {
                    _mmf = MemoryMappedFile.CreateFromFile(_fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                    _view = _mmf.CreateViewAccessor(0, ArenaTapeFormat.FileBytes(count), MemoryMappedFileAccess.Read);
                    byte* p = null;
                    _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                    _records = p + _view.PointerOffset + ArenaTapeFormat.HeaderSize;
                }
            }
            catch
            {
                _view?.Dispose(); _mmf?.Dispose(); _fs.Dispose();
                throw;
            }
        }

        /// <summary>테이프 열기(검증 실패 = InvalidDataException · 파일 오류 = IOException).</summary>
        public static ArenaTapeReader Open(string path) => new(path ?? throw new ArgumentNullException(nameof(path)));

        private ArenaTapeRecord* At(int index)
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return (ArenaTapeRecord*)(_records + (long)index * ArenaTapeFormat.RecordSize);
        }

        public byte KindOf(int index) => At(index)->Kind;
        public override long TimestampOf(int index) => At(index)->Ticks;

        public override bool Read(int index, out OrderBookPayload book)
        {
            var r = At(index);
            book = default;
            if (r->Kind != ArenaTapeFormat.KindBook) return false;
            book.SymbolCode = r->Symbol; book.TimestampTicks = r->Ticks;
            for (int l = 0; l < 10; l++)
            {
                book.AskPrice[l] = r->AskPx[l]; book.AskQty[l] = r->AskQty[l];
                book.BidPrice[l] = r->BidPx[l]; book.BidQty[l] = r->BidQty[l];
            }
            return true;
        }

        public override bool TryReadTrade(int index, out ArenaTrade trade)
        {
            var r = At(index);
            if (r->Kind != ArenaTapeFormat.KindTrade) { trade = default; return false; }
            trade = new ArenaTrade { Symbol = r->Symbol, PriceWon = r->AskPx[0], Qty = r->AskQty[0], Side = r->Side };
            return true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            if (_view is not null) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _view.Dispose(); }
            _mmf?.Dispose();
            _fs.Dispose();
        }
    }

    /// <summary>
    /// 정규장 라이브 녹화기(IArenaTapePort) — 브리지 수신 스레드에서 OnBook/OnTick 호출(녹화 OFF = Volatile 1회 · ON = 포인터 기록 δ=0).
    /// 체결 거래량 = 누적 거래량 차분(종목별 · 첫 관측 = 기준선). 녹화 파일 = 디렉터리/yyyyMMdd_HHmmss.tape(UTC).
    /// </summary>
    public sealed class ArenaTapeRecorder : IArenaTapePort, IDisposable
    {
        public const long DefaultCapacity = 2_000_000;

        private readonly string _dir;
        private readonly long _capacity;
        private readonly Func<long> _utcTicks;
        private readonly object _gate = new();
        private readonly CumulativeVolumeTracker _volume = new();
        private ArenaTapeWriter? _writer;
        private long _lastCount, _lastDropped;
        private string? _lastFile;

        public ArenaTapeRecorder(string directory, long capacityRecords = DefaultCapacity, Func<long>? utcTicks = null)
        {
            _dir = directory ?? throw new ArgumentNullException(nameof(directory));
            _capacity = capacityRecords;
            _utcTicks = utcTicks ?? (static () => DateTime.UtcNow.Ticks);
        }

        public string Directory => _dir;
        public bool Recording => Volatile.Read(ref _writer) is not null;
        public long RecordedCount => Volatile.Read(ref _writer)?.Count ?? Interlocked.Read(ref _lastCount);
        public long RecordDropped => Volatile.Read(ref _writer)?.Dropped ?? Interlocked.Read(ref _lastDropped);
        public string? RecordingFile => Volatile.Read(ref _writer)?.Path ?? _lastFile;

        /// <summary>호가 피드(브리지 수신 스레드 · δ=0).</summary>
        public void OnBook(in OrderBookPayload b) => Volatile.Read(ref _writer)?.AppendBook(in b, _utcTicks());

        /// <summary>체결 피드(누적 거래량 · 브리지 수신 스레드 · δ=0). 녹화 중이 아니어도 기준선 유지(재개 첫 틱 오계상 0).</summary>
        public void OnTick(long symbol, long priceWon, long cumulativeVolume)
        {
            long qty = _volume.Delta(symbol, cumulativeVolume);
            var w = Volatile.Read(ref _writer);
            if (w is null || qty <= 0 || priceWon <= 0) return;
            w.AppendTrade(new ArenaTrade { Symbol = symbol, PriceWon = priceWon, Qty = qty }, _utcTicks());
        }

        public bool StartRecording()
        {
            lock (_gate)
            {
                if (_writer is not null) return false;
                try
                {
                    System.IO.Directory.CreateDirectory(_dir);
                    long now = _utcTicks();
                    string path = System.IO.Path.Combine(_dir, new DateTime(now, DateTimeKind.Utc).ToString("yyyyMMdd_HHmmss_fff") + ArenaTapeFormat.Extension);
                    Volatile.Write(ref _writer, ArenaTapeWriter.Create(path, _capacity, now));
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Trace.WriteLine("[ArenaTape] 녹화 시작 실패: " + ex.Message);
                    return false;
                }
            }
        }

        public void StopRecording()
        {
            lock (_gate)
            {
                var w = Interlocked.Exchange(ref _writer, null);
                if (w is null) return;
                Interlocked.Exchange(ref _lastCount, w.Seal());                  // 봉인 = 진행 중 기록 완료 대기 후 확정
                Interlocked.Exchange(ref _lastDropped, w.Dropped);
                _lastFile = w.Path;
            }
        }

        public IReadOnlyList<ArenaTapeInfo> ListTapes() => ArenaTapeStore.List(_dir, Volatile.Read(ref _writer)?.Path);

        public void Dispose() => StopRecording();
    }

    /// <summary>테이프 디렉터리 목록(헤더만 판독 · 콜드).</summary>
    public static class ArenaTapeStore
    {
        public static string DefaultDirectory { get; } = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MS2E.StockSuite", "tapes");

        public static IReadOnlyList<ArenaTapeInfo> List(string directory, string? exclude = null)
        {
            var list = new List<ArenaTapeInfo>();
            if (!System.IO.Directory.Exists(directory)) return list;
            foreach (var f in new DirectoryInfo(directory).GetFiles("*" + ArenaTapeFormat.Extension))
            {
                if (exclude is not null && string.Equals(f.FullName, System.IO.Path.GetFullPath(exclude), StringComparison.OrdinalIgnoreCase)) continue;
                long records = -1;
                try { using var r = ArenaTapeReader.Open(f.FullName); records = r.Count; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { }
                list.Add(new ArenaTapeInfo(f.FullName, f.Name, records, f.Length, f.CreationTimeUtc));
            }
            list.Sort(static (a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
            return list;
        }
    }
}
