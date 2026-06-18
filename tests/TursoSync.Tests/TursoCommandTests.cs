using System.Data;
using System.Data.Common;
using Turso;

namespace TursoSync.Tests;

/// <summary>
/// Exercises <see cref="TursoCommand"/> execution paths (scalar / non-query / reader, parameter binding by
/// position and name, validation errors) and the <see cref="TursoDataReader"/> surface not covered by
/// <see cref="TursoDataReaderTests"/>. Against the real native engine; skipped when it's absent.
/// </summary>
[TestClass]
public class TursoCommandTests
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
    public void Setup() => _dir = Path.Combine(Path.GetTempPath(), "tweed-turso-cmd-" + Guid.NewGuid().ToString("n"));

    [TestCleanup]
    public void Teardown()
    {
        TursoConnection.ClearPool();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private TursoConnection OpenWithTable()
    {
        Directory.CreateDirectory(_dir);
        var conn = new TursoConnection($"Data Source={Path.Combine(_dir, "store.db")}");
        conn.Open();
        conn.ExecuteNonQuery("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT, score REAL, data BLOB)");
        return conn;
    }

    // ---- command execution -----------------------------------------------------------------------

    [TestMethod]
    public void ExecuteScalar_ReturnsFirstColumn()
    {
        if (NativeMissing()) return;

        using var conn = OpenWithTable();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 7";
        cmd.ExecuteScalar().Should().Be(7L);
    }

    [TestMethod]
    public void ExecuteScalar_NoRows_ReturnsNull()
    {
        if (NativeMissing()) return;

        using var conn = OpenWithTable();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM t WHERE id = 999";
        cmd.ExecuteScalar().Should().BeNull();
    }

    [TestMethod]
    public void ExecuteNonQuery_ReturnsRowsAffected()
    {
        if (NativeMissing()) return;

        using var conn = OpenWithTable();
        conn.ExecuteNonQuery("INSERT INTO t (id, name) VALUES (1, 'a'), (2, 'b')");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE t SET name = 'z'";
        cmd.ExecuteNonQuery().Should().Be(2);
    }

    [TestMethod]
    public void Parameters_BindByPosition()
    {
        if (NativeMissing()) return;

        using var conn = OpenWithTable();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO t (id, name) VALUES (?, ?)";
        cmd.Parameters.Add(new TursoParameter(1));
        cmd.Parameters.Add(new TursoParameter("alice"));
        cmd.ExecuteNonQuery().Should().Be(1);

        using var read = conn.CreateCommand();
        read.CommandText = "SELECT name FROM t WHERE id = 1";
        read.ExecuteScalar().Should().Be("alice");
    }

    [TestMethod]
    public void Parameters_BindByName_WithSigilResolution()
    {
        if (NativeMissing()) return;

        using var conn = OpenWithTable();
        using var cmd = (TursoCommand)conn.CreateCommand();
        cmd.CommandText = "INSERT INTO t (id, name) VALUES (@id, @name)";
        cmd.Parameters.AddWithValue("id", 5);       // no sigil — must resolve to @id
        cmd.Parameters.AddWithValue("@name", "bob");
        cmd.ExecuteNonQuery().Should().Be(1);

        using var read = conn.CreateCommand();
        read.CommandText = "SELECT name FROM t WHERE id = 5";
        read.ExecuteScalar().Should().Be("bob");
    }

    [TestMethod]
    public void Parameters_UnknownName_Throws()
    {
        if (NativeMissing()) return;

        using var conn = OpenWithTable();
        using var cmd = (TursoCommand)conn.CreateCommand();
        cmd.CommandText = "SELECT @known";
        cmd.Parameters.AddWithValue("@nope", 1);
        var act = () => cmd.ExecuteScalar();
        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void Parameters_MissingValue_Throws()
    {
        if (NativeMissing()) return;

        using var conn = OpenWithTable();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ?, ?";
        cmd.Parameters.Add(new TursoParameter(1));   // only one of two bound
        var act = () => cmd.ExecuteScalar();
        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void Execute_WithoutConnection_Throws()
    {
        if (NativeMissing()) return;

        using var cmd = new TursoCommand { CommandText = "SELECT 1" };
        var act = () => cmd.ExecuteScalar();
        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void Prepare_WithoutCommandText_Throws()
    {
        if (NativeMissing()) return;

        using var conn = OpenWithTable();
        using var cmd = conn.CreateCommand();
        var act = cmd.Prepare;
        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void CommandType_NonText_Throws()
    {
        using var cmd = new TursoCommand();
        cmd.CommandType = CommandType.Text;          // allowed
        var act = () => cmd.CommandType = CommandType.StoredProcedure;
        act.Should().Throw<NotSupportedException>();
    }

    [TestMethod]
    public void CommandTimeout_Negative_Throws()
    {
        using var cmd = new TursoCommand();
        var act = () => cmd.CommandTimeout = -1;
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void DbTransaction_WrongType_Throws()
    {
        using var cmd = new TursoCommand();
        var act = () => ((IDbCommand)cmd).Transaction = new FakeTransaction();
        act.Should().Throw<ArgumentException>();
    }

    // ---- data reader surface ---------------------------------------------------------------------

    [TestMethod]
    public void Reader_Metadata_AndAccessors()
    {
        if (NativeMissing()) return;

        using var conn = OpenWithTable();
        conn.ExecuteNonQuery("INSERT INTO t (id, name, score) VALUES (1, 'alice', 9.5)");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, score FROM t";
        using var reader = cmd.ExecuteReader();

        reader.FieldCount.Should().Be(3);
        reader.HasRows.Should().BeTrue();
        reader.IsClosed.Should().BeFalse();
        reader.Depth.Should().Be(0);
        reader.GetName(1).Should().Be("name");
        reader.GetOrdinal("score").Should().Be(2);

        reader.Read().Should().BeTrue();
        reader.GetInt64(0).Should().Be(1L);
        reader["name"].Should().Be("alice");
        reader[2].Should().Be(9.5);
        reader.IsDBNull(0).Should().BeFalse();

        var values = new object[3];
        reader.GetValues(values).Should().Be(3);
        values[1].Should().Be("alice");

        reader.Read().Should().BeFalse();
    }

    [TestMethod]
    public void Reader_GetOrdinal_Unknown_Throws()
    {
        if (NativeMissing()) return;

        using var conn = OpenWithTable();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM t";
        using var reader = cmd.ExecuteReader();

        var act = () => reader.GetOrdinal("missing");
        act.Should().Throw<IndexOutOfRangeException>();
    }

    [TestMethod]
    public void Reader_TypesAndNull_AreReported()
    {
        if (NativeMissing()) return;

        using var conn = OpenWithTable();
        conn.ExecuteNonQuery("INSERT INTO t (id, name, score) VALUES (1, NULL, 2.5)");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, score FROM t";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        reader.GetFieldType(0).Should().Be<long>();      // INTEGER PRIMARY KEY → declared INTEGER
        reader.GetFieldType(1).Should().Be<string>();    // TEXT
        reader.GetFieldType(2).Should().Be<double>();    // REAL
        reader.IsDBNull(1).Should().BeTrue();
        reader.GetValue(1).Should().Be(DBNull.Value);
        reader.GetDataTypeName(0).Should().Be("INTEGER");
    }

    [TestMethod]
    public void Reader_AfterDispose_IsClosed_AndReadThrows()
    {
        if (NativeMissing()) return;

        using var conn = OpenWithTable();
        conn.ExecuteNonQuery("INSERT INTO t (id) VALUES (1)");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM t";

        var reader = cmd.ExecuteReader();
        reader.Dispose();

        reader.IsClosed.Should().BeTrue();
        var act = () => reader.Read();
        act.Should().Throw<InvalidOperationException>();
    }

    [TestMethod]
    public void Reader_CloseConnectionBehavior_ClosesConnection()
    {
        if (NativeMissing()) return;

        using var conn = OpenWithTable();
        conn.ExecuteNonQuery("INSERT INTO t (id) VALUES (1)");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM t";

        using (var reader = cmd.ExecuteReader(CommandBehavior.CloseConnection))
        {
            reader.Read().Should().BeTrue();
        }

        conn.State.Should().Be(ConnectionState.Closed);
    }

    [TestMethod]
    public void Reader_GetBytes_ReadsBlob()
    {
        if (NativeMissing()) return;

        using var conn = OpenWithTable();
        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO t (id, data) VALUES (1, ?)";
            insert.Parameters.Add(new TursoParameter(new byte[] { 1, 2, 3, 4 }));
            insert.ExecuteNonQuery();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data FROM t WHERE id = 1";
        using var reader = cmd.ExecuteReader();
        reader.Read();

        reader.GetBytes(0, 0, null, 0, 100).Should().Be(4);   // null buffer → bytes available (capped at length)
        var buffer = new byte[4];
        reader.GetBytes(0, 0, buffer, 0, 4).Should().Be(4);
        buffer.Should().Equal(1, 2, 3, 4);
    }

    private sealed class FakeTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
        protected override DbConnection? DbConnection => null;
        public override void Commit() { }
        public override void Rollback() { }
    }
}
