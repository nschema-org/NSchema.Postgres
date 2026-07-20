# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

NSchema.Postgres is the PostgreSQL provider for [NSchema](https://github.com/nschema-org/NSchema), a schema-migration framework. It plugs PostgreSQL-specific implementations of NSchema's `IDatabaseIntrospector` (introspection) and `SqlDialect` (DDL generation) into the host application via `NSchemaApplicationBuilder.UsePostgres(...)`.

Target framework: `net10.0`. C# `LangVersion=latest` with nullable reference types and `TreatWarningsAsErrors=true`.

## Commands

- Build: `dotnet build NSchema.Postgres.slnx`
- Test (all): `dotnet test NSchema.Postgres.slnx`
- Test (single): `dotnet test --filter "FullyQualifiedName~PostgresSqlPlannerTests.MethodName"`
- Pack (matches CI output): `dotnet pack src/NSchema.Postgres/NSchema.Postgres.csproj -c Release`

Integration tests use **Testcontainers** to spin up `postgres:17-alpine` — Docker must be running locally. The fixture also enables the `citext` extension on startup (see `tests/.../Fixtures/PostgresContainerFixture.cs`).

CI/CD runs through an external orchestrator at `nschema-org/NSchema` (`build/build/NSchema.Build`) rather than raw `dotnet` commands — see `.github/workflows/cicd.yml`. The build pipeline expects `Build__ProjectFile=src/NSchema.Postgres/NSchema.Postgres.csproj`.

## Architecture

Two service registrations make up the entire public surface; everything else is `internal`:

- **`PostgresDatabaseIntrospector`** (`Sql/PostgresDatabaseIntrospector.cs`) — reads the live database via `information_schema` / `pg_catalog` queries and assembles an NSchema `Database`. It runs a fixed sequence of independent queries (tables, columns, PKs, FKs, unique/check constraints, indexes, comments, grants, views, enums, standalone sequences, functions, and procedures) against a single `NpgsqlConnection` opened from the injected `NpgsqlDataSource`. Each query is parameterized by an optional schema-name filter derived from the core's `PlanningScope`; `null` means "all visible schemas". Row DTOs live in `Models/`. View bodies come from `pg_get_viewdef` (the DB's canonical form, with the trailing terminator stripped) so that `apply` → `plan` round-trips without phantom diffs; view dependencies are read authoritatively from `pg_rewrite` / `pg_depend` (not parsed from the body) and feed the linearizer's drop ordering. Sequences are introspected the same anti-phantom-drift way: column-owned sequences (identity, serial, `OWNED BY`) are excluded via `pg_depend` deptype `'i'`/`'a'`, and `NormalizeSequenceOptions` folds Postgres engine defaults to `null` so a bare `CREATE SEQUENCE` declaration compares clean (an *explicitly* declared engine default therefore shows drift — omit it instead). Functions and procedures come from `pg_get_functiondef` (canonical form again): the `CREATE OR REPLACE …` header is stripped by exact length — computed from `quote_ident` + `pg_get_function_arguments`, never by scanning for a parenthesis — leaving the model's verbatim `Arguments` / `Definition` parts; extension-owned routines (everything `citext` installs) are excluded via `pg_depend` deptype `'e'`, and `NormalizeProcedureArguments` folds the explicit default `IN` mode Postgres renders for procedure arguments (same omit-the-default trade-off).
- **`PostgresSqlDialect`** (`Sql/PostgresSqlDialect.cs`) — subclasses the core's `SqlDialect`, overriding one method per migration action to render PostgreSQL DDL. A view `CreateView` (used for both an add and a body modify) emits `CREATE OR REPLACE VIEW`; an incompatible output-column change is rejected loudly by Postgres rather than silently dropping dependents. Enums map to `CREATE TYPE … AS ENUM` / anchored `ALTER TYPE … ADD VALUE`; `ADD VALUE` is the only statement emitted with `RunOutsideTransaction: true` (a value added in an uncommitted transaction can't be used; the core executor carves the statement out in order). A delta-based `ALTER SEQUENCE` resets removed options to their engine defaults explicitly (`NO MINVALUE`, `CACHE 1`, …) so the next introspection normalizes back to null. Function/procedure adds and definition-only modifies both arrive as `Create*` and emit `CREATE OR REPLACE`; the model has no routine overloading, so drops/renames/comments omit the signature. `RecreateRoutine`/`RecreateDomain` (signature/base-type changed) expand to multiple statements — `DROP` + `CREATE` + re-issued `COMMENT ON` (the drop discards it). An action Postgres cannot perform in place (e.g. making an existing plain column generated) renders as an `Unsupported` error diagnostic, never an exception.

`NSchemaApplicationBuilderExtensions` uses C# 14 **extension blocks** (`extension(NSchemaApplicationBuilder builder) { ... }`) — not classic `this`-parameter extension methods. Editing this file requires `LangVersion=latest` / .NET 10 SDK.

Central package management is on (`Directory.Packages.props` with `ManagePackageVersionsCentrally=true` and `CentralPackageTransitivePinningEnabled=true`) — add versions there, not in csproj files.

`SqlTypePostgresExtensions.cs` maps NSchema's abstract `SqlType` to Postgres-specific type names; the test fixture's `CREATE EXTENSION citext` exists because the type map includes `citext`.
