// SPDX-License-Identifier: GPL-2.0-or-later
// "Upload to Armory": send the recording that was just saved to the TFH Armory's video library. A folder and a category from the
// Armory's own lists, an optional title, the terms (which must be ticked: the site refuses the upload without them), a progress bar
// and Cancel. The transfer is Armory.cs's UploadJob; this window only drives it. Painted in the Bifrost console style (Look.cs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

class UploadForm : Form {
  public enum Phase { Loading, Ready, Uploading, Done, Failed, Unavailable }

  const string Terms = "I hand this video to the unit to access, keep, edit and use however it chooses. It is kept for 60 days and then deleted, and the media team will reach out.";

  readonly ArmoryClient client;
  readonly string file;
  readonly int? duration;
  readonly ComboBox folder = new ComboBox(), category = new ComboBox();
  readonly TextBox title = new TextBox();
  readonly CheckBox terms = new CheckBox();
  readonly PillButton go = new PillButton(), cancel = new PillButton(), close = new PillButton();
  float S = 1f;
  Phase phase = Phase.Loading;
  string status = "Asking the Armory…";
  Palette.Tone statusTone = Palette.Tone.Muted;
  long sent, total;
  UploadJob job;
  DateTime startedAt;
  long maxBytes;
  string[] formats = new string[0];

  public Dictionary<string, object> Receipt;      // what the site answered when the upload finished

  public UploadForm(ArmoryClient client, string file, int? durationSeconds, string defaultTitle) {
    this.client = client; this.file = file; duration = durationSeconds;
    Text = "Upload to the Armory";
    FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    using (var g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;
    ClientSize = new Size(P(640), P(560));
    if (MainForm.Quiet) { StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); } else StartPosition = FormStartPosition.CenterParent;
    try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

    foreach (var c in new[] { folder, category }) { c.DropDownStyle = ComboBoxStyle.DropDownList; c.FlatStyle = FlatStyle.Flat; c.Font = Fonts.Get(Fonts.Ui, P(16)); c.Enabled = false; }
    title.BorderStyle = BorderStyle.FixedSingle; title.Font = Fonts.Get(Fonts.Ui, P(16)); title.MaxLength = 400; title.Text = defaultTitle ?? "";
    terms.Text = ""; terms.AutoSize = false; terms.FlatStyle = FlatStyle.Flat;
    terms.CheckedChanged += delegate { UpdateButtons(); };
    folder.SelectedIndexChanged += delegate { UpdateButtons(); }; category.SelectedIndexChanged += delegate { UpdateButtons(); };
    go.Scale = S; go.Text = "Upload"; go.Click += delegate { Start(); };
    cancel.Scale = S; cancel.Look = PillButton.Kind.Ghost; cancel.Text = "Cancel upload"; cancel.Click += delegate { if (job != null) job.Cancel(); };
    close.Scale = S; close.Look = PillButton.Kind.Ghost; close.Text = "Close"; close.Click += delegate { Close(); };
    go.Behind = cancel.Behind = close.Behind = delegate { return Palette.Bg; };
    Controls.AddRange(new Control[] { folder, category, title, terms, go, cancel, close });
    DoLayout();
    UpdateButtons();
    ApplyTheme();
    Palette.Changed += OnPalette;
  }

  void OnPalette() { Ui(delegate { ApplyTheme(); }); }

  // The colours of the controls that hold one; the painting reads Palette each time.
  void ApplyTheme() {
    BackColor = Palette.Bg; ForeColor = Palette.Fg;
    folder.BackColor = category.BackColor = title.BackColor = Palette.Field; folder.ForeColor = category.ForeColor = title.ForeColor = Palette.Fg;
    terms.ForeColor = Palette.Fg; terms.BackColor = Palette.Bg;
    if (IsHandleCreated) Palette.StyleTitleBar(Handle);
    Invalidate(true);
  }

  int P(float v) { return (int)Math.Round(v * S); }

  void DoLayout() {
    int w = ClientSize.Width, side = P(32);
    folder.SetBounds(side, P(168), w - 2 * side, P(32));
    category.SetBounds(side, P(238), w - 2 * side, P(32));
    title.SetBounds(side, P(308), w - 2 * side, P(32));
    terms.SetBounds(side, P(366), P(24), P(24));
    go.SetBounds(w - side - P(170), ClientSize.Height - P(64), P(170), P(46));
    cancel.SetBounds(w - side - P(170) - P(14) - P(170), ClientSize.Height - P(64), P(170), P(46));
    close.SetBounds(side, ClientSize.Height - P(64), P(120), P(46));
  }

