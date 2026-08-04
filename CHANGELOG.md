# Changelog

All notable changes to NSchema will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project (mostly) adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Versioning policy

This package uses **lockstep major versioning** with the core NSchema package: `NSchema.Postgres X.*.*` requires `NSchema.Core X.*.*`, so version compatibility is always clear.

As a consequence, breaking changes that are specific to this provider (rather than the core API) are signalled by a **minor version bump** rather than a major one, and called out explicitly in this changelog.

## [5.4.0] - 2026-08-04

### Added

- **Aggregates.** `CREATE AGGREGATE` renders and introspects: the definition is reconstructed from `pg_aggregate` (canonical option tuple, non-default options only), a replacement decomposes to drop + create (Postgres has no `CREATE OR REPLACE AGGREGATE`), and every addressing statement carries the signature Postgres requires.

### Fixed

- **A created materialized view brings its indexes.** Creating a materialized view now renders its index definitions alongside it; previously they were left out of the plan.

## [5.3.0] - 2026-08-03

### Changed

- **A create is a create.** `CREATE FUNCTION`, `CREATE PROCEDURE`, and `CREATE VIEW` render without `OR REPLACE` when the plan is creating; the in-place forms render only for the new replace actions, where the plan knows the object exists.
- **Trigger changes replace in place.** A changed trigger renders as `CREATE OR REPLACE TRIGGER` instead of a drop and a create.

### Fixed

- **Functions no longer fail to create when they reference tables from the same plan.** NSchema.Core 5.3 orders routines after the tables they may reference, so a `LANGUAGE sql` body or a rowtype signature resolves during a rebuild.

## [5.2.0] - 2026-08-03

### Added

- **The engine's type vocabulary is captured.** Introspection now records the types Postgres and its installed extensions provide (`pg_catalog` base, range, and multirange types, arrays included) as `NativeType`s in the snapshot, spelled in the model's canonical names. With a captured vocabulary, a plan can verify every type the project references — `pg_catalog.tsvector` and `text[]` columns on imported schemas now resolve instead of blocking, and a reference to a type nothing provides is reported at plan time.
- **Extension-provided types record their provenance.** A type an extension installs (e.g. `citext`, arrays included) carries `ProvidedBy`, so a plan that drops the extension accounts for everything still using its types.
- **`pg_catalog` is reported as a schema Postgres provides**, alongside `public`.

## [5.1.0] - 2026-08-02

### Changed

- **`CREATE SCHEMA` no longer hedges with `IF NOT EXISTS`.** This was a hack from a much earlier version that should have been removed long ago, but I forgot about it.
- **`public` is reported as a schema Postgres provides.** It is a container rather than something a migration creates, and declaring it is an error.

## [5.0.0] - 2026-08-01

v5.0 tracks the NSchema.Core 5.0 rearchitecture. The provider's behaviour is unchanged; its seams follow the new core contracts.

### Added

- **`new` asks for the connection details.** The plugin declares host, port, database and username as scaffolding questions and composes the answers into the `connection_string` it writes. The password is deliberately not asked for — it belongs in `NSCHEMA_DATABASE_PASSWORD`.

### Changed

- **Updated to NSchema.Core 5.0.** The provider now plugs in the core's `SqlDialect` (SQL rendering) and `IDatabaseIntrospector` (live-schema reading) seams.
- **`UsePostgres` replaces `UseCurrentSchemaPostgres`.** The old name referenced a core concept that no longer exists; `UsePostgresDialect` replaces `UsePostgresGenerator` the same way.
- **The plugin is configured by a `DATABASE` statement.** `Configure` takes the core's typed `PluginSettings` and returns a `Result`; configuration problems are diagnostics.
- An action this dialect cannot execute (e.g. making an existing plain column generated in place) now reports an error diagnostic on the plan instead of throwing.
- Identifiers everywhere in generated SQL are quoted per the core's case-sensitive identifier model; this now includes role names and trigger function references.
- Revoking table privileges now revokes the specific privileges the plan names rather than `ALL PRIVILEGES`.
- `ON DELETE NO ACTION` / `ON UPDATE NO ACTION` are no longer spelled out on added foreign keys (they are the engine default).
- Schema-qualified user-defined type names are now quoted in generated SQL: a column typed as, say, `app.order_status` renders as `"app"."order_status"` rather than bare (an unqualified type name is still emitted as-is).
- A user-defined type (enum, composite, …) is now read back schema-qualified during introspection instead of losing its schema, so it no longer produces a spurious diff against a schema-qualified declaration.
- **Postgres equivalence rules.** `UsePostgres` now also registers `PostgresSqlEquivalence` (standalone: `UsePostgresEquivalence`) into the core's comparison seam, so spellings the catalog and a project may legitimately disagree on compare equal in either direction. Introspection still captures exactly what the catalog reports.

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
