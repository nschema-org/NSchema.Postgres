using System.Text;
using NSchema.Model;
using NSchema.Model.Domains;
using NSchema.Plan.Domain;
using NSchema.Plan.Domain.Domains;

namespace NSchema.Postgres.Sql;

internal sealed partial class PostgresSqlDialect
{

    // ── Domains ───────────────────────────────────────────────────────────────

    protected override Result<IReadOnlyList<SqlStatement>> CreateDomain(CreateDomain action) =>
        Statement(BuildCreateDomain(action.SchemaName, action.DomainType));

    protected override Result<IReadOnlyList<SqlStatement>> DropDomain(DropDomain action) =>
        Statement($"DROP DOMAIN {Qualify(action.Domain)}");

    protected override Result<IReadOnlyList<SqlStatement>> RenameDomain(RenameDomain action) =>
        Statement($"ALTER DOMAIN {Qualify(action.Domain)} RENAME TO {Quote(action.NewName)}");

    // A domain's base type cannot be altered in place (Postgres has no ALTER DOMAIN … TYPE), so a base-type change
    // drops + recreates — re-issuing the comment the drop discarded. Fails loudly if a column still uses the domain.
    protected override Result<IReadOnlyList<SqlStatement>> RecreateDomain(RecreateDomain action)
    {
        var statements = new List<SqlStatement>
        {
            new($"DROP DOMAIN {Qualify(action.SchemaName, action.DomainType.Name)}"),
            new(BuildCreateDomain(action.SchemaName, action.DomainType)),
        };
        if (action.DomainType.Comment is { } comment)
        {
            statements.Add(new SqlStatement($"COMMENT ON DOMAIN {Qualify(action.SchemaName, action.DomainType.Name)} IS $comment${comment}$comment$"));
        }

        return Statements([.. statements]);
    }

    protected override Result<IReadOnlyList<SqlStatement>> AlterDomainDefault(AlterDomainDefault action) =>
        Statement(action.NewDefault is { } newDefault
            ? $"ALTER DOMAIN {Qualify(action.Domain)} SET DEFAULT {newDefault.Value}"
            : $"ALTER DOMAIN {Qualify(action.Domain)} DROP DEFAULT");

    protected override Result<IReadOnlyList<SqlStatement>> AlterDomainNotNull(AlterDomainNotNull action) =>
        Statement(action.NotNull
            ? $"ALTER DOMAIN {Qualify(action.Domain)} SET NOT NULL"
            : $"ALTER DOMAIN {Qualify(action.Domain)} DROP NOT NULL");

    protected override Result<IReadOnlyList<SqlStatement>> AddDomainCheck(AddDomainCheck action) =>
        Statement($"ALTER DOMAIN {Qualify(action.Domain)} ADD CONSTRAINT {Quote(action.Check.Name)} CHECK ({action.Check.Expression.Value})");

    protected override Result<IReadOnlyList<SqlStatement>> DropDomainCheck(DropDomainCheck action) =>
        Statement($"ALTER DOMAIN {Qualify(action.Check.Owner)} DROP CONSTRAINT {Quote(action.Check.Member)}");

    protected override Result<IReadOnlyList<SqlStatement>> SetDomainComment(SetDomainComment action) =>
        Comment($"DOMAIN {Qualify(action.Domain)}", action.NewComment);

    // CREATE DOMAIN name AS type [DEFAULT expr] [NOT NULL] [CONSTRAINT n CHECK (expr)]…
    private string BuildCreateDomain(SqlIdentifier schema, DomainType domain)
    {
        var sql = new StringBuilder($"CREATE DOMAIN {Qualify(schema, domain.Name)} AS {ToPostgresType(domain.DataType)}");
        if (domain.Default is { } def)
        {
            sql.Append($" DEFAULT {def.Value}");
        }
        if (domain.NotNull)
        {
            sql.Append(" NOT NULL");
        }
        foreach (var check in domain.Checks)
        {
            sql.Append($" CONSTRAINT {Quote(check.Name)} CHECK ({check.Expression.Value})");
        }
        return sql.ToString();
    }
}
