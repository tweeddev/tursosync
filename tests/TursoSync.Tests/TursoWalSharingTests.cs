using System.Collections.Concurrent;
using Turso.Sync;

namespace TursoSync.Tests;

/// <summary>
/// How connections over ONE replica file must be arranged. <see cref="TursoConnectionPool"/> currently calls
/// <see cref="TursoSyncDatabase.Create"/> per physical connection, so a pool of up to <c>MaxIdlePerKey</c>
/// connections runs that many independent sync engines against a single file set, each with a private view of
/// the WAL.
///
/// <para><b>Established here.</b> Sharing one engine across N connections (via
/// <see cref="TursoSyncDatabase.Connect"/>, which <see cref="TursoRawConnection.Open"/> wraps) is sound: it
/// survives a checkpoint that zeroes a 23 MB WAL, offline and on the sync lane. Running an engine PER
/// connection is not: a second <see cref="TursoSyncDatabase.Create"/> over a replica another engine is
/// actively using fails with <c>database tape error: database is busy</c>. And under a concurrent
/// writer, sibling connections hit <c>database is locked</c> in BOTH arrangements despite the 5 s busy
/// timeout every connection is opened with — so a shared engine still needs access mediated.</para>
///
/// <para><b>Measured, on a 13.8 MB WAL against a live remote, 25 s of 4-way concurrent load.</b> One engine
/// per connection: 10,626 statements, 745,096 errors, 86 failed checkpoints. One shared engine, same load:
/// 10,850 statements, 263,272 errors, ZERO failed checkpoints and no <c>database is busy</c> at all. Equal
/// throughput, a third of the contention, and the checkpoint failures disappear — sharing is strictly better
/// on every axis measured. Both are still dominated by <c>prepare_single: database is locked</c>, returned
/// immediately rather than waiting out the 5 s busy timeout the connection was opened with, so mediating
/// access remains necessary either way.</para>
///
/// <para><b>Not established.</b> These probes were written to reproduce a field failure —
/// <c>statement_step: I/O error: short read on WAL frame at offset N: expected 4096 bytes, got 0</c>, seen
/// against a ~15 MB WAL with a remote bound. <b>They do not, and the cause remains unknown.</b> Ruled out by
/// direct experiment against a live remote: a sibling engine's checkpoint; Tweed's <c>journal_mode</c> /
/// <c>synchronous</c> tuning pragmas; disposal of a sibling engine; and — the last remaining shape — a
/// checkpoint landing mid-statement under sustained concurrency, in both the pooled and shared arrangements
/// (86 and 0 checkpoints respectively, 21k statements, not one short read). Do not treat any of these as the
/// explanation.</para>
/// </summary>
[TestClass]
public class TursoWalSharingTests
{
    /// <summary>Rows per fill. Enough to push the WAL well past a single frame so a checkpoint truncates a
    /// meaningful range — a one-frame WAL can leave a stale reader accidentally valid.</summary>
    private const int FillRows = 1024;

    /// <summary>Per-row payload, sized so <see cref="FillRows"/> rows put multiple megabytes through the WAL
    /// (the field report was a ~15 MB WAL). A WAL of a few KB can be checkpointed without ever invalidating a
    /// stale reader's cached frame offsets.</summary>
    private static readonly string Payload = new('p', 4096);

    /// <summary>Rows for the sync-lane probes. Smaller than <see cref="FillRows"/> because these push their
    /// WAL to a real remote, but still lands a multi-megabyte WAL — the field report was ~15 MB.</summary>
    private const int RemoteFillRows = 512;

    /// <summary>How long the churn race runs, and how many engine-churning workers it runs. Long enough for
    /// a checkpoint to land mid-statement many times over.</summary>
    private const int ChurnSeconds = 25;
    private const int ChurnWorkers = 3;

    /// <summary>Live engines for the pooled-race probe — MaxIdlePerKey, what the pool actually holds.</summary>
    private const int PooledEngines = 4;

    // ---- refuted candidates for the field failure (kept as documentation of what was ruled out) ----

