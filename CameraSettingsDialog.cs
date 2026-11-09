using System;
using System.Windows.Forms;
using TIS.Imaging;
using System.Linq;

namespace QueenPix
{
    public class CameraSettingsDialog : Form
    {
        private ICImagingControl? imagingControl;
        private CameraSettings settings;
        private bool isImagingSource;
        private int webcamDeviceIndex;

        private ComboBox cmbFormat;
        private Button? btnShowProperties;
        private NumericUpDown? numSoftwareFps;
        private Label? lblFpsRange;
        private RadioButton? rbExternalTrigger;
        private RadioButton? rbSoftwareControlled;
        private Label? lblFrameRateHeader;
        private Label? lblSoftwareFpsHeader;
        private Label? lblTriggerNoteField;
        private ComboBox? cmbWebcamFps;
        private Label? lblWebcamFpsStatus;
        private CheckBox chkShowDate;
        private CheckBox chkShowTime;
        private CheckBox chkShowMilliseconds;
        private CheckBox chkGenerateJsonTimestamps;
        private int cameraNumber = 0;
        private string recordingMode = "";
        public CameraSettings Settings { get; private set; }
        public bool SaveAsDefault { get; private set; }
        public bool ApplyToAllCameras { get; private set; }

        public CameraSettingsDialog(
            ICImagingControl? control,
            CameraSettings currentSettings,
            int cameraNum,
            string currentRecordingMode = "",
            bool isImagingSource = true,
            int webcamDeviceIndex = -1)
        {
            imagingControl = control;
            settings = currentSettings.Clone();
            Settings = settings;
            cameraNumber = cameraNum;
            recordingMode = currentRecordingMode;
            this.isImagingSource = isImagingSource;
            this.webcamDeviceIndex = webcamDeviceIndex;

            InitializeDialog();
        }
        
