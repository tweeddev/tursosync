using System.Runtime.InteropServices;

namespace Turso;

/// <summary>
/// Marshals a <see cref="TursoSyncConfig"/> into the native <see cref="TursoDatabaseConfig"/>, including
/// local at-rest encryption (cipher + hex key + the <c>encryption</c> experimental feature). Returns the
/// allocated UTF-8 string pointers so the caller can free them after the native call returns.
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

        if (config.IsEncrypted)
        {
            if (string.IsNullOrWhiteSpace(config.EncryptionKey))
            {
                throw new ArgumentException("EncryptionKey is required when EncryptionCipher is set.");
            }

            dbConfig.ExperimentalFeatures = Utf8("encryption");
            dbConfig.EncryptionCipher = Utf8(config.EncryptionCipher);
            dbConfig.EncryptionHexKey = Utf8(config.EncryptionKey);
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
