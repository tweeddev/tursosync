---
"TursoSync": minor
---

Add TursoSyncConfig.PullBytesThreshold to chunk the initial bootstrap download into multiple /pull-updates requests (0 = single round-trip, the default). Useful for large remote databases.