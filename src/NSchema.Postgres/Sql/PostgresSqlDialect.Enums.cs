using NSchema.Plan.Model;
using NSchema.Plan.Model.Enums;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{




    // ── Enums ─────────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateEnum(CreateEnum action)
    {
        var values = string.Join(", ", action.Enum.Values.Select(v => $"'{EscapeLiteral(v.Value)}'"));
        return Statement($"CREATE TYPE {Qualify(action.SchemaName, action.Enum.Name)} AS ENUM ({values})");
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropEnum(DropEnum action) =>
        Statement($"DROP TYPE {Qualify(action.Enum)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameEnum(RenameEnum action) =>
        Statement($"ALTER TYPE {Qualify(action.Enum)} RENAME TO {Quote(action.NewName)}");

    // A value added by ALTER TYPE … ADD VALUE cannot be used until the transaction that added it commits, so
    // the statement is carved out of the surrounding transaction. The executor commits the pending segment,
    // runs it alone, and resumes — ordering relative to later statements that use the value is preserved.
    protected override Result<IReadOnlyList<SqlStatement>> AddEnumValue(AddEnumValue action)
    {
        var sql = $"ALTER TYPE {Qualify(action.Enum)} ADD VALUE '{EscapeLiteral(action.Value.Value)}'";
        sql = action switch
        {
            { Before: { } before } => $"{sql} BEFORE '{EscapeLiteral(before.Value)}'",
            { After: { } after } => $"{sql} AFTER '{EscapeLiteral(after.Value)}'",
            _ => sql,
        };
        return Statement(sql, runOutsideTransaction: true);
    }

    protected override Result<IReadOnlyList<SqlStatement>> SetEnumComment(SetEnumComment action) =>
        Comment($"TYPE {Qualify(action.Enum)}", action.NewComment);
}
