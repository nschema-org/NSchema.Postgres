namespace NSchema.Postgres;

/// <summary>
/// The schemas Postgres provides.
/// </summary>
internal static class PostgresSchemas
{
    /// <summary>
    /// Every Postgres database has a <c>public</c> schema; a migration neither creates nor drops it.
    /// </summary>
    public const string Provided = "public";
}
