using System.Data.Common;
using Turso;

namespace TursoSync.Tests;

/// <summary>
/// Exercises the feature-complete provider surface against the real engine: UDFs, aggregates, collations,
/// load-extension enablement, and the <see cref="DbProviderFactory"/>. Inconclusive without the native lib.
/// </summary>
[TestClass]
public class TursoExtensionsTests
{
    private static string NewDb() =>
        Path.Combine(Path.GetTempPath(), "tweed-turso-ext-" + Guid.NewGuid().ToString("n"), "store.db");

    private static TursoConnection Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Pooling off so each test gets a fresh physical connection (extensions are per-connection).
        var c = new TursoConnection($"Data Source={path};Pooling=false");
        c.Open();
        return c;
    }

    private static void Cleanup(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static bool Skip() => !TursoNativeLibrary.IsAvailable();

    [TestMethod]
    public void ScalarFunction_IsInvoked()
    {
        if (Skip())
        {
            Assert.Inconclusive("native not found");
            return;
        }

        var path = NewDb();
        try
        {
            using var c = Open(path);
            c.CreateFunction("times_two", 1, args => Convert.ToInt64(args[0]) * 2);

            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT times_two(21)";
            Convert.ToInt64(cmd.ExecuteScalar()).Should().Be(42);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [TestMethod]
    public void ScalarFunction_StringResult()
    {
        if (Skip())
        {
            Assert.Inconclusive("native not found");
            return;
        }

        var path = NewDb();
        try
        {
            using var c = Open(path);
            c.CreateFunction("shout", 1, args => (args[0]?.ToString() ?? "") + "!");

            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT shout('hi')";
            cmd.ExecuteScalar().Should().Be("hi!");
        }
        finally
        {
            Cleanup(path);
        }
    }

    [TestMethod]
    public void Aggregate_SumsAcrossRows()
    {
        if (Skip())
        {
            Assert.Inconclusive("native not found");
            return;
        }

        var path = NewDb();
        try
        {
            using var c = Open(path);
            c.ExecuteNonQuery("CREATE TABLE n (x INTEGER)");
            c.ExecuteNonQuery("INSERT INTO n (x) VALUES (1),(2),(3),(4)");

            // seed 0; step adds; finalize returns the accumulator.
            c.CreateAggregate("my_sum", 1,
                seed: 0L,
                step: (acc, args) => Convert.ToInt64(acc) + Convert.ToInt64(args[0]),
                finalize: acc => acc);

            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT my_sum(x) FROM n";
            Convert.ToInt64(cmd.ExecuteScalar()).Should().Be(10);
        }
        finally
        {
            Cleanup(path);
        }
    }

    [TestMethod]
    public void Collation_OrdersByCustomRule()
    {
        if (Skip())
        {
            Assert.Inconclusive("native not found");
            return;
        }

        var path = NewDb();
        try
        {
            using var c = Open(path);
            // Reverse-ordinal collation.
            c.CreateCollation("rev", (a, b) => string.CompareOrdinal(b, a));
            c.ExecuteNonQuery("CREATE TABLE w (s TEXT)");
            c.ExecuteNonQuery("INSERT INTO w (s) VALUES ('a'),('b'),('c')");

            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT s FROM w ORDER BY s COLLATE rev";
            using var reader = cmd.ExecuteReader();
            var ordered = new List<string>();
            while (reader.Read())
            {
                ordered.Add(reader.GetString(0));
            }

            ordered.Should().Equal("c", "b", "a");
        }
        finally
        {
            Cleanup(path);
        }
    }

    [TestMethod]
    public void EnableExtensions_DoesNotThrow()
    {
        if (Skip())
        {
            Assert.Inconclusive("native not found");
            return;
        }

        var path = NewDb();
        try
        {
            using var c = Open(path);
            var act = () => c.EnableExtensions(true);
            act.Should().NotThrow();
        }
        finally
        {
            Cleanup(path);
        }
    }

    [TestMethod]
    public void Factory_CreatesProviderObjects()
    {
        DbProviderFactory factory = TursoFactory.Instance;
        factory.CreateConnection().Should().BeOfType<TursoConnection>();
        factory.CreateCommand().Should().BeOfType<TursoCommand>();
        factory.CreateParameter().Should().BeOfType<TursoParameter>();
        factory.CreateConnectionStringBuilder().Should().BeOfType<TursoConnectionStringBuilder>();
    }
}