        private void InitializeDialog()
        {
            if (isImagingSource && imagingControl != null)
                this.Text = $"Camera {cameraNumber}: {imagingControl.Device} Settings";
            else
                this.Text = $"Camera {cameraNumber} Settings (Webcam)";

            this.Size = new System.Drawing.Size(550, 620);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            
            int y = 20;
            int leftMargin = 20;
            int labelWidth = 120;
            int controlWidth = 340;
            
            // Format + Resolution dropdown
            Label lblFormat = new Label
            {
                Text = "Format + Resolution:",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(labelWidth, 20)
            };
            this.Controls.Add(lblFormat);
            
            cmbFormat = new ComboBox
            {
                Location = new System.Drawing.Point(leftMargin + labelWidth, y - 2),
                Size = new System.Drawing.Size(controlWidth, 25),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            this.Controls.Add(cmbFormat);
            y += 40;

        // Frame Rate Control radio buttons
        lblFrameRateHeader = new Label
        {
            Text = "Frame Rate Control:",
            Location = new System.Drawing.Point(leftMargin, y),
            Size = new System.Drawing.Size(labelWidth, 20)
        };
        this.Controls.Add(lblFrameRateHeader);

        rbExternalTrigger = new RadioButton
        {
            Text = "External Trigger",
            Location = new System.Drawing.Point(leftMargin + labelWidth, y),
            Size = new System.Drawing.Size(150, 20),
            Checked = settings.UseExternalTrigger
        };
        rbExternalTrigger.CheckedChanged += RbFrameRateControl_CheckedChanged;
        this.Controls.Add(rbExternalTrigger);

        rbSoftwareControlled = new RadioButton
        {
            Text = "Software Controlled",
            Location = new System.Drawing.Point(leftMargin + labelWidth + 160, y),
            Size = new System.Drawing.Size(180, 20),
            Checked = !settings.UseExternalTrigger
        };
        rbSoftwareControlled.CheckedChanged += RbFrameRateControl_CheckedChanged;
        this.Controls.Add(rbSoftwareControlled);

        y += 35;

        // Software Frame Rate input
        lblSoftwareFpsHeader = new Label
        {
            Text = "Software Frame Rate:",
            Location = new System.Drawing.Point(leftMargin, y),
            Size = new System.Drawing.Size(labelWidth, 20)
        };
        this.Controls.Add(lblSoftwareFpsHeader);

        numSoftwareFps = new NumericUpDown
        {
            Location = new System.Drawing.Point(leftMargin + labelWidth, y - 2),
            Size = new System.Drawing.Size(80, 25),
            DecimalPlaces = 4,          // ✅ CHANGED: 2 decimal places for precision
            Minimum = 0.0001M,            // ✅ CHANGED: 0.01 fps = 1 frame per 100 seconds
            Maximum = 240M,
            Value = (decimal)settings.SoftwareFrameRate,
            Enabled = !settings.UseExternalTrigger,
            Increment = 0.1M            // ✅ ADD THIS: Smaller increment steps
        };
        this.Controls.Add(numSoftwareFps);

        lblFpsRange = new Label
        {
            Location = new System.Drawing.Point(leftMargin + labelWidth + 90, y + 2),
            Size = new System.Drawing.Size(250, 20),
            ForeColor = System.Drawing.Color.Gray,
            Font = new System.Drawing.Font("Arial", 8)
        };
        this.Controls.Add(lblFpsRange);

        // Populate FPS range info
        PopulateFpsRangeInfo();

        y += 40;

            // Info note about external trigger
            lblTriggerNoteField = new Label
            {
                Text = "ℹ️ Note: External function generator takes priority if connected",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(460, 20),
                ForeColor = System.Drawing.Color.Blue,
                Font = new System.Drawing.Font("Arial", 8)
            };
            this.Controls.Add(lblTriggerNoteField);
            y += 35;
            
            // Date/Time Overlay Section
            Label lblOverlay = new Label
            {
                Text = "Date/Time Overlay:",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(labelWidth, 20),
                Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
            };
            this.Controls.Add(lblOverlay);
            y += 25;
            
            chkShowDate = new CheckBox
            {
                Text = "Show Date (YYYY-MM-DD)",
                Location = new System.Drawing.Point(leftMargin + labelWidth, y),
                Size = new System.Drawing.Size(200, 20),
                Checked = settings.ShowDate
            };
            this.Controls.Add(chkShowDate);
            y += 25;
            
            chkShowTime = new CheckBox
            {
                Text = "Show Time (HH:MM:SS)",
                Location = new System.Drawing.Point(leftMargin + labelWidth, y),
                Size = new System.Drawing.Size(200, 20),
                Checked = settings.ShowTime
            };
            chkShowTime.CheckedChanged += (s, e) => chkShowMilliseconds.Enabled = chkShowTime.Checked;
            this.Controls.Add(chkShowTime);
            y += 25;
            
            chkShowMilliseconds = new CheckBox
            {
                Text = "Include Milliseconds",
                Location = new System.Drawing.Point(leftMargin + labelWidth + 20, y),
                Size = new System.Drawing.Size(180, 20),
                Checked = settings.ShowMilliseconds,
                Enabled = settings.ShowTime
            };
            this.Controls.Add(chkShowMilliseconds);
            y += 30;
            
            // Disable date/time overlay options if Normal Recording mode
            bool isNormalRecording = recordingMode.Equals("Normal Recording", StringComparison.OrdinalIgnoreCase);
            if (isNormalRecording)
            {
                chkShowDate.Enabled = false;
                chkShowTime.Enabled = false;
                chkShowMilliseconds.Enabled = false;
                
                Label lblOverlayNote = new Label
                {
                    Text = "⚠️ Date/Time overlay is not available in Normal Recording mode",
                    Location = new System.Drawing.Point(leftMargin + labelWidth, y),
                    Size = new System.Drawing.Size(340, 30),
                    ForeColor = System.Drawing.Color.Orange,
                    Font = new System.Drawing.Font("Arial", 8)
                };
                this.Controls.Add(lblOverlayNote);
                y += 30; // Add extra space for the warning message
            }
            else
            {
                y += 5; // Small spacing when no warning
            }
            
            // JSON Timestamp File Generation Section
            Label lblJsonTimestamps = new Label
            {
                Text = "JSON Timestamp Files:",
                Location = new System.Drawing.Point(leftMargin, y),
                Size = new System.Drawing.Size(labelWidth, 20),
                Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
            };
            this.Controls.Add(lblJsonTimestamps);
            y += 25;
            
            chkGenerateJsonTimestamps = new CheckBox
            {
                Text = "Generate JSON timestamp files (one per video)",
                Location = new System.Drawing.Point(leftMargin + labelWidth, y),
                Size = new System.Drawing.Size(340, 20),
                Checked = settings.GenerateJsonTimestamps
            };
            this.Controls.Add(chkGenerateJsonTimestamps);
            y += 30;
            
            Label lblJsonNote = new Label
            {
                Text = "ℹ️ JSON files contain frame-by-frame timestamps and metadata",
                Location = new System.Drawing.Point(leftMargin + labelWidth, y - 5),
                Size = new System.Drawing.Size(340, 20),
                ForeColor = System.Drawing.Color.Gray,
                Font = new System.Drawing.Font("Arial", 8)
            };
            this.Controls.Add(lblJsonNote);
            y += 20;
            
            Label lblJsonWarning = new Label
            {
                Text = "ℹ️ Note: JSON files are generated alongside the saved video file",
                Location = new System.Drawing.Point(leftMargin + labelWidth, y - 5),
                Size = new System.Drawing.Size(340, 30),
                ForeColor = System.Drawing.Color.Gray,
                Font = new System.Drawing.Font("Arial", 8)
            };
            this.Controls.Add(lblJsonWarning);
            y += 35;
            
            // Populate format dropdown
            if (isImagingSource && imagingControl?.VideoFormats != null)
            {
                foreach (var format in imagingControl.VideoFormats)
                    cmbFormat.Items.Add(format.ToString());

                string currentFormat = imagingControl.VideoFormatCurrent?.ToString() ?? "";
                int idx = cmbFormat.Items.IndexOf(currentFormat);

                // Free sizes: the box is editable, so a size that is not in the list can be typed,
                // e.g. "Y800 (480x800)". It is checked against the camera when saving.
                string sizeHelp = "";
                try { sizeHelp = VideoFormatHelper.Help(imagingControl.VideoFormatDescs); } catch { }
                cmbFormat.DropDownStyle = ComboBoxStyle.DropDown;
                new ToolTip { AutoPopDelay = 30000 }.SetToolTip(cmbFormat, sizeHelp);
                this.Controls.Add(new Label
                {
                    Text = "Pick a size or type one, e.g. Y800 (480x800) - hover for the allowed sizes",
                    Location = new System.Drawing.Point(cmbFormat.Left, cmbFormat.Bottom + 1),
                    Size = new System.Drawing.Size(cmbFormat.Width, 14),
                    Font = new System.Drawing.Font("Arial", 7),
                    ForeColor = System.Drawing.Color.Gray
                });

                if (idx >= 0)
                    cmbFormat.SelectedIndex = idx;
                else if (!string.IsNullOrEmpty(currentFormat))
                    cmbFormat.Text = currentFormat;
            }
            else if (!isImagingSource)
            {
                // Webcam: populate with supported resolutions from DirectShow
                var resolutions = DirectShowHelper.GetSupportedResolutions(webcamDeviceIndex);
                foreach (var (w, h) in resolutions)
                    cmbFormat.Items.Add($"{w}x{h}");

                // Select saved format (or default to 640x480)
                string saved = settings.Format;
                if (string.IsNullOrEmpty(saved)) saved = "640x480";
                int idx = cmbFormat.Items.IndexOf(saved);
                if (idx >= 0)
                    cmbFormat.SelectedIndex = idx;
                else if (cmbFormat.Items.Count > 0)
                    cmbFormat.SelectedIndex = 0;

                // Hide TIS-only controls for webcams
                lblFrameRateHeader!.Visible = false;
                rbExternalTrigger!.Visible = false;
                rbSoftwareControlled!.Visible = false;
                lblSoftwareFpsHeader!.Visible = false;
                numSoftwareFps!.Visible = false;
                lblFpsRange!.Visible = false;
                lblTriggerNoteField!.Visible = false;

                // Webcam FPS controls — probe device for supported values
                int fpsY = 60; // same row as the hidden "Frame Rate Control:" area
                Label lblFpsLabel = new Label
                {
                    Text = "Frame Rate:",
                    Location = new System.Drawing.Point(leftMargin, fpsY + 2),
                    Size = new System.Drawing.Size(labelWidth, 20)
                };
                this.Controls.Add(lblFpsLabel);

                cmbWebcamFps = new ComboBox
                {
                    Location = new System.Drawing.Point(leftMargin + labelWidth, fpsY),
                    Size = new System.Drawing.Size(100, 25),
                    DropDownStyle = ComboBoxStyle.DropDownList
                };
                this.Controls.Add(cmbWebcamFps);

                Label lblFpsUnit = new Label
                {
                    Text = "fps",
                    Location = new System.Drawing.Point(leftMargin + labelWidth + 108, fpsY + 2),
                    Size = new System.Drawing.Size(30, 20)
                };
                this.Controls.Add(lblFpsUnit);

                lblWebcamFpsStatus = new Label
                {
                    Location = new System.Drawing.Point(leftMargin + labelWidth, fpsY + 28),
                    Size = new System.Drawing.Size(360, 20),
                    Font = new System.Drawing.Font("Arial", 8),
                    ForeColor = System.Drawing.Color.Gray
                };
                this.Controls.Add(lblWebcamFpsStatus);

                // Parse current resolution to pass to the FPS probe
                int probeW = 640, probeH = 480;
                string savedFmt = settings.Format ?? "";
                if (savedFmt.Contains("x"))
                {
                    var p = savedFmt.Split('x');
                    if (p.Length == 2 && int.TryParse(p[0], out int pw) && int.TryParse(p[1], out int ph))
                    { probeW = pw; probeH = ph; }
                }

                // Probe (device was stopped by caller before opening this dialog)
                var (supportedFps, isSoftwareControllable) = DirectShowHelper.GetSupportedFpsValues(webcamDeviceIndex, probeW, probeH);

                foreach (double fps in supportedFps)
                    cmbWebcamFps.Items.Add(fps);

                // Pre-select the saved FPS (or closest available)
                double savedFps = settings.SoftwareFrameRate > 0 ? settings.SoftwareFrameRate : 30;
                int bestIdx = 0;
                double bestDiff = double.MaxValue;
                for (int i = 0; i < supportedFps.Count; i++)
                {
                    double diff = Math.Abs(supportedFps[i] - savedFps);
                    if (diff < bestDiff) { bestDiff = diff; bestIdx = i; }
                }
                if (cmbWebcamFps.Items.Count > 0)
                    cmbWebcamFps.SelectedIndex = bestIdx;

                double maxFps = supportedFps.Count > 0 ? supportedFps.Max() : 30;
                if (isSoftwareControllable)
                {
                    lblWebcamFpsStatus.Text = $"Software controlled (driver accepted multiple rates)";
                    lblWebcamFpsStatus.ForeColor = System.Drawing.Color.DarkGreen;
                }
                else
                {
                    lblWebcamFpsStatus.Text = $"Hardware limited to {maxFps:F0} fps (cannot be changed in software)";
                    lblWebcamFpsStatus.ForeColor = System.Drawing.Color.OrangeRed;
                    cmbWebcamFps.Enabled = false;
                }
            }

            y += 15;

            if (isImagingSource)
            {
                // Camera Properties button (uses SDK's built-in dialog)
                Label lblProperties = new Label
                {
                    Text = "Camera Properties:",
                    Location = new System.Drawing.Point(leftMargin, y + 5),
                    Size = new System.Drawing.Size(labelWidth, 20)
                };
                this.Controls.Add(lblProperties);

                btnShowProperties = new Button
                {
                    Text = "Adjust Exposure, Gain, etc...",
                    Location = new System.Drawing.Point(leftMargin + labelWidth, y),
                    Size = new System.Drawing.Size(controlWidth, 30)
                };
                btnShowProperties.Click += BtnShowProperties_Click;
                this.Controls.Add(btnShowProperties);
                y += 50;

                Label lblNote = new Label
                {
                    Text = "Note: Property changes (exposure, gain, etc.) are saved automatically by the camera.",
                    Location = new System.Drawing.Point(leftMargin, y),
                    Size = new System.Drawing.Size(460, 30),
                    ForeColor = System.Drawing.Color.Gray,
                    Font = new System.Drawing.Font("Arial", 8)
                };
                this.Controls.Add(lblNote);
                y += 50;
            }
            
            // Buttons (aligned to the right)
            int buttonSpacing = 10;
            int buttonWidth = 85;
            int buttonWidthAll = 140;
            int rightMargin = 20;
            
            Button btnApply = new Button
            {
                Text = "Save",
                Location = new System.Drawing.Point(550 - rightMargin - buttonWidth, y),
                Size = new System.Drawing.Size(buttonWidth, 32),
                Font = new System.Drawing.Font("Arial", 9),
                DialogResult = DialogResult.OK
            };
            btnApply.Click += BtnApply_Click;
            this.Controls.Add(btnApply);

            Button btnApplyToAll = new Button
            {
                Text = "Save for All Cameras",
                Location = new System.Drawing.Point(550 - rightMargin - buttonWidth - buttonSpacing - buttonWidthAll, y),
                Size = new System.Drawing.Size(buttonWidthAll, 32),
                Font = new System.Drawing.Font("Arial", 9)
            };
            btnApplyToAll.Click += BtnApplyToAll_Click;
            this.Controls.Add(btnApplyToAll);

            Button btnCancel = new Button
            {
                Text = "Cancel",
                Location = new System.Drawing.Point(550 - rightMargin - buttonWidth - buttonSpacing - buttonWidthAll - buttonSpacing - buttonWidth, y),
                Size = new System.Drawing.Size(buttonWidth, 32),
                Font = new System.Drawing.Font("Arial", 9),
                DialogResult = DialogResult.Cancel
            };
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnApply;
            this.CancelButton = btnCancel;
        }
        
        private void PopulateFpsRangeInfo()
        {
            if (lblFpsRange == null) return;
            try
            {
                if (imagingControl != null &&
                    imagingControl.DeviceFrameRateAvailable &&
                    imagingControl.DeviceFrameRates != null &&
                    imagingControl.DeviceFrameRates.Length > 0)
                {
                    float[] rates = imagingControl.DeviceFrameRates;
                    float minRate = rates.Min();
                    float maxRate = rates.Max();
                    lblFpsRange.Text = $"Available: {minRate:F1} - {maxRate:F1} fps";
                    lblFpsRange.ForeColor = System.Drawing.Color.Gray;
                }
                else
                {
                    lblFpsRange.Text = "Available: Frame rate not supported by camera";
                    lblFpsRange.ForeColor = System.Drawing.Color.Orange;
                }
            }
            catch
            {
                lblFpsRange.Text = "Available: Frame rate not supported by camera";
                lblFpsRange.ForeColor = System.Drawing.Color.Orange;
            }
        }

        private void RbFrameRateControl_CheckedChanged(object? sender, EventArgs e)
        {
            if (numSoftwareFps != null) numSoftwareFps.Enabled = rbSoftwareControlled?.Checked ?? false;
            if (lblFpsRange != null) lblFpsRange.Enabled = rbSoftwareControlled?.Checked ?? false;
        }
        private float ValidateAndAdjustFrameRate(float requestedFps)
        {
            try
            {
                if (imagingControl != null &&
                    imagingControl.DeviceFrameRateAvailable &&
                    imagingControl.DeviceFrameRates != null &&
                    imagingControl.DeviceFrameRates.Length > 0)
                {
                    float[] rates = imagingControl.DeviceFrameRates;
                    
                    // Check if exact match exists
                    if (rates.Contains(requestedFps))
                    {
                        return requestedFps;
                    }
                    
                    // Find closest valid rate (nearest neighbor)
                    float closestRate;
                    var lowerRates = rates.Where(r => r <= requestedFps).ToArray();
                    var higherRates = rates.Where(r => r > requestedFps).ToArray();

                    if (lowerRates.Length == 0)
                    {
                        // All rates are higher, use minimum
                        closestRate = rates.Min();
                    }
                    else if (higherRates.Length == 0)
                    {
                        // All rates are lower, use maximum
                        closestRate = rates.Max();
                    }
                    else
                    {
                        // Find closest from both sides
                        float lower = lowerRates.Max();
                        float higher = higherRates.Min();
                        
                        // Pick whichever is closer
                        closestRate = (requestedFps - lower) < (higher - requestedFps) ? lower : higher;
                    }
                    
                    // Show warning if adjusted
                    if (Math.Abs(closestRate - requestedFps) > 0.01f)
                    {
                        MessageBox.Show(
                            $"Frame rate adjusted to nearest supported value: {closestRate:F1} fps\n\n" +
                            $"Requested: {requestedFps:F1} fps",
                            "Frame Rate Adjusted",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                    
                    return closestRate;
                }
                else
                {
                    // Camera doesn't support frame rate control - allow any value with warning
                    if (requestedFps != settings.SoftwareFrameRate)
                    {
                        MessageBox.Show(
                            $"This camera does not report supported frame rates.\n\n" +
                            $"Frame rate set to {requestedFps:F1} fps, but may not be applied correctly.",
                            "Frame Rate Warning",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                    return requestedFps;
                }
            }
            catch
            {
                return requestedFps;
            }
        }

        private void BtnShowProperties_Click(object? sender, EventArgs e)
        {
            if (imagingControl == null) return;
            try
            {
                if (!imagingControl.DeviceValid)
                {
                    MessageBox.Show("Camera is not properly connected.\n\n" +
                                  "The property dialog requires the camera to be initialized.\n" +
                                  "This may be due to:\n" +
                                  "1. TIS.Imaging SDK not installed\n" +
                                  "2. DirectShow filters not registered\n" +
                                  "3. Camera driver issues\n\n" +
                                  "Please install the TIS.Imaging SDK and restart the application.",
                                  "Camera Not Ready", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                imagingControl.ShowPropertyDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error showing property dialog: {ex.Message}\n\n" +
                              "This may indicate that:\n" +
                              "1. TIS.Imaging SDK is not properly installed\n" +
                              "2. DirectShow filters are not registered\n" +
                              "3. Camera is not fully initialized\n\n" +
                              "Please install the TIS.Imaging SDK installer and restart the application.",
                               "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        
        
        private void SaveSettings()
        {
            SaveAsDefault = true;

            // Save format setting (Imaging Source: the typed text, which may be a free size)
            if (isImagingSource && !string.IsNullOrWhiteSpace(cmbFormat.Text))
                settings.Format = cmbFormat.Text.Trim();
            else if (cmbFormat.SelectedItem != null)
                settings.Format = cmbFormat.SelectedItem.ToString() ?? "";

            if (isImagingSource)
            {
                // Save frame rate control mode
                settings.UseExternalTrigger = rbExternalTrigger?.Checked ?? false;

                // Validate and save frame rate (only if software controlled)
                if (rbSoftwareControlled?.Checked == true && numSoftwareFps != null)
                {
                    float requestedFps = (float)numSoftwareFps.Value;
                    float finalFps = ValidateAndAdjustFrameRate(requestedFps);
                    settings.SoftwareFrameRate = finalFps;
                }

                // Save VCD properties as XML string
                try
                {
                    if (imagingControl?.VCDPropertyItems != null)
                        settings.VCDPropertiesXml = imagingControl.VCDPropertyItems.Save();
                }
                catch { }
            }
            else
            {
                // Webcam: save selected FPS
                if (cmbWebcamFps?.SelectedItem is double selectedFps)
                    settings.SoftwareFrameRate = (float)selectedFps;
            }

            // Save date/time overlay settings (only if not Normal Recording mode)
            bool isNormalRecording = recordingMode.Equals("Normal Recording", StringComparison.OrdinalIgnoreCase);
            if (!isNormalRecording)
            {
                settings.ShowDate = chkShowDate.Checked;
                settings.ShowTime = chkShowTime.Checked;
                settings.ShowMilliseconds = chkShowMilliseconds.Checked && chkShowTime.Checked;
            }
            else
            {
                settings.ShowDate = false;
                settings.ShowTime = false;
                settings.ShowMilliseconds = false;
            }

            // Save JSON timestamp file generation setting
            settings.GenerateJsonTimestamps = chkGenerateJsonTimestamps.Checked;

            settings.IsImagingSource = isImagingSource;
            Settings = settings;
        }

        // A typed format must be one this camera can do; otherwise explain the allowed sizes and stay open.
        private bool FormatIsValid()
        {
            if (!isImagingSource || imagingControl == null) return true;
            string name = cmbFormat.Text.Trim();
            if (string.IsNullOrEmpty(name)) return true;
            string problem = "";
            VideoFormat? f = null;
            try { f = VideoFormatHelper.Find(imagingControl.VideoFormats, imagingControl.VideoFormatDescs, name, out problem); }
            catch (Exception ex) { problem = ex.Message; }
            if (f != null) return true;
            string help = "";
            try { help = VideoFormatHelper.Help(imagingControl.VideoFormatDescs); } catch { }
            MessageBox.Show($"{problem}\n\n{help}", "Format not possible", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private void BtnApply_Click(object? sender, EventArgs e)
        {
            if (!FormatIsValid()) { this.DialogResult = DialogResult.None; return; }
            ApplyToAllCameras = false;
            SaveSettings();
        }

        private void BtnApplyToAll_Click(object? sender, EventArgs e)
        {
            if (!FormatIsValid()) return;
            ApplyToAllCameras = true;
            SaveSettings();
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}