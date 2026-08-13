using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.Sequences;
using NSchema.Postgres.Sql;

namespace NSchema.Postgres.Tests.Sql;

/// <summary>
/// Pins <see cref="PostgresSqlEquivalence"/>: the spellings Postgres and a project may legitimately disagree
/// on — a stored literal's cast, a <c>public</c>/<c>pg_catalog</c> type qualifier, a sequence option declared
/// with the value the engine would have chosen anyway — compare equal in either direction, while real differences
/// survive. Pure unit tests — no Docker.
/// </summary>
public sealed class PostgresSqlEquivalenceTests
{
    private readonly PostgresSqlEquivalence _sut = new();

    // ── Defaults ──────────────────────────────────────────────────────────────

    [Fact]
    public void Defaults_StringLiteralCast_MatchesBareLiteral()
        => AssertDefaultsEqual("'internal'::text", "'internal'");

    [Fact]
    public void Defaults_MultiWordTypeCast_MatchesBareLiteral()
        => AssertDefaultsEqual("'internal'::character varying", "'internal'");

    [Fact]
    public void Defaults_QualifiedEnumCast_MatchesBareLiteral()
        => AssertDefaultsEqual("'draft'::identity.scope_type", "'draft'");

    [Fact]
    public void Defaults_ArrayTypeCast_MatchesBareLiteral()
        => AssertDefaultsEqual("'{}'::text[]", "'{}'");

    [Fact]
    public void Defaults_EscapedQuoteInLiteral_MatchesBareLiteral()
        => AssertDefaultsEqual("'it''s'::text", "'it''s'");

    [Fact]
    public void Defaults_NumericCast_MatchesBareNumber()
        // DEFAULT -1 round-trips as '-1'::integer.
        => AssertDefaultsEqual("'-1'::integer", "-1");

    [Fact]
    public void Defaults_DecimalCast_MatchesBareNumber()
        => AssertDefaultsEqual("'0.5'::numeric", "0.5");

    [Fact]
    public void Defaults_BothSidesSpellTheCast_Match()
        => AssertDefaultsEqual("'internal'::text", "'internal'::text");

    [Fact]
    public void Defaults_NumericLookingTextDefault_DoesNotMatchBareNumber()
        // '5'::text is a string default that happens to look numeric — the quotes are real.
        => _sut.Defaults.Equals(new SqlDefaultExpression("'5'::text"), new SqlDefaultExpression("5"))
            .ShouldBeFalse();

    [Fact]
    public void Defaults_CastInsideLargerExpression_IsNotFolded()
        => _sut.Defaults.Equals(new SqlDefaultExpression("'a'::text || 'b'::text"), new SqlDefaultExpression("'a' || 'b'"))
            .ShouldBeFalse();

    [Fact]
    public void Defaults_DifferentLiterals_DoNotMatch()
        => _sut.Defaults.Equals(new SqlDefaultExpression("'internal'::text"), new SqlDefaultExpression("'external'"))
            .ShouldBeFalse();

    [Fact]
    public void Defaults_TrailingTerminator_StillFolds()
        // The cosmetic baseline still applies beneath the cast folding.
        => AssertDefaultsEqual("now() ;", "now()");

    // ── Types ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Types_PgCatalogQualifier_MatchesBareName()
        => AssertTypesEqual(SqlType.Custom("pg_catalog", "jsonb"), SqlType.Custom("jsonb"));

    [Fact]
    public void Types_PublicQualifier_MatchesBareName()
        => AssertTypesEqual(SqlType.Custom("public", "order_status"), SqlType.Custom("order_status"));

    [Fact]
    public void Types_UserSchemaQualifier_IsSignificant()
        => _sut.Types.Equals(SqlType.Custom("app", "order_status"), SqlType.Custom("order_status"))
            .ShouldBeFalse();

    [Fact]
    public void Types_DifferentNames_DoNotMatch()
        => _sut.Types.Equals(SqlType.Custom("pg_catalog", "jsonb"), SqlType.Custom("json"))
            .ShouldBeFalse();

    [Fact]
    public void Types_BuiltIn_MatchesItself()
        => AssertTypesEqual(SqlType.VarChar(255), SqlType.VarChar(255));

    [Theory]
    [MemberData(nameof(RenderedAlike))]
    public void Types_CanonicalNamesTheDialectRendersAlike_Match(SqlType canonical, SqlType native)
        => AssertTypesEqual(canonical, native);

    /// <summary>
    /// The canonical spellings <c>ToPostgresType</c> renders onto a type Postgres has, paired with that type.
    /// The engine's own vocabulary only ever names the right-hand side.
    /// </summary>
    public static TheoryData<SqlType, SqlType> RenderedAlike() => new()
    {
        { SqlType.TinyInt, SqlType.SmallInt },
        { SqlType.NChar(4), SqlType.Char(4) },
        { SqlType.NVarChar(64), SqlType.VarChar(64) },
        { SqlType.NVarChar(), SqlType.VarChar() },
        { SqlType.Binary(16), SqlType.VarBinary() },
    };

    [Fact]
    public void Types_VarBinaryLength_IsNotSignificant()
        // bytea has no length to carry, so declaring one cannot be a difference the plan could act on.
        => AssertTypesEqual(SqlType.VarBinary(32), SqlType.VarBinary());

