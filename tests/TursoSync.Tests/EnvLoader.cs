using System.Runtime.CompilerServices;

namespace TursoSync.Tests;

/// <summary>
/// Loads a repo-local <c>.env</c> into the process environment before any test runs, so the live-sync
/// gates (<c>TURSOSYNC_SYNC_SERVER</c>, <c>TURSOSYNC_SYNC_URL</c>/<c>TURSOSYNC_SYNC_TOKEN</c>) can be
/// set without exporting them into the shell. Real environment variables always win — <see cref="DotNetEnv"/>
/// is configured to not clobber values already present — so CI, which exports them, is unaffected.
/// </summary>
internal static class EnvLoader
{
    [ModuleInitializer]
    internal static void Load()
    {
        var envPath = FindEnvFile();
        if (envPath is null)
        {
            return;
        }

        DotNetEnv.Env.Load(envPath, new DotNetEnv.LoadOptions(setEnvVars: true, clobberExistingVars: false));
    }

    /// <summary>Walk up from the test assembly until a <c>.env</c> is found (repo root during dev runs).</summary>
    private static string? FindEnvFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".env");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
