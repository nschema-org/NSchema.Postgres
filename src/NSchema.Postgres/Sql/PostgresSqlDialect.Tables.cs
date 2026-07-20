using NSchema.Plan.Model;
using NSchema.Plan.Model.Tables;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{
    // ── Tables ────────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateTable(CreateTable action)
    {
        var parts = action.Table.Columns.Select(BuildColumnDef).ToList();

        // Only the primary key is created inline; unique/check constraints, foreign keys and indexes arrive as
        // separate ALTER TABLE actions from the linearizer.
        if (action.Table.PrimaryKey is { } pk)
        {
            parts.Add($"CONSTRAINT {Quote(pk.Name)} PRIMARY KEY ({ColumnList(pk.ColumnNames)})");
        }

        return Statement($"""
                          CREATE TABLE {Qualify(action.SchemaName, action.Table.Name)} (
                              {string.Join(",\n    ", parts)}
                          )
                          """);
    }

    protected override Result<IReadOnlyList<SqlStatement>> SetTableComment(SetTableComment action) =>
        Comment($"TABLE {Qualify(action.Table)}", action.NewComment);
}
