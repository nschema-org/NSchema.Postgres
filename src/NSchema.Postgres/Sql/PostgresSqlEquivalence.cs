using System.Globalization;
using NSchema.Diff.Plugins;
using NSchema.Model;
using NSchema.Model.Columns;

namespace NSchema.Postgres.Sql;

/// <summary>
/// Postgres equivalence rules so spellings the catalog and a project may disagree on compare as equal.
/// </summary>
public sealed class PostgresSqlEquivalence : SqlEquivalence
{
    /// <inheritdoc/>
    /// <remarks>
    /// Postgres stores a literal default with an explicit cast — <c>DEFAULT 'internal'</c> on a text column
    /// reads back as <c>'internal'::text</c>, <c>DEFAULT -1</c> as <c>'-1'::integer</c> — so both sides fold
    /// the cast away (unquoting the literal when the cast target is numeric) before the cosmetic comparison.
    /// </remarks>
    public override IEqualityComparer<SqlDefaultExpression> Defaults { get; } = new DefaultEquality();

    /// <inheritdoc/>
    /// <remarks>
    /// A type in <c>public</c> or <c>pg_catalog</c> is addressable bare, so the qualifier folds away;
    /// a type in any other schema keeps it.
    /// </remarks>
    public override IEqualityComparer<SqlType> Types { get; } = new TypeEquality();

    /// <summary>
    /// Folds the cast Postgres adds when it stores a literal default: the whole expression must be a single
    /// quoted literal cast to a type name; anything larger is left untouched.
    /// </summary>
    private static string? FoldDefaultExpression(string? expression)
    {
        if (expression is null || !expression.StartsWith('\''))
        {
            return expression;
        }

        var end = FindLiteralEnd(expression);
        if (end < 0 || !IsCastToTypeName(expression, end + 1, out var target))
        {
            return expression;
        }

        var literal = expression[..(end + 1)];
        return IsNumericType(target) && IsNumericLiteral(literal[1..^1]) ? literal[1..^1] : literal;
    }

    // Index of the literal's closing quote, honouring '' escapes; -1 if unterminated.
    private static int FindLiteralEnd(string expression)
    {
        for (var i = 1; i < expression.Length; i++)
        {
            if (expression[i] != '\'')
            {
                continue;
            }
            if (i + 1 < expression.Length && expression[i + 1] == '\'')
            {
                i++;
                continue;
            }
            return i;
        }
        return -1;
    }

    // True when the remainder is "::<type name>" and nothing else — an operator or second literal means the
    // cast is part of a larger expression, which is left untouched.
    private static bool IsCastToTypeName(string expression, int start, out string target)
    {
        target = "";
        if (start + 2 >= expression.Length || expression[start] != ':' || expression[start + 1] != ':')
        {
            return false;
        }
        target = expression[(start + 2)..];
        return target.All(c => char.IsLetterOrDigit(c) || c is '_' or ' ' or '.' or '"' or '[' or ']' or '(' or ')' or ',');
    }

    private static bool IsNumericType(string target) =>
        target is "smallint" or "integer" or "bigint" or "real" or "double precision"
        || target.StartsWith("numeric", StringComparison.Ordinal);

    private static bool IsNumericLiteral(string content) =>
        decimal.TryParse(content, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);

    private sealed class DefaultEquality : IEqualityComparer<SqlDefaultExpression>
    {
        public bool Equals(SqlDefaultExpression? x, SqlDefaultExpression? y) =>
            SqlDefaultExpression.CosmeticComparer.Equals(Fold(x), Fold(y));

        public int GetHashCode(SqlDefaultExpression obj) =>
            SqlDefaultExpression.CosmeticComparer.GetHashCode(Fold(obj)!);

        private static SqlDefaultExpression? Fold(SqlDefaultExpression? value) =>
            value is null ? null : new SqlDefaultExpression(FoldDefaultExpression(value.Value)!);
    }

    private sealed class TypeEquality : IEqualityComparer<SqlType>
    {
        public bool Equals(SqlType? x, SqlType? y) => object.Equals(Fold(x), Fold(y));

        public int GetHashCode(SqlType obj) => Fold(obj)!.GetHashCode();

        private static SqlType? Fold(SqlType? type)
        {
            if (type is null)
            {
                return null;
            }

            // The canonical names the dialect renders onto a type Postgres already has. Without this the engine's
            // vocabulary — read from its own catalog, so it never contains these — cannot resolve a reference the
            // dialect renders perfectly well, and a portable schema is refused rather than applied.
            var folded = type.Name.Value switch
            {
                "tinyint" => type with { Name = new SqlIdentifier("smallint") },
                "nchar" => type with { Name = new SqlIdentifier("char") },
                "nvarchar" => type with { Name = new SqlIdentifier("varchar") },
                "binary" => type with { Name = new SqlIdentifier("varbinary") },
                _ => type,
            };

            // bytea carries no length, so a declared one is never read back and must not read as a difference.
            if (folded is { Name.Value: "varbinary", Length: not null })
            {
                folded = folded with { Length = null };
            }

            return folded.Schema?.Value is "public" or "pg_catalog" ? folded with { Schema = null } : folded;
        }
    }
}
