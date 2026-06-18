using Turso;

namespace TursoSync.Tests;

/// <summary>
/// Behaviors ported from the Go binding's <c>driver_sync_test.go</c>. The config-surface and local-engine
/// cases run offline; the remote round-trip cases (Push/Pull) mirror the Go tests but need a live Turso
/// sync server, so they are gated on <c>TWEED_TURSO_SYNC_URL</c>/<c>TWEED_TURSO_SYNC_TOKEN</c> (same pattern
/// as the Neon-gated Postgres tests) and skip — not silently pass — when that infra is absent.
/// </summary>
[TestClass]
public class TursoSyncBehaviorTests
{
    // ---- config surface (parity with TestSyncDSNParsing / TestSyncBusyTimeoutConfigPrecedence) ------

    [TestMethod]
    public void Config_DefaultsBusyTimeout()
    {
        var config = new TursoConnectionStringBuilder("Data Source=mydb.db").ToConfig();
        config.Path.Should().Be("mydb.db");
        config.BusyTimeoutMs.Should().Be(5000);
        config.BootstrapIfEmpty.Should().BeFalse();
        config.RemoteUrl.Should().BeNull();
    }

    [TestMethod]
    public void Config_ExplicitBusyTimeout_Wins()
    {
        new TursoConnectionStringBuilder("Data Source=mydb.db;Busy Timeout=10000").ToConfig()
            .BusyTimeoutMs.Should().Be(10000);
    }

    [TestMethod]
    public void Config_NegativeBusyTimeout_DisablesIt()
    {
        new TursoConnectionStringBuilder("Data Source=mydb.db;Busy Timeout=-1").ToConfig()
            .BusyTimeoutMs.Should().Be(-1);
    }

    [TestMethod]
    public void Config_SyncFields_RoundTrip()
    {
        var config = new TursoConnectionStringBuilder(
                "Data Source=x.db;Remote Url=libsql://host;Auth Token=tok;Namespace=ns;Bootstrap=true")
            .ToConfig();
        config.RemoteUrl.Should().Be("libsql://host");
        config.AuthToken.Should().Be("tok");
        config.Namespace.Should().Be("ns");
        config.BootstrapIfEmpty.Should().BeTrue();
    }

    [TestMethod]
    public void Config_KeywordAliases_AreAccepted()
    {
        var config = new TursoConnectionStringBuilder("DataSource=a.db;RemoteUrl=libsql://h;AuthToken=t;BusyTimeout=2000")
            .ToConfig();
        config.Path.Should().Be("a.db");
        config.RemoteUrl.Should().Be("libsql://h");
        config.AuthToken.Should().Be("t");
        config.BusyTimeoutMs.Should().Be(2000);
    }

    [TestMethod]
    public void Config_MissingDataSource_Throws()
    {
        var act = () => new TursoConnectionStringBuilder("Busy Timeout=1000").ToConfig();
        act.Should().Throw<ArgumentException>();
    }

    // ---- encryption is a base-engine feature; the sync engine must reject it -----------------------

    [TestMethod]
    public void Create_WithLocalEncryption_OnSyncEngine_Throws()
    {
        // Local at-rest encryption isn't supported on the sync lane (the Go binding never plumbed it, and the
        // engine can't reopen the encrypted local file). Create must reject it loudly — deterministically,
        // before any native call — rather than hand back a database that's lost on the next open.
        var config = new TursoSyncConfig
        {
            Path = "ignored.db",
            RemoteUrl = "libsql://example",
            EncryptionCipher = TursoEncryptionCipher.Aes256Gcm.ToName(),
            EncryptionKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        };

        var act = () => TursoSyncDatabase.Create(config);
        act.Should().Throw<NotSupportedException>().WithMessage("*base engine*");
    }

    // ---- local engine behaviors (Stats / Checkpoint work without a remote) -------------------------

    [TestMethod]
    public void Stats_OnLocalDb_Succeeds()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("turso_sync_sdk_kit native library not found");
            return;
        }

        var dbPath = TempDb();
        try
        {
            using var db = TursoSyncDatabase.Create(new TursoSyncConfig { Path = dbPath, BootstrapIfEmpty = false });
            using var conn = TursoRawConnection.Open(db);
            conn.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT)");
            conn.Execute("INSERT INTO t (id, v) VALUES (1, 'a')");

            var stats = db.Stats();
            stats.Should().NotBeNull();
            stats.MainWalSize.Should().BeGreaterThanOrEqualTo(0);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void Checkpoint_OnLocalDb_Succeeds()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("turso_sync_sdk_kit native library not found");
            return;
        }

        var dbPath = TempDb();
        try
        {
            using var db = TursoSyncDatabase.Create(new TursoSyncConfig { Path = dbPath, BootstrapIfEmpty = false });
            using (var conn = TursoRawConnection.Open(db))
            {
                conn.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY)");
            }

            var act = db.Checkpoint;
            act.Should().NotThrow();
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // ---- remote round-trip (parity with TestSyncPush / TestSyncPull), gated on a live sync server ---

    [TestMethod]
    public void PushPull_RoundTripsThroughRemote()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("turso_sync_sdk_kit native library not found");
            return;
        }

        var url = Environment.GetEnvironmentVariable("TWEED_TURSO_SYNC_URL");
        var token = Environment.GetEnvironmentVariable("TWEED_TURSO_SYNC_TOKEN");
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Inconclusive("Set TWEED_TURSO_SYNC_URL (+ TWEED_TURSO_SYNC_TOKEN) to run the remote sync round-trip.");
            return;
        }

        var writerPath = TempDb();
        var readerPath = TempDb();
        try
        {
            using (var writer = TursoSyncDatabase.Create(new TursoSyncConfig { Path = writerPath, RemoteUrl = url, AuthToken = token, BootstrapIfEmpty = true }))
            {
                using (var conn = TursoRawConnection.Open(writer))
                {
                    conn.Execute("CREATE TABLE IF NOT EXISTS kv (k TEXT PRIMARY KEY, v TEXT)");
                    conn.Execute("INSERT INTO kv (k, v) VALUES ('hello', 'world')");
                }

                writer.Push();
            }

            using var reader = TursoSyncDatabase.Create(new TursoSyncConfig { Path = readerPath, RemoteUrl = url, AuthToken = token, BootstrapIfEmpty = true });
            reader.Pull();
            using var readConn = TursoRawConnection.Open(reader);
            readConn.QueryScalar("SELECT v FROM kv WHERE k = 'hello'").Should().Be("world");
        }
        finally
        {
            Cleanup(writerPath);
            Cleanup(readerPath);
        }
    }

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), "tweed-turso-sync-" + Guid.NewGuid().ToString("n"), "store.db");

    private static void Cleanup(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
