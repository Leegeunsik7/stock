// ============================================================================
// PaperTrading Dialogs - 주문 다이얼로그 / 시드 재설정 (v3.0)
// ============================================================================
using System;
using System.Drawing;
using System.Windows.Forms;

namespace StockManager.PaperTrading
{
    public class OrderDialog : Form
    {
        public PaperAsset Asset { get; private set; } = PaperAsset.Coin;
        public Market StockMarket { get; private set; } = Market.KRX_KOSPI;
        public string Symbol { get; private set; } = "";
        public string NameText { get; private set; } = "";
        public PaperKind Kind { get; private set; } = PaperKind.Market;
        public decimal Qty { get; private set; }
        public decimal LimitPrice { get; private set; }

        private readonly ComboBox _asset = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox _market = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly TextBox _symbol = new();
        private readonly TextBox _name = new();
        private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly NumericUpDown _qty = new() { DecimalPlaces = 8, Maximum = 1_000_000_000, Increment = 1, ThousandsSeparator = true };
        private readonly NumericUpDown _limit = new() { DecimalPlaces = 0, Maximum = 1_000_000_000, Increment = 1000, ThousandsSeparator = true };

        public OrderDialog(PaperSide side)
        {
            Text = (side == PaperSide.Buy ? "🟢 매수 주문" : "🔴 매도 주문") + " (모의)";
            Size = new Size(420, 420);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            Font = new Font("맑은 고딕", 10F);

            int y = 18;
            void Row(string label, Control c, int h = 26)
            {
                Controls.Add(new Label { Text = label, Location = new Point(20, y + 3), AutoSize = true });
                c.Location = new Point(150, y); c.Width = 230; c.Height = h;
                Controls.Add(c); y += h + 12;
            }

            _asset.Items.AddRange(new object[] { "코인 (KRW)", "주식" });
            _asset.SelectedIndex = 0;
            _asset.SelectedIndexChanged += (s, e) => SyncAsset();
            Row("자산", _asset);

            _market.Items.AddRange(new object[] { "코스피", "코스닥", "뉴욕", "나스닥", "아멕스" });
            _market.SelectedIndex = 0;
            Row("주식 시장", _market);

            _symbol.Text = "KRW-BTC";
            Row("심볼/코드", _symbol);
            Row("표시명(선택)", _name);

            _kind.Items.AddRange(new object[] { "시장가", "지정가" });
            _kind.SelectedIndex = 0;
            _kind.SelectedIndexChanged += (s, e) => _limit.Enabled = _kind.SelectedIndex == 1;
            Row("주문 유형", _kind);

            Row("수량", _qty);
            _limit.Enabled = false;
            Row("지정가(원)", _limit);

            var ok = new Button { Text = "주문", DialogResult = DialogResult.OK, Location = new Point(190, y + 6), Size = new Size(90, 32), FlatStyle = FlatStyle.Flat, BackColor = ThemeManager.Accent, ForeColor = Color.White };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Location = new Point(290, y + 6), Size = new Size(90, 32), FlatStyle = FlatStyle.Flat };
            ok.Click += (s, e) => Collect();
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;

            SyncAsset();
            ThemeManager.Apply(this);
        }

        private void SyncAsset()
        {
            bool coin = _asset.SelectedIndex == 0;
            Asset = coin ? PaperAsset.Coin : PaperAsset.Stock;
            _market.Enabled = !coin;
            if (coin && !_symbol.Text.StartsWith("KRW-")) _symbol.Text = "KRW-BTC";
        }

        private void Collect()
        {
            Asset = _asset.SelectedIndex == 0 ? PaperAsset.Coin : PaperAsset.Stock;
            StockMarket = _market.SelectedIndex switch
            {
                1 => Market.KRX_KOSDAQ, 2 => Market.US_NYSE, 3 => Market.US_NASDAQ, 4 => Market.US_AMEX, _ => Market.KRX_KOSPI
            };
            Symbol = _symbol.Text.Trim();
            NameText = string.IsNullOrWhiteSpace(_name.Text) ? Symbol.Replace("KRW-", "") : _name.Text.Trim();
            Kind = _kind.SelectedIndex == 1 ? PaperKind.Limit : PaperKind.Market;
            Qty = _qty.Value;
            LimitPrice = _limit.Value;
        }
    }

    public class SeedDialog : Form
    {
        public decimal Seed { get; private set; }
        private readonly NumericUpDown _seed = new() { DecimalPlaces = 0, Maximum = 100_000_000_000, Increment = 1_000_000, ThousandsSeparator = true };

        public SeedDialog(decimal current)
        {
            Text = "💰 시드머니 재설정 (모의)";
            Size = new Size(380, 200);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            Font = new Font("맑은 고딕", 10F);

            Controls.Add(new Label { Text = "초기 시드머니(원). 재설정하면 보유/주문이 모두 초기화됩니다.", Location = new Point(20, 18), Size = new Size(330, 40) });
            _seed.Location = new Point(20, 70); _seed.Width = 330; _seed.Value = Math.Max(0, current);
            Controls.Add(_seed);

            var ok = new Button { Text = "재설정", DialogResult = DialogResult.OK, Location = new Point(150, 118), Size = new Size(90, 32), FlatStyle = FlatStyle.Flat, BackColor = ThemeManager.Accent, ForeColor = Color.White };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Location = new Point(250, 118), Size = new Size(90, 32), FlatStyle = FlatStyle.Flat };
            ok.Click += (s, e) => Seed = _seed.Value;
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            ThemeManager.Apply(this);
        }
    }
}
