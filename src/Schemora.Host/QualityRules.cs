using Schemora.SqlServer;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record QualityRuleDefinition(string Id, string DefaultSeverity);

public sealed record QualityPolicy(IReadOnlyDictionary<string, string> Rules, int Version = 0);

public static class QualityRules
{
    public static readonly QualityRuleDefinition[] Definitions =
    [
        new("SCHEMA_UNAVAILABLE", "error"),
        new("TABLE_WITHOUT_PRIMARY_KEY", "warning"),
        new("UNRESOLVED_DEPENDENCY", "warning"),
        new("UNCONFIRMED_RELATION", "info"),
        new("FK_WITHOUT_SUPPORTING_INDEX", "info"),
        new("NULLABLE_FOREIGN_KEY", "info"),
        new("DUPLICATE_INDEX", "warning"),
        new("PARTIAL_SQL_OBJECT", "warning"),
    ];

    public static QualityPolicy Read(CatalogStore store, string workspaceId)
    {
        WorkspaceEndpoints.RequireWorkspace(store, workspaceId);
        return store.Get<QualityPolicy>("UserSettings", "quality-rules:" + workspaceId) ?? new(new Dictionary<string, string>());
    }

    public static QualityPolicy Save(CatalogStore store, string workspaceId, QualityPolicy request) =>
        store.WithWriteLock(() =>
        {
            var current = Read(store, workspaceId);
            if (request.Version != current.Version)
                throw new AppException("QUALITY_POLICY_CHANGED", "Quality rules changed. Reload them before saving.", 409);
            if (
                request.Rules is null
                || request.Rules.Count > Definitions.Length
                || request.Rules.Any(r => !Definitions.Any(d => d.Id == r.Key) || r.Value is not ("off" or "info" or "warning" or "error"))
            )
                throw new AppException("QUALITY_POLICY_INVALID", "Choose a known quality rule and severity: off, info, warning or error.");
            var saved = new QualityPolicy(
                new SortedDictionary<string, string>(request.Rules.ToDictionary(), StringComparer.Ordinal),
                current.Version + 1
            );
            store.Put("UserSettings", "quality-rules:" + workspaceId, workspaceId, saved);
            return saved;
        });

    public static void Evaluate(SchemaGraph graph, Action<string, string, string, string, string> add)
    {
        static bool Prefix(IEnumerable<string> key, IReadOnlyList<string> columns) =>
            key.Take(columns.Count).SequenceEqual(columns, StringComparer.OrdinalIgnoreCase);
        foreach (
            var relation in graph.Relations.Where(r =>
                r.Kind == "declared-fk" || r.Kind == "foreignKey" || r.Kind == "physical" || r.Kind == "fk"
            )
        )
        {
            var table = graph.Tables.FirstOrDefault(t => t.Id == relation.FromTableId);
            if (table is null)
                continue;
            var indexes = (graph.Indexes ?? []).Where(i => i.ObjectId == table.Id).ToArray();
            var covered =
                Prefix(table.PrimaryKey, relation.FromColumns)
                || (table.UniqueKeys ?? []).Any(k => Prefix(k.Columns, relation.FromColumns))
                || indexes.Any(i =>
                    string.IsNullOrEmpty(i.Filter)
                    && !i.Kind.Contains("columnstore", StringComparison.OrdinalIgnoreCase)
                    && Prefix(i.Columns.Select(c => c.Name), relation.FromColumns)
                );
            if (!covered)
                add(
                    "FK_WITHOUT_SUPPORTING_INDEX",
                    "info",
                    table.Id,
                    relation.Name,
                    "No unfiltered index with the foreign-key columns as its leading keys is recorded. Review workload needs before adding an index."
                );
            if (
                relation.FromColumns.Any(name =>
                    table.Columns.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && c.IsNullable)
                )
            )
                add(
                    "NULLABLE_FOREIGN_KEY",
                    "info",
                    table.Id,
                    relation.Name,
                    "This foreign key includes nullable columns. Optional relationships can be intentional; review the intended cardinality."
                );
        }
        foreach (var group in (graph.Indexes ?? []).GroupBy(i => i.ObjectId))
        {
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var index in group.OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                var signature = System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        index.Kind,
                        index.IsUnique,
                        Keys = index.Columns.Select(c => new { c.Name, c.Descending }),
                        Includes = index.IncludedColumns.Order(StringComparer.OrdinalIgnoreCase),
                        index.Filter,
                    }
                );
                if (seen.TryGetValue(signature, out var other))
                    add(
                        "DUPLICATE_INDEX",
                        "warning",
                        index.ObjectId,
                        index.Name,
                        "Recorded index keys, includes, filter and uniqueness match "
                            + other
                            + ". Review dependencies before removing either index."
                    );
                else
                    seen.Add(signature, index.Name);
            }
        }
        foreach (var obj in (graph.Objects ?? []).Where(o => o.IsPartial))
            add(
                "PARTIAL_SQL_OBJECT",
                "warning",
                obj.Id,
                obj.Schema + "." + obj.Name,
                "The SQL object was only partially analyzed. Its definition or dependencies may be incomplete."
            );
    }
}
