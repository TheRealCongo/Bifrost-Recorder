// The Recorder's decisions, with no window and no process in them, so the tests can check every one: where recordings go, what
// they're called, which encoder is used and the exact ffmpeg arguments. C# 5 for Windows' own compiler.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

// What BifrostRecorder.settings.json holds. DataFolder is Bifrost Suite's; the other three are experiment switches, not shown in
// the window (the header chip shows the active set): EncoderProfile standard (the light default) | quality (the older set; "gentle" in an older settings file means standard), MaxHeight 0 (native) | 720 | 1080 ... (scaling is costly under a game),, GpuPriority high | normal.
class AppSettings {
  public string DataFolder;                  // null when the file gave none
  public string TeamServer, TeamKey;         // the Armory, from the same file Bifrost Suite writes for Planner; the key is never shown, logged or written by the Recorder
  public string EncoderProfile = "standard";
  public int MaxHeight;                      // 0 = record at the window's own size
  public string GpuPriority = "high";
  public string CaptureMethod = "window";    // window (gfxcapture) | screen (ddagrab, the whole monitor the window is on)
  public string FrameRateMode = "cfr";       // cfr (a frame every 1/60 s, repeats filling gaps) | vfr (only real frames, with their real times)
  public bool Quality { get { return EncoderProfile == "quality"; } }   // the older AQ and B-frame set, for recording what isn't a game
  public bool Screen { get { return CaptureMethod == "screen"; } }
  public bool Vfr { get { return FrameRateMode == "vfr"; } }
}

// BifrostRecorder.settings.json beside the program: Bifrost Suite writes it with DataFolder, the Recorder's work folder.
static class Settings {
  public const string FileName = "BifrostRecorder.settings.json";

  static Dictionary<string, object> Read(string settingsFile) {
    try {
      if (settingsFile == null || !File.Exists(settingsFile)) return null;
      return new JavaScriptSerializer().DeserializeObject(File.ReadAllText(settingsFile)) as Dictionary<string, object>;
    } catch (Exception) { return null; }
  }

  // Everything in the file; a missing file, bad JSON or an unusable value gives that setting's default.
  public static AppSettings Load(string settingsFile) {
    var s = new AppSettings();
    var d = Read(settingsFile);
    if (d == null) return s;
    object v;
    if (d.TryGetValue("DataFolder", out v) && v is string) {
      string folder = ((string)v).Trim();
      if (folder.Length > 0 && folder.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathRooted(folder)) s.DataFolder = folder;
    }
    if (d.TryGetValue("EncoderProfile", out v) && v is string && ((string)v).Trim().ToLowerInvariant() == "quality") s.EncoderProfile = "quality";   // "gentle" (the 0.1.1 name for the light set) and anything else is standard
    if (d.TryGetValue("TeamServer", out v) && v is string) s.TeamServer = ((string)v).Trim().TrimEnd('/');
    if (d.TryGetValue("TeamKey", out v) && v is string && ((string)v).Trim().Length > 0) s.TeamKey = ((string)v).Trim();
    if (d.TryGetValue("CaptureMethod", out v) && v is string && ((string)v).Trim().ToLowerInvariant() == "screen") s.CaptureMethod = "screen";
    if (d.TryGetValue("FrameRateMode", out v) && v is string && ((string)v).Trim().ToLowerInvariant() == "vfr") s.FrameRateMode = "vfr";
    if (d.TryGetValue("GpuPriority", out v) && v is string && ((string)v).Trim().ToLowerInvariant() == "normal") s.GpuPriority = "normal";
    if (d.TryGetValue("MaxHeight", out v) && v != null && !(v is bool) && !(v is string)) {
      try { int h = Convert.ToInt32(v, CultureInfo.InvariantCulture); if (h >= 144 && h <= 8192) s.MaxHeight = h; } catch (Exception) { }
    }
    return s;
  }

