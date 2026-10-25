using System.Text.Json;

namespace Schemora.SqlServer;

public sealed partial class SchemaDiffer
{
    private static void CompareExtended(
        SchemaGraph before,
        SchemaGraph after,
        List<SchemaChange> changes,
        List<SchemaDiagnostic> diagnostics
    )
    {
        var oldNames = Names(before);
        var newNames = Names(after);
        Compare(
            (before.Objects ?? []).Select(o => new Item(
                o.Schema + "\0" + o.Name,
                o.Id,
                o.Schema,
                o.Name,
                Shape(
                    new
                    {
                        o.Kind,
                        o.Columns,
                        Parameters = o.Parameters ?? [],
                        o.ReturnType,
                        o.Target,
                        o.Definition,
                    }
                )
            )),
            (after.Objects ?? []).Select(o => new Item(
                o.Schema + "\0" + o.Name,
                o.Id,
                o.Schema,
                o.Name,
                Shape(
                    new
                    {
                        o.Kind,
                        o.Columns,
                        Parameters = o.Parameters ?? [],
                        o.ReturnType,
                        o.Target,
                        o.Definition,
                    }
                )
            )),
            "object"
        );
        Compare(
            before.Tables.Select(t => new Item(
                t.Schema + "\0" + t.Name,
                t.Id,
                t.Schema,
                t.Name,
                Shape((t.UniqueKeys ?? []).OrderBy(k => k.Name, StringComparer.Ordinal).Select(k => new { k.Name, k.Columns }))
            )),
            after.Tables.Select(t => new Item(
                t.Schema + "\0" + t.Name,
                t.Id,
                t.Schema,
                t.Name,
                Shape((t.UniqueKeys ?? []).OrderBy(k => k.Name, StringComparer.Ordinal).Select(k => new { k.Name, k.Columns }))
            )),
            "uniqueKeys",
            changesOnly: true
        );
        Compare(
            (before.Indexes ?? []).Where(i => oldNames.ContainsKey(i.ObjectId)).Select(i => Index(i, oldNames)),
            (after.Indexes ?? []).Where(i => newNames.ContainsKey(i.ObjectId)).Select(i => Index(i, newNames)),
            "index"
        );
        Compare(
            (before.ObjectDependencies ?? []).Where(d => oldNames.ContainsKey(d.FromObjectId)).Select(d => Dependency(d, oldNames)),
            (after.ObjectDependencies ?? []).Where(d => newNames.ContainsKey(d.FromObjectId)).Select(d => Dependency(d, newNames)),
            "objectDependency"
        );
        return;

        void Compare(IEnumerable<Item> previous, IEnumerable<Item> current, string kind, bool changesOnly = false)
        {
            var a = previous.GroupBy(i => i.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            var b = current.GroupBy(i => i.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
            foreach (var key in a.Keys.Union(b.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                a.TryGetValue(key, out var left);
                b.TryGetValue(key, out var right);
                if (left?.Length > 1 || right?.Length > 1)
                {
                    diagnostics.Add(
                        new("warning", "DIFF_OBJECT_AMBIGUOUS", "Duplicate metadata identities were excluded from object comparison.")
                    );
                    continue;
                }
                var old = left?.Single();
                var next = right?.Single();
                if (changesOnly && (old is null || next is null) || old?.Value == next?.Value)
                    continue;
                if (changes.Count >= MaximumChanges)
                {
                    if (diagnostics.All(d => d.Code != "DIFF_CHANGE_LIMIT"))
                        diagnostics.Add(
                            new("warning", "DIFF_CHANGE_LIMIT", "Schema changes exceeded 10000; the bounded result is partial.")
                        );
                    break;
                }
                var item = next ?? old!;
                changes.Add(
                    new(
                        kind
                            + (
                                old is null ? "Added"
                                : next is null ? "Removed"
                                : "Changed"
                            ),
                        item.Id,
                        item.Schema,
                        item.Name,
                        null,
                        old?.Value,
                        next?.Value
                    )
                );
            }
        }
    }

    private sealed record Item(string Key, string Id, string Schema, string Name, string Value);

    private static Dictionary<string, (string Schema, string Name)> Names(SchemaGraph graph) =>
        graph
            .Tables.Select(t => (t.Id, t.Schema, t.Name))
            .Concat((graph.Objects ?? []).Select(o => (o.Id, o.Schema, o.Name)))
            .GroupBy(o => o.Id, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => (g.Single().Schema, g.Single().Name), StringComparer.Ordinal);

    private static string Shape<T>(T value) => JsonSerializer.Serialize(value);

    private static Item Index(SchemaIndex index, Dictionary<string, (string Schema, string Name)> names)
    {
        var obj = names[index.ObjectId];
        // Primary keys and unique constraints created without a name get a random system name per deployment.
        var system = IsSystemName(index.Name);
        var identity = system
            ? "~" + index.Kind + (index.IsPrimaryKey ? ":pk:" : ":") + string.Join(",", index.Columns.Select(c => c.Name))
            : index.Name;
        return new(
            obj.Schema + "\0" + obj.Name + "\0" + identity,
            index.ObjectId,
            obj.Schema,
            obj.Name,
            Shape(
                new
                {
                    Name = system ? "(system-named)" : index.Name,
                    index.Kind,
                    index.IsUnique,
                    index.IsPrimaryKey,
                    index.Columns,
                    index.IncludedColumns,
                    index.Filter,
                }
            )
        );
    }

    private static Item Dependency(SchemaObjectDependency dependency, Dictionary<string, (string Schema, string Name)> names)
    {
        var obj = names[dependency.FromObjectId];
        var target =
            dependency.ToObjectId is { } id && names.TryGetValue(id, out var name) ? name.Schema + "." + name.Name : dependency.TargetName;
        return new(
            obj.Schema + "\0" + obj.Name + "\0" + target,
            dependency.FromObjectId,
            obj.Schema,
            obj.Name,
            Shape(
                new
                {
                    Target = target,
                    dependency.Kind,
                    Bound = dependency.ToObjectId is not null,
                }
            )
        );
    }
}
