// ============================================================================
// StockManager Launcher (v2.3)
// [개선점]
//  · GdiCache 전면 적용
//  · 설정 다이얼로그 실제 구현 (Gemini API 키, KIS AppKey/Secret)
//  · 알림 센터 카드 추가 (7번째)
//  · 텔레그램 봇 설정 포함
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using StockManager.Common;

namespace StockManager
{
    public class LauncherForm : Form
    {
        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase _db;
        private readonly UpbitProvider _upbit = new();
        private PaperBroker _paper = null!;
        private Label _lblAlerts = null!;
        private Panel _header = null!;
        private TableLayoutPanel _grid = null!;
        private StatusStrip _status = null!;
        private Label _title = null!;
        private Label _sub = null!;
        private Button _themeBtn = null!;
        private CryptoWatcher.CoinMiniWidget _mini = null!;
        private readonly List<(Panel panel, Color accent)> _cards = new();
        private List<CryptoMarket> _coinMarkets = new();

        public LauncherForm(IQuoteProvider provider, LocalDatabase db)
        {
            _provider = provider;
            _db = db;
            _paper = new PaperBroker(db);
            InitializeUI();

            CommandPalette.Register(BuildCommands, ParseQuickAction);   // 전역 Ctrl+K + 퀵 액션
            ThemeManager.Changed += ApplyTheme;
            _ = LoadCoinMarketsAsync();

            AlertCenter.Instance.AlertRaised += OnAlert;
            FormClosing += (s, e) =>
            {
                AlertCenter.Instance.AlertRaised -= OnAlert;
                ThemeManager.Changed -= ApplyTheme;
            };

            var clockTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            clockTimer.Tick += (s, e) =>
            {
                _lblAlerts.Text = $"🔔 읽지 않은 알림: {AlertCenter.Instance.UnreadCount}건";
            };
            clockTimer.Start();
        }

