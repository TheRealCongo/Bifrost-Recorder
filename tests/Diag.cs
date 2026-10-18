// RecorderTests.exe --diag [fullscreen|windowed|both] [seconds] : which stage of the capture and encode starves against a focused,
// GPU-heavy, fullscreen flip-model window (FakeGame.exe), and what the Recorder does in each mode. Measured, 20 s each by default:
//   D1 gfxcapture alone (frames straight to a null output)        how many real frames does Windows Graphics Capture deliver?
//   D2 gfxcapture + NVENC passthrough                              the same, with the encoder behind it
//   D3 NVENC alone on a synthetic 2560x1440 picture                how fast the encoder runs while the GPU is saturated
//   D4 ddagrab + NVENC (cfr with its own repeats, vfr without)     the Desktop Duplication path
//   R  the Recorder itself: window or screen capture x cfr or vfr x standard or quality
// For each: real frames a second, output speed (written video time against the clock), FakeGame's presented fps without and with
// the recording (the game's cost), GPU and encoder utilisation, ffmpeg's CPU. A summary table is printed as TABLE lines.
// Safety: after every step the System log is searched for NVIDIA driver events (nvlddmkm); the first one stops everything.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

static class Diag {
  static string build, work;
  static readonly List<string> table = new List<string>();
  static DateTime startedUtc;

  static string F(double v, string f) { return v.ToString(f, CultureInfo.InvariantCulture); }

