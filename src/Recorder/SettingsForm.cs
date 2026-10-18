// The Settings window. Two pages: RECORDING, the five choices a recording is made with, in plain words with a short hint under each (a change
// applies to the next recording and is saved at once; greyed while a recording runs), and APPEARANCE, the theme presets and the accent, as in
// the other Bifrost apps (applied live). The caller's `changed` saves the prefs file and refreshes the header chip and the colours.
// Painted in the Bifrost console style (Look.cs), from the theme in use. C# 5.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Windows.Forms;

// The five theme presets as buttons: the theme's background, its panel colour and its own accent as a dot, and its name.
class ThemeStrip : Control {
  public string Selected = Theming.DefaultThemeId;
  public event Action<string> Picked;
  public float Scale = 1f;
  RectangleF[] cells = new RectangleF[0];

  public ThemeStrip() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Cursor = Cursors.Hand; }

  void Layout_() {
    int n = Theming.Themes.Length; float gap = 10 * Scale, w = (Width - gap * (n - 1)) / n;
    cells = new RectangleF[n];
    for (int i = 0; i < n; i++) cells[i] = new RectangleF(i * (w + gap), 0, w, Height - 1);
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    g.Clear(Palette.Bg);
    Layout_();
    for (int i = 0; i < cells.Length; i++) {
      var t = Theming.Themes[i]; var c = cells[i]; bool sel = t.Id == Selected;
      using (var path = PillButton.Round(new RectangleF(c.X + 0.5f, c.Y + 0.5f, c.Width - 1, c.Height - 1), 8 * Scale)) {
        using (var b = new SolidBrush(sel ? Palette.Alpha(Palette.Accent, 0.14) : Palette.Surface)) g.FillPath(b, path);
        using (var p = new Pen(sel ? Palette.Accent : Palette.Edge, sel ? 2f : 1f)) g.DrawPath(p, path);
      }
      float pad = 10 * Scale, ph = c.Height * 0.46f;   // the preview: bg and panel halves with the theme's own accent as a dot
      var pv = new RectangleF(c.X + pad, c.Y + pad, c.Width - 2 * pad, ph);
      using (var b = new SolidBrush(Palette.Hex(t.Bg))) g.FillRectangle(b, pv.X, pv.Y, pv.Width / 2, pv.Height);
      using (var b = new SolidBrush(Palette.Hex(t.Surface))) g.FillRectangle(b, pv.X + pv.Width / 2, pv.Y, pv.Width / 2, pv.Height);
      using (var p = new Pen(Palette.Edge)) g.DrawRectangle(p, pv.X, pv.Y, pv.Width, pv.Height);
      var rgb = Theming.AccentRgb(t, "theme");
      float d = pv.Height * 0.5f;
      using (var b = new SolidBrush(Color.FromArgb(rgb[0], rgb[1], rgb[2]))) g.FillEllipse(b, pv.X + pv.Width / 2 - d / 2, pv.Y + (pv.Height - d) / 2, d, d);
      using (var b = new SolidBrush(sel ? Palette.Fg : Palette.Soft))
      using (var f = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        g.DrawString(t.Name.ToUpperInvariant(), Fonts.Get(Fonts.Mono, 11 * Scale), b, new RectangleF(c.X, pv.Bottom + 6 * Scale, c.Width, c.Height - ph - pad), f);
    }
  }

  protected override void OnMouseClick(MouseEventArgs e) {
    base.OnMouseClick(e);
    Layout_();
    for (int i = 0; i < cells.Length; i++) if (cells[i].Contains(e.Location)) { Pick(Theming.Themes[i].Id); return; }
  }

  public void Pick(string id) { Selected = id; Invalidate(); var h = Picked; if (h != null) h(id); }
}

// "Theme's own" and the eight swatches, drawn in the colours the chosen theme gives them (Scribe's ACCENT_SWATCHES).
class AccentStrip : Control {
  public string Accent = "theme";
  public ThemeDef Theme = Theming.Themes[0];
  public event Action<string> Picked;
  public float Scale = 1f;
  RectangleF auto;
  RectangleF[] swatches = new RectangleF[0];

