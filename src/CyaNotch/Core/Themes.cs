using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace CyaNotch.Core;

/// <summary>How the island lets the desktop show through.</summary>
public enum Backdrop { Solid, Glass, Acrylic }

public sealed record ThemeDef(
    string Name,
    Color Surface,       // island background
    Color SurfaceEdge,   // thin outline
    Color Card,          // tiles inside the expanded island
    Color Text,
    Color SubText,
    Color Accent,
    Color AccentText,
    bool Glossy,         // soft top sheen
    Backdrop Backdrop = Backdrop.Solid,
    ThemeTouch Touch = ThemeTouch.None)
{
    public bool SeeThrough => Backdrop != Backdrop.Solid;
    public bool IsDark => Text.R + Text.G + Text.B > 3 * 128;
}

/// <summary>Small extra flourish a soft theme adds to the music progress line.</summary>
public enum ThemeTouch { None, Hearts, Sparkles, Petals }

/// <summary>Accent colours offered in Settings. Name, colour, and the text colour used on top of it.</summary>
public sealed record AccentDef(string Name, Color Color, Color OnColor);

/// <summary>Original colour themes. Applied as app-wide brush resources.</summary>
public static class Themes
{
    private static readonly Color DarkText = C(0xFF, 0xF4, 0xF5, 0xF8), DarkSub = C(0xFF, 0x9C, 0xA0, 0xAB);

