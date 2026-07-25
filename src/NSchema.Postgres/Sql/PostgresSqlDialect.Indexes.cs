using NSchema.Model.Indexes;
using NSchema.Plan.Domain;
using NSchema.Plan.Domain.Indexes;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{

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
}
