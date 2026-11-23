namespace ByteBench.Services.Hosting.DbMigration;

/// <summary>A foreign key between two tables: <see cref="From"/> (dependent) references <see cref="To"/> (principal).</summary>
public sealed record TableEdge(string From, string To, bool Nullable, string Name);

/// <summary>
/// Orders tables so that every table comes after the tables its foreign keys point to. Self-references and cycles cannot be
/// satisfied by ordering alone: one nullable foreign key per cycle is "deferred" (written as NULL first and filled in by a
/// second pass). A cycle made only of required foreign keys cannot be loaded and is reported.
/// </summary>
public static class TableOrdering
{
    public static (List<string> Order, HashSet<TableEdge> Deferred) Sort(IEnumerable<string> tables, IEnumerable<TableEdge> edges)
    {
        var names = tables.Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
        var known = names.ToHashSet();
        var outgoing = names.ToDictionary(n => n, _ => new List<TableEdge>());
        foreach (var e in edges)
        {
            if (!known.Contains(e.From) || !known.Contains(e.To)) continue;
            outgoing[e.From].Add(e);
        }
        // Required edges first: a cycle then closes over a nullable edge when it has one, and that is the one we can defer.
        foreach (var list in outgoing.Values)
            list.Sort((a, b) => a.Nullable != b.Nullable ? a.Nullable.CompareTo(b.Nullable)
                : string.CompareOrdinal(a.To + "/" + a.Name, b.To + "/" + b.Name));

        var state = new Dictionary<string, int>(); // 1 = on the DFS stack, 2 = done
        var order = new List<string>();
        var deferred = new HashSet<TableEdge>();
        var path = new List<string>();

        void Visit(string n)
        {
            state[n] = 1;
            path.Add(n);
            foreach (var e in outgoing[n])
            {
                var s = state.GetValueOrDefault(e.To);
                if (s == 2) continue;
                if (s == 0) { Visit(e.To); continue; }
                // Back edge (or self-reference): this foreign key closes a cycle.
                if (!e.Nullable)
                {
                    var cycle = path.SkipWhile(p => p != e.To).Append(e.To);
                    throw new MigrationException($"The tables {string.Join(" -> ", cycle)} reference each other through required foreign keys ({e.Name}), so their rows cannot be loaded in any order.");
                }
                deferred.Add(e);
            }
            path.RemoveAt(path.Count - 1);
            state[n] = 2;
            order.Add(n);
        }

        foreach (var n in names)
            if (!state.ContainsKey(n)) Visit(n);
        return (order, deferred);
    }
}
