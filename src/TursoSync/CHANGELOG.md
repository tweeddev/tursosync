# TursoSync

## 0.1.0
### 🩹 Fixes

- Fix enumerating a TursoDataReader (foreach / DbEnumerator): GetDataTypeName no longer throws InvalidEnumArgumentException on the Unknown value kind (which occurs while building schema before the first row); it now falls back to the declared column type.
- Reject local at-rest encryption on the sync engine: TursoSyncDatabase.Create now throws NotSupportedException when an EncryptionCipher is set, instead of silently producing a synced database that cannot be reopened ("Decryption failed for page=1"). Local at-rest encryption remains supported on the base (local-only) lane via OpenLocal. Turso Cloud server-side encryption (remote key) is unaffected.
