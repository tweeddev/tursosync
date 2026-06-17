# Turso .NET bindings — parity map

Parity validation between the upstream reference bindings
(`reference/turso-main/bindings/dotnet/src`) and the Tweed implementation
(`csharp-agents/src/Tweed.Store/Turso`).

The **Sync** column marks what's tied to Turso's remote-sync/replication feature —
the area where Tweed and the reference diverge most (Tweed adds it; the reference has none).

_Snapshot: 2026-06-17 (feature-complete pass)._

> **Status: feature complete for a standalone NuGet provider.** Every public type/member the reference
> `Turso.Data` + `Turso.Raw` expose has a Tweed equivalent (factory, encryption, UDFs, aggregates,
> collations, load-extension), plus the sync layer the reference lacks. Remaining differences are
> intentional (see notes).

## 1. ADO.NET surface (Turso.Data ↔ Tweed) — strong parity

| Reference (Turso.Data) | Tweed equivalent | Parity | Sync |
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

## 2. Low-level / raw surface (Turso.Raw ↔ Tweed) — divergent by design

| Reference (Turso.Raw, public) | Tweed equivalent | Parity | Sync |
|-------------------------------|------------------|--------|------|
| `TursoBindings` (static P/Invoke API) | `TursoNative` — **`internal`**, not public | ⚠ intentionally hidden | — |
| `TursoDatabaseHandle` / `TursoStatementHandle` (`SafeHandle`) | wrapped inside `TursoRawConnection` / `TursoRawStatement` (higher-level `IDisposable`) | ⚠ different abstraction | — |
| `TursoValue` struct + `TursoValueType` enum | `TursoValueKind` (internal in `TursoNative`) | ⚠ internalized | — |
| `OpenDatabaseWithEncryption` + `TursoEncryptionCipher` enum | `TursoEncryptionCipher` enum + `Encryption Cipher`/`Encryption Key` connection-string keys + `SetEncryption(...)`; wired into the base lane | ✅ match (base lane) | sync-lane at-rest encryption is a known gap — use the base lane or remote encryption |
| `RegisterScalarFunction` / `RegisterAggregateFunction` / `UnregisterFunction` | `TursoConnection.CreateFunction` / `CreateAggregate` (+ remove via null) | ✅ match | — |
| `RegisterCollation` / `UnregisterCollation` | `TursoConnection.CreateCollation` (+ remove via null) | ✅ match | — |
| `EnableLoadExtension` / `LoadExtension` | `TursoConnection.EnableExtensions` / `LoadExtension` | ✅ match | — |
| 7 UDF callback delegates + `TursoExtensionValue*` | `TursoExtensions.cs` — same 7 delegates + `TursoExtensionValue`/`Union`/`Type` | ✅ match | — |

## 3. Tweed-only — the sync layer (no reference counterpart)

| Tweed type / member | Purpose | Sync |
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
sync/replication layer (§3) is Tweed's value-add with no reference equivalent.

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
