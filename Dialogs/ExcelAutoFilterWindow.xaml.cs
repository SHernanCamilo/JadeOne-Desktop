using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SaraBI.Models;
using SaraBI.Services;

namespace SaraBI.Dialogs;

public partial class ExcelAutoFilterWindow : Window
{
    private readonly List<FilterValueItem> _all;
    private readonly bool _textOnly;
    private readonly bool _isDate;
    private readonly bool _hadFilter;
    private readonly List<DateTreeNode> _dateRoots = new();
    private bool _suppressSelectAll;
    private bool _closing;
    private bool _ignoreDeactivate;
    private TextFilterOperator _textOperator;

    private static readonly string[] MonthsEs =
    {
        "Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio",
        "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre",
    };

    public ColumnAutoFilter? Result { get; private set; }
    public ListSortDirection? Sort { get; private set; }
    public bool Cleared { get; private set; }
    public bool Accepted { get; private set; }

    public ExcelAutoFilterWindow(
        string columnName,
        IReadOnlyList<FilterValueItem> values,
        ColumnAutoFilter? current,
        bool textOnly,
        bool isDate = false)
    {
        InitializeComponent();
        _all = values.ToList();
        _textOnly = textOnly;
        _isDate = isDate;
        _hadFilter = current?.IsActive == true;
        _textOperator = current?.TextOperator ?? TextFilterOperator.None;

        ClearFilterText.Text = $"Borrar filtro de \"{columnName}\"";
        ClearFilterBtn.IsEnabled = _hadFilter;
        TextValueBox.Text = current?.TextValue ?? "";
        ShowTextFilter(_textOperator, current?.TextValue);

        if (isDate)
        {
            SetupDateMode(current, textOnly);
        }
        else if (textOnly)
        {
            SelectAllRow.Visibility = Visibility.Collapsed;
            ValuesList.Visibility = Visibility.Collapsed;
            ValuesPanel.Visibility = Visibility.Collapsed;
            SearchBox.Visibility = Visibility.Collapsed;
            SearchPlaceholder.Visibility = Visibility.Collapsed;
            HintText.Visibility = Visibility.Visible;
            HintText.Text = "Hay demasiados valores distintos. Use Filtros de texto (Contiene, Empieza por, etc.).";
        }
        else
        {
            ValuesList.ItemsSource = _all;
            ApplyCurrentChecks(current);
            foreach (var item in _all)
            {
                item.PropertyChanged += OnItemChanged;
            }

            RefreshSelectAllBox();
        }

        UpdateOkEnabled();
        Loaded += OnLoaded;
        Deactivated += OnWindowDeactivated;
    }

    private void SetupDateMode(ColumnAutoFilter? current, bool textOnly)
    {
        TextFiltersBtn.Visibility = Visibility.Collapsed;
        DateTabs.Visibility = Visibility.Visible;
        SearchPlaceholder.Text = "Buscar fecha...";
        Width = 292;

        FromPicker.SelectedDate = current?.DateFrom;
        ToPicker.SelectedDate = current?.DateTo;
        RefreshRangeSummary();

        if (current?.DateFrom is not null || current?.DateTo is not null)
        {
            TabRango.IsChecked = true;
        }

        if (textOnly)
        {
            ValuesPanel.Visibility = Visibility.Collapsed;
            SearchRow.Visibility = Visibility.Collapsed;
            HintText.Visibility = Visibility.Visible;
            HintText.Text = "Hay demasiadas fechas distintas. Use la pestaña Rango.";
            TabRango.IsChecked = true;
            return;
        }

        ValuesList.Visibility = Visibility.Collapsed;
        DateTree.Visibility = Visibility.Visible;
        ApplyCurrentChecks(current);
        BuildDateTree();
        foreach (var item in _all)
        {
            item.PropertyChanged += OnItemChanged;
        }

        RefreshSelectAllBox();
        RefreshDateTreeFilter();
    }

