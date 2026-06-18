using System.Data;
using Turso;

namespace TursoSync.Tests;

/// <summary>
/// ADO.NET connection lifecycle, transactions, and physical-connection pooling against the real native
/// engine over a local (offline) database. Skipped (inconclusive) when the native library isn't present.
/// </summary>
[TestClass]
public class TursoConnectionTests
{
    private static bool NativeMissing()
    {
        if (TursoNativeLibrary.IsAvailable())
        {
            return false;
        }

        Assert.Inconclusive("turso_sync_sdk_kit native library not found");
        return true;
    }

    private string _dir = string.Empty;

    [TestInitialize]
    public void Setup() => _dir = Path.Combine(Path.GetTempPath(), "tweed-turso-conn-" + Guid.NewGuid().ToString("n"));

    [TestCleanup]
    public void Teardown()
    {
        TursoConnection.ClearPool();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string ConnString(bool pooling = true)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "store.db");
        return $"Data Source={path};Pooling={pooling}";
    }

    private TursoConnection Open(bool pooling = true)
    {
        var conn = new TursoConnection(ConnString(pooling));
        conn.Open();
        return conn;
    }

    // ---- lifecycle -------------------------------------------------------------------------------

    [TestMethod]
    public void Open_SetsStateAndMetadata()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.State.Should().Be(ConnectionState.Open);
        conn.Database.Should().Be("main");
        conn.DataSource.Should().NotBeNullOrEmpty();
        conn.ServerVersion.Should().NotBeNullOrEmpty();

        conn.Close();
        conn.State.Should().Be(ConnectionState.Closed);
    }

    [TestMethod]
    public void Open_Twice_Throws()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        var act = conn.Open;
        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public async Task OpenAsync_Opens_AndCancelsBeforeWork()
    {
        if (NativeMissing()) return;

        using var conn = new TursoConnection(ConnString());
        await conn.OpenAsync();
        conn.State.Should().Be(ConnectionState.Open);

        using var conn2 = new TursoConnection(ConnString());
        var act = async () => await conn2.OpenAsync(new CancellationToken(canceled: true));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public void SetConnectionString_WhileOpen_Throws()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        var act = () => conn.ConnectionString = "Data Source=other.db";
        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void ChangeDatabase_NotSupported()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        var act = () => conn.ChangeDatabase("other");
        act.Should().Throw<NotSupportedException>();
    }

    [TestMethod]
    public void Raw_WhenClosed_Throws()
    {
        if (NativeMissing()) return;

        using var conn = new TursoConnection(ConnString());
        var act = () => conn.ExecuteNonQuery("SELECT 1");  // ExecuteNonQuery → Raw, which is closed
        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void ExecuteNonQuery_RunsSql()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.ExecuteNonQuery("CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)");
        conn.ExecuteNonQuery("INSERT INTO t (id, v) VALUES (1, 'a')").Should().Be(1);
    }

    // ---- transactions ----------------------------------------------------------------------------

    [TestMethod]
    public void Transaction_Commit_Persists()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.ExecuteNonQuery("CREATE TABLE t (id INTEGER PRIMARY KEY)");

        using (var tx = conn.BeginTransaction())
        {
            conn.ExecuteNonQuery("INSERT INTO t (id) VALUES (1)");
            tx.Commit();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM t";
        cmd.ExecuteScalar().Should().Be(1L);
    }

    [TestMethod]
    public void Transaction_Rollback_Discards()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.ExecuteNonQuery("CREATE TABLE t (id INTEGER PRIMARY KEY)");

        using (var tx = conn.BeginTransaction())
        {
            conn.ExecuteNonQuery("INSERT INTO t (id) VALUES (1)");
            tx.Rollback();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM t";
        cmd.ExecuteScalar().Should().Be(0L);
    }

    [TestMethod]
    public void Transaction_Dispose_WithoutCommit_RollsBack()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.ExecuteNonQuery("CREATE TABLE t (id INTEGER PRIMARY KEY)");

        using (var tx = conn.BeginTransaction())
        {
            conn.ExecuteNonQuery("INSERT INTO t (id) VALUES (1)");
            // no commit → Dispose must roll back
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM t";
        cmd.ExecuteScalar().Should().Be(0L);
    }

    [TestMethod]
    public void Transaction_DoubleComplete_Throws()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        tx.Commit();

        var act = tx.Commit;
        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void Transaction_IsolationLevels_AreNormalized()
    {
        if (NativeMissing()) return;

        using var conn = Open();

        using (var unspecified = conn.BeginTransaction(IsolationLevel.Unspecified))
        {
            unspecified.IsolationLevel.Should().Be(IsolationLevel.Serializable);
        }

        using (var readCommitted = conn.BeginTransaction(IsolationLevel.ReadCommitted))
        {
            readCommitted.IsolationLevel.Should().Be(IsolationLevel.Serializable);
        }

        using (var readUncommitted = conn.BeginTransaction(IsolationLevel.ReadUncommitted))
        {
            // Exercises the ReadUncommitted set-on-begin / clear-on-complete branch.
            readUncommitted.IsolationLevel.Should().Be(IsolationLevel.ReadUncommitted);
            readUncommitted.Rollback();
        }
    }

    [TestMethod]
    public void Transaction_UnsupportedIsolationLevel_Throws()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        var act = () => conn.BeginTransaction(IsolationLevel.Snapshot);
        act.Should().Throw<NotSupportedException>();
    }

    [TestMethod]
    public void BeginTransaction_WhenClosed_Throws()
    {
        if (NativeMissing()) return;

        using var conn = new TursoConnection(ConnString());
        var act = () => conn.BeginTransaction();
        act.Should().Throw<InvalidOperationException>();
    }

    // ---- pooling ---------------------------------------------------------------------------------

    [TestMethod]
    public void Pooling_ReusesAcrossOpenClose()
    {
        if (NativeMissing()) return;

        var cs = ConnString(pooling: true);

        using (var seed = new TursoConnection(cs))
        {
            seed.Open();
            seed.ExecuteNonQuery("CREATE TABLE t (id INTEGER PRIMARY KEY)");
            seed.ExecuteNonQuery("INSERT INTO t (id) VALUES (1)");
        } // returned to pool

        // Reopen the same connection string: a pooled physical connection should serve it and see the data.
        using var conn = new TursoConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM t";
        cmd.ExecuteScalar().Should().Be(1L);
    }

    [TestMethod]
    public void Pooling_OverIdleCap_DisposesExtras()
    {
        if (NativeMissing()) return;

        var cs = ConnString(pooling: true);

        // Open more simultaneous connections than the idle cap (4), then return them all. The extras over
        // the cap are disposed on return rather than pooled — exercises both Return branches.
        var conns = new List<TursoConnection>();
        for (var i = 0; i < 6; i++)
        {
            var c = new TursoConnection(cs);
            c.Open();
            conns.Add(c);
        }

        foreach (var c in conns)
        {
            c.Close();
            c.Dispose();
        }

        // Pool is still usable afterward.
        using var conn = new TursoConnection(cs);
        conn.Open();
        conn.State.Should().Be(ConnectionState.Open);
    }

    [TestMethod]
    public void Pooling_Disabled_OpensFreshEachTime()
    {
        if (NativeMissing()) return;

        var cs = ConnString(pooling: false);

        using (var seed = new TursoConnection(cs))
        {
            seed.Open();
            seed.ExecuteNonQuery("CREATE TABLE t (id INTEGER PRIMARY KEY)");
        }

        using var conn = new TursoConnection(cs);
        conn.Open();
        conn.State.Should().Be(ConnectionState.Open);
    }

    [TestMethod]
    public void Pooling_ConnectionWithUdf_IsNotPooled()
    {
        if (NativeMissing()) return;

        var cs = ConnString(pooling: true);

        using (var conn = new TursoConnection(cs))
        {
            conn.Open();
            conn.CreateFunction("answer", 0, _ => 42L);   // marks the physical connection non-poolable
        } // dropped, not pooled

        // A fresh open must not see the per-connection function.
        using var fresh = new TursoConnection(cs);
        fresh.Open();
        using var cmd = fresh.CreateCommand();
        cmd.CommandText = "SELECT answer()";
        var act = () => cmd.ExecuteScalar();
        act.Should().Throw<TursoException>();
    }
}
