using System.Windows;
using SaraBI.Models;

namespace SaraBI.Dialogs;

public partial class DateFilterDialog : Window
{
    public DateRangeFilter? Result { get; private set; }

    public DateFilterDialog(IEnumerable<FabricColumn> dateColumns)
    {
        InitializeComponent();
        var cols = dateColumns.ToList();
        ColumnCombo.ItemsSource = cols;
        if (cols.Count > 0)
        {
            ColumnCombo.SelectedIndex = 0;
        }

        FromPicker.SelectedDate = DateTime.Today.AddMonths(-1);
        ToPicker.SelectedDate = DateTime.Today;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (ColumnCombo.SelectedItem is not FabricColumn col)
        {
            MessageBox.Show(this, "Seleccione una columna de fecha.", "JadeOne Desktop",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (FromPicker.SelectedDate is not DateTime from)
        {
            MessageBox.Show(this, "Indique la fecha Desde.", "JadeOne Desktop",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new DateRangeFilter
        {
            Column = col.Name,
            From = from,
            To = ToPicker.SelectedDate,
        };
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
