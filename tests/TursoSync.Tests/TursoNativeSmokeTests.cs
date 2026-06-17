using Turso;

namespace TursoSync.Tests;

/// <summary>
/// End-to-end smoke tests against the real <c>turso_sync_sdk_kit</c> native library, exercising a local
/// (offline, no remote) synced database. These validate the interop marshaling, the resume/IO loop, and
/// the FULL_READ/FULL_WRITE file-IO handlers without needing a Turso Cloud token. Skipped (inconclusive)
/// when the native library isn't present on the machine.
/// </summary>
[TestClass]
public class TursoNativeSmokeTests
{
    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "tweed-turso-" + Guid.NewGuid().ToString("n"), "store.db");

    private static TursoSyncDatabase OpenLocal(string path) =>
        TursoSyncDatabase.Create(new TursoSyncConfig { Path = path, BootstrapIfEmpty = false });

    private static void Cleanup(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void Native_Library_Loads()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("turso_sync_sdk_kit native library not found (set TURSOSYNC_NATIVE_DIR)");
            return;
        }

        TursoNativeLibrary.IsAvailable().Should().BeTrue();
    }

    [TestMethod]
    public void LocalSyncedDb_RoundTrips_CreateInsertSelect()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("turso_sync_sdk_kit native library not found");
            return;
        }

        var dbPath = NewDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        try
        {
            using var db = OpenLocal(dbPath);
            using var conn = TursoRawConnection.Open(db);

            conn.Execute("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");

            using (var insert = conn.Prepare("INSERT INTO t (id, name) VALUES (?, ?)"))
            {
                insert.Bind(1, 1L);
                insert.Bind(2, "alice");
                insert.Step().Should().BeFalse("INSERT yields no rows");
                insert.RowsAffected.Should().Be(1);
            }

            conn.QueryScalar("SELECT COUNT(*) FROM t").Should().Be(1L);
            conn.QueryScalar("SELECT name FROM t WHERE id = 1").Should().Be("alice");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void Encryption_RoundTrips_AndRejectsWrongKey()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("turso_sync_sdk_kit native library not found");
            return;
        }

        const string key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string wrongKey = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
        var dbPath = NewDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        try
        {
            TursoSyncConfig Enc(string k) => new()
            {
                Path = dbPath,
                EncryptionCipher = TursoEncryptionCipher.Aes256Gcm.ToName(),
                EncryptionKey = k,
            };

            // Local at-rest encryption is the base-engine lane (matches the official OpenDatabaseWithEncryption).
            using (var conn = TursoRawConnection.OpenLocal(Enc(key)))
            {
                conn.Execute("CREATE TABLE secret (id INTEGER PRIMARY KEY, v TEXT)");
                using var ins = conn.Prepare("INSERT INTO secret (id, v) VALUES (1, 'classified')");
                ins.Step();
            }

            // Correct key reads it back.
            using (var conn = TursoRawConnection.OpenLocal(Enc(key)))
            {
                conn.QueryScalar("SELECT v FROM secret WHERE id = 1").Should().Be("classified");
            }

            // Wrong key must fail to open/read.
            var act = () =>
            {
                using var conn = TursoRawConnection.OpenLocal(Enc(wrongKey));
                conn.QueryScalar("SELECT v FROM secret WHERE id = 1");
            };
            act.Should().Throw<TursoException>();
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public void LocalSyncedDb_Persists_AcrossReopen()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("turso_sync_sdk_kit native library not found");
            return;
        }

        var dbPath = NewDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        try
        {
            using (var db = OpenLocal(dbPath))
            using (var conn = TursoRawConnection.Open(db))
            {
                conn.Execute("CREATE TABLE kv (k TEXT PRIMARY KEY, v TEXT)");
                using var ins = conn.Prepare("INSERT INTO kv (k, v) VALUES (?, ?)");
                ins.Bind(1, "hello");
                ins.Bind(2, "world");
                ins.Step();
                db.Checkpoint();
            }

            // Reopen the same file: the FULL_READ path must reload what FULL_WRITE persisted.
            using (var db = OpenLocal(dbPath))
            using (var conn = TursoRawConnection.Open(db))
            {
                conn.QueryScalar("SELECT v FROM kv WHERE k = 'hello'").Should().Be("world");
            }
        }
        finally
        {
            Cleanup(dbPath);
        }
    }
}
