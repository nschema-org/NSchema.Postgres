using System.Data.Common;
using Npgsql;
using NpgsqlTypes;
using NSchema.Deployment.Plugins;
using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.CompositeTypes;
using NSchema.Model.Constraints;
using NSchema.Model.Domains;
using NSchema.Model.Enums;
using NSchema.Model.Extensions;
using NSchema.Model.Indexes;
using NSchema.Model.Routines;
using NSchema.Model.Schemas;
using NSchema.Model.Sequences;
using NSchema.Model.Tables;
using NSchema.Model.Triggers;
using NSchema.Model.Types;
using NSchema.Model.Views;
using NSchema.Postgres.Models;

namespace NSchema.Postgres.Sql;

internal sealed class PostgresDatabaseIntrospector(NpgsqlDataSource dataSource) : IDatabaseIntrospector
{
    private const string Source = "postgres";

    /// <inheritdoc />
    public async ValueTask<Result<Database>> GetDatabase(PlanningScope scope, CancellationToken cancellationToken = default)
    {
        // A database that cannot be read is an expected outcome for the caller to report. Only DbException is
        // caught: anything else escaping the read is a defect in this introspector, and the engine treats an
        // escaping exception as one rather than dressing it up as an environmental problem.
        try
        {
            return await Read(scope, cancellationToken);
        }
        catch (DbException exception)
        {
            return Result.Failure<Database>(Diagnostic.Error(Source, "database-unreadable",
                $"Could not read the live database: {ExceptionMessage.Describe(exception):text}"));
        }
    }

    private async ValueTask<Database> Read(PlanningScope scope, CancellationToken cancellationToken)
    {
        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken);

        // The scope is a hint; null means "all visible schemas" and the engine re-applies the scope after the read.
        // Every address belongs to a schema (a schema address is its own), so the read narrows to those.
        var schemas = scope.IsUnscoped
            ? null
            : scope.Addresses.Select(SchemaOf).OfType<SqlIdentifier>().Distinct().Select(s => s.Value).ToArray();

        var tables = await QueryTables(conn, schemas, cancellationToken);
        var columns = await QueryColumns(conn, schemas, cancellationToken);
        var primaryKeys = await QueryPrimaryKeys(conn, schemas, cancellationToken);
        var foreignKeys = await QueryForeignKeys(conn, schemas, cancellationToken);
        var uniqueConstraints = await QueryUniqueConstraints(conn, schemas, cancellationToken);
        var checkConstraints = await QueryCheckConstraints(conn, schemas, cancellationToken);
        var exclusionConstraints = await QueryExclusionConstraints(conn, schemas, cancellationToken);
        var indexes = await QueryIndexes(conn, schemas, cancellationToken);
        var triggers = await QueryTriggers(conn, schemas, cancellationToken);
        var extensions = await QueryExtensions(conn, cancellationToken);
        var schemaComments = await QuerySchemaComments(conn, schemas, cancellationToken);
        var tableComments = await QueryTableComments(conn, schemas, cancellationToken);
        var columnComments = await QueryColumnComments(conn, schemas, cancellationToken);
        var indexComments = await QueryIndexComments(conn, schemas, cancellationToken);
        var constraintComments = await QueryConstraintComments(conn, schemas, cancellationToken);
        var schemaGrants = await QuerySchemaGrants(conn, schemas, cancellationToken);
        var tableGrants = await QueryTableGrants(conn, schemas, cancellationToken);
        var views = await QueryViews(conn, schemas, cancellationToken);
        var viewComments = await QueryViewComments(conn, schemas, cancellationToken);
        var viewDependencies = await QueryViewDependencies(conn, schemas, cancellationToken);
        var enums = await QueryEnums(conn, schemas, cancellationToken);
        var enumComments = await QueryEnumComments(conn, schemas, cancellationToken);
        var sequences = await QuerySequences(conn, schemas, cancellationToken);
        var sequenceComments = await QuerySequenceComments(conn, schemas, cancellationToken);
        var domains = await QueryDomains(conn, schemas, cancellationToken);
        var domainChecks = await QueryDomainChecks(conn, schemas, cancellationToken);
        var compositeTypes = await QueryCompositeTypes(conn, schemas, cancellationToken);
        var compositeFields = await QueryCompositeFields(conn, schemas, cancellationToken);
        var nativeTypes = await QueryNativeTypes(conn, schemas, cancellationToken);
        var functions = await QueryRoutines(conn, schemas, FunctionKind, cancellationToken);
        var functionComments = await QueryRoutineComments(conn, schemas, FunctionKind, cancellationToken);
        var procedures = await QueryRoutines(conn, schemas, ProcedureKind, cancellationToken);
        var procedureComments = await QueryRoutineComments(conn, schemas, ProcedureKind, cancellationToken);
        var aggregates = await QueryAggregates(conn, schemas, cancellationToken);
        var aggregateComments = await QueryRoutineComments(conn, schemas, AggregateKind, cancellationToken);

        return Build(
            tables, columns, primaryKeys, foreignKeys, uniqueConstraints, checkConstraints, exclusionConstraints, indexes, triggers,
            schemaComments, tableComments, columnComments, indexComments, constraintComments,
            schemaGrants, tableGrants, views, viewComments, viewDependencies,
            enums, enumComments, sequences, sequenceComments,
            domains, domainChecks, compositeTypes, compositeFields, nativeTypes,
            functions, functionComments, procedures, procedureComments, aggregates, aggregateComments,
            extensions
        );
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    private static void AddSchemasParameter(NpgsqlCommand cmd, string[]? schemas)
    {
        var parameter = cmd.Parameters.Add("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text);
        parameter.Value = (object?)schemas ?? DBNull.Value;
    }

    private static async Task<List<TableRow>> QueryTables(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<TableRow>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT table_schema, table_name
            FROM information_schema.tables
            WHERE table_type = 'BASE TABLE'
            AND (@schemas::text[] IS NULL OR table_schema = ANY(@schemas))
            AND table_schema NOT IN ('pg_catalog', 'information_schema')
            AND table_schema NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND table_schema NOT LIKE 'pg\_temp%' ESCAPE '\'
            ORDER BY table_schema, table_name
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TableRow(reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    private static async Task<List<ColumnRow>> QueryColumns(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<ColumnRow>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                c.table_schema,
                c.table_name,
                c.column_name,
                c.data_type,
                c.udt_name,
                c.domain_schema,
                c.domain_name,
                c.character_maximum_length,
                c.numeric_precision,
                c.numeric_scale,
                c.is_nullable,
                c.column_default,
                c.is_identity,
                seq.seqstart   AS identity_start,
                seq.seqmin     AS identity_min_value,
                seq.seqincrement AS identity_increment,
                CASE WHEN c.is_generated = 'ALWAYS' THEN c.generation_expression END AS generation_expression,
                c.udt_schema
            FROM information_schema.columns c
            LEFT JOIN pg_namespace    n  ON n.nspname   = c.table_schema
            LEFT JOIN pg_class        t  ON t.relname   = c.table_name
                                        AND t.relnamespace = n.oid
                                        AND t.relkind   = 'r'
            LEFT JOIN pg_attribute    a  ON a.attrelid  = t.oid
                                        AND a.attname   = c.column_name
            LEFT JOIN pg_depend       d  ON d.refobjid  = t.oid
                                        AND d.refobjsubid = a.attnum
                                        AND d.deptype   = 'i'
            LEFT JOIN pg_class        sc ON sc.oid       = d.objid
                                        AND sc.relkind  = 'S'
            LEFT JOIN pg_sequence     seq ON seq.seqrelid = sc.oid
            WHERE (@schemas::text[] IS NULL OR c.table_schema = ANY(@schemas))
            AND c.table_schema NOT IN ('pg_catalog', 'information_schema')
            AND c.table_schema NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND c.table_schema NOT LIKE 'pg\_temp%' ESCAPE '\'
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ColumnRow(
                TableSchema: reader.GetString(0),
                TableName: reader.GetString(1),
                ColumnName: reader.GetString(2),
                DataType: reader.GetString(3),
                UdtName: reader.GetString(4),
                UdtSchema: reader.IsDBNull(17) ? null : reader.GetString(17),
                DomainSchema: reader.IsDBNull(5) ? null : reader.GetString(5),
                DomainName: reader.IsDBNull(6) ? null : reader.GetString(6),
                MaxLength: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                NumericPrecision: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                NumericScale: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                IsNullable: reader.GetString(10) == "YES",
                DefaultExpression: reader.IsDBNull(11) ? null : reader.GetString(11),
                IsIdentity: reader.GetString(12) == "YES",
                IdentityStart: reader.IsDBNull(13) ? null : reader.GetInt64(13),
                IdentityMinValue: reader.IsDBNull(14) ? null : reader.GetInt64(14),
                IdentityIncrement: reader.IsDBNull(15) ? null : reader.GetInt64(15),
                GeneratedExpression: reader.IsDBNull(16) ? null : reader.GetString(16)
            ));
        }

        return rows;
    }

    private static async Task<List<PrimaryKeyRow>> QueryPrimaryKeys(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<PrimaryKeyRow>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                tc.table_schema,
                tc.table_name,
                tc.constraint_name,
                kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                ON  tc.constraint_name = kcu.constraint_name
                AND tc.table_schema    = kcu.table_schema
                AND tc.table_name      = kcu.table_name
            WHERE tc.constraint_type = 'PRIMARY KEY'
            AND (@schemas::text[] IS NULL OR tc.table_schema = ANY(@schemas))
            AND tc.table_schema NOT IN ('pg_catalog', 'information_schema')
            AND tc.table_schema NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND tc.table_schema NOT LIKE 'pg\_temp%' ESCAPE '\'
            ORDER BY tc.table_schema, tc.table_name, kcu.ordinal_position
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PrimaryKeyRow(
                TableSchema: reader.GetString(0),
                TableName: reader.GetString(1),
                ConstraintName: reader.GetString(2),
                ColumnName: reader.GetString(3)
            ));
        }

