// ============================================================================
// OnboardingHealthSentinel — v11.4.1 Think Beyond Gemini
//
//   ★ 제미나이의 두 아이디어(A: 환경 온보딩 헬스체크 · B: UI 렌더 거버너)를 '하나의 응집 엔진'으로 초월한다.
//     이 센티널은 두 리스크 표면을 동시에 봉인한다:
//       (1) 신규 개발자의 환경 구성 오류 — 런타임 하한·데이터 디렉터리 쓰기·DPAPI(at-rest 암호화)·x86 COM 프록시
//       (2) ★새로 이관된 컴포넌트의 런타임 리스크 — Phase 94-x 로 RCL 이주된 페이지의 Port 어댑터가 DI 에 '배선'
//           되지 않으면 컴파일은 통과해도 그 페이지가 런타임에 화이트스크린(미해결 @inject)으로 죽는다. 정적 분석/
//           컴파일로는 절대 못 잡는 이 결함을 부팅 시 미리 검출한다.
//     그리고 세 번째, 최상위 불변을 물리 단언한다:
//       (3) ★liveArmed:false — 부팅 시점(물리 이중 키 점화 前)에는 어떤 발주 무장도 존재하면 안 된다. 온보딩
//           환경에 무장 흔적이 있으면 '안전 거부'한다(안전한 기본값을 증명하지 못하면 진행 불가 — fail-closed 반전).
//
//   ── 아키텍처: '콜드 조립기 + δ=0 결정론 커널'(CanaryHealthSentinel 의 Arm/TryAuthorize 이원 구조 계승) ──
//     • Diagnose(probe)  : 부팅(콜드) 경로. probe 에서 신호를 모아 고정 크기 스팬으로 조립 → 커널 호출.
//     • Evaluate(span)   : ★순수 결정론 커널. 시계/난수/힙할당 0(δ=0). fail-closed 집계. 유닛 테스트의 표적.
//     probe(IOnboardingProbe)로 환경 I/O 를 추상화 → 테스트가 결정론적 값을 주입(실제 I/O 없이 재현 가능).
//
//   불변: 결정론(커널은 순수 함수) · δ=0(Evaluate/Diagnose 는 stackalloc + 스팬 폴드, 힙 0) · fail-closed(빈
//        입력/미상 = Blocked · Blocking 미통과 1건이라도 = Blocked) · liveArmed:false(순수 진단 — 발주/소켓 0) ·
//        fail-safe(부팅 배선부는 진단 예외를 삼켜 부팅 자체를 깨지 않는다). ※핫패스 아님(부팅 콜드 스캔).
// ============================================================================
using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace MS2E.Infrastructure
{
    /// <summary>단일 온보딩 점검의 심각도.</summary>
    public enum OnboardingSeverity { Ok = 0, Advisory = 1, Blocking = 2 }

    /// <summary>종합 준비도 판정(fail-closed 집계 결과).</summary>
    public enum OnboardingReadiness { Ready = 0, Degraded = 1, Blocked = 2 }

    /// <summary>점검 식별자 — 결정론적 리포트 + 해결 힌트 조회 키.</summary>
    public enum OnboardingCheckId
    {
        None = 0,
        DotnetRuntime,          // 실행 런타임 하한(net8+)
        DataDirectoryWritable,  // LocalApplicationData 쓰기 가능(설정/DB 영속)
        DpapiAvailable,         // DPAPI 가용(API 키 at-rest 암호화 — 평문 저장 금지 불변)
        KiwoomProxyPresent,     // x86 COM 프록시 바이너리(키움 실연동 시 필요)
        LiveArmingSafePosture,  // ★부팅 시 미무장(liveArmed:false 안전 자세)
        MigratedPortBindings,   // ★이관 컴포넌트의 Port 어댑터 DI 배선(미배선 → 런타임 화이트스크린)
    }

    /// <summary>단일 점검 결과. 필드가 모두 enum/bool(unmanaged) → stackalloc 가능(δ=0 커널 입력).</summary>
    public readonly record struct OnboardingCheck(OnboardingCheckId Id, OnboardingSeverity Severity, bool Passed);

    /// <summary>온보딩 준비도 리포트.</summary>
    public readonly record struct OnboardingReport(
        OnboardingReadiness Readiness, int Total, int Failed, int BlockingFailed, OnboardingCheckId FirstBlocker);

    /// <summary>
    /// 환경 신호 추상화 — 실제 I/O 를 격리해 커널을 결정론적으로 유지한다. 게터는 '이미 계산된' 값을 반환(무할당)해야
    /// Diagnose 가 δ=0 이 된다(콜드 I/O 는 구현체 생성자에서 1회 수행).
    /// </summary>
    public interface IOnboardingProbe
    {
        bool RuntimeMeetsMinimum { get; }   // net8 이상
        bool DataDirectoryWritable { get; } // 데이터 영역 쓰기 왕복 성공
        bool DpapiAvailable { get; }        // DPAPI(at-rest) 사용 가능
        bool KiwoomProxyPresent { get; }    // x86 프록시 바이너리 존재
        bool LiveArmingSafeAtBoot { get; }  // ★부팅 시 미무장(true=안전)
        bool MigratedPortsBound { get; }    // ★대표 이관 Port 전부 DI 등록됨
    }

    public sealed class OnboardingHealthSentinel
    {
        /// <summary>부팅 시 조립되는 고정 점검 개수(스팬 크기).</summary>
        public const int CheckCount = 6;

        /// <summary>가장 최근 Run 의 리포트(진단/헬스 표시용). Run 전에는 fail-closed 로 Blocked.</summary>
        public OnboardingReport LastReport { get; private set; } =
            new(OnboardingReadiness.Blocked, 0, 0, 0, OnboardingCheckId.None);

        public bool HasRun { get; private set; }

        /// <summary>부팅 진단 실행 — probe 로부터 점검을 조립·평가하고 결과를 캐시한다(콜드 경로).</summary>
        public OnboardingReport Run(IOnboardingProbe probe)
        {
            var r = Diagnose(probe);
            LastReport = r;
            HasRun = true;
            return r;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  δ=0 순수 결정론 커널 — 시계/난수/힙할당 0. fail-closed 집계.
        // ══════════════════════════════════════════════════════════════════════
        /// <summary>
        /// 점검 스팬을 폴드해 준비도를 판정한다. ★무할당(δ=0)·결정론.
        /// fail-closed 규칙:
        ///   • 빈 입력 → Blocked(미상 = 불안전).
        ///   • Id==None(오조립) → Blocking 실패로 취급.
        ///   • Blocking 미통과 ≥1 → Blocked. 아니면 Advisory 미통과 ≥1 → Degraded. 그 외 Ready.
        ///   • FirstBlocker = 최초로 발견된 Blocking 미통과 점검(스캔 순서 — 결정론적).
        /// </summary>
        public static OnboardingReport Evaluate(ReadOnlySpan<OnboardingCheck> checks)
        {
            if (checks.Length == 0)
                return new(OnboardingReadiness.Blocked, 0, 0, 0, OnboardingCheckId.None);

            int failed = 0, blockingFailed = 0, advisoryFailed = 0;
            var firstBlocker = OnboardingCheckId.None;

            for (int i = 0; i < checks.Length; i++)
            {
                ref readonly var c = ref checks[i];

                // 오조립(Id 미지정)은 결함으로 간주(fail-closed) — 침묵 통과 금지.
                bool malformed = c.Id == OnboardingCheckId.None;
                bool ok = c.Passed && !malformed;
                if (ok) continue;

                failed++;
                bool blocking = malformed || c.Severity == OnboardingSeverity.Blocking;
                if (blocking)
                {
                    blockingFailed++;
                    if (firstBlocker == OnboardingCheckId.None) firstBlocker = c.Id;
                }
                else if (c.Severity == OnboardingSeverity.Advisory)
                {
                    advisoryFailed++;
                }
            }

            var readiness = blockingFailed > 0 ? OnboardingReadiness.Blocked
                          : advisoryFailed > 0 ? OnboardingReadiness.Degraded
                          : OnboardingReadiness.Ready;

            return new(readiness, checks.Length, failed, blockingFailed, firstBlocker);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  콜드 조립기 — probe 신호를 고정 스팬으로 조립(stackalloc, δ=0) 후 커널 호출.
        // ══════════════════════════════════════════════════════════════════════
        /// <summary>probe 신호로 점검 스팬을 조립하고 Evaluate 한다. probe 게터가 무할당이면 이 메서드도 δ=0.</summary>
        public static OnboardingReport Diagnose(IOnboardingProbe probe)
        {
            if (probe is null)   // fail-closed
                return new(OnboardingReadiness.Blocked, 0, 0, 0, OnboardingCheckId.None);

            Span<OnboardingCheck> buf = stackalloc OnboardingCheck[CheckCount];
            buf[0] = new(OnboardingCheckId.DotnetRuntime,         OnboardingSeverity.Blocking, probe.RuntimeMeetsMinimum);
            buf[1] = new(OnboardingCheckId.DataDirectoryWritable, OnboardingSeverity.Blocking, probe.DataDirectoryWritable);
            buf[2] = new(OnboardingCheckId.DpapiAvailable,        OnboardingSeverity.Blocking, probe.DpapiAvailable);
            buf[3] = new(OnboardingCheckId.KiwoomProxyPresent,    OnboardingSeverity.Advisory, probe.KiwoomProxyPresent);
            buf[4] = new(OnboardingCheckId.LiveArmingSafePosture, OnboardingSeverity.Blocking, probe.LiveArmingSafeAtBoot);
            buf[5] = new(OnboardingCheckId.MigratedPortBindings,  OnboardingSeverity.Blocking, probe.MigratedPortsBound);
            return Evaluate(buf);
        }

        /// <summary>점검별 구체적 해결 힌트(신규 개발자 온보딩용). 결정론적 정적 매핑.</summary>
        public static string FixHint(OnboardingCheckId id) => id switch
        {
            OnboardingCheckId.DotnetRuntime =>
                ".NET 8 SDK/런타임을 설치하세요(현재 런타임이 하한 미만). `dotnet --info` 로 버전 확인.",
            OnboardingCheckId.DataDirectoryWritable =>
                "%LOCALAPPDATA%\\MS2E 데이터 디렉터리에 쓰기 권한이 없습니다. 폴더 권한/디스크 공간을 확인하세요.",
            OnboardingCheckId.DpapiAvailable =>
                "DPAPI 를 사용할 수 없습니다(비 Windows 또는 프로파일 손상). API 키 at-rest 암호화 불변이 깨집니다 — Windows 사용자 프로파일에서 실행하세요.",
            OnboardingCheckId.KiwoomProxyPresent =>
                "x86 KiwoomProxy 바이너리가 없습니다. 키움 실연동이 필요하면 StockManager.KiwoomProxy 를 x86 로 빌드하세요(모의/토스만 쓰면 무시 가능).",
            OnboardingCheckId.LiveArmingSafePosture =>
                "★부팅 시 발주 무장 흔적이 감지되었습니다. liveArmed:false 불변 위반 — 물리 이중 키를 제거하고 카나리 런북 절차로만 무장하세요.",
            OnboardingCheckId.MigratedPortBindings =>
                "★이관된 컴포넌트의 Port 어댑터가 DI 에 등록되지 않았습니다. BlazorHomeForm 의 services.AddSingleton<IXxxPort, XxxAdapter>() 배선을 확인하세요(미배선 시 해당 페이지 런타임 화이트스크린).",
            _ => "알 수 없는 점검 식별자입니다.",
        };
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  기본 probe 구현 — 실제 환경/DI 컬렉션에서 신호를 '생성자에서 1회' 계산(콜드 I/O).
    //  게터는 캐시된 bool 을 반환(무할당) → Diagnose 가 δ=0 을 유지한다.
    // ══════════════════════════════════════════════════════════════════════════
    public sealed class DefaultOnboardingProbe : IOnboardingProbe
    {
        // 대표 이관 Port — 하나라도 미등록이면 해당 이관 페이지가 런타임에 죽는다. Phase 94-G 신규 IAlertFeedPort 포함.
        private static readonly Type[] RequiredPorts =
        {
            typeof(MS2E.UI.Portable.Ports.Alerts.IAlertFeedPort),        // Phase 94-G
            typeof(MS2E.UI.Portable.Ports.Strategy.IStrategyHubInfraPort),
            typeof(MS2E.UI.Portable.Ports.Strategy.ISignalsRepositoryPort),
        };

        public bool RuntimeMeetsMinimum { get; }
        public bool DataDirectoryWritable { get; }
        public bool DpapiAvailable { get; }
        public bool KiwoomProxyPresent { get; }
        public bool LiveArmingSafeAtBoot { get; }
        public bool MigratedPortsBound { get; }

        /// <param name="services">부팅 DI 컬렉션(Port 등록 여부 스캔).</param>
        /// <param name="canaryArmedAtBoot">부팅 시 CanaryHealthSentinel 무장 상태(정상=false).</param>
        public DefaultOnboardingProbe(IServiceCollection services, bool canaryArmedAtBoot)
        {
            RuntimeMeetsMinimum  = Environment.Version.Major >= 8;
            DataDirectoryWritable = ProbeDataDirWritable();
            DpapiAvailable       = OperatingSystem.IsWindows();   // DPAPI 는 Windows 전용
            KiwoomProxyPresent   = ProbeKiwoomProxy();

            // ★liveArmed:false 부팅 자세: 무장 상태가 아니어야 하고, 라이브 무장 환경 오버라이드도 없어야 안전.
            bool liveEnvOverride =
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MS2E_LIVE_ARM"));
            LiveArmingSafeAtBoot = !canaryArmedAtBoot && !liveEnvOverride;

            MigratedPortsBound = ProbePortsBound(services);
        }

        private static bool ProbeDataDirWritable()
        {
            try
            {
                var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(baseDir)) return false;
                var dir = Path.Combine(baseDir, "MS2E");
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".onboarding_probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch { return false; }   // fail-closed
        }

        private static bool ProbeKiwoomProxy()
        {
            try
            {
                var baseDir = AppContext.BaseDirectory;
                // 배포/개발 레이아웃 모두 관용적으로 탐색(존재만 확인 — Advisory).
                return File.Exists(Path.Combine(baseDir, "StockManager.KiwoomProxy.exe"))
                    || Directory.Exists(Path.Combine(baseDir, "x86"))
                    || File.Exists(Path.Combine(baseDir, "KiwoomProxy", "StockManager.KiwoomProxy.exe"));
            }
            catch { return false; }
        }

        private static bool ProbePortsBound(IServiceCollection? services)
        {
            if (services is null) return false;   // fail-closed
            try
            {
                foreach (var portType in RequiredPorts)
                    if (!services.Any(d => d.ServiceType == portType))
                        return false;
                return true;
            }
            catch { return false; }
        }
    }
}
