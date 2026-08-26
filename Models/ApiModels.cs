using System.Text.Json.Serialization;

namespace SaraBI.Models;

public sealed class ClaimResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Token { get; set; }
    public string? Schema { get; set; }
    public string? View { get; set; }

    [JsonPropertyName("view_label")]
    public string? ViewLabel { get; set; }

    [JsonPropertyName("api_url")]
    public string? ApiUrl { get; set; }

    public string? User { get; set; }
}

public sealed class ExportStartResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }

    [JsonPropertyName("job_id")]
    public string? JobId { get; set; }

    [JsonPropertyName("r2_status")]
    public string? R2Status { get; set; }

    [JsonPropertyName("estimated_s")]
    public int? EstimatedS { get; set; }

    [JsonPropertyName("row_count")]
    public long? RowCount { get; set; }

    public long? Rows { get; set; }
}

public sealed class ExportStatusResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public ExportStatusData? Data { get; set; }
}

public sealed class ExportStatusData
{
    public string? Status { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public long? Rows { get; set; }
    public string? Format { get; set; }
    public string? Filename { get; set; }
}

public sealed class ColumnsResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public ColumnsPayload? Data { get; set; }
}

public sealed class ColumnsPayload
{
    [JsonPropertyName("view_name")]
    public string? ViewName { get; set; }

    [JsonPropertyName("column_count")]
    public int ColumnCount { get; set; }

    public List<FabricColumn> Columns { get; set; } = new();
}

public sealed class R2StatusResponse
{
    public bool Success { get; set; }

    [JsonPropertyName("r2_status")]
    public string? R2Status { get; set; }

    public string? Message { get; set; }

    [JsonPropertyName("estimated_s")]
    public int? EstimatedS { get; set; }

    [JsonPropertyName("row_count")]
    public long? RowCount { get; set; }
}
