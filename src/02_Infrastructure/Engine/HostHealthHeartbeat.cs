// ============================================================================
// HostHealthHeartbeat — [헬스 · 방향 A+B] 호스트 → 키움 프록시 비관리 고정 레이아웃 하트비트(MMF · seqlock · δ=0)
//
//  방향 A: 호스트(HealthMonitoringAgent)가 스캔마다 PID · QPC 타임스탬프 · Working Set · CPU‰ · 헬스 트립 플래그를 64B 고정 구조로
//          공유 메모리에 사상(홀/짝 시퀀스 seqlock — 판독자는 찢김 없는 사본만 사용).
//  방향 B: 실제 주문이 프로세스를 떠나는 곳은 x86 키움 프록시다. 프록시는 주문 TR 송신 '직전' 이 하트비트를 판독해
//          (a) 맵 없음 (b) 시퀀스 판독 실패 (c) 마지막 박동이 임계(기본 200ms)보다 오래됨 (d) 호스트 헬스 트립 — 이면 그 주문을
//          키움에 보내지 않고 거부 프레임으로 돌려보낸다. 호스트 OS 스레드 동결 동안 파이프에 쌓여 있던 '낡은 판단의 주문'이
//          동결 해제 후 한꺼번에 나가는 것을 송신 지점에서 물리 차단한다(소켓 강제 단절·방화벽 조작 없이 같은 효과 · 관리자 권한 불필요).
//  QPC(Stopwatch.GetTimestamp)는 같은 머신의 모든 프로세스에서 같은 단조 시계다(x64 호스트 ↔ x86 프록시 비교 가능).
//  이 파일은 프록시 csproj 에 소스 링크된다(BridgeProtocol 과 동일 방식 · 외부 의존 0).
// ============================================================================
using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace MS2E.Infrastructure
{
    public struct HostHeartbeatSnapshot
    {
        public int Pid, CpuPermille, Flags;
        public long QpcTicks, QpcFrequency, WorkingSetBytes, Beats;
        public const int FlagHealthTripped = 1;
        public bool HealthTripped => (Flags & FlagHealthTripped) != 0;
    }

    public static class HostHealthHeartbeat
    {
        public const string DefaultMapName = "Local\\MS2E_HOST_HEARTBEAT_V1";
        public const long Magic = 0x3154_4248_4532_534D;    // "MS2EHBT1"
        public const int Size = 64;
        // 오프셋: 0 magic · 8 seq · 16 qpc · 24 freq · 32 ws · 40 beats · 48 pid · 52 cpu‰ · 56 flags
        internal const int OffMagic = 0, OffSeq = 8, OffQpc = 16, OffFreq = 24, OffWs = 32, OffBeats = 40, OffPid = 48, OffCpu = 52, OffFlags = 56;
    }

    /// <summary>호스트 쪽 작성자(단일 라이터).</summary>
    public sealed class HostHeartbeatWriter : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private long _seq, _beats;

        public HostHeartbeatWriter(string mapName = HostHealthHeartbeat.DefaultMapName)
        {
            _mmf = MemoryMappedFile.CreateOrOpen(mapName, HostHealthHeartbeat.Size, MemoryMappedFileAccess.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, HostHealthHeartbeat.Size);
            _view.Write(HostHealthHeartbeat.OffMagic, HostHealthHeartbeat.Magic);
        }

        public long Beats => Interlocked.Read(ref _beats);

        /// <summary>박동 1회(δ=0).</summary>
        public void Beat(long qpcTicks, long qpcFrequency, long workingSetBytes, int cpuPermille, bool healthTripped)
        {
            long s = ++_seq * 2 - 1;                                   // 홀 = 기록 중
            _view.Write(HostHealthHeartbeat.OffSeq, s);
            Interlocked.MemoryBarrier();
            _view.Write(HostHealthHeartbeat.OffQpc, qpcTicks);
            _view.Write(HostHealthHeartbeat.OffFreq, qpcFrequency);
            _view.Write(HostHealthHeartbeat.OffWs, workingSetBytes);
            _view.Write(HostHealthHeartbeat.OffBeats, Interlocked.Increment(ref _beats));
            _view.Write(HostHealthHeartbeat.OffPid, Environment.ProcessId);
            _view.Write(HostHealthHeartbeat.OffCpu, cpuPermille);
            _view.Write(HostHealthHeartbeat.OffFlags, healthTripped ? HostHeartbeatSnapshot.FlagHealthTripped : 0);
            Interlocked.MemoryBarrier();
            _view.Write(HostHealthHeartbeat.OffSeq, s + 1);            // 짝 = 발행
        }

        public void Dispose() { _view.Dispose(); _mmf.Dispose(); }
    }

    /// <summary>
    /// 프록시 쪽 송신 게이트(판독자). AllowOrder = 맵 존재 ∧ 찢김 없는 판독 ∧ 박동 신선(≤ maxAge) ∧ 호스트 헬스 정상. 아니면 거부(fail-closed).
    /// 맵은 호스트가 나중에 기동할 수 있으므로 판독 시점마다 지연 열기를 재시도한다(열린 뒤에는 재사용).
    /// </summary>
    public sealed class HostHeartbeatEgressGate : IDisposable
    {
        private readonly string _mapName;
        private readonly long _maxAgeMicros;
        private readonly Func<long> _nowQpc;
        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _view;
        private long _allowed, _rejectedMissing, _rejectedStale, _rejectedTripped, _rejectedTorn;

        public HostHeartbeatEgressGate(string mapName = HostHealthHeartbeat.DefaultMapName, long maxAgeMicros = 200_000, Func<long>? nowQpc = null)
        {
            if (maxAgeMicros <= 0) throw new ArgumentOutOfRangeException(nameof(maxAgeMicros));
            _mapName = mapName; _maxAgeMicros = maxAgeMicros;
            _nowQpc = nowQpc ?? System.Diagnostics.Stopwatch.GetTimestamp;
        }

        public long Allowed => Interlocked.Read(ref _allowed);
        public long RejectedMissing => Interlocked.Read(ref _rejectedMissing);
        public long RejectedStale => Interlocked.Read(ref _rejectedStale);
        public long RejectedTripped => Interlocked.Read(ref _rejectedTripped);
        public long RejectedTorn => Interlocked.Read(ref _rejectedTorn);
        public long Rejected => RejectedMissing + RejectedStale + RejectedTripped + RejectedTorn;

        public bool TryRead(out HostHeartbeatSnapshot s)
        {
            s = default;
            var v = EnsureView();
            if (v is null || v.ReadInt64(HostHealthHeartbeat.OffMagic) != HostHealthHeartbeat.Magic) return false;
            for (int spin = 0; spin < 32; spin++)
            {
                long a = v.ReadInt64(HostHealthHeartbeat.OffSeq);
                if ((a & 1) != 0 || a == 0) { Thread.SpinWait(8); continue; }
                Interlocked.MemoryBarrier();
                s.QpcTicks = v.ReadInt64(HostHealthHeartbeat.OffQpc);
                s.QpcFrequency = v.ReadInt64(HostHealthHeartbeat.OffFreq);
                s.WorkingSetBytes = v.ReadInt64(HostHealthHeartbeat.OffWs);
                s.Beats = v.ReadInt64(HostHealthHeartbeat.OffBeats);
                s.Pid = v.ReadInt32(HostHealthHeartbeat.OffPid);
                s.CpuPermille = v.ReadInt32(HostHealthHeartbeat.OffCpu);
                s.Flags = v.ReadInt32(HostHealthHeartbeat.OffFlags);
                Interlocked.MemoryBarrier();
                if (v.ReadInt64(HostHealthHeartbeat.OffSeq) == a) return true;
            }
            return false;
        }

        /// <summary>주문 송신 허가(δ=0 · 열린 뒤).</summary>
        public bool AllowOrder()
        {
            if (EnsureView() is null) { Interlocked.Increment(ref _rejectedMissing); return false; }
            if (!TryRead(out var s) || s.QpcFrequency <= 0) { Interlocked.Increment(ref _rejectedTorn); return false; }
            if (s.HealthTripped) { Interlocked.Increment(ref _rejectedTripped); return false; }
            long age = (long)((Int128)(_nowQpc() - s.QpcTicks) * 1_000_000 / s.QpcFrequency);
            if (age > _maxAgeMicros || age < -_maxAgeMicros) { Interlocked.Increment(ref _rejectedStale); return false; }
            Interlocked.Increment(ref _allowed);
            return true;
        }

        private MemoryMappedViewAccessor? EnsureView()
        {
            var v = Volatile.Read(ref _view);
            if (v is not null) return v;
            try
            {
                var m = MemoryMappedFile.OpenExisting(_mapName, MemoryMappedFileRights.ReadWrite);
                var nv = m.CreateViewAccessor(0, HostHealthHeartbeat.Size);
                _mmf = m;
                Volatile.Write(ref _view, nv);
                return nv;
            }
            catch (FileNotFoundException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public void Dispose() { _view?.Dispose(); _mmf?.Dispose(); }
    }
}
