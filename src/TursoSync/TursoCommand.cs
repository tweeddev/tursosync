using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Turso;

/// <summary>
/// A <see cref="DbCommand"/> over a Turso connection. Mirrors the official <c>Turso.Data</c> command:
/// parameters bind by name (when named) or by position, with full binding validation; only
/// <see cref="CommandType.Text"/> is supported.
/// </summary>
public sealed class TursoCommand : DbCommand
{
    private readonly TursoParameterCollection _parameters = new();
    private TursoConnection? _connection;
    private TursoTransaction? _transaction;
    private TursoRawStatement? _statement;
    private int _commandTimeout = 30;

    /// <summary>Create an unbound command.</summary>
    public TursoCommand()
    {
    }

    /// <summary>Create a command on <paramref name="connection"/>.</summary>
    public TursoCommand(TursoConnection connection, TursoTransaction? transaction = null)
    {
        _connection = connection;
        _transaction = transaction;
        _commandTimeout = connection.DefaultTimeout;
    }

    /// <summary>Create a command on <paramref name="connection"/> with SQL text.</summary>
    public TursoCommand(TursoConnection connection, string commandText)
    {
        _connection = connection;
        _commandTimeout = connection.DefaultTimeout;
        CommandText = commandText;
    }

    /// <inheritdoc/>
    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    /// <inheritdoc/>
    public override int CommandTimeout
    {
        get => _commandTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _commandTimeout = value;
        }
    }

    /// <inheritdoc/>
    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
            {
                throw new NotSupportedException("TursoCommand only supports CommandType.Text.");
            }
        }
    }

    /// <inheritdoc/>
    public override bool DesignTimeVisible { get; set; }

    /// <inheritdoc/>
    public override UpdateRowSource UpdatedRowSource { get; set; }

    /// <summary>The connection this command runs on.</summary>
    public new TursoConnection? Connection
    {
        get => _connection;
        set => _connection = value;
    }

    /// <summary>The strongly-typed parameter collection.</summary>
    public new TursoParameterCollection Parameters => _parameters;

    /// <inheritdoc/>
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value switch
        {
            null => null,
            TursoConnection c => c,
            _ => throw new ArgumentException("Connection must be a TursoConnection.", nameof(value)),
        };
    }

    /// <inheritdoc/>
    protected override DbParameterCollection DbParameterCollection => _parameters;

    /// <inheritdoc/>
    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = value switch
        {
            null => null,
            TursoTransaction t => t,
            _ => throw new ArgumentException("Transaction must be a TursoTransaction.", nameof(value)),
        };
    }

    /// <inheritdoc/>
    public override void Cancel()
    {
    }

    /// <inheritdoc/>
    protected override DbParameter CreateDbParameter() => new TursoParameter();

    /// <inheritdoc/>
    public override int ExecuteNonQuery()
    {
        using var reader = Execute(CommandBehavior.Default);
        while (reader.Read())
        {
            // drain
        }

        return reader.RecordsAffected;
    }

    /// <inheritdoc/>
    public override object? ExecuteScalar()
    {
        using var reader = Execute(CommandBehavior.Default);
        return reader.Read() ? reader.GetValue(0) : null;
    }

    /// <inheritdoc/>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => Execute(behavior);

    /// <inheritdoc/>
    public override void Prepare()
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("Connection must be set before preparing a command.");
        }

        if (string.IsNullOrWhiteSpace(CommandText))
        {
            throw new InvalidOperationException("CommandText must be set before preparing a command.");
        }

        if (_transaction is { IsCompleted: true })
        {
            throw new InvalidOperationException("The transaction associated with this command has completed.");
        }

        TursoRawStatement? prepared = null;
        try
        {
            prepared = _connection.Raw.Prepare(CommandText);
            var parameterCount = prepared.ParameterCount;
            var bound = new bool[parameterCount + 1];

            for (var i = 0; i < _parameters.Count; i++)
            {
                var parameter = (TursoParameter)_parameters[i];
                int position;
                if (!string.IsNullOrEmpty(parameter.ParameterName))
                {
                    position = ResolveNamedPosition(prepared, parameter.ParameterName);
                    if (position == 0)
                    {
                        throw new InvalidOperationException($"Parameter {parameter.ParameterName} was not found in the SQL statement.");
                    }
                }
                else
                {
                    position = i + 1;
                    if (position > parameterCount)
                    {
                        throw new InvalidOperationException($"Parameter at position {position} was not found in the SQL statement.");
                    }
                }

                prepared.Bind(position, parameter.ToBindValue());
                bound[position] = true;
            }

            for (var i = 1; i <= parameterCount; i++)
            {
                if (!bound[i])
                {
                    var name = prepared.ParameterName(i);
                    throw new InvalidOperationException(
                        string.IsNullOrEmpty(name)
                            ? $"Missing value for parameter at position {i}."
                            : $"Missing value for parameter {name}.");
                }
            }

            _statement?.Dispose();
            _statement = prepared;
            prepared = null;
        }
        finally
        {
            prepared?.Dispose();
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statement?.Dispose();
            _statement = null;
        }

        base.Dispose(disposing);
    }

    // Dapper supplies parameter names without a sigil ("Id"), but the engine's named-parameter lookup
    // wants the full token ("@Id"/":id"/"$id"). Try the name as given, then each common sigil.
    private static int ResolveNamedPosition(TursoRawStatement statement, string name)
    {
        var position = statement.NamedPosition(name);
        if (position != 0 || (name.Length > 0 && name[0] is '@' or ':' or '$'))
        {
            return position;
        }

        foreach (var sigil in (ReadOnlySpan<char>)['@', ':', '$'])
        {
            position = statement.NamedPosition(sigil + name);
            if (position != 0)
            {
                return position;
            }
        }

        return 0;
    }

    private TursoDataReader Execute(CommandBehavior behavior)
    {
        if (_connection is null)
        {
            throw new InvalidOperationException("Connection must be set before executing a command.");
        }

        Prepare();
        var statement = _statement ?? throw new InvalidOperationException("Command was not prepared.");
        _statement = null; // ownership transfers to the reader
        return new TursoDataReader(this, statement, behavior);
    }
}
