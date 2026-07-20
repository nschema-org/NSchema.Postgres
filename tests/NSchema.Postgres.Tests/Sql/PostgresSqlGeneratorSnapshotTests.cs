using NSchema.Model.Columns;
using NSchema.Model.CompositeTypes;
using NSchema.Model.Constraints;
using NSchema.Model.Enums;
using NSchema.Model.Indexes;
using NSchema.Model.Routines;
using NSchema.Model.Scripts;
using NSchema.Model.Sequences;
using NSchema.Model.Tables;
using NSchema.Model.Triggers;
using NSchema.Model.Views;
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
using NSchema.Plan.Model.Sequences;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Triggers;
using NSchema.Plan.Model.Views;

namespace NSchema.Postgres.Tests.Sql;

/// <summary>
/// Snapshot tests for <see cref="PostgresSqlGenerator"/>. Unlike <see cref="PostgresSqlGeneratorTests"/>
/// (which executes generated DDL against a real database via Testcontainers), these tests assert on the
/// exact SQL text the generator emits — no Docker required. Snapshots live alongside this file as
/// <c>*.verified.txt</c>; review and commit them when the generated SQL intentionally changes.
/// </summary>
public sealed class PostgresSqlGeneratorSnapshotTests
{
    private static readonly ISqlGenerator Generator = new PostgresSqlGenerator();

    private static Task VerifyPlan(params MigrationAction[] actions) =>
        Verify(Generator.Generate(new MigrationPlan(actions, [], [])));

    // ── Schema operations ─────────────────────────────────────────────────────

    [Fact]
    public Task SchemaOperations() => VerifyPlan(
        new CreateSchema("sales"),
        new RenameSchema("sales", "commerce"),
        new DropSchema("commerce"));

    // ── Table operations ──────────────────────────────────────────────────────

    [Fact]
    public Task CreateTable_WithColumnsAndPrimaryKey() => VerifyPlan(
        new CreateTable("public", new Table("users",
            PrimaryKey: new PrimaryKey("pk_users", ["id"]),
            Columns:
            [
                new Column("id", SqlType.BigInt, IsNullable: false, IsIdentity: true),
                new Column("email", SqlType.VarChar(255), IsNullable: false),
                new Column("created_at", SqlType.DateTimeOffset, IsNullable: false, DefaultExpression: "now()"),
                new Column("notes", SqlType.Text),
            ])));

    [Fact]
    public Task CreateTable_WithIdentityOptions() => VerifyPlan(
        new CreateTable("public", new Table("counters",
            Columns:
            [
                new Column("id", SqlType.BigInt, IsNullable: false, IsIdentity: true,
                    IdentityOptions: new IdentityOptions(StartWith: 1000, MinValue: 1000, IncrementBy: 5)),
            ])));

    [Fact]
    public Task TableLifecycle() => VerifyPlan(
        new RenameTable("public", "old_users", "users"),
        new DropTable("public", "legacy"));

    // ── Column operations ─────────────────────────────────────────────────────

    [Fact]
    public Task ColumnOperations() => VerifyPlan(
        new AddColumn("public", "users", new Column("age", SqlType.Int)),
        new RenameColumn("public", "users", "age", "years"),
        new AlterColumnType("public", "users", "years", SqlType.Int, SqlType.BigInt),
        new AlterColumnNullability("public", "users", "years", OldNullable: true, NewNullable: false),
        new AlterColumnNullability("public", "users", "notes", OldNullable: false, NewNullable: true),
        new SetColumnDefault("public", "users", "years", null, "0"),
        new SetColumnDefault("public", "users", "years", "0", null),
        new DropColumn("public", "users", new Column("years", SqlType.BigInt)));

    [Fact]
    public Task AlterIdentitySequence() => VerifyPlan(
        new AlterIdentitySequence("public", "users", "id",
            OldOptions: new IdentityOptions(StartWith: 1, MinValue: 1, IncrementBy: 1),
            NewOptions: new IdentityOptions(StartWith: 500, MinValue: 100, IncrementBy: 2)));

