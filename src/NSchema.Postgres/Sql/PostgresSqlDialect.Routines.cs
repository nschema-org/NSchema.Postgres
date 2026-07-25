using NSchema.Model.Routines;
using NSchema.Plan.Domain;
using NSchema.Plan.Domain.Routines;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{
    // ── Routines ──────────────────────────────────────────────────────────────

    // A routine Add and a definition-only Modify both arrive as CreateRoutine; CREATE OR REPLACE serves both.
    // Functions and procedures are one model distinguished by RoutineKind, so a single set of actions carries the
    // keyword. The model has no overloading (one routine per name), so drops, renames and comments omit the
    // signature — Postgres resolves the bare name, and rejects it loudly if an out-of-model overload makes it
    // ambiguous.
    protected override Result<IReadOnlyList<SqlStatement>> CreateRoutine(CreateRoutine action)
    {
        var routine = action.Routine;
        return Statement($"CREATE OR REPLACE {RoutineKeyword(routine.RoutineKind)} {Qualify(action.SchemaName, routine.Name)}({routine.Arguments.Value}) {routine.Definition.Value}");
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropRoutine(DropRoutine action) =>
        Statement($"DROP {RoutineKeyword(action.Kind)} {Qualify(action.Routine)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameRoutine(RenameRoutine action) =>
        Statement($"ALTER {RoutineKeyword(action.Kind)} {Qualify(action.Routine)} RENAME TO {Quote(action.NewName)}");

    // A signature change cannot replace in place — CREATE OR REPLACE under a different argument list would create a
    // separate overload rather than replacing the routine. The drop also discards the catalog comment, so it is
    // re-issued from the desired model when one is set. The statements stay separate; the executor runs them inside
    // the same migration transaction.
    protected override Result<IReadOnlyList<SqlStatement>> RecreateRoutine(RecreateRoutine action)
    {
        var routine = action.Routine;
        var keyword = RoutineKeyword(routine.RoutineKind);
        var name = Qualify(action.SchemaName, routine.Name);
        var statements = new List<SqlStatement>
        {
            new($"DROP {keyword} {name}"),
            new($"CREATE {keyword} {name}({routine.Arguments.Value}) {routine.Definition.Value}"),
        };
        if (routine.Comment is { } comment)
        {
            statements.Add(new SqlStatement($"COMMENT ON {keyword} {name} IS $comment${comment}$comment$"));
        }

        return Statements([.. statements]);
    }

    protected override Result<IReadOnlyList<SqlStatement>> SetRoutineComment(SetRoutineComment action) =>
        Comment($"{RoutineKeyword(action.Kind)} {Qualify(action.Routine)}", action.NewComment);

    private static string RoutineKeyword(RoutineKind kind) => kind == RoutineKind.Procedure ? "PROCEDURE" : "FUNCTION";
}
