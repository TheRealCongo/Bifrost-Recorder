// SPDX-License-Identifier: GPL-2.0-or-later
// RecorderTests.exe --diag [windowed|fullscreen] [seconds]: does game capture keep up where window capture starved? FakeGame.exe (a Direct3D 11
// flip-model window whose load is tuned until it is GPU-bound at 60 fps, like a heavy game) is recorded by the Recorder for `seconds` each way:
//   game    game capture (OBS's hook inside FakeGame)
//   window  window capture (Windows Graphics Capture, what 0.1.x used)
// For each: FakeGame's presented fps alone and while recorded, the frames written, the frames the GPU drew late or the encoder skipped, how many
// distinct pictures the file holds (a starved capture repeats the last one), and GPU and encoder use. TABLE lines sum it up.
// Safety: like every GPU mode it refuses next to a real game (the guard), and the System log is searched for NVIDIA driver resets after each step.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

static class Diag {
  static string F(double v, string f) { return v.ToString(f, CultureInfo.InvariantCulture); }

  // ---- nvidia-smi once a second
  public class Smi {
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
    public void Avg(out double g, out double e) {
      lock (s) { g = 0; e = 0; foreach (var x in s) { g += x[0]; e += x[1]; } if (s.Count > 0) { g /= s.Count; e /= s.Count; } }
    }
  }

  // ---- FakeGame, started by the tests and the diag; it says READY with its window handle, then its presented fps each second
  public class Game {
    public Process P;
    public IntPtr Hwnd;
    public int Pid;
    public string Ready = "";
    public readonly List<KeyValuePair<DateTime, double>> Fps = new List<KeyValuePair<DateTime, double>>();
    public int Load;
    public static Action<int> Started;   // the new process's id, the moment it starts (the tests' watcher allows its window before it shows)
    EventWaitHandle quit;

    // mode fullscreen|windowed; load a number of shader loops, or "auto" (tuned to a GPU-bound 60 fps); vsync 1 or 0.
    public static Game Start(string build, string mode, string size, string load, int vsync, string log) {
      var g = new Game();
      string quitName = "Global\\BifrostFakeGame-" + Guid.NewGuid().ToString("N");
      g.quit = new EventWaitHandle(false, EventResetMode.ManualReset, quitName);
      string args = "--mode " + mode + " --monitor primary --vsync " + vsync + " --buffers 3 --load " + load + " --target-fps 60 --quit-event " + quitName + " --log " + Tools.Quote(log) + (mode == "windowed" ? " --size " + size : "");
      g.P = Proc.Start(Path.Combine(build, "FakeGame.exe"), args, false);
      g.Pid = g.P.Id;
      var started = Started; if (started != null) started(g.Pid);
      var ready = new ManualResetEvent(false);
      g.P.OutputDataReceived += delegate(object o, DataReceivedEventArgs e) {
        if (e.Data == null) return;
        if (e.Data.StartsWith("READY")) {
          g.Ready = e.Data;
          int i = e.Data.IndexOf("hwnd="); if (i >= 0) { int j = e.Data.IndexOf(' ', i); g.Hwnd = new IntPtr(long.Parse(e.Data.Substring(i + 5, j - i - 5), CultureInfo.InvariantCulture)); }
          ready.Set();
        } else if (e.Data.StartsWith("FPS ")) {
          var p = e.Data.Split(' ');
          lock (g.Fps) { g.Fps.Add(new KeyValuePair<DateTime, double>(DateTime.UtcNow, double.Parse(p[1], CultureInfo.InvariantCulture))); g.Load = int.Parse(p[3], CultureInfo.InvariantCulture); }
        } else if (e.Data.StartsWith("ERROR")) { g.Ready = e.Data; ready.Set(); }
      };
      g.P.BeginOutputReadLine(); g.P.BeginErrorReadLine();
      if (!ready.WaitOne(40000) || g.Hwnd == IntPtr.Zero) { g.Stop(); throw new Exception("FakeGame did not start: " + g.Ready); }
      return g;
    }

    // Waits until the load has settled: three presented-fps lines in a row within 4% of 60.
    public bool Calibrate(int maxSeconds) {
      var sw = Stopwatch.StartNew();
      while (sw.Elapsed.TotalSeconds < maxSeconds) {
        Thread.Sleep(500);
        lock (Fps) { int n = Fps.Count; if (n >= 3) { bool ok = true; for (int i = n - 3; i < n; i++) if (Math.Abs(Fps[i].Value - 60) > 2.5) ok = false; if (ok) return true; } }
      }
      return false;
    }

    public double FpsBetween(DateTime a, DateTime b) {
      lock (Fps) { double sum = 0; int n = 0; foreach (var kv in Fps) if (kv.Key >= a && kv.Key <= b) { sum += kv.Value; n++; } return n == 0 ? double.NaN : sum / n; }
    }

    // The window as the Recorder's list would show it.
    public WindowInfo Window() {
      foreach (var w in Native.ListWindows()) if (w.Handle == Hwnd) return w;
      return null;
    }