    [Fact]
    public Task GeneratedColumnOperations() => VerifyPlan(
        new CreateTable("public", new Table("boxes",
            Columns:
            [
                new Column("w", SqlType.Int, IsNullable: false),
                new Column("h", SqlType.Int, IsNullable: false),
                new Column("area", SqlType.Int, GeneratedExpression: "w * h"),
            ])),
        new AddColumn("public", "boxes", new Column("perimeter", SqlType.Int, GeneratedExpression: "2 * (w + h)")),
        // Change the expression in place (SET EXPRESSION), then drop the generation (DROP EXPRESSION).
        new SetColumnGenerated("public", "boxes", "area", "w * h", "w * h * 2"),
        new SetColumnGenerated("public", "boxes", "area", "w * h * 2", null));

    // ── Keys, indexes and constraints ───────────────────────────────────────────

    [Fact]
    public Task PrimaryKeyOperations() => VerifyPlan(
        new AddPrimaryKey("public", "users", new PrimaryKey("pk_users", ["id", "tenant_id"])),
        new DropPrimaryKey("public", "users", "pk_users"));

    [Fact]
    public Task ForeignKeyOperations() => VerifyPlan(
        new AddForeignKey("public", "orders", new ForeignKey(
            "fk_orders_user", ["user_id"], "public", "users", ["id"],
            OnDelete: ReferentialAction.Cascade, OnUpdate: ReferentialAction.SetNull)),
        new DropForeignKey("public", "orders", "fk_orders_user"));

    [Fact]
    public Task IndexOperations() => VerifyPlan(
        new CreateIndex("public", "users", new TableIndex("idx_users_email", ["email"], IsUnique: true)),
        new CreateIndex("public", "users", new TableIndex("idx_users_active", ["created_at"], Predicate: "notes IS NOT NULL")),
        // An access method (USING), a covering INCLUDE, descending / nulls ordering, and an expression key.
        new CreateIndex("public", "users", new TableIndex("idx_users_tags", ["tags"], Method: "gin")),
        new CreateIndex("public", "users", new TableIndex("idx_users_recent",
            [new IndexColumn("created_at", Sort: IndexSort.Descending, Nulls: IndexNulls.Last), new IndexColumn("lower(email)", IsExpression: true)],
            Include: ["id", "notes"])),
        new DropIndex("public", "users", "idx_users_email"));

    [Fact]
    public Task ExclusionConstraintOperations() => VerifyPlan(
        new AddExclusionConstraint("public", "bookings", new ExclusionConstraint("no_overlap",
            [new ExclusionElement("room", "="), new ExclusionElement("during", "&&")], Method: "gist", Predicate: "room > 0")),
        // An expression element is parenthesised.
        new AddExclusionConstraint("public", "events", new ExclusionConstraint("no_clash",
            [new ExclusionElement("tstzrange(starts, ends)", "&&", IsExpression: true)], Method: "gist")),
        new DropExclusionConstraint("public", "bookings", "no_overlap"));

    // ── Triggers ──────────────────────────────────────────────────────────────

    [Fact]
    public Task TriggerOperations() => VerifyPlan(
        new CreateTrigger("public", "users", new Trigger("users_audit", TriggerTiming.After,
            TriggerEvent.Insert | TriggerEvent.Update, "public.log_change", TriggerLevel.Row,
            UpdateOfColumns: ["email", "name"], When: "new.active", FunctionArguments: "'audit'")),
        new CreateTrigger("public", "logs", new Trigger("logs_truncate", TriggerTiming.Before,
            TriggerEvent.Truncate, "public.on_truncate", TriggerLevel.Statement)),
        new SetTriggerComment("public", "users", "users_audit", null, "audit changes"),
        new DropTrigger("public", "users", "users_audit"));

    // ── Views ─────────────────────────────────────────────────────────────────

    [Fact]
    public Task ViewOperations() => VerifyPlan(
        new CreateView("public", new View("active_users", "SELECT id, email FROM public.users WHERE active")),
        new RenameView("public", "legacy_active", "active_users"),
        new SetViewComment("public", "active_users", null, "Active users only"),
        new SetViewComment("public", "active_users", "Active users only", null),
        new DropView("public", "active_users"));

