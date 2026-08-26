using System.Buffers;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MiniExcelLibs;

namespace SaraBI.Services;

public static class DataFileParser
{
    public static DataTable Parse(
        Stream stream,
        IProgress<(int rows, string message)>? progress = null,
        IReadOnlyList<string>? knownColumns = null,
        int estimatedRows = 0)
    {
        var header = new byte[4];
        var headerLen = 0;
        while (headerLen < 4)
        {
            var n = stream.Read(header, headerLen, 4 - headerLen);
            if (n <= 0)
            {
                break;
            }

            headerLen += n;
        }

        if (headerLen == 0)
        {
            return NewTable(knownColumns, estimatedRows);
        }

        var isGzip = headerLen >= 2 && header[0] == 0x1F && header[1] == 0x8B;
        var isZip = headerLen >= 2 && header[0] == 0x50 && header[1] == 0x4B;
        using var prefixed = new PrefixStream(header, headerLen, stream);

        if (isGzip)
        {
            using var gzip = new GZipStream(prefixed, CompressionMode.Decompress, leaveOpen: true);
            return ParsePlain(gzip, progress, knownColumns, estimatedRows);
        }

        if (isZip)
        {
            using var buffer = new MemoryStream();
            prefixed.CopyTo(buffer);
            buffer.Position = 0;
            return ParseXlsx(buffer, progress, knownColumns, estimatedRows);
        }

        return ParsePlain(prefixed, progress, knownColumns, estimatedRows);
    }

