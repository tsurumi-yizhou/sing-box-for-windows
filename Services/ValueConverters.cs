using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace SFW.Services;

/// <summary>Visibility.Visible when the bound value is true.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a URL test delay in milliseconds to the SFA/SFM delay palette:
/// &lt;= 0 neutral, &lt; 800 good, &lt; 1500 medium, &gt;= 1500 bad.
/// </summary>
public sealed class DelayToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var delay = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0,
        };
        var key = delay <= 0 ? "ControlFillColorTertiaryBrush"
            : delay < 800 ? "UrlTestGoodBrush"
            : delay < 1500 ? "UrlTestMediumBrush"
            : "UrlTestBadBrush";
        return Application.Current.Resources[key] as Brush
               ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>true when the bound string is null, empty or whitespace.</summary>
public sealed class EmptyStringToBoolConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var empty = string.IsNullOrWhiteSpace(value as string);
        return Invert ? !empty : empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