  // DataFolder from the settings file, or null when the file is missing, isn't JSON, or holds no usable absolute folder.
  public static string DataFolder(string settingsFile) { return Load(settingsFile).DataFolder; }

  // <DataFolder>\Recordings, or %USERPROFILE%\Videos\Bifrost Recorder when Bifrost Suite hasn't given a folder.
  public static string RecordingsFolder(string settingsFile, string userProfile) {
    string data = DataFolder(settingsFile);
    if (data != null) return Path.Combine(data, "Recordings");
    return Path.Combine(userProfile, "Videos", "Bifrost Recorder");
  }

  // <DataFolder>\logs, or %LOCALAPPDATA%\Bifrost Recorder\logs without one. Never inside the recordings folder.
  public static string LogsFolder(string settingsFile, string localAppData) {
    string data = DataFolder(settingsFile);
    if (data != null) return Path.Combine(data, "logs");
    return Path.Combine(localAppData, "Bifrost Recorder", "logs");
  }
}
static class FileNames {
  // The program's friendly name for the file name, from the exe's version info (its FileDescription, e.g. "Arma Reforger").
  public static string Friendly(string exePath, string windowTitle) {
    string description = null;
    try { if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath)) description = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).FileDescription; } catch (Exception) { }
    return ProgramName(exePath, windowTitle, description);
  }

  static readonly string[] Reserved = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

  // The name for the file: the FileDescription if the exe has one, else the window's title, else the exe's name without ".exe", else
  // "Recording"; anything a Windows file name can't hold replaced, no trailing dots or spaces, device names (CON, NUL...) avoided.
  public static string ProgramName(string exePath, string windowTitle, string fileDescription) {
    string name = (fileDescription ?? "").Trim();
    if (name.Length == 0) name = (windowTitle ?? "").Trim();
    if (name.Length == 0) name = string.IsNullOrEmpty(exePath) ? "" : Path.GetFileNameWithoutExtension(exePath);
    var sb = new StringBuilder();
    var bad = Path.GetInvalidFileNameChars();
    foreach (char c in name) sb.Append(Array.IndexOf(bad, c) >= 0 || c < 32 ? '_' : c);
    string s = sb.ToString().Trim().TrimEnd('.', ' ');
    if (s.Length > 60) s = s.Substring(0, 60).TrimEnd();
    if (s.Length == 0) return "Recording";
    if (Array.IndexOf(Reserved, s.ToUpperInvariant()) >= 0) s += "_";
    return s;
  }

  // "<program> yyyy-MM-dd HH-mm-ss" without an extension.
  public static string BaseName(string program, DateTime when) {
    return program + " " + when.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
  }

  // The base name, with " (2)", " (3)"... added when that name (as .mp4 or .mkv) is already there.
  public static string Unique(string folder, string baseName) {
    string name = baseName;
    for (int i = 2; File.Exists(Path.Combine(folder, name + ".mp4")) || File.Exists(Path.Combine(folder, name + ".mkv")); i++) name = baseName + " (" + i + ")";
    return name;
  }
}

// One way of encoding. Everything after the capture and the 60 fps clock is here: the filters that make the frames what the
// encoder wants, and the encoder's own options.
class EncoderProfile {
  public string Name;        // ffmpeg's encoder name
  public string Label;       // what the window shows
  public string Codec;       // what ffprobe calls the stream
  public bool Hardware;      // frames stay on the GPU all the way into the encoder
  public bool Verified;      // run on real hardware here; false = arguments believed right, never run
  public string Chain;       // filters after the 60 fps clock, each preceded by a comma
  public string Note;
  public string Short;       // the header chip's name for it

