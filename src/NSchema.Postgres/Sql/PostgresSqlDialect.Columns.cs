using NSchema.Model.Columns;
using NSchema.Plan.Domain;
using NSchema.Plan.Domain.Columns;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{
    // ── Columns ───────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> AddColumn(AddColumn action) =>
        Statement($"ALTER TABLE {Qualify(action.Table)} ADD COLUMN {BuildColumnDef(action.Column)}");

    protected override Result<IReadOnlyList<SqlStatement>> AlterColumn(AlterColumn action) =>
        (action.Type, action.Nullability) switch
        {
            ({ } type, { } nullability) => Statements(
                new($"ALTER TABLE {Qualify(action.Table)} ALTER COLUMN {Quote(action.Column.Name)} TYPE {ToPostgresType(type.New!)}"),
                new($"ALTER TABLE {Qualify(action.Table)} ALTER COLUMN {Quote(action.Column.Name)} {(nullability.New! ? "DROP" : "SET")} NOT NULL")),
            ({ } type, null) => Statement($"ALTER TABLE {Qualify(action.Table)} ALTER COLUMN {Quote(action.Column.Name)} TYPE {ToPostgresType(type.New!)}"),
            (null, { } nullability) => Statement($"ALTER TABLE {Qualify(action.Table)} ALTER COLUMN {Quote(action.Column.Name)} {(nullability.New! ? "DROP" : "SET")} NOT NULL"),
            _ => Statements(),
        };

    // One clause per option that differs, as AlterSequence does: an option going back to null is the engine's own
    // default asked for explicitly, so the next introspection folds it away again and no residual drift is left.
    protected override Result<IReadOnlyList<SqlStatement>> AlterIdentitySequence(AlterIdentitySequence action)
    {
        var (old, @new) = (action.OldOptions, action.NewOptions);
        var parts = new List<string>();
        if (old?.MinValue != @new?.MinValue)
        {
            parts.Add(@new?.MinValue is { } min ? $"SET MINVALUE {min}" : "SET NO MINVALUE");
        }

        // Only a start that actually moved restarts the counter. `SET START` records where a restart begins and
        // does not move the current value, so the RESTART is what makes the new start take effect — and it is
        // also what reissues values the table already holds, which is why nothing else may drag it along.
        var startChanged = old?.StartWith != @new?.StartWith;
        if (startChanged)
        {
            // There is no NO START form; a reset asks for the start a freshly declared identity would have — its
            // effective minimum ascending, its maximum descending — which introspection then folds back to null.
            parts.Add($"SET START {@new?.StartWith ?? DefaultIdentityStart(@new)}");
        }

        if (old?.IncrementBy != @new?.IncrementBy)
        {
            parts.Add($"SET INCREMENT BY {@new?.IncrementBy ?? 1}");
        }

        if (startChanged)
        {
            parts.Add("RESTART");
        }

        if (parts.Count == 0)
        {
            return Statements();
        }
        return Statement($"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} {string.Join(" ", parts)}");
    }

    private static long DefaultIdentityStart(IdentityOptions? options) =>
        (options?.IncrementBy ?? 1) > 0 ? options?.MinValue ?? 1 : -1;

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
