// ============================================================================
// ProcessRolloverGuard — [롤오버 · 추천 A] 신·구 프로세스 간 미체결 추적 슬롯 승계(Shared MMF · 체크섬 봉인 · HandoffAck)
//
//  프로토콜(같은 사용자 세션의 두 호스트 프로세스):
//   ① 구 프로세스 Publish(exec):
//      · 송신했으나 브로커 번호를 아직 못 받은(Admitted) 주문이 있으면 승계 거부(NotQuiescent) — 번호 없는 주문은 새 프로세스가
//        어떤 체결 통보와도 짝지을 수 없다(조용히 넘기면 '추적 중인 것처럼 보이는 미추적 노출').
//      · 슬롯을 MMF 에 쓰고 SHA-256(epoch ‖ count ‖ 슬롯 바이트) 봉인 → 상태 Published.
//   ② 새 프로세스 TryAdopt(exec):
//      · 매직/버전/상태/건수/체크섬 검증 → 슬롯 전부 가져오기(하나라도 실패 = 불일치) → 상태 Acked + ack epoch/체크섬 기록.
//      · 불일치(체크섬·건수·중복 번호·포화) = sticky Rollover 비트(신규 발주 차단) — 부분 승계 상태로 운용하지 않는다.
//      · 승계 성공이어도 '브로커 대사 완료(MarkReconciled)' 전까지 Rollover 비트 유지: 승계 슬롯은 구 프로세스의 기억일 뿐,
//        그 사이 체결/취소가 있었을 수 있다(구 프록시와 함께 키움 주문번호 사상표도 사라진다 — 아래 한계).
//   ③ 구 프로세스 WaitForAck(timeout): 같은 epoch·체크섬의 Ack 를 확인해야만 true(= 안전 종료 허가). 시간 초과 = false →
//      구 프로세스는 종료하지 말고 Disarm 상태로 운용자 판단 대기(Fail-Closed). Abort() 로 승계 철회 가능(새 프로세스 Adopt 거부).
//  ★한계(정직):
//   · 이 저장소의 키움 프록시는 호스트 1개 연결만 받고 연결이 끊기면 종료한다 — 키움 주문번호↔클라이언트 ID 사상표가 프록시와 함께
//     사라져 새 프록시는 구 주문의 체결 통보를 사상하지 못한다(UnknownOrder). 따라서 승계 슬롯의 진실은 브로커 미체결 조회
//     (OPT10075 등 · 미구현)로만 확정되며, 그 전까지 Rollover 비트가 신규 발주를 막는다(= 무중단 '발주'가 아니라 무중단 '가시성').
//   · 프로덕션 호스트는 브리지 연결 호출부가 없어(KiwoomBridgeClient.ConnectAsync 0) 현재 실 추적 슬롯이 생기지 않는다.
//  δ=0: 슬롯 직렬화는 사전 할당 버퍼 + 뷰 접근자(ReadArray/WriteArray) · SHA-256 정적 Span 오버로드 · 상태 전이는 Interlocked 의미의 순서 기록.
// ============================================================================
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using MS2E.Core;
using StockManager.Adapters;

namespace StockManager.Services
{
    public enum RolloverPublishResult : byte { Published = 0, NotQuiescent = 1, CapacityExceeded = 2 }
    public enum RolloverAdoptResult : byte { NoHandoff = 0, Adopted = 1, ChecksumMismatch = 2, ImportFailed = 3, FormatInvalid = 4, Aborted = 5 }

    public sealed class ProcessRolloverGuard : IArmingBlockSource, IDisposable
    {
        public const string DefaultMapName = "Local\\MS2E_ROLLOVER_V1";
        private const long Magic = 0x3156_4F4C_4C4F_524D;   // "MROLLOV1"
        private const int Version = 1;
        private const int StateEmpty = 0, StateWriting = 1, StatePublished = 2, StateAcked = 3, StateAborted = 4;
        // 헤더 오프셋(128B)
        private const int OffMagic = 0, OffVersion = 8, OffState = 12, OffEpoch = 16, OffOldPid = 24, OffNewPid = 28, OffCount = 32,
                          OffAckEpoch = 40, OffChecksum = 48, OffAckChecksum = 80, HeaderSize = 128;

