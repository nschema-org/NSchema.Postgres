using NSchema.Model.Columns;
using NSchema.Model.Sequences;
using NSchema.Plan.Domain;
using NSchema.Plan.Domain.Sequences;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{

    // ── Sequences ─────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateSequence(CreateSequence action)
    {
        var options = action.Sequence.Options;
        var parts = new List<string>();
        if (options.DataType is { } type)
        {
            parts.Add($"AS {ToPostgresType(type)}");
        }

        if (options.IncrementBy is { } increment)
        {
            parts.Add($"INCREMENT BY {increment}");
        }

        if (options.MinValue is { } min)
        {
            parts.Add($"MINVALUE {min}");
        }

        if (options.MaxValue is { } max)
        {
            parts.Add($"MAXVALUE {max}");
        }

        if (options.StartWith is { } start)
        {
            parts.Add($"START WITH {start}");
        }

        if (options.Cache is { } cache)
        {
            parts.Add($"CACHE {cache}");
        }

        if (options.Cycle)
        {
            parts.Add("CYCLE");
        }

        var clause = parts.Count > 0 ? $" {string.Join(" ", parts)}" : string.Empty;
        return Statement($"CREATE SEQUENCE {Qualify(action.SchemaName, action.Sequence.Name)}{clause}");
    }

    // One clause per option that differs; a value going back to null resets to the engine default explicitly
    // (AS bigint, INCREMENT BY 1, NO MINVALUE, NO MAXVALUE, CACHE 1, NO CYCLE), so apply → introspect normalizes
    // back to null and shows no residual drift.
    protected override Result<IReadOnlyList<SqlStatement>> AlterSequence(AlterSequence action)
    {
        var (old, @new) = (action.OldOptions, action.NewOptions);
        var parts = new List<string>();
        if (old.DataType != @new.DataType)
        {
            parts.Add($"AS {ToPostgresType(@new.DataType ?? SqlType.BigInt)}");
        }

        if (old.IncrementBy != @new.IncrementBy)
        {
            parts.Add($"INCREMENT BY {@new.IncrementBy ?? 1}");
        }

        if (old.MinValue != @new.MinValue)
        {
            parts.Add(@new.MinValue is { } min ? $"MINVALUE {min}" : "NO MINVALUE");
        }

        if (old.MaxValue != @new.MaxValue)
        {
            parts.Add(@new.MaxValue is { } max ? $"MAXVALUE {max}" : "NO MAXVALUE");
        }

        if (old.StartWith != @new.StartWith)
        {
            // There is no NO START form; a reset emits the default a fresh sequence with the new options would
            // have (effective minvalue ascending / maxvalue descending), so the next introspection reads null.
            parts.Add($"START WITH {@new.StartWith ?? DefaultStart(@new)}");
        }

        if (old.Cache != @new.Cache)
        {
            parts.Add($"CACHE {@new.Cache ?? 1}");
        }

        if (old.Cycle != @new.Cycle)
        {
            parts.Add(@new.Cycle ? "CYCLE" : "NO CYCLE");
        }

        return Statement($"ALTER SEQUENCE {Qualify(action.Sequence)} {string.Join(" ", parts)}");
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropSequence(DropSequence action) =>
        Statement($"DROP SEQUENCE {Qualify(action.Sequence)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameSequence(RenameSequence action) =>
        Statement($"ALTER SEQUENCE {Qualify(action.Sequence)} RENAME TO {Quote(action.NewName)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetSequenceComment(SetSequenceComment action) =>
        Comment($"SEQUENCE {Qualify(action.Sequence)}", action.NewComment);

    private static long DefaultStart(SequenceOptions options) =>
        (options.IncrementBy ?? 1) > 0 ? options.MinValue ?? 1 : options.MaxValue ?? -1;
}