  public AccentStrip() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Cursor = Cursors.Hand; }

  void Layout_() {
    float h = Height - 1, gap = 8 * Scale;
    auto = new RectangleF(0, 0, 124 * Scale, h);
    swatches = new RectangleF[Theming.AccentSwatches.Length];
    float x = auto.Right + gap * 1.5f;
    for (int i = 0; i < swatches.Length; i++) { swatches[i] = new RectangleF(x, 0, h, h); x += h + gap; }
  }

  static string Spec(object a) { return a is int ? ((int)a).ToString(CultureInfo.InvariantCulture) : (string)a; }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    g.Clear(Palette.Bg);
    Layout_();
    bool selAuto = Accent == "theme";
    using (var path = PillButton.Round(new RectangleF(auto.X + 0.5f, auto.Y + 0.5f, auto.Width - 1, auto.Height - 1), auto.Height / 2))
    {
      if (selAuto) using (var b = new SolidBrush(Palette.Alpha(Palette.Accent, 0.16))) g.FillPath(b, path);
      using (var p = new Pen(selAuto ? Palette.Accent : Palette.Edge, selAuto ? 2f : 1f)) g.DrawPath(p, path);
    }
    using (var b = new SolidBrush(selAuto ? Palette.Fg : Palette.Soft))
    using (var f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
      g.DrawString("THEME'S OWN", Fonts.Get(Fonts.Mono, 11 * Scale), b, auto, f);
    for (int i = 0; i < swatches.Length; i++) {
      var rgb = Theming.AccentRgb(Theme, Spec(Theming.AccentSwatches[i]));
      bool sel = Accent == Spec(Theming.AccentSwatches[i]);
      var r = swatches[i];
      using (var b = new SolidBrush(Color.FromArgb(rgb[0], rgb[1], rgb[2]))) g.FillEllipse(b, r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6);
      using (var p = new Pen(sel ? Palette.Fg : Palette.Edge, sel ? 2.5f : 1f)) g.DrawEllipse(p, r.X + 1.5f, r.Y + 1.5f, r.Width - 3, r.Height - 3);
    }
  }

  protected override void OnMouseClick(MouseEventArgs e) {
    base.OnMouseClick(e);
    Layout_();
    if (auto.Contains(e.Location)) { Pick("theme"); return; }
    for (int i = 0; i < swatches.Length; i++) if (swatches[i].Contains(e.Location)) { Pick(Spec(Theming.AccentSwatches[i])); return; }
  }

  public void Pick(string spec) { Accent = spec; Invalidate(); var h = Picked; if (h != null) h(spec); }
}

// The hue slider, 0 to 359: the bar shows each hue as the chosen theme turns it into an accent.
class HueBar : Control {
  public int Hue;
  public ThemeDef Theme = Theming.Themes[0];
  public event Action<int> Changed;
  public float Scale = 1f;
  bool dragging;

  public HueBar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true); TabStop = true; Cursor = Cursors.Hand; }

  float Pad { get { return 12 * Scale; } }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
    g.Clear(Palette.Bg);
    float barH = 12 * Scale, y = (Height - barH) / 2, w = Width - 2 * Pad;
    using (var path = PillButton.Round(new RectangleF(Pad, y, w, barH), barH / 2))
    {
      g.SetClip(path);
      for (int x = 0; x < (int)w; x++) {
        var rgb = Theming.AccentRgb(Theme, ((int)Math.Round(x / (double)w * 359)).ToString(CultureInfo.InvariantCulture));
        using (var b = new SolidBrush(Color.FromArgb(rgb[0], rgb[1], rgb[2]))) g.FillRectangle(b, Pad + x, y, 1.5f, barH);
      }
      g.ResetClip();
      using (var p = new Pen(Palette.Edge)) g.DrawPath(p, path);
    }
    float tx = Pad + w * Hue / 359f, d = Height * 0.62f;
    var now = Theming.AccentRgb(Theme, Hue.ToString(CultureInfo.InvariantCulture));
    using (var b = new SolidBrush(Color.FromArgb(now[0], now[1], now[2]))) g.FillEllipse(b, tx - d / 2, (Height - d) / 2, d, d);
    using (var p = new Pen(Palette.Fg, 2f)) g.DrawEllipse(p, tx - d / 2, (Height - d) / 2, d, d);
  }

  void FromX(float x) {
    float w = Width - 2 * Pad;
    int h = (int)Math.Round(Math.Max(0, Math.Min(1, (x - Pad) / w)) * 359);
    Set(h);
  }

  public void Set(int h) { h = ((h % 360) + 360) % 360; if (h == Hue && !dragging) { } Hue = h; Invalidate(); var c = Changed; if (c != null) c(h); }

  protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); dragging = true; Focus(); FromX(e.X); }
  protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) FromX(e.X); }
  protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; }
  protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
  protected override void OnKeyDown(KeyEventArgs e) {
    if (e.KeyCode == Keys.Left) { Set(Math.Max(0, Hue - 1)); e.Handled = true; }
    else if (e.KeyCode == Keys.Right) { Set(Math.Min(359, Hue + 1)); e.Handled = true; }
    base.OnKeyDown(e);
  }
}

