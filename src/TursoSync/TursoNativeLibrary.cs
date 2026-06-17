using System.Runtime.InteropServices;

namespace Turso;

/// <summary>
/// Resolves the <c>turso_sync_sdk_kit</c> native library. Until we pack per-RID natives into the NuGet,
/// the dylib lives in the vendored Rust target dir; this resolver finds it via the
/// <c>TURSOSYNC_NATIVE_DIR</c> env var or a set of candidate paths, falling back to the default OS
/// search (so packaged runtimes/ assets still work).
/// </summary>
public static class TursoNativeLibrary
{
    private static int _registered;

    /// <summary>Returns true if the native library could be located and loaded.</summary>
    public static bool IsAvailable()
    {
        EnsureResolver();
        if (TryLoadFromCandidates(out var handle))
        {
            NativeLibrary.Free(handle);
            return true;
        }

        return NativeLibrary.TryLoad(TursoNative.Lib, typeof(TursoNativeLibrary).Assembly, null, out handle)
            && Free(handle);
    }

    /// <summary>Register the native-library resolver (idempotent). Called by the Turso entry points.</summary>
    public static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(TursoNativeLibrary).Assembly, (name, _, _) =>
            name == TursoNative.Lib && TryLoadFromCandidates(out var handle) ? handle : IntPtr.Zero);
    }

    private static bool TryLoadFromCandidates(out IntPtr handle)
    {
        foreach (var dir in CandidateDirs())
        {
            var path = Path.Combine(dir, FileName());
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out handle))
            {
                return true;
            }
        }

        handle = IntPtr.Zero;
        return false;
    }

    private static IEnumerable<string> CandidateDirs()
    {
        var env = Environment.GetEnvironmentVariable("TURSOSYNC_NATIVE_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env;
        }

        yield return AppContext.BaseDirectory;

        // Walk up from the build output to find the vendored Rust target dir (dev convenience).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var debug = Path.Combine(dir.FullName, "reference", "turso-main", "target", "debug");
            if (Directory.Exists(debug))
            {
                yield return debug;
            }

            dir = dir.Parent;
        }
    }

    private static string FileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return TursoNative.Lib + ".dll";
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? "lib" + TursoNative.Lib + ".dylib"
            : "lib" + TursoNative.Lib + ".so";
    }

    private static bool Free(IntPtr handle)
    {
        NativeLibrary.Free(handle);
        return true;
    }
}
