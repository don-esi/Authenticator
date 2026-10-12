using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Authenticator;

public sealed class ProgressWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 3
            || values[0] is not double value
            || values[1] is not double maximum
            || values[2] is not double width
            || maximum <= 0
            || width <= 0)
        {
            return 0d;
        }

        var fraction = Math.Clamp(value / maximum, 0d, 1d);
        return fraction * width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
