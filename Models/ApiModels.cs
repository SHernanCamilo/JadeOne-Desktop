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

public sealed class ViewsResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public ViewsPayload? Data { get; set; }

    [JsonPropertyName("esquemas")]
    public List<string>? Esquemas { get; set; }

    [JsonPropertyName("esquemas_catalogo")]
    public List<EsquemaCatalogoItem>? EsquemasCatalogo { get; set; }
}

public sealed class ViewsPayload
{
    [JsonPropertyName("schemas_allowed")]
    public List<string>? SchemasAllowed { get; set; }

    public List<SchemaBlock>? Schemas { get; set; }
}

public sealed class SchemaBlock
{
    public string Schema { get; set; } = "";
    public string Display { get; set; } = "";
    public List<ViewBlock>? Views { get; set; }
}

public sealed class ViewBlock
{
    [JsonPropertyName("view_name")]
    public string ViewName { get; set; } = "";

    [JsonPropertyName("qualified_name")]
    public string? QualifiedName { get; set; }

    [JsonPropertyName("column_count")]
    public int ColumnCount { get; set; }

    [JsonPropertyName("visible_for_site")]
    public bool VisibleForSite { get; set; } = true;

    [JsonPropertyName("bi_estado")]
    public string? BiEstado { get; set; }
}

public sealed class EsquemaCatalogoItem
{
    public string Schema { get; set; } = "";
    public string Nombre { get; set; } = "";
}

public sealed class VistaCatalogItem
{
    public string Schema { get; set; } = "";
    public string SchemaDisplay { get; set; } = "";
    public string ViewName { get; set; } = "";
    public int ColumnCount { get; set; }
    public bool Enabled { get; set; } = true;
    public string Key => $"{Schema}.{ViewName}".ToLowerInvariant();
}
