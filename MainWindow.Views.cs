using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SaraBI.Dialogs;
using SaraBI.Models;
using SaraBI.Services;

namespace SaraBI;

/// <summary>
/// Item de la barra de filtros de página de la tabla dinámica (encima de la
/// tabla, como Excel). Column = campo; Choices = valores; Selected = valor activo.
/// </summary>
public sealed class PivotPageFilterItem : INotifyPropertyChanged
{
    private string _selected = "(Todos)";

    public string Column { get; init; } = "";
    public List<string> Choices { get; init; } = new() { "(Todos)" };

    public string Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return;
            }

            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

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

    /// <summary>
    /// Rellena la barra de filtros de página (encima de la tabla) con los campos
    /// de la zona "Filtros" del pivote activo. Se llama al activar la hoja y al
    /// reconstruir. En Excel esta barra es la forma principal de filtrar.
    /// </summary>
    private void RefreshPivotFilterBar()
    {
        if (PivotFilterBar is null || PivotFilterItems is null)
        {
            return;
        }

        if (_activeSheet is not { IsPivot: true } sheet || sheet.Config.Filters.Count == 0)
        {
            PivotFilterBar.Visibility = Visibility.Collapsed;
            PivotFilterItems.ItemsSource = null;
            return;
        }

        var sourceView = ResolvePivotSource(sheet);
        var items = new List<PivotPageFilterItem>();
        foreach (var col in sheet.Config.Filters)
        {
            var choices = new List<string> { "(Todos)" };
            if (sourceView?.Table?.Columns.Contains(col) == true)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DataRowView row in sourceView)
                {
                    set.Add(PivotEngine.FormatCell(row, col));
                    if (set.Count >= 500)
                    {
                        break;
                    }
                }

                choices.AddRange(set.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase));
            }

            var selected = sheet.Config.FilterSelections.TryGetValue(col, out var sel)
                           && !string.IsNullOrWhiteSpace(sel)
                ? sel
                : "(Todos)";

            items.Add(new PivotPageFilterItem
            {
                Column = col,
                Choices = choices,
                Selected = choices.Contains(selected) ? selected : "(Todos)",
            });
        }

        PivotFilterItems.ItemsSource = items;
        PivotFilterBar.Visibility = Visibility.Visible;
    }

    private void OnPivotPageFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: PivotPageFilterItem item }
            || _activeSheet is not { IsPivot: true } sheet)
        {
            return;
        }

        var current = sheet.Config.FilterSelections.TryGetValue(item.Column, out var cur) ? cur : "(Todos)";
        if (string.Equals(current, item.Selected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        sheet.Config.FilterSelections[item.Column] = item.Selected;
        // Mantener el panel lateral en sincronía y reconstruir.
        PivotSidebar.AttachConfig(sheet.Config);
        RebuildPivot();
        ScheduleSavePivots();
    }

    /// <summary>
    /// Crea una hoja en blanco editable (tipo Excel) y la activa. Se puede
    /// escribir, copiar y pegar libremente.
    /// </summary>
    /// <summary>Abre el menú del botón + para elegir tipo de hoja nueva.</summary>
    private void OnNewSheetMenu(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } btn)
        {
            menu.PlacementTarget = btn;
            menu.IsOpen = true;
        }
    }

    private void OnNewBlankSheet(object sender, RoutedEventArgs e) => CreateBlankSheet();

    private void CreateBlankSheet()
    {
        var n = _sheets.Count(s => s.IsBlank) + 1;
        var table = new DataTable("Hoja");
        // Hoja en blanco con 10 columnas (A..J) y 100 filas vacías para empezar.
        for (var c = 0; c < 10; c++)
        {
            table.Columns.Add(ColumnLetter(c), typeof(string));
        }

        for (var r = 0; r < 100; r++)
        {
            table.Rows.Add(table.NewRow());
        }

        var sheet = new WorkbookSheet
        {
            Name = n == 1 ? "Hoja en blanco" : $"Hoja en blanco {n}",
            IsBlank = true,
            SourceTable = table,
        };
        _sheets.Add(sheet);
        RefreshSheetTabs();
        ActivateSheet(sheet, capture: true);
        StatusText.Text = "Hoja en blanco. Escriba, copie y pegue como en Excel.";
    }

    private static string ColumnLetter(int index)
    {
        var n = index + 1;
        var s = "";
        while (n > 0)
        {
            var m = (n - 1) % 26;
            s = (char)('A' + m) + s;
            n = (n - 1) / 26;
        }

        return s;
    }

    /// <summary>
    /// Abre el diálogo de columna calculada y, si el usuario confirma, agrega la
    /// columna al DataTable de la hoja de datos activa aplicando la fórmula.
    /// </summary>
    private void OnAddCalcColumn(object sender, RoutedEventArgs e)
    {
        if (!RequireBrowserSession())
        {
            return;
        }

        if (IsPivotSheet)
        {
            MessageBox.Show(this,
                "Las columnas calculadas se agregan sobre una hoja de datos, no sobre una tabla dinámica.",
                "Columna calculada", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var table = BoundTable ?? _table;
        if (table is null)
        {
            MessageBox.Show(this,
                "Cargue primero una vista (Actualizar todo) para agregar una columna calculada.",
                "Columna calculada", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Resolver de hojas para BUSCARV entre vistas cargadas (por nombre de hoja).
        DataTable? LookupResolver(string sheetName) =>
            _sheets.FirstOrDefault(s =>
                !s.IsPivot
                && s.SourceTable is not null
                && (string.Equals(s.Name, sheetName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.ViewName, sheetName, StringComparison.OrdinalIgnoreCase)))?.SourceTable;

        var engine = new FormulaEngine(LookupResolver);
        var columns = table.Columns.Cast<DataColumn>()
            .Select(c => c.ColumnName)
            .Where(n => !PivotEngine.IsHiddenColumn(n))
            .ToList();

        var dialog = new CalcColumnDialog(columns, engine) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            Grid.ItemsSource = null;
            engine.AddCalculatedColumn(table, dialog.ColumnName, dialog.Formula);
            RebindGrid(dialog.ColumnName);
            RefreshPivotFieldList();
            StatusText.Text = $"Columna calculada «{dialog.ColumnName}» agregada.";
        }
        catch (Exception ex)
        {
            AppLog.Error("Columna calculada falló", ex);
            MessageBox.Show(this, ex.Message, "Columna calculada", MessageBoxButton.OK, MessageBoxImage.Error);
            RebindGrid(null);
        }
        finally
        {
            Mouse.OverrideCursor = null;
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

        // Descarga estilo navegador: sin diálogo "Guardar como".
        // El archivo se genera directo en la carpeta Descargas del usuario,
        // con un nombre único (igual que hace un navegador), y al terminar se
        // avisa con opción de abrirlo.
        var baseName = SanitizeFile(_activeSheet?.Name ?? _session?.View ?? "vista");
        var fileName = $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        var targetPath = UniqueDownloadPath(fileName);

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

                ShowOverlay("Generando Excel...", "Preparando la tabla dinámica", true);
                var pivot = CloneVisible(_activeSheet.Result);
                await Task.Run(() => ExcelExporter.Save(pivot, targetPath));
                HideOverlay();
                NotifyDownloadReady(targetPath);
                return;
            }

            // PRIORIDAD: si ya hay datos cargados en pantalla, generamos el Excel
            // LOCAL (rápido, ~2s para 400k filas) y además respeta los filtros y
            // las columnas visibles/orden que el usuario ve. Solo usamos el
            // archivo del servidor como respaldo cuando no hay datos locales.
            var table = BuildExportTable();
            if (table is not null && table.Rows.Count > 0)
            {
                ShowOverlay("Generando Excel...", $"Escribiendo {table.Rows.Count:N0} filas", true);
                await Task.Run(() => ExcelExporter.Save(table, targetPath));
                HideOverlay();
                NotifyDownloadReady(targetPath);
                return;
            }

            // Respaldo: descargar el archivo generado en el servidor.
            var jobId = _activeSheet?.LastJobId;
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                ShowOverlay("Descargando Excel...", "Obteniendo el archivo generado en el servidor", true);
                try
                {
                    await _api.DownloadExcelFileAsync(jobId, targetPath, CancellationToken.None);
                    HideOverlay();
                    NotifyDownloadReady(targetPath);
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Descarga xlsx del servidor falló: {ex.Message}");
                    HideOverlay();
                }
            }

            if (table is null)
            {
                MessageBox.Show(this,
                    "Todavía no hay datos para exportar. Pulse Actualizar todo.",
                    "Descargar Excel", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Última opción: exportar aunque esté vacío (encabezados).
            ShowOverlay("Generando Excel...", $"Escribiendo {table.Rows.Count:N0} filas", true);
            await Task.Run(() => ExcelExporter.Save(table, targetPath));
            HideOverlay();
            NotifyDownloadReady(targetPath);
        }
        catch (Exception ex)
        {
            HideOverlay();
            MessageBox.Show(this, ex.Message, "Descargar Excel", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Carpeta Descargas del usuario. Usa la conocida "Downloads" de Windows y,
    /// si no está disponible, cae a Perfil\Downloads o al escritorio.
    /// </summary>
    private static string DownloadsFolder()
    {
        try
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var downloads = Path.Combine(profile, "Downloads");
            if (Directory.Exists(downloads))
            {
                return downloads;
            }

            Directory.CreateDirectory(downloads);
            return downloads;
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        }
    }

    /// <summary>
    /// Devuelve una ruta libre en Descargas. Si el archivo ya existe agrega
    /// " (2)", " (3)"... igual que un navegador, para no sobrescribir.
    /// </summary>
    private static string UniqueDownloadPath(string fileName)
    {
        var folder = DownloadsFolder();
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = Path.Combine(folder, fileName);
        var i = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(folder, $"{name} ({i++}){ext}");
        }

        return candidate;
    }

    /// <summary>
    /// Aviso no bloqueante estilo navegador: informa en la barra de estado y
    /// ofrece abrir el archivo descargado.
    /// </summary>
    private void NotifyDownloadReady(string path)
    {
        var fileName = Path.GetFileName(path);
        StatusText.Text = $"Descargado: {fileName}";
        AppLog.Info($"Excel descargado en {path}");

        var choice = MessageBox.Show(
            this,
            $"La descarga terminó.\n\n{fileName}\nse guardó en la carpeta Descargas.\n\n¿Desea abrirlo ahora?",
            "Descarga completada",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"No se pudo abrir el Excel descargado: {ex.Message}");
            // Como plan B, abrir la carpeta Descargas y seleccionar el archivo.
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                });
            }
            catch
            {
                // Sin acción: el archivo ya está en Descargas.
            }
        }
    }

    /// <summary>
    /// Arma la tabla a exportar respetando lo que el usuario ve: solo las filas
    /// filtradas (y en el orden actual del DataView), y solo las columnas
    /// visibles del grid en su orden de pantalla, con sus encabezados.
    /// </summary>
    private DataTable? BuildExportTable()
    {
        if (_view is null || _table is null)
        {
            return _view?.ToTable() ?? _table;
        }

        // Columnas visibles del grid, en orden de pantalla (DisplayIndex).
        var gridCols = Grid.Columns
            .Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex)
            .Select(c => ColumnKey(c))
            .Where(n => !string.IsNullOrEmpty(n) && _table.Columns.Contains(n))
            .Cast<string>()
            .ToList();

        // Si por algún motivo no hay columnas resueltas, caer al comportamiento previo.
        if (gridCols.Count == 0)
        {
            return _view.ToTable();
        }

        var export = new DataTable("Datos");
        foreach (var name in gridCols)
        {
            var src = _table.Columns[name]!;
            export.Columns.Add(DisplayHeader(name), src.DataType);
        }

        export.BeginLoadData();
        try
        {
            foreach (DataRowView drv in _view)
            {
                var row = export.NewRow();
                for (var i = 0; i < gridCols.Count; i++)
                {
                    row[i] = drv[gridCols[i]];
                }

                export.Rows.Add(row);
            }
        }
        finally
        {
            export.EndLoadData();
        }

        return export;
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
