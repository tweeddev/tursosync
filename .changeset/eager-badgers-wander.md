---
"TursoSync": minor
---

Schema guard + ReconcileLocalTables: sync operations that drop local tables now throw TursoSchemaGuardException (with optional pre-operation backup) instead of losing them silently, and ReconcileLocalTables() teaches the server tables created before the remote was attached by rebuilding them through the synced connection