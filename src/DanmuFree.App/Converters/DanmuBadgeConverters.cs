using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using DanmuFree.Core.Models;

namespace DanmuFree.App.Converters;

public sealed class BadgeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var kind = parameter as string;
        if (value is BiliUserIdentity identity)
            return Brush(kind == "role"
                ? identity.IsAnchor ? 0xB83E72 : 0x22765E
                : identity.GuardLevel switch { 1 => 0x94621C, 2 => 0x7050AE, _ => 0x286FAD });
        if (value is not FansMedal medal) return Brushes.Transparent;
        int fallback = medal.Level switch
        {
            >= 30 => 0xBC617E, >= 21 => 0x966BC4, >= 11 => 0x468EB2, _ => 0x418C72,
        };
        int color = medal.Color ?? fallback;
        if (kind == "border") return Brush(medal.BorderColor ?? color);
        if (kind == "foreground")
        {
            var start = ToColor(medal.StartColor ?? color);
            var end = ToColor(medal.EndColor ?? color);
            double lightness = ((start.R + end.R) * .299 + (start.G + end.G) * .587 + (start.B + end.B) * .114) / 2;
            return lightness > 165 ? Brush(0x182639) : Brushes.White;
        }
        var gradient = new LinearGradientBrush(ToColor(medal.StartColor ?? color),
            ToColor(medal.EndColor ?? color), 0);
        gradient.Freeze();
        return gradient;
    }

    private static Color ToColor(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    private static SolidColorBrush Brush(int rgb)
    {
        var brush = new SolidColorBrush(ToColor(rgb));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BadgeFontSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double size && double.IsFinite(size) ? Math.Clamp(size * .78, 9, 78) : 11d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class MedalIfShownConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 && values[1] is true && values[0] is string { Length: > 0 } medal ? medal + " " : "";
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
