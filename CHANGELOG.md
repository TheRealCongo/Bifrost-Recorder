# Bifrost Recorder changelog

## 0.2.0
- Records with OBS Studio's engine (libobs, from OBS Studio 32.2.2, unchanged). Bifrost Recorder is now open source (GPL-2.0-or-later) and is not made or endorsed by the OBS Project.
- Game capture is the new default: OBS's own hook copies each frame inside the game, the way OBS and Medal record games, instead of competing with the game for the GPU. Programs that can't be hooked fall back to window capture by themselves after a few seconds.
- Sound: the chosen program's own sound (or everything the PC plays), and the microphone if you turn it on. With the mic on, recordings have three tracks: Mix, Game and Mic.
- Settings: Capture (game capture, this window, or the whole screen), Sound and Microphone are new; Frame rate is gone (recordings are always a steady 60 fps). The whole-screen choice now follows Resolution too.
- Recordings are written as MP4 from the start, in a form that still plays if the PC crashes; there is no more .mkv step on Stop.
- The recording engine starts when the Recorder opens (a second or two; Record is ready after it), so starting a recording is quick. Open the Recorder before the game for the smoothest start.
- Windows on an HDR monitor record with their true colours.
- The ffmpeg folder is gone; the program now carries the OBS files it uses, their licences and THIRD-PARTY-NOTICES.md.

## 0.1.3
- A Settings window (the Settings button by Refresh): Light or Quality encoder, Native, 1080p or 720p, This window or the whole screen, steady 60 fps or only real frames, GPU priority. Changes apply to the next recording and are saved at once; the header shows what is active.
- Appearance in Settings, as in the other Bifrost apps: five themes (Console, Midnight, OD green, Paper, High contrast) and an accent (the theme's own, a swatch or a hue slider), applied live. The app icon keeps its own colours.
- Your choices are kept in their own file (Recorder.prefs.json beside the Recorder's data) that Bifrost Suite never rewrites. A damaged file says so and the defaults are used.
- Easier on the GPU by default: the light encoder, normal GPU priority, and the encoder is chosen from the graphics card's maker without a test encode (another encoder is tried, and the status line says so, only if the first one doesn't start).
- Upload to Armory after a recording (with a key from Bifrost Suite, else the website's page opens), a start/stop key (Ctrl+Alt+R, or Ctrl+Alt+Shift+R if that is taken) and a short beep on start and stop, and the Recorder's own icon.

## 0.1.2
- Screen capture switch (`"CaptureMethod": "screen"` in BifrostRecorder.settings.json): records the whole monitor the chosen window is on.
- Variable frame rate switch (`"FrameRateMode": "vfr"`): only real frames, with their real times, no repeated frames.

## 0.1.1
- The window shows whether the recording is keeping up ("Keeping up" or "Falling behind: 0.53x real time").
- One small log per recording (settings, GPU priority, a progress line every second, how Stop went); the newest 20 are kept.
- ffmpeg is given a higher GPU priority so a busy game starves it less.
- Safer Stop: asks for a closing frame, and gives ffmpeg up to 30 seconds to finish writing before anything is ended.
- Settings switches for the encoder profile (standard or gentle), the video height (native, 1080 or 720) and GPU priority.

## 0.1.0
- Video recording of one chosen window: pick a window, Record, Stop; HEVC at 60 fps, saved as an MP4.
