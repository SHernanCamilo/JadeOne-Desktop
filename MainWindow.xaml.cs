using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using Microsoft.Win32;
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
    private const int MaxLoadedViews = 8;
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
    private DispatcherTimer? _selectionTimer;
    private readonly List<ColumnOverride> _columnOverrides = new();
    private string? _selectedColumnName;
    private bool _showTotals;
    private List<VistaCatalogItem>? _viewsCache;

    public MainWindow(string? protocolUrl)
    {
        InitializeComponent();
        AttachColumnHeaderInteractions();
        _protocolUrl = protocolUrl;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _pivotSaveTimer?.Stop();
            _selectionTimer?.Stop();
            SavePivotsNow();
            _openFilter?.Close();
            _loadCts?.Cancel();
            foreach (var sheet in _sheets)
            {
                sheet.Result?.Dispose();
                sheet.SourceTable?.Dispose();
            }

            _api.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Modo demo: revisar el estilo/UI sin backend ni sesión real.
        // Se activa lanzando: JadeOneDesktop.exe "jadeone-desktop://demo"
        if (IsDemoLaunch(_protocolUrl))
        {
            LoadDemo();
            return;
        }

        if (string.IsNullOrWhiteSpace(_protocolUrl)
            || !ProtocolHandler.TryParse(_protocolUrl, out _, out _))
        {
            BlockDirectLaunch();
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        await LoadFromProtocolAsync(_protocolUrl);
    }

    private static bool IsDemoLaunch(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && url.Contains("demo", StringComparison.OrdinalIgnoreCase)
        && url.StartsWith(ProtocolHandler.Scheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Carga una tabla de ejemplo directamente (sin claim ni backend) para poder
    /// revisar el diseño de la grilla, filtros, tabla dinámica y estilos.
    /// </summary>
    private void LoadDemo()
    {
        EmptyState.Visibility = Visibility.Collapsed;
        TitleText.Text = "DEMO — Estilo";
        SubtitleText.Text = "Datos de ejemplo (sin conexión)";
        Title = "JadeOne Desktop — DEMO";
        SavedBadge.Text = "Demo";

        // Sesión ficticia para que los botones que exigen sesión funcionen.
        _session = new LaunchSession
        {
            Token = "demo",
            Schema = "demo",
            View = "Ejemplo",
            ViewLabel = "Ejemplo",
            ApiUrl = "http://127.0.0.1",
            User = "demo",
        };

        var table = BuildDemoTable();
        _columns = table.Columns.Cast<DataColumn>()
            .Select(c => new FabricColumn { Name = c.ColumnName, Type = c.DataType.Name })
            .ToList();
        FilterColumnCombo.ItemsSource = _columns;

        var sheet = EnsureDataSheet();
        BindTable(table, sheet, restorePivots: false);
        RefreshButton.IsEnabled = false;
        StatusText.Text = $"DEMO · {table.Rows.Count:N0} filas de ejemplo";
    }

    private static DataTable BuildDemoTable()
    {
        var t = new DataTable("Ejemplo");
        t.Columns.Add("Sede", typeof(string));
        t.Columns.Add("Especialidad", typeof(string));
        t.Columns.Add("Profesional", typeof(string));
        t.Columns.Add("Fecha", typeof(DateTime));
        t.Columns.Add("Estado", typeof(string));
        t.Columns.Add("Cantidad", typeof(int));
        t.Columns.Add("Valor", typeof(double));

        string[] sedes = { "Bogotá", "Neiva", "Florencia", "Mocoa", "Tunja" };
        string[] esp = { "Fisioterapia", "Fonoaudiología", "Terapia Ocupacional", "Psicología" };
        string[] profs = { "Ana Reyes", "Luis Gómez", "Gina Trujillo", "Katherine Rojas", "Paula Cadena" };
        string[] estados = { "Confirmado", "Pendiente", "Evaluado", "Registrado" };

        var rnd = new Random(7);
        var baseDate = new DateTime(2026, 1, 1);
        for (var i = 0; i < 250; i++)
        {
            t.Rows.Add(
                sedes[rnd.Next(sedes.Length)],
                esp[rnd.Next(esp.Length)],
                profs[rnd.Next(profs.Length)],
                baseDate.AddDays(rnd.Next(0, 240)).AddHours(rnd.Next(6, 20)),
                estados[rnd.Next(estados.Length)],
                rnd.Next(1, 40),
                Math.Round(rnd.NextDouble() * 900000 + 50000, 2));
        }

        return t;
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

        // Chequeo de actualización antes de canjear el ticket. Si el usuario
        // acepta, la app se cierra y el updater relanza la versión nueva.
        if (await MaybeUpdateAsync(apiUrl))
        {
            return;
        }

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
        catch (UpdateRequiredException ex)
        {
            await ForceUpdateAsync(apiUrl, ex);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    /// <summary>
    /// El backend bloqueó el claim por versión desactualizada. Ofrecemos
    /// actualizar de inmediato; si acepta, se descarga y la app se reinicia.
    /// Es el mecanismo que fuerza a las versiones viejas a ponerse al día.
    /// </summary>
    private async Task ForceUpdateAsync(string apiUrl, UpdateRequiredException ex)
    {
        HideOverlay();
        var downloadUrl = string.IsNullOrWhiteSpace(ex.DownloadUrl)
            ? apiUrl.TrimEnd('/') + "/fabric/viewer/desktop/download"
            : ex.DownloadUrl!;

        var choice = MessageBox.Show(
            this,
            ex.Message + "\n\n¿Desea actualizar ahora? La aplicación se descargará y reiniciará.",
            "Actualización obligatoria",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (choice != MessageBoxResult.Yes)
        {
            StatusText.Text = "Actualización requerida. Cierre y vuelva a abrir tras actualizar.";
            Close();
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            ShowOverlay("Actualizando...", "Descargando la versión nueva", false);
            OverlayProgress.IsIndeterminate = false;
            var progress = new Progress<double>(p =>
            {
                OverlayProgress.Value = p * 100;
                OverlayMessage.Text = $"Descargando... {p * 100:0}%";
            });

            var applied = await UpdateService.DownloadAndApplyAsync(downloadUrl, http, progress, CancellationToken.None);
            if (applied)
            {
                AppLog.Info("Actualización obligatoria aplicada; cerrando para reiniciar.");
                Application.Current.Shutdown();
                return;
            }

            HideOverlay();
            MessageBox.Show(this,
                "No se pudo aplicar la actualización automáticamente. Descárguela desde la plataforma.",
                "Actualización", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
        catch (Exception dl)
        {
            AppLog.Error("Actualización obligatoria falló", dl);
            HideOverlay();
            MessageBox.Show(this, dl.Message, "Actualización", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    /// <summary>
    /// Se invoca cuando el usuario abre otra vista desde la web estando la app
    /// ya abierta (instancia única). En vez de abrir otra ventana, traemos esta
    /// al frente y ofrecemos cargar la vista como una hoja nueva aquí.
    /// </summary>
    public async void HandleIncomingProtocol(string protocolUrl)
    {
        // Traer la ventana al frente.
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();

        if (!ProtocolHandler.TryParse(protocolUrl, out var ticket, out var env) || _session is null)
        {
            return;
        }

        var choice = MessageBox.Show(
            this,
            "Ya tiene JadeOne Desktop abierto.\n\n"
            + "¿Desea abrir la vista solicitada como una hoja nueva en esta misma ventana?\n\n"
            + "Sí = cargar aquí como hoja adicional.\n"
            + "No = ignorar (seguir con lo que tiene abierto).",
            "JadeOne Desktop ya está abierto",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var apiUrl = OfficialApi.Resolve(env);
            var session = await _api.ClaimAsync(apiUrl, ticket, CancellationToken.None);

            // ¿Ya existe una hoja con esa vista? Si sí, activarla.
            var existing = _sheets.FirstOrDefault(s =>
                !s.IsPivot && !s.IsBlank
                && string.Equals(s.Schema, session.Schema, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.ViewName, session.View, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                ActivateSheet(existing);
                StatusText.Text = $"La vista {session.ViewLabel} ya estaba abierta.";
                return;
            }

            var loaded = _sheets.Count(s => !s.IsPivot && s.SourceTable is not null);
            if (loaded >= MaxLoadedViews)
            {
                MessageBox.Show(this,
                    $"Máximo {MaxLoadedViews} vistas cargadas a la vez. Cierre una hoja antes de abrir otra.",
                    "Abrir vista", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sheet = new WorkbookSheet
            {
                Name = session.ViewLabel,
                Schema = session.Schema,
                ViewName = session.View,
                IsExtraView = true,
            };
            _sheets.Add(sheet);
            RefreshSheetTabs();
            ActivateSheet(sheet, capture: true);
            await LoadDataAsync(sheet);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"No se pudo cargar la vista entrante: {ex.Message}");
            ShowError(ex.Message);
        }
    }

    /// <summary>
    /// Consulta si hay una versión nueva publicada. Si la hay y el usuario
    /// acepta, la descarga y lanza el updater. Devuelve true cuando se está
    /// aplicando la actualización (el llamador debe detener el arranque).
    /// </summary>
    private async Task<bool> MaybeUpdateAsync(string apiUrl)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            ShowOverlay("Buscando actualizaciones...", "Comprobando la última versión", true);
            var info = await UpdateService.CheckAsync(apiUrl, http, CancellationToken.None);
            HideOverlay();

            if (info is null
                || !info.Available
                || string.IsNullOrWhiteSpace(info.DownloadUrl)
                || !UpdateService.IsNewer(info.Version))
            {
                return false;
            }

            var choice = MessageBox.Show(
                this,
                $"Hay una versión nueva de JadeOne Desktop disponible.\n\n"
                + $"Actual: {UpdateService.Current.Major}.{UpdateService.Current.Minor}.{UpdateService.Current.Build}\n"
                + $"Nueva: {info.Version}\n\n"
                + "¿Desea actualizar ahora? La aplicación se reiniciará automáticamente.",
                "Actualización disponible",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (choice != MessageBoxResult.Yes)
            {
                return false;
            }

            ShowOverlay("Actualizando...", "Descargando la versión nueva", false);
            OverlayProgress.IsIndeterminate = false;
            var progress = new Progress<double>(p =>
            {
                OverlayProgress.Value = p * 100;
                OverlayMessage.Text = $"Descargando... {p * 100:0}%";
            });

            var applied = await UpdateService.DownloadAndApplyAsync(info.DownloadUrl, http, progress, CancellationToken.None);
            if (applied)
            {
                AppLog.Info($"Actualizando a versión {info.Version}; cerrando para aplicar.");
                Application.Current.Shutdown();
                return true;
            }

            HideOverlay();
            MessageBox.Show(this,
                "No se pudo aplicar la actualización. Se continuará con la versión actual.",
                "Actualización", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Actualización omitida por error: {ex.Message}");
            HideOverlay();
            return false;
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (!RequireBrowserSession())
        {
            return;
        }

        var target = _activeSheet is { IsPivot: false }
            ? _activeSheet
            : _activeSheet?.PivotSource ?? EnsureDataSheet();
        await LoadDataAsync(target);
    }

    private async Task LoadDataAsync(WorkbookSheet? into = null)
    {
        if (_session is null)
        {
            return;
        }

        var sheet = into ?? EnsureDataSheet();
        var schema = sheet.Schema ?? _session.Schema;
        var view = sheet.ViewName ?? _session.View;
        var dateFilter = sheet.DateFilter;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        _elapsed.Restart();
        RefreshButton.IsEnabled = false;
        CancelButton.IsEnabled = true;

        try
        {
            ShowOverlay("Preparando columnas...", $"Consultando metadatos de {view}", true);
            _columns = await _api.GetColumnsAsync(schema, view, ct);
            FilterColumnCombo.ItemsSource = _columns;
            if (_columns.Count > 0 && FilterColumnCombo.SelectedIndex < 0)
            {
                FilterColumnCombo.SelectedIndex = 0;
            }

            ShowOverlay("Exportando datos...", "Solicitando gzip a Fabric", true);
            var start = await _api.StartExportAsync(schema, view, MaxExportRows, dateFilter, ct);

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

                dateFilter = _dateFilter;
                sheet.DateFilter = dateFilter;
                ShowOverlay("Exportando con filtro...", "Aplicando rango de fechas", true);
                start = await _api.StartExportAsync(schema, view, MaxExportRows, dateFilter, ct);
            }

            if (string.Equals(start.R2Status, "generating", StringComparison.OrdinalIgnoreCase))
            {
                start = await WaitForR2ThenExportAsync(schema, view, start, dateFilter, ct);
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

                dateFilter = _dateFilter;
                sheet.DateFilter = dateFilter;
                start = await _api.StartExportAsync(schema, view, MaxExportRows, dateFilter, ct);
            }

            if (string.IsNullOrWhiteSpace(start.JobId))
            {
                throw new InvalidOperationException(start.Message ?? "No se pudo iniciar la descarga.");
            }

            var jobId = start.JobId;
            sheet.LastJobId = jobId;
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
            BindTable(table, sheet, restorePivots: !sheet.IsExtraView);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            HideOverlay();
            EmptyState.Visibility = Visibility.Collapsed;
            RefreshButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _elapsed.Stop();
            UpdateMetrics();
            StatusText.Text = $"{table.Rows.Count:N0} registros cargados";
            HintText.Text = $"{_elapsed.Elapsed.TotalSeconds:0} seg. carga";
            AppLog.Info($"Carga OK view={schema}.{view} rows={table.Rows.Count} elapsed={_elapsed.Elapsed.TotalSeconds:0.0}s");
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

    private async Task<ExportStartResponse> WaitForR2ThenExportAsync(
        string schema,
        string view,
        ExportStartResponse start,
        DateRangeFilter? dateFilter,
        CancellationToken ct)
    {
        var estimated = start.EstimatedS ?? 60;
        OverlayTitle.Text = "Preparando datos...";
        OverlayMessage.Text = $"Generando archivo de la vista (~{estimated}s). Puede tardar en la primera carga.";
        AppLog.Info($"R2 generating {schema}.{view} estimated={estimated}s");
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(5000, ct);
            var status = await _api.GetR2StatusAsync(schema, view, ct);
            var r2 = status.R2Status ?? "";
            AppLog.Info($"R2 poll status={r2} msg={status.Message}");
            if (r2 is "ready" or "ready_stale")
            {
                OverlayMessage.Text = "Datos listos, descargando...";
                return await _api.StartExportAsync(schema, view, MaxExportRows, dateFilter, ct);
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
                return await _api.StartExportAsync(schema, view, MaxExportRows, dateFilter, ct);
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

    private void BindTable(DataTable table, WorkbookSheet sheet, bool restorePivots)
    {
        _openFilter?.Close();

        // Preservar filtros al ACTUALIZAR la misma vista: si la tabla recargada
        // tiene las mismas columnas, conservamos los autofiltros, la búsqueda,
        // el filtro de columna y el orden para reaplicarlos tras el bind.
        var samas = sheet.SourceTable is not null
                    && ColumnsMatch(sheet.SourceTable, table);
        var preservedFilters = samas
            ? new Dictionary<string, ColumnAutoFilter>(_columnFilters, StringComparer.OrdinalIgnoreCase)
            : null;
        var preservedSearch = samas ? SearchBox.Text : null;
        var preservedColText = samas ? ColumnFilterBox.Text : null;
        var preservedSort = samas ? _view?.Sort : null;

        _columnFilters.Clear();
        _selectedColumnName = null;

        var old = sheet.SourceTable;
        sheet.SourceTable = table;
        sheet.Columns.Clear();
        sheet.Columns.AddRange(_columns);

        _table = table;
        ColumnMutator.StampOriginalNames(_table);
        LoadOverridesFor(sheet);
        ApplyColumnLayout();
        _view = table.DefaultView;
        sheet.Filters.Clear();
        sheet.Overrides.Clear();
        sheet.Overrides.AddRange(_columnOverrides);

        // Restaurar filtros preservados sobre la nueva tabla.
        if (preservedFilters is { Count: > 0 })
        {
            foreach (var kv in preservedFilters)
            {
                if (table.Columns.Contains(kv.Key))
                {
                    _columnFilters[kv.Key] = kv.Value;
                }
            }
        }

        if (!string.IsNullOrEmpty(preservedSort) && _view is not null)
        {
            try { _view.Sort = preservedSort; } catch { /* columna pudo cambiar */ }
        }

        if (old is not null
            && !ReferenceEquals(old, table)
            && !_sheets.Any(s => ReferenceEquals(s.SourceTable, old)))
        {
            old.Dispose();
        }

        WorkbookSheet? prefer = null;
        if (restorePivots && !_didRestorePivots)
        {
            prefer = TryRestorePivots();
            _didRestorePivots = true;
        }
        else
        {
            prefer = sheet;
        }

        RebuildAllPivots(bindActive: false);
        ActivateSheet(prefer ?? sheet, capture: false);

        // Reaplicar la búsqueda/filtro de texto preservados (dispara ApplyFilters,
        // que también reconstruye los pivotes con los datos ya filtrados).
        if (preservedSearch is not null || preservedColText is not null || _columnFilters.Count > 0)
        {
            if (preservedSearch is not null)
            {
                SearchBox.Text = preservedSearch;
            }

            if (preservedColText is not null)
            {
                ColumnFilterBox.Text = preservedColText;
            }

            ApplyFilters();
            RefreshHeaderFilterIcons();
        }
    }

    /// <summary>True si dos tablas tienen el mismo conjunto de columnas (por nombre).</summary>
    private static bool ColumnsMatch(DataTable a, DataTable b)
    {
        if (a.Columns.Count != b.Columns.Count)
        {
            return false;
        }

        foreach (DataColumn c in a.Columns)
        {
            if (!b.Columns.Contains(c.ColumnName))
            {
                return false;
            }
        }

        return true;
    }

    private WorkbookSheet EnsureDataSheet()
    {
        var data = _sheets.FirstOrDefault(s => !s.IsPivot && !s.IsExtraView);
        if (data is null)
        {
            data = new WorkbookSheet
            {
                Name = string.IsNullOrWhiteSpace(_session?.ViewLabel) ? "Datos" : _session!.ViewLabel,
                IsPivot = false,
                Schema = _session?.Schema,
                ViewName = _session?.View,
            };
            _sheets.Insert(0, data);
        }
        else if (!string.IsNullOrWhiteSpace(_session?.ViewLabel))
        {
            data.Name = _session.ViewLabel;
            data.Schema ??= _session?.Schema;
            data.ViewName ??= _session?.View;
        }

        return data;
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

        // Aplicar el filtro combinado. Si falla (una cláusula inválida), NO
        // borramos todos los filtros: reintentamos descartando solo las
        // cláusulas que rompen, para no perder el resto (síntoma "se dañan").
        var applied = TrySetRowFilter(string.Join(" AND ", parts));
        if (!applied)
        {
            var valid = new List<string>();
            foreach (var part in parts)
            {
                if (SafeToAdd(valid, part))
                {
                    valid.Add(part);
                }
                else
                {
                    AppLog.Warn($"Cláusula de filtro descartada por inválida: {part}");
                }
            }

            TrySetRowFilter(string.Join(" AND ", valid));
        }

        UpdateMetrics();
        RebuildPivotsUsing(_table);
    }

    /// <summary>Intenta fijar el RowFilter; devuelve false si la expresión es inválida.</summary>
    private bool TrySetRowFilter(string expression)
    {
        if (_view is null)
        {
            return false;
        }

        try
        {
            _view.RowFilter = expression;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Comprueba si agregar una cláusula al conjunto sigue siendo válido.</summary>
    private bool SafeToAdd(List<string> current, string candidate)
    {
        if (_view is null)
        {
            return false;
        }

        var test = current.Count == 0 ? candidate : string.Join(" AND ", current) + " AND " + candidate;
        try
        {
            _view.RowFilter = test;
            return true;
        }
        catch
        {
            return false;
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
            HintText.Text = $"{_sheets.Count} hojas · {_sheets.Count(s => !s.IsPivot)} vistas";
        }

        RefreshTotals();
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
        if (RibbonVista is not null)
        {
            RibbonVista.Visibility = TabVista.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
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
        var source = _activeSheet is { IsPivot: false } data
            ? data
            : _activeSheet?.PivotSource ?? _sheets.FirstOrDefault(s => !s.IsPivot);
        var n = _sheets.Count(s => s.IsPivot) + 1;
        var sheet = new WorkbookSheet
        {
            Name = n == 1 ? "Tabla dinámica1" : $"Tabla dinámica{n}",
            IsPivot = true,
            PivotSource = source,
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
        if (sender is not FrameworkElement { Tag: WorkbookSheet sheet } || !sheet.CanClose)
        {
            return;
        }

        var index = _sheets.IndexOf(sheet);
        sheet.Result?.Dispose();
        if (sheet.IsExtraView
            && sheet.SourceTable is not null
            && !_sheets.Any(s => !ReferenceEquals(s, sheet) && ReferenceEquals(s.SourceTable, sheet.SourceTable)))
        {
            if (ReferenceEquals(_table, sheet.SourceTable))
            {
                _table = null;
                _view = null;
            }

            sheet.SourceTable.Dispose();
            sheet.SourceTable = null;
        }

        foreach (var pivot in _sheets.Where(s => s.IsPivot && ReferenceEquals(s.PivotSource, sheet)).ToList())
        {
            pivot.Result?.Dispose();
            _sheets.Remove(pivot);
        }

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

    private void ActivateSheet(WorkbookSheet sheet, bool capture = true)
    {
        if (capture)
        {
            CaptureActiveDataState();
        }
        foreach (var s in _sheets)
        {
            s.IsActive = ReferenceEquals(s, sheet);
        }

        _activeSheet = sheet;
        RefreshSheetTabs();
        _openFilter?.Close();

        if (sheet.IsPivot)
        {
            var sourceView = ResolvePivotSource(sheet);
            var sourceTable = sourceView?.Table;
            if (sourceTable is not null && sourceView is not null)
            {
                PivotSidebar.AttachConfig(sheet.Config);
                PivotSidebar.SetFields(sourceTable.Columns.Cast<DataColumn>().Select(c => c.ColumnName), sourceView);
            }

            AddViewSidebar.Visibility = Visibility.Collapsed;
            ColumnSidebar.Visibility = Visibility.Collapsed;
            PivotSidebar.Visibility = Visibility.Visible;
            Grid.IsReadOnly = true;
            Grid.ItemsSource = sheet.Result?.DefaultView;
            TabAnalisis.IsChecked = true;
            StatusText.Text = sheet.Config.CanBuild
                ? $"Tabla dinámica · {sheet.Snapshot?.LeafCount ?? sheet.Result?.Rows.Count ?? 0:N0} grupos"
                : "Arrastre campos a Filas, Columnas o Valores.";
        }
        else if (sheet.IsBlank)
        {
            PivotSidebar.Visibility = Visibility.Collapsed;
            AddViewSidebar.Visibility = Visibility.Collapsed;
            ColumnSidebar.Visibility = Visibility.Collapsed;
            _table = sheet.SourceTable;
            _view = sheet.SourceTable?.DefaultView;
            Grid.IsReadOnly = false; // editable como Excel
            Grid.ItemsSource = _view;
            TabDatos.IsChecked = true;
            StatusText.Text = "Hoja en blanco (editable)";
        }
        else
        {
            RestoreDataState(sheet);
            Grid.IsReadOnly = true;
            PivotSidebar.Visibility = Visibility.Collapsed;
            if (_view is not null)
            {
                Grid.ItemsSource = _view;
            }

            TabDatos.IsChecked = true;
            StatusText.Text = $"{_view?.Count ?? 0:N0} registros";
        }

        RefreshPivotFilterBar();
        UpdateMetrics();
        RefreshColumnPanel();
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
        RefreshPivotFilterBar();
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
                PivotSource = _sheets.FirstOrDefault(s => !s.IsPivot && !s.IsExtraView),
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
        foreach (var sheet in _sheets.Where(s => s.IsPivot && s.Config.CanBuild))
        {
            var source = ResolvePivotSource(sheet);
            if (source is null)
            {
                continue;
            }

            ApplyPivotBuild(sheet, PivotEngine.Build(source, sheet.Config));
        }

        if (bindActive && _activeSheet?.IsPivot == true)
        {
            Grid.ItemsSource = _activeSheet.Result?.DefaultView;
        }
    }

    private DataView? ResolvePivotSource(WorkbookSheet pivot)
    {
        var table = pivot.PivotSource?.SourceTable
                    ?? _sheets.FirstOrDefault(s => !s.IsPivot && !s.IsExtraView)?.SourceTable
                    ?? _table;
        return table?.DefaultView;
    }

    private void RebuildPivot()
    {
        if (_activeSheet is not { IsPivot: true } sheet)
        {
            return;
        }

        var source = ResolvePivotSource(sheet);
        if (source is null)
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

        RebuildPivotAsync(sheet, source);
    }

    /// <summary>
    /// Construye el pivote en segundo plano para no congelar la ventana con
    /// vistas grandes. Muestra un overlay de progreso mientras trabaja.
    /// </summary>
    private async void RebuildPivotAsync(WorkbookSheet sheet, DataView source)
    {
        // Snapshot inmutable de la config para pasarlo al hilo de fondo sin
        // riesgo de que cambie mientras se calcula.
        var rowCount = source.Count;
        var showOverlay = rowCount > 20_000; // solo para volúmenes que se notan
        if (showOverlay)
        {
            ShowOverlay("Armando tabla dinámica...", $"Procesando {rowCount:N0} filas", true);
        }

        try
        {
            var built = await Task.Run(() => PivotEngine.Build(source, sheet.Config));
            // Si el usuario cambió de hoja mientras calculaba, no pisar la UI.
            if (!ReferenceEquals(_activeSheet, sheet))
            {
                built.Table?.Dispose();
                return;
            }

            ApplyPivotBuild(sheet, built);
            Grid.ItemsSource = sheet.Result?.DefaultView;
            RefreshPivotFilterBar();
            var groups = sheet.Snapshot?.LeafCount ?? sheet.Result?.Rows.Count ?? 0;
            StatusText.Text = $"Tabla dinámica · {groups:N0} grupos"
                              + (sheet.Config.Columns.Count > 0
                                  ? $" (máx. {PivotEngine.MaxCrossColumns} columnas)"
                                  : "");
            UpdateMetrics();
        }
        catch (Exception ex)
        {
            AppLog.Error("Tabla dinámica falló", ex);
            MessageBox.Show(this, ex.Message, "Tabla dinámica", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (showOverlay)
            {
                HideOverlay();
            }
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

    private void OnAddView(object sender, RoutedEventArgs e) => OpenAddViewPanel();

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
                        kind is ColumnDataKind.Moneda or ColumnDataKind.Numero
                            or ColumnDataKind.Entero or ColumnDataKind.Porcentaje
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
            else if (kind == ColumnDataKind.Porcentaje)
            {
                text.Binding = new Binding("[" + name.Replace("]", @"\]") + "]")
                {
                    Converter = PorcentajeDisplayConverter.Instance,
                    Mode = BindingMode.OneWay,
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

    /// <summary>
    /// Resumen de la selección en la barra de estado, como Excel:
    /// Promedio · Recuento · Suma (y Mín/Máx) de las celdas numéricas seleccionadas.
    /// </summary>
    private void OnSelectedCellsChanged(object? sender, SelectedCellsChangedEventArgs e)
    {
        if (SelectionSummaryText is null)
        {
            return;
        }

        // Debounce: al arrastrar la selección este evento se dispara cientos de
        // veces. En vez de recalcular en cada uno, esperamos a que la selección
        // se estabilice (~120 ms) y calculamos una sola vez.
        _selectionTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _selectionTimer.Tick -= OnSelectionTick;
        _selectionTimer.Tick += OnSelectionTick;
        _selectionTimer.Stop();
        _selectionTimer.Start();
    }

    private void OnSelectionTick(object? sender, EventArgs e)
    {
        _selectionTimer?.Stop();
        ComputeSelectionSummary();
    }

    private void ComputeSelectionSummary()
    {
        if (SelectionSummaryText is null)
        {
            return;
        }

        var cells = Grid.SelectedCells;
        if (cells is null || cells.Count <= 1)
        {
            SelectionSummaryText.Text = "";
            return;
        }

        // Tope de seguridad: sumar cientos de miles de celdas al vuelo no aporta
        // y podría trabar la UI. Excel también limita el cálculo en la barra.
        const int maxCells = 100_000;
        if (cells.Count > maxCells)
        {
            SelectionSummaryText.Text = $"Recuento: {cells.Count:N0} (selección muy grande)";
            return;
        }

        var es = CultureInfo.GetCultureInfo("es-CO");
        var count = 0;
        var numCount = 0;
        double sum = 0, min = double.MaxValue, max = double.MinValue;

        foreach (var cell in cells)
        {
            if (cell.Item is not DataRowView row || cell.Column is null)
            {
                continue;
            }

            var name = ColumnKey(cell.Column);
            if (string.IsNullOrEmpty(name)
                || PivotEngine.IsHiddenColumn(name)
                || !row.Row.Table.Columns.Contains(name))
            {
                continue;
            }

            count++;
            var raw = row[name];
            if (raw is DBNull or null)
            {
                continue;
            }

            if (double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
                || double.TryParse(Convert.ToString(raw, es),
                    NumberStyles.Any, es, out v))
            {
                numCount++;
                sum += v;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        if (count <= 1)
        {
            SelectionSummaryText.Text = "";
            return;
        }

        if (numCount >= 2)
        {
            var avg = sum / numCount;
            SelectionSummaryText.Text =
                $"Promedio: {avg.ToString("N2", es)}   Recuento: {count}   Mín: {min.ToString("N2", es)}   Máx: {max.ToString("N2", es)}   Suma: {sum.ToString("N2", es)}";
        }
        else
        {
            SelectionSummaryText.Text = $"Recuento: {count}";
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
                : kind == ColumnDataKind.Porcentaje
                    ? PorcentajeDisplayConverter.Format(cell) ?? ""
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

        // Para columnas de fecha/hora agrupamos por DÍA (ignoramos la hora),
        // igual que el árbol Año>Mes>Día de Excel. Así una columna datetime con
        // miles de timestamps únicos no revienta el umbral ni cae en "textOnly":
        // lo que importa para el filtro por lista son los días distintos.
        var isDateCol = _table.Columns[column]!.DataType == typeof(DateTime);

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
                // Solo la fecha (sin hora) para el árbol y la lista de valores.
                s = dt.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            }
            else
            {
                // IMPORTANTE: usar InvariantCulture, la MISMA representación que
                // usa el RowFilter con CONVERT(col,'System.String'). Con
                // CurrentCulture, números/decimales/bool no coincidían y el
                // filtro "no mostraba nada".
                s = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
            }
            if (s.Length == 0)
            {
                blanks = true;
                continue;
            }

            set.Add(s);
            // Las columnas de fecha nunca caen en textOnly: por muchos días que
            // haya, el árbol Año>Mes>Día los organiza sin problema.
            if (!isDateCol && set.Count > maxValues)
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

        var col = _table.Columns[column]!;
        var isDate = col.DataType == typeof(DateTime);
        var isNumeric = col.DataType == typeof(double) || col.DataType == typeof(decimal)
            || col.DataType == typeof(float) || col.DataType == typeof(int)
            || col.DataType == typeof(long) || col.DataType == typeof(short);
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

            // Operadores numéricos (mayor/menor/entre): comparar por VALOR sobre
            // la columna cruda si es numérica; si no, sobre su versión numérica.
            var numOps = filter.TextOperator is TextFilterOperator.GreaterThan
                or TextFilterOperator.GreaterOrEqual or TextFilterOperator.LessThan
                or TextFilterOperator.LessOrEqual or TextFilterOperator.Between;

            if (numOps && double.TryParse(filter.TextValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var n1))
            {
                var numCol = isNumeric ? $"[{EscapeCol(column)}]" : $"CONVERT([{EscapeCol(column)}], 'System.Double')";
                var lit1 = n1.ToString(CultureInfo.InvariantCulture);
                switch (filter.TextOperator)
                {
                    case TextFilterOperator.GreaterThan:
                        parts.Add($"{numCol} > {lit1}");
                        break;
                    case TextFilterOperator.GreaterOrEqual:
                        parts.Add($"{numCol} >= {lit1}");
                        break;
                    case TextFilterOperator.LessThan:
                        parts.Add($"{numCol} < {lit1}");
                        break;
                    case TextFilterOperator.LessOrEqual:
                        parts.Add($"{numCol} <= {lit1}");
                        break;
                    case TextFilterOperator.Between:
                        if (double.TryParse(filter.TextValue2, NumberStyles.Any, CultureInfo.InvariantCulture, out var n2))
                        {
                            var lo = Math.Min(n1, n2).ToString(CultureInfo.InvariantCulture);
                            var hi = Math.Max(n1, n2).ToString(CultureInfo.InvariantCulture);
                            parts.Add($"({numCol} >= {lo} AND {numCol} <= {hi})");
                        }
                        else
                        {
                            parts.Add($"{numCol} >= {lit1}");
                        }
                        break;
                }
            }
            else
            {
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
                    // Los valores seleccionados son días (dd/MM/yyyy). Filtramos
                    // por rango [día, día+1) para incluir cualquier hora de ese día.
                    // Si un valor no parsea como fecha (dato mixto), caemos a
                    // comparación de texto para no excluir filas silenciosamente.
                    foreach (var v in chunk)
                    {
                        if (DataFileParser.TryParseFecha(v, out var dt))
                        {
                            var d = dt.Date;
                            valueParts.Add($"({name} >= #{d:MM/dd/yyyy}# AND {name} < #{d.AddDays(1):MM/dd/yyyy}#)");
                        }
                        else
                        {
                            var textCol = $"CONVERT([{EscapeCol(column)}], 'System.String')";
                            valueParts.Add($"{textCol} = '{v.Replace("'", "''")}'");
                        }
                    }
                }
                else if (isNumeric)
                {
                    // Columnas numéricas: comparar por VALOR, no por texto.
                    // CONVERT(col,'System.String') depende de la cultura y no
                    // coincidía con el string de la lista -> el filtro daba 0.
                    // Usamos el nombre crudo [col] con literales numéricos
                    // invariantes (los que no parsean se comparan como texto).
                    var nums = new List<string>();
                    foreach (var v in chunk)
                    {
                        if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                        {
                            nums.Add(d.ToString(CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            valueParts.Add($"CONVERT([{EscapeCol(column)}], 'System.String') = '{v.Replace("'", "''")}'");
                        }
                    }

                    if (nums.Count > 0)
                    {
                        valueParts.Add($"[{EscapeCol(column)}] IN ({string.Join(",", nums)})");
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

        // Acotar el popup al MONITOR donde está la ventana propietaria (no al
        // primario). SystemParameters.WorkArea es solo el monitor primario, lo
        // que hacía que el filtro saltara a la pantalla 1 en multi-monitor.
        var bounds = MonitorBoundsFor(window.Owner ?? window);
        var height = window.Height > 0 ? window.Height : 428;
        var width = window.Width > 0 ? window.Width : 300;

        if (window.Left + width > bounds.Right)
        {
            window.Left = Math.Max(bounds.Left, bounds.Right - width);
        }

        if (window.Left < bounds.Left)
        {
            window.Left = bounds.Left;
        }

        if (window.Top + height > bounds.Bottom)
        {
            // Abrir hacia arriba del anclaje si no cabe debajo.
            window.Top = Math.Max(bounds.Top, point.Y - height - target.ActualHeight);
        }
    }

    /// <summary>
    /// Límites (en unidades WPF) del monitor donde está la ventana dada. Usa
    /// Win32 (MonitorFromWindow + GetMonitorInfo) para soportar multi-monitor
    /// sin depender de WinForms.
    /// </summary>
    private static Rect MonitorBoundsFor(Window win)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            if (hwnd != IntPtr.Zero)
            {
                const uint MONITOR_DEFAULTTONEAREST = 2;
                var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        var wa = info.rcWork; // píxeles del dispositivo
                        var src = PresentationSource.FromVisual(win);
                        var m = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                        var topLeft = m.Transform(new Point(wa.left, wa.top));
                        var bottomRight = m.Transform(new Point(wa.right, wa.bottom));
                        return new Rect(topLeft, bottomRight);
                    }
                }
            }
        }
        catch
        {
            // Fallback abajo.
        }

        return new Rect(
            SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top,
            SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

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
            var schema = _activeSheet?.Schema ?? _session.Schema;
            var view = _activeSheet?.ViewName ?? _session.View;
            var saved = ColumnLayoutStore.Load(_session, schema, view);
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

        var schema = _activeSheet?.Schema ?? _session.Schema;
        var view = _activeSheet?.ViewName ?? _session.View;
        ColumnLayoutStore.Save(_session, _columnOverrides, schema, view);
        if (_activeSheet is { IsPivot: false })
        {
            _activeSheet.Overrides.Clear();
            _activeSheet.Overrides.AddRange(_columnOverrides);
        }
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
            ("Porcentaje", "Porcentaje"),
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
        var autofit = new MenuItem { Header = "Autoajustar columna" };
        autofit.Click += (_, _) => AutoFitSelectedColumn();
        var hide = new MenuItem { Header = "Ocultar columna" };
        hide.Click += (_, _) => HideSelectedColumn();
        var showAll = new MenuItem { Header = "Mostrar todas las columnas" };
        showAll.Click += OnShowAllColumns;
        menu.Items.Add(autofit);
        menu.Items.Add(hide);
        menu.Items.Add(showAll);
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

        // Pegar estilo Excel (Ctrl+V) solo en hojas en blanco editables.
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && _activeSheet is { IsBlank: true })
        {
            e.Handled = true;
            PasteIntoBlankSheet();
        }
    }

    /// <summary>
    /// Pega el contenido del portapapeles (texto tabulado, como copia Excel) en
    /// la hoja en blanco a partir de la celda actual, expandiendo filas y
    /// columnas si es necesario.
    /// </summary>
    private void PasteIntoBlankSheet()
    {
        if (_table is null || !Clipboard.ContainsText())
        {
            return;
        }

        var text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var rows = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None);
        // Quitar última línea vacía típica del portapapeles.
        if (rows.Length > 1 && rows[^1].Length == 0)
        {
            rows = rows[..^1];
        }

        var startRow = Grid.Items.IndexOf(Grid.CurrentItem);
        if (startRow < 0)
        {
            startRow = 0;
        }

        var startCol = Grid.CurrentCell.Column is null ? 0 : Grid.Columns.IndexOf(Grid.CurrentCell.Column);
        if (startCol < 0)
        {
            startCol = 0;
        }

        _table.BeginLoadData();
        try
        {
            for (var r = 0; r < rows.Length; r++)
            {
                var cells = rows[r].Split('\t');
                var targetRow = startRow + r;
                while (targetRow >= _table.Rows.Count)
                {
                    _table.Rows.Add(_table.NewRow());
                }

                for (var c = 0; c < cells.Length; c++)
                {
                    var targetCol = startCol + c;
                    while (targetCol >= _table.Columns.Count)
                    {
                        _table.Columns.Add(ColLetter(_table.Columns.Count), typeof(string));
                    }

                    _table.Rows[targetRow][targetCol] = cells[c];
                }
            }
        }
        finally
        {
            _table.EndLoadData();
        }

        // Rebindear para reflejar columnas nuevas si se agregaron.
        Grid.ItemsSource = null;
        Grid.ItemsSource = _table.DefaultView;
        StatusText.Text = "Contenido pegado";
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            TabFiltros.IsChecked = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
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
