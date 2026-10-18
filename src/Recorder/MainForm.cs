// SPDX-License-Identifier: GPL-2.0-or-later
// Bifrost Recorder's window: the windows you can record down the middle, Record and Stop at the bottom. Painted in the
// Bifrost console style (Look.cs). Everything a test needs to drive it, and to take its picture, is public at the bottom.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

class MainForm : Form {
  public enum State { Idle, Starting, Recording, Saving }

  // The tests set this before opening the window: it then opens off the monitors, off the taskbar and never takes the focus.
  public static bool Quiet;

  [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string list);

  class WindowList : ListBox {
    public WindowList() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
  }

  readonly WindowList list = new WindowList();
  readonly PillButton record = new PillButton(), refresh = new PillButton(), open = new PillButton(), upload = new PillButton(), settingsBtn = new PillButton();
  string prefsFile;
  string encoderNote = "";                       // when the first encoder didn't start and another is in use
  EncoderProfile chipEncoder;                    // the encoder the chip names
  SettingsForm settingsWindow;
  string hotkeyText = "", hotkeyProblem;       // what is shown for the global key; why it isn't registered, if it isn't
  WindowInfo lastTarget;                          // the window recorded last, for the hotkey when nothing is selected
  readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
  readonly Dictionary<string, Image> icons = new Dictionary<string, Image>();
  float S = 1f;
  State state = State.Idle;
  Recording current;
  WindowInfo target;
  string status = "Pick a window, then press Record.";
  Palette.Tone statusTone = Palette.Tone.Muted;
  string encoderText = "Checking the encoder…";
  string recordingsFolder, logsFolder;
  AppSettings cfg = new AppSettings();
  string gpuShown;   // what the last recording got on the GPU ("high", "above-normal", "normal"); until then the setting
  bool closeWhenDone;
  bool encoderReady;

  // Tests: which windows may be listed (null = all), where recordings go, what "Open recordings folder" does.
  public Func<WindowInfo, bool> WindowFilter;
  public Action<string> OpenFolder;
  public event Action<RecordingResult> Saved;
  public RecordingResult LastResult;

  // Tests: no beeps, and a place to see which cue would have played; what "Upload to Armory" does when there is no key (opens the website).
  public static bool NoSound;
  public static bool SkipEncoderCheck;   // the tests that must not start any GPU encoder session (nothing is recorded in them)
  public static Action<string> CueHook;
  public Action<string> OpenUrl;

