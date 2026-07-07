using System;
using Microsoft.UI.Xaml.Data;
using Windows.UI;

namespace Authenticator.Converters;

public class FirstLetterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is string text && !string.IsNullOrEmpty(text) ? text[0].ToString().ToUpper() : "?";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class StringToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string hexColor && !string.IsNullOrEmpty(hexColor))
        {
            try
            {
                hexColor = hexColor.TrimStart('#');
                byte a = 255;
                var r = System.Convert.ToByte(hexColor.Substring(0, 2), 16);
                var g = System.Convert.ToByte(hexColor.Substring(2, 2), 16);
                var b = System.Convert.ToByte(hexColor.Substring(4, 2), 16);
                return Color.FromArgb(a, r, g, b);
            }
            catch
            {
                return Color.FromArgb(255, 78, 205, 196); // Default teal
            }
        }
        return Color.FromArgb(255, 78, 205, 196);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
#pragma warning disable S3358
        return value is int count
            ? count > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Collapsed;
#pragma warning restore S3358
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class ProgressToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double progress)
        {
            // Assuming progress is 0.0 to 1.0, convert to width (max 200px as seen in AccountCard)
            return progress * 200.0;
        }
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
#pragma warning disable S3358
        return value is bool boolValue
            ? boolValue ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Collapsed;
#pragma warning restore S3358
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
#pragma warning disable S3358
        return value is bool boolValue
            ? boolValue ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Visible;
#pragma warning restore S3358
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
#pragma warning disable S3358
        return value is int count
            ? count == 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Collapsed;
#pragma warning restore S3358
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class ViewModeIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool isGridView)
        {
            return isGridView ? "\uE8FD" : "\uE80A"; // Grid icon vs List icon
        }
        return "\uE80A";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}