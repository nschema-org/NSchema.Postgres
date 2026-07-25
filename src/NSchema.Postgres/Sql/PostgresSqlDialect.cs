using NSchema.Model.Columns;
using NSchema.Plan.Backends;
using NSchema.Plan.Domain;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect : SqlDialect
{
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

    private string ToPostgresType(SqlType type) => type.Name.Value switch
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
        // Any other name is a database-specific or user-defined type (e.g. citext, jsonb, a domain); a
        // schema-qualified type is quoted (a user-defined type in another schema), an unqualified one emitted bare.
        _ => type.Schema is { } schema ? $"{Quote(schema)}.{Quote(type.Name)}" : type.Name.Value,
    };
}
