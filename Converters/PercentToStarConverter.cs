using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DuplicateDetector.Converters;

public class PercentToStarConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double d)
            return new GridLength(d, GridUnitType.Star);
        return new GridLength(0, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
