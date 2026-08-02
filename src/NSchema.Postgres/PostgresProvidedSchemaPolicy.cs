using NSchema.Model.Schemas;
using NSchema.Project.Domain.Directives;
using NSchema.Project.Policies;

namespace NSchema.Postgres;

/// <summary>
/// Rejects a declaration of a schema Postgres provides.
/// </summary>
/// <remarks>
/// Declaring <c>public</c> asks for something the database already guarantees, and the plan would carry a
/// <c>CREATE SCHEMA</c> that cannot succeed. Naming its objects is enough — the schema is there to hold them.
/// </remarks>
internal sealed class PostgresProvidedSchemaPolicy : IProjectPolicy
{
    private const string Source = "postgres";

    /// <inheritdoc />
    public IEnumerable<Diagnostic> Validate(ProjectDefinition project) => project.Database.Schemas
        .Where(schema => !schema.IsImplicit && schema.Name == PostgresSchemas.Provided)
        .Select(schema => Diagnostic.Warning(Source, "provided-schema-declared", $"Postgres provides the '{schema.Name}' schema, so it will be ignored."));
}
