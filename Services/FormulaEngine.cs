using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NCalc;

namespace SaraBI.Services;

/// <summary>
/// Motor de columnas calculadas estilo Excel. Evalúa una fórmula fila por fila
/// sobre un DataTable, resolviendo referencias a columnas y funciones comunes en
/// español (SI, BUSCARV, CONCATENAR, REDONDEAR, IZQUIERDA, etc.).
///
/// Se apoya en NCalc para el parseo/evaluación de la expresión y añade las
/// funciones de Excel más usadas. No cubre las 450 funciones de Excel: cubre las
/// prácticas para calcular, validar y cruzar datos.
///
/// Referencias a columnas: [Nombre Columna]. Ejemplos:
///   [Cantidad] * [Precio]
///   [Nombre] + ' ' + [Apellido]
///   SI([Saldo] &gt; 0, 'Debe', 'Al dia')
///   BUSCARV([Codigo], 'OtraVista', 'CodProducto', 'Descripcion')
/// </summary>
public sealed class FormulaEngine
{
    private readonly Func<string, DataTable?> _lookupTableResolver;

    public FormulaEngine(Func<string, DataTable?>? lookupTableResolver = null)
    {
        _lookupTableResolver = lookupTableResolver ?? (_ => null);
    }

    /// <summary>
    /// Valida la fórmula evaluándola una vez con valores dummy de las columnas.
    /// Devuelve null si es válida, o un mensaje de error legible.
    /// </summary>
    public string? Validate(string formula, IEnumerable<string> columns)
    {
        if (string.IsNullOrWhiteSpace(formula))
        {
            return "La fórmula está vacía.";
        }

        try
        {
            var expr = BuildExpression(formula);
            foreach (var col in columns)
            {
                expr.Parameters[SanitizeParam(col)] = 0d;
            }

            expr.Evaluate();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Agrega una columna calculada aplicando la fórmula a cada fila.</summary>
    public void AddCalculatedColumn(DataTable table, string columnName, string formula)
    {
        var unique = UniqueColumnName(table, columnName);
        table.Columns.Add(new DataColumn(unique, typeof(string)));

        var paramMap = BuildParamMap(table);
        var expr = BuildExpression(formula);

        table.BeginLoadData();
        try
        {
            foreach (DataRow row in table.Rows)
            {
                object? result;
                try
                {
                    foreach (var (param, source) in paramMap)
                    {
                        expr.Parameters[param] = CellValue(row, source);
                    }

                    result = expr.Evaluate();
                }
                catch
                {
                    result = "#ERROR";
                }

                row[unique] = FormatResult(result);
            }
        }
        finally
        {
            table.EndLoadData();
        }
    }

    // =========================================================================
    // PRIVATE
    // =========================================================================

    private Expression BuildExpression(string formula)
    {
        // [columna] -> parámetro seguro p_xxx
        var translated = Regex.Replace(formula, @"\[([^\]]+)\]",
            m => SanitizeParam(m.Groups[1].Value));

        var expr = new Expression(translated, ExpressionOptions.IgnoreCaseAtBuiltInFunctions);
        BindFunctions(expr);
        return expr;
    }

    private static List<(string param, string source)> BuildParamMap(DataTable table)
    {
        var list = new List<(string, string)>();
        foreach (DataColumn c in table.Columns)
        {
            list.Add((SanitizeParam(c.ColumnName), c.ColumnName));
        }

        return list;
    }

    private static object CellValue(DataRow row, string column)
    {
        var raw = row[column];
        if (raw is DBNull or null)
        {
            return "";
        }

        if (raw is double or int or long or decimal or float or short)
        {
            return Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        }

        if (raw is DateTime dt)
        {
            return dt;
        }

        var s = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : s;
    }

    private static string FormatResult(object? result) => result switch
    {
        null => "",
        bool b => b ? "VERDADERO" : "FALSO",
        double d => d.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DateTime dt => DataFileParser.FormatFecha(dt),
        _ => Convert.ToString(result, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>
    /// Funciones estilo Excel. NCalc 6.1: expr.Functions["NAME"] = args => ...
    /// FunctionData expone args.Count y args.Evaluate(i).
    /// </summary>
    private void BindFunctions(Expression expr)
    {
        expr.Functions["SI"] = a =>
            Convert.ToBoolean(a.Evaluate(0)) ? a.Evaluate(1) : a.Evaluate(2);

        expr.Functions["SIERROR"] = a =>
        {
            try { return a.Evaluate(0); }
            catch { return a.Evaluate(1); }
        };

        expr.Functions["Y"] = a =>
        {
            for (var i = 0; i < a.Count; i++)
            {
                if (!Convert.ToBoolean(a.Evaluate(i)))
                {
                    return false;
                }
            }
            return true;
        };
        expr.Functions["O"] = a =>
        {
            for (var i = 0; i < a.Count; i++)
            {
                if (Convert.ToBoolean(a.Evaluate(i)))
                {
                    return true;
                }
            }
            return false;
        };
        expr.Functions["NO"] = a => !Convert.ToBoolean(a.Evaluate(0));

        expr.Functions["CONCATENAR"] = a =>
        {
            var sb = new StringBuilder();
            for (var i = 0; i < a.Count; i++)
            {
                sb.Append(Str(a.Evaluate(i)));
            }
            return sb.ToString();
        };
        expr.Functions["CONCAT"] = expr.Functions["CONCATENAR"];

        expr.Functions["MAYUSC"] = a => Str(a.Evaluate(0)).ToUpperInvariant();
        expr.Functions["MINUSC"] = a => Str(a.Evaluate(0)).ToLowerInvariant();
        expr.Functions["ESPACIOS"] = a => Str(a.Evaluate(0)).Trim();
        expr.Functions["LARGO"] = a => (double)Str(a.Evaluate(0)).Length;

        expr.Functions["IZQUIERDA"] = a =>
        {
            var s = Str(a.Evaluate(0));
            var n = Convert.ToInt32(a.Evaluate(1));
            return s.Length <= n ? s : s[..Math.Max(0, n)];
        };
        expr.Functions["DERECHA"] = a =>
        {
            var s = Str(a.Evaluate(0));
            var n = Convert.ToInt32(a.Evaluate(1));
            return s.Length <= n ? s : s[^Math.Max(0, n)..];
        };

        expr.Functions["REDONDEAR"] = a =>
        {
            var num = Convert.ToDouble(a.Evaluate(0), CultureInfo.InvariantCulture);
            var d = a.Count > 1 ? Convert.ToInt32(a.Evaluate(1)) : 0;
            return Math.Round(num, Math.Max(0, d), MidpointRounding.AwayFromZero);
        };
        expr.Functions["ABS"] = a => Math.Abs(Convert.ToDouble(a.Evaluate(0), CultureInfo.InvariantCulture));
        expr.Functions["ENTERO"] = a => Math.Floor(Convert.ToDouble(a.Evaluate(0), CultureInfo.InvariantCulture));

        // BUSCARV(valor, "NombreHoja", "ColumnaClave", "ColumnaResultado")
        expr.Functions["BUSCARV"] = a =>
            LookupVertical(Str(a.Evaluate(0)), Str(a.Evaluate(1)), Str(a.Evaluate(2)), Str(a.Evaluate(3)));
    }

    private static string Str(object? o) => Convert.ToString(o, CultureInfo.InvariantCulture) ?? "";

    private object LookupVertical(string lookup, string sheetName, string keyCol, string resultCol)
    {
        var table = _lookupTableResolver(sheetName);
        if (table is null || !table.Columns.Contains(keyCol) || !table.Columns.Contains(resultCol))
        {
            return "#N/D";
        }

        foreach (DataRow row in table.Rows)
        {
            var key = Str(row[keyCol]);
            if (string.Equals(key.Trim(), lookup.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var val = row[resultCol];
                return val is DBNull or null ? "" : Str(val);
            }
        }

        return "#N/D";
    }

    private static string SanitizeParam(string name)
    {
        var sb = new StringBuilder("p_");
        foreach (var c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return sb.ToString();
    }

    private static string UniqueColumnName(DataTable table, string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Calculada" : name.Trim();
        var candidate = baseName;
        var i = 2;
        while (table.Columns.Contains(candidate))
        {
            candidate = $"{baseName} ({i++})";
        }

        return candidate;
    }
}
