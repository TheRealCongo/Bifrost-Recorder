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
    shotsDir = Path.Combine(project, "docs", "screenshots", "m1b");   // the pictures kept for the Dev timeline
    throwDir = Path.Combine(outDir, "shots");                               // the other pictures the tests take, not kept
    TestWindow.LayerAlpha = 1;

    if (args.Length > 0 && args[0] == "--job-child") return JobChild();   // started by a run that already passed the guard; CPU only
    if (args.Length > 0 && args[0] == "--guard-check") { Console.WriteLine("game running: " + (RunningGame() ?? "none")); return 0; }   // reads the process list only; nothing else
    if (args.Length > 0 && args[0] == "--guard-test") { bool go = Guard(); Console.WriteLine("guard says: " + (go ? "go" : "refuse")); return go ? 0 : 2; }   // the guard and nothing after it
    if (args.Length > 0 && args[0] == "--unit") return UnitOnly();   // no GPU, no windows: safe while a game runs
    if (!Guard()) return 2;
    if (args.Length > 0 && args[0] == "--bench") return Bench(args);
    if (args.Length > 0 && args[0] == "--hold") return Hold(args);

    if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    Directory.CreateDirectory(outDir);
    Directory.CreateDirectory(shotsDir); Directory.CreateDirectory(throwDir);
    Console.WriteLine("Counts before: " + Counts());
    var watch = new Watcher(); watch.Start();
    try {
      Units(true);
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
  // --unit: only the checks that need neither the GPU nor a window (so they may run next to a game): no test encode, no capture.
  static int UnitOnly() {
    if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
    Directory.CreateDirectory(outDir);
    Console.WriteLine("Counts before: " + Counts());
    try { Units(false); } catch (Exception ex) { Check("the run finished", false, ex.ToString()); }
    int alive = 0;
    lock (Proc.Started) foreach (int pid in Proc.Started) { try { var p = Process.GetProcessById(pid); if (!p.HasExited) alive++; } catch (Exception) { } }
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

    UnitsM1b(tmp);
    JobTest();
    if (gpu) EncoderTest();
  }

  // ------------------------------------------------------------------------------------------------ M1b: switches, telemetry, log
  static void UnitsM1b(string tmp) {
    // the three experiment switches in BifrostRecorder.settings.json
    string sw = Path.Combine(tmp, "switches.json");
    File.WriteAllText(sw, "{\"ManagedBy\":\"Bifrost Suite\",\"DataFolder\":\"D:\\\\Suite\\\\Bifrost Recorder\",\"EncoderProfile\":\"gentle\",\"MaxHeight\":1080,\"GpuPriority\":\"normal\"}");
    var c = Settings.Load(sw);
    Check("switches are read: gentle, 1080, normal, and DataFolder beside them", c.Gentle && c.MaxHeight == 1080 && c.GpuPriority == "normal" && c.DataFolder == @"D:\Suite\Bifrost Recorder", c.EncoderProfile + " " + c.MaxHeight + " " + c.GpuPriority);
    var d = Settings.Load(Path.Combine(tmp, "absent.json"));
    Check("no settings file: standard, native size, GPU high, no DataFolder", !d.Gentle && d.EncoderProfile == "standard" && d.MaxHeight == 0 && d.GpuPriority == "high" && d.DataFolder == null);
    File.WriteAllText(sw, "{\"EncoderProfile\":\"turbo\",\"MaxHeight\":\"1080\",\"GpuPriority\":\"max\"}");
    c = Settings.Load(sw);
    Check("unusable switch values fall back to the defaults", c.EncoderProfile == "standard" && c.MaxHeight == 0 && c.GpuPriority == "high");
    File.WriteAllText(sw, "{\"EncoderProfile\":\" GENTLE \",\"MaxHeight\":720,\"GpuPriority\":\"Normal\"}");
    c = Settings.Load(sw);
    Check("switch values ignore case and spaces; MaxHeight 720", c.Gentle && c.MaxHeight == 720 && c.GpuPriority == "normal");
    File.WriteAllText(sw, "{\"MaxHeight\":100}"); Check("a MaxHeight below 144 means native", Settings.Load(sw).MaxHeight == 0);
    File.WriteAllText(sw, "{\"MaxHeight\":0}"); Check("MaxHeight 0 means native", Settings.Load(sw).MaxHeight == 0);
    File.WriteAllText(sw, "{\"MaxHeight\":true}"); Check("MaxHeight true is not a number", Settings.Load(sw).MaxHeight == 0);
    File.WriteAllText(sw, "{\"EncoderProfile\":\"standard\",\"MaxHeight\":0,\"GpuPriority\":\"high\"}");
    Check("a settings file without DataFolder still saves to %USERPROFILE%\\Videos\\Bifrost Recorder", Settings.RecordingsFolder(sw, @"C:\Users\Someone") == @"C:\Users\Someone\Videos\Bifrost Recorder");
    Check("logs: %LOCALAPPDATA%\\Bifrost Recorder\\logs without a DataFolder", Settings.LogsFolder(sw, @"C:\Users\Someone\AppData\Local") == @"C:\Users\Someone\AppData\Local\Bifrost Recorder\logs");
    File.WriteAllText(sw, "{\"DataFolder\":\"D:\\\\Suite\\\\Bifrost Recorder\"}");
    Check("logs: <DataFolder>\\logs, and never the recordings folder", Settings.LogsFolder(sw, @"C:\L") == @"D:\Suite\Bifrost Recorder\logs" && Settings.LogsFolder(sw, @"C:\L") != Settings.RecordingsFolder(sw, @"C:\U") && !Settings.LogsFolder(sw, @"C:\L").StartsWith(Settings.RecordingsFolder(sw, @"C:\U")));

    // the gentle NVENC profile
    var gentle = new AppSettings { EncoderProfile = "gentle" };
    string g = FfmpegArgs.Record(EncoderProfile.Nvenc, 1, 1920, 1080, "a.mkv", gentle);
    Check("gentle: preset p4, tune hq, VBR cq 24, no B-frames, no spatial AQ, no lookahead, multipass off, 2 s keyframes",
      g.Contains("-c:v hevc_nvenc -preset p4 -tune hq -profile:v main -rc vbr -cq 24 -b:v 0") && g.Contains("-bf 0") && g.Contains("-spatial-aq 0") && g.Contains("-rc-lookahead 0") && g.Contains("-multipass disabled") && g.Contains("-g 120"), g);
    Check("gentle: bufsize equals the ceiling (30000k), not double", g.Contains("-maxrate 30000k -bufsize 30000k"));
    Check("gentle: no B-frame reference mode, no forced IDR", !g.Contains("-b_ref_mode") && !g.Contains("-forced-idr") && !g.Contains("-spatial-aq 1"));
    string st = FfmpegArgs.Record(EncoderProfile.Nvenc, 1, 1920, 1080, "a.mkv", new AppSettings());
    Check("standard is unchanged: preset p5, 2 B-frames, spatial AQ, bufsize double", st.Contains("-preset p5") && st.Contains("-bf 2 -b_ref_mode middle") && st.Contains("-spatial-aq 1") && st.Contains("-maxrate 30000k -bufsize 60000k"));
    Check("gentle changes only the NVENC profile (AMF and QSV arguments are the same as standard)",
      FfmpegArgs.Record(EncoderProfile.Amf, 1, 1920, 1080, "a.mkv", gentle) == FfmpegArgs.Record(EncoderProfile.Amf, 1, 1920, 1080, "a.mkv") && FfmpegArgs.Record(EncoderProfile.Qsv, 1, 1920, 1080, "a.mkv", gentle) == FfmpegArgs.Record(EncoderProfile.Qsv, 1, 1920, 1080, "a.mkv"));

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
    Check("chip: standard native", MainForm.ChipText(EncoderProfile.Nvenc, new AppSettings(), null) == "HEVC · NVENC · standard · native · GPU high", MainForm.ChipText(EncoderProfile.Nvenc, new AppSettings(), null));
    Check("chip: gentle 1080p GPU normal", MainForm.ChipText(EncoderProfile.Nvenc, new AppSettings { EncoderProfile = "gentle", MaxHeight = 1080, GpuPriority = "normal" }, null) == "HEVC · NVENC · gentle · 1080p · GPU normal");
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
      CheckLog(logPath, "main recording", true);

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

      // M1b: the switches through the window: gentle encoder, scaled to 360 high, GPU priority left at normal
      Console.WriteLine("-- M1b switches: gentle encoder, 360p, GPU priority normal");
      var swin = TestWindow.Open(Scene.Simple, 1280, 720, true, WindowPrefix + ": gentle and scaled");
      Invoke(form, delegate { form.Config = new AppSettings { EncoderProfile = "gentle", MaxHeight = 360, GpuPriority = "normal" }; form.ReloadWindows(); form.SelectHwnd(swin.Handle); });
      form.LastResult = null;
      Invoke(form, delegate { form.PressRecord(); });
      Check("switches: recording started", WaitFor(delegate { return form.Mode == MainForm.State.Recording; }, 30000, "recording"), form.StatusText);
      var srec = form.Current;
      Thread.Sleep(5000);
      Check("switches: the chip shows gentle, 360p and GPU normal", form.ChipTextNow == "HEVC · NVENC · gentle · 360p · GPU normal", form.ChipTextNow);
      Check("switches: ffmpeg was started with the gentle options and a 640x360 capture", srec.Command.Contains("-preset p4") && srec.Command.Contains("-bf 0") && srec.Command.Contains("width=640:height=360") && srec.Command.Contains("resize_mode=scale_aspect"), srec.Command);
      Shot(form.Handle, "02-chip-gentle-scaled.png", true);
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
      Check("switches: the log records the settings, the scaling and that GPU priority was left at normal", stext.Contains("settings: EncoderProfile=gentle MaxHeight=360 GpuPriority=normal") && stext.Contains("(scaled by MaxHeight)") && stext.Contains("left at NORMAL"), slog);
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
    Check(what + ": the log has the start time, settings, encoder, window and video size, and the ffmpeg command", text.Contains("started 20") && text.Contains("settings: EncoderProfile=standard MaxHeight=0 GpuPriority=high") && text.Contains("encoder hevc_nvenc") && text.Contains("window client area") && text.Contains("ffmpeg -hide_banner"));
    Check(what + ": about one progress line a second (" + lines + ")", lines >= 15, lines.ToString());
    Check(what + ": the log says Stop was pressed, that ffmpeg was not forced, and how long Stop took", text.Contains("Stop pressed") && text.Contains("forced: no") && text.Contains("Stop took"));
    Check(what + ": the log has the GPU priority result with its NTSTATUS", text.Contains("D3DKMTSetProcessSchedulingPriorityClass(HIGH) = 0x"));
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

  // A picture of one of the tests' own windows (PrintWindow, full content). keep puts it in docs\screenshots\m1b for the Dev timeline;
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
    // --bench <Scene> <width> <height> <seconds> [standard|gentle] [MaxHeight] [high|normal]
    var cfg = new AppSettings { EncoderProfile = a.Length > 5 ? a[5] : "standard", MaxHeight = a.Length > 6 ? int.Parse(a[6]) : 0, GpuPriority = a.Length > 7 ? a[7] : "high" };
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
    return 0;
  }
}
