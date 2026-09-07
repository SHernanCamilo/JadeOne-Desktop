using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SaraBI.Models;
using SaraBI.Services;

namespace SaraBI;

public sealed class ColumnVisibilityItem : INotifyPropertyChanged
{
    private bool _visible = true;

    public string Name { get; init; } = "";

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value)
            {
                return;
            }

            _visible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visible)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow
{
    private void CaptureActiveDataState()
    {
        if (_activeSheet is not { IsPivot: false } data)
        {
            return;
        }

        if (_table is not null
            && (data.SourceTable is null || ReferenceEquals(data.SourceTable, _table)))
        {
            data.SourceTable = _table;
        }

        data.Filters.Clear();
        foreach (var kv in _columnFilters)
        {
            data.Filters[kv.Key] = kv.Value;
        }

        data.Overrides.Clear();
        data.Overrides.AddRange(_columnOverrides);
        if (data.Columns.Count == 0 && _columns.Count > 0)
        {
            data.Columns.AddRange(_columns);
        }
    }

    private void RestoreDataState(WorkbookSheet sheet)
    {
        _table = sheet.SourceTable;
        _view = _table?.DefaultView;
        if (sheet.Columns.Count > 0)
        {
            _columns = sheet.Columns.ToList();
            FilterColumnCombo.ItemsSource = _columns;
        }

        _columnFilters.Clear();
        foreach (var kv in sheet.Filters)
        {
            _columnFilters[kv.Key] = kv.Value;
        }

        _columnOverrides.Clear();
        _columnOverrides.AddRange(sheet.Overrides);
        RefreshHeaderFilterIcons();
    }

    private void LoadOverridesFor(WorkbookSheet sheet)
    {
        _columnOverrides.Clear();
        if (sheet.Overrides.Count > 0)
        {
            _columnOverrides.AddRange(sheet.Overrides);
            return;
        }

        if (_session is null || string.IsNullOrEmpty(sheet.Schema) || string.IsNullOrEmpty(sheet.ViewName))
        {
            return;
        }

        var saved = ColumnLayoutStore.Load(_session, sheet.Schema, sheet.ViewName);
        if (saved?.Columns is not { Count: > 0 })
        {
            return;
        }

        _columnOverrides.AddRange(saved.Columns);
        sheet.Overrides.AddRange(saved.Columns);
    }

    private void RebuildPivotsUsing(DataTable? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var sheet in _sheets.Where(s => s.IsPivot && s.Config.CanBuild))
        {
            var srcTable = sheet.PivotSource?.SourceTable
                           ?? _sheets.FirstOrDefault(s => !s.IsPivot && !s.IsExtraView)?.SourceTable;
            if (srcTable is not null && !ReferenceEquals(srcTable, source))
            {
                continue;
            }

            var view = srcTable?.DefaultView ?? source.DefaultView;
            ApplyPivotBuild(sheet, PivotEngine.Build(view, sheet.Config));
        }

        if (_activeSheet?.IsPivot == true)
        {
            Grid.ItemsSource = _activeSheet.Result?.DefaultView;
        }
    }

    private async void OpenAddViewPanel()
    {
        if (!RequireBrowserSession())
        {
            return;
        }

        PivotSidebar.Visibility = Visibility.Collapsed;
        ColumnSidebar.Visibility = Visibility.Collapsed;
        AddViewSidebar.Visibility = Visibility.Visible;

        if (_viewsCache is { Count: > 0 })
        {
            AddViewSidebar.SetViews(_viewsCache);
            return;
        }

        AddViewSidebar.ShowLoading();
        try
        {
            _viewsCache = await _api.GetViewsAsync(CancellationToken.None);
            AddViewSidebar.SetViews(_viewsCache);
        }
        catch (Exception ex)
        {
            AddViewSidebar.ShowError(ex.Message);
        }
    }

    private void OnAddViewClosed(object? sender, EventArgs e) =>
        AddViewSidebar.Visibility = Visibility.Collapsed;

    private async void OnAddViewPicked(object? sender, VistaCatalogItem vista)
    {
        AddViewSidebar.Visibility = Visibility.Collapsed;
        if (!RequireBrowserSession() || !vista.Enabled)
        {
            return;
        }

        var existing = _sheets.FirstOrDefault(s =>
            !s.IsPivot
            && string.Equals(s.Schema, vista.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.ViewName, vista.ViewName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateSheet(existing);
            return;
        }

        var loaded = _sheets.Count(s => !s.IsPivot && s.SourceTable is not null);
        if (loaded >= MaxLoadedViews)
        {
            MessageBox.Show(this,
                $"Máximo {MaxLoadedViews} vistas cargadas a la vez.\nCierre una hoja de datos con la X de su pestaña antes de abrir otra.",
                "Agregar vista", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sheet = new WorkbookSheet
        {
            Name = vista.ViewName,
            Schema = vista.Schema,
            ViewName = vista.ViewName,
            IsExtraView = true,
        };
        _sheets.Add(sheet);
        RefreshSheetTabs();
        ActivateSheet(sheet, capture: true);
        await LoadDataAsync(sheet);
    }

    private async void OnExportExcel(object sender, RoutedEventArgs e)
    {
        if (!RequireBrowserSession())
        {
            return;
        }

        var name = SanitizeFile(_activeSheet?.Name ?? _session?.View ?? "vista");
        var dialog = new SaveFileDialog
        {
            Filter = "Excel (*.xlsx)|*.xlsx",
            FileName = $"{name}_{DateTime.Now:yyyyMMdd}.xlsx",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            if (IsPivotSheet)
            {
                if (_activeSheet?.Result is null)
                {
                    MessageBox.Show(this, "Arme primero la tabla dinámica para exportarla.",
                        "Exportar", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                ExcelExporter.Save(CloneVisible(_activeSheet.Result), dialog.FileName);
                StatusText.Text = "Tabla dinámica exportada";
                return;
            }

            var jobId = _activeSheet?.LastJobId;
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                ShowOverlay("Descargando Excel...", "Obteniendo el archivo generado en el servidor", true);
                try
                {
                    await _api.DownloadExcelFileAsync(jobId, dialog.FileName, CancellationToken.None);
                    HideOverlay();
                    StatusText.Text = "Excel descargado";
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Descarga xlsx del servidor falló: {ex.Message}");
                    HideOverlay();
                }
            }

            var table = _view?.ToTable() ?? _table;
            if (table is null)
            {
                MessageBox.Show(this,
                    "Todavía no hay datos para exportar. Pulse Actualizar todo.",
                    "Descargar Excel", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ShowOverlay("Generando Excel...", "Escribiendo el archivo local", true);
            await Task.Run(() => ExcelExporter.Save(table, dialog.FileName));
            HideOverlay();
            StatusText.Text = "Excel generado";
        }
        catch (Exception ex)
        {
            HideOverlay();
            MessageBox.Show(this, ex.Message, "Descargar Excel", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static DataTable CloneVisible(DataTable source)
    {
        var copy = source.Clone();
        foreach (DataColumn col in copy.Columns.Cast<DataColumn>().ToList())
        {
            if (PivotEngine.IsHiddenColumn(col.ColumnName))
            {
                copy.Columns.Remove(col);
            }
        }

        foreach (DataRow row in source.Rows)
        {
            var next = copy.NewRow();
            foreach (DataColumn col in copy.Columns)
            {
                next[col.ColumnName] = row[col.ColumnName];
            }

            copy.Rows.Add(next);
        }

        return copy;
    }

    private static string SanitizeFile(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        return new string(chars);
    }

    private void OnFormatCop(object sender, RoutedEventArgs e) =>
        ApplySelectedColumnType(ColumnDataKind.Moneda);

    private void OnFormatPercent(object sender, RoutedEventArgs e) =>
        ApplySelectedColumnType(ColumnDataKind.Porcentaje);

    private void OnFormatNumber(object sender, RoutedEventArgs e) =>
        ApplySelectedColumnType(ColumnDataKind.Numero);

    private void OnFormatInteger(object sender, RoutedEventArgs e) =>
        ApplySelectedColumnType(ColumnDataKind.Entero);

    private void OnFormatDate(object sender, RoutedEventArgs e) =>
        ApplySelectedColumnType(ColumnDataKind.Fecha);

    private void OnToggleTotals(object sender, RoutedEventArgs e)
    {
        _showTotals = !_showTotals;
        TotalsBar.Visibility = _showTotals ? Visibility.Visible : Visibility.Collapsed;
        RefreshTotals();
    }

    private void RefreshTotals()
    {
        if (!_showTotals)
        {
            TotalsBar.Visibility = Visibility.Collapsed;
            return;
        }

        TotalsBar.Visibility = Visibility.Visible;
        var view = IsPivotSheet ? _activeSheet?.Result?.DefaultView : _view;
        var table = view?.Table;
        if (view is null || table is null)
        {
            TotalsText.Text = "Totales  ·  sin datos";
            return;
        }

        if (view.Count > 500_000)
        {
            TotalsText.Text = $"Totales  ·  {view.Count:N0} filas (demasiadas para sumar al vuelo)";
            return;
        }

        var es = CultureInfo.GetCultureInfo("es-CO");
        var parts = new List<string>();
        foreach (DataColumn col in table.Columns)
        {
            if (PivotEngine.IsHiddenColumn(col.ColumnName)
                || col.DataType == typeof(string)
                || col.DataType == typeof(DateTime)
                || col.DataType == typeof(bool)
                || col.DataType == typeof(Guid))
            {
                continue;
            }

            double sum = 0;
            var n = 0;
            foreach (DataRowView row in view)
            {
                var raw = row[col.ColumnName];
                if (raw is DBNull or null)
                {
                    continue;
                }

                if (double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var v))
                {
                    sum += v;
                    n++;
                }
            }

            if (n == 0)
            {
                continue;
            }

            parts.Add($"{col.ColumnName}: {sum.ToString("N2", es)}");
            if (parts.Count >= 8)
            {
                break;
            }
        }

        TotalsText.Text = parts.Count == 0
            ? "Totales  ·  no hay columnas numéricas visibles"
            : "Totales  ·  " + string.Join("     ", parts);
    }

    private void OnFreezeColumns(object sender, RoutedEventArgs e)
    {
        Grid.FrozenColumnCount = Math.Min(1, Grid.Columns.Count);
        StatusText.Text = "Primera columna congelada";
    }

    private void OnUnfreezeColumns(object sender, RoutedEventArgs e)
    {
        Grid.FrozenColumnCount = 0;
        StatusText.Text = "Columnas descongeladas";
    }

    private void OnAutoFit(object sender, RoutedEventArgs e) => AutoFitAllColumns();

    private void AutoFitAllColumns()
    {
        foreach (var col in Grid.Columns)
        {
            if (col.Visibility != Visibility.Visible)
            {
                continue;
            }

            col.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
        }

        Grid.UpdateLayout();
        foreach (var col in Grid.Columns)
        {
            if (col.Visibility != Visibility.Visible)
            {
                continue;
            }

            var width = Math.Clamp(col.ActualWidth, 100, 400);
            col.Width = new DataGridLength(width);
        }
    }

    private void AutoFitSelectedColumn()
    {
        var key = ResolveSelectedColumn();
        var col = Grid.Columns.FirstOrDefault(c =>
            string.Equals(ColumnKey(c), key, StringComparison.Ordinal));
        if (col is null)
        {
            AutoFitAllColumns();
            return;
        }

        col.Width = new DataGridLength(1, DataGridLengthUnitType.Auto);
        Grid.UpdateLayout();
        col.Width = new DataGridLength(Math.Clamp(col.ActualWidth, 100, 400));
    }

    private void HideSelectedColumn()
    {
        var key = ResolveSelectedColumn();
        var col = Grid.Columns.FirstOrDefault(c =>
            string.Equals(ColumnKey(c), key, StringComparison.Ordinal));
        if (col is null)
        {
            PromptSelectColumn();
            return;
        }

        col.Visibility = Visibility.Collapsed;
        RefreshColumnPanel();
    }

    private void OnShowAllColumns(object sender, RoutedEventArgs e)
    {
        foreach (var col in Grid.Columns)
        {
            col.Visibility = Visibility.Visible;
        }

        RefreshColumnPanel();
    }

    private void OnShowColumnPanel(object sender, RoutedEventArgs e)
    {
        PivotSidebar.Visibility = Visibility.Collapsed;
        AddViewSidebar.Visibility = Visibility.Collapsed;
        ColumnSidebar.Visibility = Visibility.Visible;
        RefreshColumnPanel();
    }

    private void OnCloseColumnPanel(object sender, RoutedEventArgs e) =>
        ColumnSidebar.Visibility = Visibility.Collapsed;

    private void RefreshColumnPanel()
    {
        if (ColumnSidebar.Visibility != Visibility.Visible)
        {
            return;
        }

        ColumnList.ItemsSource = Grid.Columns
            .Select(c => new ColumnVisibilityItem
            {
                Name = ColumnKey(c) ?? "",
                Visible = c.Visibility == Visibility.Visible,
            })
            .Where(i => !string.IsNullOrEmpty(i.Name) && !PivotEngine.IsHiddenColumn(i.Name))
            .ToList();
    }

    private void OnColumnVisibleChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: ColumnVisibilityItem item })
        {
            return;
        }

        var col = Grid.Columns.FirstOrDefault(c =>
            string.Equals(ColumnKey(c), item.Name, StringComparison.Ordinal));
        if (col is not null)
        {
            col.Visibility = item.Visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnRowHeightChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Grid is null
            || RowHeightCombo?.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !double.TryParse(tag, NumberStyles.Any, CultureInfo.InvariantCulture, out var height))
        {
            return;
        }

        Grid.RowHeight = height;
    }
}
