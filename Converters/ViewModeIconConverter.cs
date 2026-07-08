namespace Authenticator.Converters;

public partial class ViewModeIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is true
            ? FluentIcons.GridView
            : FluentIcons.List;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}