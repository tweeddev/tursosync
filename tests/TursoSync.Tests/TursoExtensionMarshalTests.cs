using Turso;

namespace TursoSync.Tests;

/// <summary>
/// Drives <c>TursoExtensionMarshal</c> across every argument and result value kind through registered UDFs
/// and aggregates: argument unmarshalling (int/real/text/blob/null), result marshalling (the CreateResult
/// type switch), and error propagation. Needs the native engine.
/// </summary>
[TestClass]
public class TursoExtensionMarshalTests
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
    public void Setup() => _dir = Path.Combine(Path.GetTempPath(), "tweed-turso-marshal-" + Guid.NewGuid().ToString("n"));

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
    public void Scalar_RoundTripsEveryArgAndResultKind()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        // Identity UDF: each call unmarshals one argument and re-marshals it as the result, exercising both
        // ReadArguments (integer/real/text/blob/null) and CreateResult for the matching CLR types.
        conn.CreateFunction("ident", 1, args => args[0]);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ident(42), ident(2.5), ident('hi'), ident(x'010203'), ident(NULL)";
        using var reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue();

        reader.GetValue(0).Should().Be(42L);
        reader.GetValue(1).Should().Be(2.5);
        reader.GetValue(2).Should().Be("hi");
        ((byte[])reader.GetValue(3)).Should().Equal(1, 2, 3);
        reader.IsDBNull(4).Should().BeTrue();
    }

    [TestMethod]
    public void Scalar_ResultTypeConversions()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.CreateFunction("as_bool", 0, _ => true);                 // bool → integer 1
        conn.CreateFunction("as_byte", 0, _ => (byte)7);             // byte → integer
        conn.CreateFunction("as_decimal", 0, _ => 1.5m);            // decimal → real
        conn.CreateFunction("as_guid", 0, _ => Guid.Empty);        // other → invariant text

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT as_bool(), as_byte(), as_decimal(), as_guid()";
        using var reader = cmd.ExecuteReader();
        reader.Read().Should().BeTrue();

        reader.GetValue(0).Should().Be(1L);
        reader.GetValue(1).Should().Be(7L);
        reader.GetValue(2).Should().Be(1.5);
        reader.GetValue(3).Should().Be(Guid.Empty.ToString());
    }

    [TestMethod]
    public void Scalar_ThrowingFunction_SurfacesAsError()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.CreateFunction("boom", 0, _ => throw new InvalidOperationException("kaboom"));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT boom()";
        var act = () => cmd.ExecuteScalar();
        act.Should().Throw<TursoException>();
    }

    [TestMethod]
    public void Scalar_Unregister_RemovesFunction()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.CreateFunction("temp", 0, _ => 1L);
        conn.CreateFunction("temp", 0, null);    // null function → unregister path

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT temp()";
        var act = () => cmd.ExecuteScalar();
        act.Should().Throw<TursoException>();
    }

    [TestMethod]
    public void Aggregate_OverTextArgs_ReturnsText()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.ExecuteNonQuery("CREATE TABLE words (w TEXT)");
        conn.ExecuteNonQuery("INSERT INTO words (w) VALUES ('a'), ('b'), ('c')");

        // Step reads a text argument each row; finalize returns a text result.
        conn.CreateAggregate(
            "concat_all",
            argc: 1,
            seed: string.Empty,
            step: (acc, args) => (string)acc! + (string)args[0]!,
            finalize: acc => acc);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT concat_all(w) FROM words";
        cmd.ExecuteScalar().Should().Be("abc");
    }

    [TestMethod]
    public void Collation_OverUtf8_Compares()
    {
        if (NativeMissing()) return;

        using var conn = Open();
        conn.ExecuteNonQuery("CREATE TABLE c (v TEXT)");
        conn.ExecuteNonQuery("INSERT INTO c (v) VALUES ('a'), ('B'), ('c')");

        conn.CreateCollation("ci", (l, r) => string.Compare(l, r, StringComparison.OrdinalIgnoreCase));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM c ORDER BY v COLLATE ci";
        using var reader = cmd.ExecuteReader();

        var ordered = new List<string>();
        while (reader.Read())
        {
            ordered.Add(reader.GetString(0));
        }

        ordered.Should().Equal("a", "B", "c");   // case-insensitive ordering
    }
}
