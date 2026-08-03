namespace NSchema.Postgres.Models;

internal sealed record NativeTypeRow(string Schema, string Name, string? Extension);
