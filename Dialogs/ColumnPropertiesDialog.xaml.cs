using System.Windows;
using SaraBI.Models;

namespace SaraBI.Dialogs;

public partial class ColumnPropertiesDialog : Window
{
    public sealed class KindItem
    {
        public ColumnDataKind Kind { get; init; }
        public string Label { get; init; } = "";
    }

    public string ColumnName => NameBox.Text.Trim();
    public ColumnDataKind Kind => KindCombo.SelectedItem is KindItem item ? item.Kind : ColumnDataKind.Texto;

    public ColumnPropertiesDialog(string currentName, ColumnDataKind currentKind)
    {
        InitializeComponent();
        NameBox.Text = currentName;
        var items = new[]
        {
            new KindItem { Kind = ColumnDataKind.Texto, Label = "Texto" },
            new KindItem { Kind = ColumnDataKind.Numero, Label = "Número" },
            new KindItem { Kind = ColumnDataKind.Moneda, Label = "Moneda" },
            new KindItem { Kind = ColumnDataKind.Entero, Label = "Entero" },
            new KindItem { Kind = ColumnDataKind.Fecha, Label = "Fecha" },
            new KindItem { Kind = ColumnDataKind.Logico, Label = "Verdadero/Falso" },
        };
        KindCombo.ItemsSource = items;
        KindCombo.SelectedItem = items.FirstOrDefault(i => i.Kind == currentKind) ?? items[0];
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            MessageBox.Show(this, "Escriba un nombre para la columna.", "Columna",
                MessageBoxButton.OK, MessageBoxImage.Information);
            NameBox.Focus();
            return;
        }

        DialogResult = true;
    }
}