  protected override CreateParams CreateParams {
    get { var cp = base.CreateParams; if (MainForm.Quiet) cp.ExStyle = (cp.ExStyle | 0x80 | 0x08000000) & ~0x40000; return cp; }
  }
  protected override bool ShowWithoutActivation { get { return MainForm.Quiet; } }

  protected override void OnHandleCreated(EventArgs e) {
    base.OnHandleCreated(e);
    Palette.StyleTitleBar(Handle);
  }

  protected override void OnShown(EventArgs e) {
    base.OnShown(e);
    var t = new Thread(Load) { IsBackground = true, Name = "armory lists" };
    t.Start();
  }

  protected override void OnFormClosed(FormClosedEventArgs e) { Palette.Changed -= OnPalette; base.OnFormClosed(e); }

  protected override void OnFormClosing(FormClosingEventArgs e) {
    if (phase == Phase.Uploading) { e.Cancel = true; if (job != null) job.Cancel(); }   // a cancel first; the window closes when it has been aborted
    base.OnFormClosing(e);
  }

  void Ui(MethodInvoker a) { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch (Exception) { } }

  // The site's rules first (connected, size, format), then its folders and categories.
  void Load() {
    try {
      var me = client.GetMe();
      maxBytes = me.MaxBytes; formats = me.Formats;
      long size = new FileInfo(file).Length;
      string ext = Path.GetExtension(file).ToLowerInvariant();
      if (!me.Connected) { Ui(delegate { SetPhase(Phase.Unavailable, "The Armory's video storage isn't connected right now. Try again later.", Palette.Red); }); return; }
      if (maxBytes > 0 && size > maxBytes) { Ui(delegate { SetPhase(Phase.Unavailable, "This recording is bigger than the Armory takes (" + Megabytes(maxBytes) + ").", Palette.Red); }); return; }
      if (formats.Length > 0 && Array.IndexOf(formats, ext) < 0) { Ui(delegate { SetPhase(Phase.Unavailable, "The Armory doesn't take " + ext + " files.", Palette.Red); }); return; }
      List<NamedItem> folders, cats;
      client.GetOptions(out folders, out cats);
      Ui(delegate {
        foreach (var f in folders) folder.Items.Add(f);
        foreach (var c in cats) category.Items.Add(c);
        if (folder.Items.Count > 0) folder.SelectedIndex = 0;
        if (category.Items.Count > 0) category.SelectedIndex = 0;
        folder.Enabled = category.Enabled = true;
        if (folders.Count == 0 || cats.Count == 0) SetPhase(Phase.Unavailable, "The Armory has no folders or categories to send to yet.", Palette.Red);
        else SetPhase(Phase.Ready, "Pick a folder and a category, tick the terms, then Upload.", Palette.Muted);
      });
    } catch (Exception ex) {
      string m = ex.Message;
      Ui(delegate { SetPhase(Phase.Unavailable, m, Palette.Red); });
    }
  }

  void SetPhase(Phase p, string text, Color c) { phase = p; status = text; statusTone = Palette.ToneOf(c); UpdateButtons(); Invalidate(); }

  void UpdateButtons() {
    bool ready = phase == Phase.Ready || phase == Phase.Failed;
    go.Enabled = ready && folder.SelectedItem != null && category.SelectedItem != null && terms.Checked;
    go.Text = phase == Phase.Failed ? "Try again" : "Upload";
    go.Visible = phase != Phase.Done;
    cancel.Visible = phase == Phase.Uploading;
    close.Text = phase == Phase.Done ? "Done" : "Close";
    close.Enabled = phase != Phase.Uploading;
    folder.Enabled = category.Enabled = title.Enabled = terms.Enabled = ready;
    go.Invalidate(); cancel.Invalidate(); close.Invalidate();
  }

  void Start() {
    var f = folder.SelectedItem as NamedItem; var c = category.SelectedItem as NamedItem;
    if (f == null || c == null || !terms.Checked) return;
    job = new UploadJob { Client = client, File = file, FolderId = f.Id, CategoryId = c.Id, Title = title.Text.Trim(), DurationSeconds = duration };
    sent = 0; total = new FileInfo(file).Length; startedAt = DateTime.UtcNow;
    var mine = job;
    long lastPaint = 0;
    job.Progress += delegate(long s, long t) {
      sent = s; total = t;
      long now = Environment.TickCount;
      if (now - lastPaint > 100 || s >= t) { lastPaint = now; Ui(delegate { Invalidate(); }); }
    };
    SetPhase(Phase.Uploading, "Uploading…", Palette.Soft);
    var th = new Thread(delegate() {
      try {
        var r = mine.Run();
        Ui(delegate { Receipt = r; sent = total; SetPhase(Phase.Done, "Uploaded. The media team will find it in the Armory (kept for 60 days).", Palette.Accent); });
      } catch (OperationCanceledException) {
        Ui(delegate { sent = 0; SetPhase(Phase.Failed, "Cancelled. Nothing was kept at the Armory.", Palette.Muted); });
      } catch (Exception ex) {
        string m = ex.Message;
        Ui(delegate { sent = 0; SetPhase(Phase.Failed, m, Palette.Red); });
      }
    });
    th.IsBackground = true; th.Name = "armory upload"; th.Start();
  }

