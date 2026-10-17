// Windows calls the Recorder needs: the list of windows that can be recorded, and the Job Object that keeps every child
// process (ffmpeg, ffprobe) from outliving the Recorder, even after a crash. C# 5 for Windows' own compiler.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

// One window the list can offer.
class WindowInfo {
  public IntPtr Handle;
  public string Title, ExePath, ExeName, ClassName;
  public uint ProcessId;
  public bool Minimized;
  public int Width, Height;     // the visible frame, for the list and for the bitrate cap
  public override string ToString() { return Title + " [" + ExeName + "]"; }
}

static class Native {
  delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lparam);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
  [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr region, uint flags);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
  [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
  [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr p, uint flags, StringBuilder sb, ref uint size);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

  const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
  const uint GW_OWNER = 4, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, RDW_INVALIDATE = 1, RDW_NOERASE = 0x20;

  // Which windows are offered: what Alt+Tab would list. Pure, so the tests can feed it every case.
  public static bool Capturable(bool visible, string title, bool cloaked, bool toolWindow, bool appWindow, bool owned, uint pid, uint ownPid, string className) {
    if (!visible || cloaked || pid == ownPid) return false;
    if (string.IsNullOrWhiteSpace(title)) return false;
    if (toolWindow && !appWindow) return false;
    if (owned && !appWindow) return false;
    if (className == "Progman" || className == "WorkerW") return false;   // the desktop itself
    return true;
  }

  // Windows of this process are never offered. The tests, whose own windows are what they record, set this to 0.
  public static uint ExcludePid = (uint)Process.GetCurrentProcess().Id;

  public static List<WindowInfo> ListWindows() {
    var list = new List<WindowInfo>();
    uint own = ExcludePid;
    EnumWindows(delegate(IntPtr h, IntPtr lp) {
      try {
        if (!IsWindowVisible(h)) return true;
        int len = GetWindowTextLength(h);
        if (len <= 0) return true;
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        var cls = new StringBuilder(256);
        GetClassName(h, cls, cls.Capacity);
        int cloaked = 0;
        DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, 4);
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        uint pid; GetWindowThreadProcessId(h, out pid);
        string title = sb.ToString();
        if (!Capturable(true, title, cloaked != 0, (ex & WS_EX_TOOLWINDOW) != 0, (ex & WS_EX_APPWINDOW) != 0, GetWindow(h, GW_OWNER) != IntPtr.Zero, pid, own, cls.ToString())) return true;
        var info = new WindowInfo { Handle = h, Title = title, ClassName = cls.ToString(), ProcessId = pid, Minimized = IsIconic(h) };
        info.ExePath = ExePathOf(pid);
        info.ExeName = string.IsNullOrEmpty(info.ExePath) ? "" : Path.GetFileName(info.ExePath);
        RECT r;
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) == 0 || GetWindowRect(h, out r)) { info.Width = r.Right - r.Left; info.Height = r.Bottom - r.Top; }
        list.Add(info);
      } catch (Exception) { }
      return true;
    }, IntPtr.Zero);
    return list;
  }

  public static string ExePathOf(uint pid) {
    IntPtr p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
    if (p == IntPtr.Zero) return null;
    try {
      var sb = new StringBuilder(1024);
      uint size = (uint)sb.Capacity;
      return QueryFullProcessImageName(p, 0, sb, ref size) ? sb.ToString() : null;
    } finally { CloseHandle(p); }
  }

  public static bool IsMinimized(IntPtr h) { return IsIconic(h); }

  // The size of the window's visible frame, or 0 x 0 when it can't be read.
  public static Size FrameSize(IntPtr h) {
    RECT r;
    if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) != 0 && !GetWindowRect(h, out r)) return Size.Empty;
    return new Size(r.Right - r.Left, r.Bottom - r.Top);
  }

  // A window that isn't redrawing sends the capture nothing, and ffmpeg waits for a frame before it will do anything else,
  // even stop. Asking the window to repaint (no waiting on it) makes it send one.
  public static void AskToRepaint(IntPtr h) { RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_NOERASE); }

  // A picture of one window, taken by the window itself (PrintWindow, full content): the tests' screenshots of their own windows.
  public static Bitmap Picture(IntPtr h) {
    RECT r;
    GetWindowRect(h, out r);
    int w = r.Right - r.Left, ht = r.Bottom - r.Top;
    if (w <= 0 || ht <= 0) return null;
    var bmp = new Bitmap(w, ht);
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr dc = g.GetHdc();
      try { PrintWindow(h, dc, 2); } finally { g.ReleaseHdc(dc); }
    }
    return bmp;
  }
}

