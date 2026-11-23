using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NpgsqlTypes;

namespace ByteBench.Services.Hosting.DbMigration;

public sealed class MigrationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Type conversion between what SQLite hands back (integers, floats, text and blobs only) and what PostgreSQL stores. A value goes
/// raw SQLite value -> the model's provider type (<see cref="FromSqlite"/>) -> EF value converter -> model value -> the Postgres
/// provider type (EF converter of the Postgres context, e.g. UTC DateTimes) -> the exact CLR type the column needs
/// (<see cref="ToPostgres"/>).
/// </summary>
public static class ValueConversion
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---------------------------------------------------------------- store type -> Npgsql type

    public static NpgsqlDbType PgTypeOf(string storeType)
    {
        var s = Regex.Replace(storeType.ToLowerInvariant(), @"\([^)]*\)", "");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.EndsWith("[]")) return NpgsqlDbType.Array | PgTypeOf(s[..^2]); // List<int>, string[] ... (primitive collections)
        return s switch
        {
            "integer" or "int" or "int4" => NpgsqlDbType.Integer,
            "bigint" or "int8" => NpgsqlDbType.Bigint,
            "smallint" or "int2" => NpgsqlDbType.Smallint,
            "boolean" or "bool" => NpgsqlDbType.Boolean,
            "text" or "citext" => NpgsqlDbType.Text,
            "character varying" or "varchar" => NpgsqlDbType.Varchar,
            "timestamp with time zone" or "timestamptz" => NpgsqlDbType.TimestampTz,
            "timestamp without time zone" or "timestamp" => NpgsqlDbType.Timestamp,
            "uuid" => NpgsqlDbType.Uuid,
            "bytea" => NpgsqlDbType.Bytea,
            "numeric" or "decimal" => NpgsqlDbType.Numeric,
            "double precision" or "float8" => NpgsqlDbType.Double,
            "real" or "float4" => NpgsqlDbType.Real,
            "date" => NpgsqlDbType.Date,
            "time without time zone" or "time" => NpgsqlDbType.Time,
            "interval" => NpgsqlDbType.Interval,
            "json" => NpgsqlDbType.Json,
            "jsonb" => NpgsqlDbType.Jsonb,
            _ => throw new MigrationException($"The PostgreSQL column type '{storeType}' is not supported by migrate-db."),
        };
    }

    public static bool IsInteger(NpgsqlDbType t) => t is NpgsqlDbType.Integer or NpgsqlDbType.Bigint or NpgsqlDbType.Smallint;

    // ---------------------------------------------------------------- SQLite -> provider CLR type

    /// <summary>Converts a raw SQLite value to the provider CLR type the model's mapping expects (null stays null).</summary>
    public static object? FromSqlite(object? raw, Type providerType)
    {
        if (raw is null or DBNull) return null;
        var t = Nullable.GetUnderlyingType(providerType) ?? providerType;
        if (t == typeof(string)) return raw is string s ? s : raw is byte[] bs ? Encoding.UTF8.GetString(bs) : Convert.ToString(raw, Inv);
        if (t == typeof(bool)) return raw switch { bool b => b, string str => str is "1" || str.Equals("true", StringComparison.OrdinalIgnoreCase), _ => Convert.ToInt64(raw, Inv) != 0 };
        if (t == typeof(int)) return checked((int)ToLong(raw));
        if (t == typeof(long)) return ToLong(raw);
        if (t == typeof(short)) return checked((short)ToLong(raw));
        if (t == typeof(byte)) return checked((byte)ToLong(raw));
        if (t == typeof(sbyte)) return checked((sbyte)ToLong(raw));
        if (t == typeof(ushort)) return checked((ushort)ToLong(raw));
        if (t == typeof(uint)) return checked((uint)ToLong(raw));
        if (t == typeof(ulong)) return raw is string us ? ulong.Parse(us, Inv) : checked((ulong)ToLong(raw));
        if (t == typeof(DateTime)) return ParseDateTime(raw);
        if (t == typeof(DateTimeOffset)) return raw is string dos ? DateTimeOffset.Parse(dos, Inv, DateTimeStyles.AssumeUniversal) : new DateTimeOffset(ParseDateTime(raw), TimeSpan.Zero);
        if (t == typeof(DateOnly)) return raw is string dos2 ? DateOnly.Parse(dos2, Inv) : throw Bad(raw, t);
        if (t == typeof(TimeOnly)) return raw is string tos ? TimeOnly.Parse(tos, Inv) : throw Bad(raw, t);
        if (t == typeof(TimeSpan)) return raw is string tss ? TimeSpan.Parse(tss, Inv) : TimeSpan.FromTicks(ToLong(raw));
        if (t == typeof(Guid)) return raw switch { Guid g => g, byte[] b when b.Length == 16 => new Guid(b), string gs => Guid.Parse(gs), _ => throw Bad(raw, t) };
        if (t == typeof(decimal)) return raw switch { string ds => decimal.Parse(ds, NumberStyles.Float, Inv), long l => l, double d => (decimal)d, decimal m => m, _ => throw Bad(raw, t) };
        if (t == typeof(double)) return raw is string dd ? double.Parse(dd, NumberStyles.Float, Inv) : Convert.ToDouble(raw, Inv);
        if (t == typeof(float)) return raw is string ff ? float.Parse(ff, NumberStyles.Float, Inv) : Convert.ToSingle(raw, Inv);
        if (t == typeof(byte[])) return raw as byte[] ?? (raw is string bsr ? Encoding.UTF8.GetBytes(bsr) : throw Bad(raw, t));
        if (t == typeof(char)) return raw is string cs && cs.Length == 1 ? cs[0] : throw Bad(raw, t);
        if (t.IsEnum) return Enum.ToObject(t, ToLong(raw));
        throw new MigrationException($"No conversion from a SQLite value ({raw.GetType().Name}) to {t.Name}.");
    }

    private static MigrationException Bad(object raw, Type t) => new($"Cannot read '{Preview(raw)}' as {t.Name}.");

    private static string Preview(object raw) => raw is byte[] b ? $"<{b.Length} bytes>" : raw.ToString() is { Length: > 40 } s ? s[..40] + "..." : raw.ToString() ?? "";

    private static long ToLong(object raw) => raw switch
    {
        long l => l,
        int i => i,
        string s => long.Parse(s, NumberStyles.Integer, Inv),
        double d when d == Math.Floor(d) => (long)d,
        bool b => b ? 1 : 0,
        _ => Convert.ToInt64(raw, Inv),
    };

    /// <summary>SQLite keeps DateTimes as ISO text without a kind; ByteBench stores UTC, so Unspecified means UTC.</summary>
    public static DateTime ParseDateTime(object raw)
    {
        DateTime d;
        if (raw is DateTime dt) d = dt;
        else if (raw is string s)
        {
            if (!DateTime.TryParse(s, Inv, DateTimeStyles.RoundtripKind, out d))
                throw new MigrationException($"'{Preview(raw)}' is not a date and time.");
        }
        else if (raw is long or double) d = DateTime.SpecifyKind(DateTime.UnixEpoch.AddSeconds(Convert.ToDouble(raw, Inv)), DateTimeKind.Utc);
        else throw Bad(raw, typeof(DateTime));
        return AsUtc(d);
    }

    public static DateTime AsUtc(DateTime d) => d.Kind switch
    {
        DateTimeKind.Utc => d,
        DateTimeKind.Local => d.ToUniversalTime(),
        _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
    };

    // ---------------------------------------------------------------- provider value -> exact Npgsql CLR type

    /// <summary>Makes a value exactly the CLR type Npgsql writes for <paramref name="type"/>, rejecting what PostgreSQL cannot store.</summary>
    public static object ToPostgres(object value, NpgsqlDbType type)
    {
        if (type.HasFlag(NpgsqlDbType.Array)) return ToArray(value, type & ~NpgsqlDbType.Array);
        switch (type)
        {
            case NpgsqlDbType.Integer: return checked((int)IntegralOf(value));
            case NpgsqlDbType.Bigint: return IntegralOf(value);
            case NpgsqlDbType.Smallint: return checked((short)IntegralOf(value));
            case NpgsqlDbType.Boolean: return value is bool b ? b : throw Wrong(value, type);
            case NpgsqlDbType.Text or NpgsqlDbType.Varchar or NpgsqlDbType.Json or NpgsqlDbType.Jsonb:
                var s = value as string ?? (value is char c ? c.ToString() : throw Wrong(value, type));
                // PostgreSQL text cannot contain NUL: report it instead of silently changing the data.
                if (s.Contains('\0')) throw new MigrationException("the text contains a NUL character (\\u0000), which PostgreSQL cannot store; remove it from the source row first");
                return s;
            case NpgsqlDbType.TimestampTz:
                return value switch { DateTime d => AsUtc(d), DateTimeOffset o => o.UtcDateTime, _ => throw Wrong(value, type) };
            case NpgsqlDbType.Timestamp:
                return value switch { DateTime d => DateTime.SpecifyKind(d, DateTimeKind.Unspecified), DateTimeOffset o => DateTime.SpecifyKind(o.UtcDateTime, DateTimeKind.Unspecified), _ => throw Wrong(value, type) };
            case NpgsqlDbType.Date:
                return value switch { DateOnly d => d, DateTime dt => DateOnly.FromDateTime(dt), _ => throw Wrong(value, type) };
            case NpgsqlDbType.Time:
                return value switch { TimeOnly t => t, TimeSpan ts => TimeOnly.FromTimeSpan(ts), _ => throw Wrong(value, type) };
            case NpgsqlDbType.Interval: return value is TimeSpan i ? i : throw Wrong(value, type);
            case NpgsqlDbType.Uuid: return value is Guid g ? g : throw Wrong(value, type);
            case NpgsqlDbType.Bytea: return value is byte[] by ? by : throw Wrong(value, type);
            case NpgsqlDbType.Numeric:
                return value switch { decimal m => m, long or int or short or byte => Convert.ToDecimal(value, Inv), double dd => checked((decimal)dd), float ff => checked((decimal)ff), _ => throw Wrong(value, type) };
            case NpgsqlDbType.Double: return value switch { double d => d, float f => (double)f, _ => Convert.ToDouble(value, Inv) };
            case NpgsqlDbType.Real: return value switch { float f => f, double d => (float)d, _ => Convert.ToSingle(value, Inv) };
            default: throw new MigrationException($"Unsupported PostgreSQL type {type}.");
        }
    }

    private static object ToArray(object value, NpgsqlDbType element)
    {
        if (value is string or byte[] || value is not System.Collections.IEnumerable items) throw Wrong(value, NpgsqlDbType.Array | element);
        var clr = ElementClr(element);
        var list = items.Cast<object?>().ToList();
        var array = Array.CreateInstance(clr, list.Count);
        for (var i = 0; i < list.Count; i++)
            array.SetValue(list[i] == null ? throw new MigrationException("a collection contains NULL") : ToPostgres(list[i]!, element), i);
        return array;
    }

    private static Type ElementClr(NpgsqlDbType t) => t switch
    {
        NpgsqlDbType.Integer => typeof(int), NpgsqlDbType.Bigint => typeof(long), NpgsqlDbType.Smallint => typeof(short),
        NpgsqlDbType.Text or NpgsqlDbType.Varchar => typeof(string), NpgsqlDbType.Boolean => typeof(bool), NpgsqlDbType.Uuid => typeof(Guid),
        NpgsqlDbType.Double => typeof(double), NpgsqlDbType.Real => typeof(float), NpgsqlDbType.Numeric => typeof(decimal),
        NpgsqlDbType.TimestampTz or NpgsqlDbType.Timestamp => typeof(DateTime),
        _ => throw new MigrationException($"Unsupported array element type {t}."),
    };

    private static long IntegralOf(object v) => v switch
    {
        int i => i, long l => l, short s => s, byte b => b, sbyte sb => sb, ushort us => us, uint ui => ui,
        ulong ul => checked((long)ul),
        _ when v.GetType().IsEnum => Convert.ToInt64(v, Inv),
        _ => throw Wrong(v, NpgsqlDbType.Bigint),
    };

    private static MigrationException Wrong(object v, NpgsqlDbType t) => new($"a {v.GetType().Name} value cannot be stored in a {t} column");

    // ---------------------------------------------------------------- canonical form for checksums

    /// <summary>
    /// Appends a canonical, provider-independent encoding of a value (already passed through <see cref="ToPostgres"/>). Timestamps are
    /// cut to PostgreSQL's microsecond resolution and decimals are normalised, so the same data hashes identically in both databases.
    /// </summary>
    public static void AppendCanonical(IncrementalHash hash, object? value)
    {
        void Tag(char c) => hash.AppendData([(byte)c]);
        void Long(long l) => hash.AppendData(BitConverter.GetBytes(l));
        switch (value)
        {
            case null: Tag('0'); break;
            case int or long or short: Tag('i'); Long(Convert.ToInt64(value, Inv)); break;
            case bool b: Tag('b'); Long(b ? 1 : 0); break;
            case string s: { var bytes = Encoding.UTF8.GetBytes(s); Tag('s'); Long(bytes.Length); hash.AppendData(bytes); break; }
            case byte[] y: Tag('y'); Long(y.Length); hash.AppendData(y); break;
            case Array a: Tag('a'); Long(a.Length); foreach (var e in a) AppendCanonical(hash, e); break;
            case DateTime d: Tag('t'); Long(d.Ticks - d.Ticks % 10); break;
            case DateOnly d: Tag('d'); Long(d.DayNumber); break;
            case TimeOnly t: Tag('m'); Long(t.Ticks - t.Ticks % 10); break;
            case TimeSpan ts: Tag('v'); Long(ts.Ticks - ts.Ticks % 10); break;
            case Guid g: Tag('g'); hash.AppendData(g.ToByteArray()); break;
            case decimal m: { var bytes = Encoding.ASCII.GetBytes((m / 1.000000000000000000000000000000000m).ToString(Inv)); Tag('n'); Long(bytes.Length); hash.AppendData(bytes); break; }
            case double dd: Tag('f'); Long(BitConverter.DoubleToInt64Bits(dd)); break;
            case float ff: Tag('f'); Long(BitConverter.DoubleToInt64Bits(ff)); break;
            default: throw new MigrationException($"Cannot checksum a {value.GetType().Name}.");
        }
    }

    /// <summary>Order-independent table checksum: the sum (mod 2^256) of the SHA-256 of every row, so it does not depend on how either database sorts.</summary>
    public sealed class TableChecksum
    {
        private static readonly System.Numerics.BigInteger Mask = (System.Numerics.BigInteger.One << 256) - 1;
        private System.Numerics.BigInteger sum;
        public long Rows { get; private set; }

        public void AddRow(IReadOnlyList<object?> values)
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var v in values) AppendCanonical(h, v);
            sum = (sum + new System.Numerics.BigInteger(h.GetHashAndReset(), isUnsigned: true)) & Mask;
            Rows++;
        }

        public string Hex => sum.ToString("x64").TrimStart('0').PadLeft(8, '0');
    }
}
