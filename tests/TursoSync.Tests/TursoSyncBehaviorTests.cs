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
        // A unique key per run so the test is repeatable against a persistent remote (a real sync database
        // accumulates rows; it is not truncated between runs) rather than assuming a pristine remote.
        var key = "hello-" + Guid.NewGuid().ToString("n");
        try
        {
            using (var writer = TursoSyncDatabase.Create(new TursoSyncConfig { Path = writerPath, RemoteUrl = url, AuthToken = token, BootstrapIfEmpty = true }))
            {
                using (var conn = TursoRawConnection.Open(writer))
                {
                    conn.Execute("CREATE TABLE IF NOT EXISTS kv (k TEXT PRIMARY KEY, v TEXT)");
                    conn.Execute($"INSERT INTO kv (k, v) VALUES ('{key}', 'world')");
                }

                writer.Push();
            }

            using var reader = TursoSyncDatabase.Create(new TursoSyncConfig { Path = readerPath, RemoteUrl = url, AuthToken = token, BootstrapIfEmpty = true });
            reader.Pull();
            using var readConn = TursoRawConnection.Open(reader);
            readConn.QueryScalar($"SELECT v FROM kv WHERE k = '{key}'").Should().Be("world");
        }
        finally
        {
            Cleanup(writerPath);
            Cleanup(readerPath);
        }
    }

    [TestMethod]
    public void RemoteSync_BadAuthToken_FailsClearly()
    {
        var (url, _) = RemoteOrSkip();

        // A bogus token must surface as a loud error (the remote rejects /info with 401), not a hang or a
        // silently-empty database. Guards the Authorization header path in the HTTP IO handler.
        var dbPath = TempDb();
        try
        {
            var act = () => TursoSyncDatabase.Create(new TursoSyncConfig
            {
                Path = dbPath,
                RemoteUrl = url,
                AuthToken = "invalid.invalid.invalid",
                BootstrapIfEmpty = true,
            });
            act.Should().Throw<TursoException>();
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void RemoteSync_UpdateAndDelete_Propagate()
    {
        var (url, token) = RemoteOrSkip();

        // The round-trip test only covers INSERT; UPDATE and DELETE must sync too. A unique table keeps the
        // test idempotent against the persistent remote, and it is dropped (and the drop pushed) at the end.
        var table = "sync_ud_" + Guid.NewGuid().ToString("n");
        var writerPath = TempDb();
        var readerPath = TempDb();
        try
        {
            using (var writer = TursoSyncDatabase.Create(RemoteConfig(writerPath, url, token)))
            {
                using (var conn = TursoRawConnection.Open(writer))
                {
                    conn.Execute($"CREATE TABLE {table} (k INTEGER PRIMARY KEY, v TEXT)");
                    conn.Execute($"INSERT INTO {table} (k, v) VALUES (1, 'one'), (2, 'two'), (3, 'three')");
                    conn.Execute($"UPDATE {table} SET v = 'TWO' WHERE k = 2");
                    conn.Execute($"DELETE FROM {table} WHERE k = 3");
                }

                writer.Push();
            }

            using var reader = TursoSyncDatabase.Create(RemoteConfig(readerPath, url, token));
            reader.Pull();
            using var readConn = TursoRawConnection.Open(reader);
            readConn.QueryScalar($"SELECT v FROM {table} WHERE k = 1").Should().Be("one");
            readConn.QueryScalar($"SELECT v FROM {table} WHERE k = 2").Should().Be("TWO");
            readConn.QueryScalar($"SELECT count(*) FROM {table}").Should().Be(2L);
        }
        finally
        {
            DropRemoteTable(url, token, table);
            Cleanup(writerPath);
            Cleanup(readerPath);
        }
    }

    [TestMethod]
    public void RemoteSync_ValueTypes_RoundTrip()
    {
        var (url, token) = RemoteOrSkip();

        // Integer, real, text, NULL and BLOB must survive the sync encoding intact, not just short ASCII text.
        var table = "sync_vt_" + Guid.NewGuid().ToString("n");
        var blob = new byte[] { 0, 1, 2, 253, 254, 255 };
        var bigText = new string('x', 100_000);
        var writerPath = TempDb();
        var readerPath = TempDb();
        try
        {
            using (var writer = TursoSyncDatabase.Create(RemoteConfig(writerPath, url, token)))
            {
                using (var conn = TursoRawConnection.Open(writer))
                {
                    conn.Execute($"CREATE TABLE {table} (i INTEGER, r REAL, t TEXT, n TEXT, b BLOB, big TEXT)");
                    conn.Execute(
                        $"INSERT INTO {table} (i, r, t, n, b, big) VALUES " +
                        $"(42, 3.5, 'hello', NULL, x'000102fdfeff', '{bigText}')");
                }

                writer.Push();
            }

            using var reader = TursoSyncDatabase.Create(RemoteConfig(readerPath, url, token));
            reader.Pull();
            using var readConn = TursoRawConnection.Open(reader);
            readConn.QueryScalar($"SELECT i FROM {table}").Should().Be(42L);
            readConn.QueryScalar($"SELECT r FROM {table}").Should().Be(3.5);
            readConn.QueryScalar($"SELECT t FROM {table}").Should().Be("hello");
            readConn.QueryScalar($"SELECT n FROM {table}").Should().BeNull();
            readConn.QueryScalar($"SELECT b FROM {table}").Should().BeEquivalentTo(blob);
            readConn.QueryScalar($"SELECT big FROM {table}").Should().Be(bigText);
        }
        finally
        {
            DropRemoteTable(url, token, table);
            Cleanup(writerPath);
            Cleanup(readerPath);
        }
    }

    [TestMethod]
    public void RemoteSync_ReopenExistingLocalDb_Resumes()
    {
        var (url, token) = RemoteOrSkip();

        // Reopening an existing synced local database (BootstrapIfEmpty=false) must resume from the persisted
        // local state rather than re-bootstrap — a distinct engine path from the fresh-bootstrap round-trip.
        var table = "sync_re_" + Guid.NewGuid().ToString("n");
        var dbPath = TempDb();
        try
        {
            using (var db = TursoSyncDatabase.Create(RemoteConfig(dbPath, url, token)))
            {
                using (var conn = TursoRawConnection.Open(db))
                {
                    conn.Execute($"CREATE TABLE {table} (k INTEGER PRIMARY KEY, v TEXT)");
                    conn.Execute($"INSERT INTO {table} (k, v) VALUES (1, 'first')");
                }

                db.Push();
            }

            // Reopen the same local directory without bootstrapping; the row must still be present locally.
            using (var reopened = TursoSyncDatabase.Create(
                new TursoSyncConfig { Path = dbPath, RemoteUrl = url, AuthToken = token, BootstrapIfEmpty = false }))
            {
                using var conn = TursoRawConnection.Open(reopened);
                conn.QueryScalar($"SELECT v FROM {table} WHERE k = 1").Should().Be("first");

                conn.Execute($"INSERT INTO {table} (k, v) VALUES (2, 'second')");
                reopened.Push();
                conn.QueryScalar($"SELECT count(*) FROM {table}").Should().Be(2L);
            }
        }
        finally
        {
            DropRemoteTable(url, token, table);
            Cleanup(dbPath);
        }
    }

    // ---- remote-sync harness -----------------------------------------------------------------------

    /// <summary>Skip (Inconclusive) unless the native and the cloud sync credentials are both present.</summary>
    private static (string Url, string? Token) RemoteOrSkip()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("turso_sync_sdk_kit native library not found");
        }

        var url = Environment.GetEnvironmentVariable("TWEED_TURSO_SYNC_URL");
        var token = Environment.GetEnvironmentVariable("TWEED_TURSO_SYNC_TOKEN");
        if (string.IsNullOrWhiteSpace(url))
        {
            Assert.Inconclusive("Set TWEED_TURSO_SYNC_URL (+ TWEED_TURSO_SYNC_TOKEN) to run the remote sync round-trip.");
        }

        return (url!, token);
    }

    private static TursoSyncConfig RemoteConfig(string path, string url, string? token) =>
        new() { Path = path, RemoteUrl = url, AuthToken = token, BootstrapIfEmpty = true };

    /// <summary>Drop a test table on the remote and push, so gated cloud tests don't accumulate schema.</summary>
    private static void DropRemoteTable(string url, string? token, string table)
    {
        try
        {
            var path = TempDb();
            using var db = TursoSyncDatabase.Create(RemoteConfig(path, url, token));
            using (var conn = TursoRawConnection.Open(db))
            {
                conn.Execute($"DROP TABLE IF EXISTS {table}");
            }

            db.Push();
            Cleanup(path);
        }
        catch
        {
            // best-effort remote cleanup — never fail a test on teardown
        }
    }

    private static string TempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tweed-turso-sync-" + Guid.NewGuid().ToString("n"));
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
