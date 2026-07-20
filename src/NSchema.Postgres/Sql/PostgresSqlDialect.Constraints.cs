using NSchema.Model.Constraints;
using NSchema.Plan.Model;
using NSchema.Plan.Model.Constraints;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{
    // ── Constraints ───────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> AddExclusionConstraint(AddExclusionConstraint action)
    {
        var exclusion = action.ExclusionConstraint;
        var method = exclusion.Method is { } m ? $" USING {m.Value}" : "";
        var elements = string.Join(", ", exclusion.Elements.Select(ExclusionElementText));
        var where = exclusion.Predicate is { } p ? $" WHERE ({p.Value})" : "";
        return Statement($"ALTER TABLE {Qualify(action.Table)} ADD CONSTRAINT {Quote(exclusion.Name)} EXCLUDE{method} ({elements}){where}");
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropExclusionConstraint(DropExclusionConstraint action) =>
        Statement($"ALTER TABLE {Qualify(action.Constraint.Owner)} DROP CONSTRAINT {Quote(action.Constraint.Member)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetConstraintComment(SetConstraintComment action) =>
        Comment($"CONSTRAINT {Quote(action.Constraint.Member)} ON {Qualify(action.Constraint.Owner)}", action.NewComment);

    // A plain column element is quoted; an expression element is parenthesised and verbatim. The operator follows
    // WITH (e.g. =, &&) and needs no quoting.
    private string ExclusionElementText(ExclusionElement element)
    {
        var target = element.Column is { } column ? Quote(column) : $"({element.Expression!.Value})";
        return $"{target} WITH {element.Operator}";
    }
}
