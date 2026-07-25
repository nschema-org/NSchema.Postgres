using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.CompositeTypes;
using NSchema.Model.Constraints;
using NSchema.Model.Domains;
using NSchema.Model.Enums;
using NSchema.Model.Extensions;
using NSchema.Model.Indexes;
using NSchema.Model.Routines;
using NSchema.Model.Scripts;
using NSchema.Model.Sequences;
using NSchema.Model.Tables;
using NSchema.Model.Triggers;
using NSchema.Model.Views;
using NSchema.Plan.Domain;
using NSchema.Plan.Domain.Columns;
using NSchema.Plan.Domain.CompositeTypes;
using NSchema.Plan.Domain.Constraints;
using NSchema.Plan.Domain.Domains;
using NSchema.Plan.Domain.Enums;
using NSchema.Plan.Domain.Extensions;
using NSchema.Plan.Domain.Indexes;
using NSchema.Plan.Domain.Routines;
using NSchema.Plan.Domain.Schemas;
using NSchema.Plan.Domain.Scripts;
using NSchema.Plan.Domain.Sequences;
using NSchema.Plan.Domain.Tables;
using NSchema.Plan.Domain.Triggers;
using NSchema.Plan.Domain.Views;
using NSchema.Postgres.Sql;

namespace NSchema.Postgres.Tests.Sql;

/// <summary>
/// Snapshot tests for <see cref="PostgresSqlDialect"/>. Unlike <see cref="PostgresSqlDialectTests"/>
/// (which executes generated DDL against a real database via Testcontainers), these tests assert on the
/// exact SQL text the dialect emits — no Docker required. Snapshots live alongside this file as
/// <c>*.verified.txt</c>; review and commit them when the generated SQL intentionally changes.
/// </summary>
public sealed class PostgresSqlDialectSnapshotTests
{
    private static readonly PostgresSqlDialect Dialect = new();

    private static Task VerifyActions(params MigrationAction[] actions) =>
        Verify(actions
            .SelectMany(action => Dialect.Generate(action).Require())
            .Select(statement => new { Sql = statement.Sql.Value, statement.RunOutsideTransaction })
            .ToList());

    // ── Schema operations ─────────────────────────────────────────────────────

    [Fact]
    public Task SchemaOperations() => VerifyActions(
        new CreateSchema("sales"),
        new RenameSchema("sales", "commerce"),
        new DropSchema("commerce"));

    // ── Table operations ──────────────────────────────────────────────────────

    [Fact]
    public Task CreateTable_WithColumnsAndPrimaryKey() => VerifyActions(
        new CreateTable("public", new Table
        {
            Name = "users",
            PrimaryKey = new PrimaryKey { Name = "pk_users", ColumnNames = ["id"] },
            Columns =
            [
                new Column { Name = "id", Type = SqlType.BigInt, IsNullable = false, IsIdentity = true },
                new Column { Name = "email", Type = SqlType.VarChar(255), IsNullable = false },
                new Column { Name = "created_at", Type = SqlType.DateTimeOffset, IsNullable = false, DefaultExpression = "now()" },
                new Column { Name = "notes", Type = SqlType.Text },
            ],
        }));

    [Fact]
    public Task CreateTable_WithIdentityOptions() => VerifyActions(
        new CreateTable("public", new Table
        {
            Name = "counters",
            Columns =
            [
                new Column
                {
                    Name = "id",
                    Type = SqlType.BigInt,
                    IsNullable = false,
                    IsIdentity = true,
                    IdentityOptions = new IdentityOptions(StartWith: 1000, MinValue: 1000, IncrementBy: 5),
                },
            ],
        }));

