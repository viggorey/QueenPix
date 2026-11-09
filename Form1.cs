using System;
using System.Collections.Generic;
using System.Windows.Forms;
using TIS.Imaging;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.Runtime.InteropServices;


namespace QueenPix
{
    // User settings class for persistent storage
    public class UserSettings
    {
        public string WorkingFolder { get; set; } = "";
        public string FfmpegPath { get; set; } = "";
        
        // Screenshot settings
        public bool ShowScreenshotSaveDialog { get; set; } = false;
        
        // Max duration settings
        public bool MaxDurationEnabled { get; set; } = false;
        public int MaxDurationValue { get; set; } = 60;
        public string MaxDurationUnit { get; set; } = "minutes"; // "minutes", "hours", "days"
        
        // Charlotte mode settings
        public bool CharlotteMode { get; set; } = false;
        
        public static string GetSettingsPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolder = Path.Combine(appData, "QueenPix");
            Directory.CreateDirectory(appFolder);
            return Path.Combine(appFolder, "settings.json");
        }
        public Dictionary<string, CameraSettings> CameraSettingsByDevice { get; set; } = new Dictionary<string, CameraSettings>();
        public List<CameraNameProfile> NameProfiles { get; set; } = new List<CameraNameProfile>();
        public string LastUsedProfile { get; set; } = "";

        // Naming blocks: grids of run names per session (NamingGrid.cs), clicked in the save dialog
        public bool NamingBlocksEnabled { get; set; } = false;
        public List<NamingGrid> NamingGrids { get; set; } = new List<NamingGrid>();
        public string LastNamingGrid { get; set; } = "";

        // Camera group assignments: DeviceName → GroupId ("A","B","C","D") or "" = unassigned
        public Dictionary<string, string> CameraGroupAssignments { get; set; } = new();
        public Dictionary<string, string> GroupNames { get; set; } = new()
            { ["A"] = "Group A", ["B"] = "Group B", ["C"] = "Group C", ["D"] = "Group D" };
        public List<string> ActiveGroupIds { get; set; } = new() { "A", "B" };

        // Webcam visibility: devices the user has explicitly managed via Manage Cameras
        public List<string> KnownWebcamDevices { get; set; } = new();
        // Persisted exclusion list (updated whenever Manage Cameras is closed with OK)
        public List<string> ExcludedCameraDevices { get; set; } = new();

        public static UserSettings Load()
        {
            try
            {
                string path = GetSettingsPath();
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
                }
            }
            catch { }
            return new UserSettings();
        }
        
        public void Save()
        {
            try
            {
                string path = GetSettingsPath();
                string tmp = path + ".tmp";
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, overwrite: true);
            }
            catch { }
        }
    }

    public class CameraNameProfile
    {
        public string ProfileName { get; set; } = "";
        public Dictionary<string, string> CameraNames { get; set; } = new Dictionary<string, string>();
    }

    public partial class Form1 : Form
    {
        // Class to hold information about each camera
        private class CameraControl
        {
            public ICImagingControl ImagingControl { get; set; } = null!;
            public Label NameLabel { get; set; }
            public Label FpsLabel { get; set; }
            public BaseSink? OriginalSink { get; set; }
            public Button SettingsButton { get; set; }
            public Button TriggerButton { get; set; }
            public CameraSettings Settings { get; set; }
            public int FrameCount { get; set; }
            public DateTime LastFpsUpdate { get; set; }
            public string DeviceName { get; set; }
            public string? RecordingFilePath { get; set; }
            public int TotalRecordedFrames { get; set; }
            public DateTime? RecordingStartTime { get; set; }
            public DateTime? RecordingStopTime { get; set; }
            public TextBox NameTextBox { get; set; }
            public string CustomName { get; set; }
            public int MaxLoopFrames { get; set; }        // loop recording: frames kept
            public int DroppedFrames { get; set; }
            public bool HardwareTriggerEnabledByApp;
            // Recorder of the current normal or loop recording (RAM queue, see QueuedAviRecorder)
            public QueuedAviRecorder? QueuedRecorder { get; set; }
            // Light preview shown over the camera view while recording with the live display off
            public PictureBox? RecordingPreview { get; set; }
            // Timelapse fields
            public bool IsTimelapseMode { get; set; }
            public DateTime? LastTimelapseCapture { get; set; }
            public int TimelapseFrameCount { get; set; }
            public string? TimelapseFolder { get; set; }    // replaces per-frame path list to avoid RAM growth
            public string? TimelapseBaseName { get; set; } // recording base name used in filenames, needed for image2 pattern
            public double TimelapseIntervalSeconds { get; set; }

            // Group assignment
            public string GroupId { get; set; } = "";
            public Label? GroupIndicatorLabel { get; set; }

            // Camera type flag
            public bool IsImagingSource { get; set; } = true;

            // NativeWindow hook used to capture mouse clicks on TIS ImagingControl.
            // The native DirectShow child window inside ImagingControl swallows WM_LBUTTONDOWN
            // before WinForms can raise Click/MouseClick, so we hook at the Win32 level instead.
            public ImagingControlClickHook? ClickHook { get; set; }

            // Webcam-specific fields
            public int WebcamDeviceIndex { get; set; }
            public VideoCapture? WebcamCapture { get; set; }
            public System.Windows.Forms.PictureBox? WebcamPreview { get; set; }
            public Thread? WebcamThread { get; set; }
            public CancellationTokenSource? WebcamCts { get; set; }
            public VideoWriter? WebcamWriter { get; set; }
            public Mat? WebcamLastFrame { get; set; }
            public object WebcamFrameLock { get; set; } = new object();
            public long WebcamFrameCount { get; set; }
            public (int Width, int Height) WebcamResolution { get; set; } = (640, 480);

            // Preview throttle (Fix 1)
            public DateTime WebcamLastPreviewTime { get; set; } = DateTime.MinValue;

            // File size cache (Fix 2)
            public long CachedFileSizeMB { get; set; }
            public DateTime FileSizeLastChecked { get; set; } = DateTime.MinValue;

            // FPS smoothing (Fix 5)
            public double[] FpsHistory { get; set; } = new double[3];
            public int FpsHistoryIndex { get; set; }

            public CameraControl(string deviceName, bool isImagingSource = true)
            {
                if (isImagingSource)
                    ImagingControl = new ICImagingControl();
                NameLabel = new Label();
                FpsLabel = new Label();
                OriginalSink = null;
                FrameCount = 0;
                LastFpsUpdate = DateTime.Now;
                DeviceName = deviceName;
                RecordingFilePath = null;
                TotalRecordedFrames = 0;
                RecordingStartTime = null;
                RecordingStopTime = null;
                SettingsButton = new Button();
                TriggerButton = new Button();
                Settings = new CameraSettings();
                NameTextBox = new TextBox();
                CustomName = "";
                MaxLoopFrames = 0;
            }
        }
        private class SystemInfo
        {
            public double TotalRamGB { get; private set; }
            public double AvailableRamGB { get; private set; }
            public int LogicalCores { get; private set; }
            public int PhysicalCores { get; private set; }
            
            public SystemInfo()
            {
                try
                {
                    // Get RAM info
                    var computerInfo = new Microsoft.VisualBasic.Devices.ComputerInfo();
                    TotalRamGB = computerInfo.TotalPhysicalMemory / (1024.0 * 1024.0 * 1024.0);
                    AvailableRamGB = computerInfo.AvailablePhysicalMemory / (1024.0 * 1024.0 * 1024.0);
                    
                    // Get CPU info
                    LogicalCores = Environment.ProcessorCount;
                    
                    // Estimate physical cores (usually half of logical cores due to hyperthreading)
                    PhysicalCores = LogicalCores / 2;
                    if (PhysicalCores < 1) PhysicalCores = LogicalCores;
                }
                catch
                {
                    // Fallback values if detection fails
                    TotalRamGB = 8.0;
                    AvailableRamGB = 4.0;
                    LogicalCores = 4;
                    PhysicalCores = 2;
                }
            }
            
            public string GetSummary()
            {
                return $"RAM: {AvailableRamGB:F1}/{TotalRamGB:F1} GB available | CPU: {PhysicalCores} cores ({LogicalCores} logical)";
            }
        }

        // List to hold all camera controls
        private List<CameraControl> cameras = new List<CameraControl>();
        
        // Control buttons
        private Button btnRefreshCameras = null!;
        private Button btnStartLive = null!;
        private Button btnStopLive = null!;
        private Button btnLiveToggle = null!;   // the visible Start/Stop Live button
        private Button btnRecordToggle = null!; // the visible Start/Stop Recording button
        private Button btnStartRecording = null!;
        private Button btnStopRecording = null!;
        private Button btnScreenshot = null!;
        private NumericUpDown numTimelapseHours = null!;
        private NumericUpDown numTimelapseMinutes = null!;
        private NumericUpDown numTimelapseSeconds = null!;
        private Dictionary<string, System.Windows.Forms.Timer> _timelapsTimers = new(); // groupId → timer
        private CheckBox chkLoopRecording = null!;
        
        // Working folder controls
        private Label lblWorkingFolder = null!;
        private TextBox txtWorkingFolder = null!;
        private Button btnBrowseWorkingFolder = null!;
        private ComboBox cmbRecordingMode = null!;
        private Label lblLoopDuration = null!;
        private NumericUpDown numLoopDuration = null!;
        private Label lblExternalFps = null!;
        private NumericUpDown numExternalTriggerFps = null!;
        private Label lblRamEstimate = null!;
        private UserSettings settings = null!;     
        private SystemInfo systemInfo = null!;
        
        // Recording state
        private bool isRecording = false;
        // Normal recordings: frames go into a RAM queue and a writer thread saves them, so a slow
        // moment on the disk can no longer make the cameras skip triggers (see QueuedAviRecorder).
        private const int RecordBufferMBPerCamera = 1024;
        private bool isTimelapseMode = false; // class-level flag for webcam capture loop
        private string currentRecordingBaseName = ""; // Store the timestamp-based name
        private bool wasLiveBeforeSettings = false;
        private bool isBenchmarkMode = false;

        // Group recording state
        private Dictionary<string, bool> _groupRecording = new();
        private Dictionary<string, string> _groupRecordingBaseName = new();
        private Dictionary<string, string> _groupRecordingMode = new(); // "A" → "Normal Recording" / "Timelapse"
        private Dictionary<string, List<CameraControl>> _groupCameras = new(); // "A" → exact cameras recorded
        private List<CameraControl>? _recordingFilter = null; // null = all cameras

        // Group UI
        private Panel _groupButtonPanel = null!;
        private Dictionary<string, (Button Rec, Button Stop, Label Status)> _groupButtonMap = new();
        private Button btnManageGroups = null!;

        // Group color map
        private static readonly Dictionary<string, System.Drawing.Color> GroupColors = new()
        {
            ["A"] = System.Drawing.Color.RoyalBlue,
            ["B"] = System.Drawing.Color.ForestGreen,
            ["C"] = System.Drawing.Color.DarkOrange,
            ["D"] = System.Drawing.Color.MediumPurple,
        };


        private bool IsOneDrivePath(string path)
        {
            return path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase);
        }

        // Timer for FPS calculation
        private System.Windows.Forms.Timer fpsTimer = null!;
        
        // FFmpeg path (configurable)
        private string FFMPEG_PATH
        {
            get
            {
                // Use saved path if available and valid
                if (!string.IsNullOrEmpty(settings.FfmpegPath) && File.Exists(settings.FfmpegPath))
                {
                    return settings.FfmpegPath;
                }
                
                // Try to auto-detect
                string? detectedPath = DetectFfmpegPath();
                if (!string.IsNullOrEmpty(detectedPath) && File.Exists(detectedPath))
                {
                    // Save the detected path for future use
                    settings.FfmpegPath = detectedPath;
                    settings.Save();
                    return detectedPath;
                }
                
                // Fallback to default location (this will likely fail, but provides a clear error message)
                return @"C:\Program Files\The Imaging Source Europe GmbH\ffmpeg-8.0-essentials_build\bin\ffmpeg.exe";
            }
        }
        
        // Layout constants
        private const int CAMERA_WIDTH = 320;
        private const int CAMERA_HEIGHT = 240;
        private const int CAMERA_SPACING = 10;
        private const int TOP_MARGIN = 185; // Increased for menu + buttons + working folder + group buttons
        private const int SIDE_MARGIN = 10;
        private readonly float dpiScale;
        
        private int ScaleValue(int value) => (int)Math.Round(value * dpiScale);
        private System.Drawing.Point ScalePoint(int x, int y) => new System.Drawing.Point(ScaleValue(x), ScaleValue(y));
        private System.Drawing.Size ScaleSize(int width, int height) => new System.Drawing.Size(ScaleValue(width), ScaleValue(height));
        private System.Drawing.Size ScaleSize(System.Drawing.Size size) => ScaleSize(size.Width, size.Height);
        private int expandedCameraIndex = -1;
        private bool _inUpdateExpandedLayout = false;
        private long _lastExpansionToggleTick = 0; // Environment.TickCount64 of last ToggleCameraExpansion call
        private int currentPreviewWidth = 320;
        private int currentPreviewHeight = 240;
        private ComboBox cmbPreviewSize = null!;
        private ComboBox cmbLayoutMode = null!;

        private System.Windows.Forms.Timer diskMonitorTimer = null!;
        // Add near line 160, with other private fields
        private HashSet<string> excludedCameraDevices = new HashSet<string>();
        private long lastTotalBytesWritten = 0;
        private bool diskSpaceWarningShown = false;
        private Label lblDiskSpace = null!;
        private Label lblLoopWebcamWarning = null!;

        public Form1()
        {
            InitializeComponent();
            dpiScale = this.DeviceDpi / 96f;
            this.Resize += (s, e) =>
            {
                if (expandedCameraIndex >= 0)
                {
                    UpdateExpandedLayout();
                }
            };
            settings = UserSettings.Load();
            systemInfo = new SystemInfo();
            SetupMainWindow();
            SetupMenu();
            SetupFpsTimer();
            UpdateRecordingButtonColor(); // Apply Charlotte mode setting
            
            // Show splash screen and detect cameras (form will be shown after)
            ShowSplashScreenAndDetectCameras();
            
            CheckDiskSpace(); // Initial disk space check
            this.KeyPreview = true;
            this.KeyDown += Form1_KeyDown;
        }

        private void Form1_KeyDown(object? sender, KeyEventArgs e)
        {
            // Ctrl+R - Start/Stop Recording
            if (e.Control && e.KeyCode == Keys.R)
            {
                if (btnStartRecording.Enabled)
                    BtnStartRecording_Click(null, EventArgs.Empty);
                else if (btnStopRecording.Enabled)
                    BtnStopRecording_Click(null, EventArgs.Empty);
                e.Handled = true;
            }
            // Ctrl+L - Start/Stop Live
            else if (e.Control && e.KeyCode == Keys.L)
            {
                if (btnStartLive.Enabled)
                    BtnStartLive_Click(null, EventArgs.Empty);
                else if (btnStopLive.Enabled)
                    BtnStopLive_Click(null, EventArgs.Empty);
                e.Handled = true;
            }
            // F5 - Refresh Cameras
            else if (e.KeyCode == Keys.F5)
            {
                BtnRefreshCameras_Click(null, EventArgs.Empty);
                e.Handled = true;
            }
            // Escape - Collapse expanded camera
            else if (e.KeyCode == Keys.Escape)
            {
                if (expandedCameraIndex >= 0)
                {
                    CollapseExpandedCamera();
                    e.Handled = true;
                }
            }
            // Ctrl+S - Screenshot
            else if (e.Control && e.KeyCode == Keys.S)
            {
                if (btnScreenshot.Enabled)
                    BtnScreenshot_Click(null, EventArgs.Empty);
                e.Handled = true;
            }
        } 

        private void SetupMenu()
        {
            MenuStrip menuStrip = new MenuStrip();
            
            // File menu
            ToolStripMenuItem fileMenu = new ToolStripMenuItem("File");
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => Application.Exit();
            fileMenu.DropDownItems.Add(exitItem);
            
            // Tools menu
            ToolStripMenuItem toolsMenu = new ToolStripMenuItem("Tools");
            ToolStripMenuItem convertItem = new ToolStripMenuItem("Convert AVIs to MP4...");
            convertItem.Click += ConvertAvisMenuItem_Click;
            toolsMenu.DropDownItems.Add(convertItem);
            ToolStripMenuItem trimItem = new ToolStripMenuItem("Trim Videos...");
            trimItem.Click += TrimVideosMenuItem_Click;
            toolsMenu.DropDownItems.Add(trimItem);
            ToolStripMenuItem verifyItem = new ToolStripMenuItem("Verify recording...");
            verifyItem.Click += (s, e) =>
            {
                using var verifyDialog = new VerifyRecordingDialog(FFMPEG_PATH);
                verifyDialog.ShowDialog(this);
            };
            toolsMenu.DropDownItems.Add(verifyItem);

            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem capacityCalcItem = new ToolStripMenuItem("Loop Recording RAM Calculator...");
            capacityCalcItem.Click += (s, e) => ShowCapacityCalculator();
            toolsMenu.DropDownItems.Add(capacityCalcItem);

            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem centrifugeItem = new ToolStripMenuItem("Centrifuge Analysis...");
            centrifugeItem.Click += (s, e) => new CentrifugeAnalysisDialog().ShowDialog(this);
            toolsMenu.DropDownItems.Add(centrifugeItem);

            // Settings menu
            ToolStripMenuItem settingsMenu = new ToolStripMenuItem("Settings");
            ToolStripMenuItem preferencesItem = new ToolStripMenuItem("Preferences...");
            preferencesItem.Click += (s, e) => ShowSettingsDialog();
            settingsMenu.DropDownItems.Add(preferencesItem);
            
            ToolStripMenuItem profilesItem = new ToolStripMenuItem("Camera Name Profiles...");
            profilesItem.Click += (s, e) => ManageCameraProfiles();
            settingsMenu.DropDownItems.Add(profilesItem);


            
            settingsMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem exportSettingsItem = new ToolStripMenuItem("Export Settings...");
            exportSettingsItem.Click += (s, e) => ExportSettings();
            settingsMenu.DropDownItems.Add(exportSettingsItem);
            
            ToolStripMenuItem importSettingsItem = new ToolStripMenuItem("Import Settings...");
            importSettingsItem.Click += (s, e) => ImportSettings();
            settingsMenu.DropDownItems.Add(importSettingsItem);
            
            // Help menu
            ToolStripMenuItem helpMenu = new ToolStripMenuItem("Help");

            ToolStripMenuItem aboutItem = new ToolStripMenuItem("About");
            aboutItem.Click += (s, e) => MessageBox.Show(
                "QueenPix\n\n" +
                "Version 2.0\n\n" +
                "Synchronized multi-camera recording with accurate frame rate control.\n\n" +
                "KEYBOARD SHORTCUTS:\n" +
                "• Ctrl+L - Start/Stop Live Preview\n" +
                "• Ctrl+R - Start/Stop Recording\n" +
                "• Ctrl+B - Recording Test\n" +
                "• Ctrl+S - Screenshot\n" +
                "• F5 - Refresh Cameras\n",
                "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
            helpMenu.DropDownItems.Add(aboutItem);

            menuStrip.Items.Add(fileMenu);
            menuStrip.Items.Add(toolsMenu);
            menuStrip.Items.Add(settingsMenu);
            menuStrip.Items.Add(helpMenu);
            
            this.MainMenuStrip = menuStrip;
            this.Controls.Add(menuStrip);
        }

        /// <summary>
        /// Auto-detects FFmpeg path by checking application directory first, then common installation locations and PATH environment variable
        /// </summary>
        private string? DetectFfmpegPath()
        {
            // First, check the application directory (for bundled FFmpeg)
            string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string[] appDirectoryPaths = new[]
            {
                Path.Combine(appDirectory, "ffmpeg.exe"),
                Path.Combine(appDirectory, "ffmpeg", "ffmpeg.exe"),
                Path.Combine(appDirectory, "bin", "ffmpeg.exe"),
            };

            foreach (string path in appDirectoryPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            // Common installation paths to check
            string[] commonPaths = new[]
            {
                @"C:\Program Files\The Imaging Source Europe GmbH\ffmpeg-8.0-essentials_build\bin\ffmpeg.exe",
                @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                @"C:\ffmpeg\bin\ffmpeg.exe",
                @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ffmpeg", "bin", "ffmpeg.exe"),
            };

            // Check common paths
            foreach (string path in commonPaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            // Check PATH environment variable
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                string[] paths = pathEnv.Split(Path.PathSeparator);
                foreach (string dir in paths)
                {
                    if (!string.IsNullOrEmpty(dir))
                    {
                        string ffmpegPath = Path.Combine(dir, "ffmpeg.exe");
                        if (File.Exists(ffmpegPath))
                        {
                            return ffmpegPath;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Opens a dialog to configure the FFmpeg path
        /// </summary>
        private void ConfigureFfmpegPath()
        {
            using (Form configForm = new Form())
            {
                configForm.Text = "FFmpeg Settings";
                configForm.Size = new System.Drawing.Size(600, 200);
                configForm.StartPosition = FormStartPosition.CenterParent;
                configForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                configForm.MaximizeBox = false;
                configForm.MinimizeBox = false;

                Label lblInfo = new Label
                {
                    Text = "FFmpeg Path:",
                    Location = new System.Drawing.Point(10, 15),
                    Size = new System.Drawing.Size(100, 20),
                    AutoSize = true
                };
                configForm.Controls.Add(lblInfo);

                TextBox txtPath = new TextBox
                {
                    Text = settings.FfmpegPath,
                    Location = new System.Drawing.Point(10, 40),
                    Size = new System.Drawing.Size(450, 25),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                configForm.Controls.Add(txtPath);

                Button btnBrowse = new Button
                {
                    Text = "Browse...",
                    Location = new System.Drawing.Point(470, 38),
                    Size = new System.Drawing.Size(80, 25),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                btnBrowse.Click += (s, e) =>
                {
                    using (OpenFileDialog dlg = new OpenFileDialog())
                    {
                        dlg.Filter = "FFmpeg executable|ffmpeg.exe|All files|*.*";
                        dlg.FileName = "ffmpeg.exe";
                        if (dlg.ShowDialog() == DialogResult.OK)
                        {
                            txtPath.Text = dlg.FileName;
                        }
                    }
                };
                configForm.Controls.Add(btnBrowse);

                Button btnAutoDetect = new Button
                {
                    Text = "Auto-Detect",
                    Location = new System.Drawing.Point(10, 75),
                    Size = new System.Drawing.Size(100, 25)
                };
                btnAutoDetect.Click += (s, e) =>
                {
                    string? detected = DetectFfmpegPath();
                    if (!string.IsNullOrEmpty(detected))
                    {
                        txtPath.Text = detected;
                        MessageBox.Show($"FFmpeg found at:\n{detected}", "Auto-Detect", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show("FFmpeg not found in common locations or PATH.\nPlease browse to the location manually.", "Auto-Detect", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
                configForm.Controls.Add(btnAutoDetect);

                Label lblCurrent = new Label
                {
                    Text = $"Current: {FFMPEG_PATH}",
                    Location = new System.Drawing.Point(10, 110),
                    Size = new System.Drawing.Size(550, 30),
                    AutoSize = false
                };
                if (!File.Exists(FFMPEG_PATH))
                {
                    lblCurrent.ForeColor = System.Drawing.Color.Red;
                    lblCurrent.Text += " (NOT FOUND)";
                }
                configForm.Controls.Add(lblCurrent);

                Button btnOK = new Button
                {
                    Text = "OK",
                    DialogResult = DialogResult.OK,
                    Location = new System.Drawing.Point(400, 130),
                    Size = new System.Drawing.Size(75, 25),
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right
                };
                configForm.Controls.Add(btnOK);

                Button btnCancel = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Location = new System.Drawing.Point(480, 130),
                    Size = new System.Drawing.Size(75, 25),
                    Anchor = AnchorStyles.Bottom | AnchorStyles.Right
                };
                configForm.Controls.Add(btnCancel);

                configForm.AcceptButton = btnOK;
                configForm.CancelButton = btnCancel;

                if (configForm.ShowDialog() == DialogResult.OK)
                {
                    string newPath = txtPath.Text.Trim();
                    if (string.IsNullOrEmpty(newPath))
                    {
                        // Clear the setting to use auto-detect
                        settings.FfmpegPath = "";
                        settings.Save();
                        MessageBox.Show("FFmpeg path cleared. Auto-detection will be used on next startup.", "Settings Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else if (File.Exists(newPath))
                    {
                        settings.FfmpegPath = newPath;
                        settings.Save();
                        MessageBox.Show($"FFmpeg path saved:\n{newPath}", "Settings Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show($"The specified path does not exist:\n{newPath}\n\nPlease verify the path and try again.", "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
        }

        private void SetupMainWindow()
        {
            this.Text = "QueenPix";
            this.MinimumSize = ScaleSize(800, 400);
            
            // Set application icon - embedded via ApplicationIcon in .csproj
            // Try to load from embedded resources first, then fallback to file if exists
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                // Try embedded resource (when ApplicationIcon is set, it may be embedded)
                using (var stream = assembly.GetManifestResourceStream("QueenPix.icon.ico"))
                {
                    if (stream != null)
                    {
                        this.Icon = new System.Drawing.Icon(stream);
                    }
                    else
                    {
                        // Fallback: try loading from file (for development)
                        string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                        if (File.Exists(iconPath))
                        {
                            this.Icon = new System.Drawing.Icon(iconPath);
                        }
                    }
                }
            }
            catch
            {
                // Icon will use default or embedded icon from ApplicationIcon property
                // The executable will still have the icon embedded for Windows Explorer/taskbar
            }
            
            // Set initial size, but ensure it fits on screen (width can exceed for scrolling)
            int screenWidth = Screen.PrimaryScreen.WorkingArea.Width;
            int screenHeight = Screen.PrimaryScreen.WorkingArea.Height;
            int initialWidth = ScaleSize(1400, 485).Width;
            int initialHeight = ScaleSize(1400, 485).Height;
            
            // Width can be wider than screen (will scroll), but cap at reasonable max
            initialWidth = Math.Min(initialWidth, Math.Max(screenWidth, 1400));
            // Height should fit on screen
            initialHeight = Math.Min(initialHeight, screenHeight - 50); // Leave 50px margin
            
            this.Size = new System.Drawing.Size(initialWidth, initialHeight);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.AutoScroll = true; // Enable horizontal and vertical scrolling

            // Create control buttons at the top (below menu bar)
            int buttonY = 30; // Moved down for menu bar
            int buttonX = 10;
            int buttonWidth = 120;
            int buttonSpacing = 130;

            btnRefreshCameras = new Button
            {
                Text = "Refresh Cameras",
                Location = ScalePoint(buttonX, buttonY),
                Size = ScaleSize(buttonWidth, 30)
            };
            btnRefreshCameras.Click += BtnRefreshCameras_Click;
            this.Controls.Add(btnRefreshCameras);

            btnStartLive = new Button
            {
                Text = "Start All Live",
                Location = ScalePoint(buttonX + buttonSpacing, buttonY),
                Size = ScaleSize(buttonWidth, 30),
                Enabled = false
            };
            btnStartLive.Click += BtnStartLive_Click;
            this.Controls.Add(btnStartLive);

            btnStopLive = new Button
            {
                Text = "Stop All Live",
                Location = ScalePoint(buttonX + buttonSpacing * 2, buttonY),
                Size = ScaleSize(buttonWidth, 30),
                Enabled = false
            };
            btnStopLive.Click += BtnStopLive_Click;
            this.Controls.Add(btnStopLive);

            // One visible Live button. The two buttons above stay (hidden) because the rest of the
            // code enables/disables them to track the live state; the toggle mirrors that state.
            btnStartLive.Visible = false;
            btnStopLive.Visible = false;
            btnLiveToggle = new Button
            {
                Text = "Start All Live",
                Location = ScalePoint(buttonX + buttonSpacing, buttonY),
                Size = ScaleSize(buttonWidth, 30),
                Enabled = false
            };
            btnLiveToggle.Click += (s, e) =>
            {
                if (btnStopLive.Enabled) BtnStopLive_Click(null, EventArgs.Empty);
                else if (btnStartLive.Enabled) BtnStartLive_Click(null, EventArgs.Empty);
            };
            btnStartLive.EnabledChanged += (s, e) => UpdateLiveToggle();
            btnStopLive.EnabledChanged += (s, e) => UpdateLiveToggle();
            this.Controls.Add(btnLiveToggle);

            btnStartRecording = new Button
            {
                Text = "Start Recording",
                Location = ScalePoint(buttonX + buttonSpacing * 2, buttonY),
                Size = ScaleSize(buttonWidth, 30),
                Enabled = false
            };
            btnStartRecording.Click += BtnStartRecording_Click;
            this.Controls.Add(btnStartRecording);

            btnStopRecording = new Button
            {
                Text = "Stop Recording",
                Location = ScalePoint(buttonX + buttonSpacing * 3, buttonY),
                Size = ScaleSize(buttonWidth, 30),
                Enabled = false
            };
            btnStopRecording.Click += BtnStopRecording_Click;
            this.Controls.Add(btnStopRecording);

            // One visible Record button, mirroring the (hidden) start/stop buttons' state
            btnStartRecording.Visible = false;
            btnStopRecording.Visible = false;
            btnRecordToggle = new Button
            {
                Text = "Start Recording",
                Location = ScalePoint(buttonX + buttonSpacing * 2, buttonY),
                Size = ScaleSize(buttonWidth, 30),
                Enabled = false
            };
            btnRecordToggle.Click += (s, e) =>
            {
                if (btnStopRecording.Enabled) BtnStopRecording_Click(null, EventArgs.Empty);
                else if (btnStartRecording.Enabled) BtnStartRecording_Click(null, EventArgs.Empty);
            };
            btnStartRecording.EnabledChanged += (s, e) => UpdateRecordToggle();
            btnStopRecording.EnabledChanged += (s, e) => UpdateRecordToggle();
            this.Controls.Add(btnRecordToggle);

            // Add after btnStopRecording setup (around line 258)
            Button btnManageCameras = new Button
            {
                Text = "Manage Cameras",
                Location = ScalePoint(buttonX + buttonSpacing * 3, buttonY),
                Size = ScaleSize(buttonWidth, 30)
            };
            btnManageCameras.Click += BtnManageCameras_Click;
            this.Controls.Add(btnManageCameras);

            // Add Screenshot button
            btnScreenshot = new Button  // ← Changed from "Button btnScreenshot" to "btnScreenshot"
            {
                Text = "📷 Screenshot",
                Location = ScalePoint(buttonX + buttonSpacing * 4, buttonY),
                Size = ScaleSize(buttonWidth, 30),
                Enabled = false
            };
            btnScreenshot.Click += BtnScreenshot_Click;
            this.Controls.Add(btnScreenshot);

            btnManageGroups = new Button
            {
                Text = "👥 Groups",
                Location = ScalePoint(buttonX + buttonSpacing * 5, buttonY),
                Size = ScaleSize(buttonWidth, 30)
            };
            btnManageGroups.Click += (s, e) => ShowGroupsDialog();
            this.Controls.Add(btnManageGroups);

            // Working folder controls
            int folderY = buttonY + 40;
            
            lblWorkingFolder = new Label
            {
                Text = "Working Folder:",
                Location = ScalePoint(buttonX, folderY + 5),
                Size = ScaleSize(100, 20),
                Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
            };
            this.Controls.Add(lblWorkingFolder);
            
            txtWorkingFolder = new TextBox
            {
                Location = ScalePoint(buttonX + 105, folderY),
                Size = ScaleSize(280, 25),
                Text = string.IsNullOrEmpty(settings.WorkingFolder) ? @"C:\CameraRecordings" : settings.WorkingFolder,
                ReadOnly = true
            };
            this.Controls.Add(txtWorkingFolder);
            
            btnBrowseWorkingFolder = new Button
            {
                Text = "📁",
                Location = ScalePoint(buttonX + 390, folderY - 2),
                Size = ScaleSize(35, 28),
                Font = new System.Drawing.Font("Segoe UI", 10)
            };
            btnBrowseWorkingFolder.Click += BtnBrowseWorkingFolder_Click;
            this.Controls.Add(btnBrowseWorkingFolder);

            lblDiskSpace = new Label
            {
                Text = "Disk Space: --",
                Location = ScalePoint(buttonX + 430, folderY + 2),
                Size = ScaleSize(250, 20),
                Font = new System.Drawing.Font("Arial", 8),
                ForeColor = System.Drawing.Color.Gray
            };
            this.Controls.Add(lblDiskSpace);

            int loopY = folderY + 35;

            // Recording Mode Dropdown
            Label lblRecordingMode = new Label
            {
                Text = "Recording Mode:",
                Location = ScalePoint(buttonX, loopY + 4),
                Size = ScaleSize(110, 20),
                Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
            };
            this.Controls.Add(lblRecordingMode);

            ComboBox cmbRecordingMode = new ComboBox
            {
                Location = ScalePoint(buttonX + 115, loopY),
                Size = ScaleSize(140, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new System.Drawing.Font("Arial", 9)
            };
            cmbRecordingMode.Items.AddRange(new object[] {
                "Normal Recording",
                "Timelapse",
                "Loop Recording"
            });
            cmbRecordingMode.SelectedIndex = 0; // Default to Normal
            this.Controls.Add(cmbRecordingMode);

            // Event handler to show/hide controls based on recording mode
            cmbRecordingMode.SelectedIndexChanged += (s, e) =>
            {
                string selectedMode = cmbRecordingMode.SelectedItem?.ToString() ?? "";
                
                // Hide ALL mode-specific controls first
                lblLoopDuration.Visible = false;
                numLoopDuration.Visible = false;
                lblExternalFps.Visible = false;
                numExternalTriggerFps.Visible = false;
                lblRamEstimate.Visible = false;
                
                // Hide all timelapse controls
                foreach (Control ctrl in this.Controls)
                {
                    if (ctrl.Tag?.ToString() == "timelapse")
                    {
                        ctrl.Visible = false;
                    }
                }
                
                // Show mode-specific controls
                if (selectedMode == "Loop Recording")
                {
                    lblLoopDuration.Visible = true;
                    numLoopDuration.Visible = true;
                    numLoopDuration.Enabled = true;
                    lblExternalFps.Visible = true;
                    numExternalTriggerFps.Visible = true;
                    numExternalTriggerFps.Enabled = true;  // Enable the control
                    lblRamEstimate.Visible = true;
                    UpdateRamEstimate();
                }
                else if (selectedMode == "Timelapse")
                {
                    // Show timelapse controls
                    foreach (Control ctrl in this.Controls)
                    {
                        if (ctrl.Tag?.ToString() == "timelapse")
                        {
                            ctrl.Visible = true;
                        }
                    }
                }
                // Normal Recording shows nothing extra
            };

            // Store reference for later use
            cmbRecordingMode.Tag = "recordingMode";

            // Loop Recording Controls (hidden by default)
            lblLoopDuration = new Label
            {
                Text = "Duration (sec):",
                Location = ScalePoint(buttonX + 265, loopY + 4),
                Size = ScaleSize(90, 20),
                Font = new System.Drawing.Font("Arial", 8),
                Visible = false
            };
            this.Controls.Add(lblLoopDuration);

            numLoopDuration = new NumericUpDown
            {
                Location = ScalePoint(buttonX + 360, loopY),
                Size = ScaleSize(60, 25),
                Minimum = 2,
                Maximum = 60,
                Value = 10,
                Visible = false
            };
            this.Controls.Add(numLoopDuration);

            chkLoopRecording = new CheckBox
            {
                Text = "Loop Recording",
                Location = ScalePoint(buttonX + 130, loopY),
                Size = ScaleSize(120, 25),
                Checked = false,
                Visible = false
            };
            this.Controls.Add(chkLoopRecording);

            // Timelapse Controls (hidden by default)
            Label lblTimelapseInterval = new Label
            {
                Text = "Frame every:",
                Location = ScalePoint(buttonX + 265, loopY + 4),
                Size = ScaleSize(80, 20),
                Font = new System.Drawing.Font("Arial", 8),
                Visible = false,
                Tag = "timelapse"
            };
            this.Controls.Add(lblTimelapseInterval);

            numTimelapseHours = new NumericUpDown
            {
                Location = ScalePoint(buttonX + 350, loopY),
                Size = ScaleSize(50, 25),
                Minimum = 0,
                Maximum = 23,
                Value = 0,
                Visible = false,
                Tag = "timelapse"
            };
            this.Controls.Add(numTimelapseHours);

            Label lblHours = new Label
            {
                Text = "h",
                Location = ScalePoint(buttonX + 405, loopY + 4),
                Size = ScaleSize(15, 20),
                Font = new System.Drawing.Font("Arial", 8),
                Visible = false,
                Tag = "timelapse"
            };
            this.Controls.Add(lblHours);

            numTimelapseMinutes = new NumericUpDown
            {
                Location = ScalePoint(buttonX + 425, loopY),
                Size = ScaleSize(50, 25),
                Minimum = 0,
                Maximum = 59,
                Value = 1,
                Visible = false,
                Tag = "timelapse"
            };
            this.Controls.Add(numTimelapseMinutes);

            Label lblMinutes = new Label
            {
                Text = "m",
                Location = ScalePoint(buttonX + 480, loopY + 4),
                Size = ScaleSize(15, 20),
                Font = new System.Drawing.Font("Arial", 8),
                Visible = false,
                Tag = "timelapse"
            };
            this.Controls.Add(lblMinutes);

            numTimelapseSeconds = new NumericUpDown
            {
                Location = ScalePoint(buttonX + 500, loopY),
                Size = ScaleSize(50, 25),
                Minimum = 0,
                Maximum = 59,
                Value = 0,
                Visible = false,
                Tag = "timelapse"
            };
            this.Controls.Add(numTimelapseSeconds);

            Label lblSeconds = new Label
            {
                Text = "s",
                Location = ScalePoint(buttonX + 555, loopY + 4),
                Size = ScaleSize(15, 20),
                Font = new System.Drawing.Font("Arial", 8),
                Visible = false,
                Tag = "timelapse"
            };
            this.Controls.Add(lblSeconds);

            // Expected FPS control - positioned after Duration (sec), on same line
            lblExternalFps = new Label
            {
                Text = "Expected FPS:",
                Location = ScalePoint(buttonX + 430, loopY + 4),
                Size = ScaleSize(90, 20),
                Font = new System.Drawing.Font("Arial", 8),
                Visible = false
            };
            this.Controls.Add(lblExternalFps);

            numExternalTriggerFps = new NumericUpDown
            {
                Location = ScalePoint(buttonX + 525, loopY),
                Size = ScaleSize(70, 25),
                Minimum = 1,
                Maximum = 1000,   // label only: the camera's own limit is set from its driver (DeviceFrameRates)
                DecimalPlaces = 1,
                Value = 30,
                Enabled = false,
                Visible = false
            };
            this.Controls.Add(numExternalTriggerFps);

            // RAM estimate label (visible in Loop Recording mode)
            lblRamEstimate = new Label
            {
                Text = "Est. RAM: 0 MB",
                Location = ScalePoint(buttonX + 600, loopY + 4),
                Size = ScaleSize(150, 20),
                Font = new System.Drawing.Font("Arial", 8),
                ForeColor = System.Drawing.Color.Blue,
                Visible = false
            };
            this.Controls.Add(lblRamEstimate);

            // Preview Size and Layout controls (positioned after RAM estimate to avoid overlap)
            Label lblLayout = new Label
            {
                Text = "Preview Size:",
                Location = ScalePoint(buttonX + 760, loopY + 4),
                Size = ScaleSize(85, 20),
                Font = new System.Drawing.Font("Arial", 8)
            };
            this.Controls.Add(lblLayout);

            cmbPreviewSize = new ComboBox
            {
                Location = ScalePoint(buttonX + 850, loopY),
                Size = ScaleSize(100, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new System.Drawing.Font("Arial", 8)
            };
            cmbPreviewSize.Items.AddRange(new object[] {
                "Small (240x180)",
                "Medium (320x240)",
                "Large (400x300)",
                "XLarge (480x360)"
            });
            cmbPreviewSize.SelectedIndex = 1; // Default to Medium
            cmbPreviewSize.SelectedIndexChanged += (s, e) => UpdateCameraLayout();
            cmbPreviewSize.Tag = "previewSize";
            this.Controls.Add(cmbPreviewSize);

            Label lblLayoutMode = new Label
            {
                Text = "Layout:",
                Location = ScalePoint(buttonX + 960, loopY + 4),
                Size = ScaleSize(45, 20),
                Font = new System.Drawing.Font("Arial", 8)
            };
            this.Controls.Add(lblLayoutMode);

            cmbLayoutMode = new ComboBox
            {
                Location = ScalePoint(buttonX + 1010, loopY),
                Size = ScaleSize(90, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new System.Drawing.Font("Arial", 8)
            };
            cmbLayoutMode.Items.AddRange(new object[] {
                "Auto Grid",
                "1 Row",
                "2 Rows",
                "3 Rows",
                "4 Rows"
            });
            cmbLayoutMode.SelectedIndex = 0; // Default to Auto
            cmbLayoutMode.SelectedIndexChanged += (s, e) => UpdateCameraLayout();
            cmbLayoutMode.Tag = "layoutMode";
            this.Controls.Add(cmbLayoutMode);

            // Enable/disable controls based on checkbox
            chkLoopRecording.CheckedChanged += (s, e) =>
            {
                if (isBenchmarkMode) return;
                numLoopDuration.Enabled = chkLoopRecording.Checked;
                numExternalTriggerFps.Enabled = chkLoopRecording.Checked;
                UpdateRamEstimate();
            };

            // Update RAM estimate when values change
            numLoopDuration.ValueChanged += (s, e) => UpdateRamEstimate();
            numExternalTriggerFps.ValueChanged += (s, e) => UpdateRamEstimate();

            // Store references
            numExternalTriggerFps.Tag = "externalTriggerFps";
            numLoopDuration.Tag = "loopDuration";
            this.cmbRecordingMode = cmbRecordingMode;

            // Group button panel (Row 4, y=145 in design space, hidden by default)
            _groupButtonPanel = new Panel
            {
                Location = ScalePoint(SIDE_MARGIN, 145),
                Size = new System.Drawing.Size(this.Width - SIDE_MARGIN * 2, ScaleValue(38)),
                Visible = false
            };
            this.Controls.Add(_groupButtonPanel);
        }
        
        /// <summary>
        /// Builds FFmpeg drawtext filter for date/time overlay. Returns filter string or empty if overlay not needed.
        /// Uses a simpler approach with static text based on recording start time.
        /// Note: This shows the recording start time, not per-frame time (FFmpeg dynamic time expressions are complex).
        /// </summary>
        private string BuildFFmpegDateTimeOverlayFilter(CameraSettings settings, DateTime recordingStartTime, int videoHeight)
        {
            if (!settings.ShowDate && !settings.ShowTime)
                return "";
            
            List<string> textParts = new List<string>();
            
            if (settings.ShowDate)
            {
                // Date: yyyy-MM-dd format from recording start time
                textParts.Add(recordingStartTime.ToString("yyyy-MM-dd"));
            }
            
            if (settings.ShowTime)
            {
                // Time: HH:mm:ss or HH:mm:ss.fff format from recording start time
                if (settings.ShowMilliseconds)
                {
                    textParts.Add(recordingStartTime.ToString("HH:mm:ss.fff"));
                }
                else
                {
                    textParts.Add(recordingStartTime.ToString("HH:mm:ss"));
                }
            }
            
            if (textParts.Count == 0)
                return "";
            
            // Combine parts with newline (\\n in C# string becomes \n in FFmpeg filter)
            // Escape special characters for FFmpeg filter syntax
            // First escape single quotes, then add newlines
            for (int i = 0; i < textParts.Count; i++)
            {
                textParts[i] = textParts[i].Replace("'", "''").Replace(":", "\\:");
            }
            string textExpression = string.Join("\\n", textParts);
            
            // Calculate font size based on video height (~2% of height, min 12, max 48)
            int fontSize = Math.Max(12, Math.Min(48, (int)(videoHeight * 0.02)));
            
            // Build the drawtext filter
            // Use single quotes around text to handle special characters
            string filter = $"drawtext=text='{textExpression}':x=10:y=10:fontcolor=white@1.0:borderw=2:bordercolor=black@1.0:fontsize={fontSize}:box=0";
            
            return filter;
        }
        
        /// <summary>
        /// Efficiently overlays date/time text on a bitmap using OpenCV. Returns a new bitmap with overlay.
        /// Optimized for performance to avoid frame rate drops.
        /// </summary>
        private System.Drawing.Bitmap ApplyDateTimeOverlay(System.Drawing.Bitmap source, CameraSettings settings, DateTime? frameTime = null)
        {
            // Early exit if no overlay needed
            if (!settings.ShowDate && !settings.ShowTime)
                return source;
            
            DateTime timestamp = frameTime ?? DateTime.Now;
            
            // Build overlay text
            List<string> overlayLines = new List<string>();
            if (settings.ShowDate)
            {
                overlayLines.Add(timestamp.ToString("yyyy-MM-dd"));
            }
            if (settings.ShowTime)
            {
                string timeFormat = settings.ShowMilliseconds ? "HH:mm:ss.fff" : "HH:mm:ss";
                overlayLines.Add(timestamp.ToString(timeFormat));
            }
            
            if (overlayLines.Count == 0)
                return source;
            
            try
            {
                // Convert bitmap to Mat for efficient OpenCV operations
                using (Mat mat = OpenCvSharp.Extensions.BitmapConverter.ToMat(source))
                {
                    // Ensure we have a color image (3 channels) for text rendering
                    Mat colorMat;
                    bool needsConversion = mat.Channels() == 1;
                    if (needsConversion)
                    {
                        colorMat = new Mat();
                        Cv2.CvtColor(mat, colorMat, ColorConversionCodes.GRAY2BGR);
                    }
                    else
                    {
                        colorMat = mat;
                    }
                    
                    // Calculate font scale based on resolution (scale to ~0.7% of height)
                    double fontScale = Math.Max(0.4, Math.Min(2.0, colorMat.Height / 700.0));
                    int thickness = Math.Max(1, (int)(fontScale * 2));
                    int padding = Math.Max(5, (int)(colorMat.Height * 0.01)); // 1% of height, min 5px
                    
                    // Text properties
                    HersheyFonts font = HersheyFonts.HersheySimplex;
                    Scalar textColor = new Scalar(255, 255, 255); // White
                    Scalar outlineColor = new Scalar(0, 0, 0); // Black
                    int lineHeight = (int)(fontScale * 30);
                    
                    // Draw text with outline for visibility (draw outline first, then text)
                    int yPos = padding + lineHeight;
                    foreach (string line in overlayLines)
                    {
                        // Draw black outline (thicker, offset in 8 directions)
                        for (int dx = -thickness; dx <= thickness; dx++)
                        {
                            for (int dy = -thickness; dy <= thickness; dy++)
                            {
                                if (dx != 0 || dy != 0)
                                {
                                    Cv2.PutText(colorMat, line, 
                                        new OpenCvSharp.Point(padding + dx, yPos + dy), 
                                        font, fontScale, outlineColor, thickness + 1, LineTypes.AntiAlias);
                                }
                            }
                        }
                        
                        // Draw white text on top
                        Cv2.PutText(colorMat, line, 
                            new OpenCvSharp.Point(padding, yPos), 
                            font, fontScale, textColor, thickness, LineTypes.AntiAlias);
                        
                        yPos += lineHeight;
                    }
                    
                    // Convert back to bitmap
                    System.Drawing.Bitmap result;
                    if (needsConversion)
                    {
                        // Convert back to grayscale if original was grayscale
                        Mat grayMat = new Mat();
                        Cv2.CvtColor(colorMat, grayMat, ColorConversionCodes.BGR2GRAY);
                        result = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(grayMat);
                        grayMat.Dispose();
                        colorMat.Dispose();
                    }
                    else
                    {
                        result = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(colorMat);
                        if (needsConversion)
                            colorMat.Dispose();
                    }
                    
                    return result;
                }
            }
            catch (Exception ex)
            {
                // If overlay fails, return original bitmap
                LogCameraInfo($"Warning: Date/time overlay failed: {ex.Message}");
                return source;
            }
        }
        private void ShowSettingsDialog()
        {
            using (SettingsDialog dialog = new SettingsDialog(settings))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    // Update settings
                    settings.ShowScreenshotSaveDialog = dialog.Settings.ShowScreenshotSaveDialog;
                    settings.MaxDurationEnabled = dialog.Settings.MaxDurationEnabled;
                    settings.MaxDurationValue = dialog.Settings.MaxDurationValue;
                    settings.MaxDurationUnit = dialog.Settings.MaxDurationUnit;
                    settings.CharlotteMode = dialog.Settings.CharlotteMode;
                    settings.NamingBlocksEnabled = dialog.Settings.NamingBlocksEnabled;
                    settings.NamingGrids = dialog.Settings.NamingGrids;
                    settings.Save();
                    UpdateRecordingButtonColor();
                }
            }
        }
        
        private void UpdateRecordingButtonColor()
        {
            if (btnStartRecording != null)
            {
                if (settings.CharlotteMode)
                {
                    btnStartRecording.BackColor = System.Drawing.Color.FromArgb(0, 220, 0); // Bright green
                    btnStartRecording.ForeColor = System.Drawing.Color.White;
                }
                else
                {
                    btnStartRecording.BackColor = System.Drawing.SystemColors.Control;
                    btnStartRecording.ForeColor = System.Drawing.SystemColors.ControlText;
                }
            }
        }

        private void ExportSettings()
        {
            try
            {
                using (SaveFileDialog saveDialog = new SaveFileDialog())
                {
                    saveDialog.Filter = "JSON Files|*.json|All Files|*.*";
                    saveDialog.FileName = "QueenPix_Settings.json";
                    saveDialog.Title = "Export Settings";

                    if (saveDialog.ShowDialog(this) == DialogResult.OK)
                    {
                        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(saveDialog.FileName, json);
                        MessageBox.Show($"Settings exported successfully to:\n{saveDialog.FileName}",
                                        "Export Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting settings: {ex.Message}",
                                "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ImportSettings()
        {
            try
            {
                using (OpenFileDialog openDialog = new OpenFileDialog())
                {
                    openDialog.Filter = "JSON Files|*.json|All Files|*.*";
                    openDialog.Title = "Import Settings";

                    if (openDialog.ShowDialog(this) == DialogResult.OK)
                    {
                        string json = File.ReadAllText(openDialog.FileName);
                        UserSettings? importedSettings = JsonSerializer.Deserialize<UserSettings>(json);

                        if (importedSettings == null)
                        {
                            MessageBox.Show("Invalid settings file format.",
                                            "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }

                        var result = MessageBox.Show(
                            "Importing settings will replace your current settings.\n\n" +
                            "This includes:\n" +
                            "• Working folder\n" +
                            "• FFmpeg path\n" +
                            "• Screenshot settings\n" +
                            "• Max duration settings\n" +
                            "• Camera settings\n" +
                            "• Camera name profiles\n\n" +
                            "Continue?",
                            "Confirm Import",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);

                        if (result == DialogResult.Yes)
                        {
                            // Merge settings (keep current camera settings if cameras are active)
                            settings.WorkingFolder = importedSettings.WorkingFolder;
                            settings.FfmpegPath = importedSettings.FfmpegPath;
                            settings.ShowScreenshotSaveDialog = importedSettings.ShowScreenshotSaveDialog;
                            settings.MaxDurationEnabled = importedSettings.MaxDurationEnabled;
                            settings.MaxDurationValue = importedSettings.MaxDurationValue;
                            settings.MaxDurationUnit = importedSettings.MaxDurationUnit;
                            settings.CharlotteMode = importedSettings.CharlotteMode;
                            settings.CameraSettingsByDevice = importedSettings.CameraSettingsByDevice;
                            settings.NameProfiles = importedSettings.NameProfiles;
                            settings.LastUsedProfile = importedSettings.LastUsedProfile;

                            settings.Save();

                            // Update UI
                            txtWorkingFolder.Text = settings.WorkingFolder;
                            CheckDiskSpace();
                            UpdateRecordingButtonColor();

                            MessageBox.Show("Settings imported successfully!\n\n" +
                                          "You may need to refresh cameras for camera settings to take effect.",
                                          "Import Successful", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing settings: {ex.Message}",
                                "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ShowCapacityCalculator()
        {
            Form calcForm = new Form
            {
                Text = "Loop Recording RAM Calculator",
                Size = new System.Drawing.Size(750, 650),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                AutoScroll = true
            };

            int y = 20;

            // System Information Display
            GroupBox grpSystemInfo = new GroupBox
            {
                Text = "Your System",
                Location = new System.Drawing.Point(20, y),
                Size = new System.Drawing.Size(690, 80),
                Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
            };

            Label lblSystemInfo = new Label
            {
                Text = $"Total RAM: {systemInfo.TotalRamGB:F1} GB\n" +
                    $"Available RAM: {systemInfo.AvailableRamGB:F1} GB\n" +
                    $"CPU Cores: {systemInfo.PhysicalCores} physical ({systemInfo.LogicalCores} logical)",
                Location = new System.Drawing.Point(15, 25),
                Size = new System.Drawing.Size(660, 50),
                Font = new System.Drawing.Font("Arial", 9)
            };
            grpSystemInfo.Controls.Add(lblSystemInfo);
            calcForm.Controls.Add(grpSystemInfo);
            y += 90;

            // CALCULATOR SECTION
            GroupBox grpCalc = new GroupBox
            {
                Text = "Camera Configuration",
                Location = new System.Drawing.Point(20, y),
                Size = new System.Drawing.Size(690, 400),
                Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
            };

            int calcY = 30;

            // Number of cameras
            Label lblNumCameras = new Label
            {
                Text = "Number of Cameras:",
                Location = new System.Drawing.Point(20, calcY),
                Size = new System.Drawing.Size(150, 20)
            };
            grpCalc.Controls.Add(lblNumCameras);

            NumericUpDown numCameras = new NumericUpDown
            {
                Location = new System.Drawing.Point(180, calcY - 2),
                Size = new System.Drawing.Size(80, 25),
                Minimum = 1,
                Maximum = 12,
                Value = 4
            };
            grpCalc.Controls.Add(numCameras);

            // Loop duration (shared by all cameras)
            Label lblDuration = new Label
            {
                Text = "Loop Duration (sec):",
                Location = new System.Drawing.Point(300, calcY),
                Size = new System.Drawing.Size(150, 20)
            };
            grpCalc.Controls.Add(lblDuration);

            NumericUpDown numDuration = new NumericUpDown
            {
                Location = new System.Drawing.Point(460, calcY - 2),
                Size = new System.Drawing.Size(80, 25),
                Minimum = 1,
                Maximum = 60,
                Value = 5
            };
            grpCalc.Controls.Add(numDuration);
            calcY += 40;

            // Apply to All checkbox
            CheckBox chkApplyAll = new CheckBox
            {
                Text = "Apply Camera 1 settings to all cameras",
                Location = new System.Drawing.Point(20, calcY),
                Size = new System.Drawing.Size(300, 20),
                Checked = true
            };
            grpCalc.Controls.Add(chkApplyAll);
            calcY += 30;

            // Scrollable panel for camera settings
            Panel cameraPanel = new Panel
            {
                Location = new System.Drawing.Point(20, calcY),
                Size = new System.Drawing.Size(650, 200),
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            grpCalc.Controls.Add(cameraPanel);

            // Store camera controls
            List<ComboBox> resolutionCombos = new List<ComboBox>();
            List<NumericUpDown> fpsNumerics = new List<NumericUpDown>();

            // Function to rebuild camera controls
            Action rebuildCameraControls = () =>
            {
                cameraPanel.Controls.Clear();
                resolutionCombos.Clear();
                fpsNumerics.Clear();

                int camY = 10;
                int numCams = (int)numCameras.Value;

                for (int i = 0; i < numCams; i++)
                {
                    Label lblCam = new Label
                    {
                        Text = $"Camera {i + 1}:",
                        Location = new System.Drawing.Point(10, camY + 2),
                        Size = new System.Drawing.Size(80, 20),
                        Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
                    };
                    cameraPanel.Controls.Add(lblCam);

                    ComboBox cmbRes = new ComboBox
                    {
                        Location = new System.Drawing.Point(100, camY),
                        Size = new System.Drawing.Size(180, 25),
                        DropDownStyle = ComboBoxStyle.DropDownList,
                        Enabled = (i == 0 || !chkApplyAll.Checked)
                    };
                    cmbRes.Items.AddRange(new object[] {
                        "640x480 (Grayscale)",
                        "640x480 (Color)",
                        "1280x720 (Grayscale)",
                        "1280x720 (Color)",
                        "1920x1080 (Color)"
                    });
                    cmbRes.SelectedIndex = 0;
                    cameraPanel.Controls.Add(cmbRes);
                    resolutionCombos.Add(cmbRes);

                    Label lblFpsLabel = new Label
                    {
                        Text = "FPS:",
                        Location = new System.Drawing.Point(300, camY + 2),
                        Size = new System.Drawing.Size(40, 20)
                    };
                    cameraPanel.Controls.Add(lblFpsLabel);

                    NumericUpDown numFps = new NumericUpDown
                    {
                        Location = new System.Drawing.Point(345, camY),
                        Size = new System.Drawing.Size(80, 25),
                        Minimum = 1,
                        Maximum = 240,
                        DecimalPlaces = 1,
                        Value = 108,
                        Enabled = (i == 0 || !chkApplyAll.Checked)
                    };
                    cameraPanel.Controls.Add(numFps);
                    fpsNumerics.Add(numFps);

                    camY += 35;
                }

                // Wire up Apply to All logic
                if (resolutionCombos.Count > 0 && fpsNumerics.Count > 0)
                {
                    resolutionCombos[0].SelectedIndexChanged += (s, e) =>
                    {
                        if (chkApplyAll.Checked)
                        {
                            for (int j = 1; j < resolutionCombos.Count; j++)
                            {
                                resolutionCombos[j].SelectedIndex = resolutionCombos[0].SelectedIndex;
                            }
                        }
                    };

                    fpsNumerics[0].ValueChanged += (s, e) =>
                    {
                        if (chkApplyAll.Checked)
                        {
                            for (int j = 1; j < fpsNumerics.Count; j++)
                            {
                                fpsNumerics[j].Value = fpsNumerics[0].Value;
                            }
                        }
                    };
                }
            };

            // Rebuild when number of cameras changes
            numCameras.ValueChanged += (s, e) => rebuildCameraControls();

            // Handle Apply to All checkbox
            chkApplyAll.CheckedChanged += (s, e) =>
            {
                if (chkApplyAll.Checked && resolutionCombos.Count > 0)
                {
                    // Apply first camera settings to all
                    for (int j = 1; j < resolutionCombos.Count; j++)
                    {
                        resolutionCombos[j].SelectedIndex = resolutionCombos[0].SelectedIndex;
                        resolutionCombos[j].Enabled = false;
                        fpsNumerics[j].Value = fpsNumerics[0].Value;
                        fpsNumerics[j].Enabled = false;
                    }
                }
                else
                {
                    // Enable all controls
                    for (int j = 1; j < resolutionCombos.Count; j++)
                    {
                        resolutionCombos[j].Enabled = true;
                        fpsNumerics[j].Enabled = true;
                    }
                }
            };

            // Initial build
            rebuildCameraControls();

            calcY += 210;

            // Result Label
            Label lblResult = new Label
            {
                Text = "Click Calculate to check compatibility",
                Location = new System.Drawing.Point(20, calcY),
                Size = new System.Drawing.Size(650, 60),
                Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold),
                ForeColor = System.Drawing.Color.Blue
            };
            grpCalc.Controls.Add(lblResult);

            // Calculate Button
            Button btnCalculate = new Button
            {
                Text = "Calculate",
                Location = new System.Drawing.Point(550, 25),
                Size = new System.Drawing.Size(120, 35),
                Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
            };

            btnCalculate.Click += (s, e) =>
            {
                int duration = (int)numDuration.Value;
                double totalRamGB = 0;
                double totalProcessingLoad = 0;
                int totalFrames = 0;

                for (int i = 0; i < resolutionCombos.Count; i++)
                {
                    double fps = (double)fpsNumerics[i].Value;
                    string resolution = resolutionCombos[i].SelectedItem.ToString();

                    // Parse resolution and color
                    int width = 640, height = 480;
                    bool isColor = resolution.Contains("Color");

                    if (resolution.Contains("1280x720"))
                    {
                        width = 1280;
                        height = 720;
                    }
                    else if (resolution.Contains("1920x1080"))
                    {
                        width = 1920;
                        height = 1080;
                    }

                    // Calculate RAM usage
                    int bytesPerPixel = isColor ? 3 : 1;
                    long bytesPerFrame = width * height * bytesPerPixel;
                    long bitmapOverhead = (long)(bytesPerFrame * 1.2);
                    int framesPerCamera = (int)(fps * duration);
                    long cameraRamBytes = framesPerCamera * bitmapOverhead;
                    totalRamGB += cameraRamBytes / (1024.0 * 1024.0 * 1024.0);
                    totalFrames += framesPerCamera;

                    // Calculate processing time per frame
                    double msPerFrame = 1000.0 / fps;
                    double copyTimeMs = 0;

                    if (width == 640 && height == 480)
                        copyTimeMs = isColor ? 1.5 : 1.0;
                    else if (width == 1280 && height == 720)
                        copyTimeMs = isColor ? 3.5 : 2.5;
                    else if (width == 1920 && height == 1080)
                        copyTimeMs = isColor ? 7.0 : 5.0;

                    double processingLoad = (copyTimeMs / msPerFrame) * 100;
                    totalProcessingLoad += processingLoad;
                }

                // Adjust for actual hardware
                double effectiveCores = systemInfo.PhysicalCores * 0.8;
                double adjustedCpuLoad = totalProcessingLoad / effectiveCores;
                double ramPercentage = (totalRamGB / systemInfo.AvailableRamGB) * 100;

                // Determine compatibility
                string verdict = "";
                System.Drawing.Color color = System.Drawing.Color.Green;

                if (ramPercentage > 80 || adjustedCpuLoad > 80)
                {
                    verdict = "❌ NOT RECOMMENDED - Exceeds system capacity";
                    color = System.Drawing.Color.Red;
                }
                else if (ramPercentage > 50 || adjustedCpuLoad > 60)
                {
                    verdict = "⚠️  BORDERLINE - May work but will stress system";
                    color = System.Drawing.Color.Orange;
                }
                else
                {
                    verdict = "✅ COMPATIBLE - Should work well on your system";
                    color = System.Drawing.Color.Green;
                }

                lblResult.Text = $"{verdict}\n" +
                                $"RAM: {totalRamGB:F2} GB ({ramPercentage:F0}% of available) | " +
                                $"CPU: ~{adjustedCpuLoad:F0}% | Total frames: {totalFrames:N0}";
                lblResult.ForeColor = color;
            };

            grpCalc.Controls.Add(btnCalculate);
            calcForm.Controls.Add(grpCalc);
            y += 410;

            // Close Button
            Button btnClose = new Button
            {
                Text = "Close",
                Location = new System.Drawing.Point(310, y),
                Size = new System.Drawing.Size(120, 35),
                DialogResult = DialogResult.OK
            };
            calcForm.Controls.Add(btnClose);

            calcForm.AcceptButton = btnClose;
            calcForm.ShowDialog();
        }
        private void ManageCameraProfiles()
        {
            Form profileForm = new Form
            {
                Text = "Camera Name Profiles",
                Size = new System.Drawing.Size(500, 400),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };
            
            Label lblInfo = new Label
            {
                Text = "Save and load camera naming configurations:",
                Location = new System.Drawing.Point(20, 20),
                Size = new System.Drawing.Size(460, 20),
                Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
            };
            profileForm.Controls.Add(lblInfo);
            
            ListBox lstProfiles = new ListBox
            {
                Location = new System.Drawing.Point(20, 50),
                Size = new System.Drawing.Size(300, 250)
            };
            
            // Load existing profiles
            foreach (var profile in settings.NameProfiles)
            {
                lstProfiles.Items.Add(profile.ProfileName);
            }
            
            profileForm.Controls.Add(lstProfiles);
            
            // Save Current button
            Button btnSaveCurrent = new Button
            {
                Text = "Save Current Names",
                Location = new System.Drawing.Point(340, 50),
                Size = new System.Drawing.Size(140, 30)
            };
            btnSaveCurrent.Click += (s, e) =>
            {
                if (cameras.Count == 0)
                {
                    MessageBox.Show("No cameras detected!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                
                string profileName = Microsoft.VisualBasic.Interaction.InputBox(
                    "Enter profile name:",
                    "Save Profile",
                    $"Profile {settings.NameProfiles.Count + 1}");
                
                if (string.IsNullOrWhiteSpace(profileName))
                    return;
                
                // Create new profile
                var newProfile = new CameraNameProfile
                {
                    ProfileName = profileName,
                    CameraNames = new Dictionary<string, string>()
                };
                
                foreach (var camera in cameras)
                {
                    newProfile.CameraNames[camera.DeviceName] = camera.CustomName;
                }
                
                settings.NameProfiles.Add(newProfile);
                settings.Save();
                
                lstProfiles.Items.Add(profileName);
                MessageBox.Show($"Profile '{profileName}' saved!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            profileForm.Controls.Add(btnSaveCurrent);
            
            // Load Profile button
            Button btnLoad = new Button
            {
                Text = "Load Selected",
                Location = new System.Drawing.Point(340, 90),
                Size = new System.Drawing.Size(140, 30)
            };
            btnLoad.Click += (s, e) =>
            {
                if (lstProfiles.SelectedIndex < 0)
                {
                    MessageBox.Show("Please select a profile!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                
                var profile = settings.NameProfiles[lstProfiles.SelectedIndex];
                
                // Apply names
                int applied = 0;
                foreach (var camera in cameras)
                {
                    if (profile.CameraNames.ContainsKey(camera.DeviceName))
                    {
                        camera.CustomName = profile.CameraNames[camera.DeviceName];
                        camera.NameTextBox.Text = camera.CustomName;
                        applied++;
                    }
                }
                
                settings.LastUsedProfile = profile.ProfileName;
                settings.Save();
                
                MessageBox.Show($"Applied names to {applied} camera(s)!", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            profileForm.Controls.Add(btnLoad);
            
            // Delete Profile button
            Button btnDelete = new Button
            {
                Text = "Delete Selected",
                Location = new System.Drawing.Point(340, 130),
                Size = new System.Drawing.Size(140, 30)
            };
            btnDelete.Click += (s, e) =>
            {
                if (lstProfiles.SelectedIndex < 0)
                {
                    MessageBox.Show("Please select a profile!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                
                var result = MessageBox.Show("Delete this profile?", "Confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (result == DialogResult.Yes)
                {
                    settings.NameProfiles.RemoveAt(lstProfiles.SelectedIndex);
                    lstProfiles.Items.RemoveAt(lstProfiles.SelectedIndex);
                    settings.Save();
                }
            };
            profileForm.Controls.Add(btnDelete);
            
            // Close button
            Button btnClose = new Button
            {
                Text = "Close",
                Location = new System.Drawing.Point(200, 320),
                Size = new System.Drawing.Size(100, 35),
                DialogResult = DialogResult.OK
            };
            profileForm.Controls.Add(btnClose);
            profileForm.AcceptButton = btnClose;
            
            profileForm.ShowDialog();
        }
        private void UpdateRamEstimate()
        {
            try
            {
                // Check if we even have the controls
                if (cameras == null || cameras.Count == 0 || lblRamEstimate == null)
                {
                    if (lblRamEstimate != null)
                    {
                        lblRamEstimate.Text = "";
                        lblRamEstimate.ForeColor = System.Drawing.Color.Blue;
                    }
                    return;
                }
                
                // Only show RAM estimate for loop recording mode
                string selectedMode = cmbRecordingMode?.SelectedItem?.ToString() ?? "";
                if (selectedMode != "Loop Recording")
                {
                    lblRamEstimate.Text = "";
                    lblRamEstimate.ForeColor = System.Drawing.Color.Gray;
                    return;
                }
                
                // Check if cameras are ready
                bool allCamerasReady = cameras.All(c => c.ImagingControl != null && c.ImagingControl.VideoFormatCurrent != null);
                if (!allCamerasReady)
                {
                    lblRamEstimate.Text = "⏳ Cameras loading...";
                    lblRamEstimate.ForeColor = System.Drawing.Color.Gray;
                    return;
                }
                
                int loopSeconds = (int)numLoopDuration.Value;
                double externalTriggerFps = (double)numExternalTriggerFps.Value;
                
                long totalRamBytes = 0;
                double totalProcessingLoad = 0;
                
                foreach (var camera in cameras)
                {
                    // Determine FPS for this camera
                    double fps = camera.Settings.UseExternalTrigger ? externalTriggerFps : camera.Settings.SoftwareFrameRate;
                    
                    // Calculate number of frames
                    int maxFrames = (int)(loopSeconds * fps);
                    
                    // Estimate frame size based on camera format
                    int width = 640;
                    int height = 480;
                    int bytesPerPixel = 1;
                    
                    if (camera.ImagingControl.VideoFormatCurrent != null)
                    {
                        string format = camera.ImagingControl.VideoFormatCurrent.ToString();
                        
                        var match = System.Text.RegularExpressions.Regex.Match(format, @"(\d+)x(\d+)");
                        if (match.Success)
                        {
                            width = int.Parse(match.Groups[1].Value);
                            height = int.Parse(match.Groups[2].Value);
                        }
                        
                        if (format.Contains("RGB") || format.Contains("BGR"))
                        {
                            bytesPerPixel = 3;
                        }
                        else if (format.Contains("Y800") || format.Contains("Mono8"))
                        {
                            bytesPerPixel = 1;
                        }
                    }
                    
                    // Calculate RAM for this camera
                    long bytesPerFrame = width * height * bytesPerPixel;
                    long bitmapOverhead = (long)(bytesPerFrame * 1.2);
                    long cameraRamBytes = maxFrames * bitmapOverhead;
                    totalRamBytes += cameraRamBytes;
                    
                    // Calculate processing load per physical core
                    double msPerFrame = 1000.0 / fps;
                    double copyTimeMs = 0;
                    
                    if (width <= 640 && height <= 480)
                    {
                        copyTimeMs = bytesPerPixel == 3 ? 1.5 : 1.0;
                    }
                    else if (width <= 1280 && height <= 720)
                    {
                        copyTimeMs = bytesPerPixel == 3 ? 3.5 : 2.5;
                    }
                    else if (width <= 1920 && height <= 1080)
                    {
                        copyTimeMs = bytesPerPixel == 3 ? 7.0 : 5.0;
                    }
                    else
                    {
                        double pixelRatio = (width * height) / (1920.0 * 1080.0);
                        copyTimeMs = (bytesPerPixel == 3 ? 7.0 : 5.0) * pixelRatio;
                    }
                    
                    double processingLoad = (copyTimeMs / msPerFrame) * 100;
                    totalProcessingLoad += processingLoad;
                }
                
                // Convert to GB
                double totalRamGB = totalRamBytes / (1024.0 * 1024.0 * 1024.0);
                
                // Calculate percentage of available system RAM
                double ramPercentage = (totalRamGB / systemInfo.AvailableRamGB) * 100;
                
                // Adjust CPU load based on actual core count
                // Assume we can use 80% of physical cores efficiently
                double effectiveCores = systemInfo.PhysicalCores * 0.8;
                double adjustedCpuLoad = totalProcessingLoad / effectiveCores;
                
                // Determine status based on ACTUAL hardware
                string statusIcon = "";
                System.Drawing.Color statusColor = System.Drawing.Color.Green;
                
                // RAM thresholds based on actual available RAM
                bool ramCritical = ramPercentage > 80;  // Using >80% of available RAM
                bool ramHigh = ramPercentage > 50;      // Using >50% of available RAM
                bool ramModerate = ramPercentage > 30;  // Using >30% of available RAM
                
                // CPU thresholds
                bool cpuCritical = adjustedCpuLoad > 80;
                bool cpuHigh = adjustedCpuLoad > 60;
                bool cpuModerate = adjustedCpuLoad > 40;
                
                if (ramCritical || cpuCritical)
                {
                    statusIcon = "❌";
                    statusColor = System.Drawing.Color.Red;
                }
                else if (ramHigh || cpuHigh)
                {
                    statusIcon = "⚠️";
                    statusColor = System.Drawing.Color.Orange;
                }
                else if (ramModerate || cpuModerate)
                {
                    statusIcon = "⚠️";
                    statusColor = System.Drawing.Color.DarkOrange;
                }
                else
                {
                    statusIcon = "✅";
                    statusColor = System.Drawing.Color.Green;
                }
                
                // Display
                lblRamEstimate.Text = $"{statusIcon} RAM: {totalRamGB:F2} GB ({ramPercentage:F0}%)";
                lblRamEstimate.ForeColor = statusColor;
                
                // Enhanced tooltip with actual hardware info
                string tooltipText = $"SYSTEM HARDWARE:\n" +
                                    $"Total RAM: {systemInfo.TotalRamGB:F1} GB\n" +
                                    $"Available RAM: {systemInfo.AvailableRamGB:F1} GB\n" +
                                    $"CPU Cores: {systemInfo.PhysicalCores} ({systemInfo.LogicalCores} logical)\n" +
                                    $"\n" +
                                    $"ESTIMATED LOAD:\n" +
                                    $"RAM Needed: {totalRamGB:F2} GB ({ramPercentage:F0}% of available)\n" +
                                    $"CPU Load: ~{adjustedCpuLoad:F0}% (across {effectiveCores:F1} effective cores)\n" +
                                    $"Cameras: {cameras.Count}";
                
                if (ramPercentage > 50)
                    tooltipText += $"\n\n⚠️ Using {ramPercentage:F0}% of available RAM!";
                if (adjustedCpuLoad > 60)
                    tooltipText += $"\n⚠️ High CPU load - may drop frames!";
                
                // Manage tooltip
                System.Windows.Forms.ToolTip? tooltip = null;
                
                if (lblRamEstimate.Tag is System.Windows.Forms.ToolTip existingTooltip)
                {
                    tooltip = existingTooltip;
                }
                else
                {
                    tooltip = new System.Windows.Forms.ToolTip();
                    lblRamEstimate.Tag = tooltip;
                }
                
                tooltip.SetToolTip(lblRamEstimate, tooltipText);
            }
            catch (Exception ex)
            {
                LogCameraInfo($"UpdateRamEstimate ERROR: {ex.Message}");
                if (lblRamEstimate != null)
                {
                    lblRamEstimate.Text = "❓ Error";
                    lblRamEstimate.ForeColor = System.Drawing.Color.Gray;
                }
            }
        }
        private void SetupFpsTimer()
        {
            fpsTimer = new System.Windows.Forms.Timer();
            fpsTimer.Interval = 1000; // Update every second
            fpsTimer.Tick += FpsTimer_Tick;
            fpsTimer.Start();

            // Add disk monitoring timer
            diskMonitorTimer = new System.Windows.Forms.Timer();
            diskMonitorTimer.Interval = 1000; // Check every second
            diskMonitorTimer.Tick += (s, e) => MonitorDiskWriteSpeed();
            diskMonitorTimer.Start();
        }

        private async void ShowSplashScreenAndDetectCameras()
        {
            // Hide main form initially (before showing splash)
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.Opacity = 0;
            this.Visible = false;
            
            SplashScreen splash = new SplashScreen();
            splash.Show();
            Application.DoEvents(); // Ensure splash screen is visible

            // Run camera detection (this may take a moment)
            DetectAndSetupCameras(suppressMessage: true);

            // Update splash screen with result
            if (cameras.Count > 0)
            {
                splash.UpdateMessage($"Detected {cameras.Count} camera{(cameras.Count == 1 ? "" : "s")}!");
            }
            else
            {
                splash.UpdateMessage("No cameras detected");
            }

            Application.DoEvents();
            await splash.CloseAfterDelay(1500); // Show result for 1.5 seconds

            splash.Close();

            // Now show the main form - ensure we're on UI thread
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => ShowMainForm()));
            }
            else
            {
                ShowMainForm();
            }
        }

        private void ShowMainForm()
        {
            // Now show the main form - restore visibility and state first
            this.Opacity = 1.0;
            this.ShowInTaskbar = true;
            this.Visible = true;
            this.WindowState = FormWindowState.Normal;
            
            // Force refresh to ensure form size is correct
            this.Show();
            this.Refresh();
            this.Update();
            Application.DoEvents();
            
            // Position window in center of screen (after form is shown and sized)
            int screenWidth = Screen.PrimaryScreen.WorkingArea.Width;
            int screenHeight = Screen.PrimaryScreen.WorkingArea.Height;
            
            // Get actual form size (may be different from Width/Height if window state changed)
            int formWidth = this.Width;
            int formHeight = this.Height;
            
            // Center the form
            int xPos = (screenWidth - formWidth) / 2;
            int yPos = (screenHeight - formHeight) / 2;
            
            // If form is wider than screen, align to left edge instead
            if (formWidth > screenWidth)
            {
                xPos = 0;
            }
            else
            {
                // Ensure at least 200px is visible on the right
                xPos = Math.Max(0, Math.Min(xPos, screenWidth - 200));
            }
            
            // Ensure at least 100px is visible at the bottom
            yPos = Math.Max(0, Math.Min(yPos, screenHeight - 100));
            
            this.Location = new System.Drawing.Point(xPos, yPos);
            
            // Force refresh and activation
            this.BringToFront();
            this.Activate();
            this.Focus();
            
            // Ensure all controls are visible and refreshed
            this.Invalidate(true);
            this.PerformLayout();
            
            Application.DoEvents();
        }
        
        private void DetectAndSetupCameras(bool suppressMessage = false)
        {
            // Clear existing cameras
            foreach (var cam in cameras)
            {
                if (!cam.IsImagingSource)
                {
                    // Webcam cleanup
                    try { cam.WebcamCts?.Cancel(); } catch { }
                    try { cam.WebcamThread?.Join(1000); } catch { }
                    try { cam.WebcamCapture?.Dispose(); } catch { }
                    try { cam.WebcamLastFrame?.Dispose(); } catch { }
                    try { cam.WebcamWriter?.Dispose(); } catch { }
                    if (cam.WebcamPreview != null)
                        this.Controls.Remove(cam.WebcamPreview);
                }
                else
                {
                    try
                    {
                        if (cam.ImagingControl.LiveVideoRunning)
                            cam.ImagingControl.LiveStop();
                    }
                    catch { }
                    cam.ImagingControl.Dispose();
                    this.Controls.Remove(cam.ImagingControl);
                }
                this.Controls.Remove(cam.NameLabel);
                this.Controls.Remove(cam.FpsLabel);
                this.Controls.Remove(cam.NameTextBox);
                this.Controls.Remove(cam.SettingsButton);
                this.Controls.Remove(cam.TriggerButton);
                if (cam.GroupIndicatorLabel != null)
                {
                    this.Controls.Remove(cam.GroupIndicatorLabel);
                    cam.GroupIndicatorLabel = null;
                }
            }
            cameras.Clear();

            // Create a temporary ICImagingControl to get available TIS devices
            string[] deviceNames;
            try
            {
                using var tempControl = new ICImagingControl();
                var devices = tempControl.Devices;
                deviceNames = devices?.Select(d => d.Name).ToArray() ?? Array.Empty<string>();
            }
            catch { deviceNames = Array.Empty<string>(); }

            System.Threading.Thread.Sleep(200);


            // Now create a camera control for each device
            for (int i = 0; i < deviceNames.Length; i++)
            {
                string deviceName = deviceNames[i];
                var camera = new CameraControl(deviceName);
                
                int xPosition = SIDE_MARGIN + (i * (CAMERA_WIDTH + CAMERA_SPACING));
                int yPosition = TOP_MARGIN;

                // Editable camera name textbox
                camera.NameTextBox = new TextBox
                {
                    Text = $"Camera{i + 1}",  // This is already correct!
                    Location = new System.Drawing.Point(xPosition, yPosition),
                    Size = new System.Drawing.Size(CAMERA_WIDTH - 10, 20),
                    Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold),
                    BorderStyle = BorderStyle.FixedSingle
                };
                camera.CustomName = $"Camera{i + 1}";
                int capturedIndex = i; // Capture for event handler
                camera.NameTextBox.TextChanged += (s, e) => 
                {
                    cameras[capturedIndex].CustomName = camera.NameTextBox.Text;
                };
                this.Controls.Add(camera.NameTextBox);

                // Device name label (smaller, below textbox)
                camera.NameLabel.Text = deviceName;
                camera.NameLabel.Location = new System.Drawing.Point(xPosition, yPosition + 22);
                camera.NameLabel.AutoSize = false;
                camera.NameLabel.Size = new System.Drawing.Size(CAMERA_WIDTH, 18);
                camera.NameLabel.Font = new System.Drawing.Font("Arial", 7);
                camera.NameLabel.ForeColor = System.Drawing.Color.Gray;
                this.Controls.Add(camera.NameLabel);

                camera.SettingsButton = new Button
                {
                    Text = "⚙️ Settings",
                    Location = new System.Drawing.Point(xPosition + CAMERA_WIDTH - 80, yPosition + 42), 
                    Size = new System.Drawing.Size(75, 22),
                    Font = new System.Drawing.Font("Arial", 7)
                };
                int cameraIndex = i; // Capture index for event handler
                camera.SettingsButton.Click += (s, e) => ShowCameraSettings(cameraIndex);
                this.Controls.Add(camera.SettingsButton);
                camera.SettingsButton.BringToFront();

                // Trigger button - positioned to the left of Settings button
                camera.TriggerButton = new Button
                {
                    Text = "🔘 Trigger",
                    Location = new System.Drawing.Point(xPosition + CAMERA_WIDTH - 160, yPosition + 42),
                    Size = new System.Drawing.Size(75, 22),
                    Font = new System.Drawing.Font("Arial", 7)
                };
                int triggerCameraIndex = i; // Capture index for event handler
                camera.TriggerButton.Click += (s, e) => ToggleCameraTrigger(triggerCameraIndex);
                this.Controls.Add(camera.TriggerButton);
                camera.TriggerButton.BringToFront();

                // Load saved settings for this camera if available
                if (settings.CameraSettingsByDevice.ContainsKey(deviceName))
                {
                    camera.Settings = settings.CameraSettingsByDevice[deviceName];
                }
                if (settings.CameraGroupAssignments.TryGetValue(deviceName, out string? gid))
                    camera.GroupId = gid ?? "";

                camera.FpsLabel.Text = "Ready";
                camera.FpsLabel.Location = new System.Drawing.Point(xPosition, yPosition + 45);
                camera.FpsLabel.Size = new System.Drawing.Size(CAMERA_WIDTH, 20);
                camera.FpsLabel.ForeColor = System.Drawing.Color.Green;
                camera.FpsLabel.Font = new System.Drawing.Font("Arial", 8, System.Drawing.FontStyle.Bold);
                this.Controls.Add(camera.FpsLabel);

                camera.ImagingControl.Location = new System.Drawing.Point(xPosition, yPosition + 68);
                camera.ImagingControl.Size = new System.Drawing.Size(CAMERA_WIDTH, CAMERA_HEIGHT);
                camera.ImagingControl.Name = $"icImagingControl{i + 1}";
                camera.ImagingControl.LiveDisplay = true;
                
                try
                {
                    camera.ImagingControl.Device = deviceName;
                    
                    // Prefer Y800/Mono8 for all cameras, but use whatever is available if not
                    bool formatSet = false;

                    if (camera.ImagingControl.VideoFormats != null && camera.ImagingControl.VideoFormats.Length > 0)
                    {
                        // Try to find any Y800/Mono8 format (at any resolution)
                        var y800Format = camera.ImagingControl.VideoFormats
                            .FirstOrDefault(f => (f.ToString().Contains("Y800") || f.ToString().Contains("Mono8")) &&
                                                f.ToString().Contains("640") &&
                                                f.ToString().Contains("480"));
                        
                        if (y800Format != null)
                        {
                            camera.ImagingControl.VideoFormat = y800Format;
                            formatSet = true;
                        }
                        else
                        {
                            camera.ImagingControl.VideoFormat = camera.ImagingControl.VideoFormats[0];
                            formatSet = true;
                            Console.WriteLine($"Warning: Camera {i + 1} using {camera.ImagingControl.VideoFormats[0]} (Y800 not available)");
                        }
                    }

                    if (!formatSet)
                    {
                        MessageBox.Show($"ERROR: Could not set video format for Camera {i + 1}",
                                      "Format Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }

                   if (camera.Settings != null && !string.IsNullOrEmpty(camera.Settings.Format))
                    {
                        // Try to apply saved format
                        // a preset, or a free size such as "Y800 (480x800)" made from the format description
                        var savedFormat = VideoFormatHelper.Find(camera.ImagingControl.VideoFormats,
                            camera.ImagingControl.VideoFormatDescs, camera.Settings.Format, out string formatProblem);
                        if (savedFormat == null)
                            LogCameraInfo($"Camera {i + 1}: saved format '{camera.Settings.Format}' not applied - {formatProblem}");
                        
                        if (savedFormat != null)
                        {
                            try
                            {
                                camera.ImagingControl.VideoFormat = savedFormat;
                                LogCameraInfo($"Camera {i + 1} loaded saved format: {camera.Settings.Format}");
                            }
                            catch { }
                        }
                        
                        SelectFastestFormat(camera);
                        // Apply other saved properties (exposure, gain, etc.)
                        ApplyCameraSettings(camera, camera.Settings);
                    } 

                    // Just show device name in the small label
                    camera.NameLabel.Text = deviceName;

                    camera.ImagingControl.ImageAvailable += (s, e) => 
                    {
                        if (cameraIndex < cameras.Count)  // Use the existing cameraIndex variable
                        {
                            cameras[cameraIndex].FrameCount++;
                            // Debug: Log first 5 frames to verify event is firing
                            if (cameras[cameraIndex].FrameCount <= 5)
                            {
                                Console.WriteLine($"Camera {cameraIndex + 1} frame {cameras[cameraIndex].FrameCount}");
                            }
                        }
                    };

                    // Add error handler to detect when camera stops unexpectedly
                    camera.ImagingControl.DeviceLost += (s, e) =>
                    {
                        LogCameraInfo($"Camera {cameraIndex + 1} DEVICE LOST during recording!");
                        // Update camera name label to show disconnected status
                        camera.NameLabel.Text = $"{deviceName} [DISCONNECTED]";
                        camera.NameLabel.ForeColor = System.Drawing.Color.Red;
                        if (isRecording)
                        {
                            MessageBox.Show($"WARNING: Camera {cameraIndex + 1} disconnected during recording!",
                                        "Camera Lost", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    };
                    // Hook the ImagingControl's HWND to catch clicks on its native DirectShow
                    // child window (which swallows WM_LBUTTONDOWN before WinForms sees it).
                    int clickIndex = i;
                    camera.ClickHook = new ImagingControlClickHook(
                        camera.ImagingControl, () => ToggleCameraExpansion(clickIndex));
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error setting up camera {i + 1} ({deviceName}): {ex.Message}",
                                    "Camera Setup Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                this.Controls.Add(camera.ImagingControl);
                cameras.Add(camera);
            }

            // ---- Webcam enumeration ----
            // Reload exclusions from persisted settings (covers Refresh Cameras scenario)
            excludedCameraDevices = new HashSet<string>(settings.ExcludedCameraDevices, StringComparer.OrdinalIgnoreCase);

            var tisNames = new HashSet<string>(deviceNames, StringComparer.OrdinalIgnoreCase);
            var allDirectShowDevices = DirectShowHelper.EnumerateVideoDevices();

            // Auto-exclude any webcam the user has never explicitly managed — webcams are
            // opt-in by default so the laptop's built-in camera doesn't appear uninvited.
            var knownWebcams = new HashSet<string>(settings.KnownWebcamDevices, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, _) in allDirectShowDevices.Where(d => !tisNames.Contains(d.Name)))
            {
                if (!knownWebcams.Contains(name))
                    excludedCameraDevices.Add(name);
            }

            var webcamDevices = allDirectShowDevices
                .Where(d => !tisNames.Contains(d.Name) && !excludedCameraDevices.Contains(d.Name))
                .ToList();

            int webcamStartIndex = cameras.Count; // position offset for layout
            for (int wi = 0; wi < webcamDevices.Count; wi++)
            {
                var (wcName, wcIndex) = webcamDevices[wi];
                int camIdx = webcamStartIndex + wi;

                var camera = new CameraControl(wcName, isImagingSource: false)
                {
                    IsImagingSource = false,
                    WebcamDeviceIndex = wcIndex,
                };
                camera.Settings.IsImagingSource = false;

                int xPosition = SIDE_MARGIN + (camIdx * (CAMERA_WIDTH + CAMERA_SPACING));
                int yPosition = TOP_MARGIN;

                // Editable camera name textbox
                camera.NameTextBox = new TextBox
                {
                    Text = $"Camera{camIdx + 1}",
                    Location = new System.Drawing.Point(xPosition, yPosition),
                    Size = new System.Drawing.Size(CAMERA_WIDTH - 10, 20),
                    Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold),
                    BorderStyle = BorderStyle.FixedSingle
                };
                camera.CustomName = $"Camera{camIdx + 1}";
                int capturedWcIdx = camIdx;
                camera.NameTextBox.TextChanged += (s, e) =>
                {
                    if (capturedWcIdx < cameras.Count)
                        cameras[capturedWcIdx].CustomName = camera.NameTextBox.Text;
                };
                this.Controls.Add(camera.NameTextBox);

                // Device name label
                camera.NameLabel.Text = wcName + " (Webcam)";
                camera.NameLabel.Location = new System.Drawing.Point(xPosition, yPosition + 22);
                camera.NameLabel.AutoSize = false;
                camera.NameLabel.Size = new System.Drawing.Size(CAMERA_WIDTH, 18);
                camera.NameLabel.Font = new System.Drawing.Font("Arial", 7);
                camera.NameLabel.ForeColor = System.Drawing.Color.DarkBlue;
                this.Controls.Add(camera.NameLabel);

                // Settings button only (NO trigger button for webcams)
                camera.SettingsButton = new Button
                {
                    Text = "⚙️ Settings",
                    Location = new System.Drawing.Point(xPosition + CAMERA_WIDTH - 80, yPosition + 42),
                    Size = new System.Drawing.Size(75, 22),
                    Font = new System.Drawing.Font("Arial", 7)
                };
                int settingsCamIdx = camIdx;
                camera.SettingsButton.Click += (s, e) => ShowCameraSettings(settingsCamIdx);
                this.Controls.Add(camera.SettingsButton);
                camera.SettingsButton.BringToFront();

                // FPS label
                camera.FpsLabel.Text = "Ready";
                camera.FpsLabel.Location = new System.Drawing.Point(xPosition, yPosition + 45);
                camera.FpsLabel.Size = new System.Drawing.Size(CAMERA_WIDTH - 90, 20);
                camera.FpsLabel.ForeColor = System.Drawing.Color.DarkBlue;
                camera.FpsLabel.Font = new System.Drawing.Font("Arial", 8, System.Drawing.FontStyle.Bold);
                this.Controls.Add(camera.FpsLabel);

                // PictureBox for webcam preview
                camera.WebcamPreview = new System.Windows.Forms.PictureBox
                {
                    Location = new System.Drawing.Point(xPosition, yPosition + 68),
                    Size = new System.Drawing.Size(CAMERA_WIDTH, CAMERA_HEIGHT),
                    SizeMode = System.Windows.Forms.PictureBoxSizeMode.StretchImage,
                    BackColor = System.Drawing.Color.Black
                };
                int previewCamIdx = camIdx;
                camera.WebcamPreview.MouseClick += (s, e) => ToggleCameraExpansion(previewCamIdx);
                this.Controls.Add(camera.WebcamPreview);

                // Load saved settings
                if (settings.CameraSettingsByDevice.ContainsKey(wcName))
                {
                    camera.Settings = settings.CameraSettingsByDevice[wcName];
                    camera.Settings.IsImagingSource = false;
                    if (!string.IsNullOrEmpty(camera.Settings.Format) &&
                        camera.Settings.Format.Contains("x"))
                    {
                        var parts = camera.Settings.Format.Split('x');
                        if (parts.Length == 2 &&
                            int.TryParse(parts[0], out int sw) &&
                            int.TryParse(parts[1], out int sh))
                        {
                            camera.WebcamResolution = (sw, sh);
                        }
                    }
                }
                else
                {
                    camera.Settings.Format = "640x480";
                    camera.WebcamResolution = (640, 480);
                }
                if (settings.CameraGroupAssignments.TryGetValue(wcName, out string? wcGid))
                    camera.GroupId = wcGid ?? "";

                cameras.Add(camera);
            }

            // Show "no cameras" message only if both TIS and webcam lists are empty
            if (cameras.Count == 0)
            {
                MessageBox.Show("No cameras detected! Please connect cameras and click 'Refresh Cameras'.",
                                "No Cameras", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Update loop recording availability based on webcam presence
            UpdateLoopAvailability();
            UpdateGroupButtonRow();

            if (cameras.Count > 0)
            {
                btnStartLive.Enabled = true;
                if (!suppressMessage)
                {
                    MessageBox.Show($"Detected {cameras.Count} camera(s)!",
                                    "Cameras Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }

                // Update RAM estimate with new camera info
                UpdateRamEstimate();
            }

            int requiredWidth = SIDE_MARGIN * 2 + (cameras.Count * (CAMERA_WIDTH + CAMERA_SPACING));
            if (requiredWidth > this.Width)
            {
                this.Width = Math.Min(requiredWidth, 5000);
            }
            UpdateRamEstimate();

            if (cmbPreviewSize != null && cmbLayoutMode != null)
            {
                UpdateCameraLayout();
            }
        }

        /// <summary>
        /// Disables Loop Recording mode when any webcam is connected (webcams don't support it).
        /// </summary>
        private void UpdateLoopAvailability()
        {
            bool hasWebcam = cameras.Any(c => !c.IsImagingSource);
            bool hasGroupAssignments = cameras.Any(c => !string.IsNullOrEmpty(c.GroupId));
            bool disableLoop = hasWebcam || hasGroupAssignments;

            // Remove or re-add "Loop Recording" from the dropdown
            int loopIdx = cmbRecordingMode.Items.IndexOf("Loop Recording");

            if (disableLoop && loopIdx >= 0)
            {
                // If currently selected, switch to Normal Recording first
                if (cmbRecordingMode.SelectedItem?.ToString() == "Loop Recording")
                    cmbRecordingMode.SelectedIndex = 0;
                cmbRecordingMode.Items.RemoveAt(loopIdx);
            }
            else if (!disableLoop && loopIdx < 0)
            {
                // Re-add Loop Recording when no webcams and no group assignments
                cmbRecordingMode.Items.Add("Loop Recording");
            }

            // Show/hide warning label (created lazily)
            if (lblLoopWebcamWarning == null)
            {
                // Find the combobox location to position the warning nearby
                lblLoopWebcamWarning = new Label
                {
                    Text = "⚠️ Loop Recording unavailable — webcam(s) connected",
                    AutoSize = false,
                    Size = new System.Drawing.Size(350, 18),
                    ForeColor = System.Drawing.Color.OrangeRed,
                    Font = new System.Drawing.Font("Arial", 7),
                    Visible = false
                };
                // Position below the recording mode combo
                lblLoopWebcamWarning.Location = new System.Drawing.Point(
                    cmbRecordingMode.Left,
                    cmbRecordingMode.Bottom + 2);
                this.Controls.Add(lblLoopWebcamWarning);
            }
            if (hasWebcam)
                lblLoopWebcamWarning.Text = "⚠️ Loop Recording unavailable — webcam(s) connected";
            else if (hasGroupAssignments)
                lblLoopWebcamWarning.Text = "⚠️ Loop Recording unavailable — group assignments active";
            lblLoopWebcamWarning.Visible = disableLoop;
        }

        private void UpdateGroupButtonRow()
        {
            if (_groupButtonPanel == null) return;
            _groupButtonPanel.Controls.Clear();
            _groupButtonMap.Clear();

            int x = 0;
            int btnH = ScaleValue(30);
            foreach (string groupId in settings.ActiveGroupIds)
            {
                var groupCameras = cameras.Where(c => c.GroupId == groupId).ToList();
                if (groupCameras.Count == 0) continue;

                string gName = settings.GroupNames.GetValueOrDefault(groupId, $"Group {groupId}");
                bool isGroupRec = _groupRecording.GetValueOrDefault(groupId);

                // Colored swatch
                var swatch = new Label
                {
                    Size = new System.Drawing.Size(ScaleValue(14), ScaleValue(14)),
                    BackColor = GroupColors.GetValueOrDefault(groupId, System.Drawing.Color.Gray),
                    BorderStyle = BorderStyle.FixedSingle,
                    Location = new System.Drawing.Point(x, (btnH - ScaleValue(14)) / 2)
                };
                _groupButtonPanel.Controls.Add(swatch);
                x += ScaleValue(18);

                // Group name label
                var nameLabel = new Label
                {
                    Text = gName,
                    AutoSize = false,
                    Size = new System.Drawing.Size(ScaleValue(90), btnH),
                    Location = new System.Drawing.Point(x, 0),
                    TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                    Font = new System.Drawing.Font("Arial", 8, System.Drawing.FontStyle.Bold)
                };
                _groupButtonPanel.Controls.Add(nameLabel);
                x += ScaleValue(94);

                // Rec button
                var recBtn = new Button
                {
                    Text = "▶ Rec",
                    Size = new System.Drawing.Size(ScaleValue(65), btnH),
                    Location = new System.Drawing.Point(x, 0),
                    Enabled = !isGroupRec && !string.IsNullOrEmpty(txtWorkingFolder.Text) && cameras.Count > 0,
                    ForeColor = System.Drawing.Color.DarkGreen
                };
                string capturedGroupId = groupId;
                recBtn.Click += (s2, e2) =>
                {
                    _recordingFilter = cameras.Where(c => c.GroupId == capturedGroupId).ToList();
                    BtnStartRecording_Click(null, EventArgs.Empty);
                };
                _groupButtonPanel.Controls.Add(recBtn);
                x += ScaleValue(69);

                // Stop button
                var stopBtn = new Button
                {
                    Text = "■ Stop",
                    Size = new System.Drawing.Size(ScaleValue(65), btnH),
                    Location = new System.Drawing.Point(x, 0),
                    Enabled = isGroupRec,
                    ForeColor = System.Drawing.Color.DarkRed
                };
                stopBtn.Click += (s2, e2) =>
                {
                    btnScreenshot.Enabled = btnStopLive.Enabled;
                    StopRecording(capturedGroupId);
                };
                _groupButtonPanel.Controls.Add(stopBtn);
                x += ScaleValue(69);

                // Status label
                var statusLabel = new Label
                {
                    Text = isGroupRec ? "🔴 REC" : "Ready",
                    AutoSize = false,
                    Size = new System.Drawing.Size(ScaleValue(80), btnH),
                    Location = new System.Drawing.Point(x, 0),
                    TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                    ForeColor = isGroupRec ? System.Drawing.Color.Red : System.Drawing.Color.Gray,
                    Font = new System.Drawing.Font("Arial", 8)
                };
                _groupButtonPanel.Controls.Add(statusLabel);
                x += ScaleValue(88);

                _groupButtonMap[groupId] = (recBtn, stopBtn, statusLabel);
            }

            bool hasAnyGroupedCamera = cameras.Any(c => !string.IsNullOrEmpty(c.GroupId));
            _groupButtonPanel.Visible = hasAnyGroupedCamera;

            // Global Start Recording: only for unassigned cameras; independent of group sessions
            bool hasUnassigned = cameras.Any(c => string.IsNullOrEmpty(c.GroupId));
            bool globalSessionActive = _groupRecording.GetValueOrDefault(""); // "" key = unassigned cameras
            btnStartRecording.Enabled = hasUnassigned && !globalSessionActive
                && cameras.Count > 0 && !string.IsNullOrEmpty(txtWorkingFolder.Text);
            // Global Stop Recording: only enabled when unassigned cameras are actually recording
            btnStopRecording.Enabled = globalSessionActive;
        }

        private void ShowGroupsDialog()
        {
            using var dlg = new Form
            {
                Text = "Camera Groups",
                Size = new System.Drawing.Size(460, 520),
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false
            };

            // Working copies
            var activeIds = new List<string>(settings.ActiveGroupIds);
            var groupNames = new Dictionary<string, string>(settings.GroupNames);

            // ---- Group name rows ----
            var groupNameBoxes = new Dictionary<string, TextBox>();
            var namesPanel = new Panel { Location = new System.Drawing.Point(10, 10), Size = new System.Drawing.Size(420, 120), BorderStyle = BorderStyle.FixedSingle };
            var namesPanelLabel = new Label { Text = "Group Names", Location = new System.Drawing.Point(10, 0), Size = new System.Drawing.Size(200, 18), Font = new System.Drawing.Font("Arial", 8, System.Drawing.FontStyle.Bold) };
            namesPanel.Controls.Add(namesPanelLabel);
            dlg.Controls.Add(namesPanel);

            string[] allGroupIds = { "A", "B", "C", "D" };
            void RebuildNameRows()
            {
                var oldControls = namesPanel.Controls.OfType<Control>()
                    .Where(c => c != namesPanelLabel).ToList();
                foreach (var c in oldControls) namesPanel.Controls.Remove(c);
                groupNameBoxes.Clear();
                int ny = 20;
                foreach (var gid in activeIds)
                {
                    var sw = new Label
                    {
                        Size = new System.Drawing.Size(14, 14),
                        BackColor = GroupColors.GetValueOrDefault(gid, System.Drawing.Color.Gray),
                        BorderStyle = BorderStyle.FixedSingle,
                        Location = new System.Drawing.Point(8, ny + 2)
                    };
                    namesPanel.Controls.Add(sw);
                    var tb = new TextBox
                    {
                        Text = groupNames.GetValueOrDefault(gid, $"Group {gid}"),
                        Location = new System.Drawing.Point(28, ny),
                        Size = new System.Drawing.Size(180, 22),
                        Tag = gid
                    };
                    namesPanel.Controls.Add(tb);
                    groupNameBoxes[gid] = tb;
                    ny += 26;
                }
            }
            RebuildNameRows();

            var btnAddGroup = new Button { Text = "+ Add Group", Location = new System.Drawing.Point(10, 136), Size = new System.Drawing.Size(90, 26) };
            var btnRemoveGroup = new Button { Text = "− Remove Last", Location = new System.Drawing.Point(108, 136), Size = new System.Drawing.Size(100, 26) };
            dlg.Controls.Add(btnAddGroup);
            dlg.Controls.Add(btnRemoveGroup);
            btnAddGroup.Click += (s, e) =>
            {
                if (activeIds.Count >= 4) { MessageBox.Show("Maximum 4 groups.", "Limit", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                foreach (var id in allGroupIds)
                {
                    if (!activeIds.Contains(id)) { activeIds.Add(id); break; }
                }
                RebuildNameRows();
                namesPanel.Refresh();
            };
            btnRemoveGroup.Click += (s, e) =>
            {
                if (activeIds.Count <= 1) { MessageBox.Show("At least 1 group required.", "Limit", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                activeIds.RemoveAt(activeIds.Count - 1);
                RebuildNameRows();
                namesPanel.Refresh();
            };

            // ---- Camera assignment table ----
            var assignPanel = new Panel { Location = new System.Drawing.Point(10, 172), Size = new System.Drawing.Size(420, 250), BorderStyle = BorderStyle.FixedSingle, AutoScroll = true };
            var assignLabel = new Label { Text = "Camera Assignments (select group or None)", Location = new System.Drawing.Point(4, 2), Size = new System.Drawing.Size(380, 18), Font = new System.Drawing.Font("Arial", 8, System.Drawing.FontStyle.Bold) };
            assignPanel.Controls.Add(assignLabel);
            dlg.Controls.Add(assignPanel);

            var assignRadios = new Dictionary<string, Dictionary<string, RadioButton>>(); // cam→groupId→radio
            int ay = 22;
            foreach (var cam in cameras)
            {
                bool isRecordingCam = cam.RecordingStartTime.HasValue && IsCameraRecording(cam);

                // Camera name label (outside the row panel so it's not clipped)
                var camLabel = new Label { Text = cam.CustomName, Location = new System.Drawing.Point(4, ay + 2), Size = new System.Drawing.Size(110, 20), Font = new System.Drawing.Font("Arial", 8) };
                assignPanel.Controls.Add(camLabel);

                // Each camera gets its own Panel so its RadioButtons are mutually exclusive only within that row
                var rowPanel = new Panel { Location = new System.Drawing.Point(116, ay), Size = new System.Drawing.Size(295, 22) };
                assignPanel.Controls.Add(rowPanel);

                int rx = 0;
                var camRadios = new Dictionary<string, RadioButton>();
                // None radio
                var noneRadio = new RadioButton { Text = "None", Location = new System.Drawing.Point(rx, 1), Size = new System.Drawing.Size(55, 20), Checked = string.IsNullOrEmpty(cam.GroupId), Enabled = !isRecordingCam, Tag = "" };
                rowPanel.Controls.Add(noneRadio);
                camRadios[""] = noneRadio;
                rx += 58;
                foreach (var gid in allGroupIds)
                {
                    var rb = new RadioButton { Text = gid, Location = new System.Drawing.Point(rx, 1), Size = new System.Drawing.Size(36, 20), Checked = cam.GroupId == gid, Enabled = !isRecordingCam && activeIds.Contains(gid), Tag = gid };
                    rowPanel.Controls.Add(rb);
                    camRadios[gid] = rb;
                    rx += 38;
                }
                assignRadios[cam.DeviceName] = camRadios;
                ay += 24;
            }

            // ---- OK / Cancel ----
            var btnOK = new Button { Text = "OK", Location = new System.Drawing.Point(230, 432), Size = new System.Drawing.Size(90, 28), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "Cancel", Location = new System.Drawing.Point(330, 432), Size = new System.Drawing.Size(80, 28), DialogResult = DialogResult.Cancel };
            dlg.Controls.Add(btnOK);
            dlg.Controls.Add(btnCancel);
            dlg.AcceptButton = btnOK;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                // Save group names
                foreach (var gid in activeIds)
                {
                    if (groupNameBoxes.TryGetValue(gid, out var tb))
                        groupNames[gid] = tb.Text;
                }
                settings.GroupNames = groupNames;
                settings.ActiveGroupIds = new List<string>(activeIds);

                // Save camera assignments
                foreach (var cam in cameras)
                {
                    if (!assignRadios.TryGetValue(cam.DeviceName, out var radios)) continue;
                    string chosenGroup = "";
                    foreach (var kvp in radios)
                        if (kvp.Value.Checked) { chosenGroup = kvp.Key; break; }
                    cam.GroupId = chosenGroup;
                    settings.CameraGroupAssignments[cam.DeviceName] = chosenGroup;
                }
                settings.Save();
                UpdateGroupButtonRow();
                UpdateCameraLayout();
                UpdateLoopAvailability();
            }
        }

        private void BtnManageCameras_Click(object? sender, EventArgs e)
        {
            bool wasLive = cameras.Any(c => c.IsImagingSource && c.ImagingControl.LiveVideoRunning);
            bool wasWebcamLive = cameras.Any(c => !c.IsImagingSource && c.WebcamCapture != null);

            if (isRecording)
            {
                MessageBox.Show("Cannot manage cameras while recording!\n\nStop recording first.",
                                "Recording Active", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Enumerate TIS devices
            List<string> allTisDevices = new List<string>();
            using (var tempControl = new ICImagingControl())
            {
                var devices = tempControl.Devices;
                if (devices != null)
                    allTisDevices = devices.Select(d => d.Name).ToList();
            }

            // Enumerate webcam devices (DirectShow devices not in TIS list)
            var tisNameSet = new HashSet<string>(allTisDevices, StringComparer.OrdinalIgnoreCase);
            var allWebcamDevices = DirectShowHelper.EnumerateVideoDevices()
                                    .Where(d => !tisNameSet.Contains(d.Name)).ToList();

            if (allTisDevices.Count == 0 && allWebcamDevices.Count == 0)
            {
                MessageBox.Show("No cameras detected!", "No Cameras", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Unified device list: (Name, IsWebcam, WebcamIndex)
            var deviceEntries = new List<(string Name, bool IsWebcam, int WebcamIndex)>();
            foreach (var d in allTisDevices)
                deviceEntries.Add((d, false, -1));
            foreach (var (name, idx) in allWebcamDevices)
                deviceEntries.Add((name, true, idx));

            // Create dialog
            Form manageForm = new Form
            {
                Text = "Manage Cameras",
                Size = new System.Drawing.Size(500, 400),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false
            };

            int y = 20;

            Label lblInfo = new Label
            {
                Text = "Select which cameras to use:",
                Location = new System.Drawing.Point(20, y),
                Size = new System.Drawing.Size(450, 20),
                Font = new System.Drawing.Font("Arial", 10, System.Drawing.FontStyle.Bold)
            };
            manageForm.Controls.Add(lblInfo);
            y += 30;

            Label lblNote = new Label
            {
                Text = "Webcams are hidden by default — check one to enable it.\nUncheck any camera to hide it from the session.",
                Location = new System.Drawing.Point(20, y),
                Size = new System.Drawing.Size(450, 35),
                ForeColor = System.Drawing.Color.Gray,
                Font = new System.Drawing.Font("Arial", 8)
            };
            manageForm.Controls.Add(lblNote);
            y += 45;

            List<CheckBox> deviceCheckboxes = new List<CheckBox>();

            foreach (var (name, isWebcam, _) in deviceEntries)
            {
                bool isCurrentlyActive = cameras.Any(c => c.DeviceName == name);
                bool isExcluded = excludedCameraDevices.Contains(name);

                string label = isWebcam ? $"{name} (Webcam)" : name;

                CheckBox chkDevice = new CheckBox
                {
                    Text = label,
                    Location = new System.Drawing.Point(40, y),
                    Size = new System.Drawing.Size(400, 25),
                    Checked = !isExcluded,
                    Font = new System.Drawing.Font("Arial", 9)
                };

                if (isCurrentlyActive)
                {
                    chkDevice.Text += " (active)";
                    chkDevice.ForeColor = isWebcam ? System.Drawing.Color.DarkBlue : System.Drawing.Color.Green;
                }
                else if (isExcluded)
                {
                    chkDevice.Text += " (excluded)";
                    chkDevice.ForeColor = System.Drawing.Color.Gray;
                }
                else
                {
                    chkDevice.Text += " (new)";
                    chkDevice.ForeColor = System.Drawing.Color.Blue;
                }

                manageForm.Controls.Add(chkDevice);
                deviceCheckboxes.Add(chkDevice);
                y += 30;
            }

            y += 20;

            Button btnSelectAll = new Button
            {
                Text = "Select All",
                Location = new System.Drawing.Point(120, y),
                Size = new System.Drawing.Size(100, 30)
            };
            btnSelectAll.Click += (s, ev) => { foreach (var chk in deviceCheckboxes) chk.Checked = true; };
            manageForm.Controls.Add(btnSelectAll);

            Button btnDeselectAll = new Button
            {
                Text = "Deselect All",
                Location = new System.Drawing.Point(230, y),
                Size = new System.Drawing.Size(100, 30)
            };
            btnDeselectAll.Click += (s, ev) => { foreach (var chk in deviceCheckboxes) chk.Checked = false; };
            manageForm.Controls.Add(btnDeselectAll);

            y += 50;

            Button btnCancel = new Button
            {
                Text = "Cancel",
                Location = new System.Drawing.Point(260, y),
                Size = new System.Drawing.Size(100, 35),
                DialogResult = DialogResult.Cancel
            };
            manageForm.Controls.Add(btnCancel);

            Button btnApply = new Button
            {
                Text = "Apply",
                Location = new System.Drawing.Point(370, y),
                Size = new System.Drawing.Size(100, 35),
                DialogResult = DialogResult.OK
            };
            manageForm.Controls.Add(btnApply);

            manageForm.AcceptButton = btnApply;
            manageForm.CancelButton = btnCancel;
            manageForm.Height = Math.Max(400, y + 80);

            if (manageForm.ShowDialog() == DialogResult.OK)
            {
                // Update excluded devices list
                excludedCameraDevices.Clear();
                for (int i = 0; i < deviceEntries.Count; i++)
                {
                    if (!deviceCheckboxes[i].Checked)
                        excludedCameraDevices.Add(deviceEntries[i].Name);
                }

                // Mark all webcams shown in this dialog as acknowledged (opt-in seen)
                foreach (var (name, isWebcam, _) in deviceEntries)
                {
                    if (isWebcam && !settings.KnownWebcamDevices.Contains(name))
                        settings.KnownWebcamDevices.Add(name);
                }
                // Persist both the exclusion list and the known-webcams list
                settings.ExcludedCameraDevices = excludedCameraDevices.ToList();
                settings.Save();

                // Check if anything actually changed
                var currentDevices = cameras.Select(c => c.DeviceName).ToHashSet();
                var newActiveDevices = deviceEntries
                    .Where(d => !excludedCameraDevices.Contains(d.Name))
                    .Select(d => d.Name).ToHashSet();

                if (!currentDevices.SetEquals(newActiveDevices))
                {
                    // Stop all live cameras
                    if (wasLive)
                    {
                        foreach (var camera in cameras.Where(c => c.IsImagingSource))
                            try { camera.ImagingControl.LiveStop(); } catch { }
                    }
                    if (wasWebcamLive)
                    {
                        foreach (var camera in cameras.Where(c => !c.IsImagingSource))
                        {
                            camera.WebcamCts?.Cancel();
                            camera.WebcamThread?.Join(1000);
                            camera.WebcamCapture?.Dispose();
                            camera.WebcamCapture = null;
                        }
                    }

                    RebuildCameraList();

                    // Restart live if any cameras were running
                    if ((wasLive || wasWebcamLive) && cameras.Count > 0)
                    {
                        foreach (var camera in cameras)
                        {
                            try
                            {
                                if (camera.IsImagingSource)
                                {
                                    camera.ImagingControl.LiveStart();
                                    camera.LastFpsUpdate = DateTime.Now;
                                    camera.FrameCount = 0;
                                }
                                else
                                {
                                    camera.WebcamCts = new CancellationTokenSource();
                                    camera.WebcamCapture = new VideoCapture(camera.WebcamDeviceIndex, (VideoCaptureAPIs)700);
                                    camera.WebcamCapture.Set(VideoCaptureProperties.FrameWidth, camera.WebcamResolution.Width);
                                    camera.WebcamCapture.Set(VideoCaptureProperties.FrameHeight, camera.WebcamResolution.Height);
                                    camera.WebcamCapture.Set(VideoCaptureProperties.Fps, camera.Settings.SoftwareFrameRate);
                                    camera.LastFpsUpdate = DateTime.Now;
                                    camera.WebcamFrameCount = 0;
                                    var wc = camera;
                                    camera.WebcamThread = new Thread(() => RunWebcamCaptureLoop(wc));
                                    camera.WebcamThread.IsBackground = true;
                                    camera.WebcamThread.Start();
                                }
                            }
                            catch { }
                        }

                        btnStartLive.Enabled = false;
                        btnStopLive.Enabled = true;
                        btnStartRecording.Enabled = true;

                        // Re-apply display settings after LiveStart() resets LiveDisplayDefault.
                        UpdateCameraLayout();
                    }

                    MessageBox.Show($"Camera configuration updated!\n\nActive cameras: {cameras.Count}",
                                    "Cameras Updated", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }
        private void RebuildCameraList()
        {
            // Always reset expanded state — the camera list is about to be rebuilt from scratch
            // and any stale index would auto-expand the wrong (or only) remaining camera.
            expandedCameraIndex = -1;

            // Save current custom names before clearing
            Dictionary<string, string> savedNames = new Dictionary<string, string>();
            foreach (var cam in cameras)
                savedNames[cam.DeviceName] = cam.CustomName;

            // Clear existing cameras from UI (handle both TIS and webcam)
            foreach (var cam in cameras)
            {
                if (cam.IsImagingSource)
                {
                    try { if (cam.ImagingControl.LiveVideoRunning) cam.ImagingControl.LiveStop(); } catch { }
                    this.Controls.Remove(cam.ImagingControl);
                    cam.ImagingControl.Dispose();
                }
                else
                {
                    cam.WebcamCts?.Cancel();
                    cam.WebcamThread?.Join(500);
                    cam.WebcamCapture?.Dispose();
                    cam.WebcamLastFrame?.Dispose();
                    if (cam.WebcamPreview != null)
                        this.Controls.Remove(cam.WebcamPreview);
                }

                this.Controls.Remove(cam.NameLabel);
                this.Controls.Remove(cam.FpsLabel);
                this.Controls.Remove(cam.NameTextBox);
                this.Controls.Remove(cam.SettingsButton);
                if (cam.TriggerButton != null)
                    this.Controls.Remove(cam.TriggerButton);
                cam.ClickHook?.Detach();
                cam.ClickHook = null;
                if (cam.GroupIndicatorLabel != null)
                {
                    this.Controls.Remove(cam.GroupIndicatorLabel);
                    cam.GroupIndicatorLabel = null;
                }
            }
            cameras.Clear();

            // Get TIS devices (empty is OK — webcams may still exist)
            string[] deviceNames = Array.Empty<string>();
            using (var tempControl = new ICImagingControl())
            {
                var devices = tempControl.Devices;
                if (devices != null && devices.Length > 0)
                    deviceNames = devices.Select(d => d.Name).ToArray();
            }

            if (deviceNames.Length > 0)
                System.Threading.Thread.Sleep(200);

            // Filter out excluded TIS devices
            var activeDevices = deviceNames.Where(d => !excludedCameraDevices.Contains(d)).ToList();
            
            // Create camera controls for active devices only
            for (int i = 0; i < activeDevices.Count; i++)
            {
                string deviceName = activeDevices[i];
                var camera = new CameraControl(deviceName);
                
                int xPosition = SIDE_MARGIN + (i * (CAMERA_WIDTH + CAMERA_SPACING));
                int yPosition = TOP_MARGIN;
                
                // Restore custom name if we had one, otherwise use Camera1, Camera2, etc.
                string customName = savedNames.ContainsKey(deviceName) ? savedNames[deviceName] : $"Camera{i + 1}";
                camera.CustomName = customName;
                
                // Editable camera name textbox
                camera.NameTextBox = new TextBox
                {
                    Text = customName,
                    Location = new System.Drawing.Point(xPosition, yPosition),
                    Size = new System.Drawing.Size(CAMERA_WIDTH - 10, 20),
                    Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold),
                    BorderStyle = BorderStyle.FixedSingle
                };
                int capturedIndex = i;
                camera.NameTextBox.TextChanged += (s, e) =>
                {
                    cameras[capturedIndex].CustomName = camera.NameTextBox.Text;
                };
                this.Controls.Add(camera.NameTextBox);
                
                // Device name label
                camera.NameLabel.Text = deviceName;
                camera.NameLabel.Location = new System.Drawing.Point(xPosition, yPosition + 22);
                camera.NameLabel.AutoSize = false;
                camera.NameLabel.Size = new System.Drawing.Size(CAMERA_WIDTH, 18);
                camera.NameLabel.Font = new System.Drawing.Font("Arial", 7);
                camera.NameLabel.ForeColor = System.Drawing.Color.Gray;
                this.Controls.Add(camera.NameLabel);
                
                camera.SettingsButton = new Button
                {
                    Text = "⚙️ Settings",
                    Location = new System.Drawing.Point(xPosition + CAMERA_WIDTH - 80, yPosition + 42),
                    Size = new System.Drawing.Size(75, 22),
                    Font = new System.Drawing.Font("Arial", 7)
                };
                int cameraIndex = i;
                camera.SettingsButton.Click += (s, e) => ShowCameraSettings(cameraIndex);
                this.Controls.Add(camera.SettingsButton);
                camera.SettingsButton.BringToFront();

                // Trigger button - positioned to the left of Settings button
                camera.TriggerButton = new Button
                {
                    Text = "🔘 Trigger",
                    Location = new System.Drawing.Point(xPosition + CAMERA_WIDTH - 160, yPosition + 42),
                    Size = new System.Drawing.Size(75, 22),
                    Font = new System.Drawing.Font("Arial", 7)
                };
                int triggerCameraIndex2 = i; // Capture index for event handler
                camera.TriggerButton.Click += (s, e) => ToggleCameraTrigger(triggerCameraIndex2);
                this.Controls.Add(camera.TriggerButton);
                camera.TriggerButton.BringToFront();
                
                // Load saved settings for this camera if available
                if (settings.CameraSettingsByDevice.ContainsKey(deviceName))
                {
                    camera.Settings = settings.CameraSettingsByDevice[deviceName];
                }
                if (settings.CameraGroupAssignments.TryGetValue(deviceName, out string? rbGid))
                    camera.GroupId = rbGid ?? "";

                camera.FpsLabel.Text = "Ready";
                camera.FpsLabel.Location = new System.Drawing.Point(xPosition, yPosition + 45);
                camera.FpsLabel.Size = new System.Drawing.Size(CAMERA_WIDTH, 20);
                camera.FpsLabel.ForeColor = System.Drawing.Color.Green;
                camera.FpsLabel.Font = new System.Drawing.Font("Arial", 8, System.Drawing.FontStyle.Bold);
                this.Controls.Add(camera.FpsLabel);

                camera.ImagingControl.Location = new System.Drawing.Point(xPosition, yPosition + 68);
                camera.ImagingControl.Size = new System.Drawing.Size(CAMERA_WIDTH, CAMERA_HEIGHT);
                camera.ImagingControl.Name = $"icImagingControl{i + 1}";
                camera.ImagingControl.LiveDisplay = true;
                
                try
                {
                    camera.ImagingControl.Device = deviceName;
                    
                    // Set video format
                    if (camera.ImagingControl.VideoFormats != null && camera.ImagingControl.VideoFormats.Length > 0)
                    {
                        var y800Format = camera.ImagingControl.VideoFormats
                            .FirstOrDefault(f => (f.ToString().Contains("Y800") || f.ToString().Contains("Mono8")) &&
                                                f.ToString().Contains("640") &&
                                                f.ToString().Contains("480"));
                        
                        if (y800Format != null)
                        {
                            camera.ImagingControl.VideoFormat = y800Format;
                        }
                        else
                        {
                            camera.ImagingControl.VideoFormat = camera.ImagingControl.VideoFormats[0];
                        }
                    }
                    
                    // Apply saved format if available
                    if (camera.Settings != null && !string.IsNullOrEmpty(camera.Settings.Format))
                    {
                        var savedFormat = VideoFormatHelper.Find(camera.ImagingControl.VideoFormats,
                            camera.ImagingControl.VideoFormatDescs, camera.Settings.Format, out string formatProblem);
                        if (savedFormat == null)
                            LogCameraInfo($"Camera {camera.DeviceName}: saved format '{camera.Settings.Format}' not applied - {formatProblem}");
                        
                        if (savedFormat != null)
                        {
                            try
                            {
                                camera.ImagingControl.VideoFormat = savedFormat;
                            }
                            catch { }
                        }
                        
                        SelectFastestFormat(camera);
                        ApplyCameraSettings(camera, camera.Settings);
                    }
                    
                    camera.ImagingControl.ImageAvailable += (s, e) =>
                    {
                        if (cameraIndex < cameras.Count)
                        {
                            cameras[cameraIndex].FrameCount++;
                        }
                    };
                    
                    camera.ImagingControl.DeviceLost += (s, e) =>
                    {
                        LogCameraInfo($"Camera {cameraIndex + 1} DEVICE LOST!");
                        // Update camera name label to show disconnected status
                        camera.NameLabel.Text = $"{deviceName} [DISCONNECTED]";
                        camera.NameLabel.ForeColor = System.Drawing.Color.Red;
                        if (isRecording)
                        {
                            MessageBox.Show($"WARNING: Camera {cameraIndex + 1} disconnected during recording!",
                                        "Camera Lost", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    };
                    // Same hook approach as in DetectAndSetupCameras.
                    int clickIndex = i;
                    camera.ClickHook = new ImagingControlClickHook(
                        camera.ImagingControl, () => ToggleCameraExpansion(clickIndex));
                }
                catch (Exception ex)
                {
                    LogCameraInfo($"Error setting up camera {i + 1} ({deviceName}): {ex.Message}");
                }

                this.Controls.Add(camera.ImagingControl);
                cameras.Add(camera);
            }

            // ---- Webcam creation (same filtering via excludedCameraDevices) ----
            var tisNameSet2 = new HashSet<string>(deviceNames, StringComparer.OrdinalIgnoreCase);
            var allDirectShowDevices2 = DirectShowHelper.EnumerateVideoDevices();
            var webcamDevices2 = allDirectShowDevices2
                .Where(d => !tisNameSet2.Contains(d.Name) && !excludedCameraDevices.Contains(d.Name))
                .ToList();

            int webcamStartIndex2 = cameras.Count;
            for (int wi = 0; wi < webcamDevices2.Count; wi++)
            {
                var (wcName, wcIndex) = webcamDevices2[wi];
                int camIdx = webcamStartIndex2 + wi;

                var camera = new CameraControl(wcName, isImagingSource: false)
                {
                    IsImagingSource = false,
                    WebcamDeviceIndex = wcIndex,
                };
                camera.Settings.IsImagingSource = false;

                int xPosition = SIDE_MARGIN + (camIdx * (CAMERA_WIDTH + CAMERA_SPACING));
                int yPosition = TOP_MARGIN;

                string customName = savedNames.ContainsKey(wcName) ? savedNames[wcName] : $"Camera{camIdx + 1}";
                camera.CustomName = customName;

                camera.NameTextBox = new TextBox
                {
                    Text = customName,
                    Location = new System.Drawing.Point(xPosition, yPosition),
                    Size = new System.Drawing.Size(CAMERA_WIDTH - 10, 20),
                    Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold),
                    BorderStyle = BorderStyle.FixedSingle
                };
                int capturedWcIdx = camIdx;
                camera.NameTextBox.TextChanged += (s, e) =>
                {
                    if (capturedWcIdx < cameras.Count)
                        cameras[capturedWcIdx].CustomName = camera.NameTextBox.Text;
                };
                this.Controls.Add(camera.NameTextBox);

                camera.NameLabel.Text = wcName + " (Webcam)";
                camera.NameLabel.Location = new System.Drawing.Point(xPosition, yPosition + 22);
                camera.NameLabel.AutoSize = false;
                camera.NameLabel.Size = new System.Drawing.Size(CAMERA_WIDTH, 18);
                camera.NameLabel.Font = new System.Drawing.Font("Arial", 7);
                camera.NameLabel.ForeColor = System.Drawing.Color.DarkBlue;
                this.Controls.Add(camera.NameLabel);

                camera.SettingsButton = new Button
                {
                    Text = "⚙️ Settings",
                    Location = new System.Drawing.Point(xPosition + CAMERA_WIDTH - 80, yPosition + 42),
                    Size = new System.Drawing.Size(75, 22),
                    Font = new System.Drawing.Font("Arial", 7)
                };
                int settingsCamIdx = camIdx;
                camera.SettingsButton.Click += (s, e) => ShowCameraSettings(settingsCamIdx);
                this.Controls.Add(camera.SettingsButton);
                camera.SettingsButton.BringToFront();

                camera.FpsLabel.Text = "Ready";
                camera.FpsLabel.Location = new System.Drawing.Point(xPosition, yPosition + 45);
                camera.FpsLabel.Size = new System.Drawing.Size(CAMERA_WIDTH - 90, 20);
                camera.FpsLabel.ForeColor = System.Drawing.Color.DarkBlue;
                camera.FpsLabel.Font = new System.Drawing.Font("Arial", 8, System.Drawing.FontStyle.Bold);
                this.Controls.Add(camera.FpsLabel);

                camera.WebcamPreview = new System.Windows.Forms.PictureBox
                {
                    Location = new System.Drawing.Point(xPosition, yPosition + 68),
                    Size = new System.Drawing.Size(CAMERA_WIDTH, CAMERA_HEIGHT),
                    SizeMode = System.Windows.Forms.PictureBoxSizeMode.StretchImage,
                    BackColor = System.Drawing.Color.Black
                };
                int previewCamIdx = camIdx;
                camera.WebcamPreview.MouseClick += (s, e) => ToggleCameraExpansion(previewCamIdx);
                this.Controls.Add(camera.WebcamPreview);

                if (settings.CameraSettingsByDevice.ContainsKey(wcName))
                {
                    camera.Settings = settings.CameraSettingsByDevice[wcName];
                    camera.Settings.IsImagingSource = false;
                    if (!string.IsNullOrEmpty(camera.Settings.Format) && camera.Settings.Format.Contains("x"))
                    {
                        var parts = camera.Settings.Format.Split('x');
                        if (parts.Length == 2 && int.TryParse(parts[0], out int sw) && int.TryParse(parts[1], out int sh))
                            camera.WebcamResolution = (sw, sh);
                    }
                }
                else
                {
                    camera.Settings.Format = "640x480";
                    camera.WebcamResolution = (640, 480);
                }
                if (settings.CameraGroupAssignments.TryGetValue(wcName, out string? rbWcGid))
                    camera.GroupId = rbWcGid ?? "";

                cameras.Add(camera);
            }

            UpdateLoopAvailability();
            UpdateGroupButtonRow();

            if (cameras.Count > 0)
            {
                btnStartLive.Enabled = true;
                UpdateRamEstimate();
            }
            else
            {
                btnStartLive.Enabled = false;
                btnStopLive.Enabled = false;
                btnStartRecording.Enabled = false;
            }

            // Adjust window width
            int requiredWidth = SIDE_MARGIN * 2 + (cameras.Count * (CAMERA_WIDTH + CAMERA_SPACING));
            if (requiredWidth > this.Width)
            {
                this.Width = Math.Min(requiredWidth, 5000);
            }

            UpdateRamEstimate();
            if (cmbPreviewSize != null && cmbLayoutMode != null)
            {
                UpdateCameraLayout();
            }
        }
        private void ToggleCameraTrigger(int cameraIndex)
        {
            if (cameraIndex < 0 || cameraIndex >= cameras.Count)
                return;
            
            var camera = cameras[cameraIndex];
            
            try
            {
                if (!camera.ImagingControl.DeviceValid)
                {
                    MessageBox.Show("Camera is not properly connected.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                
                if (camera.ImagingControl.VCDPropertyItems == null)
                {
                    MessageBox.Show("Camera properties not available.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                
                if (isRecording)
                {
                    MessageBox.Show("Stop the recording before switching the trigger.", "Recording",
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Preferred path: the camera's trigger "Enable" switch. The button and the settings
                // window's Frame Rate Control (External Trigger / Software Controlled) now always agree.
                var enableSw = FindTriggerEnableSwitch(camera.ImagingControl);
                if (enableSw != null && !enableSw.ReadOnly)
                {
                    bool turnOn = !enableSw.Switch;
                    bool wasLive = camera.ImagingControl.LiveVideoRunning;
                    if (wasLive) camera.ImagingControl.LiveStop();
                    camera.Settings.UseExternalTrigger = turnOn;
                    ConfigureCameraTrigger(camera, turnOn);
                    if (wasLive) camera.ImagingControl.LiveStart();
                    settings.CameraSettingsByDevice[camera.DeviceName] = camera.Settings.Clone();
                    settings.Save();
                    LogCameraInfo($"Camera {cameraIndex + 1}: Trigger button -> camera trigger {(turnOn ? "ON (External Trigger)" : "OFF (Software Controlled)")}");
                    return;
                }

                // Fallback (other camera models): try to find and toggle trigger-related properties
                // Based on TIS.Imaging SDK documentation: iterate through Items -> Elements -> Interfaces
                bool triggerToggled = false;
                string[] triggerPropertyNames = { "Trigger", "Trigger On/Off", "TriggerMode", "TriggerSoftware", "Trigger Software", "TriggerEnable" };
                
                try
                {
                    var propertyItems = camera.ImagingControl.VCDPropertyItems;
                    
                    // Iterate through all VCDPropertyItems
                    foreach (VCDPropertyItem propertyItem in propertyItems)
                    {
                        try
                        {
                            string propName = propertyItem.Name ?? "";
                            
                            // Check if this property name matches any trigger property
                            bool isTriggerProperty = false;
                            foreach (string triggerName in triggerPropertyNames)
                            {
                                if (propName.IndexOf(triggerName, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    isTriggerProperty = true;
                                    break;
                                }
                            }
                            
                            if (!isTriggerProperty) continue;
                            
                            // Iterate through all Elements of this property
                            foreach (VCDPropertyElement propertyElement in propertyItem.Elements)
                            {
                                // Iterate through all Interfaces of this element
                                foreach (VCDPropertyInterface propertyInterface in propertyElement)
                                {
                                    try
                                    {
                                        // Check if it's a Switch interface (on/off toggle)
                                        if (propertyInterface.InterfaceGUID == VCDGUIDs.VCDInterface_Switch)
                                        {
                                            VCDSwitchProperty switchProp = (VCDSwitchProperty)propertyInterface;
                                            if (!switchProp.ReadOnly)
                                            {
                                                bool currentValue = switchProp.Switch;
                                                switchProp.Switch = !currentValue;
                                                triggerToggled = true;
                                                LogCameraInfo($"Camera {cameraIndex + 1}: Trigger ({propName}) toggled to {!currentValue}");
                                                break;
                                            }
                                        }
                                        // Check if it's a Button interface (execute/push)
                                        else if (propertyInterface.InterfaceGUID == VCDGUIDs.VCDInterface_Button)
                                        {
                                            VCDButtonProperty buttonProp = (VCDButtonProperty)propertyInterface;
                                            if (!buttonProp.ReadOnly)
                                            {
                                                buttonProp.Push();
                                                triggerToggled = true;
                                                LogCameraInfo($"Camera {cameraIndex + 1}: Trigger ({propName}) executed");
                                                break;
                                            }
                                        }
                                    }
                                    catch (Exception interfaceEx)
                                    {
                                        LogCameraInfo($"Error accessing interface: {interfaceEx.Message}");
                                    }
                                }
                                
                                if (triggerToggled) break;
                            }
                            
                            if (triggerToggled) break;
                        }
                        catch (Exception itemEx)
                        {
                            LogCameraInfo($"Error accessing property item: {itemEx.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogCameraInfo($"Error accessing properties: {ex.Message}");
                }
                
                if (!triggerToggled)
                {
                    // Fallback: Open properties dialog if we can't toggle programmatically
                    camera.ImagingControl.ShowPropertyDialog();
                    LogCameraInfo($"Camera {cameraIndex + 1}: Could not toggle trigger programmatically, opened properties dialog");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error toggling trigger: {ex.Message}\n\n" +
                              "Opening properties dialog instead...",
                              "Error", MessageBoxButtons.OK, MessageBoxIcon.Information);
                try
                {
                    camera.ImagingControl.ShowPropertyDialog();
                }
                catch { }
                LogCameraInfo($"Error toggling trigger for camera {cameraIndex + 1}: {ex.Message}");
            }
        }

        private void ShowCameraSettings(int cameraIndex)
        {
            if (cameraIndex >= cameras.Count)
                return;
            
            var camera = cameras[cameraIndex];
            
            // Check if camera is busy
            if (isRecording)
            {
                MessageBox.Show("Cannot change settings while recording!",
                                "Camera Busy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            // For TIS cameras, validate device is ready
            if (camera.IsImagingSource && !camera.ImagingControl.DeviceValid)
            {
                MessageBox.Show("Camera is not properly connected or initialized.\n\n" +
                              "Possible causes:\n" +
                              "1. TIS.Imaging SDK not installed - Run the SDK installer\n" +
                              "2. Camera driver not installed\n" +
                              "3. Camera disconnected\n\n" +
                              "Please:\n" +
                              "- Install TIS.Imaging SDK from Installers\\TIS_Imaging_SDK_Installer.exe\n" +
                              "- Click 'Refresh Cameras' to reconnect",
                              "Camera Not Ready", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Track if ANY TIS camera is currently live
            wasLiveBeforeSettings = cameras.Any(c => c.IsImagingSource && c.ImagingControl.LiveVideoRunning);
            bool wasLive = camera.IsImagingSource && camera.ImagingControl.LiveVideoRunning;

            // Store current settings to detect what changed
            string oldFormat = camera.Settings.Format;
            bool oldUseExternalTrigger = camera.Settings.UseExternalTrigger;
            float oldSoftwareFrameRate = camera.Settings.SoftwareFrameRate;

            // DON'T stop TIS camera - let it run so Update button works in property dialog

            // For webcams: stop capture BEFORE opening dialog so GetSupportedResolutions()
            // can open the device freely (most webcams only allow one capture at a time)
            bool webcamWasRunning = !camera.IsImagingSource && camera.WebcamCapture != null;
            if (webcamWasRunning)
            {
                camera.WebcamCts?.Cancel();
                camera.WebcamThread?.Join(2000);
                camera.WebcamCapture?.Dispose();
                camera.WebcamCapture = null;
                if (camera.WebcamPreview != null) camera.WebcamPreview.Image = null;
            }

            // Show settings dialog
            string currentRecordingMode = cmbRecordingMode.SelectedItem?.ToString() ?? "";
            using (CameraSettingsDialog dialog = new CameraSettingsDialog(
                camera.IsImagingSource ? camera.ImagingControl : null,
                camera.Settings, cameraIndex + 1, currentRecordingMode,
                camera.IsImagingSource, camera.WebcamDeviceIndex))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    // Check what changed
                    bool formatChanged = !string.IsNullOrEmpty(dialog.Settings.Format) && 
                                        dialog.Settings.Format != oldFormat;
                    bool triggerModeChanged = dialog.Settings.UseExternalTrigger != oldUseExternalTrigger;
                    bool fpsChanged = dialog.Settings.SoftwareFrameRate != oldSoftwareFrameRate;
                    
                    // Webcams: resolution update is handled inline; no full DetectAndSetupCameras needed
                    bool needsRefresh = camera.IsImagingSource && (formatChanged || triggerModeChanged || fpsChanged);
                    
                    // Save settings for current camera
                    camera.Settings = dialog.Settings;
                    settings.CameraSettingsByDevice[camera.DeviceName] = dialog.Settings.Clone();

                    // For webcams, update the resolution tuple from the format string
                    if (!camera.IsImagingSource && !string.IsNullOrEmpty(dialog.Settings.Format))
                    {
                        var parts = dialog.Settings.Format.Split('x');
                        if (parts.Length == 2 && int.TryParse(parts[0], out int rw) && int.TryParse(parts[1], out int rh))
                            camera.WebcamResolution = (rw, rh);
                    }
                    
                    // If "Save for All Cameras" was clicked, apply to cameras of the SAME type only
                    if (dialog.ApplyToAllCameras)
                    {
                        int appliedCount = 0;
                        for (int i = 0; i < cameras.Count; i++)
                        {
                            if (i == cameraIndex) continue; // Skip the current camera
                            var otherCamera = cameras[i];
                            if (otherCamera.IsImagingSource != camera.IsImagingSource) continue; // Different type
                            try
                            {
                                var clonedSettings = dialog.Settings.Clone();
                                clonedSettings.DeviceName = otherCamera.DeviceName;
                                clonedSettings.IsImagingSource = otherCamera.IsImagingSource;
                                otherCamera.Settings = clonedSettings;
                                settings.CameraSettingsByDevice[otherCamera.DeviceName] = clonedSettings.Clone();

                                if (otherCamera.IsImagingSource && otherCamera.ImagingControl.DeviceValid)
                                {
                                    try
                                    {
                                        if (!string.IsNullOrEmpty(clonedSettings.VCDPropertiesXml) &&
                                            otherCamera.ImagingControl.VCDPropertyItems != null)
                                            otherCamera.ImagingControl.VCDPropertyItems.Load(clonedSettings.VCDPropertiesXml);
                                    }
                                    catch (Exception ex)
                                    {
                                        LogCameraInfo($"Warning: Could not apply properties to camera {i + 1}: {ex.Message}");
                                    }
                                }
                                else if (!otherCamera.IsImagingSource && !string.IsNullOrEmpty(clonedSettings.Format))
                                {
                                    var parts = clonedSettings.Format.Split('x');
                                    if (parts.Length == 2 && int.TryParse(parts[0], out int rw) && int.TryParse(parts[1], out int rh))
                                        otherCamera.WebcamResolution = (rw, rh);
                                }

                                appliedCount++;
                            }
                            catch (Exception ex)
                            {
                                LogCameraInfo($"Error applying settings to camera {i + 1}: {ex.Message}");
                            }
                        }
                        LogCameraInfo($"Applied settings from camera {cameraIndex + 1} to {appliedCount} other camera(s)");
                    }
                    
                    settings.Save();

                    if (needsRefresh)
                    {
                        // Format, trigger mode, or FPS changed - need full refresh
                        LogCameraInfo($"Camera {cameraIndex + 1}: Settings changed (Format: {formatChanged}, Trigger: {triggerModeChanged}, FPS: {fpsChanged}) - refreshing cameras");
                        
                        // Refresh all cameras to apply settings properly (suppress the detection message)
                        DetectAndSetupCameras(suppressMessage: true);

                        // If cameras were live before, restart them automatically
                        if (wasLiveBeforeSettings)
                        {
                            try
                            {
                                int successCount = 0;
                                for (int i = 0; i < cameras.Count; i++)
                                {
                                    try
                                    {
                                        if (cameras[i].IsImagingSource)
                                        {
                                            cameras[i].ImagingControl.LiveStart();
                                            cameras[i].FrameCount = 0;
                                        }
                                        else
                                        {
                                            // Webcam: restart capture thread with possibly new resolution
                                            cameras[i].WebcamCts?.Cancel();
                                            cameras[i].WebcamThread?.Join(1000);
                                            cameras[i].WebcamCapture?.Dispose();
                                            cameras[i].WebcamCts = new CancellationTokenSource();
                                            cameras[i].WebcamCapture = new VideoCapture(cameras[i].WebcamDeviceIndex, (VideoCaptureAPIs)700);
                                            cameras[i].WebcamCapture.Set(VideoCaptureProperties.FrameWidth, cameras[i].WebcamResolution.Width);
                                            cameras[i].WebcamCapture.Set(VideoCaptureProperties.FrameHeight, cameras[i].WebcamResolution.Height);
                                            cameras[i].WebcamCapture.Set(VideoCaptureProperties.Fps, cameras[i].Settings.SoftwareFrameRate);
                                            var wc = cameras[i];
                                            cameras[i].WebcamThread = new Thread(() => RunWebcamCaptureLoop(wc));
                                            cameras[i].WebcamThread.IsBackground = true;
                                            cameras[i].WebcamThread.Start();
                                        }
                                        cameras[i].LastFpsUpdate = DateTime.Now;
                                        successCount++;
                                    }
                                    catch { }
                                }

                                btnStartLive.Enabled = false;
                                btnStopLive.Enabled = true;
                                btnStartRecording.Enabled = true;
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        // Only camera properties changed (exposure, gain, etc.)
                        // SDK already applied them via Update/OK button - no refresh needed!
                        LogCameraInfo($"Camera {cameraIndex + 1}: Only camera properties changed - no refresh needed");

                        // Update device name label to show device name only
                        camera.NameLabel.Text = camera.DeviceName;
                    }
                }

                // Restart webcam capture (always, whether OK or Cancel — we stopped it above)
                if (webcamWasRunning)
                {
                    try
                    {
                        camera.WebcamCts = new CancellationTokenSource();
                        camera.WebcamCapture = new VideoCapture(camera.WebcamDeviceIndex, (VideoCaptureAPIs)700);
                        camera.WebcamCapture.Set(VideoCaptureProperties.FrameWidth, camera.WebcamResolution.Width);
                        camera.WebcamCapture.Set(VideoCaptureProperties.FrameHeight, camera.WebcamResolution.Height);
                        camera.WebcamCapture.Set(VideoCaptureProperties.Fps, camera.Settings.SoftwareFrameRate);
                        camera.LastFpsUpdate = DateTime.Now;
                        camera.WebcamFrameCount = 0;
                        var wc = camera;
                        camera.WebcamThread = new Thread(() => RunWebcamCaptureLoop(wc));
                        camera.WebcamThread.IsBackground = true;
                        camera.WebcamThread.Start();
                    }
                    catch (Exception ex)
                    {
                        LogCameraInfo($"Webcam restart after settings error: {ex.Message}");
                    }
                }
            }
        }

        private void ApplyCameraSettings(CameraControl camera, CameraSettings settings)
        {
            try
            {
                // Apply VCD properties from saved XML string
                if (!string.IsNullOrEmpty(settings.VCDPropertiesXml) && camera.ImagingControl.VCDPropertyItems != null)
                {
                    try
                    {
                        camera.ImagingControl.VCDPropertyItems.Load(settings.VCDPropertiesXml);
                        LogCameraInfo($"Loaded VCD properties for {camera.DeviceName} - frame-rate list max now {MaxRate(camera.ImagingControl):F1} fps");
                    }
                    catch (Exception ex)
                    {
                        LogCameraInfo($"Could not load VCD properties: {ex.Message}");
                    }
                }
                
                // Apply saved software frame rate if available (only if NOT using external trigger)
                try
                {
                    if (!settings.UseExternalTrigger)
                    {
                        // Software-controlled frame rate
                        if (camera.ImagingControl.DeviceFrameRateAvailable)
                        {
                            // Check if the saved rate is valid
                            if (camera.ImagingControl.DeviceFrameRates != null && 
                                camera.ImagingControl.DeviceFrameRates.Length > 0)
                            {
                                float[] validRates = camera.ImagingControl.DeviceFrameRates;
                                float requestedRate = settings.SoftwareFrameRate;
                                
                                // Find closest valid rate (nearest neighbor)
                                float closestRate;
                                var lowerRates = validRates.Where(r => r <= requestedRate).ToArray();
                                var higherRates = validRates.Where(r => r > requestedRate).ToArray();

                                if (lowerRates.Length == 0)
                                {
                                    // All rates are higher, use minimum
                                    closestRate = validRates.Min();
                                }
                                else if (higherRates.Length == 0)
                                {
                                    // All rates are lower, use maximum
                                    closestRate = validRates.Max();
                                }
                                else
                                {
                                    // Find closest from both sides
                                    float lower = lowerRates.Max();
                                    float higher = higherRates.Min();
                                    
                                    // Pick whichever is closer
                                    closestRate = (requestedRate - lower) < (higher - requestedRate) ? lower : higher;
                                }
                                
                                camera.ImagingControl.DeviceFrameRate = closestRate;
                                LogCameraInfo($"Camera {camera.DeviceName}: Set SOFTWARE frame rate to {closestRate:F1} fps");
                            }
                            else
                            {
                                // Try to set it anyway
                                camera.ImagingControl.DeviceFrameRate = settings.SoftwareFrameRate;
                                LogCameraInfo($"Camera {camera.DeviceName}: Attempted to set SOFTWARE frame rate to {settings.SoftwareFrameRate:F1} fps");
                            }
                        }
                        else
                        {
                            LogCameraInfo($"Camera {camera.DeviceName}: Frame rate control not supported");
                        }

                        // "Software Controlled" selected -> camera trigger must be OFF (free-running)
                        try
                        {
                            var sw = FindTriggerEnableSwitch(camera.ImagingControl);
                            if (sw != null && sw.Switch && !sw.ReadOnly)
                            {
                                sw.Switch = false;
                                LogCameraInfo($"Camera {camera.DeviceName}: camera trigger switched OFF (software-controlled frame rate)");
                            }
                            UpdateTriggerButton(camera);
                        }
                        catch (Exception swEx)
                        {
                            LogCameraInfo($"Camera {camera.DeviceName}: could not switch camera trigger off: {swEx.Message}");
                        }
                    }
                    else
                    {
                        // External trigger: arm the camera's trigger and make sure its internal frame
                        // rate is as high as possible (otherwise triggers above that rate are skipped).
                        ConfigureCameraTrigger(camera, true);
                    }
                }
                catch (Exception ex)
                {
                    LogCameraInfo($"Camera {camera.DeviceName}: Failed to set frame rate - {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                LogCameraInfo($"Error applying camera settings: {ex.Message}");
            }
        }
        private string GetCurrentFormat(ICImagingControl control)
        {
            try
            {
                if (control.VideoFormatCurrent != null)
                {
                    return control.VideoFormatCurrent.ToString();
                }
                return "Unknown";
            }
            catch
            {
                return "Error";
            }
        }
        
        private void FpsTimer_Tick(object? sender, EventArgs e)
        {
            foreach (var camera in cameras)
            {
                if (!camera.IsImagingSource)
                {
                    // Webcam FPS display
                    DateTime now = DateTime.Now;
                    double elapsedSeconds = (now - camera.LastFpsUpdate).TotalSeconds;
                    if (elapsedSeconds > 0)
                    {
                        long frames = camera.WebcamFrameCount;
                        camera.WebcamFrameCount = 0;
                        double rawFps = frames / elapsedSeconds;
                        camera.FpsHistory[camera.FpsHistoryIndex % 3] = rawFps;
                        camera.FpsHistoryIndex++;
                        double fps = camera.FpsHistory.Average();

                        if (IsCameraRecording(camera))
                        {
                            if (camera.IsTimelapseMode)
                            {
                                int nextSec = 0;
                                if (camera.LastTimelapseCapture != null)
                                {
                                    double elapsed = (now - camera.LastTimelapseCapture.Value).TotalSeconds;
                                    nextSec = Math.Max(0, (int)(camera.TimelapseIntervalSeconds - elapsed));
                                }
                                camera.FpsLabel.Text = $"⏱️ TIMELAPSE | {camera.TimelapseFrameCount} frames | Next in {nextSec}s";
                                camera.FpsLabel.ForeColor = System.Drawing.Color.Purple;
                            }
                            else
                            {
                                double recDuration = (now - camera.RecordingStartTime.Value).TotalSeconds;
                                if ((now - camera.FileSizeLastChecked).TotalSeconds >= 1.0)
                                {
                                    if (camera.RecordingFilePath != null && File.Exists(camera.RecordingFilePath))
                                    {
                                        try { camera.CachedFileSizeMB = new FileInfo(camera.RecordingFilePath).Length / (1024 * 1024); }
                                        catch { }
                                    }
                                    camera.FileSizeLastChecked = now;
                                }
                                camera.FpsLabel.Text = $"🔴 REC {fps:F1}fps | {camera.CachedFileSizeMB}MB | {recDuration:F1}s";
                                camera.FpsLabel.ForeColor = System.Drawing.Color.Red;
                            }
                        }
                        else
                        {
                            bool isRunning = camera.WebcamCapture != null && camera.WebcamCapture.IsOpened();
                            if (isRunning)
                            {
                                camera.FpsLabel.Text = $"LIVE | {fps:F1} fps";
                                camera.FpsLabel.ForeColor = System.Drawing.Color.DarkBlue;
                            }
                            else
                            {
                                camera.FpsLabel.Text = "Ready";
                                camera.FpsLabel.ForeColor = System.Drawing.Color.DarkBlue;
                            }
                        }
                        camera.LastFpsUpdate = now;
                    }
                    continue;
                }

                // TIS camera FPS display
                bool isConnected = false;
                try { isConnected = camera.ImagingControl.DeviceValid; }
                catch { }

                if (!isConnected && !camera.NameLabel.Text.Contains("[DISCONNECTED]"))
                {
                    camera.NameLabel.Text = $"{camera.DeviceName} [DISCONNECTED]";
                    camera.NameLabel.ForeColor = System.Drawing.Color.Red;
                }
                else if (isConnected && camera.NameLabel.Text.Contains("[DISCONNECTED]"))
                {
                    camera.NameLabel.Text = camera.DeviceName;
                    camera.NameLabel.ForeColor = System.Drawing.Color.Black;
                }
                else if (isConnected && camera.NameLabel.ForeColor == System.Drawing.Color.Red)
                {
                    camera.NameLabel.ForeColor = System.Drawing.Color.Black;
                }

                DateTime tisnow = DateTime.Now;
                double tisElapsed = (tisnow - camera.LastFpsUpdate).TotalSeconds;

                if (tisElapsed > 0)
                {
                    double fps = camera.FrameCount / tisElapsed;

                    if (IsCameraRecording(camera))
                    {
                        if (camera.IsTimelapseMode)
                        {
                            int nextCaptureSeconds = 0;
                            if (camera.LastTimelapseCapture != null)
                            {
                                double elapsed = (tisnow - camera.LastTimelapseCapture.Value).TotalSeconds;
                                nextCaptureSeconds = (int)(camera.TimelapseIntervalSeconds - elapsed);
                                if (nextCaptureSeconds < 0) nextCaptureSeconds = 0;
                            }
                            camera.FpsLabel.Text = $"⏱️ TIMELAPSE | {camera.TimelapseFrameCount} frames | Next in {nextCaptureSeconds}s";
                            camera.FpsLabel.ForeColor = System.Drawing.Color.Purple;
                        }
                        else if (camera.QueuedRecorder != null && camera.QueuedRecorder.IsLoop)
                        {
                            int bufferFrames = camera.QueuedRecorder.QueueDepth;

                            double expectedFps = camera.Settings.UseExternalTrigger
                                ? (double)numExternalTriggerFps.Value
                                : camera.Settings.SoftwareFrameRate;

                            double bufferSeconds = bufferFrames / expectedFps;
                            int loopDuration = (int)numLoopDuration.Value;
                            camera.FpsLabel.Text = $"🔴 LOOP {loopDuration}s | {bufferFrames} frames ({bufferSeconds:F1}s)";
                            camera.FpsLabel.ForeColor = System.Drawing.Color.Orange;
                        }
                        else
                        {
                            double recordingDuration = (tisnow - camera.RecordingStartTime.Value).TotalSeconds;
                            if ((tisnow - camera.FileSizeLastChecked).TotalSeconds >= 1.0)
                            {
                                if (camera.RecordingFilePath != null && File.Exists(camera.RecordingFilePath))
                                {
                                    try { camera.CachedFileSizeMB = new FileInfo(camera.RecordingFilePath).Length / (1024 * 1024); }
                                    catch { }
                                }
                                camera.FileSizeLastChecked = tisnow;
                            }
                            camera.FpsLabel.Text = $"🔴 REC | {camera.CachedFileSizeMB}MB | {recordingDuration:F1}s";
                            camera.FpsLabel.ForeColor = System.Drawing.Color.Red;
                        }
                    }
                    else
                    {
                        if (camera.Settings.UseExternalTrigger)
                            camera.FpsLabel.Text = "LIVE | External trigger";
                        else
                            camera.FpsLabel.Text = $"LIVE | {camera.Settings.SoftwareFrameRate:F1} fps (software set)";
                        camera.FpsLabel.ForeColor = System.Drawing.Color.Green;
                    }

                    camera.FrameCount = 0;
                    camera.LastFpsUpdate = tisnow;
                }
            }
        }
        
        private bool IsCameraRecording(CameraControl cam)
        {
            if (!cam.RecordingStartTime.HasValue) return false;
            string key = string.IsNullOrEmpty(cam.GroupId) ? "" : cam.GroupId;
            return _groupRecording.TryGetValue(key, out bool v) && v;
        }

        private void UpdateCameraLayout()
        {
            if (cameras.Count == 0)
                return;
            
            // Check if a camera is expanded
            if (expandedCameraIndex >= 0 && expandedCameraIndex < cameras.Count)
            {
                // EXPANDED VIEW - Show only one camera large
                UpdateExpandedLayout();
            }
            else
            {
                // GRID VIEW - Show all cameras
                UpdateGridLayout();
            }
        }

        private void UpdateExpandedLayout()
        {
            if (_inUpdateExpandedLayout) return;
            _inUpdateExpandedLayout = true;
            try { UpdateExpandedLayoutCore(); } finally { _inUpdateExpandedLayout = false; }
        }

        private void UpdateExpandedLayoutCore()
        {
            var expandedCamera = cameras[expandedCameraIndex];
            
            // Hide all other cameras
            for (int i = 0; i < cameras.Count; i++)
            {
                if (i != expandedCameraIndex)
                {
                    cameras[i].NameTextBox.Visible = false;
                    cameras[i].NameLabel.Visible = false;
                    cameras[i].FpsLabel.Visible = false;
                    cameras[i].SettingsButton.Visible = false;
                    cameras[i].TriggerButton.Visible = false;
                    if (cameras[i].IsImagingSource)
                        cameras[i].ImagingControl.Visible = false;
                    if (!cameras[i].IsImagingSource && cameras[i].WebcamPreview != null)
                        cameras[i].WebcamPreview.Visible = false;
                    if (cameras[i].GroupIndicatorLabel != null)
                        cameras[i].GroupIndicatorLabel.Visible = false;
                }
            }

            // Calculate available space for expanded preview
            int availableWidth = this.ClientSize.Width - (SIDE_MARGIN * 2);
            int availableHeight = this.ClientSize.Height - TOP_MARGIN;

            // Use actual camera aspect ratio if available, otherwise 4:3
            float aspectRatio = 4.0f / 3.0f; // Default 4:3

            // For webcams, derive aspect ratio from the captured resolution
            if (!expandedCamera.IsImagingSource)
            {
                var res = expandedCamera.WebcamResolution;
                if (res.Width > 0 && res.Height > 0)
                    aspectRatio = (float)res.Width / res.Height;
            }
            else
            try
            {
                string? formatStr = null;
                
                // First try VideoFormatCurrent (if camera is running)
                if (expandedCamera.ImagingControl.VideoFormatCurrent != null)
                {
                    formatStr = expandedCamera.ImagingControl.VideoFormatCurrent.ToString();
                }
                // Fallback: Check VideoFormat (the format that's been set)
                else if (expandedCamera.ImagingControl.VideoFormat != null)
                {
                    formatStr = expandedCamera.ImagingControl.VideoFormat.ToString();
                }
                // Also check saved settings
                else if (expandedCamera.Settings != null && !string.IsNullOrEmpty(expandedCamera.Settings.Format))
                {
                    formatStr = expandedCamera.Settings.Format;
                }
                
                // Parse resolution from format string
                if (!string.IsNullOrEmpty(formatStr))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(formatStr, @"(\d+)x(\d+)");
                    if (match.Success)
                    {
                        int camWidth = int.Parse(match.Groups[1].Value);
                        int camHeight = int.Parse(match.Groups[2].Value);
                        aspectRatio = (float)camWidth / camHeight;
                    }
                }
            }
            catch { }

            int expandedWidth, expandedHeight;

            // Space needed above the preview (name, device label, fps label, etc.)
            int controlsHeight = 75;

            // Calculate maximum size that fits while maintaining aspect ratio
            int maxHeight = availableHeight - controlsHeight - 10; // 10px bottom margin
            int maxWidth = availableWidth - 10;   // Minimal side margins

            if (maxWidth <= 0 || maxHeight <= 0)
                return; // Window too small to render anything meaningful

            if ((float)maxWidth / maxHeight > aspectRatio)
            {
                // Height is limiting factor - use full height
                expandedHeight = maxHeight;
                expandedWidth = (int)(expandedHeight * aspectRatio);
            }
            else
            {
                // Width is limiting factor - use full width
                expandedWidth = maxWidth;
                expandedHeight = (int)(expandedWidth / aspectRatio);
            }

            // Clamp to at least 1px to avoid ArgumentException on Size assignment
            expandedWidth = Math.Max(1, expandedWidth);
            expandedHeight = Math.Max(1, expandedHeight);

            // Center horizontally
            int xPosition = SIDE_MARGIN + (availableWidth - expandedWidth) / 2;
            int yPosition = TOP_MARGIN;
            
            // Position expanded camera controls
            expandedCamera.NameTextBox.Location = new System.Drawing.Point(xPosition, yPosition);
            expandedCamera.NameTextBox.Size = new System.Drawing.Size(Math.Min(expandedWidth, 400), 25);
            expandedCamera.NameTextBox.Visible = true;
            
            expandedCamera.NameLabel.Location = new System.Drawing.Point(xPosition, yPosition + 28);
            expandedCamera.NameLabel.Size = new System.Drawing.Size(expandedWidth, 18);
            expandedCamera.NameLabel.Visible = true;
            
            expandedCamera.SettingsButton.Location = new System.Drawing.Point(xPosition + expandedWidth - 80, yPosition + 48);
            expandedCamera.SettingsButton.Visible = true;
            expandedCamera.SettingsButton.BringToFront();
            
            expandedCamera.TriggerButton.Location = new System.Drawing.Point(xPosition + expandedWidth - 160, yPosition + 48);
            expandedCamera.TriggerButton.Visible = true;
            expandedCamera.TriggerButton.BringToFront();
            
            expandedCamera.FpsLabel.Location = new System.Drawing.Point(xPosition, yPosition + 50);
            expandedCamera.FpsLabel.Size = new System.Drawing.Size(expandedWidth - 90, 20);
            expandedCamera.FpsLabel.Visible = true;

            // Group indicator label
            if (expandedCamera.GroupIndicatorLabel == null)
            {
                expandedCamera.GroupIndicatorLabel = new Label
                {
                    Size = new System.Drawing.Size(14, 14),
                    BorderStyle = BorderStyle.FixedSingle,
                };
                this.Controls.Add(expandedCamera.GroupIndicatorLabel);
            }
            expandedCamera.GroupIndicatorLabel.Location = new System.Drawing.Point(
                xPosition + Math.Min(expandedWidth, 400) - 20, yPosition + 4);
            expandedCamera.GroupIndicatorLabel.BackColor = string.IsNullOrEmpty(expandedCamera.GroupId)
                ? System.Drawing.Color.Gainsboro
                : GroupColors.GetValueOrDefault(expandedCamera.GroupId, System.Drawing.Color.Gray);
            expandedCamera.GroupIndicatorLabel.Visible = true;
            expandedCamera.GroupIndicatorLabel.BringToFront();

            // Suspend form layout to prevent intermediate redraws
            this.SuspendLayout();

            if (!expandedCamera.IsImagingSource && expandedCamera.WebcamPreview != null)
            {
                // Zoom mode shows the entire frame at correct aspect ratio (with letterboxing if
                // the PictureBox aspect differs from the frame); StretchImage would distort.
                expandedCamera.WebcamPreview.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
                expandedCamera.WebcamPreview.Location = new System.Drawing.Point(xPosition, yPosition + 75);
                expandedCamera.WebcamPreview.Size = new System.Drawing.Size(expandedWidth, expandedHeight);
                expandedCamera.WebcamPreview.Visible = true;
                expandedCamera.WebcamPreview.BringToFront();
            }
            else
            {
                // Set LiveDisplay scaling BEFORE resizing the control so the SDK never
                // clips a frame at the old (smaller) scale during the transition.
                expandedCamera.ImagingControl.LiveDisplayDefault = false;
                expandedCamera.ImagingControl.LiveDisplayWidth = expandedWidth;
                expandedCamera.ImagingControl.LiveDisplayHeight = expandedHeight;
                expandedCamera.ImagingControl.LiveDisplay = true;

                expandedCamera.ImagingControl.Location = new System.Drawing.Point(xPosition, yPosition + 75);
                expandedCamera.ImagingControl.Size = new System.Drawing.Size(expandedWidth, expandedHeight);
                expandedCamera.ImagingControl.Visible = true;
                expandedCamera.ImagingControl.BringToFront();
            }
            
            // Resume form layout
            this.ResumeLayout(false);
            
            // Force the control to refresh and update its display
            if (!expandedCamera.IsImagingSource && expandedCamera.WebcamPreview != null)
            {
                expandedCamera.WebcamPreview.Invalidate();
                expandedCamera.WebcamPreview.Update();
            }
            else
            {
                expandedCamera.ImagingControl.Invalidate(true);
                expandedCamera.ImagingControl.Refresh();
                expandedCamera.ImagingControl.Update();
            }
            
            // Force a layout update to ensure proper rendering
            this.PerformLayout();
            this.Invalidate(true);
            this.Update();

            // Give the TIS control time to process the resize, especially for Y800 format.
            // Not needed for webcams (PictureBox stretches immediately), and skipping DoEvents
            // on webcam paths avoids re-entrant layout calls from pending Windows messages.
            if (expandedCamera.IsImagingSource)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(100);
                Application.DoEvents();
            }
            
            // Add "Back to Grid" label if not already present
            Label? backLabel = this.Controls.OfType<Label>().FirstOrDefault(l => l.Tag?.ToString() == "backToGrid");
            if (backLabel == null)
            {
                backLabel = new Label
                {
                    Text = "← Back to Grid (Esc)",
                    Location = new System.Drawing.Point(SIDE_MARGIN, TOP_MARGIN + 2),
                    Size = new System.Drawing.Size(250, 20),  // Reduced width
                    Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold),
                    ForeColor = System.Drawing.Color.Blue,
                    Cursor = Cursors.Hand,
                    Tag = "backToGrid",
                    BackColor = System.Drawing.Color.Transparent  // Make background transparent
                };
                backLabel.Click += (s, e) => CollapseExpandedCamera();
                this.Controls.Add(backLabel);
            }
            backLabel.Visible = true;
        }

        private void UpdateGridLayout()
        {
            // Hide the "Back to Grid" label
            Label? backLabel = this.Controls.OfType<Label>().FirstOrDefault(l => l.Tag?.ToString() == "backToGrid");
            if (backLabel != null)
            {
                backLabel.Visible = false;
            }
            
            // Get selected preview size
            if (cmbPreviewSize != null)
            {
                switch (cmbPreviewSize.SelectedIndex)
                {
                    case 0: // Small
                        currentPreviewWidth = 240;
                        currentPreviewHeight = 180;
                        break;
                    case 1: // Medium
                        currentPreviewWidth = 320;
                        currentPreviewHeight = 240;
                        break;
                    case 2: // Large
                        currentPreviewWidth = 400;
                        currentPreviewHeight = 300;
                        break;
                    case 3: // XLarge
                        currentPreviewWidth = 480;
                        currentPreviewHeight = 360;
                        break;
                    default:
                        currentPreviewWidth = 320;
                        currentPreviewHeight = 240;
                        break;
                }
            }
            
            // Calculate grid layout
            int camerasPerRow;
            int numRows;
            
            if (cmbLayoutMode == null || cmbLayoutMode.SelectedIndex == 0) // Auto Grid
            {
                // Calculate optimal grid based on available width
                int availableWidth = this.ClientSize.Width - (SIDE_MARGIN * 2);
                camerasPerRow = Math.Max(1, availableWidth / (currentPreviewWidth + CAMERA_SPACING));
                camerasPerRow = Math.Min(camerasPerRow, cameras.Count); // Don't exceed camera count
                numRows = (int)Math.Ceiling((double)cameras.Count / camerasPerRow);
            }
            else // Manual row count
            {
                numRows = cmbLayoutMode.SelectedIndex; // 1, 2, 3, or 4 rows
                camerasPerRow = (int)Math.Ceiling((double)cameras.Count / numRows);
            }
            
            // Reposition all camera controls and make them visible
            for (int i = 0; i < cameras.Count; i++)
            {
                var camera = cameras[i];
                
                int col = i % camerasPerRow;
                int row = i / camerasPerRow;
                
                int xPosition = SIDE_MARGIN + (col * (currentPreviewWidth + CAMERA_SPACING));
                int yPosition = TOP_MARGIN + (row * (currentPreviewHeight + 90)); // 90 = space for labels and controls
                
                // Update positions and make visible
                camera.NameTextBox.Location = new System.Drawing.Point(xPosition, yPosition);
                camera.NameTextBox.Size = new System.Drawing.Size(currentPreviewWidth - 10, 20);
                camera.NameTextBox.Visible = true;
                
                camera.NameLabel.Location = new System.Drawing.Point(xPosition, yPosition + 22);
                camera.NameLabel.Size = new System.Drawing.Size(currentPreviewWidth, 18);
                camera.NameLabel.Visible = true;
                
                camera.SettingsButton.Location = new System.Drawing.Point(xPosition + currentPreviewWidth - 80, yPosition + 42);
                camera.SettingsButton.Visible = true;
                camera.SettingsButton.BringToFront();
                
                camera.TriggerButton.Location = new System.Drawing.Point(xPosition + currentPreviewWidth - 160, yPosition + 42);
                camera.TriggerButton.Visible = true;
                camera.TriggerButton.BringToFront();
                
                camera.FpsLabel.Location = new System.Drawing.Point(xPosition, yPosition + 45);
                camera.FpsLabel.Size = new System.Drawing.Size(currentPreviewWidth, 20);
                camera.FpsLabel.Visible = true;

                // Group indicator label (small colored square)
                if (camera.GroupIndicatorLabel == null)
                {
                    camera.GroupIndicatorLabel = new Label
                    {
                        Size = new System.Drawing.Size(14, 14),
                        BorderStyle = BorderStyle.FixedSingle,
                    };
                    this.Controls.Add(camera.GroupIndicatorLabel);
                }
                camera.GroupIndicatorLabel.Location = new System.Drawing.Point(
                    xPosition + currentPreviewWidth - 20, yPosition + 4);
                camera.GroupIndicatorLabel.BackColor = string.IsNullOrEmpty(camera.GroupId)
                    ? System.Drawing.Color.Gainsboro
                    : GroupColors.GetValueOrDefault(camera.GroupId, System.Drawing.Color.Gray);
                camera.GroupIndicatorLabel.Visible = true;
                camera.GroupIndicatorLabel.BringToFront();

                if (!camera.IsImagingSource && camera.WebcamPreview != null)
                {
                    camera.WebcamPreview.Location = new System.Drawing.Point(xPosition, yPosition + 68);
                    camera.WebcamPreview.Size = new System.Drawing.Size(currentPreviewWidth, currentPreviewHeight);
                    camera.WebcamPreview.Visible = true;
                }
                else
                {
                    // Set LiveDisplay scaling BEFORE resizing the control so the SDK never
                    // renders a frame at the wrong size and clips it.
                    camera.ImagingControl.LiveDisplayDefault = false;
                    camera.ImagingControl.LiveDisplayWidth = currentPreviewWidth;
                    camera.ImagingControl.LiveDisplayHeight = currentPreviewHeight;

                    camera.ImagingControl.Location = new System.Drawing.Point(xPosition, yPosition + 68);
                    camera.ImagingControl.Size = new System.Drawing.Size(currentPreviewWidth, currentPreviewHeight);
                    camera.ImagingControl.Visible = true;

                    // Force a repaint so the new scale takes effect on the already-visible frame.
                    if (camera.ImagingControl.LiveVideoRunning)
                    {
                        camera.ImagingControl.Invalidate();
                        camera.ImagingControl.Refresh();
                    }
                }
            }
            
            // Adjust form size to fit all cameras
            int requiredWidth = SIDE_MARGIN * 2 + (camerasPerRow * (currentPreviewWidth + CAMERA_SPACING));
            int requiredHeight = TOP_MARGIN + (numRows * (currentPreviewHeight + 90)) + 50;
            
            // Set minimum size (allow wider than screen for horizontal scrolling)
            // Cap height to screen to prevent vertical overflow, but allow width to exceed screen
            this.MinimumSize = new System.Drawing.Size(
                Math.Min(requiredWidth, 5000), // Allow up to 5000px width for scrolling
                Math.Min(requiredHeight, Screen.PrimaryScreen.WorkingArea.Height)
            );
            
            // Resize form if needed
            if (this.Width < requiredWidth)
                this.Width = Math.Min(requiredWidth, 5000); // Allow wider than screen for scrolling
            if (this.Height < requiredHeight)
                this.Height = Math.Min(requiredHeight, Screen.PrimaryScreen.WorkingArea.Height);
            
            // Update RAM estimate
            UpdateRamEstimate();
        }

        private void BtnBrowseWorkingFolder_Click(object? sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select Working Folder for Recordings";
                dialog.SelectedPath = txtWorkingFolder.Text;
                dialog.ShowNewFolderButton = true;
                
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    string selectedPath = dialog.SelectedPath;
                    
                    // Check if folder is writable
                    try
                    {
                        string testFile = Path.Combine(selectedPath, $"test_write_{Guid.NewGuid()}.tmp");
                        File.WriteAllText(testFile, "test");
                        File.Delete(testFile);
                    }
                    catch
                    {
                        MessageBox.Show($"Cannot write to this folder:\n{selectedPath}\n\nPlease choose a different location.",
                                        "Folder Not Writable", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    
                    // Check for OneDrive
                    if (IsOneDrivePath(selectedPath))
                    {
                        var result = MessageBox.Show(
                            "⚠️ WARNING: This appears to be a OneDrive folder!\n\n" +
                            $"Path: {selectedPath}\n\n" +
                            "OneDrive sync can interfere with recording.\n\n" +
                            "Are you sure you want to use this folder?",
                            "OneDrive Detected",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning);
                        
                        if (result == DialogResult.No)
                            return;
                    }
                    
                    txtWorkingFolder.Text = selectedPath;
                    settings.WorkingFolder = selectedPath;
                    settings.Save();
                    
                    // Update disk space display
                    CheckDiskSpace();
                }
            }
        }

        private void BtnRefreshCameras_Click(object? sender, EventArgs e)
        {
            DetectAndSetupCameras();
        }

        private void BtnStartLive_Click(object? sender, EventArgs e)
        {
            try
            {
                int successCount = 0;
                List<string> errors = new List<string>();

                for (int i = 0; i < cameras.Count; i++)
                {
                    var camera = cameras[i];
                    try
                    {
                        if (camera.IsImagingSource)
                        {
                            // Always set up a FrameHandlerSink for live preview to enable screenshots
                            // Store the original sink (might be null) before setting up preview sink
                            camera.OriginalSink = camera.ImagingControl.Sink;

                            // Create a FrameHandlerSink for live preview (same as timelapse)
                            var previewSink = new FrameHandlerSink();
                            previewSink.SnapMode = false; // Grab mode (continuous)
                            camera.ImagingControl.Sink = previewSink;

                            // Now start live
                            camera.ImagingControl.LiveStart();
                            camera.LastFpsUpdate = DateTime.Now;
                            camera.FrameCount = 0;
                        }
                        else
                        {
                            // Webcam path — OpenCV capture + background thread
                            camera.WebcamCts = new CancellationTokenSource();
                            camera.WebcamCapture = new VideoCapture(camera.WebcamDeviceIndex, (VideoCaptureAPIs)700);
                            camera.WebcamCapture.Set(VideoCaptureProperties.FrameWidth, camera.WebcamResolution.Width);
                            camera.WebcamCapture.Set(VideoCaptureProperties.FrameHeight, camera.WebcamResolution.Height);
                            camera.WebcamCapture.Set(VideoCaptureProperties.Fps, camera.Settings.SoftwareFrameRate);
                            camera.LastFpsUpdate = DateTime.Now;
                            camera.WebcamFrameCount = 0;
                            camera.WebcamThread = new Thread(() => RunWebcamCaptureLoop(camera));
                            camera.WebcamThread.IsBackground = true;
                            camera.WebcamThread.Start();
                        }
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Camera {i + 1}: {ex.Message}");
                    }
                }

                btnStartLive.Enabled = false;
                btnStopLive.Enabled = true;
                btnScreenshot.Enabled = true;
                btnStartRecording.Enabled = true;

                // Re-apply display settings now that cameras are live.  The TIS SDK may
                // reset LiveDisplayDefault to true on LiveStart(), which would cause the
                // native-resolution image to be clipped instead of scaled to fit the control.
                UpdateCameraLayout();

                // Only show popup if there were errors
                if (errors.Count > 0)
                {
                    MessageBox.Show($"Started {successCount}/{cameras.Count} cameras.\n\nErrors:\n{string.Join("\n", errors)}",
                                    "Partial Success", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error starting cameras: {ex.Message}",
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnStopLive_Click(object? sender, EventArgs e)
        {
            try
            {
                if (isRecording)
                {
                    StopRecording(null); // null = stop all cameras (Stop Live)
                }

                foreach (var camera in cameras)
                {
                    try
                    {
                        if (camera.IsImagingSource)
                        {
                            camera.ImagingControl.LiveStop();

                            // Restore original sink (might be null) and dispose preview sink
                            var currentSink = camera.ImagingControl.Sink;
                            if (currentSink is FrameHandlerSink previewSink &&
                                camera.OriginalSink != previewSink)
                            {
                                previewSink.Dispose();
                            }

                            camera.ImagingControl.Sink = camera.OriginalSink;

                            if (!isRecording)
                                camera.OriginalSink = null;
                        }
                        else
                        {
                            // Webcam stop
                            camera.WebcamCts?.Cancel();
                            camera.WebcamThread?.Join(2000);
                            camera.WebcamCapture?.Dispose();
                            camera.WebcamCapture = null;
                            camera.WebcamLastFrame?.Dispose();
                            camera.WebcamLastFrame = null;
                            if (camera.WebcamPreview != null)
                                camera.WebcamPreview.Image = null;
                        }
                    }
                    catch { }
                }

                btnStartLive.Enabled = true;
                btnStopLive.Enabled = false;
                btnStartRecording.Enabled = false;

                MessageBox.Show("All cameras stopped.", "Success", 
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error stopping cameras: {ex.Message}",
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Background thread that drives webcam preview, FPS counting, and recording.
        /// </summary>
        private void RunWebcamCaptureLoop(CameraControl camera)
        {
            using var frame = new Mat();
            while (camera.WebcamCts != null && !camera.WebcamCts.Token.IsCancellationRequested)
            {
                if (camera.WebcamCapture == null || !camera.WebcamCapture.Read(frame) || frame.Empty())
                {
                    Thread.Sleep(10);
                    continue;
                }

                camera.WebcamFrameCount++;

                // Store last frame for timelapse
                lock (camera.WebcamFrameLock)
                {
                    camera.WebcamLastFrame?.Dispose();
                    camera.WebcamLastFrame = frame.Clone();
                }

                // Write to file if normal recording
                if (IsCameraRecording(camera) && !camera.IsTimelapseMode && camera.WebcamWriter != null)
                {
                    lock (camera.WebcamFrameLock)
                    {
                        // Re-check inside lock: StopRecording may have disposed the writer
                        // while we were waiting for the lock
                        camera.WebcamWriter?.Write(frame);
                    }
                }

                // Update preview PictureBox (throttled to ~30 FPS)
                bool shouldUpdatePreview = (DateTime.UtcNow - camera.WebcamLastPreviewTime).TotalMilliseconds >= 33;
                if (shouldUpdatePreview && camera.WebcamPreview != null && camera.WebcamPreview.IsHandleCreated)
                {
                    camera.WebcamLastPreviewTime = DateTime.UtcNow;
                    try
                    {
                        var bmp = BitmapConverter.ToBitmap(frame);
                        if (camera.Settings.ShowDate || camera.Settings.ShowTime)
                            bmp = ApplyDateTimeOverlay(bmp, camera.Settings);
                        camera.WebcamPreview.BeginInvoke(() =>
                        {
                            if (camera.WebcamPreview.IsDisposed) { bmp.Dispose(); return; }
                            var old = camera.WebcamPreview.Image;
                            camera.WebcamPreview.Image = bmp;
                            old?.Dispose();
                        });
                    }
                    catch { }
                }
            }
        }

        private void BtnScreenshot_Click(object? sender, EventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(txtWorkingFolder.Text))
                {
                    MessageBox.Show("Please set a working folder first!",
                                "No Working Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Create Screenshots subfolder (used as default location if save dialog is enabled)
                string screenshotsFolder = Path.Combine(txtWorkingFolder.Text, "Screenshots");
                Directory.CreateDirectory(screenshotsFolder);

                // Generate timestamp for filenames
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");

                // Naming blocks on: screenshots start with the session prefix (S1_2026-..._L.png), so the
                // plate calibration images are linked to that session (asks which one if there are several)
                string? shotPrefix = NamingBlockPrompt.ScreenshotPrefix(this, settings);
                if (shotPrefix == null) return;
                timestamp = shotPrefix + timestamp;
                
                int savedCount = 0;
                List<string> errors = new List<string>();
                bool showSaveDialog = settings.ShowScreenshotSaveDialog;

                foreach (var camera in cameras)
                {
                    try
                    {
                        if (!camera.IsImagingSource)
                        {
                            // Webcam screenshot — grab last captured frame
                            System.Drawing.Bitmap? webcamBmp = null;
                            lock (camera.WebcamFrameLock)
                            {
                                if (camera.WebcamLastFrame != null && !camera.WebcamLastFrame.Empty())
                                    webcamBmp = OpenCvSharp.Extensions.BitmapConverter.ToBitmap(camera.WebcamLastFrame);
                            }
                            if (webcamBmp == null)
                            {
                                errors.Add($"Camera {cameras.IndexOf(camera) + 1}: No frame available");
                                continue;
                            }
                            try
                            {
                                string cameraName = string.IsNullOrWhiteSpace(camera.CustomName)
                                    ? $"Camera{cameras.IndexOf(camera) + 1}"
                                    : camera.CustomName.Replace(" ", "_");
                                string filename = $"{timestamp}_{cameraName}.png";
                                string filepath = Path.Combine(screenshotsFolder, filename);
                                if (showSaveDialog)
                                {
                                    using (SaveFileDialog saveDialog = new SaveFileDialog())
                                    {
                                        saveDialog.Filter = "PNG Image|*.png|All Files|*.*";
                                        saveDialog.FileName = filename;
                                        saveDialog.InitialDirectory = screenshotsFolder;
                                        saveDialog.Title = $"Save Screenshot - {cameraName}";
                                        if (saveDialog.ShowDialog(this) == DialogResult.OK)
                                            filepath = saveDialog.FileName;
                                        else
                                            continue;
                                    }
                                }
                                webcamBmp.Save(filepath, System.Drawing.Imaging.ImageFormat.Png);
                                savedCount++;
                            }
                            finally { webcamBmp?.Dispose(); }
                            continue;
                        }

                        if (camera.QueuedRecorder != null)
                        {
                            // Recording: the frame comes from the recorder (its sink keeps no image)
                            int camNo = cameras.IndexOf(camera) + 1;
                            using (var shot = camera.QueuedRecorder.TakeSnapshot())
                            {
                                if (shot == null)
                                {
                                    errors.Add($"Camera {camNo}: no frame from the recording within 1 s (trigger off?)");
                                    continue;
                                }
                                string shotName = string.IsNullOrWhiteSpace(camera.CustomName)
                                    ? $"Camera{camNo}" : camera.CustomName.Replace(" ", "_");
                                string shotPath = Path.Combine(screenshotsFolder, $"{timestamp}_{shotName}.png");
                                if (showSaveDialog)
                                {
                                    using (SaveFileDialog saveDialog = new SaveFileDialog())
                                    {
                                        saveDialog.Filter = "PNG Image|*.png|All Files|*.*";
                                        saveDialog.FileName = Path.GetFileName(shotPath);
                                        saveDialog.InitialDirectory = screenshotsFolder;
                                        saveDialog.Title = $"Save Screenshot - {shotName}";
                                        if (saveDialog.ShowDialog(this) != DialogResult.OK)
                                            continue;
                                        shotPath = saveDialog.FileName;
                                    }
                                }
                                shot.Save(shotPath, System.Drawing.Imaging.ImageFormat.Png);
                                savedCount++;
                            }
                            continue;
                        }

                        if (camera.ImagingControl.LiveVideoRunning)
                        {
                            ImageBuffer? imageBuffer = null;
                            
                            // Method 1: active sink is FrameHandlerSink (live preview, loop, timelapse)
                            if (camera.ImagingControl.Sink is FrameHandlerSink frameHandlerSink)
                            {
                                imageBuffer = frameHandlerSink.LastAcquiredBuffer;
                            }
                            // Method 2: try the FrameHandlerSink that was active during live preview
                            // (stored in OriginalSink).
                            // Its LastAcquiredBuffer may be null if the SDK released it on LiveStop().
                            else if (camera.OriginalSink is FrameHandlerSink originalFrameHandlerSink)
                            {
                                imageBuffer = originalFrameHandlerSink.LastAcquiredBuffer;
                            }

                            // Method 3: final fallback — ImageActiveBuffer (valid during frame delivery).
                            // Always attempted when the above methods yield nothing, regardless of which
                            // path was taken (fixes the silent skip when Method 2 returns null).
                            if (imageBuffer == null)
                            {
                                imageBuffer = camera.ImagingControl.ImageActiveBuffer;
                            }
                            
                            if (imageBuffer != null)
                            {
                                try
                                {
                                    // Generate filename with camera name or number
                                    string cameraName = string.IsNullOrWhiteSpace(camera.CustomName) 
                                        ? $"Camera{cameras.IndexOf(camera) + 1}" 
                                        : camera.CustomName.Replace(" ", "_");
                                    
                                    string filename = $"{timestamp}_{cameraName}.png";
                                    string filepath = Path.Combine(screenshotsFolder, filename);
                                    
                                    // If save dialog is enabled, show dialog for each camera
                                    if (showSaveDialog)
                                    {
                                        using (SaveFileDialog saveDialog = new SaveFileDialog())
                                        {
                                            saveDialog.Filter = "PNG Image|*.png|All Files|*.*";
                                            saveDialog.FileName = filename;
                                            saveDialog.InitialDirectory = screenshotsFolder;
                                            saveDialog.Title = $"Save Screenshot - {cameraName}";
                                            
                                            if (saveDialog.ShowDialog(this) == DialogResult.OK)
                                            {
                                                filepath = saveDialog.FileName;
                                            }
                                            else
                                            {
                                                // User cancelled this camera's screenshot
                                                continue;
                                            }
                                        }
                                    }
                                    
                                    // Create bitmap and save as PNG
                                    var bitmap = imageBuffer.CreateBitmapWrap();
                                    if (bitmap != null && bitmap.Width > 0 && bitmap.Height > 0)
                                    {
                                        using (System.Drawing.Bitmap clone = new System.Drawing.Bitmap(bitmap))
                                        {
                                            clone.Save(filepath, System.Drawing.Imaging.ImageFormat.Png);
                                        }
                                        savedCount++;
                                    }
                                    else
                                    {
                                        errors.Add($"Camera {cameras.IndexOf(camera) + 1}: Invalid bitmap (empty or zero size)");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    errors.Add($"Camera {cameras.IndexOf(camera) + 1}: Error saving bitmap - {ex.Message}");
                                }
                            }
                            else
                            {
                                errors.Add($"Camera {cameras.IndexOf(camera) + 1}: No image buffer available");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Camera {cameras.IndexOf(camera) + 1}: {ex.Message}");
                    }
                }

                // Show result (only if save dialog is disabled, or if there were errors)
                if (!showSaveDialog)
                {
                    if (savedCount > 0)
                    {
                        string message = $"✅ Saved {savedCount} screenshot(s) to:\n{screenshotsFolder}";
                        if (errors.Count > 0)
                        {
                            message += $"\n\n⚠️ Errors:\n{string.Join("\n", errors)}";
                        }
                        MessageBox.Show(message, "Screenshots Saved", 
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show("❌ No screenshots captured!\n\n" +
                                    (errors.Count > 0 ? string.Join("\n", errors) : "Make sure cameras are running."),
                                    "Screenshot Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                else if (errors.Count > 0)
                {
                    // If save dialog is enabled, only show errors
                    MessageBox.Show($"⚠️ Errors:\n{string.Join("\n", errors)}",
                                "Screenshot Errors", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error taking screenshots: {ex.Message}",
                            "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnStartRecording_Click(object? sender, EventArgs e)
        {
            try
            {
                // Ensure working folder exists
                string workingFolder = txtWorkingFolder.Text;
                if (string.IsNullOrEmpty(workingFolder))
                {
                    MessageBox.Show("Please select a Working Folder first!",
                                    "No Working Folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                
                if (!Directory.Exists(workingFolder))
                {
                    try
                    {
                        Directory.CreateDirectory(workingFolder);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Cannot create working folder:\n{ex.Message}",
                                        "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                cmbRecordingMode.Enabled = false;

                // Determine target cameras (group subset or all)
                var targets = (IReadOnlyList<CameraControl>)(_recordingFilter ?? cameras);

                int successCount = 0;
                List<string> errors = new List<string>();

                // Generate timestamp-based base name
                currentRecordingBaseName = $"Recording_{DateTime.Now:yyyyMMdd_HHmmss}";

                LogCameraInfo("=== RECORDING START ===");
                
                // Check recording mode
                string recordingMode = cmbRecordingMode.SelectedItem?.ToString() ?? "Normal Recording";
                bool isLoopMode = recordingMode == "Loop Recording";
                isTimelapseMode = recordingMode == "Timelapse"; // set class-level field for webcam loop
                int loopDurationSeconds = (int)numLoopDuration.Value;

                if (isLoopMode)
                {
                    LogCameraInfo($"Loop mode ENABLED - {loopDurationSeconds}s buffer");
                }
                else if (isTimelapseMode)
                {
                    int hours = (int)numTimelapseHours.Value;
                    int minutes = (int)numTimelapseMinutes.Value;
                    int seconds = (int)numTimelapseSeconds.Value;
                    double intervalSeconds = (hours * 3600) + (minutes * 60) + seconds;
                    
                    if (intervalSeconds < 1)
                    {
                        MessageBox.Show("Timelapse interval must be at least 1 second!",
                                    "Invalid Interval", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        cmbRecordingMode.Enabled = true;
                        return;
                    }
                    
                    LogCameraInfo($"Timelapse mode ENABLED - frame every {intervalSeconds}s");
                }
                // PHASE 1: Stop target cameras first (synchronized); webcams stay running
                foreach (var camera in targets)
                {
                    try
                    {
                        if (camera.IsImagingSource)
                            camera.ImagingControl.LiveStop();
                        camera.TotalRecordedFrames = 0;
                        camera.FrameCount = 0;

                        // Initialize timelapse if needed
                        if (isTimelapseMode)
                        {
                            int hours = (int)numTimelapseHours.Value;
                            int minutes = (int)numTimelapseMinutes.Value;
                            int seconds = (int)numTimelapseSeconds.Value;
                            double intervalSeconds = (hours * 3600) + (minutes * 60) + seconds;
                            
                            camera.IsTimelapseMode = true;
                            camera.TimelapseIntervalSeconds = intervalSeconds;
                            camera.TimelapseFrameCount = 0;
                            camera.LastTimelapseCapture = null;
                            camera.TimelapseFolder = null;
                            camera.TimelapseBaseName = null;
                            
                            LogCameraInfo($"{camera.CustomName}: Timelapse initialized - interval {intervalSeconds}s");
                        }
                        else
                        {
                            camera.IsTimelapseMode = false;
                        }
                        
                        // Initialize loop buffer if in loop mode
                        if (isLoopMode)
                        {
                            // Calculate expected FPS
                            double expectedFps;
                            if (camera.Settings.UseExternalTrigger)
                            {
                                expectedFps = (double)numExternalTriggerFps.Value;  // ← Use user-specified value
                                LogCameraInfo($"{camera.CustomName}: Using External Trigger mode - Expected FPS: {expectedFps} (from 'Expected FPS' control)");
                            }
                            else
                            {
                                expectedFps = camera.Settings.SoftwareFrameRate;
                                LogCameraInfo($"{camera.CustomName}: Using Software Frame Rate: {expectedFps} fps");
                            }
                            
                            camera.MaxLoopFrames = (int)(loopDurationSeconds * expectedFps);
                            LogCameraInfo($"{camera.CustomName}: loop keeps {camera.MaxLoopFrames} frames ({expectedFps:F1} fps)");
                        }
                    }
                    catch { }
                }

                System.Threading.Thread.Sleep(50);

                // PHASE 2: Configure sinks for target cameras
                for (int i = 0; i < targets.Count; i++)
                {
                    var camera = targets[i];
                    try
                    {
                        if (!camera.IsImagingSource)
                        {
                            // Webcam — no sink to configure; only set up WebcamWriter for normal recording
                            if (!isTimelapseMode && !isLoopMode)
                            {
                                string safeName = string.Join("_", camera.CustomName.Split(Path.GetInvalidFileNameChars()));
                                if (string.IsNullOrWhiteSpace(safeName)) safeName = $"Camera{i + 1}";
                                string aviPath = Path.Combine(workingFolder, $"{currentRecordingBaseName}_{safeName}.avi");
                                camera.RecordingFilePath = aviPath;
                                int w = camera.WebcamCapture != null
                                    ? (int)camera.WebcamCapture.Get(VideoCaptureProperties.FrameWidth)
                                    : camera.WebcamResolution.Width;
                                int h = camera.WebcamCapture != null
                                    ? (int)camera.WebcamCapture.Get(VideoCaptureProperties.FrameHeight)
                                    : camera.WebcamResolution.Height;
                                camera.WebcamWriter = new VideoWriter(aviPath, FourCC.MJPG, 30.0, new OpenCvSharp.Size(w, h));
                                LogCameraInfo($"Webcam {camera.CustomName}: recording to {aviPath}");
                            }
                            else if (isTimelapseMode)
                            {
                                // Timelapse folder already created in PHASE 1 (camera.IsTimelapseMode set);
                                // RunWebcamCaptureLoop keeps WebcamLastFrame updated; timer saves it.
                                string timelapseMainFolder = Path.Combine(workingFolder, $"Timelapse_Frames_{currentRecordingBaseName}");
                                string camName = string.Join("_", camera.CustomName.Split(Path.GetInvalidFileNameChars()));
                                string camFolder = Path.Combine(timelapseMainFolder, camName);
                                Directory.CreateDirectory(camFolder);
                                camera.TimelapseFolder = camFolder;
                                LogCameraInfo($"Webcam {camera.CustomName}: timelapse folder {camFolder}");
                            }
                            successCount++;
                            continue;
                        }

                        camera.OriginalSink = camera.ImagingControl.Sink;
                        camera.QueuedRecorder = null;

                        if (isLoopMode)
                        {
                            // Loop mode: same queued recorder as normal recordings, but it keeps only
                            // the last MaxLoopFrames frames in RAM and writes them when recording stops.
                            string loopFolder = Path.Combine(Path.GetTempPath(), $"LoopRecording_{currentRecordingBaseName}");
                            Directory.CreateDirectory(loopFolder);
                            string loopName = string.Join("_", camera.CustomName.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_");
                            if (string.IsNullOrWhiteSpace(loopName))
                                loopName = $"Camera{i + 1}";
                            string loopFile = Path.Combine(loopFolder, $"{currentRecordingBaseName}_{loopName}.avi");
                            double loopFps = camera.Settings?.UseExternalTrigger == true
                                ? (double)numExternalTriggerFps.Value
                                : camera.Settings?.SoftwareFrameRate ?? 0;
                            var loopRecorder = new QueuedAviRecorder(loopFile, $"Camera {i + 1}",
                                camera.ImagingControl.ImageWidth, camera.ImagingControl.ImageHeight,
                                loopFps, 0, LogCameraInfo, Math.Max(10, camera.MaxLoopFrames));
                            camera.QueuedRecorder = loopRecorder;
                            camera.ImagingControl.Sink = loopRecorder.Sink;
                            camera.RecordingFilePath = loopFile;
                        }
                        else if (isTimelapseMode)
                        {
                            // Timelapse mode - use FrameHandlerSink for frame capture
                            var frameHandlerSink = new FrameHandlerSink();
                            frameHandlerSink.SnapMode = false;
                            camera.ImagingControl.Sink = frameHandlerSink;
                            camera.RecordingFilePath = null;
                            
                            // Create shared timelapse folder with camera subfolder
                            string timelapseMainFolder = Path.Combine(workingFolder, $"Timelapse_Frames_{currentRecordingBaseName}");
                            string cameraName = string.Join("_", camera.CustomName.Split(Path.GetInvalidFileNameChars()));
                            string cameraFolder = Path.Combine(timelapseMainFolder, cameraName);
                            Directory.CreateDirectory(cameraFolder);
                            
                            LogCameraInfo($"Camera {i + 1} timelapse mode - folder: {cameraFolder}");
                        }
                        else
                        {
                            // Normal recording - check if Y800 format (requires FrameHandlerSink)
                            // Log current format for debugging
                            string currentFormatStr = "Unknown";
                            try
                            {
                                if (camera.ImagingControl.VideoFormatCurrent != null)
                                    currentFormatStr = camera.ImagingControl.VideoFormatCurrent.ToString();
                                else if (camera.ImagingControl.VideoFormat != null)
                                    currentFormatStr = camera.ImagingControl.VideoFormat.ToString();
                                else if (camera.Settings != null && !string.IsNullOrEmpty(camera.Settings.Format))
                                    currentFormatStr = camera.Settings.Format;
                            }
                            catch { }
                            
                            LogCameraInfo($"Camera {i + 1}: Checking format - Current: {currentFormatStr}");
                            // External trigger: make sure the camera is really armed before recording
                            if (camera.IsImagingSource && camera.Settings != null && camera.Settings.UseExternalTrigger)
                            {
                                bool armed = false;
                                try { armed = FindTriggerEnableSwitch(camera.ImagingControl)?.Switch == true; } catch { }
                                if (!armed)
                                {
                                    LogCameraInfo($"Camera {i + 1}: trigger was NOT armed at recording start - arming now");
                                    ConfigureCameraTrigger(camera, true);
                                    try { armed = FindTriggerEnableSwitch(camera.ImagingControl)?.Switch == true; } catch { }
                                    if (!armed)
                                        LogCameraInfo($"WARNING: Camera {i + 1} is NOT triggered - its frames will not be in sync with the box");
                                }
                            }
                            
                            // Uncompressed Y800 AVI via the queued recorder (frames never wait for the disk).
                            // Overlay will be applied during conversion/trimming using FFmpeg
                            string safeName = string.Join("_", camera.CustomName.Split(Path.GetInvalidFileNameChars()));
                            if (string.IsNullOrWhiteSpace(safeName))
                                safeName = $"Camera{i + 1}";
                                
                            string aviFilename = Path.Combine(workingFolder, 
                                $"{currentRecordingBaseName}_{safeName}.avi");
                            
                            camera.RecordingFilePath = aviFilename;
                            double headerFps = camera.Settings?.UseExternalTrigger == true
                                ? (double)numExternalTriggerFps.Value
                                : camera.Settings?.SoftwareFrameRate ?? 0;
                            var recorder = new QueuedAviRecorder(aviFilename, $"Camera {i + 1}",
                                camera.ImagingControl.ImageWidth, camera.ImagingControl.ImageHeight,
                                headerFps, RecordBufferMBPerCamera, LogCameraInfo);
                            camera.QueuedRecorder = recorder;
                            camera.ImagingControl.Sink = recorder.Sink;
                            
                            LogCameraInfo($"Camera {i + 1} recording mode configured: {aviFilename}");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Camera {i + 1} setup: {ex.Message}");
                        LogCameraInfo($"Camera {i + 1} setup ERROR: {ex.Message}");
                    }
                }

                // PHASE 3: Start target cameras (webcams stay running; only TIS cameras need LiveStart)
                for (int i = 0; i < targets.Count; i++)
                {
                    var camera = targets[i];
                    try
                    {
                        if (camera.IsImagingSource)
                        {
                            if (!isTimelapseMode)
                            {
                                // The SDK's live display makes the cameras stall one after another when
                                // triggering starts (6 Nov 2026), so it is always off while recording.
                                // The light preview (recorded frames, half size) replaces it.
                                camera.ImagingControl.LiveDisplay = false;
                                if (camera.QueuedRecorder != null)
                                    ShowRecordingPreview(camera);
                            }
                            camera.ImagingControl.LiveStart();
                            System.Threading.Thread.Sleep(10);
                        }
                        // Webcams: already running via RunWebcamCaptureLoop; just mark start time
                        camera.RecordingStartTime = DateTime.Now;
                        successCount++;
                        LogCameraInfo($"Camera {i + 1} started at {camera.RecordingStartTime.Value:HH:mm:ss.fff}");
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Camera {i + 1} start: {ex.Message}");
                        LogCameraInfo($"Camera {i + 1} start ERROR: {ex.Message}");
                    }
                }
                if (successCount > 0)
                {
                    isRecording = true;
                    btnStartRecording.Enabled = false;
                    btnStopRecording.Enabled = true;
                    btnScreenshot.Enabled = true;

                    // Track group recording state
                    string activeGroupId = _recordingFilter?[0]?.GroupId ?? "";
                    _groupRecording[activeGroupId] = true;
                    _groupRecordingBaseName[activeGroupId] = currentRecordingBaseName;
                    _groupRecordingMode[activeGroupId] = recordingMode;
                    // Store the EXACT cameras being recorded for this group — used at stop time
                    _groupCameras[activeGroupId] = new List<CameraControl>(targets);
                    _recordingFilter = null;   // always reset after use
                    LogCameraInfo($"Group '{activeGroupId}' recording started with {_groupCameras[activeGroupId].Count} cameras: {string.Join(", ", _groupCameras[activeGroupId].Select(c => c.CustomName))}");
                    UpdateGroupButtonRow();

                    // Start per-group timelapse capture timer if in timelapse mode
                    if (isTimelapseMode)
                    {
                        string capturedBaseName = currentRecordingBaseName;
                        string capturedGroupId = activeGroupId;
                        // Snapshot the group's cameras so this timer only touches them
                        var capturedCameras = new List<CameraControl>(_groupCameras[capturedGroupId]);

                        // Stop any previous timer for this group (shouldn't exist, but be safe)
                        if (_timelapsTimers.TryGetValue(capturedGroupId, out var oldTimer))
                        {
                            oldTimer.Stop();
                            oldTimer.Dispose();
                            _timelapsTimers.Remove(capturedGroupId);
                        }

                        var timer = new System.Windows.Forms.Timer { Interval = 100 };
                        _timelapsTimers[capturedGroupId] = timer;

                        timer.Tick += (s, e) =>
                        {
                            // Guard: timer may fire once after Dispose
                            if (!_timelapsTimers.ContainsKey(capturedGroupId)) return;
                            // Stop if this group is no longer recording
                            if (!_groupRecording.GetValueOrDefault(capturedGroupId))
                            {
                                if (_timelapsTimers.TryGetValue(capturedGroupId, out var t))
                                {
                                    _timelapsTimers.Remove(capturedGroupId);
                                    t.Stop();
                                    t.Dispose();
                                }
                                return;
                            }

                            DateTime now = DateTime.Now;

                            foreach (var camera in capturedCameras)
                            {
                                if (!camera.IsTimelapseMode) continue;

                                // Check if it's time to capture
                                bool shouldCapture = false;
                                if (camera.LastTimelapseCapture == null)
                                {
                                    shouldCapture = true; // First capture
                                }
                                else
                                {
                                    double elapsed = (now - camera.LastTimelapseCapture.Value).TotalSeconds;
                                    if (elapsed >= camera.TimelapseIntervalSeconds)
                                        shouldCapture = true;
                                }

                                if (shouldCapture)
                                {
                                    try
                                    {
                                        System.Drawing.Bitmap? tlBitmap = null;

                                        if (camera.IsImagingSource)
                                        {
                                            if (camera.ImagingControl.Sink is FrameHandlerSink tlSink)
                                            {
                                                var imageBuffer = tlSink.LastAcquiredBuffer;
                                                if (imageBuffer != null)
                                                    tlBitmap = new System.Drawing.Bitmap(imageBuffer.CreateBitmapWrap());
                                            }
                                        }
                                        else
                                        {
                                            lock (camera.WebcamFrameLock)
                                            {
                                                if (camera.WebcamLastFrame != null)
                                                    tlBitmap = BitmapConverter.ToBitmap(camera.WebcamLastFrame);
                                            }
                                        }

                                        if (tlBitmap != null)
                                        {
                                            using (var clone = tlBitmap)
                                            {
                                                string cameraName = string.Join("_", camera.CustomName.Split(Path.GetInvalidFileNameChars()));
                                                string timelapseMainFolder = Path.Combine(workingFolder, $"Timelapse_Frames_{capturedBaseName}");
                                                string cameraFolder = Path.Combine(timelapseMainFolder, cameraName);
                                                camera.TimelapseFolder ??= cameraFolder;     // store once for compilation later
                                                camera.TimelapseBaseName ??= capturedBaseName;
                                                string frameNumber = camera.TimelapseFrameCount.ToString("D8"); // D8 supports up to 99,999,999 frames
                                                string imagePath = Path.Combine(cameraFolder, $"{capturedBaseName}_{cameraName}_{frameNumber}.png");

                                                System.Drawing.Bitmap finalBitmap = clone;
                                                bool overlayApplied = false;
                                                if (camera.Settings.ShowDate || camera.Settings.ShowTime)
                                                {
                                                    finalBitmap = ApplyDateTimeOverlay(clone, camera.Settings, now);
                                                    overlayApplied = true;
                                                }

                                                try
                                                {
                                                    finalBitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
                                                }
                                                finally
                                                {
                                                    if (overlayApplied && finalBitmap != clone)
                                                        finalBitmap.Dispose();
                                                }

                                                camera.TimelapseFrameCount++;
                                                camera.LastTimelapseCapture = now;
                                                // Throttle: log every 100 frames to avoid a multi-MB log file
                                                if (camera.TimelapseFrameCount % 100 == 1)
                                                    LogCameraInfo($"{camera.CustomName}: Timelapse frame {camera.TimelapseFrameCount} captured");
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        LogCameraInfo($"{camera.CustomName}: Timelapse capture error - {ex.Message}");
                                    }
                                }
                            }
                        };

                        timer.Start();
                        LogCameraInfo($"Timelapse capture timer started for group '{capturedGroupId}'");
                    }

                    // Check max duration from settings
                    if (settings.MaxDurationEnabled)
                    {
                        // Convert max duration to minutes based on selected unit
                        int maxDurationValue = settings.MaxDurationValue;
                        string durationUnit = settings.MaxDurationUnit;
                        int maxMinutes = maxDurationValue;
                        
                        if (durationUnit == "hours")
                            maxMinutes = maxDurationValue * 60;
                        else if (durationUnit == "days")
                            maxMinutes = maxDurationValue * 60 * 24;
                        
                        // Format duration string for display
                        string durationDisplay = $"{maxDurationValue} {durationUnit}";
                        if (maxDurationValue == 1)
                        {
                            // Remove 's' for singular
                            durationDisplay = durationDisplay.Replace("minutes", "minute")
                                                             .Replace("hours", "hour")
                                                             .Replace("days", "day");
                        }
                        
                        DateTime recordingStart = DateTime.Now;
                        
                        string activeGidForTimer = activeGroupId;
                        System.Windows.Forms.Timer maxDurationTimer = new System.Windows.Forms.Timer();
                        maxDurationTimer.Interval = 10000; // Check every 10 seconds
                        maxDurationTimer.Tick += (s, e) =>
                        {
                            if (!_groupRecording.GetValueOrDefault(activeGidForTimer))
                            {
                                maxDurationTimer.Stop();
                                maxDurationTimer.Dispose();
                                return;
                            }

                            double elapsedMinutes = (DateTime.Now - recordingStart).TotalMinutes;
                            if (elapsedMinutes >= maxMinutes)
                            {
                                maxDurationTimer.Stop();
                                LogCameraInfo($"Max duration reached ({durationDisplay}) - stopping recording automatically");

                                // Auto-stop recording for this specific group
                                this.Invoke(new Action(() =>
                                {
                                    btnScreenshot.Enabled = btnStopLive.Enabled;
                                    StopRecording(activeGidForTimer);
                                    MessageBox.Show($"Recording automatically stopped after {durationDisplay}.",
                                                    "Max Duration Reached", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                }));

                                maxDurationTimer.Dispose();
                            }
                        };
                        maxDurationTimer.Start();
                        
                        LogCameraInfo($"Max duration monitoring enabled: {durationDisplay}");
                    }
                }
                
                
            // Only show popup if there were errors
            if (errors.Count > 0)
            {
                MessageBox.Show($"Recording started on {successCount}/{cameras.Count} cameras.\n\nErrors:\n{string.Join("\n", errors)}",
                                "Partial Success", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error starting recording: {ex.Message}\n\nStack trace: {ex.StackTrace}",
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LogCameraInfo($"CRITICAL ERROR: {ex.Message}");
            }
        }

        private void BtnStopRecording_Click(object? sender, EventArgs e)
        {
            btnScreenshot.Enabled = btnStopLive.Enabled;
            StopRecording(""); // "" = stop only unassigned cameras (group cameras unaffected)
        }

        /// <summary>
        /// Generates a JSON timestamp file for a video, mapping frame numbers to timestamps.
        /// </summary>
        private void GenerateTimestampFile(string videoFile, DateTime recordingStartTime, double fps, int? cameraIndex = null)
        {
            // Check if JSON generation is enabled for this camera
            if (cameraIndex.HasValue && cameraIndex.Value < cameras.Count)
            {
                if (!cameras[cameraIndex.Value].Settings.GenerateJsonTimestamps)
                {
                    return; // JSON generation disabled for this camera
                }
            }
            
            try
            {
                // Get video information
                int frameCount = GetVideoFrameCount(videoFile);
                if (frameCount == 0)
                {
                    LogCameraInfo($"Warning: Could not determine frame count for {Path.GetFileName(videoFile)}, skipping timestamp file generation");
                    return;
                }
                
                double videoFps = GetVideoFrameRate(videoFile);
                if (videoFps <= 0)
                    videoFps = fps; // Use provided FPS as fallback
                
                // Get video resolution
                int width = 0, height = 0;
                try
                {
                    using (var capture = new VideoCapture(videoFile))
                    {
                        width = (int)capture.Get(VideoCaptureProperties.FrameWidth);
                        height = (int)capture.Get(VideoCaptureProperties.FrameHeight);
                    }
                }
                catch { }
                
                // Get file info
                FileInfo fileInfo = new FileInfo(videoFile);
                long fileSize = fileInfo.Length;
                DateTime fileCreated = fileInfo.CreationTime;
                DateTime fileModified = fileInfo.LastWriteTime;
                
                // Build JSON structure
                var timestampData = new
                {
                    metadata = new
                    {
                        videoFile = Path.GetFileName(videoFile),
                        videoPath = videoFile,
                        recordingStartTime = recordingStartTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        recordingStartTimeLocal = recordingStartTime.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                        frameCount = frameCount,
                        frameRate = videoFps,
                        durationSeconds = frameCount / videoFps,
                        resolution = new { width = width, height = height },
                        fileSizeBytes = fileSize,
                        fileCreated = fileCreated.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                        fileModified = fileModified.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                        cameraIndex = cameraIndex,
                        generatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        generatedAtLocal = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff")
                    },
                    frames = new List<object>()
                };
                
                // Generate timestamp for each frame (1-based)
                for (int frameNum = 1; frameNum <= frameCount; frameNum++)
                {
                    // Calculate timestamp: recording start + (frame number - 1) / fps
                    // Frame 1 is at recording start time
                    double secondsOffset = (frameNum - 1) / fps;
                    DateTime frameTimestamp = recordingStartTime.AddSeconds(secondsOffset);
                    
                    // Calculate Unix timestamp
                    DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    long unixTimestamp = (long)(frameTimestamp.ToUniversalTime() - epoch).TotalSeconds;
                    double unixTimestampMs = (frameTimestamp.ToUniversalTime() - epoch).TotalMilliseconds;
                    
                    var frameData = new
                    {
                        frameNumber = frameNum, // 1-based
                        timestamp = frameTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), // ISO 8601 UTC
                        timestampLocal = frameTimestamp.ToString("yyyy-MM-ddTHH:mm:ss.fff"), // Local time
                        date = frameTimestamp.ToString("yyyy-MM-dd"), // Date only
                        time = frameTimestamp.ToString("HH:mm:ss"), // Time only (no milliseconds)
                        timeWithMs = frameTimestamp.ToString("HH:mm:ss.fff"), // Time with milliseconds
                        unixTimestamp = unixTimestamp, // Unix timestamp in seconds
                        unixTimestampMs = unixTimestampMs, // Unix timestamp in milliseconds
                        secondsFromStart = secondsOffset // Seconds elapsed since recording start
                    };
                    
                    timestampData.frames.Add(frameData);
                }
                
                // Write JSON file
                string jsonFile = Path.ChangeExtension(videoFile, ".json");
                string json = System.Text.Json.JsonSerializer.Serialize(timestampData, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                
                File.WriteAllText(jsonFile, json);
                LogCameraInfo($"Generated timestamp file: {Path.GetFileName(jsonFile)} ({frameCount} frames)");
            }
            catch (Exception ex)
            {
                LogCameraInfo($"Error generating timestamp file for {Path.GetFileName(videoFile)}: {ex.Message}");
            }
        }
        
        private int GetVideoFrameCount(string videoFile)
        {
            try
            {
                string ffprobePath = Path.Combine(Path.GetDirectoryName(FFMPEG_PATH)!, "ffprobe.exe");
                
                if (!File.Exists(ffprobePath))
                    return 0;

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -select_streams v:0 -count_packets -show_entries stream=nb_read_packets -of csv=p=0 \"{videoFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                using (Process process = Process.Start(startInfo)!)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (int.TryParse(output.Trim(), out int frameCount))
                    {
                        return frameCount;
                    }
                }
            }
            catch { }

            return 0;
        }

        private double GetVideoFrameRate(string videoFile)
        {
            try
            {
                string ffprobePath = Path.Combine(Path.GetDirectoryName(FFMPEG_PATH)!, "ffprobe.exe");
                
                if (!File.Exists(ffprobePath))
                    return 0;

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=r_frame_rate -of default=noprint_wrappers=1:nokey=1 \"{videoFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                using (Process process = Process.Start(startInfo)!)
                {
                    string output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit();

                    // Frame rate often comes as "90/1" or "11500/1000"
                    if (output.Contains("/"))
                    {
                        string[] parts = output.Split('/');
                        if (parts.Length == 2 && 
                            double.TryParse(parts[0], out double numerator) && 
                            double.TryParse(parts[1], out double denominator) && 
                            denominator > 0)
                        {
                            return numerator / denominator;
                        }
                    }
                    else if (double.TryParse(output, out double fps))
                    {
                        return fps;
                    }
                }
            }
            catch { }

            return 0;
        }

        // groupId: null = stop ALL cameras (Stop Live / disk-full emergency)
        //          ""   = stop only unassigned cameras (global Stop Recording button)
        //          "A"  = stop only Group A cameras (group-specific stop)
        private void StopRecording(string? groupId = null)
        {
            try
            {
                if (isRecording)
                {
                    LogCameraInfo($"=== RECORDING STOP REQUESTED (groupId='{groupId ?? "ALL"}') ===");

                    // Determine target cameras
                    bool stopAll = groupId == null;
                    string activeGroupId = groupId ?? "";
                    IReadOnlyList<CameraControl> targets;
                    if (stopAll)
                    {
                        targets = cameras;
                    }
                    else if (_groupCameras.TryGetValue(activeGroupId, out var storedCams) && storedCams.Count > 0)
                    {
                        // Use the EXACT cameras that were stored when recording started for this group
                        targets = storedCams;
                    }
                    else
                    {
                        // Fallback: filter by GroupId (should not normally be needed)
                        targets = cameras.Where(c => c.GroupId == activeGroupId).ToList();
                    }

                    LogCameraInfo($"StopRecording: groupId='{activeGroupId}', stopAll={stopAll}, targets={targets.Count}: {string.Join(", ", targets.Select(c => c.CustomName))}");

                    // Guard: nothing to stop
                    if (targets.Count == 0)
                    {
                        LogCameraInfo($"StopRecording: no cameras found for groupId='{activeGroupId}', returning");
                        return;
                    }

                    // Determine mode from what was used when this group started recording
                    string recordingMode = _groupRecordingMode.GetValueOrDefault(
                        activeGroupId, cmbRecordingMode.SelectedItem?.ToString() ?? "Normal Recording");
                    bool isGroupLoopMode = recordingMode == "Loop Recording";
                    bool isGroupTimelapse = recordingMode == "Timelapse";

                    int successCount = 0;
                    List<string> aviFiles = new List<string>();
                    List<(string filename, int frames, double duration)> frameStats = new List<(string, int, double)>();
                    List<long> aviFileSizes = new List<long>();

                    if (isGroupTimelapse)
                    {
                        // TIMELAPSE MODE: Compile images to video
                        LogCameraInfo("Timelapse mode - stopping and compiling videos");

                        // Stop target cameras
                        foreach (var camera in targets)
                        {
                            try
                            {
                                camera.RecordingStopTime = DateTime.Now;
                                if (camera.IsImagingSource)
                                    camera.ImagingControl.LiveStop();
                                LogCameraInfo($"{camera.CustomName}: Stopped - captured {camera.TimelapseFrameCount} frames");
                            }
                            catch (Exception ex)
                            {
                                LogCameraInfo($"{camera.CustomName}: Error stopping - {ex.Message}");
                            }
                        }

                        // Stop this group's timelapse timer only
                        if (stopAll)
                        {
                            foreach (var kvp in _timelapsTimers.ToList())
                            {
                                _timelapsTimers.Remove(kvp.Key);
                                kvp.Value.Stop();
                                kvp.Value.Dispose();
                            }
                        }
                        else if (_timelapsTimers.TryGetValue(activeGroupId, out var tTimer))
                        {
                            _timelapsTimers.Remove(activeGroupId); // remove before Stop to prevent Tick re-entry
                            tTimer.Stop();
                            tTimer.Dispose();
                        }

                        // Restart TIS cameras in live mode; webcams keep running
                        foreach (var camera in targets)
                        {
                            try
                            {
                                if (camera.IsImagingSource)
                                {
                                    camera.ImagingControl.Sink = camera.OriginalSink;
                                    camera.ImagingControl.LiveStart();
                                }
                            }
                            catch { }
                        }
                        if (stopAll)
                        {
                            foreach (var key in _groupRecording.Keys.ToList()) _groupRecording[key] = false;
                            _groupCameras.Clear();
                        }
                        else
                        {
                            _groupRecording[activeGroupId] = false;
                            _groupCameras.Remove(activeGroupId);
                        }
                        isRecording = _groupRecording.Values.Any(v => v);
                        if (!isRecording) cmbRecordingMode.Enabled = true;
                        UpdateGroupButtonRow();
                        btnStopRecording.Enabled = _groupRecording.GetValueOrDefault("");

                        // Show timelapse compilation dialog for this group's cameras only
                        ShowTimelapseCompilationDialog(targets);
                        return;
                    }
                    else if (isGroupLoopMode)
                    {
                        // LOOP MODE: CRITICAL - Stop cameras FIRST to freeze the buffer!
                        LogCameraInfo("Loop mode - stopping cameras immediately to freeze buffer");

                        // Stop the cameras first so the loop buffers stop changing
                        for (int i = 0; i < targets.Count; i++)
                        {
                            var camera = targets[i];
                            try
                            {
                                camera.RecordingStopTime = DateTime.Now;
                                if (camera.IsImagingSource)
                                {
                                    LogFrameDropCounters(camera, i + 1);   // must be read BEFORE LiveStop
                                    camera.ImagingControl.LiveStop();
                                }
                                LogCameraInfo($"{camera.CustomName}: Camera stopped at {camera.RecordingStopTime.Value:HH:mm:ss.fff}");
                            }
                            catch (Exception ex)
                            {
                                LogCameraInfo($"{camera.CustomName}: Error stopping camera - {ex.Message}");
                            }
                        }

                        // Write each loop buffer (oldest frame first) to its temporary AVI
                        for (int i = 0; i < targets.Count; i++)
                        {
                            var camera = targets[i];
                            try
                            {
                                var loopRecorder = camera.QueuedRecorder;
                                if (loopRecorder != null)
                                {
                                    RemoveRecordingPreview(camera);
                                    loopRecorder.Finish();
                                    if (loopRecorder.FramesLost > 0)
                                        camera.DroppedFrames = Math.Max(0, camera.DroppedFrames) + (int)loopRecorder.FramesLost;
                                    if (loopRecorder.OldestKeptTime.HasValue)
                                        camera.RecordingStartTime = loopRecorder.OldestKeptTime;   // overlay time = first saved frame
                                    if (camera.RecordingFilePath != null && File.Exists(camera.RecordingFilePath))
                                        aviFiles.Add(camera.RecordingFilePath);
                                    else
                                        LogCameraInfo($"{camera.CustomName}: loop buffer was empty - no frames to save");
                                }

                                if (camera.IsImagingSource)
                                {
                                    camera.ImagingControl.Sink = camera.OriginalSink;
                                    loopRecorder?.DisposeSink();
                                    camera.ImagingControl.LiveDisplay = true;   // preview back on after recording
                                    camera.ImagingControl.LiveStart();
                                }

                                successCount++;
                            }
                            catch (Exception ex)
                            {
                                LogCameraInfo($"Camera {i + 1} loop stop ERROR: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        // NORMAL MODE: Stop target cameras and capture individual stop times
                        for (int i = 0; i < targets.Count; i++)
                        {
                            var camera = targets[i];
                            try
                            {
                                System.Threading.Thread.Sleep(10);
                                camera.RecordingStopTime = DateTime.Now;
                                LogCameraInfo($"Camera {i + 1} stopping at {camera.RecordingStopTime.Value:HH:mm:ss.fff}");

                                if (camera.IsImagingSource)
                                {
                                    LogFrameDropCounters(camera, i + 1);   // must be read BEFORE LiveStop
                                    camera.ImagingControl.LiveStop();
                                    if (camera.QueuedRecorder != null)
                                    {
                                        RemoveRecordingPreview(camera);
                                        // write what is still in the RAM queue and close the AVI
                                        camera.QueuedRecorder.Finish();
                                        if (camera.QueuedRecorder.FramesLost > 0)
                                            camera.DroppedFrames = Math.Max(0, camera.DroppedFrames)
                                                                   + (int)camera.QueuedRecorder.FramesLost;
                                    }
                                    camera.ImagingControl.Sink = camera.OriginalSink;
                                    camera.QueuedRecorder?.DisposeSink();
                                    camera.ImagingControl.LiveDisplay = true;   // preview back on after recording
                                    camera.ImagingControl.LiveStart();
                                }
                                else
                                {
                                    // Webcam: stop writer under lock so RunWebcamCaptureLoop
                                    // cannot be mid-write when we dispose (race → native crash)
                                    lock (camera.WebcamFrameLock)
                                    {
                                        camera.WebcamWriter?.Dispose();
                                        camera.WebcamWriter = null;
                                    }
                                }

                                if (camera.RecordingFilePath != null)
                                {
                                    aviFiles.Add(camera.RecordingFilePath);
                                }

                                successCount++;
                            }
                            catch (Exception ex)
                            {
                                LogCameraInfo($"Camera {i + 1} stop ERROR: {ex.Message}");
                            }
                        }
                    }

                    // Clear group recording state
                    if (stopAll)
                    {
                        foreach (var key in _groupRecording.Keys.ToList()) _groupRecording[key] = false;
                        _groupCameras.Clear();
                    }
                    else
                    {
                        _groupRecording[activeGroupId] = false;
                        _groupCameras.Remove(activeGroupId);
                    }
                    isRecording = _groupRecording.Values.Any(v => v);
                    if (!isRecording) cmbRecordingMode.Enabled = true;
                    UpdateGroupButtonRow();
                    btnStopRecording.Enabled = _groupRecording.GetValueOrDefault("");

                    // Give files a moment to finalize
                    System.Threading.Thread.Sleep(500);

                    // Get frame counts and calculate durations (same for both modes)

                    // Get frame counts and calculate durations (same for both modes)
                    for (int i = 0; i < aviFiles.Count && i < targets.Count; i++)
                    {
                        string aviFile = aviFiles[i];
                        var camera = targets[i];
                        
                        if (File.Exists(aviFile))
                        {
                            FileInfo fileInfo = new FileInfo(aviFile);
                            long fileSize = fileInfo.Length;
                            aviFileSizes.Add(fileSize);
                            
                            int frameCount = 0;
                            double actualDuration = 0;
                            
                            if (isGroupLoopMode)
                            {
                                // For loop mode, we know the frame count from the buffer
                                // Try FFprobe first, but fall back to buffer count if it fails
                                frameCount = GetVideoFrameCount(aviFile);
                                
                                if (frameCount == 0)
                                {
                                    // FFprobe failed, use the buffer size we saved
                                    LogCameraInfo($"Camera {i + 1}: FFprobe failed, using buffer frame count");
                                    // Count frames directly from the file using OpenCV
                                    try
                                    {
                                        using (var capture = new VideoCapture(aviFile))
                                        {
                                            frameCount = (int)capture.FrameCount;
                                        }
                                    }
                                    catch
                                    {
                                        // Last resort: estimate from file size
                                        // Assume uncompressed grayscale 640x480 = ~300KB per frame
                                        frameCount = (int)(fileSize / 300000);
                                        LogCameraInfo($"Camera {i + 1}: Using estimated frame count: {frameCount}");
                                    }
                                }
                                
                                // Calculate duration from frame count
                                double expectedFps;
                                if (camera.Settings.UseExternalTrigger)
                                {
                                    expectedFps = (double)numExternalTriggerFps.Value;
                                }
                                else
                                {
                                    expectedFps = camera.Settings.SoftwareFrameRate;
                                }
                                actualDuration = frameCount / expectedFps;

                                if (camera.QueuedRecorder != null)
                                {
                                    long missed = camera.QueuedRecorder.LogTimeline($"Camera {i + 1}");
                                    if (missed > 0)   // ignored triggers: invisible to the SDK counters
                                        camera.DroppedFrames = Math.Max(0, camera.DroppedFrames) + (int)missed;
                                }
                            }
                            else
                            {
                                // Normal recording mode
                                frameCount = GetVideoFrameCount(aviFile);
                                
                                if (camera.RecordingStartTime.HasValue && camera.RecordingStopTime.HasValue)
                                {
                                    actualDuration = (camera.RecordingStopTime.Value - camera.RecordingStartTime.Value).TotalSeconds;
                                }

                                // Prefer the REAL frame rate measured from frame arrival times. The Start/Stop
                                // button times include the camera/file start-up delay and idle time before the
                                // trigger is switched on, which made the reported fps too low.
                                double measuredFps = camera.QueuedRecorder?.MeasuredFps() ?? 0;
                                if (measuredFps > 0)
                                {
                                    LogCameraInfo($"Camera {i + 1}: measured frame rate {measuredFps:F2} fps " +
                                                  $"(button-time estimate was {(actualDuration > 0 ? frameCount / actualDuration : 0):F2} fps)");
                                    actualDuration = frameCount / measuredFps;
                                }
                                else
                                {
                                    LogCameraInfo($"Camera {i + 1}: no frame timing available - fps estimated from Start/Stop times");
                                }
                                if (camera.QueuedRecorder != null)
                                {
                                    long missed = camera.QueuedRecorder.LogTimeline($"Camera {i + 1}");
                                    if (missed > 0)   // ignored triggers: invisible to the SDK counters
                                        camera.DroppedFrames = Math.Max(0, camera.DroppedFrames) + (int)missed;
                                }
                            }
                            
                            double aviDuration = GetVideoDuration(aviFile);
                            
                            LogCameraInfo($"Camera {i + 1} AVI contains: {frameCount} frames");
                            LogCameraInfo($"Camera {i + 1} Duration: {actualDuration:F2}s, AVI claims: {aviDuration:F2}s");
                            
                            if (camera.DroppedFrames > 0)
                            {
                                LogCameraInfo($"⚠️ Camera {i + 1} DROPPED {camera.DroppedFrames} frames during recording!");
                            }
                            else if (camera.DroppedFrames == 0)
                            {
                                LogCameraInfo($"✅ Camera {i + 1} captured all frames without drops");
                            }
                            string filename = Path.GetFileName(aviFile);
                            frameStats.Add((filename, frameCount, actualDuration));
                            
                            // Note: JSON timestamp files are generated when files are saved/converted,
                            // not for the original temporary recording files
                        }
                    }

                    foreach (var doneCam in targets) doneCam.QueuedRecorder = null;   // free the recorders (timeline/fps done)

                    // Remove files that have 0 frames (recording too short / empty AVI)
                    for (int i = frameStats.Count - 1; i >= 0; i--)
                    {
                        if (frameStats[i].frames == 0)
                        {
                            LogCameraInfo($"Skipping empty file: {frameStats[i].filename}");
                            try { if (File.Exists(aviFiles[i])) File.Delete(aviFiles[i]); } catch { }
                            frameStats.RemoveAt(i);
                            aviFiles.RemoveAt(i);
                            aviFileSizes.RemoveAt(i);
                        }
                    }

                    // Check if we got any valid files
                    if (frameStats.Count == 0)
                    {
                        MessageBox.Show("No valid video files were created. Recording may have been too short.",
                                        "Recording Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // Show frame selection and save dialog (same as normal)
                    string groupBaseName = _groupRecordingBaseName.GetValueOrDefault(activeGroupId, currentRecordingBaseName);
                    using (FrameSelectionDialog dialog = new FrameSelectionDialog(
                        frameStats,
                        aviFileSizes,
                        aviFiles,
                        txtWorkingFolder.Text,
                        groupBaseName,
                        settings))
                    {
                        if (dialog.ShowDialog() == DialogResult.OK)
                        {
                            ProcessRecordingOutputWithTrimming(aviFiles, dialog.SelectedPath, dialog.SelectedFilename, 
                                                            dialog.SelectedFps, dialog.SelectedFormat, dialog.FrameRanges);
                        }
                        else
                        {
                            // User cancelled - delete temp files
                            foreach (string aviFile in aviFiles)
                            {
                                try
                                {
                                    if (File.Exists(aviFile))
                                    {
                                        File.Delete(aviFile);
                                        LogCameraInfo($"Deleted: {aviFile}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LogCameraInfo($"Failed to delete {aviFile}: {ex.Message}");
                                }
                            }
                            
                            // Delete temp folder if it exists
                            if (isGroupLoopMode)
                            {
                                try
                                {
                                    string tempFolder = Path.GetDirectoryName(aviFiles[0]);
                                    if (tempFolder != null && Directory.Exists(tempFolder))
                                    {
                                        Directory.Delete(tempFolder, true);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error stopping recording: {ex.Message}",
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LogCameraInfo($"Stop recording ERROR: {ex.Message}");
            }
        }
        private void ProcessRecordingOutput(List<string> aviFiles, string outputPath, string outputFilename, 
                                            double fps, FrameSelectionDialog.OutputFormat format)
        {
            try
            {
                Directory.CreateDirectory(outputPath);
                
                if (format == FrameSelectionDialog.OutputFormat.MP4)
                {
                    // Convert to MP4 and delete AVIs
                    ConvertAvisToMp4(aviFiles, outputPath, outputFilename, fps, deleteOriginals: true);
                }
                else if (format == FrameSelectionDialog.OutputFormat.AVI)
                {
                    // Re-encode AVIs with correct FPS metadata
                    ConvertAvisToAvi(aviFiles, outputPath, outputFilename, fps);
                }
                else // Both
                {
                    // First save AVIs
                    ConvertAvisToAvi(aviFiles, outputPath, outputFilename, fps);
                    // Then create MP4s (keep the newly created AVIs)
                    List<string> newAviFiles = new List<string>();
                    for (int i = 0; i < aviFiles.Count; i++)
                    {
                        string newAviPath = Path.Combine(outputPath, $"{outputFilename}_Camera{i + 1}_AVI.avi");
                        newAviFiles.Add(newAviPath);
                    }
                    ConvertAvisToMp4(newAviFiles, outputPath, outputFilename, fps, deleteOriginals: false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error processing recording output: {ex.Message}",
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LogCameraInfo($"ProcessRecordingOutput ERROR: {ex.Message}");
            }
        }

        private void ProcessRecordingOutputWithTrimming(List<string> aviFiles, string outputPath, string outputFilename, 
                                                        double fps, FrameSelectionDialog.OutputFormat format,
                                                        Dictionary<string, (int startFrame, int endFrame)> frameRanges)
        {
            try
            {
                Directory.CreateDirectory(outputPath);
                
                // Extract custom camera names from AVI filenames
                List<string> customNames = new List<string>();
                foreach (string aviFile in aviFiles)
                {
                    string filename = Path.GetFileNameWithoutExtension(aviFile);
                    // Remove the timestamp prefix to get just the camera name
                    // Format is: Recording_20251110_162636_CameraName
                    int lastUnderscore = filename.LastIndexOf('_');
                    if (lastUnderscore >= 0)
                    {
                        customNames.Add(filename.Substring(lastUnderscore + 1));
                    }
                    else
                    {
                        customNames.Add($"Camera{customNames.Count + 1}");
                    }
                }
                
                if (format == FrameSelectionDialog.OutputFormat.MP4)
                {
                    // Convert to MP4 with trimming and delete AVIs
                    ConvertAvisToMp4WithTrimming(aviFiles, outputPath, outputFilename, fps, frameRanges, deleteOriginals: true, customNames);
                }
                else if (format == FrameSelectionDialog.OutputFormat.AVI)
                {
                    // Re-encode AVIs with correct FPS metadata and trimming
                    ConvertAvisToAviWithTrimming(aviFiles, outputPath, outputFilename, fps, frameRanges, customNames);
                }
                else // Both
                {
                    // First save trimmed AVIs
                    ConvertAvisToAviWithTrimming(aviFiles, outputPath, outputFilename, fps, frameRanges, customNames);
                    // Then create MP4s (keep the newly created AVIs)
                    List<string> newAviFiles = new List<string>();
                    for (int i = 0; i < aviFiles.Count; i++)
                    {
                        string cameraName = string.Join("_", customNames[i].Split(Path.GetInvalidFileNameChars()));
                        string newAviPath = Path.Combine(outputPath, $"{outputFilename}_{cameraName}_AVI.avi");
                        newAviFiles.Add(newAviPath);
                    }
                    
                    // Create frame ranges for new files (full range since already trimmed)
                    var fullRanges = new Dictionary<string, (int, int)>();
                    foreach (var file in newAviFiles)
                    {
                        string filename = Path.GetFileName(file);
                        // Get frame count of trimmed file
                        int frameCount = GetVideoFrameCount(file);
                        fullRanges[filename] = (1, frameCount);
                    }
                    
                    ConvertAvisToMp4WithTrimming(newAviFiles, outputPath, outputFilename, fps, fullRanges, deleteOriginals: false, customNames);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error processing recording output: {ex.Message}",
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LogCameraInfo($"ProcessRecordingOutputWithTrimming ERROR: {ex.Message}");
            }
        }

        private void ConvertAvisToAviWithTrimming(List<string> aviFiles, string outputPath, string outputFilename,
                                    double fps, Dictionary<string, (int startFrame, int endFrame)> frameRanges,
                                    List<string> customNames)
        {
            // Ensure output directory exists
            Directory.CreateDirectory(outputPath);

            // Verify it's a valid path
            if (outputPath.Length > 260)
            {
                MessageBox.Show("Output path is too long! Windows has a 260 character limit.\n\nPlease choose a shorter path.",
                                "Path Too Long", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }    
                
            if (!File.Exists(FFMPEG_PATH))
            {
                MessageBox.Show($"FFmpeg not found at:\n{FFMPEG_PATH}\n\nPlease install FFmpeg.",
                                "FFmpeg Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Create progress form
            Form progressForm = new Form
            {
                Text = "Saving Trimmed AVI Files",
                Size = new System.Drawing.Size(500, 200),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ControlBox = false
            };

            Label statusLabel = new Label
            {
                Text = "Preparing...",
                Location = new System.Drawing.Point(20, 20),
                Size = new System.Drawing.Size(450, 40),
                Font = new System.Drawing.Font("Arial", 10)
            };
            progressForm.Controls.Add(statusLabel);

            Label detailLabel = new Label
            {
                Text = "",
                Location = new System.Drawing.Point(20, 65),
                Size = new System.Drawing.Size(450, 20),
                Font = new System.Drawing.Font("Arial", 8),
                ForeColor = System.Drawing.Color.Gray
            };
            progressForm.Controls.Add(detailLabel);

            ProgressBar progressBar = new ProgressBar
            {
                Location = new System.Drawing.Point(20, 95),
                Size = new System.Drawing.Size(450, 30),
                Minimum = 0,
                Maximum = aviFiles.Count
            };
            progressForm.Controls.Add(progressBar);

            progressForm.Show();
            Application.DoEvents();

            int successCount = 0;
            List<string> errors = new List<string>();
            
            for (int i = 0; i < aviFiles.Count; i++)
            {
                string aviFile = aviFiles[i];
                if (!File.Exists(aviFile))
                    continue;

                string filename = Path.GetFileName(aviFile);
                string cameraName = i < customNames.Count ? customNames[i] : $"Camera{i + 1}";
                // Sanitize camera name
                cameraName = string.Join("_", cameraName.Split(Path.GetInvalidFileNameChars()));

                // Use a temporary output file first, then rename
                string finalOutputAvi = Path.Combine(outputPath, $"{outputFilename}_{cameraName}_AVI.avi");
                string tempOutputAvi = Path.Combine(outputPath, $"temp_{outputFilename}_{cameraName}_AVI.avi");

                statusLabel.Text = $"Processing {i + 1} of {aviFiles.Count}: {cameraName}";
                Application.DoEvents();

                try
                {
                    // Detect source pixel format and codec tag
                    string pixelFormat = GetVideoPixelFormat(aviFile);
                    string codecTag = GetVideoCodecTag(aviFile);
                    
                    // Get frame range for this file
                    var range = frameRanges[filename];
                    int startFrame = range.startFrame;
                    int endFrame = range.endFrame;
                    int totalFrames = endFrame - startFrame + 1;
                    
                    // Calculate time offsets (frames are 1-based)
                    double startTime = (startFrame - 1) / fps;
                    double duration = totalFrames / fps;
                    
                    detailLabel.Text = $"Frames {startFrame}-{endFrame} ({totalFrames} frames) | Format: {pixelFormat} | Tag: {codecTag}";
                    Application.DoEvents();
                    
                    LogCameraInfo($"Trimming {filename}: frames {startFrame}-{endFrame}, pixel format: {pixelFormat}, codec tag: {codecTag}, fps: {fps}");
                    
                    // ✅ FIXED: Use temp file to avoid overwriting input
                    // For RGB/color formats, re-encode to ensure compatibility
                    // For grayscale (Y800), use stream copy for speed but fix frame rate with setpts
                    string videoCodec;
                    string videoFilter = "";

                    // Get camera settings for overlay (match by index)
                    CameraSettings? cameraSettings = null;
                    DateTime? recordingStartTime = null;
                    int videoHeight = 480; // Default
                    
                    if (i < cameras.Count)
                    {
                        cameraSettings = cameras[i].Settings;
                        recordingStartTime = cameras[i].RecordingStartTime;
                        
                        // Get video height for font scaling
                        try
                        {
                            using (var capture = new VideoCapture(aviFile))
                            {
                                videoHeight = (int)capture.Get(VideoCaptureProperties.FrameHeight);
                            }
                        }
                        catch { }
                    }
                    
                    // Build overlay filter if needed
                    string overlayFilter = "";
                    if (cameraSettings != null && recordingStartTime.HasValue && 
                        (cameraSettings.ShowDate || cameraSettings.ShowTime))
                    {
                        overlayFilter = BuildFFmpegDateTimeOverlayFilter(cameraSettings, recordingStartTime.Value, videoHeight);
                    }

                    if (pixelFormat.Contains("bgr") || pixelFormat.Contains("rgb") || pixelFormat.Contains("bgra") || pixelFormat.Contains("rgba"))
                    {
                        // Color format - re-encode to ensure compatibility
                        videoCodec = "-c:v rawvideo -pix_fmt bgr24";
                        LogCameraInfo($"Using re-encoding for color format: {pixelFormat}");
                        
                        // Build video filter chain
                        List<string> filters = new List<string>();
                        if (!string.IsNullOrEmpty(overlayFilter))
                        {
                            filters.Add(overlayFilter);
                        }
                        if (filters.Count > 0)
                        {
                            videoFilter = $"-vf \"{string.Join(",", filters)}\"";
                        }
                    }
                    else
                    {
                        // Grayscale - use stream copy for speed, but need to re-encode if overlay is needed
                        if (!string.IsNullOrEmpty(overlayFilter))
                        {
                            // Need to re-encode to apply overlay
                            videoCodec = "-c:v rawvideo -pix_fmt gray";
                            videoFilter = $"-vf \"{overlayFilter}\"";
                            LogCameraInfo($"Using re-encoding with overlay for grayscale format: {pixelFormat}");
                        }
                        else
                        {
                            videoCodec = "-c:v copy";
                            LogCameraInfo($"Using stream copy for format: {pixelFormat}");
                        }
                    }

                    string ffmpegArgs = $"-fflags +genpts -i \"{aviFile}\" -ss {startTime:F6} -t {duration:F6} {videoCodec} {videoFilter} -r {fps:F3} -avoid_negative_ts make_zero -an -y \"{tempOutputAvi}\"";

                    LogCameraInfo($"FFmpeg command: ffmpeg {ffmpegArgs}");
                    
                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = FFMPEG_PATH,
                        Arguments = ffmpegArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };

                    string lastError = "";
                    
                    using (Process process = Process.Start(startInfo)!)
                    {
                        // Capture error output for debugging
                        process.ErrorDataReceived += (sender, e) =>
                        {
                            if (!string.IsNullOrEmpty(e.Data))
                            {
                                lastError = e.Data;
                                LogCameraInfo($"FFmpeg: {e.Data}");
                            }
                        };
                        process.BeginErrorReadLine();
                        
                        bool exited = process.WaitForExit(300000); // 5 minute timeout per file

                        if (!exited)
                        {
                            process.Kill();
                            errors.Add($"{cameraName}: Timeout (>5 minutes)");
                            LogCameraInfo($"ERROR: Timeout trimming {filename}");
                            
                            // ✅ Clean up temp file
                            if (File.Exists(tempOutputAvi))
                                File.Delete(tempOutputAvi);
                        }
                        else if (process.ExitCode == 0 && File.Exists(tempOutputAvi))  // ✅ CHANGED: Check temp file
                        {
                            FileInfo outputInfo = new FileInfo(tempOutputAvi);  // ✅ CHANGED: Use temp file
                            if (outputInfo.Length > 1000)
                            {
                                // ✅ NEW: Verify the temp output file is readable
                                int outputFrames = GetVideoFrameCount(tempOutputAvi);
                                LogCameraInfo($"Temp output file {Path.GetFileName(tempOutputAvi)}: {outputFrames} frames (expected {totalFrames}), {outputInfo.Length / (1024 * 1024)} MB");
                                
                                // Check if frame count matches expected
                                if (Math.Abs(outputFrames - totalFrames) > 2)
                                {
                                    LogCameraInfo($"WARNING: Frame count mismatch! Expected {totalFrames}, got {outputFrames}");
                                }
                                
                                // ✅ NEW: Delete original input file, then rename temp to final
                                File.Delete(aviFile);
                                File.Move(tempOutputAvi, finalOutputAvi);
                                
                                // Generate timestamp file for the trimmed AVI (after rename, so it matches final filename)
                                if (recordingStartTime.HasValue)
                                {
                                    // Adjust recording start time based on trim start frame
                                    DateTime adjustedStartTime = recordingStartTime.Value.AddSeconds(startTime);
                                    GenerateTimestampFile(finalOutputAvi, adjustedStartTime, fps, i < cameras.Count ? i : null);
                                }
                                
                                successCount++;
                                LogCameraInfo($"Successfully trimmed {filename} -> {Path.GetFileName(finalOutputAvi)}");
                            }
                            else
                            {
                                errors.Add($"{cameraName}: Output file too small (possibly corrupted)");
                                LogCameraInfo($"ERROR: Output file too small for {filename}");
                                
                                // ✅ Clean up temp file
                                if (File.Exists(tempOutputAvi))
                                    File.Delete(tempOutputAvi);
                            }
                        }
                        else
                        {
                            string errorMsg = $"Exit code: {process.ExitCode}";
                            if (!string.IsNullOrEmpty(lastError))
                                errorMsg += $" - {lastError}";
                            
                            errors.Add($"{cameraName}: {errorMsg}");
                            LogCameraInfo($"ERROR: Failed to trim {filename}: {errorMsg}");
                            
                            // ✅ CHANGED: Clean up temp file instead of outputAvi
                            if (File.Exists(tempOutputAvi))
                                File.Delete(tempOutputAvi);
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{cameraName}: {ex.Message}");
                    LogCameraInfo($"EXCEPTION trimming {filename}: {ex.Message}");
                    
                    // ✅ NEW: Clean up temp file on exception
                    try
                    {
                        if (File.Exists(tempOutputAvi))
                            File.Delete(tempOutputAvi);
                    }
                    catch { }
                }

                progressBar.Value = i + 1;
                Application.DoEvents();
            }

            progressForm.Close();

            if (errors.Count > 0)
            {
                string errorList = string.Join("\n", errors.Select(e => $"  • {e}"));
                MessageBox.Show($"Completed with {errors.Count} error(s):\n\n{errorList}\n\nCheck CameraRecording.log on Desktop for details.",
                                "Conversion Complete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                MessageBox.Show($"Successfully saved {successCount} trimmed AVI file(s)!\n\nFiles saved to:\n{outputPath}\n\nNote: These files play in VLC Media Player.",
                                "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        private void ConvertAvisToMp4WithTrimming(List<string> aviFiles, string outputPath, string outputFilename, 
                                  double fps, Dictionary<string, (int startFrame, int endFrame)> frameRanges,
                                  bool deleteOriginals = true, List<string>? customNames = null)
        {
            if (!File.Exists(FFMPEG_PATH))
            {
                MessageBox.Show($"FFmpeg not found at:\n{FFMPEG_PATH}\n\nPlease install FFmpeg.",
                                "FFmpeg Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Create progress form
            Form progressForm = new Form
            {
                Text = "Converting Trimmed Videos to MP4",
                Size = new System.Drawing.Size(500, 220),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ControlBox = false
            };

            Label statusLabel = new Label
            {
                Text = "Preparing conversion...",
                Location = new System.Drawing.Point(20, 20),
                Size = new System.Drawing.Size(460, 20),
                Font = new System.Drawing.Font("Arial", 10, System.Drawing.FontStyle.Bold)
            };
            progressForm.Controls.Add(statusLabel);

            ProgressBar overallProgress = new ProgressBar
            {
                Location = new System.Drawing.Point(20, 50),
                Size = new System.Drawing.Size(460, 30),
                Minimum = 0,
                Maximum = aviFiles.Count * 100,
                Value = 0
            };
            progressForm.Controls.Add(overallProgress);

            Label currentFileLabel = new Label
            {
                Text = "",
                Location = new System.Drawing.Point(20, 90),
                Size = new System.Drawing.Size(460, 20)
            };
            progressForm.Controls.Add(currentFileLabel);

            ProgressBar fileProgress = new ProgressBar
            {
                Location = new System.Drawing.Point(20, 115),
                Size = new System.Drawing.Size(460, 25),
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Style = ProgressBarStyle.Continuous
            };
            progressForm.Controls.Add(fileProgress);

            Label timeLabel = new Label
            {
                Text = "Elapsed: 0s",
                Location = new System.Drawing.Point(20, 150),
                Size = new System.Drawing.Size(460, 20),
                ForeColor = System.Drawing.Color.Gray
            };
            progressForm.Controls.Add(timeLabel);

            progressForm.Show();
            Application.DoEvents();

            int successCount = 0;
            int failCount = 0;
            List<string> convertedFiles = new List<string>();
            List<string> errorMessages = new List<string>();
            DateTime startTime = DateTime.Now;

            for (int i = 0; i < aviFiles.Count; i++)
            {
                string aviFile = aviFiles[i];
                
                if (!File.Exists(aviFile))
                {
                    errorMessages.Add($"{Path.GetFileName(aviFile)}: File not found");
                    overallProgress.Value = (i + 1) * 100;
                    Application.DoEvents();
                    continue;
                }

                string fileName = Path.GetFileName(aviFile);
                string cameraName = (customNames != null && i < customNames.Count) ? customNames[i] : $"Camera{i + 1}";
                // Sanitize the camera name to remove any invalid characters
                cameraName = string.Join("_", cameraName.Split(Path.GetInvalidFileNameChars()));
                string mp4File = Path.Combine(outputPath, $"{outputFilename}_{cameraName}_MP4.mp4");

                statusLabel.Text = $"Converting {i + 1} of {aviFiles.Count}";
                currentFileLabel.Text = $"File: {fileName}";
                fileProgress.Value = 0;
                Application.DoEvents();

                DateTime fileStartTime = DateTime.Now;

                try
                {
                    // Get frame range for this file
                    var range = frameRanges[fileName];
                    int startFrame = range.startFrame;
                    int endFrame = range.endFrame;
                    int totalFrames = endFrame - startFrame + 1;
                    
                    // Calculate time offsets
                    double startTime_sec = (startFrame - 1) / fps;
                    double endTime_sec = endFrame / fps;
                    double duration = endTime_sec - startTime_sec;
                    
                    LogCameraInfo($"Converting {fileName}: frames {startFrame}-{endFrame} ({totalFrames} frames, {duration:F2}s)");

                    statusLabel.Text = $"Converting {i + 1} of {aviFiles.Count} - Frames: {startFrame}-{endFrame}";
                    Application.DoEvents();

                    // Get camera settings for overlay (match by index)
                    CameraSettings? cameraSettings = null;
                    DateTime? recordingStartTime = null;
                    int videoHeight = 480; // Default
                    
                    if (i < cameras.Count)
                    {
                        cameraSettings = cameras[i].Settings;
                        recordingStartTime = cameras[i].RecordingStartTime;
                        
                        // Get video height for font scaling
                        try
                        {
                            using (var capture = new VideoCapture(aviFile))
                            {
                                videoHeight = (int)capture.Get(VideoCaptureProperties.FrameHeight);
                            }
                        }
                        catch { }
                    }
                    
                    // Build overlay filter if needed
                    string overlayFilter = "";
                    if (cameraSettings != null && recordingStartTime.HasValue && 
                        (cameraSettings.ShowDate || cameraSettings.ShowTime))
                    {
                        try
                        {
                            overlayFilter = BuildFFmpegDateTimeOverlayFilter(cameraSettings, recordingStartTime.Value, videoHeight);
                            LogCameraInfo($"Built overlay filter for {cameraName}: {overlayFilter}");
                        }
                        catch (Exception ex)
                        {
                            LogCameraInfo($"Error building overlay filter: {ex.Message}");
                            overlayFilter = ""; // Disable overlay if filter build fails
                        }
                    }
                    
                    // Build video filter chain
                    string videoFilterChain;
                    if (!string.IsNullOrEmpty(overlayFilter))
                    {
                        // Combine FPS filter and overlay filter
                        videoFilterChain = $"setpts=N/(TB*{fps:F6}),{overlayFilter}";
                    }
                    else
                    {
                        // Just FPS filter
                        videoFilterChain = $"setpts=N/(TB*{fps:F6})";
                    }
                    
                    // FFmpeg command with trimming and encoding
                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = FFMPEG_PATH,
                        Arguments = $"-ss {startTime_sec:F6} -i \"{aviFile}\" -t {duration:F6} -vf \"{videoFilterChain}\" -c:v libx264 -pix_fmt yuv420p -preset ultrafast -crf 23 -r {fps:F3} -progress pipe:1 -y \"{mp4File}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    string lastError = "";

                    using (Process process = Process.Start(startInfo)!)
                    {
                        process.OutputDataReceived += (sender, e) =>
                        {
                            if (!string.IsNullOrEmpty(e.Data))
                            {
                                try
                                {
                                    if (e.Data.StartsWith("out_time_ms="))
                                    {
                                        string timeValue = e.Data.Substring(12);
                                        if (long.TryParse(timeValue, out long microseconds) && duration > 0)
                                        {
                                            double currentSeconds = microseconds / 1000000.0;
                                            double progressPercent = Math.Min((currentSeconds / duration) * 100, 100);
                                            
                                            if (fileProgress.InvokeRequired)
                                            {
                                                fileProgress.Invoke(new Action(() =>
                                                {
                                                    fileProgress.Value = (int)progressPercent;
                                                    overallProgress.Value = (i * 100) + (int)progressPercent;
                                                    
                                                    TimeSpan elapsed = DateTime.Now - startTime;
                                                    timeLabel.Text = $"Elapsed: {elapsed.Minutes}m {elapsed.Seconds}s";
                                                }));
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        };

                        process.ErrorDataReceived += (sender, e) =>
                        {
                            if (!string.IsNullOrEmpty(e.Data))
                            {
                                lastError = e.Data;
                            }
                        };

                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();

                        bool exited = process.WaitForExit(300000); // 5 minute timeout

                        if (!exited)
                        {
                            process.Kill();
                            errorMessages.Add($"{fileName}: Conversion timeout (>5 minutes)");
                            failCount++;
                            
                            if (File.Exists(mp4File))
                                File.Delete(mp4File);
                        }
                        else if (process.ExitCode == 0 && File.Exists(mp4File))
                        {
                            FileInfo mp4Info = new FileInfo(mp4File);
                            if (mp4Info.Length > 1000)
                            {
                                fileProgress.Value = 100;
                                overallProgress.Value = (i + 1) * 100;
                                Application.DoEvents();
                                
                                TimeSpan conversionTime = DateTime.Now - fileStartTime;

                                if (deleteOriginals)
                                {
                                    File.Delete(aviFile);
                                }
                                convertedFiles.Add(mp4File);
                                successCount++;

                                // Generate JSON timestamp file for the MP4
                                if (recordingStartTime.HasValue)
                                {
                                    DateTime adjustedStartTime = recordingStartTime.Value.AddSeconds(startTime_sec);
                                    GenerateTimestampFile(mp4File, adjustedStartTime, fps, i < cameras.Count ? i : null);
                                }

                                LogCameraInfo($"Converted {fileName} in {conversionTime.TotalSeconds:F1}s");
                            }
                            else
                            {
                                errorMessages.Add($"{fileName}: Output file too small (possibly corrupted)");
                                File.Delete(mp4File);
                                failCount++;
                            }
                        }
                        else
                        {
                            string errorMsg = $"Exit code: {process.ExitCode}";
                            if (!string.IsNullOrEmpty(lastError))
                                errorMsg += $"\n{lastError}";

                            errorMessages.Add($"{fileName}: {errorMsg}");
                            failCount++;

                            if (File.Exists(mp4File))
                                File.Delete(mp4File);
                        }
                    }
                }
                catch (Exception ex)
                {
                    errorMessages.Add($"{fileName}: Exception - {ex.Message}");
                    failCount++;
                }

                Application.DoEvents();
            }

            progressForm.Close();

            // Show results
            TimeSpan totalTime = DateTime.Now - startTime;
            string resultMessage = $"Total time: {totalTime.Minutes}m {totalTime.Seconds}s\n\n";
            
            if (successCount > 0)
            {
                string fileList = string.Join("\n", convertedFiles.Select(f => 
                {
                    int frames = GetVideoFrameCount(f);
                    double duration = GetVideoDuration(f);
                    double calculatedFps = duration > 0 ? frames / duration : 0;
                    double storedFps = GetVideoFrameRate(f);
                    
                    return $"{Path.GetFileName(f)} - {frames} frames, {duration:F2}s\n   Calculated: {calculatedFps:F2} fps | Stored: {storedFps:F2} fps";
                }));
                
                string deleteMsg = deleteOriginals ? "Original AVI files deleted." : "";
                resultMessage += $"Successfully converted {successCount} trimmed video(s) to MP4!\n\nFinal Videos:\n{fileList}\n\n{deleteMsg}";
            }

            if (failCount > 0)
            {
                if (successCount > 0)
                    resultMessage += "\n\n";
                
                resultMessage += $"Failed to convert {failCount} video(s). AVI files kept.\n\nErrors:\n{string.Join("\n\n", errorMessages)}";
            }

            MessageBox.Show(resultMessage, 
                            failCount > 0 ? "Conversion Complete (with errors)" : "Conversion Complete", 
                            MessageBoxButtons.OK, 
                            failCount > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private void ShowTimelapseCompilationDialog(IReadOnlyList<CameraControl>? groupCameras = null)
        {
            try
            {
                // Check if any cameras have captured frames (limited to this group's cameras if specified)
                var searchCameras = groupCameras ?? cameras;
                var camerasWithFrames = searchCameras.Where(c => c.TimelapseFolder != null && c.TimelapseFrameCount > 0).ToList();
                
                if (camerasWithFrames.Count == 0)
                {
                    MessageBox.Show("No timelapse frames were captured!",
                                "No Frames", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                
                // Create compilation dialog
                Form compilationDialog = new Form
                {
                    Text = "Compile Timelapse Videos",
                    Size = new System.Drawing.Size(550, 450),  // ← Increased size
                    StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false
                };
                
                int y = 20;
                
                // Summary
                Label lblSummary = new Label
                {
                    Text = $"Captured timelapse frames from {camerasWithFrames.Count} camera(s):",
                    Location = new System.Drawing.Point(20, y),
                    Size = new System.Drawing.Size(450, 20),
                    Font = new System.Drawing.Font("Arial", 10, System.Drawing.FontStyle.Bold)
                };
                compilationDialog.Controls.Add(lblSummary);
                y += 30;
                
                // Camera frame counts
                string frameInfo = "";
                foreach (var camera in camerasWithFrames)
                {
                    frameInfo += $"  • {camera.CustomName}: {camera.TimelapseFrameCount} frames\n";
                }
                
                Label lblFrameInfo = new Label
                {
                    Text = frameInfo.TrimEnd(),
                    Location = new System.Drawing.Point(40, y),
                    Size = new System.Drawing.Size(440, camerasWithFrames.Count * 20),
                    Font = new System.Drawing.Font("Arial", 9)
                };
                compilationDialog.Controls.Add(lblFrameInfo);
                y += camerasWithFrames.Count * 20 + 20;
                
                // Output frame rate
                int totalFrames = camerasWithFrames.Max(c => c.TimelapseFrameCount);

                Label lblOutputFps = new Label
                {
                    Text = "Output Video Frame Rate:",
                    Location = new System.Drawing.Point(20, y),
                    Size = new System.Drawing.Size(180, 20),
                    Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
                };
                compilationDialog.Controls.Add(lblOutputFps);

                NumericUpDown numOutputFps = new NumericUpDown
                {
                    Location = new System.Drawing.Point(210, y - 2),
                    Size = new System.Drawing.Size(80, 25),
                    Minimum = 0.1m,
                    Maximum = 120,
                    Value = 24,
                    DecimalPlaces = 1,
                    Increment = 0.5m
                };
                compilationDialog.Controls.Add(numOutputFps);

                Label lblFpsUnit = new Label
                {
                    Text = "fps",
                    Location = new System.Drawing.Point(295, y),
                    Size = new System.Drawing.Size(30, 20),
                    Font = new System.Drawing.Font("Arial", 9)
                };
                compilationDialog.Controls.Add(lblFpsUnit);

                // Duration preview: updates live as fps changes
                Label lblDurationPreview = new Label
                {
                    Text = "",
                    Location = new System.Drawing.Point(335, y),
                    Size = new System.Drawing.Size(180, 20),
                    Font = new System.Drawing.Font("Arial", 8),
                    ForeColor = System.Drawing.Color.Gray
                };
                compilationDialog.Controls.Add(lblDurationPreview);

                Action updateDurationPreview = () =>
                {
                    double fps = (double)numOutputFps.Value;
                    if (fps > 0)
                    {
                        double seconds = totalFrames / fps;
                        string dur = seconds >= 60
                            ? $"{(int)(seconds / 60)}m {seconds % 60:F0}s"
                            : $"{seconds:F1}s";
                        lblDurationPreview.Text = $"≈ {dur} video";
                    }
                };
                numOutputFps.ValueChanged += (s2, e2) => updateDurationPreview();
                updateDurationPreview();

                y += 40;
                
                // Output format
                Label lblFormat = new Label
                {
                    Text = "Output Format:",
                    Location = new System.Drawing.Point(20, y),
                    Size = new System.Drawing.Size(180, 20),
                    Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
                };
                compilationDialog.Controls.Add(lblFormat);
                
                RadioButton rbMP4 = new RadioButton
                {
                    Text = "MP4 (compressed)",
                    Location = new System.Drawing.Point(210, y),
                    Size = new System.Drawing.Size(150, 20),
                    Checked = true
                };
                compilationDialog.Controls.Add(rbMP4);
                
                RadioButton rbAVI = new RadioButton
                {
                    Text = "AVI (uncompressed)",
                    Location = new System.Drawing.Point(210, y + 25),
                    Size = new System.Drawing.Size(150, 20)
                };
                compilationDialog.Controls.Add(rbAVI);
                y += 60;
                
                // Keep source images
                CheckBox chkKeepImages = new CheckBox
                {
                    Text = "Keep source images (don't delete after compilation)",
                    Location = new System.Drawing.Point(20, y),
                    Size = new System.Drawing.Size(400, 20),
                    Font = new System.Drawing.Font("Arial", 9),
                    Checked = false
                };
                compilationDialog.Controls.Add(chkKeepImages);
                y += 40;
                
                // Output location info
                Label lblOutputInfo = new Label
                {
                    Text = $"Videos will be saved to:\n{txtWorkingFolder.Text}",
                    Location = new System.Drawing.Point(20, y),
                    Size = new System.Drawing.Size(450, 40),
                    Font = new System.Drawing.Font("Arial", 8),
                    ForeColor = System.Drawing.Color.Gray
                };
                compilationDialog.Controls.Add(lblOutputInfo);
                y += 50;
                
                // Buttons
                Button btnCancel = new Button
                {
                    Text = "Cancel (Delete Images)",
                    Location = new System.Drawing.Point(220, y),
                    Size = new System.Drawing.Size(150, 35),
                    DialogResult = DialogResult.Cancel
                };
                compilationDialog.Controls.Add(btnCancel);
                
                Button btnCompile = new Button
                {
                    Text = "Compile Videos",
                    Location = new System.Drawing.Point(380, y),
                    Size = new System.Drawing.Size(100, 35),
                    DialogResult = DialogResult.OK,
                    Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
                };
                compilationDialog.Controls.Add(btnCompile);
                
                compilationDialog.AcceptButton = btnCompile;
                compilationDialog.CancelButton = btnCancel;
                
                if (compilationDialog.ShowDialog() == DialogResult.OK)
                {
                    double outputFps = (double)numOutputFps.Value;
                    bool outputMP4 = rbMP4.Checked;
                    bool keepImages = chkKeepImages.Checked;
                    
                    // Compile timelapse videos
                    CompileTimelapseVideos(camerasWithFrames, outputFps, outputMP4, keepImages);
                }
                else
                {
                    // User cancelled - delete all timelapse images
                    DeleteTimelapseImages(camerasWithFrames);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error in timelapse compilation dialog: {ex.Message}",
                            "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                LogCameraInfo($"Timelapse compilation dialog ERROR: {ex.Message}");
            }
        }

        private void CompileTimelapseVideos(List<CameraControl> camerasWithFrames, double outputFps, bool outputMP4, bool keepImages)
        {
            if (!File.Exists(FFMPEG_PATH))
            {
                MessageBox.Show($"FFmpeg not found at:\n{FFMPEG_PATH}\n\nCannot compile timelapse videos.\nSource images will be kept.",
                            "FFmpeg Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            // Create progress form
            Form progressForm = new Form
            {
                Text = "Compiling Timelapse Videos",
                Size = new System.Drawing.Size(500, 200),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ControlBox = false
            };
            
            Label statusLabel = new Label
            {
                Text = "Preparing...",
                Location = new System.Drawing.Point(20, 20),
                Size = new System.Drawing.Size(460, 20),
                Font = new System.Drawing.Font("Arial", 10, System.Drawing.FontStyle.Bold)
            };
            progressForm.Controls.Add(statusLabel);
            
            ProgressBar progressBar = new ProgressBar
            {
                Location = new System.Drawing.Point(20, 50),
                Size = new System.Drawing.Size(460, 30),
                Minimum = 0,
                Maximum = camerasWithFrames.Count,
                Value = 0
            };
            progressForm.Controls.Add(progressBar);
            
            Label detailLabel = new Label
            {
                Text = "",
                Location = new System.Drawing.Point(20, 90),
                Size = new System.Drawing.Size(460, 40),
                Font = new System.Drawing.Font("Arial", 9)
            };
            progressForm.Controls.Add(detailLabel);
            
            progressForm.Show();
            Application.DoEvents();
            
            int successCount = 0;
            List<string> compiledVideos = new List<string>();
            List<string> errors = new List<string>();
            
            for (int i = 0; i < camerasWithFrames.Count; i++)
            {
                var camera = camerasWithFrames[i];
                
                statusLabel.Text = $"Compiling {i + 1} of {camerasWithFrames.Count}: {camera.CustomName}";
                detailLabel.Text = $"Processing {camera.TimelapseFrameCount} frames...";
                Application.DoEvents();
                
                try
                {
                    string cameraName = string.Join("_", camera.CustomName.Split(Path.GetInvalidFileNameChars()));
                    // Derive folder from stored path (set on first frame capture)
                    string timelapseFolder = camera.TimelapseFolder
                        ?? Path.Combine(txtWorkingFolder.Text, $"Timelapse_Frames_{currentRecordingBaseName}", cameraName);

                    // Use image2 demuxer — reads frames directly by filename pattern, no filelist.txt needed.
                    // This handles millions of files efficiently; concat demuxer would require a ~150 MB list file.
                    // Frames are named {baseName}_{cameraName}_%08d.png starting at 00000000.
                    string baseName = camera.TimelapseBaseName ?? currentRecordingBaseName;
                    string framePattern = Path.Combine(timelapseFolder, $"{baseName}_{cameraName}_%08d.png");

                    // Output video path
                    string extension = outputMP4 ? ".mp4" : ".avi";
                    string outputVideo = Path.Combine(txtWorkingFolder.Text, $"{baseName}_{cameraName}_timelapse{extension}");

                    // FFmpeg command
                    string codec = outputMP4
                        ? "-c:v libx264 -pix_fmt yuv420p -preset medium -crf 18"
                        : "-c:v rawvideo -pix_fmt bgr24";

                    string ffmpegArgs = $"-framerate {outputFps:F3} -start_number 0 -i \"{framePattern}\" {codec} -y \"{outputVideo}\"";
                    
                    LogCameraInfo($"Compiling timelapse for {camera.CustomName}: {camera.TimelapseFrameCount} frames at {outputFps} fps");
                    LogCameraInfo($"FFmpeg command: ffmpeg {ffmpegArgs}");
                    
                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = FFMPEG_PATH,
                        Arguments = ffmpegArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };
                    
                    using (Process process = Process.Start(startInfo)!)
                    {
                        process.ErrorDataReceived += (s, e) =>
                        {
                            if (!string.IsNullOrEmpty(e.Data))
                            {
                                LogCameraInfo($"FFmpeg: {e.Data}");
                            }
                        };
                        process.BeginErrorReadLine();
                        
                        bool exited = process.WaitForExit(300000); // 5 minute timeout
                        
                        if (!exited)
                        {
                            process.Kill();
                            errors.Add($"{camera.CustomName}: Compilation timeout");
                            LogCameraInfo($"ERROR: Timeout compiling {camera.CustomName}");
                        }
                        else if (process.ExitCode == 0 && File.Exists(outputVideo))
                        {
                            FileInfo videoInfo = new FileInfo(outputVideo);
                            if (videoInfo.Length > 1000)
                            {
                                compiledVideos.Add(outputVideo);
                                successCount++;
                                LogCameraInfo($"Successfully compiled {camera.CustomName} timelapse: {videoInfo.Length / (1024 * 1024)} MB");
                                
                                // Delete images if requested
                                if (!keepImages)
                                {
                                    try
                                    {
                                        Directory.Delete(timelapseFolder, true);
                                        LogCameraInfo($"Deleted source images for {camera.CustomName}");
                                    }
                                    catch (Exception ex)
                                    {
                                        LogCameraInfo($"Warning: Could not delete images for {camera.CustomName}: {ex.Message}");
                                    }
                                }
                            }
                            else
                            {
                                errors.Add($"{camera.CustomName}: Output file too small");
                                LogCameraInfo($"ERROR: Output file too small for {camera.CustomName}");
                            }
                        }
                        else
                        {
                            errors.Add($"{camera.CustomName}: FFmpeg error (exit code {process.ExitCode})");
                            LogCameraInfo($"ERROR: FFmpeg failed for {camera.CustomName}: exit code {process.ExitCode}");
                        }
                    }
                    
                }
                catch (Exception ex)
                {
                    errors.Add($"{camera.CustomName}: {ex.Message}");
                    LogCameraInfo($"EXCEPTION compiling {camera.CustomName}: {ex.Message}");
                }
                
                progressBar.Value = i + 1;
                Application.DoEvents();
            }

            progressForm.Close();

            // Delete the now-empty parent timelapse folder (per-camera subfolders were already deleted above)
            if (!keepImages)
            {
                try
                {
                    string parentFolder = Path.Combine(txtWorkingFolder.Text, $"Timelapse_Frames_{currentRecordingBaseName}");
                    if (Directory.Exists(parentFolder) && !Directory.EnumerateFileSystemEntries(parentFolder).Any())
                        Directory.Delete(parentFolder);
                }
                catch { }
            }

            // Show results
            string resultMessage = "";
            if (successCount > 0)
            {
                string videoList = string.Join("\n", compiledVideos.Select(v => $"  • {Path.GetFileName(v)}"));
                resultMessage = $"✅ Successfully compiled {successCount} timelapse video(s)!\n\nVideos:\n{videoList}";
                
                if (!keepImages)
                {
                    resultMessage += "\n\nSource images deleted.";
                }
                else
                {
                    resultMessage += "\n\nSource images kept in Timelapse_* folders.";
                }
            }
            
            if (errors.Count > 0)
            {
                if (successCount > 0) resultMessage += "\n\n";
                resultMessage += $"⚠️ Errors ({errors.Count}):\n{string.Join("\n", errors)}";
            }
            
            MessageBox.Show(resultMessage,
                        errors.Count > 0 ? "Compilation Complete (with errors)" : "Compilation Complete",
                        MessageBoxButtons.OK,
                        errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private void DeleteTimelapseImages(List<CameraControl> camerasWithFrames)
        {
            try
            {
                LogCameraInfo("User cancelled timelapse compilation - deleting source images");
                
                foreach (var camera in camerasWithFrames)
                {
                    try
                    {
                        string timelapseMainFolder = Path.Combine(txtWorkingFolder.Text, $"Timelapse_Frames_{currentRecordingBaseName}");

                        if (Directory.Exists(timelapseMainFolder))
                        {
                            Directory.Delete(timelapseMainFolder, true);
                            LogCameraInfo($"Deleted entire timelapse folder: {timelapseMainFolder}");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogCameraInfo($"Error deleting images for {camera.CustomName}: {ex.Message}");
                    }
                }
                
                MessageBox.Show("Timelapse cancelled. Source images deleted.",
                            "Cancelled", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                LogCameraInfo($"Error in DeleteTimelapseImages: {ex.Message}");
            }
        }

        private void ConvertAvisMenuItem_Click(object? sender, EventArgs e)
        {
            try
            {
                // File selection dialog
                OpenFileDialog openDialog = new OpenFileDialog
                {
                    Title = "Select AVI Files to Convert",
                    Filter = "AVI Files (*.avi)|*.avi|All Files (*.*)|*.*",
                    Multiselect = true
                };
                
                if (openDialog.ShowDialog() != DialogResult.OK || openDialog.FileNames.Length == 0)
                    return;
                
                List<string> selectedFiles = openDialog.FileNames.ToList();
                
                // Check for large files
                foreach (string file in selectedFiles)
                {
                    FileInfo fi = new FileInfo(file);
                    if (fi.Length > 1024L * 1024 * 1024) // 1GB
                    {
                        var result = MessageBox.Show(
                            $"Warning: {Path.GetFileName(file)} is very large ({fi.Length / (1024 * 1024)} MB).\n\n" +
                            "Conversion may take several minutes.\n\nContinue?",
                            "Large File Detected",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning);
                        
                        if (result == DialogResult.No)
                            return;
                        
                        break; // Only show warning once
                    }
                }
                
                // Show simple conversion dialog
                Form conversionDialog = new Form
                {
                    Text = "Convert AVIs to MP4",
                    Size = new System.Drawing.Size(450, 220),
                    StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false
                };
                
                int y = 20;
                
                Label lblFiles = new Label
                {
                    Text = $"Selected {selectedFiles.Count} file(s)",
                    Location = new System.Drawing.Point(20, y),
                    Size = new System.Drawing.Size(400, 20),
                    Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
                };
                conversionDialog.Controls.Add(lblFiles);
                y += 30;
                
                Label lblFps = new Label
                {
                    Text = "Frame Rate (fps):",
                    Location = new System.Drawing.Point(20, y),
                    Size = new System.Drawing.Size(120, 20)
                };
                conversionDialog.Controls.Add(lblFps);
                
                TextBox txtFps = new TextBox
                {
                    Text = "30.0",
                    Location = new System.Drawing.Point(150, y - 2),
                    Size = new System.Drawing.Size(100, 25)
                };
                conversionDialog.Controls.Add(txtFps);
                y += 40;
                
                Label lblInfo = new Label
                {
                    Text = "Output: Same folder as input files",
                    Location = new System.Drawing.Point(20, y),
                    Size = new System.Drawing.Size(400, 20),
                    ForeColor = System.Drawing.Color.Gray
                };
                conversionDialog.Controls.Add(lblInfo);
                y += 40;
                
                Button btnCancel = new Button
                {
                    Text = "Cancel",
                    Location = new System.Drawing.Point(220, y),
                    Size = new System.Drawing.Size(90, 35),
                    DialogResult = DialogResult.Cancel
                };
                conversionDialog.Controls.Add(btnCancel);
                
                Button btnConvert = new Button
                {
                    Text = "Convert",
                    Location = new System.Drawing.Point(320, y),
                    Size = new System.Drawing.Size(90, 35),
                    DialogResult = DialogResult.OK
                };
                conversionDialog.Controls.Add(btnConvert);
                
                conversionDialog.AcceptButton = btnConvert;
                conversionDialog.CancelButton = btnCancel;
                
                if (conversionDialog.ShowDialog() == DialogResult.OK)
                {
                    if (!double.TryParse(txtFps.Text, out double fps) || fps < 0.1 || fps > 240)
                    {
                        MessageBox.Show("Please enter a valid frame rate between 0.1 and 240 fps.",
                                        "Invalid FPS", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    
                    // Convert files
                    string outputPath = Path.GetDirectoryName(selectedFiles[0]) ?? "";
                    string dummyFilename = "converted"; // Not used for standalone conversion
                    
                    ConvertAvisToMp4(selectedFiles, outputPath, dummyFilename, fps, deleteOriginals: false, standaloneMode: true);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error in conversion tool: {ex.Message}",
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void TrimVideosMenuItem_Click(object? sender, EventArgs e)
        {
            try
            {
                // File selection dialog
                OpenFileDialog openDialog = new OpenFileDialog
                {
                    Title = "Select Video Files to Trim",
                    Filter = "Video Files (*.avi;*.mp4)|*.avi;*.mp4|AVI Files (*.avi)|*.avi|MP4 Files (*.mp4)|*.mp4|All Files (*.*)|*.*",
                    Multiselect = true
                };
                
                if (openDialog.ShowDialog() != DialogResult.OK || openDialog.FileNames.Length == 0)
                    return;
                
                List<string> selectedFiles = openDialog.FileNames.ToList();
                
                // Validate all files exist and are readable
                foreach (string file in selectedFiles)
                {
                    if (!File.Exists(file))
                    {
                        MessageBox.Show($"File not found: {Path.GetFileName(file)}",
                                        "File Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
                
                // Build frame statistics for each video
                List<(string filename, int frames, double duration)> frameStats = new List<(string, int, double)>();
                List<long> fileSizes = new List<long>();
                List<string> filePaths = new List<string>();
                
                foreach (string file in selectedFiles)
                {
                    int frameCount = GetVideoFrameCount(file);
                    double duration = GetVideoDuration(file);
                    
                    if (frameCount == 0)
                    {
                        MessageBox.Show($"Could not read frame count from: {Path.GetFileName(file)}\n\nThis file may be corrupted or in an unsupported format.",
                                        "Video Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    
                    frameStats.Add((Path.GetFileName(file), frameCount, duration));
                    fileSizes.Add(new FileInfo(file).Length);
                    filePaths.Add(file);
                }
                
                // Initialize frame ranges (all frames by default)
                Dictionary<string, (int startFrame, int endFrame)> frameRanges = new Dictionary<string, (int, int)>();
                foreach (var stat in frameStats)
                {
                    frameRanges[stat.filename] = (1, stat.frames);
                }
                
                // Open trim dialog
                using (VideoTrimDialog trimDialog = new VideoTrimDialog(frameStats, filePaths, frameRanges))
                {
                    if (trimDialog.ShowDialog() != DialogResult.OK)
                        return;
                    
                    // Get the updated frame ranges
                    frameRanges = trimDialog.FrameRanges;
                    
                    // Ask user for output location
                    using (FolderBrowserDialog folderDialog = new FolderBrowserDialog())
                    {
                        folderDialog.Description = "Select output folder for trimmed videos";
                        folderDialog.SelectedPath = Path.GetDirectoryName(selectedFiles[0]) ?? "";
                        
                        if (folderDialog.ShowDialog() != DialogResult.OK)
                            return;
                        
                        string outputPath = folderDialog.SelectedPath;
                        
                        // Ask for output format and naming
                        Form formatDialog = new Form
                        {
                            Text = "Output Options",
                            Size = new System.Drawing.Size(400, 280),
                            StartPosition = FormStartPosition.CenterParent,
                            FormBorderStyle = FormBorderStyle.FixedDialog,
                            MaximizeBox = false,
                            MinimizeBox = false
                        };

                        int dialogY = 20;

                        Label lblFormat = new Label
                        {
                            Text = "Output format:",
                            Location = new System.Drawing.Point(20, dialogY),
                            Size = new System.Drawing.Size(350, 20),
                            Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
                        };
                        formatDialog.Controls.Add(lblFormat);
                        dialogY += 25;

                        RadioButton rbMP4 = new RadioButton
                        {
                            Text = "MP4 (compressed)",
                            Location = new System.Drawing.Point(40, dialogY),
                            Size = new System.Drawing.Size(200, 20),
                            Checked = true
                        };
                        formatDialog.Controls.Add(rbMP4);
                        dialogY += 25;

                        RadioButton rbAVI = new RadioButton
                        {
                            Text = "AVI (original format)",
                            Location = new System.Drawing.Point(40, dialogY),
                            Size = new System.Drawing.Size(200, 20)
                        };
                        formatDialog.Controls.Add(rbAVI);
                        dialogY += 35;

                        // Naming options
                        Label lblNaming = new Label
                        {
                            Text = "Output filename:",
                            Location = new System.Drawing.Point(20, dialogY),
                            Size = new System.Drawing.Size(350, 20),
                            Font = new System.Drawing.Font("Arial", 9, System.Drawing.FontStyle.Bold)
                        };
                        formatDialog.Controls.Add(lblNaming);
                        dialogY += 25;

                        Label lblPrefix = new Label
                        {
                            Text = "Prefix:",
                            Location = new System.Drawing.Point(40, dialogY + 2),
                            Size = new System.Drawing.Size(50, 20)
                        };
                        formatDialog.Controls.Add(lblPrefix);

                        TextBox txtPrefix = new TextBox
                        {
                            Text = "trimmed_",
                            Location = new System.Drawing.Point(95, dialogY),
                            Size = new System.Drawing.Size(100, 25)
                        };
                        formatDialog.Controls.Add(txtPrefix);

                        Label lblOriginal = new Label
                        {
                            Text = "[original name]",
                            Location = new System.Drawing.Point(200, dialogY + 2),
                            Size = new System.Drawing.Size(100, 20),
                            ForeColor = System.Drawing.Color.Gray
                        };
                        formatDialog.Controls.Add(lblOriginal);
                        dialogY += 30;

                        Label lblExample = new Label
                        {
                            Text = "Example: trimmed_video.mp4",
                            Location = new System.Drawing.Point(40, dialogY),
                            Size = new System.Drawing.Size(320, 20),
                            ForeColor = System.Drawing.Color.Blue,
                            Font = new System.Drawing.Font("Arial", 8)
                        };
                        formatDialog.Controls.Add(lblExample);
                        dialogY += 35;

                        Button btnCancel = new Button
                        {
                            Text = "Cancel",
                            Location = new System.Drawing.Point(180, dialogY),
                            Size = new System.Drawing.Size(90, 30),
                            DialogResult = DialogResult.Cancel
                        };
                        formatDialog.Controls.Add(btnCancel);

                        Button btnOK = new Button
                        {
                            Text = "Trim",
                            Location = new System.Drawing.Point(280, dialogY),
                            Size = new System.Drawing.Size(90, 30),
                            DialogResult = DialogResult.OK
                        };
                        formatDialog.Controls.Add(btnOK);

                        formatDialog.AcceptButton = btnOK;
                        formatDialog.CancelButton = btnCancel;

                        if (formatDialog.ShowDialog() != DialogResult.OK)
                            return;

                        bool outputMP4 = rbMP4.Checked;
                        string prefix = txtPrefix.Text;

                        // Perform trimming
                        TrimVideos(filePaths, frameStats, frameRanges, outputPath, outputMP4, prefix);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error in trim tool: {ex.Message}",
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void TrimVideos(List<string> inputFiles, 
                            List<(string filename, int frames, double duration)> frameStats,
                            Dictionary<string, (int startFrame, int endFrame)> frameRanges,
                            string outputPath,
                            bool outputMP4,
                            string prefix = "trimmed_")
        {
            if (!File.Exists(FFMPEG_PATH))
            {
                MessageBox.Show($"FFmpeg not found at:\n{FFMPEG_PATH}\n\nPlease install FFmpeg to trim videos.",
                                "FFmpeg Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            // Create progress form
            Form progressForm = new Form
            {
                Text = "Trimming Videos",
                Size = new System.Drawing.Size(500, 200),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ControlBox = false
            };
            
            Label statusLabel = new Label
            {
                Text = "Preparing...",
                Location = new System.Drawing.Point(20, 20),
                Size = new System.Drawing.Size(460, 20),
                Font = new System.Drawing.Font("Arial", 10, System.Drawing.FontStyle.Bold)
            };
            progressForm.Controls.Add(statusLabel);
            
            ProgressBar progressBar = new ProgressBar
            {
                Location = new System.Drawing.Point(20, 50),
                Size = new System.Drawing.Size(460, 30),
                Minimum = 0,
                Maximum = inputFiles.Count,
                Value = 0
            };
            progressForm.Controls.Add(progressBar);
            
            Label currentFileLabel = new Label
            {
                Text = "",
                Location = new System.Drawing.Point(20, 90),
                Size = new System.Drawing.Size(460, 20)
            };
            progressForm.Controls.Add(currentFileLabel);
            
            Label timeLabel = new Label
            {
                Text = "Elapsed: 0s",
                Location = new System.Drawing.Point(20, 120),
                Size = new System.Drawing.Size(460, 20),
                ForeColor = System.Drawing.Color.Gray
            };
            progressForm.Controls.Add(timeLabel);
            
            progressForm.Show();
            Application.DoEvents();
            
            int successCount = 0;
            List<string> trimmedFiles = new List<string>();
            List<string> errorMessages = new List<string>();
            DateTime startTime = DateTime.Now;
            
            for (int i = 0; i < inputFiles.Count; i++)
            {
                string inputFile = inputFiles[i];
                string filename = frameStats[i].filename;
                var range = frameRanges[filename];
                
                statusLabel.Text = $"Trimming {i + 1} of {inputFiles.Count}";
                currentFileLabel.Text = $"File: {filename} (frames {range.startFrame}-{range.endFrame})";
                Application.DoEvents();
                
                DateTime fileStartTime = DateTime.Now;
                
                try
                {
                    // Calculate time stamps from frame numbers
                    double fps = GetVideoFrameRate(inputFile);
                    if (fps <= 0)
                    {
                        fps = frameStats[i].duration > 0 ? frameStats[i].frames / frameStats[i].duration : 30.0;
                    }
                    
                    double startTime_sec = (range.startFrame - 1) / fps;
                    double endTime_sec = range.endFrame / fps;
                    double duration_sec = endTime_sec - startTime_sec;
                    
                    // Build output filename with prefix
                    string baseName = Path.GetFileNameWithoutExtension(filename);
                    string extension = outputMP4 ? ".mp4" : Path.GetExtension(inputFile);
                    string outputFile = Path.Combine(outputPath, $"{prefix}{baseName}{extension}");
                    
                    // Avoid overwriting
                    int counter = 1;
                    while (File.Exists(outputFile))
                    {
                        outputFile = Path.Combine(outputPath, $"{baseName}_trimmed_{counter}{extension}");
                        counter++;
                    }
                    
                    // Build FFmpeg command
                    string codec = outputMP4 ? "-c:v libx264 -pix_fmt yuv420p -preset fast -crf 18" : "-c:v copy";
                    
                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = FFMPEG_PATH,
                        Arguments = $"-ss {startTime_sec:F6} -i \"{inputFile}\" -t {duration_sec:F6} {codec} -y \"{outputFile}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using (Process process = Process.Start(startInfo)!)
                    {
                        // Read stderr asynchronously to prevent blocking
                        process.BeginErrorReadLine();
                        process.BeginOutputReadLine();
                        
                        process.WaitForExit(120000); // 2 minute timeout
                        
                        if (process.ExitCode == 0 && File.Exists(outputFile))
                        {
                            FileInfo outInfo = new FileInfo(outputFile);
                            if (outInfo.Length > 1000)
                            {
                                trimmedFiles.Add(outputFile);
                                successCount++;
                            }
                            else
                            {
                                errorMessages.Add($"{filename}: Output file too small");
                                if (File.Exists(outputFile))
                                    File.Delete(outputFile);
                            }
                        }
                        else
                        {
                            errorMessages.Add($"{filename}: FFmpeg error (exit code {process.ExitCode})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errorMessages.Add($"{filename}: {ex.Message}");
                }
                
                progressBar.Value = i + 1;
                TimeSpan elapsed = DateTime.Now - startTime;
                timeLabel.Text = $"Elapsed: {elapsed.Minutes}m {elapsed.Seconds}s";
                Application.DoEvents();
            }
            
            progressForm.Close();
            
            // Show results
            string resultMessage = "";
            if (successCount > 0)
            {
                string fileList = string.Join("\n", trimmedFiles.Select(f => $"  • {Path.GetFileName(f)}"));
                resultMessage = $"Successfully trimmed {successCount} video(s)!\n\nOutput files:\n{fileList}";
            }
            
            if (errorMessages.Count > 0)
            {
                if (successCount > 0)
                    resultMessage += "\n\n";
                resultMessage += $"Failed to trim {errorMessages.Count} video(s):\n{string.Join("\n", errorMessages)}";
            }
            
            MessageBox.Show(resultMessage,
                            errorMessages.Count > 0 ? "Trimming Complete (with errors)" : "Trimming Complete",
                            MessageBoxButtons.OK,
                            errorMessages.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private void ConvertAvisToAvi(List<string> aviFiles, string outputPath, string outputFilename, double fps)
        {
            if (!File.Exists(FFMPEG_PATH))
            {
                MessageBox.Show($"FFmpeg not found at:\n{FFMPEG_PATH}\n\nPlease install FFmpeg.",
                                "FFmpeg Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Create progress form (simplified version)
            Form progressForm = new Form
            {
                Text = "Re-encoding AVIs",
                Size = new System.Drawing.Size(400, 150),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ControlBox = false
            };

            Label statusLabel = new Label
            {
                Text = "Processing...",
                Location = new System.Drawing.Point(20, 20),
                Size = new System.Drawing.Size(360, 20),
                Font = new System.Drawing.Font("Arial", 10, System.Drawing.FontStyle.Bold)
            };
            progressForm.Controls.Add(statusLabel);

            ProgressBar progressBar = new ProgressBar
            {
                Location = new System.Drawing.Point(20, 50),
                Size = new System.Drawing.Size(360, 30),
                Minimum = 0,
                Maximum = aviFiles.Count,
                Value = 0
            };
            progressForm.Controls.Add(progressBar);

            progressForm.Show();
            Application.DoEvents();

            int successCount = 0;
            List<string> errors = new List<string>();
            
            for (int i = 0; i < aviFiles.Count; i++)
            {
                string aviFile = aviFiles[i];
                if (!File.Exists(aviFile))
                    continue;

                string outputAvi = Path.Combine(outputPath, $"{outputFilename}_Camera{i + 1}_AVI.avi");
                
                statusLabel.Text = $"Processing {i + 1} of {aviFiles.Count}...";
                Application.DoEvents();

                try
                {
                    // Re-encode with correct FPS metadata using stream copy (fast)
                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = FFMPEG_PATH,
                        Arguments = $"-r {fps:F3} -i \"{aviFile}\" -c:v copy -y \"{outputAvi}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true
                    };

                    using (Process process = Process.Start(startInfo)!)
                    {
                        process.WaitForExit(60000); // 1 minute timeout per file

                        if (process.ExitCode == 0 && File.Exists(outputAvi))
                        {
                            // Delete original timestamp-based AVI
                            File.Delete(aviFile);
                            successCount++;
                        }
                        else
                        {
                            errors.Add($"Camera {i + 1}: Conversion failed");
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Camera {i + 1}: {ex.Message}");
                }

                progressBar.Value = i + 1;
                Application.DoEvents();
            }

            progressForm.Close();

            if (errors.Count > 0)
            {
                MessageBox.Show($"Completed with {errors.Count} error(s):\n\n{string.Join("\n", errors)}",
                                "Conversion Complete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                MessageBox.Show($"Successfully saved {successCount} AVI file(s)!",
                                "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        private void ConvertAvisToMp4(List<string> aviFiles, string? outputPath = null, string? outputFilename = null, 
                                      double? forcedFps = null, bool deleteOriginals = true, bool standaloneMode = false)
        {
            // If parameters not provided, use defaults for backward compatibility
            if (outputPath == null) outputPath = Path.GetDirectoryName(aviFiles[0]) ?? "";
            if (outputFilename == null) outputFilename = "converted";
            
            if (!File.Exists(FFMPEG_PATH))
            {
                MessageBox.Show($"FFmpeg not found at:\n{FFMPEG_PATH}\n\nPlease install FFmpeg to convert videos to MP4.\n\nAVI files have been saved.",
                                "FFmpeg Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Create a progress form
            Form progressForm = new Form
            {
                Text = "Converting to MP4",
                Size = new System.Drawing.Size(500, 220),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ControlBox = false
            };

            Label statusLabel = new Label
            {
                Text = "Preparing conversion...",
                Location = new System.Drawing.Point(20, 20),
                Size = new System.Drawing.Size(460, 20),
                Font = new System.Drawing.Font("Arial", 10, System.Drawing.FontStyle.Bold)
            };
            progressForm.Controls.Add(statusLabel);

            ProgressBar overallProgress = new ProgressBar
            {
                Location = new System.Drawing.Point(20, 50),
                Size = new System.Drawing.Size(460, 30),
                Minimum = 0,
                Maximum = aviFiles.Count * 100,
                Value = 0
            };
            progressForm.Controls.Add(overallProgress);

            Label currentFileLabel = new Label
            {
                Text = "",
                Location = new System.Drawing.Point(20, 90),
                Size = new System.Drawing.Size(460, 20)
            };
            progressForm.Controls.Add(currentFileLabel);

            ProgressBar fileProgress = new ProgressBar
            {
                Location = new System.Drawing.Point(20, 115),
                Size = new System.Drawing.Size(460, 25),
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Style = ProgressBarStyle.Continuous
            };
            progressForm.Controls.Add(fileProgress);

            Label timeLabel = new Label
            {
                Text = "Elapsed: 0s",
                Location = new System.Drawing.Point(20, 150),
                Size = new System.Drawing.Size(460, 20),
                ForeColor = System.Drawing.Color.Gray
            };
            progressForm.Controls.Add(timeLabel);

            progressForm.Show();
            Application.DoEvents();

            int successCount = 0;
            int failCount = 0;
            List<string> convertedFiles = new List<string>();
            List<string> errorMessages = new List<string>();
            DateTime startTime = DateTime.Now;

            for (int i = 0; i < aviFiles.Count; i++)
            {
                string aviFile = aviFiles[i];
                
                if (!File.Exists(aviFile))
                {
                    errorMessages.Add($"{Path.GetFileName(aviFile)}: File not found");
                    overallProgress.Value = (i + 1) * 100;
                    Application.DoEvents();
                    continue;
                }

                string fileName = Path.GetFileName(aviFile);  // Declare once at the top
                string mp4File;
                if (standaloneMode)
                {
                    // Standalone conversion: replace .avi with .mp4 in same location
                    mp4File = Path.ChangeExtension(aviFile, ".mp4");
                }
                else
                {
                    // Recording conversion: use output path and filename
                    string outputFileName = $"{outputFilename}_Camera{i + 1}_MP4.mp4";  // ← Use different variable name
                    mp4File = Path.Combine(outputPath, outputFileName);
                }

                statusLabel.Text = $"Converting {i + 1} of {aviFiles.Count}";
                currentFileLabel.Text = $"File: {fileName}";
                fileProgress.Value = 0;
                Application.DoEvents();

                DateTime fileStartTime = DateTime.Now;

                try
                {
                    // Get video info from AVI
                    int aviFrameCount = GetVideoFrameCount(aviFile);
                    double aviDuration = GetVideoDuration(aviFile);
                    double aviStoredFrameRate = GetVideoFrameRate(aviFile); // What the AVI claims
                    FileInfo aviInfo = new FileInfo(aviFile);
                    long aviSize = aviInfo.Length;

                    // Use forced FPS if provided, otherwise calculate
                    double actualFps;
                    if (forcedFps.HasValue)
                    {
                        actualFps = forcedFps.Value;
                    }
                    else if (i < cameras.Count && cameras[i].RecordingStartTime.HasValue && cameras[i].RecordingStopTime.HasValue)
                    {
                        // Calculate from actual recording time
                        double recordingDuration = (cameras[i].RecordingStopTime!.Value - cameras[i].RecordingStartTime!.Value).TotalSeconds;
                        actualFps = recordingDuration > 0 ? aviFrameCount / recordingDuration : 11.5;
                    }
                    else if (aviDuration > 0)
                    {
                        // Fallback: use AVI duration
                        actualFps = aviFrameCount / aviDuration;
                    }
                    else
                    {
                        actualFps = 30.0; // Final fallback
                    }
                    
                    LogCameraInfo($"Converting {fileName}: {aviFrameCount} frames");
                    LogCameraInfo($"  AVI says: {aviDuration:F2}s @ {(aviDuration > 0 ? aviFrameCount/aviDuration : 0):F2}fps (stored rate: {aviStoredFrameRate:F2}fps)");
                    LogCameraInfo($"  Forcing actual: {actualFps:F2} fps");

                    statusLabel.Text = $"Converting {i + 1} of {aviFiles.Count} - Size: {aviSize / (1024 * 1024)}MB, FPS: {actualFps:F2}";
                    Application.DoEvents();

                    // FFmpeg command - use setpts filter to completely rebuild timestamps from scratch
                    // This ignores all input timing and creates perfect constant frame rate output
                    string fpsFilter = $"setpts=N/(TB*{actualFps:F6})";
                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = FFMPEG_PATH,
                        Arguments = $"-i \"{aviFile}\" -vf \"{fpsFilter}\" -c:v libx264 -pix_fmt yuv420p -preset ultrafast -crf 23 -r {actualFps:F3} -progress pipe:1 -y \"{mp4File}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    string lastError = "";

                    using (Process process = Process.Start(startInfo)!)
                    {
                        // Read output for progress
                        process.OutputDataReceived += (sender, e) =>
                        {
                            if (!string.IsNullOrEmpty(e.Data))
                            {
                                try
                                {
                                    if (e.Data.StartsWith("out_time_ms="))
                                    {
                                        string timeValue = e.Data.Substring(12);
                                        if (long.TryParse(timeValue, out long microseconds) && aviDuration > 0)
                                        {
                                            double currentSeconds = microseconds / 1000000.0;
                                            double progressPercent = Math.Min((currentSeconds / aviDuration) * 100, 100);
                                            
                                            if (fileProgress.InvokeRequired)
                                            {
                                                fileProgress.Invoke(new Action(() =>
                                                {
                                                    fileProgress.Value = (int)progressPercent;
                                                    overallProgress.Value = (i * 100) + (int)progressPercent;
                                                    
                                                    TimeSpan elapsed = DateTime.Now - startTime;
                                                    timeLabel.Text = $"Elapsed: {elapsed.Minutes}m {elapsed.Seconds}s";
                                                }));
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        };

                        process.ErrorDataReceived += (sender, e) =>
                        {
                            if (!string.IsNullOrEmpty(e.Data))
                            {
                                lastError = e.Data;
                            }
                        };

                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();

                        bool exited = process.WaitForExit(300000); // 5 minute timeout

                        if (!exited)
                        {
                            process.Kill();
                            errorMessages.Add($"{fileName}: Conversion timeout (>5 minutes)");
                            failCount++;
                            
                            if (File.Exists(mp4File))
                                File.Delete(mp4File);
                        }
                        else if (process.ExitCode == 0 && File.Exists(mp4File))
                        {
                            FileInfo mp4Info = new FileInfo(mp4File);
                            if (mp4Info.Length > 1000)
                            {
                                fileProgress.Value = 100;
                                overallProgress.Value = (i + 1) * 100;
                                Application.DoEvents();
                                
                                TimeSpan conversionTime = DateTime.Now - fileStartTime;
                                
                                if (deleteOriginals)
                                {
                                    File.Delete(aviFile);
                                }
                                convertedFiles.Add(mp4File);
                                successCount++;
                                
                                LogCameraInfo($"Converted {fileName} in {conversionTime.TotalSeconds:F1}s");
                            }
                            else
                            {
                                errorMessages.Add($"{fileName}: Output file too small (possibly corrupted)");
                                File.Delete(mp4File);
                                failCount++;
                            }
                        }
                        else
                        {
                            string errorMsg = $"Exit code: {process.ExitCode}";
                            if (!string.IsNullOrEmpty(lastError))
                                errorMsg += $"\n{lastError}";
                            
                            errorMessages.Add($"{fileName}: {errorMsg}");
                            failCount++;
                            
                            if (File.Exists(mp4File))
                                File.Delete(mp4File);
                        }
                    }
                }
                catch (Exception ex)
                {
                    errorMessages.Add($"{fileName}: Exception - {ex.Message}");
                    failCount++;
                }

                Application.DoEvents();
            }

            progressForm.Close();

            // Show results with both calculated and stored frame rates
            TimeSpan totalTime = DateTime.Now - startTime;
            string resultMessage = $"Total time: {totalTime.Minutes}m {totalTime.Seconds}s\n\n";
            
            if (successCount > 0)
            {
                string fileList = string.Join("\n", convertedFiles.Select(f => 
                {
                    int frames = GetVideoFrameCount(f);
                    double duration = GetVideoDuration(f);
                    double calculatedFps = duration > 0 ? frames / duration : 0;
                    double storedFps = GetVideoFrameRate(f);
                    
                    return $"{Path.GetFileName(f)} - {frames} frames, {duration:F2}s\n   Calculated: {calculatedFps:F2} fps | Stored: {storedFps:F2} fps";
                }));
                resultMessage += $"Successfully converted {successCount} video(s) to MP4!\n\nFinal Videos:\n{fileList}\n\nOriginal AVI files deleted.";
            }

            if (failCount > 0)
            {
                if (successCount > 0)
                    resultMessage += "\n\n";
                
                resultMessage += $"Failed to convert {failCount} video(s). AVI files kept.\n\nErrors:\n{string.Join("\n\n", errorMessages)}";
            }

            MessageBox.Show(resultMessage, 
                            failCount > 0 ? "Conversion Complete (with errors)" : "Conversion Complete", 
                            MessageBoxButtons.OK, 
                            failCount > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private void LogCameraInfo(string message)
        {
            string logFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "CameraRecording.log");
            try
            {
                File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\n");
            }
            catch { }
        }

        private void MonitorDiskWriteSpeed()
        {
            // Always check disk space, not just when recording
            CheckDiskSpace();

            if (!isRecording)
                return;

            long totalBytes = 0;
            foreach (var camera in cameras)
            {
                if (camera.RecordingFilePath != null && File.Exists(camera.RecordingFilePath))
                {
                    try
                    {
                        FileInfo fileInfo = new FileInfo(camera.RecordingFilePath);
                        totalBytes += fileInfo.Length;
                    }
                    catch { }
                }
            }

            if (lastTotalBytesWritten > 0)
            {
                long bytesPerSecond = totalBytes - lastTotalBytesWritten;
                double mbPerSecond = bytesPerSecond / (1024.0 * 1024.0);
                LogCameraInfo($"Disk write speed: {mbPerSecond:F2} MB/s, Total written: {totalBytes / (1024 * 1024)} MB");
                
                if (mbPerSecond < 50 && mbPerSecond > 0)
                {
                    LogCameraInfo($"WARNING: Low disk write speed detected: {mbPerSecond:F2} MB/s");
                }
            }

            lastTotalBytesWritten = totalBytes;
        }

        private void CheckDiskSpace()
        {
            try
            {
                if (!IsHandleCreated) return;   // called during start-up, before the window exists
                string workingFolder = txtWorkingFolder.Text;
                if (string.IsNullOrEmpty(workingFolder) || !Directory.Exists(workingFolder))
                    return;

                DriveInfo driveInfo = new DriveInfo(Path.GetPathRoot(workingFolder)!);
                if (!driveInfo.IsReady)
                    return;

                long freeSpaceBytes = driveInfo.AvailableFreeSpace;
                long totalSpaceBytes = driveInfo.TotalSize;
                double freeSpaceGB = freeSpaceBytes / (1024.0 * 1024.0 * 1024.0);
                double totalSpaceGB = totalSpaceBytes / (1024.0 * 1024.0 * 1024.0);
                double usedPercent = ((totalSpaceBytes - freeSpaceBytes) / (double)totalSpaceBytes) * 100.0;

                // Update disk space label
                if (lblDiskSpace != null)
                {
                    this.Invoke(new Action(() =>
                    {
                        lblDiskSpace.Text = $"Disk Space: {freeSpaceGB:F1} GB free [{usedPercent:F1}% used]";
                        if (usedPercent > 90)
                        {
                            lblDiskSpace.ForeColor = System.Drawing.Color.Red;
                        }
                        else if (usedPercent > 75)
                        {
                            lblDiskSpace.ForeColor = System.Drawing.Color.Orange;
                        }
                        else
                        {
                            lblDiskSpace.ForeColor = System.Drawing.Color.Gray;
                        }
                    }));
                }

                // Check thresholds
                if (isRecording)
                {
                    // Critical: Less than 1 GB free - auto-stop
                    if (freeSpaceGB < 1.0)
                    {
                        this.Invoke(new Action(() =>
                        {
                            BtnStopRecording_Click(null, EventArgs.Empty);
                            MessageBox.Show(
                                $"⚠️ CRITICAL: Disk space is critically low ({freeSpaceGB:F2} GB free)!\n\n" +
                                "Recording has been automatically stopped to prevent data loss.\n\n" +
                                "Please free up disk space before recording again.",
                                "Disk Full - Recording Stopped",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Error);
                        }));
                        LogCameraInfo($"CRITICAL: Disk space critically low ({freeSpaceGB:F2} GB) - auto-stopped recording");
                    }
                    // Warning: Less than 5 GB free - show warning once
                    else if (freeSpaceGB < 5.0 && !diskSpaceWarningShown)
                    {
                        diskSpaceWarningShown = true;
                        this.Invoke(new Action(() =>
                        {
                            MessageBox.Show(
                                $"⚠️ WARNING: Disk space is running low!\n\n" +
                                $"Free space: {freeSpaceGB:F2} GB\n" +
                                $"Used: {usedPercent:F1}%\n\n" +
                                "Recording will automatically stop if space drops below 1 GB.\n\n" +
                                "Consider freeing up disk space soon.",
                                "Low Disk Space Warning",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                        }));
                        LogCameraInfo($"WARNING: Low disk space ({freeSpaceGB:F2} GB free)");
                    }
                }
                else
                {
                    // Reset warning flag when not recording
                    if (freeSpaceGB >= 5.0)
                    {
                        diskSpaceWarningShown = false;
                    }
                }
            }
            catch (Exception ex)
            {
                LogCameraInfo($"Error checking disk space: {ex.Message}");
            }
        }
        private string GetVideoCodecTag(string videoFile)
        {
            try
            {
                string ffprobePath = Path.Combine(Path.GetDirectoryName(FFMPEG_PATH)!, "ffprobe.exe");
                
                if (!File.Exists(ffprobePath))
                    return "";

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=codec_tag_string -of default=noprint_wrappers=1:nokey=1 \"{videoFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                using (Process process = Process.Start(startInfo)!)
                {
                    string output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit();

                    if (!string.IsNullOrEmpty(output))
                    {
                        LogCameraInfo($"Detected codec tag for {Path.GetFileName(videoFile)}: {output}");
                        return output;
                    }
                }
            }
            catch (Exception ex)
            {
                LogCameraInfo($"Error detecting codec tag: {ex.Message}");
            }

            return "";
        }
        private string GetVideoPixelFormat(string videoFile)
        {
            try
            {
                string ffprobePath = Path.Combine(Path.GetDirectoryName(FFMPEG_PATH)!, "ffprobe.exe");
                
                if (!File.Exists(ffprobePath))
                    return ""; // Will let FFmpeg auto-detect

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=pix_fmt -of default=noprint_wrappers=1:nokey=1 \"{videoFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                using (Process process = Process.Start(startInfo)!)
                {
                    string output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit();

                    if (!string.IsNullOrEmpty(output))
                    {
                        LogCameraInfo($"Detected pixel format for {Path.GetFileName(videoFile)}: {output}");
                        return output;
                    }
                }
            }
            catch (Exception ex)
            {
                LogCameraInfo($"Error detecting pixel format: {ex.Message}");
            }

            return ""; // Empty means auto-detect
        }


        // Reads the SDK's own frame-drop counters for the running stream and logs them. Call it
        // BEFORE LiveStop - the counters belong to the live run. The driver breakdown says WHERE
        // frames were lost: in the camera (trigger skipped), on USB (packet loss), in format
        // conversion (transforms), or because the application/sink queue was full.
        // Stores the total in camera.DroppedFrames (-1 = counters not available).
        private void LogFrameDropCounters(CameraControl camera, int number)
        {
            var ic = camera.ImagingControl;
            camera.DroppedFrames = -1;
            try
            {
                long dropped = ic.DeviceCountOfFramesDropped;
                long notDropped = ic.DeviceCountOfFramesNotDropped;
                camera.DroppedFrames = (int)dropped;
                LogCameraInfo($"Camera {number} SDK counters: not dropped {notDropped}, dropped {dropped}");
            }
            catch (Exception ex)
            {
                LogCameraInfo($"Camera {number}: device drop counters not available: {ex.Message}");
            }
            try
            {
                var d = ic.DriverFrameDropInformation;
                LogCameraInfo($"Camera {number} driver: delivered {d.FramesDelivered}, " +
                              $"dropped in device {d.FramesDroppedInDevice}, " +
                              $"packet loss {d.FramesDroppedDueToPacketLoss}, " +
                              $"transforms {d.FramesDroppedDueToTransforms}, " +
                              $"application queue {d.FramesDroppedDueToApplicationQueue}, " +
                              $"unspecified {d.FramesDroppedDueUnspecified}");
            }
            catch (Exception ex)
            {
                LogCameraInfo($"Camera {number}: driver drop information not available: {ex.Message}");
            }
        }

        // Puts a camera into external-trigger mode (external = true) or free-running mode.
        // External: trigger OFF -> re-apply the video format (the driver builds its frame-rate list
        // when the format is set, and builds a reduced list - max 90 fps here - if the trigger is on
        // at that moment) -> set the MAXIMUM frame rate -> trigger ON. The camera skips any trigger
        // that arrives faster than its internal frame rate, so that rate must be above the box rate.
        private void ConfigureCameraTrigger(CameraControl camera, bool external)
        {
            var ic = camera.ImagingControl;
            VCDSwitchProperty? sw = null;
            // The property list can be empty for a moment right after a camera (re)connects: retry.
            for (int attempt = 0; attempt < 10 && sw == null; attempt++)
            {
                try { sw = FindTriggerEnableSwitch(ic); }
                catch (Exception ex) { if (attempt == 9) LogCameraInfo($"Camera {camera.DeviceName}: trigger switch lookup failed: {ex.Message}"); }
                if (sw == null) System.Threading.Thread.Sleep(100);
            }
            if (sw == null)
                LogCameraInfo($"WARNING: Camera {camera.DeviceName}: trigger property not found (device valid = {SafeDeviceValid(ic)}) - " +
                              "camera is NOT triggered and will free-run. Unplug/replug it or press Refresh Cameras.");

            if (!external)
            {
                try { if (sw != null && sw.Switch && !sw.ReadOnly) sw.Switch = false; }
                catch (Exception ex) { LogCameraInfo($"Camera {camera.DeviceName}: could not switch trigger off: {ex.Message}"); }
                try
                {
                    if (ic.DeviceFrameRateAvailable && camera.Settings.SoftwareFrameRate > 0)
                    {
                        float[] rates = ic.DeviceFrameRates;
                        float target = camera.Settings.SoftwareFrameRate;
                        ic.DeviceFrameRate = (rates != null && rates.Length > 0)
                            ? rates.OrderBy(r => Math.Abs(r - target)).First() : target;
                    }
                }
                catch (Exception ex) { LogCameraInfo($"Camera {camera.DeviceName}: could not set software frame rate: {ex.Message}"); }
                camera.HardwareTriggerEnabledByApp = false;
                LogCameraInfo($"Camera {camera.DeviceName}: SOFTWARE CONTROLLED - trigger OFF, device frame rate {SafeRate(ic):F1} fps");
                UpdateTriggerButton(camera);
                return;
            }

            bool wasOn = false;
            try { if (sw != null && sw.Switch && !sw.ReadOnly) { wasOn = true; sw.Switch = false; } }
            catch (Exception ex) { LogCameraInfo($"Camera {camera.DeviceName}: could not switch trigger off: {ex.Message}"); }

            float listMaxBefore = MaxRate(ic);
            try
            {
                if (!ic.LiveVideoRunning)
                {
                    var fmt = ic.VideoFormat;
                    if (fmt != null) ic.VideoFormat = fmt;   // rebuild the frame-rate list with trigger OFF
                }
                else LogCameraInfo($"Camera {camera.DeviceName}: live view running - video format not re-applied");
            }
            catch (Exception ex) { LogCameraInfo($"Camera {camera.DeviceName}: could not re-apply video format: {ex.Message}"); }

            float listMax = MaxRate(ic);
            try { if (ic.DeviceFrameRateAvailable && listMax > 0) ic.DeviceFrameRate = listMax; }
            catch (Exception ex) { LogCameraInfo($"Camera {camera.DeviceName}: could not set max frame rate: {ex.Message}"); }

            try
            {
                if (sw == null) sw = FindTriggerEnableSwitch(ic);
                if (sw != null && !sw.ReadOnly) sw.Switch = true;
            }
            catch (Exception ex) { LogCameraInfo($"Camera {camera.DeviceName}: could not arm trigger: {ex.Message}"); }

            camera.HardwareTriggerEnabledByApp = sw?.Switch == true;
            LogCameraInfo($"Camera {camera.DeviceName}: EXTERNAL TRIGGER - frame-rate list max {listMaxBefore:F1} -> {listMax:F1} fps " +
                          $"after re-applying format with trigger off; device frame rate {SafeRate(ic):F1} fps; " +
                          $"camera trigger ARMED = {sw?.Switch} (was {(wasOn ? "on" : "off")})");
            UpdateTriggerButton(camera);
        }

        // The camera offers several formats; their frame-rate limits differ a lot (a full-sensor
        // scaled/binned mode can be capped near 90 fps while a 640x480 region runs at 300+ fps).
        // With the trigger OFF, try every format with the same colour type and resolution as the
        // chosen one, log each one's maximum frame rate, and keep the fastest.
        private void SelectFastestFormat(CameraControl camera)
        {
            var ic = camera.ImagingControl;
            try
            {
                if (ic.LiveVideoRunning || ic.VideoFormats == null) return;
                VideoFormat? current = ic.VideoFormatCurrent;
                if (current == null)
                {
                    string want = ic.VideoFormat?.ToString() ?? camera.Settings?.Format ?? "";
                    current = ic.VideoFormats.FirstOrDefault(f => f.ToString() == want);
                }
                if (current == null) return;

                var sw = FindTriggerEnableSwitch(ic);
                bool trigWasOn = false;
                try { if (sw != null && sw.Switch && !sw.ReadOnly) { trigWasOn = true; sw.Switch = false; } } catch { }

                string res = $"{current.Width}x{current.Height}";
                string type = current.ToString().Split(' ')[0];
                LogCameraInfo($"Camera {camera.DeviceName}: format scan (trigger off) - current '{current}' list max {MaxRate(ic):F1} fps; {ic.VideoFormats.Length} formats offered");

                VideoFormat? best = current;
                float bestMax = -1f;
                foreach (var f in ic.VideoFormats)
                {
                    string name = f.ToString();
                    bool sameKind = name.StartsWith(type) && f.Width == current.Width && f.Height == current.Height;
                    if (!sameKind && !name.Contains(res)) continue;
                    try
                    {
                        ic.VideoFormat = f;
                        float m = MaxRate(ic);
                        LogCameraInfo($"    '{name}'  {f.Width}x{f.Height}  max {m:F1} fps{(sameKind ? "" : "  (other type, not chosen)")}");
                        if (sameKind && m > bestMax) { bestMax = m; best = f; }
                    }
                    catch (Exception ex) { LogCameraInfo($"    '{name}' could not be set: {ex.Message}"); }
                }
                if (best != null) ic.VideoFormat = best;
                LogCameraInfo($"Camera {camera.DeviceName}: using '{ic.VideoFormat}' - frame-rate list {SafeMin(ic):F1} - {MaxRate(ic):F1} fps");
                if (ic.VideoFormats.All(f => f.Width <= 640 && f.Height <= 480))
                    LogCameraInfo($"WARNING: Camera {camera.DeviceName} offers no format larger than 640x480 - it is probably connected at USB 2.0 speed " +
                                  "(cable not fully seated, USB 2.0 cable/extension/adapter, or USB 2.0 port). Frame rate will be capped near 90 fps.");

                try { if (trigWasOn && sw != null) sw.Switch = true; } catch { }
            }
            catch (Exception ex) { LogCameraInfo($"Camera {camera.DeviceName}: format scan failed: {ex.Message}"); }
        }

        private static float SafeMin(ICImagingControl ic)
        {
            try { var r = ic.DeviceFrameRates; return (r != null && r.Length > 0) ? r.Min() : 0f; }
            catch { return 0f; }
        }

        private static bool SafeDeviceValid(ICImagingControl ic)
        {
            try { return ic.DeviceValid; } catch { return false; }
        }

        private static float MaxRate(ICImagingControl ic)
        {
            try { var r = ic.DeviceFrameRates; return (r != null && r.Length > 0) ? r.Max() : 0f; }
            catch { return 0f; }
        }

        private static float SafeRate(ICImagingControl ic)
        {
            try { return ic.DeviceFrameRate; } catch { return 0f; }
        }

        // Shows the camera's REAL trigger state on its Trigger button.
        private void UpdateTriggerButton(CameraControl camera)
        {
            try
            {
                if (camera.TriggerButton == null || !camera.IsImagingSource) return;
                bool on = FindTriggerEnableSwitch(camera.ImagingControl)?.Switch == true;
                void apply()
                {
                    camera.TriggerButton.Text = on ? "● Trigger ON" : "○ Trigger OFF";
                    camera.TriggerButton.ForeColor = on ? System.Drawing.Color.DarkGreen : System.Drawing.Color.DimGray;
                }
                if (camera.TriggerButton.IsHandleCreated && camera.TriggerButton.InvokeRequired)
                    camera.TriggerButton.BeginInvoke((Action)apply);
                else
                    apply();
            }
            catch { }
        }

        // Finds the camera's trigger "Enable" switch the same way the Trigger button does
        // (by property name), which works on the DMK 23UP1300.
        private static VCDSwitchProperty? FindTriggerEnableSwitch(ICImagingControl ic)
        {
            if (ic.VCDPropertyItems == null) return null;
            foreach (VCDPropertyItem item in ic.VCDPropertyItems)
            {
                if ((item.Name ?? "").IndexOf("Trigger", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (VCDPropertyElement element in item.Elements)
                {
                    string elName = element.Name ?? "";
                    if (!elName.Equals("Enable", StringComparison.OrdinalIgnoreCase) &&
                        !elName.Equals("Value", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (VCDPropertyInterface itf in element)
                    {
                        if (itf.InterfaceGUID == VCDGUIDs.VCDInterface_Switch)
                            return (VCDSwitchProperty)itf;
                    }
                }
            }
            return null;
        }

        private void UpdateRecordToggle()
        {
            if (btnRecordToggle == null) return;
            bool recording = btnStopRecording.Enabled;
            btnRecordToggle.Text = recording ? "Stop Recording" : "Start Recording";
            btnRecordToggle.BackColor = recording ? System.Drawing.Color.IndianRed : System.Drawing.SystemColors.Control;
            btnRecordToggle.UseVisualStyleBackColor = !recording;
            btnRecordToggle.Enabled = recording || btnStartRecording.Enabled;
        }

        private void UpdateLiveToggle()
        {
            if (btnLiveToggle == null) return;
            bool live = btnStopLive.Enabled;
            btnLiveToggle.Text = live ? "Stop All Live" : "Start All Live";
            btnLiveToggle.Enabled = live || btnStartLive.Enabled;
        }

        // Light preview while recording with the SDK live display off (the live display made the
        // cameras stall one after another when triggering started). The recorder hands over a
        // half-size copy of a recorded frame 5 times a second (writer thread; in loop mode the
        // copy is made right after a frame is stored); it is shown in a PictureBox over the camera
        // view, with the buffer bar along its top.
        private void ShowRecordingPreview(CameraControl camera)
        {
            var recorder = camera.QueuedRecorder;
            var parent = camera.ImagingControl.Parent;
            if (recorder == null || parent == null) return;

            var pb = new PictureBox
            {
                Bounds = camera.ImagingControl.Bounds,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = System.Drawing.Color.Black
            };
            parent.Controls.Add(pb);
            pb.BringToFront();
            camera.RecordingPreview = pb;

            // Buffer bar along the top of the preview (like StreamPix): green < 50 %, orange < 80 %,
            // red above that or as soon as any frame was lost. White tick = highest fill so far.
            pb.Paint += (s, e) =>
            {
                int pool = recorder.PoolFrames;
                if (pool <= 0) return;
                double fill = Math.Min(1.0, (double)recorder.QueueDepth / pool);
                double peak = Math.Min(1.0, (double)recorder.MaxQueueDepth / pool);
                long lost = recorder.FramesLost;
                int w = pb.ClientSize.Width, h = 18;
                var g = e.Graphics;
                g.FillRectangle(System.Drawing.Brushes.DimGray, 0, 0, w, h);
                // loop recording: the buffer is meant to fill up (it keeps the last N frames)
                var barColor = lost > 0 ? System.Drawing.Color.Red
                             : recorder.IsLoop ? System.Drawing.Color.SteelBlue
                             : fill >= 0.8 ? System.Drawing.Color.Red
                             : fill >= 0.5 ? System.Drawing.Color.Orange
                             : System.Drawing.Color.ForestGreen;
                using (var brush = new System.Drawing.SolidBrush(barColor))
                    g.FillRectangle(brush, 0, 0, Math.Max(2, (int)(w * fill)), h);
                int px = (int)(w * peak);
                if (!recorder.IsLoop) g.DrawLine(System.Drawing.Pens.White, px, 0, px, h - 1);
                string text = (recorder.IsLoop
                                  ? $"Loop {recorder.QueueDepth} of {pool} frames   "
                                  : $"Buffer {fill:P0} (peak {peak:P0})   {recorder.FramesReceived} frames   ") +
                              (lost > 0 ? $"LOST {lost}" : "lost 0");
                TextRenderer.DrawText(g, text, this.Font, new System.Drawing.Rectangle(4, 0, w - 4, h),
                                      System.Drawing.Color.White,
                                      TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            };
            var barTimer = new System.Windows.Forms.Timer { Interval = 250 };
            barTimer.Tick += (s, e) => { if (!pb.IsDisposed) pb.Invalidate(); };
            barTimer.Start();
            pb.Tag = barTimer;

            recorder.PreviewFrame = bmp =>
            {
                try
                {
                    if (pb.IsDisposed || !pb.IsHandleCreated) { bmp.Dispose(); return; }
                    pb.BeginInvoke(new Action(() =>
                    {
                        if (pb.IsDisposed) { bmp.Dispose(); return; }
                        pb.Bounds = camera.ImagingControl.Bounds;   // follow layout changes
                        var old = pb.Image;
                        pb.Image = bmp;
                        old?.Dispose();
                    }));
                }
                catch { bmp.Dispose(); }
            };
        }

        private void RemoveRecordingPreview(CameraControl camera)
        {
            if (camera.QueuedRecorder != null) camera.QueuedRecorder.PreviewFrame = null;
            var pb = camera.RecordingPreview;
            camera.RecordingPreview = null;
            if (pb == null) return;
            if (pb.Tag is System.Windows.Forms.Timer barTimer) { barTimer.Stop(); barTimer.Dispose(); }
            pb.Parent?.Controls.Remove(pb);
            pb.Image?.Dispose();
            pb.Dispose();
        }

        private void ToggleCameraExpansion(int cameraIndex)
        {
            LogCameraInfo($"Live view clicked: camera {cameraIndex + 1} (expanded camera was {expandedCameraIndex + 1})");
            // Debounce: a single physical click on a TIS camera can generate both
            // WM_PARENTNOTIFY and WM_LBUTTONDOWN, which would fire this method twice
            // and immediately undo the expansion.  Ignore calls within 500 ms of the
            // last accepted toggle to absorb the duplicate message.
            long now = Environment.TickCount64;
            if (now - _lastExpansionToggleTick < 500)
                return;
            _lastExpansionToggleTick = now;

            if (expandedCameraIndex == cameraIndex)
            {
                // Already expanded, collapse back to grid
                expandedCameraIndex = -1;
            }
            else
            {
                // Expand this camera
                expandedCameraIndex = cameraIndex;
            }

            UpdateCameraLayout();
        }

        private void CollapseExpandedCamera()
        {
            if (expandedCameraIndex >= 0)
            {
                expandedCameraIndex = -1;
                UpdateCameraLayout();
            }
        }

        private double GetVideoDuration(string videoFile)
        {
            try
            {
                string ffprobePath = Path.Combine(Path.GetDirectoryName(FFMPEG_PATH)!, "ffprobe.exe");
                
                if (!File.Exists(ffprobePath))
                    return 0;

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };

                using (Process process = Process.Start(startInfo)!)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();

                    if (double.TryParse(output.Trim(), out double duration))
                    {
                        return duration;
                    }
                }
            }
            catch { }

            return 0;
        }

        // -------------------------------------------------------------------------
        // NativeWindow hook that detects left-clicks on a TIS ImagingControl.
        //
        // The ICImagingControl renders into a native child HWND (DirectShow VMR/EVR).
        // That child owns WM_LBUTTONDOWN so WinForms Click/MouseClick never fire.
        // We attach this hook to the ImagingControl's own HWND and listen for
        //   • WM_LBUTTONDOWN  – fires if the top-level ImagingControl HWND gets the click
        //   • WM_PARENTNOTIFY – fires when the rendering child HWND gets a click and
        //                       forwards WM_LBUTTONDOWN notification up via DefWindowProc
        // -------------------------------------------------------------------------
        internal class ImagingControlClickHook : NativeWindow
        {
            private const int WM_LBUTTONDOWN  = 0x0201;
            private const int WM_PARENTNOTIFY = 0x0210;

            private readonly Action _onLeftClick;

            private Control? _control;

            public ImagingControlClickHook(Control control, Action onLeftClick)
            {
                _onLeftClick = onLeftClick;
                _control = control;
                // Stay subscribed: the SDK/WinForms can recreate the control's window handle
                // (e.g. after LiveStart/LiveStop or a layout change). Without re-attaching, the
                // hook silently stops seeing clicks and the live view no longer expands.
                control.HandleCreated += OnHandleCreated;
                control.HandleDestroyed += OnHandleDestroyed;
                if (control.IsHandleCreated)
                    AssignHandle(control.Handle);
            }

            private void OnHandleCreated(object? sender, EventArgs e)
            {
                if (sender is Control c)
                {
                    if (Handle != IntPtr.Zero) ReleaseHandle();
                    AssignHandle(c.Handle);
                }
            }

            private void OnHandleDestroyed(object? sender, EventArgs e)
            {
                if (Handle != IntPtr.Zero) ReleaseHandle();
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg == WM_LBUTTONDOWN ||
                    (m.Msg == WM_PARENTNOTIFY && (m.WParam.ToInt32() & 0xFFFF) == WM_LBUTTONDOWN))
                {
                    _onLeftClick();
                }
            }

            public void Detach()
            {
                if (_control != null)
                {
                    _control.HandleCreated -= OnHandleCreated;
                    _control.HandleDestroyed -= OnHandleDestroyed;
                    _control = null;
                }
                if (Handle != IntPtr.Zero) ReleaseHandle();
            }
        }
    }
}