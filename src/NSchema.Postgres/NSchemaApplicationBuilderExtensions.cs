using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSchema.Postgres.Sql;

namespace NSchema.Postgres;

/// <summary>
/// Provides extension methods for configuring NSchema to use PostgreSQL as the underlying database provider.
/// </summary>
public static class NSchemaApplicationBuilderExtensions
{
    extension(NSchemaApplicationBuilder builder)
    {
        /// <summary>
        /// Configures NSchema to use PostgreSQL as the database provider with the specified connection string.
        /// </summary>
        /// <param name="connectionString">The connection string to the PostgreSQL database.</param>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UsePostgres(string connectionString)
        {
            builder.Services.AddNpgsqlDataSource(connectionString);
            return builder.UsePostgres();
        }

        /// <summary>
        /// Configures NSchema to use PostgreSQL as the database provider with a custom configuration action for the NpgsqlDataSourceBuilder.
        /// </summary>
        /// <param name="configure">A delegate that can be used to configure the NpgsqlDataSourceBuilder.</param>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UsePostgres(Action<NpgsqlDataSourceBuilder> configure)
        {
            builder.Services.AddNpgsqlDataSource("", configure);
            return builder.UsePostgres();
        }

        /// <summary>
        /// Configures NSchema to use PostgreSQL as the database provider with a custom configuration action for the NpgsqlDataSourceBuilder that has access to the IServiceProvider.
        /// </summary>
        /// <param name="configure">A delegate that can be used to configure the NpgsqlDataSourceBuilder.</param>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UsePostgres(Action<IServiceProvider, NpgsqlDataSourceBuilder> configure)
        {
            builder.Services.AddNpgsqlDataSource("", configure);
            return builder.UsePostgres();
        }

        /// <summary>
        /// Configures NSchema to use PostgreSQL as the database provider by registering the introspector, SQL dialect and equivalence rules.
        /// </summary>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UsePostgres() => builder
            .UseDatabaseIntrospector<PostgresDatabaseIntrospector>()
            .UsePostgresDialect()
            .UsePostgresEquivalence();

        /// <summary>
        /// Configures the NSchema application to render SQL with the PostgreSQL dialect.
        /// </summary>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UsePostgresDialect() => builder.UseSqlDialect<PostgresSqlDialect>();

        /// <summary>
        /// Configures the NSchema application to compare schemas with the PostgreSQL equivalence rules.
        /// </summary>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UsePostgresEquivalence() => builder.UseSqlEquivalence<PostgresSqlEquivalence>();
    }
}
