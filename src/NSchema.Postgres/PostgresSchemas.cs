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

    /// <summary>
    /// The engine's own schema, captured for the native types it provides.
    /// </summary>
    public const string Catalog = "pg_catalog";
}
