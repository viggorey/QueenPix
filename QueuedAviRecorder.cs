using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TIS.Imaging;

namespace QueenPix
{
    /// <summary>
    /// Records one camera to an uncompressed Y800 AVI without letting the disk hold up the camera.
    ///
    /// Why: a triggered camera that cannot hand over its frame ignores the next trigger silently
    /// (no SDK counter sees it). With MediaStreamSink the AVI is written in the delivery path, so
    /// every disk/file-cache hiccup cost frames in all cameras at once (6 Nov 2026: ~9 % with four
    /// cameras; 0 % with the same set-up when nothing was written).
    ///
    /// How: the FrameQueueSink callback only copies each frame into a pre-allocated RAM pool and
    /// returns the SDK buffer at once; a writer thread saves frames from the pool to disk. A disk
    /// pause only makes the queue longer. If the pool ever runs out, the frame is counted as lost
    /// (never silent). Arrival times are kept for the per-camera timeline in the log.
    /// </summary>
    internal sealed class QueuedAviRecorder
    {
        private readonly string _path;
        private readonly string _label;
        private readonly double _fps;
        private readonly Action<string> _log;
        private readonly int _width, _height, _frameBytes;
        private readonly byte[][] _pool;
        private readonly ConcurrentQueue<int> _free = new ConcurrentQueue<int>();
        private readonly BlockingCollection<int> _filled = new BlockingCollection<int>(new ConcurrentQueue<int>());
        private readonly Thread _writer;
        private readonly long[] _arrivalTicks = new long[2_000_000];   // ~2.5 h at 218 Hz
        private int _arrivals;

        private bool _formatChecked, _formatOk, _bottomUp;
        private long _framesQueued, _framesWritten, _lostNoBuffer, _lostBadFormat, _lostWriteError;
        private int _maxQueueDepth;
        private volatile Exception? _writerError;
        private volatile Exception? _callbackError;
        private bool _finished;
        private double _finalFps;
        private long _lastPreviewTicks;

        public FrameQueueSink Sink { get; }

        /// <summary>
        /// Light preview while recording: every PreviewIntervalMs the writer thread hands over a
        /// half-size copy of a frame it has just written (never the camera's thread). The receiver
        /// owns the bitmap and must dispose it. Null = no preview.
        /// </summary>
        public Action<System.Drawing.Bitmap>? PreviewFrame { get; set; }
        public int PreviewIntervalMs { get; set; } = 500;
        public long FramesWritten => Interlocked.Read(ref _framesWritten);
        public long FramesLost => Interlocked.Read(ref _lostNoBuffer) + Interlocked.Read(ref _lostBadFormat)
                                  + Interlocked.Read(ref _lostWriteError);
        public int PoolFrames => _pool.Length;
        public int QueueDepth => _filled.Count;                  // frames waiting to be written
        public int MaxQueueDepth => _maxQueueDepth;
        public long FramesReceived => Interlocked.Read(ref _framesQueued);

        public QueuedAviRecorder(string path, string label, int width, int height, double fps, int bufferMB,
                                 Action<string> log)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException($"{label}: invalid image size {width}x{height}");
            _path = path;
            _label = label;
            _width = width;
            _height = height;
            _frameBytes = width * height;            // Y800 = 1 byte per pixel
            _fps = fps > 0 ? fps : 200;
            _log = log;

            int poolFrames = (int)Math.Max(100, (long)bufferMB * 1024 * 1024 / _frameBytes);
            _pool = new byte[poolFrames][];
            for (int k = 0; k < poolFrames; k++)
            {
                _pool[k] = new byte[_frameBytes];
                _free.Enqueue(k);
            }

            Sink = new FrameQueueSink(OnFrameQueued, MediaSubtypes.Y800, 32);

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = $"AVI writer {label}",
                Priority = ThreadPriority.AboveNormal
            };
            _writer.Start();

