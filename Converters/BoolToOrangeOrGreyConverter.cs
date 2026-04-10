using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DuplicateDetector.Converters;

public class BoolToOrangeOrGreyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b)
            return Brushes.Orange;

        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
