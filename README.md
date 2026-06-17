# TursoSync

[![CI](https://github.com/tweeddev/tursosync/actions/workflows/ci.yml/badge.svg)](https://github.com/tweeddev/tursosync/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/TursoSync.svg)](https://www.nuget.org/packages/TursoSync)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A **drop-in ADO.NET provider for [Turso](https://github.com/tursodatabase/turso)** (namespace `Turso`) with
**local↔cloud sync** and **tantivy full-text search** — the pieces the official `Turso.Data` binding doesn't
ship yet. Same API surface, so existing `Turso.Data` code compiles unchanged; the sync engine is the add-on.

```csharp
using Turso;

// Local-only (offline fast path — plain engine, no sync overhead)
await using var conn = new TursoConnection("Data Source=app.db");
await conn.OpenAsync();

// …or synced against Turso Cloud
await using var synced = new TursoConnection(
    "Data Source=app.db;Remote Url=libsql://my-db.turso.io;Auth Token=…");
await synced.OpenAsync();
synced.SyncDatabase!.Push();   // push local changes
synced.SyncDatabase!.Pull();   // pull + apply remote changes
```

## Packages

| Package | What |
|---------|------|
| [`TursoSync`](https://www.nuget.org/packages/TursoSync) | The ADO.NET provider (namespace `Turso`): connection/command/reader/parameter/transaction, `TursoFactory`, sync engine, UDFs, aggregates, collations, load-extension, local at-rest encryption, connection pooling. |
| [`TursoSync.DbUp`](https://www.nuget.org/packages/TursoSync.DbUp) | DbUp database provider — `DeployChanges.To.TursoDatabase(connectionString)`. |
| [`TursoSync.Dapper`](https://www.nuget.org/packages/TursoSync.Dapper) | Dapper type handlers that round-trip `Ulid`, `DateTimeOffset` and `Guid` as portable `TEXT`. |

```sh
dotnet add package TursoSync
dotnet add package TursoSync.DbUp     # optional: migrations
dotnet add package TursoSync.Dapper   # optional: Dapper type handlers
```

## Highlights

- **Two lanes, one API.** No `Remote Url` → the plain local engine (`AsyncIO=0`, no IO pump); a remote (or
  `Sync=true`) → the sync engine. In **release** builds, local performance is on par with SQLite.
- **Drop-in.** Public types live in namespace `Turso` (`TursoConnection`, `TursoCommand`, …) and work with
  Dapper, DbUp and the `DbProviderFactory` pattern.
- **Connection pooling** on by default (`Pooling=false` to disable) — opening Turso is expensive, the pool
  makes the open-per-op pattern ~50× cheaper.
- **Extensibility:** `CreateFunction` / `CreateAggregate` / `CreateCollation` / `EnableExtensions` /
  `LoadExtension`.
- **Local at-rest encryption:** `…;Encryption Cipher=aes256gcm;Encryption Key=<hex>`.

## Connection string keys

`Data Source` (required) · `Remote Url` · `Auth Token` · `Namespace` · `Bootstrap` · `Sync` · `Pooling`
· `Busy Timeout` · `Long Poll Timeout` · `Encryption Cipher` · `Encryption Key`.

## Native library

The provider P/Invokes the `turso_sync_sdk_kit` native (the Turso sync engine + tantivy FTS). Released
packages carry it under `runtimes/<rid>/native/`. For local development against a self-built engine, point
`TURSOSYNC_NATIVE_DIR` at the folder containing the built `turso_sync_sdk_kit` library. **Build the native in
release** — debug builds are ~25× slower.

## Engine version

The Turso engine (the `turso_sync_sdk_kit` native) is **not vendored** — CI builds it from
[tursodatabase/turso](https://github.com/tursodatabase/turso) at the commit pinned in
[`turso-engine.json`](turso-engine.json). The C ABI is beta, so the pin keeps builds reproducible.

- Bump it with `scripts/bump-turso.sh <tag|latest>` (resolves the tag → commit SHA).
- A weekly **Engine bump** workflow opens a PR when a newer release appears in the pinned series; CI builds
  + tests against it on the PR, so an ABI change is caught in review before merge.

## Examples

```csharp
// Migrations (TursoSync.DbUp)
using DbUp;
var result = DeployChanges.To.TursoDatabase("Data Source=app.db")
    .WithScriptsEmbeddedInAssembly(typeof(Program).Assembly)
    .Build()
    .PerformUpgrade();

// Dapper type handlers (TursoSync.Dapper)
Turso.TursoTypeHandlers.Register();

// A scalar UDF
conn.CreateFunction("times_two", 1, args => Convert.ToInt64(args[0]) * 2);
```

## Status

Feature-complete relative to the official `Turso.Data` + `Turso.Raw` surface (see
[TURSO-PARITY.md](TURSO-PARITY.md)), plus the sync layer. The underlying Turso engine is **beta**; one known
gap is sync-lane at-rest encryption (base-lane encryption is supported).

## Releasing

Releases are driven by [shipRig](https://rigsmith.dev) (changesets) — config in `.changeset/`.

1. Record intent: `shiprig add` (pick the bump + summary; the three packages are `fixed`, so they
   version together; `TursoSync.Tests` is ignored).
2. Ship: `shiprig release` → versions + changelog → commit → tags `TursoSync@x.y.z` → push.

The pushed tag triggers the **Release** workflow, which builds all six RID natives (release + FTS,
stripped), packs, and publishes to NuGet via **trusted publishing** (OIDC — no API key). shipRig only
versions/tags/pushes; the cross-arch native build + publish stay in CI (`.changeset/release.jsonc`).

Day-to-day dev uses [rig](https://rigsmith.dev) (`.rig.json`): `rig build` / `rig test` / `rig coverage`,
plus `rig engine <tag|latest>` (pin the engine) and `rig pack` (local Tier-0 pack + consume test).

## License

MIT.
