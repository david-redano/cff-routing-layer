// Queries/TemporalScope.cs
namespace CffRoutingLayerDemo.Queries;

public enum TemporalScopeType
{
    None,           // No time dimension
    PointInTime,    // "as of March 15"
    BoundedPeriod,  // "from Jan to Mar"
    RelativeWindow, // "last 30 days"
    OpenEnded       // "all time", "since inception"
}

public enum OutputFormat
{
    Summary,        // Aggregated metrics (totals, averages)
    DetailedList,   // Row-level records
    Comparison,     // Side-by-side or delta
    Forecast,       // Projected values with confidence
    Narrative       // Text report
}

public sealed record TemporalScope
{
    public required TemporalScopeType Type { get; init; }
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
    public string? RelativeExpression { get; init; }    // "last 30 days" (for diagnostics)
}
