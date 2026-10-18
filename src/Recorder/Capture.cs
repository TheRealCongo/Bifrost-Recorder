// Recording a window with the bundled ffmpeg: finding the encoder that works, running the capture, stopping it cleanly, and
// turning the .mkv into the .mp4. C# 5 for Windows' own compiler.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Where ffmpeg is: build\bin beside the program.
static class Ffmpeg {
  public static string Dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin");
  public static string Exe { get { return Path.Combine(Dir, "ffmpeg.exe"); } }
  public static string ProbeExe { get { return Path.Combine(Dir, "ffprobe.exe"); } }
}

// Which encoder this PC uses: a half-second test encode of each, once; the answer is kept for as long as the program runs.
static class EncoderChoice {
  static EncoderProfile picked;
  static readonly object gate = new object();
  public static string Log = "";   // what was tried, for the status line and the tests

  public static EncoderProfile Picked { get { lock (gate) return picked; } }

  // No test encode: the encoder of the first GPU maker DXGI lists (NVIDIA NVENC, AMD AMF, Intel QSV), else the software one. Used when a test encode
  // itself is a risk (a session started next to a running game has reset this PC's GPU driver). The real start then falls back with Next() if the pick fails.
  public static EncoderProfile ByVendor() {
    EncoderProfile pick = EncoderProfile.Software;
    foreach (var o in Dxgi.Outputs()) {
      if (o.VendorId == 0x10DE) pick = EncoderProfile.Nvenc; else if (o.VendorId == 0x1002) pick = EncoderProfile.Amf; else if (o.VendorId == 0x8086) pick = EncoderProfile.Qsv; else continue;
      break;
    }
    lock (gate) { picked = pick; Log = "encoder chosen by the GPU maker (no test encode): " + pick.Name + Environment.NewLine; }
    return pick;
  }

  // The encoder to try after one that failed to start: the ones after it in the order, then the software one; null after the software one.
  public static EncoderProfile Next(EncoderProfile failed) {
    if (failed == EncoderProfile.Software) return null;
    int i = Array.IndexOf(EncoderProfile.Preference, failed);
    return i >= 0 && i + 1 < EncoderProfile.Preference.Length ? EncoderProfile.Preference[i + 1] : EncoderProfile.Software;
  }

  public static void Set(EncoderProfile e) { lock (gate) picked = e; }
  public static void Reset() { lock (gate) { picked = null; Log = ""; } }   // for the tests

  public static bool Works(string ffmpegExe, EncoderProfile e, out string why) {
    string so, se;
    int code = Proc.Run(ffmpegExe, FfmpegArgs.TestEncode(e), 20000, out so, out se);
    why = code == 0 ? null : ("exit " + code + ": " + Last(se, 2));
    return code == 0;
  }

  public static EncoderProfile Detect(string ffmpegExe) {
    lock (gate) {
      if (picked != null) return picked;
      var log = new StringBuilder();
      picked = EncoderProfile.Pick(delegate(EncoderProfile e) {
        string why;
        bool ok = Works(ffmpegExe, e, out why);
        log.AppendLine(e.Name + ": " + (ok ? "works" : "no (" + why + ")"));
        return ok;
      });
      log.AppendLine("using " + picked.Name);
      Log = log.ToString();
      return picked;
    }
  }

  public static string Last(string text, int lines) {
    var all = new List<string>();
    foreach (var l in (text ?? "").Split('\n')) if (l.Trim().Length > 0) all.Add(l.Trim());
    int from = Math.Max(0, all.Count - lines);
    return string.Join(" | ", all.GetRange(from, all.Count - from).ToArray());
  }
}

class ProbeResult {
  public bool Ok;
  public string Codec, Profile, PixFmt, FrameRate, AvgFrameRate, Range, Space, Transfer, Primaries, Tag, Error;
  public int Width, Height;
  public long Frames = -1;       // nb_frames from the file, or the count when asked to count
  public double Duration;
  public long Size;

  static string S(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null; }
  static long L(string s) { long v; return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1; }
  static double D(string s) { double v; return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0; }

