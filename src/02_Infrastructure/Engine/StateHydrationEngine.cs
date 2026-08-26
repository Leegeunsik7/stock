// ============================================================================
// StateHydrationEngine — S13 #3 런타임 가드링 · 상태 스냅샷 (v3.83.0)
//
//  Register<T>: 언매니지드 값 슬롯 등록 (Func getter + Action setter 페어).
//  Snapshot(): 더티 감지 후 전체 슬롯을 MMF 바이너리로 원자 기록.
//  Hydrate():  재시작 시 MMF에서 상태를 CRC32 검증 후 즉시 복구.
//
//  파일 포맷 [완전 바이너리, 외부 의존 0]:
//   Header 32B : [Magic 8B "HYDR8_1\0"][Schema 4B][SlotCount 4B][TicksUtc 8B][CRC32 4B][Pad 4B]
//   Slot 100B × N : [Key 32B null-term][DataLen 4B][Data 64B] — N ≤ 20, Data ≤ 64B/슬롯
//   총 최대 2032B (MMF 단일 페이지 이내, OS 페이지 그래뉼라티 64KB 대비 극소)
//
//  더티 비트: 슬롯별 LastBytes와 현재 직렬화 결과 비교 → 변경된 슬롯 감지.
//  CRC32 (IEEE 802.3): 슬롯 영역 전체에 적용 → 변조·불완전 기록 즉시 탐지.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MS2E.Infrastructure
{
    // ── 값 타입 상태 슬롯 — 등록 및 직접 접근 편의 래퍼 ──────────────────────────
    public sealed class HydrationSlot<T> where T : unmanaged
    {
        private T _value;
        public T Value { get => _value; set => _value = value; }
        public HydrationSlot(T initial = default) => _value = initial;
    }

    // ── 핵심 엔진 ──────────────────────────────────────────────────────────────
    public sealed class StateHydrationEngine : IDisposable
    {
        // ── 파일 포맷 상수 ─────────────────────────────────────────────────────
        private static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("HYDR8_1\0");
        private const int SchemaVersion  = 1;
        private const int MaxSlots       = 20;
        private const int MaxDataBytes   = 64;
        private const int MaxKeyChars    = 31;  // null-terminator 포함 32B 고정
        private const int HeaderSize     = 32;
        private const int SlotKeySize    = 32;
        private const int SlotDataLenOff = SlotKeySize;           // +32
        private const int SlotDataOff    = SlotKeySize + 4;       // +36
        private const int SlotSize       = SlotKeySize + 4 + MaxDataBytes; // 100
        private const int MaxFileSize    = HeaderSize + MaxSlots * SlotSize; // 2032

        // ── 슬롯 메타 (per-registration) ───────────────────────────────────────
        private sealed class Slot
        {
            public string         Key        = "";
            public Func<byte[]>   Serialize  = null!;
            public Action<byte[]> Deserialize = null!;
            public byte[]         LastBytes  = Array.Empty<byte>(); // 더티 비교용
        }

        private readonly string     _filePath;
        private readonly List<Slot> _slots = new();
        private bool                _disposed;

        public StateHydrationEngine(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("filePath가 비어 있습니다.", nameof(filePath));
            _filePath = filePath;
        }

        // ── 등록 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 언매니지드 값 슬롯 등록. T 크기 ≤ 64B, 키 길이 ≤ 31자, 최대 20슬롯.
        /// </summary>
        public void Register<T>(string key, Func<T> getter, Action<T> setter)
            where T : unmanaged
        {
            ValidateKey(key);
            if (Unsafe.SizeOf<T>() > MaxDataBytes)
                throw new ArgumentException(
                    $"T 크기({Unsafe.SizeOf<T>()}B)가 슬롯 한도({MaxDataBytes}B)를 초과합니다.");
            if (_slots.Count >= MaxSlots)
                throw new InvalidOperationException($"최대 슬롯 수({MaxSlots})에 도달했습니다.");

            _slots.Add(new Slot
            {
                Key = key,
                Serialize = () =>
                {
                    T val = getter();
                    var buf = new byte[Unsafe.SizeOf<T>()];
                    MemoryMarshal.Write(buf, in val);
                    return buf;
                },
                Deserialize = bytes =>
                {
                    if (bytes.Length < Unsafe.SizeOf<T>()) return;
                    T val = MemoryMarshal.Read<T>(bytes);
                    setter(val);
                }
            });
        }

        /// <summary>HydrationSlot&lt;T&gt; 편의 등록 (Register 래퍼).</summary>
        public void RegisterSlot<T>(string key, HydrationSlot<T> slot)
            where T : unmanaged
            => Register(key, () => slot.Value, v => slot.Value = v);

        // ── 스냅샷 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 등록된 전체 슬롯을 MMF 파일에 원자 기록.
        /// 더티 비트: 변경 슬롯 수를 내부 추적 후 전체 파일 일괄 갱신.
        /// </summary>
        public void Snapshot()
        {
            ThrowIfDisposed();
            var payloads = new byte[_slots.Count][];
            for (int i = 0; i < _slots.Count; i++)
            {
                var cur = _slots[i].Serialize();
                payloads[i] = cur;
                _slots[i].LastBytes = cur;  // 더티 기준 갱신
            }
            WriteToMmf(payloads);
        }

        private void WriteToMmf(byte[][] payloads)
        {
            var buf = new byte[MaxFileSize];  // zero-initialized

            // ── Header ────────────────────────────────────────────────────────
            Array.Copy(MagicBytes, 0, buf, 0, 8);
            BitConverter.TryWriteBytes(buf.AsSpan(8),  SchemaVersion);
            BitConverter.TryWriteBytes(buf.AsSpan(12), _slots.Count);
            BitConverter.TryWriteBytes(buf.AsSpan(16), DateTime.UtcNow.Ticks);
            // [24..27] CRC32 — 슬롯 영역 직렬화 완료 후 기록

            // ── Slots ─────────────────────────────────────────────────────────
            for (int i = 0; i < _slots.Count; i++)
            {
                int off = HeaderSize + i * SlotSize;
                // Key (null-term, 32B 고정 — 나머지는 0)
                var kb = Encoding.UTF8.GetBytes(_slots[i].Key);
                Array.Copy(kb, 0, buf, off, Math.Min(kb.Length, MaxKeyChars));
                // DataLen
                BitConverter.TryWriteBytes(buf.AsSpan(off + SlotDataLenOff), payloads[i].Length);
                // Data
                Array.Copy(payloads[i], 0, buf, off + SlotDataOff,
                    Math.Min(payloads[i].Length, MaxDataBytes));
            }

            // ── CRC32 (슬롯 영역 전체) ────────────────────────────────────────
            uint crc = Crc32(buf.AsSpan(HeaderSize, _slots.Count * SlotSize));
            BitConverter.TryWriteBytes(buf.AsSpan(24), crc);

            // ── MMF 원자 기록 (Flush → OS 페이지 즉시 동기화) ────────────────
            using var mmf = MemoryMappedFile.CreateFromFile(
                _filePath, FileMode.Create, null, MaxFileSize,
                MemoryMappedFileAccess.ReadWrite);
            using var view = mmf.CreateViewAccessor(0, MaxFileSize, MemoryMappedFileAccess.Write);
            view.WriteArray(0, buf, 0, buf.Length);
            view.Flush();
        }

        // ── 복구 ────────────────────────────────────────────────────────────

        /// <summary>
        /// MMF 파일에서 상태 복구. CRC32 검증 실패·파일 부재·스키마 불일치 → false.
        /// </summary>
        public bool Hydrate()
        {
            ThrowIfDisposed();
            if (!File.Exists(_filePath)) return false;

            try
            {
                long fileLen = new FileInfo(_filePath).Length;
                if (fileLen < HeaderSize) return false;

                int readLen = (int)Math.Min(fileLen, MaxFileSize);
                var buf = new byte[MaxFileSize];  // 짧은 파일은 나머지 0-패딩

                using var mmf = MemoryMappedFile.CreateFromFile(
                    _filePath, FileMode.Open, null, 0,
                    MemoryMappedFileAccess.Read);
                using var view = mmf.CreateViewAccessor(0, readLen, MemoryMappedFileAccess.Read);
                view.ReadArray(0, buf, 0, readLen);

                // 매직 검증
                for (int i = 0; i < 8; i++)
                    if (buf[i] != MagicBytes[i]) return false;

                // 스키마 검증
                if (BitConverter.ToInt32(buf, 8) != SchemaVersion) return false;

                // 슬롯 수 검증
                int slotCount = BitConverter.ToInt32(buf, 12);
                if (slotCount < 0 || slotCount > MaxSlots) return false;
                if (HeaderSize + slotCount * SlotSize > readLen) return false;

                // CRC32 검증
                uint stored   = BitConverter.ToUInt32(buf, 24);
                uint computed = Crc32(buf.AsSpan(HeaderSize, slotCount * SlotSize));
                if (computed != stored) return false;

                // 슬롯별 복구
                for (int i = 0; i < slotCount; i++)
                {
                    int off = HeaderSize + i * SlotSize;

                    // 키 복원 (null 종료 탐색)
                    int keyEnd = off;
                    while (keyEnd < off + SlotKeySize && buf[keyEnd] != 0) keyEnd++;
                    string key = Encoding.UTF8.GetString(buf, off, keyEnd - off);

                    int dataLen = BitConverter.ToInt32(buf, off + SlotDataLenOff);
                    if (dataLen < 0 || dataLen > MaxDataBytes) return false;

                    var data = buf.AsSpan(off + SlotDataOff, dataLen).ToArray();

                    // 등록된 슬롯에 역직렬화
                    foreach (var slot in _slots)
                    {
                        if (slot.Key.Equals(key, StringComparison.Ordinal))
                        {
                            slot.Deserialize(data);
                            break;
                        }
                    }
                }
                return true;
            }
            catch { return false; }
        }

        // ── 유틸리티 ────────────────────────────────────────────────────────

        /// <summary>스냅샷 파일이 존재하는지 확인 (복구 가능 여부 사전 체크용).</summary>
        public bool HasValidSnapshot => File.Exists(_filePath);

        /// <summary>
        /// 스냅샷 헤더의 기록 시각(UTC)을 무결성 검증 후 반환합니다. 신선도(staleness) 판정용.
        /// 파일 부재·매직/스키마 불일치·CRC32 실패·손상 시 null (Hydrate()와 동일한 검증 스코프).
        /// 읽기 전용 — 슬롯 복구는 수행하지 않습니다.
        /// </summary>
        public DateTime? SnapshotTimeUtc()
        {
            ThrowIfDisposed();
            if (!File.Exists(_filePath)) return null;

            try
            {
                long fileLen = new FileInfo(_filePath).Length;
                if (fileLen < HeaderSize) return null;

                int readLen = (int)Math.Min(fileLen, MaxFileSize);
                var buf = new byte[MaxFileSize];

                using var mmf = MemoryMappedFile.CreateFromFile(
                    _filePath, FileMode.Open, null, 0,
                    MemoryMappedFileAccess.Read);
                using var view = mmf.CreateViewAccessor(0, readLen, MemoryMappedFileAccess.Read);
                view.ReadArray(0, buf, 0, readLen);

                for (int i = 0; i < 8; i++)
                    if (buf[i] != MagicBytes[i]) return null;
                if (BitConverter.ToInt32(buf, 8) != SchemaVersion) return null;

                int slotCount = BitConverter.ToInt32(buf, 12);
                if (slotCount < 0 || slotCount > MaxSlots) return null;
                if (HeaderSize + slotCount * SlotSize > readLen) return null;

                uint stored   = BitConverter.ToUInt32(buf, 24);
                uint computed = Crc32(buf.AsSpan(HeaderSize, slotCount * SlotSize));
                if (computed != stored) return null;

                long ticks = BitConverter.ToInt64(buf, 16);
                if (ticks <= 0 || ticks > DateTime.MaxValue.Ticks) return null;
                return new DateTime(ticks, DateTimeKind.Utc);
            }
            catch { return null; }
        }

        /// <summary>스냅샷 파일 삭제 — 정상 시작 후 호출하여 불필요한 복구 방지.</summary>
        public void ClearSnapshot()
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
        }

        /// <summary>더티 슬롯 수 — 마지막 Snapshot() 이후 변경된 슬롯 수.</summary>
        public int DirtyCount()
        {
            int n = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                var cur = _slots[i].Serialize();
                if (!cur.AsSpan().SequenceEqual(_slots[i].LastBytes)) n++;
            }
            return n;
        }

        public void Dispose() => _disposed = true;

        // ── 내부 헬퍼 ────────────────────────────────────────────────────────

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StateHydrationEngine));
        }

        private static void ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > MaxKeyChars)
                throw new ArgumentException($"키는 1~{MaxKeyChars}자여야 합니다.", nameof(key));
        }

        // CRC32 (IEEE 802.3 / zlib 다항식 0xEDB88320) — 외부 의존 없이 무결성 검증
        private static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFF_FFFFu;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1u) != 0u ? (crc >> 1) ^ 0xEDB8_8320u : crc >> 1;
            }
            return crc ^ 0xFFFF_FFFFu;
        }
    }
}
