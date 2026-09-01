using System.ComponentModel;
using System.Data;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SaraBI.Models;
using SaraBI.Services;

namespace SaraBI.Controls;

public partial class PivotPanel : UserControl
{
    private const string DragFormat = "JadeOne.PivotField";
    private static readonly Brush ZoneNormal = Brushes.White;
    private static readonly Brush ZoneHover = new SolidColorBrush(Color.FromRgb(226, 239, 218));

    private readonly List<FieldItem> _allFields = new();
    private readonly Dictionary<string, List<string>> _choices = new(StringComparer.OrdinalIgnoreCase);
    private Point _dragStart;
    private bool _syncing;
    private DataView? _view;

    private PivotConfig _config = new();
    public PivotConfig Config => _config;

    public event EventHandler? ConfigChanged;
    public event EventHandler? Closed;
    public event EventHandler? Cleared;

    public PivotPanel()
    {
        InitializeComponent();
    }

    public void AttachConfig(PivotConfig config)
    {
        _config = config;
        RefreshChips();
        SyncChecks();
        ApplyButton.IsEnabled = false;
    }

    public void SetFields(IEnumerable<string> fields, DataView? view = null)
    {
        _view = view;
        _allFields.Clear();
        _allFields.AddRange(fields.Select(f => new FieldItem(f)));
        CollectChoices(view);
        RefreshFieldList();
        RefreshChips();
        SyncChecks();
    }

    public void Reset()
    {
        Config.Clear();
        RefreshChips();
        SyncChecks();
        ApplyButton.IsEnabled = false;
    }

    private void CollectChoices(DataView? view)
    {
        _choices.Clear();
        if (view is null)
        {
            return;
        }

        foreach (var field in _allFields)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRowView row in view)
            {
                set.Add(PivotEngine.FormatCell(row, field.Name));
                if (set.Count >= 200)
                {
                    break;
                }
            }