    public void Stop() {
      try { quit.Set(); } catch (Exception) { }
      try { if (!P.WaitForExit(8000)) { P.Kill(); P.WaitForExit(3000); } } catch (Exception) { }
      try { quit.Close(); } catch (Exception) { }
    }
  }

  // How many different pictures a video holds (each FakeGame frame shows its own frame number, so a repeated picture is a starved capture).
  public static int DistinctFrames(string video, out int total) {
    string so, se;
    total = 0;
    Proc.Run(Tools.Ffmpeg, "-hide_banner -loglevel error -nostdin -i " + Tools.Quote(video) + " -map 0:v:0 -f framemd5 -", 600000, out so, out se);
    var seen = new HashSet<string>();
    foreach (string l in so.Split('\n')) {
      if (l.StartsWith("#") || l.Trim().Length == 0) continue;
      var p = l.Split(',');
      if (p.Length >= 6) { total++; seen.Add(p[5].Trim()); }
    }
    return seen.Count;
  }

  public static int Run(string[] args, string build) {
    string mode = args.Length > 1 ? args[1] : "windowed";
    int seconds = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 15;
    string work = Path.Combine(build, "diag");
    Directory.CreateDirectory(work);
    var table = new List<string>();
    Console.WriteLine("diag: FakeGame " + mode + (mode == "windowed" ? " 1920x1080" : "") + ", load tuned to a GPU-bound 60 fps; " + seconds + " s each way");
    // The engine first, before the GPU is busy: its start opens test sessions on every encoder, and on this PC that next to a GPU held at 99%
    // reset the driver (Oct 18, 02:00:28) and took 60 s. The Recorder itself starts it when it opens, for the same reason.
    string startErr = Obs.Start(1920, 1080, 1920, 1080);
    if (startErr != null) { Console.WriteLine("ENGINE FAILED " + startErr); return 1; }
    GpuSafety.Check("the engine start");
    var g = Game.Start(build, mode, "1920x1080", "auto", 0, Path.Combine(work, "fakegame.log"));
    var smi = new Smi(); smi.Start();
    try {
      Console.WriteLine("FakeGame: " + g.Ready);
      Console.WriteLine("calibrated: " + g.Calibrate(40) + ", load " + g.Load);
      var t0 = DateTime.UtcNow; smi.Clear(); Thread.Sleep(6000);
      double alone = g.FpsBetween(t0, DateTime.UtcNow), ga, ea; smi.Avg(out ga, out ea);
      Console.WriteLine("ALONE FakeGame " + F(alone, "0.0") + " fps, GPU " + F(ga, "0") + "%, encoder " + F(ea, "0") + "%");
      table.Add("TABLE | FakeGame alone | " + F(alone, "0.0") + " fps | - | - | - | GPU " + F(ga, "0") + "% enc " + F(ea, "0") + "%");
      GpuSafety.Check("FakeGame alone");
      var win = g.Window();
      if (win == null) throw new Exception("FakeGame's window isn't in the window list");
      foreach (string method in new[] { "game", "window" }) {
        var cfg = new AppSettings { CaptureMethod = method };
        var size = Native.ClientSize(g.Hwnd);
        var rec = new Recording(null, win, size.Width, size.Height, work, "diag-" + mode + "-" + method, cfg, Path.Combine(work, "logs"));
        string err = Obs.Start(rec.BaseWidth, rec.BaseHeight, rec.OutWidth, rec.OutHeight);
        if (err == null && !rec.Start(out err, 20000)) { }
        if (err != null) { Console.WriteLine(method + ": START FAILED " + err); table.Add("TABLE | " + method + " | failed: " + err); continue; }
        smi.Clear();
        var a = DateTime.UtcNow;
        Thread.Sleep(seconds * 1000);
        var b = DateTime.UtcNow;
        double during = g.FpsBetween(a.AddSeconds(2), b), gu, eu; smi.Avg(out gu, out eu);
        string health = rec.Health;
        var res = rec.Stop();
        int total = 0;
        int distinct = res.Ok ? DistinctFrames(res.Mp4, out total) : 0;
        string line = method + ": capture " + rec.Capture + ", FakeGame " + F(during, "0.0") + " fps (alone " + F(alone, "0.0") + "), frames written " + res.Frames + " for " + F((b - a).TotalSeconds, "0.0") + " s, late " + res.Lagged + ", skipped " + res.Skipped +
                      ", distinct pictures " + distinct + " of " + total + " (" + F(total > 0 ? 100.0 * distinct / total : 0, "0") + "%), GPU " + F(gu, "0") + "% encoder " + F(eu, "0") + "%, status at the end: " + health + ", " + res.Message;
        Console.WriteLine(line);
        table.Add("TABLE | " + method + " capture | " + F(during, "0.0") + " fps | " + res.Frames + " written | late " + res.Lagged + " skipped " + res.Skipped + " | " + distinct + "/" + total + " distinct | GPU " + F(gu, "0") + "% enc " + F(eu, "0") + "%");
        GpuSafety.Check("the " + method + " capture run");
      }
    } finally { smi.Stop(); g.Stop(); }
    foreach (string t in table) Console.WriteLine(t);
    return 0;
  }
}
