// SPDX-License-Identifier: GPL-2.0-or-later
// The Bifrost apps' colours: five theme presets that set every interface colour, and one adjustable accent on top. A port of Bifrost Scribe's
// src/shared/theme.ts (itself Studio's colour maths and Planner's presets), token for token, so a given theme and accent look identical in
// Scribe and here. Pure (no drawing, no windows): the tests check the maths. The Recorder's own icon is never themed. C# 5.
using System;
using System.Collections.Generic;
using System.Globalization;

class ThemeDef {
  public string Id, Name;
  public bool Dark;
  public int Hue;                  // the theme's own accent hue (0-359), used when the accent is "theme"
  public double[] Sv;              // saturation and value of the accent; null = the defaults for dark or light surfaces
  public double[] Soft;            // alpha of the text colour for soft rules, frames and outlines; null = 0.06, 0.07, 0.14
  public string Bg, Surface, Card, Field, Rule, Fg, Muted, Dim, Faint, On, Off, Call;   // Scribe's twelve tokens, as #rrggbb
  public string Warn;              // the amber of Planner's presets (Scribe has no warning token): "Falling behind"
}

// The accent: "theme" (the preset's own hue), a hue "0" to "359", "white" or "black". Kept as text so it saves and compares simply.
static class Theming {
  public const string DefaultThemeId = "console";
  public static readonly object[] AccentSwatches = { 32, 0, 120, 190, 225, 280, "white", "black" };   // Studio's, as in Scribe's ACCENT_SWATCHES

  // Scribe's BUILTIN_THEMES, in its order, with its token values.
  public static readonly ThemeDef[] Themes = {
    new ThemeDef { Id = "console", Name = "Console", Dark = true, Hue = 32,
      Bg = "#0b0d10", Surface = "#12161b", Card = "#171c22", Field = "#0e1216", Rule = "#252c34", Fg = "#e6edf2", Muted = "#a3afba", Dim = "#6c7884", Faint = "#414b57", On = "#3fbf5a", Off = "#e0463c", Call = "#7fb2e5", Warn = "#e9a23b" },
    new ThemeDef { Id = "midnight", Name = "Midnight", Dark = true, Hue = 225,
      Bg = "#0a0e19", Surface = "#10162a", Card = "#151c33", Field = "#0b1121", Rule = "#222b47", Fg = "#dfe4f5", Muted = "#a2aac9", Dim = "#6d769b", Faint = "#3b4466", On = "#5bd6a0", Off = "#ff6b81", Call = "#c3a6ff", Warn = "#f5b84a" },
    new ThemeDef { Id = "od-green", Name = "OD green", Dark = true, Hue = 48,
      Bg = "#0f110c", Surface = "#161a12", Card = "#1c2117", Field = "#11140e", Rule = "#2c3324", Fg = "#e3e6d8", Muted = "#aab096", Dim = "#767d64", Faint = "#454b39", On = "#8fc46a", Off = "#e0644f", Call = "#a9c4d6", Warn = "#e3a33b" },
    new ThemeDef { Id = "paper", Name = "Paper", Dark = false, Hue = 20,
      Bg = "#e9e6df", Surface = "#f5f3ee", Card = "#ffffff", Field = "#ffffff", Rule = "#d2cdc2", Fg = "#1f2328", Muted = "#4b5259", Dim = "#6b7178", Faint = "#a3a7ab", On = "#2e7d4f", Off = "#c62828", Call = "#2f6aa3", Warn = "#b45309" },
    new ThemeDef { Id = "high-contrast", Name = "High contrast", Dark = true, Hue = 52, Sv = new[] { 1.0, 1.0 }, Soft = new[] { 0.35, 0.6, 0.8 },
      Bg = "#000000", Surface = "#000000", Card = "#0d0d0d", Field = "#000000", Rule = "#ffffff", Fg = "#ffffff", Muted = "#e6e6e6", Dim = "#cccccc", Faint = "#9a9a9a", On = "#55ff88", Off = "#ff5555", Call = "#7ad0ff", Warn = "#ffb000" }
  };

