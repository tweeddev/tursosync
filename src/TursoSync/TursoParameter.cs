using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Turso;

/// <summary>
/// A bind parameter for <see cref="TursoCommand"/>. Mirrors the official <c>Turso.Data</c> parameter: only
/// input direction, a CLR→native type map, and invariant-culture text formatting for dates/decimals/guids.
/// </summary>
public sealed class TursoParameter : DbParameter
{
    private int _size;

    private static readonly Dictionary<Type, TursoValueKind> TypeMapping = new()
    {
        [typeof(bool)] = TursoValueKind.Integer,
        [typeof(byte)] = TursoValueKind.Integer,
        [typeof(sbyte)] = TursoValueKind.Integer,
        [typeof(short)] = TursoValueKind.Integer,
        [typeof(ushort)] = TursoValueKind.Integer,
        [typeof(int)] = TursoValueKind.Integer,
        [typeof(uint)] = TursoValueKind.Integer,
        [typeof(long)] = TursoValueKind.Integer,
        [typeof(ulong)] = TursoValueKind.Integer,
        [typeof(double)] = TursoValueKind.Real,
        [typeof(float)] = TursoValueKind.Real,
        [typeof(byte[])] = TursoValueKind.Blob,
        [typeof(char)] = TursoValueKind.Text,
        [typeof(string)] = TursoValueKind.Text,
        [typeof(decimal)] = TursoValueKind.Text,
        [typeof(Guid)] = TursoValueKind.Text,
        [typeof(DateTime)] = TursoValueKind.Text,
        [typeof(DateTimeOffset)] = TursoValueKind.Text,
        [typeof(DateOnly)] = TursoValueKind.Text,
        [typeof(TimeOnly)] = TursoValueKind.Text,
        [typeof(TimeSpan)] = TursoValueKind.Text,
        [typeof(DBNull)] = TursoValueKind.Null,
    };

    /// <summary>Create an empty parameter.</summary>
    public TursoParameter()
    {
    }

    /// <summary>Create a parameter with a value.</summary>
    public TursoParameter(object? value) => Value = value;

    /// <summary>Create a named parameter with a value.</summary>
    public TursoParameter(string parameterName, object? value)
    {
        ParameterName = parameterName;
        Value = value;
    }

    /// <inheritdoc/>
    public override DbType DbType { get; set; } = DbType.String;

    /// <inheritdoc/>
    public override ParameterDirection Direction
    {
        get => ParameterDirection.Input;
        set
        {
            if (value != ParameterDirection.Input)
            {
                throw new ArgumentException("Only input parameters are supported.");
            }
        }
    }

    /// <inheritdoc/>
    public override bool IsNullable { get; set; } = true;

    /// <inheritdoc/>
    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    /// <inheritdoc/>
    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    /// <inheritdoc/>
    public override object? Value { get; set; }

    /// <inheritdoc/>
    public override bool SourceColumnNullMapping { get; set; }

    /// <inheritdoc/>
    public override int Size
    {
        get => _size;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, -1);
            _size = value;
        }
    }

    /// <inheritdoc/>
    public override void ResetDbType() => DbType = DbType.String;

    /// <summary>
    /// Convert <see cref="Value"/> to the CLR primitive (<c>long</c>/<c>double</c>/<c>string</c>/<c>byte[]</c>
    /// /<c>null</c>) the native bind functions accept, applying the same conversions as the official wrapper.
    /// </summary>
    public object? ToBindValue()
    {
        if (Value is null || Value is DBNull)
        {
            return null;
        }

        var type = Value.GetType();
        if (!TypeMapping.TryGetValue(type, out var kind))
        {
            kind = TursoValueKind.Text; // best-effort: stringify unknown types invariantly
        }

        return kind switch
        {
            TursoValueKind.Null => null,
            TursoValueKind.Integer => Convert.ToInt64(Value, CultureInfo.InvariantCulture),
            TursoValueKind.Real => Convert.ToDouble(Value, CultureInfo.InvariantCulture),
            TursoValueKind.Blob => (byte[])Value,
            _ => ToInvariantString(Value),
        };
    }

    private static string ToInvariantString(object value) => value switch
    {
        string s => s,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}

/// <summary>Parameter collection for <see cref="TursoCommand"/>, mirroring the official wrapper.</summary>
public sealed class TursoParameterCollection : DbParameterCollection
{
    private readonly List<TursoParameter> _parameters = [];

    /// <inheritdoc/>
    public override int Count => _parameters.Count;

    /// <inheritdoc/>
    public override object SyncRoot => ((ICollection)_parameters).SyncRoot;

    /// <summary>Add a named parameter with a value and return it.</summary>
    public TursoParameter AddWithValue(string parameterName, object? value)
    {
        var parameter = new TursoParameter(parameterName, value);
        _parameters.Add(parameter);
        return parameter;
    }

    /// <inheritdoc/>
    public override int Add(object value)
    {
        _parameters.Add(value as TursoParameter ?? new TursoParameter(value));
        return _parameters.Count - 1;
    }

    /// <inheritdoc/>
    public override void AddRange(Array values)
    {
        foreach (var value in values)
        {
            Add(value!);
        }
    }

    /// <inheritdoc/>
    public override void Clear() => _parameters.Clear();

    /// <inheritdoc/>
    public override bool Contains(object value) =>
        _parameters.Any(p => value is TursoParameter ? ReferenceEquals(p, value) : Equals(p.Value, value));

    /// <inheritdoc/>
    public override bool Contains(string value) => IndexOf(value) >= 0;

    /// <inheritdoc/>
    public override void CopyTo(Array array, int index) => ((ICollection)_parameters).CopyTo(array, index);

    /// <inheritdoc/>
    public override IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    /// <inheritdoc/>
    public override int IndexOf(object value) =>
        _parameters.FindIndex(p => value is TursoParameter ? ReferenceEquals(p, value) : Equals(p.Value, value));

    /// <inheritdoc/>
    public override int IndexOf(string parameterName) =>
        _parameters.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public override void Insert(int index, object value) =>
        _parameters.Insert(index, value as TursoParameter ?? new TursoParameter(value));

    /// <inheritdoc/>
    public override void Remove(object value)
    {
        var index = IndexOf(value);
        if (index < 0)
        {
            throw new ArgumentException("Parameter not found.", nameof(value));
        }

        _parameters.RemoveAt(index);
    }

    /// <inheritdoc/>
    public override void RemoveAt(int index) => _parameters.RemoveAt(index);

    /// <inheritdoc/>
    public override void RemoveAt(string parameterName)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
        {
            throw new ArgumentException($"Parameter {parameterName} not found.", nameof(parameterName));
        }

        _parameters.RemoveAt(index);
    }

    /// <inheritdoc/>
    protected override DbParameter GetParameter(int index) => _parameters[index];

    /// <inheritdoc/>
    protected override DbParameter GetParameter(string parameterName) =>
        _parameters.Find(p => string.Equals(p.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"Parameter {parameterName} not found.", nameof(parameterName));

    /// <inheritdoc/>
    protected override void SetParameter(int index, DbParameter value) =>
        _parameters[index] = value as TursoParameter ?? throw new ArgumentException("Expected a TursoParameter.", nameof(value));

    /// <inheritdoc/>
    protected override void SetParameter(string parameterName, DbParameter value)
    {
        var index = IndexOf(parameterName);
        if (index < 0)
        {
            Add(value);
        }
        else
        {
            _parameters[index] = value as TursoParameter ?? throw new ArgumentException("Expected a TursoParameter.", nameof(value));
        }
    }
}
