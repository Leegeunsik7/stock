// ============================================================================
// AlertCenterForm - 통합 알림 센터 UI (기능 A)
// ============================================================================
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MS2E.Infrastructure
{
    public class AlertCenterForm : Form
    {
        private readonly LocalDatabase _db;
        private DataGridView _grid = null!;
        private ToolStrip _toolbar = null!;
        private ComboBox _filterCombo = null!;
        private Label _summary = null!;

        public AlertCenterForm(LocalDatabase db)
        {
            _db = db;
            InitializeUI();
            LoadAlerts();
            AlertCenter.Instance.AlertRaised += OnAlertRaised;
            FormClosing += (s, e) => AlertCenter.Instance.AlertRaised -= OnAlertRaised;
        }

        private void InitializeUI()
        {
            Text = "🔔 알림 센터";
            Size = new Size(1000, 700);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 28, 36);
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 9F);

            _toolbar = new ToolStrip
            {
                BackColor = Color.FromArgb(40, 46, 58),
                ForeColor = Color.White,
                GripStyle = ToolStripGripStyle.Hidden,
                Padding = new Padding(8, 4, 8, 4),
                RenderMode = ToolStripRenderMode.System
            };

            var btnReadAll = new ToolStripButton("✅ 모두 읽음") { ForeColor = Color.White };
            btnReadAll.Click += (s, e) => { AlertCenter.Instance.MarkAllRead(); LoadAlerts(); };

            var btnRefresh = new ToolStripButton("🔄 새로고침") { ForeColor = Color.White };
            btnRefresh.Click += (s, e) => LoadAlerts();

            _toolbar.Items.AddRange(new ToolStripItem[] {
                btnRefresh, btnReadAll, new ToolStripSeparator(),
                new ToolStripLabel("필터:") { ForeColor = Color.LightGray }
            });

            _filterCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 120,
                BackColor = Color.FromArgb(50, 56, 70),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _filterCombo.Items.AddRange(new[] { "전체", "읽지않음", "가격", "시그널", "뉴스", "배당" });
            _filterCombo.SelectedIndex = 0;
            _filterCombo.SelectedIndexChanged += (s, e) => LoadAlerts();
            _toolbar.Items.Add(new ToolStripControlHost(_filterCombo));

            _grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(30, 34, 44),
                ForeColor = Color.White,
                GridColor = Color.FromArgb(55, 60, 72),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                EnableHeadersVisualStyles = false,
                RowTemplate = { Height = 36 },
                Font = new Font("맑은 고딕", 10F)
            };
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(50, 56, 70),
                ForeColor = Color.White,
                Font = new Font("맑은 고딕", 10F, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleCenter
            };
            _grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(30, 34, 44),
                ForeColor = Color.White,
                SelectionBackColor = Color.FromArgb(70, 120, 200),
                SelectionForeColor = Color.White,
                Padding = new Padding(6, 0, 6, 0)
            };

            _grid.Columns.Add("Time", "시각");
            _grid.Columns.Add("Source", "유형");
            _grid.Columns.Add("Level", "레벨");
            _grid.Columns.Add("Symbol", "종목");
            _grid.Columns.Add("Title", "제목");
            _grid.Columns.Add("Message", "내용");
            _grid.Columns["Time"]!.FillWeight = 14;
            _grid.Columns["Source"]!.FillWeight = 8;
            _grid.Columns["Level"]!.FillWeight = 8;
            _grid.Columns["Symbol"]!.FillWeight = 10;
            _grid.Columns["Title"]!.FillWeight = 25;
            _grid.Columns["Message"]!.FillWeight = 35;

            _grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var lvl = _grid.Rows[e.RowIndex].Cells["Level"].Value?.ToString() ?? "";
                if (lvl == "Critical") _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = Color.Salmon;
                else if (lvl == "Warning") _grid.Rows[e.RowIndex].DefaultCellStyle.ForeColor = Color.Gold;
            };

            var status = new StatusStrip
            {
                BackColor = Color.FromArgb(40, 46, 58),
                SizingGrip = false
            };
            _summary = new Label { Text = "", ForeColor = Color.LightGreen, AutoSize = true };
            status.Items.Add(new ToolStripControlHost(_summary));

            Controls.Add(_grid);
            Controls.Add(status);
            Controls.Add(_toolbar);
        }

        private void LoadAlerts()
        {
            _grid.Rows.Clear();
            var filter = _filterCombo.SelectedItem?.ToString() ?? "전체";
            var alerts = filter == "읽지않음"
                ? _db.GetAlerts(500, unreadOnly: true)
                : _db.GetAlerts(500);

            alerts = filter switch
            {
                "가격" => alerts.Where(a => a.Source == AlertSource.Price).ToList(),
                "시그널" => alerts.Where(a => a.Source == AlertSource.Signal).ToList(),
                "뉴스" => alerts.Where(a => a.Source == AlertSource.News).ToList(),
                "배당" => alerts.Where(a => a.Source == AlertSource.Dividend).ToList(),
                _ => alerts
            };

            foreach (var a in alerts)
            {
                var row = _grid.Rows[_grid.Rows.Add(
                    a.Time.ToString("MM-dd HH:mm:ss"),
                    a.Source.ToString(),
                    a.Level.ToString(),
                    a.Symbol,
                    a.Title,
                    a.Message)];
                if (!a.IsRead)
                    row.DefaultCellStyle.Font = new Font("맑은 고딕", 10F, FontStyle.Bold);
            }

            _summary.Text = $"   📋 총 {alerts.Count}건  ·  🔴 읽지않음 {_db.GetUnreadAlertCount()}건";
        }

        private void OnAlertRaised(object? sender, AlertItem a)
        {
            if (InvokeRequired) { BeginInvoke(() => OnAlertRaised(sender, a)); return; }
            LoadAlerts();
        }
    }
}