            _log($"{_label}: buffered recording ready - RAM buffer {poolFrames} frames ({bufferMB} MB), " +
                 $"{width}x{height} Y800");
        }

        // Runs on the SDK's sink thread for every frame. Must never wait for anything.
        private FrameQueuedResult OnFrameQueued(IFrameQueueBuffer buf)
        {
            long now = Stopwatch.GetTimestamp();
            int a = _arrivals;
            if (a < _arrivalTicks.Length) { _arrivalTicks[a] = now; _arrivals = a + 1; }

            try
            {
                if (!_formatChecked)
                {
                    _formatChecked = true;
                    FrameType ft = buf.FrameType;
                    _bottomUp = ft.IsBottomUp;
                    _formatOk = ft.Width == _width && ft.Height == _height && ft.BytesPerLine == _width;
                    if (!_formatOk)
                        _log($"{_label}: ERROR - camera delivers {ft.Width}x{ft.Height} ({ft.BytesPerLine} bytes/line), " +
                             $"recorder expects {_width}x{_height} Y800. Frames are NOT recorded.");
                }
                if (!_formatOk)
                {
                    Interlocked.Increment(ref _lostBadFormat);
                    return FrameQueuedResult.ReQueue;
                }
                if (!_free.TryDequeue(out int idx))
                {
                    Interlocked.Increment(ref _lostNoBuffer);
                    return FrameQueuedResult.ReQueue;
                }

                IntPtr src = buf.GetIntPtr();
                byte[] dst = _pool[idx];
                if (_bottomUp)
                {
                    // store top-down (AVI with FourCC Y800 is read top-down)
                    for (int r = 0; r < _height; r++)
                        Marshal.Copy(src + (_height - 1 - r) * _width, dst, r * _width, _width);
                }
                else
                {
                    Marshal.Copy(src, dst, 0, _frameBytes);
                }

                _filled.Add(idx);
                Interlocked.Increment(ref _framesQueued);
                int depth = _filled.Count;
                if (depth > _maxQueueDepth) _maxQueueDepth = depth;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _lostBadFormat);
                if (_callbackError == null) _callbackError = ex;
            }
            return FrameQueuedResult.ReQueue;
        }

        private void WriterLoop()
        {
            AviWriter? avi = null;
            try
            {
                foreach (int idx in _filled.GetConsumingEnumerable())
                {
                    if (_writerError == null)
                    {
                        try
                        {
                            avi ??= new AviWriter(_path, _width, _height, _fps);
                            avi.WriteFrame(_pool[idx], _frameBytes);
                            Interlocked.Increment(ref _framesWritten);
                            MaybeSendPreview(_pool[idx]);
                        }
                        catch (Exception ex)
                        {
                            _writerError = ex;
                            Interlocked.Increment(ref _lostWriteError);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref _lostWriteError);
                    }
                    _free.Enqueue(idx);
                }
            }
            finally
            {
                try { avi?.Close(_finalFps); }
                catch (Exception ex) { if (_writerError == null) _writerError = ex; }
            }
        }

        private void MaybeSendPreview(byte[] frame)
        {
            var receiver = PreviewFrame;
            if (receiver == null) return;
            long now = Stopwatch.GetTimestamp();
            if (now - _lastPreviewTicks < PreviewIntervalMs * Stopwatch.Frequency / 1000) return;
            _lastPreviewTicks = now;
            try { receiver(MakePreviewBitmap(frame)); } catch { /* preview must never stop recording */ }
        }

        // Half-size 8-bit grayscale bitmap of a (top-down) frame.
        private System.Drawing.Bitmap MakePreviewBitmap(byte[] frame)
        {
            int w = _width / 2, h = _height / 2;
            var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format8bppIndexed);
            System.Drawing.Imaging.ColorPalette pal = bmp.Palette;
            for (int k = 0; k < 256; k++) pal.Entries[k] = System.Drawing.Color.FromArgb(k, k, k);
            bmp.Palette = pal;
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                    System.Drawing.Imaging.PixelFormat.Format8bppIndexed);
            try
            {
                var row = new byte[w];
                for (int y = 0; y < h; y++)
                {
                    int src = 2 * y * _width;
                    for (int x = 0; x < w; x++) row[x] = frame[src + 2 * x];
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, w);
                }
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        /// <summary>Call after LiveStop: writes the remaining queue, closes the AVI, logs the result.</summary>
        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            PreviewFrame = null;
            double measured = MeasuredFps();
            _finalFps = measured > 0 ? measured : _fps;   // AVI header gets the real trigger rate
            _filled.CompleteAdding();
            _writer.Join();

            long sinkDropped = 0;
            try { sinkDropped = Sink.CountOfFramesDropped; } catch { }
            _log($"{_label}: buffered recording - received {Interlocked.Read(ref _framesQueued)}, " +
                 $"written {FramesWritten}, max queue {_maxQueueDepth} frames ({_maxQueueDepth / _finalFps:F2} s; " +
                 $"buffer {_pool.Length}), lost: buffer full {Interlocked.Read(ref _lostNoBuffer)}, " +
                 $"format {Interlocked.Read(ref _lostBadFormat)}, write error {Interlocked.Read(ref _lostWriteError)}, " +
                 $"sink dropped {sinkDropped}");
            if (_writerError != null)
                _log($"{_label}: RECORDING ERROR (writer) - {_writerError.GetType().Name}: {_writerError.Message}");
            if (_callbackError != null)
                _log($"{_label}: RECORDING ERROR (frame copy) - {_callbackError.GetType().Name}: {_callbackError.Message}");

            // release the RAM buffer
            while (_free.TryDequeue(out _)) { }
            for (int k = 0; k < _pool.Length; k++) _pool[k] = null!;
        }

        public void DisposeSink()
        {
            try { Sink.Dispose(); } catch { }
        }

        private double MedianInterval()
        {
            int n = _arrivals;
            if (n < 3) return 0;
            var d = new long[n - 1];
            for (int k = 1; k < n; k++) d[k - 1] = _arrivalTicks[k] - _arrivalTicks[k - 1];
            Array.Sort(d);
            return d[d.Length / 2] / (double)Stopwatch.Frequency;
        }

        /// <summary>Trigger rate from the arrival times (median interval), 0 if unknown.</summary>
        public double MeasuredFps()
        {
            double med = MedianInterval();
            return med > 0 ? 1.0 / med : 0;
        }

        /// <summary>
        /// Logs frames per second, gaps (missed triggers) and the estimated total missed.
        /// Returns the number of triggers missed inside gaps (0 = every trigger gave a frame).
        /// </summary>
        public long LogTimeline(string label)
        {
            int n = _arrivals;
            double med = MedianInterval();
            if (n < 10 || med <= 0)
            {
                _log($"{label} timeline: too few frames ({n})");
                return 0;
            }
            double f = Stopwatch.Frequency;
            long nowTicks = Stopwatch.GetTimestamp();
            DateTime now = DateTime.Now;
            string Clock(long t) => now.AddSeconds((t - nowTicks) / f).ToString("HH:mm:ss.fff");

            long t0 = _arrivalTicks[0];
            var counts = new List<int>();
            int idx = 0;
            for (int s = 1; ; s++)
            {
                long boundary = t0 + (long)(s * f);
                if (boundary > _arrivalTicks[n - 1]) break;
                int c = 0;
                while (idx < n && _arrivalTicks[idx] < boundary) { idx++; c++; }
                counts.Add(c);
            }
            int rest = n - idx;

            // gap = at least two trigger periods without a frame (>= 1 missed trigger, robust to jitter)
            var gaps = new List<string>();
            long missedInGaps = 0;
            for (int k = 1; k < n; k++)
            {
                double dt = (_arrivalTicks[k] - _arrivalTicks[k - 1]) / f;
                if (dt > 1.8 * med)
                {
                    long missed = (long)Math.Round(dt / med) - 1;
                    missedInGaps += missed;
                    if (gaps.Count < 40) gaps.Add($"{Clock(_arrivalTicks[k - 1])} +{dt * 1000:F0} ms (~{missed})");
                }
            }
            double span = (_arrivalTicks[n - 1] - t0) / f;
            long expected = (long)Math.Round(span / med) + 1;

            _log($"{label} frames per second from {Clock(t0)}: {string.Join(" ", counts)} | rest {rest}");
            string gapText = gaps.Count == 0 ? "no gaps" : "gaps: " + string.Join(", ", gaps);
            if (gaps.Count >= 40) gapText += ", ...";
            _log($"{label}: {n} frames over {span:F2} s at {1 / med:F2} fps -> ~{Math.Max(0, expected - n)} missed triggers " +
                 $"between first and last frame; {gapText}");
            return missedInGaps;
        }
    }

    /// <summary>
    /// Minimal OpenDML (AVI 2.0) writer for uncompressed 8-bit Y800 video. RIFF segments of up to
    /// 1 GiB ('AVI ' then 'AVIX'), a standard index ('ix00') per segment, a super index ('indx'),
    /// 'dmlh' total frames, and a legacy 'idx1' for the first segment.
    /// </summary>
    internal sealed class AviWriter
    {
        private const long MaxRiffBytes = (1L << 30) - (64L << 20);   // 1 GiB minus room for the indexes
        private const int SuperIndexEntries = 256;                    // up to ~256 GB per file

        private readonly FileStream _fs;
        private readonly BinaryWriter _w;
        private readonly int _frameBytes;

        private long _riffSizePos, _moviSizePos, _moviFourccPos;
        private readonly long _avihTotalFramesPos, _strhLengthPos, _dmlhTotalFramesPos, _indxPayloadPos;
        private readonly long _avihUsPerFramePos, _strhRatePos;
        private const uint RateScale = 1000;
        private readonly List<long> _segDataOffsets = new List<long>(4096);
        private readonly List<(long pos, uint size, uint frames)> _superEntries = new List<(long, uint, uint)>();
        private int _segment;
        private uint _totalFrames, _firstRiffFrames;
        private bool _closed;

        public AviWriter(string path, int width, int height, double fps)
        {
            _frameBytes = width * height;
            uint scale = RateScale;
            uint rate = (uint)Math.Round(fps * scale);

            _fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 22);
            _w = new BinaryWriter(_fs, Encoding.ASCII, leaveOpen: true);

            FourCC("RIFF"); _riffSizePos = Pos; U32(0); FourCC("AVI ");

            FourCC("LIST"); long hdrl = Pos; U32(0); FourCC("hdrl");

            FourCC("avih"); U32(56);
            _avihUsPerFramePos = Pos;
            U32((uint)Math.Round(1e6 / fps));                         // dwMicroSecPerFrame
            U32((uint)Math.Min(uint.MaxValue, _frameBytes * fps));    // dwMaxBytesPerSec
            U32(0);                                                   // dwPaddingGranularity
            U32(0x10);                                                // AVIF_HASINDEX
            _avihTotalFramesPos = Pos; U32(0);                        // dwTotalFrames (first RIFF)
            U32(0);                                                   // dwInitialFrames
            U32(1);                                                   // dwStreams
            U32((uint)(_frameBytes + 8));                             // dwSuggestedBufferSize
            U32((uint)width); U32((uint)height);
            U32(0); U32(0); U32(0); U32(0);

            FourCC("LIST"); long strl = Pos; U32(0); FourCC("strl");

            FourCC("strh"); U32(56);
            FourCC("vids"); FourCC("Y800");
            U32(0);                                                   // dwFlags
            U16(0); U16(0);                                           // wPriority, wLanguage
            U32(0);                                                   // dwInitialFrames
            U32(scale); _strhRatePos = Pos; U32(rate);
            U32(0);                                                   // dwStart
            _strhLengthPos = Pos; U32(0);                             // dwLength (all frames)
            U32((uint)(_frameBytes + 8));                             // dwSuggestedBufferSize
            U32(0xFFFFFFFF);                                          // dwQuality
            U32(0);                                                   // dwSampleSize
            U16(0); U16(0); U16((ushort)width); U16((ushort)height);  // rcFrame

            FourCC("strf"); U32(40);                                  // BITMAPINFOHEADER
            U32(40); I32(width); I32(height); U16(1); U16(8);
            FourCC("Y800"); U32((uint)_frameBytes);
            I32(0); I32(0); U32(0); U32(0);

            FourCC("indx"); U32((uint)(24 + 16 * SuperIndexEntries));
            _indxPayloadPos = Pos;
            U16(4); U8(0); U8(0);                                     // wLongsPerEntry, sub type, AVI_INDEX_OF_INDEXES
            U32(0);                                                   // nEntriesInUse
            FourCC("00dc");
            U32(0); U32(0); U32(0);
            for (int k = 0; k < SuperIndexEntries; k++) { U64(0); U32(0); U32(0); }

            EndChunk(strl);

            FourCC("LIST"); long odml = Pos; U32(0); FourCC("odml");
            FourCC("dmlh"); U32(248);
            _dmlhTotalFramesPos = Pos; U32(0);
            _w.Write(new byte[244]);
            EndChunk(odml);

            EndChunk(hdrl);
            StartMovi();
        }

        private long Pos => _fs.Position;
        private void FourCC(string s) { _w.Write(Encoding.ASCII.GetBytes(s)); }
        private void U8(byte v) => _w.Write(v);
        private void U16(ushort v) => _w.Write(v);
        private void U32(uint v) => _w.Write(v);
        private void I32(int v) => _w.Write(v);
        private void U64(ulong v) => _w.Write(v);

        // sizePos = position of a chunk's size field; size = everything after that field
        private void EndChunk(long sizePos) => Patch(sizePos, (uint)(Pos - sizePos - 4));

        private void Patch(long pos, uint value)
        {
            _w.Flush();
            long end = _fs.Position;
            _fs.Position = pos;
            _w.Write(value);
            _w.Flush();
            _fs.Position = end;
        }

        private void StartMovi()
        {
            FourCC("LIST"); _moviSizePos = Pos; U32(0);
            _moviFourccPos = Pos; FourCC("movi");
            _segDataOffsets.Clear();
        }

        public void WriteFrame(byte[] data, int length)
        {
            if (_closed) throw new InvalidOperationException("AVI already closed");
            if (length != _frameBytes) throw new ArgumentException($"frame size {length}, expected {_frameBytes}");
            if (_segDataOffsets.Count > 0 && Pos - (_riffSizePos - 4) + length + 8 > MaxRiffBytes)
                NewSegment();

            FourCC("00dc"); U32((uint)length);
            _segDataOffsets.Add(Pos);
            _w.Write(data, 0, length);
            if ((length & 1) == 1) U8(0);
            _totalFrames++;
        }

        private void CloseSegment()
        {
            // standard index for this segment, inside its movi list
            int n = _segDataOffsets.Count;
            long ixPos = Pos;
            uint ixSize = (uint)(24 + 8 * n);
            FourCC("ix00"); U32(ixSize);
            U16(2); U8(0); U8(1);                                     // wLongsPerEntry, frame index, AVI_INDEX_OF_CHUNKS
            U32((uint)n);
            FourCC("00dc");
            U64((ulong)_moviFourccPos);                               // qwBaseOffset
            U32(0);
            foreach (long off in _segDataOffsets)
            {
                U32((uint)(off - _moviFourccPos));
                U32((uint)_frameBytes);                               // bit 31 clear = key frame
            }
            _superEntries.Add((ixPos, ixSize + 8, (uint)n));

            EndChunk(_moviSizePos);

            if (_segment == 0)
            {
                _firstRiffFrames = (uint)n;
                FourCC("idx1"); U32((uint)(16 * n));
                foreach (long off in _segDataOffsets)
                {
                    FourCC("00dc");
                    U32(0x10);                                        // AVIIF_KEYFRAME
                    U32((uint)(off - 8 - _moviFourccPos));            // chunk header, relative to 'movi'
                    U32((uint)_frameBytes);
                }
            }
            EndChunk(_riffSizePos);
        }

        private void NewSegment()
        {
            if (_superEntries.Count >= SuperIndexEntries - 1)
                throw new IOException("AVI file too large for the super index");
            CloseSegment();
            _segment++;
            FourCC("RIFF"); _riffSizePos = Pos; U32(0); FourCC("AVIX");
            StartMovi();
        }

        /// <summary>Closes the file; fps &gt; 0 replaces the frame rate written at the start.</summary>
        public void Close(double fps)
        {
            if (_closed) return;
            _closed = true;
            try
            {
                CloseSegment();
                if (fps > 0)
                {
                    Patch(_avihUsPerFramePos, (uint)Math.Round(1e6 / fps));
                    Patch(_avihUsPerFramePos + 4, (uint)Math.Min(uint.MaxValue, _frameBytes * fps));
                    Patch(_strhRatePos, (uint)Math.Round(fps * RateScale));
                }
                Patch(_avihTotalFramesPos, _firstRiffFrames);
                Patch(_strhLengthPos, _totalFrames);
                Patch(_dmlhTotalFramesPos, _totalFrames);
                Patch(_indxPayloadPos + 4, (uint)_superEntries.Count);
                _w.Flush();
                long end = _fs.Position;
                for (int k = 0; k < _superEntries.Count; k++)
                {
                    _fs.Position = _indxPayloadPos + 24 + 16L * k;
                    _w.Write((ulong)_superEntries[k].pos);
                    _w.Write(_superEntries[k].size);
                    _w.Write(_superEntries[k].frames);
                }
                _w.Flush();
                _fs.Position = end;
            }
            finally
            {
                _w.Dispose();
                _fs.Dispose();
            }
        }
    }
}