  static string Megabytes(long b) { return b >= 1073741824L ? (b / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB" : (b / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB"; }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    int w = ClientSize.Width, side = P(32);
    using (var b = new SolidBrush(Palette.Bg)) g.FillRectangle(b, 0, 0, w, ClientSize.Height);
    using (var b = new SolidBrush(Palette.Band)) g.FillRectangle(b, 0, 0, w, P(84));
    using (var b = new SolidBrush(Palette.Accent)) g.FillRectangle(b, 0, P(82), w, P(2));
    using (var b = new SolidBrush(Palette.Fg)) g.DrawString("Upload to the Armory", Fonts.Get(Fonts.UiBold, P(26)), b, side, P(14));
    string fi = Path.GetFileName(file);
    long size = 0; try { size = new FileInfo(file).Length; } catch (Exception) { }
    using (var f = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
    using (var b = new SolidBrush(Palette.Soft)) g.DrawString(fi + "   ·   " + Megabytes(size) + (duration.HasValue ? "   ·   " + TimeSpan.FromSeconds(duration.Value).ToString(@"h\:mm\:ss") : ""), Fonts.Get(Fonts.Mono, P(13)), b, new RectangleF(side, P(52), w - 2 * side, P(24)), f);

    Label(g, "FOLDER", side, P(140)); Label(g, "CATEGORY", side, P(210)); Label(g, "TITLE (OPTIONAL)", side, P(280));
    using (var f = new StringFormat())
    using (var b = new SolidBrush(Palette.Soft)) g.DrawString(Terms, Fonts.Get(Fonts.Ui, P(14)), b, new RectangleF(side + P(36), P(362), w - 2 * side - P(36), P(70)), f);

    // progress
    float py = P(446), ph = P(14);
    using (var b = new SolidBrush(Palette.Track)) g.FillRectangle(b, side, py, w - 2 * side, ph);
    double frac = total > 0 ? Math.Min(1.0, (double)sent / total) : 0;
    using (var b = new SolidBrush(phase == Phase.Failed ? Palette.Red : Palette.Accent)) g.FillRectangle(b, side, py, (float)((w - 2 * side) * frac), ph);
    string detail = "";
    if (phase == Phase.Uploading || phase == Phase.Done) {
      double secs = Math.Max(0.5, (DateTime.UtcNow - startedAt).TotalSeconds), rate = sent / secs;
      detail = (frac * 100).ToString("0", CultureInfo.InvariantCulture) + "%   ·   " + Megabytes(sent) + " of " + Megabytes(total) + (phase == Phase.Uploading ? "   ·   " + (rate / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s" : "");
    }
    using (var b = new SolidBrush(Palette.Dim)) g.DrawString(detail, Fonts.Get(Fonts.Mono, P(12)), b, side, py + P(18));
    using (var f = new StringFormat { Trimming = StringTrimming.EllipsisWord })
    using (var b = new SolidBrush(Palette.OfTone(statusTone))) g.DrawString(status, Fonts.Get(Fonts.Ui, P(15)), b, new RectangleF(side, py + P(40), w - 2 * side, P(26)), f);
  }

  static void Label(Graphics g, string text, float x, float y) {
    using (var b = new SolidBrush(Palette.Dim)) g.DrawString(text, Fonts.Get(Fonts.Mono, 12), b, x, y, StringFormat.GenericTypographic);
  }

  // ---------------------------------------------------------------------------------------------- for the tests
  public Phase CurrentPhase { get { return phase; } }
  public string StatusText { get { return status; } }
  public int FolderCount { get { return folder.Items.Count; } }
  public bool GoEnabled { get { return go.Enabled; } }
  public void Fill(int folderIndex, int categoryIndex, string titleText, bool accept) {
    if (folderIndex >= 0 && folderIndex < folder.Items.Count) folder.SelectedIndex = folderIndex;
    if (categoryIndex >= 0 && categoryIndex < category.Items.Count) category.SelectedIndex = categoryIndex;
    title.Text = titleText; terms.Checked = accept;
  }
  public void PressUpload() { go.Press(); }
  public void PressCancel() { cancel.Press(); }
}
