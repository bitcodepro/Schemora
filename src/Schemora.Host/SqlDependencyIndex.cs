using Microsoft.Data.Sqlite;
using Schemora.SqlServer;
using Schemora.Workspaces;

namespace Schemora.Host;

internal sealed record IndexedRecipe(
    string Id,
    string Name,
    IReadOnlyList<string> ObjectIds,
    IReadOnlyDictionary<string, string> ColumnLabels
);

internal sealed record DependencyReadContext(
    IReadOnlyList<DependencyNode> Nodes,
    IReadOnlyList<SchemaObjectDependency> Edges,
    IReadOnlyList<IndexedRecipe> Recipes,
    int RefreshedDatabases,
    int UpdatedNodes,
    int UpdatedEdges
);

internal static class SqlDependencyIndex
{
    public static DependencyReadContext Load(CatalogStore store, string workspaceId)
    {
        var catalog = store.Catalog(workspaceId);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.Path }.ToString());
        connection.Open();
        SqliteTransaction? activeTransaction = null;
        int Exec(string sql, params (string, object?)[] args)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = activeTransaction;
            foreach (var (k, v) in args)
                command.Parameters.AddWithValue(k, v ?? DBNull.Value);
            return command.ExecuteNonQuery();
        }
        Exec(
            "CREATE TABLE IF NOT EXISTS SqlDependencyMeta(WorkspaceId TEXT,DatabaseId TEXT,SnapshotId TEXT,PRIMARY KEY(WorkspaceId,DatabaseId));CREATE TABLE IF NOT EXISTS SqlDependencyNodes(WorkspaceId TEXT,DatabaseId TEXT,Id TEXT,Name TEXT,Kind TEXT,PRIMARY KEY(WorkspaceId,Id));CREATE TABLE IF NOT EXISTS SqlDependencyEdges(WorkspaceId TEXT,DatabaseId TEXT,Id TEXT,FromId TEXT,ToId TEXT,TargetName TEXT,Kind TEXT,SourcePath TEXT,Line INTEGER,Confidence REAL,PRIMARY KEY(WorkspaceId,Id));CREATE INDEX IF NOT EXISTS IX_SqlDependencyEdges_To ON SqlDependencyEdges(WorkspaceId,ToId);"
        );
        var refreshed = 0;
        var updatedNodes = 0;
        var updatedEdges = 0;
        foreach (var db in catalog.Databases)
        {
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT SnapshotId FROM SqlDependencyMeta WHERE WorkspaceId=$ws AND DatabaseId=$db";
            check.Parameters.AddWithValue("$ws", workspaceId);
            check.Parameters.AddWithValue("$db", db.Id);
            if (check.ExecuteScalar() as string == (db.CurrentSnapshotId ?? "missing"))
                continue;
            var graph = store.CurrentGraph(db.Id);
            using var transaction = connection.BeginTransaction();
            activeTransaction = transaction;
            var incomingNodes = (graph?.Tables ?? [])
                .Select(t => new DependencyNode(t.Id, t.Schema + "." + t.Name, "table", 0))
                .Concat((graph?.Objects ?? []).Select(o => new DependencyNode(o.Id, o.Schema + "." + o.Name, o.Kind, 0)))
                .ToArray();
            var incomingEdges = graph?.ObjectDependencies ?? [];
            updatedNodes += Exec(
                "DELETE FROM SqlDependencyNodes WHERE WorkspaceId=$ws AND DatabaseId=$db AND Id NOT IN(SELECT value FROM json_each($ids))",
                ("$ws", workspaceId),
                ("$db", db.Id),
                ("$ids", System.Text.Json.JsonSerializer.Serialize(incomingNodes.Select(n => n.Id)))
            );
            updatedEdges += Exec(
                "DELETE FROM SqlDependencyEdges WHERE WorkspaceId=$ws AND DatabaseId=$db AND Id NOT IN(SELECT value FROM json_each($ids))",
                ("$ws", workspaceId),
                ("$db", db.Id),
                ("$ids", System.Text.Json.JsonSerializer.Serialize(incomingEdges.Select(e => e.Id)))
            );
            foreach (var node in incomingNodes)
                updatedNodes += Exec(
                    "INSERT INTO SqlDependencyNodes VALUES($ws,$db,$id,$name,$kind) ON CONFLICT(WorkspaceId,Id) DO UPDATE SET Name=excluded.Name,Kind=excluded.Kind WHERE Name IS NOT excluded.Name OR Kind IS NOT excluded.Kind",
                    ("$ws", workspaceId),
                    ("$db", db.Id),
                    ("$id", node.Id),
                    ("$name", node.Name),
                    ("$kind", node.Kind)
                );
            foreach (var edge in incomingEdges)
                updatedEdges += Exec(
                    "INSERT INTO SqlDependencyEdges VALUES($ws,$db,$id,$from,$to,$name,$kind,$path,$line,$confidence) ON CONFLICT(WorkspaceId,Id) DO UPDATE SET FromId=excluded.FromId,ToId=excluded.ToId,TargetName=excluded.TargetName,Kind=excluded.Kind,SourcePath=excluded.SourcePath,Line=excluded.Line,Confidence=excluded.Confidence WHERE FromId IS NOT excluded.FromId OR ToId IS NOT excluded.ToId OR TargetName IS NOT excluded.TargetName OR Kind IS NOT excluded.Kind OR SourcePath IS NOT excluded.SourcePath OR Line IS NOT excluded.Line OR Confidence IS NOT excluded.Confidence",
                    ("$ws", workspaceId),
                    ("$db", db.Id),
                    ("$id", edge.Id),
                    ("$from", edge.FromObjectId),
                    ("$to", edge.ToObjectId),
                    ("$name", edge.TargetName),
                    ("$kind", edge.Kind),
                    ("$path", edge.SourcePath),
                    ("$line", edge.Line),
                    ("$confidence", edge.Confidence)
                );
            Exec(
                "INSERT OR REPLACE INTO SqlDependencyMeta VALUES($ws,$db,$snapshot)",
                ("$ws", workspaceId),
                ("$db", db.Id),
                ("$snapshot", db.CurrentSnapshotId ?? "missing")
            );
            transaction.Commit();
            activeTransaction = null;
            refreshed++;
        }
        foreach (var table in new[] { "SqlDependencyNodes", "SqlDependencyEdges", "SqlDependencyMeta" })
            Exec(
                "DELETE FROM " + table + " WHERE WorkspaceId=$ws AND DatabaseId NOT IN(SELECT Id FROM Databases WHERE WorkspaceId=$ws)",
                ("$ws", workspaceId)
            );
        var nodes = new List<DependencyNode>();
        var edges = new List<SchemaObjectDependency>();
        var recipes = new List<IndexedRecipe>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id,Name,Kind FROM SqlDependencyNodes WHERE WorkspaceId=$ws";
            command.Parameters.AddWithValue("$ws", workspaceId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                nodes.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), 0));
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT Id,FromId,ToId,TargetName,Kind,SourcePath,Line,Confidence FROM SqlDependencyEdges WHERE WorkspaceId=$ws";
            command.Parameters.AddWithValue("$ws", workspaceId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                edges.Add(
                    new(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetInt32(6),
                        reader.GetDouble(7)
                    )
                );
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT Id,json_extract(Json,'$.definition.name'),json_extract(Json,'$.validation.dependencies') FROM Recipes WHERE WorkspaceId=$ws AND coalesce(json_extract(Json,'$.archived'),0)=0";
            command.Parameters.AddWithValue("$ws", workspaceId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                using var json = System.Text.Json.JsonDocument.Parse(reader.IsDBNull(2) ? "[]" : reader.GetString(2));
                recipes.Add(
                    new(
                        reader.GetString(0),
                        reader.GetString(1),
                        json.RootElement.EnumerateArray().Select(d => d.GetProperty("tableId").GetString()!).Distinct().ToArray(),
                        json.RootElement.EnumerateArray()
                            .GroupBy(d => d.GetProperty("tableId").GetString()!)
                            .ToDictionary(
                                g => g.Key,
                                g =>
                                    string.Join(
                                        ", ",
                                        g.Select(d =>
                                                d.TryGetProperty("columnName", out var column)
                                                && column.ValueKind == System.Text.Json.JsonValueKind.String
                                                    ? column.GetString()
                                                    : "*"
                                            )
                                            .Distinct()
                                    )
                            )
                    )
                );
            }
        }
        return new(nodes, edges, recipes, refreshed, updatedNodes, updatedEdges);
    }
}
