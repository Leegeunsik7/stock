// ============================================================================
// ZombieAssemblyShield — v5.3.0 ★Think Beyond Gemini: 좀비 어셈블리 방어 & 세대 펜스
//
//  ── 아키텍처 논증(제미나이 초월) ────────────────────────────────────────────
//  제미나이 힌트 A(좀비 릭 어테스테이션)와 B(과도기 대조기)는 각각 '언로드 실패 감지'와 '신/구 출력 대조'를 다룬다. 본 설계는
//  동적 언로드의 두 파멸 리스크(영구 메모리 누수 = 좀비 어셈블리, 교체 찰나 진입 패킷의 고아화 = orphan event)를 ★단일
//  메커니즘 '세대 펜스(generation fence) + WeakReference 수거 어테스테이션'으로 동시 방어한다:
//   ① 세대 펜스: 핫스왑마다 세대를 전진(Advance). 구 ALC(좀비)의 누출된 이벤트/콜백이 뒤늦게 발화해도 자신의 세대가 현
//      세대와 불일치하면 AcceptEvent 가 거부(δ=0 long 비교) — 고아 이벤트가 신버전 전략을 오염시키지 못함.
//   ② WeakReference 어테스테이션: 구 ALC Unload() 후 WeakReference.IsAlive 를 grace 창(기본 5s) 롤링 확인. 창 초과에도
//      살아있으면(이벤트 구독/스레드/정적 참조 누출로 수거 실패) '좀비'로 격리 계수 + IgnitionLock.Downgrade(참조 클램프·후퇴).
//  ▷ 초월점: A/B 를 따로 짜지 않고, 세대 토큰 하나로 '고아 이벤트 거부(즉시)'와 '좀비 누수 어테스테이션(롤링)'을 통합.
//    수거 가능 ALC 는 참조 1개만 남아도 언로드 실패하므로, 세대 펜스가 그 '살아있는 참조가 발화하는 이벤트'까지 무해화한다.
//
//  δ=0: AcceptEvent(핫)는 Volatile long 비교 1회(힙 0). RegisterUnload/SweepOnce 는 콜드(리로드·5s 롤링). GC 강제는 옵션·콜드.
//  결정론: 세대는 단조 정수. 수거 확인 시각은 주입 시계. GC.Collect 는 δ=0 핫패스 무간섭(콜드 스레드에서만).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>세대 펜스(고아 이벤트 거부) + WeakReference 수거 어테스테이션(좀비 누수 방어) 통합 CLR 방어막.</summary>
    public sealed class ZombieAssemblyShield
    {
        private sealed class Pending { public long Gen; public long RegisterTicks; public Func<bool> IsAlive = static () => false; public bool Active; }

        private readonly IgnitionLock?      _ignition;
        private readonly MmfFlightRecorder? _blackbox;
        private readonly Func<long>         _nowTicks;
        private readonly long               _graceTicks;      // 수거 확인 grace(기본 5s)
        private readonly object             _gate = new();
        private readonly Pending[]          _pending;         // 고정 슬롯(리로드 저빈도·언로드 추적)

        private long _currentGeneration;
        private long _orphanRejected, _zombies, _collected;

        public ZombieAssemblyShield(IgnitionLock? ignition = null, Func<long>? nowTicks = null, long tickFrequency = 0,
                                    double graceSeconds = 5.0, int capacity = 16, MmfFlightRecorder? blackbox = null)
        {
            _ignition = ignition; _blackbox = blackbox;
            _nowTicks = nowTicks ?? System.Diagnostics.Stopwatch.GetTimestamp;
            long freq = tickFrequency > 0 ? tickFrequency : System.Diagnostics.Stopwatch.Frequency;
            _graceTicks = (long)((graceSeconds <= 0 ? 5.0 : graceSeconds) * freq);
            int cap = capacity < 4 ? 4 : capacity;
            _pending = new Pending[cap];
            for (int i = 0; i < cap; i++) _pending[i] = new Pending();
        }

        public long CurrentGeneration  => Interlocked.Read(ref _currentGeneration);
        public long OrphanEventsRejected => Interlocked.Read(ref _orphanRejected);
        public long ZombieCount         => Interlocked.Read(ref _zombies);
        public long CollectedCount      => Interlocked.Read(ref _collected);
        public int  PendingUnloads      { get { lock (_gate) { int c = 0; foreach (var p in _pending) if (p.Active) c++; return c; } } }

        /// <summary>핫스왑 시 세대 전진. 이후 구 세대 이벤트는 고아로 거부됨.</summary>
        public void Advance(long newGeneration) => Interlocked.Exchange(ref _currentGeneration, newGeneration);

        /// <summary>★세대 펜스(δ=0·핫) — 이벤트의 세대가 현 세대와 일치하면 수용(true), 구 세대(좀비 누출)면 거부(false·orphan 계수).</summary>
        public bool AcceptEvent(long eventGeneration)
        {
            if (eventGeneration == Interlocked.Read(ref _currentGeneration)) return true;
            Interlocked.Increment(ref _orphanRejected);
            return false;
        }

        /// <summary>구 ALC 언로드 등록 — grace 창 내 수거(IsAlive=false) 확인 대상. isAlive 는 WeakReference.IsAlive 프로브.</summary>
        public void RegisterUnload(long generation, Func<bool> isAlive)
        {
            if (isAlive is null) return;
            lock (_gate)
            {
                if (TryPlace(generation, isAlive)) return;
                // ★적대검증(high) 교정 — 슬롯 만원. 무조건 좀비 격발하면 이미 수거된 구 ALC 만 쌓여도 허위 Downgrade 가 오발된다
                //   (스윕 펌프와 등록의 타이밍 경합). 격발 전에 이미 수거된(IsAlive==false) 슬롯을 인라인 회수(콜드·GC 무유발)하고 재시도.
                for (int i = 0; i < _pending.Length; i++)
                {
                    var p = _pending[i];
                    if (!p.Active) continue;
                    bool alive; try { alive = p.IsAlive(); } catch { alive = true; }
                    if (!alive) { p.Active = false; Interlocked.Increment(ref _collected); }
                }
                if (TryPlace(generation, isAlive)) return;
                // 회수 후에도 만원 = 16개 ALC 가 실제로 동시 생존(진짜 누수 폭주) — 보수적 좀비 격발.
            }
            EscalateZombie(generation, ageTicks: _graceTicks + 1);
        }

        /// <summary>빈 슬롯에 등록(호출자가 _gate 보유). 성공 시 true.</summary>
        private bool TryPlace(long generation, Func<bool> isAlive)
        {
            for (int i = 0; i < _pending.Length; i++)
                if (!_pending[i].Active)
                { _pending[i].Gen = generation; _pending[i].RegisterTicks = _nowTicks(); _pending[i].IsAlive = isAlive; _pending[i].Active = true; return true; }
            return false;
        }

        /// <summary>
        /// 주기 스윕(콜드) — grace 초과 등록 건의 수거 여부 확인. driveGc=true면 GC.Collect+대기(콜드 전용)로 수거 유도.
        /// 여전히 살아있으면(수거 실패) 좀비 격리 + IgnitionLock.Downgrade, 수거됐으면 등록 해제.
        /// </summary>
        public void SweepOnce(bool driveGc = false)
        {
            // ★적대검증 교정 — 활성 등록이 없으면 즉시 반환. 유휴 프로세스에서 driveGc 펌프가 매 틱 GC 를 강제해 지터를 유발하는 것 방지.
            bool anyActive = false;
            lock (_gate) { for (int i = 0; i < _pending.Length; i++) if (_pending[i].Active) { anyActive = true; break; } }
            if (!anyActive) return;
            if (driveGc)
            {
                // ★콜드 경로 전용 — δ=0 핫패스와 격리. 수거 가능 ALC 배출 유도(대기 등록이 있을 때만 도달).
                try { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); } catch { }
            }
            long now = _nowTicks();
            lock (_gate)
            {
                for (int i = 0; i < _pending.Length; i++)
                {
                    var p = _pending[i];
                    if (!p.Active) continue;
                    bool alive;
                    try { alive = p.IsAlive(); } catch { alive = true; }   // 프로브 실패 = 보수적으로 살아있다고 간주
                    if (!alive) { p.Active = false; Interlocked.Increment(ref _collected); continue; }   // 정상 수거
                    if (now - p.RegisterTicks > _graceTicks)
                    {
                        p.Active = false;   // 추적 종료(격리 계수 후 반복 격발 방지)
                        EscalateZombie(p.Gen, now - p.RegisterTicks);
                    }
                }
            }
        }

        private void EscalateZombie(long gen, long ageTicks)
        {
            Interlocked.Increment(ref _zombies);
            try { _blackbox?.Record(FlightEventKind.ZombieAssembly, 0L, gen, ageTicks, 0L, 81); } catch { }
            try { _ignition?.Downgrade($"ZombieAssemblyShield: 구 전략 ALC(세대 {gen}) 수거 실패 — 좀비 잔존(참조 누출)·참조 클램프·후퇴"); } catch { }
        }
    }
}
