using System.Text.Json;
using Microsoft.Data.Sqlite;
using Schemora.SqlServer;
using Schemora.Workspaces.Discovery;

namespace Schemora.Workspaces;

/// <summary>Versioned local catalog. JSON records retain module contracts; indexed tables keep workspace/snapshot boundaries explicit.</summary>
public sealed partial class CatalogStore
{
    private readonly string connectionString;
    private readonly object writeGate = new();
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Tables = new(StringComparer.Ordinal)
    {
        "Workspaces",
        "Roots",
        "Sources",
        "Repositories",
        "Projects",
        "Databases",
        "SchemaSnapshots",
        "SchemaObjects",
        "Columns",
        "Relations",
        "RelationEvidence",
        "RelationOverrides",
        "Recipes",
        "RecipeVersions",
        "RecipeInputs",
        "RecipeDependencies",
        "ValidationRuns",
        "ScanRuns",
        "ToolchainCapabilities",
        "UserSettings",
        "Jobs",
        "RepairProposals",
        "RecipeRegressionFixtures",
        "RecipeRegressionRuns",
        "PortableReviews",
        "AnalysisReceipts",
        "RecipeBatchRuns",
    };
    public string Path { get; }

    public CatalogStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 10,
        }.ToString();
        using var connection = Open();
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(versionCommand.ExecuteScalar());
        if (version > 2)
            throw new InvalidOperationException(
                "Catalog version is newer than this application. Restore a compatible executable; do not overwrite the catalog."
            );
        if (version == 1)
        {
            var migrationBackup = Path + ".before-v2-" + Guid.NewGuid().ToString("N") + ".sqlite";
            using var backupConnection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = migrationBackup, Pooling = false }.ToString()
            );
            backupConnection.Open();
            connection.BackupDatabase(backupConnection);
        }
        using var transaction = connection.BeginTransaction();
        foreach (var table in Tables)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"CREATE TABLE IF NOT EXISTS {table} (Id TEXT PRIMARY KEY, WorkspaceId TEXT, Json TEXT NOT NULL, UpdatedAt TEXT NOT NULL); CREATE INDEX IF NOT EXISTS IX_{table}_Workspace ON {table}(WorkspaceId)";
            command.ExecuteNonQuery();
        }
        using var migration = connection.CreateCommand();
        migration.Transaction = transaction;
        migration.CommandText =
            "CREATE INDEX IF NOT EXISTS IX_Snapshots_Database ON SchemaSnapshots(json_extract(Json,'$.databaseId')); CREATE INDEX IF NOT EXISTS IX_RecipeVersions_Recipe ON RecipeVersions(json_extract(Json,'$.recipeId')); PRAGMA user_version=2";
        migration.ExecuteNonQuery();
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=10000; PRAGMA foreign_keys=ON";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ValidateTable(string table)
    {
        if (!Tables.Contains(table))
            throw new ArgumentException("Unknown catalog table", nameof(table));
    }

    public T? Get<T>(string table, string id)
    {
        ValidateTable(table);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Json FROM {table} WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, Json) : default;
    }

    public IReadOnlyList<T> All<T>(string table, string? workspaceId = null)
    {
        ValidateTable(table);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT Json FROM {table}" + (workspaceId is null ? "" : " WHERE WorkspaceId=$workspace") + " ORDER BY UpdatedAt, Id";
        if (workspaceId is not null)
            command.Parameters.AddWithValue("$workspace", workspaceId);
        using var reader = command.ExecuteReader();
        var values = new List<T>();
        while (reader.Read())
            if (JsonSerializer.Deserialize<T>(reader.GetString(0), Json) is { } value)
                values.Add(value);
        return values;
    }

    public void Put(string table, string id, string? workspaceId, object value) => Batch([new(table, id, workspaceId, value)]);

    public void Batch(IEnumerable<StoredRecord> records, IReadOnlyList<(string Table, string WorkspaceId)>? clear = null)
    {
        lock (writeGate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (clear is not null)
                foreach (var deletion in clear)
                {
                    ValidateTable(deletion.Table);
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"DELETE FROM {deletion.Table} WHERE WorkspaceId=$workspace";
                    command.Parameters.AddWithValue("$workspace", deletion.WorkspaceId);
                    command.ExecuteNonQuery();
                }
            foreach (var record in records)
            {
                ValidateTable(record.Table);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    $"INSERT INTO {record.Table}(Id,WorkspaceId,Json,UpdatedAt) VALUES($id,$workspace,$json,$updated)"
                    + (
                        record.Table is "SchemaSnapshots" or "RecipeVersions"
                            ? ""
                            : " ON CONFLICT(Id) DO UPDATE SET WorkspaceId=excluded.WorkspaceId,Json=excluded.Json,UpdatedAt=excluded.UpdatedAt"
                    );
                command.Parameters.AddWithValue("$id", record.Id);
                command.Parameters.AddWithValue("$workspace", (object?)record.WorkspaceId ?? DBNull.Value);
                command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record.Value, record.Value.GetType(), Json));
                command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public T WithWriteLock<T>(Func<T> action)
    {
        lock (writeGate)
            return action();
    }

    public bool Delete(string table, string id)
    {
        ValidateTable(table);
        lock (writeGate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {table} WHERE Id=$id";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public WorkspaceCatalog Catalog(string workspaceId)
    {
        lock (writeGate)
        {
            var workspace = Get<Workspace>("Workspaces", workspaceId) ?? throw new KeyNotFoundException("Workspace not found");
            return new(
                workspace,
                All<DiscoveredSource>("Sources", workspaceId),
                All<DatabaseInfo>("Databases", workspaceId),
                All<ScanRun>("ScanRuns", workspaceId).Reverse().Take(30).ToArray()
            );
        }
    }

    public IReadOnlyList<SnapshotSummary> Snapshots(string databaseId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id,json_extract(Json,'$.createdAt'),json_extract(Json,'$.mode'),json_extract(Json,'$.fingerprint'),json_array_length(Json,'$.graph.tables'),json_array_length(Json,'$.graph.relations') FROM SchemaSnapshots WHERE json_extract(Json,'$.databaseId')=$database ORDER BY json_extract(Json,'$.createdAt') DESC";
        command.Parameters.AddWithValue("$database", databaseId);
        using var reader = command.ExecuteReader();
        var result = new List<SnapshotSummary>();
        while (reader.Read())
            result.Add(
                new(
                    reader.GetString(0),
                    databaseId,
                    DateTimeOffset.Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5)
                )
            );
        return result;
    }

    public SchemaGraph? CurrentGraph(string databaseId)
    {
        var database = Get<DatabaseInfo>("Databases", databaseId);
        return database?.CurrentSnapshotId is { } id && Get<SnapshotRecord>("SchemaSnapshots", id)?.Graph is { } graph
            ? graph with
            {
                DatabaseName = database.Name,
            }
            : null;
    }

    public void Publish(
        string workspaceId,
        IReadOnlyList<DiscoveredSource> sources,
        IReadOnlyList<DatabaseInfo> databases,
        IReadOnlyList<SnapshotRecord> snapshots,
        ScanRun run,
        Workspace? expectedWorkspace = null,
        IReadOnlyList<StoredRecord>? additionalRecords = null
    )
    {
        lock (writeGate)
        {
            if (expectedWorkspace is not null)
            {
                var current = Get<Workspace>("Workspaces", expectedWorkspace.Id);
                if (
                    current is null
                    || !current.Roots.SequenceEqual(
                        expectedWorkspace.Roots,
                        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
                    )
                    || !current.Excludes.SequenceEqual(expectedWorkspace.Excludes, StringComparer.Ordinal)
                )
                    throw new InvalidOperationException(
                        "Конфигурация рабочей области изменилась во время сканирования; результат не опубликован. Повторите сканирование новых корней."
                    );
            }
            var records = new List<StoredRecord>();
            foreach (var source in sources)
            {
                var id = workspaceId + ":" + source.Id;
                records.Add(new("Sources", id, workspaceId, source));
                if (source.Kind == "repository")
                    records.Add(new("Repositories", id, workspaceId, source));
                if (source.Kind is "solution" or "sqlProject" or "csharpProject")
                    records.Add(new("Projects", id, workspaceId, source));
            }
            records.AddRange(databases.Select(d => new StoredRecord("Databases", d.Id, workspaceId, d)));
            foreach (var snapshot in snapshots)
            {
                records.Add(new("SchemaSnapshots", snapshot.Id, workspaceId, snapshot));
                foreach (var table in snapshot.Graph.Tables)
                {
                    records.Add(
                        new(
                            "SchemaObjects",
                            snapshot.Id + ":" + table.Id,
                            workspaceId,
                            new
                            {
                                snapshot.Id,
                                SnapshotId = snapshot.Id,
                                Table = table,
                            }
                        )
                    );
                    records.AddRange(
                        table.Columns.Select(c => new StoredRecord(
                            "Columns",
                            snapshot.Id + ":" + table.Id + ":" + c.Name,
                            workspaceId,
                            new
                            {
                                SnapshotId = snapshot.Id,
                                ObjectId = table.Id,
                                Column = c,
                            }
                        ))
                    );
                }
                foreach (var obj in snapshot.Graph.Objects ?? [])
                {
                    records.Add(
                        new("SchemaObjects", snapshot.Id + ":" + obj.Id, workspaceId, new { SnapshotId = snapshot.Id, Object = obj })
                    );
                    records.AddRange(
                        obj.Columns.Select(c => new StoredRecord(
                            "Columns",
                            snapshot.Id + ":" + obj.Id + ":" + c.Name,
                            workspaceId,
                            new
                            {
                                SnapshotId = snapshot.Id,
                                ObjectId = obj.Id,
                                Column = c,
                            }
                        ))
                    );
                }
                records.AddRange(
                    (snapshot.Graph.Indexes ?? []).Select(i => new StoredRecord(
                        "SchemaObjects",
                        snapshot.Id + ":index:" + i.Id,
                        workspaceId,
                        new { SnapshotId = snapshot.Id, Index = i }
                    ))
                );
                records.AddRange(
                    (snapshot.Graph.ObjectDependencies ?? []).Select(d => new StoredRecord(
                        "RelationEvidence",
                        snapshot.Id + ":dependency:" + d.Id,
                        workspaceId,
                        new { SnapshotId = snapshot.Id, ObjectDependency = d }
                    ))
                );
                records.AddRange(
                    snapshot.Graph.Relations.Select(r => new StoredRecord(
                        "Relations",
                        snapshot.Id + ":" + r.Id,
                        workspaceId,
                        new { SnapshotId = snapshot.Id, Relation = r }
                    ))
                );
            }
            records.Add(new("ScanRuns", run.Id, workspaceId, run));
            records.AddRange(additionalRecords ?? []);
            Batch(
                records,
                [("Sources", workspaceId), ("Repositories", workspaceId), ("Projects", workspaceId), ("Databases", workspaceId)]
            );
        }
    }

    public void Backup(string destination)
    {
        using var source = Open();
        // Unpooled, so the copy is closed when this returns and can be moved or deleted at once.
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Pooling = false }.ToString());
        target.Open();
        source.BackupDatabase(target);
    }
}
