using Turso.Sync;

namespace TursoSync.Tests;

/// <summary>
/// The schema guard's offline surface: the pure inventory/diff helpers, the config + connection-string
/// plumbing, and the local (no-server) engine paths. The guard firing against a real remote — and
/// <see cref="TursoSyncDatabase.ReconcileLocalTables"/> repairing a stranded table — live in
/// <see cref="LiveSyncIntegrationTests"/>, gated on the local sync server.
/// </summary>
[TestClass]
public class TursoSchemaGuardTests
{
    // ---- pure helpers ----------------------------------------------------------------------------

    [TestMethod]
    public void Dropped_Reports_Only_Missing_Tables()
    {
        TursoSchemaGuard.Dropped(["a", "b", "c"], ["a", "c", "d"]).Should().Equal("b");
        TursoSchemaGuard.Dropped(["a"], ["A"]).Should().BeEmpty("table names compare case-insensitively");
        TursoSchemaGuard.Dropped([], ["x"]).Should().BeEmpty();
        TursoSchemaGuard.Dropped(["x", "y"], []).Should().Equal("x", "y");
    }

    [TestMethod]
    public void Internal_Tables_Are_Filtered()
    {
        TursoSchemaGuard.IsInternal("sqlite_sequence").Should().BeTrue();
        TursoSchemaGuard.IsInternal("turso_cdc").Should().BeTrue();
        TursoSchemaGuard.IsInternal("turso_sync_last_change_id").Should().BeTrue();
        TursoSchemaGuard.IsInternal("__turso_internal_seq_x").Should().BeTrue();
        TursoSchemaGuard.IsInternal("thread_checkpoint").Should().BeFalse();
        TursoSchemaGuard.IsInternal("users").Should().BeFalse();
    }

    // ---- config + connection-string plumbing -----------------------------------------------------

    [TestMethod]
    public void Config_Defaults_To_Detect() =>
        new TursoSyncConfig { Path = "x.db" }.SchemaGuard.Should().Be(TursoSchemaGuardMode.Detect);

    [TestMethod]
    public void Builder_Defaults_To_Detect() =>
        new TursoConnectionStringBuilder("Data Source=x.db").SchemaGuard.Should().Be(TursoSchemaGuardMode.Detect);

    [TestMethod]
    public void Builder_Parses_All_Modes_CaseInsensitively()
    {
        new TursoConnectionStringBuilder("Data Source=x.db;Schema Guard=off").SchemaGuard
            .Should().Be(TursoSchemaGuardMode.Off);
        new TursoConnectionStringBuilder("Data Source=x.db;SchemaGuard=DETECT").SchemaGuard
            .Should().Be(TursoSchemaGuardMode.Detect);
        new TursoConnectionStringBuilder("Data Source=x.db;Schema Guard=detectandbackup").SchemaGuard
            .Should().Be(TursoSchemaGuardMode.DetectAndBackup);
    }

    [TestMethod]
    public void Builder_Rejects_An_Invalid_Mode()
    {
        var builder = new TursoConnectionStringBuilder("Data Source=x.db;Schema Guard=paranoid");
        var act = () => builder.SchemaGuard;
        act.Should().Throw<ArgumentException>().WithMessage("*Schema Guard*paranoid*");
    }

    [TestMethod]
    public void ToConfig_Carries_SchemaGuard()
    {
        new TursoConnectionStringBuilder("Data Source=x.db;Schema Guard=DetectAndBackup").ToConfig()
            .SchemaGuard.Should().Be(TursoSchemaGuardMode.DetectAndBackup);
        new TursoConnectionStringBuilder("Data Source=x.db").ToConfig()
            .SchemaGuard.Should().Be(TursoSchemaGuardMode.Detect);
    }

    // ---- local engine paths (native-gated, no server) --------------------------------------------

    [TestMethod]
    public void CopyInventory_Reads_Tables_From_A_File_Copy()
    {
        RequireNative();
        var dbPath = TempDb();
        try
        {
            using (var conn = TursoRawConnection.OpenLocal(new TursoSyncConfig { Path = dbPath }))
            {
                conn.Execute("CREATE TABLE alpha (x TEXT)");
                conn.Execute("CREATE TABLE beta (y INTEGER)");
                conn.Execute("INSERT INTO alpha VALUES ('keep')");
            }

            var copyDir = dbPath + ".copy";
            TursoSchemaGuard.CopyReplicaFiles(dbPath, copyDir);
            var copied = Path.Combine(copyDir, Path.GetFileName(dbPath));
            File.Exists(copied).Should().BeTrue();

            // The copy inventories independently of the original (which could be live elsewhere).
            TursoSchemaGuard.UserTablesOfCopy(copied).Should().BeEquivalentTo(["alpha", "beta"]);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void LocalOnly_Reopen_Never_Engages_The_Guard()
    {
        RequireNative();
        var dbPath = TempDb();
        try
        {
            using (var db = TursoSyncDatabase.Create(new TursoSyncConfig { Path = dbPath }))
            using (var conn = TursoRawConnection.Open(db))
            {
                conn.Execute("CREATE TABLE keepme (x TEXT)");
            }

            // No remote → the guard stays inactive (nothing reconciles against a server), and the reopen
            // must neither throw nor leave guard backup dirs behind.
            using (var db = TursoSyncDatabase.Create(new TursoSyncConfig { Path = dbPath }))
            using (var conn = TursoRawConnection.Open(db))
            {
                TursoSchemaGuard.UserTables(conn).Should().Contain("keepme");
            }

            Directory.GetDirectories(Path.GetDirectoryName(dbPath)!, "*.guard-*").Should().BeEmpty();
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // ---- harness ---------------------------------------------------------------------------------

    private static void RequireNative()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("turso_sync_sdk_kit native library not found");
        }
    }

    private static string TempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tursosync-guard-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "store.db");
    }

    private static void Cleanup(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
