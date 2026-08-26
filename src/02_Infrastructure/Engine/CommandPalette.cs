// ============================================================================
// CommandPalette - 전역 Ctrl+K 명령 팔레트 (v2.7)
//  · IMessageFilter로 앱 전역에서 Ctrl+K 감지 → 퍼지 검색 팝업
//  · 명령 목록은 Launcher가 provider로 주입 (모듈 열기/테마/종목·코인 검색 등)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MS2E.Infrastructure
{
    public class PaletteCommand
    {
        public string Title { get; set; } = "";
        public string Category { get; set; } = "";
        public Action Action { get; set; } = () => { };
    }

    public static class CommandPalette
    {
        private static Func<List<PaletteCommand>>? _provider;
        private static Func<string, PaletteCommand?>? _quick;
        private static HotkeyFilter? _filter;
        private static bool _open;

        public static void Register(Func<List<PaletteCommand>> provider, Func<string, PaletteCommand?>? quick = null)
        {
            _provider = provider;
            _quick = quick;
            if (_filter == null)
            {
                _filter = new HotkeyFilter();
                Application.AddMessageFilter(_filter);
            }
        }

        public static void Show()
        {
            if (_provider == null || _open) return;
            _open = true;
            try
            {
                using var f = new PaletteForm(_provider(), _quick);
                f.ShowDialog();
                f.Selected?.Invoke();
            }
            catch (Exception ex) { Log.Warn($"[Palette] {ex.Message}"); }
            finally { _open = false; }
        }

        private class HotkeyFilter : IMessageFilter
        {
            public bool PreFilterMessage(ref Message m)
            {
                const int WM_KEYDOWN = 0x100;
                if (m.Msg == WM_KEYDOWN)
                {
                    var key = (Keys)m.WParam.ToInt32();
                    if (key == Keys.K && (Control.ModifierKeys & Keys.Control) == Keys.Control)
                    {
                        Show();
                        return true;
                    }
                }
                return false;
            }
        }
    }

    public class PaletteForm : Form
    {
        public Action? Selected { get; private set; }
        private readonly List<PaletteCommand> _all;
        private readonly Func<string, PaletteCommand?>? _quick;
        private readonly TextBox _search;
        private readonly ListBox _list;

        public PaletteForm(List<PaletteCommand> commands, Func<string, PaletteCommand?>? quick = null)
        {
            _all = commands ?? new List<PaletteCommand>();
            _quick = quick;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(620, 420);
            BackColor = ThemeManager.Panel;
            ForeColor = ThemeManager.Text;
            Font = new Font("맑은 고딕", 11F);
            ShowInTaskbar = false;

            var border = new Panel { Dock = DockStyle.Fill, Padding = new Padding(2), BackColor = ThemeManager.Accent };
            var inner = new Panel { Dock = DockStyle.Fill, BackColor = ThemeManager.Panel, Padding = new Padding(12) };

            _search = new TextBox
            {
                Dock = DockStyle.Top, Height = 36, BorderStyle = BorderStyle.FixedSingle,
                BackColor = ThemeManager.Field, ForeColor = ThemeManager.Text,
                Font = new Font("맑은 고딕", 13F)
            };
            _search.TextChanged += (s, e) => Render(_search.Text.Trim());

            _list = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                BackColor = ThemeManager.Panel, ForeColor = ThemeManager.Text,
                Font = new Font("맑은 고딕", 11F), ItemHeight = 28,
                IntegralHeight = false
            };
            _list.DoubleClick += (s, e) => Execute();

            var hint = new Label
            {
                Dock = DockStyle.Bottom, Height = 26, ForeColor = ThemeManager.SubText,
                Text = "  ↑↓ 이동 · Enter 실행 · Esc 닫기", TextAlign = ContentAlignment.MiddleLeft
            };

            inner.Controls.Add(_list);
            inner.Controls.Add(hint);
            inner.Controls.Add(_search);
            border.Controls.Add(inner);
            Controls.Add(border);

            KeyPreview = true;
            _search.KeyDown += OnKey;
            _list.KeyDown += OnKey;
            Deactivate += (s, e) => Close();

            Render("");
            Shown += (s, e) => _search.Focus();
        }

        private void OnKey(object? s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { Execute(); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { MoveSel(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { MoveSel(-1); e.Handled = true; }
        }

        private void MoveSel(int d)
        {
            if (_list.Items.Count == 0) return;
            int i = Math.Max(0, Math.Min(_list.Items.Count - 1, _list.SelectedIndex + d));
            _list.SelectedIndex = i;
        }

        private List<PaletteCommand> _filtered = new();

        private void Render(string q)
        {
            var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _filtered = _all.Where(c =>
            {
                var hay = (c.Category + " " + c.Title).ToLowerInvariant();
                return tokens.All(t => hay.Contains(t.ToLowerInvariant()));
            }).Take(200).ToList();

            // 퀵 액션(매크로)을 맨 위에
            if (_quick != null && !string.IsNullOrWhiteSpace(q))
            {
                try { var qa = _quick(q); if (qa != null) _filtered.Insert(0, qa); }
                catch { }
            }

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var c in _filtered)
                _list.Items.Add(string.IsNullOrEmpty(c.Category) ? c.Title : $"[{c.Category}]  {c.Title}");
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            _list.EndUpdate();
        }

        private void Execute()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _filtered.Count) return;
            Selected = _filtered[i].Action;   // 닫힌 뒤 호출 (재진입 방지)
            Close();
        }
    }
}
