using System;
using Microsoft.UI.Xaml.Data;

namespace Authenticator.Converters;

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool boolValue ? !boolValue : false;
    }

#pragma warning disable S4144

    public object ConvertBack(object value, Type targetType, object parameter, string language)
#pragma warning restore S4144
    {
        return value is bool boolValue ? !boolValue : false;
    }
}