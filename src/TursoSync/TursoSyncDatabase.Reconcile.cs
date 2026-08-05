namespace Turso.Sync;

/// <summary>Options for <see cref="TursoSyncDatabase.ReconcileLocalTables"/>.</summary>
public sealed record TursoReconcileOptions
{
    /// <summary>
    /// False (default): rebuild only the local tables the server does not know — the ones stranded by
    /// creating them before the remote was attached. True: rebuild EVERY local user table, which also
    /// re-records rows that predate the attach in tables the server does know — the full "attached the
    /// cloud later" repair. Rebuilding rewrites each table's rows through the synced connection, so expect
    /// roughly two row-writes per row (staging + final) to travel to the server.
    /// </summary>
    public bool AllTables { get; init; }

    /// <summary>Push after a successful rebuild (default true), so the repair reaches the server at once.</summary>
    public bool PushAfter { get; init; } = true;

    /// <summary>
    /// Directory for the throwaway replica used to read the server's table set; default: a fresh directory
    /// under the system temp path, removed afterwards.
    /// </summary>
    public string? ScratchDirectory { get; init; }
}

/// <summary>What <see cref="TursoSyncDatabase.ReconcileLocalTables"/> did.</summary>
/// <param name="RebuiltTables">Tables rebuilt through the synced connection (DDL + rows now in CDC).</param>
/// <param name="SkippedTables">Tables that matched but were not rebuilt (unsupported shape — e.g. generated
/// columns, or no retrievable DDL); listed so the caller knows the repair is incomplete.</param>
/// <param name="RowsCopied">Total rows re-recorded across the rebuilt tables.</param>
public sealed record TursoReconcileResult(
    IReadOnlyList<string> RebuiltTables,
    IReadOnlyList<string> SkippedTables,
    long RowsCopied);

