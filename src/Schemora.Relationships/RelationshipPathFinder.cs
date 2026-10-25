using System.Text;

namespace Schemora.Relationships;

public sealed class RelationshipPathFinder
{
    public IReadOnlyList<JoinPath> Find(RelationshipGraph graph, string fromTableId, string toTableId, int maxDepth = 6, int maxPaths = 5)
    {
        if (maxDepth is < 0 or > 12 || maxPaths is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "Path search supports depth 0..12 and 1..20 results.");
        var tables = graph.Tables.ToDictionary(table => table.Table.Id, StringComparer.Ordinal);
        if (!tables.ContainsKey(fromTableId) || !tables.ContainsKey(toTableId))
            throw new ArgumentException("Both path endpoints must exist in the current schema graph.");
        var trusted = graph
            .Relations.Where(edge => edge.IsTrusted && edge.Status is "physical" or "mapped" or "confirmed")
            .Where(edge => RelationshipRules.Valid(edge.Relation, tables))
            .OrderByDescending(edge => edge.Relation.Confidence)
            .ThenBy(edge => edge.Relation.Id, StringComparer.Ordinal)
            .ToArray();
        var neighbors = tables.Keys.ToDictionary(id => id, _ => new List<RelationshipEdge>(), StringComparer.Ordinal);
        foreach (var edge in trusted)
        {
            neighbors[edge.Relation.FromTableId].Add(edge);
            if (edge.Relation.FromTableId != edge.Relation.ToTableId)
                neighbors[edge.Relation.ToTableId].Add(edge);
        }
        var result = new List<JoinPath>();
        var pending = new Queue<(string[] Tables, RelationshipEdge[] Edges)>();
        pending.Enqueue(([fromTableId], []));
        var visitedStates = 0;
        while (pending.Count > 0 && result.Count < maxPaths && ++visitedStates <= 50_000)
        {
            var state = pending.Dequeue();
            var current = state.Tables[^1];
            if (current == toTableId)
            {
                var selected = state.Tables.Select(id => tables[id]).ToArray();
                if (
                    selected
                        .GroupBy(table => table.DatabaseName, StringComparer.OrdinalIgnoreCase)
                        .Any(group => group.Select(table => table.DatabaseId).Distinct().Count() > 1)
                )
                    continue;
                result.Add(
                    new(
                        state.Tables,
                        state.Edges.Select(edge => edge.Relation.Id).ToArray(),
                        Render(selected, state.Edges),
                        state
                            .Edges.Select(edge =>
                                $"{edge.Relation.Name}: {edge.Status}; {string.Join("; ", edge.Evidence.Select(item => item.Detail))}"
                            )
                            .ToArray()
                    )
                );
                continue;
            }
            if (state.Edges.Length >= maxDepth)
                continue;
            foreach (var edge in neighbors[current])
            {
                var next = edge.Relation.FromTableId == current ? edge.Relation.ToTableId : edge.Relation.FromTableId;
                if (state.Tables.Contains(next, StringComparer.Ordinal))
                    continue;
                if (pending.Count < 50_000)
                    pending.Enqueue((state.Tables.Append(next).ToArray(), state.Edges.Append(edge).ToArray()));
            }
        }
        return result;
    }

    private static string Render(IReadOnlyList<RelationshipTable> tables, IReadOnlyList<RelationshipEdge> edges)
    {
        var sql = new StringBuilder("SELECT [t0].*\nFROM ").Append(Name(tables[0])).Append(" AS [t0]");
        for (var index = 0; index < edges.Count; index++)
        {
            var edge = edges[index].Relation;
            var forward = edge.FromTableId == tables[index].Table.Id;
            var from = forward ? edge.FromColumns : edge.ToColumns;
            var to = forward ? edge.ToColumns : edge.FromColumns;
            sql.Append("\nINNER JOIN ").Append(Name(tables[index + 1])).Append(" AS ").Append(Quote("t" + (index + 1))).Append(" ON ");
            sql.Append(
                string.Join(
                    " AND ",
                    from.Zip(to)
                        .Select(pair => $"{Quote("t" + index)}.{Quote(pair.First)} = {Quote("t" + (index + 1))}.{Quote(pair.Second)}")
                )
            );
        }
        return sql.Append(';').ToString();
    }

    private static string Name(RelationshipTable table) =>
        $"{Quote(table.DatabaseName)}.{Quote(table.Table.Schema)}.{Quote(table.Table.Name)}";

    private static string Quote(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
