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
  public ProbeResult Probe;
}

// One recording of one window. Start returns once the first picture has arrived; Stop writes the finished file.
class Recording {
  public readonly IntPtr Hwnd;
  public readonly EncoderProfile Encoder;
  public readonly string MkvPath, Mp4Path;
  readonly int width, height;
  Process proc;
  Thread monitor;
  Stopwatch clock;
  readonly object gate = new object();
  readonly List<string> errors = new List<string>();
  long frames, dup, drop, lastCount;
  DateTime lastFrameAt = DateTime.UtcNow;
  volatile bool stopping, ended;
  int endedRaised;
  public string Command;          // what ffmpeg was started with (the tests print it)
  public event Action<string> Ended;   // the window closed, or ffmpeg ended by itself; the reason. Raised once, from a worker thread.

  public Recording(EncoderProfile encoder, IntPtr hwnd, int width, int height, string folder, string baseName) {
    Encoder = encoder; Hwnd = hwnd; this.width = width; this.height = height;
    MkvPath = Path.Combine(folder, baseName + ".mkv");
    Mp4Path = Path.Combine(folder, baseName + ".mp4");
  }

  public long Frames { get { lock (gate) return frames; } }
  public long Dup { get { lock (gate) return dup; } }
  public long Drop { get { lock (gate) return drop; } }
  public TimeSpan Elapsed { get { return clock == null ? TimeSpan.Zero : clock.Elapsed; } }
  public long FileBytes { get { try { return File.Exists(MkvPath) ? new FileInfo(MkvPath).Length : 0; } catch (Exception) { return 0; } } }
  public TimeSpan Cpu { get { try { return proc.TotalProcessorTime; } catch (Exception) { return TimeSpan.Zero; } } }   // ffmpeg's processor time so far
  public bool Alive { get { try { return proc != null && !proc.HasExited; } catch (Exception) { return false; } } }

  string Tail() { lock (gate) return EncoderChoice.Last(string.Join("\n", errors.ToArray()), 3); }

  // Starts ffmpeg and waits for the first picture. A window that isn't redrawing sends none, so it's asked to repaint.
  public bool Start(out string error, int firstFrameTimeoutMs) {
    error = null;
    try { Directory.CreateDirectory(Path.GetDirectoryName(MkvPath)); }
    catch (Exception ex) { error = "Can't make the recordings folder: " + ex.Message; return false; }
    Command = FfmpegArgs.Record(Encoder, Hwnd.ToInt64(), width, height, MkvPath);
    try { proc = Proc.Start(Ffmpeg.Exe, Command, true); }
    catch (Exception ex) { error = "Can't start ffmpeg: " + ex.Message; return false; }
    proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Progress(e.Data); };
    proc.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (gate) { errors.Add(e.Data); if (errors.Count > 40) errors.RemoveAt(0); } };
    proc.BeginOutputReadLine(); proc.BeginErrorReadLine();

    var wait = Stopwatch.StartNew();
    DateTime nudged = DateTime.MinValue;
    while (Frames < 1) {
      if (proc.HasExited) { proc.WaitForExit(); error = "ffmpeg ended before the first picture: " + Tail(); Cleanup(); return false; }
      if (wait.ElapsedMilliseconds > firstFrameTimeoutMs) {
        Kill();
        error = "That window sent no picture in " + (firstFrameTimeoutMs / 1000) + " seconds. It may be minimized, or not drawing anything.";
        Cleanup();
        return false;
      }
      if ((DateTime.UtcNow - nudged).TotalMilliseconds > 400) { nudged = DateTime.UtcNow; Native.AskToRepaint(Hwnd); }
      Thread.Sleep(40);
    }
    clock = Stopwatch.StartNew();
    lastFrameAt = DateTime.UtcNow;
    monitor = new Thread(Watch) { IsBackground = true, Name = "recording watch" };
    monitor.Start();
    return true;
  }

  // ffmpeg's -progress output: key=value lines, "progress=" ends each report.
  void Progress(string line) {
    int eq = line.IndexOf('=');
    if (eq <= 0) return;
    string k = line.Substring(0, eq), v = line.Substring(eq + 1).Trim();
    long n;
    if (!long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return;
    lock (gate) {
      if (k == "frame") frames = n;
      else if (k == "dup_frames") dup = n;
      else if (k == "drop_frames") drop = n;
    }
  }

  // Every 200 ms: note progress, ask a window that has stopped drawing to repaint (else ffmpeg has nothing to write and
  // won't even hear "stop"), and say when the window has gone or ffmpeg has ended.
  void Watch() {
    DateTime nudged = DateTime.MinValue;
    while (!ended) {
      Thread.Sleep(200);
      long f = Frames;
      var now = DateTime.UtcNow;
      if (f != lastCount) { lastCount = f; lastFrameAt = now; }
      if (!Alive) { Raise("ffmpeg ended by itself" + (Tail().Length > 0 ? ": " + Tail() : "")); return; }
      if (!Native.IsWindow(Hwnd)) { Raise("The window was closed"); return; }
      if ((now - lastFrameAt).TotalMilliseconds > 1000 && (now - nudged).TotalMilliseconds > 500) { nudged = now; Native.AskToRepaint(Hwnd); }
    }
  }

  void Raise(string reason) {
    if (Interlocked.Exchange(ref endedRaised, 1) != 0) return;
    var h = Ended;
    if (h != null && !stopping) h(reason);
  }

  void Kill() { try { if (proc != null && !proc.HasExited) { proc.Kill(); proc.WaitForExit(5000); } } catch (Exception) { } }

  void Cleanup() {
    ended = true;
    try { if (File.Exists(MkvPath) && new FileInfo(MkvPath).Length < 2048) File.Delete(MkvPath); } catch (Exception) { }
  }

  // Ends the recording the polite way (q to ffmpeg, so it finishes the file), and turns the .mkv into the .mp4. Blocks.
  public RecordingResult Stop() {
    var res = new RecordingResult();
    stopping = true;
    if (proc == null) { res.Message = "Nothing was started."; return res; }
    try { proc.StandardInput.Write("q\n"); proc.StandardInput.Flush(); } catch (Exception) { }
    var wait = Stopwatch.StartNew();
    DateTime nudged = DateTime.MinValue;
    while (!proc.HasExited && wait.ElapsedMilliseconds < 10000) {
      if ((DateTime.UtcNow - nudged).TotalMilliseconds > 300) { nudged = DateTime.UtcNow; if (Native.IsWindow(Hwnd)) Native.AskToRepaint(Hwnd); }
      proc.WaitForExit(100);
    }
    if (!proc.HasExited) { res.Forced = true; Kill(); }
    try { proc.WaitForExit(); } catch (Exception) { }
    ended = true;
    if (monitor != null) monitor.Join(2000);
    res.Frames = Frames; res.Dup = Dup; res.Drop = Drop;

    if (!File.Exists(MkvPath) || new FileInfo(MkvPath).Length < 2048) {
      Cleanup();
      res.Message = "Nothing was recorded." + (Tail().Length > 0 ? " " + Tail() : "");
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
    try { proc.Dispose(); } catch (Exception) { }
    return res;
  }
}
