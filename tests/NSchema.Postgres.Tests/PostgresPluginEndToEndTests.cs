using NSchema.Configuration;
using NSchema.Operations.Apply;
using NSchema.Operations.Plan;
using NSchema.Postgres.Sql;
using NSchema.Postgres.Tests.Fixtures;
using NSchema.Sql.Model;

namespace NSchema.Postgres.Tests;

/// <summary>
/// End-to-end proof that the <see cref="PostgresPlugin"/> manifest wires a fully working provider: it runs a real
/// migration THROUGH the plugin's <c>Configure</c> (not the direct <c>UseCurrentSchemaPostgres</c> API) against a real
/// PostgreSQL container, then re-introspects to confirm the schema was applied. Requires Docker.
/// </summary>
[Collection("postgres")]
public sealed class PostgresPluginEndToEndTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture = fixture;
    private readonly string _schema = $"e2e_{Guid.NewGuid():N}";
    private string _projectDir = null!;

    public ValueTask InitializeAsync()
    {
        _projectDir = Directory.CreateTempSubdirectory("nschema-pg-e2e-").FullName;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Directory.Delete(_projectDir, recursive: true);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE";
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Apply_ThroughThePlugin_CreatesTheDesiredSchema()
    {
        // Arrange — a desired schema on disk, and a host configured ONLY through the plugin manifest.
        await File.WriteAllTextAsync(Path.Combine(_projectDir, "schema.sql"), $"""
            CREATE SCHEMA {_schema};

            CREATE TABLE {_schema}.widgets (
              id   bigint NOT NULL,
              name text,
              CONSTRAINT widgets_pkey PRIMARY KEY (id)
            );
            """, TestContext.Current.CancellationToken);

        var builder = NSchemaApplication.CreateBuilder();
        var configured = new PostgresPlugin().Configure(builder, new ConfigBlock("provider", "postgres", new Dictionary<string, ConfigValue>
        {
            ["connection_string"] = ConfigValue.OfString(_fixture.ConnectionString),
        }));
        configured.Succeeded.ShouldBeTrue();

        builder.AddDdlSchemas(_projectDir);
        using var app = builder.Build();

        // Act — a real plan + apply through the plugin-wired provider.
        var planResult = await app.Operations.Plan(new PlanArguments { Schemas = [_schema], Target = PlanTarget.Live }, TestContext.Current.CancellationToken);
        planResult.IsSuccess.ShouldBeTrue();
        await app.Operations.Apply(new ApplyArguments { Sql = planResult.Value!.Sql ?? new SqlPlan([]) }, TestContext.Current.CancellationToken);

        // Assert — the table really exists, read back via a fresh introspection.
        var live = await new PostgresSchemaProvider(_fixture.DataSource).GetSchema([_schema], TestContext.Current.CancellationToken);
        var table = live.Schemas.ShouldHaveSingleItem().Tables.ShouldHaveSingleItem();
        table.Name.ShouldBe("widgets");
        table.Columns.Select(column => column.Name).ShouldBe(["id", "name"]);
    }

    [Fact]
    public async Task Apply_ThenPlanAgain_ShowsNoChanges()
    {
        // Arrange — a schema carrying both grant kinds, since the owner's implicit self-grant only materializes
        // in the ACLs once a real GRANT runs; a phantom "revoke from the owner" on the second plan is the
        // round-trip drift this locks out.
        var role = $"role_{Guid.NewGuid():N}";
        await Exec($"""CREATE ROLE "{role}" """);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(_projectDir, "schema.sql"), $"""
                CREATE SCHEMA {_schema};
                GRANT USAGE ON SCHEMA {_schema} TO {role};

                CREATE TABLE {_schema}.widgets (
                  id bigint NOT NULL,
                  CONSTRAINT widgets_pkey PRIMARY KEY (id)
                );
                GRANT SELECT, INSERT ON {_schema}.widgets TO {role};
                """, TestContext.Current.CancellationToken);

            var builder = NSchemaApplication.CreateBuilder();
            new PostgresPlugin().Configure(builder, new ConfigBlock("provider", "postgres", new Dictionary<string, ConfigValue>
            {
                ["connection_string"] = ConfigValue.OfString(_fixture.ConnectionString),
            })).Succeeded.ShouldBeTrue();
            builder.AddDdlSchemas(_projectDir);
            using var app = builder.Build();

            // Act — apply the schema, then plan the same desired state again.
            var first = await app.Operations.Plan(new PlanArguments { Schemas = [_schema], Target = PlanTarget.Live }, TestContext.Current.CancellationToken);
            first.IsSuccess.ShouldBeTrue();
            (await app.Operations.Apply(new ApplyArguments { Sql = first.Value!.Sql ?? new SqlPlan([]) }, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

            var second = await app.Operations.Plan(new PlanArguments { Schemas = [_schema], Target = PlanTarget.Live }, TestContext.Current.CancellationToken);

            // Assert — a clean cycle plans no further changes.
            second.IsSuccess.ShouldBeTrue();
            second.Value!.HasChanges.ShouldBeFalse();
        }
        finally
        {
            await Exec($"""DROP SCHEMA IF EXISTS "{_schema}" CASCADE; DROP OWNED BY "{role}"; DROP ROLE "{role}" """);
        }
    }

    private async Task Exec(string sql)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
