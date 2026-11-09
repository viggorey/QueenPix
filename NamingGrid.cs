using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Windows.Forms;

namespace QueenPix
{
    /// <summary>
    /// A naming block: a grid of run names for one session. Rows x columns give the cells
    /// (e.g. colonies x segments); a cell's name is Prefix_Row_Column, and a saved recording is
    /// Prefix_Row_Column_Run (+ the camera suffix QueenPix adds), e.g. S1_C1_T1_W7_1_L_MP4.mp4.
    /// </summary>
    public class NamingGrid
    {
        public string Name { get; set; } = "";
        public string Prefix { get; set; } = "";
        public List<string> Rows { get; set; } = new List<string>();
        public List<string> Columns { get; set; } = new List<string>();
        public string Separator { get; set; } = "_";
        public string SaveFolder { get; set; } = "";

        public NamingGrid Copy() => new NamingGrid
        {
            Name = Name, Prefix = Prefix, Rows = new List<string>(Rows), Columns = new List<string>(Columns),
            Separator = Separator, SaveFolder = SaveFolder
        };

        private string Sep => string.IsNullOrEmpty(Separator) ? "_" : Separator;

        /// <summary>Prefix_Row_Column (empty parts are left out)</summary>
        public string CellName(string row, string column) =>
            string.Join(Sep, new[] { Prefix, row, column }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

        public string RunName(string row, string column, int run) => CellName(row, column) + Sep + run;

        /// <summary>run numbers already saved for a cell in a folder (files named cell_run_camera_...)</summary>
        public List<int> SavedRuns(string folder, string row, string column)
        {
            var runs = new List<int>();
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return runs;
            var rx = new Regex("^" + Regex.Escape(CellName(row, column) + Sep) + @"(\d+)(?:[_.]|$)", RegexOptions.IgnoreCase);
            try
            {
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    var m = rx.Match(Path.GetFileName(f));
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && !runs.Contains(n)) runs.Add(n);
                }
            }
            catch { }
            runs.Sort();
            return runs;
        }

        /// <summary>the next free run number of a cell (1 if none saved yet)</summary>
        public int NextRun(string folder, string row, string column)
        {
            var r = SavedRuns(folder, row, column);
            return r.Count == 0 ? 1 : r.Max() + 1;
        }

        /// <summary>split a list typed by the user: one per line, or separated by commas, semicolons or tabs</summary>
        public static List<string> ParseList(string text) =>
            (text ?? "").Split(new[] { '\r', '\n', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        /// <summary>characters that are not allowed in file names, in any part of the grid</summary>
        public string? Problem()
        {
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (var part in new[] { Prefix, Separator }.Concat(Rows).Concat(Columns))
                if ((part ?? "").IndexOfAny(bad) >= 0) return $"\"{part}\" contains characters that are not allowed in file names";
            if (Rows.Count == 0 || Columns.Count == 0) return "add at least one row and one column";
            if (Rows.Distinct().Count() != Rows.Count) return "two rows have the same name";
            if (Columns.Distinct().Count() != Columns.Count) return "two columns have the same name";
            return null;
        }
    }

    /// <summary>Screenshots while naming blocks are on start with the session prefix (S1_2026-..._L.png),
    /// so plate images are linked to their session.</summary>
    public static class NamingBlockPrompt
    {
        /// <summary>"" = no prefix (naming blocks off), "S1_" etc., or null = cancelled (take no screenshot).
        /// With several blocks it asks which session (the last used one preselected: Enter is enough).</summary>
        public static string? ScreenshotPrefix(IWin32Window owner, UserSettings s)
        {
            if (s == null || !s.NamingBlocksEnabled || s.NamingGrids.Count == 0) return "";
            NamingGrid g;
            if (s.NamingGrids.Count == 1) g = s.NamingGrids[0];
            else
            {
                using (var f = new Form
                {
                    Text = "Screenshots",
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterParent,
                    ClientSize = new Size(340, 110),
                    MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false
                })
                {
                    f.Controls.Add(new Label { Text = "Name the screenshots for session:", Location = new Point(12, 12), Size = new Size(310, 20) });
                    var cmb = new ComboBox { Location = new Point(12, 36), Size = new Size(316, 25), DropDownStyle = ComboBoxStyle.DropDownList };
                    foreach (var x in s.NamingGrids) cmb.Items.Add($"{x.Name}   (prefix {x.Prefix})");
                    int last = s.NamingGrids.FindIndex(x => x.Name == s.LastNamingGrid);
                    cmb.SelectedIndex = last >= 0 ? last : 0;
                    f.Controls.Add(cmb);
                    var ok = new Button { Text = "OK", Location = new Point(162, 72), Size = new Size(80, 28), DialogResult = DialogResult.OK };
                    var cancel = new Button { Text = "Cancel", Location = new Point(248, 72), Size = new Size(80, 28), DialogResult = DialogResult.Cancel };
                    f.Controls.Add(ok); f.Controls.Add(cancel);
                    f.AcceptButton = ok; f.CancelButton = cancel;
                    if (f.ShowDialog(owner) != DialogResult.OK) return null;
                    g = s.NamingGrids[cmb.SelectedIndex];
                }
                if (s.LastNamingGrid != g.Name) { s.LastNamingGrid = g.Name; s.Save(); }   // the save dialog then opens on it too
            }
            if (string.IsNullOrWhiteSpace(g.Prefix)) return "";
            return g.Prefix.Trim() + (string.IsNullOrEmpty(g.Separator) ? "_" : g.Separator);
        }
    }
}
