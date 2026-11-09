using System;
using System.Linq;
using System.Windows.Forms;

namespace QueenPix
{
    public class SettingsDialog : Form
    {
        private UserSettings settings;
        
        private CheckBox chkShowScreenshotSaveDialog;
        private CheckBox chkMaxDurationEnabled;
        private NumericUpDown numMaxDurationValue;
        private ComboBox cmbMaxDurationUnit;
        private CheckBox chkCharlotteMode;
        private CheckBox chkNamingBlocks;
        private Label lblNamingBlocks;
        
        public UserSettings Settings { get; private set; }
        
        public SettingsDialog(UserSettings currentSettings)
        {
            settings = new UserSettings
            {
                WorkingFolder = currentSettings.WorkingFolder,
                FfmpegPath = currentSettings.FfmpegPath,
                ShowScreenshotSaveDialog = currentSettings.ShowScreenshotSaveDialog,
                MaxDurationEnabled = currentSettings.MaxDurationEnabled,
                MaxDurationValue = currentSettings.MaxDurationValue,
                MaxDurationUnit = currentSettings.MaxDurationUnit,
                CharlotteMode = currentSettings.CharlotteMode,
                CameraSettingsByDevice = currentSettings.CameraSettingsByDevice,
                NameProfiles = currentSettings.NameProfiles,
                LastUsedProfile = currentSettings.LastUsedProfile,
                NamingBlocksEnabled = currentSettings.NamingBlocksEnabled,
                NamingGrids = currentSettings.NamingGrids.Select(g => g.Copy()).ToList(),
                LastNamingGrid = currentSettings.LastNamingGrid
            };
            Settings = settings;
            
            InitializeDialog();
        }
        
