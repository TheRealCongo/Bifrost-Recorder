// SPDX-License-Identifier: GPL-2.0-or-later
// FakeGame: a stand-in for a DirectX 12 game, for testing the Recorder against what a real game does that a plain window does not:
// a borderless fullscreen window on one monitor with a Direct3D 11 device and a flip-model DXGI swap chain (FLIP_DISCARD, 2 or 3
// buffers). Windows then presents it "independent flip" (straight to the display, not composed by DWM), so Windows Graphics Capture
// gets almost no frames from it, exactly as with a focused fullscreen game. "windowed" mode (not covering the monitor) is composed by
// DWM, for comparison.
//
// Silent by design (the tests run while someone may be using the PC): the window is layered at alpha 1/255 and click-through, never
// activated or given the focus (no key is sent to get it), at the bottom of the window order, never topmost, and its taskbar button is
// removed with ITaskbarList.DeleteTab. The window is still an ordinary visible top-level window, so OBS's hook and the Recorder's list see it.
// When Windows reports the window as occluded, Present returns at once; with vsync on the loop then paces itself to the target rate.
//
// Raw COM interop, no NuGet, C# 5. Content: a moving gradient and bar, a big frame counter (7 segments, drawn by the shader), eight
// colour swatches along the bottom, and an optional full-white flash on a schedule. The pixel shader's loop count is the GPU-load
// knob; --load auto adjusts it each second until the unlocked frame rate is the target (a GPU-bound 60 fps, like a heavy game).
//
//   FakeGame.exe [--mode fullscreen|windowed] [--monitor primary|secondary|\\.\DISPLAYn] [--size WxH]   (size: windowed mode only)
//                [--vsync 1|0] [--buffers 2|3] [--load N|auto] [--target-fps 60] [--seconds S] [--quit-event NAME]
//                [--flash-every S] [--flash-ms 120] [--log FILE]
// Stdout (and --log): READY hwnd=.. mode=.. size=.. foreground=.. exstyle=0x.. alpha=.., then one line a second: FPS <presented> LOAD <loops> FG <0|1> OCC <n>, and
// FLASH <seconds> qpc=<ticks> at each flash (the hook for the audio builder: static FakeGame.FlashHook is called at the same moment).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class FakeGame {
  // ---- hook for later: called on the thread that renders, at the first presented frame of each flash, with the seconds since start
  public static Action<double> FlashHook;

  // ---------------------------------------------------------------------------------------------- Win32
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
  [DllImport("user32.dll", EntryPoint = "GetLayeredWindowAttributes")] static extern bool GetLayeredWindowAttributesNative(IntPtr h, out uint key, out byte alpha, out uint flags);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int index);
  [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
  [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);

  // The taskbar's own list of windows: DeleteTab takes this window's button away without changing the window (it stays an ordinary,
  // visible top-level window, which game capture and the Recorder's list need).
  [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface ITaskbarList {
    [PreserveSig] int HrInit();
    [PreserveSig] int AddTab(IntPtr h);
    [PreserveSig] int DeleteTab(IntPtr h);
    [PreserveSig] int ActivateTab(IntPtr h);
    [PreserveSig] int SetActiveAlt(IntPtr h);
  }
  [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")] class TaskbarListCom { }

  // ---------------------------------------------------------------------------------------------- D3D11 / DXGI (raw COM)
  [DllImport("d3d11.dll")]
  static extern int D3D11CreateDeviceAndSwapChain(IntPtr adapter, uint driverType, IntPtr software, uint flags, IntPtr featureLevels, uint numLevels,
    uint sdkVersion, ref SwapChainDesc desc, out IntPtr swapChain, out IntPtr device, out uint featureLevel, out IntPtr context);
  [DllImport("d3dcompiler_47.dll")]
  static extern int D3DCompile(byte[] src, UIntPtr len, string name, IntPtr defines, IntPtr include, string entry, string target, uint f1, uint f2, out IntPtr code, out IntPtr errors);

  [StructLayout(LayoutKind.Sequential)] struct Rational { public uint Num, Den; }
  [StructLayout(LayoutKind.Sequential)] struct ModeDesc { public uint Width, Height; public Rational Refresh; public uint Format, Scanline, Scaling; }
  [StructLayout(LayoutKind.Sequential)] struct SampleDesc { public uint Count, Quality; }
  [StructLayout(LayoutKind.Sequential)]
  struct SwapChainDesc { public ModeDesc Buffer; public SampleDesc Sample; public uint Usage, BufferCount; public IntPtr Window; public int Windowed; public uint SwapEffect, Flags; }
  [StructLayout(LayoutKind.Sequential)] struct BufferDesc { public uint ByteWidth, Usage, BindFlags, CpuAccess, Misc, Stride; }
  [StructLayout(LayoutKind.Sequential)] struct Viewport { public float X, Y, W, H, MinZ, MaxZ; }
  [StructLayout(LayoutKind.Sequential)] struct Consts { public float Time, Frame, Loops, Flash, W, H, P0, P1; public uint FrameU, P2, P3, P4; }   // 48 bytes

  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr BlobPtrFn(IntPtr self);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate UIntPtr BlobSizeFn(IntPtr self);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBufferFn(IntPtr self, ref BufferDesc desc, IntPtr init, out IntPtr buffer);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateRtvFn(IntPtr self, IntPtr resource, IntPtr desc, out IntPtr rtv);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateShaderFn(IntPtr self, IntPtr bytecode, UIntPtr len, IntPtr linkage, out IntPtr shader);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetShaderFn(IntPtr self, IntPtr shader, IntPtr instances, uint count);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void SetCBFn(IntPtr self, uint start, uint num, ref IntPtr buffers);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void DrawFn(IntPtr self, uint count, uint start);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void TopologyFn(IntPtr self, uint topology);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void OmSetRtFn(IntPtr self, uint num, ref IntPtr rtvs, IntPtr dsv);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void RsViewportsFn(IntPtr self, uint num, ref Viewport vp);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UpdateSubFn(IntPtr self, IntPtr resource, uint sub, IntPtr box, IntPtr data, uint rowPitch, uint depthPitch);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void ClearRtvFn(IntPtr self, IntPtr rtv, float[] color);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PresentFn(IntPtr self, uint sync, uint flags);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBufferFn(IntPtr self, uint index, ref Guid iid, out IntPtr surface);

  static T Slot<T>(IntPtr obj, int index) where T : class {
    return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size), typeof(T));
  }
  static void Release(IntPtr obj) { if (obj != IntPtr.Zero) Slot<ReleaseFn>(obj, 2)(obj); }
  static void Check(int hr, string what) { if (hr < 0) throw new Exception(what + " failed: 0x" + ((uint)hr).ToString("X8")); }

  const string Hlsl = @"
cbuffer C : register(b0) { float4 p0; float4 p1; uint4 p2; };   // p0: time, frame, loops, flash   p1: width, height   p2.x: frame number

float4 vs(uint id : SV_VertexID) : SV_Position {
  float2 uv = float2((id << 1) & 2, id & 2);
  return float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
}

static const uint SEGS[10] = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };   // a b c d e f g -> bits 0..6