    [Fact]
    public Task MaterializedViewOperations() => VerifyPlan(
        // A materialized view: CREATE MATERIALIZED VIEW (never CREATE OR REPLACE), an index on it (a plain
        // CreateIndex), and the MATERIALIZED variants of rename/comment/drop.
        new CreateView("public", new View("daily_totals", "SELECT date, sum(amount) AS total FROM public.sales GROUP BY date", IsMaterialized: true)),
        new CreateIndex("public", "daily_totals", new TableIndex("idx_daily_totals_date", ["date"], IsUnique: true)),
        new RenameView("public", "legacy_totals", "daily_totals", IsMaterialized: true),
        new SetViewComment("public", "daily_totals", null, "Daily rollup", IsMaterialized: true),
        new DropView("public", "daily_totals", IsMaterialized: true));

    // ── Enums ──────────────────────────────────────────────────────────────────

    [Fact]
    public Task EnumOperations() => VerifyPlan(
        new CreateEnum("public", new EnumType("order_status", ["pending", "shipped", "won't_ship"])),
        new RenameEnum("public", "order_state", "order_status"),
        new AddEnumValue("public", "order_status", "delivered"),
        new AddEnumValue("public", "order_status", "draft", Before: "pending"),
        new AddEnumValue("public", "order_status", "in_transit", After: "shipped"),
        new SetEnumComment("public", "order_status", null, "Order lifecycle"),
        new SetEnumComment("public", "order_status", "Order lifecycle", null),
        new DropEnum("public", "order_status"));

    // ── Composite types ──────────────────────────────────────────────────────

    [Fact]
    public Task CompositeTypeOperations() => VerifyPlan(
        new CreateCompositeType("public", new CompositeType("address",
            [new CompositeField("street", SqlType.Text), new CompositeField("zip", SqlType.Int)])),
        new AddCompositeField("public", "address", new CompositeField("country", SqlType.Text)),
        new AlterCompositeFieldType("public", "address", "zip", SqlType.Int, SqlType.VarChar(10)),
        new DropCompositeField("public", "address", "country"),
        new RenameCompositeType("public", "old_address", "address"),
        new SetCompositeTypeComment("public", "address", null, "a postal address"),
        new DropCompositeType("public", "address"));

    // ── Domains ────────────────────────────────────────────────────────────────

    [Fact]
    public Task DomainOperations() => VerifyPlan(
        new CreateDomain("public", new Domain("email", SqlType.Text, Default: "'n/a'", NotNull: true,
            Checks: [new CheckConstraint("email_fmt", "VALUE ~ '@'")])),
        new AlterDomainDefault("public", "email", "'n/a'", "'unknown'"),
        new AlterDomainDefault("public", "email", "'unknown'", null),
        new AlterDomainNotNull("public", "email", false),
        new AddDomainCheck("public", "email", new CheckConstraint("email_len", "length(VALUE) > 3")),
        new DropDomainCheck("public", "email", "email_fmt"),
        // A base-type change recreates (drop + create, re-issuing the comment).
        new RecreateDomain("public", new Domain("code", SqlType.VarChar(8), Comment: "a code")),
        new RenameDomain("public", "old_code", "code"),
        new SetDomainComment("public", "email", null, "an email"),
        new DropDomain("public", "email"));

    // ── Sequences ──────────────────────────────────────────────────────────────

    [Fact]
    public Task SequenceOperations() => VerifyPlan(
        new CreateSequence("public", new Sequence("order_id")),
        new CreateSequence("public", new Sequence("invoice_id", new SequenceOptions(
            SqlType.SmallInt, StartWith: 100, IncrementBy: 5, MinValue: 10, MaxValue: 30000, Cache: 20, Cycle: true))),
        new RenameSequence("public", "bill_id", "invoice_id"),
        // A mixed delta: one option changes value, every other resets to its engine default explicitly.
        new AlterSequence("public", "invoice_id",
            OldOptions: new SequenceOptions(SqlType.SmallInt, StartWith: 100, IncrementBy: 5, MinValue: 10, MaxValue: 30000, Cache: 20, Cycle: true),
            NewOptions: new SequenceOptions(IncrementBy: 50)),
        new SetSequenceComment("public", "invoice_id", null, "Invoice numbers"),
        new SetSequenceComment("public", "invoice_id", "Invoice numbers", null),
        new DropSequence("public", "invoice_id"));

