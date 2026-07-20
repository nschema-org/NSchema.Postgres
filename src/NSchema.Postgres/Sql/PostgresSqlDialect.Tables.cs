using NSchema.Plan.Model;
using NSchema.Plan.Model.Tables;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{
    // ── Tables ────────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateTable(CreateTable action)
    {
        // Every constraint is created inline: the shared clauses (primary key, unique, check, foreign keys) plus
        // Postgres's own exclusion constraints. Only indexes arrive as separate actions.
        var parts = action.Table.Columns.Select(BuildColumnDef)
            .Concat(InlineConstraintClauses(action.Table))
            .Concat(action.Table.ExclusionConstraints.Select(ExclusionConstraintClause))
            .ToList();

        return Statement($"""
                          CREATE TABLE {Qualify(action.SchemaName, action.Table.Name)} (
                              {string.Join(",\n    ", parts)}
                          )
                          """);
    }

    protected override Result<IReadOnlyList<SqlStatement>> SetTableComment(SetTableComment action) =>
        Comment($"TABLE {Qualify(action.Table)}", action.NewComment);
}
