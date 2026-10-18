// SPDX-License-Identifier: GPL-2.0-or-later
// The Recorder's decisions, with no window, no process and no libobs call in them, so the tests can check every one: where recordings go,
// what they're called, which encoder is used with which settings, what is captured and which sound goes on which track. C# 5 for Windows' own compiler.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

// The choices a recording is made with. They come from, in order of weight: the member's own choices in the Settings window (the prefs file),
// the experiment keys in BifrostRecorder.settings.json, and the defaults below.
class AppSettings {
  public string DataFolder;                  // null when the file gave none (Bifrost Suite's)
  public string TeamServer, TeamKey;         // the Armory, from the same file Bifrost Suite writes for Planner; the key is never shown, logged or written by the Recorder
  // ---- the six choices of the Settings window
  public string EncoderProfile = "standard"; // standard = the light set, best for games | quality = spatial AQ and B-frames, for desktop apps
  public int MaxHeight;                      // 0 = record at the window's own size | 1080 | 720
  public string CaptureMethod = "game";      // game (OBS's game capture: a hook inside the game copies each frame) | window (Windows' window capture) | screen (the whole monitor)
  public string Sound = "game";              // game (only the chosen program's sound) | pc (everything the PC plays)
  public string Mic = "off";                 // off | on (the default microphone, on its own track)
  public string GpuPriority = "normal";      // the GPU scheduling class asked for the Recorder: normal (leave it) | high (HIGH, else ABOVE_NORMAL)
  public string ThemeId = "console";         // the look (Theme.cs): console | midnight | od-green | paper | high-contrast
  public string Accent = "theme";            // the accent on top of it: theme (the preset's own hue) | a hue 0-359 | white | black
  // ---- settings.json only
  public string Hotkey = "Ctrl+Alt+R";       // the global start/stop key ("off" for none)
  public bool Cue = true;                    // a short beep on start and stop (they are not part of what's recorded)
  public bool Quality { get { return EncoderProfile == "quality"; } }
  public bool Screen { get { return CaptureMethod == "screen"; } }
  public bool WithMic { get { return Mic == "on"; } }
  public AppSettings Clone() { return (AppSettings)MemberwiseClone(); }
}

// The Settings window's choices, as plain labels, and what each one sets. Pure, so the tests can check the mapping.
static class SettingChoices {
  public enum Field { Encoder, Resolution, Capture, Sound, Mic, Gpu }

  public static readonly string[] Fields = { "ENCODER", "RESOLUTION", "CAPTURE", "SOUND", "MICROPHONE", "GPU PRIORITY" };

  public static string[] Labels(Field f) {
    switch (f) {
      case Field.Encoder: return new[] { "Light, best for games", "Quality, for desktop apps only (heavier on the GPU)" };
      case Field.Resolution: return new[] { "Native", "1080p", "720p" };
      case Field.Capture: return new[] { "Game capture, best for games", "This window", "The whole screen it's on" };
      case Field.Sound: return new[] { "The program's own sound", "Everything the PC plays" };
      case Field.Mic: return new[] { "Off", "On, on its own track" };
      default: return new[] { "Normal", "High" };
    }
  }

  public static string Hint(Field f) {
    switch (f) {
      case Field.Encoder: return "Light keeps the game's frame rate. Quality looks a little better but asks much more of the GPU.";
      case Field.Resolution: return "Scaling costs a little GPU time while a game runs.";
      case Field.Capture: return "Game capture copies each frame inside the game, like OBS. Other programs fall back to window capture.";
      case Field.Sound: return "The program's own sound leaves out Discord, music and everything else.";
      case Field.Mic: return "With the mic on, recordings get three tracks: Mix, Game and Mic.";
      default: return "High ranks the Recorder above other programs on the GPU.";
    }
  }

  // Which label is chosen now.
  public static int Get(AppSettings s, Field f) {
    switch (f) {
      case Field.Encoder: return s.Quality ? 1 : 0;
      case Field.Resolution: return s.MaxHeight == 1080 ? 1 : s.MaxHeight == 720 ? 2 : 0;
      case Field.Capture: return s.CaptureMethod == "window" ? 1 : s.CaptureMethod == "screen" ? 2 : 0;
      case Field.Sound: return s.Sound == "pc" ? 1 : 0;
      case Field.Mic: return s.WithMic ? 1 : 0;
      default: return s.GpuPriority == "high" ? 1 : 0;
    }
  }

