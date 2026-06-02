// Queries/Constraint.cs
namespace CffRoutingLayerDemo.Queries;

public enum ConstraintOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    LessThan,
    Contains,
    In,
    Between
}

public sealed record Constraint
{
    public required string Field { get; init; }
    public required ConstraintOperator Operator { get; init; }
    public required string Value { get; init; }
    public required bool IsNegation { get; init; }
}
