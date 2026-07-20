using NSchema.Postgres.Models;

namespace NSchema.Postgres.Tests.Sql;

/// <summary>
/// Pins <see cref="PostgresSchemaProvider.NormalizeSequenceOptions"/>: Postgres engine defaults must fold to
/// null so a bare <c>CREATE SEQUENCE</c> introspects to an all-null <see cref="SequenceOptions"/> and the core's
/// record-equality comparison sees no drift. Pure unit tests — no Docker.
/// </summary>
public sealed class SequenceOptionsNormalizationTests
{
    private static SequenceRow Row(
        string dataType = "bigint",
        long? start = null,
        long increment = 1,
        long? min = null,
        long? max = null,
        long cache = 1,
        bool cycle = false)
    {
        var ascending = increment > 0;
        var (typeMin, typeMax) = dataType switch
        {
            "smallint" => ((long)short.MinValue, (long)short.MaxValue),
            "integer" => ((long)int.MinValue, (long)int.MaxValue),
            _ => (long.MinValue, long.MaxValue),
        };
        var effectiveMin = min ?? (ascending ? 1L : typeMin);
        var effectiveMax = max ?? (ascending ? typeMax : -1L);
        var effectiveStart = start ?? (ascending ? effectiveMin : effectiveMax);
        return new SequenceRow("app", "q", dataType, effectiveStart, increment, effectiveMin, effectiveMax, cache, cycle);
    }

    [Fact]
    public void Normalize_BareAscendingBigint_IsAllNull()
        => PostgresSchemaProvider.NormalizeSequenceOptions(Row())
            .ShouldBe(new SequenceOptions());

    [Fact]
    public void Normalize_BareDescendingBigint_KeepsOnlyIncrement()
        => PostgresSchemaProvider.NormalizeSequenceOptions(Row(increment: -1))
            .ShouldBe(new SequenceOptions(IncrementBy: -1));

    [Fact]
    public void Normalize_BareInteger_KeepsOnlyDataType()
        // Proves the format_type ↔ SqlType.Parse bridge: "integer" must come back as the core's canonical Int.
        => PostgresSchemaProvider.NormalizeSequenceOptions(Row(dataType: "integer"))
            .ShouldBe(new SequenceOptions(DataType: SqlType.Int));

    [Fact]
    public void Normalize_BareSmallint_KeepsOnlyDataType()
        => PostgresSchemaProvider.NormalizeSequenceOptions(Row(dataType: "smallint"))
            .ShouldBe(new SequenceOptions(DataType: SqlType.SmallInt));

    [Fact]
    public void Normalize_StartEqualToEffectiveMinValue_FoldsStartToNull()
        // CREATE SEQUENCE q MINVALUE 5 starts at 5 — the default start tracks the effective bound, not 1.
        => PostgresSchemaProvider.NormalizeSequenceOptions(Row(min: 5, start: 5))
            .ShouldBe(new SequenceOptions(MinValue: 5));

    [Fact]
    public void Normalize_StartDifferentFromEffectiveMinValue_IsKept()
        => PostgresSchemaProvider.NormalizeSequenceOptions(Row(min: 5, start: 20))
            .ShouldBe(new SequenceOptions(StartWith: 20, MinValue: 5));

    [Fact]
    public void Normalize_DescendingStartEqualToEffectiveMaxValue_FoldsStartToNull()
        => PostgresSchemaProvider.NormalizeSequenceOptions(Row(increment: -2, max: -5, start: -5))
            .ShouldBe(new SequenceOptions(IncrementBy: -2, MaxValue: -5));

    [Fact]
    public void Normalize_FullyOptionedRow_PreservesEveryOption()
        => PostgresSchemaProvider.NormalizeSequenceOptions(
                new SequenceRow("app", "q", "integer", Start: 20, Increment: 5, MinValue: 10, MaxValue: 1000, Cache: 10, Cycle: true))
            .ShouldBe(new SequenceOptions(SqlType.Int, StartWith: 20, IncrementBy: 5, MinValue: 10, MaxValue: 1000, Cache: 10, Cycle: true));

    [Fact]
    public void Normalize_CacheAboveOne_IsKept()
        => PostgresSchemaProvider.NormalizeSequenceOptions(Row(cache: 20))
            .ShouldBe(new SequenceOptions(Cache: 20));

    [Fact]
    public void Normalize_Cycle_IsKept()
        => PostgresSchemaProvider.NormalizeSequenceOptions(Row(cycle: true))
            .ShouldBe(new SequenceOptions(Cycle: true));
}
