// Understanding/ConstraintParser.cs
namespace CffRoutingLayerDemo.Understanding;

using System.Text.RegularExpressions;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Extracts filter constraints from query text deterministically.
/// </summary>
public sealed class ConstraintParser
{
    private static readonly (Regex Pattern, Func<Match, Constraint> Factory)[] Rules =
    [
        (new Regex(@"\bgreater\s+than\s+\$?(?<v>[\d,]+)", RegexOptions.IgnoreCase),
            m => new Constraint { Field = "amount", Operator = ConstraintOperator.GreaterThan, Value = m.Groups["v"].Value.Replace(",", ""), IsNegation = false }),

        (new Regex(@"\bless\s+than\s+\$?(?<v>[\d,]+)", RegexOptions.IgnoreCase),
            m => new Constraint { Field = "amount", Operator = ConstraintOperator.LessThan, Value = m.Groups["v"].Value.Replace(",", ""), IsNegation = false }),

        (new Regex(@"\bexcluding\s+(?<v>\w+)", RegexOptions.IgnoreCase),
            m => new Constraint { Field = "category", Operator = ConstraintOperator.NotEquals, Value = m.Groups["v"].Value, IsNegation = true }),

        (new Regex(@"\bonly\s+(?<v>\w+)", RegexOptions.IgnoreCase),
            m => new Constraint { Field = "category", Operator = ConstraintOperator.Equals, Value = m.Groups["v"].Value, IsNegation = false }),
    ];

    public IReadOnlyList<Constraint> Extract(string text)
    {
        var constraints = new List<Constraint>();
        foreach (var (pattern, factory) in Rules)
        {
            var match = pattern.Match(text);
            if (match.Success)
                constraints.Add(factory(match));
        }
        return constraints;
    }
}
