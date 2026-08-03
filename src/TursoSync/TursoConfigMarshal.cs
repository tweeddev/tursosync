using System.Runtime.InteropServices;

namespace Turso.Sync;

/// <summary>
/// Marshals a <see cref="TursoSyncConfig"/> into the native <see cref="TursoDatabaseConfig"/>, including
/// local at-rest encryption (cipher + hex key) and the engine's comma-separated experimental-feature
/// list (<c>encryption</c> and/or <c>index_method</c>). Returns the allocated UTF-8 string pointers so
/// the caller can free them after the native call returns.
/// </summary>
internal static class TursoConfigMarshal
{
    public static (TursoDatabaseConfig Config, IntPtr[] ToFree) BuildDatabaseConfig(TursoSyncConfig config, ulong asyncIo)
    {
        var ptrs = new List<IntPtr>();

        IntPtr Utf8(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return IntPtr.Zero;
            }

            var ptr = Marshal.StringToCoTaskMemUTF8(value);
            ptrs.Add(ptr);
            return ptr;
        }

        var dbConfig = new TursoDatabaseConfig
        {
            AsyncIo = asyncIo,
            Path = Utf8(config.Path),
        };

        // The native takes one comma-separated experimental-feature string; enabled features are combined
        // so encryption and FTS (index_method) can be on together — which is how an encrypted DB gets an
        // encrypted-at-rest full-text index.
        var features = new List<string>(2);

        if (config.IsEncrypted)
        {
            if (string.IsNullOrWhiteSpace(config.EncryptionKey))
            {
                throw new ArgumentException("EncryptionKey is required when EncryptionCipher is set.");
            }

            features.Add("encryption");
            dbConfig.EncryptionCipher = Utf8(config.EncryptionCipher);
            dbConfig.EncryptionHexKey = Utf8(config.EncryptionKey);
        }

        if (config.ExperimentalIndexMethod)
        {
            features.Add("index_method");
        }

        if (features.Count > 0)
        {
            dbConfig.ExperimentalFeatures = Utf8(string.Join(',', features));
        }

        return (dbConfig, ptrs.ToArray());
    }

    public static void Free(IntPtr[] pointers)
    {
        foreach (var ptr in pointers)
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }
    }
}
