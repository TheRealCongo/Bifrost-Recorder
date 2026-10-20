// SPDX-License-Identifier: GPL-2.0-or-later
// Recording one window with libobs (Obs.cs): the capture, the program's sound and the mic, the encoders and the MP4, then stopping cleanly.
// OBS's hybrid MP4 output writes the file as it goes and finishes it on Stop: a crash leaves a file that still plays. C# 5 for Windows' own compiler.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Which encoder the header chip names before the engine has started: the one for the GPU's maker, read from DXGI (no GPU work at all). The
// engine's own pick (the encoders whose test passed) replaces it at the first Record.
static class EncoderChoice {
  static EncoderProfile picked;
  static readonly object gate = new object();

  public static EncoderProfile Picked { get { lock (gate) return picked; } }

  public static EncoderProfile ByVendor() {
    EncoderProfile pick = EncoderProfile.Software;
    foreach (var o in Dxgi.Outputs()) {
      if (o.VendorId == 0x10DE) pick = EncoderProfile.Nvenc; else if (o.VendorId == 0x1002) pick = EncoderProfile.Amf; else if (o.VendorId == 0x8086) pick = EncoderProfile.Qsv; else continue;
      break;
    }
    lock (gate) picked = pick;
    return pick;
  }

  public static void Set(EncoderProfile e) { lock (gate) picked = e; }
  public static void Reset() { lock (gate) picked = null; }   // for the tests
}

class RecordingResult {
  public bool Ok;              // an .mp4 was written and finished
  public string Mp4;
  public string Message;
  public double Seconds;       // length of the saved video (frames written / 60)
  public long Bytes;           // size of the saved file
  public long Frames, Skipped, Lagged;
  public bool Forced;          // the output had to be stopped by force
  public double StopSeconds;   // how long Stop took
}

// How the recording is doing, from libobs's own counters: frames written, frames the encoder had no time for (skipped), and frames the GPU
// drew late (lagged: libobs repeats the last picture). Over the last few seconds, more than 5% of frames missed is "falling behind". A game
// whose picture isn't arriving (game capture not hooked yet, or the game minimized) is "waiting", not behind.
class Telemetry {
  public const double WindowSeconds = 4.0, BehindAbove = 0.05, BehindForSeconds = 3.0;
  public long Frames, Skipped, Lagged, Bytes;
  public bool NoPicture, Behind;
  public double MissedShare = double.NaN;   // share of the frames due in the window that were skipped or late
  public string MissedBy = "";              // "the encoder" or "the GPU"
  readonly List<double[]> samples = new List<double[]>();   // { clock seconds, skipped, lagged }
  double behindSince = -1;

  public void Feed(double now, long frames, long skipped, long lagged, long bytes, bool picture) {
    lock (this) {
      Frames = frames; Skipped = skipped; Lagged = lagged; Bytes = bytes; NoPicture = !picture;
      samples.Add(new double[] { now, skipped, lagged });
      while (samples.Count > 2 && now - samples[0][0] > WindowSeconds) samples.RemoveAt(0);
      MissedShare = double.NaN;
      var a = samples[0]; var b = samples[samples.Count - 1];
      double span = b[0] - a[0];
      if (span >= 2.0) {
        double sk = b[1] - a[1], lg = b[2] - a[2];
        MissedShare = (sk + lg) / (span * Sizes.Fps);
        MissedBy = sk >= lg ? "the encoder" : "the GPU";
      }
      if (NoPicture) behindSince = -1;
      else if (!double.IsNaN(MissedShare)) {
        if (MissedShare > BehindAbove) { if (behindSince < 0) behindSince = now; }
        else behindSince = -1;
      }
      Behind = behindSince >= 0 && now - behindSince > BehindForSeconds;
    }
  }

  public string Text {
    get {
      lock (this) {
        if (NoPicture) return "Waiting for the picture";
        if (Behind) return "Falling behind: " + Math.Round(MissedShare * 100) + "% of frames late (" + MissedBy + " is busy)";
        return "Keeping up";
      }
    }
  }

