---
"TursoSync": minor
---

Pre-open sync-state guard: foreign or unusable on-disk sync state (unparseable -info, unsupported metadata version, or state stamped by a newer engine) now throws a typed TursoSyncStateException before the native engine touches the files, instead of risking a native panic that aborts the host process. Successful opens stamp the engine version in a new -tursosync sidecar. TursoException is now unsealed so the new exception subclasses it. Escape hatch: TURSOSYNC_IGNORE_STATE_GUARD=1.