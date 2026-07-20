using System.Text;
using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.Constraints;
using NSchema.Model.Domains;
using NSchema.Model.Indexes;
using NSchema.Model.Routines;
using NSchema.Model.Sequences;
using NSchema.Model.Triggers;
using NSchema.Plan.Backends;
using NSchema.Plan.Model;
using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.CompositeTypes;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Domains;
using NSchema.Plan.Model.Enums;
using NSchema.Plan.Model.Extensions;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Routines;
using NSchema.Plan.Model.Schemas;
using NSchema.Plan.Model.Sequences;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Triggers;
using NSchema.Plan.Model.Views;

namespace NSchema.Postgres.Sql;

internal sealed class PostgresSqlDialect : SqlDialect
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

    // ── Tables ────────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateTable(CreateTable action)
    {
        var parts = action.Table.Columns.Select(BuildColumnDef).ToList();

        // Only the primary key is created inline; unique/check constraints, foreign keys and indexes arrive as
        // separate ALTER TABLE actions from the linearizer.
        if (action.Table.PrimaryKey is { } pk)
        {
            parts.Add($"CONSTRAINT {Quote(pk.Name)} PRIMARY KEY ({ColumnList(pk.ColumnNames)})");
        }

        return Statement($"""
            CREATE TABLE {Qualify(action.SchemaName, action.Table.Name)} (
                {string.Join(",\n    ", parts)}
            )
            """);
    }

    protected override Result<IReadOnlyList<SqlStatement>> SetTableComment(SetTableComment action) =>
        Comment($"TABLE {Qualify(action.Table)}", action.NewComment);

    // ── Columns ───────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> AddColumn(AddColumn action) =>
        Statement($"ALTER TABLE {Qualify(action.Table)} ADD COLUMN {BuildColumnDef(action.Column)}");

    protected override Result<IReadOnlyList<SqlStatement>> AlterColumnType(AlterColumnType action) =>
        Statement($"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} TYPE {ToPostgresType(action.NewType)}");

    protected override Result<IReadOnlyList<SqlStatement>> AlterColumnNullability(AlterColumnNullability action) =>
        Statement(action.NewNullable
            ? $"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} DROP NOT NULL"
            : $"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} SET NOT NULL");

    protected override Result<IReadOnlyList<SqlStatement>> AlterIdentitySequence(AlterIdentitySequence action)
    {
        var opts = action.NewOptions;
        var parts = new List<string>();
        if (opts?.MinValue is { } min)
        {
            parts.Add($"SET MINVALUE {min}");
        }

        if (opts?.StartWith is { } start)
        {
            parts.Add($"SET START {start}");
        }

        if (opts?.IncrementBy is { } increment)
        {
            parts.Add($"SET INCREMENT BY {increment}");
        }

        parts.Add("RESTART");
        return Statement($"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} {string.Join(" ", parts)}");
    }

    // Changing a column's generation expression in place: PG 17+ replaces it with SET EXPRESSION, and a generated
    // column is converted back to a plain one with DROP EXPRESSION (data is kept). PostgreSQL has no in-place way
    // to make an existing plain column generated, so that transition is unsupported — the column must be re-added.
    protected override Result<IReadOnlyList<SqlStatement>> SetColumnGenerated(SetColumnGenerated action) => action switch
    {
        { NewExpression: null } =>
            Statement($"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} DROP EXPRESSION"),
        { OldExpression: not null, NewExpression: { } expression } =>
            Statement($"ALTER TABLE {Qualify(action.Column.Owner)} ALTER COLUMN {Quote(action.Column.Member)} SET EXPRESSION AS ({expression.Value})"),
        _ => Unsupported(action),
    };

    protected override Result<IReadOnlyList<SqlStatement>> SetColumnComment(SetColumnComment action) =>
        Comment($"COLUMN {Qualify(action.Column.Owner)}.{Quote(action.Column.Member)}", action.NewComment);

    // ── Constraints ───────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> AddExclusionConstraint(AddExclusionConstraint action)
    {
        var exclusion = action.ExclusionConstraint;
        var method = exclusion.Method is { } m ? $" USING {m.Value}" : "";
        var elements = string.Join(", ", exclusion.Elements.Select(ExclusionElementText));
        var where = exclusion.Predicate is { } p ? $" WHERE ({p.Value})" : "";
        return Statement($"ALTER TABLE {Qualify(action.Table)} ADD CONSTRAINT {Quote(exclusion.Name)} EXCLUDE{method} ({elements}){where}");
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropExclusionConstraint(DropExclusionConstraint action) =>
        Statement($"ALTER TABLE {Qualify(action.Constraint.Owner)} DROP CONSTRAINT {Quote(action.Constraint.Member)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetConstraintComment(SetConstraintComment action) =>
        Comment($"CONSTRAINT {Quote(action.Constraint.Member)} ON {Qualify(action.Constraint.Owner)}", action.NewComment);

    // A plain column element is quoted; an expression element is parenthesised and verbatim. The operator follows
    // WITH (e.g. =, &&) and needs no quoting.
    private string ExclusionElementText(ExclusionElement element)
    {
        var target = element.Column is { } column ? Quote(column) : $"({element.Expression!.Value})";
        return $"{target} WITH {element.Operator}";
    }

    // ── Indexes ───────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateIndex(CreateIndex action)
    {
        var index = action.Index;
        var method = index.Method is { } m ? $" USING {m.Value}" : "";
        var keys = string.Join(", ", index.Columns.Select(IndexKeyText));
        var include = index.Include.Count > 0 ? $" INCLUDE ({ColumnList(index.Include)})" : "";
        var sql = $"CREATE {(index.IsUnique ? "UNIQUE " : "")}INDEX {Quote(index.Name)} ON {Qualify(action.Table)}{method} ({keys}){include}";
        return Statement(index.Predicate is { } predicate ? $"{sql} WHERE {predicate.Value}" : sql);
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropIndex(DropIndex action) =>
        Statement($"DROP INDEX {Qualify(action.Index.Schema, action.Index.Member)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetIndexComment(SetIndexComment action) =>
        Comment($"INDEX {Qualify(action.Index.Schema, action.Index.Member)}", action.NewComment);

    // A plain column key is quoted; an expression key is emitted parenthesised and verbatim. ASC/DESC and
    // NULLS FIRST/LAST are rendered only when explicit (IndexSort/IndexNulls.Default omits them, letting the
    // engine default stand so the index introspects back without drift).
    private string IndexKeyText(IndexColumn column)
    {
        var key = column.Column is { } name ? Quote(name) : $"({column.Expression!.Value})";
        var sort = column.Sort switch
        {
            IndexSort.Ascending => " ASC",
            IndexSort.Descending => " DESC",
            _ => "",
        };
        var nulls = column.Nulls switch
        {
            IndexNulls.First => " NULLS FIRST",
            IndexNulls.Last => " NULLS LAST",
            _ => "",
        };
        return $"{key}{sort}{nulls}";
    }

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

    // ── Domains ───────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateDomain(CreateDomain action) =>
        Statement(BuildCreateDomain(action.SchemaName, action.DomainType));

    protected override Result<IReadOnlyList<SqlStatement>> DropDomain(DropDomain action) =>
        Statement($"DROP DOMAIN {Qualify(action.Domain)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameDomain(RenameDomain action) =>
        Statement($"ALTER DOMAIN {Qualify(action.Domain)} RENAME TO {Quote(action.NewName)}");

    // A domain's base type cannot be altered in place (Postgres has no ALTER DOMAIN … TYPE), so a base-type change
    // drops + recreates — re-issuing the comment the drop discarded. Fails loudly if a column still uses the domain.
    protected override Result<IReadOnlyList<SqlStatement>> RecreateDomain(RecreateDomain action)
    {
        var statements = new List<SqlStatement>
        {
            new($"DROP DOMAIN {Qualify(action.SchemaName, action.DomainType.Name)}"),
            new(BuildCreateDomain(action.SchemaName, action.DomainType)),
        };
        if (action.DomainType.Comment is { } comment)
        {
            statements.Add(new SqlStatement($"COMMENT ON DOMAIN {Qualify(action.SchemaName, action.DomainType.Name)} IS $comment${comment}$comment$"));
        }

        return Statements([.. statements]);
    }

    protected override Result<IReadOnlyList<SqlStatement>> AlterDomainDefault(AlterDomainDefault action) =>
        Statement(action.NewDefault is { } newDefault
            ? $"ALTER DOMAIN {Qualify(action.Domain)} SET DEFAULT {newDefault.Value}"
            : $"ALTER DOMAIN {Qualify(action.Domain)} DROP DEFAULT");

    protected override Result<IReadOnlyList<SqlStatement>> AlterDomainNotNull(AlterDomainNotNull action) =>
        Statement(action.NotNull
            ? $"ALTER DOMAIN {Qualify(action.Domain)} SET NOT NULL"
            : $"ALTER DOMAIN {Qualify(action.Domain)} DROP NOT NULL");

    protected override Result<IReadOnlyList<SqlStatement>> AddDomainCheck(AddDomainCheck action) =>
        Statement($"ALTER DOMAIN {Qualify(action.Domain)} ADD CONSTRAINT {Quote(action.Check.Name)} CHECK ({action.Check.Expression.Value})");

    protected override Result<IReadOnlyList<SqlStatement>> DropDomainCheck(DropDomainCheck action) =>
        Statement($"ALTER DOMAIN {Qualify(action.Check.Owner)} DROP CONSTRAINT {Quote(action.Check.Member)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetDomainComment(SetDomainComment action) =>
        Comment($"DOMAIN {Qualify(action.Domain)}", action.NewComment);

    // CREATE DOMAIN name AS type [DEFAULT expr] [NOT NULL] [CONSTRAINT n CHECK (expr)]…
    private string BuildCreateDomain(SqlIdentifier schema, DomainType domain)
    {
        var sql = new StringBuilder($"CREATE DOMAIN {Qualify(schema, domain.Name)} AS {ToPostgresType(domain.DataType)}");
        if (domain.Default is { } def)
        {
            sql.Append($" DEFAULT {def.Value}");
        }
        if (domain.NotNull)
        {
            sql.Append(" NOT NULL");
        }
        foreach (var check in domain.Checks)
        {
            sql.Append($" CONSTRAINT {Quote(check.Name)} CHECK ({check.Expression.Value})");
        }
        return sql.ToString();
    }

    // ── Composite types ───────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateCompositeType(CreateCompositeType action)
    {
        var fields = string.Join(", ", action.CompositeType.Fields.Select(f => $"{Quote(f.Name)} {ToPostgresType(f.DataType)}"));
        return Statement($"CREATE TYPE {Qualify(action.SchemaName, action.CompositeType.Name)} AS ({fields})");
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropCompositeType(DropCompositeType action) =>
        Statement($"DROP TYPE {Qualify(action.Type)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameCompositeType(RenameCompositeType action) =>
        Statement($"ALTER TYPE {Qualify(action.Type)} RENAME TO {Quote(action.NewName)}");

    protected override Result<IReadOnlyList<SqlStatement>> AddCompositeField(AddCompositeField action) =>
        Statement($"ALTER TYPE {Qualify(action.Type)} ADD ATTRIBUTE {Quote(action.Field.Name)} {ToPostgresType(action.Field.DataType)}");

    protected override Result<IReadOnlyList<SqlStatement>> DropCompositeField(DropCompositeField action) =>
        Statement($"ALTER TYPE {Qualify(action.Field.Owner)} DROP ATTRIBUTE {Quote(action.Field.Member)}");

    protected override Result<IReadOnlyList<SqlStatement>> AlterCompositeFieldType(AlterCompositeFieldType action) =>
        Statement($"ALTER TYPE {Qualify(action.Field.Owner)} ALTER ATTRIBUTE {Quote(action.Field.Member)} TYPE {ToPostgresType(action.NewType)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetCompositeTypeComment(SetCompositeTypeComment action) =>
        Comment($"TYPE {Qualify(action.Type)}", action.NewComment);

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Result<IReadOnlyList<SqlStatement>> Comment(string target, string? comment) =>
        Statement(comment is null
            ? $"COMMENT ON {target} IS NULL"
            : $"COMMENT ON {target} IS $comment${comment}$comment$");

    private string BuildColumnDef(Column column)
    {
        var type = ToPostgresType(column.Type);
        var nullable = column.IsNullable ? "" : " NOT NULL";
        var identity = column.IsIdentity ? BuildIdentityClause(column.IdentityOptions) : "";
        var def = column is { DefaultExpression: { } d, IsIdentity: false } ? $" DEFAULT {d.Value}" : "";
        // A generated column is mutually exclusive with a default (the core's structural policy enforces this).
        var generated = column.GeneratedExpression is { } g ? $" GENERATED ALWAYS AS ({g.Value}) STORED" : "";
        return $"{Quote(column.Name)} {type}{nullable}{identity}{def}{generated}";
    }

    private static string BuildIdentityClause(IdentityOptions? options)
    {
        if (options is null)
        {
            return " GENERATED ALWAYS AS IDENTITY";
        }

        var parts = new List<string>();
        if (options.MinValue.HasValue)
        {
            parts.Add($"MINVALUE {options.MinValue}");
        }

        if (options.StartWith.HasValue)
        {
            parts.Add($"START WITH {options.StartWith}");
        }

        if (options.IncrementBy.HasValue)
        {
            parts.Add($"INCREMENT BY {options.IncrementBy}");
        }

        return parts.Count > 0
            ? $" GENERATED ALWAYS AS IDENTITY ({string.Join(" ", parts)})"
            : " GENERATED ALWAYS AS IDENTITY";
    }

    private static string EscapeLiteral(string value) => value.Replace("'", "''");

    // ── Type mapping ──────────────────────────────────────────────────────────

    private static string ToPostgresType(SqlType type) => type.Name.Value switch
    {
        "boolean" => "boolean",
        "tinyint" => "smallint",
        "smallint" => "smallint",
        "int" => "integer",
        "bigint" => "bigint",
        "float" => "real",
        "double" => "double precision",
        "decimal" => $"numeric({type.Precision}, {type.Scale})",
        "char" or "nchar" => $"character({type.Length})",
        "varchar" or "nvarchar" => type.Length is { } length ? $"character varying({length})" : "character varying",
        "text" => "text",
        "date" => "date",
        "time" => "time",
        "datetime" => "timestamp",
        "datetimeoffset" => "timestamptz",
        "guid" => "uuid",
        "binary" or "varbinary" => "bytea",
        // Any other name is a database-specific or user-defined type (e.g. citext, jsonb, a domain);
        // emit it verbatim, qualified when the model carries a schema.
        _ => type.Schema is { } schema ? $"{schema.Value}.{type.Name.Value}" : type.Name.Value,
    };
}