    public static readonly IReadOnlyList<ThemeDef> All = new[]
    {
        new ThemeDef("Black",       C(0xFF, 0x0E, 0x0F, 0x12), C(0x26, 0xFF, 0xFF, 0xFF), C(0xFF, 0x1B, 0x1C, 0x21),
                     DarkText, DarkSub, C(0xFF, 0xF2, 0xF2, 0xF2), C(0xFF, 0x10, 0x10, 0x10), false),
        new ThemeDef("Pure black",  C(0xFF, 0x00, 0x00, 0x00), C(0x00, 0x00, 0x00, 0x00), C(0xFF, 0x12, 0x12, 0x14),
                     C(0xFF, 0xFF, 0xFF, 0xFF), C(0xFF, 0x8E, 0x8E, 0x93), C(0xFF, 0xF2, 0xF2, 0xF2), C(0xFF, 0x10, 0x10, 0x10), false),
        new ThemeDef("Pure white",  C(0xFF, 0xFF, 0xFF, 0xFF), C(0x22, 0x00, 0x00, 0x00), C(0xFF, 0xF2, 0xF3, 0xF5),
                     C(0xFF, 0x14, 0x15, 0x18), C(0xFF, 0x6B, 0x6F, 0x78), C(0xFF, 0x14, 0x15, 0x18), C(0xFF, 0xFF, 0xFF, 0xFF), false),
        new ThemeDef("Graphite",    C(0xFF, 0x2A, 0x2C, 0x31), C(0x30, 0xFF, 0xFF, 0xFF), C(0xFF, 0x36, 0x39, 0x3F),
                     C(0xFF, 0xEC, 0xED, 0xEF), C(0xFF, 0xA3, 0xA6, 0xAD), C(0xFF, 0xF2, 0xF2, 0xF2), C(0xFF, 0x10, 0x10, 0x10), false),
        new ThemeDef("Glass",       C(0x70, 0x12, 0x15, 0x1C), C(0x50, 0xFF, 0xFF, 0xFF), C(0x1C, 0xFF, 0xFF, 0xFF),
                     DarkText, DarkSub, C(0xFF, 0xF2, 0xF2, 0xF2), C(0xFF, 0x10, 0x10, 0x10), true, Backdrop.Glass),
        new ThemeDef("Acrylic",     C(0x40, 0x20, 0x22, 0x28), C(0x38, 0xFF, 0xFF, 0xFF), C(0x22, 0xFF, 0xFF, 0xFF),
                     DarkText, DarkSub, C(0xFF, 0xF2, 0xF2, 0xF2), C(0xFF, 0x10, 0x10, 0x10), false, Backdrop.Acrylic),
        new ThemeDef("Rosé",        C(0xFF, 0xF8, 0xE4, 0xE7), C(0x40, 0xD9, 0x67, 0x7F), C(0xFF, 0xFF, 0xF1, 0xF3),
                     C(0xFF, 0x3D, 0x1F, 0x27), C(0xFF, 0x9A, 0x6B, 0x76), C(0xFF, 0xD9, 0x5B, 0x78), C(0xFF, 0xFF, 0xFF, 0xFF), true, Backdrop.Solid, ThemeTouch.Hearts),
        new ThemeDef("Lavender",    C(0xFF, 0xEB, 0xE6, 0xF8), C(0x40, 0x8B, 0x6F, 0xD6), C(0xFF, 0xF5, 0xF2, 0xFD),
                     C(0xFF, 0x2A, 0x22, 0x40), C(0xFF, 0x7C, 0x72, 0x9A), C(0xFF, 0x86, 0x66, 0xD8), C(0xFF, 0xFF, 0xFF, 0xFF), true, Backdrop.Solid, ThemeTouch.Sparkles),
        new ThemeDef("Peach",       C(0xFF, 0xFF, 0xEA, 0xDC), C(0x40, 0xE8, 0x76, 0x3A), C(0xFF, 0xFF, 0xF5, 0xEE),
                     C(0xFF, 0x3A, 0x22, 0x18), C(0xFF, 0x8A, 0x6A, 0x5C), C(0xFF, 0xE8, 0x6A, 0x3A), C(0xFF, 0xFF, 0xFF, 0xFF), true),
        new ThemeDef("Sakura",      C(0xFF, 0xFC, 0xE8, 0xF0), C(0x40, 0xE8, 0x5D, 0x9A), C(0xFF, 0xFF, 0xF3, 0xF7),
                     C(0xFF, 0x40, 0x1C, 0x2E), C(0xFF, 0x9C, 0x6B, 0x82), C(0xFF, 0xE0, 0x4F, 0x8F), C(0xFF, 0xFF, 0xFF, 0xFF), true, Backdrop.Solid, ThemeTouch.Petals),
        new ThemeDef("Candy glass", C(0x80, 0x3A, 0x14, 0x30), C(0x60, 0xFF, 0xB3, 0xDA), C(0x26, 0xFF, 0xD6, 0xEC),
                     C(0xFF, 0xFF, 0xF1, 0xF8), C(0xFF, 0xE6, 0xA8, 0xC8), C(0xFF, 0xFF, 0x8A, 0xC8), C(0xFF, 0x2A, 0x0B, 0x1C), true, Backdrop.Glass, ThemeTouch.Sparkles),
    };

    public static readonly IReadOnlyList<AccentDef> Accents = new[]
    {
        new AccentDef("White",  C(0xFF, 0xF2, 0xF2, 0xF2), C(0xFF, 0x10, 0x10, 0x10)),
        new AccentDef("Teal",   C(0xFF, 0x2B, 0xB3, 0xC0), C(0xFF, 0x05, 0x16, 0x18)),
        new AccentDef("Blue",   C(0xFF, 0x3B, 0x8E, 0xFF), C(0xFF, 0xFF, 0xFF, 0xFF)),
        new AccentDef("Purple", C(0xFF, 0xA8, 0x5C, 0xF7), C(0xFF, 0xFF, 0xFF, 0xFF)),
        new AccentDef("Pink",   C(0xFF, 0xF4, 0x3F, 0x7A), C(0xFF, 0xFF, 0xFF, 0xFF)),
        new AccentDef("Orange", C(0xFF, 0xF5, 0x9E, 0x0B), C(0xFF, 0x1A, 0x10, 0x00)),
        new AccentDef("Green",  C(0xFF, 0x2E, 0xCC, 0x5E), C(0xFF, 0x04, 0x1A, 0x0A)),
        new AccentDef("Sky",    C(0xFF, 0x6C, 0xCB, 0xF5), C(0xFF, 0x05, 0x16, 0x20)),
    };

