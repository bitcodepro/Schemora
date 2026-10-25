using System.Text.Json;
using Microsoft.Data.Sqlite;
using Schemora.Relationships;
using Schemora.SqlServer;
using Schemora.Workspaces;

namespace Schemora.Host;

public sealed record GraphPage(
    IReadOnlyList<RelationshipTable> Tables,
    IReadOnlyList<RelationshipEdge> Relations,
    IReadOnlyList<SchemaDiagnostic> Diagnostics,
    int TotalTables,
    bool Truncated,
    bool NeighborhoodTruncated
);

public sealed record RelationPage(IReadOnlyList<RelationshipEdge> Items, int Total, int Offset, int Limit);

// A disposable projection in the catalog, rebuilt when its source revision changes. Browsing
// only deserializes selected rows; full inference is needed once per changed context.
public sealed class GraphReadIndex(CatalogStore store, RelationshipService relationships)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool initialized;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = store.Path,
                Pooling = true,
                DefaultTimeout = 10,
            }.ToString()
        );
        connection.Open();
        connection.CreateFunction<string?, string?>("lower", value => value?.ToLowerInvariant(), isDeterministic: true);
        connection.CreateCollation("NOCASE", StringComparer.OrdinalIgnoreCase.Compare);
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string, object?)[] args)
    {
        using var command = Command(connection, sql, args);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string, object?)[] args)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in args)
            command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }

    public async Task Ensure(string workspaceId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            using var connection = Open();
            if (!initialized)
            {
                Execute(
                    connection,
                    "CREATE TABLE IF NOT EXISTS GraphReadMeta(WorkspaceId TEXT PRIMARY KEY,Revision TEXT NOT NULL,Json TEXT NOT NULL);"
                        + "CREATE TABLE IF NOT EXISTS GraphReadTables(WorkspaceId TEXT,Id TEXT,DatabaseId TEXT,DatabaseName TEXT,SchemaName TEXT,Name TEXT,Json TEXT,PRIMARY KEY(WorkspaceId,Id));"
                        + "CREATE TABLE IF NOT EXISTS GraphReadEdges(WorkspaceId TEXT,Id TEXT,FromId TEXT,ToId TEXT,Kind TEXT,Status TEXT,Confidence REAL,Name TEXT,Json TEXT,PRIMARY KEY(WorkspaceId,Id));"
                        + "CREATE INDEX IF NOT EXISTS IX_GraphReadTables_Filter ON GraphReadTables(WorkspaceId,DatabaseId,SchemaName,Name);"
                        + "CREATE INDEX IF NOT EXISTS IX_GraphReadEdges_From ON GraphReadEdges(WorkspaceId,FromId);"
                        + "CREATE INDEX IF NOT EXISTS IX_GraphReadEdges_To ON GraphReadEdges(WorkspaceId,ToId);"
                );
                initialized = true;
            }
            var revision = relationships.ContextRevision(workspaceId);
            using (var check = Command(connection, "SELECT Revision FROM GraphReadMeta WHERE WorkspaceId=$ws", ("$ws", workspaceId)))
                if (check.ExecuteScalar() as string == revision)
                    return;
            var context = await relationships.GetSnapshot(workspaceId, ct);
            store.WithWriteLock(() =>
            {
                if (relationships.ContextRevision(workspaceId) != context.Revision)
                    throw new AppException("RELATION_CONTEXT_CHANGED", "The model changed. Refresh and try again.", 409);
                using var transaction = connection.BeginTransaction();
                void Run(string sql, params (string, object?)[] args)
                {
                    using var command = Command(connection, sql, args);
                    command.Transaction = transaction;
                    command.ExecuteNonQuery();
                }
                foreach (var table in new[] { "GraphReadTables", "GraphReadEdges", "GraphReadMeta" })
                    Run($"DELETE FROM {table} WHERE WorkspaceId=$ws", ("$ws", workspaceId));
                foreach (var table in context.Graph.Tables)
                {
                    ct.ThrowIfCancellationRequested();
                    Run(
                        "INSERT INTO GraphReadTables VALUES($ws,$id,$db,$dn,$schema,$name,$json)",
                        ("$ws", workspaceId),
                        ("$id", table.Table.Id),
                        ("$db", table.DatabaseId),
                        ("$dn", table.DatabaseName),
                        ("$schema", table.Table.Schema),
                        ("$name", table.Table.Name),
                        ("$json", JsonSerializer.Serialize(table, CatalogStore.Json))
                    );
                }
                foreach (var edge in context.Graph.Relations)
                {
                    ct.ThrowIfCancellationRequested();
                    Run(
                        "INSERT INTO GraphReadEdges VALUES($ws,$id,$from,$to,$kind,$status,$confidence,$name,$json)",
                        ("$ws", workspaceId),
                        ("$id", edge.Relation.Id),
                        ("$from", edge.Relation.FromTableId),
                        ("$to", edge.Relation.ToTableId),
                        ("$kind", edge.Relation.Kind),
                        ("$status", edge.Status),
                        ("$confidence", edge.Relation.Confidence),
                        ("$name", edge.Relation.Name),
                        ("$json", JsonSerializer.Serialize(edge, CatalogStore.Json))
                    );
                }
                Run(
                    "INSERT INTO GraphReadMeta VALUES($ws,$revision,$json)",
                    ("$ws", workspaceId),
                    ("$revision", context.Revision),
                    ("$json", JsonSerializer.Serialize(context.Graph.Diagnostics, CatalogStore.Json))
                );
                transaction.Commit();
                return true;
            });
        }
        finally
        {
            gate.Release();
        }
    }

    private static IReadOnlyList<T> Read<T>(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
            if (JsonSerializer.Deserialize<T>(reader.GetString(0), CatalogStore.Json) is { } item)
                result.Add(item);
        return result;
    }

    private static (string Where, List<(string, object?)> Args) TableFilter(string ws, string? databaseId, string? schema, string? query)
    {
        var args = new List<(string, object?)> { ("$ws", ws) };
        var where = "WorkspaceId=$ws";
        if (!string.IsNullOrEmpty(databaseId))
        {
            where += " AND DatabaseId=$db";
            args.Add(("$db", databaseId));
        }
        if (!string.IsNullOrEmpty(schema))
        {
            where += " AND SchemaName=$schema COLLATE NOCASE";
            args.Add(("$schema", schema));
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            where +=
                " AND (instr(lower(DatabaseName||'.'||SchemaName||'.'||Name),lower($query))>0 OR EXISTS(SELECT 1 FROM json_each(GraphReadTables.Json,'$.table.columns') c WHERE instr(lower(json_extract(c.value,'$.name')),lower($query))>0))";
            args.Add(("$query", query));
        }
        return (where, args);
    }

    public object Tables(string ws, string? db, string? schema, string? query, int offset, int limit)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var filter = TableFilter(ws, db, schema, query);
        using var count = Command(connection, "SELECT count(*) FROM GraphReadTables WHERE " + filter.Where, filter.Args.ToArray());
        count.Transaction = transaction;
        var total = Convert.ToInt32(count.ExecuteScalar());
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 200);
        filter.Args.Add(("$offset", offset));
        filter.Args.Add(("$limit", limit));
        using var items = Command(
            connection,
            "SELECT Json FROM GraphReadTables WHERE "
                + filter.Where
                + " ORDER BY DatabaseName,SchemaName,Name,Id LIMIT $limit OFFSET $offset",
            filter.Args.ToArray()
        );
        items.Transaction = transaction;
        return new
        {
            Items = Read<RelationshipTable>(items).Select(GraphEndpoints.Table),
            Total = total,
            Offset = offset,
            Limit = limit,
        };
    }

    public RelationPage Relations(string ws, int offset, int limit, string? query)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 200);
        var where = "WorkspaceId=$ws";
        var args = new List<(string, object?)> { ("$ws", ws) };
        if (!string.IsNullOrWhiteSpace(query))
        {
            where +=
                " AND (instr(lower(Name||' '||Kind||' '||Status),lower($query))>0 OR EXISTS(SELECT 1 FROM GraphReadTables t WHERE t.WorkspaceId=$ws AND t.Id IN (FromId,ToId) AND instr(lower(t.DatabaseName||'.'||t.SchemaName||'.'||t.Name),lower($query))>0))";
            args.Add(("$query", query));
        }
        using var count = Command(connection, "SELECT count(*) FROM GraphReadEdges WHERE " + where, args.ToArray());
        count.Transaction = transaction;
        var total = Convert.ToInt32(count.ExecuteScalar());
        args.Add(("$offset", offset));
        args.Add(("$limit", limit));
        using var items = Command(
            connection,
            "SELECT Json FROM GraphReadEdges WHERE " + where + " ORDER BY Name,Id LIMIT $limit OFFSET $offset",
            args.ToArray()
        );
        items.Transaction = transaction;
        return new(Read<RelationshipEdge>(items), total, offset, limit);
    }

    public GraphPage Graph(
        string ws,
        string? db,
        string? schema,
        string? query,
        string? focus,
        int hops,
        double confidence,
        string? kind,
        int limit
    )
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var filter = TableFilter(ws, db, schema, query);
        var edgeWhere = "WorkspaceId=$ws AND Status!='rejected' AND Confidence >= $confidence";
        var edgeArgs = new List<(string, object?)> { ("$ws", ws), ("$confidence", confidence) };
        var kinds = (kind ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (kinds.Length > 0)
        {
            edgeWhere += " AND Kind IN (SELECT value FROM json_each($kinds))";
            edgeArgs.Add(("$kinds", JsonSerializer.Serialize(kinds)));
        }
        bool neighborhoodTruncated = false;
        if (focus is not null)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal) { focus };
            var frontier = new[] { focus };
            for (var depth = 0; depth < Math.Clamp(hops, 0, 3) && frontier.Length > 0; depth++)
            {
                using var adjacent = Command(
                    connection,
                    "SELECT DISTINCT CASE WHEN FromId IN (SELECT value FROM json_each($ids)) THEN ToId ELSE FromId END FROM GraphReadEdges WHERE "
                        + edgeWhere
                        + " AND (FromId IN (SELECT value FROM json_each($ids)) OR ToId IN (SELECT value FROM json_each($ids))) LIMIT 10001",
                    edgeArgs.Append(("$ids", (object?)JsonSerializer.Serialize(frontier))).ToArray()
                );
                adjacent.Transaction = transaction;
                var next = new List<string>();
                using var reader = adjacent.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (allowed.Contains(id))
                        continue;
                    if (allowed.Count >= 10000)
                    {
                        neighborhoodTruncated = true;
                        break;
                    }
                    allowed.Add(id);
                    next.Add(id);
                }
                frontier = next.ToArray();
                if (neighborhoodTruncated)
                    break;
            }
            filter.Where += " AND Id IN (SELECT value FROM json_each($allowed))";
            filter.Args.Add(("$allowed", JsonSerializer.Serialize(allowed)));
        }
        using var count = Command(connection, "SELECT count(*) FROM GraphReadTables WHERE " + filter.Where, filter.Args.ToArray());
        count.Transaction = transaction;
        var total = Convert.ToInt32(count.ExecuteScalar());
        filter.Args.Add(("$limit", Math.Clamp(limit, 1, 500)));
        using var select = Command(
            connection,
            "SELECT Json FROM GraphReadTables WHERE " + filter.Where + " ORDER BY DatabaseName,SchemaName,Name,Id LIMIT $limit",
            filter.Args.ToArray()
        );
        select.Transaction = transaction;
        var tables = Read<RelationshipTable>(select);
        edgeArgs.Add(("$ids", JsonSerializer.Serialize(tables.Select(t => t.Table.Id))));
        using var edges = Command(
            connection,
            "SELECT Json FROM GraphReadEdges WHERE "
                + edgeWhere
                + " AND FromId IN (SELECT value FROM json_each($ids)) AND ToId IN (SELECT value FROM json_each($ids)) ORDER BY Id LIMIT 2001",
            edgeArgs.ToArray()
        );
        edges.Transaction = transaction;
        var relations = Read<RelationshipEdge>(edges);
        using var diagnostics = Command(connection, "SELECT Json FROM GraphReadMeta WHERE WorkspaceId=$ws", ("$ws", ws));
        diagnostics.Transaction = transaction;
        var notes = JsonSerializer.Deserialize<SchemaDiagnostic[]>(diagnostics.ExecuteScalar() as string ?? "[]", CatalogStore.Json) ?? [];
        return new(
            tables,
            relations.Take(2000).ToArray(),
            notes,
            total,
            total > tables.Count || relations.Count > 2000,
            neighborhoodTruncated
        );
    }
}