  // The theme with this id, or Console.
  public static ThemeDef Find(string id) {
    foreach (var t in Themes) if (t.Id == id) return t;
    return Themes[0];
  }

  public static bool Known(string id) { foreach (var t in Themes) if (t.Id == id) return true; return false; }

  // JavaScript's Math.round (halves go up), which C#'s Math.Round (halves to even) is not.
  static int Round(double x) { return (int)Math.Floor(x + 0.5); }

  // Studio's hsvToRgb, as Scribe has it.
  public static int[] HsvToRgb(double h, double s, double v) {
    Func<double, int> f = delegate(double n) {
      double k = (n + h / 60.0) % 6.0;
      return Round((v - v * s * Math.Max(0, Math.Min(Math.Min(k, 4 - k), 1))) * 255);
    };
    return new[] { f(5), f(3), f(1) };
  }

  public static int[] HexToRgb(string hex) {
    return new[] { Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16) };
  }

  // Scribe's normalizeAccent for a saved value: "theme" | "white" | "black" | a hue (a number, or digits) wrapped into 0-359; anything else is "theme".
  public static string NormalizeAccent(object raw) {
    string s = raw as string;
    if (s != null) {
      s = s.Trim().ToLowerInvariant();
      if (s == "theme" || s == "white" || s == "black") return s;
      double d;
      if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return HueText(d);
      return "theme";
    }
    if (raw is int || raw is long || raw is double || raw is decimal || raw is float) {
      try { return HueText(Convert.ToDouble(raw, CultureInfo.InvariantCulture)); } catch (Exception) { }
    }
    return "theme";
  }

  static string HueText(double d) {
    if (double.IsNaN(d) || double.IsInfinity(d)) return "theme";
    int h = ((Round(d) % 360) + 360) % 360;
    return h.ToString(CultureInfo.InvariantCulture);
  }

  // Scribe's accentRgb.
  public static int[] AccentRgb(ThemeDef theme, string accent) {
    if (accent == "white") return new[] { 220, 226, 232 };
    if (accent == "black") return new[] { 120, 128, 138 };
    double s = theme.Sv != null ? theme.Sv[0] : theme.Dark ? 0.8 : 0.85, v = theme.Sv != null ? theme.Sv[1] : theme.Dark ? 0.85 : 0.72;
    int hue = theme.Hue;
    int parsed;
    if (accent != "theme" && int.TryParse(accent, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) hue = parsed;
    return HsvToRgb(hue, s, v);
  }

  static double Luminance(int[] rgb) {
    Func<int, double> c = delegate(int x) { double v = x / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); };
    return 0.2126 * c(rgb[0]) + 0.7152 * c(rgb[1]) + 0.0722 * c(rgb[2]);
  }

  // WCAG contrast ratio, 1 to 21.
  public static double Contrast(int[] a, int[] b) {
    double la = Luminance(a), lb = Luminance(b);
    return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
  }

  public const string DarkText = "#0b0d10", LightText = "#ffffff";

  // The text colour on an accent-coloured button: dark or white, whichever reads better (Scribe's --on-accent).
  public static string OnAccent(int[] accentRgb) {
    return Contrast(accentRgb, HexToRgb(DarkText)) >= Contrast(accentRgb, HexToRgb(LightText)) ? DarkText : LightText;
  }

  // Scribe's --lift: toward white on dark themes (Studio's hover text), toward black on light ones.
  public static int[] Lift(ThemeDef theme, int[] rgb) {
    var r = new int[3];
    for (int i = 0; i < 3; i++) r[i] = Round(theme.Dark ? rgb[i] + (255 - rgb[i]) * 0.45 : rgb[i] * 0.62);
    return r;
  }

  // Alphas of the text colour for soft rules, frames and outlines (Scribe's --rule-soft, --frame, --outline).
  public static double[] SoftAlphas(ThemeDef theme) { return theme.Soft ?? new[] { 0.06, 0.07, 0.14 }; }
}
