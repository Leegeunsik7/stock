// ============================================================================
// TightenOnlySettingGuardTests — [v12.18.3-DEV · 추천 A] 강화 전용 UI 설정 기입 · 단조성 IL 판독
//
//  §1 판정: 게이트 false→true 만 · 임계 상향만(유효값 = 호스트 클램프 · "-5" = 5000 완화 거부 · "99999" = 10000 강화) · 승인 한도 하향만 ·
//     형식 불량 · 무장/카나리/디스패치/브로커/미등재 Strategy 키 = 폐기 · 환경변수 덮음 = 폐기(거짓 성공 0) · 저장 실패 = 기존 값 유지.
//  §2 단조성 속성 퍼징: 무작위 현재값 × 제안값 20만 — 강화 판정이면 유효값이 규칙 방향으로만 이동 · 완화 판정이면 기입 0.
//  §3 경합: 8 스레드 동시 상향/하향 — 기입 순서열이 단조(늦게 쓴 덜 엄격한 값이 이기지 않음).
//  §4 어댑터 실 DB: TightenSetting · TryGetTightenable · SetSetting(Strategy.*) 여전히 폐기 · 판정 코드 RCL ↔ 호스트 대조.
//  §5 런타임 상향 전용: 확신 하한 · 횡보 최대 임계 올리기만 · 스위치 1회 판독 반영 · 드라이런 게이트가 상향 즉시 거부.
//  §6 출하 IL 판독(실 호스트 DLL): Ok · 키 6 · 호스트 판독 지점 · 1바이트 패치 6종(clt→cgt · 상수 true · brfalse→br · 규칙 비트 2→4 ·
//     폴백 9500→5000 · 진입점 TryWrite 토큰 교체) = 전부 거부 · PS 5.1 역슬래시 zip · CLI 보고.
//  §7 δ=0(판정 루프) · 회귀(오중 빌드 · 래칫 · 인터락 30).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports;
using StockManager.Engines;
using StockManager.Simulation;
using StockManager.Tools;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class TightenOnlySettingGuardTests
    {
        private readonly ITestOutputHelper _out;
        public TightenOnlySettingGuardTests(ITestOutputHelper o) => _out = o;

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        /// <summary>메모리 저장소(환경변수 · DB 분리 · 기입 순서 기록 · 실패 주입).</summary>
        private sealed class MemStore : ITightenSettingStore
        {
            public readonly Dictionary<string, string> Env = new(), Db = new();
            public readonly List<(string Key, string Value)> Writes = new();
            public bool FailWrite, FailRead;
            public string? ReadEnvironment(string key) { if (FailRead) throw new IOException("env"); return Env.TryGetValue(key, out var v) ? v : null; }
            public string? ReadStored(string key) { lock (Db) return Db.TryGetValue(key, out var v) ? v : null; }
            public void WriteStored(string key, string value)
            {
                if (FailWrite) throw new IOException("db");
                lock (Db) { Db[key] = value; Writes.Add((key, value)); }
            }
        }

        /// <summary>판정 테스트용 감사 싱크(항상 기록 성공 · 레코드 수만 셈) — 원장 자체는 MonotonicSettingAuditLogTests 가 검증.</summary>
        internal sealed class PassAudit : ITightenAuditSink
        {
            public static readonly PassAudit Instance = new();
            private long _n;
            public long Records => Interlocked.Read(ref _n);
            public bool Frozen => false;
            public bool Append(int keyIndex, string? key, TightenVerdict verdict, long current, long proposed) { Interlocked.Increment(ref _n); return true; }
        }

        private static TightenVerdict W(MemStore s, string key, string value) => TightenOnlyUiSettingWriter.TryWrite(key, value, s, PassAudit.Instance);

        // ════════════ §1 판정 ════════════

        [Fact]
        public void Gate_FalseToTrueOnly_LooseningDropped_StoredPreserved()
        {
            var s = new MemStore();
            const string K = "Strategy.SignalGateEnabled";
            Assert.Equal(TightenVerdict.Loosening, W(s, K, "false"));                    // TrueOnly = true 만 기입 가능(false 는 현재와 같아도 거부 · 기입 0)
            Assert.Equal(TightenVerdict.Malformed, W(s, K, "maybe"));
            Assert.Equal(TightenVerdict.Tightened, W(s, K, "true"));
            Assert.Equal("true", s.Db[K]);
            foreach (var off in new[] { "false", "0", "no", "OFF" })
                Assert.Equal(TightenVerdict.Loosening, W(s, K, off));
            Assert.Equal("true", s.Db[K]);                                                // ★기존 값 보존
            Assert.Equal(TightenVerdict.Unchanged, W(s, K, "YES"));
            Assert.Single(s.Writes);
            s.Db[K] = "garbage";                                                          // 저장값 불량 = 호스트 폴백(false) 기준
            Assert.Equal(TightenVerdict.Tightened, W(s, K, "on"));
        }

        [Fact]
        public void Thresholds_RaiseOnly_EffectiveValueCompare_HostClamp()
        {
            var s = new MemStore();
            const string M = "Strategy.AdaptiveMaxBp", C = "Strategy.ConfidenceThresholdBp";
            Assert.Equal(TightenVerdict.Loosening, W(s, M, "9400"));                     // 미설정 폴백 9500 기준
            Assert.Equal(TightenVerdict.Loosening, W(s, M, "-5"));                       // 클램프 5000 = 완화
            Assert.Equal(TightenVerdict.Malformed, W(s, M, "9,600"));
            Assert.Equal(TightenVerdict.Malformed, W(s, M, "abc"));
            Assert.Equal(TightenVerdict.Unchanged, W(s, M, "9500"));
            Assert.Equal(TightenVerdict.Tightened, W(s, M, "9600"));
            Assert.Equal(TightenVerdict.Loosening, W(s, M, "9599"));
            Assert.Equal(TightenVerdict.Tightened, W(s, M, "99999"));                    // 클램프 10000 = 강화(원문 저장 · 호스트도 같은 클램프)
            Assert.Equal(TightenVerdict.Unchanged, W(s, M, "20000"));                    // 유효값 10000 = 같음
            Assert.Equal("99999", s.Db[M]);
            Assert.Equal(TightenVerdict.Loosening, W(s, C, "8000"));
            Assert.Equal(TightenVerdict.Tightened, W(s, C, " 9000 "));                   // 호스트 int.TryParse(Integer) = 앞뒤 공백 허용
            Assert.Equal(TightenVerdict.Loosening, W(s, C, "8999"));
        }

        [Fact]
        public void ChurnLimits_LowerOnly()
        {
            var s = new MemStore();
            Assert.Equal(TightenVerdict.Loosening, W(s, "Strategy.ChurnSoftPerMin", "31"));
            Assert.Equal(TightenVerdict.Tightened, W(s, "Strategy.ChurnSoftPerMin", "20"));
            Assert.Equal(TightenVerdict.Loosening, W(s, "Strategy.ChurnHardPerMin", "61"));
            Assert.Equal(TightenVerdict.Tightened, W(s, "Strategy.ChurnHardPerMin", "40"));
            Assert.Equal(TightenVerdict.Tightened, W(s, "Strategy.ChurnHardPerMin", "0"));   // 클램프 하한 2 = 더 엄격(호스트는 max(soft+1, hard))
            Assert.Equal(TightenVerdict.Loosening, W(s, "Strategy.AdaptiveThreshold", "false"));   // 폴백 true(적응 사용) → 끄기 = 완화
            Assert.Empty(s.Writes.Where(w => w.Key == "Strategy.AdaptiveThreshold"));
        }

        [Fact]
        public void ArmingCanaryDispatchBrokerAndUnlistedKeys_AllDropped_NothingWritten()
        {
            var s = new MemStore();
            foreach (var key in new[] { "Dispatch.ValveWired", "Dispatch.LiveArmingWired", "Dispatch.DryRunActive", "Broker.LiveArmed", "Canary.CapBps",
                                        "Risk.MaxNotional", "Security.Anything", "Strategy.CrossMaxSkewMs", "Strategy.SignalGateEnabledX", "strategy.signalgateenabled",
                                        "", "Strategy." })
            {
                Assert.Equal(TightenVerdict.UnknownKey, W(s, key, "true"));
                Assert.Equal(TightenVerdict.UnknownKey, W(s, key, "0"));
            }
            Assert.Equal(TightenVerdict.UnknownKey, TightenOnlyUiSettingWriter.TryWrite(null, "true", s, PassAudit.Instance));
            Assert.Equal(TightenVerdict.Malformed, TightenOnlyUiSettingWriter.TryWrite("Strategy.SignalGateEnabled", null, s, PassAudit.Instance));
            Assert.Empty(s.Writes);
        }

        [Fact]
        public void EnvironmentOverride_And_StoreFaults_FailClosed()
        {
            var s = new MemStore();
            s.Env["Strategy.AdaptiveMaxBp"] = "9700";
            Assert.Equal(TightenVerdict.EnvironmentOverride, W(s, "Strategy.AdaptiveMaxBp", "9900"));   // DB 기입은 무효 = 거짓 성공 금지
            Assert.Empty(s.Writes);
            s.Env.Clear();
            s.FailRead = true;
            Assert.Equal(TightenVerdict.StoreFault, W(s, "Strategy.AdaptiveMaxBp", "9900"));
            s.FailRead = false; s.FailWrite = true;
            Assert.Equal(TightenVerdict.StoreFault, W(s, "Strategy.AdaptiveMaxBp", "9900"));
            Assert.False(s.Db.ContainsKey("Strategy.AdaptiveMaxBp"));
        }

        // ════════════ §2 단조성 속성 퍼징 ════════════

        [Fact]
        public void MonotonicityProperty_Fuzz_200k_NoLooseningEverWritten()
        {
            long rng = 0x2545F4914F6CDD1D;
            string Rand(int idx)
            {
                rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17;
                long r = rng & 0xFFFFFF;
                if (TightenOnlyUiSettingWriter.RuleAt(idx) == TightenOnlyUiSettingWriter.RuleTrueOnly)
                    return (r % 9) switch { 0 => "true", 1 => "false", 2 => "1", 3 => "0", 4 => "yes", 5 => "off", 6 => "On", 7 => "x", _ => "" };
                return (r % 7) switch { 0 => "abc", 1 => (-(r % 50_000)).ToString(), 2 => (r % 200_000).ToString(), _ => (r % 12_000).ToString() };
            }
            int written = 0, loosen = 0;
            for (int n = 0; n < 200_000; n++)
            {
                int idx = (int)((ulong)rng % (ulong)TightenOnlyUiSettingWriter.KeyCount);
                string key = TightenOnlyUiSettingWriter.KeyAt(idx);
                var s = new MemStore();
                string? cur = (n % 5 == 0) ? null : Rand(idx);
                if (cur is not null) s.Db[key] = cur;
                TightenOnlyUiSettingWriter.TryEffective(idx, (cur ?? "").AsSpan(), false, out long before);
                var v = W(s, key, Rand(idx));
                TightenOnlyUiSettingWriter.TryEffective(idx, (s.Db.TryGetValue(key, out var after) ? after : "").AsSpan(), false, out long eff);
                if (v == TightenVerdict.Tightened)
                {
                    written++;
                    byte rule = TightenOnlyUiSettingWriter.RuleAt(idx);
                    Assert.True(rule switch { 1 => eff == 1 && before == 0, 2 => eff > before, 4 => eff < before, _ => false }, $"{key} {before} → {eff}");
                }
                else
                {
                    if (v == TightenVerdict.Loosening) loosen++;
                    Assert.Equal(cur, s.Db.TryGetValue(key, out var kept) ? kept : null);  // ★거부 = 기존 값 그대로
                    Assert.Empty(s.Writes);
                }
            }
            _out.WriteLine($"[퍼징] 20만 · 강화 기입 {written} · 완화 거부 {loosen}");
            Assert.True(written > 10_000 && loosen > 10_000);
        }

        // ════════════ §3 경합 ════════════

        [Fact]
        public async Task ConcurrentWriters_SequenceOfWritesIsMonotonic()
        {
            var s = new MemStore();
            const string K = "Strategy.AdaptiveMaxBp";
            using var go = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
            {
                go.Wait();
                var r = new Random(t * 7919);
                for (int i = 0; i < 2_000; i++) W(s, K, r.Next(9_000, 10_001).ToString());
            })).ToArray();
            go.Set();
            await Task.WhenAll(tasks);
            var seq = s.Writes.Select(w => int.Parse(w.Value)).ToArray();
            _out.WriteLine($"[경합] 8 스레드 × 2,000 · 기입 {seq.Length} · 최종 {s.Db[K]}");
            for (int i = 1; i < seq.Length; i++) Assert.True(seq[i] > seq[i - 1], $"#{i} {seq[i - 1]} → {seq[i]}");
            Assert.Equal(seq[^1].ToString(), s.Db[K]);
        }

        /// <summary>첫 ReadStored 가 값을 읽은 '뒤' 해제 신호까지 멈추는 저장소(판독-기입 사이 경합 창을 결정론으로 연다).</summary>
        private sealed class PausingStore : ITightenSettingStore
        {
            public readonly MemStore Inner = new();
            public readonly ManualResetEventSlim FirstRead = new(false), Release = new(false);
            private int _reads;
            public string? ReadEnvironment(string key) => null;
            public string? ReadStored(string key)
            {
                string? v = Inner.ReadStored(key);
                if (Interlocked.Increment(ref _reads) == 1) { FirstRead.Set(); Release.Wait(5_000); }
                return v;
            }
            public void WriteStored(string key, string value) => Inner.WriteStored(key, value);
        }

        [Fact]
        public async Task Interleaving_StaleReadCannotOverwriteStricterValue()
        {
            // A: 9600 제안 · 9500 을 읽은 뒤 멈춤. B: 9800 제안. 잠금이 없으면 B 가 9500 을 읽고 9800 을 쓴 뒤 A 가 깨어나 9600 을 쓴다(= 완화).
            var st = new PausingStore();
            const string K = "Strategy.AdaptiveMaxBp";
            var a = Task.Run(() => TightenOnlyUiSettingWriter.TryWrite(K, "9600", st, PassAudit.Instance));
            Assert.True(st.FirstRead.Wait(5_000));
            var b = Task.Run(() => TightenOnlyUiSettingWriter.TryWrite(K, "9800", st, PassAudit.Instance));
            await Task.WhenAny(b, Task.Delay(300));                                        // 잠금 = B 는 A 가 끝날 때까지 진입 불가
            st.Release.Set();
            var va = await a; var vb = await b;
            _out.WriteLine($"[경합 창] A {va} · B {vb} · 기입 순서 {string.Join(" → ", st.Inner.Writes.Select(w => w.Value))} · 최종 {st.Inner.Db[K]}");
            Assert.Equal("9800", st.Inner.Db[K]);
            Assert.Equal(new[] { "9600", "9800" }, st.Inner.Writes.Select(w => w.Value).ToArray());
        }

        // ════════════ §4 어댑터 · 실 DB ════════════

        [Fact]
        public void Adapter_RealDb_TightenOnly_UiSetSettingStillBlocked_CodesMatchRcl()
        {
            string path = Path.Combine(Path.GetTempPath(), "ms2e_tighten_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var db = new LocalDatabase(path);
                using var ledger = StockManager.Security.MonotonicSettingAuditLog.CreateVolatile(new StrategyAnomalyInterlock());
                IAdminSettingWidgetPort port = new AdminSettingWidgetAdapter(db, ledger);
                Assert.True(port.TryGetTightenable("Strategy.SignalGateEnabled", out long g) && g == 0);
                Assert.True(port.TryGetTightenable("Strategy.AdaptiveMaxBp", out long m) && m == 9_500);
                Assert.False(port.TryGetTightenable("Dispatch.ValveWired", out _));
                port.SetSetting("Strategy.SignalGateEnabled", "true");                     // 일반 UI 기입 = 여전히 폐기(허용 목록 밖)
                Assert.Null(db.GetSetting("Strategy.SignalGateEnabled"));
                Assert.Equal(TightenVerdictCode.Tightened, port.TightenSetting("Strategy.SignalGateEnabled", "true"));
                Assert.Equal("true", db.GetSetting("Strategy.SignalGateEnabled"));
                Assert.Equal(TightenVerdictCode.Loosening, port.TightenSetting("Strategy.SignalGateEnabled", "false"));
                Assert.Equal("true", db.GetSetting("Strategy.SignalGateEnabled"));
                Assert.Equal(TightenVerdictCode.Tightened, port.TightenSetting("Strategy.AdaptiveMaxBp", "9800"));
                Assert.Equal(TightenVerdictCode.Loosening, port.TightenSetting("Strategy.AdaptiveMaxBp", "9000"));
                Assert.True(port.TryGetTightenable("Strategy.AdaptiveMaxBp", out m) && m == 9_800);
                Assert.Equal(TightenVerdictCode.UnknownKey, port.TightenSetting("Dispatch.ValveWired", "true"));
                Assert.Null(db.GetSetting("Dispatch.ValveWired"));
                // 하네스 래퍼도 실 어댑터로 위임
                var h = new HarnessSettingsPort(port);
                Assert.Equal(TightenVerdictCode.Loosening, h.TightenSetting("Strategy.AdaptiveMaxBp", "9700"));
                Assert.Equal(6, ledger.Count);                                                  // [v12.18.2-DEV] 시도 6건 전부 원장 기록(강화 2 · 거부 4)
                Assert.Equal(TightenVerdictCode.AuditFrozen, (int)TightenVerdict.AuditFrozen);
            }
            finally { try { File.Delete(path); } catch (IOException) { } }

            foreach (TightenVerdict v in Enum.GetValues<TightenVerdict>())
                Assert.NotEqual("?", TightenVerdictCode.Label((int)v));
            Assert.Equal(Enum.GetValues<TightenVerdict>().Length, TightenVerdictCode.Count);
            Assert.Equal(((int)TightenVerdict.Tightened, (int)TightenVerdict.Loosening, (int)TightenVerdict.EnvironmentOverride, (int)TightenVerdict.StoreFault),
                         (TightenVerdictCode.Tightened, TightenVerdictCode.Loosening, TightenVerdictCode.EnvironmentOverride, TightenVerdictCode.StoreFault));
        }

        // ════════════ §5 런타임 상향 전용 ════════════

        [Fact]
        public void RuntimeFloors_RaiseOnly_SwitchApplies_DryRunGateRejectsImmediately()
        {
            var cal = new VolatilityAdaptiveCalibrator();
            Assert.False(cal.RaiseMaxBp(9_000));
            Assert.True(cal.RaiseMaxBp(9_800));
            Assert.False(cal.RaiseMaxBp(9_700));
            Assert.Equal((9_800, 9_800, 9_150), (cal.MaxBp, cal.ThresholdFor(MicroRegime.Choppy), cal.ThresholdFor(MicroRegime.MeanReverting)));
            Assert.True(cal.RaiseMaxBp(20_000));
            Assert.Equal(10_000, cal.MaxBp);

            using var h = new MultiBrokerDryRunHarness(confidenceThreshold: 0.5);
            h.SetPhysicalKeys(true, true); h.SetCaps(60, 40);
            const long Sym = 5930, Mid = 70_000, Tick = 100;
            void Ramp()
            {
                for (int k = 0; k < 6; k++)
                {
                    double imb10 = -0.05 + 0.2 * Math.Min(k, 5);
                    long strong10 = (long)Math.Round(5_000 * (1 + imb10)), weak10 = 10_000 - strong10;
                    h.OnBook(Sym, Mid, 950, 50, (strong10 - 4_750) / 5, (weak10 - 250) / 5);
                    h.Advance(1_000);
                }
            }
            Ramp();
            Assert.True(h.Signals.TryGetSignal(Sym, out var sig) && sig.Confidence is > 0.6 and < 0.95, "확신 " + sig.Confidence);
            h.SubmitFromSignal(Sym, OrderSide.Buy, 10, Mid + Tick); h.Pump(); h.Advance(50_000);
            long placed = h.InnerPlaces;
            Assert.True(placed > 0);
            int floor = 5_000;
            var sw = new SignalGateRuntimeSwitch(h.Gate, h.Signals, () => true, () => true, confidenceFloorBp: () => floor);
            sw.Poll();
            Assert.Equal(5_000, h.Signals.ConfidenceFloorBp);
            floor = 9_600; sw.Poll();                                                      // UI 강화 기입 → 1초 판독 → 하한 상향
            Assert.Equal(9_600, h.Signals.ConfidenceFloorBp);
            floor = 6_000; sw.Poll();                                                      // 하향 판독 = 무시(실행 중 완화 0)
            Assert.Equal(9_600, h.Signals.ConfidenceFloorBp);
            Assert.Equal(2, sw.FloorRaises);
            Ramp();
            h.SubmitFromSignal(Sym, OrderSide.Buy, 10, Mid + Tick); h.Pump();
            Assert.Equal(placed, h.InnerPlaces);                                           // ★상향 즉시 전방 거부
            Assert.True(h.Signals.RejectedLowConfidence >= 1);
            Assert.False(h.Signals.RaiseConfidenceFloorBp(9_000));
        }

        // ════════════ §6 출하 IL 판독 ════════════

        private static string HostDll() => typeof(ReleasePackager).Assembly.Location;

        private static readonly OpCode[] One = new OpCode[0x100], Two = new OpCode[0x100];
        static TightenOnlySettingGuardTests()
        {
            foreach (var f in typeof(OpCodes).GetFields())
                if (f.GetValue(null) is OpCode op) { if (op.Size == 1) One[op.Value & 0xFF] = op; else Two[op.Value & 0xFF] = op; }
        }

        /// <summary>메서드 본문 IL 의 파일 오프셋 · IL 바이트 · (명령 IL 오프셋, 옵코드, 피연산자) 목록.</summary>
        internal static (int FileOffset, byte[] Il, List<(int At, OpCode Op, int Operand)> Ins) Body(byte[] dll, string type, string method)
        {
            using var pe = new PEReader(new MemoryStream(dll));
            var md = pe.GetMetadataReader();
            foreach (var mh in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(mh);
                if (md.GetString(m.Name) != method || md.GetString(md.GetTypeDefinition(m.GetDeclaringType()).Name) != type) continue;
                int rva = m.RelativeVirtualAddress;
                int file = RvaToFile(pe, rva);
                int header = (dll[file] & 0x3) == 0x2 ? 1 : ((dll[file + 1] >> 4) & 0xF) * 4;
                var il = pe.GetMethodBody(rva).GetILBytes()!;
                var ins = new List<(int, OpCode, int)>();
                for (int i = 0; i < il.Length;)
                {
                    int at = i;
                    OpCode op = il[i] == 0xFE ? Two[il[++i]] : One[il[i]];
                    i++;
                    int size = op.OperandType switch
                    {
                        OperandType.InlineNone => 0, OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i), _ => 4,
                    };
                    ins.Add((at, op, size == 4 ? BitConverter.ToInt32(il, i) : size == 1 ? il[i] : 0));
                    i += size;
                }
                return (file + header, il, ins);
            }
            throw new InvalidOperationException(type + "." + method + " 없음");
        }

        private static int RvaToFile(PEReader pe, int rva)
        {
            foreach (var s in pe.PEHeaders.SectionHeaders)
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData)) return rva - s.VirtualAddress + s.PointerToRawData;
            throw new InvalidOperationException("RVA 섹션 없음");
        }

        internal static int FieldDataFile(byte[] dll, string getter)
        {
            var (_, _, ins) = Body(dll, ShippedBinaryLiveArmedGuard.TightenWriterType, getter);
            var tok = ins.First(i => i.Op == OpCodes.Ldsflda || i.Op == OpCodes.Ldtoken).Operand;
            using var pe = new PEReader(new MemoryStream(dll));
            var f = pe.GetMetadataReader().GetFieldDefinition((FieldDefinitionHandle)MetadataTokens.EntityHandle(tok));
            return RvaToFile(pe, f.GetRelativeVirtualAddress());
        }

        internal static int FallbacksDataFile(byte[] dll)
        {
            var (_, _, ins) = Body(dll, ShippedBinaryLiveArmedGuard.TightenWriterType, ".cctor");
            using var pe = new PEReader(new MemoryStream(dll));
            var md = pe.GetMetadataReader();
            for (int k = 0; k < ins.Count; k++)
            {
                if (ins[k].Op != OpCodes.Stsfld) continue;
                var f = md.GetFieldDefinition((FieldDefinitionHandle)MetadataTokens.EntityHandle(ins[k].Operand));
                if (md.GetString(f.Name) != "Fallbacks") continue;
                int t = ins.FindLastIndex(k, i => i.Op == OpCodes.Ldtoken);
                var data = md.GetFieldDefinition((FieldDefinitionHandle)MetadataTokens.EntityHandle(ins[t].Operand));
                return RvaToFile(pe, data.GetRelativeVirtualAddress());
            }
            throw new InvalidOperationException("Fallbacks 초기화 없음");
        }

        private static ShippedBinaryLiveArmedGuard.Result ScanBytes(byte[] dll) { using var ms = new MemoryStream(dll); return ShippedBinaryLiveArmedGuard.Scan(ms); }

        [Fact]
        public void IlGuard_BuiltHost_Ok_SixKeys_HostSitesMatched_Ps51ZipAndCli()
        {
            var r = ShippedBinaryLiveArmedGuard.Scan(File.OpenRead(HostDll()));
            _out.WriteLine($"[IL] 조회 {r.Sites} · 강화 전용 키 {r.TightenKeys} · 호스트 판독 대조 {r.TightenHostSites} · 위반 {r.Violations.Count} {string.Join(" | ", r.Violations)}");
            Assert.True(r.Ok, string.Join(" | ", r.Violations));
            Assert.Equal(6, r.TightenKeys);
            Assert.True(r.TightenHostSites >= 8, "호스트 판독 지점 " + r.TightenHostSites);   // 기동 판독 6 + 1초 판독(게이트 · 확신 · 최대) — 전부 표와 일치

            string zip = Path.Combine(Path.GetTempPath(), "ms2e_tight_" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var z = ZipFile.Open(zip, ZipArchiveMode.Create)) using (var e = z.CreateEntry("host\\MS2E.StockSuite.dll").Open()) e.Write(File.ReadAllBytes(HostDll()));
                Assert.True(ShippedBinaryLiveArmedGuard.ScanZip(zip).Ok);
                var w = new StringWriter();
                Assert.Equal(ReleaseHandshakeCli.ExitOk, ReleaseHandshakeCli.Run(new[] { "--verify-live-armed", zip }, w));
                Assert.Contains("강화 전용 키 6", w.ToString());
                _out.WriteLine(w.ToString().TrimEnd());
            }
            finally { File.Delete(zip); }
        }

        [Fact]
        public void IlGuard_OneBytePatches_AllRejected()
        {
            byte[] dll = File.ReadAllBytes(HostDll());
            const string T = ShippedBinaryLiveArmedGuard.TightenWriterType;
            var cases = new List<(string Name, byte[] Bytes, string Expect)>();

            // ① IsTightening ≥ → ≤ (clt FE 04 → cgt FE 02) — NonDecreasing 키가 '내리기만' 허용으로 뒤집힘
            {
                var (off, il, ins) = Body(dll, T, "IsTightening");
                int k = ins.FindIndex(i => i.Op == OpCodes.Clt);
                Assert.True(k > 0);
                var b = (byte[])dll.Clone(); b[off + ins[k].At + 1] = 0x02;
                cases.Add(("clt→cgt", b, "IsTightening"));
            }
            // ② IsTightening 첫 명령 → ldc.i4.1; ret(무조건 통과)
            {
                var (off, _, _) = Body(dll, T, "IsTightening");
                var b = (byte[])dll.Clone(); b[off] = 0x17; b[off + 1] = 0x2A;
                cases.Add(("상수 true", b, "IsTightening"));
            }
            // ③ TryWrite: Judge 뒤 첫 brfalse.s → br.s(판정 무시 기입)
            {
                var (off, _, ins) = Body(dll, T, "TryWrite");
                int j = ins.FindIndex(i => (i.Op == OpCodes.Call) && i.Operand == JudgeToken(dll));
                int k = ins.FindIndex(j, i => i.Op.FlowControl is FlowControl.Cond_Branch or FlowControl.Branch);
                Assert.True(ins[k].Op == OpCodes.Brfalse_S, "예상 brfalse.s · 실제 " + ins[k].Op.Name);
                var b = (byte[])dll.Clone(); b[off + ins[k].At] = 0x2B;
                cases.Add(("brfalse.s→br.s", b, "TryWrite"));
            }
            // ④ 규칙 블롭: AdaptiveMaxBp(인덱스 3) NonDecreasing 2 → NonIncreasing 4
            {
                int f = FieldDataFile(dll, "get_Rules");
                Assert.Equal(2, dll[f + 3]);
                var b = (byte[])dll.Clone(); b[f + 3] = 4;
                cases.Add(("규칙 2→4", b, "규칙"));
            }
            // ⑤ 폴백 블롭: AdaptiveMaxBp 9500 → 5000(미설정 = 느슨한 현재값 오인)
            {
                int f = FallbacksDataFile(dll) + 3 * 4;
                Assert.Equal(9_500, BitConverter.ToInt32(dll, f));
                var b = (byte[])dll.Clone(); BitConverter.GetBytes(5_000).CopyTo(b, f);
                cases.Add(("폴백 9500→5000", b, "폴백"));
            }
            // ⑥ 진입점: TightenSetting 의 call TryWrite 토큰 → IndexOf(같은 형식 · 판정 없는 메서드)
            {
                var (off, _, ins) = Body(dll, "AdminSettingWidgetAdapter", "TightenSetting");
                int k = ins.FindIndex(i => i.Op == OpCodes.Call && i.Operand == MethodToken(dll, T, "TryWrite"));
                Assert.True(k >= 0);
                var b = (byte[])dll.Clone(); BitConverter.GetBytes(MethodToken(dll, T, "IndexOf")).CopyTo(b, off + ins[k].At + 1);
                cases.Add(("진입점 TryWrite 제거", b, "TryWrite 호출 0"));
            }
            foreach (var (name, bytes, expect) in cases)
            {
                var r = ScanBytes(bytes);
                _out.WriteLine($"[패치] {name} → {(r.Ok ? "Ok" : "Rejected")} · {string.Join(" | ", r.Violations.Where(v => v.Contains("Tighten", StringComparison.Ordinal)))}");
                Assert.False(r.Ok, name);
                Assert.Contains(r.Violations, v => v.Contains(expect, StringComparison.Ordinal));
            }
            Assert.True(ScanBytes(dll).Ok);                                                // 원본 무손상
        }

        internal static int MethodToken(byte[] dll, string type, string method)
        {
            using var pe = new PEReader(new MemoryStream(dll));
            var md = pe.GetMetadataReader();
            foreach (var mh in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(mh);
                if (md.GetString(m.Name) == method && md.GetString(md.GetTypeDefinition(m.GetDeclaringType()).Name) == type) return MetadataTokens.GetToken(mh);
            }
            throw new InvalidOperationException(method);
        }

        private static int JudgeToken(byte[] dll) => MethodToken(dll, ShippedBinaryLiveArmedGuard.TightenWriterType, "Judge");

        // ════════════ §7 δ=0 · 회귀 ════════════

        [Fact]
        public void JudgeLoop_ZeroAlloc()
        {
            int gate = TightenOnlyUiSettingWriter.IndexOf("Strategy.SignalGateEnabled"), max = TightenOnlyUiSettingWriter.IndexOf("Strategy.AdaptiveMaxBp");
            string cur = "9500";
            long Loop()
            {
                long acc = 0;
                for (int i = 0; i < 10_000; i++)
                {
                    acc += (int)TightenOnlyUiSettingWriter.Judge(max, "9600".AsSpan(), cur, out _, out _);
                    acc += (int)TightenOnlyUiSettingWriter.Judge(max, "-5".AsSpan(), cur, out _, out _);
                    acc += (int)TightenOnlyUiSettingWriter.Judge(gate, "TRUE".AsSpan(), "false", out _, out _);
                    acc += TightenOnlyUiSettingWriter.IndexOf("Strategy.ChurnHardPerMin");
                }
                return acc;
            }
            Loop();
            long alloc = MinAllocated(() => Loop());
            _out.WriteLine($"[δ=0] 판정 4만 회 {alloc}B");
            Assert.Equal(0, alloc);
        }

        [Fact]
        public void Regression_FiveBuilds_Ratchet_InterlockSpec_UiAllowlistUnchanged()
        {
            string root = RepoRoot();
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
            {
                Assert.Contains(proj, sln);
                Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", File.ReadAllText(Path.Combine(root, proj)));
            }
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
            Assert.Equal(30, Enum.GetValues<ArmingInterlockSource>().Length);
            foreach (var key in new[] { "Strategy.SignalGateEnabled", "Strategy.AdaptiveMaxBp", "Dispatch.ValveWired", "Canary.CapBps" })
                Assert.False(UiSettingWriteGuard.IsUiWritable(key), key);                 // 일반 UI 허용 목록은 그대로(강화 전용 경로만 추가)
            string page = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Pages", "Settings", "SettingsPage.razor"));
            Assert.Contains("Settings.TightenSetting(\"Strategy.SignalGateEnabled\", \"true\")", page);
            Assert.DoesNotContain("TightenSetting(\"Strategy.SignalGateEnabled\", \"false\")", page);
        }
    }
}