class SettingsForm : Form {
  readonly AppSettings cfg;
  readonly Func<bool> busy;
  readonly Action changed;
  readonly ComboBox[] combos = new ComboBox[5];
  readonly PillButton reset = new PillButton(), done = new PillButton();
  readonly ThemeStrip themes = new ThemeStrip();
  readonly AccentStrip accents = new AccentStrip();
  readonly HueBar hue = new HueBar();
  readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
  float S = 1f;
  bool filling;
  bool wasBusy;
  int page;   // 0 = recording, 1 = appearance
  RectangleF[] tabs = new RectangleF[2];
  static readonly string[] TabNames = { "RECORDING", "APPEARANCE" };

  public SettingsForm(AppSettings cfg, Func<bool> busy, Action changed) {
    this.cfg = cfg; this.busy = busy; this.changed = changed;
    Text = "Bifrost Recorder settings";
    FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    using (var g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiX / 96f;
    ClientSize = new Size(P(640), P(640));
    if (MainForm.Quiet) { StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); } else StartPosition = FormStartPosition.CenterParent;
    try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

    var fields = (SettingChoices.Field[])Enum.GetValues(typeof(SettingChoices.Field));
    filling = true;
    for (int i = 0; i < fields.Length; i++) {
      var field = fields[i];
      var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = Fonts.Get(Fonts.Ui, P(16)) };
      foreach (string l in SettingChoices.Labels(field)) c.Items.Add(l);
      c.SelectedIndex = SettingChoices.Get(cfg, field);
      c.SelectedIndexChanged += delegate { if (!filling) Chosen(field, c.SelectedIndex); };
      combos[i] = c; Controls.Add(c);
    }
    filling = false;
    reset.Scale = S; reset.Look = PillButton.Kind.Ghost; reset.Text = "Reset to defaults"; reset.Behind = delegate { return Palette.Bg; }; reset.Click += delegate { ResetAll(); };
    done.Scale = S; done.Text = "Done"; done.Behind = delegate { return Palette.Bg; }; done.Click += delegate { Close(); };
    Controls.Add(reset); Controls.Add(done);

    themes.Scale = S; themes.Selected = cfg.ThemeId; themes.Picked += delegate(string id) { cfg.ThemeId = id; ThemeChanged(); };
    accents.Scale = S; accents.Accent = cfg.Accent; accents.Picked += delegate(string a) { cfg.Accent = a; AccentChanged(); };
    hue.Scale = S; hue.Changed += delegate(int h) { cfg.Accent = h.ToString(CultureInfo.InvariantCulture); accents.Accent = cfg.Accent; accents.Invalidate(); if (changed != null) changed(); Invalidate(); };
    Controls.Add(themes); Controls.Add(accents); Controls.Add(hue);
    SyncAppearance();

