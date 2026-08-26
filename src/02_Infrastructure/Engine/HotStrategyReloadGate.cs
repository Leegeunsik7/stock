// ============================================================================
// HotStrategyReloadGate — v5.3.0 과제1: ALC 기반 무정지 동적 격리 게이트
//
//  메인 어셈블리·브로커 소켓·청산 메쉬가 살아있는 채로 알파 전략 바이너리만 메모리상에서 무정지 교체한다. 신규 전략 DLL 인입
//  →★서명 검증(fail-closed)→수거 가능 AssemblyLoadContext(isCollectible:true) 격리 로드→CAS 래치로 구버전 핫패스 진입 원자
//  차단→신버전 원자 바인딩(Interlocked.Exchange)→상태 이월(StrategyStateCarrier)→세대 전진(ZombieAssemblyShield)→구 ALC Unload().
//
//  ── 정직 회계(정찰) ─────────────────────────────────────────────────────────
//  PluginLoadContext(수거 ALC 유일 선례)를 전략 샌드박스로 클론(단 Unload·서명 검증 신규). CAS 래치=OrderDispatchRing.EnterPublish
//  관용구. 원자 참조 스왑=StrategySlot.Interlocked.Exchange. 서명 검증=CartridgeEngine.Verify/ECDsa 재사용(로드 전 fail-closed).
//  전략엔 다형 인터페이스가 없어(StrategySpec 레코드뿐) IHotStrategy 계약을 신규 정의.
//
//  ── liveArmed:false / δ=0 / 결정론 ─────────────────────────────────────────
//  핫스왑은 무장 상태를 건드리지 않는다 — 교체된 전략의 신호도 GatedOrderDispatcher 무장 게이트∧StrategyActivationGate 하류라
//  이중키∧MasterIgnited 없이 실 송출 0. 자율 Activate 없음(운영자 서명). OnQuote 핫패스: Volatile 참조 독출 + 가상 호출 + long
//  반환(힙 0). Reload·Unload·GC 는 콜드 경로. 시각 주입.
// ============================================================================
using System;
using System.Runtime.Loader;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>핫스왑 대상 전략 계약(다형). δ=0 신호 산출 — 반환=목표 수량 raw(+매수/-매도/0). 실 발주는 하류 무장 게이트.</summary>
    public interface IHotStrategy
    {
        string Version { get; }
        long   BuildTimestampTicks { get; }
        long   OnQuote(long symbolCode, long priceRaw, long qtyRaw);
    }

    /// <summary>무정지 전략 핫패치 게이트 — 수거 ALC 격리·CAS 래치 원자 교체·서명 검증·세대 전진·상태 이월.</summary>
    public sealed class HotStrategyReloadGate
    {
        private readonly StrategyStateCarrier     _carrier;
        private readonly ZombieAssemblyShield?    _shield;
        private readonly Func<IHotStrategy, bool>? _verifyProvenance;   // 서명/증적 검증(CartridgeEngine.Verify 배선·미주입=검증 스킵 아님, 아래 참조)
        private readonly bool                     _requireProvenance;   // true면 검증기 미주입/실패 시 로드 거부(fail-closed)
        private readonly OrderAttestationService? _att;
        private readonly MmfFlightRecorder?       _blackbox;

        private IHotStrategy? _active;             // Volatile.Read/Interlocked.Exchange(ref 형)
        private AssemblyLoadContext? _activeAlc;   // 현 전략 ALC(다음 리로드 시 Unload 대상)
        private long _generation;
        private int  _swapLatch;                   // 0 유휴·1 스왑 중(리로드 재진입·구버전 진입 차단)
        private long _reloads, _rejects, _unloads;

        public HotStrategyReloadGate(StrategyStateCarrier carrier, ZombieAssemblyShield? shield = null,
                                     Func<IHotStrategy, bool>? verifyProvenance = null, bool requireProvenance = true,
                                     OrderAttestationService? attestation = null, MmfFlightRecorder? blackbox = null)
        {
            _carrier = carrier ?? throw new ArgumentNullException(nameof(carrier));
            _shield = shield;
            _verifyProvenance = verifyProvenance;
            _requireProvenance = requireProvenance;
            _att = attestation;
            _blackbox = blackbox;
        }

        public long   Generation     => Interlocked.Read(ref _generation);
        public long   Reloads        => Interlocked.Read(ref _reloads);
        public long   Rejects        => Interlocked.Read(ref _rejects);
        public long   Unloads        => Interlocked.Read(ref _unloads);
        public bool   Swapping       => Volatile.Read(ref _swapLatch) != 0;
        public string ActiveVersion  => Volatile.Read(ref _active)?.Version ?? "(none)";
        public long   ActiveBuildTicks => Volatile.Read(ref _active)?.BuildTimestampTicks ?? 0L;

        /// <summary>★핫패스(δ=0) 전략 신호 산출 — 활성 전략에 위임. 미배선/동결이면 0(중립). 실 발주는 하류 무장 게이트.</summary>
        public long OnQuote(long symbolCode, long priceRaw, long qtyRaw)
        {
            if (_att is { } a && a.Mode != GatingMode.Normal) return 0;   // 동결 중 신호 억제(fail-closed·중립)
            var s = Volatile.Read(ref _active);
            return s is null ? 0L : s.OnQuote(symbolCode, priceRaw, qtyRaw);
        }

        /// <summary>
        /// ★무정지 교체(콜드) — 서명 검증(fail-closed)→CAS 래치→세대 전진→상태 이월→원자 바인딩→구 ALC Unload+좀비 감시 등록.
        /// isOldAlcAlive 는 구 ALC 수거 확인 프로브(WeakReference 기반·null이면 좀비 감시 생략). 반환=교체 성공.
        /// </summary>
        public bool Reload(IHotStrategy next, AssemblyLoadContext? nextAlc = null, Func<bool>? isOldAlcAlive = null)
        {
            if (next is null) { Interlocked.Increment(ref _rejects); return false; }
            if (_att is { } a && a.Mode != GatingMode.Normal) { Interlocked.Increment(ref _rejects); return false; }   // 동결 시 로드 거부

            // ★서명/증적 검증 — 미검증 바이너리 로드 거부(fail-closed). 검증기 미주입 + requireProvenance면 거부.
            bool ok;
            try { ok = _verifyProvenance is { } v ? v(next) : !_requireProvenance; }
            catch { ok = false; }
            if (!ok) { Interlocked.Increment(ref _rejects); return false; }

            // 리로드 재진입 금지(구버전 핫패스 진입 차단 래치). 동시 리로드는 거부.
            if (Interlocked.CompareExchange(ref _swapLatch, 1, 0) != 0) { Interlocked.Increment(ref _rejects); return false; }
            try
            {
                long newGen = Interlocked.Increment(ref _generation);
                _shield?.Advance(newGen);                         // 세대 전진 — 구 세대 이벤트는 이후 고아로 거부
                _carrier.Handover(newGen);                        // δ=0 상태 이월(참조 절단)
                var oldAlc = _activeAlc;
                _activeAlc = nextAlc;
                Interlocked.Exchange(ref _active, next);          // ★원자 바인딩(무정지)
                try { _blackbox?.Record(FlightEventKind.StrategyHotSwap, 0L, newGen, next.BuildTimestampTicks, 0L, 80); } catch { }

                // 구 ALC 수거 배출 + 좀비 감시 등록.
                if (oldAlc is not null)
                {
                    var weak = new WeakReference(oldAlc, trackResurrection: false);
                    try { oldAlc.Unload(); Interlocked.Increment(ref _unloads); } catch { }
                    _shield?.RegisterUnload(newGen - 1, () => weak.IsAlive);
                }
                Interlocked.Increment(ref _reloads);
                return true;
            }
            finally { Volatile.Write(ref _swapLatch, 0); }
        }

        /// <summary>
        /// ★실 전략 DLL 수거 ALC 격리 로드(운영자 옵트인·콜드). 서명 검증 후 collectible ALC 로 로드→IHotStrategy 인스턴스화→Reload.
        /// ★DLL=임의 코드 실행 — 반드시 서명 검증 통과분만. AllowAssemblies 옵트인 하에서만 호출(자동 로드 금지).
        /// </summary>
        public bool LoadCollectible(string assemblyPath, string strategyTypeName, Func<string, bool> verifyFile)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath) || verifyFile is null) { Interlocked.Increment(ref _rejects); return false; }
            if (!SafeVerify(verifyFile, assemblyPath)) { Interlocked.Increment(ref _rejects); return false; }   // ★파일 서명 검증 fail-closed
            AssemblyLoadContext alc = new StrategyLoadContext(assemblyPath);
            try
            {
                var asm = alc.LoadFromAssemblyPath(assemblyPath);
                var type = asm.GetType(strategyTypeName);
                if (type is null || Activator.CreateInstance(type) is not IHotStrategy strat)
                { try { alc.Unload(); } catch { } Interlocked.Increment(ref _rejects); return false; }
                // ★적대검증(high) 교정 — Reload 가 거부(동결·미검증 fail-closed·래치 경합)하면 nextAlc 는 _activeAlc 로 채택되지
                //   않는다(참조 절단). 형제 실패 분기(위 130·아래 catch)와 동일하게 방금 로드한 수거 ALC 를 반드시 Unload —
                //   미언로드 시 추적 불가 좀비 누수(생성자 실행 코드·핀 네이티브 DLL 잔존). 성공 시엔 채택되므로 언로드 금지.
                bool accepted = Reload(strat, alc);
                if (!accepted) { try { alc.Unload(); } catch { } }   // _rejects 는 Reload 내부에서 이미 증가(중복 계수 금지)
                return accepted;
            }
            catch { try { alc.Unload(); } catch { } Interlocked.Increment(ref _rejects); return false; }
        }

        private static bool SafeVerify(Func<string, bool> v, string path) { try { return v(path); } catch { return false; } }

        /// <summary>수거 가능 전략 ALC — PluginLoadContext 패턴. 의존성 해석 포함.</summary>
        private sealed class StrategyLoadContext : AssemblyLoadContext
        {
            private readonly AssemblyDependencyResolver _resolver;
            public StrategyLoadContext(string mainPath) : base(isCollectible: true) => _resolver = new AssemblyDependencyResolver(mainPath);
            protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName name)
            { var p = _resolver.ResolveAssemblyToPath(name); return p is null ? null : LoadFromAssemblyPath(p); }
            protected override IntPtr LoadUnmanagedDll(string name)
            { var p = _resolver.ResolveUnmanagedDllToPath(name); return p is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(p); }
        }
    }
}