    [Fact]
    public Task CreateTable_WithInlineConstraints() => VerifyActions(
        // A newly-created table carries every constraint inline: primary key, unique, check, foreign key, and
        // Postgres's exclusion constraint — the linearizer folds these into CREATE TABLE rather than emitting adds.
        new CreateTable("public", new Table
        {
            Name = "bookings",
            PrimaryKey = new PrimaryKey { Name = "pk_bookings", ColumnNames = ["id"] },
            Columns =
            [
                new Column { Name = "id", Type = SqlType.BigInt, IsNullable = false, IsIdentity = true },
                new Column { Name = "room_id", Type = SqlType.BigInt, IsNullable = false },
                new Column { Name = "code", Type = SqlType.VarChar(20), IsNullable = false },
                new Column { Name = "guests", Type = SqlType.Int, IsNullable = false },
                new Column { Name = "during", Type = new SqlType("tsrange"), IsNullable = false },
            ],
            UniqueConstraints = [new UniqueConstraint { Name = "uq_bookings_code", ColumnNames = ["code"] }],
            CheckConstraints = [new CheckConstraint { Name = "ck_bookings_guests", Expression = "guests > 0" }],
            ForeignKeys =
            [
                new ForeignKey
                {
                    Name = "fk_bookings_room",
                    ColumnNames = ["room_id"],
                    References = new ObjectAddress("public", "rooms"),
                    ReferencedColumnNames = ["id"],
                    OnDelete = ReferentialAction.Cascade,
                },
            ],
            ExclusionConstraints =
            [
                new ExclusionConstraint { Name = "no_overlap", Elements = [new ExclusionElement("&&", "during")], Method = "gist" },
            ],
        }));

    [Fact]
    public Task TableLifecycle() => VerifyActions(
        new RenameTable(new ObjectAddress("public", "old_users"), "users"),
        new DropTable(new ObjectAddress("public", "legacy")));

    // ── Column operations ─────────────────────────────────────────────────────

    [Fact]
    public Task ColumnOperations() => VerifyActions(
        new AddColumn(new ObjectAddress("public", "users"), new Column { Name = "age", Type = SqlType.Int }),
        new RenameColumn(new MemberAddress("public", "users", "age"), "years"),
        new AlterColumn(new ObjectAddress("public", "users"), new Column { Name = "years", Type = SqlType.BigInt }, Type: new(SqlType.Int, SqlType.BigInt), Nullability: new(true, false)),
        new AlterColumn(new ObjectAddress("public", "users"), new Column { Name = "notes", Type = SqlType.Text, IsNullable = true }, Nullability: new(false, true)),
        new SetColumnDefault(new MemberAddress("public", "users", "years"), null, "0"),
        new SetColumnDefault(new MemberAddress("public", "users", "years"), "0", null),
        new DropColumn(new ObjectAddress("public", "users"), new Column { Name = "years", Type = SqlType.BigInt }));

    [Fact]
    public Task AlterIdentitySequence() => VerifyActions(
        new AlterIdentitySequence(new MemberAddress("public", "users", "id"),
            OldOptions: new IdentityOptions(StartWith: 1, MinValue: 1, IncrementBy: 1),
            NewOptions: new IdentityOptions(StartWith: 500, MinValue: 100, IncrementBy: 2)));

    [Fact]
    public Task GeneratedColumnOperations() => VerifyActions(
        new CreateTable("public", new Table
        {
            Name = "boxes",
            Columns =
            [
                new Column { Name = "w", Type = SqlType.Int, IsNullable = false },
                new Column { Name = "h", Type = SqlType.Int, IsNullable = false },
                new Column { Name = "area", Type = SqlType.Int, GeneratedExpression = "w * h" },
            ],
        }),
        new AddColumn(new ObjectAddress("public", "boxes"), new Column { Name = "perimeter", Type = SqlType.Int, GeneratedExpression = "2 * (w + h)" }),
        // Change the expression in place (SET EXPRESSION), then drop the generation (DROP EXPRESSION).
        new SetColumnGenerated(new MemberAddress("public", "boxes", "area"), "w * h", "w * h * 2"),
        new SetColumnGenerated(new MemberAddress("public", "boxes", "area"), "w * h * 2", null));

    [Fact]
    public void SetColumnGenerated_MakingAPlainColumnGenerated_IsAnErrorDiagnostic()
    {
        // PostgreSQL has no in-place ADD GENERATED, so the transition is an unsupported-action error, not SQL.
        var result = Dialect.Generate(new SetColumnGenerated(new MemberAddress("public", "boxes", "area"), null, "w * h"));

        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldHaveSingleItem().Message.ShouldContain("set column generated");
    }