    // ── Extensions ────────────────────────────────────────────────────────────

    [Fact]
    public Task ExtensionOperations() => VerifyPlan(
        new CreateExtension(new Extension("citext")),
        new CreateExtension(new Extension("postgis", Version: "3.4")),
        // A hyphenated name must be quoted.
        new CreateExtension(new Extension("uuid-ossp")),
        new AlterExtension("postgis", "3.4", "3.5"),
        new SetExtensionComment("citext", null, "case-insensitive text"),
        new DropExtension("postgis"));

    // ── Functions ─────────────────────────────────────────────────────────────

    [Fact]
    public Task FunctionOperations() => VerifyPlan(
        new CreateRoutine("public", new Routine("active_user_count", RoutineKind.Function, "",
            "RETURNS integer LANGUAGE sql AS $$ SELECT count(*) FROM public.users WHERE active $$")),
        new RenameRoutine("public", "user_count", "active_user_count", RoutineKind.Function),
        // A signature change: drop + recreate, re-issuing the comment the drop discarded.
        new RecreateRoutine("public", new Routine("add_numbers", RoutineKind.Function, "a integer, b integer, c integer DEFAULT 0",
            "RETURNS integer LANGUAGE sql AS $$ SELECT a + b + c $$", Comment: "Adds numbers")),
        new RecreateRoutine("public", new Routine("subtract_numbers", RoutineKind.Function, "a integer, b integer",
            "RETURNS integer LANGUAGE sql AS $$ SELECT a - b $$")),
        new SetRoutineComment("public", "active_user_count", null, "Count of active users", RoutineKind.Function),
        new SetRoutineComment("public", "active_user_count", "Count of active users", null, RoutineKind.Function),
        new DropRoutine("public", "active_user_count", RoutineKind.Function));

    // ── Procedures ────────────────────────────────────────────────────────────

    [Fact]
    public Task ProcedureOperations() => VerifyPlan(
        new CreateRoutine("public", new Routine("archive_users", RoutineKind.Procedure, "cutoff date",
            "LANGUAGE sql AS $$ DELETE FROM public.users WHERE created_at < cutoff $$")),
        new RenameRoutine("public", "purge_users", "archive_users", RoutineKind.Procedure),
        new RecreateRoutine("public", new Routine("archive_users", RoutineKind.Procedure, "cutoff timestamptz",
            "LANGUAGE sql AS $$ DELETE FROM public.users WHERE created_at < cutoff $$", Comment: "Archives stale users")),
        new SetRoutineComment("public", "archive_users", null, "Archive job", RoutineKind.Procedure),
        new SetRoutineComment("public", "archive_users", "Archive job", null, RoutineKind.Procedure),
        new DropRoutine("public", "archive_users", RoutineKind.Procedure));

    // ── Comments ────────────────────────────────────────────────────────────────

    [Fact]
    public Task CommentOperations() => VerifyPlan(
        new SetSchemaComment("public", null, "Application schema"),
        new SetTableComment("public", "users", null, "Registered users"),
        new SetColumnComment("public", "users", "email", null, "Unique login address"),
        new SetIndexComment("public", "users", "idx_users_email", null, "Lookup by email"),
        new SetTableComment("public", "users", "Registered users", null));

    // ── Grants ────────────────────────────────────────────────────────────────

    [Fact]
    public Task GrantOperations() => VerifyPlan(
        new GrantSchemaUsage("public", "app_role"),
        new GrantTablePrivileges("public", "users", "app_role", TablePrivilege.Select | TablePrivilege.Insert),
        new GrantTablePrivileges("public", "users", "readonly", TablePrivilege.Select),
        new RevokeTablePrivileges("public", "users", "app_role", TablePrivilege.All),
        new RevokeSchemaUsage("public", "app_role"));

    // ── Type mapping ────────────────────────────────────────────────────────────

