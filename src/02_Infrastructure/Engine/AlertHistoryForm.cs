// ============================================================================
// AlertHistoryForm - 알림 발생 이력 (필터 / 검색 / CSV) (v3.2.5)
//  · AlertCenter DB 이력을 레벨·소스 필터 + 텍스트 검색으로 조회, CSV 내보내기.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MS2E.Infrastructure
{
    public class AlertHistoryForm : Form
    {
        private readonly LocalDatabase _db;
        private readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private readonly ComboBox _source = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        private readonly TextBox _search = new() { Width = 180 };
        private DataGridView _grid = null!;
        private Label _status = null!;
        private List<AlertItem> _all = new();

        public AlertHistoryForm(LocalDatabase db)
        {
            _db = db;
            Text = "📜 알림 발생 이력";
            Size = new Size(940, 580);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("맑은 고딕", 10F);
            InitUI();
            ApplyTheme();
            ThemeManager.Changed += ApplyTheme;
            WindowMemory.Attach(this, db, "AlertHistory");
            FormClosing += (s, e) => ThemeManager.Changed -= ApplyTheme;
            Load += (s, e) => Reload();
        }

        private void InitUI()
        {
            var tb = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Renderer = new ToolStripProfessionalRenderer() };
            _level.Items.AddRange(new object[] { "전체 레벨", "Info", "Warning", "Critical" }); _level.SelectedIndex = 0;
            _source.Items.AddRange(new object[] { "전체 소스", "Price", "News", "Signal", "Dividend", "System" }); _source.SelectedIndex = 0;
            _level.SelectedIndexChanged += (s, e) => ApplyFilter();
            _source.SelectedIndexChanged += (s, e) => ApplyFilter();
            _search.TextChanged += (s, e) => ApplyFilter();
            var bRefresh = new ToolStripButton("🔄 새로고침");
            var bCsv = new ToolStripButton("⬇ CSV");
            bRefresh.Click += (s, e) => Reload();
            bCsv.Click += (s, e) => CsvExporter.ExportGrid(_grid, $"alert_history_{DateTime.Now:yyyyMMdd_HHmm}");
            tb.Items.AddRange(new ToolStripItem[]
            {
                new ToolStripLabel("레벨:"), new ToolStripControlHost(_level),
                new ToolStripLabel(" 소스:"), new ToolStripControlHost(_source),
                new ToolStripLabel(" 검색:"), new ToolStripControlHost(_search),
                new ToolStripSeparator(), bRefresh, bCsv
            });

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, ReadOnly = true, MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowTemplate = { Height = 28 }
            };
            _grid.Columns.Add("time", "시간");
            _grid.Columns.Add("level", "레벨");
            _grid.Columns.Add("source", "소스");
            _grid.Columns.Add("symbol", "종목");
            _grid.Columns.Add("title", "제목");
            _grid.Columns.Add("msg", "메시지");
            _grid.Columns["title"].FillWeight = 150;
            _grid.Columns["msg"].FillWeight = 260;
            ThemeManager.StyleGrid(_grid);

            var ss = new StatusStrip { SizingGrip = false };
            _status = new Label { Text = "", ForeColor = Color.Gold, AutoSize = true };
            ss.Items.Add(new ToolStripControlHost(_status));

            Controls.Add(_grid);
            Controls.Add(ss);
            Controls.Add(tb);
        }

        private void Reload()
        {
            try { _all = AlertCenter.Instance.GetFromDb(500); } catch { _all = new(); }
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            IEnumerable<AlertItem> q = _all;
            if (_level.SelectedIndex > 0 && Enum.TryParse<AlertLevel>(_level.Text, out var lv)) q = q.Where(a => a.Level == lv);
            if (_source.SelectedIndex > 0 && Enum.TryParse<AlertSource>(_source.Text, out var src)) q = q.Where(a => a.Source == src);
            var kw = _search.Text.Trim();
            if (kw.Length > 0)
                q = q.Where(a => (a.Title?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false)
                              || (a.Message?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false)
                              || (a.Symbol?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false));

            var list = q.OrderByDescending(a => a.Time).ToList();
            _grid.Rows.Clear();
            foreach (var a in list)
            {
                int i = _grid.Rows.Add(a.Time.ToString("MM-dd HH:mm:ss"), LevelKr(a.Level),
                    a.Source.ToString(), a.Symbol, a.Title, a.Message);
                var c = a.Level == AlertLevel.Critical ? ThemeManager.Down
                      : a.Level == AlertLevel.Warning ? ThemeManager.Up : ThemeManager.SubText;
                _grid.Rows[i].Cells["level"].Style.ForeColor = c;
            }
            _status.Text = $"총 {_all.Count}건 중 {list.Count}건 표시";
        }

        private static string LevelKr(AlertLevel l) => l switch
        { AlertLevel.Critical => "🔴 중요", AlertLevel.Warning => "🟡 경고", _ => "ℹ️ 정보" };

        private void ApplyTheme()
        {
            if (IsDisposed) return;
            ThemeManager.Apply(this);
            ThemeManager.StyleGrid(_grid);
            Invalidate(true);
        }
    }
}