  // Chooses a label. A MaxHeight set by hand in the settings file to some other height shows as Native until a choice is made.
  public static void Set(AppSettings s, Field f, int index) {
    switch (f) {
      case Field.Encoder: s.EncoderProfile = index == 1 ? "quality" : "standard"; break;
      case Field.Resolution: s.MaxHeight = index == 1 ? 1080 : index == 2 ? 720 : 0; break;
      case Field.Capture: s.CaptureMethod = index == 1 ? "window" : index == 2 ? "screen" : "game"; break;
      case Field.Sound: s.Sound = index == 1 ? "pc" : "game"; break;
      case Field.Mic: s.Mic = index == 1 ? "on" : "off"; break;
      default: s.GpuPriority = index == 1 ? "high" : "normal"; break;
    }
  }

  public static void Defaults(AppSettings s) {
    var d = new AppSettings();
    s.EncoderProfile = d.EncoderProfile; s.MaxHeight = d.MaxHeight; s.CaptureMethod = d.CaptureMethod; s.Sound = d.Sound; s.Mic = d.Mic; s.GpuPriority = d.GpuPriority;
  }
}

// BifrostRecorder.settings.json beside the program: Bifrost Suite writes it with DataFolder, the Recorder's work folder (and rewrites it whole on
// every install or update, so a choice kept there would be lost: the member's own choices are kept in the prefs file instead).
static class Settings {
  public const string FileName = "BifrostRecorder.settings.json";

  static Dictionary<string, object> Read(string file, out bool malformed) {
    malformed = false;
    try {
      if (file == null || !File.Exists(file)) return null;
      var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
      if (d == null) malformed = true;
      return d;
    } catch (Exception) { malformed = true; return null; }
  }

  static string Word(Dictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) && v is string ? ((string)v).Trim().ToLowerInvariant() : null; }

  // The six choices of the Settings window and the look, from a JSON object (settings.json or the prefs file). A value that isn't one of the known
  // ones leaves that choice as it was. Keys from older versions (FrameRateMode, EncoderCheck) are ignored.
  static void ApplyChoices(Dictionary<string, object> d, AppSettings s) {
    object v;
    string t = Word(d, "EncoderProfile");
    if (t == "quality") s.EncoderProfile = "quality"; else if (t == "standard" || t == "gentle") s.EncoderProfile = "standard";   // "gentle" is the 0.1.1 name for the light set
    t = Word(d, "CaptureMethod"); if (t == "game" || t == "window" || t == "screen") s.CaptureMethod = t;
    t = Word(d, "Sound"); if (t == "game" || t == "pc") s.Sound = t;
    t = Word(d, "Mic"); if (t == "on" || t == "off") s.Mic = t;
    t = Word(d, "GpuPriority"); if (t == "high" || t == "normal") s.GpuPriority = t;
    t = Word(d, "Theme"); if (t != null && Theming.Known(t)) s.ThemeId = t;
    if (d.TryGetValue("Accent", out v) && v != null && !(v is bool)) { string acc = Theming.NormalizeAccent(v); if (acc != "theme" || (v is string && ((string)v).Trim().ToLowerInvariant() == "theme")) s.Accent = acc; }
    if (d.TryGetValue("MaxHeight", out v) && v != null && !(v is bool) && !(v is string)) {
      try { int h = Convert.ToInt32(v, CultureInfo.InvariantCulture); if (h == 0 || (h >= 144 && h <= 8192)) s.MaxHeight = h; } catch (Exception) { }
    }
  }

  // Everything in the settings file; a missing file, bad JSON or an unusable value gives that setting's default.
  public static AppSettings Load(string settingsFile) {
    var s = new AppSettings();
    bool bad;
    var d = Read(settingsFile, out bad);
    if (d == null) return s;
    object v;
    if (d.TryGetValue("DataFolder", out v) && v is string) {
      string folder = ((string)v).Trim();
      if (folder.Length > 0 && folder.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathRooted(folder)) s.DataFolder = folder;
    }
    if (d.TryGetValue("TeamServer", out v) && v is string) s.TeamServer = ((string)v).Trim().TrimEnd('/');
    if (d.TryGetValue("TeamKey", out v) && v is string && ((string)v).Trim().Length > 0) s.TeamKey = ((string)v).Trim();
    if (d.TryGetValue("Hotkey", out v) && v is string && ((string)v).Trim().Length > 0) s.Hotkey = ((string)v).Trim();
    if (Word(d, "Cue") == "off") s.Cue = false;
    ApplyChoices(d, s);
    return s;
  }

  // The settings file, then the member's own choices on top of it (the prefs file; a choice there wins over the same key in the settings file).
  // problem is set when the prefs file exists but can't be read: it is then ignored, the rest stands, and nothing throws.
  public static AppSettings LoadAll(string settingsFile, string prefsFile, out string problem) {
    var s = Load(settingsFile);
    problem = null;
    bool bad;
    var d = Read(prefsFile, out bad);
    if (bad) problem = "Your saved choices couldn't be read, so the defaults are used until you change a setting.";
    else if (d != null) ApplyChoices(d, s);
    return s;
  }

  // The member's choices, written beside the Recorder's other data, never in the file Bifrost Suite rewrites. Throws on a disk error (the caller says so).
  public static void SavePrefs(string prefsFile, AppSettings s) {
    Directory.CreateDirectory(Path.GetDirectoryName(prefsFile));
    int hue;
    object accent = int.TryParse(s.Accent, NumberStyles.Integer, CultureInfo.InvariantCulture, out hue) ? (object)hue : s.Accent;
    var d = new Dictionary<string, object> { { "EncoderProfile", s.EncoderProfile }, { "MaxHeight", s.MaxHeight }, { "CaptureMethod", s.CaptureMethod }, { "Sound", s.Sound }, { "Mic", s.Mic }, { "GpuPriority", s.GpuPriority }, { "Theme", s.ThemeId }, { "Accent", accent } };
    string tmp = prefsFile + ".tmp";
    File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(d), new UTF8Encoding(false));
    if (File.Exists(prefsFile)) File.Delete(prefsFile);
    File.Move(tmp, prefsFile);
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

  // <DataFolder>\Recorder.prefs.json, or %LOCALAPPDATA%\Bifrost Recorder\prefs.json without one.
  public static string PrefsFile(string settingsFile, string localAppData) {
    string data = DataFolder(settingsFile);
    if (data != null) return Path.Combine(data, "Recorder.prefs.json");
    return Path.Combine(localAppData, "Bifrost Recorder", "prefs.json");
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

  // The base name, with " (2)", " (3)"... added when that name (as .mp4, or an .mkv from an older version) is already there.
  public static string Unique(string folder, string baseName) {
    string name = baseName;
    for (int i = 2; File.Exists(Path.Combine(folder, name + ".mp4")) || File.Exists(Path.Combine(folder, name + ".mkv")); i++) name = baseName + " (" + i + ")";
    return name;
  }
}

