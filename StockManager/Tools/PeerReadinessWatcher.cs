// ============================================================================
// PeerReadinessWatcher — [피어 대기 · 방향 B] QuantumVault 준비 상태 전이 경보(MMF 블랙박스 · 이중 저장소 런북 트리거 · 비파괴)
//
//  PeerReadinessProbe(1회 판정)를 주기 관측으로 감싼다. 폴링마다(콜드 · git 판독 = --no-optional-locks · 피어 쓰기 0):
//   ① 판정 묶음(Ready 비트 · 사유 비트마스크 · HEAD 앞 15 hex)이 직전과 다르면 PeerReadinessTransition(102) 블랙박스 기록
//      arg0 = 직전 묶음 · arg1 = 새 묶음 · arg2 = HEAD 앞 60비트(15 hex) · symbol = 폴링 순번.
//   ② NotReady → Ready 는 '같은 HEAD 로 연속 ConfirmPolls(기본 2)회 Ready' 일 때만 확정 — 피어 세션이 커밋/재봉인을 연달아 하는 도중의
//      순간 Ready 로 트리거가 튀지 않게(디바운스). 확정 시 onReady(보고서 · 런북 문자열) 1회 · RunbookTriggers 계수.
//      Ready 상태에서 HEAD 가 바뀌면(피어 재커밋) 확정 해제 → 다시 연속 확인 후 재트리거.
//  ★트리거 = '알림'이다 — 출하 스크립트를 스스로 실행하지 않는다. 이중 저장소 봉인 · QuantumVault 태그는 외부 반영 행위라 사용자 승인 몫
//    (QuantumVault 읽기 전용 원칙 · 출하/태그는 명시 지시 시에만).
//  ★호스트 런타임에 결선하지 않는다(피어 독립 · Zero Peer Contamination) — 운용 CLI(--peer-watch)와 테스트에서만 구동.
//  정직: 지시서의 '0.1ns 단위 진단'은 불가(단조 시계 해상도 ≈100ns · 폴링 주기는 초 단위가 적절 — git 호출 자체가 수십 ms).
// ============================================================================
using System;
using System.Threading;
using MS2E.Infrastructure;

namespace StockManager.Tools
{
    public sealed class PeerReadinessWatcher
    {
        public const int SourceCode = 0x5052;                                      // 'PR'
        public const int DefaultConfirmPolls = 2;
        public const string Runbook =
            "이중 저장소 정식 봉인 런북(HANDOVER §47.3): ① StockManager 새 버전 커밋 · 태그(봉인된 단독 출하물 덮어쓰기 금지) " +
            "② QuantumVault 태그 = 피어 세션 또는 사용자 승인 시에만 ③ 태그 기준 형제 작업 트리(safe.directory = GIT_CONFIG_* env)에서 " +
            "release-package.ps1 (-NoPeer 없이) → [0/10] 피어 고정 · [10/10] 핸드셰이크 PeerPin.";

        private readonly string _peerRoot, _manifestRel;
        private readonly MmfFlightRecorder? _blackbox;
        private readonly Action<PeerReadinessReport, string>? _onReady;
        private readonly Func<string, string, PeerReadinessReport> _probe;
        private readonly int _confirmPolls;
        private long _lastPacked = -1, _polls, _transitions, _triggers;
        private string? _readyHead;
        private int _readyStreak;
        private bool _confirmed;

        /// <param name="probe">판정기(기본 PeerReadinessProbe.Probe · 테스트 주입).</param>
        public PeerReadinessWatcher(string peerRoot, MmfFlightRecorder? blackbox = null, Action<PeerReadinessReport, string>? onReady = null,
                                    int confirmPolls = DefaultConfirmPolls, string manifestRel = PeerReadinessProbe.DefaultManifest,
                                    Func<string, string, PeerReadinessReport>? probe = null)
        {
            _peerRoot = peerRoot ?? throw new ArgumentNullException(nameof(peerRoot));
            if (confirmPolls < 1) throw new ArgumentOutOfRangeException(nameof(confirmPolls));
            _blackbox = blackbox; _onReady = onReady; _confirmPolls = confirmPolls; _manifestRel = manifestRel;
            _probe = probe ?? PeerReadinessProbe.Probe;
        }

        public long Polls => Interlocked.Read(ref _polls);
        public long Transitions => Interlocked.Read(ref _transitions);
        public long RunbookTriggers => Interlocked.Read(ref _triggers);
        public bool ConfirmedReady => Volatile.Read(ref _confirmed);
        public PeerReadinessReport? Last { get; private set; }

        /// <summary>판정 묶음: [0] Ready · [8..15] 사유 비트(PeerReadinessIssue 값 위치) · [16..] HEAD 앞 12 hex(48비트).</summary>
        public static long Pack(PeerReadinessReport r)
        {
            long v = r.Ready ? 1 : 0;
            foreach (var (issue, _) in r.Issues) v |= 1L << (8 + (int)issue);
            return v | (HeadPrefix(r.Head, 12) << 16);
        }

        private static long HeadPrefix(string? head, int hexDigits)
        {
            if (head is null || head.Length < hexDigits) return 0;
            long v = 0;
            for (int i = 0; i < hexDigits; i++)
            {
                int d = Convert.ToInt32(head[i].ToString(), 16);
                v = (v << 4) | (long)d;
            }
            return v;
        }

        /// <summary>1회 관측(콜드). 반환 = 이번 폴링에서 런북 트리거가 확정됐는가.</summary>
        public bool Poll()
        {
            long seq = Interlocked.Increment(ref _polls);
            var r = _probe(_peerRoot, _manifestRel);
            Last = r;
            long packed = Pack(r);
            long prev = Interlocked.Exchange(ref _lastPacked, packed);
            if (packed != prev)
            {
                Interlocked.Increment(ref _transitions);
                _blackbox?.Record(FlightEventKind.PeerReadinessTransition, seq, prev, packed, HeadPrefix(r.Head, 15), SourceCode);
            }
            if (!r.Ready)
            {
                _readyStreak = 0; _readyHead = null; Volatile.Write(ref _confirmed, false);
                return false;
            }
            if (!string.Equals(r.Head, _readyHead, StringComparison.OrdinalIgnoreCase))
            {
                _readyHead = r.Head; _readyStreak = 0; Volatile.Write(ref _confirmed, false);   // 새 HEAD = 처음부터 다시 확인
            }
            _readyStreak++;
            if (Volatile.Read(ref _confirmed) || _readyStreak < _confirmPolls) return false;
            Volatile.Write(ref _confirmed, true);
            Interlocked.Increment(ref _triggers);
            _blackbox?.Record(FlightEventKind.PeerReadinessTransition, seq, packed, packed | (1L << 1), HeadPrefix(r.Head, 15), SourceCode);   // [1] = 런북 트리거 확정
            try { _onReady?.Invoke(r, Runbook); } catch (Exception) { }
            return true;
        }
    }
}
