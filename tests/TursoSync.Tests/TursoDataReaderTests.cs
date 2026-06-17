using Turso;

namespace TursoSync.Tests;

/// <summary>
/// Exercises the <see cref="TursoDataReader"/> typed getters / metadata against the real engine across the
/// four storage classes (integer, real, text, blob, null). Inconclusive without the native library.
/// </summary>
[TestClass]
public class TursoDataReaderTests
{
    private static string NewDb() =>
        Path.Combine(Path.GetTempPath(), "tweed-turso-rdr-" + Guid.NewGuid().ToString("n"), "store.db");

    [TestMethod]
    public void TypedGetters_AcrossStorageClasses()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("native not found");
            return;
        }

        var path = NewDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var c = new TursoConnection($"Data Source={path};Pooling=false");
            c.Open();
            c.ExecuteNonQuery("CREATE TABLE r (i INTEGER, d REAL, t TEXT, b BLOB, n TEXT)");
            c.ExecuteNonQuery("INSERT INTO r (i, d, t, b, n) VALUES (42, 2.5, 'hello', x'010203', NULL)");

            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT i, d, t, b, n FROM r";
            using var reader = cmd.ExecuteReader();

            reader.FieldCount.Should().Be(5);
            reader.GetName(0).Should().Be("i");
            reader.GetOrdinal("t").Should().Be(2);
            reader.Read().Should().BeTrue();

            reader.GetInt32(0).Should().Be(42);
            reader.GetInt64(0).Should().Be(42L);
            reader.GetInt16(0).Should().Be((short)42);
            reader.GetBoolean(0).Should().BeTrue();
            reader.GetFieldType(0).Should().Be(typeof(long));

            reader.GetDouble(1).Should().Be(2.5);
            reader.GetFloat(1).Should().Be(2.5f);
            reader.GetDecimal(1).Should().Be(2.5m);

            reader.GetString(2).Should().Be("hello");
            reader["t"].Should().Be("hello");
            reader[0].Should().Be(42L);

            var buf = new byte[3];
            reader.GetBytes(3, 0, buf, 0, 3).Should().Be(3);
            buf.Should().Equal(1, 2, 3);

            reader.IsDBNull(4).Should().BeTrue();
            reader.GetValue(4).Should().Be(DBNull.Value);

            var values = new object[5];
            reader.GetValues(values).Should().Be(5);

            reader.Read().Should().BeFalse();
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void GetGuidAndDateTime_FromText()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("native not found");
            return;
        }

        var path = NewDb();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
            using var c = new TursoConnection($"Data Source={path};Pooling=false");
            c.Open();
            c.ExecuteNonQuery("CREATE TABLE g (id TEXT, ts TEXT)");
            using (var ins = c.CreateCommand())
            {
                ins.CommandText = "INSERT INTO g (id, ts) VALUES (@id, @ts)";
                var pid = ins.CreateParameter();
                pid.ParameterName = "@id";
                pid.Value = guid.ToString();
                ins.Parameters.Add(pid);
                var pts = ins.CreateParameter();
                pts.ParameterName = "@ts";
                pts.Value = "2026-06-17 08:30:00";
                ins.Parameters.Add(pts);
                ins.ExecuteNonQuery();
            }

            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, ts FROM g";
            using var reader = cmd.ExecuteReader();
            reader.Read().Should().BeTrue();
            reader.GetGuid(0).Should().Be(guid);
            reader.GetDateTime(1).Should().Be(new DateTime(2026, 6, 17, 8, 30, 0));
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
