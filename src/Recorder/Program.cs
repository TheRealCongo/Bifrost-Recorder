// Bifrost Recorder: records one window (a game or a program) with the ffmpeg in the bin folder beside it.
// BifrostRecorder.settings.json beside the program (written by Bifrost Suite) holds DataFolder; recordings go to
// <DataFolder>\Recordings, or to %USERPROFILE%\Videos\Bifrost Recorder without it.
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Program {
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

  [STAThread]
  static int Main(string[] args) {
    try { SetProcessDPIAware(); } catch (Exception) { }
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.Run(new MainForm());
    return 0;
  }
}
