# QueenPix — notes for Claude (updated 6 Nov 2026)

QueenPix = .NET 8 WinForms app for recording up to four TIS DMK 23UP1300 cameras (IC Imaging Control 3.5, USB3Vision) plus webcams. Main code: `Form1.cs` (~11,000 lines; find functions by name, line numbers drift). Log: `C:\Users\viggo\Desktop\CameraRecording.log`. Camera settings: `%AppData%\QueenPix\settings.json`. Backup of the pre-debugging version: `..\QueenPix_backup_2026-10-29`.

## Recording path (normal recordings, since 6 Nov 2026)
- Buffered: `QueuedAviRecorder` (FrameQueueSink → RAM pool → writer thread → `AviWriter`). SDK live display paused during recording; instead a PictureBox over each camera view shows a half-size recorded frame every 500 ms (`PreviewIntervalMs`), made on the writer thread. AVI header frame rate is patched at close with the measured trigger rate. Old direct `MediaStreamSink` path still selectable (Settings menu).
- Recording preview shows a buffer bar (fill, peak, frames, lost) updated every 250 ms.
- The SDK live display is ALWAYS off during normal recordings (no setting); the light preview replaces it. Triggers missed inside timeline gaps are added to the dropped count (⚠️ message).
- UI: one "Start/Stop All Live" toggle button (`btnLiveToggle`); the hidden btnStartLive/btnStopLive still carry the live state that the rest of Form1 sets.
- Tools → "Verify recording..." (`VerifyRecordingDialog.cs`): ffmpeg-decodes every frame of the selected videos; checks header vs decoded vs box count (±1), decode errors, identical consecutive frames, black frames, and - with the box flashing on every other pulse - the bright/dark alternation per video and the same sequence across cameras. Report saved as <first video>_verify.txt.
- First rig test 6 Nov 17:04: 4 cameras, 218 Hz, 18.5 s → 4027 frames in every camera, no gaps, max queue 92 frames, nothing lost.
- Limits: disk space (4 cameras at 218 Hz ≈ 335 MB/s ≈ 20 GB/min); disk must sustain that on average (RAM buffer covers ~12 s of the disk falling behind; losses beyond are counted); AVI super index ≈ 245 GB per file (~49 min per camera at 218 Hz); arrival-time log 2,000,000 frames.

## Hardware trigger — working
- Trigger source: CamSync box (Arduino Nano, Timer1 phase-correct PWM; camera pulse D9 → 74AHCT125 → BNC → camera pins 11/12; flash pulse D10, centred in the camera pulse). Box counts its pulses.
- DMK 23UP1300 trigger VCD item (`VCDID_TriggerMode`) has only Enable (Switch), Polarity (Switch, false = rising edge) and Delay (AbsoluteValue, 15 µs). No trigger-mode sub-element.
- Set trigger properties with live stopped (they may be unavailable while streaming). Code: `ConfigureCameraTrigger`, `FindTriggerEnableSwitch`, `ToggleCameraTrigger`; recording start re-checks that every camera is armed.
- Do NOT use `ICImagingControl.DeviceTrigger` (legacy DirectShow; throws on USB3Vision cameras).
- The camera STROBE output is not configured anywhere in QueenPix.

## Recording path (old direct mode)
- Uncompressed AVI per camera via `MediaStreamSink((AviCompressor?)null, file)`; no per-frame callback in this mode. Frame rate is estimated from file growth (`StartFileGrowthTiming`, `MeasuredRecordingFps`); the fps QueenPix writes is measured, the true rate is the box's.
- `LogFrameDropCounters` (called before LiveStop) logs device and driver drop counters.

## Open problem: missed triggers (since 29 Oct)
Set-up: 4 cameras, Y800 480×800, 215 Hz, exposure 0.5 ms fixed, gain 0, device frame rate 272.9 fps, all on one USB 3.0 hub, ~330 MB/s total, laptop Alienware Area-51 (Win 11).
- Symptom: in most 20 s triggered runs each camera has 1–6 % fewer frames than the box count, a different number per camera, in bursts (disk monitor dips below ~315 MiB/s). Some runs are perfect with nothing changed. Breaks frame-to-frame sync across cameras.
- Established: SDK drop counters are always 0 → the cameras never make those frames. TIS support: a trigger is silently ignored while the camera is busy exposing/delivering; no ignored-trigger counter; 0–5 V drive fine; no newer firmware; they suspect Windows/USB stalls. In trigger mode all four send at once (~420 MB/s peak through one hub).
- Tried without a reliable fix: W550 vs W2000 pulse width, flash off, 2 cameras instead of 4, 2 trigger cables instead of 4, USB selective suspend / PCIe link-state PM / hub power saving off, live preview off. Laptop on Performance / Best performance, charger connected, efficiency mode off.
- Next: two cameras per laptop USB controller without hub (3 × 20 s runs with box count); 200 Hz or smaller ROI; function generator instead of box; desktop PC. Scope: trigger line at a camera (BNC tee, camera stays connected) and camera strobe outputs, scope triggered on a > 7 ms gap. Per-camera strobe logger (spare Arduino) would give the exact missed pulses and allow re-syncing recordings.
- Check for every recording: box pulse count = AVI frame count in every camera (±1).
- Open: with W2000 the flash comes 900–1100 µs after the trigger edge, outside the 0.5 ms exposure → longer exposure (~1/800 s) or shorter pulse.

## Findings 6 Nov 2026 (missed triggers)
- Diagnostics used on 6 Nov (since removed except the timeline): per-camera timeline at stop; a "Live preview while recording" switch; a "record without writing files" test mode (FrameHandlerSink, driver "delivered" vs box count).
- Live preview ON caused a start-up stall: when triggering begins, cameras stop one after another (~0.4 s, then ~0.7 s, ...) → different frame counts per camera, broken sync. Preview off removed it.
- Trigger path is fine: scope on a camera's trigger line clean during a 23 %-loss run; with two independent generators the mid-run losses still hit both cameras at the same moments.
- LatencyMon: worst DPC 1.07 ms (Wdf01000), ISR 0.5 ms → no driver stalls of 60-160 ms.
- DECISIVE: 4 cameras, 218 Hz, preview off, NO file writing → every camera delivered 4323 of 4324 box pulses (same run with AVI writing: ~9 % shared losses). The missed triggers come from the MediaStreamSink/AVI write path stalling frame delivery, not from USB, hub, laptop ports or the box.
- Fix (6 Nov, not yet tested on the rig): `QueuedAviRecorder.cs` - normal recordings use a FrameQueueSink whose callback only copies each frame into a pre-allocated RAM pool (1 GB per camera, `RecordBufferMBPerCamera`) and a writer thread per camera writes an OpenDML (AVI 2.0) Y800 AVI (`AviWriter`, 1 GiB RIFF segments, ix00 + indx + idx1). Lost frames (pool full, wrong format, write error) are counted and logged; the log also gives per-camera frames per second, gaps and estimated missed triggers from the frame arrival times. Settings → "Buffered recording (RAM queue)" (on by default) switches back to the old MediaStreamSink path.
- To verify on first use: image orientation vs. older recordings (frames stored top-down with FourCC Y800; bottom-up SDK buffers are flipped), files > 1 GB open in ffprobe/ffmpeg and the save dialog, box count = frame count in every camera.