        private void InitializeUI()
        {
            Text = "📊 MS2-E Stock Suite v2.8 - 통합 주식 관리 플랫폼";
            Size = new Size(1100, 940);
            WindowState = FormWindowState.Maximized;   // 풀스크린(최대화)
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = ThemeManager.Bg;
            ForeColor = ThemeManager.Text;
            Font = new Font("맑은 고딕", 10F);

            _header = new Panel { Dock = DockStyle.Top, Height = 120, BackColor = ThemeManager.HeaderBg };
            _title = new Label
            {
                Text = "📊 MS2-E Stock Suite",
                Font = new Font("맑은 고딕", 28F, FontStyle.Bold),
                ForeColor = ThemeManager.Text,
                Location = new Point(35, 25), AutoSize = true
            };
            _sub = new Label
            {
                Text = "한국·미국·코인 통합 관리 플랫폼  ·  v3.0 (모의투자 Paper Trading)",
                Font = new Font("맑은 고딕", 11F),
                ForeColor = ThemeManager.SubText,
                Location = new Point(38, 78), AutoSize = true
            };
            _themeBtn = new Button
            {
                Text = ThemeManager.Mode == ThemeMode.Dark ? "☀ 화이트" : "🌙 블랙",
                Size = new Size(110, 30), Location = new Point(720, 16),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat, BackColor = ThemeManager.Accent, ForeColor = Color.White
            };
            _themeBtn.Click += (s, e) => ThemeManager.Toggle(_db);
            var paletteHint = new Label
            {
                Text = "Ctrl + K",
                Location = new Point(840, 22), Size = new Size(70, 22),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                ForeColor = ThemeManager.SubText, TextAlign = ContentAlignment.MiddleCenter
            };
            _mini = new CryptoWatcher.CoinMiniWidget
            {
                Size = new Size(240, 62), Location = new Point(820, 50),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                BackColor = ThemeManager.Panel
            };
            _mini.ClickAction = () => OpenThemed(new CryptoWatcher.MainForm(_db));
            _header.Controls.Add(_title);
            _header.Controls.Add(_sub);
            _header.Controls.Add(_themeBtn);
            _header.Controls.Add(paletteHint);
            _header.Controls.Add(_mini);

            _grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 4, ColumnCount = 3,
                Padding = new Padding(24),
                BackColor = ThemeManager.Bg
            };
            for (int i = 0; i < 3; i++)
                _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            for (int i = 0; i < 4; i++)
                _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));

            _grid.Controls.Add(MakeCard("📈", "실시간 시세", "시세·히트맵·52주 신고가 스캔",
                Color.FromArgb(220, 50, 50), () => OpenThemed(new RealtimeWatcher.MainForm(_provider, _db))), 0, 0);
            _grid.Controls.Add(MakeCard("💼", "포트폴리오", "보유·거래·세금·배당·상관관계",
                Color.FromArgb(50, 200, 100), () => OpenThemed(new Portfolio.MainForm(_provider, _db))), 1, 0);
            _grid.Controls.Add(MakeCard("🧠", "AI 분석", "Gemini 기업 분석·레이더 차트",
                Color.FromArgb(150, 80, 200), () => OpenThemed(new AIAnalysis.MainForm(_provider, _db))), 2, 0);
            _grid.Controls.Add(MakeCard("📰", "뉴스 레이더", "RSS 수집·Gemini 감성분석",
                Color.FromArgb(200, 150, 60), () => OpenThemed(new NewsRadar.MainForm(_db))), 0, 1);
            _grid.Controls.Add(MakeCard("⚡", "시그널 엔진", "백테스팅·섹터 로테이션",
                Color.FromArgb(200, 100, 50), () => OpenThemed(new SignalEngine.MainForm(_provider, _db))), 1, 1);
            _grid.Controls.Add(MakeCard("🔔", "알림 센터", "가격·시그널·뉴스 통합 스트림",
                Color.FromArgb(220, 180, 50), () => OpenThemed(new AlertCenterForm(_db))), 2, 1);
            _grid.Controls.Add(MakeCard("📑", "주간 리포트", "포트폴리오 PDF 주간 요약",
                Color.FromArgb(60, 160, 180), GenerateWeeklyReport), 0, 2);
            _grid.Controls.Add(MakeCard("⚙️", "설정", "설정 마법사 · Gemini/KIS/텔레그램 + 연결 테스트",
                Color.FromArgb(100, 100, 100), ShowSetupWizard), 1, 2);
            _grid.Controls.Add(MakeCard("ℹ️", "About", "v3.0 · 모의투자",
                Color.FromArgb(80, 130, 180), () => MessageBox.Show("MS2-E Stock Suite v2.8\n\n" +
                    "· v2.8 신규: 화이트 기본·풀스크린 · CSV 원클릭 내보내기 · Ctrl+K 퀵 액션(매크로) · 코인 스파크라인 · 창 위치 기억\n" +
                    "· v2.7: Ctrl+K 팔레트 · 코인 미니 위젯 · 블랙/화이트 테마\n" +
                    "· v2.6: 총자산 통합 대시보드 · 코인 차트 연결\n" +
                    "· v2.5: 코인 실시간 WS · 김프 · 알림 / v2.4: 설정 마법사\n\n" +
                    "MS2-E v2.1.1 통합 주식 모듈", "About")), 2, 2);
            _grid.Controls.Add(MakeCard("🪙", "코인 실시간", "업비트 WS·바이낸스·김프·지정가 알림",
                Color.FromArgb(240, 170, 40), () => OpenThemed(new CryptoWatcher.MainForm(_db))), 0, 3);
            _grid.Controls.Add(MakeCard("🧮", "총자산", "주식+코인 통합 평가·파이·손익",
                Color.FromArgb(90, 185, 160), () => OpenThemed(new TotalAsset.MainForm(_provider, _db))), 1, 3);
            _grid.Controls.Add(MakeCard("🧪", "모의투자", "가상 시드 매매·주문·포지션 (Paper)",
                Color.FromArgb(150, 120, 70), () => OpenThemed(new PaperTrading.MainForm(_provider, _db))), 2, 3);

            _status = new StatusStrip { BackColor = ThemeManager.Panel, SizingGrip = false };
            _lblAlerts = new Label { Text = "🔔 읽지 않은 알림: 0건", ForeColor = Color.Gold, AutoSize = true };
            _status.Items.Add(new ToolStripControlHost(_lblAlerts));

            Controls.Add(_grid);
            Controls.Add(_status);
            Controls.Add(_header);
        }

        private Panel MakeCard(string icon, string title, string desc, Color accent, Action onClick)
        {
            var p = new Panel
            {
                Margin = new Padding(10),
                BackColor = ThemeManager.Card,
                Cursor = Cursors.Hand,
                Dock = DockStyle.Fill
            };
            p.Paint += (s, e) =>
            {
                var g = e.Graphics;
                var r = p.ClientRectangle;
                g.FillRectangle(GdiCache.Brush(p.BackColor), r);
                g.FillRectangle(GdiCache.Brush(accent), 0, 0, 5, r.Height);
                g.DrawString(icon, GdiCache.Emoji, GdiCache.Brush(ThemeManager.Text), r.X + 25, r.Y + 15);
                g.DrawString(title, GdiCache.Title, GdiCache.Brush(ThemeManager.Text), r.X + 20, r.Y + 105);
                g.DrawString(desc, GdiCache.Normal, GdiCache.Brush(ThemeManager.SubText), r.X + 20, r.Y + 135);
            };
            p.Click += (s, e) => { try { onClick(); } catch (Exception ex) { MessageBox.Show(ex.Message); } };
            p.MouseEnter += (s, e) => { p.BackColor = ThemeManager.CardHover; p.Invalidate(); };
            p.MouseLeave += (s, e) => { p.BackColor = ThemeManager.Card; p.Invalidate(); };
            _cards.Add((p, accent));
            return p;
        }

        // ===== 테마 / 팔레트 / 위젯 =====
        private void OpenThemed(Form f)
        {
            try { ThemeManager.Apply(f); } catch { }
            f.Show();
        }

        private void ApplyTheme()
        {
            BackColor = ThemeManager.Bg;
            ForeColor = ThemeManager.Text;
            if (_header != null) _header.BackColor = ThemeManager.HeaderBg;
            if (_title != null) _title.ForeColor = ThemeManager.Text;
            if (_sub != null) _sub.ForeColor = ThemeManager.SubText;
            if (_grid != null) _grid.BackColor = ThemeManager.Bg;
            if (_status != null) _status.BackColor = ThemeManager.Panel;
            if (_themeBtn != null) _themeBtn.Text = ThemeManager.Mode == ThemeMode.Dark ? "☀ 화이트" : "🌙 블랙";
            if (_mini != null) { _mini.BackColor = ThemeManager.Panel; _mini.Invalidate(); }
            foreach (var (panel, _) in _cards) { panel.BackColor = ThemeManager.Card; panel.Invalidate(); }
            Invalidate(true);
        }

        private async System.Threading.Tasks.Task LoadCoinMarketsAsync()
        {
            try { _coinMarkets = await _upbit.GetMarketsAsync(); } catch { }
        }

        private async void OpenCoinChartFromPalette(string market)
        {
            try
            {
                var candles = await _upbit.GetCandlesAsync(market, 200);
                if (candles.Count < 5) return;
                var name = _coinMarkets.FirstOrDefault(m => m.Market == market)?.KoreanName ?? market;
                OpenThemed(new RealtimeWatcher.ChartForm($"{name} ({market})", candles));
            }
            catch { }
        }

        private List<PaletteCommand> BuildCommands()
        {
            var cmds = new List<PaletteCommand>();
            void M(string title, Action a) => cmds.Add(new PaletteCommand { Category = "모듈", Title = title, Action = a });
            M("실시간 시세", () => OpenThemed(new RealtimeWatcher.MainForm(_provider, _db)));
            M("포트폴리오", () => OpenThemed(new Portfolio.MainForm(_provider, _db)));
            M("AI 분석", () => OpenThemed(new AIAnalysis.MainForm(_provider, _db)));
            M("뉴스 레이더", () => OpenThemed(new NewsRadar.MainForm(_db)));
            M("시그널 엔진", () => OpenThemed(new SignalEngine.MainForm(_provider, _db)));
            M("알림 센터", () => OpenThemed(new AlertCenterForm(_db)));
            M("코인 실시간", () => OpenThemed(new CryptoWatcher.MainForm(_db)));
            M("총자산", () => OpenThemed(new TotalAsset.MainForm(_provider, _db)));
            M("모의투자 (Paper)", () => OpenThemed(new PaperTrading.MainForm(_provider, _db)));
            M("스마트 복합 알림", () => OpenThemed(new CompositeAlerts.MainForm(_db, _provider)));
            M("거래 설정 (모의/실전·키)", () => OpenThemed(new BrokerSettingsForm(_db)));
            M("알림 이력", () => OpenThemed(new AlertHistoryForm(_db)));
            M("주간 리포트 생성", GenerateWeeklyReport);
            M("설정 마법사", ShowSetupWizard);
            cmds.Add(new PaletteCommand
            {
                Category = "테마",
                Title = ThemeManager.Mode == ThemeMode.Dark ? "화이트(라이트)로 전환" : "블랙(다크)로 전환",
                Action = () => ThemeManager.Toggle(_db)
            });
            foreach (var m in _coinMarkets)
            {
                var mk = m.Market; var nm = m.KoreanName;
                cmds.Add(new PaletteCommand { Category = "코인", Title = $"{nm} ({mk})", Action = () => OpenCoinChartFromPalette(mk) });
            }
            foreach (var h in _db.GetAllHoldings())
            {
                var sym = h.Symbol; var mkt = h.Market; var nm = h.Name;
                cmds.Add(new PaletteCommand { Category = "주식", Title = $"{nm} ({sym})", Action = () => OpenThemed(new RealtimeWatcher.ChartForm(_provider, sym, mkt)) });
            }
            foreach (var n in new[] { "단타뷰", "분석뷰", "내 프리셋" })
            {
                var nm = n;
                cmds.Add(new PaletteCommand { Category = "레이아웃 저장", Title = $"현재 창 배치를 '{nm}'(으)로 저장", Action = () => SaveLayoutPreset(nm) });
                cmds.Add(new PaletteCommand { Category = "레이아웃 열기", Title = $"'{nm}' 배치 불러오기", Action = () => LoadLayoutPreset(nm) });
            }
            return cmds;
        }

        // ===== 다중 창 레이아웃 프리셋 =====
        private Dictionary<string, Func<Form>> OpenerMap() => new()
        {
            ["Realtime"] = () => new RealtimeWatcher.MainForm(_provider, _db),
            ["Portfolio"] = () => new Portfolio.MainForm(_provider, _db),
            ["AI"] = () => new AIAnalysis.MainForm(_provider, _db),
            ["News"] = () => new NewsRadar.MainForm(_db),
            ["Signal"] = () => new SignalEngine.MainForm(_provider, _db),
            ["Crypto"] = () => new CryptoWatcher.MainForm(_db),
            ["TotalAsset"] = () => new TotalAsset.MainForm(_provider, _db),
        };

        private static string KeyOf(Form f) => f switch
        {
            RealtimeWatcher.MainForm => "Realtime",
            Portfolio.MainForm => "Portfolio",
            AIAnalysis.MainForm => "AI",
            NewsRadar.MainForm => "News",
            SignalEngine.MainForm => "Signal",
            CryptoWatcher.MainForm => "Crypto",
            TotalAsset.MainForm => "TotalAsset",
            _ => ""
        };

        private void SaveLayoutPreset(string name)
        {
            var items = new List<string>();
            foreach (Form f in Application.OpenForms)
            {
                var k = KeyOf(f);
                if (k == "" || f.WindowState != FormWindowState.Normal) continue;
                items.Add($"{k}:{f.Left},{f.Top},{f.Width},{f.Height}");
            }
            if (items.Count == 0) { MessageBox.Show("저장할 모듈 창이 없습니다.\n(최대화 상태 창은 제외됩니다)"); return; }
            _db.SetSetting("Layout." + name, string.Join(";", items));
            MessageBox.Show($"✅ 레이아웃 프리셋 저장: '{name}'  ({items.Count}개 창)");
        }

        private void LoadLayoutPreset(string name)
        {
            var v = _db.GetSetting("Layout." + name);
            if (string.IsNullOrWhiteSpace(v)) { MessageBox.Show($"저장된 프리셋이 없습니다: '{name}'"); return; }
            var map = OpenerMap();
            foreach (var part in v.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');
                if (kv.Length != 2 || !map.TryGetValue(kv[0], out var opener)) continue;
                var b = kv[1].Split(',');
                if (b.Length != 4) continue;

                Form? f = Application.OpenForms.Cast<Form>().FirstOrDefault(x => KeyOf(x) == kv[0] && !x.IsDisposed);
                if (f == null) { f = opener(); ThemeManager.Apply(f); f.Show(); }
                if (int.TryParse(b[0], out var x) && int.TryParse(b[1], out var y) &&
                    int.TryParse(b[2], out var w) && int.TryParse(b[3], out var h))
                {
                    f.StartPosition = FormStartPosition.Manual;
                    f.WindowState = FormWindowState.Normal;
                    f.Bounds = new Rectangle(x, y, w, h);
                    f.BringToFront();
                }
            }
        }

        // ===== Ctrl+K 퀵 액션 (매크로) =====
        private PaletteCommand? ParseQuickAction(string q)
        {
            var t = q.Trim();
            if (t.Length == 0) return null;
            var low = t.ToLowerInvariant();

            // 테마 전환
            if (t.Contains("테마"))
            {
                bool toLight = t.Contains("화이트") || t.Contains("라이트") || low.Contains("light") || low.Contains("white");
                bool toDark = t.Contains("블랙") || t.Contains("다크") || low.Contains("dark") || low.Contains("black");
                return new PaletteCommand
                {
                    Category = "⚡ 액션",
                    Title = toLight ? "테마 → 화이트" : toDark ? "테마 → 블랙" : "테마 전환",
                    Action = () =>
                    {
                        if (toLight && ThemeManager.Mode == ThemeMode.Dark) ThemeManager.Toggle(_db);
                        else if (toDark && ThemeManager.Mode == ThemeMode.Light) ThemeManager.Toggle(_db);
                        else if (!toLight && !toDark) ThemeManager.Toggle(_db);
                    }
                };
            }

            // 모든 알림 끄기
            if (t.Contains("알림") && (t.Contains("끄") || t.Contains("해제") || low.Contains("off")))
            {
                return new PaletteCommand
                {
                    Category = "⚡ 액션",
                    Title = "모든 코인 알림 + 텔레그램 자동전송 끄기",
                    Action = () =>
                    {
                        _db.SetSetting("Telegram.Enabled", "false");
                        DisableAllCoinAlerts();
                        MessageBox.Show("✅ 텔레그램 자동 전송 OFF · 코인 지정가 알림 모두 제거");
                    }
                };
            }

            var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // "{코인} 알림 {금액}"  → 지정가 알림 등록
            if (parts.Length >= 3 && parts[1].Contains("알림"))
            {
                var market = "KRW-" + parts[0].ToUpperInvariant();
                var amt = ParseAmount(parts[2]);
                if (amt != null)
                    return new PaletteCommand
                    {
                        Category = "⚡ 액션",
                        Title = $"코인 알림 등록: {market} {amt:N0}원 이상",
                        Action = () => { AddCoinAlert(market, amt.Value); MessageBox.Show($"✅ 알림 등록: {market} {amt:N0}원 이상"); }
                    };
            }

            // "{코인} {금액} 매수/매도"  → 모의투자 시장가 주문
            if (parts.Length >= 3 && (parts[2].Contains("매수") || parts[2].Contains("매도")))
            {
                var market = "KRW-" + parts[0].ToUpperInvariant();
                var amt = ParseAmount(parts[1]);
                var side = parts[2].Contains("매도") ? PaperSide.Sell : PaperSide.Buy;
                if (amt != null)
                    return new PaletteCommand
                    {
                        Category = "⚡ 모의 주문",
                        Title = $"{(side == PaperSide.Buy ? "모의 매수" : "모의 매도")}: {market} {amt:N0}원",
                        Action = () => PaperQuick(market, amt.Value, side)
                    };
            }
            return null;
        }

        private async void PaperQuick(string market, decimal krw, PaperSide side)
        {
            try
            {
                var ts = await _upbit.GetTickersAsync(new[] { market });
                var t = ts.FirstOrDefault();
                var price = t?.Price ?? 0m;
                if (price <= 0) { MessageBox.Show("시세를 가져오지 못했습니다."); return; }
                var name = t?.KoreanName ?? market.Replace("KRW-", "");
                var qty = krw / price;

                // v4.67.0 — 브릿지에서 gate/att 추출 → ShadowClearingBroker 배선(등록 전이면 null → 기본 폴백).
                var (shadowGate, liveAtt) = ExecutionInfrastructureHub.TryGet();
                IBroker broker = BrokerHub.Create(_db, shadowGate, liveAtt);   // 모의/실전 토글 반영(실전은 안전상 거부)
                if (side == PaperSide.Sell)
                {
                    var held = broker.HeldQty(PaperAsset.Coin, market);
                    qty = Math.Min(qty, held);
                    if (qty <= 0) { MessageBox.Show("보유 수량이 없습니다."); return; }
                }
                var verb = side == PaperSide.Buy ? "매수" : "매도";
                using var confirm = new OrderConfirmDialog($"{verb} 확인",
                    $"{name} ({market})\n{verb} {qty:0.####}개 @ {price:N0}원\n모드: {broker.Name}", broker.IsLive);
                if (confirm.ShowDialog() != DialogResult.OK) return;

                var (ok, msg) = broker.Place(PaperAsset.Coin, market, name, side, PaperKind.Market, qty, 0, price);
                MessageBox.Show(msg, ok ? broker.Name : "실패");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private static decimal? ParseAmount(string s)
        {
            s = s.Replace(",", "").Replace("원", "").Trim();
            decimal mult = 1m;
            if (s.EndsWith("억")) { mult = 100_000_000m; s = s[..^1]; }
            else if (s.EndsWith("만")) { mult = 10_000m; s = s[..^1]; }
            return decimal.TryParse(s, out var v) ? v * mult : (decimal?)null;
        }

        private CryptoWatcher.CryptoState LoadCryptoState()
        {
            try
            {
                var j = _db.GetSetting("Crypto.State");
                if (!string.IsNullOrWhiteSpace(j))
                    return System.Text.Json.JsonSerializer.Deserialize<CryptoWatcher.CryptoState>(j) ?? new CryptoWatcher.CryptoState();
            }
            catch { }
            return new CryptoWatcher.CryptoState();
        }

        private void SaveCryptoState(CryptoWatcher.CryptoState s)
        {
            try { _db.SetSetting("Crypto.State", System.Text.Json.JsonSerializer.Serialize(s)); } catch { }
        }

        private void DisableAllCoinAlerts()
        {
            var s = LoadCryptoState(); s.Alerts.Clear(); SaveCryptoState(s);
        }

        private void AddCoinAlert(string market, decimal target)
        {
            var s = LoadCryptoState();
            if (!s.Watch.Contains(market)) s.Watch.Add(market);
            s.Alerts.Add(new CryptoWatcher.CoinAlert { Market = market, Above = true, Target = target });
            SaveCryptoState(s);
        }

        private async void RecordCoinBuy(string market, decimal krwAmount)
        {
            try
            {
                var ticks = await _upbit.GetTickersAsync(new[] { market });
                var price = ticks.FirstOrDefault()?.Price ?? 0m;
                if (price <= 0) { MessageBox.Show("시세를 가져오지 못했습니다."); return; }
                var qty = krwAmount / price;
                var s = LoadCryptoState();
                if (!s.Watch.Contains(market)) s.Watch.Add(market);
                if (s.Holdings.TryGetValue(market, out var h) && h.Qty > 0)
                {
                    var totalCost = h.Avg * h.Qty + krwAmount;
                    var totalQty = h.Qty + qty;
                    h.Qty = totalQty;
                    h.Avg = totalQty > 0 ? totalCost / totalQty : price;
                }
                else s.Holdings[market] = new CryptoWatcher.CryptoHolding { Qty = qty, Avg = price };
                SaveCryptoState(s);
                MessageBox.Show($"✅ 로컬 보유 기록\n{market}  {qty:0.########}개 @ {price:N0}원\n(실제 거래소 주문이 아닙니다)");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void OnAlert(object? s, AlertItem a)
        {
            if (InvokeRequired) { BeginInvoke(() => OnAlert(s, a)); return; }
            if (a.Level >= AlertLevel.Warning) TrayNotifier.Show(a);
        }

        private void GenerateWeeklyReport()
        {
            try
            {
                var report = WeeklyReportGenerator.Build(_db);
                using var dlg = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = $"weekly_report_{DateTime.Now:yyyyMMdd}.pdf" };
                if (dlg.ShowDialog() != DialogResult.OK) return;
                WeeklyReportGenerator.SaveToPdf(report, dlg.FileName);
                MessageBox.Show($"✅ 주간 리포트 저장됨\n{dlg.FileName}", "리포트");
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); }
                catch { }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void ShowSetupWizard()
        {
            using var w = new SetupWizard(_db);
            w.ShowDialog();
        }

        private void ShowSettings()
        {
            using var dlg = new SettingsDialog(_db);
            dlg.ShowDialog();
        }
    }

    // ===== 설정 다이얼로그 =====
    public class SettingsDialog : Form
    {
        private readonly LocalDatabase _db;
        public SettingsDialog(LocalDatabase db) { _db = db; InitializeUI(); }

        private void InitializeUI()
        {
            Text = "⚙️ 설정";
            Size = new Size(620, 560);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(24, 28, 36);
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 10F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;

            var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(25) };

            int y = 15;
            AddSection(panel, "🧠 Gemini AI", ref y);
            var txtGemini = AddTextBox(panel, "API Key", "Gemini.ApiKey", ref y, password: true, secret: true);

            y += 15;
            AddSection(panel, "🏦 한국투자증권 KIS", ref y);
            var txtKisKey = AddTextBox(panel, "App Key", "KIS.AppKey", ref y, password: true, secret: true);
            var txtKisSecret = AddTextBox(panel, "App Secret", "KIS.AppSecret", ref y, password: true, secret: true);
            var txtKisAcc = AddTextBox(panel, "계좌번호 (8자리-2자리)", "KIS.AccountNo", ref y);
            var chkPaper = new CheckBox
            {
                Text = "모의투자 모드",
                Location = new Point(0, y),
                ForeColor = Color.White,
                AutoSize = true,
                Checked = _db.GetSetting("KIS.IsPaper") != "false"
            };
            panel.Controls.Add(chkPaper);
            y += 35;

            AddSection(panel, "📱 텔레그램 봇", ref y);
            var txtTgToken = AddTextBox(panel, "Bot Token", "Telegram.Token", ref y, password: true, secret: true);
            var txtTgChat = AddTextBox(panel, "Chat ID", "Telegram.ChatId", ref y);

            var btnSave = new Button
            {
                Text = "💾 저장",
                Location = new Point(350, y + 20),
                Size = new Size(100, 36),
                BackColor = Color.FromArgb(70, 130, 200),
                ForeColor = Color.White, FlatStyle = FlatStyle.Flat
            };
            btnSave.Click += (s, e) =>
            {
                _db.SetSecret("Gemini.ApiKey", txtGemini.Text);
                _db.SetSecret("KIS.AppKey", txtKisKey.Text);
                _db.SetSecret("KIS.AppSecret", txtKisSecret.Text);
                _db.SetSetting("KIS.AccountNo", txtKisAcc.Text);
                _db.SetSetting("KIS.IsPaper", chkPaper.Checked ? "true" : "false");
                _db.SetSecret("Telegram.Token", txtTgToken.Text);
                _db.SetSetting("Telegram.ChatId", txtTgChat.Text);
                MessageBox.Show("✅ 저장 완료 (API 키는 DPAPI로 암호화됨)");
                DialogResult = DialogResult.OK; Close();
            };
            var btnTest = new Button
            {
                Text = "📱 텔레그램 테스트",
                Location = new Point(180, y + 20),
                Size = new Size(160, 36),
                BackColor = Color.FromArgb(60, 160, 60),
                ForeColor = Color.White, FlatStyle = FlatStyle.Flat
            };
            btnTest.Click += async (s, e) =>
            {
                _db.SetSecret("Telegram.Token", txtTgToken.Text);
                _db.SetSetting("Telegram.ChatId", txtTgChat.Text);
                var ok = await TelegramNotifier.SendAsync(_db, "✅ MS2-E Stock Suite 연결 테스트 메시지");
                MessageBox.Show(ok ? "✅ 전송 성공" : "❌ 전송 실패");
            };
            panel.Controls.Add(btnSave);
            panel.Controls.Add(btnTest);

            Controls.Add(panel);
        }

        private void AddSection(Panel parent, string title, ref int y)
        {
            parent.Controls.Add(new Label
            {
                Text = title,
                Font = new Font("맑은 고딕", 12F, FontStyle.Bold),
                ForeColor = Color.Gold,
                Location = new Point(0, y), AutoSize = true
            });
            y += 30;
        }

        private TextBox AddTextBox(Panel parent, string label, string key, ref int y, bool password = false, bool secret = false)
        {
            parent.Controls.Add(new Label
            {
                Text = label, Location = new Point(0, y + 4),
                AutoSize = true, ForeColor = Color.LightGray
            });
            var t = new TextBox
            {
                Location = new Point(170, y),
                Width = 380, Height = 25,
                BackColor = Color.FromArgb(40, 46, 58),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                UseSystemPasswordChar = password,
                Text = (secret ? _db.GetSecret(key) : _db.GetSetting(key)) ?? ""
            };
            parent.Controls.Add(t);
            y += 35;
            return t;
        }
    }
}
