# QueenPix — notes for Claude (updated 6 Nov 2026)

QueenPix = .NET 8 WinForms app for recording up to four TIS DMK 23UP1300 cameras (IC Imaging Control 3.5, USB3Vision) plus webcams. Main code: `Form1.cs` (~11,000 lines; find functions by name, line numbers drift). Log: `C:\Users\viggo\Desktop\CameraRecording.log`. Camera settings: `%AppData%\QueenPix\settings.json`. Backup of the pre-debugging version: `..\QueenPix_backup_2026-10-29`.

## Recording (since 9 Nov 2026 clean-up)
- Normal AND loop recordings use `QueuedAviRecorder` (FrameQueueSink → RAM pool → `AviWriter`, OpenDML Y800 AVI). Normal: 1 GB pool per camera + writer thread. Loop: pool = the last `MaxLoopFrames` frames (oldest reused), written oldest-first at stop to %TEMP%\LoopRecording_<name>\; RecordingStartTime is set to the first saved frame (date/time overlay).
- The SDK live display is always off while recording (normal + loop); a PictureBox shows a half-size recorded frame 5×/s (`PreviewIntervalMs` = 200) with a buffer bar (normal: fill/peak/frames/lost; loop: frames kept/lost). Timelapse is unchanged (FrameHandlerSink snapshots, live display on).
- Screenshot while recording: `QueuedAviRecorder.TakeSnapshot()` (full-size copy of the next frame).
- At stop the log has per camera: SDK/driver counters, recorder summary (received/written/max queue/lost), frames per second, gaps and estimated missed triggers; missed triggers and lost frames make the ⚠️ DROPPED message.
- Removed in the clean-up: old MediaStreamSink path and its file-growth timing, the "Buffered recording", "Live preview while recording" and "record without writing files" settings, the Tools → Recording Test (it measured the old paths; Ctrl+B), the old loop polling thread (Bitmap queue).
- UI: single Start/Stop All Live and Start/Stop Recording buttons (`btnLiveToggle`, `btnRecordToggle`, red while recording); the hidden btnStartLive/btnStopLive/btnStartRecording/btnStopRecording still carry the state the rest of Form1 sets (Ctrl+L / Ctrl+R use them).
- Save window defaults to AVI (exact frames); MP4 is lossy x264.
- Build check without Windows: Roslyn csc against .NET 8 refs + Mono 4.7.2 WinForms/Drawing + OpenCvSharp built from source; only environment errors remain (Program.cs HighDpi/ApplicationConfiguration, Microsoft.VisualBasic.Devices).

## Hardware trigger — working
- Trigger source: CamSync box (Arduino Nano, Timer1 phase-correct PWM; camera pulse D9 → 74AHCT125 → BNC → camera pins 11/12; flash pulse D10, centred in the camera pulse). Box counts its pulses.
- DMK 23UP1300 trigger VCD item (`VCDID_TriggerMode`) has only Enable (Switch), Polarity (Switch, false = rising edge) and Delay (AbsoluteValue, 15 µs). No trigger-mode sub-element.
- Set trigger properties with live stopped (they may be unavailable while streaming). Code: `ConfigureCameraTrigger`, `FindTriggerEnableSwitch`, `ToggleCameraTrigger`; recording start re-checks that every camera is armed.
- Do NOT use `ICImagingControl.DeviceTrigger` (legacy DirectShow; throws on USB3Vision cameras).
- The camera STROBE output is not configured anywhere in QueenPix.

## History: missed triggers (29 Oct – 9 Nov 2026) — solved
- Symptom: triggered 4-camera recordings at ~215 Hz lost 1–25 % of the box's pulses, a different number per camera, SDK drop counters 0.
- Causes: (1) the SDK live display during recording made the cameras stall one after another when triggering started; (2) MediaStreamSink wrote the AVI in the frame-delivery path, so disk/file-cache stalls made all cameras ignore triggers. Proof: with nothing written, all 4 cameras delivered 4323 of 4324 pulses.
- Ruled out: trigger signal (scope, independent function generators), hub/USB ports, driver latency (LatencyMon worst DPC 1.07 ms).
- Verified 9 Nov: 31,675 box pulses → 31,675 frames in every camera; CamSync v3 "Flash every 2" + Tools → Verify recording showed unbroken bright/dark alternation and the same sequence in all four cameras.
- CamSync settings for 0.5 ms exposure at 218 Hz: Pulse 550 µs, Flash 200 µs (flash 175–375 µs after the edge). "Flash every" back to 1 for real recordings.
- Check for every recording: box pulse count = frame count in every camera (±1); the log's timeline shows gaps.