        private void InitializeDialog()
        {
            this.Text = "Settings";
            this.Size = new System.Drawing.Size(500, 530);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            
            int y = 20;
            int leftMargin = 20;
            
            // Screenshot settings section
            GroupBox grpScreenshot = new GroupBox
            {
                Text = "Screenshot Settings",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(440, 60),
                Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
            };
            this.Controls.Add(grpScreenshot);
            
            chkShowScreenshotSaveDialog = new CheckBox
            {
                Text = "Show save dialog after screenshot",
                Location = new System.Drawing.Point(15, 25),
                Size = new System.Drawing.Size(410, 20),
                Checked = settings.ShowScreenshotSaveDialog
            };
            grpScreenshot.Controls.Add(chkShowScreenshotSaveDialog);
            
            y += 80;
            
            // Max duration settings section
            GroupBox grpMaxDuration = new GroupBox
            {
                Text = "Recording Max Duration",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(440, 100),
                Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
            };
            this.Controls.Add(grpMaxDuration);
            
            chkMaxDurationEnabled = new CheckBox
            {
                Text = "Enable max duration limit",
                Location = new System.Drawing.Point(15, 25),
                Size = new System.Drawing.Size(200, 20),
                Checked = settings.MaxDurationEnabled
            };
            chkMaxDurationEnabled.CheckedChanged += (s, e) =>
            {
                numMaxDurationValue.Enabled = chkMaxDurationEnabled.Checked;
                cmbMaxDurationUnit.Enabled = chkMaxDurationEnabled.Checked;
            };
            grpMaxDuration.Controls.Add(chkMaxDurationEnabled);
            
            Label lblMaxDurationValue = new Label
            {
                Text = "Duration:",
                Location = new System.Drawing.Point(15, 55),
                Size = new System.Drawing.Size(70, 20)
            };
            grpMaxDuration.Controls.Add(lblMaxDurationValue);
            
            numMaxDurationValue = new NumericUpDown
            {
                Location = new System.Drawing.Point(90, 52),
                Size = new System.Drawing.Size(70, 25),
                Minimum = 1,
                Maximum = 525600, // Will be adjusted based on unit
                Value = settings.MaxDurationValue,
                Enabled = settings.MaxDurationEnabled
            };
            grpMaxDuration.Controls.Add(numMaxDurationValue);
            
            cmbMaxDurationUnit = new ComboBox
            {
                Location = new System.Drawing.Point(170, 52),
                Size = new System.Drawing.Size(100, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Enabled = settings.MaxDurationEnabled
            };
            cmbMaxDurationUnit.Items.AddRange(new object[] { "minutes", "hours", "days" });
            int unitIndex = cmbMaxDurationUnit.Items.IndexOf(settings.MaxDurationUnit);
            cmbMaxDurationUnit.SelectedIndex = unitIndex >= 0 ? unitIndex : 0;
            
            // Update maximum based on selected unit
            void UpdateMaxDurationMaximum()
            {
                string selectedUnit = cmbMaxDurationUnit.SelectedItem?.ToString() ?? "minutes";
                int currentValue = (int)numMaxDurationValue.Value;
                
                switch (selectedUnit)
                {
                    case "minutes":
                        numMaxDurationValue.Maximum = 525600; // 365 days in minutes
                        break;
                    case "hours":
                        numMaxDurationValue.Maximum = 8760; // 365 days in hours
                        break;
                    case "days":
                        numMaxDurationValue.Maximum = 365; // 365 days
                        break;
                }
                
                // Ensure current value doesn't exceed new maximum
                if (currentValue > numMaxDurationValue.Maximum)
                {
                    numMaxDurationValue.Value = numMaxDurationValue.Maximum;
                }
                else if (currentValue < numMaxDurationValue.Minimum)
                {
                    numMaxDurationValue.Value = numMaxDurationValue.Minimum;
                }
            }
            
            // Set initial maximum based on current unit
            UpdateMaxDurationMaximum();
            
            // Update maximum when unit changes
            cmbMaxDurationUnit.SelectedIndexChanged += (s, e) => UpdateMaxDurationMaximum();
            
            grpMaxDuration.Controls.Add(cmbMaxDurationUnit);
            
            y += 120;
            
            // Charlotte mode settings section
            GroupBox grpCharlotteMode = new GroupBox
            {
                Text = "Appearance",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(440, 50),
                Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
            };
            this.Controls.Add(grpCharlotteMode);
            
            chkCharlotteMode = new CheckBox
            {
                Text = "Charlotte mode",
                Location = new System.Drawing.Point(15, 25),
                Size = new System.Drawing.Size(200, 20),
                Checked = settings.CharlotteMode
            };
            grpCharlotteMode.Controls.Add(chkCharlotteMode);
            
            y += 70;

            // Naming blocks: click a cell of a session grid in the save dialog instead of typing the name
            GroupBox grpNaming = new GroupBox
            {
                Text = "Naming blocks",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(440, 80),
                Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
            };
            this.Controls.Add(grpNaming);
            chkNamingBlocks = new CheckBox
            {
                Text = "Name recordings by clicking a cell of a naming block",
                Location = new System.Drawing.Point(15, 22),
                Size = new System.Drawing.Size(410, 20),
                Checked = settings.NamingBlocksEnabled
            };
            grpNaming.Controls.Add(chkNamingBlocks);
            Button btnEditBlocks = new Button
            {
                Text = "Edit naming blocks...",
                Location = new System.Drawing.Point(15, 46),
                Size = new System.Drawing.Size(150, 26),
                Font = new System.Drawing.Font("Arial", 9)
            };
            lblNamingBlocks = new Label
            {
                Location = new System.Drawing.Point(175, 51),
                Size = new System.Drawing.Size(255, 20),
                ForeColor = System.Drawing.Color.DimGray,
                Font = new System.Drawing.Font("Arial", 9)
            };
            void UpdateBlocksLabel() => lblNamingBlocks.Text = settings.NamingGrids.Count == 0
                ? "none yet" : string.Join(", ", settings.NamingGrids.Select(g => g.Name));
            UpdateBlocksLabel();
            btnEditBlocks.Click += (s, e) =>
            {
                using (var d = new NamingGridsDialog(settings.NamingGrids))
                {
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        settings.NamingGrids = d.Grids;
                        if (d.Grids.Count > 0) chkNamingBlocks.Checked = true;
                        UpdateBlocksLabel();
                    }
                }
            };
            grpNaming.Controls.Add(btnEditBlocks);
            grpNaming.Controls.Add(lblNamingBlocks);

            y += 90;
            
            // Buttons
            Button btnOK = new Button
            {
                Text = "OK",
                Location = new System.Drawing.Point(280, y + 10),
                Size = new System.Drawing.Size(80, 35),
                DialogResult = DialogResult.OK
            };
            btnOK.Click += BtnOK_Click;
            this.Controls.Add(btnOK);
            
            Button btnCancel = new Button
            {
                Text = "Cancel",
                Location = new System.Drawing.Point(370, y + 10),
                Size = new System.Drawing.Size(80, 35),
                DialogResult = DialogResult.Cancel
            };
            this.Controls.Add(btnCancel);
            
            this.AcceptButton = btnOK;
            this.CancelButton = btnCancel;
        }
        
        private void BtnOK_Click(object? sender, EventArgs e)
        {
            // Save settings
            settings.ShowScreenshotSaveDialog = chkShowScreenshotSaveDialog.Checked;
            settings.MaxDurationEnabled = chkMaxDurationEnabled.Checked;
            settings.MaxDurationValue = (int)numMaxDurationValue.Value;
            settings.MaxDurationUnit = cmbMaxDurationUnit.SelectedItem?.ToString() ?? "minutes";
            settings.CharlotteMode = chkCharlotteMode.Checked;
            settings.NamingBlocksEnabled = chkNamingBlocks.Checked;
            Settings = settings;
        }
    }
}

