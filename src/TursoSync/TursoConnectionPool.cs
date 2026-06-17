using System.Collections.Concurrent;

namespace Turso;

/// <summary>
/// A small physical-connection pool for Turso, keyed by connection string. Opening a Turso connection is
/// expensive — the sync lane re-reads metadata/bootstraps (~2.5x a base open), and even a base open +
/// durable write costs ~80x a reused connection (measured). Tweed's store opens a connection per operation,
/// so without pooling that cost dominates. ADO.NET's other providers (Npgsql, Microsoft.Data.Sqlite) pool
/// by default; this gives Turso the same.
/// </summary>
internal static class TursoConnectionPool
{
    /// <summary>Max idle physical connections kept per connection string.</summary>
    private const int MaxIdlePerKey = 4;

    private static readonly ConcurrentDictionary<string, ConcurrentQueue<TursoPhysicalConnection>> Pools = new(StringComparer.Ordinal);

    /// <summary>Rent a physical connection for <paramref name="config"/>, reusing an idle one if available.</summary>
    public static TursoPhysicalConnection Rent(string key, TursoSyncConfig config, bool forceSync)
    {
        if (Pools.TryGetValue(key, out var queue) && queue.TryDequeue(out var pooled) && pooled.IsUsable())
        {
            return pooled;
        }

        return TursoPhysicalConnection.Create(config, forceSync);
    }

    /// <summary>Return a physical connection to the pool, or dispose it if the pool is full / it's unhealthy.</summary>
    public static void Return(string key, TursoPhysicalConnection physical)
    {
        // A connection that registered UDFs/collations or loaded extensions carries per-connection state
        // that must not leak to the next renter — drop it instead of pooling.
        if (physical.NonPoolable || physical.Raw.HasExtensions || !physical.IsUsable())
        {
            physical.Dispose();
            return;
        }

        var queue = Pools.GetOrAdd(key, _ => new ConcurrentQueue<TursoPhysicalConnection>());
        if (queue.Count >= MaxIdlePerKey)
        {
            physical.Dispose();
            return;
        }

        queue.Enqueue(physical);
    }

    /// <summary>Dispose and drop all pooled connections (test isolation / shutdown).</summary>
    public static void Clear()
    {
        foreach (var queue in Pools.Values)
        {
            while (queue.TryDequeue(out var physical))
            {
                physical.Dispose();
            }
        }

        Pools.Clear();
    }
}

/// <summary>
/// A physical Turso connection (the raw connection plus, for the sync lane, its owning sync database).
/// Pooled and reused across logical <see cref="TursoConnection"/> opens.
/// </summary>
internal sealed class TursoPhysicalConnection : IDisposable
{
    private TursoSyncDatabase? _syncDatabase;

    private TursoPhysicalConnection(TursoRawConnection raw, TursoSyncDatabase? syncDatabase)
    {
        Raw = raw;
        _syncDatabase = syncDatabase;
    }

    public TursoRawConnection Raw { get; }

    /// <summary>When set, this physical connection is dropped rather than pooled on return.</summary>
    public bool NonPoolable { get; set; }

    /// <summary>The owning sync database for the sync lane, or null for a base (local) connection.</summary>
    public TursoSyncDatabase? SyncDatabase => _syncDatabase;

    public static TursoPhysicalConnection Create(TursoSyncConfig config, bool forceSync = false)
    {
        // No remote and not forced → base local lane (no sync engine, no IO pump). Otherwise → sync engine.
        if (string.IsNullOrEmpty(config.RemoteUrl) && !forceSync)
        {
            return new TursoPhysicalConnection(TursoRawConnection.OpenLocal(config), null);
        }

        var db = TursoSyncDatabase.Create(config);
        try
        {
            return new TursoPhysicalConnection(TursoRawConnection.Open(db, config.BusyTimeoutMs), db);
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>Cheap health probe: a trivial query must succeed for the connection to be reused.</summary>
    public bool IsUsable()
    {
        try
        {
            return Equals(Raw.QueryScalar("SELECT 1"), 1L);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        Raw.Dispose();
        _syncDatabase?.Dispose();
        _syncDatabase = null;
    }
}
