---
"TursoSync": minor
---

Add an `ExperimentalIndexMethod` option that enables the engine's `index_method` feature, unlocking tantivy full-text search (`CREATE INDEX … USING fts`, `fts_match`/`fts_score`/`fts_highlight`, `OPTIMIZE INDEX`). Set it via the `Experimental Index Method` connection-string key or `TursoSyncConfig.ExperimentalIndexMethod`. Experimental features are now sent to the native as a comma-separated list, so encryption and FTS compose — an FTS index inside an encrypted database is itself encrypted at rest.