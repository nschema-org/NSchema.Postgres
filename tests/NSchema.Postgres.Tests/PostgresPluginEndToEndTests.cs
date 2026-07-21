using NSchema.Model;
using NSchema.Operations;
using NSchema.Plugins;
using NSchema.Plugins.Model.Config;
using NSchema.Postgres.Sql;
using NSchema.Postgres.Tests.Fixtures;

namespace NSchema.Postgres.Tests;

/// <summary>
/// End-to-end proof that the <see cref="PostgresPlugin"/> manifest wires a fully working provider: it runs a real
/// migration THROUGH the plugin's <c>Configure</c> (not the direct <c>UsePostgres</c> API) against a real
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

        using var app = BuildApp();

        // Act — a real refresh + plan + apply through the plugin-wired provider.
        var plan = await Plan(app);
        (await app.Operations.Apply(new ApplyArguments { Plan = plan }, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        // Assert — the table really exists, read back via a fresh introspection.
        var live = await Introspect();
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

            using var app = BuildApp();

            // Act — apply the schema, then plan the same desired state again.
            var first = await Plan(app);
            (await app.Operations.Apply(new ApplyArguments { Plan = first }, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

            var second = await app.Operations.Plan(new PlanArguments(), TestContext.Current.CancellationToken);

            // Assert — a clean cycle plans no further changes.
            second.IsSuccess.ShouldBeTrue();
            second.Value!.HasChanges.ShouldBeFalse();
        }
        finally
        {
            await Exec($"""DROP SCHEMA IF EXISTS "{_schema}" CASCADE; DROP OWNED BY "{role}"; DROP ROLE "{role}" """);
        }
    }

    [Fact]
    public async Task Apply_WithChangeScript_BackfillsThenTightensToNotNull()
    {
        // Arrange — a live table that already holds rows, and a desired schema adding a NOT NULL column with no
        // default plus the SCRIPT that backfills it. The core decomposes the add (nullable add → backfill
        // → SET NOT NULL) and the provider must run the backfill SQL verbatim, so the apply succeeds against the
        // populated table.
        await Exec($"""
            CREATE SCHEMA "{_schema}";
            CREATE TABLE "{_schema}".widgets (id bigint NOT NULL, CONSTRAINT widgets_pkey PRIMARY KEY (id));
            INSERT INTO "{_schema}".widgets (id) VALUES (1), (2);
            """);

        await File.WriteAllTextAsync(Path.Combine(_projectDir, "schema.sql"), $"""
            CREATE SCHEMA {_schema};

            CREATE TABLE {_schema}.widgets (
              id     bigint NOT NULL,
              status text   NOT NULL,
              CONSTRAINT widgets_pkey PRIMARY KEY (id)
            );

            SCRIPT backfill RUN ON ADD COLUMN {_schema}.widgets.status AS $$
              UPDATE {_schema}.widgets SET status = 'active'
            $$;
            """, TestContext.Current.CancellationToken);

        using var app = BuildApp();

        // Act — refresh the recorded state from live, plan, and apply.
        var plan = await Plan(app);
        (await app.Operations.Apply(new ApplyArguments { Plan = plan }, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        // Assert — the column ended NOT NULL and every pre-existing row was backfilled.
        var live = await Introspect();
        var status = live.Schemas.ShouldHaveSingleItem().Tables.ShouldHaveSingleItem().Columns.Single(column => column.Name == "status");
        status.IsNullable.ShouldBeFalse();
        (await Scalar($"""SELECT string_agg(status, ',' ORDER BY id) FROM "{_schema}".widgets""")).ShouldBe("active,active");
    }

    /// <summary>Builds an app wired only through the plugin manifest, plus the ephemeral state planning requires.</summary>
    private NSchemaApplication BuildApp()
    {
        var builder = NSchemaApplication.CreateBuilder();
        var configured = new PostgresPlugin().Configure(builder, new PluginConfig("postgres", new Dictionary<AttributeKey, ConfigValue>
        {
            [new AttributeKey("connection_string")] = ConfigValue.OfString(_fixture.ConnectionString),
        }));
        configured.IsSuccess.ShouldBeTrue();

        builder.AddProjectSource(_projectDir);
        builder.UseEphemeralState();
        return builder.Build();
    }

    /// <summary>Puts the live schema on record, then computes the plan towards the project.</summary>
    private async Task<NSchema.Plan.Model.MigrationPlan> Plan(NSchemaApplication app)
    {
        (await app.Operations.Refresh(new RefreshArguments(), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        var planResult = await app.Operations.Plan(new PlanArguments(), TestContext.Current.CancellationToken);
        planResult.IsSuccess.ShouldBeTrue();
        return planResult.Value!.Plan.ShouldNotBeNull();
    }

    private async Task<Database> Introspect() =>
        await new PostgresDatabaseIntrospector(_fixture.DataSource)
            .GetDatabase(PlanningScope.To([new SqlIdentifier(_schema)]), TestContext.Current.CancellationToken);

    private async Task Exec(string sql)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> Scalar(string sql)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string?)await command.ExecuteScalarAsync();
    }
}