    private void BuildDateTree()
    {
        _dateRoots.Clear();
        DateTreeNode? blanks = null;
        var years = new Dictionary<int, DateTreeNode>();

        foreach (var item in _all)
        {
            if (item.Value.Length == 0 || !DataFileParser.TryParseFecha(item.Value, out var dt))
            {
                blanks ??= new DateTreeNode { Label = "(En blanco)", Level = 0 };
                blanks.Items.Add(item);
                continue;
            }

            var day = dt.Date;
            if (!years.TryGetValue(day.Year, out var yearNode))
            {
                yearNode = new DateTreeNode { Label = $"Año {day.Year}", Level = 0 };
                years[day.Year] = yearNode;
                _dateRoots.Add(yearNode);
            }

            var monthKey = day.Month;
            var monthNode = yearNode.Children.FirstOrDefault(c => c.Level == 1 && c.Label.StartsWith(MonthsEs[monthKey - 1], StringComparison.Ordinal));
            if (monthNode is null)
            {
                monthNode = new DateTreeNode
                {
                    Label = $"{MonthsEs[monthKey - 1]} {day.Year}",
                    Level = 1,
                    Parent = yearNode,
                };
                yearNode.Children.Add(monthNode);
            }

            var dayLabel = $"{day.Day} {MonthsEs[monthKey - 1]} {day.Year}";
            var dayNode = monthNode.Children.FirstOrDefault(c => c.Label == dayLabel);
            if (dayNode is null)
            {
                dayNode = new DateTreeNode
                {
                    Label = dayLabel,
                    Level = 2,
                    Parent = monthNode,
                };
                monthNode.Children.Add(dayNode);
            }

            dayNode.Items.Add(item);
        }

        foreach (var year in _dateRoots)
        {
            year.Children.Sort((a, b) => MonthIndex(a.Label).CompareTo(MonthIndex(b.Label)));
            foreach (var month in year.Children)
            {
                month.Children.Sort((a, b) => ExtractDayNum(a.Label).CompareTo(ExtractDayNum(b.Label)));
                foreach (var day in month.Children)
                {
                    day.RefreshFromChildren();
                }

                month.RefreshFromChildren();
            }

            year.RefreshFromChildren();
        }

        _dateRoots.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.Ordinal));
        if (blanks is not null)
        {
            blanks.RefreshFromChildren();
            _dateRoots.Insert(0, blanks);
        }
    }

    private static int MonthIndex(string label)
    {
        for (var i = 0; i < MonthsEs.Length; i++)
        {
            if (label.StartsWith(MonthsEs[i], StringComparison.Ordinal))
            {
                return i;
            }
        }

        return 99;
    }

    private static int ExtractDayNum(string label)
    {
        var space = label.IndexOf(' ');
        return space > 0 && int.TryParse(label[..space], out var n) ? n : 0;
    }

    private void RefreshDateTreeFilter()
    {
        var q = SearchBox.Text ?? "";
        var visible = new List<DateTreeNode>();
        foreach (var root in _dateRoots)
        {
            if (root.ApplySearch(q))
            {
                visible.Add(root);
            }
        }

        DateTree.ItemsSource = visible;
    }

    private void OnDateTabChanged(object sender, RoutedEventArgs e)
    {
        if (DateTabs.Visibility != Visibility.Visible)
        {
            return;
        }

        var range = TabRango.IsChecked == true;
        RangePanel.Visibility = range ? Visibility.Visible : Visibility.Collapsed;
        SearchRow.Visibility = range ? Visibility.Collapsed : Visibility.Visible;
        ValuesPanel.Visibility = range ? Visibility.Collapsed : Visibility.Visible;
        UpdateOkEnabled();
    }

    private void OnDatePickerOpened(object sender, RoutedEventArgs e) => _ignoreDeactivate = true;

    private void OnDatePickerClosed(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(() => _ignoreDeactivate = false, DispatcherPriority.Input);

    private void OnRangeChanged(object? sender, SelectionChangedEventArgs e) => RefreshRangeSummary();

    private void RefreshRangeSummary()
    {
        if (RangeSummary is null)
        {
            return;
        }

        if (FromPicker.SelectedDate is DateTime from && ToPicker.SelectedDate is DateTime to)
        {
            RangeSummary.Text = $"Filtrando: {from:dd/MM/yyyy} - {to:dd/MM/yyyy}";
            RangeSummary.Visibility = Visibility.Visible;
        }
        else
        {
            RangeSummary.Visibility = Visibility.Collapsed;
        }

        UpdateOkEnabled();
    }

    private void OnRangeShortcut(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var today = DateTime.Today;
        DateTime from;
        DateTime to;
        switch (tag)
        {
            case "yesterday":
                from = to = today.AddDays(-1);
                break;
            case "thisWeek":
                var delta = ((int)today.DayOfWeek + 6) % 7;
                from = today.AddDays(-delta);
                to = from.AddDays(6);
                break;
            case "lastWeek":
                var d2 = ((int)today.DayOfWeek + 6) % 7;
                from = today.AddDays(-d2 - 7);
                to = from.AddDays(6);
                break;
            case "thisMonth":
                from = new DateTime(today.Year, today.Month, 1);
                to = from.AddMonths(1).AddDays(-1);
                break;
            case "lastMonth":
                from = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                to = new DateTime(today.Year, today.Month, 1).AddDays(-1);
                break;
            case "thisYear":
                from = new DateTime(today.Year, 1, 1);
                to = new DateTime(today.Year, 12, 31);
                break;
            case "lastYear":
                from = new DateTime(today.Year - 1, 1, 1);
                to = new DateTime(today.Year - 1, 12, 31);
                break;
            default:
                from = to = today;
                break;
        }

        FromPicker.SelectedDate = from;
        ToPicker.SelectedDate = to;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (TextFilterPanel.Visibility == Visibility.Visible)
        {
            TextValueBox.Focus();
            TextValueBox.SelectAll();
        }
        else if (SearchBox.Visibility == Visibility.Visible)
        {
            SearchBox.Focus();
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_ignoreDeactivate || _closing)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_ignoreDeactivate || _closing || IsActive)
            {
                return;
            }

            Finish(false);
        }, DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        Finish(false);
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

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSelectAll || e.PropertyName != nameof(FilterValueItem.IsChecked))
        {
            return;
        }

        RefreshSelectAllBox();
        UpdateOkEnabled();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_textOnly)
        {
            return;
        }

        if (_isDate)
        {
            RefreshDateTreeFilter();
            RefreshSelectAllBox();
            return;
        }

        var q = SearchBox.Text.Trim();
        ValuesList.ItemsSource = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(v => v.Display.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

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
        if (visible.Count == 0)
        {
            SelectAllBox.IsChecked = false;
        }
        else if (visible.All(v => v.IsChecked))
        {
            SelectAllBox.IsChecked = true;
        }
        else if (visible.Any(v => v.IsChecked))
        {
            SelectAllBox.IsChecked = null;
        }
        else
        {
            SelectAllBox.IsChecked = false;
        }

        _suppressSelectAll = false;
        UpdateOkEnabled();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectAll || _textOnly)
        {
            return;
        }

        var checkAll = VisibleItems().Any(v => !v.IsChecked);
        _suppressSelectAll = true;
        foreach (var item in VisibleItems())
        {
            item.IsChecked = checkAll;
        }

        if (_isDate)
        {
            foreach (var root in _dateRoots)
            {
                root.RefreshFromChildren();
            }
        }

        _suppressSelectAll = false;
        RefreshSelectAllBox();
    }

    private void OnTextFiltersHover(object sender, MouseEventArgs e) => OpenTextFiltersMenu();

    private void OnTextFiltersClick(object sender, RoutedEventArgs e) => OpenTextFiltersMenu();

    private void OpenTextFiltersMenu()
    {
        if (TextFiltersMenu.IsOpen)
        {
            return;
        }

        _ignoreDeactivate = true;
        TextFiltersMenu.PlacementTarget = TextFiltersBtn;
        TextFiltersMenu.Placement = PlacementMode.Right;
        TextFiltersMenu.IsOpen = true;
    }

    private void OnTextFiltersMenuClosed(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _ignoreDeactivate = false;
            if (!IsActive && !_closing)
            {
                Finish(false);
            }
        }, DispatcherPriority.Input);
    }

    private void OnTextOperator(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string tag)
        {
            return;
        }

        _textOperator = Enum.TryParse<TextFilterOperator>(tag, out var op)
            ? op
            : TextFilterOperator.Contains;
        ShowTextFilter(_textOperator, TextValueBox.Text);
        TextValueBox.Focus();
        TextValueBox.SelectAll();
        UpdateOkEnabled();
    }

    private void ShowTextFilter(TextFilterOperator op, string? value)
    {
        if (op == TextFilterOperator.None)
        {
            TextFilterPanel.Visibility = Visibility.Collapsed;
            return;
        }

        _textOperator = op;
        TextOperatorLabel.Text = OperatorLabel(op);
        TextValueBox.Text = value ?? "";
        TextFilterPanel.Visibility = Visibility.Visible;
    }

    private void OnClearTextFilter(object sender, RoutedEventArgs e)
    {
        _textOperator = TextFilterOperator.None;
        TextValueBox.Text = "";
        TextFilterPanel.Visibility = Visibility.Collapsed;
        UpdateOkEnabled();
    }

    private void OnTextValueChanged(object sender, TextChangedEventArgs e) => UpdateOkEnabled();

    private void UpdateOkEnabled()
    {
        var hasText = _textOperator != TextFilterOperator.None
                      && !string.IsNullOrWhiteSpace(TextValueBox.Text);
        if (_textOnly)
        {
            OkButton.IsEnabled = hasText || _hadFilter;
            return;
        }

        if (_isDate && TabRango.IsChecked == true)
        {
            OkButton.IsEnabled = FromPicker.SelectedDate is not null || ToPicker.SelectedDate is not null || _hadFilter;
            return;
        }

        OkButton.IsEnabled = hasText || _all.Any(v => v.IsChecked);
    }

    private void OnValuesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_textOnly || (e.OriginalSource as FrameworkElement)?.DataContext is not FilterValueItem item)
        {
            return;
        }

        _suppressSelectAll = true;
        foreach (var v in _all)
        {
            v.IsChecked = ReferenceEquals(v, item);
        }

        _suppressSelectAll = false;
        OnOk(sender, e);
    }

    private void OnSortAsc(object sender, RoutedEventArgs e)
    {
        Sort = ListSortDirection.Ascending;
        Finish(true);
    }

    private void OnSortDesc(object sender, RoutedEventArgs e)
    {
        Sort = ListSortDirection.Descending;
        Finish(true);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Cleared = true;
        Result = null;
        Finish(true);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Finish(false);

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var textValue = TextValueBox.Text.Trim();
        var textOp = string.IsNullOrEmpty(textValue) ? TextFilterOperator.None : _textOperator;

        if (_isDate && TabRango.IsChecked == true)
        {
            Result = new ColumnAutoFilter
            {
                DateFrom = FromPicker.SelectedDate,
                DateTo = ToPicker.SelectedDate,
            };
            if (Result.DateFrom is null && Result.DateTo is null)
            {
                Result = null;
            }

            Finish(true);
            return;
        }

        if (_textOnly)
        {
            Result = textOp == TextFilterOperator.None
                ? null
                : new ColumnAutoFilter { TextOperator = textOp, TextValue = textValue };
            Finish(true);
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

        var allChecked = _all.Count > 0 && _all.All(v => v.IsChecked);
        if (allChecked && textOp == TextFilterOperator.None)
        {
            Result = null;
        }
        else
        {
            Result = new ColumnAutoFilter
            {
                Selected = allChecked ? null : selected,
                IncludeBlanks = includeBlanks,
                TextOperator = textOp,
                TextValue = textOp == TextFilterOperator.None ? null : textValue,
            };
        }

        Finish(true);
    }

    private void Finish(bool accepted)
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Accepted = accepted;
        Close();
    }

    private static string OperatorLabel(TextFilterOperator op) => op switch
    {
        TextFilterOperator.Equals => "Es igual a",
        TextFilterOperator.NotEquals => "No es igual a",
        TextFilterOperator.StartsWith => "Empieza por",
        TextFilterOperator.EndsWith => "Termina en",
        TextFilterOperator.NotContains => "No contiene",
        _ => "Contiene",
    };
}
