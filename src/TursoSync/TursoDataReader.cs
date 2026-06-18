using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Turso;

/// <summary>
/// Forward-only reader over a <see cref="TursoRawStatement"/>. Mirrors the official <c>Turso.Data</c>
/// reader surface and behavior, but every <see cref="Read"/> advances the statement through the sync IO
/// pump (so reads on a synced database also drive sync IO).
/// </summary>
public sealed class TursoDataReader : DbDataReader
{
    private readonly TursoCommand _command;
    private readonly TursoRawStatement _statement;
    private readonly CommandBehavior _behavior;
    private bool _closed;

    internal TursoDataReader(TursoCommand command, TursoRawStatement statement, CommandBehavior behavior)
    {
        _command = command;
        _statement = statement;
        _behavior = behavior;
    }

    /// <inheritdoc/>
    public override int FieldCount => _statement.ColumnCount;

    /// <inheritdoc/>
    public override bool HasRows => true;

    /// <inheritdoc/>
    public override bool IsClosed => _closed;

    /// <inheritdoc/>
    public override int RecordsAffected => _statement.RowsAffected;

    /// <inheritdoc/>
    public override int Depth => 0;

    /// <inheritdoc/>
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc/>
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <inheritdoc/>
    public override bool Read()
    {
        EnsureOpen();
        return _statement.Step();
    }

    /// <inheritdoc/>
    public override bool NextResult()
    {
        EnsureOpen();
        while (_statement.Step())
        {
            // drain remaining rows of the current statement
        }

        return false;
    }

    /// <inheritdoc/>
    public override string GetName(int ordinal) => _statement.ColumnName(ordinal);

    /// <inheritdoc/>
    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < FieldCount; i++)
        {
            if (string.Equals(_statement.ColumnName(i), name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new IndexOutOfRangeException($"column {name} not found");
    }

    /// <inheritdoc/>
    public override object GetValue(int ordinal) => _statement.GetValue(ordinal) ?? DBNull.Value;

    /// <inheritdoc/>
    public override int GetValues(object[] values)
    {
        var i = 0;
        for (; i < FieldCount; i++)
        {
            values[i] = GetValue(i);
        }

        return i;
    }

    /// <inheritdoc/>
    public override bool IsDBNull(int ordinal) =>
        _statement.ValueKind(ordinal) is TursoValueKind.Null or TursoValueKind.Unknown;

    /// <inheritdoc/>
    public override Type GetFieldType(int ordinal)
    {
        // Prefer the declared column type so mapping works even before/without a current row (e.g. empty
        // result sets); fall back to the current value kind for expressions with no declared type.
        var declared = _statement.Decltype(ordinal);
        return ClrTypeFromDeclared(declared, _statement.ValueKind(ordinal));
    }

    private static Type ClrTypeFromDeclared(string declared, TursoValueKind fallback)
    {
        var t = declared.ToUpperInvariant();
        if (t.Length == 0)
        {
            return fallback switch
            {
                TursoValueKind.Integer => typeof(long),
                TursoValueKind.Real => typeof(double),
                TursoValueKind.Text => typeof(string),
                TursoValueKind.Blob => typeof(byte[]),
                _ => typeof(object),
            };
        }

        if (t.Contains("INT", StringComparison.Ordinal))
        {
            return typeof(long);
        }

        if (t.Contains("CHAR", StringComparison.Ordinal) || t.Contains("CLOB", StringComparison.Ordinal) || t.Contains("TEXT", StringComparison.Ordinal))
        {
            return typeof(string);
        }

        if (t.Contains("REAL", StringComparison.Ordinal) || t.Contains("FLOA", StringComparison.Ordinal) || t.Contains("DOUB", StringComparison.Ordinal))
        {
            return typeof(double);
        }

        return t.Contains("BLOB", StringComparison.Ordinal) ? typeof(byte[]) : typeof(string);
    }

    /// <inheritdoc/>
    public override string GetDataTypeName(int ordinal) => _statement.ValueKind(ordinal) switch
    {
        TursoValueKind.Null => "NULL",
        TursoValueKind.Integer => "INTEGER",
        TursoValueKind.Real => "REAL",
        TursoValueKind.Text => "TEXT",
        TursoValueKind.Blob => "BLOB",
        // Unknown happens when no row is current (e.g. DbEnumerator/foreach builds the schema before the
        // first Read). Fall back to the declared column type instead of throwing, so enumeration works.
        _ => DataTypeNameFromClrType(GetFieldType(ordinal)),
    };

    private static string DataTypeNameFromClrType(Type type) =>
        type == typeof(long) ? "INTEGER"
        : type == typeof(double) ? "REAL"
        : type == typeof(byte[]) ? "BLOB"
        : "TEXT";

    /// <inheritdoc/>
    public override bool GetBoolean(int ordinal) => GetInt64(ordinal) != 0;

    /// <inheritdoc/>
    public override byte GetByte(int ordinal) => (byte)GetInt64(ordinal);

    /// <inheritdoc/>
    public override char GetChar(int ordinal)
    {
        var value = GetValue(ordinal);
        return value is string { Length: 1 } s ? s[0] : (char)Convert.ToInt64(value, Culture);
    }

    /// <inheritdoc/>
    public override short GetInt16(int ordinal) => (short)GetInt64(ordinal);

    /// <inheritdoc/>
    public override int GetInt32(int ordinal) => (int)GetInt64(ordinal);

    /// <inheritdoc/>
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal), Culture);

    /// <inheritdoc/>
    public override float GetFloat(int ordinal) => (float)GetDouble(ordinal);

    /// <inheritdoc/>
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal), Culture);

    /// <inheritdoc/>
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal), Culture);

    /// <inheritdoc/>
    public override string GetString(int ordinal) => Convert.ToString(GetValue(ordinal), Culture) ?? string.Empty;

    /// <inheritdoc/>
    public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));

    /// <inheritdoc/>
    public override DateTime GetDateTime(int ordinal) =>
        _statement.ValueKind(ordinal) == TursoValueKind.Text
            ? DateTime.Parse(GetString(ordinal), Culture)
            : DateTime.MinValue;

    /// <inheritdoc/>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        GetArray(ordinal, dataOffset, buffer, bufferOffset, length);

    /// <inheritdoc/>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        GetArray(ordinal, dataOffset, buffer, bufferOffset, length);

    /// <inheritdoc/>
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _statement.Dispose();
            if ((_behavior & CommandBehavior.CloseConnection) == CommandBehavior.CloseConnection)
            {
                _command.Connection?.Close();
            }
        }

        _closed = true;
        base.Dispose(disposing);
    }

    private long GetArray<T>(int ordinal, long dataOffset, T[]? buffer, int bufferOffset, int length)
        where T : struct
    {
        var bytes = (byte[])GetValue(ordinal);
        if (buffer is null)
        {
            return Math.Min(bytes.Length - dataOffset, length);
        }

        var position = 0;
        for (; position < length; position++)
        {
            if (bufferOffset + position >= buffer.Length || position + dataOffset >= bytes.Length)
            {
                break;
            }

            buffer[bufferOffset + position] = Unsafe.As<byte, T>(ref bytes[position + dataOffset]);
        }

        return position;
    }

    private void EnsureOpen()
    {
        if (_closed)
        {
            throw new InvalidOperationException("The data reader is closed.");
        }
    }

    private static readonly IFormatProvider Culture = CultureInfo.InvariantCulture;
}
