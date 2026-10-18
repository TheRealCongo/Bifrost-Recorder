// The Recorder's look: the Bifrost console style (Bahnschrift and Cascadia Mono, which Windows ships), drawn from the theme tokens and the
// accent chosen in Settings (Theme.cs, the same colours as Bifrost Scribe). Palette holds the colours in use; Palette.Apply changes them,
// and every window repaints from it. The app icon is not themed. C# 5 for Windows' own compiler.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

static class Palette {
  public static Color Bg, Surface, Card, Field, Rule, Edge, Fg, Soft, Muted, Dim, Faint, Track, Accent, AccentInk, AccentLift, OnAccent, Red, OnRed, Green, Amber, Band;
  public static ThemeDef Theme;
  public static string AccentSpec = "theme";
  public static bool Dark = true;

  // Windows (and anything that caches a colour) listen to this to restyle themselves.
  public static event Action Changed;

  static Palette() { Apply(Theming.DefaultThemeId, "theme"); }

  public static Color Hex(string hex) {
    hex = hex.TrimStart('#');
    return Color.FromArgb(Convert.ToInt32(hex.Substring(0, 2), 16), Convert.ToInt32(hex.Substring(2, 2), 16), Convert.ToInt32(hex.Substring(4, 2), 16));
  }

  public static Color Alpha(Color c, double a) { return Color.FromArgb((int)Math.Round(a * 255), c); }
  public static Color Over(Color under, Color over, double a) {
    return Color.FromArgb((int)(under.R + (over.R - under.R) * a), (int)(under.G + (over.G - under.G) * a), (int)(under.B + (over.B - under.B) * a));
  }
  static Color Rgb(int[] c) { return Color.FromArgb(c[0], c[1], c[2]); }

  // A status line keeps which colour it is (not the colour), so it follows the theme when the theme changes.
  public enum Tone { Muted, Soft, Fg, Accent, Red, Amber }
  public static Tone ToneOf(Color c) { return c == Red ? Tone.Red : c == Amber ? Tone.Amber : c == Accent ? Tone.Accent : c == Soft ? Tone.Soft : c == Fg ? Tone.Fg : Tone.Muted; }
  public static Color OfTone(Tone t) { switch (t) { case Tone.Red: return Red; case Tone.Amber: return Amber; case Tone.Accent: return AccentInk; case Tone.Soft: return Soft; case Tone.Fg: return Fg; default: return Muted; } }

  // The text colour that reads on a given fill (Scribe's contrast rule for --on-accent).
  public static Color TextOn(Color fill) { return Hex(Theming.OnAccent(new int[] { fill.R, fill.G, fill.B })); }

  // Sets the theme and the accent ("theme", a hue, "white" or "black") and tells the windows.
  public static void Apply(string themeId, string accent) {
    var t = Theming.Find(themeId);
    Theme = t; Dark = t.Dark; AccentSpec = Theming.NormalizeAccent(accent);
    Bg = Hex(t.Bg); Surface = Hex(t.Surface); Card = Hex(t.Card); Field = Hex(t.Field); Rule = Hex(t.Rule);
    Fg = Hex(t.Fg); Muted = Hex(t.Muted); Dim = Hex(t.Dim); Faint = Hex(t.Faint);
    Green = Hex(t.On); Red = Hex(t.Off); Amber = Hex(t.Warn);
    var alphas = Theming.SoftAlphas(t);
    Edge = Over(Bg, Fg, alphas[2]);                 // Scribe's --outline over the background
    Track = Rule;
    Soft = Over(Muted, Fg, 0.5);
    var rgb = Theming.AccentRgb(t, AccentSpec);
    Accent = Rgb(rgb); AccentLift = Rgb(Theming.Lift(t, rgb)); OnAccent = Hex(Theming.OnAccent(rgb));
    OnRed = TextOn(Red);
    Band = Over(Surface, Accent, Dark ? 0.20 : 0.16);   // the header's tint: the surface with the accent in it
    // The accent as TEXT or a thin line: itself unless it can't be read on the panels (the white accent on Paper), then the text colour.
    var onSurface = new int[] { Surface.R, Surface.G, Surface.B }; var onBand = new int[] { Band.R, Band.G, Band.B };
    AccentInk = Theming.Contrast(rgb, onSurface) >= 3.0 && Theming.Contrast(rgb, onBand) >= 3.0 ? Accent : Fg;
    var h = Changed; if (h != null) h();
  }

