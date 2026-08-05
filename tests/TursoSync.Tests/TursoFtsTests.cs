using System.Data.Common;
using Turso.Sync;

namespace TursoSync.Tests;

/// <summary>
/// Full-text search against the real native engine, exercised through the public <see cref="TursoConnection"/>
/// surface — so the whole path is covered: the <c>Experimental Index Method</c> connection-string key →
/// <see cref="TursoConnectionStringBuilder.ToConfig"/> → the comma-separated experimental-feature string the
/// native parses. Proves the tantivy index method (<c>CREATE INDEX … USING fts</c>, <c>fts_match/score/highlight</c>,
/// <c>OPTIMIZE INDEX</c>) works when the flag is on, works <b>inside an encrypted database</b>, and stays
/// gated when the flag is off. Skipped (inconclusive) when the native library isn't present.
/// </summary>
[TestClass]
public class TursoFtsTests
{
    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "tursosync-fts-" + Guid.NewGuid().ToString("n"), "store.db");

    private static void Cleanup(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static async Task ExecAsync(TursoConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedArticlesAsync(TursoConnection conn)
    {
        await ExecAsync(conn, "CREATE TABLE articles (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");
        await ExecAsync(conn, "CREATE INDEX idx_articles ON articles USING fts (body)");
        // Ranking invariant the {3, 1} assertions depend on: docs 1 and 3 both contain 'invoice' and
        // 'payment' exactly once, but doc 1 is deliberately LONGER (13 tokens vs 7), so BM25 ranks doc 3
        // strictly higher for both terms. When the bodies were equal length (both 7 tokens) the scores tied
        // bit-for-bit and `ORDER BY fts_score DESC` flipped the order between runs — a real ~50% flake.
        await ExecAsync(conn, "INSERT INTO articles (id, body) VALUES (1, 'please find the invoice attached for payment when you have a spare moment')");
        await ExecAsync(conn, "INSERT INTO articles (id, body) VALUES (2, 'want to grab lunch on friday')");
        await ExecAsync(conn, "INSERT INTO articles (id, body) VALUES (3, 'reminder: the invoice payment is now overdue')");
    }

    private static async Task<List<long>> MatchIdsAsync(TursoConnection conn, string query)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM articles WHERE fts_match(body, @q) ORDER BY fts_score(body, @q) DESC";
        var p = cmd.CreateParameter();
        p.ParameterName = "@q";
        p.Value = query;
        cmd.Parameters.Add(p);

        var ids = new List<long>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) ids.Add(r.GetInt64(0));
        return ids;
    }

    [TestMethod]
    public async Task Fts_index_and_query_work_when_the_experimental_flag_is_on()
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
            await using var conn = new TursoConnection(
                $"Data Source={dbPath};Experimental Index Method=true;Pooling=false");
            await conn.OpenAsync();

            await SeedArticlesAsync(conn);

            (await MatchIdsAsync(conn, "invoice")).Should().BeEquivalentTo(new[] { 1L, 3L }); // both docs mention "invoice"
            (await MatchIdsAsync(conn, "lunch")).Should().Equal(2L);
            (await MatchIdsAsync(conn, "zxqwv")).Should().BeEmpty();

            // fts_highlight wraps the matched term.
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT fts_highlight(body, '[', ']', 'invoice') FROM articles WHERE id = 1";
                ((string)(await cmd.ExecuteScalarAsync())!).Should().Contain("[invoice]");
            }

            // OPTIMIZE INDEX (the maintenance hook) runs clean.
            await ExecAsync(conn, "OPTIMIZE INDEX idx_articles");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public async Task Fts_works_inside_an_encrypted_database_and_survives_reopen()
    {
        if (!TursoNativeLibrary.IsAvailable())
        {
            Assert.Inconclusive("turso_sync_sdk_kit native library not found");
            return;
        }

        const string key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var dbPath = NewDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var cs = $"Data Source={dbPath};Encryption Cipher=aes256gcm;Encryption Key={key};" +
                 "Experimental Index Method=true;Pooling=false";
        try
        {
            // Encryption + index_method together: the FTS index lives inside the encrypted file.
            await using (var conn = new TursoConnection(cs))
            {
                await conn.OpenAsync();
                await SeedArticlesAsync(conn);
                (await MatchIdsAsync(conn, "payment")).Should().Equal(3L, 1L);
            }

            // Reopen with the same key: index persisted and still queryable.
            await using (var conn = new TursoConnection(cs))
            {
                await conn.OpenAsync();
                (await MatchIdsAsync(conn, "invoice")).Should().Equal(3L, 1L);
            }
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [TestMethod]
    public async Task Without_the_flag_creating_an_fts_index_is_rejected()
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
            await using var conn = new TursoConnection($"Data Source={dbPath};Pooling=false");
            await conn.OpenAsync();
            await ExecAsync(conn, "CREATE TABLE articles (id INTEGER PRIMARY KEY, body TEXT NOT NULL)");

            var act = async () => await ExecAsync(conn, "CREATE INDEX idx ON articles USING fts (body)");

            (await act.Should().ThrowAsync<Exception>("USING fts needs the experimental index-method flag"))
                .Which.Message.Should().Contain("experimental");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }
}
