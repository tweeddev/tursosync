# TursoSync

## 1.0.1

## 1.0.0
### Minor Changes

- Add TursoSyncConfig.PullBytesThreshold to chunk the initial bootstrap download into multiple /pull-updates requests (0 = single round-trip, the default). Useful for large remote databases.

### Patch Changes

- Fix remote sync against Turso Cloud: zero-length request bodies (e.g. the initial /pull-updates protobuf, which encodes to no bytes) are now sent with their content-type and Content-Length: 0, so Cloud no longer rejects the bootstrap with HTTP 400. Bundled Turso engine bumped to v0.7.0.

### Breaking

- BREAKING: move public types from namespace Turso to Turso.Sync so the package coexists with the official Turso.Data.Sqlite (which owns namespace Turso). Update 'using Turso;' to 'using Turso.Sync;'. Also rename the live-test env vars TWEED_TURSO_SYNC_URL/TOKEN to TURSOSYNC_SYNC_URL/TOKEN.

## 0.1.0
### 🩹 Fixes

- Fix enumerating a TursoDataReader (foreach / DbEnumerator): GetDataTypeName no longer throws InvalidEnumArgumentException on the Unknown value kind (which occurs while building schema before the first row); it now falls back to the declared column type.
- Reject local at-rest encryption on the sync engine: TursoSyncDatabase.Create now throws NotSupportedException when an EncryptionCipher is set, instead of silently producing a synced database that cannot be reopened ("Decryption failed for page=1"). Local at-rest encryption remains supported on the base (local-only) lane via OpenLocal. Turso Cloud server-side encryption (remote key) is unaffected.