    // ── Keys, indexes and constraints ───────────────────────────────────────────

    [Fact]
    public Task PrimaryKeyOperations() => VerifyActions(
        new AddPrimaryKey(new ObjectAddress("public", "users"), new PrimaryKey { Name = "pk_users", ColumnNames = ["id", "tenant_id"] }),
        new DropPrimaryKey(new MemberAddress("public", "users", "pk_users")));

    [Fact]
    public Task ForeignKeyOperations() => VerifyActions(
        new AddForeignKey(new ObjectAddress("public", "orders"), new ForeignKey
        {
            Name = "fk_orders_user",
            ColumnNames = ["user_id"],
            References = new ObjectAddress("public", "users"),
            ReferencedColumnNames = ["id"],
            OnDelete = ReferentialAction.Cascade,
            OnUpdate = ReferentialAction.SetNull,
        }),
        new DropForeignKey(new MemberAddress("public", "orders", "fk_orders_user")));

    [Fact]
    public Task IndexOperations() => VerifyActions(
        new CreateIndex(new ObjectAddress("public", "users"), new TableIndex { Name = "idx_users_email", Columns = ["email"], IsUnique = true }),
        new CreateIndex(new ObjectAddress("public", "users"), new TableIndex { Name = "idx_users_active", Columns = ["created_at"], Predicate = "notes IS NOT NULL" }),
        // An access method (USING), a covering INCLUDE, descending / nulls ordering, and an expression key.
        new CreateIndex(new ObjectAddress("public", "users"), new TableIndex { Name = "idx_users_tags", Columns = ["tags"], Method = "gin" }),
        new CreateIndex(new ObjectAddress("public", "users"), new TableIndex
        {
            Name = "idx_users_recent",
            Columns =
            [
                new IndexColumn("created_at", Sort: IndexSort.Descending, Nulls: IndexNulls.Last),
                new IndexColumn(Expression: "lower(email)"),
            ],
            Include = ["id", "notes"],
        }),
        new DropIndex(new MemberAddress("public", "users", "idx_users_email")));

    [Fact]
    public Task ExclusionConstraintOperations() => VerifyActions(
        new AddExclusionConstraint(new ObjectAddress("public", "bookings"), new ExclusionConstraint
        {
            Name = "no_overlap",
            Elements = [new ExclusionElement("=", Column: "room"), new ExclusionElement("&&", Column: "during")],
            Method = "gist",
            Predicate = "room > 0",
        }),
        // An expression element is parenthesised.
        new AddExclusionConstraint(new ObjectAddress("public", "events"), new ExclusionConstraint
        {
            Name = "no_clash",
            Elements = [new ExclusionElement("&&", Expression: "tstzrange(starts, ends)")],
            Method = "gist",
        }),
        new DropExclusionConstraint(new MemberAddress("public", "bookings", "no_overlap")));

    // ── Triggers ──────────────────────────────────────────────────────────────

    [Fact]
    public Task TriggerOperations() => VerifyActions(
        new CreateTrigger(new ObjectAddress("public", "users"), new Trigger
        {
            Name = "users_audit",
            Timing = TriggerTiming.After,
            Events = TriggerEvent.Insert | TriggerEvent.Update,
            Function = new RoutineReference("public", "log_change"),
            Level = TriggerLevel.Row,
            UpdateOfColumns = ["email", "name"],
            When = "new.active",
            FunctionArguments = "'audit'",
        }),
        new CreateTrigger(new ObjectAddress("public", "logs"), new Trigger
        {
            Name = "logs_truncate",
            Timing = TriggerTiming.Before,
            Events = TriggerEvent.Truncate,
            Function = new RoutineReference(Schema: null, "on_truncate"),
            Level = TriggerLevel.Statement,
        }),
        new SetTriggerComment(new MemberAddress("public", "users", "users_audit"), null, "audit changes"),
        new DropTrigger(new MemberAddress("public", "users", "users_audit")));

    // ── Views ─────────────────────────────────────────────────────────────────

