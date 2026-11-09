using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QueenPix
{
    /// <summary>
    /// Tools → Verify recording: decodes every frame of the selected camera videos with ffmpeg and
    /// checks frame counts (file header, decoded, box pulse count), decode errors, repeated
    /// (identical) frames and black frames. If the box flashes on every other pulse, it also checks
    /// the bright/dark alternation frame by frame (a missing or repeated frame breaks it) and
    /// whether all cameras show the same sequence (= frame n is the same pulse in every camera).
    /// The report is shown and saved next to the first video as &lt;name&gt;_verify.txt.
    /// </summary>
    internal sealed class VerifyRecordingDialog : Form
    {
        private readonly string _ffmpeg, _ffprobe;
        private readonly NumericUpDown _numBoxCount;
        private readonly Button _btnSelect;
        private readonly TextBox _txtReport;
        private readonly Label _lblStatus;

        public VerifyRecordingDialog(string ffmpegPath)
        {
            _ffmpeg = ffmpegPath;
            _ffprobe = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");

            Text = "Verify recording";
            Width = 950;
            Height = 650;
            StartPosition = FormStartPosition.CenterParent;

            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6), WrapContents = false };
            top.Controls.Add(new Label { Text = "Box pulse count (OLED, 0 = unknown):", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
            _numBoxCount = new NumericUpDown { Minimum = 0, Maximum = 100_000_000, Width = 100 };
            top.Controls.Add(_numBoxCount);
            _btnSelect = new Button { Text = "Select videos and verify...", AutoSize = true };
            _btnSelect.Click += async (s, e) => await SelectAndVerifyAsync();
            top.Controls.Add(_btnSelect);
            _lblStatus = new Label { AutoSize = true, Margin = new Padding(10, 8, 3, 3) };
            top.Controls.Add(_lblStatus);

            _txtReport = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                ReadOnly = true,
                WordWrap = false,
                Font = new System.Drawing.Font(System.Drawing.FontFamily.GenericMonospace, 9f)
            };
            Controls.Add(_txtReport);
            Controls.Add(top);
        }

        private async Task SelectAndVerifyAsync()
        {
            using var dlg = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "Videos (*.avi;*.mp4)|*.avi;*.mp4|All files (*.*)|*.*",
                Title = "Select the camera videos of ONE recording"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.FileNames.Length == 0) return;
            if (!File.Exists(_ffmpeg))
            {
                MessageBox.Show(this, $"ffmpeg not found:\n{_ffmpeg}", "Verify recording", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string[] files = dlg.FileNames.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            long boxCount = (long)_numBoxCount.Value;
            _btnSelect.Enabled = false;
            _txtReport.Clear();
            IProgress<string> progress = new Progress<string>(t => _lblStatus.Text = t);
            try
            {
                string report = await Task.Run(() => Verify(files, boxCount, progress));
                _txtReport.Text = report.Replace("\n", "\r\n");
                string path = Path.Combine(Path.GetDirectoryName(files[0])!,
                                           Path.GetFileNameWithoutExtension(files[0]) + "_verify.txt");
                File.WriteAllText(path, report);
                _lblStatus.Text = "Report saved: " + path;
            }
            catch (Exception ex)
            {
                _txtReport.Text = "ERROR: " + ex;
                _lblStatus.Text = "Verification failed";
            }
            finally
            {
                _btnSelect.Enabled = true;
            }
        }

        private sealed class FileResult
        {
            public string Path = "";
            public string Codec = "", PixFmt = "", FrameRate = "";
            public int Width, Height;
            public long HeaderFrames = -1;
            public int Decoded;
            public readonly List<double> Means = new List<double>();
            public readonly List<int> Repeated = new List<int>();   // frame index identical to the previous frame
            public int Black;
            public string DecodeErrors = "";
            public int[]? Classes;                                  // 1 = bright, 0 = dark (flash pattern)
            public double Separation;
            public readonly List<int> Breaks = new List<int>();     // index where alternation breaks
            public bool PatternFound;
        }

        private string Verify(string[] files, long boxCount, IProgress<string> progress)
        {
            var results = new List<FileResult>();
            foreach (string f in files)
            {
                var r = new FileResult { Path = f };
                progress.Report($"{Path.GetFileName(f)}: reading header");
                Probe(r);
                Decode(r, progress);
                AnalyseFlashPattern(r);
                results.Add(r);
            }
            return BuildReport(results, boxCount);
        }

        private void Probe(FileResult r)
        {
            if (!File.Exists(_ffprobe)) return;
            string output = Run(_ffprobe,
                $"-v error -select_streams v:0 -show_entries stream=codec_name,pix_fmt,width,height,r_frame_rate,nb_frames " +
                $"-of default=noprint_wrappers=1 \"{r.Path}\"");
            foreach (string line in output.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim(), val = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "codec_name": r.Codec = val; break;
                    case "pix_fmt": r.PixFmt = val; break;
                    case "r_frame_rate": r.FrameRate = val; break;
                    case "width": int.TryParse(val, out r.Width); break;
                    case "height": int.TryParse(val, out r.Height); break;
                    case "nb_frames": if (long.TryParse(val, out long n)) r.HeaderFrames = n; break;
                }
            }
        }

        private static string Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            string err = "";
            var errTask = Task.Run(() => err = p.StandardError.ReadToEnd());
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            errTask.Wait();
            return output;
        }

        // Decodes every frame to 8-bit gray and checks each one.
        private void Decode(FileResult r, IProgress<string> progress)
        {
            if (r.Width <= 0 || r.Height <= 0)
            {
                r.DecodeErrors = "could not read the image size (ffprobe missing or file unreadable)";
                return;
            }
            int frameBytes = r.Width * r.Height;
            var psi = new ProcessStartInfo(_ffmpeg,
                $"-v error -i \"{r.Path}\" -map 0:v:0 -f rawvideo -pix_fmt gray -")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            var stderr = new StringBuilder();
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
            p.BeginErrorReadLine();

            Stream stdout = p.StandardOutput.BaseStream;
            byte[] cur = new byte[frameBytes], prev = new byte[frameBytes];
            bool havePrev = false;
            string name = Path.GetFileName(r.Path);
            while (true)
            {
                int got = ReadFull(stdout, cur, frameBytes);
                if (got == 0) break;
                if (got < frameBytes)
                {
                    lock (stderr) stderr.AppendLine($"incomplete last frame ({got} of {frameBytes} bytes)");
                    break;
                }
                long sum = 0;
                for (int k = 0; k < frameBytes; k++) sum += cur[k];
                double mean = sum / (double)frameBytes;
                r.Means.Add(mean);
                if (mean < 2.0) r.Black++;
                if (havePrev && cur.AsSpan().SequenceEqual(prev)) r.Repeated.Add(r.Decoded);
                (cur, prev) = (prev, cur);
                havePrev = true;
                r.Decoded++;
                if (r.Decoded % 250 == 0) progress.Report($"{name}: {r.Decoded} frames checked");
            }
            p.WaitForExit();
            lock (stderr) r.DecodeErrors = stderr.ToString().Trim();
            if (p.ExitCode != 0 && r.DecodeErrors.Length == 0) r.DecodeErrors = $"ffmpeg exit code {p.ExitCode}";
        }

        private static int ReadFull(Stream s, byte[] buf, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buf, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        // Two clearly separated brightness levels (Otsu split of the frame means) that alternate =
        // flash on every other pulse. Each place where two neighbouring frames have the same level
        // is a break (a missing or repeated frame).
        private static void AnalyseFlashPattern(FileResult r)
        {
            int n = r.Means.Count;
            if (n < 20) return;
            double[] sorted = r.Means.OrderBy(x => x).ToArray();
            double total = sorted.Sum(), cum = 0, best = -1;
            int bestK = -1;
            for (int k = 1; k < n; k++)
            {
                cum += sorted[k - 1];
                double w0 = k / (double)n, w1 = 1 - w0;
                double mu0 = cum / k, mu1 = (total - cum) / (n - k);
                double between = w0 * w1 * (mu1 - mu0) * (mu1 - mu0);
                if (between > best) { best = between; bestK = k; }
            }
            if (bestK < 1) return;
            double threshold = (sorted[bestK - 1] + sorted[bestK]) / 2;
            var lo = r.Means.Where(m => m <= threshold).ToArray();
            var hi = r.Means.Where(m => m > threshold).ToArray();
            if (lo.Length < n / 10 || hi.Length < n / 10) return;
            double sdLo = StdDev(lo), sdHi = StdDev(hi);
            r.Separation = (hi.Average() - lo.Average()) / (sdLo + sdHi + 1e-9);
            if (r.Separation < 4) return;

            r.Classes = r.Means.Select(m => m > threshold ? 1 : 0).ToArray();
            for (int k = 1; k < n; k++)
                if (r.Classes[k] == r.Classes[k - 1]) r.Breaks.Add(k);
            r.PatternFound = r.Breaks.Count < n / 5;   // otherwise two levels, but not alternating
        }

        private static double StdDev(double[] v)
        {
            if (v.Length < 2) return 0;
            double m = v.Average();
            return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Length - 1));
        }

        private static string ListText(IEnumerable<int> items, int max = 30)
        {
            var all = items.ToList();
            string s = string.Join(", ", all.Take(max));
            return all.Count > max ? s + $", ... ({all.Count} in total)" : s;
        }

        private static string BuildReport(List<FileResult> results, long boxCount)
        {
            var sb = new StringBuilder();
            var inv = CultureInfo.InvariantCulture;
            sb.AppendLine($"QueenPix recording verification - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(boxCount > 0 ? $"Box pulse count: {boxCount}" : "Box pulse count: not given");
            sb.AppendLine();

            bool allOk = true;
            foreach (var r in results)
            {
                var problems = new List<string>();
                sb.AppendLine(Path.GetFileName(r.Path));
                sb.AppendLine($"  format: {r.Codec} {r.PixFmt} {r.Width}x{r.Height}, frame rate {r.FrameRate}");
                sb.AppendLine($"  frames: header {(r.HeaderFrames >= 0 ? r.HeaderFrames.ToString(inv) : "?")}, decoded {r.Decoded}");
                if (r.HeaderFrames >= 0 && r.HeaderFrames != r.Decoded)
                    problems.Add($"header says {r.HeaderFrames} frames, {r.Decoded} decoded");
                if (boxCount > 0 && Math.Abs(r.Decoded - boxCount) > 1)
                    problems.Add($"{r.Decoded - boxCount:+#;-#;0} frames vs box count");
                if (r.DecodeErrors.Length > 0)
                    problems.Add("decode errors: " + r.DecodeErrors.Replace(Environment.NewLine, " | "));
                if (r.Repeated.Count > 0)
                    problems.Add($"{r.Repeated.Count} frame(s) identical to the previous one at {ListText(r.Repeated)}");
                if (r.Black > 0)
                    problems.Add($"{r.Black} black frame(s) (mean brightness < 2)");
                if (r.Means.Count > 0)
                    sb.AppendLine(string.Format(inv, "  brightness: mean {0:F1}, min {1:F1}, max {2:F1}",
                        r.Means.Average(), r.Means.Min(), r.Means.Max()));

                if (r.PatternFound)
                {
                    sb.AppendLine(string.Format(inv, "  flash pattern: alternating bright/dark found (separation {0:F1})", r.Separation));
                    if (r.Breaks.Count > 0)
                        problems.Add($"flash pattern breaks (missing or repeated frame) at frame {ListText(r.Breaks)}");
                }
                else if (r.Classes != null)
                    sb.AppendLine($"  flash pattern: two brightness levels, but not alternating ({r.Breaks.Count} repeats)");
                else
                    sb.AppendLine("  flash pattern: none (flash off, or on every pulse)");

                if (problems.Count == 0) sb.AppendLine("  RESULT: OK");
                else
                {
                    allOk = false;
                    foreach (string p in problems) sb.AppendLine("  PROBLEM: " + p);
                }
                sb.AppendLine();
            }

            // across cameras
            if (results.Count > 1)
            {
                sb.AppendLine("Across cameras");
                var counts = results.Select(r => r.Decoded).Distinct().ToList();
                if (counts.Count == 1)
                    sb.AppendLine($"  all {results.Count} videos have {counts[0]} frames");
                else
                {
                    allOk = false;
                    sb.AppendLine("  PROBLEM: frame counts differ: " +
                                  string.Join(", ", results.Select(r => $"{Path.GetFileName(r.Path)} {r.Decoded}")));
                }

                var withPattern = results.Where(r => r.PatternFound && r.Classes != null).ToList();
                if (withPattern.Count > 1)
                {
                    var refR = withPattern[0];
                    foreach (var r in withPattern.Skip(1))
                    {
                        int len = Math.Min(refR.Classes!.Length, r.Classes!.Length);
                        int firstDiff = -1;
                        for (int k = 0; k < len; k++)
                            if (refR.Classes[k] != r.Classes[k]) { firstDiff = k; break; }
                        if (firstDiff < 0 && refR.Classes.Length == r.Classes.Length)
                            sb.AppendLine($"  {Path.GetFileName(r.Path)}: same flash sequence as {Path.GetFileName(refR.Path)} - frames aligned");
                        else
                        {
                            allOk = false;
                            sb.AppendLine($"  PROBLEM: {Path.GetFileName(r.Path)} flash sequence differs from " +
                                          $"{Path.GetFileName(refR.Path)}" +
                                          (firstDiff >= 0 ? $" from frame {firstDiff} on" : " in length"));
                        }
                    }
                }
                else
                {
                    sb.AppendLine("  frame-by-frame alignment: not checked (needs the alternating flash in every video)");
                }
                sb.AppendLine();
            }

            sb.AppendLine(allOk ? "OVERALL: OK - no problems found" : "OVERALL: PROBLEMS FOUND (see above)");
            return sb.ToString();
        }
    }
}
