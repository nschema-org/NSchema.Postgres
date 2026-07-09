using System.Text;
using NSchema.Plan.Model;
using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.CompositeTypes;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Domains;
using NSchema.Plan.Model.Enums;
using NSchema.Plan.Model.Extensions;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Migrations;
using NSchema.Plan.Model.Routines;
using NSchema.Plan.Model.Schemas;
using NSchema.Plan.Model.Sequence;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Triggers;
using NSchema.Plan.Model.Views;
using NSchema.Schema.Model.Columns;
using NSchema.Schema.Model.Constraints;
using NSchema.Schema.Model.Domains;
using NSchema.Schema.Model.Indexes;
using NSchema.Schema.Model.Routines;
using NSchema.Schema.Model.Sequences;
using NSchema.Schema.Model.Tables;
using NSchema.Schema.Model.Triggers;
using NSchema.Sql;
using NSchema.Sql.Model;

namespace NSchema.Postgres.Sql;

internal sealed class PostgresSqlGenerator : ISqlGenerator
{
    public SqlPlan Generate(MigrationPlan plan)
    {
        var preDeploymentStatements = plan.PreDeploymentScripts.Select(s => new SqlStatement(s.Sql, s.RunOutsideTransaction));
        var postDeploymentStatements = plan.PostDeploymentScripts.Select(s => new SqlStatement(s.Sql, s.RunOutsideTransaction));
        var statements = plan.Actions.SelectMany(GenerateStatements).ToList();
        List<SqlStatement> allStatements = [.. preDeploymentStatements, .. statements, .. postDeploymentStatements];
        return new SqlPlan(allStatements);
    }

    // ── SQL generation ────────────────────────────────────────────────────────

    private static IEnumerable<SqlStatement> GenerateStatements(MigrationAction action) => action switch
    {
        // A value added by ALTER TYPE … ADD VALUE cannot be used until the transaction that added it commits, so
        // the statement is carved out of the surrounding transaction. The executor commits the pending segment,
        // runs it alone, and resumes — ordering relative to later statements that use the value is preserved.
        AddEnumValue x => [new SqlStatement(BuildAddEnumValue(x), RunOutsideTransaction: true)],
        // Data-migration SQL is user-authored for the target dialect and passed through verbatim.
        ExecuteDataMigration x => [new SqlStatement(x.Sql, x.RunOutsideTransaction)],
        RecreateRoutine x => BuildRecreateRoutine(RoutineKeyword(x.Routine.Kind), x.SchemaName, x.Routine.Name, x.Routine.Arguments, x.Routine.Definition, x.Routine.Comment),
        RecreateDomain x => BuildRecreateDomain(x),
        _ => [new SqlStatement(GenerateSql(action))],
    };

