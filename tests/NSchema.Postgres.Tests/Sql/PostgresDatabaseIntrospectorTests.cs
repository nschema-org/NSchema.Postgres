using Npgsql;
using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.Routines;
using NSchema.Model.Sequences;
using NSchema.Model.Tables;
using NSchema.Postgres.Sql;
using NSchema.Postgres.Tests.Fixtures;

namespace NSchema.Postgres.Tests.Sql;

[Collection("postgres")]
public sealed class PostgresDatabaseIntrospectorTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly NpgsqlDataSource _dataSource = fixture.DataSource;
    private readonly string _schema = $"test_{Guid.NewGuid():N}";
    private NpgsqlConnection _connection = null!;
    private PostgresDatabaseIntrospector _sut = null!;

    public async ValueTask InitializeAsync()
    {
        _connection = await _dataSource.OpenConnectionAsync();
        _sut = new PostgresDatabaseIntrospector(_dataSource);
        await Exec($"CREATE SCHEMA \"{_schema}\"");
    }

    public async ValueTask DisposeAsync()
    {
        await Exec($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE");
        await _connection.DisposeAsync();
    }

    private async Task Exec(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Reads the live schema scoped to the given schema names.</summary>
    private async Task<Database> Introspect(params string[] schemas) =>
        await _sut.GetDatabase(PlanningScope.To(schemas.Select(s => new SchemaAddress(s))), TestContext.Current.CancellationToken);

    // ── Schema / table structure ──────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_EmptySchema_ReturnsSchemaWithNoTables()
    {
        // Arrange
        // (schema created in InitializeAsync)

        // Act
        var model = await Introspect(_schema);

        // Assert
        model.Schemas.ShouldHaveSingleItem();
        model.Schemas[0].Name.ShouldBe(_schema);
        model.Schemas[0].Tables.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetDatabase_SingleTable_ReturnsTable()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id    INTEGER NOT NULL,
                email TEXT    NOT NULL
            )
            """);

        // Act
        var model = await Introspect(_schema);

        // Assert
        model.Schemas[0].Tables.ShouldHaveSingleItem();
        model.Schemas[0].Tables[0].Name.ShouldBe("users");
    }

    // ── Nullability ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_Columns_NullabilityMappedCorrectly()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id    INTEGER NOT NULL,
                email TEXT
            )
            """);

        // Act
        var cols = (await Introspect(_schema))
            .Schemas[0].Tables[0].Columns.ToDictionary(c => c.Name);

        // Assert
        cols["id"].IsNullable.ShouldBeFalse();
        cols["email"].IsNullable.ShouldBeTrue();
    }

    // ── Type mapping ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_Columns_StandardTypesMappedCorrectly()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".types_test (
                col_bool     BOOLEAN,
                col_smallint SMALLINT,
                col_int      INTEGER,
                col_bigint   BIGINT,
                col_real     REAL,
                col_double   DOUBLE PRECISION,
                col_numeric  NUMERIC(10, 3),
                col_char     CHAR(5),
                col_varchar  VARCHAR(100),
                col_text     TEXT,
                col_date     DATE,
                col_time     TIME,
                col_ts       TIMESTAMP,
                col_tstz     TIMESTAMPTZ,
                col_uuid     UUID,
                col_bytea    BYTEA
            )
            """);

        // Act
        var cols = (await Introspect(_schema))
            .Schemas[0].Tables[0].Columns.ToDictionary(c => c.Name);

        // Assert
        cols["col_bool"].Type.ShouldBe(SqlType.Boolean);
        cols["col_smallint"].Type.ShouldBe(SqlType.SmallInt);
        cols["col_int"].Type.ShouldBe(SqlType.Int);
        cols["col_bigint"].Type.ShouldBe(SqlType.BigInt);
        cols["col_real"].Type.ShouldBe(SqlType.Float);
        cols["col_double"].Type.ShouldBe(SqlType.Double);
        cols["col_numeric"].Type.ShouldBe(SqlType.Decimal(10, 3));
        cols["col_char"].Type.ShouldBe(SqlType.Char(5));
        cols["col_varchar"].Type.ShouldBe(SqlType.VarChar(100));
        cols["col_text"].Type.ShouldBe(SqlType.Text);
        cols["col_date"].Type.ShouldBe(SqlType.Date);
        cols["col_time"].Type.ShouldBe(SqlType.Time);
        cols["col_ts"].Type.ShouldBe(SqlType.DateTime);
        cols["col_tstz"].Type.ShouldBe(SqlType.DateTimeOffset);
        cols["col_uuid"].Type.ShouldBe(SqlType.Guid);
        cols["col_bytea"].Type.ShouldBe(SqlType.VarBinary());
    }

    [Fact]
    public async Task GetDatabase_CustomType_MapsToCustomSqlType()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id    INTEGER NOT NULL,
                email CITEXT  NOT NULL
            )
            """);

        // Act
        var emailCol = (await Introspect(_schema))
            .Schemas[0].Tables[0].Columns.Single(c => c.Name == "email");

        // Assert
        emailCol.Type.ShouldBe(SqlType.Custom("citext"));
    }

    // ── Identity & defaults ───────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_IdentityColumn_SetsIsIdentityAndClearsDefault()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id    INTEGER GENERATED ALWAYS AS IDENTITY,
                email TEXT NOT NULL
            )
            """);

        // Act
        var idCol = (await Introspect(_schema))
            .Schemas[0].Tables[0].Columns.Single(c => c.Name == "id");

        // Assert
        idCol.IsIdentity.ShouldBeTrue();
        idCol.DefaultExpression.ShouldBeNull();
    }

    [Fact]
    public async Task GetDatabase_ColumnDefault_CapturesExpression()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id     INTEGER NOT NULL,
                status TEXT DEFAULT 'active'
            )
            """);

        // Act
        var statusCol = (await Introspect(_schema))
            .Schemas[0].Tables[0].Columns.Single(c => c.Name == "status");

        // Assert
        statusCol.DefaultExpression.ShouldNotBeNull();
        statusCol.DefaultExpression!.Value.ShouldContain("active");
    }

    // ── Primary key ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_PrimaryKey_ReturnsPrimaryKey()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id INTEGER NOT NULL,
                CONSTRAINT pk_users PRIMARY KEY (id)
            )
            """);

        // Act
        var table = (await Introspect(_schema)).Schemas[0].Tables[0];

        // Assert
        table.PrimaryKey.ShouldNotBeNull();
        table.PrimaryKey!.Name.ShouldBe("pk_users");
        table.PrimaryKey.ColumnNames.ShouldBe(["id"]);
    }

    [Fact]
    public async Task GetDatabase_CompositePrimaryKey_ReturnsColumnsInOrder()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".order_items (
                order_id INTEGER NOT NULL,
                item_id  INTEGER NOT NULL,
                CONSTRAINT pk_order_items PRIMARY KEY (order_id, item_id)
            )
            """);

        // Act
        var pk = (await Introspect(_schema)).Schemas[0].Tables[0].PrimaryKey;

        // Assert
        pk.ShouldNotBeNull();
        pk!.ColumnNames.ShouldBe(["order_id", "item_id"]);
    }

    [Fact]
    public async Task GetDatabase_TableWithNoPrimaryKey_ReturnsNullPrimaryKey()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".events (
                name TEXT NOT NULL
            )
            """);

        // Act
        var table = (await Introspect(_schema)).Schemas[0].Tables[0];

        // Assert
        table.PrimaryKey.ShouldBeNull();
    }

    // ── Foreign keys ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_ForeignKey_ReturnsConstraint()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".organisations (
                id INTEGER NOT NULL CONSTRAINT pk_orgs PRIMARY KEY
            );
            CREATE TABLE "{_schema}".users (
                id     INTEGER NOT NULL CONSTRAINT pk_users PRIMARY KEY,
                org_id INTEGER NOT NULL,
                CONSTRAINT fk_users_org FOREIGN KEY (org_id)
                    REFERENCES "{_schema}".organisations (id)
            )
            """);

        // Act
        var fks = (await Introspect(_schema))
            .Schemas[0].Tables.Single(t => t.Name == "users").ForeignKeys;

        // Assert
        fks.ShouldNotBeNull();
        fks!.ShouldHaveSingleItem();
        fks[0].Name.ShouldBe("fk_users_org");
        fks[0].ColumnNames.ShouldBe(["org_id"]);
        fks[0].References.ShouldBe(new ObjectAddress(_schema, "organisations"));
        fks[0].ReferencedColumnNames.ShouldBe(["id"]);
        fks[0].OnDelete.ShouldBe(ReferentialAction.NoAction);
        fks[0].OnUpdate.ShouldBe(ReferentialAction.NoAction);
    }

    [Fact]
    public async Task GetDatabase_ForeignKeyOnDelete_MapsReferentialAction()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".organisations (
                id INTEGER NOT NULL CONSTRAINT pk_orgs PRIMARY KEY
            );
            CREATE TABLE "{_schema}".users (
                id     INTEGER NOT NULL CONSTRAINT pk_users PRIMARY KEY,
                org_id INTEGER NOT NULL,
                CONSTRAINT fk_users_org FOREIGN KEY (org_id)
                    REFERENCES "{_schema}".organisations (id)
                    ON DELETE CASCADE
                    ON UPDATE SET NULL
            )
            """);

        // Act
        var fk = (await Introspect(_schema))
            .Schemas[0].Tables.Single(t => t.Name == "users").ForeignKeys[0];

        // Assert
        fk.OnDelete.ShouldBe(ReferentialAction.Cascade);
        fk.OnUpdate.ShouldBe(ReferentialAction.SetNull);
    }

    [Fact]
    public async Task GetDatabase_TableWithNoForeignKeys_ReturnsEmptyForeignKeys()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".standalone (
                id INTEGER NOT NULL
            )
            """);

        // Act
        var table = (await Introspect(_schema)).Schemas[0].Tables[0];

        // Assert
        table.ForeignKeys.ShouldBeEmpty();
    }

    // ── Indexes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_Index_ReturnsIndex()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id    INTEGER NOT NULL,
                email TEXT    NOT NULL
            );
            CREATE INDEX ix_users_email ON "{_schema}".users (email)
            """);

        // Act
        var idx = (await Introspect(_schema))
            .Schemas[0].Tables[0].Indexes.Single();

        // Assert
        idx.Name.ShouldBe("ix_users_email");
        idx.Columns.Select(c => c.Column).ShouldBe(["email"]);
        idx.IsUnique.ShouldBeFalse();
    }

    [Fact]
    public async Task GetDatabase_UniqueIndex_SetsIsUniqueTrue()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id    INTEGER NOT NULL,
                email TEXT    NOT NULL
            );
            CREATE UNIQUE INDEX ix_users_email ON "{_schema}".users (email)
            """);

        // Act
        var idx = (await Introspect(_schema))
            .Schemas[0].Tables[0].Indexes.Single();

        // Assert
        idx.IsUnique.ShouldBeTrue();
    }

    [Fact]
    public async Task GetDatabase_CompositeIndex_ReturnsColumnsInOrder()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".events (
                id       INTEGER   NOT NULL,
                user_id  INTEGER   NOT NULL,
                happened TIMESTAMP NOT NULL
            );
            CREATE INDEX ix_events_user_time ON "{_schema}".events (user_id, happened)
            """);

        // Act
        var idx = (await Introspect(_schema))
            .Schemas[0].Tables[0].Indexes.Single();

        // Assert
        idx.Columns.Select(c => c.Column).ShouldBe(["user_id", "happened"]);
    }

    [Fact]
    public async Task GetDatabase_PrimaryKeyIndex_IsNotReturnedAsTableIndex()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id INTEGER NOT NULL CONSTRAINT pk_users PRIMARY KEY
            )
            """);

        // Act
        var table = (await Introspect(_schema)).Schemas[0].Tables[0];

        // Assert
        table.Indexes.ShouldBeEmpty();
    }

    // ── Unique constraints ────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_UniqueConstraint_ReturnsConstraint()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id    INTEGER NOT NULL,
                email TEXT    NOT NULL,
                CONSTRAINT uq_users_email UNIQUE (email)
            )
            """);

        // Act
        var table = (await Introspect(_schema)).Schemas[0].Tables[0];

        // Assert
        var unique = table.UniqueConstraints.ShouldHaveSingleItem();
        unique.Name.ShouldBe("uq_users_email");
        unique.ColumnNames.ShouldBe(["email"]);
    }

    [Fact]
    public async Task GetDatabase_CompositeUniqueConstraint_ReturnsColumnsInOrder()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".memberships (
                org_id  INTEGER NOT NULL,
                user_id INTEGER NOT NULL,
                CONSTRAINT uq_membership UNIQUE (org_id, user_id)
            )
            """);

        // Act
        var unique = (await Introspect(_schema))
            .Schemas[0].Tables[0].UniqueConstraints.Single();

        // Assert
        unique.ColumnNames.ShouldBe(["org_id", "user_id"]);
    }

    [Fact]
    public async Task GetDatabase_UniqueConstraint_IsNotReturnedAsTableIndex()
    {
        // Arrange — a unique constraint is backed by an index, but it should surface as a constraint, not an index.
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id    INTEGER NOT NULL,
                email TEXT    NOT NULL,
                CONSTRAINT uq_users_email UNIQUE (email)
            )
            """);

        // Act
        var table = (await Introspect(_schema)).Schemas[0].Tables[0];

        // Assert
        table.UniqueConstraints.ShouldHaveSingleItem();
        table.Indexes.ShouldBeEmpty();
    }

    // ── Check constraints ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_CheckConstraint_ReturnsConstraintWithExpression()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".accounts (
                id      INTEGER NOT NULL,
                balance INTEGER NOT NULL,
                CONSTRAINT ck_balance CHECK (balance >= 0)
            )
            """);

        // Act
        var check = (await Introspect(_schema))
            .Schemas[0].Tables[0].CheckConstraints.ShouldHaveSingleItem();

        // Assert — the "CHECK (...)" wrapper is stripped, leaving just the predicate.
        check.Name.ShouldBe("ck_balance");
        check.Expression.ShouldBe("balance >= 0");
    }

    // ── Constraint comments ───────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_PrimaryKeyComment_IsCaptured()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id INTEGER NOT NULL,
                CONSTRAINT pk_users PRIMARY KEY (id)
            );
            COMMENT ON CONSTRAINT pk_users ON "{_schema}".users IS 'the surrogate key';
            """);

        // Act
        var pk = (await Introspect(_schema)).Schemas[0].Tables[0].PrimaryKey;

        // Assert
        pk.ShouldNotBeNull();
        pk!.Comment.ShouldBe("the surrogate key");
    }

    [Fact]
    public async Task GetDatabase_UniqueConstraintComment_IsCaptured()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id    INTEGER NOT NULL,
                email TEXT    NOT NULL,
                CONSTRAINT uq_users_email UNIQUE (email)
            );
            COMMENT ON CONSTRAINT uq_users_email ON "{_schema}".users IS 'one account per email';
            """);

        // Act
        var unique = (await Introspect(_schema))
            .Schemas[0].Tables[0].UniqueConstraints.Single();

        // Assert
        unique.Comment.ShouldBe("one account per email");
    }

    [Fact]
    public async Task GetDatabase_CheckConstraintComment_IsCaptured()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".accounts (
                id      INTEGER NOT NULL,
                balance INTEGER NOT NULL,
                CONSTRAINT ck_balance CHECK (balance >= 0)
            );
            COMMENT ON CONSTRAINT ck_balance ON "{_schema}".accounts IS 'no overdrafts';
            """);

        // Act
        var check = (await Introspect(_schema))
            .Schemas[0].Tables[0].CheckConstraints.Single();

        // Assert
        check.Comment.ShouldBe("no overdrafts");
    }

    // ── Schema grants ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_SchemaGrants_ExcludeOwnerImplicitGrants()
    {
        // Arrange — granting USAGE to any role materializes nspacl, which includes the owner's implicit
        // self-grant. That must not surface, or it would read as drift against a desired schema that never
        // declares the owner's own access.
        var role = $"role_{Guid.NewGuid():N}";
        await Exec($"""CREATE ROLE "{role}" """);
        try
        {
            await Exec($"""GRANT USAGE ON SCHEMA "{_schema}" TO "{role}" """);

            // Act
            var grants = (await Introspect(_schema)).Schemas[0].Grants;

            // Assert
            grants.ShouldHaveSingleItem().Role.ShouldBe(role);
        }
        finally
        {
            await Exec($"""DROP OWNED BY "{role}"; DROP ROLE "{role}" """);
        }
    }

    // ── Table grants ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_TableGrants_ExcludeOwnerImplicitGrants()
    {
        // Arrange — the owner implicitly holds all privileges. Those must not surface as grants, or they would read
        // as drift against a desired schema that never declares the owner's own access.
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id INTEGER NOT NULL
            )
            """);

        // Act
        var table = (await Introspect(_schema)).Schemas[0].Tables[0];

        // Assert
        table.Grants.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetDatabase_TableGrants_ReturnsExplicitGrantToOtherRole()
    {
        // Arrange — an explicit grant to a non-owner role should be captured.
        var role = $"role_{Guid.NewGuid():N}";
        await Exec($"""CREATE ROLE "{role}" """);
        try
        {
            await Exec($"""
                CREATE TABLE "{_schema}".users (id INTEGER NOT NULL);
                GRANT SELECT, INSERT ON "{_schema}".users TO "{role}";
                """);

            // Act
            var grants = (await Introspect(_schema))
                .Schemas[0].Tables[0].Grants;

            // Assert
            var grant = grants.ShouldHaveSingleItem();
            grant.Role.ShouldBe(role);
            grant.Privileges.ShouldBe(TablePrivilege.Select | TablePrivilege.Insert);
        }
        finally
        {
            await Exec($"""DROP OWNED BY "{role}"; DROP ROLE "{role}" """);
        }
    }

    [Fact]
    public async Task GetDatabase_ForeignKeyComment_IsCaptured()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".organisations (
                id INTEGER NOT NULL CONSTRAINT pk_orgs PRIMARY KEY
            );
            CREATE TABLE "{_schema}".users (
                id     INTEGER NOT NULL CONSTRAINT pk_users PRIMARY KEY,
                org_id INTEGER NOT NULL,
                CONSTRAINT fk_users_org FOREIGN KEY (org_id) REFERENCES "{_schema}".organisations (id)
            );
            COMMENT ON CONSTRAINT fk_users_org ON "{_schema}".users IS 'owning organisation';
            """);

        // Act
        var fk = (await Introspect(_schema))
            .Schemas[0].Tables.Single(t => t.Name == "users").ForeignKeys[0];

        // Assert
        fk.Comment.ShouldBe("owning organisation");
    }

    // ── Views ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_View_ReturnsViewWithCanonicalDefinition()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (id INTEGER NOT NULL, active BOOLEAN NOT NULL);
            CREATE VIEW "{_schema}".active_users AS SELECT id FROM "{_schema}".users WHERE active;
            """);

        // Act
        var view = (await Introspect(_schema))
            .Schemas[0].Views.ShouldHaveSingleItem();

        // Assert — body is the DB's canonical form (no trailing ';'), so apply → plan round-trips clean.
        view.Name.ShouldBe("active_users");
        view.Body.Value.ShouldContain("SELECT");
        view.Body.Value.ShouldContain("id");
        view.Body.Value.TrimEnd().ShouldNotEndWith(";");
    }

    [Fact]
    public async Task GetDatabase_View_IsNotReturnedAsTable()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (id INTEGER NOT NULL);
            CREATE VIEW "{_schema}".u AS SELECT id FROM "{_schema}".users;
            """);

        // Act
        var schema = (await Introspect(_schema)).Schemas[0];

        // Assert — the view must not leak into the table set.
        schema.Tables.Select(t => t.Name).ShouldBe(["users"]);
        schema.Views.ShouldHaveSingleItem().Name.ShouldBe("u");
    }

    [Fact]
    public async Task GetDatabase_ViewComment_IsCaptured()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (id INTEGER NOT NULL);
            CREATE VIEW "{_schema}".u AS SELECT id FROM "{_schema}".users;
            COMMENT ON VIEW "{_schema}".u IS 'just the ids';
            """);

        // Act
        var view = (await Introspect(_schema))
            .Schemas[0].Views.ShouldHaveSingleItem();

        // Assert
        view.Comment.ShouldBe("just the ids");
    }

    [Fact]
    public async Task GetDatabase_ViewDependencies_CaptureUnderlyingTable()
    {
        // Arrange
        await Exec($"""
            CREATE TABLE "{_schema}".users (id INTEGER NOT NULL);
            CREATE VIEW "{_schema}".u AS SELECT id FROM "{_schema}".users;
            """);

        // Act
        var view = (await Introspect(_schema))
            .Schemas[0].Views.ShouldHaveSingleItem();

        // Assert
        view.DependsOn.ShouldHaveSingleItem().ShouldBe(new ObjectAddress(_schema, "users"));
    }

    [Fact]
    public async Task GetDatabase_ViewOnView_CapturesViewDependency()
    {
        // Arrange — a view reading another view must record the view-to-view dependency for drop ordering.
        await Exec($"""
            CREATE TABLE "{_schema}".users (id INTEGER NOT NULL, active BOOLEAN NOT NULL);
            CREATE VIEW "{_schema}".active_users AS SELECT id FROM "{_schema}".users WHERE active;
            CREATE VIEW "{_schema}".active_ids AS SELECT id FROM "{_schema}".active_users;
            """);

        // Act
        var derived = (await Introspect(_schema))
            .Schemas[0].Views.Single(v => v.Name == "active_ids");

        // Assert
        derived.DependsOn.ShouldHaveSingleItem().ShouldBe(new ObjectAddress(_schema, "active_users"));
    }

    // ── Enums ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_Enum_ReturnsValuesInCreationOrder()
    {
        // Arrange
        await Exec($"""CREATE TYPE "{_schema}".order_status AS ENUM ('draft', 'active', 'archived')""");

        // Act
        var enumType = (await Introspect(_schema))
            .Schemas[0].Enums.ShouldHaveSingleItem();

        // Assert — order is the type's comparison order, not alphabetical.
        enumType.Name.ShouldBe("order_status");
        enumType.Values.ShouldBe(["draft", "active", "archived"]);
    }

    [Fact]
    public async Task GetDatabase_EnumComment_IsCaptured()
    {
        // Arrange
        await Exec($"""
            CREATE TYPE "{_schema}".order_status AS ENUM ('draft');
            COMMENT ON TYPE "{_schema}".order_status IS 'order lifecycle';
            """);

        // Act
        var enumType = (await Introspect(_schema))
            .Schemas[0].Enums.ShouldHaveSingleItem();

        // Assert
        enumType.Comment.ShouldBe("order lifecycle");
    }

    [Fact]
    public async Task GetDatabase_EnumColumn_MappedAsCustomType_PreservingSchema()
    {
        // Arrange — a column typed as a user-defined enum comes back through MapSqlType's fall-through; its
        // udt_schema is preserved so a type in another schema round-trips.
        await Exec($"""
            CREATE TYPE "{_schema}".order_status AS ENUM ('draft', 'active');
            CREATE TABLE "{_schema}".orders (status "{_schema}".order_status NOT NULL);
            """);

        // Act
        var column = (await Introspect(_schema))
            .Schemas[0].Tables.ShouldHaveSingleItem().Columns.ShouldHaveSingleItem();

        // Assert
        column.Type.ShouldBe(SqlType.Custom(_schema, "order_status"));
    }

    [Fact]
    public async Task GetDatabase_BuiltInFallthroughColumn_MappedUnqualified()
    {
        // Arrange — jsonb also hits MapSqlType's fall-through, but its pg_catalog udt_schema
        // must collapse so the type reads as declared.
        await Exec($"""CREATE TABLE "{_schema}".events (payload jsonb NOT NULL);""");

        // Act
        var column = (await Introspect(_schema))
            .Schemas[0].Tables.ShouldHaveSingleItem().Columns.ShouldHaveSingleItem();

        // Assert
        column.Type.ShouldBe(SqlType.Custom("jsonb"));
    }

    // ── Sequences ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_BareSequence_AllOptionsNull()
    {
        // Arrange — the anti-phantom-drift gate: a bare sequence must introspect to all-null options so it
        // compares equal to a bare "CREATE SEQUENCE" declaration in the desired schema.
        await Exec($"""CREATE SEQUENCE "{_schema}".order_id""");

        // Act
        var sequence = (await Introspect(_schema))
            .Schemas[0].Sequences.ShouldHaveSingleItem();

        // Assert
        sequence.Name.ShouldBe("order_id");
        sequence.Options.ShouldBe(new SequenceOptions());
    }

    [Fact]
    public async Task GetDatabase_DescendingSequence_OnlyIncrementKept()
    {
        // Arrange — a descending sequence's defaults (max -1, min = type min, start = max) must also fold to null.
        await Exec($"""CREATE SEQUENCE "{_schema}".countdown INCREMENT -1""");

        // Act
        var sequence = (await Introspect(_schema))
            .Schemas[0].Sequences.ShouldHaveSingleItem();

        // Assert
        sequence.Options.ShouldBe(new SequenceOptions(IncrementBy: -1));
    }

    [Fact]
    public async Task GetDatabase_FullyOptionedSequence_OptionsCaptured()
    {
        // Arrange — start deliberately differs from minvalue so it is not folded away.
        await Exec($"""CREATE SEQUENCE "{_schema}".order_id AS integer INCREMENT 5 MINVALUE 10 MAXVALUE 1000 START 20 CACHE 10 CYCLE""");

        // Act
        var sequence = (await Introspect(_schema))
            .Schemas[0].Sequences.ShouldHaveSingleItem();

        // Assert
        sequence.Options.ShouldBe(new SequenceOptions(
            SqlType.Int, StartWith: 20, IncrementBy: 5, MinValue: 10, MaxValue: 1000, Cache: 10, Cycle: true));
    }

    [Fact]
    public async Task GetDatabase_IdentityOwnedSequence_IsExcluded()
    {
        // Arrange — an identity column's backing sequence is the column's implementation detail, not a
        // standalone sequence. The identity options must still round-trip through the columns query.
        await Exec($"""
            CREATE TABLE "{_schema}".users (
                id BIGINT GENERATED ALWAYS AS IDENTITY (START WITH 100) PRIMARY KEY
            )
            """);

        // Act
        var schema = (await Introspect(_schema)).Schemas[0];

        // Assert
        schema.Sequences.ShouldBeEmpty();
        var id = schema.Tables.ShouldHaveSingleItem().Columns.ShouldHaveSingleItem();
        id.IsIdentity.ShouldBeTrue();
        id.IdentityOptions!.StartWith.ShouldBe(100);
    }

    [Fact]
    public async Task GetDatabase_SerialOwnedSequence_IsExcluded()
    {
        // Arrange — serial's sequence is owned by the column (pg_depend deptype 'a').
        await Exec($"""CREATE TABLE "{_schema}".users (id SERIAL)""");

        // Act
        var schema = (await Introspect(_schema)).Schemas[0];

        // Assert
        schema.Sequences.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetDatabase_SequenceComment_IsCaptured()
    {
        // Arrange
        await Exec($"""
            CREATE SEQUENCE "{_schema}".order_id;
            COMMENT ON SEQUENCE "{_schema}".order_id IS 'order numbers';
            """);

        // Act
        var sequence = (await Introspect(_schema))
            .Schemas[0].Sequences.ShouldHaveSingleItem();

        // Assert
        sequence.Comment.ShouldBe("order numbers");
    }

    // ── Functions & procedures ────────────────────────────────────────────────

    [Fact]
    public async Task GetDatabase_Function_ReturnsArgumentsAndDefinition()
    {
        // Arrange
        await Exec($"""
            CREATE FUNCTION "{_schema}".add_numbers(a integer, b integer)
            RETURNS integer LANGUAGE sql AS $$ SELECT a + b $$
            """);

        // Act
        var function = (await Introspect(_schema))
            .Schemas[0].Routines.ShouldHaveSingleItem();

        // Assert — both parts are the DB's canonical form: the argument list as pg_get_function_arguments renders
        // it, and the definition starting right after the CREATE header (at RETURNS).
        function.Name.ShouldBe("add_numbers");
        function.Arguments.ShouldBe("a integer, b integer");
        function.Definition.Value.ShouldStartWith("RETURNS integer");
        function.Definition.Value.ShouldContain("LANGUAGE sql");
        function.Definition.Value.ShouldContain("SELECT a + b");
    }

    [Fact]
    public async Task GetDatabase_FunctionWithParenthesisedDefault_HeaderStripSurvives()
    {
        // Arrange — a default containing parentheses would defeat any "cut at the first ')'" parsing; the header
        // strip must be driven by the rendered argument list instead.
        await Exec($"""
            CREATE FUNCTION "{_schema}".pad(value text DEFAULT repeat('x', 3))
            RETURNS text LANGUAGE sql AS $$ SELECT value $$
            """);

        // Act
        var function = (await Introspect(_schema))
            .Schemas[0].Routines.ShouldHaveSingleItem();

        // Assert
        function.Arguments.Value.ShouldStartWith("value text DEFAULT repeat(");
        function.Definition.Value.ShouldStartWith("RETURNS text");
    }

    [Fact]
    public async Task GetDatabase_QuotedFunctionName_HeaderStripSurvives()
    {
        // Arrange — a mixed-case name is quoted in the pg_get_functiondef header; the strip must match that form.
        await Exec($"""CREATE FUNCTION "{_schema}"."GetAnswer"() RETURNS integer LANGUAGE sql AS $$ SELECT 42 $$""");

        // Act
        var function = (await Introspect(_schema))
            .Schemas[0].Routines.ShouldHaveSingleItem();

        // Assert
        function.Name.ShouldBe("GetAnswer");
        function.Arguments.ShouldBe("");
        function.Definition.Value.ShouldStartWith("RETURNS integer");
    }

    [Fact]
    public async Task GetDatabase_FunctionComment_IsCaptured()
    {
        // Arrange
        await Exec($"""
            CREATE FUNCTION "{_schema}".answer() RETURNS integer LANGUAGE sql AS $$ SELECT 42 $$;
            COMMENT ON FUNCTION "{_schema}".answer IS 'the answer';
            """);

        // Act
        var function = (await Introspect(_schema))
            .Schemas[0].Routines.ShouldHaveSingleItem();

        // Assert
        function.Comment.ShouldBe("the answer");
    }

    [Fact]
    public async Task GetDatabase_Procedure_ReturnedAsProcedureNotFunction()
    {
        // Arrange
        await Exec($"""CREATE PROCEDURE "{_schema}".noop(a integer) LANGUAGE sql AS $$ SELECT 1 $$""");

        // Act
        var schema = (await Introspect(_schema)).Schemas[0];

        // Assert — prokind is carried as Routine.Kind; a procedure must be tagged Procedure, not Function.
        var procedure = schema.Routines.ShouldHaveSingleItem();
        procedure.RoutineKind.ShouldBe(RoutineKind.Procedure);
        procedure.Name.ShouldBe("noop");
        procedure.Arguments.ShouldBe("a integer");
        procedure.Definition.Value.ShouldStartWith("LANGUAGE sql");
        procedure.Definition.Value.ShouldContain("SELECT 1");
    }

    [Fact]
    public async Task GetDatabase_ProcedureComment_IsCaptured()
    {
        // Arrange
        await Exec($"""
            CREATE PROCEDURE "{_schema}".noop() LANGUAGE sql AS $$ SELECT 1 $$;
            COMMENT ON PROCEDURE "{_schema}".noop IS 'does nothing';
            """);

        // Act
        var procedure = (await Introspect(_schema))
            .Schemas[0].Routines.ShouldHaveSingleItem();

        // Assert
        procedure.RoutineKind.ShouldBe(RoutineKind.Procedure);
        procedure.Comment.ShouldBe("does nothing");
    }

    [Fact]
    public async Task GetDatabase_ExtensionFunctions_AreExcluded()
    {
        // Arrange — the fixture enables citext in public, which installs dozens of support functions. They are the
        // extension's implementation detail and must not surface, or they would read as drift to drop.
        // (Nothing else in the suite creates routines in public.)

        // Act
        var publicSchema = (await Introspect("public"))
            .Schemas.Single(s => s.Name == "public");

        // Assert
        publicSchema.Routines.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetDatabase_Aggregate_IsNotReturnedAsFunction()
    {
        // Arrange — an aggregate is a pg_proc row too (prokind 'a'), but it is not part of the model.
        await Exec($"""CREATE AGGREGATE "{_schema}".int_sum (integer) (sfunc = int4pl, stype = integer)""");

        // Act
        var schema = (await Introspect(_schema)).Schemas[0];

        // Assert
        schema.Routines.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetDatabase_Extensions_AreReportedAtRootWithVersion()
    {
        // Arrange — the fixture enables citext database-wide; extensions are global, so a schema-scoped read still
        // surfaces them at the root. plpgsql (the always-present default) is excluded.

        // Act
        var schema = await Introspect(_schema);

        // Assert
        var citext = schema.Extensions.Single(e => e.Name == "citext");
        citext.Version.ShouldNotBeNull();
        schema.Extensions.ShouldNotContain(e => e.Name == "plpgsql");
    }

    // ── Same table name across schemas ────────────────────────────────────────

    // Regression: the columns query joined pg_class on relname alone (not namespace), so a table name shared by
    // two schemas matched both pg_class rows and fanned every column row out once per schema — columns appeared
    // duplicated in each table. Each table must report only its own columns.
    [Fact]
    public async Task GetDatabase_SameTableNameInDifferentSchemas_DoesNotDuplicateColumns()
    {
        // Arrange
        var other = $"test_{Guid.NewGuid():N}";
        await Exec($"CREATE SCHEMA \"{other}\"");
        try
        {
            await Exec($"""
                CREATE TABLE "{_schema}".users (
                    id   INTEGER NOT NULL,
                    name TEXT    NOT NULL
                )
                """);
            await Exec($"""
                CREATE TABLE "{other}".users (
                    code   INTEGER NOT NULL,
                    region TEXT    NOT NULL
                )
                """);

            // Act
            var model = await Introspect(_schema, other);

            // Assert
            var primary = model.Schemas.Single(s => s.Name == _schema).Tables.Single(t => t.Name == "users");
            var secondary = model.Schemas.Single(s => s.Name == other).Tables.Single(t => t.Name == "users");
            primary.Columns.Select(c => c.Name).ShouldBe(["id", "name"]);
            secondary.Columns.Select(c => c.Name).ShouldBe(["code", "region"]);
        }
        finally
        {
            await Exec($"DROP SCHEMA IF EXISTS \"{other}\" CASCADE");
        }
    }
}
