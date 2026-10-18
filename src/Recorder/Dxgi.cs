// SPDX-License-Identifier: GPL-2.0-or-later
// Which DXGI output (monitor, on which graphics adapter) a window is on, for screen capture (ddagrab). Enumeration only: it creates a
// DXGI factory and asks it questions; no device, no GPU work. C# 5 for Windows' own compiler.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

// One monitor as DXGI sees it. Adapter and Output are DXGI's own enumeration indexes, which are what ffmpeg uses too:
//   -init_hw_device d3d11va=dda:<Adapter>   makes ffmpeg's Direct3D 11 device on adapter number <Adapter> (IDXGIFactory::EnumAdapters)
//   ddagrab=output_idx=<Output>            takes output number <Output> OF THAT DEVICE'S ADAPTER (IDXGIAdapter::EnumOutputs)
// so the pair is only right together. The Desktop Duplication API can only capture an output through a device on the adapter the
// monitor is plugged into; the capture's frames then live on that adapter, and an encoder on another GPU can't read them.
class DxgiOutput {
  public int Adapter, Output;
  public string AdapterName, DeviceName;   // e.g. "NVIDIA GeForce RTX 3070 Ti", "\\.\DISPLAY1"
  public uint VendorId;                    // 0x10DE NVIDIA, 0x1002 AMD, 0x8086 Intel
  public Rectangle Desktop;                // the monitor's rectangle on the virtual desktop, in pixels
  public IntPtr Monitor;                   // its HMONITOR
  public int Width { get { return Desktop.Width; } }
  public int Height { get { return Desktop.Height; } }
  public override string ToString() { return "adapter " + Adapter + " (" + AdapterName + ") output " + Output + " " + DeviceName + " " + Desktop.Width + "x" + Desktop.Height + " at " + Desktop.X + "," + Desktop.Y; }

  // The GPU vendor an encoder belongs to, or 0 when it runs on any (the software one).
  public static uint VendorOf(EncoderProfile e) {
    switch (e.Name) { case "hevc_nvenc": return 0x10DE; case "hevc_amf": return 0x1002; case "hevc_qsv": return 0x8086; default: return 0; }
  }
}

static class Dxgi {
  [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
  [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
  [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(Native.POINT pt, uint flags);

  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumFn(IntPtr self, uint index, out IntPtr result);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AdapterDescFn(IntPtr self, out AdapterDesc1 desc);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OutputDescFn(IntPtr self, out OutputDesc desc);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct AdapterDesc1 {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    public uint VendorId, DeviceId, SubSysId, Revision;
    public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
    public long AdapterLuid;
    public uint Flags;
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct OutputDesc {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    public Native.RECT DesktopCoordinates;
    public int AttachedToDesktop, Rotation;
    public IntPtr Monitor;
  }

  // Slots in the COM vtables (IUnknown 0-2, then IDXGIObject 3-6): IDXGIFactory1::EnumAdapters1 is 12; IDXGIAdapter::EnumOutputs is 7 and
  // IDXGIAdapter1::GetDesc1 is 10; IDXGIOutput::GetDesc is 7.
  static T Slot<T>(IntPtr obj, int index) where T : class {
    return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), index * IntPtr.Size), typeof(T));
  }

  static void Release(IntPtr obj) { if (obj != IntPtr.Zero) Slot<ReleaseFn>(obj, 2)(obj); }

  // Every output of every adapter, in DXGI's order.
  public static List<DxgiOutput> Outputs() {
    var list = new List<DxgiOutput>();
    Guid iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
    IntPtr factory;
    try { if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return list; }
    catch (Exception) { return list; }
    try {
      for (uint a = 0; ; a++) {
        IntPtr adapter;
        if (Slot<EnumFn>(factory, 12)(factory, a, out adapter) < 0) break;
        try {
          AdapterDesc1 ad;
          if (Slot<AdapterDescFn>(adapter, 10)(adapter, out ad) < 0) continue;
          for (uint o = 0; ; o++) {
            IntPtr output;
            if (Slot<EnumFn>(adapter, 7)(adapter, o, out output) < 0) break;
            try {
              OutputDesc od;
              if (Slot<OutputDescFn>(output, 7)(output, out od) < 0) continue;
              var r = od.DesktopCoordinates;
              list.Add(new DxgiOutput {
                Adapter = (int)a, Output = (int)o, AdapterName = ad.Description, VendorId = ad.VendorId, DeviceName = od.DeviceName,
                Desktop = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), Monitor = od.Monitor });
            } finally { Release(output); }
          }
        } finally { Release(adapter); }
      }
    } finally { Release(factory); }
    return list;
  }

  // The output a monitor handle belongs to (HMONITORs are what Windows and DXGI both hand out), or null.
  public static DxgiOutput ForMonitor(IntPtr monitor) {
    if (monitor == IntPtr.Zero) return null;
    foreach (var o in Outputs()) if (o.Monitor == monitor) return o;
    return null;
  }

  // The output holding most of a window (MONITOR_DEFAULTTONEAREST), or null.
  public static DxgiOutput ForWindow(IntPtr hwnd) { return ForMonitor(MonitorFromWindow(hwnd, 2)); }

  public static DxgiOutput ForPoint(int x, int y) { return ForMonitor(MonitorFromPoint(new Native.POINT { X = x, Y = y }, 2)); }
}
