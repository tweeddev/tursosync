---
"TursoSync": minor
---

Schema guard + ReconcileLocalTables: sync operations that drop local tables now throw TursoSchemaGuardException (with optional pre-operation backup) instead of losing them silently, and ReconcileLocalTables() repairs tables stranded by a pre-attach create — their schema is created on the server over Hrana `/v2/pipeline` (CDC never replicates DDL) and their rows re-recorded through the synced connection so the next push replays them