  // Asks ffprobe about a file's first video stream. countFrames decodes the whole file to count its frames (slow on long files).
  public static ProbeResult Run(string file, bool countFrames) {
    var r = new ProbeResult();
    string so, se;
    int code = Proc.Run(Ffmpeg.ProbeExe, FfmpegArgs.Probe(file, countFrames), countFrames ? 300000 : 30000, out so, out se);
    if (code != 0) { r.Error = "ffprobe exit " + code + ": " + EncoderChoice.Last(se, 2); return r; }
    try {
      var root = new JavaScriptSerializer().DeserializeObject(so) as Dictionary<string, object>;
      var streams = root != null && root.ContainsKey("streams") ? root["streams"] as object[] : null;
      if (streams == null || streams.Length == 0) { r.Error = "no video stream"; return r; }
      var s = (Dictionary<string, object>)streams[0];
      r.Codec = S(s, "codec_name"); r.Profile = S(s, "profile"); r.PixFmt = S(s, "pix_fmt");
      r.Width = (int)L(S(s, "width") ?? "0"); r.Height = (int)L(S(s, "height") ?? "0");
      r.FrameRate = S(s, "r_frame_rate"); r.AvgFrameRate = S(s, "avg_frame_rate");
      r.Range = S(s, "color_range"); r.Space = S(s, "color_space"); r.Transfer = S(s, "color_transfer"); r.Primaries = S(s, "color_primaries");
      r.Tag = S(s, "codec_tag_string");
      r.Frames = countFrames ? L(S(s, "nb_read_frames")) : L(S(s, "nb_frames") ?? "-1");
      var fmt = root.ContainsKey("format") ? root["format"] as Dictionary<string, object> : null;
      if (fmt != null) { r.Duration = D(S(fmt, "duration")); r.Size = L(S(fmt, "size") ?? "0"); }
      r.Ok = r.Codec != null && r.Width > 0 && r.Height > 0;
      if (!r.Ok) r.Error = "ffprobe found no usable picture";
    } catch (Exception ex) { r.Error = "ffprobe output unreadable: " + ex.Message; }
    return r;
  }
}

class RecordingResult {
  public bool Ok;              // an .mp4 was made, probed and the .mkv removed
  public string Mp4, Mkv;      // Mkv is set only when it was kept
  public string Message;
  public double Seconds;       // length of the saved video
  public long Bytes;           // size of the saved file
  public long Frames, Dup, Drop;
  public bool Forced;          // ffmpeg had to be ended by force
  public double StopSeconds;   // how long Stop took, ffmpeg's finish and the remux
  public ProbeResult Probe;
}

// What ffmpeg's -progress says (frame, fps, speed, dup_frames, drop_frames, out_time_us, total_size) and what to make of it.
// speed is ffmpeg's own, averaged since the start; what the window shows is the speed over the last few seconds, worked out
// from how far the written video has got (out_time_us) against the clock, so a recording that has fallen behind says so while
// it is happening. A window that has stopped drawing is not "behind": nothing is arriving.
class Telemetry {
  public const double BehindBelow = 0.95, BehindForSeconds = 5.0, WindowSeconds = 4.0, StalledAfter = 2.0;
  public long Frames, Dup, Drop, TotalSize, OutUs = -1;
  public double Fps, CumulativeSpeed = double.NaN, WindowSpeed = double.NaN;
  public bool Stalled, Behind;
  readonly List<double[]> samples = new List<double[]>();   // { clock seconds, written video seconds }
  long lastFrames = -1;
  double lastChange, behindSince = -1;