public sealed partial class TursoSyncDatabase
{
    /// <summary>
    /// Teach the server local tables it never learned. The sync engine only replicates changes recorded
    /// AFTER a replica is bound to its remote, so schema and rows that existed before the attach are
    /// stranded local-only — and a later revert/reconcile can silently DROP such tables to match the server
    /// (the failure <see cref="TursoSchemaGuardMode"/> detects). This repairs the strand in place: each
    /// affected table is rebuilt through the synced connection (stage rows → drop → re-create from its
    /// original DDL → re-insert → re-create indexes and triggers), so the DDL and every row enter CDC and
    /// replicate on the next push.
    /// </summary>
    /// <remarks>
    /// The server's table set is read by bootstrapping a throwaway replica from the remote (network
    /// required). Each table is rebuilt in its own transaction; a failure rolls that table back and
    /// rethrows, leaving already-rebuilt tables done — re-running is safe. Tables with generated columns
    /// are skipped (reported in <see cref="TursoReconcileResult.SkippedTables"/>): their re-insert would
    /// need column surgery this deliberately avoids.
    /// </remarks>
    public TursoReconcileResult ReconcileLocalTables(TursoReconcileOptions? options = null)
    {
        options ??= new TursoReconcileOptions();
        var config = _config
            ?? throw new InvalidOperationException("ReconcileLocalTables needs the database's create config.");
        if (string.IsNullOrEmpty(config.RemoteUrl))
        {
            throw new InvalidOperationException(
                "ReconcileLocalTables requires a remote — a local-only database has no server to reconcile with.");
        }

        var serverTables = FetchServerTables(config, options.ScratchDirectory);

        using var connection = TursoRawConnection.Open(this);
        var local = TursoSchemaGuard.UserTables(connection);
        var known = new HashSet<string>(serverTables, StringComparer.OrdinalIgnoreCase);
        var targets = options.AllTables ? local : local.Where(t => !known.Contains(t)).ToList();

        var rebuilt = new List<string>();
        var skipped = new List<string>();
        long rowsCopied = 0;
        foreach (var table in targets)
        {
            var ddl = SchemaSqlOf(connection, "table", table);
            if (ddl is null || ddl.Contains("GENERATED", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(table);
                continue;
            }

            rowsCopied += Rebuild(connection, table, ddl);
            rebuilt.Add(table);
        }

        if (options.PushAfter && rebuilt.Count > 0)
        {
            Push();
        }

        return new TursoReconcileResult(rebuilt, skipped, rowsCopied);
    }

    /// <summary>
    /// Rebuild one table through the synced connection so its schema + rows are freshly recorded in CDC.
    /// Stage-drop-recreate rather than rename-recreate: RENAME can rewrite REFERENCES clauses in OTHER
    /// tables to the temporary name, which would leave them pointing at a table this then drops. With
    /// drop-and-recreate the original name never changes for the rest of the schema.
    /// </summary>
    private static long Rebuild(TursoRawConnection connection, string table, string ddl)
    {
        // Index/trigger DDL is captured before the drop (DROP TABLE removes both), recreated after.
        var indexes = SchemaSqlsOn(connection, "index", table);
        var triggers = SchemaSqlsOn(connection, "trigger", table);
        var staging = Quote("__tursosync_reconcile__" + table);
        var target = Quote(table);

        connection.Execute("BEGIN IMMEDIATE");
        try
        {
            connection.Execute($"DROP TABLE IF EXISTS {staging}");
            connection.Execute($"CREATE TABLE {staging} AS SELECT * FROM {target}");
            connection.Execute($"DROP TABLE {target}");
            connection.Execute(ddl);
            long rows;
            using (var insert = connection.Prepare($"INSERT INTO {target} SELECT * FROM {staging}"))
            {
                while (insert.Step())
                {
                    // no rows from an INSERT…SELECT; drain for completeness
                }

                rows = insert.RowsAffected;
            }

            connection.Execute($"DROP TABLE {staging}");
            foreach (var sql in indexes)
            {
                connection.Execute(sql);
            }

            foreach (var sql in triggers)
            {
                connection.Execute(sql);
            }

            connection.Execute("COMMIT");
            return rows;
        }
        catch
        {
            try { connection.Execute("ROLLBACK"); } catch { /* connection state unknown; surface the original */ }
            throw;
        }
    }

    /// <summary>The server's user tables, read from a throwaway replica bootstrapped off the remote.</summary>
    private static List<string> FetchServerTables(TursoSyncConfig config, string? scratchDirectory)
    {
        var dir = scratchDirectory
            ?? Path.Combine(Path.GetTempPath(), "tursosync-reconcile-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            using var scratch = Create(config with
            {
                Path = Path.Combine(dir, "scratch.db"),
                BootstrapIfEmpty = true,
                SchemaGuard = TursoSchemaGuardMode.Off, // a fresh scratch replica has nothing to protect
                ClientName = config.ClientName + "-reconcile",
            });
            using var connection = TursoRawConnection.Open(scratch);
            return TursoSchemaGuard.UserTables(connection);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort scratch cleanup */ }
        }
    }

    /// <summary>The stored DDL of one named schema object, or null when absent (or auto-created, sql NULL).</summary>
    private static string? SchemaSqlOf(TursoRawConnection connection, string type, string name)
    {
        using var stmt = connection.Prepare(
            "SELECT sql FROM sqlite_master WHERE type = ?1 AND name = ?2");
        stmt.Bind(1, type);
        stmt.Bind(2, name);
        return stmt.Step() ? stmt.GetValue(0) as string : null;
    }

    /// <summary>Stored DDL of all objects of <paramref name="type"/> attached to <paramref name="table"/>
    /// (auto-created ones have NULL sql and are skipped — the table DDL re-creates them itself).</summary>
    private static List<string> SchemaSqlsOn(TursoRawConnection connection, string type, string table)
    {
        var sqls = new List<string>();
        using var stmt = connection.Prepare(
            "SELECT sql FROM sqlite_master WHERE type = ?1 AND tbl_name = ?2 AND sql IS NOT NULL");
        stmt.Bind(1, type);
        stmt.Bind(2, table);
        while (stmt.Step())
        {
            if (stmt.GetValue(0) is string sql)
            {
                sqls.Add(sql);
            }
        }

        return sqls;
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
