using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SaraBI.Controls;
using SaraBI.Converters;
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
    private readonly Dictionary<string, ColumnAutoFilter> _columnFilters = new(StringComparer.OrdinalIgnoreCase);
    private ExcelAutoFilterWindow? _openFilter;
    private string? _openFilterColumn;
    private readonly List<WorkbookSheet> _sheets = new();
    private WorkbookSheet? _activeSheet;
    private bool _didRestorePivots;
    private DispatcherTimer? _pivotSaveTimer;
    private readonly List<ColumnOverride> _columnOverrides = new();
    private string? _selectedColumnName;

    public MainWindow(string? protocolUrl)
    {
        InitializeComponent();
        AttachColumnHeaderInteractions();
        _protocolUrl = protocolUrl;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _pivotSaveTimer?.Stop();
            SavePivotsNow();
            _openFilter?.Close();
            _loadCts?.Cancel();
            foreach (var sheet in _sheets)
            {
                sheet.Result?.Dispose();
            }

            _api.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_protocolUrl)
            || !ProtocolHandler.TryParse(_protocolUrl, out _, out _))
        {
            BlockDirectLaunch();
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        await LoadFromProtocolAsync(_protocolUrl);
    }

    private void BlockDirectLaunch()
    {
        AppLog.Warn($"Arranque sin enlace web args={_protocolUrl ?? "(ninguno)"}");
        MessageBox.Show(
            this,
            "No se puede usar JadeOne Desktop desde Excel ni ejecutando el programa directo.\n\n"
            + "Ábralo desde JadeOne en el navegador: entre a la plataforma, abra la vista y use el botón de escritorio.",
            "Abra JadeOne desde el navegador",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        Close();
    }

    private bool RequireBrowserSession()
    {
        if (_session is not null)
        {
            return true;
        }

        BlockDirectLaunch();
        return false;
    }

    private async Task LoadFromProtocolAsync(string protocolUrl)
    {
        if (!ProtocolHandler.TryParse(protocolUrl, out var ticket, out var env))
        {
            BlockDirectLaunch();
            return;
        }

        var apiUrl = OfficialApi.Resolve(env);
        ShowOverlay("Conectando...", "Canjeando ticket de sesión", true);
        try
        {
            _session = await _api.ClaimAsync(apiUrl, ticket, CancellationToken.None);
            AppLog.Info($"Claim OK user={_session.User} view={_session.Schema}.{_session.View} env={env} api={_session.ApiUrl}");
            TitleText.Text = _session.ViewLabel;
            SubtitleText.Text = $"{_session.Schema}.{_session.View}"
                                + (string.IsNullOrWhiteSpace(_session.User) ? "" : $"  ·  {_session.User}");
            Title = $"JadeOne Desktop — {_session.ViewLabel}";
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
        if (!RequireBrowserSession())
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

    private bool IsPivotSheet => _activeSheet?.IsPivot == true;

    private void BindTable(DataTable table)
    {
        _openFilter?.Close();
        _columnFilters.Clear();
        _selectedColumnName = null;
        _table?.Dispose();
        _table = table;
        ColumnMutator.StampOriginalNames(_table);
        ApplyColumnLayout();
        _view = table.DefaultView;
        EnsureDataSheet();
        WorkbookSheet? prefer = null;
        if (!_didRestorePivots)
        {
            prefer = TryRestorePivots();
            _didRestorePivots = true;
        }
        else if (_activeSheet is not null && _sheets.Contains(_activeSheet))
        {
            prefer = _activeSheet;
        }

        RebuildAllPivots(bindActive: false);
        ActivateSheet(prefer ?? _sheets.First(s => !s.IsPivot));
    }

    private void EnsureDataSheet()
    {
        var data = _sheets.FirstOrDefault(s => !s.IsPivot);
        if (data is null)
        {
            data = new WorkbookSheet
            {
                Name = string.IsNullOrWhiteSpace(_session?.ViewLabel) ? "Datos" : _session!.ViewLabel,
                IsPivot = false,
            };
            _sheets.Insert(0, data);
        }
        else if (!string.IsNullOrWhiteSpace(_session?.ViewLabel))
        {
            data.Name = _session.ViewLabel;
        }
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
            var name = MapSourceColumn(col.Name);
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
            RebuildAllPivots();
        }
        catch
        {
            _view.RowFilter = "";
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
        var pivotBit = IsPivotSheet && _activeSheet?.Result is not null
            ? $"   {_activeSheet.Result.Rows.Count:N0} grupos"
            : "";
        MetricsText.Text = total == 0
            ? "— total   — mostrados   0 filtro(s)"
            : $"{total:N0} total   {filtered:N0} mostrados   {filters} filtro(s){pivotBit}   {gcMb} MB CLR";

        if (_elapsed.IsRunning || _elapsed.Elapsed.TotalSeconds > 0)
        {
            var load = $"{_elapsed.Elapsed.TotalSeconds:0} seg. carga";
            HintText.Text = _sheets.Count > 1 ? $"{_sheets.Count} hojas · {load}" : load;
        }
        else if (_sheets.Count > 1)
        {
            HintText.Text = $"{_sheets.Count} hojas";
        }
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
        RibbonAnalisis.Visibility = TabAnalisis.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShowFiltersTab(object sender, RoutedEventArgs e)
    {
        TabFiltros.IsChecked = true;
    }

    private void OnShowPivot(object sender, RoutedEventArgs e)
    {
        if (!RequireBrowserSession())
        {
            return;
        }

        if (_table is null || _view is null)
        {
            MessageBox.Show(this,
                "Cargue primero una vista (Datos → Actualizar todo) para armar la tabla dinámica.",
                "Tabla dinámica", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EnsureDataSheet();
        var n = _sheets.Count(s => s.IsPivot) + 1;
        var sheet = new WorkbookSheet
        {
            Name = n == 1 ? "Tabla dinámica1" : $"Tabla dinámica{n}",
            IsPivot = true,
        };
        _sheets.Add(sheet);
        ActivateSheet(sheet);
        StatusText.Text = "Hoja nueva. Arrastre campos a Filas, Columnas o Valores.";
        ScheduleSavePivots();
    }

    private void OnSheetTabClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WorkbookSheet sheet })
        {
            ActivateSheet(sheet);
            ScheduleSavePivots();
        }
    }

    private void OnCloseSheetTab(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: WorkbookSheet sheet } || !sheet.IsPivot)
        {
            return;
        }

        var index = _sheets.IndexOf(sheet);
        sheet.Result?.Dispose();
        _sheets.Remove(sheet);
        var next = _activeSheet == sheet
            ? (index > 0 ? _sheets[index - 1] : _sheets[0])
            : _activeSheet;
        if (next is not null)
        {
            ActivateSheet(next);
        }
        else
        {
            RefreshSheetTabs();
        }

        ScheduleSavePivots();
    }

    private void ActivateSheet(WorkbookSheet sheet)
    {
        foreach (var s in _sheets)
        {
            s.IsActive = ReferenceEquals(s, sheet);
        }

        _activeSheet = sheet;
        RefreshSheetTabs();
        _openFilter?.Close();

        if (sheet.IsPivot)
        {
            if (_table is not null && _view is not null)
            {
                PivotSidebar.AttachConfig(sheet.Config);
                PivotSidebar.SetFields(_table.Columns.Cast<DataColumn>().Select(c => c.ColumnName), _view);
            }

            PivotSidebar.Visibility = Visibility.Visible;
            Grid.ItemsSource = sheet.Result?.DefaultView;
            TabAnalisis.IsChecked = true;
            StatusText.Text = sheet.Config.CanBuild
                ? $"Tabla dinámica · {sheet.Snapshot?.LeafCount ?? sheet.Result?.Rows.Count ?? 0:N0} grupos"
                : "Arrastre campos a Filas, Columnas o Valores.";
        }
        else
        {
            PivotSidebar.Visibility = Visibility.Collapsed;
            if (_view is not null)
            {
                Grid.ItemsSource = _view;
            }

            TabDatos.IsChecked = true;
            StatusText.Text = $"{_view?.Count ?? 0:N0} registros";
        }

        UpdateMetrics();
    }

    private void RefreshSheetTabs()
    {
        if (SheetTabStrip is null)
        {
            return;
        }

        SheetTabStrip.ItemsSource = null;
        SheetTabStrip.ItemsSource = _sheets.ToList();
    }

    private void OnClearPivot(object sender, RoutedEventArgs e)
    {
        ClearActivePivot();
    }

    private void OnPivotCleared(object? sender, EventArgs e) => ClearActivePivot();

    private void ClearActivePivot()
    {
        if (_activeSheet is not { IsPivot: true })
        {
            return;
        }

        PivotSidebar.Reset();
        _activeSheet.Result?.Dispose();
        _activeSheet.Result = null;
        _activeSheet.Snapshot = null;
        Grid.ItemsSource = null;
        UpdateMetrics();
        StatusText.Text = "Tabla dinámica limpiada. Arrastre campos para volver a armarla.";
        ScheduleSavePivots();
    }

    private void OnPivotClosed(object? sender, EventArgs e)
    {
        PivotSidebar.Visibility = Visibility.Collapsed;
    }

    private void OnPivotConfigChanged(object? sender, EventArgs e)
    {
        RebuildPivot();
        ScheduleSavePivots();
    }

    private void OnSavePivot(object sender, RoutedEventArgs e)
    {
        if (!RequireBrowserSession())
        {
            return;
        }

        if (_session is null)
        {
            return;
        }

        if (!_sheets.Any(s => s.IsPivot && s.Config.CanBuild))
        {
            MessageBox.Show(this,
                "Arme primero una tabla dinámica (Filas, Columnas o Valores) para guardarla en este equipo.",
                "Guardar tabla dinámica", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (SavePivotsNow())
        {
            SavedBadge.Text = "Tabla guardada";
            StatusText.Text = "Tabla dinámica guardada en este equipo. Se abrirá sola al volver a esta vista.";
        }
    }

    private void OnOpenSavedPivot(object sender, RoutedEventArgs e)
    {
        if (!RequireBrowserSession() || _session is null)
        {
            return;
        }

        if (_table is null || _view is null)
        {
            MessageBox.Show(this,
                "Cargue primero la vista para abrir la tabla dinámica guardada.",
                "Abrir tabla dinámica", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!PivotStateStore.Exists(_session))
        {
            MessageBox.Show(this,
                "No hay una tabla dinámica guardada en este equipo para esta vista.",
                "Abrir tabla dinámica", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EnsureDataSheet();
        RemovePivotSheets();
        var opened = TryRestorePivots();
        if (opened is null)
        {
            MessageBox.Show(this,
                "El archivo guardado no tiene campos que coincidan con esta vista.",
                "Abrir tabla dinámica", MessageBoxButton.OK, MessageBoxImage.Warning);
            ActivateSheet(_sheets.First(s => !s.IsPivot));
            return;
        }

        RebuildAllPivots(bindActive: false);
        ActivateSheet(opened);
        SavedBadge.Text = "Tabla guardada";
        StatusText.Text = "Tabla dinámica restaurada desde este equipo.";
    }

    private WorkbookSheet? TryRestorePivots()
    {
        if (_session is null || _table is null)
        {
            return null;
        }

        var saved = PivotStateStore.Load(_session);
        if (saved is null)
        {
            return null;
        }

        string? MapColumn(string name) => MapSourceColumn(name);

        WorkbookSheet? firstBuilt = null;
        WorkbookSheet? namedActive = null;
        foreach (var item in saved.PivotSheets)
        {
            var sheet = new WorkbookSheet
            {
                Name = string.IsNullOrWhiteSpace(item.Name) ? "Tabla dinámica" : item.Name,
                IsPivot = true,
            };
            RestorePivotLayout(sheet, item);
            sheet.Config.ApplySaved(item.Config, MapColumn);
            if (_view is not null)
            {
                sheet.Config.ExpandBareDateFields(_table, _view);
            }

            if (!sheet.Config.CanBuild)
            {
                continue;
            }

            _sheets.Add(sheet);
            firstBuilt ??= sheet;
            if (!string.IsNullOrWhiteSpace(saved.ActiveSheetName)
                && string.Equals(sheet.Name, saved.ActiveSheetName, StringComparison.OrdinalIgnoreCase))
            {
                namedActive = sheet;
            }
        }

        var target = namedActive ?? firstBuilt;
        if (target is not null)
        {
            SavedBadge.Text = "Tabla guardada";
            AppLog.Info($"Tabla dinámica restaurada {_session.Schema}.{_session.View} sheets={_sheets.Count(s => s.IsPivot)}");
        }

        return target;
    }

    private void RemovePivotSheets()
    {
        foreach (var sheet in _sheets.Where(s => s.IsPivot).ToList())
        {
            sheet.Result?.Dispose();
            _sheets.Remove(sheet);
        }
    }

    private void ScheduleSavePivots()
    {
        if (_session is null)
        {
            return;
        }

        _pivotSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _pivotSaveTimer.Tick -= OnPivotSaveTick;
        _pivotSaveTimer.Tick += OnPivotSaveTick;
        _pivotSaveTimer.Stop();
        _pivotSaveTimer.Start();
    }

    private void OnPivotSaveTick(object? sender, EventArgs e)
    {
        _pivotSaveTimer?.Stop();
        SavePivotsNow();
    }

    private bool SavePivotsNow()
    {
        if (_session is null)
        {
            return false;
        }

        var built = _sheets.Where(s => s.IsPivot && s.Config.CanBuild).ToList();
        if (built.Count == 0)
        {
            PivotStateStore.Delete(_session);
            return false;
        }

        try
        {
            var state = new SavedPivotState
            {
                Schema = _session.Schema,
                View = _session.View,
                ViewLabel = _session.ViewLabel,
                User = _session.User,
                SavedAt = DateTime.Now,
                ActiveSheetName = _activeSheet is { IsPivot: true } ? _activeSheet.Name : built[0].Name,
                PivotSheets = built.Select(s => new SavedPivotSheet
                {
                    Name = s.Name,
                    Config = s.Config.ToSaved(),
                    Captions = new Dictionary<string, string>(s.Captions, StringComparer.Ordinal),
                    ColumnKinds = s.ColumnKinds.ToDictionary(
                        kv => kv.Key,
                        kv => kv.Value.ToString(),
                        StringComparer.Ordinal),
                }).ToList(),
            };
            PivotStateStore.Save(_session, state);
            SavedBadge.Text = "Tabla guardada";
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("No se pudo guardar la tabla dinámica local", ex);
            return false;
        }
    }

    private void RebuildAllPivots(bool bindActive = true)
    {
        if (_view is null)
        {
            return;
        }

        foreach (var sheet in _sheets.Where(s => s.IsPivot && s.Config.CanBuild))
        {
            ApplyPivotBuild(sheet, PivotEngine.Build(_view, sheet.Config));
        }

        if (bindActive && _activeSheet?.IsPivot == true)
        {
            Grid.ItemsSource = _activeSheet.Result?.DefaultView;
        }
    }

    private void RebuildPivot()
    {
        if (_view is null || _activeSheet is not { IsPivot: true } sheet)
        {
            return;
        }

        if (!sheet.Config.CanBuild)
        {
            sheet.Result?.Dispose();
            sheet.Result = null;
            sheet.Snapshot = null;
            Grid.ItemsSource = null;
            StatusText.Text = "Arrastre campos a Filas, Columnas o Valores.";
            UpdateMetrics();
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            ApplyPivotBuild(sheet, PivotEngine.Build(_view, sheet.Config));
            Grid.ItemsSource = sheet.Result?.DefaultView;
            var groups = sheet.Snapshot?.LeafCount ?? sheet.Result?.Rows.Count ?? 0;
            StatusText.Text = $"Tabla dinámica · {groups:N0} grupos"
                              + (sheet.Config.Columns.Count > 0
                                  ? $" (máx. {PivotEngine.MaxCrossColumns} columnas)"
                                  : "");
            UpdateMetrics();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Tabla dinámica", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private static void ApplyPivotBuild(WorkbookSheet sheet, PivotBuild built)
    {
        sheet.Result?.Dispose();
        sheet.Result = built.Table;
        sheet.Snapshot = built.Snapshot;
        ApplyPivotColumnLayout(sheet);
    }

    private void OnPivotOutlineClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsPivotSheet || _activeSheet?.Snapshot is not { Outline: true } snapshot)
        {
            return;
        }

        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell?.Column is null || cell.DataContext is not DataRowView row)
        {
            return;
        }

        var name = cell.Column.SortMemberPath;
        if (string.IsNullOrEmpty(name) && cell.Column.Header is string header)
        {
            name = header;
        }

        if (!string.Equals(name, PivotEngine.OutlineColumn, StringComparison.Ordinal))
        {
            return;
        }

        if (!row.Row.Table.Columns.Contains(PivotEngine.KidsColumn)
            || row[PivotEngine.KidsColumn] is not true)
        {
            return;
        }

        var path = Convert.ToString(row[PivotEngine.PathColumn], CultureInfo.InvariantCulture) ?? "";
        if (path.Length == 0)
        {
            return;
        }

        e.Handled = true;
        _activeSheet.Config.ToggleExpand(path);
        var next = PivotEngine.Flatten(snapshot, _activeSheet.Config.ExpandedPaths);
        _activeSheet.Result?.Dispose();
        _activeSheet.Result = next;
        ApplyPivotColumnLayout(_activeSheet);
        Grid.ItemsSource = next.DefaultView;
        UpdateMetrics();
    }

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        _openFilter?.Close();
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
        if (!RequireBrowserSession())
        {
            return;
        }

        MessageBox.Show(this,
            "La hoja de datos se abre desde el listado web. Use Tabla dinámica para crear otra hoja y armar el análisis, como en Excel.",
            "Agregar vista", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnLoadingRow(object sender, DataGridRowEventArgs e)
    {
        e.Row.Header = (e.Row.GetIndex() + 1).ToString();
    }

    private void OnAutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        var name = e.PropertyName;
        if (PivotEngine.IsHiddenColumn(name))
        {
            e.Cancel = true;
            return;
        }

        e.Column.MinWidth = 88;
        e.Column.Header = DisplayHeader(name);
        if (string.Equals(name, PivotEngine.OutlineColumn, StringComparison.Ordinal))
        {
            e.Column.CanUserSort = false;
            e.Column.MinWidth = 180;
        }
        e.Column.SortMemberPath = name;
        ExcelColumnHeader.SetFilterActive(
            e.Column,
            _columnFilters.TryGetValue(name, out var existing) && existing.IsActive);
        ExcelColumnHeader.SetIsSelected(
            e.Column,
            string.Equals(name, _selectedColumnName, StringComparison.Ordinal));
        if (e.Column is DataGridTextColumn text)
        {
            var kind = ResolveColumnKind(name);
            text.ElementStyle = new Style(typeof(TextBlock))
            {
                Setters =
                {
                    new Setter(TextBlock.PaddingProperty, new Thickness(4, 0, 4, 0)),
                    new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                    new Setter(TextBlock.HorizontalAlignmentProperty,
                        kind is ColumnDataKind.Moneda or ColumnDataKind.Numero or ColumnDataKind.Entero
                            ? HorizontalAlignment.Right
                            : HorizontalAlignment.Left),
                },
            };
            if (kind == ColumnDataKind.Fecha || e.PropertyType == typeof(DateTime))
            {
                text.Binding = new Binding(name)
                {
                    Converter = FechaDisplayConverter.Instance,
                };
            }
            else if (kind == ColumnDataKind.Moneda)
            {
                text.Binding = new Binding("[" + name.Replace("]", @"\]") + "]")
                {
                    Converter = MonedaDisplayConverter.Instance,
                    Mode = BindingMode.OneWay,
                };
            }
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
        var name = ColumnKey(Grid.CurrentCell.Column);
        SelectGridColumn(Grid.CurrentCell.Column);

        CellRefText.Text = $"{colLetter}{Math.Max(rowIndex, 1)}";
        if (string.IsNullOrEmpty(name) || !row.Row.Table.Columns.Contains(name))
        {
            FormulaBox.Text = "";
            return;
        }

        var cell = row[name];
        var kind = ResolveColumnKind(name);
        FormulaBox.Text = kind == ColumnDataKind.Fecha && cell is DateTime dt
            ? DataFileParser.FormatFecha(dt)
            : kind == ColumnDataKind.Moneda
                ? MonedaDisplayConverter.Format(cell) ?? ""
                : Convert.ToString(cell, CultureInfo.CurrentCulture) ?? "";
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

    private void OnColumnHeaderFilterClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (IsPivotSheet)
        {
            return;
        }

        if (sender is not FrameworkElement fe)
        {
            return;
        }

        var colHeader = FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(fe);
        var name = ColumnKey(colHeader?.Column);

        if (!string.IsNullOrEmpty(name))
        {
            OpenColumnFilter(name, fe);
        }
    }

    private void OpenColumnFilter(string columnName, FrameworkElement anchor)
    {
        if (_table is null)
        {
            return;
        }

        if (_openFilter is not null)
        {
            var sameColumn = string.Equals(_openFilterColumn, columnName, StringComparison.Ordinal);
            _openFilter.Close();
            if (sameColumn)
            {
                return;
            }
        }

        Mouse.OverrideCursor = Cursors.Wait;
        List<FilterValueItem> items;
        bool textOnly;
        try
        {
            (items, textOnly) = CollectFilterValues(columnName);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        _columnFilters.TryGetValue(columnName, out var current);
        var bound = BoundTable;
        var isDate = bound is not null
                     && bound.Columns.Contains(columnName)
                     && bound.Columns[columnName]!.DataType == typeof(DateTime);
        var win = new ExcelAutoFilterWindow(columnName, items, current, textOnly, isDate)
        {
            Owner = this,
        };
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openFilter, win))
            {
                _openFilter = null;
                _openFilterColumn = null;
            }

            if (win.Accepted)
            {
                ApplyAutoFilterResult(columnName, win);
            }
        };

        PlaceBelow(win, anchor);
        _openFilter = win;
        _openFilterColumn = columnName;
        win.Show();
        win.Activate();
    }

    private void ApplyAutoFilterResult(string columnName, ExcelAutoFilterWindow win)
    {
        if (win.Sort is { } dir && _view is not null)
        {
            var safe = EscapeCol(columnName);
            _view.Sort = $"[{safe}] {(dir == ListSortDirection.Ascending ? "ASC" : "DESC")}";
        }

        if (win.Cleared)
        {
            _columnFilters.Remove(columnName);
        }
        else if (win.Sort is null)
        {
            if (win.Result is null)
            {
                _columnFilters.Remove(columnName);
            }
            else
            {
                _columnFilters[columnName] = win.Result;
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
            string s;
            if (raw is DBNull or null)
            {
                s = "";
            }
            else if (raw is DateTime dt)
            {
                s = DataFileParser.FormatFecha(dt);
            }
            else
            {
                s = Convert.ToString(raw, CultureInfo.CurrentCulture) ?? "";
            }
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
        if (_table is null || !_table.Columns.Contains(column))
        {
            return null;
        }

        var col = _table.Columns[column];
        var isDate = col.DataType == typeof(DateTime);
        var name = isDate
            ? $"[{EscapeCol(column)}]"
            : $"CONVERT([{EscapeCol(column)}], 'System.String')";
        var parts = new List<string>();
        if (filter.DateFrom is DateTime from)
        {
            parts.Add($"{name} >= #{from:MM/dd/yyyy}#");
        }

        if (filter.DateTo is DateTime to)
        {
            parts.Add($"{name} < #{to.Date.AddDays(1):MM/dd/yyyy}#");
        }

        if (filter.TextOperator != TextFilterOperator.None && !string.IsNullOrEmpty(filter.TextValue))
        {
            var textCol = $"CONVERT([{EscapeCol(column)}], 'System.String')";
            var escaped = EscapeFilter(filter.TextValue);
            var literal = filter.TextValue.Replace("'", "''");
            parts.Add(filter.TextOperator switch
            {
                TextFilterOperator.Equals => $"{textCol} = '{literal}'",
                TextFilterOperator.NotEquals => $"{textCol} <> '{literal}'",
                TextFilterOperator.StartsWith => $"{textCol} LIKE '{escaped}%'",
                TextFilterOperator.EndsWith => $"{textCol} LIKE '%{escaped}'",
                TextFilterOperator.NotContains => $"NOT ({textCol} LIKE '%{escaped}%')",
                _ => $"{textCol} LIKE '%{escaped}%'",
            });
        }

        if (filter.Selected is not null)
        {
            var valueParts = new List<string>();
            if (filter.IncludeBlanks)
            {
                valueParts.Add(isDate ? $"{name} IS NULL" : $"{name} = ''");
            }

            foreach (var chunk in filter.Selected.Chunk(200))
            {
                if (isDate)
                {
                    foreach (var v in chunk)
                    {
                        if (DataFileParser.TryParseFecha(v, out var dt))
                        {
                            valueParts.Add($"{name} = #{dt:MM/dd/yyyy HH:mm:ss}#");
                        }
                    }
                }
                else
                {
                    var listed = string.Join(",", chunk.Select(v => $"'{v.Replace("'", "''")}'"));
                    valueParts.Add($"{name} IN ({listed})");
                }
            }

            parts.Add(valueParts.Count == 0 ? "1=0" : "(" + string.Join(" OR ", valueParts) + ")");
        }

        return parts.Count == 0 ? null : "(" + string.Join(" AND ", parts) + ")";
    }

    private void RefreshHeaderFilterIcons()
    {
        foreach (var col in Grid.Columns)
        {
            var name = col.SortMemberPath;
            if (string.IsNullOrEmpty(name))
            {
                name = col.Header as string;
            }

            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            ExcelColumnHeader.SetFilterActive(
                col,
                _columnFilters.TryGetValue(name, out var f) && f.IsActive);
        }
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        while (start is not null)
        {
            if (start is T match)
            {
                return match;
            }

            start = VisualTreeHelper.GetParent(start);
        }

        return null;
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

        var height = window.Height > 0 ? window.Height : 428;
        if (window.Top + height > SystemParameters.WorkArea.Bottom)
        {
            window.Top = Math.Max(0, point.Y - height - target.ActualHeight);
        }
    }

    private static string EscapeFilter(string value) =>
        value.Replace("'", "''").Replace("[", "[[").Replace("]", "]]").Replace("%", "[%]").Replace("*", "[*]");

    private static string EscapeCol(string name) => name.Replace("]", "]]");

    private DataTable? BoundTable => IsPivotSheet ? _activeSheet?.Result : _table;

    private static string? ColumnKey(DataGridColumn? column)
    {
        if (column is null)
        {
            return null;
        }

        return string.IsNullOrEmpty(column.SortMemberPath)
            ? column.Header as string
            : column.SortMemberPath;
    }

    private string DisplayHeader(string name)
    {
        if (IsPivotSheet
            && _activeSheet?.Captions.TryGetValue(name, out var caption) == true
            && !string.IsNullOrWhiteSpace(caption))
        {
            return caption;
        }

        return name;
    }

    private ColumnDataKind ResolveColumnKind(string name)
    {
        var table = BoundTable;
        if (table is not null && table.Columns.Contains(name))
        {
            var col = table.Columns[name]!;
            if (ColumnMutator.TryGetKind(col, out var stamped))
            {
                return stamped;
            }
        }

        if (IsPivotSheet && _activeSheet?.ColumnKinds.TryGetValue(name, out var pivotKind) == true)
        {
            return pivotKind;
        }

        var ov = _columnOverrides.FirstOrDefault(o =>
            string.Equals(o.DisplayName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(o.OriginalName, name, StringComparison.OrdinalIgnoreCase));
        if (ov is not null
            && ColumnDataKinds.TryParse(ov.Kind, out var savedKind))
        {
            return savedKind;
        }

        if (table is not null && table.Columns.Contains(name))
        {
            return ColumnDataKinds.FromType(table.Columns[name]!.DataType);
        }

        return ColumnDataKind.Texto;
    }

    private string? MapSourceColumn(string name)
    {
        if (_table is null || string.IsNullOrEmpty(name))
        {
            return null;
        }

        var ov = _columnOverrides.FirstOrDefault(o =>
            string.Equals(o.OriginalName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(o.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        var want = string.IsNullOrWhiteSpace(ov?.DisplayName) ? name : ov!.DisplayName;
        return _table.Columns.Cast<DataColumn>().FirstOrDefault(c =>
            string.Equals(c.ColumnName, want, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.ColumnName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ColumnMutator.OriginalNameOf(c), name, StringComparison.OrdinalIgnoreCase))
            ?.ColumnName;
    }

    private static DataColumn? FindColumn(DataTable table, string name) =>
        table.Columns.Cast<DataColumn>().FirstOrDefault(c =>
            string.Equals(c.ColumnName, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ColumnMutator.OriginalNameOf(c), name, StringComparison.OrdinalIgnoreCase));

    private void ApplyColumnLayout()
    {
        if (_table is null)
        {
            return;
        }

        if (_session is not null && _columnOverrides.Count == 0)
        {
            var saved = ColumnLayoutStore.Load(_session);
            if (saved?.Columns is { Count: > 0 })
            {
                _columnOverrides.AddRange(saved.Columns);
            }
        }

        foreach (var ov in _columnOverrides.ToList())
        {
            var col = FindColumn(_table, ov.OriginalName) ?? FindColumn(_table, ov.DisplayName);
            if (col is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(ov.Kind)
                && ColumnDataKinds.TryParse(ov.Kind, out var kind))
            {
                if (ColumnDataKinds.FromType(col.DataType) != kind)
                {
                    ColumnMutator.ChangeType(_table, col.ColumnName, kind);
                    col = _table.Columns[col.ColumnName];
                    if (col is null)
                    {
                        continue;
                    }
                }
                else
                {
                    ColumnMutator.StampKind(col, kind);
                }
            }

            var target = string.IsNullOrWhiteSpace(ov.DisplayName) ? ov.OriginalName : ov.DisplayName;
            if (!string.Equals(col.ColumnName, target, StringComparison.Ordinal))
            {
                ColumnMutator.Rename(_table, col.ColumnName, target);
            }
        }
    }

    private void PersistColumnLayout()
    {
        if (_session is null)
        {
            return;
        }

        ColumnLayoutStore.Save(_session, _columnOverrides);
    }

    private static void RestorePivotLayout(WorkbookSheet sheet, SavedPivotSheet saved)
    {
        sheet.Captions.Clear();
        foreach (var kv in saved.Captions ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            {
                sheet.Captions[kv.Key] = kv.Value;
            }
        }

        sheet.ColumnKinds.Clear();
        foreach (var kv in saved.ColumnKinds ?? new Dictionary<string, string>())
        {
            if (ColumnDataKinds.TryParse(kv.Value, out var kind))
            {
                sheet.ColumnKinds[kv.Key] = kind;
            }
        }
    }

    private static void ApplyPivotColumnLayout(WorkbookSheet sheet)
    {
        if (sheet.Result is null || sheet.ColumnKinds.Count == 0)
        {
            return;
        }

        foreach (var name in sheet.Result.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList())
        {
            if (PivotEngine.IsHiddenColumn(name)
                || string.Equals(name, PivotEngine.OutlineColumn, StringComparison.Ordinal)
                || !sheet.ColumnKinds.TryGetValue(name, out var kind))
            {
                continue;
            }

            var col = sheet.Result.Columns[name];
            if (col is null)
            {
                continue;
            }

            if (ColumnDataKinds.FromType(col.DataType) == kind)
            {
                ColumnMutator.StampKind(col, kind);
                continue;
            }

            ColumnMutator.ChangeType(sheet.Result, name, kind);
        }
    }

    private ColumnOverride UpsertSourceOverride(DataColumn col)
    {
        var original = ColumnMutator.OriginalNameOf(col);
        var existing = _columnOverrides.FirstOrDefault(o =>
            string.Equals(o.OriginalName, original, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new ColumnOverride
            {
                OriginalName = original,
                DisplayName = col.ColumnName,
            };
            _columnOverrides.Add(existing);
        }

        existing.DisplayName = col.ColumnName;
        return existing;
    }

    private void SelectGridColumn(DataGridColumn? column)
    {
        _selectedColumnName = ColumnKey(column);
        foreach (var col in Grid.Columns)
        {
            ExcelColumnHeader.SetIsSelected(
                col,
                !string.IsNullOrEmpty(_selectedColumnName)
                && string.Equals(ColumnKey(col), _selectedColumnName, StringComparison.Ordinal));
        }
    }

    private string? ResolveSelectedColumn()
    {
        if (!string.IsNullOrEmpty(_selectedColumnName)
            && Grid.Columns.Any(c => string.Equals(ColumnKey(c), _selectedColumnName, StringComparison.Ordinal)))
        {
            return _selectedColumnName;
        }

        return ColumnKey(Grid.CurrentCell.Column);
    }

    private bool CanEditColumn([NotNullWhen(true)] string? name) =>
        !string.IsNullOrEmpty(name) && !PivotEngine.IsHiddenColumn(name);

    private static bool CanChangeColumnType(string name) =>
        !PivotEngine.IsHiddenColumn(name)
        && !string.Equals(name, PivotEngine.OutlineColumn, StringComparison.Ordinal);

    private void AttachColumnHeaderInteractions()
    {
        Grid.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnColumnHeaderClick), true);
        Grid.AddHandler(Control.MouseDoubleClickEvent, new MouseButtonEventHandler(OnColumnHeaderDoubleClick), true);
        Grid.AddHandler(FrameworkElement.ContextMenuOpeningEvent, new ContextMenuEventHandler(OnColumnHeaderMenuOpening), true);

        var menu = CreateColumnHeaderMenu();
        var current = Grid.ColumnHeaderStyle;
        var style = current is { IsSealed: true }
            ? new Style(typeof(DataGridColumnHeader), current)
            : current ?? new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty, menu));
        Grid.ColumnHeaderStyle = style;
    }

    private ContextMenu CreateColumnHeaderMenu()
    {
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "Cambiar nombre...", InputGestureText = "F2" };
        rename.Click += OnRenameColumn;

        var types = new MenuItem { Header = "Tipo de dato" };
        foreach (var (label, tag) in new (string Label, string Tag)[]
        {
            ("Texto", "Texto"),
            ("Número", "Numero"),
            ("Moneda", "Moneda"),
            ("Entero", "Entero"),
            ("Fecha", "Fecha"),
            ("Verdadero/Falso", "Logico"),
        })
        {
            var item = new MenuItem { Header = label, Tag = tag };
            item.Click += OnChangeColumnType;
            types.Items.Add(item);
        }

        var props = new MenuItem { Header = "Propiedades de columna..." };
        props.Click += OnColumnProperties;

        menu.Items.Add(rename);
        menu.Items.Add(types);
        menu.Items.Add(new Separator());
        menu.Items.Add(props);
        return menu;
    }

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        var header = FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
        if (header?.Column is not null)
        {
            SelectGridColumn(header.Column);
        }
    }

    private void OnColumnHeaderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d
            && FindAncestor<Button>(d) is not null)
        {
            return;
        }

        var header = FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(
            e.OriginalSource as DependencyObject);
        if (header?.Column is null)
        {
            return;
        }

        SelectGridColumn(header.Column);
        OpenColumnProperties();
        e.Handled = true;
    }

    private void OnColumnHeaderMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var header = FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(
            e.OriginalSource as DependencyObject);
        if (header?.Column is not null)
        {
            SelectGridColumn(header.Column);
        }
    }

    private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            e.Handled = true;
            OpenColumnProperties();
        }
    }

    private void OnRibbonColumnType(object sender, RoutedEventArgs e)
    {
        if (ResolveSelectedColumn() is null)
        {
            PromptSelectColumn();
            return;
        }

        if (sender is not Button { ContextMenu: { } menu } btn)
        {
            OpenColumnProperties();
            return;
        }

        menu.PlacementTarget = btn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnRenameColumn(object sender, RoutedEventArgs e) => OpenColumnProperties();

    private void OnColumnProperties(object sender, RoutedEventArgs e) => OpenColumnProperties();

    private void OnChangeColumnType(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }
            || !ColumnDataKinds.TryParse(tag, out var kind))
        {
            return;
        }

        ApplySelectedColumnType(kind);
    }

    private void PromptSelectColumn()
    {
        MessageBox.Show(this,
            "Seleccione una columna haciendo clic en su encabezado, como en Excel.",
            "Columna", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenColumnProperties()
    {
        var key = ResolveSelectedColumn();
        if (!CanEditColumn(key) || BoundTable is null)
        {
            PromptSelectColumn();
            return;
        }

        var table = BoundTable;
        if (!table.Columns.Contains(key))
        {
            PromptSelectColumn();
            return;
        }

        var col = table.Columns[key]!;
        var currentName = DisplayHeader(key);
        var currentKind = ResolveColumnKind(key);
        var dialog = new ColumnPropertiesDialog(currentName, currentKind) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ApplySelectedColumnRename(dialog.ColumnName);
        if (CanChangeColumnType(key))
        {
            ApplySelectedColumnType(dialog.Kind);
        }
    }

    private void ApplySelectedColumnRename(string newName)
    {
        var key = ResolveSelectedColumn();
        if (!CanEditColumn(key) || BoundTable is null)
        {
            PromptSelectColumn();
            return;
        }

        var table = BoundTable;
        if (!table.Columns.Contains(key))
        {
            return;
        }

        var sanitized = ColumnMutator.SanitizeName(newName);
        if (string.Equals(DisplayHeader(key), sanitized, StringComparison.Ordinal))
        {
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            if (IsPivotSheet && _activeSheet is not null)
            {
                _activeSheet.Captions[key] = sanitized;
                var gridCol = Grid.Columns.FirstOrDefault(c =>
                    string.Equals(ColumnKey(c), key, StringComparison.Ordinal));
                if (gridCol is not null)
                {
                    gridCol.Header = sanitized;
                }

                ScheduleSavePivots();
                StatusText.Text = $"Columna renombrada a «{sanitized}».";
                return;
            }

            Grid.ItemsSource = null;
            var next = ColumnMutator.Rename(table, key, sanitized);
            RemapSourceColumnName(key, next);
            var dataCol = table.Columns[next];
            if (dataCol is not null)
            {
                UpsertSourceOverride(dataCol);
            }

            PersistColumnLayout();
            RebindGrid(next);
            RefreshPivotFieldList();
            StatusText.Text = $"Columna renombrada a «{next}».";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cambiar nombre", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void ApplySelectedColumnType(ColumnDataKind kind)
    {
        var key = ResolveSelectedColumn();
        if (string.IsNullOrEmpty(key) || BoundTable is null)
        {
            PromptSelectColumn();
            return;
        }

        if (!CanChangeColumnType(key) || !BoundTable.Columns.Contains(key))
        {
            MessageBox.Show(this,
                "Esta columna no admite cambio de tipo de dato.",
                "Tipo de dato", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var table = BoundTable;
        var col = table.Columns[key]!;
        if (ColumnDataKinds.FromType(col.DataType) == kind
            && (!IsPivotSheet || _activeSheet?.ColumnKinds.GetValueOrDefault(key) == kind))
        {
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            Grid.ItemsSource = null;
            var result = ColumnMutator.ChangeType(table, key, kind);
            if (IsPivotSheet && _activeSheet is not null)
            {
                _activeSheet.ColumnKinds[key] = kind;
                RebindGrid(key);
                ScheduleSavePivots();
            }
            else
            {
                var dataCol = table.Columns[key];
                if (dataCol is not null)
                {
                    var ov = UpsertSourceOverride(dataCol);
                    ov.Kind = kind.ToString();
                }

                PersistColumnLayout();
                RebuildAllPivots(bindActive: false);
                RebindGrid(key);
                RefreshPivotFieldList();
            }

            StatusText.Text = $"Tipo de «{DisplayHeader(key)}» → {ColumnDataKinds.Label(kind)}.";
            if (result.Failed > 0)
            {
                MessageBox.Show(this,
                    $"{result.Failed:N0} de {result.Total:N0} celdas no se pudieron convertir y quedaron en blanco.",
                    "Tipo de dato", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"Cambio de tipo a {kind} en '{key}'", ex);
            MessageBox.Show(this, ex.Message, "Tipo de dato", MessageBoxButton.OK, MessageBoxImage.Error);
            if (BoundTable is not null)
            {
                RebindGrid(key);
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void RemapSourceColumnName(string oldName, string newName)
    {
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return;
        }

        if (_columnFilters.Remove(oldName, out var filter))
        {
            _columnFilters[newName] = filter;
        }

        foreach (var sheet in _sheets.Where(s => s.IsPivot))
        {
            sheet.Config.RenameField(oldName, newName);
        }

        _selectedColumnName = newName;
    }

    private void RebindGrid(string? keepColumn)
    {
        Grid.ItemsSource = null;
        if (IsPivotSheet)
        {
            Grid.ItemsSource = _activeSheet?.Result?.DefaultView;
        }
        else
        {
            Grid.ItemsSource = _view;
        }
        if (!string.IsNullOrEmpty(keepColumn))
        {
            var col = Grid.Columns.FirstOrDefault(c =>
                string.Equals(ColumnKey(c), keepColumn, StringComparison.Ordinal));
            SelectGridColumn(col);
        }

        UpdateMetrics();
    }

    private void RefreshPivotFieldList()
    {
        if (_table is null || _view is null || PivotSidebar.Visibility != Visibility.Visible)
        {
            return;
        }

        PivotSidebar.SetFields(_table.Columns.Cast<DataColumn>().Select(c => c.ColumnName), _view);
    }
}
