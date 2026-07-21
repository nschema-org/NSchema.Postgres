using NSchema.Plugins;
using NSchema.Plugins.Model.Config;

namespace NSchema.Postgres;

/// <summary>
/// The NSchema plugin manifest for the PostgreSQL provider.
/// </summary>
public sealed class PostgresPlugin : INSchemaDatabasePlugin
{
    private const string DiagnosticSource = "postgres";

    private const string EnvConnectionString = "NSCHEMA_POSTGRES_CONNECTION_STRING";
    private const string EnvUsername = "NSCHEMA_POSTGRES_USERNAME";
    private const string EnvPassword = "NSCHEMA_POSTGRES_PASSWORD";

    /// <summary>The options a DATABASE statement binds onto.</summary>
    private sealed class PostgresOptions
    {
        public string? ConnectionString { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public int? CommandTimeout { get; set; }
    }

    /// <inheritdoc />
    public string GetScaffoldTemplate(ScaffoldContext context) =>
        $"""
        DATABASE postgres (
          -- Prefer the {EnvConnectionString} environment variable, which
          -- overrides the value below.
          connection_string = ''
          -- Credentials may be supplied separately from the connection string (e.g. from
          -- a secret store) via {EnvUsername} / {EnvPassword}.
          -- They override any user/password embedded in connection_string.
        );
        """;

    /// <inheritdoc />
    public string GetSampleSchema() =>
        """
        CREATE SCHEMA app;

        CREATE TABLE app.widgets (
          id   bigint NOT NULL,
          name text,
          CONSTRAINT widgets_pkey PRIMARY KEY (id)
        );
        """;

    /// <inheritdoc />
    public Result Configure(NSchemaApplicationBuilder builder, PluginConfig config)
    {
        var bound = config.Bind<PostgresOptions>();
        var diagnostics = new List<Diagnostic>(bound.Diagnostics);
        var options = bound.Value!;

        // Credentials may be supplied out of band (e.g. a secret store); the environment overrides the statement.
        var connectionString = Environment.GetEnvironmentVariable(EnvConnectionString) ?? options.ConnectionString;
        var username = Environment.GetEnvironmentVariable(EnvUsername) ?? options.Username;
        var password = Environment.GetEnvironmentVariable(EnvPassword) ?? options.Password;

        if (string.IsNullOrEmpty(connectionString))
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticSource,
                $"DATABASE postgres: connection_string is required. Set it via the {EnvConnectionString} environment variable or the statement attribute."));
        }

        if (options.CommandTimeout is < 0)
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticSource, "DATABASE postgres: command_timeout must not be negative."));
        }

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return Result.From(diagnostics);
        }

        builder.UsePostgres(dataSource =>
        {
            // Order matters: assigning ConnectionString re-parses the whole string, so it must precede the discrete overrides.
            dataSource.ConnectionStringBuilder.ConnectionString = connectionString;
            if (username is not null)
            {
                dataSource.ConnectionStringBuilder.Username = username;
            }

            if (password is not null)
            {
                dataSource.ConnectionStringBuilder.Password = password;
            }

            if (options.CommandTimeout is { } timeout)
            {
                dataSource.ConnectionStringBuilder.CommandTimeout = timeout;
            }
        });

        return Result.From(diagnostics);
    }
}