  // Every tag below describes the pixels the encoder is given. NVENC converts d3d11 BGRA frames itself and measured BT.601
  // limited range (red = Y 81, Cb 90, Cr 240); tagging that BT.709 would shift every colour, so it is tagged as what it is.
  // Primaries and transfer stay BT.709 (the screen's). The software path converts with swscale and does produce BT.709.
  const string TagsGpu = "setparams=colorspace=smpte170m:range=tv:color_primaries=bt709:color_trc=bt709";
  const string TagsCpu = "setparams=colorspace=bt709:range=tv:color_primaries=bt709:color_trc=bt709";

  // The options go after "-map [v]". maxrate is in kilobits a second, scaled to the picture by FfmpegArgs.MaxrateKbps.
  // NVENC has two sets. The default ("standard") asks little of the GPU: preset p4, no B-frames, no spatial AQ (it runs on the GPU's CUDA cores,
  // which a game starves: measured 56 fps without it against 28 with it under Arma), no lookahead, a bufsize equal to the ceiling. "quality" is the
  // older set for recording something that isn't a game: preset p5, spatial AQ, 2 B-frames with middle references, double bufsize, forced IDR keyframes.
  public string Options(int maxrateKbps, int quality, bool qualitySet) {
    string max = maxrateKbps + "k", buf = (maxrateKbps * 2) + "k", q = quality.ToString(CultureInfo.InvariantCulture);
    switch (Name) {
      case "hevc_nvenc":
        if (!qualitySet)
          return "-c:v hevc_nvenc -preset p4 -tune hq -profile:v main -rc vbr -cq " + q + " -b:v 0 -maxrate " + max + " -bufsize " + max +
                 " -bf 0 -spatial-aq 0 -rc-lookahead 0 -multipass disabled -g 120";
        // Constant quality with a ceiling: VBR with -cq and no average target, capped by maxrate.
        return "-c:v hevc_nvenc -preset p5 -tune hq -profile:v main -rc vbr -cq " + q + " -b:v 0 -maxrate " + max + " -bufsize " + buf +
               " -spatial-aq 1 -bf 2 -b_ref_mode middle -g 120 -forced-idr 1";
      case "hevc_amf":   // UNTESTED: no AMD GPU here
        return "-c:v hevc_amf -usage high_quality -quality high_quality -profile:v main -rc qvbr -qvbr_quality_level " + q + " -maxrate " + max + " -bufsize " + buf +
               " -vbaq 1 -g 120";
      case "hevc_qsv":   // UNTESTED unless Verified is set: the arguments are Intel's documented ones
        return "-c:v hevc_qsv -preset slow -profile:v main -global_quality " + q + " -look_ahead 1 -maxrate " + max + " -bufsize " + buf + " -g 120 -forced_idr 1";
      default:           // libx264: the way out when there's no hardware encoder
        return "-c:v libx264 -preset veryfast -profile:v high -crf " + q + " -maxrate " + max + " -bufsize " + buf + " -g 120";
    }
  }

  public static readonly EncoderProfile Nvenc = new EncoderProfile {
    Name = "hevc_nvenc", Short = "NVENC", Label = "NVIDIA NVENC, HEVC", Codec = "hevc", Hardware = true, Verified = true, Chain = "," + TagsGpu,
    Note = "d3d11 frames straight into the encoder" };
  public static readonly EncoderProfile Amf = new EncoderProfile {
    Name = "hevc_amf", Short = "AMF", Label = "AMD AMF, HEVC", Codec = "hevc", Hardware = true, Verified = false, Chain = "," + TagsGpu,
    Note = "UNTESTED: written from ffmpeg's documentation, never run on an AMD GPU" };
  public static readonly EncoderProfile Qsv = new EncoderProfile {
    Name = "hevc_qsv", Short = "QSV", Label = "Intel Quick Sync, HEVC", Codec = "hevc", Hardware = true, Verified = false, Chain = ",hwmap=derive_device=qsv,format=qsv," + TagsGpu,
    Note = "UNTESTED: written from ffmpeg's documentation" };
  public static readonly EncoderProfile Software = new EncoderProfile {
    Name = "libx264", Short = "x264 CPU", Label = "Software, H.264 (uses the CPU)", Codec = "h264", Hardware = false, Verified = true,
    Chain = ",hwdownload,format=bgra,scale=out_color_matrix=bt709:out_range=tv,format=yuv420p," + TagsCpu,
    Note = "frames come down to the CPU: only used when no GPU encoder works" };

