// ============================================================================
// BlackBoxDumpTriggerBridge — v12.5.0 Black-Box Flight Recorder 트리거 결속(Section 1 §2)
//
//   ★기존 CrashResilientDumper(v4.95.0)는 OrderAttestationService.RegisterFreezeObserver(ForceFreeze)로만
//   격발되도록 이미 배선되어 있었다(실측 확인) — Fatal Exception·Hardware Kill-Switch·OOM 임박이라는 v12.5.0
//   이 요구하는 3개 트리거 경로는 결속되어 있지 않았다. 본 브리지는 CrashResilientDumper 를 재구현하지 않고,
//   그 공개 멱등 API(DumpOnce — CAS 로 1회만 실행)에 3개 신규 트리거를 순수 배선(fan-in)한다.
//
//   불변: liveArmed:false(트리거·덤프 모두 관측/영속화 — 발주 경로 무접촉) · fail-closed(어느 트리거든 최초 1회만
//        실제 덤프 수행 — DumpOnce 자체의 CAS 멱등성에 위임, 본 파일은 중복 방지 로직을 따로 두지 않는다).
// ============================================================================
using System;

namespace MS2E.Infrastructure
{
    /// <summary>Fatal Exception/Hardware Kill-Switch/OOM 임박을 CrashResilientDumper.DumpOnce() 로 팬인.</summary>
    public sealed class BlackBoxDumpTriggerBridge
    {
        private readonly CrashResilientDumper _dumper;

        public BlackBoxDumpTriggerBridge(CrashResilientDumper dumper)
            => _dumper = dumper ?? throw new ArgumentNullException(nameof(dumper));

        /// <summary>하드웨어 킬스위치 Killed 이벤트에 구독 — 격발 즉시 블랙박스 덤프.</summary>
        public void WireKillSwitch(MS2E.Core.IHardwareKillSwitchPort killSwitch)
        {
            if (killSwitch is null) throw new ArgumentNullException(nameof(killSwitch));
            killSwitch.Killed += _ => _dumper.DumpOnce();
        }

        /// <summary>Fatal(미처리) 예외 발생 시 호출(예: AppDomain.CurrentDomain.UnhandledException 핸들러에서 결선).</summary>
        public void OnFatalException(Exception ex) => _dumper.DumpOnce();

        /// <summary>OOM 임박(LiveSoakSentinel.NoteOomImminent 등)이 격발되면 호출.</summary>
        public void OnOomImminent() => _dumper.DumpOnce();

        /// <summary>LiveSoakSentinel 의 임의 브리치(Live-Soak 서킷 브레이커 격발) 발생 시 호출 — Section 2 요구사항
        /// ("즉각 0ms 원자 Disarm 및 서킷 브레이커를 격발")과 Section 1 의 블랙박스 덤프를 한 지점에서 결선.</summary>
        public void OnLiveSoakBreach(LiveSoakBreachSource source) => _dumper.DumpOnce();
    }
}
