using NSchema.Plan.Domain;
using NSchema.Plan.Domain.Schemas;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{
    // ── Schemas ───────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateSchema(CreateSchema action) =>
        Statement($"CREATE SCHEMA IF NOT EXISTS {Quote(action.SchemaName)}");

    protected override Result<IReadOnlyList<SqlStatement>> GrantSchemaUsage(GrantSchemaUsage action) =>
        Statement($"GRANT USAGE ON SCHEMA {Quote(action.SchemaName)} TO {Quote(action.Role)}");

    protected override Result<IReadOnlyList<SqlStatement>> RevokeSchemaUsage(RevokeSchemaUsage action) =>
        Statement($"REVOKE USAGE ON SCHEMA {Quote(action.SchemaName)} FROM {Quote(action.Role)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetSchemaComment(SetSchemaComment action) =>
        Comment($"SCHEMA {Quote(action.SchemaName)}", action.NewComment);
}