    [Fact]
    public void Types_LengthOnATypeThatCarriesOne_IsStillSignificant()
        => _sut.Types.Equals(SqlType.VarChar(32), SqlType.VarChar(64)).ShouldBeFalse();

    // ── Sequence options ──────────────────────────────────────────────────────

    [Fact]
    public void Fold_SequenceDeclaringNothing_IsUnchanged()
        => _sut.Fold(new SequenceOptions()).ShouldBe(new SequenceOptions());

    [Fact]
    public void Fold_SequenceDeclaringTheEngineDefaults_FoldsToNothingDeclared()
        // The whole point: a project that says out loud what Postgres would have chosen anyway has to compare equal
        // to the catalog row, which cannot report which of the two happened.
        => _sut.Fold(new SequenceOptions(
                DataType: SqlType.BigInt, StartWith: 1, IncrementBy: 1, MinValue: 1, MaxValue: long.MaxValue, Cache: 1))
            .ShouldBe(new SequenceOptions());

    [Fact]
    public void Fold_SequenceDeclaringTheDescendingDefaults_KeepsOnlyTheIncrement()
        => _sut.Fold(new SequenceOptions(StartWith: -1, IncrementBy: -1, MinValue: long.MinValue, MaxValue: -1))
            .ShouldBe(new SequenceOptions(IncrementBy: -1));

    [Fact]
    public void Fold_SequenceStartFollowingADeclaredMinimum_FoldsTheStartOnly()
        // CREATE SEQUENCE q MINVALUE 5 starts at 5, so a declared START 5 is the default while the minimum is not.
        => _sut.Fold(new SequenceOptions(StartWith: 5, MinValue: 5)).ShouldBe(new SequenceOptions(MinValue: 5));

    [Fact]
    public void Fold_SequenceOptionsThatDifferFromTheDefaults_AreKept()
        => _sut.Fold(new SequenceOptions(SqlType.Int, StartWith: 20, IncrementBy: 5, MinValue: 10, MaxValue: 1000, Cache: 10, Cycle: true))
            .ShouldBe(new SequenceOptions(SqlType.Int, StartWith: 20, IncrementBy: 5, MinValue: 10, MaxValue: 1000, Cache: 10, Cycle: true));

    [Fact]
    public void Fold_SequenceBoundsFollowTheDeclaredType()
        // int's maximum is the default ceiling for an integer sequence and a real one for a bigint sequence.
        => _sut.Fold(new SequenceOptions(SqlType.Int, MaxValue: int.MaxValue)).ShouldBe(new SequenceOptions(SqlType.Int));

    [Fact]
    public void Fold_IntegerMaximumOnABigintSequence_IsKept()
        => _sut.Fold(new SequenceOptions(MaxValue: int.MaxValue)).ShouldBe(new SequenceOptions(MaxValue: int.MaxValue));

    // ── Identity options ──────────────────────────────────────────────────────

    [Fact]
    public void Fold_IdentityDeclaringTheEngineDefaults_FoldsToNothingDeclared()
        // pg_sequence reports a start and a minimum for every identity, asked for or not.
        => _sut.Fold(new IdentityOptions(StartWith: 1, MinValue: 1, IncrementBy: 1), SqlType.BigInt)
            .ShouldBe(new IdentityOptions(null, null, null));

    [Fact]
    public void Fold_IdentityStartFollowingADeclaredMinimum_FoldsTheStartOnly()
        => _sut.Fold(new IdentityOptions(StartWith: 5, MinValue: 5, IncrementBy: null), SqlType.Int)
            .ShouldBe(new IdentityOptions(null, 5, null));

    [Fact]
    public void Fold_IdentityOptionsThatDifferFromTheDefaults_AreKept()
        => _sut.Fold(new IdentityOptions(StartWith: 100, MinValue: 10, IncrementBy: 5), SqlType.Int)
            .ShouldBe(new IdentityOptions(100, 10, 5));

    [Fact]
    public void Fold_IdentityKeepsNotForReplication()
        // Not an option Postgres has a default for, and it has to survive the trip.
        => _sut.Fold(new IdentityOptions(1, 1, 1, NotForReplication: true), SqlType.BigInt)
            .NotForReplication.ShouldBeTrue();

    private void AssertDefaultsEqual(string x, string y)
    {
        // Equivalence is symmetric — neither side's spelling is the sanctioned one — and equal values hash equal.
        _sut.Defaults.Equals(new SqlDefaultExpression(x), new SqlDefaultExpression(y)).ShouldBeTrue();
        _sut.Defaults.Equals(new SqlDefaultExpression(y), new SqlDefaultExpression(x)).ShouldBeTrue();
        _sut.Defaults.GetHashCode(new SqlDefaultExpression(x)).ShouldBe(_sut.Defaults.GetHashCode(new SqlDefaultExpression(y)));
    }

    private void AssertTypesEqual(SqlType x, SqlType y)
    {
        _sut.Types.Equals(x, y).ShouldBeTrue();
        _sut.Types.Equals(y, x).ShouldBeTrue();
        _sut.Types.GetHashCode(x).ShouldBe(_sut.Types.GetHashCode(y));
    }
}
