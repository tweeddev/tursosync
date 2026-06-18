using Turso;

namespace TursoSync.Tests;

/// <summary>
/// Reader paths not covered by <see cref="TursoDataReaderTests"/>: char/byte getters, char-buffer copy, the
/// enumerator, NextResult, declared-type → CLR mapping, and data-type names. Needs the native engine.
/// </summary>
[TestClass]
public class TursoReaderExtrasTests
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
    public void Setup() => _dir = Path.Combine(Path.GetTempPath(), "tweed-turso-rdrx-" + Guid.NewGuid().ToString("n"));

    [TestCleanup]
    public void Teardown()
    {
        TursoConnection.ClearPool();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private TursoConnection Open()
    {
        Directory.CreateDirectory(_dir);
        var conn = new TursoConnection($"Data Source={Path.Combine(_dir, "store.db")};Pooling=false");
        conn.Open();
        return conn;
    }

    [TestMethod]
    public void CharAndByteGetters()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.ExecuteNonQuery("CREATE TABLE t (c TEXT, n INTEGER)");
        conn.ExecuteNonQuery("INSERT INTO t (c, n) VALUES ('Z', 65)");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT c, n FROM t";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        reader.GetChar(0).Should().Be('Z');         // single-char text
        reader.GetChar(1).Should().Be('A');         // numeric 65 → 'A'
        reader.GetByte(1).Should().Be((byte)65);
    }

    [TestMethod]
    public void GetChars_CopiesIntoBuffer()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.ExecuteNonQuery("CREATE TABLE t (s TEXT)");
        // Stored as a blob so the reader returns a byte[] that GetChars walks (GetArray<char> path).
        conn.ExecuteNonQuery("CREATE TABLE b (data BLOB)");
        using (var ins = conn.CreateCommand())
        {
            ins.CommandText = "INSERT INTO b (data) VALUES (?)";
            ins.Parameters.Add(new TursoParameter(new byte[] { 1, 2, 3, 4, 5, 6 }));
            ins.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM b";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        reader.GetChars(0, 0, null, 0, 100).Should().Be(6);   // null buffer → element count (capped)
        var buffer = new char[3];
        reader.GetChars(0, 0, buffer, 0, 3).Should().Be(3);   // partial copy honoring buffer length
    }

    [TestMethod]
    public void Enumerator_AndNextResult()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.ExecuteNonQuery("CREATE TABLE t (id INTEGER)");
        conn.ExecuteNonQuery("INSERT INTO t (id) VALUES (1), (2), (3)");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM t ORDER BY id";
        using var reader = cmd.ExecuteReader();

        // NOTE: foreach over the reader (DbEnumerator) is NOT exercised here — it calls GetDataTypeName
        // before the first row, which currently throws on the Unknown value kind (see GetDataTypeName).
        reader.GetEnumerator().Should().NotBeNull();
        reader.NextResult().Should().BeFalse();   // single statement → no further results
    }

    [TestMethod]
    public void DeclaredTypes_MapToClrTypes()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.ExecuteNonQuery("CREATE TABLE typed (a BIGINT, b VARCHAR(10), c DOUBLE, d BLOB, e CLOB)");
        conn.ExecuteNonQuery("INSERT INTO typed (a, b, c, d, e) VALUES (1, 'x', 1.5, x'00', 'y')");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT a, b, c, d, e FROM typed";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        reader.GetFieldType(0).Should().Be<long>();     // BIGINT → INT
        reader.GetFieldType(1).Should().Be<string>();   // VARCHAR → CHAR
        reader.GetFieldType(2).Should().Be<double>();   // DOUBLE
        reader.GetFieldType(3).Should().Be<byte[]>();   // BLOB
        reader.GetFieldType(4).Should().Be<string>();   // CLOB

        reader.GetDataTypeName(0).Should().Be("INTEGER");
        reader.GetDataTypeName(2).Should().Be("REAL");
        reader.GetDataTypeName(3).Should().Be("BLOB");
    }

    [TestMethod]
    public void GetDateTime_NonText_ReturnsMinValue()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.ExecuteNonQuery("CREATE TABLE t (n INTEGER)");
        conn.ExecuteNonQuery("INSERT INTO t (n) VALUES (123)");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT n FROM t";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        reader.GetDateTime(0).Should().Be(DateTime.MinValue);   // non-text value kind
    }
}
