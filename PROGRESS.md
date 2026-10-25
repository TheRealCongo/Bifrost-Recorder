# Bifrost Recorder: progress

Native Windows exe (C# WinForms, C# 5, compiled with Windows' own `csc.exe`, no NuGet). Since 0.2.0 it records with libobs, the engine
of OBS Studio (32.2.2's official release files, unmodified). Built like Bifrost Suite: `build.ps1`. Public on GitHub since 2026-10-18:
TheRealCongo/Bifrost-Recorder, GPL-2.0-or-later.

## M2: the OBS engine (0.2.0, 2026-10-18)

**Why.** Under Arma's GPU load any capture from outside the game competes with it for the GPU (M1b/M1c live tests). OBS and Medal record
games smoothly because a hook copies each frame inside the game's own frame. Congo chose to open-source the Recorder so it can run OBS's
engine and its signed hook under the Recorder's own window (Oct 18: "I'll open source the Bifrost Recorder and we can just use the OBS
hooks"). BattlEye trusts OBS's hook by its signature, so the hook is used exactly as OBS ships it, never rebuilt or changed.

**What changed.**
- `Obs.cs`: P/Invoke to obs.dll (only the functions used), the engine started once at the first Record on the window's thread (libobs
  wants a single-threaded COM apartment there), libobs's log caught into the recording's log, the encoder picked from what libobs registered.
- `Capture.cs`: a recording is a libobs scene with one capture (game_capture, window_capture with Windows Graphics Capture, or
  monitor_capture), the program's sound (wasapi_process_output_capture, or wasapi_output_capture for everything) and the mic
  (wasapi_input_capture), NVENC HEVC (CQVBR 24, the light or quality set; AMF, QSV, x264 after it), AAC tracks, and OBS's hybrid MP4
  output (crash-safe, finished on Stop; no MKV, no remux). Telemetry is libobs's own counters: frames the GPU drew late, frames the encoder
  skipped. Game capture that can't hook falls back to window capture after 6 s (`CaptureChoice`).
- Settings: Capture (game / window / screen), Sound (program / PC), Microphone (off / on, own track) are new; Frame rate is gone.
- `build.ps1`: downloads OBS Studio 32.2.2's Windows zip and sources once into `deps\`, checks both against their published SHA-256,
  copies only what is used, and refuses to finish unless the game capture hook and its injector are validly signed. The ffmpeg folder is gone.
- Licences: LICENSE (GPL-2.0), THIRD-PARTY-NOTICES.md and `licenses\` (OBS's own and obs-deps 2026-07-15's licence texts, GPL-3.0 for
  ffmpeg, which OBS builds `--enable-gpl --enable-version3`) travel with every build.

**Findings (libobs from C#, all measured here).**
- libobs needs `/` in the paths it is given: its shaders `#include` each other and it finds their folder by the last `/` only
  (`cf-lexer.c`). With `\` every shader failed to parse and `obs_reset_video` returned -1 with no message at all.
- libobs looks for its helper programs (obs-nvenc-test.exe and the like) next to the running exe, not next to obs.dll, so obs.dll and its
  libraries sit beside BifrostRecorder.exe; plugins and data are in `obs\`.
- win-capture does fetch its compatibility list (the programs that need special handling) from obsproject.com in the background; the
  etag of the last answer is in its meta.json. With no module config path to obs_startup, libobs builds win-capture's folder from "" and
  writes `win-capture\` (compatibility.json, meta.json, package.json) into whatever folder the Recorder was started from. The Recorder
  now gives obs_startup `<data folder>\engine` (`Settings.EngineConfigFolder`: beside the prefs and the logs), so the files live
  there and nothing is written beside the program. The fetch itself is OBS's and isn't blocked.
- Its hook still works with no module config path: it then loads graphics-hook64.dll from the Recorder's own folder instead of copying it
  to C:\ProgramData\obs-studio-hook, and no Vulkan layer is registered (Vulkan-only games fall back to window capture). Giving the config
  path changed none of that: after a full run with it, C:\ProgramData\obs-studio-hook and HKCU\SOFTWARE\Khronos\Vulkan\ImplicitLayers
  were untouched and the game capture checks on FakeGame still pass.
- libobs never captures a window of its own process (`window-helpers.c`), so the tests' recorded windows run in child processes.
- libobs's SDR white level is 0 until `obs_set_video_levels` is called (OBS Studio's frontend sets 300 and 1000 nits). Windows hands a
  window or screen on an HDR monitor over as 16-bit float, libobs tone-maps it with 80 / white level, and with 0 every window recording
  was black (this PC's primary monitor runs HDR). Game capture of an SDR game was unaffected. The Recorder now sets OBS's levels at start.
- win-capture runs get-graphics-offsets32/64 at plugin load and reads their pipes to the end. A process started meanwhile with handle
  inheritance (every .NET Process.Start) holds the pipe open until it exits, and the first game capture waits for it (409 s in one run).
- OBS's MP4 names each track in its udta `name` box; ffprobe shows it as the tag `name` (Mix, Game, Mic), not `title`.
- The official libobs-d3d11 tries to raise the process's GPU priority itself (REALTIME without HAGS, HIGH with it); without
  administrator rights it is refused ("not admin?") and nothing changes.
- Window and screen capture ask Windows for SDR pictures (`force_sdr`); left to libobs, an HDR desktop's float pictures are tone-mapped
  at libobs's 300-nit white, not the monitor's (480 here): cyan came back 136,209,206. With it, colours are within 2/255.

**Game capture under a GPU-bound game (the diag, `RecorderTests.exe --diag windowed 15`, 2026-10-18 02:00).** FakeGame at 1920x1080,
vsync off, its load tuned until the GPU sat at 99% for 60 fps. Recording it 15 s with game capture: FakeGame kept 59.7 fps (59.7 alone),
900 different pictures in 900 frames, 0 skipped by the encoder, 5 drawn late, "Keeping up", encoder 30%. This is the case window capture
starved in (M1b). The window capture half did not run: the safety check stopped the run (next point).

**Live test (M2): for Congo, single-player or offline first (BattlEye trusts OBS's signed hook, but prove it offline before any server).**
1. Close Medal (its own game hook and encoder would share the game with ours). No test runs while Arma is open.
2. Open `build\BifrostRecorder.exe` **before** Arma, and wait for "Pick a window, then press Record." (the engine has started).
3. Start Arma Reforger, single player. Alt+Tab to the Recorder, pick Arma, Record (the chip says game capture). Back to the game.
4. Play 2 to 3 minutes, then Ctrl+Alt+Shift+R (Ctrl+Alt+R is taken on this PC) to stop.
5. What to check: the status says Keeping up (not "Falling behind"); the MP4 plays smoothly at 60 fps; the game's FPS with and without
   recording; the sound is the game's; the log in `<DataFolder>\logs` (or `%LOCALAPPDATA%\Bifrost Recorder\logs`) has the game capture
   lines ("hooked", "shared texture capture successful").

**A driver reset at engine start.** nvlddmkm event 153 at 02:00:28, the moment libobs finished starting, which in that run happened with
FakeGame already holding the GPU at 99% and took about 60 s (1.5 s otherwise). Starting libobs opens a test session on every GPU encoder;
the recording's own NVENC session and 15 s of encoding under the same load caused no further event. This matches M1's finding that new
encoder sessions next to a saturated GPU are what this PC's driver dislikes (it logged 30 such events in 30 days, 18 since the evening
before, most during test runs). So the Recorder now starts the engine when it opens, on a thread of its own (the window never freezes,
Record waits until it is ready), not at the first Record while the game runs; the diag starts the engine before FakeGame. Not re-run.


## M1b (after the M1 live test of 2026-10-17)

**What the M1 live test showed** (Congo, Arma 2560x1440 borderless, 6 min 15 s, Medal closed): Arma stayed at 55-65 FPS, no crash. The MP4 was only 201.999 s
(12120 frames, 73.4 MB), 88% of the packets were tiny repeats, and the .mkv had grown at 0.3 to 0.6 x real time. Reading: with the game using the whole GPU the pipeline
(gfxcapture, NVENC's own RGB to YUV conversion, the encode) was starved at normal GPU priority and managed about 32 encoded frames a second; the fps filter kept
creating the missing 60 fps repeats (each one is encoder work too), so the encode fell further behind real time, and `q` plus a 10 s wait dropped the backlog (about 173 s).

**M1b changes** (code, unit tests and compile VERIFIED; end-to-end and bench: see "Open issues" below for what has and hasn't been run):

1. **Telemetry**: `-progress` is parsed (frame, fps, speed, dup_frames, drop_frames, out_time_us, total_size). The status line shows `Keeping up · 1.00x`, or after more than
   5 s below 0.95x over a 4 s window `Falling behind: 0.53x real time` (amber), or `Waiting for the window to draw` when no frames arrive (not "behind"). One log per recording,
   `<DataFolder>\logs\<name>.log` (or `%LOCALAPPDATA%\Bifrost Recorder\logs` without a DataFolder; never the recordings folder; newest 20 kept): settings, ffmpeg command,
   GPU-priority NTSTATUS, a progress line every second, Stop timing, forced or not.
2. **GPU priority**: right after ffmpeg starts `D3DKMTSetProcessSchedulingPriorityClass` is called on its process handle: HIGH (4), else ABOVE_NORMAL (3), never REALTIME; retried
   for a few seconds in case ffmpeg has not opened the GPU yet; each NTSTATUS is logged, and the class read back. CPU priority untouched.
3. **Switches** in `BifrostRecorder.settings.json` (not in the UI; the header chip shows the active set, e.g. `HEVC · NVENC · gentle · 1080p · GPU high`):
   `"EncoderProfile": "standard" | "gentle"`, `"MaxHeight": 0 | 720 | 1080` (any 144 to 8192; done by gfxcapture itself with `width=`/`height=` and
   `resize_mode=scale_aspect`, on the GPU), `"GpuPriority": "high" | "normal"`. Gentle = `-preset p4 -tune hq -rc vbr -cq 24 -b:v 0 -maxrate M -bufsize M -bf 0 -spatial-aq 0
   -rc-lookahead 0 -multipass disabled -g 120`. A settings file without DataFolder still saves to `%USERPROFILE%\Videos\Bifrost Recorder`.
4. **Studio-safe end**: Stop asks the window to repaint first (a closing frame), and if the encoder is behind (more than 1.5 s of video unwritten, or flagged behind) ffmpeg gets up

### M1c: two more switches to try in a live test (code and unit tests only; never run on the GPU)

Both are in `BifrostRecorder.settings.json`, not in the UI; the chip shows them (`... · native · screen · vfr · GPU high`).

- **`"CaptureMethod": "window"` (default) | `"screen"`.** `screen` records the whole monitor the selected window is on with ffmpeg's `ddagrab` (Desktop Duplication),
  60 fps, pointer drawn, frames staying Direct3D 11 into NVENC. **It records whatever is on that monitor**, including the Recorder itself if Congo alt-tabs onto that
  monitor, notifications, other windows over the game. `MaxHeight` is ignored with it (ddagrab can't scale, and `scale_d3d11` doesn't work here); the video is the monitor's size.
  *Mapping the window to ddagrab's output*: `ddagrab=output_idx=N` counts the outputs of the **adapter of the Direct3D device it is given**, so two numbers are needed and
  `Screen.AllScreens` order is not used. `Dxgi.cs` asks DXGI itself (`CreateDXGIFactory1`, `EnumAdapters1`, `EnumOutputs`, `GetDesc`) for every adapter and output, takes the
  window's `HMONITOR` (`MonitorFromWindow`) and finds the output whose `DXGI_OUTPUT_DESC.Monitor` is the same handle, giving (adapter index, output index on that adapter).
  The command then has `-init_hw_device d3d11va=dda:<adapter> -filter_hw_device dda` (ffmpeg's D3D11 device on that adapter, same enumeration order) and
  `ddagrab=output_idx=<output>`. On this PC DXGI lists adapter 0 (RTX 3070 Ti) with output 0 = `\.\DISPLAY1` 2560x1440 and output 1 = `\.\DISPLAY2` 1920x1080 (both monitors
  are on the NVIDIA GPU; the Intel iGPU has none), so DISPLAY2 is `d3d11va=dda:0` with `output_idx=1`. VERIFIED: the enumeration and the HMONITOR match for every monitor Windows has
  (unit test). NOT verified: ddagrab itself. If the monitor's adapter is not the encoder's GPU (vendor 10DE for NVENC, 1002 AMF, 8086 QSV) the Recorder refuses before starting anything.
- **`"FrameRateMode": "cfr"` (default) | `"vfr"`.** `vfr` removes the fps filter and the repeats: `-fps_mode passthrough -enc_time_base filter`; ddagrab gets `dup_frames=false`.
  Only frames that really arrived are encoded, each with its real capture time (the capture filters' time base is 1/10,000,000). Findings from CPU-only ffmpeg runs (libx264,
  a test pattern with gaps and jittered times; in the unit tests): (1) `-fps_mode passthrough` alone **rounds every time to the 1/60 grid** (the encoder time base defaults to
  1/frame rate), so `-enc_time_base filter` is needed to keep them exact; (2) `-r 60` with passthrough is **refused by ffmpeg** ("contradictory"), so it is not used; the encoder
  still reports 60 fps (the capture filter's own frame rate), so it does not derive a rate from the 10 MHz time base (shown here with a lavfi source; the capture filters'
  link frame rate is from the upstream source, not run); (3) the MKV to MP4 remux with `-c copy` keeps every capture time exactly and the frame count (192 of 240 here), the MP4
  has time base 1/16000, `avg_frame_rate` is the real average (48 fps) and `r_frame_rate` is a meaningless guess (16000/1). Studio reads `avg_frame_rate` first (Z:\BFStudio\main.js:108),
  so it shows 48.0 fps. Telemetry is unchanged: speed is still the written video's time (`out_time_us`) against the clock; a window that sends nothing is "Waiting for the
  window to draw", never "Falling behind". Unit tests check the exact arguments of all four combinations (window or screen, cfr or vfr), the chip, the refusal paths and these ffmpeg behaviours.

### 0.1.3 and what the evening's live tests showed (2026-10-17)

**GPU hangs, honestly.** Arma Reforger crashed with DXGI_ERROR_DEVICE_HUNG at 19:38:18 and 19:41:19 (during my test runs: NVENC sessions started next to the game),
at **23:09:50 with nothing of ours running** (Recorder idle, no ffmpeg; nvlddmkm id 153 logged at 23:09:44, 46 and 49), and at 23:29:13: my `--ui` test run
started at 23:29:05 opened a Recorder window whose startup encoder check is a 0.5 s NVENC test encode, and id 153 was logged at 23:29:08, 08, 10 and 13. So this PC resets its driver by itself too,
but a GPU encoder session started next to a running game is a trigger we have produced more than once. Consequences: every test mode that can touch the GPU, including `--ui`, now goes through the
game guard and checks the System log for nvlddmkm afterwards (exit 9 on a hit); the Recorder's encoder is now chosen **without a test encode** by default (`EncoderCheck` = vendor).

**What the measurements said** (the orchestrator's diagnostics against real Arma at 2560x1440, about 60 fps, normal GPU priority): window capture alone 58 fps (capture is fine, so the independent-flip
idea was wrong); NVENC with spatial AQ 22 fps; without AQ 53 to 56 fps at any preset; B-frames cost about 5%; scaling inside gfxcapture 28 fps; every run with **GPU priority HIGH** was catastrophic
(2 to 3 fps). So the defaults are now: Light encoder (p4, no AQ, no B-frames, no lookahead, bufsize = ceiling), Native size, This window, Steady 60 fps, GPU priority **normal**, EncoderCheck vendor.
Scaling (1080p, 720p) is documented in the Settings window as costly while a game runs; the older AQ set remains as "Quality, for desktop apps only".

**Settings window and prefs (0.1.3).** Bifrost Suite rewrites `BifrostRecorder.settings.json` **whole** on every install and update (Bifrost-Hub src/Hub/Core.cs, `WriteSettingsFile`, for apps with a
SettingsFile and no KeyFile), so a choice kept there would vanish. The member's choices therefore live in `<DataFolder>\Recorder.prefs.json` (without a DataFolder: `%LOCALAPPDATA%\Bifrost Recorder\prefs.json`),
written at once on every change; precedence is prefs, then the experiment keys in settings.json, then the defaults; a damaged prefs file is ignored with a status-line note. The five choices and the look (theme id
and accent) are saved there. The look is a C# port of Bifrost Scribe's `src/shared/theme.ts`: the same five presets with the same token values, the same accent maths (HSV, hue 0 to 359, Studio's swatches,
white and black), on-accent text by contrast; unit tests check the values against theme.ts's maths. Two additions of mine: the amber of "Falling behind" comes from Planner's presets (Scribe has no
warning token), and the accent used as text or a thin line falls back to the text colour when it would not read (white accent on Paper). The default accent is now the preset's own (Console's amber), so the
Recorder's UI no longer defaults to lime; the app icon is unchanged.

**Findings to act on elsewhere.** (1) The Hub writes `BifrostRecorder.settings.json` with only `ManagedBy` and `DataFolder` (the recorder's catalog entry has SettingsFile, no KeyFile), so the Recorder never
receives `TeamServer`/`TeamKey` from Bifrost Suite: the Upload to Armory button opens the website until the catalog entry is changed to a KeyFile (which merges, as for Planner). (2) Ctrl+Alt+R is already
registered by another program on this PC (Win32 error 1409), so the Recorder falls back to Ctrl+Alt+Shift+R when the default is taken and shows the key it actually registered.
   to 30 s to finish after `q` (10 s otherwise) before any kill; the window says `Finishing…  N s of video still to write`.
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
- "Program name" in the file name = the exe's friendly name (version-info FileDescription, e.g. "Arma Reforger"), else the window title, else the exe name, sanitized for Windows (orchestrator's call, 19:4x).
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
- A window that isn't on a monitor (off-screen at -30000,-30000: 0 frames in 5 s; or minimized) sends nothing either, and one with only 8 px on
  a monitor sent a blank picture. So the test windows open on the **primary** monitor (the NVIDIA one, the path a game takes) at the bottom of the window order, layered at alpha
  1/255 (invisible), click-through, no-activate and never topmost; `BIFROST_TEST_SCREEN=secondary` puts them inside the other monitor instead (fallback,
  not yet needed or tried). The Recorder's own window in the tests is off every monitor (PrintWindow pictures it there fine). A watcher checks every 50 ms that
  no test window is topmost, visible (alpha above 2) or in the foreground.
- Muxing `-flush_packets 1`: without it ffmpeg's buffering left a 0-byte .mkv when it was killed.
- gfxcapture with `display_border=false` captures the window's client area (no title bar or frame): 1279x719 client gave 1278x718 with `width=-2:height=-2`.

## Screenshots (docs\screenshots\m1\, made by the end-to-end test, 2026-10-17; only the tests' own windows)

- `01-window-list.png`: the Recorder idle, window list filled with the tests' three windows, one selected (test: end-to-end, step "list")
- `02-test-window.png`: the animated test window (moving bar, frame counter, colour swatches) pictured with PrintWindow
- `03-recording.png`: the Recorder while recording, timer at 00:00:08, Stop button, file size growing
- `04-saved.png`: the Recorder after Stop: saved file name, length and size
- `05-recorded-frame.png`: a still pulled from the recorded MP4 at 10 s with ffmpeg (`-ss 10 -frames:v 1`)

## Open issues / not done

- **Waiting for an all-clear (Congo's live test)**: one full `build\RecorderTests.exe` run with the secondary-monitor placement (compiled, not yet run) and
  then the remaining measurements. Source HEAD c270ce3 is the last state whose full suite passed (108 of 108).
- The test window's own drawing was the bottleneck in the first Hard-scene figure: its frame took 3.8 ms to draw and 43.8 ms to put in the window
  (GDI+ converting a non-premultiplied bitmap), so it drew 21.8 fps. Fixed (premultiplied bitmap: 1.4 ms); the bench now also prints what the
  window draws when not being recorded.
- Measurements (MB per minute, GPU/encoder load, ffmpeg CPU) on the harder scene: after the live-test build.
- AMF and QSV never run.
- A `.mkv` left by a crash is not recovered automatically on the next start (it stays playable in the folder).
- Exclusive-fullscreen games can't be captured by window capture (borderless is fine).

## Test safety (added after Arma hit DXGI_ERROR_DEVICE_HUNG during test runs, 2026-10-17)

- `RecorderTests.exe` (suite, `--bench`, `--hold`) refuses to start while a game is running (any process named ArmaReforger*, arma3*, DayZ*; prints why, exit
  code 2) and ends itself (exit 3, which closes the Job Object and so ends ffmpeg) if one starts mid-run. A named mutex (`Global\BifrostRecorderTestRun`)
  stops two runs overlapping. `--guard-check` only lists processes; `--guard-test` runs the whole guard and nothing else (VERIFIED refusing with Arma running:
  exit code 2). The two-run mutex refusal is written but not exercised yet.
- No GPU run has been made since the guard was added; the full suite with the new window placement and file names is compiled but not yet run.
