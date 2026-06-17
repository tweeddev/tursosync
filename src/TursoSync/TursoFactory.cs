using System.Data.Common;

namespace Turso;

/// <summary>
/// <see cref="DbProviderFactory"/> for Turso, so code using the <c>DbProviderFactories</c> pattern (or
/// anything that resolves a provider factory) can create Turso connections/commands generically. Mirrors
/// the official <c>Turso.Data</c> factory.
/// </summary>
public sealed class TursoFactory : DbProviderFactory
{
    /// <summary>The singleton factory instance.</summary>
    public static readonly TursoFactory Instance = new();

    private TursoFactory()
    {
    }

    /// <inheritdoc/>
    public override DbConnection CreateConnection() => new TursoConnection();

    /// <inheritdoc/>
    public override DbCommand CreateCommand() => new TursoCommand();

    /// <inheritdoc/>
    public override DbParameter CreateParameter() => new TursoParameter();

    /// <inheritdoc/>
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new TursoConnectionStringBuilder();
}
