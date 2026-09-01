using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SaraBI.Controls;

public partial class ExcelColumnHeader : UserControl
{
    public static readonly DependencyProperty FilterActiveProperty =
        DependencyProperty.RegisterAttached(
            "FilterActive",
            typeof(bool),
            typeof(ExcelColumnHeader),
            new FrameworkPropertyMetadata(false));

    public static void SetFilterActive(DependencyObject element, bool value) =>
        element.SetValue(FilterActiveProperty, value);

    public static bool GetFilterActive(DependencyObject element) =>
        (bool)element.GetValue(FilterActiveProperty);

    public string ColumnName { get; } = "";

    public ExcelColumnHeader()
    {
        InitializeComponent();
    }
}
