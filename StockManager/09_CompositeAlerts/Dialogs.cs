// ============================================================================
// CompositeAlerts Dialogs - 규칙 편집기 / 조건 편집기 (v3.2)
// ============================================================================
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using StockManager.Common;

namespace StockManager.CompositeAlerts
{
    public class RuleDialog : Form
    {
        private readonly CompositeRule _rule;
        private readonly TextBox _name = new();
        private readonly ComboBox _asset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox _target = new();
        private readonly ComboBox _logic = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly NumericUpDown _cooldown = new() { Minimum = 1, Maximum = 1440, Value = 30 };
        private readonly ListBox _conds = new();
        private Label _hint = null!;

        public RuleDialog(CompositeRule rule)
        {
            _rule = rule;
            Text = "복합 알림 규칙";
            Size = new Size(520, 480);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            Font = new Font("맑은 고딕", 10F);

            int y = 16;
            void Row(string label, Control c, int h = 26)
            {
                Controls.Add(new Label { Text = label, Location = new Point(20, y + 3), AutoSize = true });
                c.Location = new Point(130, y); c.Width = 350; c.Height = h; Controls.Add(c); y += h + 10;
            }
            _name.Text = rule.Name; Row("이름", _name);
            _asset.Items.AddRange(new object[] { "코인 (업비트)", "주식 (KIS)" });
            _asset.SelectedIndex = rule.AssetKind == "stock" ? 1 : 0;
            _asset.SelectedIndexChanged += (s, e) => UpdateHint();
            Row("자산", _asset);
            _target.Text = rule.Target; Row("대상", _target);
            _hint = new Label { Location = new Point(130, y), AutoSize = true, ForeColor = Color.Gray, Font = new Font("맑은 고딕", 8.5F) };
            Controls.Add(_hint); y += 22;
            UpdateHint();
            _logic.Items.AddRange(new object[] { "모든 조건(AND)", "하나라도(OR)" });
            _logic.SelectedIndex = rule.Logic == RuleLogic.Or ? 1 : 0; Row("결합", _logic);
            _cooldown.Value = Math.Clamp(rule.CooldownMin, 1, 1440); Row("쿨다운(분)", _cooldown);

            Controls.Add(new Label { Text = "조건", Location = new Point(20, y + 3), AutoSize = true });
            _conds.Location = new Point(130, y); _conds.Size = new Size(350, 150);
            RefreshConds();
            Controls.Add(_conds); 

            var bAddC = new Button { Text = "조건 추가", Location = new Point(130, y + 158), Size = new Size(110, 30), FlatStyle = FlatStyle.Flat };
            var bDelC = new Button { Text = "선택 삭제", Location = new Point(250, y + 158), Size = new Size(110, 30), FlatStyle = FlatStyle.Flat };
            bAddC.Click += (s, e) => { using var d = new ConditionDialog(new CompositeCondition()); if (d.ShowDialog(this) == DialogResult.OK) { _rule.Conditions.Add(d.Result); RefreshConds(); } };
            bDelC.Click += (s, e) => { if (_conds.SelectedIndex >= 0 && _conds.SelectedIndex < _rule.Conditions.Count) { _rule.Conditions.RemoveAt(_conds.SelectedIndex); RefreshConds(); } };
            Controls.Add(bAddC); Controls.Add(bDelC);

            var ok = new Button { Text = "저장", DialogResult = DialogResult.OK, Location = new Point(300, y + 200), Size = new Size(85, 32), FlatStyle = FlatStyle.Flat, BackColor = ThemeManager.Accent, ForeColor = Color.White };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Location = new Point(395, y + 200), Size = new Size(85, 32), FlatStyle = FlatStyle.Flat };
            ok.Click += (s, e) => Collect();
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            ThemeManager.Apply(this);
        }

        private void RefreshConds()
        {
            _conds.Items.Clear();
            foreach (var c in _rule.Conditions) _conds.Items.Add(c.Describe());
        }

        private void UpdateHint()
        {
            _hint.Text = _asset.SelectedIndex == 1
                ? "예: 005930 (삼성전자 종목코드)"
                : "예: KRW-BTC (업비트 마켓코드)";
        }

        private void Collect()
        {
            _rule.Name = string.IsNullOrWhiteSpace(_name.Text) ? "규칙" : _name.Text.Trim();
            _rule.AssetKind = _asset.SelectedIndex == 1 ? "stock" : "coin";
            _rule.Target = string.IsNullOrWhiteSpace(_target.Text)
                ? (_rule.AssetKind == "stock" ? "005930" : "KRW-BTC")
                : _target.Text.Trim().ToUpperInvariant();
            _rule.Logic = _logic.SelectedIndex == 1 ? RuleLogic.Or : RuleLogic.And;
            _rule.CooldownMin = (int)_cooldown.Value;
        }
    }

    public class ConditionDialog : Form
    {
        public CompositeCondition Result { get; private set; }
        private readonly ComboBox _metric = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly NumericUpDown _param = new() { Minimum = 1, Maximum = 200, Value = 20 };
        private readonly ComboBox _op = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly NumericUpDown _thr = new() { DecimalPlaces = 2, Minimum = -1_000_000_000, Maximum = 1_000_000_000, ThousandsSeparator = true };

        private static readonly string[] MetricKr = { "현재가", "등락률(%)", "MA 괴리(%)", "RSI", "거래량 증가(%)" };

        public ConditionDialog(CompositeCondition c)
        {
            Result = c;
            Text = "조건";
            Size = new Size(380, 280);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            Font = new Font("맑은 고딕", 10F);

            int y = 18;
            void Row(string label, Control ctl)
            {
                Controls.Add(new Label { Text = label, Location = new Point(20, y + 3), AutoSize = true });
                ctl.Location = new Point(120, y); ctl.Width = 220; Controls.Add(ctl); y += 38;
            }
            _metric.Items.AddRange(MetricKr.Cast<object>().ToArray());
            _metric.SelectedIndex = (int)c.Metric;
            _metric.SelectedIndexChanged += (s, e) => SyncParam();
            Row("지표", _metric);
            _param.Value = Math.Clamp(c.Param, 1, 200); Row("기간(N)", _param);
            _op.Items.AddRange(new object[] { "이상 (≥)", "이하 (≤)" });
            _op.SelectedIndex = c.Op == CondOp.Lte ? 1 : 0; Row("연산", _op);
            _thr.Value = Math.Clamp(c.Threshold, -1_000_000_000m, 1_000_000_000m); Row("기준값", _thr);

            var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Location = new Point(150, y + 6), Size = new Size(85, 30), FlatStyle = FlatStyle.Flat, BackColor = ThemeManager.Accent, ForeColor = Color.White };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Location = new Point(245, y + 6), Size = new Size(85, 30), FlatStyle = FlatStyle.Flat };
            ok.Click += (s, e) => Collect();
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            SyncParam();
            ThemeManager.Apply(this);
        }

        private void SyncParam()
        {
            // MA/RSI만 기간 사용
            _param.Enabled = _metric.SelectedIndex == 2 || _metric.SelectedIndex == 3;
        }

        private void Collect()
        {
            Result.Metric = (CondMetric)_metric.SelectedIndex;
            Result.Param = (int)_param.Value;
            Result.Op = _op.SelectedIndex == 1 ? CondOp.Lte : CondOp.Gte;
            Result.Threshold = _thr.Value;
        }
    }
}