        private readonly int _capacity, _slotSize;
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly HandoffSlot[] _slots;
        private readonly byte[] _slotBytes;
        private readonly byte[] _hashInput;
        private readonly byte[] _checksum = new byte[32], _scratch32 = new byte[32];
        private ArmingInterlockLink? _link;
        private long _epoch;
        private int _mismatch, _adoptedCount, _reconciled = 1, _disposed;

        /// <param name="createIfMissing">구 프로세스(작성자) = true · 새 프로세스(판독자)는 false 로 열고 없으면 NoHandoff.</param>
        private ProcessRolloverGuard(MemoryMappedFile mmf, int capacity)
        {
            _capacity = capacity;
            _slotSize = Marshal.SizeOf<HandoffSlot>();
            _mmf = mmf;
            _view = mmf.CreateViewAccessor(0, HeaderSize + (long)capacity * _slotSize);
            _slots = new HandoffSlot[capacity];
            _slotBytes = new byte[capacity * _slotSize];
            _hashInput = new byte[16 + capacity * _slotSize];
        }

        /// <summary>구 프로세스(승계 작성자) — 맵 생성/열기.</summary>
        public static ProcessRolloverGuard CreatePublisher(string mapName = DefaultMapName, int capacity = 1024)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            long size = HeaderSize + (long)capacity * Marshal.SizeOf<HandoffSlot>();
            return new ProcessRolloverGuard(MemoryMappedFile.CreateOrOpen(mapName, size, MemoryMappedFileAccess.ReadWrite), capacity);
        }