    [TestMethod]
    public void SeparateDatabases_SamePath_Checkpoint_DoesNotPoisonSibling()
    {
        SkipUnlessNative();

        var dbPath = TempDb();
        try
        {
            using var first = TursoSyncDatabase.Create(LocalConfig(dbPath));
            using var firstConn = TursoRawConnection.Open(first);
            Fill(firstConn);

            // A second, independent engine over the same files — exactly what the pool hands out today.
            using var second = TursoSyncDatabase.Create(LocalConfig(dbPath));
            using var secondConn = TursoRawConnection.Open(second);
            // Full scan, so this connection actually indexes WAL frames rather than just page 1.
            secondConn.QueryScalar("SELECT count(*) FROM t WHERE payload LIKE 'p%'").Should().Be((long)FillRows);

            var walBefore = first.Stats().MainWalSize;
            Console.WriteLine($"[probe] WAL before checkpoint: {walBefore} bytes");
            walBefore.Should().BeGreaterThan(1_000_000, "the probe needs a WAL big enough to invalidate a stale reader");

            // First engine folds the WAL into the main file and truncates it. The second engine is not party
            // to that and still indexes the frames that were just dropped.
            first.Checkpoint();

            // Refuted: the sibling reads through cleanly. Kept so a future change that DOES break this is caught.
            var act = () => secondConn.QueryScalar("SELECT sum(x) FROM t WHERE payload LIKE 'p%'");
            act.Should().NotThrow("a sibling engine's checkpoint does not, on its own, invalidate this WAL view");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // ---- the same pair on the SYNC lane (remote bound), where the field failure was seen ------------

    [TestMethod]
    public void RemoteLane_SeparateDatabases_SamePath_Checkpoint_DoesNotPoisonSibling()
    {
        var (url, token) = RemoteOrSkip();

        // The offline lane does NOT reproduce the field failure even at a 23 MB WAL. The generation /
        // wal_fragment_no / revert machinery only engages once a remote is bound, so this runs the same
        // sequence Tweed hit in production: two engines over one replica (what the pool hands out), a push,
        // then a checkpoint on one of them. Result: also does not reproduce — refuted.
        var table = "wal_poison_" + Guid.NewGuid().ToString("n");
        var dbPath = TempDb();
        try
        {
            using var first = TursoSyncDatabase.Create(RemoteConfig(dbPath, url, token));
            using var firstConn = TursoRawConnection.Open(first);
            Fill(firstConn, table, RemoteFillRows);

            using var second = TursoSyncDatabase.Create(RemoteConfig(dbPath, url, token));
            using var secondConn = TursoRawConnection.Open(second);
            secondConn.QueryScalar($"SELECT count(*) FROM {table} WHERE payload LIKE 'p%'")
                .Should().Be((long)RemoteFillRows);

            Console.WriteLine($"[probe] WAL before push/checkpoint: {first.Stats().MainWalSize} bytes");

            first.Push();
            first.Checkpoint();

            var act = () => secondConn.QueryScalar($"SELECT sum(x) FROM {table} WHERE payload LIKE 'p%'");
            act.Should().NotThrow("a sibling engine's checkpoint does not, on its own, invalidate this WAL view");
        }
        finally
        {
            DropRemoteTable(url, token, table);
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void RemoteLane_SharedDatabase_TwoConnections_SurviveCheckpoint()
    {
        var (url, token) = RemoteOrSkip();

        // The fix shape, on the lane that actually fails: one engine, two connections, push + checkpoint.
        var table = "wal_shared_" + Guid.NewGuid().ToString("n");
        var dbPath = TempDb();
        try
        {
            using var db = TursoSyncDatabase.Create(RemoteConfig(dbPath, url, token));
            using var writer = TursoRawConnection.Open(db);
            using var reader = TursoRawConnection.Open(db);

            Fill(writer, table, RemoteFillRows);
            reader.QueryScalar($"SELECT count(*) FROM {table}").Should().Be((long)RemoteFillRows);

            db.Push();
            db.Checkpoint();
            db.Stats().MainWalSize.Should().Be(0);

            reader.QueryScalar($"SELECT count(*) FROM {table}").Should().Be((long)RemoteFillRows);
            reader.QueryScalar($"SELECT sum(x) FROM {table} WHERE payload LIKE 'p%'")
                .Should().Be((long)RemoteFillRows * (RemoteFillRows - 1) / 2);
        }
        finally
        {
            DropRemoteTable(url, token, table);
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void RemoteLane_JournalModePragma_DoesNotDisturbTheWal()
    {
        var (url, token) = RemoteOrSkip();

        // The one production ingredient the probes above omit. Tweed re-asserts `PRAGMA journal_mode=WAL` on
        // every connection open (idempotent on plain SQLite), and the field failure struck INSIDE that pragma
        // execution. If the pragma resets the sync engine's WAL, that — not a sibling's checkpoint — is what
        // pulled the floor out from under the other connections.
        var table = "wal_pragma_" + Guid.NewGuid().ToString("n");
        var dbPath = TempDb();
        try
        {
            using var db = TursoSyncDatabase.Create(RemoteConfig(dbPath, url, token));
            using var conn = TursoRawConnection.Open(db);
            Fill(conn, table, RemoteFillRows);

            var before = db.Stats().MainWalSize;
            before.Should().BeGreaterThan(1_000_000);

            var mode = conn.QueryScalar("PRAGMA journal_mode=WAL");
            var after = db.Stats().MainWalSize;
            Console.WriteLine($"[probe] journal_mode returned '{mode}'; WAL {before} -> {after} bytes");

            after.Should().Be(before, "re-asserting journal_mode must not reset the sync engine's WAL");
        }
        finally
        {
            DropRemoteTable(url, token, table);
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void RemoteLane_JournalModePragma_DoesNotPoisonSibling()
    {
        var (url, token) = RemoteOrSkip();

        // Same pragma, but with a second engine live over the same replica — the full production shape: a
        // pooled connection opens, runs the tuning pragmas, and a sibling that was mid-flight then reads.
        var table = "wal_pragma_sib_" + Guid.NewGuid().ToString("n");
        var dbPath = TempDb();
        try
        {
            using var first = TursoSyncDatabase.Create(RemoteConfig(dbPath, url, token));
            using var firstConn = TursoRawConnection.Open(first);
            Fill(firstConn, table, RemoteFillRows);

            using var second = TursoSyncDatabase.Create(RemoteConfig(dbPath, url, token));
            using var secondConn = TursoRawConnection.Open(second);
            secondConn.QueryScalar($"SELECT count(*) FROM {table}").Should().Be((long)RemoteFillRows);

            // The freshly-opened connection runs Tweed's tuning set.
            secondConn.QueryScalar("PRAGMA journal_mode=WAL");
            secondConn.QueryScalar("PRAGMA synchronous=NORMAL");
            Console.WriteLine($"[probe] after sibling pragmas: WAL {first.Stats().MainWalSize} bytes");

            var act = () => firstConn.QueryScalar($"SELECT sum(x) FROM {table} WHERE payload LIKE 'p%'");
            act.Should().NotThrow("a sibling's tuning pragmas must not invalidate this connection's WAL view");
        }
        finally
        {
            DropRemoteTable(url, token, table);
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void RemoteLane_DisposingOneEngine_DoesNotPoisonLiveSibling()
    {
        var (url, token) = RemoteOrSkip();

        // Closer to what the pool actually does. Explicit Checkpoint() is rare; DISPOSAL is constant — every
        // return past MaxIdlePerKey drops a physical connection, and TursoPhysicalConnection.Dispose tears
        // down its whole sync engine while up to four siblings stay live over the same files. If teardown
        // finalizes/truncates the WAL, that is the eviction-driven version of the same failure.
        var table = "wal_dispose_" + Guid.NewGuid().ToString("n");
        var dbPath = TempDb();
        try
        {
            var first = TursoSyncDatabase.Create(RemoteConfig(dbPath, url, token));
            var firstConn = TursoRawConnection.Open(first);
            Fill(firstConn, table, RemoteFillRows);

            using var second = TursoSyncDatabase.Create(RemoteConfig(dbPath, url, token));
            using var secondConn = TursoRawConnection.Open(second);
            secondConn.QueryScalar($"SELECT count(*) FROM {table} WHERE payload LIKE 'p%'")
                .Should().Be((long)RemoteFillRows);

            Console.WriteLine($"[probe] WAL before dispose: {second.Stats().MainWalSize} bytes");

            // Evict the first engine, exactly as the pool would.
            firstConn.Dispose();
            first.Dispose();

            Console.WriteLine($"[probe] WAL after dispose:  {second.Stats().MainWalSize} bytes");

            var act = () => secondConn.QueryScalar($"SELECT sum(x) FROM {table} WHERE payload LIKE 'p%'");
            act.Should().NotThrow("evicting a sibling engine must not invalidate this connection's WAL view");
        }
        finally
        {
            DropRemoteTable(url, token, table);
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void RemoteLane_ChurnRace_DoesNotShortReadTheWal()
    {
        var (url, token) = RemoteOrSkip();

        // The shape every earlier probe missed: everything at once, the way Tweed runs it. A long-lived
        // connection (the firehose's PersistentBatchWriter) reads throughout, while worker threads churn
        // engines the way the pool does — create, use, dispose — and a syncer thread pushes and checkpoints
        // on its own engine the way SyncCoordinator does every 20 s. Contention errors (`database is
        // locked` / `database is busy`) are EXPECTED here and are not what this is looking for; the verdict
        // is solely whether a `short read on WAL frame` ever appears.
        var table = "wal_churn_" + Guid.NewGuid().ToString("n");
        var dbPath = TempDb();
        try
        {
            using var victim = TursoSyncDatabase.Create(RemoteConfig(dbPath, url, token));
            using var victimConn = TursoRawConnection.Open(victim);
            Fill(victimConn, table, RemoteFillRows);
            Console.WriteLine($"[probe] seeded WAL: {victim.Stats().MainWalSize} bytes");

            var failures = new ConcurrentQueue<Exception>();
            using var done = new CancellationTokenSource(TimeSpan.FromSeconds(ChurnSeconds));
            var ct = done.Token;

            // The victim: a long-lived connection reading across the whole run.
            var victimTask = Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        victimConn.QueryScalar($"SELECT count(*) FROM {table} WHERE payload LIKE 'p%'");
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                }
            });

            // Pool churn: engines created and torn down over the same replica, as Rent/Return does.
            var churnTasks = Enumerable.Range(0, ChurnWorkers).Select(w => Task.Run(() =>
            {
                var n = 0;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        using var db = TursoSyncDatabase.Create(RemoteConfig(dbPath, url, token));
                        using var conn = TursoRawConnection.Open(db);
                        conn.QueryScalar($"SELECT count(*) FROM {table}");
                        conn.Execute($"INSERT INTO {table} VALUES ({100_000 + (w * 10_000) + n++}, '{Payload}')");
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                }
            })).ToArray();

            // The SyncCoordinator: its own engine, pushing and checkpointing underneath everyone.
            var syncTask = Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        using var db = TursoSyncDatabase.Create(RemoteConfig(dbPath, url, token));
                        db.Push();
                        db.Checkpoint();
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                }
            });

            Task.WaitAll([victimTask, syncTask, .. churnTasks]);

            var all = failures.ToArray();
            var shortReads = all.Where(e => e.Message.Contains("short read", StringComparison.OrdinalIgnoreCase)).ToArray();
            var contention = all.Length - shortReads.Length;
            Console.WriteLine($"[probe] {all.Length} errors: {shortReads.Length} short-read, {contention} contention");
            foreach (var group in all.GroupBy(e => Summarize(e.Message)).OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"[probe]   {group.Count(),5}x {group.Key}");
            }

            shortReads.Should().BeEmpty("a short read on a WAL frame is the field failure being hunted");
        }
        finally
        {
            DropRemoteTable(url, token, table);
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void RemoteLane_PooledEngines_CheckpointDuringStatements_DoesNotShortRead()
    {
        var (url, token) = RemoteOrSkip();

        // Correction to the churn probe above: creating an engine per iteration means nearly every worker
        // dies at `sync_database_create` and never reaches a statement, so the statement-vs-checkpoint race
        // is never actually exercised. The pool does NOT create per operation — it keeps up to MaxIdlePerKey
        // physicals alive and hands them out. So: stand up that many engines ONCE, keep them live, then run
        // statements on all of them concurrently while one checkpoints underneath. This is the arrangement
        // Tweed actually runs, and the narrowest remaining shape for the field failure.
        var table = "wal_pooled_" + Guid.NewGuid().ToString("n");
        var dbPath = TempDb();
        var engines = new List<TursoSyncDatabase>();
        var conns = new List<TursoRawConnection>();
        try
        {
            for (var i = 0; i < PooledEngines; i++)
            {
                var db = TursoSyncDatabase.Create(RemoteConfig(dbPath, url, token));
                engines.Add(db);
                conns.Add(TursoRawConnection.Open(db));
            }

            Fill(conns[0], table, RemoteFillRows);
            Console.WriteLine($"[probe] seeded WAL: {engines[0].Stats().MainWalSize} bytes across {PooledEngines} live engines");

            var failures = new ConcurrentQueue<Exception>();
            var statements = 0;
            using var done = new CancellationTokenSource(TimeSpan.FromSeconds(ChurnSeconds));
            var ct = done.Token;

            // Every pooled connection runs statements continuously...
            var workers = conns.Select((conn, i) => Task.Run(() =>
            {
                var n = 0;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        conn.QueryScalar($"SELECT count(*) FROM {table} WHERE payload LIKE 'p%'");
                        conn.Execute($"INSERT INTO {table} VALUES ({100_000 + (i * 10_000) + n++}, '{Payload}')");
                        Interlocked.Add(ref statements, 2);
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                }
            })).ToArray();

            // ...while one engine checkpoints and pushes underneath them, mid-statement, over and over.
            var syncTask = Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        engines[^1].Checkpoint();
                        engines[^1].Push();
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                }
            });

            Task.WaitAll([syncTask, .. workers]);

            var all = failures.ToArray();
            var shortReads = all.Where(e => e.Message.Contains("short read", StringComparison.OrdinalIgnoreCase)).ToArray();
            Console.WriteLine($"[probe] {statements} statements executed; {all.Length} errors, {shortReads.Length} short-read");
            foreach (var group in all.GroupBy(e => Summarize(e.Message)).OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"[probe]   {group.Count(),5}x {group.Key}");
            }

            statements.Should().BeGreaterThan(100, "the probe is meaningless unless statements actually ran");
            shortReads.Should().BeEmpty("a short read on a WAL frame is the field failure being hunted");
        }
        finally
        {
            foreach (var c in conns) { try { c.Dispose(); } catch { /* teardown */ } }
            foreach (var e in engines) { try { e.Dispose(); } catch { /* teardown */ } }
            DropRemoteTable(url, token, table);
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void RemoteLane_SharedEngine_CheckpointDuringStatements_DoesNotShortRead()
    {
        var (url, token) = RemoteOrSkip();

        // Identical load to the pooled probe, but ONE engine with N connections instead of N engines — the
        // proposed fix. Reports the same statement/error tally so the two can be compared directly: the
        // question is not only "no short read" but whether sharing improves or worsens the lock storm the
        // pooled arrangement produces.
        var table = "wal_shared_race_" + Guid.NewGuid().ToString("n");
        var dbPath = TempDb();
        var conns = new List<TursoRawConnection>();
        try
        {
            using var db = TursoSyncDatabase.Create(RemoteConfig(dbPath, url, token));
            for (var i = 0; i < PooledEngines; i++)
            {
                conns.Add(TursoRawConnection.Open(db));
            }

            Fill(conns[0], table, RemoteFillRows);
            Console.WriteLine($"[probe] seeded WAL: {db.Stats().MainWalSize} bytes across 1 engine / {PooledEngines} connections");

            var failures = new ConcurrentQueue<Exception>();
            var statements = 0;
            using var done = new CancellationTokenSource(TimeSpan.FromSeconds(ChurnSeconds));
            var ct = done.Token;

            var workers = conns.Select((conn, i) => Task.Run(() =>
            {
                var n = 0;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        conn.QueryScalar($"SELECT count(*) FROM {table} WHERE payload LIKE 'p%'");
                        conn.Execute($"INSERT INTO {table} VALUES ({100_000 + (i * 10_000) + n++}, '{Payload}')");
                        Interlocked.Add(ref statements, 2);
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                }
            })).ToArray();

            var syncTask = Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        db.Checkpoint();
                        db.Push();
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                }
            });

            Task.WaitAll([syncTask, .. workers]);

            var all = failures.ToArray();
            var shortReads = all.Where(e => e.Message.Contains("short read", StringComparison.OrdinalIgnoreCase)).ToArray();
            Console.WriteLine($"[probe] {statements} statements executed; {all.Length} errors, {shortReads.Length} short-read");
            foreach (var group in all.GroupBy(e => Summarize(e.Message)).OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"[probe]   {group.Count(),5}x {group.Key}");
            }

