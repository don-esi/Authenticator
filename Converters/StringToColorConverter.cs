namespace Authenticator.Converters;

public partial class StringToColorConverter : IValueConverter
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