bool inb(float2 p, float2 a, float2 b) { return p.x >= a.x && p.x <= b.x && p.y >= a.y && p.y <= b.y; }

// one 7-segment digit in a 0.6 x 1.0 cell
bool digit(float2 p, uint d) {
  uint m = SEGS[d];
  float t = 0.13;
  if ((m & 1)  && inb(p, float2(0.10, 0.00), float2(0.50, t))) return true;            // a top
  if ((m & 2)  && inb(p, float2(0.50 - t, 0.04), float2(0.60 - 0.02, 0.50 - t * 0.5))) return true;  // b top right
  if ((m & 4)  && inb(p, float2(0.50 - t, 0.50 + t * 0.5), float2(0.60 - 0.02, 0.96))) return true;  // c bottom right
  if ((m & 8)  && inb(p, float2(0.10, 1.00 - t), float2(0.50, 1.00))) return true;       // d bottom
  if ((m & 16) && inb(p, float2(0.02, 0.50 + t * 0.5), float2(0.02 + t, 0.96))) return true;         // e bottom left
  if ((m & 32) && inb(p, float2(0.02, 0.04), float2(0.02 + t, 0.50 - t * 0.5))) return true;          // f top left
  if ((m & 64) && inb(p, float2(0.10, 0.50 - t * 0.5), float2(0.50, 0.50 + t * 0.5))) return true;   // g middle
  return false;
}

