using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LicenseKeyGen;

/// <summary>라이선스 키 발급기 — ANG UAV Cognitive Combat 관제 톤의 다크 프리미엄 UI(사이버 시안·세그먼트 유효기간·컬러 칩·통계·호버·글래스 카드).</summary>
public sealed class MainForm : Form
{
    // ── 팔레트(ANG UAV · 딥 사파이어 + 사이버 시안) ──
    private static readonly Color FormBg     = Color.FromArgb(9, 17, 30);
    private static readonly Color CardBg     = Color.FromArgb(15, 26, 43);
    private static readonly Color CardBorder = Color.FromArgb(22, 52, 82);
    private static readonly Color FieldBg    = Color.FromArgb(8, 15, 28);
    private static readonly Color RowAlt     = Color.FromArgb(13, 23, 39);
    private static readonly Color RowHover   = Color.FromArgb(18, 40, 62);
    private static readonly Color Ink        = Color.FromArgb(234, 244, 255);
    private static readonly Color Sub        = Color.FromArgb(127, 156, 187);
    private static readonly Color Brand      = Color.FromArgb(129, 140, 245);
    private static readonly Color GradTop    = Color.FromArgb(129, 140, 245);
    private static readonly Color GradBot    = Color.FromArgb(79, 91, 213);
    private static readonly Color Good       = Color.FromArgb(52, 211, 153);
    private static readonly Color Danger     = Color.FromArgb(255, 107, 107);
    private static readonly Color Sel        = Color.FromArgb(12, 74, 110);
    private static readonly Color PillIdle   = Color.FromArgb(14, 28, 46);
    private static readonly Color PillBorder = Color.FromArgb(30, 58, 88);
    private static readonly Color SoftBtn    = Color.FromArgb(20, 34, 54);

    private const string FontKr = "맑은 고딕";
    private static readonly string[] ExpiryLabels = { "1년", "2년", "3년", "5년", "무기한" };

    private readonly TextBox _txtName = new();
    private readonly TextBox _txtKey = new();
    private readonly Label _lblStatus = new();
    private readonly Label _lblStats = new();
    private readonly Label _keyHint = new();
    private readonly ListView _lvIssued = new();
    private readonly List<PremiumButton> _expiryPills = new();
    private int _expiryIndex;
    private int _hoverRow = -1;