        /// <summary>새 프로세스(승계 판독자) — 맵이 없으면 null(= 승계 없음 · 신규 기동).</summary>
        public static ProcessRolloverGuard? OpenAdopter(string mapName = DefaultMapName, int capacity = 1024)
        {
            try { return new ProcessRolloverGuard(MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.ReadWrite), capacity); }
            catch (FileNotFoundException) { return null; }
        }

        public bool Mismatch => Volatile.Read(ref _mismatch) == 1;
        public int AdoptedCount => Volatile.Read(ref _adoptedCount);
        public bool Reconciled => Volatile.Read(ref _reconciled) == 1;
        /// <summary>신규 발주 차단 = 불일치(sticky) ∨ 승계 슬롯 존재 ∧ 브로커 대사 미완료.</summary>
        public bool ArmingBlocked => Mismatch || (AdoptedCount > 0 && !Reconciled);
        public long Epoch => Interlocked.Read(ref _epoch);

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        // ── ① 구 프로세스 ──

        public RolloverPublishResult Publish(OrderExecutionFeedAdapter exec, long epoch)
        {
            int n = exec.ExportSlots(_slots);
            if (n == -2) return RolloverPublishResult.NotQuiescent;
            if (n < 0) return RolloverPublishResult.CapacityExceeded;
            _view.Write(OffState, StateWriting);                      // 판독자는 Writing 상태를 승계 대상으로 보지 않는다
            Interlocked.MemoryBarrier();
            _view.WriteArray(HeaderSize, _slots, 0, n);
            _view.Write(OffMagic, Magic);
            _view.Write(OffVersion, Version);
            _view.Write(OffEpoch, epoch);
            _view.Write(OffOldPid, Environment.ProcessId);
            _view.Write(OffNewPid, 0);
            _view.Write(OffCount, n);
            _view.Write(OffAckEpoch, 0L);
            ComputeChecksum(epoch, n, _checksum);
            _view.WriteArray(OffChecksum, _checksum, 0, 32);
            Interlocked.MemoryBarrier();
            _view.Write(OffState, StatePublished);                    // 발행(release)
            Interlocked.Exchange(ref _epoch, epoch);
            return RolloverPublishResult.Published;
        }

        /// <summary>새 프로세스의 Ack(같은 epoch · 같은 체크섬) 확인 — true 여야만 구 프로세스 안전 종료(δ=0 판독 · 폴링).</summary>
        public bool WaitForAck(TimeSpan timeout)
        {
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            do
            {
                if (IsAcked()) return true;
                Thread.Sleep(5);
            }
            while (Environment.TickCount64 < deadline);
            return IsAcked();
        }

        public bool IsAcked()
        {
            if (_view.ReadInt32(OffState) != StateAcked || _view.ReadInt64(OffAckEpoch) != Interlocked.Read(ref _epoch)) return false;
            _view.ReadArray(OffAckChecksum, _scratch32, 0, 32);
            return CryptographicOperations.FixedTimeEquals(_scratch32, _checksum);
        }

        /// <summary>승계 철회(Ack 전) — 새 프로세스의 Adopt 를 거부시킨다.</summary>
        public void Abort() => _view.Write(OffState, StateAborted);

        // ── ② 새 프로세스 ──

        public RolloverAdoptResult TryAdopt(OrderExecutionFeedAdapter exec)
        {
            int state = _view.ReadInt32(OffState);
            if (state == StateEmpty) return RolloverAdoptResult.NoHandoff;
            if (state == StateAborted) return RolloverAdoptResult.Aborted;
            if (state != StatePublished || _view.ReadInt64(OffMagic) != Magic || _view.ReadInt32(OffVersion) != Version)
                return Fail(RolloverAdoptResult.FormatInvalid);
            long epoch = _view.ReadInt64(OffEpoch);
            int n = _view.ReadInt32(OffCount);
            if (n < 0 || n > _capacity) return Fail(RolloverAdoptResult.FormatInvalid);
            _view.ReadArray(HeaderSize, _slots, 0, n);
            ComputeChecksum(epoch, n, _checksum);
            _view.ReadArray(OffChecksum, _scratch32, 0, 32);
            if (!CryptographicOperations.FixedTimeEquals(_scratch32, _checksum)) return Fail(RolloverAdoptResult.ChecksumMismatch);
            for (int i = 0; i < n; i++)
                if (!exec.ImportSlot(in _slots[i])) return Fail(RolloverAdoptResult.ImportFailed);   // 부분 승계 운용 금지(sticky)
            Interlocked.Exchange(ref _epoch, epoch);
            Volatile.Write(ref _adoptedCount, n);
            Volatile.Write(ref _reconciled, n == 0 ? 1 : 0);
            _view.Write(OffNewPid, Environment.ProcessId);
            _view.WriteArray(OffAckChecksum, _checksum, 0, 32);
            _view.Write(OffAckEpoch, epoch);
            Interlocked.MemoryBarrier();
            _view.Write(OffState, StateAcked);
            Volatile.Read(ref _link)?.Resync();
            return RolloverAdoptResult.Adopted;
        }

        /// <summary>브로커 미체결/잔고 대사로 승계 슬롯이 확정됐음을 표시(운용자/대사기) — Rollover 비트 해제(불일치는 해제 불가).</summary>
        public void MarkReconciled()
        {
            Volatile.Write(ref _reconciled, 1);
            Volatile.Read(ref _link)?.Resync();
        }

        // ── 내부 ──

        private RolloverAdoptResult Fail(RolloverAdoptResult r)
        {
            Volatile.Write(ref _mismatch, 1);
            Volatile.Read(ref _link)?.Resync();
            return r;
        }

        // SHA-256(epoch 8B ‖ count 8B ‖ 슬롯 n × 80B) — 사전 할당 버퍼 · 정적 Span 오버로드(δ=0)
        private void ComputeChecksum(long epoch, int n, byte[] dst)
        {
            int len = n * _slotSize;
            MemoryMarshal.AsBytes(_slots.AsSpan(0, n)).CopyTo(_slotBytes);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(_hashInput, epoch);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(_hashInput.AsSpan(8), n);
            _slotBytes.AsSpan(0, len).CopyTo(_hashInput.AsSpan(16));
            SHA256.HashData(_hashInput.AsSpan(0, 16 + len), dst);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _view.Dispose();
            _mmf.Dispose();
        }
    }
}
