---
"TursoSync": major
---

BREAKING: move public types from namespace Turso to Turso.Sync so the package coexists with the official Turso.Data.Sqlite (which owns namespace Turso). Update 'using Turso;' to 'using Turso.Sync;'. Also rename the live-test env vars TWEED_TURSO_SYNC_URL/TOKEN to TURSOSYNC_SYNC_URL/TOKEN.