// SPDX-License-Identifier: GPL-2.0-or-later
// Tests for Bifrost Recorder: run build\RecorderTests.exe after build.ps1 (or build.ps1 -Test).
//   no arguments   the unit tests, the engine (libobs), the end-to-end tests, then the window tests
//   --unit         the unit tests only (no GPU, no windows: safe while a game runs)
//   --ui           the window tests only (no capture, no encoder)
//   --engine       only start libobs, list what it registered, and stop it
//   --diag [windowed|fullscreen] [seconds]   game capture against window capture on FakeGame at a GPU-bound 60 fps (Diag.cs)
// Only windows these tests create are ever recorded or pictured: their own test windows (nearly invisible, alpha 1/255, click-through)
// and FakeGame.exe (a small Direct3D 11 window that game capture hooks: layered at alpha 1/255, click-through, never focused, no taskbar button). The tests never record the screen, the
// PC's sound or a microphone: the sound is the test process's own (silent), and the mic track is made from a device that doesn't exist.
// Everything the tests make is in build\test-output\ (cleared at the start); the screenshots kept for the Dev timeline go to docs\screenshots\m2\.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class RecorderTests {
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  static int passed, failed;
  static string project, build, outDir, shotsDir, throwDir;
  static Watcher watcher;
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
    shotsDir = Path.Combine(project, "docs", "screenshots", "m2");   // the pictures kept for the Dev timeline
    throwDir = Path.Combine(outDir, "shots");                          // the other pictures the tests take, not kept
    TestWindow.LayerAlpha = 1;
    Recording.MicDevice = "bifrost-test-no-such-microphone";        // the three-track recordings open no real microphone

    if (args.Length > 0 && args[0] == "--job-child") return JobChild();   // started by a run that already passed the guard; CPU only
    if (args.Length > 0 && args[0] == "--window") return WindowChild(args);   // one test window for a run that already passed the guard; CPU only
    if (args.Length > 0 && args[0] == "--crash-child") return CrashChild(args);   // started by a run that already passed the guard and holds the lock; records, then ends itself outright
    if (args.Length > 0 && args[0] == "--guard-check") { Console.WriteLine("game running: " + (RunningGame() ?? "none")); return 0; }   // reads the process list only; nothing else
    if (args.Length > 0 && args[0] == "--guard-test") { bool go = Guard(); Console.WriteLine("guard says: " + (go ? "go" : "refuse")); return go ? 0 : 2; }   // the guard and nothing after it
    if (args.Length > 0 && args[0] == "--unit") return UnitOnly();   // no GPU, no windows: safe while a game runs
    if (!Guard()) return 2;
    GpuSafety.Begin();
    MainForm.KeepEngine = true;   // one engine for the whole run, started on this thread and shut down at the end
    if (args.Length > 0 && args[0] == "--ui") return UiOnly();      // the Recorder's windows (off every monitor), no encoder started: guarded like the GPU modes since a window could
    if (args.Length > 0 && args[0] == "--diag") { int code = Diag.Run(args, build); Obs.Shutdown(); return code; }
    if (args.Length > 0 && args[0] == "--hold") return Hold(args);
    if (args.Length > 0 && args[0] == "--engine") { EngineTest(); Obs.Shutdown(); GpuSafety.Check("the engine start"); Console.WriteLine(passed + " passed, " + failed + " failed (engine only)"); return failed == 0 ? 0 : 1; }

    ClearOutDir();
    Directory.CreateDirectory(outDir);
    Directory.CreateDirectory(shotsDir); Directory.CreateDirectory(throwDir);
    Console.WriteLine("Counts before: " + Counts());
    var watch = new Watcher(); watch.Start(); watcher = watch;
    Diag.Game.Started = delegate(int pid) { watch.Allow(pid); };   // FakeGame's window is a visible top-level window (alpha 1/255): the watcher checks it as a test window instead (Silent)
    try {
      Units(true);
      GpuSafety.Check("the unit tests");
      EngineTest();
      GpuSafety.Check("the engine start");
      EndToEnd(watch);
      GpuSafety.Check("the end-to-end tests");
      UiTests(watch);
      GpuSafety.Check("the window tests");
    } catch (Exception ex) { Check("the run finished", false, ex.ToString()); }
    var down = Stopwatch.StartNew();
    Obs.Shutdown();
    Check("the engine shuts down on its own thread (" + down.ElapsedMilliseconds + " ms)", !Obs.Started && down.ElapsedMilliseconds < 10000);
    var violations = watch.Stop();
    lock (TestWindow.Placed) foreach (var p in TestWindow.Placed) Note("placed: " + p);
    string wantScreen = TestWindow.OnSecondary ? "(secondary)" : "(primary)";
    bool allPlaced = true;
    lock (TestWindow.Placed) foreach (var p in TestWindow.Placed) if (!p.Contains(wantScreen)) allPlaced = false;
    Check("every test window opened fully inside the " + (TestWindow.OnSecondary ? "secondary" : "primary") + " monitor, as asked", TestWindow.Placed.Count > 0 && allPlaced, TestWindow.Placed.Count + " windows");
    Check("no window of a child process was ever visible (FakeGame's own is held to the test windows' rules: alpha 1/255, click-through, no-activate, never topmost or in front), and the Recorder's own window never touched a monitor", violations.Count == 0 && watch.Looks > 20,
      watch.Looks + " looks; " + string.Join("; ", violations.ToArray()));
    int alive = Proc.StillRunning();
    Check("no process the tests started is still running", alive == 0, alive + " alive of " + Proc.Started.Count);
    Console.WriteLine("Counts after:  " + Counts());
    Console.WriteLine();
    Console.WriteLine(passed + " passed, " + failed + " failed");
    return failed == 0 ? 0 : 1;
  }

  // ------------------------------------------------------------------------------------------------ the guard
  // Everything here uses the GPU (window capture and NVENC), so none of it runs next to a game, and two runs never overlap.
  // Process names that start with one of these count as a game.
  static readonly string[] GamePrefixes = { "ArmaReforger", "arma3", "DayZ", "Medal" };   // Medal: its encoder and game hook spoil the measurements   // ArmaReforgerSteam, ...SteamDiag, ...WorkbenchSteamDiag
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
    // a game that starts during the run ends it: exiting ends libobs with the process, and closes the job, which ends FakeGame and ffprobe
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
  // The test output folder emptied before a run. A file made by the last run may still be held open for a moment by something else on the PC
  // (a virus scanner or the search indexer looking at a fresh .mp4), so deleting is tried again for up to 20 s before the run gives up.
  static void ClearOutDir() {
    for (int tries = 0; ; tries++) {
      try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); return; }
      catch (IOException) { if (tries >= 40) throw; Thread.Sleep(500); }
      catch (UnauthorizedAccessException) { if (tries >= 40) throw; Thread.Sleep(500); }
    }
  }

  // --unit: only the checks that need neither the GPU nor a window (so they may run next to a game): no test encode, no capture.
  static int UnitOnly() {
    ClearOutDir();
    Directory.CreateDirectory(outDir);
    Console.WriteLine("Counts before: " + Counts());
    try { Units(false); } catch (Exception ex) { Check("the run finished", false, ex.ToString()); }
    int alive = Proc.StillRunning();
    Check("no process the tests started is still running", alive == 0, alive + " alive of " + Proc.Started.Count);
    Console.WriteLine("Counts after:  " + Counts());
    Console.WriteLine();
    Console.WriteLine(passed + " passed, " + failed + " failed (unit tests only)");
    return failed == 0 ? 0 : 1;
  }

  static void Units(bool gpu) {
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

    UnitsEncoders();

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

    UnitsSettings(tmp);
    UnitsSources(tmp);
    UnitsCaptureChoice();
    UnitsArmory(tmp);
    UnitsHotkey(tmp);
    UnitsPrefs(tmp);
    UnitsTheme();
    GpuPriorityApiTest();
    JobTest();
  }

  // ------------------------------------------------------------------------------------------------ the encoders and what libobs is told
  static string Names(List<EncoderProfile> l) { var n = new List<string>(); foreach (var e in l) n.Add(e.Name); return string.Join(",", n.ToArray()); }
  static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) ? v as string : null; }
  static long Int(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null && !(v is string) && !(v is bool) ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : -1; }
  static bool Bool(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v is bool && (bool)v; }
  static bool Has(Dictionary<string, object> d, string k) { return d.ContainsKey(k); }
  static string Dump(Dictionary<string, object> d) { var p = new List<string>(); foreach (var kv in d) p.Add(kv.Key + "=" + Convert.ToString(kv.Value, CultureInfo.InvariantCulture)); return string.Join(" ", p.ToArray()); }

  static void UnitsEncoders() {
    var all = new List<string> { "obs_nvenc_hevc_tex", "h265_texture_amf", "obs_qsv11_hevc", "obs_x264", "ffmpeg_aac" };
    Check("fallback from NVENC: NVENC, AMF, QSV (the ones libobs registered), then x264", Names(EncoderProfile.FallbackChain(EncoderProfile.Nvenc, all)) == "obs_nvenc_hevc_tex,h265_texture_amf,obs_qsv11_hevc,obs_x264", Names(EncoderProfile.FallbackChain(EncoderProfile.Nvenc, all)));
    Check("fallback skips the encoders libobs didn't register (their own GPU test failed)", Names(EncoderProfile.FallbackChain(EncoderProfile.Nvenc, new List<string> { "obs_nvenc_hevc_tex", "obs_x264" })) == "obs_nvenc_hevc_tex,obs_x264");
    Check("fallback from QSV: QSV then x264 (never back to the ones before it)", Names(EncoderProfile.FallbackChain(EncoderProfile.Qsv, all)) == "obs_qsv11_hevc,obs_x264");
    Check("fallback from the software encoder is only itself", Names(EncoderProfile.FallbackChain(EncoderProfile.Software, all)) == "obs_x264");
    Check("every encoder is known by libobs's id", EncoderProfile.Find("obs_nvenc_hevc_tex") == EncoderProfile.Nvenc && EncoderProfile.Find("h265_texture_amf") == EncoderProfile.Amf && EncoderProfile.Find("obs_qsv11_hevc") == EncoderProfile.Qsv && EncoderProfile.Find("obs_x264") == EncoderProfile.Software && EncoderProfile.Find("hevc_nvenc") == null);
    Check("only NVENC (and x264) are marked as run on hardware here", EncoderProfile.Nvenc.Verified && EncoderProfile.Software.Verified && !EncoderProfile.Amf.Verified && !EncoderProfile.Qsv.Verified);

    Check("ceiling 30000 kb/s at 1920x1080", Sizes.MaxrateKbps(1920, 1080) == 30000, Sizes.MaxrateKbps(1920, 1080).ToString());
    Check("ceiling scales by pixels (2560x1440 = 53300)", Sizes.MaxrateKbps(2560, 1440) == 53300, Sizes.MaxrateKbps(2560, 1440).ToString());
    Check("ceiling at 3840x2160 = 120000", Sizes.MaxrateKbps(3840, 2160) == 120000);
    Check("ceiling never under 5000", Sizes.MaxrateKbps(640, 360) == 5000);

    var light = EncoderProfile.Nvenc.Settings(30000, 24, false);
    Check("NVENC light (the default): CQVBR at quality 24 with a 30000 kb/s ceiling, HEVC Main, preset p4, tune hq, one pass, no B-frames, no spatial AQ, no lookahead, 2 s keyframes",
      Str(light, "rate_control") == "CQVBR" && Int(light, "target_quality") == 24 && Int(light, "max_bitrate") == 30000 && Str(light, "profile") == "main" && Str(light, "preset") == "p4" && Str(light, "tune") == "hq" &&
      Str(light, "multipass") == "disabled" && Int(light, "bf") == 0 && Has(light, "adaptive_quantization") && !Bool(light, "adaptive_quantization") && Has(light, "lookahead") && !Bool(light, "lookahead") && Int(light, "keyint_sec") == 2, Dump(light));
    var heavy = EncoderProfile.Nvenc.Settings(30000, 24, true);
    Check("NVENC quality: preset p5, quarter-resolution second pass, 2 B-frames, spatial AQ; same quality and ceiling",
      Str(heavy, "preset") == "p5" && Str(heavy, "multipass") == "qres" && Int(heavy, "bf") == 2 && Bool(heavy, "adaptive_quantization") && Int(heavy, "target_quality") == 24 && Int(heavy, "max_bitrate") == 30000, Dump(heavy));
    var amf = EncoderProfile.Amf.Settings(30000, 24, false);
    Check("AMF (untested): QVBR at level 24 (AMF's cqp value), HEVC Main, no B-frames, 2 s keyframes", Str(amf, "rate_control") == "QVBR" && Int(amf, "cqp") == 24 && Str(amf, "profile") == "main" && Int(amf, "bf") == 0 && Int(amf, "keyint_sec") == 2, Dump(amf));
    var qsv = EncoderProfile.Qsv.Settings(30000, 24, false);
    Check("QSV (untested): ICQ 24, HEVC Main, target usage 4, no B-frames, 2 s keyframes", Str(qsv, "rate_control") == "ICQ" && Int(qsv, "icq_quality") == 24 && Str(qsv, "profile") == "main" && Str(qsv, "target_usage") == "TU4" && Int(qsv, "bframes") == 0 && Int(qsv, "keyint_sec") == 2, Dump(qsv));
    var x = EncoderProfile.Software.Settings(30000, 24, false);
    Check("x264: CRF 24, veryfast, High profile, 2 s keyframes", Str(x, "rate_control") == "CRF" && Int(x, "crf") == 24 && Str(x, "preset") == "veryfast" && Str(x, "profile") == "high" && Int(x, "keyint_sec") == 2, Dump(x));
  }

  // ------------------------------------------------------------------------------------------------ settings, sizes, chip, telemetry, log
  static WindowInfo Win(string title) { return new WindowInfo { Handle = IntPtr.Zero, Title = title, ClassName = "TestClass", ExeName = "Test.exe" }; }

  static void UnitsSettings(string tmp) {
    // the switches in BifrostRecorder.settings.json
    string sw = Path.Combine(tmp, "switches.json");
    File.WriteAllText(sw, "{\"ManagedBy\":\"Bifrost Suite\",\"DataFolder\":\"D:\\\\Suite\\\\Bifrost Recorder\",\"EncoderProfile\":\"gentle\",\"MaxHeight\":1080,\"GpuPriority\":\"normal\"}");
    var c = Settings.Load(sw);
    Check("switches are read: gentle (the 0.1.1 name) means standard, 1080, normal, and DataFolder beside them", c.EncoderProfile == "standard" && !c.Quality && c.MaxHeight == 1080 && c.GpuPriority == "normal" && c.DataFolder == @"D:\Suite\Bifrost Recorder", c.EncoderProfile + " " + c.MaxHeight + " " + c.GpuPriority);
    var d = Settings.Load(Path.Combine(tmp, "absent.json"));
    Check("no settings file: standard, native size, game capture, the program's sound, no mic, GPU normal, no DataFolder", !d.Quality && d.EncoderProfile == "standard" && d.MaxHeight == 0 && d.CaptureMethod == "game" && d.Sound == "game" && d.Mic == "off" && d.GpuPriority == "normal" && d.DataFolder == null);
    File.WriteAllText(sw, "{\"EncoderProfile\":\"turbo\",\"MaxHeight\":\"1080\",\"GpuPriority\":\"max\"}");
    c = Settings.Load(sw);
    Check("unusable switch values fall back to the defaults", c.EncoderProfile == "standard" && c.MaxHeight == 0 && c.GpuPriority == "normal");
    File.WriteAllText(sw, "{\"GpuPriority\":\" High \"}"); Check("GpuPriority high is read (ignoring case and spaces)", Settings.Load(sw).GpuPriority == "high");
    File.WriteAllText(sw, "{\"EncoderProfile\":\" QUALITY \",\"MaxHeight\":720,\"GpuPriority\":\"Normal\"}");
    c = Settings.Load(sw);
    Check("switch values ignore case and spaces; quality; MaxHeight 720", c.Quality && c.MaxHeight == 720 && c.GpuPriority == "normal");
    File.WriteAllText(sw, "{\"MaxHeight\":100}"); Check("a MaxHeight below 144 means native", Settings.Load(sw).MaxHeight == 0);
    File.WriteAllText(sw, "{\"MaxHeight\":0}"); Check("MaxHeight 0 means native", Settings.Load(sw).MaxHeight == 0);
    File.WriteAllText(sw, "{\"MaxHeight\":true}"); Check("MaxHeight true is not a number", Settings.Load(sw).MaxHeight == 0);
    File.WriteAllText(sw, "{\"EncoderProfile\":\"standard\",\"MaxHeight\":0,\"GpuPriority\":\"high\"}");
    Check("a settings file without DataFolder still saves to %USERPROFILE%\\Videos\\Bifrost Recorder", Settings.RecordingsFolder(sw, @"C:\Users\Someone") == @"C:\Users\Someone\Videos\Bifrost Recorder");
    Check("logs: %LOCALAPPDATA%\\Bifrost Recorder\\logs without a DataFolder", Settings.LogsFolder(sw, @"C:\Users\Someone\AppData\Local") == @"C:\Users\Someone\AppData\Local\Bifrost Recorder\logs");
    File.WriteAllText(sw, "{\"DataFolder\":\"D:\\\\Suite\\\\Bifrost Recorder\"}");
    Check("logs: <DataFolder>\\logs, and never the recordings folder", Settings.LogsFolder(sw, @"C:\L") == @"D:\Suite\Bifrost Recorder\logs" && Settings.LogsFolder(sw, @"C:\L") != Settings.RecordingsFolder(sw, @"C:\U") && !Settings.LogsFolder(sw, @"C:\L").StartsWith(Settings.RecordingsFolder(sw, @"C:\U")));
    File.WriteAllText(sw, "{\"FrameRateMode\":\"vfr\",\"EncoderCheck\":\"startup\",\"CaptureMethod\":\"window\"}");
    c = Settings.Load(sw);
    Check("keys of older versions (FrameRateMode, EncoderCheck) are ignored and the rest is read", c.CaptureMethod == "window");

    // the video's size: MaxHeight scales on the GPU inside libobs, keeping the shape
    int w, h; bool scaled;
    Sizes.OutputSize(2560, 1440, 1080, out w, out h, out scaled); Check("MaxHeight 1080: 2560x1440 becomes 1920x1080", w == 1920 && h == 1080 && scaled);
    Sizes.OutputSize(2560, 1440, 720, out w, out h, out scaled); Check("MaxHeight 720: 2560x1440 becomes 1280x720", w == 1280 && h == 720 && scaled);
    Sizes.OutputSize(3440, 1440, 1080, out w, out h, out scaled); Check("MaxHeight 1080: 3440x1440 ultrawide becomes 2580x1080 (shape kept, even)", w == 2580 && h == 1080 && scaled, w + "x" + h);
    Sizes.OutputSize(1920, 1080, 1080, out w, out h, out scaled); Check("a window already at MaxHeight is not scaled", w == 1920 && h == 1080 && !scaled);
    Sizes.OutputSize(1280, 720, 1080, out w, out h, out scaled); Check("a window smaller than MaxHeight is never scaled up", w == 1280 && h == 720 && !scaled);
    Sizes.OutputSize(1279, 719, 0, out w, out h, out scaled); Check("native size is the client area rounded down to even", w == 1278 && h == 718 && !scaled);
    Sizes.OutputSize(1281, 1441, 1080, out w, out h, out scaled); Check("odd sizes scale to even ones", w % 2 == 0 && h % 2 == 0 && h == 1080 && scaled, w + "x" + h);
    var plan = new Recording(null, Win("plan"), 2560, 1440, tmp, "plan", new AppSettings { MaxHeight = 720 }, null);
    Check("a Recording's canvas is the window's client area and its video follows MaxHeight", plan.BaseWidth == 2560 && plan.BaseHeight == 1440 && plan.OutWidth == 1280 && plan.OutHeight == 720, plan.BaseWidth + "x" + plan.BaseHeight + " -> " + plan.OutWidth + "x" + plan.OutHeight);
    var odd = new Recording(null, Win("odd"), 1279, 719, tmp, "odd", new AppSettings(), null);
    Check("an odd client area gives an even canvas", odd.BaseWidth == 1278 && odd.BaseHeight == 718 && odd.OutWidth == 1278 && odd.OutHeight == 718);
    var monitor = new DxgiOutput { Adapter = 0, Output = 0, AdapterName = "NVIDIA GeForce RTX 3070 Ti", VendorId = 0x10DE, DeviceName = @"\\.\DISPLAY1", Desktop = new Rectangle(0, 0, 2560, 1440), Monitor = new IntPtr(1001) };
    var scr = new Recording(null, Win("screen"), 1280, 720, tmp, "screen", new AppSettings { CaptureMethod = "screen", MaxHeight = 1080 }, null, monitor);
    Check("screen capture: the canvas is the monitor, and MaxHeight now scales it too (libobs scales on the GPU)", scr.BaseWidth == 2560 && scr.BaseHeight == 1440 && scr.OutWidth == 1920 && scr.OutHeight == 1080, scr.BaseWidth + "x" + scr.BaseHeight + " -> " + scr.OutWidth + "x" + scr.OutHeight);

    // the header chip
    Check("chip: the defaults", MainForm.ChipText(EncoderProfile.Nvenc, new AppSettings()) == "HEVC · NVENC · light · native · game capture · GPU normal", MainForm.ChipText(EncoderProfile.Nvenc, new AppSettings()));
    Check("chip: quality, 1080p, window, mic, GPU high", MainForm.ChipText(EncoderProfile.Nvenc, new AppSettings { EncoderProfile = "quality", MaxHeight = 1080, CaptureMethod = "window", Mic = "on", GpuPriority = "high" }) == "HEVC · NVENC · quality · 1080p · window · mic · GPU high");
    Check("chip: screen", MainForm.ChipText(EncoderProfile.Nvenc, new AppSettings { CaptureMethod = "screen" }) == "HEVC · NVENC · light · native · screen · GPU normal");
    Check("chip: the software encoder is H.264", MainForm.ChipText(EncoderProfile.Software, new AppSettings()).StartsWith("H.264 · x264 CPU · light"));

    // GPU priority names and status formatting
    Check("GPU priority classes are named (4 HIGH, 3 ABOVE_NORMAL, 2 NORMAL)", GpuScheduling.Name(4) == "HIGH" && GpuScheduling.Name(3) == "ABOVE_NORMAL" && GpuScheduling.Name(2) == "NORMAL" && GpuScheduling.Name(5) == "REALTIME");
    Check("NTSTATUS values print in hex", GpuScheduling.Hex(0) == "0x00000000" && GpuScheduling.Hex(unchecked((int)0xC0000022)) == "0xC0000022");
    Check("REALTIME is never one of the classes tried (High=4, AboveNormal=3)", GpuScheduling.High == 4 && GpuScheduling.AboveNormal == 3 && GpuScheduling.High < 5);

    // telemetry: libobs's counters, and when the window says Keeping up, Falling behind or Waiting
    var keep = Sim(0, 0, 12.0, true);
    Check("telemetry: no late or skipped frames is Keeping up", !keep.Behind && keep.Text == "Keeping up", keep.Text);
    var late = Sim(6, 0, 12.0, true);
    Check("telemetry: 10% of frames drawn late for 12 s is Falling behind, blamed on the GPU", late.Behind && late.Text == "Falling behind: 10% of frames late (the GPU is busy)", late.Text);
    var skip = Sim(0, 6, 12.0, true);
    Check("telemetry: 10% skipped by the encoder is blamed on the encoder", skip.Behind && skip.Text == "Falling behind: 10% of frames late (the encoder is busy)", skip.Text);
    var early = Sim(6, 0, 4.6, true);
    Check("telemetry: late for under 3 s past the first measurement is not yet 'behind'", !early.Behind, early.Text);
    var under = Sim(2.4, 0, 12.0, true);
    Check("telemetry: 4% late is still Keeping up (the line is 5%)", !under.Behind && under.Text == "Keeping up", under.Text + " " + under.MissedShare.ToString("0.000"));
    var over = Sim(3.6, 0, 12.0, true);
    Check("telemetry: 6% late counts as behind", over.Behind, over.Text + " " + over.MissedShare.ToString("0.000"));
    var recover = Sim(6, 0, 16.0, true, 8.0);
    Check("telemetry: back to no late frames for a few seconds clears 'behind'", !recover.Behind && recover.Text == "Keeping up", recover.Text);
    var waiting = Sim(6, 0, 12.0, false);
    Check("telemetry: a capture with no picture (game not hooked, window minimized) is 'Waiting for the picture', never behind", waiting.NoPicture && !waiting.Behind && waiting.Text == "Waiting for the picture", waiting.Text);
    string line = keep.Line(12.0);
    Check("telemetry: the log line has the frames, late and skipped counts, the share late and the state", line.Contains("frames=720") && line.Contains("skipped=0") && line.Contains("lagged=0") && line.Contains("late=0.0%") && line.EndsWith("[ok]"), line);

    // the log: where, what, and the newest 20
    string logs = Path.Combine(tmp, "logs"); Directory.CreateDirectory(logs);
    for (int i = 0; i < 25; i++) { string f = Path.Combine(logs, "old " + i.ToString("00") + ".log"); File.WriteAllText(f, "x"); File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-100 + i)); }
    var lg = new RecordingLog(logs, "new one");
    lg.Line("hello"); lg.Close();
    var left = new List<string>(Directory.GetFiles(logs, "*.log"));
    Check("the log keeps the newest 20 (25 old + 1 new gives 20; the oldest are gone)", left.Count == 20 && File.Exists(Path.Combine(logs, "new one.log")) && !File.Exists(Path.Combine(logs, "old 00.log")) && File.Exists(Path.Combine(logs, "old 24.log")), left.Count.ToString());
    string text = File.ReadAllText(Path.Combine(logs, "new one.log"));
    Check("a log line carries a clock time and the seconds since the log began", System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d\d:\d\d:\d\d\.\d{3}  \+\d+\.\d{3}s  hello"), text.Trim());
    var none = new RecordingLog(null, "x"); none.Line("no folder, no problem"); none.Close();
    Check("no log folder: logging is simply off", none.Path == null);
  }

  // A pretend recording: every 0.2 s the clock moves, 12 frames are written, and lagPerSec / skipPerSec frames are late or skipped (until
  // cleanAfter seconds, when given); picture false is a capture that sends nothing.
  static Telemetry Sim(double lagPerSec, double skipPerSec, double seconds, bool picture, double cleanAfter = double.MaxValue) {
    var t = new Telemetry();
    long frames = 0; double lag = 0, skip = 0;
    for (double now = 0.2; now <= seconds + 1e-9; now += 0.2) {
      frames += 12;
      if (now < cleanAfter) { lag += lagPerSec * 0.2; skip += skipPerSec * 0.2; }
      t.Feed(now, frames, (long)skip, (long)lag, frames * 1000, picture);
    }
    return t;
  }

  // ------------------------------------------------------------------------------------------------ what is captured and which sound goes where
  [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();

  static string Decode(string s) { return s.Replace("#3A", ":").Replace("#22", "#"); }   // libobs's decode_str, for the round trip

  static void UnitsSources(string tmp) {
    string sw = Path.Combine(tmp, "switches2.json");
    File.WriteAllText(sw, "{\"CaptureMethod\":\"screen\",\"Sound\":\"pc\",\"Mic\":\"on\"}");
    var c = Settings.Load(sw);
    Check("switches: CaptureMethod screen, Sound pc and Mic on are read", c.Screen && c.CaptureMethod == "screen" && c.Sound == "pc" && c.WithMic);
    File.WriteAllText(sw, "{\"CaptureMethod\":\" Window \",\"Sound\":\" GAME \",\"Mic\":\"OFF\"}");
    c = Settings.Load(sw); Check("switches: ignore case and spaces", c.CaptureMethod == "window" && c.Sound == "game" && !c.WithMic);
    File.WriteAllText(sw, "{\"CaptureMethod\":\"monitor\",\"Sound\":\"loud\",\"Mic\":\"yes\"}");
    c = Settings.Load(sw); Check("switches: unusable values mean game capture, the program's sound, no mic", c.CaptureMethod == "game" && c.Sound == "game" && !c.WithMic);

    Check("libobs's sources: game_capture, window_capture, monitor_capture", Sources.Id("game") == "game_capture" && Sources.Id("window") == "window_capture" && Sources.Id("screen") == "monitor_capture");
    Check("a window as OBS names it: title:class:program.exe", Sources.WindowSpec("Arma Reforger", "ARMA Reforger", "ArmaReforgerSteam.exe") == "Arma Reforger:ARMA Reforger:ArmaReforgerSteam.exe");
    Check("colons and hashes in a part are written #3A and #22", Sources.WindowSpec("a:b#c", "C:1", "x.exe") == "a#3Ab#22c:C#3A1:x.exe", Sources.WindowSpec("a:b#c", "C:1", "x.exe"));
    bool round = true;
    foreach (string s in new[] { "#3A", "a::b", "##", "#22:#3A", "Game: The Sequel #2", "" }) if (Decode(Sources.WindowSpec(s, "c", "e.exe").Split(':')[0]) != s) round = false;
    Check("every title comes back unchanged through libobs's decoding (#3A and #22 in a title included)", round);

    var game = Sources.VideoSettings("game", "T:C:g.exe", null);
    Check("game capture: this window (matched by its program first), the cursor drawn, OBS's anti-cheat compatibility hook", Str(game, "capture_mode") == "window" && Str(game, "window") == "T:C:g.exe" && Int(game, "priority") == 2 && Bool(game, "capture_cursor") && Bool(game, "anti_cheat_hook"), Dump(game));
    var win = Sources.VideoSettings("window", "T:C:g.exe", null);
    Check("window capture: Windows Graphics Capture (method 2), the client area, the cursor drawn, SDR pictures from Windows", Str(win, "window") == "T:C:g.exe" && Int(win, "method") == 2 && Bool(win, "client_area") && Bool(win, "cursor") && Int(win, "priority") == 2 && Bool(win, "force_sdr"), Dump(win));
    var scr = Sources.VideoSettings("screen", "T:C:g.exe", @"\\.\DISPLAY2");
    Check("screen capture: the monitor by its device name, the cursor drawn, libobs picks the method, SDR pictures from Windows", Str(scr, "monitor_id") == @"\\.\DISPLAY2" && Bool(scr, "capture_cursor") && Int(scr, "method") == 0 && Bool(scr, "force_sdr") && !Has(scr, "window"), Dump(scr));
    Check("game capture is left to the game's own colours (libobs tone-maps an HDR game at OBS's levels, set at start)", !Has(game, "force_sdr"));

    var plain = new AppSettings();
    Check("tracks: the program's sound alone is one track, Game", string.Join(",", Sources.TrackNames(plain)) == "Game" && Sources.SoundMixers(plain) == 1 && Sources.MicMixers(plain) == 0);
    Check("tracks: everything the PC plays alone is one track, PC sound", string.Join(",", Sources.TrackNames(new AppSettings { Sound = "pc" })) == "PC sound");
    var mic = new AppSettings { Mic = "on" };
    Check("tracks: with the mic, Mix, Game and Mic; the game feeds Mix and Game, the mic feeds Mix and Mic", string.Join(",", Sources.TrackNames(mic)) == "Mix,Game,Mic" && Sources.SoundMixers(mic) == 3 && Sources.MicMixers(mic) == 5);
    Check("tracks: the PC's sound with the mic is Mix, PC sound, Mic", string.Join(",", Sources.TrackNames(new AppSettings { Sound = "pc", Mic = "on" })) == "Mix,PC sound,Mic");

    // the monitors as DXGI lists them (screen capture records one by its device name), matched by HMONITOR, never by Screen.AllScreens' order
    var outs = Dxgi.Outputs();
    foreach (var o in outs) Note("DXGI: " + o);
    Check("DXGI lists at least one output, each with a monitor handle, a size and a device name", outs.Count >= 1 && outs.TrueForAll(delegate(DxgiOutput o) { return o.Monitor != IntPtr.Zero && o.Width > 0 && o.Height > 0 && o.AdapterName.Length > 0 && o.DeviceName.StartsWith(@"\\.\DISPLAY"); }), outs.Count + " outputs");
    bool allMatch = true; string why = "";
    foreach (var s in Screen.AllScreens) {
      var o = Dxgi.ForPoint(s.Bounds.X + s.Bounds.Width / 2, s.Bounds.Y + s.Bounds.Height / 2);
      if (o == null || o.Desktop != s.Bounds || o.DeviceName != s.DeviceName) { allMatch = false; why += s.DeviceName + " -> " + (o == null ? "none" : o.ToString()) + "; "; }
    }
    Check("every monitor Windows has maps to the DXGI output with the same rectangle and device name (matched by HMONITOR)", allMatch, why);
    var primary = Dxgi.ForWindow(GetDesktopWindow());
    Check("a window on the primary monitor (the desktop window) maps to the primary monitor's output", primary != null && primary.Desktop == Screen.PrimaryScreen.Bounds, primary == null ? "none" : primary.ToString());

    // the chip before the engine has started: by the GPU's maker, with no GPU work
    var v = EncoderChoice.ByVendor();
    bool hasNvidia = false; foreach (var o in outs) if (o.VendorId == 0x10DE) hasNvidia = true;
    Check("before the engine starts the chip names the encoder for the GPU's maker (NVIDIA here: NVENC)", (hasNvidia ? v == EncoderProfile.Nvenc : v != null) && EncoderChoice.Picked == v, v.Name);
    EncoderChoice.Reset();
  }

  // ------------------------------------------------------------------------------------------------ a capture with no picture yet
  // The rules any CaptureChoice.WhenNoPicture must keep, whatever its timings: the start's overall limit is 15 s.
  static string FirstAnswer(string method, bool minimized, out int atMs) {
    for (atMs = 0; atMs <= 15000; atMs += 50) { string r = CaptureChoice.WhenNoPicture(method, atMs, minimized); if (r != null) return r; }
    atMs = -1; return null;
  }

  static void UnitsCaptureChoice() {
    var known = new List<string> { null, "fail", "game", "window", "screen" };
    bool valid = true; string bad = "";
    foreach (string m in new[] { "game", "window", "screen" })
      foreach (int ms in new[] { 0, 50, 500, 1000, 2000, 3000, 5000, 8000, 10000, 15000, 60000 })
        foreach (bool min in new[] { false, true }) {
          string r = CaptureChoice.WhenNoPicture(m, ms, min);
          if (!known.Contains(r)) { valid = false; bad += m + "/" + ms + "/" + min + " -> " + r + "; "; }
          if (r == "screen" && m != "screen") { valid = false; bad += m + " switched to screen capture (it would record the whole monitor without being asked); "; }
        }
    Check("no picture: the answer is always keep waiting, fail, or another capture, and never a switch to screen capture", valid, bad);
    Check("no picture at the very start (0 ms): every capture is given time", CaptureChoice.WhenNoPicture("game", 0, false) == null && CaptureChoice.WhenNoPicture("window", 0, false) == null && CaptureChoice.WhenNoPicture("screen", 0, false) == null);
    int at;
    string g = FirstAnswer("game", false, out at);
    Check("game capture that never hooks moves to window capture (what the Settings hint promises) within 15 s", g == "window", (g ?? "no answer") + " at " + at + " ms");
    string w = FirstAnswer("window", false, out at);
    Check("window capture that never sends a picture gives up within 15 s", w == "fail", (w ?? "no answer") + " at " + at + " ms");
    string s2 = FirstAnswer("screen", false, out at);
    Check("screen capture that never sends a picture gives up within 15 s", s2 == "fail", (s2 ?? "no answer") + " at " + at + " ms");
    string mg = FirstAnswer("game", true, out at);
    Check("a minimized window gets an answer within 15 s too", mg != null, (mg ?? "no answer") + " at " + at + " ms");
  }

  // ------------------------------------------------------------------------------------------------ the Armory upload client, against a stand-in on this PC
  // The key used here is made up by the test; nothing in the output may contain it, and the real site is never contacted.
  static void UnitsArmory(string tmp) {
    string key = "test-key-" + Guid.NewGuid().ToString("N");
    var leaks = new List<string>();      // every message and note the client produced: none may hold the key
    ArmoryClient.TestLoopbackHttp = true;
    try {
      // what counts as a usable server and storage address
      Check("server: https is fine", ArmoryClient.ServerProblem("https://taskforcehalberd.org") == null);
      Check("server: plain http only for this PC", ArmoryClient.ServerProblem("http://127.0.0.1:8080") == null && ArmoryClient.ServerProblem("http://localhost:1") == null && ArmoryClient.ServerProblem("http://taskforcehalberd.org") != null && ArmoryClient.ServerProblem("http://192.168.1.5") != null);
      Check("server: not a web address, empty, or with a user name is refused", ArmoryClient.ServerProblem("ftp://x") != null && ArmoryClient.ServerProblem("") != null && ArmoryClient.ServerProblem("not a url") != null && ArmoryClient.ServerProblem("https://user:pw@taskforcehalberd.org") != null);
      bool threw = false; try { new ArmoryClient("http://example.com", "k"); } catch (ArmoryException) { threw = true; }
      Check("a client for an http team server that is not on this PC cannot be made (the key would travel unprotected)", threw);
      threw = false; try { new ArmoryClient("https://taskforcehalberd.org", ""); } catch (ArmoryException) { threw = true; }
      Check("a client without a key cannot be made", threw);
      ArmoryClient.TestLoopbackHttp = false;
      Check("storage: signed addresses must be https (http on this PC only in the tests)", ArmoryClient.StorageProblem("https://videos.taskforcehalberd.org:9443/x?sig=1") == null && ArmoryClient.StorageProblem("http://127.0.0.1:1/x") != null && ArmoryClient.StorageProblem("http://videos.example.com/x") != null && ArmoryClient.StorageProblem("ftp://x/y") != null && ArmoryClient.StorageProblem("https://u:p@videos.example.com/x") != null);
      ArmoryClient.TestLoopbackHttp = true;
      Check("Usable: needs a key and a good server", ArmoryClient.Usable(new AppSettings { TeamServer = "https://taskforcehalberd.org", TeamKey = "k" }) && !ArmoryClient.Usable(new AppSettings { TeamServer = "https://taskforcehalberd.org" }) && !ArmoryClient.Usable(new AppSettings { TeamServer = "http://example.com", TeamKey = "k" }) && !ArmoryClient.Usable(new AppSettings()));
      var withKey = new AppSettings { TeamServer = "https://x", TeamKey = key };
      Check("the settings object never prints its key", !withKey.ToString().Contains(key));
      string sf = Path.Combine(tmp, "armory-settings.json");
      File.WriteAllText(sf, "{\"TeamServer\":\"https://taskforcehalberd.org/\",\"TeamKey\":\"" + key + "\",\"TeamMember\":\"Congo\",\"DataFolder\":\"D:\\\\S\\\\Rec\"}");
      var loaded = Settings.Load(sf);
      Check("TeamServer (no trailing slash) and TeamKey are read from the settings file", loaded.TeamServer == "https://taskforcehalberd.org" && loaded.TeamKey == key && loaded.DataFolder == @"D:\S\Rec");

      // a recording file to send: 5.5 MiB of recognisable bytes, in 1 MiB parts (6 parts)
      string file = Path.Combine(tmp, "upload-test.mp4");
      var data = new byte[5 * 1024 * 1024 + 512 * 1024]; new Random(3).NextBytes(data); File.WriteAllBytes(file, data);

      using (var s = new ArmoryStandIn(key)) {
        var client = new ArmoryClient(s.Url, key);
        var me = client.GetMe();
        Check("GET /me: connected, the size limit, the formats and the 60 days", me.Connected && me.MaxBytes == 20L * 1024 * 1024 * 1024 && me.Formats.Length == 2 && me.RetentionDays == 60 && me.Name == "Test Member");
        List<NamedItem> folders, cats; client.GetOptions(out folders, out cats);
        Check("GET /options: the folders and categories to pick from", folders.Count == 2 && folders[0].Id == "f1" && folders[0].Name == "Operations" && cats.Count == 2 && cats[1].Name == "Bug");

        // the happy path
        var job = new UploadJob { Client = client, File = file, FolderId = "f1", CategoryId = "c2", Title = "My clip", DurationSeconds = 123, RetryDelayMs = 30 };
        long lastSent = 0, lastTotal = 0; bool monotonic = true;
        job.Progress += delegate(long sent, long total) { lock (leaks) { if (sent < lastSent - (long)s.PartSize) monotonic = false; lastSent = sent; lastTotal = total; } };
        job.Note += delegate(string n) { lock (leaks) leaks.Add(n); };
        s.PutDelayMs = 150;
        var receipt = job.Run();
        s.PutDelayMs = 0;
        Check("upload: every part arrived with the right size and complete answered the receipt", receipt.ContainsKey("id") && s.ReceivedParts() == 6 && s.ReceivedBytes() == data.Length && s.Completes == 1, s.ReceivedParts() + " parts, " + s.ReceivedBytes() + " bytes");
        Check("upload: started once with the folder, category, file name, size, title, duration and accept_terms true", s.Starts == 1 && s.LastStartBody.Contains("\"folder_id\":\"f1\"") && s.LastStartBody.Contains("\"category_id\":\"c2\"") && s.LastStartBody.Contains("\"filename\":\"upload-test.mp4\"") && s.LastStartBody.Contains("\"size\":" + data.Length) && s.LastStartBody.Contains("\"title\":\"My clip\"") && s.LastStartBody.Contains("\"duration\":123") && s.LastStartBody.Contains("\"accept_terms\":true"), s.LastStartBody);
        Check("upload: up to four parts at a time (saw " + s.MaxConcurrentPuts + ")", s.MaxConcurrentPuts >= 2 && s.MaxConcurrentPuts <= 4, s.MaxConcurrentPuts.ToString());
        Check("upload: progress reached the whole file", lastTotal == data.Length && lastSent == data.Length && monotonic, lastSent + " of " + lastTotal);
        Check("upload: the key went to the team server on every API call and never to the bucket", !s.KeyOnBucket && !s.KeyMissingOnApi);
        Check("upload: the signed addresses were asked for in one batch, not one by one", s.PartNumberRequests.Count == 1 && s.PartNumberRequests[0] == "1 2 3 4 5 6", string.Join(" | ", s.PartNumberRequests.ToArray()));
        Check("upload: nothing was aborted", s.Deletes == 0);

        // a part that fails once is tried again with a fresh address
        s.FailFirstPutOfPart = 3; s.PartNumberRequests.Clear();
        var job2 = new UploadJob { Client = client, File = file, FolderId = "f1", CategoryId = "c1", RetryDelayMs = 30 };
        job2.Note += delegate(string n) { lock (leaks) leaks.Add(n); };
        var r2 = job2.Run();
        Check("retry: part 3 failed once (500), was asked for again with a fresh single address, and the upload completed", r2.ContainsKey("id") && s.PartNumberRequests.Contains("3") && s.ReceivedParts() == 6, string.Join(" | ", s.PartNumberRequests.ToArray()));
        s.FailFirstPutOfPart = 0;

        // a redirect from the bucket is never followed
        s.RedirectPartTo = 2; s.Deletes = 0;
        var job3 = new UploadJob { Client = client, File = file, FolderId = "f1", CategoryId = "c1", RetryDelayMs = 10, Attempts = 2 };
        job3.Note += delegate(string n) { lock (leaks) leaks.Add(n); };
        string msg3 = null;
        try { job3.Run(); } catch (ArmoryException ex) { msg3 = ex.Message; lock (leaks) leaks.Add(ex.Message); }
        Check("redirect: not followed (nothing reached /evil), the upload failed with a clear message and was aborted at the server", msg3 != null && s.EvilHits == 0 && msg3.Contains("redirect") && s.Deletes == 1, msg3 + " evil=" + s.EvilHits + " deletes=" + s.Deletes);
        s.RedirectPartTo = 0;

        // a cancel ends it at once and aborts it at the server
        s.PutDelayMs = 700; s.Deletes = 0;
        var job4 = new UploadJob { Client = client, File = file, FolderId = "f1", CategoryId = "c1", RetryDelayMs = 10 };
        bool cancelled = false; var sw = Stopwatch.StartNew();
        var th = new Thread(delegate() { try { job4.Run(); } catch (OperationCanceledException) { cancelled = true; } catch (Exception) { } });
        th.Start(); Thread.Sleep(300); job4.Cancel(); th.Join(10000);
        Check("cancel: the upload stopped with a cancellation in well under the part time, and DELETE was sent", cancelled && s.Deletes == 1 && sw.Elapsed.TotalSeconds < 5, "cancelled=" + cancelled + " deletes=" + s.Deletes + " " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
        s.PutDelayMs = 0;

        // the site refusing the file at the end (not a video): its reason is shown, and the upload is aborted
        s.CompleteError = "That file isn't a video."; s.Deletes = 0;
        var job5 = new UploadJob { Client = client, File = file, FolderId = "f1", CategoryId = "c1", RetryDelayMs = 10 };
        string msg5 = null;
        try { job5.Run(); } catch (ArmoryException ex) { msg5 = ex.Message; lock (leaks) leaks.Add(ex.Message); }
        Check("complete refused: the site's own reason is the message, and DELETE was sent", msg5 == "That file isn't a video." && s.Deletes == 1, msg5);
        s.CompleteError = null;

        // a wrong key
        var wrong = new ArmoryClient(s.Url, "wrong-" + Guid.NewGuid().ToString("N"));
        string msg6 = null;
        try { wrong.GetMe(); } catch (ArmoryException ex) { msg6 = ex.Message; lock (leaks) leaks.Add(ex.Message); }
        Check("a wrong key: a plain message, status 401", msg6 != null && msg6.Contains("didn't accept the key"), msg6);
        s.NotConnected = true;
        Check("the storage not connected is reported by /me", !client.GetMe().Connected);
        s.NotConnected = false;
      }

      // nothing is listening: a clear message, no hang
      var dead = new ArmoryClient("http://127.0.0.1:9", key) { Timeout = 3000 };
      string msg7 = null;
      try { dead.GetMe(); } catch (ArmoryException ex) { msg7 = ex.Message; lock (leaks) leaks.Add(ex.Message); }
      Check("no server: 'can't be reached', not a crash", msg7 != null && msg7.Contains("can't be reached"), msg7);

      bool leaked = false; foreach (string l in leaks) if (l.Contains(key)) leaked = true;
      Check("the key appears in no message and no note the client produced (" + leaks.Count + " collected)", !leaked && leaks.Count > 0);
    } finally { ArmoryClient.TestLoopbackHttp = false; }
  }

  // ------------------------------------------------------------------------------------------------ the window's upload, hotkey and cue (no GPU)
  // --ui: only these (a Recorder window off every monitor and the upload window, against the stand-in server; no capture, no encode).
  static int UiOnly() {
    ClearOutDir();
    Directory.CreateDirectory(outDir); Directory.CreateDirectory(shotsDir); Directory.CreateDirectory(throwDir);
    Console.WriteLine("Counts before: " + Counts());
    Native.ExcludePid = 0;
    var watch = new Watcher(); watch.Start();
    try { UiTests(watch); } catch (Exception ex) { Check("the run finished", false, ex.ToString()); }
    var violations = watch.Stop();
    GpuSafety.Check("the window tests");
    Check("no window of a child process was ever visible, and the Recorder's own windows never touched a monitor", violations.Count == 0, string.Join("; ", violations.ToArray()));
    int alive = Proc.StillRunning();
    Check("no process the tests started is still running", alive == 0, alive + " alive of " + Proc.Started.Count);
    Console.WriteLine("Counts after:  " + Counts());
    Console.WriteLine();
    Console.WriteLine(passed + " passed, " + failed + " failed (ui tests only)");
    return failed == 0 ? 0 : 1;
  }

  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
  [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

  static void UiTests(Watcher watch) {
    Console.WriteLine("== window: upload, hotkey, cue");
    MainForm.SkipEncoderCheck = true;   // nothing is recorded here, so no encoder session may be started
    string key = "test-key-" + Guid.NewGuid().ToString("N");
    var cues = new List<string>();
    MainForm.NoSound = true; MainForm.CueHook = delegate(string c) { lock (cues) cues.Add(c); };
    string ui = Path.Combine(outDir, "ui"); Directory.CreateDirectory(ui);

    // a small real MP4 made on the CPU (a test pattern), to send
    string clip = Path.Combine(ui, "Test Game 2026-10-17 23-59-59.mp4"), so, se;
    int code = Proc.Run(Tools.Ffmpeg, "-hide_banner -loglevel error -nostdin -y -f lavfi -i testsrc2=size=640x360:rate=30:duration=3 -c:v libx264 -preset ultrafast -pix_fmt yuv420p " + Tools.Quote(clip), 60000, out so, out se);
    Check("a small MP4 to upload was made on the CPU", code == 0 && File.Exists(clip), se);
    long clipSize = new FileInfo(clip).Length;
    var result = new RecordingResult { Ok = true, Mp4 = clip, Seconds = 3, Bytes = clipSize };

    ArmoryClient.TestLoopbackHttp = true;
    using (var stand = new ArmoryStandIn(key) { PartSize = 32 * 1024, PutDelayMs = 160 }) {
      var form = OpenRecorder(delegate(WindowInfo w) { return false; }, Path.Combine(outDir, "Recordings"));
      watch.Quiet(form.Handle);
      var opened = new List<string>(); form.OpenUrl = delegate(string u) { opened.Add(u); };
      try {
        Check("the Recorder's window has the global key shown (Ctrl+Alt+R)", form.HotkeyShown == "CTRL+ALT+R", form.HotkeyShown);
        Check("Upload to Armory is off until a recording has been saved", !form.UploadEnabled);
        Invoke(form, delegate { form.SetResultForTest(result); });
        Check("Upload to Armory is on once one has", form.UploadEnabled);
        Shot(form.Handle, "01-main-hotkey-upload.png", true);

        // no key in the settings: the button opens the website instead
        Invoke(form, delegate { form.Config = new AppSettings(); form.PressUploadButton(); });
        Check("without an Armory key the button opens https://taskforcehalberd.org/hub/videos and no upload window", opened.Count == 1 && opened[0] == "https://taskforcehalberd.org/hub/videos" && form.UploadWindow == null, string.Join(",", opened.ToArray()));
        Invoke(form, delegate { form.Config = new AppSettings { TeamServer = "http://example.com", TeamKey = key }; form.PressUploadButton(); });
        Check("with a key but a team server that isn't https the website is opened too (the key never goes out unprotected)", opened.Count == 2 && form.UploadWindow == null);

        // the key and an Armory on this PC: the upload window
        Invoke(form, delegate { form.Config = new AppSettings { TeamServer = stand.Url, TeamKey = key }; form.PressUploadButton(); });
        var uw = form.UploadWindow;
        Check("with a key the upload window opens", uw != null);
        Check("it asked the Armory for its rules and lists, and has the folders", WaitFor(delegate { return uw.CurrentPhase == UploadForm.Phase.Ready; }, 20000, "lists") && uw.FolderCount == 2, uw.StatusText);
        Check("Upload stays off until the terms are ticked", !uw.GoEnabled);
        Invoke(uw, delegate { uw.Fill(1, 1, "My clip from tonight", false); });
        Check("still off with the terms unticked", !uw.GoEnabled);
        Invoke(uw, delegate { uw.Fill(1, 1, "My clip from tonight", true); });
        Check("on once a folder, a category and the terms are chosen", uw.GoEnabled);
        Shot(uw.Handle, "02-upload-ready.png", true);
        Invoke(uw, delegate { uw.PressUpload(); });
        Check("uploading has begun", WaitFor(delegate { return uw.CurrentPhase == UploadForm.Phase.Uploading; }, 5000, "uploading"));
        WaitFor(delegate { return stand.ReceivedParts() >= 2; }, 20000, "some parts");
        Shot(uw.Handle, "03-upload-progress.png", true);
        Check("the upload finished and the window says so", WaitFor(delegate { return uw.CurrentPhase == UploadForm.Phase.Done; }, 60000, "done"), uw.StatusText);
        Shot(uw.Handle, "04-upload-done.png", true);
        Check("every byte of the file reached the stand-in, in parts of 32 KiB, with the folder, category and title chosen",
          stand.ReceivedBytes() == clipSize && stand.LastStartBody.Contains("\"folder_id\":\"f2\"") && stand.LastStartBody.Contains("\"category_id\":\"c2\"") && stand.LastStartBody.Contains("\"title\":\"My clip from tonight\"") && stand.LastStartBody.Contains("\"duration\":3") && stand.LastStartBody.Contains("\"accept_terms\":true"), stand.ReceivedBytes() + " of " + clipSize);
        Check("the key reached only the team server calls, never the bucket", !stand.KeyOnBucket && !stand.KeyMissingOnApi);
        Check("the window and its status never show the key", !uw.StatusText.Contains(key) && uw.Receipt != null && !uw.Receipt.ToString().Contains(key));
        Invoke(uw, delegate { uw.Close(); });
        form.UploadWindow = null;

        // cancel in the window
        stand.PutDelayMs = 900; stand.Deletes = 0;
        Invoke(form, delegate { form.PressUploadButton(); });
        var uw2 = form.UploadWindow;
        WaitFor(delegate { return uw2.CurrentPhase == UploadForm.Phase.Ready; }, 20000, "lists");
        Invoke(uw2, delegate { uw2.Fill(0, 0, "", true); uw2.PressUpload(); });
        WaitFor(delegate { return uw2.CurrentPhase == UploadForm.Phase.Uploading; }, 5000, "uploading");
        Thread.Sleep(400);
        Invoke(uw2, delegate { uw2.PressCancel(); });
        Check("Cancel in the window stops the upload, tells the server (DELETE) and says nothing was kept", WaitFor(delegate { return uw2.CurrentPhase == UploadForm.Phase.Failed; }, 15000, "cancelled") && stand.Deletes == 1 && uw2.StatusText.Contains("Nothing was kept"), uw2.StatusText + " deletes=" + stand.Deletes);
        Invoke(uw2, delegate { uw2.Close(); });
        form.UploadWindow = null;
        stand.PutDelayMs = 0;

        // the storage not connected
        stand.NotConnected = true;
        Invoke(form, delegate { form.PressUploadButton(); });
        var uw3 = form.UploadWindow;
        Check("an Armory whose storage isn't connected says so and offers no upload", WaitFor(delegate { return uw3.CurrentPhase == UploadForm.Phase.Unavailable; }, 20000, "unavailable") && !uw3.GoEnabled && uw3.StatusText.Contains("isn't connected"), uw3.StatusText);
        Invoke(uw3, delegate { uw3.Close(); });
        form.UploadWindow = null;
        stand.NotConnected = false;

        // the hotkey and the cue: the message Windows sends when the key is pressed, with nothing selected and no window recorded yet
        lock (cues) cues.Clear();
        PostMessage(form.Handle, 0x0312, new IntPtr(0xB1F2), IntPtr.Zero);
        Check("the hotkey with nothing to record says so and plays the error cue", WaitFor(delegate { lock (cues) return cues.Contains("error"); }, 5000, "cue") && form.StatusText.Contains("pick one"), form.StatusText);

        // the Settings window: the choices, saved at once to the prefs file and applied live; the look
        string prefsPath = Path.Combine(ui, "prefs.json");
        form.PrefsPath = prefsPath;
        Invoke(form, delegate { form.Config = new AppSettings { TeamServer = stand.Url, TeamKey = key }; form.SetChipEncoderForTest(EncoderProfile.Nvenc); form.PressSettings(); });
        var sw0 = form.SettingsWindow;
        Check("the Settings button opens the Settings window", sw0 != null);
        Check("it shows the defaults in plain words: Light, Native, Game capture, the program's own sound, mic Off, Normal",
          sw0.ChosenLabel(SettingChoices.Field.Encoder) == "Light, best for games" && sw0.ChosenLabel(SettingChoices.Field.Resolution) == "Native" && sw0.ChosenLabel(SettingChoices.Field.Capture) == "Game capture, best for games" &&
          sw0.ChosenLabel(SettingChoices.Field.Sound) == "The program's own sound" && sw0.ChosenLabel(SettingChoices.Field.Mic) == "Off" && sw0.ChosenLabel(SettingChoices.Field.Gpu) == "Normal");
        Check("the chip shows the defaults", form.ChipTextNow == "HEVC · NVENC · light · native · game capture · GPU normal", form.ChipTextNow);
        Shot(sw0.Handle, "05-settings-recording-console.png", true);
        Invoke(sw0, delegate { sw0.Choose(SettingChoices.Field.Encoder, 1); sw0.Choose(SettingChoices.Field.Resolution, 2); sw0.Choose(SettingChoices.Field.Capture, 2); sw0.Choose(SettingChoices.Field.Sound, 1); sw0.Choose(SettingChoices.Field.Mic, 1); sw0.Choose(SettingChoices.Field.Gpu, 1); });
        Check("choosing the other options: the chip follows at once", form.ChipTextNow == "HEVC · NVENC · quality · 720p · screen · mic · GPU high", form.ChipTextNow);
        string savedPrefs = File.Exists(prefsPath) ? File.ReadAllText(prefsPath) : "";
        Check("and it is saved at once in the prefs file", savedPrefs.Contains("\"EncoderProfile\":\"quality\"") && savedPrefs.Contains("\"MaxHeight\":720") && savedPrefs.Contains("\"CaptureMethod\":\"screen\"") && savedPrefs.Contains("\"Sound\":\"pc\"") && savedPrefs.Contains("\"Mic\":\"on\"") && savedPrefs.Contains("\"GpuPriority\":\"high\""), savedPrefs);
        Invoke(sw0, delegate { sw0.PressReset(); });
        Check("Reset to defaults puts the six back, in the window, the chip and the file", sw0.ChosenLabel(SettingChoices.Field.Encoder) == "Light, best for games" && sw0.ChosenLabel(SettingChoices.Field.Mic) == "Off" && form.ChipTextNow == "HEVC · NVENC · light · native · game capture · GPU normal" && File.ReadAllText(prefsPath).Contains("\"EncoderProfile\":\"standard\"") && File.ReadAllText(prefsPath).Contains("\"Mic\":\"off\""));
        form.BusyForTest = true; Invoke(sw0, delegate { sw0.RefreshNow(); });
        Check("while a recording runs the choices are greyed out", !sw0.ControlsEnabled);
        Shot(sw0.Handle, "settings-busy.png");
        form.BusyForTest = false; Invoke(sw0, delegate { sw0.RefreshNow(); });
        Check("and come back when it has been saved", sw0.ControlsEnabled);

        Invoke(sw0, delegate { sw0.ShowTab(1); });
        Shot(sw0.Handle, "06-settings-appearance-console.png", true);
        Invoke(sw0, delegate { sw0.PickAccent("225"); });
        Check("an accent swatch applies live and is saved as a number", Palette.AccentSpec == "225" && File.ReadAllText(prefsPath).Contains("\"Accent\":225"));
        Invoke(sw0, delegate { sw0.SetHue(190); });
        Check("the hue slider sets the accent too (190)", Palette.AccentSpec == "190" && sw0.HueShown == 190 && File.ReadAllText(prefsPath).Contains("\"Accent\":190") && Hex6(Palette.Accent) == "#2bbcd9", Hex6(Palette.Accent));
        Invoke(sw0, delegate { sw0.PickAccent("theme"); sw0.PickTheme("paper"); });
        bool paperOn = WaitFor(delegate { return Invoke(form, delegate { return form.BackColor == Palette.Bg; }); }, 2000, "main window repainted");   // the window takes the colours on its own thread, a moment later
        Check("the Paper theme applies live to the main window (light background and text), saved in the prefs", Palette.Theme.Id == "paper" && !Palette.Dark && paperOn && Hex6(Palette.Bg) == "#e9e6df" && File.ReadAllText(prefsPath).Contains("\"Theme\":\"paper\""), Hex6(form.BackColor) + " vs " + Hex6(Palette.Bg));
        Shot(sw0.Handle, "07-settings-appearance-paper.png", true);
        Shot(form.Handle, "08-main-paper.png", true);
        Invoke(form, delegate { form.SetResultForTest(result); });
        Invoke(form, delegate { form.PressUploadButton(); });
        var uwp = form.UploadWindow;
        WaitFor(delegate { return uwp != null && uwp.CurrentPhase == UploadForm.Phase.Ready; }, 20000, "lists");
        Check("the upload window takes the theme too", uwp != null && uwp.BackColor == Palette.Bg);
        Shot(uwp.Handle, "upload-paper.png");
        Invoke(uwp, delegate { uwp.Close(); }); form.UploadWindow = null;
        Invoke(sw0, delegate { sw0.PickTheme("console"); });
        bool consoleOn = WaitFor(delegate { return Invoke(form, delegate { return form.BackColor == Palette.Bg; }); }, 2000, "main window repainted");
        Check("back to Console: dark again", Palette.Dark && Hex6(Palette.Bg) == "#0b0d10" && consoleOn, Hex6(form.BackColor));
        Invoke(sw0, delegate { sw0.Close(); });

        // a new Recorder window reads the prefs file: the same theme and choices come back
        string unusedProblem;
        var again = Settings.LoadAll(Path.Combine(ui, "none.json"), prefsPath, out unusedProblem);
        Check("a Recorder started next time reads the same theme and choices from the prefs file", unusedProblem == null && again.ThemeId == "console" && again.Accent == "theme" && again.EncoderProfile == "standard" && again.MaxHeight == 0);
      } finally {
        try { Invoke(form, delegate { form.Close(); }); } catch (Exception) { }
      }
    }
    ArmoryClient.TestLoopbackHttp = false;
    MainForm.CueHook = null;

    // the real global-key call works for the default key, and is given back at once (it can fail only if another program holds it)
    uint mods, vk; string shown;
    Check("the default hotkey text parses to Ctrl+Alt+R", Hotkeys.TryParse(Hotkeys.Default, out mods, out vk, out shown) && mods == (Hotkeys.Control | Hotkeys.Alt) && vk == 'R' && shown == "CTRL+ALT+R");
    var nw = new NativeWindow(); nw.CreateHandle(new CreateParams());
    var tries = new[] { new[] { "Ctrl+Alt+R", "" }, new[] { "Ctrl+Alt+Shift+R", "" }, new[] { "Ctrl+Alt+F10", "" }, new[] { "Ctrl+Alt+Shift+F24", "" } };
    bool reg = false;
    foreach (var t in tries) {
      uint m2, v2; string s2; Hotkeys.TryParse(t[0], out m2, out v2, out s2);
      bool ok = RegisterHotKey(nw.Handle, 0xB1F3, m2 | 0x4000, v2); int err = ok ? 0 : Marshal.GetLastWin32Error();
      if (ok) UnregisterHotKey(nw.Handle, 0xB1F3);
      if (t[0] == "Ctrl+Alt+R") reg = ok;
      Note("RegisterHotKey(" + t[0] + ") " + (ok ? "worked and was given back" : "refused, Win32 error " + err + (err == 1409 ? " (already registered by another program)" : "")));
    }
    nw.DestroyHandle();
    Check("RegisterHotKey is callable (and its result is reported)", true);
  }

  // ------------------------------------------------------------------------------------------------ the global key and the cue setting
  static void UnitsHotkey(string tmp) {
    uint m, v; string n;
    Check("hotkey: Ctrl+Alt+R is control + alt + R", Hotkeys.TryParse("Ctrl+Alt+R", out m, out v, out n) && m == (Hotkeys.Control | Hotkeys.Alt) && v == 0x52 && n == "CTRL+ALT+R");
    Check("hotkey: case, spaces and order don't matter: ' shift + CONTROL + f9 '", Hotkeys.TryParse(" shift + CONTROL + f9 ", out m, out v, out n) && m == (Hotkeys.Control | Hotkeys.Shift) && v == 0x78 && n == "SHIFT+CTRL+F9", n);
    Check("hotkey: Win+Alt+1", Hotkeys.TryParse("Win+Alt+1", out m, out v, out n) && m == (Hotkeys.Win | Hotkeys.Alt) && v == 0x31);
    Check("hotkey: F24 is the last function key", Hotkeys.TryParse("Ctrl+F24", out m, out v, out n) && v == 0x87 && !Hotkeys.TryParse("Ctrl+F25", out m, out v, out n) && !Hotkeys.TryParse("Ctrl+F0", out m, out v, out n));
    Check("hotkey: a bare key, a missing key, a doubled modifier, two keys, an unknown key and empty text are refused",
      !Hotkeys.TryParse("R", out m, out v, out n) && !Hotkeys.TryParse("Ctrl+Alt", out m, out v, out n) && !Hotkeys.TryParse("Ctrl+Ctrl+R", out m, out v, out n) && !Hotkeys.TryParse("Ctrl+R+T", out m, out v, out n) && !Hotkeys.TryParse("Ctrl+Escape", out m, out v, out n) && !Hotkeys.TryParse("", out m, out v, out n) && !Hotkeys.TryParse("Ctrl+", out m, out v, out n) && !Hotkeys.TryParse(null, out m, out v, out n));
    string f = Path.Combine(tmp, "hk.json");
    File.WriteAllText(f, "{}"); var s = Settings.Load(f);
    Check("settings: the hotkey defaults to Ctrl+Alt+R and the cue is on", s.Hotkey == "Ctrl+Alt+R" && s.Cue);
    File.WriteAllText(f, "{\"Hotkey\":\"Ctrl+Shift+F8\",\"Cue\":\"off\"}"); s = Settings.Load(f);
    Check("settings: Hotkey and Cue are read (Cue off)", s.Hotkey == "Ctrl+Shift+F8" && !s.Cue);
    File.WriteAllText(f, "{\"Hotkey\":\"off\"}"); s = Settings.Load(f);
    Check("settings: Hotkey off is kept as off", s.Hotkey == "off");
  }

  // ------------------------------------------------------------------------------------------------ the member's choices (prefs), the Settings window's labels, and the look
  static bool Rgb(int[] c, int r, int g, int b) { return c[0] == r && c[1] == g && c[2] == b; }
  static string Hex6(Color c) { return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"); }

  static void UnitsPrefs(string tmp) {
    // defaults everywhere
    var d = new AppSettings();
    Check("defaults: Light (standard), Native, game capture, the program's own sound, mic off, GPU normal, Console theme with its own accent",
      d.EncoderProfile == "standard" && d.MaxHeight == 0 && d.CaptureMethod == "game" && d.Sound == "game" && d.Mic == "off" && d.GpuPriority == "normal" && d.ThemeId == "console" && d.Accent == "theme");

    // the labels of the Settings window and what each sets
    Check("six choices, labelled ENCODER, RESOLUTION, CAPTURE, SOUND, MICROPHONE, GPU PRIORITY", string.Join("|", SettingChoices.Fields) == "ENCODER|RESOLUTION|CAPTURE|SOUND|MICROPHONE|GPU PRIORITY" && Enum.GetValues(typeof(SettingChoices.Field)).Length == SettingChoices.Fields.Length);
    Check("labels: Encoder is 'Light, best for games' and 'Quality, for desktop apps only (heavier on the GPU)'", string.Join("|", SettingChoices.Labels(SettingChoices.Field.Encoder)) == "Light, best for games|Quality, for desktop apps only (heavier on the GPU)");
    Check("labels: Resolution is Native, 1080p, 720p", string.Join("|", SettingChoices.Labels(SettingChoices.Field.Resolution)) == "Native|1080p|720p");
    Check("labels: Capture is game capture, this window, the whole screen", string.Join("|", SettingChoices.Labels(SettingChoices.Field.Capture)) == "Game capture, best for games|This window|The whole screen it's on");
    Check("labels: Sound is the program's own or everything the PC plays", string.Join("|", SettingChoices.Labels(SettingChoices.Field.Sound)) == "The program's own sound|Everything the PC plays");
    Check("labels: Microphone is Off or 'On, on its own track'", string.Join("|", SettingChoices.Labels(SettingChoices.Field.Mic)) == "Off|On, on its own track");
    Check("labels: GPU priority is Normal and High", string.Join("|", SettingChoices.Labels(SettingChoices.Field.Gpu)) == "Normal|High");
    Check("the capture hint promises the fall back to window capture", SettingChoices.Hint(SettingChoices.Field.Capture).Contains("fall back to window capture"));
    bool mapOk = true;
    foreach (SettingChoices.Field fld in Enum.GetValues(typeof(SettingChoices.Field))) {
      int n = SettingChoices.Labels(fld).Length;
      for (int i = 0; i < n; i++) { var s = new AppSettings(); SettingChoices.Set(s, fld, i); if (SettingChoices.Get(s, fld) != i) mapOk = false; }
    }
    Check("every label maps to a setting and back to the same label", mapOk);
    var m = new AppSettings();
    foreach (SettingChoices.Field fld in Enum.GetValues(typeof(SettingChoices.Field))) SettingChoices.Set(m, fld, 1);
    Check("choosing the second label of each sets quality, 1080, window capture, the PC's sound, the mic, high", m.Quality && m.MaxHeight == 1080 && m.CaptureMethod == "window" && m.Sound == "pc" && m.WithMic && m.GpuPriority == "high");
    SettingChoices.Set(m, SettingChoices.Field.Resolution, 2); SettingChoices.Set(m, SettingChoices.Field.Capture, 2);
    Check("the third labels are 720 and screen capture", m.MaxHeight == 720 && m.Screen);
    m.ThemeId = "paper"; m.Accent = "225"; SettingChoices.Defaults(m);
    Check("Reset to defaults restores the six recording choices and leaves the look alone", m.EncoderProfile == "standard" && m.MaxHeight == 0 && m.CaptureMethod == "game" && m.Sound == "game" && !m.WithMic && m.GpuPriority == "normal" && m.ThemeId == "paper" && m.Accent == "225");
    var odd = new AppSettings { MaxHeight = 900 };
    Check("a MaxHeight set by hand to another height shows as Native until a choice is made", SettingChoices.Get(odd, SettingChoices.Field.Resolution) == 0);

    // the prefs file: where, round trip, precedence over the settings file, malformed
    string settings = Path.Combine(tmp, "pf-settings.json"), local = Path.Combine(tmp, "local");
    File.WriteAllText(settings, "{\"DataFolder\":\"" + Path.Combine(tmp, "suite").Replace("\\", "\\\\") + "\"}");
    string prefs = Settings.PrefsFile(settings, local);
    Check("prefs live in <DataFolder>\\Recorder.prefs.json, a file the Suite never writes", prefs == Path.Combine(tmp, "suite", "Recorder.prefs.json") && prefs != settings);
    Check("prefs without a DataFolder live in %LOCALAPPDATA%\\Bifrost Recorder\\prefs.json", Settings.PrefsFile(Path.Combine(tmp, "none.json"), local) == Path.Combine(local, "Bifrost Recorder", "prefs.json"));
    var all = new AppSettings { EncoderProfile = "quality", MaxHeight = 720, CaptureMethod = "screen", Sound = "pc", Mic = "on", GpuPriority = "high", ThemeId = "midnight", Accent = "280" };
    string problem;
    Settings.SavePrefs(prefs, all);
    string saved = File.ReadAllText(prefs);
    Check("saving creates the folder, writes the choices, a hue accent as a number (as Scribe saves it) and leaves no temp file", File.Exists(prefs) && !File.Exists(prefs + ".tmp") && saved.Contains("\"Accent\":280") && saved.Contains("\"Theme\":\"midnight\"") && saved.Contains("\"EncoderProfile\":\"quality\""), saved);
    var back = Settings.LoadAll(settings, prefs, out problem);
    Check("prefs round trip: all six choices, the theme and the accent come back", problem == null && back.Quality && back.MaxHeight == 720 && back.Screen && back.Sound == "pc" && back.WithMic && back.GpuPriority == "high" && back.ThemeId == "midnight" && back.Accent == "280" && back.DataFolder == Path.Combine(tmp, "suite"));
    foreach (string acc in new[] { "theme", "white", "black", "0", "359" }) {
      var a1 = new AppSettings { Accent = acc }; Settings.SavePrefs(prefs, a1);
      Check("prefs round trip of the accent '" + acc + "'", Settings.LoadAll(settings, prefs, out problem).Accent == acc && problem == null);
    }
    Settings.SavePrefs(prefs, new AppSettings());
    Check("saving over an existing prefs file replaces it", Settings.LoadAll(settings, prefs, out problem).EncoderProfile == "standard" && !File.Exists(prefs + ".tmp"));

    // precedence: prefs > the settings file's experiment keys > defaults
    File.WriteAllText(settings, "{\"EncoderProfile\":\"quality\",\"GpuPriority\":\"high\",\"MaxHeight\":1080,\"Theme\":\"od-green\",\"Accent\":\"white\"}");
    var onlySettings = Settings.LoadAll(settings, Path.Combine(tmp, "missing-prefs.json"), out problem);
    Check("no prefs file: the settings file's keys apply, no problem reported", problem == null && onlySettings.Quality && onlySettings.GpuPriority == "high" && onlySettings.MaxHeight == 1080 && onlySettings.ThemeId == "od-green" && onlySettings.Accent == "white");
    File.WriteAllText(prefs, "{\"EncoderProfile\":\"standard\",\"Theme\":\"paper\"}");
    var both = Settings.LoadAll(settings, prefs, out problem);
    Check("prefs win over the settings file for the keys they hold (encoder standard, theme paper) and the rest still comes from the settings file (GPU high, 1080, white accent)", problem == null && !both.Quality && both.ThemeId == "paper" && both.GpuPriority == "high" && both.MaxHeight == 1080 && both.Accent == "white");
    File.WriteAllText(prefs, "{\"MaxHeight\":0,\"GpuPriority\":\"normal\"}");
    var zero = Settings.LoadAll(settings, prefs, out problem);
    Check("a prefs value that equals the default still wins over the settings file (native, GPU normal)", zero.MaxHeight == 0 && zero.GpuPriority == "normal" && zero.Quality);

    // malformed prefs: a note, never a crash, the rest stands
    foreach (string bad in new[] { "{ not json", "", "[1,2]", "\"text\"", "null", "{\"EncoderProfile\":" }) {
      File.WriteAllText(prefs, bad);
      AppSettings got = null; bool threw = false; problem = null;
      try { got = Settings.LoadAll(settings, prefs, out problem); } catch (Exception) { threw = true; }
      Check("malformed prefs (" + (bad.Length == 0 ? "empty file" : bad) + "): no crash, a note for the status line, the settings file's keys still apply", !threw && problem != null && problem.StartsWith("Your saved choices couldn't be read") && got.Quality && got.GpuPriority == "high", problem);
    }
    File.WriteAllText(prefs, "{\"EncoderProfile\":\"turbo\",\"MaxHeight\":\"1080\",\"Theme\":\"neon\",\"Accent\":\"purple\",\"GpuPriority\":5,\"CaptureMethod\":null}");
    var junk = Settings.LoadAll(settings, prefs, out problem);
    Check("unusable individual prefs values are ignored (the settings file's value, else the default, stands)", problem == null && junk.Quality && junk.MaxHeight == 1080 && junk.ThemeId == "od-green" && junk.Accent == "white" && junk.GpuPriority == "high" && !junk.Screen);
    Settings.SavePrefs(prefs, new AppSettings());
    Check("the next save after a malformed file writes a good one", Settings.LoadAll(settings, prefs, out problem).EncoderProfile == "standard" && problem == null);
    string suiteFile = File.ReadAllText(settings);
    Settings.SavePrefs(prefs, all);
    Check("saving prefs never touches the settings file", File.ReadAllText(settings) == suiteFile);
  }

  static void UnitsTheme() {
    // the presets: Scribe's five, in its order, with its token values
    var ids = new List<string>(); foreach (var t in Theming.Themes) ids.Add(t.Id);
    Check("five presets, in Scribe's order: console, midnight, od-green, paper, high-contrast", string.Join(",", ids.ToArray()) == "console,midnight,od-green,paper,high-contrast");
    var con = Theming.Find("console"); var pap = Theming.Find("paper"); var hc = Theming.Find("high-contrast"); var mid = Theming.Find("midnight"); var od = Theming.Find("od-green");
    Check("token values: Console", con.Bg == "#0b0d10" && con.Surface == "#12161b" && con.Card == "#171c22" && con.Field == "#0e1216" && con.Rule == "#252c34" && con.Fg == "#e6edf2" && con.Muted == "#a3afba" && con.Dim == "#6c7884" && con.Faint == "#414b57" && con.On == "#3fbf5a" && con.Off == "#e0463c" && con.Call == "#7fb2e5" && con.Dark && con.Hue == 32);
    Check("token values: Midnight, OD green", mid.Bg == "#0a0e19" && mid.Fg == "#dfe4f5" && mid.On == "#5bd6a0" && mid.Off == "#ff6b81" && mid.Hue == 225 && od.Bg == "#0f110c" && od.Surface == "#161a12" && od.On == "#8fc46a" && od.Hue == 48);
    Check("token values: Paper is light, High contrast has white rules and its own sv and soft alphas", !pap.Dark && pap.Bg == "#e9e6df" && pap.Surface == "#f5f3ee" && pap.Card == "#ffffff" && pap.Fg == "#1f2328" && pap.Off == "#c62828" && pap.Hue == 20 && hc.Rule == "#ffffff" && hc.Hue == 52 && hc.Sv[0] == 1 && hc.Sv[1] == 1 && hc.Soft[0] == 0.35 && hc.Soft[2] == 0.8);
    Check("an unknown theme id is Console", Theming.Find("neon") == con && Theming.Find(null) == con && !Theming.Known("neon") && Theming.Known("paper"));
    Check("the swatches are Scribe's: hues 32, 0, 120, 190, 225, 280, white, black", Theming.AccentSwatches.Length == 8 && (int)Theming.AccentSwatches[0] == 32 && (int)Theming.AccentSwatches[5] == 280 && (string)Theming.AccentSwatches[6] == "white" && (string)Theming.AccentSwatches[7] == "black");

    // the colour maths, against what theme.ts gives (worked out from its hsvToRgb and accentRgb; dark themes use s 0.8, v 0.85, light 0.85 and 0.72)
    Check("accent of Console's own hue 32 is #d9882b (217, 136, 43), Planner's console amber", Rgb(Theming.AccentRgb(con, "theme"), 217, 136, 43) && Rgb(Theming.AccentRgb(con, "32"), 217, 136, 43));
    Check("dark themes: hue 0 (217,43,43), 120 (43,217,43), 190 (43,188,217), 225 (43,87,217), 280 (159,43,217)",
      Rgb(Theming.AccentRgb(con, "0"), 217, 43, 43) && Rgb(Theming.AccentRgb(con, "120"), 43, 217, 43) && Rgb(Theming.AccentRgb(con, "190"), 43, 188, 217) && Rgb(Theming.AccentRgb(con, "225"), 43, 87, 217) && Rgb(Theming.AccentRgb(con, "280"), 159, 43, 217));
    Check("a theme's own accent: Midnight 225 is (43,87,217) too; OD green hue 48 is (217,182,43)", Rgb(Theming.AccentRgb(mid, "theme"), 43, 87, 217) && Rgb(Theming.AccentRgb(od, "theme"), 217, 182, 43), string.Join(",", Theming.AccentRgb(od, "theme")));
    Check("light theme (Paper, s 0.85 v 0.72): its own hue 20 is (184,80,28), hue 32 (184,111,28), hue 225 (28,67,184)", Rgb(Theming.AccentRgb(pap, "theme"), 184, 80, 28) && Rgb(Theming.AccentRgb(pap, "32"), 184, 111, 28) && Rgb(Theming.AccentRgb(pap, "225"), 28, 67, 184));
    Check("High contrast uses its own s and v (1, 1): hue 52 is (255,221,0)", Rgb(Theming.AccentRgb(hc, "theme"), 255, 221, 0));
    Check("white and black accents are fixed greys (220,226,232) and (120,128,138) in every theme", Rgb(Theming.AccentRgb(con, "white"), 220, 226, 232) && Rgb(Theming.AccentRgb(pap, "white"), 220, 226, 232) && Rgb(Theming.AccentRgb(con, "black"), 120, 128, 138) && Rgb(Theming.AccentRgb(hc, "black"), 120, 128, 138));
    Check("JavaScript's rounding (halves go up): v 0.3 is 76.5, which theme.ts rounds to 77", Rgb(Theming.HsvToRgb(0, 0, 0.3), 77, 77, 77), string.Join(",", Theming.HsvToRgb(0, 0, 0.3)));
    Check("hsvToRgb: pure red, green, blue at full s and v", Rgb(Theming.HsvToRgb(0, 1, 1), 255, 0, 0) && Rgb(Theming.HsvToRgb(120, 1, 1), 0, 255, 0) && Rgb(Theming.HsvToRgb(240, 1, 1), 0, 0, 255) && Rgb(Theming.HsvToRgb(60, 1, 1), 255, 255, 0));
    Check("a hue is wrapped into 0-359 and text accents are kept: 361 is 1, -1 is 359, 225.4 is 225, 'white', 'theme'", Theming.NormalizeAccent(361) == "1" && Theming.NormalizeAccent(-1) == "359" && Theming.NormalizeAccent(225.4) == "225" && Theming.NormalizeAccent("white") == "white" && Theming.NormalizeAccent(" Black ") == "black" && Theming.NormalizeAccent("theme") == "theme" && Theming.NormalizeAccent("225") == "225");
    Check("anything else is 'theme'", Theming.NormalizeAccent("purple") == "theme" && Theming.NormalizeAccent(null) == "theme" && Theming.NormalizeAccent(true) == "theme" && Theming.NormalizeAccent(new object[0]) == "theme");
    Check("contrast ratios: white on black is 21, a colour on itself is 1", Math.Abs(Theming.Contrast(new[] { 255, 255, 255 }, new[] { 0, 0, 0 }) - 21.0) < 1e-9 && Math.Abs(Theming.Contrast(new[] { 12, 34, 56 }, new[] { 12, 34, 56 }) - 1.0) < 1e-9);
    Check("on-accent text: dark on the amber (Console's own), dark on white and black accents, white on blue and violet", Theming.OnAccent(Theming.AccentRgb(con, "theme")) == "#0b0d10" && Theming.OnAccent(Theming.AccentRgb(con, "white")) == "#0b0d10" && Theming.OnAccent(Theming.AccentRgb(con, "black")) == "#0b0d10" && Theming.OnAccent(Theming.AccentRgb(con, "225")) == "#ffffff" && Theming.OnAccent(Theming.AccentRgb(con, "280")) == "#ffffff");
    Check("lift: toward white on dark themes (234,190,138), toward black on light ones (114,50,17)", Rgb(Theming.Lift(con, new[] { 217, 136, 43 }), 234, 190, 138) && Rgb(Theming.Lift(pap, new[] { 184, 80, 28 }), 114, 50, 17));
    Check("soft alphas default to 0.06, 0.07, 0.14; High contrast has its own", Theming.SoftAlphas(con)[0] == 0.06 && Theming.SoftAlphas(con)[2] == 0.14 && Theming.SoftAlphas(hc)[1] == 0.6);

    // what the Recorder draws with, and that it stays readable in every theme (Paper included, with every swatch)
    Palette.Apply("paper", "225");
    Check("Palette after Apply(paper, 225): the Paper tokens and a blue accent for light surfaces (28,67,184), not dark", !Palette.Dark && Hex6(Palette.Bg) == "#e9e6df" && Hex6(Palette.Surface) == "#f5f3ee" && Hex6(Palette.Fg) == "#1f2328" && Hex6(Palette.Red) == "#c62828" && Hex6(Palette.Accent) == "#1c43b8", Hex6(Palette.Accent));
    Palette.Apply("console", "theme");
    Check("Palette back on Console: the amber accent, dark text on it, and the Console tokens", Palette.Dark && Hex6(Palette.Accent) == "#d9882b" && Hex6(Palette.OnAccent) == "#0b0d10" && Hex6(Palette.Bg) == "#0b0d10" && Hex6(Palette.Green) == "#3fbf5a");
    int fails = 0; var failText = new StringBuilder();
    foreach (var t in Theming.Themes) {
      var bg = Theming.HexToRgb(t.Bg); var sur = Theming.HexToRgb(t.Surface);
      Action<string, double, double> need = delegate(string what, double got, double min) { if (got < min) { fails++; failText.Append(t.Id + " " + what + " " + got.ToString("0.0") + " < " + min + "; "); } };
      need("text on the background", Theming.Contrast(Theming.HexToRgb(t.Fg), bg), 7);
      need("text on the panels", Theming.Contrast(Theming.HexToRgb(t.Fg), sur), 7);
      need("secondary text on the panels", Theming.Contrast(Theming.HexToRgb(t.Muted), sur), 4.5);
      need("labels on the panels", Theming.Contrast(Theming.HexToRgb(t.Dim), sur), 3);
      need("the recording red on the panels", Theming.Contrast(Theming.HexToRgb(t.Off), sur), 3);
      need("the 'falling behind' amber on the panels", Theming.Contrast(Theming.HexToRgb(t.Warn), sur), 3);
      need("success green on the panels", Theming.Contrast(Theming.HexToRgb(t.On), sur), 3);
      foreach (object a in new object[] { "theme", 32, 0, 120, 190, 225, 280, "white", "black" }) {
        Palette.Apply(t.Id, a is int ? ((int)a).ToString() : (string)a);
        var ink = new int[] { Palette.AccentInk.R, Palette.AccentInk.G, Palette.AccentInk.B };
        need("accent ink (" + a + ") on the panels", Theming.Contrast(ink, new int[] { Palette.Surface.R, Palette.Surface.G, Palette.Surface.B }), 3);
        need("accent ink (" + a + ") on the header", Theming.Contrast(ink, new int[] { Palette.Band.R, Palette.Band.G, Palette.Band.B }), 3);
        need("text on an accent button (" + a + ")", Theming.Contrast(new int[] { Palette.OnAccent.R, Palette.OnAccent.G, Palette.OnAccent.B }, new int[] { Palette.Accent.R, Palette.Accent.G, Palette.Accent.B }), 3);
        need("text on the red Stop button", Theming.Contrast(new int[] { Palette.OnRed.R, Palette.OnRed.G, Palette.OnRed.B }, new int[] { Palette.Red.R, Palette.Red.G, Palette.Red.B }), 3);
      }
    }
    Palette.Apply("paper", "white");
    Check("Paper with the white accent: the accent as text falls back to the text colour (white on white would vanish)", Palette.AccentInk == Palette.Fg && Palette.Accent.R == 220);
    Palette.Apply("console", "theme");
    Check("Console keeps its accent as ink", Palette.AccentInk == Palette.Accent);
    Palette.Apply(Theming.DefaultThemeId, "theme");
    Check("every theme and accent is readable: text 7:1, secondary 4.5:1, labels, red, amber, green and accent ink 3:1" + (fails > 0 ? " (" + fails + " short)" : ""), fails == 0, failText.ToString());
  }

  // A child that does no GPU work at all (ping, waiting): what the PC answers for HIGH and ABOVE_NORMAL without administrator rights.
  static string Ping { get { return Path.Combine(Environment.SystemDirectory, "PING.EXE"); } }

  static void GpuPriorityApiTest() {
    var p = Proc.Start(Ping, "-n 30 127.0.0.1", false);
    try {
      Thread.Sleep(400);
      var lines = new List<string>();
      int got = GpuScheduling.Try(p.Handle, delegate(string s) { lines.Add(s); });
      foreach (string l in lines) Note("gpu priority (CPU-only child): " + l);
      Check("the GPU priority call exists and answers with an NTSTATUS for HIGH (no exception)", lines.Count >= 1 && lines[0].Contains("(HIGH) = 0x"), lines.Count > 0 ? lines[0] : "no lines");
      Note("result: " + (got == GpuScheduling.High ? "HIGH accepted without administrator rights" : got == GpuScheduling.AboveNormal ? "HIGH refused, ABOVE_NORMAL accepted" : "both refused or unavailable"));
    } finally { try { if (!p.HasExited) p.Kill(); p.WaitForExit(3000); } catch (Exception) { } }
  }

  // A process that is killed outright (what a crash looks like) must take the children the tests started with it: the Job Object.
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
    Check("job object: a child was running while its parent lived", wasRunning, line);
    Check("job object: the parent was killed outright and its child ended with it", p.HasExited && gone, "child " + pid + " gone=" + gone);
  }

  // --hold <Scene> <width> <height> <seconds>: opens a test window and prints its handle, for trying captures by hand.
  static int Hold(string[] a) {
    var win = TestWindow.Open((Scene)Enum.Parse(typeof(Scene), a[1]), int.Parse(a[2]), int.Parse(a[3]), false, WindowPrefix + ": held");
    Console.WriteLine("HWND=" + win.Handle.ToInt64()); Console.Out.Flush();
    Thread.Sleep(int.Parse(a[4]) * 1000);
    win.CloseWindow();
    return 0;
  }

  // --window <Scene> <width> <height> <border 0|1> <title>: one test window in a process of its own, because libobs never captures a window of
  // the process it runs in (window-helpers.c). It says HWND=<handle> PLACED=<where>, then takes orders on stdin: freeze, resize W H, close.
  static int WindowChild(string[] a) {
    TestWindow.LayerAlpha = 255;   // not layered: GDI writes no alpha, so a layered window reaches libobs with alpha 0 and records black
    var win = TestWindow.Open((Scene)Enum.Parse(typeof(Scene), a[1]), int.Parse(a[2]), int.Parse(a[3]), a[4] == "1", a[5]);
    string placed; lock (TestWindow.Placed) placed = TestWindow.Placed[TestWindow.Placed.Count - 1];
    Console.WriteLine("HWND=" + win.Handle.ToInt64() + " PLACED=" + placed); Console.Out.Flush();
    string line;
    var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), true);   // the parent writes UTF-8 with a byte order mark first, which this drops
    while ((line = input.ReadLine()) != null) {
      var p = line.Trim().Split(' ');
      if (p[0] == "close") break;
      string answer = "UNKNOWN " + line;
      if (p[0] == "freeze") { win.Frozen = true; answer = "OK freeze"; }
      else if (p[0] == "resize") { int w = int.Parse(p[1]), h = int.Parse(p[2]); win.Invoke(new MethodInvoker(delegate { win.ClientSize = new Size(w, h); })); answer = "OK resize"; }
      Console.WriteLine(answer); Console.Out.Flush();
    }
    // how long each step of closing takes, beside the other test output (a captured window once took 8 s to close)
    var sw = Stopwatch.StartNew();
    var steps = new StringBuilder("close ordered\n");
    win.CloseWindow(delegate(string step) { lock (steps) steps.Append(sw.ElapsedMilliseconds + " ms  " + step + "\n"); });
    steps.Append(sw.ElapsedMilliseconds + " ms  closed\n");
    try { File.WriteAllText(Path.Combine(outDir, "window-close-" + Process.GetCurrentProcess().Id + ".txt"), a[5] + "\n" + steps); } catch (Exception) { }
    return 0;
  }

  // A test window in its own process (WindowChild), as the tests drive it. It is an ordinary opaque window: the tests' invisible windows are
  // layered, GDI writes no alpha into them, and libobs draws window capture with its alpha, so they recorded black (ffmpeg ignored alpha).
  // It shows at the bottom of the window order, behind everything else; the watcher checks it never activates, goes topmost or takes the focus.
  class RemoteWindow {
    public Process P;
    public IntPtr Handle;
    public int Pid;
    public static readonly List<int> Pids = new List<int>();
    public static readonly List<IntPtr> Handles = new List<IntPtr>();

    public static RemoteWindow Open(Scene scene, int w, int h, bool border, string title) {
      var r = new RemoteWindow();
      r.P = Proc.Start(Path.Combine(build, "RecorderTests.exe"), "--window " + scene + " " + w + " " + h + " " + (border ? "1" : "0") + " " + Tools.Quote(title), true);
      r.Pid = r.P.Id;
      if (watcher != null) watcher.Allow(r.Pid);   // before its window shows
      string line = r.P.StandardOutput.ReadLine() ?? "";
      int i = line.IndexOf("HWND="), j = line.IndexOf(" PLACED=");
      if (i < 0 || j < 0) throw new Exception("the test window process didn't start: " + line);
      r.Handle = new IntPtr(long.Parse(line.Substring(i + 5, j - i - 5), CultureInfo.InvariantCulture));
      lock (Pids) Pids.Add(r.Pid);
      lock (Handles) Handles.Add(r.Handle);
      lock (TestWindow.Placed) TestWindow.Placed.Add(line.Substring(j + 8));
      return r;
    }

    // An order to the window's process, and its answer ("OK freeze"): the tests check the order was understood.
    public string Send(string order) { P.StandardInput.WriteLine(order); P.StandardInput.Flush(); return P.StandardOutput.ReadLine() ?? ""; }
    public bool Freeze() { return Send("freeze") == "OK freeze"; }
    public bool Resize(int w, int h) { return Send("resize " + w + " " + h) == "OK resize"; }
    public void CloseWindow() {
      try { P.StandardInput.WriteLine("close"); P.StandardInput.Flush(); if (!P.WaitForExit(8000)) P.Kill(); } catch (Exception) { }
    }
    public static bool Owns(WindowInfo w) { lock (Pids) return Pids.Contains((int)w.ProcessId) && w.Title.StartsWith(WindowPrefix); }
  }

  // --crash-child <hwnd> <folder> <seconds>: records the given window (window capture), says RECORDING=<file>, and after <seconds> ends its own
  // process outright, with no Stop: what a crash or End Task leaves behind. The parent checks the file still plays.
  static int CrashChild(string[] a) {
    string err; bool started;
    var rec = Rec(new IntPtr(long.Parse(a[1], CultureInfo.InvariantCulture)), a[2], "crash", null, null, out started, out err);
    if (!started) { Console.WriteLine("FAILED=" + err); Console.Out.Flush(); return 1; }
    Console.WriteLine("RECORDING=" + rec.Mp4Path); Console.Out.Flush();
    Thread.Sleep(int.Parse(a[3], CultureInfo.InvariantCulture) * 1000);
    Process.GetCurrentProcess().Kill();
    return 0;
  }

  static int JobChild() {
    var p = Proc.Start(Ping, "-n 600 127.0.0.1", false);
    Console.WriteLine("PID=" + p.Id); Console.Out.Flush();
    Thread.Sleep(1500);
    Process.GetCurrentProcess().Kill();
    return 0;
  }

  // ------------------------------------------------------------------------------------------------ the engine
  // OBS Studio's files beside the Recorder, libobs started on this thread (a single-threaded apartment, as on the Recorder's window
  // thread), and the encoders its plugins registered after their own GPU tests.
  static void EngineTest() {
    Console.WriteLine("== the engine (libobs)");
    Check("the OBS files the Recorder needs are all beside it", Obs.Missing() == null, Obs.Missing());
    string hooks = Path.Combine(build, @"obs\data\obs-plugins\win-capture");
    bool signed = true; string who = "";
    foreach (string f in new[] { "graphics-hook64.dll", "graphics-hook32.dll", "inject-helper64.exe", "inject-helper32.exe" }) {
      try { var cert = X509Certificate.CreateFromSignedFile(Path.Combine(hooks, f)); who = cert.Subject; } catch (Exception) { signed = false; who = f + " has no signature"; }
    }
    Check("the game capture hook and its injector are OBS's own signed files (build.ps1 refuses any whose signature isn't valid)", signed && who.Contains("O=\"OBS Project, LLC\""), who);
    // started the way the Recorder starts it: on a thread of its own, the answer coming back from that thread
    var sw = Stopwatch.StartNew();
    string err = "no answer in 120 s";
    var answered = new ManualResetEvent(false);
    int callerThread = Thread.CurrentThread.ManagedThreadId, engineThread = -1;
    Obs.StartInBackground(delegate(string e) { err = e; engineThread = Thread.CurrentThread.ManagedThreadId; answered.Set(); });
    answered.WaitOne(120000);
    long ms = sw.ElapsedMilliseconds;
    Check("libobs starts on a thread of its own, its plugins load and the encoders test themselves (" + ms + " ms)", err == null && Obs.Started && engineThread != callerThread && engineThread > 0, err);
    if (!Obs.Started) { lock (Obs.StartupLog) foreach (string l in Obs.StartupLog) Note(l); return; }
    Check("it is OBS Studio 32.2.2's libobs", Obs.Version == "32.2.2", Obs.Version);
    Check("the encoders registered include NVENC HEVC (this PC's RTX 3070 Ti), x264 and AAC", Obs.Encoders.Contains("obs_nvenc_hevc_tex") && Obs.Encoders.Contains("obs_x264") && Obs.Encoders.Contains("ffmpeg_aac"), string.Join(",", Obs.Encoders.ToArray()));
    Check("the engine picks NVENC here", Obs.PickEncoder() == EncoderProfile.Nvenc, Obs.PickEncoder().Name);
    string log;
    lock (Obs.StartupLog) log = string.Join("\n", Obs.StartupLog.ToArray());
    Check("libobs's messages were caught (" + Obs.StartupLog.Count + " lines), none shown", Obs.StartupLog.Count > 10);
    Check("nothing was fetched from obsproject.com: win-capture was given no config folder, so it keeps no compatibility updates", !log.Contains("obsproject.com") && !log.Contains("update_info"), "");
    Check("no plugin failed to load", !log.Contains("Failed to load") && !log.Contains("LoadLibrary failed"), Tools.Last(log, 3));
    foreach (string l in Obs.StartupLog) if (l.Contains("GPU priority") || l.Contains("Loading module") || l.Contains("ERROR") || l.Contains("warning") || l.Contains("NVENC") || l.Contains("adapter")) Note(l);
    Check("asking again with another canvas only resets the video", Obs.Start(640, 360, 640, 360) == null);
    // win-capture runs two helper programs at plugin load (get-graphics-offsets32/64) and reads their pipes to the end. A process started
    // meanwhile (as the tests start their window processes) inherits that pipe and holds it open until it exits, which held the first game
    // capture for minutes. Making a game capture source waits for that setup, so the tests do it before starting any process.
    sw.Restart();
    IntPtr probe;
    using (var d = new ObsApi.Data()) probe = ObsApi.obs_source_create(ObsApi.U("game_capture"), ObsApi.U("hook setup"), d.Ptr, IntPtr.Zero);
    Check("win-capture's hook setup has finished (" + sw.ElapsedMilliseconds + " ms)", probe != IntPtr.Zero);
    if (probe != IntPtr.Zero) ObsApi.obs_source_release(probe);
  }

  // ------------------------------------------------------------------------------------------------ end to end
  static void EndToEnd(Watcher watch) {
    Console.WriteLine("== end to end");
    // the windows these tests own: one that moves, two that don't (they make the list look like a list)
    var moving = RemoteWindow.Open(Scene.Simple, 1280, 720, true, WindowPrefix + ": moving bar and frame counter");
    var still1 = RemoteWindow.Open(Scene.Static, 800, 450, true, WindowPrefix + ": drawn once (800 x 450)");
    var still2 = RemoteWindow.Open(Scene.Static, 640, 360, true, WindowPrefix + ": drawn once (640 x 360)");
    uint own = (uint)Process.GetCurrentProcess().Id;
    Native.ExcludePid = 0;   // the test windows are this process's own
    Func<WindowInfo, bool> mine = RemoteWindow.Owns;
    var form = OpenRecorder(mine, Path.Combine(outDir, "Recordings"));
    watch.Quiet(form.Handle);
    try {
      Check("the Recorder's window opened and named an encoder", WaitFor(delegate { return form.EncoderReady; }, 10000, "encoder"));
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

      // the main recording: 20 seconds of the moving test window through the real window controls, with window capture (it is a plain
      // GDI window: game capture is for Direct3D, OpenGL and Vulkan programs)
      Invoke(form, delegate { form.Config = new AppSettings { CaptureMethod = "window" }; });
      var clock = Stopwatch.StartNew();
      Invoke(form, delegate { form.PressRecord(); });
      Check("pressing Record starts a recording (first picture arrived)", WaitFor(delegate { return form.Mode == MainForm.State.Recording; }, 30000, "recording"), form.StatusText);
      Check("while recording the button says Stop", form.RecordText == "Stop");
      var rec = form.Current;
      Check("the capture in use is window capture, as asked", rec != null && rec.Capture == "window", rec == null ? "none" : rec.Capture);
      if (rec == null) return;
      WaitFor(delegate { return rec.Elapsed.TotalSeconds >= 8; }, 20000, "8 s");
      string health8 = rec.Health, chip8 = form.ChipTextNow, status8 = form.StatusText;
      Shot(form.Handle, "rec-01-recording.png", true);
      Note("at 8 s: " + health8 + " | chip: " + chip8 + " | window said: " + status8);
      Check("the status line says Keeping up", health8 == "Keeping up", health8);
      Check("the chip shows the engine's encoder and the active set", chip8 == "HEVC · NVENC · light · native · window · GPU normal", chip8);
      string logPath = rec.LogPath;
      WaitFor(delegate { return rec.Elapsed.TotalSeconds >= 20; }, 40000, "20 s");
      double elapsed = rec.Elapsed.TotalSeconds;
      Invoke(form, delegate { form.PressRecord(); });
      Check("pressing Stop saves and the window goes back to idle", WaitFor(delegate { return form.Mode == MainForm.State.Idle && form.LastResult != null; }, 60000, "saved"), form.StatusText);
      var res = form.LastResult;
      Shot(form.Handle, "rec-02-saved.png", true);
      Note("the Recorder took " + clock.Elapsed.TotalSeconds.ToString("0.0") + " s from Record to saved");
      CheckMainRecording(res, elapsed, form);
      CheckLog(logPath, "main recording");

      GameCapture(form, watch, mine);

      // the window closing under a recording
      Console.WriteLine("-- the window closes while recording");
      var doomed = RemoteWindow.Open(Scene.Simple, 640, 360, true, WindowPrefix + ": about to close");
      Invoke(form, delegate { form.Config = new AppSettings { CaptureMethod = "window" }; form.ReloadWindows(); form.SelectHwnd(doomed.Handle); });
      form.LastResult = null;
      Invoke(form, delegate { form.PressRecord(); });
      Check("closing test: recording started", WaitFor(delegate { return form.Mode == MainForm.State.Recording; }, 30000, "recording"), form.StatusText);
      Thread.Sleep(4000);
      doomed.CloseWindow();
      Check("closing the window stops the recording and saves it cleanly by itself", WaitFor(delegate { return form.Mode == MainForm.State.Idle && form.LastResult != null; }, 30000, "auto stop"), form.StatusText);
      var closed = form.LastResult;
      Check("the closed window's recording is a good .mp4 of about 4 seconds, not forced", closed != null && closed.Ok && !closed.Forced && closed.Seconds > 2.5 && closed.Seconds < 6.5, closed == null ? "null" : closed.Message + " " + closed.Seconds.ToString("0.0"));

      // the quality set, scaled to 360 high, and three audio tracks (the mic is a device that doesn't exist: nobody's mic is opened)
      Console.WriteLine("-- quality encoder, 360p, three audio tracks");
      var swin = RemoteWindow.Open(Scene.Simple, 1280, 720, true, WindowPrefix + ": quality, scaled, three tracks");
      Invoke(form, delegate { form.Config = new AppSettings { EncoderProfile = "quality", MaxHeight = 360, CaptureMethod = "window", Mic = "on" }; form.ReloadWindows(); form.SelectHwnd(swin.Handle); });
      form.LastResult = null;
      Invoke(form, delegate { form.PressRecord(); });
      Check("quality: recording started", WaitFor(delegate { return form.Mode == MainForm.State.Recording; }, 30000, "recording"), form.StatusText);
      var srec = form.Current;
      Thread.Sleep(5000);
      Check("quality: the chip shows quality, 360p, window and mic", form.ChipTextNow == "HEVC · NVENC · quality · 360p · window · mic · GPU normal", form.ChipTextNow);
      Shot(form.Handle, "rec-03-chip-quality-scaled-mic.png", true);
      string slog = srec != null ? srec.LogPath : null;
      Invoke(form, delegate { form.PressRecord(); });
      Check("quality: Stop saves", WaitFor(delegate { return form.Mode == MainForm.State.Idle && form.LastResult != null; }, 60000, "saved"), form.StatusText);
      var sres = form.LastResult;
      if (sres != null && sres.Ok) {
        var sp = ProbeResult.Run(sres.Mp4, true);
        Check("quality: the video is HEVC, 640 x 360 (scaled on the GPU by libobs), 60/1, about 5 s", sp.Codec == "hevc" && sp.Width == 640 && sp.Height == 360 && sp.FrameRate == "60/1" && Math.Abs(sp.Duration - 5) < 1.5, sp.Codec + " " + sp.Width + "x" + sp.Height + " " + sp.FrameRate + " " + sp.Duration.ToString("0.00"));
        var titles = new List<string>(); foreach (var a in sp.Audio) titles.Add(a.Title ?? a.Handler ?? "?");
        Check("quality: three AAC tracks, 48 kHz stereo, named Mix, Game, Mic in that order", sp.Audio.Count == 3 && string.Join(",", titles.ToArray()) == "Mix,Game,Mic" && sp.Audio.TrueForAll(delegate(AudioStream a) { return a.Codec == "aac" && a.SampleRate == 48000 && a.Channels == 2; }), sp.AudioSummary());
        string spng = Path.Combine(throwDir, "scaled-frame.png");
        if (Tools.Still(sres.Mp4, 3, spng)) using (var b = new Bitmap(spng)) {
          int worst = 0;
          for (int i = 0; i < TestWindow.Swatches.Length; i++) {   // the swatches, half their size at 360 high: centre of each
            var want = TestWindow.Swatches[i]; var got = b.GetPixel((20 + i * TestWindow.SwatchWidth + TestWindow.SwatchWidth / 2) / 2, (360 * 2 - TestWindow.SwatchHeight - 20 + TestWindow.SwatchHeight / 2) / 2);
            worst = Math.Max(worst, Math.Max(Math.Abs(got.R - want.R), Math.Max(Math.Abs(got.G - want.G), Math.Abs(got.B - want.B))));
          }
          Check("quality: the scaled picture still has the right colours (worst error " + worst + " of 255)", worst <= 10);
        }
      }
      string stext = slog != null && File.Exists(slog) ? File.ReadAllText(slog) : "";
      Check("quality: the log records the settings, the scaling, the quality set and the three tracks", stext.Contains("settings: EncoderProfile=quality MaxHeight=360 CaptureMethod=window Sound=game Mic=on GpuPriority=normal") && stext.Contains("(scaled by MaxHeight)") && stext.Contains("quality set") && stext.Contains("tracks Mix, Game, Mic"), slog);
      swin.CloseWindow();

      // game capture asked for on a plain window: nothing to hook, so the Recorder moves to window capture
      Console.WriteLine("-- game capture on a plain window");
      var plain = RemoteWindow.Open(Scene.Simple, 640, 360, true, WindowPrefix + ": no Direct3D here");
      Invoke(form, delegate { form.Config = new AppSettings(); form.ReloadWindows(); form.SelectHwnd(plain.Handle); });
      form.LastResult = null;
      Invoke(form, delegate { form.PressRecord(); });
      bool fbStarted = WaitFor(delegate { return form.Mode == MainForm.State.Recording; }, 30000, "recording");
      var fb = form.Current;
      Check("game capture on a plain window still records, in window capture, and says so", fbStarted && fb != null && fb.Capture == "window" && form.StatusText.Contains("using window capture"), form.StatusText);
      if (fbStarted) {
        Thread.Sleep(3000);
        Invoke(form, delegate { form.PressRecord(); });
        Check("and it saves", WaitFor(delegate { return form.Mode == MainForm.State.Idle && form.LastResult != null && form.LastResult.Ok; }, 30000, "saved"), form.LastResult == null ? form.StatusText : form.LastResult.Message);
      }
      plain.CloseWindow();
      Invoke(form, delegate { form.Config = new AppSettings(); });

      // the Recorder itself closed under a recording: it finishes the file first, then closes
      Console.WriteLine("-- the Recorder's own window is closed while recording");
      var keep = RemoteWindow.Open(Scene.Simple, 640, 360, true, WindowPrefix + ": recorder closes");
      Invoke(form, delegate { form.Config = new AppSettings { CaptureMethod = "window" }; form.ReloadWindows(); form.SelectHwnd(keep.Handle); });
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
    Check("the Recordings folder holds only .mp4 files (no .mkv, no leftovers)", Directory.GetFiles(Path.Combine(outDir, "Recordings")).Length == Directory.GetFiles(Path.Combine(outDir, "Recordings"), "*.mp4").Length);

    OtherRecordings();
  }

  // Game capture of FakeGame: a Direct3D 11 window with a flip-model swap chain, like a game, hooked by OBS's own signed hook. FakeGame sits on
  // the primary monitor at the bottom of the window order, invisible (alpha 1/255) and click-through (a small window, a light load, vsync on).
  static void GameCapture(MainForm form, Watcher watch, Func<WindowInfo, bool> mine) {
    Console.WriteLine("-- game capture of FakeGame (silent: alpha 1/255, click-through, never focused, no taskbar button)");
    Diag.Game g = null;
    try {
      try { g = Diag.Game.Start(build, "windowed", "1280x720", "20", 1, Path.Combine(outDir, "fakegame.log")); }
      catch (Exception ex) { Check("FakeGame started", false, ex.Message); return; }
      watch.Allow(g.Pid); watch.Silent(g.Hwnd);
      Note("FakeGame: " + g.Ready);
      Check("FakeGame is silent: it was never given the foreground and sits at alpha 1/255", g.Ready.Contains("foreground=False") && g.Ready.Contains("alpha=1 "), g.Ready);
      var gamePid = (uint)g.Pid;
      Invoke(form, delegate { form.WindowFilter = delegate(WindowInfo w) { return mine(w) || w.ProcessId == gamePid; }; form.Config = new AppSettings(); form.ReloadWindows(); });
      Check("FakeGame's window is in the list and selectable", Invoke(form, delegate { return form.SelectHwnd(g.Hwnd); }));
      form.LastResult = null;
      Invoke(form, delegate { form.PressRecord(); });
      Check("game capture: recording started", WaitFor(delegate { return form.Mode == MainForm.State.Recording; }, 30000, "recording"), form.StatusText);
      var rec = form.Current;
      Check("game capture: OBS's hook took FakeGame's frames (the capture stayed game capture)", rec != null && rec.Capture == "game", rec == null ? "none" : rec.Capture + " " + form.StatusText);
      Thread.Sleep(7000);
      string health = rec == null ? "" : rec.Health;
      Check("game capture: Keeping up", health == "Keeping up", health);
      Check("game capture: the chip says game capture", form.ChipTextNow == "HEVC · NVENC · light · native · game capture · GPU normal", form.ChipTextNow);
      Shot(form.Handle, "rec-04-game-capture.png", true);
      string log = rec == null ? null : rec.LogPath;
      Invoke(form, delegate { form.PressRecord(); });
      Check("game capture: Stop saves", WaitFor(delegate { return form.Mode == MainForm.State.Idle && form.LastResult != null; }, 60000, "saved"), form.StatusText);
      var res = form.LastResult;
      if (res != null && res.Ok) {
        var p = ProbeResult.Run(res.Mp4, true);
        Check("game capture: HEVC, 1280 x 720 (FakeGame's swap chain), 60/1, about 7 s, frames matching the time", p.Codec == "hevc" && p.Width == 1280 && p.Height == 720 && p.FrameRate == "60/1" && Math.Abs(p.Duration - 7) < 1.5 && Math.Abs(p.Frames - p.VideoDuration * 60) <= 3,
          p.Codec + " " + p.Width + "x" + p.Height + " " + p.FrameRate + " " + p.Duration.ToString("0.00") + " s " + p.Frames + " frames");
        int total, distinct = Diag.DistinctFrames(res.Mp4, out total);
        Check("game capture: the frames are FakeGame's own, each one new (" + distinct + " different pictures in " + total + " frames)", total > 0 && distinct >= total * 0.9, distinct + "/" + total);
        string png = Path.Combine(throwDir, "game-frame.png");
        if (Tools.Still(res.Mp4, 3, png)) using (var b = new Bitmap(png)) {
          long sum = 0; int n = 0;
          for (int y = 0; y < b.Height; y += 16) for (int x = 0; x < b.Width; x += 16) { var c = b.GetPixel(x, y); sum += c.R + c.G + c.B; n++; }
          Check("game capture: the picture is FakeGame's (not black)", sum / (3.0 * n) > 20, (sum / (3.0 * n)).ToString("0.0"));
          File.Copy(png, Path.Combine(shotsDir, "rec-05-game-capture-frame.png"), true);
        }
      }
      string text = log != null && File.Exists(log) ? File.ReadAllText(log) : "";
      Check("game capture: the log has libobs's own game capture lines (the hook going in)", text.Contains("game-capture") && text.ToLowerInvariant().Contains("hook"), log);
      foreach (string l in text.Split('\n')) if (l.Contains("game-capture")) Note("log: " + l.Trim());
    } finally {
      if (g != null) g.Stop();
      Invoke(form, delegate { form.WindowFilter = mine; form.Config = new AppSettings(); form.ReloadWindows(); });
    }
  }

  // The recording's log: where it is, what it says.
  static void CheckLog(string logPath, string what) {
    string logs = Path.Combine(outDir, "logs");
    Check(what + ": a log was written to the logs folder, and none to the recordings folder", logPath != null && File.Exists(logPath) && Path.GetDirectoryName(logPath) == logs && Directory.GetFiles(Path.Combine(outDir, "Recordings"), "*.log").Length == 0, logPath);
    if (logPath == null || !File.Exists(logPath)) return;
    string text = File.ReadAllText(logPath);
    int lines = 0, obs = 0;
    foreach (string l in text.Split('\n')) { if (l.Contains(" late=")) lines++; if (l.Contains("s  obs ")) obs++; }
    Check(what + ": the log has the start, the engine's version, the settings, the canvas, the encoder and the file", text.Contains("started 20") && text.Contains("with libobs 32.2.2") && text.Contains("settings: EncoderProfile=standard MaxHeight=0 CaptureMethod=window Sound=game Mic=off GpuPriority=normal") &&
      text.Contains("canvas 1280x720, video 1280x720") && text.Contains("encoder obs_nvenc_hevc_tex") && text.Contains("first picture after") && text.Contains("recording to "));
    Check(what + ": about one progress line a second (" + lines + ")", lines >= 15, lines.ToString());
    Check(what + ": libobs's own messages are in it (" + obs + ")", obs >= 3, obs.ToString());
    Check(what + ": the log says Stop was pressed, that the output stopped with code 0 and not by force, and how long Stop took", text.Contains("Stop pressed") && text.Contains("code 0") && !text.Contains(", forced") && text.Contains("Stop took"));
    Check(what + ": the GPU priority was left as it is (the default), with no D3DKMT call", text.Contains("gpu priority: left as it is (GpuPriority=normal)") && !text.Contains("D3DKMT"));
    foreach (string l in text.Split('\n')) if (l.Contains("encoder ") || l.Contains("Stop pressed") || l.Contains("output stopped") || l.Contains("first picture")) Note("log: " + l.Trim());
  }

  static void CheckMainRecording(RecordingResult res, double elapsed, MainForm form) {
    Check("recording saved: " + (res == null ? "?" : res.Message), res != null && res.Ok && res.Mp4 != null && File.Exists(res.Mp4), res == null ? null : res.Message);
    if (res == null || !res.Ok) return;
    string friendly = FileNames.Friendly(Application.ExecutablePath, WindowPrefix + ": moving bar and frame counter");
    Check("the file is named <friendly name> yyyy-MM-dd HH-mm-ss.mp4 (the test exe has no FileDescription, so it's the window's title)",
      System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(res.Mp4), "^" + System.Text.RegularExpressions.Regex.Escape(friendly) + @" \d{4}-\d\d-\d\d \d\d-\d\d-\d\d\.mp4$"), Path.GetFileName(res.Mp4));
    Check("it went to the Recordings folder", Path.GetDirectoryName(res.Mp4) == Path.Combine(outDir, "Recordings"));
    var p = ProbeResult.Run(res.Mp4, true);
    Check("ffprobe reads the .mp4 clean", p.Ok, p.Error);
    Check("video is hevc, Main profile, 8-bit 4:2:0", p.Codec == "hevc" && p.Profile == "Main" && p.PixFmt == "yuv420p", p.Codec + " " + p.Profile + " " + p.PixFmt);
    Check("r_frame_rate is 60/1", p.FrameRate == "60/1", p.FrameRate + " (avg " + p.AvgFrameRate + ")");
    Check("duration is about 20 s", Math.Abs(p.Duration - 20.0) < 1.0, p.Duration.ToString("0.000"));
    Check("frame count is about 1200 (within 5%)", Math.Abs(p.Frames - 1200) <= 60, p.Frames.ToString());
    Check("frame count matches the video's duration at 60 fps (within 2 frames)", Math.Abs(p.Frames - p.VideoDuration * 60) <= 2, p.Frames + " frames, " + p.VideoDuration.ToString("0.000") + " s = " + (p.VideoDuration * 60).ToString("0.0"));
    Check("the frames libobs counted match the file within 5 (its counter runs a few frames past the last one muxed at Stop)", Math.Abs(res.Frames - p.Frames) <= 5, res.Frames + " vs " + p.Frames);
    Check("picture is the window's own area: 1280 x 720", p.Width == 1280 && p.Height == 720, p.Width + "x" + p.Height);
    Check("tagged BT.709, limited range (libobs converts to NV12 BT.709 on the GPU)", p.Range == "tv" && p.Primaries == "bt709" && p.Transfer == "bt709" && p.Space == "bt709", p.Range + " " + p.Primaries + " " + p.Transfer + " " + p.Space);
    Note("MP4 tag: " + p.Tag);
    var a = p.Audio.Count > 0 ? p.Audio[0] : null;
    Check("one audio track, Game: AAC 48 kHz stereo (the program's own sound: here the silent test process)", p.Audio.Count == 1 && a.Codec == "aac" && a.SampleRate == 48000 && a.Channels == 2 && (a.Title == "Game" || a.Handler == "Game"), p.AudioSummary());
    if (a != null) Check("the sound is as long as the picture (within 0.3 s)", Math.Abs(a.Duration - p.VideoDuration) < 0.3, a.Duration.ToString("0.000") + " vs " + p.VideoDuration.ToString("0.000"));
    Note("MEASURED  frames=" + p.Frames + "  duration=" + p.Duration.ToString("0.000") + " s  elapsed(app)=" + elapsed.ToString("0.000") + " s  size=" + (res.Bytes / 1048576.0).ToString("0.00") +
         " MB  late=" + res.Lagged + "  skipped=" + res.Skipped + "  " + p.AudioSummary());
    Note("the Recorder's own report: " + form.StatusText);

    // a still from the recording, and its colours against what the window drew
    string png = Path.Combine(throwDir, "recorded-frame.png");
    bool made = Tools.Still(res.Mp4, 10, png);
    Check("a still frame at 10 s was pulled from the recorded .mp4", made);
    if (made) { CheckColours(png, 1280, 720, "the recorded HEVC"); File.Copy(png, Path.Combine(shotsDir, "rec-06-recorded-frame.png"), true); }
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

  // A recording straight through the Recording class, the way the Recorder's window makes one: canvas first, then Start.
  static Recording Rec(IntPtr hwnd, string folder, string name, AppSettings cfg, EncoderProfile enc, out bool started, out string err) {
    WindowInfo w = null;
    foreach (var x in Native.ListWindows()) if (x.Handle == hwnd) w = x;
    if (w == null) w = new WindowInfo { Handle = hwnd, Title = name, ClassName = "", ExeName = "" };
    var sz = Native.ClientSize(hwnd);
    var rec = new Recording(enc, w, sz.Width > 0 ? sz.Width : 640, sz.Height > 0 ? sz.Height : 360, folder, name, cfg ?? new AppSettings { CaptureMethod = "window" }, Path.Combine(outDir, "logs"));
    err = Obs.Start(rec.BaseWidth, rec.BaseHeight, rec.OutWidth, rec.OutHeight);
    started = err == null && rec.Start(out err, 15000);
    return rec;
  }

  // Stalls, windows that don't draw, a resize, and the software encoder: short recordings straight through the Recording class.
  static void OtherRecordings() {
    Console.WriteLine("-- recordings that stress the edges");
    string folder = Path.Combine(outDir, "edges");
    string err; bool started;

    // a window that stops drawing mid-recording: libobs keeps the clock and repeats the last picture
    var w1 = RemoteWindow.Open(Scene.Simple, 640, 360, true, WindowPrefix + ": stalls");
    var r1 = Rec(w1.Handle, folder, "stall", null, null, out started, out err);
    Check("stall: recording started", started, err);
    if (started) {
      Thread.Sleep(2500);
      Check("stall: the window stopped drawing", w1.Freeze());
      Thread.Sleep(4000);
      var sw = Stopwatch.StartNew();
      var res = r1.Stop();
      Check("stall: Stop finishes, not by force, in under 5 s", res.Ok && !res.Forced && sw.Elapsed.TotalSeconds < 5, res.Message + " forced=" + res.Forced + " " + sw.Elapsed.TotalSeconds.ToString("0.0") + "s");
      if (res.Ok) {
        var p = ProbeResult.Run(res.Mp4, true);
        Check("stall: the file covers the whole 6.5 s at 60/1, a constant frame count for the time (repeated pictures fill the stall)", p.Ok && p.Duration > 5.5 && Math.Abs(p.Frames - p.VideoDuration * 60) <= 3 && p.FrameRate == "60/1", p.Frames + " frames " + p.VideoDuration.ToString("0.00") + " s " + p.FrameRate);
      }
    }
    w1.CloseWindow();

    // a window that has drawn once and never again
    var w2 = RemoteWindow.Open(Scene.Static, 640, 360, true, WindowPrefix + ": never redraws");
    var r2 = Rec(w2.Handle, folder, "static", null, null, out started, out err);
    Check("static window: recording starts", started, err);
    if (started) {
      Thread.Sleep(3000);
      var res = r2.Stop();
      Check("static window: saved as a good .mp4", res.Ok && !res.Forced, res.Message + " forced=" + res.Forced);
      if (res.Ok) Note("static window recording: " + res.Seconds.ToString("0.00") + " s, " + res.Frames + " frames");
    }
    w2.CloseWindow();

    // a window that's resized while recorded keeps the video's size (the picture is fitted into the canvas)
    var w3 = RemoteWindow.Open(Scene.Simple, 640, 360, true, WindowPrefix + ": resized");
    var r3 = Rec(w3.Handle, folder, "resize", null, null, out started, out err);
    Check("resize: recording started", started, err);
    if (started) {
      Thread.Sleep(2000);
      Check("resize: the window grew to 960 x 540 mid-recording", w3.Resize(960, 540) && Native.ClientSize(w3.Handle) == new Size(960, 540), Native.ClientSize(w3.Handle).ToString());
      Thread.Sleep(3000);
      var res = r3.Stop();
      Check("resize: saved as a good .mp4", res.Ok && !res.Forced, res.Message);
      if (res.Ok) {
        var p = ProbeResult.Run(res.Mp4, false);
        Check("resize: the video stays the size it started at (640 x 360)", p.Width == 640 && p.Height == 360, p.Width + "x" + p.Height);
      }
    }
    w3.CloseWindow();

    // the software encoder, end to end: the way out when no GPU encoder works
    var w4 = RemoteWindow.Open(Scene.Simple, 1280, 720, true, WindowPrefix + ": software encoder");
    var r4 = Rec(w4.Handle, folder, "software", null, EncoderProfile.Software, out started, out err);
    Check("software: recording started", started, err);
    if (started) {
      Check("software: the encoder in use is x264", r4.Encoder == EncoderProfile.Software, r4.Encoder == null ? "none" : r4.Encoder.Name);
      Thread.Sleep(6000);
      var res = r4.Stop();
      Check("software: saved as a good .mp4", res.Ok && !res.Forced, res.Message);
      if (res.Ok) {
        var p = ProbeResult.Run(res.Mp4, true);
        Check("software: h264, 60/1, about 6 s, tagged BT.709 tv", p.Codec == "h264" && p.FrameRate == "60/1" && Math.Abs(p.Duration - 6) < 1.5 && p.Space == "bt709" && p.Range == "tv", p.Codec + " " + p.FrameRate + " " + p.Duration.ToString("0.00") + " " + p.Space + " " + p.Range);
        string png = Path.Combine(throwDir, "software.png");
        if (Tools.Still(res.Mp4, 3, png)) CheckColours(png, 1280, 720, "the software H.264");
        Note("software encoder: " + (res.Bytes / 1048576.0).ToString("0.00") + " MB for " + res.Seconds.ToString("0.0") + " s");
      }
    }
    w4.CloseWindow();

    // the Recorder ended outright mid-recording (a crash, End Task): CHANGELOG 0.2.0 says the MP4 "still plays if the PC crashes"
    var w5 = RemoteWindow.Open(Scene.Simple, 640, 360, true, WindowPrefix + ": recorder killed");
    var crash = Proc.Start(Path.Combine(build, "RecorderTests.exe"), "--crash-child " + w5.Handle.ToInt64() + " " + Tools.Quote(folder) + " 6", false);
    var firstLine = crash.StandardOutput.ReadLine() ?? "";
    string crashFile = firstLine.StartsWith("RECORDING=") ? firstLine.Substring(10) : null;
    Check("crash test: the child recorder started recording", crashFile != null, firstLine);
    if (crashFile != null) {
      bool died = crash.WaitForExit(30000);
      Check("crash test: the recorder was ended outright, with no Stop", died && crash.ExitCode != 0, "exit " + (died ? crash.ExitCode.ToString() : "still running"));
      Thread.Sleep(500);
      var cp = File.Exists(crashFile) ? ProbeResult.Run(crashFile, true) : null;
      Check("crash test: the unfinished .mp4 still plays: video and audio, about 6 s (at least 3 s)", cp != null && cp.Ok && cp.Audio.Count == 1 && cp.Duration > 3, cp == null ? "no file" : (cp.Ok ? cp.Frames + " frames " + cp.Duration.ToString("0.00") + " s " + cp.AudioSummary() : cp.Error));
    }
    w5.CloseWindow();

    // a recordings folder that exists but can't be written to (denied by its permissions): the recording must not start, the message must
    // point at the folder and not at the encoders, and nothing may be left behind
    var w6 = RemoteWindow.Open(Scene.Simple, 640, 360, true, WindowPrefix + ": folder can't be written");
    string denied = Path.Combine(outDir, "denied");
    Directory.CreateDirectory(denied);
    string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value, aso, ase;
    string icacls = Path.Combine(Environment.SystemDirectory, "icacls.exe");
    int denyCode = Proc.Run(icacls, Tools.Quote(denied) + " /deny *" + sid + ":(OI)(CI)(WD,AD)", 20000, out aso, out ase);
    Check("unwritable folder: the folder was made unwritable for this test (icacls deny)", denyCode == 0, denyCode + " " + Tools.Last(aso + ase, 2));
    var r6 = Rec(w6.Handle, denied, "denied", null, null, out started, out err);
    Note("unwritable folder: Start said: " + err);
    Check("unwritable folder: the recording does not start", !started, err);
    Check("unwritable folder: the message names the folder, not the encoders", err != null && err.IndexOf("folder", StringComparison.OrdinalIgnoreCase) >= 0 && !err.StartsWith("No encoder"), err);
    Proc.Run(icacls, Tools.Quote(denied) + " /remove:d *" + sid, 20000, out aso, out ase);
    string deniedLog = r6.LogPath != null && File.Exists(r6.LogPath) ? File.ReadAllText(r6.LogPath) : "";
    Check("unwritable folder: no encoder was tried (no GPU encoder session opened for a folder fault)", deniedLog.Length > 0 && !deniedLog.Contains("encoder obs_") && !deniedLog.Contains("encoder h265_"), Tools.Last(deniedLog, 2));
    Check("unwritable folder: nothing was left in it", Directory.GetFileSystemEntries(denied).Length == 0, string.Join(",", Directory.GetFileSystemEntries(denied)));
    w6.CloseWindow();

    // the output stopping by itself (what a full disk or a failed encoder does): the Recorder is told, and Stop afterwards still ends cleanly
    var w7 = RemoteWindow.Open(Scene.Simple, 640, 360, true, WindowPrefix + ": output stops by itself");
    var r7 = Rec(w7.Handle, folder, "selfstop", null, null, out started, out err);
    Check("self-stop: recording started", started, err);
    if (started) {
      string why = null;
      r7.Ended += delegate(string s) { why = s; };
      Thread.Sleep(2500);
      var outField = typeof(Recording).GetField("output", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
      ObsApi.obs_output_force_stop((IntPtr)outField.GetValue(r7));   // libobs ends the output without Stop being asked
      Check("self-stop: the Recorder is told the recording stopped by itself within 3 s", WaitFor(delegate { return why != null; }, 3000, "ended") && why.StartsWith("The recording stopped by itself"), why);
      var res7 = r7.Stop();
      Check("self-stop: Stop afterwards returns quickly, not by force, and keeps the file", !res7.Forced && res7.StopSeconds < 5 && res7.Bytes > 0, res7.Message + " forced=" + res7.Forced + " " + res7.StopSeconds.ToString("0.0") + "s " + res7.Bytes + " bytes");
      if (res7.Bytes > 0) {
        var p7 = ProbeResult.Run(r7.Mp4Path, false);
        Check("self-stop: the kept file plays (video, 1 audio track, at least 1.5 s)", p7.Ok && p7.Audio.Count == 1 && p7.Duration > 1.5, p7.Ok ? p7.Duration.ToString("0.00") + " s " + p7.AudioSummary() : p7.Error);
      }
    }
    w7.CloseWindow();

    // a window that doesn't exist: a clear failure, nothing left behind
    var gone = new Recording(null, new WindowInfo { Handle = new IntPtr(0x7FFFF), Title = "gone", ClassName = "", ExeName = "" }, 640, 360, folder, "gone", new AppSettings(), null);
    started = gone.Start(out err, 3000);
    Check("a window that doesn't exist fails with a clear message and leaves nothing", !started && err == "That window has gone." && !File.Exists(Path.Combine(folder, "gone.mp4")), err);
    Check("only .mp4 files are left in the edge-case folder", Directory.GetFiles(folder).Length == Directory.GetFiles(folder, "*.mp4").Length, string.Join(",", Directory.GetFiles(folder)));
  }

  // ------------------------------------------------------------------------------------------------ helpers
  static MainForm OpenRecorder(Func<WindowInfo, bool> filter, string folder) {
    MainForm.Quiet = true;
    MainForm f = null;
    var shown = new ManualResetEvent(false);
    var t = new Thread(delegate() {
      f = new MainForm();
      f.WindowFilter = filter; f.RecordingsFolder = folder; f.LogsFolder = Path.Combine(outDir, "logs");
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

  // A picture of one of the tests' own windows (PrintWindow, full content). keep puts it in docs\screenshotsm1c for the Dev timeline;
  // otherwise it goes to build\test-output\shots and is thrown away with the rest of the test output.
  static void Shot(IntPtr h, string name, bool keep = false) {
    Bitmap bmp = null;
    var ctl = Control.FromHandle(h);
    if (ctl != null && ctl.InvokeRequired) bmp = (Bitmap)ctl.Invoke(new Func<Bitmap>(delegate { return Native.Picture(h); }));
    else bmp = Native.Picture(h);
    Check("screenshot " + name + " taken of the tests' own window", bmp != null);
    if (bmp == null) return;
    bmp.Save(Path.Combine(keep ? shotsDir : throwDir, name), ImageFormat.Png);
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
    readonly HashSet<uint> allowed = new HashSet<uint>();   // children whose window is an ordinary visible one at alpha 1/255 (FakeGame): not flagged as a child's window
    readonly List<IntPtr> silent = new List<IntPtr>();       // FakeGame's window: held to the same rules as the test windows
    Thread t; volatile bool stop;
    public int Looks;

    public void Quiet(IntPtr h) { lock (quiet) quiet.Add(h); }
    public void Allow(int pid) { lock (allowed) allowed.Add((uint)pid); }
    public void Silent(IntPtr h) { lock (silent) silent.Add(h); }
    public void Start() { t = new Thread(Loop) { IsBackground = true, Name = "window watcher" }; t.Start(); }
    public List<string> Stop() { stop = true; t.Join(3000); lock (violations) return new List<string>(violations); }

    void Loop() {
      while (!stop) {
        HashSet<uint> pids;
        lock (Proc.Started) { pids = new HashSet<uint>(); foreach (int p in Proc.Started) pids.Add((uint)p); }
        lock (allowed) pids.ExceptWith(allowed);
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
        // the recorded test windows in their own processes: opaque by design, but no-activate, never topmost, never in front
        IntPtr fg = GetForegroundWindow();
        lock (RemoteWindow.Handles) foreach (var h in RemoteWindow.Handles) {
          if (!Native.IsWindow(h)) continue;
          int ex = GetWindowLong(h, -20);
          if ((ex & 0x8) != 0) Add("a recorded test window was topmost");
          if ((ex & 0x08000000) == 0) Add("a recorded test window could take the focus (no WS_EX_NOACTIVATE)");
          if (h == fg) Add("a recorded test window took the focus");
        }
        lock (silent) foreach (var h in silent) {
          if (!Native.IsWindow(h)) continue;
          int ex = GetWindowLong(h, -20);
          if ((ex & 0x8) != 0) Add("FakeGame's window was topmost");
          if ((ex & (0x80000 | 0x20 | 0x08000000)) != (0x80000 | 0x20 | 0x08000000)) Add("FakeGame's window wasn't layered, click-through and no-activate");
          uint key, flags; byte alpha;
          if (GetLayeredWindowAttributes(h, out key, out alpha, out flags) && (flags & 2) != 0 && alpha > 2) Add("FakeGame's window was visible (alpha " + alpha + ")");
          if (h == fg) Add("FakeGame's window took the focus");
        }
        uint fpid; GetWindowThreadProcessId(GetForegroundWindow(), out fpid);
        if (fpid == (uint)Process.GetCurrentProcess().Id) Add("a window of the tests took the focus");
        Looks++;
        Thread.Sleep(50);
      }
    }

    void Add(string v) { lock (violations) if (!violations.Contains(v)) violations.Add(v); }
  }
}
