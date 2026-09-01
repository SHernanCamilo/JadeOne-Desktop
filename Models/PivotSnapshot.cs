using System.Data;

namespace SaraBI.Models;

public sealed class PivotSnapshot
{
    public List<PivotLeaf> Leaves { get; init; } = new();
    public List<string> Cross { get; init; } = new();
    public Dictionary<string, string> CrossSort { get; init; } = new(StringComparer.Ordinal);
    public PivotValueField Value { get; init; } = new();
    public IReadOnlyList<PivotAxisField> RowFields { get; init; } = Array.Empty<PivotAxisField>();
    public bool Outline { get; init; }
    public int LeafCount => Leaves.Count;
}

public sealed class PivotLeaf
{
    public string[] Labels { get; init; } = Array.Empty<string>();
    public string[] SortKeys { get; init; } = Array.Empty<string>();
    public Dictionary<string, PivotAgg> Cols { get; init; } = new(StringComparer.Ordinal);
}

public sealed class PivotAgg
{
    public double Sum;
    public int Count;
    public int NumCount;
    public double Min = double.MaxValue;
    public double Max = double.MinValue;
    public HashSet<string>? Distinct;
}

public sealed class PivotBuild
{
    public required DataTable Table { get; init; }
    public PivotSnapshot? Snapshot { get; init; }
}
