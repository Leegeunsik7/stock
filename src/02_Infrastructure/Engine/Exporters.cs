// ============================================================================
// Exporters & WindowMemory - CSV 원클릭 내보내기 / 창 위치 기억 (v2.8)
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace MS2E.Infrastructure
{
    public static class CsvExporter
    {
        /// <summary>DataGridView 내용을 CSV로 저장 (Excel 한글 호환 UTF-8 BOM)</summary>
        public static void ExportGrid(DataGridView g, string suggestedName)
        {
            var headers = g.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText);
            var rows = new List<IEnumerable<string>>();
            foreach (DataGridViewRow r in g.Rows)
            {
                if (r.IsNewRow) continue;
                rows.Add(g.Columns.Cast<DataGridViewColumn>().Select(c => r.Cells[c.Index].Value?.ToString() ?? ""));
            }
            Export(suggestedName, headers, rows);
        }

        public static void Export(string suggestedName, IEnumerable<string> headers, IEnumerable<IEnumerable<string>> rows)
        {
            try
            {
                using var dlg = new SaveFileDialog
                {
                    Filter = "CSV 파일|*.csv",
                    FileName = suggestedName.EndsWith(".csv") ? suggestedName : suggestedName + ".csv"
                };
                if (dlg.ShowDialog() != DialogResult.OK) return;

                var sb = new StringBuilder();
                sb.AppendLine(string.Join(",", headers.Select(Q)));
                foreach (var row in rows)
                    sb.AppendLine(string.Join(",", row.Select(Q)));

                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // BOM
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dlg.FileName) { UseShellExecute = true }); }
                catch { }
            }
            catch (Exception ex) { MessageBox.Show("CSV 내보내기 실패: " + ex.Message); }
        }

        private static string Q(string? s)
        {
            s ??= "";
            return s.Contains(',') || s.Contains('"') || s.Contains('\n')
                ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }

    /// <summary>창 위치/크기 기억 — Settings 'Win.{key}'에 저장, 다음 실행 시 복원</summary>
    public static class WindowMemory
    {
        public static void Attach(Form f, LocalDatabase db, string key)
        {
            f.Load += (s, e) =>
            {
                try
                {
                    var v = db.GetSetting("Win." + key);
                    if (string.IsNullOrWhiteSpace(v)) return;
                    var p = v.Split(',');
                    if (p.Length != 4) return;
                    if (int.TryParse(p[0], out var x) && int.TryParse(p[1], out var y) &&
                        int.TryParse(p[2], out var w) && int.TryParse(p[3], out var h) && w > 200 && h > 150)
                    {
                        // 화면 밖이면 복원하지 않음
                        var screen = Screen.FromPoint(new System.Drawing.Point(x, y));
                        if (screen.WorkingArea.IntersectsWith(new System.Drawing.Rectangle(x, y, w, h)))
                        {
                            f.StartPosition = FormStartPosition.Manual;
                            f.Bounds = new System.Drawing.Rectangle(x, y, w, h);
                        }
                    }
                }
                catch { }
            };
            f.FormClosing += (s, e) =>
            {
                try
                {
                    if (f.WindowState == FormWindowState.Normal)
                        db.SetSetting("Win." + key, $"{f.Left},{f.Top},{f.Width},{f.Height}");
                }
                catch { }
            };
        }
    }
}
