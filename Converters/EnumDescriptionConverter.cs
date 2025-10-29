using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows.Data;

namespace DuplicateDetector.Converters;

public class EnumDescriptionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null) return "";

        FieldInfo fi = value.GetType().GetField(value.ToString());
        var attr = fi.GetCustomAttribute<DescriptionAttribute>();
        return attr != null ? attr.Description : value.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
