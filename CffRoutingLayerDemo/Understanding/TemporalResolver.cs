// Understanding/TemporalResolver.cs
namespace CffRoutingLayerDemo.Understanding;

using System.Text.RegularExpressions;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Resolves relative temporal expressions into concrete TemporalScope values.
/// Deterministic — no LLM involved.
/// </summary>
public sealed class TemporalResolver
{
    private static readonly (Regex Pattern, Func<DateOnly, TemporalScope> Resolver)[] Rules =
    [
        (new Regex(@"\blast\s+month\b", RegexOptions.IgnoreCase),
            today => new TemporalScope
            {
                Type = TemporalScopeType.RelativeWindow,
                Start = today.AddMonths(-1).AddDays(1 - today.AddMonths(-1).Day),
                End   = today.AddDays(-today.Day),
                RelativeExpression = "last month"
            }),

        (new Regex(@"\bthis\s+month\b", RegexOptions.IgnoreCase),
            today => new TemporalScope
            {
                Type = TemporalScopeType.RelativeWindow,
                Start = today.AddDays(1 - today.Day),
                End   = today,
                RelativeExpression = "this month"
            }),

        (new Regex(@"\blast\s+quarter\b|Q(?<q>\d)\s+\d{4}|\bq(?<q2>\d)\b", RegexOptions.IgnoreCase),
            today =>
            {
                var q = (today.Month - 1) / 3;  // 0-based previous quarter
                var qStart = new DateOnly(today.Year, q * 3 + 1, 1);
                return new TemporalScope
                {
                    Type = TemporalScopeType.BoundedPeriod,
                    Start = qStart.AddMonths(-3),
                    End   = qStart.AddDays(-1),
                    RelativeExpression = "last quarter"
                };
            }),

        (new Regex(@"\bthis\s+year\b|\bytd\b|\byear.to.date\b", RegexOptions.IgnoreCase),
            today => new TemporalScope
            {
                Type = TemporalScopeType.RelativeWindow,
                Start = new DateOnly(today.Year, 1, 1),
                End   = today,
                RelativeExpression = "year to date"
            }),

        (new Regex(@"\blast\s+(?<n>\d+)\s+days?\b", RegexOptions.IgnoreCase),
            today =>
            {
                // n captured separately; use a non-capturing variant here
                return new TemporalScope
                {
                    Type = TemporalScopeType.RelativeWindow,
                    Start = today.AddDays(-30),
                    End   = today,
                    RelativeExpression = "last N days"
                };
            }),
    ];

    private static readonly Regex LastNDays =
        new(@"\blast\s+(?<n>\d+)\s+days?\b", RegexOptions.IgnoreCase);

    /// <summary>Extract a TemporalScope from free text.</summary>
    public TemporalScope? Extract(string text)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Check last-N-days with captured group first
        var nMatch = LastNDays.Match(text);
        if (nMatch.Success && int.TryParse(nMatch.Groups["n"].Value, out var n))
        {
            return new TemporalScope
            {
                Type = TemporalScopeType.RelativeWindow,
                Start = today.AddDays(-n),
                End   = today,
                RelativeExpression = $"last {n} days"
            };
        }

        foreach (var (pattern, resolver) in Rules)
        {
            if (pattern.IsMatch(text))
                return resolver(today);
        }

        return null;
    }

    /// <summary>Re-resolve a scope that may have a stale relative expression.</summary>
    public TemporalScope Resolve(string? relativeExpression, DateOnly referenceDate)
    {
        if (relativeExpression is null)
            return new TemporalScope { Type = TemporalScopeType.None };

        return Extract(relativeExpression) ?? new TemporalScope
        {
            Type = TemporalScopeType.OpenEnded,
            RelativeExpression = relativeExpression
        };
    }
}