float4 ps(float4 pos : SV_Position) : SV_Target {
  float2 res = p1.xy;
  float2 uv = pos.xy / res;
  float t = p0.x;
  float3 col = float3(0.08, 0.12, 0.30) + 0.35 * float3(uv.x, uv.y * 0.7, 0.5 + 0.5 * sin(t * 1.3));

  // a moving bar and a second one going the other way
  float bx = frac(t * 0.35);
  if (abs(uv.x - bx) < 0.025 && uv.y > 0.20 && uv.y < 0.62) col = float3(0.95, 0.95, 0.35);
  if (abs(uv.x - (1.0 - bx)) < 0.012 && uv.y > 0.66 && uv.y < 0.80) col = float3(0.90, 0.35, 0.25);

  // eight colour swatches along the bottom left
  if (uv.y > 0.90 && uv.y < 0.98 && uv.x > 0.02 && uv.x < 0.34) {
    uint k = (uint)((uv.x - 0.02) / 0.04);
    float3 sw[8] = { float3(1,0,0), float3(0,1,0), float3(0,0,1), float3(1,1,1), float3(0.5,0.5,0.5), float3(0,0,0), float3(1,1,0), float3(0,1,1) };
    col = sw[min(k, 7u)];
  }

  // the frame counter: 8 digits, top left, 11% of the height tall
  float ch = res.y * 0.11, cw = ch * 0.6, gap = cw * 0.35;
  float2 o = float2(res.x * 0.02, res.y * 0.03);
  if (pos.y >= o.y && pos.y <= o.y + ch && pos.x >= o.x && pos.x <= o.x + 8 * (cw + gap)) {
    float cell = (pos.x - o.x) / (cw + gap);
    uint idx = (uint)cell;
    float lx = (cell - idx) * (cw + gap);
    if (lx <= cw) {
      uint n = p2.x;
      for (uint q = 0; q < 7 - idx; q++) n /= 10;
      uint d = n % 10;
      if (digit(float2(lx / cw * 0.6, (pos.y - o.y) / ch), d)) col = float3(1, 1, 1);
      else col = col * 0.35;
    }
  }

  // the GPU load: a per-pixel loop whose length is the knob (the result is mixed in so the compiler keeps it)
  uint loops = (uint)p0.z;
  float acc = 0;
  [loop] for (uint i = 0; i < loops; i++) acc += sin(pos.x * 0.011 + i * 0.37 + t) * cos(pos.y * 0.013 - i * 0.11);
  col += acc * 1e-7;

  if (p0.w > 0.5) col = float3(1, 1, 1);
  return float4(col, 1);
}
";

  // ---------------------------------------------------------------------------------------------- the program
  static StreamWriter logw;
  static readonly object logGate = new object();

  static void Log(string line) {
    try { Console.WriteLine(line); Console.Out.Flush(); } catch (Exception) { }
    lock (logGate) { if (logw != null) { try { logw.WriteLine(line); } catch (Exception) { } } }
  }

  static string Arg(string[] a, string name, string def) {
    for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
    return def;
  }
  static bool Has(string[] a, string name) { foreach (string s in a) if (s == name) return true; return false; }

  class GameForm : Form {
    public GameForm() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true); }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
      get {
        var cp = base.CreateParams;
        cp.ExStyle |= 0x08000000 | 0x80000 | 0x20;   // WS_EX_NOACTIVATE (never takes the focus), WS_EX_LAYERED, WS_EX_TRANSPARENT (clicks go through)
        return cp;
      }
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }
    public volatile bool Quit;
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Quit = true; base.OnKeyDown(e); }
    protected override void OnFormClosing(FormClosingEventArgs e) { Quit = true; base.OnFormClosing(e); }
  }

  [STAThread]
  static int Main(string[] args) {
    try { SetProcessDPIAware(); } catch (Exception) { }
    Application.EnableVisualStyles();
    string mode = Arg(args, "--mode", "fullscreen");
    string monitor = Arg(args, "--monitor", "primary");
    int vsync = int.Parse(Arg(args, "--vsync", "1"), CultureInfo.InvariantCulture);
    int buffers = int.Parse(Arg(args, "--buffers", "3"), CultureInfo.InvariantCulture);
    string loadArg = Arg(args, "--load", "auto");
    double targetFps = double.Parse(Arg(args, "--target-fps", "60"), CultureInfo.InvariantCulture);
    double seconds = double.Parse(Arg(args, "--seconds", "0"), CultureInfo.InvariantCulture);
    string quitName = Arg(args, "--quit-event", null);
    double flashEvery = double.Parse(Arg(args, "--flash-every", "0"), CultureInfo.InvariantCulture);
    double flashMs = double.Parse(Arg(args, "--flash-ms", "120"), CultureInfo.InvariantCulture);
    string logFile = Arg(args, "--log", null);
    if (logFile != null) { try { logw = new StreamWriter(new FileStream(logFile, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true }; } catch (Exception) { } }

    Screen screen = Screen.PrimaryScreen;
    if (monitor == "secondary") { foreach (var s in Screen.AllScreens) if (!s.Primary) { screen = s; break; } }
    else if (monitor.StartsWith("\\\\.\\")) { foreach (var s in Screen.AllScreens) if (string.Equals(s.DeviceName, monitor, StringComparison.OrdinalIgnoreCase)) screen = s; }
    bool fullscreen = mode != "windowed";
    Size size = screen.Bounds.Size;
    if (!fullscreen) {
      var parts = Arg(args, "--size", "1600x900").Split('x');
      size = new Size(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    EventWaitHandle quitEvent = null;
    if (quitName != null) { try { quitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, quitName); } catch (Exception ex) { Log("quit event failed: " + ex.Message); } }

    var form = new GameForm();
    form.Text = "FakeGame (" + (fullscreen ? "fullscreen" : "windowed") + ")";
    form.FormBorderStyle = FormBorderStyle.None;
    form.StartPosition = FormStartPosition.Manual;
    form.BackColor = Color.Black;
    form.TopMost = false;
    form.Bounds = fullscreen ? screen.Bounds : new Rectangle(screen.Bounds.X + 100, screen.Bounds.Y + 100, size.Width, size.Height);
    IntPtr hwnd = form.Handle;
    SetLayeredWindowAttributes(hwnd, 0, 1, 2);                                   // LWA_ALPHA at 1/255 before the window is first shown: invisible
    SetWindowPos(hwnd, (IntPtr)1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);        // HWND_BOTTOM, no move, no size, no activate
    form.Show();
    SetWindowPos(hwnd, (IntPtr)1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    try { var tb = (ITaskbarList)new TaskbarListCom(); tb.HrInit(); tb.DeleteTab(hwnd); } catch (Exception ex) { Log("taskbar button not removed: " + ex.Message); }
    Application.DoEvents();
    SetThreadExecutionState(0x80000000 | 0x00000002 | 0x00000001);   // ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED
    timeBeginPeriod(1);

    // the Direct3D 11 device with a flip-model swap chain
    IntPtr sc = IntPtr.Zero, dev = IntPtr.Zero, ctx = IntPtr.Zero, rtv = IntPtr.Zero, tex = IntPtr.Zero, cb = IntPtr.Zero, vsh = IntPtr.Zero, psh = IntPtr.Zero;
    try {
      var desc = new SwapChainDesc {
        Buffer = new ModeDesc { Width = (uint)size.Width, Height = (uint)size.Height, Refresh = new Rational { Num = 0, Den = 1 }, Format = 87 },
        Sample = new SampleDesc { Count = 1, Quality = 0 }, Usage = 0x20, BufferCount = (uint)buffers, Window = form.Handle, Windowed = 1, SwapEffect = 4,   // FLIP_DISCARD
        Flags = vsync == 0 ? 0x800u : 0u };                                                                                                      // ALLOW_TEARING when unlocked
      uint fl;
      int hr = D3D11CreateDeviceAndSwapChain(IntPtr.Zero, 1, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, ref desc, out sc, out dev, out fl, out ctx);
      bool tearing = vsync == 0;
      if (hr < 0 && tearing) { desc.Flags = 0; tearing = false; hr = D3D11CreateDeviceAndSwapChain(IntPtr.Zero, 1, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, ref desc, out sc, out dev, out fl, out ctx); Log("tearing flag refused, running without it"); }
      Check(hr, "D3D11CreateDeviceAndSwapChain");

      Guid iidTex = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");   // ID3D11Texture2D
      Check(Slot<GetBufferFn>(sc, 9)(sc, 0, ref iidTex, out tex), "GetBuffer");
      Check(Slot<CreateRtvFn>(dev, 9)(dev, tex, IntPtr.Zero, out rtv), "CreateRenderTargetView");

      byte[] src = Encoding.ASCII.GetBytes(Hlsl);
      IntPtr vsBlob = Compile(src, "vs", "vs_5_0"), psBlob = Compile(src, "ps", "ps_5_0");
      Check(Slot<CreateShaderFn>(dev, 12)(dev, Slot<BlobPtrFn>(vsBlob, 3)(vsBlob), Slot<BlobSizeFn>(vsBlob, 4)(vsBlob), IntPtr.Zero, out vsh), "CreateVertexShader");
      Check(Slot<CreateShaderFn>(dev, 15)(dev, Slot<BlobPtrFn>(psBlob, 3)(psBlob), Slot<BlobSizeFn>(psBlob, 4)(psBlob), IntPtr.Zero, out psh), "CreatePixelShader");
      Release(vsBlob); Release(psBlob);
      var bd = new BufferDesc { ByteWidth = 48, Usage = 0, BindFlags = 4, CpuAccess = 0, Misc = 0, Stride = 0 };   // DEFAULT, CONSTANT_BUFFER
      Check(Slot<CreateBufferFn>(dev, 3)(dev, ref bd, IntPtr.Zero, out cb), "CreateBuffer");

      var vp = new Viewport { X = 0, Y = 0, W = size.Width, H = size.Height, MinZ = 0, MaxZ = 1 };
      var pBuf = Marshal.AllocHGlobal(48);
      var setVs = Slot<SetShaderFn>(ctx, 11); var setPs = Slot<SetShaderFn>(ctx, 9); var setCb = Slot<SetCBFn>(ctx, 16); var draw = Slot<DrawFn>(ctx, 13);
      var topo = Slot<TopologyFn>(ctx, 24); var omRt = Slot<OmSetRtFn>(ctx, 33); var rsVp = Slot<RsViewportsFn>(ctx, 44); var upd = Slot<UpdateSubFn>(ctx, 48);
      var present = Slot<PresentFn>(sc, 8);

      // the window is never given the foreground; the line says what it has (the tests check it stays that way)
      bool fg = GetForegroundWindow() == form.Handle;
      byte alpha = 255; uint ck, lf; GetLayeredWindowAttributesNative(hwnd, out ck, out alpha, out lf);
      Log("READY hwnd=" + form.Handle.ToInt64() + " mode=" + (fullscreen ? "fullscreen" : "windowed") + " monitor=" + screen.DeviceName + " size=" + size.Width + "x" + size.Height +
          " vsync=" + vsync + " buffers=" + buffers + " tearing=" + tearing + " foreground=" + fg + " exstyle=0x" + GetWindowLong(hwnd, -20).ToString("X") + " alpha=" + alpha + " pid=" + Process.GetCurrentProcess().Id);

      double loops = loadArg == "auto" ? 200 : double.Parse(loadArg, CultureInfo.InvariantCulture);
      bool auto = loadArg == "auto";
      var clock = Stopwatch.StartNew();
      long frame = 0, lastCount = 0, occluded = 0; double lastSec = 0, nextFrameAt = 0;
      bool flashing = false; double lastFlashStart = -1;
      while (!form.Quit) {
        Application.DoEvents();
        double now = clock.Elapsed.TotalSeconds;
        if (seconds > 0 && now >= seconds) break;
        if (quitEvent != null && quitEvent.WaitOne(0)) break;

        // a flash on a schedule: the first presented frame of each is the flash instant
        bool flash = false;
        if (flashEvery > 0) { double ph = now % flashEvery; flash = now >= flashEvery * 0.5 && ph < flashMs / 1000.0; }
        if (flash && !flashing) {
          flashing = true;
          Log("FLASH " + now.ToString("0.000", CultureInfo.InvariantCulture) + " qpc=" + Stopwatch.GetTimestamp());
          var hook = FlashHook; if (hook != null) hook(now);
        } else if (!flash) flashing = false;

        var c = new Consts { Time = (float)now, Frame = frame, Loops = (float)loops, Flash = flash ? 1f : 0f, W = size.Width, H = size.Height, FrameU = (uint)frame };
        Marshal.StructureToPtr(c, pBuf, false);
        upd(ctx, cb, 0, IntPtr.Zero, pBuf, 0, 0);
        omRt(ctx, 1, ref rtv, IntPtr.Zero);
        rsVp(ctx, 1, ref vp);
        topo(ctx, 4);
        setVs(ctx, vsh, IntPtr.Zero, 0);
        setPs(ctx, psh, IntPtr.Zero, 0);
        setCb(ctx, 0, 1, ref cb);
        draw(ctx, 3, 0);
        int phr = present(sc, vsync == 0 ? 0u : 1u, vsync == 0 && tearing ? 0x200u : 0u);   // ALLOW_TEARING
        if (phr < 0 && (uint)phr != 0x087A0001) { Log("Present failed 0x" + ((uint)phr).ToString("X8")); break; }   // DXGI_STATUS_OCCLUDED is fine
        if ((uint)phr == 0x087A0001) {   // occluded: Present came back at once. With vsync on there is nothing to wait for, so keep the target rate
          occluded++;
          if (vsync != 0) {
            double period = 1.0 / targetFps; nextFrameAt = Math.Max(nextFrameAt + period, clock.Elapsed.TotalSeconds - period);
            double wait = nextFrameAt - clock.Elapsed.TotalSeconds;
            if (wait > 0.0015) Thread.Sleep((int)(wait * 1000 - 1));
          }
        } else nextFrameAt = clock.Elapsed.TotalSeconds;
        frame++;

        if (now - lastSec >= 1.0) {
          double fps = (frame - lastCount) / (now - lastSec);
          bool inFg = GetForegroundWindow() == form.Handle;
          Log("FPS " + fps.ToString("0.0", CultureInfo.InvariantCulture) + " LOAD " + ((int)loops).ToString(CultureInfo.InvariantCulture) + " FG " + (inFg ? 1 : 0) + " OCC " + occluded);
          if (auto) {   // a GPU-bound frame rate at the target: more work when faster, less when slower
            if (fps > targetFps * 1.03) loops = Math.Min(400000, loops * (fps > targetFps * 1.5 ? 1.5 : 1.12));
            else if (fps < targetFps * 0.97) loops = Math.Max(0, loops / (fps < targetFps * 0.6 ? 1.5 : 1.12));
          }
          lastCount = frame; lastSec = now;
        }
      }
      Log("EXIT frames=" + frame + " seconds=" + clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture));
      Marshal.FreeHGlobal(pBuf);
      return 0;
    } catch (Exception ex) {
      Log("ERROR " + ex.Message);
      return 2;
    } finally {
      try { Release(cb); Release(psh); Release(vsh); Release(rtv); Release(tex); Release(ctx); Release(dev); Release(sc); } catch (Exception) { }
      SetThreadExecutionState(0x80000000);
      try { form.Close(); } catch (Exception) { }
    }
  }

  static IntPtr Compile(byte[] src, string entry, string target) {
    IntPtr code, errors;
    int hr = D3DCompile(src, (UIntPtr)src.Length, "fakegame.hlsl", IntPtr.Zero, IntPtr.Zero, entry, target, 0, 0, out code, out errors);
    if (hr < 0) {
      string msg = "";
      if (errors != IntPtr.Zero) msg = Marshal.PtrToStringAnsi(Slot<BlobPtrFn>(errors, 3)(errors), (int)(ulong)Slot<BlobSizeFn>(errors, 4)(errors));
      throw new Exception("shader " + entry + " did not compile: " + msg);
    }
    if (errors != IntPtr.Zero) Release(errors);
    return code;
  }
}