    private static DataTable ParsePlain(
        Stream stream,
        IProgress<(int rows, string message)>? progress,
        IReadOnlyList<string>? knownColumns,
        int estimatedRows)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 128 * 1024,
            leaveOpen: true);
        var first = ReadFirstNonEmptyLine(reader);
        if (first is null)
        {
            return NewTable(knownColumns, estimatedRows);
        }

        var trimmed = first.TrimStart('\uFEFF').Trim();
        if (trimmed.StartsWith('{'))
        {
            return ParseNdjson(trimmed, reader, progress, knownColumns, estimatedRows);
        }

        return ParseCsv(trimmed, reader, progress, estimatedRows);
    }

    private static DataTable ParseNdjson(
        string firstLine,
        StreamReader reader,
        IProgress<(int rows, string message)>? progress,
        IReadOnlyList<string>? knownColumns,
        int estimatedRows)
    {
        var table = NewTable(knownColumns, estimatedRows);
        var intern = new StringInterner();
        table.BeginLoadData();
        try
        {
            var rowCount = 0;
            AddNdjsonRow(table, firstLine, intern);
            rowCount++;

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                AddNdjsonRow(table, line, intern);
                rowCount++;
                if (rowCount % 8000 == 0)
                {
                    progress?.Report((rowCount, $"Leyendo {rowCount:N0} filas..."));
                }
            }

            progress?.Report((rowCount, $"{rowCount:N0} filas leídas"));
        }
        finally
        {
            table.EndLoadData();
            intern.Clear();
        }

        return table;
    }

    private static void AddNdjsonRow(DataTable table, string line, StringInterner intern)
    {
        var utf8 = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(line.Length));
        try
        {
            var len = Encoding.UTF8.GetBytes(line, utf8);
            using var doc = JsonDocument.Parse(utf8.AsMemory(0, len));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            EnsureColumns(table, doc.RootElement);
            var row = table.NewRow();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var col = SanitizeColumn(prop.Name);
                if (!table.Columns.Contains(col))
                {
                    continue;
                }

                row[col] = intern.Get(JsonToString(prop.Value));
            }

            table.Rows.Add(row);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(utf8);
        }
    }

    private static void EnsureColumns(DataTable table, JsonElement obj)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            var name = SanitizeColumn(prop.Name);
            if (!table.Columns.Contains(name))
            {
                table.Columns.Add(UniqueColumn(table, name), typeof(string));
            }
        }
    }

    private static DataTable ParseCsv(
        string headerLine,
        StreamReader reader,
        IProgress<(int rows, string message)>? progress,
        int estimatedRows)
    {
        if (headerLine.StartsWith("sep=", StringComparison.OrdinalIgnoreCase))
        {
            headerLine = reader.ReadLine() ?? "";
        }

        var delim = headerLine.Count(c => c == ';') > headerLine.Count(c => c == ',') ? ';' : ',';
        var headers = SplitCsvLine(headerLine, delim);
        var table = new DataTable("Datos");
        if (estimatedRows > 0)
        {
            table.MinimumCapacity = estimatedRows;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in headers)
        {
            var name = UniqueColumn(table, SanitizeColumn(raw), used);
            table.Columns.Add(name, typeof(string));
            used.Add(name);
        }

        var intern = new StringInterner();
        table.BeginLoadData();
        try
        {
            var rowCount = 0;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var values = SplitCsvLine(line, delim);
                var row = table.NewRow();
                for (var i = 0; i < table.Columns.Count && i < values.Count; i++)
                {
                    row[i] = intern.Get(values[i]);
                }

                table.Rows.Add(row);
                rowCount++;
                if (rowCount % 8000 == 0)
                {
                    progress?.Report((rowCount, $"Leyendo {rowCount:N0} filas..."));
                }
            }

            progress?.Report((rowCount, $"{rowCount:N0} filas leídas"));
        }
        finally
        {
            table.EndLoadData();
            intern.Clear();
        }

        return table;
    }

    private static DataTable ParseXlsx(
        Stream stream,
        IProgress<(int rows, string message)>? progress,
        IReadOnlyList<string>? knownColumns,
        int estimatedRows)
    {
        var rawRows = MiniExcel.Query(stream, useHeaderRow: false)
            .OfType<IDictionary<string, object>>()
            .Select(d => d.Values.Select(v => v?.ToString()?.Trim() ?? "").ToList())
            .Where(r => r.Any(v => v.Length > 0))
            .ToList();

        if (rawRows.Count == 0)
        {
            return NewTable(knownColumns, estimatedRows);
        }

        var known = new HashSet<string>(
            (knownColumns ?? Array.Empty<string>()).Select(c => c.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        var headerIdx = 0;
        var scan = Math.Min(rawRows.Count, 10);
        for (var i = 0; i < scan; i++)
        {
            var nonEmpty = rawRows[i].Where(v => v.Length > 0).ToList();
            if (nonEmpty.Count == 0)
            {
                continue;
            }

            if (known.Count > 0)
            {
                var matches = nonEmpty.Count(v => known.Contains(v.ToLowerInvariant()));
                if (matches >= Math.Min(3, known.Count))
                {
                    headerIdx = i;
                    break;
                }
            }
            else if (nonEmpty.Count >= 3
                     && nonEmpty.All(v => !v.Contains('—') && !v.Contains("Exportado", StringComparison.OrdinalIgnoreCase)))
            {
                headerIdx = i;
                break;
            }
        }

        var headers = rawRows[headerIdx];
        var table = new DataTable("Datos");
        if (estimatedRows > 0)
        {
            table.MinimumCapacity = estimatedRows;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in headers)
        {
            var name = UniqueColumn(table, SanitizeColumn(raw), used);
            table.Columns.Add(name, typeof(string));
            used.Add(name);
        }

        var intern = new StringInterner();
        table.BeginLoadData();
        try
        {
            var rowCount = 0;
            for (var i = headerIdx + 1; i < rawRows.Count; i++)
            {
                var values = rawRows[i];
                if (values.All(v => v.Length == 0))
                {
                    continue;
                }

                var row = table.NewRow();
                for (var c = 0; c < table.Columns.Count && c < values.Count; c++)
                {
                    row[c] = intern.Get(values[c]);
                }

                table.Rows.Add(row);
                rowCount++;
                if (rowCount % 8000 == 0)
                {
                    progress?.Report((rowCount, $"Leyendo {rowCount:N0} filas..."));
                }
            }

            progress?.Report((rowCount, $"{rowCount:N0} filas leídas"));
        }
        finally
        {
            table.EndLoadData();
            intern.Clear();
        }

        return table;
    }

    private static DataTable NewTable(IReadOnlyList<string>? knownColumns, int estimatedRows)
    {
        var table = new DataTable("Datos");
        if (estimatedRows > 0)
        {
            table.MinimumCapacity = estimatedRows;
        }

        if (knownColumns is null)
        {
            return table;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in knownColumns)
        {
            var name = UniqueColumn(table, SanitizeColumn(raw), used);
            table.Columns.Add(name, typeof(string));
            used.Add(name);
        }

        return table;
    }

    private static string? ReadFirstNonEmptyLine(StreamReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return null;
    }

    private static string JsonToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        JsonValueKind.Number => value.TryGetInt64(out var l)
            ? l.ToString(CultureInfo.InvariantCulture)
            : value.GetRawText(),
        _ => value.GetRawText(),
    };

    private static string SanitizeColumn(string name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "Columna";
        }

        var chars = trimmed.Select(c => c is '.' or '[' or ']' or '/' or '\\' ? '_' : c).ToArray();
        return new string(chars);
    }

    private static string UniqueColumn(DataTable table, string name, HashSet<string>? used = null)
    {
        var candidate = name;
        var i = 2;
        while (table.Columns.Contains(candidate) || (used is not null && used.Contains(candidate)))
        {
            candidate = $"{name}_{i++}";
        }

        return candidate;
    }

    private static List<string> SplitCsvLine(string line, char delim)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cur.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == delim && !inQuotes)
            {
                result.Add(cur.ToString());
                cur.Clear();
            }
            else
            {
                cur.Append(ch);
            }
        }

        result.Add(cur.ToString());
        return result;
    }

    private sealed class StringInterner
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

        public string Get(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            if (_map.TryGetValue(value, out var existing))
            {
                return existing;
            }

            _map[value] = value;
            return value;
        }

        public void Clear() => _map.Clear();
    }

    private sealed class PrefixStream : Stream
    {
        private readonly byte[] _prefix;
        private int _prefixPos;
        private readonly Stream _rest;

        public PrefixStream(byte[] prefix, int length, Stream rest)
        {
            _prefix = new byte[length];
            Buffer.BlockCopy(prefix, 0, _prefix, 0, length);
            _rest = rest;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var total = 0;
            if (_prefixPos < _prefix.Length && count > 0)
            {
                var take = Math.Min(count, _prefix.Length - _prefixPos);
                Buffer.BlockCopy(_prefix, _prefixPos, buffer, offset, take);
                _prefixPos += take;
                total += take;
                offset += take;
                count -= take;
            }

            if (count > 0)
            {
                total += _rest.Read(buffer, offset, count);
            }

            return total;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var total = 0;
            if (_prefixPos < _prefix.Length && buffer.Length > 0)
            {
                var take = Math.Min(buffer.Length, _prefix.Length - _prefixPos);
                _prefix.AsSpan(_prefixPos, take).CopyTo(buffer.Span);
                _prefixPos += take;
                total += take;
                buffer = buffer[take..];
            }

            if (buffer.Length > 0)
            {
                total += await _rest.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            return total;
        }

        public override void Flush() => _rest.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