    public static ThemeDef Current { get; private set; } = All[0];

    public static void Apply(string name)
    {
        var t = All[0];
        foreach (var x in All) if (x.Name == name) t = x;
        // Settings picks an accent; "Theme" keeps the theme's own colour.
        foreach (var a in Accents)
            if (a.Name == Settings.Current.Accent) t = t with { Accent = a.Color, AccentText = a.OnColor };
        // Glass tint: how much of the desktop shows through see-through surfaces.
        if (t.SeeThrough)
            t = t with { Surface = Color.FromArgb((byte)Math.Clamp(Settings.Current.GlassTint * 255, 0x18, 0xE8), t.Surface.R, t.Surface.G, t.Surface.B) };
        Current = t;

        var r = Application.Current.Resources;
        r["SurfaceBrush"] = Brush(t.Surface);
        r["SurfaceEdgeBrush"] = Brush(t.SurfaceEdge);
        r["CardBrush"] = Brush(t.Card);
        r["TextBrush"] = Brush(t.Text);
        r["SubTextBrush"] = Brush(t.SubText);
        r["AccentBrush"] = Brush(t.Accent);
        r["AccentTextBrush"] = Brush(t.AccentText);
        r["HoverBrush"] = Brush(Color.FromArgb(0x22, t.Text.R, t.Text.G, t.Text.B));
        r["TrackBrush"] = Brush(Color.FromArgb(0x33, t.Text.R, t.Text.G, t.Text.B));

        // Glossy themes get a soft top sheen, like light catching frosted glass.
        var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(t.Glossy ? 0x30 : 0x00), 255, 255, 255), 0));
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 255, 255), 0.45));
        sheen.Freeze();
        r["SheenBrush"] = sheen;

        // Edge highlight: brighter along the top, fading down the sides (light catching glass).
        var edge = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        edge.GradientStops.Add(new GradientStop(Color.FromArgb((byte)Math.Min(255, t.SurfaceEdge.A * 2), t.SurfaceEdge.R, t.SurfaceEdge.G, t.SurfaceEdge.B), 0));
        edge.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(t.SurfaceEdge.A / 3), t.SurfaceEdge.R, t.SurfaceEdge.G, t.SurfaceEdge.B), 1));
        edge.Freeze();
        r["SurfaceEdgeBrush"] = edge;

        r["VizBrush"] = VizGradient(t.Accent, t.IsDark ? Dim(t.Accent, 0.7) : Dim(t.Accent, 0.8));
        ApplyMediaAccent(_mediaAccent);
    }

    private static Color? _mediaAccent;

    /// <summary>
    /// Colour taken from the song's artwork. It tints the visualizer and the progress line;
    /// null falls back to the theme's accent.
    /// </summary>
    public static void ApplyMediaAccent(Color? art)
    {
        _mediaAccent = art;
        var r = Application.Current.Resources;
        if (art is { } a && Settings.Current.ArtworkColors)
        {
            r["MediaAccentBrush"] = Brush(a);
            r["MediaVizBrush"] = VizGradient(a, Dim(a, 0.72));
        }
        else
        {
            r["MediaAccentBrush"] = r["AccentBrush"];
            r["MediaVizBrush"] = r["VizBrush"];
        }
    }

    /// <summary>Accent in the middle fading into a second hue at both ends.</summary>
    private static LinearGradientBrush VizGradient(Color main, Color second)
    {
        var viz = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        viz.GradientStops.Add(new GradientStop(second, 0));
        viz.GradientStops.Add(new GradientStop(main, 0.5));
        viz.GradientStops.Add(new GradientStop(second, 1));
        viz.Freeze();
        return viz;
    }

    private static Color Dim(Color c, double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    private static Color C(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