// One video encoder of libobs, and the settings the Recorder gives it.
class EncoderProfile {
  public string Name;        // libobs's encoder id
  public string Label;       // what the window shows
  public string Codec;       // what ffprobe calls the stream
  public bool Hardware;      // frames stay on the GPU all the way into the encoder
  public bool Verified;      // run on real hardware here; false = settings believed right, never run
  public string Short;       // the header chip's name for it

  // Constant quality with a ceiling (maxrate in kilobits a second, Sizes.MaxrateKbps) and a keyframe every 2 seconds. The light set (the default)
  // asks little of the GPU: NVENC preset p4, no B-frames, no spatial AQ (it runs on the GPU's CUDA cores, which a game starves: measured 56 fps
  // without it against 28 with it under Arma), no lookahead, one pass. qualitySet is the heavier one for recording something that isn't a game.
  public Dictionary<string, object> Settings(int maxrateKbps, int quality, bool qualitySet) {
    var d = new Dictionary<string, object> { { "keyint_sec", 2 } };
    switch (Name) {
      case "obs_nvenc_hevc_tex":
        d["rate_control"] = "CQVBR"; d["target_quality"] = quality; d["max_bitrate"] = maxrateKbps; d["bitrate"] = maxrateKbps;
        d["profile"] = "main"; d["tune"] = "hq"; d["preset"] = qualitySet ? "p5" : "p4"; d["multipass"] = qualitySet ? "qres" : "disabled";
        d["bf"] = qualitySet ? 2 : 0; d["adaptive_quantization"] = qualitySet; d["lookahead"] = false;
        break;
      case "h265_texture_amf":   // UNTESTED: no AMD GPU here. QVBR's quality level is the "cqp" value.
        d["rate_control"] = "QVBR"; d["cqp"] = quality; d["bitrate"] = maxrateKbps; d["profile"] = "main"; d["preset"] = qualitySet ? "quality" : "balanced"; d["bf"] = 0;
        break;
      case "obs_qsv11_hevc":     // UNTESTED: the frames live on the NVIDIA GPU here
        d["rate_control"] = "ICQ"; d["icq_quality"] = quality; d["profile"] = "main"; d["target_usage"] = qualitySet ? "TU2" : "TU4"; d["bframes"] = qualitySet ? 2 : 0;
        break;
      default:                   // obs_x264: the way out when there's no hardware encoder
        d["rate_control"] = "CRF"; d["crf"] = quality; d["preset"] = "veryfast"; d["profile"] = "high";
        break;
    }
    return d;
  }

