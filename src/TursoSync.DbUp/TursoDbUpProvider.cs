using DbUp.Builder;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using DbUp.Support;
using Turso;

namespace DbUp.Turso;

/// <summary>
/// A first-class DbUp provider for Turso, mirroring DbUp's official SQLite provider (same dialect) but
/// running every script through the <see cref="TursoConnection"/> — so migrations execute against the
/// synced engine instead of a separate Microsoft.Data.Sqlite connection fighting it over the file.
/// </summary>
public sealed class TursoConnectionManager : DatabaseConnectionManager
{
    /// <summary>Create a connection manager for <paramref name="connectionString"/>.</summary>
    public TursoConnectionManager(string connectionString) : base(_ => new TursoConnection(connectionString))
    {
    }

    /// <summary>
    /// Split a script into individual statements. Unlike DbUp's SQLite provider (which relies on the SQLite
    /// driver executing multiple statements in one command), Turso's <c>prepare_single</c> handles exactly
    /// one statement, so we split on top-level <c>;</c> while respecting string/identifier literals and
    /// comments.
    /// </summary>
    public override IEnumerable<string> SplitScriptIntoCommands(string scriptContents)
    {
        var statements = new List<string>();
        var start = 0;
        for (var i = 0; i < scriptContents.Length; i++)
        {
            var c = scriptContents[i];
            switch (c)
            {
                case '\'':
                case '"':
                case '`':
                    i = SkipQuoted(scriptContents, i, c);
                    break;
                case '-' when Peek(scriptContents, i + 1) == '-':
                    i = SkipLineComment(scriptContents, i);
                    break;
                case '/' when Peek(scriptContents, i + 1) == '*':
                    i = SkipBlockComment(scriptContents, i);
                    break;
                case ';':
                    AddIfNotBlank(statements, scriptContents[start..i]);
                    start = i + 1;
                    break;
            }
        }

        AddIfNotBlank(statements, scriptContents[start..]);
        return statements;
    }

    private static char Peek(string s, int index) => index < s.Length ? s[index] : '\0';

    private static int SkipQuoted(string s, int i, char quote)
    {
        for (var j = i + 1; j < s.Length; j++)
        {
            if (s[j] != quote)
            {
                continue;
            }

            if (Peek(s, j + 1) == quote)
            {
                j++; // doubled quote is an escaped literal quote
                continue;
            }

            return j;
        }

        return s.Length - 1;
    }

    private static int SkipLineComment(string s, int i)
    {
        var end = s.IndexOf('\n', i);
        return end < 0 ? s.Length - 1 : end;
    }

    private static int SkipBlockComment(string s, int i)
    {
        var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return end < 0 ? s.Length - 1 : end + 1;
    }

    private static void AddIfNotBlank(List<string> statements, string candidate)
    {
        var trimmed = candidate.Trim();
        if (trimmed.Length > 0)
        {
            statements.Add(trimmed);
        }
    }
}

/// <summary>SQL object parser using SQLite-style <c>[ ]</c> identifier quoting.</summary>
public sealed class TursoObjectParser : SqlObjectParser
{
    /// <summary>Create the parser.</summary>
    public TursoObjectParser() : base("[", "]")
    {
    }
}

/// <summary>Tracks executed scripts in a Turso table (SQLite-dialect DDL).</summary>
public sealed class TursoTableJournal : TableJournal
{
    /// <summary>Create the journal for <paramref name="table"/>.</summary>
    public TursoTableJournal(Func<IConnectionManager> connectionManager, Func<IUpgradeLog> logger, string table)
        : base(connectionManager, logger, new TursoObjectParser(), null, table)
    {
    }

    /// <inheritdoc/>
    protected override string GetInsertJournalEntrySql(string scriptName, string applied) =>
        $"insert into {FqSchemaTableName} (ScriptName, Applied) values ({scriptName}, {applied})";

    /// <inheritdoc/>
    protected override string GetJournalEntriesSql() =>
        $"select [ScriptName] from {FqSchemaTableName} order by [ScriptName]";

    /// <inheritdoc/>
    protected override string CreateSchemaTableSql(string quotedPrimaryKeyName) =>
        $@"CREATE TABLE {FqSchemaTableName} (
    SchemaVersionID INTEGER CONSTRAINT {quotedPrimaryKeyName} PRIMARY KEY AUTOINCREMENT NOT NULL,
    ScriptName TEXT NOT NULL,
    Applied DATETIME NOT NULL
)";

    /// <inheritdoc/>
    protected override string DoesTableExistSql() =>
        $"SELECT count(name) FROM sqlite_master WHERE type = 'table' AND name = '{UnquotedSchemaTableName}'";
}

/// <summary>Executes scripts against a Turso database, surfacing <see cref="TursoException"/> with context.</summary>
public sealed class TursoScriptExecutor : ScriptExecutor
{
    /// <summary>Create the executor.</summary>
    public TursoScriptExecutor(
        Func<IConnectionManager> connectionManagerFactory,
        Func<IUpgradeLog> log,
        string? schema,
        Func<bool> variablesEnabled,
        IEnumerable<IScriptPreprocessor> scriptPreprocessors,
        Func<IJournal> journalFactory)
        : base(connectionManagerFactory, new TursoObjectParser(), log, schema, variablesEnabled, scriptPreprocessors, journalFactory)
    {
    }

    /// <inheritdoc/>
    protected override string GetVerifySchemaSql(string schema) => throw new NotSupportedException();

    /// <inheritdoc/>
    protected override void ExecuteCommandsWithinExceptionHandler(int index, SqlScript script, Action executeCommand)
    {
        try
        {
            executeCommand();
        }
        catch (TursoException exception)
        {
            Log().LogInformation("Turso exception has occurred in script: '{0}'", script.Name);
            Log().LogError("Script block number: {0}; Message: {1}", index, exception.Message);
            Log().LogError(exception.ToString());
            throw;
        }
    }
}
