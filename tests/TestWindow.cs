// SPDX-License-Identifier: GPL-2.0-or-later
// The animated window the tests record: the only thing they ever capture. It sits off every monitor, never takes the focus,
// and redraws at 60 fps from its own thread (GDI into the window, the way a plain game window shows frames).
//   Simple  a moving bar, a frame counter and a strip of reference colours (for the colour check)
//   Hard    a detailed foliage-like texture panning at changing speeds under 120 moving shapes: the stand-in for Arma
//   Noise   a full frame of new random pixels every frame: the worst case any encoder can be given
//   Static  drawn once and never again (a window that doesn't redraw)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

enum Scene { Simple, Hard, Noise, Static }

class TestWindow : Form {
  [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
  [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

  // The reference colours along the bottom of the Simple scene, left to right, as drawn.
  public static readonly Color[] Swatches = {
    Color.FromArgb(255, 0, 0), Color.FromArgb(0, 255, 0), Color.FromArgb(0, 0, 255), Color.FromArgb(255, 255, 255),
    Color.FromArgb(128, 128, 128), Color.FromArgb(0, 0, 0), Color.FromArgb(255, 255, 0), Color.FromArgb(0, 255, 255) };
  public const int SwatchWidth = 80, SwatchHeight = 80;

  // Where windows open. By default on the primary monitor (the one on the NVIDIA GPU, the path a game's window takes), at the bottom of the
  // window order, layered at alpha 1/255 (invisible), click-through and never taking the focus. BIFROST_TEST_SCREEN=secondary puts them fully
  // inside the first other monitor instead (the fallback if the invisible bottom window can't be captured). Off every monitor sends the
  // capture nothing (measured).
  public static readonly bool OnSecondary = Environment.GetEnvironmentVariable("BIFROST_TEST_SCREEN") == "secondary" && HasSecondary();
  public static Point Place = OnSecondary ? SecondaryOrigin() : new Point(Screen.PrimaryScreen.Bounds.X + 100, Screen.PrimaryScreen.Bounds.Y + 100);
  public static readonly List<IntPtr> Handles = new List<IntPtr>();   // every test window opened, for the watcher
  public static readonly List<string> Placed = new List<string>();    // where each one ended up, for the log
  static Point SecondaryOrigin() {
    foreach (var s in Screen.AllScreens) if (!s.Primary) return s.Bounds.Location;
    return Screen.PrimaryScreen.Bounds.Location;
  }
  public static bool HasSecondary() { foreach (var s in Screen.AllScreens) if (!s.Primary) return true; return false; }
  public static int LayerAlpha = 255;                       // below 255 the window is layered with this alpha
  const PixelFormat BackFormat = PixelFormat.Format32bppPArgb;   // the format GDI+ puts into a window fastest
  readonly Scene scene;
  readonly int cw, ch;
  readonly object gate = new object();
  readonly ManualResetEvent shown = new ManualResetEvent(false);
  Bitmap back;
  Thread loop;
  volatile bool stopping;
  public long Drawn, Late;               // frames drawn, and frames that started more than 8 ms after they were due
  public long RenderTicks, PresentTicks; // Stopwatch ticks spent making frames and putting them in the window
  public volatile int LastCounter;       // the counter drawn last
  public volatile bool Frozen;           // set: the loop stops redrawing (a game that has stopped producing frames)

  TestWindow(Scene scene, int w, int h, bool border, string title) {
    this.scene = scene; cw = w; ch = h;
    Text = title;
    FormBorderStyle = border ? FormBorderStyle.FixedSingle : FormBorderStyle.None;
    MaximizeBox = false;
    StartPosition = FormStartPosition.Manual;
    Location = Place;
    ClientSize = new Size(w, h);
    var scr = Screen.FromPoint(Place).Bounds;   // the whole window stays inside the monitor it opens on
    Location = new Point(Math.Max(scr.X, Math.Min(Place.X, scr.Right - Width)), Math.Max(scr.Y, Math.Min(Place.Y, scr.Bottom - Height)));
    BackColor = Color.Black;
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
    back = new Bitmap(w, h, BackFormat);
  }

  public int PixelWidth { get { return cw; } }
  public int PixelHeight { get { return ch; } }

  // Opens the window on its own UI thread and returns once it's showing (and, for the animated scenes, drawing).
  public static TestWindow Open(Scene scene, int w, int h, bool border, string title) {
    TestWindow win = null;
    var created = new ManualResetEvent(false);
    var t = new Thread(delegate() {
      win = new TestWindow(scene, w, h, border, title);
      created.Set();
      Application.Run(win);
    });
    t.SetApartmentState(ApartmentState.STA);
    t.IsBackground = true;
    t.Start();
    if (!created.WaitOne(15000)) throw new Exception("the test window didn't open");
    if (!win.shown.WaitOne(15000)) throw new Exception("the test window didn't show");
    Thread.Sleep(300);   // let the first frames reach the compositor
    lock (Handles) Handles.Add(win.Handle);
    Native.RECT r; Native.GetWindowRect(win.Handle, out r);
    string where = "off every monitor";
    foreach (var s in Screen.AllScreens) if (s.Bounds.Contains(new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top))) where = "inside " + s.DeviceName + (s.Primary ? " (primary)" : " (secondary)");
    lock (Placed) Placed.Add(title + ": " + r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + " " + where);
    return win;
  }

  public void CloseWindow() { CloseWindow(null); }

  // step, when given, hears each stage of closing (the window tests time them).
  public void CloseWindow(Action<string> step) {
    stopping = true;
    try { if (loop != null) { bool joined = loop.Join(3000); if (step != null) step(joined ? "frame loop ended" : "frame loop still running after 3 s"); } } catch (Exception) { }
    try { Invoke(new MethodInvoker(Close)); if (step != null) step("window closed"); } catch (Exception ex) { if (step != null) step("close failed: " + ex.Message); }
  }

  protected override bool ShowWithoutActivation { get { return true; } }

  protected override CreateParams CreateParams {
    get {
      var cp = base.CreateParams;
      cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE: never takes the focus
      if (LayerAlpha < 255) cp.ExStyle |= 0x80000 | 0x20;   // WS_EX_LAYERED and WS_EX_TRANSPARENT: nearly invisible, and clicks go through
      return cp;
    }
  }

  protected override void OnShown(EventArgs e) {
    base.OnShown(e);
    if (scene == Scene.Static) { Render(0); Present(); }
    else { loop = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "test window frames" }; loop.Start(); }
    if (LayerAlpha < 255) SetLayeredWindowAttributes(Handle, 0, (byte)LayerAlpha, 2);
    SetWindowPos(Handle, (IntPtr)1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // HWND_BOTTOM; no size, no move, no activate: never topmost
    shown.Set();
  }

  protected override void OnPaint(PaintEventArgs e) { lock (gate) e.Graphics.DrawImageUnscaled(back, 0, 0); }

  protected override void OnFormClosed(FormClosedEventArgs e) { stopping = true; base.OnFormClosed(e); }

  // (Putting frames in the window with SetDIBitsToDevice was tried: it was fast, but the capture then got about 10 frames a second
  // from this near-invisible layered window. GDI+ drawing the bitmap into the window's DC reaches the compositor every frame.)
  void Present() {
    if (!IsHandleCreated) return;
    try {
      using (var g = Graphics.FromHwnd(Handle)) lock (gate) g.DrawImageUnscaled(back, 0, 0);
    } catch (Exception) { }
  }

  void Loop() {
    timeBeginPeriod(1);
    try {
      var sw = Stopwatch.StartNew();
      const double period = 1000.0 / 60.0;
      long n = 0;
      while (!stopping) {
        double due = n * period, now = sw.Elapsed.TotalMilliseconds;
        if (now - due > 100) { n = (long)(now / period); due = n * period; }   // fell far behind (a stall): carry on from now
        while ((now = sw.Elapsed.TotalMilliseconds) < due - 1.5) Thread.Sleep(1);
        while (sw.Elapsed.TotalMilliseconds < due) Thread.SpinWait(40);
        if (sw.Elapsed.TotalMilliseconds - due > 8) Interlocked.Increment(ref Late);
        if (!Frozen) {
          long t0 = Stopwatch.GetTimestamp();
          Render(n);
          long t1 = Stopwatch.GetTimestamp();
          Present();
          long t2 = Stopwatch.GetTimestamp();
          Interlocked.Add(ref RenderTicks, t1 - t0); Interlocked.Add(ref PresentTicks, t2 - t1);
          Interlocked.Increment(ref Drawn);
        }
        n++;
      }
    } finally { timeEndPeriod(1); }
  }

  // ------------------------------------------------------------------------------------------------ scenes
  Bitmap texture;          // Hard: the panning texture, as 32-bit pixels
  int[] texPixels, framePixels;
  int texW, texH;
  uint rnd = 2463534242;
  double[] sx, sy, vx, vy; int[] sr;

  void Render(long n) {
    lock (gate) {
      if (scene == Scene.Noise) RenderNoise();
      else if (scene == Scene.Hard) RenderHard(n);
      else RenderSimple(n);
    }
  }

  void RenderSimple(long n) {
    using (var g = Graphics.FromImage(back)) {
      g.SmoothingMode = SmoothingMode.None;
      using (var b = new LinearGradientBrush(new Rectangle(0, 0, cw, ch), Color.FromArgb(24, 32, 48), Color.FromArgb(70, 50, 90), 90f)) g.FillRectangle(b, 0, 0, cw, ch);
      int barW = Math.Max(16, cw / 16);
      int x = (int)((n * 12) % (cw + barW)) - barW;      // crosses the window about every 2 seconds at 720 wide-ish
      using (var b = new SolidBrush(Color.FromArgb(240, 240, 120))) g.FillRectangle(b, x, ch / 5, barW, ch / 2);
      using (var b = new SolidBrush(Color.FromArgb(230, 90, 60))) g.FillRectangle(b, cw - x - barW, ch / 5 + ch / 2 + 10, barW, 40);
      using (var f = new Font("Consolas", Math.Max(20, ch / 12), FontStyle.Bold, GraphicsUnit.Pixel)) {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        g.DrawString("FRAME " + n.ToString("000000"), f, Brushes.White, 12, 8);
      }
      int sw = SwatchWidth, sh = SwatchHeight;
      for (int i = 0; i < Swatches.Length; i++) using (var b = new SolidBrush(Swatches[i])) g.FillRectangle(b, 20 + i * sw, ch - sh - 20, sw, sh);
    }
    LastCounter = (int)n;
  }

  void EnsureHard() {
    if (texture != null) return;
    texW = Math.Max(2560, cw); texH = Math.Max(1440, ch);
    texPixels = new int[texW * texH]; framePixels = new int[cw * ch];
    var rng = new Random(7);
    int[] cells = { 160, 48, 14, 4 };
    double[] amp = { 0.45, 0.30, 0.17, 0.08 };
    var field = new double[texW * texH];
    for (int o = 0; o < cells.Length; o++) {
      int c = cells[o], gw = texW / c + 2, gh = texH / c + 2;
      var grid = new double[gw * gh];
      for (int i = 0; i < grid.Length; i++) grid[i] = rng.NextDouble();
      for (int y = 0; y < texH; y++) {
        int gy = y / c; double fy = (y % c) / (double)c;
        for (int x = 0; x < texW; x++) {
          int gx = x / c; double fx = (x % c) / (double)c;
          double a = grid[gy * gw + gx], b = grid[gy * gw + gx + 1], cc = grid[(gy + 1) * gw + gx], d = grid[(gy + 1) * gw + gx + 1];
          field[y * texW + x] += amp[o] * (a + (b - a) * fx + (cc - a) * fy + (a - b - cc + d) * fx * fy);
        }
      }
    }
    for (int i = 0; i < field.Length; i++) {
      double v = Math.Max(0, Math.Min(1, field[i]));
      int r = (int)(20 + 90 * v), g = (int)(40 + 150 * v), bl = (int)(10 + 50 * v * v);
      texPixels[i] = (255 << 24) | (r << 16) | (g << 8) | bl;
    }
    texture = new Bitmap(1, 1);
    sx = new double[120]; sy = new double[120]; vx = new double[120]; vy = new double[120]; sr = new int[120];
    for (int i = 0; i < 120; i++) { sx[i] = rng.Next(cw); sy[i] = rng.Next(ch); vx[i] = rng.NextDouble() * 10 - 5; vy[i] = rng.NextDouble() * 10 - 5; sr[i] = 6 + rng.Next(26); }
  }

  void RenderHard(long n) {
    EnsureHard();
    // the camera pans: 3 to 13 px a frame sideways, changing over a few seconds, and a slower drift up and down
    double t = n / 60.0;
    int ox = (int)(n * 8 + 300 * Math.Sin(t * 0.7)) % texW; if (ox < 0) ox += texW;
    int oy = (int)(n * 3 + 140 * Math.Sin(t * 0.4)) % texH; if (oy < 0) oy += texH;
    for (int y = 0; y < ch; y++) {
      int sy0 = (y + oy) % texH, row = sy0 * texW, dst = y * cw;
      int first = Math.Min(cw, texW - ox);
      Array.Copy(texPixels, row + ox, framePixels, dst, first);
      if (first < cw) Array.Copy(texPixels, row, framePixels, dst + first, cw - first);
    }
    var bd = back.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.WriteOnly, BackFormat);
    try { Marshal.Copy(framePixels, 0, bd.Scan0, framePixels.Length); } finally { back.UnlockBits(bd); }
    using (var g = Graphics.FromImage(back)) {
      g.SmoothingMode = SmoothingMode.None;
      for (int i = 0; i < sx.Length; i++) {
        sx[i] += vx[i]; sy[i] += vy[i];
        if (sx[i] < 0 || sx[i] > cw) vx[i] = -vx[i];
        if (sy[i] < 0 || sy[i] > ch) vy[i] = -vy[i];
        using (var b = new SolidBrush(Color.FromArgb(40 + (i * 37) % 200, 60 + (i * 91) % 190, 30 + (i * 53) % 150))) g.FillEllipse(b, (float)sx[i], (float)sy[i], sr[i], sr[i]);
      }
      using (var f = new Font("Consolas", 40, FontStyle.Bold, GraphicsUnit.Pixel)) {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
        g.DrawString("FRAME " + n.ToString("000000"), f, Brushes.White, 12, 8);
      }
    }
    LastCounter = (int)n;
  }

  void RenderNoise() {
    if (framePixels == null) framePixels = new int[cw * ch];
    uint s = rnd;
    for (int i = 0; i < framePixels.Length; i++) {
      s ^= s << 13; s ^= s >> 17; s ^= s << 5;
      framePixels[i] = (int)(s | 0xFF000000u);
    }
    rnd = s;
    var bd = back.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.WriteOnly, BackFormat);
    try { Marshal.Copy(framePixels, 0, bd.Scan0, framePixels.Length); } finally { back.UnlockBits(bd); }
    LastCounter++;
  }
}