    [Fact]
    public Task ViewOperations() => VerifyActions(
        new CreateView("public", new View { Name = "active_users", Body = "SELECT id, email FROM public.users WHERE active" }),
        new RenameView(new ObjectAddress("public", "legacy_active"), "active_users"),
        new SetViewComment(new ObjectAddress("public", "active_users"), null, "Active users only"),
        new SetViewComment(new ObjectAddress("public", "active_users"), "Active users only", null),
        new DropView(new ObjectAddress("public", "active_users")));

    [Fact]
    public Task MaterializedViewOperations() => VerifyActions(
        // A materialized view: CREATE MATERIALIZED VIEW (never CREATE OR REPLACE), an index on it (a plain
        // CreateIndex), and the MATERIALIZED variants of rename/comment/drop.
        new CreateView("public", new View { Name = "daily_totals", Body = "SELECT date, sum(amount) AS total FROM public.sales GROUP BY date", IsMaterialized = true }),
        new CreateIndex(new ObjectAddress("public", "daily_totals"), new TableIndex { Name = "idx_daily_totals_date", Columns = ["date"], IsUnique = true }),
        new RenameView(new ObjectAddress("public", "legacy_totals"), "daily_totals", IsMaterialized: true),
        new SetViewComment(new ObjectAddress("public", "daily_totals"), null, "Daily rollup", IsMaterialized: true),
        new DropView(new ObjectAddress("public", "daily_totals"), IsMaterialized: true));

    // ── Enums ──────────────────────────────────────────────────────────────────

    [Fact]
    public Task EnumOperations() => VerifyActions(
        new CreateEnum("public", new EnumType { Name = "order_status", Values = ["pending", "shipped", "won't_ship"] }),
        new RenameEnum(new ObjectAddress("public", "order_state"), "order_status"),
        new AddEnumValue(new ObjectAddress("public", "order_status"), "delivered"),
        new AddEnumValue(new ObjectAddress("public", "order_status"), "draft", Before: "pending"),
        new AddEnumValue(new ObjectAddress("public", "order_status"), "in_transit", After: "shipped"),
        new SetEnumComment(new ObjectAddress("public", "order_status"), null, "Order lifecycle"),
        new SetEnumComment(new ObjectAddress("public", "order_status"), "Order lifecycle", null),
        new DropEnum(new ObjectAddress("public", "order_status")));

    // ── Composite types ──────────────────────────────────────────────────────

    [Fact]
    public Task CompositeTypeOperations() => VerifyActions(
        new CreateCompositeType("public", new CompositeType
        {
            Name = "address",
            Fields = [new CompositeField("street", SqlType.Text), new CompositeField("zip", SqlType.Int)],
        }),
        new AddCompositeField(new ObjectAddress("public", "address"), new CompositeField("country", SqlType.Text)),
        new AlterCompositeFieldType(new MemberAddress("public", "address", "zip"), SqlType.Int, SqlType.VarChar(10)),
        new DropCompositeField(new MemberAddress("public", "address", "country")),
        new RenameCompositeType(new ObjectAddress("public", "old_address"), "address"),
        new SetCompositeTypeComment(new ObjectAddress("public", "address"), null, "a postal address"),
        new DropCompositeType(new ObjectAddress("public", "address")));

    // ── Domains ────────────────────────────────────────────────────────────────

    [Fact]
    public Task DomainOperations() => VerifyActions(
        new CreateDomain("public", new DomainType
        {
            Name = "email",
            DataType = SqlType.Text,
            Default = "'n/a'",
            NotNull = true,
            Checks = [new CheckConstraint { Name = "email_fmt", Expression = "VALUE ~ '@'" }],
        }),
        new AlterDomainDefault(new ObjectAddress("public", "email"), "'n/a'", "'unknown'"),
        new AlterDomainDefault(new ObjectAddress("public", "email"), "'unknown'", null),
        new AlterDomainNotNull(new ObjectAddress("public", "email"), false),
        new AddDomainCheck(new ObjectAddress("public", "email"), new CheckConstraint { Name = "email_len", Expression = "length(VALUE) > 3" }),
        new DropDomainCheck(new MemberAddress("public", "email", "email_fmt")),
        // A base-type change recreates (drop + create, re-issuing the comment).
        new RecreateDomain("public", new DomainType { Name = "code", DataType = SqlType.VarChar(8), Comment = "a code" }),
        new RenameDomain(new ObjectAddress("public", "old_code"), "code"),
        new SetDomainComment(new ObjectAddress("public", "email"), null, "an email"),
        new DropDomain(new ObjectAddress("public", "email")));

