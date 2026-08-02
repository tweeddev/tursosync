---
"TursoSync": minor
---

Surface lock contention as `TursoException.IsBusy` so callers can tell a retryable busy from a real error.

`Check` threw on the engine's error pointer *before* looking at the status code, so a `Busy` result was
reported as a plain message with the status discarded — leaving string-matching on "database is locked" as
the only way to classify it. The status is now carried on the exception in both branches.

`IsBusy` and `IsBusySnapshot` are deliberately mutually exclusive. The engine treats them as different
failures: plain `Busy` is lock contention worth retrying, while `BusySnapshot` means a checkpoint advanced the
WAL and the read snapshot is permanently stale — "Retrying with busy_timeout will NEVER HELP", the transaction
has to be rolled back and restarted. Both are also matched on message text, because contention sometimes
arrives with a generic status and an explanatory string rather than the dedicated code.

This does not retry anything. A concurrent checkpoint currently fails statements outright rather than making
them wait: measured with one writer and a checkpoint every 100 ms, a contended `prepare_single` fails in ~3
microseconds (median) whether the connection's busy timeout is 200 ms or 5000 ms — 25x the budget, identical
time-to-failure — so the engine's busy handler is not consulted on that path. Without a checkpoint running,
a concurrent writer produces no contention failures at all. `TursoBusyTimeoutTests` pins this so that a fix
in the engine flips the assertion.

A managed retry loop was tried and reverted: waiting out the busy means holding native statement state across
a live checkpoint, and the workload stops draining entirely (a 3-second probe arm ran indefinitely at 165%
CPU, versus 3.0 s with retry off). The rationale is recorded in `TursoRawConnection` so it is not
reintroduced. The real fix belongs in the engine, next to the lock it needs to wait on.