// Every child process goes in one Job Object whose handle only this process holds: when the Recorder ends, however it ends,
// Windows ends everything in the job. JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
static class Jobs {
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);
  [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

  [StructLayout(LayoutKind.Sequential)]
  struct BasicLimits {
    public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass, SchedulingClass;
  }
  [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong a, b, c, d, e, f; }
  [StructLayout(LayoutKind.Sequential)]
  struct ExtendedLimits {
    public BasicLimits Basic;
    public IoCounters Io;
    public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
  }

  const uint KILL_ON_JOB_CLOSE = 0x2000;
  const int JobObjectExtendedLimitInformation = 9;
  static IntPtr job = IntPtr.Zero;
  static readonly object gate = new object();

  static IntPtr Job() {
    lock (gate) {
      if (job != IntPtr.Zero) return job;
      IntPtr h = CreateJobObject(IntPtr.Zero, null);
      if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't create the job that keeps child processes from outliving the Recorder");
      var limits = new ExtendedLimits();
      limits.Basic.LimitFlags = KILL_ON_JOB_CLOSE;
      int size = Marshal.SizeOf(typeof(ExtendedLimits));
      IntPtr buf = Marshal.AllocHGlobal(size);
      try {
        Marshal.StructureToPtr(limits, buf, false);
        if (!SetInformationJobObject(h, JobObjectExtendedLimitInformation, buf, (uint)size)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't set the job's kill-on-close limit");
      } finally { Marshal.FreeHGlobal(buf); }
      job = h;
      return job;
    }
  }

  public static void Add(Process p) {
    if (!AssignProcessToJobObject(Job(), p.Handle)) {
      int err = Marshal.GetLastWin32Error();
      try { p.Kill(); } catch (Exception) { }
      throw new Win32Exception(err, "Couldn't put " + p.StartInfo.FileName + " in the Recorder's job; it was ended");
    }
  }
}

// Starts a program the way the Recorder always does: no window of any kind, every stream redirected, inside the job.
static class Proc {
  public static readonly List<int> Started = new List<int>();   // every pid started here, for the tests' watcher

  public static Process Start(string exe, string args, bool redirectInput) {
    var psi = new ProcessStartInfo(exe, args) {
      UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
      RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
      StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
      WorkingDirectory = Path.GetDirectoryName(exe)
    };
    var p = Process.Start(psi);
    try { Jobs.Add(p); } catch (Exception) { try { p.Kill(); } catch (Exception) { } throw; }
    lock (Started) Started.Add(p.Id);
    if (!redirectInput) { try { p.StandardInput.Close(); } catch (Exception) { } }
    return p;
  }

  // Runs a program to its end and returns its exit code, -1 when it had to be ended after the timeout.
  public static int Run(string exe, string args, int timeoutMs, out string stdout, out string stderr) {
    var p = Start(exe, args, false);
    var o = new StringBuilder(); var e = new StringBuilder();
    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs d) { if (d.Data != null) lock (o) o.AppendLine(d.Data); };
    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs d) { if (d.Data != null) lock (e) e.AppendLine(d.Data); };
    p.BeginOutputReadLine(); p.BeginErrorReadLine();
    int code;
    if (p.WaitForExit(timeoutMs)) { p.WaitForExit(); code = p.ExitCode; }
    else { try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { } code = -1; }
    lock (o) stdout = o.ToString();
    lock (e) stderr = e.ToString();
    p.Dispose();
    return code;
  }
}
