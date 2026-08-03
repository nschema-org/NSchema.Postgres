using NSchema.Plan.Domain;
using NSchema.Plan.Domain.Views;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{
    // ── Views ─────────────────────────────────────────────────────────────────

    // A create is a plain CREATE: if the view already exists, the database has drifted from the plan's
    // belief, and Postgres saying so is the correct outcome.
    protected override Result<IReadOnlyList<SqlStatement>> CreateView(CreateView action) =>
        Statement($"CREATE {ViewKind(action.View.IsMaterialized)} {Qualify(action.SchemaName, action.View.Name)} AS {action.View.Body.Value}");

    // A body change replaces in place; the plan knows the view exists, so OR REPLACE is honest here. An
    // incompatible output-column change (rename/drop/retype/reorder) is rejected loudly by Postgres rather
    // than silently dropping dependents. A materialized view has no CREATE OR REPLACE form, so the core
    // plans its body change as drop + recreate and a ReplaceView is never materialized.
    protected override Result<IReadOnlyList<SqlStatement>> ReplaceView(ReplaceView action) =>
        Statement($"CREATE OR REPLACE VIEW {Qualify(action.SchemaName, action.View.Name)} AS {action.View.Body.Value}");

    protected override Result<IReadOnlyList<SqlStatement>> DropView(DropView action) =>
        Statement($"DROP {ViewKind(action.IsMaterialized)} {Qualify(action.View)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameView(RenameView action) =>
        Statement($"ALTER {ViewKind(action.IsMaterialized)} {Qualify(action.View)} RENAME TO {Quote(action.NewName)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetViewComment(SetViewComment action) =>
        Comment($"{ViewKind(action.IsMaterialized)} {Qualify(action.View)}", action.NewComment);

    private static string ViewKind(bool isMaterialized) => isMaterialized ? "MATERIALIZED VIEW" : "VIEW";
}
