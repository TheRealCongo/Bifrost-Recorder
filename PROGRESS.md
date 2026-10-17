# Bifrost Recorder: progress

Native Windows exe (C# WinForms, C# 5, compiled with Windows' own `csc.exe`, no NuGet), recording one window with the bundled ffmpeg.
Built like Bifrost Suite: `build.ps1`. Repo is local only (no remote, nothing pushed).

## Live test (M1): for Congo

1. Run `build\BifrostRecorder.exe` (double-click). `build\bin\` has to stay next to it (it holds ffmpeg).
2. Run the game **borderless or windowed**. Exclusive fullscreen can't be captured by Windows' window capture; set the game to
   "borderless/windowed fullscreen" first. Then start the game and put the Recorder beside it (or Alt+Tab back to it).
3. In the Recorder pick the game in the list (press Refresh if it isn't there), press **Record**. The clock runs and the line
   under it shows the file growing. Play 1 to 2 minutes, then Alt+Tab back and press **Stop** (the window can be anywhere, even
   behind the game).
4. With no `BifrostRecorder.settings.json` beside the exe the video is saved to `%USERPROFILE%\Videos\Bifrost Recorder\<program> yyyy-MM-dd HH-mm-ss.mp4`
   ("Open recordings folder" opens it).
5. What to check: the MP4 plays smoothly and the picture is the game's window only; colours look right; file size (a guide for
   Arma is in the measurements below once they're in: the cap is 30 Mb/s at 1080p, about 225 MB a minute at the very most);
   whether the game's frame rate dropped while recording (compare with the game's own FPS counter with and without recording);
   there is no console window or leftover process afterwards (Task Manager: no `ffmpeg.exe` once the Recorder is closed or
   after Stop).
6. If something fails the status line says why. A crash or kill of the Recorder ends ffmpeg with it and leaves a `.mkv` in the
   same folder: that file is playable (VLC) and can be remuxed.

Not in M1: sound (video only), hotkey, upload.

## Decision record

```
REQUIREMENTS
- Window list (visible, titled, not cloaked, not tool windows, not itself) with title + exe name + icon; Refresh; Record/Stop; elapsed; status; Open recordings folder.
- Output: <DataFolder>\Recordings from BifrostRecorder.settings.json, else %USERPROFILE%\Videos\Bifrost Recorder; "<program> yyyy-MM-dd HH-mm-ss.mp4".
- ffmpeg gfxcapture hwnd=, 60 fps constant, GPU frames into the encoder (hevc_nvenc > hevc_amf > hevc_qsv by test encode, cached), HEVC Main 8-bit,
  constant quality + ceiling scaled by pixels, 2 s keyframes, tagged; .mkv while recording, q to stop, remux to .mp4, ffprobe, delete .mkv.
- Window closes: stop and save. No console windows, Job Object kill-on-close, nothing left behind.
- Tests (unit + end to end with own animated window), measurements, screenshots of own windows only.
MINIMUM COMPONENTS NEEDED
- Native.cs (window list, job, process start), Core.cs (settings, names, encoder profiles, arguments), Capture.cs (recording), MainForm/Look/Program, tests + test window.
REJECTED / OUT OF M1
- Audio, upload, hotkey, icon, installer, persistent encoder cache (a stale answer after a GPU or driver change is worse than 0.3 s at start), own capture code.
PRIMARY RISKS
- Window capture delivers nothing for windows that don't redraw (found, handled). scale_d3d11 broken in this build (found, avoided).
- Colour matrix of NVENC's internal RGB conversion (measured, tagged truthfully).
REQUEST INTERPRETATION
- "Program name" in the file name = the exe name without .exe.
```

## Done, with evidence

All of it from `build.ps1 -Test` (108 checks, last run 108 passed, 0 failed; log: `build\test-run3.log` style output from `build\RecorderTests.exe`).
Evidence level key: **VERIFIED** = run here and checked; **ASSUMED** = not run here.

| Item | Status |
|---|---|
| Build: `BifrostRecorder.exe`, `bin\ffmpeg.exe` + `ffprobe.exe` + DLLs (gyan 9.0.1 shared build, 224 MB, copied from Studio), `RecorderTests.exe` | VERIFIED |
| Window list, filter rules, Refresh, Record/Stop, timer, status line, Open recordings folder | VERIFIED (driven through the real window controls in the end-to-end test, and pictured) |
| Settings/folder fallback and file naming | VERIFIED (unit tests) |
| Encoder pick by test encode, cached; NVENC | VERIFIED (hevc_nvenc picked in about 0.3 s) |
| AMF and QSV argument sets | **ASSUMED**: unit tested only as strings; never run (no AMD GPU here; Intel UHD 630 is present but the capture's frames live on the NVIDIA adapter) |
| Software fallback (libx264, CPU) | VERIFIED: forced and recorded end to end, BT.709 colours check out |
| 60 fps constant, 2 s keyframes, HEVC Main 8-bit, hvc1 tag, faststart | VERIFIED (ffprobe) |
| Record to .mkv, `q` to stop, remux, probe, .mkv deleted | VERIFIED |
| Window closes mid-recording: auto stop and save | VERIFIED |
| Window stops redrawing / never redraws: still stops cleanly and starts | VERIFIED (see findings) |
| Window resized mid-recording keeps the output size | VERIFIED |
| Job Object: killing the parent outright ends its ffmpeg | VERIFIED |
| Hygiene: no child-process window ever visible, no process of ours left | VERIFIED (watcher thread, 1100+ looks, plus process counts) |

## Findings that shaped the build (all measured here)

- **`scale_d3d11` is unusable in this ffmpeg on this PC**: it fails with "Could not create the texture (E_INVALIDARG)" even on a synthetic
  frame and even for `format=bgra`. `hwmap` from d3d11 to cuda, d3d12, vulkan, opencl also fail. So the frames go BGRA d3d11
  straight into NVENC, which converts to YUV inside the encoder (no CPU).
- **NVENC's own RGB to YUV conversion is BT.601, limited range** (pure red came back Y 81, Cb 90, Cr 240; white 235, black 16), and ffmpeg
  would have tagged the stream "gbr / full range", which is wrong. So the stream is tagged what the pixels are: tv range, BT.709
  primaries and transfer, **smpte170m (BT.601) matrix**. Players decode it correctly (colour error 2 of 255 after the round trip).
  **This is a deviation from "BT.709 tagged"**: true BT.709 needs a GPU colour conversion filter that works here, and none does.
  Tagging 709 on 601 data would shift every colour. The software fallback does convert with swscale and is genuinely BT.709.
- **gfxcapture sends no picture for a window that isn't redrawing, and ffmpeg ignores `q` until a picture arrives.** The Recorder
  therefore asks the window to repaint (`RedrawWindow`, invalidate only, no waiting) when no picture has come for a second, at start
  and at stop. Otherwise a static window never starts and a stalled one can't be stopped. Games redraw constantly, so this only
  matters for quiet windows.
- A window that isn't on a monitor (off-screen, or minimized) sends nothing either, so the test windows sit on a monitor but are
  layered at alpha 1/255 (invisible) and click-through.
- Muxing `-flush_packets 1`: without it ffmpeg's buffering left a 0-byte .mkv when it was killed.
- gfxcapture with `display_border=false` captures the window's client area (no title bar or frame): 1279x719 client gave 1278x718 with `width=-2:height=-2`.

## Screenshots (docs\screenshots\m1\, made by the end-to-end test, 2026-10-17; only the tests' own windows)

- `01-window-list.png`: the Recorder idle, window list filled with the tests' three windows, one selected (test: end-to-end, step "list")
- `02-test-window.png`: the animated test window (moving bar, frame counter, colour swatches) pictured with PrintWindow
- `03-recording.png`: the Recorder while recording, timer at 00:00:08, Stop button, file size growing
- `04-saved.png`: the Recorder after Stop: saved file name, length and size
- `05-recorded-frame.png`: a still pulled from the recorded MP4 at 10 s with ffmpeg (`-ss 10 -frames:v 1`)

## Open issues / not done

- Measurements (MB per minute, GPU/encoder load, ffmpeg CPU) on the harder scene: after the live-test build.
- AMF and QSV never run.
- A `.mkv` left by a crash is not recovered automatically on the next start (it stays playable in the folder).
- Exclusive-fullscreen games can't be captured by window capture (borderless is fine).
