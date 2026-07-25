using System.ComponentModel.DataAnnotations;
using NSchema.Configuration.Plugins;
using NSchema.Plugins;
using NSchema.Project.Nsql;
using NSchema.Project.Nsql.Syntax;
using NSchema.Project.Nsql.Syntax.Settings;
using NSchema.Project.Nsql.Tokens;

namespace NSchema.Postgres;

/// <summary>
/// The NSchema plugin manifest for the PostgreSQL provider.
/// </summary>
public sealed class PostgresPlugin : INSchemaDatabasePlugin
{
    private const string DiagnosticSource = "postgres";

    /// <summary>
    /// The options a DATABASE statement binds onto.
    /// </summary>
    private sealed class PostgresOptions
    {
        [Required(ErrorMessage = "DATABASE postgres: connection_string is required. Set it in the statement, or supply NSCHEMA_DATABASE_CONNECTION_STRING.")]
        public string? ConnectionString { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public int? CommandTimeout { get; set; }
    }

    /// <inheritdoc />
    public SettingsStatement GetScaffoldTemplate(ScaffoldContext context) =>
        new(SettingsKeyword.Database, Identifier.Synthetic("postgres"), new SeparatedSyntaxList<Setting>(
        [
            new Setting("connection_string", string.Empty),
        ]))
        {
            DocComment = new Token(
                TokenKind.DocComment,
                "Prefer the NSCHEMA_DATABASE_CONNECTION_STRING environment variable, which overrides the value below.\n" +
                $"Credentials may be supplied separately from the connection string (e.g. from a secret\n" +
                "store) via NSCHEMA_DATABASE_USERNAME / NSCHEMA_DATABASE_PASSWORD. They override any user/password\n" +
                "connection_string.",
                SourcePosition.None),
        };

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
    public Result Configure(NSchemaApplicationBuilder builder, PluginSettings settings)
    {
        var bound = settings.Get<PostgresOptions>();
        if (bound.Value is not { } options)
        {
            return Result.From(bound.Diagnostics);
        }

        var diagnostics = new List<Diagnostic>(bound.Diagnostics);

        // The engine has already applied any NSCHEMA_DATABASE_* override, so the bound values are final.
        var connectionString = options.ConnectionString;
        var username = options.Username;
        var password = options.Password;


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
