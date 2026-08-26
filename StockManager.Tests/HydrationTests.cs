// ============================================================================
// HydrationTests — S13 #3 런타임 가드링 · 상태 스냅샷 단위 테스트
//  · Snapshot: 파일 생성 · 매직 헤더 · 슬롯 수 · 데이터 바이트 일치
//  · Hydrate: int/double/enum/다중슬롯 라운드트립 (프로세스 재시작 시뮬레이션)
//  · 무결성: 변조 파일(매직·CRC 손상) → false 반환
//  · 더티 비트: DirtyCount() 정확도
//  · 부가: HydrationSlot<T> 라운드트립 · ClearSnapshot · HasValidSnapshot
// ============================================================================
using System;
using System.IO;
using Xunit;

namespace StockManager.Tests
{
    public class HydrationTests : IDisposable
    {
        private readonly string _path;

        public HydrationTests()
            => _path = Path.Combine(Path.GetTempPath(), $"hydration_{Guid.NewGuid():N}.bin");

        public void Dispose()
        {
            try { File.Delete(_path); } catch { }
        }

        // ════════════════════════════════════════════════════════════════════
        // 1. 파일 생성 · 헤더 검증
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Snapshot_CreatesFile()
        {
            using var eng = new StateHydrationEngine(_path);
            int v = 1;
            eng.Register("v", () => v, x => v = x);
            eng.Snapshot();
            Assert.True(File.Exists(_path));
        }

        [Fact]
        public void Snapshot_MagicHeader_CorrectBytes()
        {
            using var eng = new StateHydrationEngine(_path);
            int v = 0;
            eng.Register("v", () => v, x => v = x);
            eng.Snapshot();

            var bytes = File.ReadAllBytes(_path);
            Assert.Equal((byte)'H', bytes[0]);
            Assert.Equal((byte)'Y', bytes[1]);
            Assert.Equal((byte)'D', bytes[2]);
            Assert.Equal((byte)'R', bytes[3]);
            Assert.Equal((byte)'8', bytes[4]);
            Assert.Equal((byte)'_', bytes[5]);
            Assert.Equal((byte)'1', bytes[6]);
            Assert.Equal(0,         bytes[7]);
        }

        [Fact]
        public void Snapshot_SlotCount_WrittenToHeader()
        {
            using var eng = new StateHydrationEngine(_path);
            int a = 1, b = 2;
            eng.Register("a", () => a, x => a = x);
            eng.Register("b", () => b, x => b = x);
            eng.Snapshot();

            var bytes = File.ReadAllBytes(_path);
            int slotCount = BitConverter.ToInt32(bytes, 12);
            Assert.Equal(2, slotCount);
        }

        [Fact]
        public void Snapshot_ByteForByte_IntPayloadCorrect()
        {
            int val = 0x12345678;
            using var eng = new StateHydrationEngine(_path);
            eng.Register("x", () => val, v => val = v);
            eng.Snapshot();

            var bytes = File.ReadAllBytes(_path);
            // 슬롯 0 데이터 시작 = Header(32) + Key(32) + DataLen(4) = 68
            int dataOff = 32 + 32 + 4;
            Assert.Equal(0x12345678, BitConverter.ToInt32(bytes, dataOff));
        }

        // ════════════════════════════════════════════════════════════════════
        // 2. Hydrate 없음 → false
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Hydrate_NoFile_ReturnsFalse()
        {
            using var eng = new StateHydrationEngine(_path);
            Assert.False(eng.Hydrate());
        }

        // ════════════════════════════════════════════════════════════════════
        // 3. 라운드트립 (프로세스 재시작 시뮬레이션)
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Snapshot_Hydrate_RestoresInt()
        {
            // "프로세스 A" — 저장
            {
                using var eng = new StateHydrationEngine(_path);
                int v = 99;
                eng.Register("v", () => v, x => v = x);
                eng.Snapshot();
            }

            // "프로세스 B" — 복구
            int restored = 0;
            using var eng2 = new StateHydrationEngine(_path);
            eng2.Register("v", () => restored, x => restored = x);
            Assert.True(eng2.Hydrate());
            Assert.Equal(99, restored);
        }

