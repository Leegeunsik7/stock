// ============================================================================
// Broker - 실거래 어댑터 전환 "준비" 계층 (v3.2)
//  · IBroker: 모의/실거래 공통 인터페이스(주문 인터페이스 통일).
//  · PaperBroker는 이 인터페이스를 구현(별도 변경 최소).
//  · LiveBrokerStub: KIS/업비트 실주문 어댑터 골격 — 안전을 위해 주문은 거부(미구현).
//  · BrokerHub: 모의/실전 토글(Settings). ApiKeyVault: 키를 DPAPI(Secret)로 보관.
//  ※ 실거래 실행 로직은 의도적으로 비활성. 모의에서 충분히 검증 후 별도 단계에서 연결.
// ============================================================================
using System;
using System.Windows.Forms;
using System.Drawing;

namespace StockManager.Common
{
    public interface IBroker
    {
        string Name { get; }
        bool IsLive { get; }
        (bool ok, string msg) Place(PaperAsset asset, string symbol, string name,
            PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice);
        void Cancel(string orderId);
        decimal HeldQty(PaperAsset asset, string symbol);
    }

    /// <summary>실거래 어댑터 골격 — 안전상 주문은 거부(미구현). 향후 KIS/업비트 연결 지점.</summary>
    public class LiveBrokerStub : IBroker
    {
        public string Name => "실거래(KIS/업비트)";
        public bool IsLive => true;

        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name,
            PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
            => (false, "실거래가 '무장'되지 않았습니다.\n거래 설정에서 실전 모드 + 무장 체크 후 이용하세요.");

        public void Cancel(string orderId) { /* 실거래 미구현 */ }
        public decimal HeldQty(PaperAsset asset, string symbol) => 0m;
    }

    public static class BrokerHub
    {
        public static bool IsLive(LocalDatabase db) => db.GetSetting("Broker.Mode") == "live";
        public static void SetLive(LocalDatabase db, bool live) => db.SetSetting("Broker.Mode", live ? "live" : "paper");

        // 실거래 최종 무장 스위치(기본 OFF). 실전 모드라도 무장 전에는 주문 거부.
        public static bool IsArmed(LocalDatabase db) => db.GetSetting("Broker.LiveArmed") == "1";
        public static void SetArmed(LocalDatabase db, bool armed) => db.SetSetting("Broker.LiveArmed", armed ? "1" : "0");

        // 주문 금액 상한(원). 기본 100,000원.
        public static decimal MaxOrderKrw(LocalDatabase db)
            => decimal.TryParse(db.GetSetting("Broker.MaxOrderKrw"), out var v) && v > 0 ? v : 100_000m;
        public static void SetMaxOrderKrw(LocalDatabase db, decimal v) => db.SetSetting("Broker.MaxOrderKrw", ((long)v).ToString());

        /// <summary>현재 모드/무장 상태에 맞는 브로커.
        /// 모의 → PaperBroker, 실전·미무장 → 거부 스텁, 실전·무장 → 실거래 라우터(금액 상한 적용).</summary>
        public static IBroker Create(LocalDatabase db)
        {
            if (!IsLive(db)) return new PaperBroker(db);
            if (!IsArmed(db)) return new LiveBrokerStub();
            return new LiveRouterBroker(db);
        }
    }

    /// <summary>API 키 보안 보관 (DPAPI 기반 Secret 사용)</summary>
    public static class ApiKeyVault
    {
        public static void SaveKis(LocalDatabase db, string appKey, string appSecret, string account)
        { db.SetSecret("KIS.AppKey", appKey); db.SetSecret("KIS.AppSecret", appSecret); db.SetSecret("KIS.Account", account); }
        public static (string key, string secret, string account) GetKis(LocalDatabase db)
            => (db.GetSecret("KIS.AppKey") ?? "", db.GetSecret("KIS.AppSecret") ?? "", db.GetSecret("KIS.Account") ?? "");

