using System;
using System.Globalization;
using System.Windows.Data;

namespace CyaNotch.Views;

/// <summary>Width of a battery bar: fraction (0–1) times the track's width.</summary>
public sealed class FractionWidth : IMultiValueConverter
{
    public static readonly FractionWidth Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var f = values.Length > 0 && values[0] is double d ? Math.Clamp(d, 0, 1) : 0;
        var w = values.Length > 1 && values[1] is double t && !double.IsNaN(t) ? t : 0;
        return f * w;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