        [Fact]
        public void Snapshot_Hydrate_RestoresDouble()
        {
            {
                using var eng = new StateHydrationEngine(_path);
                double v = 42.1234567890123;
                eng.Register("d", () => v, x => v = x);
                eng.Snapshot();
            }

            double r = 0.0;
            using var eng2 = new StateHydrationEngine(_path);
            eng2.Register("d", () => r, x => r = x);
            Assert.True(eng2.Hydrate());
            Assert.Equal(42.1234567890123, r, precision: 13);
        }

        [Fact]
        public void Snapshot_Hydrate_RestoresEnum_GatingMode()
        {
            {
                using var eng = new StateHydrationEngine(_path);
                int mode = (int)GatingMode.EmergencyFreeze;
                eng.Register("mode", () => mode, x => mode = x);
                eng.Snapshot();
            }

            int restored = (int)GatingMode.Normal;
            using var eng2 = new StateHydrationEngine(_path);
            eng2.Register("mode", () => restored, x => restored = x);
            Assert.True(eng2.Hydrate());
            Assert.Equal((int)GatingMode.EmergencyFreeze, restored);
        }

        [Fact]
        public void Snapshot_Hydrate_MultipleSlots_AllRestored()
        {
            int shortP = 5, longP = 20, seed = 42;
            double ret = 15.678;
            {
                using var eng = new StateHydrationEngine(_path);
                eng.Register("shortP", () => shortP, x => shortP = x);
                eng.Register("longP",  () => longP,  x => longP  = x);
                eng.Register("seed",   () => seed,   x => seed   = x);
                eng.Register("ret",    () => ret,    x => ret    = x);
                eng.Snapshot();
            }

            int rs = 0, rl = 0, rseed = 0; double rret = 0.0;
            using var eng2 = new StateHydrationEngine(_path);
            eng2.Register("shortP", () => rs,    x => rs    = x);
            eng2.Register("longP",  () => rl,    x => rl    = x);
            eng2.Register("seed",   () => rseed, x => rseed = x);
            eng2.Register("ret",    () => rret,  x => rret  = x);
            Assert.True(eng2.Hydrate());
            Assert.Equal(5,  rs);
            Assert.Equal(20, rl);
            Assert.Equal(42, rseed);
            Assert.Equal(15.678, rret, precision: 10);
        }

        [Fact]
        public void Snapshot_Hydrate_Long_Roundtrip()
        {
            long ticks = DateTime.UtcNow.Ticks;
            {
                using var eng = new StateHydrationEngine(_path);
                eng.Register("ticks", () => ticks, x => ticks = x);
                eng.Snapshot();
            }
            long restored = 0L;
            using var eng2 = new StateHydrationEngine(_path);
            eng2.Register("ticks", () => restored, x => restored = x);
            Assert.True(eng2.Hydrate());
            Assert.Equal(ticks, restored);
        }

        // ════════════════════════════════════════════════════════════════════
        // 4. 무결성 검증 — 변조 → false
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Hydrate_TamperedData_CrcMismatch_ReturnsFalse()
        {
            {
                using var eng = new StateHydrationEngine(_path);
                int v = 1;
                eng.Register("v", () => v, x => v = x);
                eng.Snapshot();
            }

            // 슬롯 0 데이터 첫 바이트 반전 (Header=32, Key=32, DataLen=4 → offset 68)
            var bytes = File.ReadAllBytes(_path);
            bytes[68] ^= 0xFF;
            File.WriteAllBytes(_path, bytes);

            int restored = 0;
            using var eng2 = new StateHydrationEngine(_path);
            eng2.Register("v", () => restored, x => restored = x);
            Assert.False(eng2.Hydrate());
        }

        [Fact]
        public void Hydrate_TamperedMagic_ReturnsFalse()
        {
            {
                using var eng = new StateHydrationEngine(_path);
                int v = 1;
                eng.Register("v", () => v, x => v = x);
                eng.Snapshot();
            }

            var bytes = File.ReadAllBytes(_path);
            bytes[0] = 0xFF;  // 매직 첫 바이트 손상
            File.WriteAllBytes(_path, bytes);

            int r = 0;
            using var eng2 = new StateHydrationEngine(_path);
            eng2.Register("v", () => r, x => r = x);
            Assert.False(eng2.Hydrate());
        }