  public string Line(double now) {
    lock (this)
      return "frames=" + Frames + " skipped=" + Skipped + " lagged=" + Lagged + " late=" + (double.IsNaN(MissedShare) ? "?" : (MissedShare * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%") +
             " size=" + (Bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + "MB t=" + now.ToString("0.0", CultureInfo.InvariantCulture) + "s " +
             (NoPicture ? "[no picture]" : Behind ? "[BEHIND]" : "[ok]");
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

// One recording of one window. Make it on the window's thread, start the engine there with its canvas (Obs.Start(BaseWidth, BaseHeight,
// OutWidth, OutHeight)), then Start returns once the first picture has arrived and the file is being written; Stop finishes the file.
class Recording {
  public readonly WindowInfo Window;
  public readonly AppSettings Config;
  public readonly Telemetry Tele = new Telemetry();
  public readonly string Mp4Path;
  public readonly int BaseWidth, BaseHeight;   // the canvas: the window's client area, or the monitor
  public int OutWidth, OutHeight;              // the size of the video
  public EncoderProfile Encoder;               // the encoder in use (the one asked for, or the next that started)
  public string Capture;                       // the capture in use: "game", "window" or "screen" (a game capture with no picture may have switched)
  readonly EncoderProfile asked;
  readonly RecordingLog log;
  readonly DxgiOutput screen;
  IntPtr scene, video, sound, mic, venc, output;
  readonly List<IntPtr> aencs = new List<IntPtr>();
  Stopwatch clock;
  Thread monitor;
  volatile bool stopping, ended, live;   // live: the output has started
  int endedRaised;
  long skipped0, lagged0;
  volatile int stopCode = int.MinValue;
  readonly ManualResetEvent stopped = new ManualResetEvent(false);
  readonly ObsApi.SignalCallback onStop;   // kept alive while libobs holds it
  static bool gpuRaised;                   // the Recorder's GPU class was raised by an earlier recording
  public static string MicDevice = "default";   // the tests name a device that doesn't exist, so the three tracks are made without opening anyone's mic
  // The window closed, or the output stopped by itself; the reason. Raised once, from a worker thread. Whoever subscribes after it has happened
  // (the window subscribes a moment after Start returns) is told at once, from the subscribing thread.
  public event Action<string> Ended {
    add {
      string already;
      lock (endedGate) { endedHandler += value; already = endedReason; }
      if (already != null) value(already);
    }
    remove { lock (endedGate) endedHandler -= value; }
  }
  readonly object endedGate = new object();
  Action<string> endedHandler;
  string endedReason;                      // set once Ended has been raised for a recording that was not being stopped by the Recorder

  // encoder null = the engine's pick (Obs.PickEncoder). logFolder null = no log. screen: the monitor the window is on, for screen capture.
  public Recording(EncoderProfile encoder, WindowInfo window, int clientWidth, int clientHeight, string folder, string baseName, AppSettings cfg = null, string logFolder = null, DxgiOutput screen = null) {
    asked = Encoder = encoder; Window = window; this.screen = screen;
    Config = cfg ?? new AppSettings();
    Capture = Config.CaptureMethod;
    Mp4Path = Path.Combine(folder, baseName + ".mp4");
    if (Config.Screen && screen != null) { BaseWidth = Math.Max(2, screen.Width & ~1); BaseHeight = Math.Max(2, screen.Height & ~1); }
    else { BaseWidth = Math.Max(2, clientWidth & ~1); BaseHeight = Math.Max(2, clientHeight & ~1); }
    bool scaled;
    Sizes.OutputSize(BaseWidth, BaseHeight, Config.MaxHeight, out OutWidth, out OutHeight, out scaled);
    log = new RecordingLog(logFolder, baseName);
    onStop = OnStop;
  }

  public void LogLine(string text) { log.Line(text); }
  public string LogPath { get { return log.Path; } }
  public long Frames { get { return Tele.Frames; } }
  public long Skipped { get { return Tele.Skipped + Tele.Lagged; } }
  public string Health { get { return Tele.Text; } }
  public bool Behind { get { return Tele.Behind; } }
  public TimeSpan Elapsed { get { return clock == null ? TimeSpan.Zero : clock.Elapsed; } }
  public long FileBytes { get { try { return output != IntPtr.Zero ? (long)ObsApi.obs_output_get_total_bytes(output) : 0; } catch (Exception) { return 0; } } }
  public bool Alive { get { return output != IntPtr.Zero && !ended && ObsApi.obs_output_active(output); } }
  public bool HasPicture { get { return video != IntPtr.Zero && ObsApi.obs_source_get_width(video) > 0; } }

  static IntPtr Create(string id, string name, Dictionary<string, object> values) {
    using (var d = Data(values)) return ObsApi.obs_source_create(ObsApi.U(id), ObsApi.U(name), d.Ptr, IntPtr.Zero);
  }

  static ObsApi.Data Data(Dictionary<string, object> values) {
    var d = new ObsApi.Data();
    foreach (var kv in values) {
      if (kv.Value is string) d.Set(kv.Key, (string)kv.Value);
      else if (kv.Value is bool) d.Set(kv.Key, (bool)kv.Value);
      else d.Set(kv.Key, Convert.ToInt64(kv.Value, CultureInfo.InvariantCulture));
    }
    return d;
  }

  string Spec { get { return Sources.WindowSpec(Window.Title, Window.ClassName, Window.ExeName); } }

  // The capture's source, added to the scene so it is drawn into the canvas (scaled to fit, centred). Replaces the one there was.
  bool UseCapture(string method) {
    if (video != IntPtr.Zero) { ObsApi.obs_source_release(video); video = IntPtr.Zero; }
    if (scene != IntPtr.Zero) { ObsApi.obs_set_output_source(0, IntPtr.Zero); ObsApi.obs_scene_release(scene); }
    scene = ObsApi.obs_scene_create(ObsApi.U("Bifrost Recorder"));
    video = Create(Sources.Id(method), "Capture", Sources.VideoSettings(method, Spec, screen != null ? screen.DeviceName : null));
    if (video == IntPtr.Zero) { log.Line("libobs has no " + Sources.Id(method) + " source"); return false; }
    IntPtr item = ObsApi.obs_scene_add(scene, video);
    var bounds = new ObsApi.Vec2 { X = BaseWidth, Y = BaseHeight };
    ObsApi.obs_sceneitem_set_bounds_type(item, ObsApi.BoundsScaleInner);
    ObsApi.obs_sceneitem_set_bounds_alignment(item, ObsApi.AlignCenter);
    ObsApi.obs_sceneitem_set_bounds(item, ref bounds);
    ObsApi.obs_set_output_source(0, ObsApi.obs_scene_get_source(scene));
    Capture = method;
    log.Line("capture: " + Sources.Id(method) + (method == "screen" ? " of " + (screen != null ? screen.ToString() : "?") : " of [" + Window.ExeName + "] " + Window.Title));
    return true;
  }

  // The program's sound (only that program's, or everything the PC plays) and the mic, each feeding its tracks (Sources.TrackNames).
  void UseSound() {
    if (Config.Sound == "pc") sound = Create("wasapi_output_capture", "PC sound", new Dictionary<string, object> { { "device_id", "default" } });
    else {
      sound = Create("wasapi_process_output_capture", "Game", new Dictionary<string, object> { { "window", Spec }, { "priority", Sources.MatchExe } });
      if (sound == IntPtr.Zero) {   // Windows before 10 2004 can't capture one program's sound
        log.Line("this Windows can't capture one program's sound: everything the PC plays is recorded instead");
        sound = Create("wasapi_output_capture", "PC sound", new Dictionary<string, object> { { "device_id", "default" } });
      }
    }
    if (sound != IntPtr.Zero) { ObsApi.obs_source_set_audio_mixers(sound, Sources.SoundMixers(Config)); ObsApi.obs_set_output_source(1, sound); }
    if (Config.WithMic) {
      mic = Create("wasapi_input_capture", "Mic", new Dictionary<string, object> { { "device_id", MicDevice } });
      if (mic != IntPtr.Zero) { ObsApi.obs_source_set_audio_mixers(mic, Sources.MicMixers(Config)); ObsApi.obs_set_output_source(2, mic); }
    }
    log.Line("sound: " + (Config.Sound == "pc" ? "everything the PC plays" : "the program's own") + (Config.WithMic ? ", mic on" : ", mic off") + "; tracks " + string.Join(", ", Sources.TrackNames(Config)));
  }

  void OnStop(IntPtr data, IntPtr calldata) {
    stopCode = (int)ObsApi.CallInt(calldata, "code");
    stopped.Set();
    if (!stopping && live) Raise("The recording stopped by itself: " + StopReason());
  }

  string StopReason() {
    string e = output != IntPtr.Zero ? ObsApi.S(ObsApi.obs_output_get_last_error(output)) : null;
    if (!string.IsNullOrEmpty(e)) return e;
    switch (stopCode) { case -1: return "the folder can't be written to"; case -7: return "the disk is full"; case -8: return "the encoder failed"; case -6: return "this encoder or format isn't supported"; default: return "code " + stopCode; }
  }

  // Starts the video encoder and the file. Tries the encoders after this one when it won't start (a busy or missing GPU encoder).
  bool StartOutput(out string error) {
    error = null;
    using (var d = Data(new Dictionary<string, object> { { "path", Mp4Path } })) output = ObsApi.obs_output_create(ObsApi.U("mp4_output"), ObsApi.U("Recording"), d.Ptr, IntPtr.Zero);
    if (output == IntPtr.Zero) { error = "libobs has no MP4 output (obs-outputs)."; return false; }
    ObsApi.signal_handler_connect(ObsApi.obs_output_get_signal_handler(output), ObsApi.U("stop"), onStop, IntPtr.Zero);
    string[] tracks = Sources.TrackNames(Config);
    for (int i = 0; i < tracks.Length; i++) {
      IntPtr a;
      using (var d = Data(new Dictionary<string, object> { { "bitrate", 160 } })) a = ObsApi.obs_audio_encoder_create(ObsApi.U("ffmpeg_aac"), ObsApi.U(tracks[i]), d.Ptr, (UIntPtr)(uint)i, IntPtr.Zero);
      if (a == IntPtr.Zero) { error = "libobs has no AAC encoder (obs-ffmpeg)."; return false; }
      ObsApi.obs_encoder_set_audio(a, ObsApi.obs_get_audio());
      ObsApi.obs_output_set_audio_encoder(output, a, (UIntPtr)(uint)i);
      aencs.Add(a);
    }
    int maxrate = Sizes.MaxrateKbps(OutWidth, OutHeight);
    foreach (var e in EncoderProfile.FallbackChain(asked ?? Obs.PickEncoder(), Obs.Encoders)) {
      if (venc != IntPtr.Zero) { ObsApi.obs_encoder_release(venc); venc = IntPtr.Zero; }
      using (var d = Data(e.Settings(maxrate, Sizes.Quality, Config.Quality))) venc = ObsApi.obs_video_encoder_create(ObsApi.U(e.Name), ObsApi.U("Video"), d.Ptr, IntPtr.Zero);
      if (venc == IntPtr.Zero) { log.Line("encoder " + e.Name + ": libobs couldn't create it"); continue; }
      ObsApi.obs_encoder_set_video(venc, ObsApi.obs_get_video());
      ObsApi.obs_output_set_video_encoder(output, venc);
      log.Line("encoder " + e.Name + " (" + e.Label + "), quality " + Sizes.Quality + ", ceiling " + maxrate + " kb/s, " + (Config.Quality ? "quality" : "light") + " set");
      skipped0 = ObsApi.video_output_get_skipped_frames(ObsApi.obs_get_video());
      lagged0 = ObsApi.obs_get_lagged_frames();
      if (ObsApi.obs_output_start(output)) { Encoder = e; live = true; return true; }
      error = ObsApi.S(ObsApi.obs_output_get_last_error(output)) ?? "the encoder didn't start";
      log.Line("encoder " + e.Name + " didn't start: " + error);
      stopped.Reset(); stopCode = int.MinValue;   // a failed start may have signalled "stop"
    }
    error = "No encoder would start: " + error;
    return false;
  }

  // Starts the recording. When it can't start, for whatever reason, the recording's log file is closed again.
  public bool Start(out string error, int firstPictureTimeoutMs) {
    bool ok = false;
    try { ok = StartCore(out error, firstPictureTimeoutMs); }
    finally { if (!ok) log.Close(); }
    return ok;
  }

  bool StartCore(out string error, int firstPictureTimeoutMs) {
    error = null;
    if (!Native.IsWindow(Window.Handle)) { error = "That window has gone."; return false; }
    if (!Obs.Started) { error = "The recording engine isn't running."; return false; }
    try { Directory.CreateDirectory(Path.GetDirectoryName(Mp4Path)); }
    catch (Exception ex) { error = "Can't make the recordings folder: " + ex.Message; return false; }
    // Before any capture or encoder: can the file be made at all? libobs only says "Unable to open file" and then every encoder looks as if it
    // refused to start (a GPU encoder session is opened for each), so a folder that can't be written to would read as an encoder fault.
    try {
      bool existed = File.Exists(Mp4Path);
      using (new FileStream(Mp4Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) { }
      if (!existed) File.Delete(Mp4Path);
    } catch (Exception ex) { error = "Can't write to the recordings folder: " + ex.Message; log.Line(error); log.Close(); return false; }
    Obs.Sink = LogLine;   // libobs's own messages go to this recording's log
    log.Line("Bifrost Recorder " + Application.ProductVersion + " with libobs " + Obs.Version + " (OBS Studio's engine)");
    lock (Obs.StartupLog) {   // the first recording's log also gets the engine's start: the GPUs, the monitors, which encoders passed their tests
      if (Obs.StartupLog.Count > 0) { log.Line("---- the engine's start"); foreach (string l in Obs.StartupLog) log.Line(l); log.Line("---- end of the engine's start"); Obs.StartupLog.Clear(); }
    }
    log.Line("started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ", " + Environment.ProcessorCount + " logical processors");
    log.Line("canvas " + BaseWidth + "x" + BaseHeight + ", video " + OutWidth + "x" + OutHeight + " at " + Sizes.Fps + " fps" + (OutHeight != BaseHeight ? " (scaled by MaxHeight)" : ""));
    log.Line("settings: EncoderProfile=" + Config.EncoderProfile + " MaxHeight=" + Config.MaxHeight + " CaptureMethod=" + Config.CaptureMethod + " Sound=" + Config.Sound + " Mic=" + Config.Mic + " GpuPriority=" + Config.GpuPriority);
    log.Line("encoders libobs offers: " + string.Join(", ", Obs.Encoders.ToArray()));
    SetGpuPriority();

    try {
      if (!UseCapture(Config.CaptureMethod)) { error = "The recording engine can't capture that way."; Cleanup(); return false; }
      var waited = Stopwatch.StartNew(); var total = Stopwatch.StartNew();
      DateTime nudged = DateTime.MinValue;
      while (!HasPicture) {
        if (!Native.IsWindow(Window.Handle)) { error = "That window has gone."; Cleanup(); return false; }
        string next = CaptureChoice.WhenNoPicture(Capture, (int)waited.ElapsedMilliseconds, Native.IsMinimized(Window.Handle));
        if (next == "fail" || total.ElapsedMilliseconds > firstPictureTimeoutMs) {
          error = "That window sent no picture" + (Capture == "game" ? " (game capture couldn't hook it)" : "") + ". It may be minimized, or not drawing anything.";
          log.Line(error + " after " + total.ElapsedMilliseconds + " ms"); Cleanup(); return false;
        }
        if (next != null && next != Capture) {
          log.Line(Capture + " capture sent no picture in " + waited.ElapsedMilliseconds + " ms: switching to " + next + " capture");
          if (!UseCapture(next)) { error = "The recording engine can't capture that way."; Cleanup(); return false; }
          waited.Restart();
        }
        if (Capture == "window" && (DateTime.UtcNow - nudged).TotalMilliseconds > 400) { nudged = DateTime.UtcNow; Native.AskToRepaint(Window.Handle); }   // a quiet window sends nothing until it redraws
        Thread.Sleep(50);
      }
      log.Line("first picture after " + total.ElapsedMilliseconds + " ms: " + ObsApi.obs_source_get_width(video) + "x" + ObsApi.obs_source_get_height(video) + " from " + Capture + " capture");
      UseSound();
      if (!StartOutput(out error)) { log.Line(error); Cleanup(); return false; }
    } catch (Exception ex) { error = "The recording engine failed: " + ex.Message; log.Line(ex.ToString()); Cleanup(); return false; }

    clock = Stopwatch.StartNew();
    log.Line("recording to " + Mp4Path);
    monitor = new Thread(Watch) { IsBackground = true, Name = "recording watch" };
    monitor.Start();
    return true;
  }

  // The GPU scheduling class of the Recorder's own process, where libobs draws and encodes: HIGH when asked for, back to NORMAL when not.
  void SetGpuPriority() {
    IntPtr me = Process.GetCurrentProcess().Handle;
    if (Config.GpuPriority == "high") { if (GpuScheduling.Try(me, delegate(string s) { log.Line("gpu priority: " + s); }) > 0) gpuRaised = true; }
    else if (gpuRaised) { GpuScheduling.Set(me, GpuScheduling.Normal, delegate(string s) { log.Line("gpu priority: " + s); }); gpuRaised = false; }
    else log.Line("gpu priority: left as it is (GpuPriority=normal)");
  }

  // Every 200 ms: libobs's counters into the telemetry, a log line each second, and whether the window or the output has gone.
  void Watch() {
    double nextLog = 1.0;
    IntPtr v = ObsApi.obs_get_video();
    while (!ended) {
      Thread.Sleep(200);
      if (ended) return;
      double t = clock.Elapsed.TotalSeconds;
      try {
        Tele.Feed(t, ObsApi.obs_output_get_total_frames(output), ObsApi.video_output_get_skipped_frames(v) - skipped0, ObsApi.obs_get_lagged_frames() - lagged0, (long)ObsApi.obs_output_get_total_bytes(output), HasPicture);
      } catch (Exception) { }
      if (t >= nextLog) { nextLog += 1.0; log.Line(Tele.Line(t)); }
      if (!Native.IsWindow(Window.Handle)) { Raise("The window was closed"); return; }
    }
  }

  void Raise(string reason) {
    if (Interlocked.Exchange(ref endedRaised, 1) != 0) return;
    log.Line("ended: " + reason);
    Action<string> h;
    lock (endedGate) {
      if (stopping) return;
      endedReason = reason; h = endedHandler;
    }
    if (h != null) h(reason);
  }

  // Everything this recording made in libobs, released; the engine itself keeps running for the next one. The log is closed unless keepLog.
  void Cleanup(bool keepLog = false) {
    ended = true;
    try {
      if (output != IntPtr.Zero) { ObsApi.signal_handler_disconnect(ObsApi.obs_output_get_signal_handler(output), ObsApi.U("stop"), onStop, IntPtr.Zero); ObsApi.obs_output_release(output); output = IntPtr.Zero; }
      if (venc != IntPtr.Zero) { ObsApi.obs_encoder_release(venc); venc = IntPtr.Zero; }
      foreach (var a in aencs) ObsApi.obs_encoder_release(a);
      aencs.Clear();
      for (uint ch = 0; ch < 3; ch++) ObsApi.obs_set_output_source(ch, IntPtr.Zero);
      if (sound != IntPtr.Zero) { ObsApi.obs_source_release(sound); sound = IntPtr.Zero; }
      if (mic != IntPtr.Zero) { ObsApi.obs_source_release(mic); mic = IntPtr.Zero; }
      if (video != IntPtr.Zero) { ObsApi.obs_source_release(video); video = IntPtr.Zero; }
      if (scene != IntPtr.Zero) { ObsApi.obs_scene_release(scene); scene = IntPtr.Zero; }
    } catch (Exception ex) { log.Line("cleanup: " + ex.Message); }
    try { if (File.Exists(Mp4Path) && new FileInfo(Mp4Path).Length < 2048) File.Delete(Mp4Path); } catch (Exception) { }
    if (Obs.Sink == (Action<string>)LogLine) Obs.Sink = null;
    if (!keepLog) log.Close();
  }

  // Ends the recording: libobs finishes encoding what it has, then the MP4 is completed. Up to 30 s before it is stopped by force. Blocks.
  public RecordingResult Stop() {
    var res = new RecordingResult();
    var total = Stopwatch.StartNew();
    stopping = true;
    if (output == IntPtr.Zero) { res.Message = "Nothing was started."; return res; }
    double at = clock == null ? 0 : clock.Elapsed.TotalSeconds;
    log.Line("Stop pressed at " + at.ToString("0.0", CultureInfo.InvariantCulture) + " s; " + Tele.Text);
    ObsApi.obs_output_stop(output);
    if (!stopped.WaitOne(30000)) { res.Forced = true; log.Line("the output didn't finish in 30 s: stopping it by force"); ObsApi.obs_output_force_stop(output); stopped.WaitOne(5000); }
    ended = true;
    if (monitor != null) monitor.Join(2000);
    res.Frames = ObsApi.obs_output_get_total_frames(output);
    res.Skipped = Tele.Skipped; res.Lagged = Tele.Lagged;
    string reason = stopCode == ObsApi.OutputSuccess ? null : StopReason();
    log.Line("output stopped " + total.ElapsedMilliseconds + " ms after Stop, code " + stopCode + (res.Forced ? ", forced" : "") + "; " + Tele.Line(at));
    Cleanup(true);
    res.Seconds = res.Frames / (double)Sizes.Fps;
    res.Bytes = File.Exists(Mp4Path) ? new FileInfo(Mp4Path).Length : 0;
    res.Ok = res.Bytes > 0 && res.Frames > 0 && reason == null;
    if (res.Ok) { res.Mp4 = Mp4Path; res.Message = "Saved " + Path.GetFileName(Mp4Path); }
    else res.Message = res.Bytes > 0 ? "The recording was kept as " + Path.GetFileName(Mp4Path) + ", but it may be incomplete: " + (reason ?? "no frames were written") : "Nothing was recorded" + (reason != null ? ": " + reason : ".");
    if (res.Bytes > 0 && !res.Ok) res.Mp4 = Mp4Path;
    res.StopSeconds = total.Elapsed.TotalSeconds;
    log.Line(res.Message + ": " + res.Seconds.ToString("0.000", CultureInfo.InvariantCulture) + " s of video (clock said " + at.ToString("0.0", CultureInfo.InvariantCulture) + " s), " +
             (res.Bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB; Stop took " + res.StopSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s");
    log.Close();
    return res;
  }
}
