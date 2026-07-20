using Npgsql;
using NSchema.Plan.Model;
using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.CompositeTypes;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Domains;
using NSchema.Plan.Model.Enums;
using NSchema.Plan.Model.Extensions;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Routines;
using NSchema.Plan.Model.Schemas;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Triggers;
using NSchema.Plan.Model.Views;
using NSchema.Postgres.Tests.Fixtures;

namespace NSchema.Postgres.Tests.Sql;

[Collection("postgres")]
public sealed class PostgresSqlGeneratorTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly NpgsqlDataSource _dataSource = fixture.DataSource;
    private readonly string _schema = $"test_{Guid.NewGuid():N}";
    private NpgsqlConnection _conn = null!;
    private PostgresSqlGenerator _generator = null!;
    private StatementRunner _executor = null!;

    public async ValueTask InitializeAsync()
    {
        _conn = await _dataSource.OpenConnectionAsync();
        _generator = new PostgresSqlGenerator();
        _executor = new StatementRunner(_dataSource);
        await Exec($"""CREATE SCHEMA "{_schema}" """);
    }

    public async ValueTask DisposeAsync()
    {
        await Exec($"""DROP SCHEMA IF EXISTS "{_schema}" CASCADE""");
        await _conn.DisposeAsync();
    }

    // ── Schema operations ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSchema_CreatesSchemaInDatabase()
    {
        // Arrange
        var name = $"test_{Guid.NewGuid():N}";

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateSchema(name)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.schemata WHERE schema_name = '{name}'");
        exists.ShouldBeTrue();

        await Exec($"""DROP SCHEMA "{name}" CASCADE""");
    }

    [Fact]
    public async Task DropSchema_RemovesSchemaFromDatabase()
    {
        // Arrange
        var name = $"test_{Guid.NewGuid():N}";
        await Exec($"""CREATE SCHEMA "{name}" """);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new DropSchema(name)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.schemata WHERE schema_name = '{name}'");
        exists.ShouldBeFalse();
    }

    [Fact]
    public async Task DropSchema_AfterDroppingItsTable_RemovesANonEmptySchema()
    {
        // Destroy now drops a schema's objects explicitly before the schema itself, so DROP SCHEMA no longer needs
        // CASCADE. Prove the sequence works against a real database: a schema holding a table is torn down by a
        // DROP TABLE followed by a plain (non-cascading) DROP SCHEMA.
        var name = $"test_{Guid.NewGuid():N}";
        await Exec($"""CREATE SCHEMA "{name}" """);
        await Exec($"""CREATE TABLE "{name}"."widgets" (id integer)""");

        // Act
        await _executor.Execute(
            _generator.Generate(new MigrationPlan([new DropTable(name, "widgets"), new DropSchema(name)], [], [])),
            TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.schemata WHERE schema_name = '{name}'");
        exists.ShouldBeFalse();
    }

    [Fact]
    public async Task RenameSchema_RenamesSchemaInDatabase()
    {
        // Arrange
        var oldName = $"test_{Guid.NewGuid():N}";
        var newName = $"test_{Guid.NewGuid():N}";
        await Exec($"""CREATE SCHEMA "{oldName}" """);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new RenameSchema(oldName, newName)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var oldExists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.schemata WHERE schema_name = '{oldName}'");
        var newExists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.schemata WHERE schema_name = '{newName}'");
        oldExists.ShouldBeFalse();
        newExists.ShouldBeTrue();

        await Exec($"""DROP SCHEMA "{newName}" CASCADE""");
    }

    // ── Table operations ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTable_CreatesTableInDatabase()
    {
        // Arrange
        var table = new Table("users",
            Columns: [new Column("id", SqlType.BigInt, IsNullable: false)]);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateTable(_schema, table)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.tables WHERE table_schema = '{_schema}' AND table_name = 'users'");
        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateTable_WithPrimaryKey_CreatesPrimaryKeyConstraint()
    {
        // Arrange
        var table = new Table("orders",
            PrimaryKey: new PrimaryKey("pk_orders", ["id"]), Columns: [new Column("id", SqlType.BigInt, IsNullable: false)]);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateTable(_schema, table)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.table_constraints WHERE table_schema = '{_schema}' AND table_name = 'orders' AND constraint_type = 'PRIMARY KEY' AND constraint_name = 'pk_orders'");
        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task DropTable_RemovesTableFromDatabase()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."products" (id integer)""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new DropTable(_schema, "products")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.tables WHERE table_schema = '{_schema}' AND table_name = 'products'");
        exists.ShouldBeFalse();
    }

    [Fact]
    public async Task RenameTable_RenamesTableInDatabase()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."old_name" (id integer)""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new RenameTable(_schema, "old_name", "new_name")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var oldExists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.tables WHERE table_schema = '{_schema}' AND table_name = 'old_name'");
        var newExists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.tables WHERE table_schema = '{_schema}' AND table_name = 'new_name'");
        oldExists.ShouldBeFalse();
        newExists.ShouldBeTrue();
    }

    // ── Column operations ─────────────────────────────────────────────────────

    [Fact]
    public async Task AddColumn_AddsColumnToTable()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer)""");
        var column = new Column("name", SqlType.VarChar(100), IsNullable: false);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new AddColumn(_schema, "items", column)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.columns WHERE table_schema = '{_schema}' AND table_name = 'items' AND column_name = 'name'");
        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task DropColumn_RemovesColumnFromTable()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, name text)""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new DropColumn(_schema, "items", new Column("name", SqlType.Text))], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.columns WHERE table_schema = '{_schema}' AND table_name = 'items' AND column_name = 'name'");
        exists.ShouldBeFalse();
    }

    [Fact]
    public async Task RenameColumn_RenamesColumnInTable()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, old_col text)""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new RenameColumn(_schema, "items", "old_col", "new_col")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var oldExists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.columns WHERE table_schema = '{_schema}' AND table_name = 'items' AND column_name = 'old_col'");
        var newExists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.columns WHERE table_schema = '{_schema}' AND table_name = 'items' AND column_name = 'new_col'");
        oldExists.ShouldBeFalse();
        newExists.ShouldBeTrue();
    }

    [Fact]
    public async Task AlterColumnType_ChangesColumnDataType()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, value integer)""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new AlterColumnType(_schema, "items", "value", SqlType.Int, SqlType.BigInt)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var dataType = await ScalarString(
            $"SELECT data_type FROM information_schema.columns WHERE table_schema = '{_schema}' AND table_name = 'items' AND column_name = 'value'");
        dataType.ShouldBe("bigint");
    }

    [Fact]
    public async Task AlterColumnNullability_MakesColumnNotNull()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, name text)""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new AlterColumnNullability(_schema, "items", "name", OldNullable: true, NewNullable: false)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var isNullable = await ScalarString(
            $"SELECT is_nullable FROM information_schema.columns WHERE table_schema = '{_schema}' AND table_name = 'items' AND column_name = 'name'");
        isNullable.ShouldBe("NO");
    }

    [Fact]
    public async Task AlterColumnNullability_MakesColumnNullable()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, name text NOT NULL)""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new AlterColumnNullability(_schema, "items", "name", OldNullable: false, NewNullable: true)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var isNullable = await ScalarString(
            $"SELECT is_nullable FROM information_schema.columns WHERE table_schema = '{_schema}' AND table_name = 'items' AND column_name = 'name'");
        isNullable.ShouldBe("YES");
    }

    [Fact]
    public async Task SetColumnDefault_SetsDefaultExpression()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, quantity integer)""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new SetColumnDefault(_schema, "items", "quantity", null, "0")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var hasDefault = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.columns WHERE table_schema = '{_schema}' AND table_name = 'items' AND column_name = 'quantity' AND column_default IS NOT NULL");
        hasDefault.ShouldBeTrue();
    }

    [Fact]
    public async Task SetColumnDefault_DropsDefaultExpression()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, quantity integer DEFAULT 0)""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new SetColumnDefault(_schema, "items", "quantity", "0", null)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var hasDefault = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.columns WHERE table_schema = '{_schema}' AND table_name = 'items' AND column_name = 'quantity' AND column_default IS NOT NULL");
        hasDefault.ShouldBeFalse();
    }

    [Fact]
    public async Task RoundTrip_GeneratedColumn_IntrospectsAsGeneratedNotDefault()
    {
        // Arrange — a stored generated column applied via CREATE TABLE must read back as generated, with the
        // expression in GeneratedExpression and no DefaultExpression (the two are mutually exclusive).
        var table = new Table("boxes", Columns:
        [
            new Column("w", SqlType.Int, IsNullable: false),
            new Column("h", SqlType.Int, IsNullable: false),
            new Column("area", SqlType.Int, GeneratedExpression: "w * h"),
        ]);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateTable(_schema, table)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var provider = new PostgresSchemaProvider(_dataSource);
        var area = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].Tables[0].Columns.Single(c => c.Name == "area");
        area.GeneratedExpression.ShouldNotBeNull();
        area.GeneratedExpression!.ShouldContain("w * h");
        area.DefaultExpression.ShouldBeNull();
    }

    [Fact]
    public async Task SetColumnGenerated_ChangesAndDropsExpression()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}".boxes (w int, h int, area int GENERATED ALWAYS AS (w * h) STORED)""");
        var provider = new PostgresSchemaProvider(_dataSource);

        // Act — change the expression (SET EXPRESSION)...
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new SetColumnGenerated(_schema, "boxes", "area", "w * h", "w + h")], [], [])), TestContext.Current.CancellationToken);
        var changed = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].Tables[0].Columns.Single(c => c.Name == "area");

        // ...then drop it (DROP EXPRESSION), making it a plain column.
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new SetColumnGenerated(_schema, "boxes", "area", "w + h", null)], [], [])), TestContext.Current.CancellationToken);
        var dropped = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].Tables[0].Columns.Single(c => c.Name == "area");

        // Assert
        changed.GeneratedExpression!.ShouldContain("w + h");
        dropped.GeneratedExpression.ShouldBeNull();
    }

    // ── Primary key operations ────────────────────────────────────────────────

    [Fact]
    public async Task AddPrimaryKey_AddsConstraintToTable()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer NOT NULL)""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new AddPrimaryKey(_schema, "items", new PrimaryKey("pk_items", ["id"]))], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.table_constraints WHERE table_schema = '{_schema}' AND table_name = 'items' AND constraint_type = 'PRIMARY KEY' AND constraint_name = 'pk_items'");
        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task DropPrimaryKey_RemovesConstraintFromTable()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer NOT NULL, CONSTRAINT pk_items PRIMARY KEY (id))""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new DropPrimaryKey(_schema, "items", "pk_items")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.table_constraints WHERE table_schema = '{_schema}' AND table_name = 'items' AND constraint_type = 'PRIMARY KEY'");
        exists.ShouldBeFalse();
    }

    // ── Foreign key operations ────────────────────────────────────────────────

    [Fact]
    public async Task AddForeignKey_AddsReferentialConstraint()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."parents" (id integer NOT NULL, CONSTRAINT pk_parents PRIMARY KEY (id))""");
        await Exec($"""CREATE TABLE "{_schema}"."children" (id integer NOT NULL, parent_id integer)""");
        var fk = new ForeignKey("fk_children_parent", ["parent_id"], _schema, "parents", ["id"]);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new AddForeignKey(_schema, "children", fk)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.referential_constraints WHERE constraint_schema = '{_schema}' AND constraint_name = 'fk_children_parent'");
        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task DropForeignKey_RemovesReferentialConstraint()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."parents" (id integer NOT NULL, CONSTRAINT pk_parents PRIMARY KEY (id))""");
        await Exec($"""CREATE TABLE "{_schema}"."children" (id integer, parent_id integer, CONSTRAINT fk_children_parent FOREIGN KEY (parent_id) REFERENCES "{_schema}"."parents" (id))""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new DropForeignKey(_schema, "children", "fk_children_parent")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.referential_constraints WHERE constraint_schema = '{_schema}' AND constraint_name = 'fk_children_parent'");
        exists.ShouldBeFalse();
    }

    // ── Unique constraint operations ──────────────────────────────────────────

    [Fact]
    public async Task AddUniqueConstraint_AddsConstraintToTable()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, code text)""");
        var unique = new UniqueConstraint("uq_items_code", ["code"]);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new AddUniqueConstraint(_schema, "items", unique)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.table_constraints WHERE table_schema = '{_schema}' AND table_name = 'items' AND constraint_type = 'UNIQUE' AND constraint_name = 'uq_items_code'");
        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task DropUniqueConstraint_RemovesConstraintFromTable()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, code text, CONSTRAINT uq_items_code UNIQUE (code))""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new DropUniqueConstraint(_schema, "items", "uq_items_code")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.table_constraints WHERE table_schema = '{_schema}' AND table_name = 'items' AND constraint_type = 'UNIQUE'");
        exists.ShouldBeFalse();
    }

    // ── Check constraint operations ───────────────────────────────────────────

    [Fact]
    public async Task AddCheckConstraint_AddsConstraintToTable()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."accounts" (id integer, balance integer)""");
        var check = new CheckConstraint("ck_balance", "balance >= 0");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new AddCheckConstraint(_schema, "accounts", check)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.table_constraints WHERE table_schema = '{_schema}' AND table_name = 'accounts' AND constraint_type = 'CHECK' AND constraint_name = 'ck_balance'");
        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task DropCheckConstraint_RemovesConstraintFromTable()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."accounts" (id integer, balance integer, CONSTRAINT ck_balance CHECK (balance >= 0))""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new DropCheckConstraint(_schema, "accounts", "ck_balance")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.table_constraints WHERE table_schema = '{_schema}' AND table_name = 'accounts' AND constraint_name = 'ck_balance'");
        exists.ShouldBeFalse();
    }

    // ── Exclusion constraint operations ───────────────────────────────────────

    [Fact]
    public async Task AddExclusionConstraint_MultiElement_RoundTripsThroughIntrospection()
    {
        // Arrange — the canonical "no overlapping booking of the same room": a scalar `=` plus a range `&&`.
        // The scalar element in a gist index needs btree_gist (a contrib extension shipped with the image).
        await Exec("CREATE EXTENSION IF NOT EXISTS btree_gist");
        await Exec($"""CREATE TABLE "{_schema}".bookings (room integer, during tstzrange)""");
        var exclusion = new ExclusionConstraint("no_overlap",
            [new ExclusionElement("room", "="), new ExclusionElement("during", "&&")], Method: "gist", Predicate: "room > 0");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new AddExclusionConstraint(_schema, "bookings", exclusion)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var provider = new PostgresSchemaProvider(_dataSource);
        var introspected = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].Tables[0].ExclusionConstraints.ShouldHaveSingleItem();
        introspected.Name.ShouldBe("no_overlap");
        introspected.Method.ShouldBe("gist");
        introspected.Predicate.ShouldNotBeNull();
        introspected.Predicate!.ShouldContain("room > 0");
        introspected.Elements.Select(e => (e.Expression, e.Operator, e.IsExpression))
            .ShouldBe([("room", "=", false), ("during", "&&", false)]);
    }

    [Fact]
    public async Task AddExclusionConstraint_ExpressionElement_RoundTripsThroughIntrospection()
    {
        // Arrange — an expression element (a computed range) excluded with &&. No btree_gist needed.
        await Exec($"""CREATE TABLE "{_schema}".events (starts timestamptz, ends timestamptz)""");
        var exclusion = new ExclusionConstraint("no_clash",
            [new ExclusionElement("tstzrange(starts, ends)", "&&", IsExpression: true)], Method: "gist");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new AddExclusionConstraint(_schema, "events", exclusion)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var provider = new PostgresSchemaProvider(_dataSource);
        var element = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].Tables[0].ExclusionConstraints.ShouldHaveSingleItem().Elements.ShouldHaveSingleItem();
        element.IsExpression.ShouldBeTrue();
        element.Expression.ShouldContain("tstzrange");
        element.Operator.ShouldBe("&&");
    }

    // ── Constraint comments ───────────────────────────────────────────────────

    [Fact]
    public async Task SetConstraintComment_SetsCommentOnConstraint()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, code text, CONSTRAINT uq_items_code UNIQUE (code))""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new SetConstraintComment(_schema, "items", "uq_items_code", null, "one row per code")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var comment = await ScalarString(
            $"SELECT obj_description(oid, 'pg_constraint') FROM pg_constraint WHERE conname = 'uq_items_code' AND connamespace = '{_schema}'::regnamespace");
        comment.ShouldBe("one row per code");
    }

    [Fact]
    public async Task SetConstraintComment_ClearsCommentWhenNull()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, code text, CONSTRAINT uq_items_code UNIQUE (code))""");
        await Exec($"""COMMENT ON CONSTRAINT uq_items_code ON "{_schema}"."items" IS 'old comment'""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new SetConstraintComment(_schema, "items", "uq_items_code", "old comment", null)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var hasComment = await ScalarBool(
            $"SELECT obj_description(oid, 'pg_constraint') IS NOT NULL FROM pg_constraint WHERE conname = 'uq_items_code' AND connamespace = '{_schema}'::regnamespace");
        hasComment.ShouldBeFalse();
    }

    // ── Index operations ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateIndex_CreatesIndexOnTable()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, name text)""");
        var index = new TableIndex("idx_items_name", ["name"]);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateIndex(_schema, "items", index)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM pg_indexes WHERE schemaname = '{_schema}' AND tablename = 'items' AND indexname = 'idx_items_name'");
        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateIndex_Unique_CreatesUniqueIndexOnTable()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, code text)""");
        var index = new TableIndex("idx_items_code_unique", ["code"], IsUnique: true);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateIndex(_schema, "items", index)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var isUnique = await ScalarBool(
            $"SELECT ix.indisunique FROM pg_indexes pi JOIN pg_class t ON t.relname = pi.tablename JOIN pg_index ix ON ix.indexrelid = (SELECT oid FROM pg_class WHERE relname = 'idx_items_code_unique') WHERE pi.schemaname = '{_schema}' AND pi.indexname = 'idx_items_code_unique'");
        isUnique.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateIndex_RichIndex_RoundTripsThroughIntrospection()
    {
        // Arrange — a covering index with a descending key, an explicit non-default null ordering, and an
        // expression key. What is applied must introspect back to the same shape (no phantom drift).
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, name text, qty integer)""");
        var index = new TableIndex("idx_items_rich",
            [new IndexColumn("id", Sort: IndexSort.Descending, Nulls: IndexNulls.Last), new IndexColumn("lower(name)", IsExpression: true)],
            Include: ["qty"]);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateIndex(_schema, "items", index)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var provider = new PostgresSchemaProvider(_dataSource);
        var introspected = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].Tables[0].Indexes.ShouldHaveSingleItem();
        introspected.Method.ShouldBeNull(); // btree folds to null
        introspected.Include.ShouldBe(["qty"]);
        introspected.Columns.Count.ShouldBe(2);
        introspected.Columns[0].ShouldBe(new IndexColumn("id", IsExpression: false, Sort: IndexSort.Descending, Nulls: IndexNulls.Last));
        introspected.Columns[1].IsExpression.ShouldBeTrue();
        introspected.Columns[1].Expression.ShouldContain("lower");
    }

    [Fact]
    public async Task CreateIndex_GinMethod_RoundTripsPreservingMethod()
    {
        // Arrange — a non-btree access method must survive introspection (it does not fold to null).
        await Exec($"""CREATE TABLE "{_schema}"."docs" (tags text[])""");
        var index = new TableIndex("idx_docs_tags", ["tags"], Method: "gin");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateIndex(_schema, "docs", index)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var provider = new PostgresSchemaProvider(_dataSource);
        var introspected = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].Tables[0].Indexes.ShouldHaveSingleItem();
        introspected.Method.ShouldBe("gin");
        introspected.Columns.ShouldHaveSingleItem().Expression.ShouldBe("tags");
    }

    [Fact]
    public async Task DropIndex_RemovesIndexFromTable()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."items" (id integer, name text)""");
        await Exec($"""CREATE INDEX "idx_items_name" ON "{_schema}"."items" (name)""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new DropIndex(_schema, "items", "idx_items_name")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM pg_indexes WHERE schemaname = '{_schema}' AND indexname = 'idx_items_name'");
        exists.ShouldBeFalse();
    }

    // ── Views ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateView_CreatesViewInDatabase()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."users" (id integer, active boolean)""");
        var view = new View("active_users", $"""SELECT id FROM "{_schema}"."users" WHERE active""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateView(_schema, view)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.views WHERE table_schema = '{_schema}' AND table_name = 'active_users'");
        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateView_OnExistingView_ReplacesDefinition()
    {
        // Arrange — CreateView serves both add and body-modify; the second create must replace, not error.
        await Exec($"""CREATE TABLE "{_schema}"."users" (id integer, email text)""");
        await Exec($"""CREATE VIEW "{_schema}"."u" AS SELECT id FROM "{_schema}"."users" """);
        var replacement = new View("u", $"""SELECT id, email FROM "{_schema}"."users" """);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateView(_schema, replacement)], [], [])), TestContext.Current.CancellationToken);

        // Assert — the definition now includes the email column.
        var def = await ScalarString(
            $"SELECT pg_get_viewdef('\"{_schema}\".\"u\"'::regclass)");
        def.ShouldContain("email");
    }

    [Fact]
    public async Task DropView_RemovesView()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."users" (id integer)""");
        await Exec($"""CREATE VIEW "{_schema}"."u" AS SELECT id FROM "{_schema}"."users" """);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new DropView(_schema, "u")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.views WHERE table_schema = '{_schema}' AND table_name = 'u'");
        exists.ShouldBeFalse();
    }

    [Fact]
    public async Task RenameView_RenamesView()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."users" (id integer)""");
        await Exec($"""CREATE VIEW "{_schema}"."old_u" AS SELECT id FROM "{_schema}"."users" """);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new RenameView(_schema, "old_u", "new_u")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.views WHERE table_schema = '{_schema}' AND table_name = 'new_u'");
        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task SetViewComment_SetsComment()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}"."users" (id integer)""");
        await Exec($"""CREATE VIEW "{_schema}"."u" AS SELECT id FROM "{_schema}"."users" """);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new SetViewComment(_schema, "u", null, "the view")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var comment = await ScalarString(
            $"SELECT obj_description('\"{_schema}\".\"u\"'::regclass)");
        comment.ShouldBe("the view");
    }

    [Fact]
    public async Task RoundTrip_MaterializedView_IntrospectsAsMaterializedWithIndex()
    {
        // Arrange — a materialized view over a base table, plus a unique index on it.
        await Exec($"""CREATE TABLE "{_schema}".sales (id integer, amount integer)""");
        var view = new View("totals", $"""SELECT id, sum(amount) AS total FROM "{_schema}".sales GROUP BY id""", IsMaterialized: true);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateView(_schema, view)], [], [])), TestContext.Current.CancellationToken);
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new CreateIndex(_schema, "totals", new TableIndex("idx_totals_id", ["id"], IsUnique: true))], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var provider = new PostgresSchemaProvider(_dataSource);
        var introspected = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].Views.ShouldHaveSingleItem();
        introspected.IsMaterialized.ShouldBeTrue();
        introspected.Body.ShouldContain("sum");
        introspected.DependsOn.ShouldContain(d => d.Name == "sales");
        var index = introspected.Indexes.ShouldHaveSingleItem();
        index.Name.ShouldBe("idx_totals_id");
        index.IsUnique.ShouldBeTrue();
        index.Columns.ShouldHaveSingleItem().Expression.ShouldBe("id");
    }

    // ── Triggers ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_Trigger_IntrospectsWithDecodedAttributes()
    {
        // Arrange — a trigger function, then a row-level AFTER trigger with UPDATE OF and a WHEN condition.
        await Exec($"""CREATE TABLE "{_schema}".users (id int, email text, active boolean)""");
        await Exec($"""CREATE FUNCTION "{_schema}".audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$""");
        var trigger = new Trigger("users_audit", TriggerTiming.After, TriggerEvent.Insert | TriggerEvent.Update,
            $"{_schema}.audit", TriggerLevel.Row, UpdateOfColumns: ["email"], When: "new.active");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateTrigger(_schema, "users", trigger)], [], [])), TestContext.Current.CancellationToken);

        // Assert — the tgtype bitmask decodes back to the same timing/level/events.
        var provider = new PostgresSchemaProvider(_dataSource);
        var introspected = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].Tables[0].Triggers.ShouldHaveSingleItem();
        introspected.Name.ShouldBe("users_audit");
        introspected.Timing.ShouldBe(TriggerTiming.After);
        introspected.Level.ShouldBe(TriggerLevel.Row);
        introspected.Events.ShouldBe(TriggerEvent.Insert | TriggerEvent.Update);
        introspected.UpdateOfColumns.ShouldBe(["email"]);
        introspected.Function.ShouldBe($"{_schema}.audit");
        introspected.When.ShouldNotBeNull();
        introspected.When!.ShouldContain("active");
        introspected.FunctionArguments.ShouldBeNull();
    }

    // ── Extensions ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateExtension_ThenIntrospect_ReportsExtension()
    {
        // Act — create a contrib extension via the generator (extensions are database-global).
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateExtension(new Extension("hstore"))], [], [])), TestContext.Current.CancellationToken);

        // Assert — it surfaces as a root-level extension with a version; plpgsql is excluded.
        var provider = new PostgresSchemaProvider(_dataSource);
        var schema = await provider.GetSchema([_schema], TestContext.Current.CancellationToken);
        var hstore = schema.Extensions.Single(e => e.Name == "hstore");
        hstore.Version.ShouldNotBeNull();
        schema.Extensions.ShouldNotContain(e => e.Name == "plpgsql");
    }

    // ── Composite types ──────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_CompositeType_IntrospectsWithFields()
    {
        // Arrange
        var type = new CompositeType("address", [new CompositeField("street", SqlType.Text), new CompositeField("zip", SqlType.Int)]);

        // Act — create, then exercise an in-place field add (ALTER TYPE … ADD ATTRIBUTE).
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateCompositeType(_schema, type)], [], [])), TestContext.Current.CancellationToken);
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new AddCompositeField(_schema, "address", new CompositeField("country", SqlType.Text))], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var provider = new PostgresSchemaProvider(_dataSource);
        var introspected = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].CompositeTypes.ShouldHaveSingleItem();
        introspected.Name.ShouldBe("address");
        introspected.Fields.Select(f => (f.Name, f.DataType)).ShouldBe(
            [("street", SqlType.Text), ("zip", SqlType.Int), ("country", SqlType.Text)]);
    }

    // ── Domains ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_Domain_IntrospectsWithAllFacets()
    {
        // Arrange — a domain over text with a default, NOT NULL, and a named check.
        var domain = new Domain("us_postal", SqlType.Text, Default: "'00000'", NotNull: true,
            Checks: [new CheckConstraint("us_postal_fmt", "VALUE ~ '^[0-9]{5}$'")]);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([new CreateDomain(_schema, domain)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var provider = new PostgresSchemaProvider(_dataSource);
        var introspected = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].Domains.ShouldHaveSingleItem();
        introspected.Name.ShouldBe("us_postal");
        introspected.DataType.ShouldBe(SqlType.Text);
        introspected.NotNull.ShouldBeTrue();
        introspected.Default.ShouldNotBeNull();
        introspected.Default!.ShouldContain("00000");
        introspected.Checks.ShouldHaveSingleItem().Name.ShouldBe("us_postal_fmt");
    }

    // ── Deployment scripts ────────────────────────────────────────────────────

    [Fact]
    public async Task RunScript_PreDeployment_ExecutesSql()
    {
        // Arrange
        var script = new Script("seed", $"""CREATE TABLE "{_schema}"."seeded" (id integer)""", ScriptType.PreDeployment);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([], [script], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.tables WHERE table_schema = '{_schema}' AND table_name = 'seeded'");
        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task RunScript_PostDeployment_ExecutesSql()
    {
        // Arrange
        var script = new Script("seed", $"""CREATE TABLE "{_schema}"."seeded_post" (id integer)""", ScriptType.PostDeployment);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([], [], [script])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool(
            $"SELECT COUNT(*) > 0 FROM information_schema.tables WHERE table_schema = '{_schema}' AND table_name = 'seeded_post'");
        exists.ShouldBeTrue();
    }

    // ── Enums ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateEnum_CreatesTypeWithValuesInOrder()
    {
        // Arrange — includes an apostrophe to prove literal escaping.
        var action = new CreateEnum(_schema, new EnumType("order_status", ["pending", "shipped", "won't_ship"]));

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([action], [], [])), TestContext.Current.CancellationToken);

        // Assert
        (await EnumLabels("order_status")).ShouldBe("pending,shipped,won't_ship");
    }

    [Fact]
    public async Task AddEnumValue_AppendsToEnd()
    {
        // Arrange
        await Exec($"""CREATE TYPE "{_schema}".order_status AS ENUM ('a', 'b')""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new AddEnumValue(_schema, "order_status", "c")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        (await EnumLabels("order_status")).ShouldBe("a,b,c");
    }

    [Fact]
    public async Task AddEnumValue_Before_InsertsBeforeAnchor()
    {
        // Arrange
        await Exec($"""CREATE TYPE "{_schema}".order_status AS ENUM ('b', 'c')""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new AddEnumValue(_schema, "order_status", "a", Before: "b")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        (await EnumLabels("order_status")).ShouldBe("a,b,c");
    }

    [Fact]
    public async Task AddEnumValue_After_InsertsAfterAnchor()
    {
        // Arrange
        await Exec($"""CREATE TYPE "{_schema}".order_status AS ENUM ('a', 'c')""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new AddEnumValue(_schema, "order_status", "b", After: "a")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        (await EnumLabels("order_status")).ShouldBe("a,b,c");
    }

    [Fact]
    public async Task RenameEnum_RenamesType()
    {
        // Arrange
        await Exec($"""CREATE TYPE "{_schema}".order_state AS ENUM ('a')""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new RenameEnum(_schema, "order_state", "order_status")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        (await EnumLabels("order_status")).ShouldBe("a");
    }

    [Fact]
    public async Task SetEnumComment_SetsAndClearsComment()
    {
        // Arrange
        await Exec($"""CREATE TYPE "{_schema}".order_status AS ENUM ('a')""");
        var commentSql = $"""
            SELECT obj_description(t.oid, 'pg_type')
            FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE n.nspname = '{_schema}' AND t.typname = 'order_status'
            """;

        // Act + Assert — set...
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new SetEnumComment(_schema, "order_status", null, "lifecycle")], [], [])), TestContext.Current.CancellationToken);
        (await ScalarString(commentSql)).ShouldBe("lifecycle");

        // ...and clear.
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new SetEnumComment(_schema, "order_status", "lifecycle", null)], [], [])), TestContext.Current.CancellationToken);
        (await ScalarBool($"SELECT ({commentSql}) IS NULL")).ShouldBeTrue();
    }

    [Fact]
    public async Task DropEnum_RemovesType()
    {
        // Arrange
        await Exec($"""CREATE TYPE "{_schema}".order_status AS ENUM ('a')""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new DropEnum(_schema, "order_status")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool($"""
            SELECT COUNT(*) > 0 FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
            WHERE n.nspname = '{_schema}' AND t.typname = 'order_status'
            """);
        exists.ShouldBeFalse();
    }

    // ── Sequences ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSequence_Bare_CreatesSequenceWithEngineDefaults()
    {
        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new CreateSequence(_schema, new Sequence("order_id"))], [], [])), TestContext.Current.CancellationToken);

        // Assert
        (await SequenceCatalogValues("order_id")).ShouldBe("bigint,1,1,1,9223372036854775807,1,false");
    }

    [Fact]
    public async Task CreateSequence_WithOptions_AppliesEveryOption()
    {
        // Arrange
        var sequence = new Sequence("invoice_id", new SequenceOptions(
            SqlType.Int, StartWith: 20, IncrementBy: 5, MinValue: 10, MaxValue: 1000, Cache: 10, Cycle: true));

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new CreateSequence(_schema, sequence)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        (await SequenceCatalogValues("invoice_id")).ShouldBe("integer,20,5,10,1000,10,true");
    }

    [Fact]
    public async Task AlterSequence_ChangesOptions()
    {
        // Arrange
        await Exec($"""CREATE SEQUENCE "{_schema}".order_id""");
        var action = new AlterSequence(_schema, "order_id",
            OldOptions: new SequenceOptions(),
            NewOptions: new SequenceOptions(IncrementBy: 5, MaxValue: 1000, Cycle: true));

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([action], [], [])), TestContext.Current.CancellationToken);

        // Assert
        (await SequenceCatalogValues("order_id")).ShouldBe("bigint,1,5,1,1000,1,true");
    }

    [Fact]
    public async Task AlterSequence_ResetsOptionsToEngineDefaults()
    {
        // Arrange — exercises every explicit reset form (AS bigint, INCREMENT BY 1, NO MINVALUE, NO MAXVALUE,
        // START WITH <computed>, CACHE 1, NO CYCLE).
        await Exec($"""CREATE SEQUENCE "{_schema}".order_id AS integer INCREMENT 5 MINVALUE 10 MAXVALUE 1000 START 20 CACHE 10 CYCLE""");
        var action = new AlterSequence(_schema, "order_id",
            OldOptions: new SequenceOptions(SqlType.Int, StartWith: 20, IncrementBy: 5, MinValue: 10, MaxValue: 1000, Cache: 10, Cycle: true),
            NewOptions: new SequenceOptions());

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan([action], [], [])), TestContext.Current.CancellationToken);

        // Assert
        (await SequenceCatalogValues("order_id")).ShouldBe("bigint,1,1,1,9223372036854775807,1,false");
    }

    [Fact]
    public async Task RenameSequence_RenamesSequence()
    {
        // Arrange
        await Exec($"""CREATE SEQUENCE "{_schema}".bill_id""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new RenameSequence(_schema, "bill_id", "invoice_id")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool($"""
            SELECT COUNT(*) > 0 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'S' AND n.nspname = '{_schema}' AND c.relname = 'invoice_id'
            """);
        exists.ShouldBeTrue();
    }

    [Fact]
    public async Task SetSequenceComment_SetsAndClearsComment()
    {
        // Arrange
        await Exec($"""CREATE SEQUENCE "{_schema}".order_id""");
        var commentSql = $"""
            SELECT obj_description(c.oid, 'pg_class')
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'S' AND n.nspname = '{_schema}' AND c.relname = 'order_id'
            """;

        // Act + Assert — set...
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new SetSequenceComment(_schema, "order_id", null, "order numbers")], [], [])), TestContext.Current.CancellationToken);
        (await ScalarString(commentSql)).ShouldBe("order numbers");

        // ...and clear.
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new SetSequenceComment(_schema, "order_id", "order numbers", null)], [], [])), TestContext.Current.CancellationToken);
        (await ScalarBool($"SELECT ({commentSql}) IS NULL")).ShouldBeTrue();
    }

    [Fact]
    public async Task DropSequence_RemovesSequence()
    {
        // Arrange
        await Exec($"""CREATE SEQUENCE "{_schema}".order_id""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new DropSequence(_schema, "order_id")], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool($"""
            SELECT COUNT(*) > 0 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'S' AND n.nspname = '{_schema}' AND c.relname = 'order_id'
            """);
        exists.ShouldBeFalse();
    }

    // ── Functions ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateFunction_CreatesFunctionInDatabase()
    {
        // Arrange
        var function = new Routine("add_numbers", RoutineKind.Function, "a integer, b integer",
            "RETURNS integer LANGUAGE sql AS $$ SELECT a + b $$");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new CreateRoutine(_schema, function)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        (await ScalarString($"""SELECT "{_schema}".add_numbers(2, 3)::text""")).ShouldBe("5");
    }

    [Fact]
    public async Task CreateFunction_OnExistingFunction_ReplacesDefinition()
    {
        // Arrange — CreateFunction serves both add and definition-only modify; the second create must replace.
        await Exec($"""CREATE FUNCTION "{_schema}".answer() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$""");
        var replacement = new Routine("answer", RoutineKind.Function, "", "RETURNS integer LANGUAGE sql AS $$ SELECT 42 $$");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new CreateRoutine(_schema, replacement)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        (await ScalarString($"""SELECT "{_schema}".answer()::text""")).ShouldBe("42");
    }

    [Fact]
    public async Task RecreateFunction_ChangedSignature_ReplacesWithoutLeavingAnOverload()
    {
        // Arrange — a changed argument list must drop + recreate; CREATE OR REPLACE would add an overload instead.
        // The drop discards the catalog comment, so the recreate must re-issue it from the desired model.
        await Exec($"""
            CREATE FUNCTION "{_schema}".add_numbers(a integer, b integer) RETURNS integer LANGUAGE sql AS $$ SELECT a + b $$;
            COMMENT ON FUNCTION "{_schema}".add_numbers IS 'Adds numbers';
            """);
        var desired = new Routine("add_numbers", RoutineKind.Function, "a integer, b integer, c integer",
            "RETURNS integer LANGUAGE sql AS $$ SELECT a + b + c $$", Comment: "Adds numbers");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new RecreateRoutine(_schema, desired)], [], [])), TestContext.Current.CancellationToken);

        // Assert — exactly one routine remains, under the new signature, with the comment restored.
        var count = await ScalarString($"""
            SELECT count(*)::text FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = '{_schema}' AND p.proname = 'add_numbers'
            """);
        count.ShouldBe("1");
        (await ScalarString($"""SELECT "{_schema}".add_numbers(1, 2, 3)::text""")).ShouldBe("6");
        (await ScalarString(RoutineCommentSql("add_numbers"))).ShouldBe("Adds numbers");
    }

    [Fact]
    public async Task RenameFunction_RenamesFunction()
    {
        // Arrange
        await Exec($"""CREATE FUNCTION "{_schema}".old_answer() RETURNS integer LANGUAGE sql AS $$ SELECT 42 $$""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new RenameRoutine(_schema, "old_answer", "answer", RoutineKind.Function)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        (await ScalarString($"""SELECT "{_schema}".answer()::text""")).ShouldBe("42");
    }

    [Fact]
    public async Task SetFunctionComment_SetsAndClearsComment()
    {
        // Arrange
        await Exec($"""CREATE FUNCTION "{_schema}".answer() RETURNS integer LANGUAGE sql AS $$ SELECT 42 $$""");
        var commentSql = RoutineCommentSql("answer");

        // Act + Assert — set...
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new SetRoutineComment(_schema, "answer", null, "the answer", RoutineKind.Function)], [], [])), TestContext.Current.CancellationToken);
        (await ScalarString(commentSql)).ShouldBe("the answer");

        // ...and clear.
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new SetRoutineComment(_schema, "answer", "the answer", null, RoutineKind.Function)], [], [])), TestContext.Current.CancellationToken);
        (await ScalarBool($"SELECT ({commentSql}) IS NULL")).ShouldBeTrue();
    }

    [Fact]
    public async Task DropFunction_RemovesFunction()
    {
        // Arrange
        await Exec($"""CREATE FUNCTION "{_schema}".answer() RETURNS integer LANGUAGE sql AS $$ SELECT 42 $$""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new DropRoutine(_schema, "answer", RoutineKind.Function)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool($"""
            SELECT COUNT(*) > 0 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = '{_schema}' AND p.proname = 'answer'
            """);
        exists.ShouldBeFalse();
    }

    // ── Procedures ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProcedure_CreatesProcedureInDatabase()
    {
        // Arrange
        await Exec($"""CREATE TABLE "{_schema}".audit (entry text)""");
        var procedure = new Routine("log_entry", RoutineKind.Procedure, "message text",
            $"""LANGUAGE sql AS $$ INSERT INTO "{_schema}".audit (entry) VALUES (message) $$""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new CreateRoutine(_schema, procedure)], [], [])), TestContext.Current.CancellationToken);

        // Assert — the procedure exists and is callable.
        await Exec($"""CALL "{_schema}".log_entry('hello')""");
        (await ScalarString($"""SELECT entry FROM "{_schema}".audit""")).ShouldBe("hello");
    }

    [Fact]
    public async Task RecreateProcedure_ChangedSignature_ReplacesWithoutLeavingAnOverload()
    {
        // Arrange
        await Exec($"""
            CREATE PROCEDURE "{_schema}".noop(a integer) LANGUAGE sql AS $$ SELECT 1 $$;
            COMMENT ON PROCEDURE "{_schema}".noop IS 'does nothing';
            """);
        var desired = new Routine("noop", RoutineKind.Procedure, "a integer, b integer",
            "LANGUAGE sql AS $$ SELECT 1 $$", Comment: "does nothing");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new RecreateRoutine(_schema, desired)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var count = await ScalarString($"""
            SELECT count(*)::text FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = '{_schema}' AND p.proname = 'noop'
            """);
        count.ShouldBe("1");
        await Exec($"""CALL "{_schema}".noop(1, 2)""");
        (await ScalarString(RoutineCommentSql("noop"))).ShouldBe("does nothing");
    }

    [Fact]
    public async Task RenameProcedure_RenamesProcedure()
    {
        // Arrange
        await Exec($"""CREATE PROCEDURE "{_schema}".old_noop() LANGUAGE sql AS $$ SELECT 1 $$""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new RenameRoutine(_schema, "old_noop", "noop", RoutineKind.Procedure)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        await Exec($"""CALL "{_schema}".noop()""");
    }

    [Fact]
    public async Task SetProcedureComment_SetsAndClearsComment()
    {
        // Arrange
        await Exec($"""CREATE PROCEDURE "{_schema}".noop() LANGUAGE sql AS $$ SELECT 1 $$""");
        var commentSql = RoutineCommentSql("noop");

        // Act + Assert — set...
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new SetRoutineComment(_schema, "noop", null, "does nothing", RoutineKind.Procedure)], [], [])), TestContext.Current.CancellationToken);
        (await ScalarString(commentSql)).ShouldBe("does nothing");

        // ...and clear.
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new SetRoutineComment(_schema, "noop", "does nothing", null, RoutineKind.Procedure)], [], [])), TestContext.Current.CancellationToken);
        (await ScalarBool($"SELECT ({commentSql}) IS NULL")).ShouldBeTrue();
    }

    [Fact]
    public async Task DropProcedure_RemovesProcedure()
    {
        // Arrange
        await Exec($"""CREATE PROCEDURE "{_schema}".noop() LANGUAGE sql AS $$ SELECT 1 $$""");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new DropRoutine(_schema, "noop", RoutineKind.Procedure)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var exists = await ScalarBool($"""
            SELECT COUNT(*) > 0 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = '{_schema}' AND p.proname = 'noop'
            """);
        exists.ShouldBeFalse();
    }

    // ── Round-trips (generate → execute → introspect) ─────────────────────────

    [Fact]
    public async Task RoundTrip_FullyOptionedSequence_IntrospectsToSameOptions()
    {
        // Arrange
        var options = new SequenceOptions(SqlType.Int, StartWith: 20, IncrementBy: 5, MinValue: 10, MaxValue: 1000, Cache: 10, Cycle: true);

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new CreateSequence(_schema, new Sequence("order_id", options))], [], [])), TestContext.Current.CancellationToken);

        // Assert — what was applied is exactly what introspection reads back, so plan shows no drift.
        var provider = new PostgresSchemaProvider(_dataSource);
        var sequence = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].Sequences.ShouldHaveSingleItem();
        sequence.Options.ShouldBe(options);
    }

    [Fact]
    public async Task RoundTrip_BareSequence_IntrospectsToAllNullOptions()
    {
        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new CreateSequence(_schema, new Sequence("order_id"))], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var provider = new PostgresSchemaProvider(_dataSource);
        var sequence = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].Sequences.ShouldHaveSingleItem();
        sequence.Options.ShouldBe(new SequenceOptions());
    }

    [Fact]
    public async Task RoundTrip_EnumWithAnchoredAdditions_IntrospectsToDesiredOrder()
    {
        // Arrange — mirrors what the core comparer plans for ['a','c'] → ['a','b','c','d'].
        var plan = new MigrationPlan(
            [
                new CreateEnum(_schema, new EnumType("order_status", ["a", "c"])),
                new AddEnumValue(_schema, "order_status", "b", Before: "c"),
                new AddEnumValue(_schema, "order_status", "d", After: "c"),
            ], [], []);

        // Act
        await _executor.Execute(_generator.Generate(plan), TestContext.Current.CancellationToken);

        // Assert
        var provider = new PostgresSchemaProvider(_dataSource);
        var enumType = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].Enums.ShouldHaveSingleItem();
        enumType.Values.ShouldBe(["a", "b", "c", "d"]);
    }

    [Fact]
    public async Task RoundTrip_Function_IntrospectsWithSameArguments()
    {
        // Arrange — the argument list is the recreate trigger, so what was applied must read back verbatim.
        // (The definition reads back in the DB's canonical form — $function$ quoting, qualified names — which the
        // core reconciles by storing the DB-reported form, as with view bodies.)
        var function = new Routine("add_numbers", RoutineKind.Function, "a integer, b integer",
            "RETURNS integer LANGUAGE sql AS $$ SELECT a + b $$");

        // Act
        await _executor.Execute(_generator.Generate(new MigrationPlan(
            [new CreateRoutine(_schema, function)], [], [])), TestContext.Current.CancellationToken);

        // Assert
        var provider = new PostgresSchemaProvider(_dataSource);
        var introspected = (await provider.GetSchema([_schema], TestContext.Current.CancellationToken))
            .Schemas[0].Routines.ShouldHaveSingleItem();
        introspected.Name.ShouldBe("add_numbers");
        introspected.Arguments.ShouldBe("a integer, b integer");
        introspected.Definition.ShouldContain("SELECT a + b");
    }

    // Transaction/rollback semantics are the core executor's behaviour (DefaultSqlExecutor, now internal) and are
    // tested in the core, not here — this suite covers the Postgres SQL the generator emits.

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Runs the generated statements directly (the core executor is internal). This is only a vehicle for asserting
    // the generated SQL is valid Postgres; it intentionally does not replicate the executor's transaction handling.
    private sealed class StatementRunner(NpgsqlDataSource dataSource)
    {
        public async Task Execute(SqlPlan plan, CancellationToken cancellationToken = default)
        {
            foreach (var statement in plan.Statements)
            {
                await using var command = dataSource.CreateCommand(statement.Sql);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private async Task Exec(string sql)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> ScalarBool(string sql)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<string> ScalarString(string sql)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>SQL returning the comment on the test schema's function or procedure with the given name.</summary>
    private string RoutineCommentSql(string routineName) => $"""
        SELECT obj_description(p.oid, 'pg_proc')
        FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = '{_schema}' AND p.proname = '{routineName}'
        """;

    /// <summary>The enum's labels in comparison order, comma-joined.</summary>
    private Task<string> EnumLabels(string enumName) => ScalarString($"""
        SELECT string_agg(e.enumlabel, ',' ORDER BY e.enumsortorder)
        FROM pg_enum e
        JOIN pg_type t ON t.oid = e.enumtypid
        JOIN pg_namespace n ON n.oid = t.typnamespace
        WHERE n.nspname = '{_schema}' AND t.typname = '{enumName}'
        """);

    /// <summary>The sequence's raw catalog values: type,start,increment,min,max,cache,cycle.</summary>
    private Task<string> SequenceCatalogValues(string sequenceName) => ScalarString($"""
        SELECT format_type(s.seqtypid, NULL) || ',' || s.seqstart || ',' || s.seqincrement || ',' ||
               s.seqmin || ',' || s.seqmax || ',' || s.seqcache || ',' || s.seqcycle
        FROM pg_sequence s
        JOIN pg_class c ON c.oid = s.seqrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = '{_schema}' AND c.relname = '{sequenceName}'
        """);
}
