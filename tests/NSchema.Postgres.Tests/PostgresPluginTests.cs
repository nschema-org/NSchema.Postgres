using NSchema.Plan.Backends;
using NSchema.Plugins;

namespace NSchema.Postgres.Tests;

/// <summary>
/// Pins <see cref="PostgresPlugin"/>'s attribute parsing, environment-override precedence, and validation. The
/// result-returning <c>Configure</c> aggregates problems instead of throwing, so a misconfigured provider can
/// be reported rather than aborting. Pure unit tests — no Docker. The <c>NSCHEMA_POSTGRES_*</c> variables are
/// snapshotted and cleared so a developer's ambient environment cannot make the outcome non-deterministic.
/// </summary>
public sealed class PostgresPluginTests : IDisposable
{
    private static readonly string[] EnvVars =
    [
        "NSCHEMA_POSTGRES_CONNECTION_STRING",
        "NSCHEMA_POSTGRES_USERNAME",
        "NSCHEMA_POSTGRES_PASSWORD",
    ];

    private readonly Dictionary<string, string?> _savedEnv = new();
    private readonly PostgresPlugin _sut = new();

    public PostgresPluginTests()
    {
        foreach (var name in EnvVars)
        {
            _savedEnv[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    public void Dispose()
    {
        foreach (var (name, value) in _savedEnv)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public void GetScaffoldTemplate_ReturnsDatabaseStatement()
        => _sut.GetScaffoldTemplate(new ScaffoldContext()).ShouldContain("DATABASE postgres");

    [Fact]
    public void GetSampleSchema_ScaffoldsANamedSchema()
    {
        // Unlike SQLite (main), Postgres scaffolds a dedicated schema.
        var schema = _sut.GetSampleSchema();

        schema.ShouldContain("CREATE SCHEMA app;");
        schema.ShouldContain("CREATE TABLE app.widgets");
    }

    [Fact]
    public void Configure_ValidConnectionString_SucceedsAndRegistersProvider()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(("connection_string", ConfigValue.OfString("Host=localhost;Database=app")));

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
            ("connection_string", ConfigValue.OfString("Host=localhost")),
            ("nonsense", ConfigValue.OfString("x")));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldContain(e => e.Message.Contains("unknown attribute 'nonsense'"));
    }

    [Fact]
    public void Configure_NonIntegerCommandTimeout_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(
            ("connection_string", ConfigValue.OfString("Host=localhost")),
            ("command_timeout", ConfigValue.OfString("soon")));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldContain(e => e.Message.Contains("command_timeout must be an integer"));
    }

    [Fact]
    public void Configure_NegativeCommandTimeout_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(
            ("connection_string", ConfigValue.OfString("Host=localhost")),
            ("command_timeout", ConfigValue.OfInteger(-1)));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldContain(e => e.Message.Contains("command_timeout must not be negative"));
    }

    [Fact]
    public void Configure_MultipleProblems_AggregatesEveryError()
    {
        // Arrange — an unknown attribute and no connection string: both must be reported, not just the first.
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(("nope", ConfigValue.OfString("x")));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.Count().ShouldBe(2);
    }

    [Fact]
    public void Configure_EnvironmentConnectionString_SatisfiesOmittedAttribute()
    {
        // Arrange
        Environment.SetEnvironmentVariable("NSCHEMA_POSTGRES_CONNECTION_STRING", "Host=env-host;Database=app");
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config();

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    private static PluginConfig Config(params (string Key, ConfigValue Value)[] attributes)
        => new("postgres", attributes.ToDictionary(a => new AttributeKey(a.Key), a => a.Value));
}
