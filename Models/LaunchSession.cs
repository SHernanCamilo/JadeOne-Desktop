namespace SaraBI.Models;

public sealed class LaunchSession
{
    public required string Token { get; init; }
    public required string Schema { get; init; }
    public required string View { get; init; }
    public required string ViewLabel { get; init; }
    public required string ApiUrl { get; init; }
    public string? User { get; init; }
}

public sealed class FabricColumn
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Nullable { get; set; } = true;

    public bool IsDate =>
        Type.Contains("date", StringComparison.OrdinalIgnoreCase)
        || Type.Contains("time", StringComparison.OrdinalIgnoreCase);
}

public sealed class DateRangeFilter
{
    public required string Column { get; init; }
    public required DateTime From { get; init; }
    public DateTime? To { get; init; }
}
