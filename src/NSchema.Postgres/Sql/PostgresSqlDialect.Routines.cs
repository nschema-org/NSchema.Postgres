using NSchema.Model;
using NSchema.Model.Routines;
using NSchema.Plan.Domain;
using NSchema.Plan.Domain.Routines;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{
    // ── Routines ──────────────────────────────────────────────────────────────

    // A create is a plain CREATE: if the routine already exists, the database has drifted from the plan's
    // belief, and Postgres saying so is the correct outcome. Functions, procedures, and aggregates are one
    // model distinguished by RoutineKind, so a single set of actions carries the keyword. The model has no
    // overloading (one routine per name), so a function's drops, renames and comments omit the signature —
    // Postgres resolves the bare name, and rejects it loudly if an out-of-model overload makes it ambiguous.
    // An aggregate is addressed WITH its signature: Postgres requires one on DROP/ALTER/COMMENT, and an
    // aggregate signature cannot carry DEFAULT clauses, so the declared arguments paste verbatim.
    protected override Result<IReadOnlyList<SqlStatement>> CreateRoutine(CreateRoutine action)
    {
        var routine = action.Routine;
        return Statement($"CREATE {RoutineKeyword(routine.RoutineKind)} {Qualify(action.SchemaName, routine.Name)}({routine.Arguments.Value}) {routine.Definition.Value}");
    }

    // A definition-only change replaces in place; the plan knows the routine exists, so OR REPLACE is honest
    // here. Postgres has no CREATE OR REPLACE AGGREGATE, so an aggregate's replacement decomposes to a drop
    // and a create — the mechanism is the dialect's to choose — re-issuing the comment the drop discards.
    protected override Result<IReadOnlyList<SqlStatement>> ReplaceRoutine(ReplaceRoutine action)
    {
        var routine = action.Routine;
        if (routine.RoutineKind == RoutineKind.Aggregate)
        {
            return DropAndCreate(action.SchemaName, routine, routine.Arguments);
        }

        return Statement($"CREATE OR REPLACE {RoutineKeyword(routine.RoutineKind)} {Qualify(action.SchemaName, routine.Name)}({routine.Arguments.Value}) {routine.Definition.Value}");
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropRoutine(DropRoutine action) =>
        Statement($"DROP {RoutineKeyword(action.Kind)} {Address(action.Routine, action.Kind, action.Arguments)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameRoutine(RenameRoutine action) =>
        Statement($"ALTER {RoutineKeyword(action.Kind)} {Address(action.Routine, action.Kind, action.Arguments)} RENAME TO {Quote(action.NewName)}");

    // A signature change cannot replace in place — CREATE OR REPLACE under a different argument list would create a
    // separate overload rather than replacing the routine. The statements stay separate; the executor runs them
    // inside the same migration transaction.
    protected override Result<IReadOnlyList<SqlStatement>> RecreateRoutine(RecreateRoutine action) =>
        DropAndCreate(action.SchemaName, action.Routine, action.PreviousArguments);

    protected override Result<IReadOnlyList<SqlStatement>> SetRoutineComment(SetRoutineComment action) =>
        Comment($"{RoutineKeyword(action.Kind)} {Address(action.Routine, action.Kind, action.Arguments)}", action.NewComment);

    // The drop discards the catalog comment, so it is re-issued from the desired model when one is set.
    private Result<IReadOnlyList<SqlStatement>> DropAndCreate(SqlIdentifier schemaName, Routine routine, SqlText? dropArguments)
    {
        var keyword = RoutineKeyword(routine.RoutineKind);
        var name = Qualify(schemaName, routine.Name);
        var statements = new List<SqlStatement>
        {
            new($"DROP {keyword} {Address(new ObjectAddress(schemaName, routine.Name), routine.RoutineKind, dropArguments)}"),
            new($"CREATE {keyword} {name}({routine.Arguments.Value}) {routine.Definition.Value}"),
        };
        if (routine.Comment is { } comment)
        {
            statements.Add(new SqlStatement($"COMMENT ON {keyword} {Address(new ObjectAddress(schemaName, routine.Name), routine.RoutineKind, routine.Arguments)} IS $comment${comment}$comment$"));
        }

        return Statements([.. statements]);
    }

    private string Address(ObjectAddress routine, RoutineKind kind, SqlText? arguments) =>
        kind == RoutineKind.Aggregate
            ? $"{Qualify(routine)}({arguments?.Value ?? string.Empty})"
            : Qualify(routine);

    private static string RoutineKeyword(RoutineKind kind) => kind switch
    {
        RoutineKind.Procedure => "PROCEDURE",
        RoutineKind.Aggregate => "AGGREGATE",
        _ => "FUNCTION",
    };
}