    Palette.Changed += OnPalette;
    DoLayout();
    ApplyTheme();
    poll.Interval = 300; poll.Tick += delegate { Refresh_(); }; poll.Start();
    Refresh_();
  }

  int P(float v) { return (int)Math.Round(v * S); }

  const float TabsTop = 96, RowTop = 160, RowH = 88;

  void DoLayout() {
    int w = ClientSize.Width, side = P(32);
    for (int i = 0; i < combos.Length; i++) combos[i].SetBounds(side, P(RowTop + i * RowH + 22), w - 2 * side, P(32));
    themes.SetBounds(side, P(RowTop + 24), w - 2 * side, P(104));
    accents.SetBounds(side, P(RowTop + 168), w - 2 * side, P(36));
    hue.SetBounds(side, P(RowTop + 258), w - 2 * side - P(64), P(36));
    reset.SetBounds(side, ClientSize.Height - P(64), P(190), P(46));
    done.SetBounds(w - side - P(150), ClientSize.Height - P(64), P(150), P(46));
    ShowPage();
  }

  void ShowPage() {
    foreach (var c in combos) c.Visible = page == 0;
    reset.Visible = page == 0;
    themes.Visible = accents.Visible = hue.Visible = page == 1;
    Invalidate();
  }

  protected override CreateParams CreateParams {
    get { var cp = base.CreateParams; if (MainForm.Quiet) cp.ExStyle = (cp.ExStyle | 0x80 | 0x08000000) & ~0x40000; return cp; }
  }
  protected override bool ShowWithoutActivation { get { return MainForm.Quiet; } }

  protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Palette.StyleTitleBar(Handle); }

  protected override void OnFormClosed(FormClosedEventArgs e) { poll.Stop(); Palette.Changed -= OnPalette; base.OnFormClosed(e); }

  void OnPalette() { try { if (IsHandleCreated && !IsDisposed) BeginInvoke((MethodInvoker)delegate { ApplyTheme(); }); } catch (Exception) { } }

  // The colours of the windows' own controls (the custom ones paint from Palette each time).
  void ApplyTheme() {
    BackColor = Palette.Bg; ForeColor = Palette.Fg;
    foreach (var c in combos) { c.BackColor = Palette.Field; c.ForeColor = Palette.Fg; }
    accents.Theme = Palette.Theme; hue.Theme = Palette.Theme;
    if (IsHandleCreated) Palette.StyleTitleBar(Handle);
    Invalidate(true);
  }

  void SyncAppearance() {
    themes.Selected = cfg.ThemeId; accents.Accent = cfg.Accent;
    var t = Theming.Find(cfg.ThemeId);
    accents.Theme = t; hue.Theme = t;
    int h; hue.Hue = int.TryParse(cfg.Accent, NumberStyles.Integer, CultureInfo.InvariantCulture, out h) ? h : t.Hue;
  }

  void ThemeChanged() { if (changed != null) changed(); SyncAppearance(); Invalidate(true); }
  void AccentChanged() { if (changed != null) changed(); SyncAppearance(); Invalidate(true); }

  // Greyed while a recording runs.
  void Refresh_() {
    bool b = busy != null && busy();
    if (b == wasBusy && combos[0].Enabled == !b) return;
    wasBusy = b;
    foreach (var c in combos) c.Enabled = !b;
    reset.Enabled = !b;
    Invalidate();
  }

  void Chosen(SettingChoices.Field field, int index) {
    if (busy != null && busy()) return;
    SettingChoices.Set(cfg, field, index);
    if (changed != null) changed();
  }

  void ResetAll() {
    if (busy != null && busy()) return;
    SettingChoices.Defaults(cfg);
    filling = true;
    var fields = (SettingChoices.Field[])Enum.GetValues(typeof(SettingChoices.Field));
    for (int i = 0; i < fields.Length; i++) combos[i].SelectedIndex = SettingChoices.Get(cfg, fields[i]);
    filling = false;
    if (changed != null) changed();
    Invalidate();
  }

  protected override void OnMouseClick(MouseEventArgs e) {
    base.OnMouseClick(e);
    for (int i = 0; i < tabs.Length; i++) if (tabs[i].Contains(e.Location)) { page = i; ShowPage(); return; }
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    int w = ClientSize.Width, side = P(32);
    using (var b = new SolidBrush(Palette.Bg)) g.FillRectangle(b, 0, 0, w, ClientSize.Height);
    using (var b = new SolidBrush(Palette.Band)) g.FillRectangle(b, 0, 0, w, P(84));
    using (var b = new SolidBrush(Palette.Accent)) g.FillRectangle(b, 0, P(82), w, P(2));
    using (var b = new SolidBrush(Palette.Fg)) g.DrawString("Settings", Fonts.Get(Fonts.UiBold, P(26)), b, side, P(14));
    using (var b = new SolidBrush(Palette.Soft)) g.DrawString(page == 0 ? "Changes apply to the next recording and are saved at once." : "The look is applied at once and saved.", Fonts.Get(Fonts.Ui, P(15)), b, side + 2, P(52));

    // the two tabs
    float tx = side;
    for (int i = 0; i < 2; i++) {
      float tw = P(150), th = P(34);
      tabs[i] = new RectangleF(tx, P(TabsTop), tw, th);
      bool sel = i == page;
      using (var path = PillButton.Round(new RectangleF(tabs[i].X + 0.5f, tabs[i].Y + 0.5f, tw - 1, th - 1), th / 2))
      {
        if (sel) using (var b = new SolidBrush(Palette.Alpha(Palette.Accent, 0.16))) g.FillPath(b, path);
        using (var p = new Pen(sel ? Palette.Accent : Palette.Edge, sel ? 2f : 1f)) g.DrawPath(p, path);
      }
      using (var b = new SolidBrush(sel ? Palette.Fg : Palette.Soft))
      using (var f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        g.DrawString(TabNames[i], Fonts.Get(Fonts.Mono, P(12)), b, tabs[i], f);
      tx += tw + P(10);
    }

    if (page == 0) {
      bool isBusy = busy != null && busy();
      var fields = (SettingChoices.Field[])Enum.GetValues(typeof(SettingChoices.Field));
      for (int i = 0; i < fields.Length; i++) {
        float y = P(RowTop + i * RowH);
        using (var b = new SolidBrush(Palette.Dim)) g.DrawString(SettingChoices.Fields[i], Fonts.Get(Fonts.Mono, 12), b, side, y, StringFormat.GenericTypographic);
        using (var f = new StringFormat { Trimming = StringTrimming.EllipsisWord })
        using (var b = new SolidBrush(Palette.Muted)) g.DrawString(SettingChoices.Hint(fields[i]), Fonts.Get(Fonts.Ui, P(13)), b, new RectangleF(side, y + P(58), w - 2 * side, P(28)), f);
      }
      if (isBusy) using (var b = new SolidBrush(Palette.Amber)) g.DrawString("A recording is running: these can't change until it is saved.", Fonts.Get(Fonts.Ui, P(14)), b, side + P(206), ClientSize.Height - P(50));
    } else {
      using (var b = new SolidBrush(Palette.Dim)) {
        g.DrawString("THEME", Fonts.Get(Fonts.Mono, 12), b, side, P(RowTop), StringFormat.GenericTypographic);
        g.DrawString("ACCENT COLOUR", Fonts.Get(Fonts.Mono, 12), b, side, P(RowTop + 144), StringFormat.GenericTypographic);
        g.DrawString("HUE", Fonts.Get(Fonts.Mono, 12), b, side, P(RowTop + 234), StringFormat.GenericTypographic);
      }
      using (var b = new SolidBrush(Palette.Muted)) g.DrawString(hue.Hue.ToString(CultureInfo.InvariantCulture), Fonts.Get(Fonts.Mono, P(14)), b, w - side - P(52), P(RowTop + 266));
      using (var b = new SolidBrush(Palette.Muted)) g.DrawString("The app icon always keeps its own colours.", Fonts.Get(Fonts.Ui, P(14)), b, side, P(RowTop + 318));
    }
  }

  // ---------------------------------------------------------------------------------------------- for the tests
  public string ChosenLabel(SettingChoices.Field f) { return combos[(int)f].SelectedItem as string; }
  public bool ControlsEnabled { get { return combos[0].Enabled; } }
  public void Choose(SettingChoices.Field f, int index) { combos[(int)f].SelectedIndex = index; }
  public void PressReset() { reset.Press(); }
  public void RefreshNow() { Refresh_(); }
  public void ShowTab(int index) { page = index; ShowPage(); }
  public void PickTheme(string id) { themes.Pick(id); }
  public void PickAccent(string spec) { accents.Pick(spec); }
  public void SetHue(int h) { hue.Set(h); }
  public int HueShown { get { return hue.Hue; } }
}
