namespace NSchema.Postgres.Tests.Sql;

/// <summary>
/// Pins <see cref="PostgresSchemaProvider.NormalizeProcedureArguments"/>: Postgres renders a procedure's argument
/// list with the default <c>IN</c> mode spelled out, which must fold away so the idiomatic declaration
/// (<c>a integer</c>) compares clean — while the signature-bearing modes survive. Pure unit tests — no Docker.
/// </summary>
public sealed class ProcedureArgumentNormalizationTests
{
    [Fact]
    public void Normalize_EmptyArgumentList_StaysEmpty()
        => PostgresSchemaProvider.NormalizeProcedureArguments("")
            .ShouldBe("");

    [Fact]
    public void Normalize_SingleInArgument_StripsMode()
        => PostgresSchemaProvider.NormalizeProcedureArguments("IN a integer")
            .ShouldBe("a integer");

    [Fact]
    public void Normalize_MultipleInArguments_StripsEveryMode()
        => PostgresSchemaProvider.NormalizeProcedureArguments("IN a integer, IN b text")
            .ShouldBe("a integer, b text");

    [Fact]
    public void Normalize_NonDefaultModes_ArePreserved()
        => PostgresSchemaProvider.NormalizeProcedureArguments("IN a integer, OUT b integer, INOUT c text, VARIADIC d integer[]")
            .ShouldBe("a integer, OUT b integer, INOUT c text, VARIADIC d integer[]");

    [Fact]
    public void Normalize_ArgumentNamedLikeMode_IsNotMangled()
        // Only the standalone mode word is stripped — an argument whose name merely starts with "IN" survives.
        => PostgresSchemaProvider.NormalizeProcedureArguments("INDEX integer")
            .ShouldBe("INDEX integer");

    [Fact]
    public void Normalize_CommaInsideParenthesisedDefault_DoesNotSplitTheArgument()
        => PostgresSchemaProvider.NormalizeProcedureArguments("IN a text DEFAULT repeat('x'::text, 3), IN b integer")
            .ShouldBe("a text DEFAULT repeat('x'::text, 3), b integer");

    [Fact]
    public void Normalize_CommaInsideQuotedDefault_DoesNotSplitTheArgument()
        => PostgresSchemaProvider.NormalizeProcedureArguments("IN a text DEFAULT 'one, IN two', IN b integer")
            .ShouldBe("a text DEFAULT 'one, IN two', b integer");
}
