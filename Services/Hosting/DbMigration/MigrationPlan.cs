using ByteBench.Data;
using ByteBench.Data.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NpgsqlTypes;

namespace ByteBench.Services.Hosting.DbMigration;

public sealed class ColumnPlan(string name, IProperty source, IProperty target)
{
    public string Name { get; } = name;
    public bool Nullable { get; } = target.IsNullable;
    public NpgsqlDbType PgType { get; } = ValueConversion.PgTypeOf(target.GetRelationalTypeMapping().StoreType);

    private readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter? sourceConverter = source.GetRelationalTypeMapping().Converter;
    private readonly Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter? targetConverter = target.GetRelationalTypeMapping().Converter;
    private readonly Type sourceProviderType = source.GetRelationalTypeMapping().Converter?.ProviderClrType
        ?? Nullable_(source.ClrType);

    private static Type Nullable_(Type t) => System.Nullable.GetUnderlyingType(t) ?? t;

    /// <summary>A freshly numbered column (identity/serial) whose sequence must continue after the highest copied value.</summary>
    public bool IsGenerated { get; } = target.ValueGenerated == ValueGenerated.OnAdd && ValueConversion.IsInteger(ValueConversion.PgTypeOf(target.GetRelationalTypeMapping().StoreType));

    private readonly Microsoft.EntityFrameworkCore.Storage.Json.JsonValueReaderWriter? sourceJson =
        source.GetRelationalTypeMapping().ElementTypeMapping != null && source.GetRelationalTypeMapping().Converter == null ? source.GetRelationalTypeMapping().JsonValueReaderWriter : null;

    /// <summary>A raw SQLite value as the exact value to hand to Npgsql (null stays null).</summary>
    public object? Convert(object? raw)
    {
        if (raw is null or DBNull) return null;
        // A collection (List<int>, string[] ...) is stored by SQLite as JSON text and becomes a PostgreSQL array.
        if (sourceJson != null)
        {
            var parsed = sourceJson.FromJsonString(raw as string ?? throw new MigrationException("a collection column does not hold JSON text"));
            return parsed is null ? null : ValueConversion.ToPostgres(targetConverter != null ? targetConverter.ConvertToProvider(parsed)! : parsed, PgType);
        }
        var provider = ValueConversion.FromSqlite(raw, sourceProviderType);
        var model = sourceConverter != null ? sourceConverter.ConvertFromProvider(provider) : provider;
        var back = targetConverter != null ? targetConverter.ConvertToProvider(model) : model;
        return back is null ? null : ValueConversion.ToPostgres(back, PgType);
    }

    /// <summary>A value read back from PostgreSQL, in the same canonical CLR type <see cref="Convert"/> yields.</summary>
    public object? FromPostgres(object? value) => value is null or DBNull ? null : ValueConversion.ToPostgres(value, PgType);
}

public sealed record ForeignKeyPlan(string Name, string[] Columns, string PrincipalTable, string[] PrincipalColumns, bool Nullable, bool Deferred, TableEdge Edge);

public sealed class TablePlan(string? schema, string name, ColumnPlan[] columns, string[] key, ForeignKeyPlan[] foreignKeys)
{
    public string? Schema { get; } = schema;
    public string Name { get; } = name;
    public ColumnPlan[] Columns { get; } = columns;
    /// <summary>Primary key columns (all columns for a table without one).</summary>
    public string[] Key { get; } = key;
    public ForeignKeyPlan[] ForeignKeys { get; set; } = foreignKeys;
    public string Display => Schema == null ? Name : Schema + "." + Name;
    public string Quoted => Schema == null ? Q(Name) : Q(Schema) + "." + Q(Name);
    public IEnumerable<ForeignKeyPlan> Deferred => ForeignKeys.Where(f => f.Deferred);
    public ColumnPlan Column(string name) => Columns.First(c => c.Name == name);
    public int Index(string name) => Array.FindIndex(Columns, c => c.Name == name);
    public static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    public string SelectAll(string? where = null, string? orderBy = null) =>
        $"SELECT {string.Join(", ", Columns.Select(c => Q(c.Name)))} FROM {Quoted}{(where == null ? "" : " WHERE " + where)}{(orderBy == null ? "" : " ORDER BY " + orderBy)}";
}