  [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
  [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
  const int WM_HOTKEY = 0x0312, HotkeyId = 0xB1F2;
  const string WebsiteVideos = "https://taskforcehalberd.org/hub/videos";

  public MainForm() {
    Text = "Bifrost Recorder";
    BackColor = Palette.Bg; ForeColor = Palette.Fg;
    KeyPreview = true;
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    using (var g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;
    ClientSize = new Size(P(820), P(678));
    MinimumSize = SizeFromClientSize(new Size(P(640), P(560)));
    try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
    if (Quiet) { StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); }
    else StartPosition = FormStartPosition.CenterScreen;

    string settingsFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Settings.FileName);
    prefsFile = Settings.PrefsFile(settingsFile, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    string prefsProblem;
    cfg = Settings.LoadAll(settingsFile, prefsFile, out prefsProblem);   // the member's own choices over the settings file over the defaults
    if (prefsProblem != null) { status = prefsProblem; statusTone = Palette.Tone.Amber; }
    Palette.Apply(cfg.ThemeId, cfg.Accent);
    BackColor = Palette.Bg; ForeColor = Palette.Fg;
    recordingsFolder = Settings.RecordingsFolder(settingsFile, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    logsFolder = Settings.LogsFolder(settingsFile, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    list.DrawMode = DrawMode.OwnerDrawFixed; list.BorderStyle = BorderStyle.None; list.BackColor = Palette.Surface; list.ForeColor = Palette.Fg;
    list.IntegralHeight = false; list.ItemHeight = P(56);
    list.DrawItem += DrawRow;
    list.SelectedIndexChanged += delegate { UpdateButtons(); Invalidate(); };
    list.DoubleClick += delegate { if (state == State.Idle && record.Enabled) record.Press(); };
    Controls.Add(list);

    record.Scale = S; record.Text = "Record"; record.Click += delegate { Toggle(); };
    refresh.Scale = S; refresh.Look = PillButton.Kind.Ghost; refresh.Text = "Refresh"; refresh.Click += delegate { if (state == State.Idle) ReloadWindows(); };
    open.Scale = S; open.Look = PillButton.Kind.Ghost; open.Text = "Open recordings folder"; open.Click += delegate { OpenRecordings(); };
    upload.Scale = S; upload.Look = PillButton.Kind.Ghost; upload.Text = "Upload to Armory"; upload.Click += delegate { OpenUpload(); };
    upload.Enabled = false;   // until a recording has been saved
    settingsBtn.Scale = S; settingsBtn.Look = PillButton.Kind.Ghost; settingsBtn.Text = "Settings"; settingsBtn.Click += delegate { OpenSettings(); };
    record.Behind = open.Behind = upload.Behind = delegate { return Palette.Surface; };   // each sits on the colour behind it
    refresh.Behind = settingsBtn.Behind = delegate { return Palette.Bg; };
    Controls.Add(record); Controls.Add(refresh); Controls.Add(settingsBtn); Controls.Add(open); Controls.Add(upload);
    Palette.Changed += OnPalette;

    tick.Interval = 250;
    tick.Tick += delegate {
      if (state == State.Recording && current != null) { UpdateRecordingStatus(); InvalidateBottom(); }
      else if (state == State.Saving && current != null && current.Draining) { SetStatus("Finishing…  " + current.LagSeconds.ToString("0") + " s of video still to write", Palette.Amber); InvalidateBottom(); }
    };
    DoLayout();
  }

  int P(float v) { return (int)Math.Round(v * S); }

  const float HeaderH = 92, BottomH = 214, Side = 32;

  void DoLayout() {
    int w = ClientSize.Width, h = ClientSize.Height;
    refresh.SetBounds(w - P(Side) - P(112), P(HeaderH + 16), P(112), P(34));
    settingsBtn.SetBounds(w - P(Side) - P(112) - P(10) - P(118), P(HeaderH + 16), P(118), P(34));
    list.SetBounds(P(Side), P(HeaderH + 60), w - 2 * P(Side), h - P(HeaderH + 60) - P(BottomH) - P(14));
    record.SetBounds(w - P(Side) - P(230), h - P(BottomH) + P(22), P(230), P(56));
    open.SetBounds(w - P(Side) - P(230), h - P(BottomH) + P(92), P(230), P(36));
    upload.SetBounds(w - P(Side) - P(230), h - P(BottomH) + P(134), P(230), P(36));
  }

  protected override void OnResize(EventArgs e) { base.OnResize(e); if (record != null) DoLayout(); }

  protected override CreateParams CreateParams {
    get {
      var cp = base.CreateParams;
      if (Quiet) cp.ExStyle = (cp.ExStyle | 0x80 | 0x08000000) & ~0x40000;   // tool window, no activate, no taskbar button
      return cp;
    }
  }
  protected override bool ShowWithoutActivation { get { return Quiet; } }

  protected override void OnHandleCreated(EventArgs e) {
    base.OnHandleCreated(e);
    Palette.StyleTitleBar(Handle);
    try { SetWindowTheme(list.Handle, Palette.Dark ? "DarkMode_Explorer" : "Explorer", null); } catch (Exception) { }
    RegisterGlobalKey();
  }

  protected override void OnHandleDestroyed(EventArgs e) { try { UnregisterHotKey(Handle, HotkeyId); } catch (Exception) { } base.OnHandleDestroyed(e); }

  // The global start/stop key, so nobody has to switch to the Recorder (and away from the game) to start or stop.
  void RegisterGlobalKey() {
    hotkeyProblem = null; hotkeyText = "";
    if (string.Equals(cfg.Hotkey, "off", StringComparison.OrdinalIgnoreCase)) { hotkeyProblem = "off"; return; }
    uint mods, vk; string shown;
    if (!Hotkeys.TryParse(cfg.Hotkey, out mods, out vk, out shown)) { hotkeyProblem = "the Hotkey setting isn't a key like Ctrl+Alt+R"; if (!Hotkeys.TryParse(Hotkeys.Default, out mods, out vk, out shown)) return; }
    if (Quiet) { hotkeyText = shown; return; }   // the tests don't take a real global key
    if (RegisterHotKey(Handle, HotkeyId, mods | 0x4000, vk)) { hotkeyText = shown; return; }
    // Taken by another program (Ctrl+Alt+R is, on the PC this was built on). The default then falls back to the same key with Shift added;
    // a key the member chose themselves is not swapped for another: it is reported.
    uint m2, v2; string s2;
    if (string.Equals(cfg.Hotkey, Hotkeys.Default, StringComparison.OrdinalIgnoreCase) && Hotkeys.TryParse(Hotkeys.Fallback, out m2, out v2, out s2) && RegisterHotKey(Handle, HotkeyId, m2 | 0x4000, v2)) { hotkeyText = s2; hotkeyProblem = null; return; }
    hotkeyProblem = shown + " is taken by another program";
  }

  protected override void WndProc(ref Message m) {
    if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId) { HotkeyPressed(); return; }
    base.WndProc(ref m);
  }

  // The same as pressing Record or Stop. With nothing selected, the window recorded last is used if it is still there.
  public void HotkeyPressed() {
    if (state == State.Recording) { BeginStop(null); return; }
    if (state != State.Idle) return;
    ReloadWindows();
    if (list.SelectedItem == null && lastTarget != null && Native.IsWindow(lastTarget.Handle)) SelectHwnd(lastTarget.Handle);
    if (list.SelectedItem == null) { SetStatus("The hotkey needs a window: pick one in the list first.", Palette.Red); Cue("error"); return; }
    BeginStart();
  }

  // A short beep on start and stop, so a recording started or ended from inside a game can be heard (the beep isn't part of what is recorded).
  void Cue(string what) {
    var hook = CueHook; if (hook != null) hook(what);
    if (NoSound || !cfg.Cue) return;
    ThreadPool.QueueUserWorkItem(delegate {
      try {
        if (what == "start") { Console.Beep(660, 90); Console.Beep(990, 120); }
        else if (what == "stop") { Console.Beep(990, 90); Console.Beep(660, 140); }
        else { Console.Beep(330, 260); }
      } catch (Exception) { }
    });
  }

  // Upload to Armory: the upload window when Armory.cs has a key and a https server from the settings, else the website's page for it.
  void OpenUpload() {
    if (LastResult == null || !LastResult.Ok || LastResult.Mp4 == null || !File.Exists(LastResult.Mp4)) return;
    if (!ArmoryClient.Usable(cfg)) {
      if (OpenUrl != null) { OpenUrl(WebsiteVideos); return; }
      try { Process.Start(new ProcessStartInfo(WebsiteVideos) { UseShellExecute = true }); } catch (Exception ex) { SetStatus("Can't open the browser: " + ex.Message, Palette.Red); }
      return;
    }
    ArmoryClient c;
    try { c = new ArmoryClient(cfg.TeamServer, cfg.TeamKey); } catch (ArmoryException ex) { SetStatus(ex.Message, Palette.Red); return; }
    string baseTitle = Path.GetFileNameWithoutExtension(LastResult.Mp4);
    UploadWindow = new UploadForm(c, LastResult.Mp4, (int)Math.Round(LastResult.Seconds), baseTitle);
    if (Quiet) { UploadWindow.Show(); return; }
    UploadWindow.ShowDialog(this);
    UploadWindow.Dispose(); UploadWindow = null;
  }

  public UploadForm UploadWindow;

  protected override void OnShown(EventArgs e) {
    base.OnShown(e);
    ReloadWindows();
    tick.Start();
    if (!File.Exists(Ffmpeg.Exe)) { SetStatus("ffmpeg.exe is missing from the bin folder next to the Recorder.", Palette.Red); encoderText = "No ffmpeg"; UpdateButtons(); Invalidate(); return; }
    // How the encoder is chosen (the EncoderCheck setting): a test encode starts a GPU encoder session, and starting one next to a running game has reset
    // this PC's GPU driver, so it can be put off to the first Record, or skipped (by the GPU's maker, with a fall back if the real start fails).
    if (SkipEncoderCheck) { encoderText = "Encoder not checked (test)"; Invalidate(); return; }
    if (cfg.EncoderCheck == "vendor") { var v = EncoderChoice.ByVendor(); encoderReady = true; ShowChip(v); UpdateButtons(); Invalidate(); return; }
    if (cfg.EncoderCheck == "record") { encoderReady = true; encoderText = "Encoder: checked at the first Record"; UpdateButtons(); Invalidate(); return; }
    var t = new Thread(delegate() {
      try { var enc = EncoderChoice.Detect(Ffmpeg.Exe); Ui(delegate { encoderReady = true; ShowChip(enc); UpdateButtons(); Invalidate(); }); }
      catch (Exception ex) { Ui(delegate { SetStatus("Couldn't check the encoder: " + ex.Message, Palette.Red); }); }
    });
    t.IsBackground = true; t.Name = "encoder check"; t.Start();
  }

  // The header chip: the active set, e.g. "HEVC · NVENC · standard · 1080p · GPU high".
  public static string ChipText(EncoderProfile e, AppSettings c, string gpu) {
    return (e.Codec == "hevc" ? "HEVC" : "H.264") + " · " + e.Short + " · " + c.EncoderProfile + " · " + (c.MaxHeight > 0 && !c.Screen ? c.MaxHeight + "p" : "native") +
           (c.Screen ? " · screen" : "") + (c.Vfr ? " · vfr" : "") + " · GPU " + (gpu ?? c.GpuPriority);
  }

  protected override void OnActivated(EventArgs e) { base.OnActivated(e); if (state == State.Idle && IsHandleCreated && !Quiet) ReloadWindows(); }

  protected override void OnFormClosing(FormClosingEventArgs e) {
    if (state != State.Idle) {   // finish the file first
      e.Cancel = true; closeWhenDone = true;
      if (state == State.Recording) BeginStop("Saving before closing…");
    }
    base.OnFormClosing(e);
  }

  protected override void OnFormClosed(FormClosedEventArgs e) { tick.Stop(); Palette.Changed -= OnPalette; base.OnFormClosed(e); }

  void ShowChip(EncoderProfile e) { chipEncoder = e; encoderText = ChipText(e, cfg, gpuShown); }

  // The colours changed (the Settings window, live): the controls that hold a colour take the new one, everything else repaints from Palette.
  void OnPalette() { Ui(delegate { ApplyTheme(); }); }

  void ApplyTheme() {
    BackColor = Palette.Bg; ForeColor = Palette.Fg;
    list.BackColor = Palette.Surface; list.ForeColor = Palette.Fg;
    if (IsHandleCreated) { Palette.StyleTitleBar(Handle); try { SetWindowTheme(list.Handle, Palette.Dark ? "DarkMode_Explorer" : "Explorer", null); } catch (Exception) { } }
    Invalidate(true);
  }

  // Settings: the five recording choices and the look. Changes are saved at once (the prefs file) and apply to the next recording; the chip follows.
  void OpenSettings() {
    if (settingsWindow != null && !settingsWindow.IsDisposed) { settingsWindow.Focus(); return; }
    settingsWindow = new SettingsForm(cfg, delegate { return BusyForTest || state != State.Idle; }, SettingsChanged);
    if (Quiet) { settingsWindow.Show(); return; }
    settingsWindow.ShowDialog(this);
    settingsWindow.Dispose(); settingsWindow = null;
  }

  void SettingsChanged() {
    Palette.Apply(cfg.ThemeId, cfg.Accent);
    try { Settings.SavePrefs(prefsFile, cfg); }
    catch (Exception ex) { SetStatus("Couldn't save your choices: " + ex.Message, Palette.Red); }
    if (chipEncoder != null) { ShowChip(chipEncoder); }
    Invalidate();
  }

  void Ui(MethodInvoker action) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(action); } catch (Exception) { } }

  // ---------------------------------------------------------------------------------------------- the list
  public void ReloadWindows() {
    IntPtr keep = list.SelectedItem is WindowInfo ? ((WindowInfo)list.SelectedItem).Handle : IntPtr.Zero;
    var found = Native.ListWindows();
    list.BeginUpdate();
    list.Items.Clear();
    foreach (var w in found) if (WindowFilter == null || WindowFilter(w)) list.Items.Add(w);
    if (keep != IntPtr.Zero) SelectHwnd(keep);
    list.EndUpdate();
    UpdateButtons();
    Invalidate();
  }

  void DrawRow(object sender, DrawItemEventArgs e) {
    if (e.Index < 0) return;
    var w = (WindowInfo)list.Items[e.Index];
    var g = e.Graphics;
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.SmoothingMode = SmoothingMode.AntiAlias;
    var r = e.Bounds;
    bool sel = (e.State & DrawItemState.Selected) != 0;
    bool busy = state != State.Idle;
    using (var b = new SolidBrush(sel ? Palette.Over(Palette.Surface, busy && state != State.Idle ? Palette.Red : Palette.Accent, 0.22) : Palette.Surface)) g.FillRectangle(b, r);
    using (var b = new SolidBrush(Palette.Rule)) g.FillRectangle(b, r.X, r.Bottom - 1, r.Width, 1);
    if (sel) using (var b = new SolidBrush(busy ? Palette.Red : Palette.Accent)) g.FillRectangle(b, r.X, r.Y, P(4), r.Height);
    var icon = IconFor(w);
    if (icon != null) g.DrawImage(icon, r.X + P(18), r.Y + (r.Height - P(30)) / 2, P(30), P(30));
    using (var f = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }) {
      float x = r.X + P(64), width = r.Width - P(64) - P(16);
      using (var b = new SolidBrush(busy && !sel ? Palette.Dim : Palette.Fg)) g.DrawString(w.Title, Fonts.Get(Fonts.UiBold, P(17)), b, new RectangleF(x, r.Y + P(8), width, P(24)), f);
      string size = w.Width > 0 ? w.Width + " × " + w.Height : "";
      string line = (w.ExeName.Length > 0 ? w.ExeName : "unknown program") + (size.Length > 0 ? "  ·  " + size : "") + (w.Minimized ? "  ·  minimized" : "");
      using (var b = new SolidBrush(Palette.Dim)) g.DrawString(line, Fonts.Get(Fonts.Mono, P(12)), b, new RectangleF(x, r.Y + P(33), width, P(18)), f);
    }
  }

