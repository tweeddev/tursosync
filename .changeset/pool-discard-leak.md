---
"TursoSync": patch
---

Fix a connection-pool leak, and stop treating lock contention as a dead connection.

`Rent` dequeued an idle connection and, if the health probe rejected it, returned it to nobody — the
connection was dropped without being disposed. That always leaked the native connection; with connections now
sharing a sync engine per replica it also leaked the engine reference, so the refcount never reached zero, the
replica's files stayed open for the life of the process, and `ClearPool()` could no longer release them for
`RemoteAttach` to move. `Rent` now disposes rejected connections and keeps draining the queue instead of
falling straight through to opening a new one.

`IsUsable` treated every exception as a dead connection, including `database is locked`. A checkpoint fails
concurrent statements — the probe included — so the whole pool was discarded and rebuilt at exactly the moment
it was most contended, against an engine that was still busy. Contention (`IsBusy`/`IsBusySnapshot`) now
counts as healthy; only real failures retire a connection.