  // The order they're tried in.
  public static readonly EncoderProfile[] Preference = { Nvenc, Amf, Qsv };

  public static EncoderProfile Find(string name) {
    foreach (var e in Preference) if (e.Name == name) return e;
    return name == Software.Name ? Software : null;
  }

  // The first GPU encoder the test accepts, else the software one. The test is handed in so this stays a pure decision.
  public static EncoderProfile Pick(Func<EncoderProfile, bool> works) {
    foreach (var e in Preference) if (works(e)) return e;
    return Software;
  }
}

static class FfmpegArgs {
  public const int Fps = 60, Quality = 24;

  // About 30 Mb/s at 1920x1080, scaled by the number of pixels, never under 5 Mb/s.
  public static int MaxrateKbps(int width, int height) {
    double k = 30000.0 * ((double)width * height) / (1920.0 * 1080.0);
    return (int)Math.Max(5000, Math.Round(k / 100.0) * 100);
  }

  public static string Quote(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }

  // The capture itself: this window, at most 60 a second, the pointer drawn, no yellow border (the border option is what
  // makes the capture the window's own area, without its title bar and frame), an even size (encoders need one).
  public static string Source(long hwnd) { return Source(hwnd, 0, 0); }

  // With a size (outWidth and outHeight above 0) the capture is scaled to fit that canvas, keeping its shape (resize_mode=scale_aspect,
  // done by the capture filter itself on the GPU); with 0 it keeps the window's own size, rounded down to even.
  public static string Source(long hwnd, int outWidth, int outHeight) {
    string size = outWidth > 0 && outHeight > 0 ? "width=" + outWidth + ":height=" + outHeight : "width=-2:height=-2";
    return "gfxcapture=hwnd=" + hwnd + ":max_framerate=" + Fps + ":capture_cursor=true:display_border=false:resize_mode=scale_aspect:" + size;
  }

  // The size of the video for a window of clientWidth x clientHeight and the MaxHeight setting: its own (even) size, or, when it is
  // taller than MaxHeight, scaled down to MaxHeight keeping its shape. scaled says which.
  public static void OutputSize(int clientWidth, int clientHeight, int maxHeight, out int width, out int height, out bool scaled) {
    scaled = maxHeight > 0 && clientHeight > maxHeight;
    if (!scaled) { width = Math.Max(2, clientWidth & ~1); height = Math.Max(2, clientHeight & ~1); return; }
    height = Math.Max(2, maxHeight & ~1);
    width = Math.Max(2, (int)Math.Round(clientWidth * (double)height / clientHeight) & ~1);
  }

  // Record the window into a .mkv (which survives a crash). width and height are the window's size (its client area).
  public static string Record(EncoderProfile enc, long hwnd, int width, int height, string mkv) { return Record(enc, hwnd, width, height, mkv, new AppSettings()); }

  public static string Record(EncoderProfile enc, long hwnd, int width, int height, string mkv, AppSettings cfg) { return Record(enc, hwnd, width, height, mkv, cfg, null); }

  // Frame timing. cfr: the fps filter and -fps_mode cfr make a frame every 1/60 s, repeating the last one when the picture hasn't changed.
  // vfr: no fps filter and -fps_mode passthrough: only frames that really arrived, each with its capture time (the capture time base is
  // 1/10,000,000). ffmpeg would round those times to the 1/60 grid (the encoder time base defaults to 1/frame rate), so -enc_time_base
  // filter keeps them exact. -r 60 is NOT used: ffmpeg refuses it with passthrough ("-r ... together with a non-CFR -fps_mode ... is
  // contradictory"), and the capture filters already tell the encoder 60 fps, so it doesn't derive a rate from the time base.
  public const string CfrSync = "-fps_mode cfr", VfrSync = "-fps_mode passthrough -enc_time_base filter";

