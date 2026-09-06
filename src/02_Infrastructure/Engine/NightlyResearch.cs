// ============================================================================
// NightlyResearch - 자율 야간 연구 루프 (Season 8 #2) · "The Terminal Dreams"
//  사용자가 잠든 새벽, 터미널이 스스로 한 바퀴 연구를 돈다:
//   ① 발굴   보유 종목마다 StrategyForge로 워크포워드 OOS 전략 자가 발견
//   ② 서명   발굴 전략을 CartridgeEngine.Attest → CartridgeService로 자가서명 카트리지(.ms2cart)
//   ③ 투영   각 종목 과거 수익률로 FutureCone 확률 미래(1개월 기대·손실확률·목표달성확률)
//   ④ 평가   CopilotEngine로 현재 포트 리스크 자세(변동성·낙폭·레짐) 점검 → 행동 '제안'
//   ⑤ 기록   요약을 시스템 알림으로 발생 → Alerts 테이블 → 인과 저널 타임라인(🔔)에 자동 적재
//   ⑥ 브리핑 아침 일지(/research) + (옵트인) 텔레그램 전송
//  ⚠️ 게이팅: 발굴·서명·투영·평가는 모두 '제안/읽기전용'. 실주문/DCA 절대 미접촉(하드 라인).
//  순수 헬퍼(DailyReturns·Build·ToStrategies·Headline·ComposeText)는 네트워크 0·결정론 → 단위 테스트 가능.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    // 한 종목의 밤사이 발견(최고 전략) + 미래 투영 요약
    public sealed record ResearchFinding(
        string Symbol, string Name, int Short, int Long,
        double OosMeanPct, double OosStdPct, double Score, int Trades, double FullReturnPct,
        bool HasCone, double ProjExpPct, double ProjP5Pct, double ProjP95Pct, double ProbLossPct, double GoalHitPct)
        : MS2E.UI.Portable.Ports.Tools.IResearchFindingView;   // Phase 94-P: 공변(Symbol/Name/Short/Long/OosMean/OosStd/Score/Trades/HasCone/ProjExp/ProbLoss/GoalHit 암시)

    // 하룻밤 전체 결과(영속화 + /research 표시)
    public sealed record ResearchBrief(
        string GeneratedIso, int Scanned, int Found, double GoalPct, string CartridgeFile,
        IReadOnlyList<ResearchFinding> Findings, IReadOnlyList<string> Proposals, string Headline, string Note)
        : MS2E.UI.Portable.Ports.Tools.IResearchBriefView   // Phase 94-P: 공변(Proposals[동일형]·GeneratedIso/Scanned/Found/GoalPct/CartridgeFile/Headline 암시)
    {
        IReadOnlyList<MS2E.UI.Portable.Ports.Tools.IResearchFindingView>
            MS2E.UI.Portable.Ports.Tools.IResearchBriefView.Findings => Findings;   // 명시적 공변(동명 반환형)
    }

    public static class NightlyResearch
    {
        public const int Horizon = 21;       // ≈ 1개월(거래일)
        public const int Sims = 500;         // 결정론 시드 고정
        public const int Seed = 12345;       // FuturePage와 동일
        public const int AttestSeed = 7;     // ForgePage와 동일(카트리지 자가서명 기준 시드)
        public const int HistoryCap = 30;    // 일지 보관 상한

        // 정직성 면책(일지·텔레그램·알림 공용) — 확률 추정/자가증명/합성기준임을 일관 고지
        public const string Disclaimer =
            "투영은 블록 부트스트랩 확률 추정이며 예측·투자 권유가 아닙니다. 카트리지 증명 수치는 합성 기준 시리즈에 대한 자가증명(실종목 OOS 아님)이며 받는 쪽이 재실행해 대조할 수 있습니다.";

        // 스케줄러(타이머·UI 스레드)와 페이지 '지금 실행'이 같은 진입점을 쓰므로 동시 실행을 차단하는 공용 게이트.
        private static readonly SemaphoreSlim _gate = new(1, 1);
        public static bool IsRunning => _gate.CurrentCount == 0;

        // ===== 순수 헬퍼 (네트워크 0 · 결정론 · 테스트 대상) =====

        /// <summary>일봉 종가 → 일수익률(소수, FutureCone 입력 규약 1+r). 표본 부족 시 빈 배열.</summary>
        public static double[] DailyReturns(IReadOnlyList<Candle> candles)
        {
            if (candles == null || candles.Count < 2) return Array.Empty<double>();
            var r = new double[candles.Count - 1];
            for (int i = 1; i < candles.Count; i++)
            {
                double prev = (double)candles[i - 1].Close;
                r[i - 1] = prev != 0 ? (double)candles[i].Close / prev - 1.0 : 0;
            }
            return r;
        }

        /// <summary>Forge 결과(최고 후보) + 선택적 FutureCone 투영 → 한 종목 발견. 유효 후보 없으면 null.</summary>
        public static ResearchFinding? Build(string symbol, string name, ForgeResult forge, FutureConeResult? cone)
        {
            if (forge == null || !forge.IsValid || forge.Ranked.Count == 0) return null;
            var c = forge.Ranked[0];
            bool hc = cone is { IsValid: true };
            return new ResearchFinding(symbol, name, c.Short, c.Long,
                c.OosMeanPct, c.OosStdPct, c.Score, c.Trades, c.FullReturnPct,
                hc, hc ? cone!.ExpReturnPct : 0, hc ? cone!.P5ReturnPct : 0, hc ? cone!.P95ReturnPct : 0,
                hc ? cone!.ProbLossPct : 0, hc ? cone!.GoalHitPct : 0);
        }

        /// <summary>발견들을 (Short,Long) 중복 제거 후 자가서명용 CartStrategy로 증명(Attest). 결정론.</summary>
        public static List<CartStrategy> ToStrategies(IEnumerable<ResearchFinding> findings)
        {
            var seen = new HashSet<(int, int)>();
            var list = new List<CartStrategy>();
            foreach (var f in findings ?? Enumerable.Empty<ResearchFinding>())
            {
                if (f.Short < 1 || f.Long <= f.Short) continue;
                if (!seen.Add((f.Short, f.Long))) continue;
                list.Add(CartridgeEngine.Attest($"MA {f.Short}/{f.Long}", f.Short, f.Long, AttestSeed));
            }
            return list;
        }

        /// <summary>한 줄 헤드라인(인과 저널 알림·일지 요약).</summary>
        public static string Headline(IReadOnlyList<ResearchFinding> findings, int scanned)
        {
            if (findings == null || findings.Count == 0)
                return $"{scanned}종목 스캔 — 유효한 신규 전략 발견 없음";
            var t = findings[0];
            string cone = t.HasCone ? $" · 1개월 기대(확률추정) {t.ProjExpPct.ToString("+0.0;-0.0")}%(손실확률 {t.ProbLossPct:0}%)" : "";
            return $"{scanned}종목 스캔 · {findings.Count}개 전략 발굴 — 최고 {t.Name} MA{t.Short}/{t.Long} " +
                   $"(과거 OOS {t.OosMeanPct.ToString("+0.0;-0.0")}%·점수 {t.Score:0.0}){cone}";
        }

        /// <summary>아침 브리핑/텔레그램용 한국어 본문(상위 8건 + 제안 + 카트리지).</summary>
        public static string ComposeText(ResearchBrief b)
        {
            var sb = new StringBuilder();
            string ts = DateTime.TryParse(b.GeneratedIso, out var dt) ? dt.ToString("yyyy-MM-dd HH:mm") : b.GeneratedIso;
            sb.AppendLine($"🌙 밤사이 연구 — {ts}");
            sb.AppendLine($"보유 {b.Scanned}종목 스캔 · {b.Found}개 전략 발굴 (목표 {b.GoalPct:0}%)");
            sb.AppendLine();
            if (b.Findings.Count == 0) sb.AppendLine("유효한 신규 전략을 찾지 못했습니다.");
            else
            {
                sb.AppendLine("◆ 발굴 전략(상위)");
                foreach (var f in b.Findings.Take(8))
                {
                    string cone = f.HasCone
                        ? $" → 1개월 기대(확률추정) {f.ProjExpPct.ToString("+0.0;-0.0")}%·손실 {f.ProbLossPct:0}%·목표달성 {f.GoalHitPct:0}%"
                        : "";
                    sb.AppendLine($"· {f.Name}({f.Symbol}) MA{f.Short}/{f.Long} 과거OOS {f.OosMeanPct.ToString("+0.0;-0.0")}%±{f.OosStdPct:0.0} 점수 {f.Score:0.0} 거래 {f.Trades}{cone}");
                }
            }
            if (b.Proposals.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("◆ 코파일럿 제안");
                foreach (var p in b.Proposals) sb.AppendLine($"· {p}");
            }
            if (!string.IsNullOrEmpty(b.CartridgeFile))
            {
                sb.AppendLine();
                sb.AppendLine($"◆ 자가서명 카트리지 생성: {System.IO.Path.GetFileName(b.CartridgeFile)}");
            }
            sb.AppendLine();
            sb.AppendLine("ℹ️ 제안·읽기전용 연구이며 자동 주문은 없습니다(게이팅).");
            sb.AppendLine("ℹ️ " + Disclaimer);
            return sb.ToString();
        }

        // ===== IO 오케스트레이션 (스케줄러/페이지 공용) =====

        /// <summary>한 바퀴 야간 연구. 발굴→서명→투영→평가→영속화→인과저널 적재. 텔레그램 전송은 호출측 책임.
        /// 이미 실행 중이면(타이머·버튼 동시 진입) 즉시 null 반환 — 중복 실행/이중 기록 방지.</summary>
        public static async Task<ResearchBrief?> RunAsync(LocalDatabase db, IQuoteProvider provider,
            double goalPct = 10.0, int maxSymbols = 25, CancellationToken ct = default)
        {
            if (!await _gate.WaitAsync(0, ct)) return null;   // 이미 진행 중 → 중복 실행 거부(스케줄러/버튼 공유 게이트)
            try { return await RunCoreAsync(db, provider, goalPct, maxSymbols, ct); }
            finally { _gate.Release(); }
        }

        private static async Task<ResearchBrief> RunCoreAsync(LocalDatabase db, IQuoteProvider provider,
            double goalPct, int maxSymbols, CancellationToken ct)
        {
            var holdings = db.GetAllHoldings()
                .Where(h => h.Market != Market.CRYPTO && !string.IsNullOrWhiteSpace(h.Symbol))
                .GroupBy(h => h.Symbol, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Take(Math.Clamp(maxSymbols, 1, 50)).ToList();

            var findings = new List<ResearchFinding>();
            int scanned = 0;
            foreach (var h in holdings)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var candles = (await provider.GetHistoryAsync(h.Symbol, h.Market,
                            DateTime.Now.AddMonths(-18), DateTime.Now, ct))
                        .OrderBy(c => c.Date).ToList();
                    scanned++;
                    if (candles.Count < 120) continue;                 // Forge 최소 표본
                    // CPU 집약(격자×폴드 백테스트 + 부트스트랩)은 UI 스레드 점유 방지를 위해 오프로드. 결정론 불변.
                    var (forge, cone) = await Task.Run(() =>
                    {
                        var fr = StrategyForge.Run(candles);
                        var rs = DailyReturns(candles);
                        FutureConeResult? cn = fr.IsValid && rs.Length >= 20
                            ? FutureCone.Project(rs, Horizon, Sims, Seed, goalPct) : null;
                        return (fr, cn);
                    }, ct);
                    if (!forge.IsValid) continue;
                    var f = Build(h.Symbol, string.IsNullOrWhiteSpace(h.Name) ? h.Symbol : h.Name, forge, cone);
                    if (f != null) findings.Add(f);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Warn($"[Research] {h.Symbol} 실패: {ex.Message}"); }
                await Task.Delay(150, ct);                              // API 한도 보호(옵티마이저와 동일)
            }

            findings = findings.OrderByDescending(f => f.Score).ToList();

            // ② 자가서명 카트리지
            string cartFile = "";
            try
            {
                var strategies = ToStrategies(findings);
                if (strategies.Count > 0)
                {
                    var today = DateTime.Now.ToString("yyyy-MM-dd");
                    var cart = CartridgeService.BuildSigned(db, $"야간연구 {today}",
                        $"자율 야간 연구가 보유 {scanned}종목에서 발굴한 {strategies.Count}개 MA 전략 — 자가서명. " +
                        "증명 수치는 결정론 합성 기준 시리즈 기준이며 실종목 OOS 성과가 아님(받는 쪽이 재실행해 대조 가능).",
                        strategies);
                    cartFile = CartridgeService.ExportToFile(cart);
                }
            }
            catch (Exception ex) { Log.Warn($"[Research] 카트리지 서명 실패: {ex.Message}"); }

            // ④ 코파일럿 평가(현재 리스크 자세) — Dashboard.Snapshots 기반(네트워크 0)
            var proposals = new List<string>();
            try
            {
                var policies = CopilotStore.Load(db);
                var j = db.GetSetting("Dashboard.Snapshots");
                if (policies.Count > 0 && !string.IsNullOrWhiteSpace(j))
                {
                    var snaps = JsonSerializer.Deserialize<Dictionary<string, decimal>>(j);
                    if (snaps != null && snaps.Count >= 3)
                    {
                        var equity = snaps.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Value).ToList();
                        var ctx = CopilotEngine.BuildCtx(equity);
                        foreach (var p in CopilotEngine.Evaluate(policies, ctx))
                            proposals.Add($"{p.Title} — {p.Detail}");
                    }
                }
            }
            catch (Exception ex) { Log.Warn($"[Research] 코파일럿 평가 실패: {ex.Message}"); }

            var brief = new ResearchBrief(
                DateTime.Now.ToString("o"), scanned, findings.Count, goalPct, cartFile,
                findings, proposals, Headline(findings, scanned),
                "자율 야간 연구 — 발굴·서명·투영·평가는 제안/읽기전용이며 자동 주문 없음(게이팅)");

            // ⑤ 영속화 + 인과 저널 적재 + 당일 게이트 공유 마킹(스케줄러·버튼 공통 — 같은 날 1회)
            try { db.SetSetting("Research.Brief", JsonSerializer.Serialize(brief)); } catch { }
            try
            {
                var hist = LoadHistory(db);
                hist.Insert(0, brief);
                if (hist.Count > HistoryCap) hist = hist.Take(HistoryCap).ToList();
                db.SetSetting("Research.History", JsonSerializer.Serialize(hist));
            }
            catch { }
            try { db.SetSetting("Research.LastRun", DateTime.Now.ToString("yyyy-MM-dd")); } catch { }
            try { AlertCenter.Instance.RaiseSystem("🌙 밤사이 연구 완료", brief.Headline + "  (확률 추정·제안일 뿐, 예측/자동 주문 아님)", AlertLevel.Info); }
            catch { }

            return brief;
        }

        public static ResearchBrief? LoadLatest(LocalDatabase db)
        {
            try
            {
                var j = db.GetSetting("Research.Brief");
                if (!string.IsNullOrWhiteSpace(j)) return JsonSerializer.Deserialize<ResearchBrief>(j);
            }
            catch { }
            return null;
        }

        public static List<ResearchBrief> LoadHistory(LocalDatabase db)
        {
            try
            {
                var j = db.GetSetting("Research.History");
                if (!string.IsNullOrWhiteSpace(j))
                    return JsonSerializer.Deserialize<List<ResearchBrief>>(j) ?? new();
            }
            catch { }
            return new();
        }
    }
}
