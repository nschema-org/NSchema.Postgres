using NSchema.Model;
using NSchema.Postgres.Sql;

namespace NSchema.Postgres.Tests.Sql;

public sealed class NativeTypeNameNormalizationTests
{
    [Theory]
    [InlineData("uuid", "guid")]
    [InlineData("bool", "boolean")]
    [InlineData("int2", "smallint")]
    [InlineData("int4", "int")]
    [InlineData("int8", "bigint")]
    [InlineData("float4", "float")]
    [InlineData("float8", "double")]
    [InlineData("timestamp", "datetime")]
    [InlineData("timestamptz", "datetimeoffset")]
    [InlineData("numeric", "decimal")]
    [InlineData("bpchar", "char")]
    [InlineData("char", "char")]
    [InlineData("varchar", "varchar")]
    [InlineData("bytea", "varbinary")]
    [InlineData("text", "text")]
    [InlineData("tsvector", "tsvector")]
    [InlineData("_text", "_text")]
    [InlineData("citext", "citext")]
    public void NormalizeNativeTypeName_YieldsTheModelsCanonicalSpelling(string typeName, string expected) =>
        PostgresDatabaseIntrospector.NormalizeNativeTypeName(typeName).ShouldBe(new SqlIdentifier(expected));
}
