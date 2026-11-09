using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace QueenPix
{
    /// <summary>Create and edit naming blocks (one grid of run names per session).</summary>
    public class NamingGridsDialog : Form
    {
        public List<NamingGrid> Grids { get; private set; }

        private readonly ListBox lstGrids;
        private readonly TextBox txtName, txtPrefix, txtSeparator, txtFolder, txtRows, txtColumns;
        private readonly Label lblPreview;
        private int current = -1;
        private bool loading;

        public NamingGridsDialog(List<NamingGrid> grids)
        {
            Grids = grids.Select(g => g.Copy()).ToList();

            Text = "Naming blocks";
            Size = new Size(760, 560);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;

            var help = new Label
            {
                Text = "A naming block is the grid of run names for one session. Rows x columns are the cells " +
                       "(e.g. colonies x segments). A saved recording is named Prefix_Row_Column_Run, " +
                       "e.g. S1_C1_T1_W7_1 (QueenPix adds the camera, e.g. _L_MP4).",
                Location = new Point(15, 10), Size = new Size(715, 35), ForeColor = Color.DimGray
            };
            Controls.Add(help);

            lstGrids = new ListBox { Location = new Point(15, 50), Size = new Size(180, 380), IntegralHeight = false };
            lstGrids.SelectedIndexChanged += (s, e) =>
            {
                // only a real click on another block switches the editor; renaming the list entry
                // while typing must not reload the boxes (that reset the cursor after every key)
                if (loading || lstGrids.SelectedIndex == current) return;
                Store();
                ShowGrid(lstGrids.SelectedIndex);
            };
            Controls.Add(lstGrids);

            var btnNew = new Button { Text = "New", Location = new Point(15, 435), Size = new Size(55, 28) };
            var btnDup = new Button { Text = "Copy", Location = new Point(77, 435), Size = new Size(55, 28) };
            var btnDel = new Button { Text = "Delete", Location = new Point(139, 435), Size = new Size(56, 28) };
            btnNew.Click += (s, e) => Add(new NamingGrid { Name = $"Session {Grids.Count + 1}", Prefix = $"S{Grids.Count + 1}" });
            btnDup.Click += (s, e) =>
            {
                if (current < 0) return;
                Store();
                var g = Grids[current].Copy();
                g.Name = Grids[current].Name + " (copy)";
                Add(g);
            };
            btnDel.Click += (s, e) =>
            {
                if (current < 0) return;
                if (MessageBox.Show(this, $"Delete \"{Grids[current].Name}\"?", "Naming blocks", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                Grids.RemoveAt(current); current = -1; Fill(Math.Min(lstGrids.SelectedIndex, Grids.Count - 1));
            };
            Controls.AddRange(new Control[] { btnNew, btnDup, btnDel });

            int x = 215, y = 50;
            Label L(string t, int yy) { var l = new Label { Text = t, Location = new Point(x, yy + 3), Size = new Size(95, 20) }; Controls.Add(l); return l; }
            L("Name", y); txtName = new TextBox { Location = new Point(x + 100, y), Size = new Size(200, 25) }; Controls.Add(txtName);
            L("Prefix", y + 32); txtPrefix = new TextBox { Location = new Point(x + 100, y + 32), Size = new Size(100, 25) }; Controls.Add(txtPrefix);
            var lblSep = new Label { Text = "Separator", Location = new Point(x + 215, y + 35), Size = new Size(65, 20) }; Controls.Add(lblSep);
            txtSeparator = new TextBox { Location = new Point(x + 285, y + 32), Size = new Size(30, 25) }; Controls.Add(txtSeparator);
            L("Save folder", y + 64);
            txtFolder = new TextBox { Location = new Point(x + 100, y + 64), Size = new Size(370, 25) }; Controls.Add(txtFolder);
            var btnBrowse = new Button { Text = "📁", Location = new Point(x + 475, y + 62), Size = new Size(35, 28) };
            btnBrowse.Click += (s, e) =>
            {
                using var d = new FolderBrowserDialog { Description = "Folder the videos of this session are saved in", ShowNewFolderButton = true, SelectedPath = txtFolder.Text };
                if (d.ShowDialog(this) == DialogResult.OK) txtFolder.Text = d.SelectedPath;
            };
            Controls.Add(btnBrowse);
            var lblFolderHint = new Label { Text = "(empty = the folder chosen in the save dialog)", Location = new Point(x + 100, y + 92), Size = new Size(400, 18), ForeColor = Color.DimGray };
            Controls.Add(lblFolderHint);

            var lblRows = new Label { Text = "Rows (one per line), e.g. colonies:", Location = new Point(x, y + 118), Size = new Size(240, 20) }; Controls.Add(lblRows);
            txtRows = new TextBox { Location = new Point(x, y + 140), Size = new Size(240, 210), Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
            Controls.Add(txtRows);
            var lblCols = new Label { Text = "Columns (one per line), e.g. segments:", Location = new Point(x + 270, y + 118), Size = new Size(250, 20) }; Controls.Add(lblCols);
            txtColumns = new TextBox { Location = new Point(x + 270, y + 140), Size = new Size(240, 210), Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
            Controls.Add(txtColumns);

            lblPreview = new Label { Location = new Point(x, y + 358), Size = new Size(515, 40), ForeColor = Color.DarkGreen };
            Controls.Add(lblPreview);
            foreach (var tb in new[] { txtName, txtPrefix, txtSeparator, txtFolder, txtRows, txtColumns })
                tb.TextChanged += (s, e) => { if (!loading) { Store(renameInList: false); UpdatePreview(); } };
            txtName.Leave += (s, e) => Store();          // the list on the left shows the new name once you leave the box

            var btnOK = new Button { Text = "OK", Location = new Point(545, 475), Size = new Size(85, 32), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "Cancel", Location = new Point(640, 475), Size = new Size(85, 32), DialogResult = DialogResult.Cancel };
            btnOK.Click += (s, e) =>
            {
                Store();
                for (int i = 0; i < Grids.Count; i++)
                {
                    string? p = Grids[i].Problem();
                    if (p != null)
                    {
                        MessageBox.Show(this, $"{Grids[i].Name}: {p}.", "Naming blocks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Fill(i); DialogResult = DialogResult.None; return;
                    }
                }
                var dupe = Grids.GroupBy(g => g.Name.Trim()).FirstOrDefault(gr => gr.Count() > 1);
                if (dupe != null)
                {
                    MessageBox.Show(this, $"Two naming blocks are called \"{dupe.Key}\".", "Naming blocks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                }
            };
            Controls.AddRange(new Control[] { btnOK, btnCancel });
            CancelButton = btnCancel;

            Fill(Grids.Count > 0 ? 0 : -1);
        }

        private void Add(NamingGrid g)
        {
            Store();
            Grids.Add(g);
            Fill(Grids.Count - 1);
            txtName.Focus(); txtName.SelectAll();
        }

        private void Fill(int select)
        {
            loading = true;
            lstGrids.Items.Clear();
            foreach (var g in Grids) lstGrids.Items.Add(g.Name);
            loading = false;
            current = -1;
            if (select >= 0 && select < Grids.Count) lstGrids.SelectedIndex = select;
            else ShowGrid(-1);
        }

        private void ShowGrid(int i)
        {
            loading = true;
            current = i;
            bool on = i >= 0 && i < Grids.Count;
            foreach (var tb in new[] { txtName, txtPrefix, txtSeparator, txtFolder, txtRows, txtColumns }) tb.Enabled = on;
            var g = on ? Grids[i] : new NamingGrid();
            txtName.Text = g.Name; txtPrefix.Text = g.Prefix; txtSeparator.Text = on ? g.Separator : "";
            txtFolder.Text = g.SaveFolder;
            txtRows.Text = string.Join(Environment.NewLine, g.Rows);
            txtColumns.Text = string.Join(Environment.NewLine, g.Columns);
            loading = false;
            UpdatePreview();
        }

        /// <summary>editor -> the current grid</summary>
        private void Store(bool renameInList = true)
        {
            if (loading || current < 0 || current >= Grids.Count) return;
            var g = Grids[current];
            g.Name = txtName.Text.Trim(); g.Prefix = txtPrefix.Text.Trim();
            g.Separator = string.IsNullOrEmpty(txtSeparator.Text) ? "_" : txtSeparator.Text;
            g.SaveFolder = txtFolder.Text.Trim();
            g.Rows = NamingGrid.ParseList(txtRows.Text);
            g.Columns = NamingGrid.ParseList(txtColumns.Text);
            if (renameInList && lstGrids.Items.Count > current && (string)lstGrids.Items[current] != g.Name)
            {
                loading = true;
                lstGrids.Items[current] = g.Name;
                if (lstGrids.SelectedIndex != current) lstGrids.SelectedIndex = current;
                loading = false;
            }
        }

        private void UpdatePreview()
        {
            if (current < 0 || current >= Grids.Count) { lblPreview.Text = "Click New to make a naming block."; return; }
            var g = Grids[current];
            string? p = g.Problem();
            lblPreview.ForeColor = p == null ? Color.DarkGreen : Color.Firebrick;
            lblPreview.Text = p ?? $"{g.Rows.Count} x {g.Columns.Count} = {g.Rows.Count * g.Columns.Count} cells. " +
                                   $"First run: {g.RunName(g.Rows[0], g.Columns[0], 1)}";
        }
    }
}