    [Fact]
    public Task TypeMapping_CoversAllSqlTypes() => VerifyPlan(
        Alter(SqlType.Boolean),
        Alter(SqlType.TinyInt),
        Alter(SqlType.SmallInt),
        Alter(SqlType.Int),
        Alter(SqlType.BigInt),
        Alter(SqlType.Float),
        Alter(SqlType.Double),
        Alter(SqlType.Decimal(18, 4)),
        Alter(SqlType.Char(10)),
        Alter(SqlType.NChar(10)),
        Alter(SqlType.VarChar(null)),
        Alter(SqlType.VarChar(100)),
        Alter(SqlType.NVarChar(null)),
        Alter(SqlType.NVarChar(100)),
        Alter(SqlType.Text),
        Alter(SqlType.Date),
        Alter(SqlType.Time),
        Alter(SqlType.DateTime),
        Alter(SqlType.DateTimeOffset),
        Alter(SqlType.Guid),
        Alter(SqlType.Binary(16)),
        Alter(SqlType.VarBinary(null)),
        Alter(SqlType.Custom("citext")));

    private static AlterColumnType Alter(SqlType type) =>
        new("public", "t", "c", SqlType.Int, type);

    // ── Deployment scripts ────────────────────────────────────────────────────

    [Fact]
    public void DeploymentScript_RunOutsideTransaction_IsCarriedOntoTheStatement()
    {
        // A script declared `run_outside_transaction = true` (e.g. CREATE INDEX CONCURRENTLY, which Postgres forbids
        // inside a transaction) must carry the flag through to the generated statement so the executor carves it out;
        // an ordinary script must not.
        var concurrent = new Script("reindex", "CREATE INDEX CONCURRENTLY i ON s.t (c)", ScriptType.PostDeployment)
        {
            RunOutsideTransaction = true,
        };
        var ordinary = new Script("seed", "INSERT INTO s.t VALUES (1)", ScriptType.PreDeployment);

        var plan = Generator.Generate(new MigrationPlan([], [ordinary], [concurrent]));

        plan.Statements.Single(s => s.Sql.Contains("INSERT")).RunOutsideTransaction.ShouldBeFalse();
        plan.Statements.Single(s => s.Sql.Contains("CONCURRENTLY")).RunOutsideTransaction.ShouldBeTrue();
    }

    // ── Data migrations ───────────────────────────────────────────────────────

    [Fact]
    public Task DataMigrationOperations() => VerifyPlan(
        new ExecuteDataMigration("backfill", DataMigrationTrigger.AddColumn, "public", "users", "status",
            "UPDATE public.users SET status = 'active' WHERE status IS NULL"),
        new ExecuteDataMigration(null, DataMigrationTrigger.AddConstraint, "public", "users", "uq_users_email",
            "CREATE UNIQUE INDEX CONCURRENTLY uq_users_email ON public.users (email)")
        { RunOutsideTransaction = true });

    [Fact]
    public void DataMigration_SqlIsEmittedVerbatimAndRunOutsideTransactionPropagates()
    {
        // The migration body is user-authored SQL for the target dialect, so it must pass through untouched — no
        // quoting, escaping or rewriting — and run_outside_transaction must carry onto the statement in both states.
        var inTransaction = new ExecuteDataMigration("backfill", DataMigrationTrigger.AddColumn, "public", "users", "status",
            """UPDATE public."users" SET status = 'new' WHERE "status" IS NULL -- $body$ left alone""");
        var outsideTransaction = new ExecuteDataMigration(null, DataMigrationTrigger.AlterColumnType, "public", "users", "id",
            "CREATE INDEX CONCURRENTLY idx_users_id ON public.users (id)")
        { RunOutsideTransaction = true };

        var plan = Generator.Generate(new MigrationPlan([inTransaction, outsideTransaction], [], []));

        plan.Statements.Count.ShouldBe(2);
        plan.Statements[0].Sql.ShouldBe(inTransaction.Sql);
        plan.Statements[0].RunOutsideTransaction.ShouldBeFalse();
        plan.Statements[1].Sql.ShouldBe(outsideTransaction.Sql);
        plan.Statements[1].RunOutsideTransaction.ShouldBeTrue();
    }
}
