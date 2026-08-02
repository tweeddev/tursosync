using Turso.Sync;

namespace TursoSync.Tests;

/// <summary>
/// Pooling and error-classification behaviour. These use <c>Sync=true</c> to engage the sync lane against a
/// purely local replica, so they exercise the shared-engine path without needing a remote.
/// </summary>
[TestClass]
public class TursoPoolingBehaviorTests
{
    // ---- error classification ----------------------------------------------------------------------

    [TestMethod]
    public void ErrorKind_ClassifiesAConstraintViolation()
    {
        SkipUnlessNative();

        var dir = NewDir();
        try
        {
            using var conn = Open(Cs(dir));
            conn.Raw.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY)");
            conn.Raw.Execute("INSERT INTO t (id) VALUES (1)");

            var act = () => conn.Raw.Execute("INSERT INTO t (id) VALUES (1)");

            // A duplicate key is a data error the caller can act on — it must be distinguishable from a
            // fault without matching on message text.
            act.Should().Throw<TursoException>()
                .Which.Kind.Should().Be(TursoErrorKind.Constraint);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [TestMethod]
    public void ErrorKind_DoesNotMisreportOrdinaryErrorsAsContention()
    {
        SkipUnlessNative();

        var dir = NewDir();
        try
        {
            using var conn = Open(Cs(dir));

            var ex = ((Action)(() => conn.Raw.Execute("SELECT FROM nowhere")))
                .Should().Throw<TursoException>().Which;

            ex.IsBusy.Should().BeFalse();
            ex.IsBusySnapshot.Should().BeFalse();
            ex.Kind.Should().NotBe(TursoErrorKind.Busy).And.NotBe(TursoErrorKind.BusySnapshot);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ---- pool key normalization --------------------------------------------------------------------

    [TestMethod]
    public void PoolKey_DifferentSpellingsOfOneConnection_ShareThePool()
    {
        SkipUnlessNative();

        var dir = NewDir();
        try
        {
            TursoConnection.ClearPool();
            var dbPath = Path.Combine(dir, "store.db");

            // Spelling A: canonical.
            using (var a = Open($"Data Source={dbPath};Sync=true"))
            {
                a.Raw.Execute("CREATE TABLE t (x INTEGER)");
            }

            TursoSyncDatabaseCache.RefCountFor(dbPath).Should().Be(1, "the closed connection is pooled");

            // Spelling B: alias keyword, different case, and a redundant path segment — same connection.
            var awkward = Path.Combine(dir, ".", "store.db");
            using (var b = Open($"DataSource={awkward};SYNC=True"))
            {
                b.Raw.QueryScalar("SELECT count(*) FROM t").Should().Be(0L);
            }

            // If the two spellings keyed different pools, B would have opened a SECOND physical connection
            // and the shared engine's refcount would have gone to 2.
            TursoSyncDatabaseCache.RefCountFor(dbPath).Should().Be(1,
                "both spellings must rent from the same pool rather than each keeping their own connection");
        }
        finally
        {
            TursoConnection.ClearPool();
            Cleanup(dir);
        }
    }

    // ---- configurable idle cap ---------------------------------------------------------------------

    [TestMethod]
    public void MaxIdleConnections_CapsWhatThePoolKeeps()
    {
        SkipUnlessNative();

        var dir = NewDir();
        try
        {
            TursoConnection.ClearPool();
            var dbPath = Path.Combine(dir, "store.db");
            var cs = $"Data Source={dbPath};Sync=true;Max Idle Connections=1";

            var conns = new List<TursoConnection>();
            for (var i = 0; i < 3; i++)
            {
                var c = new TursoConnection(cs);
                c.Open();
                conns.Add(c);
            }

            TursoSyncDatabaseCache.RefCountFor(dbPath).Should().Be(3);
            foreach (var c in conns)
            {
                c.Dispose();
            }

            // Two of the three exceed the cap and are disposed on return, releasing their engine references.
            TursoSyncDatabaseCache.RefCountFor(dbPath).Should().Be(1,
                "only Max Idle Connections may stay pooled; the rest must be disposed, not retained");
        }
        finally
        {
            TursoConnection.ClearPool();
            Cleanup(dir);
        }
    }

    [TestMethod]
    public void MaxIdleConnections_DefaultsAndRoundTrips()
    {
        new TursoConnectionStringBuilder("Data Source=x.db").MaxIdleConnections.Should().Be(4);
        new TursoConnectionStringBuilder("Data Source=x.db;Max Idle Connections=9").MaxIdleConnections.Should().Be(9);
        new TursoConnectionStringBuilder("Data Source=x.db;MaxPoolSize=7").MaxIdleConnections.Should().Be(7);
    }

    // ---- leak visibility ---------------------------------------------------------------------------

    [TestMethod]
    public void OpenReplicas_ReportsHeldReplicas_AndEmptiesWhenAllAreClosed()
    {
        SkipUnlessNative();

        var dir = NewDir();
        try
        {
            TursoConnection.ClearPool();
            var dbPath = Path.Combine(dir, "store.db");

            var conn = new TursoConnection($"Data Source={dbPath};Sync=true");
            conn.Open();

            var held = TursoConnection.OpenReplicas.SingleOrDefault(r => r.Path == Path.GetFullPath(dbPath));
            held.Should().NotBeNull("an open connection pins its replica's engine");
            held!.Connections.Should().Be(1);

            conn.Dispose();
            TursoConnection.ClearPool();

            TursoConnection.OpenReplicas.Should().NotContain(r => r.Path == Path.GetFullPath(dbPath),
                "closing everything must release the replica so its files can be moved");
        }
        finally
        {
            TursoConnection.ClearPool();
            Cleanup(dir);
        }
    }

    // ---- harness -----------------------------------------------------------------------------------

    private static void SkipUnlessNative()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("turso_sync_sdk_kit native library not found");
        }
    }

    private static string Cs(string dir) => $"Data Source={Path.Combine(dir, "store.db")};Sync=true";

    private static TursoConnection Open(string connectionString)
    {
        var conn = new TursoConnection(connectionString);
        conn.Open();
        return conn;
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tursosync-pool-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }
}
