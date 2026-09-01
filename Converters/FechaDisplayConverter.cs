using System.Globalization;
using System.Windows.Data;
using SaraBI.Services;

namespace SaraBI.Converters;

public sealed class FechaDisplayConverter : IValueConverter
{
    public static readonly FechaDisplayConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dt)
        {
            return DataFileParser.FormatFecha(dt);
        }

        return value is DBNull or null ? "" : value;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