  Image IconFor(WindowInfo w) {
    string key = w.ExePath ?? "";
    Image img;
    if (icons.TryGetValue(key, out img)) return img;
    try {
      using (var ic = key.Length > 0 ? Icon.ExtractAssociatedIcon(key) : null) img = ic != null ? new Bitmap(ic.ToBitmap()) : null;
    } catch (Exception) { img = null; }
    if (img == null) img = new Bitmap(SystemIcons.Application.ToBitmap());
    icons[key] = img;
    return img;
  }

  // ---------------------------------------------------------------------------------------------- painting
  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    int w = ClientSize.Width, h = ClientSize.Height;
    using (var b = new SolidBrush(Palette.Bg)) g.FillRectangle(b, 0, 0, w, h);

    // header
    using (var b = new SolidBrush(Palette.Band)) g.FillRectangle(b, 0, 0, w, P(HeaderH));
    using (var b = new SolidBrush(Palette.Accent)) g.FillRectangle(b, 0, P(HeaderH) - P(2), w, P(2));
    DrawRecDot(g, P(Side), P(30), P(26), state == State.Recording);
    using (var b = new SolidBrush(Palette.Fg)) g.DrawString("Bifrost Recorder", Fonts.Get(Fonts.UiBold, P(30)), b, P(Side + 40), P(16));
    using (var b = new SolidBrush(Palette.Soft)) g.DrawString("Record a game or program, light on your PC", Fonts.Get(Fonts.Ui, P(15)), b, P(Side + 42), P(56));
    var badgeFont = Fonts.Get(Fonts.Mono, P(12));
    var sz = g.MeasureString(encoderText, badgeFont);
    var badge = new RectangleF(w - P(Side) - sz.Width - P(20), P(34), sz.Width + P(20), P(26));
    using (var p = new Pen(Palette.Alpha(Palette.AccentInk, 0.6))) g.DrawRectangle(p, badge.X, badge.Y, badge.Width, badge.Height);
    using (var b = new SolidBrush(Palette.AccentInk)) g.DrawString(encoderText, badgeFont, b, badge.X + P(10), badge.Y + P(5));