    private static string GenerateSql(MigrationAction action) => action switch
    {
        CreateSchema x => $"CREATE SCHEMA IF NOT EXISTS \"{x.SchemaName}\"",
        DropSchema x => $"DROP SCHEMA \"{x.SchemaName}\"",
        RenameSchema x => $"ALTER SCHEMA \"{x.OldName}\" RENAME TO \"{x.NewName}\"",
        CreateTable x => BuildCreateTable(x),
        DropTable x => $"DROP TABLE \"{x.SchemaName}\".\"{x.TableName}\"",
        RenameTable x => $"ALTER TABLE \"{x.SchemaName}\".\"{x.OldName}\" RENAME TO \"{x.NewName}\"",
        AddColumn x => $"""ALTER TABLE "{x.SchemaName}"."{x.TableName}" ADD COLUMN {BuildColumnDef(x.Column)}""",
        DropColumn x => $"ALTER TABLE \"{x.SchemaName}\".\"{x.TableName}\" DROP COLUMN \"{x.ColumnName}\"",
        RenameColumn x => $"ALTER TABLE \"{x.SchemaName}\".\"{x.TableName}\" RENAME COLUMN \"{x.OldName}\" TO \"{x.NewName}\"",
        AlterColumnType x => $"""ALTER TABLE "{x.SchemaName}"."{x.TableName}" ALTER COLUMN "{x.ColumnName}" TYPE {ToPostgresType(x.NewType)}""",
        AlterColumnNullability { NewNullable: false } x => $"""ALTER TABLE "{x.SchemaName}"."{x.TableName}" ALTER COLUMN "{x.ColumnName}" SET NOT NULL""",
        AlterColumnNullability x => $"""ALTER TABLE "{x.SchemaName}"."{x.TableName}" ALTER COLUMN "{x.ColumnName}" DROP NOT NULL""",
        AlterIdentitySequence x => BuildAlterIdentitySequence(x),
        SetColumnDefault { NewDefault: null } x => $"""ALTER TABLE "{x.SchemaName}"."{x.TableName}" ALTER COLUMN "{x.ColumnName}" DROP DEFAULT""",
        SetColumnDefault x => $"""ALTER TABLE "{x.SchemaName}"."{x.TableName}" ALTER COLUMN "{x.ColumnName}" SET DEFAULT {x.NewDefault}""",
        SetColumnGenerated x => BuildSetColumnGenerated(x),
        AddPrimaryKey x => $"""ALTER TABLE "{x.SchemaName}"."{x.TableName}" ADD CONSTRAINT "{x.PrimaryKey.Name}" PRIMARY KEY ({ColList(x.PrimaryKey.ColumnNames)})""",
        DropPrimaryKey x => $"ALTER TABLE \"{x.SchemaName}\".\"{x.TableName}\" DROP CONSTRAINT \"{x.PrimaryKeyName}\"",
        AddForeignKey x => BuildAddForeignKey(x),
        DropForeignKey x => $"ALTER TABLE \"{x.SchemaName}\".\"{x.TableName}\" DROP CONSTRAINT \"{x.ForeignKeyName}\"",
        AddUniqueConstraint x => $"""ALTER TABLE "{x.SchemaName}"."{x.TableName}" ADD CONSTRAINT "{x.UniqueConstraint.Name}" UNIQUE ({ColList(x.UniqueConstraint.ColumnNames)})""",
        DropUniqueConstraint x => $"ALTER TABLE \"{x.SchemaName}\".\"{x.TableName}\" DROP CONSTRAINT \"{x.ConstraintName}\"",
        AddCheckConstraint x => $"""ALTER TABLE "{x.SchemaName}"."{x.TableName}" ADD CONSTRAINT "{x.CheckConstraint.Name}" CHECK ({x.CheckConstraint.Expression})""",
        DropCheckConstraint x => $"ALTER TABLE \"{x.SchemaName}\".\"{x.TableName}\" DROP CONSTRAINT \"{x.ConstraintName}\"",
        AddExclusionConstraint x => BuildAddExclusionConstraint(x),
        DropExclusionConstraint x => $"ALTER TABLE \"{x.SchemaName}\".\"{x.TableName}\" DROP CONSTRAINT \"{x.ConstraintName}\"",
        CreateIndex x => BuildCreateIndex(x),
        DropIndex x => $"DROP INDEX \"{x.SchemaName}\".\"{x.IndexName}\"",
        CreateTrigger x => BuildCreateTrigger(x),
        DropTrigger x => $"DROP TRIGGER \"{x.TriggerName}\" ON \"{x.SchemaName}\".\"{x.TableName}\"",
        SetTriggerComment x => x.NewComment is null
            ? $"""COMMENT ON TRIGGER "{x.TriggerName}" ON "{x.SchemaName}"."{x.TableName}" IS NULL"""
            : $"""COMMENT ON TRIGGER "{x.TriggerName}" ON "{x.SchemaName}"."{x.TableName}" IS $comment${x.NewComment}$comment$""",
        // A view Add and a body Modify both arrive as CreateView; CREATE OR REPLACE serves both. An incompatible
        // output-column change (rename/drop/retype/reorder) is rejected loudly by Postgres rather than silently
        // dropping dependents — see CLAUDE.md / the core view-body decision.
        // A materialized view has no CREATE OR REPLACE form, so the core plans a body change as drop + recreate;
        // CreateView for a matview is therefore always a fresh CREATE MATERIALIZED VIEW. A plain view's body
        // change is an in-place CREATE OR REPLACE.
        CreateView { View.IsMaterialized: true } x => $"""CREATE MATERIALIZED VIEW "{x.SchemaName}"."{x.View.Name}" AS {x.View.Body}""",
        CreateView x => $"""CREATE OR REPLACE VIEW "{x.SchemaName}"."{x.View.Name}" AS {x.View.Body}""",
        DropView x => $"DROP {ViewKind(x.IsMaterialized)} \"{x.SchemaName}\".\"{x.ViewName}\"",
        RenameView x => $"ALTER {ViewKind(x.IsMaterialized)} \"{x.SchemaName}\".\"{x.OldName}\" RENAME TO \"{x.NewName}\"",
        SetViewComment x => x.NewComment is null
            ? $"""COMMENT ON {ViewKind(x.IsMaterialized)} "{x.SchemaName}"."{x.ViewName}" IS NULL"""
            : $"""COMMENT ON {ViewKind(x.IsMaterialized)} "{x.SchemaName}"."{x.ViewName}" IS $comment${x.NewComment}$comment$""",
        CreateEnum x => BuildCreateEnum(x),
        DropEnum x => $"DROP TYPE \"{x.SchemaName}\".\"{x.EnumName}\"",
        RenameEnum x => $"ALTER TYPE \"{x.SchemaName}\".\"{x.OldName}\" RENAME TO \"{x.NewName}\"",
        SetEnumComment x => x.NewComment is null
            ? $"""COMMENT ON TYPE "{x.SchemaName}"."{x.EnumName}" IS NULL"""
            : $"""COMMENT ON TYPE "{x.SchemaName}"."{x.EnumName}" IS $comment${x.NewComment}$comment$""",
        CreateDomain x => BuildCreateDomain(x.SchemaName, x.Domain),
        DropDomain x => $"DROP DOMAIN \"{x.SchemaName}\".\"{x.DomainName}\"",
        RenameDomain x => $"ALTER DOMAIN \"{x.SchemaName}\".\"{x.OldName}\" RENAME TO \"{x.NewName}\"",
        AlterDomainDefault { NewDefault: null } x => $"""ALTER DOMAIN "{x.SchemaName}"."{x.DomainName}" DROP DEFAULT""",
        AlterDomainDefault x => $"""ALTER DOMAIN "{x.SchemaName}"."{x.DomainName}" SET DEFAULT {x.NewDefault}""",
        AlterDomainNotNull { NotNull: true } x => $"""ALTER DOMAIN "{x.SchemaName}"."{x.DomainName}" SET NOT NULL""",
        AlterDomainNotNull x => $"""ALTER DOMAIN "{x.SchemaName}"."{x.DomainName}" DROP NOT NULL""",
        AddDomainCheck x => $"""ALTER DOMAIN "{x.SchemaName}"."{x.DomainName}" ADD CONSTRAINT "{x.Check.Name}" CHECK ({x.Check.Expression})""",
        DropDomainCheck x => $"ALTER DOMAIN \"{x.SchemaName}\".\"{x.DomainName}\" DROP CONSTRAINT \"{x.CheckName}\"",
        SetDomainComment x => x.NewComment is null
            ? $"""COMMENT ON DOMAIN "{x.SchemaName}"."{x.DomainName}" IS NULL"""
            : $"""COMMENT ON DOMAIN "{x.SchemaName}"."{x.DomainName}" IS $comment${x.NewComment}$comment$""",
        CreateCompositeType x => BuildCreateCompositeType(x),
        DropCompositeType x => $"DROP TYPE \"{x.SchemaName}\".\"{x.TypeName}\"",
        RenameCompositeType x => $"ALTER TYPE \"{x.SchemaName}\".\"{x.OldName}\" RENAME TO \"{x.NewName}\"",
        SetCompositeTypeComment x => x.NewComment is null
            ? $"""COMMENT ON TYPE "{x.SchemaName}"."{x.TypeName}" IS NULL"""
            : $"""COMMENT ON TYPE "{x.SchemaName}"."{x.TypeName}" IS $comment${x.NewComment}$comment$""",
        AddCompositeField x => $"""ALTER TYPE "{x.SchemaName}"."{x.TypeName}" ADD ATTRIBUTE "{x.Field.Name}" {ToPostgresType(x.Field.DataType)}""",
        DropCompositeField x => $"ALTER TYPE \"{x.SchemaName}\".\"{x.TypeName}\" DROP ATTRIBUTE \"{x.FieldName}\"",
        AlterCompositeFieldType x => $"""ALTER TYPE "{x.SchemaName}"."{x.TypeName}" ALTER ATTRIBUTE "{x.FieldName}" TYPE {ToPostgresType(x.NewType)}""",
        CreateSequence x => BuildCreateSequence(x),
        DropSequence x => $"DROP SEQUENCE \"{x.SchemaName}\".\"{x.SequenceName}\"",
        RenameSequence x => $"ALTER SEQUENCE \"{x.SchemaName}\".\"{x.OldName}\" RENAME TO \"{x.NewName}\"",
        AlterSequence x => BuildAlterSequence(x),
        SetSequenceComment x => x.NewComment is null
            ? $"""COMMENT ON SEQUENCE "{x.SchemaName}"."{x.SequenceName}" IS NULL"""
            : $"""COMMENT ON SEQUENCE "{x.SchemaName}"."{x.SequenceName}" IS $comment${x.NewComment}$comment$""",
        // A routine Add and a definition-only Modify both arrive as CreateRoutine; CREATE OR REPLACE serves both.
        // Functions and procedures are one model distinguished by Kind, so a single set of actions carries the
        // keyword. The model has no overloading (one routine per name), so drops, renames and comments omit the
        // signature — Postgres resolves the bare name, and rejects it loudly if an out-of-model overload makes it
        // ambiguous.
        CreateRoutine x => $"""CREATE OR REPLACE {RoutineKeyword(x.Routine.Kind)} "{x.SchemaName}"."{x.Routine.Name}"({x.Routine.Arguments}) {x.Routine.Definition}""",
        DropRoutine x => $"DROP {RoutineKeyword(x.Kind)} \"{x.SchemaName}\".\"{x.RoutineName}\"",
        RenameRoutine x => $"ALTER {RoutineKeyword(x.Kind)} \"{x.SchemaName}\".\"{x.OldName}\" RENAME TO \"{x.NewName}\"",
        SetRoutineComment x => x.NewComment is null
            ? $"""COMMENT ON {RoutineKeyword(x.Kind)} "{x.SchemaName}"."{x.RoutineName}" IS NULL"""
            : $"""COMMENT ON {RoutineKeyword(x.Kind)} "{x.SchemaName}"."{x.RoutineName}" IS $comment${x.NewComment}$comment$""",
        SetSchemaComment x => x.NewComment is null
            ? $"""COMMENT ON SCHEMA "{x.SchemaName}" IS NULL"""
            : $"""COMMENT ON SCHEMA "{x.SchemaName}" IS $comment${x.NewComment}$comment$""",
        SetTableComment x => x.NewComment is null
            ? $"""COMMENT ON TABLE "{x.SchemaName}"."{x.TableName}" IS NULL"""
            : $"""COMMENT ON TABLE "{x.SchemaName}"."{x.TableName}" IS $comment${x.NewComment}$comment$""",
        SetColumnComment x => x.NewComment is null
            ? $"""COMMENT ON COLUMN "{x.SchemaName}"."{x.TableName}"."{x.ColumnName}" IS NULL"""
            : $"""COMMENT ON COLUMN "{x.SchemaName}"."{x.TableName}"."{x.ColumnName}" IS $comment${x.NewComment}$comment$""",
        SetIndexComment x => x.NewComment is null
            ? $"""COMMENT ON INDEX "{x.SchemaName}"."{x.IndexName}" IS NULL"""
            : $"""COMMENT ON INDEX "{x.SchemaName}"."{x.IndexName}" IS $comment${x.NewComment}$comment$""",
        SetConstraintComment x => x.NewComment is null
            ? $"""COMMENT ON CONSTRAINT "{x.ConstraintName}" ON "{x.SchemaName}"."{x.TableName}" IS NULL"""
            : $"""COMMENT ON CONSTRAINT "{x.ConstraintName}" ON "{x.SchemaName}"."{x.TableName}" IS $comment${x.NewComment}$comment$""",
        CreateExtension x => BuildCreateExtension(x),
        // A version change updates in place; with no target version, UPDATE moves to the default (latest) version.
        AlterExtension { NewVersion: { } v } x => $"""ALTER EXTENSION "{x.ExtensionName}" UPDATE TO '{EscapeLiteral(v)}'""",
        AlterExtension x => $"""ALTER EXTENSION "{x.ExtensionName}" UPDATE""",
        DropExtension x => $"DROP EXTENSION \"{x.ExtensionName}\"",
        SetExtensionComment x => x.NewComment is null
            ? $"""COMMENT ON EXTENSION "{x.ExtensionName}" IS NULL"""
            : $"""COMMENT ON EXTENSION "{x.ExtensionName}" IS $comment${x.NewComment}$comment$""",
        GrantSchemaUsage x => $"""GRANT USAGE ON SCHEMA "{x.SchemaName}" TO {x.Role}""",
        RevokeSchemaUsage x => $"""REVOKE USAGE ON SCHEMA "{x.SchemaName}" FROM {x.Role}""",
        GrantTablePrivileges x => $"""GRANT {PrivilegeList(x.Privileges)} ON TABLE "{x.SchemaName}"."{x.TableName}" TO {x.Role}""",
        RevokeTablePrivileges x => $"""REVOKE ALL PRIVILEGES ON TABLE "{x.SchemaName}"."{x.TableName}" FROM {x.Role}""",
        _ => throw new ArgumentOutOfRangeException(nameof(action), $"Unhandled action type: {action.GetType().Name}. The plan may come from a newer NSchema.Core than this provider supports — check for a provider update.")
    };

