using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CyaNotch.Core;

/// <summary>
/// Picks colours out of the song's artwork, so the visualizer and progress line match what's playing.
/// </summary>
public static class ArtColors
{
    /// <summary>
    /// The most vivid common colour in the image, brightened enough to read on a dark surface.
    /// Null when the artwork is mostly grey.
    /// </summary>
    public static Color? Accent(BitmapSource? art)
    {
        if (art == null) return null;
        try
        {
            const int size = 24;
            BitmapSource src = new FormatConvertedBitmap(art, PixelFormats.Bgra32, null, 0);
            src = new TransformedBitmap(src, new ScaleTransform((double)size / src.PixelWidth, (double)size / src.PixelHeight));
            var w = src.PixelWidth;
            var h = src.PixelHeight;
            var px = new byte[w * h * 4];
            src.CopyPixels(px, w * 4, 0);

            // 12 hue buckets, weighted by saturation and brightness.
            var weight = new double[12];
            var sumR = new double[12];
            var sumG = new double[12];
            var sumB = new double[12];
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                var (hue, sat, val) = ToHsv(r, g, b);
                if (sat < 0.25 || val < 0.25) continue;
                var k = (int)(hue / 30) % 12;
                var wgt = sat * sat * val;
                weight[k] += wgt;
                sumR[k] += r * wgt;
                sumG[k] += g * wgt;
                sumB[k] += b * wgt;
            }

            var best = -1;
            for (int k = 0; k < 12; k++)
                if (weight[k] > 0 && (best < 0 || weight[k] > weight[best])) best = k;
            if (best < 0 || weight[best] < w * h * 0.01) return null;

            var (hh, ss, _) = ToHsv(sumR[best] / weight[best], sumG[best] / weight[best], sumB[best] / weight[best]);
            // Keep the hue, soften the saturation a little, and make it bright: it's drawn on dark glass.
            return FromHsv(hh, Math.Clamp(ss, 0.35, 0.75), 0.95);
        }
        catch
        {
            return null;
        }
    }

    private static (double h, double s, double v) ToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
