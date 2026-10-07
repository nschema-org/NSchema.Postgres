namespace NSchema.Postgres.Models;

internal sealed record PublishedTableRow(
    string Publication,
    string Schema,
    string Table,
    string[]? Columns,
    string? Filter);
