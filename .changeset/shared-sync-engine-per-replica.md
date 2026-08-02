---
"TursoSync": patch
---

Share one sync engine per replica file set instead of creating one per connection.

`TursoPhysicalConnection.Create` called `TursoSyncDatabase.Create` for every physical connection, so a pool
holding up to `MaxIdlePerKey` connections ran that many independent sync engines over a single replica — each
owning and fragmenting the same WAL. Connections now acquire a reference-counted, shared engine
(`TursoSyncDatabaseCache`) and open over it via `TursoSyncDatabase.Connect`, which is what that API is for.
The engine is disposed when its last connection goes, so pooled connections keep it warm and `ClearPool()`
still releases the replica's file handles.

Measured against a live remote on a 13.8 MB WAL under 25 s of 4-way concurrent load with a checkpoint/push
loop underneath. Engine per connection: 10,626 statements, 745,096 errors, 86 failed checkpoints, and
`sync_database_create ... database tape error: database is busy` — connections that could not be opened at
all while another engine was active. Shared engine, same load through the ADO.NET surface: 10,260 statements,
125 errors, zero failed checkpoints, and no open-time failures whatsoever.

Opening a replica against a different remote than the one it is already open against is now rejected with a
clear error rather than silently handed an engine bound elsewhere.
