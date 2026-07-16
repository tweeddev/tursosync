# Turso .NET — surface map vs. the official binding

How TursoSync's ADO.NET surface (namespace `Turso.Sync`, in `src/TursoSync`) maps to the **official**
[`Turso.Data.Sqlite`](https://www.nuget.org/packages/Turso.Data.Sqlite) binding (`bindings/dotnet` in
[tursodatabase/turso](https://github.com/tursodatabase/turso)).

TursoSync is the **sync + FTS complement** to the official package, not a replacement. It deliberately models
its connection/command/reader surface on `Turso.Data` for familiarity — but bound to the sync-capable
`turso_sync_sdk_kit` native, so it can drive a *synced* database (which the official base native cannot). This
map exists to keep that surface recognizable and to mark where the two diverge.

The **Sync** column marks what's tied to Turso's offline replication — the area unique to TursoSync (the
official binding ships base + a per-statement remote HTTP client, but **no offline sync**).

> **Scope.** TursoSync intentionally does **not** reproduce official's SQLite-compat facade, EF Core provider,
> NativeAOT static linking, or broad mobile/RID packaging — use `Turso.Data.Sqlite` directly for those. What's
> mapped below is only the query surface needed to use a synced database, plus the sync engine official lacks.

## 1. ADO.NET surface (Turso.Data ↔ TursoSync) — strong parity

| Reference (Turso.Data) | TursoSync equivalent | Parity | Sync |
|------------------------|------------------|--------|------|
| `TursoConnection : DbConnection` + `.ExecuteNonQuery(sql)` | `TursoConnection` — has `ExecuteNonQuery`, `OpenAsync` | ✅ match | — |
| `TursoCommand : DbCommand` | `TursoCommand` | ✅ match | — |
| `TursoDataReader : DbDataReader` | `TursoDataReader` | ✅ match | — |
| `TursoParameter : DbParameter` | `TursoParameter` — adds `ToBindValue()` | ✅ match (+extra) | — |
| `TursoParameterCollection` | `TursoParameterCollection` — has `AddWithValue` | ✅ match | — |
| `TursoTransaction : DbTransaction` | `TursoTransaction` | ✅ match | — |
| `TursoConnectionStringBuilder` (`DefaultTimeout`, `GetEncryptionCipher()`) | `TursoConnectionStringBuilder` — adds `ToConfig()`, sync keys | ✅ match (+sync keys) | ⬅ adds `Remote Url`, `Auth Token`, `Namespace`, `Bootstrap`, `Long Poll Timeout` |
| `TursoConnectionOptions` (`Parse`) | folded into `TursoConnectionStringBuilder.ToConfig()` → `TursoSyncConfig` | ⚠ divergent shape | ⬅ |
| `TursoFactory : DbProviderFactory` (`.Instance`) | `TursoFactory` (`.Instance`) + `TursoConnection.DbProviderFactory` override | ✅ match | — |
| `TursoException` (in Turso.Raw) | `TursoException` + `TursoStats` record | ✅ match | — |

## 2. Low-level / raw surface (Turso.Raw ↔ TursoSync) — divergent by design

| Reference (Turso.Raw, public) | TursoSync equivalent | Parity | Sync |
|-------------------------------|------------------|--------|------|
| `TursoBindings` (static P/Invoke API) | `TursoNative` — **`internal`**, not public | ⚠ intentionally hidden | — |
| `TursoDatabaseHandle` / `TursoStatementHandle` (`SafeHandle`) | wrapped inside `TursoRawConnection` / `TursoRawStatement` (higher-level `IDisposable`) | ⚠ different abstraction | — |
| `TursoValue` struct + `TursoValueType` enum | `TursoValueKind` (internal in `TursoNative`) | ⚠ internalized | — |
| `OpenDatabaseWithEncryption` + `TursoEncryptionCipher` enum | `TursoEncryptionCipher` enum + `Encryption Cipher`/`Encryption Key` connection-string keys + `SetEncryption(...)`; wired into the base lane | ✅ match (base lane) | sync-lane at-rest encryption is a known gap — use the base lane or remote encryption |
| `RegisterScalarFunction` / `RegisterAggregateFunction` / `UnregisterFunction` | `TursoConnection.CreateFunction` / `CreateAggregate` (+ remove via null) | ✅ match | — |
| `RegisterCollation` / `UnregisterCollation` | `TursoConnection.CreateCollation` (+ remove via null) | ✅ match | — |
| `EnableLoadExtension` / `LoadExtension` | `TursoConnection.EnableExtensions` / `LoadExtension` | ✅ match | — |
| 7 UDF callback delegates + `TursoExtensionValue*` | `TursoExtensions.cs` — same 7 delegates + `TursoExtensionValue`/`Union`/`Type` | ✅ match | — |

## 3. TursoSync-only — the sync layer (no reference counterpart)

| TursoSync type / member | Purpose | Sync |
|---------------------|---------|------|
| `TursoSyncDatabase` — `Create`, `Connect`, `Push`, `Pull`, `Checkpoint`, `Stats`, `ProcessOneIo` | remote replication driver | ✅ **sync core** |
| `TursoSyncConfig` — `RemoteUrl`, `AuthToken`, `Namespace`, `BootstrapIfEmpty`, `LongPollTimeoutMs`, `ClientName` | sync configuration | ✅ **sync** |
| `TursoStats` record | push/pull stats | ✅ **sync** |
| `TursoRawConnection.Open(database, busyTimeout)` vs `.OpenLocal(config)` | sync-backed vs local-only raw conn | ✅ **sync** |
| `TursoNativeLibrary` — `IsAvailable()`, `EnsureResolver()` | native lib resolution | — |

## Parity verdict

**Feature complete.** The ADO.NET contract, the provider factory, encryption, and the full
extensibility surface (UDFs, aggregates, collations, load-extension) all match the reference and
are validated against the real engine (`TursoExtensionsTests`, `TursoNativeSmokeTests`). The
sync/replication layer (§3) is TursoSync's value-add with no reference equivalent.

### Closed since the first snapshot

1. ✅ **`TursoFactory : DbProviderFactory`** — `.Instance` + `CreateConnection/Command/Parameter/
   ConnectionStringBuilder`; `TursoConnection.DbProviderFactory` returns it.
2. ✅ **Encryption** — `TursoEncryptionCipher` enum + `Encryption Cipher`/`Encryption Key` keys +
   `SetEncryption(...)`, wired into the base lane (round-trip + wrong-key rejection tested).
3. ✅ **UDF / aggregate / collation / load-extension** — `CreateFunction`, `CreateAggregate`,
   `CreateCollation`, `EnableExtensions`, `LoadExtension`, with full native-callback value marshaling
   (`TursoExtensions.cs`). Connections with registered extensions are not returned to the pool.

### Intentional differences / known gaps

- **`TursoNative` / handles are `internal`** — we expose ergonomic `TursoConnection` methods instead of
  a public raw P/Invoke surface. Deliberate (don't leak FFI into the package's public API).
- **`TursoConnectionOptions`** folded into `TursoConnectionStringBuilder.ToConfig()` — same capability,
  cleaner shape.
- **Sync-lane at-rest encryption** — encryption is wired/validated on the base lane; the sync engine wraps
  storage and its at-rest encryption path isn't validated yet (use the base lane, or remote encryption).
