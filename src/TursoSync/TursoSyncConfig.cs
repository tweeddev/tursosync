namespace Turso;

/// <summary>Supported ciphers for local at-rest database encryption (mirrors the native engine).</summary>
public enum TursoEncryptionCipher
{
    /// <summary>AES-128-GCM.</summary>
    Aes128Gcm,

    /// <summary>AES-256-GCM.</summary>
    Aes256Gcm,

    /// <summary>AEGIS-256.</summary>
    Aegis256,

    /// <summary>AEGIS-256X2.</summary>
    Aegis256x2,

    /// <summary>AEGIS-128L.</summary>
    Aegis128l,

    /// <summary>AEGIS-128X2.</summary>
    Aegis128x2,

    /// <summary>AEGIS-128X4.</summary>
    Aegis128x4,
}

/// <summary>Cipher name helpers.</summary>
public static class TursoEncryptionCipherExtensions
{
    /// <summary>The native cipher name (e.g. <c>aes256gcm</c>).</summary>
    public static string ToName(this TursoEncryptionCipher cipher) => cipher switch
    {
        TursoEncryptionCipher.Aes128Gcm => "aes128gcm",
        TursoEncryptionCipher.Aes256Gcm => "aes256gcm",
        TursoEncryptionCipher.Aegis256 => "aegis256",
        TursoEncryptionCipher.Aegis256x2 => "aegis256x2",
        TursoEncryptionCipher.Aegis128l => "aegis128l",
        TursoEncryptionCipher.Aegis128x2 => "aegis128x2",
        TursoEncryptionCipher.Aegis128x4 => "aegis128x4",
        _ => throw new ArgumentOutOfRangeException(nameof(cipher), cipher, null),
    };
}

/// <summary>
/// Configuration for a synced Turso database. With <see cref="RemoteUrl"/> unset the database is purely
/// local (offline): the engine only issues file IO and never HTTP, which is the free/local tier. Set
/// <see cref="RemoteUrl"/> + <see cref="AuthToken"/> to sync against Turso Cloud.
/// </summary>
public sealed record TursoSyncConfig
{
    /// <summary>Path to the main local database file. Auxiliary files derive their names from it.</summary>
    public required string Path { get; init; }

    /// <summary>Remote sync URL (<c>libsql://…</c>, <c>https://…</c>); null/empty for local-only.</summary>
    public string? RemoteUrl { get; init; }

    /// <summary>Remote namespace prefix (sent as the request <c>Host</c>), optional.</summary>
    public string? Namespace { get; init; }

    /// <summary>Bearer token for remote auth; sent as <c>Authorization: Bearer …</c>.</summary>
    public string? AuthToken { get; init; }

    /// <summary>Client-id prefix the engine records in metadata; defaults to <c>tweed-turso</c>.</summary>
    public string ClientName { get; init; } = "tweed-turso";

    /// <summary>Long-poll timeout (ms) the server holds a pull open waiting for changes; 0 = off.</summary>
    public int LongPollTimeoutMs { get; init; }

    /// <summary>
    /// Bootstrap an empty DB from the remote on create. Must be false for local-only use (true requires
    /// the network to be online to connect to a fresh DB).
    /// </summary>
    public bool BootstrapIfEmpty { get; init; }

    /// <summary>Busy timeout (ms) for the connection; 0 uses the engine default, &lt;0 disables it.</summary>
    public int BusyTimeoutMs { get; init; } = 5000;

    /// <summary>
    /// Local at-rest encryption cipher name (e.g. <c>aes256gcm</c>); null disables encryption.
    /// <para><b>Base-engine lane only.</b> At-rest encryption applies to a local-only database (no
    /// <see cref="RemoteUrl"/>, sync disabled). The sync engine does not support it — creating a synced
    /// database with a cipher set throws <see cref="NotSupportedException"/>, because the engine cannot
    /// reopen the encrypted local file. (Turso Cloud server-side encryption is a separate, remote concept.)</para>
    /// </summary>
    public string? EncryptionCipher { get; init; }

    /// <summary>Hex-encoded encryption key. Required when <see cref="EncryptionCipher"/> is set. Base-lane only — see <see cref="EncryptionCipher"/>.</summary>
    public string? EncryptionKey { get; init; }

    /// <summary>True when local at-rest encryption is configured.</summary>
    public bool IsEncrypted => !string.IsNullOrWhiteSpace(EncryptionCipher);
}
