// ============================================================================
// ArchitectureFitnessAnalyzer — v11.2.0: Clean Architecture 적합도 함수 (Think Beyond Gemini)
//
//   ★제미나이의 '폴더 재배치(1회성 정적 정돈)'를 정적분석 레벨에서 초월한다 — 아키텍처를 '한 번 정렬'하는 게 아니라
//   '지속 검증(fitness function)'한다. 5대 레이어(Core→Infra→UI→Host→Tests) 모델 + 의존 방향 규칙(안으로만:
//   바깥 레이어는 안쪽만 참조 가능)을 코드로 박제하여, 유닛 테스트가 레이어 위반(예: Core→Infrastructure,
//   UI→Infrastructure[엔진 참조])을 발견하는 순간 빌드가 실패한다. 즉 폴더 재배치가 '자기강제(self-enforcing)'
//   된다. 또한 '의도 레이어(역할)' vs '물리 위치(경로)'를 분리 분류해 '오배치(misplacement)'를 정량 탐지 —
//   재배치가 무엇을 옮겨야 하는지 뷰어(ProjectCodeViewer)가 즉시 시각화한다.
//
//   ★불변: δ=0(ClassifyLayer/ClassifyDomain/CanReference/Ingest 핫패스 힙 0 — ReadOnlySpan 연산·enum·정수
//     카운터만, string/ToLower/배열 미생성) · 결정론(경로 키워드 순서 규칙·주입 없는 순수 함수·시계/난수 0) ·
//     liveArmed:false(순수 정적 분석 — IBroker·소켓·발주 유발 0) · fail-open 분류(Unknown 은 위반으로 오판하지
//     않아 거짓 경보를 억제 — 분류 불확실이 아키텍처 게이트를 잠그지 않는다).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>5대 Clean Architecture 레이어(안쪽=낮은 번호). 의존은 '안으로만' 향한다.</summary>
    public enum ArchLayer : byte
    {
        Core           = 0,   // 01_Core — 순수 도메인·엔티티·불변 DTO·Port 인터페이스
        Infrastructure = 1,   // 02_Infrastructure — 외부 어댑터·HW·보안·텔레메트리·영속화
        UiPortable     = 2,   // 03_UI.Portable — 순수 Blazor UI(RCL·엔진 무참조)
        Host           = 3,   // 04_Host — WinForms/Blazor Hybrid 합성 루트(모든 레이어 참조 가능)
        Tests          = 4,   // 05_Tests — 유닛/통합/δ=0 증명
        Unknown        = 255, // 분류 불확실(fail-open — 위반 판정 제외)
    }

    /// <summary>레이어 내 도메인(뷰어 그룹핑·오배치 탐지용).</summary>
    public enum ArchDomain : byte
    {
        // Core
        Entities, Ports, ValueObjects,
        // Infrastructure
        BrokerAdapters, Hardware, Security, Telemetry, Persistence, Networking, Engine,
        // UI
        Components, Pages, ViewModels,
        // Host
        HostBootstrap, Navigation,
        // Tests
        UnitTests,
        Unclassified,
    }

    /// <summary>순수 정적 분류 + Clean Architecture 의존 방향 규칙(모두 δ=0·결정론·부작용 0).</summary>
    public static class ArchitectureFitnessAnalyzer
    {
        // ── 의존 방향 규칙(핵심) — 의존은 안으로만 ───────────────────────────
        /// <summary>
        /// ★Clean Architecture 불변: 바깥 레이어는 '안쪽(더 낮은 번호) 또는 동일' 레이어만 참조할 수 있다.
        ///  · Core          → Core 만(가장 안쪽 — 바깥 의존 0)
        ///  · Infrastructure→ Core, Infrastructure(Core Port 를 구현)
        ///  · UiPortable    → Core, UiPortable(★엔진[Infra] 무참조 — 순수 UI 불변)
        ///  · Host / Tests  → 모든 레이어(합성 루트 / 검증)
        ///  · Unknown 관여   → true(fail-open — 분류 불확실을 위반으로 오판하지 않음)
        /// 반환 false = 아키텍처 위반(안→바깥 역참조).
        /// </summary>
        public static bool CanReference(ArchLayer from, ArchLayer to)
        {
            if (from == ArchLayer.Unknown || to == ArchLayer.Unknown) return true;
            return from switch
            {
                ArchLayer.Host           => true,
                ArchLayer.Tests          => true,
                ArchLayer.Core           => to == ArchLayer.Core,
                ArchLayer.Infrastructure => to == ArchLayer.Core || to == ArchLayer.Infrastructure,
                ArchLayer.UiPortable     => to == ArchLayer.Core || to == ArchLayer.UiPortable,
                _                        => true,
            };
        }

        // ── δ=0 순수 분류: 경로/파일명 → 의도 레이어 ──────────────────────────
        /// <summary>파일 경로를 '의도 레이어'로 분류(역할 기반·δ=0). 첫 매칭 우선(결정론적 우선순위).</summary>
        public static ArchLayer ClassifyLayer(ReadOnlySpan<char> path)
        {
            // 1) 테스트 프로젝트
            if (Has(path, "StockManager.Tests") || Has(path, ".Tests") || Has(path, "PrimesBench")) return ArchLayer.Tests;
            // 2) 순수 UI RCL
            if (Has(path, "UI.Portable")) return ArchLayer.UiPortable;
            // 3) 호스트 합성 루트/네비게이션(엔트리·폼·네비·레이아웃) — .razor 보다 먼저 걸러야 함
            if (IsHostShell(path)) return ArchLayer.Host;
            // 4) 그 외 Blazor 컴포넌트 = UI 의도(현재 물리 위치는 Host 일 수 있음 → 오배치로 탐지)
            if (Has(path, ".razor")) return ArchLayer.UiPortable;
            // 5) Core(Port/값객체/엔티티/DTO)
            if (IsCore(path, out _)) return ArchLayer.Core;
            // 6) 나머지 .cs(엔진·어댑터·보안·텔레메트리·영속화) = Infrastructure
            if (Has(path, ".cs")) return ArchLayer.Infrastructure;
            return ArchLayer.Unknown;
        }

        /// <summary>파일 경로를 '의도 도메인'으로 분류(δ=0). 뷰어 그룹핑용.</summary>
        public static ArchDomain ClassifyDomain(ReadOnlySpan<char> path)
        {
            ArchLayer layer = ClassifyLayer(path);
            switch (layer)
            {
                case ArchLayer.Tests:
                    return ArchDomain.UnitTests;

                case ArchLayer.UiPortable:
                    if (Has(path, "Page") || Has(path, "/Pages/") || Has(path, "\\Pages\\")) return ArchDomain.Pages;
                    if (Has(path, "ViewModel") || Has(path, "Dto")) return ArchDomain.ViewModels;
                    return ArchDomain.Components;

                case ArchLayer.Host:
                    if (Has(path, "Nav") || Has(path, "Menu") || Has(path, "Layout")) return ArchDomain.Navigation;
                    return ArchDomain.HostBootstrap;

                case ArchLayer.Core:
                    IsCore(path, out ArchDomain cd);
                    return cd;

                case ArchLayer.Infrastructure:
                    return InfraDomain(path);

                default:
                    return ArchDomain.Unclassified;
            }
        }

        // ── 물리 위치(경로) → 실제 레이어(오배치 탐지용) ──────────────────────
        /// <summary>파일이 '물리적으로' 어느 프로젝트/레이어에 위치하는지(경로 최상위 프로젝트 디렉터리 기준·δ=0).</summary>
        public static ArchLayer LocateLayer(ReadOnlySpan<char> path)
        {
            if (Has(path, "StockManager.Tests") || Has(path, "PrimesBench") || Has(path, "KiwoomProxy")) return ArchLayer.Tests;
            if (Has(path, "UI.Portable")) return ArchLayer.UiPortable;
            // 그 외는 전부 현재 모놀리식 Host 프로젝트(StockManager/)에 물리 존재
            if (Has(path, ".cs") || Has(path, ".razor")) return ArchLayer.Host;
            return ArchLayer.Unknown;
        }

        /// <summary>★오배치: 의도 레이어(역할) ≠ 물리 위치 레이어. Host/Tests 물리위치는 합성상 허용(오배치 아님).</summary>
        public static bool IsMisplaced(ReadOnlySpan<char> path)
        {
            ArchLayer intended = ClassifyLayer(path);
            ArchLayer located  = LocateLayer(path);
            if (intended == ArchLayer.Unknown || located == ArchLayer.Unknown) return false;
            // Core/Infra 로 의도된 파일이 Host 프로젝트(StockManager/)에 물리 존재하면 → 재배치 대상(오배치).
            if (located == ArchLayer.Host && (intended == ArchLayer.Core || intended == ArchLayer.Infrastructure || intended == ArchLayer.UiPortable))
                return true;
            return false;
        }

        // ── 표시 헬퍼(레이어/도메인 한글명·폴더명) ────────────────────────────
        public static string LayerFolder(ArchLayer l) => l switch
        {
            ArchLayer.Core => "01_Core", ArchLayer.Infrastructure => "02_Infrastructure",
            ArchLayer.UiPortable => "03_UI.Portable", ArchLayer.Host => "04_Host", ArchLayer.Tests => "05_Tests",
            _ => "(unknown)",
        };
        public static string LayerName(ArchLayer l) => l switch
        {
            ArchLayer.Core => "순수 도메인", ArchLayer.Infrastructure => "인프라", ArchLayer.UiPortable => "UI 포터블",
            ArchLayer.Host => "호스트", ArchLayer.Tests => "테스트", _ => "미분류",
        };

        // ── 내부 규칙(δ=0) ──────────────────────────────────────────────────
        private static bool Has(ReadOnlySpan<char> hay, ReadOnlySpan<char> needle)
            => hay.Contains(needle, StringComparison.OrdinalIgnoreCase);

        /// <summary>호스트 셸(엔트리·WinForms 폼·네비·레이아웃) 여부.</summary>
        private static bool IsHostShell(ReadOnlySpan<char> path)
            => Has(path, "Program.cs") || Has(path, "App.razor") || Has(path, "BlazorHomeForm")
               || Has(path, "LauncherForm") || Has(path, "NavMenu") || Has(path, "MainLayout")
               || Has(path, "EmptyLayout") || Has(path, "Routes.razor") || EndsWith(path, "Form.cs");

        /// <summary>Core(Port/값객체/엔티티/DTO) 여부 + 도메인 산출.</summary>
        private static bool IsCore(ReadOnlySpan<char> path, out ArchDomain domain)
        {
            // Port 인터페이스: 파일명이 I*Port / *Port.cs, 또는 Ports 폴더
            if (Has(path, "Port.cs") || Has(path, "/Ports/") || Has(path, "\\Ports\\")) { domain = ArchDomain.Ports; return true; }
            // 값객체: QFixed/QMoney/QMath/QMultiAsset/QFutureValue 등 고정소수 원시 + Price/Quantity/Symbol/Money
            if (Has(path, "QFixed") || Has(path, "QMoney") || Has(path, "QMath") || Has(path, "QMultiAsset")
                || Has(path, "QFutureValue") || Has(path, "ValueObject") || Has(path, "/ValueObjects/"))
            { domain = ArchDomain.ValueObjects; return true; }
            // 엔티티/불변 DTO: Types/Dto/Snapshot/Entity + 도메인 명사
            if (Has(path, "Types.cs") || Has(path, "Dto") || Has(path, "Snapshot") || Has(path, "Entity")
                || Has(path, "/Entities/"))
            { domain = ArchDomain.Entities; return true; }
            domain = ArchDomain.Unclassified;
            return false;
        }

        /// <summary>Infrastructure 도메인 세분(δ=0·키워드 우선순위).</summary>
        private static ArchDomain InfraDomain(ReadOnlySpan<char> path)
        {
            if (Has(path, "Adapter") || Has(path, "Toss") || Has(path, "Kis") || Has(path, "Kiwoom")
                || Has(path, "Broker") || Has(path, "Provider") || Has(path, "Gateway") || Has(path, "Mesh"))
                return ArchDomain.BrokerAdapters;
            if (Has(path, "Vault") || Has(path, "Security") || Has(path, "Dpapi") || Has(path, "Signoff")
                || Has(path, "Crypto") || Has(path, "AntiHook") || Has(path, "HoneyKey") || Has(path, "Zeroiz")
                || Has(path, "Seal") || Has(path, "Redact"))
                return ArchDomain.Security;
            if (Has(path, "Telemetry") || Has(path, "Ring") || Has(path, "Mmf") || Has(path, "Audit")
                || Has(path, "FlightRecorder") || Has(path, "BlackBox") || Has(path, "Trace") || Has(path, "Dumper"))
                return ArchDomain.Telemetry;
            if (Has(path, "Simd") || Has(path, "Avx") || Has(path, "Affinity") || Has(path, "Cpu")
                || Has(path, "PCore") || Has(path, "Hardware") || Has(path, "Thermal") || Has(path, "Numa") || Has(path, "Kernel"))
                return ArchDomain.Hardware;
            if (Has(path, "Database") || Has(path, "Sqlite") || Has(path, "Repository") || Has(path, "Persist")
                || Has(path, "LocalDb"))
                return ArchDomain.Persistence;
            if (Has(path, "Http") || Has(path, "WebSocket") || Has(path, "Socket") || Has(path, "Lan") || Has(path, "Ipc"))
                return ArchDomain.Networking;
            return ArchDomain.Engine;
        }

        private static bool EndsWith(ReadOnlySpan<char> hay, ReadOnlySpan<char> suffix)
            => hay.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 적합도 스캔 집계기(δ=0 Ingest) — 의존 엣지(from→to)를 받아 위반을 계수한다. 유닛 테스트/뷰어가
    /// 실제 참조 그래프를 흘려 넣어 Clean = (Violations==0) 을 단언. 스레드 안전(Interlocked·힙 0).
    /// </summary>
    public sealed class FitnessScan
    {
        private long _edges, _violations;

        public long Edges      => Interlocked.Read(ref _edges);
        public long Violations => Interlocked.Read(ref _violations);
        public bool Clean      => Interlocked.Read(ref _violations) == 0L;

        /// <summary>엣지 1건 반영(δ=0). 반환=규칙 준수 여부(false=위반).</summary>
        public bool Ingest(ArchLayer from, ArchLayer to)
        {
            Interlocked.Increment(ref _edges);
            bool ok = ArchitectureFitnessAnalyzer.CanReference(from, to);
            if (!ok) Interlocked.Increment(ref _violations);
            return ok;
        }

        public void Reset() { Interlocked.Exchange(ref _edges, 0L); Interlocked.Exchange(ref _violations, 0L); }
    }
}
