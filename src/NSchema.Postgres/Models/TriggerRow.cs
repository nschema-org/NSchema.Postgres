namespace NSchema.Postgres.Models;

/// <summary>
/// A row of trigger metadata. <see cref="TgType"/> is the raw pg_trigger.tgtype bitmask (timing/level/events),
/// decoded into the model's enums when mapped.
/// </summary>
internal sealed record TriggerRow(
    string TableSchema,
    string TableName,
    string Name,
    int TgType,
    string FunctionSchema,
    string FunctionName,
    string? When,
    string[] UpdateOfColumns,
    string? FunctionArguments,
    string? Comment);
