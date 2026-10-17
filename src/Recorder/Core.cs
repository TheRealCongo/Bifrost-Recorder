// The Recorder's decisions, with no window and no process in them, so the tests can check every one: where recordings go, what
// they're called, which encoder is used and the exact ffmpeg arguments. C# 5 for Windows' own compiler.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

// BifrostRecorder.settings.json beside the program: Bifrost Suite writes it with DataFolder, the Recorder's work folder.
static class Settings {
  public const string FileName = "BifrostRecorder.settings.json";

  // DataFolder from the settings file, or null when the file is missing, isn't JSON, or holds no usable absolute folder.
  public static string DataFolder(string settingsFile) {
    try {
      if (settingsFile == null || !File.Exists(settingsFile)) return null;
      var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(settingsFile)) as Dictionary<string, object>;
      object v;
      if (d == null || !d.TryGetValue("DataFolder", out v) || !(v is string)) return null;
      string folder = ((string)v).Trim();
      if (folder.Length == 0 || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathRooted(folder)) return null;
      return folder;
    } catch (Exception) { return null; }
  }

  // <DataFolder>\Recordings, or %USERPROFILE%\Videos\Bifrost Recorder when Bifrost Suite hasn't given a folder.
  public static string RecordingsFolder(string settingsFile, string userProfile) {
    string data = DataFolder(settingsFile);
    if (data != null) return Path.Combine(data, "Recordings");
    return Path.Combine(userProfile, "Videos", "Bifrost Recorder");
  }
}

static class FileNames {
  // The program's name for the file name: its exe without ".exe", with anything a file name can't hold replaced.
  public static string ProgramName(string exePath, string windowTitle) {
    string name = string.IsNullOrEmpty(exePath) ? "" : Path.GetFileNameWithoutExtension(exePath);
    if (name.Trim().Length == 0) name = windowTitle ?? "";
    var sb = new StringBuilder();
    var bad = Path.GetInvalidFileNameChars();
    foreach (char c in name) sb.Append(Array.IndexOf(bad, c) >= 0 || c < 32 ? '_' : c);
    string s = sb.ToString().Trim().TrimEnd('.');
    if (s.Length > 60) s = s.Substring(0, 60).TrimEnd();
    return s.Length == 0 ? "Recording" : s;
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

  // Every tag below describes the pixels the encoder is given. NVENC converts d3d11 BGRA frames itself and measured BT.601
  // limited range (red = Y 81, Cb 90, Cr 240); tagging that BT.709 would shift every colour, so it is tagged as what it is.
  // Primaries and transfer stay BT.709 (the screen's). The software path converts with swscale and does produce BT.709.
  const string TagsGpu = "setparams=colorspace=smpte170m:range=tv:color_primaries=bt709:color_trc=bt709";
  const string TagsCpu = "setparams=colorspace=bt709:range=tv:color_primaries=bt709:color_trc=bt709";

  // The options go after "-map [v]". maxrate is in kilobits a second, scaled to the picture by FfmpegArgs.MaxrateKbps.
  public string Options(int maxrateKbps, int quality) {
    string max = maxrateKbps + "k", buf = (maxrateKbps * 2) + "k", q = quality.ToString(CultureInfo.InvariantCulture);
    switch (Name) {
      case "hevc_nvenc":
        // Constant quality with a ceiling: VBR with -cq and no average target, capped by maxrate. Two B-frames, spatial AQ.
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
    Name = "hevc_nvenc", Label = "NVIDIA NVENC, HEVC", Codec = "hevc", Hardware = true, Verified = true, Chain = "," + TagsGpu,
    Note = "d3d11 frames straight into the encoder" };
  public static readonly EncoderProfile Amf = new EncoderProfile {
    Name = "hevc_amf", Label = "AMD AMF, HEVC", Codec = "hevc", Hardware = true, Verified = false, Chain = "," + TagsGpu,
    Note = "UNTESTED: written from ffmpeg's documentation, never run on an AMD GPU" };
  public static readonly EncoderProfile Qsv = new EncoderProfile {
    Name = "hevc_qsv", Label = "Intel Quick Sync, HEVC", Codec = "hevc", Hardware = true, Verified = false, Chain = ",hwmap=derive_device=qsv,format=qsv," + TagsGpu,
    Note = "UNTESTED: written from ffmpeg's documentation" };
  public static readonly EncoderProfile Software = new EncoderProfile {
    Name = "libx264", Label = "Software, H.264 (uses the CPU)", Codec = "h264", Hardware = false, Verified = true,
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
  public static string Source(long hwnd) {
    return "gfxcapture=hwnd=" + hwnd + ":max_framerate=" + Fps + ":capture_cursor=true:display_border=false:resize_mode=scale_aspect:width=-2:height=-2";
  }

  // Record the window into a .mkv (which survives a crash). width and height are the window's size, for the bitrate ceiling.
  public static string Record(EncoderProfile enc, long hwnd, int width, int height, string mkv) {
    return "-hide_banner -loglevel warning -y -nostats -progress pipe:1 -stats_period 0.25 " +
           "-filter_complex " + Quote(Source(hwnd) + ",fps=" + Fps + enc.Chain + "[v]") + " -map \"[v]\" " +
           enc.Options(MaxrateKbps(width, height), Quality) + " -fps_mode cfr -flush_packets 1 " + Quote(mkv);
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
           enc.Options(MaxrateKbps(640, 360), Quality) + " -f null -";
  }
}
