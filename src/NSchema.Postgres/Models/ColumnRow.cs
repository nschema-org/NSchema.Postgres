namespace NSchema.Postgres.Models;

internal sealed record ColumnRow(
    string TableSchema,
    string TableName,
    string ColumnName,
    string DataType,
    string UdtName,
    string? UdtSchema,
    string? DomainSchema,
    string? DomainName,
    int? MaxLength,
    int? NumericPrecision,
    int? NumericScale,
    bool IsNullable,
    string? DefaultExpression,
    bool IsIdentity,
    long? IdentityStart = null,
    long? IdentityMinValue = null,
    long? IdentityIncrement = null,
    string? GeneratedExpression = null);
