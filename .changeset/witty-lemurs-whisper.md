---
type: fix
"TursoSync": minor
---

Reject local at-rest encryption on the sync engine: TursoSyncDatabase.Create now throws NotSupportedException when an EncryptionCipher is set, instead of silently producing a synced database that cannot be reopened ("Decryption failed for page=1"). Local at-rest encryption remains supported on the base (local-only) lane via OpenLocal. Turso Cloud server-side encryption (remote key) is unaffected.