    private static string BuildCreateTable(CreateTable x)
    {
        var parts = x.Table.Columns.Select(BuildColumnDef).ToList();

        // Only the primary key is created inline; unique/check constraints, foreign keys and indexes arrive as
        // separate ALTER TABLE actions from the linearizer (see DefaultPlanLinearizer.EmitTable).
        if (x.Table.PrimaryKey is { } pk)
        {
            parts.Add($"""CONSTRAINT "{pk.Name}" PRIMARY KEY ({ColList(pk.ColumnNames)})""");
        }

        return $"""
            CREATE TABLE "{x.SchemaName}"."{x.Table.Name}" (
                {string.Join(",\n    ", parts)}
            )
            """;
    }

    private static string BuildAddExclusionConstraint(AddExclusionConstraint x)
    {
        var ex = x.ExclusionConstraint;
        var method = ex.Method is { } m ? $" USING {m}" : "";
        var elements = string.Join(", ", ex.Elements.Select(ExclusionElementText));
        var where = ex.Predicate is { } p ? $" WHERE ({p})" : "";
        return $"""ALTER TABLE "{x.SchemaName}"."{x.TableName}" ADD CONSTRAINT "{ex.Name}" EXCLUDE{method} ({elements}){where}""";
    }

    // A plain column element is quoted; an expression element is parenthesised and verbatim. The operator follows
    // WITH (e.g. =, &&) and needs no quoting.
    private static string ExclusionElementText(ExclusionElement element)
    {
        var target = element.IsExpression ? $"({element.Expression})" : $"\"{element.Expression}\"";
        return $"{target} WITH {element.Operator}";
    }

