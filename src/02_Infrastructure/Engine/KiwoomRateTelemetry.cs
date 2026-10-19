// ============================================================================
// KiwoomRateTelemetry — [v12.18.22-DEV] x86 키움 프록시 → 64비트 호스트 레이트 게이트 관제 채널(MMF · seqlock · 64B 고정 레이아웃 · δ=0)
//
//  방향: HostHealthHeartbeat(호스트 → 프록시)의 역방향. 키움 TR 레이트 게이트(LockFreeTokenBucket × 2 + BrokerBackoffController)는
//  프록시 프로세스 안에 있다 — 호스트 대시보드는 이 맵의 값 사본만 판독한다(게이트 · 송신 경로 무간섭 · 읽기 전용 · 주문 경로 0).
//
//  레이아웃(64B · little-endian · x86/x64 동일): 0 magic · 8 seq(홀 = 기록 중) · 16 qpc(기록 시각 · OS QPC = 프로세스 간 공통 단조) ·
//    24 freq · 32 거부 누계 · 40 백오프 남은 µs · 48 주문 1초 잔여 i16 · 50 주문 60초 잔여 i16 · 52 조회 1초 잔여 i16 · 54 조회 60초 잔여 i16 ·
//    56 연속 초과 i16 · 58 플래그 i16(1 = 래치 · 2 = 게이트 결선) · 60 pid.
//  작성자(프록시): 게이트 판정 · OCX 응답 기록마다 + 50ms 펌프 박동마다 Publish. 다중 호출 스레드(STA 이벤트 · 파이프 송신)는
//    in-process CAS 소유권으로 단일 작성자화 — 점유 중 호출은 '더티'만 세우고 즉시 반환, 소유자가 해제 전에 다시 게시(대기 0 · 유실 0).
//  판독자(호스트): 지연 열기(맵 부재 = 1초 간격 재시도 · 콜드) · 시퀀스 판독 32회 상한(블로킹 0) · 찢김/부재/노후(> 2초)를 상태로 분류.
//  ★프록시 비정상 종료: 호스트가 뷰를 잡고 있으면 맵은 마지막 값으로 남는다 → 박동 시각이 멈춰 '노후'로 판정(가짜 '정상' 0).
//    기록 도중 종료(seq 홀수 고착) = 찢김 판정 · 무한 대기 0 · 예외 전파 0.
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>판독 사본(값 구조체).</summary>
    public struct KiwoomRateTelemetrySnapshot
    {
        public long Sequence, QpcTicks, QpcFrequency, Rejected, BackoffMicros;
        public int OrderRemaining1s, OrderRemaining60s, QueryRemaining1s, QueryRemaining60s, Consecutive, Flags, Pid;
        public const int FlagLatched = 1, FlagGateAttached = 2;
        public bool Latched => (Flags & FlagLatched) != 0;
        public bool GateAttached => (Flags & FlagGateAttached) != 0;

        /// <summary>게이트 상태 → 게시 값(δ=0). 순번 · 시각 · pid 는 작성자가 채운다.</summary>
        public static void FromGates(ref KiwoomRateTelemetrySnapshot s, LockFreeTokenBucket? order, LockFreeTokenBucket? query, BrokerBackoffController? backoff, long rejected)
        {
            s.Flags = (order is not null || query is not null ? FlagGateAttached : 0) | (backoff is { Latched: true } ? FlagLatched : 0);
            s.Rejected = rejected;
            s.BackoffMicros = backoff?.RemainingBackoffMicros() ?? 0;
            s.OrderRemaining1s = order?.RemainingInWindow(0) ?? 0;
            s.OrderRemaining60s = order?.RemainingInWindow(1) ?? 0;
            s.QueryRemaining1s = query?.RemainingInWindow(0) ?? 0;
            s.QueryRemaining60s = query?.RemainingInWindow(1) ?? 0;
            s.Consecutive = (int)Math.Min(int.MaxValue, backoff?.Consecutive ?? 0);
        }
    }

    /// <summary>게시 원천 — 소유 스레드가 게시 회차마다 '지금' 값을 채운다(재게시 시 최신 반영 · δ=0).</summary>
    public interface IKiwoomRateTelemetrySource
    {
        void FillRateTelemetry(ref KiwoomRateTelemetrySnapshot s);
    }

    /// <summary>판독 결과 분류.</summary>
    public enum KiwoomRateTelemetryRead : byte { Missing = 0, Torn = 1, Stale = 2, Live = 3 }

    public static class KiwoomRateTelemetry
    {
        public const string DefaultMapName = "Local\\MS2E_KIWOOM_RATE_V1";
        public const long Magic = 0x3154_4152_4B32_534D;                  // "MS2KRAT1"
        public const int Size = 64;
        internal const int OffMagic = 0, OffSeq = 8, OffQpc = 16, OffFreq = 24, OffRejected = 32, OffBackoff = 40,
                           OffOrd1 = 48, OffOrd60 = 50, OffQry1 = 52, OffQry60 = 54, OffConsec = 56, OffFlags = 58, OffPid = 60;
        public const long DefaultStaleMicros = 2_000_000;

        internal static short Clamp16(long v) => (short)(v < 0 ? 0 : v > short.MaxValue ? short.MaxValue : v);
    }

    /// <summary>프록시 측 작성자(δ=0 · 단일 작성자화 CAS · 대기 0).</summary>
    public sealed class KiwoomRateTelemetryWriter : IDisposable
    {
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly Func<long> _nowQpc;
        private readonly int _pid;
        private long _seq, _publishes;
        private int _owner, _dirty, _disposed;

        public KiwoomRateTelemetryWriter(string mapName = KiwoomRateTelemetry.DefaultMapName, Func<long>? nowQpc = null)
        {
            _mmf = MemoryMappedFile.CreateOrOpen(mapName, KiwoomRateTelemetry.Size, MemoryMappedFileAccess.ReadWrite);
            _view = _mmf.CreateViewAccessor(0, KiwoomRateTelemetry.Size);
            _nowQpc = nowQpc ?? Stopwatch.GetTimestamp;
            _pid = Environment.ProcessId;
            _seq = _view.ReadInt64(KiwoomRateTelemetry.OffSeq) & ~1L;      // 재기동 = 기존 시퀀스 이어 쓰기(판독자 단조 유지)
            _view.Write(KiwoomRateTelemetry.OffMagic, KiwoomRateTelemetry.Magic);
        }

        public long Publishes => Interlocked.Read(ref _publishes);

        /// <summary>게시(δ=0). 다른 스레드가 기록 중이면 더티만 세우고 반환 — 소유자가 해제 전 원천을 다시 읽어 최신 값으로 재게시.</summary>
        public void Publish(IKiwoomRateTelemetrySource source)
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            Volatile.Write(ref _dirty, 1);
            while (Volatile.Read(ref _dirty) == 1)
            {
                if (Interlocked.CompareExchange(ref _owner, 1, 0) != 0) return;   // 소유자가 더티를 보고 재게시
                try
                {
                    while (Interlocked.Exchange(ref _dirty, 0) == 1)
                    {
                        if (Volatile.Read(ref _disposed) == 1) return;
                        var snap = default(KiwoomRateTelemetrySnapshot);
                        source.FillRateTelemetry(ref snap);
                        Write(in snap);
                    }
                }
                finally { Volatile.Write(ref _owner, 0); }
            }
        }

        private void Write(in KiwoomRateTelemetrySnapshot v)
        {
            long s = _seq + 1;                                               // 홀 = 기록 중
            _view.Write(KiwoomRateTelemetry.OffSeq, s);
            Interlocked.MemoryBarrier();
            _view.Write(KiwoomRateTelemetry.OffQpc, _nowQpc());
            _view.Write(KiwoomRateTelemetry.OffFreq, Stopwatch.Frequency);
            _view.Write(KiwoomRateTelemetry.OffRejected, v.Rejected);
            _view.Write(KiwoomRateTelemetry.OffBackoff, v.BackoffMicros);
            _view.Write(KiwoomRateTelemetry.OffOrd1, KiwoomRateTelemetry.Clamp16(v.OrderRemaining1s));
            _view.Write(KiwoomRateTelemetry.OffOrd60, KiwoomRateTelemetry.Clamp16(v.OrderRemaining60s));
            _view.Write(KiwoomRateTelemetry.OffQry1, KiwoomRateTelemetry.Clamp16(v.QueryRemaining1s));
            _view.Write(KiwoomRateTelemetry.OffQry60, KiwoomRateTelemetry.Clamp16(v.QueryRemaining60s));
            _view.Write(KiwoomRateTelemetry.OffConsec, KiwoomRateTelemetry.Clamp16(v.Consecutive));
            _view.Write(KiwoomRateTelemetry.OffFlags, (short)v.Flags);
            _view.Write(KiwoomRateTelemetry.OffPid, _pid);
            Interlocked.MemoryBarrier();
            _view.Write(KiwoomRateTelemetry.OffSeq, s + 1);                  // 짝 = 발행
            _seq = s + 1;
            Interlocked.Increment(ref _publishes);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            var w = new SpinWait();
            while (Interlocked.CompareExchange(ref _owner, 1, 0) != 0) w.SpinOnce();   // 진행 중 기록 종료 대기(유한 · 소유자는 블로킹 없음)
            _view.Dispose(); _mmf.Dispose();
        }
    }

    /// <summary>호스트 측 판독자(δ=0 · 블로킹 0 · 예외 전파 0). 단일 판독 스레드(허브 펌프) 전제.</summary>
    public sealed class KiwoomRateTelemetryReader : IDisposable
    {
        private const int Spins = 32;
        private readonly string _mapName;
        private readonly Func<long> _nowQpc;
        private readonly long _staleTicks, _retryTicks;
        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _view;
        private long _nextOpenTry = long.MinValue, _openAttempts, _torn, _stale, _live;
        private int _disposed;

        public KiwoomRateTelemetryReader(string mapName = KiwoomRateTelemetry.DefaultMapName, long staleMicros = KiwoomRateTelemetry.DefaultStaleMicros,
                                         Func<long>? nowQpc = null, long reopenIntervalMicros = 1_000_000)
        {
            if (staleMicros <= 0) throw new ArgumentOutOfRangeException(nameof(staleMicros));
            _mapName = mapName;
            _nowQpc = nowQpc ?? Stopwatch.GetTimestamp;
            _staleTicks = staleMicros * Stopwatch.Frequency / 1_000_000;
            _retryTicks = Math.Max(1, reopenIntervalMicros) * Stopwatch.Frequency / 1_000_000;
        }

        public long OpenAttempts => Interlocked.Read(ref _openAttempts);
        public long TornReads => Interlocked.Read(ref _torn);
        public long StaleReads => Interlocked.Read(ref _stale);
        public long LiveReads => Interlocked.Read(ref _live);

        /// <summary>판독 1회. Live 외 결과도 s 에 마지막으로 읽힌 값(노후)이 들어올 수 있다 — 표시측은 결과로 분기.</summary>
        public KiwoomRateTelemetryRead TryRead(out KiwoomRateTelemetrySnapshot s)
        {
            s = default;
            try { return ReadCore(ref s); }
            catch (ObjectDisposedException) { s = default; return KiwoomRateTelemetryRead.Missing; }   // 종료 중 펌프 ↔ Dispose 경합 = 부재(예외 전파 0)
        }

        private KiwoomRateTelemetryRead ReadCore(ref KiwoomRateTelemetrySnapshot s)
        {
            var v = EnsureView();
            if (v is null || v.ReadInt64(KiwoomRateTelemetry.OffMagic) != KiwoomRateTelemetry.Magic) return KiwoomRateTelemetryRead.Missing;
            for (int spin = 0; spin < Spins; spin++)
            {
                long a = v.ReadInt64(KiwoomRateTelemetry.OffSeq);
                if (a == 0) return KiwoomRateTelemetryRead.Missing;                // 맵만 생성 · 첫 게시 전
                if ((a & 1) != 0) { Thread.SpinWait(8); continue; }
                Interlocked.MemoryBarrier();
                s.Sequence = a;
                s.QpcTicks = v.ReadInt64(KiwoomRateTelemetry.OffQpc);
                s.QpcFrequency = v.ReadInt64(KiwoomRateTelemetry.OffFreq);
                s.Rejected = v.ReadInt64(KiwoomRateTelemetry.OffRejected);
                s.BackoffMicros = v.ReadInt64(KiwoomRateTelemetry.OffBackoff);
                s.OrderRemaining1s = v.ReadInt16(KiwoomRateTelemetry.OffOrd1);
                s.OrderRemaining60s = v.ReadInt16(KiwoomRateTelemetry.OffOrd60);
                s.QueryRemaining1s = v.ReadInt16(KiwoomRateTelemetry.OffQry1);
                s.QueryRemaining60s = v.ReadInt16(KiwoomRateTelemetry.OffQry60);
                s.Consecutive = v.ReadInt16(KiwoomRateTelemetry.OffConsec);
                s.Flags = v.ReadInt16(KiwoomRateTelemetry.OffFlags);
                s.Pid = v.ReadInt32(KiwoomRateTelemetry.OffPid);
                Interlocked.MemoryBarrier();
                if (v.ReadInt64(KiwoomRateTelemetry.OffSeq) != a) continue;
                long age = _nowQpc() - s.QpcTicks;
                if (age > _staleTicks || age < -_staleTicks) { Interlocked.Increment(ref _stale); return KiwoomRateTelemetryRead.Stale; }
                Interlocked.Increment(ref _live);
                return KiwoomRateTelemetryRead.Live;
            }
            s = default;
            Interlocked.Increment(ref _torn);
            return KiwoomRateTelemetryRead.Torn;
        }

        // 지연 열기 — 부재 시 재시도는 reopenInterval 간격(예외 생성 = 콜드 · 펌프 회차마다 던지지 않음).
        private MemoryMappedViewAccessor? EnsureView()
        {
            var v = _view;
            if (v is not null || Volatile.Read(ref _disposed) == 1) return v;
            long now = _nowQpc();
            if (now < _nextOpenTry) return null;
            _nextOpenTry = now + _retryTicks;
            Interlocked.Increment(ref _openAttempts);
            try
            {
                var m = MemoryMappedFile.OpenExisting(_mapName, MemoryMappedFileRights.Read);
                try { _view = m.CreateViewAccessor(0, KiwoomRateTelemetry.Size, MemoryMappedFileAccess.Read); }
                catch { m.Dispose(); throw; }
                _mmf = m;
                return _view;
            }
            catch (FileNotFoundException) { return null; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _view?.Dispose(); _mmf?.Dispose();
            _view = null; _mmf = null;
        }
    }
}
