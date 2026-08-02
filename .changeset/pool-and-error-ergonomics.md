---
"TursoSync": minor
---

Typed error kinds, a normalized pool key, a configurable idle cap, and leak visibility.

`TursoException.Kind` (`TursoErrorKind`) exposes the engine's own classification — `Constraint`, `ReadOnly`,
`DatabaseFull`, `Corrupt`, `NotADatabase`, `Io`, `Interrupted`, `Busy`, `BusySnapshot` — so a caller can tell a
unique-key violation from a real fault without matching on message text. `IsBusy`/`IsBusySnapshot` are now
derived from it.

Physical connections are pooled under a key built from the PARSED connection string rather than its raw text.
The base builder normalizes keywords but not values, so `Sync=true` and `SYNC=True` previously keyed two
separate pools for one connection, each keeping its own idle set; paths are resolved to full paths for the
same reason. Only settings that make two connections non-interchangeable are part of the key.

`Max Idle Connections` (aliases: `MaxIdleConnections`, `Max Pool Size`, `MaxPoolSize`) makes the per-key idle
cap configurable, defaulting to the previous hard-coded 4.

Renting no longer re-runs the health probe on a connection returned within the last second — it was proven
healthy on the way in, and the probe is a full round trip on every checkout for consumers that open a
connection per operation.

`TursoConnection.OpenReplicas` lists the replicas currently held open and how many connections hold each. One
sync engine is shared per replica and lives until its last connection is disposed, so a connection that is
never closed pins its replica for the life of the process and no amount of `ClearPool()` will release its
files; asserting this list is empty at shutdown catches that.