    // ── Sequences ──────────────────────────────────────────────────────────────

    [Fact]
    public Task SequenceOperations() => VerifyActions(
        new CreateSequence("public", new Sequence { Name = "order_id" }),
        new CreateSequence("public", new Sequence
        {
            Name = "invoice_id",
            Options = new SequenceOptions(SqlType.SmallInt, StartWith: 100, IncrementBy: 5, MinValue: 10, MaxValue: 30000, Cache: 20, Cycle: true),
        }),
        new RenameSequence(new ObjectAddress("public", "bill_id"), "invoice_id"),
        // A mixed delta: one option changes value, every other resets to its engine default explicitly.
        new AlterSequence(new ObjectAddress("public", "invoice_id"),
            OldOptions: new SequenceOptions(SqlType.SmallInt, StartWith: 100, IncrementBy: 5, MinValue: 10, MaxValue: 30000, Cache: 20, Cycle: true),
            NewOptions: new SequenceOptions(IncrementBy: 50)),
        new SetSequenceComment(new ObjectAddress("public", "invoice_id"), null, "Invoice numbers"),
        new SetSequenceComment(new ObjectAddress("public", "invoice_id"), "Invoice numbers", null),
        new DropSequence(new ObjectAddress("public", "invoice_id")));

    // ── Extensions ────────────────────────────────────────────────────────────

    [Fact]
    public Task ExtensionOperations() => VerifyActions(
        new CreateExtension(new Extension { Name = "citext" }),
        new CreateExtension(new Extension { Name = "postgis", Version = "3.4" }),
        // A hyphenated name must be quoted.
        new CreateExtension(new Extension { Name = "uuid-ossp" }),
        new AlterExtension("postgis", "3.4", "3.5"),
        new SetExtensionComment("citext", null, "case-insensitive text"),
        new DropExtension("postgis"));

    // ── Functions ─────────────────────────────────────────────────────────────

    [Fact]
    public Task FunctionOperations() => VerifyActions(
        new CreateRoutine("public", Routine(RoutineKind.Function, "active_user_count", "",
            "RETURNS integer LANGUAGE sql AS $$ SELECT count(*) FROM public.users WHERE active $$")),
        new RenameRoutine(new ObjectAddress("public", "user_count"), "active_user_count", RoutineKind.Function),
        // A signature change: drop + recreate, re-issuing the comment the drop discarded.
        new RecreateRoutine("public", Routine(RoutineKind.Function, "add_numbers", "a integer, b integer, c integer DEFAULT 0",
            "RETURNS integer LANGUAGE sql AS $$ SELECT a + b + c $$", comment: "Adds numbers")),
        new RecreateRoutine("public", Routine(RoutineKind.Function, "subtract_numbers", "a integer, b integer",
            "RETURNS integer LANGUAGE sql AS $$ SELECT a - b $$")),
        new SetRoutineComment(new ObjectAddress("public", "active_user_count"), null, "Count of active users", RoutineKind.Function),
        new SetRoutineComment(new ObjectAddress("public", "active_user_count"), "Count of active users", null, RoutineKind.Function),
        new DropRoutine(new ObjectAddress("public", "active_user_count"), RoutineKind.Function));

    // ── Procedures ────────────────────────────────────────────────────────────

