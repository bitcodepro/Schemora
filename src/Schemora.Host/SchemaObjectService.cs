using System.Text.Json;
using Microsoft.Data.Sqlite;
using Schemora.RecipeEngine;
using Schemora.SqlServer;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record SchemaObjectInfo(
    string DatabaseId,
    string DatabaseName,
    string Id,
    string Schema,
    string Name,
    string Kind,
    IReadOnlyList<SchemaColumn> Columns,
    IReadOnlyList<SchemaParameter> Parameters,
    string? ReturnType,
    string? Target,
    string? Definition,
    string? SourcePath,
    int? Line,
    bool IsPartial,
    IReadOnlyList<string>? PrimaryKey = null,
    IReadOnlyList<SchemaUniqueKey>? UniqueKeys = null
);

public sealed class SchemaObjectService(CatalogStore store)
{
    private sealed record ObjectContext(
        IReadOnlyList<SchemaObjectInfo> Objects,
        IReadOnlyList<SchemaIndex> Indexes,
        IReadOnlyList<SchemaObjectDependency> Dependencies
    );

    // A pinned snapshot replaces its database's current model, so a historical view never mixes in newer objects.
    private SnapshotRecord? Pinned(string workspaceId, string? snapshotId)
    {
        if (string.IsNullOrEmpty(snapshotId))
            return null;
        var snapshot = store.Get<SnapshotRecord>("SchemaSnapshots", snapshotId);
        return snapshot is not null && snapshot.WorkspaceId == workspaceId
            ? snapshot
            : throw new AppException("SNAPSHOT_NOT_FOUND", "Choose a snapshot of this workspace.", 404);
    }

    private ObjectContext Capture(string workspaceId, string? snapshotId = null) =>
        store.WithWriteLock(() =>
        {
            WorkspaceEndpoints.RequireWorkspace(store, workspaceId);
            var pinned = Pinned(workspaceId, snapshotId);
            var objects = new List<SchemaObjectInfo>();
            var indexes = new List<SchemaIndex>();
            var dependencies = new List<SchemaObjectDependency>();
            foreach (var db in store.All<DatabaseInfo>("Databases", workspaceId))
            {
                var graph = pinned?.DatabaseId == db.Id ? pinned.Graph : store.CurrentGraph(db.Id);
                if (graph is null)
                    continue;
                var partial = db.Status != "ready";
                objects.AddRange(
                    graph.Tables.Select(t => new SchemaObjectInfo(
                        db.Id,
                        db.Name,
                        t.Id,
                        t.Schema,
                        t.Name,
                        "table",
                        t.Columns,
                        [],
                        null,
                        null,
                        null,
                        graph.SourcePath,
                        null,
                        partial,
                        t.PrimaryKey,
                        t.UniqueKeys ?? []
                    ))
                );
                objects.AddRange(
                    (graph.Objects ?? []).Select(o => new SchemaObjectInfo(
                        db.Id,
                        db.Name,
                        o.Id,
                        o.Schema,
                        o.Name,
                        o.Kind,
                        o.Columns,
                        o.Parameters ?? [],
                        o.ReturnType,
                        o.Target,
                        o.Definition,
                        o.SourcePath,
                        o.Line,
                        partial || o.IsPartial
                    ))
                );
                indexes.AddRange(graph.Indexes ?? []);
                dependencies.AddRange(graph.ObjectDependencies ?? []);
            }
            return new ObjectContext(objects, indexes, dependencies);
        });

