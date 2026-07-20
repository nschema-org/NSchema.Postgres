using NSchema.Plan.Model;
using NSchema.Plan.Model.CompositeTypes;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{

    // ── Composite types ───────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateCompositeType(CreateCompositeType action)
    {
        var fields = string.Join(", ", action.CompositeType.Fields.Select(f => $"{Quote(f.Name)} {ToPostgresType(f.DataType)}"));
        return Statement($"CREATE TYPE {Qualify(action.SchemaName, action.CompositeType.Name)} AS ({fields})");
    }

    protected override Result<IReadOnlyList<SqlStatement>> DropCompositeType(DropCompositeType action) =>
        Statement($"DROP TYPE {Qualify(action.Type)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameCompositeType(RenameCompositeType action) =>
        Statement($"ALTER TYPE {Qualify(action.Type)} RENAME TO {Quote(action.NewName)}");

    protected override Result<IReadOnlyList<SqlStatement>> AddCompositeField(AddCompositeField action) =>
        Statement($"ALTER TYPE {Qualify(action.Type)} ADD ATTRIBUTE {Quote(action.Field.Name)} {ToPostgresType(action.Field.DataType)}");

    protected override Result<IReadOnlyList<SqlStatement>> DropCompositeField(DropCompositeField action) =>
        Statement($"ALTER TYPE {Qualify(action.Field.Owner)} DROP ATTRIBUTE {Quote(action.Field.Member)}");

    protected override Result<IReadOnlyList<SqlStatement>> AlterCompositeFieldType(AlterCompositeFieldType action) =>
        Statement($"ALTER TYPE {Qualify(action.Field.Owner)} ALTER ATTRIBUTE {Quote(action.Field.Member)} TYPE {ToPostgresType(action.NewType)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetCompositeTypeComment(SetCompositeTypeComment action) =>
        Comment($"TYPE {Qualify(action.Type)}", action.NewComment);
}
