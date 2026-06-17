namespace Turso;

/// <summary>Public projection of Turso sync-engine statistics.</summary>
/// <param name="CdcOperations">Local operations written since the last pull.</param>
/// <param name="MainWalSize">Size of the main WAL file.</param>
/// <param name="RevertWalSize">Size of the revert WAL file.</param>
/// <param name="LastPullUnixTime">Unix time of the last successful pull.</param>
/// <param name="LastPushUnixTime">Unix time of the last successful push.</param>
/// <param name="NetworkSentBytes">Total bytes sent over the network.</param>
/// <param name="NetworkReceivedBytes">Total bytes received over the network.</param>
/// <param name="Revision">Opaque server revision string.</param>
public sealed record TursoStats(
    long CdcOperations,
    long MainWalSize,
    long RevertWalSize,
    long LastPullUnixTime,
    long LastPushUnixTime,
    long NetworkSentBytes,
    long NetworkReceivedBytes,
    string Revision);

/// <summary>An error surfaced from the Turso native sync/SQL engine.</summary>
public sealed class TursoException : Exception
{
    /// <summary>Create a <see cref="TursoException"/> with a message.</summary>
    public TursoException(string message) : base(message)
    {
    }

    /// <summary>Create a <see cref="TursoException"/> with a message and inner exception.</summary>
    public TursoException(string message, Exception inner) : base(message, inner)
    {
    }
}
