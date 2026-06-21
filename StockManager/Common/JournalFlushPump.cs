// ============================================================================
// JournalFlushPump — S24-b 미션2: 런타임 비동기 플러시 (핫패스 무할당·무블로킹)
//
//  온실 챔피언 경신·StrategySlot 핫스왑 등 핫패스가 디스크 MMF 동기 플러시로 메인 틱 파이프라인에
//  Hiccup 을 유발하지 않도록, 디스크 I/O 책임을 격리된 백그라운드 단일 스레드로 비동기 이관한다.
//
//  ── producer(핫패스) / consumer(백그라운드) 분리 ───────────────────────────
//  · 핫패스는 RequestFlush() 한 줄(Interlocked.Increment(ref long _dirtyGen))만 — 할당 0·블로킹 0·디스크 무접촉.
//  · 백그라운드 단일 스레드(BelowNormal·IsBackground)만 Persist(MMF I/O) 실행 → _scratch 데이터 레이스 0.
//
//  ── lost-update 0 : (A)→(B)→(C) 순서 ───────────────────────────────────────
//  (A) _dirtyGen 먼저 캡처 → (B) 수집·기록 → (C) 성공 시에만 _flushedGen 을 그 캡처값으로 전진.
//  (B)~(C) 사이 도착한 RequestFlush 는 _flushedGen<_dirtyGen 을 유지해 다음 주기에 반드시 재플러시.
//
//  ── 종료 : Join 후 최종 플러시 ─────────────────────────────────────────────
//  Stop 은 백그라운드 합류 '후' 단일 호출자로 FlushIfDirty — 동시 라이터 torn write(부팅 BadCrc 오판) 차단.
//  volatile long 불법 → 모든 long 은 Interlocked.*(ref long). _running 만 volatile bool(합법).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>최신 상태를 JournalRecord 로 dest 에 채워 개수 반환(백그라운드 단일 스레드 호출).</summary>
    public interface IJournalSnapshotProvider
    {
        int CaptureInto(Span<JournalRecord> dest);
    }

    /// <summary>RejectionForensicAuditor 의 거부 레코드를 JournalRecord 로 변환하는 무할당 어댑터.</summary>
    public sealed class ForensicJournalProvider : IJournalSnapshotProvider
    {
        private readonly RejectionForensicAuditor _forensics;
        private readonly RejectionRecord[]        _scratch;   // 재사용(무할당)

        public ForensicJournalProvider(RejectionForensicAuditor forensics, int capacity = 64)
        {
            _forensics = forensics ?? throw new ArgumentNullException(nameof(forensics));
            _scratch   = new RejectionRecord[capacity < 1 ? 1 : capacity];
        }

        public int CaptureInto(Span<JournalRecord> dest)
        {
            int n = _forensics.SnapshotInto(_scratch);
            int k = Math.Min(n, dest.Length);
            for (int i = 0; i < k; i++)
            {
                var r = _scratch[i];
                dest[i] = new JournalRecord(
                    r.Seq, RecoveryKind.Rejection, (byte)r.Stage, promotion: 0,
                    r.AsOfTicks, r.NowTicks, r.WindowTicks, r.Fitness, JournalRecord.HashId(r.SpecId));
            }
            return k;
        }
    }

    public sealed class JournalFlushPump : IDisposable
    {
        private readonly CrashRecoveryJournaler   _journaler;
        private readonly IJournalSnapshotProvider _provider;
        private readonly JournalRecord[]          _scratch;     // 재사용 수집 버퍼(생성 시 1회 할당)
        private readonly int                      _intervalMs;

        private long          _dirtyGen, _flushedGen, _flushes, _flushErrors;
        private volatile bool _running;
        private Thread?       _thread;

        public JournalFlushPump(
            CrashRecoveryJournaler   journaler,
            IJournalSnapshotProvider provider,
            int                      capacity   = 64,
            int                      intervalMs = 1000)
        {
            _journaler  = journaler ?? throw new ArgumentNullException(nameof(journaler));
            _provider   = provider  ?? throw new ArgumentNullException(nameof(provider));
            _scratch    = new JournalRecord[capacity < 1 ? 1 : capacity];
            _intervalMs = intervalMs < 25 ? 25 : intervalMs;
        }

        /// <summary>핫패스 — 단일 Interlocked. 할당 0·블로킹 0·디스크 무접촉.</summary>
        public void RequestFlush() => Interlocked.Increment(ref _dirtyGen);

        public long Flushes     => Interlocked.Read(ref _flushes);
        public long FlushErrors => Interlocked.Read(ref _flushErrors);
        public long DirtyGen    => Interlocked.Read(ref _dirtyGen);   // S24-c 진단 — 다중 소스 누적 요청 수(손실 0 검증)
        public bool Running     => _running;

        /// <summary>동기 진입점(단일 호출자 전제). (A)캡처→(B)수집·기록→(C)성공시 전진. lost-update 0.</summary>
        public bool FlushIfDirty()
        {
            long observed = Interlocked.Read(ref _dirtyGen);                       // (A)
            if (observed == Interlocked.Read(ref _flushedGen)) return false;

            int n;
            try { n = _provider.CaptureInto(_scratch); }
            catch { Interlocked.Increment(ref _flushErrors); return false; }       // 수집 실패 → 미전진

            if (!_journaler.Persist(_scratch.AsSpan(0, n)))                        // (B)
            { Interlocked.Increment(ref _flushErrors); return false; }            // 디스크 실패 → 미전진·재시도

            Interlocked.Exchange(ref _flushedGen, observed);                      // (C) 성공시에만 전진
            Interlocked.Increment(ref _flushes);
            return true;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread  = new Thread(Loop)
            {
                IsBackground = true,
                Priority     = ThreadPriority.BelowNormal,
                Name         = "JournalFlush",
            };
            _thread.Start();
        }

        private void Loop()
        {
            while (_running)
            {
                try { FlushIfDirty(); } catch { /* 격리 — 플러시 예외는 메인에 전파 안 함 */ }
                for (int slept = 0; slept < _intervalMs && _running; slept += 25)
                    Thread.Sleep(Math.Min(25, _intervalMs - slept));
            }
        }

        public void Stop()
        {
            _running = false;
            var t = _thread;
            bool joined = true;
            if (t is not null && t.IsAlive)
            {
                try { joined = t.Join(2000); } catch { joined = false; }          // ★합류 '확인'
            }
            _thread = null;

            // ★ Join 이 타임아웃하면(백그라운드가 느린 Persist I/O 에 블록) 그 스레드가 아직 _buf/_scratch 를
            //   쓰는 중일 수 있다. 이때 최종 FlushIfDirty 를 강행하면 동시 라이터 torn write → 다음 부팅 BadCrc
            //   오판으로 멀쩡한 시스템을 동결시키는 자가당착이 발생한다. 합류가 '확인된' 경우에만 최종 플러시.
            if (joined) { try { FlushIfDirty(); } catch { } }
            else { try { Log.Warn("[플러시펌프] 백그라운드 합류 실패(I/O 블록 추정) — torn write 방지 위해 최종 플러시 생략"); } catch { } }
        }

        public void Dispose() => Stop();
    }
}
