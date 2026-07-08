namespace Authenticator.Converters;

public partial class ProgressToWidthConverter : IValueConverter
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