  public static readonly EncoderProfile Nvenc = new EncoderProfile { Name = "obs_nvenc_hevc_tex", Short = "NVENC", Label = "NVIDIA NVENC, HEVC", Codec = "hevc", Hardware = true, Verified = true };
  public static readonly EncoderProfile Amf = new EncoderProfile { Name = "h265_texture_amf", Short = "AMF", Label = "AMD AMF, HEVC", Codec = "hevc", Hardware = true, Verified = false };
  public static readonly EncoderProfile Qsv = new EncoderProfile { Name = "obs_qsv11_hevc", Short = "QSV", Label = "Intel Quick Sync, HEVC", Codec = "hevc", Hardware = true, Verified = false };
  public static readonly EncoderProfile Software = new EncoderProfile { Name = "obs_x264", Short = "x264 CPU", Label = "Software, H.264 (uses the CPU)", Codec = "h264", Hardware = false, Verified = true };

  // The order they're tried in.
  public static readonly EncoderProfile[] Preference = { Nvenc, Amf, Qsv };

  public static EncoderProfile Find(string name) {
    foreach (var e in Preference) if (e.Name == name) return e;
    return name == Software.Name ? Software : null;
  }

  // The encoders to try, in order, from the one picked: the hardware ones after it that libobs registered, then the software one.
  public static List<EncoderProfile> FallbackChain(EncoderProfile first, ICollection<string> registered) {
    var list = new List<EncoderProfile> { first };
    int from = Array.IndexOf(Preference, first);
    if (from >= 0) for (int i = from + 1; i < Preference.Length; i++) if (registered.Contains(Preference[i].Name)) list.Add(Preference[i]);
    if (first != Software) list.Add(Software);
    return list;
  }
}

static class Sizes {
  public const int Fps = 60, Quality = 24;

  // About 30 Mb/s at 1920x1080, scaled by the number of pixels, never under 5 Mb/s.
  public static int MaxrateKbps(int width, int height) {
    double k = 30000.0 * ((double)width * height) / (1920.0 * 1080.0);
    return (int)Math.Max(5000, Math.Round(k / 100.0) * 100);
  }

  // The size of the video for a picture of clientWidth x clientHeight and the MaxHeight setting: its own (even) size, or, when it is
  // taller than MaxHeight, scaled down to MaxHeight keeping its shape. scaled says which.
  public static void OutputSize(int clientWidth, int clientHeight, int maxHeight, out int width, out int height, out bool scaled) {
    scaled = maxHeight > 0 && clientHeight > maxHeight;
    if (!scaled) { width = Math.Max(2, clientWidth & ~1); height = Math.Max(2, clientHeight & ~1); return; }
    height = Math.Max(2, maxHeight & ~1);
    width = Math.Max(2, (int)Math.Round(clientWidth * (double)height / clientHeight) & ~1);
  }
}

// What libobs is asked to capture, and which sound goes on which track.
static class Sources {
  // libobs's source for each capture method.
  public static string Id(string method) { return method == "window" ? "window_capture" : method == "screen" ? "monitor_capture" : "game_capture"; }

  // How OBS names a window in its settings: "title:class:program.exe", with # and : in each part written #22 and #3A (libobs window-helpers.c).
  public static string WindowSpec(string title, string className, string exeName) { return Enc(title) + ":" + Enc(className) + ":" + Enc(exeName); }
  static string Enc(string s) { return (s ?? "").Replace("#", "#22").Replace(":", "#3A"); }

  public const int MatchExe = 2;   // enum window_priority: the same program first, then the title

  // The capture's settings. The cursor is drawn; game capture uses OBS's own defaults for everything else, its anti-cheat compatibility hook included.
  // Window and screen capture ask Windows for SDR pictures (force_sdr): on an HDR monitor Windows otherwise hands over 16-bit float, which
  // libobs tone-maps at its own white level, not the monitor's (cyan came back 136,209,206 on this PC's HDR monitor); the video is SDR anyway.
  public static Dictionary<string, object> VideoSettings(string method, string windowSpec, string monitorDevice) {
    var d = new Dictionary<string, object>();
    if (method == "screen") { d["monitor_id"] = monitorDevice; d["capture_cursor"] = true; d["method"] = 0; d["force_sdr"] = true; }
    else if (method == "window") { d["window"] = windowSpec; d["priority"] = MatchExe; d["method"] = 2; d["cursor"] = true; d["client_area"] = true; d["force_sdr"] = true; }   // 2 = Windows Graphics Capture
    else { d["capture_mode"] = "window"; d["window"] = windowSpec; d["priority"] = MatchExe; d["capture_cursor"] = true; d["anti_cheat_hook"] = true; }
    return d;
  }

