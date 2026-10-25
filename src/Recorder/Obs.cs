// SPDX-License-Identifier: GPL-2.0-or-later
// libobs, the engine of OBS Studio (GPL-2.0-or-later, https://github.com/obsproject/obs-studio), called from C#: only what the Recorder uses.
// The OBS files are OBS Studio 32.2.2's own released files, unmodified: obs.dll and the libraries it needs beside BifrostRecorder.exe (libobs
// looks for its helper programs, such as obs-nvenc-test.exe, next to the running program), the plugins in obs\obs-plugins\64bit and their data
// in obs\data. THIRD-PARTY-NOTICES.md lists every one and where its source is. Strings cross as UTF-8. C# 5 for Windows' own compiler.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// The C functions, as obs.h (and obs-data.h, calldata.h, signal.h, base.h, video-io.h) of OBS 32.2.2 declare them.
static class ObsApi {
  const string L = "obs.dll";
  const CallingConvention C = CallingConvention.Cdecl;

  [StructLayout(LayoutKind.Sequential)]
  public struct VideoInfo {                // struct obs_video_info
    public IntPtr GraphicsModule;          // const char *, "libobs-d3d11"
    public uint FpsNum, FpsDen, BaseWidth, BaseHeight, OutputWidth, OutputHeight;
    public int OutputFormat;               // enum video_format
    public uint Adapter;
    [MarshalAs(UnmanagedType.U1)] public bool GpuConversion;
    public int Colorspace, Range, ScaleType;
  }
  [StructLayout(LayoutKind.Sequential)] public struct AudioInfo { public uint SamplesPerSec; public int Speakers; }   // struct obs_audio_info
  [StructLayout(LayoutKind.Sequential)] public struct Vec2 { public float X, Y; }

  public const int VideoFormatNV12 = 2, Cs709 = 2, RangePartial = 1, ScaleBicubic = 2, SpeakersStereo = 2;
  public const int BoundsScaleInner = 2;   // enum obs_bounds_type: fit inside, keep the shape
  public const int AlignCenter = 0;
  public const int VideoSuccess = 0;       // obs_reset_video
  public const int LogError = 100, LogWarning = 200, LogInfo = 300;
  public const int OutputSuccess = 0;      // the code of an output's "stop" signal

  [UnmanagedFunctionPointer(C)] public delegate void LogHandler(int level, IntPtr format, IntPtr args, IntPtr param);
  [UnmanagedFunctionPointer(C)] public delegate void SignalCallback(IntPtr data, IntPtr calldata);