        public static void SaveUpbit(LocalDatabase db, string accessKey, string secretKey)
        { db.SetSecret("Upbit.AccessKey", accessKey); db.SetSecret("Upbit.SecretKey", secretKey); }
        public static (string access, string secret) GetUpbit(LocalDatabase db)
            => (db.GetSecret("Upbit.AccessKey") ?? "", db.GetSecret("Upbit.SecretKey") ?? "");
    }

    /// <summary>주문 실행 전 확인 다이얼로그(실거래 전환 대비 공통 UI)</summary>
    public class OrderConfirmDialog : Form
    {        public OrderConfirmDialog(string title, string detail, bool live)
        {
            Text = title;
            Size = new Size(420, 240);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;
            Font = new Font("맑은 고딕", 10F);

            var head = new Label
            {
                Text = live ? "⚠️ 실거래 주문 확인" : "모의 주문 확인",
                Location = new Point(20, 18), AutoSize = true,
                Font = new Font("맑은 고딕", 12F, FontStyle.Bold),
                ForeColor = live ? Color.FromArgb(200, 40, 40) : ThemeManager.Text
            };
            var body = new Label { Text = detail, Location = new Point(20, 56), Size = new Size(370, 90) };

            var ok = new Button { Text = live ? "실행(실거래)" : "주문", DialogResult = DialogResult.OK, Location = new Point(200, 158), Size = new Size(90, 32), FlatStyle = FlatStyle.Flat, BackColor = live ? Color.FromArgb(200, 40, 40) : ThemeManager.Accent, ForeColor = Color.White };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Location = new Point(300, 158), Size = new Size(90, 32), FlatStyle = FlatStyle.Flat };
            Controls.Add(head); Controls.Add(body); Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            ThemeManager.Apply(this);
        }
    }
}

namespace StockManager.Common
{
    /// <summary>거래 설정: 모의/실전 토글 + API 키 보관(DPAPI). 실전은 현재 비활성(주문 거부).</summary>
    public class BrokerSettingsForm : Form
    {
        private readonly LocalDatabase _db;
        private readonly RadioButton _paper = new() { Text = "모의투자 (안전)", AutoSize = true };
        private readonly RadioButton _live = new() { Text = "실거래 (현재 비활성)", AutoSize = true };
        private readonly TextBox _kisKey = new() { UseSystemPasswordChar = true };
        private readonly TextBox _kisSecret = new() { UseSystemPasswordChar = true };
        private readonly TextBox _kisAcct = new();
        private readonly TextBox _upAccess = new() { UseSystemPasswordChar = true };
        private readonly TextBox _upSecret = new() { UseSystemPasswordChar = true };
        private readonly CheckBox _armed = new() { Text = "실거래 주문 최종 활성화(무장)", AutoSize = true };
        private readonly CheckBox _kisReal = new() { Text = "KIS 실전 도메인(기본: 모의 VTS)", AutoSize = true };
        private readonly NumericUpDown _maxKrw = new() { Minimum = 0, Maximum = 1_000_000_000, Increment = 10000, ThousandsSeparator = true };