    private static string BuildAddForeignKey(AddForeignKey x)
    {
        var fk = x.ForeignKey;
        var onDelete = ToReferentialAction(fk.OnDelete);
        var onUpdate = ToReferentialAction(fk.OnUpdate);
        return $"""ALTER TABLE "{x.SchemaName}"."{x.TableName}" ADD CONSTRAINT "{fk.Name}" FOREIGN KEY ({ColList(fk.ColumnNames)}) REFERENCES "{fk.ReferencedSchema}"."{fk.ReferencedTable}" ({ColList(fk.ReferencedColumnNames)}) ON DELETE {onDelete} ON UPDATE {onUpdate}""";
    }

    private static string BuildCreateIndex(CreateIndex x)
    {
        var idx = x.Index;
        var method = idx.Method is { } m ? $" USING {m}" : "";
        var keys = string.Join(", ", idx.Columns.Select(IndexKeyText));
        var include = idx.Include.Count > 0 ? $" INCLUDE ({ColList(idx.Include)})" : "";
        var sql = $"""CREATE {(idx.IsUnique ? "UNIQUE " : "")}INDEX "{idx.Name}" ON "{x.SchemaName}"."{x.TableName}"{method} ({keys}){include}""";
        return idx.Predicate is { } pred ? $"{sql} WHERE {pred}" : sql;
    }

