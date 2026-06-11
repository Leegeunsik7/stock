// ============================================================================
// ThemeManager - 블랙(다크)/화이트(라이트) 2색 테마 (v2.7)
//  · 색상 팔레트 + Settings('UI.Theme') 영속 + 컨트롤 트리 재귀 적용
//  · 신규/표준 컨트롤은 Apply()로 즉시 리컬러. 커스텀 Paint는 각 화면이 토큰 참조
// ============================================================================
using System;
using System.Drawing;
using System.Windows.Forms;

namespace StockManager.Common
{
    public enum ThemeMode { Dark, Light }

    public static class ThemeManager
    {
        public static ThemeMode Mode { get; private set; } = ThemeMode.Light;
        public static event Action? Changed;

        private static bool D => Mode == ThemeMode.Dark;
        private static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);

        // 디자인 토큰 (다크 / 라이트)
        public static Color Bg        => D ? C(24, 28, 36)  : C(245, 246, 248);
        public static Color Panel     => D ? C(34, 39, 50)  : C(255, 255, 255);
        public static Color Card      => D ? C(40, 46, 58)  : C(252, 252, 253);
        public static Color CardHover => D ? C(50, 56, 70)  : C(234, 239, 245);
        public static Color Field     => D ? C(45, 50, 62)  : C(255, 255, 255);
        public static Color Text      => D ? Color.White    : C(28, 32, 40);
        public static Color SubText   => D ? Color.LightGray : C(96, 104, 116);
        public static Color Accent    => C(70, 130, 200);
        public static Color Grid      => D ? C(55, 60, 72)  : C(214, 219, 226);
        public static Color GridAlt   => D ? C(34, 39, 50)  : C(247, 248, 250);
        public static Color HeaderBg  => D ? C(40, 46, 58)  : C(232, 236, 242);
        public static Color Up        => D ? C(225, 80, 80) : C(200, 40, 40);
        public static Color Down      => D ? C(70, 135, 225): C(40, 90, 200);

        public static void Load(LocalDatabase db)
        {
            try { Mode = db.GetSetting("UI.Theme") == "dark" ? ThemeMode.Dark : ThemeMode.Light; }
            catch { Mode = ThemeMode.Light; }
        }

        public static void Toggle(LocalDatabase db) => Set(D ? ThemeMode.Light : ThemeMode.Dark, db);

        /// <summary>명시 모드 설정 — Blazor 토글처럼 외부 상태가 기준일 때 사용.
        /// ⚠️ 순서 계약 유지: ① 저장 ② ReapplyOpenForms(표준 컨트롤) ③ Changed(커스텀 폼이 마지막 확정)</summary>
        public static void Set(ThemeMode mode, LocalDatabase db)
        {
            Mode = mode;
            try { db.SetSetting("UI.Theme", D ? "dark" : "light"); } catch { }
            ReapplyOpenForms();   // 표준 컨트롤·그리드를 모든 열린 창에 먼저 적용
            Changed?.Invoke();    // 커스텀 테마를 가진 폼이 마지막에 자기 색을 확정
        }

        /// <summary>현재 열려 있는 모든 폼의 표준 컨트롤·그리드를 테마에 맞춤(레거시 포함)</summary>
        public static void ReapplyOpenForms()
        {
            try
            {
                foreach (Form f in Application.OpenForms)
                {
                    Apply(f);
                    f.Invalidate(true);
                }
            }
            catch { }
        }

        /// <summary>DataGridView 전체 색을 테마 토큰으로 일괄 설정 (가독성 보장)</summary>
        public static void StyleGrid(DataGridView g)
        {
            g.BackgroundColor = Bg;
            g.GridColor = Grid;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersDefaultCellStyle.BackColor = HeaderBg;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Text;
            g.DefaultCellStyle.BackColor = Bg;
            g.DefaultCellStyle.ForeColor = Text;
            g.DefaultCellStyle.SelectionBackColor = Accent;
            g.DefaultCellStyle.SelectionForeColor = Color.White;
            g.AlternatingRowsDefaultCellStyle.BackColor = GridAlt;
            g.AlternatingRowsDefaultCellStyle.ForeColor = Text;
            g.RowHeadersDefaultCellStyle.BackColor = HeaderBg;
            g.RowHeadersDefaultCellStyle.ForeColor = Text;
            g.Invalidate();
        }

        /// <summary>컨트롤 트리를 재귀 순회하며 표준 컨트롤 색을 테마에 맞춤</summary>
        public static void Apply(Control root)
        {
            ApplyTo(root);
            foreach (Control c in root.Controls) Apply(c);
        }

        private static void ApplyTo(Control c)
        {
            switch (c)
            {
                case DataGridView g:
                    StyleGrid(g);
                    break;
                case TextBox t: t.BackColor = Field; t.ForeColor = Text; break;
                case NumericUpDown nud: nud.BackColor = Field; nud.ForeColor = Text; break;
                case ComboBox cb: cb.BackColor = Field; cb.ForeColor = Text; break;
                case ListBox lb: lb.BackColor = Field; lb.ForeColor = Text; break;
                case CheckBox ck: ck.ForeColor = Text; break;
                case Button b:
                    if (b.BackColor != Accent) { b.BackColor = Panel; b.ForeColor = Text; }
                    break;
                case Label l: l.ForeColor = Text; l.BackColor = Color.Transparent; break;
                case StatusStrip ss:
                    ss.BackColor = Panel; ss.ForeColor = Text;
                    foreach (ToolStripItem it in ss.Items) { it.ForeColor = Text; if (it is ToolStripControlHost h && h.Control != null) h.Control.ForeColor = Text; }
                    break;
                case ToolStrip ts:
                    ts.BackColor = Panel; ts.ForeColor = Text;
                    foreach (ToolStripItem it in ts.Items) it.ForeColor = Text;
                    break;
                case TabControl tc: tc.BackColor = Bg; break;
                case Panel p: p.BackColor = Bg; p.ForeColor = Text; break;
                case Form f: f.BackColor = Bg; f.ForeColor = Text; break;
                default: break;
            }
        }
    }
}