        [Fact]
        public void Hydrate_TruncatedFile_ReturnsFalse()
        {
            // Header보다 짧은 파일
            File.WriteAllBytes(_path, new byte[10]);
            using var eng = new StateHydrationEngine(_path);
            int r = 0;
            eng.Register("v", () => r, x => r = x);
            Assert.False(eng.Hydrate());
        }

        // ════════════════════════════════════════════════════════════════════
        // 5. 더티 비트 추적
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void DirtyCount_AfterChange_IncrementsCorrectly()
        {
            int a = 1, b = 2;
            using var eng = new StateHydrationEngine(_path);
            eng.Register("a", () => a, x => a = x);
            eng.Register("b", () => b, x => b = x);
            eng.Snapshot();  // LastBytes 초기화

            a = 99;  // a만 변경
            Assert.Equal(1, eng.DirtyCount());

            b = 77;  // b도 변경
            Assert.Equal(2, eng.DirtyCount());

            eng.Snapshot();  // LastBytes 갱신
            Assert.Equal(0, eng.DirtyCount());  // 다시 클린
        }

        [Fact]
        public void DirtyCount_NoChange_ReturnsZero()
        {
            int v = 5;
            using var eng = new StateHydrationEngine(_path);
            eng.Register("v", () => v, x => v = x);
            eng.Snapshot();
            Assert.Equal(0, eng.DirtyCount());
        }

        // ════════════════════════════════════════════════════════════════════
        // 6. HydrationSlot<T> 라운드트립
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void RegisterSlot_Hydrate_RestoredViaSlot()
        {
            var slot = new HydrationSlot<int>(42);
            {
                using var eng = new StateHydrationEngine(_path);
                eng.RegisterSlot("s", slot);
                eng.Snapshot();
            }

            var slot2 = new HydrationSlot<int>(0);
            using var eng2 = new StateHydrationEngine(_path);
            eng2.RegisterSlot("s", slot2);
            Assert.True(eng2.Hydrate());
            Assert.Equal(42, slot2.Value);
        }

        // ════════════════════════════════════════════════════════════════════
        // 7. 유틸리티
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void HasValidSnapshot_BeforeAndAfterSnapshot()
        {
            using var eng = new StateHydrationEngine(_path);
            Assert.False(eng.HasValidSnapshot);

            int v = 1;
            eng.Register("v", () => v, x => v = x);
            eng.Snapshot();
            Assert.True(eng.HasValidSnapshot);
        }

        [Fact]
        public void ClearSnapshot_DeletesFile()
        {
            using var eng = new StateHydrationEngine(_path);
            int v = 1;
            eng.Register("v", () => v, x => v = x);
            eng.Snapshot();
            Assert.True(File.Exists(_path));
            eng.ClearSnapshot();
            Assert.False(File.Exists(_path));
        }

        [Fact]
        public void Snapshot_CalledTwice_OverwritesPrevious()
        {
            int v = 1;
            using var eng = new StateHydrationEngine(_path);
            eng.Register("v", () => v, x => v = x);
            eng.Snapshot();

            v = 999;
            eng.Snapshot();  // 덮어쓰기

            int restored = 0;
            using var eng2 = new StateHydrationEngine(_path);
            eng2.Register("v", () => restored, x => restored = x);
            Assert.True(eng2.Hydrate());
            Assert.Equal(999, restored);  // 두 번째 스냅샷 값
        }

        [Fact]
        public void Register_KeyTooLong_ThrowsArgumentException()
        {
            using var eng = new StateHydrationEngine(_path);
            int v = 0;
            var longKey = new string('k', 32);  // MaxKeyChars = 31 초과
            Assert.Throws<ArgumentException>(() =>
                eng.Register(longKey, () => v, x => v = x));
        }

        [Fact]
        public void Hydrate_UnknownKeyInFile_IgnoredGracefully()
        {
            // 파일에 "x" 키 저장, 복구 엔진에는 "y" 등록 → 매칭 없음이지만 예외 없음
            {
                using var eng = new StateHydrationEngine(_path);
                int v = 7;
                eng.Register("x", () => v, x => v = x);
                eng.Snapshot();
            }

            int y = 0;
            using var eng2 = new StateHydrationEngine(_path);
            eng2.Register("y", () => y, x => y = x);
            var result = eng2.Hydrate();  // 매칭 없어도 true (파일 자체는 유효)
            Assert.True(result);
            Assert.Equal(0, y);  // 복구 없음 — 기본값 유지
        }
    }
}
