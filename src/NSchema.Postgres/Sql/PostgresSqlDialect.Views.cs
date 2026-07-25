using NSchema.Plan.Domain;
using NSchema.Plan.Domain.Views;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{
    // ── Views ─────────────────────────────────────────────────────────────────

    // A view Add and a body Modify both arrive as CreateView; CREATE OR REPLACE serves both. An incompatible
    // output-column change (rename/drop/retype/reorder) is rejected loudly by Postgres rather than silently
    // dropping dependents.
    // A materialized view has no CREATE OR REPLACE form, so the core plans a body change as drop + recreate;
    // CreateView for a matview is therefore always a fresh CREATE MATERIALIZED VIEW.
    protected override Result<IReadOnlyList<SqlStatement>> CreateView(CreateView action) =>
        Statement(action.View.IsMaterialized
            ? $"CREATE MATERIALIZED VIEW {Qualify(action.SchemaName, action.View.Name)} AS {action.View.Body.Value}"
            : $"CREATE OR REPLACE VIEW {Qualify(action.SchemaName, action.View.Name)} AS {action.View.Body.Value}");

    protected override Result<IReadOnlyList<SqlStatement>> DropView(DropView action) =>
        Statement($"DROP {ViewKind(action.IsMaterialized)} {Qualify(action.View)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameView(RenameView action) =>
        Statement($"ALTER {ViewKind(action.IsMaterialized)} {Qualify(action.View)} RENAME TO {Quote(action.NewName)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetViewComment(SetViewComment action) =>
        Comment($"{ViewKind(action.IsMaterialized)} {Qualify(action.View)}", action.NewComment);

    private static string ViewKind(bool isMaterialized) => isMaterialized ? "MATERIALIZED VIEW" : "VIEW";
}
