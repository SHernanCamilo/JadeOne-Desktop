using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using MiniExcelLibs;

namespace SaraBI.Services;

public static class ExcelExporter
{
    public static void Save(DataTable table, string path)
    {
        MiniExcel.SaveAs(path, table, overwriteFile: true);
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