    // caption above the list
    Spaced(g, "WINDOWS", Fonts.Get(Fonts.Mono, P(12)), Palette.Dim, P(Side), P(HeaderH + 26), P(1.5f));
    var cnt = list.Items.Count.ToString();
    float cx = P(Side) + SpacedWidth(g, "WINDOWS", Fonts.Get(Fonts.Mono, P(12)), P(1.5f)) + P(12);
    using (var p = new Pen(Palette.Edge)) g.DrawRectangle(p, cx, P(HeaderH + 24), P(8) + g.MeasureString(cnt, Fonts.Get(Fonts.Mono, P(12))).Width, P(20));
    using (var b = new SolidBrush(Palette.Muted)) g.DrawString(cnt, Fonts.Get(Fonts.Mono, P(12)), b, cx + P(4), P(HeaderH + 27));
    if (list.Items.Count == 0) {
      using (var b = new SolidBrush(Palette.Surface)) g.FillRectangle(b, list.Bounds);
      using (var b = new SolidBrush(Palette.Dim)) g.DrawString("No windows to record. Open the game or program, then Refresh.", Fonts.Get(Fonts.Ui, P(16)), b, list.Left + P(20), list.Top + P(20));
    }
    using (var p = new Pen(Palette.Rule)) g.DrawRectangle(p, list.Left - 1, list.Top - 1, list.Width + 1, list.Height + 1);