    // A plain column key is quoted; an expression key is emitted parenthesised and verbatim. ASC/DESC and
    // NULLS FIRST/LAST are rendered only when explicit (IndexSort/IndexNulls.Default omits them, letting the
    // engine default stand so the index introspects back without drift).
    private static string IndexKeyText(IndexColumn col)
    {
        var key = col.IsExpression ? $"({col.Expression})" : $"\"{col.Expression}\"";
        var sort = col.Sort switch
        {
            IndexSort.Ascending => " ASC",
            IndexSort.Descending => " DESC",
            _ => "",
        };
        var nulls = col.Nulls switch
        {
            IndexNulls.First => " NULLS FIRST",
            IndexNulls.Last => " NULLS LAST",
            _ => "",
        };
        return $"{key}{sort}{nulls}";
    }

    private static string BuildColumnDef(Column col)
    {
        var type = ToPostgresType(col.Type);
        var nullable = col.IsNullable ? "" : " NOT NULL";
        var identity = col.IsIdentity ? BuildIdentityClause(col.IdentityOptions) : "";
        var def = col is { DefaultExpression: { } d, IsIdentity: false } ? $" DEFAULT {d}" : "";
        // A generated column is mutually exclusive with a default (the core's structural policy enforces this).
        var generated = col.GeneratedExpression is { } g ? $" GENERATED ALWAYS AS ({g}) STORED" : "";
        return $"\"{col.Name}\" {type}{nullable}{identity}{def}{generated}";
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

    private static string BuildAlterIdentitySequence(AlterIdentitySequence x)
    {
        var opts = x.NewOptions;
        var parts = new List<string>();
        if (opts?.MinValue.HasValue == true)
        {
            parts.Add($"SET MINVALUE {opts.MinValue}");
        }

        if (opts?.StartWith.HasValue == true)
        {
            parts.Add($"SET START {opts.StartWith}");
        }

        if (opts?.IncrementBy.HasValue == true)
        {
            parts.Add($"SET INCREMENT BY {opts.IncrementBy}");
        }

        parts.Add("RESTART");
        return $"""ALTER TABLE "{x.SchemaName}"."{x.TableName}" ALTER COLUMN "{x.ColumnName}" {string.Join(" ", parts)}""";
    }

    private static string BuildCreateEnum(CreateEnum x)
    {
        var values = string.Join(", ", x.Enum.Values.Select(v => $"'{EscapeLiteral(v)}'"));
        return $"""CREATE TYPE "{x.SchemaName}"."{x.Enum.Name}" AS ENUM ({values})""";
    }

    private static string BuildAddEnumValue(AddEnumValue x)
    {
        var sql = $"""ALTER TYPE "{x.SchemaName}"."{x.EnumName}" ADD VALUE '{EscapeLiteral(x.Value)}'""";
        return x switch
        {
            { Before: { } before } => $"{sql} BEFORE '{EscapeLiteral(before)}'",
            { After: { } after } => $"{sql} AFTER '{EscapeLiteral(after)}'",
            _ => sql,
        };
    }

    private static string BuildCreateSequence(CreateSequence x)
    {
        var o = x.Sequence.Options;
        var parts = new List<string>();
        if (o.DataType is { } type)
        {
            parts.Add($"AS {ToPostgresType(type)}");
        }

        if (o.IncrementBy is { } increment)
        {
            parts.Add($"INCREMENT BY {increment}");
        }

        if (o.MinValue is { } min)
        {
            parts.Add($"MINVALUE {min}");
        }

        if (o.MaxValue is { } max)
        {
            parts.Add($"MAXVALUE {max}");
        }

        if (o.StartWith is { } start)
        {
            parts.Add($"START WITH {start}");
        }

        if (o.Cache is { } cache)
        {
            parts.Add($"CACHE {cache}");
        }

        if (o.Cycle)
        {
            parts.Add("CYCLE");
        }

        var clause = parts.Count > 0 ? $" {string.Join(" ", parts)}" : string.Empty;
        return $"""CREATE SEQUENCE "{x.SchemaName}"."{x.Sequence.Name}"{clause}""";
    }

    // One clause per option that differs; a value going back to null resets to the engine default explicitly
    // (AS bigint, INCREMENT BY 1, NO MINVALUE, NO MAXVALUE, CACHE 1, NO CYCLE), so apply → introspect normalizes
    // back to null and shows no residual drift.
    private static string BuildAlterSequence(AlterSequence x)
    {
        var (old, @new) = (x.OldOptions, x.NewOptions);
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

        return $"""ALTER SEQUENCE "{x.SchemaName}"."{x.SequenceName}" {string.Join(" ", parts)}""";
    }

    // A signature change cannot replace in place — CREATE OR REPLACE under a different argument list would create a
    // separate overload rather than replacing the routine. The drop also discards the catalog comment, so it is
    // re-issued from the desired model when one is set. The statements stay separate; the executor runs them inside
    // the same migration transaction.
    private static IEnumerable<SqlStatement> BuildRecreateRoutine(string kind, string schemaName, string name, string arguments, string definition, string? comment)
    {
        yield return new SqlStatement($"DROP {kind} \"{schemaName}\".\"{name}\"");
        yield return new SqlStatement($"""CREATE {kind} "{schemaName}"."{name}"({arguments}) {definition}""");
        if (comment is not null)
        {
            yield return new SqlStatement($"""COMMENT ON {kind} "{schemaName}"."{name}" IS $comment${comment}$comment$""");
        }
    }

    // Changing a column's generation expression in place: PG 17+ replaces it with SET EXPRESSION, and a generated
    // column is converted back to a plain one with DROP EXPRESSION (data is kept). PostgreSQL has no in-place way
    // to make an existing plain column generated, so that transition is rejected — the column must be re-added.
    private static string BuildSetColumnGenerated(SetColumnGenerated x) => x switch
    {
        { NewExpression: null } => $"""ALTER TABLE "{x.SchemaName}"."{x.TableName}" ALTER COLUMN "{x.ColumnName}" DROP EXPRESSION""",
        { OldExpression: not null, NewExpression: { } expr } => $"""ALTER TABLE "{x.SchemaName}"."{x.TableName}" ALTER COLUMN "{x.ColumnName}" SET EXPRESSION AS ({expr})""",
        _ => throw new NotSupportedException(
            $"""Cannot make existing column "{x.SchemaName}"."{x.TableName}"."{x.ColumnName}" generated in place; PostgreSQL has no ADD GENERATED — drop and re-add the column."""),
    };

    private static string BuildCreateCompositeType(CreateCompositeType x)
    {
        var fields = string.Join(", ", x.CompositeType.Fields.Select(f => $"\"{f.Name}\" {ToPostgresType(f.DataType)}"));
        return $"""CREATE TYPE "{x.SchemaName}"."{x.CompositeType.Name}" AS ({fields})""";
    }

    // CREATE DOMAIN name AS type [DEFAULT expr] [NOT NULL] [CONSTRAINT n CHECK (expr)]…
    private static string BuildCreateDomain(string schema, Domain domain)
    {
        var sb = new StringBuilder($"""CREATE DOMAIN "{schema}"."{domain.Name}" AS {ToPostgresType(domain.DataType)}""");
        if (domain.Default is { } def)
        {
            sb.Append($" DEFAULT {def}");
        }
        if (domain.NotNull)
        {
            sb.Append(" NOT NULL");
        }
        foreach (var check in domain.Checks)
        {
            sb.Append($""" CONSTRAINT "{check.Name}" CHECK ({check.Expression})""");
        }
        return sb.ToString();
    }

    // A domain's base type cannot be altered in place (Postgres has no ALTER DOMAIN … TYPE), so a base-type change
    // drops + recreates — re-issuing the comment the drop discarded. Fails loudly if a column still uses the domain.
    private static IEnumerable<SqlStatement> BuildRecreateDomain(RecreateDomain x)
    {
        yield return new SqlStatement($"DROP DOMAIN \"{x.SchemaName}\".\"{x.Domain.Name}\"");
        yield return new SqlStatement(BuildCreateDomain(x.SchemaName, x.Domain));
        if (x.Domain.Comment is { } comment)
        {
            yield return new SqlStatement($"""COMMENT ON DOMAIN "{x.SchemaName}"."{x.Domain.Name}" IS $comment${comment}$comment$""");
        }
    }

    // CREATE TRIGGER name {BEFORE|AFTER|INSTEAD OF} {event [OR …]} ON s.t FOR EACH {ROW|STATEMENT}
    //   [WHEN (cond)] EXECUTE FUNCTION fn(args)
    private static string BuildCreateTrigger(CreateTrigger x)
    {
        var t = x.Trigger;
        var sb = new StringBuilder(
            $"""CREATE TRIGGER "{t.Name}" {TriggerTimingText(t.Timing)} {TriggerEventsText(t)} ON "{x.SchemaName}"."{x.TableName}" FOR EACH {(t.Level == TriggerLevel.Row ? "ROW" : "STATEMENT")}""");
        if (t.When is { } when)
        {
            sb.Append($" WHEN ({when})");
        }
        sb.Append($" EXECUTE FUNCTION {t.Function}({t.FunctionArguments ?? string.Empty})");
        return sb.ToString();
    }

    private static string TriggerTimingText(TriggerTiming timing) => timing switch
    {
        TriggerTiming.Before => "BEFORE",
        TriggerTiming.After => "AFTER",
        TriggerTiming.InsteadOf => "INSTEAD OF",
        _ => throw new ArgumentOutOfRangeException(nameof(timing), timing, "Unknown trigger timing."),
    };

    private static string TriggerEventsText(Trigger trigger)
    {
        var parts = new List<string>(4);
        if (trigger.Events.HasFlag(TriggerEvent.Insert))
        {
            parts.Add("INSERT");
        }
        if (trigger.Events.HasFlag(TriggerEvent.Update))
        {
            parts.Add(trigger.UpdateOfColumns.Count > 0 ? $"UPDATE OF {ColList(trigger.UpdateOfColumns)}" : "UPDATE");
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

    private static string BuildCreateExtension(CreateExtension x)
    {
        var sql = $"CREATE EXTENSION IF NOT EXISTS \"{x.Extension.Name}\"";
        return x.Extension.Version is { } version ? $"{sql} VERSION '{EscapeLiteral(version)}'" : sql;
    }

    private static string ViewKind(bool isMaterialized) => isMaterialized ? "MATERIALIZED VIEW" : "VIEW";

    private static string RoutineKeyword(RoutineKind kind) => kind == RoutineKind.Procedure ? "PROCEDURE" : "FUNCTION";

    private static long DefaultStart(SequenceOptions options) =>
        (options.IncrementBy ?? 1) > 0 ? options.MinValue ?? 1 : options.MaxValue ?? -1;

    private static string EscapeLiteral(string value) => value.Replace("'", "''");

    private static string ColList(IReadOnlyList<string> cols) =>
        string.Join(", ", cols.Select(c => $"\"{c}\""));

    private static string PrivilegeList(TablePrivilege privileges)
    {
        var parts = new List<string>();
        if (privileges.HasFlag(TablePrivilege.Select))
        {
            parts.Add("SELECT");
        }

        if (privileges.HasFlag(TablePrivilege.Insert))
        {
            parts.Add("INSERT");
        }

        if (privileges.HasFlag(TablePrivilege.Update))
        {
            parts.Add("UPDATE");
        }

        if (privileges.HasFlag(TablePrivilege.Delete))
        {
            parts.Add("DELETE");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "ALL PRIVILEGES";
    }

    // ── Type mapping ──────────────────────────────────────────────────────────

    private static string ToPostgresType(SqlType type) => type.Name switch
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
        "varchar" => type.Length is { } vn ? $"character varying({vn})" : "character varying",
        "nvarchar" => type.Length is { } nvn ? $"character varying({nvn})" : "character varying",
        "text" => "text",
        "date" => "date",
        "time" => "time",
        "datetime" => "timestamp",
        "datetimeoffset" => "timestamptz",
        "guid" => "uuid",
        "binary" => "bytea",
        "varbinary" => "bytea",
        // Any other name is a database-specific or user-defined type (e.g. citext, jsonb);
        // emit it verbatim.
        _ => type.Name,
    };

    private static string ToReferentialAction(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => "NO ACTION",
    };
}
