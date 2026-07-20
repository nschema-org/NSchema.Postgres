using System.Text;
using NSchema.Model.Triggers;
using NSchema.Plan.Model;
using NSchema.Plan.Model.Triggers;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{
    // ── Triggers ──────────────────────────────────────────────────────────────

    // CREATE TRIGGER name {BEFORE|AFTER|INSTEAD OF} {event [OR …]} ON s.t FOR EACH {ROW|STATEMENT}
    //   [WHEN (cond)] EXECUTE FUNCTION fn(args)
    protected override Result<IReadOnlyList<SqlStatement>> CreateTrigger(CreateTrigger action)
    {
        var trigger = action.Trigger;
        if (trigger.Function is not { } function)
        {
            // Postgres triggers execute a function; a trigger carrying only a body belongs to another engine.
            return Unsupported(action);
        }

        var sql = new StringBuilder(
            $"CREATE TRIGGER {Quote(trigger.Name)} {TriggerTimingText(trigger.Timing)} {TriggerEventsText(trigger)} " +
            $"ON {Qualify(action.Table)} FOR EACH {(trigger.Level == TriggerLevel.Row ? "ROW" : "STATEMENT")}");
        if (trigger.When is { } when)
        {
            sql.Append($" WHEN ({when.Value})");
        }

        var functionName = function.Schema is { } schema ? Qualify(schema, function.Name) : Quote(function.Name);
        sql.Append($" EXECUTE FUNCTION {functionName}({trigger.FunctionArguments?.Value ?? string.Empty})");
        return Statement(sql.ToString());
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropTrigger(DropTrigger action) =>
        Statement($"DROP TRIGGER {Quote(action.Trigger.Member)} ON {Qualify(action.Trigger.Owner)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetTriggerComment(SetTriggerComment action) =>
        Comment($"TRIGGER {Quote(action.Trigger.Member)} ON {Qualify(action.Trigger.Owner)}", action.NewComment);

    private static string TriggerTimingText(TriggerTiming timing) => timing switch
    {
        TriggerTiming.Before => "BEFORE",
        TriggerTiming.After => "AFTER",
        TriggerTiming.InsteadOf => "INSTEAD OF",
        _ => throw new ArgumentOutOfRangeException(nameof(timing), timing, "Unknown trigger timing."),
    };

    private string TriggerEventsText(Trigger trigger)
    {
        var parts = new List<string>(4);
        if (trigger.Events.HasFlag(TriggerEvent.Insert))
        {
            parts.Add("INSERT");
        }
        if (trigger.Events.HasFlag(TriggerEvent.Update))
        {
            parts.Add(trigger.UpdateOfColumns.Count > 0 ? $"UPDATE OF {ColumnList(trigger.UpdateOfColumns)}" : "UPDATE");
        }
        if (trigger.Events.HasFlag(TriggerEvent.Delete))
        {
            parts.Add("DELETE");
        }
        if (trigger.Events.HasFlag(TriggerEvent.Truncate))
        {
            parts.Add("TRUNCATE");
        }
        return string.Join(" OR ", parts);
    }
}