  // The title bar of a window in the theme (Windows 10 1809+ for the dark flag, Windows 11 for the colours).
  public static void StyleTitleBar(IntPtr handle) {
    int dark = Dark ? 1 : 0, caption = Colorref(Band), text = Colorref(Fg);
    Native.DwmSetWindowAttribute(handle, 20, ref dark, 4);
    Native.DwmSetWindowAttribute(handle, 35, ref caption, 4);
    Native.DwmSetWindowAttribute(handle, 36, ref text, 4);
  }

  static int Colorref(Color c) { return (c.B << 16) | (c.G << 8) | c.R; }   // 0x00BBGGRR
}

static class Fonts {
  static readonly HashSet<string> installed = Installed();
  static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();

  static HashSet<string> Installed() {
    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    using (var fc = new InstalledFontCollection()) foreach (var f in fc.Families) set.Add(f.Name);
    return set;
  }

  static string Pick(params string[] names) { foreach (var n in names) if (installed.Contains(n)) return n; return "Segoe UI"; }

  public static readonly string Ui = Pick("Bahnschrift SemiCondensed", "Segoe UI"), UiBold = Pick("Bahnschrift SemiBold SemiConden", "Segoe UI Semibold"),
    Mono = Pick("Cascadia Mono", "Consolas");

  // Sizes are in pixels, already scaled by the caller.
  public static Font Get(string family, float px) {
    string key = family + "|" + px.ToString("0.0");
    lock (cache) {
      Font f;
      if (!cache.TryGetValue(key, out f)) { f = new Font(family, px, FontStyle.Regular, GraphicsUnit.Pixel); cache[key] = f; }
      return f;
    }
  }
}

// A flat rounded button in the console style: a filled one for the main action, red for Stop, an outline for the rest. Behind says what colour
// is behind it (a function, so it follows the theme).
class PillButton : Control {
  public enum Kind { Primary, Danger, Ghost }
  public Kind Look = Kind.Primary;
  public float Scale = 1f;
  public Func<Color> Behind;
  bool hover;

  public PillButton() {
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    Cursor = Cursors.Hand;
    TabStop = true;
    Palette.Changed += Restyle;
  }

  protected override void Dispose(bool disposing) { if (disposing) Palette.Changed -= Restyle; base.Dispose(disposing); }
  void Restyle() { try { if (IsHandleCreated && !IsDisposed) BeginInvoke((MethodInvoker)delegate { Invalidate(); }); } catch (Exception) { } }

  public override string Text { get { return base.Text; } set { base.Text = value; Invalidate(); } }
  public void Press() { if (Enabled) OnClick(EventArgs.Empty); }

  protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
  protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
  protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
  protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
  protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
  protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }
  protected override void OnKeyDown(KeyEventArgs e) {
    if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Press(); e.Handled = true; }
    base.OnKeyDown(e);
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    g.Clear(Behind != null ? Behind() : Palette.Bg);
    var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
    // a hover lifts a filled button toward white on dark themes and toward black on light ones (Scribe's --lift)
    Color toward = Palette.Dark ? Color.White : Color.Black;
    Color fill, line, text;
    if (!Enabled) { fill = Look == Kind.Ghost ? Color.Transparent : Palette.Track; line = Palette.Edge; text = Palette.Dim; }
    else if (Look == Kind.Primary) { fill = hover ? Palette.Over(Palette.Accent, toward, 0.18) : Palette.Accent; line = fill; text = Palette.OnAccent; }
    else if (Look == Kind.Danger) { fill = hover ? Palette.Over(Palette.Red, toward, 0.15) : Palette.Red; line = fill; text = Palette.OnRed; }
    else { fill = hover ? Palette.Alpha(Palette.Accent, 0.16) : Color.Transparent; line = hover ? Palette.Accent : Palette.Edge; text = hover ? Palette.Fg : Palette.Soft; }
    using (var path = Round(r, Height / 2f - 1)) {
      if (fill != Color.Transparent) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
      using (var p = new Pen(line, 1f)) g.DrawPath(p, path);
      if (Focused && Enabled) using (var p = new Pen(Palette.Fg, 1.5f)) { p.DashStyle = DashStyle.Dot; using (var inner = Round(new RectangleF(3, 3, Width - 7, Height - 7), Height / 2f - 4)) g.DrawPath(p, inner); }
    }
    using (var b = new SolidBrush(text))
    using (var f = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
      g.DrawString(Text.ToUpperInvariant(), Fonts.Get(Fonts.UiBold, (Look == Kind.Ghost ? 14f : 17f) * Scale), b, new RectangleF(0, 0, Width, Height + 1), f);
  }

  public static GraphicsPath Round(RectangleF r, float radius) {
    var p = new GraphicsPath(); float d = Math.Max(1, radius * 2);
    p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
  }
}