  // ---- nvidia-smi once a second
  class Smi {
    readonly List<int[]> s = new List<int[]>();
    Process p;
    public void Start() {
      p = Proc.Start("nvidia-smi", "--query-gpu=utilization.gpu,utilization.encoder --format=csv,noheader,nounits -l 1", false);
      p.OutputDataReceived += delegate(object o, DataReceivedEventArgs e) {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        var parts = e.Data.Split(','); int a, b;
        if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out a) && int.TryParse(parts[1].Trim(), out b)) lock (s) s.Add(new[] { a, b });
      };
      p.BeginOutputReadLine(); p.BeginErrorReadLine();
    }
    public void Clear() { lock (s) s.Clear(); }
    public void Stop() { try { if (!p.HasExited) p.Kill(); p.WaitForExit(3000); } catch (Exception) { } }
    public string Avg(out double g, out double e) {
      lock (s) {
        g = 0; e = 0;
        foreach (var x in s) { g += x[0]; e += x[1]; }
        if (s.Count > 0) { g /= s.Count; e /= s.Count; }
        return s.Count + " samples";
      }
    }
  }

  // ---- FakeGame
  class Game {
    public Process P;
    public long Hwnd;
    public string Ready = "", Mode;
    public bool Foreground;
    public readonly List<KeyValuePair<DateTime, double>> Fps = new List<KeyValuePair<DateTime, double>>();
    public int Load;
    string quitName;
    EventWaitHandle quit;

    public static Game Start(string mode, int vsync, string log) {
      var g = new Game { Mode = mode };
      g.quitName = "Global\\BifrostFakeGame-" + Guid.NewGuid().ToString("N");
      g.quit = new EventWaitHandle(false, EventResetMode.ManualReset, g.quitName);
      string args = "--mode " + mode + " --monitor primary --vsync " + vsync + " --buffers 3 --load auto --target-fps 60 --quit-event " + g.quitName + " --log " + FfmpegArgs.Quote(log) + (mode == "windowed" ? " --size 1600x900" : "");
      g.P = Proc.Start(Path.Combine(build, "FakeGame.exe"), args, false);
      var ready = new ManualResetEvent(false);
      g.P.OutputDataReceived += delegate(object o, DataReceivedEventArgs e) {
        if (e.Data == null) return;
        if (e.Data.StartsWith("READY")) {
          g.Ready = e.Data; g.Foreground = e.Data.Contains("foreground=True");
          int i = e.Data.IndexOf("hwnd="); if (i >= 0) { int j = e.Data.IndexOf(' ', i); g.Hwnd = long.Parse(e.Data.Substring(i + 5, j - i - 5), CultureInfo.InvariantCulture); }
          ready.Set();
        } else if (e.Data.StartsWith("FPS ")) {
          var p = e.Data.Split(' ');
          lock (g.Fps) { g.Fps.Add(new KeyValuePair<DateTime, double>(DateTime.UtcNow, double.Parse(p[1], CultureInfo.InvariantCulture))); g.Load = int.Parse(p[3], CultureInfo.InvariantCulture); }
        } else if (e.Data.StartsWith("ERROR")) { g.Ready = e.Data; ready.Set(); }
      };
      g.P.BeginOutputReadLine(); g.P.BeginErrorReadLine();
      if (!ready.WaitOne(40000) || g.Hwnd == 0) throw new Exception("FakeGame did not start: " + g.Ready);
      return g;
    }

    // Waits until the load controller has settled: three presented-fps lines in a row within 4% of 60.
    public bool Calibrate(int maxSeconds) {
      var sw = Stopwatch.StartNew();
      while (sw.Elapsed.TotalSeconds < maxSeconds) {
        Thread.Sleep(500);
        lock (Fps) { int n = Fps.Count; if (n >= 3) { bool ok = true; for (int i = n - 3; i < n; i++) if (Math.Abs(Fps[i].Value - 60) > 2.5) ok = false; if (ok) return true; } }
      }
      return false;
    }

    public double FpsBetween(DateTime a, DateTime b) {
      lock (Fps) {
        double sum = 0; int n = 0;
        foreach (var kv in Fps) if (kv.Key >= a && kv.Key <= b) { sum += kv.Value; n++; }
        return n == 0 ? double.NaN : sum / n;
      }
    }

    public void Stop() {
      try { quit.Set(); } catch (Exception) { }
      try { if (!P.WaitForExit(8000)) { P.Kill(); P.WaitForExit(3000); } } catch (Exception) { }
      try { quit.Close(); } catch (Exception) { }
    }
  }

  // ---- the System log: any NVIDIA display driver event since the run began stops everything
  static List<string> DriverEvents(DateTime sinceUtc) {
    var found = new List<string>();
    try {
      string ts = sinceUtc.ToString("yyyy-MM-ddTHH:mm:ss.000Z", CultureInfo.InvariantCulture);
      var q = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='nvlddmkm'] and TimeCreated[@SystemTime>='" + ts + "']]]");
      using (var r = new EventLogReader(q)) {
        EventRecord e;
        while ((e = r.ReadEvent()) != null) { found.Add("id " + e.Id + " at " + e.TimeCreated); e.Dispose(); }
      }
    } catch (Exception ex) { found.Add("(couldn't read the System log: " + ex.Message + ")"); if (!ex.Message.Contains("log")) { } }
    return found;
  }

  static void SafetyCheck(string where) {
    var ev = DriverEvents(startedUtc.AddSeconds(-2));
    ev.RemoveAll(delegate(string s) { return s.StartsWith("(couldn't"); });
    if (ev.Count > 0) {
      Console.WriteLine("SAFETY STOP after " + where + ": NVIDIA driver events in the System log since this run began: " + string.Join("; ", ev.ToArray()));
      Console.WriteLine("NVLDDMKM EVENT: stopping all GPU runs.");
      Console.Out.Flush();
      Environment.Exit(9);   // the job object ends anything still running
    }
  }

  // ---- one measured stretch: what the game does, what the GPU does, and how far a capture/encode (ffmpeg or the Recorder) gets
  class Result { public double RealFps = double.NaN, Speed = double.NaN, GameBase = double.NaN, GameDuring = double.NaN, Gpu, Enc, CpuPct, Dup; public string Note = ""; }

  // Runs ffmpeg for `seconds` of wall time (progress read from -progress), then ends it with q.
  static Result RunFfmpeg(Game game, Smi smi, string args, int seconds, string note) {
    var res = new Result { Note = note };
    var tele = new Telemetry();
    var errors = new StringBuilder();
    var p = Proc.Start(Ffmpeg.Exe, args, true);
    p.OutputDataReceived += delegate(object o, DataReceivedEventArgs e) { if (e.Data != null) tele.Feed(e.Data); };
    p.ErrorDataReceived += delegate(object o, DataReceivedEventArgs e) { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
    p.BeginOutputReadLine(); p.BeginErrorReadLine();
    // ffmpeg's start-up (devices, first frame) isn't part of the measurement: sample from 3 s after it starts
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < 3 && !p.HasExited) Thread.Sleep(50);
    long f0 = tele.Frames, o0 = tele.OutUs; double t0 = sw.Elapsed.TotalSeconds;
    DateTime g0 = DateTime.UtcNow; smi.Clear(); TimeSpan cpu0 = SafeCpu(p);
    while (sw.Elapsed.TotalSeconds < 3 + seconds && !p.HasExited) Thread.Sleep(100);
    long f1 = tele.Frames, o1 = tele.OutUs; double t1 = sw.Elapsed.TotalSeconds;
    DateTime g1 = DateTime.UtcNow; TimeSpan cpu1 = SafeCpu(p);
    double ga, ea; smi.Avg(out ga, out ea); res.Gpu = ga; res.Enc = ea;
    res.GameDuring = game.FpsBetween(g0, g1);
    if (!p.HasExited) { double dt = t1 - t0; res.RealFps = (f1 - f0) / dt; res.Speed = o0 >= 0 && o1 >= 0 ? (o1 - o0) / 1e6 / dt : double.NaN; res.CpuPct = 100 * (cpu1 - cpu0).TotalSeconds / dt; }
    res.Dup = tele.Dup;
    // end it: q, then the grace a stalled capture needs, then force
    try { p.StandardInput.Write("q\n"); p.StandardInput.Flush(); } catch (Exception) { }
    if (!p.WaitForExit(15000)) { res.Note += " [q ignored 15 s, killed]"; try { p.Kill(); p.WaitForExit(3000); } catch (Exception) { } }
    if (p.HasExited && p.ExitCode != 0 && double.IsNaN(res.RealFps)) { lock (errors) res.Note += " [ffmpeg: " + EncoderChoice.Last(errors.ToString(), 2) + "]"; }
    return res;
  }

  static TimeSpan SafeCpu(Process p) { try { return p.TotalProcessorTime; } catch (Exception) { return TimeSpan.Zero; } }

  static string Row(string scenario, string test, Result r) {
    return "| " + scenario + " | " + test + " | " + (double.IsNaN(r.RealFps) ? "-" : F(r.RealFps, "0.0")) + " | " + (double.IsNaN(r.Speed) ? "-" : F(r.Speed, "0.00") + "x") + " | " +
           (double.IsNaN(r.GameBase) ? "-" : F(r.GameBase, "0.0")) + " > " + (double.IsNaN(r.GameDuring) ? "-" : F(r.GameDuring, "0.0")) + " | " + F(r.Gpu, "0") + "% / " + F(r.Enc, "0") + "% | " +
           (double.IsNaN(r.CpuPct) ? "-" : F(r.CpuPct, "0") + "%") + " | " + r.Note.Trim() + " |";
  }

  static void Report(string scenario, string test, Result r) {
    string row = Row(scenario, test, r);
    table.Add(row);
    Console.WriteLine("TABLE " + row);
    Console.Out.Flush();
    SafetyCheck(scenario + " / " + test);
  }

  const string Tags = "setparams=colorspace=smpte170m:range=tv:color_primaries=bt709:color_trc=bt709";
  static string Gfx(long hwnd) { return "gfxcapture=hwnd=" + hwnd + ":max_framerate=60:capture_cursor=true:display_border=false:resize_mode=scale_aspect:width=-2:height=-2"; }
  static string Nv(int maxrate) { return EncoderProfile.Nvenc.Options(maxrate, 24, false); }

  public static int Run(string[] args, string buildDir) {
    build = buildDir;
    work = Path.Combine(build, "diag"); if (Directory.Exists(work)) Directory.Delete(work, true); Directory.CreateDirectory(work);
    startedUtc = DateTime.UtcNow;
    string which = args.Length > 1 && (args[1] == "fullscreen" || args[1] == "windowed" || args[1] == "both") ? args[1] : "both";
    int seconds = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 20;
    string only = null; for (int i = 1; i < args.Length - 1; i++) if (args[i] == "--only") only = "," + args[i + 1] + ",";
    var scenarios = which == "both" ? new[] { "fullscreen", "windowed" } : new[] { which };
    Console.WriteLine("Counts before: ffmpeg=" + Process.GetProcessesByName("ffmpeg").Length + " ffprobe=" + Process.GetProcessesByName("ffprobe").Length + " cmd=" + Process.GetProcessesByName("cmd").Length + " conhost=" + Process.GetProcessesByName("conhost").Length);
    var smi = new Smi(); smi.Start();
    var enc = EncoderChoice.Detect(Ffmpeg.Exe);
    Console.WriteLine("encoder: " + enc.Name + "; each measurement is " + seconds + " s; FakeGame at 2560x1440 unlocked, load auto-tuned to a GPU-bound 60 fps");
    try {
      foreach (string mode in scenarios) {
        Game g = null;
        try {
          g = Game.Start(mode, 0, Path.Combine(work, "fakegame-" + mode + ".log"));
          Console.WriteLine("FakeGame: " + g.Ready);
          bool cal = g.Calibrate(40);
          Console.WriteLine("FakeGame calibrated: " + cal + ", load " + g.Load);
          SafetyCheck(mode + " start");
          // the game alone
          smi.Clear(); DateTime b0 = DateTime.UtcNow; Thread.Sleep(8000); DateTime b1 = DateTime.UtcNow;
          double bg, be; smi.Avg(out bg, out be);
          double gameBase = g.FpsBetween(b0, b1);
          var baseRes = new Result { GameBase = gameBase, GameDuring = gameBase, Gpu = bg, Enc = be, Note = "FakeGame alone, load " + g.Load + ", foreground=" + g.Foreground };
          Report(mode, "game alone", baseRes);
          if (only != null && !only.Contains(",game,")) { }

          Func<string, bool> want = delegate(string id) { return only == null || only.Contains("," + id + ","); };
          Result r;
          int ceiling = FfmpegArgs.MaxrateKbps(2560, 1440);
          if (want("D1")) {
            r = RunFfmpeg(g, smi, "-hide_banner -loglevel warning -nostats -progress pipe:1 -stats_period 0.25 -y -filter_complex \"" + Gfx(g.Hwnd) + "[v]\" -map \"[v]\" -f null -", seconds, "gfxcapture to null");
            r.GameBase = gameBase; Report(mode, "D1 gfxcapture alone", r);
          }
          if (want("D2")) {
            r = RunFfmpeg(g, smi, "-hide_banner -loglevel warning -nostats -progress pipe:1 -stats_period 0.25 -y -filter_complex \"" + Gfx(g.Hwnd) + "," + Tags + "[v]\" -map \"[v]\" " + Nv(ceiling) + " " + FfmpegArgs.VfrSync + " -f null -", seconds, "passthrough, no repeats");
            r.GameBase = gameBase; Report(mode, "D2 gfxcapture + NVENC (vfr)", r);
          }
          if (want("D3")) {
            r = RunFfmpeg(g, smi, "-hide_banner -loglevel warning -nostats -progress pipe:1 -stats_period 0.25 -y -init_hw_device d3d11va=d3d -filter_hw_device d3d -f lavfi -i testsrc2=size=2560x1440:rate=60 -vf \"format=bgra,hwupload," + Tags + "\" " + Nv(ceiling) + " -f null -", seconds, "synthetic 1440p, as fast as it goes (60 fps = 1.00x)");
            r.GameBase = gameBase; Report(mode, "D3 NVENC alone", r);
          }
          if (want("D4")) {
            var m = Dxgi.ForWindow(new IntPtr(g.Hwnd));
            if (m == null) Console.WriteLine("no DXGI output for FakeGame's window");
            else {
              string hw = "-init_hw_device d3d11va=dda:" + m.Adapter + " -filter_hw_device dda ";
              r = RunFfmpeg(g, smi, "-hide_banner -loglevel warning -nostats -progress pipe:1 -stats_period 0.25 -y " + hw + "-filter_complex \"ddagrab=output_idx=" + m.Output + ":framerate=60:draw_mouse=true," + Tags + "[v]\" -map \"[v]\" " + Nv(ceiling) + " -fps_mode cfr -f null -", seconds, "ddagrab, its own repeats (dup_frames on)");
              r.GameBase = gameBase; Report(mode, "D4a ddagrab + NVENC (cfr)", r);
              r = RunFfmpeg(g, smi, "-hide_banner -loglevel warning -nostats -progress pipe:1 -stats_period 0.25 -y " + hw + "-filter_complex \"ddagrab=output_idx=" + m.Output + ":framerate=60:draw_mouse=true:dup_frames=false," + Tags + "[v]\" -map \"[v]\" " + Nv(ceiling) + " " + FfmpegArgs.VfrSync + " -f null -", seconds, "ddagrab, real frames only");
              r.GameBase = gameBase; Report(mode, "D4b ddagrab + NVENC (vfr)", r);
            }
          }
          if (want("R")) {
            foreach (bool screen in new[] { false, true }) foreach (bool vfr in new[] { false, true }) foreach (bool qual in new[] { false, true }) {
              var cfg = new AppSettings { CaptureMethod = screen ? "screen" : "window", FrameRateMode = vfr ? "vfr" : "cfr", EncoderProfile = qual ? "quality" : "standard" };
              string id = "R-" + (screen ? "screen" : "window") + "-" + (vfr ? "vfr" : "cfr") + "-" + (qual ? "quality" : "standard");
              if (only != null && !only.Contains(",R,") && !only.Contains("," + id + ",")) continue;
              r = RunRecorder(g, smi, enc, cfg, id, seconds);
              r.GameBase = gameBase; Report(mode, "Recorder " + id.Substring(2), r);
            }
          }
        } finally { if (g != null) g.Stop(); }
        Thread.Sleep(2000);
      }
    } finally { smi.Stop(); }
    SafetyCheck("the end");
    Console.WriteLine();
    Console.WriteLine("| scenario | test | real frames/s | output speed | game fps alone > with it | GPU / encoder util | ffmpeg CPU (of one core) | note |");
    Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
    foreach (string row in table) Console.WriteLine(row);
    Console.WriteLine();
    Console.WriteLine("Counts after:  ffmpeg=" + Process.GetProcessesByName("ffmpeg").Length + " ffprobe=" + Process.GetProcessesByName("ffprobe").Length + " cmd=" + Process.GetProcessesByName("cmd").Length + " conhost=" + Process.GetProcessesByName("conhost").Length);
    int alive = 0;
    lock (Proc.Started) foreach (int pid in Proc.Started) { try { var q = Process.GetProcessById(pid); if (!q.HasExited) alive++; } catch (Exception) { } }
    Console.WriteLine("processes this run started that are still running: " + alive);
    try { Directory.Delete(work, true); } catch (Exception) { }
    return 0;
  }

  // The Recorder itself, on FakeGame's window, for `seconds`; the result file is checked a little and deleted.
  static Result RunRecorder(Game g, Smi smi, EncoderProfile enc, AppSettings cfg, string id, int seconds) {
    var res = new Result();
    string folder = Path.Combine(work, "rec");
    var win = new IntPtr(g.Hwnd);
    var size = Native.ClientSize(win);
    var scr = cfg.Screen ? Dxgi.ForWindow(win) : null;
    var rec = new Recording(enc, win, size.Width, size.Height, folder, id, cfg, Path.Combine(work, "logs"), scr);
    string err;
    if (!rec.Start(out err, 15000)) { res.Note = "[did not start: " + err + "]"; return res; }
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < 3) Thread.Sleep(50);
    long f0 = rec.Frames; var t0 = sw.Elapsed.TotalSeconds; double o0 = rec.Tele.OutUs < 0 ? double.NaN : rec.Tele.OutUs / 1e6;
    DateTime g0 = DateTime.UtcNow; smi.Clear(); TimeSpan cpu0 = rec.Cpu;
    double minWin = double.NaN; bool behind = false;
    while (sw.Elapsed.TotalSeconds < 3 + seconds) {
      Thread.Sleep(250);
      double ws = rec.Tele.WindowSpeed; if (!double.IsNaN(ws) && (double.IsNaN(minWin) || ws < minWin)) minWin = ws;
      if (rec.Behind) behind = true;
    }
    long f1 = rec.Frames; double t1 = sw.Elapsed.TotalSeconds; double o1 = rec.Tele.OutUs < 0 ? double.NaN : rec.Tele.OutUs / 1e6;
    DateTime g1 = DateTime.UtcNow; TimeSpan cpu1 = rec.Cpu;
    double ga, ea; smi.Avg(out ga, out ea); res.Gpu = ga; res.Enc = ea;
    res.GameDuring = g.FpsBetween(g0, g1);
    double dt = t1 - t0;
    res.Speed = (o1 - o0) / dt; res.CpuPct = 100 * (cpu1 - cpu0).TotalSeconds / dt;
    double encoded = (f1 - f0) / dt;
    var done = rec.Stop();
    res.Dup = done.Dup;
    // real frames: in vfr every frame is real; in cfr the repeats are tiny packets, so count the packets that carry a picture change
    double real = encoded;
    if (done.Ok && !cfg.Vfr) {
      string so, se;
      Proc.Run(Ffmpeg.ProbeExe, "-v error -select_streams v:0 -show_entries packet=size -of csv=p=0 " + FfmpegArgs.Quote(done.Mp4), 60000, out so, out se);
      int big = 0, all = 0; foreach (string l in so.Split(new[] { (char)10 })) { int n; if (int.TryParse(l.Trim(), out n)) { all++; if (n > 1200) big++; } }
      real = all > 0 ? encoded * big / all : double.NaN;
      res.Note += " " + big + " of " + all + " packets carry a new picture;";
    }
    res.RealFps = real;
    res.Note += " encoded " + F(encoded, "0.0") + " fps, slowest 4 s window " + F(minWin, "0.00") + "x, status: " + rec.Health + (behind ? " (flagged behind)" : "") + ", gpu priority " + rec.GpuPriorityResult + (done.Forced ? ", FORCED" : "") + ", stop " + F(done.StopSeconds, "0.0") + " s;";
    if (done.Ok) { try { File.Delete(done.Mp4); } catch (Exception) { } }
    else res.Note += " [" + done.Message + "]";
    try { foreach (string f in Directory.GetFiles(folder, id + ".*")) File.Delete(f); } catch (Exception) { }
    return res;
  }
}
