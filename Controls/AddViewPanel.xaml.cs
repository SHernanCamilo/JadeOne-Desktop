using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SaraBI.Models;

namespace SaraBI.Controls;

public partial class AddViewPanel : UserControl
{
    private List<VistaCatalogItem> _all = new();

    public event EventHandler<VistaCatalogItem>? ViewPicked;
    public event EventHandler? Closed;

    public AddViewPanel()
    {
        InitializeComponent();
    }

    public void SetViews(IEnumerable<VistaCatalogItem> views)
    {
        _all = views.ToList();
        StatusText.Text = _all.Count == 0
            ? "No se encontraron vistas disponibles."
            : $"{_all.Count} vistas";
        ApplyFilter();
    }

    public void ShowLoading(string message = "Cargando vistas...")
    {
        StatusText.Text = message;
        ViewList.ItemsSource = null;
    }

    public void ShowError(string message)
    {
        StatusText.Text = message;
        ViewList.ItemsSource = null;
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var term = SearchBox.Text.Trim();
        IEnumerable<VistaCatalogItem> list = _all;
        if (!string.IsNullOrEmpty(term))
        {
            list = _all.Where(v =>
                v.ViewName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || v.SchemaDisplay.Contains(term, StringComparison.CurrentCultureIgnoreCase)
                || v.Schema.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        }

        ViewList.ItemsSource = list.ToList();
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VistaCatalogItem item } && item.Enabled)
        {
            ViewPicked?.Invoke(this, item);
        }
    }

    private void OnItemActivate(object sender, MouseButtonEventArgs e)
    {
        if (ViewList.SelectedItem is VistaCatalogItem item && item.Enabled)
        {
            ViewPicked?.Invoke(this, item);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Closed?.Invoke(this, EventArgs.Empty);
}
