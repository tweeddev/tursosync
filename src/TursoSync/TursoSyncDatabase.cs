using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace Turso;

/// <summary>
/// A synced Turso database and the host-driven IO loop that powers it. Ported from the Go binding's
/// <c>driver_sync.go</c>: every async engine operation is driven by <see cref="DriveOpUntilDone"/>, which
/// resumes the operation and, whenever the engine asks for IO, executes the requested HTTP / file work and
/// feeds the result back. Tweed is the <i>host</i> — the engine never does network or disk itself.
/// </summary>
public sealed class TursoSyncDatabase : IDisposable
{
    private readonly object _gate = new();
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _authToken;
    private readonly string? _namespace;
    private IntPtr _db;
    private bool _disposed;

    static TursoSyncDatabase() => TursoNativeLibrary.EnsureResolver();

    private TursoSyncDatabase(IntPtr db, string baseUrl, string? authToken, string? ns, HttpClient http)
    {
        _db = db;
        _baseUrl = baseUrl;
        _authToken = authToken;
        _namespace = ns;
        _http = http;
    }

    /// <summary>
    /// Create (or open) a synced database per <paramref name="config"/>, driving the create operation to
    /// completion. Bootstraps from the remote if configured; otherwise opens purely locally.
    /// </summary>
    public static TursoSyncDatabase Create(TursoSyncConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var baseUrl = NormalizeUrl(config.RemoteUrl ?? "");
        var http = new HttpClient();

        // AsyncIo MUST be true for the sync engine so it hands IO to us instead of doing it itself.
        var (dbConfig, dbConfigPtrs) = TursoConfigMarshal.BuildDatabaseConfig(config, asyncIo: 1);
        var strings = new MarshaledStrings();
        IntPtr db;
        try
        {
            var syncConfig = new TursoSyncDatabaseConfig
            {
                Path = strings.Utf8(config.Path),
                RemoteUrl = strings.Utf8OrNull(baseUrl),
                ClientName = strings.Utf8(config.ClientName),
                LongPollTimeoutMs = config.LongPollTimeoutMs,
                BootstrapIfEmpty = config.BootstrapIfEmpty,
            };

            var status = TursoNative.SyncDatabaseNew(ref dbConfig, ref syncConfig, out db, out var errorPtr);
            Check(status, errorPtr, "sync_database_new");
        }
        finally
        {
            strings.Dispose();
            TursoConfigMarshal.Free(dbConfigPtrs);
        }

        var self = new TursoSyncDatabase(db, baseUrl.TrimEnd('/'), Trim(config.AuthToken), config.Namespace, http);
        try
        {
            var op = self.Begin(TursoNative.SyncDatabaseCreate, "sync_database_create");
            self.DriveAndDeinit(op, "sync_database_create");
            return self;
        }
        catch
        {
            self.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Open a new connection to the synced database, driving the connect operation. The returned handle is
    /// a raw <c>turso_connection_t</c>; the caller owns it and must close/deinit it.
    /// </summary>
    public IntPtr Connect(int busyTimeoutMs)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var op = Begin(TursoNative.SyncDatabaseConnect, "sync_database_connect");
            try
            {
                var kind = DriveOpUntilDone(op, "sync_database_connect");
                if (kind != TursoSyncResultKind.Connection)
                {
                    throw new TursoException($"sync_database_connect: unexpected result kind {kind}");
                }

                var status = TursoNative.SyncOperationExtractConnection(op, out var connection);
                Check(status, IntPtr.Zero, "extract_connection");

                var timeout = busyTimeoutMs == 0 ? 5000 : busyTimeoutMs;
                if (timeout > 0)
                {
                    TursoNative.ConnectionSetBusyTimeoutMs(connection, timeout);
                }

                return connection;
            }
            finally
            {
                TursoNative.SyncOperationDeinit(op);
            }
        }
    }