    // bottom: elapsed time, status line
    float by = h - P(BottomH);
    using (var b = new SolidBrush(Palette.Surface)) g.FillRectangle(b, 0, by, w, P(BottomH));
    using (var b = new SolidBrush(Palette.Rule)) g.FillRectangle(b, 0, by, w, 1);
    TimeSpan t = current != null && (state == State.Recording || state == State.Saving) ? current.Elapsed : TimeSpan.Zero;
    string clock = string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
    using (var b = new SolidBrush(state == State.Recording ? Palette.Red : Palette.Fg)) g.DrawString(clock, Fonts.Get(Fonts.Mono, P(46)), b, P(Side) - P(2), by + P(18));
    Spaced(g, state == State.Recording ? "RECORDING" : state == State.Saving ? "SAVING" : state == State.Starting ? "STARTING" : "ELAPSED", Fonts.Get(Fonts.Mono, P(11)), Palette.Dim, P(Side), by + P(80), P(1.5f));
    string hk = hotkeyText.Length > 0 ? "HOTKEY  " + hotkeyText + "  START / STOP" : hotkeyProblem == "off" ? "HOTKEY OFF" : "NO HOTKEY: " + hotkeyProblem;
    Spaced(g, hk, Fonts.Get(Fonts.Mono, P(11)), hotkeyText.Length > 0 || hotkeyProblem == "off" ? Palette.Dim : Palette.Amber, P(Side), h - P(30), P(1.2f));
    using (var f = new StringFormat { Trimming = StringTrimming.EllipsisWord })
    using (var b = new SolidBrush(Palette.OfTone(statusTone)))
      g.DrawString(status, Fonts.Get(Fonts.Ui, P(15)), b, new RectangleF(P(Side), by + P(104), w - 3 * P(Side) - P(230), h - by - P(104) - P(10)), f);
  }

  void DrawRecDot(Graphics g, float x, float y, float d, bool live) {
    using (var b = new SolidBrush(live ? Palette.Red : Palette.Accent)) g.FillEllipse(b, x, y, d, d);
    using (var b = new SolidBrush(Palette.Band)) g.FillEllipse(b, x + d * 0.3f, y + d * 0.3f, d * 0.4f, d * 0.4f);
  }

  static void Spaced(Graphics g, string s, Font f, Color c, float x, float y, float spacing) {
    using (var b = new SolidBrush(c)) foreach (char ch in s) { g.DrawString(ch.ToString(), f, b, x, y, StringFormat.GenericTypographic); x += g.MeasureString(ch.ToString(), f, 1000, StringFormat.GenericTypographic).Width + spacing; }
  }

  static float SpacedWidth(Graphics g, string s, Font f, float spacing) {
    float x = 0;
    foreach (char ch in s) x += g.MeasureString(ch.ToString(), f, 1000, StringFormat.GenericTypographic).Width + spacing;
    return x;
  }

  void InvalidateBottom() { Invalidate(new Rectangle(0, ClientSize.Height - P(BottomH), ClientSize.Width - P(Side + 230 + 20), P(BottomH))); }

  // ---------------------------------------------------------------------------------------------- recording
  void SetStatus(string text, Color c) { status = text; statusTone = Palette.ToneOf(c); Invalidate(); }

  void UpdateButtons() {
    bool idle = state == State.Idle;
    record.Enabled = (idle && list.SelectedItem != null && encoderReady && File.Exists(Ffmpeg.Exe)) || state == State.Recording;
    record.Look = state == State.Recording ? PillButton.Kind.Danger : PillButton.Kind.Primary;
    record.Text = state == State.Recording ? "Stop" : state == State.Starting ? "Starting…" : state == State.Saving ? "Saving…" : "Record";
    refresh.Enabled = idle; list.Enabled = idle; open.Enabled = true;
    upload.Enabled = idle && LastResult != null && LastResult.Ok && LastResult.Mp4 != null && File.Exists(LastResult.Mp4);
    record.Invalidate(); refresh.Invalidate();
  }

  void Toggle() {
    if (state == State.Idle) BeginStart();
    else if (state == State.Recording) BeginStop(null);
  }

  void BeginStart() {
    var w = list.SelectedItem as WindowInfo;
    if (w == null) return;
    if (!Native.IsWindow(w.Handle)) { SetStatus("That window has gone. Press Refresh.", Palette.Red); ReloadWindows(); return; }
    if (Native.IsMinimized(w.Handle)) { SetStatus("That window is minimized: it sends no picture. Restore it first.", Palette.Red); return; }
    target = w; lastTarget = w; encoderNote = "";
    state = State.Starting; UpdateButtons();
    SetStatus("Starting…", Palette.Muted);
    string folder = recordingsFolder;
    var t = new Thread(delegate() {
      EncoderProfile enc;
      var mine = cfg.Clone();   // what this recording is made with: later changes in Settings apply to the next one
      try { enc = EncoderChoice.Picked ?? (cfg.EncoderCheck == "record" ? EncoderChoice.Detect(Ffmpeg.Exe) : EncoderProfile.Software); }
      catch (Exception) { enc = EncoderProfile.Software; }
      Size sz = Native.ClientSize(w.Handle);   // what the capture records: the client area
      if (sz.Width <= 0 || sz.Height <= 0) sz = Native.FrameSize(w.Handle);
      string baseName = FileNames.Unique(folder, FileNames.BaseName(FileNames.Friendly(w.ExePath, w.Title), DateTime.Now));
      var firstEncoder = enc;
      Recording rec; string error; bool ok;
      while (true) {
        rec = new Recording(enc, w.Handle, sz.Width > 0 ? sz.Width : w.Width, sz.Height > 0 ? sz.Height : w.Height, folder, baseName, mine, logsFolder, mine.Screen ? Dxgi.ForWindow(w.Handle) : null);
        ok = rec.Start(out error, 15000);
        // an encoder picked without a test encode that ffmpeg then refuses: the next one is tried
        var next = ok || mine.EncoderCheck != "vendor" || error == null || !error.StartsWith("ffmpeg ended before the first picture") ? null : EncoderChoice.Next(enc);
        if (next == null) break;
        enc = next; EncoderChoice.Set(enc);
      }
      Ui(delegate {
        if (!ok) { state = State.Idle; UpdateButtons(); SetStatus(error, Palette.Red); Cue("error"); if (closeWhenDone) Close(); return; }
        current = rec; gpuShown = rec.GpuPriorityResult; ShowChip(enc);
        if (enc != firstEncoder) encoderNote = "  ·  " + firstEncoder.Short + " didn't start, using " + enc.Short;
        rec.Ended += delegate(string why) { Ui(delegate { if (state == State.Recording && current == rec) BeginStop(why + ". Saving…"); }); };
        state = State.Recording; UpdateButtons(); UpdateRecordingStatus(); Invalidate(); Cue("start");
        if (closeWhenDone) BeginStop("Saving before closing…");
      });
    });
    t.IsBackground = true; t.Name = "recording start"; t.Start();
  }

  void UpdateRecordingStatus() {
    if (current == null) return;
    string size = Megabytes(current.FileBytes);
    string drops = current.Drop > 0 ? "  ·  " + current.Drop + " dropped" : "";
    // whether the encoder is keeping up with the window: ffmpeg's progress, worked out over the last few seconds (Telemetry)
    SetStatus("Recording " + (target.ExeName.Length > 0 ? target.ExeName : target.Title) + "  ·  " + size + drops + "  ·  " + current.Health + encoderNote, current.Behind ? Palette.Amber : Palette.Soft);
  }

  static string Megabytes(long bytes) { return bytes >= 1024L * 1024 * 1024 ? (bytes / 1073741824.0).ToString("0.00") + " GB" : (bytes / 1048576.0).ToString("0.0") + " MB"; }

  void BeginStop(string why) {
    if (state != State.Recording || current == null) return;
    state = State.Saving; UpdateButtons();
    var rec = current;
    SetStatus(why ?? (rec.BehindNow ? "Finishing…  " + rec.LagSeconds.ToString("0") + " s of video still to write" : "Saving…"), rec.BehindNow ? Palette.Amber : Palette.Muted);
    var t = new Thread(delegate() {
      RecordingResult res;
      try { res = rec.Stop(); }
      catch (Exception ex) { res = new RecordingResult { Message = "Stopping failed: " + ex.Message }; }
      Ui(delegate {
        LastResult = res;
        state = State.Idle; UpdateButtons(); Cue(res.Ok ? "stop" : "error");
        if (res.Ok) SetStatus("Saved " + Path.GetFileName(res.Mp4) + "  ·  " + FormatTime(res.Seconds) + "  ·  " + Megabytes(res.Bytes), Palette.Accent);
        else SetStatus(res.Message, Palette.Red);
        var handler = Saved; if (handler != null) handler(res);
        if (closeWhenDone) Close();
      });
    });
    t.IsBackground = true; t.Name = "recording stop"; t.Start();
  }

  static string FormatTime(double secs) { var t = TimeSpan.FromSeconds(Math.Round(secs)); return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds); }

  void OpenRecordings() {
    try { Directory.CreateDirectory(recordingsFolder); } catch (Exception ex) { SetStatus("Can't make the folder: " + ex.Message, Palette.Red); return; }
    if (OpenFolder != null) { OpenFolder(recordingsFolder); return; }
    try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + recordingsFolder + "\"") { UseShellExecute = false }); }
    catch (Exception ex) { SetStatus("Can't open the folder: " + ex.Message, Palette.Red); }
  }

  // ---------------------------------------------------------------------------------------------- for the tests
  public State Mode { get { return state; } }
  public string StatusText { get { return status; } }
  public string RecordingsFolder { get { return recordingsFolder; } set { recordingsFolder = value; } }
  public bool EncoderReady { get { return encoderReady; } }
  public int WindowCount { get { return list.Items.Count; } }
  public Recording Current { get { return current; } }
  public bool RecordEnabled { get { return record.Enabled; } }
  public string RecordText { get { return record.Text; } }
  public string ChipTextNow { get { return encoderText; } }
  public AppSettings Config { get { return cfg; } set { cfg = value; } }
  public string LogsFolder { get { return logsFolder; } set { logsFolder = value; } }

  public bool SelectHwnd(IntPtr h) {
    for (int i = 0; i < list.Items.Count; i++) if (((WindowInfo)list.Items[i]).Handle == h) { list.SelectedIndex = i; return true; }
    return false;
  }

  public void PressRecord() { record.Press(); }
  public void PressRefresh() { refresh.Press(); }
  public void PressOpen() { open.Press(); }
  public void PressUploadButton() { upload.Press(); }
  public void PressSettings() { settingsBtn.Press(); }
  public SettingsForm SettingsWindow { get { return settingsWindow; } }
  public string PrefsPath { get { return prefsFile; } set { prefsFile = value; } }
  public bool BusyForTest;                                     // makes the Settings window think a recording is running
  public void SetChipEncoderForTest(EncoderProfile e) { ShowChip(e); Invalidate(); }
  public bool UploadEnabled { get { return upload.Enabled; } }
  public string HotkeyShown { get { return hotkeyText; } }
  public string HotkeyProblem { get { return hotkeyProblem; } }
  public void SetResultForTest(RecordingResult r) { LastResult = r; UpdateButtons(); }
}
