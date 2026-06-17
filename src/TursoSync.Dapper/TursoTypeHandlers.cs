using System.Data;
using System.Globalization;
using Dapper;

namespace Turso;

/// <summary>
/// Dapper type handlers that round-trip <see cref="Ulid"/>, <see cref="DateTimeOffset"/> and
/// <see cref="Guid"/> as portable <c>TEXT</c> (26-char ULID, ISO-8601 UTC timestamp, "D"-format GUID).
/// Handy with the TursoSync provider — and any SQLite-family ADO.NET provider — so the same SQL round-trips
/// without engine-native type divergence. Idempotent; call <see cref="Register"/> before first use.
/// </summary>
public static class TursoTypeHandlers
{
    private static int _registered;

    /// <summary>Register the handlers once per process.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        SqlMapper.AddTypeHandler(new UlidHandler());
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new GuidHandler());
    }

    private sealed class UlidHandler : SqlMapper.TypeHandler<Ulid>
    {
        public override Ulid Parse(object value) => Ulid.Parse((string)value);

        public override void SetValue(IDbDataParameter parameter, Ulid value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToString();
        }
    }

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) =>
            DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }
    }

    private sealed class GuidHandler : SqlMapper.TypeHandler<Guid>
    {
        public override Guid Parse(object value) => Guid.Parse((string)value);

        public override void SetValue(IDbDataParameter parameter, Guid value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.ToString("D", CultureInfo.InvariantCulture);
        }
    }
}
