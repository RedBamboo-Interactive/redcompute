using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace RedCompute.App.Converters;

public class StringToSemiTransparentBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && hex.StartsWith('#'))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                color.A = 51; // ~20% opacity
                return new SolidColorBrush(color);
            }
            catch { }
        }
        return new SolidColorBrush(Color.FromArgb(51, 114, 118, 125));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
