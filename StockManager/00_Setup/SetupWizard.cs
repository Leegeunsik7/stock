// ============================================================================
// SetupWizard - 통합 API 설정 위저드 (v2.4)
//  · 최초 실행 온보딩 + 재설정 겸용 (Launcher "설정" 카드 / Program 최초 실행)
//  · Gemini · KIS · 텔레그램 키를 한 흐름에서 입력 + 실제 연결 테스트
//  · 저장은 v2.3 DPAPI(SetSecret)로 암호화. 모든 서비스는 선택(건너뛰기 가능)
// ============================================================================
using System;
using System.Drawing;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StockManager
{
    public class SetupWizard : Form
    {
        private readonly LocalDatabase _db;

        // 색상 토큰 (Launcher와 통일)
        private static readonly Color Bg = Color.FromArgb(24, 28, 36);
        private static readonly Color Panel2 = Color.FromArgb(32, 37, 48);
        private static readonly Color Field = Color.FromArgb(45, 50, 62);
        private static readonly Color Accent = Color.FromArgb(70, 130, 200);
        private static readonly Color OkC = Color.FromArgb(70, 200, 120);
        private static readonly Color ErrC = Color.FromArgb(230, 95, 95);
        private static readonly Color Muted = Color.FromArgb(160, 168, 180);

        private readonly string[] _steps = { "환영", "Gemini AI", "한국투자증권", "텔레그램", "완료" };
        private int _current = 0;

        private Panel _rail = null!, _content = null!;
        private Button _btnPrev = null!, _btnNext = null!, _btnSkip = null!;
        private readonly Panel?[] _pages;

        // 입력 컨트롤 (생성자에서 1회 생성 → 페이지에 parenting, 값 보존)
        private readonly TextBox _txtGemini;
        private readonly TextBox _txtKisKey, _txtKisSecret, _txtKisAcc;
        private readonly CheckBox _chkPaper;
        private readonly TextBox _txtTgToken, _txtTgChat;
        private Label _finishSummary = null!;

        // 연결 테스트 통과 상태
        private bool _geminiOk, _kisOk, _tgOk;

        public SetupWizard(LocalDatabase db)
        {
            _db = db;
            _pages = new Panel?[_steps.Length];

            _txtGemini = MakeInput(true);
            _txtKisKey = MakeInput(true);
            _txtKisSecret = MakeInput(true);
            _txtKisAcc = MakeInput(false);
            _txtTgToken = MakeInput(true);
            _txtTgChat = MakeInput(false);
            _chkPaper = new CheckBox
            {
                Text = "모의투자(VTS) 모드", AutoSize = true, ForeColor = Color.White,
                Checked = _db.GetSetting("KIS.IsPaper") != "false"
            };

            // 기존 설정값 미리 채우기 (재설정 시)
            _txtGemini.Text = _db.GetSecret("Gemini.ApiKey") ?? "";
            _txtKisKey.Text = _db.GetSecret("KIS.AppKey") ?? "";
            _txtKisSecret.Text = _db.GetSecret("KIS.AppSecret") ?? "";
            _txtKisAcc.Text = _db.GetSetting("KIS.AccountNo") ?? "";
            _txtTgToken.Text = _db.GetSecret("Telegram.Token") ?? "";
            _txtTgChat.Text = _db.GetSetting("Telegram.ChatId") ?? "";

            InitShell();
            RenderStep();
        }

        // ===== 셸 레이아웃 =====
        private void InitShell()
        {
            Text = "🚀 MS2-E Stock Suite — 설정 마법사";
            Size = new Size(920, 640);
            MinimumSize = new Size(920, 640);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            BackColor = Bg;
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 10F);

            // z-order에 의존하지 않는 결정적 격자 레이아웃 (header / [rail | content] / nav)
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Bg
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));

            var header = new Panel { Dock = DockStyle.Fill, BackColor = Panel2 };
            header.Controls.Add(new Label
            {
                Text = "초기 설정 마법사", Location = new Point(24, 14), AutoSize = true,
                Font = new Font("맑은 고딕", 16F, FontStyle.Bold), ForeColor = Color.White
            });
            header.Controls.Add(new Label
            {
                Text = "API 키를 한곳에서 입력하고 즉시 연결을 확인하세요. (모두 선택 사항)",
                Location = new Point(26, 42), AutoSize = true, ForeColor = Muted
            });

            var mid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Bg
            };
            mid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            mid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _rail = new Panel { Dock = DockStyle.Fill, BackColor = Panel2 };
            _rail.Paint += DrawRail;

            _content = new Panel { Dock = DockStyle.Fill, BackColor = Bg, Padding = new Padding(28, 22, 28, 10) };

            mid.Controls.Add(_rail, 0, 0);
            mid.Controls.Add(_content, 1, 0);

            var nav = new Panel { Dock = DockStyle.Fill, BackColor = Panel2 };
            _btnPrev = MakeButton("← 이전", Color.FromArgb(60, 66, 80));
            _btnSkip = MakeButton("건너뛰기", Color.FromArgb(60, 66, 80));
            _btnNext = MakeButton("다음 →", Accent);
            _btnPrev.Click += (s, e) => Go(-1);
            _btnSkip.Click += (s, e) => Go(+1);
            _btnNext.Click += OnNext;
            nav.Controls.AddRange(new Control[] { _btnPrev, _btnSkip, _btnNext });
            void PlaceNav()
            {
                _btnPrev.Top = _btnSkip.Top = _btnNext.Top = 14;
                _btnPrev.Left = 24;
                _btnNext.Left = Math.Max(140, nav.Width - 24 - _btnNext.Width);
                _btnSkip.Left = _btnNext.Left - 12 - _btnSkip.Width;
            }
            nav.Resize += (s, e) => PlaceNav();
            PlaceNav();

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(mid, 0, 1);
            root.Controls.Add(nav, 0, 2);
            Controls.Add(root);
        }

        private void DrawRail(object? s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int y = 28;
            for (int i = 0; i < _steps.Length; i++)
            {
                bool active = i == _current;
                bool done = StepDone(i);
                var circleC = active ? Accent : done ? OkC : Color.FromArgb(70, 76, 90);
                g.FillEllipse(GdiCache.Brush(circleC), 24, y, 26, 26);
                var mark = done && !active ? "✓" : (i + 1).ToString();
                using var f = new Font("맑은 고딕", 10F, FontStyle.Bold);
                var ms = g.MeasureString(mark, f);
                g.DrawString(mark, f, Brushes.White, 24 + (26 - ms.Width) / 2, y + (26 - ms.Height) / 2);

                using var tf = new Font("맑은 고딕", 10.5F, active ? FontStyle.Bold : FontStyle.Regular);
                g.DrawString(_steps[i], tf, GdiCache.Brush(active ? Color.White : Muted), 62, y + 3);

                if (i < _steps.Length - 1)
                    g.DrawLine(GdiCache.Pen(Color.FromArgb(70, 76, 90), 2), 37, y + 28, 37, y + 48);
                y += 50;
            }
        }

        private bool StepDone(int i) => i switch
        {
            1 => _geminiOk,
            2 => _kisOk,
            3 => _tgOk,
            _ => false
        };

        // ===== 스텝 전환 =====
        private void RenderStep()
        {
            for (int i = 0; i < _pages.Length; i++)
                if (_pages[i] != null) _pages[i]!.Visible = false;

            if (_pages[_current] == null)
            {
                _pages[_current] = BuildPage(_current);
                _content.Controls.Add(_pages[_current]!);
            }
            if (_current == _steps.Length - 1) UpdateFinishSummary();

            _pages[_current]!.Visible = true;
            _pages[_current]!.BringToFront();

            _btnPrev.Enabled = _current > 0;
            bool last = _current == _steps.Length - 1;
            _btnNext.Text = last ? "✅ 완료" : "다음 →";
            _btnSkip.Visible = _current >= 1 && _current <= 3; // 선택 서비스 단계만
            _rail.Invalidate();
        }

        private Panel BuildPage(int i)
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
            switch (i)
            {
                case 0: BuildWelcome(p); break;
                case 1: BuildGemini(p); break;
                case 2: BuildKis(p); break;
                case 3: BuildTelegram(p); break;
                case 4: BuildFinish(p); break;
            }
            return p;
        }

        // ===== 각 스텝 =====
        private void BuildWelcome(Panel p)
        {
            p.Controls.Add(Title("환영합니다 👋", 0, 6));
            p.Controls.Add(Body(
                "이 마법사는 외부 연동 키를 한 번에 설정합니다.\n\n" +
                "• Gemini AI — 기업 분석 / 뉴스 감성분석\n" +
                "• 한국투자증권(KIS) — 실시간 시세 우선 제공\n" +
                "• 텔레그램 — Critical 알림 자동 전송\n\n" +
                "세 가지 모두 선택 사항이며, 미설정 시에도 네이버·야후 시세로 동작합니다.",
                0, 50, 620));

            var sec = new Panel { Location = new Point(0, 250), Size = new Size(620, 86), BackColor = Panel2 };
            sec.Controls.Add(new Label
            {
                Text = "🔐 입력한 키는 Windows DPAPI로 암호화되어 이 PC·이 사용자 계정에서만",
                Location = new Point(16, 14), AutoSize = true, ForeColor = OkC
            });
            sec.Controls.Add(new Label
            {
                Text = "    복호화됩니다. 평문으로 저장되지 않습니다. (저장 형식: enc:v1:…)",
                Location = new Point(16, 38), AutoSize = true, ForeColor = OkC
            });
            p.Controls.Add(sec);

            var adv = new LinkLabel
            {
                Text = "고급: 기존 설정 다이얼로그 열기",
                Location = new Point(0, 350), AutoSize = true,
                LinkColor = Muted, ActiveLinkColor = Color.White
            };
            adv.LinkClicked += (s, e) => { using var d = new SettingsDialog(_db); d.ShowDialog(); };
            p.Controls.Add(adv);
        }

        private void BuildGemini(Panel p)
        {
            p.Controls.Add(Title("🧠 Gemini AI", 0, 6));
            p.Controls.Add(Body("Google AI Studio에서 발급한 API 키를 입력하세요.", 0, 48, 620));
            p.Controls.Add(Lbl("API Key", 0, 92));
            Place(_txtGemini, p, 0, 116, 540);

            var (btn, status) = TestRow(p, 168);
            btn.Click += async (s, e) => await RunTest(btn, status,
                () => TestGeminiAsync(_txtGemini.Text.Trim()), ok => _geminiOk = ok);

            p.Controls.Add(Hint("키 발급: aistudio.google.com → Get API key", 0, 214, 620));
        }

        private void BuildKis(Panel p)
        {
            p.Controls.Add(Title("🏦 한국투자증권 (KIS)", 0, 6));
            p.Controls.Add(Body("KIS Developers에서 발급한 App Key / App Secret을 입력하세요.", 0, 48, 620));

            p.Controls.Add(Lbl("App Key", 0, 88));
            Place(_txtKisKey, p, 0, 110, 540);
            p.Controls.Add(Lbl("App Secret", 0, 150));
            Place(_txtKisSecret, p, 0, 172, 540);
            p.Controls.Add(Lbl("계좌번호 (예: 12345678-01)", 0, 212));
            Place(_txtKisAcc, p, 0, 234, 260);
            _chkPaper.Location = new Point(280, 238);
            p.Controls.Add(_chkPaper);

            var (btn, status) = TestRow(p, 286);
            btn.Click += async (s, e) => await RunTest(btn, status,
                () => TestKisAsync(_txtKisKey.Text.Trim(), _txtKisSecret.Text.Trim(), _chkPaper.Checked),
                ok => _kisOk = ok);

            p.Controls.Add(Hint("연결 테스트는 OAuth 토큰 발급(/oauth2/tokenP)으로 검증합니다.", 0, 332, 620));
        }

        private void BuildTelegram(Panel p)
        {
            p.Controls.Add(Title("📱 텔레그램 봇", 0, 6));
            p.Controls.Add(Body("@BotFather에서 만든 봇 토큰과 수신 Chat ID를 입력하세요.", 0, 48, 620));

            p.Controls.Add(Lbl("Bot Token", 0, 88));
            Place(_txtTgToken, p, 0, 110, 540);
            p.Controls.Add(Lbl("Chat ID", 0, 150));
            Place(_txtTgChat, p, 0, 172, 260);

            var (btn, status) = TestRow(p, 224);
            btn.Click += async (s, e) => await RunTest(btn, status,
                () => TestTelegramAsync(_txtTgToken.Text.Trim(), _txtTgChat.Text.Trim()),
                ok => _tgOk = ok);

            p.Controls.Add(Hint("Chat ID 입력 시 테스트 메시지를 실제로 1건 전송합니다.", 0, 270, 620));
        }

        private void BuildFinish(Panel p)
        {
            p.Controls.Add(Title("🎉 설정 요약", 0, 6));
            _finishSummary = new Label
            {
                Location = new Point(0, 56), Size = new Size(660, 260),
                ForeColor = Color.White, Font = new Font("맑은 고딕", 11F)
            };
            p.Controls.Add(_finishSummary);
            p.Controls.Add(Hint("'완료'를 누르면 입력한 키가 DPAPI로 암호화되어 저장됩니다.", 0, 330, 620));
        }

        private void UpdateFinishSummary()
        {
            string Row(string name, bool filled, bool tested) =>
                "  " + (tested ? "✅" : filled ? "🟡" : "⬜") + "  " + name + " — " +
                (tested ? "연결 확인됨" : filled ? "입력됨 (미검증)" : "미설정");

            _finishSummary.Text =
                "입력하신 설정은 다음과 같습니다:\n\n" +
                Row("Gemini AI", !string.IsNullOrWhiteSpace(_txtGemini.Text), _geminiOk) + "\n" +
                Row("한국투자증권 KIS", !string.IsNullOrWhiteSpace(_txtKisKey.Text) && !string.IsNullOrWhiteSpace(_txtKisSecret.Text), _kisOk) + "\n" +
                Row("텔레그램", !string.IsNullOrWhiteSpace(_txtTgToken.Text), _tgOk) + "\n\n" +
                (_kisOk ? "→ KIS가 실시간 시세 우선 제공자로 사용됩니다.\n" : "→ 네이버·야후 시세로 동작합니다.\n");
        }

        // ===== 내비게이션 =====
        private void Go(int delta)
        {
            var t = _current + delta;
            if (t < 0 || t >= _steps.Length) return;
            _current = t;
            RenderStep();
        }

        private void OnNext(object? s, EventArgs e)
        {
            if (_current == _steps.Length - 1)
            {
                SaveAll();
                DialogResult = DialogResult.OK;
                Close();
            }
            else Go(+1);
        }

        private void SaveAll()
        {
            _db.SetSecret("Gemini.ApiKey", _txtGemini.Text.Trim());
            _db.SetSecret("KIS.AppKey", _txtKisKey.Text.Trim());
            _db.SetSecret("KIS.AppSecret", _txtKisSecret.Text.Trim());
            _db.SetSetting("KIS.AccountNo", _txtKisAcc.Text.Trim());
            _db.SetSetting("KIS.IsPaper", _chkPaper.Checked ? "true" : "false");
            _db.SetSecret("Telegram.Token", _txtTgToken.Text.Trim());
            _db.SetSetting("Telegram.ChatId", _txtTgChat.Text.Trim());
            _db.SetSetting("Telegram.Enabled",
                !string.IsNullOrWhiteSpace(_txtTgToken.Text) ? "true" : "false");
            _db.SetSetting("Setup.Completed", "true");
            Log.Info("[Setup] 설정 위저드 저장 완료");
        }

        private async Task RunTest(Button btn, Label status,
            Func<Task<(bool ok, string msg)>> test, Action<bool> setFlag)
        {
            btn.Enabled = false;
            status.ForeColor = Muted;
            status.Text = "🔌 연결 확인 중...";
            try
            {
                var (ok, msg) = await test();
                setFlag(ok);
                status.ForeColor = ok ? OkC : ErrC;
                status.Text = (ok ? "✅ " : "❌ ") + msg;
            }
            catch (Exception ex)
            {
                setFlag(false);
                status.ForeColor = ErrC;
                status.Text = "❌ " + ex.Message;
            }
            finally
            {
                btn.Enabled = true;
                _rail.Invalidate();
            }
        }

        // ===== 연결 테스트 — 공용 ConnectionTester로 위임 (Blazor /setup와 로직 공유) =====
        private static Task<(bool ok, string msg)> TestGeminiAsync(string key) => ConnectionTester.Gemini(key);
        private static Task<(bool ok, string msg)> TestKisAsync(string key, string secret, bool paper) => ConnectionTester.Kis(key, secret, paper);
        private static Task<(bool ok, string msg)> TestTelegramAsync(string token, string chatId) => ConnectionTester.Telegram(token, chatId);

        // ===== UI 헬퍼 =====
        private TextBox MakeInput(bool password) => new()
        {
            Width = 540, Height = 26, BackColor = Field, ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = password,
            Font = new Font("맑은 고딕", 10F)
        };

        private static void Place(Control c, Panel host, int x, int y, int w)
        {
            c.Location = new Point(x, y);
            c.Width = w;
            host.Controls.Add(c);
        }

        private Button MakeButton(string text, Color back) => new()
        {
            Text = text, Size = new Size(100, 34), FlatStyle = FlatStyle.Flat,
            BackColor = back, ForeColor = Color.White, Font = new Font("맑은 고딕", 10F)
        };

        private (Button btn, Label status) TestRow(Panel host, int y)
        {
            var btn = new Button
            {
                Text = "🔌 연결 테스트", Location = new Point(0, y), Size = new Size(130, 32),
                FlatStyle = FlatStyle.Flat, BackColor = Accent, ForeColor = Color.White
            };
            var status = new Label
            {
                Location = new Point(144, y + 6), AutoSize = true, ForeColor = Muted,
                Text = "연결 상태: 미확인"
            };
            host.Controls.Add(btn);
            host.Controls.Add(status);
            return (btn, status);
        }

        private static Label Title(string t, int x, int y) => new()
        {
            Text = t, Location = new Point(x, y), AutoSize = true,
            Font = new Font("맑은 고딕", 15F, FontStyle.Bold), ForeColor = Color.White
        };

        private static Label Body(string t, int x, int y, int w) => new()
        {
            Text = t, Location = new Point(x, y), Size = new Size(w, 190),
            ForeColor = Color.Gainsboro, Font = new Font("맑은 고딕", 10.5F)
        };

        private static Label Lbl(string t, int x, int y) => new()
        {
            Text = t, Location = new Point(x, y), AutoSize = true, ForeColor = Color.LightGray
        };

        private static Label Hint(string t, int x, int y, int w) => new()
        {
            Text = "💡 " + t, Location = new Point(x, y), Size = new Size(w, 40), ForeColor = Muted
        };
    }
}
