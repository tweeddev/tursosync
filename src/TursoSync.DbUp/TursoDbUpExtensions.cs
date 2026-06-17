using DbUp.Builder;
using DbUp.Turso;

// Extension methods on DbUp's SupportedDatabases live in the global namespace by convention so they are
// always discoverable, exactly like DbUp's own provider extensions.
// ReSharper disable once CheckNamespace
namespace DbUp;

/// <summary>DbUp configuration extensions for Turso.</summary>
public static class TursoDbUpExtensions
{
    /// <summary>Create an upgrader for a Turso database.</summary>
    public static UpgradeEngineBuilder TursoDatabase(this SupportedDatabases supported, string connectionString)
    {
        _ = supported;
        var builder = new UpgradeEngineBuilder();
        builder.Configure(c => c.ConnectionManager = new TursoConnectionManager(connectionString));
        builder.Configure(c => c.Journal = new TursoTableJournal(() => c.ConnectionManager, () => c.Log, "SchemaVersions"));
        builder.Configure(c => c.ScriptExecutor = new TursoScriptExecutor(
            () => c.ConnectionManager, () => c.Log, null, () => c.VariablesEnabled, c.ScriptPreprocessors, () => c.Journal));
        return builder;
    }

    /// <summary>Track executed scripts in a custom Turso journal table.</summary>
    public static UpgradeEngineBuilder JournalToTursoTable(this UpgradeEngineBuilder builder, string table)
    {
        builder.Configure(c => c.Journal = new TursoTableJournal(() => c.ConnectionManager, () => c.Log, table));
        return builder;
    }
}
