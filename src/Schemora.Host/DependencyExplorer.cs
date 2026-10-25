using Schemora.RecipeEngine;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record DependencyNode(string Id, string Name, string Kind, int Depth);

public sealed record DependencyLink(string Id, string Source, string Target, string Explanation, double Confidence);

public sealed record DependencyPage(
    IReadOnlyList<DependencyNode> Nodes,
    IReadOnlyList<DependencyLink> Edges,
    int Total,
    int Offset,
    int Limit,
    bool Partial,
    int RefreshedDatabases = 0,
    int UpdatedNodes = 0,
    int UpdatedEdges = 0
);

public static class DependencyExplorer
{
    public static DependencyPage Read(CatalogStore store, string workspaceId, string objectId, string? query, int offset, int limit) =>
        store.WithWriteLock(() => ReadCore(store, workspaceId, objectId, query, offset, limit));

    private static DependencyPage ReadCore(CatalogStore store, string workspaceId, string objectId, string? query, int offset, int limit)
    {
        var context = SqlDependencyIndex.Load(store, workspaceId);
        var objects = context.Nodes.ToDictionary(o => o.Id);
        if (!objects.ContainsKey(objectId))
            throw new AppException("SCHEMA_OBJECT_NOT_FOUND", "Object not found.", 404);
        var dependencies = context.Edges;
        var reverse = dependencies
            .Where(d => d.ToObjectId is not null)
            .GroupBy(d => d.ToObjectId!)
            .ToDictionary(g => g.Key, g => g.ToArray());
        var reached = new Dictionary<string, int> { { objectId, 0 } };
        var parent = new Dictionary<string, string>();
        var queue = new Queue<string>();
        queue.Enqueue(objectId);
        var truncated = false;
        while (queue.TryDequeue(out var current))
            foreach (var d in reverse.GetValueOrDefault(current) ?? [])
            {
                if (reached.ContainsKey(d.FromObjectId) || !objects.ContainsKey(d.FromObjectId))
                    continue;
                if (reached.Count >= 10_000)
                {
                    truncated = true;
                    continue;
                }
                reached[d.FromObjectId] = reached[current] + 1;
                parent[d.FromObjectId] = current;
                queue.Enqueue(d.FromObjectId);
            }
        var recipes = context
            .Recipes.Where(r => r.ObjectIds.Any(reached.ContainsKey))
            .Where(r => string.IsNullOrWhiteSpace(query) || r.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToArray();
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);
        var nodes = new Dictionary<string, DependencyNode> { { objectId, objects[objectId] } };
        var edges = new Dictionary<string, DependencyLink>();
        foreach (var recipe in recipes.Skip(offset).Take(limit))
        {
            var dependency = recipe
                .ObjectIds.Where(reached.ContainsKey)
                .OrderBy(d => reached[d])
                .ThenBy(d => d, StringComparer.Ordinal)
                .First();
            var path = new List<string>();
            var current = dependency;
            while (current != objectId)
            {
                path.Add(current);
                current = parent[current];
            }
            path.Add(objectId);
            // One shortest explanatory path per recipe. Never return dangling or silently cropped paths.
            if (nodes.Count + path.Count + 1 > 1000)
            {
                truncated = true;
                continue;
            }
            foreach (var item in path)
                nodes[item] = objects[item] with { Depth = reached[item] };
            for (var i = 0; i < path.Count - 1; i++)
            {
                var caller = path[i];
                var target = path[i + 1];
                var d = reverse[target].First(d => d.FromObjectId == caller);
                edges[d.Id] = new(d.Id, target, caller, d.Kind + ": " + d.TargetName, d.Confidence);
            }
            var rid = "recipe:" + recipe.Id;
            nodes[rid] = new(rid, recipe.Name, "recipe", reached[dependency] + 1);
            edges[rid] = new(rid, dependency, rid, "recipe dependency: " + recipe.ColumnLabels.GetValueOrDefault(dependency, "*"), 1);
        }
        truncated |= dependencies.Any(d => d.ToObjectId is null && reached.ContainsKey(d.FromObjectId));
        return new(
            nodes.Values.ToArray(),
            edges.Values.ToArray(),
            recipes.Length,
            offset,
            limit,
            truncated,
            context.RefreshedDatabases,
            context.UpdatedNodes,
            context.UpdatedEdges
        );
    }
}
