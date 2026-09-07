using System.Globalization;
using System.Windows.Data;

namespace SaraBI.Converters;

public sealed class PorcentajeDisplayConverter : IValueConverter
{
    public static readonly PorcentajeDisplayConverter Instance = new();
    private static readonly CultureInfo EsCo = CultureInfo.GetCultureInfo("es-CO");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var formatted = Format(value);
        return formatted ?? (value is DBNull or null ? "" : value);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;

    public static string? Format(object? value)
    {
        if (value is DBNull or null)
        {
            return "";
        }

        try
        {
            var n = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (n is >= -1 and <= 1)
            {
                n *= 100;
            }

            return n.ToString("N2", EsCo) + " %";
        }
        catch
        {
            return null;
        }
    }
}
