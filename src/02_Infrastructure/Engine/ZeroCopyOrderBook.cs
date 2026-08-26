// ============================================================================
// ZeroCopyOrderBook — v8.0.0 Phase 8 Task 2: L3 제로카피 오더북 어셈블리(Flat Struct LOB)
//
//  클래스 객체 생성을 철저히 배제한 고정 크기 평면(flat) 지정가 호가창(LOB). 40개 long(매수 10호가 가격/잔량 +
//  매도 10호가 가격/잔량)을 64B 정렬 NativeMemory 에 상주시켜, 갱신마다 힙 할당 없이 in-place 로 변형한다.
//  ReadOnlySpan<byte> 이진 L3 메시지(Add/Modify/Delete)를 직접 파싱해 인-플레이스 갱신하고, 갱신된 사다리를
//  ReadOnlySpan<long> 로 즉시(무복사) 노출한다 → Phase 9 엔진 및 저널로 직결.
//
//  ── L3 메시지(20B) ────────────────────────────────────────────────────────
//   [0]=type(0 Set/1 Modify/2 Delete) [1]=side(0 Bid/1 Ask) [2]=level(0..9) [3]=flags
//   [4..12)=price i64 LE  [12..20)=qty i64 LE
//
//  ★불변 제약: δ=0 갱신·조회 핫패스(네이티브 포인터 in-place·스팬 무복사 — new/box/LINQ 0). 결정론(순수 파싱).
//    NativeMemory 상주라 스팬이 GC 이동에 무관(안정) — 프리한 뒤 접근 금지. ★unsafe 범위: 평면 LOB 가 본질.
//    liveArmed:false(순수 조립·관측). 기존 불변 회로 무접촉(신규 파일).
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>고정 크기 평면 L3 오더북(NativeMemory 상주·in-place 갱신·스팬 무복사 노출). IDisposable.</summary>
    public sealed unsafe class ZeroCopyOrderBook : IDisposable
    {
        public const int Levels  = 10;
        private const int Slots  = Levels * 4;   // [bidPx|bidQty|askPx|askQty] × 10
        private const int MsgLen = 20;
        private const int OffBidPx = 0, OffBidQty = Levels, OffAskPx = Levels * 2, OffAskQty = Levels * 3;

        private long* _book;
        private long  _version;
        private int   _disposed;

        public ZeroCopyOrderBook()
        {
            _book = (long*)NativeMemory.AlignedAlloc((nuint)(Slots * sizeof(long)), 64);
            NativeMemory.Clear(_book, (nuint)(Slots * sizeof(long)));
        }

        /// <summary>갱신 세대(스팬 캐시 무효화·핑거프린트 힌트). δ=0.</summary>
        public long Version => Volatile.Read(ref _version);

        /// <summary>이진 L3 메시지(Add/Modify/Delete)를 직접 파싱해 in-place 갱신(δ=0). 형식 불량이면 false.</summary>
        public bool Apply(ReadOnlySpan<byte> msg)
        {
            if (msg.Length < MsgLen || _book == null) return false;
            byte type = msg[0], side = msg[1], level = msg[2];
            if (type > 2 || side > 1 || level >= Levels) return false;

            long price = BinaryPrimitives.ReadInt64LittleEndian(msg.Slice(4));
            long qty   = BinaryPrimitives.ReadInt64LittleEndian(msg.Slice(12));
            int pxOff  = (side == 0 ? OffBidPx : OffAskPx) + level;
            int qtyOff = (side == 0 ? OffBidQty : OffAskQty) + level;

            switch (type)
            {
                case 0:   // Set(Add): 가격·잔량 확정
                    _book[pxOff] = price; _book[qtyOff] = qty; break;
                case 1:   // Modify: 잔량 갱신(가격은 nonzero 일 때만)
                    _book[qtyOff] = qty; if (price != 0) _book[pxOff] = price; break;
                default:  // Delete(2): 호가 소거
                    _book[pxOff] = 0; _book[qtyOff] = 0; break;
            }
            Volatile.Write(ref _version, _version + 1);
            return true;
        }

        /// <summary>직접 호가 세팅(테스트/시드·δ=0).</summary>
        public bool Set(int side, int level, long price, long qty)
        {
            if ((uint)side > 1 || (uint)level >= Levels || _book == null) return false;
            _book[(side == 0 ? OffBidPx : OffAskPx) + level]  = price;
            _book[(side == 0 ? OffBidQty : OffAskQty) + level] = qty;
            Volatile.Write(ref _version, _version + 1);
            return true;
        }

        // ── 사다리 무복사 노출(ReadOnlySpan<long>) — NativeMemory 상주라 GC 이동 무관 ──
        public ReadOnlySpan<long> GetBidPriceSpan() => _book == null ? default : new ReadOnlySpan<long>(_book + OffBidPx,  Levels);
        public ReadOnlySpan<long> GetBidQtySpan()   => _book == null ? default : new ReadOnlySpan<long>(_book + OffBidQty, Levels);
        public ReadOnlySpan<long> GetAskPriceSpan() => _book == null ? default : new ReadOnlySpan<long>(_book + OffAskPx,  Levels);
        public ReadOnlySpan<long> GetAskQtySpan()   => _book == null ? default : new ReadOnlySpan<long>(_book + OffAskQty, Levels);

        /// <summary>호가창 전체 소거(레짐 리셋). δ=0.</summary>
        public void Clear()
        {
            if (_book != null) NativeMemory.Clear(_book, (nuint)(Slots * sizeof(long)));
            Volatile.Write(ref _version, _version + 1);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            long* b = _book; _book = null;
            if (b != null) NativeMemory.AlignedFree(b);
        }
    }
}
