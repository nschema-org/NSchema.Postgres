using NSchema.Plan.Model;
using NSchema.Plan.Model.Extensions;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{
    // ── Extensions ────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateExtension(CreateExtension action)
    {
        var sql = $"CREATE EXTENSION IF NOT EXISTS {Quote(action.Extension.Name)}";
        return Statement(action.Extension.Version is { } version ? $"{sql} VERSION '{EscapeLiteral(version)}'" : sql);
    }

    // A version change updates in place; with no target version, UPDATE moves to the default (latest) version.
    protected override Result<IReadOnlyList<SqlStatement>> AlterExtension(AlterExtension action) =>
        Statement(action.NewVersion is { } version
            ? $"ALTER EXTENSION {Quote(action.ExtensionName)} UPDATE TO '{EscapeLiteral(version)}'"
            : $"ALTER EXTENSION {Quote(action.ExtensionName)} UPDATE");

    protected override Result<IReadOnlyList<SqlStatement>> DropExtension(DropExtension action) =>
        Statement($"DROP EXTENSION {Quote(action.ExtensionName)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetExtensionComment(SetExtensionComment action) =>
        Comment($"EXTENSION {Quote(action.ExtensionName)}", action.NewComment);
}
