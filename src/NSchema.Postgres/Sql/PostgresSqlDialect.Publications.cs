using NSchema.Model.Publications;
using NSchema.Plan.Domain;
using NSchema.Plan.Domain.Publications;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{
    // ── Publications ──────────────────────────────────────────────────────────

    // Tables first, each run opened once: the only spelling Postgres before 15 accepts, and valid after it.
    protected override Result<IReadOnlyList<SqlStatement>> CreatePublication(CreatePublication action)
    {
        var publication = action.Publication;
        var targets = new List<string>();
        if (publication.Tables.Count > 0)
        {
            targets.Add("TABLE " + string.Join(", ", publication.Tables.Select(PublishedTable)));
        }
        if (publication.Schemas.Count > 0)
        {
            targets.Add("TABLES IN SCHEMA " + string.Join(", ", publication.Schemas.Select(Quote)));
        }

        var sql = $"CREATE PUBLICATION {Quote(publication.Name)}";
        if (publication.AllTables)
        {
            sql += " FOR ALL TABLES";
        }
        else if (targets.Count > 0)
        {
            sql += " FOR " + string.Join(", ", targets);
        }
        if (publication.Operations != PublishedOperations.All)
        {
            sql += $" WITH (publish = '{Publish(publication.Operations)}')";
        }

        return Statement(sql);
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropPublication(DropPublication action) =>
        Statement($"DROP PUBLICATION {Quote(action.PublicationName)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenamePublication(RenamePublication action) =>
        Statement($"ALTER PUBLICATION {Quote(action.OldName)} RENAME TO {Quote(action.NewName)}");

    protected override Result<IReadOnlyList<SqlStatement>> AddPublicationTable(AddPublicationTable action) =>
        Statement($"ALTER PUBLICATION {Quote(action.PublicationName)} ADD TABLE {PublishedTable(action.Table)}");

    protected override Result<IReadOnlyList<SqlStatement>> DropPublicationTable(DropPublicationTable action) =>
        Statement($"ALTER PUBLICATION {Quote(action.PublicationName)} DROP TABLE {Qualify(action.Table)}");

    protected override Result<IReadOnlyList<SqlStatement>> AddPublicationSchema(AddPublicationSchema action) =>
        Statement($"ALTER PUBLICATION {Quote(action.PublicationName)} ADD TABLES IN SCHEMA {Quote(action.Schema)}");

    protected override Result<IReadOnlyList<SqlStatement>> DropPublicationSchema(DropPublicationSchema action) =>
        Statement($"ALTER PUBLICATION {Quote(action.PublicationName)} DROP TABLES IN SCHEMA {Quote(action.Schema)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetPublicationOperations(SetPublicationOperations action) =>
        Statement($"ALTER PUBLICATION {Quote(action.PublicationName)} SET (publish = '{Publish(action.NewOperations)}')");

    protected override Result<IReadOnlyList<SqlStatement>> SetPublicationComment(SetPublicationComment action) =>
        Comment($"PUBLICATION {Quote(action.PublicationName)}", action.NewComment);

    private string PublishedTable(PublishedTable table)
    {
        var sql = Qualify(table.Table);
        if (table.Columns is { } columns)
        {
            sql += $" ({ColumnList(columns)})";
        }
        if (table.Filter is { } filter)
        {
            sql += $" WHERE ({filter.Value})";
        }
        return sql;
    }

    private static string Publish(PublishedOperations operations) => string.Join(", ",
        new[] { PublishedOperations.Insert, PublishedOperations.Update, PublishedOperations.Delete, PublishedOperations.Truncate }
            .Where(o => operations.HasFlag(o))
            .Select(o => o.ToString().ToLowerInvariant()));
}