  // One line of -progress output.
  public void Feed(string line) {
    int eq = line.IndexOf('=');
    if (eq <= 0) return;
    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
    lock (this) {
      long n; double d;
      if (k == "frame" && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) Frames = n;
      else if (k == "dup_frames" && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) Dup = n;
      else if (k == "drop_frames" && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) Drop = n;
      else if (k == "total_size" && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) TotalSize = n;
      else if (k == "out_time_us" && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) OutUs = n;
      else if (k == "fps" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) Fps = d;
      else if (k == "speed" && double.TryParse(v.TrimEnd('x', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) CumulativeSpeed = d;
    }
  }

  // Every 200 ms or so, with the seconds on the recording's clock.
  public void Tick(double now) {
    lock (this) {
      if (Frames != lastFrames) { lastFrames = Frames; lastChange = now; }
      Stalled = now - lastChange >= StalledAfter;
      if (OutUs >= 0) {
        samples.Add(new[] { now, OutUs / 1e6 });
        while (samples.Count > 2 && now - samples[0][0] > WindowSeconds) samples.RemoveAt(0);
      }
      WindowSpeed = double.NaN;
      if (samples.Count >= 2) {
        var a = samples[0]; var b = samples[samples.Count - 1];
        if (b[0] - a[0] >= 2.0) WindowSpeed = (b[1] - a[1]) / (b[0] - a[0]);
      }
      if (Stalled) behindSince = -1;
      else if (!double.IsNaN(WindowSpeed)) {
        if (WindowSpeed < BehindBelow) { if (behindSince < 0) behindSince = now; }
        else behindSince = -1;
      }
      Behind = behindSince >= 0 && now - behindSince > BehindForSeconds;
    }
  }

  // Seconds of video that should have been written by now and haven't been.
  public double LagSeconds(double now) { lock (this) return OutUs < 0 ? 0 : Math.Max(0, now - OutUs / 1e6); }

  public string Text {
    get {
      lock (this) {
        if (Stalled) return "Waiting for the window to draw";
        if (Behind) return "Falling behind: " + WindowSpeed.ToString("0.00", CultureInfo.InvariantCulture) + "x real time";
        return double.IsNaN(WindowSpeed) ? "Keeping up" : "Keeping up · " + WindowSpeed.ToString("0.00", CultureInfo.InvariantCulture) + "x";
      }
    }
  }

  public string Line(double now) {
    lock (this)
      return "frame=" + Frames + " fps=" + Fps.ToString("0.0", CultureInfo.InvariantCulture) + " speed=" + (double.IsNaN(CumulativeSpeed) ? "?" : CumulativeSpeed.ToString("0.00", CultureInfo.InvariantCulture)) +
             "x(ffmpeg, since start) window=" + (double.IsNaN(WindowSpeed) ? "?" : WindowSpeed.ToString("0.00", CultureInfo.InvariantCulture)) + "x dup=" + Dup + " drop=" + Drop +
             " size=" + (TotalSize / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "MB out=" + (OutUs < 0 ? "?" : (OutUs / 1e6).ToString("0.0", CultureInfo.InvariantCulture)) +
             "s lag=" + LagSeconds(now).ToString("0.0", CultureInfo.InvariantCulture) + "s " + (Stalled ? "[no new frames]" : Behind ? "[BEHIND]" : "[ok]");
  }
}

// One small text log per recording: <DataFolder>\logs, or %LOCALAPPDATA%\Bifrost Recorder\logs, never the recordings folder. The newest 20
// are kept. Writing it can never stop a recording: every failure is swallowed.
class RecordingLog {
  public const int Keep = 20;
  public readonly string Path;
  StreamWriter w;
  readonly object gate = new object();
  readonly Stopwatch sw = Stopwatch.StartNew();

  public RecordingLog(string folder, string name) {
    if (string.IsNullOrEmpty(folder)) return;
    try {
      Directory.CreateDirectory(folder);
      Prune(folder, Keep - 1);
      Path = System.IO.Path.Combine(folder, name + ".log");
      w = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
      w.AutoFlush = true;
    } catch (Exception) { w = null; }
  }

  public void Line(string text) {
    lock (gate) {
      if (w == null) return;
      try { w.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  +" + sw.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s  " + text); } catch (Exception) { }
    }
  }

  public void Close() { lock (gate) { if (w != null) { try { w.Dispose(); } catch (Exception) { } w = null; } } }

  // Deletes the oldest .log files until only keep are left.
  public static void Prune(string folder, int keep) {
    try {
      var files = new List<FileInfo>(new DirectoryInfo(folder).GetFiles("*.log"));
      files.Sort(delegate(FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
      for (int i = keep; i < files.Count; i++) { try { files[i].Delete(); } catch (Exception) { } }
    } catch (Exception) { }
  }
}

// One recording of one window. Start returns once the first picture has arrived; Stop writes the finished file.
class Recording {
  public readonly IntPtr Hwnd;
  public readonly EncoderProfile Encoder;
  public readonly AppSettings Config;
  public readonly Telemetry Tele = new Telemetry();
  public readonly string MkvPath, Mp4Path;
  readonly int width, height;
  readonly RecordingLog log;
  readonly DxgiOutput screen;      // screen capture: the monitor (null for window capture)
  Process proc;
  Thread monitor;
  Stopwatch clock;
  readonly object gate = new object();
  readonly List<string> errors = new List<string>();
  DateTime lastFrameAt = DateTime.UtcNow;
  long lastCount;
  volatile bool stopping, ended;
  int endedRaised, gpuTries, gpuClass;
  volatile bool draining;
  public string Command;               // what ffmpeg was started with (the tests print it)
  public int OutWidth, OutHeight;      // the size of the video
  public event Action<string> Ended;   // the window closed, or ffmpeg ended by itself; the reason. Raised once, from a worker thread.

  // width and height are the window's client area (what is captured). logFolder null = no log.
  public Recording(EncoderProfile encoder, IntPtr hwnd, int width, int height, string folder, string baseName, AppSettings cfg = null, string logFolder = null, DxgiOutput screen = null) {
    Encoder = encoder; Hwnd = hwnd; this.width = width; this.height = height; this.screen = screen;
    Config = cfg ?? new AppSettings();
    MkvPath = Path.Combine(folder, baseName + ".mkv");
    Mp4Path = Path.Combine(folder, baseName + ".mp4");
    bool scaled;
    if (Config.Screen && screen != null) { OutWidth = Math.Max(2, screen.Width & ~1); OutHeight = Math.Max(2, screen.Height & ~1); }
    else FfmpegArgs.OutputSize(width, height, Config.MaxHeight, out OutWidth, out OutHeight, out scaled);
    log = new RecordingLog(logFolder, baseName);
  }

  public string LogPath { get { return log.Path; } }
  public long Frames { get { return Tele.Frames; } }
  public long Dup { get { return Tele.Dup; } }
  public long Drop { get { return Tele.Drop; } }
  public string Health { get { return Tele.Text; } }
  public bool Behind { get { return Tele.Behind; } }
  // Video still to be written: more than 1.5 s unwritten while frames are still coming (a window that has stopped drawing has nothing to write), or flagged behind.
  public bool BehindNow { get { return (LagSeconds > 1.5 && !Tele.Stalled) || Tele.Behind; } }
  public bool Draining { get { return draining; } }
  public double LagSeconds { get { return Tele.LagSeconds(clock == null ? 0 : clock.Elapsed.TotalSeconds); } }
  public TimeSpan Elapsed { get { return clock == null ? TimeSpan.Zero : clock.Elapsed; } }
  public long FileBytes { get { try { return File.Exists(MkvPath) ? new FileInfo(MkvPath).Length : 0; } catch (Exception) { return 0; } } }
  public TimeSpan Cpu { get { try { return proc.TotalProcessorTime; } catch (Exception) { return TimeSpan.Zero; } } }   // ffmpeg's processor time so far
  public bool Alive { get { try { return proc != null && !proc.HasExited; } catch (Exception) { return false; } } }

  // How ffmpeg ranks on the GPU: "high", "above-normal", or "normal" (switched off, or refused).
  public string GpuPriorityResult { get { return gpuClass == GpuScheduling.High ? "high" : gpuClass == GpuScheduling.AboveNormal ? "above-normal" : "normal"; } }

  string Tail() { lock (gate) return EncoderChoice.Last(string.Join("\n", errors.ToArray()), 3); }

  // HIGH, then ABOVE_NORMAL; ffmpeg may not have opened the GPU yet in its first moments, so this is tried again until it takes.
  void TryGpuPriority() {
    if (Config.GpuPriority != "high" || gpuClass > 0 || gpuTries >= 15 || proc == null) return;
    gpuTries++;
    int n = gpuTries;
    int c = GpuScheduling.Try(proc.Handle, delegate(string s) { log.Line("gpu priority (try " + n + "): " + s); });
    if (c > 0) gpuClass = c;
    else if (gpuTries >= 15) log.Line("gpu priority: gave up, ffmpeg stays at NORMAL");
  }

  // Starts ffmpeg and waits for the first picture. A window that isn't redrawing sends none, so it's asked to repaint.
  public bool Start(out string error, int firstFrameTimeoutMs) {
    error = null;
    if (!Native.IsWindow(Hwnd)) { error = "That window has gone."; return false; }
    try { Directory.CreateDirectory(Path.GetDirectoryName(MkvPath)); }
    catch (Exception ex) { error = "Can't make the recordings folder: " + ex.Message; return false; }
    if (Config.Screen) {
      if (screen == null) { error = "Screen capture needs the monitor that window is on, and DXGI does not list it."; return false; }
      uint vendor = DxgiOutput.VendorOf(Encoder);
      if (vendor != 0 && screen.VendorId != vendor) { error = "That window's monitor is on " + screen.AdapterName + ", which can't feed " + Encoder.Short + ". Move the window to a monitor on the encoder's GPU, or use window capture."; return false; }
    }
    Command = FfmpegArgs.Record(Encoder, Hwnd.ToInt64(), width, height, MkvPath, Config, screen);
    log.Line("Bifrost Recorder " + Application.ProductVersion + " recording log");
    log.Line("started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ", " + Environment.ProcessorCount + " logical processors");
    if (Config.Screen) {
      log.Line("screen capture (ddagrab): " + screen + "; window client area " + width + "x" + height + "; video is the whole monitor" + (Config.MaxHeight > 0 ? " (MaxHeight is ignored: ddagrab can't scale on the GPU)" : ""));
      log.Line("ffmpeg device: -init_hw_device d3d11va=dda:" + screen.Adapter + " (DXGI adapter index), ddagrab output_idx=" + screen.Output + " (that adapter's own output index; matched by HMONITOR " + screen.Monitor.ToInt64() + ")");
    } else
      log.Line("window client area " + width + "x" + height + ", video " + OutWidth + "x" + OutHeight + (OutWidth != (width & ~1) || OutHeight != (height & ~1) ? " (scaled by MaxHeight)" : ""));
    log.Line("settings: EncoderProfile=" + Config.EncoderProfile + " MaxHeight=" + Config.MaxHeight + " GpuPriority=" + Config.GpuPriority + " CaptureMethod=" + Config.CaptureMethod + " FrameRateMode=" + Config.FrameRateMode);
    log.Line("frame timing: " + (Config.Vfr ? "vfr (no fps filter; " + FfmpegArgs.VfrSync + "): only frames that really arrived, with their capture times" : "cfr (fps filter, -fps_mode cfr): a frame every 1/60 s, repeats filling gaps"));
    log.Line("encoder " + Encoder.Name + " (" + Encoder.Label + "); ceiling " + FfmpegArgs.MaxrateKbps(OutWidth, OutHeight) + " kb/s");
    log.Line("ffmpeg " + Command);
    try { proc = Proc.Start(Ffmpeg.Exe, Command, true); }
    catch (Exception ex) { error = "Can't start ffmpeg: " + ex.Message; log.Line("could not start ffmpeg: " + ex.Message); log.Close(); return false; }
    proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Tele.Feed(e.Data); };
    proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (gate) { errors.Add(e.Data); if (errors.Count > 40) errors.RemoveAt(0); } };
    proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
    log.Line("ffmpeg started, pid " + proc.Id);
    if (Config.GpuPriority == "high") TryGpuPriority(); else log.Line("gpu priority: left at NORMAL (GpuPriority=normal)");

    var wait = Stopwatch.StartNew();
    DateTime nudged = DateTime.MinValue, gpuAt = DateTime.UtcNow;
    while (Frames < 1) {
      if (proc.HasExited) { proc.WaitForExit(); error = "ffmpeg ended before the first picture: " + Tail(); log.Line("ffmpeg ended before the first picture: " + Tail()); Cleanup(); log.Close(); return false; }
      if (wait.ElapsedMilliseconds > firstFrameTimeoutMs) {
        Kill();
        error = "That window sent no picture in " + (firstFrameTimeoutMs / 1000) + " seconds. It may be minimized, or not drawing anything.";
        log.Line(error);
        Cleanup(); log.Close();
        return false;
      }
      if ((DateTime.UtcNow - nudged).TotalMilliseconds > 400) { nudged = DateTime.UtcNow; Native.AskToRepaint(Hwnd); }
      if (gpuClass == 0 && (DateTime.UtcNow - gpuAt).TotalMilliseconds > 200) { gpuAt = DateTime.UtcNow; TryGpuPriority(); }
      Thread.Sleep(40);
    }
    clock = Stopwatch.StartNew();
    lastFrameAt = DateTime.UtcNow;
    log.Line("first picture after " + wait.ElapsedMilliseconds + " ms; clock starts; gpu priority " + GpuPriorityResult);
    monitor = new Thread(Watch) { IsBackground = true, Name = "recording watch" };
    monitor.Start();
    return true;
  }

  // Every 200 ms: note progress and health, write a log line each second, ask a window that has stopped drawing to repaint (else
  // ffmpeg has nothing to write and won't even hear "stop"), and say when the window has gone or ffmpeg has ended.
  void Watch() {
    DateTime nudged = DateTime.MinValue;
    double nextLog = 1.0;
    while (!ended) {
      Thread.Sleep(200);
      long f = Frames;
      var now = DateTime.UtcNow;
      double t = clock.Elapsed.TotalSeconds;
      Tele.Tick(t);
      if (f != lastCount) { lastCount = f; lastFrameAt = now; }
      if (t >= nextLog) { nextLog += 1.0; log.Line(Tele.Line(t)); if (gpuClass == 0) TryGpuPriority(); }
      if (!Alive) { Raise("ffmpeg ended by itself" + (Tail().Length > 0 ? ": " + Tail() : "")); return; }
      if (!Native.IsWindow(Hwnd)) { Raise("The window was closed"); return; }
      if ((now - lastFrameAt).TotalMilliseconds > 1000 && (now - nudged).TotalMilliseconds > 500) { nudged = now; Native.AskToRepaint(Hwnd); }
    }
  }

  void Raise(string reason) {
    if (Interlocked.Exchange(ref endedRaised, 1) != 0) return;
    log.Line("ended: " + reason);
    var h = Ended;
    if (h != null && !stopping) h(reason);
  }

  void Kill() { try { if (proc != null && !proc.HasExited) { log.Line("killing ffmpeg"); proc.Kill(); proc.WaitForExit(5000); } } catch (Exception) { } }

  void Cleanup() {
    ended = true;
    try { if (File.Exists(MkvPath) && new FileInfo(MkvPath).Length < 2048) File.Delete(MkvPath); } catch (Exception) { }
  }

  // Ends the recording the polite way (q to ffmpeg, so it finishes the file), and turns the .mkv into the .mp4. Blocks.
  // A closing repaint is asked for first, so a last picture lands (the Studio-safe end: the video isn't shorter than the sound).
  // When the encoder is behind (video still to write), ffmpeg gets up to 30 s to finish it instead of 10 before anything is killed.
  public RecordingResult Stop() {
    var res = new RecordingResult();
    var total = Stopwatch.StartNew();
    stopping = true;
    if (proc == null) { res.Message = "Nothing was started."; return res; }
    double now = clock == null ? 0 : clock.Elapsed.TotalSeconds, lag = Tele.LagSeconds(now);
    bool behind = (lag > 1.5 && !Tele.Stalled) || Tele.Behind;
    draining = behind;
    int limit = behind ? 30000 : 10000;
    log.Line("Stop pressed at " + now.ToString("0.0", CultureInfo.InvariantCulture) + " s: lag " + lag.ToString("0.0", CultureInfo.InvariantCulture) + " s, " + Tele.Text + (behind ? "; waiting up to 30 s for ffmpeg to finish writing" : "; waiting up to 10 s"));
    if (Native.IsWindow(Hwnd)) Native.AskToRepaint(Hwnd);
    try { proc.StandardInput.Write("q\n"); proc.StandardInput.Flush(); } catch (Exception) { }
    var wait = Stopwatch.StartNew();
    DateTime nudged = DateTime.MinValue;
    while (!proc.HasExited && wait.ElapsedMilliseconds < limit) {
      if ((DateTime.UtcNow - nudged).TotalMilliseconds > 300) { nudged = DateTime.UtcNow; if (Native.IsWindow(Hwnd)) Native.AskToRepaint(Hwnd); }
      proc.WaitForExit(100);
    }
    if (!proc.HasExited) { res.Forced = true; Kill(); }
    try { proc.WaitForExit(); } catch (Exception) { }
    ended = true;
    draining = false;
    if (monitor != null) monitor.Join(2000);
    int exitCode = -1; try { exitCode = proc.ExitCode; } catch (Exception) { }
    res.Frames = Frames; res.Dup = Dup; res.Drop = Drop;
    log.Line("ffmpeg ended " + wait.ElapsedMilliseconds + " ms after q, exit code " + exitCode + ", forced: " + (res.Forced ? "YES" : "no") + "; " + Tele.Line(clock == null ? 0 : clock.Elapsed.TotalSeconds));

    if (!File.Exists(MkvPath) || new FileInfo(MkvPath).Length < 2048) {
      Cleanup();
      res.Message = "Nothing was recorded." + (Tail().Length > 0 ? " " + Tail() : "");
      res.StopSeconds = total.Elapsed.TotalSeconds;
      log.Line(res.Message + " (Stop took " + res.StopSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s)"); log.Close();
      return res;
    }
    string so, se;
    int code = Proc.Run(Ffmpeg.Exe, FfmpegArgs.Remux(Encoder, MkvPath, Mp4Path), 1800000, out so, out se);
    var probe = code == 0 ? ProbeResult.Run(Mp4Path, false) : null;
    res.Probe = probe;
    if (code == 0 && probe != null && probe.Ok && probe.Codec == Encoder.Codec && probe.Duration > 0) {
      try { File.Delete(MkvPath); } catch (Exception) { res.Mkv = MkvPath; }
      res.Ok = true; res.Mp4 = Mp4Path; res.Seconds = probe.Duration; res.Bytes = new FileInfo(Mp4Path).Length;
      res.Message = "Saved " + Path.GetFileName(Mp4Path);
    } else {
      res.Mkv = MkvPath;
      try { if (File.Exists(Mp4Path)) File.Delete(Mp4Path); } catch (Exception) { }
      res.Message = "The recording is kept as " + Path.GetFileName(MkvPath) + " (making the .mp4 failed: " +
                    (code != 0 ? "ffmpeg exit " + code + " " + EncoderChoice.Last(se, 2) : probe == null ? "?" : probe.Error ?? "the file didn't check out") + ")";
    }
    res.StopSeconds = total.Elapsed.TotalSeconds;
    log.Line((res.Ok ? "saved " + Path.GetFileName(res.Mp4) + ": " + res.Seconds.ToString("0.000", CultureInfo.InvariantCulture) + " s of video (clock said " + (clock == null ? 0 : clock.Elapsed.TotalSeconds).ToString("0.0", CultureInfo.InvariantCulture) + " s), " +
             (res.Bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : res.Message) + "; Stop took " + res.StopSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
    log.Close();
    try { proc.Dispose(); } catch (Exception) { }
    return res;
  }
}
