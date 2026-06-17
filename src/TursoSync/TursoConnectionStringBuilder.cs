using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Turso;

/// <summary>
/// Strongly-typed connection-string builder for Turso, mirroring the official <c>Turso.Data</c> builder's
/// keyword normalization and adding the sync-engine keys Tweed needs (<c>Remote Url</c>, <c>Auth Token</c>,
/// <c>Namespace</c>, <c>Bootstrap</c>, <c>Busy Timeout</c>).
/// </summary>
public sealed class TursoConnectionStringBuilder : DbConnectionStringBuilder
{
    private static readonly Dictionary<string, string> KeywordMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Data Source"] = "Data Source",
        ["DataSource"] = "Data Source",
        ["Filename"] = "Data Source",
        ["Path"] = "Data Source",
        ["Default Timeout"] = "Default Timeout",
        ["DefaultTimeout"] = "Default Timeout",
        ["Command Timeout"] = "Default Timeout",
        ["CommandTimeout"] = "Default Timeout",
        ["Remote Url"] = "Remote Url",
        ["RemoteUrl"] = "Remote Url",
        ["Auth Token"] = "Auth Token",
        ["AuthToken"] = "Auth Token",
        ["Namespace"] = "Namespace",
        ["Client Name"] = "Client Name",
        ["ClientName"] = "Client Name",
        ["Bootstrap"] = "Bootstrap",
        ["Bootstrap If Empty"] = "Bootstrap",
        ["BootstrapIfEmpty"] = "Bootstrap",
        ["Busy Timeout"] = "Busy Timeout",
        ["BusyTimeout"] = "Busy Timeout",
        ["Long Poll Timeout"] = "Long Poll Timeout",
        ["LongPollTimeout"] = "Long Poll Timeout",
        ["Pooling"] = "Pooling",
        ["Sync"] = "Sync",
        ["Encryption Cipher"] = "Encryption Cipher",
        ["EncryptionCipher"] = "Encryption Cipher",
        ["Encryption Key"] = "Encryption Key",
        ["EncryptionKey"] = "Encryption Key",
    };

    /// <summary>Create an empty builder.</summary>
    public TursoConnectionStringBuilder()
    {
    }

    /// <summary>Create a builder from <paramref name="connectionString"/>.</summary>
    public TursoConnectionStringBuilder(string? connectionString) => ConnectionString = connectionString ?? string.Empty;

    /// <summary>Local database file path.</summary>
    public string DataSource
    {
        get => GetString("Data Source");
        set => this["Data Source"] = value;
    }

    /// <summary>Remote sync URL (libsql/https); empty for local-only.</summary>
    public string RemoteUrl
    {
        get => GetString("Remote Url");
        set => this["Remote Url"] = value;
    }

    /// <summary>Bearer auth token for the remote.</summary>
    public string AuthToken
    {
        get => GetString("Auth Token");
        set => this["Auth Token"] = value;
    }

    /// <summary>Remote namespace prefix.</summary>
    public string Namespace
    {
        get => GetString("Namespace");
        set => this["Namespace"] = value;
    }

    /// <summary>Bootstrap an empty DB from the remote on create.</summary>
    public bool Bootstrap
    {
        get => GetBool("Bootstrap");
        set => this["Bootstrap"] = value;
    }

    /// <summary>Default command timeout (seconds).</summary>
    public int DefaultTimeout
    {
        get => GetInt("Default Timeout", 30);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            this["Default Timeout"] = value;
        }
    }

    /// <summary>Connection busy timeout (ms); 0 = engine default, &lt;0 = disabled.</summary>
    public int BusyTimeout
    {
        get => GetInt("Busy Timeout", 5000);
        set => this["Busy Timeout"] = value;
    }

    /// <summary>Server long-poll timeout (ms) for pulls.</summary>
    public int LongPollTimeout
    {
        get => GetInt("Long Poll Timeout", 0);
        set => this["Long Poll Timeout"] = value;
    }

    /// <summary>Whether physical connections are pooled and reused (default true).</summary>
    public bool Pooling
    {
        get => !ContainsKey("Pooling") || GetBool("Pooling");
        set => this["Pooling"] = value;
    }

    /// <summary>
    /// Force the sync engine even without a remote (local synced database). Normally a remote URL selects
    /// the sync lane and its absence selects the base lane; this overrides that for testing/benchmarking.
    /// </summary>
    public bool Sync
    {
        get => GetBool("Sync");
        set => this["Sync"] = value;
    }

    /// <summary>Local at-rest encryption cipher name (e.g. <c>aes256gcm</c>).</summary>
    public string EncryptionCipher
    {
        get => GetString("Encryption Cipher");
        set => this["Encryption Cipher"] = value;
    }

    /// <summary>Hex-encoded local encryption key.</summary>
    public string EncryptionKey
    {
        get => GetString("Encryption Key");
        set => this["Encryption Key"] = value;
    }

    /// <summary>Set the local at-rest encryption cipher (typed) and key.</summary>
    public void SetEncryption(TursoEncryptionCipher cipher, string hexKey)
    {
        EncryptionCipher = cipher.ToName();
        EncryptionKey = hexKey;
    }

    /// <inheritdoc/>
    [AllowNull]
    public override object this[string keyword]
    {
        get => base[NormalizeKeyword(keyword)];
        set
        {
            var normalized = NormalizeKeyword(keyword);
            if (value is null)
            {
                Remove(normalized);
                return;
            }

            base[normalized] = value;
        }
    }

    /// <inheritdoc/>
    public override bool ContainsKey(string keyword) => base.ContainsKey(NormalizeKeyword(keyword));

    /// <inheritdoc/>
    public override bool Remove(string keyword) => base.Remove(NormalizeKeyword(keyword));

    /// <inheritdoc/>
    public override bool TryGetValue(string keyword, out object value)
    {
        var found = base.TryGetValue(NormalizeKeyword(keyword), out var result);
        value = result!;
        return found;
    }

    internal string? GetOption(string keyword) =>
        TryGetValue(keyword, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    /// <summary>Build the sync config this connection string describes.</summary>
    public TursoSyncConfig ToConfig()
    {
        var path = GetOption("Data Source")
            ?? throw new ArgumentException("Turso connection string requires a 'Data Source'.");

        return new TursoSyncConfig
        {
            Path = path,
            RemoteUrl = NullIfEmpty(GetOption("Remote Url")),
            AuthToken = NullIfEmpty(GetOption("Auth Token")),
            Namespace = NullIfEmpty(GetOption("Namespace")),
            ClientName = NullIfEmpty(GetOption("Client Name")) ?? "tweed-turso",
            BootstrapIfEmpty = GetBool("Bootstrap"),
            LongPollTimeoutMs = GetInt("Long Poll Timeout", 0),
            BusyTimeoutMs = GetInt("Busy Timeout", 5000),
            EncryptionCipher = NullIfEmpty(GetOption("Encryption Cipher")),
            EncryptionKey = NullIfEmpty(GetOption("Encryption Key")),
        };
    }

    private static string NormalizeKeyword(string keyword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);
        if (KeywordMap.TryGetValue(keyword, out var normalized))
        {
            return normalized;
        }

        throw new ArgumentException($"Unsupported keyword: {keyword}", nameof(keyword));
    }

    private string GetString(string keyword) => GetOption(keyword) ?? string.Empty;

    private bool GetBool(string keyword) =>
        TryGetValue(keyword, out var value) && Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private int GetInt(string keyword, int defaultValue) =>
        TryGetValue(keyword, out var value) ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : defaultValue;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
