using NSchema.Model;
using NSchema.Plan.Domain;
using NSchema.Plan.Domain.Views;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{
    // ── Views ─────────────────────────────────────────────────────────────────

    // A create is a plain CREATE: if the view already exists, the database has drifted from the plan's
    // belief, and Postgres saying so is the correct outcome. A materialized view's indexes ride its
    // definition (the linearizer emits no separate CreateIndex for a created view), so they render here.
    protected override Result<IReadOnlyList<SqlStatement>> CreateView(CreateView action)
    {
        var view = action.View;

        // A plain view stores no rows, so Postgres has nothing to index. Other engines do index one — that is
        // what SQL Server's indexed view is — so the project grammar accepts the declaration and the refusal
        // belongs to the dialect that cannot honour it.
        if (view is { IsMaterialized: false, Indexes.Count: > 0 })
        {
            return Unsupported(action);
        }

        // Every Postgres view is schema-bound in effect — dependencies are tracked and what a view reads cannot
        // be dropped or retyped from under it — but there is no clause to write, and nothing to introspect back.
        // Declaring it would therefore drift on every plan, so it is refused rather than silently honoured.
        if (view.IsSchemaBound)
        {
            return Unsupported(action);
        }

        var address = new ObjectAddress(action.SchemaName, view.Name);
        return Statements([
            new($"CREATE {ViewKind(view.IsMaterialized)} {Qualify(action.SchemaName, view.Name)} AS {view.Body.Value}"),
            .. view.Indexes.Select(index => new SqlStatement(IndexSql(address, index))),
        ]);
    }

    // A body change replaces in place; the plan knows the view exists, so OR REPLACE is honest here. An
    // incompatible output-column change (rename/drop/retype/reorder) is rejected loudly by Postgres rather
    // than silently dropping dependents. A materialized view has no CREATE OR REPLACE form, so the core
    // plans its body change as drop + recreate and a ReplaceView is never materialized.
    protected override Result<IReadOnlyList<SqlStatement>> ReplaceView(ReplaceView action) =>
        action.View.IsSchemaBound
            ? Unsupported(action)
            : Statement($"CREATE OR REPLACE VIEW {Qualify(action.SchemaName, action.View.Name)} AS {action.View.Body.Value}");

    protected override Result<IReadOnlyList<SqlStatement>> DropView(DropView action) =>
        Statement($"DROP {ViewKind(action.IsMaterialized)} {Qualify(action.View)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameView(RenameView action) =>
        Statement($"ALTER {ViewKind(action.IsMaterialized)} {Qualify(action.View)} RENAME TO {Quote(action.NewName)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetViewComment(SetViewComment action) =>
        Comment($"{ViewKind(action.IsMaterialized)} {Qualify(action.View)}", action.NewComment);

    private static string ViewKind(bool isMaterialized) => isMaterialized ? "MATERIALIZED VIEW" : "VIEW";
}
