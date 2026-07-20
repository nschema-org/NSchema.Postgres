using NSchema.Plan.Model;
using NSchema.Plan.Model.Columns;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{
    // ── Columns ───────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> AddColumn(AddColumn action) =>
        Statement($"ALTER TABLE {Qualify(action.Table)} ADD COLUMN {BuildColumnDef(action.Column)}");

    protected override Result<IReadOnlyList<SqlStatement>> AlterColumnType(AlterColumnType action) =>
        Statement($"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} TYPE {ToPostgresType(action.NewType)}");

    protected override Result<IReadOnlyList<SqlStatement>> AlterColumnNullability(AlterColumnNullability action) =>
        Statement(action.NewNullable
            ? $"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} DROP NOT NULL"
            : $"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} SET NOT NULL");

    protected override Result<IReadOnlyList<SqlStatement>> AlterIdentitySequence(AlterIdentitySequence action)
    {
        var opts = action.NewOptions;
        var parts = new List<string>();
        if (opts?.MinValue is { } min)
        {
            parts.Add($"SET MINVALUE {min}");
        }

        if (opts?.StartWith is { } start)
        {
            parts.Add($"SET START {start}");
        }

        if (opts?.IncrementBy is { } increment)
        {
            parts.Add($"SET INCREMENT BY {increment}");
        }

        parts.Add("RESTART");
        return Statement($"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} {string.Join(" ", parts)}");
    }

    // Changing a column's generation expression in place: PG 17+ replaces it with SET EXPRESSION, and a generated
    // column is converted back to a plain one with DROP EXPRESSION (data is kept). PostgreSQL has no in-place way
    // to make an existing plain column generated, so that transition is unsupported — the column must be re-added.
    protected override Result<IReadOnlyList<SqlStatement>> SetColumnGenerated(SetColumnGenerated action) => action switch
    {
        { NewExpression: null } =>
            Statement($"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} DROP EXPRESSION"),
        { OldExpression: not null, NewExpression: { } expression } =>
            Statement($"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} SET EXPRESSION AS ({expression.Value})"),
        _ => Unsupported(action),
    };

    protected override Result<IReadOnlyList<SqlStatement>> SetColumnComment(SetColumnComment action) =>
        Comment($"COLUMN {Qualify(action.Column.Owner)}.{Quote(action.Column.Member)}", action.NewComment);

}
