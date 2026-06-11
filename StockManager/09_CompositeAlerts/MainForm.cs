// ============================================================================
// CompositeAlerts.MainForm - 복합 알림 관리 + 평가 러너 (v3.2)
//  · 규칙 추가/편집/삭제/사용, 15초마다 업비트 데이터로 평가 → 충족 시 알림.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using StockManager.Common;

namespace StockManager.CompositeAlerts
{
    public class MainForm : Form
    {
        private readonly LocalDatabase _db;
        private readonly CompositeAlertService _svc = CompositeAlertService.Instance;
        private List<CompositeRule> _rules => _svc.Rules;   // 단일 소스
        private DataGridView _grid = null!;
        private Label _status = null!;

        public MainForm(LocalDatabase db, IQuoteProvider? provider = null)
        {
            _db = db;
            _svc.Start(db, provider);   // 이미 기동돼 있으면 무시(idempotent)
            InitUI();
            ApplyTheme();
            ThemeManager.Changed += ApplyTheme;
            WindowMemory.Attach(this, db, "CompositeAlerts");
            _svc.Evaluated += OnEvaluated;
            Load += async (s, e) => { RebuildGrid(); await _svc.EvaluateNowAsync(); };
            FormClosing += (s, e) => { _svc.Evaluated -= OnEvaluated; ThemeManager.Changed -= ApplyTheme; };
        }

        private void OnEvaluated()
        {
            if (IsDisposed) return;
            try { if (InvokeRequired) { BeginInvoke(new Action(RefreshStatus)); return; } RefreshStatus(); } catch { }
        }

        private void RefreshStatus()
        {
            RebuildGrid();
            _status.Text = $"🕒 {_svc.LastRun:HH:mm:ss} · 규칙 {_rules.Count}개 · 활성 {_rules.Count(r => r.Enabled)} · 최근 발생 {_svc.LastFiredCount} · (백그라운드 상시 평가)";
        }

        private void InitUI()
        {
            Text = "🧠 스마트 복합 알림";
            Size = new Size(900, 560);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("맑은 고딕", 10F);

            var tb = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Renderer = new ToolStripProfessionalRenderer() };
            var bAdd = new ToolStripButton("➕ 규칙 추가");
            var bEdit = new ToolStripButton("✏️ 편집");
            var bDel = new ToolStripButton("🗑 삭제");
            var bTog = new ToolStripButton("▶/⏸ 사용 전환");
            var bEval = new ToolStripButton("🔄 지금 평가");
            var lblInt = new ToolStripLabel("주기(초):");
            var numInt = new NumericUpDown { Minimum = 10, Maximum = 600, Value = Math.Clamp(_svc.IntervalSec, 10, 600), Width = 60 };
            numInt.ValueChanged += (s, e) => _svc.SetInterval((int)numInt.Value);
            tb.Items.AddRange(new ToolStripItem[] { bAdd, bEdit, bDel, bTog, new ToolStripSeparator(), bEval, new ToolStripSeparator(), lblInt, new ToolStripControlHost(numInt) });
            bAdd.Click += (s, e) => EditRule(null);
            bEdit.Click += (s, e) => EditRule(Selected());
            bDel.Click += (s, e) => { var r = Selected(); if (r != null) { _rules.Remove(r); Persist(); } };
            bTog.Click += (s, e) => { var r = Selected(); if (r != null) { r.Enabled = !r.Enabled; Persist(); } };
            bEval.Click += async (s, e) => await _svc.EvaluateNowAsync();

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, ReadOnly = true, MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowTemplate = { Height = 30 }
            };
            _grid.Columns.Add("on", "사용");
            _grid.Columns.Add("name", "이름");
            _grid.Columns.Add("target", "대상");
            _grid.Columns.Add("cond", "조건");
            _grid.Columns.Add("fired", "마지막 발생");
            _grid.Columns["cond"].FillWeight = 200;
            _grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) EditRule(Selected()); };
            ThemeManager.StyleGrid(_grid);

            var ss = new StatusStrip { SizingGrip = false };
            _status = new Label { Text = "준비", ForeColor = Color.Gold, AutoSize = true };
            ss.Items.Add(new ToolStripControlHost(_status));

            Controls.Add(_grid);
            Controls.Add(ss);
            Controls.Add(tb);
        }

        private CompositeRule? Selected() =>
            _grid.CurrentRow?.Tag is CompositeRule r ? r : null;

        private void RebuildGrid()
        {
            _grid.Rows.Clear();
            foreach (var r in _rules)
            {
                var cond = string.Join(r.Logic == RuleLogic.And ? "  AND  " : "  OR  ",
                    r.Conditions.Select(x => x.Describe()));
                int i = _grid.Rows.Add(r.Enabled ? "✅" : "⏸", r.Name,
                    (r.AssetKind == "stock" ? "📈 " : "🪙 ") + r.Target, cond,
                    r.LastFired?.ToString("MM-dd HH:mm") ?? "-");
                _grid.Rows[i].Tag = r;
            }
        }

        private void Persist() { _svc.Persist(); RebuildGrid(); }

        private void EditRule(CompositeRule? rule)
        {
            bool isNew = rule == null;
            rule ??= new CompositeRule { Conditions = { new CompositeCondition { Metric = CondMetric.MaGapPct, Op = CondOp.Gte, Threshold = 0 } } };
            using var dlg = new RuleDialog(rule);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (isNew) _rules.Add(rule);
            Persist();
        }

        private void ApplyTheme()
        {
            if (IsDisposed) return;
            ThemeManager.Apply(this);
            ThemeManager.StyleGrid(_grid);
            Invalidate(true);
        }
    }
}
