// Tests for Bifrost Recorder: run build\RecorderTests.exe after build.ps1 (or build.ps1 -Test).
//   no arguments      the unit tests, then the end-to-end tests
//   --bench <Scene> <width> <height> <seconds>   records one of the test scenes and prints size, GPU and CPU figures
// Only windows these tests create are ever recorded or pictured. They are nearly invisible (alpha 1/255) and click-through;
// they can't sit off the monitors because a window that isn't on a monitor sends the capture nothing (measured, see PROGRESS.md).
// Everything the tests make is in build\test-output\ (cleared at the start); the screenshots go to docs\screenshots\m1\.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class RecorderTests {
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  static int passed, failed;
  static string project, build, outDir, shotsDir;
  const string WindowPrefix = "Bifrost Recorder test window";

  static void Check(string name, bool ok, string detail = null) {
    if (ok) passed++; else failed++;
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail != null ? "   [" + detail + "]" : ""));
  }

  static void Note(string text) { Console.WriteLine("      " + text); }

  [STAThread]
  static int Main(string[] args) {
    SetProcessDPIAware();
    try { Console.OutputEncoding = Encoding.UTF8; } catch (Exception) { }
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    build = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    project = Path.GetFullPath(Path.Combine(build, ".."));
    outDir = Path.Combine(build, "test-output");
    shotsDir = Path.Combine(project, "docs", "screenshots", "m1");
    TestWindow.LayerAlpha = 1;

    if (args.Length > 0 && args[0] == "--job-child") return JobChild();   // started by a run that already passed the guard; CPU only
    if (args.Length > 0 && args[0] == "--guard-check") { Console.WriteLine("game running: " + (RunningGame() ?? "none")); return 0; }   // reads the process list only; nothing else
    if (args.Length > 0 && args[0] == "--guard-test") { bool go = Guard(); Console.WriteLine("guard says: " + (go ? "go" : "refuse")); return go ? 0 : 2; }   // the guard and nothing after it
    if (!Guard()) return 2;
    if (args.Length > 0 && args[0] == "--bench") return Bench(args);
    if (args.Length > 0 && args[0] == "--hold") return Hold(args);

    if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    Directory.CreateDirectory(outDir);
    Directory.CreateDirectory(shotsDir);
    Console.WriteLine("Counts before: " + Counts());
    var watch = new Watcher(); watch.Start();
    try {
      Units();
      EndToEnd(watch);
    } catch (Exception ex) { Check("the run finished", false, ex.ToString()); }
    var violations = watch.Stop();
    lock (TestWindow.Placed) foreach (var p in TestWindow.Placed) Note("placed: " + p);
    string wantScreen = TestWindow.OnSecondary ? "(secondary)" : "(primary)";
    bool allPlaced = true;
    lock (TestWindow.Placed) foreach (var p in TestWindow.Placed) if (!p.Contains(wantScreen)) allPlaced = false;
    Check("every test window opened fully inside the " + (TestWindow.OnSecondary ? "secondary" : "primary") + " monitor, as asked", TestWindow.Placed.Count > 0 && allPlaced, TestWindow.Placed.Count + " windows");
    Check("no window of a child process was ever visible, and the Recorder's own window never touched a monitor", violations.Count == 0 && watch.Looks > 20,
      watch.Looks + " looks; " + string.Join("; ", violations.ToArray()));
    int alive = 0;
    lock (Proc.Started) foreach (int pid in Proc.Started) { try { var p = Process.GetProcessById(pid); if (!p.HasExited) alive++; } catch (Exception) { } }
    Check("no process the tests started is still running", alive == 0, alive + " alive of " + Proc.Started.Count);
    Console.WriteLine("Counts after:  " + Counts());
    Console.WriteLine();
    Console.WriteLine(passed + " passed, " + failed + " failed");
    return failed == 0 ? 0 : 1;
  }

  // ------------------------------------------------------------------------------------------------ the guard
  // Everything here uses the GPU (window capture and NVENC), so none of it runs next to a game, and two runs never overlap.
  // Process names that start with one of these count as a game.
  static readonly string[] GamePrefixes = { "ArmaReforger", "arma3", "DayZ" };   // ArmaReforgerSteam, ...SteamDiag, ...WorkbenchSteamDiag
  static Mutex runLock;   // held for the whole run

  static string RunningGame() {
    foreach (var p in Process.GetProcesses()) {
      try {
        string n = p.ProcessName;
        foreach (string g in GamePrefixes) if (n.StartsWith(g, StringComparison.OrdinalIgnoreCase)) return n + ".exe (pid " + p.Id + ")";
      } catch (Exception) { } finally { p.Dispose(); }
    }
    return null;
  }

  static bool Guard() {
    string game = RunningGame();
    if (game != null) {
      Console.Error.WriteLine("REFUSED: " + game + " is running. The tests, --bench and --hold capture windows and encode with the GPU, and never run next to a game.");
      return false;
    }
    bool mine;
    runLock = new Mutex(true, @"GlobalBifrostRecorderTestRun", out mine);
    if (!mine) { Console.Error.WriteLine("REFUSED: another RecorderTests run (tests, --bench or --hold) is already going."); return false; }
    // a game that starts during the run ends it: exiting closes the job, which ends ffmpeg with it
    var watch = new Thread(delegate() {
      while (true) {
        Thread.Sleep(1000);
        string g = RunningGame();
        if (g != null) { Console.Error.WriteLine("ABORTED: " + g + " started during the run."); Environment.Exit(3); }
      }
    });
    watch.IsBackground = true; watch.Name = "game guard"; watch.Start();
    return true;
  }

  static string Counts() {
    return "ffmpeg=" + Process.GetProcessesByName("ffmpeg").Length + " ffprobe=" + Process.GetProcessesByName("ffprobe").Length +
           " cmd=" + Process.GetProcessesByName("cmd").Length + " conhost=" + Process.GetProcessesByName("conhost").Length;
  }

  // ------------------------------------------------------------------------------------------------ unit tests
  static void Units() {
    Console.WriteLine("== unit tests");
    string tmp = Path.Combine(outDir, "unit"); Directory.CreateDirectory(tmp);
    string profile = @"C:\Users\Someone";

    // settings and the recordings folder
    string good = Path.Combine(tmp, "good.json");
    File.WriteAllText(good, "{\"ManagedBy\":\"Bifrost Suite\",\"DataFolder\":\"D:\\\\Bifrost Suite\\\\Bifrost Recorder\"}");
    Check("DataFolder gives <DataFolder>\\Recordings", Settings.RecordingsFolder(good, profile) == @"D:\Bifrost Suite\Bifrost Recorder\Recordings", Settings.RecordingsFolder(good, profile));
    Check("no settings file falls back to Videos\\Bifrost Recorder", Settings.RecordingsFolder(Path.Combine(tmp, "none.json"), profile) == @"C:\Users\Someone\Videos\Bifrost Recorder");
    string bad = Path.Combine(tmp, "bad.json"); File.WriteAllText(bad, "{ not json");
    Check("a settings file that isn't JSON falls back", Settings.RecordingsFolder(bad, profile) == @"C:\Users\Someone\Videos\Bifrost Recorder");
    foreach (string body in new[] { "{}", "{\"DataFolder\":\"\"}", "{\"DataFolder\":\"   \"}", "{\"DataFolder\":\"relative\\\\path\"}", "{\"DataFolder\":5}", "{\"DataFolder\":null}", "[1,2]" }) {
      File.WriteAllText(bad, body);
      Check("settings " + body + " falls back", Settings.RecordingsFolder(bad, profile) == @"C:\Users\Someone\Videos\Bifrost Recorder");
    }
    File.WriteAllText(bad, "{\"DataFolder\":\"E:\\\\Data\\\\\"}");
    Check("a DataFolder with a trailing slash still gives one Recordings folder", Settings.RecordingsFolder(bad, profile) == @"E:\Data\Recordings", Settings.RecordingsFolder(bad, profile));

    // file names
    var when = new DateTime(2026, 10, 17, 18, 5, 9);
    Check("file name is <program> yyyy-MM-dd HH-mm-ss", FileNames.BaseName("ArmaReforgerSteam", when) == "ArmaReforgerSteam 2026-10-17 18-05-09");
    const string exe = @"C:\Games\ArmaReforgerSteam.exe";
    Check("name: the exe's FileDescription comes first", FileNames.ProgramName(exe, "Arma Reforger - 1.2 - window", "Arma Reforger") == "Arma Reforger");
    Check("name: no FileDescription: the window title", FileNames.ProgramName(exe, "Arma Reforger", null) == "Arma Reforger" && FileNames.ProgramName(exe, "Arma", "  ") == "Arma");
    Check("name: no description and no title: the exe name without .exe", FileNames.ProgramName(exe, "", " ") == "ArmaReforgerSteam" && FileNames.ProgramName(exe, null, null) == "ArmaReforgerSteam");
    Check("name: characters a file name can't hold are replaced", FileNames.ProgramName(null, "a/b:c*d?", "") == "a_b_c_d_" && FileNames.ProgramName(exe, "", "Foo \"Bar\" <1>|2") == "Foo _Bar_ _1__2");
    Check("name: trailing dots and spaces are dropped", FileNames.ProgramName(exe, "", "Foo. . ") == "Foo");
    Check("name: nothing to go on gives Recording", FileNames.ProgramName("", "  ", null) == "Recording" && FileNames.ProgramName(null, null, "...") == "Recording");
    Check("name: Windows device names (CON, nul, COM1) get an underscore", FileNames.ProgramName(null, "", "CON") == "CON_" && FileNames.ProgramName(null, "", "nul") == "nul_" && FileNames.ProgramName(null, "", "COM1") == "COM1_" && FileNames.ProgramName(null, "", "Console") == "Console");
    Check("name: a long one is cut to 60", FileNames.ProgramName(exe, "", new string('A', 200)).Length == 60);
    string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
    Check("name: read from a real exe's version info (Notepad)", FileNames.Friendly(notepad, "ignored") == "Notepad", FileNames.Friendly(notepad, "ignored"));
    Check("name: an exe that isn't there falls back to the title", FileNames.Friendly(@"C:\nowhere\x.exe", "A Title") == "A Title");
    string names = Path.Combine(tmp, "names"); Directory.CreateDirectory(names);
    Check("a free name stays as it is", FileNames.Unique(names, "Game 1") == "Game 1");
    File.WriteAllText(Path.Combine(names, "Game 1.mp4"), "x");
    Check("a name taken by an .mp4 gets (2)", FileNames.Unique(names, "Game 1") == "Game 1 (2)");
    File.WriteAllText(Path.Combine(names, "Game 1 (2).mkv"), "x");
    Check("a name taken by an .mkv gets (3)", FileNames.Unique(names, "Game 1") == "Game 1 (3)");

    // encoder pick
    var tried = new List<string>();
    var pick = EncoderProfile.Pick(delegate(EncoderProfile e) { tried.Add(e.Name); return e.Name == "hevc_amf"; });
    Check("encoder pick: the first that works wins, in the order nvenc, amf, qsv", pick.Name == "hevc_amf" && string.Join(",", tried.ToArray()) == "hevc_nvenc,hevc_amf", string.Join(",", tried.ToArray()));
    Check("encoder pick: nvenc first when it works (and nothing else is tried)", EncoderProfile.Pick(delegate(EncoderProfile e) { return true; }).Name == "hevc_nvenc");
    Check("encoder pick: qsv when only it works", EncoderProfile.Pick(delegate(EncoderProfile e) { return e.Name == "hevc_qsv"; }).Name == "hevc_qsv");
    Check("encoder pick: none works gives the software encoder", EncoderProfile.Pick(delegate(EncoderProfile e) { return false; }) == EncoderProfile.Software);
    Check("only NVENC is marked as run on hardware here", EncoderProfile.Nvenc.Verified && !EncoderProfile.Amf.Verified && !EncoderProfile.Qsv.Verified);

    // bitrate ceiling
    Check("ceiling 30000 kb/s at 1920x1080", FfmpegArgs.MaxrateKbps(1920, 1080) == 30000, FfmpegArgs.MaxrateKbps(1920, 1080).ToString());
    Check("ceiling scales by pixels (2560x1440 = 53300)", FfmpegArgs.MaxrateKbps(2560, 1440) == 53300, FfmpegArgs.MaxrateKbps(2560, 1440).ToString());
    Check("ceiling at 3840x2160 = 120000", FfmpegArgs.MaxrateKbps(3840, 2160) == 120000);
    Check("ceiling never under 5000", FfmpegArgs.MaxrateKbps(640, 360) == 5000);

    // arguments
    string rec = FfmpegArgs.Record(EncoderProfile.Nvenc, 12345, 1920, 1080, @"C:\Rec ordings\Game 1.mkv");
    Check("capture: this window, 60 max, cursor on, no border", rec.Contains("gfxcapture=hwnd=12345:max_framerate=60:capture_cursor=true:display_border=false"), rec);
    Check("capture: even width and height", rec.Contains("width=-2:height=-2"));
    Check("clock: constant 60 fps (fps filter and cfr)", rec.Contains(",fps=60,") && rec.Contains("-fps_mode cfr"));
    Check("NVENC path keeps frames on the GPU: no hwdownload, no scale, no format conversion", !rec.Contains("hwdownload") && !rec.Contains("scale_d3d11") && !rec.Contains(",scale=") && !rec.Contains("format=") && !rec.Contains("hwupload"));
    Check("NVENC: HEVC Main, constant quality 24 in VBR with a 30000k ceiling", rec.Contains("-c:v hevc_nvenc") && rec.Contains("-profile:v main") && rec.Contains("-rc vbr -cq 24 -b:v 0 -maxrate 30000k -bufsize 60000k"));
    Check("NVENC: preset p5, tune hq, spatial AQ, B-frames", rec.Contains("-preset p5 -tune hq") && rec.Contains("-spatial-aq 1") && rec.Contains("-bf 2"));
    Check("2-second keyframes (-g 120)", rec.Contains("-g 120"));
    Check("tagged as what the pixels are: tv range, BT.709 primaries and transfer, BT.601 matrix from NVENC", rec.Contains("setparams=colorspace=smpte170m:range=tv:color_primaries=bt709:color_trc=bt709"));
    Check("the file is flushed packet by packet (a crash keeps it) and is the .mkv, quoted", rec.Contains("-flush_packets 1") && rec.EndsWith("\"C:\\Rec ordings\\Game 1.mkv\""));
    Check("progress is read from ffmpeg", rec.Contains("-progress pipe:1"));
    string amf = FfmpegArgs.Record(EncoderProfile.Amf, 1, 1920, 1080, "a.mkv"), qsv = FfmpegArgs.Record(EncoderProfile.Qsv, 1, 1920, 1080, "a.mkv");
    Check("AMF arguments (untested) are HEVC, quality-VBR, 30000k ceiling, GPU frames", amf.Contains("-c:v hevc_amf") && amf.Contains("-rc qvbr -qvbr_quality_level 24") && amf.Contains("-maxrate 30000k") && !amf.Contains("hwdownload"));
    Check("QSV arguments (untested) are HEVC, ICQ 24, GPU frames mapped to QSV", qsv.Contains("-c:v hevc_qsv") && qsv.Contains("-global_quality 24") && qsv.Contains("hwmap=derive_device=qsv") && !qsv.Contains("hwdownload"));
    string soft = FfmpegArgs.Record(EncoderProfile.Software, 1, 1280, 720, "a.mkv");
    Check("software fallback comes down to the CPU and converts with BT.709", soft.Contains("hwdownload") && soft.Contains("out_color_matrix=bt709") && soft.Contains("colorspace=bt709") && soft.Contains("-c:v libx264"));
    string remux = FfmpegArgs.Remux(EncoderProfile.Nvenc, "a b.mkv", "a b.mp4");
    Check("remux copies the stream, faststart, hvc1 tag", remux.Contains("-c copy") && remux.Contains("-movflags +faststart") && remux.Contains("-tag:v hvc1") && remux.Contains("\"a b.mp4\""));
    Check("remux of H.264 has no hvc1 tag", !FfmpegArgs.Remux(EncoderProfile.Software, "a.mkv", "a.mp4").Contains("hvc1"));
    string test = FfmpegArgs.TestEncode(EncoderProfile.Nvenc);
    Check("the encoder test uploads to d3d11 and uses the same options and chain", test.Contains("-init_hw_device d3d11va") && test.Contains("hwupload") && test.Contains("-c:v hevc_nvenc") && test.Contains("setparams=") && test.EndsWith("-f null -"));
    Check("probe arguments ask for the stream and the format as JSON", FfmpegArgs.Probe("x.mp4", true).Contains("-count_frames") && FfmpegArgs.Probe("x.mp4", false).Contains("-of json") && !FfmpegArgs.Probe("x.mp4", false).Contains("-count_frames"));

    // which windows are offered
    uint me = 100, other = 200;
    Check("window: an ordinary one is offered", Native.Capturable(true, "Arma", false, false, false, false, other, me, "X"));
    Check("window: not visible is not", !Native.Capturable(false, "Arma", false, false, false, false, other, me, "X"));
    Check("window: no title is not", !Native.Capturable(true, "", false, false, false, false, other, me, "X") && !Native.Capturable(true, "  ", false, false, false, false, other, me, "X"));
    Check("window: cloaked is not", !Native.Capturable(true, "Arma", true, false, false, false, other, me, "X"));
    Check("window: a tool window is not, unless it says it's an app window", !Native.Capturable(true, "T", false, true, false, false, other, me, "X") && Native.Capturable(true, "T", false, true, true, false, other, me, "X"));
    Check("window: an owned window (a dialog) is not", !Native.Capturable(true, "Dlg", false, false, false, true, other, me, "X"));
    Check("window: the Recorder itself is not", !Native.Capturable(true, "Bifrost Recorder", false, false, false, false, me, me, "X"));
    Check("window: the desktop is not", !Native.Capturable(true, "Program Manager", false, false, false, false, other, me, "Progman"));
    var found = Native.ListWindows();
    bool sane = true; uint own = (uint)Process.GetCurrentProcess().Id;
    foreach (var w in found) if (string.IsNullOrWhiteSpace(w.Title) || w.ProcessId == own || w.Handle == IntPtr.Zero) sane = false;
    Check("the live window list is well formed (count only; titles are not printed)", sane, found.Count + " windows");

    JobTest();
    EncoderTest();
  }

  // A process that is killed outright (what a crash looks like) must take its ffmpeg with it: the Job Object.
  static void JobTest() {
    string exe = Path.Combine(build, "RecorderTests.exe");
    var p = Proc.Start(exe, "--job-child", false);
    string line = p.StandardOutput.ReadLine() ?? "";
    int pid = line.StartsWith("PID=") ? int.Parse(line.Substring(4)) : -1;
    bool wasRunning = false;
    try { wasRunning = pid > 0 && !Process.GetProcessById(pid).HasExited; } catch (Exception) { }
    p.WaitForExit(15000);
    bool gone = false;
    for (int i = 0; i < 50 && !gone; i++) {
      try { var c = Process.GetProcessById(pid); gone = c.HasExited; } catch (ArgumentException) { gone = true; }
      if (!gone) Thread.Sleep(100);
    }
    Check("job object: a child ffmpeg was running while its parent lived", wasRunning, line);
    Check("job object: the parent was killed outright and its ffmpeg ended with it", p.HasExited && gone, "ffmpeg " + pid + " gone=" + gone);
  }

  // --hold <Scene> <width> <height> <seconds>: opens a test window and prints its handle, for trying ffmpeg settings by hand.
  static int Hold(string[] a) {
    var win = TestWindow.Open((Scene)Enum.Parse(typeof(Scene), a[1]), int.Parse(a[2]), int.Parse(a[3]), false, WindowPrefix + ": held");
    Console.WriteLine("HWND=" + win.Handle.ToInt64()); Console.Out.Flush();
    Thread.Sleep(int.Parse(a[4]) * 1000);
    win.CloseWindow();
    return 0;
  }

  static int JobChild() {
    var p = Proc.Start(Ffmpeg.Exe, "-hide_banner -loglevel error -re -f lavfi -i testsrc=size=320x240:rate=30 -t 600 -f null -", true);
    Console.WriteLine("PID=" + p.Id); Console.Out.Flush();
    Thread.Sleep(1500);
    Process.GetCurrentProcess().Kill();
    return 0;
  }

  static void EncoderTest() {
    Check("ffmpeg.exe and ffprobe.exe are in bin", File.Exists(Ffmpeg.Exe) && File.Exists(Ffmpeg.ProbeExe));
    var swatch = Stopwatch.StartNew();
    var enc = EncoderChoice.Detect(Ffmpeg.Exe);
    long ms = swatch.ElapsedMilliseconds;
    foreach (var l in EncoderChoice.Log.Split('\n')) if (l.Trim().Length > 0) Note(l.Trim());
    Check("encoder detection picks hevc_nvenc on this PC (RTX 3070 Ti)", enc.Name == "hevc_nvenc", enc.Name + " in " + ms + " ms");
    Check("the pick is cached: asking again is instant and the same", EncoderChoice.Detect(Ffmpeg.Exe) == enc && EncoderChoice.Picked == enc);
    string why;
    Check("the software encoder also passes its own test encode", EncoderChoice.Works(Ffmpeg.Exe, EncoderProfile.Software, out why), why);
  }

  // ------------------------------------------------------------------------------------------------ end to end
  static void EndToEnd(Watcher watch) {
    Console.WriteLine("== end to end");
    // the windows these tests own: one that moves, two that don't (they make the list look like a list)
    var moving = TestWindow.Open(Scene.Simple, 1280, 720, true, WindowPrefix + ": moving bar and frame counter");
    var still1 = TestWindow.Open(Scene.Static, 800, 450, true, WindowPrefix + ": drawn once (800 x 450)");
    var still2 = TestWindow.Open(Scene.Static, 640, 360, true, WindowPrefix + ": drawn once (640 x 360)");
    uint own = (uint)Process.GetCurrentProcess().Id;
    Native.ExcludePid = 0;   // the test windows are this process's own
    var form = OpenRecorder(delegate(WindowInfo w) { return w.ProcessId == own && w.Title.StartsWith(WindowPrefix); }, Path.Combine(outDir, "Recordings"));
    watch.Quiet(form.Handle);
    try {
      Check("the Recorder's window opened and the encoder was chosen", WaitFor(delegate { return form.EncoderReady; }, 60000, "encoder"));
      Invoke(form, delegate { form.ReloadWindows(); });
      Check("the list shows the three test windows (and nothing else: the tests filter it to their own)", form.WindowCount == 3, form.WindowCount.ToString());
      Check("Record is off until a window is picked", !form.RecordEnabled);
      Check("the moving test window is selectable", Invoke(form, delegate { return form.SelectHwnd(moving.Handle); }));
      Check("Record is on once a window is picked", form.RecordEnabled && form.RecordText == "Record");
      Shot(form.Handle, "01-window-list.png");
      Shot(moving.Handle, "02-test-window.png");

      var opened = new List<string>();
      form.OpenFolder = delegate(string f) { opened.Add(f); };
      Invoke(form, delegate { form.PressOpen(); });
      Check("Open recordings folder asks for the Recordings folder (and made it)", opened.Count == 1 && opened[0] == Path.Combine(outDir, "Recordings") && Directory.Exists(opened[0]), string.Join(",", opened.ToArray()));

      // the main recording: 20 seconds through the real window controls
      long d0 = moving.Drawn, l0 = moving.Late;
      var clock = Stopwatch.StartNew();
      Invoke(form, delegate { form.PressRecord(); });
      Check("pressing Record starts a recording (first picture arrived)", WaitFor(delegate { return form.Mode == MainForm.State.Recording; }, 30000, "recording"), form.StatusText);
      Check("while recording the button says Stop", form.RecordText == "Stop");
      var rec = form.Current;
      Note("ffmpeg " + rec.Command);
      WaitFor(delegate { return rec.Elapsed.TotalSeconds >= 8; }, 20000, "8 s");
      Shot(form.Handle, "03-recording.png");
      WaitFor(delegate { return rec.Elapsed.TotalSeconds >= 20; }, 40000, "20 s");
      double elapsed = rec.Elapsed.TotalSeconds;
      long cpuMs = (long)rec.Cpu.TotalMilliseconds;
      Invoke(form, delegate { form.PressRecord(); });
      Check("pressing Stop saves and the window goes back to idle", WaitFor(delegate { return form.Mode == MainForm.State.Idle && form.LastResult != null; }, 60000, "saved"), form.StatusText);
      var res = form.LastResult;
      Shot(form.Handle, "04-saved.png");
      double drawnRate = (moving.Drawn - d0) / clock.Elapsed.TotalSeconds;
      Note("the test window drew " + (moving.Drawn - d0) + " frames in " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s (" + drawnRate.ToString("0.0") + "/s), " + (moving.Late - l0) + " late");
      Note("ffmpeg CPU time " + cpuMs + " ms in " + elapsed.ToString("0.0") + " s of recording");
      CheckMainRecording(res, elapsed, form);

      // the window closing under a recording
      Console.WriteLine("-- the window closes while recording");
      var doomed = TestWindow.Open(Scene.Simple, 640, 360, true, WindowPrefix + ": about to close");
      Invoke(form, delegate { form.ReloadWindows(); form.SelectHwnd(doomed.Handle); });
      form.LastResult = null;
      Invoke(form, delegate { form.PressRecord(); });
      Check("closing test: recording started", WaitFor(delegate { return form.Mode == MainForm.State.Recording; }, 30000, "recording"), form.StatusText);
      Thread.Sleep(4000);
      doomed.CloseWindow();
      Check("closing the window stops the recording and saves it cleanly by itself", WaitFor(delegate { return form.Mode == MainForm.State.Idle && form.LastResult != null; }, 30000, "auto stop"), form.StatusText);
      var closed = form.LastResult;
      Check("the closed window's recording is a good .mp4 of about 4 seconds, not forced", closed != null && closed.Ok && !closed.Forced && closed.Seconds > 2.5 && closed.Seconds < 6, closed == null ? "null" : closed.Message + " " + closed.Seconds.ToString("0.0"));
      Check("closing test: no .mkv left", Directory.GetFiles(Path.Combine(outDir, "Recordings"), "*.mkv").Length == 0);

      // the Recorder itself closed under a recording: it finishes the file first, then closes
      Console.WriteLine("-- the Recorder's own window is closed while recording");
      var keep = TestWindow.Open(Scene.Simple, 640, 360, true, WindowPrefix + ": recorder closes");
      Invoke(form, delegate { form.ReloadWindows(); form.SelectHwnd(keep.Handle); });
      form.LastResult = null;
      Invoke(form, delegate { form.PressRecord(); });
      Check("close test: recording started", WaitFor(delegate { return form.Mode == MainForm.State.Recording; }, 30000, "recording"), form.StatusText);
      Thread.Sleep(3000);
      Invoke(form, delegate { form.Close(); });
      Check("closing the Recorder while recording saves the file first and then closes", WaitFor(delegate { return form.IsDisposed; }, 40000, "closed") && form.LastResult != null && form.LastResult.Ok && form.LastResult.Seconds > 2, form.LastResult == null ? "no result" : form.LastResult.Message);
      keep.CloseWindow();
    } finally {
      try { if (!form.IsDisposed) Invoke(form, delegate { form.Close(); }); } catch (Exception) { }
      moving.CloseWindow(); still1.CloseWindow(); still2.CloseWindow();
    }

    OtherRecordings();
  }

  static void CheckMainRecording(RecordingResult res, double elapsed, MainForm form) {
    Check("recording saved: " + (res == null ? "?" : res.Message), res != null && res.Ok && res.Mp4 != null && File.Exists(res.Mp4), res == null ? null : res.Message);
    if (res == null || !res.Ok) return;
    string friendly = FileNames.Friendly(Application.ExecutablePath, WindowPrefix + ": moving bar and frame counter");
    Check("the file is named <friendly name> yyyy-MM-dd HH-mm-ss.mp4 (the test exe has no FileDescription, so it's the window's title)",
      System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(res.Mp4), "^" + System.Text.RegularExpressions.Regex.Escape(friendly) + @" \d{4}-\d\d-\d\d \d\d-\d\d-\d\d\.mp4$"), Path.GetFileName(res.Mp4));
    Check("it went to the Recordings folder", Path.GetDirectoryName(res.Mp4) == Path.Combine(outDir, "Recordings"));
    Check("the .mkv is gone", !File.Exists(Path.ChangeExtension(res.Mp4, ".mkv")));
    var p = ProbeResult.Run(res.Mp4, true);
    Check("ffprobe reads the .mp4 clean", p.Ok, p.Error);
    Check("video is hevc, Main profile, 8-bit 4:2:0", p.Codec == "hevc" && p.Profile == "Main" && p.PixFmt == "yuv420p", p.Codec + " " + p.Profile + " " + p.PixFmt);
    Check("r_frame_rate is 60/1", p.FrameRate == "60/1", p.FrameRate + " (avg " + p.AvgFrameRate + ")");
    Check("duration is about 20 s", Math.Abs(p.Duration - 20.0) < 1.0, p.Duration.ToString("0.000"));
    Check("frame count is about 1200 (within 5%: the clock starts at the first picture, which is reported up to a quarter second late)", Math.Abs(p.Frames - 1200) <= 60, p.Frames.ToString());
    Check("frame count matches duration at 60 fps (within 2 frames)", Math.Abs(p.Frames - p.Duration * 60) <= 2, p.Frames + " frames, " + p.Duration.ToString("0.000") + " s = " + (p.Duration * 60).ToString("0.0"));
    Check("picture is the window's own area: 1280 x 720", p.Width == 1280 && p.Height == 720, p.Width + "x" + p.Height);
    Check("tagged tv range, BT.709 primaries and transfer, smpte170m matrix (what NVENC makes)", p.Range == "tv" && p.Primaries == "bt709" && p.Transfer == "bt709" && p.Space == "smpte170m", p.Range + " " + p.Primaries + " " + p.Transfer + " " + p.Space);
    Check("the MP4 tag is hvc1", p.Tag == "hvc1", p.Tag);
    Note("MEASURED  frames=" + p.Frames + "  duration=" + p.Duration.ToString("0.000") + " s  elapsed(app)=" + elapsed.ToString("0.000") + " s  size=" + (res.Bytes / 1048576.0).ToString("0.00") +
         " MB  r_frame_rate=" + p.FrameRate + "  avg_frame_rate=" + p.AvgFrameRate + "  dup=" + res.Dup + "  drop=" + res.Drop);
    Note("the Recorder's own report: " + form.StatusText);

    // a still from the recording, and its colours against what the window drew
    string png = Path.Combine(shotsDir, "05-recorded-frame.png");
    string so, se;
    int code = Proc.Run(Ffmpeg.Exe, "-hide_banner -loglevel error -nostdin -y -ss 10 -i " + FfmpegArgs.Quote(res.Mp4) + " -frames:v 1 " + FfmpegArgs.Quote(png), 30000, out so, out se);
    Check("a still frame at 10 s was pulled from the recorded .mp4", code == 0 && File.Exists(png), se);
    if (code == 0 && File.Exists(png)) CheckColours(png, 1280, 720, "the recorded HEVC");
  }

  // The swatches along the bottom of the Simple scene against what they were drawn as.
  static void CheckColours(string png, int w, int h, string what) {
    using (var bmp = new Bitmap(png)) {
      int worst = 0; string worstAt = "";
      for (int i = 0; i < TestWindow.Swatches.Length; i++) {
        var want = TestWindow.Swatches[i];
        var got = bmp.GetPixel(20 + i * TestWindow.SwatchWidth + TestWindow.SwatchWidth / 2, h - TestWindow.SwatchHeight - 20 + TestWindow.SwatchHeight / 2);
        int d = Math.Max(Math.Abs(got.R - want.R), Math.Max(Math.Abs(got.G - want.G), Math.Abs(got.B - want.B)));
        if (d > worst) { worst = d; worstAt = "swatch " + i + " drawn " + want.R + "," + want.G + "," + want.B + " came back " + got.R + "," + got.G + "," + got.B; }
      }
      Check("colours: " + what + " decodes to the colours the window drew (worst error " + worst + " of 255)", worst <= 6, worstAt);
    }
  }

  // Stalls, windows that don't draw, a resize, and the software fallback: short recordings straight through the Recording class.
  static void OtherRecordings() {
    Console.WriteLine("-- recordings that stress the edges");
    string folder = Path.Combine(outDir, "edges");

    // a window that stops drawing mid-recording: ffmpeg gets nothing to write and ignores q until the Recorder asks the window to repaint
    var w1 = TestWindow.Open(Scene.Simple, 640, 360, true, WindowPrefix + ": stalls");
    var enc = EncoderChoice.Picked;
    var r1 = new Recording(enc, w1.Handle, 640, 360, folder, "stall");
    string err;
    bool started = r1.Start(out err, 15000);
    Check("stall: recording started", started, err);
    if (started) {
      Thread.Sleep(2500);
      w1.Frozen = true;
      Thread.Sleep(4000);
      var sw = Stopwatch.StartNew();
      var res = r1.Stop();
      Check("stall: Stop finishes (it doesn't wait for a frame that never comes), not by force, in under 5 s", res.Ok && !res.Forced && sw.Elapsed.TotalSeconds < 5, res.Message + " forced=" + res.Forced + " " + sw.Elapsed.TotalSeconds.ToString("0.0") + "s");
      if (res.Ok) {
        var p = ProbeResult.Run(res.Mp4, true);
        Check("stall: the file covers the whole 6.5 s, 60/1, constant frame count for the time (repeated frames fill the stall)", p.Ok && p.Duration > 5.5 && Math.Abs(p.Frames - p.Duration * 60) <= 3 && p.FrameRate == "60/1", p.Frames + " frames " + p.Duration.ToString("0.00") + " s " + p.FrameRate);
      }
    }
    w1.CloseWindow();

    // a window that has drawn once and never again: no first picture until the Recorder asks for one
    var w2 = TestWindow.Open(Scene.Static, 640, 360, true, WindowPrefix + ": never redraws");
    var r2 = new Recording(enc, w2.Handle, 640, 360, folder, "static");
    started = r2.Start(out err, 15000);
    Check("static window: recording starts (the Recorder asked it to repaint)", started, err);
    if (started) {
      Thread.Sleep(3000);
      var res = r2.Stop();
      Check("static window: saved as a good .mp4", res.Ok && !res.Forced, res.Message + " forced=" + res.Forced);
      if (res.Ok) Note("static window recording: " + res.Seconds.ToString("0.00") + " s, " + res.Frames + " frames");
    }
    w2.CloseWindow();

    // a window that's resized while recorded keeps the output size
    var w3 = TestWindow.Open(Scene.Simple, 640, 360, true, WindowPrefix + ": resized");
    var r3 = new Recording(enc, w3.Handle, 640, 360, folder, "resize");
    started = r3.Start(out err, 15000);
    Check("resize: recording started", started, err);
    if (started) {
      Thread.Sleep(2000);
      w3.Invoke(new MethodInvoker(delegate { w3.ClientSize = new Size(960, 540); }));
      Thread.Sleep(3000);
      var res = r3.Stop();
      Check("resize: saved as a good .mp4", res.Ok && !res.Forced, res.Message);
      if (res.Ok) {
        var p = ProbeResult.Run(res.Mp4, false);
        Check("resize: the picture stays the size it started at (640 x 360)", p.Width == 640 && p.Height == 360, p.Width + "x" + p.Height);
      }
    }
    w3.CloseWindow();

    // the software encoder, end to end: it's the way out when no GPU encoder works, and must produce a good file with BT.709 colours
    var w4 = TestWindow.Open(Scene.Simple, 1280, 720, true, WindowPrefix + ": software encoder");
    var r4 = new Recording(EncoderProfile.Software, w4.Handle, 1280, 720, folder, "software");
    started = r4.Start(out err, 15000);
    Check("software: recording started", started, err);
    if (started) {
      Thread.Sleep(6000);
      long cpu4 = (long)r4.Cpu.TotalMilliseconds;
      var res = r4.Stop();
      Check("software: saved as a good .mp4", res.Ok && !res.Forced, res.Message);
      if (res.Ok) {
        var p = ProbeResult.Run(res.Mp4, true);
        Check("software: h264, 60/1, about 6 s, tagged BT.709 tv", p.Codec == "h264" && p.FrameRate == "60/1" && Math.Abs(p.Duration - 6) < 1.5 && p.Space == "bt709" && p.Range == "tv", p.Codec + " " + p.FrameRate + " " + p.Duration.ToString("0.00") + " " + p.Space + " " + p.Range);
        string png = Path.Combine(outDir, "software.png"), so, se;
        Proc.Run(Ffmpeg.Exe, "-hide_banner -loglevel error -nostdin -y -ss 3 -i " + FfmpegArgs.Quote(res.Mp4) + " -frames:v 1 " + FfmpegArgs.Quote(png), 30000, out so, out se);
        if (File.Exists(png)) CheckColours(png, 1280, 720, "the software H.264");
        Note("software encoder: " + (res.Bytes / 1048576.0).ToString("0.00") + " MB for " + res.Seconds.ToString("0.0") + " s; ffmpeg CPU " + cpu4 + " ms");
      }
    }
    w4.CloseWindow();

    // a window that doesn't exist, and a recording folder that can't be made: clear failures, nothing left behind
    var r5 = new Recording(enc, new IntPtr(0x7FFFF), 640, 360, folder, "gone");
    started = r5.Start(out err, 3000);
    Check("a window that doesn't exist fails with a clear message and leaves nothing", !started && err == "That window has gone." && !File.Exists(Path.Combine(folder, "gone.mkv")), err);
    Check("no .mkv is left anywhere in the edge-case folder", Directory.GetFiles(folder, "*.mkv").Length == 0, string.Join(",", Directory.GetFiles(folder, "*.mkv")));
  }

  // ------------------------------------------------------------------------------------------------ helpers
  static MainForm OpenRecorder(Func<WindowInfo, bool> filter, string folder) {
    MainForm.Quiet = true;
    MainForm f = null;
    var shown = new ManualResetEvent(false);
    var t = new Thread(delegate() {
      f = new MainForm();
      f.WindowFilter = filter; f.RecordingsFolder = folder;
      f.Shown += delegate { shown.Set(); };
      Application.Run(f);
    });
    t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Name = "recorder window"; t.Start();
    if (!shown.WaitOne(30000)) throw new Exception("the Recorder's window didn't open");
    return f;
  }

  static void Invoke(Control c, MethodInvoker a) { c.Invoke(a); }
  static T Invoke<T>(Control c, Func<T> a) { return (T)c.Invoke(a); }

  static bool WaitFor(Func<bool> cond, int ms, string what) {
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(50); }
    return cond();
  }

  // A picture of one of the tests' own windows (PrintWindow, full content) into docs\screenshots\m1.
  static void Shot(IntPtr h, string name) {
    Bitmap bmp = null;
    var ctl = Control.FromHandle(h);
    if (ctl != null && ctl.InvokeRequired) bmp = (Bitmap)ctl.Invoke(new Func<Bitmap>(delegate { return Native.Picture(h); }));
    else bmp = Native.Picture(h);
    Check("screenshot " + name + " taken of the tests' own window", bmp != null);
    if (bmp == null) return;
    bmp.Save(Path.Combine(shotsDir, name), ImageFormat.Png);
    bmp.Dispose();
  }

  // ------------------------------------------------------------------------------------------------ the watcher
  // Looks every 50 ms at every window on the desktop: no window of a process the tests started may ever be visible (that
  // would be a console), and the Recorder's own window, which tests mark as quiet, may never touch a monitor.
  class Watcher {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);

    readonly List<string> violations = new List<string>();
    readonly List<IntPtr> quiet = new List<IntPtr>();
    Thread t; volatile bool stop;
    public int Looks;

    public void Quiet(IntPtr h) { lock (quiet) quiet.Add(h); }
    public void Start() { t = new Thread(Loop) { IsBackground = true, Name = "window watcher" }; t.Start(); }
    public List<string> Stop() { stop = true; t.Join(3000); lock (violations) return new List<string>(violations); }

    void Loop() {
      while (!stop) {
        HashSet<uint> pids;
        lock (Proc.Started) { pids = new HashSet<uint>(); foreach (int p in Proc.Started) pids.Add((uint)p); }
        EnumWindows(delegate(IntPtr h, IntPtr l) {
          uint pid; GetWindowThreadProcessId(h, out pid);
          if (pids.Contains(pid) && IsWindowVisible(h)) { var c = new StringBuilder(128); GetClassName(h, c, 128); Add("a visible window of a child process (" + c + ")"); }
          return true;
        }, IntPtr.Zero);
        lock (quiet) foreach (var h in quiet) {
          Native.RECT r;
          if (!Native.IsWindow(h) || !Native.GetWindowRect(h, out r)) continue;
          foreach (var s in Screen.AllScreens) if (s.Bounds.IntersectsWith(new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top))) Add("the Recorder's window was on a monitor");
        }
        // the tests' own windows must stay invisible (alpha 1/255), click-through, no-activate and never topmost, and out of the foreground;
        // with the secondary-monitor fallback they must also stay off the primary monitor
        lock (TestWindow.Handles) foreach (var h in TestWindow.Handles) {
          Native.RECT r;
          if (!Native.IsWindow(h) || !Native.GetWindowRect(h, out r)) continue;
          int ex = GetWindowLong(h, -20);
          if ((ex & 0x8) != 0) Add("a test window was topmost");
          if ((ex & (0x80000 | 0x20 | 0x08000000)) != (0x80000 | 0x20 | 0x08000000)) Add("a test window wasn't layered, click-through and no-activate");
          uint key, flags; byte alpha;
          if (GetLayeredWindowAttributes(h, out key, out alpha, out flags) && (flags & 2) != 0 && alpha > 2) Add("a test window was visible (alpha " + alpha + ")");
          if (TestWindow.OnSecondary) foreach (var s in Screen.AllScreens) if (s.Primary && s.Bounds.IntersectsWith(new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top))) Add("a test window was on the main screen");
        }
        uint fpid; GetWindowThreadProcessId(GetForegroundWindow(), out fpid);
        if (fpid == (uint)Process.GetCurrentProcess().Id) Add("a window of the tests took the focus");
        Looks++;
        Thread.Sleep(50);
      }
    }

    void Add(string v) { lock (violations) if (!violations.Contains(v)) violations.Add(v); }
  }

  // ------------------------------------------------------------------------------------------------ bench
  // nvidia-smi once a second while something runs.
  class Smi {
    readonly List<int[]> samples = new List<int[]>();
    Process p;
    public void Start() {
      p = Proc.Start("nvidia-smi", "--query-gpu=utilization.gpu,utilization.encoder --format=csv,noheader,nounits -l 1", false);
      p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        var parts = e.Data.Split(',');
        int a, b;
        if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out a) && int.TryParse(parts[1].Trim(), out b)) lock (samples) samples.Add(new[] { a, b });
      };
      p.BeginOutputReadLine(); p.BeginErrorReadLine();
    }
    public List<int[]> Take() { lock (samples) { var r = new List<int[]>(samples); samples.Clear(); return r; } }
    public void Stop() { try { if (!p.HasExited) p.Kill(); p.WaitForExit(3000); } catch (Exception) { } }
    public static string Summary(List<int[]> s) {
      if (s.Count == 0) return "no samples";
      double g = 0, e = 0; int gm = 0, em = 0;
      foreach (var x in s) { g += x[0]; e += x[1]; gm = Math.Max(gm, x[0]); em = Math.Max(em, x[1]); }
      return "GPU " + (g / s.Count).ToString("0.0") + "% avg / " + gm + "% max, encoder " + (e / s.Count).ToString("0.0") + "% avg / " + em + "% max (" + s.Count + " samples)";
    }
  }

  static int Bench(string[] a) {
    var scene = (Scene)Enum.Parse(typeof(Scene), a[1]);
    int w = int.Parse(a[2]), h = int.Parse(a[3]), secs = int.Parse(a[4]);
    Directory.CreateDirectory(Path.Combine(build, "bench"));
    var win = TestWindow.Open(scene, w, h, false, WindowPrefix + ": bench " + scene);
    lock (TestWindow.Placed) foreach (var placed in TestWindow.Placed) Console.WriteLine("PLACED " + placed);
    var smi = new Smi(); smi.Start();
    long bd0 = win.Drawn, bl0 = win.Late;
    var bsw = Stopwatch.StartNew();
    Thread.Sleep(6000);
    double bwall = bsw.Elapsed.TotalSeconds;
    Console.WriteLine("BASELINE (window drawing, not recording): " + Smi.Summary(smi.Take()));
    Console.WriteLine("BASELINE the test window drew " + (win.Drawn - bd0) + " frames in " + bwall.ToString("0.0") + " s (" + ((win.Drawn - bd0) / bwall).ToString("0.0") + "/s), " + (win.Late - bl0) + " late (>8 ms), render " + (win.RenderTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, win.Drawn)).ToString("0.00") + " ms, present " + (win.PresentTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, win.Drawn)).ToString("0.00") + " ms");
    var enc = EncoderChoice.Detect(Ffmpeg.Exe);
    string name = "bench-" + scene + "-" + w + "x" + h;
    var rec = new Recording(enc, win.Handle, w, h, Path.Combine(build, "bench"), name);
    long d0 = win.Drawn, l0 = win.Late;
    string err;
    if (!rec.Start(out err, 15000)) { Console.WriteLine("START FAILED " + err); smi.Stop(); win.CloseWindow(); return 1; }
    Console.WriteLine("ffmpeg " + rec.Command);
    smi.Take();
    var sw = Stopwatch.StartNew();
    Thread.Sleep(secs * 1000);
    double wall = sw.Elapsed.TotalSeconds;
    var during = smi.Take();
    TimeSpan cpu = rec.Cpu;
    long drawn = win.Drawn - d0, late = win.Late - l0;
    var res = rec.Stop();
    smi.Stop();
    win.CloseWindow();
    if (!res.Ok) { Console.WriteLine("STOP FAILED " + res.Message); return 1; }
    var p = ProbeResult.Run(res.Mp4, true);
    double mb = res.Bytes / 1048576.0;
    Console.WriteLine("RESULT scene=" + scene + " " + w + "x" + h + " encoder=" + enc.Name + " recorded=" + p.Duration.ToString("0.00") + " s frames=" + p.Frames + " r_frame_rate=" + p.FrameRate + " dup=" + res.Dup + " drop=" + res.Drop);
    Console.WriteLine("RESULT size=" + mb.ToString("0.00") + " MB  =>  " + (mb / p.Duration * 60).ToString("0.0") + " MB per minute  (" + (res.Bytes * 8 / p.Duration / 1e6).ToString("0.0") + " Mb/s average; ceiling " + FfmpegArgs.MaxrateKbps(w, h) / 1000.0 + " Mb/s)");
    Console.WriteLine("RESULT while recording: " + Smi.Summary(during));
    Console.WriteLine("RESULT ffmpeg CPU " + cpu.TotalSeconds.ToString("0.00") + " s over " + wall.ToString("0.0") + " s = " + (100 * cpu.TotalSeconds / wall).ToString("0.0") + "% of one core, " + (100 * cpu.TotalSeconds / wall / Environment.ProcessorCount).ToString("0.00") + "% of the " + Environment.ProcessorCount + "-thread CPU");
    Console.WriteLine("RESULT test window cost per frame: render " + (win.RenderTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, win.Drawn)).ToString("0.00") + " ms, present " + (win.PresentTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, win.Drawn)).ToString("0.00") + " ms");
    Console.WriteLine("RESULT test window drew " + drawn + " frames in " + wall.ToString("0.0") + " s (" + (drawn / wall).ToString("0.0") + "/s) with " + late + " late (>8 ms) while being recorded");
    string so, se;
    string png = Path.Combine(build, "bench", name + ".png");
    Proc.Run(Ffmpeg.Exe, "-hide_banner -loglevel error -nostdin -y -ss " + (secs / 2) + " -i " + FfmpegArgs.Quote(res.Mp4) + " -frames:v 1 " + FfmpegArgs.Quote(png), 30000, out so, out se);
    Console.WriteLine("RESULT still frame " + png);
    return 0;
  }
}
