# Changelog

All notable changes to NSchema will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project (mostly) adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Versioning policy

This package uses **lockstep major versioning** with the core NSchema package: `NSchema.Postgres X.*.*` requires `NSchema.Core X.*.*`, so version compatibility is always clear.

As a consequence, breaking changes that are specific to this provider (rather than the core API) are signalled by a **minor version bump** rather than a major one, and called out explicitly in this changelog.

## [4.3.0] - 2026-07-09

### Added

- Support for the `MIGRATION FOR` data migrations introduced in NSchema.Core 4.3.

### Changed

- A plan action this provider doesn't recognize now reports that the plan may come from a newer NSchema.Core than the provider supports, and to check for a provider update.

### Fixed

- Schema introspection no longer surfaces the schema owner's implicit `USAGE` self-grant, which materializes in the ACL once any schema grant is applied and read as a phantom "revoke from the owner" on the next plan. Table grants already excluded the owner; schema grants now do the same.

## [4.0.0] - 2026-07-01

### Added

- Added plugin manifest to allow for automatic registration of the provider coming in `NSchema 4.0.0.

## [3.0.1] - 2026-02-24

### Fixed

- The Postgres provider will now no-longer call `CASCADE` its `DROP SCHEMA` actions, to behave more consistently with other providers that do not support it.

## [3.0.0] - 2026-06-20

### Added

- `NSchemaApplicationBuilder.UseCurrentSchemaPostgres` extension for registering only the Postgres SQL generator and not the provider.
- Full coverage of NSchema.Core 3.0.0's domain model.

### Changed

- **Breaking:** Updated to NSchema 3.0.0, which includes many breaking changes to the core NSchema API.

### Fixed

- Removed trailing whitespace from generated SQL statements.

## [2.0.0] - 2026-06-01

### Changed

- **Breaking:** Updated to NSchema 2.0.0, which includes some breaking changes to the core NSchema API.
- **Breaking:** The `UsePostgres` methods have been renamed to `UseCurrentSchemaPostgres` to be more explicit about what you're configuring.

## [1.0.0] - 2026-05-27

First stable release of the PostgreSQL provider for NSchema, tracking the 1.0 release of NSchema itself.

### Added

- `UsePostgres(...)` extensions on `NSchemaApplicationBuilder` for registering the provider — overloads for a connection string, an `NpgsqlDataSourceBuilder` configuration delegate, the same with `IServiceProvider` access, and a no-arg form for bring-your-own `NpgsqlDataSource`.
- `PostgresSchemaProvider` — `ISchemaProvider` implementation that reads the live database via `information_schema` and `pg_catalog`, with optional schema-name scoping. Reads schemas, tables, columns, primary keys, foreign keys, indexes, comments (on schemas, tables, columns, and indexes), and `GRANT`s (on schemas and tables).
- `PostgresSqlPlanner` — `ISqlPlanner` implementation that translates an NSchema `MigrationPlan` into PostgreSQL DDL.
- `SqlType.Citext` and `SqlType.Jsonb` Postgres-specific type helpers on `SqlType`.
- SourceLink and symbol packages (`.snupkg`) published alongside the main package for source-level debugging.

[3.0.0]: https://github.com/nschema-org/NSchema.Postgres/compare/v2.0.0...v3.0.0
[2.0.0]: https://github.com/nschema-org/NSchema.Postgres/compare/v1.0.0...v2.0.0
[1.0.0]: https://github.com/nschema-org/NSchema.Postgres/releases/tag/v1.0.0
