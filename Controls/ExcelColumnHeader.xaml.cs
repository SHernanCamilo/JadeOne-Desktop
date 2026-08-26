using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SaraBI.Controls;

public partial class ExcelColumnHeader : UserControl
{
    private static readonly Brush Idle = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush Active = new SolidColorBrush(Color.FromRgb(0x21, 0x73, 0x46));

    public string ColumnName { get; }

    public event EventHandler? FilterClicked;

    public ExcelColumnHeader(string columnName)
    {
        InitializeComponent();
        ColumnName = columnName;
        TitleBlock.Text = columnName;
        FilterIcon.Fill = Idle;
    }

    public void SetFilterActive(bool active)
    {
        FilterIcon.Fill = active ? Active : Idle;
        FilterButton.ToolTip = active ? "Filtro activo — clic para editar" : "Filtro de columna";
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        FilterClicked?.Invoke(this, EventArgs.Empty);
    }
}
