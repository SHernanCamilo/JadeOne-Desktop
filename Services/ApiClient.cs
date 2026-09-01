using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SaraBI.Models;

namespace SaraBI.Services;

public sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
    };

    public string ApiUrl { get; private set; } = "";
    public string Token { get; private set; } = "";
    public string Schema { get; private set; } = "";
    public string View { get; private set; } = "";

    public void Dispose() => _http.Dispose();

    public void ApplySession(LaunchSession session)
    {
        ApiUrl = session.ApiUrl.TrimEnd('/');
        Token = session.Token;
        Schema = session.Schema;
        View = session.View;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task<LaunchSession> ClaimAsync(string apiUrl, string ticket, CancellationToken ct)
    {
        if (!OfficialApi.IsTrustedBase(apiUrl))
        {
            throw new InvalidOperationException("La API de destino no está en la lista permitida.");
        }

        var baseUrl = OfficialApi.Normalize(apiUrl);
        var url = baseUrl + "/fabric/viewer/desktop/claim";
        var body = JsonSerializer.Serialize(new { ticket });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<ClaimResponse>(json, JsonOptions)
                     ?? throw new InvalidOperationException("Respuesta de claim vacía.");

        if (!response.IsSuccessStatusCode || !parsed.Success || string.IsNullOrWhiteSpace(parsed.Token))
        {
            throw new InvalidOperationException(parsed.Message ?? $"No se pudo canjear el ticket ({(int)response.StatusCode}).");
        }

        var session = new LaunchSession
        {
            Token = parsed.Token,
            Schema = parsed.Schema ?? "",
            View = parsed.View ?? "",
            ViewLabel = parsed.ViewLabel ?? parsed.View ?? "",
            ApiUrl = OfficialApi.BindSessionUrl(baseUrl, parsed.ApiUrl),
            User = parsed.User,
        };
        ApplySession(session);
        return session;
    }

    public async Task<List<FabricColumn>> GetColumnsAsync(CancellationToken ct)
    {
        var payload = new { schema_name = Schema, view_name = View };
        var parsed = await PostJsonAsync<ColumnsResponse>("/fabric/viewer/columns", payload, ct)
                     .ConfigureAwait(false);
        if (parsed is null || !parsed.Success)
        {
            throw new InvalidOperationException(parsed?.Message ?? "No se pudieron obtener las columnas.");
        }

        return parsed.Data?.Columns ?? new List<FabricColumn>();
    }

    public async Task<ExportStartResponse> StartExportAsync(
        int maxRows,
        DateRangeFilter? dateFilter,
        CancellationToken ct)
    {
        var filters = new Dictionary<string, object>();
        if (dateFilter is not null)
        {
            filters[dateFilter.Column] = new Dictionary<string, object?>
            {
                ["type"] = "dateRange",
                ["from"] = dateFilter.From.ToString("yyyy-MM-dd"),
                ["to"] = dateFilter.To?.ToString("yyyy-MM-dd"),
            };
        }

        var payload = new Dictionary<string, object?>
        {
            ["schema_name"] = Schema,
            ["view"] = View,
            ["format"] = "gzip",
            ["max_rows"] = maxRows,
            ["filters"] = filters,
        };

        return await PostJsonAsync<ExportStartResponse>("/fabric/viewer/export/start", payload, ct)
               .ConfigureAwait(false)
               ?? throw new InvalidOperationException("Respuesta de export vacía.");
    }

    public async Task<R2StatusResponse> GetR2StatusAsync(CancellationToken ct)
    {
        var url = $"{ApiUrl}/fabric/viewer/r2/status?schema={Uri.EscapeDataString(Schema)}&view={Uri.EscapeDataString(View)}";
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<R2StatusResponse>(json, JsonOptions)
               ?? new R2StatusResponse { Success = false, Message = "Sin respuesta R2." };
    }

    public async Task<ExportStatusData?> GetExportStatusAsync(string jobId, CancellationToken ct)
    {
        var url = $"{ApiUrl}/fabric/viewer/export/status/{Uri.EscapeDataString(jobId)}";
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<ExportStatusResponse>(json, JsonOptions);
        if (parsed is null || !parsed.Success)
        {
            return null;
        }

        return parsed.Data;
    }

    public async Task<Stream> DownloadExportAsync(string jobId, CancellationToken ct)
    {
        var url = $"{ApiUrl}/fabric/viewer/export/download/{Uri.EscapeDataString(jobId)}"
                  + "?token=" + Uri.EscapeDataString(Token);
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new HttpContentStream(response, stream);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<T?> PostJsonAsync<T>(string path, object payload, CancellationToken ct)
    {
        var url = ApiUrl + path;
        var body = JsonSerializer.Serialize(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }
}

internal sealed class HttpContentStream : Stream
{
    private readonly HttpResponseMessage _response;
    private readonly Stream _inner;

    public HttpContentStream(HttpResponseMessage response, Stream inner)
    {
        _response = response;
        _inner = inner;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _response.Dispose();
        }

        base.Dispose(disposing);
    }
}
