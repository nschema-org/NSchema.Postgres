namespace NSchema.Postgres.Models;

internal sealed record PublicationRow(
    string Name,
    bool AllTables,
    bool Insert,
    bool Update,
    bool Delete,
    bool Truncate,
    string? Comment);