            var list = new List<string> { "(Todos)" };
            list.AddRange(set.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase));
            _choices[field.Name] = list;
        }
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        if (FieldSearchHint is not null)
        {
            FieldSearchHint.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        RefreshFieldList();
    }

    private void RefreshFieldList()
    {
        var q = SearchBox?.Text.Trim() ?? "";
        FieldsList.ItemsSource = string.IsNullOrEmpty(q)
            ? _allFields.ToList()
            : _allFields.Where(f => f.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnFieldCheck(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not CheckBox { DataContext: FieldItem item })
        {
            return;
        }

        if (item.IsChecked)
        {
            if (!IsPlaced(item.Name))
            {
                AddToZone(item.Name, "rows", DateGroupLevel.None, expandDates: true);
                RaiseChanged();
            }
        }
        else
        {
            RemoveFromAll(item.Name);
            RaiseChanged();
        }
    }

    private void OnFieldMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    private void OnFieldMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement fe)
        {
            return;
        }

        if (!MovedEnough(e.GetPosition(this)))
        {
            return;
        }

        var name = fe.DataContext is FieldItem item ? item.Name : fe.DataContext as string;
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        DragDrop.DoDragDrop(fe, new DataObject(DragFormat, new FieldDrag(name, DateGroupLevel.None, null)), DragDropEffects.Move);
    }

    private void OnChipMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    private void OnChipMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { Tag: ChipItem chip })
        {
            return;
        }

        if (IsInteractiveSource(e.OriginalSource))
        {
            return;
        }

        if (!MovedEnough(e.GetPosition(this)))
        {
            return;
        }

        DragDrop.DoDragDrop(
            (DependencyObject)sender,
            new DataObject(DragFormat, new FieldDrag(chip.Column, chip.Group, chip.Zone)),
            DragDropEffects.Move);
    }

    private static bool IsInteractiveSource(object? source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is ComboBox or Button or TextBox or System.Windows.Controls.Primitives.ScrollBar)
            {
                return true;
            }

            d = VisualTreeHelper.GetParent(d);
        }

        return false;
    }

    private bool MovedEnough(Point now) =>
        Math.Abs(now.X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance
        || Math.Abs(now.Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance;

    private void OnZoneDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnZoneDragEnter(object sender, DragEventArgs e)
    {
        if (sender is Border b && e.Data.GetDataPresent(DragFormat))
        {
            b.Background = ZoneHover;
        }
    }

    private void OnZoneDragLeave(object sender, DragEventArgs e)
    {
        if (sender is not Border b)
        {
            return;
        }

        var p = e.GetPosition(b);
        if (p.X < 0 || p.Y < 0 || p.X > b.ActualWidth || p.Y > b.ActualHeight)
        {
            b.Background = ZoneNormal;
        }
    }

    private void OnZoneDrop(object sender, DragEventArgs e)
    {
        if (sender is Border b)
        {
            b.Background = ZoneNormal;
        }

        if (sender is not FrameworkElement { Tag: string zone }
            || e.Data.GetData(DragFormat) is not FieldDrag drag)
        {
            return;
        }

        e.Handled = true;
        MoveToZone(drag.Column, zone, drag.Group, drag.FromZone);
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not FieldDrag drag)
        {
            return;
        }

        e.Handled = true;
        RemoveFromAll(drag.Column);
        RaiseChanged();
    }

    private void MoveToZone(string column, string zone, DateGroupLevel group, string? fromZone)
    {
        if (string.IsNullOrEmpty(column))
        {
            return;
        }

        if (fromZone is null)
        {
            if (IsInZone(column, zone, group, bySource: true))
            {
                return;
            }

            RemoveFromAll(column);
            AddToZone(column, zone, group, expandDates: true);
            RaiseChanged();
            return;
        }

        RemoveExact(column, group, fromZone);
        if (IsInZone(column, zone, group, bySource: false))
        {
            RaiseChanged();
            return;
        }

        AddToZone(column, zone, group, expandDates: false);
        RaiseChanged();
    }

    private bool IsInZone(string column, string zone, DateGroupLevel group, bool bySource) => zone switch
    {
        "filters" => Config.Filters.Contains(column, StringComparer.OrdinalIgnoreCase),
        "columns" => bySource
            ? Config.Columns.Any(c => string.Equals(c.Column, column, StringComparison.OrdinalIgnoreCase))
            : Config.Columns.Any(c => c.Matches(column, group)),
        "rows" => bySource
            ? Config.Rows.Any(r => string.Equals(r.Column, column, StringComparison.OrdinalIgnoreCase))
            : Config.Rows.Any(r => r.Matches(column, group)),
        "values" => Config.Values.Any(v => string.Equals(v.Column, column, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private void AddToZone(string column, string zone, DateGroupLevel group, bool expandDates)
    {
        var table = _view?.Table;
        switch (zone)
        {
            case "filters":
                Config.Filters.Add(column);
                Config.FilterSelections[column] = "(Todos)";
                break;
            case "columns":
                if (expandDates && group == DateGroupLevel.None && DateGroup.IsDateColumn(table, column))
                {
                    Config.Columns.Add(new PivotAxisField(column, DateGroup.DefaultColumnLevel(_view, column)));
                }
                else
                {
                    Config.Columns.Add(new PivotAxisField(column, group));
                }

                break;
            case "rows":
                if (expandDates && group == DateGroupLevel.None && DateGroup.IsDateColumn(table, column))
                {
                    foreach (var level in DateGroup.DefaultRowLevels(_view, column))
                    {
                        Config.Rows.Add(new PivotAxisField(column, level));
                    }
                }
                else
                {
                    Config.Rows.Add(new PivotAxisField(column, group));
                }

                break;
            case "values":
                Config.Values.Add(new PivotValueField
                {
                    Column = column,
                    Operation = DateGroup.IsDateColumn(table, column) ? PivotOp.Count : PivotOp.Sum,
                });
                break;
        }
    }

    private void OnRemoveChip(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChipItem chip })
        {
            return;
        }

        RemoveExact(chip.Column, chip.Group, chip.Zone);
        RaiseChanged();
    }

    private void OnOpChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing
            || sender is not ComboBox { Tag: ChipItem item, SelectedItem: ComboBoxItem { Tag: string tag } }
            || !Enum.TryParse<PivotOp>(tag, out var op)
            || item.Index < 0
            || item.Index >= Config.Values.Count
            || Config.Values[item.Index].Operation == op)
        {
            return;
        }

        Config.Values[item.Index].Operation = op;
        NotifyConfig();
    }

    private void OnFilterValueChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || sender is not ComboBox { Tag: ChipItem item })
        {
            return;
        }

        var next = item.SelectedFilter;
        if (string.IsNullOrEmpty(next))
        {
            return;
        }

        if (Config.FilterSelections.TryGetValue(item.Column, out var current)
            && string.Equals(current, next, StringComparison.Ordinal))
        {
            return;
        }

        Config.FilterSelections[item.Column] = next;
        NotifyConfig();
    }

    private void OnDeferChanged(object sender, RoutedEventArgs e)
    {
        if (DeferCheck.IsChecked != true && ApplyButton.IsEnabled)
        {
            OnApply(sender, e);
        }
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        Config.Clear();
        RefreshChips();
        SyncChecks();
        ApplyButton.IsEnabled = false;
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Closed?.Invoke(this, EventArgs.Empty);

    private void RaiseChanged()
    {
        Config.ExpandedPaths.Clear();
        RefreshChips();
        SyncChecks();
        NotifyConfig();
    }

    private void NotifyConfig()
    {
        if (DeferCheck.IsChecked == true)
        {
            ApplyButton.IsEnabled = true;
            return;
        }

        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshChips()
    {
        _syncing = true;
        FilterChips.ItemsSource = Config.Filters.Select((n, i) => new ChipItem(n, i, "filters")
        {
            Choices = _choices.TryGetValue(n, out var c) ? c : new List<string> { "(Todos)" },
            SelectedFilter = Config.FilterSelections.TryGetValue(n, out var sel) ? sel : "(Todos)",
        }).ToList();
        ColumnChips.ItemsSource = Config.Columns.Select((f, i) => new ChipItem(f.Column, i, "columns", group: f.Group)).ToList();
        RowChips.ItemsSource = Config.Rows.Select((f, i) => new ChipItem(f.Column, i, "rows", group: f.Group)).ToList();
        ValueChips.ItemsSource = Config.Values.Select((v, i) => new ChipItem(v.Column, i, "values", v.Operation)).ToList();
        _syncing = false;
    }

    private void SyncChecks()
    {
        _syncing = true;
        foreach (var field in _allFields)
        {
            field.IsChecked = IsPlaced(field.Name);
        }

        _syncing = false;
    }

    private bool IsPlaced(string name) =>
        Config.Rows.Any(r => string.Equals(r.Column, name, StringComparison.OrdinalIgnoreCase))
        || Config.Columns.Any(c => string.Equals(c.Column, name, StringComparison.OrdinalIgnoreCase))
        || Config.Filters.Contains(name, StringComparer.OrdinalIgnoreCase)
        || Config.Values.Any(v => string.Equals(v.Column, name, StringComparison.OrdinalIgnoreCase));

    private void RemoveFromAll(string name)
    {
        Config.Rows.RemoveAll(r => string.Equals(r.Column, name, StringComparison.OrdinalIgnoreCase));
        Config.Columns.RemoveAll(c => string.Equals(c.Column, name, StringComparison.OrdinalIgnoreCase));
        Config.Filters.RemoveAll(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        Config.FilterSelections.Remove(name);
        Config.Values.RemoveAll(v => string.Equals(v.Column, name, StringComparison.OrdinalIgnoreCase));
        Config.ExpandedPaths.Clear();
    }

    private void RemoveExact(string column, DateGroupLevel group, string zone)
    {
        switch (zone)
        {
            case "rows":
                Config.Rows.RemoveAll(r => r.Matches(column, group));
                break;
            case "columns":
                Config.Columns.RemoveAll(c => c.Matches(column, group));
                break;
            case "filters":
                Config.Filters.RemoveAll(f => string.Equals(f, column, StringComparison.OrdinalIgnoreCase));
                Config.FilterSelections.Remove(column);
                break;
            case "values":
                Config.Values.RemoveAll(v => string.Equals(v.Column, column, StringComparison.OrdinalIgnoreCase));
                break;
        }

        Config.ExpandedPaths.Clear();
    }

    public sealed class FieldItem : INotifyPropertyChanged
    {
        private bool _isChecked;

        public FieldItem(string name) => Name = name;

        public string Name { get; }

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                {
                    return;
                }

                _isChecked = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class ChipItem : INotifyPropertyChanged
    {
        private string _selectedFilter = "(Todos)";

        public ChipItem(string column, int index, string zone, PivotOp op = PivotOp.Sum, DateGroupLevel group = DateGroupLevel.None)
        {
            Column = column;
            Group = group;
            Name = DateGroup.FieldLabel(column, group);
            Index = index;
            Zone = zone;
            Operation = op;
        }

        public string Column { get; }
        public DateGroupLevel Group { get; }
        public string Name { get; }
        public int Index { get; }
        public string Zone { get; }
        public PivotOp Operation { get; }
        public int OpIndex => (int)Operation;
        public IReadOnlyList<string> Choices { get; init; } = new List<string> { "(Todos)" };

        public string SelectedFilter
        {
            get => _selectedFilter;
            set
            {
                if (_selectedFilter == value)
                {
                    return;
                }

                _selectedFilter = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed record FieldDrag(string Column, DateGroupLevel Group, string? FromZone);
}
