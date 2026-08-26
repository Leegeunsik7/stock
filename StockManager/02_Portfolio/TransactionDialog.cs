// ============================================================================
// Program #2: TransactionDialog (v2.2)
// 매수/매도 거래 입력 다이얼로그
// ============================================================================
using System;
using System.Drawing;
using System.Windows.Forms;

namespace StockManager.Portfolio
{
    public class TransactionDialog : Form
    {
        public Transaction? Result { get; private set; }
        private readonly TransactionType _type;

        private TextBox _txtSymbol = null!;
        private TextBox _txtName = null!;
        private NumericUpDown _nQty = null!;
        private NumericUpDown _nPrice = null!;
        private NumericUpDown _nCommission = null!;
        private NumericUpDown _nTax = null!;
        private DateTimePicker _dtpExecuted = null!;
        private ComboBox _cbBroker = null!;
        private TextBox _txtMemo = null!;
        private Label _lblTotal = null!;

        public TransactionDialog(TransactionType type)
        {
            _type = type;
            InitializeUI();
        }

        private void InitializeUI()
        {
            Text = _type == TransactionType.Buy ? "➕ 매수 기록" : "➖ 매도 기록";
            Size = new Size(500, 600);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(24, 28, 36);
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 10F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;

            var accentColor = _type == TransactionType.Buy
                ? Color.FromArgb(50, 200, 100)
                : Color.FromArgb(220, 80, 80);

            var header = new Panel
            {
                Dock = DockStyle.Top, Height = 60,
                BackColor = accentColor
            };
            header.Controls.Add(new Label
            {
                Text = _type == TransactionType.Buy ? "📈 매수 거래 기록" : "📉 매도 거래 기록",
                Font = new Font("맑은 고딕", 14F, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(20, 18), AutoSize = true
            });

            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20, 20, 20, 20),
                BackColor = Color.FromArgb(24, 28, 36)
            };

            int y = 15;
            int labelW = 100;
            int inputW = 320;

            AddLabel(panel, "종목코드", 0, y);
            _txtSymbol = AddTextBox(panel, labelW + 10, y, inputW);
            y += 40;

            AddLabel(panel, "종목명", 0, y);
            _txtName = AddTextBox(panel, labelW + 10, y, inputW);
            y += 40;

            AddLabel(panel, "거래일시", 0, y);
            _dtpExecuted = new DateTimePicker
            {
                Location = new Point(labelW + 10, y),
                Width = inputW, Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd HH:mm",
                Value = DateTime.Now,
                Font = new Font("맑은 고딕", 10F)
            };
            panel.Controls.Add(_dtpExecuted);
            y += 40;

            AddLabel(panel, "수량", 0, y);
            _nQty = AddNumeric(panel, labelW + 10, y, inputW, 0, 10_000_000, 1);
            y += 40;

            AddLabel(panel, "단가", 0, y);
            _nPrice = AddNumeric(panel, labelW + 10, y, inputW, 0, 100_000_000m, 100);
            _nPrice.DecimalPlaces = 2;
            y += 40;

            AddLabel(panel, "수수료", 0, y);
            _nCommission = AddNumeric(panel, labelW + 10, y, inputW, 0, 10_000_000m, 10);
            _nCommission.DecimalPlaces = 2;
            y += 40;

            AddLabel(panel, "세금", 0, y);
            _nTax = AddNumeric(panel, labelW + 10, y, inputW, 0, 10_000_000m, 10);
            _nTax.DecimalPlaces = 2;
            y += 40;

            AddLabel(panel, "증권사", 0, y);
            _cbBroker = new ComboBox
            {
                Location = new Point(labelW + 10, y),
                Width = inputW,
                DropDownStyle = ComboBoxStyle.DropDown,
                BackColor = Color.FromArgb(40, 46, 58),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _cbBroker.Items.AddRange(new object[] { "키움증권", "한국투자증권", "삼성증권", "토스증권", "NH투자증권", "미래에셋" });
            panel.Controls.Add(_cbBroker);
            y += 40;

            AddLabel(panel, "메모", 0, y);
            _txtMemo = AddTextBox(panel, labelW + 10, y, inputW);
            y += 40;

            _lblTotal = new Label
            {
                Text = "총 금액: 0원",
                Font = new Font("맑은 고딕", 12F, FontStyle.Bold),
                ForeColor = accentColor,
                Location = new Point(0, y + 10), AutoSize = true
            };
            panel.Controls.Add(_lblTotal);

            _nQty.ValueChanged += UpdateTotal;
            _nPrice.ValueChanged += UpdateTotal;
            _nCommission.ValueChanged += UpdateTotal;
            _nTax.ValueChanged += UpdateTotal;

            var btnOk = new Button
            {
                Text = "✅ 저장",
                Size = new Size(120, 36),
                Location = new Point(270, y + 60),
                BackColor = accentColor,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("맑은 고딕", 10F, FontStyle.Bold)
            };
            btnOk.Click += OnSaveClick;

            var btnCancel = new Button
            {
                Text = "취소",
                Size = new Size(120, 36),
                Location = new Point(130, y + 60),
                BackColor = Color.FromArgb(60, 60, 70),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            panel.Controls.Add(btnOk);
            panel.Controls.Add(btnCancel);

            Controls.Add(panel);
            Controls.Add(header);
        }

        private Label AddLabel(Panel p, string text, int x, int y)
        {
            var l = new Label
            {
                Text = text,
                Location = new Point(x, y + 5),
                AutoSize = true,
                ForeColor = Color.LightGray,
                Font = new Font("맑은 고딕", 10F)
            };
            p.Controls.Add(l);
            return l;
        }

        private TextBox AddTextBox(Panel p, int x, int y, int w)
        {
            var t = new TextBox
            {
                Location = new Point(x, y),
                Width = w,
                BackColor = Color.FromArgb(40, 46, 58),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("맑은 고딕", 10F)
            };
            p.Controls.Add(t);
            return t;
        }

        private NumericUpDown AddNumeric(Panel p, int x, int y, int w, decimal min, decimal max, decimal inc)
        {
            var n = new NumericUpDown
            {
                Location = new Point(x, y),
                Width = w, Minimum = min, Maximum = max, Increment = inc,
                BackColor = Color.FromArgb(40, 46, 58),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("맑은 고딕", 10F),
                ThousandsSeparator = true
            };
            p.Controls.Add(n);
            return n;
        }

        private void UpdateTotal(object? s, EventArgs e)
        {
            var total = _nQty.Value * _nPrice.Value + _nCommission.Value + _nTax.Value;
            _lblTotal.Text = $"총 금액: {total:N0}원";
        }

        private void OnSaveClick(object? s, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_txtSymbol.Text))
            {
                MessageBox.Show("종목코드를 입력하세요."); return;
            }
            if (_nQty.Value == 0 || _nPrice.Value == 0)
            {
                MessageBox.Show("수량과 단가를 입력하세요."); return;
            }

            Result = new Transaction
            {
                Symbol = _txtSymbol.Text.Trim(),
                Type = _type,
                Quantity = (int)_nQty.Value,
                Price = _nPrice.Value,
                Commission = _nCommission.Value,
                Tax = _nTax.Value,
                ExecutedAt = _dtpExecuted.Value,
                BrokerAccount = _cbBroker.Text,
                Memo = _txtMemo.Text
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
