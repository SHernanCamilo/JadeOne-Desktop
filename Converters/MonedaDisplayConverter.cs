using System.Globalization;
using System.Windows.Data;

namespace SaraBI.Converters;

public sealed class MonedaDisplayConverter : IValueConverter
{
    public static readonly MonedaDisplayConverter Instance = new();
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
            var amount = System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return amount.ToString("C2", EsCo);
        }
        catch
        {
            return null;
        }
    }
}