  // screen: when cfg.Screen, the whole monitor the window is on, by ddagrab (frames stay Direct3D 11 into the encoder, the pointer drawn);
  // the DXGI adapter is chosen with -init_hw_device because ddagrab's output_idx counts the outputs of the device's own adapter.
  // MaxHeight can't apply there (ddagrab can't scale, and scale_d3d11 doesn't work on this PC), so the video is the monitor's size.
  // In vfr mode ddagrab is told dup_frames=false, so it sends only real desktop updates instead of repeating frames itself.
  public static string Record(EncoderProfile enc, long hwnd, int width, int height, string mkv, AppSettings cfg, DxgiOutput screen) {
    int ow, oh; bool scaled = false;
    string hw = "", source;
    if (cfg.Screen && screen != null) {
      ow = Math.Max(2, screen.Width & ~1); oh = Math.Max(2, screen.Height & ~1);
      hw = "-init_hw_device d3d11va=dda:" + screen.Adapter + " -filter_hw_device dda ";
      source = "ddagrab=output_idx=" + screen.Output + ":framerate=" + Fps + ":draw_mouse=true" + (cfg.Vfr ? ":dup_frames=false" : "");
    } else {
      OutputSize(width, height, cfg.MaxHeight, out ow, out oh, out scaled);
      source = scaled ? Source(hwnd, ow, oh) : Source(hwnd);
    }
    return "-hide_banner -loglevel warning -y -nostats -progress pipe:1 -stats_period 0.25 " + hw +
           "-filter_complex " + Quote(source + (cfg.Vfr ? "" : ",fps=" + Fps) + enc.Chain + "[v]") + " -map \"[v]\" " +
           enc.Options(MaxrateKbps(ow, oh), Quality, cfg.Quality) + " " + (cfg.Vfr ? VfrSync : CfrSync) + " -flush_packets 1 " + Quote(mkv);
  }

  // mkv to mp4 without re-encoding. hvc1 is the HEVC tag players on Windows, Mac and the web all open.
  public static string Remux(EncoderProfile enc, string mkv, string mp4) {
    return "-hide_banner -loglevel error -nostdin -y -i " + Quote(mkv) + " -map 0:v:0 -c copy " + (enc.Codec == "hevc" ? "-tag:v hvc1 " : "") +
           "-movflags +faststart " + Quote(mp4);
  }

  public static string Probe(string file, bool countFrames) {
    return "-v error " + (countFrames ? "-count_frames " : "") + "-select_streams v:0 -show_entries stream=codec_name,profile,width,height,r_frame_rate,avg_frame_rate,nb_frames,nb_read_frames,pix_fmt," +
           "color_range,color_space,color_transfer,color_primaries,codec_tag_string:format=duration,size -of json " + Quote(file);
  }

  // A half-second encode with the encoder's own options and chain, from a made-up picture uploaded to the GPU the same way
  // the capture's frames are. It proves the encoder, its options and the filters work on this PC; it captures nothing.
  public static string TestEncode(EncoderProfile enc) {
    string hw = enc.Hardware ? "-init_hw_device d3d11va=d3d -filter_hw_device d3d " : "";
    string chain = enc.Hardware ? "format=bgra,hwupload" + enc.Chain : "format=bgra" + enc.Chain.Replace(",hwdownload,format=bgra", "");
    return "-hide_banner -loglevel error -nostdin -y " + hw + "-f lavfi -i color=c=0x303840:s=640x360:r=60:d=0.5 -vf " + Quote(chain) + " " +
           enc.Options(MaxrateKbps(640, 360), Quality, false) + " -f null -";
  }
}