    [Fact]
    public Task ProcedureOperations() => VerifyActions(
        new CreateRoutine("public", Routine(RoutineKind.Procedure, "archive_users", "cutoff date",
            "LANGUAGE sql AS $$ DELETE FROM public.users WHERE created_at < cutoff $$")),
        new RenameRoutine(new ObjectAddress("public", "purge_users"), "archive_users", RoutineKind.Procedure),
        new RecreateRoutine("public", Routine(RoutineKind.Procedure, "archive_users", "cutoff timestamptz",
            "LANGUAGE sql AS $$ DELETE FROM public.users WHERE created_at < cutoff $$", comment: "Archives stale users")),
        new SetRoutineComment(new ObjectAddress("public", "archive_users"), null, "Archive job", RoutineKind.Procedure),
        new SetRoutineComment(new ObjectAddress("public", "archive_users"), "Archive job", null, RoutineKind.Procedure),
        new DropRoutine(new ObjectAddress("public", "archive_users"), RoutineKind.Procedure));

    // ── Comments ────────────────────────────────────────────────────────────────

    [Fact]
    public Task CommentOperations() => VerifyActions(
        new SetSchemaComment("public", null, "Application schema"),
        new SetTableComment(new ObjectAddress("public", "users"), null, "Registered users"),
        new SetColumnComment(new MemberAddress("public", "users", "email"), null, "Unique login address"),
        new SetIndexComment(new MemberAddress("public", "users", "idx_users_email"), null, "Lookup by email"),
        new SetTableComment(new ObjectAddress("public", "users"), "Registered users", null));

    // ── Grants ────────────────────────────────────────────────────────────────

    [Fact]
    public Task GrantOperations() => VerifyActions(
        new GrantSchemaUsage("public", "app_role"),
        new GrantTablePrivileges(new ObjectAddress("public", "users"), "app_role", TablePrivilege.Select | TablePrivilege.Insert),
        new GrantTablePrivileges(new ObjectAddress("public", "users"), "readonly", TablePrivilege.Select),
        new RevokeTablePrivileges(new ObjectAddress("public", "users"), "app_role", TablePrivilege.All),
        new RevokeSchemaUsage("public", "app_role"));

    // ── Type mapping ────────────────────────────────────────────────────────────

    [Fact]
    public Task TypeMapping_CoversAllSqlTypes() => VerifyActions(
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
        Alter(SqlType.Custom("citext")),
        // A schema-qualified user-defined type (e.g. a domain outside the search path) renders qualified.
        Alter(SqlType.Custom("app", "order_status")));

    private static AlterColumn Alter(SqlType type) =>
        new(new ObjectAddress("public", "t"), new Column { Name = "c", Type = type }, Type: new(SqlType.Int, type));

    // ── Scripts ───────────────────────────────────────────────────────────────

    [Fact]
    public void ExecuteScript_SqlIsEmittedVerbatimAndRunOutsideTransactionPropagates()
    {
        // Script SQL is user-authored for the target dialect, so it must pass through untouched — no quoting,
        // escaping or rewriting — and RunOutsideTransaction must carry onto the statement in both states
        // (e.g. CREATE INDEX CONCURRENTLY, which Postgres forbids inside a transaction).
        var ordinary = new ChangeScript("backfill",
            """UPDATE public."users" SET status = 'new' WHERE "status" IS NULL -- $body$ left alone""",
            new ChangeTarget("public", "users", "status", ChangeTrigger.AddColumn));
        var concurrent = new DeploymentScript("reindex", "CREATE INDEX CONCURRENTLY i ON s.t (c)", ScopeSchema: null, DeploymentPhase.Post)
        {
            RunOutsideTransaction = true,
        };

        var statements = new[] { new ExecuteScript(ordinary), new ExecuteScript(concurrent) }
            .SelectMany(action => Dialect.Generate(action).Require())
            .ToList();

        statements.Count.ShouldBe(2);
        statements[0].Sql.ShouldBe(ordinary.Sql);
        statements[0].RunOutsideTransaction.ShouldBeFalse();
        statements[1].Sql.ShouldBe(concurrent.Sql);
        statements[1].RunOutsideTransaction.ShouldBeTrue();
    }

    private static Routine Routine(RoutineKind kind, string name, string arguments, string definition, string? comment = null) => new()
    {
        Name = name,
        RoutineKind = kind,
        Arguments = arguments,
        Definition = definition,
        Comment = comment,
    };
}