        return rows;
    }

    private static async Task<List<ForeignKeyRow>> QueryForeignKeys(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<ForeignKeyRow>();
        await using var cmd = conn.CreateCommand();
        // information_schema doesn't preserve FK column ordering, so use pg_catalog.
        // confupdtype / confdeltype are internal "char" — cast to text for reliable ADO reading.
        cmd.CommandText = """
            SELECT
                n.nspname  AS table_schema,
                t.relname  AS table_name,
                c.conname  AS constraint_name,
                array_agg(a.attname  ORDER BY array_position(c.conkey,  a.attnum))  AS column_names,
                fn.nspname AS foreign_schema,
                ft.relname AS foreign_table,
                array_agg(fa.attname ORDER BY array_position(c.confkey, fa.attnum)) AS foreign_column_names,
                c.confupdtype::text AS update_rule,
                c.confdeltype::text AS delete_rule
            FROM pg_constraint c
            JOIN pg_class     t  ON t.oid  = c.conrelid
            JOIN pg_namespace n  ON n.oid  = t.relnamespace
            JOIN pg_class     ft ON ft.oid = c.confrelid
            JOIN pg_namespace fn ON fn.oid = ft.relnamespace
            JOIN pg_attribute a  ON a.attrelid = t.oid  AND a.attnum = ANY(c.conkey)
            JOIN pg_attribute fa ON fa.attrelid = ft.oid AND fa.attnum = ANY(c.confkey)
            WHERE c.contype = 'f'
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            GROUP BY n.nspname, t.relname, c.conname, fn.nspname, ft.relname, c.confupdtype, c.confdeltype
            ORDER BY n.nspname, t.relname, c.conname
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ForeignKeyRow(
                TableSchema: reader.GetString(0),
                TableName: reader.GetString(1),
                ConstraintName: reader.GetString(2),
                ColumnNames: reader.GetFieldValue<string[]>(3),
                ForeignSchema: reader.GetString(4),
                ForeignTable: reader.GetString(5),
                ForeignColumnNames: reader.GetFieldValue<string[]>(6),
                UpdateRule: reader.GetString(7)[0],
                DeleteRule: reader.GetString(8)[0]
            ));
        }

        return rows;
    }

    private static async Task<List<UniqueConstraintRow>> QueryUniqueConstraints(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<UniqueConstraintRow>();
        await using var cmd = conn.CreateCommand();
        // Unique constraints (contype = 'u') only — not unique indexes, which are introspected separately as
        // indexes. Column ordering comes from conkey.
        cmd.CommandText = """
            SELECT
                n.nspname AS table_schema,
                t.relname AS table_name,
                c.conname AS constraint_name,
                array_agg(a.attname ORDER BY array_position(c.conkey, a.attnum)) AS column_names
            FROM pg_constraint c
            JOIN pg_class     t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(c.conkey)
            WHERE c.contype = 'u'
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            GROUP BY n.nspname, t.relname, c.conname
            ORDER BY n.nspname, t.relname, c.conname
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new UniqueConstraintRow(
                TableSchema: reader.GetString(0),
                TableName: reader.GetString(1),
                ConstraintName: reader.GetString(2),
                ColumnNames: reader.GetFieldValue<string[]>(3)
            ));
        }

        return rows;
    }

    private static async Task<List<CheckConstraintRow>> QueryCheckConstraints(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<CheckConstraintRow>();
        await using var cmd = conn.CreateCommand();
        // Check constraints (contype = 'c'). pg_get_constraintdef returns "CHECK (expr)" — strip the wrapper so the
        // stored expression matches what the author wrote. Exclude NOT NULL (those surface as column nullability).
        cmd.CommandText = """
            SELECT
                n.nspname AS table_schema,
                t.relname AS table_name,
                c.conname AS constraint_name,
                pg_get_constraintdef(c.oid) AS definition
            FROM pg_constraint c
            JOIN pg_class     t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE c.contype = 'c'
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            ORDER BY n.nspname, t.relname, c.conname
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new CheckConstraintRow(
                TableSchema: reader.GetString(0),
                TableName: reader.GetString(1),
                ConstraintName: reader.GetString(2),
                Expression: StripCheckWrapper(reader.GetString(3))
            ));
        }

        return rows;
    }

    // pg_get_constraintdef renders a check as "CHECK (expr)". Postgres also re-parenthesises the predicate, so a
    // declared "balance >= 0" comes back as "CHECK ((balance >= 0))". Strip the "CHECK (...)" wrapper and then one
    // layer of fully-enclosing parentheses so the stored expression matches what the author wrote.
    private static string StripCheckWrapper(string definition)
    {
        const string prefix = "CHECK (";
        var start = definition.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return definition.Trim();
        }

        var inner = definition[(start + prefix.Length)..];
        var end = inner.LastIndexOf(')');
        var expression = (end >= 0 ? inner[..end] : inner).Trim();
        return StripEnclosingParens(expression);
    }

    // Removes a single pair of parentheses that wraps the whole expression (Postgres adds these around check
    // predicates). Leaves inner grouping parentheses intact, and is a no-op when the outermost "(" does not match
    // the final ")" — e.g. "(a > 0) AND (b > 0)".
    private static string StripEnclosingParens(string expression)
    {
        if (expression.Length < 2 || expression[0] != '(' || expression[^1] != ')')
        {
            return expression;
        }

        var depth = 0;
        for (var i = 0; i < expression.Length; i++)
        {
            depth += expression[i] switch { '(' => 1, ')' => -1, _ => 0 };
            // If we return to depth 0 before the final character, the leading "(" closes early, so it does not wrap
            // the whole expression.
            if (depth == 0 && i < expression.Length - 1)
            {
                return expression;
            }
        }

        return expression[1..^1].Trim();
    }

    private static async Task<List<ExclusionConstraintRow>> QueryExclusionConstraints(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<ExclusionConstraintRow>();
        await using var cmd = conn.CreateCommand();
        // Exclusion constraints (contype = 'x'). Each is backed by an index (conindid): the index supplies the
        // access method and the per-element column/expression text (read the same way as a plain index), while
        // conexclop supplies the parallel operator for each element. The conexclop unnest joins on position, so it
        // naturally restricts to the key elements (any INCLUDE columns have no operator and drop out). The access
        // method is btree-folded to null so an EXCLUDE without USING round-trips clean.
        cmd.CommandText = """
            SELECT
                n.nspname AS table_schema,
                t.relname AS table_name,
                c.conname AS constraint_name,
                NULLIF(am.amname, 'btree') AS method,
                pg_get_expr(ix.indpred, ix.indrelid) AS predicate,
                array_agg(CASE WHEN k.attnum = 0 THEN pg_get_indexdef(c.conindid, k.ordinality::int, true) ELSE a.attname END ORDER BY k.ordinality) AS element_texts,
                array_agg(k.attnum = 0 ORDER BY k.ordinality) AS is_expressions,
                array_agg(op.oprname ORDER BY k.ordinality) AS operators
            FROM pg_constraint c
            JOIN pg_class     t  ON t.oid = c.conrelid
            JOIN pg_namespace n  ON n.oid = t.relnamespace
            JOIN pg_index     ix ON ix.indexrelid = c.conindid
            JOIN pg_class     i  ON i.oid = c.conindid
            JOIN pg_am        am ON am.oid = i.relam
            JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ordinality) ON TRUE
            LEFT JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            JOIN LATERAL unnest(c.conexclop) WITH ORDINALITY AS e(opoid, eord) ON e.eord = k.ordinality
            JOIN pg_operator op ON op.oid = e.opoid
            WHERE c.contype = 'x'
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            GROUP BY n.nspname, t.relname, c.conname, am.amname, ix.indpred, ix.indrelid, c.conindid
            ORDER BY n.nspname, t.relname, c.conname
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ExclusionConstraintRow(
                TableSchema: reader.GetString(0),
                TableName: reader.GetString(1),
                ConstraintName: reader.GetString(2),
                Method: reader.IsDBNull(3) ? null : reader.GetString(3),
                Predicate: reader.IsDBNull(4) ? null : reader.GetString(4),
                ElementTexts: reader.GetFieldValue<string[]>(5),
                IsExpressions: reader.GetFieldValue<bool[]>(6),
                Operators: reader.GetFieldValue<string[]>(7)
            ));
        }

        return rows;
    }

    private static async Task<List<DomainRow>> QueryDomains(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<DomainRow>();
        await using var cmd = conn.CreateCommand();
        // The base type (and its length/precision/scale) come from information_schema.domains — the same shape a
        // column reports — so MapSqlType can be reused. NOT NULL is not exposed there, so it is read from
        // pg_type.typnotnull; the comment is on the domain's type entry.
        cmd.CommandText = """
            SELECT
                d.domain_schema,
                d.domain_name,
                d.data_type,
                d.udt_name,
                d.character_maximum_length,
                d.numeric_precision,
                d.numeric_scale,
                t.typnotnull AS not_null,
                d.domain_default,
                obj_description(t.oid, 'pg_type') AS comment
            FROM information_schema.domains d
            JOIN pg_namespace n ON n.nspname = d.domain_schema
            JOIN pg_type      t ON t.typname = d.domain_name AND t.typnamespace = n.oid AND t.typtype = 'd'
            WHERE (@schemas::text[] IS NULL OR d.domain_schema = ANY(@schemas))
            AND d.domain_schema NOT IN ('pg_catalog', 'information_schema')
            AND d.domain_schema NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND d.domain_schema NOT LIKE 'pg\_temp%' ESCAPE '\'
            ORDER BY d.domain_schema, d.domain_name
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DomainRow(
                Schema: reader.GetString(0),
                Name: reader.GetString(1),
                DataType: reader.GetString(2),
                UdtName: reader.GetString(3),
                MaxLength: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Precision: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Scale: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                NotNull: reader.GetBoolean(7),
                Default: reader.IsDBNull(8) ? null : reader.GetString(8),
                Comment: reader.IsDBNull(9) ? null : reader.GetString(9)
            ));
        }

        return rows;
    }

    private static async Task<List<DomainCheckRow>> QueryDomainChecks(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<DomainCheckRow>();
        await using var cmd = conn.CreateCommand();
        // Domain check constraints are pg_constraint rows attached to the type (contypid), not a table (conrelid).
        // pg_get_constraintdef renders "CHECK (expr)" — strip the wrapper, as for table checks.
        cmd.CommandText = """
            SELECT
                n.nspname AS schema_name,
                t.typname AS domain_name,
                c.conname AS check_name,
                pg_get_constraintdef(c.oid) AS definition
            FROM pg_constraint c
            JOIN pg_type      t ON t.oid = c.contypid
            JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE c.contype = 'c' AND c.contypid <> 0
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            ORDER BY n.nspname, t.typname, c.conname
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DomainCheckRow(
                Schema: reader.GetString(0),
                DomainName: reader.GetString(1),
                CheckName: reader.GetString(2),
                Expression: StripCheckWrapper(reader.GetString(3))
            ));
        }

        return rows;
    }

    private static async Task<List<CompositeTypeRow>> QueryCompositeTypes(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<CompositeTypeRow>();
        await using var cmd = conn.CreateCommand();
        // Standalone composite types only: every table/view/sequence also has a composite type in pg_type (its row
        // type), so the backing pg_class is joined and filtered to relkind 'c' — a free-standing CREATE TYPE — to
        // exclude those relation row types.
        cmd.CommandText = """
            SELECT n.nspname AS schema_name,
                   t.typname AS type_name,
                   obj_description(t.oid, 'pg_type') AS comment
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_class     c ON c.oid = t.typrelid
            WHERE t.typtype = 'c' AND c.relkind = 'c'
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            ORDER BY n.nspname, t.typname
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new CompositeTypeRow(
                Schema: reader.GetString(0),
                Name: reader.GetString(1),
                Comment: reader.IsDBNull(2) ? null : reader.GetString(2)
            ));
        }

        return rows;
    }

    private static async Task<List<CompositeFieldRow>> QueryCompositeFields(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<CompositeFieldRow>();
        await using var cmd = conn.CreateCommand();
        // information_schema.attributes lists composite-type fields in the same shape a column reports, so
        // MapSqlType can be reused for the field type.
        cmd.CommandText = """
            SELECT
                a.udt_schema,
                a.udt_name,
                a.attribute_name,
                a.ordinal_position,
                a.data_type,
                a.attribute_udt_name,
                a.character_maximum_length,
                a.numeric_precision,
                a.numeric_scale
            FROM information_schema.attributes a
            WHERE (@schemas::text[] IS NULL OR a.udt_schema = ANY(@schemas))
            AND a.udt_schema NOT IN ('pg_catalog', 'information_schema')
            AND a.udt_schema NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND a.udt_schema NOT LIKE 'pg\_temp%' ESCAPE '\'
            ORDER BY a.udt_schema, a.udt_name, a.ordinal_position
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new CompositeFieldRow(
                Schema: reader.GetString(0),
                TypeName: reader.GetString(1),
                FieldName: reader.GetString(2),
                OrdinalPosition: reader.GetInt32(3),
                DataType: reader.GetString(4),
                UdtName: reader.GetString(5),
                MaxLength: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                Precision: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                Scale: reader.IsDBNull(8) ? null : reader.GetInt32(8)
            ));
        }

        return rows;
    }

    private static async Task<List<NativeTypeRow>> QueryNativeTypes(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<NativeTypeRow>();
        await using var cmd = conn.CreateCommand();
        // The engine's type vocabulary: base, range, and multirange types the engine (pg_catalog) or an
        // installed extension provides. Pseudo types are excluded — nothing a column can be typed with —
        // as are rowtypes, domains, enums, and composites, which introspect as their own kinds. An array
        // type carries no extension dependency of its own, so its element's (typelem) counts for it.
        cmd.CommandText = """
            SELECT DISTINCT n.nspname AS schema_name,
                   t.typname AS type_name,
                   e.extname AS extension_name
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            LEFT JOIN pg_depend d ON d.classid = 'pg_type'::regclass
                                 AND d.objid IN (t.oid, t.typelem)
                                 AND d.refclassid = 'pg_extension'::regclass
                                 AND d.deptype = 'e'
            LEFT JOIN pg_extension e ON e.oid = d.refobjid
            WHERE t.typtype IN ('b', 'r', 'm')
            AND (n.nspname = 'pg_catalog' OR e.extname IS NOT NULL)
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            ORDER BY n.nspname, t.typname
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new NativeTypeRow(
                Schema: reader.GetString(0),
                Name: reader.GetString(1),
                Extension: reader.IsDBNull(2) ? null : reader.GetString(2)
            ));
        }

        return rows;
    }

    private static async Task<List<ExtensionRow>> QueryExtensions(NpgsqlConnection conn, CancellationToken ct)
    {
        var rows = new List<ExtensionRow>();
        await using var cmd = conn.CreateCommand();
        // Extensions are database-global (not schema-scoped), so they are not filtered by the schema list. plpgsql
        // is the always-installed procedural language and never part of a declared schema, so it is excluded.
        cmd.CommandText = """
            SELECT e.extname AS name,
                   e.extversion AS version,
                   obj_description(e.oid, 'pg_extension') AS comment
            FROM pg_extension e
            WHERE e.extname <> 'plpgsql'
            ORDER BY e.extname
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ExtensionRow(
                Name: reader.GetString(0),
                Version: reader.IsDBNull(1) ? null : reader.GetString(1),
                Comment: reader.IsDBNull(2) ? null : reader.GetString(2)
            ));
        }

        return rows;
    }

    private static async Task<List<TriggerRow>> QueryTriggers(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<TriggerRow>();
        await using var cmd = conn.CreateCommand();
        // User triggers only — NOT tgisinternal excludes the system triggers that enforce foreign keys and other
        // constraints. tgtype is a bitmask (timing/level/events) decoded when mapped; the UPDATE OF column set comes
        // from tgattr. The WHEN condition and the function arguments are both pulled out of pg_get_triggerdef (its
        // canonical form): pg_get_expr cannot render a WHEN that references both OLD and NEW, and there is no clean
        // catalog column for the decoded arguments.
        cmd.CommandText = """
            SELECT
                n.nspname AS table_schema,
                c.relname AS table_name,
                t.tgname  AS trigger_name,
                t.tgtype  AS tg_type,
                fn.nspname AS function_schema,
                p.proname  AS function_name,
                substring(td.def FROM 'WHEN \((.*)\) EXECUTE (?:FUNCTION|PROCEDURE)') AS when_expr,
                COALESCE(
                    (SELECT array_agg(a.attname ORDER BY k.ord)
                     FROM unnest(string_to_array(NULLIF(t.tgattr::text, ''), ' ')::int[]) WITH ORDINALITY AS k(attnum, ord)
                     JOIN pg_attribute a ON a.attrelid = t.tgrelid AND a.attnum = k.attnum),
                    ARRAY[]::text[]) AS update_of_columns,
                substring(td.def FROM 'EXECUTE (?:FUNCTION|PROCEDURE) [^(]+\((.*)\)$') AS function_args,
                obj_description(t.oid, 'pg_trigger') AS comment
            FROM pg_trigger   t
            JOIN pg_class     c  ON c.oid = t.tgrelid
            JOIN pg_namespace n  ON n.oid = c.relnamespace
            JOIN pg_proc      p  ON p.oid = t.tgfoid
            JOIN pg_namespace fn ON fn.oid = p.pronamespace
            CROSS JOIN LATERAL (SELECT pg_get_triggerdef(t.oid) AS def) td
            WHERE NOT t.tgisinternal
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            ORDER BY n.nspname, c.relname, t.tgname
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TriggerRow(
                TableSchema: reader.GetString(0),
                TableName: reader.GetString(1),
                Name: reader.GetString(2),
                TgType: reader.GetInt16(3),
                FunctionSchema: reader.GetString(4),
                FunctionName: reader.GetString(5),
                When: reader.IsDBNull(6) ? null : StripEnclosingParens(reader.GetString(6)),
                UpdateOfColumns: reader.GetFieldValue<string[]>(7),
                FunctionArguments: reader.IsDBNull(8) || reader.GetString(8).Length == 0 ? null : reader.GetString(8),
                Comment: reader.IsDBNull(9) ? null : reader.GetString(9)
            ));
        }

        return rows;
    }

    private static async Task<Dictionary<(string, string, string), string?>> QueryConstraintComments(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var result = new Dictionary<(string, string, string), string?>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n.nspname, t.relname, c.conname, d.description
            FROM pg_constraint c
            JOIN pg_class     t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            LEFT JOIN pg_description d
                ON d.objoid = c.oid
                AND d.classoid = 'pg_constraint'::regclass
                AND d.objsubid = 0
            WHERE (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            ORDER BY n.nspname, t.relname, c.conname
            """;
        AddSchemasParameter(cmd, schemas);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[(reader.GetString(0), reader.GetString(1), reader.GetString(2))] = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        return result;
    }

    private static async Task<List<IndexRow>> QueryIndexes(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<IndexRow>();
        await using var cmd = conn.CreateCommand();
        // Exclude primary-key indexes and any index that backs a constraint (a UNIQUE constraint's implicit index
        // surfaces as a UniqueConstraint, not a TableIndex). Standalone CREATE UNIQUE INDEXes have no backing
        // constraint, so they are still returned.
        //
        // Columns are read positionally (indkey order, all indnatts of them — keys then INCLUDE): a plain column
        // is its attname; an expression key (attnum = 0) is its pg_get_indexdef text. indnkeyatts marks the
        // key/INCLUDE split, indoption carries per-key ASC/DESC + NULLS bits, and the access method is btree-folded
        // to null (the default) so a plain index round-trips clean.
        cmd.CommandText = """
            SELECT
                n.nspname  AS schema_name,
                t.relname  AS table_name,
                i.relname  AS index_name,
                ix.indisunique AS is_unique,
                NULLIF(am.amname, 'btree') AS method,
                ix.indnkeyatts AS num_key_atts,
                pg_get_expr(ix.indpred, ix.indrelid) AS predicate,
                array_agg(CASE WHEN k.attnum = 0 THEN pg_get_indexdef(ix.indexrelid, k.ordinality::int, true) ELSE a.attname END ORDER BY k.ordinality) AS column_texts,
                array_agg(k.attnum = 0 ORDER BY k.ordinality) AS is_expressions,
                array_agg(COALESCE((string_to_array(ix.indoption::text, ' '))[k.ordinality]::int, 0) ORDER BY k.ordinality) AS options
            FROM pg_index ix
            JOIN pg_class     t ON t.oid = ix.indrelid
            JOIN pg_class     i ON i.oid = ix.indexrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_am        am ON am.oid = i.relam
            JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ordinality) ON TRUE
            LEFT JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            WHERE (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            AND NOT ix.indisprimary
            AND NOT EXISTS (SELECT 1 FROM pg_constraint con WHERE con.conindid = i.oid)
            GROUP BY n.nspname, t.relname, i.relname, ix.indisunique, am.amname, ix.indnkeyatts, ix.indpred, ix.indrelid, ix.indexrelid
            ORDER BY n.nspname, t.relname, i.relname
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new IndexRow(
                SchemaName: reader.GetString(0),
                TableName: reader.GetString(1),
                IndexName: reader.GetString(2),
                IsUnique: reader.GetBoolean(3),
                Method: reader.IsDBNull(4) ? null : reader.GetString(4),
                NumKeyAtts: reader.GetInt16(5),
                Predicate: reader.IsDBNull(6) ? null : reader.GetString(6),
                ColumnTexts: reader.GetFieldValue<string[]>(7),
                IsExpressions: reader.GetFieldValue<bool[]>(8),
                Options: reader.GetFieldValue<int[]>(9)
            ));
        }

        return rows;
    }

    private static async Task<Dictionary<string, string?>> QuerySchemaComments(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n.nspname, d.description
            FROM pg_namespace n
            LEFT JOIN pg_description d
                ON d.objoid = n.oid
                AND d.classoid = 'pg_namespace'::regclass
                AND d.objsubid = 0
            WHERE (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            """;
        AddSchemasParameter(cmd, schemas);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        return result;
    }

    private static async Task<Dictionary<(string, string), string?>> QueryTableComments(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), string?>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n.nspname, c.relname, d.description
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_description d
                ON d.objoid = c.oid
                AND d.classoid = 'pg_class'::regclass
                AND d.objsubid = 0
            WHERE (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            AND c.relkind = 'r'
            ORDER BY n.nspname, c.relname
            """;
        AddSchemasParameter(cmd, schemas);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[(reader.GetString(0), reader.GetString(1))] = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        return result;
    }

    private static async Task<Dictionary<(string, string, string), string?>> QueryColumnComments(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var result = new Dictionary<(string, string, string), string?>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n.nspname, c.relname, a.attname, d.description
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            LEFT JOIN pg_description d
                ON d.objoid = c.oid
                AND d.classoid = 'pg_class'::regclass
                AND d.objsubid = a.attnum
            WHERE (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            AND c.relkind = 'r'
            ORDER BY n.nspname, c.relname, a.attnum
            """;
        AddSchemasParameter(cmd, schemas);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[(reader.GetString(0), reader.GetString(1), reader.GetString(2))] = reader.IsDBNull(3) ? null : reader.GetString(3);
        }

        return result;
    }

    private static async Task<Dictionary<(string, string), string?>> QueryIndexComments(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), string?>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n.nspname, i.relname, d.description
            FROM pg_class i
            JOIN pg_index ix ON ix.indexrelid = i.oid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            LEFT JOIN pg_description d
                ON d.objoid = i.oid
                AND d.classoid = 'pg_class'::regclass
                AND d.objsubid = 0
            WHERE (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            AND NOT ix.indisprimary
            ORDER BY n.nspname, i.relname
            """;
        AddSchemasParameter(cmd, schemas);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[(reader.GetString(0), reader.GetString(1))] = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        return result;
    }

    private static async Task<List<SchemaGrantRow>> QuerySchemaGrants(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<SchemaGrantRow>();
        await using var cmd = conn.CreateCommand();
        // The schema owner's implicit self-grant materializes in nspacl as soon as any GRANT runs on the schema.
        // It is excluded (like the table owner in QueryTableGrants) so it doesn't read as drift against a desired
        // schema that never declares the owner's own access. PUBLIC (grantee 0) is excluded too.
        cmd.CommandText = """
            SELECT n.nspname AS schema_name,
                   acl.grantee::regrole::text AS role
            FROM pg_namespace n
            CROSS JOIN LATERAL aclexplode(n.nspacl) AS acl(grantor, grantee, privilege_type, is_grantable)
            WHERE (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            AND acl.privilege_type = 'USAGE'
            AND acl.grantee <> 0
            AND acl.grantee <> n.nspowner
            """;
        AddSchemasParameter(cmd, schemas);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SchemaGrantRow(reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    private static async Task<List<TableGrantRow>> QueryTableGrants(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<TableGrantRow>();
        await using var cmd = conn.CreateCommand();
        // Read from pg_class.relacl (via aclexplode) rather than information_schema.role_table_grants so we can see
        // the grantee's oid and exclude the table owner. The owner holds all privileges implicitly, and
        // information_schema surfaces those as ordinary grants — which would otherwise read as drift against a
        // desired schema that (correctly) never declares the owner's own access. PUBLIC (grantee 0) is excluded too.
        cmd.CommandText = """
            SELECT n.nspname AS table_schema,
                   c.relname AS table_name,
                   acl.grantee::regrole::text AS role,
                   acl.privilege_type
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            CROSS JOIN LATERAL aclexplode(c.relacl) AS acl(grantor, grantee, privilege_type, is_grantable)
            WHERE c.relkind = 'r'
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            AND acl.privilege_type IN ('SELECT', 'INSERT', 'UPDATE', 'DELETE')
            AND acl.grantee <> 0
            AND acl.grantee <> c.relowner
            ORDER BY n.nspname, c.relname, role, acl.privilege_type
            """;
        AddSchemasParameter(cmd, schemas);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TableGrantRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    private static async Task<List<ViewRow>> QueryViews(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<ViewRow>();
        await using var cmd = conn.CreateCommand();
        // relkind 'v' = plain views, 'm' = materialized views (one model, distinguished by the flag). pg_get_viewdef
        // returns the canonical definition for both — this is what makes apply → plan round-trip cleanly: state
        // captures the DB's own form.
        cmd.CommandText = """
            SELECT n.nspname AS schema_name,
                   c.relname AS view_name,
                   pg_get_viewdef(c.oid) AS definition,
                   c.relkind = 'm' AS is_materialized
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('v', 'm')
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            ORDER BY n.nspname, c.relname
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ViewRow(reader.GetString(0), reader.GetString(1), CleanViewBody(reader.GetString(2)), reader.GetBoolean(3)));
        }

        return rows;
    }

    // pg_get_viewdef pretty-prints the SELECT and terminates it with ';'. Strip the wrapping whitespace and trailing
    // terminator so the stored body matches the form the DSL writer emits (it appends its own ';').
    private static string CleanViewBody(string definition)
    {
        var body = definition.Trim();
        return body.EndsWith(';') ? body[..^1].TrimEnd() : body;
    }

    private static async Task<Dictionary<(string, string), string?>> QueryViewComments(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), string?>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n.nspname, c.relname, d.description
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_description d
                ON d.objoid = c.oid
                AND d.classoid = 'pg_class'::regclass
                AND d.objsubid = 0
            WHERE (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            AND c.relkind = 'v'
            ORDER BY n.nspname, c.relname
            """;
        AddSchemasParameter(cmd, schemas);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[(reader.GetString(0), reader.GetString(1))] = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        return result;
    }

    private static async Task<List<ViewDependencyRow>> QueryViewDependencies(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<ViewDependencyRow>();
        await using var cmd = conn.CreateCommand();
        // A view reads other relations through its rewrite rule (pg_rewrite), which depends on those relations via
        // pg_depend. DISTINCT collapses the per-column rows to one per referenced relation; a view depends on its own
        // columns, so the self-reference is excluded. Refs may live in any schema (cross-schema ordering is real).
        cmd.CommandText = """
            SELECT DISTINCT
                vn.nspname AS view_schema,
                v.relname  AS view_name,
                dn.nspname AS ref_schema,
                d.relname  AS ref_name
            FROM pg_rewrite r
            JOIN pg_class     v  ON v.oid  = r.ev_class
            JOIN pg_namespace vn ON vn.oid = v.relnamespace
            JOIN pg_depend    dep ON dep.objid = r.oid
                                 AND dep.classid    = 'pg_rewrite'::regclass
                                 AND dep.refclassid = 'pg_class'::regclass
            JOIN pg_class     d  ON d.oid  = dep.refobjid
            JOIN pg_namespace dn ON dn.oid = d.relnamespace
            WHERE v.relkind IN ('v', 'm')
            AND d.relkind IN ('r', 'v', 'm')
            AND d.oid <> v.oid
            AND (@schemas::text[] IS NULL OR vn.nspname = ANY(@schemas))
            AND vn.nspname NOT IN ('pg_catalog', 'information_schema')
            AND vn.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND vn.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            ORDER BY vn.nspname, v.relname, dn.nspname, d.relname
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ViewDependencyRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return rows;
    }

    private static async Task<List<EnumRow>> QueryEnums(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<EnumRow>();
        await using var cmd = conn.CreateCommand();
        // typtype 'e' = enum types. LEFT JOIN + FILTER so a zero-value enum (legal in Postgres) still surfaces.
        // enumsortorder is the type's comparison order — the model's value order must match it exactly.
        cmd.CommandText = """
            SELECT n.nspname AS schema_name,
                   t.typname AS enum_name,
                   coalesce(array_agg(e.enumlabel::text ORDER BY e.enumsortorder)
                            FILTER (WHERE e.enumlabel IS NOT NULL), '{}') AS values
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            LEFT JOIN pg_enum e ON e.enumtypid = t.oid
            WHERE t.typtype = 'e'
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            GROUP BY n.nspname, t.typname
            ORDER BY n.nspname, t.typname
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new EnumRow(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<string[]>(2)));
        }

        return rows;
    }

    private static async Task<Dictionary<(string, string), string?>> QueryEnumComments(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), string?>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n.nspname, t.typname, d.description
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            LEFT JOIN pg_description d
                ON d.objoid = t.oid
                AND d.classoid = 'pg_type'::regclass
                AND d.objsubid = 0
            WHERE t.typtype = 'e'
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            ORDER BY n.nspname, t.typname
            """;
        AddSchemasParameter(cmd, schemas);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[(reader.GetString(0), reader.GetString(1))] = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        return result;
    }

    private static async Task<List<SequenceRow>> QuerySequences(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<SequenceRow>();
        await using var cmd = conn.CreateCommand();
        // Only standalone sequences belong to the schema model. Owned sequences are a column's implementation
        // detail and are excluded via pg_depend: deptype 'i' (identity) and 'a' (serial — and, by the same token,
        // a user sequence later attached via ALTER SEQUENCE … OWNED BY, which transfers its lifecycle to the column).
        cmd.CommandText = """
            SELECT n.nspname AS schema_name,
                   c.relname AS sequence_name,
                   format_type(s.seqtypid, NULL) AS data_type,
                   s.seqstart,
                   s.seqincrement,
                   s.seqmin,
                   s.seqmax,
                   s.seqcache,
                   s.seqcycle
            FROM pg_sequence s
            JOIN pg_class c ON c.oid = s.seqrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            AND NOT EXISTS (
                SELECT 1
                FROM pg_depend dep
                WHERE dep.classid = 'pg_class'::regclass
                AND dep.objid = c.oid
                AND dep.refclassid = 'pg_class'::regclass
                AND dep.deptype IN ('a', 'i')
            )
            ORDER BY n.nspname, c.relname
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SequenceRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
                reader.GetBoolean(8)));
        }

        return rows;
    }

    private static async Task<Dictionary<(string, string), string?>> QuerySequenceComments(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), string?>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n.nspname, c.relname, d.description
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_description d
                ON d.objoid = c.oid
                AND d.classoid = 'pg_class'::regclass
                AND d.objsubid = 0
            WHERE (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            AND c.relkind = 'S'
            ORDER BY n.nspname, c.relname
            """;
        AddSchemasParameter(cmd, schemas);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[(reader.GetString(0), reader.GetString(1))] = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        return result;
    }

    // pg_proc.prokind discriminators — 'w' (window) is not part of the model.
    private const char FunctionKind = 'f';
    private const char ProcedureKind = 'p';
    private const char AggregateKind = 'a';

    private static async Task<List<RoutineRow>> QueryAggregates(NpgsqlConnection conn, string[]? schemas, CancellationToken ct)
    {
        var rows = new List<RoutineRow>();
        await using var cmd = conn.CreateCommand();
        // pg_get_functiondef refuses aggregates, so the definition is reconstructed from pg_aggregate as the
        // canonical option tuple, non-default options only, so an import is stable and format-canonical.
        // regproc renders a function qualified only when it is off the search path, as a dump spells it.
        cmd.CommandText = """
            SELECT n.nspname AS schema_name,
                   p.proname AS aggregate_name,
                   pg_get_function_arguments(p.oid) AS arguments,
                   a.aggtransfn::regproc::text AS sfunc,
                   format_type(a.aggtranstype, NULL) AS stype,
                   a.agginitval AS initcond,
                   CASE WHEN a.aggfinalfn <> 0 THEN a.aggfinalfn::regproc::text END AS finalfunc
            FROM pg_proc p
            JOIN pg_aggregate a ON a.aggfnoid = p.oid
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE p.prokind = 'a'
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            AND NOT EXISTS (
                SELECT 1
                FROM pg_depend dep
                WHERE dep.classid = 'pg_proc'::regclass
                AND dep.objid = p.oid
                AND dep.deptype = 'e'
            )
            ORDER BY n.nspname, p.proname
            """;
        AddSchemasParameter(cmd, schemas);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var options = new List<string>
            {
                $"SFUNC = {reader.GetString(3)}",
                $"STYPE = {reader.GetString(4)}",
            };
            if (!reader.IsDBNull(5))
            {
                options.Add($"INITCOND = '{reader.GetString(5).Replace("'", "''")}'");
            }
            if (!reader.IsDBNull(6))
            {
                options.Add($"FINALFUNC = {reader.GetString(6)}");
            }

            rows.Add(new RoutineRow(
                Schema: reader.GetString(0),
                Name: reader.GetString(1),
                Arguments: reader.GetString(2),
                Definition: $"({string.Join(", ", options)})"
            ));
        }

        return rows;
    }

    private static async Task<List<RoutineRow>> QueryRoutines(NpgsqlConnection conn, string[]? schemas, char kind, CancellationToken ct)
    {
        var rows = new List<RoutineRow>();
        await using var cmd = conn.CreateCommand();
        // pg_get_functiondef is the DB's canonical form (same anti-phantom-drift approach as views): its header is
        // built as CREATE OR REPLACE <kind> <quoted schema>.<quoted name>(<pg_get_function_arguments>), so stripping
        // a prefix of exactly that shape leaves the model's verbatim Definition — splitting on a parenthesis would
        // break on argument defaults that themselves contain parentheses. Extension-owned routines are an extension's
        // implementation detail (e.g. everything citext installs), excluded via pg_depend deptype 'e'.
        cmd.CommandText = """
            SELECT n.nspname AS schema_name,
                   p.proname AS routine_name,
                   pg_get_function_arguments(p.oid) AS arguments,
                   substr(pg_get_functiondef(p.oid),
                          length(format('CREATE OR REPLACE %s %s.%s(%s)',
                                        CASE WHEN p.prokind = 'p' THEN 'PROCEDURE' ELSE 'FUNCTION' END,
                                        quote_ident(n.nspname), quote_ident(p.proname),
                                        pg_get_function_arguments(p.oid))) + 1) AS definition
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE p.prokind = @kind::"char"
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            AND NOT EXISTS (
                SELECT 1
                FROM pg_depend dep
                WHERE dep.classid = 'pg_proc'::regclass
                AND dep.objid = p.oid
                AND dep.deptype = 'e'
            )
            ORDER BY n.nspname, p.proname
            """;
        AddSchemasParameter(cmd, schemas);
        cmd.Parameters.AddWithValue("kind", kind.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var arguments = reader.GetString(2);
            rows.Add(new RoutineRow(
                reader.GetString(0), reader.GetString(1),
                kind == ProcedureKind ? NormalizeProcedureArguments(arguments) : arguments,
                reader.GetString(3).Trim()));
        }

        return rows;
    }

    // Postgres spells out the default IN mode when rendering a *procedure's* argument list ("IN a integer") —
    // for functions it only prints non-default modes. Fold the explicit default away so the idiomatic declaration
    // ("a integer") compares clean; OUT / INOUT / VARIADIC are real signature information and survive. Same
    // documented trade-off as NormalizeSequenceOptions: a desired schema that explicitly writes "IN" shows drift —
    // omit it instead.
    internal static string NormalizeProcedureArguments(string arguments)
    {
        return string.Join(", ", SplitTopLevel(arguments).Select(argument =>
            argument.StartsWith("IN ", StringComparison.Ordinal) ? argument[3..] : argument));
    }

    // Splits the rendered argument list on top-level commas only — a comma inside a parenthesised or quoted
    // default expression (e.g. DEFAULT repeat('x', 3)) is part of its argument.
    private static List<string> SplitTopLevel(string arguments)
    {
        var parts = new List<string>();
        var depth = 0;
        var quote = '\0';
        var start = 0;
        for (var i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0'; // a doubled quote re-enters on the next character, which splits identically
                }
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(arguments[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        parts.Add(arguments[start..].Trim());
        return parts;
    }

    private static async Task<Dictionary<(string, string), string?>> QueryRoutineComments(NpgsqlConnection conn, string[]? schemas, char kind, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), string?>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT n.nspname, p.proname, d.description
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            LEFT JOIN pg_description d
                ON d.objoid = p.oid
                AND d.classoid = 'pg_proc'::regclass
                AND d.objsubid = 0
            WHERE p.prokind = @kind::"char"
            AND (@schemas::text[] IS NULL OR n.nspname = ANY(@schemas))
            AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            AND n.nspname NOT LIKE 'pg\_toast%' ESCAPE '\'
            AND n.nspname NOT LIKE 'pg\_temp%' ESCAPE '\'
            ORDER BY n.nspname, p.proname
            """;
        AddSchemasParameter(cmd, schemas);
        cmd.Parameters.AddWithValue("kind", kind.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[(reader.GetString(0), reader.GetString(1))] = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        return result;
    }

    // ── Model assembly ────────────────────────────────────────────────────────

    private static Database Build(
        List<TableRow> tables,
        List<ColumnRow> columns,
        List<PrimaryKeyRow> primaryKeys,
        List<ForeignKeyRow> foreignKeys,
        List<UniqueConstraintRow> uniqueConstraints,
        List<CheckConstraintRow> checkConstraints,
        List<ExclusionConstraintRow> exclusionConstraints,
        List<IndexRow> indexes,
        List<TriggerRow> triggers,
        Dictionary<string, string?> schemaComments,
        Dictionary<(string, string), string?> tableComments,
        Dictionary<(string, string, string), string?> columnComments,
        Dictionary<(string, string), string?> indexComments,
        Dictionary<(string, string, string), string?> constraintComments,
        List<SchemaGrantRow> schemaGrants,
        List<TableGrantRow> tableGrants,
        List<ViewRow> views,
        Dictionary<(string, string), string?> viewComments,
        List<ViewDependencyRow> viewDependencies,
        List<EnumRow> enums,
        Dictionary<(string, string), string?> enumComments,
        List<SequenceRow> sequences,
        Dictionary<(string, string), string?> sequenceComments,
        List<DomainRow> domains,
        List<DomainCheckRow> domainChecks,
        List<CompositeTypeRow> compositeTypes,
        List<CompositeFieldRow> compositeFields,
        List<NativeTypeRow> nativeTypes,
        List<RoutineRow> functions,
        Dictionary<(string, string), string?> functionComments,
        List<RoutineRow> procedures,
        Dictionary<(string, string), string?> procedureComments,
        List<RoutineRow> aggregates,
        Dictionary<(string, string), string?> aggregateComments,
        List<ExtensionRow> extensions
    )
    {
        var bySchema = tables
            .GroupBy(t => t.Schema)
            .ToDictionary(
                g => g.Key,
                g => g.Select(t => BuildTable(t, columns, primaryKeys, foreignKeys, uniqueConstraints, checkConstraints, exclusionConstraints, indexes, triggers, tableComments, columnComments, indexComments, constraintComments, tableGrants)).ToList());

        var viewsBySchema = views
            .GroupBy(v => v.Schema)
            .ToDictionary(
                g => g.Key,
                g => g.Select(v => BuildView(v, viewComments, viewDependencies, indexes, indexComments)).ToList());

        var enumsBySchema = enums
            .GroupBy(e => e.Schema)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => new EnumType
                {
                    Name = e.Name,
                    Values = [.. e.Values.Select(v => new EnumLabel(v))],
                    Comment = enumComments.GetValueOrDefault((e.Schema, e.Name)),
                }).ToList());

        var sequencesBySchema = sequences
            .GroupBy(s => s.Schema)
            .ToDictionary(
                g => g.Key,
                g => g.Select(s => new Sequence
                {
                    Name = s.Name,
                    Options = NormalizeSequenceOptions(s),
                    Comment = sequenceComments.GetValueOrDefault((s.Schema, s.Name)),
                }).ToList());

        // Functions and procedures are one model (Routine) distinguished by RoutineKind, sharing a single name
        // space, so the two query results merge into one routine list per schema.
        var routinesBySchema = functions
            .Select(f => (f.Schema, Routine: BuildRoutine(f, RoutineKind.Function, functionComments)))
            .Concat(procedures.Select(p => (p.Schema, Routine: BuildRoutine(p, RoutineKind.Procedure, procedureComments))))
            .Concat(aggregates.Select(a => (a.Schema, Routine: BuildRoutine(a, RoutineKind.Aggregate, aggregateComments))))
            .GroupBy(x => x.Schema)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Routine).ToList());

        var domainsBySchema = domains
            .GroupBy(d => d.Schema)
            .ToDictionary(g => g.Key, g => g.Select(d => MapDomain(d, domainChecks)).ToList());

        var compositeTypesBySchema = compositeTypes
            .GroupBy(c => c.Schema)
            .ToDictionary(g => g.Key, g => g.Select(c => MapCompositeType(c, compositeFields)).ToList());

        // Names normalize to the model's canonical spellings, the same universe the column mapping produces,
        // so a reference and its vocabulary entry always compare in one spelling. Two catalog rows can meet
        // at one canonical name (char and bpchar), so entries dedupe after normalizing.
        var nativeTypesBySchema = nativeTypes
            .Select(t => (t.Schema, Type: new NativeType
            {
                Name = NormalizeNativeTypeName(t.Name),
                ProvidedBy = t.Extension is null ? null : new ExtensionReference(t.Extension),
            }))
            .GroupBy(x => x.Schema)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Type).DistinctBy(t => t.Name).ToList());

        // Drive schema list from what actually exists in the database, not from what was requested.
        var existingSchemas = schemaComments.Keys
            .Union(bySchema.Keys)
            .Union(viewsBySchema.Keys)
            .Union(enumsBySchema.Keys)
            .Union(sequencesBySchema.Keys)
            .Union(routinesBySchema.Keys)
            .Union(domainsBySchema.Keys)
            .Union(compositeTypesBySchema.Keys)
            .Union(nativeTypesBySchema.Keys)
            .Union(schemaGrants.Select(g => g.SchemaName))
            .Distinct(StringComparer.Ordinal);

        var dbSchemas = existingSchemas
            .Select(name => new Schema
            {
                Name = name,
                // Postgres provides `public` and `pg_catalog`: containers, not something a migration creates or drops.
                IsImplicit = name is PostgresSchemas.Provided or PostgresSchemas.Catalog,
                Comment = schemaComments.GetValueOrDefault(name),
                Grants = [.. schemaGrants.Where(g => g.SchemaName == name).Select(g => new SchemaGrant(g.Role))],
                Tables = [.. bySchema.GetValueOrDefault(name, [])],
                Views = [.. viewsBySchema.GetValueOrDefault(name, [])],
                Enums = [.. enumsBySchema.GetValueOrDefault(name, [])],
                Sequences = [.. sequencesBySchema.GetValueOrDefault(name, [])],
                Routines = [.. routinesBySchema.GetValueOrDefault(name, [])],
                Domains = [.. domainsBySchema.GetValueOrDefault(name, [])],
                CompositeTypes = [.. compositeTypesBySchema.GetValueOrDefault(name, [])],
                NativeTypes = [.. nativeTypesBySchema.GetValueOrDefault(name, [])],
            })
            .ToList();

        var dbExtensions = extensions
            .Select(e => new Extension { Name = e.Name, Version = e.Version, Comment = e.Comment })
            .ToList();
        return new Database { Schemas = dbSchemas, Extensions = dbExtensions };
    }

    // Postgres engine defaults are folded to null so a bare "CREATE SEQUENCE" round-trips to an all-null
    // SequenceOptions and the core's plain record equality sees no drift. Documented trade-off: a desired schema
    // that *explicitly* declares an engine default (e.g. START 1 on an ascending sequence) shows drift against the
    // normalized null; the fix is to omit the option.
    internal static SequenceOptions NormalizeSequenceOptions(SequenceRow row)
    {
        var ascending = row.Increment > 0;
        var (typeMin, typeMax) = row.DataType switch
        {
            "smallint" => ((long)short.MinValue, (long)short.MaxValue),
            "integer" => ((long)int.MinValue, (long)int.MaxValue),
            _ => (long.MinValue, long.MaxValue), // bigint
        };

        var defaultMin = ascending ? 1L : typeMin;
        var defaultMax = ascending ? typeMax : -1L;
        // The default start is the sequence's *effective* minvalue (ascending) / maxvalue (descending), not the
        // default min/max — CREATE SEQUENCE q MINVALUE 5 starts at 5.
        var defaultStart = ascending ? row.MinValue : row.MaxValue;

        return new SequenceOptions(
            DataType: row.DataType == "bigint" ? null : SqlType.Parse(row.DataType),
            StartWith: row.Start == defaultStart ? null : row.Start,
            IncrementBy: row.Increment == 1 ? null : row.Increment,
            MinValue: row.MinValue == defaultMin ? null : row.MinValue,
            MaxValue: row.MaxValue == defaultMax ? null : row.MaxValue,
            Cache: row.Cache == 1 ? null : row.Cache,
            Cycle: row.Cycle);
    }

    private static Routine BuildRoutine(RoutineRow row, RoutineKind kind, Dictionary<(string, string), string?> comments) => new()
    {
        Name = row.Name,
        RoutineKind = kind,
        Arguments = row.Arguments,
        Definition = row.Definition,
        Comment = comments.GetValueOrDefault((row.Schema, row.Name)),
    };

    private static View BuildView(
        ViewRow row,
        Dictionary<(string, string), string?> viewComments,
        List<ViewDependencyRow> viewDependencies,
        List<IndexRow> allIndexes,
        Dictionary<(string, string), string?> indexComments)
    {
        var dependsOn = viewDependencies
            .Where(d => d.ViewSchema == row.Schema && d.ViewName == row.Name)
            .Select(d => new ObjectAddress(d.RefSchema, d.RefName))
            .ToList();

        // Only a materialized view can carry indexes; the same index rows that a table would consume are routed
        // here when the relation they sit on is this matview (relation names are unique per schema, so there is no
        // overlap with a table's indexes).
        var indexes = row.IsMaterialized
            ? allIndexes
                .Where(i => i.SchemaName == row.Schema && i.TableName == row.Name)
                .Select(i => MapIndex(i, indexComments.GetValueOrDefault((row.Schema, i.IndexName))))
                .ToList()
            : [];

        return new View
        {
            Name = row.Name,
            Body = row.Definition,
            Comment = viewComments.GetValueOrDefault((row.Schema, row.Name)),
#pragma warning disable CS0618 // Dependencies moved to the dependency graph in Core 5.6; migrating this read is its own change.
            DependsOn = dependsOn,
#pragma warning restore CS0618
            IsMaterialized = row.IsMaterialized,
            Indexes = [.. indexes],
        };
    }

    private static Table BuildTable(
        TableRow tableRow,
        List<ColumnRow> allColumns,
        List<PrimaryKeyRow> allPrimaryKeys,
        List<ForeignKeyRow> allForeignKeys,
        List<UniqueConstraintRow> allUniqueConstraints,
        List<CheckConstraintRow> allCheckConstraints,
        List<ExclusionConstraintRow> allExclusionConstraints,
        List<IndexRow> allIndexes,
        List<TriggerRow> allTriggers,
        Dictionary<(string, string), string?> tableComments,
        Dictionary<(string, string, string), string?> columnComments,
        Dictionary<(string, string), string?> indexComments,
        Dictionary<(string, string, string), string?> constraintComments,
        List<TableGrantRow> allTableGrants
    )
    {
        var cols = allColumns
            .Where(c => c.TableSchema == tableRow.Schema && c.TableName == tableRow.Name)
            .Select(c => MapColumn(c, columnComments));

        var pk = allPrimaryKeys
            .Where(pk => pk.TableSchema == tableRow.Schema && pk.TableName == tableRow.Name)
            .GroupBy(pk => pk.ConstraintName)
            .Select(g => new PrimaryKey
            {
                Name = g.Key,
                ColumnNames = [.. g.Select(r => new SqlIdentifier(r.ColumnName))],
                Comment = constraintComments.GetValueOrDefault((tableRow.Schema, tableRow.Name, g.Key)),
            })
            .FirstOrDefault();

        var fks = allForeignKeys
            .Where(fk => fk.TableSchema == tableRow.Schema && fk.TableName == tableRow.Name)
            .Select(fk => MapForeignKey(fk, constraintComments.GetValueOrDefault((tableRow.Schema, tableRow.Name, fk.ConstraintName))));

        var uniques = allUniqueConstraints
            .Where(u => u.TableSchema == tableRow.Schema && u.TableName == tableRow.Name)
            .Select(u => new UniqueConstraint
            {
                Name = u.ConstraintName,
                ColumnNames = [.. u.ColumnNames.Select(c => new SqlIdentifier(c))],
                Comment = constraintComments.GetValueOrDefault((tableRow.Schema, tableRow.Name, u.ConstraintName)),
            });

        var checks = allCheckConstraints
            .Where(c => c.TableSchema == tableRow.Schema && c.TableName == tableRow.Name)
            .Select(c => new CheckConstraint
            {
                Name = c.ConstraintName,
                Expression = c.Expression,
                Comment = constraintComments.GetValueOrDefault((tableRow.Schema, tableRow.Name, c.ConstraintName)),
            });

        var exclusions = allExclusionConstraints
            .Where(e => e.TableSchema == tableRow.Schema && e.TableName == tableRow.Name)
            .Select(e => MapExclusionConstraint(e, constraintComments.GetValueOrDefault((tableRow.Schema, tableRow.Name, e.ConstraintName))));

        var idxs = allIndexes
            .Where(i => i.SchemaName == tableRow.Schema && i.TableName == tableRow.Name)
            .Select(i => MapIndex(i, indexComments.GetValueOrDefault((tableRow.Schema, i.IndexName))));

        var triggers = allTriggers
            .Where(t => t.TableSchema == tableRow.Schema && t.TableName == tableRow.Name)
            .Select(MapTrigger);

        var grants = allTableGrants
            .Where(g => g.SchemaName == tableRow.Schema && g.TableName == tableRow.Name)
            .GroupBy(g => g.Role)
            .Select(g => new TableGrant(g.Key, ToTablePrivilege(g.Select(r => r.Privilege))));

        return new Table
        {
            Name = tableRow.Name,
            PrimaryKey = pk,
            Comment = tableComments.GetValueOrDefault((tableRow.Schema, tableRow.Name)),
            Columns = [.. cols],
            ForeignKeys = [.. fks],
            UniqueConstraints = [.. uniques],
            CheckConstraints = [.. checks],
            ExclusionConstraints = [.. exclusions],
            Indexes = [.. idxs],
            Grants = [.. grants],
            Triggers = [.. triggers],
        };
    }

    // tgtype is a bitmask: bit 0 = ROW (else STATEMENT); bit 6 = INSTEAD OF, else bit 1 = BEFORE, else AFTER;
    // bits 2/3/4/5 = INSERT/DELETE/UPDATE/TRUNCATE. The model's TriggerEvent flags differ from these bit values,
    // so the events are translated rather than copied.
    private static Trigger MapTrigger(TriggerRow row)
    {
        var timing = (row.TgType & 64) != 0 ? TriggerTiming.InsteadOf
            : (row.TgType & 2) != 0 ? TriggerTiming.Before
            : TriggerTiming.After;
        var level = (row.TgType & 1) != 0 ? TriggerLevel.Row : TriggerLevel.Statement;

        var events = TriggerEvent.None;
        if ((row.TgType & 4) != 0)
        {
            events |= TriggerEvent.Insert;
        }

        if ((row.TgType & 8) != 0)
        {
            events |= TriggerEvent.Delete;
        }

        if ((row.TgType & 16) != 0)
        {
            events |= TriggerEvent.Update;
        }

        if ((row.TgType & 32) != 0)
        {
            events |= TriggerEvent.Truncate;
        }

        return new Trigger
        {
            Name = row.Name,
            Timing = timing,
            Events = events,
            Function = new RoutineReference(row.FunctionSchema, row.FunctionName),
            Level = level,
            UpdateOfColumns = [.. row.UpdateOfColumns.Select(c => new SqlIdentifier(c))],
            When = row.When,
            FunctionArguments = row.FunctionArguments,
            Comment = row.Comment,
        };
    }

    private static CompositeType MapCompositeType(CompositeTypeRow row, List<CompositeFieldRow> allFields)
    {
        var fields = allFields
            .Where(f => f.Schema == row.Schema && f.TypeName == row.Name)
            .OrderBy(f => f.OrdinalPosition)
            .Select(f => new CompositeField(f.FieldName, MapSqlType(f.DataType, f.UdtName, udtSchema: null, domainSchema: null, domainName: null, f.MaxLength, f.Precision, f.Scale)))
            .ToList();
        return new CompositeType { Name = row.Name, Fields = fields, Comment = row.Comment };
    }

    private static DomainType MapDomain(DomainRow row, List<DomainCheckRow> allChecks)
    {
        var checks = allChecks
            .Where(c => c.Schema == row.Schema && c.DomainName == row.Name)
            .Select(c => new CheckConstraint { Name = c.CheckName, Expression = c.Expression });
        return new DomainType
        {
            Name = row.Name,
            DataType = MapSqlType(row.DataType, row.UdtName, udtSchema: null, domainSchema: null, domainName: null, row.MaxLength, row.Precision, row.Scale),
            Default = row.Default,
            NotNull = row.NotNull,
            Checks = [.. checks],
            Comment = row.Comment,
        };
    }

    private static ExclusionConstraint MapExclusionConstraint(ExclusionConstraintRow row, string? comment)
    {
        var elements = new List<ExclusionElement>();
        for (var i = 0; i < row.ElementTexts.Length; i++)
        {
            elements.Add(row.IsExpressions[i]
                ? new ExclusionElement(row.Operators[i], Expression: row.ElementTexts[i])
                : new ExclusionElement(row.Operators[i], Column: row.ElementTexts[i]));
        }

        return new ExclusionConstraint
        {
            Name = row.ConstraintName,
            Elements = elements,
            Method = row.Method,
            Predicate = row.Predicate,
            Comment = comment,
        };
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static TableIndex MapIndex(IndexRow row, string? comment)
    {
        var keys = new List<IndexColumn>();
        var include = new List<SqlIdentifier>();
        for (var i = 0; i < row.ColumnTexts.Length; i++)
        {
            if (i < row.NumKeyAtts)
            {
                var (sort, nulls) = DecodeIndexOption(row.Options[i]);
                keys.Add(row.IsExpressions[i]
                    ? new IndexColumn(Expression: row.ColumnTexts[i], Sort: sort, Nulls: nulls)
                    : new IndexColumn(Column: row.ColumnTexts[i], Sort: sort, Nulls: nulls));
            }
            else
            {
                // INCLUDE columns are always plain columns and carry no ordering.
                include.Add(row.ColumnTexts[i]);
            }
        }

        return new TableIndex
        {
            Name = row.IndexName,
            Columns = keys,
            IsUnique = row.IsUnique,
            Comment = comment,
            Predicate = row.Predicate,
            Method = row.Method,
            Include = include,
        };
    }

    // indoption packs two bits per key: 0x01 = DESC, 0x02 = NULLS FIRST. The engine default is NULLS LAST for an
    // ascending key and NULLS FIRST for a descending one (i.e. the default of the NULLS-FIRST bit equals the DESC
    // bit), so a key matching that default normalizes to Default/Default and a plain index round-trips without
    // phantom drift — only an explicitly non-default ordering surfaces.
    private static (IndexSort Sort, IndexNulls Nulls) DecodeIndexOption(int option)
    {
        var descending = (option & 1) != 0;
        var nullsFirst = (option & 2) != 0;
        var sort = descending ? IndexSort.Descending : IndexSort.Default;
        var nulls = nullsFirst == descending
            ? IndexNulls.Default
            : nullsFirst ? IndexNulls.First : IndexNulls.Last;
        return (sort, nulls);
    }

    private static Column MapColumn(ColumnRow row, Dictionary<(string, string, string), string?> columnComments) => new()
    {
        Name = row.ColumnName,
        Type = MapSqlType(row.DataType, row.UdtName, row.UdtSchema, row.DomainSchema, row.DomainName, row.MaxLength, row.NumericPrecision, row.NumericScale),
        IsNullable = row.IsNullable,
        IsIdentity = row.IsIdentity,
        DefaultExpression = row.DefaultExpression,
        IdentityOptions = row.IsIdentity
            ? new IdentityOptions(row.IdentityStart, row.IdentityMinValue, row.IdentityIncrement)
            : null,
        GeneratedExpression = row.GeneratedExpression,
        // Postgres has only stored generated columns, so one that exists is stored by construction.
        IsStored = row.GeneratedExpression is not null,
        Comment = columnComments.GetValueOrDefault((row.TableSchema, row.TableName, row.ColumnName)),
    };

    /// <summary>
    /// The canonical model spelling of a catalog type name (<c>int4</c> to <c>int</c>, <c>uuid</c> to
    /// <c>guid</c>); a name the model has no spelling for is kept verbatim.
    /// </summary>
    internal static SqlIdentifier NormalizeNativeTypeName(string typeName) => typeName switch
    {
        // Parse cannot read these facet-less: the facets belong to a use, but the catalog names the type.
        "numeric" => "decimal",
        "bpchar" or "char" => "char",
        _ => SqlType.Parse(typeName).Name,
    };

    private static SqlType MapSqlType(string dataType, string udtName, string? udtSchema, string? domainSchema, string? domainName, int? maxLength, int? precision, int? scale)
    {
        // For a column declared against a domain, information_schema returns the domain's
        // base type in data_type (e.g. "text"). Preserve the domain name so the schema
        // round-trips faithfully.
        if (domainName is not null)
        {
            return domainSchema is null
                ? SqlType.Custom(domainName)
                : SqlType.Custom(domainSchema, domainName);
        }

        return dataType switch
        {
            "boolean" => SqlType.Boolean,
            "smallint" => SqlType.SmallInt,
            "integer" => SqlType.Int,
            "bigint" => SqlType.BigInt,
            "real" => SqlType.Float,
            "double precision" => SqlType.Double,
            "numeric" => SqlType.Decimal(precision ?? 18, scale ?? 0),
            "character" => SqlType.Char(maxLength ?? 1),
            "character varying" => SqlType.VarChar(maxLength),
            "text" => SqlType.Text,
            "date" => SqlType.Date,
            "time without time zone" => SqlType.Time,
            "timestamp without time zone" => SqlType.DateTime,
            "timestamp with time zone" => SqlType.DateTimeOffset,
            "uuid" => SqlType.Guid,
            "bytea" => SqlType.VarBinary(),
            // A user-defined type (enum, composite, …) or a built-in outside the switch (jsonb, inet, …):
            // captured verbatim, qualifier included — PostgresSqlEquivalence decides what a qualifier means.
            _ => udtSchema is null ? SqlType.Custom(udtName) : SqlType.Custom(udtSchema, udtName),
        };
    }

    private static TablePrivilege ToTablePrivilege(IEnumerable<string> privileges)
    {
        return privileges.Aggregate(TablePrivilege.None, (current, p) => current | p switch
        {
            "SELECT" => TablePrivilege.Select,
            "INSERT" => TablePrivilege.Insert,
            "UPDATE" => TablePrivilege.Update,
            "DELETE" => TablePrivilege.Delete,
            _ => TablePrivilege.None,
        });
    }

    private static ForeignKey MapForeignKey(ForeignKeyRow row, string? comment = null) => new()
    {
        Name = row.ConstraintName,
        ColumnNames = [.. row.ColumnNames.Select(c => new SqlIdentifier(c))],
        References = new ObjectAddress(row.ForeignSchema, row.ForeignTable),
        ReferencedColumnNames = [.. row.ForeignColumnNames.Select(c => new SqlIdentifier(c))],
        OnDelete = MapReferentialAction(row.DeleteRule),
        OnUpdate = MapReferentialAction(row.UpdateRule),
        Comment = comment,
    };

    private static ReferentialAction MapReferentialAction(char code) => code switch
    {
        'c' => ReferentialAction.Cascade,
        'n' => ReferentialAction.SetNull,
        'd' => ReferentialAction.SetDefault,
        'r' => ReferentialAction.Restrict,
        _ => ReferentialAction.NoAction, // 'a' = NO ACTION
    };

    // An address names the schema it sits in: a schema address is its own, an object and a member each carry
    // theirs, and an extension belongs to the database rather than to any schema.
    private static SqlIdentifier? SchemaOf(Address address) => address switch
    {
        DatabaseAddress { Kind: DatabaseObjectKind.Schema } schema => schema.Name,
        ObjectAddress @object => @object.Schema,
        MemberAddress member => member.Schema,
        _ => null,
    };

}
