using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SaraBI.Controls;
using SaraBI.Dialogs;
using SaraBI.Models;
using SaraBI.Services;

namespace SaraBI;

public partial class MainWindow : Window
{
    private const int MaxExportRows = 3_000_000;
    private readonly ApiClient _api = new();
    private readonly string? _protocolUrl;
    private DataTable? _table;
    private DataView? _view;
    private List<FabricColumn> _columns = new();
    private DateRangeFilter? _dateFilter;
    private CancellationTokenSource? _loadCts;
    private LaunchSession? _session;
    private readonly Stopwatch _elapsed = new();
    private bool _showTotals;
    private readonly Dictionary<string, ColumnAutoFilter> _columnFilters = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(string? protocolUrl)
    {
        InitializeComponent();
        _protocolUrl = protocolUrl;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _loadCts?.Cancel();
            _api.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_protocolUrl))
        {
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        await LoadFromProtocolAsync(_protocolUrl);
    }

    private async Task LoadFromProtocolAsync(string protocolUrl)
    {
        if (!ProtocolHandler.TryParse(protocolUrl, out var ticket, out var apiUrl))
        {
            ShowError("El enlace de escritorio no es válido. Vuelva a abrir la vista desde la plataforma.");
            return;
        }

        ShowOverlay("Conectando...", "Canjeando ticket de sesión", true);
        try
        {
            _session = await _api.ClaimAsync(apiUrl, ticket, CancellationToken.None);
            AppLog.Info($"Claim OK user={_session.User} view={_session.Schema}.{_session.View} api={_session.ApiUrl}");
            TitleText.Text = _session.ViewLabel;
            SubtitleText.Text = $"{_session.Schema}.{_session.View}"
                                + (string.IsNullOrWhiteSpace(_session.User) ? "" : $"  ·  {_session.User}");
            Title = $"JadeOne Desktop — {_session.ViewLabel}";
            SheetTabText.Text = _session.ViewLabel;
            EmptyTitle.Text = _session.ViewLabel;
            SavedBadge.Text = "Guardado";
            await LoadDataAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        _elapsed.Restart();
        RefreshButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        CsvButton.IsEnabled = false;
        CancelButton.IsEnabled = true;

        try
        {
            ShowOverlay("Preparando columnas...", "Consultando metadatos de la vista", true);
            _columns = await _api.GetColumnsAsync(ct);
            FilterColumnCombo.ItemsSource = _columns;
            if (_columns.Count > 0 && FilterColumnCombo.SelectedIndex < 0)
            {
                FilterColumnCombo.SelectedIndex = 0;
            }

            ShowOverlay("Exportando datos...", "Solicitando gzip a Fabric", true);
            var start = await _api.StartExportAsync(MaxExportRows, _dateFilter, ct);

            if (string.Equals(start.R2Status, "too_big", StringComparison.OrdinalIgnoreCase))
            {
                HideOverlay();
                if (!AskDateFilter(start.RowCount))
                {
                    StatusText.Text = "Carga cancelada: se requiere filtro de fechas.";
                    RefreshButton.IsEnabled = true;
                    CancelButton.IsEnabled = false;
                    return;
                }

                ShowOverlay("Exportando con filtro...", "Aplicando rango de fechas", true);
                start = await _api.StartExportAsync(MaxExportRows, _dateFilter, ct);
            }

            if (string.Equals(start.R2Status, "generating", StringComparison.OrdinalIgnoreCase))
            {
                start = await WaitForR2ThenExportAsync(start, ct);
            }

            if (string.Equals(start.R2Status, "too_big", StringComparison.OrdinalIgnoreCase))
            {
                HideOverlay();
                if (!AskDateFilter(start.RowCount))
                {
                    StatusText.Text = "Carga cancelada: se requiere filtro de fechas.";
                    RefreshButton.IsEnabled = true;
                    CancelButton.IsEnabled = false;
                    return;
                }

                start = await _api.StartExportAsync(MaxExportRows, _dateFilter, ct);
            }

            if (string.IsNullOrWhiteSpace(start.JobId))
            {
                throw new InvalidOperationException(start.Message ?? "No se pudo iniciar la descarga.");
            }

            var jobId = start.JobId;
            ShowOverlay("Procesando...", start.Message ?? "Fabric está exportando los datos", true);
            var completed = await WaitForJobAsync(jobId, ct);
            var rowsHint = completed?.Rows ?? start.Rows ?? 0;

            ShowOverlay("Descargando...", $"{rowsHint:N0} registros", false);
            OverlayProgress.IsIndeterminate = true;
            await using var stream = await _api.DownloadExportAsync(jobId, ct);

            ShowOverlay("Leyendo archivo...", "Parseando gzip / NDJSON", true);
            var progress = new Progress<(int rows, string message)>(p =>
            {
                OverlayMessage.Text = p.message;
                StatusText.Text = p.message;
            });

            var table = await Task.Run(
                () => DataFileParser.Parse(
                    stream,
                    progress,
                    _columns.Select(c => c.Name).ToList(),
                    rowsHint > int.MaxValue ? int.MaxValue : (int)rowsHint),
                ct);
            BindTable(table);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            HideOverlay();
            EmptyState.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
            ExportButton.IsEnabled = table.Rows.Count > 0;
            CsvButton.IsEnabled = table.Rows.Count > 0;
            CancelButton.IsEnabled = false;
            _elapsed.Stop();
            UpdateMetrics();
            StatusText.Text = $"{table.Rows.Count:N0} registros cargados";
            HintText.Text = $"{_elapsed.Elapsed.TotalSeconds:0} seg. carga";
            AppLog.Info($"Carga OK rows={table.Rows.Count} elapsed={_elapsed.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException)
        {
            HideOverlay();
            StatusText.Text = "Actualización cancelada";
            RefreshButton.IsEnabled = _session is not null;
            CancelButton.IsEnabled = false;
            if (_table is null)
            {
                EmptyState.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Carga fallida", ex);
            ShowError(ex.Message);
            RefreshButton.IsEnabled = _session is not null;
            CancelButton.IsEnabled = false;
        }
    }

    private async Task<ExportStartResponse> WaitForR2ThenExportAsync(ExportStartResponse start, CancellationToken ct)
    {
        var estimated = start.EstimatedS ?? 60;
        OverlayTitle.Text = "Preparando datos...";
        OverlayMessage.Text = $"Generando archivo de la vista (~{estimated}s). Puede tardar en la primera carga.";
        AppLog.Info($"R2 generating { _session?.Schema}.{_session?.View} estimated={estimated}s");
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(5000, ct);
            var status = await _api.GetR2StatusAsync(ct);
            var r2 = status.R2Status ?? "";
            AppLog.Info($"R2 poll status={r2} msg={status.Message}");
            if (r2 is "ready" or "ready_stale")
            {
                OverlayMessage.Text = "Datos listos, descargando...";
                return await _api.StartExportAsync(MaxExportRows, _dateFilter, ct);
            }

            if (r2 == "too_big")
            {
                return new ExportStartResponse
                {
                    Success = true,
                    R2Status = "too_big",
                    RowCount = status.RowCount,
                    Message = status.Message,
                };
            }

            if (r2 == "unavailable")
            {
                AppLog.Warn("R2 unavailable, fallback a stream");
                return await _api.StartExportAsync(MaxExportRows, _dateFilter, ct);
            }

            OverlayMessage.Text = $"Generando archivo de la vista (~{status.EstimatedS ?? estimated}s). Espere, no cierre la ventana.";
        }

        throw new OperationCanceledException();
    }

    private async Task<ExportStatusData?> WaitForJobAsync(string jobId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var data = await _api.GetExportStatusAsync(jobId, ct);
            var status = data?.Status ?? "";
            if (status == "completed")
            {
                return data;
            }

            if (status == "failed")
            {
                throw new InvalidOperationException(data?.Error ?? data?.Message ?? "El export falló en el servidor.");
            }

            var rows = data?.Rows ?? 0;
            OverlayMessage.Text = data?.Message ?? (rows > 0
                ? $"Exportando {rows:N0} filas..."
                : "Exportando datos desde Fabric...");
            await Task.Delay(2500, ct);
        }

        throw new OperationCanceledException();
    }

    private bool AskDateFilter(long? rowCount)
    {
        var dateCols = _columns.Where(c => c.IsDate).ToList();
        if (dateCols.Count == 0)
        {
            MessageBox.Show(this,
                $"La vista tiene ~{rowCount ?? 0:N0} registros y no se encontraron columnas de fecha para filtrar.",
                "JadeOne Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var dialog = new DateFilterDialog(dateCols) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is null)
        {
            return false;
        }

        _dateFilter = dialog.Result;
        return true;
    }

    private void BindTable(DataTable table)
    {
        _columnFilters.Clear();
        _table?.Dispose();
        _table = table;
        _view = table.DefaultView;
        Grid.ItemsSource = _view;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyFilters();
    }

    private void OnColumnFilter(object sender, RoutedEventArgs e) => ApplyFilters();

    private void ApplyFilters()
    {
        if (_view is null || _table is null)
        {
            return;
        }

        var parts = new List<string>();
        var global = SearchBox.Text.Trim();
        if (!string.IsNullOrEmpty(global) && _table.Columns.Count > 0)
        {
            var escaped = EscapeFilter(global);
            var ors = _table.Columns.Cast<DataColumn>()
                .Select(c => $"CONVERT([{EscapeCol(c.ColumnName)}], 'System.String') LIKE '%{escaped}%'");
            parts.Add("(" + string.Join(" OR ", ors) + ")");
        }

        var colText = ColumnFilterBox.Text.Trim();
        if (!string.IsNullOrEmpty(colText) && FilterColumnCombo.SelectedItem is FabricColumn col)
        {
            var name = _table.Columns.Cast<DataColumn>()
                .FirstOrDefault(c => string.Equals(c.ColumnName, col.Name, StringComparison.OrdinalIgnoreCase))
                ?.ColumnName;
            if (!string.IsNullOrEmpty(name))
            {
                parts.Add($"CONVERT([{EscapeCol(name)}], 'System.String') LIKE '%{EscapeFilter(colText)}%'");
            }
        }

        foreach (var kv in _columnFilters)
        {
            var clause = BuildExcelFilter(kv.Key, kv.Value);
            if (!string.IsNullOrEmpty(clause))
            {
                parts.Add(clause);
            }
        }

        try
        {
            _view.RowFilter = string.Join(" AND ", parts);
            UpdateMetrics();
        }
        catch
        {
            _view.RowFilter = "";
        }
    }

    private void OnExportExcel(object sender, RoutedEventArgs e)
    {
        if (_view is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "Excel (*.xlsx)|*.xlsx",
            FileName = $"{_session?.View ?? "vista"}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var table = _view.ToTable();
            ExcelExporter.Save(table, dialog.FileName);
            StatusText.Text = $"Exportado: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error al exportar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExportCsv(object sender, RoutedEventArgs e)
    {
        if (_view is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"{_session?.View ?? "vista"}_{DateTime.Now:yyyyMMdd_HHmm}.csv",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ExcelExporter.SaveCsv(_view.ToTable(), dialog.FileName);
            StatusText.Text = $"Exportado: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error al exportar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateMetrics()
    {
        var total = _table?.Rows.Count ?? 0;
        var filtered = _view?.Count ?? total;

        var filters = 0;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) filters++;
        if (!string.IsNullOrWhiteSpace(ColumnFilterBox.Text)) filters++;
        filters += _columnFilters.Count;

        var gcMb = Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 1);
        MetricsText.Text = total == 0
            ? "— total   — mostrados   0 filtro(s)"
            : $"{total:N0} total   {filtered:N0} mostrados   {filters} filtro(s)   {gcMb} MB CLR";

        if (_elapsed.IsRunning || _elapsed.Elapsed.TotalSeconds > 0)
        {
            HintText.Text = $"{_elapsed.Elapsed.TotalSeconds:0} seg. carga";
        }

        UpdateTotals();
    }

    private void ShowOverlay(string title, string message, bool indeterminate)
    {
        OverlayTitle.Text = title;
        OverlayMessage.Text = message;
        OverlayProgress.IsIndeterminate = indeterminate;
        Overlay.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        StatusText.Text = message;
    }

    private void HideOverlay() => Overlay.Visibility = Visibility.Collapsed;

    private void ShowError(string message)
    {
        HideOverlay();
        EmptyState.Visibility = Visibility.Collapsed;
        StatusText.Text = message;
        MessageBox.Show(this, message, "JadeOne Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshButton.IsEnabled = _session is not null;
        CancelButton.IsEnabled = false;
    }

    private void OnRibbonTab(object sender, RoutedEventArgs e)
    {
        if (RibbonDatos is null)
        {
            return;
        }

        RibbonDatos.Visibility = TabDatos.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RibbonFiltros.Visibility = TabFiltros.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RibbonVista.Visibility = TabVista.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RibbonFormato.Visibility = TabFormato.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RibbonFormulas.Visibility = TabFormulas.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RibbonAnalisis.Visibility = TabAnalisis.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShowFiltersTab(object sender, RoutedEventArgs e)
    {
        TabFiltros.IsChecked = true;
    }

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        ColumnFilterBox.Text = "";
        _columnFilters.Clear();
        ApplyFilters();
        RefreshHeaderFilterIcons();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _loadCts?.Cancel();
        StatusText.Text = "Actualización cancelada";
    }

    private void OnAddViewHint(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "En escritorio se abre una vista desde el listado web (botón escritorio). Para varias hojas use la vista Excel en el navegador.",
            "Agregar vista", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnFreeze(object sender, RoutedEventArgs e) => Grid.FrozenColumnCount = Math.Min(2, Grid.Columns.Count);

    private void OnUnfreeze(object sender, RoutedEventArgs e) => Grid.FrozenColumnCount = 0;

    private void OnToggleTotals(object sender, RoutedEventArgs e)
    {
        _showTotals = !_showTotals;
        TotalsBar.Visibility = _showTotals ? Visibility.Visible : Visibility.Collapsed;
        UpdateTotals();
    }

    private void OnZoomChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GridZoom is null || ZoomLabel is null || ZoomStatus is null)
        {
            return;
        }

        var scale = e.NewValue / 100d;
        GridZoom.ScaleX = scale;
        GridZoom.ScaleY = scale;
        ZoomLabel.Text = $"{e.NewValue:0}%";
        ZoomStatus.Text = $"{e.NewValue:0}%";
    }

    private void OnLoadingRow(object sender, DataGridRowEventArgs e)
    {
        e.Row.Header = (e.Row.GetIndex() + 1).ToString();
    }

    private void OnAutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        e.Column.MinWidth = 88;
        var name = e.PropertyName;
        var header = new ExcelColumnHeader(name);
        header.SetFilterActive(_columnFilters.ContainsKey(name));
        header.FilterClicked += OnColumnHeaderFilter;
        e.Column.Header = header;
        e.Column.SortMemberPath = name;
        if (e.Column is DataGridTextColumn text)
        {
            text.ElementStyle = new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.PaddingProperty, new Thickness(4, 0, 4, 0)),
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                },
            };
        }
    }

    private void OnCurrentCellChanged(object? sender, EventArgs e)
    {
        if (Grid.CurrentCell.Column is null || Grid.CurrentItem is not DataRowView row)
        {
            CellRefText.Text = "A1";
            FormulaBox.Text = "";
            return;
        }

        var colIndex = Grid.Columns.IndexOf(Grid.CurrentCell.Column);
        var colLetter = ColLetter(colIndex);
        var rowIndex = Grid.Items.IndexOf(Grid.CurrentItem) + 1;
        var name = Grid.CurrentCell.Column.SortMemberPath;
        if (string.IsNullOrEmpty(name) && Grid.CurrentCell.Column.Header is ExcelColumnHeader header)
        {
            name = header.ColumnName;
        }

        CellRefText.Text = $"{colLetter}{Math.Max(rowIndex, 1)}";
        FormulaBox.Text = !string.IsNullOrEmpty(name) && row.Row.Table.Columns.Contains(name)
            ? Convert.ToString(row[name], CultureInfo.CurrentCulture) ?? ""
            : "";
    }

    private void UpdateTotals()
    {
        if (!_showTotals || _view is null || _table is null)
        {
            TotalsText.Text = "";
            return;
        }

        var numeric = _table.Columns.Cast<DataColumn>()
            .Where(c => c.DataType == typeof(int) || c.DataType == typeof(long)
                        || c.DataType == typeof(decimal) || c.DataType == typeof(double)
                        || c.DataType == typeof(float))
            .Take(4)
            .ToList();

        var parts = new List<string> { $"Σ  {_view.Count:N0} filas" };
        foreach (var col in numeric)
        {
            decimal sum = 0;
            foreach (DataRowView row in _view)
            {
                if (row[col.ColumnName] is not DBNull and not null
                    && decimal.TryParse(Convert.ToString(row[col.ColumnName], CultureInfo.InvariantCulture), out var n))
                {
                    sum += n;
                }
            }

            parts.Add($"{col.ColumnName}={sum:N0}");
        }

        TotalsText.Text = string.Join("   ·   ", parts);
    }

    private static string ColLetter(int index)
    {
        if (index < 0)
        {
            return "A";
        }

        var n = index + 1;
        var letters = new StringBuilder();
        while (n > 0)
        {
            n--;
            letters.Insert(0, (char)('A' + (n % 26)));
            n /= 26;
        }

        return letters.ToString();
    }

    private void OnColumnHeaderFilter(object? sender, EventArgs e)
    {
        if (sender is not ExcelColumnHeader header || _table is null)
        {
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        List<FilterValueItem> items;
        bool textOnly;
        try
        {
            (items, textOnly) = CollectFilterValues(header.ColumnName);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        _columnFilters.TryGetValue(header.ColumnName, out var current);
        var win = new ExcelAutoFilterWindow(header.ColumnName, items, current, textOnly)
        {
            Owner = this,
        };
        PlaceBelow(win, header);
        if (win.ShowDialog() != true)
        {
            return;
        }

        if (win.Sort is { } dir && _view is not null)
        {
            var safe = EscapeCol(header.ColumnName);
            _view.Sort = $"[{safe}] {(dir == ListSortDirection.Ascending ? "ASC" : "DESC")}";
        }

        if (win.Cleared)
        {
            _columnFilters.Remove(header.ColumnName);
        }
        else if (win.Sort is null)
        {
            if (win.Result is null)
            {
                _columnFilters.Remove(header.ColumnName);
            }
            else
            {
                _columnFilters[header.ColumnName] = win.Result;
            }
        }

        ApplyFilters();
        RefreshHeaderFilterIcons();
    }

    private (List<FilterValueItem> items, bool textOnly) CollectFilterValues(string column)
    {
        const int maxValues = 2500;
        var items = new List<FilterValueItem>();
        if (_table is null || !_table.Columns.Contains(column))
        {
            return (items, true);
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        var blanks = false;
        foreach (DataRow row in _table.Rows)
        {
            var raw = row[column];
            var s = raw is DBNull or null ? "" : Convert.ToString(raw, CultureInfo.CurrentCulture) ?? "";
            if (s.Length == 0)
            {
                blanks = true;
                continue;
            }

            set.Add(s);
            if (set.Count > maxValues)
            {
                return (items, true);
            }
        }

        items.AddRange(set
            .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase)
            .Select(v => new FilterValueItem { Value = v, Display = v, IsChecked = true }));
        if (blanks)
        {
            items.Insert(0, new FilterValueItem { Value = "", Display = "(En blanco)", IsChecked = true });
        }

        return (items, false);
    }

    private string? BuildExcelFilter(string column, ColumnAutoFilter filter)
    {
        if (!_table?.Columns.Contains(column) ?? true)
        {
            return null;
        }

        var name = $"CONVERT([{EscapeCol(column)}], 'System.String')";
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(filter.Contains))
        {
            parts.Add($"{name} LIKE '%{EscapeFilter(filter.Contains)}%'");
        }

        if (filter.Selected is not null)
        {
            var valueParts = new List<string>();
            if (filter.IncludeBlanks)
            {
                valueParts.Add($"{name} = ''");
            }

            foreach (var chunk in filter.Selected.Chunk(200))
            {
                var listed = string.Join(",", chunk.Select(v => $"'{v.Replace("'", "''")}'"));
                valueParts.Add($"{name} IN ({listed})");
            }

            parts.Add(valueParts.Count == 0 ? "1=0" : "(" + string.Join(" OR ", valueParts) + ")");
        }

        return parts.Count == 0 ? null : "(" + string.Join(" AND ", parts) + ")";
    }

    private void RefreshHeaderFilterIcons()
    {
        foreach (var col in Grid.Columns)
        {
            if (col.Header is ExcelColumnHeader header)
            {
                header.SetFilterActive(_columnFilters.TryGetValue(header.ColumnName, out var f) && f.IsActive);
            }
        }
    }

    private static void PlaceBelow(Window window, FrameworkElement target)
    {
        var source = PresentationSource.FromVisual(target);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var screen = target.PointToScreen(new Point(0, target.ActualHeight));
        var point = fromDevice.Transform(screen);
        window.Left = point.X;
        window.Top = point.Y;
        if (window.Left + window.Width > SystemParameters.WorkArea.Right)
        {
            window.Left = Math.Max(0, SystemParameters.WorkArea.Right - window.Width);
        }

        if (window.Top + 440 > SystemParameters.WorkArea.Bottom)
        {
            window.Top = Math.Max(0, point.Y - 440);
        }
    }

    private static string EscapeFilter(string value) =>
        value.Replace("'", "''").Replace("[", "[[").Replace("]", "]]").Replace("%", "[%]").Replace("*", "[*]");

    private static string EscapeCol(string name) => name.Replace("]", "]]");
}
