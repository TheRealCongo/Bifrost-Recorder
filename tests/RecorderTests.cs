// SPDX-License-Identifier: GPL-2.0-or-later
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
  static string project, build, outDir, shotsDir, throwDir;
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
    shotsDir = Path.Combine(project, "docs", "screenshots", "m1c");   // the pictures kept for the Dev timeline
    throwDir = Path.Combine(outDir, "shots");                               // the other pictures the tests take, not kept
    TestWindow.LayerAlpha = 1;

    if (args.Length > 0 && args[0] == "--job-child") return JobChild();   // started by a run that already passed the guard; CPU only
    if (args.Length > 0 && args[0] == "--guard-check") { Console.WriteLine("game running: " + (RunningGame() ?? "none")); return 0; }   // reads the process list only; nothing else
    if (args.Length > 0 && args[0] == "--guard-test") { bool go = Guard(); Console.WriteLine("guard says: " + (go ? "go" : "refuse")); return go ? 0 : 2; }   // the guard and nothing after it
    if (args.Length > 0 && args[0] == "--unit") return UnitOnly();   // no GPU, no windows: safe while a game runs
    if (!Guard()) return 2;
    GpuSafety.Begin();
    if (args.Length > 0 && args[0] == "--ui") return UiOnly();      // the Recorder's windows (off every monitor), no encoder started: guarded like the GPU modes since a window could
    if (args.Length > 0 && args[0] == "--bench") return Bench(args);
    if (args.Length > 0 && args[0] == "--diag") return Diag.Run(args, build);
    if (args.Length > 0 && args[0] == "--hold") return Hold(args);

    if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    Directory.CreateDirectory(outDir);
    Directory.CreateDirectory(shotsDir); Directory.CreateDirectory(throwDir);
    Console.WriteLine("Counts before: " + Counts());
    var watch = new Watcher(); watch.Start();
    try {
      Units(true);
      GpuSafety.Check("the unit tests");
      EndToEnd(watch);
      GpuSafety.Check("the end-to-end tests");
      UiTests(watch);
      GpuSafety.Check("the window tests");
    } catch (Exception ex) { Check("the run finished", false, ex.ToString()); }
    var violations = watch.Stop();
    lock (TestWindow.Placed) foreach (var p in TestWindow.Placed) Note("placed: " + p);
    string wantScreen = TestWindow.OnSecondary ? "(secondary)" : "(primary)";
    bool allPlaced = true;
    lock (TestWindow.Placed) foreach (var p in TestWindow.Placed) if (!p.Contains(wantScreen)) allPlaced = false;
    Check("every test window opened fully inside the " + (TestWindow.OnSecondary ? "secondary" : "primary") + " monitor, as asked", TestWindow.Placed.Count > 0 && allPlaced, TestWindow.Placed.Count + " windows");
    Check("no window of a child process was ever visible, and the Recorder's own window never touched a monitor", violations.Count == 0 && watch.Looks > 20,
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
  // --unit: only the checks that need neither the GPU nor a window (so they may run next to a game): no test encode, no capture.
  static int UnitOnly() {
    if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
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
    Check("NVENC: HEVC Main, constant quality 24 in VBR with a 30000k ceiling", rec.Contains("-c:v hevc_nvenc") && rec.Contains("-profile:v main") && rec.Contains("-rc vbr -cq 24 -b:v 0 -maxrate 30000k -bufsize 30000k"));
    Check("NVENC: preset p4, tune hq, no spatial AQ, no B-frames (the light default)", rec.Contains("-preset p4 -tune hq") && rec.Contains("-spatial-aq 0") && rec.Contains("-bf 0"));
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

    UnitsM1b(tmp);
    UnitsM1c(tmp);
    UnitsArmory(tmp);
    UnitsHotkey(tmp);
    UnitsEncoderCheck(tmp);
    UnitsPrefs(tmp);
    UnitsTheme();
    GpuPriorityApiTest();
    JobTest();
    if (gpu) EncoderTest();
  }

  // ------------------------------------------------------------------------------------------------ M1b: switches, telemetry, log
  static void UnitsM1b(string tmp) {
    // the three experiment switches in BifrostRecorder.settings.json
    string sw = Path.Combine(tmp, "switches.json");
    File.WriteAllText(sw, "{\"ManagedBy\":\"Bifrost Suite\",\"DataFolder\":\"D:\\\\Suite\\\\Bifrost Recorder\",\"EncoderProfile\":\"gentle\",\"MaxHeight\":1080,\"GpuPriority\":\"normal\"}");
    var c = Settings.Load(sw);
    Check("switches are read: gentle (the 0.1.1 name) means standard, 1080, normal, and DataFolder beside them", c.EncoderProfile == "standard" && !c.Quality && c.MaxHeight == 1080 && c.GpuPriority == "normal" && c.DataFolder == @"D:\Suite\Bifrost Recorder", c.EncoderProfile + " " + c.MaxHeight + " " + c.GpuPriority);
    var d = Settings.Load(Path.Combine(tmp, "absent.json"));
    Check("no settings file: standard, native size, GPU normal, no DataFolder", !d.Quality && d.EncoderProfile == "standard" && d.MaxHeight == 0 && d.GpuPriority == "normal" && d.DataFolder == null);
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

    // the two NVENC sets: standard (the light default, what 0.1.1 called gentle) and quality (the older AQ and B-frame set)
    var quality = new AppSettings { EncoderProfile = "quality" };
    string g = FfmpegArgs.Record(EncoderProfile.Nvenc, 1, 1920, 1080, "a.mkv", new AppSettings());
    Check("standard: preset p4, tune hq, VBR cq 24, no B-frames, no spatial AQ, no lookahead, multipass off, 2 s keyframes",
      g.Contains("-c:v hevc_nvenc -preset p4 -tune hq -profile:v main -rc vbr -cq 24 -b:v 0") && g.Contains("-bf 0") && g.Contains("-spatial-aq 0") && g.Contains("-rc-lookahead 0") && g.Contains("-multipass disabled") && g.Contains("-g 120"), g);
    Check("standard: bufsize equals the ceiling (30000k), not double", g.Contains("-maxrate 30000k -bufsize 30000k"));
    Check("standard: no B-frame reference mode, no forced IDR, no spatial AQ on", !g.Contains("-b_ref_mode") && !g.Contains("-forced-idr") && !g.Contains("-spatial-aq 1"));
    string st = FfmpegArgs.Record(EncoderProfile.Nvenc, 1, 1920, 1080, "a.mkv", quality);
    Check("quality is the older set: preset p5, 2 B-frames, spatial AQ, bufsize double, forced IDR", st.Contains("-preset p5") && st.Contains("-bf 2 -b_ref_mode middle") && st.Contains("-spatial-aq 1") && st.Contains("-maxrate 30000k -bufsize 60000k") && st.Contains("-forced-idr 1"));
    Check("the profile only changes NVENC (AMF and QSV arguments are the same either way)",
      FfmpegArgs.Record(EncoderProfile.Amf, 1, 1920, 1080, "a.mkv", quality) == FfmpegArgs.Record(EncoderProfile.Amf, 1, 1920, 1080, "a.mkv") && FfmpegArgs.Record(EncoderProfile.Qsv, 1, 1920, 1080, "a.mkv", quality) == FfmpegArgs.Record(EncoderProfile.Qsv, 1, 1920, 1080, "a.mkv"));

    // MaxHeight: the capture filter scales on the GPU (resize_mode=scale_aspect with an explicit size)
    int w, h; bool scaled;
    FfmpegArgs.OutputSize(2560, 1440, 1080, out w, out h, out scaled); Check("MaxHeight 1080: 2560x1440 becomes 1920x1080", w == 1920 && h == 1080 && scaled);
    FfmpegArgs.OutputSize(2560, 1440, 720, out w, out h, out scaled); Check("MaxHeight 720: 2560x1440 becomes 1280x720", w == 1280 && h == 720 && scaled);
    FfmpegArgs.OutputSize(3440, 1440, 1080, out w, out h, out scaled); Check("MaxHeight 1080: 3440x1440 ultrawide becomes 2580x1080 (shape kept, even)", w == 2580 && h == 1080 && scaled, w + "x" + h);
    FfmpegArgs.OutputSize(1920, 1080, 1080, out w, out h, out scaled); Check("a window already at MaxHeight is not scaled", w == 1920 && h == 1080 && !scaled);
    FfmpegArgs.OutputSize(1280, 720, 1080, out w, out h, out scaled); Check("a window smaller than MaxHeight is never scaled up", w == 1280 && h == 720 && !scaled);
    FfmpegArgs.OutputSize(1279, 719, 0, out w, out h, out scaled); Check("native size is the client area rounded down to even", w == 1278 && h == 718 && !scaled);
    FfmpegArgs.OutputSize(1281, 1441, 1080, out w, out h, out scaled); Check("odd sizes scale to even ones", w % 2 == 0 && h % 2 == 0 && h == 1080 && scaled, w + "x" + h);
    string sc = FfmpegArgs.Record(EncoderProfile.Nvenc, 7, 2560, 1440, "a.mkv", new AppSettings { MaxHeight = 1080 });
    Check("scaled capture: width=1920:height=1080 with resize_mode=scale_aspect, on the GPU (no scale filter, no hwdownload)", sc.Contains("gfxcapture=hwnd=7:max_framerate=60:capture_cursor=true:display_border=false:resize_mode=scale_aspect:width=1920:height=1080") && !sc.Contains("hwdownload") && !sc.Contains("scale_d3d11") && !sc.Contains(",scale="), sc);
    Check("the ceiling follows the scaled size (1080p: 30000k), not the window's", sc.Contains("-maxrate 30000k"));
    Check("unscaled capture keeps width=-2:height=-2", FfmpegArgs.Record(EncoderProfile.Nvenc, 7, 1920, 1080, "a.mkv", new AppSettings { MaxHeight = 1080 }).Contains("width=-2:height=-2"));
    var plan = new Recording(EncoderProfile.Nvenc, IntPtr.Zero, 2560, 1440, tmp, "plan", new AppSettings { MaxHeight = 720 }, null);
    Check("a Recording works out its video size from the settings", plan.OutWidth == 1280 && plan.OutHeight == 720, plan.OutWidth + "x" + plan.OutHeight);

    // the header chip
    Check("chip: standard native", MainForm.ChipText(EncoderProfile.Nvenc, new AppSettings(), null) == "HEVC · NVENC · standard · native · GPU normal", MainForm.ChipText(EncoderProfile.Nvenc, new AppSettings(), null));
    Check("chip: quality 1080p GPU normal", MainForm.ChipText(EncoderProfile.Nvenc, new AppSettings { EncoderProfile = "quality", MaxHeight = 1080, GpuPriority = "normal" }, null) == "HEVC · NVENC · quality · 1080p · GPU normal");
    Check("chip: shows what the GPU actually granted once known", MainForm.ChipText(EncoderProfile.Nvenc, new AppSettings(), "above-normal") == "HEVC · NVENC · standard · native · GPU above-normal");
    Check("chip: software encoder", MainForm.ChipText(EncoderProfile.Software, new AppSettings(), null).StartsWith("H.264 · x264 CPU · standard"));

    // GPU priority names and status formatting (the call itself is made on ffmpeg, in the end-to-end test)
    Check("GPU priority classes are named (4 HIGH, 3 ABOVE_NORMAL, 2 NORMAL)", GpuScheduling.Name(4) == "HIGH" && GpuScheduling.Name(3) == "ABOVE_NORMAL" && GpuScheduling.Name(2) == "NORMAL" && GpuScheduling.Name(5) == "REALTIME");
    Check("NTSTATUS values print in hex", GpuScheduling.Hex(0) == "0x00000000" && GpuScheduling.Hex(unchecked((int)0xC0000022)) == "0xC0000022");
    Check("REALTIME is never one of the classes tried (High=4, AboveNormal=3)", GpuScheduling.High == 4 && GpuScheduling.AboveNormal == 3 && GpuScheduling.High < 5);

    // telemetry: ffmpeg's -progress lines, and when the window says "Keeping up" or "Falling behind"
    var t = new Telemetry();
    foreach (string l in new[] { "frame=120", "fps=59.97", "speed=0.986x", "dup_frames=3", "drop_frames=1", "out_time_us=2000000", "total_size=1048576", "speed=N/A", "out_time_us=N/A", "progress=continue", "garbage" }) t.Feed(l);
    Check("progress lines are read: frame, fps, speed, dup, drop, out_time_us, total_size (N/A and junk ignored)", t.Frames == 120 && Math.Abs(t.Fps - 59.97) < 0.001 && Math.Abs(t.CumulativeSpeed - 0.986) < 0.0001 && t.Dup == 3 && t.Drop == 1 && t.OutUs == 2000000 && t.TotalSize == 1048576);
    Check("lag: 2 s of video written when the clock says 10 s is 8 s behind", Math.Abs(t.LagSeconds(10) - 8.0) < 1e-9, t.LagSeconds(10).ToString());

    var keep = Simulate(delegate(double now) { return now; }, 12.0, true);   // video written as fast as the clock
    Check("telemetry: at 1.00x it says Keeping up and is not behind", !keep.Behind && keep.Text.StartsWith("Keeping up") && Math.Abs(keep.WindowSpeed - 1.0) < 0.05, keep.Text);
    var slow = Simulate(delegate(double now) { return now * 0.5; }, 12.0, true);
    Check("telemetry: at 0.5x for 12 s it says Falling behind: 0.50x real time", slow.Behind && slow.Text == "Falling behind: 0.50x real time", slow.Text);
    var early = Simulate(delegate(double now) { return now * 0.5; }, 6.0, true);
    Check("telemetry: slow for under 5 s is not yet 'behind'", !early.Behind, early.Text);
    var edge = Simulate(delegate(double now) { return now * 0.94; }, 12.0, true);
    Check("telemetry: 0.94x counts as behind (below 0.95)", edge.Behind && edge.Text == "Falling behind: 0.94x real time", edge.Text);
    var ok96 = Simulate(delegate(double now) { return now * 0.96; }, 12.0, true);
    Check("telemetry: 0.96x is still Keeping up", !ok96.Behind && ok96.Text.StartsWith("Keeping up"), ok96.Text);
    var recover = Simulate(delegate(double now) { return now < 8 ? now * 0.5 : 4.0 + (now - 8) * 1.0; }, 16.0, true);
    Check("telemetry: back to 1.00x for a few seconds clears 'behind'", !recover.Behind && recover.Text.StartsWith("Keeping up"), recover.Text);
    var stall = Simulate(delegate(double now) { return Math.Min(now, 4.0); }, 12.0, false);   // the window stops drawing at 4 s: no new frames, nothing written
    Check("telemetry: a window that stopped drawing is 'Waiting for the window to draw', not behind", stall.Stalled && !stall.Behind && stall.Text == "Waiting for the window to draw", stall.Text);

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

  // A pretend recording: every 0.2 s the clock moves and the written video is at written(now); frames keep arriving unless a stall is wanted.
  static Telemetry Simulate(Func<double, double> written, double seconds, bool framesKeepComing) {
    var t = new Telemetry();
    long frames = 0;
    double lastW = -1;
    for (double now = 0.2; now <= seconds + 1e-9; now += 0.2) {
      double w = written(now);
      if (framesKeepComing || w > lastW + 1e-9) frames += 12;
      lastW = w;
      t.Feed("frame=" + frames);
      t.Feed("out_time_us=" + (long)(w * 1e6));
      t.Tick(now);
    }
    return t;
  }

  // ------------------------------------------------------------------------------------------------ CaptureMethod and FrameRateMode
  [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();

  static void UnitsM1c(string tmp) {
    // the two new switches
    string sw = Path.Combine(tmp, "switches2.json");
    File.WriteAllText(sw, "{\"CaptureMethod\":\"screen\",\"FrameRateMode\":\"vfr\"}");
    var c = Settings.Load(sw);
    Check("switches: CaptureMethod screen and FrameRateMode vfr are read", c.Screen && c.Vfr && c.CaptureMethod == "screen" && c.FrameRateMode == "vfr");
    File.WriteAllText(sw, "{\"CaptureMethod\":\" Screen \",\"FrameRateMode\":\"VFR\"}");
    c = Settings.Load(sw); Check("switches: ignore case and spaces", c.Screen && c.Vfr);
    File.WriteAllText(sw, "{\"CaptureMethod\":\"monitor\",\"FrameRateMode\":\"variable\"}");
    c = Settings.Load(sw); Check("switches: unusable values mean window capture and cfr", !c.Screen && !c.Vfr && c.CaptureMethod == "window" && c.FrameRateMode == "cfr");
    c = Settings.Load(Path.Combine(tmp, "absent2.json")); Check("switches: defaults are window and cfr", !c.Screen && !c.Vfr);

    var onNvidia = new DxgiOutput { Adapter = 0, Output = 0, AdapterName = "NVIDIA GeForce RTX 3070 Ti", VendorId = 0x10DE, DeviceName = @"\\.\DISPLAY1", Desktop = new Rectangle(0, 0, 2560, 1440), Monitor = new IntPtr(1001) };
    var secondOutput = new DxgiOutput { Adapter = 0, Output = 1, AdapterName = "NVIDIA GeForce RTX 3070 Ti", VendorId = 0x10DE, DeviceName = @"\\.\DISPLAY3", Desktop = new Rectangle(2560, 0, 1920, 1080), Monitor = new IntPtr(1003) };
    var onIntel = new DxgiOutput { Adapter = 1, Output = 0, AdapterName = "Intel(R) UHD Graphics 630", VendorId = 0x8086, DeviceName = @"\\.\DISPLAY2", Desktop = new Rectangle(352, -1080, 1920, 1080), Monitor = new IntPtr(1002) };
    var screenCfr = new AppSettings { CaptureMethod = "screen" };
    var screenVfr = new AppSettings { CaptureMethod = "screen", FrameRateMode = "vfr" };
    var windowVfr = new AppSettings { FrameRateMode = "vfr" };

    // window + cfr is the default and is checked above; window + vfr
    string wv = FfmpegArgs.Record(EncoderProfile.Nvenc, 9, 1920, 1080, "a.mkv", windowVfr);
    Check("window + vfr: gfxcapture straight into the chain, no fps filter, passthrough with the exact capture times", wv.Contains("gfxcapture=hwnd=9:") && !wv.Contains("fps=60") && wv.Contains("-fps_mode passthrough -enc_time_base filter") && !wv.Contains("-fps_mode cfr"), wv);
    Check("vfr never uses -r (ffmpeg refuses it with passthrough) and never repeats frames", !wv.Contains(" -r ") && !wv.Contains("fps="));
    Check("vfr keeps the frames on the GPU and the colour tags", !wv.Contains("hwdownload") && wv.Contains("setparams=colorspace=smpte170m:range=tv:color_primaries=bt709:color_trc=bt709"));
    Check("window + cfr still has the fps filter and cfr mode", FfmpegArgs.Record(EncoderProfile.Nvenc, 9, 1920, 1080, "a.mkv", new AppSettings()).Contains(",fps=60,") && FfmpegArgs.Record(EncoderProfile.Nvenc, 9, 1920, 1080, "a.mkv", new AppSettings()).Contains("-fps_mode cfr"));

    // screen capture
    string sc = FfmpegArgs.Record(EncoderProfile.Nvenc, 9, 1280, 720, "a.mkv", screenCfr, onNvidia);
    Check("screen + cfr: ddagrab on DXGI adapter 0 output 0, 60 fps, pointer drawn, frames stay Direct3D 11", sc.Contains("-init_hw_device d3d11va=dda:0 -filter_hw_device dda") && sc.Contains("ddagrab=output_idx=0:framerate=60:draw_mouse=true,fps=60,setparams=") && !sc.Contains("gfxcapture") && !sc.Contains("hwdownload") && !sc.Contains("hwupload"), sc);
    Check("screen + cfr: constant 60 fps like window capture, dup_frames left at its default", sc.Contains("-fps_mode cfr") && !sc.Contains("dup_frames"));
    Check("screen: the video is the monitor's size, so the ceiling is for 2560x1440 (53300k) whatever the window's size", sc.Contains("-maxrate 53300k"), sc);
    Check("screen: MaxHeight is ignored (ddagrab can't scale)", FfmpegArgs.Record(EncoderProfile.Nvenc, 9, 1280, 720, "a.mkv", new AppSettings { CaptureMethod = "screen", MaxHeight = 720 }, onNvidia) == sc);
    string sv = FfmpegArgs.Record(EncoderProfile.Nvenc, 9, 1280, 720, "a.mkv", screenVfr, onNvidia);
    Check("screen + vfr: ddagrab with dup_frames=false, no fps filter, passthrough", sv.Contains("ddagrab=output_idx=0:framerate=60:draw_mouse=true:dup_frames=false,setparams=") && !sv.Contains("fps=60") && sv.Contains("-fps_mode passthrough -enc_time_base filter"), sv);
    string s2 = FfmpegArgs.Record(EncoderProfile.Nvenc, 9, 1280, 720, "a.mkv", screenCfr, secondOutput);
    Check("screen: output_idx is the output's index on its adapter (the second output of adapter 0 is 1)", s2.Contains("d3d11va=dda:0 ") && s2.Contains("output_idx=1:") && s2.Contains("-maxrate 30000k"), s2);
    string s3 = FfmpegArgs.Record(EncoderProfile.Software, 9, 1280, 720, "a.mkv", screenCfr, onIntel);
    Check("screen: the adapter index follows the monitor's adapter (Intel is adapter 1, its own output 0), and the software encoder downloads after ddagrab", s3.Contains("d3d11va=dda:1 ") && s3.Contains("ddagrab=output_idx=0:") && s3.Contains("hwdownload"), s3);
    Check("screen capture asked for but no monitor known: the window capture arguments are used", FfmpegArgs.Record(EncoderProfile.Nvenc, 9, 1280, 720, "a.mkv", screenCfr, null).Contains("gfxcapture=hwnd=9"));
    Check("the quality set works with screen and vfr too", FfmpegArgs.Record(EncoderProfile.Nvenc, 9, 1280, 720, "a.mkv", new AppSettings { CaptureMethod = "screen", FrameRateMode = "vfr", EncoderProfile = "quality" }, onNvidia).Contains("-preset p5 -tune hq"));

    // the chip
    Check("chip: screen", MainForm.ChipText(EncoderProfile.Nvenc, screenCfr, null) == "HEVC · NVENC · standard · native · screen · GPU normal", MainForm.ChipText(EncoderProfile.Nvenc, screenCfr, null));
    Check("chip: vfr", MainForm.ChipText(EncoderProfile.Nvenc, windowVfr, null) == "HEVC · NVENC · standard · native · vfr · GPU normal");
    Check("chip: quality, screen, vfr and GPU normal (MaxHeight shows native with screen capture)", MainForm.ChipText(EncoderProfile.Nvenc, new AppSettings { EncoderProfile = "quality", MaxHeight = 1080, CaptureMethod = "screen", FrameRateMode = "vfr", GpuPriority = "normal" }, null) == "HEVC · NVENC · quality · native · screen · vfr · GPU normal");

    // vendors, and refusing a monitor the encoder can't read before anything starts
    Check("encoder vendors: NVENC 10DE, AMF 1002, QSV 8086, software none", DxgiOutput.VendorOf(EncoderProfile.Nvenc) == 0x10DE && DxgiOutput.VendorOf(EncoderProfile.Amf) == 0x1002 && DxgiOutput.VendorOf(EncoderProfile.Qsv) == 0x8086 && DxgiOutput.VendorOf(EncoderProfile.Software) == 0);
    string err;
    var mismatch = new Recording(EncoderProfile.Nvenc, GetDesktopWindow(), 1280, 720, Path.Combine(tmp, "rec"), "mismatch", screenCfr, null, onIntel);
    bool started = mismatch.Start(out err, 1000);
    Check("screen on a monitor of another GPU than the encoder's is refused with a clear message, and nothing is started", !started && err.Contains("Intel(R) UHD Graphics 630") && err.Contains("can't feed NVENC") && mismatch.Command == null, err);
    var nomon = new Recording(EncoderProfile.Nvenc, GetDesktopWindow(), 1280, 720, Path.Combine(tmp, "rec"), "nomon", screenCfr, null, null);
    started = nomon.Start(out err, 1000);
    Check("screen without a known monitor is refused, nothing is started", !started && err.Contains("Screen capture needs the monitor") && nomon.Command == null, err);

    // which DXGI output a window is on: asked of DXGI, matched by HMONITOR; never by position in Screen.AllScreens
    var outs = Dxgi.Outputs();
    foreach (var o in outs) Note("DXGI: " + o);
    var order = new List<string>(); foreach (var s in Screen.AllScreens) order.Add(s.DeviceName);
    Note("Screen.AllScreens order: " + string.Join(", ", order.ToArray()));
    Check("DXGI lists at least one output, each with a monitor handle and a size", outs.Count >= 1 && outs.TrueForAll(delegate(DxgiOutput o) { return o.Monitor != IntPtr.Zero && o.Width > 0 && o.Height > 0 && o.AdapterName.Length > 0; }), outs.Count + " outputs");
    bool allMatch = true; string why = "";
    foreach (var s in Screen.AllScreens) {
      var o = Dxgi.ForPoint(s.Bounds.X + s.Bounds.Width / 2, s.Bounds.Y + s.Bounds.Height / 2);
      if (o == null || o.Desktop != s.Bounds || o.DeviceName != s.DeviceName) { allMatch = false; why += s.DeviceName + " -> " + (o == null ? "none" : o.ToString()) + "; "; }
    }
    Check("every monitor Windows has maps to the DXGI output with the same rectangle and device name (matched by HMONITOR)", allMatch, why);
    var primary = Dxgi.ForWindow(GetDesktopWindow());
    Check("a window on the primary monitor (the desktop window) maps to the primary monitor's output", primary != null && primary.Desktop == Screen.PrimaryScreen.Bounds, primary == null ? "none" : primary.ToString());
    var seen = new HashSet<string>(); bool unique = true;
    foreach (var o in outs) if (!seen.Add(o.Adapter + "/" + o.Output)) unique = false;
    Check("adapter/output index pairs are unique", unique);

    // a VFR recording on the CPU (a test pattern with gaps and jittered times, libx264): ffmpeg keeps the real times with the VFR options, and
    // the MKV to MP4 remux keeps them and the average frame rate stays the real one. No GPU.
    string vdir = Path.Combine(tmp, "vfr"); Directory.CreateDirectory(vdir);
    string src = "testsrc2=size=320x240:rate=60:duration=4,select='not(eq(mod(n,5),2))',settb=1/10000000,setpts='PTS+0.003*sin(N*1.3)/TB'";
    string mkv = Path.Combine(vdir, "v.mkv"), mp4 = Path.Combine(vdir, "v.mp4"), so, se;
    int code = Proc.Run(Ffmpeg.Exe, "-hide_banner -nostdin -loglevel error -y -f lavfi -i \"" + src + "\" -c:v libx264 -preset ultrafast " + FfmpegArgs.VfrSync + " " + FfmpegArgs.Quote(mkv), 60000, out so, out se);
    Check("vfr options: a recording with gaps is written (192 of 240 frames kept, none repeated)", code == 0 && File.Exists(mkv), se);
    var pm = PacketTimes(mkv);
    double dev = 0; foreach (double t in pm) { double x = t * 60; dev += Math.Abs(x - Math.Round(x)); } dev /= Math.Max(1, pm.Count);
    Check("vfr options: the capture times are kept (not rounded to the 1/60 grid): mean distance from the grid " + dev.ToString("0.000") + " (about 0.12 exact, 0.01 rounded)", pm.Count == 192 && dev > 0.08, pm.Count + " packets");
    string so2, se2;
    int rcode = Proc.Run(Ffmpeg.Exe, "-hide_banner -nostdin -loglevel error -y -f lavfi -i \"" + src + "\" -c:v libx264 -preset ultrafast -fps_mode passthrough -r 60 " + FfmpegArgs.Quote(Path.Combine(vdir, "r.mkv")), 60000, out so2, out se2);
    Check("-r together with passthrough is refused by ffmpeg (why the Recorder doesn't use it)", rcode != 0, "exit " + rcode);
    string so3, se3;
    int qcode = Proc.Run(Ffmpeg.Exe, "-hide_banner -nostdin -loglevel error -y -f lavfi -i \"" + src + "\" -c:v libx264 -preset ultrafast -fps_mode passthrough " + FfmpegArgs.Quote(Path.Combine(vdir, "q.mkv")), 60000, out so3, out se3);
    var pq = qcode == 0 ? PacketTimes(Path.Combine(vdir, "q.mkv")) : new List<double>();
    double devQ = 0; foreach (double t in pq) { double x = t * 60; devQ += Math.Abs(x - Math.Round(x)); } devQ /= Math.Max(1, pq.Count);
    Check("without -enc_time_base filter ffmpeg rounds the times to the 1/60 grid (why the option is there): mean distance " + devQ.ToString("0.000"), pq.Count > 0 && devQ < 0.04, pq.Count + " packets");
    code = Proc.Run(Ffmpeg.Exe, FfmpegArgs.Remux(EncoderProfile.Software, mkv, mp4), 60000, out so, out se);
    var pr = ProbeResult.Run(mp4, true);
    Check("remux of the VFR recording to MP4: 192 frames, 4.0 s, every capture time kept", code == 0 && pr.Ok && pr.Frames == 192 && Math.Abs(pr.Duration - 4.0) < 0.1 && SameTimes(pm, PacketTimes(mp4)), pr.Frames + " frames " + pr.Duration.ToString("0.000") + " s " + pr.Error);
    double avg = 0; { var parts = (pr.AvgFrameRate ?? "0/1").Split('/'); double n = double.Parse(parts[0]), d = parts.Length > 1 ? double.Parse(parts[1]) : 1; avg = d > 0 ? n / d : 0; }
    Check("the VFR MP4's average frame rate is the real one (48 fps here), which is what Studio reads first (main.js:108); r_frame_rate is a guess (" + pr.FrameRate + ")", Math.Abs(avg - 48.0) < 1.0, pr.AvgFrameRate + " = " + avg.ToString("0.00"));

    // telemetry in vfr: a window that sends a frame only every 0.5 s still has output time moving with the clock: speed is output time against wall time
    var t2 = new Telemetry(); long fr = 0;
    for (double now = 0.2; now <= 12.0 + 1e-9; now += 0.2) { if (Math.Abs(now / 0.5 - Math.Round(now / 0.5)) < 1e-6) fr++; t2.Feed("frame=" + fr); t2.Feed("out_time_us=" + (long)(Math.Floor(now / 0.5) * 0.5 * 1e6)); t2.Tick(now); }
    Check("telemetry in vfr: sparse real frames with output time moving at the clock's pace is Keeping up (speed " + t2.WindowSpeed.ToString("0.00") + ")", !t2.Behind && !t2.Stalled && t2.Text.StartsWith("Keeping up") && Math.Abs(t2.WindowSpeed - 1.0) < 0.2, t2.Text);
    var t3 = new Telemetry(); fr = 0;
    for (double now = 0.2; now <= 14.0 + 1e-9; now += 0.2) { if (now < 3) fr += 12; t3.Feed("frame=" + fr); t3.Feed("out_time_us=" + (long)(Math.Min(now, 3.0) * 1e6)); t3.Tick(now); }
    Check("telemetry in vfr: no frames at all after 3 s is 'Waiting for the window to draw', not Falling behind", t3.Stalled && !t3.Behind && t3.Text == "Waiting for the window to draw", t3.Text);
    var slow = new Recording(EncoderProfile.Nvenc, IntPtr.Zero, 1280, 720, tmp, "x", windowVfr, null);
    Check("a stalled window is not 'video still to write' at Stop", !slow.BehindNow);
  }

  // Presentation times of a file's video packets, in seconds.
  static List<double> PacketTimes(string file) {
    string so, se;
    Proc.Run(Ffmpeg.ProbeExe, "-v error -select_streams v:0 -show_entries packet=pts_time -of csv=p=0 " + FfmpegArgs.Quote(file), 30000, out so, out se);
    var list = new List<double>();
    foreach (string l in so.Split(new[] { (char)10 })) { double v; if (double.TryParse(l.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) list.Add(v); }
    list.Sort();
    return list;
  }

  static bool SameTimes(List<double> a, List<double> b) {
    if (a.Count != b.Count) return false;
    for (int i = 0; i < a.Count; i++) if (Math.Abs(a[i] - b[i]) > 0.0011) return false;
    return true;
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
    if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
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
    int code = Proc.Run(Ffmpeg.Exe, "-hide_banner -loglevel error -nostdin -y -f lavfi -i testsrc2=size=640x360:rate=30:duration=3 -c:v libx264 -preset ultrafast -pix_fmt yuv420p " + FfmpegArgs.Quote(clip), 60000, out so, out se);
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
        Check("it shows the defaults in plain words: Light, Native, This window, Steady 60 fps, Normal",
          sw0.ChosenLabel(SettingChoices.Field.Encoder) == "Light, best for games" && sw0.ChosenLabel(SettingChoices.Field.Resolution) == "Native" && sw0.ChosenLabel(SettingChoices.Field.Capture) == "This window" &&
          sw0.ChosenLabel(SettingChoices.Field.FrameRate) == "Steady 60 fps, best for editing" && sw0.ChosenLabel(SettingChoices.Field.Gpu) == "Normal");
        Check("the chip shows the defaults", form.ChipTextNow == "HEVC · NVENC · standard · native · GPU normal", form.ChipTextNow);
        Shot(sw0.Handle, "05-settings-recording-console.png", true);
        Invoke(sw0, delegate { sw0.Choose(SettingChoices.Field.Encoder, 1); sw0.Choose(SettingChoices.Field.Resolution, 2); sw0.Choose(SettingChoices.Field.Capture, 1); sw0.Choose(SettingChoices.Field.FrameRate, 1); sw0.Choose(SettingChoices.Field.Gpu, 1); });
        Check("choosing the heavier options: the chip follows at once", form.ChipTextNow == "HEVC · NVENC · quality · 720p · screen · vfr · GPU high", form.ChipTextNow);
        string savedPrefs = File.Exists(prefsPath) ? File.ReadAllText(prefsPath) : "";
        Check("and it is saved at once in the prefs file", savedPrefs.Contains("\"EncoderProfile\":\"quality\"") && savedPrefs.Contains("\"MaxHeight\":720") && savedPrefs.Contains("\"CaptureMethod\":\"screen\"") && savedPrefs.Contains("\"FrameRateMode\":\"vfr\"") && savedPrefs.Contains("\"GpuPriority\":\"high\""), savedPrefs);
        Invoke(sw0, delegate { sw0.PressReset(); });
        Check("Reset to defaults puts the five back, in the window, the chip and the file", sw0.ChosenLabel(SettingChoices.Field.Encoder) == "Light, best for games" && form.ChipTextNow == "HEVC · NVENC · standard · native · GPU normal" && File.ReadAllText(prefsPath).Contains("\"EncoderProfile\":\"standard\""));
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
        Check("the Paper theme applies live to the main window (light background and text), saved in the prefs", Palette.Theme.Id == "paper" && !Palette.Dark && form.BackColor == Palette.Bg && Hex6(Palette.Bg) == "#e9e6df" && File.ReadAllText(prefsPath).Contains("\"Theme\":\"paper\""));
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
        Check("back to Console: dark again", Palette.Dark && Hex6(Palette.Bg) == "#0b0d10" && form.BackColor == Palette.Bg);
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

  // ------------------------------------------------------------------------------------------------ EncoderCheck and the fall-back order
  static void UnitsEncoderCheck(string tmp) {
    Check("the fall-back order after a failed encoder: nvenc, amf, qsv, software, then nothing",
      EncoderChoice.Next(EncoderProfile.Nvenc) == EncoderProfile.Amf && EncoderChoice.Next(EncoderProfile.Amf) == EncoderProfile.Qsv && EncoderChoice.Next(EncoderProfile.Qsv) == EncoderProfile.Software && EncoderChoice.Next(EncoderProfile.Software) == null);
    var v = EncoderChoice.ByVendor();
    bool hasNvidia = false; foreach (var o in Dxgi.Outputs()) if (o.VendorId == 0x10DE) hasNvidia = true;
    Check("by the GPU's maker, with no test encode: NVIDIA here gives hevc_nvenc, and the choice is logged as such", (hasNvidia ? v == EncoderProfile.Nvenc : v != null) && EncoderChoice.Picked == v && EncoderChoice.Log.Contains("no test encode"), v.Name);
    EncoderChoice.Reset();
    Check("Reset forgets the pick (so the real detection still runs in the end-to-end tests)", EncoderChoice.Picked == null);
  }

  // ------------------------------------------------------------------------------------------------ the member's choices (prefs), the Settings window's labels, and the look
  static bool Rgb(int[] c, int r, int g, int b) { return c[0] == r && c[1] == g && c[2] == b; }
  static string Hex6(Color c) { return "#" + c.R.ToString("x2") + c.G.ToString("x2") + c.B.ToString("x2"); }

  static void UnitsPrefs(string tmp) {
    // defaults everywhere
    var d = new AppSettings();
    Check("defaults: Light (standard), Native, This window, Steady 60, GPU normal, EncoderCheck vendor, Console theme with its own accent",
      d.EncoderProfile == "standard" && d.MaxHeight == 0 && d.CaptureMethod == "window" && d.FrameRateMode == "cfr" && d.GpuPriority == "normal" && d.EncoderCheck == "vendor" && d.ThemeId == "console" && d.Accent == "theme");
    string f = Path.Combine(tmp, "ec2.json");
    File.WriteAllText(f, "{}"); Check("EncoderCheck defaults to vendor", Settings.Load(f).EncoderCheck == "vendor");
    File.WriteAllText(f, "{\"EncoderCheck\":\"record\"}"); Check("EncoderCheck record is read", Settings.Load(f).EncoderCheck == "record");
    File.WriteAllText(f, "{\"EncoderCheck\":\" Startup \"}"); Check("EncoderCheck startup is read (it is no longer the default)", Settings.Load(f).EncoderCheck == "startup");
    File.WriteAllText(f, "{\"EncoderCheck\":\"never\"}"); Check("an unknown EncoderCheck means vendor", Settings.Load(f).EncoderCheck == "vendor");

    // the labels of the Settings window and what each sets
    Check("labels: Encoder is 'Light, best for games' and 'Quality, for desktop apps only (heavier on the GPU)'", string.Join("|", SettingChoices.Labels(SettingChoices.Field.Encoder)) == "Light, best for games|Quality, for desktop apps only (heavier on the GPU)");
    Check("labels: Resolution is Native, 1080p, 720p", string.Join("|", SettingChoices.Labels(SettingChoices.Field.Resolution)) == "Native|1080p|720p");
    Check("labels: Capture is 'This window' and 'The whole screen it's on'", string.Join("|", SettingChoices.Labels(SettingChoices.Field.Capture)) == "This window|The whole screen it's on");
    Check("labels: Frame rate is 'Steady 60 fps, best for editing' and 'Only real frames (variable)'", string.Join("|", SettingChoices.Labels(SettingChoices.Field.FrameRate)) == "Steady 60 fps, best for editing|Only real frames (variable)");
    Check("labels: GPU priority is Normal and 'High (not recommended with games)'", string.Join("|", SettingChoices.Labels(SettingChoices.Field.Gpu)) == "Normal|High (not recommended with games)");
    Check("the resolution hint says scaling costs performance while a game runs", SettingChoices.Hint(SettingChoices.Field.Resolution) == "Scaling costs performance while a game runs.");
    bool mapOk = true;
    foreach (SettingChoices.Field fld in Enum.GetValues(typeof(SettingChoices.Field))) {
      int n = SettingChoices.Labels(fld).Length;
      for (int i = 0; i < n; i++) { var s = new AppSettings(); SettingChoices.Set(s, fld, i); if (SettingChoices.Get(s, fld) != i) mapOk = false; }
    }
    Check("every label maps to a setting and back to the same label", mapOk);
    var m = new AppSettings(); SettingChoices.Set(m, SettingChoices.Field.Encoder, 1); SettingChoices.Set(m, SettingChoices.Field.Resolution, 1); SettingChoices.Set(m, SettingChoices.Field.Capture, 1); SettingChoices.Set(m, SettingChoices.Field.FrameRate, 1); SettingChoices.Set(m, SettingChoices.Field.Gpu, 1);
    Check("choosing the second label of each sets quality, 1080, screen, vfr, high", m.Quality && m.MaxHeight == 1080 && m.Screen && m.Vfr && m.GpuPriority == "high");
    SettingChoices.Set(m, SettingChoices.Field.Resolution, 2); Check("the third resolution label is 720", m.MaxHeight == 720);
    m.ThemeId = "paper"; m.Accent = "225"; SettingChoices.Defaults(m);
    Check("Reset to defaults restores the five recording choices and leaves the look alone", m.EncoderProfile == "standard" && m.MaxHeight == 0 && !m.Screen && !m.Vfr && m.GpuPriority == "normal" && m.ThemeId == "paper" && m.Accent == "225");
    var odd = new AppSettings { MaxHeight = 900 };
    Check("a MaxHeight set by hand to another height shows as Native until a choice is made", SettingChoices.Get(odd, SettingChoices.Field.Resolution) == 0);

    // the prefs file: where, round trip, precedence over the settings file, malformed
    string settings = Path.Combine(tmp, "pf-settings.json"), local = Path.Combine(tmp, "local");
    File.WriteAllText(settings, "{\"DataFolder\":\"" + Path.Combine(tmp, "suite").Replace("\\", "\\\\") + "\"}");
    string prefs = Settings.PrefsFile(settings, local);
    Check("prefs live in <DataFolder>\\Recorder.prefs.json, a file the Suite never writes", prefs == Path.Combine(tmp, "suite", "Recorder.prefs.json") && prefs != settings);
    Check("prefs without a DataFolder live in %LOCALAPPDATA%\\Bifrost Recorder\\prefs.json", Settings.PrefsFile(Path.Combine(tmp, "none.json"), local) == Path.Combine(local, "Bifrost Recorder", "prefs.json"));
    var all = new AppSettings { EncoderProfile = "quality", MaxHeight = 720, CaptureMethod = "screen", FrameRateMode = "vfr", GpuPriority = "high", ThemeId = "midnight", Accent = "280" };
    string problem;
    Settings.SavePrefs(prefs, all);
    string saved = File.ReadAllText(prefs);
    Check("saving creates the folder, writes the choices, a hue accent as a number (as Scribe saves it) and leaves no temp file", File.Exists(prefs) && !File.Exists(prefs + ".tmp") && saved.Contains("\"Accent\":280") && saved.Contains("\"Theme\":\"midnight\"") && saved.Contains("\"EncoderProfile\":\"quality\""), saved);
    var back = Settings.LoadAll(settings, prefs, out problem);
    Check("prefs round trip: all five choices, the theme and the accent come back", problem == null && back.Quality && back.MaxHeight == 720 && back.Screen && back.Vfr && back.GpuPriority == "high" && back.ThemeId == "midnight" && back.Accent == "280" && back.DataFolder == Path.Combine(tmp, "suite"));
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

  // The GPU-priority call, tried on a child that does no GPU work at all (ffmpeg making a test pattern for nothing): what the PC answers
  // for HIGH and ABOVE_NORMAL without administrator rights. The same call is made on the real recording's ffmpeg in the end-to-end test.
  static void GpuPriorityApiTest() {
    var p = Proc.Start(Ffmpeg.Exe, "-hide_banner -loglevel error -re -f lavfi -i testsrc=size=160x120:rate=10 -t 20 -f null -", true);
    try {
      Thread.Sleep(400);
      var lines = new List<string>();
      int got = GpuScheduling.Try(p.Handle, delegate(string s) { lines.Add(s); });
      foreach (string l in lines) Note("gpu priority (CPU-only child): " + l);
      Check("the GPU priority call exists and answers with an NTSTATUS for HIGH (no exception)", lines.Count >= 1 && lines[0].Contains("(HIGH) = 0x"), lines.Count > 0 ? lines[0] : "no lines");
      Note("result: " + (got == GpuScheduling.High ? "HIGH accepted without administrator rights" : got == GpuScheduling.AboveNormal ? "HIGH refused, ABOVE_NORMAL accepted" : "both refused or unavailable"));
    } finally { try { if (!p.HasExited) p.Kill(); p.WaitForExit(3000); } catch (Exception) { } }
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
      string health8 = rec.Health, chip8 = form.ChipTextNow, status8 = form.StatusText;
      Shot(form.Handle, "01-recording-speed-chip.png", true);
      Note("at 8 s: " + health8 + " | chip: " + chip8 + " | window said: " + status8);
      Check("the status line says Keeping up (with the speed) while the encoder keeps up", health8.StartsWith("Keeping up"), health8);
      Check("the chip shows the active set and what the GPU granted", chip8 == "HEVC · NVENC · standard · native · GPU " + rec.GpuPriorityResult, chip8);
      string logPath = rec.LogPath;
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
      CheckLog(logPath, "main recording", false);

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

      // M1b: the switches through the window: quality encoder, scaled to 360 high, GPU priority left at normal
      Console.WriteLine("-- M1b switches: quality encoder, 360p, GPU priority normal");
      var swin = TestWindow.Open(Scene.Simple, 1280, 720, true, WindowPrefix + ": quality and scaled");
      Invoke(form, delegate { form.Config = new AppSettings { EncoderProfile = "quality", MaxHeight = 360, GpuPriority = "normal" }; form.ReloadWindows(); form.SelectHwnd(swin.Handle); });
      form.LastResult = null;
      Invoke(form, delegate { form.PressRecord(); });
      Check("switches: recording started", WaitFor(delegate { return form.Mode == MainForm.State.Recording; }, 30000, "recording"), form.StatusText);
      var srec = form.Current;
      Thread.Sleep(5000);
      Check("switches: the chip shows quality, 360p and GPU normal", form.ChipTextNow == "HEVC · NVENC · quality · 360p · GPU normal", form.ChipTextNow);
      Check("switches: ffmpeg was started with the quality options and a 640x360 capture", srec.Command.Contains("-preset p5") && srec.Command.Contains("-bf 2") && srec.Command.Contains("width=640:height=360") && srec.Command.Contains("resize_mode=scale_aspect"), srec.Command);
      Shot(form.Handle, "02-chip-quality-scaled.png", true);
      string slog = srec.LogPath;
      Invoke(form, delegate { form.PressRecord(); });
      Check("switches: Stop saves", WaitFor(delegate { return form.Mode == MainForm.State.Idle && form.LastResult != null; }, 60000, "saved"), form.StatusText);
      var sres = form.LastResult;
      if (sres != null && sres.Ok) {
        var sp = ProbeResult.Run(sres.Mp4, true);
        Check("switches: the video is HEVC, 640 x 360 (scaled on the GPU by the capture filter), 60/1, about 5 s", sp.Codec == "hevc" && sp.Width == 640 && sp.Height == 360 && sp.FrameRate == "60/1" && Math.Abs(sp.Duration - 5) < 1.5, sp.Codec + " " + sp.Width + "x" + sp.Height + " " + sp.FrameRate + " " + sp.Duration.ToString("0.00"));
        string spng = Path.Combine(throwDir, "scaled-frame.png"), so0, se0;
        Proc.Run(Ffmpeg.Exe, "-hide_banner -loglevel error -nostdin -y -ss 3 -i " + FfmpegArgs.Quote(sres.Mp4) + " -frames:v 1 " + FfmpegArgs.Quote(spng), 30000, out so0, out se0);
        if (File.Exists(spng)) using (var b = new Bitmap(spng)) {
          int worst = 0;
          for (int i = 0; i < TestWindow.Swatches.Length; i++) {   // the swatches, half their size at 360 high: centre of each
            var want = TestWindow.Swatches[i]; var got = b.GetPixel((20 + i * TestWindow.SwatchWidth + TestWindow.SwatchWidth / 2) / 2, (360 * 2 - TestWindow.SwatchHeight - 20 + TestWindow.SwatchHeight / 2) / 2);
            worst = Math.Max(worst, Math.Max(Math.Abs(got.R - want.R), Math.Max(Math.Abs(got.G - want.G), Math.Abs(got.B - want.B))));
          }
          Check("switches: the scaled picture still has the right colours (worst error " + worst + " of 255)", worst <= 10);
        }
      }
      string stext = slog != null && File.Exists(slog) ? File.ReadAllText(slog) : "";
      Check("switches: the log records the settings, the scaling and that GPU priority was left at normal", stext.Contains("settings: EncoderProfile=quality MaxHeight=360 GpuPriority=normal") && stext.Contains("(scaled by MaxHeight)") && stext.Contains("left at NORMAL"), slog);
      Invoke(form, delegate { form.Config = new AppSettings(); });
      swin.CloseWindow();

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

  // The recording's log: where it is, what it says. The GPU priority lines (with their NTSTATUS) are printed for the report.
  static void CheckLog(string logPath, string what, bool gpuHigh) {
    string logs = Path.Combine(outDir, "logs");
    Check(what + ": a log was written to the logs folder, and none to the recordings folder", logPath != null && File.Exists(logPath) && Path.GetDirectoryName(logPath) == logs && Directory.GetFiles(Path.Combine(outDir, "Recordings"), "*.log").Length == 0, logPath);
    if (logPath == null || !File.Exists(logPath)) return;
    string text = File.ReadAllText(logPath);
    int lines = 0;
    foreach (string l in text.Split(new[] { (char)10 })) if (l.Contains("s lag=")) lines++;
    Check(what + ": the log has the start time, settings, encoder, window and video size, and the ffmpeg command", text.Contains("started 20") && text.Contains("settings: EncoderProfile=standard MaxHeight=0 GpuPriority=" + (gpuHigh ? "high" : "normal")) && text.Contains("encoder hevc_nvenc") && text.Contains("window client area") && text.Contains("ffmpeg -hide_banner"));
    Check(what + ": about one progress line a second (" + lines + ")", lines >= 15, lines.ToString());
    Check(what + ": the log says Stop was pressed, that ffmpeg was not forced, and how long Stop took", text.Contains("Stop pressed") && text.Contains("forced: no") && text.Contains("Stop took"));
    if (gpuHigh) Check(what + ": the log has the GPU priority result with its NTSTATUS", text.Contains("D3DKMTSetProcessSchedulingPriorityClass(HIGH) = 0x"));
    else Check(what + ": the log says the GPU priority was left at normal (the default), and made no D3DKMT call", text.Contains("left at NORMAL (GpuPriority=normal)") && !text.Contains("D3DKMT"));
    foreach (string l in text.Split(new[] { (char)10 })) if (l.Contains("gpu priority") || l.Contains("D3DKMT") || l.Contains("Stop pressed") || l.Contains("ffmpeg ended")) Note("log: " + l.Trim());
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
    string png = Path.Combine(throwDir, "recorded-frame.png");
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
    // --bench <Scene> <width> <height> <seconds> [standard|quality] [MaxHeight] [high|normal]
    var cfg = new AppSettings { EncoderProfile = a.Length > 5 ? a[5] : "standard", MaxHeight = a.Length > 6 ? int.Parse(a[6]) : 0, GpuPriority = a.Length > 7 ? a[7] : "normal" };
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
    string name = "bench-" + scene + "-" + w + "x" + h + "-" + cfg.EncoderProfile + (cfg.MaxHeight > 0 ? "-" + cfg.MaxHeight : "") + "-gpu" + cfg.GpuPriority;
    var rec = new Recording(enc, win.Handle, w, h, Path.Combine(build, "bench"), name, cfg, Path.Combine(build, "bench", "logs"));
    long d0 = win.Drawn, l0 = win.Late;
    string err;
    if (!rec.Start(out err, 15000)) { Console.WriteLine("START FAILED " + err); smi.Stop(); win.CloseWindow(); return 1; }
    Console.WriteLine("ffmpeg " + rec.Command);
    smi.Take();
    double minSpeed = double.NaN; bool everBehind = false;
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < secs) {
      Thread.Sleep(250);
      double ws = rec.Tele.WindowSpeed;
      if (!double.IsNaN(ws) && (double.IsNaN(minSpeed) || ws < minSpeed)) minSpeed = ws;
      if (rec.Tele.Behind) everBehind = true;
    }
    double wall = sw.Elapsed.TotalSeconds;
    double cumSpeed = rec.Tele.CumulativeSpeed; string gpuGot = rec.GpuPriorityResult; string health = rec.Health;
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
    Console.WriteLine("RESULT size=" + mb.ToString("0.00") + " MB  =>  " + (mb / p.Duration * 60).ToString("0.0") + " MB per minute  (" + (res.Bytes * 8 / p.Duration / 1e6).ToString("0.0") + " Mb/s average; ceiling " + FfmpegArgs.MaxrateKbps(rec.OutWidth, rec.OutHeight) / 1000.0 + " Mb/s)");
    Console.WriteLine("RESULT settings: " + cfg.EncoderProfile + ", MaxHeight " + cfg.MaxHeight + " (video " + rec.OutWidth + "x" + rec.OutHeight + "), GPU priority asked " + cfg.GpuPriority + ", got " + gpuGot);
    Console.WriteLine("RESULT speed: ffmpeg's own (average since start) " + cumSpeed.ToString("0.000") + "x, slowest 4-second window " + minSpeed.ToString("0.000") + "x, flagged behind: " + (everBehind ? "YES" : "no") + ", status line at the end: " + health + ", Stop took " + res.StopSeconds.ToString("0.0") + " s, forced: " + (res.Forced ? "YES" : "no"));
    Console.WriteLine("RESULT while recording: " + Smi.Summary(during));
    Console.WriteLine("RESULT ffmpeg CPU " + cpu.TotalSeconds.ToString("0.00") + " s over " + wall.ToString("0.0") + " s = " + (100 * cpu.TotalSeconds / wall).ToString("0.0") + "% of one core, " + (100 * cpu.TotalSeconds / wall / Environment.ProcessorCount).ToString("0.00") + "% of the " + Environment.ProcessorCount + "-thread CPU");
    Console.WriteLine("RESULT test window cost per frame: render " + (win.RenderTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, win.Drawn)).ToString("0.00") + " ms, present " + (win.PresentTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, win.Drawn)).ToString("0.00") + " ms");
    double gAvg = 0, eAvg = 0; foreach (var x in during) { gAvg += x[0]; eAvg += x[1]; } if (during.Count > 0) { gAvg /= during.Count; eAvg /= during.Count; }
    Console.WriteLine("TABLE | " + scene + " " + w + "x" + h + " | " + cfg.EncoderProfile + " | " + rec.OutWidth + "x" + rec.OutHeight + (cfg.MaxHeight > 0 ? " (scaled)" : " (native)") + " | " + (mb / p.Duration * 60).ToString("0.0") + " | " + (res.Bytes * 8 / p.Duration / 1e6).ToString("0.0") + " | " + cumSpeed.ToString("0.00") + "x / " + minSpeed.ToString("0.00") + "x | GPU " + gAvg.ToString("0") + "% enc " + eAvg.ToString("0") + "% | cpu " + (100 * cpu.TotalSeconds / wall).ToString("0.0") + "% of a core | window " + (drawn / wall).ToString("0") + "/s | dup " + res.Dup + " drop " + res.Drop);
    Console.WriteLine("RESULT test window drew " + drawn + " frames in " + wall.ToString("0.0") + " s (" + (drawn / wall).ToString("0.0") + "/s) with " + late + " late (>8 ms) while being recorded");
    string so, se;
    string png = Path.Combine(build, "bench", name + ".png");
    Proc.Run(Ffmpeg.Exe, "-hide_banner -loglevel error -nostdin -y -ss " + (secs / 2) + " -i " + FfmpegArgs.Quote(res.Mp4) + " -frames:v 1 " + FfmpegArgs.Quote(png), 30000, out so, out se);
    Console.WriteLine("RESULT still frame " + png);
    GpuSafety.Check("the bench");
    return 0;
  }
}
