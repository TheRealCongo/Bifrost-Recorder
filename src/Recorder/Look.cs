// The Recorder's look: the Bifrost Suite console palette and fonts (Bahnschrift and Cascadia Mono, which Windows ships), with the
// Recorder's own accent, lime on olive. C# 5 for Windows' own compiler.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

static class Palette {
  public static readonly Color Bg = Hex("#0b0d10"), Surface = Hex("#12161b"), Field = Hex("#0e1216"), Rule = Hex("#252c34"), Edge = Hex("#2f3742"),
    Fg = Hex("#e6edf2"), Soft = Hex("#c3ccd5"), Muted = Hex("#a3afba"), Dim = Hex("#8b97a3"), Track = Hex("#2b3139"),
    Accent = Hex("#8FD94F"), Olive = Hex("#2D3A24"), OnAccent = Hex("#0b0d10"), Red = Hex("#e0463c"), Green = Hex("#3fbf5a");

  public static Color Hex(string hex) {
    hex = hex.TrimStart('#');
    return Color.FromArgb(Convert.ToInt32(hex.Substring(0, 2), 16), Convert.ToInt32(hex.Substring(2, 2), 16), Convert.ToInt32(hex.Substring(4, 2), 16));
  }

  public static Color Alpha(Color c, double a) { return Color.FromArgb((int)Math.Round(a * 255), c); }
  public static Color Over(Color under, Color over, double a) {
    return Color.FromArgb((int)(under.R + (over.R - under.R) * a), (int)(under.G + (over.G - under.G) * a), (int)(under.B + (over.B - under.B) * a));
  }
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

// A flat rounded button in the console style: a filled one for the main action, red for Stop, an outline for the rest.
class PillButton : Control {
  public enum Kind { Primary, Danger, Ghost }
  public Kind Look = Kind.Primary;
  public float Scale = 1f;
  bool hover;

  public PillButton() {
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    Cursor = Cursors.Hand;
    TabStop = true;
  }

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
    g.Clear(Parent != null ? Parent.BackColor : Palette.Bg);
    var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
    Color fill, line, text;
    if (!Enabled) { fill = Look == Kind.Ghost ? Color.Transparent : Palette.Track; line = Palette.Edge; text = Palette.Dim; }
    else if (Look == Kind.Primary) { fill = hover ? Palette.Over(Palette.Accent, Color.White, 0.18) : Palette.Accent; line = fill; text = Palette.OnAccent; }
    else if (Look == Kind.Danger) { fill = hover ? Palette.Over(Palette.Red, Color.White, 0.15) : Palette.Red; line = fill; text = Color.White; }
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

  static GraphicsPath Round(RectangleF r, float radius) {
    var p = new GraphicsPath(); float d = Math.Max(1, radius * 2);
    p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
  }
}
