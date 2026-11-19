using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace CyaNotch.Core;

public sealed record ThemeDef(
    string Name,
    Color Surface,       // island background
    Color SurfaceEdge,   // thin outline
    Color Card,          // tiles inside the expanded island
    Color Text,
    Color SubText,
    Color Accent,
    Color AccentText,
    bool Glossy);

/// <summary>Original colour themes. Applied as app-wide brush resources.</summary>
public static class Themes
{
    public static readonly IReadOnlyList<ThemeDef> All = new[]
    {
        new ThemeDef("Glass",    C(0x80, 0x12, 0x15, 0x1C), C(0x50, 0xFF, 0xFF, 0xFF), C(0x1C, 0xFF, 0xFF, 0xFF),
                     C(0xFF, 0xF4, 0xF5, 0xF8), C(0xFF, 0x9C, 0xA0, 0xAB), C(0xFF, 0x40, 0xE0, 0xE8), C(0xFF, 0x0B, 0x12, 0x1C), true),
        new ThemeDef("Black",    C(0xFF, 0x00, 0x00, 0x00), C(0x00, 0x00, 0x00, 0x00), C(0xFF, 0x16, 0x16, 0x18),
                     C(0xFF, 0xFF, 0xFF, 0xFF), C(0xFF, 0x8E, 0x8E, 0x93), C(0xFF, 0xFF, 0xB0, 0x3B), C(0xFF, 0x1A, 0x10, 0x00), false),
        new ThemeDef("Graphite", C(0xFF, 0x2A, 0x2C, 0x31), C(0x30, 0xFF, 0xFF, 0xFF), C(0xFF, 0x36, 0x39, 0x3F),
                     C(0xFF, 0xEC, 0xED, 0xEF), C(0xFF, 0xA3, 0xA6, 0xAD), C(0xFF, 0x9B, 0xE3, 0xA8), C(0xFF, 0x0E, 0x22, 0x13), false),
        new ThemeDef("Dusk",     C(0x99, 0x2B, 0x1F, 0x3A), C(0x40, 0xE8, 0xC8, 0xFF), C(0x28, 0xFF, 0xFF, 0xFF),
                     C(0xFF, 0xF6, 0xEF, 0xFF), C(0xFF, 0xB9, 0xA9, 0xCF), C(0xFF, 0xFF, 0x9E, 0xC7), C(0xFF, 0x2A, 0x0B, 0x1C), true),
        new ThemeDef("Sage",     C(0xF2, 0xE9, 0xEE, 0xE4), C(0x30, 0x00, 0x00, 0x00), C(0xFF, 0xFA, 0xFC, 0xF7),
                     C(0xFF, 0x1E, 0x26, 0x1C), C(0xFF, 0x63, 0x6E, 0x5F), C(0xFF, 0x4A, 0x8C, 0x5C), C(0xFF, 0xFF, 0xFF, 0xFF), false),
        new ThemeDef("Peach",    C(0xF2, 0xFF, 0xEC, 0xE0), C(0x30, 0x00, 0x00, 0x00), C(0xFF, 0xFF, 0xF8, 0xF3),
                     C(0xFF, 0x3A, 0x22, 0x18), C(0xFF, 0x8A, 0x6A, 0x5C), C(0xFF, 0xE8, 0x6A, 0x3A), C(0xFF, 0xFF, 0xFF, 0xFF), false),
    };

    public static ThemeDef Current { get; private set; } = All[0];

    public static void Apply(string name)
    {
        var t = All[0];
        foreach (var x in All) if (x.Name == name) t = x;
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

        r["VizBrush"] = VizGradient(t.Accent, t.Glossy ? Color.FromRgb(0xA7, 0x8B, 0xFA) : Dim(t.Accent, 0.7));
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
