using NSchema.Configuration.Plugins;
using NSchema.Plan.Plugins;
using NSchema.Plugins;
using NSchema.Project.Nsql;
using NSchema.Project.Nsql.Syntax.Settings;

namespace NSchema.Postgres.Tests;

/// <summary>
/// Pins <see cref="PostgresPlugin"/>'s attribute parsing, scaffolding, and validation. The result-returning
/// <c>Configure</c> aggregates problems instead of throwing, so a misconfigured provider can be reported rather than
/// aborting. Pure unit tests — no Docker.
/// </summary>
public sealed class PostgresPluginTests
{
    private readonly PostgresPlugin _sut = new();

    private static ScaffoldContext Answered(params (string Key, string Value)[] answers) =>
        new()
        {
            Answers = answers.ToDictionary(a => a.Key, a => (string?)a.Value, StringComparer.OrdinalIgnoreCase),
        };

    private static string ConnectionString(SettingsStatement statement) =>
        statement.Settings.Single(setting => setting.Key == "connection_string").Value;

    private static SettingsStatement Configured(NsqlDocument document) =>
        document.Statements.OfType<SettingsStatement>().ShouldHaveSingleItem();

    [Fact]
    public void GetScaffoldTemplate_ReturnsDatabaseStatement()
    {
        // Act
        var block = Configured(_sut.GetScaffoldTemplate(new ScaffoldContext()));

        // Assert
        block.Keyword.ShouldBe(SettingsKeyword.Database);
        block.Label!.Value.ShouldBe("postgres");
        block.Settings.ShouldContain(a => a.Key == "connection_string");
    }

    [Fact]
    public void GetScaffoldPrompts_AsksForThePartsOfAConnectionString()
    {
        // Act
        var prompts = _sut.GetScaffoldPrompts(new ScaffoldContext());

        // Assert — every part has a default, so a non-interactive scaffold is never blocked.
        prompts.Select(prompt => prompt.Key).ShouldBe(["host", "port", "database", "username"]);
        prompts.ShouldAllBe(prompt => !prompt.IsRequired);
    }

    [Fact]
    public void GetScaffoldPrompts_DoesNotAskForThePassword()
    {
        // Act
        var prompts = _sut.GetScaffoldPrompts(new ScaffoldContext());

        // Assert — a password answered here would be written into a committed file; it belongs in the environment.
        prompts.ShouldNotContain(prompt => prompt.Key == "password");
    }

    [Fact]
    public void GetScaffoldTemplate_ComposesTheAnswersIntoTheConnectionString()
    {
        // Arrange
        var context = Answered(("host", "db.internal"), ("port", "6432"), ("database", "orders"), ("username", "app"));

        // Act
        var block = Configured(_sut.GetScaffoldTemplate(context));

        // Assert
        var connection = ConnectionString(block);
        connection.ShouldContain("Host=db.internal");
        connection.ShouldContain("Port=6432");
        connection.ShouldContain("Database=orders");
        connection.ShouldContain("Username=app");
    }

    [Fact]
    public void GetScaffoldTemplate_UnansweredLeavesThePlaceholderToEdit()
    {
        // Act
        var block = Configured(_sut.GetScaffoldTemplate(new ScaffoldContext()));

        // Assert
        ConnectionString(block).ShouldBeEmpty();
    }

    [Fact]
    public void GetSampleSchema_ScaffoldsANamedSchema()
    {
        // Act — unlike SQLite (main), Postgres scaffolds a dedicated schema.
        var schema = NsqlWriter.Write(_sut.GetSampleSchema());

        // Assert
        schema.ShouldContain("CREATE SCHEMA app;");
        schema.ShouldContain("CREATE TABLE app.widgets");
    }

    [Fact]
    public void Configure_ValidConnectionString_SucceedsAndRegistersProvider()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(("connection_string", "Host=localhost;Database=app"));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
        builder.Services.ShouldContain(d => d.ServiceType == typeof(SqlDialect));
    }

    [Fact]
    public void Configure_MissingConnectionString_FailsWithRequiredError()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config();

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldContain(e => e.Message.Contains("connection_string is required"));
    }

    [Fact]
    public void Configure_UnknownAttribute_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(
            ("connection_string", "Host=localhost"),
            ("nonsense", "x"));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldContain(e => e.Message.Contains("nonsense", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Configure_NonIntegerCommandTimeout_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(
            ("connection_string", "Host=localhost"),
            ("command_timeout", "soon"));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert — the binder rejects a value it cannot convert to int.
        result.IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Configure_NegativeCommandTimeout_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(
            ("connection_string", "Host=localhost"),
            ("command_timeout", "-1"));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldContain(e => e.Message.Contains("command_timeout must not be negative"));
    }

    [Fact]
    public void Configure_MultipleProblems_AggregatesEveryError()
    {
        // Arrange — no connection string and a negative timeout: both must be reported, not just the first.
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(("command_timeout", "-1"));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.Count().ShouldBe(2);
    }

    [Fact]
    public void Configure_SuppliedConnectionString_Succeeds()
    {
        // Arrange — the engine applies any NSCHEMA_DATABASE_* override before binding, so by here the
        // setting is simply present; where it came from is not the plugin's concern.
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(("connection_string", "Host=env-host;Database=app"));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    private static PluginSettings Config(params (string Key, string? Value)[] attributes)
        => new("postgres", attributes.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void GetSampleSchema_IsAlreadyCanonicallyFormatted()
    {
        // Act — the sample is a document, so what `new` writes needs no reformatting.
        var schema = NsqlWriter.Write(_sut.GetSampleSchema());

        // Assert
        NsqlWriter.Format(schema).Require().ShouldBe(schema);
    }
}
