// ============================================================================
// BootPreFlightSentinel — [사전 검증 · 추천 A] 부팅/런타임 사전 검증 Fail-Closed 게이트 + API 키 즉시 소거
//
//  GenesisPreFlightCertifier(주기 채점 · 무장 임대)가 보지 않는 세 가지 '전제 붕괴'를 잡는다:
//   ① 시계 역행 — (a) 부팅: 직전 실행이 남긴 시계 앵커(%LOCALAPPDATA%\MS2E\preflight.clock · UTC 틱)보다 현재 시각이
//      허용치(기본 2분) 이상 과거 = 시스템 시계 되감기(체결/감사 시각 위조·만료 우회 수법). 앵커는 '절대 뒤로 쓰지 않는다'
//      (되감긴 시계로 앵커를 덮어써 증거를 지우는 것 차단) — 시계를 바로잡기 전까지 매 부팅 재차단.
//      (b) 런타임: 벽시계 경과 < 단조 시계(QPC) 경과 − 허용치 = 실행 중 되감기. 정방향 점프(NTP 보정)는 기준 재설정.
//   ② 안전 설정 런타임 변조 — 설정은 환경변수 → DB 를 '호출마다' 읽는다(RuntimeConfig). 안전 핵심 키(물리 이중키 · 리스크 한도 ·
//      익스포저 상한 · 봉인 앵커 …)의 지문(SHA-256)을 부팅 시 봉인하고 주기 재계산 — 실행 중 바뀌면 변조(이 호스트에는 해당 키를
//      쓰는 UI 경로가 없다 = 외부 DB 편집).
//   ③ 프록시 파이프 서버 신원 불일치 — 잘 알려진 파이프 이름을 다른 프로세스가 선점(squatting)해 키움 프록시를 사칭하면
//      위조 체결/시세/잔고 주입 + 주문 탈취가 가능하다. KiwoomBridgeClient 가 연결 직후 서버 PID → 실행 파일 경로를 대조하고
//      불일치를 여기로 보고.
//  어느 하나라도 = sticky Trip: 집계 비트(ArmingInterlockSource.PreFlightBoot) Raise + 발주 토큰/API Secret(PoisonedSecretBuffer ·
//  VolatileMemoryPurger) 즉시 0x00 소거. 이 호스트에는 운용자 재무장 UI 경로가 없으므로 복구 = 원인 제거 후 재시작.
//  δ=0: ObserveClock · ObserveConfigFingerprint · 판정은 값 비교만(힙 0). 지문 '산출'(설정 문자열 조회)과 앵커 파일 I/O 는 콜드.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Security
{
    [Flags]
    public enum PreFlightFault : int
    {
        None = 0, ClockRegressedAtBoot = 1, ClockRegressedRuntime = 2, SafetyConfigTampered = 4, ProxyIdentityRejected = 8,
    }

    public sealed class BootPreFlightSentinel : IArmingBlockSource
    {
        public static readonly TimeSpan DefaultClockTolerance = TimeSpan.FromMinutes(2);

        /// <summary>런타임 변조 감시 대상 안전 핵심 설정 키(값이 바뀌면 무장 차단).</summary>
        public static readonly string[] SafetyKeys =
        {
            "Dispatch.ValveWired", "Dispatch.LiveArmingWired", "Dispatch.DryRunActive", "Dispatch.ArmingLeaseSeconds",
            "Risk.Var99LimitKrw", "Release.ExpectedMasterSeal",
            "Router.MaxSlippageBps", "Router.RequireAccountSync", "Router.MarketRequiresDepth", "Router.ExtremeSlippageBps",
            "Exposure.SingleNameCapBps", "Exposure.SectorCapBps", "Correlation.DropBps", "Correlation.MinNames", "Correlation.BreadthBps",
        };

        private readonly long _toleranceTicks;
        private readonly VolatileMemoryPurger? _purger;
        private readonly PoisonedSecretBuffer[] _secrets;
        private readonly Action<PreFlightFault>? _onTrip;
        private ArmingInterlockLink? _link;
        private int _faults, _tripped;
        private long _baseWall, _baseMono, _monoFreq;   // 단일 호출자(주기 타이머) 전용 — 비원자 필드
        private readonly byte[] _sealedFingerprint = new byte[32];
        private int _configSealed;
        private long _purges, _callbackFaults, _clockChecks, _configChecks;

        public BootPreFlightSentinel(VolatileMemoryPurger? purger = null, IReadOnlyList<PoisonedSecretBuffer>? secrets = null,
                                     TimeSpan? clockTolerance = null, Action<PreFlightFault>? onTrip = null)
        {
            _purger = purger;
            _secrets = secrets is null ? Array.Empty<PoisonedSecretBuffer>() : new List<PoisonedSecretBuffer>(secrets).ToArray();
            var tol = clockTolerance ?? DefaultClockTolerance;
            if (tol <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(clockTolerance));
            _toleranceTicks = tol.Ticks;
            _onTrip = onTrip;
        }

        public PreFlightFault Faults => (PreFlightFault)Volatile.Read(ref _faults);
        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;
        public long Purges => Interlocked.Read(ref _purges);
        public long ClockChecks => Interlocked.Read(ref _clockChecks);
        public long ConfigChecks => Interlocked.Read(ref _configChecks);
        public bool ConfigSealed => Volatile.Read(ref _configSealed) == 1;

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        // ── ① 시계 ──

        /// <summary>부팅 시계 판정(δ=0). anchorUtcTicks ≤ 0 = 앵커 없음(최초 실행) → 통과.</summary>
        public PreFlightFault EvaluateBootClock(long nowUtcTicks, long anchorUtcTicks)
        {
            Interlocked.Increment(ref _clockChecks);
            if (anchorUtcTicks > 0 && nowUtcTicks < anchorUtcTicks - _toleranceTicks)
            {
                Trip(PreFlightFault.ClockRegressedAtBoot);
                return PreFlightFault.ClockRegressedAtBoot;
            }
            return PreFlightFault.None;
        }

        /// <summary>런타임 시계 관측(δ=0 · 주기 호출). monoTicks/monoFrequency = Stopwatch.GetTimestamp/Frequency.</summary>
        public void ObserveClock(long utcTicks, long monoTicks, long monoFrequency)
        {
            Interlocked.Increment(ref _clockChecks);
            if (monoFrequency <= 0) return;
            if (Interlocked.Read(ref _monoFreq) == 0)
            {
                _baseWall = utcTicks; _baseMono = monoTicks;
                Volatile.Write(ref _monoFreq, monoFrequency);
                return;
            }
            long monoElapsed = (long)((Int128)(monoTicks - _baseMono) * TimeSpan.TicksPerSecond / monoFrequency);
            long wallElapsed = utcTicks - _baseWall;
            if (wallElapsed < monoElapsed - _toleranceTicks) { Trip(PreFlightFault.ClockRegressedRuntime); return; }
            // 정방향 점프(NTP 보정 · 절전 복귀) — 기준 재설정(되감기 판정만 엄격)
            if (wallElapsed > monoElapsed + _toleranceTicks) { _baseWall = utcTicks; _baseMono = monoTicks; }
        }

        /// <summary>앵커 판독(콜드). 없음/손상 = 0.</summary>
        public static long ReadClockAnchor(string path)
        {
            try
            {
                if (!File.Exists(path)) return 0;
                var text = File.ReadAllText(path).Trim();
                return long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long t) && t > 0 ? t : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }

        /// <summary>앵커 기록(콜드) — 기존 값보다 '앞'일 때만(되감긴 시계로 증거 덮어쓰기 금지). 반환 = 기록 여부.</summary>
        public static bool AdvanceClockAnchor(string path, long nowUtcTicks)
        {
            try
            {
                if (nowUtcTicks <= ReadClockAnchor(path)) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, nowUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        public static string DefaultAnchorPath()
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MS2E", "preflight.clock");

        // ── ② 안전 설정 ──

        /// <summary>안전 핵심 키 지문(콜드 — 설정 문자열 조회). 부재 키는 "&lt;null&gt;" 로 구분.</summary>
        public static void ComputeConfigFingerprint(Func<string, string?> raw, Span<byte> dst32)
        {
            var sb = new StringBuilder(512);
            foreach (var k in SafetyKeys)
            {
                string? v;
                try { v = raw(k); } catch { v = "<fault>"; }
                sb.Append(k).Append('=').Append(v ?? "<null>").Append('\n');
            }
            SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()), dst32);
        }

        /// <summary>부팅 시 지문 봉인(1회).</summary>
        public void SealConfig(ReadOnlySpan<byte> fingerprint32)
        {
            if (fingerprint32.Length != 32) throw new ArgumentException("SHA-256 32B", nameof(fingerprint32));
            if (Interlocked.CompareExchange(ref _configSealed, 2, 0) != 0) return;
            fingerprint32.CopyTo(_sealedFingerprint);
            Volatile.Write(ref _configSealed, 1);
        }

        /// <summary>런타임 지문 대조(δ=0). 봉인 전 호출은 무시.</summary>
        public void ObserveConfigFingerprint(ReadOnlySpan<byte> fingerprint32)
        {
            if (Volatile.Read(ref _configSealed) != 1) return;
            Interlocked.Increment(ref _configChecks);
            if (fingerprint32.Length != 32 || !CryptographicOperations.FixedTimeEquals(fingerprint32, _sealedFingerprint))
                Trip(PreFlightFault.SafetyConfigTampered);
        }

        // ── ③ 프록시 신원 ──

        public void ReportProxyIdentityRejected() => Trip(PreFlightFault.ProxyIdentityRejected);

        // ── 내부 ──

        private void Trip(PreFlightFault fault)
        {
            Interlocked.Or(ref _faults, (int)fault);
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return;
            Volatile.Read(ref _link)?.Resync();                         // 차단 선반영 → 소거
            foreach (var s in _secrets) { try { s.PurgeAndPoison(); } catch { Interlocked.Increment(ref _callbackFaults); } }
            try { _purger?.PurgeAll(); } catch { Interlocked.Increment(ref _callbackFaults); }
            Interlocked.Increment(ref _purges);
            try { _onTrip?.Invoke(fault); } catch { Interlocked.Increment(ref _callbackFaults); }
        }
    }
}
