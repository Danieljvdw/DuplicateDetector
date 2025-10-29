using System.Globalization;
using System.Windows.Data;

namespace DuplicateDetector.Converters;

public class ByteSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            //return $"{bytes:N0} bytes ({len:0.##} {sizes[order]})"; // original raw bytes
            return $"({len:0.##} {sizes[order]})"; // cleaner only biggest order
        }
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