    public object List(
        string workspaceId,
        string? databaseId,
        string? kind,
        string? query,
        int offset,
        int limit,
        string? snapshotId = null
    )
    {
        WorkspaceEndpoints.RequireWorkspace(store, workspaceId);
        var pinned = Pinned(workspaceId, snapshotId);
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 200);
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = store.Path,
                Mode = SqliteOpenMode.ReadOnly,
                DefaultTimeout = 10,
            }.ToString()
        );
        connection.Open();
        connection.CreateFunction<string?, string?>("lower", value => value?.ToLowerInvariant(), isDeterministic: true);
        using var transaction = connection.BeginTransaction();
        var prefix =
            "WITH objects AS (SELECT d.Id AS DatabaseId,json_extract(d.Json,'$.name') AS DatabaseName,json_extract(d.Json,'$.status') AS Status,o.value AS ObjectJson,'table' AS Kind,json_extract(s.Json,'$.graph.sourcePath') AS SourcePath FROM Databases d JOIN SchemaSnapshots s ON s.Id=(CASE WHEN d.Id=$snapshotDb THEN $snapshot ELSE json_extract(d.Json,'$.currentSnapshotId') END),json_each(s.Json,'$.graph.tables') o WHERE d.WorkspaceId=$ws UNION ALL SELECT d.Id,json_extract(d.Json,'$.name'),json_extract(d.Json,'$.status'),o.value,json_extract(o.value,'$.kind'),json_extract(o.value,'$.sourcePath') FROM Databases d JOIN SchemaSnapshots s ON s.Id=(CASE WHEN d.Id=$snapshotDb THEN $snapshot ELSE json_extract(d.Json,'$.currentSnapshotId') END),json_each(s.Json,'$.graph.objects') o WHERE d.WorkspaceId=$ws) ";
        var where =
            " WHERE ($db IS NULL OR DatabaseId=$db) AND ($kinds='[]' OR Kind IN (SELECT value FROM json_each($kinds))) AND ($query='' OR instr(lower(DatabaseName||'.'||json_extract(ObjectJson,'$.schema')||'.'||json_extract(ObjectJson,'$.name')),lower($query))>0 OR EXISTS(SELECT 1 FROM json_each(ObjectJson,'$.columns') c WHERE instr(lower(json_extract(c.value,'$.name')),lower($query))>0))";
        SqliteCommand Command(string sql)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = prefix + sql;
            command.Parameters.AddWithValue("$ws", workspaceId);
            command.Parameters.AddWithValue("$db", (object?)databaseId ?? DBNull.Value);
            command.Parameters.AddWithValue("$snapshot", (object?)pinned?.Id ?? DBNull.Value);
            command.Parameters.AddWithValue("$snapshotDb", (object?)pinned?.DatabaseId ?? DBNull.Value);
            command.Parameters.AddWithValue("$query", query?.Trim() ?? "");
            command.Parameters.AddWithValue(
                "$kinds",
                JsonSerializer.Serialize((kind ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            );
            return command;
        }
        using var count = Command("SELECT count(*) FROM objects" + where);
        var total = Convert.ToInt32(count.ExecuteScalar());
        using var items = Command(
            "SELECT DatabaseId,DatabaseName,Status,ObjectJson,Kind,SourcePath FROM objects"
                + where
                + " ORDER BY DatabaseName,json_extract(ObjectJson,'$.schema'),json_extract(ObjectJson,'$.name'),Kind LIMIT $limit OFFSET $offset"
        );
        items.Parameters.AddWithValue("$limit", limit);
        items.Parameters.AddWithValue("$offset", offset);
        using var reader = items.ExecuteReader();
        var result = new List<SchemaObjectInfo>();
        while (reader.Read())
        {
            var obj = JsonSerializer.Deserialize<JsonElement>(reader.GetString(3), CatalogStore.Json);
            var objectKind = reader.GetString(4);
            T? Value<T>(string name) =>
                obj.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                    ? value.Deserialize<T>(CatalogStore.Json)
                    : default;
            result.Add(
                new(
                    reader.GetString(0),
                    reader.GetString(1),
                    Value<string>("id")!,
                    Value<string>("schema")!,
                    Value<string>("name")!,
                    objectKind,
                    Value<SchemaColumn[]>("columns") ?? [],
                    Value<SchemaParameter[]>("parameters") ?? [],
                    Value<string>("returnType"),
                    Value<string>("target"),
                    null,
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    Value<int?>("line"),
                    reader.GetString(2) != "ready" || Value<bool>("isPartial"),
                    Value<string[]>("primaryKey"),
                    Value<SchemaUniqueKey[]>("uniqueKeys")
                )
            );
        }
        return new
        {
            Items = result,
            Total = total,
            Offset = offset,
            Limit = limit,
        };
    }

    public object Detail(string workspaceId, string objectId, string? snapshotId = null)
    {
        var context = Capture(workspaceId, snapshotId);
        var item =
            context.Objects.FirstOrDefault(o => o.Id == objectId)
            ?? throw new AppException("SCHEMA_OBJECT_NOT_FOUND", "Объект не найден.", 404);
        var affected = new HashSet<string>(StringComparer.Ordinal) { objectId };
        var pending = new Queue<string>();
        pending.Enqueue(objectId);
        var reverse = context
            .Dependencies.Where(d => d.ToObjectId is not null)
            .GroupBy(d => d.ToObjectId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(d => d.FromObjectId).Distinct().ToArray(), StringComparer.Ordinal);
        while (pending.TryDequeue(out var id) && affected.Count < 10_000)
            foreach (var caller in reverse.GetValueOrDefault(id) ?? [])
                if (affected.Count < 10_000 && affected.Add(caller))
                    pending.Enqueue(caller);
        var recipeIds = store
            .All<RecipeEntry>("Recipes", workspaceId)
            .Where(r => !r.Archived && r.Validation.Dependencies.Any(d => affected.Contains(d.TableId)))
            .Select(r => r.Definition.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new
        {
            item.DatabaseId,
            item.DatabaseName,
            item.Id,
            item.Schema,
            item.Name,
            item.Kind,
            item.Columns,
            item.Parameters,
            item.ReturnType,
            item.Target,
            item.Definition,
            item.SourcePath,
            item.Line,
            item.IsPartial,
            item.PrimaryKey,
            item.UniqueKeys,
            Indexes = context.Indexes.Where(i => i.ObjectId == objectId),
            Dependencies = context.Dependencies.Where(d => d.FromObjectId == objectId),
            Dependents = context.Dependencies.Where(d => d.ToObjectId == objectId),
            Impact = new
            {
                ObjectIds = affected.Where(id => id != objectId).Order(StringComparer.Ordinal).ToArray(),
                RecipeIds = recipeIds,
                Partial = affected.Count >= 10_000
                    || context.Dependencies.Any(d => affected.Contains(d.FromObjectId) && d.ToObjectId is null),
            },
        };
    }
}