/// <summary>
/// What to copy and in which order, read from the EF model (never a hand-written table list, so tables added by later migrations
/// are covered automatically): one entry per table, columns from the Postgres model, foreign keys ordered by <see cref="TableOrdering"/>.
/// </summary>
public sealed class MigrationPlan
{
    public required IReadOnlyList<TablePlan> Tables { get; init; }

    public static MigrationPlan Build(IModel sourceModel, IModel targetModel)
    {
        var plans = new Dictionary<string, TablePlan>();
        var edges = new List<TableEdge>();

        foreach (var group in sourceModel.GetEntityTypes().Where(e => e.GetTableName() != null && e.GetViewName() == null)
                     .GroupBy(e => (Schema: e.GetSchema(), Table: e.GetTableName()!)).OrderBy(g => g.Key.Table, StringComparer.Ordinal))
        {
            var store = StoreObjectIdentifier.Table(group.Key.Table, group.Key.Schema);
            var columns = new List<ColumnPlan>();
            var seen = new HashSet<string>();
            foreach (var et in group.OrderBy(e => e.BaseType == null ? 0 : 1).ThenBy(e => e.Name, StringComparer.Ordinal))
            {
                var targetEt = targetModel.FindEntityType(et.Name) ?? throw new MigrationException($"The PostgreSQL model has no entity {et.DisplayName()}.");
                foreach (var p in et.GetProperties())
                {
                    var col = p.GetColumnName(store);
                    if (col == null || !seen.Add(col)) continue;
                    var tp = targetEt.FindProperty(p.Name) ?? throw new MigrationException($"The PostgreSQL model has no property {et.DisplayName()}.{p.Name}.");
                    columns.Add(new ColumnPlan(col, p, tp));
                }
            }
            var root = group.OrderBy(e => e.BaseType == null ? 0 : 1).First();
            var key = root.FindPrimaryKey()?.Properties.Select(p => p.GetColumnName(store)!).ToArray() ?? columns.Select(c => c.Name).ToArray();

            var fks = new List<ForeignKeyPlan>();
            var seenFks = new HashSet<string>();
            foreach (var et in group)
                foreach (var fk in et.GetDeclaredForeignKeys())
                {
                    var principalTable = fk.PrincipalEntityType.GetTableName();
                    if (principalTable == null) continue;
                    var cols = fk.Properties.Select(p => p.GetColumnName(store)!).ToArray();
                    var pStore = StoreObjectIdentifier.Table(principalTable, fk.PrincipalEntityType.GetSchema());
                    var pcols = fk.PrincipalKey.Properties.Select(p => p.GetColumnName(pStore)!).ToArray();
                    var name = $"{group.Key.Table}({string.Join(",", cols)})->{principalTable}";
                    if (!seenFks.Add(name)) continue;
                    var nullable = fk.Properties.All(p => p.IsNullable);
                    var edge = new TableEdge(group.Key.Table, principalTable, nullable, name);
                    fks.Add(new ForeignKeyPlan(name, cols, principalTable, pcols, nullable, false, edge));
                    edges.Add(edge);
                }
            plans[group.Key.Table] = new TablePlan(group.Key.Schema, group.Key.Table, columns.ToArray(), key, fks.ToArray());
        }

        var (order, deferred) = TableOrdering.Sort(plans.Keys, edges);
        foreach (var t in plans.Values)
        {
            t.ForeignKeys = t.ForeignKeys.Select(f => f with { Deferred = deferred.Contains(f.Edge) }).ToArray();
            if (t.Deferred.Any() && t.Key.Length == 0)
                throw new MigrationException($"{t.Display} needs a deferred foreign key but has no primary key.");
        }
        return new MigrationPlan { Tables = order.Select(n => plans[n]).ToList() };
    }

    /// <summary>The plan for the real models (SQLite model as the source, Postgres model as the target); no database is opened.</summary>
    public static MigrationPlan Build()
    {
        using var sqlite = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=:memory:").Options);
        using var pg = new PostgresAppDbContext(new DbContextOptionsBuilder<PostgresAppDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);
        return Build(sqlite.Model, pg.Model);
    }
}
