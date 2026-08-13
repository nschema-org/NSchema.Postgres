using System.Globalization;
using NSchema.Diff.Plugins;
using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.Sequences;

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

    /// <inheritdoc/>
    /// <remarks>
    /// <c>pg_sequence</c> holds a row of concrete values whatever was declared, so every option the engine would
    /// have chosen anyway folds back to <see langword="null"/>: <c>bigint</c>, <c>INCREMENT BY 1</c>,
    /// <c>CACHE 1</c>, the bound at the ascending or descending end of the type, and the start that follows from
    /// the effective bound — <c>CREATE SEQUENCE q MINVALUE 5</c> starts at 5, not at 1.
    /// </remarks>
    public override SequenceOptions WithDefaults(SequenceOptions options) => FoldOptions(options);

    /// <inheritdoc cref="WithDefaults(SequenceOptions)"/>
    /// <remarks>Static so introspection folds a catalog row through the same rules the comparison uses.</remarks>
    internal static SequenceOptions FoldOptions(SequenceOptions options)
    {
        var bounds = Bounds(options.DataType, options.IncrementBy);
        var start = options.IncrementBy is null or > 0
            ? options.MinValue ?? bounds.Min
            : options.MaxValue ?? bounds.Max;

        return new SequenceOptions(
            DataType: IsBigInt(options.DataType) ? null : options.DataType,
            StartWith: options.StartWith == start ? null : options.StartWith,
            IncrementBy: options.IncrementBy == 1 ? null : options.IncrementBy,
            MinValue: options.MinValue == bounds.Min ? null : options.MinValue,
            MaxValue: options.MaxValue == bounds.Max ? null : options.MaxValue,
            Cache: options.Cache == 1 ? null : options.Cache,
            Cycle: options.Cycle);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// An identity is a sequence Postgres owns, and <c>pg_sequence</c> reports its minimum and start whether or
    /// not either was declared — so a column that asked only to be an identity reads back carrying both, and
    /// differs from itself on every deploy until they are folded away.
    /// </remarks>
    public override IdentityOptions WithDefaults(IdentityOptions options, SqlType columnType) => FoldOptions(options, columnType);

    /// <inheritdoc cref="WithDefaults(IdentityOptions, SqlType)"/>
    /// <remarks>Static so introspection folds a catalog row through the same rules the comparison uses.</remarks>
    internal static IdentityOptions FoldOptions(IdentityOptions options, SqlType columnType)
    {
        var bounds = Bounds(columnType, options.IncrementBy);
        var start = options.IncrementBy is null or > 0 ? options.MinValue ?? bounds.Min : bounds.Max;

        return new IdentityOptions(
            StartWith: options.StartWith == start ? null : options.StartWith,
            MinValue: options.MinValue == bounds.Min ? null : options.MinValue,
            IncrementBy: options.IncrementBy == 1 ? null : options.IncrementBy,
            NotForReplication: options.NotForReplication);
    }

    // The bounds a sequence of this type takes when neither end is declared: an ascending one runs from 1 to the
    // type's maximum, a descending one from the type's minimum to -1.
    private static (long Min, long Max) Bounds(SqlType? dataType, long? increment)
    {
        var (typeMin, typeMax) = TypeRange(dataType);
        return increment is null or > 0 ? (1L, typeMax) : (typeMin, -1L);
    }

    // Postgres has no tinyint; the dialect renders one as smallint, so it carries smallint's range.
    private static (long Min, long Max) TypeRange(SqlType? dataType) => dataType?.Name.Value switch
    {
        "tinyint" or "smallint" => (short.MinValue, short.MaxValue),
        "int" => (int.MinValue, int.MaxValue),
        _ => (long.MinValue, long.MaxValue),
    };

    private static bool IsBigInt(SqlType? dataType) => dataType is null || dataType.Name.Value == "bigint";

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