    /// <summary>Push local changes to the remote. No-op effect when local-only.</summary>
    public void Push()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            DriveAndDeinit(Begin(TursoNative.SyncDatabasePushChanges, "push_changes"), "push_changes");
        }
    }

    /// <summary>
    /// Pull remote changes and apply them locally. Returns true if any changes were applied.
    /// </summary>
    public bool Pull()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var waitOp = Begin(TursoNative.SyncDatabaseWaitChanges, "wait_changes");
            IntPtr changes;
            try
            {
                var kind = DriveOpUntilDone(waitOp, "wait_changes");
                if (kind != TursoSyncResultKind.Changes)
                {
                    throw new TursoException($"wait_changes: unexpected result kind {kind}");
                }

                var status = TursoNative.SyncOperationExtractChanges(waitOp, out changes);
                Check(status, IntPtr.Zero, "extract_changes");
            }
            finally
            {
                TursoNative.SyncOperationDeinit(waitOp);
            }

            if (changes == IntPtr.Zero)
            {
                return false;
            }

            // apply_changes consumes the changes handle (even on failure) — do not deinit it ourselves.
            var status2 = TursoNative.SyncDatabaseApplyChanges(_db, changes, out var applyOp, out var errorPtr);
            Check(status2, errorPtr, "apply_changes");
            DriveAndDeinit(applyOp, "apply_changes");
            return true;
        }
    }

    /// <summary>Checkpoint the local WAL.</summary>
    public void Checkpoint()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            DriveAndDeinit(Begin(TursoNative.SyncDatabaseCheckpoint, "checkpoint"), "checkpoint");
        }
    }

    /// <summary>Collect sync stats.</summary>
    public TursoStats Stats()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var op = Begin(TursoNative.SyncDatabaseStats, "stats");
            try
            {
                var kind = DriveOpUntilDone(op, "stats");
                if (kind != TursoSyncResultKind.Stats)
                {
                    throw new TursoException($"stats: unexpected result kind {kind}");
                }

                var status = TursoNative.SyncOperationExtractStats(op, out var stats);
                Check(status, IntPtr.Zero, "extract_stats");
                return new TursoStats(
                    stats.CdcOperations,
                    stats.MainWalSize,
                    stats.RevertWalSize,
                    stats.LastPullUnixTime,
                    stats.LastPushUnixTime,
                    stats.NetworkSentBytes,
                    stats.NetworkReceivedBytes,
                    SliceToString(stats.Revision));
            }
            finally
            {
                TursoNative.SyncOperationDeinit(op);
            }
        }
    }

    /// <summary>
    /// Process at most one queued IO item, then step engine callbacks. Called once per statement-step
    /// iteration by the ADO.NET layer so queries on a synced DB also advance sync IO (Go's <c>extra</c>).
    /// </summary>
    public void ProcessOneIo()
    {
        ThrowIfDisposed();
        var status = TursoNative.SyncIoTakeItem(_db, out var item, out var errorPtr);
        Check(status, errorPtr, "io_take_item");
        if (item != IntPtr.Zero)
        {
            try
            {
                HandleIoItem(item);
            }
            finally
            {
                TursoNative.SyncIoItemDeinit(item);
            }
        }

        StepCallbacks();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_db != IntPtr.Zero)
        {
            TursoNative.SyncDatabaseDeinit(_db);
            _db = IntPtr.Zero;
        }

        _http.Dispose();
    }

    // ---- the resume / IO loop --------------------------------------------------------------------

    private delegate TursoStatus BeginOp(IntPtr db, out IntPtr operation, out IntPtr errorPtr);

    private IntPtr Begin(BeginOp begin, string ctx)
    {
        var status = begin(_db, out var op, out var errorPtr);
        Check(status, errorPtr, ctx);
        return op;
    }

    private void DriveAndDeinit(IntPtr op, string ctx)
    {
        try
        {
            DriveOpUntilDone(op, ctx);
        }
        finally
        {
            TursoNative.SyncOperationDeinit(op);
        }
    }

    private TursoSyncResultKind DriveOpUntilDone(IntPtr op, string ctx)
    {
        while (true)
        {
            var code = TursoNative.SyncOperationResume(op, out var errorPtr);
            switch (code)
            {
                case TursoStatus.Done:
                    if (errorPtr != IntPtr.Zero)
                    {
                        TursoNative.FreeString(errorPtr);
                    }

                    return TursoNative.SyncOperationResultKind(op);
                case TursoStatus.Io:
                    if (errorPtr != IntPtr.Zero)
                    {
                        TursoNative.FreeString(errorPtr);
                    }

                    ProcessIoQueue();
                    continue;
                case TursoStatus.Ok:
                    if (errorPtr != IntPtr.Zero)
                    {
                        TursoNative.FreeString(errorPtr);
                    }

                    continue;
                default:
                    Check(code, errorPtr, $"{ctx} resume");
                    continue; // unreachable; Check throws on error codes
            }
        }
    }

    private void ProcessIoQueue()
    {
        while (true)
        {
            var status = TursoNative.SyncIoTakeItem(_db, out var item, out var errorPtr);
            Check(status, errorPtr, "io_take_item");
            if (item == IntPtr.Zero)
            {
                break;
            }

            try
            {
                HandleIoItem(item);
            }
            finally
            {
                TursoNative.SyncIoItemDeinit(item);
            }
        }

        StepCallbacks();
    }

    private void StepCallbacks()
    {
        var status = TursoNative.SyncIoStepCallbacks(_db, out var errorPtr);
        Check(status, errorPtr, "io_step_callbacks");
    }

    private void HandleIoItem(IntPtr item)
    {
        switch (TursoNative.SyncIoRequestKind(item))
        {
            case TursoSyncIoKind.Http:
                HandleHttp(item);
                break;
            case TursoSyncIoKind.FullRead:
                HandleFullRead(item);
                break;
            case TursoSyncIoKind.FullWrite:
                HandleFullWrite(item);
                break;
            default:
                TursoNative.SyncIoDone(item);
                break;
        }
    }

    private void HandleHttp(IntPtr item)
    {
        try
        {
            var status = TursoNative.SyncIoRequestHttp(item, out var req);
            Check(status, IntPtr.Zero, "io_request_http");

            var path = SliceToString(req.Path);
            using var message = new HttpRequestMessage(new HttpMethod(SliceToString(req.Method)), JoinUrl(_baseUrl, path));

            var body = SliceToBytes(req.Body);
            if (body is not null)
            {
                message.Content = new ByteArrayContent(body);
            }

            for (var i = 0; i < req.Headers; i++)
            {
                var hs = TursoNative.SyncIoRequestHttpHeader(item, (nuint)i, out var header);
                Check(hs, IntPtr.Zero, "io_request_http_header");
                var key = SliceToString(header.Key);
                if (key.Length == 0)
                {
                    continue;
                }

                var value = SliceToString(header.Value);
                if (!message.Headers.TryAddWithoutValidation(key, value))
                {
                    message.Content?.Headers.TryAddWithoutValidation(key, value);
                }
            }

            if (!string.IsNullOrEmpty(_authToken))
            {
                message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _authToken);
            }

            if (!message.Headers.Contains("User-Agent"))
            {
                message.Headers.TryAddWithoutValidation("User-Agent", "tweed-turso");
            }

            message.Headers.Host = BuildHost(_baseUrl, _namespace);

            using var response = _http.Send(message, HttpCompletionOption.ResponseHeadersRead);
            TursoNative.SyncIoStatus(item, (int)response.StatusCode);

            using var stream = response.Content.ReadAsStream();
            PumpStream(item, stream);
            TursoNative.SyncIoDone(item);
        }
        catch (Exception ex)
        {
            Poison(item, ex.Message);
            TursoNative.SyncIoDone(item);
        }
    }

    private void HandleFullRead(IntPtr item)
    {
        try
        {
            var status = TursoNative.SyncIoRequestFullRead(item, out var req);
            Check(status, IntPtr.Zero, "io_request_full_read");
            var path = SliceToString(req.Path);

            if (!File.Exists(path))
            {
                // A missing file must be treated as an empty file (per the ABI contract).
                TursoNative.SyncIoDone(item);
                return;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            PumpStream(item, stream);
            TursoNative.SyncIoDone(item);
        }
        catch (Exception ex)
        {
            Poison(item, ex.Message);
            TursoNative.SyncIoDone(item);
        }
    }

    private static void HandleFullWrite(IntPtr item)
    {
        try
        {
            var status = TursoNative.SyncIoRequestFullWrite(item, out var req);
            Check(status, IntPtr.Zero, "io_request_full_write");
            var path = SliceToString(req.Path);
            var content = SliceToBytes(req.Content) ?? [];

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Atomic-ish: write to a temp file then rename over the destination.
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, content);
            File.Move(tmp, path, overwrite: true);
            TursoNative.SyncIoDone(item);
        }
        catch (Exception ex)
        {
            Poison(item, ex.Message);
            TursoNative.SyncIoDone(item);
        }
    }

    private static void PumpStream(IntPtr item, Stream stream)
    {
        var buffer = new byte[64 * 1024];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var ptr = handle.AddrOfPinnedObject();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var slice = new TursoSlice { Ptr = ptr, Len = (nuint)read };
                TursoNative.SyncIoPushBuffer(item, ref slice);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private static void Poison(IntPtr item, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length == 0)
        {
            var empty = default(TursoSlice);
            TursoNative.SyncIoPoison(item, ref empty);
            return;
        }

        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var slice = new TursoSlice { Ptr = handle.AddrOfPinnedObject(), Len = (nuint)bytes.Length };
            TursoNative.SyncIoPoison(item, ref slice);
        }
        finally
        {
            handle.Free();
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    internal static void Check(TursoStatus status, IntPtr errorPtr, string ctx)
    {
        if (errorPtr != IntPtr.Zero)
        {
            var message = Marshal.PtrToStringUTF8(errorPtr);
            TursoNative.FreeString(errorPtr);
            throw new TursoException($"{ctx}: {message}");
        }

        if (status is TursoStatus.Ok or TursoStatus.Done or TursoStatus.Row)
        {
            return;
        }

        throw new TursoException($"{ctx}: status {status}");
    }

    private static byte[]? SliceToBytes(TursoSlice slice)
    {
        if (slice.Ptr == IntPtr.Zero || slice.Len == 0)
        {
            return null;
        }

        var n = checked((int)slice.Len);
        var dst = new byte[n];
        Marshal.Copy(slice.Ptr, dst, 0, n);
        return dst;
    }

    private static string SliceToString(TursoSlice slice)
    {
        var bytes = SliceToBytes(slice);
        return bytes is null ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    private static string NormalizeUrl(string url) =>
        url.StartsWith("libsql://", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url["libsql://".Length..]
            : url;

    private static string JoinUrl(string baseUrl, string path)
    {
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return baseUrl.TrimEnd('/') + path;
    }

    private static string? BuildHost(string baseUrl, string? ns)
    {
        if (string.IsNullOrEmpty(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return string.IsNullOrEmpty(ns) ? uri.Host : ns + "." + uri.Host;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Pins and owns UTF-8 C strings for the duration of a single native call.</summary>
    private sealed class MarshaledStrings : IDisposable
    {
        private readonly List<IntPtr> _ptrs = [];

        public IntPtr Utf8(string value)
        {
            var ptr = Marshal.StringToCoTaskMemUTF8(value);
            _ptrs.Add(ptr);
            return ptr;
        }

        public IntPtr Utf8OrNull(string? value) => string.IsNullOrEmpty(value) ? IntPtr.Zero : Utf8(value);

        public void Dispose()
        {
            foreach (var ptr in _ptrs)
            {
                Marshal.FreeCoTaskMem(ptr);
            }

            _ptrs.Clear();
        }
    }
}
