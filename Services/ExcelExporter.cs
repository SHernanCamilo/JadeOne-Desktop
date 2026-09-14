using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using MiniExcelLibs;

namespace SaraBI.Services;

public static class ExcelExporter
{
    /// <summary>
    /// Escribe el DataTable a .xlsx. MiniExcel ya trabaja en streaming (fila a
    /// fila, RAM constante). Para acelerar la escritura a disco usamos un
    /// FileStream propio con buffer grande (1 MB) en vez de dejar que la
    /// librería abra el archivo con el buffer por defecto: en tablas de cientos
    /// de miles de filas esto reduce notablemente el tiempo de I/O.
    /// </summary>
    public static void Save(DataTable table, string path)
    {
        using var fs = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);

        fs.SaveAs(table, printHeader: true, excelType: ExcelType.XLSX);
    }

    public static void SaveCsv(DataTable table, string path)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(string.Join(",", table.Columns.Cast<DataColumn>().Select(c => Csv(c.ColumnName))));
        foreach (DataRow row in table.Rows)
        {
            writer.WriteLine(string.Join(",", row.ItemArray.Select(v => Csv(Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""))));
        }

        static string Csv(string value)
        {
            if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return value;
        }
    }
}