            statements.Should().BeGreaterThan(100, "the probe is meaningless unless statements actually ran");
            shortReads.Should().BeEmpty("a short read on a WAL frame is the field failure being hunted");
        }
        finally
        {
            foreach (var c in conns) { try { c.Dispose(); } catch { /* teardown */ } }
            DropRemoteTable(url, token, table);
            Cleanup(dbPath);
        }
    }

    /// <summary>
    /// Everything in <paramref name="failures"/> that is NOT ordinary lock/busy contention — i.e. short reads,
    /// torn reads, and blown invariants. Contention is a throughput property and is measured elsewhere; these
    /// are correctness failures and must be empty.
    /// </summary>
    private static IReadOnlyList<Exception> Corruption(IEnumerable<Exception> failures) => failures
        .Where(e => !e.Message.Contains("is locked", StringComparison.OrdinalIgnoreCase)
                 && !e.Message.Contains("is busy", StringComparison.OrdinalIgnoreCase))
        .ToArray();

    /// <summary>Collapse an error message to its distinguishing prefix so the probe can tally by kind.</summary>
    private static string Summarize(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length <= 110 ? line : line[..110];
    }

    // ---- the shared-engine cache, through the public ADO.NET surface -------------------------------

    [TestMethod]
    public void Pool_SharesOneEngineAcrossConnections_AndReleasesItOnClear()
    {
        var (url, token) = RemoteOrSkip();

        var dbPath = TempDb();
        try
        {
            TursoConnection.ClearPool();
            TursoSyncDatabaseCache.Clear();

            var cs = ConnectionString(dbPath, url, token);
            var conns = new List<TursoConnection>();
            try
            {
                for (var i = 0; i < PooledEngines; i++)
                {
                    var c = new TursoConnection(cs);
                    c.Open();
                    conns.Add(c);
                }

                // Scoped to THIS replica, so unrelated cache entries can't skew it.
                TursoSyncDatabaseCache.RefCountFor(dbPath).Should().Be(PooledEngines,
                    "each open connection takes one reference on the replica's engine");
                conns.Select(c => c.SyncDatabase).Distinct().Should().HaveCount(1,
                    "every connection over one replica must share a single sync engine");
            }
            finally
            {
                foreach (var c in conns)
                {
                    c.Dispose();
                }
            }

            // Closed connections are returned to the pool, which still holds the engine warm...
            TursoSyncDatabaseCache.RefCountFor(dbPath).Should().BeGreaterThan(0,
                "an idle pooled connection keeps the engine warm rather than re-paying the bootstrap");

            // ...until the pool is cleared, which drops the last reference and frees the replica's handles.
            TursoConnection.ClearPool();
            TursoSyncDatabaseCache.RefCountFor(dbPath).Should().Be(0,
                "the last reference going must dispose the engine");
        }
        finally
        {
            TursoConnection.ClearPool();
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void Pool_DiscardingAnUnusableConnection_ReleasesItsEngineReference()
    {
        var (url, token) = RemoteOrSkip();

        // Rent() dequeues a pooled connection and drops it if the health probe fails. Dropped is not the same
        // as disposed: an undisposed physical keeps its reference on the shared engine, so the refcount never
        // reaches zero, the replica's files stay open, and ClearPool() can no longer release them — which is
        // what RemoteAttach relies on before it moves those files.
        var dbPath = TempDb();
        var key = "pool-leak-probe:" + dbPath;
        try
        {
            TursoConnection.ClearPool();
            var config = RemoteConfig(dbPath, url, token);

            // Pool a HEALTHY connection — Return() probes too, so poisoning before it would just make Return
            // dispose it and the queue would be empty, never exercising Rent's discard path at all.
            var poisoned = TursoPhysicalConnection.Create(config, forceSync: true);
            TursoSyncDatabaseCache.RefCountFor(dbPath).Should().Be(1);
            TursoConnectionPool.Return(key, poisoned);

            // Now poison it while it sits in the queue, so the rejection happens inside Rent.
            poisoned.Raw.Dispose();                       // probe will now throw -> IsUsable() false

            // Rent must DISPOSE the rejected connection (releasing its engine reference) rather than drop it.
            var fresh = TursoConnectionPool.Rent(key, config, forceSync: true);
            try
            {
                TursoSyncDatabaseCache.RefCountFor(dbPath).Should().Be(1,
                    "the rejected connection's reference must be released, leaving only the fresh one");
            }
            finally
            {
                fresh.Dispose();
            }

            TursoSyncDatabaseCache.RefCountFor(dbPath).Should().Be(0,
                "with every connection gone the engine must be disposed and the replica's files released");
        }
        finally
        {
            TursoConnection.ClearPool();
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void Cache_SamePathDifferentRemote_IsRejected()
    {
        var (url, token) = RemoteOrSkip();

        var dbPath = TempDb();
        try
        {
            TursoSyncDatabaseCache.Clear();
            var (key, _) = TursoSyncDatabaseCache.Acquire(RemoteConfig(dbPath, url, token));
            try
            {
                // Sharing an engine bound elsewhere would silently point this caller at the wrong remote.
                var act = () => TursoSyncDatabaseCache.Acquire(
                    RemoteConfig(dbPath, "libsql://somewhere-else.turso.io", token));
                act.Should().Throw<InvalidOperationException>().WithMessage("*already open against remote*");
            }
            finally
            {
                TursoSyncDatabaseCache.Release(key);
            }

            TursoSyncDatabaseCache.RefCountFor(dbPath).Should().Be(0);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void PublicApi_ChurnRace_DoesNotShortReadOrFailToOpen()
    {
        var (url, token) = RemoteOrSkip();

        // End-to-end proof through the surface Tweed actually uses: ADO.NET connections opened per operation
        // against one replica, with a checkpoint/push loop underneath — the shape that produced the field
        // failure. Before the shared-engine cache this arrangement could not even open connections reliably
        // (`sync_database_create ... database is busy`); that class must now be absent entirely.
        var table = "wal_public_" + Guid.NewGuid().ToString("n");
        var dbPath = TempDb();
        var cs = ConnectionString(dbPath, url, token);
        try
        {
            TursoConnection.ClearPool();
            using (var seed = new TursoConnection(cs))
            {
                seed.Open();
                Fill(seed.Raw, table, RemoteFillRows);
            }

            var failures = new ConcurrentQueue<Exception>();
            var statements = 0;
            using var done = new CancellationTokenSource(TimeSpan.FromSeconds(ChurnSeconds));
            var ct = done.Token;

            var workers = Enumerable.Range(0, ChurnWorkers).Select(w => Task.Run(() =>
            {
                var n = 0;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        using var conn = new TursoConnection(cs);   // open per operation, as the stores do
                        conn.Open();
                        conn.Raw.QueryScalar($"SELECT count(*) FROM {table} WHERE payload LIKE 'p%'");
                        conn.Raw.Execute($"INSERT INTO {table} VALUES ({100_000 + (w * 10_000) + n++}, '{Payload}')");
                        Interlocked.Add(ref statements, 2);
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                }
            })).ToArray();

            var syncTask = Task.Run(() =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        using var conn = new TursoConnection(cs);   // as SyncCoordinator does
                        conn.Open();
                        conn.SyncDatabase!.Push();
                        conn.SyncDatabase!.Checkpoint();
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                }
            });

            Task.WaitAll([syncTask, .. workers]);

            var all = failures.ToArray();
            var shortReads = all.Where(e => e.Message.Contains("short read", StringComparison.OrdinalIgnoreCase)).ToArray();
            var cannotOpen = all.Where(e => e.Message.Contains("sync_database_create", StringComparison.OrdinalIgnoreCase)
                                         || e.Message.Contains("sync_database_connect", StringComparison.OrdinalIgnoreCase)).ToArray();
            Console.WriteLine($"[probe] {statements} statements; {all.Length} errors, {shortReads.Length} short-read, {cannotOpen.Length} could-not-open");
            foreach (var group in all.GroupBy(e => Summarize(e.Message)).OrderByDescending(g => g.Count()))
            {
                Console.WriteLine($"[probe]   {group.Count(),5}x {group.Key}");
            }

            statements.Should().BeGreaterThan(100, "the probe is meaningless unless statements actually ran");
            shortReads.Should().BeEmpty("a short read on a WAL frame is the field failure being hunted");
            cannotOpen.Should().BeEmpty("sharing the engine must eliminate open-time `database is busy` failures");
        }
        finally
        {
            TursoConnection.ClearPool();
            DropRemoteTable(url, token, table);
            Cleanup(dbPath);
        }
    }

    private static string ConnectionString(string dbPath, string url, string? token) =>
        $"Data Source={dbPath};Remote Url={url};Auth Token={token};Bootstrap=true";

    // ---- the arrangement the fix moves to ----------------------------------------------------------

    [TestMethod]
    public void SharedDatabase_TwoConnections_SurviveCheckpoint()
    {
        SkipUnlessNative();

        var dbPath = TempDb();
        try
        {
            using var db = TursoSyncDatabase.Create(LocalConfig(dbPath));
            using var writer = TursoRawConnection.Open(db);
            using var reader = TursoRawConnection.Open(db);

            Fill(writer);
            reader.QueryScalar("SELECT count(*) FROM t").Should().Be((long)FillRows);

            db.Checkpoint();
            db.Stats().MainWalSize.Should().Be(0, "checkpoint folds the WAL into the main file");

            // The sibling connection must be unaffected: same engine, so its WAL view moved with it.
            reader.QueryScalar("SELECT count(*) FROM t").Should().Be((long)FillRows);
            reader.QueryScalar("SELECT sum(x) FROM t").Should().Be((long)FillRows * (FillRows - 1) / 2);

            // And the pair must keep working after the checkpoint, not just survive it.
            writer.Execute($"INSERT INTO t VALUES ({FillRows}, '{Payload}')");
            reader.QueryScalar("SELECT count(*) FROM t").Should().Be((long)FillRows + 1);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void SharedDatabase_ConcurrentSiblings_StayConsistent()
    {
        SkipUnlessNative();

        // The managed layer serializes the database's own operations behind its gate, but the connection
        // handles it hands out are used concurrently with no such lock. This is the load the pool will put on
        // the shared engine: several connections in flight at once, one of them writing, plus a checkpoint
        // landing mid-flight. Failures surface as exceptions or as a torn read (a count outside the window
        // the reader could legitimately have observed).
        var dbPath = TempDb();
        try
        {
            using var db = TursoSyncDatabase.Create(LocalConfig(dbPath));
            Seed(db);

            var failures = RunConcurrentLoad(() => (null, TursoRawConnection.Open(db)), db.Checkpoint);

            // Lock/busy contention is expected and pervasive here (see the class doc's measurements) — it is
            // not what this asserts. What must never appear is a torn read or a short read on the WAL.
            Corruption(failures).Should().BeEmpty("sibling connections must never read torn or missing WAL data");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void SeparateDatabases_ConcurrentSiblings_StayConsistent()
    {
        SkipUnlessNative();

        // Control for SharedDatabase_ConcurrentSiblings_StayConsistent. Same load, but each connection gets
        // its own engine — today's pool arrangement. If this locks too, contention under concurrent
        // read/write is a pre-existing property of the engine and NOT something the shared-database fix
        // introduces; the two results have to be compared before reading anything into either.
        var dbPath = TempDb();
        try
        {
            using var seedDb = TursoSyncDatabase.Create(LocalConfig(dbPath));
            Seed(seedDb);

            // Every participant gets its OWN engine, as the pool hands out today. Each owns and disposes it.
            var failures = RunConcurrentLoad(
                () =>
                {
                    var own = TursoSyncDatabase.Create(LocalConfig(dbPath));
                    return (own, TursoRawConnection.Open(own));
                },
                seedDb.Checkpoint);

            // Same as the shared case: contention is expected. Note the DIFFERENCE this control pins — here
            // the failures include `sync_database_create ... database is busy`, i.e. engines that could not
            // even be opened, a class that disappears entirely once the engine is shared.
            Corruption(failures).Should().BeEmpty("separate engines must never read torn or missing WAL data");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // ---- harness -----------------------------------------------------------------------------------

    private const int ConcurrentReaders = 3;
    private const int ConcurrentWrites = 200;

    private static void Seed(TursoSyncDatabase db)
    {
        using var conn = TursoRawConnection.Open(db);
        conn.Execute("CREATE TABLE t (x INTEGER PRIMARY KEY)");
    }

    /// <summary>
    /// Drive one writer and <see cref="ConcurrentReaders"/> readers against the same replica, with a
    /// checkpoint landing mid-flight, and collect everything that went wrong. <paramref name="open"/> supplies
    /// each participant's connection plus the engine it owns (null when the engine is shared), so the same
    /// load can be run over one shared database or one per connection.
    /// </summary>
    private static IReadOnlyList<Exception> RunConcurrentLoad(
        Func<(TursoSyncDatabase? Owned, TursoRawConnection Conn)> open, Action checkpoint)
    {
        var failures = new ConcurrentQueue<Exception>();
        using var done = new CancellationTokenSource();

        var readerTasks = Enumerable.Range(0, ConcurrentReaders).Select(_ => Task.Run(() =>
        {
            var (owned, conn) = (default(TursoSyncDatabase), default(TursoRawConnection));
            try
            {
                (owned, conn) = open();
                var seen = 0L;
                while (!done.IsCancellationRequested)
                {
                    var count = (long)conn.QueryScalar("SELECT count(*) FROM t")!;
                    // Rows are only ever added, so a reader must never see the table shrink.
                    count.Should().BeGreaterThanOrEqualTo(seen).And.BeLessThanOrEqualTo(ConcurrentWrites);
                    seen = count;
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
            finally
            {
                conn?.Dispose();
                owned?.Dispose();
            }
        })).ToArray();

        var writerTask = Task.Run(() =>
        {
            var (owned, conn) = (default(TursoSyncDatabase), default(TursoRawConnection));
            try
            {
                (owned, conn) = open();
                for (var i = 0; i < ConcurrentWrites; i++)
                {
                    conn.Execute($"INSERT INTO t VALUES ({i})");
                    if (i == ConcurrentWrites / 2)
                    {
                        checkpoint();   // mid-flight, with readers live
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
            finally
            {
                conn?.Dispose();
                owned?.Dispose();
            }
        });

        writerTask.GetAwaiter().GetResult();
        done.Cancel();
        Task.WaitAll(readerTasks);
        return failures.ToArray();
    }

    private static void SkipUnlessNative()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("turso_sync_sdk_kit native library not found");
        }
    }

    /// <summary>A local-only sync-lane database. No remote is needed to exercise WAL sharing, and staying
    /// offline keeps these runnable without cloud credentials.</summary>
    private static TursoSyncConfig LocalConfig(string path) => new() { Path = path, BootstrapIfEmpty = false };

    /// <summary>Create the table and write enough rows to grow the WAL past a single frame.</summary>
    private static void Fill(TursoRawConnection conn, string table = "t", int rows = FillRows)
    {
        conn.Execute($"CREATE TABLE {table} (x INTEGER PRIMARY KEY, payload TEXT)");
        for (var i = 0; i < rows; i++)
        {
            conn.Execute($"INSERT INTO {table} VALUES ({i}, '{Payload}')");
        }
    }

    /// <summary>Skip (Inconclusive) unless the native and a sync remote are both available.</summary>
    private static (string Url, string? Token) RemoteOrSkip()
    {
        SkipUnlessNative();
        var url = Environment.GetEnvironmentVariable("TURSOSYNC_TEST_REMOTE_URL");
        var token = Environment.GetEnvironmentVariable("TURSOSYNC_TEST_REMOTE_TOKEN");
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Inconclusive("Set TURSOSYNC_TEST_REMOTE_URL (+ TURSOSYNC_TEST_REMOTE_TOKEN) to run the sync-lane probes.");
        }

        return (url!, token);
    }

    private static TursoSyncConfig RemoteConfig(string path, string url, string? token) =>
        new() { Path = path, RemoteUrl = url, AuthToken = token, BootstrapIfEmpty = true };

    /// <summary>Drop a probe table on the remote and push, so runs don't accumulate schema.</summary>
    private static void DropRemoteTable(string url, string? token, string table)
    {
        var path = TempDb();
        try
        {
            using var db = TursoSyncDatabase.Create(RemoteConfig(path, url, token));
            using (var conn = TursoRawConnection.Open(db))
            {
                conn.Execute($"DROP TABLE IF EXISTS {table}");
            }

            db.Push();
        }
        catch
        {
            // best-effort remote cleanup — never fail a probe on teardown
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static string TempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tursosync-wal-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "store.db");
    }

    private static void Cleanup(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* handles may linger; temp dir */ }
        }
    }
}
