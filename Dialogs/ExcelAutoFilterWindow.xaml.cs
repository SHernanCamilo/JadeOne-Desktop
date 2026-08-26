using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SaraBI.Models;

namespace SaraBI.Dialogs;

public partial class ExcelAutoFilterWindow : Window
{
    private readonly List<FilterValueItem> _all;
    private readonly bool _textOnly;
    private bool _suppressSelectAll;

    public ColumnAutoFilter? Result { get; private set; }
    public ListSortDirection? Sort { get; private set; }
    public bool Cleared { get; private set; }

    public ExcelAutoFilterWindow(
        string columnName,
        IReadOnlyList<FilterValueItem> values,
        ColumnAutoFilter? current,
        bool textOnly)
    {
        InitializeComponent();
        TitleText.Text = columnName;
        _all = values.ToList();
        _textOnly = textOnly;
        ContainsBox.Text = current?.Contains ?? "";
        SearchBox.Text = "";

        if (textOnly)
        {
            SelectAllBox.Visibility = Visibility.Collapsed;
            ValuesList.Visibility = Visibility.Collapsed;
            SearchBox.Visibility = Visibility.Collapsed;
            HintText.Text = "Hay demasiados valores distintos. Filtre por texto (Contiene).";
        }
        else
        {
            HintText.Text = $"{_all.Count:N0} valores. Busque o marque los que desea ver.";
            ValuesList.ItemsSource = _all;
            ApplyCurrentChecks(current);
            RefreshSelectAllBox();
        }

        Loaded += (_, _) => SearchBox.Focus();
    }

    private void ApplyCurrentChecks(ColumnAutoFilter? current)
    {
        if (current?.Selected is null)
        {
            foreach (var item in _all)
            {
                item.IsChecked = true;
            }

            return;
        }

        foreach (var item in _all)
        {
            item.IsChecked = item.Value.Length == 0
                ? current.IncludeBlanks
                : current.Selected.Contains(item.Value);
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_textOnly)
        {
            return;
        }

        var q = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            ValuesList.ItemsSource = _all;
        }
        else
        {
            ValuesList.ItemsSource = _all
                .Where(v => v.Display.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        RefreshSelectAllBox();
    }

    private IEnumerable<FilterValueItem> VisibleItems() =>
        ValuesList.ItemsSource as IEnumerable<FilterValueItem> ?? _all;

    private void RefreshSelectAllBox()
    {
        if (_textOnly)
        {
            return;
        }

        _suppressSelectAll = true;
        var visible = VisibleItems().ToList();
        SelectAllBox.IsChecked = visible.Count > 0 && visible.All(v => v.IsChecked);
        _suppressSelectAll = false;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectAll || _textOnly)
        {
            return;
        }

        var check = SelectAllBox.IsChecked == true;
        foreach (var item in VisibleItems())
        {
            item.IsChecked = check;
        }
    }

    private void OnSortAsc(object sender, RoutedEventArgs e)
    {
        Sort = ListSortDirection.Ascending;
        DialogResult = true;
    }

    private void OnSortDesc(object sender, RoutedEventArgs e)
    {
        Sort = ListSortDirection.Descending;
        DialogResult = true;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Cleared = true;
        Result = null;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var contains = ContainsBox.Text.Trim();
        if (_textOnly)
        {
            Result = string.IsNullOrEmpty(contains)
                ? null
                : new ColumnAutoFilter { Contains = contains };
            DialogResult = true;
            return;
        }

        var selected = new HashSet<string>(StringComparer.Ordinal);
        var includeBlanks = false;
        foreach (var item in _all)
        {
            if (!item.IsChecked)
            {
                continue;
            }

            if (item.Value.Length == 0)
            {
                includeBlanks = true;
            }
            else
            {
                selected.Add(item.Value);
            }
        }

        var allChecked = _all.All(v => v.IsChecked);
        if (allChecked && string.IsNullOrEmpty(contains))
        {
            Result = null;
        }
        else
        {
            Result = new ColumnAutoFilter
            {
                Selected = allChecked ? null : selected,
                IncludeBlanks = includeBlanks,
                Contains = string.IsNullOrEmpty(contains) ? null : contains,
            };
        }

        DialogResult = true;
    }
}
