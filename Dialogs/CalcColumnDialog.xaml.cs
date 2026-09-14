using System.Windows;
using System.Windows.Controls;
using SaraBI.Services;

namespace SaraBI.Dialogs;

public partial class CalcColumnDialog : Window
{
    private readonly FormulaEngine _engine;
    private readonly List<string> _columns;

    public string ColumnName => NameBox.Text.Trim();
    public string Formula => FormulaBox.Text.Trim();

    private static readonly (string Label, string Insert)[] Functions =
    {
        ("SI(cond, siVerdadero, siFalso)", "SI(; ; )"),
        ("SIERROR(valor, siError)", "SIERROR(; )"),
        ("Y(a, b, ...)", "Y(; )"),
        ("O(a, b, ...)", "O(; )"),
        ("NO(cond)", "NO()"),
        ("CONCATENAR(a, b, ...)", "CONCATENAR(; )"),
        ("MAYUSC(texto)", "MAYUSC()"),
        ("MINUSC(texto)", "MINUSC()"),
        ("ESPACIOS(texto)", "ESPACIOS()"),
        ("LARGO(texto)", "LARGO()"),
        ("IZQUIERDA(texto, n)", "IZQUIERDA(; )"),
        ("DERECHA(texto, n)", "DERECHA(; )"),
        ("REDONDEAR(numero, decimales)", "REDONDEAR(; )"),
        ("ABS(numero)", "ABS()"),
        ("ENTERO(numero)", "ENTERO()"),
        ("BUSCARV(valor, 'Hoja', 'ColClave', 'ColResultado')", "BUSCARV(; ''; ''; '')"),
    };

    public CalcColumnDialog(IEnumerable<string> columns, FormulaEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        _columns = columns.ToList();
        ColumnsList.ItemsSource = _columns;
        FunctionsList.ItemsSource = Functions.Select(f => f.Label).ToList();
        NameBox.Text = SuggestName();
        Loaded += (_, _) => FormulaBox.Focus();
    }

    private string SuggestName()
    {
        var baseName = "Calculada";
        var n = 1;
        var name = baseName;
        while (_columns.Contains(name))
        {
            name = $"{baseName}{++n}";
        }

        return name;
    }

    private void OnColumnPick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ColumnsList.SelectedItem is string col)
        {
            InsertAtCaret($"[{col}]");
        }
    }

    private void OnFunctionPick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FunctionsList.SelectedIndex < 0)
        {
            return;
        }

        InsertAtCaret(Functions[FunctionsList.SelectedIndex].Insert);
    }

    private void InsertAtCaret(string text)
    {
        var idx = FormulaBox.CaretIndex;
        FormulaBox.Text = FormulaBox.Text.Insert(idx, text);
        FormulaBox.CaretIndex = idx + text.Length;
        FormulaBox.Focus();
    }

    private void OnFormulaChanged(object sender, TextChangedEventArgs e)
    {
        if (ErrorText is null)
        {
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ColumnName))
        {
            ShowError("Escriba un nombre para la columna.");
            return;
        }

        if (string.IsNullOrWhiteSpace(Formula))
        {
            ShowError("Escriba una fórmula.");
            return;
        }

        var error = _engine.Validate(Formula, _columns);
        if (error is not null)
        {
            ShowError("La fórmula no es válida: " + error);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