    public MainForm()
    {
        Text = "라이선스 키 발급기 · MS2E StockSuite 관제";
        BackColor = FormBg;
        ForeColor = Ink;
        Font = new Font(FontKr, 9.75F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(940, 1188);
        MinimumSize = new Size(760, 900);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* 기본 아이콘 */ }
        BuildUi();
        RefreshList();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
            int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch { /* 구버전 무시 */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnShown(EventArgs e) { base.OnShown(e); SizeColumns(); }

    private void BuildUi()
    {
        const int W = 940, M = 26, CW = W - M * 2;   // 콘텐츠 폭 888

        // ══ 헤더 ══
        Controls.Add(new HeaderBar
        {
            Location = new Point(0, 0), Size = new Size(W, 120),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        });

        // ══ 카드 1 · 새 발급 ══
        var card1 = NewCard(M, 144, CW, 336, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
        card1.Controls.Add(SectionTitle("🪪  새 라이선스 발급", 26, 22));

        card1.Controls.Add(FieldLabel("수신자 이름 (부대·기관·담당자)", 26, 68));
        _txtName.Font = new Font(FontKr, 12F);
        var nameField = WrapField(_txtName, false);
        nameField.Location = new Point(26, 98);
        nameField.Size = new Size(CW - 52, 54);
        nameField.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        card1.Controls.Add(nameField);

        card1.Controls.Add(FieldLabel("유효기간", 26, 170));
        BuildExpiryPills(card1, 26, 198, CW - 52);

        var btnIssue = new PremiumButton
        {
            Text = "🔑   발급하기",
            Location = new Point(26, 258), Size = new Size(CW - 52, 58),
            Font = new Font(FontKr, 14F, FontStyle.Bold),
            UseGradient = true, GTop = GradTop, GBot = GradBot, Radius = 14,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        btnIssue.Click += OnIssue;
        card1.Controls.Add(btnIssue);
        Controls.Add(card1);

        // ══ 카드 2 · 발급 결과 ══
        var card2 = NewCard(M, 500, CW, 372, AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right);
        card2.Controls.Add(SectionTitle("📤  발급 결과", 26, 22));
        card2.Controls.Add(FieldLabel("발급된 라이선스 키 (이 값을 수령자에게 전달)", 26, 62));

        _txtKey.Multiline = true;
        _txtKey.ReadOnly = true;
        _txtKey.WordWrap = true;
        _txtKey.ScrollBars = ScrollBars.Vertical;
        _txtKey.Font = new Font("Consolas", 9F);
        _txtKey.ForeColor = Good;
        var keyField = WrapField(_txtKey, true);
        keyField.Location = new Point(26, 92);
        keyField.Size = new Size(CW - 52, 208);
        keyField.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _keyHint.Text = "🔑  발급하기를 누르면 여기에 라이선스 키가 표시됩니다";
        _keyHint.ForeColor = Color.FromArgb(96, 116, 146);
        _keyHint.BackColor = FieldBg;
        _keyHint.TextAlign = ContentAlignment.MiddleCenter;
        _keyHint.Font = new Font(FontKr, 10F);
        _keyHint.Dock = DockStyle.Fill;
        keyField.Controls.Add(_keyHint);
        _keyHint.BringToFront();
        card2.Controls.Add(keyField);

        var btnCopy = new PremiumButton
        {
            Text = "📋  키 복사", Location = new Point(26, 316), Size = new Size(170, 42),
            BaseColor = SoftBtn, Radius = 11, ForeColor = Ink, Border = CardBorder,
        };
        btnCopy.Click += (_, _) => CopyKey();
        card2.Controls.Add(btnCopy);

        var btnPubKey = new PremiumButton
        {
            Text = "🛡  배포용 공개키", Location = new Point(208, 316), Size = new Size(210, 42),
            BaseColor = SoftBtn, Radius = 11, ForeColor = Ink, Border = CardBorder,
        };
        btnPubKey.Click += (_, _) => ShowPublicKey();
        card2.Controls.Add(btnPubKey);

        _lblStatus.AutoSize = false;
        _lblStatus.Location = new Point(432, 316);
        _lblStatus.Size = new Size(CW - 432 - 26, 42);
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        _lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _lblStatus.ForeColor = LicenseCore.SigningKeyExists ? Good : Sub;
        _lblStatus.Text = LicenseCore.SigningKeyExists ? "🔗 앱과 동일 키 공유" : "ⓘ 첫 발급 시 키 생성";
        card2.Controls.Add(_lblStatus);
        Controls.Add(card2);

        // ══ 카드 3 · 발급 명부 ══
        var card3 = NewCard(M, 892, CW, ClientSize.Height - 892 - M,
            AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right);
        card3.Controls.Add(SectionTitle("📚  발급 명부", 26, 20));
        _lblStats.AutoSize = true;
        _lblStats.Location = new Point(172, 25);
        _lblStats.ForeColor = Brand;
        _lblStats.BackColor = Color.Transparent;
        _lblStats.Font = new Font(FontKr, 9.5F, FontStyle.Bold);
        _lblStats.Text = "· 총 0건";
        card3.Controls.Add(_lblStats);

        var btnDelete = new PremiumButton
        {
            Text = "🗑 삭제", Size = new Size(96, 34), Location = new Point(CW - 26 - 96, 18),
            BaseColor = Color.FromArgb(60, 30, 38), Radius = 9, ForeColor = Color.FromArgb(252, 165, 165),
            Border = Color.FromArgb(96, 46, 56), Font = new Font(FontKr, 9F), Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        btnDelete.Click += OnDelete;
        card3.Controls.Add(btnDelete);

        var btnRefresh = new PremiumButton
        {
            Text = "↻ 새로고침", Size = new Size(122, 34), Location = new Point(CW - 26 - 96 - 12 - 122, 18),
            BaseColor = SoftBtn, Radius = 9, ForeColor = Ink, Border = CardBorder,
            Font = new Font(FontKr, 9F), Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        btnRefresh.Click += (_, _) => RefreshList();
        card3.Controls.Add(btnRefresh);

        StyleListView();
        _lvIssued.Location = new Point(26, 64);
        _lvIssued.Size = new Size(CW - 52, card3.Height - 64 - 38);
        _lvIssued.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        card3.Controls.Add(_lvIssued);

        card3.Controls.Add(new Label
        {
            Text = "관리자 모드(/admin/users · 발급 라이선스 관리)와 동일  ·  %LocalAppData%\\StockManager\\license_registry.json  ·  행 더블클릭 = 키 복사",
            ForeColor = Color.FromArgb(108, 126, 150), AutoSize = false,
            Location = new Point(26, card3.Height - 30), Size = new Size(CW - 52, 18),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right, Font = new Font(FontKr, 8F),
        });
        Controls.Add(card3);

        AcceptButton = btnIssue;
    }

    private void BuildExpiryPills(Control host, int x, int y, int totalW)
    {
        const int n = 5, gap = 12;
        int pw = (totalW - gap * (n - 1)) / n;
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            var pill = new PremiumButton
            {
                Text = ExpiryLabels[i],
                Location = new Point(x + i * (pw + gap), y),
                Size = new Size(pw, 44), Radius = 11,
                Font = new Font(FontKr, 11F, FontStyle.Bold),
            };
            pill.Click += (_, _) => { _expiryIndex = idx; UpdateExpiryPills(); };
            _expiryPills.Add(pill);
            host.Controls.Add(pill);
        }
        UpdateExpiryPills();
    }

    private void UpdateExpiryPills()
    {
        for (int i = 0; i < _expiryPills.Count; i++)
        {
            var p = _expiryPills[i];
            bool on = i == _expiryIndex;
            p.UseGradient = on;
            p.GTop = GradTop; p.GBot = GradBot;
            p.BaseColor = PillIdle;
            p.Border = on ? (Color?)null : PillBorder;
            p.ForeColor = on ? Color.White : Sub;
            p.Invalidate();
        }
    }

    // ── 팩토리 ──
    private static CardPanel NewCard(int x, int y, int w, int h, AnchorStyles a) => new()
    {
        Location = new Point(x, y), Size = new Size(w, h), Anchor = a,
        BackColor = CardBg, Border = CardBorder, Radius = 16,
    };

    private static Label SectionTitle(string text, int x, int y) => new()
    {
        Text = text, ForeColor = Ink, AutoSize = true, Location = new Point(x, y),
        Font = new Font(FontKr, 12F, FontStyle.Bold), BackColor = Color.Transparent,
    };

    private static Label FieldLabel(string text, int x, int y) => new()
    {
        Text = text, ForeColor = Sub, AutoSize = true, Location = new Point(x, y),
        Font = new Font(FontKr, 9.25F, FontStyle.Bold), BackColor = Color.Transparent,
    };

    private static CardPanel WrapField(TextBox tb, bool multiline)
    {
        tb.BorderStyle = BorderStyle.None;
        tb.BackColor = FieldBg;
        tb.ForeColor = tb.ForeColor == SystemColors.WindowText ? Ink : tb.ForeColor;
        var card = new CardPanel { BackColor = FieldBg, Border = CardBorder, Radius = 10 };
        card.Controls.Add(tb);
        void Layout()
        {
            const int px = 14;
            tb.Width = card.Width - px * 2;
            if (multiline) { tb.Multiline = true; tb.Location = new Point(px, 11); tb.Height = card.Height - 22; }
            else tb.Location = new Point(px, Math.Max(6, (card.Height - tb.PreferredHeight) / 2));
        }
        card.Resize += (_, _) => Layout();
        card.HandleCreated += (_, _) => Layout();
        return card;
    }

    private void StyleListView()
    {
        _lvIssued.View = View.Details;
        _lvIssued.FullRowSelect = true;
        _lvIssued.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _lvIssued.BorderStyle = BorderStyle.None;
        _lvIssued.BackColor = CardBg;
        _lvIssued.ForeColor = Ink;
        _lvIssued.Font = new Font(FontKr, 9.75F);
        _lvIssued.OwnerDraw = true;
        _lvIssued.SmallImageList = new ImageList { ImageSize = new Size(1, 32) };   // 행 높이 확보
        _lvIssued.Columns.Add("수신자", 250);
        _lvIssued.Columns.Add("발급일", 130);
        _lvIssued.Columns.Add("만료일", 150);
        _lvIssued.Columns.Add("ID", 130);
        _lvIssued.DoubleClick += (_, _) => CopySelectedKey();
        _lvIssued.Resize += (_, _) => SizeColumns();
        _lvIssued.HandleCreated += (_, _) => SizeColumns();
        _lvIssued.MouseMove += (_, e) =>
        {
            var hit = _lvIssued.HitTest(e.Location);
            int idx = hit.Item?.Index ?? -1;
            if (idx != _hoverRow) { _hoverRow = idx; _lvIssued.Invalidate(); }
        };
        _lvIssued.MouseLeave += (_, _) => { if (_hoverRow != -1) { _hoverRow = -1; _lvIssued.Invalidate(); } };
        _lvIssued.DrawColumnHeader += (s, e) =>
        {
            using var b = new SolidBrush(Color.FromArgb(18, 32, 52));
            e.Graphics.FillRectangle(b, e.Bounds);
            using var pen = new Pen(CardBorder);
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var r = e.Bounds; r.X += 12;
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", new Font(FontKr, 9F, FontStyle.Bold), r, Sub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };
        _lvIssued.DrawItem += (s, e) => { e.DrawDefault = false; };
        _lvIssued.DrawSubItem += (s, e) =>
        {
            bool selected = e.Item?.Selected ?? false;
            bool hover = e.ItemIndex == _hoverRow && !selected;
            var bg = selected ? Sel : hover ? RowHover : (e.ItemIndex % 2 == 0 ? CardBg : RowAlt);
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);

            var text = e.SubItem?.Text ?? "";
            if (e.ColumnIndex == 2 && text.Length > 0)   // 만료일 = 컬러 칩
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                bool perpetual = text == "무기한";
                var fg = perpetual ? Color.FromArgb(125, 200, 255) : Good;
                var chipBg = perpetual ? Color.FromArgb(16, 46, 78) : Color.FromArgb(16, 58, 46);
                var sz = TextRenderer.MeasureText(text, _lvIssued.Font);
                var chip = new Rectangle(e.Bounds.X + 12, e.Bounds.Y + (e.Bounds.Height - 24) / 2, sz.Width + 22, 24);
                using (var path = UiDraw.Round(chip, 12))
                using (var cb = new SolidBrush(chipBg)) e.Graphics.FillPath(cb, path);
                TextRenderer.DrawText(e.Graphics, text, _lvIssued.Font, chip, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                var fg = selected ? Color.White : Ink;
                var r = e.Bounds; r.X += 12; r.Width -= 14;
                TextRenderer.DrawText(e.Graphics, text, _lvIssued.Font, r, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        };
    }

    // ── 동작 ──
    private void OnIssue(object? sender, EventArgs e)
    {
        var name = _txtName.Text.Trim();
        if (name.Length == 0) { SetStatus("수신자 이름을 입력하세요.", true); _txtName.Focus(); return; }

        DateTime? expiry = _expiryIndex switch
        {
            0 => DateTime.Now.AddYears(1),
            1 => DateTime.Now.AddYears(2),
            2 => DateTime.Now.AddYears(3),
            3 => DateTime.Now.AddYears(5),
            _ => null,
        };
        try
        {
            var key = LicenseCore.Issue(name, expiry);
            _txtKey.Text = key;
            _keyHint.Visible = false;
            var copied = TrySetClipboard(key);
            var expTxt = expiry is null ? "무기한" : expiry.Value.ToString("yyyy-MM-dd");
            SetStatus($"발급 완료 · 만료 {expTxt}{(copied ? " · 복사됨" : "")}", false);
            RefreshList();
            _txtName.Clear();
            _txtName.Focus();
        }
        catch (Exception ex) { SetStatus("발급 실패: " + ex.Message, true); }
    }

    private void OnDelete(object? sender, EventArgs e)
    {
        if (_lvIssued.SelectedItems.Count == 0) { SetStatus("삭제할 항목을 목록에서 선택하세요.", true); return; }
        var id = _lvIssued.SelectedItems[0].SubItems[3].Text;
        var name = _lvIssued.SelectedItems[0].Text;
        if (MessageBox.Show($"'{name}' (ID {id}) 발급 기록을 명부에서 삭제할까요?\n※ 이미 전달한 키의 원격 무효화는 아닙니다.",
                "발급 명부 삭제", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (LicenseCore.RemoveIssued(id)) { SetStatus("명부에서 삭제했습니다.", false); RefreshList(); }
        else SetStatus("삭제 실패(이미 없거나 파일 오류).", true);
    }

    private void CopyKey()
    {
        if (_txtKey.Text.Trim().Length == 0) { SetStatus("복사할 키가 없습니다. 먼저 발급하세요.", true); return; }
        if (TrySetClipboard(_txtKey.Text.Trim())) SetStatus("클립보드에 복사됨.", false);
    }

    private void CopySelectedKey()
    {
        if (_lvIssued.SelectedItems.Count == 0) return;
        if (_lvIssued.SelectedItems[0].Tag is string key && key.Length > 0)
        {
            _txtKey.Text = key; _keyHint.Visible = false;
            if (TrySetClipboard(key)) SetStatus("선택한 키를 클립보드에 복사했습니다.", false);
        }
    }

    private void ShowPublicKey()
    {
        try
        {
            var pub = LicenseCore.PublicKeyBase64();
            using var dlg = new Form
            {
                Text = "배포용 공개키 · LicenseService.cs PublicKey 상수",
                BackColor = FormBg, ForeColor = Ink, Font = new Font(FontKr, 9.5F),
                ClientSize = new Size(600, 330), StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog,
            };
            try { dlg.Icon = Icon; } catch { }
            var info = new Label
            {
                Text = "수령자 PC에서 키가 검증되려면 배포본 StockManager\\Common\\Account\\LicenseService.cs 의\nPublicKey 상수를 아래 값으로 교체하고 다시 빌드하세요. (최초 1회)",
                ForeColor = Sub, Location = new Point(20, 16), Size = new Size(560, 42),
            };
            var box = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Location = new Point(20, 66), Size = new Size(560, 196),
                BackColor = FieldBg, ForeColor = Good, Font = new Font("Consolas", 9F),
                Text = pub, BorderStyle = BorderStyle.FixedSingle,
            };
            var copy = new PremiumButton
            {
                Text = "📋  공개키 복사", Location = new Point(20, 274), Size = new Size(180, 38),
                UseGradient = true, GTop = GradTop, GBot = GradBot, Radius = 10,
            };
            copy.Click += (_, _) => TrySetClipboard(pub);
            dlg.Controls.AddRange(new Control[] { info, box, copy });
            dlg.ShowDialog(this);
        }
        catch (Exception ex) { SetStatus("공개키 로드 실패: " + ex.Message, true); }
    }

    private void RefreshList()
    {
        var list = LicenseCore.ListIssued();
        _lvIssued.BeginUpdate();
        _lvIssued.Items.Clear();
        foreach (var r in list)
        {
            var expiry = string.IsNullOrEmpty(r.Expiry) ? "무기한" : LicenseCore.FormatYmd(r.Expiry);
            _lvIssued.Items.Add(new ListViewItem(new[] { r.Licensee, LicenseCore.FormatYmd(r.IssuedAt), expiry, r.Id }) { Tag = r.Key });
        }
        _lvIssued.EndUpdate();

        int total = list.Count;
        int valid = list.Count(IsValid);
        _lblStats.Text = total == 0 ? "· 발급 내역 없음" : $"· 총 {total}건 · 유효 {valid}건";

        if (IsHandleCreated) BeginInvoke(new Action(SizeColumns)); else SizeColumns();
    }

    private static bool IsValid(LicenseRecord r)
    {
        if (string.IsNullOrEmpty(r.Expiry)) return true;
        return DateTime.TryParseExact(r.Expiry, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
               && d.Date >= DateTime.Now.Date;
    }

    private void SizeColumns()
    {
        if (_lvIssued.Columns.Count < 4) return;
        int w = _lvIssued.ClientSize.Width - 2;
        if (w < 120) return;
        int c0 = (int)(w * 0.40), c1 = (int)(w * 0.19), c2 = (int)(w * 0.21);
        _lvIssued.Columns[0].Width = c0;
        _lvIssued.Columns[1].Width = c1;
        _lvIssued.Columns[2].Width = c2;
        _lvIssued.Columns[3].Width = Math.Max(60, w - c0 - c1 - c2);
    }

    private void SetStatus(string text, bool warn)
    {
        _lblStatus.Text = (warn ? "⚠ " : "✅ ") + text;
        _lblStatus.ForeColor = warn ? Danger : Good;
    }

    private static bool TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); return true; } catch { return false; }
    }
}

// ══════════════════════ 커스텀 컨트롤 ══════════════════════

internal sealed class CardPanel : Panel
{
    public int Radius { get; set; } = 12;
    public Color Border { get; set; } = Color.FromArgb(22, 52, 82);

    public CardPanel()
    {
        DoubleBuffered = true; ResizeRedraw = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? BackColor);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiDraw.Round(r, Radius);
        using (var b = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height)),
                   UiDraw.Lighten(BackColor, 0.05f), BackColor, LinearGradientMode.Vertical))
            g.FillPath(b, path);
        using var pen = new Pen(Border, 1);
        g.DrawPath(pen, path);
    }
}

/// <summary>상단 그라디언트 헤더 — 사이버 시안 아이콘 배지 + 타이틀 + 서브 + 상태 필 + 브랜드 언더라인.</summary>
internal sealed class HeaderBar : Panel
{
    private const string FontKr = "맑은 고딕";

    public HeaderBar()
    {
        DoubleBuffered = true; ResizeRedraw = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var full = new Rectangle(0, 0, Width, Height);
        using (var br = new LinearGradientBrush(full, Color.FromArgb(16, 34, 60), Color.FromArgb(8, 15, 28), LinearGradientMode.Horizontal))
            g.FillRectangle(br, full);

        // 브랜드 언더라인(하단 시안 광선)
        var underline = new Rectangle(0, Height - 3, Width, 3);
        using (var ub = new LinearGradientBrush(underline, Color.FromArgb(129, 140, 245), Color.FromArgb(79, 91, 213), LinearGradientMode.Horizontal))
            g.FillRectangle(ub, underline);

        // 아이콘 배지
        var badge = new Rectangle(30, 32, 54, 54);
        using (var bp = UiDraw.Round(badge, 15))
        using (var bb = new LinearGradientBrush(badge, Color.FromArgb(129, 140, 245), Color.FromArgb(79, 91, 213), LinearGradientMode.ForwardDiagonal))
            g.FillPath(bb, bp);
        TextRenderer.DrawText(g, "🔑", new Font(FontKr, 17F), badge, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // 타이틀 + 서브
        TextRenderer.DrawText(g, "MS2E StockSuite 라이선스 키 발급기", new Font(FontKr, 17.5F, FontStyle.Bold),
            new Point(98, 26), Color.FromArgb(233, 242, 255));
        TextRenderer.DrawText(g, "수신자 이름만 넣고 [발급]을 누르세요 · 관리자 모드(/admin/users)에 자동 등록됩니다.",
            new Font(FontKr, 9.75F), new Point(100, 70), Color.FromArgb(127, 156, 187));

        // 상태 필
        var pill = new Rectangle(Width - 30 - 158, 42, 158, 36);
        using (var pp = UiDraw.Round(pill, 18))
        using (var pb = new SolidBrush(Color.FromArgb(16, 40, 32)))
            g.FillPath(pb, pp);
        using (var pen = new Pen(Color.FromArgb(52, 211, 153)))
        using (var pp2 = UiDraw.Round(pill, 18))
            g.DrawPath(pen, pp2);
        TextRenderer.DrawText(g, "● 오프라인 발급", new Font(FontKr, 10F, FontStyle.Bold), pill,
            Color.FromArgb(52, 211, 153), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>프리미엄 플랫 버튼 — 라운드 + 그라디언트/솔리드 + 선택 테두리 + 호버 라이트닝.</summary>
internal sealed class PremiumButton : Button
{
    public int Radius { get; set; } = 10;
    public bool UseGradient { get; set; }
    public Color GTop { get; set; } = Color.FromArgb(129, 140, 245);
    public Color GBot { get; set; } = Color.FromArgb(79, 91, 213);
    public Color BaseColor { get; set; } = Color.FromArgb(129, 140, 245);
    public Color? Border { get; set; }
    private bool _hover;

    public PremiumButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseOverBackColor = Color.Transparent;
        FlatAppearance.MouseDownBackColor = Color.Transparent;
        ForeColor = Color.White;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        MouseLeave += (_, _) => { _hover = false; Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? BackColor);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiDraw.Round(r, Radius);
        if (UseGradient)
        {
            var t = _hover ? UiDraw.Lighten(GTop, 0.10f) : GTop;
            var b = _hover ? UiDraw.Lighten(GBot, 0.10f) : GBot;
            using var br = new LinearGradientBrush(r, t, b, LinearGradientMode.Vertical);
            g.FillPath(br, path);
        }
        else
        {
            var c = _hover ? UiDraw.Lighten(BaseColor, 0.10f) : BaseColor;
            using var br = new SolidBrush(c);
            g.FillPath(br, path);
        }
        if (Border.HasValue)
        {
            using var pen = new Pen(_hover ? UiDraw.Lighten(Border.Value, 0.15f) : Border.Value, 1);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font, r, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

internal static class UiDraw
{
    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        if (radius <= 0 || r.Width <= 0 || r.Height <= 0) { p.AddRectangle(r); return p; }
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Lighten(Color c, float amt) => Color.FromArgb(
        c.A,
        (int)Math.Min(255, c.R + 255 * amt),
        (int)Math.Min(255, c.G + 255 * amt),
        (int)Math.Min(255, c.B + 255 * amt));
}