  [DllImport(L, CallingConvention = C)] public static extern void base_set_log_handler(LogHandler handler, IntPtr param);
  [DllImport(L, CallingConvention = C)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_startup(byte[] locale, byte[] moduleConfigPath, IntPtr store);
  [DllImport(L, CallingConvention = C)] public static extern void obs_shutdown();
  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_get_version_string();
  [DllImport(L, CallingConvention = C)] public static extern int obs_reset_video(ref VideoInfo ovi);
  [DllImport(L, CallingConvention = C)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_reset_audio(ref AudioInfo oai);
  [DllImport(L, CallingConvention = C)] public static extern void obs_add_data_path(byte[] path);
  [DllImport(L, CallingConvention = C)] public static extern void obs_add_module_path(byte[] bin, byte[] data);
  [DllImport(L, CallingConvention = C)] public static extern void obs_load_all_modules();
  [DllImport(L, CallingConvention = C)] public static extern void obs_post_load_modules();
  [DllImport(L, CallingConvention = C)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_enum_encoder_types(UIntPtr idx, out IntPtr id);
  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_get_encoder_codec(byte[] id);
  [DllImport(L, CallingConvention = C)] public static extern void obs_set_output_source(uint channel, IntPtr source);
  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_get_video();
  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_get_audio();
  [DllImport(L, CallingConvention = C)] public static extern uint obs_get_lagged_frames();
  [DllImport(L, CallingConvention = C)] public static extern void obs_set_video_levels(float sdrWhiteLevel, float hdrNominalPeakLevel);

  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_data_create();
  [DllImport(L, CallingConvention = C)] public static extern void obs_data_release(IntPtr data);
  [DllImport(L, CallingConvention = C)] public static extern void obs_data_set_string(IntPtr data, byte[] name, byte[] val);
  [DllImport(L, CallingConvention = C)] public static extern void obs_data_set_int(IntPtr data, byte[] name, long val);
  [DllImport(L, CallingConvention = C)] public static extern void obs_data_set_bool(IntPtr data, byte[] name, [MarshalAs(UnmanagedType.U1)] bool val);

  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_source_create(byte[] id, byte[] name, IntPtr settings, IntPtr hotkeyData);
  [DllImport(L, CallingConvention = C)] public static extern void obs_source_release(IntPtr source);
  [DllImport(L, CallingConvention = C)] public static extern uint obs_source_get_width(IntPtr source);
  [DllImport(L, CallingConvention = C)] public static extern uint obs_source_get_height(IntPtr source);
  [DllImport(L, CallingConvention = C)] public static extern void obs_source_set_audio_mixers(IntPtr source, uint mixers);
  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_source_get_signal_handler(IntPtr source);

  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_scene_create(byte[] name);
  [DllImport(L, CallingConvention = C)] public static extern void obs_scene_release(IntPtr scene);
  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_scene_get_source(IntPtr scene);
  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_scene_add(IntPtr scene, IntPtr source);
  [DllImport(L, CallingConvention = C)] public static extern void obs_sceneitem_set_bounds_type(IntPtr item, int type);
  [DllImport(L, CallingConvention = C)] public static extern void obs_sceneitem_set_bounds_alignment(IntPtr item, uint alignment);
  [DllImport(L, CallingConvention = C)] public static extern void obs_sceneitem_set_bounds(IntPtr item, ref Vec2 bounds);

  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_video_encoder_create(byte[] id, byte[] name, IntPtr settings, IntPtr hotkeyData);
  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_audio_encoder_create(byte[] id, byte[] name, IntPtr settings, UIntPtr mixerIdx, IntPtr hotkeyData);
  [DllImport(L, CallingConvention = C)] public static extern void obs_encoder_set_video(IntPtr encoder, IntPtr video);
  [DllImport(L, CallingConvention = C)] public static extern void obs_encoder_set_audio(IntPtr encoder, IntPtr audio);
  [DllImport(L, CallingConvention = C)] public static extern void obs_encoder_release(IntPtr encoder);

  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_output_create(byte[] id, byte[] name, IntPtr settings, IntPtr hotkeyData);
  [DllImport(L, CallingConvention = C)] public static extern void obs_output_release(IntPtr output);
  [DllImport(L, CallingConvention = C)] public static extern void obs_output_set_video_encoder(IntPtr output, IntPtr encoder);
  [DllImport(L, CallingConvention = C)] public static extern void obs_output_set_audio_encoder(IntPtr output, IntPtr encoder, UIntPtr idx);
  [DllImport(L, CallingConvention = C)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_output_start(IntPtr output);
  [DllImport(L, CallingConvention = C)] public static extern void obs_output_stop(IntPtr output);
  [DllImport(L, CallingConvention = C)] public static extern void obs_output_force_stop(IntPtr output);
  [DllImport(L, CallingConvention = C)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool obs_output_active(IntPtr output);
  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_output_get_last_error(IntPtr output);
  [DllImport(L, CallingConvention = C)] public static extern IntPtr obs_output_get_signal_handler(IntPtr output);
  [DllImport(L, CallingConvention = C)] public static extern ulong obs_output_get_total_bytes(IntPtr output);
  [DllImport(L, CallingConvention = C)] public static extern int obs_output_get_total_frames(IntPtr output);

  [DllImport(L, CallingConvention = C)] public static extern uint video_output_get_skipped_frames(IntPtr video);
  [DllImport(L, CallingConvention = C)] public static extern uint video_output_get_total_frames(IntPtr video);

  [DllImport(L, CallingConvention = C)] public static extern void signal_handler_connect(IntPtr handler, byte[] signal, SignalCallback callback, IntPtr data);
  [DllImport(L, CallingConvention = C)] public static extern void signal_handler_disconnect(IntPtr handler, byte[] signal, SignalCallback callback, IntPtr data);
  [DllImport(L, CallingConvention = C)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool calldata_get_data(IntPtr data, byte[] name, out long val, UIntPtr size);
  [DllImport(L, CallingConvention = C)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool calldata_get_string(IntPtr data, byte[] name, out IntPtr str);

  // libobs's messages arrive as a printf format and its va_list; the C runtime OBS itself uses formats them.
  [DllImport("ucrtbase.dll", CallingConvention = C)] static extern int __stdio_common_vsprintf(ulong options, byte[] buffer, UIntPtr count, IntPtr format, IntPtr locale, IntPtr args);

  // A string for libobs: UTF-8 with its terminating zero.
  public static byte[] U(string s) { return s == null ? null : Encoding.UTF8.GetBytes(s + "\0"); }

  // A string from libobs (UTF-8, zero-terminated), or null.
  public static string S(IntPtr p) {
    if (p == IntPtr.Zero) return null;
    int n = 0;
    while (Marshal.ReadByte(p, n) != 0) n++;
    var b = new byte[n];
    Marshal.Copy(p, b, 0, n);
    return Encoding.UTF8.GetString(b);
  }

  public static string Format(IntPtr format, IntPtr args) {
    var buf = new byte[4096];
    int n = __stdio_common_vsprintf(2 /* standard snprintf behaviour */, buf, (UIntPtr)buf.Length, format, IntPtr.Zero, args);
    if (n < 0) return S(format);
    return Encoding.UTF8.GetString(buf, 0, Math.Min(n, buf.Length - 1));
  }

  public static long CallInt(IntPtr calldata, string name) { long v; return calldata_get_data(calldata, U(name), out v, (UIntPtr)8) ? v : 0; }
  public static string CallString(IntPtr calldata, string name) { IntPtr p; return calldata_get_string(calldata, U(name), out p) ? S(p) : null; }

  // obs_data for one source, encoder or output: set the values, hand it to libobs, release it.
  public sealed class Data : IDisposable {
    public readonly IntPtr Ptr = obs_data_create();
    public Data Set(string k, string v) { obs_data_set_string(Ptr, U(k), U(v)); return this; }
    public Data Set(string k, long v) { obs_data_set_int(Ptr, U(k), v); return this; }
    public Data Set(string k, bool v) { obs_data_set_bool(Ptr, U(k), v); return this; }
    public void Dispose() { obs_data_release(Ptr); }
  }
}

// The engine: started once, as soon as the Recorder opens, on a thread of its own (a single-threaded COM apartment, as libobs wants), and
// shut down on that thread when the Recorder closes. Starting it loads the plugins, and NVENC, AMF and Quick Sync each open a test session.
// On this PC new GPU encoder sessions next to a game that holds the GPU at 99% have reset the driver, and the start took 60 s instead of
// 1.5 (Oct 18), so it happens when the Recorder opens (usually before the game), never on the window's thread, and not at the first Record.
static class Obs {
  // Paths go to libobs with forward slashes: its shader files #include each other, and it finds their folder by the last '/' only
  // (cf-lexer.c), so with backslashes every shader fails to load and the video can't start (silently: obs_reset_video returns -1).
  public static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
  public static string PluginBin { get { return Fwd(Path.Combine(Dir, @"obs\obs-plugins\64bit")); } }
  public static string PluginData { get { return Fwd(Path.Combine(Dir, @"obs\data\obs-plugins\%module%")); } }
  public static string CoreData { get { return Fwd(Path.Combine(Dir, @"obs\data\libobs")) + "/"; } }
  public static string Fwd(string path) { return path.Replace('\\', '/'); }

  public static string ConfigFolder;   // where the plugins keep their files; null = the Recorder's data folder (Settings.EngineConfigFolder). The tests name their own.

  public static bool Started { get; private set; }
  public static string Version { get; private set; }
  public static readonly List<string> Encoders = new List<string>();   // every encoder libobs registered (the hardware ones only when their own test passed)
  public static readonly List<string> StartupLog = new List<string>(); // libobs's messages before the first recording's log existed

  // Where libobs's messages go: the recording's log while there is one. Set by Recording.
  public static Action<string> Sink;
  static readonly ObsApi.LogHandler logHandler = OnLog;   // kept alive: libobs holds the pointer
  static IntPtr graphicsModule;                           // "libobs-d3d11", allocated once and never freed

  // What is missing for the engine to start, or null.
  public static string Missing() {
    foreach (string f in new[] { "obs.dll", "libobs-d3d11.dll", "w32-pthreads.dll" })
      if (!File.Exists(Path.Combine(Dir, f))) return f + " is missing next to the Recorder.";
    foreach (string m in new[] { "win-capture", "win-wasapi", "obs-ffmpeg", "obs-outputs" })
      if (!File.Exists(Path.Combine(PluginBin, m + ".dll"))) return "obs\\obs-plugins\\64bit\\" + m + ".dll is missing.";
    if (!File.Exists(Path.Combine(CoreData, "default.effect"))) return "obs\\data\\libobs is missing.";
    return null;
  }

  static readonly bool debugLog = Environment.GetEnvironmentVariable("BIFROST_OBS_DEBUG") == "1";   // libobs's debug lines too, for troubleshooting

  static void OnLog(int level, IntPtr format, IntPtr args, IntPtr param) {
    if (level > ObsApi.LogInfo && !debugLog) return;
    string text;
    try { text = ObsApi.Format(format, args); } catch (Exception) { return; }
    string line = "obs " + (level <= ObsApi.LogError ? "ERROR " : level <= ObsApi.LogWarning ? "warning " : "") + text;
    var sink = Sink;
    if (sink != null) { try { sink(line); } catch (Exception) { } }
    else lock (StartupLog) { if (StartupLog.Count < 2000) StartupLog.Add(line); }
  }

  // Starts libobs at the given canvas: loads the plugins and lists the encoders. Returns an error to show, or null. Call on the window's thread.
  public static string Start(int baseWidth, int baseHeight, int outWidth, int outHeight) {
    if (Started) return ResetVideo(baseWidth, baseHeight, outWidth, outHeight);
    string missing = Missing();
    if (missing != null) return missing;
    try {
      ObsApi.base_set_log_handler(logHandler, IntPtr.Zero);
      // The plugins' own files go in the Recorder's data folder. Left out, libobs builds their paths from "" and win-capture writes its
      // "win-capture" folder into whatever folder the Recorder was started from. win-capture does fetch its list of programs that need
      // special handling from obsproject.com (meta.json holds the HTTP etag of the last answer); that fetch is OBS's own and isn't blocked.
      string config = ConfigFolder ?? Settings.EngineConfigFolder(Path.Combine(Dir, Settings.FileName), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
      try { Directory.CreateDirectory(config); } catch (Exception) { }
      if (!ObsApi.obs_startup(ObsApi.U("en-US"), ObsApi.U(Fwd(config)), IntPtr.Zero)) return "The recording engine (libobs) didn't start.";
    } catch (DllNotFoundException ex) {
      return "The recording engine couldn't load (" + ex.Message + "). Installing the Microsoft Visual C++ Redistributable (x64) may fix it.";
    } catch (BadImageFormatException ex) { return "The recording engine's files are damaged: " + ex.Message; }
    Version = ObsApi.S(ObsApi.obs_get_version_string());
    var audio = new ObsApi.AudioInfo { SamplesPerSec = 48000, Speakers = ObsApi.SpeakersStereo };
    if (!ObsApi.obs_reset_audio(ref audio)) { ObsApi.obs_shutdown(); return "The recording engine couldn't set up sound."; }
    graphicsModule = Marshal.StringToHGlobalAnsi("libobs-d3d11");
    ObsApi.obs_add_data_path(ObsApi.U(CoreData));   // before the first video reset: that loads libobs's shaders from here
    string err = ResetVideo(baseWidth, baseHeight, outWidth, outHeight);
    if (err != null) { ObsApi.obs_shutdown(); return err; }
    // The SDR white and HDR peak levels OBS Studio sets at its start (300 and 1000 nits). Left unset they are 0, and a window or screen on
    // an HDR monitor, which Windows hands over as 16-bit float, is tone-mapped with 80 / 0 and records black.
    ObsApi.obs_set_video_levels(300f, 1000f);
    ObsApi.obs_add_module_path(ObsApi.U(PluginBin), ObsApi.U(PluginData));
    ObsApi.obs_load_all_modules();
    ObsApi.obs_post_load_modules();
    Encoders.Clear();
    for (uint i = 0; ; i++) {
      IntPtr id;
      if (!ObsApi.obs_enum_encoder_types((UIntPtr)i, out id)) break;
      Encoders.Add(ObsApi.S(id));
    }
    Started = true;
    return null;
  }

  // The canvas (what the sources are drawn on) and the video's size, 60 frames a second, NV12 BT.709 limited range (what every player expects).
  // Only possible while nothing is recording.
  public static string ResetVideo(int baseWidth, int baseHeight, int outWidth, int outHeight) {
    var v = new ObsApi.VideoInfo {
      GraphicsModule = graphicsModule, FpsNum = (uint)Sizes.Fps, FpsDen = 1,
      BaseWidth = (uint)baseWidth, BaseHeight = (uint)baseHeight, OutputWidth = (uint)outWidth, OutputHeight = (uint)outHeight,
      OutputFormat = ObsApi.VideoFormatNV12, Adapter = 0, GpuConversion = true,
      Colorspace = ObsApi.Cs709, Range = ObsApi.RangePartial, ScaleType = ObsApi.ScaleBicubic };
    int r = ObsApi.obs_reset_video(ref v);
    if (r == ObsApi.VideoSuccess) return null;
    return r == -5 ? "The recording engine couldn't load Direct3D 11 (libobs-d3d11)." : r == -4 ? "The recording engine is still busy with the last recording." : "The recording engine couldn't set up the video (code " + r + ").";
  }

  // The first encoder this PC can use, in the Recorder's order: NVIDIA, AMD, Intel, then the software one.
  public static EncoderProfile PickEncoder() {
    foreach (var e in EncoderProfile.Preference) if (Encoders.Contains(e.Name)) return e;
    return EncoderProfile.Software;
  }

  static System.Threading.Thread engineThread;
  static readonly System.Threading.ManualResetEvent quit = new System.Threading.ManualResetEvent(false);

  // Starts the engine on its own thread and calls done with null or the error, from that thread. Once started, the thread waits (a managed
  // wait, which pumps COM on a single-threaded apartment) until Shutdown. Already started (the tests start it themselves): done(null) at once.
  public static void StartInBackground(Action<string> done) {
    if (Started) { done(null); return; }
    if (engineThread != null) return;
    engineThread = new System.Threading.Thread(delegate() {
      string err;
      try { err = Start(1280, 720, 1280, 720); } catch (Exception ex) { err = "The recording engine failed to start: " + ex.Message; }
      done(err);
      if (err != null) return;
      quit.WaitOne();
      Started = false;
      try { ObsApi.obs_shutdown(); } catch (Exception) { }
    });
    engineThread.SetApartmentState(System.Threading.ApartmentState.STA);
    engineThread.IsBackground = true;
    engineThread.Name = "recording engine";
    engineThread.Start();
  }

  // Shuts libobs down on the thread that started it. Nothing may be recording.
  public static void Shutdown() {
    if (engineThread != null) { quit.Set(); engineThread.Join(10000); return; }
    if (!Started) return;
    Started = false;
    try { ObsApi.obs_shutdown(); } catch (Exception) { }
  }
}
