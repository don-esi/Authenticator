namespace Authenticator.Converters;

public partial class FirstLetterConverter : IValueConverter
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