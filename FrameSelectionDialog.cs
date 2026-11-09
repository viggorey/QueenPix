using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace QueenPix
{
    public class FrameSelectionDialog : Form
    {
        // Return values
        public string SelectedPath { get; private set; } = "";
        public string SelectedFilename { get; private set; } = "";
        public double SelectedFps { get; private set; } = 30.0;
        public OutputFormat SelectedFormat { get; private set; } = OutputFormat.MP4;
        public Dictionary<string, (int startFrame, int endFrame)> FrameRanges { get; private set; }
        
        private List<(string filename, int frames, double duration)> frameStats;
        private List<long> aviFileSizes;
        private List<string> aviFilePaths;
        
        private TextBox txtFps;
        private RadioButton rbMP4, rbAVI, rbBoth;
        private Label lblEstimatedSize;
        private TextBox txtLocation;
        private TextBox txtFilename;
        private Label lblFrameInfo;

        // naming block (Settings -> Naming blocks): click a cell, type the run number
        private readonly UserSettings? settings;
        private string defaultLocation = "";
        private ComboBox? cmbGrid;
        private Panel? gridPanel;
        private TextBox? txtRun;
        private Label? lblGridInfo;
        private NamingGrid? activeGrid;
        private (string row, string col)? selectedCell;
        private readonly Dictionary<(string row, string col), Button> cellButtons = new Dictionary<(string row, string col), Button>();
        private readonly ToolTip cellTips = new ToolTip();
        
        public enum OutputFormat
        {
            MP4,
            AVI,
            Both
        }
        
        public FrameSelectionDialog(List<(string filename, int frames, double duration)> stats, 
                                    List<long> fileSizes, 
                                    List<string> filePaths,
                                    string defaultPath, 
                                    string defaultFilename,
                                    UserSettings? settings = null)
        {
            this.settings = settings;
            defaultLocation = defaultPath;
            frameStats = stats;
            aviFileSizes = fileSizes;
            aviFilePaths = filePaths;
            FrameRanges = new Dictionary<string, (int, int)>();
            
            // Initialize frame ranges (all frames by default)
            foreach (var stat in frameStats)
            {
                FrameRanges[stat.filename] = (1, stat.frames);
            }
            
            // Default output fps = MEDIAN of the cameras' measured rates. Triggered cameras all run at
            // the box rate, so one camera that is off (not triggered, double-triggering) cannot drag
            // the value away as it did with the average.
            var rates = stats.Where(s => s.duration > 0 && s.frames > 0).Select(s => s.frames / s.duration).OrderBy(r => r).ToList();
            double avgFps = rates.Count == 0 ? 0
                : rates.Count % 2 == 1 ? rates[rates.Count / 2]
                : (rates[rates.Count / 2 - 1] + rates[rates.Count / 2]) / 2;
            medianFps = avgFps;
            
            InitializeDialog(defaultPath, defaultFilename, avgFps);
        }
        
        private void InitializeDialog(string defaultPath, string defaultFilename, double avgFps)
        {
            this.Text = "Save Recording";
            this.Size = new System.Drawing.Size(625, 530);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            
            int y = 20;
            int leftMargin = 20;
            
            // Frame Statistics header
            Label lblStats = new Label
            {
                Text = "Frame Statistics:",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(500, 20),
                Font = new System.Drawing.Font("Arial", 10, System.Drawing.FontStyle.Bold)
            };
            this.Controls.Add(lblStats);
            y += 25;
            
            // Frame statistics list with trim info - in scrollable panel
            Panel frameInfoPanel = new Panel
            {
                Location = new System.Drawing.Point(leftMargin + 10, y),
                Size = new System.Drawing.Size(560, 100),
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            this.Controls.Add(frameInfoPanel);

            lblFrameInfo = new Label
            {
                Location = new System.Drawing.Point(5, 5),
                AutoSize = true,  // Let it grow based on content
                MaximumSize = new System.Drawing.Size(530, 0),  // Limit width, unlimited height
                Font = new System.Drawing.Font("Arial", 9)
            };
            frameInfoPanel.Controls.Add(lblFrameInfo);
            UpdateFrameInfoDisplay();
            y += 110;
            
            // Trim Videos button
            Button btnTrimVideos = new Button
            {
                Text = "Trim Videos...",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(120, 30),
                Font = new System.Drawing.Font("Arial", 9)
            };
            btnTrimVideos.Click += BtnTrimVideos_Click;
            this.Controls.Add(btnTrimVideos);
            y += 45;
            
            // FPS input
            Label lblFps = new Label
            {
                Text = "Output Frame Rate (fps):",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(180, 20)
            };
            this.Controls.Add(lblFps);
            
            txtFps = new TextBox
            {
                Text = avgFps.ToString("F2"),
                Location = new System.Drawing.Point(leftMargin + 185, y - 2),
                Size = new System.Drawing.Size(100, 25)
            };
            this.Controls.Add(txtFps);
            y += 35;
            
            // Output format
            Label lblFormat = new Label
            {
                Text = "Output Format:",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(180, 20)
            };
            this.Controls.Add(lblFormat);
            
            rbMP4 = new RadioButton
            {
                Text = "MP4 (compressed)",
                Location = new System.Drawing.Point(leftMargin + 185, y),
                Size = new System.Drawing.Size(150, 20),
                Checked = true
            };
            rbMP4.CheckedChanged += UpdateEstimatedSize;
            this.Controls.Add(rbMP4);
            
            rbAVI = new RadioButton
            {
                Text = "AVI (uncompressed)",
                Location = new System.Drawing.Point(leftMargin + 345, y),
                Size = new System.Drawing.Size(150, 20)
            };
            rbAVI.CheckedChanged += UpdateEstimatedSize;
            this.Controls.Add(rbAVI);
            
            rbBoth = new RadioButton
            {
                Text = "Both",
                Location = new System.Drawing.Point(leftMargin + 345, y + 25),
                Size = new System.Drawing.Size(80, 20)
            };
            rbBoth.CheckedChanged += UpdateEstimatedSize;
            this.Controls.Add(rbBoth);
            y += 55;
            
            // Estimated size
            lblEstimatedSize = new Label
            {
                Text = "Calculating...",
                Location = new System.Drawing.Point(leftMargin + 185, y),
                Size = new System.Drawing.Size(400, 20),
                ForeColor = System.Drawing.Color.Gray,
                Font = new System.Drawing.Font("Arial", 9)
            };
            this.Controls.Add(lblEstimatedSize);
            y += 35;

            bool useBlocks = settings != null && settings.NamingBlocksEnabled && settings.NamingGrids.Count > 0;
            if (useBlocks) y = AddNamingBlock(leftMargin, y);
            
            // Save location
            Label lblLocation = new Label
            {
                Text = "Save Location:",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(100, 20)
            };
            this.Controls.Add(lblLocation);
            
            txtLocation = new TextBox
            {
                Text = defaultPath,
                Location = new System.Drawing.Point(leftMargin + 105, y - 2),
                Size = new System.Drawing.Size(410, 25),
                ReadOnly = true
            };
            this.Controls.Add(txtLocation);
            
            Button btnBrowse = new Button
            {
                Text = "📁",
                Location = new System.Drawing.Point(leftMargin + 520, y - 4),
                Size = new System.Drawing.Size(35, 28)
            };
            btnBrowse.Click += BtnBrowse_Click;
            this.Controls.Add(btnBrowse);
            y += 35;
            
            // Filename
            Label lblFilename = new Label
            {
                Text = "Filename:",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(100, 20)
            };
            this.Controls.Add(lblFilename);
            
            txtFilename = new TextBox
            {
                Text = defaultFilename,
                Location = new System.Drawing.Point(leftMargin + 105, y - 2),
                Size = new System.Drawing.Size(450, 25)
            };
            this.Controls.Add(txtFilename);
            y += 50;
            
            // Buttons
            Button btnCancel = new Button
            {
                Text = "Cancel",
                Location = new System.Drawing.Point(leftMargin + 345, y),
                Size = new System.Drawing.Size(100, 35),
                DialogResult = DialogResult.Cancel
            };
            this.Controls.Add(btnCancel);
            
            Button btnSave = new Button
            {
                Text = "Save & Convert",
                Location = new System.Drawing.Point(leftMargin + 455, y),
                Size = new System.Drawing.Size(120, 35),
                DialogResult = DialogResult.OK
            };
            btnSave.Click += BtnSave_Click;
            this.Controls.Add(btnSave);
            
            this.AcceptButton = btnSave;
            this.CancelButton = btnCancel;

            if (useBlocks)
            {
                this.ClientSize = new System.Drawing.Size(this.ClientSize.Width, y + 50);
                txtFilename.Text = "";                     // click a cell (or type a name)
                SelectGrid(cmbGrid!.SelectedIndex);
            }
            
            // Initial size calculation
            UpdateEstimatedSize(null, EventArgs.Empty);
        }
        
        private double medianFps;

        private void UpdateFrameInfoDisplay()
        {
            string info = "";
            bool mismatch = false;
            foreach (var stat in frameStats)
            {
                var range = FrameRanges[stat.filename];
                int selectedFrames = range.endFrame - range.startFrame + 1;
                bool isTrimmed = range.startFrame != 1 || range.endFrame != stat.frames;
                
                if (isTrimmed)
                {
                    info += $"• {stat.filename}: {stat.frames} frames → {selectedFrames} frames (trimmed)\n";
                }
                else
                {
                    double r = stat.duration > 0 ? stat.frames / stat.duration : 0;
                    bool off = medianFps > 0 && Math.Abs(r - medianFps) > 0.03 * medianFps;
                    if (off) mismatch = true;
                    info += $"• {stat.filename}: {stat.frames} frames in {stat.duration:F2}s = {r:F2} fps{(off ? "  ⚠" : "")}\n";
                }
            }
            if (mismatch)
                info += "⚠ Cameras recorded at different rates - check the marked camera's trigger (cable / ARMED in the log).\n";
            lblFrameInfo.Text = info.TrimEnd('\n');
        }
        
        private void BtnTrimVideos_Click(object? sender, EventArgs e)
        {
            using (VideoTrimDialog dialog = new VideoTrimDialog(frameStats, aviFilePaths, FrameRanges))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    // Update frame ranges from trim dialog
                    FrameRanges = dialog.FrameRanges;
                    
                    // Update display
                    UpdateFrameInfoDisplay();
                    UpdateEstimatedSize(null, EventArgs.Empty);
                }
            }
        }
        
        private void UpdateEstimatedSize(object? sender, EventArgs e)
        {
            try
            {
                lblEstimatedSize.Text = "Calculating...";
                Application.DoEvents();
                
                System.Threading.Tasks.Task.Run(() =>
                {
                    long totalSize = 0;
                    
                    for (int i = 0; i < aviFileSizes.Count; i++)
                    {
                        var range = FrameRanges[frameStats[i].filename];
                        int selectedFrames = range.endFrame - range.startFrame + 1;
                        int totalFrames = frameStats[i].frames;
                        
                        if (totalFrames > 0)
                        {
                            long fullSize = aviFileSizes[i];
                            long trimmedSize = (fullSize * selectedFrames) / totalFrames;
                            totalSize += trimmedSize;
                        }
                    }
                    
                    long estimatedMp4Size = totalSize / 8;
                    
                    string sizeText = "";
                    if (rbMP4.Checked)
                    {
                        sizeText = $"Estimated size: ~{estimatedMp4Size / (1024 * 1024)} MB total";
                    }
                    else if (rbAVI.Checked)
                    {
                        sizeText = $"Estimated size: ~{totalSize / (1024 * 1024)} MB total";
                    }
                    else if (rbBoth.Checked)
                    {
                        long bothSize = totalSize + estimatedMp4Size;
                        sizeText = $"Estimated size: ~{bothSize / (1024 * 1024)} MB total";
                    }
                    
                    if (lblEstimatedSize.InvokeRequired)
                    {
                        lblEstimatedSize.Invoke(new Action(() => lblEstimatedSize.Text = sizeText));
                    }
                    else
                    {
                        lblEstimatedSize.Text = sizeText;
                    }
                });
            }
            catch
            {
                lblEstimatedSize.Text = "Estimated size: N/A";
            }
        }
        
        private void BtnBrowse_Click(object? sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select Save Location";
                dialog.SelectedPath = txtLocation.Text;
                dialog.ShowNewFolderButton = true;
                
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    txtLocation.Text = dialog.SelectedPath;
                    RefreshCells();
                }
            }
        }
        
        private void BtnSave_Click(object? sender, EventArgs e)
        {
            // Validate FPS
            if (!double.TryParse(txtFps.Text, out double fps) || fps < 0.1 || fps > 1000)
            {
                MessageBox.Show("Please enter a valid frame rate between 0.1 and 1000 fps.",
                                "Invalid FPS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.None;
                return;
            }

            // Naming block: the run number must be a whole number >= 1
            if (selectedCell != null && activeGrid != null)
            {
                if (txtRun == null || !int.TryParse(txtRun.Text, out int run) || run < 1)
                {
                    MessageBox.Show("Please type the run number (1, 2, ...).", "Run number",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    this.DialogResult = DialogResult.None;
                    txtRun?.Focus();
                    return;
                }
            }

            // Validate filename
            if (string.IsNullOrWhiteSpace(txtFilename.Text))
            {
                MessageBox.Show(cmbGrid != null ? "Click a cell of the naming block (or type a filename)." : "Please enter a filename.",
                                "Missing Filename", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.None;
                return;
            }
            
            // Check for invalid characters
            char[] invalidChars = Path.GetInvalidFileNameChars();
            if (txtFilename.Text.IndexOfAny(invalidChars) >= 0)
            {
                MessageBox.Show("Filename contains invalid characters.",
                                "Invalid Filename", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.None;
                return;
            }

            // Check for existing files with the same base name
            string checkName = txtFilename.Text;
            string checkPath = txtLocation.Text;
            bool hasConflict = NameTaken(checkPath, checkName);

            if (hasConflict)
            {
                using (Form warningDialog = new Form())
                {
                    warningDialog.Text = "File Already Exists";
                    warningDialog.Size = new System.Drawing.Size(440, 160);
                    warningDialog.StartPosition = FormStartPosition.CenterParent;
                    warningDialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                    warningDialog.MaximizeBox = false;
                    warningDialog.MinimizeBox = false;

                    Label msg = new Label
                    {
                        Text = $"A file named \"{checkName}\" already exists in the selected folder.\nWhat would you like to do?",
                        Location = new System.Drawing.Point(15, 15),
                        Size = new System.Drawing.Size(400, 45)
                    };
                    warningDialog.Controls.Add(msg);

                    Button btnOverwrite = new Button { Text = "Overwrite", Location = new System.Drawing.Point(15, 75), Size = new System.Drawing.Size(110, 32) };
                    Button btnRename   = new Button { Text = "Auto-rename",  Location = new System.Drawing.Point(140, 75), Size = new System.Drawing.Size(110, 32) };
                    Button btnCancelW  = new Button { Text = "Cancel",    Location = new System.Drawing.Point(265, 75), Size = new System.Drawing.Size(110, 32) };

                    string choice = "cancel";
                    btnOverwrite.Click += (s, _) => { choice = "overwrite"; warningDialog.Close(); };
                    btnRename.Click   += (s, _) => { choice = "rename";    warningDialog.Close(); };
                    btnCancelW.Click  += (s, _) => { choice = "cancel";    warningDialog.Close(); };

                    warningDialog.Controls.AddRange(new Control[] { btnOverwrite, btnRename, btnCancelW });
                    warningDialog.ShowDialog(this);

                    if (choice == "cancel")
                    {
                        this.DialogResult = DialogResult.None;
                        return;
                    }

                    if (choice == "rename" && selectedCell != null && activeGrid != null && txtRun != null)
                    {
                        // naming block: the next free run number instead of "(Copy n)"
                        var (r, c) = selectedCell.Value;
                        int next = activeGrid.NextRun(checkPath, r, c);
                        txtRun.Text = next.ToString();
                        checkName = activeGrid.RunName(r, c, next);
                        txtFilename.Text = checkName;
                    }
                    else if (choice == "rename")
                    {
                        int copy = 1;
                        string newName;
                        do
                        {
                            newName = $"{checkName} (Copy {copy++})";
                        } while (NameTaken(checkPath, newName));

                        txtFilename.Text = newName;
                        checkName = newName;
                    }
                }
            }

            try { if (!string.IsNullOrWhiteSpace(checkPath)) Directory.CreateDirectory(checkPath); } catch { }
            if (settings != null && activeGrid != null)
            {
                settings.LastNamingGrid = activeGrid.Name;
                settings.Save();
            }

            SelectedPath = checkPath;
            SelectedFilename = checkName;
            SelectedFps = fps;
            
            if (rbMP4.Checked)
                SelectedFormat = OutputFormat.MP4;
            else if (rbAVI.Checked)
                SelectedFormat = OutputFormat.AVI;
            else
                SelectedFormat = OutputFormat.Both;
        }

        /// <summary>is a recording with this base name already saved in the folder? (name_camera_..., name.ext)
        /// S1_C1_T1_W7_1 does not count S1_C1_T1_W7_12 as taken</summary>
        private static bool NameTaken(string folder, string name)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return false;
            try
            {
                return Directory.EnumerateFiles(folder, name + "*").Any(f =>
                {
                    string fn = Path.GetFileName(f);
                    if (fn.Length == name.Length) return true;
                    char next = fn[name.Length];
                    return next == '_' || next == '.' || next == ' ' || next == '-';
                });
            }
            catch { return false; }
        }

        // ---------------- naming block ----------------
        private const int CellW = 74, CellH = 26, HeadW = 72, HeadH = 20;

        /// <summary>the naming block controls at y; returns the y below them</summary>
        private int AddNamingBlock(int leftMargin, int y)
        {
            var s = settings!;
            int maxCols = s.NamingGrids.Max(g => Math.Max(1, g.Columns.Count));
            int maxRows = s.NamingGrids.Max(g => Math.Max(1, g.Rows.Count));
            int width = Math.Max(this.ClientSize.Width, leftMargin * 2 + 30 + HeadW + maxCols * CellW);
            this.ClientSize = new System.Drawing.Size(width, this.ClientSize.Height);
            int gridH = HeadH + Math.Min(maxRows, 8) * CellH + 22;     // scrolls beyond 8 rows

            GroupBox grp = new GroupBox
            {
                Text = "Naming block",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(width - 2 * leftMargin, gridH + 90),
                Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
            };
            this.Controls.Add(grp);
            var normal = new System.Drawing.Font("Arial", 9);

            grp.Controls.Add(new Label { Text = "Session:", Location = new System.Drawing.Point(12, 25), Size = new System.Drawing.Size(60, 20), Font = normal });
            cmbGrid = new ComboBox
            {
                Location = new System.Drawing.Point(75, 22),
                Size = new System.Drawing.Size(180, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = normal
            };
            foreach (var g in s.NamingGrids) cmbGrid.Items.Add(g.Name);
            int last = s.NamingGrids.FindIndex(g => g.Name == s.LastNamingGrid);
            cmbGrid.SelectedIndex = last >= 0 ? last : 0;
            cmbGrid.SelectedIndexChanged += (o, e) => SelectGrid(cmbGrid.SelectedIndex);
            grp.Controls.Add(cmbGrid);

            lblGridInfo = new Label
            {
                Location = new System.Drawing.Point(265, 25),
                Size = new System.Drawing.Size(grp.Width - 275, 20),
                ForeColor = System.Drawing.Color.DimGray,
                Font = normal
            };
            grp.Controls.Add(lblGridInfo);

            gridPanel = new Panel
            {
                Location = new System.Drawing.Point(10, 52),
                Size = new System.Drawing.Size(grp.Width - 20, gridH),
                AutoScroll = true
            };
            grp.Controls.Add(gridPanel);

            int yr = 52 + gridH + 6;
            grp.Controls.Add(new Label { Text = "Run:", Location = new System.Drawing.Point(12, yr + 3), Size = new System.Drawing.Size(40, 20), Font = normal });
            txtRun = new TextBox { Location = new System.Drawing.Point(55, yr), Size = new System.Drawing.Size(50, 25), Font = normal };
            txtRun.TextChanged += (o, e) => UpdateNameFromCell();
            grp.Controls.Add(txtRun);
            grp.Controls.Add(new Label
            {
                Text = "click the cell of this segment, type the run number, Enter = save   (✓ = runs already saved)",
                Location = new System.Drawing.Point(115, yr + 3),
                Size = new System.Drawing.Size(grp.Width - 125, 20),
                ForeColor = System.Drawing.Color.DimGray,
                Font = normal
            });
            return y + grp.Height + 10;
        }

        private void SelectGrid(int index)
        {
            if (settings == null || index < 0 || index >= settings.NamingGrids.Count) return;
            activeGrid = settings.NamingGrids[index];
            selectedCell = null;
            // each session saves to its own folder (else the normal working folder); a name from another
            // session must never be left in the box
            txtLocation.Text = string.IsNullOrWhiteSpace(activeGrid.SaveFolder) ? defaultLocation : activeGrid.SaveFolder;
            txtFilename.Text = "";
            if (txtRun != null) txtRun.Text = "";
            BuildGrid();
        }

        private void BuildGrid()
        {
            if (gridPanel == null || activeGrid == null) return;
            gridPanel.SuspendLayout();
            gridPanel.Controls.Clear();
            cellButtons.Clear();
            var normal = new System.Drawing.Font("Arial", 9);
            for (int c = 0; c < activeGrid.Columns.Count; c++)
                gridPanel.Controls.Add(new Label
                {
                    Text = activeGrid.Columns[c],
                    Location = new System.Drawing.Point(HeadW + c * CellW, 0),
                    Size = new System.Drawing.Size(CellW - 4, HeadH),
                    TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                    Font = new System.Drawing.Font("Arial", 8.5f, System.Drawing.FontStyle.Bold)
                });
            for (int r = 0; r < activeGrid.Rows.Count; r++)
            {
                gridPanel.Controls.Add(new Label
                {
                    Text = activeGrid.Rows[r],
                    Location = new System.Drawing.Point(0, HeadH + r * CellH + 4),
                    Size = new System.Drawing.Size(HeadW - 6, CellH - 6),
                    TextAlign = System.Drawing.ContentAlignment.MiddleRight,
                    Font = new System.Drawing.Font("Arial", 8.5f, System.Drawing.FontStyle.Bold)
                });
                for (int c = 0; c < activeGrid.Columns.Count; c++)
                {
                    var key = (activeGrid.Rows[r], activeGrid.Columns[c]);
                    var b = new Button
                    {
                        Location = new System.Drawing.Point(HeadW + c * CellW, HeadH + r * CellH),
                        Size = new System.Drawing.Size(CellW - 4, CellH - 3),
                        Font = normal,
                        Tag = key,
                        TabStop = false
                    };
                    b.Click += Cell_Click;
                    cellButtons[key] = b;
                    gridPanel.Controls.Add(b);
                }
            }
            gridPanel.ResumeLayout();
            RefreshCells();
        }

        /// <summary>cell colours: green = runs already saved in the save folder, blue = selected</summary>
        private void RefreshCells()
        {
            if (activeGrid == null) return;
            string folder = txtLocation.Text;
            int done = 0;
            foreach (var kv in cellButtons)
            {
                var runs = activeGrid.SavedRuns(folder, kv.Key.row, kv.Key.col);
                if (runs.Count > 0) done++;
                var b = kv.Value;
                bool sel = selectedCell.HasValue && selectedCell.Value == kv.Key;
                b.Text = runs.Count == 0 ? "" : "✓ " + string.Join(",", runs);
                b.UseVisualStyleBackColor = !sel && runs.Count == 0;
                if (sel) b.BackColor = System.Drawing.Color.LightSkyBlue;
                else if (runs.Count > 0) b.BackColor = System.Drawing.Color.PaleGreen;
                cellTips.SetToolTip(b, activeGrid.CellName(kv.Key.row, kv.Key.col) +
                                       (runs.Count > 0 ? $"   saved runs: {string.Join(", ", runs)}" : "   not saved yet"));
            }
            if (lblGridInfo != null)
                lblGridInfo.Text = $"{done} of {cellButtons.Count} cells saved" +
                                   (string.IsNullOrWhiteSpace(activeGrid.SaveFolder) ? "" : $"  ·  folder {activeGrid.SaveFolder}");
        }

        private void Cell_Click(object? sender, EventArgs e)
        {
            if (sender is not Button b || activeGrid == null || txtRun == null) return;
            var key = ((string row, string col))b.Tag!;
            selectedCell = key;
            txtRun.Text = activeGrid.NextRun(txtLocation.Text, key.row, key.col).ToString();
            UpdateNameFromCell();
            RefreshCells();
            txtRun.Focus();
            txtRun.SelectAll();
        }

        private void UpdateNameFromCell()
        {
            if (activeGrid == null || selectedCell == null || txtRun == null) return;
            if (int.TryParse(txtRun.Text, out int n) && n > 0)
                txtFilename.Text = activeGrid.RunName(selectedCell.Value.row, selectedCell.Value.col, n);
        }
    }
}