        public BrokerSettingsForm(LocalDatabase db)
        {
            _db = db;
            Text = "⚙️ 거래 설정 (모의/실전 · API 키)";
            Size = new Size(500, 620);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = MinimizeBox = false;
            Font = new Font("맑은 고딕", 10F);

            int y = 16;
            void Lbl(string t, int h = 0) { Controls.Add(new Label { Text = t, Location = new Point(20, y), AutoSize = true, Font = new Font("맑은 고딕", 10F, h == 1 ? FontStyle.Bold : FontStyle.Regular) }); y += 26; }
            void Field(string t, Control tb) { Controls.Add(new Label { Text = t, Location = new Point(24, y + 3), AutoSize = true }); tb.Location = new Point(150, y); tb.Width = 320; Controls.Add(tb); y += 32; }

            Lbl("거래 모드", 1);
            _paper.Location = new Point(24, y); Controls.Add(_paper);
            _live.Location = new Point(180, y); Controls.Add(_live); y += 30;
            var warn = new Label
            {
                Text = "⚠️ 실거래는 실제 자금이 집행됩니다. 이 빌드의 실주문 코드는 검증되지 않았으니\n반드시 KIS 모의도메인/업비트 소액으로 먼저 확인하세요. '무장'을 켜야 실주문이 나갑니다.",
                Location = new Point(24, y), Size = new Size(440, 46), ForeColor = Color.FromArgb(200, 50, 30)
            };
            Controls.Add(warn); y += 52;
            _armed.Location = new Point(24, y); _armed.ForeColor = Color.FromArgb(200, 50, 30); Controls.Add(_armed); y += 28;
            _kisReal.Location = new Point(24, y); Controls.Add(_kisReal); y += 30;
            Field("주문 금액 상한(원)", _maxKrw);

            Lbl("KIS (한국투자증권)", 1);
            Field("App Key", _kisKey);
            Field("App Secret", _kisSecret);
            Field("계좌번호(예: 12345678-01)", _kisAcct);
            Lbl("업비트", 1);
            Field("Access Key", _upAccess);
            Field("Secret Key", _upSecret);

            var save = new Button { Text = "저장", Location = new Point(290, y + 6), Size = new Size(90, 32), FlatStyle = FlatStyle.Flat, BackColor = ThemeManager.Accent, ForeColor = Color.White };
            var close = new Button { Text = "닫기", Location = new Point(386, y + 6), Size = new Size(84, 32), FlatStyle = FlatStyle.Flat, DialogResult = DialogResult.Cancel };
            save.Click += (s, e) => Save();
            Controls.Add(save); Controls.Add(close);
            CancelButton = close;

            LoadExisting();
            ThemeManager.Apply(this);
        }

        private void LoadExisting()
        {
            bool live = BrokerHub.IsLive(_db);
            _paper.Checked = !live; _live.Checked = live;
            _armed.Checked = BrokerHub.IsArmed(_db);
            _kisReal.Checked = _db.GetSetting("Kis.UseRealDomain") == "1";
            _maxKrw.Value = Math.Clamp(BrokerHub.MaxOrderKrw(_db), 0, 1_000_000_000m);
            var (kk, ks, ka) = ApiKeyVault.GetKis(_db);
            _kisKey.Text = kk; _kisSecret.Text = ks; _kisAcct.Text = ka;
            var (ua, us) = ApiKeyVault.GetUpbit(_db);
            _upAccess.Text = ua; _upSecret.Text = us;
        }

        private void Save()
        {
            // 실거래+무장+실전도메인을 동시에 켜면 한 번 더 확인
            if (_live.Checked && _armed.Checked)
            {
                var dom = _kisReal.Checked ? "KIS 실전 도메인" : "KIS 모의(VTS)";
                var r = MessageBox.Show(
                    $"실거래가 '무장' 상태로 저장됩니다.\n· 모드: 실전  · {dom}  · 금액 상한 {(_maxKrw.Value):N0}원\n\n실제 주문이 집행될 수 있습니다. 계속할까요?\n(이 빌드의 실주문 코드는 검증되지 않았습니다 — 소액으로 먼저 확인하세요)",
                    "실거래 무장 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }
            BrokerHub.SetLive(_db, _live.Checked);
            BrokerHub.SetArmed(_db, _armed.Checked);
            BrokerHub.SetMaxOrderKrw(_db, _maxKrw.Value);
            _db.SetSetting("Kis.UseRealDomain", _kisReal.Checked ? "1" : "0");
            ApiKeyVault.SaveKis(_db, _kisKey.Text.Trim(), _kisSecret.Text.Trim(), _kisAcct.Text.Trim());
            ApiKeyVault.SaveUpbit(_db, _upAccess.Text.Trim(), _upSecret.Text.Trim());
            var mode = !_live.Checked ? "모의투자"
                : (_armed.Checked ? "실거래(무장)" : "실거래(미무장 — 주문 거부)");
            MessageBox.Show($"저장됨. 현재 모드: {mode}", "거래 설정");
        }
    }
}
