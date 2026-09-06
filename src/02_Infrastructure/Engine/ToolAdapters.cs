// ============================================================================
// ToolAdapters — v12.0.0 Phase 94-P: 잔여 도구 페이지 Port 호스트 어댑터
//   TimeMachine/Research/Copilot/Cartridge 격리 + 공용 폴더열기(Process.Start). ★liveArmed:false: 도구/정보 전용.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Portfolio;   // PfMarket
using MS2E.UI.Portable.Ports.Tools;

namespace MS2E.Infrastructure
{
    // 공용: 폴더 열기(데스크톱 셸) — Process.Start(UseShellExecute) 격리(순수 UI 미접촉).
    public sealed class FolderOpenAdapter : IFolderOpenPort
    {
        public void OpenFolder(string path)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch { }
        }
    }

    // ① 타임머신 — 시세 fetch + TimeMachine.Replay 내재화.
    public sealed class TimeMachineAdapter : ITimeMachinePort
    {
        private readonly IQuoteProvider _prov;
        public TimeMachineAdapter(IQuoteProvider prov) => _prov = prov;

        public async Task<ITimeMachineResultView> ReplayAsync(string symbol, PfMarket market, DateTime asOf, int horizon)
        {
            var mkt = (Market)(int)market;
            var candles = await _prov.GetHistoryAsync(symbol, mkt, asOf.AddYears(-3), DateTime.Now);
            return TimeMachine.Replay(candles, asOf, mkt, horizon, symbol);
        }
    }

    // ② 퀀트 코파일럿 — CopilotStore(설정 영속) + CopilotEngine(순수) + 스냅샷 격리(어댑터가 전부 래핑).
    public sealed class CopilotAdapter : ICopilotPort
    {
        private readonly LocalDatabase _db;
        public CopilotAdapter(LocalDatabase db) => _db = db;

        public bool GetEnabled() => _db.GetSetting("Copilot.Enabled") == "true";
        public void SetEnabled(bool on) => _db.SetSetting("Copilot.Enabled", on ? "true" : "false");

        public IReadOnlyList<ICopilotPolicyView> GetPolicies()
            => CopilotStore.Load(_db).Select(p => new PolicyRow(p.Name, (int)p.Kind, p.Enabled)).ToList();

        public string AddFromNl(string nl)
        {
            var p = CopilotEngine.ParseGoal(nl);
            if (p == null) return "인식 실패 — '변동성/낙폭/레짐' 키워드 + 숫자로.";
            var list = CopilotStore.Load(_db); list.Add(p); CopilotStore.Save(_db, list);
            return $"추가됨: {p.Name}";
        }

        public string AddManual(string kind, double threshold)
        {
            var k = Enum.TryParse<PolicyKind>(kind, out var kk) ? kk : PolicyKind.VolTarget;
            var name = k switch { PolicyKind.VolTarget => $"변동성 {threshold:0}% 이하", PolicyKind.MaxDrawdown => $"낙폭 -{threshold:0}% 방어", _ => "레짐 변화 가드" };
            var list = CopilotStore.Load(_db); list.Add(new CopilotPolicy { Kind = k, Threshold = threshold, Name = name }); CopilotStore.Save(_db, list);
            return $"추가됨: {name}";
        }

        public void TogglePolicy(int index, bool enabled)
        {
            var list = CopilotStore.Load(_db);
            if (index >= 0 && index < list.Count) { list[index].Enabled = enabled; CopilotStore.Save(_db, list); }
        }

        public void RemovePolicy(int index)
        {
            var list = CopilotStore.Load(_db);
            if (index >= 0 && index < list.Count) { list.RemoveAt(index); CopilotStore.Save(_db, list); }
        }

        public (ICopilotCtxView ctx, IReadOnlyList<ICopilotProposalView> proposals) Evaluate()
        {
            try
            {
                var j = _db.GetSetting("Dashboard.Snapshots");
                var snaps = string.IsNullOrWhiteSpace(j) ? null : JsonSerializer.Deserialize<Dictionary<string, decimal>>(j);
                var equity = snaps?.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Value).ToList() ?? new();
                var ctx = CopilotEngine.BuildCtx(equity);
                var proposals = CopilotEngine.Evaluate(CopilotStore.Load(_db), ctx);
                return (new CtxRow(ctx.Valid, ctx.PortfolioVolPct, ctx.DrawdownPct, ctx.Regime.ToString()),
                        proposals.Select(pr => new ProposalRow(pr.Title, pr.Detail)).ToList());
            }
            catch { return (new CtxRow(false, 0, 0, ""), Array.Empty<ICopilotProposalView>()); }
        }

        private sealed record PolicyRow(string Name, int Kind, bool Enabled) : ICopilotPolicyView;
        private sealed record ProposalRow(string Title, string Detail) : ICopilotProposalView;
        private sealed record CtxRow(bool Valid, double PortfolioVolPct, double DrawdownPct, string Regime) : ICopilotCtxView;
    }

    // ③ 야간 연구 일지 — NightlyResearch(정적) + 설정 격리.
    public sealed class ResearchAdapter : IResearchPort
    {
        private readonly LocalDatabase _db;
        private readonly IQuoteProvider _prov;
        public ResearchAdapter(LocalDatabase db, IQuoteProvider prov) { _db = db; _prov = prov; }

        public (bool enabled, bool telegram, int hour, double goal) LoadSettings()
        {
            bool en = _db.GetSetting("Research.Enabled") == "true";
            bool tg = _db.GetSetting("Research.Telegram") == "true";
            int hr = int.TryParse(_db.GetSetting("Research.Hour"), out var h) ? Math.Clamp(h, 0, 23) : 5;
            double g = double.TryParse(_db.GetSetting("Research.GoalPct"), out var gg) && gg != 0 ? gg : 10;
            return (en, tg, hr, g);
        }

        public void SaveSettings(bool enabled, bool telegram, int hour, double goal)
        {
            hour = Math.Clamp(hour, 0, 23);
            _db.SetSetting("Research.Enabled", enabled ? "true" : "false");
            _db.SetSetting("Research.Telegram", telegram ? "true" : "false");
            _db.SetSetting("Research.Hour", hour.ToString());
            _db.SetSetting("Research.GoalPct", goal.ToString("0.###"));
        }

        public bool IsRunning => NightlyResearch.IsRunning;
        public IResearchBriefView? LoadLatest() => NightlyResearch.LoadLatest(_db);
        public IReadOnlyList<IResearchBriefView> LoadHistory() => NightlyResearch.LoadHistory(_db);   // 공변
        public async Task<IResearchBriefView?> RunAsync(double goal, CancellationToken ct)
            => await NightlyResearch.RunAsync(_db, _prov, goal, ct: ct);
    }

    // ④ 전략 카트리지 — CartridgeService(서명/파일/매크로) + CartridgeEngine(순수) 격리. ★상태(드래프트·가져온 카트리지) 보유 → scoped.
    public sealed class CartridgeAdapter : ICartridgePort
    {
        private readonly LocalDatabase _db;
        private readonly List<CartStrategy> _drafts = new();
        private Cartridge? _imported;
        public CartridgeAdapter(LocalDatabase db) => _db = db;

        public string EnsureIdentity() => CartridgeService.EnsureIdentity(_db);
        public string Author() => CartridgeService.Author(_db);
        public int MacroCount() => CartridgeService.Macros(_db).Count;
        public IReadOnlyList<string> ListFiles() => CartridgeService.ListFiles();

        public IReadOnlyList<ICartStrategyView> Drafts => _drafts.Select(s => Row(s, reproduced: true)).ToList();

        public string AddDraft(int shortMa, int longMa, int seed)
        {
            if (shortMa < 1 || longMa <= shortMa) return "⚠️ 단기<장기, 단기≥1 이어야 합니다.";
            _drafts.Add(CartridgeEngine.Attest($"MA {shortMa}/{longMa}", shortMa, longMa, seed));
            return "";
        }

        public void RemoveDraft(int index) { if (index >= 0 && index < _drafts.Count) _drafts.RemoveAt(index); }

        public string Export(string name, string desc)
        {
            if (_drafts.Count == 0 && CartridgeService.Macros(_db).Count == 0) return "⚠️ 전략 또는 매크로가 하나 이상 필요합니다.";
            try
            {
                var cart = CartridgeService.BuildSigned(_db, name, desc, _drafts.ToList());
                var path = CartridgeService.ExportToFile(cart);
                _drafts.Clear();
                return $"✅ 서명·내보내기 완료: {System.IO.Path.GetFileName(path)}";
            }
            catch (Exception ex) { return "내보내기 실패: " + ex.Message; }
        }

        public (ICartridgeInfoView? cart, ICartridgeVerdictView? verdict, string err) Import(string file)
        {
            _imported = null;
            if (string.IsNullOrEmpty(file)) return (null, null, "");
            if (!CartridgeService.TryImport(file, out var cart, out var err) || cart is null) return (null, null, "❌ " + err);
            _imported = cart;
            var v = CartridgeEngine.Verify(cart);
            return (BuildInfo(cart), new VerdictRow(v.SignatureOk, v.AttestationOk, v.FullyTrusted, v.Note), "");
        }

        public string ApplyMacros()
        {
            if (_imported is null) return "";
            if (!CartridgeEngine.Verify(_imported).SignatureOk) return "";
            var existing = CartridgeService.MacroMap(_db);
            int over = _imported.Macros.Count(m => existing.ContainsKey(m.Alias));
            CartridgeService.ApplyMacros(_db, _imported.Macros);
            return $"✅ 매크로 {_imported.Macros.Count}개 적용" + (over > 0 ? $" (기존 {over}개 덮어씀)" : "")
                 + " — Ctrl+K에서 alias로 사용. 매크로는 읽기전용 내비게이션만 수행합니다.";
        }

        public void OpenFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(CartridgeService.Dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = CartridgeService.Dir, UseShellExecute = true });
            }
            catch { }
        }

        private ICartridgeInfoView BuildInfo(Cartridge c) => new InfoRow(
            c.Name, c.Author, c.CreatedIso, c.AuthorPubKey,
            c.Strategies.Select(s => Row(s, CartridgeEngine.VerifyStrategy(s))).ToList(),
            c.Macros.Select(m => m.Alias).ToList(), c.Macros.Count);

        private static ICartStrategyView Row(CartStrategy s, bool reproduced) =>
            new StratRow(s.Name, s.Seed, s.AttestTotalReturnPct, s.AttestMaxDrawdownPct, s.AttestTrades, reproduced);

        private sealed record StratRow(string Name, int Seed, double AttestTotalReturnPct, double AttestMaxDrawdownPct, int AttestTrades, bool Reproduced) : ICartStrategyView;
        private sealed record VerdictRow(bool SignatureOk, bool AttestationOk, bool FullyTrusted, string Note) : ICartridgeVerdictView;
        private sealed record InfoRow(string Name, string Author, string CreatedIso, string AuthorPubKey,
            IReadOnlyList<ICartStrategyView> Strategies, IReadOnlyList<string> MacroAliases, int MacroCount) : ICartridgeInfoView;
    }
}