  // The audio tracks, in the file's order. With the mic: Mix (what players and the Armory play), the program's sound, the mic. Without: one track.
  public static string[] TrackNames(AppSettings c) {
    string sound = c.Sound == "pc" ? "PC sound" : "Game";
    return c.WithMic ? new[] { "Mix", sound, "Mic" } : new[] { sound };
  }

  // Which tracks a source feeds (bit 0 = the first track).
  public static uint SoundMixers(AppSettings c) { return c.WithMic ? 1u | 2u : 1u; }
  public static uint MicMixers(AppSettings c) { return c.WithMic ? 1u | 4u : 0u; }
}

// When the capture hasn't sent a picture yet. The whole start is capped at 15 s, so HookMs + PictureMs stays under it.
static class CaptureChoice {
  public const int HookMs = 6000;      // game capture: OBS retries a hook every 2 s (FakeGame hooked on the second try, 2.1 s): three tries
  public const int PictureMs = 5000;   // window and screen capture send their first picture within a second when they can
  public const int MinimizedMs = 3000; // a minimized window draws nothing until it is restored, whatever the capture

  // Asked about every 50 ms while a recording starts and the capture has sent no picture.
  //   method    the capture now in use: "game", "window" or "screen"
  //   waitedMs  how long that capture has been waiting for its first picture
  //   minimized the window is minimized
  // Returns null to keep waiting, another method to switch to (the wait then starts again for it), or "fail" to give up with an error.
  // Never "screen": that would record the whole monitor without being asked.
  public static string WhenNoPicture(string method, int waitedMs, bool minimized) {
    if (minimized) return waitedMs >= MinimizedMs ? "fail" : null;
    if (method == "game") return waitedMs >= HookMs ? "window" : null;   // nothing to hook (not Direct3D, OpenGL or Vulkan): the window as Windows sees it
    return waitedMs >= PictureMs ? "fail" : null;
  }
}

// The global start/stop key: "Ctrl+Alt+R" style text to RegisterHotKey's modifiers and virtual key. At least one modifier is required (a
// bare letter as a global key would swallow it everywhere).
static class Hotkeys {
  public const uint Alt = 1, Control = 2, Shift = 4, Win = 8;
  public const string Default = "Ctrl+Alt+R", Fallback = "Ctrl+Alt+Shift+R";   // the fallback only when the default is taken by another program

  // false (with the default filled in) when the text isn't usable. normalized is the text to show: "CTRL+ALT+R".
  public static bool TryParse(string text, out uint mods, out uint vk, out string normalized) {
    mods = 0; vk = 0; normalized = null;
    if (string.IsNullOrWhiteSpace(text)) return false;
    var shown = new List<string>();
    foreach (string raw in text.Split('+')) {
      string t = raw.Trim().ToLowerInvariant();
      if (t.Length == 0) return false;
      if (t == "ctrl" || t == "control") { if ((mods & Control) != 0) return false; mods |= Control; shown.Add("CTRL"); }
      else if (t == "alt") { if ((mods & Alt) != 0) return false; mods |= Alt; shown.Add("ALT"); }
      else if (t == "shift") { if ((mods & Shift) != 0) return false; mods |= Shift; shown.Add("SHIFT"); }
      else if (t == "win" || t == "windows") { if ((mods & Win) != 0) return false; mods |= Win; shown.Add("WIN"); }
      else {
        if (vk != 0) return false;   // one key
        if (t.Length == 1 && ((t[0] >= 'a' && t[0] <= 'z') || (t[0] >= '0' && t[0] <= '9'))) { vk = (uint)char.ToUpperInvariant(t[0]); shown.Add(t.ToUpperInvariant()); }
        else if (t[0] == 'f' && t.Length <= 3) { int n; if (!int.TryParse(t.Substring(1), out n) || n < 1 || n > 24) return false; vk = (uint)(0x70 + n - 1); shown.Add("F" + n); }
        else return false;
      }
    }
    if (vk == 0 || mods == 0) return false;
    normalized = string.Join("+", shown.ToArray());
    return true;
  }
}
