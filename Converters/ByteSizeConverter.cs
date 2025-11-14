using System.Globalization;
using System.Windows.Data;

namespace DuplicateDetector.Converters;

public class ByteSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double && value is not long)
            return value;

        double bytes = System.Convert.ToDouble(value);
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        string mode = (parameter as string)?.ToLowerInvariant() ?? "normal";
        string suffix = sizes[order];

        if (mode is "speed")
            suffix += "/s";

        // Round to two decimals for readability
        return $"{len:0.##} {suffix}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
