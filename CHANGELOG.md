# Bifrost Recorder changelog

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
